using System;

namespace ProGPU.Xaml.Roslyn;

[Flags]
public enum RoslynXamlExtensionCapabilities
{
    None = 0,
    MarkupExtensionExpression = 1 << 0,
    BoundDocumentValidation = 1 << 1,
    ConstructionProgramTransform = 1 << 2,
    BoundDocumentTransform = 1 << 3
}

public enum RoslynXamlExtensionConflictPolicy
{
    Diagnose,
    CoalesceEquivalent
}

public interface IRoslynXamlExtension
{
    string Id { get; }
    int ContractVersion { get; }
    int Version { get; }
    int Priority { get; }
    RoslynXamlExtensionCapabilities Capabilities { get; }
    RoslynXamlExtensionConflictPolicy ConflictPolicy { get; }
}

/// <summary>
/// Optional contract implemented by a framework profile that contributes Roslyn compiler
/// extensions as part of its package. The returned host must be immutable and may be shared
/// across compilations.
/// </summary>
public interface IRoslynXamlExtensionProvider
{
    RoslynXamlExtensionHost RoslynExtensionHost { get; }
}

public sealed class RoslynXamlExtensionHostOptions
{
    public int MaximumTransformedBoundNodes { get; set; } =
        RoslynXamlExtensionHost.DefaultMaximumTransformedBoundNodes;

    public int MaximumTransformedIrNodes { get; set; } =
        RoslynXamlExtensionHost.DefaultMaximumTransformedIrNodes;
}
