using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Peers;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum AutomationNavigationDirection
{
    Parent = 0,
    NextSibling = 1,
    PreviousSibling = 2,
    FirstChild = 3,
    LastChild = 4
}
