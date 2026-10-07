using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

public sealed class MainWindow : Window
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Frame _frame = new();
    private readonly NavigationView _navigation = new();
    private OverlayWindow? _overlay;

    public MainWindow(LiveAssistantViewModel vm)
    {
        _vm = vm;
        Title = "B站直播弹幕助手";
        _navigation.IsSettingsVisible = false;
        _navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        _navigation.AlwaysShowHeader = true;
        _navigation.Header = new TextBlock { Text = "直播控制台", FontSize = 20, FontWeight = new Windows.UI.Text.FontWeight(600) };
        _navigation.MenuItems.Add(Item("概览", "dashboard", Symbol.Home));
        _navigation.MenuItems.Add(Item("直播间", "room", Symbol.Video));
        _navigation.MenuItems.Add(Item("显示设置", "display", Symbol.Setting));
        _navigation.MenuItems.Add(Item("账号设置", "account", Symbol.Contact));
        _navigation.MenuItems.Add(Item("关于", "about", Symbol.Help));
        _navigation.Content = _frame;
        _navigation.SelectionChanged += OnSelectionChanged;
        Content = _navigation;
        _navigation.SelectedItem = _navigation.MenuItems[0];
        Closed += async (_, _) => await _vm.DisposeAsync();
    }

    private static NavigationViewItem Item(string label, string tag, Symbol symbol) => new()
    {
        Content = label,
        Tag = tag,
        Icon = new SymbolIcon(symbol),
    };

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();
        _frame.Content = tag switch
        {
            "room" => new RoomPage(_vm),
            "display" => new DisplayPage(_vm),
            "account" => new AccountPage(_vm),
            "about" => new AboutPage(),
            _ => new DashboardPage(_vm, OpenOverlay),
        };
    }

    public void OpenOverlay()
    {
        _overlay ??= new OverlayWindow(_vm);
        _overlay.ActivateOverlay();
    }

    public async Task ShowFirstRunDialogAsync()
    {
        var room = new TextBox { Header = "直播间ID或直播链接", PlaceholderText = "例如 12722549", MinWidth = 320 };
        var dialog = new ContentDialog
        {
            Title = "欢迎使用 B站直播弹幕助手",
            Content = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = "先填写一个直播间，Cookie 可以稍后在账号设置中补充。", TextWrapping = TextWrapping.Wrap }, room } },
            PrimaryButtonText = "开始使用",
            CloseButtonText = "稍后设置",
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var roomId = RoomIdParser.Parse(room.Text);
        if (roomId is null) return;
        await _vm.StartAsync(roomId.Value);
    }
}

internal static class UiFactory
{
    public static StackPanel Stack(double spacing = 12) => new() { Spacing = spacing, Padding = new Thickness(24) };
    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 26, FontWeight = new Windows.UI.Text.FontWeight(600), Margin = new Thickness(0, 0, 0, 8) };
    public static Border Card(UIElement content) => new() { Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Background = new SolidColorBrush(ColorHelper.FromArgb(24, 0, 161, 214)) };
    public static Button Button(string text) => new() { Content = text, Padding = new Thickness(18, 8, 18, 8), HorizontalAlignment = HorizontalAlignment.Left };
}

internal sealed class DashboardPage : Page
{
    private readonly LiveAssistantViewModel _vm;
    private readonly TextBlock _status = new();
    private readonly TextBlock _online = new();
    private readonly ListView _events = new() { Height = 420 };
    private readonly Action _showOverlay;

    public DashboardPage(LiveAssistantViewModel vm, Action showOverlay)
    {
        _vm = vm; _showOverlay = showOverlay;
        var root = UiFactory.Stack();
        root.Children.Add(UiFactory.Heading("概览"));
        _status.Text = vm.Status; _status.FontSize = 16;
        _online.Text = $"人气：{vm.OnlineText}"; _online.FontSize = 16;
        var state = new StackPanel { Spacing = 6 };
        state.Children.Add(_status); state.Children.Add(_online); state.Children.Add(new TextBlock { Text = vm.RoomId > 0 ? $"直播间：{vm.RoomId}" : "尚未设置直播间" });
        root.Children.Add(UiFactory.Card(state));
        var overlay = UiFactory.Button("打开弹幕悬浮窗"); overlay.Click += (_, _) => _showOverlay();
        root.Children.Add(overlay);
        _events.Header = new TextBlock { Text = "最近事件", FontSize = 18, Margin = new Thickness(0, 12, 0, 4) };
        root.Children.Add(_events);
        Content = new ScrollViewer { Content = root };
        vm.PropertyChanged += OnPropertyChanged;
        vm.Events.CollectionChanged += OnEventsChanged;
        RefreshEvents();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveAssistantViewModel.Status)) _status.Text = _vm.Status;
        if (e.PropertyName == nameof(LiveAssistantViewModel.OnlineText)) _online.Text = $"人气：{_vm.OnlineText}";
    }
    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshEvents();
    private void RefreshEvents()
    {
        _events.Items.Clear();
        foreach (var item in _vm.Events.Take(100))
            _events.Items.Add(new TextBlock { Text = $"[{item.Type}] {item.Username} {item.Content}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
    }
}

internal sealed class RoomPage : Page
{
    public RoomPage(LiveAssistantViewModel vm)
    {
        var root = UiFactory.Stack(); root.Children.Add(UiFactory.Heading("直播间"));
        var input = new TextBox { Header = "房间号或直播链接", Text = vm.RoomId > 0 ? vm.RoomId.ToString() : string.Empty, PlaceholderText = "例如 12722549" };
        var error = new TextBlock { Foreground = new SolidColorBrush(Colors.IndianRed) };
        var action = UiFactory.Button("开始监听");
        action.Click += async (_, _) =>
        {
            if (vm.IsConnected)
            {
                await vm.StopAsync();
                action.Content = "开始监听";
                return;
            }
            var room = RoomIdParser.Parse(input.Text);
            if (room is null) { error.Text = "请输入有效的直播间ID或 live.bilibili.com 链接"; return; }
            await vm.StartAsync(room.Value);
            action.Content = "停止监听";
        };
        root.Children.Add(input); root.Children.Add(error); root.Children.Add(action);
        Content = new ScrollViewer { Content = root };
    }
}

internal sealed class DisplayPage : Page
{
    public DisplayPage(LiveAssistantViewModel vm)
    {
        var root = UiFactory.Stack(); root.Children.Add(UiFactory.Heading("显示设置"));
        var settings = vm.Settings.Display;
        var font = new NumberBox { Header = "字体大小", Value = settings.FontSize, Minimum = 12, Maximum = 72, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var speed = new NumberBox { Header = "滚动速度（px/s）", Value = settings.ScrollSpeed, Minimum = 30, Maximum = 500, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var opacity = new Slider { Header = "透明度", Value = settings.Opacity, Minimum = 0.1, Maximum = 1, StepFrequency = 0.05 };
        var enter = new ToggleSwitch { Header = "显示进入/关注/分享", IsOn = settings.ShowEnter };
        var gift = new ToggleSwitch { Header = "显示礼物和上舰", IsOn = settings.ShowGift };
        var sc = new ToggleSwitch { Header = "显示醒目留言（SC）", IsOn = settings.ShowSuperChat };
        var online = new ToggleSwitch { Header = "显示人气和高能榜", IsOn = settings.ShowOnline };
        var save = UiFactory.Button("保存显示设置");
        save.Click += async (_, _) => await vm.SaveAsync(vm.Settings with { Display = settings with { FontSize = (int)font.Value, ScrollSpeed = (int)speed.Value, Opacity = opacity.Value, ShowEnter = enter.IsOn, ShowGift = gift.IsOn, ShowSuperChat = sc.IsOn, ShowOnline = online.IsOn } });
        root.Children.Add(font); root.Children.Add(speed); root.Children.Add(opacity); root.Children.Add(enter); root.Children.Add(gift); root.Children.Add(sc); root.Children.Add(online); root.Children.Add(save);
        Content = new ScrollViewer { Content = root };
    }
}

internal sealed class AccountPage : Page
{
    public AccountPage(LiveAssistantViewModel vm)
    {
        var root = UiFactory.Stack(); root.Children.Add(UiFactory.Heading("账号设置"));
        var sess = new PasswordBox { Header = "SESSDATA", Password = vm.Settings.Cookies.Sessdata };
        var buvid = new TextBox { Header = "buvid3", Text = vm.Settings.Cookies.Buvid3 };
        var jct = new PasswordBox { Header = "bili_jct", Password = vm.Settings.Cookies.BiliJct };
        var tip = new TextBlock { Text = "Cookie 仅用于访问 B站接口，请勿分享 settings.json。", Foreground = new SolidColorBrush(Colors.Gray), TextWrapping = TextWrapping.Wrap };
        var save = UiFactory.Button("保存账号设置");
        save.Click += async (_, _) => await vm.SaveAsync(vm.Settings with { Cookies = new CookieOptions(buvid.Text.Trim(), sess.Password.Trim(), jct.Password.Trim()) });
        root.Children.Add(sess); root.Children.Add(buvid); root.Children.Add(jct); root.Children.Add(tip); root.Children.Add(save);
        Content = new ScrollViewer { Content = root };
    }
}

internal sealed class AboutPage : Page
{
    public AboutPage() => Content = new StackPanel { Spacing = 12, Padding = new Thickness(24), Children = { UiFactory.Heading("关于"), new TextBlock { Text = "B站直播弹幕助手\nWinUI 3 版本\n\n弹幕数据仅用于本地显示。", TextWrapping = TextWrapping.Wrap } } };
}
