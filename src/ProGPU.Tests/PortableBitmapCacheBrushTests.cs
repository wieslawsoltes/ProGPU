using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableBitmapCacheBrushTests
{
    [Fact]
    public void DefaultPolicyRetainsSourceIdentityWithoutTileMapping()
    {
        object target = new();
        var brush = new PortableBitmapCacheBrush(target);
        Assert.Same(target, brush.InternalTarget);
        Assert.Null(brush.BitmapCache);
        Assert.Equal(1, brush.Opacity);
        Assert.False(brush.HasTransform);
        Assert.False(brush.HasRelativeTransform);
    }

    [Fact]
    public void SnapshotPreservesExplicitPolicyAndUnvalidatedValues()
    {
        object cache = new();
        var transform = new PortableMatrix3x2(2, 0, 0, 3, 4, 5);
        var relative = new PortableMatrix3x2(1, 0, 0, 1, 0.25, 0.5);
        var brush = new PortableBitmapCacheBrush(null, cache, double.NaN,
            true, transform, true, relative);
        Assert.Null(brush.InternalTarget);
        Assert.Same(cache, brush.BitmapCache);
        Assert.True(double.IsNaN(brush.Opacity));
        Assert.Equal(transform, brush.Transform);
        Assert.Equal(relative, brush.RelativeTransform);
        Assert.True(brush.HasTransform);
        Assert.True(brush.HasRelativeTransform);
    }
}
