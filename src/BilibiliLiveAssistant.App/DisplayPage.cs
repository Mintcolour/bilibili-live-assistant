using System.ComponentModel;
using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class DisplayPage : Page
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly Dictionary<OverlayRegionKind, OverlayRegionSettings> _drafts = new();
    private readonly ComboBox _selector = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 180 };
    private readonly VisualRegionEditor _editor = new();
    private readonly DashboardSettingsEditor _dashboard;
    private readonly DisplayPreview _preview;
    private readonly FeaturePreview _featurePreview;
    private readonly MediaSettingsEditor _mediaEditor = new();
    private readonly CardSettingsEditor _cardEditor = new();
    private readonly HistorySettingsEditor _historyEditor = new();
    private readonly TextBlock _historyPreview = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(12) };
    private HistorySettings _historyDraft = new();
    private readonly TextBlock _layoutLabel = new() { Text = "桌面布局 · 拖动区域，右下角缩放", FontSize = 14 };
    private readonly VisualLayoutEditor _layout = new();
    private readonly ToggleSwitch _enabled = new() { Header = "显示这一类悬浮内容" };
    private readonly ToggleSwitch _locked = new() { Header = "锁定实际窗口位置" };
    private readonly HashSet<OverlayRegionKind> _layoutDirty = new();
    private readonly CheckBox _avatars = new() { Content = "头像" }, _medals = new() { Content = "粉丝牌" }, _identity = new() { Content = "身份徽章" };
    private readonly StackPanel _shared = new() { Spacing = 8 };
    private readonly ComboBox _frameRate = new() { Header = "滚动帧率", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly SettingSlider _tracks = new("普通弹幕最多轨道", 8, 1, 24);
    private readonly SettingSlider _poll = new("房间信息获取间隔", 30, 5, 600, " 秒");
    private readonly TextBlock _feedback = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _columns = new() { ColumnSpacing = 18 };
    private DashboardSettings _dashboardDraft;
    private DisplaySettings _displayDraft;
    private MediaPreviewSettings _mediaDraft = new();
    private DanmakuCardSettings _cardDraft = new();
    private string? _feature;
    private bool _cardModeDirty, _savedCardEnabled;
    private OverlayRegionKind? _selected;
    private bool _loading, _disposed, _saving;
    public DisplayPage(LiveAssistantViewModel vm, Action openOverlay, Func<bool> isVisible)
    {
        _vm = vm; _visible = isVisible; _dashboardDraft = vm.Settings.Dashboard; _displayDraft = vm.Settings.Display;
        _dashboard = new(_dashboardDraft); _preview = new(vm, isVisible); _featurePreview = new(vm, isVisible);
        var root = new Grid { Padding = new Thickness(20, 12, 20, 12), RowSpacing = 12 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { ColumnSpacing = 14 }; header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.Children.Add(new TextBlock { Text = "可视化设置", FontSize = 22, VerticalAlignment = VerticalAlignment.Center });
        _selector.Items.Add(new ComboBoxItem { Content = "概览消息", Tag = "dashboard" });
        foreach (var kind in Enum.GetValues<OverlayRegionKind>()) _selector.Items.Add(new ComboBoxItem { Content = OverlayRegions.Title(kind), Tag = kind });
        _selector.Items.Add(new ComboBoxItem { Content = "媒体预览", Tag = "media" });
        _selector.Items.Add(new ComboBoxItem { Content = "概览卡片", Tag = "cards" });
        _selector.Items.Add(new ComboBoxItem { Content = "本地历史存档", Tag = "history" });
        Grid.SetColumn(_selector, 1); header.Children.Add(_selector); root.Children.Add(header);
        _columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 280 }); _columns.ColumnDefinitions.Add(new() { Width = new(330), MinWidth = 270 });
        var previewContent = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = "实时预览", FontSize = 16 }, _preview,
            _featurePreview, _historyPreview, _layoutLabel, _layout } };
        _columns.Children.Add(new ScrollViewer { Content = previewContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _shared.Children.Add(new TextBlock { Text = "共享图文元素", FontSize = 13 });
        _shared.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _avatars, _medals, _identity } });
        foreach (var (label, rate) in new[] { ("30 FPS · 省电", 30), ("60 FPS · 平衡", 60), ("120 FPS", 120), ("跟随屏幕", 0) }) _frameRate.Items.Add(new ComboBoxItem { Content = label, Tag = rate });
        var properties = new StackPanel { Spacing = 12, Children = { _dashboard, _mediaEditor, _cardEditor, _historyEditor, _enabled, _shared, _editor, _tracks, _frameRate, _locked, _poll } };
        var propertiesScroll = new ScrollViewer { Content = properties, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(propertiesScroll, 1); _columns.Children.Add(propertiesScroll);
        Grid.SetRow(_columns, 1); root.Children.Add(_columns);
        var footer = new StackPanel { Spacing = 6 }; var buttons = new Grid { ColumnSpacing = 8 };
        var save = UiFactory.Button("保存并应用"); save.Click += async (_, _) => await SaveAsync();
        var restore = UiFactory.Button("还原修改"); restore.Click += (_, _) => { LoadStored(); _feedback.Text = "已还原为当前保存的设置。"; };
        var resetLayout = UiFactory.Button("默认布局"); resetLayout.Click += (_, _) =>
        {
            Capture(); foreach (var kind in Enum.GetValues<OverlayRegionKind>()) { _drafts[kind] = _drafts[kind] with { X = null, Y = null, Width = 0, Height = 0 }; _layoutDirty.Add(kind); }
            LoadSelection(); RefreshPreview(); _feedback.Text = "布局草稿已恢复默认，保存后应用。";
        };
        var examples = UiFactory.Button("保存并显示示例"); examples.Click += async (_, _) => { if (await SaveAsync()) { openOverlay(); vm.SendDisplayExamples(); } };
        var more = DashboardUi.Button("\uE712", "样式操作"); var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "复制当前外观到所有悬浮区域" };
        copy.Click += (_, _) =>
        {
            if (_selected is not { } sourceKind) { _feedback.Text = "先选择一个悬浮类别。"; return; }
            Capture(); var source = _drafts[sourceKind];
            foreach (var kind in Enum.GetValues<OverlayRegionKind>()) _drafts[kind] = _drafts[kind] with
            { FontFamily = source.FontFamily, FontSize = source.FontSize, EmoteSize = source.EmoteSize, LargeEmoteHeight = source.LargeEmoteHeight, Color = source.Color,
                Bold = source.Bold, Italic = source.Italic, Outline = source.Outline, Opacity = source.Opacity };
            LoadSelection(); RefreshPreview(); _feedback.Text = "外观已复制到草稿，保存后应用。";
        };
        var readable = new MenuFlyoutItem { Text = "清晰简约悬浮样式" };
        readable.Click += (_, _) =>
        {
            Capture(); foreach (var kind in Enum.GetValues<OverlayRegionKind>()) _drafts[kind] = _drafts[kind] with
            { FontFamily = "Microsoft YaHei UI", Bold = false, Italic = false, Outline = kind != OverlayRegionKind.Interaction, Opacity = 0.95,
                Color = kind switch { OverlayRegionKind.Danmaku => "#FFFFFF", OverlayRegionKind.SuperChat => "#E6B85D", OverlayRegionKind.Gift => "#D48BE5", OverlayRegionKind.Interaction => "#71C6A6", _ => "#7BADE8" } };
            LoadSelection(); RefreshPreview(); _feedback.Text = "样式已更新到草稿，保存后应用。";
        };
        menu.Items.Add(copy); menu.Items.Add(readable); more.Flyout = menu;
        buttons.Children.Add(save); buttons.Children.Add(restore); buttons.Children.Add(resetLayout); buttons.Children.Add(examples); buttons.Children.Add(more);
        for (var i = 0; i < buttons.Children.Count; i++)
        {
            buttons.ColumnDefinitions.Add(new() { Width = i == 4 ? GridLength.Auto : new(1, GridUnitType.Star) }); Grid.SetColumn((FrameworkElement)buttons.Children[i], i);
            if (buttons.Children[i] is Button button && i < 4) { button.MinWidth = 0; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.Padding = new(8, 8, 8, 8); button.FontSize = 12; }
        }
        footer.Children.Add(buttons); footer.Children.Add(_feedback); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _selector.SelectionChanged += (_, _) =>
        {
            if (_loading) return; Capture(); _selected = (_selector.SelectedItem as ComboBoxItem)?.Tag is OverlayRegionKind kind ? kind : null;
            _feature = (_selector.SelectedItem as ComboBoxItem)?.Tag is string tag && tag is "media" or "cards" or "history" ? tag : null;
            LoadSelection(); RefreshPreview();
        };
        _editor.Changed += OnEdited; _dashboard.Changed += OnEdited;
        _mediaEditor.Changed += OnEdited; _cardEditor.Changed += OnEdited;
        _historyEditor.Changed += OnEdited;
        foreach (var control in new DependencyObject[] { _enabled, _shared, _tracks, _frameRate, _locked, _poll }) SettingEvents.Watch(control, OnEdited);
        _layout.Selected += kind => { _selector.SelectedItem = _selector.Items.OfType<ComboBoxItem>().First(item => item.Tag is OverlayRegionKind value && value == kind); };
        _layout.Changed += (kind, region) =>
        {
            _drafts[kind] = region;
            _layoutDirty.Add(kind);
            if (_selected == kind) { _loading = true; _editor.LoadLayout(region); _loading = false; }
            _feedback.Text = "布局草稿已更新 · 保存后应用";
        };
        _columns.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); _columns.RowDefinitions.Add(new() { Height = new(0) });
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 700;
            _columns.ColumnDefinitions[0].MinWidth = narrow ? 0 : 280; _columns.ColumnDefinitions[1].MinWidth = narrow ? 0 : 270;
            _columns.ColumnDefinitions[1].Width = new(narrow ? 0 : Math.Clamp(e.NewSize.Width * 0.34, 270, 360));
            _columns.RowDefinitions[1].Height = new(narrow ? 1 : 0, GridUnitType.Star); Grid.SetColumn(propertiesScroll, narrow ? 0 : 1); Grid.SetRow(propertiesScroll, narrow ? 1 : 0);
        };
        vm.PropertyChanged += OnSettingsChanged;
        Loaded += (_, _) => ResumeVisuals(); Unloaded += (_, _) => { _disposed = true; vm.PropertyChanged -= OnSettingsChanged; _preview.Dispose(); _featurePreview.Dispose(); };
        LoadStored();
    }
    private void LoadStored()
    {
        _loading = true; _layoutDirty.Clear(); _displayDraft = _vm.Settings.Display; _dashboardDraft = _vm.Settings.Dashboard;
        _mediaDraft = _vm.Settings.MediaPreview; _cardDraft = _vm.Settings.Cards; _cardModeDirty = false; _savedCardEnabled = _cardDraft.Enabled;
        _historyDraft = _vm.Settings.History;
        foreach (var kind in Enum.GetValues<OverlayRegionKind>()) _drafts[kind] = OverlayRegions.Get(_displayDraft, kind);
        _dashboard.Load(_dashboardDraft); _tracks.Value = _displayDraft.MaxTracks; _poll.Value = _vm.Settings.PollIntervalSeconds;
        _frameRate.SelectedItem = _frameRate.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (int)item.Tag == _displayDraft.AnimationFrameRate) ?? _frameRate.Items[1];
        _locked.IsOn = _displayDraft.LayoutLocked;
        if (_selector.SelectedIndex < 0) _selector.SelectedIndex = 0;
        _selected = (_selector.SelectedItem as ComboBoxItem)?.Tag is OverlayRegionKind selected ? selected : null;
        _feature = (_selector.SelectedItem as ComboBoxItem)?.Tag is string tag && tag is "media" or "cards" or "history" ? tag : null;
        _loading = false; LoadSelection(); RefreshPreview();
    }
    private void LoadSelection()
    {
        _loading = true; var overview = _selected is null && _feature is null;
        _dashboard.Visibility = _poll.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        _mediaEditor.Visibility = _feature == "media" ? Visibility.Visible : Visibility.Collapsed;
        _cardEditor.Visibility = _feature == "cards" ? Visibility.Visible : Visibility.Collapsed;
        _historyEditor.Visibility = _feature == "history" ? Visibility.Visible : Visibility.Collapsed;
        _historyPreview.Visibility = _feature == "history" ? Visibility.Visible : Visibility.Collapsed;
        _preview.Visibility = _layout.Visibility = _layoutLabel.Visibility = _feature is null ? Visibility.Visible : Visibility.Collapsed;
        _featurePreview.Visibility = _feature is "media" or "cards" ? Visibility.Visible : Visibility.Collapsed;
        _editor.Visibility = _shared.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        _enabled.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        _tracks.Visibility = _selected == OverlayRegionKind.Danmaku ? Visibility.Visible : Visibility.Collapsed;
        _frameRate.Visibility = _selected is not null and not OverlayRegionKind.Interaction ? Visibility.Visible : Visibility.Collapsed;
        _locked.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        _avatars.IsChecked = _dashboardDraft.ShowAvatars; _medals.IsChecked = _dashboardDraft.ShowMedals; _identity.IsChecked = _dashboardDraft.ShowIdentity;
        if (_selected is { } kind) { _editor.Load(kind, _drafts[kind]); _enabled.IsOn = OverlayRegions.Enabled(_displayDraft, kind); }
        else if (_feature == "media") _mediaEditor.Load(_mediaDraft);
        else if (_feature == "cards") _cardEditor.Load(_cardDraft);
        else if (_feature == "history") _historyEditor.Load(_historyDraft, _vm.History.Status);
        else _dashboard.Load(_dashboardDraft);
        _loading = false;
    }
    private void Capture()
    {
        if (_loading) return;
        if (_feature == "media") { _mediaDraft = _mediaEditor.Read(); return; }
        if (_feature == "cards") { _cardDraft = _cardEditor.Read(); _cardModeDirty = _cardDraft.Enabled != _savedCardEnabled; return; }
        if (_feature == "history") { _historyDraft = _historyEditor.Read(); return; }
        if (_selected is { } kind)
        {
            var previous = _drafts[kind]; var next = _editor.Read(previous) with { Enabled = _enabled.IsOn }; _drafts[kind] = next;
            if (previous.X != next.X || previous.Y != next.Y || previous.Width != next.Width || previous.Height != next.Height) _layoutDirty.Add(kind);
            _displayDraft = OverlayRegions.WithEnabled(_displayDraft, kind, _enabled.IsOn);
            _dashboardDraft = _dashboardDraft with { ShowAvatars = _avatars.IsChecked == true, ShowMedals = _medals.IsChecked == true, ShowIdentity = _identity.IsChecked == true };
        }
        else _dashboardDraft = _dashboard.Read(_dashboardDraft);
        _displayDraft = _displayDraft with { LayoutLocked = _locked.IsOn, MaxTracks = (int)_tracks.Value, AnimationFrameRate = (_frameRate.SelectedItem as ComboBoxItem)?.Tag is int rate ? rate : 60 };
    }
    private void OnEdited() { if (_loading || _disposed) return; Capture(); RefreshPreview(); _feedback.Text = "预览已更新 · 尚未应用到实际窗口"; }
    private void RefreshPreview()
    {
        _preview.SetActive(_feature is null && _visible());
        if (_feature is "media" or "cards") _featurePreview.Update(_feature, _mediaDraft, _cardDraft, _dashboardDraft);
        _featurePreview.SetActive(_feature is "media" or "cards" && _visible());
        if (_feature == "history")
        {
            try { _historyPreview.Text = HistorySettingsEditor.Preview(_historyDraft, _vm.History.Status); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.IOException) { _historyPreview.Text = "目录无效：" + ex.Message; }
        }
        if (_feature is not null) return;
        _preview.Update(_selected, _selected is { } kind ? _drafts[kind] : new(), _dashboardDraft, _displayDraft.AnimationFrameRate,
            _selected is not { } selected || OverlayRegions.Enabled(_displayDraft, selected), _displayDraft.MaxTracks);
        _layout.Update(_displayDraft, _drafts, _selected);
    }
    private async Task<bool> SaveAsync()
    {
        if (_saving) return false;
        Capture();
        try
        {
            var display = _displayDraft with { ScrollSpeed = _drafts[OverlayRegionKind.Danmaku].ScrollSpeed };
            foreach (var (kind, region) in _drafts) display = OverlayRegions.With(display, kind, region.Normalize());
            var current = _vm.Settings.Dashboard;
            var dashboard = _dashboardDraft with { DanmakuColumnWidth = current.DanmakuColumnWidth, GiftColumnWidth = current.GiftColumnWidth, SuperChatColumnWidth = current.SuperChatColumnWidth,
                InteractionPanelRatio = current.InteractionPanelRatio, RankPanelRatio = current.RankPanelRatio, RankPanelCollapsed = current.RankPanelCollapsed, RankKind = current.RankKind };
            var cards = _cardModeDirty ? _cardDraft : _cardDraft with { Enabled = _vm.Settings.Cards.Enabled };
            _saving = true; _feedback.Text = "正在保存并应用；更换存档目录时会先复制并校验，请稍候…";
            await _vm.SaveAsync(_vm.Settings with { Display = display, Dashboard = dashboard.Normalize(), PollIntervalSeconds = (int)_poll.Value,
                MediaPreview = _mediaDraft.Normalize(), Cards = cards.Normalize(), History = _historyDraft.Normalize() });
            _cardDraft = _vm.Settings.Cards; _savedCardEnabled = _cardDraft.Enabled; _cardModeDirty = false;
            if (_feature == "cards") { _loading = true; _cardEditor.Load(_cardDraft); _loading = false; }
            _layoutDirty.Clear();
            _feedback.Text = "已保存并应用。"; return true;
        }
        catch (Exception ex) { _feedback.Text = $"保存失败：{ex.Message}"; return false; }
        finally { _saving = false; }
    }
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_disposed && !_saving && args.PropertyName == nameof(LiveAssistantViewModel.HistoryWarning) && _feature == "history")
        { _historyEditor.UpdateUsage(_vm.History.Status); RefreshPreview(); return; }
        if (_disposed || _saving || args.PropertyName != nameof(LiveAssistantViewModel.Settings)) return;
        Capture();
        if (!_cardModeDirty)
        {
            _cardDraft = _cardDraft with { Enabled = _vm.Settings.Cards.Enabled }; _savedCardEnabled = _cardDraft.Enabled;
            if (_feature == "cards") { _loading = true; _cardEditor.Load(_cardDraft); _loading = false; }
        }
        foreach (var kind in Enum.GetValues<OverlayRegionKind>())
        {
            if (_layoutDirty.Contains(kind)) continue;
            var stored = OverlayRegions.Get(_vm.Settings.Display, kind);
            _drafts[kind] = _drafts[kind] with { X = stored.X, Y = stored.Y, Width = stored.Width, Height = stored.Height };
            if (_selected == kind) { _loading = true; _editor.LoadLayout(_drafts[kind]); _loading = false; }
        }
        RefreshPreview();
    }
    internal void SuspendVisuals() { _preview.SetActive(false); _featurePreview.SetActive(false); }
    internal void ResumeVisuals() { if (!_disposed) RefreshPreview(); }
    internal static NumberBox Number(string label, double value, double min, double max) => new()
    { Header = label, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    internal static int Value(NumberBox box, int fallback) => double.IsFinite(box.Value) ? (int)Math.Clamp(Math.Round(box.Value), box.Minimum, box.Maximum) : fallback;
}
