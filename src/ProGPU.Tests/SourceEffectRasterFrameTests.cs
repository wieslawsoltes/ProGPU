using System;
using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public sealed class SourceEffectRasterFrameTests
{
    [Fact]
    public void OutwardCaptureUsesOriginalEdgesAndIndependentPhysicalAxes()
    {
        var source = new ShaderEffectSourceCapture(.25, -.75, 1.5, 1.25, .125, .375, .125, .375);
        Assert.True(EffectCaptureFrame.TryCreateSource(source, Vector2.Zero, new Vector2(2, 4), 1, out var frame));
        // Original inflated edges are (.125, -.875, 2.125, .875).
        // Their physical edges are (.25, -3.5, 4.25, 3.5), so the
        // outward pixel rectangle is (0, -4, 5, 4), not ceil(size*DPI).
        Assert.Equal(new Rect(.125f, -.875f, 2, 1.75f), frame.PaddedBounds);
        Assert.Equal(5U, frame.PixelWidth);
        Assert.Equal(8U, frame.PixelHeight);
        Assert.True(frame.HasPhysicalOrigin);
        Assert.Equal(new Vector2(2, 4), frame.PixelsPerUnit);
        Assert.Equal(new Vector2(0, -4), frame.PhysicalOrigin);
        Assert.Equal(new Rect(0, -1, 2.5f, 2), frame.RasterBounds);
        Assert.Equal(new Vector4(.05f, .0625f, .85f, .9375f), frame.TextureUvBounds);
        Assert.Equal(new Vector2(.25f, .5f),
            Vector2.Transform(frame.PaddedBounds.Position, frame.SourceToRaster));
        Assert.Equal(Vector2.Zero, Vector2.Transform(frame.RasterBounds.Position, frame.SourceToRaster));
    }

    [Fact]
    public void ActualSourceRebaseDoesNotSelectADifferentPhysicalLattice()
    {
        var source = new ShaderEffectSourceCapture(10.25, 20.5, 1, 1, 0, 0, 0, 0);
        Assert.True(EffectCaptureFrame.TryCreateSource(source, Vector2.Zero, new Vector2(2), 1, out var original));
        Assert.True(EffectCaptureFrame.TryCreateSource(source, new Vector2(-10.25f, -20.5f),
            new Vector2(2), 1, out var rebased));
        Assert.Equal(new Rect(10.25f, 20.5f, 1, 1), original.PaddedBounds);
        Assert.Equal(new Rect(0, 0, 1, 1), rebased.PaddedBounds);
        // Rounding the already rebased rectangle would incorrectly use 2x2.
        Assert.Equal(3U, original.PixelWidth);
        Assert.Equal(2U, original.PixelHeight);
        Assert.Equal(original.PixelWidth, rebased.PixelWidth);
        Assert.Equal(original.PixelHeight, rebased.PixelHeight);
        Assert.Equal(new Vector2(20, 41), original.PhysicalOrigin);
        Assert.Equal(original.PhysicalOrigin, rebased.PhysicalOrigin);
        Assert.Equal(original.TextureUvBounds, rebased.TextureUvBounds);
        Assert.Equal(new Rect(-.25f, 0, 1.5f, 1), rebased.RasterBounds);
        Assert.Equal(new Vector2(.5f, 0), Vector2.Transform(Vector2.Zero, rebased.SourceToRaster));
    }

    [Fact]
    public void SmallSourceDoesNotAcquireTheLegacyOneLogicalUnitMinimum()
    {
        var source = new ShaderEffectSourceCapture(.125, .0625, .125, .125, 0, 0, 0, 0);
        Assert.True(EffectCaptureFrame.TryCreateSource(source, Vector2.Zero, new Vector2(8, 16), 1, out var frame));
        Assert.Equal(new Rect(.125f, .0625f, .125f, .125f), frame.PaddedBounds);
        Assert.Equal(1U, frame.PixelWidth);
        Assert.Equal(2U, frame.PixelHeight);
        Assert.Equal(new Vector2(1, 1), frame.PhysicalOrigin);
        Assert.Equal(frame.PaddedBounds, frame.RasterBounds);
        Assert.Equal(new Vector4(0, 0, 1, 1), frame.TextureUvBounds);

        // Calling the old scalar overload remains an explicit legacy choice.
        Assert.True(EffectCaptureFrame.TryCreateSource(source, 8, out var legacy));
        Assert.Equal(8U, legacy.PixelWidth);
        Assert.Equal(8U, legacy.PixelHeight);
        Assert.False(legacy.HasPhysicalOrigin);
    }

    [Fact]
    public void FractionalLogicalWidthKeepsItsPhysicalProjection()
    {
        var source = new ShaderEffectSourceCapture(0, 0, 1.125, 1.25, 0, 0, 0, 0);
        Assert.True(EffectCaptureFrame.TryCreateSource(source, Vector2.Zero, new Vector2(8, 4), 1, out var frame));
        Assert.Equal(9U, frame.PixelWidth);
        Assert.Equal(5U, frame.PixelHeight);
        Assert.Equal(new Rect(0, 0, 1.125f, 1.25f), frame.RasterBounds);
        Assert.Equal(new Vector2(1.125f, 1.25f), frame.ProjectionExtent);
        Assert.Equal(new Vector2(9, 5), Vector2.Transform(new Vector2(1.125f, 1.25f), frame.SourceToRaster));
        Assert.Equal(new Vector2(8, 4), Vector2.Transform(Vector2.One, frame.SourceToRaster));
    }

    [Fact]
    public void FarEndpointIsNarrowedAfterOriginalDoubleAddition()
    {
        var source = new ShaderEffectSourceCapture(.50000002, 0, .000000025, 1, 0, 0, 0, 0);
        Assert.True(EffectCaptureFrame.TryCreateSource(source, Vector2.Zero, Vector2.One, 1, out var frame));
        // The original far endpoint rounds to .5 + 2^-24, while adding
        // the separately narrowed width to .5 would collapse the rectangle.
        Assert.Equal(new Rect(.5f, 0, 1f / 16777216, 1), frame.PaddedBounds);
        Assert.Equal(1U, frame.PixelWidth);
        Assert.Equal(1U, frame.PixelHeight);
    }

    [Fact]
    public void EqualPixelExtentsDoNotEraseOriginalCaptureOrigin()
    {
        var first = new ShaderEffectSourceCapture(.125, .25, 1, 1, 0, 0, 0, 0);
        var second = new ShaderEffectSourceCapture(1.125, -1.75, 1, 1, 0, 0, 0, 0);
        Assert.True(EffectCaptureFrame.TryCreateSource(first, Vector2.Zero, new Vector2(2, 4), 1, out var a));
        Assert.True(EffectCaptureFrame.TryCreateSource(second, Vector2.Zero, new Vector2(2, 4), 1, out var b));
        Assert.Equal(a.PixelWidth, b.PixelWidth);
        Assert.Equal(a.PixelHeight, b.PixelHeight);
        Assert.NotEqual(a, b);
        Assert.Equal(new Vector2(0, 1), a.PhysicalOrigin);
        Assert.Equal(new Vector2(2, -7), b.PhysicalOrigin);
        Assert.Equal(a.TextureUvBounds, b.TextureUvBounds);
    }

    [Fact]
    public void PhysicalOriginRequiresAnExactSignedFloatInteger()
    {
        foreach (double x in new[] { -16777216.0, 16777214.0 })
        {
            var source = new ShaderEffectSourceCapture(x, 0, 2, 1, 0, 0, 0, 0);
            Assert.True(EffectCaptureFrame.TryCreateSource(source, Vector2.Zero, Vector2.One, 1, out var frame));
            Assert.Equal(new Vector2((float)x, 0), frame.PhysicalOrigin);
            Assert.Equal(2U, frame.PixelWidth);
        }
        Reject(new ShaderEffectSourceCapture(-16777218, 0, 2, 1, 0, 0, 0, 0), Vector2.Zero, Vector2.One);
        Reject(new ShaderEffectSourceCapture(16777218, 0, 2, 1, 0, 0, 0, 0), Vector2.Zero, Vector2.One);
        // Both endpoints are exact, but the true integer span is 2^24+1.
        // Subtracting in float first would round down and admit 2^24 instead.
        Reject(new ShaderEffectSourceCapture(-16777216, 0, 16777217, 1, 0, 0, 0, 0), Vector2.Zero, Vector2.One);
        Reject(new ShaderEffectSourceCapture(0, -16777216, 1, 16777217, 0, 0, 0, 0), Vector2.Zero, Vector2.One);
    }

    [Fact]
    public void SemanticDpiIsIndependentOfActualGeometricAxes()
    {
        var source = new ShaderEffectSourceCapture(.25, .5, 2, 3, 0, 0, 0, 0);
        foreach (float dpi in new[] { .75f, 1, 1.5f, 2 })
        {
            Assert.True(EffectCaptureFrame.TryCreateSource(source, Vector2.Zero, new Vector2(2, 4), dpi, out var frame));
            Assert.Equal(dpi, frame.DpiScale);
            Assert.Equal(new Vector2(2, 4), frame.PixelsPerUnit);
            Assert.Equal(new Vector2(0, 2), frame.PhysicalOrigin);
            Assert.Equal(5U, frame.PixelWidth);
            Assert.Equal(12U, frame.PixelHeight);
        }

        foreach (float invalid in new[] { 0f, -1, float.NaN, float.PositiveInfinity })
        {
            Assert.True(EffectCaptureFrame.TryCreateSource(source, Vector2.Zero, new Vector2(2, 4), 1, out var frame));
            Assert.False(EffectCaptureFrame.TryCreateSource(source, Vector2.Zero, new Vector2(2, 4), invalid, out frame));
            Assert.Equal(default(EffectCaptureFrame), frame);
        }
    }

    [Fact]
    public void InvalidAxesAndTranslationPublishNoPartialFrame()
    {
        var source = new ShaderEffectSourceCapture(.25, .5, 2, 3, 0, 0, 0, 0);
        foreach (float invalid in new[] { 0f, -1, float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue })
        {
            Reject(source, Vector2.Zero, new Vector2(invalid, 2));
            Reject(source, Vector2.Zero, new Vector2(2, invalid));
        }
        Reject(source, new Vector2(float.NaN, 0), Vector2.One);
        Reject(source, new Vector2(0, float.PositiveInfinity), Vector2.One);
        Reject(new ShaderEffectSourceCapture(0, 0, 1, 1, 0, 0, double.NaN, 0), Vector2.Zero, Vector2.One);
        Reject(new ShaderEffectSourceCapture(0, 0, 1, 1, 0, 0, -1, 0), Vector2.Zero, Vector2.One);
        Reject(new ShaderEffectSourceCapture(16777216, 0, .25, 1, 0, 0, 0, 0), Vector2.Zero, Vector2.One);
        // Finite frame dimensions are insufficient if the actual offscreen
        // projection coefficient (2 / logicalExtent) would overflow.
        Reject(new ShaderEffectSourceCapture(0, 0, float.Epsilon, 1, 0, 0, 0, 0),
            Vector2.Zero, new Vector2(float.MaxValue, 1));
        Reject(new ShaderEffectSourceCapture(0, 0, 1, float.Epsilon, 0, 0, 0, 0),
            Vector2.Zero, new Vector2(1, float.MaxValue));
    }

    [Fact]
    public void TargetMappingUsesActualNormalizedViewportAndKeepsAxesIndependent()
    {
        Assert.True(EffectCaptureFrame.TryResolveSourcePixelsPerUnit(64, 32, 256, 128,
            new RenderTargetViewport(16, 8, 128, 32), out var scale));
        Assert.Equal(new Vector2(2, 1), scale);
        Assert.True(EffectCaptureFrame.TryResolveSourcePixelsPerUnit(64, 32, 256, 128,
            new RenderTargetViewport(24, 12, 128, 32), out var moved));
        Assert.Equal(scale, moved);

        // Target clamping is exactly the ordinary compositor's viewport clamp:
        // the requested 256-wide viewport only has 128 pixels left from x=128.
        Assert.True(EffectCaptureFrame.TryResolveSourcePixelsPerUnit(64, 32, 256, 128,
            new RenderTargetViewport(128, 8, 256, 32), out var clamped));
        Assert.Equal(scale, clamped);
        Assert.True(EffectCaptureFrame.TryResolveSourcePixelsPerUnit(64, 32, 256, 128,
            RenderTargetViewport.Full(256, 128), out var full));
        Assert.Equal(new Vector2(4, 4), full);
    }

    [Fact]
    public void InvalidTargetMappingPublishesNoPartialAxes()
    {
        foreach (var dimensions in new (uint Width, uint Height, uint TargetWidth, uint TargetHeight)[]
        {
            (0, 32, 256, 128), (64, 0, 256, 128),
            (64, 32, 0, 128), (64, 32, 256, 0)
        })
        {
            Vector2 scale = new(7, 11);
            Assert.False(EffectCaptureFrame.TryResolveSourcePixelsPerUnit(dimensions.Width, dimensions.Height,
                dimensions.TargetWidth, dimensions.TargetHeight, new RenderTargetViewport(0, 0, 128, 32), out scale));
            Assert.Equal(Vector2.Zero, scale);
        }

        foreach (var viewport in new[]
        {
            new RenderTargetViewport(float.NaN, 0, 128, 32),
            new RenderTargetViewport(0, float.PositiveInfinity, 128, 32),
            new RenderTargetViewport(0, 0, 0, 32),
            new RenderTargetViewport(0, 0, 128, -1)
        })
        {
            Vector2 scale = new(7, 11);
            Assert.False(EffectCaptureFrame.TryResolveSourcePixelsPerUnit(64, 32, 256, 128, viewport, out scale));
            Assert.Equal(Vector2.Zero, scale);
        }
    }

    private static void Reject(ShaderEffectSourceCapture source, Vector2 translation, Vector2 pixelsPerUnit)
    {
        Assert.True(EffectCaptureFrame.TryCreate(new Rect(1, 2, 3, 4), 0, 1, out var frame));
        Assert.False(EffectCaptureFrame.TryCreateSource(source, translation, pixelsPerUnit, 1, out frame));
        Assert.Equal(default(EffectCaptureFrame), frame);
    }
}
