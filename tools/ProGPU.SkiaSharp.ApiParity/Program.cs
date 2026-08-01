using System.Text.Json;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0)
                return Usage();

            var options = CliOptions.Parse(args.AsSpan(1));
            return args[0] switch
            {
                "acquire" => await AcquireAsync(options),
                "compare" => Compare(options),
                "self-test" => MetadataApiSurfaceSelfTests.Run(),
                _ => Usage()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> AcquireAsync(CliOptions options)
    {
        var baseline = ReadBaseline(options.Required("lock"));
        await NuGetMetadataAcquirer.AcquireAsync(
            baseline,
            options.Required("output"));
        return 0;
    }

    private static int Compare(CliOptions options)
    {
        var baseline = ReadBaseline(options.Required("lock"));
        var reference = MetadataApiSurface.Read(
            options.Required("reference"),
            baseline.NamespacePrefixes);
        var candidate = MetadataApiSurface.Read(
            options.Required("candidate"),
            baseline.NamespacePrefixes);
        var report = ApiParityReport.Create(baseline, reference, candidate);

        WriteText(
            options.Required("json"),
            JsonSerializer.Serialize(report, JsonOptions) + "\n");
        WriteText(options.Required("markdown"), report.ToMarkdown());

        Console.WriteLine(
            $"SkiaSharp API parity: reference={report.ReferenceEntryCount}, " +
            $"candidate={report.CandidateEntryCount}, " +
            $"matching={report.MatchingEntryCount}, " +
            $"missing={report.MissingEntries.Count}, " +
            $"extra={report.ExtraEntries.Count}.");

        var failed = false;
        if (report.MissingEntries.Count >
            baseline.RegressionBudget.MaximumMissingEntries)
        {
            Console.Error.WriteLine(
                $"Missing API entries increased above the budget: " +
                $"{report.MissingEntries.Count} > " +
                $"{baseline.RegressionBudget.MaximumMissingEntries}.");
            failed = true;
        }

        if (report.MatchingEntryCount <
            baseline.RegressionBudget.MinimumMatchingEntries)
        {
            Console.Error.WriteLine(
                $"Matching API entries fell below the budget: " +
                $"{report.MatchingEntryCount} < " +
                $"{baseline.RegressionBudget.MinimumMatchingEntries}.");
            failed = true;
        }

        return failed ? 1 : 0;
    }

    private static SkiaSharpApiBaseline ReadBaseline(string path)
    {
        var baseline = JsonSerializer.Deserialize<SkiaSharpApiBaseline>(
            File.ReadAllText(path),
            JsonOptions);
        return baseline ??
            throw new InvalidDataException($"Invalid baseline lock: {path}");
    }

    private static void WriteText(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("Output path has no directory."));
        File.WriteAllText(fullPath, contents);
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            Usage:
              ProGPU.SkiaSharp.ApiParity acquire --lock <json> --output <directory>
              ProGPU.SkiaSharp.ApiParity compare --lock <json> --reference <dll> --candidate <dll> --json <path> --markdown <path>
              ProGPU.SkiaSharp.ApiParity self-test
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
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) ||
                index + 1 >= args.Length)
            {
                throw new ArgumentException($"Invalid option near '{key}'.");
            }

            values.Add(key[2..], args[index + 1]);
        }

        return new CliOptions(values);
    }

    public string Required(string name) =>
        _values.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"Missing required option --{name}.");
}
