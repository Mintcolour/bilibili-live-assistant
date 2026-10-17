using System.ComponentModel;
using BilibiliLiveAssistant.Core;
using Microsoft.UI.Dispatching;

namespace BilibiliLiveAssistant.App;

// A single time line survives desktop mode changes while OBS is connected.
internal sealed class CompactOverlayController : IAsyncDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _wake, _publish;
    private ObsOverlayServer? _server;
    private bool _desktop, _disposed;
    private int _obsConsumers;
    private CompactOverlaySettings _configuration;
    private long _revision;
    private readonly ObsOverlayProjection _projection = new();
    public CompactTimeline Timeline { get; } = new();
    public event Action? Changed;
    public string ObsAddress => _server?.Address ?? "";
    private bool Active => _desktop || _obsConsumers > 0;
    internal object GetDiagnostics() => new { desktop = _desktop, obsConsumers = _obsConsumers, rows = Timeline.Rows.Count,
        queued = Timeline.Queued, omitted = Timeline.Omitted, revision = _revision, server = _server?.GetDiagnostics() };
    public CompactOverlayController(LiveAssistantViewModel vm, DispatcherQueue dispatcher)
    {
        _vm = vm; _dispatcher = dispatcher; _configuration = vm.Settings.Compact;
        _wake = dispatcher.CreateTimer(); _wake.IsRepeating = false; _wake.Tick += (_, _) => { if (Timeline.Advance(DateTimeOffset.UtcNow, vm.Settings)) QueuePublish(); Schedule(); };
        _publish = dispatcher.CreateTimer(); _publish.IsRepeating = false; _publish.Interval = TimeSpan.FromMilliseconds(100);
        _publish.Tick += (_, _) => Publish();
        Timeline.Dropped += vm.RecordOverlayDropped;
        vm.EventBatchReceived += Intake; vm.RoomHistoryReset += Reset; vm.Dashboard.Cleared += Reset; vm.PropertyChanged += SettingsChanged;
        vm.HotPhrasesChanged += HotPhrasesChanged;
        ConfigureServer();
    }
    public void SetDesktopVisible(bool visible)
    {
        if (_disposed || _desktop == visible) return;
        _desktop = visible; ConsumersChanged();
    }
    private void ConsumersChanged()
    {
        if (_obsConsumers == 0) { _server?.ClearSnapshot(); _projection.Reset(); }
        if (!Active) { Reset(); return; }
        Timeline.Advance(DateTimeOffset.UtcNow, _vm.Settings); Publish(); Schedule();
    }
    private void Intake(IReadOnlyList<LiveEvent> batch)
    {
        if (_disposed || !Active) return;
        Timeline.AddBatch(batch, DateTimeOffset.UtcNow, _vm.Settings); QueuePublish(); Schedule();
    }
    private void QueuePublish() { if (!_publish.IsRunning) _publish.Start(); }
    private void HotPhrasesChanged() { if (!_disposed && Active) QueuePublish(); }
    private void Publish()
    {
        if (_disposed) return;
        _revision++;
        // Build at most once per input burst, regardless of connected clients.
        if (_obsConsumers > 0) _server?.Publish(_projection.Create(_vm, Timeline, _revision));
        Changed?.Invoke();
    }
    private void Schedule()
    {
        _wake.Stop(); if (_disposed || !Active || Timeline.NextWake() is not { } due) return;
        _wake.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Math.Ceiling((due - DateTimeOffset.UtcNow).TotalMilliseconds), 1, int.MaxValue)); _wake.Start();
    }
    private void Reset()
    {
        _wake.Stop(); _publish.Stop(); Timeline.Clear(); _projection.Reset(); Publish();
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || args.PropertyName != nameof(LiveAssistantViewModel.Settings)) return;
        var current = _vm.Settings.Compact;
        var restart = current.ObsEnabled != _configuration.ObsEnabled || current.ObsPort != _configuration.ObsPort;
        _configuration = current;
        if (restart) ConfigureServer();
        _server?.PublishTheme(current.ObsTheme);
        Publish(); Schedule();
    }
    private void ConfigureServer()
    {
        var old = _server; _server = null; _obsConsumers = 0;
        if (old is not null) _ = old.DisposeAsync();
        if (!_configuration.ObsEnabled) { ConsumersChanged(); return; }
        try
        {
            var server = new ObsOverlayServer(_configuration.ObsPort, _vm.Images);
            server.ConsumersChanged += count => _dispatcher.TryEnqueue(() =>
            {
                if (_disposed || !ReferenceEquals(server, _server)) return;
                _obsConsumers = server.ConsumerCount; ConsumersChanged();
            });
            _server = server; server.PublishTheme(_configuration.ObsTheme); server.Start(); Publish();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (_server is { } failed) _ = failed.DisposeAsync(); _server = null; _vm.ReportSettingsError("OBS 网页源未能启动，请检查端口占用后重新启用。" ); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _wake.Stop(); _publish.Stop();
        _vm.EventBatchReceived -= Intake; _vm.RoomHistoryReset -= Reset; _vm.Dashboard.Cleared -= Reset; _vm.PropertyChanged -= SettingsChanged;
        _vm.HotPhrasesChanged -= HotPhrasesChanged;
        Timeline.Dropped -= _vm.RecordOverlayDropped; Timeline.Clear(); _projection.Reset(); Changed = null;
        if (_server is { } server) { _server = null; await server.DisposeAsync(); }
    }
}
