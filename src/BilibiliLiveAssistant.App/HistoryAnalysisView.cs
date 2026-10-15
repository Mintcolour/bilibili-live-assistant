using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace BilibiliLiveAssistant.App;

// No timer: redraw only when data, visibility or available size changes.
internal sealed class HistoryAnalysisView : Grid, IDisposable
{
    private readonly Canvas[] _plots = Enumerable.Range(0, 3).Select(_ => new Canvas { Height = 100, Background = MessageCard.Brush("#1B202A") }).ToArray();
    private readonly TextBlock[] _labels = Enumerable.Range(0, 3).Select(_ => new TextBlock { FontSize = 14, FontWeight = new(600) }).ToArray();
    private readonly TextBlock _range = new() { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _selection = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly HistoryMessageList _members;
    private readonly ComboBox _samples = new() { PlaceholderText = "选择采样时间（含大航海总榜）", HorizontalAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<HistoryTrendPoint> _points = [];
    private bool _active;
    public event Action<HistoryTrendPoint>? Selected;
    public HistoryAnalysisView(LiveAssistantViewModel vm, Func<bool> visible)
    {
        _members = new(vm, visible);
        RowSpacing = 10; RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var charts = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < 3; i++)
        {
            var index = i; charts.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var item = new StackPanel { Spacing = 8, Children = { _labels[i], _plots[i] } }; Grid.SetColumn(item, i); charts.Children.Add(item);
            _plots[i].SizeChanged += (_, _) => Draw();
            _plots[i].PointerPressed += (_, e) =>
            {
                var rows = Series(index).Where(p => Value(p, index).HasValue).ToArray(); if (rows.Length == 0) return;
                var x = e.GetCurrentPoint(_plots[index]).Position.X;
                var span = Math.Max(1, (_points[^1].Sample.At - _points[0].Sample.At).TotalSeconds);
                var at = _points[0].Sample.At.AddSeconds(Math.Clamp(x / Math.Max(1, _plots[index].ActualWidth), 0, 1) * span);
                Selected?.Invoke(rows.MinBy(p => Math.Abs((p.Sample.At - at).TotalSeconds))!); e.Handled = true;
            };
        }
        Children.Add(new StackPanel { Spacing = 8, Children = { charts, _range, _samples } });
        _samples.SelectionChanged += (_, _) => { if (_samples.SelectedItem is HistoryTrendPoint p) Selected?.Invoke(p); };
        Grid.SetRow(_selection, 1); Children.Add(_selection); Grid.SetRow(_members, 2); Children.Add(_members);
    }
    private IEnumerable<HistoryTrendPoint> Series(int i) => _points.Where(p => i == 1
        ? p.Sample.Kind == HistorySampleKind.Energy || p.Sample.Kind == HistorySampleKind.Counts && p.Sample.Energy.HasValue
        : p.Sample.Kind == HistorySampleKind.Counts);
    private static long? Value(HistoryTrendPoint p, int i) => i switch { 0 => p.Sample.Guards, 1 => p.Sample.Energy, _ => p.Sample.Fans };
    public void SetPoints(IReadOnlyList<HistoryTrendPoint> points)
    {
        _points = points; _samples.ItemsSource = points.Reverse().ToArray(); _members.SetRows([]); _selection.Text = "点击曲线时间点，查看该次人数或榜单快照"; Draw();
    }
    public void SetActive(bool active) { _active = active; _members.SetActive(active); if (active) Draw(); else foreach (var plot in _plots) plot.Children.Clear(); }
    private void Draw()
    {
        if (!_active) return;
        var names = new[] { "大航海", "高能", "粉丝团" }; var colors = new[] { "#9C9BFF", "#FFC76A", "#F28CB6" };
        for (var i = 0; i < 3; i++)
        {
            var plot = _plots[i]; plot.Children.Clear(); var rows = Series(i).ToArray(); var valid = rows.Where(p => Value(p, i).HasValue).ToArray();
            _labels[i].Text = valid.Length == 0 ? names[i] + " · 暂无数据" : $"{names[i]}  {Value(valid[^1], i):N0}  ({Value(valid[^1], i) - Value(valid[0], i):+0;-0;0})";
            if (valid.Length == 0 || plot.ActualWidth < 1) continue;
            var min = valid.Min(p => Value(p, i)!.Value); var max = valid.Max(p => Value(p, i)!.Value); var spread = Math.Max(1, max - min);
            var duration = Math.Max(1, (_points[^1].Sample.At - _points[0].Sample.At).TotalSeconds);
            HistoryTrendPoint? previous = null; Polyline? line = null;
            foreach (var point in rows)
            {
                if (Value(point, i) is not { } number) { previous = null; line = null; continue; }
                if (previous is null || previous.SessionId != point.SessionId || (point.Sample.At - previous.Sample.At).TotalSeconds > Math.Max(previous.Sample.IntervalSeconds, point.Sample.IntervalSeconds) * 2.5
                    || previous.Sample.Status != "ok" || point.Sample.Status != "ok")
                { line = new() { Stroke = MessageCard.Brush(colors[i]), StrokeThickness = 2 }; plot.Children.Add(line); }
                var position = new Point(5 + (point.Sample.At - _points[0].Sample.At).TotalSeconds / duration * Math.Max(1, plot.ActualWidth - 10), 90 - (number - min) / (double)spread * 80);
                line!.Points.Add(position);
                if (line.Points.Count == 1)
                { var dot = new Ellipse { Width = 4, Height = 4, Fill = line.Stroke }; Canvas.SetLeft(dot, position.X - 2); Canvas.SetTop(dot, position.Y - 2); plot.Children.Add(dot); }
                previous = point;
            }
            ToolTipService.SetToolTip(plot, $"{names[i]} · 最低 {min:N0} / 最高 {max:N0} · 点击查看快照");
        }
        _range.Text = _points.Count == 0 ? "暂无趋势采样。升级前未记录的成员信息无法补录。" :
            $"{_points[0].Sample.At.ToLocalTime():MM-dd HH:mm} — {_points[^1].Sample.At.ToLocalTime():MM-dd HH:mm} · 最近 {_points.Count} / 最多 1200 次采样 · 异常/旧版 {_points.Count(p => p.Sample.Status != "ok")} 次\n空档不补零，离榜不推断离开。旧版缓存采样的新鲜度未知。";
    }
    public void ShowSample(HistoryAnalysisSample sample)
    {
        _selection.Text = $"{sample.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {sample.Kind switch { HistorySampleKind.Energy => "高能榜", HistorySampleKind.Guards => "大航海总榜", _ => "人数" }} · {sample.Status}\n" +
            (sample.Kind == HistorySampleKind.Counts ? $"大航海 {sample.Guards?.ToString() ?? "暂无数据"} · 粉丝团 {sample.Fans?.ToString() ?? "暂无数据"} · 人气 {sample.Popularity?.ToString() ?? "暂无数据"} · 看过 {sample.Watched?.ToString() ?? "暂无数据"}"
                : $"已采集 {sample.Collected} / 接口总人数 {sample.Total?.ToString() ?? "暂无数据"} · 采集上限 {sample.Limit}");
        var rows = sample.Members.Select((m, i) => new HistoryRow(i, sample.At, null, "", "",
            new LiveEvent(LiveEventType.Rank, m.Username, $"第 {m.Rank} 名 · UID {m.Uid} · 贡献 {m.Contribution?.ToString() ?? "暂无数据"} · 陪伴天数 {m.AccompanyDays?.ToString() ?? "暂无数据"}", "#FFFFFF", m.Uid, sample.At.ToLocalTime(), Appearance: m.Appearance), new())).ToArray();
        _members.SetRows(rows);
    }
    public void Clear() { _points = []; _samples.ItemsSource = null; _members.SetRows([]); foreach (var plot in _plots) plot.Children.Clear(); _selection.Text = ""; }
    public void Dispose() { Clear(); _members.Dispose(); }
}
