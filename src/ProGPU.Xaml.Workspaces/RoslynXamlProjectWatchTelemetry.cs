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
        TimeSpan medianDurationUpperBound,
        TimeSpan p95DurationUpperBound,
        TimeSpan p99DurationUpperBound,
        long allocationMeasurementCount,
        long totalAllocatedBytes,
        long lastAllocatedBytes,
        long maximumAllocatedBytes,
        long medianAllocatedBytesUpperBound,
        long p95AllocatedBytesUpperBound,
        long p99AllocatedBytesUpperBound)
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
        MedianDurationUpperBound =
            medianDurationUpperBound;
        P95DurationUpperBound =
            p95DurationUpperBound;
        P99DurationUpperBound =
            p99DurationUpperBound;
        AllocationMeasurementCount =
            allocationMeasurementCount;
        TotalAllocatedBytes = totalAllocatedBytes;
        LastAllocatedBytes = lastAllocatedBytes;
        MaximumAllocatedBytes = maximumAllocatedBytes;
        MedianAllocatedBytesUpperBound =
            medianAllocatedBytesUpperBound;
        P95AllocatedBytesUpperBound =
            p95AllocatedBytesUpperBound;
        P99AllocatedBytesUpperBound =
            p99AllocatedBytesUpperBound;
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
    public TimeSpan AverageDuration =>
        CompletedCount == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(
                TotalDuration.Ticks /
                CompletedCount);
    public TimeSpan MedianDurationUpperBound { get; }
    public TimeSpan P95DurationUpperBound { get; }
    public TimeSpan P99DurationUpperBound { get; }
    public long AllocationMeasurementCount { get; }
    public bool HasAllocationMeasurements =>
        AllocationMeasurementCount != 0;
    public long TotalAllocatedBytes { get; }
    public long LastAllocatedBytes { get; }
    public long MaximumAllocatedBytes { get; }
    public long AverageAllocatedBytes =>
        AllocationMeasurementCount == 0
            ? 0
            : TotalAllocatedBytes /
              AllocationMeasurementCount;
    public long MedianAllocatedBytesUpperBound { get; }
    public long P95AllocatedBytesUpperBound { get; }
    public long P99AllocatedBytesUpperBound { get; }

    private static long SaturatingAdd(
        long left,
        long right) =>
        left > long.MaxValue - right
            ? long.MaxValue
            : left + right;
}

/// <summary>
/// A fixed base-2 cumulative histogram for non-negative 64-bit measurements.
/// Recording is allocation-free and bounded to at most 63 shifts. Quantiles are
/// nearest-rank bucket upper bounds, so they never claim precision absent from
/// the retained distribution.
/// </summary>
internal sealed class RoslynXamlProjectWatchHistogram
{
    private const int BucketCount = 65;
    private readonly long[] _buckets =
        new long[BucketCount];
    private long _count;

    public void Record(long value)
    {
        if (_count == long.MaxValue)
            return;

        var bucket = GetBucketIndex(
            value < 0
                ? 0
                : value);
        _count++;
        _buckets[bucket]++;
    }

    public void GetUpperBounds(
        out long median,
        out long p95,
        out long p99)
    {
        if (_count == 0)
        {
            median = 0;
            p95 = 0;
            p99 = 0;
            return;
        }

        var medianRank = GetRank(50);
        var p95Rank = GetRank(95);
        var p99Rank = GetRank(99);
        median = 0;
        p95 = 0;
        p99 = 0;
        var hasMedian = false;
        var hasP95 = false;
        long cumulative = 0;
        for (var index = 0;
             index < _buckets.Length;
             index++)
        {
            cumulative += _buckets[index];
            var upperBound =
                GetBucketUpperBound(index);
            if (!hasMedian &&
                cumulative >= medianRank)
            {
                median = upperBound;
                hasMedian = true;
            }
            if (!hasP95 &&
                cumulative >= p95Rank)
            {
                p95 = upperBound;
                hasP95 = true;
            }
            if (cumulative >= p99Rank)
            {
                p99 = upperBound;
                return;
            }
        }

        p99 = long.MaxValue;
    }

    private long GetRank(
        int percentile)
    {
        var quotient = _count / 100;
        var remainder = _count % 100;
        return quotient * percentile +
            (remainder * percentile + 99) /
            100;
    }

    private static int GetBucketIndex(
        long value)
    {
        if (value == 0)
            return 0;

        var remaining =
            (ulong)(value - 1);
        var index = 1;
        while (remaining != 0)
        {
            remaining >>= 1;
            index++;
        }

        return index;
    }

    private static long GetBucketUpperBound(
        int index)
    {
        if (index == 0)
            return 0;
        if (index == BucketCount - 1)
            return long.MaxValue;
        return 1L << (index - 1);
    }
}
