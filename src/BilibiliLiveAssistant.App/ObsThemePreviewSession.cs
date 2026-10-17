using BilibiliLiveAssistant.Core;
using Microsoft.UI.Dispatching;

namespace BilibiliLiveAssistant.App;

// Separate random loopback address and sample-only channel; never a live OBS consumer.
internal sealed class ObsThemePreviewSession : IDisposable
{
    private readonly BiliImageCache _images;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _update;
    private ObsOverlayServer? _server;
    private AppSettings? _draft;
    private bool _disposed;
    public ObsThemePreviewSession(BiliImageCache images, DispatcherQueue dispatcher)
    {
        _images = images; _dispatcher = dispatcher; _update = dispatcher.CreateTimer();
        _update.IsRepeating = false; _update.Interval = TimeSpan.FromMilliseconds(300);
        _update.Tick += (_, _) => { if (_server is { PreviewConsumerCount: > 0 } server && _draft is { } draft) server.PublishPreview(draft); };
    }
    public Uri Open(AppSettings draft)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _draft = draft;
        if (_server is null)
        {
            var server = new ObsOverlayServer(0, _images, previewOnly: true);
            server.PreviewConsumersChanged += count => _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || !ReferenceEquals(_server, server)) return;
                if (server.PreviewConsumerCount > 0 && _draft is { } latest) server.PublishPreview(latest);
                else { _update.Stop(); server.ClearPreview(); }
            });
            try { server.Start(); _server = server; }
            catch { _ = ReleaseAsync(server); throw; }
        }
        _server.PublishPreview(draft); return new Uri(_server.Address + "preview.html");
    }
    public void Update(AppSettings draft)
    {
        if (_disposed || _server is null) return;
        _draft = draft; _update.Stop();
        if (_server.PreviewConsumerCount > 0) _update.Start();
    }
    public void Close()
    {
        _update.Stop(); _draft = null;
        if (_server is not { } server) return;
        _server = null; _ = ReleaseAsync(server);
    }
    private static async Task ReleaseAsync(ObsOverlayServer server)
    {
        try { await server.DisposeAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { RuntimeErrorLog.Record("obs-theme-preview-close", ex); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Close(); }
}
