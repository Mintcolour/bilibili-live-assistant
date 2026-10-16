using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class TradeFilterEditor : StackPanel
{
    internal static string SortLabel(MessageSort sort) => sort switch
    {
        MessageSort.Oldest => "时间最早", MessageSort.AmountDescending => "总金额从高到低",
        MessageSort.AmountAscending => "总金额从低到高", MessageSort.QuantityDescending => "数量从多到少", _ => "时间最新",
    };
    internal TradeFilterEditor(TradeFilter current, bool gifts, bool sc, Action<TradeFilter> apply,
        bool? unread = null, Action<bool>? setUnread = null, IEnumerable<string>? giftNames = null)
    {
        Width = 300; Spacing = 10; RequestedTheme = ElementTheme.Dark;
        var category = new ComboBox { Header = "礼物类型", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var pair in new[] { (GiftCategory.All, "全部礼物 / 上舰"), (GiftCategory.Paid, "普通付费礼物"), (GiftCategory.Free, "免费礼物"), (GiftCategory.Guard, "上舰") })
            category.Items.Add(new ComboBoxItem { Content = pair.Item2, Tag = pair.Item1 });
        category.SelectedIndex = (int)current.Category;
        var name = new ComboBox { Header = "礼物名称（可直接输入完整名称）", IsEditable = true, Text = current.GiftName, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "留空显示全部" };
        foreach (var gift in (giftNames ?? []).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(512)) name.Items.Add(gift);
        if (gifts) { Children.Add(category); Children.Add(name); }
        var min = new NumberBox { Header = "SC 最低金额（元）", Value = current.MinimumMilliYuan is { } low ? low / 1000d : double.NaN, Minimum = 0, Maximum = 1_000_000_000_000, PlaceholderText = "不限" };
        var max = new NumberBox { Header = "SC 最高金额（元）", Value = current.MaximumMilliYuan is { } high ? high / 1000d : double.NaN, Minimum = 0, Maximum = 1_000_000_000_000, PlaceholderText = "不限" };
        if (sc) { Children.Add(min); Children.Add(max); }
        var sort = new ComboBox { Header = "排序", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in Enum.GetValues<MessageSort>())
        {
            if (!gifts && value == MessageSort.QuantityDescending) continue;
            var item = new ComboBoxItem { Content = SortLabel(value), Tag = value }; sort.Items.Add(item);
            if (value == current.Sort) sort.SelectedItem = item;
        }
        if (sort.SelectedIndex < 0) sort.SelectedIndex = 0;
        Children.Add(sort);
        var unreadBox = new CheckBox { Content = "仅显示未读", IsChecked = unread == true };
        if (unread is not null) Children.Add(unreadBox);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = MessageCard.Brush("#FF9999") }; Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = UiFactory.Button("应用"); var reset = UiFactory.Button("重置"); buttons.Children.Add(save); buttons.Children.Add(reset); Children.Add(buttons);
        save.Click += (_, _) =>
        {
            static long? Milli(double value) => double.IsFinite(value) ? (long)Math.Round(value * 1000, MidpointRounding.AwayFromZero) : null;
            var result = new TradeFilter { Category = gifts && category.SelectedItem is ComboBoxItem c ? (GiftCategory)c.Tag : GiftCategory.All,
                GiftName = gifts ? name.Text : "", MinimumMilliYuan = sc ? Milli(min.Value) : null, MaximumMilliYuan = sc ? Milli(max.Value) : null,
                Sort = sort.SelectedItem is ComboBoxItem s ? (MessageSort)s.Tag : MessageSort.Newest, OnlyUnread = unread is not null && unreadBox.IsChecked == true }.Normalize();
            if (result.MinimumMilliYuan is { } a && result.MaximumMilliYuan is { } b && a > b) { error.Text = "最低金额不能大于最高金额"; return; }
            setUnread?.Invoke(unreadBox.IsChecked == true); apply(result);
        };
        reset.Click += (_, _) => { setUnread?.Invoke(false); apply(new()); };
        Children.Add(new TextBlock { Text = "未知金额排最后 · 同金额按时间最新\n筛选不改变统计与浮窗播报", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
    }
}
