using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ProGPU.Xaml.Workspaces;

/// <summary>
/// Identifies the immutable project-watch transport contract. Major versions are
/// incompatible; a host may accept a request whose minor version is no newer than its
/// own. This contract versions the typed in-process boundary and is independent of any
/// wire encoding selected by an IDE adapter.
/// </summary>
public readonly struct RoslynXamlProjectWatchProtocolVersion :
    IEquatable<RoslynXamlProjectWatchProtocolVersion>
{
    public RoslynXamlProjectWatchProtocolVersion(
        int major,
        int minor)
    {
        if (major < 0)
            throw new ArgumentOutOfRangeException(nameof(major));
        if (minor < 0)
            throw new ArgumentOutOfRangeException(nameof(minor));
        Major = major;
        Minor = minor;
    }

    public static RoslynXamlProjectWatchProtocolVersion Current { get; } =
        new RoslynXamlProjectWatchProtocolVersion(1, 0);

    public int Major { get; }
    public int Minor { get; }

    public bool CanServe(
        RoslynXamlProjectWatchProtocolVersion requested) =>
        Major == requested.Major &&
        Minor >= requested.Minor;

    public bool Equals(
        RoslynXamlProjectWatchProtocolVersion other) =>
        Major == other.Major &&
        Minor == other.Minor;

    public override bool Equals(object? obj) =>
        obj is RoslynXamlProjectWatchProtocolVersion other &&
        Equals(other);

    public override int GetHashCode() =>
        unchecked((Major * 397) ^ Minor);

    public override string ToString() =>
        Major.ToString(CultureInfo.InvariantCulture) +
        "." +
        Minor.ToString(CultureInfo.InvariantCulture);

    public static bool operator ==(
        RoslynXamlProjectWatchProtocolVersion left,
        RoslynXamlProjectWatchProtocolVersion right) =>
        left.Equals(right);

    public static bool operator !=(
        RoslynXamlProjectWatchProtocolVersion left,
        RoslynXamlProjectWatchProtocolVersion right) =>
        !left.Equals(right);
}

/// <summary>
/// One immutable IDE or playground submission. Roslyn projects, document IDs, and source
/// text are immutable snapshots; the transport never mutates the owning workspace.
/// </summary>
public sealed class RoslynXamlProjectWatchRequest
{
    public RoslynXamlProjectWatchRequest(
        long sequence,
        Project project,
        DocumentId xamlDocumentId,
        SourceText? unsavedText = null,
        bool immediate = false,
        RoslynXamlProjectWatchProtocolVersion? protocolVersion = null)
    {
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        Sequence = sequence;
        Project = project ??
            throw new ArgumentNullException(nameof(project));
        XamlDocumentId = xamlDocumentId ??
            throw new ArgumentNullException(nameof(xamlDocumentId));
        UnsavedText = unsavedText;
        Immediate = immediate;
        ProtocolVersion =
            protocolVersion ??
            RoslynXamlProjectWatchProtocolVersion.Current;
    }

    public RoslynXamlProjectWatchProtocolVersion ProtocolVersion { get; }
    public long Sequence { get; }
    public Project Project { get; }
    public DocumentId XamlDocumentId { get; }
    public SourceText? UnsavedText { get; }
    public bool Immediate { get; }
}

/// <summary>
/// Bounds the detached result projection retained by an editor transport. Options are
/// snapshotted when the transport is constructed.
/// </summary>
public sealed class RoslynXamlProjectWatchTransportOptions
{
    public const int DefaultMaximumDiagnosticCount = 256;
    public const int DefaultMaximumTextLength = 4096;
    public const int AbsoluteMaximumDiagnosticCount = 4096;
    public const int AbsoluteMaximumTextLength = 65536;

    public int MaximumDiagnosticCount { get; set; } =
        DefaultMaximumDiagnosticCount;

    public int MaximumTextLength { get; set; } =
        DefaultMaximumTextLength;
}

/// <summary>
/// A detached diagnostic value. It retains no Roslyn diagnostic, syntax tree,
/// compilation, source text, or workspace object.
/// </summary>
public sealed class RoslynXamlProjectWatchDiagnosticSnapshot
{
    internal RoslynXamlProjectWatchDiagnosticSnapshot(
        string id,
        DiagnosticSeverity severity,
        string message,
        string? path,
        int startLine,
        int startCharacter,
        int endLine,
        int endCharacter,
        bool textTruncated)
    {
        Id = id;
        Severity = severity;
        Message = message;
        Path = path;
        StartLine = startLine;
        StartCharacter = startCharacter;
        EndLine = endLine;
        EndCharacter = endCharacter;
        TextTruncated = textTruncated;
    }

    public string Id { get; }
    public DiagnosticSeverity Severity { get; }
    public string Message { get; }
    public string? Path { get; }
    public int StartLine { get; }
    public int StartCharacter { get; }
    public int EndLine { get; }
    public int EndCharacter { get; }
    public bool TextTruncated { get; }
}

/// <summary>
/// A bounded immutable result for IDE, playground, and command-line transports. The
/// projection intentionally excludes the prepared update and compiler graph; runtime
/// publication remains owned by the configured watch session.
/// </summary>
public sealed class RoslynXamlProjectWatchResultSnapshot
{
    private RoslynXamlProjectWatchResultSnapshot(
        RoslynXamlProjectWatchProtocolVersion protocolVersion,
        long sequence,
        long version,
        RoslynXamlProjectWatchStatus status,
        RoslynXamlProjectCommitResult? commitResult,
        long committedGeneration,
        RoslynXamlProjectDeltaMode? mode,
        RoslynXamlReloadAction? action,
        ImmutableArray<RoslynXamlMetadataDeltaReason> metadataReasons,
        bool? isInitial,
        bool? requiresRuntimePublication,
        string? resourceUri,
        string? qualifiedTypeName,
        bool? targetDocumentChanged,
        bool? targetDependencyChanged,
        bool? metadataChanged,
        TimeSpan duration,
        RoslynXamlProjectWatchTelemetry telemetry,
        string message,
        ImmutableArray<RoslynXamlProjectWatchDiagnosticSnapshot> diagnostics,
        bool diagnosticsTruncated,
        bool textTruncated)
    {
        ProtocolVersion = protocolVersion;
        Sequence = sequence;
        Version = version;
        Status = status;
        CommitResult = commitResult;
        CommittedGeneration = committedGeneration;
        Mode = mode;
        Action = action;
        MetadataReasons = metadataReasons;
        IsInitial = isInitial;
        RequiresRuntimePublication = requiresRuntimePublication;
        ResourceUri = resourceUri;
        QualifiedTypeName = qualifiedTypeName;
        TargetDocumentChanged = targetDocumentChanged;
        TargetDependencyChanged = targetDependencyChanged;
        MetadataChanged = metadataChanged;
        Duration = duration;
        Telemetry = telemetry;
        Message = message;
        Diagnostics = diagnostics;
        DiagnosticsTruncated = diagnosticsTruncated;
        TextTruncated = textTruncated;
    }

    public RoslynXamlProjectWatchProtocolVersion ProtocolVersion { get; }
    public long Sequence { get; }
    public long Version { get; }
    public RoslynXamlProjectWatchStatus Status { get; }
    public RoslynXamlProjectCommitResult? CommitResult { get; }
    public long CommittedGeneration { get; }
    public RoslynXamlProjectDeltaMode? Mode { get; }
    public RoslynXamlReloadAction? Action { get; }
    public ImmutableArray<RoslynXamlMetadataDeltaReason> MetadataReasons { get; }
    public bool? IsInitial { get; }
    public bool? RequiresRuntimePublication { get; }
    public string? ResourceUri { get; }
    public string? QualifiedTypeName { get; }
    public bool? TargetDocumentChanged { get; }
    public bool? TargetDependencyChanged { get; }
    public bool? MetadataChanged { get; }
    public TimeSpan Duration { get; }
    public RoslynXamlProjectWatchTelemetry Telemetry { get; }
    public string Message { get; }
    public ImmutableArray<RoslynXamlProjectWatchDiagnosticSnapshot> Diagnostics { get; }
    public bool DiagnosticsTruncated { get; }
    public bool TextTruncated { get; }
    public bool Accepted =>
        CommitResult ==
        RoslynXamlProjectCommitResult.Accepted;

    public static RoslynXamlProjectWatchResultSnapshot Create(
        RoslynXamlProjectWatchResult result,
        long sequence,
        RoslynXamlProjectWatchTransportOptions? options = null)
    {
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        var limits = SnapshotOptions(options);
        var update = result.Update;
        var plan = update?.Delta;
        var diagnostics = ImmutableArray.CreateBuilder<
            RoslynXamlProjectWatchDiagnosticSnapshot>();
        var identities = new HashSet<DiagnosticIdentity>();
        var diagnosticsTruncated = false;
        var textTruncated = false;
        var diagnosticsScanned = 0;

        if (plan != null)
        {
            AddDiagnostics(
                plan.Diagnostics,
                limits,
                identities,
                diagnostics,
                ref diagnosticsScanned,
                ref diagnosticsTruncated,
                ref textTruncated);
        }

        if (!diagnosticsTruncated && update != null)
        {
            AddDiagnostics(
                update.Current.Artifact?.Diagnostics ??
                update.Current.CompilationInspection
                    .CompilationResult.Diagnostics,
                limits,
                identities,
                diagnostics,
                ref diagnosticsScanned,
                ref diagnosticsTruncated,
                ref textTruncated);
        }

        var message = LimitText(
            result.Message,
            limits.MaximumTextLength,
            ref textTruncated)!;
        var resourceUri = LimitText(
            update?.Current.ResourceUri,
            limits.MaximumTextLength,
            ref textTruncated);
        var qualifiedTypeName = LimitText(
            update?.Current.QualifiedTypeName,
            limits.MaximumTextLength,
            ref textTruncated);

        return new RoslynXamlProjectWatchResultSnapshot(
            RoslynXamlProjectWatchProtocolVersion.Current,
            sequence,
            result.Version,
            result.Status,
            result.CommitResult,
            result.CommittedGeneration,
            plan?.Mode,
            plan?.Action,
            plan?.MetadataReasons ??
            ImmutableArray<RoslynXamlMetadataDeltaReason>.Empty,
            update?.IsInitial,
            update?.RequiresRuntimePublication,
            resourceUri,
            qualifiedTypeName,
            plan?.TargetDocumentChanged,
            plan?.TargetDependencyChanged,
            plan?.MetadataChanged,
            result.Duration,
            result.Telemetry,
            message,
            diagnostics.ToImmutable(),
            diagnosticsTruncated,
            textTruncated);
    }

    internal static RoslynXamlProjectWatchTransportOptions SnapshotOptions(
        RoslynXamlProjectWatchTransportOptions? options)
    {
        options ??= new RoslynXamlProjectWatchTransportOptions();
        if (options.MaximumDiagnosticCount < 0 ||
            options.MaximumDiagnosticCount >
            RoslynXamlProjectWatchTransportOptions
                .AbsoluteMaximumDiagnosticCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaximumDiagnosticCount));
        }
        if (options.MaximumTextLength <= 0 ||
            options.MaximumTextLength >
            RoslynXamlProjectWatchTransportOptions
                .AbsoluteMaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaximumTextLength));
        }

        return new RoslynXamlProjectWatchTransportOptions
        {
            MaximumDiagnosticCount =
                options.MaximumDiagnosticCount,
            MaximumTextLength =
                options.MaximumTextLength
        };
    }

    private static void AddDiagnostics(
        IEnumerable<Diagnostic> source,
        RoslynXamlProjectWatchTransportOptions limits,
        ISet<DiagnosticIdentity> identities,
        ImmutableArray<RoslynXamlProjectWatchDiagnosticSnapshot>.Builder target,
        ref int diagnosticsScanned,
        ref bool diagnosticsTruncated,
        ref bool textTruncated)
    {
        foreach (var diagnostic in source)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Hidden)
                continue;
            if (diagnosticsScanned >=
                checked(limits.MaximumDiagnosticCount + 1))
            {
                diagnosticsTruncated = true;
                return;
            }
            diagnosticsScanned++;

            var diagnosticTextTruncated = false;
            var lineSpan = diagnostic.Location.GetLineSpan();
            var id = LimitText(
                diagnostic.Id,
                limits.MaximumTextLength,
                ref diagnosticTextTruncated)!;
            var message = LimitText(
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                limits.MaximumTextLength,
                ref diagnosticTextTruncated)!;
            var path = LimitText(
                lineSpan.Path,
                limits.MaximumTextLength,
                ref diagnosticTextTruncated);
            textTruncated |= diagnosticTextTruncated;
            var identity = new DiagnosticIdentity(
                id,
                diagnostic.Severity,
                message,
                path,
                lineSpan.StartLinePosition.Line,
                lineSpan.StartLinePosition.Character,
                lineSpan.EndLinePosition.Line,
                lineSpan.EndLinePosition.Character);
            if (!identities.Add(identity))
                continue;
            if (target.Count >= limits.MaximumDiagnosticCount)
            {
                diagnosticsTruncated = true;
                return;
            }
            target.Add(
                new RoslynXamlProjectWatchDiagnosticSnapshot(
                    id,
                    diagnostic.Severity,
                    message,
                    path,
                    lineSpan.StartLinePosition.Line,
                    lineSpan.StartLinePosition.Character,
                    lineSpan.EndLinePosition.Line,
                    lineSpan.EndLinePosition.Character,
                    diagnosticTextTruncated));
        }
    }

    private static string? LimitText(
        string? value,
        int maximumLength,
        ref bool truncated)
    {
        if (value == null || value.Length <= maximumLength)
            return value;
        truncated = true;
        return value.Substring(0, maximumLength);
    }

    private readonly struct DiagnosticIdentity :
        IEquatable<DiagnosticIdentity>
    {
        public DiagnosticIdentity(
            string id,
            DiagnosticSeverity severity,
            string message,
            string? path,
            int startLine,
            int startCharacter,
            int endLine,
            int endCharacter)
        {
            _id = id;
            _severity = severity;
            _message = message;
            _path = path;
            _startLine = startLine;
            _startCharacter = startCharacter;
            _endLine = endLine;
            _endCharacter = endCharacter;
        }

        private readonly string _id;
        private readonly DiagnosticSeverity _severity;
        private readonly string _message;
        private readonly string? _path;
        private readonly int _startLine;
        private readonly int _startCharacter;
        private readonly int _endLine;
        private readonly int _endCharacter;

        public bool Equals(DiagnosticIdentity other) =>
            _severity == other._severity &&
            _startLine == other._startLine &&
            _startCharacter == other._startCharacter &&
            _endLine == other._endLine &&
            _endCharacter == other._endCharacter &&
            string.Equals(_id, other._id, StringComparison.Ordinal) &&
            string.Equals(
                _message,
                other._message,
                StringComparison.Ordinal) &&
            string.Equals(
                _path,
                other._path,
                StringComparison.Ordinal);

        public override bool Equals(object? obj) =>
            obj is DiagnosticIdentity other &&
            Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)_severity;
                hash = (hash * 397) ^ _startLine;
                hash = (hash * 397) ^ _startCharacter;
                hash = (hash * 397) ^ _endLine;
                hash = (hash * 397) ^ _endCharacter;
                hash = (hash * 397) ^
                    StringComparer.Ordinal.GetHashCode(_id);
                hash = (hash * 397) ^
                    StringComparer.Ordinal.GetHashCode(_message);
                hash = (hash * 397) ^
                    (_path == null
                        ? 0
                        : StringComparer.Ordinal
                            .GetHashCode(_path));
                return hash;
            }
        }
    }
}

/// <summary>
/// A thin typed adapter over a caller-owned watch session. It validates protocol
/// compatibility, forwards one immutable snapshot, and returns a detached bounded
/// result. Disposing and runtime publication remain the caller's responsibility.
/// </summary>
public sealed class RoslynXamlProjectWatchTransport
{
    private readonly RoslynXamlProjectWatchSession _session;
    private readonly RoslynXamlProjectWatchTransportOptions _options;

    public RoslynXamlProjectWatchTransport(
        RoslynXamlProjectWatchSession session,
        RoslynXamlProjectWatchTransportOptions? options = null)
    {
        _session = session ??
            throw new ArgumentNullException(nameof(session));
        _options =
            RoslynXamlProjectWatchResultSnapshot
                .SnapshotOptions(options);
    }

    public RoslynXamlProjectWatchProtocolVersion ProtocolVersion =>
        RoslynXamlProjectWatchProtocolVersion.Current;

    public async Task<RoslynXamlProjectWatchResultSnapshot> SubmitAsync(
        RoslynXamlProjectWatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (!ProtocolVersion.CanServe(request.ProtocolVersion))
        {
            throw new NotSupportedException(
                "Project-watch protocol " +
                request.ProtocolVersion +
                " is not compatible with host protocol " +
                ProtocolVersion +
                ".");
        }

        var result = await _session.SubmitAsync(
                request.Project,
                request.XamlDocumentId,
                request.UnsavedText,
                request.Immediate,
                cancellationToken)
            .ConfigureAwait(false);
        return RoslynXamlProjectWatchResultSnapshot.Create(
            result,
            request.Sequence,
            _options);
    }
}
