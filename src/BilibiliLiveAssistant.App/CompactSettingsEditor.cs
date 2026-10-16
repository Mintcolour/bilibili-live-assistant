using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace BilibiliLiveAssistant.App;

internal sealed class CompactSettingsEditor : StackPanel
{
    private readonly ToggleSwitch _desktop = new() { Header = "桌面使用紧凑面板" }, _obs = new() { Header = "启用 OBS 本机透明网页源" };
    private readonly NumberBox _port = DisplayPage.Number("本机端口", 17865, 1024, 65535), _width = DisplayPage.Number("OBS 输出宽度", 480, 240, 1920), _height = DisplayPage.Number("OBS 输出高度", 800, 320, 2160);
    private readonly TextBox _address = new() { Header = "OBS 浏览器源地址", IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    private readonly SettingSlider _font = new("文字大小", 18, 12, 48, " px"), _emote = new("表情大小", 28, 16, 128, " px"),
        _opacity = new("内容不透明度", 100, 10, 100, "%"), _background = new("背景不透明度", 0, 0, 100, "%"),
        _lines = new("固定 SC 正文行数", 6, 1, 12), _follow = new("每条关注停留", 5, 1, 300, " 秒");
    private readonly SettingColor _color = new("背景颜色");
    private readonly CheckBox[] _sections = new[] { "人数与高能榜", "SC 固定强调区", "礼物／上舰固定强调区", "混合消息列表", "进房提示", "关注提示" }.Select(s => new CheckBox { Content = s }).ToArray();
    private readonly PriceTiersEditor _sc = new("SC · 金额下限（元）／停留（秒）", 10, 50), _gift = new("礼物 · 合并总金额下限／停留（秒）", 3, 30);
    private readonly SettingSlider _guardUnknown = new("上舰缺失金额时停留", 30, 3, 300, " 秒");
    private CompactOverlaySettings _current = new();
    public event Action? Changed;
    public CompactSettingsEditor(Func<string> address)
    {
        Spacing = 12;
        Children.Add(_desktop);
        var copy = UiFactory.Button("复制 OBS 地址"); copy.Click += (_, _) =>
        {
            UpdateAddress(address()); if (_address.Text.Length == 0) return;
            var data = new DataPackage(); data.SetText(_address.Text); Clipboard.SetContent(data);
        };
        var obs = new StackPanel { Spacing = 10, Children = { _obs, _address, copy,
            new TextBlock { Text = "保存启用后复制地址。在 OBS 添加“浏览器”源，将宽高设为下方数值。桌面隐藏或切回普通模式不影响 OBS。", TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12 }, _width, _height, _port } };
        var style = new StackPanel { Spacing = 10, Children = { _font, _emote, _opacity, _background, _color } };
        var sections = new StackPanel { Spacing = 8 }; foreach (var section in _sections) sections.Children.Add(section); sections.Children.Add(_lines); sections.Children.Add(_follow);
        var prices = new StackPanel { Spacing = 12, Children = { _sc, _gift, _guardUnknown } };
        Children.Add(new Expander { Header = "OBS 网页源", Content = obs, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch });
        Children.Add(new Expander { Header = "共享外观", Content = style, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch });
        Children.Add(new Expander { Header = "显示区域与关注停留", Content = sections, HorizontalAlignment = HorizontalAlignment.Stretch });
        Children.Add(new Expander { Header = "金额停留 · 含普通固定卡片", Content = prices, HorizontalAlignment = HorizontalAlignment.Stretch });
        Children.Add(new TextBlock { Text = "计时从实际显示开始；滚动模式仍完整滚完。免费及未知金额礼物为 3 秒，未知金额 SC 为 10 秒。字体、头像、粉丝牌、阴影等沿用对应悬浮类别的样式。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65 });
        foreach (var child in Children) SettingEvents.Watch(child, () => Changed?.Invoke());
        _sc.Changed += () => Changed?.Invoke(); _gift.Changed += () => Changed?.Invoke();
    }
    public void UpdateAddress(string value) { _address.Text = value; _address.PlaceholderText = "保存启用后生成；启动失败时请换用其他端口。"; }
    public void Load(CompactOverlaySettings compact, OverlayPriceDurations prices, string address)
    {
        _current = compact; _desktop.IsOn = compact.DesktopEnabled; _obs.IsOn = compact.ObsEnabled; _port.Value = compact.ObsPort;
        _width.Value = compact.ObsWidth; _height.Value = compact.ObsHeight; UpdateAddress(address);
        _font.Value = compact.FontSize; _emote.Value = compact.EmoteSize; _opacity.Value = compact.Opacity * 100;
        _background.Value = compact.BackgroundOpacity * 100; _color.Text = compact.BackgroundColor;
        var values = new[] { compact.ShowStatus, compact.ShowSuperChat, compact.ShowGift, compact.ShowList, compact.ShowEntry, compact.ShowFollow };
        for (var i = 0; i < values.Length; i++) _sections[i].IsChecked = values[i];
        _lines.Value = compact.SuperChatLines; _follow.Value = compact.FollowSeconds;
        _sc.Load(prices.SuperChat); _gift.Load(prices.Gift); _guardUnknown.Value = prices.UnknownGuardSeconds;
    }
    public CompactOverlaySettings Read() => (_current with { DesktopEnabled = _desktop.IsOn, ObsEnabled = _obs.IsOn,
        ObsPort = DisplayPage.Value(_port, 17865), ObsWidth = DisplayPage.Value(_width, 480), ObsHeight = DisplayPage.Value(_height, 800),
        FontSize = (int)_font.Value, EmoteSize = (int)_emote.Value, Opacity = _opacity.Value / 100, BackgroundOpacity = _background.Value / 100,
        BackgroundColor = _color.Text, ShowStatus = _sections[0].IsChecked == true, ShowSuperChat = _sections[1].IsChecked == true,
        ShowGift = _sections[2].IsChecked == true, ShowList = _sections[3].IsChecked == true, ShowEntry = _sections[4].IsChecked == true, ShowFollow = _sections[5].IsChecked == true,
        SuperChatLines = (int)_lines.Value, FollowSeconds = _follow.Value }).Normalize();
    public OverlayPriceDurations ReadPrices() => new OverlayPriceDurations { SuperChat = _sc.Read(), Gift = _gift.Read(), UnknownGuardSeconds = _guardUnknown.Value }.Normalize();
    public void Validate() { _sc.Validate(); _gift.Validate(); }
}

internal sealed class PriceTiersEditor : StackPanel
{
    private readonly StackPanel _rows = new() { Spacing = 5 };
    private readonly List<(NumberBox From, NumberBox Seconds)> _values = new();
    private readonly double _min, _max;
    public event Action? Changed;
    public PriceTiersEditor(string label, double min, double max)
    {
        _min = min; _max = max; Spacing = 6; Children.Add(new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap }); Children.Add(_rows);
        var add = UiFactory.Button("添加金额档位"); add.Click += (_, _) =>
        {
            if (_values.Count >= 16) return;
            var last = _values.Select(v => v.From.Value).Where(double.IsFinite).DefaultIfEmpty(0).Max();
            Add(new((decimal)Math.Clamp(last + 1, 0, 100000000), max)); Changed?.Invoke();
        }; Children.Add(add);
    }
    public void Load(IReadOnlyList<PriceDurationTier> tiers) { _rows.Children.Clear(); _values.Clear(); foreach (var tier in tiers) Add(tier); }
    private void Add(PriceDurationTier tier)
    {
        var from = new NumberBox { Value = (double)tier.FromYuan, Minimum = 0, Maximum = 100000000, IsEnabled = _values.Count != 0, MinWidth = 70 };
        var seconds = new NumberBox { Value = tier.Seconds, Minimum = _min, Maximum = _max, MinWidth = 60 };
        var row = new Grid { ColumnSpacing = 6 }; row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(from); Grid.SetColumn(seconds, 1); row.Children.Add(seconds);
        if (_values.Count > 0) { var remove = UiFactory.Button("×"); Grid.SetColumn(remove, 2); row.Children.Add(remove); remove.Click += (_, _) => { _values.Remove((from, seconds)); _rows.Children.Remove(row); Changed?.Invoke(); }; }
        _values.Add((from, seconds)); _rows.Children.Add(row);
        from.ValueChanged += (_, _) => Changed?.Invoke(); seconds.ValueChanged += (_, _) => Changed?.Invoke();
    }
    public IReadOnlyList<PriceDurationTier> Read() => _values.Select(v => new PriceDurationTier(double.IsFinite(v.From.Value) ? (decimal)Math.Clamp(v.From.Value, 0, 100000000) : 0,
        double.IsFinite(v.Seconds.Value) ? v.Seconds.Value : _min)).ToArray();
    public void Validate()
    {
        if (_values.Any(v => !double.IsFinite(v.From.Value) || !double.IsFinite(v.Seconds.Value)) || Read().Select(t => t.FromYuan).Distinct().Count() != _values.Count)
            throw new InvalidOperationException("金额档位需要填写有效数字，且金额下限不能重复。");
    }
}
