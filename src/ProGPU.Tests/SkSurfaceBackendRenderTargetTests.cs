using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Reflection;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkSurfaceBackendRenderTargetTests
{
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
        var field = typeof(SKSurface).GetField(
            "_compositorCache",
            BindingFlags.Static | BindingFlags.NonPublic);
        var cache = (IDictionary)field!.GetValue(null)!;
        lock (cache)
        {
            return cache.Contains(context);
        }
    }

    private static TextureFormat[] SurfaceCompositorCacheFormats(WgpuContext context)
    {
        var field = typeof(SKSurface).GetField(
            "_compositorCache",
            BindingFlags.Static | BindingFlags.NonPublic);
        var cache = (IDictionary)field!.GetValue(null)!;
        lock (cache)
        {
            var formatCache = (IDictionary?)cache[context];
            if (formatCache == null)
            {
                return Array.Empty<TextureFormat>();
            }

            var formats = new TextureFormat[formatCache.Keys.Count];
            formatCache.Keys.CopyTo(formats, 0);
            return formats;
        }
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
