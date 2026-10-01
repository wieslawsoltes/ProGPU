using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Native;

// Diagnostic-only original caller input, not a reconstruction of prepared GPU
// instances/atlas tiles. No provider objects or process addresses are persisted.
internal static class NativeHintedPaintFailureReceipt
{
    internal const string OutputVariable = "PROGPU_HINTED_PAINT_FAILURE_OUTPUT";

    internal static void TryCapture(WgpuContext context, Exception failure, string fontSha256,
        float dpi, int frame, NativeGlyphOutline[] outlines, NativePathSegment[] segments,
        NativePositionedGlyph[] glyphs, NativePositionedGlyph[] referenceGlyphs,
        byte[] writerBytes, byte[] scene, NativeSceneGlyphPaint paint, byte[] texel,
        byte[] actual, byte[] expected)
    {
        try
        {
            string? root = Environment.GetEnvironmentVariable(OutputVariable);
            if (string.IsNullOrEmpty(root)) return;
            string directory = Write(root, failure.Message, fontSha256, dpi, frame,
                outlines, segments, glyphs, referenceGlyphs, writerBytes, scene, paint,
                texel, actual, expected, context.BackendKind.ToString(),
                context.AdapterBackendType.ToString(), context.AdapterName,
                context.SelectedDx12ShaderCompiler?.ToString() ?? "unknown", CaptureLoadedLibraries());
            Console.Error.WriteLine($"Authentic hinted paint failure inputs: {directory}");
        }
        catch (Exception captureFailure)
        {
            // Receipt failure must not replace the original pixel assertion.
            try { Console.Error.WriteLine($"Hinted paint receipt failed: {captureFailure}"); }
            catch { }
        }
    }

    internal static string Write(string root, string failure, string fontSha256,
        float dpi, int frame, ReadOnlySpan<NativeGlyphOutline> outlines,
        ReadOnlySpan<NativePathSegment> segments, ReadOnlySpan<NativePositionedGlyph> glyphs,
        ReadOnlySpan<NativePositionedGlyph> referenceGlyphs, ReadOnlySpan<byte> writerBytes,
        ReadOnlySpan<byte> scene, NativeSceneGlyphPaint paint, ReadOnlySpan<byte> texel,
        ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected,
        string provider, string backend, string adapter, string compiler,
        ReadOnlySpan<LoadedLibraryIdentity> verifiedLibraries = default)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Receipt root must be absolute.", nameof(root));
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Receipt records require little-endian storage.");
        if (actual.Length != 96 * 96 * 4 || expected.Length != actual.Length || texel.Length != 4)
            throw new ArgumentException("Receipt requires the original 96x96 RGBA outputs and 1x1 texel.");
        string directory = Path.Combine(root, "bounded-texture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var files = new List<(string Name, int Length, string Sha256)>();
        Save(directory, files, "outlines.bin", MemoryMarshal.AsBytes(outlines));
        Save(directory, files, "segments.bin", MemoryMarshal.AsBytes(segments));
        Save(directory, files, "glyphs.bin", MemoryMarshal.AsBytes(glyphs));
        Save(directory, files, "reference-glyphs.bin", MemoryMarshal.AsBytes(referenceGlyphs));
        Save(directory, files, "original-writer.bin", writerBytes);
        Save(directory, files, "scene.bin", scene);
        Save(directory, files, "paint.bin", MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in paint, 1)));
        Save(directory, files, "source-texture.rgba8", texel);
        Save(directory, files, "actual.rgba8", actual);
        Save(directory, files, "expected.rgba8", expected);
        Save(directory, files, "text-production.wgsl", Encoding.UTF8.GetBytes(Shaders.TextShader));
        Save(directory, files, "paint-production.wgsl", Encoding.UTF8.GetBytes(Shaders.HintedGlyphPaintShader));

        // Written last: a complete receipt has a manifest. Utf8JsonWriter keeps
        // this fixture compatible with its existing NativeAOT package controls.
        string pending = Path.Combine(directory, "receipt.pending");
        using (var output = new FileStream(pending, FileMode.CreateNew))
        using (var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("Schema", 1);
            json.WriteString("Scope", "Authentic failed fixture caller inputs and completed outputs; not captured prepared atlas/GPU instances, package qualification or Display admission.");
            json.WriteString("Failure", failure);
            json.WriteString("SourceCommit", Environment.GetEnvironmentVariable("GITHUB_SHA"));
            json.WriteString("RunId", Environment.GetEnvironmentVariable("GITHUB_RUN_ID"));
            json.WriteString("RunAttempt", Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"));
            json.WriteString("Job", Environment.GetEnvironmentVariable("GITHUB_JOB"));
            json.WriteString("OS", RuntimeInformation.OSDescription);
            json.WriteString("Architecture", RuntimeInformation.ProcessArchitecture.ToString());
            json.WriteString("Runtime", RuntimeInformation.FrameworkDescription);
            json.WriteNumber("NativeWordBytes", IntPtr.Size);
            json.WriteString("ByteOrder", "little-endian; original raw typed records, offsets are counts not addresses");
            json.WriteString("Provider", provider); json.WriteString("Backend", backend);
            json.WriteString("Adapter", adapter); json.WriteString("Compiler", compiler);
            json.WriteString("FontSha256", fontSha256);
            json.WriteNumber("Dpi", dpi); json.WriteNumber("Frame", frame);
            json.WriteNumber("TargetWidth", 96); json.WriteNumber("TargetHeight", 96);
            json.WriteString("TargetFormat", "Rgba8Unorm");
            json.WriteString("TargetAlphaMetadata", "Premultiplied");
            json.WriteString("SourceTextureFormat", "Rgba8Unorm, Straight, 1x1");
            Vector(json, "ClearColor", Vector4.Zero);
            json.WriteStartObject("Paint");
            json.WriteNumber("Kind", paint.Kind); json.WriteNumber("BrushIndex", paint.BrushIndex);
            json.WriteNumber("Flags", paint.Flags);
            Vector(json, "SourceOffsetOpacity", paint.SourceOffsetOpacity); Vector(json, "UVBounds", paint.UVBounds);
            Vector(json, "TextureQuad01", paint.TextureQuad01); Vector(json, "TextureQuad23", paint.TextureQuad23);
            Vector(json, "Sampling", paint.Sampling); json.WriteEndObject();
            json.WriteString("Completion", "Both original native submissions completed and both existing texture copy/map readbacks returned before capture.");
            json.WriteNumber("OutlineRecordBytes", Marshal.SizeOf<NativeGlyphOutline>());
            json.WriteNumber("SegmentRecordBytes", Marshal.SizeOf<NativePathSegment>());
            json.WriteNumber("GlyphRecordBytes", Marshal.SizeOf<NativePositionedGlyph>());
            json.WriteNumber("PaintRecordBytes", Marshal.SizeOf<NativeSceneGlyphPaint>());
            json.WriteStartArray("Outlines");
            foreach (ref readonly var outline in outlines)
            {
                json.WriteStartObject();
                json.WriteNumber("SegmentOffset", (ulong)outline.SegmentOffset);
                json.WriteNumber("SegmentCount", (ulong)outline.SegmentCount);
                Vector(json, "Minimum", outline.Minimum); Vector(json, "Maximum", outline.Maximum);
                json.WriteNumber("RasterScale", outline.RasterScale); json.WriteNumber("SubpixelX", outline.SubpixelX);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("Glyphs");
            foreach (ref readonly var glyph in glyphs)
            {
                json.WriteStartObject(); json.WriteNumber("OutlineIndex", glyph.OutlineIndex);
                Vector(json, "Position", glyph.Position); Vector(json, "BasisX", glyph.BasisX); Vector(json, "BasisY", glyph.BasisY);
                Vector(json, "Color", glyph.Color);
                json.WriteNumber("AtlasToLogicalScale", glyph.AtlasToLogicalScale);
                json.WriteNumber("BoldOffset", glyph.BoldOffset); json.WriteNumber("ItalicSkew", glyph.ItalicSkew);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("Files");
            foreach (var file in files)
            {
                json.WriteStartObject(); json.WriteString("Name", file.Name);
                json.WriteNumber("Bytes", file.Length); json.WriteString("Sha256", file.Sha256); json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteBoolean("RuntimeIdentityVerified", !verifiedLibraries.IsEmpty);
            json.WriteStartArray("LoadedLibraries");
            foreach (ref readonly var library in verifiedLibraries)
            {
                json.WriteStartObject(); json.WriteString("Path", library.Path);
                json.WriteString("Sha256", library.Sha256); json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject(); json.Flush();
        }
        File.Move(pending, Path.Combine(directory, "receipt.json"), overwrite: false);
        return directory;
    }

    internal readonly record struct LoadedLibraryIdentity(string Path, string Sha256);

    private static LoadedLibraryIdentity[] CaptureLoadedLibraries()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Fresh native receipt identity verification currently requires Windows.");
        int nativeLibraries = 0, webGpuLibraries = 0;
        var identities = new List<LoadedLibraryIdentity>();
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            string name = module.ModuleName;
            if (!name.Contains("progpu_native", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("wgpu_native", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("d3d", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("dxcompiler", StringComparison.OrdinalIgnoreCase)) continue;
            using var library = File.OpenRead(module.FileName);
            string hash = Convert.ToHexString(SHA256.HashData(library));
            if (name.Equals("progpu_native.dll", StringComparison.OrdinalIgnoreCase))
            {
                VerifyLibraryIdentity(module.FileName, hash,
                    Environment.GetEnvironmentVariable("PROGPU_HINTED_PAINT_NATIVE_PATH"),
                    Environment.GetEnvironmentVariable("PROGPU_HINTED_PAINT_NATIVE_SHA256"), requirePath: true);
                nativeLibraries++;
            }
            if (name.Equals("wgpu_native.dll", StringComparison.OrdinalIgnoreCase))
            {
                // WgpuContext may select the consumer's pinned NuGet copy.
                // Its bytes, not PATH intent, must match.
                VerifyLibraryIdentity(module.FileName, hash, null,
                    Environment.GetEnvironmentVariable("PROGPU_HINTED_PAINT_WGPU_SHA256"), requirePath: false);
                webGpuLibraries++;
            }
            identities.Add(new(module.FileName, hash));
        }
        if (nativeLibraries != 1 || webGpuLibraries != 1)
            throw new InvalidOperationException("Receipt requires exactly the fresh stock native renderer and pinned WebGPU library.");
        return identities.ToArray();
    }

    internal static void VerifyLibraryIdentity(string loadedPath, string actualHash,
        string? expectedPath, string? expectedHash, bool requirePath)
    {
        if (expectedHash is null || expectedHash.Length != 64 ||
            !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) ||
            (requirePath && (expectedPath is null || !Path.IsPathFullyQualified(expectedPath) ||
                !Path.GetFullPath(loadedPath).Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Loaded diagnostic library does not match the fresh build identity: " + loadedPath);
    }

    private static void Save(string directory, List<(string Name, int Length, string Sha256)> files,
        string name, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Unexpected diagnostic payload size.");
        using var output = new FileStream(Path.Combine(directory, name), FileMode.CreateNew);
        output.Write(bytes);
        files.Add((name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
    }

    private static void Vector(Utf8JsonWriter json, string name, Vector2 value)
    {
        json.WriteStartArray(name); json.WriteNumberValue(value.X); json.WriteNumberValue(value.Y); json.WriteEndArray();
    }

    private static void Vector(Utf8JsonWriter json, string name, Vector4 value)
    {
        json.WriteStartArray(name); json.WriteNumberValue(value.X); json.WriteNumberValue(value.Y);
        json.WriteNumberValue(value.Z); json.WriteNumberValue(value.W); json.WriteEndArray();
    }
}
