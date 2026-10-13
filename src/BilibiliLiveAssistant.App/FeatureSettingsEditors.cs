using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class MediaSettingsEditor : StackPanel
{
    private readonly ToggleSwitch _dashboard = new() { Header = "概览媒体预览" }, _overlay = new() { Header = "悬浮消息附带媒体" };
    private readonly ComboBox _mode = new() { Header = "概览展示方式", ItemsSource = new[] { "自动内嵌", "点击展开" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly SettingSlider _width = new("资料卡宽度", 420, 260, 800, " px"), _cover = new("封面高度", 96, 48, 160, " px"), _limit = new("悬浮媒体同时显示上限", 2, 1, 4, " 条");
    private bool _loading;
    public event Action? Changed;
    public MediaSettingsEditor()
    {
        Spacing = 12;
        foreach (var item in new UIElement[] { _dashboard, _mode, _overlay, _width, _cover, _limit,
            new TextBlock { Text = "识别 B站 BV 号与 Steam 游戏编号。悬浮沿用对应弹幕 / SC 的速度；空间不足时只显示原文。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 } }) Children.Add(item);
        SettingEvents.Watch(this, () => { if (!_loading) Changed?.Invoke(); });
    }
    public void Load(MediaPreviewSettings settings)
    { _loading = true; _dashboard.IsOn = settings.DashboardEnabled; _mode.SelectedIndex = (int)settings.Mode; _overlay.IsOn = settings.OverlayEnabled; _width.Value = settings.Width; _cover.Value = settings.CoverHeight; _limit.Value = settings.OverlayMaxVisible; _loading = false; }
    public MediaPreviewSettings Read() => new() { DashboardEnabled = _dashboard.IsOn, Mode = _mode.SelectedIndex == 1 ? MediaPreviewMode.OnClick : MediaPreviewMode.Inline,
        OverlayEnabled = _overlay.IsOn, Width = (int)_width.Value, CoverHeight = (int)_cover.Value, OverlayMaxVisible = (int)_limit.Value };
}

internal sealed class CardSettingsEditor : StackPanel
{
    private readonly ToggleSwitch _enabled = new() { Header = "概览普通弹幕使用卡片模式" }, _autoHide = new() { Header = "自动隐藏刷屏内容" };
    private readonly SettingSlider _columns = new("期望列数", 2, 1, 4), _height = new("卡片高度", 180, 140, 320, " px"), _threshold = new("触发重复次数", 20, 2, 1000, " 次"), _seconds = new("暂时隐藏", 30, 5, 600, " 秒");
    private bool _loading;
    public event Action? Changed;
    public CardSettingsEditor()
    {
        Spacing = 12;
        foreach (var item in new UIElement[] { _enabled, _columns, _height, _autoHide, _threshold, _seconds,
            new TextBlock { Text = "固定位置，重复内容原位计数；悬停保护正在阅读的卡片。隐藏仅影响概览卡片。窄窗口会自动减少列数。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 } }) Children.Add(item);
        SettingEvents.Watch(this, () => { if (!_loading) { UpdateEnabled(); Changed?.Invoke(); } });
    }
    private void UpdateEnabled() { _threshold.IsEnabled = _seconds.IsEnabled = _autoHide.IsOn; }
    public void Load(DanmakuCardSettings settings)
    { _loading = true; _enabled.IsOn = settings.Enabled; _columns.Value = settings.Columns; _height.Value = settings.Height; _autoHide.IsOn = settings.AutoHide; _threshold.Value = settings.RepeatThreshold; _seconds.Value = settings.HideSeconds; UpdateEnabled(); _loading = false; }
    public DanmakuCardSettings Read() => new() { Enabled = _enabled.IsOn, Columns = (int)_columns.Value, Height = (int)_height.Value, AutoHide = _autoHide.IsOn,
        RepeatThreshold = (int)_threshold.Value, HideSeconds = (int)_seconds.Value };
}
