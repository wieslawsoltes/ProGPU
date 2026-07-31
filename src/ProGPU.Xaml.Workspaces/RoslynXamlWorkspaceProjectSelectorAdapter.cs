using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ProGPU.Xaml.Workspaces;

/// <summary>
/// Describes whether the current IDE/project-system selection can produce a project-watch
/// request from the owning workspace's current immutable solution.
/// </summary>
public enum RoslynXamlWorkspaceSelectionStatus
{
    Empty,
    Ready,
    ProjectUnavailable,
    DocumentUnavailable,
    NotAdditionalDocument,
    NotXamlDocument,
    AmbiguousPath
}

/// <summary>
/// A small immutable view of the current project-system selection. The snapshot retains
/// stable Roslyn identifiers and normalized paths, but no solution, project, document,
/// compilation, syntax tree, or workspace graph.
/// </summary>
public readonly struct RoslynXamlWorkspaceSelectionSnapshot
{
    internal RoslynXamlWorkspaceSelectionSnapshot(
        long revision,
        RoslynXamlWorkspaceSelectionStatus status,
        ProjectId? projectId,
        DocumentId? documentId,
        string? projectFilePath,
        string? documentFilePath,
        bool hasUnsavedText)
    {
        Revision = revision;
        Status = status;
        ProjectId = projectId;
        DocumentId = documentId;
        ProjectFilePath = projectFilePath;
        DocumentFilePath = documentFilePath;
        HasUnsavedText = hasUnsavedText;
    }

    public long Revision { get; }
    public RoslynXamlWorkspaceSelectionStatus Status { get; }
    public ProjectId? ProjectId { get; }
    public DocumentId? DocumentId { get; }
    public string? ProjectFilePath { get; }
    public string? DocumentFilePath { get; }
    public bool HasUnsavedText { get; }
    public bool CanSubmit =>
        Status == RoslynXamlWorkspaceSelectionStatus.Ready;
}

public sealed class RoslynXamlWorkspaceSelectionChangedEventArgs :
    EventArgs
{
    internal RoslynXamlWorkspaceSelectionChangedEventArgs(
        RoslynXamlWorkspaceSelectionSnapshot selection)
    {
        Selection = selection;
    }

    public RoslynXamlWorkspaceSelectionSnapshot Selection { get; }
}

/// <summary>
/// Connects an IDE-neutral Roslyn workspace/project selection to the shared project-watch
/// transport. The workspace and transport remain caller-owned. The adapter observes
/// <see cref="Workspace.CurrentSolution"/>, never applies changes, and materializes a
/// request only from one immutable current-solution snapshot. Stable-ID resolution and
/// selection reads are O(1); recovery is O(P + D) for P projects and D additional
/// documents and runs only while an ID is unavailable or recreated.
/// </summary>
public sealed class RoslynXamlWorkspaceProjectSelectorAdapter :
    IDisposable
{
    private static readonly StringComparison PathComparison =
        Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly object _gate = new object();
    private readonly Workspace _workspace;
    private readonly RoslynXamlProjectWatchTransport _transport;
    private readonly WorkspaceEventRegistration
        _workspaceRegistration;
    private ProjectId? _projectId;
    private DocumentId? _documentId;
    private string? _projectFilePath;
    private string? _documentFilePath;
    private SourceText? _unsavedText;
    private RoslynXamlWorkspaceSelectionStatus _status;
    private long _revision;
    private bool _disposed;

    public RoslynXamlWorkspaceProjectSelectorAdapter(
        Workspace workspace,
        RoslynXamlProjectWatchTransport transport)
    {
        _workspace = workspace ??
            throw new ArgumentNullException(nameof(workspace));
        _transport = transport ??
            throw new ArgumentNullException(nameof(transport));
        _status = RoslynXamlWorkspaceSelectionStatus.Empty;
        _workspaceRegistration =
            _workspace.RegisterWorkspaceChangedImmediateHandler(
                OnWorkspaceChanged);
    }

    public event EventHandler<
        RoslynXamlWorkspaceSelectionChangedEventArgs>?
        SelectionChanged;

    public RoslynXamlWorkspaceSelectionSnapshot Selection
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return CreateSnapshot();
            }
        }
    }

    public RoslynXamlWorkspaceSelectionSnapshot Select(
        ProjectId projectId,
        DocumentId documentId,
        string? projectFilePath = null,
        string? documentFilePath = null)
    {
        if (projectId == null)
            throw new ArgumentNullException(nameof(projectId));
        if (documentId == null)
            throw new ArgumentNullException(nameof(documentId));

        RoslynXamlWorkspaceSelectionSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            _projectId = projectId;
            _documentId = documentId;
            _projectFilePath = NormalizeOptionalPath(
                projectFilePath);
            _documentFilePath = NormalizeOptionalPath(
                documentFilePath);
            _unsavedText = null;
            Resolve(_workspace.CurrentSolution);
            _revision++;
            snapshot = CreateSnapshot();
        }

        RaiseSelectionChanged(snapshot);
        return snapshot;
    }

    public RoslynXamlWorkspaceSelectionSnapshot ClearSelection()
    {
        RoslynXamlWorkspaceSelectionSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            _projectId = null;
            _documentId = null;
            _projectFilePath = null;
            _documentFilePath = null;
            _unsavedText = null;
            _status = RoslynXamlWorkspaceSelectionStatus.Empty;
            _revision++;
            snapshot = CreateSnapshot();
        }

        RaiseSelectionChanged(snapshot);
        return snapshot;
    }

    public RoslynXamlWorkspaceSelectionSnapshot SetUnsavedText(
        SourceText? unsavedText)
    {
        RoslynXamlWorkspaceSelectionSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_unsavedText, unsavedText))
                return CreateSnapshot();
            _unsavedText = unsavedText;
            _revision++;
            snapshot = CreateSnapshot();
        }

        RaiseSelectionChanged(snapshot);
        return snapshot;
    }

    public bool TryCreateRequest(
        long sequence,
        out RoslynXamlProjectWatchRequest? request,
        bool immediate = false,
        RoslynXamlProjectWatchProtocolVersion? protocolVersion = null)
    {
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        lock (_gate)
        {
            ThrowIfDisposed();
            var solution = _workspace.CurrentSolution;
            Resolve(solution);
            if (_status !=
                    RoslynXamlWorkspaceSelectionStatus.Ready ||
                _projectId == null ||
                _documentId == null)
            {
                request = null;
                return false;
            }

            var project = solution.GetProject(_projectId);
            if (project == null ||
                project.GetAdditionalDocument(_documentId) == null)
            {
                request = null;
                return false;
            }

            request = new RoslynXamlProjectWatchRequest(
                sequence,
                project,
                _documentId,
                _unsavedText,
                immediate,
                protocolVersion);
            return true;
        }
    }

    public Task<RoslynXamlProjectWatchResultSnapshot> SubmitAsync(
        long sequence,
        bool immediate = false,
        RoslynXamlProjectWatchProtocolVersion? protocolVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateRequest(
                sequence,
                out var request,
                immediate,
                protocolVersion))
        {
            throw new InvalidOperationException(
                "The current workspace selection is not a XAML additional document.");
        }

        return _transport.SubmitAsync(
            request!,
            cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _workspaceRegistration.Dispose();
    }

    private void OnWorkspaceChanged(
        WorkspaceChangeEventArgs args)
    {
        RoslynXamlWorkspaceSelectionSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed || _projectId == null)
                return;

            var priorProjectId = _projectId;
            var priorDocumentId = _documentId;
            var priorStatus = _status;
            Resolve(args.NewSolution);
            var identityChanged =
                !Equals(priorProjectId, _projectId) ||
                !Equals(priorDocumentId, _documentId) ||
                priorStatus != _status;
            var selectedProjectChanged =
                args.ProjectId == null ||
                Equals(args.ProjectId, priorProjectId) ||
                Equals(args.ProjectId, _projectId);
            if (!identityChanged && !selectedProjectChanged)
                return;

            _revision++;
            snapshot = CreateSnapshot();
        }

        RaiseSelectionChanged(snapshot);
    }

    private void Resolve(Solution solution)
    {
        if (_projectId == null)
        {
            _status = RoslynXamlWorkspaceSelectionStatus.Empty;
            return;
        }

        var project = solution.GetProject(_projectId);
        if (project == null && _projectFilePath != null)
        {
            project = FindProjectByPath(
                solution,
                _projectFilePath,
                out var ambiguousProject);
            if (ambiguousProject)
            {
                _status =
                    RoslynXamlWorkspaceSelectionStatus.AmbiguousPath;
                return;
            }
            if (project != null)
                _projectId = project.Id;
        }

        if (project == null)
        {
            _status =
                RoslynXamlWorkspaceSelectionStatus.ProjectUnavailable;
            return;
        }

        if (_projectFilePath == null &&
            !string.IsNullOrWhiteSpace(project.FilePath))
        {
            _projectFilePath = NormalizeOptionalPath(
                project.FilePath);
        }

        TextDocument? document = _documentId == null
            ? null
            : project.GetAdditionalDocument(_documentId);
        if (document == null && _documentFilePath != null)
        {
            document = FindAdditionalDocumentByPath(
                project,
                _documentFilePath,
                out var ambiguousDocument);
            if (ambiguousDocument)
            {
                _status =
                    RoslynXamlWorkspaceSelectionStatus.AmbiguousPath;
                return;
            }
            if (document != null)
                _documentId = document.Id;
        }

        if (document == null)
        {
            if (_documentId != null &&
                project.GetDocument(_documentId) != null)
            {
                _status =
                    RoslynXamlWorkspaceSelectionStatus
                        .NotAdditionalDocument;
            }
            else
            {
                _status =
                    RoslynXamlWorkspaceSelectionStatus
                        .DocumentUnavailable;
            }
            return;
        }

        if (!IsXamlDocument(document))
        {
            _status =
                RoslynXamlWorkspaceSelectionStatus.NotXamlDocument;
            return;
        }

        if (_documentFilePath == null &&
            !string.IsNullOrWhiteSpace(document.FilePath))
        {
            _documentFilePath = NormalizeOptionalPath(
                document.FilePath);
        }
        _status = RoslynXamlWorkspaceSelectionStatus.Ready;
    }

    private static Project? FindProjectByPath(
        Solution solution,
        string path,
        out bool ambiguous)
    {
        Project? match = null;
        ambiguous = false;
        foreach (var project in solution.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.FilePath) ||
                !PathsEqual(project.FilePath!, path))
            {
                continue;
            }

            if (match != null)
            {
                ambiguous = true;
                return null;
            }
            match = project;
        }
        return match;
    }

    private static TextDocument? FindAdditionalDocumentByPath(
        Project project,
        string path,
        out bool ambiguous)
    {
        TextDocument? match = null;
        ambiguous = false;
        foreach (var document in project.AdditionalDocuments)
        {
            if (string.IsNullOrWhiteSpace(document.FilePath) ||
                !PathsEqual(document.FilePath!, path))
            {
                continue;
            }

            if (match != null)
            {
                ambiguous = true;
                return null;
            }
            match = document;
        }
        return match;
    }

    private static bool IsXamlDocument(TextDocument document)
    {
        var candidate = string.IsNullOrWhiteSpace(document.FilePath)
            ? document.Name
            : document.FilePath!;
        return candidate.EndsWith(
            ".xaml",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeOptionalPath(left),
            NormalizeOptionalPath(right),
            PathComparison);

    private static string? NormalizeOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return Path.GetFullPath(path);
    }

    private RoslynXamlWorkspaceSelectionSnapshot CreateSnapshot() =>
        new RoslynXamlWorkspaceSelectionSnapshot(
            _revision,
            _status,
            _projectId,
            _documentId,
            _projectFilePath,
            _documentFilePath,
            _unsavedText != null);

    private void RaiseSelectionChanged(
        RoslynXamlWorkspaceSelectionSnapshot snapshot) =>
        SelectionChanged?.Invoke(
            this,
            new RoslynXamlWorkspaceSelectionChangedEventArgs(
                snapshot));

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(
                    RoslynXamlWorkspaceProjectSelectorAdapter));
        }
    }
}
