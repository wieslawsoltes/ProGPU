using Microsoft.UI.Xaml.Automation.Peers;

namespace Microsoft.UI.Xaml.Automation.Provider;

public sealed class IRawElementProviderSimple
{
    internal IRawElementProviderSimple(AutomationPeer peer) => Peer = peer;

    public AutomationPeer Peer { get; }
}
