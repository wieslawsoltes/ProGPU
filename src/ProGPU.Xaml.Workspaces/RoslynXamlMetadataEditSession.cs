using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace ProGPU.Xaml.Workspaces;

[Flags]
public enum RoslynXamlMetadataEditCapabilities
{
    None = 0,
    UpdateMethodBody = 1,
    UpdatePropertyAccessor = 2,
    UpdateEventAccessor = 4,
    UpdateConstructorBody = 8,
    UpdateDestructorBody = 16,
    UpdateOperatorBody = 32,
    AddMethodToExistingType = 64
}

public enum RoslynXamlMetadataDeltaStatus
{
    NoChanges,
    Ready,
    RejectedCompilation,
    RejectedUnsupportedEdit,
    RejectedEmit
}

public enum RoslynXamlMetadataDeltaCommitResult
{
    Accepted,
    RejectedInvalidCandidate,
    RejectedStale,
    RejectedForeignSession,
    RejectedDisposed
}

/// <summary>
/// An immutable metadata/IL/PDB delta prepared against one accepted generation.
/// The payload owns detached bounded byte arrays and does not retain streams.
/// </summary>
public sealed class RoslynXamlMetadataDeltaUpdate
{
    internal RoslynXamlMetadataDeltaUpdate(
        RoslynXamlMetadataEditSession owner,
        long baselineGeneration,
        Compilation compilation,
        EmitBaseline baseline,
        ImmutableHashSet<string> addedMethodKeys,
        RoslynXamlMetadataDeltaStatus status,
        ImmutableArray<byte> metadataDelta,
        ImmutableArray<byte> ilDelta,
        ImmutableArray<byte> pdbDelta,
        ImmutableArray<int> updatedMethodTokens,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Owner = owner;
        BaselineGeneration = baselineGeneration;
        Compilation = compilation;
        Baseline = baseline;
        AddedMethodKeys = addedMethodKeys;
        Status = status;
        MetadataDelta = metadataDelta;
        IlDelta = ilDelta;
        PdbDelta = pdbDelta;
        UpdatedMethodTokens = updatedMethodTokens;
        Diagnostics = diagnostics;
    }

    internal RoslynXamlMetadataEditSession Owner { get; }

    internal Compilation Compilation { get; }

    internal EmitBaseline Baseline { get; }

    internal ImmutableHashSet<string> AddedMethodKeys { get; }

    public long BaselineGeneration { get; }

    public RoslynXamlMetadataDeltaStatus Status { get; }

    public ImmutableArray<byte> MetadataDelta { get; }

    public ImmutableArray<byte> IlDelta { get; }

    public ImmutableArray<byte> PdbDelta { get; }

    public ImmutableArray<int> UpdatedMethodTokens { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public bool HasChanges =>
        Status == RoslynXamlMetadataDeltaStatus.Ready;

    public bool CanCommit =>
        Status == RoslynXamlMetadataDeltaStatus.NoChanges ||
        Status == RoslynXamlMetadataDeltaStatus.Ready;
}

/// <summary>
/// Owns one accepted Roslyn compilation and its Edit-and-Continue baseline.
/// This producer slice accepts ordinary C# method bodies, property/indexer
/// accessor bodies, custom event accessor bodies, constructors, destructors,
/// user-defined operators, and insertion of non-virtual ordinary methods into
/// existing types. Candidate compilation, declaration shape, and Roslyn emit
/// validation complete before a host can observe the detached delta or
/// advance the baseline.
/// </summary>
/// <remarks>
/// Initial emission is O(T + B) time and O(B) retained storage for T syntax
/// tokens and B PE bytes. Preparation is O(T + M log M + A + D) time and
/// O(T + M + A + D) temporary/output storage for M methods, A accepted added
/// method identities, and D delta bytes. The session retains one compilation,
/// one baseline, one initial module, and O(A) exact keys; committing replaces
/// rather than accumulates generations.
/// </remarks>
public sealed class RoslynXamlMetadataEditSession : IDisposable
{
#pragma warning disable RS2008
    private static readonly DiagnosticDescriptor UnsupportedEditDescriptor =
        new DiagnosticDescriptor(
            "PGXAML8010",
            "Metadata edit requires restart",
            "The metadata edit requires restart: {0}",
            "ProGPU.Xaml.Tooling",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
#pragma warning restore RS2008

    private readonly object _gate = new object();
    private readonly MemoryStream _initialPeStream;
    private readonly PEReader _initialPeReader;
    private readonly ModuleMetadata _module;
    private Compilation _compilation;
    private EmitBaseline _baseline;
    private ImmutableHashSet<string> _addedMethodKeys =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private long _generation;
    private bool _disposed;

    public RoslynXamlMetadataEditSession(
        Compilation initialCompilation,
        CancellationToken cancellationToken = default)
    {
        _compilation = initialCompilation ??
            throw new ArgumentNullException(
                nameof(initialCompilation));
        EnsureCSharpCompilation(initialCompilation);
        cancellationToken.ThrowIfCancellationRequested();

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        EmitResult result = initialCompilation.Emit(
            peStream,
            pdbStream,
            options: new EmitOptions(
                debugInformationFormat:
                    DebugInformationFormat.PortablePdb),
            cancellationToken: cancellationToken);
        if (!result.Success)
        {
            throw new ArgumentException(
                "The initial compilation cannot establish a metadata " +
                "baseline: " + FormatFirstError(result.Diagnostics),
                nameof(initialCompilation));
        }

        InitialPeImage = ImmutableArray.CreateRange(
            peStream.ToArray());
        InitialPdbImage = ImmutableArray.CreateRange(
            pdbStream.ToArray());
        _initialPeStream = new MemoryStream(
            InitialPeImage.ToArray(),
            writable: false);
        _initialPeReader = new PEReader(
            _initialPeStream,
            PEStreamOptions.LeaveOpen);
        _module = ModuleMetadata.CreateFromImage(
            InitialPeImage);
        _baseline = EmitBaseline.CreateInitialBaseline(
            initialCompilation,
            _module,
            static _ =>
                EditAndContinueMethodDebugInformation
                    .Create(
                        ImmutableArray<byte>.Empty,
                        ImmutableArray<byte>.Empty),
            GetLocalSignature,
            hasPortableDebugInformation: true);
    }

    public ImmutableArray<byte> InitialPeImage { get; }

    public ImmutableArray<byte> InitialPdbImage { get; }

    public RoslynXamlMetadataEditCapabilities Capabilities =>
        RoslynXamlMetadataEditCapabilities.UpdateMethodBody |
        RoslynXamlMetadataEditCapabilities.UpdatePropertyAccessor |
        RoslynXamlMetadataEditCapabilities.UpdateEventAccessor |
        RoslynXamlMetadataEditCapabilities.UpdateConstructorBody |
        RoslynXamlMetadataEditCapabilities.UpdateDestructorBody |
        RoslynXamlMetadataEditCapabilities.UpdateOperatorBody |
        RoslynXamlMetadataEditCapabilities.AddMethodToExistingType;

    public long Generation
    {
        get
        {
            lock (_gate)
                return _generation;
        }
    }

    public RoslynXamlMetadataDeltaUpdate Prepare(
        Compilation candidate,
        CancellationToken cancellationToken = default)
    {
        if (candidate == null)
            throw new ArgumentNullException(nameof(candidate));
        EnsureCSharpCompilation(candidate);

        Compilation previous;
        EmitBaseline baseline;
        ImmutableHashSet<string> addedMethodKeys;
        long generation;
        lock (_gate)
        {
            ThrowIfDisposed();
            previous = _compilation;
            baseline = _baseline;
            addedMethodKeys = _addedMethodKeys;
            generation = _generation;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var compilationErrors = candidate
            .GetDiagnostics(cancellationToken)
            .Where(
                static diagnostic =>
                    diagnostic.Severity ==
                    DiagnosticSeverity.Error)
            .ToImmutableArray();
        if (compilationErrors.Length != 0)
        {
            return CreateRejected(
                generation,
                candidate,
                baseline,
                addedMethodKeys,
                RoslynXamlMetadataDeltaStatus
                    .RejectedCompilation,
                compilationErrors);
        }

        if (!HasEquivalentEnvironment(
                previous,
                candidate))
        {
            return CreateUnsupported(
                generation,
                candidate,
                baseline,
                addedMethodKeys,
                "assembly identity, compilation options, or metadata " +
                "references changed");
        }

        CompilationShape previousShape =
            CompilationShape.Create(
                previous,
                cancellationToken);
        CompilationShape candidateShape =
            CompilationShape.Create(
                candidate,
                cancellationToken);
        if (!string.Equals(
                previousShape.DeclarationFingerprint,
                candidateShape.DeclarationFingerprint,
                StringComparison.Ordinal))
        {
            return CreateUnsupported(
                generation,
                candidate,
                baseline,
                addedMethodKeys,
                "a declaration, member signature, non-method body, or " +
                "syntax-tree topology changed");
        }

        foreach (string key in previousShape.Methods.Keys)
        {
            if (!candidateShape.Methods.TryGetValue(
                    key,
                    out MethodBodyRecord? candidateMethod))
            {
                return CreateUnsupported(
                    generation,
                    candidate,
                    baseline,
                    addedMethodKeys,
                    "the declared method set removed '" + key + "'");
            }
            if (!string.Equals(
                    previousShape.Methods[key]
                        .DeclarationFingerprint,
                    candidateMethod.DeclarationFingerprint,
                    StringComparison.Ordinal))
            {
                return CreateUnsupported(
                    generation,
                    candidate,
                    baseline,
                    addedMethodKeys,
                    "the declaration of '" + key + "' changed");
            }
        }

        var edits = ImmutableArray.CreateBuilder<SemanticEdit>();
        foreach (string key in previousShape.Methods.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MethodBodyRecord oldMethod =
                previousShape.Methods[key];
            MethodBodyRecord newMethod =
                candidateShape.Methods[key];
            if (string.Equals(
                    oldMethod.BodyFingerprint,
                    newMethod.BodyFingerprint,
                    StringComparison.Ordinal))
            {
                continue;
            }

            edits.Add(new SemanticEdit(
                SemanticEditKind.Update,
                oldMethod.Symbol,
                newMethod.Symbol,
                syntaxMap: null,
                runtimeRudeEdit: null,
                instrumentation: default));
        }

        ImmutableHashSet<string>.Builder nextAddedMethodKeys =
            addedMethodKeys.ToBuilder();
        foreach (KeyValuePair<string, MethodBodyRecord> item in
                 candidateShape.Methods)
        {
            if (previousShape.Methods.ContainsKey(item.Key))
                continue;
            if (!item.Value.SupportsInsertion)
            {
                return CreateUnsupported(
                    generation,
                    candidate,
                    baseline,
                    addedMethodKeys,
                    "the added member '" + item.Key + "' is not a " +
                    "non-virtual ordinary method");
            }

            edits.Add(new SemanticEdit(
                SemanticEditKind.Insert,
                oldSymbol: null,
                item.Value.Symbol,
                syntaxMap: null,
                runtimeRudeEdit: null,
                instrumentation: default));
            nextAddedMethodKeys.Add(item.Key);
        }

        ImmutableHashSet<string> nextAddedMethods =
            nextAddedMethodKeys.ToImmutable();

        if (edits.Count == 0)
        {
            return new RoslynXamlMetadataDeltaUpdate(
                this,
                generation,
                candidate,
                baseline,
                nextAddedMethods,
                RoslynXamlMetadataDeltaStatus.NoChanges,
                ImmutableArray<byte>.Empty,
                ImmutableArray<byte>.Empty,
                ImmutableArray<byte>.Empty,
                ImmutableArray<int>.Empty,
                ImmutableArray<Diagnostic>.Empty);
        }

        using var metadata = new MemoryStream();
        using var il = new MemoryStream();
        using var pdb = new MemoryStream();
        var previouslyAddedSymbols = previousShape.Methods
            .Where(item => addedMethodKeys.Contains(item.Key))
            .Select(static item => (ISymbol)item.Value.Symbol)
            .ToImmutableHashSet(SymbolEqualityComparer.Default);
        EmitDifferenceResult emit = candidate.EmitDifference(
            baseline,
            edits,
            previouslyAddedSymbols.Contains,
            metadata,
            il,
            pdb,
            cancellationToken);
        var diagnostics = emit.Diagnostics.ToImmutableArray();
        if (!emit.Success)
        {
            return CreateRejected(
                generation,
                candidate,
                baseline,
                addedMethodKeys,
                RoslynXamlMetadataDeltaStatus.RejectedEmit,
                diagnostics);
        }

        return new RoslynXamlMetadataDeltaUpdate(
            this,
            generation,
            candidate,
            emit.Baseline!,
            nextAddedMethods,
            RoslynXamlMetadataDeltaStatus.Ready,
            ImmutableArray.CreateRange(metadata.ToArray()),
            ImmutableArray.CreateRange(il.ToArray()),
            ImmutableArray.CreateRange(pdb.ToArray()),
            emit.UpdatedMethods
                .Select(
                    static handle =>
                        MetadataTokens.GetToken(handle))
                .OrderBy(static token => token)
                .ToImmutableArray(),
            diagnostics);
    }

    public RoslynXamlMetadataDeltaCommitResult TryCommit(
        RoslynXamlMetadataDeltaUpdate update)
    {
        if (update == null)
            throw new ArgumentNullException(nameof(update));
        lock (_gate)
        {
            if (_disposed)
            {
                return RoslynXamlMetadataDeltaCommitResult
                    .RejectedDisposed;
            }
            if (!ReferenceEquals(update.Owner, this))
            {
                return RoslynXamlMetadataDeltaCommitResult
                    .RejectedForeignSession;
            }
            if (!update.CanCommit)
            {
                return RoslynXamlMetadataDeltaCommitResult
                    .RejectedInvalidCandidate;
            }
            if (update.BaselineGeneration != _generation)
            {
                return RoslynXamlMetadataDeltaCommitResult
                    .RejectedStale;
            }

            _compilation = update.Compilation;
            _baseline = update.Baseline;
            _addedMethodKeys = update.AddedMethodKeys;
            checked
            {
                _generation++;
            }
            return RoslynXamlMetadataDeltaCommitResult.Accepted;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _module.Dispose();
            _initialPeReader.Dispose();
            _initialPeStream.Dispose();
        }
    }

    private StandaloneSignatureHandle GetLocalSignature(
        MethodDefinitionHandle methodHandle)
    {
        MetadataReader reader =
            _initialPeReader.GetMetadataReader();
        MethodDefinition method =
            reader.GetMethodDefinition(methodHandle);
        if (method.RelativeVirtualAddress == 0)
            return default;
        MethodBodyBlock body = _initialPeReader
            .GetMethodBody(method.RelativeVirtualAddress);
        return body.LocalSignature;
    }

    private RoslynXamlMetadataDeltaUpdate CreateUnsupported(
        long generation,
        Compilation candidate,
        EmitBaseline baseline,
        ImmutableHashSet<string> addedMethodKeys,
        string reason) =>
        CreateRejected(
            generation,
            candidate,
            baseline,
            addedMethodKeys,
            RoslynXamlMetadataDeltaStatus
                .RejectedUnsupportedEdit,
            ImmutableArray.Create(
                Diagnostic.Create(
                    UnsupportedEditDescriptor,
                    Location.None,
                    reason)));

    private RoslynXamlMetadataDeltaUpdate CreateRejected(
        long generation,
        Compilation candidate,
        EmitBaseline baseline,
        ImmutableHashSet<string> addedMethodKeys,
        RoslynXamlMetadataDeltaStatus status,
        ImmutableArray<Diagnostic> diagnostics) =>
        new RoslynXamlMetadataDeltaUpdate(
            this,
            generation,
            candidate,
            baseline,
            addedMethodKeys,
            status,
            ImmutableArray<byte>.Empty,
            ImmutableArray<byte>.Empty,
            ImmutableArray<byte>.Empty,
            ImmutableArray<int>.Empty,
            diagnostics);

    private static bool HasEquivalentEnvironment(
        Compilation previous,
        Compilation candidate)
    {
        if (!string.Equals(
                previous.AssemblyName,
                candidate.AssemblyName,
                StringComparison.Ordinal) ||
            !Equals(previous.Options, candidate.Options))
        {
            return false;
        }

        var oldReferences = GetReferenceIdentities(previous);
        var newReferences = GetReferenceIdentities(candidate);
        return oldReferences.SequenceEqual(
            newReferences,
            StringComparer.Ordinal);
    }

    private static ImmutableArray<string> GetReferenceIdentities(
        Compilation compilation) => compilation.References
        .Select(
            reference =>
            {
                ISymbol? symbol = compilation
                    .GetAssemblyOrModuleSymbol(reference);
                string symbolIdentity = symbol switch
                {
                    IAssemblySymbol assembly =>
                        assembly.Identity.ToString(),
                    IModuleSymbol module => module.Name,
                    _ => string.Empty
                };
                return (reference.Display ?? string.Empty) + "|" +
                    string.Join(
                        ",",
                        reference.Properties.Aliases) + "|" +
                    reference.Properties.EmbedInteropTypes + "|" +
                    symbolIdentity;
            })
        .OrderBy(static value => value, StringComparer.Ordinal)
        .ToImmutableArray();

    private static void EnsureCSharpCompilation(
        Compilation compilation)
    {
        if (compilation is not CSharpCompilation)
        {
            throw new NotSupportedException(
                "The initial metadata producer supports C# " +
                "compilations only.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(RoslynXamlMetadataEditSession));
        }
    }

    private static string FormatFirstError(
        ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.FirstOrDefault(
                static diagnostic =>
                    diagnostic.Severity ==
                    DiagnosticSeverity.Error)?
            .GetMessage() ??
        "unknown emit failure";

    private sealed class CompilationShape
    {
        private CompilationShape(
            string declarationFingerprint,
            ImmutableSortedDictionary<
                string,
                MethodBodyRecord> methods)
        {
            DeclarationFingerprint =
                declarationFingerprint;
            Methods = methods;
        }

        public string DeclarationFingerprint { get; }

        public ImmutableSortedDictionary<
            string,
            MethodBodyRecord> Methods
        { get; }

        public static CompilationShape Create(
            Compilation compilation,
            CancellationToken cancellationToken)
        {
            var declaration = new StringBuilder();
            var methods =
                ImmutableSortedDictionary.CreateBuilder<
                    string,
                    MethodBodyRecord>(
                    StringComparer.Ordinal);
            int ordinal = 0;
            foreach (SyntaxTree tree in compilation.SyntaxTrees
                         .OrderBy(
                             static item => item.FilePath,
                             StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyntaxNode root = tree.GetRoot(
                    cancellationToken);
                declaration.Append(tree.FilePath)
                    .Append('|')
                    .Append(ordinal++)
                    .Append('|');
                AppendParseOptions(
                    declaration,
                    tree.Options);
                AppendTokens(
                    declaration,
                    CompilationDeclarationEraser.Instance.Visit(root)!);
                declaration.AppendLine();

                SemanticModel model =
                    compilation.GetSemanticModel(tree);
                foreach (SyntaxNode node in root.DescendantNodes())
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    if (!TryGetExecutableBody(
                            model,
                            node,
                            cancellationToken,
                            out IMethodSymbol symbol,
                            out SyntaxNode bodyNode))
                    {
                        continue;
                    }

                    string key =
                        DocumentationCommentId
                            .CreateDeclarationId(symbol) ??
                        symbol.ToDisplayString(
                            SymbolDisplayFormat
                                .FullyQualifiedFormat);
                    var body = new StringBuilder();
                    AppendTokens(body, bodyNode);
                    var methodDeclaration = new StringBuilder();
                    AppendTokens(
                        methodDeclaration,
                        ExecutableBodyEraser.Instance.Visit(node)!);
                    if (methods.ContainsKey(key))
                    {
                        throw new InvalidOperationException(
                            "Metadata method identity is not unique: '" +
                            key + "'.");
                    }
                    methods.Add(
                        key,
                        new MethodBodyRecord(
                            symbol,
                            body.ToString(),
                            methodDeclaration.ToString(),
                            SupportsInsertion(node, symbol)));
                }
            }

            return new CompilationShape(
                declaration.ToString(),
                methods.ToImmutable());
        }

        private static bool SupportsInsertion(
            SyntaxNode node,
            IMethodSymbol symbol) =>
            node is MethodDeclarationSyntax &&
            symbol.MethodKind == MethodKind.Ordinary &&
            !symbol.IsAbstract &&
            !symbol.IsVirtual &&
            !symbol.IsOverride &&
            symbol.ExplicitInterfaceImplementations.IsEmpty;

        private static bool TryGetExecutableBody(
            SemanticModel model,
            SyntaxNode node,
            CancellationToken cancellationToken,
            out IMethodSymbol symbol,
            out SyntaxNode body)
        {
            symbol = null!;
            body = null!;
            IMethodSymbol? candidateSymbol = null;
            SyntaxNode? candidateBody = null;
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    candidateBody = (SyntaxNode?)method.Body ??
                        method.ExpressionBody;
                    if (candidateBody != null)
                    {
                        candidateSymbol = model.GetDeclaredSymbol(
                            method,
                            cancellationToken);
                    }
                    break;
                case ConstructorDeclarationSyntax constructor:
                    candidateBody = (SyntaxNode?)constructor.Body ??
                        constructor.ExpressionBody;
                    if (candidateBody != null)
                    {
                        candidateSymbol = model.GetDeclaredSymbol(
                            constructor,
                            cancellationToken);
                    }
                    break;
                case DestructorDeclarationSyntax destructor:
                    candidateBody = (SyntaxNode?)destructor.Body ??
                        destructor.ExpressionBody;
                    if (candidateBody != null)
                    {
                        candidateSymbol = model.GetDeclaredSymbol(
                            destructor,
                            cancellationToken);
                    }
                    break;
                case OperatorDeclarationSyntax operatorDeclaration:
                    candidateBody =
                        (SyntaxNode?)operatorDeclaration.Body ??
                        operatorDeclaration.ExpressionBody;
                    if (candidateBody != null)
                    {
                        candidateSymbol = model.GetDeclaredSymbol(
                            operatorDeclaration,
                            cancellationToken);
                    }
                    break;
                case ConversionOperatorDeclarationSyntax conversion:
                    candidateBody = (SyntaxNode?)conversion.Body ??
                        conversion.ExpressionBody;
                    if (candidateBody != null)
                    {
                        candidateSymbol = model.GetDeclaredSymbol(
                            conversion,
                            cancellationToken);
                    }
                    break;
                case AccessorDeclarationSyntax accessor
                    when accessor.Parent?.Parent is
                        PropertyDeclarationSyntax or
                        IndexerDeclarationSyntax or
                        EventDeclarationSyntax:
                    candidateBody = (SyntaxNode?)accessor.Body ??
                        accessor.ExpressionBody;
                    if (candidateBody != null)
                    {
                        candidateSymbol = model.GetDeclaredSymbol(
                            accessor,
                            cancellationToken);
                    }
                    break;
                case PropertyDeclarationSyntax property
                    when property.ExpressionBody != null:
                    candidateBody = property.ExpressionBody;
                    candidateSymbol = model.GetDeclaredSymbol(
                            property,
                            cancellationToken)?
                        .GetMethod;
                    break;
                case IndexerDeclarationSyntax indexer
                    when indexer.ExpressionBody != null:
                    candidateBody = indexer.ExpressionBody;
                    candidateSymbol = model.GetDeclaredSymbol(
                            indexer,
                            cancellationToken)?
                        .GetMethod;
                    break;
            }

            if (candidateSymbol is null || candidateBody is null)
                return false;
            symbol = candidateSymbol;
            body = candidateBody;
            return true;
        }

        private static void AppendTokens(
            StringBuilder builder,
            SyntaxNode node)
        {
            foreach (SyntaxToken token in node.DescendantTokens())
            {
                builder.Append(token.RawKind)
                    .Append(':')
                    .Append(token.ValueText.Length)
                    .Append(':')
                    .Append(token.ValueText)
                    .Append(';');
            }
        }

        private static void AppendParseOptions(
            StringBuilder builder,
            ParseOptions options)
        {
            builder.Append(options.Kind)
                .Append('|')
                .Append(options.DocumentationMode)
                .Append('|');
            if (options is not CSharpParseOptions csharp)
                return;
            builder.Append(csharp.LanguageVersion)
                .Append('|');
            foreach (string symbol in csharp
                         .PreprocessorSymbolNames
                         .OrderBy(
                             static value => value,
                             StringComparer.Ordinal))
            {
                builder.Append(symbol)
                    .Append(';');
            }
            builder.Append('|');
            foreach (KeyValuePair<string, string> feature in
                     csharp.Features.OrderBy(
                         static item => item.Key,
                         StringComparer.Ordinal))
            {
                builder.Append(feature.Key)
                    .Append('=')
                    .Append(feature.Value)
                    .Append(';');
            }
            builder.Append('|');
        }
    }

    private sealed class MethodBodyRecord
    {
        public MethodBodyRecord(
            IMethodSymbol symbol,
            string bodyFingerprint,
            string declarationFingerprint,
            bool supportsInsertion)
        {
            Symbol = symbol;
            BodyFingerprint = bodyFingerprint;
            DeclarationFingerprint = declarationFingerprint;
            SupportsInsertion = supportsInsertion;
        }

        public IMethodSymbol Symbol { get; }

        public string BodyFingerprint { get; }

        public string DeclarationFingerprint { get; }

        public bool SupportsInsertion { get; }
    }

    private sealed class CompilationDeclarationEraser :
        ExecutableBodyEraser
    {
        public new static CompilationDeclarationEraser Instance { get; } =
            new();

        public override SyntaxNode? VisitMethodDeclaration(
            MethodDeclarationSyntax node) =>
            node.Body is not null || node.ExpressionBody is not null
                ? null
                : base.VisitMethodDeclaration(node);
    }

    private class ExecutableBodyEraser : CSharpSyntaxRewriter
    {
        public static ExecutableBodyEraser Instance { get; } = new();

        public override SyntaxNode? VisitMethodDeclaration(
            MethodDeclarationSyntax node) =>
            node.WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(
                    SyntaxFactory.Token(
                        SyntaxKind.SemicolonToken));

        public override SyntaxNode? VisitConstructorDeclaration(
            ConstructorDeclarationSyntax node) =>
            node.WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(
                    SyntaxFactory.Token(
                        SyntaxKind.SemicolonToken));

        public override SyntaxNode? VisitDestructorDeclaration(
            DestructorDeclarationSyntax node) =>
            node.WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(
                    SyntaxFactory.Token(
                        SyntaxKind.SemicolonToken));

        public override SyntaxNode? VisitOperatorDeclaration(
            OperatorDeclarationSyntax node) =>
            node.WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(
                    SyntaxFactory.Token(
                        SyntaxKind.SemicolonToken));

        public override SyntaxNode? VisitConversionOperatorDeclaration(
            ConversionOperatorDeclarationSyntax node) =>
            node.WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(
                    SyntaxFactory.Token(
                        SyntaxKind.SemicolonToken));

        public override SyntaxNode? VisitAccessorDeclaration(
            AccessorDeclarationSyntax node)
        {
            if (node.Parent?.Parent is not
                (PropertyDeclarationSyntax or
                IndexerDeclarationSyntax or
                EventDeclarationSyntax))
            {
                return base.VisitAccessorDeclaration(node);
            }

            return node.WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(
                        SyntaxFactory.Token(
                            SyntaxKind.SemicolonToken));
        }

        public override SyntaxNode? VisitPropertyDeclaration(
            PropertyDeclarationSyntax node) =>
            base.VisitPropertyDeclaration(
                node.WithExpressionBody(null));

        public override SyntaxNode? VisitIndexerDeclaration(
            IndexerDeclarationSyntax node) =>
            base.VisitIndexerDeclaration(
                node.WithExpressionBody(null));
    }
}
