using System;
using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public sealed class CacheSamplerRasterFrameTests
{
    [Fact]
    public void OriginalBoundsPrimaryAxesAndScaleOwnTheRasterNotTheReceivingEffect()
    {
        Assert.True(CacheSamplerRasterFrame.TryCreate(10, -20, 8, 6, 2, 1.25f, 1.5f,
            4096, 2048, out var frame));
        Assert.Equal(20U, frame.PixelWidth);
        Assert.Equal(18U, frame.PixelHeight);
        Assert.Equal(new Matrix4x4(2.5f, 0, 0, 0, 0, 3, 0, 0, 0, 0, 1, 0, -25, 60, 0, 1),
            frame.SourceToRaster);
        Assert.False(frame.IsEmpty);
        Assert.Equal(8d, frame.SourceWidth);
        Assert.Equal(6d, frame.SourceHeight);
        Assert.Equal(2d, frame.RenderScale);
        Assert.Equal(1.25f, frame.PrimaryDpiScaleX);
        Assert.Equal(1.5f, frame.PrimaryDpiScaleY);
    }

    [Fact]
    public void DeviceClampChangesEachPhysicalAxisWithoutReplacingSourceIdentity()
    {
        Assert.True(CacheSamplerRasterFrame.TryCreate(3, 5, 64, 32, 2, 1.5f, 1.25f,
            96, 20, out var frame));
        Assert.Equal(96U, frame.PixelWidth);
        Assert.Equal(20U, frame.PixelHeight);
        Assert.Equal(1.5f, frame.SourceToRaster.M11);
        Assert.Equal(.625f, frame.SourceToRaster.M22);
        Assert.Equal(-4.5f, frame.SourceToRaster.M41);
        Assert.Equal(-3.125f, frame.SourceToRaster.M42);
        Assert.Equal(64d, frame.SourceWidth);
        Assert.Equal(32d, frame.SourceHeight);
        Assert.Equal(96U, frame.MaximumTextureWidth);
        Assert.Equal(20U, frame.MaximumTextureHeight);
    }

    [Theory]
    [InlineData(1d, 1U)]
    [InlineData(1.0000011920928955078125d, 1U)]
    [InlineData(1.00000131130218505859375d, 2U)]
    [InlineData(1024.0009765625d, 1024U)]
    [InlineData(1024.001953125d, 1025U)]
    [InlineData(1.5d, 2U)]
    public void RelativeFloatRoundOutIsNotUnconditionalCeiling(double extent, uint expected)
    {
        Assert.True(CacheSamplerRasterFrame.TryCreate(0, 0, extent, 2, 1, 1, 1,
            4096, 4096, out var frame));
        Assert.Equal(expected, frame.PixelWidth);
        Assert.Equal(2U, frame.PixelHeight);
        Assert.Equal(extent, frame.SourceWidth);
    }

    [Fact]
    public void EndpointNarrowingAndDoubleProductHaveDistinctObservableBoundaries()
    {
        // Binary32 spacing at 2^24 is2. Narrowing width alone would incorrectly
        // allocate a positive texel instead of recognizing the collapsed edges.
        Assert.True(CacheSamplerRasterFrame.TryCreate(16777216, 0, 1, 10, 1, 1, 1,
            4096, 4096, out var collapsed));
        Assert.True(collapsed.IsEmpty);
        Assert.Equal(1U, collapsed.PixelWidth);
        Assert.Equal(1U, collapsed.PixelHeight);
        Assert.Equal(1d, collapsed.SourceWidth);
        Assert.True(CacheSamplerRasterFrame.TryCreate(0, 0, 8, 4, 1.000000059604644775390625,
            1, 1, 4096, 4096, out var retained));
        Assert.Equal(1.000000059604644775390625, retained.RenderScale);
        Assert.Equal(8U, retained.PixelWidth);
        Assert.Equal(4U, retained.PixelHeight);
    }

    [Theory]
    [InlineData(0d, 9d, 1d)]
    [InlineData(9d, 0d, 1d)]
    [InlineData(9d, 9d, 0d)]
    [InlineData(1.401298464324817E-45d, 9d, 1E-300d)]
    public void AbsentRealizationOwnsExactlyOneTransparentTexel(double width, double height, double scale)
    {
        Assert.True(CacheSamplerRasterFrame.TryCreate(0, 0, width, height, scale,
            1, 2, 4096, 4096, out var frame));
        Assert.True(frame.IsEmpty);
        Assert.Equal(1U, frame.PixelWidth);
        Assert.Equal(1U, frame.PixelHeight);
        Assert.Equal(Matrix4x4.Identity, frame.SourceToRaster);
    }

    [Fact]
    public void EmptyRectanglePrecedesUnneededOtherAxisSizingButNotMalformedInputValidation()
    {
        Assert.True(CacheSamplerRasterFrame.TryCreate(0, 0, 0, 1, double.MaxValue,
            1, 1, 4096, 4096, out var frame));
        Assert.True(frame.IsEmpty);
        Assert.False(CacheSamplerRasterFrame.TryCreate(0, 0, 0, double.PositiveInfinity, 1,
            1, 1, 4096, 4096, out frame));
        Assert.False(frame.IsValid);
    }

    [Fact]
    public void InvalidPolicyAndLateAxisFailurePublishNoPartialFrame()
    {
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
        {
            Reject(0, 0, 8, 6, invalid, 1, 1, 4096, 4096);
            Reject(0, 0, 8, invalid, 1, 1, 1, 4096, 4096);
        }
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f, 0f })
            Reject(0, 0, 8, 6, 1, 1, invalid, 4096, 4096);
        Reject(0, 0, 8, 6, 1, 1, 1, 0, 4096);
        Reject(0, 0, 8, 6, 1, 1, 1, 4096, 0);
        Reject(0, 0, 1, 4294967296d, 1, 1, 1, 4096, 4096);
        Reject(double.MaxValue, 0, 8, 6, 1, 1, 1, 4096, 4096);
        Reject(0, 0, 8, 6, double.MaxValue, 1, 1, 4096, 4096);
    }

    private static void Reject(double x, double y, double width, double height, double scale,
        float primaryX, float primaryY, uint maximumWidth, uint maximumHeight)
    {
        Assert.False(CacheSamplerRasterFrame.TryCreate(x, y, width, height, scale,
            primaryX, primaryY, maximumWidth, maximumHeight, out var frame));
        Assert.False(frame.IsValid);
        Assert.Equal(0U, frame.PixelWidth);
        Assert.Equal(0U, frame.PixelHeight);
    }
}
