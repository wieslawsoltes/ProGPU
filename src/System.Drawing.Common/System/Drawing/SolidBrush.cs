using System.Numerics;

namespace System.Drawing;

public class SolidBrush : Brush
{
    private Color _color;
    private bool _disposed;

    public Color Color
    {
        get
        {
            ThrowIfDisposed();
            return _color;
        }
        set
        {
            ThrowIfDisposed();
            _color = value;
        }
    }

    public SolidBrush(Color color)
    {
        _color = color;
    }

    public override object Clone()
    {
        ThrowIfDisposed();
        return new SolidBrush(_color);
    }

    internal override ProGPU.Vector.Brush ToProGpuBrush()
    {
        ThrowIfDisposed();
        return new ProGPU.Vector.SolidColorBrush(new Vector4(_color.R / 255f, _color.G / 255f, _color.B / 255f, _color.A / 255f));
    }

    protected override void Dispose(bool disposing) => _disposed = true;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
