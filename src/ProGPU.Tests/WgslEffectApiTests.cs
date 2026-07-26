using ProGPU.Backend;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public sealed class WgslEffectApiTests
{
    [Fact]
    public void DefinitionRequiresStableKeyAndSource()
    {
        Assert.Throws<ArgumentException>(() => new WgslEffectDefinition("", "fn effect() {}"));
        Assert.Throws<ArgumentException>(() => new WgslEffectDefinition("example", ""));
    }

    [Fact]
    public void BuiltInEffectsUseNeutralEntryPointAndDocumentCost()
    {
        Assert.Contains("fn progpu_effect_main", WgslEffectShaders.PassThrough.Source, StringComparison.Ordinal);
        Assert.Contains("fn progpu_effect_main", WgslEffectShaders.VoxelWeather.Source, StringComparison.Ordinal);
        Assert.Contains("// Time complexity:", WgslEffectShaders.VoxelWeather.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("wpf_", WgslEffectShaders.VoxelWeather.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SamplerBindingUsesWebGpuEffectRegisterRange()
    {
        var sampler = new WgslEffectSampler(15, null, TextureSamplingMode.Nearest);

        Assert.Equal(15, sampler.Binding);
        Assert.Equal(TextureSamplingMode.Nearest, sampler.SamplingMode);
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Binding = 16);
    }
}
