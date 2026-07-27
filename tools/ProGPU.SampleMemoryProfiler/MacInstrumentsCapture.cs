using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static class MacInstrumentsCapture
{
    private static readonly IReadOnlyDictionary<string, string> TemplateNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["allocations"] = "Allocations",
            ["time"] = "Time Profiler",
            ["metal"] = "Metal System Trace"
        };

    private static readonly string[] MetalExportSchemas =
    [
        "metal-current-allocated-size",
        "metal-resource-allocations",
        "metal-kernel-resource-allocations",
        "metal-application-command-buffer-submissions",
        "metal-command-buffer-completed",
        "ca-client-buffer-wait-interval",
        "metal-command-buffer-error",
        "graphics-compiler-spill-events",
        "potential-hangs",
        "hang-risks",
        "time-profile"
    ];

    private static readonly string[] TimeProfilerExportSchemas =
    [
        "time-profile",
        "time-sample",
        "potential-hangs",
        "hang-risks"
    ];
    private const string AllocationsStatisticsXPath =
        "//trace-toc[1]/run[1]/tracks[1]/track[@name=\"Allocations\"]" +
        "/details/detail[@name=\"Statistics\"]";
    private const string AllocationsListXPath =
        "//trace-toc[1]/run[1]/tracks[1]/track[@name=\"Allocations\"]" +
        "/details/detail[@name=\"Allocations List\"]";

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine(
                "The Instruments capture command requires macOS and Xcode.");
            return 2;
        }

        if (!TryParse(args, out CaptureOptions options, out string? error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        var results = new List<TemplateCaptureResult>(options.Templates.Count);
        foreach (string templateKey in options.Templates)
        {
            string templateName = TemplateNames[templateKey];
            string slug = templateKey.ToLowerInvariant();
            string tracePath = Path.Combine(
                options.OutputDirectory,
                $"{slug}.trace");
            string logPath = Path.Combine(
                options.OutputDirectory,
                $"{slug}.log");
            if (Directory.Exists(tracePath) || File.Exists(tracePath))
            {
                Console.Error.WriteLine(
                    $"Refusing to overwrite existing trace: {tracePath}");
                return 3;
            }

            using var captureScratch = new CaptureScratchDirectory(slug);
            using var xcodeTemporaryFiles = new XcodeTemporaryFileTracker();
            Console.WriteLine(
                $"[Instruments] template=\"{templateName}\" " +
                $"duration={options.DurationSeconds}s");
            var recordArguments = new List<string>
            {
                "xctrace",
                "record",
                "--template",
                templateName,
                "--time-limit",
                $"{options.DurationSeconds}s",
                "--output",
                tracePath
            };
            if (options.WindowSeconds is { } windowSeconds)
            {
                recordArguments.Add("--window");
                recordArguments.Add($"{windowSeconds}s");
            }

            if (options.Attach)
            {
                recordArguments.Add("--attach");
                recordArguments.Add(options.Target);
            }
            else
            {
                foreach (string environmentVariable in options.EnvironmentVariables)
                {
                    recordArguments.Add("--env");
                    recordArguments.Add(environmentVariable);
                }

                recordArguments.Add("--launch");
                recordArguments.Add("--");
                recordArguments.Add(options.Target);
                recordArguments.AddRange(options.TargetArguments);
            }

            ProcessResult record = RunProcess(
                "xcrun",
                recordArguments,
                echoOutput: true,
                timeout: TimeSpan.FromSeconds(
                    checked(options.DurationSeconds + 120)),
                temporaryDirectory: captureScratch.Path);
            File.WriteAllText(
                logPath,
                record.StandardOutput + record.StandardError);

            bool traceCreated = Directory.Exists(tracePath);
            if (record.TimedOut)
            {
                long deletedBytes = traceCreated
                    ? DeleteTraceBundle(tracePath)
                    : 0;
                Console.Error.WriteLine(
                    $"Instruments did not terminate {templateName} within the bounded finalization window. " +
                    $"The process tree was stopped and {deletedBytes} B of incomplete trace data was removed.");
                return 4;
            }
            bool recordingFailed =
                record.StandardOutput.Contains(
                    "Recording failed with errors",
                    StringComparison.OrdinalIgnoreCase) ||
                record.StandardError.Contains(
                    "Recording failed with errors",
                    StringComparison.OrdinalIgnoreCase) ||
                record.StandardOutput.Contains(
                    "Failed to start the recording",
                    StringComparison.OrdinalIgnoreCase) ||
                record.StandardError.Contains(
                    "Failed to start the recording",
                    StringComparison.OrdinalIgnoreCase);
            if (!traceCreated || recordingFailed)
            {
                long deletedBytes =
                    traceCreated && options.CleanupTraces
                        ? DeleteTraceBundle(tracePath)
                        : 0;
                Console.Error.WriteLine(
                    $"Instruments did not complete {templateName}. " +
                    $"Trace created: {traceCreated}. " +
                    $"Exit code: {record.ExitCode}. " +
                    $"Removed incomplete trace bytes: {deletedBytes}.");
                return 4;
            }

            string tocPath = Path.Combine(
                options.OutputDirectory,
                $"{slug}-toc.xml");
            ProcessResult toc = RunProcess(
                "xcrun",
                [
                    "xctrace",
                    "export",
                    "--input",
                    tracePath,
                    "--toc",
                    "--output",
                    tocPath
                ],
                temporaryDirectory: captureScratch.Path);
            if (toc.ExitCode != 0)
            {
                Console.Error.WriteLine(
                    $"Failed to export the {templateName} table of contents.");
                return 5;
            }

            var exports = new List<string>();
            string tocXml = File.ReadAllText(tocPath);
            IReadOnlyList<string> exportSchemas =
                templateKey.Equals("metal", StringComparison.OrdinalIgnoreCase)
                    ? MetalExportSchemas
                    : templateKey.Equals("time", StringComparison.OrdinalIgnoreCase)
                        ? TimeProfilerExportSchemas
                        : [];
            foreach (string schema in exportSchemas)
            {
                if (!ContainsSchema(tocXml, schema))
                {
                    continue;
                }

                string exportPath = Path.Combine(
                    options.OutputDirectory,
                    $"{slug}-{schema}.xml");
                ProcessResult export = RunProcess(
                    "xcrun",
                    [
                        "xctrace",
                        "export",
                        "--input",
                        tracePath,
                        "--xpath",
                        $"//trace-toc[1]/run[1]/data[1]/table[@schema=\"{schema}\"]",
                        "--output",
                        exportPath
                    ],
                    temporaryDirectory: captureScratch.Path);
                if (export.ExitCode == 0 && File.Exists(exportPath))
                {
                    exports.Add(exportPath);
                    continue;
                }

                Console.Error.WriteLine(
                    $"Failed to export supported {templateName} table '{schema}'. " +
                    "The raw trace has been retained.");
                return 6;
            }

            if (templateKey.Equals(
                    "allocations",
                    StringComparison.OrdinalIgnoreCase))
            {
                string exportPath = Path.Combine(
                    options.OutputDirectory,
                    "allocations-statistics.xml");
                ProcessResult export = RunProcess(
                    "xcrun",
                    [
                        "xctrace",
                        "export",
                        "--input",
                        tracePath,
                        "--xpath",
                        AllocationsStatisticsXPath,
                        "--output",
                        exportPath
                    ],
                    temporaryDirectory: captureScratch.Path);
                if (export.ExitCode == 0 && File.Exists(exportPath))
                {
                    exports.Add(exportPath);
                }
                else
                {
                    Console.Error.WriteLine(
                        "Failed to export the Allocations statistics table. " +
                        "The raw trace has been retained.");
                    return 6;
                }

                if (options.AllocationDetails)
                {
                    string detailsPath = Path.Combine(
                        options.OutputDirectory,
                        "allocations-list.xml");
                    ProcessResult details = RunProcess(
                        "xcrun",
                        [
                            "xctrace",
                            "export",
                            "--input",
                            tracePath,
                            "--xpath",
                            AllocationsListXPath,
                            "--output",
                            detailsPath
                        ],
                        temporaryDirectory: captureScratch.Path);
                    if (details.ExitCode == 0 && File.Exists(detailsPath))
                    {
                        exports.Add(detailsPath);
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            "Failed to export the Allocations list. " +
                            "The raw trace has been retained.");
                        return 6;
                    }
                }
            }

            long deletedTemporaryBytes = checked(
                captureScratch.Delete() +
                xcodeTemporaryFiles.Delete());
            results.Add(
                new TemplateCaptureResult(
                    templateKey,
                    templateName,
                    tracePath,
                    tocPath,
                    logPath,
                    record.ExitCode,
                    exports,
                    TraceRetained: !options.CleanupTraces,
                    DeletedTraceBytes: options.CleanupTraces
                        ? DeleteTraceBundle(tracePath)
                        : 0,
                    ExportsRetained: true,
                    DeletedExportBytes: 0,
                    DeletedTemporaryBytes: deletedTemporaryBytes));
        }

        string manifestPath = Path.Combine(
            options.OutputDirectory,
            "instruments-manifest.json");
        InstrumentsCaptureSummary summary =
            MacInstrumentsSummary.Write(options.OutputDirectory);
        if (options.CleanupExports)
        {
            for (int index = 0; index < results.Count; index++)
            {
                results[index] = DeleteCaptureExports(results[index]);
            }
        }

        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 2,
                    capturedUtc = DateTimeOffset.UtcNow,
                    durationSeconds = options.DurationSeconds,
                    windowSeconds = options.WindowSeconds,
                    targetMode = options.Attach ? "attach" : "launch",
                    target = options.Target,
                    arguments = options.TargetArguments,
                    environmentVariableNames = options.EnvironmentVariables
                        .Select(GetEnvironmentVariableName)
                        .ToArray(),
                    cleanupTraces = options.CleanupTraces,
                    cleanupExports = options.CleanupExports,
                    allocationDetails = options.AllocationDetails,
                    summary = new
                    {
                        json = Path.Combine(
                            options.OutputDirectory,
                            "instruments-summary.json"),
                        markdown = Path.Combine(
                            options.OutputDirectory,
                            "instruments-summary.md"),
                        resourceCount = summary.Resources.Count,
                        maximumCurrentAllocatedBytes =
                            summary.CurrentAllocatedSize.Maximum,
                        nativeHeapAndAnonymousVmPersistentBytes =
                            summary.NativeAllocations
                                .HeapAndAnonymousVmPersistentBytes
                    },
                    captures = results
                },
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[Instruments] manifest={manifestPath}");
        return 0;
    }

    private static bool TryParse(
        string[] args,
        out CaptureOptions options,
        out string? error)
    {
        string? output = null;
        int duration = 15;
        int? window = null;
        var environmentVariables = new List<string>();
        bool cleanupTraces = false;
        bool cleanupExports = false;
        bool allocationDetails = false;
        var templates = new List<string>
        {
            "allocations",
            "time",
            "metal"
        };
        int launchIndex = Array.IndexOf(args, "--launch");
        int attachIndex = Array.IndexOf(args, "--attach");
        if ((launchIndex < 0) == (attachIndex < 0))
        {
            options = default!;
            error =
                "The Instruments command requires exactly one of " +
                "--launch <executable> or --attach <pid-or-name>.";
            return false;
        }
        int targetIndex = launchIndex >= 0 ? launchIndex : attachIndex;
        bool attach = attachIndex >= 0;
        if (targetIndex + 1 >= args.Length ||
            (attach && targetIndex + 2 != args.Length))
        {
            options = default!;
            error = attach
                ? "The Instruments --attach target does not accept arguments."
                : "The Instruments --launch option requires an executable.";
            return false;
        }

        for (int index = 1; index < targetIndex; index++)
        {
            string argument = args[index];
            if (argument == "--output" && index + 1 < targetIndex)
            {
                output = args[++index];
            }
            else if (argument == "--duration" &&
                     index + 1 < targetIndex &&
                     int.TryParse(args[++index], out int parsedDuration) &&
                     parsedDuration > 0)
            {
                duration = parsedDuration;
            }
            else if (argument == "--templates" && index + 1 < targetIndex)
            {
                templates = args[++index]
                    .Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries)
                    .Select(value => value.ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else if (argument == "--window" &&
                     index + 1 < targetIndex &&
                     int.TryParse(args[++index], out int parsedWindow) &&
                     parsedWindow > 0)
            {
                window = parsedWindow;
            }
            else if (argument == "--env" &&
                     index + 1 < targetIndex &&
                     IsEnvironmentVariableAssignment(args[index + 1]))
            {
                environmentVariables.Add(args[++index]);
            }
            else if (argument == "--cleanup-traces")
            {
                cleanupTraces = true;
            }
            else if (argument == "--cleanup-exports")
            {
                cleanupExports = true;
            }
            else if (argument == "--allocation-details")
            {
                allocationDetails = true;
            }
            else
            {
                options = default!;
                error = $"Unknown or invalid Instruments option: {argument}";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            options = default!;
            error = "The Instruments command requires --output <directory>.";
            return false;
        }

        if (attach && environmentVariables.Count != 0)
        {
            options = default!;
            error =
                "The Instruments --env option is available only with --launch.";
            return false;
        }

        if (templates.Count == 0 ||
            templates.Any(template => !TemplateNames.ContainsKey(template)))
        {
            options = default!;
            error =
                "Templates must be a comma-separated subset of " +
                "allocations,time,metal.";
            return false;
        }

        if (window > duration)
        {
            options = default!;
            error = "The Instruments --window duration cannot exceed --duration.";
            return false;
        }

        string targetArgument = args[targetIndex + 1];
        string target =
            !attach &&
            (Path.IsPathFullyQualified(targetArgument) ||
             targetArgument.Contains(Path.DirectorySeparatorChar) ||
             targetArgument.Contains(Path.AltDirectorySeparatorChar))
                ? Path.GetFullPath(targetArgument)
                : targetArgument;
        options = new CaptureOptions(
            Path.GetFullPath(output),
            duration,
            window,
            templates,
            environmentVariables,
            cleanupTraces,
            cleanupExports,
            allocationDetails,
            attach,
            target,
            attach
                ? []
                : args.Skip(targetIndex + 2).ToArray());
        error = null;
        return true;
    }

    private static bool IsEnvironmentVariableAssignment(string value)
    {
        int separator = value.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        for (int index = 0; index < separator; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetEnvironmentVariableName(string assignment)
    {
        int separator = assignment.IndexOf('=');
        return separator < 0 ? assignment : assignment[..separator];
    }

    private static bool ContainsSchema(string tocXml, string schema)
        => tocXml.Contains(
               $"schema=\"{schema}\"",
               StringComparison.Ordinal) ||
           tocXml.Contains(
               $"schema='{schema}'",
               StringComparison.Ordinal);

    private static ProcessResult RunProcess(
        string executable,
        IReadOnlyList<string> arguments,
        bool echoOutput = false,
        TimeSpan? timeout = null,
        string? temporaryDirectory = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (temporaryDirectory != null)
        {
            startInfo.Environment["TMPDIR"] = temporaryDirectory;
            startInfo.Environment["TMP"] = temporaryDirectory;
            startInfo.Environment["TEMP"] = temporaryDirectory;
        }
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                $"Failed to start {executable}.");
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data == null)
            {
                return;
            }

            standardOutput.AppendLine(eventArgs.Data);
            if (echoOutput)
            {
                Console.WriteLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data == null)
            {
                return;
            }

            standardError.AppendLine(eventArgs.Data);
            if (echoOutput)
            {
                Console.Error.WriteLine(eventArgs.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        bool timedOut = false;
        if (timeout is { } timeoutValue)
        {
            int timeoutMilliseconds = checked(
                (int)Math.Min(
                    int.MaxValue,
                    Math.Ceiling(
                        timeoutValue.TotalMilliseconds)));
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                timedOut = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the timeout and kill.
                }
            }
        }

        process.WaitForExit();
        return new ProcessResult(
            process.ExitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            timedOut);
    }

    private static long DeleteTraceBundle(string tracePath)
    {
        if (!Directory.Exists(tracePath))
        {
            return 0;
        }

        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(
                     tracePath,
                     "*",
                     SearchOption.AllDirectories))
        {
            bytes = checked(bytes + new FileInfo(file).Length);
        }

        Directory.Delete(tracePath, recursive: true);
        Console.WriteLine(
            $"[Instruments] deleted-trace={tracePath} bytes={bytes}");
        return bytes;
    }

    private static TemplateCaptureResult DeleteCaptureExports(
        TemplateCaptureResult result)
    {
        long bytes = DeleteFile(result.TableOfContentsPath);
        foreach (string exportPath in result.Exports)
        {
            bytes = checked(bytes + DeleteFile(exportPath));
        }

        Console.WriteLine(
            $"[Instruments] deleted-exports={result.Key} bytes={bytes}");
        return result with
        {
            ExportsRetained = false,
            DeletedExportBytes = bytes
        };
    }

    private static long DeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        long bytes = new FileInfo(path).Length;
        File.Delete(path);
        return bytes;
    }

    private sealed record CaptureOptions(
        string OutputDirectory,
        int DurationSeconds,
        int? WindowSeconds,
        IReadOnlyList<string> Templates,
        IReadOnlyList<string> EnvironmentVariables,
        bool CleanupTraces,
        bool CleanupExports,
        bool AllocationDetails,
        bool Attach,
        string Target,
        IReadOnlyList<string> TargetArguments);

    private sealed record TemplateCaptureResult(
        string Key,
        string Template,
        string TracePath,
        string TableOfContentsPath,
        string LogPath,
        int RecordExitCode,
        IReadOnlyList<string> Exports,
        bool TraceRetained,
        long DeletedTraceBytes,
        bool ExportsRetained,
        long DeletedExportBytes,
        long DeletedTemporaryBytes);

    private sealed class CaptureScratchDirectory : IDisposable
    {
        private bool _deleted;

        public CaptureScratchDirectory(string captureKey)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"progpu-instruments-{captureKey}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public long Delete()
        {
            if (_deleted || !Directory.Exists(Path))
            {
                _deleted = true;
                return 0;
            }

            long bytes = 0;
            foreach (string file in Directory.EnumerateFiles(
                         Path,
                         "*",
                         SearchOption.AllDirectories))
            {
                bytes = checked(bytes + new FileInfo(file).Length);
            }

            Directory.Delete(Path, recursive: true);
            _deleted = true;
            Console.WriteLine(
                $"[Instruments] deleted-scratch={Path} bytes={bytes}");
            return bytes;
        }

        public void Dispose() => Delete();
    }

    private sealed class XcodeTemporaryFileTracker : IDisposable
    {
        private readonly string _temporaryDirectory = Path.GetTempPath();
        private readonly HashSet<string> _preexistingFiles;
        private bool _deleted;

        public XcodeTemporaryFileTracker()
        {
            _preexistingFiles = EnumerateFiles()
                .ToHashSet(StringComparer.Ordinal);
        }

        public long Delete()
        {
            if (_deleted)
                return 0;

            long bytes = 0;
            foreach (string path in EnumerateFiles())
            {
                if (_preexistingFiles.Contains(path))
                    continue;

                try
                {
                    long length = new FileInfo(path).Length;
                    File.Delete(path);
                    bytes = checked(bytes + length);
                    Console.WriteLine(
                        $"[Instruments] deleted-xcode-scratch={path} bytes={length}");
                }
                catch (FileNotFoundException)
                {
                    // Xcode removed the file after enumeration.
                }
                catch (IOException exception)
                {
                    Console.Error.WriteLine(
                        "[Instruments] could not delete Xcode scratch " +
                        $"{path}: 0x{exception.HResult:x8}");
                }
                catch (UnauthorizedAccessException exception)
                {
                    Console.Error.WriteLine(
                        "[Instruments] could not delete Xcode scratch " +
                        $"{path}: 0x{exception.HResult:x8}");
                }
            }

            _deleted = true;
            return bytes;
        }

        public void Dispose() => Delete();

        private IEnumerable<string> EnumerateFiles()
        {
            if (!Directory.Exists(_temporaryDirectory))
                return [];

            return Directory.EnumerateFiles(
                _temporaryDirectory,
                "instruments*.ktrace",
                SearchOption.TopDirectoryOnly);
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut);
}
