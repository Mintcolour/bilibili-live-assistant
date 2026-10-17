using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

// Queries existing local observations only while explicitly opened. Never adds
// collection requests or a polling timer to the overview.
internal sealed class DashboardChangesPanel : Grid, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private readonly ComboBox _period = new() { Items = { "今天", "最近 7 天", "最近 30 天" }, SelectedIndex = 0, Width = 130 };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, MaxHeight = 340 };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _request;
    private bool _active, _disposed;

    public DashboardChangesPanel(LiveAssistantViewModel vm)
    {
        _vm = vm; Width = 540; RowSpacing = 10;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        var refresh = new Button { Content = "刷新", Padding = new(10, 4, 10, 4) };
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = {
            new TextBlock { Text = "变化动态", FontSize = 18, FontWeight = new(600), VerticalAlignment = VerticalAlignment.Center }, _period, refresh } });
        Grid.SetRow(_list, 1); Children.Add(_list); Grid.SetRow(_status, 2); Children.Add(_status);
        refresh.Click += async (_, _) => await LoadAsync();
        _period.SelectionChanged += async (_, _) => { if (_active) await LoadAsync(); };
    }
    internal async Task OpenAsync() { if (_disposed) return; _active = true; await LoadAsync(); }
    private async Task LoadAsync()
    {
        if (!_active || _disposed) return;
        _request?.Cancel(); _request?.Dispose(); var run = _request = new();
        var room = _vm.RoomId; var profile = _vm.RoomProfile;
        _list.Items.Clear(); _status.Text = "读取本地观察记录…";
        if (room <= 0) { _status.Text = "选择直播间后查看本地记录的变化。"; return; }
        var today = DateTime.Today; var days = _period.SelectedIndex switch { 1 => 6, 2 => 29, _ => 0 };
        var filter = new HistoryFilter(new(profile?.Uid is > 0 ? profile.Uid : null, room, profile?.Name ?? "", profile?.AvatarUrl ?? ""),
            today.AddDays(-days).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));
        try
        {
            var changes = await _vm.History.ChangesAsync(filter, null, run.Token);
            if (_disposed || !_active || run.IsCancellationRequested || !ReferenceEquals(_request, run) || room != _vm.RoomId) return;
            foreach (var c in changes.Take(100))
            {
                var source = c.Source switch { "energy" => "高能榜", "guards" => "大航海", "guard_event" => "上舰", _ => "粉丝牌观察" };
                static string Value(long? value) => value?.ToString("N0") ?? "—";
                var before = c.Before;
                var detail = before is null ? "首次观察" : $"名次 {Value(before.Rank)} → {Value(c.After.Rank)}  ·  贡献 {Value(before.Contribution)} → {Value(c.After.Contribution)}";
                _list.Items.Add(new Border { Padding = new(10), CornerRadius = new(8), Background = MessageCard.Brush("#252B36"), Margin = new(0, 0, 0, 6), Child =
                    new StackPanel { Spacing = 4, Children = {
                        new TextBlock { Text = $"{c.At.ToLocalTime():MM-dd HH:mm}  ·  {source}  ·  {c.After.Name}", FontWeight = new(600), TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = detail, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = $"{c.Kind} · UID {c.After.Uid}", FontSize = 11, Opacity = 0.5 } } } });
            }
            _status.Text = changes.Count == 0 ? "所选时段暂无变化记录；只记录启用监视期间实际观察到的数据。" : "最近 100 项 · 首次观察不代表刚加入，离榜不代表退出。完整记录可在历史页筛选查看。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!run.IsCancellationRequested && !_disposed) _status.Text = "读取失败，请稍后刷新。"; }
    }
    internal void Suspend() { _active = false; _request?.Cancel(); _list.Items.Clear(); }
    public void Dispose() { _disposed = true; Suspend(); _request?.Dispose(); }
}
