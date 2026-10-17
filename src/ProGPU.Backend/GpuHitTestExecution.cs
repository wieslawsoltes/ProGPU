using Silk.NET.WebGPU;

namespace ProGPU.Backend;

public enum GpuHitTestExecutionPreference
{
    Automatic,
    SinglePass,
    OrderedStages
}

/// <summary>Actual device limits. A zero/default snapshot does not admit staged queries.</summary>
public readonly record struct WgpuComputeLimits(
    ulong MaxStorageBufferBindingSize,
    uint MaxStorageBuffersPerShaderStage,
    uint MaxComputeInvocationsPerWorkgroup,
    uint MaxComputeWorkgroupSizeX,
    uint MaxComputeWorkgroupsPerDimension)
{
    public bool AdmitsOrderedHitQuery(uint referenceCount, ulong maxBufferSize)
    {
        ulong bytes = 32UL + 8UL * referenceCount;
        return referenceCount != 0 && referenceCount != uint.MaxValue &&
            bytes <= uint.MaxValue && bytes <= maxBufferSize &&
            bytes <= MaxStorageBufferBindingSize && MaxStorageBuffersPerShaderStage >= 7 &&
            MaxComputeInvocationsPerWorkgroup >= 64 && MaxComputeWorkgroupSizeX >= 64 &&
            (referenceCount + 63UL) / 64UL <= MaxComputeWorkgroupsPerDimension;
    }
}

public static class GpuHitTestExecutionPolicy
{
    public static GpuHitTestExecutionPreference ReadEnvironmentPreference() =>
        Environment.GetEnvironmentVariable("PROGPU_HIT_TEST_EXECUTION")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => GpuHitTestExecutionPreference.Automatic,
            "single-pass" => GpuHitTestExecutionPreference.SinglePass,
            "ordered-stages" => GpuHitTestExecutionPreference.OrderedStages,
            _ => throw new ArgumentException("PROGPU_HIT_TEST_EXECUTION must be auto, single-pass or ordered-stages.")
        };

    public static GpuHitTestExecutionPreference Resolve(GpuHitTestExecutionPreference preference) =>
        Resolve(preference, BackendType.Undefined, null);

    // Monolithic queries are not reliable on the pinned D3D12 runtime with
    // either owned compiler; DXC also reproduces the system-WARP spill fault.
    // Select the same ordered GPU algorithm for both renderer implementations,
    // using actual owned-device compiler identity, not OS/adapter-name guesses.
    // Borrowed/unknown compilers retain explicit host selection. Device/index
    // limits still reject unsupported ordered dispatch; there is no fallback.
    public static GpuHitTestExecutionPreference Resolve(
        GpuHitTestExecutionPreference preference,
        BackendType adapterBackend,
        WgpuDx12ShaderCompiler? shaderCompiler) => preference switch
    {
        GpuHitTestExecutionPreference.Automatic => adapterBackend == BackendType.D3D12 &&
            shaderCompiler is WgpuDx12ShaderCompiler.Fxc or WgpuDx12ShaderCompiler.Dxc
                ? GpuHitTestExecutionPreference.OrderedStages
                : GpuHitTestExecutionPreference.SinglePass,
        GpuHitTestExecutionPreference.SinglePass => GpuHitTestExecutionPreference.SinglePass,
        GpuHitTestExecutionPreference.OrderedStages => GpuHitTestExecutionPreference.OrderedStages,
        _ => throw new ArgumentOutOfRangeException(nameof(preference))
    };
}
