using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class VisualRegionEditor : StackPanel
{
    private readonly ComboBox _font = new() { Header = "字体", IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly SettingSlider _size = new("字号", 28, 12, 72, " px"), _emote = new("行内表情高度", 36, 16, 128, " px"), _large = new("整条图片表情高度", 100, 40, 256, " px");
    private readonly SettingColor _color = new("文字 / 分类强调色");
    private readonly SettingSlider _opacity = new("不透明度", 85, 10, 100, "%");
    private readonly CheckBox _bold = new() { Content = "粗体" }, _italic = new() { Content = "斜体" }, _outline = new() { Content = "描边" };
    private readonly SettingSlider _speed = new("滚动速度", 120, 30, 500, " px/s"), _duration = new("每条停留", 5, 1, 120, " 秒"),
        _count = new("同时显示上限", 3, 1, 64), _merge = new("礼物合并窗口（概览与悬浮窗）", 3, 0, 30, " 秒", 0.5), _broadcast = new("榜单播报间隔", 30, 5, 600, " 秒"), _rankCount = new("播报榜单人数", 3, 1, 10);
    private readonly ToggleSwitch _audience = new() { Header = "播报人数统计" };
    private readonly NumberBox _update = DisplayPage.Number("接收批次间隔（ms）", 100, 50, 2000);
    private readonly NumberBox _x = DisplayPage.Number("屏幕 X（留空为默认）", double.NaN, -32768, 32767), _y = DisplayPage.Number("屏幕 Y（留空为默认）", double.NaN, -32768, 32767),
        _width = DisplayPage.Number("宽度（px，0 为默认）", 0, 0, 7680), _height = DisplayPage.Number("高度（px，0 为默认）", 0, 0, 4320);
    private bool _loading;
    public event Action? Changed;
    public VisualRegionEditor()
    {
        Spacing = 10;
        try
        {
            using var installed = new System.Drawing.Text.InstalledFontCollection(); var families = installed.Families;
            try { _font.ItemsSource = families.Select(f => f.Name).Distinct().OrderBy(n => n).ToArray(); }
            finally { foreach (var family in families) family.Dispose(); }
        }
        catch { _font.ItemsSource = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Arial" }; }
        foreach (var control in new UIElement[] { _font, _size, _emote, _large, _color, _opacity,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _bold, _italic, _outline } }, _speed, _duration, _count, _merge, _broadcast, _rankCount, _audience,
            new Expander { Header = "精确布局与接收节奏", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 8, Children = { _x, _y, _width, _height, _update } } } }) Children.Add(control);
        SettingEvents.Watch(this, () => { if (!_loading) Changed?.Invoke(); });
    }
    public void Load(OverlayRegionKind kind, OverlayRegionSettings value)
    {
        _loading = true; _font.SelectedItem = value.FontFamily; _font.Text = value.FontFamily; _size.Value = value.FontSize;
        _emote.Value = value.EmoteSize; _large.Value = value.LargeEmoteHeight; _color.Text = value.Color; _opacity.Value = value.Opacity * 100;
        _bold.IsChecked = value.Bold; _italic.IsChecked = value.Italic; _outline.IsChecked = value.Outline;
        _emote.Visibility = _large.Visibility = kind is OverlayRegionKind.Danmaku or OverlayRegionKind.SuperChat ? Visibility.Visible : Visibility.Collapsed;
        _speed.Value = value.ScrollSpeed; _speed.Visibility = kind == OverlayRegionKind.Interaction ? Visibility.Collapsed : Visibility.Visible;
        _duration.Value = value.DurationSeconds; _duration.Visibility = kind == OverlayRegionKind.Interaction ? Visibility.Visible : Visibility.Collapsed;
        _count.Maximum = kind == OverlayRegionKind.Danmaku ? 64 : kind == OverlayRegionKind.Interaction ? 3 : 10; _count.Value = value.MaxVisible;
        _count.Visibility = kind is OverlayRegionKind.Status or OverlayRegionKind.SuperChat ? Visibility.Collapsed : Visibility.Visible;
        _merge.Value = value.MergeSeconds; _merge.Visibility = kind == OverlayRegionKind.Gift ? Visibility.Visible : Visibility.Collapsed;
        _broadcast.Value = value.BroadcastIntervalSeconds; _rankCount.Value = value.RankCount; _audience.IsOn = value.ShowAudienceStats;
        _broadcast.Visibility = _rankCount.Visibility = _audience.Visibility = kind == OverlayRegionKind.Status ? Visibility.Visible : Visibility.Collapsed;
        _update.Value = value.UpdateIntervalMs; _update.Visibility = kind is OverlayRegionKind.Status or OverlayRegionKind.Interaction ? Visibility.Collapsed : Visibility.Visible;
        _outline.Visibility = kind == OverlayRegionKind.Interaction ? Visibility.Collapsed : Visibility.Visible;
        LoadLayout(value); _loading = false;
    }
    public void LoadLayout(OverlayRegionSettings value)
    {
        var wasLoading = _loading; _loading = true;
        _x.Value = value.X ?? double.NaN; _y.Value = value.Y ?? double.NaN; _width.Value = value.Width; _height.Value = value.Height; _loading = wasLoading;
    }
    public OverlayRegionSettings Read(OverlayRegionSettings fallback) => (fallback with
    {
        FontFamily = string.IsNullOrWhiteSpace(_font.Text) ? _font.SelectedItem as string ?? fallback.FontFamily : _font.Text.Trim(), FontSize = (int)_size.Value,
        EmoteSize = (int)_emote.Value, LargeEmoteHeight = (int)_large.Value, Color = _color.Text, Opacity = _opacity.Value / 100,
        Bold = _bold.IsChecked == true, Italic = _italic.IsChecked == true, Outline = _outline.IsChecked == true, ScrollSpeed = (int)_speed.Value,
        DurationSeconds = _duration.Value, MaxVisible = (int)_count.Value, MergeSeconds = _merge.Value,
        BroadcastIntervalSeconds = (int)_broadcast.Value, RankCount = (int)_rankCount.Value, ShowAudienceStats = _audience.IsOn, PlaybackVersion = 1,
        UpdateIntervalMs = DisplayPage.Value(_update, fallback.UpdateIntervalMs), X = double.IsFinite(_x.Value) ? (int)_x.Value : null, Y = double.IsFinite(_y.Value) ? (int)_y.Value : null,
        Width = DisplayPage.Value(_width, fallback.Width), Height = DisplayPage.Value(_height, fallback.Height),
    }).Normalize();
}
