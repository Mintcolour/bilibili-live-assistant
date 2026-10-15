using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BilibiliLiveAssistant.App;

// A single layout callback per change, never a polling timer. Inline images are
// owned by the row; their own Loaded state is deliberately not a loading gate.
internal sealed class ListImageVisibility : IDisposable
{
    private readonly ListView _list;
    private readonly Func<bool> _active;
    private readonly Action _update;
    private ScrollViewer? _scroll;
    private bool _pending, _disposed;
    internal ListImageVisibility(ListView list, Func<bool> active, Action update)
    {
        _list = list; _active = active; _update = update;
        list.Loaded += Loaded; list.SizeChanged += SizeChanged;
    }
    internal bool IsVisible(object item)
    {
        if (_disposed || !_active() || _list.Visibility != Visibility.Visible || _list.ItemsPanelRoot is not ItemsStackPanel panel) return false;
        var index = _list.Items.IndexOf(item);
        return index >= 0 && panel.FirstVisibleIndex >= 0 && index >= panel.FirstVisibleIndex && index <= panel.LastVisibleIndex;
    }
    internal void Request()
    {
        if (_disposed || _pending) return;
        _pending = true; _list.LayoutUpdated += LayoutReady;
    }
    internal void RefreshNow() { if (_disposed) return; _update(); Request(); }
    private void LayoutReady(object? sender, object args)
    {
        _list.LayoutUpdated -= LayoutReady; _pending = false;
        if (!_disposed) { HookScroll(); _update(); }
    }
    private void Loaded(object sender, RoutedEventArgs args) { HookScroll(); RefreshNow(); }
    private void SizeChanged(object sender, SizeChangedEventArgs args) => Request();
    private void Scrolled(object? sender, ScrollViewerViewChangedEventArgs args) => RefreshNow();
    private void HookScroll()
    {
        var scroll = Find(_list);
        if (_scroll == scroll) return;
        if (_scroll is not null) _scroll.ViewChanged -= Scrolled;
        _scroll = scroll;
        if (_scroll is not null) _scroll.ViewChanged += Scrolled;
    }
    private static ScrollViewer? Find(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _list.Loaded -= Loaded; _list.SizeChanged -= SizeChanged; _list.LayoutUpdated -= LayoutReady;
        if (_scroll is not null) _scroll.ViewChanged -= Scrolled;
    }
}
