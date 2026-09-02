using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Svg;

return await CorpusApplication.RunAsync(args);

internal static class CorpusApplication
{
    private const int FixtureTimeoutMilliseconds = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            SvgDocument.SkipGdiPlusCapabilityCheck = true;

            return Task.FromResult(options.Command switch
            {
                Command.Quality => RunQuality(options),
                Command.QualityFixture => RunQualityFixture(options),
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
        var expectedExceptions = ExceptionInventory.Read(options.KnownExceptionsPath, options.Suite);
        var completeFixtureSet = FixtureCatalog.Enumerate(options.CorpusRoot, options.Suite).ToArray();
        ValidateFixtureCounts(options.Suite, completeFixtureSet);
        var fixtures = options.FixtureKey == null
            ? completeFixtureSet
            : completeFixtureSet.Where(fixture =>
                StringComparer.Ordinal.Equals(fixture.Key, options.FixtureKey)).ToArray();
        if (fixtures.Length == 0)
        {
            throw new ArgumentException($"Fixture was not found: {options.FixtureKey}", nameof(options.FixtureKey));
        }

        if (options.FixtureKey != null)
        {
            expectedDifferences.IntersectWith([options.FixtureKey]);
            expectedExceptions = expectedExceptions
                .Where(pair => StringComparer.Ordinal.Equals(pair.Key, options.FixtureKey))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        }

        var results = new List<FixtureResult>(fixtures.Length);
        for (var index = 0; index < fixtures.Length; index++)
        {
            var result = RenderAndCompareIsolated(
                fixtures[index],
                options.ArtifactsRoot,
                options.Threshold,
                index);
            results.Add(result);
            Console.WriteLine(result.ToConsoleLine());
        }

        var actualDifferences = results
            .Where(static result => result.Outcome == FixtureOutcome.PixelDifference)
            .Select(static result => result.Key)
            .ToHashSet(StringComparer.Ordinal);
        var actualExceptions = results
            .Where(static result => result.Outcome == FixtureOutcome.Exception)
            .ToDictionary(
                static result => result.Key,
                static result => result.ExceptionType ?? string.Empty,
                StringComparer.Ordinal);
        var added = actualDifferences.Except(expectedDifferences, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var resolved = expectedDifferences.Except(actualDifferences, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unexpectedExceptions = actualExceptions
            .Where(pair => !expectedExceptions.TryGetValue(pair.Key, out string? expectedType) ||
                !StringComparer.Ordinal.Equals(expectedType, pair.Value))
            .Select(pair => expectedExceptions.TryGetValue(pair.Key, out string? expectedType)
                ? $"{pair.Key}|expected:{expectedType}|actual:{pair.Value}"
                : $"{pair.Key}|unexpected:{pair.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var resolvedExceptions = expectedExceptions.Keys
            .Except(actualExceptions.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

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
            UnexpectedExceptions: unexpectedExceptions,
            ResolvedExceptions: resolvedExceptions,
            Results: results);

        WriteJson(Path.Combine(options.ArtifactsRoot, "quality-results.json"), report);
        DifferenceInventory.WriteCandidate(
            Path.Combine(options.ArtifactsRoot, "known-differences.candidate.txt"),
            results.Where(static result => result.Outcome == FixtureOutcome.PixelDifference));
        ExceptionInventory.WriteCandidate(
            Path.Combine(options.ArtifactsRoot, "known-exceptions.candidate.txt"),
            results.Where(static result => result.Outcome == FixtureOutcome.Exception));

        Console.WriteLine(
            FormattableString.Invariant(
                $"SVG System.Drawing quality: {report.Passed}/{report.Total} passed, {report.PixelDifferences} pixel differences, {report.Exceptions} exceptions."));

        if (added.Length == 0 &&
            resolved.Length == 0 &&
            unexpectedExceptions.Length == 0 &&
            resolvedExceptions.Length == 0)
        {
            return 0;
        }

        WriteDifferenceSummary("Added differences", added);
        WriteDifferenceSummary("Resolved differences", resolved);
        WriteDifferenceSummary("Unexpected exceptions", unexpectedExceptions);
        WriteDifferenceSummary("Resolved expected exceptions", resolvedExceptions);
        return 1;
    }

    private static int RunQualityFixture(Options options)
    {
        var fixture = new Fixture(
            options.WorkerSuite ?? throw new InvalidOperationException("Worker suite is missing."),
            options.WorkerFixture ?? throw new InvalidOperationException("Worker fixture is missing."),
            options.WorkerSvgPath ?? throw new InvalidOperationException("Worker SVG path is missing."),
            options.WorkerExpectedPath ?? throw new InvalidOperationException("Worker expected path is missing."),
            options.WorkerCompositeOnWhite);
        var resultPath = options.WorkerResultPath
            ?? throw new InvalidOperationException("Worker result path is missing.");
        WriteJson(resultPath, RenderAndCompare(fixture, options.ArtifactsRoot, options.Threshold));
        return 0;
    }

    private static FixtureResult RenderAndCompareIsolated(
        Fixture fixture,
        string artifactsRoot,
        double threshold,
        int index)
    {
        var workersRoot = Path.Combine(artifactsRoot, "workers");
        Directory.CreateDirectory(workersRoot);
        var resultPath = Path.Combine(workersRoot, $"{index:D4}.json");
        File.Delete(resultPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(typeof(CorpusApplication).Assembly.Location);
        AddArgument(startInfo, "quality-fixture");
        AddArgument(startInfo, "--corpus-root", ".");
        AddArgument(startInfo, "--artifacts", artifactsRoot);
        AddArgument(startInfo, "--threshold", threshold.ToString("R", CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--worker-suite", fixture.SuiteName);
        AddArgument(startInfo, "--worker-fixture", fixture.RelativeName);
        AddArgument(startInfo, "--worker-svg", fixture.SvgPath);
        AddArgument(startInfo, "--worker-expected", fixture.ExpectedPath);
        AddArgument(startInfo, "--worker-result", resultPath);
        AddArgument(
            startInfo,
            "--worker-composite-on-white",
            fixture.CompositeOnWhite ? "true" : "false");

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the isolated SVG fixture process.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(FixtureTimeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            stopwatch.Stop();
            return FixtureResult.Failed(
                fixture,
                new TimeoutException(
                    $"The isolated SVG fixture exceeded {FixtureTimeoutMilliseconds / 1000} seconds."),
                stopwatch.Elapsed,
                allocated: 0);
        }

        Task.WaitAll(standardOutput, standardError);
        stopwatch.Stop();
        if (process.ExitCode == 0 && File.Exists(resultPath))
        {
            var result = JsonSerializer.Deserialize<FixtureResult>(File.ReadAllText(resultPath), JsonOptions)
                ?? throw new InvalidDataException($"The isolated result is empty: {resultPath}");
            File.Delete(resultPath);
            return result;
        }

        string diagnostic = LastDiagnosticLine(standardError.Result, standardOutput.Result);
        return FixtureResult.Failed(
            fixture,
            new InvalidOperationException(
                $"Isolated fixture process exited with code {process.ExitCode}. {diagnostic}".Trim()),
            stopwatch.Elapsed,
            allocated: 0);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string? value = null)
    {
        startInfo.ArgumentList.Add(name);
        if (value is not null)
        {
            startInfo.ArgumentList.Add(value);
        }
    }

    private static string LastDiagnosticLine(params string[] streams)
        => streams
            .SelectMany(static stream => stream.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(static line => line.Trim())
            .LastOrDefault(static line => line.Length > 0)
            ?? "No child-process diagnostic was produced.";

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
            var error = ImageDifference.Calculate(actual, expected, fixture.CompositeOnWhite);
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
            if (File.Exists(actualPath) && new FileInfo(actualPath).Length == 0)
            {
                File.Delete(actualPath);
            }
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

        if (suite is Suite.All or Suite.W3c && w3cCount != 525)
        {
            throw new InvalidOperationException($"Expected 525 W3C fixtures, found {w3cCount}.");
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
        JsonSerializer.Serialize(stream, value, JsonOptions);
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
                yield return new Fixture("resvg", relative, svgPath, expectedPath, CompositeOnWhite: false);
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
                bool compositeOnWhite = File.Exists(chromePath) ||
                    relative is "struct-dom-19-f" or "struct-dom-20-f";
                yield return new Fixture("w3c", relative, svgPath, expectedPath, compositeOnWhite);
            }
        }
    }
}

internal static class ImageDifference
{
    public static double Calculate(
        DecodedImage actual,
        DecodedImage expected,
        bool compositeOnWhite)
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
                if (compositeOnWhite)
                {
                    red += actualAlpha - expectedAlpha;
                    green += actualAlpha - expectedAlpha;
                    blue += actualAlpha - expectedAlpha;
                }
                var alpha = expectedAlpha - actualAlpha;
                squaredError += red * red + green * green + blue * blue +
                    (compositeOnWhite ? 0d : alpha * alpha);
            }
        }

        // Svg.Skia's W3C helper counts each composited RGB channel in its
        // sample quantity and then normalizes by the three channels again.
        // Preserve that established threshold contract for the shared corpus.
        int channelNormalization = compositeOnWhite ? 9 : 4;
        return Math.Sqrt(squaredError / (actual.Width * actual.Height * channelNormalization));
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

internal static class ExceptionInventory
{
    public static Dictionary<string, string> Read(string path, Suite suite)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Known-exception inventory was not found.", path);
        }

        return File.ReadLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .Select(static line => line.Split('|', 4))
            .Select(static parts => parts.Length >= 3
                ? new KeyValuePair<string, string>($"{parts[0]}|{parts[1]}", parts[2])
                : throw new InvalidDataException($"Invalid known-exception row: {parts[0]}"))
            .Where(pair => suite == Suite.All ||
                suite == Suite.Resvg && pair.Key.StartsWith("resvg|", StringComparison.Ordinal) ||
                suite == Suite.W3c && pair.Key.StartsWith("w3c|", StringComparison.Ordinal))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    }

    public static void WriteCandidate(string path, IEnumerable<FixtureResult> results)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("# suite|fixture|exception-type|message");
        foreach (var result in results.OrderBy(static result => result.Key, StringComparer.Ordinal))
        {
            string message = (result.ExceptionMessage ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            writer.WriteLine($"{result.Key}|{result.ExceptionType}|{message}");
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
    string KnownExceptionsPath,
    string BenchmarkFixturesPath,
    string? FixtureKey,
    Suite Suite,
    double Threshold,
    int Iterations,
    string? WorkerSuite,
    string? WorkerFixture,
    string? WorkerSvgPath,
    string? WorkerExpectedPath,
    string? WorkerResultPath,
    bool WorkerCompositeOnWhite)
{
    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            throw new ArgumentException(
                "Usage: quality|performance --corpus-root PATH --artifacts PATH " +
                "[--known-differences PATH] [--known-exceptions PATH] " +
                "[--benchmark-fixtures PATH] " +
                "[--fixture suite|path] [--suite all|resvg|w3c] " +
                "[--threshold 0.12] [--iterations 7]");
        }

        var command = args[0] switch
        {
            "quality" => Command.Quality,
            "quality-fixture" => Command.QualityFixture,
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
        var knownExceptions = values.GetValueOrDefault("--known-exceptions", Path.Combine("eng", "system-drawing-svg-known-exceptions.txt"));
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

        return new Options(
            command,
            corpusRoot,
            artifactsRoot,
            knownDifferences,
            knownExceptions,
            benchmarkFixtures,
            values.GetValueOrDefault("--fixture"),
            suite,
            threshold,
            iterations,
            values.GetValueOrDefault("--worker-suite"),
            values.GetValueOrDefault("--worker-fixture"),
            values.GetValueOrDefault("--worker-svg"),
            values.GetValueOrDefault("--worker-expected"),
            values.GetValueOrDefault("--worker-result"),
            bool.Parse(values.GetValueOrDefault("--worker-composite-on-white", "false")));
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option: {name}");
}

internal sealed record Fixture(
    string SuiteName,
    string RelativeName,
    string SvgPath,
    string ExpectedPath,
    bool CompositeOnWhite)
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
    string? ExceptionMessage,
    string? ExceptionDetail)
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
            exception?.Message,
            exception?.ToString());

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
    IReadOnlyList<string> UnexpectedExceptions,
    IReadOnlyList<string> ResolvedExceptions,
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
    QualityFixture,
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
