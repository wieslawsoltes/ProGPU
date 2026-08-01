using System.Runtime.InteropServices;
using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkSurfaceApiParityTests
{
    [Fact]
    public void SurfaceUsesOfficialObjectOwnershipAndPropertySnapshot()
    {
        using var props = new SKSurfaceProperties(
            SKSurfacePropsFlags.UseDeviceIndependentFonts,
            SKPixelGeometry.BgrVertical);
        using var surface = SKSurface.Create(
            new SKImageInfo(4, 3, SKColorType.Rgba8888, SKAlphaType.Premul),
            props);

        Assert.IsAssignableFrom<SKObject>(surface);
        Assert.Null(surface.Context);
        Assert.NotSame(props, surface.SurfaceProperties);
        Assert.Equal(props.Flags, surface.SurfaceProperties.Flags);
        Assert.Equal(props.PixelGeometry, surface.SurfaceProperties.PixelGeometry);
    }

    [Fact]
    public void ExternalPixelsArePeekedWithoutCopyAndReleasedExactlyOnce()
    {
        var pixels = Marshal.AllocHGlobal(32);
        var releaseCount = 0;
        object releaseContext = new();
        var surface = SKSurface.Create(
            new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul),
            pixels,
            16,
            (address, context) =>
            {
                Assert.Equal(pixels, address);
                Assert.Same(releaseContext, context);
                releaseCount++;
                Marshal.FreeHGlobal(address);
            },
            releaseContext);

        using (var pixmap = surface.PeekPixels())
        {
            Assert.NotNull(pixmap);
            Assert.Equal(pixels, pixmap.GetPixels());
            Assert.Equal(16, pixmap.RowBytes);
        }

        surface.Dispose();
        surface.Dispose();
        Assert.Equal(1, releaseCount);
    }

    [Fact]
    public void BoundedSnapshotStaysOnGpuUntilExplicitReadback()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Red);

        using var snapshot = surface.Snapshot(new SKRectI(1, 1, 3, 3));
        Assert.Equal(2, snapshot.Width);
        Assert.Equal(2, snapshot.Height);
        Assert.True(snapshot.IsTextureBacked);

        var pixels = Marshal.AllocHGlobal(16);
        try
        {
            Assert.True(snapshot.ReadPixels(
                new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul),
                pixels,
                8));
            var bytes = new byte[16];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            Assert.All(Enumerable.Range(0, 4), index =>
            {
                Assert.Equal(255, bytes[index * 4]);
                Assert.Equal(0, bytes[index * 4 + 1]);
                Assert.Equal(0, bytes[index * 4 + 2]);
                Assert.Equal(255, bytes[index * 4 + 3]);
            });
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }

    [Fact]
    public void RasterSurfaceCreatesOneLazyStableCpuView()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Blue);

        using var first = surface.PeekPixels();
        using var second = surface.PeekPixels();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(IntPtr.Zero, first.GetPixels());
        Assert.Equal(first.GetPixels(), second.GetPixels());
        Assert.Equal(SKColors.Blue, first.GetPixelColor(0, 0));
    }

    [Fact]
    public void NullSurfaceDiscardsCommandsAndHasNoPixels()
    {
        using var surface = SKSurface.CreateNull(16, 8);
        var contextField = typeof(SKSurface).GetField(
            "_context",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(contextField);
        Assert.Null(contextField.GetValue(surface));
        surface.Canvas.Clear(SKColors.Red);
        surface.Flush();
        using var pixmap = new SKPixmap();
        Assert.False(surface.PeekPixels(pixmap));
        Assert.Null(surface.PeekPixels());
        Assert.Throws<InvalidOperationException>(() => surface.Snapshot());
    }
}
