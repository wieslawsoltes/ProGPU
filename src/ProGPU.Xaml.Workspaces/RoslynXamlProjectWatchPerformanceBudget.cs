using System;

namespace ProGPU.Xaml.Workspaces;

public enum RoslynXamlProjectWatchBudgetStatus
{
    InsufficientSamples,
    Passed,
    Exceeded
}

[Flags]
public enum RoslynXamlProjectWatchBudgetViolation
{
    None = 0,
    P95Duration = 1,
    P95AllocatedBytes = 2
}

/// <summary>
/// Defines an immutable percentile budget for one project-watch session.
/// Evaluation reads only the cumulative telemetry snapshot and performs fixed
/// work without retaining samples or allocating.
/// </summary>
public sealed class
    RoslynXamlProjectWatchPerformanceBudget
{
    public RoslynXamlProjectWatchPerformanceBudget(
        long minimumSampleCount,
        TimeSpan? maximumP95Duration = null,
        long? maximumP95AllocatedBytes = null)
    {
        if (minimumSampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumSampleCount),
                "The minimum sample count must be positive.");
        }
        if (maximumP95Duration.HasValue &&
            maximumP95Duration.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumP95Duration),
                "The P95 duration budget cannot be negative.");
        }
        if (maximumP95AllocatedBytes.HasValue &&
            maximumP95AllocatedBytes.Value < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumP95AllocatedBytes),
                "The P95 allocation budget cannot be negative.");
        }
        if (!maximumP95Duration.HasValue &&
            !maximumP95AllocatedBytes.HasValue)
        {
            throw new ArgumentException(
                "At least one P95 budget must be specified.");
        }

        MinimumSampleCount = minimumSampleCount;
        MaximumP95Duration = maximumP95Duration;
        MaximumP95AllocatedBytes =
            maximumP95AllocatedBytes;
    }

    public long MinimumSampleCount { get; }

    public TimeSpan? MaximumP95Duration { get; }

    public long? MaximumP95AllocatedBytes { get; }

    public RoslynXamlProjectWatchBudgetResult Evaluate(
        in RoslynXamlProjectWatchTelemetry telemetry)
    {
        var durationReady =
            !MaximumP95Duration.HasValue ||
            telemetry.CompletedCount >=
            MinimumSampleCount;
        var allocationReady =
            !MaximumP95AllocatedBytes.HasValue ||
            telemetry.AllocationMeasurementCount >=
            MinimumSampleCount;
        if (!durationReady || !allocationReady)
        {
            return new RoslynXamlProjectWatchBudgetResult(
                RoslynXamlProjectWatchBudgetStatus
                    .InsufficientSamples,
                RoslynXamlProjectWatchBudgetViolation
                    .None,
                telemetry.CompletedCount,
                telemetry.AllocationMeasurementCount,
                telemetry.P95DurationUpperBound,
                telemetry.P95AllocatedBytesUpperBound);
        }

        var violations =
            RoslynXamlProjectWatchBudgetViolation.None;
        if (MaximumP95Duration.HasValue &&
            telemetry.P95DurationUpperBound >
            MaximumP95Duration.Value)
        {
            violations |=
                RoslynXamlProjectWatchBudgetViolation
                    .P95Duration;
        }
        if (MaximumP95AllocatedBytes.HasValue &&
            telemetry.P95AllocatedBytesUpperBound >
            MaximumP95AllocatedBytes.Value)
        {
            violations |=
                RoslynXamlProjectWatchBudgetViolation
                    .P95AllocatedBytes;
        }

        return new RoslynXamlProjectWatchBudgetResult(
            violations ==
                RoslynXamlProjectWatchBudgetViolation.None
                ? RoslynXamlProjectWatchBudgetStatus.Passed
                : RoslynXamlProjectWatchBudgetStatus
                    .Exceeded,
            violations,
            telemetry.CompletedCount,
            telemetry.AllocationMeasurementCount,
            telemetry.P95DurationUpperBound,
            telemetry.P95AllocatedBytesUpperBound);
    }
}

public readonly struct
    RoslynXamlProjectWatchBudgetResult
{
    internal RoslynXamlProjectWatchBudgetResult(
        RoslynXamlProjectWatchBudgetStatus status,
        RoslynXamlProjectWatchBudgetViolation violations,
        long completedSampleCount,
        long allocationSampleCount,
        TimeSpan p95DurationUpperBound,
        long p95AllocatedBytesUpperBound)
    {
        Status = status;
        Violations = violations;
        CompletedSampleCount = completedSampleCount;
        AllocationSampleCount = allocationSampleCount;
        P95DurationUpperBound = p95DurationUpperBound;
        P95AllocatedBytesUpperBound =
            p95AllocatedBytesUpperBound;
    }

    public RoslynXamlProjectWatchBudgetStatus Status
    {
        get;
    }

    public RoslynXamlProjectWatchBudgetViolation Violations
    {
        get;
    }

    public long CompletedSampleCount { get; }

    public long AllocationSampleCount { get; }

    public TimeSpan P95DurationUpperBound { get; }

    public long P95AllocatedBytesUpperBound { get; }

    public bool IsConclusive =>
        Status !=
        RoslynXamlProjectWatchBudgetStatus
            .InsufficientSamples;

    public bool Passed =>
        Status ==
        RoslynXamlProjectWatchBudgetStatus.Passed;
}
