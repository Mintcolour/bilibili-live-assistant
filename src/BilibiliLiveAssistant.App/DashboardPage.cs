using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using BilibiliLiveAssistant.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BilibiliLiveAssistant.App;

internal sealed class DashboardPage : Page
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly DispatcherQueueTimer _refresh;
    private readonly DispatcherQueueTimer _stress;
    private readonly Stopwatch _stressClock = new();
    private readonly MenuFlyoutItem _stressItem = new() { Text = "手动高流量示例：100 条/秒，30 秒" };
    private int _stressSequence;
    private readonly MessageAnimationClock _animations;
    private readonly TextBlock _room = new() { FontSize = 14, FontWeight = new(600), VerticalAlignment = VerticalAlignment.Center };
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _connection = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _duration = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _audience = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _rank = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#D6B35A") };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, MaxWidth = 420 };
    private readonly StackPanel _roomInfo = new() { Orientation = Orientation.Horizontal, Spacing = 7 };
    private readonly StackPanel _durationInfo = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _audienceInfo = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Grid _ranking = new() { ColumnSpacing = 7, Margin = new Thickness(14, 0, 14, 0) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = new SolidColorBrush(Colors.IndianRed) };
    private readonly TextBlock _overlayOmissions = new() { FontSize = 12, Foreground = Brush("#D6B35A"), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _columns = new() { ColumnSpacing = 4 };
    private readonly Grid _left = new();
    private readonly HotPhrasePanel _hotPhrases;
    private readonly Grid _middle = new();
    private readonly DashboardRankPanel _rankPanel;
    private readonly AnchorSummary _anchor;
    private readonly HistoryAnchorPicker _historyAnchor;
    private readonly ToggleButton _rankExpanded = new() { Content = "榜单", Padding = new Thickness(8, 3, 8, 3), MinHeight = 28 };
    private readonly TextBlock _headingTitle = new() { Text = "直播概览", FontSize = 18, FontWeight = new(600), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _monitor = new() { Content = "开启监视", Padding = new Thickness(10, 5, 10, 5), MinWidth = 92 };
    private readonly ToggleButton _interact = DashboardUi.Toggle("\uE7C9", "调整浮窗位置、大小与显示类型 / 完成调整");
    private readonly ToggleSwitch _anonymous = new() { Header = "匿名读取", OnContent = "无需登录", OffContent = "使用账号", MinWidth = 160 };
    private bool _connectionBusy, _loadingLayout;
    private readonly DashboardList _danmaku;
    private readonly DanmakuCardPanel _cardPanel;
    private readonly Button _cardMode = new() { Content = "卡片", Padding = new(7, 3, 7, 3), MinHeight = 28 };
    private readonly DashboardList _gifts;
    private readonly DashboardList _sc;
    private readonly DashboardList _interactions;
    internal object GetResourceDiagnostics() => new { danmaku = _danmaku.GetResourceDiagnostics(), gifts = _gifts.GetResourceDiagnostics(),
        sc = _sc.GetResourceDiagnostics(), interactions = _interactions.GetResourceDiagnostics() };
    private bool _dirty = true;
    private bool _unloaded;
    private bool _settingsDirty;
    private DashboardSettings _style;
    private string _lastSummary = string.Empty;
    private string _lastOnline = string.Empty;
    private string _lastRank = string.Empty;
    private string _lastRoomInfo = string.Empty;

    public DashboardPage(LiveAssistantViewModel vm, bool overlayVisible, Func<bool, bool> setOverlayVisible, Func<bool> isWindowVisible)
    {
        _vm = vm; _visible = isWindowVisible; _style = vm.Settings.Dashboard;
        _refresh = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(_style.RefreshIntervalMs);
        _refresh.Tick += (_, _) => RefreshIfNeeded();
        _stress = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _stress.Interval = TimeSpan.FromMilliseconds(100);
        _stress.Tick += (_, _) =>
        {
            if (_stressClock.Elapsed.TotalSeconds >= 30 || !_visible()) { StopStress(); return; }
            for (var i = 0; i < 10; i++) vm.SendTestDanmaku(++_stressSequence);
        };
        _animations = new MessageAnimationClock(isWindowVisible);
        _hotPhrases = new(vm, _visible, clock: _animations) { Margin = new(5, 3, 5, 3) };
        _rankPanel = new(vm, _animations, _visible);
        _anchor = new(vm, _animations, _visible);
        _historyAnchor = new(vm, _visible);
        _danmaku = new("弹幕", "\uE8BD", vm, _animations, () => _visible() && !_vm.Settings.Cards.Enabled, false, _cardMode);
        _cardPanel = new(vm, _animations, _visible);
        _cardMode.Click += async (_, _) => await ToggleCardModeAsync();
        _cardPanel.ToggleRequested += async () => await ToggleCardModeAsync();
        var shortcut = new KeyboardAccelerator { Key = Windows.System.VirtualKey.C, Modifiers = Windows.System.VirtualKeyModifiers.Menu };
        shortcut.Invoked += async (_, args) =>
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or NumberBox) return;
            args.Handled = true; await ToggleCardModeAsync();
        };
        KeyboardAccelerators.Add(shortcut);
        _gifts = new("礼物 / 上舰", "\uF133", vm, _animations, _visible, true);
        _sc = new("SC", "\uE8F2", vm, _animations, _visible, true);
        _interactions = new("互动", "\uE716", vm, _animations, _visible, false);
        _gifts.MarkAllRequested += () => vm.Dashboard.MarkAllRead(true);
        _sc.MarkAllRequested += () => vm.Dashboard.MarkAllRead(false);
        _gifts.FilterChanged += Dirty; _sc.FilterChanged += Dirty;

        var root = new Grid { Padding = new Thickness(10, 6, 10, 6), RowSpacing = 6, Background = Brush("#171A21"), RequestedTheme = ElementTheme.Dark };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 180 });
        var heading = new StackPanel { Spacing = 4 };
        var top = new Grid { MinHeight = 40 };
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _anonymous.IsOn = vm.Settings.AnonymousReading;
        var reading = DashboardUi.Button("\uE713", "读取模式", 28);
        reading.Flyout = new Flyout { Content = new StackPanel { Spacing = 8, MaxWidth = 270, Children = { _anonymous,
            new TextBlock { Text = "匿名读取不发送已保存的登录 Cookie。未填写账号时也以匿名方式读取。", FontSize = 12, TextWrapping = TextWrapping.Wrap } } } };
        _roomInfo.Children.Add(_headingTitle); _roomInfo.Children.Add(_connection); _roomInfo.Children.Add(_historyAnchor); _roomInfo.Children.Add(_monitor); _roomInfo.Children.Add(reading);
        _monitor.Click += async (_, _) => await ToggleMonitorAsync();
        _durationInfo.Children.Add(DashboardUi.Icon("\uE823")); _durationInfo.Children.Add(_duration);
        _audienceInfo.Children.Add(DashboardUi.Icon("\uE890")); _audienceInfo.Children.Add(_audience);
        top.Children.Add(_roomInfo);
        _ranking.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        _ranking.Children.Add(_anchor);
        Grid.SetColumn(_ranking, 1); top.Children.Add(_ranking);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(_durationInfo); toolbar.Children.Add(_audienceInfo);
        var overlay = DashboardUi.Toggle("\uE740", "桌面悬浮区");
        overlay.IsChecked = overlayVisible;
        overlay.Checked += (_, _) => { if (!setOverlayVisible(true)) overlay.IsChecked = false; };
        overlay.Unchecked += (_, _) => setOverlayVisible(false);
        _interact.IsChecked = vm.OverlayInteractive;
        _interact.Click += (_, _) =>
        {
            if (overlay.IsChecked != true) overlay.IsChecked = true;
            if (overlay.IsChecked == true) vm.ToggleOverlayInteraction();
            _interact.IsChecked = vm.OverlayInteractive;
        };
        toolbar.Children.Add(_interact);
        var examples = DashboardUi.Button("\uE899", "显示示例消息");
        examples.Click += (_, _) => { if (overlay.IsChecked != true) overlay.IsChecked = true; if (overlay.IsChecked == true) vm.SendDisplayExamples(); };
        var clear = new MenuFlyoutItem { Text = "清空本次记录", Icon = new SymbolIcon(Symbol.Delete) };
        clear.Click += async (_, _) =>
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "清空概览记录？", Content = "将清空本次运行的所有记录，包括未读礼物和 SC。", PrimaryButtonText = "清空", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) vm.Dashboard.Clear();
        };
        toolbar.Children.Add(overlay); toolbar.Children.Add(examples);
        var info = DashboardUi.Button("\uE946", "房间详情与本次统计");
        var detailsFlyout = new Flyout { Content = _details };
        detailsFlyout.Opening += (_, _) => RefreshDetails();
        info.Flyout = detailsFlyout;
        toolbar.Children.Add(info);
        var more = DashboardUi.Button("\uE712", "更多操作");
        var flyout = new MenuFlyout();
        var compactMode = new ToggleMenuFlyoutItem { Text = "桌面紧凑面板", IsChecked = vm.Settings.Compact.DesktopEnabled };
        compactMode.Click += async (_, _) =>
        {
            try { await vm.UpdateCompactAsync(s => s with { DesktopEnabled = compactMode.IsChecked }); if (overlay.IsChecked != true) overlay.IsChecked = true; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { compactMode.IsChecked = vm.Settings.Compact.DesktopEnabled; vm.ReportSettingsError("浮窗模式保存失败，请重试。"); }
        };
        flyout.Opening += (_, _) => compactMode.IsChecked = vm.Settings.Compact.DesktopEnabled;
        flyout.Items.Add(compactMode);
        var single = new MenuFlyoutItem { Text = "发送单条测试弹幕" };
        single.Click += (_, _) => { if (overlay.IsChecked != true) overlay.IsChecked = true; vm.SendTestDanmaku(); };
        _stressItem.Click += (_, _) =>
        {
            if (_stress.IsRunning) { StopStress(); return; }
            if (overlay.IsChecked != true) overlay.IsChecked = true;
            _stressSequence = 0; _stressClock.Restart(); _stressItem.Text = "停止高流量示例"; _stress.Start();
        };
        var balance = new MenuFlyoutItem { Text = "均衡列宽" };
        balance.Click += async (_, _) =>
        {
            try { await vm.SaveAsync(vm.Settings with { Dashboard = vm.Settings.Dashboard with { DanmakuColumnWidth = 1.4, SuperChatColumnWidth = 1, GiftColumnWidth = 1.2 } }); }
            catch (Exception ex) { vm.ReportSettingsError($"保存列宽失败：{ex.Message}"); }
        };
        flyout.Items.Add(balance); flyout.Items.Add(clear); flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(single); flyout.Items.Add(_stressItem); more.Flyout = flyout; toolbar.Children.Add(more);
        Grid.SetColumn(toolbar, 2); top.Children.Add(toolbar);
        heading.Children.Add(top); heading.Children.Add(_error); heading.Children.Add(_overlayOmissions); root.Children.Add(heading);
        _columns.ColumnDefinitions.Add(new() { Width = new(_style.DanmakuColumnWidth, GridUnitType.Star), MinWidth = 150 });
        _columns.ColumnDefinitions.Add(new() { Width = new(6) });
        _columns.ColumnDefinitions.Add(new() { Width = new(_style.SuperChatColumnWidth, GridUnitType.Star), MinWidth = 150 });
        _columns.ColumnDefinitions.Add(new() { Width = new(6) });
        _columns.ColumnDefinitions.Add(new() { Width = new(_style.GiftColumnWidth, GridUnitType.Star), MinWidth = 150 });
        _left.RowDefinitions.Add(new() { Height = new(1 - _style.InteractionPanelRatio, GridUnitType.Star), MinHeight = 100 });
        _left.RowDefinitions.Add(new() { Height = new(8) });
        _left.RowDefinitions.Add(new() { Height = new(_style.InteractionPanelRatio, GridUnitType.Star), MinHeight = 60 });
        var chatArea = new Grid(); chatArea.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); chatArea.RowDefinitions.Add(new() { Height = GridLength.Auto });
        DashboardUi.Describe(_hotPhrases, "最近 30 秒高频弹幕 · 至少 3 次 · 前 5 条");
        Grid.SetRow(_hotPhrases, 1); chatArea.Children.Add(_hotPhrases);
        chatArea.Children.Add(_danmaku); chatArea.Children.Add(_cardPanel); _left.Children.Add(chatArea); Grid.SetRow(_interactions, 2); _left.Children.Add(_interactions);
        AddInteractionSplitter(); _columns.Children.Add(_left);
        _middle.RowDefinitions.Add(new() { Height = new(1 - _style.RankPanelRatio, GridUnitType.Star), MinHeight = 100 });
        _middle.RowDefinitions.Add(new() { Height = new(6) });
        _middle.RowDefinitions.Add(new() { Height = new(_style.RankPanelRatio, GridUnitType.Star) });
        _middle.Children.Add(_sc);
        var rankArea = new Grid { RowSpacing = 3 }; rankArea.RowDefinitions.Add(new() { Height = GridLength.Auto }); rankArea.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _rankExpanded.IsChecked = !_style.RankPanelCollapsed;
        DashboardUi.Describe(_rankExpanded, "展开 / 折叠榜单"); rankArea.Children.Add(_rankExpanded);
        Grid.SetRow(_rankPanel, 1); rankArea.Children.Add(_rankPanel); Grid.SetRow(rankArea, 2); _middle.Children.Add(rankArea);
        var rankSplitter = new Thumb { Background = Brush("#303744"), Height = 4, VerticalAlignment = VerticalAlignment.Center };
        rankSplitter.DragDelta += (_, e) =>
        {
            if (_rankExpanded.IsChecked != true) return;
            var height = Math.Max(1, _middle.RowDefinitions[0].ActualHeight + _middle.RowDefinitions[2].ActualHeight);
            var ratio = Math.Clamp(_middle.RowDefinitions[2].Height.Value - e.VerticalChange / height, 0.2, 0.8);
            _middle.RowDefinitions[0].Height = new(1 - ratio, GridUnitType.Star); _middle.RowDefinitions[2].Height = new(ratio, GridUnitType.Star);
        };
        rankSplitter.DragCompleted += async (_, _) =>
        {
            if (_rankExpanded.IsChecked != true) return;
            try { await vm.SaveAsync(vm.Settings with { Dashboard = vm.Settings.Dashboard with { RankPanelRatio = _middle.RowDefinitions[2].Height.Value } }); }
            catch (Exception ex) { vm.ReportSettingsError($"保存榜单高度失败：{ex.Message}"); }
        };
        DashboardUi.Describe(rankSplitter, "拖动调整 SC 与榜单高度"); Grid.SetRow(rankSplitter, 1); _middle.Children.Add(rankSplitter);
        _rankExpanded.Click += async (_, _) =>
        {
            if (_loadingLayout) return;
            ApplyRankLayout();
            try { await vm.SaveAsync(vm.Settings with { Dashboard = vm.Settings.Dashboard with { RankPanelCollapsed = _rankExpanded.IsChecked != true } }); }
            catch (Exception ex) { vm.ReportSettingsError($"保存榜单布局失败：{ex.Message}"); }
        };
        ApplyRankLayout();
        Grid.SetColumn(_middle, 2); _columns.Children.Add(_middle);
        Grid.SetColumn(_gifts, 4); _columns.Children.Add(_gifts);
        AddSplitter(1, 0, 2); AddSplitter(3, 2, 4);
        Grid.SetRow(_columns, 1); root.Children.Add(_columns);
        Content = root;
        SizeChanged += (_, e) =>
        {
            _ranking.Visibility = e.NewSize.Width < 1100 ? Visibility.Collapsed : Visibility.Visible;
            _durationInfo.Visibility = e.NewSize.Width < 1300 ? Visibility.Collapsed : Visibility.Visible;
            _audienceInfo.Visibility = e.NewSize.Width < 1200 ? Visibility.Collapsed : Visibility.Visible;
            _headingTitle.Visibility = e.NewSize.Width < 800 ? Visibility.Collapsed : Visibility.Visible;
            _historyAnchor.Width = e.NewSize.Width < 900 ? 220 : 270;
        };
        vm.PropertyChanged += OnPropertyChanged; vm.Dashboard.Changed += Dirty; vm.CardBoard.Changed += Dirty;
        vm.HotPhrasesChanged += Dirty;
        vm.AuthenticationChanged += _rankPanel.AuthenticationChanged;
        vm.RoomHistoryReset += ResetRank;
        Unloaded += OnUnloaded;
        RefreshIfNeeded();
    }

    private static SolidColorBrush Brush(string color) => MessageCard.Brush(color);
    private void ApplyRankLayout()
    {
        var expanded = _rankExpanded.IsChecked == true;
        _rankPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _middle.RowDefinitions[0].Height = new(expanded ? 1 - _style.RankPanelRatio : 1, GridUnitType.Star);
        _middle.RowDefinitions[1].Height = new(expanded ? 6 : 0);
        _middle.RowDefinitions[2].Height = expanded ? new(_style.RankPanelRatio, GridUnitType.Star) : GridLength.Auto;
        _rankPanel.SetActive(expanded && !_unloaded && _visible());
    }
    private void ResetRank()
    {
        // Cancel the old room's consumers synchronously, before any new room
        // events or image completions can be delivered on the dispatcher.
        _danmaku.Sync(_vm.Dashboard.Danmaku, false); _gifts.Sync(_vm.Dashboard.Gifts, false);
        _sc.Sync(_vm.Dashboard.SuperChats, false); _interactions.Sync(_vm.Dashboard.Interactions, false);
        _hotPhrases.RoomReset(); _cardPanel.Sync(); _rankPanel.RoomReset(); _anchor.Refresh(); Dirty();
    }
    private async Task ToggleMonitorAsync()
    {
        if (_connectionBusy) return;
        _connectionBusy = true; _monitor.IsEnabled = false; _anonymous.IsEnabled = false;
        _historyAnchor.IsEnabled = false;
        try
        {
            if (_vm.IsMonitoring) await _vm.StopAsync();
            else
            {
                var room = _historyAnchor.RoomId;
                if (room is null) { _vm.ReportSettingsError("请输入有效的房间号或 live.bilibili.com 链接"); return; }
                await _vm.SaveAsync(_vm.Settings with { RoomId = room.Value, AnonymousReading = _anonymous.IsOn });
                await _vm.StartAsync(room.Value);
            }
        }
        catch (Exception ex) { _vm.ReportSettingsError($"连接操作失败：{ex.Message}"); }
        finally { _connectionBusy = false; RefreshHeader(); Dirty(); }
    }
    private void AddSplitter(int column, int left, int right)
    {
        var thumb = new Thumb { Background = Brush("#303744"), MinWidth = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        DashboardUi.Describe(thumb, "拖动调整列宽");
        thumb.DragDelta += (_, e) =>
        {
            var a = _columns.ColumnDefinitions[left]; var b = _columns.ColumnDefinitions[right];
            var total = a.Width.Value + b.Width.Value;
            var shift = e.HorizontalChange / Math.Max(1, a.ActualWidth + b.ActualWidth) * total;
            var next = Math.Clamp(a.Width.Value + shift, 0.3, total - 0.3);
            a.Width = new(next, GridUnitType.Star); b.Width = new(total - next, GridUnitType.Star);
        };
        thumb.DragCompleted += async (_, _) =>
        {
            try
            {
                await _vm.SaveAsync(_vm.Settings with { Dashboard = _vm.Settings.Dashboard with
                { DanmakuColumnWidth = _columns.ColumnDefinitions[0].Width.Value, SuperChatColumnWidth = _columns.ColumnDefinitions[2].Width.Value, GiftColumnWidth = _columns.ColumnDefinitions[4].Width.Value } });
            }
            catch (Exception ex) { _vm.ReportSettingsError($"保存列宽失败：{ex.Message}"); }
        };
        Grid.SetColumn(thumb, column); _columns.Children.Add(thumb);
    }
    private void AddInteractionSplitter()
    {
        var thumb = new Thumb { Background = Brush("#303744"), Height = 4, VerticalAlignment = VerticalAlignment.Center };
        DashboardUi.Describe(thumb, "拖动调整弹幕与互动区高度");
        thumb.DragDelta += (_, e) =>
        {
            var height = Math.Max(1, _left.RowDefinitions[0].ActualHeight + _left.RowDefinitions[2].ActualHeight);
            var ratio = Math.Clamp(_left.RowDefinitions[2].Height.Value - e.VerticalChange / height, 0.12, 0.5);
            _left.RowDefinitions[0].Height = new(1 - ratio, GridUnitType.Star);
            _left.RowDefinitions[2].Height = new(ratio, GridUnitType.Star);
        };
        thumb.DragCompleted += async (_, _) =>
        {
            try { await _vm.SaveAsync(_vm.Settings with { Dashboard = _vm.Settings.Dashboard with { InteractionPanelRatio = _left.RowDefinitions[2].Height.Value } }); }
            catch (Exception ex) { _vm.ReportSettingsError($"保存互动区高度失败：{ex.Message}"); }
        };
        Grid.SetRow(thumb, 1); _left.Children.Add(thumb);
    }

    private void RefreshHeader()
    {
        _monitor.Content = _vm.IsMonitoring ? "停止监视" : "开启监视";
        _monitor.IsEnabled = !_connectionBusy; _anonymous.IsEnabled = _historyAnchor.IsEnabled = !_vm.IsMonitoring && !_connectionBusy;
        _anchor.Refresh(); _rankPanel.SetActive(_rankExpanded.IsChecked == true && !_unloaded && _visible());
        _room.Text = _vm.RoomId > 0 ? _vm.RoomId.ToString() : "—";
        var roomInfo = $"{_vm.Status} · 房间 {_room.Text}\n{_vm.ConnectionModeText}\n{_vm.RoomSummary}";
        if (_lastRoomInfo != roomInfo)
        {
            _lastRoomInfo = roomInfo;
            _connection.Fill = Brush(_vm.IsConnected ? "#76CFA6" : _vm.IsListening ? "#D6B35A" : "#758198");
            DashboardUi.Describe(_roomInfo, roomInfo);
        }
        if (_lastSummary != _vm.RoomSummary)
        {
            _lastSummary = _vm.RoomSummary;
            var parts = _lastSummary.Split(" · ", 3, StringSplitOptions.TrimEntries);
            _duration.Text = parts.Length > 1 && parts[1].StartsWith("已直播 ", StringComparison.Ordinal) ? parts[1][4..] : "—";
            _duration.Foreground = Brush(parts[0] is "直播中" or "轮播中" ? "#76CFA6" : "#9BA5BD");
            DashboardUi.Describe(_durationInfo, _lastSummary);
        }
        var audience = (_vm.LatestRank?.Extra.GetValueOrDefault("rank_total") as long?)?.ToString("N0") ?? "—";
        var audienceDetails = $"房间人数（高能榜人数）：{audience}\n看过：{_vm.LatestOnline?.Extra.GetValueOrDefault("watched") as string ?? "—"}";
        if (_lastOnline != audienceDetails)
        {
            _lastOnline = audienceDetails; _audience.Text = audience;
            DashboardUi.Describe(_audienceInfo, audienceDetails);
        }
    }
    private static string ShortRankName(string name)
    {
        if (name.Length <= 12) return name;
        var length = char.IsHighSurrogate(name[11]) ? 11 : 12;
        return name[..length] + "…";
    }
    private void RefreshDetails()
    {
        _details.Text = $"{_vm.Status} · 房间 {(_vm.RoomId > 0 ? _vm.RoomId.ToString() : "—")}\n{_vm.ConnectionModeText}\n{_vm.RoomSummary}\n{_vm.OnlineText}\n{_vm.RankText}\n\n本次接收 {_vm.ReceivedEventCount:N0}\n悬浮省略 {_vm.OverlayDroppedCount:N0} · 缓冲省略 {_vm.BufferedDroppedCount:N0}\n达到记录上限移除未读 {_vm.Dashboard.EvictedUnread:N0}";
    }
    private void Dirty()
    {
        _dirty = true;
        if (!_unloaded && _visible() && !_refresh.IsRunning) _refresh.Start();
    }
    private async Task ToggleCardModeAsync()
    {
        try { await _vm.SaveAsync(_vm.Settings with { Cards = _vm.Settings.Cards with { Enabled = !_vm.Settings.Cards.Enabled } }); }
        catch (Exception ex) { _vm.ReportSettingsError($"保存卡片模式失败：{ex.Message}"); }
    }
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveAssistantViewModel.OverlayInteractive)) _interact.IsChecked = _vm.OverlayInteractive;
        if (e.PropertyName == nameof(LiveAssistantViewModel.Settings)) { _settingsDirty = true; _refresh.Interval = TimeSpan.FromMilliseconds(_vm.Settings.Dashboard.RefreshIntervalMs); }
        Dirty();
    }
    public void RefreshIfNeeded()
    {
        _refresh.Stop();
        if (_unloaded || !_visible()) return;
        _animations.Wake();
        if (!_dirty) return;
        var history = _vm.Dashboard;
        _hotPhrases.Update(_vm.HotPhrases, _vm.Settings.HotPhrases); _hotPhrases.SetActive(true);
        RefreshHeader();
        _error.Text = string.Join("\n", new[] { _vm.ErrorText, _vm.HistoryWarning }.Where(s => s.Length > 0));
        _error.Visibility = _error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _overlayOmissions.Text = $"悬浮区已省略 {_vm.OverlayDroppedCount:N0} 条提示；概览与历史按原规则保留。";
        _overlayOmissions.Visibility = _vm.OverlayDroppedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_settingsDirty)
        {
            _style = _vm.Settings.Dashboard;
            _columns.ColumnDefinitions[0].Width = new(_style.DanmakuColumnWidth, GridUnitType.Star);
            _columns.ColumnDefinitions[2].Width = new(_style.SuperChatColumnWidth, GridUnitType.Star);
            _columns.ColumnDefinitions[4].Width = new(_style.GiftColumnWidth, GridUnitType.Star);
            _left.RowDefinitions[0].Height = new(1 - _style.InteractionPanelRatio, GridUnitType.Star);
            _left.RowDefinitions[2].Height = new(_style.InteractionPanelRatio, GridUnitType.Star);
            _loadingLayout = true; _rankExpanded.IsChecked = !_style.RankPanelCollapsed; _loadingLayout = false;
            ApplyRankLayout(); _rankPanel.SettingsChanged();
        }
        var cards = _vm.Settings.Cards.Enabled;
        _danmaku.Visibility = cards ? Visibility.Collapsed : Visibility.Visible;
        _cardPanel.Visibility = cards ? Visibility.Visible : Visibility.Collapsed;
        _cardPanel.SetActive(cards);
        if (cards) { _danmaku.Suspend(); _cardPanel.Sync(_settingsDirty); }
        else _danmaku.Sync(history.Danmaku, _settingsDirty);
        _gifts.Sync(history.Gifts, _settingsDirty);
        _sc.Sync(history.SuperChats, _settingsDirty); _interactions.Sync(history.Interactions, _settingsDirty);
        _dirty = false; _settingsDirty = false;
    }
    internal void SuspendVisuals() { _hotPhrases.SetActive(false); _refresh.Stop(); _animations.Suspend(); StopStress(); _rankPanel.SetActive(false); _anchor.Suspend(); _historyAnchor.Suspend(); _cardPanel.SetActive(false); _danmaku.Suspend(); _gifts.Suspend(); _sc.Suspend(); _interactions.Suspend(); }
    internal void ResumeVisuals() { if (!_unloaded) { _historyAnchor.Resume(); _dirty = true; RefreshIfNeeded(); if (!_vm.Settings.Cards.Enabled) _danmaku.Resume(); _gifts.Resume(); _sc.Resume(); _interactions.Resume(); } }
    private void StopStress() { _stress.Stop(); _stressClock.Stop(); _stressItem.Text = "手动高流量示例：100 条/秒，30 秒"; }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _unloaded = true; _refresh.Stop(); StopStress(); _animations.Dispose();
        _vm.PropertyChanged -= OnPropertyChanged; _vm.Dashboard.Changed -= Dirty; _vm.CardBoard.Changed -= Dirty;
        _vm.HotPhrasesChanged -= Dirty;
        _hotPhrases.Dispose();
        _vm.AuthenticationChanged -= _rankPanel.AuthenticationChanged;
        _vm.RoomHistoryReset -= ResetRank; _rankPanel.Dispose(); _anchor.Dispose(); _historyAnchor.Dispose();
        _danmaku.Dispose(); _sc.Dispose(); _gifts.Dispose(); _interactions.Dispose(); _cardPanel.Dispose();
        Unloaded -= OnUnloaded;
    }
}

internal sealed class DashboardList : Grid, IDisposable
{
    private readonly string _title;
    private readonly LiveAssistantViewModel _vm;
    private readonly MessageAnimationClock _animations;
    private readonly Func<bool> _visible;
    private readonly bool _important;
    private TradeFilter _trade = new();
    private readonly Button _tradeButton = DashboardUi.Button("\uE71C", "筛选与排序", 28);
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = null };
    private readonly RecordCollection _items = new();
    private readonly HashSet<MessageCard> _cards = new();
    private readonly Stack<MessageCard> _cardPool = new(16);
    private readonly Dictionary<Microsoft.UI.Xaml.Controls.Primitives.SelectorItem, MessageCard> _containerCards = new();
    private long _recycledWithoutRoot;
    private long _newContainers, _reusedContainers, _createdCards, _reboundCards, _pooledCards;
    private readonly Dictionary<Guid, DashboardRecord> _visibleRecords = new();
    private readonly Dictionary<Guid, int> _recordIndices = new();
    private readonly List<DashboardRecord> _filteredRecords = new();
    private readonly ListImageVisibility _visibility;
    private bool _active = true;
    private readonly TextBlock _heading = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _total = new() { FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _unreadTotal = new() { FontSize = 12, Foreground = MessageCard.Brush("#FFD878"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new() { Text = "暂无消息", FontSize = 12, Margin = new Thickness(10, 14, 10, 0), Opacity = 0.4, IsHitTestVisible = false };
    private readonly ToggleButton _unread = DashboardUi.Toggle("\uE71C", "仅显示未读", 28);
    private readonly Button _latest = DashboardUi.Button("\uE74A", "回到最新", 28);
    private readonly Button _readAll = DashboardUi.Button("\uE73E", "全部标为已读", 28);
    private long _version = -1;
    private long _contentVersion = -1, _syncCalls, _fullSyncs;
    private int _unreadCount = -1;
    private int _thankedCount = -1;
    private bool _filtered;
    private ScrollViewer? _scrollViewer;
    private (Guid Id, double Pixels)? _readingRestore;
    public event Action? MarkAllRequested;
    public event Action? FilterChanged;
    public DashboardList(string title, string glyph, LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible, bool important, UIElement? extraAction = null)
    {
        // The default panel precreates several screens of expensive rich cards.
        // Retain all message data, but materialize controls for the viewport only.
        _list.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel CacheLength='0'/></ItemsPanelTemplate>");
        _title = title; _vm = vm; _animations = animations; _visible = visible; _important = important;
        _visibility = new(_list, () => _active && _visible(), UpdateVisibleImages,
            item => item is DashboardRecord record && _recordIndices.TryGetValue(record.Id, out var index) ? index : -1);
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        RowSpacing = 3;
        var header = new Grid { ColumnSpacing = 4, MinHeight = 32 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var label = new Grid { ColumnSpacing = 6 };
        label.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        label.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        label.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        label.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        label.Children.Add(DashboardUi.Icon(glyph, 14));
        _heading.Text = title; Grid.SetColumn(_heading, 1); label.Children.Add(_heading);
        Grid.SetColumn(_total, 2); label.Children.Add(_total);
        if (important) { Grid.SetColumn(_unreadTotal, 3); label.Children.Add(_unreadTotal); }
        header.Children.Add(label);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        Grid.SetColumn(actions, 1); header.Children.Add(actions);
        _latest.Click += (_, _) => GoToLatest();
        if (important)
        {
            _readAll.Click += (_, _) => MarkAllRequested?.Invoke();
            _unread.Checked += (_, _) => { _version = -1; FilterChanged?.Invoke(); };
            _unread.Unchecked += (_, _) => { _version = -1; FilterChanged?.Invoke(); };
            _tradeButton.Click += (_, _) => OpenTradeFilter();
            actions.Children.Add(_tradeButton); actions.Children.Add(_readAll);
        }
        actions.Children.Add(_latest);
        if (extraAction is not null) actions.Children.Add(extraAction);
        UpdateActions(0, 0);
        Children.Add(header);
        _list.ItemsSource = _items;
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ContentPresenter HorizontalAlignment='Stretch'/></DataTemplate>");
        // These rows have no selection state; the message owns its visuals and
        // actions. Avoid another full list-item chrome/template around each row.
        _list.ItemContainerStyle = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListViewItem">
              <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
              <Setter Property="VerticalContentAlignment" Value="Top"/>
              <Setter Property="Padding" Value="0"/>
              <Setter Property="MinHeight" Value="0"/>
              <Setter Property="UseSystemFocusVisuals" Value="True"/>
              <Setter Property="Template"><Setter.Value>
                <ControlTemplate TargetType="ListViewItem">
                  <ContentPresenter Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}"
                    HorizontalAlignment="Stretch" VerticalAlignment="Top"/>
                </ControlTemplate>
              </Setter.Value></Setter>
            </Style>
            """);
        _list.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        _list.ContainerContentChanging += RenderContainer;
        _list.ChoosingItemContainer += ChoosingContainer;
        _list.Loaded += OnListLoaded;
        _list.PointerWheelChanged += CancelReadingRestore;
        _list.PointerPressed += CancelReadingRestore;
        _list.KeyDown += CancelReadingRestoreOnKey;
        _list.DoubleTapped += (_, e) =>
        {
            if (!_important) return;
            var node = e.OriginalSource as DependencyObject;
            while (node is not null && node is not ListViewItem) node = VisualTreeHelper.GetParent(node);
            var container = node as ListViewItem;
            if (container?.Content is RowSlot { Record: { } record }) vm.Dashboard.Mark(record, false);
        };
        Grid.SetRow(_list, 1); Children.Add(_list);
        Grid.SetRow(_empty, 1); Children.Add(_empty);
    }
    public void Sync(List<DashboardRecord> source, bool styleChanged)
    {
        _syncCalls++;
        var trade = _important ? (_title == "SC" ? _vm.Settings.TradeViews.SuperChats : _vm.Settings.TradeViews.Gifts) : TradeFilter.Default;
        if (_important && _unread.IsChecked != trade.OnlyUnread) _unread.IsChecked = trade.OnlyUnread;
        var tradeChanged = _trade != trade; _trade = trade;
        var state = _vm.Dashboard.GetColumnState(source);
        if (_version == state.Version && !styleChanged && !tradeChanged) return;
        var unread = state.Unread; var thanked = state.Thanked;
        var contentChanged = _contentVersion != state.ContentVersion || styleChanged || tradeChanged || _filtered != (_unread.IsChecked == true);
        _version = state.Version; _contentVersion = state.ContentVersion;
        _unreadCount = unread; _thankedCount = thanked; _filtered = _unread.IsChecked == true;
        _total.Text = source.Count.ToString();
        _unreadTotal.Text = unread > 0 ? $"● {unread}" : "";
        DashboardUi.Describe(_heading, _important ? $"{_title} · {source.Count} 条 · 未读 {unread}" : $"{_title} · {source.Count} 条");
        DashboardUi.Describe(_unreadTotal, $"未读 {unread} 条");
        if (!contentChanged && !_filtered)
        {
            _total.Text = !_trade.IsDefault ? $"{_items.Count} / {source.Count}" : source.Count.ToString();
            foreach (var container in _containerCards.Keys.ToArray())
            {
                var index = _list.IndexFromContainer(container);
                if (index >= 0 && index < _items.Count && container.ContentTemplateRoot is ContentPresenter host)
                    BindCard(container, host, _items[index].Record);
            }
            UpdateActions(source.Count, unread);
            return;
        }
        _fullSyncs++;
        _filteredRecords.Clear();
        IReadOnlyList<DashboardRecord> selected = source;
        if (_filtered || !_trade.IsDefault)
        {
            foreach (var record in source) if ((!_filtered || !record.IsRead) && _trade.Matches(record.Event)) _filteredRecords.Add(record);
            if (_trade.Sort != MessageSort.Newest) _filteredRecords.Sort(_trade.Compare);
            selected = _filteredRecords;
        }
        _total.Text = _filtered || !_trade.IsDefault ? $"{selected.Count} / {source.Count}" : source.Count.ToString();
        Guid readingAnchor = Guid.Empty;
        var oldIndex = -1; var readingPixels = 0d;
        if (_readingRestore is { } pending && _recordIndices.TryGetValue(pending.Id, out var pendingIndex))
        { readingAnchor = pending.Id; oldIndex = pendingIndex; readingPixels = pending.Pixels; }
        else if (_scrollViewer?.VerticalOffset >= 8 && _list.ItemsPanelRoot is ItemsStackPanel { FirstVisibleIndex: >= 0 } oldPanel
            && oldPanel.FirstVisibleIndex < _items.Count)
        {
            oldIndex = oldPanel.FirstVisibleIndex; readingAnchor = _items[oldIndex].Record.Id;
            var top = _containerCards.Keys.FirstOrDefault(container => _list.IndexFromContainer(container) == oldIndex);
            if (top is not null)
                readingPixels = Math.Clamp(_scrollViewer.VerticalOffset - top.ActualOffset.Y, 0, Math.Max(0, top.ActualHeight - 1));
        }
        _visibleRecords.Clear(); _recordIndices.Clear();
        for (var i = 0; i < selected.Count; i++)
        { _visibleRecords[selected[i].Id] = selected[i]; _recordIndices[selected[i].Id] = i; }
        // Keep realized owners until their stable row slots have been rebound.
        // A burst can replace every record in the bounded interaction history.
        // Releasing these owners here empties the presenters, while unchanged
        // RowSlot identities produce no ContainerContentChanging notification.
        if (selected.Count == 0 || styleChanged) ClearCardPool();
        _empty.Visibility = selected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = _filtered || !_trade.IsDefault ? "没有符合条件的消息" : "暂无消息";
        // Stable row identities keep the native item/template tree in place.
        // Only the managed record behind a slot changes as new messages arrive.
        _items.Synchronize(selected);
        if (readingAnchor != Guid.Empty && _recordIndices.TryGetValue(readingAnchor, out var anchorIndex) && anchorIndex != oldIndex)
        {
            _readingRestore = (readingAnchor, readingPixels);
            _list.ScrollIntoView(_items[anchorIndex], ScrollIntoViewAlignment.Leading);
        }
        // Refresh only realized rows. Passing every retained record through
        // ContainerFromItem made thousands of COM identity queries per second.
        foreach (var container in _containerCards.Keys.ToArray())
        {
            var index = _list.IndexFromContainer(container);
            if (index >= 0 && index < _items.Count && container.ContentTemplateRoot is ContentPresenter host)
                BindCard(container, host, _items[index].Record);
        }
        UpdateActions(source.Count, unread);
        _visibility.Request();
    }
    private void RenderContainer(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        // A recycled container may have already lost ContentTemplateRoot. Its
        // previous card must be released before attempting to inspect that root.
        if (e.InRecycleQueue)
        {
            if (_containerCards.Remove(e.ItemContainer, out var retired)) ReleaseCard(retired);
            if (e.ItemContainer.ContentTemplateRoot is ContentPresenter recycledHost) recycledHost.Content = null;
            else _recycledWithoutRoot++;
            e.Handled = true;
            _visibility.Request();
            return;
        }
        e.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        e.ItemContainer.VerticalContentAlignment = VerticalAlignment.Top;
        e.ItemContainer.Padding = new Thickness(0);
        e.ItemContainer.MinHeight = 0;
        if (e.ItemContainer.ContentTemplateRoot is not ContentPresenter host)
        {
            if (!e.InRecycleQueue && e.Phase == 0) e.RegisterUpdateCallback(RenderContainer);
            return;
        }
        if (!e.InRecycleQueue && e.Item is RowSlot slot)
            BindCard(e.ItemContainer, host, slot.Record);
        else
        {
            if (host.Content is MessageCard old) ReleaseCard(old);
            host.Content = null;
        }
        e.Handled = true;
        _visibility.Request();
    }
    private void ChoosingContainer(ListViewBase sender, ChoosingItemContainerEventArgs e)
    { if (e.ItemContainer is null) _newContainers++; else _reusedContainers++; }
    private void OnListLoaded(object sender, RoutedEventArgs e)
    {
        EnsureScrollViewer();
        UpdateLatestAction();
    }
    private void EnsureScrollViewer()
    {
        _list.ApplyTemplate();
        var viewer = FindScrollViewer(_list);
        if (_scrollViewer == viewer) return;
        if (_scrollViewer is not null) _scrollViewer.ViewChanged -= OnScrolled;
        _scrollViewer = viewer;
        if (_scrollViewer is not null) _scrollViewer.ViewChanged += OnScrolled;
    }
    private void GoToLatest()
    {
        _readingRestore = null;
        if (_items.Count == 0) return;
        EnsureScrollViewer();
        // ScrollIntoView also works if the template's ScrollViewer wasn't ready
        // during Loaded; a missing cached viewer must not silently eat the click.
        _list.ScrollIntoView(_items[0], ScrollIntoViewAlignment.Leading);
        _scrollViewer?.ChangeView(null, 0, null, true);
        UpdateLatestAction();
    }
    private void UpdateLatestAction()
    {
        var atLatest = _items.Count == 0 || _scrollViewer is not null && _scrollViewer.VerticalOffset < 0.5;
        _latest.IsEnabled = !atLatest;
        var position = _trade.Sort == MessageSort.Newest ? "最新位置" : "排序顶部";
        DashboardUi.Describe(_latest, _items.Count == 0 ? "暂无消息" : atLatest ? "已在" + position : "回到" + position);
    }
    private void UpdateActions(int total, int unread)
    {
        _readAll.IsEnabled = unread > 0;
        DashboardUi.Describe(_readAll, unread > 0 ? $"全部标为已读（{unread} 条）" : "没有未读消息");
        _unread.IsEnabled = total > 0 || _unread.IsChecked == true;
        _tradeButton.Foreground = MessageCard.Brush(_filtered || !_trade.IsDefault ? "#FFD878" : "#D6DCE7");
        DashboardUi.Describe(_tradeButton, (_filtered || !_trade.IsDefault ? "筛选已启用 · " : "") + "筛选与排序 · " + TradeFilterEditor.SortLabel(_trade.Sort));
        DashboardUi.Describe(_unread, _unread.IsChecked == true ? "正在仅显示未读，点击显示全部" : total > 0 ? "仅显示未读" : "暂无消息可筛选");
        UpdateLatestAction();
    }
    private void OpenTradeFilter()
    {
        var flyout = new Flyout();
        flyout.Content = new TradeFilterEditor(_trade, _title != "SC", _title == "SC", async value =>
        {
            try
            {
                var views = _vm.Settings.TradeViews;
                await _vm.SaveAsync(_vm.Settings with { TradeViews = _title == "SC" ? views with { SuperChats = value } : views with { Gifts = value } });
                _version = -1; FilterChanged?.Invoke(); flyout.Hide();
            }
            catch (Exception ex) { _vm.ReportSettingsError("保存筛选失败：" + ex.Message); }
        }, _unread.IsChecked == true, value => _unread.IsChecked = value,
            _vm.Dashboard.Gifts.Select(r => r.Event.HistoryFields?.GiftName ?? ""));
        flyout.ShowAt(_tradeButton);
    }
    private void OnScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateLatestAction();
        _animations.Wake();
    }
    private void CancelReadingRestore(object sender, PointerRoutedEventArgs e) => _readingRestore = null;
    private void CancelReadingRestoreOnKey(object sender, KeyRoutedEventArgs e) => _readingRestore = null;
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
    private void BindCard(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem container, ContentPresenter host, DashboardRecord record)
    {
        if (_containerCards.TryGetValue(container, out var previous) && !ReferenceEquals(previous, host.Content)) ReleaseCard(previous);
        if (host.Content is MessageCard old)
        {
            if (!old.IsDisposed && old.RecordId == record.Id && old.RecordRevision == record.Revision && old.MediaStyle == _vm.Settings.MediaPreview && old.MatchesStyle(_vm.Settings.Dashboard) && old.UpdateReadState(record))
            {
                old.Activate(_list.XamlRoot?.RasterizationScale ?? 1);
                return;
            }
            if (old.TryRebind(record, _vm, _animations, () => _visibility.IsVisible(record), _important))
            {
                _reboundCards++;
                old.Activate(_list.XamlRoot?.RasterizationScale ?? 1);
                return;
            }
            ReleaseCard(old);
        }
        MessageCard? card = null;
        while (MessageCard.IsReusable(record, _vm.Settings.Dashboard, _important) && _cardPool.TryPop(out var pooled))
        {
            if (pooled.TryRebind(record, _vm, _animations, () => _visibility.IsVisible(record), _important)) { card = pooled; _pooledCards++; break; }
            pooled.Dispose();
        }
        if (card is null) { card = new MessageCard(record, _vm, _animations, () => _visibility.IsVisible(record), _important); _createdCards++; }
        _cards.Add(card); _containerCards[container] = card; host.Content = card;
        card.Activate(_list.XamlRoot?.RasterizationScale ?? 1);
    }
    private void ReleaseCard(MessageCard card)
    {
        _cards.Remove(card);
        foreach (var owner in _containerCards.Where(x => ReferenceEquals(x.Value, card)).Select(x => x.Key).ToArray())
        {
            if (owner.ContentTemplateRoot is ContentPresenter host && ReferenceEquals(host.Content, card)) host.Content = null;
            _containerCards.Remove(owner);
        }
        if (_active && _cardPool.Count < 16 && card.RetireForReuse()) _cardPool.Push(card);
        else card.Dispose();
    }
    private void ClearCardPool() { while (_cardPool.TryPop(out var card)) card.Dispose(); }
    internal object GetResourceDiagnostics()
    {
        var panel = _list.ItemsPanelRoot as ItemsStackPanel;
        return new { items = _items.Count, cards = _cards.Count, cardsWithText = _cards.Count(c => c.HasBoundText), pooled = _cardPool.Count, owners = _containerCards.Count, recycledWithoutRoot = _recycledWithoutRoot, cacheLength = panel?.CacheLength,
            newContainers = _newContainers, reusedContainers = _reusedContainers, createdCards = _createdCards, reboundCards = _reboundCards, pooledCards = _pooledCards,
            syncCalls = _syncCalls, fullSyncs = _fullSyncs, columnVersion = _version, contentVersion = _contentVersion,
            ownedIndices = _containerCards.Keys.Select(x => _list.IndexFromContainer(x)).Order().ToArray(),
            firstVisible = panel?.FirstVisibleIndex, lastVisible = panel?.LastVisibleIndex,
            firstCached = panel?.FirstCacheIndex, lastCached = panel?.LastCacheIndex };
    }
    private void UpdateVisibleImages()
    {
        if (_readingRestore is { } restore && _scrollViewer is not null)
        {
            if (!_recordIndices.TryGetValue(restore.Id, out var index)) _readingRestore = null;
            else
            {
                // ScrollIntoView can realize the requested container before the
                // panel commits its new range. Its old layout offset must not
                // be used to undo that scroll in the same layout callback.
                var panel = _list.ItemsPanelRoot as ItemsStackPanel;
                var atBottom = _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 1;
                if (panel is not null && panel.FirstVisibleIndex != index && !atBottom)
                    _list.ScrollIntoView(_items[index], ScrollIntoViewAlignment.Leading);
                else if (_containerCards.Keys.FirstOrDefault(container => _list.IndexFromContainer(container) == index) is { } top)
                {
                    _readingRestore = null;
                    _scrollViewer.ChangeView(null, top.ActualOffset.Y + restore.Pixels, null, true);
                }
            }
        }
        // WinUI can retain an item container in its recycling pool without a
        // ContentChanging callback until that container is reused. Do not let
        // our ownership set keep its old rich visual tree alive in the meantime.
        foreach (var pair in _containerCards.ToArray())
        {
            if (_list.IndexFromContainer(pair.Key) >= 0) continue;
            if (pair.Key.ContentTemplateRoot is ContentPresenter host && ReferenceEquals(host.Content, pair.Value)) host.Content = null;
            ReleaseCard(pair.Value);
        }
        foreach (var card in _cards)
            if (_visibleRecords.TryGetValue(card.RecordId, out var record) && _visibility.IsVisible(record)) card.Activate(_list.XamlRoot?.RasterizationScale ?? 1);
            else card.Deactivate();
        _animations.Wake();
    }
    public void Suspend() { _active = false; _readingRestore = null; ClearCardPool(); foreach (var card in _cards) card.Deactivate(); }
    public void Resume() { _active = true; _visibility.Request(); }
    public void Dispose()
    {
        _visibility.Dispose(); _visibleRecords.Clear(); _recordIndices.Clear();
        _filteredRecords.Clear();
        _list.ContainerContentChanging -= RenderContainer;
        _list.ChoosingItemContainer -= ChoosingContainer;
        _list.Loaded -= OnListLoaded;
        _list.PointerWheelChanged -= CancelReadingRestore;
        _list.PointerPressed -= CancelReadingRestore;
        _list.KeyDown -= CancelReadingRestoreOnKey;
        if (_scrollViewer is not null) _scrollViewer.ViewChanged -= OnScrolled;
        // Include detached/recycled containers, not just those currently returned
        // by ContainerFromItem. Ownership is explicit until release.
        foreach (var card in _cards) card.Dispose();
        _cards.Clear(); _containerCards.Clear();
        ClearCardPool();
        _list.ItemsSource = null;
        _items.Clear();
    }
    private sealed class RowSlot(DashboardRecord record) { public DashboardRecord Record = record; }
    private sealed class RecordCollection : ObservableCollection<RowSlot>
    {
        public void Synchronize(IReadOnlyList<DashboardRecord> values)
        {
            for (var i = 0; i < Math.Min(Count, values.Count); i++) this[i].Record = values[i];
            while (Count > values.Count) RemoveAt(Count - 1);
            while (Count < values.Count) Add(new(values[Count]));
        }
    }
}
