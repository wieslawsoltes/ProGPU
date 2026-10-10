using System;
using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public sealed class ShaderEffectSourceCaptureTests
{
    [Fact]
    public void FourFractionalEdgesAreNeitherMaximizedNorCeiledBeforeCapture()
    {
        var source = new ShaderEffectSourceCapture(16.75, 16.25, 15.5, 7.5, 2.25, 6.5, 4.75, 11.25);
        Assert.True(source.IsValid);
        Assert.True(EffectCaptureFrame.TryCreateSource(source, 1.5f, out var frame));
        Assert.Equal(new Rect(12, 14, 31.5f, 16.25f), frame.PaddedBounds);
        Assert.Equal(31.5f, frame.LogicalWidth); Assert.Equal(16.25f, frame.LogicalHeight);
        Assert.Equal(32U, frame.LogicalRenderWidth); Assert.Equal(17U, frame.LogicalRenderHeight);
        Assert.Equal(48U, frame.PixelWidth); Assert.Equal(25U, frame.PixelHeight);

        Assert.True(EffectCaptureFrame.TryCreateSource(source, new Vector2(-16.75f, -16.25f),
            null, 1.5f, out var translated));
        Assert.Equal(new Rect(-4.75f, -2.25f, 31.5f, 16.25f), translated.PaddedBounds);
        Assert.Equal(frame.PixelWidth, translated.PixelWidth);
        Assert.Equal(frame.PixelHeight, translated.PixelHeight);
        Assert.Equal(frame.LogicalWidth, translated.LogicalWidth);
        Assert.Equal(frame.LogicalHeight, translated.LogicalHeight);
    }

    [Fact]
    public void OriginalEndpointNarrowsAfterDoubleAdditionBeforeSourceRebase()
    {
        var source = new ShaderEffectSourceCapture(16777217, 4, 1, 2, 0, 0, 0, 0);
        Assert.True(EffectCaptureFrame.TryCreateSource(source, new Vector2(-16777216, -4),
            null, 1, out var frame));
        Assert.Equal(new Rect(0, 0, 2, 2), frame.PaddedBounds);
        Assert.Equal(2U, frame.PixelWidth);
        // An explicit legacy override intentionally uses the old narrowed size.
        Assert.True(EffectCaptureFrame.TryCreateSource(source, new Vector2(-16777216, -4),
            0, 1, out frame));
        Assert.Equal(new Rect(0, 0, 1, 2), frame.PaddedBounds);
    }

    [Fact]
    public void EqualExtentsDoNotEraseChangedOriginOrDescriptorGeneration()
    {
        var first = new ShaderEffectSourceCapture(16, 16, 16, 8, 2, 6, 4, 8);
        var second = new ShaderEffectSourceCapture(16, 16, 16, 8, 6, 2, 8, 4);
        Assert.True(EffectCaptureFrame.TryCreateSource(first, 1, out var a));
        Assert.True(EffectCaptureFrame.TryCreateSource(second, 1, out var b));
        Assert.Equal(new Rect(12, 14, 28, 16), a.PaddedBounds);
        Assert.Equal(new Rect(8, 10, 28, 16), b.PaddedBounds);
        Assert.Equal(a.PixelWidth, b.PixelWidth); Assert.Equal(a.PixelHeight, b.PixelHeight);
        var effect = new WpfShaderEffect(new WpfShaderEffectParams()) { SourceCapture = first };
        long initial = effect.ChangeVersion;
        effect.SourceCapture = first;
        Assert.Equal(initial, effect.ChangeVersion);
        effect.SourceCapture = second;
        Assert.NotEqual(initial, effect.ChangeVersion);
        Assert.Equal(second, effect.SourceCapture.GetValueOrDefault());
        effect.SourceCapture = null;
        Assert.Null(effect.SourceCapture);
    }

    [Fact]
    public void DescriptorIdentityRetainsSignedZeroAndOriginalSubFloatDifferences()
    {
        var first = new ShaderEffectSourceCapture(0, 0, 16, 8, 0, 2, 4, 8);
        var signed = new ShaderEffectSourceCapture(-0.0, 0, 16, 8, -0.0, 2, 4, 8);
        var precise = new ShaderEffectSourceCapture(0, 0, 16, 8, 0, Math.BitIncrement(2), 4, 8);
        Assert.NotEqual(first, signed); Assert.NotEqual(first, precise);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(signed.X));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(signed.PaddingTop));
        Assert.True(EffectCaptureFrame.TryCreateSource(first, 1, out var a));
        Assert.True(EffectCaptureFrame.TryCreateSource(precise, 1, out var b));
        Assert.Equal(a, b);
        var effect = new WpfShaderEffect(new WpfShaderEffectParams()) { SourceCapture = first };
        long version = effect.ChangeVersion;
        effect.SourceCapture = precise;
        Assert.NotEqual(version, effect.ChangeVersion);
    }

    [Fact]
    public void ExplicitRasterOverrideRetainsLegacyNormalizationAndArithmetic()
    {
        var source = new ShaderEffectSourceCapture(10.25, -3.5, .25, .5, 2, 6, 4, 8);
        foreach (float padding in new[] { .25f, -1, float.NaN, float.PositiveInfinity })
        {
            Assert.True(EffectCaptureFrame.TryCreate(new Rect(10.25f, -3.5f, .25f, .5f),
                8, padding, 1.5f, out var legacy));
            Assert.True(EffectCaptureFrame.TryCreateSource(source, Vector2.Zero, padding, 1.5f, out var sourceFrame));
            Assert.Equal(legacy, sourceFrame);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InvalidPaddingCannotBecomeAValidFrameThroughAnOverride(int axis)
    {
        foreach (double invalid in new[] { -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue })
        {
            double[] padding = [2, 6, 4, 8]; padding[axis] = invalid;
            var source = new ShaderEffectSourceCapture(1, 2, 16, 8,
                padding[0], padding[1], padding[2], padding[3]);
            Assert.False(source.IsValid);
            Reject(source, Vector2.Zero, null, 1);
            Reject(source, Vector2.Zero, 0, 1);
        }
    }

    [Fact]
    public void InvalidMetadataDpiInflationAndTranslationPublishNoPartialFrame()
    {
        foreach (var source in new[]
        {
            new ShaderEffectSourceCapture(double.NaN, 0, 16, 8, 0, 0, 0, 0),
            new ShaderEffectSourceCapture(0, double.PositiveInfinity, 16, 8, 0, 0, 0, 0),
            new ShaderEffectSourceCapture(0, 0, -1, 8, 0, 0, 0, 0),
            new ShaderEffectSourceCapture(0, 0, 16, 0, 0, 0, 0, 0),
            new ShaderEffectSourceCapture(double.MaxValue, 0, double.MaxValue, 8, 0, 0, 0, 0),
        })
        {
            Assert.False(source.IsValid);
            Reject(source, Vector2.Zero, null, 1);
        }

        var valid = new ShaderEffectSourceCapture(1, 2, 16, 8, 2, 6, 4, 8);
        foreach (float invalid in new[] { 0f, -1, float.NaN, float.PositiveInfinity, float.MaxValue })
            Reject(valid, Vector2.Zero, null, invalid);
        Reject(valid, new Vector2(float.PositiveInfinity, 0), null, 1);
        Reject(valid, new Vector2(0, float.NaN), null, 1);
        // Valid endpoint/padding metadata is not an inflated-size admission.
        var overflow = new ShaderEffectSourceCapture(0, 0, 16, 8, 0, 0, float.MaxValue, float.MaxValue);
        Assert.True(overflow.IsValid);
        Reject(overflow, Vector2.Zero, null, 1);
        Reject(new ShaderEffectSourceCapture(16777216, 0, .25, 8, 0, 0, 0, 0), Vector2.Zero, null, 1);

        var visual = new DrawingVisual { EffectSourceTranslation = new Vector2(-1, -2) };
        Assert.Throws<ArgumentOutOfRangeException>(() => visual.EffectSourceTranslation = new Vector2(float.NaN, 0));
        Assert.Equal(new Vector2(-1, -2), visual.EffectSourceTranslation.GetValueOrDefault());
    }

    private static void Reject(ShaderEffectSourceCapture source, Vector2 translation, float? padding, float dpi)
    {
        Assert.True(EffectCaptureFrame.TryCreate(new Rect(1, 2, 3, 4), 0, 1, out var frame));
        Assert.False(EffectCaptureFrame.TryCreateSource(source, translation, padding, dpi, out frame));
        Assert.Equal(default(EffectCaptureFrame), frame);
    }
}
