using System;

namespace ProGPU.Xaml.Workspaces;

/// <summary>
/// Supplies a monotonic process-wide managed-allocation count when the host runtime
/// exposes one. The watch session records deltas without taking a dependency on a
/// runtime-specific diagnostics API.
/// </summary>
public interface IRoslynXamlProjectWatchAllocationCounter
{
    long GetTotalAllocatedBytes();
}

/// <summary>
/// An immutable cumulative snapshot of one project-watch session.
/// </summary>
public readonly struct RoslynXamlProjectWatchTelemetry
{
    internal RoslynXamlProjectWatchTelemetry(
        long submittedCount,
        long completedCount,
        long appliedCount,
        long cacheHitCount,
        long rejectedCount,
        long supersededCount,
        long stoppedCount,
        long callerCanceledCount,
        long faultedCount,
        int currentQueueDepth,
        int maximumQueueDepth,
        TimeSpan totalDuration,
        TimeSpan lastDuration,
        TimeSpan maximumDuration,
        long allocationMeasurementCount,
        long totalAllocatedBytes,
        long lastAllocatedBytes,
        long maximumAllocatedBytes)
    {
        SubmittedCount = submittedCount;
        CompletedCount = completedCount;
        AppliedCount = appliedCount;
        CacheHitCount = cacheHitCount;
        RejectedCount = rejectedCount;
        SupersededCount = supersededCount;
        StoppedCount = stoppedCount;
        CallerCanceledCount = callerCanceledCount;
        FaultedCount = faultedCount;
        CurrentQueueDepth = currentQueueDepth;
        MaximumQueueDepth = maximumQueueDepth;
        TotalDuration = totalDuration;
        LastDuration = lastDuration;
        MaximumDuration = maximumDuration;
        AllocationMeasurementCount =
            allocationMeasurementCount;
        TotalAllocatedBytes = totalAllocatedBytes;
        LastAllocatedBytes = lastAllocatedBytes;
        MaximumAllocatedBytes = maximumAllocatedBytes;
    }

    public long SubmittedCount { get; }
    public long CompletedCount { get; }
    public long AppliedCount { get; }
    public long CacheHitCount { get; }
    public long RejectedCount { get; }
    public long SupersededCount { get; }
    public long StoppedCount { get; }
    public long CallerCanceledCount { get; }
    public long FaultedCount { get; }
    public long CanceledWorkCount =>
        SaturatingAdd(
            SaturatingAdd(
                SupersededCount,
                StoppedCount),
            CallerCanceledCount);
    public int CurrentQueueDepth { get; }
    public int MaximumQueueDepth { get; }
    public TimeSpan TotalDuration { get; }
    public TimeSpan LastDuration { get; }
    public TimeSpan MaximumDuration { get; }
    public long AllocationMeasurementCount { get; }
    public bool HasAllocationMeasurements =>
        AllocationMeasurementCount != 0;
    public long TotalAllocatedBytes { get; }
    public long LastAllocatedBytes { get; }
    public long MaximumAllocatedBytes { get; }

    private static long SaturatingAdd(
        long left,
        long right) =>
        left > long.MaxValue - right
            ? long.MaxValue
            : left + right;
}
