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

    private sealed class Source : IPortablePixelShaderSource
    {
        internal int Calls;
        public bool TryGetPortablePixelShader(out PortablePixelShader pixelShader)
        { Calls++; throw new InvalidOperationException("Source identity cannot substitute original bytecode capture."); }
    }
}
