namespace ProGPU.Backend.Native;

// The C++ context mutates retained plans and fallback-font storage. A use owns
// both pointer lifetime and exclusive access for the entire synchronous call.
internal sealed class NativeTextContextOwner(nint handle, Action<nint> release) : IDisposable
{
    private readonly object _gate = new();
    private nint _handle = handle;
    private int _uses;
    private bool _disposed;

    internal Use Acquire()
    {
        Monitor.Enter(_gate);
        try
        {
            if (_disposed || _handle == 0)
                throw new ObjectDisposedException(nameof(NativeTextShapingContext));
            _uses++;
            return new Use(this, _handle);
        }
        catch
        {
            Monitor.Exit(_gate);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ReleaseIfUnused();
        }
    }

    private void ReleaseIfUnused()
    {
        if (_disposed && _uses == 0 && _handle != 0)
        {
            nint handleToRelease = _handle;
            _handle = 0;
            release(handleToRelease);
        }
    }

    private void EndUse()
    {
        try
        {
            _uses--;
            ReleaseIfUnused();
        }
        finally { Monitor.Exit(_gate); }
    }

    // Stack-only, same-thread scope: no per-operation delegate or heap lease.
    // Callers use one using declaration and must not copy/dispose it twice.
    internal readonly ref struct Use(NativeTextContextOwner owner, nint handle)
    {
        internal nint Handle => handle;
        public void Dispose() => owner.EndUse();
    }
}
