using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Reflection;
using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.WebGPU;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkSurfaceBackendRenderTargetTests
{
    [Fact]
    public void CreateFromProGpuBackendTextureFlushesWithoutTakingOwnership()
    {
        using var grContext = GRContext.CreateGl() ?? throw new InvalidOperationException("Failed to create GRContext.");
        using var texture = new GpuTexture(
            grContext.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface wrapped backend texture test");
        using var backendTexture = new GRBackendTexture(texture);

        using (var surface = SKSurface.Create(
            grContext,
            backendTexture,
            GRSurfaceOrigin.TopLeft,
            SKColorType.Rgba8888) ?? throw new InvalidOperationException("Failed to wrap backend texture."))
        {
            surface.Canvas.Clear(SKColors.Green);
            surface.Flush();
        }

        Assert.False(texture.IsDisposed);
        AssertPixel(texture.ReadPixels(), 4, 0, 0, 0, 128, 0, 255);
    }

    [Fact]
    public void CreateFromProGpuBackendRenderTargetFlushesIntoWrappedTexture()
    {
        using var grContext = GRContext.CreateGl() ?? throw new InvalidOperationException("Failed to create GRContext.");
        using var texture = new GpuTexture(
            grContext.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface wrapped render target test");
        using var renderTarget = new GRBackendRenderTarget(4, 4, texture);
        using var surface = SKSurface.Create(grContext, renderTarget, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888);

        surface.Canvas.Clear(SKColors.Red);
        surface.Flush();

        var pixels = texture.ReadPixels();
        Assert.Equal(255, pixels[0]);
        Assert.Equal(0, pixels[1]);
        Assert.Equal(0, pixels[2]);
        Assert.Equal(255, pixels[3]);
    }

    [Fact]
    public void CreateFromBgraBackendRenderTargetUsesTextureFormatCompositor()
    {
        using var grContext = GRContext.CreateGl() ?? throw new InvalidOperationException("Failed to create GRContext.");
        using var rgbaSurface = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        rgbaSurface.Canvas.Clear(SKColors.Blue);
        rgbaSurface.Flush();

        using var texture = new GpuTexture(
            grContext.Context,
            4,
            4,
            TextureFormat.Bgra8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface wrapped BGRA render target test");
        using var renderTarget = new GRBackendRenderTarget(4, 4, texture);
        using var surface = SKSurface.Create(grContext, renderTarget, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888);

        surface.Canvas.Clear(SKColors.Red);
        surface.Flush();

        var formats = SurfaceCompositorCacheFormats(grContext.Context);
        Assert.Contains(TextureFormat.Rgba8Unorm, formats);
        Assert.Contains(TextureFormat.Bgra8Unorm, formats);

        var pixels = texture.ReadPixels();
        Assert.Equal(0, pixels[0]);
        Assert.Equal(0, pixels[1]);
        Assert.Equal(255, pixels[2]);
        Assert.Equal(255, pixels[3]);
    }

    [Fact]
    public void SnapshotFromBgraBackendRenderTargetPreservesTextureFormat()
    {
        using var grContext = GRContext.CreateGl() ?? throw new InvalidOperationException("Failed to create GRContext.");
        using var texture = new GpuTexture(
            grContext.Context,
            4,
            4,
            TextureFormat.Bgra8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface wrapped BGRA snapshot test");
        using var renderTarget = new GRBackendRenderTarget(4, 4, texture);
        using var surface = SKSurface.Create(grContext, renderTarget, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888);

        surface.Canvas.Clear(SKColors.Red);
        using var snapshot = surface.Snapshot();

        Assert.Equal(TextureFormat.Bgra8Unorm, snapshot.Texture.Format);
        var pixels = snapshot.Texture.ReadPixels();
        Assert.Equal(0, pixels[0]);
        Assert.Equal(0, pixels[1]);
        Assert.Equal(255, pixels[2]);
        Assert.Equal(255, pixels[3]);
    }

    [Fact]
    public void SnapshotFromBgraBackendRenderTargetReadPixelsConvertsToRequestedRgba()
    {
        using var grContext = GRContext.CreateGl() ?? throw new InvalidOperationException("Failed to create GRContext.");
        using var texture = new GpuTexture(
            grContext.Context,
            4,
            4,
            TextureFormat.Bgra8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface wrapped BGRA snapshot readback test");
        using var renderTarget = new GRBackendRenderTarget(4, 4, texture);
        using var surface = SKSurface.Create(grContext, renderTarget, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888);

        surface.Canvas.Clear(SKColors.Red);
        using var snapshot = surface.Snapshot();
        var pixels = Marshal.AllocHGlobal(4);
        try
        {
            snapshot.ReadPixels(
                new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul),
                pixels,
                dstRowBytes: 4,
                srcX: 0,
                srcY: 0,
                SKImageCachingHint.Allow);

            var readback = new byte[4];
            Marshal.Copy(pixels, readback, 0, readback.Length);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, readback);
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }

    [Fact]
    public void RepeatedFlushesPreserveExistingGpuSurfaceContents()
    {
        using var surface = SKSurface.Create(new SKImageInfo(8, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var red = new SKPaint { Color = SKColors.Red };
        using var blue = new SKPaint { Color = SKColors.Blue };

        surface.Canvas.DrawRect(0, 0, 4, 4, red);
        surface.Flush();

        surface.Canvas.DrawRect(4, 0, 4, 4, blue);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertPixel(pixels, 8, 1, 1, 255, 0, 0, 255);
        AssertPixel(pixels, 8, 6, 1, 0, 0, 255, 255);
    }

    [Theory]
    [InlineData(SKBlendMode.Multiply)]
    [InlineData(SKBlendMode.Screen)]
    [InlineData(SKBlendMode.Darken)]
    [InlineData(SKBlendMode.Lighten)]
    [InlineData(SKBlendMode.Exclusion)]
    [InlineData(SKBlendMode.Overlay)]
    [InlineData(SKBlendMode.ColorDodge)]
    [InlineData(SKBlendMode.ColorBurn)]
    [InlineData(SKBlendMode.HardLight)]
    [InlineData(SKBlendMode.SoftLight)]
    [InlineData(SKBlendMode.Difference)]
    [InlineData(SKBlendMode.Hue)]
    [InlineData(SKBlendMode.Saturation)]
    [InlineData(SKBlendMode.Color)]
    [InlineData(SKBlendMode.Luminosity)]
    public void AdvancedImageBlendPreservesBackdropForTransparentSource(SKBlendMode blendMode)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var source = CreateRgbaImage(23, 47, 91, 0);
        using var paint = new SKPaint
        {
            BlendMode = blendMode,
            IsAntialias = false
        };

        surface.Canvas.Clear(new SKColor(200, 100, 50, 255));
        surface.Canvas.DrawImage(
            source,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(0f, 0f, 4f, 4f),
            paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        AssertPixel(snapshot.Texture.ReadPixels(), 4, 2, 2, 200, 100, 50, 255);
    }

    [Fact]
    public void DrawImageAppliesPaintColorMatrixOnGpu()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var source = CreateRgbaImage(255, 0, 0, 255);
        using var colorFilter = SKColorFilter.CreateColorMatrix(new[]
        {
            0f, 0f, 1f, 0f, 0f,
            0f, 1f, 0f, 0f, 0f,
            1f, 0f, 0f, 0f, 0f,
            0f, 0f, 0f, 1f, 0f
        });
        using var paint = new SKPaint
        {
            ColorFilter = colorFilter,
            IsAntialias = false
        };

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(
            source,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(0f, 0f, 4f, 4f),
            paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        AssertPixel(snapshot.Texture.ReadPixels(), 4, 2, 2, 0, 0, 255, 255);
    }

    [Fact]
    public void ChainedAdvancedImageBlendsPreserveDrawOrder()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var first = CreateRgbaImage(50, 20, 10, 255);
        using var second = CreateRgbaImage(10, 30, 50, 255);
        using var paint = new SKPaint
        {
            BlendMode = SKBlendMode.Difference,
            IsAntialias = false
        };

        surface.Canvas.Clear(new SKColor(200, 100, 50, 255));
        var source = new SKRect(0f, 0f, 1f, 1f);
        var destination = new SKRect(0f, 0f, 4f, 4f);
        surface.Canvas.DrawImage(first, source, destination, paint);
        surface.Canvas.DrawImage(second, source, destination, paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        AssertPixel(snapshot.Texture.ReadPixels(), 4, 2, 2, 140, 50, 10, 255);
    }

    [Fact]
    public void CpuBackedSurfaceReadbackHonorsDisjointRegionClip()
    {
        const int width = 8;
        const int height = 4;
        var pixels = Marshal.AllocHGlobal(width * height * 4);
        try
        {
            Marshal.Copy(new byte[width * height * 4], 0, pixels, width * height * 4);
            using var surface = SKSurface.Create(
                new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
                pixels,
                width * 4);
            using var yellow = new SKPaint { Color = new SKColor(255, 255, 0, 255) };
            using var red = new SKPaint { Color = SKColors.Red };

            surface.Canvas.DrawRect(0f, 0f, width, height, yellow);
            surface.Flush();
            Marshal.Copy(new byte[width * height * 4], 0, pixels, width * height * 4);

            using var region = new SKRegion();
            region.SetRect(new SKRectI(0, 0, 2, height));
            region.Op(new SKRectI(6, 0, width, height), SKRegionOperation.Union);
            surface.Canvas.Save();
            surface.Canvas.ClipRegion(region);
            surface.Canvas.DrawRect(0f, 0f, width, height, red);
            surface.Canvas.Restore();
            surface.Flush();

            var readback = new byte[width * height * 4];
            Marshal.Copy(pixels, readback, 0, readback.Length);
            AssertPixel(readback, width, 1, 1, 255, 0, 0, 255);
            AssertPixel(readback, width, 4, 1, 0, 0, 0, 0);
            AssertPixel(readback, width, 7, 1, 255, 0, 0, 255);
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void CreateRejectsInvalidImageInfoDimensions(int width, int height)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)));

        Assert.Equal("info", exception.ParamName);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void CpuBackedCreateRejectsInvalidImageInfoDimensions(int width, int height)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => SKSurface.Create(
                new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
                IntPtr.Zero,
                rowBytes: 0));

        Assert.Equal("info", exception.ParamName);
    }

    [Fact]
    public void CreateFromUnsupportedNativeBackendRenderTargetFailsExplicitly()
    {
        using var grContext = GRContext.CreateGl() ?? throw new InvalidOperationException("Failed to create GRContext.");
        using var renderTarget = new GRBackendRenderTarget(4, 4, 1, 0, new GRGlFramebufferInfo(123, 0));

        var exception = Assert.Throws<NotSupportedException>(
            () => SKSurface.Create(grContext, renderTarget, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888));
        Assert.Contains("GpuTexture", exception.Message);
    }

    [Fact]
    public void CreateFromBackendRenderTargetRequiresCopySrcForSnapshot()
    {
        using var grContext = GRContext.CreateGl() ?? throw new InvalidOperationException("Failed to create GRContext.");
        using var texture = new GpuTexture(
            grContext.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface wrapped render target missing CopySrc test");
        using var renderTarget = new GRBackendRenderTarget(4, 4, texture);

        var exception = Assert.Throws<InvalidOperationException>(
            () => SKSurface.Create(grContext, renderTarget, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888));
        Assert.Contains("CopySrc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFromBackendRenderTargetRejectsMultisampledTargets()
    {
        using var grContext = GRContext.CreateGl() ?? throw new InvalidOperationException("Failed to create GRContext.");
        using var texture = new GpuTexture(
            grContext.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface wrapped render target multisample test");
        using var renderTarget = new GRBackendRenderTarget(4, 4, sampleCount: 4, texture);

        var exception = Assert.Throws<NotSupportedException>(
            () => SKSurface.Create(grContext, renderTarget, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888));
        Assert.Contains("single-sampled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFromBottomLeftBackendRenderTargetFlipsIntoWrappedTexture()
    {
        using var grContext = GRContext.CreateGl() ?? throw new InvalidOperationException("Failed to create GRContext.");
        using var texture = new GpuTexture(
            grContext.Context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.CopyDst | TextureUsage.TextureBinding,
            "SKSurface bottom-left render target test");
        using var renderTarget = new GRBackendRenderTarget(4, 4, texture);
        using var surface = SKSurface.Create(grContext, renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
        using var paint = new SKPaint { Color = SKColors.Red };

        surface.Canvas.DrawRect(0f, 0f, 4f, 1f, paint);
        surface.Flush();

        var pixels = texture.ReadPixels();
        AssertPixel(pixels, 4, 1, 0, 0, 0, 0, 0);
        AssertPixel(pixels, 4, 1, 3, 255, 0, 0, 255);
    }

    [Fact]
    public void CpuBackedUnpremulSurfaceConvertsAtGpuBoundary()
    {
        var pixels = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(new byte[] { 255, 0, 0, 128 }, 0, pixels, 4);

            using var surface = SKSurface.Create(
                new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                pixels,
                rowBytes: 4);

            using (var snapshot = surface.Snapshot())
            {
                var rawGpuPixels = snapshot.Texture.ReadPixels();
                Assert.Equal(new byte[] { 128, 0, 0, 128 }, rawGpuPixels);
            }

            surface.Canvas.Clear(new SKColor(0, 255, 0, 128));
            surface.Flush();

            var cpuPixels = new byte[4];
            Marshal.Copy(pixels, cpuPixels, 0, cpuPixels.Length);
            Assert.Equal(0, cpuPixels[0]);
            Assert.Equal(255, cpuPixels[1]);
            Assert.Equal(0, cpuPixels[2]);
            Assert.InRange(cpuPixels[3], 96, 136);
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }

    [Fact]
    public void CpuBackedOpaqueSurfaceUnpremultipliesBeforeForcingAlpha()
    {
        var pixels = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(new byte[] { 0, 0, 0, 255 }, 0, pixels, 4);

            using var surface = SKSurface.Create(
                new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Opaque),
                pixels,
                rowBytes: 4);

            surface.Canvas.Clear(new SKColor(255, 0, 0, 128));
            surface.Flush();

            var cpuPixels = new byte[4];
            Marshal.Copy(pixels, cpuPixels, 0, cpuPixels.Length);
            Assert.InRange(cpuPixels[0], 240, 255);
            Assert.Equal(0, cpuPixels[1]);
            Assert.Equal(0, cpuPixels[2]);
            Assert.Equal(255, cpuPixels[3]);
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }

    [Fact]
    public void CpuBackedSurfaceRejectsRowBytesSmallerThanPixelWidth()
    {
        var pixels = Marshal.AllocHGlobal(8);
        try
        {
            var exception = Assert.Throws<ArgumentException>(
                () => SKSurface.Create(
                    new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Premul),
                    pixels,
                    rowBytes: 4));

            Assert.Equal("rowBytes", exception.ParamName);
            Assert.Contains("Row bytes", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }

    [Fact]
    public void SurfaceCompositorDisposingHandlerClearsContextCache()
    {
        var context = new WgpuContext();
        context.Initialize(null);
        DetachActiveContextForTest(context);
        try
        {
            using var grContext = new GRContext(context);
            using var surface = SKSurface.Create(
                grContext,
                false,
                new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));

            surface.Canvas.Clear(SKColors.Red);
            surface.Flush();

            Assert.True(SurfaceCompositorCacheContains(context));

            RemoveSurfaceCachedCompositor(context);

            Assert.False(SurfaceCompositorCacheContains(context));
        }
        finally
        {
            context.Dispose();
        }
    }

    [Fact]
    public void CpuBackedSurfaceReusesReadbackResourcesAcrossFlushes()
    {
        var pixels = Marshal.AllocHGlobal(16);
        try
        {
            using var surface = SKSurface.Create(
                new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul),
                pixels,
                rowBytes: 8);
            surface.Canvas.Clear(SKColors.Red);
            surface.Flush();

            var firstBuffer = GetPrivateField<GpuTextureReadbackBuffer>(surface, "_readbackBuffer");
            var firstPixels = GetPrivateField<byte[]>(surface, "_readbackPixels");

            surface.Canvas.Clear(SKColors.Blue);
            surface.Flush();

            Assert.Same(firstBuffer, GetPrivateField<GpuTextureReadbackBuffer>(surface, "_readbackBuffer"));
            Assert.Same(firstPixels, GetPrivateField<byte[]>(surface, "_readbackPixels"));
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }

    private static void DetachActiveContextForTest(WgpuContext context)
    {
        WgpuContext.Current = null;
        var field = typeof(WgpuContext).GetField(
            "_activeContexts",
            BindingFlags.Static | BindingFlags.NonPublic);
        var activeContexts = (IList)field!.GetValue(null)!;
        lock (activeContexts)
        {
            activeContexts.Remove(context);
        }
    }

    private static void RemoveSurfaceCachedCompositor(WgpuContext context)
    {
        var method = typeof(SKSurface).GetMethod(
            "RemoveCachedCompositor",
            BindingFlags.Static | BindingFlags.NonPublic);
        method!.Invoke(null, new object[] { context });
    }

    private static bool SurfaceCompositorCacheContains(WgpuContext context)
    {
        var field = typeof(SharedCompositorCache).GetField(
            "s_compositors",
            BindingFlags.Static | BindingFlags.NonPublic);
        var cache = (IDictionary)field!.GetValue(null)!;
        lock (cache)
        {
            var scopedCache = (IDictionary?)cache[context];
            return scopedCache?.Contains(GetSurfaceCompositorCacheScope()) == true;
        }
    }

    private static TextureFormat[] SurfaceCompositorCacheFormats(WgpuContext context)
    {
        var field = typeof(SharedCompositorCache).GetField(
            "s_compositors",
            BindingFlags.Static | BindingFlags.NonPublic);
        var cache = (IDictionary)field!.GetValue(null)!;
        lock (cache)
        {
            var scopedCache = (IDictionary?)cache[context];
            var formatCache = (IDictionary?)scopedCache?[GetSurfaceCompositorCacheScope()];
            if (formatCache == null)
            {
                return Array.Empty<TextureFormat>();
            }

            var formats = new TextureFormat[formatCache.Keys.Count];
            formatCache.Keys.CopyTo(formats, 0);
            return formats;
        }
    }

    private static object GetSurfaceCompositorCacheScope()
    {
        var field = typeof(SKSurface).GetField(
            "s_compositorCacheScope",
            BindingFlags.Static | BindingFlags.NonPublic);
        return field!.GetValue(null)!;
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static SKImage CreateRgbaImage(byte red, byte green, byte blue, byte alpha)
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(new[] { red, green, blue, alpha }, 0, bitmap.GetPixels(), 4);
        return SKImage.FromBitmap(bitmap);
    }

    private static void AssertPixel(byte[] pixels, int width, int x, int y, byte r, byte g, byte b, byte a)
    {
        int index = ((y * width) + x) * 4;
        Assert.Equal(r, pixels[index]);
        Assert.Equal(g, pixels[index + 1]);
        Assert.Equal(b, pixels[index + 2]);
        Assert.Equal(a, pixels[index + 3]);
    }
}
