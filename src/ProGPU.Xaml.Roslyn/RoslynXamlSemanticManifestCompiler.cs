using System;
using System.Linq;
using System.Threading;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Resources;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Syntax;

namespace ProGPU.Xaml.Roslyn;

/// <summary>
/// Owns the compilation-dependent resource-manifest phase shared by incremental,
/// workspace, CLI, and direct project hosts.
/// </summary>
public sealed class RoslynXamlSemanticManifestCompiler
{
    private readonly RoslynXamlExtensionHost _extensions;

    public RoslynXamlSemanticManifestCompiler()
        : this(RoslynXamlExtensionHost.Empty)
    {
    }

    public RoslynXamlSemanticManifestCompiler(
        RoslynXamlExtensionHost extensions)
    {
        _extensions = extensions ??
            throw new ArgumentNullException(nameof(extensions));
    }

    public RoslynXamlSemanticManifestCompilation Compile(
        XamlInfosetDocument infoset,
        IXamlTypeSystem typeSystem,
        IRoslynXamlFrameworkProfile framework,
        string logicalResourceUri,
        bool strict,
        CancellationToken cancellationToken = default)
    {
        if (infoset == null) throw new ArgumentNullException(nameof(infoset));
        if (typeSystem == null) throw new ArgumentNullException(nameof(typeSystem));
        if (framework == null) throw new ArgumentNullException(nameof(framework));
        if (logicalResourceUri == null)
            throw new ArgumentNullException(nameof(logicalResourceUri));
        cancellationToken.ThrowIfCancellationRequested();

        XamlDocumentBuildMetadata? buildMetadata = null;
        if (typeSystem is IXamlBuildMetadataResolver buildMetadataResolver)
        {
            buildMetadata = buildMetadataResolver.ResolveDocumentBuildMetadata(
                infoset.Path,
                GetDirectiveText(infoset.Root, "Class"));
        }
        var resourceUri = buildMetadata?.ResourceIdentity?.IsValid == true
            ? buildMetadata.ResourceIdentity.ResourceId!
            : logicalResourceUri;
        var rawManifest = new XamlResourceDocumentManifestBuilder().Build(
            infoset,
            resourceUri);
        var bound = new XamlSemanticBinder().Bind(
            infoset,
            typeSystem,
            new XamlSemanticBindingOptions { Strict = strict },
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var extensions = _extensions;
        if (framework is IRoslynXamlExtensionProvider provider)
        {
            var frameworkExtensions = provider.RoslynExtensionHost;
            if (frameworkExtensions != null)
                extensions = RoslynXamlExtensionHost.Compose(
                    frameworkExtensions,
                    extensions);
        }
        bound = extensions.ApplyBoundDocumentTransforms(
            new RoslynXamlBoundDocumentTransformContext(
                bound,
                typeSystem,
                framework.Id,
                resourceUri,
                strict,
                cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        var manifest = new XamlResourceSemanticManifestBuilder().Build(
            rawManifest,
            bound);
        return new RoslynXamlSemanticManifestCompilation(
            bound,
            manifest,
            resourceUri,
            buildMetadata);
    }

    private static string? GetDirectiveText(
        XamlInfosetObject? root,
        string name) =>
        root?.Members.FirstOrDefault(member =>
                member.Name.IsDirective &&
                string.Equals(
                    member.Name.NamespaceUri,
                    XamlNamespaces.Language2006,
                    StringComparison.Ordinal) &&
                string.Equals(
                    member.Name.LocalName,
                    name,
                    StringComparison.Ordinal))?
            .Values.OfType<XamlInfosetText>()
            .FirstOrDefault()?
            .Text;
}

public sealed class RoslynXamlSemanticManifestCompilation
{
    public RoslynXamlSemanticManifestCompilation(
        XamlBoundDocument boundDocument,
        XamlResourceDocumentManifest manifest,
        string resourceUri,
        XamlDocumentBuildMetadata? buildMetadata)
    {
        BoundDocument = boundDocument ??
            throw new ArgumentNullException(nameof(boundDocument));
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ResourceUri = resourceUri ??
            throw new ArgumentNullException(nameof(resourceUri));
        BuildMetadata = buildMetadata;
    }

    public XamlBoundDocument BoundDocument { get; }
    public XamlResourceDocumentManifest Manifest { get; }
    public string ResourceUri { get; }
    public XamlDocumentBuildMetadata? BuildMetadata { get; }
}
