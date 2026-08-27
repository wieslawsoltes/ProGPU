using ProGPU.Backend;
using ProGPU.Scene;
using System;
using System.Drawing.Imaging;
using System.Drawing.Imaging.Effects;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
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

[Serializable]
public class Bitmap : Image, IProGpuContextTextureLeaseSource
{
    private TextureLifetime? _textureLifetime;
    private readonly DrawingContext _recordedContext = new();
    private readonly object _textureLifetimeLock = new();
    private int _width;
    private int _height;
    private PixelFormat _pixelFormat = PixelFormat.Format32bppArgb;
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
    public override PixelFormat PixelFormat => _pixelFormat;

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
        ValidateConcretePixelFormat(format);
        _pixelFormat = format;
        InitializeDefaultPalette(format);
    }

    public Bitmap(int width, int height, int stride, PixelFormat format, IntPtr scan0)
        : this(width, height, format)
    {
        if (scan0 == IntPtr.Zero)
        {
            throw new ArgumentException("The pixel buffer pointer cannot be zero.", nameof(scan0));
        }

        int minimumStride = GetLockStride(width, format);
        if (stride == 0 || Math.Abs((long)stride) < minimumStride)
        {
            throw new ArgumentException("The stride is smaller than the requested pixel row.", nameof(stride));
        }

        _cpuPixels = CopyExternalPixelsToRgba(scan0, stride, width, height, format, Palette);
        _cpuAlphaMode = IsPremultiplied(format)
            ? GpuTextureAlphaMode.Premultiplied
            : GpuTextureAlphaMode.Straight;
        _hasDefinedPixels = true;
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

    public Bitmap(Image original, Size newSize)
        : this(original, newSize.Width, newSize.Height)
    {
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

    public Bitmap(string filename, bool useIcm)
        : this(filename)
    {
    }

    public Bitmap(Type type, string resource)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(resource);

        using System.IO.Stream? stream = type.Assembly.GetManifestResourceStream(type, resource);
        if (stream is null)
        {
            throw new ArgumentException(
                "The requested bitmap resource was not found in the type's assembly.",
                nameof(resource));
        }

        InitializeFromStream(stream);
    }

    public Bitmap(System.IO.Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        InitializeFromStream(stream);
    }

    public Bitmap(System.IO.Stream stream, bool useIcm)
        : this(stream)
    {
    }

#pragma warning disable SYSLIB0050
    private Bitmap(SerializationInfo info, StreamingContext context)
    {
        ArgumentNullException.ThrowIfNull(info);
        byte[] data = (byte[])info.GetValue("Data", typeof(byte[]))!;
        using var stream = new MemoryStream(data, writable: false);
        InitializeFromStream(stream);
    }
#pragma warning restore SYSLIB0050

    public Bitmap(Bitmap original)
    {
        ArgumentNullException.ThrowIfNull(original);
        _width = original.Width;
        _height = original.Height;
        _pixelFormat = original.PixelFormat;
        _cpuPixels = original.CopyPixelsForClone(out _cpuAlphaMode);
        _hasDefinedPixels = true;
        original.CopyMetadataTo(this);
    }

    public override object Clone() => new Bitmap(this);

    public Bitmap Clone(Rectangle rect, PixelFormat format) =>
        Clone(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), format);

    public Bitmap Clone(RectangleF rect, PixelFormat format)
    {
        ValidateConcretePixelFormat(format);
        if (!(rect.Width > 0f) || !(rect.Height > 0f) || !float.IsFinite(rect.X) || !float.IsFinite(rect.Y))
        {
            throw new ArgumentException("The clone rectangle must be finite and non-empty.", nameof(rect));
        }

        int left = checked((int)MathF.Floor(rect.Left));
        int top = checked((int)MathF.Floor(rect.Top));
        int right = checked((int)MathF.Ceiling(rect.Right));
        int bottom = checked((int)MathF.Ceiling(rect.Bottom));
        if (left < 0 || top < 0 || right > Width || bottom > Height || right <= left || bottom <= top)
        {
            throw new ArgumentException("The clone rectangle must be contained within the bitmap bounds.", nameof(rect));
        }

        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            byte[] source = ReadPixelsCore(out GpuTextureAlphaMode sourceAlphaMode);
            int cloneWidth = right - left;
            int cloneHeight = bottom - top;
            var pixels = new byte[checked(cloneWidth * cloneHeight * 4)];
            for (int y = 0; y < cloneHeight; y++)
            {
                source.AsSpan(((top + y) * Width + left) * 4, cloneWidth * 4)
                    .CopyTo(pixels.AsSpan(y * cloneWidth * 4));
            }

            var clone = CreateFromPixels(cloneWidth, cloneHeight, pixels, sourceAlphaMode, format);
            CopyMetadataTo(clone);
            if (PixelFormatInfo.IsIndexed(format) && !PixelFormatInfo.IsIndexed(_pixelFormat))
            {
                clone.InitializeDefaultPalette(format);
            }
            BitmapData lockData = clone.LockBits(
                new Rectangle(0, 0, cloneWidth, cloneHeight),
                ImageLockMode.ReadWrite,
                format);
            clone.UnlockBits(lockData);
            return clone;
        }
    }

    public override void RotateFlip(RotateFlipType rotateFlipType)
    {
        int operation = (int)rotateFlipType;
        if ((uint)operation > 7u)
        {
            throw new ArgumentException("Invalid rotate/flip operation.", nameof(rotateFlipType));
        }

        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            byte[] source = ReadPixelsCore(out GpuTextureAlphaMode alphaMode);
            int sourceWidth = _width;
            int sourceHeight = _height;
            int rotation = operation & 3;
            bool flipX = (operation & 4) != 0;
            int destinationWidth = (rotation & 1) == 0 ? sourceWidth : sourceHeight;
            int destinationHeight = (rotation & 1) == 0 ? sourceHeight : sourceWidth;
            byte[] destination = new byte[checked(source.Length)];

            for (int sourceY = 0; sourceY < sourceHeight; sourceY++)
            {
                for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
                {
                    int destinationX;
                    int destinationY;
                    switch (rotation)
                    {
                        case 1:
                            destinationX = sourceHeight - 1 - sourceY;
                            destinationY = sourceX;
                            break;
                        case 2:
                            destinationX = sourceWidth - 1 - sourceX;
                            destinationY = sourceHeight - 1 - sourceY;
                            break;
                        case 3:
                            destinationX = sourceY;
                            destinationY = sourceWidth - 1 - sourceX;
                            break;
                        default:
                            destinationX = sourceX;
                            destinationY = sourceY;
                            break;
                    }

                    if (flipX)
                    {
                        destinationX = destinationWidth - 1 - destinationX;
                    }

                    int sourceOffset = ((sourceY * sourceWidth) + sourceX) * 4;
                    int destinationOffset = ((destinationY * destinationWidth) + destinationX) * 4;
                    source.AsSpan(sourceOffset, 4).CopyTo(destination.AsSpan(destinationOffset, 4));
                }
            }

            if (_textureLifetime is { } lifetime)
            {
                RetireTextureLifetime(lifetime);
                _textureLifetime = null;
            }

            _width = destinationWidth;
            _height = destinationHeight;
            _cpuPixels = destination;
            _cpuAlphaMode = alphaMode;
            _hasDefinedPixels = true;
        }
    }

    /// <summary>Alters the bitmap by applying the given <paramref name="effect"/>.</summary>
    /// <param name="effect">The effect to apply.</param>
    /// <param name="area">The area to apply to, or <see cref="Rectangle.Empty"/> for the entire image.</param>
    public void ApplyEffect(Effect effect, Rectangle area = default)
    {
        ArgumentNullException.ThrowIfNull(effect);

        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            Rectangle clippedArea = area.IsEmpty
                ? new Rectangle(0, 0, Width, Height)
                : ClipToBitmap(area, Width, Height);
            if (clippedArea.IsEmpty)
            {
                return;
            }

            byte[] pixels = ReadPixelsCore(out GpuTextureAlphaMode alphaMode);
            effect.Apply(
                pixels,
                Width,
                clippedArea,
                premultiplied: alphaMode == GpuTextureAlphaMode.Premultiplied);
            WritePixelsCore(pixels, alphaMode);
        }
    }

    private static Rectangle ClipToBitmap(Rectangle area, int width, int height)
    {
        long left = Math.Max(0L, area.X);
        long top = Math.Max(0L, area.Y);
        long right = Math.Min(width, (long)area.X + area.Width);
        long bottom = Math.Min(height, (long)area.Y + area.Height);
        return right <= left || bottom <= top
            ? Rectangle.Empty
            : Rectangle.FromLTRB((int)left, (int)top, (int)right, (int)bottom);
    }

    public IntPtr GetHbitmap() => GetHbitmap(Color.Transparent);

    public IntPtr GetHbitmap(Color background) =>
        throw new PlatformNotSupportedException(
            "HBITMAP export requires the explicit Windows GDI image adapter.");

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

    internal Bitmap CreateColorRemapped(ReadOnlySpan<(Color OldColor, Color NewColor)> remapTable)
    {
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            byte[] pixels = (byte[])ReadPixelsCore(out GpuTextureAlphaMode alphaMode).Clone();
            if (remapTable.IsEmpty)
            {
                return CreateFromPixels(_width, _height, pixels, alphaMode);
            }

            var replacements = new Dictionary<int, int>(remapTable.Length);
            foreach ((Color oldColor, Color newColor) in remapTable)
            {
                replacements[oldColor.ToArgb()] = newColor.ToArgb();
            }

            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
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

                int sourceArgb = (alpha << 24) | (red << 16) | (green << 8) | blue;
                if (!replacements.TryGetValue(sourceArgb, out int replacementArgb))
                {
                    continue;
                }

                byte replacementAlpha = (byte)(replacementArgb >> 24);
                byte replacementRed = (byte)(replacementArgb >> 16);
                byte replacementGreen = (byte)(replacementArgb >> 8);
                byte replacementBlue = (byte)replacementArgb;
                if (alphaMode == GpuTextureAlphaMode.Premultiplied)
                {
                    replacementRed = PremultiplyChannel(replacementRed, replacementAlpha);
                    replacementGreen = PremultiplyChannel(replacementGreen, replacementAlpha);
                    replacementBlue = PremultiplyChannel(replacementBlue, replacementAlpha);
                }

                pixels[offset] = replacementRed;
                pixels[offset + 1] = replacementGreen;
                pixels[offset + 2] = replacementBlue;
                pixels[offset + 3] = replacementAlpha;
            }

            return CreateFromPixels(_width, _height, pixels, alphaMode);
        }
    }

    internal Bitmap CreateImageAttributesAdjusted(
        ImageAttributes attributes,
        ColorAdjustType type = ColorAdjustType.Bitmap)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            byte[] pixels = (byte[])ReadPixelsCore(out GpuTextureAlphaMode alphaMode).Clone();
            Dictionary<int, int>? replacements = attributes.CreateRemapLookup(type);

            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
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

                Color color = Color.FromArgb(alpha, red, green, blue);
                color = attributes.ApplyAdjustments(color, type, replacements);

                alpha = color.A;
                red = color.R;
                green = color.G;
                blue = color.B;
                if (alphaMode == GpuTextureAlphaMode.Premultiplied)
                {
                    red = PremultiplyChannel(red, alpha);
                    green = PremultiplyChannel(green, alpha);
                    blue = PremultiplyChannel(blue, alpha);
                }

                pixels[offset] = red;
                pixels[offset + 1] = green;
                pixels[offset + 2] = blue;
                pixels[offset + 3] = alpha;
            }

            return CreateFromPixels(_width, _height, pixels, alphaMode, PixelFormat.Format32bppPArgb);
        }
    }

    internal byte[] CopyStraightPixelsForPalette()
    {
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            byte[] pixels = ReadPixelsCore(out GpuTextureAlphaMode alphaMode);
            return alphaMode == GpuTextureAlphaMode.Premultiplied
                ? UnpremultiplyPixels(pixels)
                : (byte[])pixels.Clone();
        }
    }

    private static Bitmap CreateFromPixels(
        int width,
        int height,
        byte[] pixels,
        GpuTextureAlphaMode alphaMode,
        PixelFormat pixelFormat = PixelFormat.Format32bppArgb)
    {
        var bitmap = new Bitmap(width, height, pixelFormat)
        {
            _cpuPixels = pixels,
            _cpuAlphaMode = alphaMode,
            _hasDefinedPixels = true
        };
        return bitmap;
    }

    internal static Bitmap CreateOwnedRgba(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Captured dimensions must be positive.");
        }

        if (pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("The RGBA buffer length does not match its dimensions.", nameof(pixels));
        }

        return CreateFromPixels(
            width,
            height,
            pixels,
            GpuTextureAlphaMode.Straight,
            PixelFormat.Format32bppArgb);
    }

    private static void ValidateConcretePixelFormat(PixelFormat format)
    {
        if (!PixelFormatInfo.IsConcrete(format))
        {
            throw new ArgumentException("A concrete pixel format is required.", nameof(format));
        }
    }

    private void InitializeDefaultPalette(PixelFormat format)
    {
        if (!PixelFormatInfo.IsIndexed(format))
        {
            return;
        }

        int count = 1 << Image.GetPixelFormatSize(format);
        var colors = new Color[count];
        for (int index = 0; index < colors.Length; index++)
        {
            int intensity = colors.Length == 1 ? 0 : index * 255 / (colors.Length - 1);
            colors[index] = Color.FromArgb(255, intensity, intensity, intensity);
        }

        Palette = new ColorPalette(colors);
    }

    private static bool IsPremultiplied(PixelFormat format) =>
        ((int)format & (int)PixelFormat.PAlpha) != 0;

    private static unsafe byte[] CopyExternalPixelsToRgba(
        IntPtr scan0,
        int stride,
        int width,
        int height,
        PixelFormat format,
        ColorPalette palette)
    {
        int absoluteStride = checked((int)Math.Abs((long)stride));
        var rgba = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            byte* rowPointer = (byte*)scan0 + checked(y * stride);
            var row = new ReadOnlySpan<byte>(rowPointer, absoluteStride);
            for (int x = 0; x < width; x++)
            {
                ReadLockPixel(row, 0, x, format, palette, out byte red, out byte green, out byte blue, out byte alpha);
                int destination = ((y * width) + x) * 4;
                rgba[destination] = red;
                rgba[destination + 1] = green;
                rgba[destination + 2] = blue;
                rgba[destination + 3] = alpha;
            }
        }

        return rgba;
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

    public void SetResolution(float xDpi, float yDpi) => SetResolutionCore(xDpi, yDpi);

    public IntPtr GetHicon()
    {
        throw new PlatformNotSupportedException(
            "HICON export requires the explicit Windows GDI image adapter.");
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

    public void ConvertFormat(PixelFormat format)
    {
        ValidateConcretePixelFormat(format);
        if (format == PixelFormat.Format16bppGrayScale)
        {
            throw new NotSupportedException("Bitmap.ConvertFormat does not support Format16bppGrayScale.");
        }

        if (PixelFormatInfo.IsIndexed(format))
        {
            int paletteSize = 1 << Image.GetPixelFormatSize(format);
            bool useTransparentColor = Image.IsAlphaPixelFormat(format);
            ColorPalette palette = ColorPalette.CreateOptimalPalette(paletteSize, useTransparentColor, this);
            ConvertFormat(
                format,
                DitherType.ErrorDiffusion,
                PaletteType.Custom,
                palette,
                alphaThresholdPercent: 0.25f);
            return;
        }

        int targetSize = Image.GetPixelFormatSize(format);
        int sourceSize = Image.GetPixelFormatSize(_pixelFormat);
        ConvertFormat(format, targetSize > sourceSize ? DitherType.None : DitherType.Solid);
    }

    public void ConvertFormat(
        PixelFormat format,
        DitherType ditherType,
        PaletteType paletteType = PaletteType.Custom,
        ColorPalette? palette = null,
        float alphaThresholdPercent = 0f)
    {
        ValidateConcretePixelFormat(format);
        if (format == PixelFormat.Format16bppGrayScale)
        {
            throw new NotSupportedException("Bitmap.ConvertFormat does not support Format16bppGrayScale.");
        }

        if (ditherType is < DitherType.None or > DitherType.ErrorDiffusion)
        {
            throw new ArgumentException("Invalid dither type.", nameof(ditherType));
        }

        if (paletteType is < PaletteType.Custom or > PaletteType.FixedHalftone256 || paletteType == (PaletteType)1)
        {
            throw new ArgumentException("Invalid palette type.", nameof(paletteType));
        }

        if (!float.IsFinite(alphaThresholdPercent) || alphaThresholdPercent is < 0f or > 100f)
        {
            throw new ArgumentOutOfRangeException(nameof(alphaThresholdPercent));
        }

        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            if (_lockedBitmapData is not null)
            {
                throw new InvalidOperationException("A bitmap cannot be converted while its pixels are locked.");
            }

            if (PixelFormatInfo.IsIndexed(format))
            {
                int capacity = 1 << Image.GetPixelFormatSize(format);
                ColorPalette selectedPalette = SelectConversionPalette(
                    palette,
                    paletteType,
                    capacity,
                    alphaThresholdPercent > 0f);
                byte[] straightPixels = ReadPixelsCore(out GpuTextureAlphaMode alphaMode);
                if (alphaMode == GpuTextureAlphaMode.Premultiplied)
                {
                    UnpremultiplyPixelsInPlace(straightPixels);
                }
                QuantizeToPalette(
                    straightPixels,
                    _width,
                    _height,
                    selectedPalette,
                    capacity,
                    ditherType,
                    alphaThresholdPercent);
                Palette = selectedPalette;
                _pixelFormat = format;
                WritePixelsCore(straightPixels, GpuTextureAlphaMode.Straight);
                return;
            }

            if (palette is not null)
            {
                Palette = palette;
            }

            if (IsReducedDirectColorFormat(format) && ditherType is not DitherType.None and not DitherType.Solid)
            {
                byte[] pixels = ReadPixelsCore(out GpuTextureAlphaMode alphaMode);
                if (alphaMode == GpuTextureAlphaMode.Premultiplied)
                {
                    UnpremultiplyPixelsInPlace(pixels);
                }

                DitherDirectColor(pixels, _width, _height, format, ditherType);
                WritePixelsCore(pixels, GpuTextureAlphaMode.Straight);
            }

            _pixelFormat = format;
            BitmapData lockData = LockBits(
                new Rectangle(0, 0, _width, _height),
                ImageLockMode.ReadWrite,
                format);
            UnlockBits(lockData);
        }
    }

    private ColorPalette SelectConversionPalette(
        ColorPalette? palette,
        PaletteType paletteType,
        int capacity,
        bool useTransparentColor)
    {
        ColorPalette selected = palette is not null
            ? palette.ClonePalette()
            : paletteType == PaletteType.Custom
                ? ColorPalette.CreateOptimalPalette(capacity, useTransparentColor, this)
                : new ColorPalette(paletteType);

        if (selected.Entries.Length == 0)
        {
            throw new ArgumentException("Indexed conversion requires a non-empty palette.", nameof(palette));
        }

        return selected;
    }

    private static void QuantizeToPalette(
        byte[] pixels,
        int width,
        int height,
        ColorPalette palette,
        int capacity,
        DitherType ditherType,
        float alphaThresholdPercent)
    {
        Color[] entries = palette.Entries;
        int entryCount = Math.Min(capacity, entries.Length);
        int transparentIndex = -1;
        for (int index = 0; index < entryCount; index++)
        {
            if (entries[index].A == 0)
            {
                transparentIndex = index;
                break;
            }
        }

        int alphaThreshold = (int)MathF.Round(alphaThresholdPercent * 255f / 100f);
        if (ditherType == DitherType.ErrorDiffusion)
        {
            QuantizeWithErrorDiffusion(
                pixels,
                width,
                height,
                palette,
                entryCount,
                transparentIndex,
                alphaThreshold);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte alpha = pixels[offset + 3];
                int paletteIndex;
                if (transparentIndex >= 0 && alpha < alphaThreshold)
                {
                    paletteIndex = transparentIndex;
                }
                else
                {
                    int adjustment = GetDitherAdjustment(ditherType, x, y);
                    byte red = ClampToByte(pixels[offset] + adjustment);
                    byte green = ClampToByte(pixels[offset + 1] + adjustment);
                    byte blue = ClampToByte(pixels[offset + 2] + adjustment);
                    paletteIndex = FindNearestPaletteIndex(
                        palette,
                        red,
                        green,
                        blue,
                        alpha,
                        entryCount);
                }

                WritePalettePixel(pixels, offset, entries[paletteIndex]);
            }
        }
    }

    private static bool IsReducedDirectColorFormat(PixelFormat format) => format is
        PixelFormat.Format16bppRgb555 or
        PixelFormat.Format16bppRgb565 or
        PixelFormat.Format16bppArgb1555;

    private static void DitherDirectColor(
        byte[] pixels,
        int width,
        int height,
        PixelFormat format,
        DitherType ditherType)
    {
        int redLevels = 31;
        int greenLevels = format == PixelFormat.Format16bppRgb565 ? 63 : 31;
        int blueLevels = 31;
        if (ditherType == DitherType.ErrorDiffusion)
        {
            DitherDirectColorWithErrorDiffusion(
                pixels,
                width,
                height,
                redLevels,
                greenLevels,
                blueLevels);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                int adjustment = GetDitherAdjustment(ditherType, x, y);
                pixels[offset] = QuantizeChannel(ClampToByte(pixels[offset] + adjustment), redLevels);
                pixels[offset + 1] = QuantizeChannel(ClampToByte(pixels[offset + 1] + adjustment), greenLevels);
                pixels[offset + 2] = QuantizeChannel(ClampToByte(pixels[offset + 2] + adjustment), blueLevels);
            }
        }
    }

    private static void DitherDirectColorWithErrorDiffusion(
        byte[] pixels,
        int width,
        int height,
        int redLevels,
        int greenLevels,
        int blueLevels)
    {
        var currentRed = new int[width + 2];
        var currentGreen = new int[width + 2];
        var currentBlue = new int[width + 2];
        var nextRed = new int[width + 2];
        var nextGreen = new int[width + 2];
        var nextBlue = new int[width + 2];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                int red = ClampToByte(pixels[offset] + DivideRoundedBy16(currentRed[x + 1]));
                int green = ClampToByte(pixels[offset + 1] + DivideRoundedBy16(currentGreen[x + 1]));
                int blue = ClampToByte(pixels[offset + 2] + DivideRoundedBy16(currentBlue[x + 1]));
                byte quantizedRed = QuantizeChannel(red, redLevels);
                byte quantizedGreen = QuantizeChannel(green, greenLevels);
                byte quantizedBlue = QuantizeChannel(blue, blueLevels);
                AddDiffusionError(currentRed, nextRed, x, red - quantizedRed);
                AddDiffusionError(currentGreen, nextGreen, x, green - quantizedGreen);
                AddDiffusionError(currentBlue, nextBlue, x, blue - quantizedBlue);
                pixels[offset] = quantizedRed;
                pixels[offset + 1] = quantizedGreen;
                pixels[offset + 2] = quantizedBlue;
            }

            (currentRed, nextRed) = (nextRed, currentRed);
            (currentGreen, nextGreen) = (nextGreen, currentGreen);
            (currentBlue, nextBlue) = (nextBlue, currentBlue);
            Array.Clear(nextRed);
            Array.Clear(nextGreen);
            Array.Clear(nextBlue);
        }
    }

    private static byte QuantizeChannel(int value, int maximum) =>
        (byte)((((value * maximum) + 127) / 255) * 255 / maximum);

    private static void QuantizeWithErrorDiffusion(
        byte[] pixels,
        int width,
        int height,
        ColorPalette palette,
        int entryCount,
        int transparentIndex,
        int alphaThreshold)
    {
        var currentRed = new int[width + 2];
        var currentGreen = new int[width + 2];
        var currentBlue = new int[width + 2];
        var nextRed = new int[width + 2];
        var nextGreen = new int[width + 2];
        var nextBlue = new int[width + 2];
        Color[] entries = palette.Entries;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte alpha = pixels[offset + 3];
                int paletteIndex;
                int red = ClampToByte(pixels[offset] + DivideRoundedBy16(currentRed[x + 1]));
                int green = ClampToByte(pixels[offset + 1] + DivideRoundedBy16(currentGreen[x + 1]));
                int blue = ClampToByte(pixels[offset + 2] + DivideRoundedBy16(currentBlue[x + 1]));
                if (transparentIndex >= 0 && alpha < alphaThreshold)
                {
                    paletteIndex = transparentIndex;
                }
                else
                {
                    paletteIndex = FindNearestPaletteIndex(
                        palette,
                        (byte)red,
                        (byte)green,
                        (byte)blue,
                        alpha,
                        entryCount);
                }

                Color selected = entries[paletteIndex];
                int redError = red - selected.R;
                int greenError = green - selected.G;
                int blueError = blue - selected.B;
                AddDiffusionError(currentRed, nextRed, x, redError);
                AddDiffusionError(currentGreen, nextGreen, x, greenError);
                AddDiffusionError(currentBlue, nextBlue, x, blueError);
                WritePalettePixel(pixels, offset, selected);
            }

            (currentRed, nextRed) = (nextRed, currentRed);
            (currentGreen, nextGreen) = (nextGreen, currentGreen);
            (currentBlue, nextBlue) = (nextBlue, currentBlue);
            Array.Clear(nextRed);
            Array.Clear(nextGreen);
            Array.Clear(nextBlue);
        }
    }

    private static void AddDiffusionError(int[] current, int[] next, int x, int error)
    {
        current[x + 2] += error * 7;
        next[x] += error * 3;
        next[x + 1] += error * 5;
        next[x + 2] += error;
    }

    private static int DivideRoundedBy16(int value) =>
        value >= 0 ? (value + 8) / 16 : (value - 8) / 16;

    private static int GetDitherAdjustment(DitherType type, int x, int y)
    {
        int size = type switch
        {
            DitherType.Ordered4x4 or DitherType.Spiral4x4 or DitherType.DualSpiral4x4 => 4,
            DitherType.Ordered8x8 or DitherType.Spiral8x8 or DitherType.DualSpiral8x8 => 8,
            DitherType.Ordered16x16 => 16,
            _ => 0
        };
        if (size == 0)
        {
            return 0;
        }

        int threshold = type switch
        {
            DitherType.Spiral4x4 or DitherType.Spiral8x8 => SpiralThreshold(x, y, size),
            DitherType.DualSpiral4x4 or DitherType.DualSpiral8x8 =>
                ((x + y) & 1) == 0 ? SpiralThreshold(x, y, size) : size * size - 1 - SpiralThreshold(x, y, size),
            _ => BayerThreshold(x, y, size)
        };
        return ((threshold * 64) / (size * size - 1)) - 32;
    }

    private static int BayerThreshold(int x, int y, int size)
    {
        int value = 0;
        for (int bit = 0; (1 << bit) < size; bit++)
        {
            int xBit = (x >> bit) & 1;
            int yBit = (y >> bit) & 1;
            value |= (xBit ^ yBit) << (bit * 2);
            value |= yBit << (bit * 2 + 1);
        }

        return value;
    }

    private static int SpiralThreshold(int x, int y, int size)
    {
        int left = 0;
        int top = 0;
        int right = size - 1;
        int bottom = size - 1;
        int index = 0;
        while (left <= right && top <= bottom)
        {
            for (int column = left; column <= right; column++, index++)
            {
                if (column == (x & (size - 1)) && top == (y & (size - 1))) return index;
            }
            top++;
            for (int row = top; row <= bottom; row++, index++)
            {
                if (right == (x & (size - 1)) && row == (y & (size - 1))) return index;
            }
            right--;
            for (int column = right; column >= left && top <= bottom; column--, index++)
            {
                if (column == (x & (size - 1)) && bottom == (y & (size - 1))) return index;
            }
            bottom--;
            for (int row = bottom; row >= top && left <= right; row--, index++)
            {
                if (left == (x & (size - 1)) && row == (y & (size - 1))) return index;
            }
            left++;
        }

        return 0;
    }

    private static byte ClampToByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static void WritePalettePixel(byte[] pixels, int offset, Color color)
    {
        pixels[offset] = color.R;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.B;
        pixels[offset + 3] = color.A;
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

    public override void Save(System.IO.Stream stream, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);

        if (format.Guid == ImageFormat.Png.Guid)
        {
            SavePng(stream);
            return;
        }

        if (format.Guid == ImageFormat.Bmp.Guid)
        {
            SaveBmp(stream);
            return;
        }

        if (format.Guid == ImageFormat.Jpeg.Guid)
        {
            SaveJpeg(stream, quality: 75);
            return;
        }

        throw new NotSupportedException($"Image format '{format.Guid}' is not supported by the managed ProGPU codec layer.");
    }

    internal void SaveWithEncoder(System.IO.Stream stream, ImageFormat format, EncoderParameters? encoderParameters)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);

        if (encoderParameters is null)
        {
            Save(stream, format);
            return;
        }

        EncoderParameter[] parameters = encoderParameters.Param
            ?? throw new ArgumentException("Encoder parameter storage cannot be null.", nameof(encoderParameters));
        if (parameters.Length == 0)
        {
            Save(stream, format);
            return;
        }

        if (format.Guid != ImageFormat.Jpeg.Guid)
        {
            throw new NotSupportedException("The selected managed image encoder does not accept encoder parameters.");
        }

        int quality = 75;
        foreach (EncoderParameter? parameter in parameters)
        {
            if (parameter is null)
            {
                throw new ArgumentException("Encoder parameter arrays cannot contain null entries.", nameof(encoderParameters));
            }

            if (parameter.Encoder.Guid != Encoder.Quality.Guid || !parameter.TryGetInt64(out long value))
            {
                throw new NotSupportedException("The managed JPEG encoder currently supports only an integral Encoder.Quality parameter.");
            }

            quality = checked((int)Math.Clamp(value, 0L, 100L));
        }

        SaveJpeg(stream, quality);
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

    private void SaveJpeg(System.IO.Stream stream, int quality)
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

        var writer = new StbImageWriteSharp.ImageWriter();
        writer.WriteJpg(
            pixels,
            Width,
            Height,
            StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha,
            stream,
            Math.Clamp(quality, 1, 100));
    }

    private void SaveBmp(System.IO.Stream stream)
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

        int rowSize = checked(Width * 4);
        int pixelDataSize = checked(rowSize * Height);
        const int pixelDataOffset = 14 + 40;
        int fileSize = checked(pixelDataOffset + pixelDataSize);
        using var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write(0);
        writer.Write(pixelDataOffset);

        writer.Write(40);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(pixelDataSize);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (int y = Height - 1; y >= 0; y--)
        {
            int rowOffset = y * rowSize;
            for (int x = 0; x < Width; x++)
            {
                int pixelOffset = rowOffset + x * 4;
                writer.Write(pixels[pixelOffset + 2]);
                writer.Write(pixels[pixelOffset + 1]);
                writer.Write(pixels[pixelOffset]);
                writer.Write(pixels[pixelOffset + 3]);
            }
        }
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

    private static void UnpremultiplyPixelsInPlace(byte[] pixels)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte alpha = pixels[offset + 3];
            pixels[offset] = UnpremultiplyChannel(pixels[offset], alpha);
            pixels[offset + 1] = UnpremultiplyChannel(pixels[offset + 1], alpha);
            pixels[offset + 2] = UnpremultiplyChannel(pixels[offset + 2], alpha);
        }
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
    private BitmapData? _lockedBitmapData;
    private Rectangle _lockedRect;
    private PixelFormat _lockedPixelFormat;
    private int _lockedStride;
    private bool _lockedWriteBack;
    private GpuTextureAlphaMode _lockedTextureAlphaMode;

    public BitmapData LockBits(Rectangle rect, ImageLockMode flags, PixelFormat format)
        => LockBits(rect, flags, format, new BitmapData());

    public unsafe BitmapData LockBits(
        Rectangle rect,
        ImageLockMode flags,
        PixelFormat format,
        BitmapData bitmapData)
    {
        ArgumentNullException.ThrowIfNull(bitmapData);
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            ValidateLockBitsRectangle(rect);
            ValidateConcretePixelFormat(format);
            int access = (int)flags & (int)ImageLockMode.ReadWrite;
            if (access is < (int)ImageLockMode.ReadOnly or > (int)ImageLockMode.ReadWrite)
            {
                throw new ArgumentException("The lock mode must specify read, write, or read/write access.", nameof(flags));
            }

            if (_lockedBitmapData is not null)
            {
                throw new InvalidOperationException("Bitmap already has an active lock. Call UnlockBits before LockBits again.");
            }

            byte[] fullPixels = ReadPixelsCore(out GpuTextureAlphaMode lockAlphaMode);
            int subWidth = rect.Width;
            int subHeight = rect.Height;
            bool usesCallerBuffer = (((int)flags & (int)ImageLockMode.UserInputBuffer) != 0);
            int stride;
            if (usesCallerBuffer)
            {
                if (bitmapData.Scan0 == IntPtr.Zero)
                {
                    throw new ArgumentException("UserInputBuffer requires a non-zero Scan0 pointer.", nameof(bitmapData));
                }

                stride = bitmapData.Stride;
                int minimumRowBytes = GetMinimumRowBytes(subWidth, format);
                if (stride == 0 || Math.Abs((long)stride) < minimumRowBytes)
                {
                    throw new ArgumentException("The caller buffer stride is smaller than the requested pixel row.", nameof(bitmapData));
                }
            }
            else
            {
                stride = GetLockStride(subWidth, format);
                _lockedBytes = new byte[checked(stride * subHeight)];
                _lockedHandle = GCHandle.Alloc(_lockedBytes, GCHandleType.Pinned);
                bitmapData.Scan0 = _lockedHandle.AddrOfPinnedObject();
            }

            ColorPalette? palette = PixelFormatInfo.IsIndexed(format) ? Palette : null;
            try
            {
                if (access != (int)ImageLockMode.WriteOnly)
                {
                    CopyRgbaToExternalBuffer(
                        fullPixels,
                        bitmapData.Scan0,
                        stride,
                        rect,
                        format,
                        lockAlphaMode,
                        palette);
                }
            }
            catch
            {
                if (_lockedHandle.IsAllocated)
                {
                    _lockedHandle.Free();
                }

                _lockedBytes = null;
                throw;
            }

            bitmapData.Width = subWidth;
            bitmapData.Height = subHeight;
            bitmapData.Stride = stride;
            bitmapData.PixelFormat = format;
            _lockedRect = rect;
            _lockedPixelFormat = format;
            _lockedStride = stride;
            _lockedWriteBack = access != (int)ImageLockMode.ReadOnly;
            _lockedBitmapData = bitmapData;
            _lockedTextureAlphaMode = lockAlphaMode;
            return bitmapData;
        }
    }

    private static int GetLockStride(int width, PixelFormat format)
    {
        int bytesPerRow = GetMinimumRowBytes(width, format);
        return (bytesPerRow + 3) & ~3;
    }

    private static int GetMinimumRowBytes(int width, PixelFormat format)
    {
        ValidateConcretePixelFormat(format);
        int bitsPerPixel = Image.GetPixelFormatSize(format);
        return checked((width * bitsPerPixel + 7) / 8);
    }

    private unsafe void CopyRgbaToExternalBuffer(
        byte[] source,
        IntPtr destination,
        int stride,
        Rectangle rect,
        PixelFormat format,
        GpuTextureAlphaMode sourceAlphaMode,
        ColorPalette? palette)
    {
        int rowLength = checked((int)Math.Abs((long)stride));
        for (int y = 0; y < rect.Height; y++)
        {
            var srcOffset = ((rect.Y + y) * Width + rect.X) * 4;
            var row = new Span<byte>((byte*)destination + checked(y * stride), rowLength);
            row.Clear();
            for (int x = 0; x < rect.Width; x++)
            {
                var src = srcOffset + x * 4;
                var r = source[src];
                var g = source[src + 1];
                var b = source[src + 2];
                var a = source[src + 3];
                ConvertPixelFromTextureAlphaMode(ref r, ref g, ref b, a, sourceAlphaMode, format);
                WriteLockPixel(row, x, format, palette, r, g, b, a);
            }
        }
    }

    private static void ConvertPixelFromTextureAlphaMode(ref byte r, ref byte g, ref byte b, byte a, GpuTextureAlphaMode sourceAlphaMode, PixelFormat lockPixelFormat)
    {
        bool lockPixelsArePremultiplied = IsPremultiplied(lockPixelFormat);
        if (sourceAlphaMode == GpuTextureAlphaMode.Premultiplied && !lockPixelsArePremultiplied)
        {
            r = UnpremultiplyChannel(r, a);
            g = UnpremultiplyChannel(g, a);
            b = UnpremultiplyChannel(b, a);
        }
        else if (sourceAlphaMode == GpuTextureAlphaMode.Straight && lockPixelsArePremultiplied)
        {
            r = PremultiplyChannel(r, a);
            g = PremultiplyChannel(g, a);
            b = PremultiplyChannel(b, a);
        }
    }

    private static void WriteLockPixel(
        Span<byte> row,
        int x,
        PixelFormat format,
        ColorPalette? palette,
        byte r,
        byte g,
        byte b,
        byte a)
    {
        switch (format)
        {
            case PixelFormat.Format1bppIndexed:
            {
                int paletteIndex = FindNearestPaletteIndex(palette!, r, g, b, a, 2);
                int offset = x >> 3;
                row[offset] = (byte)(row[offset] | (paletteIndex << (7 - (x & 7))));
                break;
            }
            case PixelFormat.Format4bppIndexed:
            {
                int paletteIndex = FindNearestPaletteIndex(palette!, r, g, b, a, 16);
                int offset = x >> 1;
                int shift = (x & 1) == 0 ? 4 : 0;
                row[offset] = (byte)(row[offset] | (paletteIndex << shift));
                break;
            }
            case PixelFormat.Format8bppIndexed:
                row[x] = (byte)FindNearestPaletteIndex(palette!, r, g, b, a, 256);
                break;
            case PixelFormat.Format16bppGrayScale:
            {
                ushort gray = (ushort)(((r * 2126 + g * 7152 + b * 722) * 257L + 5000) / 10000);
                WriteUInt16(row, x * 2, gray);
                break;
            }
            case PixelFormat.Format16bppRgb555:
            {
                ushort rgb555 = (ushort)(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
                WriteUInt16(row, x * 2, rgb555);
                break;
            }
            case PixelFormat.Format32bppArgb:
            case PixelFormat.Format32bppPArgb:
                var offset32 = x * 4;
                row[offset32] = b;
                row[offset32 + 1] = g;
                row[offset32 + 2] = r;
                row[offset32 + 3] = a;
                break;
            case PixelFormat.Format32bppRgb:
                offset32 = x * 4;
                row[offset32] = b;
                row[offset32 + 1] = g;
                row[offset32 + 2] = r;
                row[offset32 + 3] = 255;
                break;
            case PixelFormat.Format24bppRgb:
                var offset24 = x * 3;
                row[offset24] = b;
                row[offset24 + 1] = g;
                row[offset24 + 2] = r;
                break;
            case PixelFormat.Format16bppRgb565:
                ushort rgb565 = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                WriteUInt16(row, x * 2, rgb565);
                break;
            case PixelFormat.Format16bppArgb1555:
            {
                ushort argb1555 = (ushort)((a >= 128 ? 0x8000 : 0) | ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
                WriteUInt16(row, x * 2, argb1555);
                break;
            }
            case PixelFormat.Format48bppRgb:
            {
                int offset = x * 6;
                WriteUInt16(row, offset, (ushort)(b * 257));
                WriteUInt16(row, offset + 2, (ushort)(g * 257));
                WriteUInt16(row, offset + 4, (ushort)(r * 257));
                break;
            }
            case PixelFormat.Format64bppArgb:
            case PixelFormat.Format64bppPArgb:
            {
                int offset = x * 8;
                WriteUInt16(row, offset, (ushort)(b * 257));
                WriteUInt16(row, offset + 2, (ushort)(g * 257));
                WriteUInt16(row, offset + 4, (ushort)(r * 257));
                WriteUInt16(row, offset + 6, (ushort)(a * 257));
                break;
            }
        }
    }

    private static int FindNearestPaletteIndex(
        ColorPalette palette,
        byte red,
        byte green,
        byte blue,
        byte alpha,
        int maximumEntries)
    {
        Color[] entries = palette.Entries;
        int count = Math.Min(maximumEntries, entries.Length);
        if (count == 0)
        {
            return 0;
        }

        int bestIndex = 0;
        long bestDistance = long.MaxValue;
        for (int index = 0; index < count; index++)
        {
            Color candidate = entries[index];
            int deltaAlpha = alpha - candidate.A;
            int deltaRed = red - candidate.R;
            int deltaGreen = green - candidate.G;
            int deltaBlue = blue - candidate.B;
            long distance = (long)deltaAlpha * deltaAlpha
                + (long)deltaRed * deltaRed
                + (long)deltaGreen * deltaGreen
                + (long)deltaBlue * deltaBlue;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
                if (distance == 0)
                {
                    break;
                }
            }
        }

        return bestIndex;
    }

    private static void WriteUInt16(Span<byte> destination, int offset, ushort value)
    {
        destination[offset] = (byte)value;
        destination[offset + 1] = (byte)(value >> 8);
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
        ArgumentNullException.ThrowIfNull(bitmapData);
        lock (_textureLifetimeLock)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(bitmapData, _lockedBitmapData))
            {
                throw new ArgumentException("BitmapData does not represent the active bitmap lock.", nameof(bitmapData));
            }

            try
            {
                if (_lockedWriteBack)
                {
                    ColorPalette? palette = PixelFormatInfo.IsIndexed(_lockedPixelFormat) ? Palette : null;
                    var rgba = CopyExternalPixelsToRgba(
                        bitmapData.Scan0,
                        _lockedStride,
                        _lockedRect.Width,
                        _lockedRect.Height,
                        _lockedPixelFormat,
                        palette ?? new ColorPalette());
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
            }
            finally
            {
                if (_lockedHandle.IsAllocated)
                {
                    _lockedHandle.Free();
                }

                _lockedBytes = null;
                _lockedBitmapData = null;
                _lockedStride = 0;
                _lockedPixelFormat = default;
                _lockedWriteBack = false;
                _lockedTextureAlphaMode = default;
            }
        }
    }

    private static void ConvertPixelsToTextureAlphaMode(byte[] rgba, PixelFormat lockPixelFormat, GpuTextureAlphaMode targetAlphaMode)
    {
        bool lockPixelsArePremultiplied = IsPremultiplied(lockPixelFormat);

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

    private static void ReadLockPixel(
        ReadOnlySpan<byte> row,
        int rowOffset,
        int x,
        PixelFormat format,
        ColorPalette palette,
        out byte r,
        out byte g,
        out byte b,
        out byte a)
    {
        switch (format)
        {
            case PixelFormat.Format1bppIndexed:
            {
                int index = (row[rowOffset + (x >> 3)] >> (7 - (x & 7))) & 1;
                ReadPaletteColor(palette, index, out r, out g, out b, out a);
                break;
            }
            case PixelFormat.Format4bppIndexed:
            {
                byte packed = row[rowOffset + (x >> 1)];
                int index = (x & 1) == 0 ? packed >> 4 : packed & 0x0f;
                ReadPaletteColor(palette, index, out r, out g, out b, out a);
                break;
            }
            case PixelFormat.Format8bppIndexed:
                ReadPaletteColor(palette, row[rowOffset + x], out r, out g, out b, out a);
                break;
            case PixelFormat.Format16bppGrayScale:
            {
                ushort gray = ReadUInt16(row, rowOffset + x * 2);
                r = g = b = (byte)((gray + 128) / 257);
                a = 255;
                break;
            }
            case PixelFormat.Format16bppRgb555:
            {
                ushort rgb555 = ReadUInt16(row, rowOffset + x * 2);
                r = Expand5To8((rgb555 >> 10) & 0x1f);
                g = Expand5To8((rgb555 >> 5) & 0x1f);
                b = Expand5To8(rgb555 & 0x1f);
                a = 255;
                break;
            }
            case PixelFormat.Format32bppArgb:
            case PixelFormat.Format32bppPArgb:
                var offset32 = rowOffset + x * 4;
                b = row[offset32];
                g = row[offset32 + 1];
                r = row[offset32 + 2];
                a = row[offset32 + 3];
                break;
            case PixelFormat.Format32bppRgb:
                offset32 = rowOffset + x * 4;
                b = row[offset32];
                g = row[offset32 + 1];
                r = row[offset32 + 2];
                a = 255;
                break;
            case PixelFormat.Format24bppRgb:
                var offset24 = rowOffset + x * 3;
                b = row[offset24];
                g = row[offset24 + 1];
                r = row[offset24 + 2];
                a = 255;
                break;
            case PixelFormat.Format16bppRgb565:
                ushort rgb565 = ReadUInt16(row, rowOffset + x * 2);
                r = Expand5To8((rgb565 >> 11) & 0x1f);
                g = Expand6To8((rgb565 >> 5) & 0x3f);
                b = Expand5To8(rgb565 & 0x1f);
                a = 255;
                break;
            case PixelFormat.Format16bppArgb1555:
            {
                ushort argb1555 = ReadUInt16(row, rowOffset + x * 2);
                r = Expand5To8((argb1555 >> 10) & 0x1f);
                g = Expand5To8((argb1555 >> 5) & 0x1f);
                b = Expand5To8(argb1555 & 0x1f);
                a = (argb1555 & 0x8000) == 0 ? (byte)0 : (byte)255;
                break;
            }
            case PixelFormat.Format48bppRgb:
            {
                int offset = rowOffset + x * 6;
                b = ToByte(ReadUInt16(row, offset));
                g = ToByte(ReadUInt16(row, offset + 2));
                r = ToByte(ReadUInt16(row, offset + 4));
                a = 255;
                break;
            }
            case PixelFormat.Format64bppArgb:
            case PixelFormat.Format64bppPArgb:
            {
                int offset = rowOffset + x * 8;
                b = ToByte(ReadUInt16(row, offset));
                g = ToByte(ReadUInt16(row, offset + 2));
                r = ToByte(ReadUInt16(row, offset + 4));
                a = ToByte(ReadUInt16(row, offset + 6));
                break;
            }
            default:
                throw new NotSupportedException($"Pixel format '{format}' is not supported.");
        }
    }

    private static void ReadPaletteColor(
        ColorPalette palette,
        int index,
        out byte red,
        out byte green,
        out byte blue,
        out byte alpha)
    {
        Color[] entries = palette.Entries;
        Color color = (uint)index < (uint)entries.Length ? entries[index] : Color.Transparent;
        red = color.R;
        green = color.G;
        blue = color.B;
        alpha = color.A;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset) =>
        (ushort)(source[offset] | (source[offset + 1] << 8));

    private static byte Expand5To8(int value) => (byte)((value << 3) | (value >> 2));

    private static byte Expand6To8(int value) => (byte)((value << 2) | (value >> 4));

    private static byte ToByte(ushort value) => (byte)((value + 128) / 257);

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
                _lockedBitmapData = null;
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
