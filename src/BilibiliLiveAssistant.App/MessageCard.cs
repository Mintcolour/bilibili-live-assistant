using BilibiliLiveAssistant.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BilibiliLiveAssistant.App;

internal sealed class MessageCard : ContentControl, IDisposable
{
    private readonly List<BiliImageView> _images = new();
    private readonly Func<bool> _visible;
    private MediaPreviewGroup? _media;
    private bool _disposed;
    internal Guid RecordId { get; }
    internal long RecordRevision { get; }
    internal DashboardSettings MessageStyle { get; }
    internal bool Read { get; }
    internal bool Thanked { get; }
    internal MediaPreviewSettings MediaStyle { get; }
    public MessageCard(DashboardRecord record, LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible, bool important, DashboardSettings? previewStyle = null)
    {
        _visible = visible;
        var message = record.Event;
        var style = previewStyle ?? vm.Settings.Dashboard;
        RecordId = record.Id; RecordRevision = record.Revision; MessageStyle = style; MediaStyle = vm.Settings.MediaPreview; Read = record.IsRead; Thanked = record.IsThanked;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        if (message.Type == LiveEventType.Enter && !string.IsNullOrEmpty(message.Username))
        {
            Content = BuildInteraction(record, vm, animations, visible, style);
            return;
        }
        if (style.CompactMessages)
        {
            Content = BuildCompact(record, vm, animations, visible, important, style);
            AttachMedia(message, vm, animations, visible, previewStyle is not null);
            return;
        }
        var border = new Border { Padding = new(style.Padding), CornerRadius = new(style.CornerRadius) };
        Margin = new(0, 0, 0, style.MessageSpacing);
        border.Background = Brush(style.BackgroundColor, style.BackgroundOpacity);
        border.BorderBrush = Brush(message.Type == LiveEventType.SuperChat ? "#D6B35A" : message.Type is LiveEventType.Gift or LiveEventType.Guard ? "#AD79AF" : "#3A4254", 0.65);
        border.BorderThickness = new(important ? 1 : 0);
        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        if (style.ShowAvatars)
        {
            var avatar = new BiliImageView(vm.Images, message.Appearance?.AvatarUrl ?? "", message.Username, style.AvatarSize, style.AvatarSize, false, animations, visible, true);
            avatar.VerticalAlignment = VerticalAlignment.Top; _images.Add(avatar); layout.Children.Add(avatar);
        }
        var content = new StackPanel { Spacing = 5 };
        var header = new RichTextBlock { FontFamily = new(style.FontFamily), FontSize = style.UsernameFontSize, Foreground = Brush(style.UsernameColor), Opacity = style.TextOpacity };
        var headerLine = new Paragraph { Margin = new(0) };
        headerLine.Inlines.Add(new Run { Text = message.Username, FontWeight = new(600) });
        if (!message.Extra.ContainsKey("rank_position"))
            headerLine.Inlines.Add(new Run { Text = $"  {message.OccurredAt:HH:mm:ss}", FontSize = 11, Foreground = Brush("#9BA5BD") });
        header.Blocks.Add(headerLine); content.Children.Add(header);
        var appearance = message.Appearance;
        var badgeLine = new Paragraph { Margin = new(0) };
        if (style.ShowMedals && appearance is { MedalName.Length: > 0, MedalLevel: > 0 }) AddBadge(badgeLine, $"{appearance.MedalName} {appearance.MedalLevel}", appearance.MedalColor, style.BadgeSize);
        if (style.ShowIdentity && appearance is not null)
        {
            var guard = appearance.GuardLevel switch { 1 => "总督", 2 => "提督", 3 => "舰长", _ => "" };
            if (guard.Length > 0) AddBadge(badgeLine, guard, appearance.GuardLevel == 3 ? "#4388BC" : "#945ABF", style.BadgeSize);
            if (appearance.IsAdmin) AddBadge(badgeLine, "房管", "#438A69", style.BadgeSize);
        }
        if (badgeLine.Inlines.Count > 0)
        {
            var badges = new RichTextBlock(); badges.Blocks.Add(badgeLine); content.Children.Add(badges);
        }
        if (!message.Extra.ContainsKey("rank_position") || message.Content.Length > 0)
            content.Children.Add(BuildBody(message.Content, appearance, style, vm, animations, visible));
        if (important)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            actions.Children.Add(new TextBlock { Text = record.IsThanked ? "✓ 已感谢" : record.IsRead ? "已读" : "● 未读", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(record.IsRead ? "#91A0B5" : "#FFD878") });
            if (!record.IsRead)
            {
                var read = new Button { Content = "已读", FontSize = 12, Padding = new(6, 2, 6, 2) };
                read.Click += (_, _) => vm.Dashboard.Mark(record, false); actions.Children.Add(read);
            }
            if (!record.IsThanked)
            {
                var thanks = new Button { Content = "已感谢", FontSize = 12, Padding = new(6, 2, 6, 2) };
                thanks.Click += (_, _) => vm.Dashboard.Mark(record, true); actions.Children.Add(thanks);
            }
            content.Children.Add(actions);
        }
        Grid.SetColumn(content, 1); layout.Children.Add(content); border.Child = layout; Content = border;
        AttachMedia(message, vm, animations, visible, previewStyle is not null);
    }
    private void AttachMedia(LiveEvent message, LiveAssistantViewModel vm, MessageAnimationClock clock, Func<bool> visible, bool preview)
    {
        if (preview || !vm.Settings.MediaPreview.DashboardEnabled || message.MediaReferences.Count == 0) return;
        var body = Content as UIElement; Content = null;
        var stack = new StackPanel { Spacing = 5 };
        if (body is not null) stack.Children.Add(body);
        _media = new(message, vm, clock, visible); stack.Children.Add(_media); Content = stack;
    }

    private Border BuildInteraction(DashboardRecord record, LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible, DashboardSettings style)
    {
        var message = record.Event;
        var appearance = message.Appearance;
        var effect = appearance?.EntryEffect;
        Margin = new(0, 0, 0, Math.Min(style.MessageSpacing, 5));
        var layout = new Grid { ColumnSpacing = 7 };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        if (style.ShowAvatars)
        {
            var size = Math.Clamp(style.AvatarSize, 28, 40);
            var frame = style.ShowIdentity && appearance?.AvatarFrameUrl is { Length: > 0 };
            var avatarLayout = new Grid { Width = size + (frame ? 6 : 0), Height = size + (frame ? 6 : 0), VerticalAlignment = VerticalAlignment.Top };
            var avatar = new BiliImageView(vm.Images, appearance?.AvatarUrl ?? "", message.Username, size, size, false, animations, visible, true);
            _images.Add(avatar); avatarLayout.Children.Add(avatar);
            if (frame)
            {
                var frameImage = new BiliImageView(vm.Images, appearance!.AvatarFrameUrl, "", size + 6, size + 6, false, animations, visible);
                _images.Add(frameImage); avatarLayout.Children.Add(frameImage);
            }
            layout.Children.Add(avatarLayout);
        }
        var text = new RichTextBlock
        {
            FontFamily = new(style.FontFamily), FontSize = style.FontSize, Foreground = Brush(effect?.TextColor ?? "#A7B1C2"),
            TextWrapping = TextWrapping.Wrap, Opacity = style.TextOpacity, VerticalAlignment = VerticalAlignment.Center,
            LineHeight = style.FontSize * 1.25 + Math.Min(style.LineSpacing, 4),
        };
        var paragraph = new Paragraph { Margin = new(0) };
        if (style.ShowIdentity && appearance is not null)
        {
            var guard = appearance.GuardLevel switch { 1 => "总督", 2 => "提督", 3 => "舰长", _ => "" };
            if (guard.Length > 0) AddBadge(paragraph, guard, LiveEntryStyles.Accent(appearance), style.BadgeSize);
            else if (effect is not null && appearance.WealthLevel > 0)
            {
                var levelText = $"◇ {appearance.WealthLevel}";
                if (effect.BadgeUrl.Length > 0)
                {
                    var levelIcon = new BiliImageView(vm.Images, effect.BadgeUrl, levelText, 46, style.BadgeSize + 9, false, animations, visible);
                    _images.Add(levelIcon); paragraph.Inlines.Add(new InlineUIContainer { Child = levelIcon });
                    paragraph.Inlines.Add(new Run { Text = " " });
                }
                else AddBadge(paragraph, levelText, LiveEntryStyles.Accent(appearance), style.BadgeSize);
            }
            if (appearance.IsAdmin) AddBadge(paragraph, "房管", "#438A69", style.BadgeSize);
        }
        if (style.ShowMedals && appearance is { MedalName.Length: > 0, MedalLevel: > 0 })
            AddBadge(paragraph, $"{appearance.MedalName} {appearance.MedalLevel}", appearance.MedalColor, style.BadgeSize);
        paragraph.Inlines.Add(new Run { Text = message.Username + "  ", FontSize = style.UsernameFontSize,
            FontWeight = new(600), Foreground = Brush(LiveEntryStyles.UsernameColor(appearance, style.UsernameColor)) });
        paragraph.Inlines.Add(new Run { Text = message.Content + "  " });
        if (style.ShowIdentity && appearance is not null)
        {
            if (appearance.InteractionIconUrl.Length > 0)
            {
                var relation = new BiliImageView(vm.Images, appearance.InteractionIconUrl, appearance.InteractionLabel,
                    72, style.BadgeSize + 9, false, animations, visible);
                DashboardUi.Describe(relation, appearance.InteractionHint.Length > 0 ? appearance.InteractionHint : appearance.InteractionLabel);
                _images.Add(relation); paragraph.Inlines.Add(new InlineUIContainer { Child = relation });
            }
            else if (appearance.InteractionLabel.Length > 0) AddBadge(paragraph, appearance.InteractionLabel, "#515C6B", style.BadgeSize);
        }
        text.Blocks.Add(paragraph);
        var banner = new Border { Padding = new(6, 3, 8, 3), CornerRadius = new(16, 0, 0, 16), Child = text };
        if (effect is not null)
        {
            var accent = Brush(LiveEntryStyles.Accent(appearance)).Color;
            banner.Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0.5), EndPoint = new(1, 0.5),
                GradientStops = {
                    new GradientStop { Color = ColorHelper.FromArgb(220, accent.R, accent.G, accent.B), Offset = 0 },
                    new GradientStop { Color = ColorHelper.FromArgb(145, accent.R, accent.G, accent.B), Offset = 0.65 },
                    new GradientStop { Color = ColorHelper.FromArgb(0, accent.R, accent.G, accent.B), Offset = 1 },
                },
            };
        }
        var bannerLayout = new Grid();
        if (banner.Background is { } gradient)
        {
            bannerLayout.Children.Add(new Border { Background = gradient, CornerRadius = banner.CornerRadius, IsHitTestVisible = false });
            banner.Background = null;
        }
        if (effect?.BackgroundUrl is { Length: > 0 } backgroundUrl)
        {
            var background = new BiliImageView(vm.Images, backgroundUrl, "", double.NaN,
                Math.Max(32, style.FontSize * 1.25 + 6), false, animations, visible, fill: true)
            { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            _images.Add(background); bannerLayout.Children.Add(background);
        }
        bannerLayout.Children.Add(banner);
        Grid.SetColumn(bannerLayout, 1); layout.Children.Add(bannerLayout);
        var border = new Border { Padding = new(3, 2, 3, 2), Child = layout };
        DashboardUi.Describe(border, $"{message.Username} · {message.OccurredAt:yyyy-MM-dd HH:mm:ss}" +
            (appearance?.InteractionHint is { Length: > 0 } hint ? $" · {hint}" : ""));
        return border;
    }

    private Border BuildCompact(DashboardRecord record, LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible, bool important, DashboardSettings style)
    {
        var message = record.Event;
        var sc = message.Type == LiveEventType.SuperChat;
        var fullEmote = message.Appearance?.FullEmote is not null;
        Margin = new(0, 0, 0, sc ? Math.Max(5, Math.Min(style.MessageSpacing, 10)) : Math.Min(style.MessageSpacing, 4));
        var border = new Border
        {
            Padding = sc ? new Thickness(Math.Clamp(style.Padding, 8, 14)) : new Thickness(5, 3, 5, 3),
            CornerRadius = new(sc ? style.CornerRadius : 4),
            Background = sc ? Brush(style.BackgroundColor, Math.Max(0.72, style.BackgroundOpacity)) : null,
            BorderBrush = sc ? Brush("#D6B35A", 0.7) : null,
            BorderThickness = new(sc ? 1 : 0),
        };
        DashboardUi.Describe(border, $"{message.Username} · {message.OccurredAt:yyyy-MM-dd HH:mm:ss}");
        var layout = new Grid { ColumnSpacing = 7 };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        if (style.ShowAvatars)
        {
            var size = Math.Clamp(style.AvatarSize, 24, 32);
            var avatar = new BiliImageView(vm.Images, message.Appearance?.AvatarUrl ?? "", message.Username, size, size, false, animations, visible, true);
            avatar.VerticalAlignment = VerticalAlignment.Top;
            _images.Add(avatar); layout.Children.Add(avatar);
        }
        var text = new RichTextBlock
        {
            FontFamily = new(style.FontFamily), FontSize = sc ? style.UsernameFontSize : style.FontSize,
            Foreground = Brush(style.TextColor), Opacity = style.TextOpacity, TextWrapping = TextWrapping.Wrap,
            LineHeight = style.FontSize * 1.25 + Math.Min(style.LineSpacing, 4), LineStackingStrategy = LineStackingStrategy.MaxHeight,
            VerticalAlignment = fullEmote && !sc ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        var paragraph = new Paragraph { Margin = new(0) };
        AddIdentity(paragraph, message.Appearance, style);
        paragraph.Inlines.Add(new Run { Text = message.Username + "  ", FontSize = style.UsernameFontSize,
            FontWeight = new(600), Foreground = Brush(style.UsernameColor) });
        if (sc)
        {
            var price = SuperChatPrice(message);
            if (price.Length > 0) AddBadge(paragraph, price, "#8C6A2B", style.BadgeSize);
        }
        else if (!fullEmote) AppendInlineBody(paragraph, message.Content, message.Appearance, style, vm, animations, visible);
        text.Blocks.Add(paragraph);
        FrameworkElement messageContent = text;
        if (fullEmote && !sc)
            messageContent = new StackPanel { Spacing = 4, Children = { text, BuildBody(message.Content, message.Appearance, style, vm, animations, visible) } };
        Grid.SetColumn(messageContent, 1); layout.Children.Add(messageContent);
        if (important)
        {
            var actions = BuildActions(record, vm);
            Grid.SetColumn(actions, 2); layout.Children.Add(actions);
        }
        if (sc)
        {
            var content = message.Content;
            if (content.StartsWith('¥') && content.IndexOf(' ') is var space && space > 0 && SuperChatPrice(message).Length > 0) content = content[(space + 1)..];
            border.Child = new StackPanel { Spacing = 7, Children = { layout, BuildBody(content, message.Appearance, style, vm, animations, visible) } };
        }
        else border.Child = layout;
        return border;
    }

    private static void AddIdentity(Paragraph paragraph, LiveMessageAppearance? appearance, DashboardSettings style)
    {
        if (style.ShowMedals && appearance is { MedalName.Length: > 0, MedalLevel: > 0 })
            AddBadge(paragraph, $"{appearance.MedalName} {appearance.MedalLevel}", appearance.MedalColor, style.BadgeSize);
        if (!style.ShowIdentity || appearance is null) return;
        var guard = appearance.GuardLevel switch { 1 => "总督", 2 => "提督", 3 => "舰长", _ => "" };
        if (guard.Length > 0) AddBadge(paragraph, guard, appearance.GuardLevel == 3 ? "#4388BC" : "#945ABF", style.BadgeSize);
        if (appearance.IsAdmin) AddBadge(paragraph, "房管", "#438A69", style.BadgeSize);
    }

    private FrameworkElement BuildBody(string content, LiveMessageAppearance? appearance, DashboardSettings style,
        LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible)
    {
        if (appearance?.FullEmote is { } full)
        {
            // Large emotes form a content block, never an inline in the name/badge
            // paragraph. Reserve their size before loading and shrink with the column.
            var image = CreateEmote(full, content, true, style, vm, animations, visible);
            return new Viewbox
            {
                Child = image, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 240, MaxHeight = style.LargeEmoteHeight,
                Opacity = style.TextOpacity,
            };
        }
        var body = new RichTextBlock
        {
            FontFamily = new(style.FontFamily), FontSize = style.FontSize, Foreground = Brush(style.TextColor),
            Opacity = style.TextOpacity, TextWrapping = TextWrapping.Wrap, LineHeight = style.FontSize * 1.35 + style.LineSpacing,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
        };
        var paragraph = new Paragraph { Margin = new(0) };
        AppendInlineBody(paragraph, content, appearance, style, vm, animations, visible);
        body.Blocks.Add(paragraph);
        return body;
    }

    private BiliImageView CreateEmote(LiveEmote emote, string fallback, bool large, DashboardSettings style,
        LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible)
    {
        double height = large ? style.LargeEmoteHeight : style.EmoteSize;
        var width = height * Math.Max(1d, emote.Width) / Math.Max(1, emote.Height);
        var maxWidth = large ? 240 : 100;
        if (width > maxWidth) { height *= maxWidth / width; width = maxWidth; }
        var image = new BiliImageView(vm.Images, emote.Url, string.IsNullOrEmpty(fallback) ? "[表情]" : fallback,
            width, height, style.AnimateEmotes, animations, visible);
        _images.Add(image);
        return image;
    }

    private void AppendInlineBody(Paragraph paragraph, string content, LiveMessageAppearance? appearance, DashboardSettings style,
        LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible)
    {
        var text = content.Length > 4096 ? content[..4096] + "…" : content;
        foreach (var segment in RichMessageSegments.Split(text, appearance))
        {
            if (segment.Emote is not { } emote) { paragraph.Inlines.Add(new Run { Text = segment.Text }); continue; }
            paragraph.Inlines.Add(new InlineUIContainer { Child = CreateEmote(emote, segment.Text, false, style, vm, animations, visible) });
        }
    }

    private static StackPanel BuildActions(DashboardRecord record, LiveAssistantViewModel vm)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Top };
        if (!record.IsRead)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, Fill = Brush("#FFD878"), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 3, 0) };
            DashboardUi.Describe(dot, "未读"); actions.Children.Add(dot);
        }
        var read = DashboardUi.Button("\uE73E", record.IsRead ? "已读" : "标为已读", 28);
        read.IsEnabled = !record.IsRead;
        read.Click += (_, _) => vm.Dashboard.Mark(record, false); actions.Children.Add(read);
        var thanks = DashboardUi.Button(record.IsThanked ? "\uE735" : "\uE734", record.IsThanked ? "已感谢" : "标为已感谢", 28);
        thanks.IsEnabled = !record.IsThanked;
        thanks.Click += (_, _) => vm.Dashboard.Mark(record, true); actions.Children.Add(thanks);
        DashboardUi.Describe(actions, record.IsThanked ? "已感谢 · 已读" : record.IsRead ? "已读" : "未读");
        return actions;
    }

    private static string SuperChatPrice(LiveEvent message)
    {
        if (message.Extra.TryGetValue("price", out var price) && price is not null) return $"¥{price}";
        var space = message.Content.IndexOf(' ');
        return message.Content.StartsWith('¥') && space is > 1 and < 16 ? message.Content[..space] : "";
    }

    private static void AddBadge(Paragraph paragraph, string text, string color, int size)
    {
        paragraph.Inlines.Add(new InlineUIContainer { Child = new Border
        {
            Background = Brush(color, 0.9), CornerRadius = new(9), Padding = new(5, 1, 5, 1), Margin = new(0, 0, 3, 0),
            Child = new TextBlock { Text = text, FontSize = size, Foreground = Brush("#FFFFFF") },
        } });
        paragraph.Inlines.Add(new Run { Text = " " });
    }
    internal static SolidColorBrush Brush(string hex, double opacity = 1)
    {
        if (!OverlayRegionSettings.IsColor(hex)) hex = "#FFFFFF";
        return new(ColorHelper.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16)));
    }
    // Realization is explicit: inline children must not gate their image load on
    // their own Loaded/IsLoaded state. The card/list owns the resource lifetime.
    internal void Activate(double rasterizationScale = 1)
    {
        if (_disposed) return;
        if (!_visible()) { Deactivate(); return; }
        foreach (var image in _images) image.Activate(XamlRoot?.RasterizationScale ?? rasterizationScale);
        _media?.SetActive(true);
    }
    internal void Deactivate() { foreach (var image in _images) image.Deactivate(); _media?.SetActive(false); }
    private void OnLoaded(object sender, RoutedEventArgs e) => Activate();
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || IsLoaded) return;
            Deactivate();
        });
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var image in _images) image.Dispose(); _images.Clear();
        _media?.Dispose(); _media = null;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        Content = null;
    }
    internal bool IsDisposed => _disposed;
}

internal sealed class BiliImageView : Grid, IDisposable
{
    private readonly BiliImageCache _cache;
    private readonly string _url;
    private readonly bool _animate;
    private readonly MessageAnimationClock _clock;
    private readonly Func<bool> _visible;
    private readonly Image? _image;
    private readonly PersonPicture? _person;
    private readonly TextBlock? _fallback;
    private BiliImageAsset? _asset;
    private BiliImageAsset? _staticAsset;
    private BitmapImage?[]? _sources;
    private BitmapImage? _firstSource;
    private bool _playing;
    private bool _loadingAnimation;
    private bool _loadingStatic;
    private CancellationTokenSource? _loadCancellation;
    private int _generation;
    private bool _disposed;
    private bool _busy;
    private bool _active;
    private double _rasterizationScale = 1;
    private int _frame;
    private DateTimeOffset _nextFrame;

    public BiliImageView(BiliImageCache cache, string url, string fallback, double width, double height, bool animate, MessageAnimationClock clock, Func<bool> visible, bool avatar = false, bool fill = false)
    {
        _cache = cache; _url = url; _animate = animate && !avatar; _clock = clock; _visible = visible;
        Width = width; Height = height;
        if (avatar) { _person = new PersonPicture { DisplayName = fallback, Width = width, Height = height }; Children.Add(_person); }
        else
        {
            _fallback = new TextBlock { Text = fallback, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            _image = new Image { Stretch = fill ? Stretch.Fill : Stretch.Uniform }; Children.Add(_fallback); Children.Add(_image);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, fallback);
    }
    internal void Activate(double rasterizationScale)
    {
        if (_disposed) return;
        _active = true; _rasterizationScale = rasterizationScale;
        RestoreVisuals();
    }
    internal async void RestoreVisuals()
    {
        if (_disposed || !_active || _loadingStatic) return;
        _loadingStatic = true;
        var generation = _generation;
        var cancellation = (_loadCancellation ??= new()).Token;
        try
        {
            if (_asset is null)
            {
                var asset = await _cache.GetForViewAsync(_url, false, cancellation);
                if (_disposed || !_active || generation != _generation || asset is null) return;
                _asset = asset; _staticAsset = asset;
                _sources = new BitmapImage?[asset.Frames.Length];
            }
            // A short layout detach keeps its source. After a real unload the
            // shared, bounded first-frame cache restores it without destroying the view.
            if (_image is { Source: null } || _person is { ProfilePicture: null }) await SetFrameAsync(0);
            _firstSource ??= _sources?[0];
            if (!_disposed && _active && generation == _generation && _animate && _asset?.CanAnimate == true) _clock.Add(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { BiliImageDiagnostics.Record(_url, "view-restore", ex); }
        finally
        {
            _loadingStatic = false;
            if (!_disposed && _active && generation != _generation) RestoreVisuals();
        }
    }
    private async Task SetFrameAsync(int index)
    {
        if (_busy || _disposed || _asset is null) return;
        _busy = true;
        var generation = _generation;
        var asset = _asset;
        var sources = _sources;
        var cancellation = _loadCancellation?.Token ?? CancellationToken.None;
        try
        {
            var source = sources?[index];
            if (source is null)
            {
                var displayWidth = double.IsFinite(Width) ? Width : ActualWidth > 0 ? ActualWidth : asset.Width;
                var pixelWidth = Math.Clamp((int)Math.Ceiling(displayWidth * _rasterizationScale), 1, asset.Width);
                if (index == 0) source = await _clock.GetFirstFrameAsync(_url, asset, pixelWidth, cancellation);
                else
                {
                    using var stream = await BiliImageCache.ToStreamAsync(asset.Frames[index]);
                    if (_disposed || generation != _generation) return;
                    source = new BitmapImage { DecodePixelWidth = pixelWidth };
                    await source.SetSourceAsync(stream);
                }
                if (_disposed || generation != _generation || source is null) return;
                if (sources is not null) sources[index] = source;
            }
            if (_disposed || generation != _generation) return;
            if (_image is not null) _image.Source = source;
            if (_person is not null) _person.ProfilePicture = source;
            if (_fallback is not null) _fallback.Visibility = Visibility.Collapsed;
            if (index == 0) _firstSource = source;
            _frame = index; _nextFrame = DateTimeOffset.UtcNow.AddMilliseconds(asset.Delays[index]);
        }
        finally
        {
            _busy = false;
            // A decode may finish after an unload/reload. Retry on the UI queue
            // once its old generation has finished and the loader flag is clear.
            if (!_disposed && _active && generation != _generation)
                DispatcherQueue.TryEnqueue(RestoreVisuals);
        }
    }
    internal bool InViewport()
    {
        if (_disposed || !_active || !_visible() || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            DependencyObject? ancestor = this;
            while (ancestor is not null && ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
            if (ancestor is not FrameworkElement viewport) return true;
            var bounds = TransformToVisual(viewport).TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
            return bounds.Bottom > 0 && bounds.Top < viewport.ActualHeight && bounds.Right > 0 && bounds.Left < viewport.ActualWidth;
        }
        catch { return false; }
    }
    internal async void Tick(DateTimeOffset now)
    {
        _playing = true;
        if (_asset is null || _busy || _loadingAnimation || now < _nextFrame || !InViewport()) return;
        var generation = _generation;
        try
        {
            if (_asset.Frames.Length == 1)
            {
                _loadingAnimation = true;
                var animated = await _cache.GetForViewAsync(_url, true, _loadCancellation?.Token ?? CancellationToken.None);
                _loadingAnimation = false;
                if (_disposed || !_playing || !_active || generation != _generation) return;
                if (animated is null) { _clock.Remove(this); return; }
                if (animated.Frames.Length <= 1) { _clock.Remove(this); return; }
                _asset = animated; _sources = new BitmapImage?[animated.Frames.Length]; _sources[0] = _firstSource;
            }
            await SetFrameAsync((_frame + 1) % _asset.Frames.Length);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == _generation) _clock.Remove(this);
        }
        finally { _loadingAnimation = false; }
    }
    internal void PauseAnimation()
    {
        _playing = false;
        if (_disposed || _staticAsset is null || _asset == _staticAsset) return;
        _generation++;
        _asset = _staticAsset; _frame = 0; _sources = new[] { _firstSource };
        if (_image is not null) _image.Source = _firstSource;
    }
    internal void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _clock.Remove(this);
        ReleaseImages();
    }
    private void ReleaseImages()
    {
        _generation++;
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); _loadCancellation = null;
        if (_image is not null) _image.Source = null;
        if (_person is not null) _person.ProfilePicture = null;
        if (_fallback is not null) _fallback.Visibility = Visibility.Visible;
        _asset = null; _staticAsset = null; _sources = null; _firstSource = null; _frame = 0;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _active = false; _generation++; _clock.Remove(this);
        ReleaseImages();
    }
}

internal sealed class MessageAnimationClock : IDisposable
{
    private const long FirstFrameLimit = 8 * 1024 * 1024;
    private readonly Dictionary<(string Url, int Width), (BitmapImage Source, long Bytes, long Used)> _firstFrames = new();
    private readonly Dictionary<(string Url, int Width), Task<BitmapImage?>> _pendingFrames = new();
    private readonly SemaphoreSlim _frameSlots = new(32, 32);
    private long _frameBytes;
    private long _frameSequence;
    private readonly List<BiliImageView> _views = new();
    private readonly DispatcherQueueTimer _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
    private readonly Func<bool> _visible;
    private bool _disposed;
    internal MessageAnimationClock(Func<bool> visible)
    {
        _visible = visible; _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) =>
        {
            if (!_visible() || _views.Count == 0) { Suspend(); return; }
            var played = 0; var now = DateTimeOffset.UtcNow;
            foreach (var view in _views.ToArray())
                if (view.InViewport() && played < 4) { played++; view.Tick(now); }
                else view.PauseAnimation();
            if (played == 0) _timer.Stop();
        };
    }
    internal void Add(BiliImageView view) { if (_disposed) return; if (!_views.Contains(view)) _views.Add(view); Wake(); }
    internal void Remove(BiliImageView view) { _views.Remove(view); view.PauseAnimation(); if (_views.Count == 0) _timer.Stop(); }
    internal void Wake() { if (!_disposed && _visible() && _views.Count > 0) _timer.Start(); }
    internal void Suspend() { _timer.Stop(); foreach (var view in _views) view.PauseAnimation(); }
    // UI-thread cache: share decoded static images, including concurrent loads,
    // across message rows. Keys do not retain the encoded/multiframe asset.
    internal async Task<BitmapImage?> GetFirstFrameAsync(string url, BiliImageAsset asset, int width, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_disposed) return null;
        var key = (BiliImageCache.NormalizeUrl(url)?.AbsoluteUri ?? url, width);
        if (_firstFrames.TryGetValue(key, out var cached))
        {
            _firstFrames[key] = (cached.Source, cached.Bytes, ++_frameSequence);
            return cached.Source;
        }
        if (_pendingFrames.TryGetValue(key, out var pending)) return await pending.WaitAsync(cancellation);
        // Wait for a decode slot instead of leaving a permanent text fallback.
        // Views cancel this wait when detached; the shared decode stays bounded.
        await _frameSlots.WaitAsync(cancellation);
        var transferredSlot = false;
        Task<BitmapImage?> shared;
        try
        {
            if (_disposed) return null;
            if (_firstFrames.TryGetValue(key, out cached))
            {
                _firstFrames[key] = (cached.Source, cached.Bytes, ++_frameSequence);
                return cached.Source;
            }
            if (_pendingFrames.TryGetValue(key, out pending)) shared = pending;
            else
            {
                var completion = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
                shared = completion.Task; _pendingFrames.Add(key, shared);
                transferredSlot = true;
                _ = DecodeFirstFrameAsync(key, asset, completion);
            }
        }
        finally { if (!transferredSlot) _frameSlots.Release(); }
        return await shared.WaitAsync(cancellation);
    }
    private async Task DecodeFirstFrameAsync((string Url, int Width) key, BiliImageAsset asset, TaskCompletionSource<BitmapImage?> completion)
    {
        try
        {
            using var stream = await BiliImageCache.ToStreamAsync(asset.Frames[0]);
            if (_disposed) { completion.TrySetResult(null); return; }
            var source = new BitmapImage { DecodePixelWidth = key.Width };
            await source.SetSourceAsync(stream);
            if (_disposed) { completion.TrySetResult(null); return; }
            var bytes = (long)key.Width * Math.Max(1, (int)Math.Ceiling((double)asset.Height * key.Width / asset.Width)) * 4;
            while (_firstFrames.Count > 0 && (_frameBytes + bytes > FirstFrameLimit || _firstFrames.Count >= 256))
            {
                var oldest = _firstFrames.MinBy(x => x.Value.Used);
                _frameBytes -= oldest.Value.Bytes; _firstFrames.Remove(oldest.Key);
            }
            if (bytes <= FirstFrameLimit)
            {
                _firstFrames.Add(key, (source, bytes, ++_frameSequence)); _frameBytes += bytes;
            }
            completion.TrySetResult(source);
        }
        catch (Exception ex)
        {
            if (ex is OutOfMemoryException) completion.TrySetException(ex);
            else
            {
                BiliImageDiagnostics.Record(key.Url, "ui-first-frame", ex);
                completion.TrySetResult(null);
            }
        }
        finally { _pendingFrames.Remove(key); _frameSlots.Release(); }
    }
    public void Dispose()
    {
        _disposed = true; Suspend(); _views.Clear();
        _firstFrames.Clear(); _frameBytes = 0;
    }
}
