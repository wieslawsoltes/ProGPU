using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Schema;

namespace ProGPU.Xaml.Roslyn;

/// <summary>
/// Ordered semantic transform over the canonical binder result before resource-graph
/// construction. The host owns document identity, existing diagnostics, and derived graphs.
/// </summary>
public interface IRoslynXamlBoundDocumentTransformExtension : IRoslynXamlExtension
{
    RoslynXamlBoundDocumentTransformResult Transform(
        RoslynXamlBoundDocumentTransformContext context);
}

public sealed class RoslynXamlBoundDocumentTransformContext
{
    public RoslynXamlBoundDocumentTransformContext(
        XamlBoundDocument document,
        IXamlTypeSystem typeSystem,
        string frameworkId,
        string? resourceUri,
        bool strict,
        CancellationToken cancellationToken = default)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        TypeSystem = typeSystem ?? throw new ArgumentNullException(nameof(typeSystem));
        FrameworkId = string.IsNullOrWhiteSpace(frameworkId)
            ? throw new ArgumentException("A framework ID is required.", nameof(frameworkId))
            : frameworkId;
        ResourceUri = resourceUri;
        Strict = strict;
        CancellationToken = cancellationToken;
    }

    public XamlBoundDocument Document { get; }
    public IXamlTypeSystem TypeSystem { get; }
    public string FrameworkId { get; }
    public string? ResourceUri { get; }
    public bool Strict { get; }
    public CancellationToken CancellationToken { get; }
}

public sealed class RoslynXamlBoundDocumentTransformResult
{
    public RoslynXamlBoundDocumentTransformResult(
        XamlBoundObject? root,
        IEnumerable<RoslynXamlValidationIssue>? issues = null)
    {
        Root = root;
        Issues = issues == null
            ? ImmutableArray<RoslynXamlValidationIssue>.Empty
            : issues.ToImmutableArray();
    }

    public XamlBoundObject? Root { get; }
    public ImmutableArray<RoslynXamlValidationIssue> Issues { get; }
}
