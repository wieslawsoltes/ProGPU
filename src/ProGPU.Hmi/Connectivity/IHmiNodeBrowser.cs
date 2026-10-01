namespace ProGPU.Hmi;

/// <summary>A detached server node descriptor. Browsing never makes a tag writable or publishes process data.</summary>
public sealed record HmiBrowseNode(string NamespaceUri, string Identifier, string DisplayName, string NodeClass);
public sealed record HmiBrowseResult(IReadOnlyList<HmiBrowseNode> Nodes, bool Truncated);

/// <summary>Optional bounded one-level address-space browsing over an explicitly opened connection.</summary>
public interface IHmiNodeBrowser
{
    ValueTask<HmiBrowseResult> BrowseAsync(string namespaceUri, string identifier, int maximumResults, CancellationToken cancellationToken);
}

/// <summary>Monotonically changes when the transport session is replaced. Reviewed commands cannot cross session identities.</summary>
public interface IHmiConnectionGeneration
{
    long ConnectionGeneration { get; }
}
