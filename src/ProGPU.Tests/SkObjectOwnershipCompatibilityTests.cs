using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkObjectOwnershipCompatibilityTests
{
    [Fact]
    public void CpuResourceViewsUseTheOfficialSkObjectOwnershipHierarchy()
    {
        Assert.Equal(typeof(SKObject), typeof(SKData).BaseType);
        Assert.Equal(typeof(SKObject), typeof(SKFont).BaseType);
        Assert.Equal(typeof(SKObject), typeof(SKPixmap).BaseType);
        Assert.Equal(typeof(SKObject), typeof(SKRegion).BaseType);
        Assert.Equal(typeof(SKObject), typeof(SKRegion.RectIterator).BaseType);
        Assert.Equal(typeof(SKObject), typeof(SKRegion.ClipIterator).BaseType);
        Assert.Equal(typeof(SKObject), typeof(SKRegion.SpanIterator).BaseType);

        var data = SKData.CreateCopy(new byte[] { 1 });
        var font = new SKFont();
        var pixmap = new SKPixmap();
        var region = new SKRegion(new SKRectI(0, 0, 1, 1));
        Assert.NotEqual(IntPtr.Zero, data.Handle);
        Assert.NotEqual(IntPtr.Zero, font.Handle);
        Assert.NotEqual(IntPtr.Zero, pixmap.Handle);
        Assert.NotEqual(IntPtr.Zero, region.Handle);

        data.Dispose();
        font.Dispose();
        pixmap.Dispose();
        region.Dispose();
        Assert.Equal(IntPtr.Zero, data.Handle);
        Assert.Equal(IntPtr.Zero, font.Handle);
        Assert.Equal(IntPtr.Zero, pixmap.Handle);
        Assert.Equal(IntPtr.Zero, region.Handle);
    }
}
