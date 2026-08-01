using Microsoft.UI.Xaml.Controls;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Peers;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public class FrameworkElementAutomationPeer : AutomationPeer
{
    private readonly FrameworkElement _owner;

    protected internal FrameworkElementAutomationPeer(
        WinRT.IObjectReference objRef)
        : base(objRef)
    {
        _owner = null!;
    }

    protected FrameworkElementAutomationPeer(
        WinRT.DerivedComposed _)
        : base(_)
    {
        _owner = null!;
    }

    public FrameworkElementAutomationPeer(FrameworkElement owner) =>
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public UIElement Owner => _owner;

    public static AutomationPeer? FromElement(UIElement element) =>
        element?.GetOrCreateAutomationPeer();

    public static AutomationPeer? CreatePeerForElement(UIElement element) =>
        element?.GetOrCreateAutomationPeer();

    internal string GetClassNameValue() =>
        _owner.GetType().Name;

    internal bool IsEnabledValue() =>
        _owner.IsEnabled;

    internal bool IsKeyboardFocusableValue() =>
        _owner.IsEnabled &&
        _owner.IsVisible;

    internal bool HasKeyboardFocusValue() =>
        _owner is Control control &&
        control.IsFocused;

    internal bool IsOffscreenValue() =>
        !_owner.IsVisible;
}
