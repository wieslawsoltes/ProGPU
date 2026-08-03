using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using SkiaSharp.Internals;

return ProgramEntry.Run(args);

internal static class ProgramEntry
{
#if PROGPU_SHIM
    private const string CompiledBackend = "ProGPU";
#else
    private const string CompiledBackend = "Native";
#endif

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static long s_sink;
    private static SKTypeface? s_variableTypeface;
    private static readonly byte[] ImagePixels = CreateImagePixels();
    private static readonly byte[] AvaloniaWriteableBitmapPixels =
        CreateAvaloniaWriteableBitmapPixels();
    private static readonly SKColorF[] ShaderColors =
    {
        new(1f, 0f, 0f, 1f),
        new(0f, 1f, 0f, 1f),
        new(0f, 0f, 1f, 1f)
    };
    private static readonly float[] ShaderColorPositions = { 0f, 0.375f, 1f };
    private static readonly ushort[] AvaloniaGlyphIndices = CreateAvaloniaGlyphIndices();
    private static readonly SKPoint[] AvaloniaGlyphPositions = CreateAvaloniaGlyphPositions();
    private const string RuntimeEffectSource = """
        uniform float gain;
        uniform float2 offset;
        uniform float4 tint;
        half4 main(float2 position) {
            return half4(position + offset, gain, tint.a);
        }
        """;

    public static int Run(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            if (args.Length == 0)
                return Usage();

            var options = CliOptions.Parse(args.AsSpan(1));
            return args[0] switch
            {
                "run" => RunBenchmarks(options),
                "compare" => Compare(options),
                _ => Usage()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunBenchmarks(CliOptions options)
    {
        var backend = options.Optional("backend") ?? CompiledBackend;
        if (!string.Equals(backend, CompiledBackend, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"This binary was compiled for {CompiledBackend}, not {backend}.");
        }

        // Thirty-two full workload passes are required for stable Tier-1 dynamic-PGO
        // code on the retained canvas route. Shorter warmups can report Tier-0 time
        // for ProGPU while the native P/Invoke wrapper has already stabilized.
        var warmupCount = options.OptionalInt("warmup", 32);
        var sampleCount = options.OptionalInt("samples", 24);
        var operationOverride = options.OptionalInt("operations", 0);
        if (warmupCount < 1 || sampleCount < 3)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (operationOverride < 0)
            throw new ArgumentOutOfRangeException(nameof(options));

        var cases = new[]
        {
            new BenchmarkCase("point-arithmetic", 200_000, RunPointArithmetic),
            new BenchmarkCase("color-span-parse", 100_000, RunColorSpanParse),
            new BenchmarkCase("roundrect-lifetime", 10_000, RunRoundRectLifetime),
            new BenchmarkCase("matrix-map-point", 100_000, RunMatrixMapPoint),
            new BenchmarkCase("pmcolor-premultiply", 65_536, RunPremultiplyColor),
            new BenchmarkCase("pmcolor-unpremultiply", 65_536, RunUnpremultiplyColor),
            new BenchmarkCase("pmcolor-array-premultiply", 1_000, RunPremultiplyColorArrays),
            new BenchmarkCase("pmcolor-array-unpremultiply", 1_000, RunUnpremultiplyColorArrays),
            new BenchmarkCase("four-byte-tag-value", 100_000, RunFourByteTagValue),
            new BenchmarkCase("four-byte-tag-format", 10_000, RunFourByteTagFormat),
            new BenchmarkCase("font-variation-value", 100_000, RunFontVariationValue),
            new BenchmarkCase("font-variation-query", 100_000, RunFontVariationQuery),
            new BenchmarkCase("font-variation-clone", 1_000, RunFontVariationClone),
            new BenchmarkCase("font-arguments-clone", 1_000, RunFontArgumentsClone),
            new BenchmarkCase("text-raw-run-buffer", 100_000, RunTextRawRunBuffer),
            new BenchmarkCase("version-compatibility", 100_000, RunVersionCompatibility),
            new BenchmarkCase("pixel-format-metadata", 100_000, RunPixelFormatMetadata),
            new BenchmarkCase("color-primaries-to-d50", 100_000, RunColorPrimariesToD50),
            new BenchmarkCase("codec-frame-info-value", 100_000, RunCodecFrameInfoValue),
            new BenchmarkCase("encoder-descriptor-value", 100_000, RunEncoderDescriptorValue),
            new BenchmarkCase("backend-handle-info-value", 100_000, RunBackendHandleInfoValue),
            new BenchmarkCase("vulkan-descriptor-value", 100_000, RunVulkanDescriptorValue),
            new BenchmarkCase("d3d-resource-info-value", 100_000, RunD3DResourceInfoValue),
            new BenchmarkCase("backend-wrapper-metadata", 100_000, RunBackendWrapperMetadata),
            new BenchmarkCase("graphics-cache-controls", 100_000, RunGraphicsCacheControls),
            new BenchmarkCase("platform-lock-read", 100_000, RunPlatformLockRead),
            new BenchmarkCase("gr-context-options", 100_000, RunGrContextOptions),
            new BenchmarkCase("canvas-retained-state-routing", 10_000, RunCanvasRetainedStateRouting),
            new BenchmarkCase("canvas-save-restore", 100_000, RunCanvasSaveRestore),
            new BenchmarkCase("canvas-matrix-routing", 100_000, RunCanvasMatrixRouting),
            new BenchmarkCase("canvas-clip-routing", 10_000, RunCanvasClipRouting),
            new BenchmarkCase("avalonia-paint-reuse", 100_000, RunAvaloniaPaintReuse),
            new BenchmarkCase("avalonia-positioned-text-blob", 1_000, RunAvaloniaPositionedTextBlob),
            new BenchmarkCase("avalonia-stream-geometry", 1_000, RunAvaloniaStreamGeometry),
            new BenchmarkCase("avalonia-path-measure-create", 10_000, RunAvaloniaPathMeasureCreate),
            new BenchmarkCase("avalonia-path-measure-query", 100_000, RunAvaloniaPathMeasureQuery),
            new BenchmarkCase("avalonia-path-transform-copy", 10_000, RunAvaloniaPathTransformCopy),
            new BenchmarkCase("avalonia-region-union-query", 10_000, RunAvaloniaRegionUnionQuery),
            new BenchmarkCase("avalonia-stroke-expand", 1_000, RunAvaloniaStrokeExpand),
            new BenchmarkCase("avalonia-path-combine", 8, RunAvaloniaPathCombine),
            // Keep each sample above the sub-millisecond timer-noise floor and
            // warm the gradient factories through their final dynamic-PGO tier.
            new BenchmarkCase("shader-gradient-factories", 16_000, RunShaderGradientFactories),
            new BenchmarkCase("runtime-effect-uniform-snapshot", 1_000, RunRuntimeEffectUniformSnapshot),
            // Amortize the one-time image upload so the distribution measures the
            // allocation and reference-count path of each immutable subset view.
            new BenchmarkCase("image-bounded-subset", 10_000, RunImageBoundedSubset),
            new BenchmarkCase("surface-bounded-snapshot", 10_000, RunSurfaceBoundedSnapshot),
            new BenchmarkCase(
                "avalonia-surface-frame",
                256,
                RunAvaloniaSurfaceFrame),
            new BenchmarkCase(
                "avalonia-surface-compose",
                128,
                RunAvaloniaSurfaceCompose),
            new BenchmarkCase(
                "avalonia-surface-conversion-readback",
                32,
                RunAvaloniaSurfaceConversionReadback),
            new BenchmarkCase(
                "avalonia-surface-direct-readback",
                32,
                RunAvaloniaSurfaceDirectReadback),
            new BenchmarkCase(
                "avalonia-image-repeated-readback",
                32,
                RunAvaloniaImageRepeatedReadback),
            new BenchmarkCase(
                "avalonia-writeable-bitmap-snapshot",
                128,
                RunAvaloniaWriteableBitmapSnapshot),
            new BenchmarkCase(
                "avalonia-immutable-image-recording",
                1_000,
                RunAvaloniaImmutableImageRecording),
            new BenchmarkCase(
                "avalonia-mixed-picture-recording",
                256,
                RunAvaloniaMixedPictureRecording),
            new BenchmarkCase("string-encoding-roundtrip", 10_000, RunStringEncodingRoundtrip),
            new BenchmarkCase("unicode-character-code", 100_000, RunUnicodeCharacterCode),
            new BenchmarkCase("swizzle-in-place-4k", 10_000, RunSwizzleInPlace),
            new BenchmarkCase("swizzle-copy-4k", 10_000, RunSwizzleCopy),
            new BenchmarkCase("path-build-bounds", 1_000, RunPathBuildBounds)
        };
        var selectedCase = options.Optional("case");
        var selectedCases = selectedCase is null
            ? cases
            : cases.Where(value => value.Name == selectedCase).ToArray();
        if (selectedCases.Length == 0)
        {
            throw new ArgumentException($"Unknown benchmark case: {selectedCase}.");
        }

        var results = new List<BenchmarkCaseResult>(selectedCases.Length);
        foreach (var benchmark in selectedCases)
        {
            var operations = operationOverride > 0
                ? operationOverride
                : benchmark.Operations;
            for (var index = 0; index < warmupCount; index++)
                Volatile.Write(ref s_sink, unchecked((long)benchmark.Body(operations)));

            var elapsed = new double[sampleCount];
            var allocated = new double[sampleCount];
            ulong checksum = 0;
            for (var index = 0; index < sampleCount; index++)
            {
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                checksum = benchmark.Body(operations);
                var finished = Stopwatch.GetTimestamp();
                var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
                Volatile.Write(ref s_sink, unchecked((long)checksum));

                elapsed[index] =
                    (finished - started) * 1_000_000_000d /
                    Stopwatch.Frequency /
                    operations;
                allocated[index] =
                    (allocatedAfter - allocatedBefore) /
                    operations;
            }

            results.Add(
                new BenchmarkCaseResult(
                    benchmark.Name,
                    operations,
                    checksum,
                    elapsed,
                    allocated,
                    Median(elapsed),
                    Percentile(elapsed, 0.95),
                    elapsed.Min(),
                    elapsed.Max(),
                    Median(allocated)));
            Console.WriteLine(
                $"{backend} {benchmark.Name}: " +
                $"median={Median(elapsed):F3} ns/op, " +
                $"p95={Percentile(elapsed, 0.95):F3} ns/op, " +
                $"allocated={Median(allocated):F3} B/op, " +
                $"checksum={checksum}.");
        }

        var run = new BenchmarkRun(
            1,
            backend,
            options.Optional("commit") ?? "unknown",
            options.Optional("dirty") ?? "unknown",
            DateTimeOffset.UtcNow,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            typeof(SKPoint).Assembly.GetName().Version?.ToString() ?? "unknown",
            warmupCount,
            sampleCount,
            results);
        WriteJson(options.Required("output"), run);
        return 0;
    }

    private static int Compare(CliOptions options)
    {
        var nativeRuns = ReadRuns(options.Required("native"));
        var proGpuRuns = ReadRuns(options.Required("progpu"));
        if (nativeRuns.Count == 0 || proGpuRuns.Count == 0)
            throw new InvalidDataException("Both backends require at least one run.");

        var comparisons = new List<BenchmarkComparison>();
        foreach (var nativeGroup in nativeRuns
                     .SelectMany(run => run.Results)
                     .GroupBy(result => result.Name, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var proGpuGroup = proGpuRuns
                .SelectMany(run => run.Results)
                .Where(result => result.Name == nativeGroup.Key)
                .ToArray();
            if (proGpuGroup.Length == 0)
                throw new InvalidDataException($"Missing ProGPU case {nativeGroup.Key}.");

            var nativeChecksums = nativeGroup.Select(result => result.Checksum).Distinct().ToArray();
            var proGpuChecksums = proGpuGroup.Select(result => result.Checksum).Distinct().ToArray();
            if (nativeChecksums.Length != 1 ||
                proGpuChecksums.Length != 1 ||
                nativeChecksums[0] != proGpuChecksums[0])
            {
                throw new InvalidDataException(
                    $"Semantic checksum mismatch for {nativeGroup.Key}: " +
                    $"native={string.Join(',', nativeChecksums)}, " +
                    $"ProGPU={string.Join(',', proGpuChecksums)}.");
            }

            var nativeTime = nativeGroup.SelectMany(result => result.NanosecondsPerOperation).ToArray();
            var proGpuTime = proGpuGroup.SelectMany(result => result.NanosecondsPerOperation).ToArray();
            var nativeAllocated = nativeGroup.SelectMany(result => result.AllocatedBytesPerOperation).ToArray();
            var proGpuAllocated = proGpuGroup.SelectMany(result => result.AllocatedBytesPerOperation).ToArray();
            var nativeMedian = Median(nativeTime);
            var proGpuMedian = Median(proGpuTime);
            comparisons.Add(
                new BenchmarkComparison(
                    nativeGroup.Key,
                    nativeTime.Length,
                    proGpuTime.Length,
                    nativeChecksums[0],
                    nativeMedian,
                    Percentile(nativeTime, 0.95),
                    proGpuMedian,
                    Percentile(proGpuTime, 0.95),
                    proGpuMedian / nativeMedian,
                    Median(nativeAllocated),
                    Median(proGpuAllocated)));
        }

        var report = new BenchmarkComparisonReport(
            1,
            DateTimeOffset.UtcNow,
            nativeRuns.Select(run => run.RepositoryCommit).Distinct().ToArray(),
            proGpuRuns.Select(run => run.RepositoryCommit).Distinct().ToArray(),
            nativeRuns[0].Framework,
            nativeRuns[0].OperatingSystem,
            nativeRuns[0].Architecture,
            comparisons);
        WriteJson(options.Required("json"), report);
        WriteMarkdown(options.Required("markdown"), report);

        foreach (var comparison in comparisons)
        {
            Console.WriteLine(
                $"{comparison.Name}: native={comparison.NativeMedianNanoseconds:F3} ns/op, " +
                $"ProGPU={comparison.ProGpuMedianNanoseconds:F3} ns/op, " +
                $"ratio={comparison.ProGpuToNativeRatio:F3}, " +
                $"alloc={comparison.NativeMedianAllocatedBytes:F3}/" +
                $"{comparison.ProGpuMedianAllocatedBytes:F3} B/op.");
        }

        return 0;
    }

    private static ulong RunGraphicsCacheControls(int operations)
    {
        _ = SKGraphics.SetFontCacheCountLimit(2_048);
        _ = SKGraphics.SetFontCacheLimit(2 * 1024 * 1024);
        _ = SKGraphics.SetResourceCacheSingleAllocationByteLimit(0);
        _ = SKGraphics.SetResourceCacheTotalByteLimit(256 * 1024 * 1024);
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var count = 1_024 + (index & 1);
            var bytes = 4_194_304L + (index & 1);
            _ = SKGraphics.SetFontCacheCountLimit(count);
            _ = SKGraphics.SetFontCacheLimit(bytes);
            checksum = Mix(checksum, (uint)SKGraphics.GetFontCacheCountLimit());
            checksum = Mix(checksum, (ulong)SKGraphics.GetFontCacheLimit());
        }

        return checksum;
    }

    private static ulong RunTextRawRunBuffer(int operations)
    {
        using var font = new SKFont(SKTypeface.Default, 16f);
        using var builder = new SKTextBlobBuilder();
        var run = builder.AllocateRawPositionedTextRun(font, 1, textByteCount: 1);
        run.Glyphs[0] = 7;
        run.Positions[0] = new SKPoint(3f, 5f);
        run.Text[0] = 11;
        run.Clusters[0] = 13;

        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            checksum = Mix(checksum, run.Glyphs[0]);
            checksum = Mix(checksum, BitConverter.SingleToUInt32Bits(run.Positions[0].X));
            checksum = Mix(checksum, run.Text[0]);
            checksum = Mix(checksum, run.Clusters[0]);
        }

        return checksum;
    }

    private static ulong RunPlatformLockRead(int operations)
    {
        IPlatformLock platformLock = PlatformLock.Create();
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            platformLock.EnterReadLock();
            checksum = Mix(checksum, (uint)(index & 7));
            platformLock.ExitReadLock();
        }

        return checksum;
    }

    private static ulong RunSurfaceBoundedSnapshot(int operations)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(new SKColor(25, 75, 125, 255));
        surface.Flush();
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var offset = index & 15;
            using var snapshot = surface.Snapshot(new SKRectI(offset, offset, offset + 32, offset + 32));
            checksum = Mix(checksum, (uint)snapshot.Width);
            checksum = Mix(checksum, (uint)snapshot.Height);
        }

        return checksum;
    }

    private static unsafe ulong RunAvaloniaSurfaceFrame(int operations)
    {
        const int width = 128;
        const int height = 96;
        var info = new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var surface = SKSurface.Create(
            info,
            new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
        var canvas = surface.Canvas;
        for (var index = 0; index < operations; index++)
        {
            canvas.Clear(new SKColor(
                (byte)index,
                (byte)(index * 3),
                (byte)(index * 7),
                255));
            canvas.Flush();
        }

        var pixels = new byte[checked(width * height * 4)];
        fixed (byte* destination = pixels)
        {
            using var snapshot = surface.Snapshot();
            if (!snapshot.ReadPixels(info, (IntPtr)destination, info.RowBytes))
            {
                throw new InvalidOperationException(
                    "The Avalonia surface-frame benchmark could not read its final frame.");
            }
        }

        return MixPixels(pixels);
    }

    private static unsafe ulong RunAvaloniaSurfaceCompose(int operations)
    {
        const int width = 96;
        const int height = 64;
        var info = new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var source = SKSurface.Create(
            info,
            new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
        using var destination = SKSurface.Create(
            info,
            new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
        var sourceCanvas = source.Canvas;
        var destinationCanvas = destination.Canvas;
        for (var index = 0; index < operations; index++)
        {
            sourceCanvas.Clear(new SKColor(
                (byte)(index * 5),
                (byte)(index * 3),
                (byte)index,
                255));
            sourceCanvas.Flush();
            destinationCanvas.Clear(SKColors.Transparent);
            source.Draw(destinationCanvas, 0f, 0f, null);
            destinationCanvas.Flush();
        }

        var pixels = new byte[checked(width * height * 4)];
        fixed (byte* destinationPixels = pixels)
        {
            using var snapshot = destination.Snapshot();
            if (!snapshot.ReadPixels(
                    info,
                    (IntPtr)destinationPixels,
                    info.RowBytes))
            {
                throw new InvalidOperationException(
                    "The Avalonia surface-compose benchmark could not read its final frame.");
            }
        }

        return MixPixels(pixels);
    }

    private static unsafe ulong RunAvaloniaSurfaceConversionReadback(int operations)
    {
        const int width = 64;
        const int height = 48;
        var info = new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        var surfacePixels = new byte[checked(width * height * 4)];
        var destinationPixels = new byte[surfacePixels.Length];
        ulong checksum = 1469598103934665603UL;
        fixed (byte* surfaceAddress = surfacePixels)
        fixed (byte* destinationAddress = destinationPixels)
        {
            using var surface = SKSurface.Create(
                info,
                (IntPtr)surfaceAddress,
                info.RowBytes,
                new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
            var canvas = surface.Canvas;
            for (var index = 0; index < operations; index++)
            {
                canvas.Clear(new SKColor(
                    (byte)(index * 11),
                    (byte)(index * 7),
                    (byte)(index * 3),
                    255));
                using var snapshot = surface.Snapshot();
                if (!snapshot.ReadPixels(
                        info,
                        (IntPtr)destinationAddress,
                        info.RowBytes,
                        0,
                        0,
                        SKImageCachingHint.Disallow))
                {
                    throw new InvalidOperationException(
                        "The Avalonia conversion benchmark could not read its frame.");
                }

                checksum = Mix(checksum, destinationPixels[0]);
                checksum = Mix(checksum, destinationPixels[1]);
                checksum = Mix(checksum, destinationPixels[2]);
                checksum = Mix(checksum, destinationPixels[3]);
            }
        }

        return checksum;
    }

    private static unsafe ulong RunAvaloniaSurfaceDirectReadback(int operations)
    {
        const int width = 64;
        const int height = 48;
        var info = new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        var destinationPixels = new byte[checked(width * height * 4)];
        ulong checksum = 1469598103934665603UL;
        fixed (byte* destinationAddress = destinationPixels)
        {
            using var surface = SKSurface.Create(
                info,
                new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
            var canvas = surface.Canvas;
            for (var index = 0; index < operations; index++)
            {
                canvas.Clear(new SKColor(
                    (byte)(index * 13),
                    (byte)(index * 7),
                    (byte)(index * 5),
                    255));
                if (!surface.ReadPixels(
                        info,
                        (IntPtr)destinationAddress,
                        info.RowBytes,
                        0,
                        0))
                {
                    throw new InvalidOperationException(
                        "The direct surface-readback benchmark could not read its frame.");
                }

                checksum = Mix(checksum, destinationPixels[0]);
                checksum = Mix(checksum, destinationPixels[1]);
                checksum = Mix(checksum, destinationPixels[2]);
                checksum = Mix(checksum, destinationPixels[3]);
            }
        }

        return checksum;
    }

    private static unsafe ulong RunAvaloniaImageRepeatedReadback(int operations)
    {
        const int width = 64;
        const int height = 48;
        var info = new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        var destinationPixels = new byte[checked(width * height * 4)];
        using var surface = SKSurface.Create(
            info,
            new SKSurfaceProperties(SKPixelGeometry.RgbHorizontal));
        surface.Canvas.Clear(new SKColor(31, 79, 143, 255));
        using var image = surface.Snapshot();
        ulong checksum = 1469598103934665603UL;
        fixed (byte* destinationAddress = destinationPixels)
        {
            for (var index = 0; index < operations; index++)
            {
                if (!image.ReadPixels(
                        info,
                        (IntPtr)destinationAddress,
                        info.RowBytes,
                        0,
                        0,
                        SKImageCachingHint.Disallow))
                {
                    throw new InvalidOperationException(
                        "The repeated image-readback benchmark could not read its frame.");
                }

                checksum = Mix(checksum, destinationPixels[0]);
                checksum = Mix(checksum, destinationPixels[1]);
                checksum = Mix(checksum, destinationPixels[2]);
                checksum = Mix(checksum, destinationPixels[3]);
            }
        }

        return checksum;
    }

    private static unsafe ulong RunAvaloniaWriteableBitmapSnapshot(int operations)
    {
        var info = new SKImageInfo(
            16,
            16,
            SKImageInfo.PlatformColorType,
            SKAlphaType.Premul);
        ulong checksum = 1469598103934665603UL;
        fixed (byte* pixels = AvaloniaWriteableBitmapPixels)
        {
            for (var index = 0; index < operations; index++)
            {
                using var image = SKImage.FromPixels(
                    info,
                    (IntPtr)pixels,
                    info.RowBytes);
                checksum = Mix(checksum, unchecked((uint)image.Width));
                checksum = Mix(checksum, unchecked((uint)image.Height));
                // Skia's native N32 channel order is selected by its build and
                // can be BGRA where the WebGPU shim deliberately normalizes to
                // RGBA. Both are the same four-channel Avalonia contract; keep
                // the semantic checksum sensitive to that contract without
                // requiring identical backend storage order.
                checksum = Mix(
                    checksum,
                    image.ColorType is SKColorType.Rgba8888 or SKColorType.Bgra8888
                        ? 4u
                        : unchecked((uint)image.ColorType));
                checksum = Mix(checksum, unchecked((uint)image.AlphaType));
            }
        }

        return checksum;
    }

    private static unsafe ulong RunAvaloniaImmutableImageRecording(int operations)
    {
        var info = new SKImageInfo(
            16,
            16,
            SKImageInfo.PlatformColorType,
            SKAlphaType.Premul);
        fixed (byte* pixels = AvaloniaWriteableBitmapPixels)
        {
            using var image = SKImage.FromPixels(
                info,
                (IntPtr)pixels,
                info.RowBytes);
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 64f, 64f));
            using var paint = new SKPaint { IsAntialias = false };
            var source = new SKRect(0f, 0f, 16f, 16f);
            ulong checksum = 1469598103934665603UL;
            for (var index = 0; index < operations; index++)
            {
                var offset = index & 7;
                var destination = new SKRect(
                    offset,
                    offset,
                    offset + 16f,
                    offset + 16f);
                canvas.DrawImage(
                    image,
                    source,
                    destination,
                    SKSamplingOptions.Default,
                    paint);
                checksum = Mix(checksum, unchecked((uint)offset));
            }

            using var picture = recorder.EndRecording();
            checksum = Mix(
                checksum,
                BitConverter.SingleToUInt32Bits(picture.CullRect.Width));
            checksum = Mix(
                checksum,
                BitConverter.SingleToUInt32Bits(picture.CullRect.Height));
            return checksum;
        }
    }

    private static ulong RunAvaloniaMixedPictureRecording(int operations)
    {
        using var image = SKImage.FromPixelCopy(
            new SKImageInfo(
                64,
                64,
                SKColorType.Rgba8888,
                SKAlphaType.Premul),
            ImagePixels);
        using var fillPaint = new SKPaint
        {
            Color = new SKColor(32, 96, 224, 208),
            IsAntialias = true
        };
        using var strokePaint = new SKPaint
        {
            Color = new SKColor(240, 224, 48, 255),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f
        };
        using var font = new SKFont(SKTypeface.Default, 14f);
        using var text = SKTextBlob.Create("Avalonia", font) ??
            throw new InvalidOperationException("Text must produce a retained blob.");
        using var builder = new SKPathBuilder();
        builder.MoveTo(2f, 3f);
        builder.QuadTo(18f, 1f, 30f, 20f);
        builder.CubicTo(38f, 31f, 47f, 4f, 60f, 28f);
        builder.Close();
        using var path = builder.Detach();
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 96f, 96f));
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var offset = (index & 7) * 0.25f;
            canvas.Save();
            canvas.Translate(offset, -offset);
            canvas.ClipRect(new SKRect(0f, 0f, 92f, 92f));
            canvas.DrawRect(new SKRect(1f, 2f, 31f, 25f), fillPaint);
            canvas.DrawPath(path, strokePaint);
            canvas.DrawText(text, 4f, 48f, fillPaint);
            canvas.DrawImage(
                image,
                new SKRect(0f, 0f, 64f, 64f),
                new SKRect(40f, 8f, 72f, 40f),
                SKSamplingOptions.Default,
                fillPaint);
            canvas.Restore();
            checksum = Mix(checksum, BitConverter.SingleToUInt32Bits(offset));
        }

        using var picture = recorder.EndRecording();
        checksum = Mix(
            checksum,
            BitConverter.SingleToUInt32Bits(picture.CullRect.Width));
        return Mix(
            checksum,
            BitConverter.SingleToUInt32Bits(picture.CullRect.Height));
    }

    private static ulong RunImageBoundedSubset(int operations)
    {
        using var image = SKImage.FromPixelCopy(
            new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul),
            ImagePixels);
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var offset = index & 15;
            using var subset = image.Subset(
                new SKRectI(offset, offset, offset + 32, offset + 32));
            checksum = Mix(checksum, (uint)subset.Width);
            checksum = Mix(checksum, (uint)subset.Height);
        }

        return checksum;
    }

    private static ulong RunCanvasRetainedStateRouting(int operations)
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 64f, 64f));
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            int restoreCount = canvas.Save();
            canvas.Scale(1.0001f);
            var translation = SKMatrix.CreateTranslation(index & 3, index & 7);
            canvas.Concat(in translation);
            canvas.ClipRect(new SKRect(1f, 2f, 63f, 62f));
            checksum = Mix(checksum, (uint)canvas.SaveCount);
            canvas.RestoreToCount(restoreCount);
            checksum = Mix(checksum, (uint)canvas.SaveCount);
        }

        using var picture = recorder.EndRecording();
        checksum = Mix(checksum, picture is null ? 0u : 1u);
        return checksum;
    }

    private static ulong RunCanvasSaveRestore(int operations)
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 64f, 64f));
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var restoreCount = canvas.Save();
            checksum = Mix(checksum, (uint)canvas.SaveCount);
            canvas.RestoreToCount(restoreCount);
        }

        using var picture = recorder.EndRecording();
        return Mix(checksum, picture is null ? 0u : 1u);
    }

    private static ulong RunCanvasMatrixRouting(int operations)
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 64f, 64f));
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            canvas.Scale(1.0001f);
            var translation = SKMatrix.CreateTranslation(index & 3, index & 7);
            canvas.Concat(in translation);
            checksum = Mix(checksum, BitConverter.SingleToUInt32Bits(canvas.TotalMatrix.TransX));
            canvas.ResetMatrix();
        }

        using var picture = recorder.EndRecording();
        return Mix(checksum, picture is null ? 0u : 1u);
    }

    private static ulong RunCanvasClipRouting(int operations)
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 64f, 64f));
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var restoreCount = canvas.Save();
            canvas.ClipRect(new SKRect(1f, 2f, 63f, 62f));
            checksum = Mix(checksum, (uint)canvas.SaveCount);
            canvas.RestoreToCount(restoreCount);
        }

        using var picture = recorder.EndRecording();
        return Mix(checksum, picture is null ? 0u : 1u);
    }

    private static ulong RunAvaloniaPaintReuse(int operations)
    {
        using var paint = new SKPaint();
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            paint.IsAntialias = (index & 1) == 0;
            paint.Color = new SKColor(
                (byte)index,
                (byte)(index >> 3),
                (byte)(255 - index),
                (byte)(192 + (index & 63)));
            paint.IsStroke = true;
            paint.StrokeWidth = 1f + (index & 7) * 0.25f;
            paint.StrokeCap = SKStrokeCap.Square;
            paint.StrokeJoin = SKStrokeJoin.Round;
            paint.StrokeMiter = 4f + (index & 3);
            paint.BlendMode = SKBlendMode.DstIn;

            checksum = Mix(checksum, (uint)paint.Color);
            checksum = Mix(checksum, BitConverter.SingleToUInt32Bits(paint.StrokeWidth));
            checksum = Mix(checksum, (uint)paint.BlendMode);
            paint.Reset();
        }

        return checksum;
    }

    private static ulong RunAvaloniaPositionedTextBlob(int operations)
    {
        using var font = new SKFont(SKTypeface.Default, 16f);
        using var builder = new SKTextBlobBuilder();
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var run = builder.AllocatePositionedRun(font, AvaloniaGlyphIndices.Length);
            run.SetPositions(AvaloniaGlyphPositions);
            run.SetGlyphs(AvaloniaGlyphIndices);
            using var blob = builder.Build() ??
                throw new InvalidOperationException("A positioned glyph run must produce a text blob.");
            checksum = Mix(checksum, blob.UniqueId == 0 ? 0u : 1u);
        }

        return checksum;
    }

#pragma warning disable CS0618 // Avalonia.Skia 12 currently builds stream geometry through legacy SKPath mutation.
    private static ulong RunAvaloniaStreamGeometry(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var offset = (index & 15) * 0.125f;
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            path.MoveTo(offset, -offset);
            for (var segment = 0; segment < 8; segment++)
            {
                var x = segment * 6f + offset;
                var y = segment * 3f - offset;
                path.LineTo(x + 1f, y + 2f);
                path.QuadTo(x + 2f, y - 1f, x + 3f, y + 3f);
                path.CubicTo(
                    x + 3.5f,
                    y + 4f,
                    x + 4.5f,
                    y - 2f,
                    x + 5f,
                    y + 1f);
            }
            path.Close();
            var bounds = path.TightBounds;
            checksum = Mix(
                checksum,
                Combine(bounds.Left + bounds.Right, bounds.Top + bounds.Bottom));
        }

        return checksum;
    }
#pragma warning restore CS0618

#pragma warning disable CS0618 // Avalonia.Skia 12 consumes these official legacy geometry APIs.
    private static ulong RunAvaloniaPathMeasureCreate(int operations)
    {
        using var path = CreateAvaloniaGeometryPath();
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            using var measure = new SKPathMeasure(path, forceClosed: false, resScale: 1f);
            checksum = Mix(checksum, measure.Length > 0f ? 1u : 0u);
            checksum = Mix(checksum, measure.IsClosed ? 1u : 0u);
        }

        return checksum;
    }

    private static ulong RunAvaloniaPathMeasureQuery(int operations)
    {
        using var path = CreateAvaloniaGeometryPath();
        using var measure = new SKPathMeasure(path, forceClosed: false, resScale: 1f);
        var length = measure.Length;
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var distance = length * ((index & 255) / 255f);
            var success = measure.GetPositionAndTangent(distance, out var point, out var tangent);
            checksum = Mix(checksum, success ? 1u : 0u);
            checksum = Mix(checksum, float.IsFinite(point.X + point.Y + tangent.X + tangent.Y) ? 1u : 0u);
        }

        return checksum;
    }

    private static ulong RunAvaloniaPathTransformCopy(int operations)
    {
        using var source = CreateAvaloniaGeometryPath();
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            using var transformed = new SKPath(source);
            var matrix = SKMatrix.CreateTranslation((index & 15) * 0.25f, (index & 7) * -0.125f);
            transformed.Transform(matrix);
            var bounds = transformed.TightBounds;
            checksum = Mix(checksum, bounds.Width > 0f && bounds.Height > 0f ? 1u : 0u);
        }

        return checksum;
    }

    private static ulong RunAvaloniaRegionUnionQuery(int operations)
    {
        using var region = new SKRegion();
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            region.SetEmpty();
            for (var rectIndex = 0; rectIndex < 24; rectIndex++)
            {
                var x = rectIndex * 3;
                region.Op(x, rectIndex & 3, x + 12, (rectIndex & 3) + 10, SKRegionOperation.Union);
            }

            checksum = Mix(checksum, region.Contains(15, 5) ? 1u : 0u);
            checksum = Mix(checksum, region.Intersects(new SKRectI(48, 2, 54, 8)) ? 1u : 0u);
            checksum = Mix(checksum, (uint)region.Bounds.Width);
        }

        return checksum;
    }

    private static ulong RunAvaloniaStrokeExpand(int operations)
    {
        using var source = CreateAvaloniaGeometryPath();
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3.5f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeMiter = 4f,
        };
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            using var destination = new SKPath();
            var success = paint.GetFillPath(source, destination, 1f);
            checksum = Mix(checksum, success ? 1u : 0u);
            checksum = Mix(checksum, destination.IsEmpty ? 0u : 1u);
        }

        return checksum;
    }

    private static ulong RunAvaloniaPathCombine(int operations)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var left = new SKPath();
        using var right = new SKPath();
        left.AddRoundRect(new SKRect(0f, 0f, 80f, 48f), 8f, 8f);
        right.AddCircle(52f, 24f, 20f);
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            using var combined = left.Op(right, (index & 1) == 0 ? SKPathOp.Union : SKPathOp.Intersect);
            checksum = Mix(checksum, combined.IsEmpty ? 0u : 1u);
            checksum = Mix(checksum, combined.Bounds.Width > 0f ? 1u : 0u);
        }

        return checksum;
    }

    private static SKPath CreateAvaloniaGeometryPath()
    {
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.MoveTo(2f, 3f);
        for (var segment = 0; segment < 12; segment++)
        {
            var x = segment * 6f;
            var y = (segment & 1) == 0 ? 4f : 18f;
            path.LineTo(x + 4f, y);
            path.QuadTo(x + 6f, y - 8f, x + 8f, y + 2f);
            path.CubicTo(x + 9f, y + 9f, x + 11f, y - 7f, x + 13f, y + 1f);
        }
        path.Close();
        return path;
    }
#pragma warning restore CS0618

    private static ushort[] CreateAvaloniaGlyphIndices()
    {
        var glyphs = new ushort[32];
        for (var index = 0; index < glyphs.Length; index++)
            glyphs[index] = (ushort)(index + 1);
        return glyphs;
    }

    private static SKPoint[] CreateAvaloniaGlyphPositions()
    {
        var positions = new SKPoint[32];
        for (var index = 0; index < positions.Length; index++)
            positions[index] = new SKPoint(index * 7.5f, (index & 3) * 0.125f);
        return positions;
    }

    private static ulong RunGrContextOptions(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var options = new GRContextOptions
            {
                AllowPathMaskCaching = (index & 1) == 0,
                AvoidStencilBuffers = (index & 2) == 0,
                BufferMapThreshold = index & 4095,
                DoManualMipmapping = (index & 4) == 0,
                GlyphCacheTextureMaximumBytes = 1_048_576 + index,
                RuntimeProgramCacheSize = 64 + (index & 31),
            };
            checksum = Mix(checksum, options.AllowPathMaskCaching ? 1u : 0u);
            checksum = Mix(checksum, options.AvoidStencilBuffers ? 1u : 0u);
            checksum = Mix(checksum, unchecked((uint)options.BufferMapThreshold));
            checksum = Mix(checksum, options.DoManualMipmapping ? 1u : 0u);
            checksum = Mix(checksum, unchecked((uint)options.GlyphCacheTextureMaximumBytes));
            checksum = Mix(checksum, unchecked((uint)options.RuntimeProgramCacheSize));
        }

        return checksum;
    }

    private static ulong RunShaderGradientFactories(int operations)
    {
        using var colorSpace = SKColorSpace.CreateSrgb();
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var localMatrix = SKMatrix.CreateTranslation(index & 3, index & 7);
            using var linear = SKShader.CreateLinearGradient(
                new SKPoint(0f, 0f),
                new SKPoint(64f, 32f),
                ShaderColors,
                colorSpace,
                ShaderColorPositions,
                SKShaderTileMode.Mirror,
                localMatrix);
            using var radial = SKShader.CreateRadialGradient(
                new SKPoint(32f, 32f),
                24f,
                ShaderColors,
                colorSpace,
                ShaderColorPositions,
                SKShaderTileMode.Repeat,
                localMatrix);
            using var sweep = SKShader.CreateSweepGradient(
                new SKPoint(32f, 32f),
                ShaderColors,
                colorSpace,
                ShaderColorPositions,
                SKShaderTileMode.Clamp,
                -45f,
                315f,
                localMatrix);
            using var conical = SKShader.CreateTwoPointConicalGradient(
                new SKPoint(8f, 8f),
                4f,
                new SKPoint(48f, 40f),
                28f,
                ShaderColors,
                colorSpace,
                ShaderColorPositions,
                SKShaderTileMode.Decal,
                localMatrix);
            checksum = Mix(checksum, linear.Handle == IntPtr.Zero ? 0u : 1u);
            checksum = Mix(checksum, radial.Handle == IntPtr.Zero ? 0u : 1u);
            checksum = Mix(checksum, sweep.Handle == IntPtr.Zero ? 0u : 1u);
            checksum = Mix(checksum, conical.Handle == IntPtr.Zero ? 0u : 1u);
        }

        return checksum;
    }

    private static ulong RunRuntimeEffectUniformSnapshot(int operations)
    {
        using var effect = SKRuntimeEffect.CreateShader(RuntimeEffectSource, out var errors);
        if (effect is null)
            throw new InvalidOperationException(errors);

        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            using var uniforms = new SKRuntimeEffectUniforms(effect);
            uniforms["gain"] = index * 0.001f;
            uniforms["offset"] = new SKPoint(index & 7, -(index & 15));
            uniforms["tint"] = new SKColorF(1f, 0.25f, 0.5f, 0.75f);
            using var data = uniforms.ToData();
            using var shader = effect.ToShader(uniforms);
            checksum = Mix(checksum, unchecked((uint)data.Size));
            checksum = Mix(checksum, unchecked((uint)BitConverter.SingleToInt32Bits(
                MemoryMarshal.Cast<byte, float>(data.Span)[index % 7])));
            checksum = Mix(checksum, shader.Handle == IntPtr.Zero ? 0u : 1u);
        }

        return checksum;
    }

    private static byte[] CreateImagePixels()
    {
        var pixels = new byte[64 * 64 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 25;
            pixels[index + 1] = 75;
            pixels[index + 2] = 125;
            pixels[index + 3] = 255;
        }

        return pixels;
    }

    private static ulong RunPointArithmetic(int operations)
    {
        var point = new SKPoint(1.25f, -3.5f);
        for (var index = 0; index < operations; index++)
        {
            var delta = new SKPoint(
                (index & 31) * 0.03125f,
                (index & 15) * -0.0625f);
            point += delta;
            point = new SKPoint(point.X * 0.99991f, point.Y * 0.99991f);
        }

        return Combine(point.X, point.Y);
    }

    private static ulong RunColorSpanParse(int operations)
    {
        ReadOnlySpan<char> value = "#7f123456";
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            if (!SKColor.TryParse(value, out var color))
                throw new InvalidOperationException("The fixed benchmark color must parse.");
            checksum = Mix(checksum, (uint)color);
        }

        return checksum;
    }

    private static ulong RunRoundRectLifetime(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var radius = (index & 15) + 1f;
            using var value = new SKRoundRect(new SKRect(0f, 0f, 64f, 48f), radius, radius * 0.5f);
            var corner = value.GetRadii(SKRoundRectCorner.UpperLeft);
            checksum = Mix(checksum, Combine(value.Width + corner.X, value.Height + corner.Y));
        }

        return checksum;
    }

    private static ulong RunMatrixMapPoint(int operations)
    {
        var matrix = SKMatrix.CreateRotationDegrees(17.25f, 31f, -12f);
        var point = new SKPoint(0.25f, -0.5f);
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            point = matrix.MapPoint(
                point.X + (index & 7) * 0.125f,
                point.Y - (index & 3) * 0.25f);
            if ((index & 1023) == 0)
            {
                checksum = Mix(checksum, Combine(point.X, point.Y));
                point = new SKPoint(index * 0.001f, index * -0.002f);
            }
        }

        return Mix(checksum, Combine(point.X, point.Y));
    }

    private static ulong RunPathBuildBounds(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            using var builder = new SKPathBuilder();
            var offset = (index & 31) * 0.25f;
            builder.MoveTo(offset, -offset);
            for (var segment = 0; segment < 16; segment++)
            {
                var x = segment * 3f + offset;
                builder.LineTo(x + 1f, segment - offset);
                builder.QuadTo(x + 2f, segment * 0.5f, x + 3f, segment + 1f);
                builder.CubicTo(
                    x + 3.25f,
                    segment + 1.5f,
                    x + 3.75f,
                    segment + 2f,
                    x + 4f,
                    segment + 2.5f);
            }
            builder.Close();
            using var path = builder.Detach();
            var bounds = path.Bounds;
            checksum = Mix(
                checksum,
                Combine(bounds.Left + bounds.Right, bounds.Top + bounds.Bottom));
        }

        return checksum;
    }

    private static ulong RunPremultiplyColor(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var alpha = (byte)(index >> 8);
            var component = (byte)index;
            var source = new SKColor(
                component,
                (byte)(component ^ 0x5a),
                (byte)(255 - component),
                alpha);
            var premultiplied = SKPMColor.PreMultiply(source);
            checksum = Mix(checksum, (uint)premultiplied);
        }

        return checksum;
    }

    private static ulong RunBackendWrapperMetadata(int operations)
    {
        using var texture = new GRBackendTexture(
            320,
            180,
            true,
            new GRGlTextureInfo(0x0de1, 17, 0x8058));
        using var target = new GRBackendRenderTarget(
            320,
            180,
            4,
            8,
            new GRGlFramebufferInfo(29, 0x8058));

        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            texture.GetGlTextureInfo(out var textureInfo);
            target.GetGlFramebufferInfo(out var framebufferInfo);
            var value =
                ((ulong)(uint)texture.Backend << 56) |
                ((ulong)(uint)target.Backend << 48) |
                ((ulong)(uint)texture.Width << 32) |
                ((ulong)(uint)target.SampleCount << 24) |
                ((ulong)textureInfo.Id << 8) |
                framebufferInfo.FramebufferObjectId;
            if (texture.IsValid && target.IsValid && texture.HasMipMaps)
                value ^= 1UL << (index & 7);
            checksum = Mix(checksum, value);
        }

        return checksum;
    }

    private static ulong RunUnpremultiplyColor(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var alpha = (byte)(index >> 8);
            var component = (byte)index;
            var packed =
                ((uint)alpha << 24) |
                ((uint)component << 16) |
                ((uint)(byte)(component ^ 0xa5) << 8) |
                (byte)(255 - component);
            checksum = Mix(
                checksum,
                (uint)SKPMColor.UnPreMultiply(new SKPMColor(packed)));
        }

        return checksum;
    }

    private static ulong RunPremultiplyColorArrays(int operations)
    {
        var source = CreateColorArray();

        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var premultiplied = SKPMColor.PreMultiply(source);
            var item = index & (source.Length - 1);
            checksum = Mix(checksum, (uint)premultiplied[item]);
        }

        return checksum;
    }

    private static ulong RunUnpremultiplyColorArrays(int operations)
    {
        var source = SKPMColor.PreMultiply(CreateColorArray());

        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var restored = SKPMColor.UnPreMultiply(source);
            var item = index & (source.Length - 1);
            checksum = Mix(checksum, (uint)restored[item]);
        }

        return checksum;
    }

    private static SKColor[] CreateColorArray()
    {
        var source = new SKColor[64];
        for (var index = 0; index < source.Length; index++)
        {
            source[index] = new SKColor(
                (byte)(index * 3),
                (byte)(255 - index * 2),
                (byte)(index * 4),
                (byte)(index * 4));
        }

        return source;
    }

    private static ulong RunFourByteTagValue(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var parsed = (index & 3) switch
            {
                0 => SKFourByteTag.Parse("a"),
                1 => SKFourByteTag.Parse("kern"),
                2 => SKFourByteTag.Parse("cmap-extra".AsSpan()),
                _ => new SKFourByteTag('w', 'g', 'h', 't')
            };
            checksum = Mix(checksum, (uint)parsed);
        }

        return checksum;
    }

    private static ulong RunFourByteTagFormat(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var tag = new SKFourByteTag(0x41424300u + (uint)(index & 0xff));
            var text = tag.ToString();
            checksum = Mix(
                checksum,
                (uint)text[0] << 24 |
                (uint)text[1] << 16 |
                (uint)text[2] << 8 |
                text[3]);
        }

        return checksum;
    }

    private static ulong RunFontVariationValue(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var axis = new SKFontVariationAxis
            {
                Tag = SKFourByteTag.Parse("wght"),
                Min = 100,
                Default = 400,
                Max = 900,
                IsHidden = (index & 1) != 0
            };
            var coordinate = new SKFontVariationPositionCoordinate
            {
                Axis = axis.Tag,
                Value = 100 + (index & 799)
            };
            var palette = new SKFontPaletteOverride
            {
                Index = (ushort)(index & 63),
                Color = 0xff000000u | (uint)index
            };
            checksum = Mix(checksum, (uint)axis.Tag);
            checksum = Mix(checksum, unchecked((uint)BitConverter.SingleToInt32Bits(coordinate.Value)));
            checksum = Mix(checksum, palette.Color ^ palette.Index ^ (axis.IsHidden ? 1u : 0u));
        }

        return checksum;
    }

    private static ulong RunFontVariationQuery(int operations)
    {
        SKTypeface typeface = GetVariableTypeface();
        Span<SKFontVariationAxis> axes = stackalloc SKFontVariationAxis[2];
        Span<SKFontVariationPositionCoordinate> position =
            stackalloc SKFontVariationPositionCoordinate[2];
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            int axisCount = typeface.GetVariationDesignParameters(axes);
            int positionCount = typeface.GetVariationDesignPosition(position);
            checksum = Mix(checksum, (uint)(axisCount + positionCount));
            checksum = Mix(checksum, (uint)axes[index & 1].Tag);
            checksum = Mix(
                checksum,
                unchecked((uint)BitConverter.SingleToInt32Bits(position[index & 1].Value)));
        }

        return checksum;
    }

    private static ulong RunFontVariationClone(int operations)
    {
        SKTypeface typeface = GetVariableTypeface();
        Span<SKFontVariationPositionCoordinate> position =
            stackalloc SKFontVariationPositionCoordinate[2]
            {
                new() { Axis = SKFourByteTag.Parse("opsz"), Value = 23 },
                new() { Axis = SKFourByteTag.Parse("wght"), Value = 537 }
            };
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            using SKTypeface clone = typeface.Clone(position);
            Span<SKFontVariationPositionCoordinate> actual =
                stackalloc SKFontVariationPositionCoordinate[2];
            int count = clone.GetVariationDesignPosition(actual);
            checksum = Mix(checksum, (uint)(clone.FontWeight + count));
            checksum = Mix(
                checksum,
                unchecked((uint)BitConverter.SingleToInt32Bits(actual[index & 1].Value)));
        }

        return checksum;
    }

    private static ulong RunFontArgumentsClone(int operations)
    {
        SKTypeface typeface = GetVariableTypeface();
        Span<SKFontVariationPositionCoordinate> position =
            stackalloc SKFontVariationPositionCoordinate[2]
            {
                new() { Axis = SKFourByteTag.Parse("opsz"), Value = 23 },
                new() { Axis = SKFourByteTag.Parse("wght"), Value = 537 }
            };
        var arguments = new SKFontArguments
        {
            CollectionIndex = 0,
            VariationDesignPosition = position
        };
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            using SKTypeface clone = typeface.Clone(arguments);
            checksum = Mix(checksum, (uint)(clone.FontWeight + clone.GlyphCount));
        }

        return checksum;
    }

    private static SKTypeface LoadVariableTypeface()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "InterVariable.ttf");
        return SKTypeface.FromFile(path) ??
            throw new InvalidOperationException($"Unable to load benchmark variable font: {path}.");
    }

    private static SKTypeface GetVariableTypeface() =>
        s_variableTypeface ??= LoadVariableTypeface();

    private static ulong RunVersionCompatibility(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var native = SkiaSharpVersion.Native;
            var minimum = SkiaSharpVersion.NativeMinimum;
            checksum = Mix(
                checksum,
                (uint)native.Major << 24 |
                (uint)native.Minor << 16 |
                (uint)minimum.Major << 8 |
                (uint)minimum.Minor |
                (SkiaSharpVersion.CheckNativeLibraryCompatible((index & 1) != 0) ? 1u : 0u));
        }

        return checksum;
    }

    private static ulong RunPixelFormatMetadata(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var colorType = (SKColorType)(index % 29);
            var geometry = (SKPixelGeometry)(index % 5);
            var alphaType = (SKAlphaType)(index & 3);
            checksum = Mix(
                checksum,
                (uint)colorType.GetBytesPerPixel() << 28 |
                (uint)colorType.GetBitShiftPerPixel() << 24 |
                (uint)colorType.GetAlphaType(alphaType) << 20 |
                (geometry.IsHorizontal() ? 1u << 19 : 0) |
                (geometry.IsVertical() ? 1u << 18 : 0) |
                (geometry.IsRgb() ? 1u << 17 : 0) |
                (geometry.IsBgr() ? 1u << 16 : 0) |
                colorType.ToGlSizedFormat());
        }

        return checksum;
    }

    private static ulong RunStringEncodingRoundtrip(int operations)
    {
        const string text = "ProGPU A😀é — retained text";
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var encoding = (SKTextEncoding)(index % 3);
            var bytes = StringUtilities.GetEncodedText(text, encoding);
            var decoded = StringUtilities.GetString(bytes, encoding);
            checksum = Mix(
                checksum,
                (uint)bytes.Length << 16 |
                (uint)decoded[0] << 8 |
                decoded[^1]);
        }

        return checksum;
    }

    private static ulong RunCodecFrameInfoValue(int operations)
    {
        var frame = new SKCodecFrameInfo
        {
            RequiredFrame = -1,
            Duration = 125,
            FullyRecieved = true,
            AlphaType = SKAlphaType.Premul,
            HasAlphaWithinBounds = true,
            DisposalMethod = SKCodecAnimationDisposalMethod.RestoreBackgroundColor,
            Blend = SKCodecAnimationBlend.SrcOver,
            FrameRect = new SKRectI(1, 2, 31, 42),
        };
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            frame.FullyRecieved = (index & 1) == 0;
            frame.HasAlphaWithinBounds = (index & 2) == 0;
            checksum = Mix(
                checksum,
                (uint)frame.Duration << 16 |
                (frame.FullyRecieved ? 1u << 1 : 0) |
                (frame.HasAlphaWithinBounds ? 1u : 0));
        }

        return checksum;
    }

    private static ulong RunColorPrimariesToD50(int operations)
    {
        var srgb = new SKColorSpacePrimaries(
            0.64f, 0.33f,
            0.30f, 0.60f,
            0.15f, 0.06f,
            0.3127f, 0.3290f);
        var displayP3 = new SKColorSpacePrimaries(
            0.68f, 0.32f,
            0.265f, 0.69f,
            0.15f, 0.06f,
            0.3127f, 0.3290f);
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var primaries = (index & 1) == 0 ? srgb : displayP3;
            if (!primaries.ToColorSpaceXyz(out var matrix))
                throw new InvalidOperationException("Valid color primaries failed conversion.");
            checksum = Mix(
                checksum,
                (ulong)(uint)MathF.Round(matrix[0, 0] * 100_000f) << 32 |
                (uint)MathF.Round(matrix[1, 1] * 100_000f));
        }

        return checksum;
    }

    private static ulong RunEncoderDescriptorValue(int operations)
    {
        var jpeg = new SKJpegEncoderOptions(
            87,
            SKJpegEncoderDownsample.Downsample444,
            SKJpegEncoderAlphaOption.BlendOnBlack);
        var png = new SKPngEncoderOptions(SKPngEncoderFilterFlags.Paeth, 9);
        var xps = new SKDocumentXpsOptions { Dpi = 144f, AllowNoPngs = true };
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            xps.AllowNoPngs = (index & 1) == 0;
            checksum = Mix(
                checksum,
                (uint)jpeg.Quality << 24 |
                (uint)jpeg.Downsample << 20 |
                (uint)png.FilterFlags << 8 |
                (uint)png.ZLibLevel << 1 |
                (xps.AllowNoPngs ? 1u : 0));
        }

        return checksum;
    }

    private static ulong RunBackendHandleInfoValue(int operations)
    {
        var framebuffer = new GRGlFramebufferInfo(17, 0x8058);
        var texture = new GRGlTextureInfo(0x0de1, 29, 0x8058);
        var metal = new GRMtlTextureInfo((IntPtr)0x1234);
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            framebuffer.Protected = (index & 1) == 0;
            texture.Protected = (index & 2) == 0;
            checksum = Mix(
                checksum,
                (ulong)framebuffer.FramebufferObjectId << 32 |
                (ulong)texture.Id << 16 |
                (framebuffer.Protected ? 1u << 1 : 0) |
                (texture.Protected ? 1u : 0));
            checksum = Mix(checksum, (ulong)metal.TextureHandle.ToInt64());
        }

        return checksum;
    }

    private static ulong RunVulkanDescriptorValue(int operations)
    {
        var conversion = new GRVkYcbcrConversionInfo
        {
            Format = 1000156003,
            ExternalFormat = 17,
            YcbcrModel = 1,
            YcbcrRange = 2,
            XChromaOffset = 3,
            YChromaOffset = 4,
            ChromaFilter = 5,
            ForceExplicitReconstruction = 6,
            Components = new GRVkYcbcrComponents { R = 1, G = 2, B = 3, A = 4 },
        };
        var image = new GRVkImageInfo
        {
            Image = 23,
            Alloc = new GRVkAlloc
            {
                Memory = 29,
                Size = 8192,
                Offset = 512,
                Flags = 3,
                BackendMemory = (IntPtr)0x1234,
            },
            ImageTiling = 1,
            ImageLayout = 2,
            Format = 3,
            ImageUsageFlags = 4,
            SampleCount = 8,
            LevelCount = 5,
            CurrentQueueFamily = 6,
            YcbcrConversionInfo = conversion,
            SharingMode = 7,
        };
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            conversion.SupportsLinearFilter = (index & 1) == 0;
            conversion.SamplerFilterMustMatchChromaFilter = (index & 2) == 0;
            image.Protected = (index & 4) == 0;
            image.YcbcrConversionInfo = conversion;
            var same = image;
            same.SharingMode += (uint)(index & 1);
            checksum = Mix(
                checksum,
                image.Image |
                (ulong)image.CurrentQueueFamily << 32 |
                (image.Equals(same) ? 1ul << 3 : 0) |
                (image.Protected ? 1ul << 2 : 0) |
                (conversion.SupportsLinearFilter ? 1ul << 1 : 0) |
                (conversion.SamplerFilterMustMatchChromaFilter ? 1ul : 0));
        }

        return checksum;
    }

    private static ulong RunD3DResourceInfoValue(int operations)
    {
        using var info = new GRD3DTextureResourceInfo
        {
            Resource = (IntPtr)0x1234,
            ResourceState = 4,
            Format = 28,
            LevelCount = 5,
            SampleCount = 8,
            SampleQualityPattern = 9,
        };
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            info.Protected = (index & 1) == 0;
            checksum = Mix(
                checksum,
                (ulong)(nuint)info.Resource |
                (ulong)info.ResourceState << 48 |
                (ulong)info.Format << 32 |
                (ulong)info.LevelCount << 16 |
                (ulong)info.SampleCount << 8 |
                info.SampleQualityPattern |
                (info.Protected ? 1ul << 63 : 0));
        }

        return checksum;
    }

    private static ulong RunUnicodeCharacterCode(int operations)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            var scalar = StringUtilities.GetUnicodeCharacterCode("😀", SKTextEncoding.Utf32);
            checksum = Mix(checksum, (uint)scalar);
        }

        return checksum;
    }

    private static ulong RunSwizzleInPlace(int operations)
    {
        var pixels = CreateSwizzlePixels();
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            SKSwizzle.SwapRedBlue(
                (ReadOnlySpan<byte>)pixels,
                pixels.Length >> 2);
            checksum = Mix(
                checksum,
                (uint)pixels[index & (pixels.Length - 1)] |
                ((uint)pixels[(index + 2) & (pixels.Length - 1)] << 8));
        }

        return checksum;
    }

    private static ulong RunSwizzleCopy(int operations)
    {
        var source = CreateSwizzlePixels();
        var destination = new byte[source.Length];
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < operations; index++)
        {
            SKSwizzle.SwapRedBlue(
                (ReadOnlySpan<byte>)destination,
                (ReadOnlySpan<byte>)source,
                source.Length >> 2);
            checksum = Mix(
                checksum,
                (uint)destination[index & (destination.Length - 1)] |
                ((uint)destination[(index + 2) & (destination.Length - 1)] << 8));
        }

        return checksum;
    }

    private static byte[] CreateSwizzlePixels()
    {
        var pixels = new byte[4096];
        for (var index = 0; index < pixels.Length; index++)
            pixels[index] = (byte)(index * 37 + 11);
        return pixels;
    }

    private static byte[] CreateAvaloniaWriteableBitmapPixels()
    {
        var pixels = new byte[16 * 16 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var alpha = (byte)(64 + ((index >> 2) % 192));
            pixels[index] = (byte)(index * 17 + 3);
            pixels[index + 1] = (byte)(index * 29 + 7);
            pixels[index + 2] = (byte)(index * 43 + 11);
            pixels[index + 3] = alpha;
        }

        return pixels;
    }

    private static ulong Combine(float first, float second) =>
        ((ulong)(uint)BitConverter.SingleToInt32Bits(first) << 32) |
        (uint)BitConverter.SingleToInt32Bits(second);

    private static ulong Mix(ulong state, ulong value) =>
        (state ^ value) * 1099511628211UL;

    private static ulong MixPixels(ReadOnlySpan<byte> pixels)
    {
        ulong checksum = 1469598103934665603UL;
        for (var index = 0; index < pixels.Length; index++)
        {
            checksum = Mix(checksum, pixels[index]);
        }

        return checksum;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
            return 0;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) * 0.5
            : ordered[middle];
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
            return 0;
        var rank = Math.Clamp(
            (int)Math.Ceiling(percentile * ordered.Length) - 1,
            0,
            ordered.Length - 1);
        return ordered[rank];
    }

    private static IReadOnlyList<BenchmarkRun> ReadRuns(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(
                path => JsonSerializer.Deserialize<BenchmarkRun>(
                    File.ReadAllText(path),
                    JsonOptions) ??
                    throw new InvalidDataException($"Invalid benchmark run: {path}"))
            .ToArray();

    private static void WriteJson<T>(string path, T value)
    {
        EnsureDirectory(path);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + "\n");
    }

    private static void WriteMarkdown(
        string path,
        BenchmarkComparisonReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SkiaSharp benchmark comparison");
        builder.AppendLine();
        builder.AppendLine($"Runtime: `{report.Framework}` on `{report.OperatingSystem}` `{report.Architecture}`.");
        builder.AppendLine();
        builder.AppendLine("| Case | Native median ns/op | Native p95 | ProGPU median ns/op | ProGPU p95 | ProGPU/native | Native B/op | ProGPU B/op |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var item in report.Comparisons)
        {
            builder.AppendLine(
                $"| `{item.Name}` | {item.NativeMedianNanoseconds:F3} | " +
                $"{item.NativeP95Nanoseconds:F3} | {item.ProGpuMedianNanoseconds:F3} | " +
                $"{item.ProGpuP95Nanoseconds:F3} | {item.ProGpuToNativeRatio:F3} | " +
                $"{item.NativeMedianAllocatedBytes:F3} | " +
                $"{item.ProGpuMedianAllocatedBytes:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("Ratios below 1.0 favor ProGPU. Shared-machine timing is evidence, not a merge gate; semantic checksums must match exactly.");
        EnsureDirectory(path);
        File.WriteAllText(path, builder.ToString());
    }

    private static void EnsureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("Output path has no directory."));
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            Usage:
              ProGPU.SkiaSharp.Benchmarks run --output <json> [--backend Native|ProGPU] [--warmup <count>] [--samples <count>] [--commit <sha>] [--dirty <state>]
              ProGPU.SkiaSharp.Benchmarks compare --native <a.json;b.json> --progpu <a.json;b.json> --json <path> --markdown <path>
            """);
        return 2;
    }
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values;

    private CliOptions(Dictionary<string, string> values)
    {
        _values = values;
    }

    public static CliOptions Parse(ReadOnlySpan<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length ||
                !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid option near '{args[index]}'.");
            }
            values.Add(args[index][2..], args[index + 1]);
        }
        return new CliOptions(values);
    }

    public string Required(string name) =>
        Optional(name) ?? throw new ArgumentException($"Missing --{name}.");

    public string? Optional(string name) =>
        _values.TryGetValue(name, out var value) ? value : null;

    public int OptionalInt(string name, int fallback) =>
        Optional(name) is { } value
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : fallback;
}

internal sealed record BenchmarkCase(
    string Name,
    int Operations,
    Func<int, ulong> Body);

internal sealed record BenchmarkCaseResult(
    string Name,
    int OperationsPerSample,
    ulong Checksum,
    double[] NanosecondsPerOperation,
    double[] AllocatedBytesPerOperation,
    double MedianNanoseconds,
    double P95Nanoseconds,
    double MinimumNanoseconds,
    double MaximumNanoseconds,
    double MedianAllocatedBytes);

internal sealed record BenchmarkRun(
    int SchemaVersion,
    string Backend,
    string RepositoryCommit,
    string DirtyState,
    DateTimeOffset CapturedAtUtc,
    string Framework,
    string OperatingSystem,
    string Architecture,
    int ProcessorCount,
    string HarnessAssemblyVersion,
    string SkiaAssemblyVersion,
    int WarmupCount,
    int SampleCount,
    IReadOnlyList<BenchmarkCaseResult> Results);

internal sealed record BenchmarkComparison(
    string Name,
    int NativeSamples,
    int ProGpuSamples,
    ulong Checksum,
    double NativeMedianNanoseconds,
    double NativeP95Nanoseconds,
    double ProGpuMedianNanoseconds,
    double ProGpuP95Nanoseconds,
    double ProGpuToNativeRatio,
    double NativeMedianAllocatedBytes,
    double ProGpuMedianAllocatedBytes);

internal sealed record BenchmarkComparisonReport(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<string> NativeCommits,
    IReadOnlyList<string> ProGpuCommits,
    string Framework,
    string OperatingSystem,
    string Architecture,
    IReadOnlyList<BenchmarkComparison> Comparisons);
