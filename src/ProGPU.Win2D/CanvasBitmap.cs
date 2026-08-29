using System.Buffers;
using System.Numerics;
using ProGPU.Backend;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace Microsoft.Graphics.Canvas;

/// <summary>GPU-resident Win2D-shaped bitmap resource.</summary>
public abstract class CanvasBitmap :
    ICanvasImage,
    ICanvasResourceCreatorWithDpi,
    IProGpuTextureLeaseSource,
    IDisposable
{
    private readonly object _lifetimeLock = new();
    private bool _isDisposed;
    private bool _textureDisposed;
    private int _leaseCount;

    internal CanvasBitmap(
        CanvasDevice device,
        GpuTexture texture,
        float width,
        float height,
        float dpi,
        DirectXPixelFormat format,
        CanvasAlphaMode alphaMode)
    {
        Device = device;
        Texture = texture;
        Size = new Size(width, height);
        Dpi = dpi;
        Format = format;
        AlphaMode = alphaMode;
    }

    public CanvasDevice Device { get; }

    public float Dpi { get; }

    public Size Size { get; }

    public Rect Bounds => new(0d, 0d, Size.Width, Size.Height);

    public BitmapSize SizeInPixels => new()
    {
        Width = Texture.Width,
        Height = Texture.Height
    };

    public DirectXPixelFormat Format { get; }

    public CanvasAlphaMode AlphaMode { get; }

    internal GpuTexture Texture { get; }

    public bool IsDisposed => _isDisposed;

    public float ConvertPixelsToDips(int pixels)
    {
        ThrowIfDisposed();
        return pixels * CanvasContract.DefaultDpi / Dpi;
    }

    public int ConvertDipsToPixels(
        float dips,
        CanvasDpiRounding dpiRounding)
    {
        ThrowIfDisposed();
        return CanvasContract.DipsToPixels(dips, Dpi, dpiRounding);
    }

    public byte[] GetPixelBytes()
    {
        ThrowIfDisposed();
        Device.Context.WaitIdle();
        return Texture.ReadPixels();
    }

    public static CanvasBitmap CreateFromBytes(
        ICanvasResourceCreator resourceCreator,
        byte[] bytes,
        int widthInPixels,
        int heightInPixels,
        DirectXPixelFormat format) =>
        CreateFromBytes(
            resourceCreator,
            bytes,
            widthInPixels,
            heightInPixels,
            format,
            CanvasContract.DefaultDpi,
            CanvasAlphaMode.Premultiplied);

    public static CanvasBitmap CreateFromBytes(
        ICanvasResourceCreator resourceCreator,
        byte[] bytes,
        int widthInPixels,
        int heightInPixels,
        DirectXPixelFormat format,
        float dpi) =>
        CreateFromBytes(
            resourceCreator,
            bytes,
            widthInPixels,
            heightInPixels,
            format,
            dpi,
            CanvasAlphaMode.Premultiplied);

    public static CanvasBitmap CreateFromBytes(
        ICanvasResourceCreator resourceCreator,
        byte[] bytes,
        int widthInPixels,
        int heightInPixels,
        DirectXPixelFormat format,
        float dpi,
        CanvasAlphaMode alphaMode)
    {
        ArgumentNullException.ThrowIfNull(resourceCreator);
        ArgumentNullException.ThrowIfNull(bytes);
        CanvasDevice device = resourceCreator.Device ??
            throw new ArgumentException(
                "The resource creator did not provide a CanvasDevice.",
                nameof(resourceCreator));
        if (device.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(resourceCreator));
        }
        CanvasContract.ValidateDpi(dpi);
        CanvasContract.ValidateFormat(format);
        CanvasContract.ValidateAlphaMode(alphaMode);
        ValidatePixelDimensions(widthInPixels, heightInPixels);
        int requiredByteCount = ValidatePixelByteCount(
            bytes.Length,
            widthInPixels,
            heightInPixels,
            nameof(bytes));

        var texture = new GpuTexture(
            device.Context,
            (uint)widthInPixels,
            (uint)heightInPixels,
            Silk.NET.WebGPU.TextureFormat.Bgra8Unorm,
            Silk.NET.WebGPU.TextureUsage.TextureBinding |
            Silk.NET.WebGPU.TextureUsage.CopySrc |
            Silk.NET.WebGPU.TextureUsage.CopyDst,
            "ProGPU Win2D CanvasBitmap",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        try
        {
            texture.WritePixels<byte>(bytes.AsSpan(0, requiredByteCount));
            float widthDips = widthInPixels *
                CanvasContract.DefaultDpi / dpi;
            float heightDips = heightInPixels *
                CanvasContract.DefaultDpi / dpi;
            return new UploadedCanvasBitmap(
                device,
                texture,
                widthDips,
                heightDips,
                dpi,
                format,
                alphaMode);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    public static CanvasBitmap CreateFromColors(
        ICanvasResourceCreator resourceCreator,
        Color[] colors,
        int widthInPixels,
        int heightInPixels) =>
        CreateFromColors(
            resourceCreator,
            colors,
            widthInPixels,
            heightInPixels,
            CanvasContract.DefaultDpi,
            CanvasAlphaMode.Premultiplied);

    public static CanvasBitmap CreateFromColors(
        ICanvasResourceCreator resourceCreator,
        Color[] colors,
        int widthInPixels,
        int heightInPixels,
        float dpi) =>
        CreateFromColors(
            resourceCreator,
            colors,
            widthInPixels,
            heightInPixels,
            dpi,
            CanvasAlphaMode.Premultiplied);

    public static CanvasBitmap CreateFromColors(
        ICanvasResourceCreator resourceCreator,
        Color[] colors,
        int widthInPixels,
        int heightInPixels,
        float dpi,
        CanvasAlphaMode alphaMode)
    {
        ArgumentNullException.ThrowIfNull(colors);
        CanvasDevice device = GetResourceCreatorDevice(resourceCreator);
        CanvasContract.ValidateDpi(dpi);
        CanvasContract.ValidateAlphaMode(alphaMode);
        ValidatePixelDimensions(widthInPixels, heightInPixels);
        int requiredColorCount = ValidatePixelColorCount(
            colors.Length,
            widthInPixels,
            heightInPixels,
            nameof(colors));
        int requiredByteCount = checked(requiredColorCount * 4);
        byte[] converted = ArrayPool<byte>.Shared.Rent(requiredByteCount);
        try
        {
            ProGpuCanvasCpuConversionPath path =
                CanvasColorBgraConverter.Convert(
                    colors.AsSpan(0, requiredColorCount),
                    converted.AsSpan(0, requiredByteCount),
                    device.PixelConversionMode);
            device.RecordPixelConversionPath(path);
            return CreateFromBytes(
                device,
                converted,
                widthInPixels,
                heightInPixels,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                dpi,
                alphaMode);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(converted);
        }
    }

    public void SetPixelBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_lifetimeLock)
        {
            ValidateCanMutate();
            int requiredByteCount = ValidatePixelByteCount(
                bytes.Length,
                checked((int)Texture.Width),
                checked((int)Texture.Height),
                nameof(bytes));
            Texture.WritePixels<byte>(bytes.AsSpan(0, requiredByteCount));
        }
    }

    public void SetPixelBytes(
        byte[] bytes,
        int left,
        int top,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_lifetimeLock)
        {
            ValidateCanMutate();
            ValidatePixelSubrectangle(
                left,
                top,
                width,
                height,
                Texture.Width,
                Texture.Height);
            int requiredByteCount = ValidatePixelByteCount(
                bytes.Length,
                width,
                height,
                nameof(bytes));
            Texture.WritePixelsSubRect<byte>(
                bytes.AsSpan(0, requiredByteCount),
                (uint)left,
                (uint)top,
                (uint)width,
                (uint)height);
        }
    }

    public void SetPixelColors(Color[] colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        lock (_lifetimeLock)
        {
            ValidateCanMutate();
            int requiredColorCount = ValidatePixelColorCount(
                colors.Length,
                checked((int)Texture.Width),
                checked((int)Texture.Height),
                nameof(colors));
            WritePixelColors(
                colors.AsSpan(0, requiredColorCount),
                x: 0,
                y: 0,
                Texture.Width,
                Texture.Height,
                fullTexture: true);
        }
    }

    public void SetPixelColors(
        Color[] colors,
        int left,
        int top,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(colors);
        lock (_lifetimeLock)
        {
            ValidateCanMutate();
            ValidatePixelSubrectangle(
                left,
                top,
                width,
                height,
                Texture.Width,
                Texture.Height);
            int requiredColorCount = ValidatePixelColorCount(
                colors.Length,
                width,
                height,
                nameof(colors));
            WritePixelColors(
                colors.AsSpan(0, requiredColorCount),
                (uint)left,
                (uint)top,
                (uint)width,
                (uint)height,
                fullTexture: false);
        }
    }

    public Rect GetBounds(ICanvasResourceCreator resourceCreator) =>
        GetBounds(resourceCreator, Matrix3x2.Identity);

    public Rect GetBounds(
        ICanvasResourceCreator resourceCreator,
        Matrix3x2 transform)
    {
        ThrowIfDisposed();
        CanvasContract.ValidateImageResourceCreator(
            resourceCreator,
            Device);
        return CanvasContract.TransformBounds(Bounds, transform);
    }

    public bool TryGetGpuTexture(out GpuTexture texture)
    {
        lock (_lifetimeLock)
        {
            if (!_isDisposed && !Texture.IsDisposed)
            {
                texture = Texture;
                return true;
            }
        }

        texture = null!;
        return false;
    }

    public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
    {
        lock (_lifetimeLock)
        {
            if (_isDisposed || Texture.IsDisposed)
            {
                lease = null!;
                return false;
            }

            _leaseCount = checked(_leaseCount + 1);
            lease = new CanvasBitmapTextureLease(this, Texture);
            return true;
        }
    }

    protected void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    protected virtual void DisposeCore()
    {
        Texture.Dispose();
    }

    protected virtual void ValidateCanDispose()
    {
    }

    protected virtual void ValidateCanMutate()
    {
        ThrowIfDisposed();
        if (_leaseCount != 0)
        {
            throw new InvalidOperationException(
                "Canvas bitmap pixels cannot be changed while deferred drawing owns a texture lease.");
        }
    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_isDisposed)
            {
                return;
            }

            ValidateCanDispose();
            _isDisposed = true;
            if (_leaseCount == 0)
            {
                DisposeTexture();
            }
        }
        GC.SuppressFinalize(this);
    }

    private void ReleaseTextureLease()
    {
        lock (_lifetimeLock)
        {
            if (_leaseCount <= 0)
            {
                throw new InvalidOperationException(
                    "Canvas bitmap texture lease accounting underflowed.");
            }

            _leaseCount--;
            if (_leaseCount == 0 && _isDisposed)
            {
                DisposeTexture();
            }
        }
    }

    private void DisposeTexture()
    {
        if (_textureDisposed)
        {
            return;
        }

        DisposeCore();
        _textureDisposed = true;
    }

    private sealed class CanvasBitmapTextureLease : IProGpuTextureLease
    {
        private CanvasBitmap? _owner;

        public CanvasBitmapTextureLease(
            CanvasBitmap owner,
            GpuTexture texture)
        {
            _owner = owner;
            Texture = texture;
        }

        public GpuTexture Texture { get; }

        public void Dispose()
        {
            CanvasBitmap? owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseTextureLease();
        }
    }

    private sealed class UploadedCanvasBitmap : CanvasBitmap
    {
        public UploadedCanvasBitmap(
            CanvasDevice device,
            GpuTexture texture,
            float width,
            float height,
            float dpi,
            DirectXPixelFormat format,
            CanvasAlphaMode alphaMode)
            : base(
                device,
                texture,
                width,
                height,
                dpi,
                format,
                alphaMode)
        {
        }
    }

    private void WritePixelColors(
        ReadOnlySpan<Color> colors,
        uint x,
        uint y,
        uint width,
        uint height,
        bool fullTexture)
    {
        int requiredByteCount = checked(colors.Length * 4);
        byte[] converted = ArrayPool<byte>.Shared.Rent(requiredByteCount);
        try
        {
            ProGpuCanvasCpuConversionPath path =
                CanvasColorBgraConverter.Convert(
                    colors,
                    converted.AsSpan(0, requiredByteCount),
                    Device.PixelConversionMode);
            Device.RecordPixelConversionPath(path);
            if (fullTexture)
            {
                Texture.WritePixels<byte>(
                    converted.AsSpan(0, requiredByteCount));
            }
            else
            {
                Texture.WritePixelsSubRect<byte>(
                    converted.AsSpan(0, requiredByteCount),
                    x,
                    y,
                    width,
                    height);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(converted);
        }
    }

    private static CanvasDevice GetResourceCreatorDevice(
        ICanvasResourceCreator resourceCreator)
    {
        ArgumentNullException.ThrowIfNull(resourceCreator);
        CanvasDevice device = resourceCreator.Device ??
            throw new ArgumentException(
                "The resource creator did not provide a CanvasDevice.",
                nameof(resourceCreator));
        if (device.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(resourceCreator));
        }

        return device;
    }

    private static void ValidatePixelDimensions(
        int widthInPixels,
        int heightInPixels)
    {
        if (widthInPixels <= 0 ||
            widthInPixels > CanvasContract.MaximumBitmapSizeInPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(widthInPixels));
        }
        if (heightInPixels <= 0 ||
            heightInPixels > CanvasContract.MaximumBitmapSizeInPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(heightInPixels));
        }
    }

    private static int ValidatePixelByteCount(
        int byteCount,
        int width,
        int height,
        string parameterName)
    {
        long required = checked((long)width * height * 4L);
        if (byteCount < required)
        {
            throw new ArgumentException(
                $"The BGRA8 pixel buffer requires at least {required} bytes.",
                parameterName);
        }

        return checked((int)required);
    }

    private static int ValidatePixelColorCount(
        int colorCount,
        int width,
        int height,
        string parameterName)
    {
        long required = checked((long)width * height);
        if (colorCount < required)
        {
            throw new ArgumentException(
                $"The pixel buffer requires at least {required} colors.",
                parameterName);
        }

        return checked((int)required);
    }

    private static void ValidatePixelSubrectangle(
        int left,
        int top,
        int width,
        int height,
        uint textureWidth,
        uint textureHeight)
    {
        if (left < 0 || top < 0 || width <= 0 || height <= 0 ||
            (uint)left > textureWidth ||
            (uint)top > textureHeight ||
            (uint)width > textureWidth - (uint)left ||
            (uint)height > textureHeight - (uint)top)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "The pixel subrectangle must fit inside the bitmap.");
        }
    }
}
