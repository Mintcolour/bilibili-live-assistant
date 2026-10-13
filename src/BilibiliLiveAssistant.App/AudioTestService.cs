using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BilibiliLiveAssistant.App;

internal sealed record AudioDeviceOption(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}

internal sealed class AudioTestService : IDisposable
{
    private readonly object _recordingGate = new();
    private WaveInEvent? _capture;
    private WaveFileWriter? _writer;
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private string? _recordingPath;
    private bool _disposed;

    public event Action<float>? InputLevelChanged;
    public event Action<float>? OutputLevelChanged;
    public event Action<string>? StatusChanged;
    public event Action<string?, string?>? RecordingCompleted;

    public bool IsRecording => _capture is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool HasRecording => _recordingPath is not null && File.Exists(_recordingPath);

    public static IReadOnlyList<AudioDeviceOption> GetCaptureDevices()
    {
        var devices = new List<AudioDeviceOption>();
        for (var deviceNumber = 0; deviceNumber < WaveIn.DeviceCount; deviceNumber++)
        {
            var capabilities = WaveIn.GetCapabilities(deviceNumber);
            devices.Add(new AudioDeviceOption(deviceNumber, capabilities.ProductName.Trim()));
        }

        return devices;
    }

    public static IReadOnlyList<AudioDeviceOption> GetPlaybackDevices()
    {
        var devices = new List<AudioDeviceOption>();
        for (var deviceNumber = 0; deviceNumber < WaveOut.DeviceCount; deviceNumber++)
        {
            var capabilities = WaveOut.GetCapabilities(deviceNumber);
            devices.Add(new AudioDeviceOption(deviceNumber, capabilities.ProductName.Trim()));
        }

        return devices;
    }

    public void StartRecording(int deviceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capture is not null) return;
        StopPlayback();

        var path = Path.Combine(Path.GetTempPath(), $"BiliLiveAssistant-AudioTest-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        var capture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(44100, 16, 1),
            BufferMilliseconds = 50,
        };
        WaveFileWriter writer;
        try { writer = new WaveFileWriter(path, capture.WaveFormat); }
        catch
        {
            capture.Dispose();
            throw;
        }
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        lock (_recordingGate)
        {
            _capture = capture;
            _writer = writer;
            _recordingPath = path;
        }

        try
        {
            capture.StartRecording();
            StatusChanged?.Invoke("正在录音…说几句话后停止，即可回放并检查音量。");
        }
        catch
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            writer.Dispose();
            lock (_recordingGate)
            {
                _capture = null;
                _writer = null;
                _recordingPath = null;
            }
            throw;
        }
    }

    public void StopRecording()
    {
        var capture = _capture;
        if (capture is null) return;

        StatusChanged?.Invoke("正在完成录音文件…");
        capture.StopRecording();
    }

    public void StartPlayback(int deviceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopPlayback();
        if (!HasRecording || _recordingPath is null)
            throw new InvalidOperationException("还没有可回放的录音，请先录制一段声音。");

        var reader = new AudioFileReader(_recordingPath);
        var meter = new MeteringSampleProvider(reader.ToSampleProvider(), 1024);
        meter.StreamVolume += OnPlaybackVolume;
        var output = new WaveOutEvent
        {
            DeviceNumber = deviceNumber,
            DesiredLatency = 120,
        };
        output.PlaybackStopped += OnPlaybackStopped;

        try
        {
            output.Init(meter.ToWaveProvider());
            _reader = reader;
            _output = output;
            output.Play();
            StatusChanged?.Invoke("正在回放录音；观察输出电平并确认声音大小合适。");
        }
        catch
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Dispose();
            reader.Dispose();
            throw;
        }
    }

    public void StopPlayback()
    {
        var output = _output;
        if (output is null) return;

        _output = null;
        output.PlaybackStopped -= OnPlaybackStopped;
        try { output.Stop(); }
        finally
        {
            output.Dispose();
            _reader?.Dispose();
            _reader = null;
            OutputLevelChanged?.Invoke(0);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        lock (_recordingGate)
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);

        var sampleCount = e.BytesRecorded / 2;
        if (sampleCount == 0) return;

        var peak = 0d;
        for (var offset = 0; offset < sampleCount * 2; offset += 2)
        {
            var sample = (short)(e.Buffer[offset] | (e.Buffer[offset + 1] << 8));
            var normalized = sample / 32768d;
            peak = Math.Max(peak, Math.Abs(normalized));
        }

        InputLevelChanged?.Invoke((float)Math.Clamp(peak, 0, 1));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        string? path;
        lock (_recordingGate)
        {
            _writer?.Dispose();
            _writer = null;
            _capture?.Dispose();
            _capture = null;
            path = _recordingPath;
        }

        InputLevelChanged?.Invoke(0);
        if (e.Exception is not null)
        {
            StatusChanged?.Invoke($"录音失败：{e.Exception.Message}");
            RecordingCompleted?.Invoke(null, e.Exception.Message);
            return;
        }

        StatusChanged?.Invoke("录音已保存，可以回放检查。建议讲话峰值保持在 -12 至 -6 dB 左右。");
        RecordingCompleted?.Invoke(path, null);
    }

    private void OnPlaybackVolume(object? sender, StreamVolumeEventArgs e)
    {
        var peak = e.MaxSampleValues.Length == 0 ? 0 : e.MaxSampleValues.Max();
        OutputLevelChanged?.Invoke(Math.Clamp(peak, 0, 1));
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WaveOutEvent output || !ReferenceEquals(output, _output)) return;

        _output = null;
        output.PlaybackStopped -= OnPlaybackStopped;
        output.Dispose();
        _reader?.Dispose();
        _reader = null;
        OutputLevelChanged?.Invoke(0);
        StatusChanged?.Invoke(e.Exception is null ? "回放已结束。" : $"回放失败：{e.Exception.Message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPlayback();

        var capture = _capture;
        if (capture is not null)
        {
            using var stopped = new ManualResetEventSlim();
            EventHandler<StoppedEventArgs> onStopped = (_, _) => stopped.Set();
            capture.RecordingStopped += onStopped;
            try
            {
                capture.StopRecording();
                stopped.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Cleanup below still releases the writer and capture when a device disappears.
            }
            finally
            {
                capture.RecordingStopped -= onStopped;
                lock (_recordingGate)
                {
                    _writer?.Dispose();
                    _writer = null;
                    _capture = null;
                }
                capture.Dispose();
            }
        }
    }
}
