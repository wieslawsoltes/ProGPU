using System.Buffers;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace SkiaSharp;

public delegate void SKImageRasterReleaseDelegate(IntPtr pixels, object context);

public delegate void SKImageTextureReleaseDelegate(object context);

public partial class SKImage
{
    public static SKImage Create(SKImageInfo info)
    {
        ValidateImageInfo(info);
        var pixels = new byte[SKBitmap.ComputeByteCount(info, info.RowBytes)];
        return CreateFromPixelCopy(info, pixels, info.RowBytes);
    }

    public static SKImage FromPixelCopy(SKImageInfo info, IntPtr pixels) =>
        FromPixelCopy(info, pixels, info.RowBytes);

    public static unsafe SKImage FromPixelCopy(SKImageInfo info, IntPtr pixels, int rowBytes)
    {
        int byteCount = ValidatePixelStorage(info, rowBytes);
        if (pixels == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        return CreateFromPixelCopy(
            info,
            new ReadOnlySpan<byte>(pixels.ToPointer(), byteCount),
            rowBytes);
    }

    public static SKImage FromPixelCopy(SKImageInfo info, byte[] pixels) =>
        FromPixelCopy(info, pixels, info.RowBytes);

    public static SKImage FromPixelCopy(SKImageInfo info, byte[] pixels, int rowBytes)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        return CreateFromPixelCopy(info, pixels, rowBytes);
    }

    public static SKImage FromPixelCopy(SKImageInfo info, ReadOnlySpan<byte> pixels) =>
        FromPixelCopy(info, pixels, info.RowBytes);

    public static SKImage FromPixelCopy(
        SKImageInfo info,
        ReadOnlySpan<byte> pixels,
        int rowBytes) =>
        CreateFromPixelCopy(info, pixels, rowBytes);

    public static SKImage FromPixelCopy(SKImageInfo info, Stream pixels) =>
        FromPixelCopy(info, pixels, info.RowBytes);

    public static SKImage FromPixelCopy(SKImageInfo info, Stream pixels, int rowBytes)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        int byteCount = ValidatePixelStorage(info, rowBytes);
        byte[] storage = GC.AllocateUninitializedArray<byte>(byteCount);
        pixels.ReadExactly(storage);
        return CreateFromPixelCopy(info, storage, rowBytes);
    }

    public static SKImage FromPixelCopy(SKImageInfo info, SKStream pixels) =>
        FromPixelCopy(info, pixels, info.RowBytes);

    public static SKImage FromPixelCopy(SKImageInfo info, SKStream pixels, int rowBytes)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        int byteCount = ValidatePixelStorage(info, rowBytes);
        byte[] storage = GC.AllocateUninitializedArray<byte>(byteCount);
        ReadExactly(pixels, storage);

        return CreateFromPixelCopy(info, storage, rowBytes);
    }

    public static SKImage FromPixelCopy(SKPixmap pixmap)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        return FromPixelCopy(pixmap.Info, pixmap.GetPixels(), pixmap.RowBytes);
    }

    public static SKImage FromPixels(SKImageInfo info, IntPtr pixels) =>
        FromPixels(info, pixels, info.RowBytes);

    public static SKImage FromPixels(SKImageInfo info, SKData data) =>
        FromPixels(info, data, info.RowBytes);

    public static SKImage FromPixels(SKImageInfo info, SKData data, int rowBytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        return CreateFromPixelCopy(info, data.AsSpan(), rowBytes);
    }

    public static SKImage FromPixels(SKPixmap pixmap) =>
        FromPixels(pixmap, null!, null!);

    public static SKImage FromPixels(SKPixmap pixmap, SKImageRasterReleaseDelegate releaseProc) =>
        FromPixels(pixmap, releaseProc, null!);

    public static SKImage FromPixels(
        SKPixmap pixmap,
        SKImageRasterReleaseDelegate releaseProc,
        object releaseContext)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        var image = FromPixelCopy(pixmap);
        image._rasterReleaseProc = releaseProc;
        image._rasterReleasePixels = pixmap.GetPixels();
        image._releaseContext = releaseContext;
        return image;
    }

    public static SKImage? FromEncodedData(SKData data, SKRectI subset)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var image = FromEncodedData(data);
        return image?.Subset(subset);
    }

    public static SKImage? FromEncodedData(SKStream data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return FromEncodedData(ReadToEnd(data));
    }

    public static SKImage? FromEncodedData(string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        return FromEncodedData(File.ReadAllBytes(filename));
    }

    public static SKImage FromPicture(SKPicture picture, SKSizeI dimensions, SKPaint paint) =>
        FromPicture(picture, dimensions, SKMatrix.Identity, paint);

    public static SKImage FromPicture(
        SKPicture picture,
        SKSizeI dimensions,
        SKMatrix matrix,
        SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (dimensions.Width <= 0 || dimensions.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        }

        using var surface = SKSurface.Create(new SKImageInfo(
            dimensions.Width,
            dimensions.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.SetMatrix(in matrix);
        if (paint is null)
        {
            surface.Canvas.DrawPicture(picture);
        }
        else
        {
            surface.Canvas.SaveLayer(paint);
            surface.Canvas.DrawPicture(picture);
            surface.Canvas.Restore();
        }

        return surface.Snapshot();
    }

    public SKImage Subset(SKRectI subset) => SubsetCore(null, subset);

    public SKImage Subset(GRRecordingContext context, SKRectI subset)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SubsetCore(context, subset);
    }

    private SKImage SubsetCore(GRRecordingContext? context, SKRectI subset)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var bounds = new SKRectI(0, 0, Width, Height);
        if (subset.Width <= 0 || subset.Height <= 0 || !bounds.Contains(subset))
        {
            return null!;
        }

        if (context is not null &&
            (context.IsAbandoned || (_isTextureBacked && !IsValid(context))))
        {
            return null!;
        }

        if (_isTextureBacked && context is null)
        {
            return null!;
        }

        return new SKImage(
            _textureStorage,
            new SKImageInfo(subset.Width, subset.Height, ColorType, AlphaType, ColorSpace),
            _portableRgbaPixels,
            checked(_portableOriginX + subset.Left),
            checked(_portableOriginY + subset.Top),
            _portableRowWidth,
            checked(_textureOriginX + (uint)subset.Left),
            checked(_textureOriginY + (uint)subset.Top),
            _isTextureBacked);
    }

    public SKImage ToTextureImage(GRContext context) =>
        ToTextureImage(context, mipmapped: false, budgeted: false);

    public SKImage ToTextureImage(GRContext context, bool mipmapped) =>
        ToTextureImage(context, mipmapped, budgeted: false);

    public SKImage ToTextureImage(GRContext context, bool mipmapped, bool budgeted)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsAbandoned)
        {
            return null!;
        }

        var targetContext = context.Context;
        if (ReferenceEquals(Texture.Context, targetContext))
        {
            var texture = new GpuTexture(
                targetContext,
                checked((uint)Width),
                checked((uint)Height),
                Texture.Format,
                TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc |
                    (mipmapped ? TextureUsage.RenderAttachment : 0),
                "SKImage Texture Copy",
                alphaMode: Texture.AlphaMode,
                mipLevelCount: mipmapped
                    ? CalculateMipLevelCount(checked((uint)Width), checked((uint)Height))
                    : 1);
            try
            {
                texture.CopyBaseLevelRegionFrom(
                    Texture,
                    _textureOriginX,
                    _textureOriginY,
                    0,
                    0,
                    checked((uint)Width),
                    checked((uint)Height));
                if (mipmapped)
                {
                    texture.GenerateMipmaps2DLinear();
                }

                return new SKImage(
                    texture,
                    true,
                    _info,
                    _portableRgbaPixels is null
                        ? null
                        : CopyPortablePixels(),
                    true);
            }
            catch
            {
                texture.Dispose();
                throw;
            }
        }

        var pixels = ReadTexturePixelsAsRgba8888();
        var transferred = CreateTextureFromRgbaPixels(
            CreateReadbackInfo(),
            pixels,
            targetContext,
            mipmapped,
            "SKImage Cross-context Texture");
        return new SKImage(transferred, true, _info, pixels, true);
    }

    public SKImage ApplyImageFilter(
        SKImageFilter filter,
        SKRectI subset,
        SKRectI clipBounds,
        out SKRectI outSubset,
        out SKPoint outOffset)
    {
        var result = ApplyImageFilterCore(null, filter, subset, clipBounds, out outSubset, out var offset);
        outOffset = new SKPoint(offset.X, offset.Y);
        return result;
    }

    public SKImage ApplyImageFilter(
        SKImageFilter filter,
        SKRectI subset,
        SKRectI clipBounds,
        out SKRectI outSubset,
        out SKPointI outOffset) =>
        ApplyImageFilterCore(null, filter, subset, clipBounds, out outSubset, out outOffset);

    public SKImage ApplyImageFilter(
        GRContext context,
        SKImageFilter filter,
        SKRectI subset,
        SKRectI clipBounds,
        out SKRectI outSubset,
        out SKPointI outOffset) =>
        ApplyImageFilterCore(context, filter, subset, clipBounds, out outSubset, out outOffset);

    public SKImage ApplyImageFilter(
        GRRecordingContext context,
        SKImageFilter filter,
        SKRectI subset,
        SKRectI clipBounds,
        out SKRectI outSubset,
        out SKPointI outOffset) =>
        ApplyImageFilterCore(context, filter, subset, clipBounds, out outSubset, out outOffset);

    private SKImage ApplyImageFilterCore(
        GRRecordingContext? context,
        SKImageFilter filter,
        SKRectI subset,
        SKRectI clipBounds,
        out SKRectI outSubset,
        out SKPointI outOffset)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var imageBounds = new SKRectI(0, 0, Width, Height);
        var sourceBounds = SKRectI.Intersect(imageBounds, subset);
        var outputBounds = clipBounds.Standardized;
        if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0 ||
            outputBounds.Width <= 0 || outputBounds.Height <= 0 ||
            (context is not null &&
             (context.IsAbandoned || (_isTextureBacked && !IsValid(context)))))
        {
            outSubset = SKRectI.Empty;
            outOffset = SKPointI.Empty;
            return null!;
        }

        using var surface = SKSurface.Create(new SKImageInfo(
            outputBounds.Width,
            outputBounds.Height,
            SKColorType.Rgba8888,
            AlphaType,
            ColorSpace));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(-outputBounds.Left, -outputBounds.Top);
        using var paint = new SKPaint { ImageFilter = filter };
        surface.Canvas.SaveLayer(new SKRect(outputBounds.Left, outputBounds.Top, outputBounds.Right, outputBounds.Bottom), paint);
        surface.Canvas.DrawImage(
            this,
            new SKRect(sourceBounds.Left, sourceBounds.Top, sourceBounds.Right, sourceBounds.Bottom),
            new SKRect(sourceBounds.Left, sourceBounds.Top, sourceBounds.Right, sourceBounds.Bottom),
            SKSamplingOptions.Default);
        surface.Canvas.Restore();
        outSubset = outputBounds;
        outOffset = outputBounds.Location;
        var result = surface.Snapshot();
        return context is null || ReferenceEquals(result.Texture.Context, context.BackendContext)
            ? result
            : TransferToRecordingContext(result, context);
    }

    private static SKImage TransferToRecordingContext(SKImage image, GRRecordingContext context)
    {
        var info = image._info;
        var pixels = image.ReadTexturePixelsAsRgba8888();
        var texture = CreateTextureFromRgbaPixels(
            image.CreateReadbackInfo(),
            pixels,
            context.BackendContext,
            generateMipmaps: false,
            "SKImage Filter Context Transfer");
        image.Dispose();
        return new SKImage(texture, true, info, pixels, true);
    }

    public static SKImage? FromAdoptedTexture(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha) =>
        FromAdoptedTexture((GRRecordingContext)context, texture, origin, colorType, alpha, null);

    public static SKImage? FromAdoptedTexture(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha,
        SKColorSpace colorspace) =>
        FromAdoptedTexture((GRRecordingContext)context, texture, origin, colorType, alpha, colorspace);

    public static SKImage? FromAdoptedTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType) =>
        FromAdoptedTexture(context, texture, origin, colorType, SKAlphaType.Premul, null);

    public static SKImage? FromAdoptedTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha) =>
        FromAdoptedTexture(context, texture, origin, colorType, alpha, null);

    public static SKImage? FromAdoptedTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha,
        SKColorSpace? colorspace) =>
        WrapBackendTexture(context, texture, colorType, alpha, colorspace, ownsTexture: true, null, null);

    public static SKImage? FromAdoptedTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        SKColorType colorType) =>
        FromAdoptedTexture(context, texture, GRSurfaceOrigin.TopLeft, colorType);

    public static SKImage? FromTexture(
        GRContext context,
        GRBackendTexture texture,
        SKColorType colorType) =>
        FromTexture((GRRecordingContext)context, texture, GRSurfaceOrigin.TopLeft, colorType);

    public static SKImage? FromTexture(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType) =>
        FromTexture((GRRecordingContext)context, texture, origin, colorType);

    public static SKImage? FromTexture(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha) =>
        FromTexture((GRRecordingContext)context, texture, origin, colorType, alpha);

    public static SKImage? FromTexture(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha,
        SKColorSpace colorspace) =>
        FromTexture((GRRecordingContext)context, texture, origin, colorType, alpha, colorspace);

    public static SKImage? FromTexture(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha,
        SKColorSpace colorspace,
        SKImageTextureReleaseDelegate releaseProc) =>
        FromTexture((GRRecordingContext)context, texture, origin, colorType, alpha, colorspace, releaseProc, null!);

    public static SKImage? FromTexture(
        GRContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha,
        SKColorSpace colorspace,
        SKImageTextureReleaseDelegate releaseProc,
        object releaseContext) =>
        FromTexture((GRRecordingContext)context, texture, origin, colorType, alpha, colorspace, releaseProc, releaseContext);

    public static SKImage? FromTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        SKColorType colorType) =>
        FromTexture(context, texture, GRSurfaceOrigin.TopLeft, colorType);

    public static SKImage? FromTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType) =>
        FromTexture(context, texture, origin, colorType, SKAlphaType.Premul);

    public static SKImage? FromTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha) =>
        FromTexture(context, texture, origin, colorType, alpha, null!);

    public static SKImage? FromTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha,
        SKColorSpace colorspace) =>
        WrapBackendTexture(context, texture, colorType, alpha, colorspace, false, null, null);

    public static SKImage? FromTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha,
        SKColorSpace colorspace,
        SKImageTextureReleaseDelegate releaseProc) =>
        FromTexture(context, texture, origin, colorType, alpha, colorspace, releaseProc, null!);

    public static SKImage? FromTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        GRSurfaceOrigin origin,
        SKColorType colorType,
        SKAlphaType alpha,
        SKColorSpace colorspace,
        SKImageTextureReleaseDelegate releaseProc,
        object releaseContext) =>
        WrapBackendTexture(context, texture, colorType, alpha, colorspace, false, releaseProc, releaseContext);

    private static SKImage? WrapBackendTexture(
        GRRecordingContext context,
        GRBackendTexture texture,
        SKColorType colorType,
        SKAlphaType alpha,
        SKColorSpace? colorspace,
        bool ownsTexture,
        SKImageTextureReleaseDelegate? releaseProc,
        object? releaseContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(texture);
        var gpuTexture = texture.BackendTexture;
        if (gpuTexture is null || context.IsAbandoned ||
            !ReferenceEquals(gpuTexture.Context, context.BackendContext))
        {
            return null;
        }

        if ((gpuTexture.Usage & TextureUsage.TextureBinding) == 0 ||
            (gpuTexture.Usage & TextureUsage.CopySrc) == 0 ||
            gpuTexture.SampleCount != 1)
        {
            return null;
        }

        return new SKImage(
            gpuTexture,
            ownsTexture,
            new SKImageInfo(texture.Width, texture.Height, colorType, alpha, colorspace),
            null,
            true,
            releaseProc,
            releaseContext);
    }

    private static SKImage CreateFromPixelCopy(
        SKImageInfo info,
        ReadOnlySpan<byte> pixels,
        int rowBytes)
    {
        int byteCount = ValidatePixelStorage(info, rowBytes);
        if (pixels.Length < byteCount)
        {
            throw new ArgumentException("The pixel buffer is smaller than the declared image storage.", nameof(pixels));
        }

        unsafe
        {
            fixed (byte* pointer = pixels)
            {
                using var bitmap = new SKBitmap();
                if (!bitmap.InstallPixels(info, (IntPtr)pointer, rowBytes))
                {
                    throw new ArgumentException("The image format or pixel storage is invalid.", nameof(info));
                }

                return FromBitmap(bitmap);
            }
        }
    }

    private static int ValidatePixelStorage(SKImageInfo info, int rowBytes)
    {
        ValidateImageInfo(info);
        if (rowBytes < info.RowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(rowBytes));
        }

        return SKBitmap.ComputeByteCount(info, rowBytes);
    }

    private static void ValidateImageInfo(SKImageInfo info)
    {
        if (info.IsEmpty || info.Width <= 0 || info.Height <= 0 || info.BytesPerPixel <= 0)
        {
            throw new ArgumentException("Image dimensions and color type must describe non-empty pixel storage.", nameof(info));
        }
    }

    private static byte[] ReadToEnd(SKStream stream)
    {
        using var output = new MemoryStream(stream.HasLength ? stream.Length : 0);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                int read = stream.Read(buffer, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static unsafe void ReadExactly(SKStream stream, byte[] storage)
    {
        fixed (byte* storageBase = storage)
        {
            int offset = 0;
            while (offset < storage.Length)
            {
                int read = stream.Read(
                    (IntPtr)(storageBase + offset),
                    storage.Length - offset);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        "The pixel stream ended before the declared image storage was read.");
                }

                offset += read;
            }
        }
    }
}
