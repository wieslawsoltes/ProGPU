using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WebGpuSharp.FFI;

namespace ProGPU.Backend.Dawn;

// Explicit startup only. Native imports remain on the original provider; no
// resolver replacement, process-wide compiler toggle or per-frame file work.
internal static unsafe partial class DawnSystemWarpArtifact
{
    internal const string DawnRevision = "01249a97332468dbdd6cf5edb8dd7bae77875de5";
    internal const string HeaderSha256 = "32f8063fa2aa5977da27d8a39479581ac834e6cb0cf39f2f9bd6de928a998e5d";
    private const string OriginalBuilderRevision = "a63b42357c8c081cf6a1ace312670b2bb61680c6";
    private static readonly object Gate = new();
    private static nint s_companion, s_provider;
    private static string? s_directory;
    private static Exception? s_failure;

    internal static void EnsureAvailable(string? companionDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Explicit Dawn system WARP requires Windows.");
        string directory = Path.GetFullPath(companionDirectory ?? AppContext.BaseDirectory);
        if (companionDirectory != null && !Path.IsPathFullyQualified(companionDirectory))
            throw new ArgumentException("The optional native companion directory must be absolute.", nameof(companionDirectory));
        lock (Gate)
        {
            if (s_failure != null)
                throw new NotSupportedException("The explicit native WARP capability failed initialization.", s_failure);
            if (s_directory != null)
            {
                if (!string.Equals(s_directory, directory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The explicit native WARP companion is immutable after first use.");
                return;
            }
            string rid;
            ushort machine;
            long providerBytes;
            string providerHash, importHash;
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                    rid = "win-x64"; machine = 0x8664; providerBytes = 10779648;
                    providerHash = "f8801b2be52bf1ddd536c72e2e8e77f757f32a85846c3505d0215ef19b444f5f";
                    importHash = "791c493f118cdc0f0a81603d8407f2eff8302e721ec6f5354743070bc1df8463";
                    break;
                case Architecture.Arm64:
                    rid = "win-arm64"; machine = 0xAA64; providerBytes = 11457536;
                    providerHash = "68e388593559c1a5dad9537873b0377f02aaec55d5f30b5eb5246b8df9743dc6";
                    importHash = "bc0a81478c5568614e9bcaff88f8520a2402831fdfaa1304ad84968531867a73";
                    break;
                default: throw new PlatformNotSupportedException("The exact original companion supports Windows x64 and ARM64.");
            }
            string manifestPath = Path.Combine(directory, "progpu-dawn-system-warp.json");
            string companionPath = Path.Combine(directory, "progpu_dawn_system_warp.dll");
            // Missing optional payload is not a process-wide failed native binding.
            if (!File.Exists(manifestPath) || !File.Exists(companionPath))
                throw new NotSupportedException("The optional original-header WARP companion is not installed.");
            try
            {
                if (new FileInfo(manifestPath).Length > 16384)
                    throw new InvalidDataException("Oversized WARP companion manifest.");
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
                JsonElement root = manifest.RootElement;
                if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                    root.GetProperty("rid").GetString() != rid ||
                    root.GetProperty("dawnRevision").GetString() != DawnRevision ||
                    root.GetProperty("generatedHeaderSha256").GetString() != HeaderSha256 ||
                    root.GetProperty("providerSha256").GetString() != providerHash ||
                    root.GetProperty("importLibrarySha256").GetString() != importHash ||
                    root.GetProperty("originalProducerRun").GetInt64() != 28411907227 ||
                    root.GetProperty("originalBuilderRevision").GetString() != OriginalBuilderRevision ||
                    root.GetProperty("status").GetString() != "unqualified-original-header-companion")
                    throw new NotSupportedException("The companion does not match the verified original Dawn release/header contract.");
                ValidateFile(companionPath, machine, root.GetProperty("companionSha256").GetString()!,
                    root.GetProperty("companionBytes").GetInt64());
                // Retain actual original modules for process lifetime, like native
                // P/Invoke binding caches. Returned identity handles are borrowed.
                s_provider = NativeLibrary.Load("webgpu_dawn", typeof(WebGPU_FFI).Assembly, null);
                string actualProviderPath = GetModulePath(s_provider);
                ValidateFile(actualProviderPath, machine, providerHash, providerBytes);
                s_companion = NativeLibrary.Load(companionPath);
                ValidateFile(GetModulePath(s_companion), machine,
                    root.GetProperty("companionSha256").GetString()!, root.GetProperty("companionBytes").GetInt64());
                int status = DawnSystemWarpNative.LibraryIdentity(out nint actualCompanion, out nint actualProvider);
                if (status < 0 || actualCompanion != s_companion || actualProvider != s_provider)
                    throw new NotSupportedException("The actual bound WARP companion/provider differs from the inspected original modules.");
                s_directory = directory;
            }
            catch (Exception failure)
            {
                // Imports may have cached module addresses. Preserve actual module
                // references and freeze failure; never unload underneath an import.
                s_failure = failure;
                throw;
            }
        }
    }

    internal static void ValidateFile(string path, ushort expectedMachine, string expectedHash, long expectedBytes)
    {
        using var stream = File.OpenRead(path);
        if (expectedBytes <= 0 || expectedBytes > 64 * 1024 * 1024 || stream.Length != expectedBytes)
            throw new BadImageFormatException("Original native payload length mismatch.");
        Span<byte> dos = stackalloc byte[64];
        stream.ReadExactly(dos);
        if (dos[0] != (byte)'M' || dos[1] != (byte)'Z')
            throw new BadImageFormatException("Missing original PE DOS header.");
        uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(dos[0x3C..]);
        if (offset > stream.Length - 24)
            throw new BadImageFormatException("Truncated original PE header.");
        stream.Position = offset;
        Span<byte> pe = stackalloc byte[24];
        stream.ReadExactly(pe);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pe) != 0x4550 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) != expectedMachine)
            throw new BadImageFormatException("The actual native PE architecture does not match the process.");
        stream.Position = 0;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The actual native file hash differs from its original receipt.");
    }

    private static string GetModulePath(nint module)
    {
        Span<char> buffer = stackalloc char[32768];
        fixed (char* path = buffer)
        {
            uint length = GetModuleFileNameW(module, path, (uint)buffer.Length);
            if (length == 0 || length >= buffer.Length)
                throw new InvalidOperationException("Cannot identify the actual loaded native module.");
            return new string(path, 0, (int)length);
        }
    }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetModuleFileNameW(nint module, char* path, uint capacity);
}
