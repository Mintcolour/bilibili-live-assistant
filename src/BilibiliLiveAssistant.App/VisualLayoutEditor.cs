using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

internal sealed class VisualLayoutEditor : Grid
{
    private readonly Canvas _canvas = new() { Height = 210, Background = MessageCard.Brush("#101319") };
    private readonly Dictionary<OverlayRegionKind, Grid> _boxes = new();
    private IReadOnlyDictionary<OverlayRegionKind, OverlayRegionSettings>? _regions;
    private DisplaySettings _display = new();
    private OverlayRegionKind? _selected;
    private double _scale = 0.2, _offsetX, _offsetY;
    private bool _dragging;
    private System.Drawing.Rectangle _dragBounds;
    public event Action<OverlayRegionKind, OverlayRegionSettings>? Changed;
    public event Action<OverlayRegionKind>? Selected;
    public VisualLayoutEditor()
    {
        Children.Add(_canvas);
        _canvas.SizeChanged += (_, _) => Draw();
        foreach (var kind in Enum.GetValues<OverlayRegionKind>())
        {
            var box = new Grid();
            var outline = new Border { BorderThickness = new(1), BorderBrush = MessageCard.Brush("#79A9E9"), Background = MessageCard.Brush("#79A9E9", 0.12), IsHitTestVisible = false };
            var drag = new Thumb { Background = MessageCard.Brush("#FFFFFF", 0.01), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            box.Children.Add(outline); box.Children.Add(drag);
            box.Children.Add(new TextBlock { Text = OverlayRegions.Title(kind), FontSize = 11, Margin = new(4), TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false });
            var resize = new Thumb { Width = 14, Height = 14, Background = MessageCard.Brush("#79A9E9"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            box.Children.Add(resize);
            drag.DragStarted += (_, _) => BeginDrag(kind); resize.DragStarted += (_, _) => BeginDrag(kind);
            drag.DragCompleted += (_, _) => EndDrag(kind); resize.DragCompleted += (_, _) => EndDrag(kind);
            drag.DragDelta += (_, e) => Edit(kind, e.HorizontalChange, e.VerticalChange, false);
            resize.DragDelta += (_, e) => Edit(kind, e.HorizontalChange, e.VerticalChange, true);
            DashboardUi.Describe(drag, $"拖动{OverlayRegions.Title(kind)}，保存后应用"); DashboardUi.Describe(resize, "拖动调整尺寸");
            _boxes[kind] = box;
        }
    }
    public void Update(DisplaySettings display, IReadOnlyDictionary<OverlayRegionKind, OverlayRegionSettings> regions, OverlayRegionKind? selected)
    { _display = display; _regions = regions; _selected = selected; if (!_dragging) Draw(); }
    private void Draw()
    {
        if (_regions is null || _canvas.ActualWidth < 1) return;
        var desktop = Forms.SystemInformation.VirtualScreen;
        _scale = Math.Min((_canvas.ActualWidth - 16) / Math.Max(1, desktop.Width), 194d / Math.Max(1, desktop.Height));
        _offsetX = (_canvas.ActualWidth - desktop.Width * _scale) / 2; _offsetY = (210 - desktop.Height * _scale) / 2;
        _canvas.Children.Clear();
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var rect = new Border { Width = screen.Bounds.Width * _scale, Height = screen.Bounds.Height * _scale, Background = MessageCard.Brush("#222733"), BorderBrush = MessageCard.Brush("#48516A"), BorderThickness = new(1), IsHitTestVisible = false };
            Canvas.SetLeft(rect, _offsetX + (screen.Bounds.X - desktop.X) * _scale); Canvas.SetTop(rect, _offsetY + (screen.Bounds.Y - desktop.Y) * _scale); _canvas.Children.Add(rect);
        }
        foreach (var kind in Enum.GetValues<OverlayRegionKind>().OrderBy(kind => kind == _selected ? 1 : 0))
        {
            var bounds = OverlayLayout.Bounds(_display, kind, _regions[kind]); var box = _boxes[kind];
            box.Width = Math.Max(18, bounds.Width * _scale); box.Height = Math.Max(18, bounds.Height * _scale); box.Opacity = _selected is null || _selected == kind ? 1 : 0.4;
            Canvas.SetLeft(box, _offsetX + (bounds.X - desktop.X) * _scale); Canvas.SetTop(box, _offsetY + (bounds.Y - desktop.Y) * _scale); _canvas.Children.Add(box);
        }
    }
    private void Edit(OverlayRegionKind kind, double dx, double dy, bool resize)
    {
        if (_regions is null) return;
        var region = _regions[kind]; var bounds = _dragBounds;
        var next = resize ? region with { X = bounds.X, Y = bounds.Y, Width = Math.Max(160, bounds.Width + (int)Math.Round(dx / _scale)), Height = Math.Max(80, bounds.Height + (int)Math.Round(dy / _scale)) }
            : region with { X = bounds.X + (int)Math.Round(dx / _scale), Y = bounds.Y + (int)Math.Round(dy / _scale), Width = bounds.Width, Height = bounds.Height };
        var desktop = Forms.SystemInformation.VirtualScreen;
        var clamped = new System.Drawing.Rectangle(Math.Clamp(next.X!.Value, desktop.Left, Math.Max(desktop.Left, desktop.Right - next.Width)),
            Math.Clamp(next.Y!.Value, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - next.Height)), Math.Min(next.Width, desktop.Width), Math.Min(next.Height, desktop.Height));
        _dragBounds = clamped;
        Changed?.Invoke(kind, next with { X = clamped.X, Y = clamped.Y, Width = clamped.Width, Height = clamped.Height });
        // Keep the dragged Thumb attached until pointer release.
        var box = _boxes[kind]; box.Width = Math.Max(18, clamped.Width * _scale); box.Height = Math.Max(18, clamped.Height * _scale);
        Canvas.SetLeft(box, _offsetX + (clamped.X - desktop.X) * _scale); Canvas.SetTop(box, _offsetY + (clamped.Y - desktop.Y) * _scale);
    }
    private void BeginDrag(OverlayRegionKind kind)
    { if (_regions is null) return; _dragging = true; _dragBounds = OverlayLayout.Bounds(_display, kind, _regions[kind]); Selected?.Invoke(kind); }
    private void EndDrag(OverlayRegionKind kind)
    {
        _dragging = false;
        if (_regions is not null)
        {
            var region = _regions[kind]; var bounds = OverlayLayout.Bounds(_display, kind, region);
            Changed?.Invoke(kind, region with { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height });
        }
        Draw();
    }
}
