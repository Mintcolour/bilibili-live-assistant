using Microsoft.UI.Xaml;
using BilibiliLiveAssistant.Core;
using BilibiliLiveAssistant.Infrastructure;

namespace BilibiliLiveAssistant.App;

public partial class App : Application
{
    public static LiveAssistantViewModel ViewModel { get; private set; } = null!;
    private MainWindow? _window;
    private TrayService? _tray;
    private bool _isExiting;
    internal static nint MainWindowHandle => Current is App app && app._window is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;

    public App()
    {
        InitializeComponent();
        var store = new JsonSettingsStore(); var settings = store.Load();
        var client = new BiliLiveClient();
        var info = new BiliLiveInfoService();
        ViewModel = new LiveAssistantViewModel(settings, store, client, info);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(ViewModel);
        _window.ExitRequested += async () => await ExitAsync();
        _window.Activate();
        _tray = new TrayService(_window, ExitAsync);
        _ = ViewModel.AccountSync.StartAsync();
        if (ViewModel.RoomId <= 0)
            _ = _window.ShowFirstRunDialogAsync();
    }

    private async Task ExitAsync()
    {
        if (_isExiting) return;
        _isExiting = true;
        try
        {
            _window?.AllowClose();
            await ViewModel.DisposeAsync();
            if (ViewModel.History.Status.Faulted && _window?.Content?.XamlRoot is { } root)
            {
                _window.ShowMainWindow();
                await new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = "历史存档未完整保存", Content = ViewModel.History.Status.Message,
                    CloseButtonText = "退出", XamlRoot = root,
                }.ShowAsync();
            }
        }
        finally
        {
            try
            {
                _tray?.Dispose();
                _window?.Close();
            }
            finally { Exit(); }
        }
    }
}
