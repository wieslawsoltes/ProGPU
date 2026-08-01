using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SkiaSharp;

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

        var warmupCount = options.OptionalInt("warmup", 8);
        var sampleCount = options.OptionalInt("samples", 24);
        if (warmupCount < 1 || sampleCount < 3)
            throw new ArgumentOutOfRangeException(nameof(options));

        var cases = new[]
        {
            new BenchmarkCase("point-arithmetic", 200_000, RunPointArithmetic),
            new BenchmarkCase("matrix-map-point", 100_000, RunMatrixMapPoint),
            new BenchmarkCase("pmcolor-premultiply", 65_536, RunPremultiplyColor),
            new BenchmarkCase("pmcolor-unpremultiply", 65_536, RunUnpremultiplyColor),
            new BenchmarkCase("pmcolor-array-premultiply", 1_000, RunPremultiplyColorArrays),
            new BenchmarkCase("pmcolor-array-unpremultiply", 1_000, RunUnpremultiplyColorArrays),
            new BenchmarkCase("four-byte-tag-value", 100_000, RunFourByteTagValue),
            new BenchmarkCase("four-byte-tag-format", 10_000, RunFourByteTagFormat),
            new BenchmarkCase("swizzle-in-place-4k", 10_000, RunSwizzleInPlace),
            new BenchmarkCase("swizzle-copy-4k", 10_000, RunSwizzleCopy),
            new BenchmarkCase("path-build-bounds", 1_000, RunPathBuildBounds)
        };
        var results = new List<BenchmarkCaseResult>(cases.Length);
        foreach (var benchmark in cases)
        {
            for (var index = 0; index < warmupCount; index++)
                Volatile.Write(ref s_sink, unchecked((long)benchmark.Body(benchmark.Operations)));

            var elapsed = new double[sampleCount];
            var allocated = new double[sampleCount];
            ulong checksum = 0;
            for (var index = 0; index < sampleCount; index++)
            {
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                checksum = benchmark.Body(benchmark.Operations);
                var finished = Stopwatch.GetTimestamp();
                var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
                Volatile.Write(ref s_sink, unchecked((long)checksum));

                elapsed[index] =
                    (finished - started) * 1_000_000_000d /
                    Stopwatch.Frequency /
                    benchmark.Operations;
                allocated[index] =
                    (allocatedAfter - allocatedBefore) /
                    (double)benchmark.Operations;
            }

            results.Add(
                new BenchmarkCaseResult(
                    benchmark.Name,
                    benchmark.Operations,
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

    private static ulong Combine(float first, float second) =>
        ((ulong)(uint)BitConverter.SingleToInt32Bits(first) << 32) |
        (uint)BitConverter.SingleToInt32Bits(second);

    private static ulong Mix(ulong state, ulong value) =>
        (state ^ value) * 1099511628211UL;

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
