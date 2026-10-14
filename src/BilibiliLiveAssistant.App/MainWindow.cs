using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using BilibiliLiveAssistant.Core;
using WinRT.Interop;

namespace BilibiliLiveAssistant.App;

public sealed class MainWindow : Window
{
    internal static string IconPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
    public event Action? ExitRequested;
    private readonly LiveAssistantViewModel _vm;
    private readonly Frame _frame = new();
    private readonly NavigationView _navigation = new();
    private readonly AppWindow _appWindow;
    private OverlayWindow? _overlay;
    private bool _allowClose;

    public MainWindow(LiveAssistantViewModel vm)
    {
        _vm = vm;
        Title = "B站直播弹幕助手";
        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        _appWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1280, workArea.Width), Math.Min(860, workArea.Height)));
        if (System.IO.File.Exists(IconPath)) _appWindow.SetIcon(IconPath);
        _appWindow.Closing += OnAppWindowClosing;
        SystemBackdrop = new MicaBackdrop();
        ConfigureTitleBar();
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.SetBorderAndTitleBar(true, true);
        }
        _navigation.IsSettingsVisible = false;
        _navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact;
        _navigation.IsPaneOpen = false;
        _navigation.CompactModeThresholdWidth = 760;
        _navigation.ExpandedModeThresholdWidth = 1100;
        _navigation.AlwaysShowHeader = false;
        _navigation.IsPaneToggleButtonVisible = true;
        _navigation.PaneTitle = "B站直播助手";
        _navigation.MenuItems.Add(Item("概览", "dashboard", Symbol.Home));
        _navigation.MenuItems.Add(Item("可视化设置", "display", Symbol.Setting));
        _navigation.MenuItems.Add(Item("音频测试", "audio", Symbol.Microphone));
        _navigation.MenuItems.Add(Item("账号设置", "account", Symbol.Contact));
        _navigation.MenuItems.Add(Item("关于", "about", Symbol.Help));
        _navigation.FooterMenuItems.Add(Item("退出软件", "exit", Symbol.Cancel));
        _navigation.Content = _frame;
        _navigation.SelectionChanged += OnSelectionChanged;
        Content = _navigation;
        _navigation.SelectedItem = _navigation.MenuItems[0];
        Activated += (_, _) => UpdatePageVisibility();
        _appWindow.Changed += (_, _) => UpdatePageVisibility();
    }

    private void ConfigureTitleBar()
    {
        var titleBar = _appWindow.TitleBar;
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        // Keep all page controls in the client area; native caption buttons and
        // the title-bar drag region must never overlap dashboard hit targets.
        titleBar.ExtendsContentIntoTitleBar = false;
        // PreferredHeightOption requires ExtendsContentIntoTitleBar == true.
        // Leave the native title bar at its default height to avoid an invalid
        // state exception while constructing the main window.
        titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 31, 33, 38);
        titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 31, 33, 38);
        titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 242, 242, 242);
        titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 190, 190, 190);
        titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 242, 242, 242);
        titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 190, 190, 190);
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(35, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(55, 255, 255, 255);
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
        if (tag == "exit") { ExitRequested?.Invoke(); return; }
        SuspendPage();
        _frame.Content = tag switch
        {
            "display" => new DisplayPage(_vm, OpenOverlay, IsMainVisible),
            "audio" => new AudioTestPage(),
            "account" => new AccountPage(_vm),
            "about" => new AboutPage(),
            _ => new DashboardPage(_vm, _overlay?.Visible == true, SetOverlayVisible,
                IsMainVisible),
        };
    }

    private bool IsMainVisible() => _appWindow.IsVisible && (_appWindow.Presenter is not OverlappedPresenter presenter || presenter.State != OverlappedPresenterState.Minimized);
    private void SuspendPage()
    {
        (_frame.Content as DashboardPage)?.SuspendVisuals();
        (_frame.Content as DisplayPage)?.SuspendVisuals();
        (_frame.Content as AccountPage)?.SetActive(false);
    }
    private void UpdatePageVisibility()
    {
        if (!IsMainVisible()) { SuspendPage(); return; }
        (_frame.Content as DashboardPage)?.ResumeVisuals(); (_frame.Content as DisplayPage)?.ResumeVisuals();
        (_frame.Content as AccountPage)?.SetActive(true);
    }

    public void OpenOverlay()
    {
        if (_overlay is null)
        {
            _overlay = new OverlayWindow(_vm);
            _overlay.FormClosed += (_, _) => _overlay = null;
        }
        _overlay.ActivateOverlay();
    }

    private void SetOverlayVisible(bool visible)
    {
        if (visible)
        {
            OpenOverlay();
            return;
        }

        _overlay?.HideOverlay();
    }

    public void HideToTray()
    {
        SuspendPage();
        _appWindow.Hide();
    }

    public void ShowMainWindow()
    {
        _appWindow.Show();
        if (_appWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();
        Activate();
    }

    public void AllowClose()
    {
        _allowClose = true;
        _appWindow.Closing -= OnAppWindowClosing;
        _overlay?.Close();
        _overlay = null;
        // Unload audio and dashboard pages before shutting down the dispatcher.
        _frame.Content = null;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        SuspendPage();
        sender.Hide();
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
    public static StackPanel Stack(double spacing = 16) => new() { Spacing = spacing, Padding = new Thickness(32, 18, 40, 32), HorizontalAlignment = HorizontalAlignment.Stretch };
    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 30, FontWeight = new Windows.UI.Text.FontWeight(600), Margin = new Thickness(0, 0, 0, 2) };
    public static TextBlock Subtitle(string text) => new() { Text = text, FontSize = 14, Foreground = new SolidColorBrush(ColorHelper.FromArgb(180, 220, 220, 220)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    public static Border Card(UIElement content) => new() { Child = content, CornerRadius = new CornerRadius(8), Padding = new Thickness(20), Background = new SolidColorBrush(ColorHelper.FromArgb(22, 255, 255, 255)), BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(30, 255, 255, 255)), BorderThickness = new Thickness(1) };
    public static Border Section(string title, string description, UIElement content) => Card(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = title, FontSize = 17, FontWeight = new Windows.UI.Text.FontWeight(600) }, Subtitle(description), content } });
    public static Button Button(string text) => new() { Content = text, Padding = new Thickness(18, 9, 18, 9), HorizontalAlignment = HorizontalAlignment.Left };
}

internal sealed class RoomPage : Page
{
    private readonly LiveAssistantViewModel _vm;
    private readonly Button _action;
    private readonly TextBlock _error;
    private readonly ToggleSwitch _anonymous;

    public RoomPage(LiveAssistantViewModel vm)
    {
        _vm = vm;
        var root = UiFactory.Stack(); root.Children.Add(UiFactory.Heading("直播间"));
        root.Children.Add(UiFactory.Subtitle("设置要监听的直播间，连接后弹幕会实时同步到悬浮窗。"));
        var input = new TextBox { Header = "房间号或直播链接", Text = vm.RoomId > 0 ? vm.RoomId.ToString() : string.Empty, PlaceholderText = "例如 12722549" };
        _anonymous = new ToggleSwitch { Header = "匿名读取（无需登录）", IsOn = vm.IsAnonymousReading, IsEnabled = !vm.IsListening };
        var anonymousTip = UiFactory.Subtitle("匿名模式可读取弹幕、礼物、上舰和醒目留言；不会发送已保存的登录 Cookie。B站可能隐藏用户名或将 UID 置为 0。关闭后使用账号设置中的 Cookie；未填写 SESSDATA 时仍以匿名方式读取。");
        _error = new TextBlock { Foreground = new SolidColorBrush(Colors.IndianRed) };
        _action = UiFactory.Button(vm.IsListening ? "停止监听" : "开始监听");
        _action.Click += async (_, _) =>
        {
            if (vm.IsListening)
            {
                await vm.StopAsync();
                _action.Content = "开始监听";
                return;
            }
            var room = RoomIdParser.Parse(input.Text);
            if (room is null) { _error.Text = "请输入有效的直播间ID或 live.bilibili.com 链接"; return; }
            await vm.SaveAsync(vm.Settings with { RoomId = room.Value, AnonymousReading = _anonymous.IsOn });
            await vm.StartAsync(room.Value);
            _action.Content = vm.IsListening ? "停止监听" : "开始监听";
            _error.Text = vm.ErrorText;
        };
        vm.PropertyChanged += OnVmPropertyChanged;
        Unloaded += OnUnloaded;
        root.Children.Add(UiFactory.Section("连接设置", "输入房间号或直播间链接，选择读取方式。", new StackPanel { Spacing = 12, Children = { input, _anonymous, anonymousTip, _error, _action } }));
        Content = new ScrollViewer { Content = root };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveAssistantViewModel.IsListening))
        {
            _action.Content = _vm.IsListening ? "停止监听" : "开始监听";
            _anonymous.IsEnabled = !_vm.IsListening;
        }
        if (e.PropertyName == nameof(LiveAssistantViewModel.ErrorText))
            _error.Text = _vm.ErrorText;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        Unloaded -= OnUnloaded;
    }
}

internal sealed class AboutPage : Page
{
    public AboutPage()
    {
        var root = UiFactory.Stack();
        root.Children.Add(UiFactory.Heading("关于"));
        root.Children.Add(UiFactory.Subtitle("一个专注于直播弹幕阅读的本地工具。"));
        root.Children.Add(UiFactory.Section("B站直播弹幕助手", "WinUI 3 版本\n\n弹幕数据仅用于本地显示。", new TextBlock { Text = "轻量、安静、可自定义的直播伴侣。", TextWrapping = TextWrapping.Wrap }));
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}

internal sealed class AudioTestPage : Page
{
    private readonly AudioTestService _audio = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly ComboBox _microphones = new() { Header = "麦克风 / 输入设备", MinWidth = 320 };
    private readonly ComboBox _outputs = new() { Header = "扬声器 / 输出设备", MinWidth = 320 };
    private readonly Button _startRecording = UiFactory.Button("开始录音");
    private readonly Button _stopRecording = UiFactory.Button("停止并保存");
    private readonly Button _playRecording = UiFactory.Button("回放录音");
    private readonly Button _stopPlayback = UiFactory.Button("停止回放");
    private readonly TextBlock _status = new() { Text = "选择设备后录一段声音，再回放确认音量。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _recordingFile = new() { Text = "尚无录音", TextWrapping = TextWrapping.Wrap };
    private readonly AudioLevelGraph _inputGraph = new("麦克风输入", "录音期间显示麦克风实时峰值电平");
    private readonly AudioLevelGraph _outputGraph = new("回放输出", "回放期间显示送往所选输出设备的峰值");
    private readonly DispatcherQueueTimer _levelTimer;
    private readonly object _levelGate = new();
    private float _pendingInputPeak;
    private float _pendingOutputPeak;
    private bool _unloaded;

    public AudioTestPage()
    {
        _levelTimer = _dispatcher.CreateTimer();
        _levelTimer.Interval = TimeSpan.FromMilliseconds(100);
        _levelTimer.Tick += OnLevelTick;
        var root = UiFactory.Stack();
        root.Children.Add(UiFactory.Heading("音频测试"));
        root.Children.Add(UiFactory.Subtitle("检查直播麦克风的拾音大小：选择输入和监听设备，录音后回放并观察电平。"));

        LoadDevices();
        _microphones.SelectionChanged += (_, _) => UpdateButtons();
        _outputs.SelectionChanged += (_, _) => UpdateButtons();

        var devicePanel = new StackPanel { Spacing = 12, Children = { _microphones, _outputs } };
        root.Children.Add(UiFactory.Section("设备", "选定要测试的麦克风和回放设备。", devicePanel));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(_startRecording);
        actions.Children.Add(_stopRecording);
        actions.Children.Add(_playRecording);
        actions.Children.Add(_stopPlayback);
        _startRecording.Click += OnStartRecording;
        _stopRecording.Click += (_, _) => RunAudioAction(_audio.StopRecording);
        _playRecording.Click += OnStartPlayback;
        _stopPlayback.Click += (_, _) => RunAudioAction(_audio.StopPlayback);

        var recordingState = new StackPanel { Spacing = 8, Children = { actions, _status, _recordingFile } };
        root.Children.Add(UiFactory.Section("录制与回放", "录音保存在本机临时目录，不会上传。", recordingState));
        root.Children.Add(UiFactory.Section("麦克风电平", "讲话时观察柱状图和 dB 数值；峰值约 -12 至 -6 dB 通常有余量，接近 0 dB 可能失真。", _inputGraph));
        root.Children.Add(UiFactory.Section("回放电平", "回放时确认声音是否清晰、是否过小或出现削波。系统输入增益请在 Windows 麦克风属性或直播软件中调整。", _outputGraph));

        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _audio.InputLevelChanged += OnInputLevelChanged;
        _audio.OutputLevelChanged += OnOutputLevelChanged;
        _audio.StatusChanged += OnStatusChanged;
        _audio.RecordingCompleted += OnRecordingCompleted;
        Unloaded += OnUnloaded;
        UpdateButtons();
    }

    private void LoadDevices()
    {
        try
        {
            foreach (var device in AudioTestService.GetCaptureDevices())
                _microphones.Items.Add(device);
            foreach (var device in AudioTestService.GetPlaybackDevices())
                _outputs.Items.Add(device);
            if (_microphones.Items.Count > 0) _microphones.SelectedIndex = 0;
            if (_outputs.Items.Count > 0) _outputs.SelectedIndex = 0;

            if (_microphones.Items.Count == 0 || _outputs.Items.Count == 0)
            {
                var missing = new List<string>();
                if (_microphones.Items.Count == 0) missing.Add("麦克风");
                if (_outputs.Items.Count == 0) missing.Add("输出设备");
                _status.Text = $"未检测到可用的{string.Join("和", missing)}，请连接设备后重新打开此页面。";
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"枚举音频设备失败：{ex.Message}";
        }
    }

    private void OnStartRecording(object sender, RoutedEventArgs e)
    {
        if (_microphones.SelectedItem is not AudioDeviceOption device) return;
        RunAudioAction(() => _audio.StartRecording(device.DeviceNumber));
        UpdateButtons();
    }

    private void OnStartPlayback(object sender, RoutedEventArgs e)
    {
        if (_outputs.SelectedItem is not AudioDeviceOption device) return;
        RunAudioAction(() => _audio.StartPlayback(device.DeviceNumber));
        UpdateButtons();
    }

    private void RunAudioAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { _status.Text = $"音频操作失败：{ex.Message}"; }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var recording = _audio.IsRecording;
        var playing = _audio.IsPlaying;
        _microphones.IsEnabled = !recording;
        _outputs.IsEnabled = !playing;
        _startRecording.IsEnabled = !recording && _microphones.SelectedItem is AudioDeviceOption;
        _stopRecording.IsEnabled = recording;
        _playRecording.IsEnabled = !recording && !playing && _audio.HasRecording && _outputs.SelectedItem is AudioDeviceOption;
        _stopPlayback.IsEnabled = playing;
        if (recording || playing)
        {
            if (!_levelTimer.IsRunning) _levelTimer.Start();
        }
        else if (_levelTimer.IsRunning)
        {
            _levelTimer.Stop();
            lock (_levelGate) { _pendingInputPeak = 0; _pendingOutputPeak = 0; }
            _inputGraph.SetLevel(0);
            _outputGraph.SetLevel(0);
        }
    }

    private void OnInputLevelChanged(float level)
    {
        lock (_levelGate) _pendingInputPeak = Math.Max(_pendingInputPeak, level);
    }
    private void OnOutputLevelChanged(float level)
    {
        lock (_levelGate) _pendingOutputPeak = Math.Max(_pendingOutputPeak, level);
    }
    private void OnLevelTick(DispatcherQueueTimer sender, object args)
    {
        float input, output;
        lock (_levelGate)
        {
            input = _pendingInputPeak; output = _pendingOutputPeak;
            _pendingInputPeak = 0; _pendingOutputPeak = 0;
        }
        _inputGraph.SetLevel(input);
        _outputGraph.SetLevel(output);
    }
    private void OnStatusChanged(string value) => Enqueue(() =>
    {
        _status.Text = value;
        UpdateButtons();
    });

    private void OnRecordingCompleted(string? path, string? error) => Enqueue(() =>
    {
        if (path is not null && error is null)
            _recordingFile.Text = $"录音文件：{path}";
        UpdateButtons();
    });

    private void Enqueue(Action update)
    {
        if (_unloaded) return;
        if (_dispatcher.HasThreadAccess) update();
        else _dispatcher.TryEnqueue(() => { if (!_unloaded) update(); });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _unloaded = true;
        _levelTimer.Stop();
        _levelTimer.Tick -= OnLevelTick;
        Unloaded -= OnUnloaded;
        _audio.InputLevelChanged -= OnInputLevelChanged;
        _audio.OutputLevelChanged -= OnOutputLevelChanged;
        _audio.StatusChanged -= OnStatusChanged;
        _audio.RecordingCompleted -= OnRecordingCompleted;
        _audio.Dispose();
    }
}

internal sealed class AudioLevelGraph : StackPanel
{
    private const int SampleCount = 36;
    private readonly Border[] _bars = new Border[SampleCount];
    private readonly TextBlock _reading = new();
    private readonly TextBlock _description;
    private readonly float[] _history = new float[SampleCount];
    private readonly SolidColorBrush _normal = new(ColorHelper.FromArgb(255, 82, 196, 130));
    private readonly SolidColorBrush _warning = new(ColorHelper.FromArgb(255, 255, 193, 7));
    private readonly SolidColorBrush _clipping = new(ColorHelper.FromArgb(255, 244, 67, 54));

    public AudioLevelGraph(string title, string description)
    {
        Spacing = 8;
        _description = new TextBlock
        {
            Text = description,
            FontSize = 13,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(180, 220, 220, 220)),
            TextWrapping = TextWrapping.Wrap,
        };
        _reading.Text = "电平：-∞ dB · 0%";
        _reading.FontSize = 14;
        Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = new Windows.UI.Text.FontWeight(600) });
        Children.Add(_description);
        Children.Add(_reading);

        var graph = new Grid
        {
            Height = 94,
            ColumnSpacing = 3,
            Background = new SolidColorBrush(ColorHelper.FromArgb(18, 255, 255, 255)),
        };
        for (var i = 0; i < SampleCount; i++)
        {
            graph.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(2),
                Background = _normal,
                Margin = new Thickness(0, 0, 0, 4),
            };
            Grid.SetColumn(bar, i);
            graph.Children.Add(bar);
            _bars[i] = bar;
        }

        Children.Add(graph);
    }

    public void SetLevel(float level)
    {
        level = Math.Clamp(level, 0, 1);
        Array.Copy(_history, 1, _history, 0, SampleCount - 1);
        _history[^1] = level;
        for (var i = 0; i < SampleCount; i++)
        {
            var value = _history[i];
            var height = Math.Max(2, value * 82);
            if (_bars[i].Height != height) _bars[i].Height = height;
            var brush = value >= 0.9f ? _clipping : value >= 0.7f ? _warning : _normal;
            if (!ReferenceEquals(_bars[i].Background, brush)) _bars[i].Background = brush;
        }

        var decibels = level <= 0.0001f ? "-∞" : (20 * Math.Log10(level)).ToString("0.0");
        var reading = $"电平：{decibels} dB · {level * 100:0}%";
        if (_reading.Text != reading) _reading.Text = reading;
    }
}
