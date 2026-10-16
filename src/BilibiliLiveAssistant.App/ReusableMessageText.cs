using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace BilibiliLiveAssistant.App;

// One visible compact row retains its runs and badge controls across bindings.
// Keep inline containers attached even when the next message has a different
// text/emote shape. Rebinding must not rebuild the rich-text document each time.
internal sealed class ReusableMessageText
{
    private readonly DashboardSettings _style;
    private readonly Badge _medal, _guard, _admin;
    private readonly Run _name;
    private readonly bool _entry;
    private readonly ReusableMessageBody? _body;
    private readonly Run? _entryContent;
    private readonly Badge? _price;
    internal ReusableMessageText(Paragraph paragraph, DashboardSettings style,
        Func<LiveEmote, string, BiliImageView> create, bool entry = false, bool headerOnly = false)
    {
        _style = style; _entry = entry;
        if (entry)
        { _guard = new(paragraph, style.BadgeSize); _admin = new(paragraph, style.BadgeSize); _medal = new(paragraph, style.BadgeSize); }
        else
        { _medal = new(paragraph, style.BadgeSize); _guard = new(paragraph, style.BadgeSize); _admin = new(paragraph, style.BadgeSize); }
        _name = new Run { FontSize = style.UsernameFontSize, FontWeight = new(600), Foreground = MessageCard.Brush(style.UsernameColor) };
        paragraph.Inlines.Add(_name);
        if (entry) { _entryContent = new Run(); paragraph.Inlines.Add(_entryContent); }
        else if (headerOnly) _price = new(paragraph, style.BadgeSize);
        else _body = new(paragraph, style, create);
    }
    internal void Update(LiveEvent message)
    {
        var appearance = message.Appearance;
        _medal.Set(_style.ShowMedals && appearance is { MedalName.Length: > 0, MedalLevel: > 0 }
            ? $"{appearance.MedalName} {appearance.MedalLevel}" : "", appearance?.MedalColor ?? "#FFFFFF");
        var guard = _style.ShowIdentity ? appearance?.GuardLevel ?? 0 : 0;
        _guard.Set(guard switch { 1 => "总督", 2 => "提督", 3 => "舰长", _ => "" },
            _entry ? LiveEntryStyles.Accent(appearance) : guard == 3 ? "#4388BC" : "#945ABF");
        _admin.Set(_style.ShowIdentity && appearance?.IsAdmin == true ? "房管" : "", "#438A69");
        var name = message.Username + "  ";
        if (_name.Text != name) _name.Text = name;
        if (_entry)
        {
            _name.Foreground = MessageCard.Brush(LiveEntryStyles.UsernameColor(appearance, _style.UsernameColor));
            _entryContent!.Text = message.Content + "  ";
            return;
        }
        if (_price is not null) _price.Set(MessageCard.SuperChatPrice(message), "#8C6A2B");
        else _body!.Update(message.Content, appearance);
    }
    internal void ClearText()
    {
        _name.Text = ""; _medal.Set("", "#FFFFFF"); _guard.Set("", "#FFFFFF"); _admin.Set("", "#FFFFFF");
        if (_entryContent is not null) _entryContent.Text = "";
        _price?.Set("", "#8C6A2B"); _body?.ClearText();
    }
    private sealed class Badge
    {
        private readonly Border _border;
        private readonly TextBlock _text;
        private readonly Run _space = new();
        private string? _label, _color;
        internal Badge(Paragraph paragraph, int size)
        {
            _text = new() { FontSize = size, Foreground = MessageCard.Brush("#FFFFFF") };
            _border = new() { CornerRadius = new(9), Padding = new(5, 1, 5, 1), Margin = new(0, 0, 3, 0), Child = _text, Visibility = Visibility.Collapsed };
            paragraph.Inlines.Add(new InlineUIContainer { Child = _border }); paragraph.Inlines.Add(_space);
        }
        internal void Set(string text, string color)
        {
            if (_label == text && _color == color) return;
            if (_label != text) _text.Text = text;
            _space.Text = text.Length > 0 ? " " : "";
            _border.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (text.Length > 0) _border.Background = MessageCard.Brush(color, 0.9);
            _label = text; _color = color;
        }
    }
}

internal sealed class ReusableMessageBody
{
    private readonly Paragraph _paragraph;
    private readonly DashboardSettings _style;
    private readonly Func<LiveEmote, string, BiliImageView> _create;
    private sealed class Slot(Run text) { internal readonly Run Text = text; internal BiliImageView? Image; }
    private readonly List<Slot> _slots = new();
    internal ReusableMessageBody(Paragraph paragraph, DashboardSettings style, Func<LiveEmote, string, BiliImageView> create)
    { _paragraph = paragraph; _style = style; _create = create; }
    internal void Update(string content, LiveMessageAppearance? appearance)
    {
        var text = content.Length > 4096 ? content[..4096] + "…" : content;
        var index = 0;
        foreach (var segment in RichMessageSegments.Split(text, appearance))
        {
            if (_slots.Count == index) { var run = new Run(); _paragraph.Inlines.Add(run); _slots.Add(new(run)); }
            var slot = _slots[index++];
            if (segment.Emote is { } emote)
            {
                slot.Text.Text = "";
                if (slot.Image is null)
                {
                    slot.Image = _create(emote, segment.Text);
                    _paragraph.Inlines.Insert(_paragraph.Inlines.IndexOf(slot.Text) + 1, new InlineUIContainer { Child = slot.Image });
                }
                var height = (double)_style.EmoteSize;
                var width = height * Math.Max(1d, emote.Width) / Math.Max(1, emote.Height);
                if (width > 100) { height *= 100 / width; width = 100; }
                if (slot.Image.Width != width) slot.Image.Width = width;
                if (slot.Image.Height != height) slot.Image.Height = height;
                slot.Image.Visibility = Visibility.Visible;
                slot.Image.Rebind(emote.Url, segment.Text, 0);
            }
            else
            {
                if (slot.Text.Text != segment.Text) slot.Text.Text = segment.Text;
                if (slot.Image is { } image) { image.Deactivate(); image.Visibility = Visibility.Collapsed; }
            }
        }
        for (; index < _slots.Count; index++)
        {
            var slot = _slots[index];
            if (slot.Text.Text.Length > 0) slot.Text.Text = "";
            if (slot.Image is { } image) { image.Deactivate(); image.Visibility = Visibility.Collapsed; }
        }
    }
    internal void ClearText()
    {
        foreach (var slot in _slots)
        {
            slot.Text.Text = "";
            if (slot.Image is { } image) { image.Deactivate(); image.Visibility = Visibility.Collapsed; }
        }
    }
}
