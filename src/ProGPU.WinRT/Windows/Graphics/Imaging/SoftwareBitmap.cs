namespace Windows.Graphics.Imaging;

public enum BitmapPixelFormat
{
    Unknown = 0,
    Rgba16 = 12,
    Rgba8 = 30,
    Gray16 = 57,
    Gray8 = 62,
    Bgra8 = 87,
    Nv12 = 103,
    P010 = 104,
    Yuy2 = 107
}

public enum BitmapAlphaMode
{
    Premultiplied = 0,
    Straight = 1,
    Ignore = 2
}

public sealed class SoftwareBitmap :
    IDisposable
{
    private int _isDisposed;

    public SoftwareBitmap(
        BitmapPixelFormat pixelFormat,
        int width,
        int height)
        : this(
            pixelFormat,
            width,
            height,
            BitmapAlphaMode.Premultiplied)
    {
    }

    public SoftwareBitmap(
        BitmapPixelFormat pixelFormat,
        int width,
        int height,
        BitmapAlphaMode alphaMode)
    {
        if (pixelFormat ==
            BitmapPixelFormat.Unknown)
        {
            throw new ArgumentException(
                "A concrete pixel format is required.",
                nameof(pixelFormat));
        }
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width));
        }
        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height));
        }

        BitmapPixelFormat = pixelFormat;
        BitmapAlphaMode = alphaMode;
        PixelWidth = width;
        PixelHeight = height;
    }

    public BitmapAlphaMode BitmapAlphaMode
    {
        get;
    }

    public BitmapPixelFormat BitmapPixelFormat
    {
        get;
    }

    public int PixelHeight { get; }

    public int PixelWidth { get; }

    public bool IsReadOnly { get; private set; }

    public void Close() => Dispose();

    public void Dispose()
    {
        Interlocked.Exchange(
            ref _isDisposed,
            1);
    }

    public void MakeReadOnly()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _isDisposed) != 0,
            this);
        IsReadOnly = true;
    }

    internal void VerifyAvailable() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _isDisposed) != 0,
            this);
}
