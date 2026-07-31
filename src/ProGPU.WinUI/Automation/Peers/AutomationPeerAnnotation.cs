using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Automation.Peers;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class AutomationPeerAnnotation : DependencyObject
{
    private static readonly DependencyProperty s_typeProperty =
        DependencyProperty.Register(
            nameof(Type),
            typeof(AnnotationType),
            typeof(AutomationPeerAnnotation),
            new PropertyMetadata(AnnotationType.Unknown));

    private static readonly DependencyProperty s_peerProperty =
        DependencyProperty.Register(
            nameof(Peer),
            typeof(AutomationPeer),
            typeof(AutomationPeerAnnotation),
            new PropertyMetadata(null));

    public AutomationPeerAnnotation()
    {
    }

    public AutomationPeerAnnotation(AnnotationType type) =>
        Type = type;

    public AutomationPeerAnnotation(
        AnnotationType type,
        AutomationPeer peer)
    {
        Type = type;
        Peer = peer;
    }

    public static DependencyProperty TypeProperty =>
        s_typeProperty;

    public static DependencyProperty PeerProperty =>
        s_peerProperty;

    public AnnotationType Type
    {
        get =>
            (AnnotationType)(GetValue(s_typeProperty) ??
            AnnotationType.Unknown);
        set => SetValue(s_typeProperty, value);
    }

    public AutomationPeer Peer
    {
        get => (AutomationPeer)GetValue(s_peerProperty)!;
        set => SetValue(s_peerProperty, value);
    }
}
