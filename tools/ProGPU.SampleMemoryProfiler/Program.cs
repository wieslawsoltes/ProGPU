using System.Globalization;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

return args.Length == 0 ? Usage() : args[0] switch
{
    "analyze" => Analyze(args),
    "summarize" => Summarize(args),
    "inspect" => Inspect(args),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine(
        "Usage:\n" +
        "  ProGPU.SampleMemoryProfiler analyze <trace.nettrace> <benchmark.log> <output.json>\n" +
        "  ProGPU.SampleMemoryProfiler summarize <results-directory> <output.json> <output.md>");
    return 2;
}

static int Inspect(string[] args)
{
    if (args.Length != 2) return Usage();
    string tracePath = Path.GetFullPath(args[1]);
    string etlxPath = Path.ChangeExtension(tracePath, ".inspect.etlx");
    try
    {
        TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath);
        using var trace = new TraceLog(etlxPath);
        _ = trace.Clr;
        foreach (TraceEvent marker in trace.Events.Where(e => e.ProviderName == "ProGPU-SampleBenchmark"))
            Console.WriteLine($"MARKER index={marker.EventIndex} name={marker.EventName} id={marker.ID} time={marker.TimeStampRelativeMSec:R} page={marker.PayloadValue(0)}");
        foreach (var group in trace.Events
                     .Where(e => e.ProviderName == "ProGPU-SampleBenchmark" || e.ProviderName == "Microsoft-Windows-DotNETRuntime")
                     .Select(e => (e.ProviderName, e.EventName, e.ID, e.TimeStampRelativeMSec, Payload: string.Join(',', e.PayloadNames), Type: e.GetType().Name))
                     .GroupBy(e => (e.ProviderName, e.EventName, e.ID, e.Payload, e.Type))
                     .OrderByDescending(g => g.Count()))
        {
            var first = group.First();
            Console.WriteLine($"{group.Count(),8} {group.Key.ProviderName} {group.Key.EventName} id={group.Key.ID} type={group.Key.Type} first={first.TimeStampRelativeMSec:F3} payload={group.Key.Payload}");
        }
    }
    finally
    {
        if (File.Exists(etlxPath)) File.Delete(etlxPath);
    }
    return 0;
}

static int Analyze(string[] args)
{
    if (args.Length != 4)
        return Usage();

    string tracePath = Path.GetFullPath(args[1]);
    string logPath = Path.GetFullPath(args[2]);
    string outputPath = Path.GetFullPath(args[3]);
    string etlxPath = Path.ChangeExtension(tracePath, ".analysis.etlx");

    var benchmark = ParseBenchmarkResult(logPath);
    var phaseTotals = new Dictionary<string, AllocationAccumulator>(StringComparer.Ordinal)
    {
        ["startup"] = new(),
        ["warmup"] = new(),
        ["measurement"] = new()
    };
    var cpuMethods = new Dictionary<string, long>(StringComparer.Ordinal);
    double? workloadStarted = null;
    double? measurementStarted = null;
    double? measurementStopped = null;
    var gcStarts = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["startup"] = 0,
        ["warmup"] = 0,
        ["measurement"] = 0
    };

    try
    {
        TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath);
        using var trace = new TraceLog(etlxPath);
        _ = trace.Clr;
        foreach (TraceEvent marker in trace.Events.Where(e => e.ProviderName == "ProGPU-SampleBenchmark"))
        {
            if (marker.EventName == "WorkloadStarted" && !workloadStarted.HasValue)
                workloadStarted = marker.TimeStampRelativeMSec;
            else if (marker.EventName == "MeasurementStarted" && !measurementStarted.HasValue)
                measurementStarted = marker.TimeStampRelativeMSec;
            else if (marker.EventName == "MeasurementStopped" && !measurementStopped.HasValue)
                measurementStopped = marker.TimeStampRelativeMSec;
        }

        foreach (TraceEvent traceEvent in trace.Events)
        {
            if (traceEvent.ProviderName == "ProGPU-SampleBenchmark")
                continue;

            string phase = PhaseAt(traceEvent.TimeStampRelativeMSec, workloadStarted, measurementStarted, measurementStopped);
            if (phase == "after")
                continue;

            if (traceEvent.ProviderName == "Microsoft-Windows-DotNETRuntime" &&
                traceEvent.EventName == "GC/Start")
            {
                gcStarts[phase]++;
            }

            if (traceEvent.ID == (TraceEventID)303 && TryReadRandomizedAllocation(traceEvent, out RandomizedAllocation allocation))
            {
                string stack = FormatStack(traceEvent.CallStack(), 12);
                phaseTotals[phase].Add(allocation.TypeName, stack, allocation.ObjectSize, allocation.EstimatedBytes);
            }
            else if (phase == "measurement" &&
                     traceEvent.ProviderName == "Microsoft-DotNETCore-SampleProfiler" &&
                     traceEvent.EventName == "Thread/Sample")
            {
                string leaf = FirstApplicationFrame(traceEvent.CallStack());
                cpuMethods[leaf] = cpuMethods.GetValueOrDefault(leaf) + 1;
            }
        }
    }
    finally
    {
        if (File.Exists(etlxPath))
            File.Delete(etlxPath);
    }

    var root = new JsonObject
    {
        ["schemaVersion"] = 2,
        ["page"] = benchmark.Page,
        ["trace"] = Path.GetFileName(tracePath),
        ["markers"] = new JsonObject
        {
            ["workloadStartedMs"] = workloadStarted,
            ["measurementStartedMs"] = measurementStarted,
            ["measurementStoppedMs"] = measurementStopped
        },
        ["metrics"] = benchmark.Metrics,
        ["runtimeGcStarts"] = new JsonObject
        {
            ["startup"] = gcStarts["startup"],
            ["warmup"] = gcStarts["warmup"],
            ["measurement"] = gcStarts["measurement"]
        },
        ["allocationPhases"] = new JsonObject
        {
            ["startup"] = phaseTotals["startup"].ToJson(),
            ["warmup"] = phaseTotals["warmup"].ToJson(),
            ["measurement"] = phaseTotals["measurement"].ToJson()
        },
        ["measurementCpuSamples"] = Ranked(cpuMethods, 30, "method")
    };

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, root.ToJsonString(JsonOptions()));
    Console.WriteLine($"Analyzed {benchmark.Page}: estimated measurement allocations={phaseTotals["measurement"].EstimatedBytes:N0} bytes");
    return measurementStarted.HasValue && measurementStopped.HasValue ? 0 : 3;
}

static int Summarize(string[] args)
{
    if (args.Length != 4)
        return Usage();

    string resultsDirectory = Path.GetFullPath(args[1]);
    string jsonPath = Path.GetFullPath(args[2]);
    string markdownPath = Path.GetFullPath(args[3]);
    var pages = Directory.EnumerateFiles(resultsDirectory, "*.json", SearchOption.TopDirectoryOnly)
        .Where(path => !Path.GetFullPath(path).Equals(jsonPath, StringComparison.Ordinal))
        .Select(path => JsonNode.Parse(File.ReadAllText(path))!.AsObject())
        .Where(node => node["page"] is not null && node["metrics"] is JsonObject)
        .OrderByDescending(node => MetricLong(node, "processPhysicalFootprintBytes", "processWorkingSetBytes"))
        .ThenBy(node => node["page"]!.GetValue<string>(), StringComparer.Ordinal)
        .ToArray();

    var summary = new JsonObject
    {
        ["schemaVersion"] = 1,
        ["generatedUtc"] = DateTimeOffset.UtcNow,
        ["pageCount"] = pages.Length,
        ["pages"] = new JsonArray(pages.Select(page => (JsonNode)page.DeepClone()).ToArray())
    };
    Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
    File.WriteAllText(jsonPath, summary.ToJsonString(JsonOptions()));
    File.WriteAllText(markdownPath, BuildMarkdown(pages));
    Console.WriteLine($"Summarized {pages.Length} pages into {markdownPath}");
    return pages.Length == 0 ? 4 : 0;
}

static string BuildMarkdown(IReadOnlyList<JsonObject> pages)
{
    var builder = new StringBuilder();
    builder.AppendLine("# ProGPU per-page Release memory profile");
    builder.AppendLine();
    builder.AppendLine("Each row is a fresh desktop process. Retained managed values are measured after a blocking compacting GC; allocation estimates are probability-weighted from .NET runtime EventPipe randomized-allocation events between the benchmark markers.");
    builder.AppendLine();
    builder.AppendLine("| Page | Physical footprint | Working set | Managed retained | GC committed | Fragmented | GPU textures/staging | Alloc/frame | Estimated allocation | FPS | Compile ms | Upload ms |");
    builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var page in pages)
    {
        string name = page["page"]!.GetValue<string>().Replace("|", "\\|");
        long physical = MetricLong(page, "processPhysicalFootprintBytes", "processWorkingSetBytes");
        long working = MetricLong(page, "processWorkingSetBytes");
        long managed = MetricLong(page, "managedHeapBytes");
        long committed = MetricLong(page, "gcCommittedBytes");
        long fragmented = MetricLong(page, "managedFragmentedBytes");
        long gpu = MetricLong(page, "glyphAtlasTextureBytes") +
                   MetricLong(page, "colorGlyphAtlasTextureBytes") +
                   MetricLong(page, "pathAtlasTextureBytes") +
                   MetricLong(page, "glyphCoverageStagingBytes") +
                   MetricLong(page, "pathRasterStagingBytes") +
                   MetricLong(page, "glyphOutlineGpuBytes");
        long allocation = page["allocationPhases"]?["measurement"]?["estimatedBytes"]?.GetValue<long>() ?? 0;
        builder.Append('|').Append(name)
            .Append('|').Append(Bytes(physical))
            .Append('|').Append(Bytes(working))
            .Append('|').Append(Bytes(managed))
            .Append('|').Append(Bytes(committed))
            .Append('|').Append(Bytes(fragmented))
            .Append('|').Append(Bytes(gpu))
            .Append('|').Append(Bytes(MetricLong(page, "allocatedBytesPerFrame")))
            .Append('|').Append(Bytes(allocation))
            .Append('|').Append(MetricText(page, "wallFps"))
            .Append('|').Append(MetricText(page, "compileMs"))
            .Append('|').Append(MetricText(page, "uploadMs"))
            .AppendLine("|");
    }

    builder.AppendLine();
    builder.AppendLine("## Highest estimated steady-state allocation types");
    builder.AppendLine();
    foreach (var page in pages.OrderByDescending(page => page["allocationPhases"]?["measurement"]?["estimatedBytes"]?.GetValue<long>() ?? 0).Take(15))
    {
        var first = page["allocationPhases"]?["measurement"]?["topTypes"]?.AsArray().FirstOrDefault()?.AsObject();
        builder.Append("- ").Append(page["page"]!.GetValue<string>()).Append(": ")
            .Append(Bytes(page["allocationPhases"]?["measurement"]?["estimatedBytes"]?.GetValue<long>() ?? 0));
        if (first is not null)
            builder.Append(" estimated; top type `").Append(first["name"]?.GetValue<string>()).Append("` (").Append(Bytes(first["bytes"]?.GetValue<long>() ?? 0)).Append(')');
        builder.AppendLine();
    }

    return builder.ToString();
}

static BenchmarkResult ParseBenchmarkResult(string path)
{
    string line = File.ReadLines(path).LastOrDefault(value => value.Contains("[SampleBenchmark] RESULT", StringComparison.Ordinal))
        ?? throw new InvalidDataException($"No benchmark RESULT line found in {path}.");
    var metrics = new JsonObject();
    string page = "Unknown";
    foreach (Match match in Regex.Matches(line, "(?<key>[A-Za-z][A-Za-z0-9]*)=(?:\\\"(?<quoted>[^\\\"]*)\\\"|(?<plain>\\S+))"))
    {
        string key = match.Groups["key"].Value;
        string value = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value;
        if (key == "page")
            page = value;
        metrics[key] = ParseValue(value);
    }
    return new BenchmarkResult(page, metrics);
}

static JsonNode ParseValue(string value)
{
    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
        return JsonValue.Create(integer)!;
    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        return JsonValue.Create(number)!;
    if (bool.TryParse(value, out bool boolean))
        return JsonValue.Create(boolean)!;
    return JsonValue.Create(value)!;
}

static string PhaseAt(double timestamp, double? workload, double? measureStart, double? measureStop)
{
    if (measureStop.HasValue && timestamp > measureStop.Value)
        return "after";
    if (measureStart.HasValue && timestamp >= measureStart.Value)
        return "measurement";
    if (workload.HasValue && timestamp >= workload.Value)
        return "warmup";
    return "startup";
}

static string FormatStack(TraceCallStack? stack, int maximumFrames)
{
    if (stack is null)
        return "(stack unavailable)";
    var frames = new List<string>(maximumFrames);
    for (TraceCallStack? current = stack; current is not null && frames.Count < maximumFrames; current = current.Caller)
    {
        string? name = current.CodeAddress?.FullMethodName;
        if (!string.IsNullOrWhiteSpace(name))
            frames.Add(name);
    }
    return frames.Count == 0 ? "(stack unavailable)" : string.Join(" <- ", frames);
}

static string FirstApplicationFrame(TraceCallStack? stack)
{
    for (TraceCallStack? current = stack; current is not null; current = current.Caller)
    {
        string? name = current.CodeAddress?.FullMethodName;
        if (string.IsNullOrWhiteSpace(name))
            continue;
        if (!name.StartsWith("System.", StringComparison.Ordinal) &&
            !name.StartsWith("Microsoft.", StringComparison.Ordinal) &&
            !name.StartsWith("[", StringComparison.Ordinal))
            return name;
    }
    return FormatStack(stack, 1);
}

static bool TryReadRandomizedAllocation(TraceEvent traceEvent, out RandomizedAllocation allocation)
{
    allocation = default;
    ReadOnlySpan<byte> data = traceEvent.EventData();
    int pointerSize = traceEvent.PointerSize;
    int typeNameStart = 6 + pointerSize;
    int trailerSize = pointerSize + sizeof(long) + sizeof(long);
    if ((pointerSize != 4 && pointerSize != 8) || data.Length < typeNameStart + 2 + trailerSize)
        return false;

    int terminator = -1;
    for (int index = typeNameStart; index + 1 < data.Length - trailerSize; index += 2)
    {
        if (data[index] == 0 && data[index + 1] == 0)
        {
            terminator = index;
            break;
        }
    }
    if (terminator < 0)
        return false;

    string typeName = Encoding.Unicode.GetString(data[typeNameStart..terminator]);
    int objectSizeOffset = terminator + 2 + pointerSize;
    if (objectSizeOffset + 16 > data.Length)
        return false;
    long objectSize = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(objectSizeOffset, 8));
    long sampledByteOffset = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(objectSizeOffset + 8, 8));
    if (objectSize <= 0 || sampledByteOffset < 0 || sampledByteOffset >= objectSize)
        return false;

    const double probability = 1d / 102_400d;
    double selectionProbability = 1d - Math.Pow(1d - probability, objectSize);
    double estimatedBytes = selectionProbability > 0d ? objectSize / selectionProbability : objectSize;
    allocation = new RandomizedAllocation(
        string.IsNullOrWhiteSpace(typeName) ? "Unknown" : typeName,
        objectSize,
        sampledByteOffset,
        estimatedBytes);
    return true;
}

static JsonArray Ranked(Dictionary<string, long> values, int count, string nameProperty)
    => new(values.OrderByDescending(pair => pair.Value).Take(count).Select(pair => (JsonNode)new JsonObject
    {
        [nameProperty] = pair.Key,
        ["count"] = pair.Value
    }).ToArray());

static long MetricLong(JsonObject page, params string[] names)
{
    var metrics = page["metrics"]?.AsObject();
    if (metrics is null)
        return 0;
    foreach (string name in names)
    {
        if (metrics[name] is JsonValue value)
        {
            if (value.TryGetValue<long>(out long integer)) return integer;
            if (value.TryGetValue<double>(out double number)) return checked((long)number);
        }
    }
    return 0;
}

static string MetricText(JsonObject page, string name)
{
    var value = page["metrics"]?[name];
    if (value is null) return "0";
    if (value is JsonValue json && json.TryGetValue<double>(out double number))
        return number.ToString("0.###", CultureInfo.InvariantCulture);
    return value.ToString();
}

static string Bytes(long value)
{
    const double scale = 1024d * 1024d;
    return (value / scale).ToString("0.00", CultureInfo.InvariantCulture) + " MiB";
}

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };

sealed record BenchmarkResult(string Page, JsonObject Metrics);
readonly record struct RandomizedAllocation(string TypeName, long ObjectSize, long SampledByteOffset, double EstimatedBytes);

sealed class AllocationAccumulator
{
    private readonly Dictionary<string, AllocationStat> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AllocationStat> _stacks = new(StringComparer.Ordinal);

    public long RawObjectBytes { get; private set; }
    public long EstimatedBytes { get; private set; }
    public long Samples { get; private set; }

    public void Add(string typeName, string stack, long objectBytes, double estimatedBytes)
    {
        long estimate = checked((long)Math.Round(estimatedBytes));
        RawObjectBytes += objectBytes;
        EstimatedBytes += estimate;
        Samples++;
        Add(_types, typeName, objectBytes, estimate);
        Add(_stacks, stack, objectBytes, estimate);
    }

    public JsonObject ToJson() => new()
    {
        ["estimatedBytes"] = EstimatedBytes,
        ["rawSampledObjectBytes"] = RawObjectBytes,
        ["sampleCount"] = Samples,
        ["topTypes"] = Stats(_types, 30),
        ["topStacks"] = Stats(_stacks, 30)
    };

    private static void Add(Dictionary<string, AllocationStat> target, string name, long objectBytes, long estimatedBytes)
    {
        if (!target.TryGetValue(name, out AllocationStat? stat))
            target[name] = stat = new AllocationStat();
        stat.RawObjectBytes += objectBytes;
        stat.EstimatedBytes += estimatedBytes;
        stat.Count++;
    }

    private static JsonArray Stats(Dictionary<string, AllocationStat> source, int count)
        => new(source.OrderByDescending(pair => pair.Value.EstimatedBytes).Take(count).Select(pair => (JsonNode)new JsonObject
        {
            ["name"] = pair.Key,
            ["bytes"] = pair.Value.EstimatedBytes,
            ["rawSampledObjectBytes"] = pair.Value.RawObjectBytes,
            ["count"] = pair.Value.Count
        }).ToArray());
}

sealed class AllocationStat
{
    public long RawObjectBytes;
    public long EstimatedBytes;
    public long Count;
}
