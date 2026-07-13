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

public class Bitmap : Image, IProGpuContextTextureLeaseSource
{
    private TextureLifetime? _textureLifetime;
    private readonly DrawingContext _recordedContext = new();
    private readonly object _textureLifetimeLock = new();
    private int _width;
    private int _height;
    private byte[]? _cpuPixels;
    private GpuTextureAlphaMode _cpuAlphaMode = GpuTextureAlphaMode.Premultiplied;
    private bool _isDisposed;
    private bool _hasDefinedPixels = true;

    public GpuTexture GpuTexture
    {
        get
        {
            lock (_textureLifetimeLock)
            {
                ThrowIfDisposed();
                FlushCore(requiredContext: null);
                GpuTexture texture = _textureLifetime is { Texture.IsDisposed: false } current
                    ? current.Texture
                    : EnsureTextureCore(GpuProvider.Context);
                // The public native escape hatch can mutate the texture without
                // notifying Bitmap, so any prior CPU snapshot is no longer
                // authoritative after it is handed out.
                _cpuPixels = null;
                _cpuAlphaMode = texture.AlphaMode;
                return texture;
            }
        }
    }

    public DrawingContext RecordedContext => _recordedContext;

    public override int Width => _width;
    public override int Height => _height;

    public Bitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _width = width;
        _height = height;
    }

    public Bitmap(int width, int height, Graphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _width = width;
        _height = height;
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
            // Record a native texture draw and let the ProGPU compositor scale
            // into this bitmap's render target. This keeps toolbox/icon resizing
            // on the GPU and, unlike the former top-left byte copy, handles row
            // pitch and arbitrary source/destination sizes correctly.
            using (Graphics graphics = Graphics.FromImage(this))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(
                    bitmap,
                    new Rectangle(0, 0, width, height),
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    GraphicsUnit.Pixel);
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
        InitializeFromStream(fs);
    }

    public Bitmap(System.IO.Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        InitializeFromStream(stream);
    }

    public Bitmap(Bitmap original)
    {
        ArgumentNullException.ThrowIfNull(original);
        _width = original.Width;
        _height = original.Height;
        _cpuPixels = original.CopyPixelsForClone(out _cpuAlphaMode);
        _hasDefinedPixels = true;
    }

    private void InitializeFromStream(System.IO.Stream stream)
    {
        using var skData = SkiaSharp.SKData.Create(stream);
        using var codec = SkiaSharp.SKCodec.Create(skData);

        if (codec is null)
        {
            throw new ArgumentException("The stream does not contain a supported bitmap image.", nameof(stream));
        }

        var decodeInfo = codec.Info;
        decodeInfo.ColorType = SkiaSharp.SKColorType.Rgba8888;
        decodeInfo.AlphaType = SkiaSharp.SKAlphaType.Unpremul;
        using var tempBitmap = SkiaSharp.SKBitmap.Decode(codec, decodeInfo);

        if (tempBitmap is null)
        {
            throw new ArgumentException("The stream does not contain a supported bitmap image.", nameof(stream));
        }

        _width = tempBitmap.Width;
        _height = tempBitmap.Height;
        _cpuAlphaMode = GpuTextureAlphaMode.Straight;

        unsafe
        {
            var pixelsSpan = new ReadOnlySpan<byte>((void*)tempBitmap.GetPixels(), tempBitmap.Width * tempBitmap.Height * 4);
            _cpuPixels = pixelsSpan.ToArray();
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
        lock (_textureLifetimeLock)
        {
            FlushCore(requiredContext: null);
        }
    }

    private void FlushCore(WgpuContext? requiredContext)
    {
        if (_isDisposed) return;
        if (_recordedContext.Commands.Count == 0) return;

        // Once commands have been recorded for an existing target, their image
        // resources belong to that target's context. Finish that generation
        // before migrating the bitmap to a newly requested host context.
        WgpuContext renderContext = _textureLifetime is { Texture.IsDisposed: false } current
            ? current.Texture.Context
            : requiredContext ?? GpuProvider.Context;
        GpuTexture texture = EnsureTextureCore(renderContext);
        NormalizeExistingContentsForPremultipliedRenderTarget(texture);

        var visual = new GraphicsVisual(_recordedContext);
        try
        {
            GpuProvider.GetCompositor(texture.Context).RenderOffscreen(
                visual,
                (uint)Width,
                (uint)Height,
                texture,
                padding: 0f,
                dpiScale: 1f,
                loadExistingContents: _hasDefinedPixels
            );

            texture.AlphaMode = GpuTextureAlphaMode.Premultiplied;
            _cpuAlphaMode = GpuTextureAlphaMode.Premultiplied;
            _cpuPixels = null;
            _hasDefinedPixels = true;
        }
        finally
        {
            _recordedContext.Clear();
        }
    }

    public bool TryGetGpuTexture(out GpuTexture texture)
    {
        WgpuContext context;
        try
        {
            context = GpuProvider.Context;
        }
        catch
        {
            texture = null!;
            return false;
        }

        return TryGetGpuTexture(context, out texture);
    }

    public bool TryGetGpuTexture(WgpuContext requiredContext, out GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);

        lock (_textureLifetimeLock)
        {
            if (_isDisposed || !requiredContext.IsInitialized)
            {
                texture = null!;
                return false;
            }

            FlushCore(requiredContext);
            texture = EnsureTextureCore(requiredContext);
            _cpuPixels = null;
            _cpuAlphaMode = texture.AlphaMode;
            return !texture.IsDisposed;
        }
    }

    public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
    {
        WgpuContext context;
        try
        {
            context = GpuProvider.Context;
        }
        catch
        {
            lease = null!;
            return false;
        }

        return TryAcquireGpuTextureLease(context, out lease);
    }

    public bool TryAcquireGpuTextureLease(
        WgpuContext requiredContext,
        out IProGpuTextureLease lease)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);

        lock (_textureLifetimeLock)
        {
            if (_isDisposed || !requiredContext.IsInitialized)
            {
                lease = null!;
                return false;
            }

            FlushCore(requiredContext);
            GpuTexture texture = EnsureTextureCore(requiredContext);
            if (_isDisposed || texture.IsDisposed)
            {
                lease = null!;
                return false;
            }

            TextureLifetime lifetime = _textureLifetime!;
            lifetime.ActiveLeaseCount++;
            _cpuPixels = null;
            _cpuAlphaMode = texture.AlphaMode;
            lease = new BitmapGpuTextureLease(this, lifetime);
            return true;
        }
    }

    private void ReleaseGpuTextureLease(TextureLifetime lifetime)
    {
        lock (_textureLifetimeLock)
        {
            if (lifetime.ActiveLeaseCount <= 0)
            {
                return;
            }

            lifetime.ActiveLeaseCount--;
            if (lifetime.ActiveLeaseCount == 0
                && lifetime.DisposeRequested
                && !lifetime.Texture.IsDisposed)
            {
                lifetime.Texture.Dispose();
            }
        }
    }

    private sealed class TextureLifetime
    {
        public TextureLifetime(GpuTexture texture)
        {
            Texture = texture;
        }

        public GpuTexture Texture { get; }

        public int ActiveLeaseCount { get; set; }

        public bool DisposeRequested { get; set; }
    }

    private sealed class BitmapGpuTextureLease : IProGpuTextureLease
    {
        private Bitmap? _owner;
        private readonly TextureLifetime _lifetime;

        public BitmapGpuTextureLease(Bitmap owner, TextureLifetime lifetime)
        {
            _owner = owner;
            _lifetime = lifetime;
        }

        public GpuTexture Texture => _lifetime.Texture;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseGpuTextureLease(_lifetime);
        }
    }

    internal WgpuContext GetDrawingContext()
    {
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            if (_textureLifetime is { Texture.IsDisposed: false } current)
            {
                return current.Texture.Context;
            }

            WgpuContext context = GpuProvider.Context;
            EnsureTextureCore(context);
            return context;
        }
    }

    private GpuTexture EnsureTextureCore(WgpuContext requiredContext)
    {
        if (!requiredContext.IsInitialized)
        {
            throw new InvalidOperationException(
                "Cannot materialize a GDI bitmap before the WebGPU context has a device and queue.");
        }

        if (_textureLifetime is { Texture.IsDisposed: false } current)
        {
            if (ReferenceEquals(current.Texture.Context, requiredContext))
            {
                return current.Texture;
            }

            SnapshotTexturePixelsCore(current.Texture);
            RetireTextureLifetime(current);
            _textureLifetime = null;
        }
        else if (_textureLifetime is not null)
        {
            _textureLifetime = null;
        }

        var texture = new GpuTexture(
            requiredContext,
            (uint)_width,
            (uint)_height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "GDI Bitmap Backing Texture",
            alphaMode: _cpuAlphaMode);

        // A newly constructed System.Drawing.Bitmap is transparent black. Keep
        // that behavior deterministic instead of depending on uninitialized
        // device memory, while allocating the CPU buffer only on materialization.
        byte[] pixels = GetOrCreateCpuPixelsCore();
        texture.WritePixels(pixels);
        texture.AlphaMode = _cpuAlphaMode;
        _textureLifetime = new TextureLifetime(texture);
        return texture;
    }

    private byte[] GetOrCreateCpuPixelsCore()
    {
        return _cpuPixels ??= new byte[checked(_width * _height * 4)];
    }

    private void SnapshotTexturePixelsCore(GpuTexture texture)
    {
        if (texture.IsDisposed)
        {
            return;
        }

        _cpuPixels = texture.ReadPixels();
        _cpuAlphaMode = texture.AlphaMode;
        _hasDefinedPixels = true;
    }

    private static void RetireTextureLifetime(TextureLifetime lifetime)
    {
        lifetime.DisposeRequested = true;
        if (lifetime.ActiveLeaseCount == 0 && !lifetime.Texture.IsDisposed)
        {
            lifetime.Texture.Dispose();
        }
    }

    private byte[] CopyPixelsForClone(out GpuTextureAlphaMode alphaMode)
    {
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            FlushCore(requiredContext: null);
            if (_textureLifetime is { Texture.IsDisposed: false } current)
            {
                alphaMode = current.Texture.AlphaMode;
                return current.Texture.ReadPixels();
            }

            alphaMode = _cpuAlphaMode;
            return (byte[])GetOrCreateCpuPixelsCore().Clone();
        }
    }

    private byte[] ReadPixelsCore(out GpuTextureAlphaMode alphaMode)
    {
        FlushCore(requiredContext: null);
        if (_textureLifetime is { Texture.IsDisposed: false } current)
        {
            alphaMode = current.Texture.AlphaMode;
            return current.Texture.ReadPixels();
        }

        alphaMode = _cpuAlphaMode;
        return GetOrCreateCpuPixelsCore();
    }

    private void WritePixelsCore(byte[] pixels, GpuTextureAlphaMode alphaMode)
    {
        if (_textureLifetime is { Texture.IsDisposed: false } current)
        {
            current.Texture.WritePixels(pixels);
            current.Texture.AlphaMode = alphaMode;
            _cpuPixels = null;
        }
        else
        {
            _cpuPixels = pixels;
        }

        _cpuAlphaMode = alphaMode;
        _hasDefinedPixels = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    public Color GetPixel(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x));

        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            byte[] pixels = ReadPixelsCore(out var alphaMode);
            int offset = (y * Width + x) * 4;
            byte alpha = pixels[offset + 3];
            byte red = pixels[offset];
            byte green = pixels[offset + 1];
            byte blue = pixels[offset + 2];

            if (alphaMode == GpuTextureAlphaMode.Premultiplied)
            {
                red = UnpremultiplyChannel(red, alpha);
                green = UnpremultiplyChannel(green, alpha);
                blue = UnpremultiplyChannel(blue, alpha);
            }

            return Color.FromArgb(alpha, red, green, blue);
        }
    }

    public void SetPixel(int x, int y, Color color)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x));

        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            FlushCore(requiredContext: null);
            GpuTextureAlphaMode alphaMode = _textureLifetime is { Texture.IsDisposed: false } current
                ? current.Texture.AlphaMode
                : _cpuAlphaMode;
            byte red = color.R;
            byte green = color.G;
            byte blue = color.B;
            if (alphaMode == GpuTextureAlphaMode.Premultiplied)
            {
                red = PremultiplyChannel(red, color.A);
                green = PremultiplyChannel(green, color.A);
                blue = PremultiplyChannel(blue, color.A);
            }

            if (_textureLifetime is { Texture.IsDisposed: false } textureLifetime)
            {
                byte[] rgba = new byte[] { red, green, blue, color.A };
                textureLifetime.Texture.WritePixelsSubRect(rgba.AsSpan(), (uint)x, (uint)y, 1, 1);
                _cpuPixels = null;
            }
            else
            {
                byte[] pixels = GetOrCreateCpuPixelsCore();
                int offset = (y * Width + x) * 4;
                pixels[offset] = red;
                pixels[offset + 1] = green;
                pixels[offset + 2] = blue;
                pixels[offset + 3] = color.A;
            }

            _cpuAlphaMode = alphaMode;
            _hasDefinedPixels = true;
        }
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
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            byte[] pixels = ReadPixelsCore(out var alphaMode);
            int keyOffset = ((Height - 1) * Width) * 4;
            byte keyAlpha = pixels[keyOffset + 3];
            if (keyAlpha < byte.MaxValue)
            {
                return;
            }

            byte keyRed = pixels[keyOffset];
            byte keyGreen = pixels[keyOffset + 1];
            byte keyBlue = pixels[keyOffset + 2];
            if (alphaMode == GpuTextureAlphaMode.Premultiplied)
            {
                keyRed = UnpremultiplyChannel(keyRed, keyAlpha);
                keyGreen = UnpremultiplyChannel(keyGreen, keyAlpha);
                keyBlue = UnpremultiplyChannel(keyBlue, keyAlpha);
            }

            ApplyTransparentColorKey(pixels, keyRed, keyGreen, keyBlue, alphaMode);
        }
    }

    public void MakeTransparent(Color transparentColor)
    {
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            byte[] pixels = ReadPixelsCore(out var alphaMode);
            ApplyTransparentColorKey(
                pixels,
                transparentColor.R,
                transparentColor.G,
                transparentColor.B,
                alphaMode);
        }
    }

    private void ApplyTransparentColorKey(
        byte[] pixels,
        byte keyRed,
        byte keyGreen,
        byte keyBlue,
        GpuTextureAlphaMode alphaMode)
    {
        bool changed = false;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte red = pixels[offset];
            byte green = pixels[offset + 1];
            byte blue = pixels[offset + 2];
            byte alpha = pixels[offset + 3];

            if (alphaMode == GpuTextureAlphaMode.Premultiplied)
            {
                red = UnpremultiplyChannel(red, alpha);
                green = UnpremultiplyChannel(green, alpha);
                blue = UnpremultiplyChannel(blue, alpha);
            }

            // GDI+ color keys compare RGB channels. The key alpha and the source
            // pixel alpha do not participate in the match.
            if (red != keyRed
                || green != keyGreen
                || blue != keyBlue)
            {
                continue;
            }

            pixels[offset] = 0;
            pixels[offset + 1] = 0;
            pixels[offset + 2] = 0;
            pixels[offset + 3] = 0;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        WritePixelsCore(pixels, alphaMode);
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
        byte[] pixels;
        GpuTextureAlphaMode alphaMode;
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            pixels = ReadPixelsCore(out alphaMode);
        }

        if (alphaMode == GpuTextureAlphaMode.Premultiplied)
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

    private void NormalizeExistingContentsForPremultipliedRenderTarget(GpuTexture texture)
    {
        if (!_hasDefinedPixels || texture.AlphaMode != GpuTextureAlphaMode.Straight)
        {
            return;
        }

        var pixels = texture.ReadPixels();
        PremultiplyPixelsInPlace(pixels);
        texture.WritePixels(pixels);
        texture.AlphaMode = GpuTextureAlphaMode.Premultiplied;
        _cpuPixels = null;
        _cpuAlphaMode = GpuTextureAlphaMode.Premultiplied;
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
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            ValidateLockBitsRectangle(rect);
            if (_lockedBytes != null || _lockedHandle.IsAllocated)
            {
                throw new InvalidOperationException("Bitmap already has an active lock. Call UnlockBits before LockBits again.");
            }

            byte[] fullPixels = ReadPixelsCore(out _lockedTextureAlphaMode);
            int subWidth = rect.Width;
            int subHeight = rect.Height;
            int stride = GetLockStride(subWidth, format);
            _lockedBytes = new byte[stride * subHeight];
            _lockedRect = rect;
            _lockedPixelFormat = format;
            _lockedStride = stride;
            _lockedWriteBack = flags != ImageLockMode.ReadOnly;

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
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
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
                    if (_textureLifetime is { Texture.IsDisposed: false } current)
                    {
                        current.Texture.WritePixelsSubRect(
                            rgba,
                            (uint)_lockedRect.X,
                            (uint)_lockedRect.Y,
                            (uint)_lockedRect.Width,
                            (uint)_lockedRect.Height);
                        current.Texture.AlphaMode = _lockedTextureAlphaMode;
                        _cpuPixels = null;
                    }
                    else
                    {
                        byte[] pixels = GetOrCreateCpuPixelsCore();
                        for (int y = 0; y < _lockedRect.Height; y++)
                        {
                            int sourceOffset = y * _lockedRect.Width * 4;
                            int destinationOffset = ((_lockedRect.Y + y) * Width + _lockedRect.X) * 4;
                            rgba.AsSpan(sourceOffset, _lockedRect.Width * 4)
                                .CopyTo(pixels.AsSpan(destinationOffset));
                        }
                    }

                    _cpuAlphaMode = _lockedTextureAlphaMode;
                    _hasDefinedPixels = true;
                }

                _lockedBytes = null;
                _lockedStride = 0;
                _lockedPixelFormat = default;
                _lockedWriteBack = false;
                _lockedTextureAlphaMode = default;
            }
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
        lock (_textureLifetimeLock)
        {
            if (_isDisposed) return;
            try
            {
                if (disposing && _recordedContext.Commands.Count != 0)
                {
                    WgpuContext? disposeContext =
                        _textureLifetime is { Texture.IsDisposed: false } current
                            && current.Texture.Context.IsInitialized
                            ? current.Texture.Context
                            : WgpuContext.Current is { IsInitialized: true } ambient
                                ? ambient
                                : null;

                    // Disposing an image discards its contents. Preserve the
                    // established flush-on-dispose behavior while a usable
                    // rendering context still exists, but never create a new
                    // device merely to throw the bitmap away during host
                    // shutdown.
                    if (disposeContext is not null)
                    {
                        FlushCore(disposeContext);
                    }
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
                _recordedContext.Clear();
                _isDisposed = true;

                if (disposing && _textureLifetime is not null)
                {
                    RetireTextureLifetime(_textureLifetime);
                    _textureLifetime = null;
                }

                _cpuPixels = null;
            }
        }
    }

    ~Bitmap()
    {
        Dispose(disposing: false);
    }
}
