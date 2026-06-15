using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public class WpfShaderEffectParamsTests
{
    [Fact]
    public void CopiesConstantsAndTextureMetadataToUniformLayout()
    {
        var constants = Enumerable.Range(0, WpfShaderEffectParams.ConstantFloatCount + 8)
            .Select(i => (float)i)
            .ToArray();
        var parameters = new WpfShaderEffectParams
        {
            Rect = new Rect(10, 20, 30, 40),
            Constants = constants
        };

        Span<float> uniforms = stackalloc float[WpfShaderEffectParams.UniformFloatCount];

        parameters.CopyUniformFloats(uniforms, textureWidth: 256, textureHeight: 128);

        Assert.Equal(0f, uniforms[0]);
        Assert.Equal(127f, uniforms[WpfShaderEffectParams.ConstantFloatCount - 1]);
        Assert.Equal(10f, uniforms[WpfShaderEffectParams.ConstantFloatCount]);
        Assert.Equal(20f, uniforms[WpfShaderEffectParams.ConstantFloatCount + 1]);
        Assert.Equal(30f, uniforms[WpfShaderEffectParams.ConstantFloatCount + 2]);
        Assert.Equal(40f, uniforms[WpfShaderEffectParams.ConstantFloatCount + 3]);
        Assert.Equal(256f, uniforms[WpfShaderEffectParams.ConstantFloatCount + 4]);
        Assert.Equal(128f, uniforms[WpfShaderEffectParams.ConstantFloatCount + 5]);
        Assert.Equal(1f / 256f, uniforms[WpfShaderEffectParams.ConstantFloatCount + 6]);
        Assert.Equal(1f / 128f, uniforms[WpfShaderEffectParams.ConstantFloatCount + 7]);
        Assert.Equal(0f, uniforms[WpfShaderEffectParams.ConstantFloatCount + 8]);
    }

    [Fact]
    public void CopiesSourceTextureRegisterToUniformLayout()
    {
        var parameters = new WpfShaderEffectParams
        {
            SourceTextureRegisterIndex = 7
        };

        Span<float> uniforms = stackalloc float[WpfShaderEffectParams.UniformFloatCount];

        parameters.CopyUniformFloats(uniforms, textureWidth: 256, textureHeight: 128);

        Assert.Equal(7f, uniforms[WpfShaderEffectParams.ConstantFloatCount + 8]);
    }

    [Fact]
    public void UsesStableGeneratedShaderKeyWhenExplicitKeyIsAbsent()
    {
        var left = new WpfShaderEffectParams
        {
            ShaderSource = "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return inputColor; }"
        };
        var right = new WpfShaderEffectParams
        {
            ShaderSource = left.ShaderSource
        };

        Assert.Equal(left.GetStableShaderKey(), right.GetStableShaderKey());
        Assert.StartsWith("wpf_shader_", left.GetStableShaderKey());
    }

    [Fact]
    public void ExplicitShaderKeyWinsOverGeneratedKey()
    {
        var parameters = new WpfShaderEffectParams
        {
            ShaderKey = "legacy_invert_ps_2_0",
            ShaderSource = "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return vec4<f32>(1.0); }"
        };

        Assert.Equal("legacy_invert_ps_2_0", parameters.GetStableShaderKey());
    }

    [Fact]
    public void VisualShaderEffectCacheKeyTracksMutableParameters()
    {
        var parameters = new WpfShaderEffectParams
        {
            Constants = new[] { 1f, 2f, 3f },
            Samplers = new[]
            {
                new WpfShaderEffectSampler(1, null, TextureSamplingMode.Linear)
            }
        };
        var effect = new WpfShaderEffect(parameters);

        var initialKey = GetRenderCacheKey(effect);
        parameters.Constants[1] = 20f;
        var constantsKey = GetRenderCacheKey(effect);
        parameters.Samplers[0].SamplingMode = TextureSamplingMode.Nearest;
        var samplerKey = GetRenderCacheKey(effect);

        Assert.NotEqual(initialKey, constantsKey);
        Assert.NotEqual(constantsKey, samplerKey);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(WpfShaderEffectParams.MaxSamplerRegisterCount)]
    public void RejectsSamplerRegistersOutsideWpfBank(int registerIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WpfShaderEffectSampler(registerIndex, null));
    }

    private static int GetRenderCacheKey(WpfShaderEffect effect)
    {
        var method = typeof(WpfShaderEffect).GetMethod(
            "GetRenderCacheKey",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (int)method.Invoke(effect, null)!;
    }
}
