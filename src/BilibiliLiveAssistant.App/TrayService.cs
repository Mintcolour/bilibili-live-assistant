using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using System.Drawing;

namespace BilibiliLiveAssistant.App;

public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon = new();

    public TrayService(MainWindow window)
    {
        _icon.Icon = SystemIcons.Application;
        _icon.ToolTipText = "B站直播弹幕助手";
        var popup = new StackPanel { Spacing = 4, Padding = new Microsoft.UI.Xaml.Thickness(8) };
        var open = new Button { Content = "打开主窗口" };
        open.Click += (_, _) => window.Activate();
        var overlay = new Button { Content = "打开弹幕悬浮窗" };
        overlay.Click += (_, _) => window.OpenOverlay();
        var quit = new Button { Content = "退出" };
        quit.Click += (_, _) => Microsoft.UI.Xaml.Application.Current.Exit();
        popup.Children.Add(open); popup.Children.Add(overlay); popup.Children.Add(quit);
        _icon.TrayPopup = popup;
        _icon.ForceCreate(true);
    }

    public void Dispose() => _icon.Dispose();
}
