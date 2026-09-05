using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public class ImageSamplingPolicyTests
{
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
