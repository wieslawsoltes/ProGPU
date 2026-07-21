using System;
using System.Threading;

namespace Windows.Foundation;

public delegate void DeferralCompletedHandler();

/// <summary>
/// Represents work that must complete before a platform operation may continue.
/// Completion is idempotent, matching the WinRT <c>Deferral</c> contract.
/// </summary>
public sealed class Deferral : IDisposable
{
    private DeferralCompletedHandler? _completedHandler;

    public Deferral(DeferralCompletedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _completedHandler = handler;
    }

    public void Complete() =>
        Interlocked.Exchange(ref _completedHandler, null)?.Invoke();

    public void Close() => Complete();

    public void Dispose() => Complete();
}
