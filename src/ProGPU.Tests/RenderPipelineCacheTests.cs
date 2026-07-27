using System.Reflection;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class RenderPipelineCacheTests
{
    private const string ShaderSourceA =
        """
        @vertex
        fn vs_main() -> @builtin(position) vec4<f32> {
            return vec4<f32>(0.0, 0.0, 0.0, 1.0);
        }

        @fragment
        fn fs_main() -> @location(0) vec4<f32> {
            return vec4<f32>(1.0, 0.0, 0.0, 1.0);
        }
        """;

    private const string ShaderSourceB =
        """
        @vertex
        fn vs_main() -> @builtin(position) vec4<f32> {
            return vec4<f32>(0.0, 0.0, 0.0, 1.0);
        }

        @fragment
        fn fs_main() -> @location(0) vec4<f32> {
            return vec4<f32>(0.0, 1.0, 0.0, 1.0);
        }
        """;

    private const string ComputeShaderSource =
        """
        @compute @workgroup_size(1)
        fn main() {
        }
        """;

    [Fact]
    public unsafe void ShaderModulesAreSharedWithinOneDeviceDomain()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var first = new RenderPipelineCache(context);
        using var second = new RenderPipelineCache(context);

        ShaderModule* firstModule = first.GetOrCreateShader(
            "DeviceDomainShader",
            ShaderSourceA);
        ShaderModule* secondModule = second.GetOrCreateShader(
            "DeviceDomainShader",
            ShaderSourceA);
        RenderPipeline* firstPipeline =
            first.GetOrCreateRenderPipeline(
                "DeviceDomainPipeline",
                firstModule);
        RenderPipeline* secondPipeline =
            second.GetOrCreateRenderPipeline(
                "DeviceDomainPipeline",
                secondModule);

        Assert.True(firstModule == secondModule);
        Assert.True(firstPipeline == secondPipeline);
        Assert.Equal(1, context.CachedDeviceShaderModuleCount);
        Assert.Equal(1, context.CachedDeviceRenderPipelineCount);

        first.Dispose();

        Assert.Equal(1, context.CachedDeviceShaderModuleCount);
        Assert.Equal(1, context.CachedDeviceRenderPipelineCount);
        Assert.True(
            secondModule ==
            second.GetOrCreateShader(
                "DeviceDomainShader",
                ShaderSourceA));

        second.Dispose();

        Assert.Equal(0, context.CachedDeviceShaderModuleCount);
        Assert.Equal(0, context.CachedDeviceRenderPipelineCount);
        context.CleanupPendingResources();
    }

    [Fact]
    public unsafe void ComputePipelinesAreSharedWithinOneDeviceDomain()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var first = new RenderPipelineCache(context);
        using var second = new RenderPipelineCache(context);
        ShaderModule* firstModule = first.GetOrCreateShader(
            "DeviceDomainComputeShader",
            ComputeShaderSource);
        ShaderModule* secondModule = second.GetOrCreateShader(
            "DeviceDomainComputeShader",
            ComputeShaderSource);

        ComputePipeline* firstPipeline =
            first.GetOrCreateComputePipeline(
                "DeviceDomainComputePipeline",
                firstModule);
        ComputePipeline* secondPipeline =
            second.GetOrCreateComputePipeline(
                "DeviceDomainComputePipeline",
                secondModule);

        Assert.True(firstPipeline == secondPipeline);
        Assert.Equal(1, context.CachedDeviceComputePipelineCount);

        first.Dispose();
        Assert.Equal(1, context.CachedDeviceComputePipelineCount);

        second.Dispose();
        Assert.Equal(0, context.CachedDeviceComputePipelineCount);
        context.CleanupPendingResources();
    }

    [Fact]
    public unsafe void DeviceDomainShaderIdentityIncludesSource()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var first = new RenderPipelineCache(context);
        using var second = new RenderPipelineCache(context);

        ShaderModule* firstModule = first.GetOrCreateShader(
            "SameLogicalName",
            ShaderSourceA);
        ShaderModule* secondModule = second.GetOrCreateShader(
            "SameLogicalName",
            ShaderSourceB);

        Assert.True(firstModule != secondModule);
        Assert.Equal(2, context.CachedDeviceShaderModuleCount);
    }

    [Fact]
    public unsafe void OneCacheRejectsLogicalShaderKeyReuseWithDifferentSource()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var cache = new RenderPipelineCache(context);
        cache.GetOrCreateShader("ReusedKey", ShaderSourceA);

        var error = Assert.Throws<InvalidOperationException>(
            () => cache.GetOrCreateShader("ReusedKey", ShaderSourceB));

        Assert.Contains(
            "different WGSL source",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void OneCacheRejectsLogicalRenderPipelineKeyReuseWithDifferentDescriptor()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var cache = new RenderPipelineCache(context);
        ShaderModule* module = cache.GetOrCreateShader(
            "RenderCollisionShader",
            ShaderSourceA);
        cache.GetOrCreateRenderPipeline(
            "RenderCollision",
            module,
            targetFormat: TextureFormat.Bgra8Unorm);

        var error = Assert.Throws<InvalidOperationException>(
            () => cache.GetOrCreateRenderPipeline(
                "RenderCollision",
                module,
                targetFormat: TextureFormat.Rgba8Unorm));

        Assert.Contains(
            "different descriptor",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public unsafe void RepeatedLocalRenderPipelineHitDoesNotAllocateDescriptorCopies()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var cache = new RenderPipelineCache(context);
        ShaderModule* module = cache.GetOrCreateShader(
            "AllocationFreeHitShader",
            ShaderSourceA);
        RenderPipeline* expected = cache.GetOrCreateRenderPipeline(
            "AllocationFreeHit",
            module);
        // Cross the tiered-PGO call threshold before taking the allocation
        // snapshot. Otherwise a full-suite method-order change can charge the
        // runtime's optimized-code transition to this hot-path assertion.
        for (var index = 0; index < 10_000; index++)
        {
            _ = cache.GetOrCreateRenderPipeline(
                "AllocationFreeHit",
                module);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            RenderPipeline* actual = cache.GetOrCreateRenderPipeline(
                "AllocationFreeHit",
                module);
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    "The local pipeline cache returned a different handle.");
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

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

    [Fact]
    public void ModulateBlendMultipliesSourceByDestination()
    {
        var blend = CreateBlendState(GpuBlendMode.Modulate, GpuTextureAlphaMode.Premultiplied);

        Assert.Equal(BlendFactor.Dst, blend.Color.SrcFactor);
        Assert.Equal(BlendFactor.Zero, blend.Color.DstFactor);
        Assert.Equal(BlendFactor.DstAlpha, blend.Alpha.SrcFactor);
        Assert.Equal(BlendFactor.Zero, blend.Alpha.DstFactor);
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
