using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>Startup backend choice for a ProGPU-owned wgpu-native instance.</summary>
public enum WgpuNativeBackend
{
    Automatic,
    Vulkan,
    OpenGL,
    Metal,
    D3D12
}

/// <summary>
/// Restricts native instance creation, not just adapter selection. Automatic preserves
/// existing platform defaults. An unavailable explicit backend fails without fallback.
/// Borrowed browser/Dawn devices cannot be reconfigured through this option.
/// </summary>
public sealed record WgpuNativeBackendOptions(WgpuNativeBackend Preference = WgpuNativeBackend.Automatic)
{
    public const string EnvironmentVariable = "PROGPU_WGPU_BACKEND";
    public const string CompatibleEnvironmentVariable = "WGPU_BACKEND";
    public static WgpuNativeBackendOptions Default { get; } = new();

    public static WgpuNativeBackendOptions FromEnvironment() => FromEnvironmentValues(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        Environment.GetEnvironmentVariable(CompatibleEnvironmentVariable));

    // A present ProGPU setting, including explicit automatic, overrides the alias.
    internal static WgpuNativeBackendOptions FromEnvironmentValues(string? preferred, string? compatible) =>
        Parse(preferred ?? compatible);

    public static WgpuNativeBackendOptions Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "auto" or "automatic" => Default,
        "vulkan" => new(WgpuNativeBackend.Vulkan),
        "gl" or "gles" or "opengl" => new(WgpuNativeBackend.OpenGL),
        "metal" => new(WgpuNativeBackend.Metal),
        "dx12" or "d3d12" => new(WgpuNativeBackend.D3D12),
        _ => throw new ArgumentException($"Unsupported native WebGPU backend '{value}'. Choose automatic, vulkan, gl, metal or dx12.", nameof(value))
    };

    internal void Validate()
    {
        if (Preference is not (WgpuNativeBackend.Automatic or WgpuNativeBackend.Vulkan or
            WgpuNativeBackend.OpenGL or WgpuNativeBackend.Metal or WgpuNativeBackend.D3D12))
            throw new ArgumentOutOfRangeException(nameof(Preference));
    }

    internal uint ResolveInstanceBackends(bool windows, bool android, bool ios, WgpuDx12ShaderCompiler compiler)
    {
        Validate();
        // Mobile native surfaces retain their existing mandatory backend contract.
        if ((android && Preference is not (WgpuNativeBackend.Automatic or WgpuNativeBackend.Vulkan)) ||
            (ios && Preference is not (WgpuNativeBackend.Automatic or WgpuNativeBackend.Metal)))
            throw new NotSupportedException("The requested backend conflicts with the native mobile surface backend.");
        if (compiler != WgpuDx12ShaderCompiler.Automatic &&
            Preference is not (WgpuNativeBackend.Automatic or WgpuNativeBackend.D3D12))
            throw new NotSupportedException("Explicit D3D12 compiler selection requires the D3D12 backend.");

        // Public WGPUInstanceBackend bits from the repository's pinned wgpu.h ABI.
        return Preference switch
        {
            WgpuNativeBackend.Vulkan => 1u << 0,
            WgpuNativeBackend.OpenGL => 1u << 1,
            WgpuNativeBackend.Metal => 1u << 2,
            WgpuNativeBackend.D3D12 => 1u << 3,
            _ => windows ? 1u << 3 : android ? 1u << 0 : ios ? 1u << 2 : 0u
        };
    }

    internal void ValidateOwnership(bool ownsNativeInstance)
    {
        Validate();
        if (!ownsNativeInstance && Preference != WgpuNativeBackend.Automatic)
            throw new NotSupportedException("Explicit native backend selection requires a ProGPU-owned wgpu-native instance.");
    }

    // Used before sharing any owner handles and after requesting an owned adapter.
    internal void ValidateAdapter(BackendType actual)
    {
        Validate();
        bool matches = Preference switch
        {
            WgpuNativeBackend.Automatic => true,
            WgpuNativeBackend.Vulkan => actual == BackendType.Vulkan,
            WgpuNativeBackend.OpenGL => actual is BackendType.OpenGL or BackendType.OpenGles,
            WgpuNativeBackend.Metal => actual == BackendType.Metal,
            WgpuNativeBackend.D3D12 => actual == BackendType.D3D12,
            _ => false
        };
        if (!matches)
            throw new NotSupportedException($"Requested native backend {Preference}, but the actual device uses {actual}; no backend fallback is permitted.");
    }
}
