using ProGPU.Backend;
using ProGPU.Scene;
using System;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;

namespace System.Drawing;

internal class GraphicsVisual : Visual
{
    private readonly DrawingContext _recordedContext;

    public GraphicsVisual(DrawingContext recordedContext)
    {
        _recordedContext = recordedContext;
    }

    public override void OnRender(DrawingContext context)
    {
        context.Append(_recordedContext);
    }
}

public class Bitmap : Image
{
    private GpuTexture _texture = null!;
    private readonly DrawingContext _recordedContext = new();
    private bool _isDisposed;
    private bool _hasDefinedPixels;

    public GpuTexture GpuTexture => _texture;
    public DrawingContext RecordedContext => _recordedContext;

    public override int Width => (int)_texture.Width;
    public override int Height => (int)_texture.Height;

    public Bitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _texture = new GpuTexture(
            GpuProvider.Context,
            (uint)width,
            (uint)height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "GDI Bitmap Backing Texture",
            alphaMode: GpuTextureAlphaMode.Premultiplied
        );
    }

    public Bitmap(int width, int height, PixelFormat format)
        : this(width, height)
    {
    }

    public Bitmap(Image original, int width, int height)
        : this(width, height)
    {
        ArgumentNullException.ThrowIfNull(original);

        if (original is Bitmap bitmap)
        {
            bitmap.Flush();
            int copyWidth = Math.Min(width, bitmap.Width);
            int copyHeight = Math.Min(height, bitmap.Height);
            if (copyWidth > 0 && copyHeight > 0)
            {
                byte[] pixels = bitmap._texture.ReadPixels();
                _texture.WritePixelsSubRect(pixels.AsSpan(), 0, 0, (uint)copyWidth, (uint)copyHeight);
                _hasDefinedPixels = true;
            }
        }
    }

    public Bitmap(Image original)
        : this(original, GetImageWidth(original), GetImageHeight(original))
    {
    }

    public Bitmap(string filename)
    {
        using var fs = System.IO.File.OpenRead(filename);
        InitializeFromStream(fs, "GDI Bitmap Backing Texture from file");
    }

    public Bitmap(System.IO.Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        InitializeFromStream(stream, "GDI Bitmap Backing Texture from stream");
    }

    public Bitmap(Bitmap original)
    {
        ArgumentNullException.ThrowIfNull(original);
        original.Flush();

        _texture = new GpuTexture(
            GpuProvider.Context,
            (uint)original.Width,
            (uint)original.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "GDI Bitmap Backing Texture copy",
            alphaMode: original._texture.AlphaMode
        );

        _texture.WritePixels(original._texture.ReadPixels());
        _hasDefinedPixels = true;
    }

    private void InitializeFromStream(System.IO.Stream stream, string label)
    {
        using var skData = SkiaSharp.SKData.Create(stream);
        using var tempBitmap = SkiaSharp.SKBitmap.Decode(skData);

        _texture = new GpuTexture(
            GpuProvider.Context,
            (uint)tempBitmap.Width,
            (uint)tempBitmap.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            label,
            alphaMode: GpuTextureAlphaMode.Straight
        );

        unsafe
        {
            var pixelsSpan = new ReadOnlySpan<byte>((void*)tempBitmap.GetPixels(), tempBitmap.Width * tempBitmap.Height * 4);
            _texture.WritePixels(pixelsSpan);
        }

        _hasDefinedPixels = true;
    }

    private static int GetImageWidth(Image original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return original.Width;
    }

    private static int GetImageHeight(Image original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return original.Height;
    }

    public void Flush()
    {
        if (_isDisposed) return;
        if (_recordedContext.Commands.Count == 0) return;

        NormalizeExistingContentsForPremultipliedRenderTarget();

        var visual = new GraphicsVisual(_recordedContext);
        try
        {
            GpuProvider.GetCompositor(_texture.Context).RenderOffscreen(
                visual,
                (uint)Width,
                (uint)Height,
                _texture,
                padding: 0f,
                dpiScale: 1f,
                loadExistingContents: _hasDefinedPixels
            );

            _texture.AlphaMode = GpuTextureAlphaMode.Premultiplied;
            _hasDefinedPixels = true;
        }
        finally
        {
            _recordedContext.Clear();
        }
    }

    public Color GetPixel(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x));

        Flush();
        byte[] pixels = _texture.ReadPixels();
        int offset = (y * Width + x) * 4;
        byte alpha = pixels[offset + 3];
        byte red = pixels[offset];
        byte green = pixels[offset + 1];
        byte blue = pixels[offset + 2];

        if (_texture.AlphaMode == GpuTextureAlphaMode.Premultiplied)
        {
            red = UnpremultiplyChannel(red, alpha);
            green = UnpremultiplyChannel(green, alpha);
            blue = UnpremultiplyChannel(blue, alpha);
        }

        return Color.FromArgb(alpha, red, green, blue);
    }

    public void SetPixel(int x, int y, Color color)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x));

        Flush();
        byte red = color.R;
        byte green = color.G;
        byte blue = color.B;
        if (_texture.AlphaMode == GpuTextureAlphaMode.Premultiplied)
        {
            red = PremultiplyChannel(red, color.A);
            green = PremultiplyChannel(green, color.A);
            blue = PremultiplyChannel(blue, color.A);
        }

        byte[] rgba = new byte[] { red, green, blue, color.A };
        _texture.WritePixelsSubRect(rgba.AsSpan(), (uint)x, (uint)y, 1, 1);
        _hasDefinedPixels = true;
    }

    public void Save(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        using var stream = System.IO.File.Create(filename);
        SavePng(stream);
    }

    public IntPtr GetHicon()
    {
        return Icon.RegisterBitmapHandle(this);
    }

    public void MakeTransparent()
    {
    }

    public void MakeTransparent(Color transparentColor)
    {
    }

    public void Save(System.IO.Stream stream, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);

        if (format.Guid != ImageFormat.Png.Guid)
        {
            throw new NotSupportedException($"Image format '{format.Guid}' is not supported by the ProGPU System.Drawing bitmap shim.");
        }

        SavePng(stream);
    }

    private void SavePng(System.IO.Stream stream)
    {
        Flush();
        byte[] pixels = _texture.ReadPixels();
        if (_texture.AlphaMode == GpuTextureAlphaMode.Premultiplied)
        {
            pixels = UnpremultiplyPixels(pixels);
        }

        PngEncoder.SavePng(stream, pixels, (uint)Width, (uint)Height);
    }

    private static byte[] UnpremultiplyPixels(byte[] pixels)
    {
        var straightPixels = new byte[pixels.Length];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            if (alpha == 0)
            {
                continue;
            }

            straightPixels[offset] = UnpremultiplyChannel(pixels[offset], alpha);
            straightPixels[offset + 1] = UnpremultiplyChannel(pixels[offset + 1], alpha);
            straightPixels[offset + 2] = UnpremultiplyChannel(pixels[offset + 2], alpha);
            straightPixels[offset + 3] = alpha;
        }

        return straightPixels;
    }

    private void NormalizeExistingContentsForPremultipliedRenderTarget()
    {
        if (!_hasDefinedPixels || _texture.AlphaMode != GpuTextureAlphaMode.Straight)
        {
            return;
        }

        var pixels = _texture.ReadPixels();
        PremultiplyPixelsInPlace(pixels);
        _texture.WritePixels(pixels);
        _texture.AlphaMode = GpuTextureAlphaMode.Premultiplied;
    }

    private static void PremultiplyPixelsInPlace(byte[] pixels)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            pixels[offset] = PremultiplyChannel(pixels[offset], alpha);
            pixels[offset + 1] = PremultiplyChannel(pixels[offset + 1], alpha);
            pixels[offset + 2] = PremultiplyChannel(pixels[offset + 2], alpha);
        }
    }

    private static byte UnpremultiplyChannel(byte channel, byte alpha)
    {
        if (alpha == 0)
        {
            return 0;
        }

        return (byte)Math.Min(255, (channel * 255 + alpha / 2) / alpha);
    }

    private static byte PremultiplyChannel(byte channel, byte alpha)
    {
        return (byte)((channel * alpha + 127) / 255);
    }

    private byte[]? _lockedBytes;
    private GCHandle _lockedHandle;
    private Rectangle _lockedRect;
    private PixelFormat _lockedPixelFormat;
    private int _lockedStride;
    private bool _lockedWriteBack;
    private GpuTextureAlphaMode _lockedTextureAlphaMode;

    public BitmapData LockBits(Rectangle rect, ImageLockMode flags, PixelFormat format)
    {
        ValidateLockBitsRectangle(rect);
        if (_lockedBytes != null || _lockedHandle.IsAllocated)
        {
            throw new InvalidOperationException("Bitmap already has an active lock. Call UnlockBits before LockBits again.");
        }

        Flush();
        byte[] fullPixels = _texture.ReadPixels();
        
        int subWidth = rect.Width;
        int subHeight = rect.Height;
        int stride = GetLockStride(subWidth, format);
        _lockedBytes = new byte[stride * subHeight];
        _lockedRect = rect;
        _lockedPixelFormat = format;
        _lockedStride = stride;
        _lockedWriteBack = flags != ImageLockMode.ReadOnly;
        _lockedTextureAlphaMode = _texture.AlphaMode;

        CopyRgbaToLockBuffer(fullPixels, _lockedBytes, rect, format, stride, _lockedTextureAlphaMode);
        
        _lockedHandle = GCHandle.Alloc(_lockedBytes, GCHandleType.Pinned);

        return new BitmapData
        {
            Width = subWidth,
            Height = subHeight,
            Stride = stride,
            PixelFormat = format,
            Scan0 = _lockedHandle.AddrOfPinnedObject()
        };
    }

    private static int GetLockStride(int width, PixelFormat format)
    {
        var bytesPerRow = format switch
        {
            PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or PixelFormat.Format32bppRgb => checked(width * 4),
            PixelFormat.Format24bppRgb => checked(width * 3),
            PixelFormat.Format16bppRgb565 => checked(width * 2),
            _ => throw new NotSupportedException($"LockBits pixel format '{format}' is not supported.")
        };

        return (bytesPerRow + 3) & ~3;
    }

    private void CopyRgbaToLockBuffer(byte[] source, byte[] destination, Rectangle rect, PixelFormat format, int stride, GpuTextureAlphaMode sourceAlphaMode)
    {
        for (int y = 0; y < rect.Height; y++)
        {
            var srcOffset = ((rect.Y + y) * Width + rect.X) * 4;
            var dstOffset = y * stride;
            for (int x = 0; x < rect.Width; x++)
            {
                var src = srcOffset + x * 4;
                var r = source[src];
                var g = source[src + 1];
                var b = source[src + 2];
                var a = source[src + 3];
                ConvertPixelFromTextureAlphaMode(ref r, ref g, ref b, a, sourceAlphaMode, format);
                WriteLockPixel(destination, dstOffset, x, format, r, g, b, a);
            }
        }
    }

    private static void ConvertPixelFromTextureAlphaMode(ref byte r, ref byte g, ref byte b, byte a, GpuTextureAlphaMode sourceAlphaMode, PixelFormat lockPixelFormat)
    {
        if (sourceAlphaMode == GpuTextureAlphaMode.Premultiplied && lockPixelFormat != PixelFormat.Format32bppPArgb)
        {
            r = UnpremultiplyChannel(r, a);
            g = UnpremultiplyChannel(g, a);
            b = UnpremultiplyChannel(b, a);
        }
        else if (sourceAlphaMode == GpuTextureAlphaMode.Straight && lockPixelFormat == PixelFormat.Format32bppPArgb)
        {
            r = PremultiplyChannel(r, a);
            g = PremultiplyChannel(g, a);
            b = PremultiplyChannel(b, a);
        }
    }

    private static void WriteLockPixel(byte[] destination, int rowOffset, int x, PixelFormat format, byte r, byte g, byte b, byte a)
    {
        switch (format)
        {
            case PixelFormat.Format32bppArgb:
            case PixelFormat.Format32bppPArgb:
                var offset32 = rowOffset + x * 4;
                destination[offset32] = b;
                destination[offset32 + 1] = g;
                destination[offset32 + 2] = r;
                destination[offset32 + 3] = a;
                break;
            case PixelFormat.Format32bppRgb:
                offset32 = rowOffset + x * 4;
                destination[offset32] = b;
                destination[offset32 + 1] = g;
                destination[offset32 + 2] = r;
                destination[offset32 + 3] = 255;
                break;
            case PixelFormat.Format24bppRgb:
                var offset24 = rowOffset + x * 3;
                destination[offset24] = b;
                destination[offset24 + 1] = g;
                destination[offset24 + 2] = r;
                break;
            case PixelFormat.Format16bppRgb565:
                var offset16 = rowOffset + x * 2;
                ushort rgb565 = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                destination[offset16] = (byte)(rgb565 & 0xFF);
                destination[offset16 + 1] = (byte)(rgb565 >> 8);
                break;
        }
    }

    private void ValidateLockBitsRectangle(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ArgumentException("LockBits rectangle must have a positive width and height.", nameof(rect));
        }

        if (rect.X < 0
            || rect.Y < 0
            || rect.X > Width - rect.Width
            || rect.Y > Height - rect.Height)
        {
            throw new ArgumentException("LockBits rectangle must be contained within the bitmap bounds.", nameof(rect));
        }
    }

    public void UnlockBits(BitmapData bitmapData)
    {
        if (_lockedBytes != null)
        {
            if (_lockedHandle.IsAllocated)
            {
                _lockedHandle.Free();
            }

            if (_lockedWriteBack)
            {
                var rgba = ConvertLockBufferToRgba(_lockedBytes, _lockedRect, _lockedPixelFormat, _lockedStride);
                ConvertPixelsToTextureAlphaMode(rgba, _lockedPixelFormat, _lockedTextureAlphaMode);
                _texture.WritePixelsSubRect(rgba, (uint)_lockedRect.X, (uint)_lockedRect.Y, (uint)_lockedRect.Width, (uint)_lockedRect.Height);
                _texture.AlphaMode = _lockedTextureAlphaMode;
                _hasDefinedPixels = true;
            }

            _lockedBytes = null;
            _lockedStride = 0;
            _lockedPixelFormat = default;
            _lockedWriteBack = false;
            _lockedTextureAlphaMode = default;
        }
    }

    private static void ConvertPixelsToTextureAlphaMode(byte[] rgba, PixelFormat lockPixelFormat, GpuTextureAlphaMode targetAlphaMode)
    {
        var lockPixelsArePremultiplied = lockPixelFormat == PixelFormat.Format32bppPArgb;

        for (int offset = 0; offset < rgba.Length; offset += 4)
        {
            var alpha = rgba[offset + 3];
            if (targetAlphaMode == GpuTextureAlphaMode.Premultiplied && !lockPixelsArePremultiplied)
            {
                rgba[offset] = PremultiplyChannel(rgba[offset], alpha);
                rgba[offset + 1] = PremultiplyChannel(rgba[offset + 1], alpha);
                rgba[offset + 2] = PremultiplyChannel(rgba[offset + 2], alpha);
            }
            else if (targetAlphaMode == GpuTextureAlphaMode.Straight && lockPixelsArePremultiplied)
            {
                rgba[offset] = UnpremultiplyChannel(rgba[offset], alpha);
                rgba[offset + 1] = UnpremultiplyChannel(rgba[offset + 1], alpha);
                rgba[offset + 2] = UnpremultiplyChannel(rgba[offset + 2], alpha);
            }
        }
    }

    private static byte[] ConvertLockBufferToRgba(byte[] source, Rectangle rect, PixelFormat format, int stride)
    {
        var rgba = new byte[rect.Width * rect.Height * 4];
        for (int y = 0; y < rect.Height; y++)
        {
            var srcRow = y * stride;
            var dstRow = y * rect.Width * 4;
            for (int x = 0; x < rect.Width; x++)
            {
                ReadLockPixel(source, srcRow, x, format, out var r, out var g, out var b, out var a);
                var dst = dstRow + x * 4;
                rgba[dst] = r;
                rgba[dst + 1] = g;
                rgba[dst + 2] = b;
                rgba[dst + 3] = a;
            }
        }

        return rgba;
    }

    private static void ReadLockPixel(byte[] source, int rowOffset, int x, PixelFormat format, out byte r, out byte g, out byte b, out byte a)
    {
        switch (format)
        {
            case PixelFormat.Format32bppArgb:
            case PixelFormat.Format32bppPArgb:
                var offset32 = rowOffset + x * 4;
                b = source[offset32];
                g = source[offset32 + 1];
                r = source[offset32 + 2];
                a = source[offset32 + 3];
                break;
            case PixelFormat.Format32bppRgb:
                offset32 = rowOffset + x * 4;
                b = source[offset32];
                g = source[offset32 + 1];
                r = source[offset32 + 2];
                a = 255;
                break;
            case PixelFormat.Format24bppRgb:
                var offset24 = rowOffset + x * 3;
                b = source[offset24];
                g = source[offset24 + 1];
                r = source[offset24 + 2];
                a = 255;
                break;
            case PixelFormat.Format16bppRgb565:
                var offset16 = rowOffset + x * 2;
                ushort rgb565 = (ushort)(source[offset16] | (source[offset16 + 1] << 8));
                r = (byte)(((rgb565 >> 11) & 0x1F) * 255 / 31);
                g = (byte)(((rgb565 >> 5) & 0x3F) * 255 / 63);
                b = (byte)((rgb565 & 0x1F) * 255 / 31);
                a = 255;
                break;
            default:
                r = g = b = a = 0;
                break;
        }
    }

    public override void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        try
        {
            if (disposing)
            {
                Flush();
            }
        }
        finally
        {
            if (_lockedHandle.IsAllocated)
            {
                _lockedHandle.Free();
            }

            _lockedBytes = null;
            _lockedStride = 0;
            _lockedPixelFormat = default;
            _lockedWriteBack = false;
            if (disposing)
            {
                _texture.Dispose();
            }

            _isDisposed = true;
        }
    }

    ~Bitmap()
    {
        Dispose(disposing: false);
    }
}
