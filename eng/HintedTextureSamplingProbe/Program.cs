using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace HintedTextureSamplingProbe;

internal static unsafe class Program
{
    private const uint AtlasSize = 1024;
    private const int AtlasTileX = 2;
    private const int AtlasTileY = 2;
    private const float Dpi = 2;
    private const float Opacity = .46875f;
    private static readonly ConcurrentQueue<string> Errors = new();
    private static int _scopeDone, _queueDone, _queueStatus;
    private static int _submissions, _completed;

    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        // A hung initialization/driver/teardown must not consume a CI job forever.
        using var deadline = new Timer(_ => Environment.FailFast("Synthetic hinted sampling probe exceeded 180 seconds."),
            null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
        try
        {
            if (args.Length == 1 && args[0] == "--verify-source-only")
            {
                Console.WriteLine($"PASS {ShaderSourceControls.Run()} deterministic source, instrumentation, atlas-translation, native-frame and independent binary-oracle controls; no GPU initialization.");
                return 0;
            }
            string? output = null;
            bool fallback = false, nativeFrame = false, canonicalFrame = false, binaryOracle = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
                else if (args[i] == "--fallback") fallback = true;
                else if (args[i] == "--native-frame") nativeFrame = true;
                else if (args[i] == "--canonical-frame") canonicalFrame = true;
                else if (args[i] == "--binary-oracle") binaryOracle = true;
                else throw new ArgumentException("Usage: HintedTextureSamplingProbe --output <new directory> [--fallback] [--native-frame [--canonical-frame [--binary-oracle]]]");
            }
            if (output is null) throw new ArgumentException("--output is required.");
            if (canonicalFrame && !nativeFrame) throw new ArgumentException("--canonical-frame requires the proven --native-frame control.");
            if (binaryOracle && !canonicalFrame) throw new ArgumentException("--binary-oracle requires --native-frame --canonical-frame.");
            if (fallback && !OperatingSystem.IsWindows()) throw new ArgumentException("--fallback explicitly requires Windows D3D12.");
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Probe targets Windows/D3D12 or macOS/Metal.");
            output = Path.GetFullPath(output);
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                throw new IOException("Refusing to overwrite an existing diagnostic artifact directory.");
            Directory.CreateDirectory(output);
            foreach (uint size in new uint[] { 96, 128 })
            {
                string caseOutput = Path.Combine(output, "target-" + size);
                Directory.CreateDirectory(caseOutput);
                Run(caseOutput, fallback, size, nativeFrame, canonicalFrame, binaryOracle);
                ThrowErrors(); // Includes errors queued during all resource/context retirement.
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Run(string output, bool fallback, uint size, bool nativeFrame, bool canonicalFrame, bool binaryOracle)
    {
        int initialSubmissions = _submissions, initialCompletions = _completed;
        string text = ShaderDiagnostics.VerifySource(false), paint = ShaderDiagnostics.VerifySource(true);
        string textProfile = ShaderDiagnostics.SourceProfile(text, false), paintProfile = ShaderDiagnostics.SourceProfile(paint, true);
        if (canonicalFrame && (!ShaderDiagnostics.HasCanonicalFrame(textProfile) || !ShaderDiagnostics.HasCanonicalFrame(paintProfile)))
            throw new InvalidOperationException("Canonical-frame opt-in requires both exact reviewed candidate modules.");
        string textDiagnostic = ShaderDiagnostics.Instrument(text, false);
        string paintDiagnostic = ShaderDiagnostics.Instrument(paint, true);
        var sources = new Dictionary<string, string>
        {
            ["text-production"] = text, ["paint-production"] = paint,
            ["text-diagnostic"] = textDiagnostic, ["paint-diagnostic"] = paintDiagnostic,
            ["text-sample-diagnostic"] = ShaderDiagnostics.Instrument(text, false, true),
            ["paint-sample-diagnostic"] = ShaderDiagnostics.Instrument(paint, true, true)
        };
        foreach (var source in sources) File.WriteAllText(Path.Combine(output, source.Key + ".wgsl"), source.Value);

        WgpuContext.OnWebGpuError += OnError;
        WgpuContext.OnWebGpuDeviceLost += OnLost;
        try
        {
            using var context = new WgpuContext
            {
                ForceFallbackAdapter = fallback,
                NativeBackendOptions = new(OperatingSystem.IsWindows() ? WgpuNativeBackend.D3D12 : WgpuNativeBackend.Metal),
                Dx12CompilerOptions = WgpuDx12CompilerOptions.Default
            };
            context.Initialize(null);
            ThrowErrors();
            Console.WriteLine($"Actual provider={context.BackendKind}, backend={context.AdapterBackendType}, adapter={context.AdapterName}, compiler={context.SelectedDx12ShaderCompiler}, requestedFallback={fallback}");
            if (context.BackendKind != WgpuBackendKind.SilkNative) throw new InvalidOperationException("Unexpected provider.");
            BackendType expectedBackend = OperatingSystem.IsWindows() ? BackendType.D3D12 : BackendType.Metal;
            if (context.AdapterBackendType != expectedBackend) throw new InvalidOperationException("Requested backend was not selected; no fallback is admitted.");
            string providerPath = ProviderLibrary.Find(context.Wgpu.Context.GetProcAddress("wgpuDevicePoll"));
            var libraries = LoadedLibraries().Append(new LibraryIdentity(providerPath,
                ShaderDiagnostics.Hash(File.ReadAllBytes(providerPath)))).Distinct().ToArray();

            using var cache = new RenderPipelineCache(context);
            using var uniforms = Buffer(context, UniformBytes(size, canonicalFrame), BufferUsage.Uniform);
            using var styles = Buffer(context, new byte[32], BufferUsage.Storage);
            byte[] brushBytes = new byte[256];
            Span<float> brush = MemoryMarshal.Cast<byte, float>(brushBytes.AsSpan());
            brush[1] = 1;
            brush[16] = 64 / 255f; brush[17] = 192 / 255f; brush[18] = 128 / 255f; brush[19] = Opacity;
            using var brushes = Buffer(context, brushBytes, BufferUsage.Storage);
            using var stops = Buffer(context, new byte[32], BufferUsage.Storage);
            using var paints = Buffer(context, PaintBytes(false, size), BufferUsage.Storage);
            byte[] instanceBytes = nativeFrame ? NativeFrameControl.Instances() : Instances();
            using var instances = Buffer(context, instanceBytes, BufferUsage.Vertex);

            using var atlas = new GpuTexture(context, AtlasSize, AtlasSize, TextureFormat.R8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
            byte[] atlasBytes = binaryOracle ? CanonicalCoverageOracle.Atlas()
                : nativeFrame ? NativeFrameControl.Atlas() : Atlas();
            atlas.WritePixels<byte>(atlasBytes);
            byte[] textureBytes = [64, 192, 128, 255];
            using var texture = new GpuTexture(context, 1, 1, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
            texture.WritePixels<byte>(textureBytes);
            // The unused color-atlas binding is real and identical for all paths.
            using var colorAtlas = new GpuTexture(context, AtlasSize, AtlasSize, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
            colorAtlas.WritePixels<byte>(new byte[AtlasSize * AtlasSize * 4]);
            using var target = new GpuTexture(context, size, size, TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc);
            using var diagnosticTarget = new GpuTexture(context, size, size, TextureFormat.Rgba32float, TextureUsage.RenderAttachment | TextureUsage.CopySrc);
            using var readback = new GpuTextureReadbackBuffer(context);
            using var diagnosticReadback = new GpuTextureReadbackBuffer(context);
            SamplerDescriptor samplerDescription = new()
            {
                AddressModeU = AddressMode.ClampToEdge, AddressModeV = AddressMode.ClampToEdge, AddressModeW = AddressMode.ClampToEdge,
                MinFilter = FilterMode.Linear, MagFilter = FilterMode.Linear, MipmapFilter = MipmapFilterMode.Nearest,
                LodMinClamp = 0, LodMaxClamp = 0, MaxAnisotropy = 1
            };
            Sampler* sampler = context.Api.DeviceCreateSampler(context.Device, &samplerDescription);
            if (sampler == null) throw new InvalidOperationException("Sampler creation failed.");
            var pipelines = new List<ProbePipeline>();
            try
            {
                foreach (var source in sources)
                {
                    bool isPaint = source.Key.StartsWith("paint", StringComparison.Ordinal);
                    bool diagnostic = source.Key.EndsWith("diagnostic", StringComparison.Ordinal);
                    BeginScope(context);
                    var pipeline = new ProbePipeline(context, cache, source.Key, source.Value, isPaint, diagnostic,
                        uniforms, styles, brushes, stops, paints, atlas, colorAtlas, texture, sampler);
                    pipelines.Add(pipeline);
                    EndScope(context, "pipeline " + source.Key);
                }

                var frames = new Dictionary<string, byte[]>();
                var missingInk = new List<string>();
                string[] paths = ["text", "material", "bounded-texture"];
                foreach (string path in paths)
                {
                    bool isPaint = path != "text";
                    byte[] paintBytes = PaintBytes(path == "bounded-texture", size);
                    paints.WriteBytes(paintBytes);
                    uint vertexCount = DrawVertexCount(isPaint ? paintProfile : textProfile, isPaint, paintBytes);
                    ProbePipeline pipeline = pipelines[isPaint ? 1 : 0];
                    for (uint count = 1; count <= 2; count++)
                    for (int frame = 0; frame < 2; frame++)
                    {
                        string name = path + (count == 1 ? "-single" : "-overlap") + (frame == 0 ? "-cold" : "-warm");
                        byte[] pixels = Draw(context, pipeline, instances, target, readback, 0, count, vertexCount, false);
                        if (!pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0))
                            missingInk.Add(name);
                        frames.Add(name, pixels);
                        File.WriteAllBytes(Path.Combine(output, name + ".rgba8"), pixels);
                    }
                    for (int sample = 0; sample < 2; sample++)
                    for (uint occurrence = 0; occurrence < 2; occurrence++)
                    {
                        ProbePipeline diagnosticPipeline = pipelines[(isPaint ? 3 : 2) + sample * 2];
                        string name = path + (sample == 0 ? "-caller" : "-sample") + "-glyph-" + occurrence;
                        byte[] pixels = Draw(context, diagnosticPipeline, instances, diagnosticTarget, diagnosticReadback, occurrence, 1, vertexCount, true);
                        foreach (float value in MemoryMarshal.Cast<byte, float>(pixels))
                            if (!float.IsFinite(value)) throw new InvalidOperationException("Nonfinite diagnostic output: " + name);
                        frames.Add(name, pixels);
                        File.WriteAllBytes(Path.Combine(output, name + ".rgba32f"), pixels);
                    }
                }
                var comparisons = new List<object>();
                int canonicalPixelDifferences = 0;
                foreach (string path in paths)
                foreach (string placement in new[] { "-single", "-overlap" })
                {
                    string key = path + placement;
                    var stable = Difference(frames[key + "-cold"], frames[key + "-warm"], false, size);
                    if (stable.DifferentComponents != 0) throw new InvalidOperationException("Cold/warm output changed: " + path);
                    comparisons.Add(new { Reference = key + "-cold", Subject = key + "-warm", Difference = stable });
                }
                foreach (string path in paths.Skip(1))
                {
                    foreach (string placement in new[] { "-single", "-overlap" })
                    {
                        string suffix = placement + "-cold";
                        var difference = Difference(frames["text" + suffix], frames[path + suffix], false, size);
                        canonicalPixelDifferences += difference.DifferentComponents;
                        Console.WriteLine($"{size}px RGBA8 text vs {path}{placement}: differentBytes={difference.DifferentComponents}, maxDelta={difference.MaximumAbsoluteDifference}");
                        comparisons.Add(new { Reference = "text" + suffix, Subject = path + suffix, Difference = difference });
                    }
                    foreach (string diagnosticKind in new[] { "-caller", "-sample" })
                    for (int occurrence = 0; occurrence < 2; occurrence++)
                    {
                        string suffix = diagnosticKind + "-glyph-" + occurrence;
                        var floats = Difference(frames["text" + suffix], frames[path + suffix], true, size);
                        Console.WriteLine($"{size}px RGBA32F text vs {path}{suffix}: differentComponents={floats.DifferentComponents}, channels={string.Join(',', floats.PerChannel)}, maxDelta={floats.MaximumAbsoluteDifference}");
                        comparisons.Add(new { Reference = "text" + suffix, Subject = path + suffix, Difference = floats });
                    }
                }
                var oracle = new List<CanonicalCoverageOracle.Result>();
                if (binaryOracle)
                    foreach (string path in paths)
                    foreach (string diagnosticKind in new[] { "-caller", "-sample" })
                    for (int occurrence = 0; occurrence < 2; occurrence++)
                    {
                        string capture = path + diagnosticKind + "-glyph-" + occurrence;
                        var result = CanonicalCoverageOracle.Check(capture, frames[capture], size, occurrence,
                            atlasBytes, normalized: diagnosticKind == "-sample", boundedTexture: path == "bounded-texture");
                        oracle.Add(result);
                        Console.WriteLine($"{size}px independent binary oracle {capture}: differences={result.DifferentComponents}, interior={result.InteriorPixels}, boundary={result.BoundaryPixels}, outside={result.OutsidePixels}");
                    }
                File.WriteAllBytes(Path.Combine(output, "atlas.r8"), atlasBytes);
                File.WriteAllBytes(Path.Combine(output, "instances.bin"), instanceBytes);
                File.WriteAllBytes(Path.Combine(output, "uniforms.bin"), UniformBytes(size, canonicalFrame));
                File.WriteAllBytes(Path.Combine(output, "material.bin"), brushBytes);
                File.WriteAllBytes(Path.Combine(output, "bounded-paint.bin"), PaintBytes(true, size));
                File.WriteAllBytes(Path.Combine(output, "texture.rgba8"), textureBytes);
                var assembly = typeof(WgpuContext).Assembly;
                var report = new
                {
                    Scope = nativeFrame
                        ? "Derived authentic native frame with controlled synthetic coverage, NOT original atlas coverage; not native font, renderer, package or Display qualification."
                        : "Synthetic shared-shader sampling diagnostic; not native font, renderer, package or Display qualification.",
                    FrameControl = nativeFrame ? "native-frame" : "synthetic-frame",
                    CanonicalCoverageGate = canonicalFrame ? -1 : 0,
                    CoverageInput = binaryOracle ? "binary-checkerboard-oracle" : "nonuniform-synthetic-regression",
                    NativeFrameReference = nativeFrame ? NativeFrameControl.Provenance() : null,
                    ReviewedShaderBaselineCommit = ShaderDiagnostics.BaselineCommit,
                    ShaderProfiles = new { Text = textProfile, HintedGlyphPaint = paintProfile },
                    DrawVertexCounts = paths.ToDictionary(path => path, path =>
                        DrawVertexCount(path == "text" ? textProfile : paintProfile, path != "text", PaintBytes(path == "bounded-texture", size))),
                    OriginalGate0Comparison = "Immutable native-frame run36884526788 at c0e9d839644f9e46664cd51ee66e637828fe63cc; compare exact receipts separately, not a claim of cross-provider parity.",
                    Provider = context.BackendKind.ToString(), Backend = context.AdapterBackendType.ToString(),
                    context.AdapterName, Compiler = context.SelectedDx12ShaderCompiler?.ToString(), RequestedFallback = fallback,
                    context.AdapterSelectionDiagnostics,
                    Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    BackendAssembly = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    BackendAssemblySha256 = ShaderDiagnostics.Hash(File.ReadAllBytes(assembly.Location)),
                    LoadedLibraries = libraries,
                    NativeProviderSymbol = "wgpuDevicePoll", NativeProviderSymbolOwner = providerPath,
                    ShaderSha256 = sources.ToDictionary(x => x.Key, x => ShaderDiagnostics.Hash(x.Value)),
                    CanonicalLfShaderSha256 = sources.ToDictionary(x => x.Key, x => ShaderDiagnostics.CanonicalHash(x.Value)),
                    Size = size, Dpi, Opacity, AtlasWidth = AtlasSize, AtlasHeight = AtlasSize, InstanceStride = 96,
                    AtlasTileOrigin = new[] { AtlasTileX, AtlasTileY },
                    AtlasTileExtent = nativeFrame ? new[] { NativeFrameControl.Width, NativeFrameControl.Height } : new[] { 23, 25 },
                    AtlasPlacementControl = nativeFrame
                        ? "Original native first-allocation origin(2,2), derived20x22 frame with4pixelpadding; interior coverage is controlled synthetic data, never captured original atlas data."
                        : "Original (17,11) at cc1fbdf33f9580a9804a898f528dfe3b32b65aeb, Windows run36877876254; only atlas placement changes to(2,2).",
                    AtlasSha256 = ShaderDiagnostics.Hash(atlasBytes), InstanceSha256 = ShaderDiagnostics.Hash(instanceBytes),
                    AtlasSampler = "linear min/mag, nearest mip, clamp-to-edge, LOD 0, anisotropy 1; shared by all paths",
                    TargetPolicy = "one shared RGBA8Unorm target with straight source-over; one shared unblended RGBA32Float diagnostic target",
                    CallerDiagnosticChannels = new[] { "atlas texel U (unclamped)", "atlas texel V (unclamped)", "sampled R8 coverage", "final source alpha" },
                    SampleDiagnosticChannels = new[] { "actual clamped normalized atlas U", "actual clamped normalized atlas V", "actual sampled R8 coverage", "actual gamma-adjusted helper coverage (before paint multiplication)" },
                    DrawSubmissions = _submissions - initialSubmissions, ActualQueueCompletions = _completed - initialCompletions,
                    Readback = "Each returned row buffer followed a successful map callback; 15-second map and queue deadlines, 180-second process watchdog.",
                    Comparisons = comparisons,
                    IndependentCoverageOracle = oracle,
                    OraclePrecision = "Independent double physical frames and texel-center bilinear math; binary0/255 plus dyadic weights give exact representable values. Exact selected-host coordinate/raw-coverage assertions, not a universal R8 sampler guarantee. Gamma/alpha remain observed original arithmetic and paired RGBA controls; no CPU pow tolerance.",
                    MissingInk = missingInk,
                    Artifacts = frames.ToDictionary(x => x.Key, x => ShaderDiagnostics.Hash(x.Value))
                };
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                ThrowErrors();
                if (missingInk.Count != 0) throw new InvalidOperationException("No ink in: " + string.Join(", ", missingInk));
                if (canonicalFrame && canonicalPixelDifferences != 0)
                    throw new InvalidOperationException("Canonical-frame original full-RGBA differential failed; see exact captures.");
                if (oracle.Any(result => result.DifferentComponents != 0))
                    throw new InvalidOperationException("Independent binary coordinate/coverage oracle failed; see exact captures.");
                Console.WriteLine($"Completed {_completed} actual draw queue callbacks; differences reported without tolerance or parity admission. Artifacts: {output}");
            }
            finally
            {
                foreach (var pipeline in pipelines) pipeline.Dispose();
                context.QueueSamplerDisposal((nint)sampler);
            }
        }
        finally
        {
            WgpuContext.OnWebGpuError -= OnError;
            WgpuContext.OnWebGpuDeviceLost -= OnLost;
        }
    }

    private static GpuBuffer Buffer(WgpuContext context, byte[] bytes, BufferUsage usage)
    {
        var buffer = new GpuBuffer(context, checked((uint)bytes.Length), usage | BufferUsage.CopyDst);
        try { buffer.WriteBytes(bytes); return buffer; }
        catch { buffer.Dispose(); throw; }
    }

    internal static byte[] UniformBytes(uint size, bool canonicalFrame = false)
    {
        byte[] bytes = new byte[224];
        Span<float> values = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        values[0] = 2 / (size / Dpi); values[5] = -2 / (size / Dpi); values[10] = -1;
        values[12] = -1; values[13] = 1; values[15] = 1;
        for (int matrix = 1; matrix < 3; matrix++)
            for (int diagonal = 0; diagonal < 4; diagonal++) values[matrix * 16 + diagonal * 5] = 1;
        values[48] = size / Dpi; values[49] = size / Dpi; values[50] = Dpi;
        // Private native host certificate at unchanged uniform byte204. Exact
        // -1 is disjoint from existing positive texture scratch-pass tags.
        // Only the proven full-target native-frame probe opts into it.
        values[51] = canonicalFrame ? -1 : 0;
        return bytes;
    }

    internal static byte[] Instances(int atlasX = AtlasTileX, int atlasY = AtlasTileY)
    {
        byte[] bytes = new byte[192];
        for (int occurrence = 0; occurrence < 2; occurrence++)
        {
            Span<float> value = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(occurrence * 96, 96));
            // Original fixture phases and overlap delta; deliberately synthetic
            // bearing/size and coverage tile, NOT a reconstructed native glyph.
            value[0] = (8.125f + occurrence * .375f) / Dpi;
            value[1] = (8.375f + occurrence * .125f) / Dpi;
            value[2] = 1; value[5] = 1;
            value[6] = 2; value[7] = 3; value[8] = 23; value[9] = 25;
            value[10] = atlasX; value[11] = atlasY; value[12] = atlasX + 23; value[13] = atlasY + 25;
            value[14] = 64 / 255f; value[15] = 192 / 255f; value[16] = 128 / 255f; value[17] = Opacity;
            value[18] = 1; value[22] = -1;
        }
        return bytes;
    }

    internal static byte[] PaintBytes(bool texture, uint size)
    {
        byte[] bytes = new byte[96];
        Span<uint> integers = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
        integers[0] = texture ? 1u : 0u; integers[2] = texture ? 2u : 0u;
        Span<float> values = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        values[6] = Opacity; values[10] = 1; values[11] = 1;
        values[14] = size / Dpi; values[16] = size / Dpi; values[17] = size / Dpi; values[19] = size / Dpi;
        values[21] = .5f;
        return bytes;
    }

    internal static byte[] Atlas(int atlasX = AtlasTileX, int atlasY = AtlasTileY,
        int width = 23, int height = 25, int padding = 1)
    {
        byte[] bytes = new byte[AtlasSize * AtlasSize];
        for (int localY = padding; localY < height - padding; localY++)
            for (int localX = padding; localX < width - padding; localX++)
            {
                // Preserve the exact original coverage formula at its original
                // (17,11) coordinates; translate storage only, never the values.
                int originalX = 17 + localX, originalY = 11 + localY;
                bytes[(atlasY + localY) * AtlasSize + atlasX + localX] =
                    (byte)(1 + (originalX * 37 + originalY * 73 + originalX * originalY * 11) % 254);
            }
        return bytes;
    }

    internal static uint DrawVertexCount(string profile, bool paint, ReadOnlySpan<byte> paintBytes)
    {
        if (profile is not (ShaderDiagnostics.BaselineProfile or ShaderDiagnostics.CanonicalFrameProfile or ShaderDiagnostics.AffineCanonicalFrameProfile))
            throw new InvalidOperationException("Unknown draw source profile.");
        if (!paint) return 6;
        if (paintBytes.Length != 96) throw new ArgumentException("Expected the original96-byte paint record.", nameof(paintBytes));
        // Match both merged engine paths: each bounded affine glyph triangle
        // retains its original image quad. Positive-axis copies are collapsed
        // by the shader, not omitted by guessing from the probe's instances.
        // Historical profiles retain their original six-vertex contract.
        return profile == ShaderDiagnostics.AffineCanonicalFrameProfile &&
            BinaryPrimitives.ReadUInt32LittleEndian(paintBytes) == 1 &&
            (BinaryPrimitives.ReadUInt32LittleEndian(paintBytes[8..]) & 2) != 0 ? 12u : 6u;
    }

    private static byte[] Draw(WgpuContext context, ProbePipeline pipeline, GpuBuffer instances,
        GpuTexture target, GpuTextureReadbackBuffer readback, uint first, uint count, uint vertexCount, bool diagnostic)
    {
        BeginScope(context);
        var encoderDescription = new CommandEncoderDescriptor();
        var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescription);
        RenderPassColorAttachment attachment = new() { View = target.ViewPtr, LoadOp = LoadOp.Clear, StoreOp = StoreOp.Store, ClearValue = new Color(0, 0, 0, 0) };
        RenderPassDescriptor description = new() { ColorAttachmentCount = 1, ColorAttachments = &attachment };
        var pass = context.Api.CommandEncoderBeginRenderPass(encoder, &description);
        context.Api.RenderPassEncoderSetPipeline(pass, pipeline.Handle);
        for (uint group = 0; group < pipeline.Groups.Count; group++)
            context.Api.RenderPassEncoderSetBindGroup(pass, group, (BindGroup*)pipeline.Groups[(int)group], 0, null);
        context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, instances.BufferPtr, 0, instances.Size);
        context.Api.RenderPassEncoderDraw(pass, vertexCount, count, 0, first);
        context.Api.RenderPassEncoderEnd(pass);
        context.Api.RenderPassEncoderRelease(pass);
        var commandsDescription = new CommandBufferDescriptor();
        var commands = context.Api.CommandEncoderFinish(encoder, &commandsDescription);
        context.Submit(1, &commands);
        _submissions++;
        context.Api.CommandBufferRelease(commands);
        context.Api.CommandEncoderRelease(encoder);
        Volatile.Write(ref _queueDone, 0);
        context.Wgpu.QueueOnSubmittedWorkDone(context.Queue, new PfnQueueWorkDoneCallback(&QueueComplete), null);
        Wait(context, () => Volatile.Read(ref _queueDone) != 0, "actual queue completion");
        if ((QueueWorkDoneStatus)_queueStatus != QueueWorkDoneStatus.Success) throw new InvalidOperationException("Queue failed: " + (QueueWorkDoneStatus)_queueStatus);
        _completed++;
        EndScope(context, "draw " + pipeline.Name);
        uint pixelSize = diagnostic ? 16u : 4u;
        byte[] bytes = new byte[target.Width * target.Height * pixelSize];
        fixed (byte* destination = bytes)
            if (!readback.TryReadTextureRows(target, target.Width, target.Height, destination, target.Width * pixelSize, pixelSize, 15000))
                throw new InvalidOperationException($"Readback failed: {readback.LastMapStatus}, timedOut={readback.LastMapTimedOut}");
        ThrowErrors();
        return bytes;
    }

    private sealed record DifferenceResult(int DifferentComponents, int[] PerChannel, double MaximumAbsoluteDifference, object[] FirstDifferences);
    private static DifferenceResult Difference(byte[] reference, byte[] actual, bool floating, uint size)
    {
        if (reference.Length != actual.Length) throw new InvalidOperationException("Mismatched output dimensions.");
        int different = 0; int[] channels = new int[4]; double maximum = 0;
        var first = new List<object>();
        int elementSize = floating ? 4 : 1;
        for (int i = 0; i < reference.Length / elementSize; i++)
        {
            double expected = floating ? BitConverter.ToSingle(reference, i * 4) : reference[i];
            double observed = floating ? BitConverter.ToSingle(actual, i * 4) : actual[i];
            bool equal = reference.AsSpan(i * elementSize, elementSize).SequenceEqual(actual.AsSpan(i * elementSize, elementSize));
            if (equal) continue;
            different++; channels[i % 4]++; maximum = Math.Max(maximum, Math.Abs(expected - observed));
            if (first.Count < 24) first.Add(new { X = i / 4 % size, Y = i / 4 / size, Channel = i % 4, Expected = expected, Actual = observed,
                ExpectedBits = Convert.ToHexString(reference.AsSpan(i * elementSize, elementSize)), ActualBits = Convert.ToHexString(actual.AsSpan(i * elementSize, elementSize)) });
        }
        return new(different, channels, maximum, first.ToArray());
    }

    private sealed record LibraryIdentity(string Path, string Sha256);
    private static LibraryIdentity[] LoadedLibraries()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Where(module =>
            new[] { "wgpu", "d3d12", "d3dcompiler", "dxcompiler", "metal" }.Any(name => module.ModuleName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(module => new LibraryIdentity(module.FileName, ShaderDiagnostics.Hash(File.ReadAllBytes(module.FileName)))).ToArray();
    }

    private static void OnError(ErrorType type, string message) => Errors.Enqueue(type + ": " + message);
    private static void OnLost(DeviceLostReason reason, string message) => Errors.Enqueue("Device lost " + reason + ": " + message);
    private static void ThrowErrors() { if (!Errors.IsEmpty) throw new InvalidOperationException(string.Join("\n", Errors)); }
    private static void BeginScope(WgpuContext context)
    { Volatile.Write(ref _scopeDone, 0); context.Wgpu.DevicePushErrorScope(context.Device, ErrorFilter.Validation); }
    private static void EndScope(WgpuContext context, string stage)
    {
        context.Wgpu.DevicePopErrorScope(context.Device, new PfnErrorCallback(&ScopeComplete), null);
        Wait(context, () => Volatile.Read(ref _scopeDone) != 0, "validation " + stage);
        ThrowErrors();
    }
    private static void Wait(WgpuContext context, Func<bool> complete, string stage)
    {
        var timer = Stopwatch.StartNew();
        while (!complete())
        {
            context.PollDevice(false);
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) Environment.FailFast("Timed out awaiting " + stage + "; pending callbacks are not completion.");
            if (!complete()) Thread.Sleep(1);
        }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ScopeComplete(ErrorType type, byte* message, void* state)
    {
        if (type != ErrorType.NoError) Errors.Enqueue(type + ": " + Marshal.PtrToStringUTF8((nint)message));
        Volatile.Write(ref _scopeDone, 1);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void QueueComplete(QueueWorkDoneStatus status, void* state)
    { _queueStatus = (int)status; Volatile.Write(ref _queueDone, 1); }
}
