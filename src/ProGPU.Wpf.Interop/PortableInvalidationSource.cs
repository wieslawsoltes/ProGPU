namespace ProGPU.Wpf.Interop;

public interface IPortableInvalidationSource
{
    bool TrySubscribeInvalidated(EventHandler handler, out IDisposable subscription);
}

public sealed class PortableInvalidationSubscription : IDisposable
{
    private Action? _unsubscribe;

    public PortableInvalidationSubscription(Action unsubscribe)
    {
        ArgumentNullException.ThrowIfNull(unsubscribe);
        _unsubscribe = unsubscribe;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
