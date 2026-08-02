using System.Runtime.InteropServices;
using System.Threading;

namespace SkiaSharp;

public partial class SKImage
{
    public uint UniqueId => unchecked((uint)Handle.ToInt64());

    public bool IsValid(GRContext? context) => IsValid((GRRecordingContext?)context);

    public bool IsValid(GRRecordingContext? context) =>
        !IsDisposed
        && !Texture.IsDisposed
        && context is { IsAbandoned: false }
        && ReferenceEquals(Texture.Context, context.BackendContext);

    public SKShader ToShader() => ToShader(
        SKShaderTileMode.Clamp,
        SKShaderTileMode.Clamp,
        SKSamplingOptions.Default,
        SKMatrix.Identity);

    public SKShader ToShader(
        SKShaderTileMode tileX,
        SKShaderTileMode tileY,
        SKSamplingOptions sampling) =>
        ToShader(tileX, tileY, sampling, SKMatrix.Identity);

    public SKShader ToShader(
        SKShaderTileMode tileX,
        SKShaderTileMode tileY,
        SKSamplingOptions sampling,
        SKMatrix localMatrix) =>
        SKShader.CreateRetainedImage(CreateOwnedCopy(), tileX, tileY, localMatrix, sampling);

#pragma warning disable CS0619
    [Obsolete("Use ToShader(SKShaderTileMode, SKShaderTileMode, SKSamplingOptions) instead.", true)]
    public SKShader ToShader(
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKFilterQuality quality) =>
        ToShader(tileModeX, tileModeY, SamplingFromQuality((int)quality), SKMatrix.Identity);

    [Obsolete("Use ToShader(SKShaderTileMode, SKShaderTileMode, SKSamplingOptions, SKMatrix) instead.", true)]
    public SKShader ToShader(
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKFilterQuality quality,
        SKMatrix localMatrix) =>
        ToShader(tileModeX, tileModeY, SamplingFromQuality((int)quality), localMatrix);
#pragma warning restore CS0619

    public SKShader ToRawShader() => ToRawShader(
        SKShaderTileMode.Clamp,
        SKShaderTileMode.Clamp,
        SKSamplingOptions.Default,
        SKMatrix.Identity);

    public SKShader ToRawShader(SKShaderTileMode tileX, SKShaderTileMode tileY) =>
        ToRawShader(tileX, tileY, SKSamplingOptions.Default, SKMatrix.Identity);

    public SKShader ToRawShader(
        SKShaderTileMode tileX,
        SKShaderTileMode tileY,
        SKMatrix localMatrix) =>
        ToRawShader(tileX, tileY, SKSamplingOptions.Default, localMatrix);

    public SKShader ToRawShader(
        SKShaderTileMode tileX,
        SKShaderTileMode tileY,
        SKSamplingOptions sampling) =>
        ToRawShader(tileX, tileY, sampling, SKMatrix.Identity);

    public SKShader ToRawShader(
        SKShaderTileMode tileX,
        SKShaderTileMode tileY,
        SKSamplingOptions sampling,
        SKMatrix localMatrix) =>
        SKShader.CreateRetainedImage(
            CreateOwnedCopy(),
            tileX,
            tileY,
            localMatrix,
            sampling,
            isRaw: true);

    public bool ReadPixels(SKImageInfo dstInfo, IntPtr dstPixels) =>
        ReadPixels(dstInfo, dstPixels, dstInfo.RowBytes, 0, 0, SKImageCachingHint.Allow);

    public bool ReadPixels(SKImageInfo dstInfo, IntPtr dstPixels, int dstRowBytes) =>
        ReadPixels(dstInfo, dstPixels, dstRowBytes, 0, 0, SKImageCachingHint.Allow);

    public bool ReadPixels(
        SKImageInfo dstInfo,
        IntPtr dstPixels,
        int dstRowBytes,
        int srcX,
        int srcY) =>
        ReadPixels(dstInfo, dstPixels, dstRowBytes, srcX, srcY, SKImageCachingHint.Allow);

    public bool ReadPixels(SKPixmap pixmap) => ReadPixels(pixmap, 0, 0);

    public bool ReadPixels(SKPixmap pixmap, int srcX, int srcY) =>
        ReadPixels(pixmap, srcX, srcY, SKImageCachingHint.Allow);

    public bool ReadPixels(
        SKPixmap pixmap,
        int srcX,
        int srcY,
        SKImageCachingHint cachingHint)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        return ReadPixels(
            pixmap.Info,
            pixmap.GetPixels(),
            pixmap.RowBytes,
            srcX,
            srcY,
            cachingHint);
    }

    public bool ScalePixels(
        SKPixmap dst,
        SKSamplingOptions sampling,
        SKImageCachingHint cachingHint) =>
        ScalePixels(dst, sampling);

#pragma warning disable CS0619
    [Obsolete("Use ScalePixels(SKPixmap, SKSamplingOptions) instead.", true)]
    public bool ScalePixels(SKPixmap dst, SKFilterQuality quality) =>
        ScalePixels(dst, SamplingFromQuality((int)quality));

    [Obsolete("Use ScalePixels(SKPixmap, SKSamplingOptions, SKImageCachingHint) instead.", true)]
    public bool ScalePixels(
        SKPixmap dst,
        SKFilterQuality quality,
        SKImageCachingHint cachingHint) =>
        ScalePixels(dst, SamplingFromQuality((int)quality), cachingHint);
#pragma warning restore CS0619

    public bool PeekPixels(SKPixmap pixmap)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        var optionalState = Volatile.Read(ref _optionalState);
        if (_isTextureBacked && optionalState?.RasterPixels is null)
        {
            pixmap.Reset();
            return false;
        }

        EnsureRasterPixels();
        optionalState = EnsureOptionalState();
        pixmap.Reset(
            _info,
            optionalState.RasterPixelsHandle.AddrOfPinnedObject(),
            _info.RowBytes);
        pixmap.SetPixelSource(this);
        return true;
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

    public SKImage ToRasterImage() => ToRasterImage(ensurePixelData: false);

    private static SKSamplingOptions SamplingFromQuality(int quality) =>
        quality switch
        {
            0 => SKSamplingOptions.Default,
            1 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            2 => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            3 => new SKSamplingOptions(SKCubicResampler.Mitchell),
            _ => SKSamplingOptions.Default,
        };

    private void EnsureRasterPixels()
    {
        var optionalState = EnsureOptionalState();
        lock (optionalState.RasterPixelLock)
        {
            if (optionalState.RasterPixels is null)
            {
                optionalState.RasterPixels = GC.AllocateUninitializedArray<byte>(
                    checked((int)_info.BytesSize64));
            }

            if (!optionalState.RasterPixelsHandle.IsAllocated &&
                optionalState.RasterPixels.Length > 0)
            {
                optionalState.RasterPixelsHandle = GCHandle.Alloc(
                    optionalState.RasterPixels,
                    GCHandleType.Pinned);
                if (!ReadPixels(
                        _info,
                        optionalState.RasterPixelsHandle.AddrOfPinnedObject(),
                        _info.RowBytes,
                        0,
                        0,
                        SKImageCachingHint.Allow))
                {
                    optionalState.RasterPixelsHandle.Free();
                    optionalState.RasterPixels = null;
                    throw new InvalidOperationException("The immutable image could not be materialized as raster pixels.");
                }
            }
        }
    }

    private void SetMaterializedRasterPixels(byte[] pixels)
    {
        var optionalState = EnsureOptionalState();
        lock (optionalState.RasterPixelLock)
        {
            if (optionalState.RasterPixelsHandle.IsAllocated)
            {
                optionalState.RasterPixelsHandle.Free();
            }

            optionalState.RasterPixels = pixels;
            if (pixels.Length > 0)
            {
                optionalState.RasterPixelsHandle = GCHandle.Alloc(
                    pixels,
                    GCHandleType.Pinned);
            }
        }
    }
}
