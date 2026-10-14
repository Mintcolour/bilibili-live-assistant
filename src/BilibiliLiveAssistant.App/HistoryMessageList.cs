using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace BilibiliLiveAssistant.App;

internal sealed class HistoryMessageList : Grid, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Func<bool> _visible;
    private readonly MessageAnimationClock _clock;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0) };
    private readonly Dictionary<ContentControl, (HistoryRow Row, MessageCard Card)> _cards = new();
    private ScrollViewer? _scroll;
    private bool _active = true, _disposed, _dialogOpen;
    private bool _layoutPending;
    public HistoryMessageList(LiveAssistantViewModel vm, Func<bool> visible)
    {
        _vm = vm; _visible = visible; _clock = new(() => _active && !_disposed && _visible());
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ContentControl HorizontalContentAlignment='Stretch' IsTabStop='False'/></DataTemplate>");
        _list.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        _list.ContainerContentChanging += Render; _list.Loaded += LoadedList; _list.DoubleTapped += async (_, args) =>
        {
            if (_dialogOpen || _disposed) return;
            var node = args.OriginalSource as DependencyObject;
            while (node is not null && node is not ListViewItem) node = VisualTreeHelper.GetParent(node);
            if ((node as ListViewItem)?.Content is not HistoryRow row) return;
            _dialogOpen = true;
            try
            {
                var f = row.Fields;
                var text = $"接收：{row.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}\nUTC：{row.ReceivedAt:O}\n接收时日期：{row.LocalDay}\n上游时间：{f.UpstreamTime?.ToLocalTime().ToString("O") ?? "暂无数据"}\n" +
                    $"发送者：{row.Event.Username} · UID {(row.Event.Uid > 0 ? row.Event.Uid.ToString() : "身份未知")}\n类型：{HistoryPage.TypeName(row.Event.Type)}\n\n{row.Event.Content}\n\n" +
                    $"礼物：{f.GiftName ?? "暂无数据"} · ID {f.GiftId?.ToString() ?? "暂无数据"}\n数量 {f.Quantity?.ToString() ?? "暂无数据"} · 币种 {f.CoinType ?? "暂无数据"}\n原始单价 {f.UnitPrice?.ToString() ?? "暂无数据"} · 总币数 {f.TotalCoin?.ToString() ?? "暂无数据"}\n" +
                    $"金额：{(f.AmountMilliYuan is { } amount ? "¥" + (amount / 1000m).ToString("0.###") : "暂无数据")}\n可靠事件 ID：{f.UniqueId ?? "暂无数据"}\n礼物交易/连击标识（不用于去重）：{f.TransactionId ?? "暂无数据"}\n监视记录：{row.SessionId}";
                await new ContentDialog { Title = "原始存档属性", Content = new ScrollViewer { MaxHeight = 450, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }, CloseButtonText = "关闭", XamlRoot = XamlRoot }.ShowAsync();
            }
            finally { _dialogOpen = false; }
        };
        Children.Add(_list);
    }
    public void SetRows(IReadOnlyList<HistoryRow> rows)
    {
        foreach (var host in _cards.Keys.ToArray()) Release(host);
        _list.ItemsSource = rows; _scroll?.ChangeView(null, 0, null, true);
        RequestVisibilityUpdate();
    }
    private bool RowVisible(HistoryRow row)
    {
        if (!_active || _disposed || !_visible()) return false;
        if (_list.ItemsPanelRoot is not ItemsStackPanel panel) return false;
        var index = _list.Items.IndexOf(row);
        return index >= panel.FirstVisibleIndex && index <= panel.LastVisibleIndex;
    }
    private void Render(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        args.ItemContainer.Padding = new Thickness(0); args.ItemContainer.MinHeight = 0;
        if (args.ItemContainer.ContentTemplateRoot is not ContentControl host)
        { if (!args.InRecycleQueue && args.Phase == 0) args.RegisterUpdateCallback(Render); return; }
        Release(host);
        if (!args.InRecycleQueue && args.Item is HistoryRow row)
        {
            // Supplying the existing style suppresses automatic rich-media expansion in history.
            var style = _vm.Settings.Dashboard;
            var card = new MessageCard(new(row.Event), _vm, _clock, () => RowVisible(row), false, style);
            var stack = new StackPanel { Spacing = 3, Margin = new Thickness(4, 3, 4, 6) };
            stack.Children.Add(new TextBlock { Text = $"{row.LocalDay} · {HistoryPage.TypeName(row.Event.Type)} · {(row.Event.Uid > 0 ? "UID " + row.Event.Uid : "身份未知")}", FontSize = 11, Opacity = 0.6 });
            stack.Children.Add(card); host.Content = stack; _cards.Add(host, (row, card));
            if (RowVisible(row)) card.Activate(); else card.Deactivate();
            RequestVisibilityUpdate();
        }
        args.Handled = true;
    }
    private void LoadedList(object sender, RoutedEventArgs args)
    {
        if (_scroll is not null) _scroll.ViewChanged -= Scrolled;
        _list.ApplyTemplate(); _scroll = FindScroll(_list);
        if (_scroll is not null) _scroll.ViewChanged += Scrolled;
        DispatcherQueue.TryEnqueue(UpdateVisible);
    }
    private void Scrolled(object? sender, ScrollViewerViewChangedEventArgs args) => UpdateVisible();
    private void RequestVisibilityUpdate()
    {
        if (_disposed || _layoutPending) return; _layoutPending = true; _list.LayoutUpdated += LayoutReady;
    }
    private void LayoutReady(object? sender, object args)
    { _list.LayoutUpdated -= LayoutReady; _layoutPending = false; UpdateVisible(); }
    private void UpdateVisible()
    {
        if (_disposed) return;
        foreach (var (_, value) in _cards)
            if (RowVisible(value.Row)) value.Card.Activate(); else value.Card.Deactivate();
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private void Release(ContentControl host)
    { if (_cards.Remove(host, out var previous)) previous.Card.Dispose(); host.Content = null; }
    public void SetActive(bool active) { _active = active; if (!active) _clock.Suspend(); UpdateVisible(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _list.ContainerContentChanging -= Render; _list.Loaded -= LoadedList;
        if (_scroll is not null) _scroll.ViewChanged -= Scrolled;
        _list.LayoutUpdated -= LayoutReady;
        foreach (var host in _cards.Keys.ToArray()) Release(host);
        _list.ItemsSource = null; _clock.Dispose();
    }
}
