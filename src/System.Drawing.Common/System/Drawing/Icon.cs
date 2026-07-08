using System.Collections.Generic;
using System.Threading;

namespace System.Drawing;

public sealed class Icon : IDisposable
{
    private static long s_nextHandle;
    private static readonly object s_handleSync = new();
    private static readonly Dictionary<IntPtr, Bitmap> s_handleBitmaps = new();

    private readonly Bitmap? _bitmap;

    public Icon(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        using var stream = File.OpenRead(fileName);
        _bitmap = new Bitmap(stream);
    }

    public Icon(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _bitmap = new Bitmap(stream);
    }

    public Icon(Icon original, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        _bitmap = original._bitmap is null ? new Bitmap(width, height) : new Bitmap(original._bitmap);
    }

    private Icon(IntPtr handle)
    {
        lock (s_handleSync)
        {
            if (s_handleBitmaps.TryGetValue(handle, out Bitmap? bitmap))
            {
                _bitmap = new Bitmap(bitmap);
                return;
            }
        }

        _bitmap = new Bitmap(1, 1);
    }

    public int Width => _bitmap?.Width ?? 0;

    public int Height => _bitmap?.Height ?? 0;

    public Size Size => new(Width, Height);

    public Bitmap ToBitmap()
    {
        return _bitmap is null ? new Bitmap(1, 1) : new Bitmap(_bitmap);
    }

    public static Icon FromHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            throw new ArgumentException("Invalid icon handle.", nameof(handle));

        return new Icon(handle);
    }

    internal static IntPtr RegisterBitmapHandle(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        bitmap.Flush();

        IntPtr handle = new(Interlocked.Increment(ref s_nextHandle));
        lock (s_handleSync)
        {
            s_handleBitmaps[handle] = new Bitmap(bitmap);
        }

        return handle;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
    }
}
