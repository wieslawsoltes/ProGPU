using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ProGPU.Xaml.Workspaces;

public enum RoslynXamlProjectWatchStatus
{
    Applied,
    AcceptedWithoutRuntimeChange,
    Rejected,
    Superseded,
    Stopped
}

public sealed class RoslynXamlProjectWatchResult
{
    internal RoslynXamlProjectWatchResult(
        long version,
        RoslynXamlProjectWatchStatus status,
        RoslynXamlProjectPreviewUpdate? update,
        RoslynXamlProjectCommitResult? commitResult,
        long committedGeneration,
        TimeSpan duration,
        RoslynXamlProjectWatchTelemetry telemetry,
        string message)
    {
        Version = version;
        Status = status;
        Update = update;
        CommitResult = commitResult;
        CommittedGeneration = committedGeneration;
        Duration = duration;
        Telemetry = telemetry;
        Message = message;
    }

    public long Version { get; }
    public RoslynXamlProjectWatchStatus Status { get; }
    public RoslynXamlProjectPreviewUpdate? Update { get; }
    public RoslynXamlProjectCommitResult? CommitResult { get; }
    public long CommittedGeneration { get; }
    public TimeSpan Duration { get; }
    public RoslynXamlProjectWatchTelemetry Telemetry { get; }
    public string Message { get; }
    public bool Accepted =>
        CommitResult ==
        RoslynXamlProjectCommitResult.Accepted;
}

/// <summary>
/// Debounces immutable Roslyn project snapshots and routes only the latest prepared
/// candidate through one transactional preview coordinator. Superseded compilation is
/// canceled, no-op semantic updates advance the baseline without invoking the runtime
/// publisher, and rejected candidates preserve the last host-confirmed snapshot.
/// </summary>
public sealed class RoslynXamlProjectWatchSession :
    IDisposable
{
    private static readonly TimeSpan MaximumDebounce =
        TimeSpan.FromMinutes(1);

    private readonly object _gate = new object();
    private readonly RoslynXamlProjectPreviewCoordinator
        _coordinator;
    private readonly Func<
        RoslynXamlProjectPreviewUpdate,
        CancellationToken,
        Task<bool>> _publishAsync;
    private readonly
        IRoslynXamlProjectWatchAllocationCounter?
        _allocationCounter;
    private readonly TimeSpan _debounce;
    private readonly CancellationTokenSource _lifetime =
        new CancellationTokenSource();
    private CancellationTokenSource? _pending;
    private long _version;
    private long _submittedCount;
    private long _completedCount;
    private long _appliedCount;
    private long _cacheHitCount;
    private long _rejectedCount;
    private long _supersededCount;
    private long _stoppedCount;
    private long _callerCanceledCount;
    private long _faultedCount;
    private int _currentQueueDepth;
    private int _maximumQueueDepth;
    private long _totalDurationTicks;
    private long _lastDurationTicks;
    private long _maximumDurationTicks;
    private long _allocationMeasurementCount;
    private long _totalAllocatedBytes;
    private long _lastAllocatedBytes;
    private long _maximumAllocatedBytes;
    private bool _disposed;

    public RoslynXamlProjectWatchSession(
        RoslynXamlProjectPreviewCoordinator coordinator,
        Func<
            RoslynXamlProjectPreviewUpdate,
            CancellationToken,
            Task<bool>> publishAsync,
        TimeSpan? debounce = null,
        IRoslynXamlProjectWatchAllocationCounter?
            allocationCounter = null)
    {
        _coordinator =
            coordinator ??
            throw new ArgumentNullException(
                nameof(coordinator));
        _publishAsync =
            publishAsync ??
            throw new ArgumentNullException(
                nameof(publishAsync));
        _allocationCounter = allocationCounter;
        _debounce =
            debounce ??
            TimeSpan.FromMilliseconds(250);
        if (_debounce < TimeSpan.Zero ||
            _debounce > MaximumDebounce)
        {
            throw new ArgumentOutOfRangeException(
                nameof(debounce),
                "The watch debounce must be between zero and one minute.");
        }
    }

    public long Version
    {
        get
        {
            lock (_gate)
                return _version;
        }
    }

    public RoslynXamlProjectPreviewCoordinator
        Coordinator => _coordinator;

    public RoslynXamlProjectWatchTelemetry Telemetry
    {
        get
        {
            lock (_gate)
                return GetTelemetryLocked();
        }
    }

    public Task<RoslynXamlProjectWatchResult> SubmitAsync(
        Project project,
        DocumentId xamlDocumentId,
        SourceText? unsavedText = null,
        bool immediate = false,
        CancellationToken cancellationToken = default)
    {
        if (project == null)
            throw new ArgumentNullException(nameof(project));
        if (xamlDocumentId == null)
        {
            throw new ArgumentNullException(
                nameof(xamlDocumentId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource operation;
        long version;
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(
                        RoslynXamlProjectWatchSession));
            }
            version = checked(++_version);
            _pending?.Cancel();
            operation =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        _lifetime.Token,
                        cancellationToken);
            _pending = operation;
            _submittedCount =
                IncrementSaturating(
                    _submittedCount);
            if (_currentQueueDepth <
                int.MaxValue)
            {
                _currentQueueDepth++;
            }
            if (_currentQueueDepth >
                _maximumQueueDepth)
            {
                _maximumQueueDepth =
                    _currentQueueDepth;
            }
        }

        return RunAsync(
            version,
            project,
            xamlDocumentId,
            unsavedText,
            immediate,
            cancellationToken,
            operation);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetime.Cancel();
            _pending?.Cancel();
        }

        _lifetime.Dispose();
    }

    private async Task<RoslynXamlProjectWatchResult>
        RunAsync(
            long version,
            Project project,
            DocumentId xamlDocumentId,
            SourceText? unsavedText,
            bool immediate,
            CancellationToken callerToken,
            CancellationTokenSource operation)
    {
        var startedTimestamp =
            Stopwatch.GetTimestamp();
        var startedAllocatedBytes =
            TryReadAllocatedBytes();
        try
        {
            if (!immediate &&
                _debounce > TimeSpan.Zero)
            {
                await Task.Delay(
                        _debounce,
                        operation.Token)
                    .ConfigureAwait(false);
            }

            var update = await PrepareAsync(
                    project,
                    xamlDocumentId,
                    unsavedText,
                    operation.Token)
                .ConfigureAwait(false);
            RoslynXamlProjectCommitResult commit;
            while (true)
            {
                operation.Token
                    .ThrowIfCancellationRequested();
                commit = await ApplyAsync(
                        version,
                        update,
                        operation.Token)
                    .ConfigureAwait(false);
                if (commit !=
                        RoslynXamlProjectCommitResult
                            .RejectedStale ||
                    IsSuperseded(version))
                {
                    break;
                }

                update = await PrepareAsync(
                        project,
                        xamlDocumentId,
                        unsavedText,
                        operation.Token)
                    .ConfigureAwait(false);
            }

            var duration =
                GetElapsed(startedTimestamp);
            if (commit !=
                    RoslynXamlProjectCommitResult.Accepted &&
                IsSuperseded(version))
            {
                return CreateResult(
                    version,
                    RoslynXamlProjectWatchStatus
                        .Superseded,
                    update,
                    commit,
                    _coordinator.Generation,
                    duration,
                    startedAllocatedBytes,
                    "A newer project snapshot superseded this update.");
            }

            var status = GetStatus(update, commit);
            return CreateResult(
                version,
                status,
                update,
                commit,
                _coordinator.Generation,
                duration,
                startedAllocatedBytes,
                GetMessage(update, commit, status));
        }
        catch (OperationCanceledException)
            when (IsSuperseded(version))
        {
            return CreateResult(
                version,
                RoslynXamlProjectWatchStatus
                    .Superseded,
                update: null,
                commitResult: null,
                _coordinator.Generation,
                GetElapsed(startedTimestamp),
                startedAllocatedBytes,
                "A newer project snapshot superseded this update.");
        }
        catch (OperationCanceledException)
            when (IsStopped())
        {
            return CreateResult(
                version,
                RoslynXamlProjectWatchStatus.Stopped,
                update: null,
                commitResult: null,
                _coordinator.Generation,
                GetElapsed(startedTimestamp),
                startedAllocatedBytes,
                "The project watch session stopped.");
        }
        catch (OperationCanceledException)
        {
            RecordTerminalOperation(
                status: null,
                callerCanceled: true,
                faulted: false,
                duration:
                    GetElapsed(startedTimestamp),
                startedAllocatedBytes:
                    startedAllocatedBytes);
            callerToken.ThrowIfCancellationRequested();
            throw;
        }
        catch
        {
            RecordTerminalOperation(
                status: null,
                callerCanceled: false,
                faulted: true,
                duration:
                    GetElapsed(startedTimestamp),
                startedAllocatedBytes:
                    startedAllocatedBytes);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(
                        _pending,
                        operation))
                {
                    _pending = null;
                }
            }

            operation.Dispose();
        }
    }

    private RoslynXamlProjectWatchResult CreateResult(
        long version,
        RoslynXamlProjectWatchStatus status,
        RoslynXamlProjectPreviewUpdate? update,
        RoslynXamlProjectCommitResult? commitResult,
        long committedGeneration,
        TimeSpan duration,
        long? startedAllocatedBytes,
        string message)
    {
        var telemetry = RecordTerminalOperation(
            status,
            callerCanceled: false,
            faulted: false,
            duration,
            startedAllocatedBytes);
        return new RoslynXamlProjectWatchResult(
            version,
            status,
            update,
            commitResult,
            committedGeneration,
            duration,
            telemetry,
            message);
    }

    private RoslynXamlProjectWatchTelemetry
        RecordTerminalOperation(
            RoslynXamlProjectWatchStatus? status,
            bool callerCanceled,
            bool faulted,
            TimeSpan duration,
            long? startedAllocatedBytes)
    {
        var completedAllocatedBytes =
            TryReadAllocatedBytes();
        lock (_gate)
        {
            if (_currentQueueDepth > 0)
                _currentQueueDepth--;
            _completedCount =
                IncrementSaturating(
                    _completedCount);
            switch (status)
            {
                case RoslynXamlProjectWatchStatus.Applied:
                    _appliedCount =
                        IncrementSaturating(
                            _appliedCount);
                    break;
                case RoslynXamlProjectWatchStatus
                        .AcceptedWithoutRuntimeChange:
                    _cacheHitCount =
                        IncrementSaturating(
                            _cacheHitCount);
                    break;
                case RoslynXamlProjectWatchStatus.Rejected:
                    _rejectedCount =
                        IncrementSaturating(
                            _rejectedCount);
                    break;
                case RoslynXamlProjectWatchStatus.Superseded:
                    _supersededCount =
                        IncrementSaturating(
                            _supersededCount);
                    break;
                case RoslynXamlProjectWatchStatus.Stopped:
                    _stoppedCount =
                        IncrementSaturating(
                            _stoppedCount);
                    break;
            }

            if (callerCanceled)
            {
                _callerCanceledCount =
                    IncrementSaturating(
                        _callerCanceledCount);
            }
            if (faulted)
            {
                _faultedCount =
                    IncrementSaturating(
                        _faultedCount);
            }

            _lastDurationTicks = duration.Ticks;
            _totalDurationTicks =
                AddSaturating(
                    _totalDurationTicks,
                    duration.Ticks);
            if (duration.Ticks >
                _maximumDurationTicks)
            {
                _maximumDurationTicks =
                    duration.Ticks;
            }

            if (startedAllocatedBytes.HasValue &&
                completedAllocatedBytes.HasValue)
            {
                var allocatedBytes =
                    completedAllocatedBytes.Value >=
                    startedAllocatedBytes.Value
                        ? completedAllocatedBytes.Value -
                          startedAllocatedBytes.Value
                        : 0;
                _allocationMeasurementCount =
                    IncrementSaturating(
                        _allocationMeasurementCount);
                _lastAllocatedBytes =
                    allocatedBytes;
                _totalAllocatedBytes =
                    AddSaturating(
                        _totalAllocatedBytes,
                        allocatedBytes);
                if (allocatedBytes >
                    _maximumAllocatedBytes)
                {
                    _maximumAllocatedBytes =
                        allocatedBytes;
                }
            }

            return GetTelemetryLocked();
        }
    }

    private RoslynXamlProjectWatchTelemetry
        GetTelemetryLocked() =>
        new RoslynXamlProjectWatchTelemetry(
            _submittedCount,
            _completedCount,
            _appliedCount,
            _cacheHitCount,
            _rejectedCount,
            _supersededCount,
            _stoppedCount,
            _callerCanceledCount,
            _faultedCount,
            _currentQueueDepth,
            _maximumQueueDepth,
            TimeSpan.FromTicks(
                _totalDurationTicks),
            TimeSpan.FromTicks(
                _lastDurationTicks),
            TimeSpan.FromTicks(
                _maximumDurationTicks),
            _allocationMeasurementCount,
            _totalAllocatedBytes,
            _lastAllocatedBytes,
            _maximumAllocatedBytes);

    private long? TryReadAllocatedBytes()
    {
        if (_allocationCounter == null)
            return null;
        try
        {
            var value =
                _allocationCounter
                    .GetTotalAllocatedBytes();
            return value >= 0
                ? value
                : (long?)null;
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan GetElapsed(
        long startedTimestamp)
    {
        var elapsedTimestamp =
            Stopwatch.GetTimestamp() -
            startedTimestamp;
        if (elapsedTimestamp <= 0)
            return TimeSpan.Zero;
        var ticks =
            elapsedTimestamp *
            (double)TimeSpan.TicksPerSecond /
            Stopwatch.Frequency;
        return ticks >= TimeSpan.MaxValue.Ticks
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks((long)ticks);
    }

    private static long IncrementSaturating(
        long value) =>
        value == long.MaxValue
            ? value
            : value + 1;

    private static long AddSaturating(
        long left,
        long right) =>
        left > long.MaxValue - right
            ? long.MaxValue
            : left + right;

    private Task<RoslynXamlProjectPreviewUpdate>
        PrepareAsync(
            Project project,
            DocumentId xamlDocumentId,
            SourceText? unsavedText,
            CancellationToken cancellationToken) =>
        _coordinator.PrepareAsync(
            project,
            xamlDocumentId,
            unsavedText,
            cancellationToken);

    private Task<RoslynXamlProjectCommitResult>
        ApplyAsync(
            long version,
            RoslynXamlProjectPreviewUpdate update,
            CancellationToken cancellationToken) =>
        _coordinator.ApplyAsync(
            update,
            update.RequiresRuntimePublication
                ? (candidate, token) =>
                    PublishIfCurrentAsync(
                        version,
                        candidate,
                        token)
                : (_, token) =>
                    ConfirmCurrentAsync(
                        version,
                        token),
            cancellationToken);

    private async Task<bool> PublishIfCurrentAsync(
        long version,
        RoslynXamlProjectPreviewUpdate update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSuperseded(version))
            return false;
        return await _publishAsync(
                update,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<bool> ConfirmCurrentAsync(
        long version,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            !IsSuperseded(version));
    }

    private bool IsSuperseded(long version)
    {
        lock (_gate)
            return version != _version;
    }

    private bool IsStopped()
    {
        lock (_gate)
            return _disposed;
    }

    private static RoslynXamlProjectWatchStatus
        GetStatus(
            RoslynXamlProjectPreviewUpdate update,
            RoslynXamlProjectCommitResult commit)
    {
        if (commit !=
            RoslynXamlProjectCommitResult.Accepted)
        {
            return RoslynXamlProjectWatchStatus
                .Rejected;
        }

        return update.RequiresRuntimePublication
            ? RoslynXamlProjectWatchStatus.Applied
            : RoslynXamlProjectWatchStatus
                .AcceptedWithoutRuntimeChange;
    }

    private static string GetMessage(
        RoslynXamlProjectPreviewUpdate update,
        RoslynXamlProjectCommitResult commit,
        RoslynXamlProjectWatchStatus status)
    {
        if (status ==
            RoslynXamlProjectWatchStatus
                .AcceptedWithoutRuntimeChange)
        {
            return "The snapshot was accepted without changing the runtime preview.";
        }

        if (commit ==
            RoslynXamlProjectCommitResult.Accepted)
        {
            return update.IsInitial
                ? "The initial project preview was published."
                : "The project preview delta was published.";
        }

        return update.FailureMessage ??
               "The project preview update was rejected (" +
               commit +
               "); the last good snapshot was retained.";
    }
}
