using System.Collections.ObjectModel;
using BilibiliLiveAssistant.Core;
using BilibiliLiveAssistant.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;

namespace BilibiliLiveAssistant.App;

internal sealed class DashboardRankPanel : Grid, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly MessageAnimationClock _animations;
    private readonly BiliDashboardRankService _service = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly ObservableCollection<LiveRankMember> _items = new();
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, Padding = new(0), Background = null };
    private readonly HashSet<RankRow> _rows = new();
    private readonly ListImageVisibility _visibility;
    private readonly ToggleButton _energy = Tab("高能榜"), _guard = Tab("大航海");
    private readonly ToggleButton _total = Tab("总榜"), _month = Tab("月榜"), _week = Tab("周榜");
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Button _refresh = DashboardUi.Button("\uE72C", "刷新榜单", 28);
    private readonly Button _more = new() { Content = "加载更多", Padding = new(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _empty = new() { Text = "开启监视后获取榜单", FontSize = 12, Margin = new(8, 12, 8, 0), Opacity = 0.5, IsHitTestVisible = false };
    private DashboardRankKind _kind;
    private CancellationTokenSource? _request;
    private DateTimeOffset _due;
    private int _page, _generation;
    private bool _active, _disposed, _busy, _hasMore;
    public DashboardRankPanel(LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible)
    {
        _vm = vm; _animations = animations; _visible = visible; _kind = vm.Settings.Dashboard.RankKind;
        _visibility = new(_list, () => Eligible, UpdateVisibleImages);
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer(); _timer.IsRepeating = false;
        _timer.Tick += async (_, _) => await FetchAsync(false);
        RowSpacing = 3;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _energy, _guard } };
        header.Children.Add(tabs); Grid.SetColumn(_refresh, 1); header.Children.Add(_refresh); Children.Add(header);
        _periods.Children.Add(_total); _periods.Children.Add(_month); _periods.Children.Add(_week); Grid.SetRow(_periods, 1); Children.Add(_periods);
        _energy.Click += async (_, _) => await SelectAsync(DashboardRankKind.Energy);
        _guard.Click += async (_, _) => await SelectAsync(DashboardRankKind.GuardTotal);
        _total.Click += async (_, _) => await SelectAsync(DashboardRankKind.GuardTotal);
        _month.Click += async (_, _) => await SelectAsync(DashboardRankKind.GuardMonth);
        _week.Click += async (_, _) => await SelectAsync(DashboardRankKind.GuardWeek);
        _refresh.Click += async (_, _) => await FetchAsync(false);
        _more.Click += async (_, _) => await FetchAsync(true);
        _list.ItemsSource = _items;
        _list.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ContentControl HorizontalContentAlignment='Stretch' IsTabStop='False'/></DataTemplate>");
        _list.ContainerContentChanging += RenderContainer;
        Grid.SetRow(_list, 2); Children.Add(_list); Grid.SetRow(_empty, 2); Children.Add(_empty);
        var footer = new StackPanel { Spacing = 2, Children = { _status, _more } }; Grid.SetRow(footer, 3); Children.Add(footer);
        UpdateTabs(); UpdateActions();
    }
    private static ToggleButton Tab(string title) => new() { Content = title, FontSize = 12, Padding = new(9, 4, 9, 4), MinWidth = 0, MinHeight = 28 };
    private bool Eligible => !_disposed && _active && _visible() && _vm.IsListening && _vm.IsConnected && _vm.RoomProfile is { Uid: > 0 };
    public void SetActive(bool value)
    {
        if (_disposed) return;
        var changed = _active != value; _active = value;
        if (!Eligible)
        {
            CancelRequest(); _timer.Stop(); ReleaseRows(); _list.ItemsSource = null; UpdateActions(); return;
        }
        if (_list.ItemsSource is null) _list.ItemsSource = _items;
        _visibility.RefreshNow();
        if (changed || !_timer.IsRunning) Schedule();
        UpdateActions();
    }
    public void RoomReset()
    {
        CancelRequest(); _timer.Stop(); ReleaseRows(); _items.Clear(); _page = 0; _due = default; _hasMore = false;
        _empty.Text = "等待房间信息…"; UpdateActions();
    }
    public void SettingsChanged()
    {
        if (_kind != _vm.Settings.Dashboard.RankKind)
        { _kind = _vm.Settings.Dashboard.RankKind; RoomReset(); UpdateTabs(); }
        ReleaseRows(); _list.ItemsSource = null; if (Eligible) _list.ItemsSource = _items;
        _timer.Stop(); SetActive(_active);
    }
    public void AuthenticationChanged()
    { CancelRequest(); _timer.Stop(); _due = default; if (Eligible) Schedule(); }
    private async Task SelectAsync(DashboardRankKind kind)
    {
        if (_disposed) return;
        if (_kind == kind) { UpdateTabs(); return; }
        _kind = kind; RoomReset(); UpdateTabs();
        try { await _vm.SaveAsync(_vm.Settings with { Dashboard = _vm.Settings.Dashboard with { RankKind = kind } }); }
        catch (Exception ex) { _vm.ReportSettingsError($"保存榜单类型失败：{ex.Message}"); }
        if (Eligible) await FetchAsync(false);
    }
    private void UpdateTabs()
    {
        _energy.IsChecked = _kind == DashboardRankKind.Energy; _guard.IsChecked = _kind != DashboardRankKind.Energy;
        _total.IsChecked = _kind == DashboardRankKind.GuardTotal; _month.IsChecked = _kind == DashboardRankKind.GuardMonth; _week.IsChecked = _kind == DashboardRankKind.GuardWeek;
        _periods.Visibility = _kind == DashboardRankKind.Energy ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Schedule()
    {
        if (!Eligible || _busy) return;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, (_due - DateTimeOffset.UtcNow).TotalMilliseconds)); _timer.Start();
    }
    private async Task FetchAsync(bool append)
    {
        _timer.Stop();
        if (!Eligible || _busy || append && (!_hasMore || _items.Count >= 200)) return;
        var profile = _vm.RoomProfile!; var kind = _kind; var generation = _generation;
        var nextPage = append ? _page + 1 : 1;
        var request = new CancellationTokenSource(); _request = request; _busy = true; _status.Text = "更新中…"; UpdateActions();
        try
        {
            var cookies = _vm.DashboardCookies;
            var result = await _service.FetchAsync(profile.RoomId, profile.Uid, kind, nextPage, cookies, request.Token);
            var firstPage = !append ? result : null;
            var fetched = result.Members.ToList();
            // Refresh only the pages the user has already loaded, in sequence.
            // Never prefetch all members or reset a long list to page one every minute.
            var retainedPages = append ? nextPage : Math.Clamp(_page, 1, 7);
            while (!append && nextPage < retainedPages && result.HasMore && fetched.Count < 200)
            {
                request.Token.ThrowIfCancellationRequested();
                result = await _service.FetchAsync(profile.RoomId, profile.Uid, kind, ++nextPage, cookies, request.Token);
                fetched.AddRange(result.Members);
            }
            if (request.IsCancellationRequested || generation != _generation || !Eligible) return;
            if (kind == DashboardRankKind.Energy && firstPage is not null) _vm.AcceptDashboardEnergyPage(profile, firstPage);
            var selected = new List<LiveRankMember>(200);
            if (append) selected.AddRange(_items);
            var added = 0;
            foreach (var row in fetched)
            {
                if (selected.Count >= 200) break;
                if (selected.Any(old => row.Uid > 0 ? old.Uid == row.Uid : old.Rank == row.Rank)) continue;
                var enriched = kind == DashboardRankKind.Energy ? _vm.GetRankMembers(new(LiveEventType.Rank, "系统", "",
                    "#FFFFFF", Metadata: new Dictionary<string, object?> { ["rank_members"] = new[] { row } }), 1)[0] : row;
                selected.Add(enriched); added++;
            }
            if (!_items.SequenceEqual(selected))
            {
                if (!append) { ReleaseRows(); _items.Clear(); }
                foreach (var member in selected.Skip(append ? _items.Count : 0)) _items.Add(member);
            }
            _page = nextPage; _hasMore = result.HasMore && _items.Count < 200 && added > 0;
            _empty.Text = "暂无榜单";
            _visibility.Request();
            ToolTipService.SetToolTip(_status, null);
            _status.Text = $"{_items.Count}{((firstPage ?? result).Total is { } total ? $" / {total}" : "")} 人 · {(firstPage ?? result).SampledAt.ToLocalTime():HH:mm:ss}{(_items.Count == 200 ? " · 已达上限" : "")}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed && generation == _generation) { _status.Text = "刷新失败，点击重试"; ToolTipService.SetToolTip(_status, ex.Message); if (_items.Count == 0) _empty.Text = "榜单暂不可用"; }
        }
        finally
        {
            request.Dispose();
            if (ReferenceEquals(_request, request)) { _request = null; _busy = false; }
            if (generation == _generation && !_disposed)
            { _due = DateTimeOffset.UtcNow.AddSeconds(_vm.Settings.Dashboard.RankRefreshSeconds); UpdateActions(); Schedule(); }
        }
    }
    private void CancelRequest()
    {
        if (_request is null) return;
        _generation++; _request.Cancel(); _request = null; _busy = false;
    }
    private void UpdateActions()
    {
        _refresh.IsEnabled = Eligible && !_busy; _more.IsEnabled = Eligible && !_busy;
        _more.Visibility = _hasMore && _items.Count < 200 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RenderContainer(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        e.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch; e.ItemContainer.Padding = new(0); e.ItemContainer.MinHeight = 0;
        if (e.ItemContainer.ContentTemplateRoot is not ContentControl host)
        { if (!e.InRecycleQueue && e.Phase == 0) e.RegisterUpdateCallback(RenderContainer); return; }
        if (host.Content is RankRow old) { old.Dispose(); _rows.Remove(old); } host.Content = null;
        if (!e.InRecycleQueue && e.Item is LiveRankMember member)
        { var row = new RankRow(member, _kind, _vm, _animations, () => _visibility.IsVisible(member)); _rows.Add(row); host.Content = row; }
        e.Handled = true;
        _visibility.Request();
    }
    private void UpdateVisibleImages()
    {
        foreach (var row in _rows) row.SetActive(_visibility.IsVisible(row.Member));
        _animations.Wake();
    }
    private void ReleaseRows() { foreach (var row in _rows) row.Dispose(); _rows.Clear(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _visibility.Dispose(); CancelRequest(); _timer.Stop(); _service.Dispose();
        _list.ContainerContentChanging -= RenderContainer; ReleaseRows(); _list.ItemsSource = null; _items.Clear();
    }
    private sealed class RankRow : Grid, IDisposable
    {
        private readonly MessageCard _card;
        internal LiveRankMember Member { get; }
        public RankRow(LiveRankMember member, DashboardRankKind kind, LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible)
        {
            Member = member;
            ColumnSpacing = 3; ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var style = vm.Settings.Dashboard with { FontSize = 14, UsernameFontSize = 14, CompactMessages = false,
                Padding = 4, MessageSpacing = 2, BackgroundOpacity = 0, AvatarSize = 32, LineSpacing = 0, CornerRadius = 0 };
            _card = new(new(OverlayBulletinRenderer.MemberEvent(member, "#FFFFFF")), vm, animations, visible, false, style);
            Children.Add(_card);
            var value = kind == DashboardRankKind.Energy ? member.Contribution is { } score ? $"{score:N0}" : "—"
                : member.AccompanyDays is { } days ? $"{days:N0}天" : "—";
            var numbers = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { new TextBlock { Text = $"#{member.Rank}", FontSize = 11, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Right },
                    new TextBlock { Text = value, FontSize = 13, FontWeight = new(600), HorizontalAlignment = HorizontalAlignment.Right } } };
            DashboardUi.Describe(numbers, kind == DashboardRankKind.Energy ? "贡献值（接口原值）" : "累计陪伴天数");
            Grid.SetColumn(numbers, 1); Children.Add(numbers); Loaded += (_, _) => _card.Activate();
        }
        public void Dispose() => _card.Dispose();
        internal void SetActive(bool active) { if (active) _card.Activate(XamlRoot?.RasterizationScale ?? 1); else _card.Deactivate(); }
    }
}
