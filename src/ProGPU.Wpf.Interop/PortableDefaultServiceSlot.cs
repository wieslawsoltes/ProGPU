namespace ProGPU.Wpf.Interop;

/// <summary>
/// Process default plus a replaceable explicit override. Registration is configuration,
/// not ownership of the service. Disposing the current override reveals the default;
/// it never resurrects an older override or disposes an in-flight reader's service.
/// </summary>
internal sealed class PortableDefaultServiceSlot<T> where T : class
{
    private T? _default;
    private Registration? _override;

    public T? Current => Volatile.Read(ref _override)?.Service ?? Volatile.Read(ref _default);

    public void EnsureDefault(T service)
    {
        ArgumentNullException.ThrowIfNull(service);
        Interlocked.CompareExchange(ref _default, service, null);
    }

    public IDisposable Register(T service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var registration = new Registration(this, service);
        Interlocked.Exchange(ref _override, registration);
        return registration;
    }

    private sealed class Registration(PortableDefaultServiceSlot<T> owner, T service) : IDisposable
    {
        internal T Service { get; } = service;
        public void Dispose() => Interlocked.CompareExchange(ref owner._override, null, this);
    }
}
