using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class HistorySettingsEditor : StackPanel
{
    private readonly ToggleSwitch _enabled = new() { Header = "本地保留真实互动" };
    private readonly TextBox _directory = new() { Header = "存档目录", PlaceholderText = "留空使用本机默认目录", TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
    private readonly NumberBox _capacity = DisplayPage.Number("容量提醒（GiB）", 10, 1, 4096);
    private readonly TextBlock _usage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
    private bool _loading;
    public event Action? Changed;
    public HistorySettingsEditor()
    {
        Spacing = 12;
        Children.Add(_enabled); Children.Add(_directory); Children.Add(_capacity); Children.Add(_usage);
        var choose = UiFactory.Button("选择存档目录…");
        choose.Click += async (_, _) =>
        {
            choose.IsEnabled = false;
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
                if (await picker.PickSingleFolderAsync() is { } folder) _directory.Text = folder.Path;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _usage.Text = "选择目录失败：" + ex.Message; }
            finally { choose.IsEnabled = true; }
        };
        Children.Insert(2, choose);
        Children.Add(new TextBlock { Text = "长期保留，不自动删除。更换目录前停止监视；保存并应用时复制、校验并切换，原库保留为备份。不会覆盖目标目录已有的历史数据库。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 });
        SettingEvents.Watch(this, () => { if (!_loading) Changed?.Invoke(); });
    }
    public void Load(HistorySettings settings, HistoryStatus status)
    {
        _loading = true; _enabled.IsOn = settings.Enabled; _directory.Text = settings.Directory; _capacity.Value = settings.CapacityWarningGiB;
        UpdateUsage(status);
        _loading = false;
    }
    public HistorySettings Read() => new() { Enabled = _enabled.IsOn, Directory = _directory.Text, CapacityWarningGiB = DisplayPage.Value(_capacity, 10) };
    public void UpdateUsage(HistoryStatus status) => _usage.Text = $"当前存档占用：{status.Bytes / (1024d * 1024):N1} MiB（含 WAL）\n默认目录：{new HistorySettings().EffectiveDirectory}";
    public static string Preview(HistorySettings settings, HistoryStatus status) =>
        $"{(settings.Enabled ? "● 开启本地存档" : "○ 暂停本地存档")}\n\n主播 → 日期 → 监视记录 → 原始互动\n\n" +
        $"弹幕、进房、礼物、SC、上舰\n已有统计与榜单每 60 秒保存\n\n长期保留 · {settings.CapacityWarningGiB} GiB 时提醒\n" +
        $"当前占用 {status.Bytes / (1024d * 1024):N1} MiB\n\n保存后的目录：\n{settings.EffectiveDirectory}\n\n" +
        "静态示例：10-14 · 20:00 开启 → 22:30 停止\n弹幕 120 条 · 免费礼物 3 个 · SC ¥30\n\n示例不写入真实历史。";
}
