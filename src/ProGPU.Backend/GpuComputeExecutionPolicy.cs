using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Selects how ProGPU executes workloads that have qualified compute, raster,
/// and CPU implementations.
/// </summary>
public enum GpuComputeExecutionPreference
{
    Fastest,
    NativeCompute,
    RasterShader,
    IntrinsicSimdCpu,
    ScalarCpu
}

/// <summary>The concrete implementation selected for one compute workload.</summary>
public enum GpuComputeExecutionPath
{
    NativeCompute,
    RasterShader,
    IntrinsicSimdCpu,
    ScalarCpu
}

/// <summary>
/// Resolves typed compute execution preferences without probing implementation
/// objects or backend-private state.
/// </summary>
public static class GpuComputeExecutionPolicy
{
    public const string EnvironmentVariable =
        "PROGPU_COMPUTE_EXECUTION";

    public static GpuComputeExecutionPreference ReadEnvironmentPreference()
    {
        string? value = Environment.GetEnvironmentVariable(
            EnvironmentVariable);
        return ParsePreference(value);
    }

    public static GpuComputeExecutionPreference ParsePreference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return GpuComputeExecutionPreference.Fastest;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "fastest" or "auto" or "automatic" =>
                GpuComputeExecutionPreference.Fastest,
            "compute" or "native-compute" =>
                GpuComputeExecutionPreference.NativeCompute,
            "raster" or "raster-shader" or "fragment" =>
                GpuComputeExecutionPreference.RasterShader,
            "simd" or "intrinsic-simd" or "cpu-simd" =>
                GpuComputeExecutionPreference.IntrinsicSimdCpu,
            "scalar" or "cpu-scalar" or "reference" =>
                GpuComputeExecutionPreference.ScalarCpu,
            _ => throw new InvalidOperationException(
                $"Environment variable {EnvironmentVariable} has unsupported value '{value}'.")
        };
    }

    /// <summary>
    /// Resolves monochrome glyph coverage. The Parallels D3D12 profile rejects
    /// the compute implementation but supports the equivalent R8 render pass,
    /// so automatic mode remains on the GPU.
    /// </summary>
    public static GpuComputeExecutionPath ResolveGlyphRasterization(
        GpuComputeExecutionPreference preference,
        BackendType backendType,
        string? adapterName)
    {
        bool nativeComputeKnownUnsupported =
            IsNativeComputeKnownUnsupported(backendType, adapterName);
        if (preference != GpuComputeExecutionPreference.Fastest)
        {
            return preference switch
            {
                GpuComputeExecutionPreference.NativeCompute =>
                    nativeComputeKnownUnsupported
                        ? throw new NotSupportedException(
                            "Native glyph compute is not supported by the " +
                            $"'{adapterName}' {backendType} adapter. Select " +
                            "'raster' for the equivalent GPU shader path or " +
                            "'simd' for the intrinsic CPU fallback.")
                        : GpuComputeExecutionPath.NativeCompute,
                GpuComputeExecutionPreference.RasterShader =>
                    GpuComputeExecutionPath.RasterShader,
                GpuComputeExecutionPreference.IntrinsicSimdCpu =>
                    GpuComputeExecutionPath.IntrinsicSimdCpu,
                GpuComputeExecutionPreference.ScalarCpu =>
                    GpuComputeExecutionPath.ScalarCpu,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(preference))
            };
        }

        return nativeComputeKnownUnsupported
            ? GpuComputeExecutionPath.RasterShader
            : GpuComputeExecutionPath.NativeCompute;
    }

    public static bool IsNativeComputeKnownUnsupported(
        BackendType backendType,
        string? adapterName) =>
        backendType == BackendType.D3D12 &&
        adapterName?.Contains(
                "Parallels Display Adapter",
                StringComparison.OrdinalIgnoreCase) == true;
}
