using System.Text;
using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace BilibiliLiveAssistant.App;

internal sealed class ObsThemeEditor : StackPanel
{
    private readonly ComboBox _template = new() { Header = "网页模板", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch _shared = new() { Header = "基础外观沿用共享设置" };
    private readonly TextBox _font = new() { Header = "字体名称", PlaceholderText = "Microsoft YaHei UI" };
    private readonly SettingSlider _size = new("字号", 18, 12, 64, " px"), _avatar = new("头像", 28, 12, 96, " px"), _emote = new("表情", 28, 16, 128, " px"),
        _gap = new("消息间距", 4, 0, 32, " px"), _padding = new("内容内边距", 6, 0, 32, " px"), _radius = new("圆角", 10, 0, 40, " px"),
        _border = new("边框", 0, 0, 8, " px"), _backgroundOpacity = new("卡片背景不透明度", 75, 0, 100, "%"), _opacity = new("整体不透明度", 100, 0, 100, "%");
    private readonly SettingColor _textColor = new("正文颜色"), _nameColor = new("昵称颜色"), _background = new("卡片背景"), _borderColor = new("边框颜色");
    private readonly ToggleSwitch _shadow = new() { Header = "文字阴影" };
    private readonly CheckBox[] _show = new[] { "头像", "粉丝牌", "身份徽章", "人数与高能榜", "固定 SC", "固定礼物／上舰", "混合列表", "进房", "关注" }.Select(s => new CheckBox { Content = s }).ToArray();
    private readonly TextBox _css = new() { Header = "自定义 CSS", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 240, MaxHeight = 440, MaxLength = ObsThemeSettings.MaxCssBytes, FontFamily = new("Consolas") };
    private readonly TextBlock _feedback = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _assets = new() { Header = "本机素材", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _assetCode = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 150 };
    private ObsThemeSettings _current = new();
    private bool _loading, _restoringCss;
    private string _lastCss = "";
    public event Action? PreviewRequested;
    public event Action? Changed;

    public ObsThemeEditor()
    {
        Spacing = 10;
        foreach (var (id, title) in new[] { ("compact", "现有紧凑"), ("text", "透明纯文字"), ("bubble", "聊天气泡"), ("card", "简洁卡片") })
            _template.Items.Add(new ComboBoxItem { Content = title, Tag = id });
        Children.Add(new TextBlock { Text = "仅修改 OBS 网页源，桌面浮窗不受影响。模板与 CSS 可叠加；修改后打开网页预览，保存后应用到 OBS。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Children.Add(_template);
        var preview = UiFactory.Button("打开真实网页预览"); preview.Click += (_, _) => PreviewRequested?.Invoke(); Children.Add(preview);
        Children.Add(_shared);
        var appearance = new StackPanel { Spacing = 10, Children = { _font, _size, _avatar, _emote, _gap, _padding, _radius, _border, _textColor, _nameColor, _background, _backgroundOpacity, _borderColor, _opacity, _shadow } };
        _shared.Toggled += (_, _) => appearance.Visibility = _shared.IsOn ? Visibility.Collapsed : Visibility.Visible;
        Children.Add(new Expander { Header = "网页基础外观", Content = appearance, HorizontalAlignment = HorizontalAlignment.Stretch });
        var show = new StackPanel { Spacing = 4 }; foreach (var check in _show) show.Children.Add(check);
        show.Children.Add(new TextBlock { Text = "这里关闭的内容仅在网页隐藏；共享设置已关闭的内容不会额外生成。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Children.Add(new Expander { Header = "网页内容显隐", Content = show, HorizontalAlignment = HorizontalAlignment.Stretch });
        var css = new StackPanel { Spacing = 8, Children = { _css } };
        var import = UiFactory.Button("导入 CSS"); import.Click += async (_, _) => await ImportCssAsync();
        var export = UiFactory.Button("导出 CSS"); export.Click += async (_, _) => await ExportCssAsync();
        var copy = UiFactory.Button("复制 CSS"); copy.Click += (_, _) => Copy(_css.Text);
        var example = UiFactory.Button("插入示例"); example.Click += (_, _) => _css.Text += "\n/* 昵称和卡片可分别调整 */\n.bla-name { color: #a7d8ff; }\n.bla-content { border-radius: 12px; }\n";
        css.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { import, export, copy } }); css.Children.Add(example);
        css.Children.Add(new TextBlock { Text = "CSS 上限 256 KiB。修改 .bla-content 定制外观；外层 .bla-message 保留给出入动画。只支持本机素材，不加载远程 @import。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Children.Add(new Expander { Header = "高级 CSS", Content = css, HorizontalAlignment = HorizontalAlignment.Stretch });
        var assetImport = UiFactory.Button("导入图片／字体"); assetImport.Click += async (_, _) =>
        { assetImport.IsEnabled = false; try { await ImportAssetAsync(); } finally { assetImport.IsEnabled = true; } };
        var assetCopy = UiFactory.Button("复制素材代码"); assetCopy.Click += (_, _) => Copy(_assetCode.Text);
        var assetFolder = UiFactory.Button("打开素材目录"); assetFolder.Click += async (_, _) =>
        { try { await Windows.System.Launcher.LaunchFolderPathAsync(ObsThemeAssets.DirectoryPath); } catch { _feedback.Text = "无法打开素材目录。"; } };
        var assets = new StackPanel { Spacing = 8, Children = { assetImport, _assets, _assetCode, assetCopy, assetFolder,
            new TextBlock { Text = "PNG／JPEG、TTF／OTF／WOFF2，每个输入最多 4 MiB；共 64 项、32 MiB。素材只留在本机，不会随发布包分享。", FontSize = 12, TextWrapping = TextWrapping.Wrap } } };
        _assets.SelectionChanged += (_, _) => UpdateAssetCode();
        Children.Add(new Expander { Header = "本地素材", Content = assets, HorizontalAlignment = HorizontalAlignment.Stretch });
        var reset = UiFactory.Button("恢复默认网页样式");
        reset.Click += (_, _) => { Load(new()); Changed?.Invoke(); _feedback.Text = "网页样式已恢复为默认草稿；保存后应用。"; };
        Children.Add(reset); Children.Add(_feedback);
        foreach (var control in new DependencyObject[] { _template, _shared, appearance, show }) SettingEvents.Watch(control, () => { if (!_loading) Changed?.Invoke(); });
        _css.TextChanged += (_, _) =>
        {
            if (_loading || _restoringCss) return;
            if (Encoding.UTF8.GetByteCount(_css.Text) > ObsThemeSettings.MaxCssBytes)
            {
                _restoringCss = true; _css.Text = _lastCss; _restoringCss = false;
                _feedback.Text = "CSS 超过 256 KiB，本次输入已撤回，原代码保留。"; return;
            }
            _lastCss = _css.Text; Changed?.Invoke();
        };
    }
    public void Load(ObsThemeSettings value)
    {
        _loading = true;
        try
        {
            _current = value; _template.SelectedItem = _template.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value.Template) ?? _template.Items[0];
            _shared.IsOn = value.UseSharedAppearance; _font.Text = value.FontFamily;
            _size.Value = value.FontSize; _avatar.Value = value.AvatarSize; _emote.Value = value.EmoteSize; _gap.Value = value.Gap; _padding.Value = value.Padding; _radius.Value = value.Radius; _border.Value = value.BorderWidth;
            _textColor.Text = value.TextColor; _nameColor.Text = value.NameColor; _background.Text = value.BackgroundColor; _borderColor.Text = value.BorderColor;
            _backgroundOpacity.Value = value.BackgroundOpacity * 100; _opacity.Value = value.Opacity * 100; _shadow.IsOn = value.Shadow; _css.Text = value.CustomCss; _lastCss = _css.Text;
            var show = new[] { value.ShowAvatars, value.ShowMedals, value.ShowIdentity, value.ShowStatus, value.ShowSuperChat, value.ShowGift, value.ShowList, value.ShowEntry, value.ShowFollow };
            for (var i = 0; i < show.Length; i++) _show[i].IsChecked = show[i];
            ReloadAssets();
        }
        finally { _loading = false; }
    }
    public ObsThemeSettings Read() => (_current with
    {
        Template = (_template.SelectedItem as ComboBoxItem)?.Tag as string ?? "compact", UseSharedAppearance = _shared.IsOn, FontFamily = _font.Text,
        FontSize = (int)_size.Value, AvatarSize = (int)_avatar.Value, EmoteSize = (int)_emote.Value, Gap = (int)_gap.Value, Padding = (int)_padding.Value, Radius = (int)_radius.Value, BorderWidth = (int)_border.Value,
        TextColor = _textColor.Text, NameColor = _nameColor.Text, BackgroundColor = _background.Text, BorderColor = _borderColor.Text,
        BackgroundOpacity = _backgroundOpacity.Value / 100, Opacity = _opacity.Value / 100, Shadow = _shadow.IsOn,
        ShowAvatars = _show[0].IsChecked == true, ShowMedals = _show[1].IsChecked == true, ShowIdentity = _show[2].IsChecked == true,
        ShowStatus = _show[3].IsChecked == true, ShowSuperChat = _show[4].IsChecked == true, ShowGift = _show[5].IsChecked == true,
        ShowList = _show[6].IsChecked == true, ShowEntry = _show[7].IsChecked == true, ShowFollow = _show[8].IsChecked == true, CustomCss = _css.Text,
    }).Normalize();
    public void Validate()
    {
        if (Encoding.UTF8.GetByteCount(_css.Text) > ObsThemeSettings.MaxCssBytes) throw new InvalidOperationException("自定义 CSS 超过 256 KiB，请缩短后保存。");
    }
    private async Task ImportCssAsync()
    {
        try
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog { Filter = "CSS 样式 (*.css)|*.css", CheckFileExists = true };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            await using var file = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            if (file.Length > ObsThemeSettings.MaxCssBytes) throw new IOException("CSS 超过 256 KiB。");
            var bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes);
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            if (Encoding.UTF8.GetByteCount(text) > ObsThemeSettings.MaxCssBytes) throw new IOException("CSS 超过 256 KiB。");
            _css.Text = text; _feedback.Text = "CSS 已导入草稿。LAPLACE 模板需要按本软件的样式接口调整。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _feedback.Text = "导入失败，原 CSS 保留。请检查文件大小和 UTF-8 编码。"; }
    }
    private async Task ExportCssAsync()
    {
        try
        {
            using var dialog = new System.Windows.Forms.SaveFileDialog { Filter = "CSS 样式 (*.css)|*.css", FileName = "弹幕样式.css", DefaultExt = "css" };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            await File.WriteAllTextAsync(dialog.FileName, _css.Text, new UTF8Encoding(false)); _feedback.Text = "已导出自定义 CSS；模板与滑块参数仍由软件保存。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _feedback.Text = "导出失败，请检查目标目录。"; }
    }
    private async Task ImportAssetAsync()
    {
        try
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog { Filter = "图片或字体|*.png;*.jpg;*.jpeg;*.ttf;*.otf;*.woff2", CheckFileExists = true };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var name = await ObsThemeAssets.ImportAsync(dialog.FileName); ReloadAssets(); _assets.SelectedItem = name; _feedback.Text = "素材已导入。复制素材代码到 CSS 后打开网页预览。";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _feedback.Text = ex is IOException ? ex.Message : "素材导入失败，文件可能损坏或格式不受支持。"; }
    }
    private void ReloadAssets()
    {
        try { var previous = _assets.SelectedItem as string; _assets.Items.Clear(); foreach (var name in ObsThemeAssets.List()) _assets.Items.Add(name); _assets.SelectedItem = previous; }
        catch (IOException) { _feedback.Text = "无法读取素材目录。"; }
        catch (UnauthorizedAccessException) { _feedback.Text = "无权读取素材目录。"; }
    }
    private void UpdateAssetCode()
    {
        if (_assets.SelectedItem is not string name) { _assetCode.Text = ""; return; }
        _assetCode.Text = name.EndsWith(".png", StringComparison.Ordinal)
            ? $".bla-content {{ background-image: url(\"assets/{name}\"); background-size: cover; }}"
            : $"@font-face {{ font-family: \"本地字体\"; src: url(\"assets/{name}\"); }}\n.bla-content {{ font-family: \"本地字体\", sans-serif; }}";
    }
    private void Copy(string text)
    {
        try { var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); _feedback.Text = "已复制。"; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _feedback.Text = "无法访问剪贴板。"; }
    }
}
