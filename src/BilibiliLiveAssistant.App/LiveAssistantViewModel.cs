using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

public sealed partial class LiveAssistantViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISettingsStore _store;
    private readonly ILiveRoomClient _client;
    private readonly ILiveInfoService _info;
    private readonly DispatcherQueue _dispatcher;
    private AppSettings _settings;

    private string _status = "未连接";
    private bool _isConnected;
    private string _onlineText = "--";
    private string _errorText = string.Empty;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }
    public string OnlineText { get => _onlineText; private set => SetProperty(ref _onlineText, value); }
    public string ErrorText { get => _errorText; private set => SetProperty(ref _errorText, value); }

    public ObservableCollection<LiveEvent> Events { get; } = new();
    public AppSettings Settings => _settings;
    public int RoomId => _settings.RoomId;

    public LiveAssistantViewModel(AppSettings settings, ISettingsStore store, ILiveRoomClient client, ILiveInfoService info)
    {
        _settings = settings;
        _store = store;
        _client = client;
        _info = info;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _client.EventReceived += OnEvent;
        _client.StatusChanged += OnStatus;
        _info.EventReceived += OnEvent;
    }

    public async Task StartAsync(int roomId)
    {
        if (roomId <= 0) { ErrorText = "请输入有效的直播间ID"; return; }
        ErrorText = string.Empty;
        _settings = _settings with { RoomId = roomId };
        await _store.SaveAsync(_settings);
        try
        {
            await _client.StartAsync(roomId, _settings.Cookies);
            await _info.StartAsync(roomId, _settings.Cookies, _settings.PollIntervalSeconds);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Status = "连接失败";
        }
    }

    public async Task StopAsync()
    {
        await _info.StopAsync();
        await _client.StopAsync();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        _settings = settings;
        await _store.SaveAsync(settings);
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(RoomId));
    }

    private void OnStatus(object? sender, string value)
    {
        void Update()
        {
            Status = value;
            IsConnected = value == "已连接";
        }
        if (_dispatcher.HasThreadAccess) Update();
        else _dispatcher.TryEnqueue(Update);
    }

    private void OnEvent(object? sender, LiveEvent value)
    {
        void Update()
        {
            if (value.Type == LiveEventType.Online || value.Type == LiveEventType.Rank)
                OnlineText = value.Content;
            Events.Insert(0, value);
            while (Events.Count > 200) Events.RemoveAt(Events.Count - 1);
        }
        if (_dispatcher.HasThreadAccess) Update();
        else _dispatcher.TryEnqueue(Update);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_client is IDisposable disposable) disposable.Dispose();
        if (_info is IDisposable info) info.Dispose();
    }
}
