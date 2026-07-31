using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Binding;
using ProGPU.Xaml.Lowering;
using ProGPU.Xaml.Resources;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Tooling;

namespace ProGPU.Xaml.Roslyn;

public sealed class RoslynXamlCompilationInspectionOptions
{
    public XamlCompilerOptions CompilerOptions { get; set; } =
        new XamlCompilerOptions { Strict = false };

    public int MaximumProjectionEntries { get; set; } = 100 * 1024;
    public int MaximumPreviewLength { get; set; } = 256;
}

public sealed class RoslynXamlCompilationInspection
{
    internal RoslynXamlCompilationInspection(
        XamlDocumentInspection sourceInspection,
        XamlCompilationResult compilationResult,
        XamlInspectionProjection bound,
        XamlInspectionProjection resources,
        XamlInspectionProjection ir,
        XamlInspectionProjection generatedSources,
        XamlInspectionProjection diagnostics)
    {
        SourceInspection = sourceInspection;
        CompilationResult = compilationResult;
        Bound = bound;
        Resources = resources;
        Ir = ir;
        GeneratedSources = generatedSources;
        Diagnostics = diagnostics;
    }

    public XamlDocumentInspection SourceInspection { get; }
    public XamlCompilationResult CompilationResult { get; }
    public XamlConstructionProgram? Program => CompilationResult.Program;
    public XamlInspectionProjection Bound { get; }
    public XamlInspectionProjection Resources { get; }
    public XamlInspectionProjection Ir { get; }
    public XamlInspectionProjection GeneratedSources { get; }
    public XamlInspectionProjection Diagnostics { get; }
}

/// <summary>
/// Projects the exact accepted construction program and Roslyn syntax output produced by
/// <see cref="CSharpXamlEmitter"/>. Tooling consumers therefore cannot drift from semantic
/// transforms, resource enrichment, validation, IR transforms, or structured generation.
/// </summary>
public sealed class RoslynXamlCompilationInspectionService
{
    private readonly CSharpXamlEmitter _emitter;

    public RoslynXamlCompilationInspectionService()
        : this(new CSharpXamlEmitter())
    {
    }

    public RoslynXamlCompilationInspectionService(
        RoslynXamlExtensionHost extensions)
        : this(new CSharpXamlEmitter(extensions))
    {
    }

    private RoslynXamlCompilationInspectionService(CSharpXamlEmitter emitter)
    {
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
    }

    public RoslynXamlCompilationInspection Inspect(
        XamlDocumentInspection sourceInspection,
        IXamlTypeSystem typeSystem,
        IRoslynXamlFrameworkProfile framework,
        RoslynXamlCompilationInspectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceInspection == null)
            throw new ArgumentNullException(nameof(sourceInspection));
        if (typeSystem == null) throw new ArgumentNullException(nameof(typeSystem));
        if (framework == null) throw new ArgumentNullException(nameof(framework));
        options = options ?? new RoslynXamlCompilationInspectionOptions();
        if (options.CompilerOptions == null)
            throw new ArgumentNullException(nameof(options.CompilerOptions));
        ValidateLimit(
            options.MaximumProjectionEntries,
            XamlDocumentInspectionOptions.MaximumSupportedProjectionEntries,
            nameof(options.MaximumProjectionEntries));
        ValidateLimit(
            options.MaximumPreviewLength,
            XamlDocumentInspectionOptions.MaximumSupportedPreviewLength,
            nameof(options.MaximumPreviewLength));
        cancellationToken.ThrowIfCancellationRequested();

        var compilationResult = _emitter.Emit(
            sourceInspection.Infoset,
            typeSystem,
            framework,
            options.CompilerOptions,
            cancellationToken);
        var program = compilationResult.Program;
        var bound = ProjectBound(
            program?.BoundDocument.Root,
            options.MaximumProjectionEntries,
            options.MaximumPreviewLength,
            cancellationToken);
        var resources = ProjectResources(
            program?.ResourceGraph,
            options.MaximumProjectionEntries,
            options.MaximumPreviewLength,
            cancellationToken);
        var ir = ProjectIr(
            program?.Root,
            options.MaximumProjectionEntries,
            options.MaximumPreviewLength,
            cancellationToken);
        var generated = ProjectGeneratedSources(
            compilationResult,
            options.MaximumProjectionEntries,
            cancellationToken);
        var diagnostics = ProjectDiagnostics(
            compilationResult.Diagnostics,
            options.MaximumProjectionEntries,
            options.MaximumPreviewLength,
            cancellationToken);
        return new RoslynXamlCompilationInspection(
            sourceInspection,
            compilationResult,
            bound,
            resources,
            ir,
            generated,
            diagnostics);
    }

    private static XamlInspectionProjection ProjectBound(
        XamlBoundObject? root,
        int maximumEntries,
        int maximumPreviewLength,
        CancellationToken cancellationToken)
    {
        var projection = new ProjectionBuilder(maximumEntries);
        if (root == null) return projection.Build();
        var visited = new HashSet<object>(ReferenceIdentityComparer.Instance);
        var stack = new Stack<BoundFrame>();
        stack.Push(new BoundFrame(root, 0));
        while (stack.Count != 0)
        {
            if ((projection.TotalCount & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var frame = stack.Pop();
            if (!visited.Add(frame.Value)) continue;
            var value = frame.Value;
            switch (value)
            {
                case XamlBoundObject objectValue:
                    projection.Add(
                        XamlInspectionEntryKind.BoundObject,
                        frame.Depth,
                        objectValue.Type.RequestedName.DisplayName,
                        objectValue.Type.Symbol?.MetadataName ??
                        (objectValue.Type.IsError ? "<unresolved>" : string.Empty),
                        objectValue.SourceSpan,
                        objectValue.StableId);
                    for (var index = objectValue.Members.Length - 1; index >= 0; index--)
                        stack.Push(new BoundFrame(
                            objectValue.Members[index],
                            frame.Depth + 1));
                    break;
                case XamlBoundMember memberValue:
                    projection.Add(
                        XamlInspectionEntryKind.BoundMember,
                        frame.Depth,
                        memberValue.Member.RequestedName.DisplayName,
                        memberValue.Member.Kind + " " +
                        (memberValue.Member.Symbol?.Symbol?.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat) ?? "<synthetic/unresolved>"),
                        memberValue.SourceSpan,
                        memberValue.StableId);
                    for (var index = memberValue.Values.Length - 1; index >= 0; index--)
                        stack.Push(new BoundFrame(
                            memberValue.Values[index],
                            frame.Depth + 1));
                    break;
                case XamlBoundText textValue:
                    projection.Add(
                        XamlInspectionEntryKind.BoundValue,
                        frame.Depth,
                        textValue.IsNormalized ? "Text (normalized)" : "Text",
                        XamlInspectionText.CreatePreview(
                            textValue.Text,
                            maximumPreviewLength),
                        textValue.SourceSpan,
                        textValue.StableId);
                    break;
                case XamlBoundTypeValue typeValue:
                    projection.Add(
                        XamlInspectionEntryKind.BoundValue,
                        frame.Depth,
                        "Type " + typeValue.Type.RequestedName.DisplayName,
                        typeValue.Type.Symbol?.MetadataName ?? "<unresolved>",
                        typeValue.SourceSpan,
                        typeValue.StableId);
                    break;
                case XamlBoundStaticMemberValue staticValue:
                    projection.Add(
                        XamlInspectionEntryKind.BoundValue,
                        frame.Depth,
                        "Static " + staticValue.RequestedName,
                        staticValue.Member?.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat) ?? "<unresolved>",
                        staticValue.SourceSpan,
                        staticValue.StableId);
                    break;
                case XamlBoundFactoryMethodValue factoryValue:
                    projection.Add(
                        XamlInspectionEntryKind.BoundValue,
                        frame.Depth,
                        "Factory " + factoryValue.RequestedName,
                        factoryValue.Method?.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat) ?? "<unresolved>",
                        factoryValue.SourceSpan,
                        factoryValue.StableId);
                    break;
                case XamlBoundNameReferenceValue nameValue:
                    projection.Add(
                        XamlInspectionEntryKind.BoundValue,
                        frame.Depth,
                        "NameReference",
                        nameValue.Name,
                        nameValue.SourceSpan,
                        nameValue.StableId);
                    break;
                case XamlBoundEventHandler eventHandler:
                    projection.Add(
                        XamlInspectionEntryKind.BoundValue,
                        frame.Depth,
                        "EventHandler " +
                        eventHandler.RequestedName,
                        eventHandler.Method?.ToDisplayString(
                            SymbolDisplayFormat
                                .FullyQualifiedFormat) ??
                        "<unresolved>",
                        eventHandler.SourceSpan,
                        eventHandler.StableId);
                    break;
                case XamlBoundBinding bindingValue:
                    projection.Add(
                        XamlInspectionEntryKind.BoundValue,
                        frame.Depth,
                        "Binding " + bindingValue.SourceKind,
                        XamlInspectionText.CreatePreview(
                            bindingValue.Path,
                            maximumPreviewLength),
                        bindingValue.SourceSpan,
                        bindingValue.StableId);
                    stack.Push(new BoundFrame(
                        bindingValue.Extension,
                        frame.Depth + 1));
                    break;
                case XamlBoundCompiledBinding bindingValue:
                    projection.Add(
                        XamlInspectionEntryKind.BoundValue,
                        frame.Depth,
                        "x:Bind " + bindingValue.Kind + "/" + bindingValue.Mode,
                        XamlInspectionText.CreatePreview(
                            bindingValue.Path,
                            maximumPreviewLength),
                        bindingValue.SourceSpan,
                        bindingValue.StableId);
                    stack.Push(new BoundFrame(
                        bindingValue.Extension,
                        frame.Depth + 1));
                    break;
            }
        }
        return projection.Build();
    }

    private static XamlInspectionProjection ProjectResources(
        XamlResourceGraph? graph,
        int maximumEntries,
        int maximumPreviewLength,
        CancellationToken cancellationToken)
    {
        var projection = new ProjectionBuilder(maximumEntries);
        if (graph == null) return projection.Build();
        for (var index = 0; index < graph.Scopes.Length; index++)
        {
            if ((projection.TotalCount & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var scope = graph.Scopes[index];
            projection.Add(
                XamlInspectionEntryKind.ResourceScope,
                0,
                scope.Kind + " scope",
                "parent=" + (scope.ParentId?.ToString() ?? "<root>") +
                "; owner=" + scope.OwnerStableId,
                default,
                scope.Id);
        }
        for (var index = 0; index < graph.Definitions.Length; index++)
        {
            if ((projection.TotalCount & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var definition = graph.Definitions[index];
            projection.Add(
                XamlInspectionEntryKind.ResourceDefinition,
                0,
                definition.ResourceKey.Kind + " " +
                XamlInspectionText.CreatePreview(
                    definition.Key,
                    maximumPreviewLength),
                "scope=" + definition.ScopeId +
                "; ordinal=" + definition.Ordinal,
                definition.SourceSpan,
                definition.ValueStableId);
        }
        for (var index = 0; index < graph.References.Length; index++)
        {
            if ((projection.TotalCount & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var reference = graph.References[index];
            projection.Add(
                XamlInspectionEntryKind.ResourceReference,
                0,
                reference.Kind + " " +
                XamlInspectionText.CreatePreview(
                    reference.Key,
                    maximumPreviewLength),
                reference.Resolution + "; scope=" + reference.ScopeId +
                (reference.ProviderPath == null
                    ? string.Empty
                    : "; provider=" + XamlInspectionText.CreatePreview(
                        reference.ProviderPath,
                        maximumPreviewLength)),
                reference.SourceSpan,
                reference.StableId);
        }
        return projection.Build();
    }

    private static XamlInspectionProjection ProjectIr(
        XamlIrObject? root,
        int maximumEntries,
        int maximumPreviewLength,
        CancellationToken cancellationToken)
    {
        var projection = new ProjectionBuilder(maximumEntries);
        if (root == null) return projection.Build();
        var visited = new HashSet<object>(ReferenceIdentityComparer.Instance);
        var stack = new Stack<IrFrame>();
        stack.Push(new IrFrame(root, 0));
        while (stack.Count != 0)
        {
            if ((projection.TotalCount & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var frame = stack.Pop();
            if (!visited.Add(frame.Value)) continue;
            var value = frame.Value;
            switch (value)
            {
                case XamlIrObject objectValue:
                    projection.Add(
                        XamlInspectionEntryKind.IrObject,
                        frame.Depth,
                        objectValue.Kind + " " +
                        objectValue.Type.RequestedName.DisplayName,
                        objectValue.InitializationMode + " " +
                        (objectValue.Type.Symbol?.MetadataName ?? "<unresolved>"),
                        objectValue.SourceSpan,
                        objectValue.StableId);
                    for (var index = objectValue.Operations.Length - 1; index >= 0; index--)
                        stack.Push(new IrFrame(
                            objectValue.Operations[index],
                            frame.Depth + 1));
                    break;
                case XamlIrOperation operation:
                    projection.Add(
                        XamlInspectionEntryKind.IrOperation,
                        frame.Depth,
                        operation.Kind.ToString(),
                        operation.Member.RequestedName.DisplayName,
                        operation.SourceSpan,
                        operation.StableId);
                    for (var index = operation.Values.Length - 1; index >= 0; index--)
                        stack.Push(new IrFrame(
                            operation.Values[index],
                            frame.Depth + 1));
                    break;
                case XamlIrText textValue:
                    AddIrValue(
                        projection,
                        frame,
                        "Text",
                        XamlInspectionText.CreatePreview(
                            textValue.Text,
                            maximumPreviewLength));
                    break;
                case XamlIrType typeValue:
                    AddIrValue(
                        projection,
                        frame,
                        "Type",
                        typeValue.Type.Symbol?.MetadataName ??
                        typeValue.Type.RequestedName.DisplayName);
                    break;
                case XamlIrStaticMember staticValue:
                    AddIrValue(
                        projection,
                        frame,
                        "Static",
                        staticValue.Value.Member?.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat) ??
                        staticValue.Value.RequestedName);
                    break;
                case XamlIrFactoryMethod factoryValue:
                    AddIrValue(
                        projection,
                        frame,
                        "Factory",
                        factoryValue.Value.Method?.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat) ??
                        factoryValue.Value.RequestedName);
                    break;
                case XamlIrNameReference nameValue:
                    AddIrValue(projection, frame, "NameReference", nameValue.Name);
                    break;
                case XamlIrEventHandler eventHandler:
                    AddIrValue(
                        projection,
                        frame,
                        "EventHandler",
                        eventHandler.Value.Method
                            ?.ToDisplayString(
                                SymbolDisplayFormat
                                    .FullyQualifiedFormat) ??
                        eventHandler.Value.RequestedName);
                    break;
                case XamlIrBinding bindingValue:
                    AddIrValue(
                        projection,
                        frame,
                        "Binding " + bindingValue.Binding.SourceKind,
                        XamlInspectionText.CreatePreview(
                            bindingValue.Binding.Path,
                            maximumPreviewLength));
                    stack.Push(new IrFrame(
                        bindingValue.Extension,
                        frame.Depth + 1));
                    break;
                case XamlIrCompiledBinding bindingValue:
                    AddIrValue(
                        projection,
                        frame,
                        "x:Bind " + bindingValue.Binding.Mode,
                        XamlInspectionText.CreatePreview(
                            bindingValue.Binding.Path,
                            maximumPreviewLength));
                    stack.Push(new IrFrame(
                        bindingValue.Extension,
                        frame.Depth + 1));
                    break;
                case XamlIrResourceReference resourceValue:
                    AddIrValue(
                        projection,
                        frame,
                        resourceValue.Reference.Kind + "Resource",
                        XamlInspectionText.CreatePreview(
                            resourceValue.Reference.Key,
                            maximumPreviewLength) + " " +
                        resourceValue.Reference.Resolution);
                    break;
            }
        }
        return projection.Build();
    }

    private static void AddIrValue(
        ProjectionBuilder projection,
        IrFrame frame,
        string name,
        string value)
    {
        var irValue = (XamlIrValue)frame.Value;
        projection.Add(
            XamlInspectionEntryKind.IrValue,
            frame.Depth,
            name,
            value,
            irValue.SourceSpan,
            irValue.StableId);
    }

    private static XamlInspectionProjection ProjectGeneratedSources(
        XamlCompilationResult result,
        int maximumEntries,
        CancellationToken cancellationToken)
    {
        var projection = new ProjectionBuilder(maximumEntries);
        for (var index = 0; index < result.Sources.Count; index++)
        {
            if ((index & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var source = result.Sources[index];
            projection.Add(
                XamlInspectionEntryKind.GeneratedSource,
                0,
                source.HintName,
                source.GeneratedSyntaxTree == null
                    ? "no Roslyn syntax tree"
                    : source.GeneratedSyntaxTree.GetRoot(cancellationToken)
                        .GetType().Name +
                      "; characters=" + source.Source.Length,
                default);
        }
        return projection.Build();
    }

    private static XamlInspectionProjection ProjectDiagnostics(
        IReadOnlyList<Diagnostic> diagnostics,
        int maximumEntries,
        int maximumPreviewLength,
        CancellationToken cancellationToken)
    {
        var projection = new ProjectionBuilder(maximumEntries);
        for (var index = 0; index < diagnostics.Count; index++)
        {
            if ((index & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = diagnostics[index];
            projection.Add(
                XamlInspectionEntryKind.Diagnostic,
                0,
                diagnostic.Id + " " + diagnostic.Severity,
                XamlInspectionText.CreatePreview(
                    diagnostic.GetMessage(),
                    maximumPreviewLength),
                diagnostic.Location.IsInSource
                    ? diagnostic.Location.SourceSpan
                    : default);
        }
        return projection.Build();
    }

    private static void ValidateLimit(int value, int maximum, string parameterName)
    {
        if (value <= 0 || value > maximum)
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The value must be between 1 and {maximum}.");
    }

    private readonly struct BoundFrame
    {
        public BoundFrame(object value, int depth)
        {
            Value = value;
            Depth = depth;
        }

        public object Value { get; }
        public int Depth { get; }
    }

    private readonly struct IrFrame
    {
        public IrFrame(object value, int depth)
        {
            Value = value;
            Depth = depth;
        }

        public object Value { get; }
        public int Depth { get; }
    }

    private sealed class ProjectionBuilder
    {
        private readonly int _maximumEntries;
        private readonly ImmutableArray<XamlInspectionEntry>.Builder _entries;

        public ProjectionBuilder(int maximumEntries)
        {
            _maximumEntries = maximumEntries;
            _entries = ImmutableArray.CreateBuilder<XamlInspectionEntry>(
                Math.Min(maximumEntries, 1024));
        }

        public int TotalCount { get; private set; }

        public void Add(
            XamlInspectionEntryKind kind,
            int depth,
            string name,
            string value,
            TextSpan sourceSpan,
            ulong? stableId = null)
        {
            if (_entries.Count < _maximumEntries)
                _entries.Add(new XamlInspectionEntry(
                    kind,
                    depth,
                    name,
                    value,
                    sourceSpan,
                    stableId));
            TotalCount++;
        }

        public XamlInspectionProjection Build() =>
            new XamlInspectionProjection(_entries.ToImmutable(), TotalCount);
    }

    private sealed class ReferenceIdentityComparer : IEqualityComparer<object>
    {
        public static ReferenceIdentityComparer Instance { get; } =
            new ReferenceIdentityComparer();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
