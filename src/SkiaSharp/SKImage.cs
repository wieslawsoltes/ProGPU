using System;
using System.Buffers;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace SkiaSharp;

public class SKImage : IDisposable
{
    public IntPtr Handle { get; } = SKObjectHandle.Create();
    public GpuTexture Texture { get; }
    private readonly bool _ownsTexture;
    private readonly SKImageInfo _info;
    public int Width => (int)Texture.Width;
    public int Height => (int)Texture.Height;
    public SKImageInfo Info => _info;
    public SKColorType ColorType => _info.ColorType;
    public SKAlphaType AlphaType => _info.AlphaType;
    public SKColorSpace? ColorSpace => _info.ColorSpace;
    public bool IsAlphaOnly => ColorType == SKColorType.Alpha8;
    public bool IsLazyGenerated => false;
    public bool IsTextureBacked => true;

    public SKImage(GpuTexture texture)
        : this(texture, ownsTexture: false, CreateTextureInfo(texture))
    {
    }

    private SKImage(GpuTexture texture, bool ownsTexture, SKImageInfo info)
    {
        Texture = texture;
        _ownsTexture = ownsTexture;
        _info = new SKImageInfo(
            (int)texture.Width,
            (int)texture.Height,
            info.ColorType,
            info.AlphaType,
            info.ColorSpace);
    }

    public static SKImage FromBitmap(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var ctx = SKContextHelper.GetContext();
        var texture = CreateTextureFromBitmap(
            bitmap,
            ctx,
            generateMipmaps: false,
            "SKImage Texture");

        return new SKImage(texture, ownsTexture: true, bitmap.Info);
    }

    internal static GpuTexture CreateTextureFromBitmap(
        SKBitmap bitmap,
        WgpuContext context,
        bool generateMipmaps,
        string label)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(context);
        var usage = TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc;
        if (generateMipmaps)
        {
            usage |= TextureUsage.RenderAttachment;
        }

        var texture = new GpuTexture(
            context,
            (uint)bitmap.Width,
            (uint)bitmap.Height,
            TextureFormat.Rgba8Unorm,
            usage,
            label,
            alphaMode: bitmap.AlphaType == SKAlphaType.Unpremul
                ? GpuTextureAlphaMode.Straight
                : GpuTextureAlphaMode.Premultiplied,
            mipLevelCount: generateMipmaps
                ? CalculateMipLevelCount((uint)bitmap.Width, (uint)bitmap.Height)
                : 1u);

        try
        {
            byte[] buffer = bitmap.CopyRgba8888Rows();
            if (bitmap.AlphaType == SKAlphaType.Opaque)
            {
                ForceOpaqueAlpha(buffer);
            }

            texture.WritePixels(new ReadOnlySpan<byte>(buffer));
            if (generateMipmaps)
            {
                texture.GenerateMipmaps2DLinear();
            }

            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    public static SKImage? FromEncodedData(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var bitmap = SKBitmap.Decode(new SKData(data));
            return bitmap is null ? null : FromBitmap(bitmap);
        }
        catch (Exception exception) when (IsInvalidEncodedImageException(exception))
        {
            return null;
        }
    }

    public static SKImage? FromEncodedData(ReadOnlySpan<byte> data) =>
        FromEncodedData(data.ToArray());

    public static SKImage? FromEncodedData(SKData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var bitmap = SKBitmap.Decode(data);
        return bitmap is null ? null : FromBitmap(bitmap);
    }

    public static SKImage? FromEncodedData(Stream data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var encoded = SKData.Create(data);
        return FromEncodedData(encoded);
    }

    private static bool IsInvalidEncodedImageException(Exception exception) =>
        exception is InvalidOperationException or ArgumentException or FormatException or IndexOutOfRangeException;

    public static SKImage FromPixels(SKImageInfo info, IntPtr pixels, int rowBytes)
    {
        using var bitmap = new SKBitmap();
        bitmap.InstallPixels(info, pixels, rowBytes);
        return FromBitmap(bitmap);
    }

    public static SKImage FromPicture(SKPicture picture, SKSizeI dimensions)
    {
        return FromPicture(picture, dimensions, SKMatrix.Identity);
    }

    public static SKImage FromPicture(SKPicture picture, SKSizeI dimensions, SKMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (dimensions.Width <= 0 || dimensions.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Picture image dimensions must be positive.");
        }

        using var surface = SKSurface.Create(new SKImageInfo(
            dimensions.Width,
            dimensions.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.SetMatrix(in matrix);
        surface.Canvas.DrawPicture(picture);
        return surface.Snapshot();
    }

    public static SKImage? FromAdoptedTexture(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(texture);
        var gpuTexture = texture.BackendTexture;
        if (gpuTexture == null)
        {
            return null;
        }

        if (!ReferenceEquals(gpuTexture.Context, context.Context))
        {
            throw new InvalidOperationException("The adopted backend texture belongs to a different ProGPU context.");
        }

        if ((gpuTexture.Usage & TextureUsage.TextureBinding) == 0 ||
            (gpuTexture.Usage & TextureUsage.CopySrc) == 0)
        {
            throw new InvalidOperationException(
                "Adopted backend textures must include TextureUsage.TextureBinding and TextureUsage.CopySrc.");
        }

        if (gpuTexture.SampleCount != 1)
        {
            throw new NotSupportedException("This WebGPU-backed Skia shim can only adopt single-sampled textures.");
        }

        var alphaType = gpuTexture.AlphaMode == GpuTextureAlphaMode.Straight
            ? SKAlphaType.Unpremul
            : SKAlphaType.Premul;
        return new SKImage(
            gpuTexture,
            ownsTexture: true,
            new SKImageInfo(texture.Width, texture.Height, colorType, alphaType));
    }

    public static SKImage? FromAdoptedTexture(
        GRContext context,
        GRBackendTexture texture,
        SKColorType colorType) =>
        FromAdoptedTexture(context, texture, GRSurfaceOrigin.TopLeft, colorType);

    public static SKImage FromTexture(GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (!texture.Usage.HasFlag(TextureUsage.CopySrc))
        {
            throw new InvalidOperationException(
                "Textures wrapped by SKImage.FromTexture must include TextureUsage.CopySrc so SKCanvas.DrawImage can retain a copy for deferred rendering.");
        }

        return new SKImage(texture, ownsTexture: false, CreateTextureInfo(texture));
    }

    private static void ForceOpaqueAlpha(byte[] rgbaPixels)
    {
        for (int i = 3; i < rgbaPixels.Length; i += 4)
        {
            rgbaPixels[i] = 255;
        }
    }

    internal static SKImage FromOwnedTexture(GpuTexture texture, SKImageInfo? info = null)
    {
        return new SKImage(texture, ownsTexture: true, info ?? CreateTextureInfo(texture));
    }

    internal static SKImage FromBorrowedTexture(GpuTexture texture, SKImageInfo info)
    {
        return new SKImage(texture, ownsTexture: false, info);
    }

    private static uint CalculateMipLevelCount(uint width, uint height)
    {
        var dimension = Math.Max(width, height);
        uint count = 1;
        while (dimension > 1)
        {
            dimension /= 2;
            count++;
        }

        return count;
    }

    internal SKImage CreateOwnedCopy()
    {
        var texture = new GpuTexture(
            Texture.Context,
            Texture.Width,
            Texture.Height,
            Texture.Format,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "SKImage Shader Retained Texture",
            alphaMode: Texture.AlphaMode);
        texture.CopyFrom(Texture);
        return FromOwnedTexture(texture, _info);
    }

    public SKShader ToShader(
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKMatrix localMatrix)
    {
        return SKShader.CreateRetainedImage(CreateOwnedCopy(), tileModeX, tileModeY, localMatrix);
    }

    public SKShader ToShader(SKShaderTileMode tileModeX, SKShaderTileMode tileModeY)
    {
        return ToShader(tileModeX, tileModeY, SKMatrix.Identity);
    }

    public void ScalePixels(SKPixmap dst, SKSamplingOptions sampling)
    {
        ArgumentNullException.ThrowIfNull(dst);
        if (dst.GetPixels() == IntPtr.Zero)
        {
            throw new ArgumentException("Destination pixmap must provide a pixel buffer.", nameof(dst));
        }

        if (dst.Info.Width <= 0 || dst.Info.Height <= 0 || Width <= 0 || Height <= 0)
        {
            return;
        }

        int dstRowBytes = dst.RowBytes > 0 ? dst.RowBytes : dst.Info.RowBytes;
        int minDstRowBytes = dst.Info.Width * 4;
        if (dstRowBytes < minDstRowBytes)
        {
            throw new ArgumentException("Destination row bytes must be large enough for one row.", nameof(dst));
        }

        byte[] src = ReadTexturePixelsAsRgba8888();
        bool sourcePremultiplied = Texture.AlphaMode == GpuTextureAlphaMode.Premultiplied;
        bool targetPremultiplied = dst.Info.AlphaType == SKAlphaType.Premul;
        bool forceOpaqueAlpha = dst.Info.AlphaType == SKAlphaType.Opaque;

        unsafe
        {
            fixed (byte* srcBase = src)
            {
                byte* dstBase = (byte*)dst.GetPixels();
                for (int y = 0; y < dst.Info.Height; y++)
                {
                    int srcY = Math.Clamp((int)((long)y * Height / dst.Info.Height), 0, Height - 1);
                    byte* dstRow = dstBase + y * dstRowBytes;

                    for (int x = 0; x < dst.Info.Width; x++)
                    {
                        int srcX = Math.Clamp((int)((long)x * Width / dst.Info.Width), 0, Width - 1);
                        byte* srcPixel = srcBase + (srcY * Width + srcX) * 4;
                        byte* dstPixel = dstRow + x * 4;

                        byte alpha = srcPixel[3];
                        byte red = srcPixel[0];
                        byte green = srcPixel[1];
                        byte blue = srcPixel[2];

                        if (sourcePremultiplied && !targetPremultiplied)
                        {
                            red = UnpremultiplyChannel(red, alpha);
                            green = UnpremultiplyChannel(green, alpha);
                            blue = UnpremultiplyChannel(blue, alpha);
                        }
                        else if (!sourcePremultiplied && targetPremultiplied)
                        {
                            red = PremultiplyChannel(red, alpha);
                            green = PremultiplyChannel(green, alpha);
                            blue = PremultiplyChannel(blue, alpha);
                        }

                        if (forceOpaqueAlpha)
                        {
                            alpha = 255;
                        }

                        if (dst.Info.ColorType == SKColorType.Bgra8888)
                        {
                            dstPixel[0] = blue;
                            dstPixel[1] = green;
                            dstPixel[2] = red;
                            dstPixel[3] = alpha;
                        }
                        else
                        {
                            dstPixel[0] = red;
                            dstPixel[1] = green;
                            dstPixel[2] = blue;
                            dstPixel[3] = alpha;
                        }
                    }
                }
            }
        }
    }

    public void ReadPixels(SKImageInfo dstInfo, IntPtr dstPixels, int dstRowBytes, int srcX, int srcY, SKImageCachingHint cachingHint)
    {
        if (dstPixels == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(dstPixels));
        }

        byte[] pixels = ReadTexturePixelsAsRgba8888();
        int srcWidth = Width;
        int srcHeight = Height;
        
        int copySrcX = Math.Max(0, srcX);
        int copySrcY = Math.Max(0, srcY);
        int dstStartX = copySrcX - srcX;
        int dstStartY = copySrcY - srcY;
        int copyWidth = Math.Min(dstInfo.Width - dstStartX, srcWidth - copySrcX);
        int copyHeight = Math.Min(dstInfo.Height - dstStartY, srcHeight - copySrcY);
        
        if (copyWidth <= 0 || copyHeight <= 0) return;
        
        int actualDstRowBytes = dstRowBytes > 0 ? dstRowBytes : dstInfo.Width * 4;
        int minimumDstRowBytes = checked((dstStartX + copyWidth) * 4);
        if (actualDstRowBytes < minimumDstRowBytes)
        {
            throw new ArgumentException("Destination row bytes must cover the copied pixel range.", nameof(dstRowBytes));
        }

        bool forceOpaqueAlpha = dstInfo.AlphaType == SKAlphaType.Opaque;
        bool convertAlpha = forceOpaqueAlpha
            || (Texture.AlphaMode == GpuTextureAlphaMode.Premultiplied
            ? dstInfo.AlphaType == SKAlphaType.Unpremul
            : dstInfo.AlphaType == SKAlphaType.Premul);
        
        unsafe
        {
            fixed (byte* src = pixels)
            {
                byte* dst = (byte*)dstPixels;
                for (int y = 0; y < copyHeight; y++)
                {
                    int srcRowY = copySrcY + y;
                    byte* srcRow = src + (srcRowY * srcWidth + copySrcX) * 4;
                    byte* dstRow = dst + (dstStartY + y) * actualDstRowBytes + dstStartX * 4;
                    
                    if (dstInfo.ColorType == SKColorType.Bgra8888 || convertAlpha)
                    {
                        for (int x = 0; x < copyWidth; x++)
                        {
                            int srcIdx = x * 4;
                            int dstIdx = x * 4;
                            byte alpha = srcRow[srcIdx + 3];
                            byte red = srcRow[srcIdx];
                            byte green = srcRow[srcIdx + 1];
                            byte blue = srcRow[srcIdx + 2];

                            if (Texture.AlphaMode == GpuTextureAlphaMode.Premultiplied
                                && (dstInfo.AlphaType == SKAlphaType.Unpremul || forceOpaqueAlpha))
                            {
                                red = UnpremultiplyChannel(red, alpha);
                                green = UnpremultiplyChannel(green, alpha);
                                blue = UnpremultiplyChannel(blue, alpha);
                            }
                            else if (Texture.AlphaMode == GpuTextureAlphaMode.Straight
                                && dstInfo.AlphaType == SKAlphaType.Premul)
                            {
                                red = PremultiplyChannel(red, alpha);
                                green = PremultiplyChannel(green, alpha);
                                blue = PremultiplyChannel(blue, alpha);
                            }

                            if (forceOpaqueAlpha)
                            {
                                alpha = 255;
                            }

                            if (dstInfo.ColorType == SKColorType.Bgra8888)
                            {
                                dstRow[dstIdx] = blue;
                                dstRow[dstIdx + 1] = green;
                                dstRow[dstIdx + 2] = red;
                                dstRow[dstIdx + 3] = alpha;
                            }
                            else
                            {
                                dstRow[dstIdx] = red;
                                dstRow[dstIdx + 1] = green;
                                dstRow[dstIdx + 2] = blue;
                                dstRow[dstIdx + 3] = alpha;
                            }
                        }
                    }
                    else
                    {
                        System.Buffer.MemoryCopy(srcRow, dstRow, actualDstRowBytes, copyWidth * 4);
                    }
                }
            }
        }
    }

    public SKImage ToRasterImage(bool share) => this;

    public SKData Encode()
    {
        return Encode(SKEncodedImageFormat.Png, 100);
    }

    public SKData Encode(SKEncodedImageFormat format, int quality)
    {
        byte[] pixels = ReadTexturePixelsAsRgba8888();
        if (Texture.AlphaMode == GpuTextureAlphaMode.Premultiplied)
        {
            UnpremultiplyRgba8888(pixels);
        }

        using (var ms = new MemoryStream())
        {
            var writer = new StbImageWriteSharp.ImageWriter();
            if (format == SKEncodedImageFormat.Jpeg)
            {
                writer.WriteJpg(pixels, Width, Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms, quality);
            }
            else
            {
                writer.WritePng(pixels, Width, Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms);
            }
            return new SKData(ms.ToArray());
        }
    }

    private byte[] ReadTexturePixelsAsRgba8888()
    {
        byte[] pixels = Texture.ReadPixels();
        if (Texture.Format is TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb)
        {
            SwizzleBgraToRgba(pixels);
        }

        return pixels;
    }

    private static void SwizzleBgraToRgba(byte[] pixels)
    {
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }
    }

    private static void UnpremultiplyRgba8888(byte[] pixels)
    {
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            if (alpha == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                continue;
            }

            if (alpha == 255)
            {
                continue;
            }

            pixels[i] = UnpremultiplyChannel(pixels[i], alpha);
            pixels[i + 1] = UnpremultiplyChannel(pixels[i + 1], alpha);
            pixels[i + 2] = UnpremultiplyChannel(pixels[i + 2], alpha);
        }
    }

    private static byte UnpremultiplyChannel(byte value, int alpha)
    {
        if (alpha == 0)
        {
            return 0;
        }

        return (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);
    }

    private static byte PremultiplyChannel(byte value, int alpha)
    {
        return (byte)((value * alpha + 127) / 255);
    }

    public void Dispose()
    {
        if (_ownsTexture)
        {
            Texture.Dispose();
        }
    }

    private static SKImageInfo CreateTextureInfo(GpuTexture texture)
    {
        var colorType = texture.Format is TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb
            ? SKColorType.Bgra8888
            : SKColorType.Rgba8888;
        var alphaType = texture.AlphaMode == GpuTextureAlphaMode.Straight
            ? SKAlphaType.Unpremul
            : SKAlphaType.Premul;
        var colorSpace = texture.Format is TextureFormat.Rgba8UnormSrgb or TextureFormat.Bgra8UnormSrgb
            ? SKColorSpace.CreateSrgb()
            : null;
        return new SKImageInfo((int)texture.Width, (int)texture.Height, colorType, alphaType, colorSpace);
    }
}

public class SKPixmap : SKObject
{
    private SKImageInfo _info;
    private IntPtr _pixels;
    private int _rowBytes;
    private object? _pixelSource;

    public SKImageInfo Info => _info;
    public int Width => _info.Width;
    public int Height => _info.Height;
    public SKSizeI Size => _info.Size;
    public SKRectI Rect => _info.Rect;
    public SKColorType ColorType => _info.ColorType;
    public SKAlphaType AlphaType => _info.AlphaType;
    public SKColorSpace? ColorSpace => _info.ColorSpace;
    public int BytesPerPixel => _info.BytesPerPixel;
    public int BitShiftPerPixel => _info.BitShiftPerPixel;
    public int RowBytes => _rowBytes;
    public int BytesSize => _info.BytesSize;
    public long BytesSize64 => _info.BytesSize64;

    internal object? PixelSource => _pixelSource;

    public SKPixmap()
        : base(SKObjectHandle.Create(), owns: true)
    {
    }

    public SKPixmap(SKImageInfo info, IntPtr pixels)
        : this(info, pixels, info.RowBytes)
    {
    }

    public SKPixmap(SKImageInfo info, IntPtr pixels, int rowBytes)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Reset(info, pixels, rowBytes);
    }

    public void Reset()
    {
        _info = default;
        _pixels = IntPtr.Zero;
        _rowBytes = 0;
        _pixelSource = null;
    }

    public void Reset(SKImageInfo info, IntPtr pixels, int rowBytes)
    {
        _info = info;
        _pixels = pixels;
        _rowBytes = rowBytes;
        _pixelSource = null;
    }

    internal void SetPixelSource(object source) => _pixelSource = source;

    public IntPtr GetPixels() => _pixels;

    public IntPtr GetPixels(int x, int y)
    {
        if (_info.IsEmpty || _info.BytesPerPixel <= 0)
        {
            return _pixels;
        }

        ValidateCoordinates(x, y);
        return IntPtr.Add(_pixels, checked(y * _rowBytes + x * _info.BytesPerPixel));
    }

    public Span<byte> GetPixelSpan() => GetPixelSpan<byte>();

    public Span<byte> GetPixelSpan(int x, int y) => GetPixelSpan<byte>(x, y);

    public Span<T> GetPixelSpan<T>() where T : unmanaged => GetPixelSpan<T>(0, 0);

    public unsafe Span<T> GetPixelSpan<T>(int x, int y) where T : unmanaged
    {
        if (_info.IsEmpty || _info.BytesPerPixel <= 0 || _pixels == IntPtr.Zero)
        {
            return Span<T>.Empty;
        }

        ValidateCoordinates(x, y);
        int length;
        int offset;
        if (typeof(T) == typeof(byte))
        {
            length = SKBitmap.ComputeByteCount(_info, _rowBytes);
            offset = checked(y * _rowBytes + x * _info.BytesPerPixel);
        }
        else
        {
            int elementSize = sizeof(T);
            if (_info.BytesPerPixel != elementSize)
            {
                throw new ArgumentException(
                    $"Size of T ({elementSize}) is not the same as the size of each pixel ({_info.BytesPerPixel}).",
                    nameof(T));
            }

            if (_info.Height > 1 && _rowBytes % elementSize != 0)
            {
                throw new ArgumentException(
                    $"The row stride ({_rowBytes}) is not a multiple of the size of each pixel ({elementSize}).");
            }

            int elementsPerRow = _rowBytes / elementSize;
            length = checked((_info.Height - 1) * elementsPerRow + _info.Width);
            offset = checked(y * elementsPerRow + x);
        }

        return new Span<T>(_pixels.ToPointer(), length).Slice(offset);
    }

    public SKColor GetPixelColor(int x, int y)
    {
        ValidateCoordinates(x, y);
        return TryReadColor(GetPixels(x, y), _info, out var color) ? color : SKColor.Empty;
    }

    public SKColorF GetPixelColorF(int x, int y)
    {
        var color = GetPixelColor(x, y);
        const float scale = 1f / 255f;
        return new SKColorF(color.R * scale, color.G * scale, color.B * scale, color.A * scale);
    }

    public float GetPixelAlpha(int x, int y) => GetPixelColor(x, y).A / 255f;

    public bool ScalePixels(SKPixmap destination) =>
        ScalePixels(destination, SKSamplingOptions.Default);

#pragma warning disable CS0619
    [Obsolete("Use ScalePixels(SKPixmap destination, SKSamplingOptions sampling) instead.", true)]
    public bool ScalePixels(SKPixmap destination, SKFilterQuality quality) =>
        ScalePixels(destination, (int)quality switch
        {
            0 => SKSamplingOptions.Default,
            1 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            2 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            3 => new SKSamplingOptions(SKCubicResampler.Mitchell),
            _ => SKSamplingOptions.Default,
        });
#pragma warning restore CS0619

    public bool ScalePixels(SKPixmap destination, SKSamplingOptions sampling)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_pixels == IntPtr.Zero || destination._pixels == IntPtr.Zero)
        {
            return false;
        }

        using var source = new SKBitmap();
        if (!source.InstallPixels(_info, _pixels, _rowBytes))
        {
            return false;
        }

        using var resized = source.Resize(destination.Info, sampling);
        if (resized is null)
        {
            return false;
        }

        using var resizedPixels = resized.PeekPixels();
        return resizedPixels.ReadPixels(destination);
    }

    public bool ReadPixels(SKImageInfo dstInfo, IntPtr dstPixels, int dstRowBytes) =>
        ReadPixels(dstInfo, dstPixels, dstRowBytes, 0, 0);

    public unsafe bool ReadPixels(
        SKImageInfo dstInfo,
        IntPtr dstPixels,
        int dstRowBytes,
        int srcX,
        int srcY)
    {
        if (_pixels == IntPtr.Zero || dstPixels == IntPtr.Zero || dstInfo.IsEmpty)
        {
            return false;
        }

        if (dstInfo.BytesPerPixel <= 0 || dstRowBytes < dstInfo.RowBytes)
        {
            return false;
        }

        var sourceLeft = Math.Max(0, srcX);
        var sourceTop = Math.Max(0, srcY);
        var sourceRight = Math.Min(Width, srcX + dstInfo.Width);
        var sourceBottom = Math.Min(Height, srcY + dstInfo.Height);
        if (sourceLeft >= sourceRight || sourceTop >= sourceBottom)
        {
            return false;
        }

        var destinationOffsetX = sourceLeft - srcX;
        var destinationOffsetY = sourceTop - srcY;
        var destinationBase = (byte*)dstPixels;
        for (var y = sourceTop; y < sourceBottom; y++)
        {
            var destinationRow = destinationBase + (destinationOffsetY + y - sourceTop) * dstRowBytes;
            for (var x = sourceLeft; x < sourceRight; x++)
            {
                if (!TryReadColor(GetPixels(x, y), _info, out var color))
                {
                    return false;
                }

                var destination = (IntPtr)(destinationRow +
                    (destinationOffsetX + x - sourceLeft) * dstInfo.BytesPerPixel);
                if (!TryWriteColor(destination, dstInfo, color))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public bool ReadPixels(SKPixmap pixmap) => ReadPixels(pixmap, 0, 0);

    public bool ReadPixels(SKPixmap pixmap, int srcX, int srcY)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        return ReadPixels(pixmap.Info, pixmap.GetPixels(), pixmap.RowBytes, srcX, srcY);
    }

    public SKData? Encode(SKEncodedImageFormat encoder, int quality)
    {
        using var stream = new SKDynamicMemoryWStream();
        return Encode(stream, encoder, quality) ? stream.DetachAsData() : null;
    }

    public bool Encode(Stream dst, SKEncodedImageFormat encoder, int quality)
    {
        ArgumentNullException.ThrowIfNull(dst);
        using var stream = new SKManagedWStream(dst);
        return Encode(stream, encoder, quality);
    }

    public bool Encode(SKWStream dst, SKEncodedImageFormat encoder, int quality)
    {
        ArgumentNullException.ThrowIfNull(dst);
        return encoder switch
        {
            SKEncodedImageFormat.Jpeg => Encode(dst, new SKJpegEncoderOptions(quality)),
            SKEncodedImageFormat.Png => Encode(dst, SKPngEncoderOptions.Default),
            SKEncodedImageFormat.Webp => Encode(
                dst,
                quality == 100
                    ? new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75f)
                    : new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, quality)),
            _ => false,
        };
    }

    public SKData? Encode(SKJpegEncoderOptions options) => EncodeToData(options);
    public SKData? Encode(SKPngEncoderOptions options) => EncodeToData(options);
    public SKData? Encode(SKWebpEncoderOptions options) => null;

    public bool Encode(Stream dst, SKJpegEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(dst);
        using var stream = new SKManagedWStream(dst);
        return Encode(stream, options);
    }

    public bool Encode(Stream dst, SKPngEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(dst);
        using var stream = new SKManagedWStream(dst);
        return Encode(stream, options);
    }

    public bool Encode(Stream dst, SKWebpEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(dst);
        return false;
    }

    public bool Encode(SKWStream dst, SKJpegEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(dst);
        var encoded = EncodeJpeg(options);
        return encoded is not null && dst.Write(encoded, encoded.Length);
    }

    public bool Encode(SKWStream dst, SKPngEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(dst);
        var encoded = EncodePng(options);
        return encoded is not null && dst.Write(encoded, encoded.Length);
    }

    public bool Encode(SKWStream dst, SKWebpEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(dst);
        return false;
    }

    public SKPixmap? ExtractSubset(SKRectI subset)
    {
        var result = new SKPixmap();
        if (ExtractSubset(result, subset))
        {
            return result;
        }

        result.Dispose();
        return null;
    }

    public bool ExtractSubset(SKPixmap result, SKRectI subset)
    {
        ArgumentNullException.ThrowIfNull(result);
        var left = Math.Clamp(subset.Left, 0, Width);
        var top = Math.Clamp(subset.Top, 0, Height);
        var right = Math.Clamp(subset.Right, 0, Width);
        var bottom = Math.Clamp(subset.Bottom, 0, Height);
        if (left >= right || top >= bottom || _pixels == IntPtr.Zero)
        {
            result.Reset();
            return false;
        }

        var info = new SKImageInfo(
            right - left,
            bottom - top,
            ColorType,
            AlphaType,
            ColorSpace);
        result.Reset(info, GetPixels(left, top), RowBytes);
        result.SetPixelSource(_pixelSource ?? this);
        return true;
    }

    public bool Erase(SKColor color) => Erase(color, Rect);

    public bool Erase(SKColor color, SKRectI subset)
    {
        var left = Math.Clamp(subset.Left, 0, Width);
        var top = Math.Clamp(subset.Top, 0, Height);
        var right = Math.Clamp(subset.Right, 0, Width);
        var bottom = Math.Clamp(subset.Bottom, 0, Height);
        if (left >= right || top >= bottom || _pixels == IntPtr.Zero)
        {
            return false;
        }

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (!TryWriteColor(GetPixels(x, y), _info, color))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public bool Erase(SKColorF color) => Erase(color, Rect);

    public bool Erase(SKColorF color, SKRectI subset) =>
        Erase(
            new SKColor(
                FloatToByte(color.R),
                FloatToByte(color.G),
                FloatToByte(color.B),
                FloatToByte(color.A)),
            subset);

    public bool ComputeIsOpaque()
    {
        if (_pixels == IntPtr.Zero || Info.IsEmpty)
        {
            return false;
        }

        if (AlphaType == SKAlphaType.Opaque || ColorType is SKColorType.Rgb565 or SKColorType.Rgb888x)
        {
            return true;
        }

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (GetPixelColor(x, y).A != byte.MaxValue)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public SKPixmap WithColorType(SKColorType newColorType) =>
        CreateView(Info.WithColorType(newColorType));

    public SKPixmap WithColorSpace(SKColorSpace newColorSpace) =>
        CreateView(Info.WithColorSpace(newColorSpace));

    public SKPixmap WithAlphaType(SKAlphaType newAlphaType) =>
        CreateView(Info.WithAlphaType(newAlphaType));

    private SKPixmap CreateView(SKImageInfo info)
    {
        var result = new SKPixmap(info, _pixels, _rowBytes);
        result.SetPixelSource(_pixelSource ?? this);
        return result;
    }

    private SKData? EncodeToData(SKJpegEncoderOptions options)
    {
        var encoded = EncodeJpeg(options);
        return encoded is null ? null : new SKData(encoded);
    }

    private SKData? EncodeToData(SKPngEncoderOptions options)
    {
        var encoded = EncodePng(options);
        return encoded is null ? null : new SKData(encoded);
    }

    private byte[]? EncodeJpeg(SKJpegEncoderOptions options)
    {
        var pixels = CopyStraightRgbaPixels(options.AlphaOption == SKJpegEncoderAlphaOption.BlendOnBlack);
        if (pixels is null)
        {
            return null;
        }

        using var output = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        writer.WriteJpg(
            pixels,
            Width,
            Height,
            StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha,
            output,
            Math.Clamp(options.Quality, 1, 100));
        return output.ToArray();
    }

    private byte[]? EncodePng(SKPngEncoderOptions options)
    {
        var pixels = CopyStraightRgbaPixels(blendOnBlack: false);
        if (pixels is null)
        {
            return null;
        }

        using var output = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        writer.WritePng(
            pixels,
            Width,
            Height,
            StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha,
            output);
        return output.ToArray();
    }

    private byte[]? CopyStraightRgbaPixels(bool blendOnBlack)
    {
        if (_pixels == IntPtr.Zero || Width <= 0 || Height <= 0)
        {
            return null;
        }

        var result = GC.AllocateUninitializedArray<byte>(checked(Width * Height * 4));
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (!TryReadColor(GetPixels(x, y), _info, out var color))
                {
                    return null;
                }

                var offset = (y * Width + x) * 4;
                if (blendOnBlack)
                {
                    result[offset] = Premultiply(color.R, color.A);
                    result[offset + 1] = Premultiply(color.G, color.A);
                    result[offset + 2] = Premultiply(color.B, color.A);
                    result[offset + 3] = byte.MaxValue;
                }
                else
                {
                    result[offset] = color.R;
                    result[offset + 1] = color.G;
                    result[offset + 2] = color.B;
                    result[offset + 3] = color.A;
                }
            }
        }

        return result;
    }

    private static unsafe bool TryReadColor(IntPtr address, SKImageInfo info, out SKColor color)
    {
        color = SKColor.Empty;
        if (address == IntPtr.Zero)
        {
            return false;
        }

        var pixel = (byte*)address;
        byte red;
        byte green;
        byte blue;
        byte alpha;
        switch (info.ColorType)
        {
            case SKColorType.Rgba8888:
            case SKColorType.Srgba8888:
                red = pixel[0];
                green = pixel[1];
                blue = pixel[2];
                alpha = pixel[3];
                break;
            case SKColorType.Bgra8888:
                red = pixel[2];
                green = pixel[1];
                blue = pixel[0];
                alpha = pixel[3];
                break;
            case SKColorType.Rgb888x:
                red = pixel[0];
                green = pixel[1];
                blue = pixel[2];
                alpha = byte.MaxValue;
                break;
            case SKColorType.Rgb565:
                var packed565 = (ushort)(pixel[0] | (pixel[1] << 8));
                red = (byte)(((packed565 >> 11) & 0x1f) * 255 / 31);
                green = (byte)(((packed565 >> 5) & 0x3f) * 255 / 63);
                blue = (byte)((packed565 & 0x1f) * 255 / 31);
                alpha = byte.MaxValue;
                break;
            case SKColorType.Argb4444:
                var packed4444 = (ushort)(pixel[0] | (pixel[1] << 8));
                alpha = ExpandNibble((byte)(packed4444 >> 12));
                red = ExpandNibble((byte)(packed4444 >> 8));
                green = ExpandNibble((byte)(packed4444 >> 4));
                blue = ExpandNibble((byte)packed4444);
                break;
            case SKColorType.Alpha8:
                red = green = blue = 0;
                alpha = pixel[0];
                break;
            case SKColorType.Gray8:
                red = green = blue = pixel[0];
                alpha = byte.MaxValue;
                break;
            case SKColorType.R8Unorm:
                red = pixel[0];
                green = blue = 0;
                alpha = byte.MaxValue;
                break;
            case SKColorType.Rg88:
                red = pixel[0];
                green = pixel[1];
                blue = 0;
                alpha = byte.MaxValue;
                break;
            default:
                return false;
        }

        if (info.AlphaType == SKAlphaType.Opaque)
        {
            alpha = byte.MaxValue;
        }
        else if (info.AlphaType == SKAlphaType.Premul && alpha is > 0 and < byte.MaxValue)
        {
            red = Unpremultiply(red, alpha);
            green = Unpremultiply(green, alpha);
            blue = Unpremultiply(blue, alpha);
        }

        color = new SKColor(red, green, blue, alpha);
        return true;
    }

    private static unsafe bool TryWriteColor(IntPtr address, SKImageInfo info, SKColor color)
    {
        if (address == IntPtr.Zero)
        {
            return false;
        }

        var alpha = info.AlphaType == SKAlphaType.Opaque ? byte.MaxValue : color.A;
        var red = info.AlphaType == SKAlphaType.Premul ? Premultiply(color.R, alpha) : color.R;
        var green = info.AlphaType == SKAlphaType.Premul ? Premultiply(color.G, alpha) : color.G;
        var blue = info.AlphaType == SKAlphaType.Premul ? Premultiply(color.B, alpha) : color.B;
        var pixel = (byte*)address;
        switch (info.ColorType)
        {
            case SKColorType.Rgba8888:
            case SKColorType.Srgba8888:
                pixel[0] = red;
                pixel[1] = green;
                pixel[2] = blue;
                pixel[3] = alpha;
                return true;
            case SKColorType.Bgra8888:
                pixel[0] = blue;
                pixel[1] = green;
                pixel[2] = red;
                pixel[3] = alpha;
                return true;
            case SKColorType.Rgb888x:
                pixel[0] = color.R;
                pixel[1] = color.G;
                pixel[2] = color.B;
                pixel[3] = byte.MaxValue;
                return true;
            case SKColorType.Rgb565:
                var packed565 = PackRgb565(color.R, color.G, color.B);
                pixel[0] = (byte)packed565;
                pixel[1] = (byte)(packed565 >> 8);
                return true;
            case SKColorType.Argb4444:
                var packed4444 = (ushort)(
                    ((alpha >> 4) << 12) |
                    ((red >> 4) << 8) |
                    ((green >> 4) << 4) |
                    (blue >> 4));
                pixel[0] = (byte)packed4444;
                pixel[1] = (byte)(packed4444 >> 8);
                return true;
            case SKColorType.Alpha8:
                pixel[0] = alpha;
                return true;
            case SKColorType.Gray8:
                pixel[0] = (byte)((color.R * 54 + color.G * 183 + color.B * 19 + 128) >> 8);
                return true;
            case SKColorType.R8Unorm:
                pixel[0] = color.R;
                return true;
            case SKColorType.Rg88:
                pixel[0] = color.R;
                pixel[1] = color.G;
                return true;
            default:
                return false;
        }
    }

    private static byte ExpandNibble(byte value) => (byte)((value & 0x0f) * 17);
    private static byte FloatToByte(float value) =>
        (byte)Math.Clamp(MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0f, 255f);

    private static ushort PackRgb565(byte red, byte green, byte blue) =>
        (ushort)(
            ((red * 31 + 127) / 255 << 11) |
            ((green * 63 + 127) / 255 << 5) |
            ((blue * 31 + 127) / 255));

    private static byte Premultiply(byte color, byte alpha) =>
        (byte)((color * alpha + 127) / 255);

    private static byte Unpremultiply(byte color, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, (color * 255 + alpha / 2) / alpha);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Reset();
        }

        base.Dispose(disposing);
    }

    private void ValidateCoordinates(int x, int y)
    {
        if ((uint)x >= (uint)_info.Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)_info.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }
    }
}

public class SKBitmap : SKObject
{
    private readonly object _canvasSync = new();
    private WeakReference<SKCanvas>? _attachedCanvas;
    private IntPtr _pixels;
    private bool _ownsPixels;
    private int _width;
    private int _height;
    private int _rowBytes;
    private SKImageInfo _info;
    private SKBitmapReleaseDelegate? _releaseDelegate;
    private object? _releaseContext;
    private object? _pixelOwner;
    private bool _isImmutable;

    public int Width => _width;
    public int Height => _height;
    public SKImageInfo Info => _info;
    public SKColorType ColorType => _info.ColorType;
    public SKAlphaType AlphaType => _info.AlphaType;
    public SKColorSpace? ColorSpace => _info.ColorSpace;
    public int BytesPerPixel => _info.BytesPerPixel;
    public int RowBytes => _rowBytes;
    public int ByteCount => ComputeByteCount(_info, _rowBytes);
    public byte[] Bytes => GetPixelSpan().ToArray();
    public SKColor[] Pixels
    {
        get
        {
            var pixels = new SKColor[checked(_width * _height)];
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    pixels[y * _width + x] = GetPixel(x, y);
                }
            }

            return pixels;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var expected = (long)_width * _height;
            if (value.LongLength != expected)
            {
                throw new ArgumentException(
                    $"The number of pixels must equal Width x Height, or {expected}.",
                    nameof(value));
            }

            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    SetPixel(x, y, value[y * _width + x]);
                }
            }
        }
    }
    public bool ReadyToDraw => !IsEmpty && !IsNull;
    public bool IsEmpty => _info.IsEmpty;
    public bool IsNull => _pixels == IntPtr.Zero;
    public bool DrawsNothing => IsEmpty || IsNull;
    public bool IsImmutable => _isImmutable;

    public SKBitmap()
        : base(SKObjectHandle.Create(), owns: true)
    {
        _pixels = IntPtr.Zero;
        _ownsPixels = false;
        _rowBytes = 0;
    }

    public SKBitmap(int width, int height, bool isOpaque = false)
        : this(new SKImageInfo(
            width,
            height,
            SKImageInfo.PlatformColorType,
            isOpaque ? SKAlphaType.Opaque : SKAlphaType.Premul))
    {
    }

    public SKBitmap(SKImageInfo info)
        : this(info, info.RowBytes)
    {
    }

    public SKBitmap(int width, int height, SKColorType colorType, SKAlphaType alphaType)
        : this(new SKImageInfo(width, height, colorType, alphaType))
    {
    }

    public SKBitmap(
        int width,
        int height,
        SKColorType colorType,
        SKAlphaType alphaType,
        SKColorSpace? colorspace)
        : this(new SKImageInfo(width, height, colorType, alphaType, colorspace))
    {
    }

    public SKBitmap(SKImageInfo info, int rowBytes)
        : this()
    {
        if (!TryAllocPixels(info, rowBytes))
        {
            throw new Exception("Unable to allocate pixels for the bitmap.");
        }
    }

    public SKBitmap(SKImageInfo info, SKBitmapAllocFlags flags)
        : this()
    {
        if (!TryAllocPixels(info, flags))
        {
            throw new Exception("Unable to allocate pixels for the bitmap.");
        }
    }

    internal void AttachCanvas(SKCanvas canvas)
    {
        SKCanvas? previous = null;
        lock (_canvasSync)
        {
            if (_attachedCanvas?.TryGetTarget(out var attached) == true && !ReferenceEquals(attached, canvas))
            {
                previous = attached;
            }

            _attachedCanvas = new WeakReference<SKCanvas>(canvas);
        }

        previous?.Flush();
    }

    internal void DetachCanvas(SKCanvas canvas)
    {
        lock (_canvasSync)
        {
            if (_attachedCanvas?.TryGetTarget(out var attached) == true && ReferenceEquals(attached, canvas))
            {
                _attachedCanvas = null;
            }
        }
    }

    private void FlushAttachedCanvas()
    {
        SKCanvas? canvas = null;
        lock (_canvasSync)
        {
            _attachedCanvas?.TryGetTarget(out canvas);
        }

        canvas?.Flush();
    }

    public bool TryAllocPixels(SKImageInfo info) => TryAllocPixels(info, info.RowBytes);

    public bool TryAllocPixels(SKImageInfo info, SKBitmapAllocFlags flags)
    {
        if (!TryAllocPixels(info, info.RowBytes))
        {
            return false;
        }

        if ((flags & SKBitmapAllocFlags.ZeroPixels) != 0 && _pixels != IntPtr.Zero)
        {
            GetPixelSpan().Clear();
        }

        return true;
    }

    public bool TryAllocPixels(SKImageInfo info, int rowBytes)
    {
        if (!TryValidateStorage(info, rowBytes, out int byteCount))
        {
            return false;
        }

        FlushAttachedCanvas();
        ReleasePixels();
        SetMetadata(info, rowBytes);
        _isImmutable = false;
        if (byteCount == 0)
        {
            return true;
        }

        try
        {
            _pixels = Marshal.AllocHGlobal(byteCount);
            _ownsPixels = true;
            GetPixelSpan().Clear();
            return true;
        }
        catch (OutOfMemoryException)
        {
            _pixels = IntPtr.Zero;
            _ownsPixels = false;
            return false;
        }
    }

    public void Reset()
    {
        FlushAttachedCanvas();
        ReleasePixels();
        _info = default;
        _width = 0;
        _height = 0;
        _rowBytes = 0;
        _isImmutable = false;
    }

    public IntPtr GetPixels() => GetPixels(out _);

    public IntPtr GetPixels(out IntPtr length)
    {
        length = (IntPtr)ByteCount;
        return _pixels;
    }

    public unsafe Span<byte> GetPixelSpan() =>
        _pixels == IntPtr.Zero || ByteCount == 0
            ? Span<byte>.Empty
            : new Span<byte>(_pixels.ToPointer(), ByteCount);

    public Span<byte> GetPixelSpan(int x, int y)
    {
        if (_info.IsEmpty || _info.BytesPerPixel <= 0)
        {
            return GetPixelSpan();
        }

        if ((uint)x >= (uint)_width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)_height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        int offset = checked(y * RowBytes + x * _info.BytesPerPixel);
        return GetPixelSpan().Slice(offset);
    }

    public void SetPixels(IntPtr pixels)
    {
        FlushAttachedCanvas();
        ReleasePixels();
        _pixels = pixels;
        _ownsPixels = false;
        _isImmutable = false;
    }

    public IntPtr GetAddress(int x, int y)
    {
        if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Pixel coordinates must be inside the bitmap.");
        }

        return IntPtr.Add(_pixels, checked(y * RowBytes + x * _info.BytesPerPixel));
    }

    public SKColor GetPixel(int x, int y)
    {
        var address = GetAddress(x, y);
        unsafe
        {
            var pixel = (byte*)address;
            if (ColorType == SKColorType.Rgb565)
            {
                ushort value = (ushort)(pixel[0] | (pixel[1] << 8));
                return new SKColor(
                    (byte)(((value >> 11) & 0x1f) * 255 / 31),
                    (byte)(((value >> 5) & 0x3f) * 255 / 63),
                    (byte)((value & 0x1f) * 255 / 31),
                    255);
            }

            var alpha = pixel[3];
            var red = ColorType == SKColorType.Bgra8888 ? pixel[2] : pixel[0];
            var green = pixel[1];
            var blue = ColorType == SKColorType.Bgra8888 ? pixel[0] : pixel[2];
            if (AlphaType == SKAlphaType.Premul && alpha is > 0 and < 255)
            {
                red = Unpremultiply(red, alpha);
                green = Unpremultiply(green, alpha);
                blue = Unpremultiply(blue, alpha);
            }

            return new SKColor(red, green, blue, alpha);
        }
    }

    public void SetPixel(int x, int y, SKColor color)
    {
        var address = GetAddress(x, y);
        unsafe
        {
            var pixel = (byte*)address;
            var alpha = AlphaType == SKAlphaType.Opaque ? (byte)255 : color.A;
            var red = AlphaType == SKAlphaType.Premul ? Premultiply(color.R, alpha) : color.R;
            var green = AlphaType == SKAlphaType.Premul ? Premultiply(color.G, alpha) : color.G;
            var blue = AlphaType == SKAlphaType.Premul ? Premultiply(color.B, alpha) : color.B;
            if (ColorType == SKColorType.Rgb565)
            {
                var value = PackRgb565(red, green, blue);
                pixel[0] = (byte)value;
                pixel[1] = (byte)(value >> 8);
                return;
            }

            if (ColorType == SKColorType.Bgra8888)
            {
                pixel[0] = blue;
                pixel[1] = green;
                pixel[2] = red;
            }
            else
            {
                pixel[0] = red;
                pixel[1] = green;
                pixel[2] = blue;
            }

            pixel[3] = alpha;
        }
    }

    public bool InstallPixels(SKImageInfo info, IntPtr pixels) =>
        InstallPixels(info, pixels, info.RowBytes, null!, null!);

    public bool InstallPixels(SKImageInfo info, IntPtr pixels, int rowBytes) =>
        InstallPixels(info, pixels, rowBytes, null!, null!);

    public bool InstallPixels(
        SKImageInfo info,
        IntPtr pixels,
        int rowBytes,
        SKBitmapReleaseDelegate releaseProc) =>
        InstallPixels(info, pixels, rowBytes, releaseProc, null!);

    public bool InstallPixels(
        SKImageInfo info,
        IntPtr pixels,
        int rowBytes,
        SKBitmapReleaseDelegate releaseProc,
        object context)
    {
        if (!TryValidateStorage(info, rowBytes, out _))
        {
            return false;
        }

        FlushAttachedCanvas();
        ReleasePixels();
        SetMetadata(info, rowBytes);
        _pixels = pixels;
        _ownsPixels = false;
        _releaseDelegate = releaseProc;
        _releaseContext = context;
        _isImmutable = false;
        return true;
    }

    public bool InstallPixels(SKPixmap pixmap)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        if (!InstallPixels(pixmap.Info, pixmap.GetPixels(), pixmap.RowBytes))
        {
            return false;
        }

        _pixelOwner = pixmap.PixelSource ?? pixmap;
        return true;
    }

    public void Erase(SKColor color)
    {
        if (_pixels == IntPtr.Zero || _width <= 0 || _height <= 0)
        {
            return;
        }

        var alpha = color.A;
        var red = _info.AlphaType == SKAlphaType.Premul ? Premultiply(color.R, alpha) : color.R;
        var green = _info.AlphaType == SKAlphaType.Premul ? Premultiply(color.G, alpha) : color.G;
        var blue = _info.AlphaType == SKAlphaType.Premul ? Premultiply(color.B, alpha) : color.B;
        unsafe
        {
            var destination = (byte*)_pixels;
            for (var y = 0; y < _height; y++)
            {
                var row = destination + y * RowBytes;
                for (var x = 0; x < _width; x++)
                {
                    if (_info.ColorType == SKColorType.Rgb565)
                    {
                        var pixel565 = row + x * 2;
                        ushort value = PackRgb565(red, green, blue);
                        pixel565[0] = (byte)value;
                        pixel565[1] = (byte)(value >> 8);
                        continue;
                    }

                    var pixel = row + x * 4;
                    if (_info.ColorType == SKColorType.Bgra8888)
                    {
                        pixel[0] = blue;
                        pixel[1] = green;
                        pixel[2] = red;
                    }
                    else
                    {
                        pixel[0] = red;
                        pixel[1] = green;
                        pixel[2] = blue;
                    }

                    pixel[3] = alpha;
                }
            }
        }
    }

    public void Erase(SKColor color, SKRectI rect)
    {
        using var pixmap = PeekPixels();
        pixmap?.Erase(color, rect);
    }

    public void NotifyPixelsChanged() { }

    public SKBitmap Copy() => Copy(ColorType);

    public SKBitmap Copy(SKColorType colorType)
    {
        var copy = new SKBitmap();
        if (CopyTo(copy, colorType))
        {
            return copy;
        }

        copy.Dispose();
        return null!;
    }

    public bool CopyTo(SKBitmap destination) => CopyTo(destination, ColorType);

    public bool CopyTo(SKBitmap destination, SKColorType colorType)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!CanCopyTo(colorType))
        {
            return false;
        }

        using var source = PeekPixels();
        if (source is null)
        {
            return false;
        }

        using var converted = new SKBitmap();
        var info = Info.WithColorType(colorType);
        if (!converted.TryAllocPixels(info))
        {
            return false;
        }

        using var convertedPixels = converted.PeekPixels();
        if (!source.ReadPixels(convertedPixels))
        {
            return false;
        }

        destination.SwapStorage(converted);
        return true;
    }

    public void SetImmutable() => _isImmutable = true;

    public bool CanCopyTo(SKColorType type) => type is
        SKColorType.Alpha8 or
        SKColorType.Rgb565 or
        SKColorType.Argb4444 or
        SKColorType.Rgba8888 or
        SKColorType.Rgb888x or
        SKColorType.Bgra8888 or
        SKColorType.Gray8 or
        SKColorType.Rg88 or
        SKColorType.Srgba8888 or
        SKColorType.R8Unorm;

    public bool ExtractSubset(SKBitmap destination, SKRectI subset)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var source = PeekPixels();
        if (source is null)
        {
            return false;
        }

        using var view = source.ExtractSubset(subset);
        if (view is null)
        {
            return false;
        }

        using var extracted = new SKBitmap();
        if (!extracted.InstallPixels(view.Info, view.GetPixels(), view.RowBytes))
        {
            return false;
        }

        extracted._pixelOwner = view.PixelSource ?? this;
        destination.SwapStorage(extracted);
        return true;
    }

    public bool ExtractAlpha(SKBitmap destination)
    {
        return ExtractAlpha(destination, null!, out _);
    }

    public bool ExtractAlpha(SKBitmap destination, out SKPointI offset)
    {
        return ExtractAlpha(destination, null!, out offset);
    }

    public bool ExtractAlpha(SKBitmap destination, SKPaint paint)
    {
        return ExtractAlpha(destination, paint, out _);
    }

    public bool ExtractAlpha(SKBitmap destination, SKPaint paint, out SKPointI offset)
    {
        ArgumentNullException.ThrowIfNull(destination);
        offset = new SKPointI(0, 0);
        using var source = PeekPixels();
        if (source is null)
        {
            return false;
        }

        var alphaInfo = new SKImageInfo(
            Width,
            Height,
            SKColorType.Alpha8,
            SKAlphaType.Unpremul);
        using var alpha = new SKBitmap(alphaInfo, checked((alphaInfo.RowBytes + 3) & ~3));
        var destinationPixels = alpha.GetPixelSpan();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                destinationPixels[y * alpha.RowBytes + x] =
                    (byte)Math.Clamp(
                        MathF.Round(source.GetPixelAlpha(x, y) * 255f),
                        0f,
                        255f);
            }
        }

        destination.SwapStorage(alpha);
        return true;
    }

    public static SKBitmap Decode(SKData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var codec = SKCodec.Create(data);
        return codec is null ? null! : Decode(codec);
    }

    public static SKBitmap Decode(SKData data, SKImageInfo bitmapInfo)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var codec = SKCodec.Create(data);
        return codec is null ? null! : Decode(codec, bitmapInfo);
    }

    public static SKBitmap FromImage(SKImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var info = new SKImageInfo(
            image.Width,
            image.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            image.ColorSpace);
        var bitmap = new SKBitmap(info);
        image.ReadPixels(
            info,
            bitmap.GetPixels(),
            bitmap.RowBytes,
            0,
            0,
            SKImageCachingHint.Allow);
        return bitmap;
    }

    public static SKBitmap Decode(Stream? stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var codec = SKCodec.Create(stream);
        return codec is null ? null! : Decode(codec);
    }

    public static SKBitmap Decode(Stream? stream, SKImageInfo bitmapInfo)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var codec = SKCodec.Create(stream);
        return codec is null ? null! : Decode(codec, bitmapInfo);
    }

    public static SKBitmap Decode(SKStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var codec = SKCodec.Create(stream);
        return codec is null ? null! : Decode(codec);
    }

    public static SKBitmap Decode(SKStream stream, SKImageInfo bitmapInfo)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var codec = SKCodec.Create(stream);
        return codec is null ? null! : Decode(codec, bitmapInfo);
    }

    public static SKBitmap Decode(SKCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var info = codec.Info;
        if (info.AlphaType == SKAlphaType.Unpremul)
        {
            info.AlphaType = SKAlphaType.Premul;
        }

        return Decode(codec, info);
    }

    public static SKBitmap Decode(SKCodec codec, SKImageInfo bitmapInfo)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (bitmapInfo.IsEmpty || !SupportsResizeColorType(bitmapInfo.ColorType))
        {
            return null!;
        }

        var result = codec.DecodedImage;
        var bitmap = new SKBitmap(bitmapInfo);

        unsafe
        {
            fixed (byte* src = result.Pixels)
            {
                byte* dst = (byte*)bitmap.GetPixels();
                for (int y = 0; y < bitmapInfo.Height; y++)
                {
                    int srcY = bitmapInfo.Height == result.Height
                        ? y
                        : Math.Clamp((int)((long)y * result.Height / bitmapInfo.Height), 0, result.Height - 1);
                    byte* dstRow = dst + y * bitmap.RowBytes;

                    for (int x = 0; x < bitmapInfo.Width; x++)
                    {
                        int srcX = bitmapInfo.Width == result.Width
                            ? x
                            : Math.Clamp((int)((long)x * result.Width / bitmapInfo.Width), 0, result.Width - 1);
                        byte* srcPixel = src + (srcY * result.Width + srcX) * 4;
                        byte* dstPixel = dstRow + x * bitmapInfo.BytesPerPixel;
                        byte alpha = bitmapInfo.AlphaType == SKAlphaType.Opaque || bitmapInfo.ColorType == SKColorType.Rgb888x
                            ? (byte)255
                            : srcPixel[3];
                        byte red = bitmapInfo.AlphaType == SKAlphaType.Premul ? Premultiply(srcPixel[0], alpha) : srcPixel[0];
                        byte green = bitmapInfo.AlphaType == SKAlphaType.Premul ? Premultiply(srcPixel[1], alpha) : srcPixel[1];
                        byte blue = bitmapInfo.AlphaType == SKAlphaType.Premul ? Premultiply(srcPixel[2], alpha) : srcPixel[2];

                        if (bitmapInfo.ColorType == SKColorType.Rgb565)
                        {
                            ushort value = PackRgb565(red, green, blue);
                            dstPixel[0] = (byte)value;
                            dstPixel[1] = (byte)(value >> 8);
                        }
                        else if (bitmapInfo.ColorType == SKColorType.Bgra8888)
                        {
                            dstPixel[0] = blue;
                            dstPixel[1] = green;
                            dstPixel[2] = red;
                            dstPixel[3] = alpha;
                        }
                        else
                        {
                            dstPixel[0] = red;
                            dstPixel[1] = green;
                            dstPixel[2] = blue;
                            dstPixel[3] = alpha;
                        }
                    }
                }
            }
        }

        return bitmap;
    }

    public static SKBitmap Decode(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Decode(buffer.AsSpan());
    }

    public static SKBitmap Decode(byte[] buffer, SKImageInfo bitmapInfo)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Decode(buffer.AsSpan(), bitmapInfo);
    }

    public static SKBitmap Decode(ReadOnlySpan<byte> buffer)
    {
        using var data = SKData.CreateCopy(buffer);
        using var codec = SKCodec.Create(data);
        return Decode(codec);
    }

    public static SKBitmap Decode(ReadOnlySpan<byte> buffer, SKImageInfo bitmapInfo)
    {
        using var data = SKData.CreateCopy(buffer);
        using var codec = SKCodec.Create(data);
        return Decode(codec, bitmapInfo);
    }

    public static SKBitmap Decode(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        using var codec = SKCodec.Create(filename);
        return codec is null ? null! : Decode(codec);
    }

    public static SKBitmap Decode(string filename, SKImageInfo bitmapInfo)
    {
        ArgumentNullException.ThrowIfNull(filename);
        using var codec = SKCodec.Create(filename);
        return codec is null ? null! : Decode(codec, bitmapInfo);
    }

    public static SKImageInfo DecodeBounds(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var codec = SKCodec.Create(stream);
        return codec?.Info ?? SKImageInfo.Empty;
    }

    public static SKImageInfo DecodeBounds(SKStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var codec = SKCodec.Create(stream);
        return codec?.Info ?? SKImageInfo.Empty;
    }

    public static SKImageInfo DecodeBounds(SKData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var codec = SKCodec.Create(data);
        return codec?.Info ?? SKImageInfo.Empty;
    }

    public static SKImageInfo DecodeBounds(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        using var codec = SKCodec.Create(filename);
        return codec?.Info ?? SKImageInfo.Empty;
    }

    public static SKImageInfo DecodeBounds(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return DecodeBounds(buffer.AsSpan());
    }

    public static SKImageInfo DecodeBounds(ReadOnlySpan<byte> buffer)
    {
        using var data = SKData.CreateCopy(buffer);
        using var codec = SKCodec.Create(data);
        return codec?.Info ?? SKImageInfo.Empty;
    }

    public SKPixmap PeekPixels()
    {
        var pixmap = new SKPixmap();
        if (PeekPixels(pixmap))
        {
            return pixmap;
        }

        pixmap.Dispose();
        return null!;
    }

    public bool PeekPixels(SKPixmap pixmap)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        if (!ReadyToDraw)
        {
            pixmap.Reset();
            return false;
        }

        pixmap.Reset(_info, _pixels, _rowBytes);
        pixmap.SetPixelSource(this);
        return true;
    }

#pragma warning disable CS0619
    [Obsolete("Use Resize(SKImageInfo info, SKSamplingOptions sampling) instead.", true)]
    public SKBitmap Resize(SKImageInfo info, SKFilterQuality quality) =>
        Resize(info, SamplingFromQuality((int)quality));

    [Obsolete("Use Resize(SKSizeI size, SKSamplingOptions sampling) instead.", true)]
    public SKBitmap Resize(SKSizeI size, SKFilterQuality quality) =>
        Resize(size, SamplingFromQuality((int)quality));
#pragma warning restore CS0619

    public SKBitmap Resize(SKImageInfo info, SKSamplingOptions sampling)
    {
        FlushAttachedCanvas();
        if (_pixels == IntPtr.Zero
            || _width <= 0
            || _height <= 0
            || info.Width <= 0
            || info.Height <= 0
            || !SupportsResizeColorType(ColorType)
            || !SupportsResizeColorType(info.ColorType)
            || (sampling.UseCubic
                && (!float.IsFinite(sampling.Cubic.B)
                    || !float.IsFinite(sampling.Cubic.C))))
        {
            return null!;
        }

        var resized = new SKBitmap(info);
        if (!sampling.UseCubic && !sampling.IsAniso && sampling.Filter != SKFilterMode.Linear)
        {
            ResizeNearestPixels(resized, info);
            return resized;
        }

        var source = CopyResizeSourcePixels();
        var sourceScaleX = (float)_width / info.Width;
        var sourceScaleY = (float)_height / info.Height;
        unsafe
        {
            var destination = (byte*)resized.GetPixels();
            for (var y = 0; y < info.Height; y++)
            {
                var sourceY = ((y + 0.5f) * sourceScaleY) - 0.5f;
                var destinationRow = destination + y * resized.RowBytes;
                for (var x = 0; x < info.Width; x++)
                {
                    var sourceX = ((x + 0.5f) * sourceScaleX) - 0.5f;
                    var color = SampleResizePixel(
                        source,
                        _width,
                        _height,
                        sourceX,
                        sourceY,
                        sampling);
                    WriteResizePixel(
                        destinationRow + x * info.BytesPerPixel,
                        info,
                        color,
                        AlphaType == SKAlphaType.Premul);
                }
            }
        }

        return resized;
    }

    public SKBitmap Resize(SKSizeI size, SKSamplingOptions sampling) =>
        Resize(Info.WithSize(size), sampling);

#pragma warning disable CS0619
    [Obsolete("Use ScalePixels(SKBitmap destination, SKSamplingOptions sampling) instead.", true)]
    public bool ScalePixels(SKBitmap destination, SKFilterQuality quality) =>
        ScalePixels(destination, SamplingFromQuality((int)quality));

    [Obsolete("Use ScalePixels(SKPixmap destination, SKSamplingOptions sampling) instead.", true)]
    public bool ScalePixels(SKPixmap destination, SKFilterQuality quality) =>
        ScalePixels(destination, SamplingFromQuality((int)quality));
#pragma warning restore CS0619

    public bool ScalePixels(SKBitmap destination, SKSamplingOptions sampling)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var pixmap = destination.PeekPixels();
        return pixmap is not null && ScalePixels(pixmap, sampling);
    }

    public bool ScalePixels(SKPixmap destination, SKSamplingOptions sampling)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var source = PeekPixels();
        return source is not null && source.ScalePixels(destination, sampling);
    }

    private static SKSamplingOptions SamplingFromQuality(int quality) => quality switch
    {
        0 => SKSamplingOptions.Default,
        1 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
        2 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
        3 => new SKSamplingOptions(SKCubicResampler.Mitchell),
        _ => SKSamplingOptions.Default,
    };

    private static bool SupportsResizeColorType(SKColorType colorType) =>
        colorType is SKColorType.Rgb565
            or SKColorType.Rgba8888
            or SKColorType.Rgb888x
            or SKColorType.Bgra8888;

    private unsafe Vector4[] CopyResizeSourcePixels()
    {
        var result = new Vector4[checked(_width * _height)];
        var source = (byte*)_pixels;
        for (var y = 0; y < _height; y++)
        {
            var sourceRow = source + y * RowBytes;
            for (var x = 0; x < _width; x++)
            {
                var pixel = sourceRow + x * _info.BytesPerPixel;
                result[y * _width + x] = ReadResizeSourcePixel(pixel);
            }
        }

        return result;
    }

    private unsafe void ResizeNearestPixels(SKBitmap resized, SKImageInfo info)
    {
        var source = (byte*)_pixels;
        var destination = (byte*)resized.GetPixels();
        var sourceScaleX = (float)_width / info.Width;
        var sourceScaleY = (float)_height / info.Height;
        var canCopyStoredPixel = ColorType == info.ColorType
            && AlphaType == info.AlphaType
            && (AlphaType != SKAlphaType.Opaque || ColorType == SKColorType.Rgb565);
        for (var y = 0; y < info.Height; y++)
        {
            var sourceY = Math.Clamp(
                (int)MathF.Ceiling(((y + 0.5f) * sourceScaleY) - 1f),
                0,
                _height - 1);
            var sourceRow = source + sourceY * RowBytes;
            var destinationRow = destination + y * resized.RowBytes;
            for (var x = 0; x < info.Width; x++)
            {
                var sourceX = Math.Clamp(
                    (int)MathF.Ceiling(((x + 0.5f) * sourceScaleX) - 1f),
                    0,
                    _width - 1);
                var sourcePixel = sourceRow + sourceX * _info.BytesPerPixel;
                var destinationPixel = destinationRow + x * info.BytesPerPixel;
                if (canCopyStoredPixel)
                {
                    System.Buffer.MemoryCopy(
                        sourcePixel,
                        destinationPixel,
                        info.BytesPerPixel,
                        info.BytesPerPixel);
                    continue;
                }

                var color = ReadResizeSourcePixel(sourcePixel);
                WriteResizePixel(
                    destinationPixel,
                    info,
                    color,
                    AlphaType == SKAlphaType.Premul);
            }
        }
    }

    private unsafe Vector4 ReadResizeSourcePixel(byte* pixel)
    {
        if (ColorType == SKColorType.Rgb565)
        {
            var packed = (ushort)(pixel[0] | (pixel[1] << 8));
            return new Vector4(
                ((packed >> 11) & 0x1f) / 31f,
                ((packed >> 5) & 0x3f) / 63f,
                (packed & 0x1f) / 31f,
                1f);
        }

        var alpha = AlphaType == SKAlphaType.Opaque || ColorType == SKColorType.Rgb888x
            ? 1f
            : pixel[3] / 255f;
        return ColorType == SKColorType.Bgra8888
            ? new Vector4(pixel[2] / 255f, pixel[1] / 255f, pixel[0] / 255f, alpha)
            : new Vector4(pixel[0] / 255f, pixel[1] / 255f, pixel[2] / 255f, alpha);
    }

    private static Vector4 SampleResizePixel(
        Vector4[] source,
        int width,
        int height,
        float x,
        float y,
        SKSamplingOptions sampling)
    {
        if (sampling.UseCubic)
        {
            return SampleResizeCubic(source, width, height, x, y, sampling.Cubic);
        }

        if (sampling.IsAniso || sampling.Filter == SKFilterMode.Linear)
        {
            var x0 = (int)MathF.Floor(x);
            var y0 = (int)MathF.Floor(y);
            var fractionX = x - x0;
            var fractionY = y - y0;
            var top = Vector4.Lerp(
                GetResizePixel(source, width, height, x0, y0),
                GetResizePixel(source, width, height, x0 + 1, y0),
                fractionX);
            var bottom = Vector4.Lerp(
                GetResizePixel(source, width, height, x0, y0 + 1),
                GetResizePixel(source, width, height, x0 + 1, y0 + 1),
                fractionX);
            return Vector4.Lerp(top, bottom, fractionY);
        }

        var nearestX = (int)MathF.Ceiling(x - 0.5f);
        var nearestY = (int)MathF.Ceiling(y - 0.5f);
        return GetResizePixel(source, width, height, nearestX, nearestY);
    }

    private static Vector4 SampleResizeCubic(
        Vector4[] source,
        int width,
        int height,
        float x,
        float y,
        SKCubicResampler resampler)
    {
        var baseX = (int)MathF.Floor(x);
        var baseY = (int)MathF.Floor(y);
        var fractionX = x - baseX;
        var fractionY = y - baseY;
        var color = Vector4.Zero;
        var totalWeight = 0f;
        for (var tapY = -1; tapY <= 2; tapY++)
        {
            var weightY = ResizeCubicWeight(fractionY - tapY, resampler.B, resampler.C);
            for (var tapX = -1; tapX <= 2; tapX++)
            {
                var weight = ResizeCubicWeight(fractionX - tapX, resampler.B, resampler.C) * weightY;
                color += GetResizePixel(source, width, height, baseX + tapX, baseY + tapY) * weight;
                totalWeight += weight;
            }
        }

        return MathF.Abs(totalWeight) > 0.0001f
            ? color / totalWeight
            : GetResizePixel(source, width, height, baseX, baseY);
    }

    private static float ResizeCubicWeight(float value, float b, float c)
    {
        var x = MathF.Abs(value);
        var x2 = x * x;
        var x3 = x2 * x;
        if (b == 0f && c == 0.5f)
        {
            const float a = -0.5f;
            if (x <= 1f)
            {
                return ((a + 2f) * x3) - ((a + 3f) * x2) + 1f;
            }

            return x < 2f
                ? (a * x3) - (5f * a * x2) + (8f * a * x) - (4f * a)
                : 0f;
        }

        if (x <= 1f)
        {
            return ((12f - 9f * b - 6f * c) * x3
                + (-18f + 12f * b + 6f * c) * x2
                + (6f - 2f * b)) / 6f;
        }

        return x < 2f
            ? ((-b - 6f * c) * x3
                + (6f * b + 30f * c) * x2
                + (-12f * b - 48f * c) * x
                + (8f * b + 24f * c)) / 6f
            : 0f;
    }

    private static Vector4 GetResizePixel(Vector4[] source, int width, int height, int x, int y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        return source[y * width + x];
    }

    private static unsafe void WriteResizePixel(
        byte* destination,
        SKImageInfo info,
        Vector4 color,
        bool sourceIsPremultiplied)
    {
        var alpha = Math.Clamp(color.W, 0f, 1f);
        var targetHasAlpha = (info.ColorType is SKColorType.Rgba8888 or SKColorType.Bgra8888)
            && info.AlphaType != SKAlphaType.Opaque;
        var targetIsPremultiplied = targetHasAlpha && info.AlphaType == SKAlphaType.Premul;
        var rgb = new Vector3(color.X, color.Y, color.Z);
        if (sourceIsPremultiplied && !targetIsPremultiplied)
        {
            rgb = alpha > 0f ? rgb / alpha : Vector3.Zero;
        }
        else if (!sourceIsPremultiplied && targetIsPremultiplied)
        {
            rgb *= alpha;
        }

        var red = ResizeChannelToByte(rgb.X);
        var green = ResizeChannelToByte(rgb.Y);
        var blue = ResizeChannelToByte(rgb.Z);
        var alphaByte = targetHasAlpha ? ResizeChannelToByte(alpha) : (byte)255;
        if (info.ColorType == SKColorType.Rgb565)
        {
            var packed = PackRgb565(red, green, blue);
            destination[0] = (byte)packed;
            destination[1] = (byte)(packed >> 8);
            return;
        }

        if (info.ColorType == SKColorType.Bgra8888)
        {
            destination[0] = blue;
            destination[1] = green;
            destination[2] = red;
        }
        else
        {
            destination[0] = red;
            destination[1] = green;
            destination[2] = blue;
        }

        destination[3] = alphaByte;
    }

    private static byte ResizeChannelToByte(float value) =>
        (byte)Math.Clamp(MathF.Floor(Math.Clamp(value, 0f, 1f) * 255f + 0.5f), 0f, 255f);

    public SKData Encode(SKEncodedImageFormat format, int quality)
    {
        using var pixmap = PeekPixels();
        return pixmap?.Encode(format, quality)!;
    }

    public bool Encode(Stream dst, SKEncodedImageFormat format, int quality)
    {
        ArgumentNullException.ThrowIfNull(dst);
        using var pixmap = PeekPixels();
        return pixmap is not null && pixmap.Encode(dst, format, quality);
    }

    public bool Encode(SKWStream dst, SKEncodedImageFormat format, int quality)
    {
        ArgumentNullException.ThrowIfNull(dst);
        using var pixmap = PeekPixels();
        return pixmap is not null && pixmap.Encode(dst, format, quality);
    }

    public SKShader ToShader() =>
        ToShader(
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            SKSamplingOptions.Default,
            SKMatrix.Identity);

    public SKShader ToShader(SKShaderTileMode tmx, SKShaderTileMode tmy) =>
        ToShader(tmx, tmy, SKSamplingOptions.Default, SKMatrix.Identity);

    public SKShader ToShader(
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKSamplingOptions sampling) =>
        ToShader(tmx, tmy, sampling, SKMatrix.Identity);

#pragma warning disable CS0619
    [Obsolete("Use ToShader(SKShaderTileMode tmx, SKShaderTileMode tmy, SKSamplingOptions sampling) instead.", true)]
    public SKShader ToShader(
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKFilterQuality quality) =>
        ToShader(tmx, tmy, SamplingFromQuality((int)quality), SKMatrix.Identity);
#pragma warning restore CS0619

    public SKShader ToShader(
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKMatrix localMatrix) =>
        ToShader(tmx, tmy, SKSamplingOptions.Default, localMatrix);

    public SKShader ToShader(
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKSamplingOptions sampling,
        SKMatrix localMatrix) =>
        SKShader.CreateRetainedImage(SKImage.FromBitmap(this), tmx, tmy, localMatrix, sampling);

#pragma warning disable CS0619
    [Obsolete("Use ToShader(SKShaderTileMode tmx, SKShaderTileMode tmy, SKSamplingOptions sampling, SKMatrix localMatrix) instead.", true)]
    public SKShader ToShader(
        SKShaderTileMode tmx,
        SKShaderTileMode tmy,
        SKFilterQuality quality,
        SKMatrix localMatrix) =>
        ToShader(tmx, tmy, SamplingFromQuality((int)quality), localMatrix);
#pragma warning restore CS0619

    internal byte[] CopyRgba8888Rows()
    {
        FlushAttachedCanvas();

        byte[] buffer = new byte[_width * _height * 4];
        if (_pixels == IntPtr.Zero || _width <= 0 || _height <= 0)
        {
            return buffer;
        }

        unsafe
        {
            byte* src = (byte*)_pixels;
            fixed (byte* dst = buffer)
            {
                for (int y = 0; y < _height; y++)
                {
                    byte* srcRow = src + y * RowBytes;
                    byte* dstRow = dst + y * _width * 4;

                    if (ColorType == SKColorType.Rgb565)
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            int srcIdx = x * 2;
                            int dstIdx = x * 4;
                            ushort value = (ushort)(srcRow[srcIdx] | (srcRow[srcIdx + 1] << 8));
                            dstRow[dstIdx] = (byte)(((value >> 11) & 0x1f) * 255 / 31);
                            dstRow[dstIdx + 1] = (byte)(((value >> 5) & 0x3f) * 255 / 63);
                            dstRow[dstIdx + 2] = (byte)((value & 0x1f) * 255 / 31);
                            dstRow[dstIdx + 3] = 255;
                        }
                    }
                    else if (ColorType == SKColorType.Bgra8888)
                    {
                        for (int x = 0; x < _width; x++)
                        {
                            int srcIdx = x * 4;
                            int dstIdx = x * 4;
                            dstRow[dstIdx] = srcRow[srcIdx + 2];
                            dstRow[dstIdx + 1] = srcRow[srcIdx + 1];
                            dstRow[dstIdx + 2] = srcRow[srcIdx];
                            dstRow[dstIdx + 3] = srcRow[srcIdx + 3];
                        }
                    }
                    else
                    {
                        System.Buffer.MemoryCopy(srcRow, dstRow, _width * 4, _width * 4);
                    }
                }
            }
        }

        return buffer;
    }

    private static unsafe void CopyRows(
        IntPtr source,
        int sourceRowBytes,
        IntPtr destination,
        int destinationRowBytes,
        int copyRowBytes,
        int height)
    {
        byte* src = (byte*)source;
        byte* dst = (byte*)destination;
        for (int y = 0; y < height; y++)
        {
            System.Buffer.MemoryCopy(
                src + y * sourceRowBytes,
                dst + y * destinationRowBytes,
                destinationRowBytes,
                copyRowBytes);
        }
    }

    private static ushort PackRgb565(byte red, byte green, byte blue)
    {
        return (ushort)(
            ((red * 31 + 127) / 255 << 11) |
            ((green * 63 + 127) / 255 << 5) |
            ((blue * 31 + 127) / 255));
    }

    private static byte Premultiply(byte color, byte alpha)
    {
        return (byte)((color * alpha + 127) / 255);
    }

    private static byte Unpremultiply(byte color, byte alpha)
    {
        return alpha == 0
            ? (byte)0
            : (byte)Math.Min(255, (color * 255 + alpha / 2) / alpha);
    }

    internal static int ComputeByteCount(SKImageInfo info, int rowBytes)
    {
        if (info.Width <= 0 || info.Height <= 0 || info.BytesPerPixel <= 0 || rowBytes <= 0)
        {
            return 0;
        }

        return checked((info.Height - 1) * rowBytes + info.RowBytes);
    }

    private static bool TryValidateStorage(SKImageInfo info, int rowBytes, out int byteCount)
    {
        byteCount = 0;
        if (info.Width < 0 || info.Height < 0 || rowBytes < 0)
        {
            return false;
        }

        try
        {
            int minimumRowBytes = info.RowBytes;
            if (info.Height > 0 && rowBytes < minimumRowBytes)
            {
                return false;
            }

            byteCount = ComputeByteCount(info, rowBytes);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void SetMetadata(SKImageInfo info, int rowBytes)
    {
        _info = info;
        _width = info.Width;
        _height = info.Height;
        _rowBytes = rowBytes;
    }

    private void SwapStorage(SKBitmap other)
    {
        FlushAttachedCanvas();
        other.FlushAttachedCanvas();
        (_pixels, other._pixels) = (other._pixels, _pixels);
        (_ownsPixels, other._ownsPixels) = (other._ownsPixels, _ownsPixels);
        (_width, other._width) = (other._width, _width);
        (_height, other._height) = (other._height, _height);
        (_rowBytes, other._rowBytes) = (other._rowBytes, _rowBytes);
        (_info, other._info) = (other._info, _info);
        (_releaseDelegate, other._releaseDelegate) = (other._releaseDelegate, _releaseDelegate);
        (_releaseContext, other._releaseContext) = (other._releaseContext, _releaseContext);
        (_pixelOwner, other._pixelOwner) = (other._pixelOwner, _pixelOwner);
        (_isImmutable, other._isImmutable) = (other._isImmutable, _isImmutable);
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            ReleasePixels();
        }
        catch when (!disposing)
        {
            // Release callbacks cannot be allowed to terminate the finalizer thread.
        }
        finally
        {
            base.Dispose(disposing);
        }
    }

    private void ReleasePixels()
    {
        var pixels = _pixels;
        var ownsPixels = _ownsPixels;
        var releaseDelegate = _releaseDelegate;
        var releaseContext = _releaseContext;

        _pixels = IntPtr.Zero;
        _ownsPixels = false;
        _releaseDelegate = null;
        _releaseContext = null;
        _pixelOwner = null;

        if (pixels == IntPtr.Zero)
        {
            return;
        }

        if (ownsPixels)
        {
            Marshal.FreeHGlobal(pixels);
        }
        else
        {
            releaseDelegate?.Invoke(pixels, releaseContext!);
        }
    }
}

public class SKManagedStream : SKAbstractManagedStream
{
    private const int CopyBufferSize = 81920;
    private Stream? _stream;
    private readonly bool _disposeStream;
    private bool _isAtEnd;

    protected override Stream? BackingStream => _stream;

    public SKManagedStream(Stream managedStream)
        : this(managedStream, disposeManagedStream: false)
    {
    }

    public SKManagedStream(Stream managedStream, bool disposeManagedStream)
    {
        ArgumentNullException.ThrowIfNull(managedStream);
        _stream = managedStream;
        _disposeStream = disposeManagedStream;
    }

    public int CopyTo(SKWStream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var source = _stream ?? throw new ObjectDisposedException(nameof(SKManagedStream));
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            var total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, read);
                total += read;
            }

            destination.Flush();
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public SKStreamAsset ToMemoryStream()
    {
        using var destination = new SKDynamicMemoryWStream();
        CopyTo(destination);
        return destination.DetachAsStream();
    }

    protected internal override unsafe IntPtr OnRead(IntPtr buffer, IntPtr size)
    {
        var requested = checked((int)size.ToInt64());
        ArgumentOutOfRangeException.ThrowIfNegative(requested);
        if (requested == 0)
        {
            return IntPtr.Zero;
        }

        var source = _stream ?? throw new ObjectDisposedException(nameof(SKManagedStream));
        int read;
        if (buffer != IntPtr.Zero)
        {
            read = source.Read(new Span<byte>(buffer.ToPointer(), requested));
        }
        else if (source.CanSeek)
        {
            var available = Math.Max(0L, source.Length - source.Position);
            read = checked((int)Math.Min(requested, available));
            source.Position += read;
        }
        else
        {
            read = SkipNonSeekable(source, requested);
        }

        if (!source.CanSeek && read < requested)
        {
            _isAtEnd = true;
        }

        return (IntPtr)read;
    }

    protected internal override IntPtr OnPeek(IntPtr buffer, IntPtr size)
    {
        var source = _stream ?? throw new ObjectDisposedException(nameof(SKManagedStream));
        if (!source.CanSeek)
        {
            return IntPtr.Zero;
        }

        var position = source.Position;
        var read = OnRead(buffer, size);
        source.Position = position;
        return read;
    }

    protected internal override bool OnIsAtEnd()
    {
        var source = _stream ?? throw new ObjectDisposedException(nameof(SKManagedStream));
        return source.CanSeek ? source.Position >= source.Length : _isAtEnd;
    }

    protected internal override bool OnHasPosition() =>
        (_stream ?? throw new ObjectDisposedException(nameof(SKManagedStream))).CanSeek;

    protected internal override bool OnHasLength() =>
        (_stream ?? throw new ObjectDisposedException(nameof(SKManagedStream))).CanSeek;

    protected internal override bool OnRewind()
    {
        var source = _stream ?? throw new ObjectDisposedException(nameof(SKManagedStream));
        if (!source.CanSeek)
        {
            return false;
        }

        source.Position = 0;
        return true;
    }

    protected internal override IntPtr OnGetPosition()
    {
        var source = _stream ?? throw new ObjectDisposedException(nameof(SKManagedStream));
        return source.CanSeek ? (IntPtr)source.Position : IntPtr.Zero;
    }

    protected internal override IntPtr OnGetLength()
    {
        var source = _stream ?? throw new ObjectDisposedException(nameof(SKManagedStream));
        return source.CanSeek ? (IntPtr)source.Length : IntPtr.Zero;
    }

    protected internal override bool OnSeek(IntPtr position)
    {
        var source = _stream ?? throw new ObjectDisposedException(nameof(SKManagedStream));
        var target = position.ToInt64();
        if (!source.CanSeek || target < 0 || target > source.Length)
        {
            return false;
        }

        source.Position = target;
        return true;
    }

    protected internal override bool OnMove(int offset)
    {
        var source = _stream ?? throw new ObjectDisposedException(nameof(SKManagedStream));
        if (!source.CanSeek)
        {
            return false;
        }

        long target;
        try
        {
            target = checked(source.Position + offset);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (target < 0 || target > source.Length)
        {
            return false;
        }

        source.Position = target;
        return true;
    }

    protected internal override IntPtr OnCreateNew() => IntPtr.Zero;

    protected override void DisposeManaged()
    {
        var stream = _stream;
        _stream = null;
        if (_disposeStream)
        {
            stream?.Dispose();
        }

        base.DisposeManaged();
    }

    private static int SkipNonSeekable(Stream source, int requested)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(requested, CopyBufferSize));
        try
        {
            var total = 0;
            while (total < requested)
            {
                var count = source.Read(rented, 0, Math.Min(CopyBufferSize, requested - total));
                if (count == 0)
                {
                    break;
                }

                total += count;
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
