using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;

internal static partial class LiveMemoryCapture
{
    private const int SchemaVersion = 1;

    public static int Run(string[] args)
    {
        try
        {
            CaptureOptions options = CaptureOptions.Parse(args);
            return RunAsync(options).GetAwaiter().GetResult();
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<int> RunAsync(CaptureOptions options)
    {
        using Process process = Process.GetProcessById(options.ProcessId);
        string processName = process.ProcessName;
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        RuntimeCounterCollector? counters = options.RuntimeCounters
            ? new RuntimeCounterCollector(options.ProcessId, options.Interval)
            : null;
        Task counterTask = counters?.StartAsync() ?? Task.CompletedTask;
        var samples = new JsonArray();
        var clock = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                if (HasExited(process))
                {
                    break;
                }

                JsonObject sample;
                try
                {
                    process.Refresh();
                    sample = CaptureProcessSample(
                        process,
                        clock.Elapsed,
                        counters);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception)
                {
                    // A short-lived target can exit between HasExited and the
                    // individual libproc/rusage queries used by Process.
                    break;
                }

                if (OperatingSystem.IsMacOS())
                {
                    sample["macVmmap"] = await CaptureMacVmmapAsync(
                        options.ProcessId,
                        options.CommandTimeout);
                }
                samples.Add(sample);

                if (clock.Elapsed >= options.Duration || HasExited(process))
                {
                    break;
                }

                TimeSpan remaining = options.Duration - clock.Elapsed;
                await Task.Delay(remaining < options.Interval ? remaining : options.Interval);
            }
        }
        finally
        {
            counters?.Stop();
            try
            {
                await counterTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (
                exception is TimeoutException or EndOfStreamException or DiagnosticsClientException)
            {
                counters?.RecordError(exception.Message);
            }
        }

        JsonObject? nativeHeap = null;
        if (options.NativeHeap &&
            OperatingSystem.IsMacOS() &&
            !HasExited(process))
        {
            nativeHeap = await CaptureMacHeapAsync(options.ProcessId, options.CommandTimeout);
        }

        JsonNode? benchmark = null;
        if (options.BenchmarkJson is { } benchmarkPath && File.Exists(benchmarkPath))
        {
            benchmark = JsonNode.Parse(await File.ReadAllTextAsync(benchmarkPath));
        }

        JsonObject root = new()
        {
            ["schemaVersion"] = SchemaVersion,
            ["capturedUtc"] = startedUtc,
            ["process"] = new JsonObject
            {
                ["pid"] = options.ProcessId,
                ["name"] = processName,
                ["platform"] = RuntimeInformation.OSDescription,
                ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString()
            },
            ["configuration"] = new JsonObject
            {
                ["durationSeconds"] = options.Duration.TotalSeconds,
                ["intervalSeconds"] = options.Interval.TotalSeconds,
                ["nativeHeap"] = options.NativeHeap,
                ["runtimeCounters"] = options.RuntimeCounters
            },
            ["samples"] = samples,
            ["macVmmapDiagnostics"] = BuildMacVmmapDiagnostics(samples),
            ["growth"] = BuildGrowth(samples),
            ["runtimeCounterError"] = counters?.Error,
            ["runtimeEventDiagnostics"] =
                counters?.Diagnostics() ??
                new JsonObject { ["disabled"] = true },
            ["nativeHeap"] = nativeHeap,
            ["benchmark"] = benchmark
        };

        string outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        string markdownPath = Path.ChangeExtension(outputPath, ".md");
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(root));
        Console.WriteLine($"Captured {samples.Count} samples for PID {options.ProcessId}.");
        Console.WriteLine(outputPath);
        Console.WriteLine(markdownPath);
        return samples.Count > 0 ? 0 : 3;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static JsonObject CaptureProcessSample(
        Process process,
        TimeSpan elapsed,
        RuntimeCounterCollector? counters)
    {
        var processNode = new JsonObject
        {
            ["workingSetBytes"] = process.WorkingSet64,
            ["privateBytes"] = process.PrivateMemorySize64,
            ["virtualBytes"] = process.VirtualMemorySize64,
            ["pagedBytes"] = process.PagedMemorySize64,
            ["threadCount"] = process.Threads.Count,
            ["handleCount"] = process.HandleCount,
            ["totalProcessorTimeMs"] = process.TotalProcessorTime.TotalMilliseconds
        };

        return new JsonObject
        {
            ["elapsedSeconds"] = elapsed.TotalSeconds,
            ["timestampUtc"] = DateTimeOffset.UtcNow,
            ["process"] = processNode,
            ["dotnet"] = counters?.Snapshot() ?? new JsonObject()
        };
    }

    private static async Task<JsonObject> CaptureMacVmmapAsync(
        int processId,
        TimeSpan timeout)
    {
        CommandResult result = await RunCommandAsync(
            "/usr/bin/vmmap",
            ["-summary", processId.ToString(CultureInfo.InvariantCulture)],
            timeout);
        var regions = new JsonObject();
        long physicalFootprint = 0;
        long peakPhysicalFootprint = 0;

        foreach (string line in Lines(result.StandardOutput))
        {
            Match footprint = PhysicalFootprintRegex().Match(line);
            if (footprint.Success)
            {
                physicalFootprint = ParseBytes(footprint.Groups["value"].Value);
                continue;
            }

            Match peak = PeakPhysicalFootprintRegex().Match(line);
            if (peak.Success)
            {
                peakPhysicalFootprint = ParseBytes(peak.Groups["value"].Value);
                continue;
            }

            Match region = VmmapRegionRegex().Match(line);
            if (!region.Success)
            {
                continue;
            }

            string name = region.Groups["name"].Value.Trim();
            if (!IsRelevantVmRegion(name))
            {
                continue;
            }

            regions[name] = new JsonObject
            {
                ["virtualBytes"] = ParseBytes(region.Groups["virtual"].Value),
                ["residentBytes"] = ParseBytes(region.Groups["resident"].Value),
                ["dirtyBytes"] = ParseBytes(region.Groups["dirty"].Value)
            };
        }

        return new JsonObject
        {
            ["physicalFootprintBytes"] = physicalFootprint,
            ["peakPhysicalFootprintBytes"] = peakPhysicalFootprint,
            ["regions"] = regions,
            ["exitCode"] = result.ExitCode,
            ["error"] = EmptyToNull(result.StandardError)
        };
    }

    private static async Task<JsonObject> CaptureMacHeapAsync(
        int processId,
        TimeSpan timeout)
    {
        CommandResult result = await RunCommandAsync(
            "/usr/bin/heap",
            ["-s", processId.ToString(CultureInfo.InvariantCulture)],
            timeout);
        long nodeCount = 0;
        long allocatedBytes = 0;
        var classes = new JsonObject();

        foreach (string line in Lines(result.StandardOutput))
        {
            Match allZones = AllZonesRegex().Match(line);
            if (allZones.Success)
            {
                nodeCount = long.Parse(
                    allZones.Groups["nodes"].Value,
                    CultureInfo.InvariantCulture);
                allocatedBytes = long.Parse(
                    allZones.Groups["bytes"].Value,
                    CultureInfo.InvariantCulture);
                continue;
            }

            Match row = HeapClassRegex().Match(line);
            if (!row.Success)
            {
                continue;
            }

            string name = row.Groups["name"].Value.Trim();
            if (!IsRelevantNativeClass(name))
            {
                continue;
            }

            classes[name] = new JsonObject
            {
                ["count"] = long.Parse(
                    row.Groups["count"].Value,
                    CultureInfo.InvariantCulture),
                ["bytes"] = long.Parse(
                    row.Groups["bytes"].Value,
                    CultureInfo.InvariantCulture)
            };
        }

        return new JsonObject
        {
            ["nodeCount"] = nodeCount,
            ["allocatedBytes"] = allocatedBytes,
            ["classes"] = classes,
            ["exitCode"] = result.ExitCode,
            ["error"] = EmptyToNull(result.StandardError)
        };
    }

    private static JsonObject BuildGrowth(JsonArray samples)
    {
        if (samples.Count == 0)
        {
            return new JsonObject();
        }

        JsonObject[] allSamples = samples
            .Select(sample => sample!.AsObject())
            .ToArray();
        JsonObject[] successfulVmmapSamples = allSamples
            .Where(IsSuccessfulMacVmmapSample)
            .ToArray();
        JsonObject[] processSamples = successfulVmmapSamples.Length > 0
            ? successfulVmmapSamples
            : allSamples;
        var growth = new JsonObject();
        AddGrowth(
            growth,
            "workingSetBytes",
            processSamples[0],
            processSamples[^1],
            "process",
            "workingSetBytes");
        if (successfulVmmapSamples.Length > 0)
        {
            JsonObject first = successfulVmmapSamples[0];
            JsonObject last = successfulVmmapSamples[^1];
            AddGrowth(
                growth,
                "physicalFootprintBytes",
                first,
                last,
                "macVmmap",
                "physicalFootprintBytes");
            AddRegionGrowth(
                growth,
                first,
                last,
                "owned unmapped (graphics)");
            AddRegionGrowth(
                growth,
                first,
                last,
                "IOAccelerator (graphics)");
            AddRegionGrowth(growth, first, last, "VM_ALLOCATE");
            AddRegionGrowth(growth, first, last, "IOSurface");
            AddRegionGrowth(
                growth,
                first,
                last,
                "Dispatch continuations");
        }
        return growth;
    }

    private static JsonObject BuildMacVmmapDiagnostics(JsonArray samples)
    {
        int attempted = 0;
        int successful = 0;
        foreach (JsonNode? sample in samples)
        {
            if (sample?["macVmmap"] is not JsonObject vmmap)
                continue;

            attempted++;
            if (ReadLong(vmmap["exitCode"]) == 0)
                successful++;
        }

        return new JsonObject
        {
            ["attemptedSamples"] = attempted,
            ["successfulSamples"] = successful,
            ["failedSamples"] = attempted - successful
        };
    }

    private static bool IsSuccessfulMacVmmapSample(JsonObject sample) =>
        sample["macVmmap"] is JsonObject vmmap &&
        ReadLong(vmmap["exitCode"]) == 0;

    private static void AddGrowth(
        JsonObject target,
        string name,
        JsonObject first,
        JsonObject last,
        string objectName,
        string valueName)
    {
        long firstValue = ReadLong(first[objectName]?[valueName]);
        long lastValue = ReadLong(last[objectName]?[valueName]);
        target[name] = new JsonObject
        {
            ["firstBytes"] = firstValue,
            ["lastBytes"] = lastValue,
            ["deltaBytes"] = lastValue - firstValue
        };
    }

    private static void AddRegionGrowth(
        JsonObject target,
        JsonObject first,
        JsonObject last,
        string regionName)
    {
        AddRegionGrowthValue(
            target,
            $"region-resident:{regionName}",
            first,
            last,
            regionName,
            "residentBytes");
        AddRegionGrowthValue(
            target,
            $"region-dirty:{regionName}",
            first,
            last,
            regionName,
            "dirtyBytes");
    }

    private static void AddRegionGrowthValue(
        JsonObject target,
        string metricName,
        JsonObject first,
        JsonObject last,
        string regionName,
        string valueName)
    {
        long firstValue = ReadLong(
            first["macVmmap"]?["regions"]?[regionName]?[valueName]);
        long lastValue = ReadLong(
            last["macVmmap"]?["regions"]?[regionName]?[valueName]);
        target[metricName] = new JsonObject
        {
            ["firstBytes"] = firstValue,
            ["lastBytes"] = lastValue,
            ["deltaBytes"] = lastValue - firstValue
        };
    }

    private static string BuildMarkdown(JsonObject root)
    {
        var builder = new StringBuilder();
        JsonObject process = root["process"]!.AsObject();
        JsonArray samples = root["samples"]!.AsArray();
        builder.AppendLine("# ProGPU memory capture");
        builder.AppendLine();
        builder.Append("PID: ").Append(process["pid"]).Append(" (`")
            .Append(process["name"]).AppendLine("`)");
        builder.Append("Samples: ").AppendLine(samples.Count.ToString(CultureInfo.InvariantCulture));
        if (root["macVmmapDiagnostics"] is JsonObject vmmapDiagnostics)
        {
            builder.Append("Successful macOS VM-map samples: ")
                .Append(vmmapDiagnostics["successfulSamples"])
                .Append(" of ")
                .AppendLine(vmmapDiagnostics["attemptedSamples"]!.ToString());
        }
        builder.AppendLine();
        builder.AppendLine("| Metric | First | Last | Delta |");
        builder.AppendLine("| --- | ---: | ---: | ---: |");
        foreach ((string name, JsonNode? value) in root["growth"]!.AsObject())
        {
            JsonObject item = value!.AsObject();
            builder.Append("| ").Append(name).Append(" | ")
                .Append(FormatBytes(ReadLong(item["firstBytes"]))).Append(" | ")
                .Append(FormatBytes(ReadLong(item["lastBytes"]))).Append(" | ")
                .Append(FormatSignedBytes(ReadLong(item["deltaBytes"]))).AppendLine(" |");
        }

        if (root["nativeHeap"] is JsonObject nativeHeap)
        {
            builder.AppendLine();
            builder.AppendLine("## Native heap");
            builder.AppendLine();
            builder.Append("Live allocator payload: ")
                .AppendLine(FormatBytes(ReadLong(nativeHeap["allocatedBytes"])));
            builder.AppendLine();
            builder.AppendLine("| Class | Count | Bytes |");
            builder.AppendLine("| --- | ---: | ---: |");
            foreach ((string name, JsonNode? value) in nativeHeap["classes"]!.AsObject())
            {
                JsonObject item = value!.AsObject();
                builder.Append("| ").Append(name).Append(" | ")
                    .Append(item["count"]).Append(" | ")
                    .Append(FormatBytes(ReadLong(item["bytes"]))).AppendLine(" |");
            }
        }

        return builder.ToString();
    }

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            return new CommandResult(-1, await stdout, "Command timed out.");
        }

        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }

    private static bool IsRelevantVmRegion(string name)
        => name is "VM_ALLOCATE" or "IOSurface" or "owned unmapped" or
               "owned unmapped (graphics)" or "IOAccelerator (graphics)" or
               "MALLOC_SMALL" or "MALLOC_SMALL (empty)" or "MALLOC_LARGE" or
               "MALLOC_TINY" or "Stack" or "Dispatch continuations";

    private static bool IsRelevantNativeClass(string name)
        => name.Contains("AGX", StringComparison.Ordinal) ||
           name.Contains("MTL", StringComparison.Ordinal) ||
           name.Contains("IOGPU", StringComparison.Ordinal) ||
           name.Contains("coreclr", StringComparison.OrdinalIgnoreCase) ||
           name == "non-object";

    private static IEnumerable<string> Lines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static long ParseBytes(string value)
    {
        string normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return 0;
        }

        char suffix = char.ToUpperInvariant(normalized[^1]);
        double scale = suffix switch
        {
            'K' => 1024d,
            'M' => 1024d * 1024d,
            'G' => 1024d * 1024d * 1024d,
            _ => 1d
        };
        string number = suffix is 'K' or 'M' or 'G'
            ? normalized[..^1]
            : normalized;
        return checked((long)Math.Round(
            double.Parse(number, CultureInfo.InvariantCulture) * scale));
    }

    private static long ReadLong(JsonNode? value)
    {
        if (value is not JsonValue json)
        {
            return 0;
        }
        if (json.TryGetValue<long>(out long integer))
        {
            return integer;
        }
        return json.TryGetValue<double>(out double number)
            ? checked((long)number)
            : 0;
    }

    private static string FormatBytes(long value)
        => (value / (1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture) +
           " MiB";

    private static string FormatSignedBytes(long value)
        => (value >= 0 ? "+" : string.Empty) + FormatBytes(value);

    private static string? EmptyToNull(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(
        @"^Physical footprint:\s+(?<value>\d+(?:\.\d+)?[KMG]?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalFootprintRegex();

    [GeneratedRegex(
        @"^Physical footprint \(peak\):\s+(?<value>\d+(?:\.\d+)?[KMG]?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PeakPhysicalFootprintRegex();

    [GeneratedRegex(
        @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_ ()-]*?)\s+(?<virtual>\d+(?:\.\d+)?[KMG]?)\s+(?<resident>\d+(?:\.\d+)?[KMG]?)\s+(?<dirty>\d+(?:\.\d+)?[KMG]?)\s+",
        RegexOptions.CultureInvariant)]
    private static partial Regex VmmapRegionRegex();

    [GeneratedRegex(
        @"^All zones:\s+(?<nodes>\d+) nodes \((?<bytes>\d+) bytes\)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AllZonesRegex();

    [GeneratedRegex(
        @"^\s*(?<count>\d+)\s+(?<bytes>\d+)\s+\d+(?:\.\d+)?\s+(?<name>.+?)\s{2,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeapClassRegex();

    private readonly record struct CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record CaptureOptions(
        int ProcessId,
        TimeSpan Duration,
        TimeSpan Interval,
        string OutputPath,
        bool NativeHeap,
        bool RuntimeCounters,
        string? BenchmarkJson,
        TimeSpan CommandTimeout)
    {
        public static CaptureOptions Parse(string[] args)
        {
            int? processId = null;
            double durationSeconds = 15;
            double intervalSeconds = 2;
            string? output = null;
            bool nativeHeap = false;
            bool runtimeCounters = true;
            string? benchmark = null;

            for (int index = 1; index < args.Length; index++)
            {
                string argument = args[index];
                switch (argument)
                {
                    case "--pid":
                        processId = int.Parse(Next(args, ref index, argument), CultureInfo.InvariantCulture);
                        break;
                    case "--duration":
                        durationSeconds = double.Parse(
                            Next(args, ref index, argument),
                            CultureInfo.InvariantCulture);
                        break;
                    case "--interval":
                        intervalSeconds = double.Parse(
                            Next(args, ref index, argument),
                            CultureInfo.InvariantCulture);
                        break;
                    case "--output":
                        output = Next(args, ref index, argument);
                        break;
                    case "--native-heap":
                        nativeHeap = true;
                        break;
                    case "--no-runtime-counters":
                        runtimeCounters = false;
                        break;
                    case "--benchmark-json":
                        benchmark = Path.GetFullPath(Next(args, ref index, argument));
                        break;
                    default:
                        throw new ArgumentException($"Unknown capture option: {argument}");
                }
            }

            if (processId is null or <= 0)
            {
                throw new ArgumentException("capture requires --pid <positive process id>.");
            }
            if (durationSeconds <= 0 || intervalSeconds <= 0)
            {
                throw new ArgumentException("capture duration and interval must be positive.");
            }
            if (output is null)
            {
                throw new ArgumentException("capture requires --output <capture.json>.");
            }

            return new CaptureOptions(
                processId.Value,
                TimeSpan.FromSeconds(durationSeconds),
                TimeSpan.FromSeconds(intervalSeconds),
                Path.GetFullPath(output),
                nativeHeap,
                runtimeCounters,
                benchmark,
                TimeSpan.FromSeconds(15));
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (++index >= args.Length)
            {
                throw new ArgumentException($"{option} requires a value.");
            }
            return args[index];
        }
    }

    private sealed class RuntimeCounterCollector
    {
        private readonly int _processId;
        private readonly TimeSpan _interval;
        private readonly ConcurrentDictionary<string, double> _values =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _eventCounts =
            new(StringComparer.Ordinal);
        private EventPipeSession? _session;
        private string? _lastPayloadType;
        private string? _lastPayloadText;

        public RuntimeCounterCollector(int processId, TimeSpan interval)
        {
            _processId = processId;
            _interval = interval;
        }

        public string? Error { get; private set; }

        public Task StartAsync()
            => Task.Run(() =>
            {
                try
                {
                    var provider = new EventPipeProvider(
                        "System.Runtime",
                        EventLevel.Informational,
                        0,
                        new Dictionary<string, string>
                        {
                            ["EventCounterIntervalSec"] =
                                Math.Max(0.1, _interval.TotalSeconds)
                                    .ToString("0.###", CultureInfo.InvariantCulture)
                        });
                    var client = new DiagnosticsClient(_processId);
                    _session = client.StartEventPipeSession([provider], requestRundown: false);
                    using var source = new EventPipeEventSource(_session.EventStream);
                    source.Dynamic.All += OnRuntimeEvent;
                    source.Process();
                }
                catch (Exception exception)
                {
                    Error = exception.Message;
                }
            });

        public void Stop()
        {
            try
            {
                _session?.Stop();
            }
            catch (Exception exception) when (
                exception is EndOfStreamException or DiagnosticsClientException)
            {
                Error ??= exception.Message;
            }
        }

        public void RecordError(string error)
        {
            Error ??= error;
        }

        public JsonObject Snapshot()
        {
            var result = new JsonObject();
            foreach ((string name, double value) in _values.OrderBy(pair => pair.Key))
            {
                result[name] = value;
            }
            return result;
        }

        public JsonObject Diagnostics()
        {
            var events = new JsonObject();
            foreach ((string name, int count) in _eventCounts.OrderBy(pair => pair.Key))
            {
                events[name] = count;
            }

            return new JsonObject
            {
                ["events"] = events,
                ["lastPayloadType"] = _lastPayloadType,
                ["lastPayloadText"] = _lastPayloadText
            };
        }

        private void OnRuntimeEvent(TraceEvent traceEvent)
        {
            _eventCounts.AddOrUpdate(traceEvent.EventName, 1, static (_, count) => count + 1);
            if (!traceEvent.EventName.StartsWith(
                    "EventCounters",
                    StringComparison.Ordinal))
            {
                return;
            }

            object? rawPayload = traceEvent.PayloadByName("Payload");
            if (rawPayload is null && traceEvent.PayloadNames.Length > 0)
            {
                rawPayload = traceEvent.PayloadValue(0);
            }
            _lastPayloadType = rawPayload?.GetType().FullName;
            _lastPayloadText = rawPayload?.ToString();
            if (rawPayload is not IDictionary<string, object> payload)
            {
                return;
            }
            if (payload.TryGetValue("Payload", out object? nested) &&
                nested is IDictionary<string, object> nestedPayload)
            {
                payload = nestedPayload;
            }

            string? name = payload.TryGetValue("Name", out object? rawName)
                ? rawName as string
                : null;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            object? raw = payload.TryGetValue("Mean", out object? mean)
                ? mean
                : payload.TryGetValue("Increment", out object? increment)
                    ? increment
                    : null;
            if (raw is IConvertible convertible)
            {
                _values[name] = convertible.ToDouble(CultureInfo.InvariantCulture);
            }
        }
    }
}
