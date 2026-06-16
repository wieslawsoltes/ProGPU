using System.Reflection;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class RenderPipelineCacheTests
{
    [Fact]
    public void SrcOverBlendUsesSrcAlphaForStraightAlphaSources()
    {
        var blend = CreateBlendState(GpuBlendMode.SrcOver, GpuTextureAlphaMode.Straight);

        Assert.Equal(BlendFactor.SrcAlpha, blend.Color.SrcFactor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, blend.Color.DstFactor);
        Assert.Equal(BlendFactor.One, blend.Alpha.SrcFactor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, blend.Alpha.DstFactor);
    }

    [Fact]
    public void SrcOverBlendUsesOneForPremultipliedSources()
    {
        var blend = CreateBlendState(GpuBlendMode.SrcOver, GpuTextureAlphaMode.Premultiplied);

        Assert.Equal(BlendFactor.One, blend.Color.SrcFactor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, blend.Color.DstFactor);
        Assert.Equal(BlendFactor.One, blend.Alpha.SrcFactor);
        Assert.Equal(BlendFactor.OneMinusSrcAlpha, blend.Alpha.DstFactor);
    }

    [Fact]
    public void SrcBlendPremultipliesStraightAlphaColorWrites()
    {
        var blend = CreateBlendState(GpuBlendMode.Src, GpuTextureAlphaMode.Straight);

        Assert.Equal(BlendFactor.SrcAlpha, blend.Color.SrcFactor);
        Assert.Equal(BlendFactor.Zero, blend.Color.DstFactor);
        Assert.Equal(BlendFactor.One, blend.Alpha.SrcFactor);
        Assert.Equal(BlendFactor.Zero, blend.Alpha.DstFactor);
    }

    [Fact]
    public void SrcBlendUsesOneForPremultipliedColorWrites()
    {
        var blend = CreateBlendState(GpuBlendMode.Src, GpuTextureAlphaMode.Premultiplied);

        Assert.Equal(BlendFactor.One, blend.Color.SrcFactor);
        Assert.Equal(BlendFactor.Zero, blend.Color.DstFactor);
        Assert.Equal(BlendFactor.One, blend.Alpha.SrcFactor);
        Assert.Equal(BlendFactor.Zero, blend.Alpha.DstFactor);
    }

    [Fact]
    public void PlusBlendPremultipliesStraightAlphaColorWrites()
    {
        var blend = CreateBlendState(GpuBlendMode.Plus, GpuTextureAlphaMode.Straight);

        Assert.Equal(BlendFactor.SrcAlpha, blend.Color.SrcFactor);
        Assert.Equal(BlendFactor.One, blend.Color.DstFactor);
        Assert.Equal(BlendFactor.One, blend.Alpha.SrcFactor);
        Assert.Equal(BlendFactor.One, blend.Alpha.DstFactor);
    }

    [Fact]
    public void PlusBlendUsesOneForPremultipliedColorWrites()
    {
        var blend = CreateBlendState(GpuBlendMode.Plus, GpuTextureAlphaMode.Premultiplied);

        Assert.Equal(BlendFactor.One, blend.Color.SrcFactor);
        Assert.Equal(BlendFactor.One, blend.Color.DstFactor);
        Assert.Equal(BlendFactor.One, blend.Alpha.SrcFactor);
        Assert.Equal(BlendFactor.One, blend.Alpha.DstFactor);
    }

    private static BlendState CreateBlendState(GpuBlendMode blendMode, GpuTextureAlphaMode sourceAlphaMode)
    {
        var method = typeof(RenderPipelineCache).GetMethod(
            "CreateBlendState",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (BlendState)method.Invoke(null, [blendMode, sourceAlphaMode])!;
    }
}
