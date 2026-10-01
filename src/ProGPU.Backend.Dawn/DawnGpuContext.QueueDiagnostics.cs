namespace ProGPU.Backend.Dawn;

public sealed unsafe partial class DawnGpuContext
{
    private sealed class DawnQueueWaitAbandonmentProbe
    {
        internal readonly Exception Failure = new InvalidOperationException("Abandon queued Dawn work notification before its wait.");
        internal DawnQueueCompletion? Completion;
    }

    internal static void VerifyDawnQueueWaitAbandonmentForDiagnostics()
    {
        using DawnGpuContext owner = CreateSystemWarpOffscreen();
        var probe = new DawnQueueWaitAbandonmentProbe();
        try
        {
            WaitForQueue(owner.Instance, owner.Queue, probe);
            throw new InvalidOperationException("Queue wait abandonment was not reached.");
        }
        catch (Exception error) when (ReferenceEquals(error, probe.Failure)) { }

        // This is the unchanged real queue wait, not a sleep or fake completion.
        // The first notification can complete synchronously or during progress;
        // deterministic late-callback order is covered independently in unit tests.
        owner.Context.WaitIdle();
        DawnQueueCompletion completion = probe.Completion
            ?? throw new InvalidOperationException("No native queue notification was registered.");
        completion.RequireSuccess();
        if (completion.Retirement != (1, 1))
            throw new InvalidOperationException($"Unexpected queue callback retirement: {completion.Retirement}.");
    }
}
