using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace BilibiliLiveAssistant.App;

// Avatar downloads are limited to visible rows, using the existing bounded cache.
internal sealed class HistoryAnchorList : Grid, IDisposable
{
    internal ListView List { get; }
    private readonly LiveAssistantViewModel _vm;
    private readonly MessageAnimationClock _clock;
    private readonly Dictionary<ContentControl, (HistoryAnchor Anchor, BiliImageView Image)> _images = new();
    private ScrollViewer? _scroll;
    private bool _active, _disposed, _pending;
    public HistoryAnchorList(LiveAssistantViewModel vm, ListView? suggestions = null)
    {
        List = suggestions ?? new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _vm = vm; _clock = new(() => _active && !_disposed);
        List.ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ContentControl Content='{Binding Label}' HorizontalContentAlignment='Stretch' IsTabStop='False'/></DataTemplate>");
        List.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
        List.ContainerContentChanging += Render; List.Loaded += LoadedList;
        if (suggestions is null) Children.Add(List);
        else LoadedList(List, new RoutedEventArgs());
    }
    internal static string DisplayName(HistoryAnchor anchor) => !string.IsNullOrWhiteSpace(anchor.Name) ? anchor.Name : $"待识别房间 {anchor.RoomId}";
    internal static Grid Row(HistoryAnchor anchor, BiliImageView image)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(2, 6, 2, 6) };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.Children.Add(image);
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = {
            new TextBlock { Text = DisplayName(anchor), FontSize = 14, FontWeight = new(600), TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = $"房间 {anchor.RoomId}", FontSize = 11, Opacity = 0.6 } } };
        Grid.SetColumn(text, 1); row.Children.Add(text); ToolTipService.SetToolTip(row, anchor.Label + $" · 房间 {anchor.RoomId}"); return row;
    }
    private bool Visible(HistoryAnchor anchor)
    {
        if (!_active || _disposed || List.ItemsPanelRoot is not ItemsStackPanel panel) return false;
        var index = List.Items.IndexOf(anchor); return index >= panel.FirstVisibleIndex && index <= panel.LastVisibleIndex;
    }
    private void Render(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (args.ItemContainer.ContentTemplateRoot is not ContentControl host)
        { if (!args.InRecycleQueue && args.Phase == 0) args.RegisterUpdateCallback(Render); return; }
        Release(host);
        if (!args.InRecycleQueue && args.Item is HistoryAnchor anchor)
        {
            var image = new BiliImageView(_vm.Images, anchor.AvatarUrl, DisplayName(anchor), 36, 36, false, _clock, () => Visible(anchor), true);
            _images.Add(host, (anchor, image)); host.Content = Row(anchor, image); RequestUpdate();
        }
        args.Handled = true;
    }
    private void Release(ContentControl host) { if (_images.Remove(host, out var row)) row.Image.Dispose(); host.Content = null; }
    private void LoadedList(object sender, RoutedEventArgs args)
    {
        if (_scroll is not null) _scroll.ViewChanged -= Scrolled;
        List.ApplyTemplate(); _scroll = FindScroll(List); if (_scroll is not null) _scroll.ViewChanged += Scrolled; RequestUpdate();
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private void Scrolled(object? sender, ScrollViewerViewChangedEventArgs args) => UpdateVisible();
    private void RequestUpdate() { if (_disposed || _pending) return; _pending = true; List.LayoutUpdated += LayoutReady; }
    private void LayoutReady(object? sender, object args) { List.LayoutUpdated -= LayoutReady; _pending = false; UpdateVisible(); }
    private void UpdateVisible()
    {
        foreach (var row in _images.Values) if (Visible(row.Anchor)) row.Image.Activate(List.XamlRoot?.RasterizationScale ?? 1); else row.Image.Deactivate();
    }
    internal void SetItems(IReadOnlyList<HistoryAnchor> anchors)
    {
        foreach (var host in _images.Keys.ToArray()) Release(host); List.ItemsSource = anchors; RequestUpdate();
    }
    internal void SetActive(bool active) { _active = active; if (!active) _clock.Suspend(); UpdateVisible(); if (active) RequestUpdate(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _active = false;
        List.ContainerContentChanging -= Render; List.Loaded -= LoadedList; List.LayoutUpdated -= LayoutReady;
        if (_scroll is not null) _scroll.ViewChanged -= Scrolled;
        foreach (var host in _images.Keys.ToArray()) Release(host); List.ItemsSource = null; _clock.Dispose();
    }
}
