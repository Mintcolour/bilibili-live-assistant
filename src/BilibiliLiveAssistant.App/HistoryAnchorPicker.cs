using BilibiliLiveAssistant.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace BilibiliLiveAssistant.App;

internal sealed class HistoryAnchorPicker : UserControl, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly AutoSuggestBox _input = new() { PlaceholderText = "主播 / 房间号 / 直播链接", FontSize = 13,
        QueryIcon = new FontIcon { Glyph = "\uE70D" }, MaxSuggestionListHeight = 340, MinWidth = 100 };
    private readonly Grid _avatarHost = new() { Width = 30, Height = 30, Margin = new Thickness(7, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherQueueTimer _debounce;
    private readonly MessageAnimationClock _clock;
    private HistoryAnchorList? _choices;
    private CancellationTokenSource? _query;
    private BiliImageView? _avatar;
    private HistoryAnchor? _selected;
    private readonly long _openCallback;
    private bool _active = true, _disposed, _updatingChoices;
    internal int? RoomId => _selected?.RoomId ?? ParseRoom(_input.Text);

    public HistoryAnchorPicker(LiveAssistantViewModel vm, Func<bool> visible)
    {
        _vm = vm; _visible = visible; _clock = new(() => _active && !_disposed && _visible());
        Width = 270; VerticalAlignment = VerticalAlignment.Center;
        var root = new Grid { ColumnSpacing = 4, Background = MessageCard.Brush("#292D35"), CornerRadius = new CornerRadius(6) };
        root.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        root.Children.Add(_avatarHost); Grid.SetColumn(_input, 1); root.Children.Add(_input); Content = root;
        _input.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ContentControl Content='{Binding Label}' HorizontalContentAlignment='Stretch' IsTabStop='False'/></DataTemplate>");
        _input.Text = vm.RoomId > 0 ? vm.RoomId.ToString() : "";
        _input.TextMemberPath = nameof(HistoryAnchor.Name);
        _input.Loaded += (_, _) => AttachSuggestions();
        _debounce = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(300); _debounce.IsRepeating = false;
        _debounce.Tick += async (_, _) => await LoadAsync();
        _input.TextChanged += (_, args) =>
        {
            if (_disposed || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || !args.CheckCurrent()) return;
            ClearSelection(); _query?.Cancel(); _debounce.Stop(); _input.ItemsSource = null;
            if (_active && IsEnabled) _debounce.Start();
        };
        _input.GotFocus += async (_, _) => { if (_active && !_disposed && IsEnabled) await LoadAsync(); };
        _input.SuggestionChosen += (_, args) => { if (args.SelectedItem is HistoryAnchor anchor && CanChoose()) Select(anchor); };
        _input.QuerySubmitted += async (_, args) =>
        {
            if (!CanChoose()) return;
            if (args.ChosenSuggestion is HistoryAnchor anchor) { Select(anchor); _input.IsSuggestionListOpen = false; }
            else await LoadAsync(true);
        };
        _openCallback = _input.RegisterPropertyChangedCallback(AutoSuggestBox.IsSuggestionListOpenProperty, (_, _) =>
        {
            AttachSuggestions();
            if (_input.IsSuggestionListOpen) DispatcherQueue.TryEnqueue(AttachSuggestions);
            _choices?.SetActive(_input.IsSuggestionListOpen && _active && _visible());
            if (!_input.IsSuggestionListOpen && !_updatingChoices) _query?.Cancel();
        });
        IsEnabledChanged += (_, _) => { _input.IsEnabled = IsEnabled; if (!IsEnabled) CloseSuggestions(); };
        Loaded += (_, _) => Resume();
        ToolTipService.SetToolTip(this, "输入房间号、直播链接或主播名称自动搜索；选择后点击开启监视");
    }
    private void AttachSuggestions()
    {
        if (_disposed) return;
        _input.ApplyTemplate();
        var list = FindSuggestions(_input);
        // Popup children may be detached from the input's visual tree until the native popup opens.
        if (list is null && _input.IsSuggestionListOpen && XamlRoot is not null)
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
                if (FindSuggestions(popup) is { } found) { list = found; break; }
        if (list is null || ReferenceEquals(_choices?.List, list)) return;
        _choices?.Dispose(); _choices = new(_vm, list);
        _choices.SetActive(_active && _visible() && _input.IsSuggestionListOpen);
    }
    private static ListView? FindSuggestions(DependencyObject root)
    {
        if (root is ListView { Name: "SuggestionsList" } list) return list;
        if (root is Microsoft.UI.Xaml.Controls.Primitives.Popup { Child: { } child }) return FindSuggestions(child);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (FindSuggestions(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private void SetCandidates(object? values)
    {
        _updatingChoices = true;
        try { _input.ItemsSource = values; }
        finally { _updatingChoices = false; }
    }
    private static int? ParseRoom(string text)
    {
        text = text.Trim();
        if (text.Length > 0 && text.All(char.IsAsciiDigit)) return int.TryParse(text, out var room) && room > 0 ? room : null;
        if (text.StartsWith("live.bilibili.com/", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.Equals(uri.Host, "live.bilibili.com", StringComparison.OrdinalIgnoreCase)) return null;
        var segment = uri.AbsolutePath.Trim('/');
        return segment.All(char.IsAsciiDigit) && int.TryParse(segment, out var number) && number > 0 ? number : null;
    }
    private bool CanChoose() => !_disposed && _active && IsEnabled && !_vm.IsMonitoring;
    private bool InputHasFocus()
    {
        if (XamlRoot is null) return false;
        var node = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (node is not null) { if (ReferenceEquals(node, _input)) return true; node = VisualTreeHelper.GetParent(node); }
        return false;
    }
    private async Task LoadAsync(bool showAll = false)
    {
        _debounce.Stop();
        if (!CanChoose() || !_visible() || !InputHasFocus()) return;
        _query?.Cancel(); _query?.Dispose(); var run = _query = new();
        var sourceText = _input.Text;
        var text = _selected is null ? _input.Text.Trim() : "";
        var room = ParseRoom(text);
        // Real room profiles are requested by the existing monitor connection, not on each keystroke.
        SetCandidates(room is { } requested ? new[] { NewRoom(requested) } : null);
        try
        {
            var anchors = await _vm.History.AnchorsAsync(run.Token, showAll ? "" : room?.ToString() ?? text, 50);
            if (_disposed || !_active || !IsEnabled || !_visible() || run.IsCancellationRequested || !ReferenceEquals(_query, run) || sourceText != _input.Text || !InputHasFocus()) return;
            var values = anchors.Take(50).ToList();
            if (room is { } id && !values.Any(a => a.RoomId == id)) values.Add(NewRoom(id));
            SetCandidates(values);
            _input.IsSuggestionListOpen = values.Count > 0;
            _choices?.SetActive(_input.IsSuggestionListOpen);
            ToolTipService.SetToolTip(this, values.Count == 0 ? "没有匹配主播，请输入有效房间号或直播链接" : "选择历史主播或连接新房间，再点击开启监视");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed && _active && ReferenceEquals(_query, run) && !run.IsCancellationRequested)
                ToolTipService.SetToolTip(this, "历史搜索失败：" + ex.Message + (room is not null ? "；仍可使用输入的房间号开启监视" : ""));
        }
    }
    private static HistoryAnchor NewRoom(int room) => new(null, room, $"连接新房间 {room}", "");
    private void Select(HistoryAnchor anchor)
    {
        _query?.Cancel(); _debounce.Stop(); ClearSelection(); _selected = anchor;
        _input.Text = HistoryAnchorList.DisplayName(anchor);
        _avatar = new(_vm.Images, anchor.AvatarUrl, HistoryAnchorList.DisplayName(anchor), 30, 30, false, _clock, () => _active && _visible(), true);
        _avatarHost.Children.Add(_avatar); _avatarHost.Visibility = Visibility.Visible; Resume();
        ToolTipService.SetToolTip(this, $"{HistoryAnchorList.DisplayName(anchor)} · 房间 {anchor.RoomId}");
    }
    private void ClearSelection() { _selected = null; _avatar?.Dispose(); _avatar = null; _avatarHost.Children.Clear(); _avatarHost.Visibility = Visibility.Collapsed; }
    private void CloseSuggestions() { _debounce.Stop(); _query?.Cancel(); _input.IsSuggestionListOpen = false; _choices?.SetActive(false); }
    internal void Suspend() { _active = false; CloseSuggestions(); _avatar?.Deactivate(); _clock.Suspend(); }
    internal void Resume() { if (_disposed) return; _active = true; if (_visible()) _avatar?.Activate(XamlRoot?.RasterizationScale ?? 1); }
    public void Dispose()
    {
        if (_disposed) return; Suspend(); _disposed = true; _input.UnregisterPropertyChangedCallback(AutoSuggestBox.IsSuggestionListOpenProperty, _openCallback);
        _query?.Dispose(); _choices?.Dispose(); _avatar?.Dispose(); _clock.Dispose();
    }
}
