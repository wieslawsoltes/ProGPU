namespace ProGPU.Backend.Dawn;

// InitializeExternalNativeDevice can fail before or after accepting its lifetime.
// The factory and context therefore share one releasing owner, not two raw copies.
internal sealed class DawnDeviceLifetime(IWebGpuExternalDeviceLifetime lifetime) : IWebGpuExternalDeviceLifetime
{
    private IWebGpuExternalDeviceLifetime? _lifetime = lifetime;

    public void Poll(bool wait)
    {
        IWebGpuExternalDeviceLifetime? active = Volatile.Read(ref _lifetime);
        ObjectDisposedException.ThrowIf(active is null, this);
        active.Poll(wait);
    }

    public void Dispose() => Interlocked.Exchange(ref _lifetime, null)?.Dispose();
}
