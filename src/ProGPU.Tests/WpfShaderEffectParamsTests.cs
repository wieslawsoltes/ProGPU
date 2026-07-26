using ProGPU.Scene;
using ProGPU.Backend;
using System.IO;
using System.Runtime.CompilerServices;
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

    [Fact]
    public void VisualShaderEffectCacheKeyTracksPrimaryTextureGeneration()
    {
        var texture = CreateTextureWithGeneration(1);
        var parameters = new WpfShaderEffectParams
        {
            Texture = texture
        };
        var effect = new WpfShaderEffect(parameters);

        var initialKey = GetRenderCacheKey(effect);
        SetTextureGeneration(texture, 2);
        var resizedKey = GetRenderCacheKey(effect);

        Assert.NotEqual(initialKey, resizedKey);
    }

    [Fact]
    public void VisualShaderEffectCacheKeyTracksSamplerTextureGeneration()
    {
        var texture = CreateTextureWithGeneration(1);
        var parameters = new WpfShaderEffectParams
        {
            Samplers = new[]
            {
                new WpfShaderEffectSampler(0, texture, TextureSamplingMode.Linear)
            }
        };
        var effect = new WpfShaderEffect(parameters);

        var initialKey = GetRenderCacheKey(effect);
        SetTextureGeneration(texture, 2);
        var resizedKey = GetRenderCacheKey(effect);

        Assert.NotEqual(initialKey, resizedKey);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(WpfShaderEffectParams.MaxSamplerRegisterCount)]
    public void RejectsSamplerRegistersOutsideWpfBank(int registerIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WpfShaderEffectSampler(registerIndex, null));
    }

    [Fact]
    public void DirectTextureBindsAtRequestedSourceRegister()
    {
        var texture = (GpuTexture)RuntimeHelpers.GetUninitializedObject(typeof(GpuTexture));
        var parameters = new WpfShaderEffectParams
        {
            Texture = texture,
            SourceTextureRegisterIndex = 3,
            SamplingMode = TextureSamplingMode.Nearest
        };

        Assert.True(parameters.TryGetSampler(3, out var resolvedTexture, out var samplingMode));
        Assert.Same(texture, resolvedTexture);
        Assert.Equal(TextureSamplingMode.Nearest, samplingMode);
    }

    [Fact]
    public void PrimaryTextureUsesDeclaredSourceRegisterBeforeSamplerZero()
    {
        var sourceTexture = (GpuTexture)RuntimeHelpers.GetUninitializedObject(typeof(GpuTexture));
        var samplerZeroTexture = (GpuTexture)RuntimeHelpers.GetUninitializedObject(typeof(GpuTexture));
        var parameters = new WpfShaderEffectParams
        {
            Texture = sourceTexture,
            SourceTextureRegisterIndex = 3,
            Samplers = new[]
            {
                new WpfShaderEffectSampler(0, samplerZeroTexture, TextureSamplingMode.Nearest)
            }
        };

        Assert.True(parameters.TryGetPrimaryTexture(out var primaryTexture));
        Assert.Same(sourceTexture, primaryTexture);
    }

    [Fact]
    public void ShaderEffectParamsScansSamplersWithIndexedLoops()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src",
            "ProGPU.Scene",
            "WpfShaderEffectParams.cs")).Replace("\r\n", "\n");

        Assert.Contains("var samplers = Samplers;\n        for (var i = 0; i < samplers.Length; i++)", source, StringComparison.Ordinal);
        Assert.Contains("var sampler = samplers[i];", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < value.Length; i++)", source, StringComparison.Ordinal);
        Assert.Contains("var c = value[i];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var sampler in Samplers)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var c in value)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderEffectDrawingHelperCopiesConstantsExplicitly()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src",
            "ProGPU.Scene",
            "DrawingContextShaderEffectExtensions.cs")).Replace("\r\n", "\n");

        Assert.Contains("var constantArray = CopyConstants(constants);", source, StringComparison.Ordinal);
        Assert.Contains("private static float[] CopyConstants(ReadOnlySpan<float> constants)", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < constants.Length; i++)", source, StringComparison.Ordinal);
        Assert.Contains("copiedConstants[i] = constants[i];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("constants.ToArray()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderEffectPipelineCollectsSamplerRegistersWithoutPerRenderArray()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src",
            "ProGPU.Scene",
            "Extensions",
            "WpfShaderEffectExtensionPipeline.cs")).Replace("\r\n", "\n");
        var pipelineCache = File.ReadAllText(FindRepoFile(
            "src",
            "ProGPU.Backend",
            "RenderPipelineCache.cs")).Replace("\r\n", "\n");

        Assert.Contains("Span<int> activeRegisters = stackalloc int[WpfShaderEffectParams.MaxSamplerRegisterCount];", source, StringComparison.Ordinal);
        Assert.Contains("var activeRegisterCount = CollectActiveSamplerRegisters(p, activeRegisters);", source, StringComparison.Ordinal);
        Assert.Contains("var activeRegisterSpan = activeRegisters[..activeRegisterCount];", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < activeSamplerRegisters.Length; i++)", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < activeRegisters.Length; i++)", source, StringComparison.Ordinal);
        Assert.Contains("var sourceRegisters = sourceLayout.Registers;", source, StringComparison.Ordinal);
        Assert.Contains("for (var i = 0; i < sourceRegisters.Length; i++)", source, StringComparison.Ordinal);
        Assert.Contains("Registers = CopyActiveRegisters(activeRegisters),", source, StringComparison.Ordinal);
        Assert.Contains("private static int[] CopyActiveRegisters(ReadOnlySpan<int> activeRegisters)", source, StringComparison.Ordinal);
        Assert.Contains("Span<VertexAttribute> attrs = stackalloc VertexAttribute[3];", source, StringComparison.Ordinal);
        Assert.Contains("Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[1];", source, StringComparison.Ordinal);
        Assert.Contains("ArrayStride = (uint)Unsafe.SizeOf<VectorVertex>()", source, StringComparison.Ordinal);
        Assert.Contains("compositor.PipelineCache.GetOrCreateRenderPipeline(\n                        pipelineKey,\n                        shaderModule,\n                        layouts,", source, StringComparison.Ordinal);
        Assert.Contains("ReadOnlySpan<VertexBufferLayout> vertexBufferLayouts", pipelineCache, StringComparison.Ordinal);
        Assert.Contains("private RenderPipeline* GetOrCreateRenderPipelineCore(", pipelineCache, StringComparison.Ordinal);
        Assert.Contains("fixed (VertexBufferLayout* pLayouts = vertexBufferLayouts)", pipelineCache, StringComparison.Ordinal);
        Assert.DoesNotContain("private static int[] CollectActiveSamplerRegisters", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return registers[..count].ToArray();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Registers = activeRegisters.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var register in activeSamplerRegisters)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var register in activeRegisters)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var register in sourceLayout.Registers)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new VertexBufferLayout[]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal.AllocHGlobal(Marshal.SizeOf<VertexAttribute>() * 3)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal.FreeHGlobal((IntPtr)layouts[0].Attributes)", source, StringComparison.Ordinal);
    }

    private static int GetRenderCacheKey(WpfShaderEffect effect)
    {
        var method = typeof(WpfShaderEffect).GetMethod(
            "GetRenderCacheKey",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (int)method.Invoke(effect, null)!;
    }

    private static GpuTexture CreateTextureWithGeneration(uint generation)
    {
        var texture = (GpuTexture)RuntimeHelpers.GetUninitializedObject(typeof(GpuTexture));
        SetTextureGeneration(texture, generation);
        return texture;
    }

    private static void SetTextureGeneration(GpuTexture texture, uint generation)
    {
        var setter = typeof(GpuTexture)
            .GetProperty(nameof(GpuTexture.Generation))!
            .GetSetMethod(nonPublic: true);
        Assert.NotNull(setter);
        setter.Invoke(texture, new object[] { generation });
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repo file '{Path.Combine(pathParts)}'.");
    }
}
