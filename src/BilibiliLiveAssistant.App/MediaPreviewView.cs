using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace BilibiliLiveAssistant.App;

internal sealed class MediaPreviewGroup : StackPanel, IDisposable
{
    private readonly List<MediaPreviewTile> _tiles = new();
    public MediaPreviewGroup(LiveEvent message, LiveAssistantViewModel vm, MessageAnimationClock clock, Func<bool> visible)
    {
        Spacing = 4;
        foreach (var reference in message.MediaReferences.Take(3))
        {
            var expander = new Expander { Header = $"{(reference.Kind == MediaKind.Bilibili ? "视频" : "Steam")} · {reference.Id}",
                IsExpanded = vm.Settings.MediaPreview.Mode == MediaPreviewMode.Inline, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var tile = new MediaPreviewTile(reference, vm, clock, () => visible() && expander.IsExpanded, vm.Settings.MediaPreview, vm.Settings.Dashboard);
            expander.Content = tile;
            expander.Expanding += (_, _) => tile.SetActive(true);
            expander.Collapsed += (_, _) => tile.SetActive(false);
            _tiles.Add(tile); Children.Add(expander);
        }
    }
    public void SetActive(bool active) { foreach (var tile in _tiles) tile.SetActive(active); }
    public void Dispose() { foreach (var tile in _tiles) tile.Dispose(); _tiles.Clear(); Children.Clear(); }
}

/// <summary>A visible consumer owns its request lease and decoded image views.</summary>
internal sealed class MediaPreviewTile : Grid, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly MediaReference _reference;
    private readonly MessageAnimationClock _clock;
    private readonly Func<bool> _visible;
    private readonly DashboardSettings _style;
    private readonly MediaPreviewSettings _settings;
    private readonly Border _cover;
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _creator = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _summary = new() { FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _creatorLine = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly List<BiliImageView> _images = new();
    private ScrollViewer? _scroll;
    private CancellationTokenSource? _load;
    private MediaDetails? _details;
    private bool _active, _disposed, _loading;
    private int _generation;
    public MediaPreviewTile(MediaReference reference, LiveAssistantViewModel vm, MessageAnimationClock clock, Func<bool> visible,
        MediaPreviewSettings settings, DashboardSettings style, MediaDetails? sample = null)
    {
        _reference = reference; _vm = vm; _clock = clock; _visible = visible; _settings = settings; _style = style; _details = sample;
        MaxWidth = settings.Width; HorizontalAlignment = HorizontalAlignment.Stretch; RowSpacing = 5;
        Background = MessageCard.Brush(style.BackgroundColor, Math.Max(0.8, style.BackgroundOpacity)); Padding = new(8); CornerRadius = new(6);
        for (var i = 0; i < 5; i++) RowDefinitions.Add(new() { Height = GridLength.Auto });
        _cover = new() { Height = settings.CoverHeight, Background = MessageCard.Brush("#343D50"), CornerRadius = new(4),
            Child = new TextBlock { Text = sample is null ? "封面" : "本地封面示例", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 } };
        _title.FontFamily = new(style.FontFamily); _title.FontSize = Math.Min(18, style.FontSize); _title.Text = reference.Id;
        _title.MinHeight = _title.FontSize * 2.5;
        _creatorLine.Children.Add(_creator); _summary.Text = sample is null ? "等待加载资料" : sample.Summary;
        var open = new HyperlinkButton { Content = "打开原页面", Padding = new(0), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += async (_, _) => { try { await Windows.System.Launcher.LaunchUriAsync(reference.Page); } catch { _summary.Text = "无法打开浏览器"; } };
        var controls = new FrameworkElement[] { _cover, _title, _creatorLine, _summary, open };
        for (var i = 0; i < controls.Length; i++) { SetRow(controls[i], i); Children.Add(controls[i]); }
        Loaded += OnLoaded; Unloaded += OnUnloaded; SizeChanged += OnSizeChanged;
        if (sample is not null) ApplyDetails();
    }
    private void OnLoaded(object sender, RoutedEventArgs e) { HookScroll(); CheckVisibility(); }
    private void OnUnloaded(object sender, RoutedEventArgs e) { StopLoading(); UnhookScroll(); }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) { HookScroll(); CheckVisibility(); }
    private void HookScroll()
    {
        if (_scroll is not null) return;
        DependencyObject? node = VisualTreeHelper.GetParent(this);
        while (node is not null && node is not ScrollViewer) node = VisualTreeHelper.GetParent(node);
        if (node is ScrollViewer scroll) { _scroll = scroll; scroll.ViewChanged += OnScrolled; scroll.SizeChanged += OnViewportSize; }
    }
    private void UnhookScroll()
    { if (_scroll is not null) { _scroll.ViewChanged -= OnScrolled; _scroll.SizeChanged -= OnViewportSize; _scroll = null; } }
    private void OnScrolled(object? sender, ScrollViewerViewChangedEventArgs e) => CheckVisibility();
    private void OnViewportSize(object sender, SizeChangedEventArgs e) => CheckVisibility();
    private bool InViewport()
    {
        if (!_active || !_visible() || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return false;
        if (_scroll is null) return true;
        try { var rect = TransformToVisual(_scroll).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight)); return rect.Bottom > 0 && rect.Top < _scroll.ActualHeight && rect.Right > 0 && rect.Left < _scroll.ActualWidth; }
        catch { return false; }
    }
    public void SetActive(bool active) { _active = active; CheckVisibility(); }
    private void CheckVisibility()
    {
        if (_disposed) return;
        if (!InViewport()) { StopLoading(); return; }
        if (_details is not null) { if (_images.Count == 0) ApplyDetails(); foreach (var image in _images) image.Activate(XamlRoot?.RasterizationScale ?? 1); }
        else if (!_loading) LoadAsync();
    }
    private async void LoadAsync()
    {
        _loading = true; var generation = _generation; var owned = new CancellationTokenSource(); _load = owned;
        _summary.Text = "加载中…";
        try
        {
            var details = await _vm.Media.GetForViewAsync(_reference, owned.Token);
            if (_disposed || generation != _generation || !InViewport()) return;
            _details = details; ApplyDetails();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_disposed && generation == _generation) _summary.Text = "资料暂不可用"; }
        finally
        {
            if (ReferenceEquals(_load, owned)) { _load = null; _loading = false; }
            owned.Dispose();
        }
    }
    private void ApplyDetails()
    {
        if (_details is not { } details) return;
        _title.Text = details.Title.Length > 0 ? details.Title : _reference.Id; _creator.Text = details.Creator;
        _summary.Text = details.IsSuccess ? details.Summary : details.Error;
        if (_images.Count > 0 || !InViewport()) return;
        if (details.Cover.Length > 0)
        {
            var image = new BiliImageView(_vm.Images, details.Cover, "封面暂不可用", Math.Min(_settings.Width - 16, Math.Max(64, ActualWidth - 16)), _settings.CoverHeight, false, _clock, InViewport);
            _images.Add(image); _cover.Child = image;
        }
        if (_style.ShowAvatars && details.CreatorAvatar.Length > 0)
        {
            var avatar = new BiliImageView(_vm.Images, details.CreatorAvatar, details.Creator, 22, 22, false, _clock, InViewport, true);
            _images.Add(avatar); _creatorLine.Children.Insert(0, avatar);
        }
        foreach (var image in _images) image.Activate(XamlRoot?.RasterizationScale ?? 1);
    }
    private void StopLoading()
    {
        if (_load is not null) { _generation++; _load.Cancel(); _load = null; _loading = false; }
        foreach (var image in _images) image.Dispose(); _images.Clear();
        if (_cover.Child is BiliImageView) _cover.Child = null;
        while (_creatorLine.Children.Count > 1) _creatorLine.Children.RemoveAt(0);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; StopLoading(); UnhookScroll();
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; SizeChanged -= OnSizeChanged;
    }
}
