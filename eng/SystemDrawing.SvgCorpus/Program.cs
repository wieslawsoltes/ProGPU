using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using Svg;

return await CorpusApplication.RunAsync(args);

internal static class CorpusApplication
{
    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            SvgDocument.SkipGdiPlusCapabilityCheck = true;

            return Task.FromResult(options.Command switch
            {
                Command.Quality => RunQuality(options),
                Command.Performance => RunPerformance(options),
                _ => throw new ArgumentOutOfRangeException()
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return Task.FromResult(2);
        }
    }

    private static int RunQuality(Options options)
    {
        Directory.CreateDirectory(options.ArtifactsRoot);

        var expectedDifferences = DifferenceInventory.Read(options.KnownDifferencesPath, options.Suite);
        var fixtures = FixtureCatalog.Enumerate(options.CorpusRoot, options.Suite).ToArray();
        ValidateFixtureCounts(options.Suite, fixtures);

        var results = new List<FixtureResult>(fixtures.Length);
        foreach (var fixture in fixtures)
        {
            var result = RenderAndCompare(fixture, options.ArtifactsRoot, options.Threshold);
            results.Add(result);
            Console.WriteLine(result.ToConsoleLine());
        }

        var actualDifferences = results
            .Where(static result => result.Outcome != FixtureOutcome.Passed)
            .Select(static result => result.Key)
            .ToHashSet(StringComparer.Ordinal);
        var added = actualDifferences.Except(expectedDifferences, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var resolved = expectedDifferences.Except(actualDifferences, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var report = new QualityReport(
            Commit: ReadCommit(),
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Threshold: options.Threshold,
            Total: results.Count,
            Passed: results.Count(static result => result.Outcome == FixtureOutcome.Passed),
            PixelDifferences: results.Count(static result => result.Outcome == FixtureOutcome.PixelDifference),
            Exceptions: results.Count(static result => result.Outcome == FixtureOutcome.Exception),
            AddedDifferences: added,
            ResolvedDifferences: resolved,
            Results: results);

        WriteJson(Path.Combine(options.ArtifactsRoot, "quality-results.json"), report);
        DifferenceInventory.WriteCandidate(
            Path.Combine(options.ArtifactsRoot, "known-differences.candidate.txt"),
            results.Where(static result => result.Outcome != FixtureOutcome.Passed));

        Console.WriteLine(
            FormattableString.Invariant(
                $"SVG System.Drawing quality: {report.Passed}/{report.Total} passed, {report.PixelDifferences} pixel differences, {report.Exceptions} exceptions."));

        if (added.Length == 0 && resolved.Length == 0)
        {
            return 0;
        }

        WriteDifferenceSummary("Added differences", added);
        WriteDifferenceSummary("Resolved differences", resolved);
        return 1;
    }

    private static int RunPerformance(Options options)
    {
        Directory.CreateDirectory(options.ArtifactsRoot);
        var requested = BenchmarkFixtureList.Read(options.BenchmarkFixturesPath);
        var catalog = FixtureCatalog.Enumerate(options.CorpusRoot, Suite.All)
            .ToDictionary(static fixture => fixture.Key, StringComparer.Ordinal);
        var fixtures = requested.Select(key => catalog.TryGetValue(key, out var fixture)
                ? fixture
                : throw new InvalidOperationException($"Benchmark fixture is missing: {key}"))
            .ToArray();

        foreach (var fixture in fixtures)
        {
            var expected = PngDecoder.Load(fixture.ExpectedPath);
            var document = SvgDocument.Open<SvgDocument>(fixture.SvgPath);
            using var bitmap = document.Draw(expected.Width, expected.Height)
                ?? throw new InvalidOperationException($"SVG rendered an empty bitmap: {fixture.Key}");
            Consume(bitmap);
        }

        var samples = new List<PerformanceSample>(options.Iterations);
        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            ulong checksum = 14695981039346656037UL;

            foreach (var fixture in fixtures)
            {
                var expected = PngDecoder.Load(fixture.ExpectedPath);
                var document = SvgDocument.Open<SvgDocument>(fixture.SvgPath);
                using var bitmap = document.Draw(expected.Width, expected.Height)
                    ?? throw new InvalidOperationException($"SVG rendered an empty bitmap: {fixture.Key}");
                checksum = Mix(checksum, bitmap.Width);
                checksum = Mix(checksum, bitmap.Height);
                checksum = Mix(checksum, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).ToArgb());
            }

            stopwatch.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            samples.Add(new PerformanceSample(iteration, stopwatch.Elapsed.TotalMilliseconds, allocated, checksum));
            Console.WriteLine(
                FormattableString.Invariant(
                    $"perf iteration={iteration} elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F3} allocated_bytes={allocated} checksum={checksum:x16}"));
        }

        var elapsed = samples.Select(static sample => sample.ElapsedMilliseconds).Order().ToArray();
        var allocations = samples.Select(static sample => sample.AllocatedBytes).Order().ToArray();
        var report = new PerformanceReport(
            Commit: ReadCommit(),
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            FixtureCount: fixtures.Length,
            Iterations: options.Iterations,
            MedianElapsedMilliseconds: Median(elapsed),
            MedianAllocatedBytes: Median(allocations),
            Samples: samples);
        WriteJson(Path.Combine(options.ArtifactsRoot, "performance-results.json"), report);
        Console.WriteLine(
            FormattableString.Invariant(
                $"SVG System.Drawing performance: {fixtures.Length} fixtures, median {report.MedianElapsedMilliseconds:F3} ms, median {report.MedianAllocatedBytes:F0} allocated bytes."));
        return 0;
    }

    private static FixtureResult RenderAndCompare(Fixture fixture, string artifactsRoot, double threshold)
    {
        var stopwatch = Stopwatch.StartNew();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var actualPath = Path.Combine(artifactsRoot, "actual", fixture.SuiteName, fixture.RelativeName + ".png");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
            var expected = PngDecoder.Load(fixture.ExpectedPath);
            var document = SvgDocument.Open<SvgDocument>(fixture.SvgPath);
            using var bitmap = document.Draw(expected.Width, expected.Height)
                ?? throw new InvalidOperationException("SVG rendered an empty bitmap.");
            bitmap.Save(actualPath, ImageFormat.Png);

            var actual = PngDecoder.Load(actualPath);
            var error = ImageDifference.Calculate(actual, expected);
            stopwatch.Stop();

            if (error <= threshold)
            {
                File.Delete(actualPath);
                return FixtureResult.Passed(fixture, error, stopwatch.Elapsed, AllocatedSince(allocatedBefore));
            }

            return FixtureResult.Different(fixture, error, stopwatch.Elapsed, AllocatedSince(allocatedBefore));
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return FixtureResult.Failed(fixture, exception, stopwatch.Elapsed, AllocatedSince(allocatedBefore));
        }
    }

    private static long AllocatedSince(long before) => GC.GetTotalAllocatedBytes(precise: true) - before;

    private static void ValidateFixtureCounts(Suite suite, IReadOnlyCollection<Fixture> fixtures)
    {
        var resvgCount = fixtures.Count(static fixture => fixture.SuiteName == "resvg");
        var w3cCount = fixtures.Count(static fixture => fixture.SuiteName == "w3c");
        if (suite is Suite.All or Suite.Resvg && resvgCount != 1_730)
        {
            throw new InvalidOperationException($"Expected 1730 resvg fixtures, found {resvgCount}.");
        }

        if (suite is Suite.All or Suite.W3c && w3cCount != 533)
        {
            throw new InvalidOperationException($"Expected 533 W3C fixtures, found {w3cCount}.");
        }
    }

    private static void WriteDifferenceSummary(string title, IReadOnlyCollection<string> differences)
    {
        if (differences.Count == 0)
        {
            return;
        }

        Console.Error.WriteLine($"{title} ({differences.Count}):");
        foreach (var difference in differences)
        {
            Console.Error.WriteLine($"  {difference}");
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ReadCommit()
        => Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local";

    private static void Consume(System.Drawing.Bitmap bitmap)
        => GC.KeepAlive(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));

    private static ulong Mix(ulong value, int component)
    {
        value ^= unchecked((uint)component);
        return value * 1099511628211UL;
    }

    private static double Median(IReadOnlyList<double> sorted)
        => sorted.Count % 2 == 0
            ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2
            : sorted[sorted.Count / 2];

    private static double Median(IReadOnlyList<long> sorted)
        => sorted.Count % 2 == 0
            ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2d
            : sorted[sorted.Count / 2];
}

internal static class FixtureCatalog
{
    public static IEnumerable<Fixture> Enumerate(string corpusRoot, Suite suite)
    {
        if (suite is Suite.All or Suite.Resvg)
        {
            foreach (var fixture in EnumerateResvg(corpusRoot))
            {
                yield return fixture;
            }
        }

        if (suite is Suite.All or Suite.W3c)
        {
            foreach (var fixture in EnumerateW3c(corpusRoot))
            {
                yield return fixture;
            }
        }
    }

    private static IEnumerable<Fixture> EnumerateResvg(string corpusRoot)
    {
        var root = Path.Combine(corpusRoot, "externals", "resvg", "crates", "resvg", "tests");
        foreach (var directoryName in new[] { "tests", "extra" })
        {
            var directory = Path.Combine(root, directoryName);
            foreach (var svgPath in Directory.EnumerateFiles(directory, "*.svg", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                var relative = Path.ChangeExtension(Path.GetRelativePath(directory, svgPath), null)!;
                relative = $"{directoryName}/{relative}".Replace(Path.DirectorySeparatorChar, '/');
                var localRelative = relative.Replace('/', Path.DirectorySeparatorChar) + ".png";
                var officialPath = Path.Combine(root, localRelative);
                var chromePath = Path.Combine(
                    corpusRoot,
                    "tests",
                    "Svg.Skia.UnitTests",
                    "ChromeReference",
                    "resvg",
                    localRelative);
                var expectedPath = File.Exists(chromePath) ? chromePath : officialPath;
                yield return new Fixture("resvg", relative, svgPath, expectedPath);
            }
        }
    }

    private static IEnumerable<Fixture> EnumerateW3c(string corpusRoot)
    {
        var root = Path.Combine(corpusRoot, "externals", "W3C_SVG_11_TestSuite", "W3C_SVG_11_TestSuite");
        var svgRoot = Path.Combine(root, "svg");
        foreach (var svgPath in Directory.EnumerateFiles(svgRoot, "*.svg", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetFileNameWithoutExtension(svgPath);
            var chromePath = Path.Combine(
                corpusRoot,
                "tests",
                "Svg.Skia.UnitTests",
                "ChromeReference",
                "W3C",
                relative + ".png");
            var officialPath = Path.Combine(root, "png", relative + ".png");
            var expectedPath = File.Exists(chromePath) ? chromePath : officialPath;
            if (File.Exists(expectedPath))
            {
                yield return new Fixture("w3c", relative, svgPath, expectedPath);
            }
        }
    }
}

internal static class ImageDifference
{
    public static double Calculate(DecodedImage actual, DecodedImage expected)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height)
        {
            return double.PositiveInfinity;
        }

        const double scale = 1d / 255d;
        double squaredError = 0;
        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var offset = (y * actual.Width + x) * 4;
                var expectedAlpha = expected.Data[offset + 3] * scale;
                var actualAlpha = actual.Data[offset + 3] * scale;
                var red = scale * (expectedAlpha * expected.Data[offset] - actualAlpha * actual.Data[offset]);
                var green = scale * (expectedAlpha * expected.Data[offset + 1] - actualAlpha * actual.Data[offset + 1]);
                var blue = scale * (expectedAlpha * expected.Data[offset + 2] - actualAlpha * actual.Data[offset + 2]);
                var alpha = expectedAlpha - actualAlpha;
                squaredError += red * red + green * green + blue * blue + alpha * alpha;
            }
        }

        return Math.Sqrt(squaredError / (actual.Width * actual.Height * 4d));
    }
}

internal static class PngDecoder
{
    public static DecodedImage Load(string path)
    {
        var result = StbImageSharp.ImageResult.FromMemory(
            File.ReadAllBytes(path),
            StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        return new DecodedImage(result.Width, result.Height, result.Data);
    }
}

internal static class DifferenceInventory
{
    public static HashSet<string> Read(string path, Suite suite)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Known-difference inventory was not found.", path);
        }

        return File.ReadLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .Select(static line => line.Split('|', 3))
            .Select(static parts => parts.Length >= 2
                ? $"{parts[0]}|{parts[1]}"
                : throw new InvalidDataException($"Invalid known-difference row: {parts[0]}"))
            .Where(key => suite == Suite.All ||
                suite == Suite.Resvg && key.StartsWith("resvg|", StringComparison.Ordinal) ||
                suite == Suite.W3c && key.StartsWith("w3c|", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static void WriteCandidate(string path, IEnumerable<FixtureResult> results)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("# suite|fixture|observed-error-or-exception");
        foreach (var result in results.OrderBy(static result => result.Key, StringComparer.Ordinal))
        {
            var observation = result.Error?.ToString("G17", CultureInfo.InvariantCulture)
                ?? $"exception:{result.ExceptionType}";
            writer.WriteLine($"{result.Key}|{observation}");
        }
    }
}

internal static class BenchmarkFixtureList
{
    public static string[] Read(string path)
        => File.ReadLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
}

internal sealed record Options(
    Command Command,
    string CorpusRoot,
    string ArtifactsRoot,
    string KnownDifferencesPath,
    string BenchmarkFixturesPath,
    Suite Suite,
    double Threshold,
    int Iterations)
{
    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            throw new ArgumentException(
                "Usage: quality|performance --corpus-root PATH --artifacts PATH " +
                "[--known-differences PATH] [--benchmark-fixtures PATH] " +
                "[--suite all|resvg|w3c] [--threshold 0.12] [--iterations 7]");
        }

        var command = args[0] switch
        {
            "quality" => Command.Quality,
            "performance" => Command.Performance,
            _ => throw new ArgumentException($"Unknown command: {args[0]}")
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Expected --name value at argument {index}.");
            }

            values.Add(args[index], args[index + 1]);
        }

        var corpusRoot = Required(values, "--corpus-root");
        var artifactsRoot = Required(values, "--artifacts");
        var knownDifferences = values.GetValueOrDefault("--known-differences", Path.Combine("eng", "system-drawing-svg-known-differences.txt"));
        var benchmarkFixtures = values.GetValueOrDefault("--benchmark-fixtures", Path.Combine("eng", "system-drawing-svg-benchmark-fixtures.txt"));
        var suite = values.GetValueOrDefault("--suite", "all") switch
        {
            "all" => Suite.All,
            "resvg" => Suite.Resvg,
            "w3c" => Suite.W3c,
            var value => throw new ArgumentException($"Unknown suite: {value}")
        };
        var threshold = double.Parse(values.GetValueOrDefault("--threshold", "0.12"), CultureInfo.InvariantCulture);
        var iterations = int.Parse(values.GetValueOrDefault("--iterations", "7"), CultureInfo.InvariantCulture);
        if (threshold is < 0 or > 1 || iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(args));
        }

        return new Options(command, corpusRoot, artifactsRoot, knownDifferences, benchmarkFixtures, suite, threshold, iterations);
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option: {name}");
}

internal sealed record Fixture(string SuiteName, string RelativeName, string SvgPath, string ExpectedPath)
{
    public string Key => $"{SuiteName}|{RelativeName}";
}

internal sealed record DecodedImage(int Width, int Height, byte[] Data);

internal sealed record FixtureResult(
    string Suite,
    string Fixture,
    FixtureOutcome Outcome,
    double? Error,
    double ElapsedMilliseconds,
    long AllocatedBytes,
    string? ExceptionType,
    string? ExceptionMessage)
{
    public string Key => $"{Suite}|{Fixture}";

    public static FixtureResult Passed(Fixture fixture, double error, TimeSpan elapsed, long allocated)
        => Create(fixture, FixtureOutcome.Passed, error, elapsed, allocated, null);

    public static FixtureResult Different(Fixture fixture, double error, TimeSpan elapsed, long allocated)
        => Create(fixture, FixtureOutcome.PixelDifference, error, elapsed, allocated, null);

    public static FixtureResult Failed(Fixture fixture, Exception exception, TimeSpan elapsed, long allocated)
        => Create(fixture, FixtureOutcome.Exception, null, elapsed, allocated, exception);

    private static FixtureResult Create(
        Fixture fixture,
        FixtureOutcome outcome,
        double? error,
        TimeSpan elapsed,
        long allocated,
        Exception? exception)
        => new(
            fixture.SuiteName,
            fixture.RelativeName,
            outcome,
            error,
            elapsed.TotalMilliseconds,
            allocated,
            exception?.GetType().FullName,
            exception?.Message);

    public string ToConsoleLine()
    {
        var error = Error.HasValue ? Error.Value.ToString("F6", CultureInfo.InvariantCulture) : "-";
        var exception = ExceptionType is null ? string.Empty : $" exception={ExceptionType}";
        return FormattableString.Invariant(
            $"{Outcome,-15} {Key} error={error} elapsed_ms={ElapsedMilliseconds:F3} allocated_bytes={AllocatedBytes}{exception}");
    }
}

internal sealed record QualityReport(
    string Commit,
    DateTimeOffset GeneratedAtUtc,
    double Threshold,
    int Total,
    int Passed,
    int PixelDifferences,
    int Exceptions,
    IReadOnlyList<string> AddedDifferences,
    IReadOnlyList<string> ResolvedDifferences,
    IReadOnlyList<FixtureResult> Results);

internal sealed record PerformanceSample(int Iteration, double ElapsedMilliseconds, long AllocatedBytes, ulong Checksum);

internal sealed record PerformanceReport(
    string Commit,
    DateTimeOffset GeneratedAtUtc,
    int FixtureCount,
    int Iterations,
    double MedianElapsedMilliseconds,
    double MedianAllocatedBytes,
    IReadOnlyList<PerformanceSample> Samples);

internal enum Command
{
    Quality,
    Performance
}

internal enum Suite
{
    All,
    Resvg,
    W3c
}

internal enum FixtureOutcome
{
    Passed,
    PixelDifference,
    Exception
}
