using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class DanmakuCardPanel : Grid, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly MessageAnimationClock _clock;
    private readonly Func<bool> _visible;
    private readonly Grid _slots = new() { RowSpacing = 6, ColumnSpacing = 6 };
    private readonly List<SlotView> _views = new();
    private readonly TextBlock _feedback = new() { FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
    private bool _active, _disposed;
    private int _columns, _rows;
    private long _version = -1;
    public event Action? ToggleRequested;
    public DanmakuCardPanel(LiveAssistantViewModel vm, MessageAnimationClock clock, Func<bool> visible)
    {
        _vm = vm; _clock = clock; _visible = visible;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var header = new Grid { MinHeight = 32, ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(_feedback);
        var clear = DashboardUi.Button("\uE894", "清除卡片隐藏清单", 28);
        clear.Click += (_, _) => { vm.CardBoard.ClearHidden(); _feedback.Text = "已清除隐藏清单"; };
        var mode = new Button { Content = "列表", Padding = new(7, 3, 7, 3), MinHeight = 28 };
        mode.Click += (_, _) => ToggleRequested?.Invoke(); SetColumn(clear, 1); SetColumn(mode, 2); header.Children.Add(clear); header.Children.Add(mode);
        Children.Add(header); SetRow(_slots, 1); Children.Add(_slots);
        _slots.SizeChanged += OnSizeChanged;
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) { if (_active) Sync(true); }
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        foreach (var view in _views) view.SetActive(active && _visible());
        if (active) Sync(true);
    }
    public void Sync(bool styleChanged = false)
    {
        if (_disposed || !_active || !_visible() || _slots.ActualWidth <= 0 || _slots.ActualHeight <= 0) return;
        var settings = _vm.Settings.Cards;
        var columns = Math.Min(settings.Columns, Math.Max(1, (int)(_slots.ActualWidth / 200)));
        var height = Math.Min(settings.Height, Math.Max(80, _slots.ActualHeight));
        var rows = Math.Clamp((int)((_slots.ActualHeight + 6) / (height + 6)), 1, Math.Min(8, 32 / columns));
        if (_columns != columns || _rows != rows || styleChanged)
        {
            _columns = columns; _rows = rows;
            _slots.ColumnDefinitions.Clear(); _slots.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) _slots.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            for (var i = 0; i < rows; i++) _slots.RowDefinitions.Add(new() { Height = new(height) });
            _vm.CardBoard.Resize(columns * rows);
            while (_views.Count > columns * rows) { var view = _views[^1]; view.Dispose(); _slots.Children.Remove(view); _views.RemoveAt(_views.Count - 1); }
            while (_views.Count < columns * rows)
            {
                var view = new SlotView(_vm, _clock, () => _active && _visible(), error => _feedback.Text = error);
                _views.Add(view); _slots.Children.Add(view);
            }
            for (var i = 0; i < _views.Count; i++) { SetRow(_views[i], i / columns); SetColumn(_views[i], i % columns); }
            styleChanged = true;
        }
        if (_version == _vm.CardBoard.Version && !styleChanged) return;
        _version = _vm.CardBoard.Version;
        for (var i = 0; i < _views.Count; i++) _views[i].Bind(_vm.CardBoard.Slots[i], styleChanged);
        _feedback.Text = $"弹幕卡片 · {_vm.CardBoard.Capacity} 格 · 隐藏 {_vm.CardBoard.HiddenCount}";
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _slots.SizeChanged -= OnSizeChanged; foreach (var view in _views) view.Dispose(); _views.Clear(); }
    private sealed class SlotView : Grid, IDisposable
    {
        private readonly LiveAssistantViewModel _vm;
        private readonly MessageAnimationClock _clock;
        private readonly Func<bool> _visible;
        private readonly Action<string> _feedback;
        private readonly ScrollViewer _body = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly Button _repeat = new() { Padding = new(5, 1, 5, 1), MinHeight = 22, FontSize = 12 };
        private readonly TextBlock _last = new() { FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid _root = new() { RowSpacing = 4 };
        private DanmakuCardEntry? _entry;
        private MessageCard? _card;
        public SlotView(LiveAssistantViewModel vm, MessageAnimationClock clock, Func<bool> visible, Action<string> feedback)
        {
            _vm = vm; _clock = clock; _visible = visible; _feedback = feedback;
            Background = MessageCard.Brush("#252B36"); CornerRadius = new(7); Padding = new(6); BorderBrush = MessageCard.Brush("#3A4352"); BorderThickness = new(1);
            _root.RowDefinitions.Add(new() { Height = GridLength.Auto }); _root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            var header = new Grid { ColumnSpacing = 4 }; header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(_repeat); SetColumn(_last, 1); header.Children.Add(_last);
            var hide = DashboardUi.Button("\uE890", "隐藏这条内容（仅卡片模式）", 22);
            hide.Click += (_, _) => { if (_entry is { } entry && !vm.CardBoard.Hide(entry)) _feedback("隐藏清单已满，请先清除隐藏清单"); };
            SetColumn(hide, 2); header.Children.Add(hide); _root.Children.Add(header); SetRow(_body, 1); _root.Children.Add(_body); Children.Add(_root);
            _repeat.Click += ShowSenders;
            PointerEntered += (_, _) => { if (_entry is not null) _entry.Protected = true; };
            PointerExited += (_, _) => { if (_entry is not null) _entry.Protected = false; };
        }
        public void Bind(DanmakuCardEntry? entry, bool styleChanged)
        {
            if (!ReferenceEquals(_entry, entry) || styleChanged)
            {
                if (_entry is not null) _entry.Protected = false;
                _card?.Dispose(); _card = null; _body.Content = null; _entry = entry;
                if (entry is not null)
                { _card = new(entry.Record, _vm, _clock, _visible, false); _body.Content = _card; _card.Activate(XamlRoot?.RasterizationScale ?? 1); }
                _root.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
            }
            if (entry is null) return;
            _repeat.Content = $"×{entry.Count}"; _last.Text = entry.LastSeen.ToString("HH:mm:ss");
        }
        private void ShowSenders(object sender, RoutedEventArgs e)
        {
            if (_entry is not { } entry) return;
            var list = new StackPanel { Spacing = 6, MaxWidth = 300 };
            foreach (var person in entry.Senders.Values.OrderByDescending(x => x.Count))
                list.Children.Add(new TextBlock { Text = $"{person.Name} · ×{person.Count} · {person.LastSeen:HH:mm:ss}", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            if (entry.UnknownCount > 0) list.Children.Add(new TextBlock { Text = $"身份未知 · ×{entry.UnknownCount}", FontSize = 12 });
            if (entry.UnlistedCount > 0) list.Children.Add(new TextBlock { Text = $"未展开次数 · {entry.UnlistedCount}", FontSize = 12 });
            var flyout = new Flyout { Content = new ScrollViewer { Content = list, MaxHeight = 320 } };
            flyout.ShowAt(_repeat);
        }
        public void SetActive(bool active) { if (active) _card?.Activate(XamlRoot?.RasterizationScale ?? 1); else { if (_entry is not null) _entry.Protected = false; _card?.Deactivate(); } }
        public void Dispose() { if (_entry is not null) _entry.Protected = false; _card?.Dispose(); _body.Content = null; _entry = null; }
    }
}
