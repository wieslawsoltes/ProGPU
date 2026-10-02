using System;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public class EffectCaptureFrameTests
{
    [Fact]
    public void OriginalPaddedBoundsRemainSeparateFromMinimumLogicalAndPhysicalExtents()
    {
        var content = new Rect(10.25f, -3.5f, 0.25f, 0.5f);
        Assert.True(EffectCaptureFrame.TryCreate(content, 0f, 1.5f, out var frame));
        Assert.Equal(content, frame.PaddedBounds);
        Assert.Equal(1f, frame.LogicalWidth);
        Assert.Equal(1f, frame.LogicalHeight);
        Assert.Equal(1U, frame.LogicalRenderWidth);
        Assert.Equal(1U, frame.LogicalRenderHeight);
        Assert.Equal(2U, frame.PixelWidth);
        Assert.Equal(2U, frame.PixelHeight);
        Assert.Equal(1.5f, frame.DpiScale);

        Assert.True(EffectCaptureFrame.TryCreate(content, 1.25f, 1.5f, out frame));
        Assert.Equal(new Rect(8.25f, -5.5f, 4.25f, 4.5f), frame.PaddedBounds);
        Assert.Equal(4.25f, frame.LogicalWidth);
        Assert.Equal(4.5f, frame.LogicalHeight);
        Assert.Equal(5U, frame.LogicalRenderWidth);
        Assert.Equal(5U, frame.LogicalRenderHeight);
        Assert.Equal(7U, frame.PixelWidth);
        Assert.Equal(7U, frame.PixelHeight);
        // Returned bounds are a value snapshot, never a mutable frame alias.
        Rect changed = frame.PaddedBounds;
        changed.Width = 100;
        Assert.Equal(4.25f, frame.PaddedBounds.Width);
    }

    [Fact]
    public void FractionalRasterOverrideDoesNotUseShaderPaddingCeiling()
    {
        var content = new Rect(10.25f, -3.5f, 0.25f, 0.5f);
        Assert.True(EffectCaptureFrame.TryCreate(content, 8f, 0.25f, 1.5f, out var frame));
        Assert.Equal(new Rect(10f, -3.75f, 0.75f, 1f), frame.PaddedBounds);
        Assert.Equal(2U, frame.PixelWidth);
        Assert.Equal(2U, frame.PixelHeight);
        foreach (float padding in new[] { -1f, float.NaN, float.NegativeInfinity, float.PositiveInfinity })
        {
            Assert.True(EffectCaptureFrame.TryCreate(content, float.NaN, padding, 1.5f, out frame));
            Assert.Equal(content, frame.PaddedBounds);
        }
    }

    [Fact]
    public void PhysicalCeilingFollowsOriginalFloatProductNotDoubleOrRoundedLogicalWidth()
    {
        Assert.True(EffectCaptureFrame.TryCreate(new Rect(-3, 4, 1.1f, 2.2f), 0, 10, out var frame));
        Assert.Equal(2U, frame.LogicalRenderWidth);
        Assert.Equal(3U, frame.LogicalRenderHeight);
        Assert.Equal(11U, frame.PixelWidth);
        Assert.Equal(22U, frame.PixelHeight);
        Assert.Equal(12d, Math.Ceiling((double)1.1f * 10));
        Assert.Equal(23d, Math.Ceiling((double)2.2f * 10));
    }

    [Fact]
    public void ShaderFramesMatchOriginalCompositorArithmeticAcrossSourceBoundsPaddingAndDpi()
    {
        foreach (float origin in new[] { -17.25f, 0f, 2.125f })
        foreach (float width in new[] { 0.125f, 1f, 1.1f, 13.25f, 256.5f })
        foreach (float height in new[] { 0.25f, 1f, 2.2f, 31.75f })
        foreach (float padding in new[] { -2f, 0f, 0.125f, 1f, 3.75f })
        foreach (float dpi in new[] { 0.5f, 1f, 1.25f, 1.5f, 2f, 10f })
        {
            // Independent original ProGPU compositor expression order at 2ab3;
            // do not call the helper's normalizers to manufacture the expected value.
            float p = MathF.Ceiling(MathF.Max(0f, padding));
            var expected = new Rect(origin - p, -origin - p, width + p * 2f, height + p * 2f);
            float logicalWidth = MathF.Max(1f, expected.Width), logicalHeight = MathF.Max(1f, expected.Height);
            Assert.True(EffectCaptureFrame.TryCreate(new Rect(origin, -origin, width, height), padding, dpi, out var frame));
            Assert.Equal(expected, frame.PaddedBounds);
            Assert.Equal(logicalWidth, frame.LogicalWidth);
            Assert.Equal(logicalHeight, frame.LogicalHeight);
            Assert.Equal((uint)MathF.Ceiling(logicalWidth), frame.LogicalRenderWidth);
            Assert.Equal((uint)MathF.Ceiling(logicalHeight), frame.LogicalRenderHeight);
            Assert.Equal((uint)MathF.Ceiling(logicalWidth * dpi), frame.PixelWidth);
            Assert.Equal((uint)MathF.Ceiling(logicalHeight * dpi), frame.PixelHeight);
        }
    }

    [Fact]
    public void InvalidAndOverflowedFramesPublishNoPartialOutput()
    {
        var valid = new Rect(2, 3, 4, 5);
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Reject(new(invalid, 3, 4, 5), 0, 1);
            Reject(new(2, invalid, 4, 5), 0, 1);
            Reject(new(2, 3, invalid, 5), 0, 1);
            Reject(new(2, 3, 4, invalid), 0, 1);
            Reject(valid, 0, invalid);
        }
        Reject(Rect.Empty, 10, 1);
        Reject(new(0, 0, -1, 3), 10, 1);
        Reject(valid, 0, 0);
        Reject(valid, 0, -1);
        Reject(valid, float.NaN, 1);
        Reject(valid, float.PositiveInfinity, 1);
        Reject(valid, float.MaxValue, 1);
        Reject(valid, 0, float.MaxValue);
        Reject(new(-float.MaxValue, 0, 1, 1), float.MaxValue / 2, 1);
        Reject(new(0, 0, 4294967296f, 1), 0, 1);
        Reject(new(0, 0, 1, 4294967296f), 0, 1);
        Reject(new(0, 0, 2147483648f, 1), 0, 2);
    }

    [Fact]
    public void RepresentableBoundaryDoesNotInventADeviceLimitOrPhysicalMinimum()
    {
        float maximum = MathF.BitDecrement(4294967296f);
        Assert.True(EffectCaptureFrame.TryCreate(new(0, 0, maximum, 1), 0, 1, out var frame));
        Assert.Equal(4294967040U, frame.PixelWidth);
        Assert.True(EffectCaptureFrame.TryCreate(new(0, 0, 0.125f, 0.125f), 0, float.Epsilon, out frame));
        Assert.Equal(1U, frame.PixelWidth);
        Assert.Equal(1U, frame.PixelHeight);
        Assert.True(EffectCaptureFrame.TryCreate(new(0, 0, 1, 1), float.NegativeInfinity, 1, out frame));
        Assert.Equal(new Rect(0, 0, 1, 1), frame.PaddedBounds);
    }

    private static void Reject(Rect content, float padding, float dpi)
    {
        Assert.True(EffectCaptureFrame.TryCreate(new(1, 2, 3, 4), 1, 2, out var frame));
        Assert.False(EffectCaptureFrame.TryCreate(content, padding, dpi, out frame));
        Assert.Equal(default(EffectCaptureFrame), frame);
    }
}
