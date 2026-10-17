using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace BilibiliLiveAssistant.App;

// At most five active chips plus five short-lived departing chips; no polling.
internal sealed class HotPhrasePanel : Canvas, IDisposable
{
    private sealed class Chip : IDisposable
    {
        public readonly Border Root = new();
        public readonly CompositeTransform Transform = new();
        public readonly HotBubbleMotion Motion = new();
        public readonly Storyboard Animation = new();
        public readonly List<BiliImageView> Images = new();
        public readonly TextBlock Count = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = MessageCard.Brush("#E9CF92") };
        public HotBubbleLayout? Layout;
        public string Family = "";
        public EventHandler<object>? Completion;
        public void Dispose() { Animation.Stop(); if (Completion is not null) Animation.Completed -= Completion; Animation.Children.Clear(); foreach (var image in Images) image.Dispose(); Images.Clear(); Root.Child = null; }
    }
    private readonly LiveAssistantViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly MessageAnimationClock _clock;
    private readonly bool _ownsClock;
    private readonly Dictionary<string, Chip> _chips = new();
    private IReadOnlyList<HotPhrase> _source = Array.Empty<HotPhrase>();
    private HotPhraseSettings _style;
    private bool _active, _disposed, _ready;
    private readonly bool _preview;
    private IReadOnlyList<HotPhrase>? _renderedSource;
    private HotPhraseSettings? _renderedStyle;
    private double _renderedWidth = -1;
    private string _renderedFamily = "";
    public HotPhrasePanel(LiveAssistantViewModel vm, Func<bool> visible, bool preview = false, MessageAnimationClock? clock = null)
    {
        _vm = vm; _visible = visible; _style = vm.Settings.HotPhrases; _preview = preview;
        _ownsClock = clock is null; _clock = clock ?? new(() => _active && _visible()); HorizontalAlignment = HorizontalAlignment.Stretch;
        SizeChanged += (_, _) => { if (_active) Sync(); };
        Loaded += (_, _) => { if (_active) foreach (var chip in _chips.Values) foreach (var image in chip.Images) image.Activate(XamlRoot?.RasterizationScale ?? 1); };
    }
    internal void Update(IReadOnlyList<HotPhrase> source, HotPhraseSettings style)
    { _source = source; _style = style; if (_active) Sync(); }
    internal void SetActive(bool active)
    {
        if (_disposed || active == _active) return; _active = active;
        if (active) Sync(); else Clear();
    }
    private void Sync()
    {
        if (_disposed || !_active || !_visible()) return;
        var family = _vm.Settings.Dashboard.FontFamily;
        if (ReferenceEquals(_renderedSource, _source) && _renderedStyle == _style && _renderedWidth == ActualWidth && _renderedFamily == family) return;
        _renderedSource = _source; _renderedStyle = _style; _renderedWidth = ActualWidth; _renderedFamily = family;
        var rowHeight = HotBubbleLayouts.RowHeight(_style);
        var layouts = HotBubbleLayouts.Create(_source, _style, (int)Math.Max(0, ActualWidth), rowHeight * 2, 2, family);
        var keep = new HashSet<string>(); var now = HotPhraseSprite.Now;
        var animate = _ready && _style.AnimationEnabled; var seconds = animate ? _style.AnimationDurationMs / 1000d : 0;
        foreach (var layout in layouts)
        {
            var key = HotBubbleLayouts.Key(layout.Phrase); keep.Add(key);
            var isNew = !_chips.TryGetValue(key, out var chip);
            if (isNew)
            {
                chip = new(); chip.Root.RenderTransform = chip.Transform; Children.Add(chip.Root); _chips.Add(key, chip);
                chip.Motion.Start(layout.Bounds.X, layout.Bounds.Y, now, seconds, animate);
            }
            var moved = chip!.Layout?.Bounds.Location != layout.Bounds.Location || chip.Motion.Leaving;
            if (!isNew) { if (!animate) chip.Motion.Start(layout.Bounds.X, layout.Bounds.Y, now, 0, false); else chip.Motion.Place(layout.Bounds.X, layout.Bounds.Y, now, seconds); }
            var rebuild = chip!.Layout is null || chip.Layout.Bounds.Size != layout.Bounds.Size || chip.Layout.CountWidth != layout.CountWidth
                || !chip.Layout.Pieces.SequenceEqual(layout.Pieces) || chip.Family != family || chip.Root.Tag is not HotPhraseSettings prior || prior != _style;
            chip.Root.Tag = _style; chip.Count.Text = "×" + layout.Phrase.Count; chip.Count.FontSize = _style.FontSize;
            chip.Root.Width = layout.Bounds.Width; chip.Root.Height = layout.Bounds.Height;
            chip.Root.Background = MessageCard.Brush("#232834", _style.BackgroundOpacity);
            chip.Root.BorderBrush = MessageCard.Brush(HotBubbleLayouts.Key(_source[0]) == key ? "#E0B564" : "#58667D");
            chip.Root.BorderThickness = new(1); chip.Root.CornerRadius = new(9);
            if (rebuild) { Build(chip, layout, family); chip.Family = family; }
            chip.Layout = layout;
            if (isNew || moved || !animate) Animate(key, chip, now, seconds);
        }
        foreach (var pair in _chips.Where(p => !keep.Contains(p.Key)).ToArray())
        {
            if (!animate) { Remove(pair.Key); continue; }
            if (!pair.Value.Motion.Leaving) { pair.Value.Motion.Leave(now, seconds); Animate(pair.Key, pair.Value, now, seconds); }
        }
        foreach (var key in _chips.Where(p => p.Value.Motion.Leaving).Skip(5).Select(p => p.Key).ToArray()) Remove(key);
        // Keep the bottom dock at its final height while departing chips fade out.
        UpdateHeight();
        Opacity = _style.Opacity;
        _ready = ActualWidth > 0;
    }
    private void Build(Chip chip, HotBubbleLayout layout, string family)
    {
        foreach (var image in chip.Images) image.Dispose(); chip.Images.Clear();
        var content = new Grid { ColumnSpacing = 8, Padding = new(9, 0, 8, 0) };
        content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); content.ColumnDefinitions.Add(new() { Width = new(layout.CountWidth) });
        var body = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var piece in layout.Pieces)
        {
            if (piece.Emote is { } emote && !_preview)
            {
                var image = new BiliImageView(_vm.Images, emote.Url, piece.Text, Math.Max(1, piece.Width - 3), _style.EmoteSize,
                    _vm.Settings.Dashboard.AnimateEmotes, _clock, () => _active && _visible());
                chip.Images.Add(image); body.Children.Add(image); image.Activate(XamlRoot?.RasterizationScale ?? 1);
            }
            else body.Children.Add(new TextBlock { Text = piece.Emote is not null ? "😀" : piece.Text, Width = piece.Width,
                FontSize = piece.Emote is not null ? _style.EmoteSize : _style.FontSize, FontFamily = MessageVisualResources.Font(family),
                TextTrimming = TextTrimming.CharacterEllipsis, Foreground = MessageCard.Brush("#F1F3F8"), VerticalAlignment = VerticalAlignment.Center });
        }
        var shadow = new ThemeShadow(); chip.Root.Shadow = shadow; chip.Root.Translation = new System.Numerics.Vector3(0, 0, 4);
        content.Children.Add(body); Grid.SetColumn(chip.Count, 1);
        // Detach the reused counter before attaching it to a new parent.
        if (chip.Count.Parent is Panel parent) parent.Children.Remove(chip.Count);
        content.Children.Add(chip.Count); chip.Root.Child = content;
    }
    private void Animate(string key, Chip chip, double now, double seconds)
    {
        var sampled = chip.Motion.Sample(now); var target = chip.Motion.Sample(now + seconds + 1);
        chip.Animation.Stop(); if (chip.Completion is not null) { chip.Animation.Completed -= chip.Completion; chip.Completion = null; } chip.Animation.Children.Clear();
        chip.Transform.TranslateX = sampled.X; chip.Transform.TranslateY = sampled.Y; chip.Root.Opacity = sampled.Alpha;
        if (seconds <= 0 || !chip.Motion.Running(now))
        { chip.Transform.TranslateX = target.X; chip.Transform.TranslateY = target.Y; chip.Root.Opacity = target.Alpha; return; }
        void Add(DependencyObject value, string property, double from, double to)
        {
            if (Math.Abs(from - to) < .001) return;
            var animation = new DoubleAnimation { From = from, To = to, Duration = new(TimeSpan.FromSeconds(seconds)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(animation, value); Storyboard.SetTargetProperty(animation, property); chip.Animation.Children.Add(animation);
        }
        Add(chip.Transform, "TranslateX", sampled.X, target.X); Add(chip.Transform, "TranslateY", sampled.Y, target.Y); Add(chip.Root, "Opacity", sampled.Alpha, target.Alpha);
        if (chip.Animation.Children.Count == 0) return;
        chip.Completion = Completed; chip.Animation.Completed += Completed;
        void Completed(object? sender, object e)
        {
            chip.Animation.Completed -= Completed;
            chip.Completion = null; chip.Animation.Stop();
            chip.Transform.TranslateX = target.X; chip.Transform.TranslateY = target.Y; chip.Root.Opacity = target.Alpha;
            if (_chips.TryGetValue(key, out var current) && ReferenceEquals(current, chip) && chip.Motion.Leaving) { Remove(key); UpdateHeight(); }
        }
        chip.Animation.Begin();
    }
    internal void Replay() { if (!_active) return; Clear(); _ready = true; Sync(); }
    internal void RoomReset() { Clear(); _source = Array.Empty<HotPhrase>(); }
    private void UpdateHeight() => Height = _chips.Count == 0 ? 0 : Math.Min(HotBubbleLayouts.RowHeight(_style) * 2, _chips.Values.Max(c => c.Layout?.Bounds.Bottom ?? 0) + 4);
    private void Remove(string key) { if (!_chips.Remove(key, out var chip)) return; Children.Remove(chip.Root); chip.Dispose(); }
    private void Clear() { foreach (var chip in _chips.Values) chip.Dispose(); _chips.Clear(); Children.Clear(); if (_ownsClock) _clock.Suspend(); _ready = false; _renderedSource = null; _renderedWidth = -1; Height = 0; }
    public void Dispose() { if (_disposed) return; _disposed = true; Clear(); if (_ownsClock) _clock.Dispose(); }
}
