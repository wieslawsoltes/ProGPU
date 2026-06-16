using System;
using System.Runtime.InteropServices;
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

    private static void AssertPixel(byte[] pixels, int width, int x, int y, byte r, byte g, byte b, byte a)
    {
        int index = ((y * width) + x) * 4;
        Assert.Equal(r, pixels[index]);
        Assert.Equal(g, pixels[index + 1]);
        Assert.Equal(b, pixels[index + 2]);
        Assert.Equal(a, pixels[index + 3]);
    }
}
