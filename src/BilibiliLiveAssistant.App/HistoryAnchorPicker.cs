using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class HistoryAnchorPicker : Button, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly HistoryAnchorList _choices;
    private readonly TextBox _search = new() { PlaceholderText = "搜索主播 / 曾用名 / 房间号", MaxLength = 128 };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private readonly Flyout _flyout;
    private readonly MessageAnimationClock _clock;
    private CancellationTokenSource? _query;
    private BiliImageView? _avatar;
    private HistoryAnchor? _selected;
    private bool _open, _disposed, _loading;
    internal event Action<HistoryAnchor>? Selected;
    public HistoryAnchorPicker(LiveAssistantViewModel vm, Func<bool> visible)
    {
        _vm = vm; _visible = visible; _clock = new(visible); _choices = new(vm);
        Width = 220; HorizontalContentAlignment = HorizontalAlignment.Stretch; Padding = new(8, 2, 8, 2); ClearSelection();
        ToolTipService.SetToolTip(this, "选择以前监视过的主播，再点击开启监视");
        var body = new Grid { Width = 320, Height = 390, RowSpacing = 8 };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var searchRow = new Grid { ColumnSpacing = 6 }; searchRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); searchRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        searchRow.Children.Add(_search); var find = DashboardUi.Button("\uE721", "搜索主播"); find.Click += async (_, _) => await LoadAsync(); Grid.SetColumn(find, 1); searchRow.Children.Add(find);
        body.Children.Add(searchRow); Grid.SetRow(_status, 1); body.Children.Add(_status); Grid.SetRow(_choices, 2); body.Children.Add(_choices);
        _flyout = new() { Content = body }; Flyout = _flyout;
        _flyout.Opening += async (_, _) => { _open = true; _choices.SetActive(true); await LoadAsync(); };
        _flyout.Closed += (_, _) => { _open = false; _query?.Cancel(); _choices.SetActive(false); };
        _search.TextChanged += (_, _) => { _query?.Cancel(); _choices.List.IsEnabled = false; _status.Text = "输入完成后按 Enter 或点击搜索"; };
        _search.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await LoadAsync(); } };
        _choices.List.SelectionChanged += (_, _) =>
        {
            if (_loading || !IsEnabled || _vm.IsMonitoring || _choices.List.SelectedItem is not HistoryAnchor anchor) return;
            SetSelection(anchor); Selected?.Invoke(anchor); _flyout.Hide();
        };
        Loaded += (_, _) => Resume();
    }
    private async Task LoadAsync()
    {
        if (_disposed || !_open) return; _query?.Cancel(); _query?.Dispose(); var run = _query = new(); _status.Text = "正在读取本地主播…"; _choices.List.IsEnabled = false;
        try
        {
            var anchors = await _vm.History.AnchorsAsync(run.Token, _search.Text.Trim());
            if (_disposed || !_open || run.IsCancellationRequested || !ReferenceEquals(_query, run)) return;
            _loading = true; _choices.SetItems(anchors); _choices.List.SelectedItem = null; _loading = false;
            _choices.List.IsEnabled = true;
            _status.Text = anchors.Count == 0 ? "尚无匹配主播 · 监视后会保留在这里" : $"{anchors.Count:N0} 位 · 选择后点击开启监视";
            if (_selected is not null && string.IsNullOrWhiteSpace(_search.Text) && !anchors.Any(a => Same(a, _selected))) ClearSelection();
        }
        catch (OperationCanceledException) { if (_open && ReferenceEquals(_query, run)) _status.Text = "输入完成后按 Enter 或点击搜索"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (_open && !_disposed && ReferenceEquals(_query, run)) _status.Text = "读取失败：" + ex.Message; }
        finally { _loading = false; }
    }
    private static bool Same(HistoryAnchor a, HistoryAnchor b) => a.Uid == b.Uid && (a.Uid is > 0 || a.RoomId == b.RoomId);
    private void SetSelection(HistoryAnchor anchor)
    {
        _avatar?.Dispose(); _selected = anchor;
        _avatar = new(_vm.Images, anchor.AvatarUrl, HistoryAnchorList.DisplayName(anchor), 30, 30, false, _clock, _visible, true);
        var content = new Grid { ColumnSpacing = 6 }; content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var row = HistoryAnchorList.Row(anchor, _avatar); row.Padding = new(0); content.Children.Add(row);
        var arrow = new TextBlock { Text = "▾", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 }; Grid.SetColumn(arrow, 1); content.Children.Add(arrow); Content = content; Resume();
    }
    private void ClearSelection() { _selected = null; _avatar?.Dispose(); _avatar = null; Content = new TextBlock { Text = "选择已监视主播  ▾", FontSize = 13 }; }
    internal void MatchRoom(int room) { if (_selected is not null && _selected.RoomId != room) ClearSelection(); }
    internal void Suspend() { _flyout.Hide(); _query?.Cancel(); _choices.SetActive(false); _avatar?.Deactivate(); _clock.Suspend(); }
    internal void Resume() { if (!_disposed && _visible()) _avatar?.Activate(XamlRoot?.RasterizationScale ?? 1); }
    public void Dispose() { if (_disposed) return; Suspend(); _disposed = true; _query?.Dispose(); _choices.Dispose(); _avatar?.Dispose(); _clock.Dispose(); }
}
