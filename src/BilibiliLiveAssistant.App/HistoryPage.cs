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
    private readonly ListView _anchors = new() { SelectionMode = ListViewSelectionMode.Single, DisplayMemberPath = nameof(HistoryAnchor.Label) };
    private readonly CalendarDatePicker _from = new() { Header = "开始日期", Date = DateTimeOffset.Now.AddDays(-6), DateFormat = "{year.full}-{month.integer(2)}-{day.integer(2)}" };
    private readonly CalendarDatePicker _to = new() { Header = "结束日期", Date = DateTimeOffset.Now, DateFormat = "{year.full}-{month.integer(2)}-{day.integer(2)}" };
    private readonly ComboBox _sessions = new() { Header = "监视记录", HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 190 };
    private readonly ComboBox _type = new() { Header = "消息类型", MinWidth = 110 };
    private readonly TextBox _uid = new() { Header = "UID", PlaceholderText = "全部", MaxLength = 20, MinWidth = 100 };
    private readonly TextBox _name = new() { Header = "昵称包含", PlaceholderText = "全部", MaxLength = 128, MinWidth = 110 };
    private readonly TextBox _keyword = new() { Header = "正文包含", PlaceholderText = "关键词", MaxLength = 256, MinWidth = 130 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = MessageCard.Brush("#B9C4D8") };
    private readonly TextBlock _page = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _storage = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly Button _newRecords = UiFactory.Button("有新记录 · 刷新"), _previous = UiFactory.Button("上一页"), _next = UiFactory.Button("更早 100 条");
    private readonly HistoryMessageList _messages;
    private readonly List<(long? Id, DateTimeOffset? At)> _cursors = new() { (null, null) };
    private CancellationTokenSource? _query;
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
        _messages = new(vm, () => _active && !_disposed && _visible());
        var root = new Grid { Padding = new Thickness(20, 12, 20, 16), RowSpacing = 12 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "历史记录", FontSize = 22 });
        _newRecords.Visibility = Visibility.Collapsed; _newRecords.Click += async (_, _) => await RefreshAsync(true);
        Grid.SetColumn(_newRecords, 1); heading.Children.Add(_newRecords); root.Children.Add(heading);
        var body = new Grid { ColumnSpacing = 16 }; body.ColumnDefinitions.Add(new() { Width = new(210), MinWidth = 130 }); body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var left = new Grid { RowSpacing = 8 }; left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        left.Children.Add(new TextBlock { Text = "主播 / 待识别房间", FontSize = 14 }); Grid.SetRow(_anchors, 1); left.Children.Add(_anchors);
        Grid.SetRow(_storage, 2); left.Children.Add(_storage); body.Children.Add(left);
        var right = new Grid { RowSpacing = 10 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) right.RowDefinitions.Add(new() { Height = height });
        var filters = new StackPanel { Spacing = 8 };
        filters.Children.Add(Strip(_from, _to, _sessions));
        var apply = UiFactory.Button("查询"); apply.VerticalAlignment = VerticalAlignment.Bottom; apply.Click += async (_, _) => await RefreshAsync(false);
        var cancel = UiFactory.Button("取消查询"); cancel.VerticalAlignment = VerticalAlignment.Bottom; cancel.Click += (_, _) => { _query?.Cancel(); _status.Text = "已取消查询"; };
        filters.Children.Add(Strip(_type, _uid, _name, _keyword, apply, cancel));
        _type.Items.Add(new ComboBoxItem { Content = "全部互动" });
        foreach (var type in new[] { LiveEventType.Danmaku, LiveEventType.Gift, LiveEventType.SuperChat, LiveEventType.Guard, LiveEventType.Enter })
            _type.Items.Add(new ComboBoxItem { Content = TypeName(type), Tag = type });
        _type.SelectedIndex = 0; right.Children.Add(filters);
        var stats = new StackPanel { Spacing = 5, Children = { _summary } };
        var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var details = UiFactory.Button("资料与榜单快照"); details.Click += async (_, _) => await ShowDetailsAsync(); more.Children.Add(details);
        var gaps = UiFactory.Button("归档缺口"); gaps.Click += async (_, _) => await ShowGapsAsync(); more.Children.Add(gaps);
        stats.Children.Add(more); Grid.SetRow(stats, 1); right.Children.Add(stats);
        Grid.SetRow(_status, 2); right.Children.Add(_status); Grid.SetRow(_messages, 3); right.Children.Add(_messages);
        var footer = new Grid { ColumnSpacing = 8 }; footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _previous, _page, _next } };
        _previous.Click += async (_, _) => { if (_cursors.Count > 1) { _cursors.RemoveAt(_cursors.Count - 1); _pageNumber--; await LoadPageAsync(); } };
        _next.Click += async (_, _) =>
        {
            if (_rows.Count < 100) return; var last = _rows[^1]; _cursors.Add((last.Id, last.ReceivedAt));
            if (_cursors.Count > 256) _cursors.RemoveAt(0); _pageNumber++; await LoadPageAsync();
        };
        footer.Children.Add(pager);
        var manage = UiFactory.Button("存档维护"); var menu = new MenuFlyout();
        var delete = new MenuFlyoutItem { Text = "清理所选主播与日期范围…" }; delete.Click += async (_, _) => await DeleteAsync();
        var compact = new MenuFlyoutItem { Text = "释放磁盘空间（停止监视后）…" }; compact.Click += async (_, _) => await CompactAsync();
        menu.Items.Add(delete); menu.Items.Add(compact); manage.Flyout = menu; Grid.SetColumn(manage, 1); footer.Children.Add(manage);
        Grid.SetRow(footer, 4); right.Children.Add(footer); Grid.SetColumn(right, 1); body.Children.Add(right); Grid.SetRow(body, 1); root.Children.Add(body); Content = root;
        _anchors.SelectionChanged += async (_, _) => { if (!_loading && !_managing) await RefreshAsync(false, true); };
        _from.DateChanged += (_, _) => ConditionsChanged(true); _to.DateChanged += (_, _) => ConditionsChanged(true);
        _sessions.SelectionChanged += (_, _) => ConditionsChanged(true); _type.SelectionChanged += (_, _) => ConditionsChanged();
        foreach (var input in new[] { _uid, _name, _keyword }) input.TextChanged += (_, _) => ConditionsChanged();
        _history.Committed += OnCommitted; _vm.PropertyChanged += OnVmChanged;
        Loaded += async (_, _) => { _active = _visible(); if (_active) await RefreshAsync(true); };
        Unloaded += OnUnloaded; UpdateStorage(); UpdatePager();
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
            list ? _name.Text.Trim() : "", list ? _keyword.Text.Trim() : "", list ? cursor.Id : null, list ? cursor.At : null);
    }
    private void ConditionsChanged(bool scope = false)
    {
        if (_loading || _disposed || _managing) return;
        _query?.Cancel(); ResetPages(); _rows = []; _messages.SetRows(_rows); _details = null; _gaps = [];
        if (scope) _summary.Text = "统计待查询";
        _status.Text = "条件已修改 · 点击查询"; UpdatePager();
    }
    private void ResetPages() { _cursors.Clear(); _cursors.Add((null, null)); _pageNumber = 1; }
    private CancellationTokenSource NewQuery()
    {
        _query?.Cancel(); _query?.Dispose(); return _query = new();
    }
    private bool Current(CancellationTokenSource run) => !_disposed && _active && ReferenceEquals(_query, run) && !run.IsCancellationRequested;
    private async Task RefreshAsync(bool anchors, bool resetSession = false)
    {
        if (_disposed || !_active || _managing) return;
        var run = NewQuery(); ResetPages(); _status.Text = "查询中…"; SetPagerEnabled(false);
        Interlocked.Exchange(ref _hasNewRecords, 0); _newRecords.Visibility = Visibility.Collapsed;
        try
        {
            if (anchors)
            {
                var selected = _anchors.SelectedItem as HistoryAnchor;
                var values = await _history.AnchorsAsync(run.Token); if (!Current(run)) return;
                _loading = true;
                _anchors.ItemsSource = values;
                _anchors.SelectedItem = selected is null ? values.FirstOrDefault() : values.FirstOrDefault(a => a.Uid == selected.Uid && (a.Uid is > 0 || a.RoomId == selected.RoomId)) ?? values.FirstOrDefault();
                _loading = false;
            }
            if (_anchors.SelectedItem is not HistoryAnchor)
            { _summary.Text = "尚无本地记录"; _status.Text = "开启监视后开始归档；测试与示例不会保存。"; _messages.SetRows([]); return; }
            var oldSession = resetSession ? null : (_sessions.SelectedItem as HistorySession)?.Id;
            var filter = Filter() with { SessionId = null };
            var sessions = await _history.SessionsAsync(filter, run.Token); if (!Current(run)) return;
            _loading = true; _sessions.ItemsSource = new object[] { "全部监视记录" }.Concat(sessions.Cast<object>()).ToArray();
            _sessions.SelectedItem = oldSession is null ? _sessions.Items[0] : sessions.FirstOrDefault(s => s.Id == oldSession) as object ?? _sessions.Items[0]; _loading = false;
            filter = Filter();
            var summary = await _history.SummaryAsync(filter, run.Token); if (!Current(run)) return;
            _details = await _history.DetailsAsync(filter, run.Token); if (!Current(run)) return;
            _gaps = await _history.GapsAsync(filter, run.Token); if (!Current(run)) return;
            _summary.Text = $"弹幕 {summary.Danmaku:N0} · 互动 {summary.Interactions:N0} · 礼物 {summary.Gifts:N0} · 上舰 {summary.Guards:N0} · SC {summary.SuperChats:N0}\n" +
                $"已知付费礼物 ¥{summary.PaidGiftMilliYuan / 1000m:N3} · 免费礼物 {summary.FreeGiftQuantity:N0} 个 · SC ¥{summary.SuperChatMilliYuan / 1000m:N3} · 未知价格 {summary.UnknownPrices:N0} 条" +
                (summary.Incomplete ? $"\n⚠ 不完整：已知遗漏 {summary.Missing:N0} 条" + (summary.UnassignedMissing > 0 ? $" · 同期另有 {summary.UnassignedMissing:N0} 条未归属缺口" : "") +
                    (summary.InterruptedSessions > 0 ? $" · {summary.InterruptedSessions:N0} 段中断，末尾遗漏可能无法确定" : "") + "；仅统计已存档部分" : "");
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
        var rows = await _history.QueryAsync(Filter(true), run.Token); if (!Current(run)) return;
        _rows = rows; _messages.SetRows(rows); _status.Text = rows.Count == 0 ? "此范围没有符合条件的消息" : "按接收时间倒序 · 列表筛选不改变上方统计";
    }
    private void UpdatePager() { _page.Text = $"第 {_pageNumber} 页 · {_rows.Count} 条"; _previous.IsEnabled = _cursors.Count > 1 && !_managing; _next.IsEnabled = _rows.Count == 100 && !_managing; }
    private void SetPagerEnabled(bool value) { _previous.IsEnabled = _next.IsEnabled = value; }
    private void OnCommitted()
    {
        if (_disposed || !_active || Interlocked.Exchange(ref _hasNewRecords, 1) != 0 || Interlocked.CompareExchange(ref _noticeScheduled, 1, 0) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() => { Interlocked.Exchange(ref _noticeScheduled, 0); if (!_disposed && _active) _newRecords.Visibility = Visibility.Visible; })) Interlocked.Exchange(ref _noticeScheduled, 0);
    }
    private void OnVmChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(LiveAssistantViewModel.HistoryWarning) or nameof(LiveAssistantViewModel.Settings)) UpdateStorage(); }
    private void UpdateStorage()
    {
        var status = _history.Status;
        _storage.Text = $"{status.Bytes / (1024d * 1024):N1} MiB\n{(_vm.Settings.History.Enabled ? "● 记录开启" : "○ 记录暂停")} · 长期保留\n" +
            (status.Faulted || status.Dropped > 0 || status.CapacityWarning ? _vm.HistoryWarning : "") + "\n目录、开关和容量提醒在可视化设置中调整";
        ToolTipService.SetToolTip(_storage, _history.DatabasePath);
    }
    private async Task DialogAsync(string title, string text)
    {
        if (_disposed || _dialogOpen || XamlRoot is null) return;
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
    private async Task DeleteAsync()
    {
        if (_managing || _disposed) return; _managing = true; _query?.Cancel(); _maintenance = new(); SetPagerEnabled(false);
        try
        {
            var filter = Filter(); var count = await _history.CountAsync(filter, _maintenance.Token);
            if (_disposed) return;
            var result = await new ContentDialog
            {
                Title = "清理本地存档", Content = $"主播：{filter.Anchor.Label}\n日期：{filter.FromDay} ～ {filter.ToDay}\n监视记录：{filter.SessionId ?? "全部"}\n存档条目：{count:N0} 条（互动、资料、快照、缺口）\n\n消息类型、昵称、关键词筛选不缩小清理范围。涉及正在记录的监视段时须先停止。",
                PrimaryButtonText = "确认清理", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
            }.ShowAsync();
            if (result != ContentDialogResult.Primary || _disposed) return;
            _status.Text = "后台分批清理中…"; await _vm.DeleteHistoryAsync(filter, _maintenance.Token); _status.Text = "清理完成，空闲页可复用";
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "清理已停止，已完成批次的统计已同步"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_disposed) _status.Text = "清理失败：" + ex.Message; }
        finally { _managing = false; _maintenance?.Dispose(); _maintenance = null; if (!_disposed) { UpdateStorage(); UpdatePager(); _newRecords.Visibility = Visibility.Visible; } }
    }
    private async Task CompactAsync()
    {
        if (_managing || _disposed) return;
        if (_vm.IsMonitoring) { _status.Text = "释放磁盘空间前请先停止监视"; return; }
        var answer = await new ContentDialog { Title = "释放磁盘空间", Content = "重新整理数据库，将删除后空闲页归还磁盘。此操作可能耗时且需要临时磁盘空间；原有历史会保留。", PrimaryButtonText = "开始整理", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot }.ShowAsync();
        if (answer != ContentDialogResult.Primary || _disposed) return;
        _managing = true; _query?.Cancel(); _maintenance = new();
        try { _status.Text = "正在后台释放空间…"; await _vm.CompactHistoryAsync(_maintenance.Token); if (!_disposed) _status.Text = "磁盘空间整理完成"; }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "整理已取消"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_disposed) _status.Text = "整理失败：" + ex.Message; }
        finally { _managing = false; _maintenance?.Dispose(); _maintenance = null; if (!_disposed) UpdateStorage(); }
    }
    internal void SetActive(bool active)
    {
        if (_disposed) return; _active = active;
        if (!active) _query?.Cancel(); _messages.SetActive(active);
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _disposed = true; _active = false; _query?.Cancel(); _query?.Dispose(); _maintenance?.Cancel();
        _history.Committed -= OnCommitted; _vm.PropertyChanged -= OnVmChanged; _messages.Dispose(); Unloaded -= OnUnloaded;
    }
}
