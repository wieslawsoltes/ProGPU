using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using W = WebGpuSharp;

namespace ProGPU.Backend.Dawn;

// Startup failure attribution only. This does not load a GPU dependency, retry
// an adapter request, or establish native ABI/compiler/adapter admission.
internal static partial class DawnAdapterRequestFailureDiagnostics
{
    internal const string DataKey = "ProGPU.Dawn.AdapterRequestFailure";
    internal const int MaximumSnapshotCharacters = 4096;
    internal const long MaximumProviderFileBytes = 64 * 1024 * 1024;
    internal const int MaximumPeHeaderOffset = 1024 * 1024;
    private const int MaximumModulePathCharacters = 4096;
    private const string Truncation = " [truncated]";
    private static readonly string[] RuntimeModuleNames =
        ["d3d12.dll", "dxgi.dll", "d3dcompiler_47.dll", "dxcompiler.dll", "dxil.dll", "d3d10warp.dll"];

    internal static void Attach(Exception failure, W.BackendType backend, bool forceFallbackAdapter,
        W.FeatureLevel featureLevel, W.PowerPreference powerPreference)
    {
        string? request = null;
        try
        {
            request = $"arch={RuntimeInformation.ProcessArchitecture}; backend={backend}; " +
                $"forceFallbackAdapter={forceFallbackAdapter}; featureLevel={featureLevel}; powerPreference={powerPreference}";
            string provider;
            string[] runtime = new string[RuntimeModuleNames.Length];
            if (OperatingSystem.IsWindows())
            {
                // Borrow the exact process-pinned provider; never resolve/load
                // another module just because the adapter request failed.
                provider = DescribeProvider(DawnNativeProvider.GetModule());
                for (int i = 0; i < runtime.Length; i++) runtime[i] = DescribeLoadedRuntime(RuntimeModuleNames[i]);
            }
            else
            {
                provider = "Windows module snapshot not applicable";
                runtime = [];
            }
            AttachSnapshot(failure, FormatSnapshot(request, provider, runtime), Console.Error);
        }
        catch (Exception diagnosticFailure)
        {
            // This originating adapter exception remains authoritative, even
            // when provider/path/file inspection or stderr itself fails.
            try
            {
                AttachSnapshot(failure, FormatSnapshot(request ?? "startup failure attribution unavailable",
                    $"diagnosticError={diagnosticFailure.GetType().Name}", []), Console.Error);
            }
            catch (Exception) { }
        }
    }

    internal static void AttachSnapshot(Exception failure, string snapshot, TextWriter output)
    {
        try
        {
            string bounded = Bound(snapshot);
            failure.Data[DataKey] = bounded;
            output.WriteLine($"[Dawn adapter request diagnostic] {bounded}");
        }
        catch (Exception)
        {
            // Reporting is never a replacement exception or a recovery path.
        }
    }

    internal static string FormatSnapshot(string request, string provider, ReadOnlySpan<string> runtime)
    {
        var text = new StringBuilder(MaximumSnapshotCharacters);
        bool omitted = false;
        Append("request: ", request);
        Append("caveat: ", "not loaded != missing or required; on-disk identity != mapped-image identity; no runtime admission");
        Append("provider: ", provider);
        // The real snapshot enumerates this exact bounded list, not a complete
        // process/module inventory. Keep any supplied diagnostic tail bounded.
        for (int i = 0; i < Math.Min(runtime.Length, RuntimeModuleNames.Length); i++) Append("runtime: ", runtime[i]);
        if (omitted || runtime.Length > RuntimeModuleNames.Length) text.Append(Truncation);
        return Bound(text.ToString());

        void Append(string label, string value)
        {
            if (text.Length >= MaximumSnapshotCharacters) { omitted = true; return; }
            if (text.Length != 0) text.Append("; ");
            text.Append(label);
            text.Append(Bound(value));
        }
    }

    private static string Bound(string value)
    {
        bool truncated = value.Length > MaximumSnapshotCharacters;
        int count = Math.Min(value.Length, MaximumSnapshotCharacters - (truncated ? Truncation.Length : 0));
        var text = new StringBuilder(MaximumSnapshotCharacters);
        for (int i = 0; i < count; i++) text.Append(char.IsControl(value[i]) ? ' ' : value[i]);
        if (truncated) text.Append(Truncation);
        return text.ToString();
    }

    private static string DescribeProvider(nint module)
    {
        string? path = null;
        try
        {
            path = GetObservedModulePath(module);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = file.Length;
            if (length > MaximumProviderFileBytes) return $"path={path}; disk metadata exceeds {MaximumProviderFileBytes}-byte bound";
            ushort? machine = ReadPeMachine(file);
            file.Position = 0;
            string hash = HashProviderFile(file, length);
            return $"path={path}; diskBytes={length}; diskPeMachine=" +
                (machine.HasValue ? $"0x{machine.Value:X4}" : "unavailable") + $"; diskSha256={hash}";
        }
        catch (Exception error)
        {
            return path is null ? $"module observation unavailable ({error.GetType().Name})"
                : $"path={path}; diskMetadataError={error.GetType().Name}";
        }
    }

    private static unsafe string DescribeLoadedRuntime(string name)
    {
        try
        {
            nint module;
            fixed (char* nativeName = name) module = GetModuleHandleW(nativeName);
            return module == 0 ? $"{name}=not loaded" : $"{name}=loaded; observedPath={GetObservedModulePath(module)}";
        }
        catch (Exception error)
        {
            return $"{name}=observation unavailable ({error.GetType().Name})";
        }
    }

    private static unsafe string GetObservedModulePath(nint module)
    {
        if (module == 0) throw new InvalidOperationException("No already-loaded provider module.");
        Span<char> path = stackalloc char[MaximumModulePathCharacters];
        fixed (char* buffer = path)
        {
            uint count = GetModuleFileNameW(module, buffer, (uint)path.Length);
            if (count == 0 || count >= path.Length)
                throw new InvalidOperationException("Loaded-module path unavailable or exceeds diagnostic bound.");
            return new string(path[..checked((int)count)]);
        }
    }

    // Microsoft PE metadata only, not loader/ABI admission. No image is loaded
    // and unknown/hybrid machine values are reported unchanged rather than
    // accepted as a compiler architecture.
    internal static ushort? ReadPeMachine(Stream file)
    {
        Span<byte> dos = stackalloc byte[64], pe = stackalloc byte[6];
        if (file.Length < dos.Length) return null;
        file.Position = 0; file.ReadExactly(dos);
        if (BinaryPrimitives.ReadUInt16LittleEndian(dos) != 0x5A4D) return null;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(dos[0x3C..]);
        if (offset < dos.Length || offset > MaximumPeHeaderOffset || offset > file.Length - pe.Length) return null;
        file.Position = offset; file.ReadExactly(pe);
        return BinaryPrimitives.ReadUInt32LittleEndian(pe) == 0x00004550
            ? BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) : null;
    }

    // Hash exactly the original bounded length. A concurrently growing file
    // cannot turn failure reporting into an unbounded read.
    internal static string HashProviderFile(Stream file, long length)
    {
        if (length < 0 || length > MaximumProviderFileBytes) throw new ArgumentOutOfRangeException(nameof(length));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[4096];
        long remaining = length;
        while (remaining != 0)
        {
            int count = file.Read(buffer[..(int)Math.Min(remaining, buffer.Length)]);
            if (count == 0) throw new EndOfStreamException("Provider file changed during diagnostic capture.");
            hash.AppendData(buffer[..count]); remaining -= count;
        }
        if (file.Length != length) throw new IOException("Provider file length changed during diagnostic capture.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Windows API calling convention, not a Dawn C export or a new provider.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetModuleFileNameW(nint module, char* fileName, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial nint GetModuleHandleW(char* moduleName);
}
