using System.Drawing;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

public sealed class TrayService : IDisposable
{
    private readonly Icon _applicationIcon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.NotifyIcon _icon;
    private bool _disposed;

    public TrayService(MainWindow window, Func<Task> exitAsync)
    {
        _applicationIcon = new Icon(MainWindow.IconPath);
        // The native tray menu also works while the WinUI window is hidden.
        _menu.Items.Add("打开主窗口", null, (_, _) => window.ShowMainWindow());
        _menu.Items.Add("打开弹幕悬浮窗", null, (_, _) => window.OpenOverlay());
        var interact = new Forms.ToolStripMenuItem("调整浮窗位置、大小与类型") { CheckOnClick = false };
        interact.Click += (_, _) => window.ToggleOverlayInteraction();
        _menu.Opening += (_, _) => { interact.Checked = window.OverlayInteractive; interact.Text = window.OverlayInteractive ? "完成调整，恢复穿透" : "调整浮窗位置、大小与类型"; };
        _menu.Items.Add(interact);
        var compact = new Forms.ToolStripMenuItem("紧凑面板");
        compact.Click += async (_, _) => await window.ToggleCompactModeAsync();
        _menu.Opening += (_, _) => compact.Checked = window.CompactMode;
        _menu.Items.Add(compact);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        var quit = _menu.Items.Add("退出软件");
        quit.Click += async (_, _) =>
        {
            quit.Enabled = false;
            await exitAsync();
        };
        _icon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "B站直播弹幕助手",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) window.ShowMainWindow();
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _applicationIcon.Dispose();
    }
}
