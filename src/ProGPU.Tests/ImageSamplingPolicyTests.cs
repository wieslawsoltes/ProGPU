using ProGPU.Backend;
using ProGPU.Scene;
using System.Numerics;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public class ImageSamplingPolicyTests
{
    [Theory]
    [InlineData(TextureSamplingMode.Nearest, 0f, -128f)]
    [InlineData(TextureSamplingMode.Linear, 0f, -64f)]
    [InlineData(TextureSamplingMode.Linear, -32f, -256f)]
    [InlineData(TextureSamplingMode.Linear, -256f, -256f)]
    [InlineData(TextureSamplingMode.Cubic, 0.33333334f, 0.33333334f)]
    [InlineData(TextureSamplingMode.LinearMipmap, 0f, 0f)]
    public void ExplicitEncodingPreservesRequestedKernel(
        TextureSamplingMode sampling, float input, float expected)
    {
        var coefficients = new Vector2(input, 0.5f);
        Assert.Equal(new Vector2(expected, 0.5f), Compositor.ResolveImageSamplingCoefficients(
            GpuImageSamplingPath.ExplicitShader, sampling, coefficients));
        Assert.Equal(coefficients, Compositor.ResolveImageSamplingCoefficients(
            GpuImageSamplingPath.NativeSampler, sampling, coefficients));
    }

    [Theory]
    [InlineData(GpuImageSamplingPreference.Automatic, GpuTilePageSamplingPath.ExplicitShader)]
    [InlineData(GpuImageSamplingPreference.ExplicitShader, GpuTilePageSamplingPath.ExplicitShader)]
    [InlineData(GpuImageSamplingPreference.NativeSampler, GpuTilePageSamplingPath.UnsupportedForcedNativeSampler)]
    public void TilePagesExposeAnIndependentFailClosedPolicy(GpuImageSamplingPreference preference,
        GpuTilePageSamplingPath expected) => Assert.Equal(expected, GpuImageSamplingPolicy.ResolveTilePagePath(preference));

    [Theory]
    [InlineData(null, GpuImageSamplingPreference.Automatic)]
    [InlineData("auto", GpuImageSamplingPreference.Automatic)]
    [InlineData("fastest", GpuImageSamplingPreference.Automatic)]
    [InlineData(" NATIVE-SAMPLER ", GpuImageSamplingPreference.NativeSampler)]
    [InlineData("explicit-shader", GpuImageSamplingPreference.ExplicitShader)]
    public void ConfigurationIsTyped(string? value, GpuImageSamplingPreference expected) =>
        Assert.Equal(expected, GpuImageSamplingPolicy.ParsePreference(value));

    [Fact]
    public void InvalidConfigurationFailsClosed() =>
        Assert.Throws<InvalidOperationException>(() => GpuImageSamplingPolicy.ParsePreference("cpu"));

    [Theory]
    [InlineData(BackendType.D3D12, "Parallels Display Adapter (WDDM)", GpuImageSamplingPath.ExplicitShader)]
    [InlineData(BackendType.D3D12, "Microsoft Basic Render Driver", GpuImageSamplingPath.NativeSampler)]
    [InlineData(BackendType.Metal, "Apple M3 Pro", GpuImageSamplingPath.NativeSampler)]
    [InlineData(BackendType.Vulkan, "Mesa", GpuImageSamplingPath.NativeSampler)]
    [InlineData(BackendType.Vulkan, "Parallels Display Adapter", GpuImageSamplingPath.NativeSampler)]
    public void AutomaticSelectionIsAdapterSpecific(BackendType backend, string name, GpuImageSamplingPath expected) =>
        Assert.Equal(expected, GpuImageSamplingPolicy.Resolve(GpuImageSamplingPreference.Automatic, backend, name));

    [Fact]
    public void ForcedUnqualifiedSamplerFailsClosed() =>
        Assert.Throws<NotSupportedException>(() => GpuImageSamplingPolicy.Resolve(
            GpuImageSamplingPreference.NativeSampler, BackendType.D3D12, "Parallels Display Adapter (WDDM)"));

    [Fact]
    public void ExplicitShaderDoesNotSelectAnotherDeviceOrCpu() =>
        Assert.Equal(GpuImageSamplingPath.ExplicitShader, GpuImageSamplingPolicy.Resolve(
            GpuImageSamplingPreference.ExplicitShader, BackendType.Metal, "Apple M3 Pro"));

    [Fact]
    public void UnknownPreferenceFailsClosed() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuImageSamplingPolicy.Resolve(
            (GpuImageSamplingPreference)99, BackendType.Metal, "Apple M3 Pro"));
}
