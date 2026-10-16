using System.ComponentModel;
using BilibiliLiveAssistant.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class HistoryPage : Page
{
    private readonly LiveAssistantViewModel _vm;
    private readonly ILocalHistory _history;
    private readonly Func<bool> _visible;
    private readonly HistoryAnchorList _anchorList;
    private ListView _anchors => _anchorList.List;
    private readonly TextBlock _anchorTitle = new() { Text = "选择一位主播", FontSize = 21, FontWeight = new(600), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _anchorSubtitle = new() { FontSize = 12, Opacity = 0.6 };
    private readonly TextBlock[] _statValues = Enumerable.Range(0, 4).Select(_ => new TextBlock { Text = "—", FontSize = 23, FontWeight = new(600) }).ToArray();
    private readonly TextBox _anchorSearch = new() { PlaceholderText = "昵称 / 曾用名 / UID / 房间号", MaxLength = 128 };
    private readonly CalendarDatePicker _from = new() { Header = "开始日期", Date = DateTimeOffset.Now.AddDays(-6), DateFormat = "{year.full}-{month.integer(2)}-{day.integer(2)}" };
    private readonly CalendarDatePicker _to = new() { Header = "结束日期", Date = DateTimeOffset.Now, DateFormat = "{year.full}-{month.integer(2)}-{day.integer(2)}" };
    private readonly ComboBox _sessions = new() { Header = "监视记录", HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 190 };
    private readonly ComboBox _broadcasts = new() { Header = "直播场次", HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 250, MaxWidth = 400 };
    private readonly ComboBox _type = new() { Header = "消息类型", MinWidth = 110 };
    private readonly Button _tradeButton = UiFactory.Button("筛选与排序");
    private long _matchingCount, _scopeMessageCount;
    private readonly TextBox _uid = new() { Header = "UID", PlaceholderText = "全部", MaxLength = 20, MinWidth = 100 };
    private readonly TextBox _name = new() { Header = "昵称包含", PlaceholderText = "全部", MaxLength = 128, MinWidth = 110 };
    private readonly TextBox _keyword = new() { Header = "正文包含", PlaceholderText = "关键词", MaxLength = 256, MinWidth = 130 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = MessageCard.Brush("#B9C4D8") };
    private readonly TextBlock _page = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _storage = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly Button _newRecords = UiFactory.Button("有新记录 · 刷新"), _previous = UiFactory.Button("上一页"), _next = UiFactory.Button("更早 100 条");
    private readonly Button _moreAnchors = UiFactory.Button("更多主播");
    private readonly List<HistoryAnchor> _anchorEntries = new();
    private bool _anchorsLoaded, _pageLoaded;
    private string _loadedAnchorSearch = "";
    private int _anchorOffset;
    private readonly HistoryMessageList _messages;
    private readonly HistoryMessageList _changes;
    private readonly HistoryAnalysisView _analysis;
    private readonly RadioButtons _tabs = new() { MaxColumns = 3, SelectedIndex = 0, Items = { "消息记录", "趋势分析", "成员变化" } };
    private readonly Grid _tabContent = new();
    private int _selectedTab;
    private readonly List<(long? Id, DateTimeOffset? At, long? SortValue)> _cursors = new() { (null, null, null) };
    private CancellationTokenSource? _query;
    private CancellationTokenSource? _anchorQuery;
    private CancellationTokenSource? _maintenance;
    private IReadOnlyList<HistoryRow> _rows = [];
    private HistoryDetails? _details;
    private IReadOnlyList<HistoryGap> _gaps = [];
    private bool _loading, _active, _disposed, _managing;
    private int _noticeScheduled, _hasNewRecords, _pageNumber = 1;
    private bool _dialogOpen;
    public HistoryPage(LiveAssistantViewModel vm, Func<bool> visible)
    {
        _vm = vm; _history = vm.History; _visible = visible;
        _anchorList = new(vm); _messages = new(vm, () => _active && !_disposed && _visible());
        _changes = new(vm, () => _active && !_disposed && _visible() && _selectedTab == 2);
        _analysis = new(vm, () => _active && !_disposed && _visible() && _selectedTab == 1);
        _analysis.Selected += async point => await ShowSampleAsync(point);
        var root = new Grid { Padding = new Thickness(20, 16, 20, 16), RowSpacing = 16, Background = MessageCard.Brush("#171A21"), RequestedTheme = ElementTheme.Dark };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "历史记录", FontSize = 22, FontWeight = new(600) });
        _newRecords.Visibility = Visibility.Collapsed; _newRecords.Click += async (_, _) => await RefreshAsync(true);
        Grid.SetColumn(_newRecords, 1); heading.Children.Add(_newRecords); root.Children.Add(heading);
        var body = new Grid { ColumnSpacing = 20 }; body.ColumnDefinitions.Add(new() { Width = new(230), MinWidth = 160 }); body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var left = new Grid { RowSpacing = 12 }; left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var anchorHeader = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "已监视主播", FontSize = 15, FontWeight = new(600) } } };
        var searchRow = new Grid { ColumnSpacing = 4 }; searchRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); searchRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _anchorSearch.PlaceholderText = "搜索主播"; searchRow.Children.Add(_anchorSearch);
        var findAnchor = DashboardUi.Button("\uE721", "查询主播 / 曾用名 / UID / 房间号"); findAnchor.Click += async (_, _) => await RefreshAsync(true); Grid.SetColumn(findAnchor, 1); searchRow.Children.Add(findAnchor); anchorHeader.Children.Add(searchRow);
        ToolTipService.SetToolTip(_anchorSearch, "默认显示所有已监视主播，按最近监视排序；可按昵称、曾用名、UID 或房间号筛选。");
        _anchorSearch.TextChanged += async (_, _) => { if (!_loading) { _query?.Cancel(); _anchorQuery?.Cancel(); if (_anchorSearch.Text.Length == 0) await RefreshAsync(true); } };
        _anchorSearch.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await RefreshAsync(true); } };
        left.Children.Add(anchorHeader); Grid.SetRow(_anchorList, 1); left.Children.Add(_anchorList);
        _moreAnchors.Visibility = Visibility.Collapsed; _moreAnchors.Click += async (_, _) => await LoadMoreAnchorsAsync();
        var anchorFooter = new StackPanel { Spacing = 8, Children = { _moreAnchors, _storage } };
        Grid.SetRow(anchorFooter, 2); left.Children.Add(anchorFooter); body.Children.Add(Surface(left, 12));
        var right = new Grid { RowSpacing = 12 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) right.RowDefinitions.Add(new() { Height = height });
        var title = new Grid { ColumnSpacing = 12 }; title.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        title.Children.Add(new StackPanel { Spacing = 4, Children = { _anchorTitle, _anchorSubtitle } });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var details = DashboardUi.Button("\uE946", "资料与榜单快照"); details.Click += async (_, _) => await ShowDetailsAsync(); tools.Children.Add(details);
        var gaps = DashboardUi.Button("\uE7BA", "查看归档缺口"); gaps.Click += async (_, _) => await ShowGapsAsync(); tools.Children.Add(gaps);
        var manage = UiFactory.Button("主播操作 ▾"); ToolTipService.SetToolTip(manage, "删除主播、日期范围清理与磁盘整理"); var menu = new MenuFlyout();
        var deleteAnchor = new MenuFlyoutItem { Text = "删除此主播全部存档…", Icon = new SymbolIcon(Symbol.Delete) }; deleteAnchor.Click += async (_, _) => await DeleteAsync(true);
        var deleteRange = new MenuFlyoutItem { Text = "清理所选日期范围…" }; deleteRange.Click += async (_, _) => await DeleteAsync();
        var compact = new MenuFlyoutItem { Text = "释放磁盘空间（停止监视后）…" }; compact.Click += async (_, _) => await CompactAsync();
        menu.Items.Add(deleteAnchor); menu.Items.Add(deleteRange); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(compact); manage.Flyout = menu; tools.Children.Add(manage);
        Grid.SetColumn(tools, 1); title.Children.Add(tools); right.Children.Add(title);
        var apply = UiFactory.Button("查询记录"); apply.VerticalAlignment = VerticalAlignment.Bottom; apply.Click += async (_, _) => await RefreshAsync(false);
        _type.Header = "类型";
        _type.Items.Add(new ComboBoxItem { Content = "全部互动" });
        foreach (var type in new[] { LiveEventType.Danmaku, LiveEventType.Gift, LiveEventType.SuperChat, LiveEventType.Guard, LiveEventType.Enter }) _type.Items.Add(new ComboBoxItem { Content = type == LiveEventType.Gift ? "礼物 / 上舰" : TypeName(type), Tag = type });
        _type.SelectedIndex = 0;
        foreach (var item in _type.Items.OfType<ComboBoxItem>())
            if (item.Tag is LiveEventType type && type == vm.Settings.TradeViews.HistoryType) _type.SelectedItem = item;
        _tradeButton.Click += (_, _) => OpenTradeFilter(); UpdateTradeButton();
        var dates = new Grid { ColumnSpacing = 8 };
        dates.ColumnDefinitions.Add(new() { Width = new(1.4, GridUnitType.Star) });
        dates.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        dates.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        _broadcasts.MinWidth = 0; _broadcasts.MaxWidth = double.PositiveInfinity;
        var dateFields = new FrameworkElement[] { _broadcasts, _from, _to };
        for (var i = 0; i < dateFields.Length; i++)
        { dateFields[i].MinWidth = 0; dateFields[i].HorizontalAlignment = HorizontalAlignment.Stretch; Grid.SetColumn(dateFields[i], i); dates.Children.Add(dateFields[i]); }
        Grid.SetRow(dates, 1); right.Children.Add(dates);
        ToolTipService.SetToolTip(_broadcasts, "按真实房间与开播时间归档，同一场直播的多次监视统一汇总；旧版或开播资料缺失的内容保留在未识别场次。");
        var stats = new StackPanel { Spacing = 8 };
        var statGrid = new Grid { ColumnSpacing = 10 };
        var labels = new[] { "弹幕", "礼物 / 上舰", "SC", "进房 / 互动" };
        for (var i = 0; i < labels.Length; i++)
        {
            statGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var card = Surface(new StackPanel { Spacing = 5, Children = { new TextBlock { Text = labels[i], FontSize = 12, Opacity = 0.65 }, _statValues[i] } }, 12);
            Grid.SetColumn(card, i); statGrid.Children.Add(card);
        }
        stats.Children.Add(statGrid); _summary.Opacity = 0.8; stats.Children.Add(_summary); Grid.SetRow(stats, 2); right.Children.Add(stats);
        var advanced = UiFactory.Button("更多条件"); advanced.VerticalAlignment = VerticalAlignment.Bottom;
        var advancedFields = new StackPanel { Spacing = 8 };
        advancedFields.Children.Add(Strip(_sessions, _uid, _name));
        var cancel = UiFactory.Button("取消查询"); cancel.VerticalAlignment = VerticalAlignment.Bottom; cancel.Click += (_, _) => { _query?.Cancel(); _status.Text = "已取消查询"; };
        var reset = UiFactory.Button("重置筛选"); reset.VerticalAlignment = VerticalAlignment.Bottom; reset.Click += async (_, _) =>
        {
            _uid.Text = _name.Text = _keyword.Text = ""; _type.SelectedIndex = 0;
            _sessions.SelectedIndex = _sessions.Items.Count > 0 ? 0 : -1; _broadcasts.SelectedIndex = _broadcasts.Items.Count > 0 ? 0 : -1;
            try { await _vm.SaveAsync(_vm.Settings with { TradeViews = _vm.Settings.TradeViews with { History = new(), HistoryType = null } }); ConditionsChanged(); UpdateTradeButton(); }
            catch (Exception ex) { _status.Text = "重置筛选失败：" + ex.Message; }
        };
        _keyword.MinWidth = 230; advancedFields.Children.Add(Strip(_keyword, reset, cancel));
        advanced.Flyout = new Flyout { Content = new ScrollViewer { Content = advancedFields, MaxHeight = 360,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            RequestedTheme = ElementTheme.Dark } };
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        foreach (var _ in Enumerable.Range(0, 3)) actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _type.HorizontalAlignment = HorizontalAlignment.Stretch; _tradeButton.VerticalAlignment = VerticalAlignment.Bottom;
        var actionFields = new FrameworkElement[] { _type, _tradeButton, advanced, apply };
        for (var i = 0; i < actionFields.Length; i++) { Grid.SetColumn(actionFields[i], i); actions.Children.Add(actionFields[i]); }
        Grid.SetRow(actions, 3); right.Children.Add(actions);
        var tabHeader = new StackPanel { Spacing = 5, Children = { _tabs, _status } };
        Grid.SetRow(tabHeader, 4); right.Children.Add(tabHeader);
        _tabContent.Children.Add(_messages); _tabContent.Children.Add(_analysis); _tabContent.Children.Add(_changes);
        _analysis.Visibility = _changes.Visibility = Visibility.Collapsed;
        var messages = Surface(_tabContent, 4); Grid.SetRow(messages, 5); right.Children.Add(messages);
        _tabs.SelectionChanged += async (_, _) =>
        {
            _selectedTab = _tabs.SelectedIndex; _query?.Cancel(); ResetPages(); _rows = []; _messages.SetRows([]); _changes.SetRows([]); _analysis.Clear();
            _messages.Visibility = _selectedTab == 0 ? Visibility.Visible : Visibility.Collapsed;
            _analysis.Visibility = _selectedTab == 1 ? Visibility.Visible : Visibility.Collapsed;
            _changes.Visibility = _selectedTab == 2 ? Visibility.Visible : Visibility.Collapsed;
            SetTabActive(); _tradeButton.IsEnabled = _type.IsEnabled = _keyword.IsEnabled = _selectedTab == 0; _uid.IsEnabled = _name.IsEnabled = _selectedTab != 1;
            await RefreshAsync(false);
        };
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _previous, _page, _next } };
        _previous.Click += async (_, _) => { if (_cursors.Count > 1) { _cursors.RemoveAt(_cursors.Count - 1); _pageNumber--; await LoadPageAsync(); } };
        _next.Click += async (_, _) =>
        {
            if (_rows.Count < 100) return; var last = _rows[^1];
            var sortValue = _vm.Settings.TradeViews.History.Sort == MessageSort.QuantityDescending ? last.Fields.Quantity : last.Fields.AmountMilliYuan;
            _cursors.Add((last.Id, last.ReceivedAt, sortValue));
            if (_cursors.Count > 256) _cursors.RemoveAt(0); _pageNumber++; await LoadPageAsync();
        };
        Grid.SetRow(pager, 6); right.Children.Add(pager); Grid.SetColumn(right, 1); body.Children.Add(right); Grid.SetRow(body, 1); root.Children.Add(body); Content = root;
        SizeChanged += (_, args) => body.ColumnDefinitions[0].Width = new(args.NewSize.Width < 900 ? 180 : 230);
        _anchors.SelectionChanged += async (_, _) => { UpdateAnchorHeading(); if (!_loading && !_managing) await RefreshAsync(false, true, true); };
        _broadcasts.SelectionChanged += async (_, _) => { if (!_loading && !_managing) await RefreshAsync(false, true); };
        _from.DateChanged += (_, _) => ConditionsChanged(true); _to.DateChanged += (_, _) => ConditionsChanged(true);
        _sessions.SelectionChanged += (_, _) => ConditionsChanged(true); _type.SelectionChanged += async (_, _) =>
        {
            ConditionsChanged(); UpdateTradeButton();
            if (_loading || _disposed) return;
            try
            {
                var selectedType = (_type.SelectedItem as ComboBoxItem)?.Tag as LiveEventType?;
                var trade = _vm.Settings.TradeViews.History;
                if (selectedType is not (LiveEventType.Gift or LiveEventType.Guard) && trade.Sort == MessageSort.QuantityDescending) trade = trade with { Sort = MessageSort.Newest };
                await _vm.SaveAsync(_vm.Settings with { TradeViews = _vm.Settings.TradeViews with { HistoryType = selectedType, History = trade } }); UpdateTradeButton();
            }
            catch (Exception ex) { _status.Text = "保存筛选失败：" + ex.Message; }
        };
        foreach (var input in new[] { _uid, _name, _keyword }) input.TextChanged += (_, _) => ConditionsChanged();
        _history.Committed += OnCommitted; _vm.PropertyChanged += OnVmChanged;
        Loaded += (_, _) => { _pageLoaded = true; SetActive(_visible()); };
        Unloaded += OnUnloaded; UpdateStorage(); UpdatePager();
    }
    private static Border Surface(UIElement child, double padding) => new() { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(8), Background = MessageCard.Brush("#222732") };
    private void UpdateAnchorHeading()
    {
        var anchor = _anchors.SelectedItem as HistoryAnchor;
        _anchorTitle.Text = anchor is null ? "选择一位主播" : HistoryAnchorList.DisplayName(anchor);
        _anchorSubtitle.Text = anchor is null ? "左侧选择主播后查看存档" : $"房间 {anchor.RoomId}" + (anchor.Uid is > 0 ? $"  ·  UID {anchor.Uid}" : "  ·  身份待识别");
    }
    private void ShowSummary(HistorySummary? summary)
    {
        _scopeMessageCount = summary?.Events ?? 0;
        var counts = summary is null ? null : new[] { summary.Danmaku, summary.Gifts + summary.Guards, summary.SuperChats, summary.Interactions };
        for (var i = 0; i < _statValues.Length; i++) _statValues[i].Text = counts is null ? "—" : counts[i].ToString("N0");
        if (summary is null) { _summary.Text = "统计待查询"; return; }
        _summary.Text = $"付费礼物 ¥{summary.PaidGiftMilliYuan / 1000m:N3}   ·   SC ¥{summary.SuperChatMilliYuan / 1000m:N3}   ·   免费礼物 {summary.FreeGiftQuantity:N0} 个";
        ToolTipService.SetToolTip(_summary, $"仅统计已存档数据，不估算实际收入。\n未知价格 {summary.UnknownPrices:N0} 条 · 上舰 {summary.Guards:N0} 条");
        if (summary.Incomplete) _summary.Text += $"\n⚠ 存档不完整 · 已知遗漏 {summary.Missing:N0} 条 · 未归属缺口 {summary.UnassignedMissing:N0} 条 · 中断 {summary.InterruptedSessions:N0} 段";
    }
    private static ScrollViewer Strip(params UIElement[] controls)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in controls) stack.Children.Add(control);
        return new() { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    internal static string TypeName(LiveEventType type) => type switch
    { LiveEventType.Danmaku => "弹幕", LiveEventType.Enter => "进房 / 互动", LiveEventType.Gift => "礼物", LiveEventType.SuperChat => "SC", LiveEventType.Guard => "上舰", LiveEventType.Online => "统计", _ => "榜单" };
    private HistoryFilter Filter(bool list = false)
    {
        if (_anchors.SelectedItem is not HistoryAnchor anchor) throw new InvalidOperationException("请先选择主播或房间");
        if (_from.Date is not { } from || _to.Date is not { } to || from.Date > to.Date) throw new InvalidOperationException("请选择有效的开始与结束日期");
        long? uid = null;
        if (list && _uid.Text.Trim().Length > 0)
        { if (!long.TryParse(_uid.Text.Trim(), out var number) || number < 0) throw new InvalidOperationException("UID 必须为非负整数，0 表示身份未知"); uid = number; }
        var cursor = _cursors[^1];
        return new(anchor, from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), (_sessions.SelectedItem as HistorySession)?.Id,
            list && (_type.SelectedItem as ComboBoxItem)?.Tag is LiveEventType type ? type : null, uid,
            list ? _name.Text.Trim() : "", list ? _keyword.Text.Trim() : "", list ? cursor.Id : null, list ? cursor.At : null,
            BroadcastId: (_broadcasts.SelectedItem as HistoryBroadcast)?.Id,
            Trade: list && _selectedTab == 0 ? _vm.Settings.TradeViews.History : null,
            BeforeSortValue: list ? cursor.SortValue : null, IncludeGuardGifts: true);
    }
    private void ConditionsChanged(bool scope = false)
    {
        if (_loading || _disposed || _managing) return;
        _query?.Cancel(); ResetPages(); _rows = []; _messages.SetRows(_rows); _details = null; _gaps = [];
        _changes.SetRows([]); _analysis.Clear();
        if (scope) ShowSummary(null);
        _status.Text = "条件已修改 · 点击查询"; UpdatePager();
    }
    private void ResetPages() { _cursors.Clear(); _cursors.Add((null, null, null)); _matchingCount = 0; _pageNumber = 1; }
    private CancellationTokenSource NewQuery()
    {
        _query?.Cancel(); _query?.Dispose(); return _query = new();
    }
    private bool Current(CancellationTokenSource run) => !_disposed && _active && ReferenceEquals(_query, run) && !run.IsCancellationRequested;
    private async Task RefreshAsync(bool anchors, bool resetSession = false, bool resetBroadcast = false)
    {
        if (_disposed || !_active || _managing) return;
        // Anchor discovery has its own cancellation scope: date/tab changes must not
        // erase the default anchor list while the page finishes its first layout.
        if (anchors) _query?.Cancel();
        if (anchors && !await LoadAnchorsAsync(false)) return;
        if (_disposed || !_active || _managing) return;
        var run = NewQuery(); ResetPages(); _status.Text = "查询中…"; SetPagerEnabled(false);
        _rows = []; _messages.SetRows([]); _details = null; _gaps = []; ShowSummary(null);
        _changes.SetRows([]); _analysis.Clear();
        Interlocked.Exchange(ref _hasNewRecords, 0); _newRecords.Visibility = Visibility.Collapsed;
        try
        {
            if (_anchors.SelectedItem is not HistoryAnchor)
            { ShowSummary(null); _summary.Text = "尚无本地记录"; _status.Text = "开启监视后开始归档；测试与示例不会保存。"; _rows = []; _messages.SetRows([]); _details = null; _gaps = []; _loading = true; _sessions.ItemsSource = _broadcasts.ItemsSource = null; _loading = false; UpdatePager(); return; }
            var oldSession = resetSession ? null : (_sessions.SelectedItem as HistorySession)?.Id;
            var oldBroadcast = resetBroadcast ? null : (_broadcasts.SelectedItem as HistoryBroadcast)?.Id;
            var broadcasts = await _history.BroadcastsAsync(Filter() with { SessionId = null, BroadcastId = null }, run.Token); if (!Current(run)) return;
            _loading = true; _broadcasts.ItemsSource = new object[] { "全部直播场次" }.Concat(broadcasts.Cast<object>()).ToArray();
            _broadcasts.SelectedItem = oldBroadcast is null ? _broadcasts.Items[0] : broadcasts.FirstOrDefault(b => b.Id == oldBroadcast) as object ?? _broadcasts.Items[0]; _loading = false;
            var filter = Filter() with { SessionId = null };
            var sessions = await _history.SessionsAsync(filter, run.Token); if (!Current(run)) return;
            _loading = true; _sessions.ItemsSource = new object[] { "全部监视记录" }.Concat(sessions.Cast<object>()).ToArray();
            _sessions.SelectedItem = oldSession is null ? _sessions.Items[0] : sessions.FirstOrDefault(s => s.Id == oldSession) as object ?? _sessions.Items[0]; _loading = false;
            filter = Filter();
            var summary = await _history.SummaryAsync(filter, run.Token); if (!Current(run)) return;
            _details = await _history.DetailsAsync(filter, run.Token); if (!Current(run)) return;
            _gaps = await _history.GapsAsync(filter, run.Token); if (!Current(run)) return;
            ShowSummary(summary);
            if (_broadcasts.SelectedItem is HistoryBroadcast selectedBroadcast)
                _summary.Text += $"\n{(selectedBroadcast.StartedAt is { } started ? $"开播 {started.ToLocalTime():yyyy-MM-dd HH:mm}" : "开播时间未取得")} · {selectedBroadcast.MonitorCount} 次监视共同归档 · 仅汇总已采集数据";
            await QueryRowsAsync(run); if (!Current(run)) return;
            UpdateStorage();
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_query, run) && _active) _status.Text = "查询已取消或超过 5 秒，请缩小日期范围后重试"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (Current(run)) _status.Text = "查询失败：" + ex.Message; }
        finally { _loading = false; if (Current(run)) UpdatePager(); }
    }
    private async Task LoadPageAsync()
    {
        if (_disposed || !_active || _managing) return; var run = NewQuery(); SetPagerEnabled(false);
        try { await QueryRowsAsync(run); }
        catch (OperationCanceledException) { if (ReferenceEquals(_query, run)) _status.Text = "查询已取消或超时"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (Current(run)) _status.Text = "查询失败：" + ex.Message; }
        finally { if (Current(run)) UpdatePager(); }
    }
    private async Task QueryRowsAsync(CancellationTokenSource run)
    {
        if (_selectedTab == 1)
        {
            var points = await _history.TrendsAsync(Filter(), run.Token); if (!Current(run)) return;
            _analysis.SetPoints(points); _status.Text = "人数增减为所选采样范围首末差值 · 缺口不补零 · 点击曲线或选择采样时间查看快照"; return;
        }
        if (_selectedTab == 2)
        {
            var cursor = _cursors[^1];
            var changes = await _history.ChangesAsync(Filter(true), cursor.Id is { } id && cursor.At is { } at ? new(at, id) : null, run.Token); if (!Current(run)) return;
            _rows = changes.Select(c => new HistoryRow(c.Id, c.At, null, "", c.SessionId,
                new LiveEvent(LiveEventType.Rank, c.After.Name, ChangeDescription(c), "#FFFFFF", c.After.Uid, c.At.ToLocalTime(), Appearance: c.After.Appearance), new())).ToArray();
            _changes.SetRows(_rows); _status.Text = "可核实的观察变化 · 首次观察不是加入事件 · 粉丝牌归属未核实 · 不从离榜推断退舰/退团"; return;
        }
        var listFilter = Filter(true);
        if (_pageNumber == 1) { _matchingCount = await _history.MatchingCountAsync(listFilter, run.Token); if (!Current(run)) return; }
        var rows = await _history.QueryAsync(listFilter, run.Token); if (!Current(run)) return;
        _rows = rows; _messages.SetRows(rows); _status.Text = $"匹配 {_matchingCount:N0} / {_scopeMessageCount:N0} 条 · {TradeFilterEditor.SortLabel(_vm.Settings.TradeViews.History.Sort)} · 筛选不改变统计";
    }
    private void UpdatePager() { _page.Text = _selectedTab == 1 ? "最多显示最近 1200 次采样" : $"第 {_pageNumber} 页 · {_rows.Count} 条"; _previous.Visibility = _next.Visibility = _selectedTab == 1 ? Visibility.Collapsed : Visibility.Visible; _previous.IsEnabled = _cursors.Count > 1 && !_managing; _next.IsEnabled = _rows.Count == 100 && !_managing; }
    private static string ChangeDescription(HistoryMemberChange c)
    {
        static string State(HistoryMemberState? s) => s is null ? "此前暂无观察" :
            $"{s.Name} · 名次 {s.Rank?.ToString() ?? "—"} · 贡献 {s.Contribution?.ToString() ?? "—"} · 陪伴 {s.AccompanyDays?.ToString() ?? "—"} 天 · " +
            (s.MedalKnown ? $"{s.Appearance.MedalName} {s.Appearance.MedalLevel} 级" : "粉丝牌未知") + " · " + (s.GuardKnown ? $"大航海等级 {s.Appearance.GuardLevel}" : "大航海身份未知");
        var source = c.Source switch { "energy" => "高能榜", "guards" => "大航海总榜", "guard_event" => "上舰事件", _ => "粉丝牌观察（归属未核实）" };
        return $"{source} · {c.Kind} · UID {c.After.Uid}\n之前：{State(c.Before)}\n本次：{State(c.After)}";
    }
    private async Task ShowSampleAsync(HistoryTrendPoint point)
    {
        if (_selectedTab != 1 || !_active || _disposed || _managing) return;
        var run = NewQuery();
        try { var sample = point.Sample.Status.StartsWith("旧版", StringComparison.Ordinal) ? point.Sample : await _history.SampleAsync(Filter(), point.Id, run.Token); if (Current(run) && sample is not null) _analysis.ShowSample(sample); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (Current(run)) _status.Text = "快照查询失败：" + ex.Message; }
    }
    private void SetTabActive() { _messages.SetActive(_active && _selectedTab == 0); _changes.SetActive(_active && _selectedTab == 2); _analysis.SetActive(_active && _selectedTab == 1); }
    private void SetPagerEnabled(bool value) { _previous.IsEnabled = _next.IsEnabled = value; }
    private void OnCommitted()
    {
        if (_disposed || !_active || Interlocked.Exchange(ref _hasNewRecords, 1) != 0 || Interlocked.CompareExchange(ref _noticeScheduled, 1, 0) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() => { Interlocked.Exchange(ref _noticeScheduled, 0); if (!_disposed && _active) _newRecords.Visibility = Visibility.Visible; })) Interlocked.Exchange(ref _noticeScheduled, 0);
    }
    private void OnVmChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(LiveAssistantViewModel.HistoryWarning) or nameof(LiveAssistantViewModel.Settings)) UpdateStorage(); }
    private void UpdateTradeButton()
    {
        _tradeButton.Content = _vm.Settings.TradeViews.History.IsDefault ? "筛选与排序" : "● 筛选与排序";
        _next.Content = "下一页 100 条";
    }
    private void OpenTradeFilter()
    {
        var type = (_type.SelectedItem as ComboBoxItem)?.Tag as LiveEventType?;
        var flyout = new Flyout();
        flyout.Content = new TradeFilterEditor(_vm.Settings.TradeViews.History, type is LiveEventType.Gift or LiveEventType.Guard, type == LiveEventType.SuperChat, async value =>
        {
            try
            {
                await _vm.SaveAsync(_vm.Settings with { TradeViews = _vm.Settings.TradeViews with { History = value } });
                ConditionsChanged(); UpdateTradeButton(); flyout.Hide(); await RefreshAsync(false);
            }
            catch (Exception ex) { _status.Text = "保存筛选失败：" + ex.Message; }
        });
        flyout.ShowAt(_tradeButton);
    }
    private void UpdateStorage()
    {
        var status = _history.Status;
        _storage.Text = $"{(_vm.Settings.History.Enabled ? "● 正在归档" : "○ 归档暂停")}  ·  {status.Bytes / (1024d * 1024):N1} MiB" +
            (status.Faulted || status.Dropped > 0 || status.CapacityWarning ? "\n" + _vm.HistoryWarning : "");
        ToolTipService.SetToolTip(_storage, _history.DatabasePath + "\n长期保留 · 目录、开关和容量提醒在可视化设置中调整");
    }
    private async Task DialogAsync(string title, string text)
    {
        if (_disposed || _managing || _dialogOpen || XamlRoot is null) return;
        _dialogOpen = true;
        try { await new ContentDialog { Title = title, Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 420 }, CloseButtonText = "关闭", XamlRoot = XamlRoot }.ShowAsync(); }
        finally { _dialogOpen = false; }
    }
    private Task ShowDetailsAsync()
    {
        if (_details is not { } details) return DialogAsync("资料与榜单快照", "请先查询所选主播与日期范围");
        var p = details.Profile;
        var text = p is null ? "主播资料：暂无数据" : $"{p.Name} · UID {p.Uid}\n房间 {p.RoomId}\n标题：{p.Title}\n粉丝团 {p.FansClub?.ToString() ?? "暂无数据"} · 大航海 {p.Guards?.ToString() ?? "暂无数据"} · 点赞 {p.Likes?.ToString() ?? "暂无数据"}";
        text += $"\n\n统计快照 {details.Snapshots:N0} 份 · 最新 {details.SnapshotAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "暂无数据"}\n人气 {details.Popularity?.ToString() ?? "暂无数据"} · 看过 {details.Watched?.ToString() ?? "暂无数据"}\n高能人数 {details.RankTotal?.ToString() ?? "暂无数据"}\n";
        if (details.Rank is { Count: > 0 }) text += string.Join("\n", details.Rank.Select(r => $"{r.Rank}. {r.Username} · UID {r.Uid} · 贡献 {r.Contribution?.ToString() ?? "暂无数据"}"));
        else text += "榜单：暂无数据";
        return DialogAsync("所选范围最后保存的资料与统计", text);
    }
    private Task ShowGapsAsync() => DialogAsync("归档缺口（最近 100 项）", _gaps.Count == 0 ? "所选范围未记录到缺口。仅保存软件实际收到的数据，未监视时无法补录。"
        : string.Join("\n\n", _gaps.Select(g => $"{g.FirstAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} ～ {g.LastAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{TypeName(g.Type)} · {g.Count:N0} 条 · {g.Reason}")));
    private async Task DeleteAsync(bool entireAnchor = false)
    {
        if (_managing || _disposed || _dialogOpen || XamlRoot is null) return;
        if (_anchors.SelectedItem is not HistoryAnchor anchor) { _status.Text = "请先选择要清理的主播"; return; }
        _managing = true; _query?.Cancel(); _maintenance = new(); SetPagerEnabled(false); var completed = false;
        try
        {
            var filter = entireAnchor ? new HistoryFilter(anchor, "0001-01-01", "9999-12-31") : Filter();
            var count = await _history.CountAsync(filter, _maintenance.Token);
            if (_disposed) return;
            _dialogOpen = true;
            ContentDialogResult result;
            try { result = await new ContentDialog
            {
                Title = entireAnchor ? "删除此主播全部存档？" : "清理所选日期范围？",
                Content = $"主播：{filter.Anchor.Label}\n" + (entireAnchor ? "范围：所有日期、所有监视记录" : $"日期：{filter.FromDay} ～ {filter.ToDay}\n场次：{_broadcasts.SelectedItem}\n监视记录：{filter.SessionId ?? "全部"}") +
                    $"\n存档条目：{count:N0} 条（互动、资料、快照、缺口）\n\n" +
                    (entireAnchor ? "将删除该主播的消息、资料、榜单快照、统计和监视记录，并从已监视主播列表移除。此操作无法撤销。" : "消息类型、昵称、关键词筛选不缩小清理范围。") +
                    (filter.BroadcastId is not null ? "\n跨场次且无法拆分计数的缺口会保留。" : "") + "\n涉及正在记录的监视段时须先停止监视。",
                PrimaryButtonText = entireAnchor ? "删除全部存档" : "确认清理", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
            }.ShowAsync(); }
            finally { _dialogOpen = false; }
            if (result != ContentDialogResult.Primary || _disposed) return;
            _status.Text = "后台分批清理中…"; await _vm.DeleteHistoryAsync(filter, _maintenance.Token); completed = true;
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "清理已停止，已完成批次的统计已同步"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_disposed) _status.Text = "清理失败：" + ex.Message; }
        finally { _managing = false; _maintenance?.Dispose(); _maintenance = null; if (!_disposed) { UpdateStorage(); UpdatePager(); _newRecords.Visibility = Visibility.Visible; } }
        if (completed && !_disposed)
        {
            // Delete the selected anchor even when an active search would otherwise keep it selected.
            _loading = true; if (entireAnchor) _anchors.SelectedItem = null; _loading = false;
            await RefreshAsync(true, entireAnchor, entireAnchor);
            if (!_disposed && _active) _status.Text = entireAnchor ? "已删除该主播全部存档" : "所选范围已清理 · 空闲页可复用";
        }
    }
    private async Task CompactAsync()
    {
        if (_managing || _disposed || _dialogOpen || XamlRoot is null) return;
        if (_vm.IsMonitoring) { _status.Text = "释放磁盘空间前请先停止监视"; return; }
        _dialogOpen = true;
        ContentDialogResult answer;
        try { answer = await new ContentDialog { Title = "释放磁盘空间", Content = "重新整理数据库，将删除后空闲页归还磁盘。此操作可能耗时且需要临时磁盘空间；原有历史会保留。", PrimaryButtonText = "开始整理", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot }.ShowAsync(); }
        finally { _dialogOpen = false; }
        if (answer != ContentDialogResult.Primary || _disposed) return;
        _managing = true; _query?.Cancel(); _maintenance = new();
        try { _status.Text = "正在后台释放空间…"; await _vm.CompactHistoryAsync(_maintenance.Token); if (!_disposed) _status.Text = "磁盘空间整理完成"; }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "整理已取消"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_disposed) _status.Text = "整理失败：" + ex.Message; }
        finally { _managing = false; _maintenance?.Dispose(); _maintenance = null; if (!_disposed) UpdateStorage(); }
    }
    private async Task LoadMoreAnchorsAsync() { await LoadAnchorsAsync(true); }
    private async Task<bool> LoadAnchorsAsync(bool append)
    {
        if (!_active || _disposed || _managing) return false;
        _anchorQuery?.Cancel(); _anchorQuery?.Dispose(); var run = _anchorQuery = new(); _moreAnchors.IsEnabled = false;
        bool CurrentAnchors() => !_disposed && _active && ReferenceEquals(_anchorQuery, run) && !run.IsCancellationRequested;
        try
        {
            var search = append ? _loadedAnchorSearch : _anchorSearch.Text.Trim();
            var values = await _history.AnchorsAsync(run.Token, search, 200, append ? _anchorOffset : 0); if (!CurrentAnchors()) return false;
            var selected = _anchors.SelectedItem as HistoryAnchor; _loading = true;
            if (!append) { _anchorEntries.Clear(); _anchorOffset = 0; }
            _anchorOffset += values.Count; _loadedAnchorSearch = search;
            foreach (var value in values) if (!_anchorEntries.Any(a => a.Uid == value.Uid && (a.Uid is > 0 || a.RoomId == value.RoomId))) _anchorEntries.Add(value);
            _anchorList.SetItems(_anchorEntries.ToArray());
            _anchors.SelectedItem = selected is null ? _anchorEntries.FirstOrDefault() : _anchorEntries.FirstOrDefault(a => a.Uid == selected.Uid && (a.Uid is > 0 || a.RoomId == selected.RoomId)) ?? _anchorEntries.FirstOrDefault();
            _loading = false; _anchorsLoaded = true; UpdateAnchorHeading();
            _moreAnchors.Visibility = values.Count == 200 ? Visibility.Visible : Visibility.Collapsed;
            return true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (CurrentAnchors()) _status.Text = "加载主播失败：" + ex.Message; }
        finally { _loading = false; if (!_disposed) _moreAnchors.IsEnabled = true; }
        return false;
    }
    internal async void SetActive(bool active)
    {
        if (_disposed) return; var becameActive = active && !_active; _active = active;
        if (!active) { _query?.Cancel(); _anchorQuery?.Cancel(); } SetTabActive(); _anchorList.SetActive(active);
        if (active && _pageLoaded && (becameActive || _query is null && _anchorQuery is null)) await RefreshAsync(!_anchorsLoaded);
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _disposed = true; _active = false; _query?.Cancel(); _query?.Dispose(); _anchorQuery?.Cancel(); _anchorQuery?.Dispose(); _maintenance?.Cancel();
        _history.Committed -= OnCommitted; _vm.PropertyChanged -= OnVmChanged; _messages.Dispose(); _anchorList.Dispose(); Unloaded -= OnUnloaded;
        _changes.Dispose(); _analysis.Dispose();
    }
}
