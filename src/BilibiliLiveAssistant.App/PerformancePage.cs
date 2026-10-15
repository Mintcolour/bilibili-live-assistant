using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class PerformancePage : Page
{
    private readonly PerformanceLog _log;
    private readonly Button _toggle = UiFactory.Button("开始记录");
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private bool _changing;
    internal PerformancePage(PerformanceLog log)
    {
        _log = log;
        var root = UiFactory.Stack();
        root.Children.Add(UiFactory.Heading("性能诊断"));
        root.Children.Add(UiFactory.Subtitle("调试专用 · 默认关闭 · 每 5 秒采样 · 日志最多 8 MiB"));
        var scenario = new ComboBox { Header = "当前采集场景", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "空闲 / 无弹幕", "正常监视", "大量弹幕", "主窗或浮窗隐藏", "浏览历史", "调整浮窗 / 设置预览" }, SelectedIndex = log.Scenario };
        scenario.SelectionChanged += (_, _) => log.Scenario = scenario.SelectedIndex;
        _toggle.Click += async (_, _) =>
        {
            _changing = true; _toggle.IsEnabled = false;
            try { if (_log.State.Running) await _log.StopAsync(); else _log.Start(); }
            finally { _changing = false; Update(); }
        };
        var folder = UiFactory.Button("打开日志目录");
        folder.Click += (_, _) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(PerformanceLog.DirectoryPath);
                Process.Start(new ProcessStartInfo(PerformanceLog.DirectoryPath) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { _status.Text = "无法打开目录：" + ex.GetType().Name; }
        };
        root.Children.Add(UiFactory.Card(new StackPanel { Spacing = 16, Children = { scenario, _toggle, _status, folder } }));
        root.Children.Add(UiFactory.Section("怎样采集", "切换页面后会继续记录，停止记录或退出软件后结束；重启后需要重新开启。",
            new TextBlock { Text = "先记录 2 分钟无弹幕状态，再记录 3–5 分钟正常弹幕。出现卡顿或占用增长时，选择对应场景，继续保留几分钟后停止。\n\n日志包括本进程 CPU、内存、GC、句柄、UI 延迟、缓存、队列及分区绘制耗时，不包含房间号、UID、昵称、消息正文、图片地址和 Cookie。BrowserHost 与浏览器进程不计入本进程 CPU。\n\n日志目录：" + PerformanceLog.DirectoryPath,
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }));
        Content = new ScrollViewer { Content = root };
        Loaded += (_, _) => { _log.Changed += Update; Update(); };
        Unloaded += (_, _) => _log.Changed -= Update;
        Update();
    }
    private void Update()
    {
        var state = _log.State;
        _toggle.Content = state.Running ? "停止记录" : "开始记录";
        _toggle.IsEnabled = !_changing;
        _status.Text = (state.Running ? "正在记录" : "已停止") + $" · 已保存 {state.Samples} 次采样" +
            (state.Error.Length > 0 ? "\n诊断异常：" + state.Error + "。监视继续运行。" : "");
    }
}
