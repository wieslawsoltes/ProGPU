using System;

namespace SkiaSharp;

public abstract class SKAbstractManagedStream : SKStreamAsset
{
    protected SKAbstractManagedStream()
        : this(owns: true)
    {
    }

    protected SKAbstractManagedStream(bool owns)
        : base(Array.Empty<byte>())
    {
        OwnsHandle = owns;
    }

    protected internal abstract IntPtr OnRead(IntPtr buffer, IntPtr size);
    protected internal abstract IntPtr OnPeek(IntPtr buffer, IntPtr size);
    protected internal abstract bool OnIsAtEnd();
    protected internal abstract bool OnHasPosition();
    protected internal abstract bool OnHasLength();
    protected internal abstract bool OnRewind();
    protected internal abstract IntPtr OnGetPosition();
    protected internal abstract IntPtr OnGetLength();
    protected internal abstract bool OnSeek(IntPtr position);
    protected internal abstract bool OnMove(int offset);
    protected internal abstract IntPtr OnCreateNew();

    protected internal virtual IntPtr OnFork() => OnCreateNew();
    protected internal virtual IntPtr OnDuplicate() => OnCreateNew();

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
