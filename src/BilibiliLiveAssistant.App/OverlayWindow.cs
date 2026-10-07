using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BilibiliLiveAssistant.Core;
using WinRT.Interop;

namespace BilibiliLiveAssistant.App;

public sealed class OverlayWindow : Window
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Canvas _canvas = new();
    private readonly List<OverlayItem> _items = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly nint _hwnd;
    private bool _clickThrough;

    public OverlayWindow(LiveAssistantViewModel vm)
    {
        _vm = vm;
        Title = "B站弹幕";
        _canvas.Background = new SolidColorBrush(ColorHelper.FromArgb(155, 12, 16, 24));
        Content = _canvas;
        _hwnd = WindowNative.GetWindowHandle(this);
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) => Tick();
        _vm.Events.CollectionChanged += OnEventsChanged;
        Closed += (_, _) => { _vm.Events.CollectionChanged -= OnEventsChanged; _timer.Stop(); };
        ConfigureWindow();
    }

    public void ActivateOverlay()
    {
        Activate();
        _timer.Start();
    }

    private void ConfigureWindow()
    {
        var id = Win32Interop.GetWindowIdFromWindow(_hwnd);
        var appWindow = AppWindow.GetFromWindowId(id);
        var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
        var height = (int)(area.WorkArea.Height * _vm.Settings.Display.WindowHeightRatio);
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.WorkArea.X, area.WorkArea.Y, area.WorkArea.Width, height));
        SetWindowLongPtr(_hwnd, -20, GetWindowLongPtr(_hwnd, -20) | 0x00000080); // WS_EX_TOOLWINDOW
        SetWindowPos(_hwnd, new nint(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST + NOMOVE/NOSIZE/SHOW
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null) return;
        foreach (LiveEvent item in e.NewItems)
        {
            var text = new TextBlock
            {
                Text = Format(item),
                FontSize = _vm.Settings.Display.FontSize,
                FontWeight = new Windows.UI.Text.FontWeight(600),
                Foreground = new SolidColorBrush(ParseColor(item.Color)),
            };
            var overlay = new OverlayItem(text, _canvas.ActualWidth + 20, _items.Count % Math.Max(1, _vm.Settings.Display.MaxTracks));
            _items.Add(overlay);
            _canvas.Children.Add(text);
            Canvas.SetLeft(text, overlay.X);
            Canvas.SetTop(text, overlay.Track * _vm.Settings.Display.TrackHeight);
        }
    }

    private void Tick()
    {
        var speed = _vm.Settings.Display.ScrollSpeed * 0.016;
        foreach (var item in _items.ToArray())
        {
            item.X -= speed;
            Canvas.SetLeft(item.Text, item.X);
            if (item.X + item.Text.ActualWidth < -50)
            {
                _canvas.Children.Remove(item.Text);
                _items.Remove(item);
            }
        }
    }

    private static string Format(LiveEvent item) => item.Type == LiveEventType.Online || item.Type == LiveEventType.Rank
        ? item.Content
        : $"{item.Username} {item.Content}";

    private static Windows.UI.Color ParseColor(string value)
    {
        try { return Microsoft.UI.ColorHelper.FromArgb(255, Convert.ToByte(value[1..3], 16), Convert.ToByte(value[3..5], 16), Convert.ToByte(value[5..7], 16)); }
        catch { return Colors.White; }
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        var style = GetWindowLongPtr(_hwnd, -20);
        style = enabled ? style | 0x00000020 : style & ~0x00000020; // WS_EX_TRANSPARENT
        SetWindowLongPtr(_hwnd, -20, style);
    }

    private sealed class OverlayItem
    {
        public OverlayItem(TextBlock text, double x, int track) { Text = text; X = x; Track = track; }
        public TextBlock Text { get; }
        public double X { get; set; }
        public int Track { get; }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
