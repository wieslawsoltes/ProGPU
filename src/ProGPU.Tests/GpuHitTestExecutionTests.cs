using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class GpuHitTestExecutionTests
{
    private static readonly WgpuComputeLimits Limits = new(128UL * 1024 * 1024, 8, 256, 256, 65535);

    [Fact]
    public void UnknownDeviceRetainsSinglePassAndExplicitChoices()
    {
        Assert.Equal(GpuHitTestExecutionPreference.SinglePass, GpuHitTestExecutionPolicy.Resolve(GpuHitTestExecutionPreference.Automatic));
        Assert.Equal(GpuHitTestExecutionPreference.OrderedStages, GpuHitTestExecutionPolicy.Resolve(GpuHitTestExecutionPreference.OrderedStages));
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuHitTestExecutionPolicy.Resolve((GpuHitTestExecutionPreference)99));
    }

    [Theory]
    [InlineData(BackendType.D3D12, WgpuDx12ShaderCompiler.Fxc, GpuHitTestExecutionPreference.OrderedStages)]
    [InlineData(BackendType.D3D12, WgpuDx12ShaderCompiler.Dxc, GpuHitTestExecutionPreference.OrderedStages)]
    [InlineData(BackendType.D3D12, WgpuDx12ShaderCompiler.Automatic, GpuHitTestExecutionPreference.SinglePass)]
    [InlineData(BackendType.D3D12, null, GpuHitTestExecutionPreference.SinglePass)]
    [InlineData(BackendType.Metal, WgpuDx12ShaderCompiler.Fxc, GpuHitTestExecutionPreference.SinglePass)]
    [InlineData(BackendType.Vulkan, WgpuDx12ShaderCompiler.Fxc, GpuHitTestExecutionPreference.SinglePass)]
    [InlineData(BackendType.Metal, WgpuDx12ShaderCompiler.Dxc, GpuHitTestExecutionPreference.SinglePass)]
    [InlineData(BackendType.Vulkan, WgpuDx12ShaderCompiler.Dxc, GpuHitTestExecutionPreference.SinglePass)]
    [InlineData(BackendType.Undefined, WgpuDx12ShaderCompiler.Fxc, GpuHitTestExecutionPreference.SinglePass)]
    public void AutomaticUsesActualD3D12CompilerIdentity(
        BackendType backend, WgpuDx12ShaderCompiler? compiler, GpuHitTestExecutionPreference expected)
    {
        Assert.Equal(expected, GpuHitTestExecutionPolicy.Resolve(GpuHitTestExecutionPreference.Automatic, backend, compiler));
        Assert.Equal(GpuHitTestExecutionPreference.SinglePass,
            GpuHitTestExecutionPolicy.Resolve(GpuHitTestExecutionPreference.SinglePass, backend, compiler));
        Assert.Equal(GpuHitTestExecutionPreference.OrderedStages,
            GpuHitTestExecutionPolicy.Resolve(GpuHitTestExecutionPreference.OrderedStages, backend, compiler));
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuHitTestExecutionPolicy.Resolve((GpuHitTestExecutionPreference)99, backend, compiler));
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(64u, true)]
    [InlineData(65u, true)]
    [InlineData(4194240u, true)]
    [InlineData(4194241u, false)]
    [InlineData(0u, false)]
    [InlineData(uint.MaxValue, false)]
    public void DispatchBoundsAreCheckedWithoutIntegerWrap(uint count, bool admitted) =>
        Assert.Equal(admitted, Limits.AdmitsOrderedHitQuery(count, WgpuContext.DefaultMaxBufferSize));

    [Fact]
    public void MissingOrInsufficientDeviceLimitsReject()
    {
        Assert.False(default(WgpuComputeLimits).AdmitsOrderedHitQuery(1, 256));
        Assert.False(Limits.AdmitsOrderedHitQuery(1, 39));
        Assert.True(Limits.AdmitsOrderedHitQuery(1, 40));
        Assert.False((Limits with { MaxStorageBufferBindingSize = 39 }).AdmitsOrderedHitQuery(1, 256));
        Assert.False((Limits with { MaxStorageBuffersPerShaderStage = 6 }).AdmitsOrderedHitQuery(1, 256));
        Assert.False((Limits with { MaxComputeInvocationsPerWorkgroup = 63 }).AdmitsOrderedHitQuery(1, 256));
        Assert.False((Limits with { MaxComputeWorkgroupSizeX = 63 }).AdmitsOrderedHitQuery(1, 256));
        Assert.False((Limits with { MaxComputeWorkgroupsPerDimension = 0 }).AdmitsOrderedHitQuery(1, 256));
    }
}
