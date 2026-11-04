using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProGPU.Backend;

// Package acceptance only: no native load, device creation, input or product policy.
internal static partial class WindowsOwnedRuntimeValidation
{
    internal static unsafe void Verify(WgpuContext context)
    {
        if (!OperatingSystem.IsWindows()) return;
        nint entry = context.Wgpu.Context.GetProcAddress("wgpuCreateInstance");
        // FROM_ADDRESS | UNCHANGED_REFCOUNT: identify the module of the actual API
        // entry point without selecting another DLL or changing module ownership.
        if (entry == 0 || GetModuleHandleExW(0x6, entry, out nint module) == 0)
            throw new InvalidDataException("Cannot identify the initialized WebGPU module.");
        char[] buffer = new char[32768];
        string path;
        fixed (char* text = buffer)
        {
            uint count = GetModuleFileNameW(module, text, (uint)buffer.Length);
            if (count == 0 || count >= buffer.Length)
                throw new InvalidDataException("Cannot read the initialized WebGPU module path.");
            path = new string(text, 0, checked((int)count));
        }
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new InvalidDataException("Unexpected Windows package architecture.")
        };
        string manifestPath = Path.Combine(Path.GetDirectoryName(path)!, "wgpu-native-build.json");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        using FileStream library = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(library)).ToLowerInvariant();
        ValidateReceipt(manifest.RootElement, rid, hash);
        library.Position = 0;
        using var pe = new BinaryReader(library, System.Text.Encoding.UTF8, leaveOpen: true);
        if (library.Length < 64 || pe.ReadUInt16() != 0x5A4D)
            throw new InvalidDataException("Loaded runtime has an invalid DOS header.");
        library.Position = 0x3C;
        uint offset = pe.ReadUInt32();
        if (offset < 64 || offset > library.Length - 26)
            throw new InvalidDataException("Loaded runtime PE header is truncated.");
        library.Position = offset;
        if (pe.ReadUInt32() != 0x4550 || pe.ReadUInt16() != (rid == "win-x64" ? 0x8664 : 0xAA64))
            throw new InvalidDataException("Loaded runtime architecture differs from the consumer.");
        Console.WriteLine($"package-consumer: owned-wgpu path={path}; sha256={hash}; rid={rid}; " +
            $"backend={context.AdapterBackendType}; compiler={context.SelectedDx12ShaderCompiler?.ToString() ?? "not-D3D12"}; " +
            $"receipt={manifestPath}");
    }

    internal static void ValidateReceipt(JsonElement receipt, string rid, string actualHash)
    {
        using JsonDocument native = ReadPin("WindowsNativePin.json");
        using JsonDocument build = ReadPin("WindowsBuildPin.json");
        using JsonDocument shader = ReadPin("WindowsShaderPin.json");
        try
        {
            if (receipt.GetProperty("schemaVersion").GetInt32() != 2 ||
                receipt.GetProperty("rid").GetString() != rid ||
                !Matches(receipt, "backendAbi", native.RootElement, "backendAbi") ||
                !Matches(receipt, "nativeRevision", native.RootElement, "revision") ||
                !Matches(receipt, "headersRevision", native.RootElement, "webGpuHeadersRevision") ||
                !Matches(receipt, "wgpuRevision", build.RootElement, "wgpuRevision") ||
                !Matches(receipt, "compilerFeature", build.RootElement, "feature") ||
                !Matches(receipt, "lockSha256", build.RootElement, "lockSha256") ||
                !EqualPin(receipt.GetProperty("shaderInputs"), shader.RootElement) ||
                receipt.GetProperty("librarySha256").GetString() != actualHash ||
                receipt.GetProperty("files").GetProperty("wgpu_native.dll").GetString() != actualHash)
                throw new InvalidDataException("Loaded Windows runtime differs from its exact embedded producer pins or receipt hash.");
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Malformed Windows runtime producer receipt.", exception);
        }
    }

    private static bool Matches(JsonElement actual, string actualName, JsonElement expected, string expectedName) =>
        actual.GetProperty(actualName).ValueKind == JsonValueKind.String &&
        actual.GetProperty(actualName).GetString() == expected.GetProperty(expectedName).GetString();

    private static bool EqualPin(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind) return false;
        if (expected.ValueKind == JsonValueKind.Object)
        {
            if (actual.EnumerateObject().Count() != expected.EnumerateObject().Count()) return false;
            foreach (JsonProperty property in expected.EnumerateObject())
                if (!actual.TryGetProperty(property.Name, out JsonElement value) || !EqualPin(value, property.Value)) return false;
            return true;
        }
        if (expected.ValueKind == JsonValueKind.Array)
        {
            if (actual.GetArrayLength() != expected.GetArrayLength()) return false;
            for (int index = 0; index < expected.GetArrayLength(); index++)
                if (!EqualPin(actual[index], expected[index])) return false;
            return true;
        }
        return expected.ValueKind == JsonValueKind.String && actual.GetString() == expected.GetString();
    }

    private static JsonDocument ReadPin(string name)
    {
        using Stream stream = typeof(WindowsOwnedRuntimeValidation).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Missing embedded package producer pin: {name}");
        return JsonDocument.Parse(stream);
    }

    internal static void RunMetadataControls()
    {
        using JsonDocument native = ReadPin("WindowsNativePin.json");
        using JsonDocument build = ReadPin("WindowsBuildPin.json");
        using JsonDocument shader = ReadPin("WindowsShaderPin.json");
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        JsonObject Original(string rid) => new()
        {
            ["schemaVersion"] = 2, ["rid"] = rid,
            ["backendAbi"] = native.RootElement.GetProperty("backendAbi").GetString(),
            ["nativeRevision"] = native.RootElement.GetProperty("revision").GetString(),
            ["headersRevision"] = native.RootElement.GetProperty("webGpuHeadersRevision").GetString(),
            ["wgpuRevision"] = build.RootElement.GetProperty("wgpuRevision").GetString(),
            ["compilerFeature"] = build.RootElement.GetProperty("feature").GetString(),
            ["lockSha256"] = build.RootElement.GetProperty("lockSha256").GetString(),
            ["shaderInputs"] = JsonNode.Parse(shader.RootElement.GetRawText()),
            ["librarySha256"] = hash, ["files"] = new JsonObject { ["wgpu_native.dll"] = hash }
        };
        int passed = 0;
        foreach (string rid in new[] { "win-x64", "win-arm64" })
        {
            using JsonDocument receipt = JsonDocument.Parse(Original(rid).ToJsonString());
            ValidateReceipt(receipt.RootElement, rid, hash);
            passed++;
        }
        Action<JsonObject>[] faults =
        [
            receipt => receipt["schemaVersion"] = 1,
            receipt => receipt["rid"] = "win-arm64",
            receipt => receipt["nativeRevision"] = "foreign",
            receipt => receipt["headersRevision"] = "foreign",
            receipt => receipt["lockSha256"] = "foreign",
            receipt => receipt["librarySha256"] = new string('0', 64),
            receipt => receipt["files"]!["wgpu_native.dll"] = new string('0', 64),
            receipt => receipt["shaderInputs"]!["patchSha256"] = new string('0', 64),
            receipt => receipt["shaderInputs"]!["modifiedFiles"] = new JsonArray(),
            receipt => receipt.Remove("shaderInputs")
        ];
        foreach (Action<JsonObject> fault in faults)
        {
            JsonObject candidate = Original("win-x64");
            fault(candidate);
            using JsonDocument receipt = JsonDocument.Parse(candidate.ToJsonString());
            try { ValidateReceipt(receipt.RootElement, "win-x64", hash); }
            catch (InvalidDataException) { passed++; continue; }
            throw new InvalidDataException("Malformed package producer metadata was accepted.");
        }
        Console.WriteLine($"Windows owned runtime metadata controls passed: {passed}.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetModuleHandleExW(uint flags, nint address, out nint module);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetModuleFileNameW(nint module, char* path, uint count);
}
