using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class DashboardSettingsEditor : StackPanel
{
    private readonly ComboBox _preset = new() { Header = "样式预设", ItemsSource = new[] { "深色简约", "圆角气泡", "透明紧凑" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _font = new() { Header = "概览字体", IsEditable = true, ItemsSource = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial", "Segoe UI" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly SettingSlider _size = new("正文字号", 18, 12, 48, " px");
    private readonly SettingSlider _nameSize = new("昵称字号", 15, 12, 36, " px");
    private readonly SettingColor _textColor = new("正文颜色");
    private readonly SettingColor _nameColor = new("昵称颜色");
    private readonly SettingColor _background = new("消息背景颜色");
    private readonly Slider _textOpacity = new() { Header = "文字不透明度", Minimum = 20, Maximum = 100, StepFrequency = 5 };
    private readonly Slider _backgroundOpacity = new() { Header = "背景不透明度", Minimum = 0, Maximum = 100, StepFrequency = 5 };
    private readonly SettingSlider _lines = new("额外行距", 4, 0, 24, " px");
    private readonly SettingSlider _spacing = new("消息间距", 8, 0, 32, " px");
    private readonly SettingSlider _padding = new("消息内边距", 12, 0, 32, " px");
    private readonly SettingSlider _radius = new("气泡圆角", 8, 0, 32, " px");
    private readonly CheckBox _avatars = new() { Content = "显示头像（缺失时显示占位）" };
    private readonly CheckBox _medals = new() { Content = "显示粉丝牌" };
    private readonly CheckBox _identity = new() { Content = "显示舰长 / 提督 / 总督 / 房管标识" };
    private readonly CheckBox _animation = new() { Content = "播放动态表情（最多同时 4 个，最高 10 帧/秒）" };
    private readonly CheckBox _compact = new() { Content = "紧凑消息：徽章、昵称和正文同行，时间放入提示" };
    private readonly SettingSlider _avatarSize = new("头像尺寸", 36, 24, 64, " px");
    private readonly SettingSlider _badgeSize = new("徽章字号", 12, 10, 20, " px");
    private readonly SettingSlider _emoteSize = new("行内表情尺寸", 28, 16, 64, " px");
    private readonly SettingSlider _largeEmote = new("整条图片表情高度上限", 100, 40, 160, " px");
    private readonly SettingSlider _rankRefresh = new("榜单可见时刷新间隔", 60, 15, 600, " 秒");
    private readonly NumberBox _refresh = Number("概览刷新间隔（毫秒）", 250, 100, 2000);
    private readonly NumberBox _danmaku = Number("普通弹幕保留条数", 500, 50, 2000);
    private readonly NumberBox _gifts = Number("礼物 / 上舰保留条数", 200, 20, 1000);
    private readonly NumberBox _sc = Number("SC 保留条数", 100, 20, 500);
    private readonly NumberBox _interactions = Number("进房 / 关注保留条数（最多 15 条）", DashboardSettings.MaxInteractionCapacity, 1, DashboardSettings.MaxInteractionCapacity);
    private DashboardSettings _draft;
    private bool _loading;
    public event Action? Changed;
    private static NumberBox Number(string label, double value, double min, double max) => DisplayPage.Number(label, value, min, max);
    private static int Value(NumberBox box, int fallback) => DisplayPage.Value(box, fallback);
    private static int Value(SettingSlider slider, int fallback) => (int)Math.Round(slider.Value);
    public DashboardSettingsEditor(DashboardSettings settings)
    {
        _draft = settings; Spacing = 10;
        var apply = UiFactory.Button("应用预设到下方设置");
        apply.Click += (_, _) => { Load(DashboardSettings.ForPreset(_preset.SelectedItem as string ?? "深色简约", Read(_draft))); Changed?.Invoke(); };
        foreach (var child in new UIElement[] { _preset, apply, _font, _size, _nameSize, _textColor, _nameColor, _background, _textOpacity, _backgroundOpacity,
            new Expander { Header = "排版与图文尺寸", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 10, Children = { _lines, _spacing, _padding, _radius, _avatarSize, _badgeSize, _emoteSize, _largeEmote } } },
            _compact, _avatars, _medals, _identity, _animation,
            UiFactory.Subtitle("表情默认显示静态首帧。动画只在概览可见时播放；格式或帧数不适合播放时回退静态。桌面滚动弹幕使用静态图文缓存。"),
            new Expander { Header = "刷新与本次记录上限", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 10, Children = { _rankRefresh, _refresh, _danmaku, _gifts, _sc, _interactions,
                    UiFactory.Subtitle("记录仅本次运行保留，切换房间后清空。进房 / 关注仅保留最近最多 15 条；其他消息达到上限先移除已读 / 已感谢记录，再移除最早记录。") } } },
        }) Children.Add(child);
        Load(settings);
        SettingEvents.Watch(this, () => { if (!_loading) Changed?.Invoke(); });
    }
    public void Load(DashboardSettings settings)
    {
        _loading = true;
        _draft = settings;
        _preset.SelectedItem = settings.Preset; _font.Text = settings.FontFamily;
        _size.Value = settings.FontSize; _nameSize.Value = settings.UsernameFontSize;
        _textColor.Text = settings.TextColor; _nameColor.Text = settings.UsernameColor; _background.Text = settings.BackgroundColor;
        _textOpacity.Value = settings.TextOpacity * 100; _backgroundOpacity.Value = settings.BackgroundOpacity * 100;
        _lines.Value = settings.LineSpacing; _spacing.Value = settings.MessageSpacing; _padding.Value = settings.Padding; _radius.Value = settings.CornerRadius;
        _avatars.IsChecked = settings.ShowAvatars; _medals.IsChecked = settings.ShowMedals; _identity.IsChecked = settings.ShowIdentity; _animation.IsChecked = settings.AnimateEmotes;
        _compact.IsChecked = settings.CompactMessages;
        _avatarSize.Value = settings.AvatarSize; _badgeSize.Value = settings.BadgeSize; _emoteSize.Value = settings.EmoteSize; _largeEmote.Value = settings.LargeEmoteHeight;
        _refresh.Value = settings.RefreshIntervalMs; _danmaku.Value = settings.DanmakuCapacity; _gifts.Value = settings.GiftCapacity; _sc.Value = settings.SuperChatCapacity; _interactions.Value = settings.InteractionCapacity;
        _rankRefresh.Value = settings.RankRefreshSeconds; _loading = false;
    }
    public DashboardSettings Read(DashboardSettings current)
    {
        return (current with
        {
            Preset = _draft.Preset, FontFamily = _font.Text, FontSize = Value(_size, 18), UsernameFontSize = Value(_nameSize, 15),
            TextColor = _textColor.Text.Trim(), UsernameColor = _nameColor.Text.Trim(), BackgroundColor = _background.Text.Trim(), TextOpacity = _textOpacity.Value / 100, BackgroundOpacity = _backgroundOpacity.Value / 100,
            LineSpacing = Value(_lines, 4), MessageSpacing = Value(_spacing, 8), Padding = Value(_padding, 12), CornerRadius = Value(_radius, 8),
            ShowAvatars = _avatars.IsChecked == true, ShowMedals = _medals.IsChecked == true, ShowIdentity = _identity.IsChecked == true, AnimateEmotes = _animation.IsChecked == true,
            CompactMessages = _compact.IsChecked == true,
            AvatarSize = Value(_avatarSize, 36), BadgeSize = Value(_badgeSize, 12), EmoteSize = Value(_emoteSize, 28), LargeEmoteHeight = Value(_largeEmote, 100), RefreshIntervalMs = Value(_refresh, 250),
            RankRefreshSeconds = Value(_rankRefresh, 60),
            DanmakuCapacity = Value(_danmaku, 500), GiftCapacity = Value(_gifts, 200), SuperChatCapacity = Value(_sc, 100), InteractionCapacity = Value(_interactions, DashboardSettings.MaxInteractionCapacity),
        }).Normalize();
    }
}
