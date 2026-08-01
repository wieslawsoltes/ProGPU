using Microsoft.UI.Xaml.Automation.Peers;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Provider;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class IRawElementProviderSimple :
    DependencyObject
{
    internal IRawElementProviderSimple(AutomationPeer peer) => Peer = peer;

    internal AutomationPeer Peer { get; }
}
