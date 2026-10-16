using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;

namespace BilibiliLiveAssistant.App;

internal static class DashboardUi
{
    public static FontIcon Icon(string glyph, double size = 16) => new()
    {
        Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size,
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button Button(string glyph, string label, double size = 32)
    {
        var button = new Button
        {
            Content = Icon(glyph), Width = size, Height = size, MinWidth = 0, MinHeight = 0,
            // A transparent brush still participates in hit testing. A null
            // background with a non-hit-testable icon leaves no reliable target.
            Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Describe(button, label);
        return button;
    }

    public static ToggleButton Toggle(string glyph, string label, double size = 32)
    {
        var button = new ToggleButton
        {
            Content = Icon(glyph), Width = size, Height = size, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        void UpdateState()
        {
            button.Background = button.IsChecked == true ? MessageCard.Brush("#4474AE", 0.28) : new SolidColorBrush(Colors.Transparent);
            button.Foreground = MessageCard.Brush(button.IsChecked == true ? "#9FC9FF" : "#CED5E1");
        }
        button.Checked += (_, _) => UpdateState();
        button.Unchecked += (_, _) => UpdateState();
        Describe(button, label);
        return button;
    }

    public static void Describe(DependencyObject element, string label)
    {
        // A realized/recycled row changes its description frequently. Preserve
        // one tooltip control instead of replacing its implicit tooltip object.
        if (ToolTipService.GetToolTip(element) is ToolTip tooltip)
        {
            if (tooltip.Content as string != label) tooltip.Content = label;
        }
        else ToolTipService.SetToolTip(element, new ToolTip { Content = label });
        if (AutomationProperties.GetName(element) != label) AutomationProperties.SetName(element, label);
    }

    // Disconnected rich rows may still own attached tooltip registrations and
    // their templated trees. Detach them as part of the row's explicit lifetime.
    public static void ReleaseDescriptions(DependencyObject element)
    {
        if (ToolTipService.GetToolTip(element) is ToolTip tooltip)
        {
            tooltip.IsOpen = false; tooltip.Content = null; tooltip.Template = null;
        }
        ToolTipService.SetToolTip(element, null);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            ReleaseDescriptions(VisualTreeHelper.GetChild(element, i));
    }
}
