namespace System.Drawing;

public abstract class Brush : IDisposable
{
    public abstract ProGPU.Vector.Brush ToProGpuBrush();
    public virtual void Dispose() {}
}
