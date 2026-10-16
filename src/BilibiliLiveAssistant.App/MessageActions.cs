using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class MessageActions : StackPanel, IDisposable
{
    private readonly LiveAssistantViewModel _vm;
    private DashboardRecord? _record;
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _dot = new() { Width = 4, Height = 4,
        Fill = MessageCard.Brush("#FFD878"), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 3, 0) };
    private readonly Button _read = DashboardUi.Button("\uE73E", "标为已读", 28);
    private readonly Button _thanks = DashboardUi.Button("\uE734", "标为已感谢", 28);
    internal MessageActions(DashboardRecord record, LiveAssistantViewModel vm)
    {
        _vm = vm; Orientation = Orientation.Horizontal; Spacing = 1; VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_dot); Children.Add(_read); Children.Add(_thanks);
        DashboardUi.Describe(_dot, "未读"); _read.Click += Read; _thanks.Click += Thanks; Bind(record);
    }
    internal void Bind(DashboardRecord record)
    {
        _record = record; _dot.Visibility = record.IsRead ? Visibility.Collapsed : Visibility.Visible;
        _read.IsEnabled = !record.IsRead; _thanks.IsEnabled = !record.IsThanked;
        if (_thanks.Content is FontIcon icon) icon.Glyph = record.IsThanked ? "\uE735" : "\uE734";
        DashboardUi.Describe(_read, record.IsRead ? "已读" : "标为已读");
        DashboardUi.Describe(_thanks, record.IsThanked ? "已感谢" : "标为已感谢");
        DashboardUi.Describe(this, record.IsThanked ? "已感谢 · 已读" : record.IsRead ? "已读" : "未读");
    }
    internal void ClearBinding() { _record = null; _read.IsEnabled = _thanks.IsEnabled = false; }
    private void Read(object sender, RoutedEventArgs args) { if (_record is { } record) _vm.Dashboard.Mark(record, false); }
    private void Thanks(object sender, RoutedEventArgs args) { if (_record is { } record) _vm.Dashboard.Mark(record, true); }
    public void Dispose()
    {
        ClearBinding(); _read.Click -= Read; _thanks.Click -= Thanks;
        DashboardUi.ReleaseDescriptions(this);
        _read.Content = null; _read.Template = null; _thanks.Content = null; _thanks.Template = null;
        Children.Clear();
    }
}
