using System.Reflection;
using System.Text.Json;
using BilibiliLiveAssistant.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

// Manual UI smoke mode: show the actual native tray menu without having to
// locate its icon among the user's other notification-area icons.
public partial class App : Application
{
    public static LiveAssistantViewModel ViewModel { get; private set; } = null!;
    private MainWindow _window = null!;
    private TrayService _tray = null!;
    private Forms.ContextMenuStrip _menu = null!;
    private Forms.NotifyIcon _icon = null!;
    private OverlayWindow _overlay = null!;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
        ViewModel = new LiveAssistantViewModel(new AppSettings { RoomId = 1 }, new FakeStore(), new FakeClient(), new FakeInfo());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(ViewModel) { Title = "托盘菜单与退出验证" };
        _window.ExitRequested += async () => await ExitFromMenuAsync();
        _window.Activate();
        _tray = new TrayService(_window, ExitFromMenuAsync);
        _window.OpenOverlay();
        _overlay = (OverlayWindow)typeof(MainWindow).GetField("_overlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_window)!;
        _window.HideToTray();
        _window.ShowMainWindow();
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(_window)));
        if (!appWindow.IsVisible) throw new InvalidOperationException("Tray restore did not show the main window.");
        _menu = (Forms.ContextMenuStrip)typeof(TrayService).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_tray)!;
        _icon = (Forms.NotifyIcon)typeof(TrayService).GetField("_icon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_tray)!;
        if (!_icon.Visible || _icon.Icon is null || _icon.ContextMenuStrip != _menu)
            throw new InvalidOperationException("Tray icon/right-click menu was not registered.");
        WriteResult("ready", false);
        // The same menu that NotifyIcon opens on a real right click.
        _menu.Show(new System.Drawing.Point(600, 450));
    }

    private async Task ExitFromMenuAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            _window.AllowClose();
            await ViewModel.DisposeAsync();
        }
        finally
        {
            _tray.Dispose();
            WriteResult("exited", !_icon.Visible && _menu.IsDisposed && _overlay.IsDisposed);
            _window.Close();
            Exit();
        }
    }

    private void WriteResult(string stage, bool cleanupPassed) => File.WriteAllText(
        Path.Combine(AppContext.BaseDirectory, "tray-smoke-results.json"),
        JsonSerializer.Serialize(new { Stage = stage, CleanupPassed = cleanupPassed, RestoredMainWindow = true, NativeContextMenuRegistered = true }));
}
