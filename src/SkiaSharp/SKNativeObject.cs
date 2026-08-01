namespace SkiaSharp;

internal interface ISKSkipObjectRegistration
{
}

public abstract class SKNativeObject : IDisposable
{
    private int _disposed;
    private bool _ignorePublicDispose;

    internal SKNativeObject(IntPtr handle)
        : this(handle, ownsHandle: true)
    {
    }

    internal SKNativeObject(IntPtr handle, bool ownsHandle)
    {
        Handle = handle;
        OwnsHandle = ownsHandle;
    }

    ~SKNativeObject()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            Dispose(disposing: false);
        }
    }

    public virtual IntPtr Handle { get; protected set; }
    protected internal virtual bool OwnsHandle { get; protected set; }
    protected internal bool IgnorePublicDispose => _ignorePublicDispose;
    protected internal bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    internal void PreventPublicDisposal() => _ignorePublicDispose = true;

    protected virtual void DisposeUnownedManaged()
    {
    }

    protected virtual void DisposeManaged()
    {
    }

    protected virtual void DisposeNative()
    {
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeUnownedManaged();
        }

        if (Handle != IntPtr.Zero && OwnsHandle)
        {
            DisposeNative();
        }

        if (disposing)
        {
            DisposeManaged();
        }

        Handle = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (IgnorePublicDispose || Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected internal void DisposeInternal()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}

public abstract class SKObject : SKNativeObject
{
    internal SKObject(IntPtr handle, bool owns)
        : base(handle, owns)
    {
    }

    public override IntPtr Handle
    {
        get => base.Handle;
        protected set => base.Handle = value;
    }

    protected override void DisposeUnownedManaged()
    {
        base.DisposeUnownedManaged();
    }

    protected override void DisposeManaged()
    {
        base.DisposeManaged();
    }

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
