using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProGPU.Backend;

/// <summary>Compiler selection for a ProGPU-owned, pinned wgpu-native D3D12 instance.</summary>
public enum WgpuDx12ShaderCompiler
{
    Automatic,
    Fxc,
    Dxc
}

/// <summary>
/// Immutable startup configuration. Automatic retains the qualified backend default;
/// explicit DXC requires the feature-enabled native artifact and real compiler libraries.
/// This option does not change the compiler of a borrowed Dawn/browser device.
/// </summary>
public sealed record WgpuDx12CompilerOptions(
    WgpuDx12ShaderCompiler Preference = WgpuDx12ShaderCompiler.Automatic,
    string? LibraryDirectory = null)
{
    public const string CompilerEnvironmentVariable = "PROGPU_DX12_SHADER_COMPILER";
    public const string DirectoryEnvironmentVariable = "PROGPU_DX12_COMPILER_DIRECTORY";
    public static WgpuDx12CompilerOptions Default { get; } = new();

    public static WgpuDx12CompilerOptions FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(CompilerEnvironmentVariable),
        Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable));

    public static WgpuDx12CompilerOptions Parse(string? compiler, string? directory = null)
    {
        WgpuDx12ShaderCompiler preference = compiler?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" or "automatic" => WgpuDx12ShaderCompiler.Automatic,
            "fxc" => WgpuDx12ShaderCompiler.Fxc,
            "dxc" => WgpuDx12ShaderCompiler.Dxc,
            _ => throw new ArgumentException($"Unsupported {CompilerEnvironmentVariable} value '{compiler}'.", nameof(compiler))
        };
        var result = preference == WgpuDx12ShaderCompiler.Automatic && string.IsNullOrWhiteSpace(directory)
            ? Default : new WgpuDx12CompilerOptions(preference, directory);
        result.Validate();
        return result;
    }

    internal void Validate()
    {
        if (Preference is not (WgpuDx12ShaderCompiler.Automatic or WgpuDx12ShaderCompiler.Fxc or WgpuDx12ShaderCompiler.Dxc))
            throw new ArgumentOutOfRangeException(nameof(Preference));
        if (LibraryDirectory != null && (Preference != WgpuDx12ShaderCompiler.Dxc ||
            string.IsNullOrWhiteSpace(LibraryDirectory) || LibraryDirectory.Contains('\0') || !Path.IsPathFullyQualified(LibraryDirectory)))
            throw new ArgumentException("A compiler directory must be an absolute path and requires explicit DXC selection.", nameof(LibraryDirectory));
    }

    internal void ValidateOwnership(bool ownsWindowsNativeInstance)
    {
        Validate();
        if (!ownsWindowsNativeInstance && Preference != WgpuDx12ShaderCompiler.Automatic)
            throw new NotSupportedException("Explicit D3D12 compiler selection requires a ProGPU-owned Windows wgpu-native instance.");
    }
}

// Initialization-only metadata: never read, hash or parse files in a render/query hot path.
internal readonly record struct WgpuDx12CompilerPaths(string Compiler, string Validator)
{
    internal byte[] CompilerUtf8 => Encoding.UTF8.GetBytes(Compiler + '\0');
    internal byte[] ValidatorUtf8 => Encoding.UTF8.GetBytes(Validator + '\0');
}

internal static partial class WgpuDx12CompilerArtifact
{
    internal const string NativeRevision = "33133da4ec5a0174cb21539ef2d3346f75200411";
    internal const string HeadersRevision = "aef5e428a1fdab2ea770581ae7c95d8779984e0a";
    internal const string WgpuRevision = "87576b72b37c6b78b41104eb25fc31893af94092";
    internal const string LockSha256 = "3740ec190969a9f4d7a244bed8790c1b947df4b943bb4d71045bf3a6f4e01163";
    internal const string NagaVersion = "0.19.2";
    internal const string ShaderPatchSha256 = "048251c667d3807f8bf28f6dc7d7d30f47cf08316c86c4b6f627f247d8575847";
    internal const string OriginalWriterSha256 = "8e61501bae6982273a525734d3e446694d4bf7a081419ec312c32b354e89d3ef";
    internal const string PatchedWriterSha256 = "26ce406b7a707d7d46f903e86e0f6dbdbc1693ae0c7b0e0a88811678b6dadad2";
    private const string BackendAbi = "PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05";

    internal static WgpuDx12CompilerPaths Validate(string libraryPath, string? compilerDirectory, Architecture architecture)
    {
        string rid = architecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("The pinned DXC artifact supports Windows x64 and ARM64.")
        };
        string directory = Path.GetDirectoryName(libraryPath) ?? throw new ArgumentException("Missing native library directory.");
        string manifestPath = Path.Combine(directory, "wgpu-native-build.json");
        if (!File.Exists(manifestPath))
            throw new NotSupportedException($"Forced DXC requires the feature-enabled native build manifest beside the loaded library: {manifestPath}");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = manifest.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 2 ||
            root.GetProperty("rid").GetString() != rid ||
            root.GetProperty("backendAbi").GetString() != BackendAbi ||
            root.GetProperty("nativeRevision").GetString() != NativeRevision ||
            root.GetProperty("headersRevision").GetString() != HeadersRevision ||
            root.GetProperty("wgpuRevision").GetString() != WgpuRevision ||
            root.GetProperty("compilerFeature").GetString() != "dxc_shader_compiler" ||
            root.GetProperty("lockSha256").GetString() != LockSha256 ||
            !HasReviewedShaderInputs(root))
            throw new NotSupportedException("The loaded native compiler artifact does not match the pinned DXC feature/ABI contract.");
        using (var library = File.OpenRead(libraryPath))
        {
            string actualHash = Convert.ToHexString(SHA256.HashData(library));
            if (!string.Equals(actualHash, root.GetProperty("librarySha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("The loaded native library hash differs from its compiler build manifest.");
        }
        ValidateMachine(libraryPath, architecture);
        string compilerRoot = compilerDirectory ?? directory;
        string compiler = Path.Combine(compilerRoot, "dxcompiler.dll");
        string validator = Path.Combine(compilerRoot, "dxil.dll");
        ValidateMachine(compiler, architecture);
        ValidateMachine(validator, architecture);
        return new(compiler, validator);
    }

    private static bool HasReviewedShaderInputs(JsonElement root)
    {
        if (!root.TryGetProperty("shaderInputs", out JsonElement inputs) ||
            inputs.ValueKind != JsonValueKind.Object ||
            !HasString(inputs, "wgpuRevision", WgpuRevision) ||
            !HasString(inputs, "nagaVersion", NagaVersion) ||
            !HasString(inputs, "patchSha256", ShaderPatchSha256) ||
            !inputs.TryGetProperty("modifiedFiles", out JsonElement files) ||
            files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 1)
            return false;
        JsonElement writer = files[0];
        return writer.ValueKind == JsonValueKind.Object &&
            HasString(writer, "path", "naga/src/back/hlsl/writer.rs") &&
            HasString(writer, "originalSha256", OriginalWriterSha256) &&
            HasString(writer, "patchedSha256", PatchedWriterSha256);
    }

    private static bool HasString(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String && property.GetString() == expected;

    internal static void ValidateMachine(string path, Architecture architecture)
    {
        ushort expected = architecture switch
        {
            Architecture.X64 => 0x8664,
            Architecture.Arm64 => 0xAA64,
            _ => throw new PlatformNotSupportedException("Unsupported Windows compiler architecture.")
        };
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadUInt16() != 0x5A4D) throw new BadImageFormatException($"Missing PE DOS header: {path}");
        reader.BaseStream.Position = 0x3C;
        uint offset = reader.ReadUInt32();
        reader.BaseStream.Position = offset;
        if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != expected)
            throw new BadImageFormatException($"The compiler dependency does not match {architecture}: {path}");
    }

    internal static unsafe string GetLoadedLibraryPath(nint library)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (library == 0) throw new InvalidOperationException("The native library has not been loaded.");
        // Windows' maximum extended path, bounded and used only for explicit startup.
        char[] buffer = new char[32768];
        fixed (char* path = buffer)
        {
            uint count = GetModuleFileNameW(library, path, (uint)buffer.Length);
            if (count == 0 || count >= buffer.Length)
                throw new InvalidOperationException("Cannot identify the loaded native compiler dependency.");
            return new string(path, 0, (int)count);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetModuleFileNameW(nint module, char* fileName, uint size);
}
