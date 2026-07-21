using Microsoft.UI.Xaml.Controls;

namespace Microsoft.UI.Xaml.Automation.Peers;

public class FrameworkElementAutomationPeer : AutomationPeer
{
    public FrameworkElementAutomationPeer(FrameworkElement owner) =>
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public FrameworkElement Owner { get; }

    public static AutomationPeer? FromElement(UIElement element) =>
        element?.GetOrCreateAutomationPeer();

    public static AutomationPeer? CreatePeerForElement(UIElement element) =>
        element?.GetOrCreateAutomationPeer();

    public override bool IsKeyboardFocusable() => Owner.IsEnabled && Owner.IsVisible;

    public override bool HasKeyboardFocus() => Owner is Control control && control.IsFocused;
}
