using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BilibiliLiveAssistant.App;

internal sealed class SettingSlider : StackPanel
{
    private readonly Slider _slider;
    private readonly TextBlock _value = new() { Opacity = 0.65, FontSize = 12 };
    private readonly string _unit;
    public event Action? Changed;
    public SettingSlider(string label, double value, double min, double max, string unit = "", double step = 1)
    {
        _unit = unit; Spacing = 2;
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = label, FontSize = 13 }); Grid.SetColumn(_value, 1); header.Children.Add(_value);
        _slider = new() { Minimum = min, Maximum = max, StepFrequency = step, Value = value, HorizontalAlignment = HorizontalAlignment.Stretch };
        _slider.ValueChanged += (_, _) => { _value.Text = $"{_slider.Value:0.#}{_unit}"; Changed?.Invoke(); };
        Children.Add(header); Children.Add(_slider); Value = value;
    }
    public double Value { get => _slider.Value; set { _slider.Value = value; _value.Text = $"{_slider.Value:0.#}{_unit}"; } }
    public double Maximum { get => _slider.Maximum; set => _slider.Maximum = value; }
    public double Minimum { get => _slider.Minimum; set => _slider.Minimum = value; }
    public bool IsEnabled { get => _slider.IsEnabled; set { _slider.IsEnabled = value; Opacity = value ? 1 : 0.5; } }
}

internal sealed class SettingColor : StackPanel
{
    private readonly Button _button;
    private readonly ColorPicker _picker = new() { IsAlphaEnabled = false, IsHexInputVisible = true, IsColorChannelTextInputVisible = true,
        IsColorSliderVisible = true, IsColorSpectrumVisible = true };
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _swatch = new() { Width = 32, Height = 20, RadiusX = 4, RadiusY = 4 };
    private readonly TextBlock _value = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    public event Action? Changed;
    public SettingColor(string label)
    {
        Spacing = 4; Children.Add(new TextBlock { Text = label, FontSize = 13 });
        _button = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _swatch, _value } } };
        _button.Flyout = new Flyout { Content = _picker }; Children.Add(_button);
        _picker.ColorChanged += (_, _) => { Refresh(); Changed?.Invoke(); }; Text = "#FFFFFF";
    }
    public string Text
    {
        get => $"#{_picker.Color.R:X2}{_picker.Color.G:X2}{_picker.Color.B:X2}";
        set { if (BilibiliLiveAssistant.Core.OverlayRegionSettings.IsColor(value)) _picker.Color = Color.FromArgb(255, Convert.ToByte(value.Substring(1, 2), 16), Convert.ToByte(value.Substring(3, 2), 16), Convert.ToByte(value.Substring(5, 2), 16)); Refresh(); }
    }
    private void Refresh() { _swatch.Fill = new SolidColorBrush(_picker.Color); _value.Text = Text; DashboardUi.Describe(_button, $"选择颜色 {Text}"); }
}

internal static class SettingEvents
{
    public static void Watch(DependencyObject node, Action changed)
    {
        switch (node)
        {
            case SettingSlider slider: slider.Changed += changed; return;
            case SettingColor color: color.Changed += changed; return;
            case Slider slider: slider.ValueChanged += (_, _) => changed(); break;
            case NumberBox number: number.ValueChanged += (_, _) => changed(); break;
            case ComboBox combo: combo.SelectionChanged += (_, _) => changed(); if (combo.IsEditable) combo.TextSubmitted += (_, _) => changed(); break;
            case TextBox text: text.TextChanged += (_, _) => changed(); break;
            case CheckBox check: check.Checked += (_, _) => changed(); check.Unchecked += (_, _) => changed(); break;
            case ToggleSwitch toggle: toggle.Toggled += (_, _) => changed(); break;
        }
        if (node is Panel panel) foreach (var child in panel.Children) Watch(child, changed);
        if (node is Expander expander && expander.Content is DependencyObject content) Watch(content, changed);
    }
}
