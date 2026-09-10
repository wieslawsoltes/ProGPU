using System;

namespace System.Drawing;

[AttributeUsage(AttributeTargets.Assembly)]
public class BitmapSuffixInSatelliteAssemblyAttribute : Attribute
{
}

public static class BufferedGraphicsManager
{
    private static readonly BufferedGraphicsContext s_current = new();
    public static BufferedGraphicsContext Current => s_current;
}

public sealed class BufferedGraphicsContext : IDisposable
{
    private Size _maximumBuffer = new(225, 96);

    public Size MaximumBuffer
    {
        get => _maximumBuffer;
        set
        {
            if (value.Width <= 0 || value.Height <= 0)
                throw new ArgumentException("MaximumBuffer must be positive.", nameof(value));
            _maximumBuffer = value;
        }
    }

    public BufferedGraphics Allocate(Graphics targetGraphics, Rectangle targetRectangle)
    {
        ArgumentNullException.ThrowIfNull(targetGraphics);
        if (targetRectangle.Width <= 0 || targetRectangle.Height <= 0)
            throw new ArgumentException("The buffer rectangle must be positive.", nameof(targetRectangle));
        return new BufferedGraphics(targetGraphics, targetRectangle);
    }

    public BufferedGraphics Allocate(IntPtr targetDC, Rectangle targetRectangle) =>
        throw new PlatformNotSupportedException("HDC buffering requires the Windows GDI adapter.");

    public void Invalidate() { }
    public void Dispose() { }
}

public sealed class BufferedGraphics : IDisposable
{
    private readonly Graphics _target;
    private readonly Rectangle _targetRectangle;
    private readonly Bitmap _buffer;
    private bool _disposed;

    internal BufferedGraphics(Graphics target, Rectangle targetRectangle)
    {
        _target = target;
        _targetRectangle = targetRectangle;
        _buffer = new Bitmap(targetRectangle.Width, targetRectangle.Height);
        Graphics = Graphics.FromImage(_buffer);
        Graphics.TranslateTransform(-targetRectangle.X, -targetRectangle.Y);
    }

    public Graphics Graphics { get; }
    public void Render() => Render(_target);

    public void Render(Graphics target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        target.DrawImage(_buffer, _targetRectangle);
    }

    public void Render(IntPtr targetDC) =>
        throw new PlatformNotSupportedException("HDC buffering requires the Windows GDI adapter.");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Graphics.Dispose();
        _buffer.Dispose();
    }
}
