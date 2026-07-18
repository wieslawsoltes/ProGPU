using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Snapshot of opt-in queue-completion instrumentation. Submitted frames are compositor
/// frame submissions, while acknowledged frames are queue callbacks that have fired.
/// </summary>
public readonly record struct GpuFrameCompletionMetrics(
    long SubmittedFrames,
    long AcknowledgedFrames,
    long CompletedFrames,
    long FailedFrames,
    int InFlightFrames,
    int MaxInFlightFrames,
    long LastCompletionTimestamp)
{
    public bool HasCompletion => LastCompletionTimestamp != 0;
}

/// <summary>
/// Tracks queue completion without allocating one managed state object per frame. Every callback
/// shares one rooted tracker handle; WebGPU guarantees submitted-work callbacks observe queue order.
/// The tracker is created only for explicit performance diagnostics.
/// </summary>
internal sealed unsafe class GpuFrameCompletionTracker : IDisposable
{
    private readonly object _lifetimeLock = new();
    private GCHandle _selfHandle;
    private long _submittedFrames;
    private long _acknowledgedFrames;
    private long _completedFrames;
    private long _failedFrames;
    private long _lastCompletionTimestamp;
    private int _inFlightFrames;
    private int _maxInFlightFrames;
    private int _disposed;

    public GpuFrameCompletionTracker()
    {
        _selfHandle = GCHandle.Alloc(this);
    }

    public GpuFrameCompletionMetrics Metrics => new(
        Interlocked.Read(ref _submittedFrames),
        Interlocked.Read(ref _acknowledgedFrames),
        Interlocked.Read(ref _completedFrames),
        Interlocked.Read(ref _failedFrames),
        Volatile.Read(ref _inFlightFrames),
        Volatile.Read(ref _maxInFlightFrames),
        Interlocked.Read(ref _lastCompletionTimestamp));

    public void RecordSubmission(IWebGpuApi api, Queue* queue)
    {
        if (Volatile.Read(ref _disposed) != 0 || queue == null)
        {
            return;
        }

        Interlocked.Increment(ref _submittedFrames);
        int inFlight = Interlocked.Increment(ref _inFlightFrames);
        UpdateMaximum(ref _maxInFlightFrames, inFlight);

        try
        {
            api.QueueOnSubmittedWorkDone(
                queue,
                new PfnQueueWorkDoneCallback(&OnSubmittedWorkDone),
                (void*)GCHandle.ToIntPtr(_selfHandle));
        }
        catch
        {
            Complete(QueueWorkDoneStatus.Error);
            throw;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnSubmittedWorkDone(QueueWorkDoneStatus status, void* userData)
    {
        if (userData == null)
        {
            return;
        }

        var handle = GCHandle.FromIntPtr((nint)userData);
        if (handle.Target is GpuFrameCompletionTracker tracker)
        {
            tracker.Complete(status);
        }
    }

    private void Complete(QueueWorkDoneStatus status)
    {
        Interlocked.Increment(ref _acknowledgedFrames);
        if (status == QueueWorkDoneStatus.Success)
        {
            Interlocked.Increment(ref _completedFrames);
        }
        else
        {
            Interlocked.Increment(ref _failedFrames);
        }

        Interlocked.Exchange(ref _lastCompletionTimestamp, Stopwatch.GetTimestamp());
        int remaining = Interlocked.Decrement(ref _inFlightFrames);
        if (remaining == 0 && Volatile.Read(ref _disposed) != 0)
        {
            ReleaseHandle();
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int current = Volatile.Read(ref target);
        while (candidate > current)
        {
            int observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _inFlightFrames) == 0)
        {
            ReleaseHandle();
        }
    }

    private void ReleaseHandle()
    {
        lock (_lifetimeLock)
        {
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
        }
    }
}
