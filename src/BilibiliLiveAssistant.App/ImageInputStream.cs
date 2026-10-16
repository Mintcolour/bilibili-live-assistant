using Windows.Storage.Streams;

namespace BilibiliLiveAssistant.App;

// The WinRT adapter reads the original body instead of first copying the whole
// image into an InMemoryRandomAccessStream. Closing drops the array reference
// even if a decoder wrapper retains the already-closed stream until a later GC.
internal sealed class ImageInputStream(byte[] bytes) : Stream
{
    private byte[]? _bytes = bytes;
    private long _position;
    public override bool CanRead => _bytes is not null;
    public override bool CanSeek => _bytes is not null;
    public override bool CanWrite => false;
    public override long Length => Body.LongLength;
    private byte[] Body => _bytes ?? throw new ObjectDisposedException(nameof(ImageInputStream));
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var source = Body;
        var count = (int)Math.Min(buffer.Length, Math.Max(0, source.LongLength - _position));
        if (count > 0) { source.AsSpan((int)_position, count).CopyTo(buffer); _position += count; }
        return count;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var source = Body;
        var position = checked(offset + (origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => _position, SeekOrigin.End => source.LongLength, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }));
        if (position < 0) throw new IOException("图片流位置无效");
        return _position = position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { _bytes = null; base.Dispose(disposing); }
    internal static IRandomAccessStream Open(byte[] bytes) => new ImageInputStream(bytes).AsRandomAccessStream();
}
