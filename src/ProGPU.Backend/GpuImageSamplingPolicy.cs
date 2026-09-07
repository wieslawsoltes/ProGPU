using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>Selects the implementation of base-level nearest/linear/Fant image sampling.</summary>
public enum GpuImageSamplingPreference
{
    Automatic,
    NativeSampler,
    ExplicitShader
}

/// <summary>The same-device implementation selected before renderer resources are created.</summary>
public enum GpuImageSamplingPath
{
    NativeSampler,
    ExplicitShader
}

public enum GpuTilePageSamplingPath
{
    ExplicitShader,
    UnsupportedForcedNativeSampler
}

/// <summary>
/// Base-level image sampling policy, including the bounded Fant footprint.
/// Cubic, mipmapped and anisotropic sampling retain their own algorithms; this
/// policy never reduces those modes to a base-level approximation. No path reads
/// pixels back to the CPU.
/// </summary>
public static class GpuImageSamplingPolicy
{
    /// <summary>Occupied pooled pages require per-tap subregion addressing.</summary>
    public static GpuTilePageSamplingPath ResolveTilePagePath(GpuImageSamplingPreference preference) =>
        preference switch
        {
            GpuImageSamplingPreference.Automatic or GpuImageSamplingPreference.ExplicitShader =>
                GpuTilePageSamplingPath.ExplicitShader,
            GpuImageSamplingPreference.NativeSampler => GpuTilePageSamplingPath.UnsupportedForcedNativeSampler,
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        };

    public const string EnvironmentVariable = "PROGPU_IMAGE_SAMPLING";

    // Shared Texture.wgsl vertex encoding, outside the valid cubic B/C range.
    public const float ExplicitLinearCoefficient = -64f;
    public const float ExplicitNearestCoefficient = -128f;
    public const float ExplicitFantCoefficient = -256f;

    public static GpuImageSamplingPreference ReadEnvironmentPreference() =>
        ParsePreference(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static GpuImageSamplingPreference ParsePreference(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" or "automatic" or "fastest" => GpuImageSamplingPreference.Automatic,
            "native" or "native-sampler" => GpuImageSamplingPreference.NativeSampler,
            "shader" or "explicit-shader" => GpuImageSamplingPreference.ExplicitShader,
            _ => throw new InvalidOperationException(
                $"Environment variable {EnvironmentVariable} has unsupported value '{value}'.")
        };

    public static GpuImageSamplingPath Resolve(
        GpuImageSamplingPreference preference, BackendType backend, string? adapterName)
    {
        bool nativeSamplerKnownUnsupported = backend == BackendType.D3D12 &&
            adapterName?.Contains("Parallels Display Adapter", StringComparison.OrdinalIgnoreCase) == true;
        return preference switch
        {
            GpuImageSamplingPreference.Automatic => nativeSamplerKnownUnsupported
                ? GpuImageSamplingPath.ExplicitShader : GpuImageSamplingPath.NativeSampler,
            GpuImageSamplingPreference.ExplicitShader => GpuImageSamplingPath.ExplicitShader,
            GpuImageSamplingPreference.NativeSampler => nativeSamplerKnownUnsupported
                ? throw new NotSupportedException(
                    "Native base-level image sampling is not qualified on the Parallels D3D12 adapter. " +
                    "Select automatic or explicit-shader sampling.")
                : GpuImageSamplingPath.NativeSampler,
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        };
    }
}
