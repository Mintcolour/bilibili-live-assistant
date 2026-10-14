using BilibiliLiveAssistant.Core;
using BilibiliLiveAssistant.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal sealed class AccountPage : Page
{
    private readonly LiveAssistantViewModel _vm;
    private readonly BrowserAccountController _sync;
    private readonly BrowserHostInstaller _installer = new();
    private readonly MessageAnimationClock _clock;
    private readonly Border _avatar = new();
    private BiliImageView? _image;
    private string _avatarUrl = "";
    private readonly TextBlock _name = new() { FontSize = 22 }, _state = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap },
        _source = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap }, _feedback = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Button _connect = UiFactory.Button("连接浏览器"), _disconnect = UiFactory.Button("断开同步"), _guide = UiFactory.Button("在浏览器同步"), _refresh = UiFactory.Button("刷新本机状态／重试验证");
    private readonly ComboBox _policy = new() { Header = "浏览器明确退出后", ItemsSource = new[] { "继续匿名读取", "停止直播监视" }, HorizontalAlignment = HorizontalAlignment.Left };
    private bool _active = true, _disposed, _loading, _busy;
    public AccountPage(LiveAssistantViewModel vm)
    {
        _vm = vm; _sync = vm.AccountSync; _clock = new(() => _active && !_disposed);
        var root = UiFactory.Stack(); root.Children.Add(UiFactory.Heading("账号设置"));
        root.Children.Add(UiFactory.Subtitle("使用 Chrome／Edge 已登录的 B站账号，仅同步到这台电脑。浏览器关闭不等于账号退出。"));
        var info = new StackPanel { Spacing = 8, Children = { _name, _state, _source } };
        var account = new Grid { ColumnSpacing = 16 }; account.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); account.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        account.Children.Add(_avatar); Grid.SetColumn(info, 1); account.Children.Add(info); root.Children.Add(UiFactory.Card(account));
        var actions = new StackPanel { Spacing = 8, Children = { _connect, _guide, _refresh, _disconnect, _policy, _feedback } }; root.Children.Add(actions);
        var repair = UiFactory.Button("修复已绑定浏览器的连接组件"); repair.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_sync.State.Binding is not { } binding) { _feedback.Text = "请通过连接浏览器安装组件。"; return; }
            await Task.Run(() => _installer.Install(binding.Browser, Path.Combine(AppContext.BaseDirectory, "BrowserHost")));
            _feedback.Text = "组件已修复，请在扩展点击立即同步。";
        }); actions.Children.Insert(3, repair);
        _connect.Click += async (_, _) => await RunAsync(ConnectAsync);
        _guide.Click += async (_, _) => await ShowGuideAsync();
        _refresh.Click += async (_, _) => await RunAsync(async () => { await _sync.RefreshAsync(true); _feedback.Text = "已读取本机状态；获取最新浏览器状态请在扩展点击立即同步。"; });
        _disconnect.Click += async (_, _) =>
        {
            if (await ConfirmAsync("断开本机同步？", "撤销绑定并清除本机账号凭据，浏览器里的 B站登录不会改变。")) await RunAsync(_sync.DisconnectAsync);
        };
        _policy.SelectionChanged += async (_, _) =>
        { if (!_loading && _policy.SelectedIndex >= 0) await RunAsync(() => vm.SaveAsync(vm.Settings with { BrowserSync = vm.Settings.BrowserSync with { OnLogout = (AccountLossPolicy)_policy.SelectedIndex } })); };
        var advanced = new StackPanel { Spacing = 10 };
        var sess = new PasswordBox { Header = "SESSDATA" }; var buvid = new TextBox { Header = "buvid3" }; var jct = new PasswordBox { Header = "bili_jct" };
        advanced.Children.Add(new TextBlock { Text = "手动导入只写入当前 Windows 用户的加密库。已绑定浏览器时请先断开，字段不会自动填回界面。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        foreach (var control in new UIElement[] { sess, buvid, jct }) advanced.Children.Add(control);
        var import = UiFactory.Button("手动导入并验证"); import.Click += async (_, _) => await RunAsync(async () =>
        { await _sync.ImportAsync(new(buvid.Text.Trim(), sess.Password.Trim(), jct.Password.Trim())); sess.Password = jct.Password = ""; buvid.Text = ""; }); advanced.Children.Add(import);
        var uninstall = UiFactory.Button("卸载本机连接组件"); uninstall.Click += async (_, _) => await UninstallAsync(); advanced.Children.Add(uninstall);
        var recover = UiFactory.Button("恢复不可读取的账号存储"); recover.Click += async (_, _) =>
        {
            if (await ConfirmAsync("恢复账号存储？", "仅在密文损坏或无法解密时使用。保留原密文为备份，撤销原绑定，再重新连接浏览器。若此前提示旧设置迁移失败，恢复后请重启助手以重试迁移。"))
                await RunAsync(async () => { await _sync.RecoverAsync(); _feedback.Text = "原密文已保留，请重新连接浏览器；旧设置迁移失败时先重启助手。"; });
        }; advanced.Children.Add(recover);
        var storage = UiFactory.Button("打开账号存储目录"); storage.Click += async (_, _) => await RunAsync(async () =>
            { await Windows.System.Launcher.LaunchFolderAsync(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(new BrowserAuthVault().DirectoryPath)); }); advanced.Children.Add(storage);
        root.Children.Add(new Expander { Header = "高级设置", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch });
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _sync.Changed += Refresh; vm.PropertyChanged += OnVmChanged;
        Loaded += (_, _) => { _active = true; Refresh(); };
        Unloaded += (_, _) => { _disposed = true; _sync.Changed -= Refresh; vm.PropertyChanged -= OnVmChanged; _image?.Dispose(); _clock.Dispose(); _sync.ClosePairing(); };
        Refresh();
    }
    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(LiveAssistantViewModel.ConnectionModeText) or nameof(LiveAssistantViewModel.Settings)) Refresh(); }
    private void Refresh()
    {
        if (_disposed || !_active) return;
        var state = _sync.State; _name.Text = state.Account?.Name ?? "未登录账号";
        var label = state.AccountState switch { AccountState.LoggedIn => "已验证登录", AccountState.Pending => "同步中，等待验证", AccountState.Expired => "登录已失效，请在浏览器重新登录",
            AccountState.Unavailable => "验证暂不可用，请重试", _ => state.Binding is null ? "未连接浏览器" : "请在此浏览器登录 B站并同步" };
        _state.Text = label + "\n" + _vm.ConnectionModeText;
        _source.Text = (state.Binding is { } binding ? $"{binding.Browser} · {binding.Name} · 自动跟随{(binding.Follow ? "已开启" : "已关闭")}" : state.Source == CredentialSource.Manual ? "手动导入" : "无账号来源")
            + (state.CapturedAt is { } time ? $"\n最近同步 {time.LocalDateTime:MM-dd HH:mm:ss}" : "");
        if (state.Binding is { } connected && !_installer.IsInstalled(connected.Browser)) _source.Text += "\n连接组件需修复：点击修复连接组件。";
        if (_sync.Error.Length > 0) _feedback.Text = _sync.Error;
        _connect.Content = state.Binding is null ? "连接浏览器" : "更换来源";
        _disconnect.IsEnabled = !_busy && (state.Binding is not null || state.Source != CredentialSource.None);
        _connect.IsEnabled = _guide.IsEnabled = _refresh.IsEnabled = !_busy;
        _loading = true; _policy.SelectedIndex = (int)_vm.Settings.BrowserSync.OnLogout; _loading = false;
        var url = state.Account?.Avatar ?? "";
        if (_image is null || _avatarUrl != url)
        {
            _image?.Dispose(); _avatarUrl = url; _image = new(_vm.Images, url, state.Account?.Name ?? "账号", 64, 64, false, _clock, () => _active && !_disposed, true); _avatar.Child = _image;
        }
        _image.Activate(XamlRoot?.RasterizationScale ?? 1);
    }
    internal void SetActive(bool active) { _active = active; if (active) Refresh(); else { _image?.Deactivate(); _clock.Suspend(); } }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return; _busy = true; Refresh();
        try { await action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _feedback.Text = ex is InvalidOperationException ? ex.Message : "操作未完成，请检查组件或本机存储后重试。"; }
        finally { _busy = false; Refresh(); }
    }
    private async Task<bool> ConfirmAsync(string title, string text)
        => await new ContentDialog { Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = "确认", CloseButtonText = "取消", XamlRoot = XamlRoot }.ShowAsync() == ContentDialogResult.Primary;
    private async Task ConnectAsync()
    {
        if (_sync.State.Binding is not null)
        { if (!await ConfirmAsync("更换浏览器来源？", "先撤销当前绑定及其凭据，再连接新的浏览器配置。")) return; await _sync.DisconnectAsync(); }
        var browser = new ComboBox { Header = "浏览器", ItemsSource = new[] { "Chrome", "Edge" }, SelectedIndex = 0 };
        var installStatus = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var install = UiFactory.Button("安装／修复本机连接组件");
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            try { var selected = browser.SelectedItem as string ?? "Chrome"; await Task.Run(() => _installer.Install(selected, Path.Combine(AppContext.BaseDirectory, "BrowserHost"))); installStatus.Text = "本机组件已安装，请加载扩展并点击连接直播助手。"; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { installStatus.Text = "安装未完成，请解压完整发布包或稍后重试。"; }
            finally { install.IsEnabled = true; }
        };
        var folder = UiFactory.Button("打开扩展文件夹"); folder.Click += async (_, _) =>
        { try { await Windows.System.Launcher.LaunchFolderAsync(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.Combine(AppContext.BaseDirectory, "browser-extension"))); } catch { installStatus.Text = "找不到扩展，请解压完整发布包。"; } };
        var pending = new ComboBox { Header = "等待确认的浏览器来源", HorizontalAlignment = HorizontalAlignment.Stretch };
        var useAccount = new CheckBox { Content = "确认后切换为账号读取", IsChecked = false };
        var instructions = new TextBlock { Text = "Chrome：chrome://extensions；Edge：edge://extensions。开启开发者模式，加载扩展文件夹。\n在扩展为浏览器配置命名并点击“连接直播助手”，再在这里确认来源（流程 2 分钟内有效）。确认前不会接收 Cookie。\n开发版固定 ID：" + BrowserSyncProtocol.ExtensionId, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 430, IsTextSelectionEnabled = true };
        var dialog = new ContentDialog { Title = "连接浏览器", Content = new StackPanel { Spacing = 10, Children = { browser, install, installStatus, folder, instructions, pending, useAccount } },
            PrimaryButtonText = "确认绑定此来源", CloseButtonText = "关闭", XamlRoot = XamlRoot, IsPrimaryButtonEnabled = false };
        void UpdatePending()
        {
            var selected = (pending.SelectedItem as ComboBoxItem)?.Tag as PendingBrowser;
            pending.Items.Clear();
            foreach (var item in _sync.Pending.Where(item => item.ExpiresAt > DateTimeOffset.UtcNow)) pending.Items.Add(new ComboBoxItem { Content = $"{item.Message.Browser} · {item.Message.Name}", Tag = item });
            pending.SelectedItem = pending.Items.OfType<ComboBoxItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, selected)) ?? pending.Items.OfType<ComboBoxItem>().FirstOrDefault();
            dialog.IsPrimaryButtonEnabled = pending.SelectedItem is not null;
        }
        pending.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = pending.SelectedItem is not null;
        _sync.OpenPairing(); _sync.Changed += UpdatePending;
        browser.SelectionChanged += (_, _) => installStatus.Text = _installer.IsInstalled(browser.SelectedItem as string ?? "Chrome") ? "本机组件已安装" : "请安装本机连接组件";
        installStatus.Text = _installer.IsInstalled("Chrome") ? "本机组件已安装" : "请安装本机连接组件";
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); args.Cancel = true;
            try
            {
                if ((pending.SelectedItem as ComboBoxItem)?.Tag is not PendingBrowser selected) return;
                if (useAccount.IsChecked == true) await _vm.SaveAsync(_vm.Settings with { AnonymousReading = false });
                else if (_vm.IsAnonymousReading) await _vm.SaveAsync(_vm.Settings with { AnonymousReading = true });
                await _sync.ApproveAsync(selected); _feedback.Text = "来源已确认，请在扩展点击“完成连接”。"; args.Cancel = false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { installStatus.Text = "确认未完成或请求已过期，请重新连接。"; }
            finally { deferral.Complete(); }
        };
        try { await dialog.ShowAsync(); } finally { _sync.Changed -= UpdatePending; _sync.ClosePairing(); }
    }
    private async Task ShowGuideAsync()
    {
        var open = UiFactory.Button("打开 B站登录页面"); open.Click += async (_, _) => { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.bilibili.com/")); };
        await new ContentDialog { Title = "在浏览器同步", XamlRoot = XamlRoot, CloseButtonText = "知道了", Content = new StackPanel { Spacing = 10, Children = {
            new TextBlock { Text = "在已绑定浏览器的工具栏打开本机登录同步扩展，点击“立即同步”。自动跟随开关也在扩展中。\n此桌面入口不会主动读取浏览器；若只需重试验证，点击刷新本机状态。", TextWrapping = TextWrapping.Wrap, MaxWidth = 430 }, open } } }.ShowAsync();
    }
    private async Task UninstallAsync()
    {
        var clear = new CheckBox { Content = "同时清除本机账号凭据", IsChecked = true };
        var dialog = new ContentDialog { Title = "卸载本机连接组件", PrimaryButtonText = "卸载", CloseButtonText = "取消", XamlRoot = XamlRoot,
            Content = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "撤销浏览器绑定，仅删除本项目的注册项和组件。浏览器站点 Cookie 不会删除。", TextWrapping = TextWrapping.Wrap, MaxWidth = 430 }, clear } } };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunAsync(async () => { var clearCredentials = clear.IsChecked == true; await Task.Run(() => _installer.Uninstall(clearCredentials)); await _sync.RefreshAsync(true); _feedback.Text = "本机组件已卸载；请在浏览器扩展管理页移除扩展。"; });
    }
}
