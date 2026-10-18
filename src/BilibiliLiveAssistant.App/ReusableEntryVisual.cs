using BilibiliLiveAssistant.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace BilibiliLiveAssistant.App;

// Actual entry messages share one bounded visual tree, including optional
// decorations. Follow/like actions keep their separate, existing renderer.
internal sealed class ReusableEntryVisual
{
    private readonly DashboardSettings _style;
    private readonly Func<string, string, double, double, bool, bool, long, BiliImageView> _create;
    private readonly Grid _avatarHost = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly RichTextBlock _text;
    private readonly Run _name, _content;
    private readonly Badge _guard, _wealth, _admin, _medal, _relation;
    private readonly ImageSlot _wealthImage, _relationImage;
    private readonly Grid _banner = new();
    private readonly Border _gradient = new() { CornerRadius = new(16, 0, 0, 16), IsHitTestVisible = false };
    private readonly LinearGradientBrush _brush;
    private BiliImageView? _avatar, _frame, _background;
    private readonly double _avatarSize;
    internal Border Root { get; }

    internal ReusableEntryVisual(DashboardSettings style,
        Func<string, string, double, double, bool, bool, long, BiliImageView> create)
    {
        _style = style; _create = create; _avatarSize = Math.Clamp(style.AvatarSize, 28, 40);
        _text = new()
        {
            FontFamily = MessageVisualResources.Font(style.FontFamily), FontSize = style.FontSize,
            TextWrapping = TextWrapping.Wrap, Opacity = style.TextOpacity, VerticalAlignment = VerticalAlignment.Center,
            LineHeight = style.FontSize * 1.25 + Math.Min(style.LineSpacing, 4),
        };
        var paragraph = new Paragraph { Margin = new(0) };
        _guard = new(paragraph, style.BadgeSize); _wealthImage = new(paragraph, CreateImage);
        _wealth = new(paragraph, style.BadgeSize); _admin = new(paragraph, style.BadgeSize); _medal = new(paragraph, style.BadgeSize);
        _name = new() { FontSize = style.UsernameFontSize, FontWeight = new(600) };
        _content = new(); paragraph.Inlines.Add(_name); paragraph.Inlines.Add(_content);
        _relationImage = new(paragraph, CreateImage); _relation = new(paragraph, style.BadgeSize);
        _text.Blocks.Add(paragraph);
        _brush = new()
        {
            StartPoint = new(0, .5), EndPoint = new(1, .5),
            GradientStops = { new() { Offset = 0 }, new() { Offset = .65 }, new() { Offset = 1 } },
        };
        _gradient.Background = _brush; _banner.Children.Add(_gradient);
        _banner.Children.Add(new Border { Padding = new(6, 3, 8, 3), CornerRadius = new(16, 0, 0, 16), Child = _text });
        var layout = new Grid { ColumnSpacing = 7 };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        layout.Children.Add(_avatarHost); Grid.SetColumn(_banner, 1); layout.Children.Add(_banner);
        Root = new() { Padding = new(3, 2, 3, 2), CornerRadius = new(6), Child = layout };
    }

    private BiliImageView CreateImage(string url, string label, double width, double height)
        => _create(url, label, width, height, false, false, 0);

    internal void Update(LiveEvent message)
    {
        var appearance = message.Appearance; var effect = appearance?.EntryEffect;
        var showAvatar = _style.ShowsAvatar(message);
        var frameUrl = showAvatar && _style.ShowIdentity ? appearance?.AvatarFrameUrl ?? "" : "";
        if (showAvatar && _avatar is null)
        {
            _avatar = _create("", "", _avatarSize, _avatarSize, true, false, 0);
            _avatarHost.Children.Add(_avatar);
        }
        if (_avatar is not null) BindImage(_avatar, showAvatar ? appearance?.AvatarUrl ?? "" : "", showAvatar ? message.Username : "", showAvatar, MessageCard.AvatarUid(message));
        if (frameUrl.Length > 0 && _frame is null)
        {
            _frame = CreateImage("", "", _avatarSize + 6, _avatarSize + 6); _avatarHost.Children.Add(_frame);
        }
        if (_frame is not null) BindImage(_frame, frameUrl, "", frameUrl.Length > 0);
        _avatarHost.Visibility = showAvatar ? Visibility.Visible : Visibility.Collapsed;
        _avatarHost.Width = _avatarHost.Height = _avatarSize + (frameUrl.Length > 0 ? 6 : 0);

        var guard = _style.ShowIdentity ? appearance?.GuardLevel ?? 0 : 0;
        _guard.Set(guard switch { 1 => "总督", 2 => "提督", 3 => "舰长", _ => "" }, LiveEntryStyles.Accent(appearance));
        var showWealth = _style.ShowIdentity && guard is not (>= 1 and <= 3) && effect is not null && appearance?.WealthLevel > 0;
        var levelText = showWealth ? $"◇ {appearance!.WealthLevel}" : "";
        var levelUrl = showWealth ? effect!.BadgeUrl : "";
        _wealthImage.Set(levelUrl, levelText, 46, _style.BadgeSize + 9);
        _wealth.Set(levelUrl.Length == 0 ? levelText : "", LiveEntryStyles.Accent(appearance));
        _admin.Set(_style.ShowIdentity && appearance?.IsAdmin == true ? "房管" : "", "#438A69");
        _medal.Set(_style.ShowMedals && appearance is { MedalName.Length: > 0, MedalLevel: > 0 } ? $"{appearance.MedalName} {appearance.MedalLevel}" : "", appearance?.MedalColor ?? "#FFFFFF");
        _name.Text = message.Username + "  "; _name.Foreground = MessageCard.Brush(LiveEntryStyles.UsernameColor(appearance, _style.UsernameColor));
        _content.Text = message.Content + "  "; _text.Foreground = MessageCard.Brush(effect?.TextColor ?? "#A7B1C2");
        var relationUrl = _style.ShowIdentity ? appearance?.InteractionIconUrl ?? "" : "";
        var relationLabel = _style.ShowIdentity ? appearance?.InteractionLabel ?? "" : "";
        var hint = appearance?.InteractionHint ?? "";
        _relationImage.Set(relationUrl, relationLabel, 72, _style.BadgeSize + 9, hint.Length > 0 ? hint : relationLabel);
        _relation.Set(relationUrl.Length == 0 ? relationLabel : "", "#515C6B");

        _gradient.Visibility = effect is null ? Visibility.Collapsed : Visibility.Visible;
        if (effect is not null)
        {
            var accent = MessageCard.Brush(LiveEntryStyles.Accent(appearance)).Color;
            _brush.GradientStops[0].Color = ColorHelper.FromArgb(220, accent.R, accent.G, accent.B);
            _brush.GradientStops[1].Color = ColorHelper.FromArgb(145, accent.R, accent.G, accent.B);
            _brush.GradientStops[2].Color = ColorHelper.FromArgb(0, accent.R, accent.G, accent.B);
        }
        var backgroundUrl = effect?.BackgroundUrl ?? "";
        if (backgroundUrl.Length > 0 && _background is null)
        {
            _background = _create("", "", double.NaN, Math.Max(32, _style.FontSize * 1.25 + 6), false, true, 0);
            _background.IsHitTestVisible = false; _background.VerticalAlignment = VerticalAlignment.Center;
            _banner.Children.Insert(1, _background);
        }
        if (_background is not null) BindImage(_background, backgroundUrl, "", backgroundUrl.Length > 0);
        DashboardUi.Describe(Root, $"{message.Username} · {message.OccurredAt:yyyy-MM-dd HH:mm:ss}" + (hint.Length > 0 ? " · " + hint : ""));
    }

    internal void Clear()
    {
        _name.Text = _content.Text = "";
        _guard.Set("", "#FFFFFF"); _wealth.Set("", "#FFFFFF"); _admin.Set("", "#FFFFFF"); _medal.Set("", "#FFFFFF"); _relation.Set("", "#FFFFFF");
        _wealthImage.Clear(); _relationImage.Clear();
        if (_avatar is not null) BindImage(_avatar, "", "", false);
        if (_frame is not null) BindImage(_frame, "", "", false);
        if (_background is not null) BindImage(_background, "", "", false);
        _gradient.Visibility = Visibility.Collapsed;
        DashboardUi.Describe(Root, "");
    }

    private static void BindImage(BiliImageView image, string url, string label, bool show, long uid = 0)
    {
        image.Rebind(show ? url : "", show ? label : "", show ? uid : 0);
        image.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) image.Deactivate();
    }

    private sealed class ImageSlot
    {
        private readonly InlineUIContainer _container = new();
        private readonly Run _space = new();
        private readonly Func<string, string, double, double, BiliImageView> _create;
        private BiliImageView? _image;
        internal ImageSlot(Paragraph paragraph, Func<string, string, double, double, BiliImageView> create)
        { _create = create; paragraph.Inlines.Add(_container); paragraph.Inlines.Add(_space); }
        internal void Set(string url, string label, double width, double height, string? hint = null)
        {
            if (url.Length > 0 && _image is null) { _image = _create("", "", width, height); _container.Child = _image; }
            if (_image is not null)
            {
                BindImage(_image, url, label, url.Length > 0);
                // Clear an old interaction tooltip when this slot becomes empty.
                if (hint is not null || ToolTipService.GetToolTip(_image) is not null) DashboardUi.Describe(_image, url.Length > 0 ? hint ?? label : "");
            }
            _space.Text = url.Length > 0 ? " " : "";
        }
        internal void Clear() => Set("", "", 1, 1);
    }

    private sealed class Badge
    {
        private readonly TextBlock _text;
        private readonly Border _border;
        private readonly Run _space = new();
        internal Badge(Paragraph paragraph, int size)
        {
            _text = new() { FontSize = size, Foreground = MessageCard.Brush("#FFFFFF") };
            _border = new() { CornerRadius = new(9), Padding = new(5, 1, 5, 1), Margin = new(0, 0, 3, 0), Child = _text, Visibility = Visibility.Collapsed };
            paragraph.Inlines.Add(new InlineUIContainer { Child = _border }); paragraph.Inlines.Add(_space);
        }
        internal void Set(string text, string color)
        {
            if (_text.Text != text) _text.Text = text;
            _space.Text = text.Length > 0 ? " " : "";
            _border.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (text.Length > 0) _border.Background = MessageCard.Brush(color, .9);
        }
    }
}
