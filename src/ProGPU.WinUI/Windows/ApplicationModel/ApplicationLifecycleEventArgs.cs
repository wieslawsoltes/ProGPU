using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace Windows.ApplicationModel;

public sealed class SuspendingEventArgs : EventArgs
{
    internal SuspendingEventArgs(SuspendingOperation operation)
    {
        SuspendingOperation = operation;
    }

    public SuspendingOperation SuspendingOperation { get; }
}

public sealed class SuspendingOperation
{
    private readonly DeferralTracker _deferrals;

    internal SuspendingOperation(DateTimeOffset deadline, DeferralTracker deferrals)
    {
        Deadline = deadline;
        _deferrals = deferrals;
    }

    public DateTimeOffset Deadline { get; }

    public SuspendingDeferral GetDeferral() => new(_deferrals.GetDeferral());
}

public sealed class SuspendingDeferral : IDisposable
{
    private Deferral? _deferral;

    internal SuspendingDeferral(Deferral deferral)
    {
        _deferral = deferral;
    }

    public void Complete() =>
        Interlocked.Exchange(ref _deferral, null)?.Complete();

    public void Close() => Complete();

    public void Dispose() => Complete();
}

public sealed class EnteredBackgroundEventArgs : EventArgs
{
    private readonly DeferralTracker _deferrals;

    internal EnteredBackgroundEventArgs(DeferralTracker deferrals)
    {
        _deferrals = deferrals;
    }

    public Deferral GetDeferral() => _deferrals.GetDeferral();
}

public sealed class LeavingBackgroundEventArgs : EventArgs
{
    private readonly DeferralTracker _deferrals;

    internal LeavingBackgroundEventArgs(DeferralTracker deferrals)
    {
        _deferrals = deferrals;
    }

    public Deferral GetDeferral() => _deferrals.GetDeferral();
}

internal sealed class DeferralTracker
{
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _remaining = 1;
    private int _sealed;

    public Deferral GetDeferral()
    {
        if (Volatile.Read(ref _sealed) != 0)
            throw new InvalidOperationException("A deferral cannot be requested after the event handler has returned.");

        Interlocked.Increment(ref _remaining);
        if (Volatile.Read(ref _sealed) != 0)
        {
            CompleteOne();
            throw new InvalidOperationException("A deferral cannot be requested after the event handler has returned.");
        }

        return new Deferral(CompleteOne);
    }

    public Task SealAndWaitAsync()
    {
        if (Interlocked.Exchange(ref _sealed, 1) == 0)
            CompleteOne();

        return _completion.Task;
    }

    private void CompleteOne()
    {
        int remaining = Interlocked.Decrement(ref _remaining);
        if (remaining == 0)
            _completion.TrySetResult();
        else if (remaining < 0)
            throw new InvalidOperationException("A lifecycle deferral was completed more than once.");
    }
}
