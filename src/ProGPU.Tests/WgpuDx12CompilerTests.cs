using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class WgpuDx12CompilerTests
{
    [Fact]
    public void RuntimeAdmissionMatchesReviewedBuildPins()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "eng", "progpu-native-wgpu.version.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        using var native = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "eng", "progpu-native-wgpu.version.json")));
        using var compiler = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "eng", "wgpu-dxc", "build-inputs.json")));
        Assert.Equal(WgpuDx12CompilerArtifact.NativeRevision, native.RootElement.GetProperty("revision").GetString());
        Assert.Equal(WgpuDx12CompilerArtifact.HeadersRevision, native.RootElement.GetProperty("webGpuHeadersRevision").GetString());
        Assert.Equal(WgpuDx12CompilerArtifact.WgpuRevision, compiler.RootElement.GetProperty("wgpuRevision").GetString());
        Assert.Equal(WgpuDx12CompilerArtifact.LockSha256, compiler.RootElement.GetProperty("lockSha256").GetString());
        using var shader = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "eng", "wgpu-hlsl", "build-inputs.json")));
        Assert.Equal(WgpuDx12CompilerArtifact.WgpuRevision, shader.RootElement.GetProperty("wgpuRevision").GetString());
        Assert.Equal(WgpuDx12CompilerArtifact.NagaVersion, shader.RootElement.GetProperty("nagaVersion").GetString());
        Assert.Equal(WgpuDx12CompilerArtifact.ShaderPatchSha256, shader.RootElement.GetProperty("patchSha256").GetString());
        JsonElement writer = Assert.Single(shader.RootElement.GetProperty("modifiedFiles").EnumerateArray());
        Assert.Equal("naga/src/back/hlsl/writer.rs", writer.GetProperty("path").GetString());
        Assert.Equal(WgpuDx12CompilerArtifact.OriginalWriterSha256, writer.GetProperty("originalSha256").GetString());
        Assert.Equal(WgpuDx12CompilerArtifact.PatchedWriterSha256, writer.GetProperty("patchedSha256").GetString());
    }

    [Theory]
    [InlineData(null, WgpuDx12ShaderCompiler.Automatic)]
    [InlineData("", WgpuDx12ShaderCompiler.Automatic)]
    [InlineData(" auto ", WgpuDx12ShaderCompiler.Automatic)]
    [InlineData("automatic", WgpuDx12ShaderCompiler.Automatic)]
    [InlineData(" FXC ", WgpuDx12ShaderCompiler.Fxc)]
    [InlineData("dxc", WgpuDx12ShaderCompiler.Dxc)]
    public void ParsesExplicitStartupPolicy(string? value, WgpuDx12ShaderCompiler expected) =>
        Assert.Equal(expected, WgpuDx12CompilerOptions.Parse(value).Preference);

    [Theory]
    [InlineData("cpu")]
    [InlineData("dxil")]
    [InlineData("fastest")]
    public void UnknownModesCannotSilentlyChangeCompiler(string value) =>
        Assert.Throws<ArgumentException>(() => WgpuDx12CompilerOptions.Parse(value));

    [Theory]
    [InlineData(WgpuDx12ShaderCompiler.Fxc)]
    [InlineData(WgpuDx12ShaderCompiler.Dxc)]
    public void ExplicitCompilerCannotChangeExternalOrNonWindowsDevices(WgpuDx12ShaderCompiler compiler) =>
        Assert.Throws<NotSupportedException>(() => new WgpuDx12CompilerOptions(compiler).ValidateOwnership(false));

    [Fact]
    public void AutomaticRemainsAvailableToEveryProvider() => WgpuDx12CompilerOptions.Default.ValidateOwnership(false);

    [Fact]
    public void InvalidEnumAndDirectoryFailBeforeLoadingNativeCode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WgpuDx12CompilerOptions((WgpuDx12ShaderCompiler)99).Validate());
        Assert.Throws<ArgumentException>(() => WgpuDx12CompilerOptions.Parse("fxc", Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() => WgpuDx12CompilerOptions.Parse("dxc", "relative"));
        Assert.Throws<ArgumentException>(() => WgpuDx12CompilerOptions.Parse("dxc", Path.GetTempPath() + '\0'));
    }

    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X64)]
    public void MatchingArtifactAndCompilerArchitectureAdmitExplicitPaths(Architecture architecture)
    {
        using var fixture = new ArtifactFixture(architecture);
        var paths = WgpuDx12CompilerArtifact.Validate(fixture.Library, null, architecture);
        Assert.Equal(Path.Combine(fixture.Directory, "dxcompiler.dll"), paths.Compiler);
        Assert.Equal(Path.Combine(fixture.Directory, "dxil.dll"), paths.Validator);
        Assert.Equal(0, paths.CompilerUtf8[^1]);
        Assert.Equal(0, paths.ValidatorUtf8[^1]);
    }

    [Theory]
    [InlineData("schemaVersion", "bad-schema")]
    [InlineData("rid", "win-x64")]
    [InlineData("backendAbi", "other-abi")]
    [InlineData("nativeRevision", "other-native")]
    [InlineData("headersRevision", "other-headers")]
    [InlineData("wgpuRevision", "other-wgpu")]
    [InlineData("compilerFeature", "fxc-only")]
    [InlineData("lockSha256", "other-lock")]
    [InlineData("librarySha256", "other-library")]
    public void MismatchedArtifactIsRejected(string field, string value)
    {
        using var fixture = new ArtifactFixture(Architecture.Arm64);
        fixture.Manifest[field] = field == "schemaVersion" ? 1 : value;
        fixture.WriteManifest();
        Assert.Throws<NotSupportedException>(() => WgpuDx12CompilerArtifact.Validate(fixture.Library, null, Architecture.Arm64));
    }

    [Theory]
    [InlineData("wgpuRevision")]
    [InlineData("nagaVersion")]
    [InlineData("patchSha256")]
    [InlineData("path")]
    [InlineData("originalSha256")]
    [InlineData("patchedSha256")]
    public void DifferentShaderSourceCannotAdmitDxc(string field)
    {
        using var fixture = new ArtifactFixture(Architecture.Arm64);
        var fields = field is "path" or "originalSha256" or "patchedSha256"
            ? fixture.WriterInputs : fixture.ShaderInputs;
        fields[field] = "different";
        fixture.WriteManifest();
        Assert.Throws<NotSupportedException>(() => WgpuDx12CompilerArtifact.Validate(fixture.Library, null, Architecture.Arm64));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("array")]
    [InlineData("empty-files")]
    [InlineData("duplicate-files")]
    [InlineData("invalid-file")]
    public void IncompleteShaderProvenanceCannotAdmitDxc(string kind)
    {
        using var fixture = new ArtifactFixture(Architecture.X64);
        switch (kind)
        {
            case "missing": fixture.Manifest.Remove("shaderInputs"); break;
            case "null": fixture.Manifest["shaderInputs"] = null!; break;
            case "array": fixture.Manifest["shaderInputs"] = Array.Empty<object>(); break;
            case "empty-files": fixture.ShaderInputs["modifiedFiles"] = Array.Empty<object>(); break;
            case "duplicate-files": fixture.ShaderInputs["modifiedFiles"] = new[] { fixture.WriterInputs, fixture.WriterInputs }; break;
            case "invalid-file": fixture.ShaderInputs["modifiedFiles"] = new object[] { 1 }; break;
        }
        fixture.WriteManifest();
        Assert.Throws<NotSupportedException>(() => WgpuDx12CompilerArtifact.Validate(fixture.Library, null, Architecture.X64));
    }

    [Fact]
    public void StockNativeLibraryWithoutFeatureManifestCannotAdmitDxc()
    {
        using var fixture = new ArtifactFixture(Architecture.Arm64);
        File.Delete(fixture.ManifestPath);
        Assert.Throws<NotSupportedException>(() => WgpuDx12CompilerArtifact.Validate(fixture.Library, null, Architecture.Arm64));
    }

    [Fact]
    public void ReplacedNativeLibraryIsRejectedEvenWithMatchingMetadata()
    {
        using var fixture = new ArtifactFixture(Architecture.Arm64);
        File.AppendAllText(fixture.Library, "changed");
        Assert.Throws<NotSupportedException>(() => WgpuDx12CompilerArtifact.Validate(fixture.Library, null, Architecture.Arm64));
    }

    [Theory]
    [InlineData("dxcompiler.dll")]
    [InlineData("dxil.dll")]
    public void WrongArchitectureOrMissingCompilerCannotFallBack(string file)
    {
        using var fixture = new ArtifactFixture(Architecture.Arm64);
        string path = Path.Combine(fixture.Directory, file);
        ArtifactFixture.WritePe(path, Architecture.X64);
        Assert.Throws<BadImageFormatException>(() => WgpuDx12CompilerArtifact.Validate(fixture.Library, null, Architecture.Arm64));
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => WgpuDx12CompilerArtifact.Validate(fixture.Library, null, Architecture.Arm64));
    }

    [Fact]
    public void ExplicitCompilerDirectoryIsUsedWithoutChangingNativeArtifact()
    {
        using var fixture = new ArtifactFixture(Architecture.Arm64);
        string compilerDirectory = Path.Combine(fixture.Directory, "compiler");
        System.IO.Directory.CreateDirectory(compilerDirectory);
        foreach (string name in new[] { "dxcompiler.dll", "dxil.dll" })
            File.Move(Path.Combine(fixture.Directory, name), Path.Combine(compilerDirectory, name));
        var paths = WgpuDx12CompilerArtifact.Validate(fixture.Library, compilerDirectory, Architecture.Arm64);
        Assert.Equal(Path.Combine(compilerDirectory, "dxcompiler.dll"), paths.Compiler);
    }

    // Synthetic headers exercise metadata validation only; these files are never loaded.
    private sealed class ArtifactFixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "progpu-compiler-" + Guid.NewGuid().ToString("N"));
        internal string Library => Path.Combine(Directory, "wgpu_native.dll");
        internal string ManifestPath => Path.Combine(Directory, "wgpu-native-build.json");
        internal Dictionary<string, object> Manifest { get; }
        internal Dictionary<string, object> WriterInputs { get; } = new()
        {
            ["path"] = "naga/src/back/hlsl/writer.rs",
            ["originalSha256"] = WgpuDx12CompilerArtifact.OriginalWriterSha256,
            ["patchedSha256"] = WgpuDx12CompilerArtifact.PatchedWriterSha256
        };
        internal Dictionary<string, object> ShaderInputs { get; }

        internal ArtifactFixture(Architecture architecture)
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (string name in new[] { "wgpu_native.dll", "dxcompiler.dll", "dxil.dll" })
                WritePe(Path.Combine(Directory, name), architecture);
            ShaderInputs = new()
            {
                ["wgpuRevision"] = WgpuDx12CompilerArtifact.WgpuRevision,
                ["nagaVersion"] = WgpuDx12CompilerArtifact.NagaVersion,
                ["patchSha256"] = WgpuDx12CompilerArtifact.ShaderPatchSha256,
                ["modifiedFiles"] = new[] { WriterInputs }
            };
            Manifest = new()
            {
                ["schemaVersion"] = 2,
                ["rid"] = architecture == Architecture.Arm64 ? "win-arm64" : "win-x64",
                ["backendAbi"] = "PROGPU_NATIVE_BACKEND_ABI_WGPU_NATIVE_2024_05",
                ["nativeRevision"] = WgpuDx12CompilerArtifact.NativeRevision,
                ["headersRevision"] = WgpuDx12CompilerArtifact.HeadersRevision,
                ["wgpuRevision"] = WgpuDx12CompilerArtifact.WgpuRevision,
                ["compilerFeature"] = "dxc_shader_compiler",
                ["lockSha256"] = WgpuDx12CompilerArtifact.LockSha256,
                ["shaderInputs"] = ShaderInputs,
                ["librarySha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Library)))
            };
            WriteManifest();
        }

        internal void WriteManifest() => File.WriteAllText(ManifestPath, JsonSerializer.Serialize(Manifest));
        internal static void WritePe(string path, Architecture architecture)
        {
            byte[] bytes = new byte[128];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x5A4D);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3C), 64);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 0x4550);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), architecture == Architecture.Arm64 ? (ushort)0xAA64 : (ushort)0x8664);
            File.WriteAllBytes(path, bytes);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
