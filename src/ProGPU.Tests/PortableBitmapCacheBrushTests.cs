using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableBitmapCacheBrushTests
{
    [Fact]
    public void SharedCaptureDescriptorHasStableTargetAndNoConsumerState()
    {
        object target = new(), cache = new();
        var source = new PortableBitmapCacheBrushCaptureSource(target, cache);
        Assert.True(source.TryGetPortableBitmapCacheBrush(out var brush));
        Assert.Same(target, brush.InternalTarget);
        Assert.Same(cache, brush.BitmapCache);
        Assert.Equal(1, brush.Opacity);
        Assert.False(brush.HasTransform);
        Assert.False(brush.HasRelativeTransform);
    }

    [Fact]
    public void MappingUsesConsumerRelativeFrameThenAbsoluteTransformWithoutStretching()
    {
        var bounds = new PortableRect(10, 20, 100, 50);
        var brush = new PortableBitmapCacheBrush(null, HasRelativeTransform: true,
            RelativeTransform: new(2, 0, 0, 3, 0.25, 0.5),
            HasTransform: true, Transform: new(1, 0, 0, 1, 7, 9));
        Assert.True(PortableBitmapCacheBrushPolicy.TryGetMapping(brush, bounds, out var mapping));
        Assert.Equal(new System.Numerics.Vector2(42, 54),
            System.Numerics.Vector2.Transform(new(10, 20), mapping));
        Assert.True(PortableBitmapCacheBrushPolicy.TryGetMapping(new(null), bounds, out mapping));
        Assert.True(mapping.IsIdentity);
        Assert.False(PortableBitmapCacheBrushPolicy.TryGetMapping(brush with
        { Transform = new(double.NaN, 0, 0, 1, 0, 0) }, bounds, out _));
    }

    [Fact]
    public void CachePolicySelectsExplicitThenTargetThenDefaultAndIgnoresSnapping()
    {
        var targetCache = new CacheSource(new(3, true, false));
        var target = new VisualSource(new() { HasCacheMode = true, CacheMode = targetCache });
        Assert.True(PortableBitmapCacheBrushPolicy.TryResolve(new(target), out var policy));
        Assert.Equal(new PortableBitmapCache(3, false, false), policy);
        var explicitCache = new CacheSource(new(2, true, true));
        Assert.True(PortableBitmapCacheBrushPolicy.TryResolve(new(target, explicitCache), out policy));
        Assert.Equal(new PortableBitmapCache(2, false, true), policy);
        Assert.True(PortableBitmapCacheBrushPolicy.TryResolve(new(new VisualSource(new())), out policy));
        Assert.Equal(new PortableBitmapCache(1, false, false), policy);
        Assert.True(PortableBitmapCacheBrushPolicy.TryResolve(new(null), out policy));
        Assert.Equal(new PortableBitmapCache(1, false, false), policy);
    }

    [Fact]
    public void CachePolicyFailsClosedAndClampsNegativeScale()
    {
        Assert.False(PortableBitmapCacheBrushPolicy.TryResolve(new(new object()), out _));
        Assert.False(PortableBitmapCacheBrushPolicy.TryResolve(new(null, new object()), out _));
        Assert.False(PortableBitmapCacheBrushPolicy.TryResolve(new(new VisualSource(new() { HasCacheMode = true })), out _));
        Assert.False(PortableBitmapCacheBrushPolicy.TryResolve(new(null, new CacheSource(new(double.NaN, false, false))), out _));
        Assert.True(PortableBitmapCacheBrushPolicy.TryResolve(new(null, new CacheSource(new(-2, true, true))), out var policy));
        Assert.Equal(new PortableBitmapCache(0, false, true), policy);
    }

    private sealed class VisualSource(PortableVisualState value) : IPortableVisualStateSource
    {
        public bool TryGetPortableVisualState(out PortableVisualState state)
        { state = value; return true; }
    }

    private sealed class CacheSource(PortableBitmapCache value) : IPortableBitmapCacheSource
    {
        public bool TryGetPortableBitmapCache(out PortableBitmapCache cache)
        { cache = value; return true; }
    }

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
