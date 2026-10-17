using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class VisualRegionEditor : StackPanel
{
    private readonly ComboBox _font = new() { Header = "字体", IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mode = new() { Header = "悬浮显示模式", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly SettingSlider _followDuration = new("关注提醒停留", 15, 5, 300, " 秒");
    private OverlayRegionKind _kind;
    private readonly SettingSlider _guardDuration = new("上舰停留", 30, 5, 300, " 秒"), _cardHeight = new("卡片期望高度", 140, 100, 400, " px"), _columns = new("网格期望列数", 2, 1, 4),
        _backgroundOpacity = new("卡片背景不透明度", 90, 0, 100, "%");
    private readonly SettingColor _background = new("卡片背景颜色");
    private readonly SettingSlider _scale = new("每条内容缩放", 100, 50, 200, "%"), _shadowOpacity = new("阴影强度", 70, 0, 100, "%"),
        _shadowSize = new("阴影范围", 2, 0, 6, " px"), _shadowOffset = new("阴影偏移", 2, 0, 6, " px"),
        _displayInterval = new("新内容显示间隔（0 为不额外限速）", 0, 0, 30, " 秒", 0.1);
    private readonly CheckBox _shadow = new() { Content = "文字阴影" };
    private readonly SettingColor _shadowColor = new("阴影颜色");
    private readonly TextBlock _modeHint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 };
    private readonly SettingSlider _size = new("字号", 28, 12, 72, " px"), _emote = new("行内表情高度", 36, 16, 128, " px"), _large = new("整条图片表情高度", 100, 40, 256, " px");
    private readonly SettingColor _color = new("文字 / 分类强调色");
    private readonly SettingSlider _opacity = new("不透明度", 85, 10, 100, "%");
    private readonly CheckBox _bold = new() { Content = "粗体" }, _italic = new() { Content = "斜体" }, _outline = new() { Content = "描边" };
    private readonly SettingSlider _speed = new("滚动速度", 120, 30, 500, " px/s"), _duration = new("每条停留", 5, 1, 300, " 秒"),
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
        foreach (var control in new UIElement[] { _mode, _modeHint,
            SettingGroup.Create("文字与大小", true, _font, _scale, _size, _emote, _large,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _bold, _italic, _outline } }),
            SettingGroup.Create("颜色与透明度", false, _color, _opacity, _background, _backgroundOpacity),
            SettingGroup.Create("播放、数量与间隔", false, _speed, _duration, _followDuration, _guardDuration, _count, _cardHeight, _columns, _merge, _broadcast, _rankCount, _audience, _displayInterval),
            SettingGroup.Create("文字阴影", false, _shadow, _shadowOpacity, _shadowSize, _shadowOffset, _shadowColor),
            new Expander { Header = "精确布局与接收节奏", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 8, Children = { _x, _y, _width, _height, _update } } } }) Children.Add(control);
        SettingEvents.Watch(this, () => { if (!_loading) Changed?.Invoke(); });
        _mode.SelectionChanged += (_, _) => { if (!_loading) UpdateModeControls(); };
    }
    public void Load(OverlayRegionKind kind, OverlayRegionSettings value)
    {
        _loading = true; _kind = kind;
        _mode.ItemsSource = OverlayRegions.Modes(kind).Select(m => new ComboBoxItem { Content = OverlayRegions.ModeTitle(m), Tag = m }).ToArray();
        _mode.SelectedItem = _mode.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (OverlayDisplayMode)i.Tag == value.Mode);
        _guardDuration.Value = value.GuardDurationSeconds; _cardHeight.Value = value.CardHeight; _columns.Value = value.CardColumns; _background.Text = value.BackgroundColor; _backgroundOpacity.Value = value.BackgroundOpacity * 100;
        _scale.Value = value.ContentScale * 100; _shadow.IsChecked = value.Shadow; _shadowOpacity.Value = value.ShadowOpacity * 100;
        _shadowSize.Value = value.ShadowSize; _shadowOffset.Value = value.ShadowOffset; _shadowColor.Text = value.ShadowColor; _displayInterval.Value = value.DisplayIntervalSeconds;
        _font.SelectedItem = value.FontFamily; _font.Text = value.FontFamily; _size.Value = value.FontSize;
        _emote.Value = value.EmoteSize; _large.Value = value.LargeEmoteHeight; _color.Text = value.Color; _opacity.Value = value.Opacity * 100;
        _bold.IsChecked = value.Bold; _italic.IsChecked = value.Italic; _outline.IsChecked = value.Outline;
        _emote.Visibility = _large.Visibility = kind is OverlayRegionKind.Danmaku or OverlayRegionKind.SuperChat ? Visibility.Visible : Visibility.Collapsed;
        _speed.Value = value.ScrollSpeed; _speed.Visibility = kind == OverlayRegionKind.Interaction ? Visibility.Collapsed : Visibility.Visible;
        _duration.Value = value.DurationSeconds; _duration.Visibility = kind == OverlayRegionKind.Interaction ? Visibility.Visible : Visibility.Collapsed;
        _followDuration.Value = value.FollowDurationSeconds; _followDuration.Visibility = kind == OverlayRegionKind.Interaction ? Visibility.Visible : Visibility.Collapsed;
        _count.Maximum = kind == OverlayRegionKind.Danmaku ? 64 : kind == OverlayRegionKind.Interaction ? 3 : 10; _count.Value = value.MaxVisible;
        _count.Visibility = kind is OverlayRegionKind.Status or OverlayRegionKind.SuperChat ? Visibility.Collapsed : Visibility.Visible;
        _merge.Value = value.MergeSeconds; _merge.Visibility = kind == OverlayRegionKind.Gift ? Visibility.Visible : Visibility.Collapsed;
        _broadcast.Value = value.BroadcastIntervalSeconds; _rankCount.Value = value.RankCount; _audience.IsOn = value.ShowAudienceStats;
        _broadcast.Visibility = _rankCount.Visibility = _audience.Visibility = kind == OverlayRegionKind.Status ? Visibility.Visible : Visibility.Collapsed;
        _update.Value = value.UpdateIntervalMs; _update.Visibility = kind is OverlayRegionKind.Status or OverlayRegionKind.Interaction ? Visibility.Collapsed : Visibility.Visible;
        _outline.Visibility = Visibility.Visible;
        LoadLayout(value); UpdateModeControls(); _loading = false;
    }
    private void UpdateModeControls()
    {
        var mode = (_mode.SelectedItem as ComboBoxItem)?.Tag is OverlayDisplayMode m ? m : OverlayDisplayMode.Scrolling;
        var cards = mode == OverlayDisplayMode.Cards; var fixedMode = mode != OverlayDisplayMode.Scrolling;
        _mode.Visibility = _kind == OverlayRegionKind.Interaction ? Visibility.Collapsed : Visibility.Visible;
        _speed.Visibility = !fixedMode && _kind != OverlayRegionKind.Interaction ? Visibility.Visible : Visibility.Collapsed;
        _duration.Minimum = 1; _duration.Visibility = _kind == OverlayRegionKind.Interaction ? Visibility.Visible : Visibility.Collapsed;
        _guardDuration.Visibility = Visibility.Collapsed;
        _count.Maximum = cards ? 6 : _kind == OverlayRegionKind.Interaction ? 3 : 64;
        _count.Visibility = cards || _kind == OverlayRegionKind.Interaction || _kind == OverlayRegionKind.Danmaku && !fixedMode ? Visibility.Visible : Visibility.Collapsed;
        _cardHeight.Visibility = fixedMode && _kind != OverlayRegionKind.Status ? Visibility.Visible : Visibility.Collapsed;
        _columns.Visibility = mode == OverlayDisplayMode.Grid ? Visibility.Visible : Visibility.Collapsed;
        _background.Visibility = _backgroundOpacity.Visibility = Visibility.Visible;
        _displayInterval.Visibility = _kind == OverlayRegionKind.Interaction || _kind == OverlayRegionKind.Status && !fixedMode ? Visibility.Collapsed : Visibility.Visible;
        _broadcast.Visibility = _kind == OverlayRegionKind.Status && !fixedMode ? Visibility.Visible : Visibility.Collapsed;
        _modeHint.Text = cards ? "停留时间请到“SC / 礼物 · 金额停留”调整。到期前保护当前卡片；礼物待播 64 条/60 秒，上舰与 SC 各 64 条/5 分钟。"
            : mode == OverlayDisplayMode.Grid ? "固定槽位，最多 32 张；沿用概览卡片的精确合并和防刷屏规则。临时交互时悬停保护、点击查看发言者、右键隐藏。"
            : mode == OverlayDisplayMode.List ? "只保存最近 100 条；临时交互时滚轮翻阅。仅可见行加载图片。"
            : mode == OverlayDisplayMode.Panel ? "收到新数据后更新固定面板，无持续动画；临时交互时滚轮查看其余名次。" : "";
        _modeHint.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
    }
    public void LoadLayout(OverlayRegionSettings value)
    {
        var wasLoading = _loading; _loading = true;
        _x.Value = value.X ?? double.NaN; _y.Value = value.Y ?? double.NaN; _width.Value = value.Width; _height.Value = value.Height; _loading = wasLoading;
    }
    public OverlayRegionSettings Read(OverlayRegionSettings fallback) => (fallback with
    {
        Mode = (_mode.SelectedItem as ComboBoxItem)?.Tag is OverlayDisplayMode mode ? mode : fallback.Mode, ModeVersion = 1,
        AppearanceVersion = 1, ContentScale = _scale.Value / 100, Shadow = _shadow.IsChecked == true, ShadowOpacity = _shadowOpacity.Value / 100,
        ShadowSize = (int)_shadowSize.Value, ShadowOffset = (int)_shadowOffset.Value, ShadowColor = _shadowColor.Text, DisplayIntervalSeconds = _displayInterval.Value,
        GuardDurationSeconds = _guardDuration.Value, CardHeight = (int)_cardHeight.Value, CardColumns = (int)_columns.Value, BackgroundColor = _background.Text, BackgroundOpacity = _backgroundOpacity.Value / 100,
        FontFamily = string.IsNullOrWhiteSpace(_font.Text) ? _font.SelectedItem as string ?? fallback.FontFamily : _font.Text.Trim(), FontSize = (int)_size.Value,
        EmoteSize = (int)_emote.Value, LargeEmoteHeight = (int)_large.Value, Color = _color.Text, Opacity = _opacity.Value / 100,
        Bold = _bold.IsChecked == true, Italic = _italic.IsChecked == true, Outline = _outline.IsChecked == true, ScrollSpeed = (int)_speed.Value,
        DurationSeconds = _duration.Value, FollowDurationSeconds = _followDuration.Value, MaxVisible = (int)_count.Value, MergeSeconds = _merge.Value,
        BroadcastIntervalSeconds = (int)_broadcast.Value, RankCount = (int)_rankCount.Value, ShowAudienceStats = _audience.IsOn, PlaybackVersion = 1,
        UpdateIntervalMs = DisplayPage.Value(_update, fallback.UpdateIntervalMs), X = double.IsFinite(_x.Value) ? (int)_x.Value : null, Y = double.IsFinite(_y.Value) ? (int)_y.Value : null,
        Width = DisplayPage.Value(_width, fallback.Width), Height = DisplayPage.Value(_height, fallback.Height),
    }).Normalize();
}
