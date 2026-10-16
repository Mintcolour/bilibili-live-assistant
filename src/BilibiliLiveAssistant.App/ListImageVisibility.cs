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
    private readonly Func<object, int>? _indexOf;
    private ScrollViewer? _scroll;
    private XamlRoot? _root;
    private bool _pending, _disposed, _retriedLayout;
    internal ListImageVisibility(ListView list, Func<bool> active, Action update, Func<object, int>? indexOf = null)
    {
        _list = list; _active = active; _update = update; _indexOf = indexOf;
        list.Loaded += Loaded; list.SizeChanged += SizeChanged;
    }
    internal bool IsVisible(object item)
    {
        if (_disposed || !_active() || _list.Visibility != Visibility.Visible || _list.ItemsPanelRoot is not ItemsStackPanel panel) return false;
        var index = _indexOf is null ? _list.Items.IndexOf(item) : _indexOf(item);
        return index >= 0 && panel.FirstVisibleIndex >= 0 && index >= panel.FirstVisibleIndex && index <= panel.LastVisibleIndex;
    }
    internal void Request()
    {
        if (_disposed || _pending || !_active()) return;
        _pending = true; _list.LayoutUpdated += LayoutReady;
    }
    private void LayoutReady(object? sender, object args)
    {
        _list.LayoutUpdated -= LayoutReady; _pending = false;
        if (_disposed || !_active()) return;
        HookScroll(); _update();
        var valid = _list.ItemsPanelRoot is ItemsStackPanel { FirstVisibleIndex: >= 0, LastVisibleIndex: >= 0 };
        // Some templates report their range only after the first layout. Retry
        // once, then wait for a real container/size/scroll change; never poll.
        if (!valid && _list.Items.Count > 0 && !_retriedLayout) { _retriedLayout = true; Request(); }
        else if (valid) _retriedLayout = false;
    }
    private void Loaded(object sender, RoutedEventArgs args)
    {
        HookScroll();
        if (_root != _list.XamlRoot)
        {
            if (_root is not null) _root.Changed -= RootChanged;
            _root = _list.XamlRoot;
            if (_root is not null) _root.Changed += RootChanged;
        }
        Request();
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Request();
    private void SizeChanged(object sender, SizeChangedEventArgs args) => Request();
    private void Scrolled(object? sender, ScrollViewerViewChangedEventArgs args) => Request();
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
        if (_root is not null) _root.Changed -= RootChanged;
    }
}
