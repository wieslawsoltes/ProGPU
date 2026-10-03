using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableShaderEffectTransportTests
{
    [Fact]
    public void MissingExecutionIntentIsNotImplicitAuto()
    {
        var shader = new PortablePixelShader(null, null, [1, 2, 3, 4], 3, 0);
        Assert.Null(shader.RenderMode); Assert.Null(shader.Source);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(99)]
    public void ExplicitAndUnknownIntentStayOriginalForConsumerAdmission(int mode)
    {
        var shader = new PortablePixelShader(null, null, [1, 2, 3, 4], 2, 0) { RenderMode = (PortableShaderRenderMode)mode };
        Assert.Equal((PortableShaderRenderMode)mode, shader.RenderMode);
    }

    [Fact]
    public void OriginalBytecodeIsCapturedIndependentlyOfCallerBufferAndSourceIdentity()
    {
        byte[] bytes = [0, 2, 255, 255, 0xff, 0xff, 0, 0]; var original = bytes.ToArray();
        var source = new Source();
        var shader = new PortablePixelShader("shader.ps", "file:///shader.ps", bytes, 2, 0)
        { RenderMode = PortableShaderRenderMode.HardwareOnly, Source = source };
        bytes[0] = 0xa5; Assert.Equal(original, shader.Bytecode); Assert.NotSame(bytes, shader.Bytecode);
        Assert.Same(source, shader.Source); Assert.Equal(0, source.Calls); Assert.Equal((short)2, shader.MajorVersion);
    }

    [Fact]
    public void MissingBytecodeIsNotInventedFromMetadataOrSource()
    {
        var source = new Source();
        var shader = new PortablePixelShader("shader.ps", null, null, 3, 0) { Source = source };
        Assert.Empty(shader.Bytecode); Assert.Equal(0, source.Calls);
    }

    [Fact]
    public void EffectCapturesConstantAndSamplerArraysWithoutReplacingBrushIdentity()
    {
        object brush = new(); var sampler = new PortableShaderSampler(7, brush, PortableShaderSamplingMode.Auto);
        float[] constants = [1, -0.0f, 3, 4]; PortableShaderSampler[] samplers = [sampler];
        var effect = new PortableShaderEffect(null, null, null, constants, samplers, 0, 0, 0, 0, 0, 0, 31);
        constants[0] = 99; samplers[0] = PortableShaderSampler.ImplicitInput(0, PortableShaderSamplingMode.NearestNeighbor);
        Assert.Equal(1, effect.FloatConstants[0]); Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(effect.FloatConstants[1]));
        Assert.Same(sampler, effect.Samplers[0]); Assert.Same(brush, effect.Samplers[0].Brush);
        Assert.Equal(31, effect.DdxUvDdyUvRegisterIndex);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void PaddingRetainsOriginalBitsWithoutAdmittingOrRepairingTheSource(int axis)
    {
        long[] bitPatterns =
        [
            0, long.MinValue, 1, 0x3ff0000000000001,
            0x7fefffffffffffff, unchecked((long)0xbff0000000000000),
            0x7ff0000000000000, unchecked((long)0xfff0000000000000),
            0x7ff8000000000042, unchecked((long)0xfff8000000000087)
        ];
        foreach (long bits in bitPatterns)
        {
            double[] original = [2.25, 6.5, 4.75, 12.125];
            original[axis] = BitConverter.Int64BitsToDouble(bits);
            var effect = new PortableShaderEffect(null, null, null, null, null,
                0, 0, original[0], original[1], original[2], original[3], -1);
            double[] captured = [effect.PaddingTop, effect.PaddingBottom, effect.PaddingLeft, effect.PaddingRight];
            for (int i = 0; i < original.Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(original[i]), BitConverter.DoubleToInt64Bits(captured[i]));
        }
    }

    [Fact]
    public void ValidPaddingMaximumDoesNotReplaceItsFourSourceAxes()
    {
        var effect = new PortableShaderEffect(null, null, null, null, null,
            0, 0, 2.25, 6.5, 4.75, 12.125, -1);
        Assert.Equal(12.125, effect.MaxPadding);
        Assert.Equal(2.25, effect.PaddingTop); Assert.Equal(6.5, effect.PaddingBottom);
        Assert.Equal(4.75, effect.PaddingLeft); Assert.Equal(12.125, effect.PaddingRight);
    }

    [Fact]
    public void ImageSamplerRetainsActualSourceBrushAndImageSeparately()
    {
        object image = new(), brush = new();
        var sampler = PortableShaderSampler.Image(15, image, PortableShaderSamplingMode.Bilinear, brush);
        Assert.Equal(PortableShaderSamplerKind.ImageSource, sampler.Kind); Assert.Equal(15, sampler.RegisterIndex);
        Assert.Same(image, sampler.ImageSource); Assert.Same(brush, sampler.Brush); Assert.NotSame(sampler.ImageSource, sampler.Brush);
        Assert.Equal(PortableShaderSamplingMode.Bilinear, sampler.SamplingMode);
        Assert.Throws<ArgumentNullException>(() => PortableShaderSampler.Image(0, image, PortableShaderSamplingMode.Auto, null!));
    }

    [Fact]
    public void LegacyImageOnlySamplerDoesNotFabricateACompleteBrushContract()
    {
        object image = new(); var sampler = PortableShaderSampler.Image(2, image, PortableShaderSamplingMode.Auto);
        Assert.Same(image, sampler.ImageSource); Assert.Null(sampler.Brush);
        var implicitInput = PortableShaderSampler.ImplicitInput(2, PortableShaderSamplingMode.Auto);
        Assert.Equal(PortableShaderSamplerKind.ImplicitInput, implicitInput.Kind); Assert.Null(implicitInput.Brush); Assert.Null(implicitInput.ImageSource);
    }

    [Fact]
    public void PortableSamplerValuesRemainDistinctFromOriginalMilNumbers()
    {
        Assert.Equal(0, (int)PortableShaderSamplingMode.NearestNeighbor);
        Assert.Equal(1, (int)PortableShaderSamplingMode.Bilinear); Assert.Equal(2, (int)PortableShaderSamplingMode.Auto);
        var unknown = PortableShaderSampler.Image(0, new(), (PortableShaderSamplingMode)99, new());
        Assert.Equal((PortableShaderSamplingMode)99, unknown.SamplingMode);
    }

    [Fact]
    public void BitmapMetadataPreservesIndependentSourceDpiWithoutReadingPixels()
    {
        var original = new PortableBitmapSourceMetrics(320, 180, 192.25, 143.75);
        var source = new BitmapMetricsSource(original);
        Assert.True(((IPortableBitmapSourceMetricsSource)source).TryGetPortableBitmapSourceMetrics(out var metrics));
        Assert.Equal(original, metrics);
        Assert.Equal(320, metrics.PixelWidth); Assert.Equal(180, metrics.PixelHeight);
        Assert.Equal(BitConverter.DoubleToInt64Bits(192.25), BitConverter.DoubleToInt64Bits(metrics.DpiX));
        Assert.Equal(BitConverter.DoubleToInt64Bits(143.75), BitConverter.DoubleToInt64Bits(metrics.DpiY));
    }

    [Fact]
    public void BitmapMetadataDoesNotNormalizeInvalidSourceValuesIntoAdmission()
    {
        var metrics = new PortableBitmapSourceMetrics(-1, 0, -0.0, double.NaN);
        Assert.Equal(-1, metrics.PixelWidth); Assert.Equal(0, metrics.PixelHeight);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(metrics.DpiX));
        Assert.True(double.IsNaN(metrics.DpiY));
    }

    private sealed class BitmapMetricsSource(PortableBitmapSourceMetrics metrics)
        : IPortableBitmapSourceMetricsSource, IPortableBitmapSourcePixelsSource
    {
        public bool TryGetPortableBitmapSourceMetrics(out PortableBitmapSourceMetrics result)
        { result = metrics; return true; }

        public bool TryGetPortableBitmapSourcePixels(out PortableBitmapSourcePixels pixels)
            => throw new InvalidOperationException("Metadata queries must not copy bitmap pixels.");
    }

    private sealed class Source : IPortablePixelShaderSource
    {
        internal int Calls;
        public bool TryGetPortablePixelShader(out PortablePixelShader pixelShader)
        { Calls++; throw new InvalidOperationException("Source identity cannot substitute original bytecode capture."); }
    }
}
