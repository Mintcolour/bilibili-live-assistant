using BilibiliLiveAssistant.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BilibiliLiveAssistant.App;

internal sealed class MessageCard : Grid, IDisposable
{
    // Rows already have a ListView content host. This wrapper only needs one
    // child; a second ControlTemplate adds bindings and a presenter per message.
    private UIElement? Content
    {
        get => Children.Count > 0 ? Children[0] : null;
        set { Children.Clear(); if (value is not null) Children.Add(value); }
    }
    private readonly List<BiliImageView> _images = new();
    private Func<bool> _visible;
    private readonly Func<bool> _ownedVisible;
    private Paragraph? _reusableParagraph;
    private Border? _reusableBorder;
    private ReusableMessageText? _reusableText;
    private ReusableMessageBody? _reusableBody;
    private bool _entryReuse, _scReuse;
    private MessageActions? _actions;
    private TextBlock? _legacyStatus;
    private Button? _legacyRead, _legacyThanks;
    private readonly bool _follow;
    private readonly bool _entryPolicy;
    private readonly bool _important;
    private MediaPreviewGroup? _media;
    private bool _disposed;
    internal Guid RecordId { get; private set; }
    internal long RecordRevision { get; private set; }
    internal DashboardSettings MessageStyle { get; }
    internal bool Read { get; private set; }
    internal bool Thanked { get; private set; }
    internal bool HasBoundText => ContainsText(this);
    private static bool ContainsText(DependencyObject node)
    {
        if (node is TextBlock text && !string.IsNullOrWhiteSpace(text.Text)) return true;
        if (node is RichTextBlock rich && rich.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<Run>().Any(r => !string.IsNullOrWhiteSpace(r.Text)))) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (ContainsText(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
    internal MediaPreviewSettings MediaStyle { get; }
    public MessageCard(DashboardRecord record, LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible, bool important, DashboardSettings? previewStyle = null)
    {
        _visible = visible; _important = important;
        // Image consumers follow the card's current binding when a plain row is
        // recycled; they must not close over its original DashboardRecord.
        visible = _ownedVisible = () => _visible();
        var message = record.Event;
        _follow = LiveInteractions.IsFollow(message);
        _entryPolicy = LiveInteractions.IsEntry(message);
        var style = previewStyle ?? vm.Settings.Dashboard;
        RecordId = record.Id; RecordRevision = record.Revision; MessageStyle = style; MediaStyle = vm.Settings.MediaPreview; Read = record.IsRead; Thanked = record.IsThanked;
        HorizontalAlignment = HorizontalAlignment.Stretch;
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
        if (style.ShowsAvatar(message))
        {
            var avatar = new BiliImageView(vm.Images, message.Appearance?.AvatarUrl ?? "", message.Username, style.AvatarSize, style.AvatarSize, false, animations, visible, true, avatarUid: AvatarUid(message));
            avatar.VerticalAlignment = VerticalAlignment.Top; _images.Add(avatar); layout.Children.Add(avatar);
        }
        var content = new StackPanel { Spacing = 5 };
        var header = new RichTextBlock { FontFamily = MessageVisualResources.Font(style.FontFamily), FontSize = style.UsernameFontSize, Foreground = Brush(style.UsernameColor), Opacity = style.TextOpacity };
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
            _legacyStatus = new TextBlock { Text = record.IsThanked ? "✓ 已感谢" : record.IsRead ? "已读" : "● 未读", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(record.IsRead ? "#91A0B5" : "#FFD878") };
            actions.Children.Add(_legacyStatus);
            if (!record.IsRead)
            {
                var read = new Button { Content = "已读", FontSize = 12, Padding = new(6, 2, 6, 2) };
                _legacyRead = read;
                read.Click += (_, _) => vm.Dashboard.Mark(record, false); actions.Children.Add(read);
            }
            if (!record.IsThanked)
            {
                var thanks = new Button { Content = "已感谢", FontSize = 12, Padding = new(6, 2, 6, 2) };
                _legacyThanks = thanks;
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
        var follow = LiveInteractions.IsFollow(message);
        var like = LiveInteractions.IsLike(message);
        var appearance = message.Appearance;
        var effect = appearance?.EntryEffect;
        Margin = new(0, 0, 0, Math.Min(style.MessageSpacing, 5));
        var layout = new Grid { ColumnSpacing = 7 };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        if (style.ShowsAvatar(message))
        {
            var size = Math.Clamp(style.AvatarSize, 28, 40);
            var frame = style.ShowIdentity && appearance?.AvatarFrameUrl is { Length: > 0 };
            var avatarLayout = new Grid { Width = size + (frame ? 6 : 0), Height = size + (frame ? 6 : 0), VerticalAlignment = VerticalAlignment.Top };
            var avatar = new BiliImageView(vm.Images, appearance?.AvatarUrl ?? "", message.Username, size, size, false, animations, visible, true, avatarUid: AvatarUid(message));
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
            FontFamily = MessageVisualResources.Font(style.FontFamily), FontSize = style.FontSize, Foreground = Brush(follow ? "#FFD275" : like ? "#FFBF98" : effect?.TextColor ?? "#A7B1C2"),
            TextWrapping = TextWrapping.Wrap, Opacity = style.TextOpacity, VerticalAlignment = VerticalAlignment.Center,
            LineHeight = style.FontSize * 1.25 + Math.Min(style.LineSpacing, 4),
        };
        var paragraph = new Paragraph { Margin = new(0) };
        var reusableEntry = IsReusableEntry(message);
        if (reusableEntry)
        {
            _entryReuse = true; _reusableParagraph = paragraph;
            _reusableText = new(paragraph, style,
                (emote, fallback) => CreateEmote(emote, fallback, false, style, vm, animations, _ownedVisible), entry: true);
            _reusableText.Update(message);
        }
        if (!reusableEntry)
        {
            if (follow) AddBadge(paragraph, LiveInteractions.FollowLabel(message), "#AD3E6C", style.BadgeSize);
            if (like) AddBadge(paragraph, "点赞", "#A75432", style.BadgeSize);
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
                FontWeight = new(600), Foreground = Brush(follow ? "#FFE4EF" : LiveEntryStyles.UsernameColor(appearance, style.UsernameColor)) });
            paragraph.Inlines.Add(new Run { Text = message.Content + "  ", FontWeight = new(follow ? (ushort)600 : (ushort)400) });
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
        if (follow && vm.Dashboard.Interactions.Contains(record))
        {
            layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            FrameworkElement thanks;
            if (record.IsThanked) thanks = new TextBlock { Text = "✓ 已感谢", FontSize = 11, Foreground = Brush("#FFB2CF"), VerticalAlignment = VerticalAlignment.Center };
            else
            {
                var button = new Button { Content = "已感谢", FontSize = 11, Padding = new(6, 3, 6, 3), VerticalAlignment = VerticalAlignment.Center };
                button.Click += (_, _) => vm.Dashboard.Mark(record, true); thanks = button;
            }
            Grid.SetColumn(thanks, 2); layout.Children.Add(thanks);
        }
        var border = new Border { Padding = follow ? new(7, 5, 7, 5) : new(3, 2, 3, 2), Child = layout,
            CornerRadius = new(6), BorderThickness = follow ? new(3, 0, 0, 0) : new(0), BorderBrush = follow ? Brush("#FF8AB3") : null,
            Background = follow ? Brush("#AD3E6C", 0.16) : null };
        DashboardUi.Describe(border, $"{message.Username} · {message.OccurredAt:yyyy-MM-dd HH:mm:ss}" +
            (appearance?.InteractionHint is { Length: > 0 } hint ? $" · {hint}" : ""));
        if (reusableEntry) _reusableBorder = border;
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
        if (style.ShowsAvatar(message))
        {
            var size = Math.Clamp(style.AvatarSize, 24, 32);
            var avatar = new BiliImageView(vm.Images, message.Appearance?.AvatarUrl ?? "", message.Username, size, size, false, animations, visible, true, avatarUid: AvatarUid(message));
            avatar.VerticalAlignment = VerticalAlignment.Top;
            _images.Add(avatar); layout.Children.Add(avatar);
        }
        var text = new RichTextBlock
        {
            FontFamily = MessageVisualResources.Font(style.FontFamily), FontSize = sc ? style.UsernameFontSize : style.FontSize,
            Foreground = Brush(style.TextColor), Opacity = style.TextOpacity, TextWrapping = TextWrapping.Wrap,
            LineHeight = style.FontSize * 1.25 + Math.Min(style.LineSpacing, 4), LineStackingStrategy = LineStackingStrategy.MaxHeight,
            VerticalAlignment = fullEmote && !sc ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        var paragraph = new Paragraph { Margin = new(0) };
        if (IsReusable(record, style, important))
        {
            _reusableParagraph = paragraph; _reusableBorder = border; _scReuse = sc;
            _reusableText = new(paragraph, style,
                (emote, fallback) => CreateEmote(emote, fallback, false, style, vm, animations, _ownedVisible), headerOnly: sc);
            _reusableText.Update(message);
        }
        else
        {
            AddIdentity(paragraph, message.Appearance, style);
            paragraph.Inlines.Add(new Run { Text = message.Username + "  ", FontSize = style.UsernameFontSize,
                FontWeight = new(600), Foreground = Brush(style.UsernameColor) });
            if (sc)
            {
                var price = SuperChatPrice(message);
                if (price.Length > 0) AddBadge(paragraph, price, "#8C6A2B", style.BadgeSize);
            }
            else if (!fullEmote) AppendInlineBody(paragraph, message.Content, message.Appearance, style, vm, animations, visible);
        }
        text.Blocks.Add(paragraph);
        FrameworkElement messageContent = text;
        if (fullEmote && !sc)
            messageContent = new StackPanel { Spacing = 4, Children = { text, BuildBody(message.Content, message.Appearance, style, vm, animations, visible) } };
        Grid.SetColumn(messageContent, 1); layout.Children.Add(messageContent);
        if (important)
        {
            var actions = _actions = new MessageActions(record, vm);
            Grid.SetColumn(actions, 2); layout.Children.Add(actions);
        }
        if (sc)
        {
            border.Child = new StackPanel { Spacing = 7, Children = { layout, BuildBody(SuperChatBody(message), message.Appearance, style, vm, animations, visible, reusable: _scReuse) } };
        }
        else border.Child = layout;
        return border;
    }

    private static bool IsReusableEntry(LiveEvent message) => LiveInteractions.IsEntry(message) && message.Username.Length > 0
        && !LiveInteractions.IsFollow(message) && message.Appearance?.EntryEffect is null
        && string.IsNullOrEmpty(message.Appearance?.AvatarFrameUrl) && string.IsNullOrEmpty(message.Appearance?.InteractionIconUrl)
        && string.IsNullOrEmpty(message.Appearance?.InteractionLabel) && string.IsNullOrEmpty(message.Appearance?.InteractionHint);
    internal static bool IsReusable(DashboardRecord record, DashboardSettings style, bool important) =>
        IsReusableEntry(record.Event) || (style.CompactMessages
        && record.Event.Type is (LiveEventType.Danmaku or LiveEventType.Gift or LiveEventType.Guard or LiveEventType.SuperChat)
        && record.Event.MediaReferences.Count == 0 && record.Event.Appearance?.FullEmote is null);
    internal bool RetireForReuse()
    {
        if (_disposed || _reusableParagraph is null || _images.Count > 8) return false;
        Deactivate(); _visible = static () => false; _actions?.ClearBinding();
        _reusableText!.ClearText(); RecordId = Guid.Empty;
        _reusableBody?.ClearText();
        foreach (var image in _images) image.Rebind("", "", 0);
        return true;
    }
    internal bool TryRebind(DashboardRecord record, LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible, bool important)
    {
        if (_disposed || _reusableParagraph is null || _important != important || !IsReusable(record, vm.Settings.Dashboard, important)
            || _entryReuse != IsReusableEntry(record.Event)
            || _scReuse != (record.Event.Type == LiveEventType.SuperChat)
            || !MatchesStyle(vm.Settings.Dashboard) || MediaStyle != vm.Settings.MediaPreview) return false;
        _visible = visible;
        RecordId = record.Id; RecordRevision = record.Revision; Read = record.IsRead; Thanked = record.IsThanked;
        _actions?.Bind(record);
        var message = record.Event; var style = vm.Settings.Dashboard;
        foreach (var image in _images) if (image.IsAvatar) image.Rebind(message.Appearance?.AvatarUrl ?? "", message.Username, AvatarUid(message));
        _reusableText!.Update(message);
        if (_scReuse) _reusableBody!.Update(SuperChatBody(message), message.Appearance);
        DashboardUi.Describe(_reusableBorder!, $"{message.Username} · {message.OccurredAt:yyyy-MM-dd HH:mm:ss}");
        return true;
    }

    internal bool UpdateReadState(DashboardRecord record)
    {
        if (Read == record.IsRead && Thanked == record.IsThanked) return true;
        if (_follow) return false;
        Read = record.IsRead; Thanked = record.IsThanked; _actions?.Bind(record);
        if (_legacyStatus is not null)
        {
            _legacyStatus.Text = Thanked ? "✓ 已感谢" : Read ? "已读" : "● 未读";
            _legacyStatus.Foreground = Brush(Read ? "#91A0B5" : "#FFD878");
            if (_legacyRead is not null) _legacyRead.Visibility = Read ? Visibility.Collapsed : Visibility.Visible;
            if (_legacyThanks is not null) _legacyThanks.Visibility = Thanked ? Visibility.Collapsed : Visibility.Visible;
        }
        return true;
    }
    internal bool MatchesStyle(DashboardSettings other) => MessageStyle.HasSameMessageStyle(other, _entryPolicy);

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
        LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible, bool reusable = false)
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
            FontFamily = MessageVisualResources.Font(style.FontFamily), FontSize = style.FontSize, Foreground = Brush(style.TextColor),
            Opacity = style.TextOpacity, TextWrapping = TextWrapping.Wrap, LineHeight = style.FontSize * 1.35 + style.LineSpacing,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
        };
        var paragraph = new Paragraph { Margin = new(0) };
        if (reusable)
        {
            _reusableBody = new(paragraph, style, (emote, fallback) => CreateEmote(emote, fallback, false, style, vm, animations, _ownedVisible));
            _reusableBody.Update(content, appearance);
        }
        else AppendInlineBody(paragraph, content, appearance, style, vm, animations, visible);
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

    private static string SuperChatBody(LiveEvent message)
    {
        var content = message.Content;
        if (content.StartsWith('¥') && content.IndexOf(' ') is var space && space > 0 && SuperChatPrice(message).Length > 0) return content[(space + 1)..];
        return content;
    }
    internal static string SuperChatPrice(LiveEvent message)
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
    // Likes can arrive in bursts without faces. Keep supplied images, but do
    // not add per-user profile requests just to decorate a short notification.
    internal static long AvatarUid(LiveEvent value) => value.Extra.GetValueOrDefault("sample") is true || LiveInteractions.IsLike(value) ? 0 : value.Uid;
    internal static SolidColorBrush Brush(string hex, double opacity = 1)
    {
        return MessageVisualResources.Brush(hex, opacity);
    }
    // Realization is explicit: inline children must not gate their image load on
    // their own Loaded/IsLoaded state. The card/list owns the resource lifetime.
    internal void Activate(double rasterizationScale = 1)
    {
        if (_disposed) return;
        if (!_visible()) { Deactivate(); return; }
        foreach (var image in _images)
            if (image.Visibility == Visibility.Visible) image.Activate(XamlRoot?.RasterizationScale ?? rasterizationScale);
            else image.Deactivate();
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
        DashboardUi.ReleaseDescriptions(this);
        ReleaseContentTree(this);
        _visible = static () => false;
        _reusableParagraph?.Inlines.Clear(); _reusableParagraph = null; _reusableBorder = null; _reusableText = null; _reusableBody = null;
        foreach (var image in _images) image.Dispose(); _images.Clear();
        _media?.Dispose(); _media = null;
        _actions?.Dispose(); _actions = null;
        _legacyStatus = null; _legacyRead = _legacyThanks = null;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        Content = null;
    }
    private static void ReleaseContentTree(DependencyObject node)
    {
        // RichTextBlock's document owns inline UI separately from the visual
        // parent. Clear both links before a discarded row loses its wrapper.
        if (node is RichTextBlock text)
        {
            foreach (var block in text.Blocks)
                if (block is Paragraph paragraph) ReleaseInlines(paragraph.Inlines);
            text.Blocks.Clear();
        }
        for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            ReleaseContentTree(VisualTreeHelper.GetChild(node, i));
        if (node is Panel panel) panel.Children.Clear();
        else if (node is Border border) border.Child = null;
        else if (node is ContentControl control) { control.Content = null; control.Template = null; }
        else if (node is Image image) image.Source = null;
    }
    private static void ReleaseInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
            if (inline is InlineUIContainer container)
            {
                if (container.Child is { } child) ReleaseContentTree(child);
                container.Child = null;
            }
            else if (inline is Span span) ReleaseInlines(span.Inlines);
        inlines.Clear();
    }
    internal bool IsDisposed => _disposed;
}

internal sealed class BiliImageView : Grid, IDisposable
{
    private readonly BiliImageCache _cache;
    private string _url;
    private long _avatarUid;
    private string _displayUrl;
    private readonly bool _animate;
    private readonly MessageAnimationClock _clock;
    private Func<bool> _visible;
    private Image? _image;
    private ImageSource? _boundSource;
    private string _automationName;
    private readonly bool _avatar;
    private TextBlock? _fallback;
    private UiImageSourceService.Lease? _first;
    private BiliAnimationAsset? _animation;
    private UiPixelSource? _animationSource;
    private CancellationTokenSource? _loadCancellation, _animationCancellation;
    private int _generation, _animationGeneration, _frame;
    private bool _disposed, _active, _loading, _busy, _playing;
    private double _rasterizationScale = 1;
    private DateTimeOffset _nextFrame;
    private int RequestedDecodeLimit => BiliImageCache.SizeTier(Math.Max(
        double.IsFinite(Width) ? Width : ActualWidth > 0 ? ActualWidth : 256,
        double.IsFinite(Height) ? Height : ActualHeight > 0 ? ActualHeight : 256) * _rasterizationScale);
    private int RequestedWidth => Math.Clamp((int)Math.Ceiling(
        (double.IsFinite(Width) ? Width : ActualWidth > 0 ? ActualWidth : 256) * _rasterizationScale), 1, 256);

    public BiliImageView(BiliImageCache cache, string url, string fallback, double width, double height, bool animate,
        MessageAnimationClock clock, Func<bool> visible, bool avatar = false, bool fill = false, long avatarUid = 0)
    {
        Interlocked.Increment(ref ImageResourceCounters.Views);
        _cache = cache; _url = url; _animate = animate && !avatar; _clock = clock; _visible = visible; _automationName = fallback;
        _avatarUid = avatar ? avatarUid : 0; _displayUrl = url; _avatar = avatar;
        Width = width; Height = height;
        if (avatar)
        {
            // A message avatar needs a circle, initials and a picture, rather
            // than the badge/presence/state templates of PersonPicture.
            var initials = new System.Globalization.StringInfo(fallback.Trim());
            _fallback = new TextBlock { Text = initials.LengthInTextElements > 0 ? initials.SubstringByTextElements(0, Math.Min(2, initials.LengthInTextElements)) : "?",
                FontSize = Math.Max(10, width * 0.38), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = MessageCard.Brush("#FFFFFF") };
            _image = new Image { Stretch = Stretch.UniformToFill };
            var picture = new Grid(); picture.Children.Add(_fallback); picture.Children.Add(_image);
            Children.Add(new Border { CornerRadius = new(width / 2), Background = MessageCard.Brush("#4B4B4B"), Child = picture });
        }
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
        if (!_visible()) { Deactivate(); return; }
        var sizeChanged = Math.Abs(_rasterizationScale - rasterizationScale) > 0.001;
        if (!_active) Interlocked.Increment(ref ImageResourceCounters.ActiveViews);
        _active = true; _rasterizationScale = rasterizationScale;
        if (sizeChanged) { _clock.Remove(this); ReleaseImages(); }
        RestoreVisuals();
    }
    internal bool IsAvatar => _avatar;
    internal void Rebind(string url, string fallback, long uid)
    {
        if (_disposed) return;
        Interlocked.Increment(ref ImageResourceCounters.RebindAttempts);
        var initials = new System.Globalization.StringInfo(fallback.Trim());
        var label = _avatar ? initials.LengthInTextElements > 0 ? initials.SubstringByTextElements(0, Math.Min(2, initials.LengthInTextElements)) : "?" : fallback;
        if (_fallback!.Text != label) _fallback.Text = label;
        if (_automationName != fallback) { _automationName = fallback; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, fallback); }
        if (_url == url && _avatarUid == (_avatar ? uid : 0)) return;
        Interlocked.Increment(ref ImageResourceCounters.RebindChanges);
        Deactivate(); _clock.Remove(this); ReleaseImages(); _url = url; _displayUrl = url; _avatarUid = _avatar ? uid : 0;
    }
    internal async void RestoreVisuals()
    {
        if (_disposed || !_active || _loading) return;
        if (_first is not null) { if (_animate && _first.Value.CanAnimate) _clock.Add(this); return; }
        _loading = true; var generation = _generation;
        var started = PerformanceCounters.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var waited = false;
        var cancellation = (_loadCancellation ??= new()).Token;
        UiImageSourceService.Lease? acquired = null;
        try
        {
            if (_avatar)
            {
                var avatar = _cache.Avatars.ResolveAsync(_avatarUid, _url, cancellation);
                waited |= !avatar.IsCompletedSuccessfully;
                _displayUrl = await avatar;
                if (_disposed || !_active || !_visible() || generation != _generation) return;
                if (_displayUrl.Length == 0) { Interlocked.Increment(ref ImageResourceCounters.MissingAvatars); return; }
            }
#if MEMORY_SOAK
            if (!App.CreateImageSources)
            {
                await _cache.GetStaticForViewAsync(_displayUrl, cancellation, RequestedDecodeLimit, _avatar ? BiliImageRole.Avatar : BiliImageRole.Content);
                return;
            }
#endif
            var source = _cache.Sources.AcquireStaticSourceAsync(_displayUrl, RequestedWidth, RequestedDecodeLimit, cancellation, _avatar ? BiliImageRole.Avatar : BiliImageRole.Content);
            waited |= !source.IsCompletedSuccessfully;
            acquired = await source;
            if (_disposed || !_active || !_visible() || generation != _generation || acquired is null) return;
            _first = acquired; acquired = null;
            Bind(_first.Value.Source);
            ImageResourceCounters.BoundReady(started, waited);
            if (_animate && _first.Value.CanAnimate) _clock.Add(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { BiliImageDiagnostics.Record(_displayUrl, "view-restore", ex); }
        finally
        {
            acquired?.Dispose(); _loading = false;
            if (!_disposed && _active && generation != _generation) RestoreVisuals();
        }
    }
    private void Bind(ImageSource source)
    {
        Interlocked.Increment(ref ImageResourceCounters.BindingAttempts);
#if MEMORY_SOAK
        if (!App.AttachImageSources) return;
#endif
        if (_image is not null && !ReferenceEquals(_boundSource, source)) { _image.Source = source; _boundSource = source; Interlocked.Increment(ref ImageResourceCounters.BindingChanges); }
        if (_avatar) Interlocked.Increment(ref ImageResourceCounters.AvatarBindings);
        if (_fallback is not null) _fallback.Visibility = Visibility.Collapsed;
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
        if (_first is null || _busy || now < _nextFrame || !InViewport()) return;
        _busy = true; var generation = _animationGeneration;
        var cancellation = (_animationCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(
            _loadCancellation?.Token ?? CancellationToken.None)).Token;
        try
        {
            if (_animation is null)
            {
                var animation = await _cache.GetAnimationForViewAsync(_url, cancellation, RequestedDecodeLimit);
                if (!CurrentAnimation(generation)) return;
                if (animation is null || animation.Frames.Length <= 1) { _clock.Remove(this); return; }
                _animation = animation;
            }
            var asset = _animation;
            var index = (_frame + 1) % asset.Frames.Length;
            if (index == 0)
            {
                if (_first is not null) Bind(_first.Value.Source);
                _animationSource?.Dispose(); _animationSource = null;
            }
            else
            {
                UiPixelSource? source = null;
                try
                {
                    var pixels = await BiliImageCache.DecodeDisplayFrameAsync(asset.Frames[index], RequestedDecodeLimit, cancellation);
                    if (!CurrentAnimation(generation)) return;
                    source = await UiPixelSource.CreateAsync(pixels, RequestedWidth, false, RequestedDecodeLimit, cancellation);
                    if (!CurrentAnimation(generation)) return;
                    Bind(source.Source);
                    var previous = _animationSource; _animationSource = source; source = null; previous?.Dispose();
                }
                finally { source?.Dispose(); }
            }
            _frame = index; _nextFrame = DateTimeOffset.UtcNow.AddMilliseconds(asset.Delays[index]);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (CurrentAnimation(generation)) _clock.Remove(this); }
        finally { _busy = false; }
    }
    private bool CurrentAnimation(int generation) => !_disposed && _active && _playing && generation == _animationGeneration;
    internal void PauseAnimation()
    {
        _playing = false; _animationGeneration++;
        _animationCancellation?.Cancel(); _animationCancellation?.Dispose(); _animationCancellation = null;
        if (!_disposed && _first is not null) Bind(_first.Value.Source);
        _animationSource?.Dispose(); _animationSource = null;
        _animation = null; _frame = 0; _nextFrame = default;
    }
    internal void Deactivate()
    {
        if (!_active) return; _active = false;
        Interlocked.Decrement(ref ImageResourceCounters.ActiveViews);
        _clock.Remove(this); ReleaseImages();
    }
    private void ReleaseImages()
    {
        _generation++;
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); _loadCancellation = null;
        if (_image is not null) _image.Source = null;
        _boundSource = null;
        if (_fallback is not null) _fallback.Visibility = Visibility.Visible;
        _first?.Dispose(); _first = null;
    }
    public void Dispose()
    {
        if (_disposed) return; Deactivate(); _disposed = true;
        _clock.Remove(this); ReleaseImages();
        _visible = static () => false; _url = _displayUrl = ""; _avatarUid = 0;
        Children.Clear(); _image = null; _fallback = null;
        Interlocked.Decrement(ref ImageResourceCounters.Views);
    }
}

// Animation scheduling only: static source ownership belongs to the shared service.
internal sealed class MessageAnimationClock : IDisposable
{
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
    public void Dispose() { _disposed = true; Suspend(); _views.Clear(); }
}
