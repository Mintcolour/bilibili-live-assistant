using Microsoft.UI.Xaml;
using BilibiliLiveAssistant.Core;
using BilibiliLiveAssistant.Infrastructure;

namespace BilibiliLiveAssistant.App;

public partial class App : Application
{
    public static LiveAssistantViewModel ViewModel { get; private set; } = null!;
    private MainWindow? _window;
    private TrayService? _tray;

    public App()
    {
        InitializeComponent();
        var settings = new JsonSettingsStore().Load();
        var client = new BiliLiveClient();
        var info = new BiliLiveInfoService();
        ViewModel = new LiveAssistantViewModel(settings, new JsonSettingsStore(), client, info);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(ViewModel);
        _window.Activate();
        _tray = new TrayService(_window);
        if (ViewModel.RoomId <= 0)
            _ = _window.ShowFirstRunDialogAsync();
    }
}
