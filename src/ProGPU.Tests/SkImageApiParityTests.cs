using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkImageApiParityTests
{
    [Fact]
    public void PixelCopyHonorsStrideAndOwnsAnImmutableRasterView()
    {
        var info = new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        byte[] source =
        {
            255, 0, 0, 255, 0, 255, 0, 255, 99, 99, 99, 99,
            0, 0, 255, 255, 255, 255, 255, 255
        };

        using var image = SKImage.FromPixelCopy(info, source, 12);
        source.AsSpan().Clear();
        using var first = image.PeekPixels();
        using var second = image.PeekPixels();

        Assert.False(image.IsTextureBacked);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.GetPixels(), second.GetPixels());
        Assert.Equal(SKColors.Red, first.GetPixelColor(0, 0));
        Assert.Equal(SKColors.Blue, first.GetPixelColor(0, 1));
    }

    [Fact]
    public void RasterReleaseCallbackRunsOnceWithOriginalPointerAndContext()
    {
        var info = new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = Marshal.AllocHGlobal(4);
        Marshal.Copy(new byte[] { 1, 2, 3, 255 }, 0, pixels, 4);
        var releaseCount = 0;
        object releaseContext = new();

        using var pixmap = new SKPixmap(info, pixels);
        var image = SKImage.FromPixels(
            pixmap,
            (address, context) =>
            {
                Assert.Equal(pixels, address);
                Assert.Same(releaseContext, context);
                releaseCount++;
                Marshal.FreeHGlobal(address);
            },
            releaseContext);

        image.Dispose();
        image.Dispose();
        Assert.Equal(1, releaseCount);
    }

    [Fact]
    public void EncodedDataRetainsAnIndependentEncodedSnapshot()
    {
        byte[] encoded = TwoPixelPngBytes();
        using var image = SKImage.FromEncodedData(encoded);
        Assert.NotNull(image);
        encoded.AsSpan().Clear();

        using var first = image.EncodedData;
        using var second = image.EncodedData;
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(TwoPixelPngBytes(), first.ToArray());
        Assert.NotSame(first, second);
    }

    [Fact]
    public void SubsetSharesStorageAndReadsOnlyTheRequestedRectangle()
    {
        var info = new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var image = SKImage.FromPixelCopy(info, new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        });

        using var subset = image.Subset(new SKRectI(1, 0, 2, 2));
        Assert.NotNull(subset);
        Assert.False(subset.IsTextureBacked);
        Assert.Equal(1, subset.Width);
        Assert.Equal(2, subset.Height);
        Assert.Same(image.Texture, subset.Texture);
        using var pixels = subset.PeekPixels();
        Assert.Equal(SKColors.Lime, pixels.GetPixelColor(0, 0));
        Assert.Equal(SKColors.White, pixels.GetPixelColor(0, 1));
        Assert.Null(image.Subset(new SKRectI(-1, 0, 1, 1)));
    }

    [Fact]
    public void NestedSubsetRetainsSharedStorageAfterParentDisposal()
    {
        var image = SKImage.FromPixelCopy(
            new SKImageInfo(3, 2, SKColorType.Rgba8888, SKAlphaType.Premul),
            new byte[]
            {
                255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255,
                255, 255, 0, 255, 0, 255, 255, 255, 255, 0, 255, 255
            });
        using var first = image.Subset(new SKRectI(1, 0, 3, 2));
        using var nested = first.Subset(new SKRectI(1, 1, 2, 2));

        Assert.Same(image.Texture, first.Texture);
        Assert.Same(image.Texture, nested.Texture);
        image.Dispose();
        first.Dispose();

        Assert.False(nested.Texture.IsDisposed);
        using var pixels = nested.PeekPixels();
        Assert.Equal(SKColors.Magenta, pixels.GetPixelColor(0, 0));
        using var context = new GRContext(nested.Texture.Context);
        using var textureCopy = nested.ToTextureImage(context);
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, textureCopy.Texture.ReadPixels());
    }

    [Fact]
    public void BoundedSubsetViewAllocationRemainsCompact()
    {
        using var image = SKImage.FromPixelCopy(
            new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul),
            Enumerable.Repeat((byte)255, 64).ToArray());
        var subsetBounds = new SKRectI(1, 1, 3, 3);
        const int iterations = 10_000;

        for (var index = 0; index < 2_000; index++)
        {
            using var warmup = image.Subset(subsetBounds);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0;
        for (var index = 0; index < iterations; index++)
        {
            using var subset = image.Subset(subsetBounds);
            checksum += subset.Width + subset.Height;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(iterations * 4, checksum);
        Assert.True(
            allocated <= iterations * 72L,
            $"Expected one compact immutable image view per subset, but measured {allocated / (double)iterations:F3} B/op.");
    }

    [Fact]
    public void SurfaceSnapshotsShareOneImmutableTexturePerContentGeneration()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Red);

        using var first = surface.Snapshot(new SKRectI(0, 0, 1, 1));
        using var second = surface.Snapshot(new SKRectI(1, 0, 2, 1));

        Assert.Same(first.Texture, second.Texture);
        Assert.Equal((uint)2, first.Texture.Width);
        Assert.Equal((uint)1, first.Texture.Height);

        surface.Canvas.Clear(SKColors.Blue);
        using var nextGeneration = surface.Snapshot(new SKRectI(0, 0, 1, 1));

        Assert.NotSame(first.Texture, nextGeneration.Texture);
        Assert.False(first.Texture.IsDisposed);
        using var firstRaster = first.ToRasterImage();
        using var nextRaster = nextGeneration.ToRasterImage();
        using var firstPixels = firstRaster.PeekPixels();
        using var nextPixels = nextRaster.PeekPixels();
        Assert.Equal(SKColors.Red, firstPixels.GetPixelColor(0, 0));
        Assert.Equal(SKColors.Blue, nextPixels.GetPixelColor(0, 0));
    }

    [Fact]
    public void DrawingSubsetMaterializesOnlyItsComposedTextureRegion()
    {
        using var image = SKImage.FromPixelCopy(
            new SKImageInfo(3, 2, SKColorType.Rgba8888, SKAlphaType.Premul),
            new byte[]
            {
                255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255,
                255, 255, 0, 255, 0, 255, 255, 255, 255, 0, 255, 255
            });
        using var first = image.Subset(new SKRectI(1, 0, 3, 2));
        using var nested = first.Subset(new SKRectI(0, 1, 2, 2));
        using var surface = SKSurface.Create(
            new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Premul));

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(nested, 0, 0);
        surface.Canvas.Flush();
        using var snapshot = surface.Snapshot();
        using var result = snapshot.ToRasterImage();
        using var pixels = result.PeekPixels();

        Assert.Equal(SKColors.Cyan, pixels.GetPixelColor(0, 0));
        Assert.Equal(SKColors.Magenta, pixels.GetPixelColor(1, 0));
    }

    [Fact]
    public void TextureBackedSubsetRequiresAndRetainsTheMatchingContext()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Blue);
        using var image = surface.Snapshot();
        using var context = new GRContext(image.Texture.Context);

        Assert.True(image.IsTextureBacked);
        Assert.Null(image.Subset(new SKRectI(0, 0, 1, 1)));
        using var subset = image.Subset(
            context,
            new SKRectI(0, 0, 1, 1));
        Assert.NotNull(subset);
        Assert.True(subset.IsTextureBacked);
        Assert.Same(image.Texture.Context, subset.Texture.Context);
        Assert.Same(image.Texture, subset.Texture);
        using var raster = subset.ToRasterImage();
        using var pixels = raster.PeekPixels();
        Assert.Equal(SKColors.Blue, pixels.GetPixelColor(0, 0));
    }

    [Fact]
    public void BorrowedTextureReleaseDoesNotDisposeCallerTexture()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var texture = new GpuTexture(
            context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "SKImage borrowed release test");
        using var recordingContext = new GRContext(context);
        using var backendTexture = new GRBackendTexture(texture);
        int releaseCount = 0;
        object releaseContext = new();

        using var colorSpace = SKColorSpace.CreateSrgb();
        var image = SKImage.FromTexture(
            recordingContext,
            backendTexture,
            GRSurfaceOrigin.TopLeft,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            colorSpace,
            contextValue =>
            {
                Assert.Same(releaseContext, contextValue);
                releaseCount++;
            },
            releaseContext);

        Assert.NotNull(image);
        using var subset = image.Subset(recordingContext, new SKRectI(0, 0, 1, 1));
        Assert.NotNull(subset);
        image.Dispose();
        image.Dispose();
        Assert.Equal(0, releaseCount);
        Assert.False(texture.IsDisposed);
        subset.Dispose();
        Assert.Equal(1, releaseCount);
        Assert.False(texture.IsDisposed);
    }

    [Fact]
    public void ToTextureImageUsesGpuCopyAndCanGenerateMipmaps()
    {
        using var source = SKImage.FromPixelCopy(
            new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul),
            Enumerable.Repeat((byte)255, 64).ToArray());
        using var context = new GRContext(source.Texture.Context);

        using var result = source.ToTextureImage(context, mipmapped: true, budgeted: false);

        Assert.NotNull(result);
        Assert.True(result.IsTextureBacked);
        Assert.NotSame(source.Texture, result.Texture);
        Assert.Equal(3u, result.Texture.MipLevelCount);
        Assert.Equal(source.Texture.ReadPixels(), result.Texture.ReadPixels());
    }

    [Fact]
    public void ApplyImageFilterReturnsClippedGpuResultAndOffset()
    {
        using var image = SKImage.FromPixelCopy(
            new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul),
            Enumerable.Repeat((byte)255, 16).ToArray());
        using var filter = SKImageFilter.CreateOffset(0, 0);

        using var result = image.ApplyImageFilter(
            filter,
            new SKRectI(0, 0, 2, 2),
            new SKRectI(1, 0, 2, 2),
            out var outputSubset,
            out SKPointI outputOffset);

        Assert.NotNull(result);
        Assert.Equal(new SKRectI(1, 0, 2, 2), outputSubset);
        Assert.Equal(new SKPointI(1, 0), outputOffset);
        Assert.Equal(1, result.Width);
        Assert.Equal(2, result.Height);
    }

    private static byte[] TwoPixelPngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAADklEQVR4nGP4z8DwHwQBEPgD/U6VwW8AAAAASUVORK5CYII=");
}
