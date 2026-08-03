using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.WebGPU;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkImageBitmapTests
{
    [Fact]
    public void FromEncodedDataReturnsNullForInvalidImage()
    {
        Assert.Null(SKImage.FromEncodedData(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void PngGammaColorSpaceFlowsFromCodecThroughBitmapAndImage()
    {
        var encoded = AddPngGammaChunk(TwoPixelPngBytes(), gammaTimes100000: 100000);
        using var codec = SKCodec.Create(SKData.CreateCopy(encoded));
        using var bitmap = SKBitmap.Decode(SKData.CreateCopy(encoded));
        using var image = SKImage.FromBitmap(bitmap);

        Assert.NotNull(codec.Info.ColorSpace);
        Assert.True(codec.Info.ColorSpace.IsLinear);
        Assert.NotNull(bitmap.Info.ColorSpace);
        Assert.True(bitmap.Info.ColorSpace.IsLinear);
        Assert.Same(bitmap.Info.ColorSpace, image.ColorSpace);
        Assert.Equal(bitmap.ColorType, image.ColorType);
        Assert.Equal(bitmap.AlphaType, image.AlphaType);
        Assert.Equal(bitmap.Info.Width, image.Info.Width);
        Assert.Equal(bitmap.Info.Height, image.Info.Height);
    }

    [Fact]
    public void FromBitmapFlushesAttachedCanvasBeforeUploadingPixels()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Red };

        canvas.DrawRect(new SKRect(0, 0, 8, 8), paint);
        using var image = SKImage.FromBitmap(bitmap);

        Assert.Equal(new byte[] { 255, 0, 0, 255 }, image.Texture.ReadPixels()[..4]);
    }

    [Fact]
    public void BitmapFlushRebasesLiveClipBeforeSaveLayerSnapshot()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Red };

        var restoreCount = canvas.Save();
        canvas.ClipRect(new SKRect(1f, 1f, 7f, 7f));
        canvas.DrawRect(new SKRect(0f, 0f, 8f, 8f), paint);
        canvas.Flush();

        Assert.Collection(
            canvas.DrawingContext.Commands,
            command => Assert.Equal(RenderCommandType.PushClip, command.Type));

        var layerRestoreCount = canvas.SaveLayer();
        Assert.Equal(2, layerRestoreCount);
        canvas.RestoreToCount(layerRestoreCount);
        canvas.RestoreToCount(restoreCount);

        Assert.Empty(canvas.DrawingContext.Commands);
    }

    [Fact]
    public void InstallPixelsPreservesRowBytesAndCopyUsesStride()
    {
        var info = new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = Marshal.AllocHGlobal(24);
        try
        {
            WriteBytes(pixels, new byte[]
            {
                1, 2, 3, 4, 5, 6, 7, 8, 99, 99, 99, 99,
                9, 10, 11, 12, 13, 14, 15, 16, 88, 88, 88, 88
            });

            using var bitmap = new SKBitmap();
            bitmap.InstallPixels(info, pixels, rowBytes: 12);

            Assert.Equal(12, bitmap.RowBytes);
            Assert.Equal(20, bitmap.ByteCount);
            Assert.Equal(12, bitmap.PeekPixels().RowBytes);

            using var copy = bitmap.Copy();
            Assert.Equal(8, copy.RowBytes);
            Assert.Equal(16, copy.ByteCount);
            Assert.Equal(new byte[]
            {
                1, 2, 3, 4, 5, 6, 7, 8,
                9, 10, 11, 12, 13, 14, 15, 16
            }, ReadBytes(copy.GetPixels(), 16));
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }

    [Fact]
    public void FromBitmapConvertsBgraRowsBeforeUpload()
    {
        var info = new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = Marshal.AllocHGlobal(24);
        var dst = Marshal.AllocHGlobal(16);
        try
        {
            WriteBytes(pixels, new byte[]
            {
                0, 0, 255, 255, 0, 255, 0, 255, 99, 99, 99, 99,
                255, 0, 0, 255, 255, 255, 255, 255, 88, 88, 88, 88
            });

            using var bitmap = new SKBitmap();
            bitmap.InstallPixels(info, pixels, rowBytes: 12);
            using var image = SKImage.FromBitmap(bitmap);

            image.ReadPixels(
                new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul),
                dst,
                dstRowBytes: 8,
                srcX: 0,
                srcY: 0,
                SKImageCachingHint.Allow);

            Assert.Equal(new byte[]
            {
                255, 0, 0, 255, 0, 255, 0, 255,
                0, 0, 255, 255, 255, 255, 255, 255
            }, ReadBytes(dst, 16));
        }
        finally
        {
            Marshal.FreeHGlobal(dst);
            Marshal.FreeHGlobal(pixels);
        }
    }

    [Fact]
    public void ImmutableTextureDisallowReadbackPreservesDestinationRowPadding()
    {
        var info = new SKImageInfo(
            2,
            2,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var surface = SKSurface.Create(
            info,
            new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
        surface.Canvas.Clear(new SKColor(10, 20, 30, 255));
        surface.Flush();
        using var image = surface.Snapshot();
        var destination = Marshal.AllocHGlobal(24);
        try
        {
            WriteBytes(destination, Enumerable.Repeat((byte)99, 24).ToArray());

            Assert.True(image.ReadPixels(
                info,
                destination,
                dstRowBytes: 12,
                srcX: 0,
                srcY: 0,
                SKImageCachingHint.Disallow));

            Assert.Equal(new byte[]
            {
                10, 20, 30, 255, 10, 20, 30, 255, 99, 99, 99, 99,
                10, 20, 30, 255, 10, 20, 30, 255, 99, 99, 99, 99
            }, ReadBytes(destination, 24));
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void FromBitmapMarksUnpremultipliedUploadsAsStraightAlpha()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        WriteBytes(bitmap.GetPixels(), new byte[] { 255, 0, 0, 128 });

        using var image = SKImage.FromBitmap(bitmap);

        Assert.Equal(GpuTextureAlphaMode.Straight, image.Texture.AlphaMode);
    }

    [Fact]
    public void FromBitmapForcesOpaqueUploadsToAlpha255()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Opaque));
        WriteBytes(bitmap.GetPixels(), new byte[] { 10, 20, 30, 0 });

        using var image = SKImage.FromBitmap(bitmap);

        Assert.Equal(new byte[] { 10, 20, 30, 255 }, image.Texture.ReadPixels());
    }

    [Fact]
    public void EncodeUnpremultipliesPremultipliedPixels()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        WriteBytes(bitmap.GetPixels(), new byte[] { 128, 0, 0, 128 });
        using var image = SKImage.FromBitmap(bitmap);

        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var decoded = SKBitmap.Decode(
            data,
            new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));

        Assert.Equal(new byte[] { 255, 0, 0, 128 }, ReadBytes(decoded.GetPixels(), 4));
    }

    [Fact]
    public void ScalePixelsWritesScaledDestinationPixmap()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        WriteBytes(bitmap.GetPixels(), new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        });
        using var image = SKImage.FromBitmap(bitmap);
        using var destination = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        WriteBytes(destination.GetPixels(), new byte[] { 9, 9, 9, 9 });

        image.ScalePixels(
            destination.PeekPixels(),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));

        Assert.Equal(new byte[] { 0, 0, 255, 255 }, ReadBytes(destination.GetPixels(), 4));
    }

    [Fact]
    public void ScalePixelsForcesOpaqueDestinationAlpha255()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        WriteBytes(bitmap.GetPixels(), new byte[] { 10, 20, 30, 64 });
        using var image = SKImage.FromBitmap(bitmap);
        using var rgbaDestination = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var bgraDestination = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Opaque));

        image.ScalePixels(
            rgbaDestination.PeekPixels(),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        image.ScalePixels(
            bgraDestination.PeekPixels(),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));

        Assert.Equal(new byte[] { 10, 20, 30, 255 }, ReadBytes(rgbaDestination.GetPixels(), 4));
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, ReadBytes(bgraDestination.GetPixels(), 4));
    }

    [Fact]
    public void DecodeCodecCopiesEncodedPixelsIntoBitmap()
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(TwoPixelPngBytes()));

        using var bitmap = SKBitmap.Decode(
            codec,
            new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Premul));

        Assert.Equal(2, bitmap.Width);
        Assert.Equal(1, bitmap.Height);
        Assert.Equal(8, bitmap.RowBytes);
        Assert.Equal(new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255
        }, ReadBytes(bitmap.GetPixels(), 8));
    }

    [Fact]
    public void DecodeCodecConvertsEncodedPixelsToRequestedBgraBitmap()
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(TwoPixelPngBytes()));

        using var bitmap = SKBitmap.Decode(
            codec,
            new SKImageInfo(2, 1, SKColorType.Bgra8888, SKAlphaType.Premul));

        Assert.Equal(SKColorType.Bgra8888, bitmap.ColorType);
        Assert.Equal(new byte[]
        {
            0, 0, 255, 255,
            0, 255, 0, 255
        }, ReadBytes(bitmap.GetPixels(), 8));
    }

    [Fact]
    public void DecodeCodecForcesOpaqueDestinationAlpha255()
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(SingleTransparentPixelPngBytes()));

        using var bitmap = SKBitmap.Decode(
            codec,
            new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Opaque));

        Assert.Equal(SKAlphaType.Opaque, bitmap.AlphaType);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, ReadBytes(bitmap.GetPixels(), 4));
    }

    [Fact]
    public void ReadPixelsClipsNegativeSourceOrigin()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        WriteBytes(bitmap.GetPixels(), new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        });
        using var image = SKImage.FromBitmap(bitmap);
        var dst = Marshal.AllocHGlobal(36);
        try
        {
            WriteBytes(dst, new byte[36]);

            image.ReadPixels(
                new SKImageInfo(3, 3, SKColorType.Rgba8888, SKAlphaType.Premul),
                dst,
                dstRowBytes: 12,
                srcX: -1,
                srcY: -1,
                SKImageCachingHint.Allow);

            Assert.Equal(new byte[]
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 255, 0, 0, 255, 0, 255, 0, 255,
                0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 255, 255
            }, ReadBytes(dst, 36));
        }
        finally
        {
            Marshal.FreeHGlobal(dst);
        }
    }

    [Fact]
    public void ReadPixelsReturnsFalseWhenDestinationStrideIsTooSmallForCopiedRange()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        WriteBytes(bitmap.GetPixels(), new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255
        });
        using var image = SKImage.FromBitmap(bitmap);
        var dst = Marshal.AllocHGlobal(8);
        try
        {
            WriteBytes(dst, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.False(image.ReadPixels(
                new SKImageInfo(3, 1, SKColorType.Rgba8888, SKAlphaType.Premul),
                dst,
                dstRowBytes: 8,
                srcX: -1,
                srcY: 0,
                SKImageCachingHint.Allow));

            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, ReadBytes(dst, 8));
        }
        finally
        {
            Marshal.FreeHGlobal(dst);
        }
    }

    [Fact]
    public void ReadPixelsReturnsFalseForZeroDestinationPointer()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        WriteBytes(bitmap.GetPixels(), new byte[] { 255, 0, 0, 255 });
        using var image = SKImage.FromBitmap(bitmap);

        Assert.False(image.ReadPixels(
            new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul),
            IntPtr.Zero,
            dstRowBytes: 4,
            srcX: 0,
            srcY: 0,
            SKImageCachingHint.Allow));
    }

    [Fact]
    public void ReadPixelsUnpremultipliesWhenDestinationRequestsUnpremul()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        WriteBytes(bitmap.GetPixels(), new byte[] { 128, 0, 0, 128 });
        using var image = SKImage.FromBitmap(bitmap);
        var dst = Marshal.AllocHGlobal(4);
        try
        {
            image.ReadPixels(
                new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                dst,
                dstRowBytes: 4,
                srcX: 0,
                srcY: 0,
                SKImageCachingHint.Allow);

            Assert.Equal(new byte[] { 255, 0, 0, 128 }, ReadBytes(dst, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(dst);
        }
    }

    [Fact]
    public void ReadPixelsUnpremultipliesPremulSourceWhenDestinationIsOpaque()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        WriteBytes(bitmap.GetPixels(), new byte[] { 128, 0, 0, 128 });
        using var image = SKImage.FromBitmap(bitmap);
        var rgbaDst = Marshal.AllocHGlobal(4);
        var bgraDst = Marshal.AllocHGlobal(4);
        try
        {
            image.ReadPixels(
                new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Opaque),
                rgbaDst,
                dstRowBytes: 4,
                srcX: 0,
                srcY: 0,
                SKImageCachingHint.Allow);

            image.ReadPixels(
                new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Opaque),
                bgraDst,
                dstRowBytes: 4,
                srcX: 0,
                srcY: 0,
                SKImageCachingHint.Allow);

            Assert.Equal(new byte[] { 255, 0, 0, 255 }, ReadBytes(rgbaDst, 4));
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, ReadBytes(bgraDst, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(bgraDst);
            Marshal.FreeHGlobal(rgbaDst);
        }
    }

    [Fact]
    public void ReadPixelsForcesOpaqueDestinationAlpha255()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        WriteBytes(bitmap.GetPixels(), new byte[] { 10, 20, 30, 64 });
        using var image = SKImage.FromBitmap(bitmap);
        var rgbaDst = Marshal.AllocHGlobal(4);
        var bgraDst = Marshal.AllocHGlobal(4);
        try
        {
            image.ReadPixels(
                new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Opaque),
                rgbaDst,
                dstRowBytes: 4,
                srcX: 0,
                srcY: 0,
                SKImageCachingHint.Allow);

            image.ReadPixels(
                new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Opaque),
                bgraDst,
                dstRowBytes: 4,
                srcX: 0,
                srcY: 0,
                SKImageCachingHint.Allow);

            Assert.Equal(new byte[] { 10, 20, 30, 255 }, ReadBytes(rgbaDst, 4));
            Assert.Equal(new byte[] { 30, 20, 10, 255 }, ReadBytes(bgraDst, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(bgraDst);
            Marshal.FreeHGlobal(rgbaDst);
        }
    }

    [Fact]
    public unsafe void DisposeDisposesOwnedImagesButLeavesBorrowedTexturesAlive()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        WriteBytes(bitmap.GetPixels(), new byte[] { 1, 2, 3, 4 });

        var ownedImage = SKImage.FromBitmap(bitmap);
        var ownedTexture = ownedImage.Texture;
        ownedImage.Dispose();
        Assert.True(ownedTexture.TexturePtr == null);

        using var context = new WgpuContext();
        context.Initialize(null);
        using var borrowedTexture = new GpuTexture(
            context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "Borrowed SKImage Test Texture");

        var borrowedImage = SKImage.FromTexture(borrowedTexture);
        borrowedImage.Dispose();
        Assert.True(borrowedTexture.TexturePtr != null);
    }

    [Fact]
    public void FromAdoptedTextureTakesOwnershipOfProGpuBackendTexture()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        var texture = new GpuTexture(
            context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "Adopted SKImage test texture");
        texture.WritePixels<byte>(new byte[] { 10, 20, 30, 255 });
        using var grContext = new GRContext(context);
        using var backendTexture = new GRBackendTexture(texture);

        var image = SKImage.FromAdoptedTexture(
            grContext,
            backendTexture,
            GRSurfaceOrigin.TopLeft,
            SKColorType.Rgba8888) ?? throw new InvalidOperationException("Failed to adopt backend texture.");

        Assert.Same(texture, image.Texture);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, image.Texture.ReadPixels());
        image.Dispose();
        Assert.True(texture.IsDisposed);
    }

    [Fact]
    public void FromTextureRequiresCopySrcForDeferredDrawImageRetention()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var texture = new GpuTexture(
            context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Borrowed SKImage Missing CopySrc Test Texture");

        var exception = Assert.Throws<InvalidOperationException>(() => SKImage.FromTexture(texture));
        Assert.Contains("CopySrc", exception.Message, StringComparison.Ordinal);
    }

    private static void WriteBytes(IntPtr destination, byte[] bytes)
    {
        Marshal.Copy(bytes, 0, destination, bytes.Length);
    }

    private static byte[] ReadBytes(IntPtr source, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(source, bytes, 0, length);
        return bytes;
    }

    private static byte[] TwoPixelPngBytes()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAADklEQVR4nGP4z8DwHwQBEPgD/U6VwW8AAAAASUVORK5CYII=");
    }

    private static byte[] SingleTransparentPixelPngBytes()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGPgEpFzAAAA5QB9CADYIgAAAABJRU5ErkJggg==");
    }

    private static byte[] AddPngGammaChunk(byte[] png, uint gammaTimes100000)
    {
        const int endOfHeaderChunk = 33;
        const int gammaChunkSize = 16;
        var result = new byte[png.Length + gammaChunkSize];
        png.AsSpan(0, endOfHeaderChunk).CopyTo(result);

        var chunk = result.AsSpan(endOfHeaderChunk, gammaChunkSize);
        BinaryPrimitives.WriteUInt32BigEndian(chunk, 4);
        "gAMA"u8.CopyTo(chunk[4..]);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[8..], gammaTimes100000);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[12..], ComputePngCrc32(chunk.Slice(4, 8)));

        png.AsSpan(endOfHeaderChunk).CopyTo(result.AsSpan(endOfHeaderChunk + gammaChunkSize));
        return result;
    }

    private static uint ComputePngCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }
}
