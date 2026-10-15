using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace BilibiliLiveAssistant.App;

internal sealed class AnchorSummary : Button, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly MessageAnimationClock _animations;
    private readonly Func<bool> _visible;
    private readonly Grid _avatarHost = new() { Width = 30, Height = 30 };
    private BiliImageView? _avatar;
    private string _avatarKey = "";
    private readonly TextBlock _summary = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = new(600), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _stats = new() { ColumnSpacing = 12 };
    private readonly Canvas _chart = new() { Height = 110, Width = 360 };
    private readonly TextBlock _range = new() { FontSize = 11, Opacity = 0.5 };
    private readonly Flyout _flyout;
    private bool _open;
    private LiveRoomProfile? _detailProfile;
    private string _detailOnline = "";
    private DateTimeOffset _detailSample;
    public AnchorSummary(LiveAssistantViewModel vm, MessageAnimationClock animations, Func<bool> visible)
    {
        _vm = vm; _animations = animations; _visible = visible;
        Padding = new(6, 3, 6, 3); Background = MessageCard.Brush("#242833", 0.4); BorderThickness = new(0); HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var content = new Grid { ColumnSpacing = 7 }; content.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        content.Children.Add(_avatarHost); Grid.SetColumn(_summary, 1); content.Children.Add(_summary); Content = content;
        _flyout = new Flyout { Content = new StackPanel { Width = 360, Spacing = 10, Children = { _title, _subtitle, _stats,
            new TextBlock { Text = "本次监视 · 房间人数（高能榜人数）", FontSize = 12, Opacity = 0.6 }, _chart, _range } } };
        _flyout.Opening += (_, _) => { _open = true; RefreshDetails(); }; _flyout.Closed += (_, _) => { _open = false; _chart.Children.Clear(); };
        Flyout = _flyout; DashboardUi.Describe(this, "主播统计 · 点击展开"); Loaded += (_, _) => _avatar?.Activate(XamlRoot?.RasterizationScale ?? 1);
        Refresh();
    }
    public void Refresh()
    {
        var p = _vm.RoomProfile;
        var key = (p?.AvatarUrl ?? "") + "|" + (p?.Name ?? "");
        if (_avatar is null || _avatarKey != key)
        {
            _avatarKey = key; _avatar?.Dispose(); _avatarHost.Children.Clear();
            _avatar = new(_vm.Images, p?.AvatarUrl ?? "", p?.Name ?? "主播", 30, 30, false, _animations, _visible, true);
            _avatarHost.Children.Add(_avatar);
        }
        if (_visible()) _avatar.Activate(XamlRoot?.RasterizationScale ?? 1);
        _summary.Text = p is null ? "主播统计" : $"⚓ {Number(p.Guards)}   {(!string.IsNullOrEmpty(p.MedalName) ? p.MedalName : "粉丝团")} {Number(p.FansClub)}   ★ {Number(p.EnergyMembers)}";
        var latestSample = _vm.AudienceHistory.LastOrDefault()?.Time ?? default;
        if (_open && _visible() && (_detailProfile != p || _detailOnline != _vm.OnlineText || _detailSample != latestSample)) RefreshDetails();
    }
    private static string Number(long? value) => value is { } n ? n >= 10000 ? $"{n / 10000d:0.#}万" : n.ToString("N0") : "—";
    private void RefreshDetails()
    {
        var p = _vm.RoomProfile; _title.Text = p?.Name is { Length: > 0 } name ? name : "主播信息暂无数据";
        _detailProfile = p; _detailOnline = _vm.OnlineText; _detailSample = _vm.AudienceHistory.LastOrDefault()?.Time ?? default;
        _subtitle.Text = p?.Title is { Length: > 0 } title ? title : _vm.RoomSummary;
        _stats.Children.Clear(); _stats.ColumnDefinitions.Clear();
        var values = new[] { ("大航海", Number(p?.Guards)), (p?.MedalName is { Length: > 0 } medal ? medal : "粉丝团", Number(p?.FansClub)),
            ("高能", Number(p?.EnergyMembers)), ("看过", _vm.LatestOnline?.Extra.GetValueOrDefault("watched") as string ?? "—"), ("点赞", Number(p?.Likes)) };
        for (var i = 0; i < values.Length; i++)
        {
            _stats.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var item = new StackPanel { Spacing = 3, Children = { new TextBlock { Text = values[i].Item1, FontSize = 11, Opacity = 0.5, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = values[i].Item2, FontSize = 13, FontWeight = new(600), TextTrimming = TextTrimming.CharacterEllipsis } } };
            Grid.SetColumn(item, i); _stats.Children.Add(item);
        }
        _chart.Children.Clear(); var samples = _vm.AudienceHistory;
        var valid = samples.Where(s => s.Value.HasValue).ToArray();
        if (valid.Length == 0) { _range.Text = "暂无人数采样"; return; }
        var highest = valid.Max(s => s.Value!.Value); var max = Math.Max(1, highest);
        Polyline? line = null;
        var seconds = Math.Max(1, (samples[^1].Time - samples[0].Time).TotalSeconds);
        foreach (var sample in samples)
        {
            if (sample.Value is not { } count) { line = null; continue; }
            if (line is null) { line = new() { Stroke = MessageCard.Brush("#FC85AA"), StrokeThickness = 2 }; _chart.Children.Add(line); }
            var point = new Point(8 + (sample.Time - samples[0].Time).TotalSeconds / seconds * 344, 100 - count / (double)max * 90);
            line.Points.Add(point);
            var dot = new Ellipse { Width = 4, Height = 4, Fill = line.Stroke }; Canvas.SetLeft(dot, point.X - 2); Canvas.SetTop(dot, point.Y - 2); _chart.Children.Add(dot);
        }
        _range.Text = $"{samples[0].Time:HH:mm} — {samples[^1].Time:HH:mm}  ·  当前 {Number(samples[^1].Value)}  ·  最高 {highest:N0}";
    }
    public void Suspend() { _open = false; _flyout.Hide(); _chart.Children.Clear(); _avatar?.Deactivate(); }
    public void Dispose() { Suspend(); _avatar?.Dispose(); _avatar = null; _avatarHost.Children.Clear(); }
}
