using System.Numerics;

namespace System.Drawing;

public class SolidBrush : Brush
{
    private Color _color;
    private bool _disposed;
    private readonly bool _immutable;

    public Color Color
    {
        get
        {
            if (_disposed && _color.ToArgb() == 0)
            {
                throw new ArgumentException("The brush has been disposed.");
            }

            return _color;
        }
        set
        {
            ThrowIfDisposed();
            ThrowIfImmutable();
            _color = value;
        }
    }

    public SolidBrush(Color color)
        : this(color, immutable: false)
    {
    }

    internal SolidBrush(Color color, bool immutable)
    {
        _color = color.IsEmpty ? Color.FromArgb(0) : color;
        _immutable = immutable;
    }

    public override object Clone()
    {
        ThrowIfDisposed();
        return new SolidBrush(Color.FromArgb(_color.ToArgb()));
    }

    internal override ProGPU.Vector.Brush ToProGpuBrush()
    {
        ThrowIfDisposed();
        return new ProGPU.Vector.SolidColorBrush(new Vector4(_color.R / 255f, _color.G / 255f, _color.B / 255f, _color.A / 255f));
    }

    protected override void Dispose(bool disposing)
    {
        ThrowIfImmutable();
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ArgumentException("The brush has been disposed.");
        }
    }

    private void ThrowIfImmutable()
    {
        if (_immutable)
        {
            throw new ArgumentException("Changes cannot be made to an immutable system brush.");
        }
    }
}
