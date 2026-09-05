namespace System.Drawing;

public abstract class Brush : MarshalByRefObject, ICloneable, IDisposable
{
    public abstract object Clone();

    internal virtual ProGPU.Vector.Brush ToProGpuBrush() =>
        throw new NotSupportedException(
            $"Drawing with brush type {GetType().FullName} requires a typed ProGPU brush adapter.");

    protected internal void SetNativeBrush(IntPtr brush) =>
        throw new PlatformNotSupportedException(
            "Native GDI+ brush handles require an explicit Windows drawing adapter.");

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
