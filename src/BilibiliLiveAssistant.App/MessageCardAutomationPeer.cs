using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace BilibiliLiveAssistant.App;

// A chat message is one read-only semantic item, not an editable document.
// Avoid creating native document text ranges for rapidly rebound RichTextBlocks.
// Keep the complete spoken message on the row and expose its actual actions as
// child peers. This does not imply that every UIA client leaks those ranges.
internal sealed class MessageCardAutomationPeer(MessageCard owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => nameof(MessageCard);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override IList<AutomationPeer> GetChildrenCore()
    {
        var actions = new List<AutomationPeer>();
        if (!owner.IsDisposed) AddActions(owner, actions);
        return actions;
    }

    private static void AddActions(DependencyObject node, List<AutomationPeer> actions)
    {
        if (node is UIElement { Visibility: Visibility.Collapsed }) return;
        // Do not instantiate document/image peers simply to filter them later.
        if (node is RichTextBlock or BiliImageView) return;
        if (node is ButtonBase or Expander)
        {
            if (node is FrameworkElement element && CreatePeerForElement(element) is { } peer) actions.Add(peer);
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            AddActions(VisualTreeHelper.GetChild(node, i), actions);
    }
}
