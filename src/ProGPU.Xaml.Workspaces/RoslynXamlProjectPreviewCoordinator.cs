using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Infoset;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Schema;

namespace ProGPU.Xaml.Workspaces;

public enum RoslynXamlProjectCommitResult
{
    Accepted,
    RejectedPublication,
    RejectedInvalidCandidate,
    RejectedStale,
    RejectedForeignCoordinator
}

/// <summary>
/// An immutable prepared project-preview update. A coordinator serializes runtime
/// application and commits this update as the next comparison baseline only after
/// publication succeeds.
/// </summary>
public sealed class RoslynXamlProjectPreviewUpdate
{
    internal RoslynXamlProjectPreviewUpdate(
        RoslynXamlProjectPreviewCoordinator owner,
        long baselineGeneration,
        RoslynXamlProjectPreview current,
        RoslynXamlProjectDeltaPlan? delta)
    {
        Owner = owner;
        BaselineGeneration = baselineGeneration;
        Current = current;
        Delta = delta;
        MetadataDiagnosticOrigin =
            CreateMetadataDiagnosticOrigin(delta);
    }

    internal RoslynXamlProjectPreviewCoordinator Owner { get; }
    public long BaselineGeneration { get; }
    public RoslynXamlProjectPreview Current { get; }
    public RoslynXamlProjectDeltaPlan? Delta { get; }
    public RoslynXamlMetadataEditDiagnosticOrigin?
        MetadataDiagnosticOrigin { get; }
    public bool IsInitial => Delta == null;
    public bool CanCommit =>
        Current.CanMaterialize &&
        (Delta == null ||
         Delta.Action !=
         RoslynXamlReloadAction.RetainLastGood);
    public bool RequiresRuntimePublication =>
        Delta == null ||
        Delta.RequiresTargetReplacement;
    public string? FailureMessage =>
        CanCommit
            ? null
            : Delta?.FailureMessage ??
              Current.MaterializationError ??
              "The project preview has no accepted executable artifact.";

    public bool TryGetExecutableUpdate(
        out byte[] peImage,
        out string qualifiedTypeName)
    {
        if (CanCommit &&
            Current.Artifact != null &&
            Current.QualifiedTypeName != null)
        {
            peImage =
                Current.Artifact.PeImage.ToArray();
            qualifiedTypeName =
                Current.QualifiedTypeName;
            return true;
        }

        peImage = Array.Empty<byte>();
        qualifiedTypeName = string.Empty;
        return false;
    }

    private static RoslynXamlMetadataEditDiagnosticOrigin?
        CreateMetadataDiagnosticOrigin(
            RoslynXamlProjectDeltaPlan? delta)
    {
        if (delta == null || !delta.HasSemanticXamlChanges)
        {
            return null;
        }

        foreach (RoslynXamlProjectDocumentDelta document in
                 delta.Documents)
        {
            if (document.HasSemanticChange &&
                string.Equals(
                    document.ResourceUri,
                    delta.Current.ResourceUri,
                    StringComparison.OrdinalIgnoreCase) &&
                TryCreateMetadataDiagnosticOrigin(
                    document,
                    out var origin))
            {
                return origin;
            }
        }

        foreach (RoslynXamlProjectDocumentDelta document in
                 delta.Documents)
        {
            if (document.HasSemanticChange &&
                !string.Equals(
                    document.ResourceUri,
                    delta.Current.ResourceUri,
                    StringComparison.OrdinalIgnoreCase) &&
                TryCreateMetadataDiagnosticOrigin(
                    document,
                    out var origin))
            {
                return origin;
            }
        }

        return null;
    }

    private static bool TryCreateMetadataDiagnosticOrigin(
        RoslynXamlProjectDocumentDelta document,
        out RoslynXamlMetadataEditDiagnosticOrigin? origin)
    {
        return TryCreateMetadataDiagnosticOrigin(
                   document.Current,
                   document.StableIdentities.Modified,
                   out origin) ||
               TryCreateMetadataDiagnosticOrigin(
                   document.Current,
                   document.StableIdentities.Added,
                   out origin) ||
               TryCreateMetadataDiagnosticOrigin(
                   document.Previous,
                   document.StableIdentities.Removed,
                   out origin);
    }

    private static bool TryCreateMetadataDiagnosticOrigin(
        RoslynXamlProjectDocumentCompilation? document,
        ImmutableArray<ulong> stableIdentities,
        out RoslynXamlMetadataEditDiagnosticOrigin? origin)
    {
        origin = null;
        XamlInfosetObject? root =
            document?.SourceInspection.Infoset.Root;
        if (root == null || stableIdentities.IsDefaultOrEmpty)
        {
            return false;
        }

        var identities = new HashSet<ulong>(stableIdentities);
        var stack = new Stack<XamlInfosetValue>();
        stack.Push(root);
        TextSpan? bestSpan = null;
        while (stack.Count != 0)
        {
            XamlInfosetValue value = stack.Pop();
            if (identities.Contains(value.StableId) &&
                (!bestSpan.HasValue ||
                 value.SourceSpan.Start < bestSpan.Value.Start ||
                 (value.SourceSpan.Start == bestSpan.Value.Start &&
                  value.SourceSpan.Length < bestSpan.Value.Length)))
            {
                bestSpan = value.SourceSpan;
            }

            if (value is XamlInfosetObject objectValue)
            {
                for (var index = objectValue.Members.Length - 1;
                     index >= 0;
                     index--)
                {
                    stack.Push(objectValue.Members[index]);
                }
            }
            else if (value is XamlInfosetMember member)
            {
                for (var index = member.Values.Length - 1;
                     index >= 0;
                     index--)
                {
                    stack.Push(member.Values[index]);
                }
            }
        }

        if (!bestSpan.HasValue)
        {
            return false;
        }

        XamlInfosetDocument infoset =
            document!.SourceInspection.Infoset;
        origin = new RoslynXamlMetadataEditDiagnosticOrigin(
            infoset.Path,
            infoset.SourceText,
            bestSpan.Value);
        return true;
    }
}

/// <summary>
/// Owns only the last host-confirmed immutable project-preview snapshot. Expensive
/// compilation runs outside the coordinator lock; optimistic generation checks reject
/// stale or foreign commits without publishing compiler state.
/// </summary>
public sealed class RoslynXamlProjectPreviewCoordinator
{
    private readonly object _gate = new object();
    private readonly SemaphoreSlim _commitGate =
        new SemaphoreSlim(1, 1);
    private readonly IRoslynXamlFrameworkProfile _profile;
    private readonly RoslynXamlProjectPreviewOptions _options;
    private readonly RoslynXamlProjectPreviewService _previewService;
    private readonly RoslynXamlProjectDeltaService _deltaService;
    private RoslynXamlProjectPreview? _accepted;
    private long _generation;

    public RoslynXamlProjectPreviewCoordinator(
        IRoslynXamlFrameworkProfile profile,
        RoslynXamlProjectPreviewOptions? options = null)
        : this(
            profile,
            options,
            new RoslynXamlProjectPreviewService(),
            new RoslynXamlProjectDeltaService())
    {
    }

    internal RoslynXamlProjectPreviewCoordinator(
        IRoslynXamlFrameworkProfile profile,
        RoslynXamlProjectPreviewOptions? options,
        RoslynXamlProjectPreviewService previewService,
        RoslynXamlProjectDeltaService deltaService)
    {
        _profile =
            profile ??
            throw new ArgumentNullException(nameof(profile));
        _options = SnapshotOptions(options);
        _previewService =
            previewService ??
            throw new ArgumentNullException(
                nameof(previewService));
        _deltaService =
            deltaService ??
            throw new ArgumentNullException(
                nameof(deltaService));
    }

    public long Generation
    {
        get
        {
            lock (_gate)
                return _generation;
        }
    }

    public RoslynXamlProjectPreview? LastAccepted
    {
        get
        {
            lock (_gate)
                return _accepted;
        }
    }

    public async Task<RoslynXamlProjectPreviewUpdate>
        PrepareAsync(
            Project project,
            DocumentId xamlDocumentId,
            SourceText? unsavedText = null,
            CancellationToken cancellationToken = default)
    {
        if (project == null)
            throw new ArgumentNullException(nameof(project));
        if (xamlDocumentId == null)
        {
            throw new ArgumentNullException(
                nameof(xamlDocumentId));
        }

        RoslynXamlProjectPreview? baseline;
        long baselineGeneration;
        lock (_gate)
        {
            baseline = _accepted;
            baselineGeneration = _generation;
        }

        var operationOptions =
            SnapshotOptions(_options);
        operationOptions.EditedText =
            unsavedText ??
            _options.EditedText;
        var current = await _previewService.CompileAsync(
                project,
                xamlDocumentId,
                _profile,
                operationOptions,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var delta = baseline == null
            ? null
            : _deltaService.CreatePlan(
                baseline,
                current,
                cancellationToken);
        return new RoslynXamlProjectPreviewUpdate(
            this,
            baselineGeneration,
            current,
            delta);
    }

    public RoslynXamlProjectCommitResult TryCommit(
        RoslynXamlProjectPreviewUpdate update)
    {
        if (update == null)
            throw new ArgumentNullException(nameof(update));
        _commitGate.Wait();
        try
        {
            lock (_gate)
                return TryCommitCore(update);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <summary>
    /// Serializes the host publication and baseline commit. A stale, foreign, or invalid
    /// update is rejected before <paramref name="publishAsync"/> can mutate runtime state.
    /// A false result or exception leaves the baseline unchanged. Cancellation is observed
    /// before publication; once the publisher reports success the matching compiler
    /// baseline commits so runtime and compiler state cannot diverge.
    /// </summary>
    public async Task<RoslynXamlProjectCommitResult>
        ApplyAsync(
            RoslynXamlProjectPreviewUpdate update,
            Func<
                RoslynXamlProjectPreviewUpdate,
                CancellationToken,
                Task<bool>> publishAsync,
            CancellationToken cancellationToken = default)
    {
        if (update == null)
            throw new ArgumentNullException(nameof(update));
        if (publishAsync == null)
        {
            throw new ArgumentNullException(
                nameof(publishAsync));
        }

        await _commitGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            RoslynXamlProjectCommitResult validation;
            lock (_gate)
                validation = ValidateCommit(update);
            if (validation !=
                RoslynXamlProjectCommitResult.Accepted)
            {
                return validation;
            }

            if (!await publishAsync(
                    update,
                    cancellationToken)
                    .ConfigureAwait(false))
            {
                return RoslynXamlProjectCommitResult
                    .RejectedPublication;
            }

            lock (_gate)
                return TryCommitCore(update);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    private RoslynXamlProjectCommitResult TryCommitCore(
        RoslynXamlProjectPreviewUpdate update)
    {
        var validation = ValidateCommit(update);
        if (validation !=
            RoslynXamlProjectCommitResult.Accepted)
        {
            return validation;
        }

        _accepted = update.Current;
        checked
        {
            _generation++;
        }

        return RoslynXamlProjectCommitResult.Accepted;
    }

    private RoslynXamlProjectCommitResult ValidateCommit(
        RoslynXamlProjectPreviewUpdate update)
    {
        if (!ReferenceEquals(update.Owner, this))
        {
            return RoslynXamlProjectCommitResult
                .RejectedForeignCoordinator;
        }

        if (!update.CanCommit)
        {
            return RoslynXamlProjectCommitResult
                .RejectedInvalidCandidate;
        }

        return update.BaselineGeneration != _generation
            ? RoslynXamlProjectCommitResult.RejectedStale
            : RoslynXamlProjectCommitResult.Accepted;
    }

    private static RoslynXamlProjectPreviewOptions
        SnapshotOptions(
            RoslynXamlProjectPreviewOptions? options)
    {
        options =
            options ??
            new RoslynXamlProjectPreviewOptions();
        var inspection =
            options.InspectionOptions ??
            throw new ArgumentNullException(
                nameof(options.InspectionOptions));
        var compiler =
            inspection.CompilerOptions ??
            throw new ArgumentNullException(
                nameof(inspection.CompilerOptions));
        return new RoslynXamlProjectPreviewOptions
        {
            EditedText = options.EditedText,
            EmitArtifact = options.EmitArtifact,
            LogicalPathProvider =
                options.LogicalPathProvider,
            InspectionOptions =
                new RoslynXamlCompilationInspectionOptions
                {
                    MaximumProjectionEntries =
                        inspection.MaximumProjectionEntries,
                    MaximumPreviewLength =
                        inspection.MaximumPreviewLength,
                    CompilerOptions =
                        new XamlCompilerOptions
                        {
                            Framework = compiler.Framework,
                            ResourceUri =
                                compiler.ResourceUri,
                            ResourceDependencies =
                                compiler
                                    .ResourceDependencies,
                            Strict = compiler.Strict,
                            EmitHotReloadHooks =
                                compiler
                                    .EmitHotReloadHooks,
                            EmitSourceComments =
                                compiler
                                    .EmitSourceComments,
                            StaticResourceForwardReferenceMode =
                                compiler
                                    .StaticResourceForwardReferenceMode
                        }
                }
        };
    }
}
