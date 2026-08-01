using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Workspaces;

namespace ProGPU.Xaml.Cli;

internal static partial class Program
{
    private static async Task<int> WatchCommandAsync(
        string[] args)
    {
        if (args.Length < 4 ||
            !TryGetOption(
                args,
                "--project",
                out var projectPath))
        {
            return MissingArgument(
                "watch requires <file> --project <project.csproj> [--output <assembly.dll>]");
        }

        var file = Path.GetFullPath(args[1]);
        projectPath = Path.GetFullPath(projectPath!);
        var projectDirectory =
            Path.GetDirectoryName(projectPath) ??
            throw new InvalidOperationException(
                "The project path must have a parent directory.");
        var output = TryGetOption(
                args,
                "--output",
                out var outputValue)
            ? Path.GetFullPath(outputValue!)
            : Path.Combine(
                projectDirectory,
                "obj",
                "ProGPU.Xaml.Cli",
                Path.GetFileNameWithoutExtension(file) +
                ".preview.dll");
        var debounce = ParseWatchDebounce(args);
        var maximumUpdates =
            ParsePositiveOption(
                args,
                "--max-updates");
        var performanceBudget =
            ParseWatchPerformanceBudget(args);
        var useStandardInput =
            HasOption(args, "--stdin");
        var json = HasOption(args, "--json");
        var profile = GetProfile(args);
        var coordinator =
            new RoslynXamlProjectPreviewCoordinator(
                profile,
                new RoslynXamlProjectPreviewOptions
                {
                    InspectionOptions =
                        new RoslynXamlCompilationInspectionOptions
                        {
                            CompilerOptions =
                                new XamlCompilerOptions
                                {
                                    Framework = profile.Id,
                                    Strict = true
                                }
                        },
                    LogicalPathProvider = document =>
                        GetProjectLogicalPath(
                            projectDirectory,
                            document)
                });
        var artifactWritten = false;
        using var session =
            new RoslynXamlProjectWatchSession(
                coordinator,
                (update, cancellationToken) =>
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    if (!update.TryGetExecutableUpdate(
                            out var peImage,
                            out _))
                    {
                        return Task.FromResult(false);
                    }

                    WriteFileTransactionally(
                        output,
                        peImage);
                    artifactWritten = true;
                    return Task.FromResult(true);
                },
                debounce,
                GcWatchAllocationCounter.Instance);
        var transport =
            new RoslynXamlProjectWatchTransport(
                session);
        using var cancellation =
            new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler =
            (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var sequence = 0L;
            var lastAccepted = false;
            sequence++;
            var initial = await SubmitWatchSnapshotAsync(
                transport,
                projectPath,
                file,
                sequence,
                immediate: true,
                cancellation.Token);
            artifactWritten =
                artifactWritten &&
                initial.Accepted;
            WriteWatchResult(
                initial,
                profile.Id,
                file,
                output,
                artifactWritten,
                performanceBudget,
                json);
            lastAccepted = initial.Accepted;
            if (maximumUpdates == sequence)
            {
                return GetWatchExitCode(
                    lastAccepted,
                    performanceBudget,
                    initial.Telemetry);
            }

            if (useStandardInput)
            {
                return await RunStandardInputWatchAsync(
                    session,
                    transport,
                    projectPath,
                    file,
                    output,
                    profile.Id,
                    maximumUpdates,
                    sequence,
                    performanceBudget,
                    json,
                    cancellation.Token,
                    lastAccepted);
            }

            return await RunFileSystemWatchAsync(
                session,
                transport,
                projectPath,
                file,
                output,
                profile.Id,
                maximumUpdates,
                sequence,
                performanceBudget,
                json,
                cancellation.Token,
                lastAccepted);
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int>
        RunStandardInputWatchAsync(
            RoslynXamlProjectWatchSession session,
            RoslynXamlProjectWatchTransport transport,
            string projectPath,
            string file,
            string output,
            string framework,
            int? maximumUpdates,
            long sequence,
            RoslynXamlProjectWatchPerformanceBudget?
                performanceBudget,
            bool json,
            CancellationToken cancellationToken,
            bool lastAccepted)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var command = await Console.In.ReadLineAsync(
                cancellationToken);
            if (command == null ||
                string.Equals(
                    command.Trim(),
                    "quit",
                    StringComparison.OrdinalIgnoreCase))
            {
                return GetWatchExitCode(
                    lastAccepted,
                    performanceBudget,
                    session.Telemetry);
            }

            if (!string.Equals(
                    command.Trim(),
                    "reload",
                    StringComparison.OrdinalIgnoreCase))
            {
                WriteWatchHostFailure(
                    ++sequence,
                    framework,
                    file,
                    output,
                    "Unknown watch input command '" +
                    command +
                    "'. Expected 'reload' or 'quit'.",
                    json);
                lastAccepted = false;
            }
            else
            {
                sequence++;
                var result =
                    await SubmitWatchSnapshotAsync(
                        transport,
                        projectPath,
                        file,
                        sequence,
                        immediate: true,
                        cancellationToken);
                var wroteArtifact =
                    result.Accepted &&
                    result.RequiresRuntimePublication ==
                    true;
                WriteWatchResult(
                    result,
                    framework,
                    file,
                    output,
                    wroteArtifact,
                    performanceBudget,
                    json);
                lastAccepted = result.Accepted;
            }

            if (maximumUpdates == sequence)
            {
                return GetWatchExitCode(
                    lastAccepted,
                    performanceBudget,
                    session.Telemetry);
            }
        }

        return 0;
    }

    private static async Task<int>
        RunFileSystemWatchAsync(
            RoslynXamlProjectWatchSession session,
            RoslynXamlProjectWatchTransport transport,
            string projectPath,
            string file,
            string output,
            string framework,
            int? maximumUpdates,
            long sequence,
            RoslynXamlProjectWatchPerformanceBudget?
                performanceBudget,
            bool json,
            CancellationToken cancellationToken,
            bool lastAccepted)
    {
        var signals =
            Channel.CreateBounded<string>(
                new BoundedChannelOptions(1)
                {
                    FullMode =
                        BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });
        var inputSet = await LoadWatchInputSetAsync(
            projectPath,
            file,
            cancellationToken);
        using var subscription =
            new RoslynXamlProjectWatchFileSystemSubscription(
                changedPath =>
                    signals.Writer.TryWrite(
                        changedPath),
                new[] { output });
        subscription.Update(inputSet);
        while (!cancellationToken
                   .IsCancellationRequested)
        {
            _ = await signals.Reader.ReadAsync(
                cancellationToken);
            while (signals.Reader.TryRead(out _))
            {
            }
            var refreshSubscription =
                subscription
                    .TakeRefreshRequested();

            RoslynXamlProjectWatchResultSnapshot result;
            try
            {
                var requestSequence =
                    checked(sequence + 1);
                if (refreshSubscription)
                {
                    var submission =
                        await SubmitWatchSnapshotAndLoadInputSetAsync(
                            transport,
                            projectPath,
                            file,
                            requestSequence,
                            cancellationToken);
                    subscription.Update(
                        submission.InputSet);
                    result = submission.Result;
                }
                else
                {
                    result =
                        await SubmitWatchSnapshotAsync(
                            transport,
                            projectPath,
                            file,
                            requestSequence,
                            immediate: false,
                            cancellationToken);
                }
            }
            catch (Exception exception)
                when (exception is not
                      OperationCanceledException)
            {
                sequence++;
                WriteWatchHostFailure(
                    sequence,
                    framework,
                    file,
                    output,
                    exception.GetBaseException()
                        .Message,
                    json);
                lastAccepted = false;
                if (maximumUpdates == sequence)
                    return 1;
                continue;
            }

            sequence++;
            var wroteArtifact =
                result.Accepted &&
                result.RequiresRuntimePublication ==
                true;
            WriteWatchResult(
                result,
                framework,
                file,
                output,
                wroteArtifact,
                performanceBudget,
                json);
            lastAccepted = result.Accepted;
            if (maximumUpdates == sequence)
            {
                return GetWatchExitCode(
                    lastAccepted,
                    performanceBudget,
                    session.Telemetry);
            }
        }

        return 0;
    }

    private static async Task<
        RoslynXamlProjectWatchInputSet>
        LoadWatchInputSetAsync(
            string projectPath,
            string file,
            CancellationToken cancellationToken)
    {
        using var loaded =
            await OpenWatchProjectAsync(
                projectPath,
                cancellationToken);
        var project = EnsureAdditionalDocument(
            loaded.Project,
            file,
            out _);
        var evaluatedBuildInputs =
            CliMsBuildProjectInputs
                .Resolve(project);
        return RoslynXamlProjectWatchInputSet.Create(
            project,
            evaluatedBuildInputs.Paths,
            explicitInputs: new[] { file });
    }

    private static async Task<
        RoslynXamlProjectWatchResultSnapshot>
        SubmitWatchSnapshotAsync(
            RoslynXamlProjectWatchTransport transport,
            string projectPath,
            string file,
            long sequence,
            bool immediate,
            CancellationToken cancellationToken)
    {
        using var loaded =
            await OpenWatchProjectAsync(
                projectPath,
                cancellationToken);
        var project = EnsureAdditionalDocument(
            loaded.Project,
            file,
            out var documentId);
        return await transport.SubmitAsync(
            new RoslynXamlProjectWatchRequest(
                sequence,
                project,
                documentId,
                immediate: immediate),
            cancellationToken);
    }

    private static async Task<WatchSnapshotSubmission>
        SubmitWatchSnapshotAndLoadInputSetAsync(
            RoslynXamlProjectWatchTransport transport,
            string projectPath,
            string file,
            long sequence,
            CancellationToken cancellationToken)
    {
        using var loaded =
            await OpenWatchProjectAsync(
                projectPath,
                cancellationToken);
        var project = EnsureAdditionalDocument(
            loaded.Project,
            file,
            out var documentId);
        var evaluatedBuildInputs =
            CliMsBuildProjectInputs
                .Resolve(project);
        var inputSet =
            RoslynXamlProjectWatchInputSet.Create(
                project,
                evaluatedBuildInputs.Paths,
                explicitInputs: new[] { file });
        var result = await transport.SubmitAsync(
            new RoslynXamlProjectWatchRequest(
                sequence,
                project,
                documentId,
                immediate: false),
            cancellationToken);
        return new WatchSnapshotSubmission(
            result,
            inputSet);
    }

    private readonly record struct
        WatchSnapshotSubmission(
            RoslynXamlProjectWatchResultSnapshot Result,
            RoslynXamlProjectWatchInputSet InputSet);

    private static async Task<LoadedWatchProject>
        OpenWatchProjectAsync(
            string projectPath,
            CancellationToken cancellationToken)
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
        var workspace = CliMsBuildWorkspace.Create();
        workspace.LoadMetadataForReferencedProjects =
            false;
        workspace.RegisterWorkspaceFailedHandler(
            eventArgs =>
                Console.Error.WriteLine(
                    "workspace: " +
                    eventArgs.Diagnostic.Message));
        try
        {
            var project =
                await workspace.OpenProjectAsync(
                    projectPath,
                    cancellationToken:
                        cancellationToken);
            return new LoadedWatchProject(
                workspace,
                project);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static TimeSpan ParseWatchDebounce(
        string[] args)
    {
        if (!TryGetOption(
                args,
                "--debounce-ms",
                out var value))
        {
            return TimeSpan.FromMilliseconds(250);
        }

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var milliseconds) ||
            milliseconds < 0 ||
            milliseconds > 60_000)
        {
            throw new ArgumentException(
                "--debounce-ms must be an integer between 0 and 60000.");
        }

        return TimeSpan.FromMilliseconds(
            milliseconds);
    }

    private static int? ParsePositiveOption(
        string[] args,
        string name)
    {
        if (!TryGetOption(
                args,
                name,
                out var value))
        {
            return null;
        }

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= 0)
        {
            throw new ArgumentException(
                name +
                " must be a positive integer.");
        }

        return parsed;
    }

    private static
        RoslynXamlProjectWatchPerformanceBudget?
        ParseWatchPerformanceBudget(
            string[] args)
    {
        var minimumSampleCount =
            ParsePositiveOption(
                args,
                "--budget-min-samples");
        TimeSpan? maximumP95Duration = null;
        if (TryGetOption(
                args,
                "--max-p95-ms",
                out var durationValue))
        {
            if (!double.TryParse(
                    durationValue,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var milliseconds) ||
                !double.IsFinite(milliseconds) ||
                milliseconds < 0 ||
                milliseconds >
                TimeSpan.MaxValue.TotalMilliseconds)
            {
                throw new ArgumentException(
                    "--max-p95-ms must be a finite non-negative number.");
            }

            maximumP95Duration =
                TimeSpan.FromMilliseconds(milliseconds);
        }

        long? maximumP95AllocatedBytes = null;
        if (TryGetOption(
                args,
                "--max-p95-allocated-bytes",
                out var allocationValue))
        {
            if (!long.TryParse(
                    allocationValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var allocatedBytes) ||
                allocatedBytes < 0)
            {
                throw new ArgumentException(
                    "--max-p95-allocated-bytes must be a non-negative integer.");
            }

            maximumP95AllocatedBytes =
                allocatedBytes;
        }

        if (!maximumP95Duration.HasValue &&
            !maximumP95AllocatedBytes.HasValue)
        {
            if (minimumSampleCount.HasValue)
            {
                throw new ArgumentException(
                    "--budget-min-samples requires a P95 duration or allocation budget.");
            }

            return null;
        }

        return new
            RoslynXamlProjectWatchPerformanceBudget(
                minimumSampleCount ?? 1,
                maximumP95Duration,
                maximumP95AllocatedBytes);
    }

    private static int GetWatchExitCode(
        bool lastAccepted,
        RoslynXamlProjectWatchPerformanceBudget?
            performanceBudget,
        in RoslynXamlProjectWatchTelemetry telemetry)
    {
        if (!lastAccepted)
            return 1;
        if (performanceBudget == null)
            return 0;
        return performanceBudget
            .Evaluate(telemetry)
            .Passed
            ? 0
            : 1;
    }

    private static void WriteWatchResult(
        RoslynXamlProjectWatchResultSnapshot snapshot,
        string framework,
        string file,
        string output,
        bool artifactWritten,
        RoslynXamlProjectWatchPerformanceBudget?
            performanceBudget,
        bool json)
    {
        var telemetry = snapshot.Telemetry;
        var budgetResult =
            performanceBudget?.Evaluate(telemetry);
        if (json)
        {
            WriteJsonLine(new
            {
                command = "watch",
                protocolVersion =
                    snapshot.ProtocolVersion.ToString(),
                sequence = snapshot.Sequence,
                version = snapshot.Version,
                framework,
                path = file,
                status = snapshot.Status.ToString(),
                commitResult =
                    snapshot.CommitResult?.ToString(),
                generation =
                    snapshot.CommittedGeneration,
                mode = snapshot.Mode?.ToString(),
                action = snapshot.Action?.ToString(),
                metadataReasons =
                    snapshot.MetadataReasons.Select(
                        static reason =>
                            reason.ToString()),
                isInitial = snapshot.IsInitial,
                requiresRuntimePublication =
                    snapshot.RequiresRuntimePublication,
                artifactWritten,
                output,
                resourceUri =
                    snapshot.ResourceUri,
                qualifiedTypeName =
                    snapshot.QualifiedTypeName,
                targetDocumentChanged =
                    snapshot.TargetDocumentChanged,
                targetDependencyChanged =
                    snapshot.TargetDependencyChanged,
                metadataChanged =
                    snapshot.MetadataChanged,
                durationMilliseconds =
                    snapshot.Duration
                        .TotalMilliseconds,
                telemetry = new
                {
                    submitted =
                        telemetry.SubmittedCount,
                    completed =
                        telemetry.CompletedCount,
                    applied =
                        telemetry.AppliedCount,
                    cacheHits =
                        telemetry.CacheHitCount,
                    rejected =
                        telemetry.RejectedCount,
                    canceledWork =
                        telemetry.CanceledWorkCount,
                    superseded =
                        telemetry.SupersededCount,
                    stopped =
                        telemetry.StoppedCount,
                    callerCanceled =
                        telemetry.CallerCanceledCount,
                    faulted =
                        telemetry.FaultedCount,
                    currentQueueDepth =
                        telemetry.CurrentQueueDepth,
                    maximumQueueDepth =
                        telemetry.MaximumQueueDepth,
                    totalDurationMilliseconds =
                        telemetry.TotalDuration
                            .TotalMilliseconds,
                    lastDurationMilliseconds =
                        telemetry.LastDuration
                            .TotalMilliseconds,
                    maximumDurationMilliseconds =
                        telemetry.MaximumDuration
                            .TotalMilliseconds,
                    averageDurationMilliseconds =
                        telemetry.AverageDuration
                            .TotalMilliseconds,
                    medianDurationUpperBoundMilliseconds =
                        telemetry
                            .MedianDurationUpperBound
                            .TotalMilliseconds,
                    p95DurationUpperBoundMilliseconds =
                        telemetry
                            .P95DurationUpperBound
                            .TotalMilliseconds,
                    p99DurationUpperBoundMilliseconds =
                        telemetry
                            .P99DurationUpperBound
                            .TotalMilliseconds,
                    allocationMeasurements =
                        telemetry
                            .AllocationMeasurementCount,
                    totalAllocatedBytes =
                        telemetry.TotalAllocatedBytes,
                    lastAllocatedBytes =
                        telemetry.LastAllocatedBytes,
                    maximumAllocatedBytes =
                        telemetry.MaximumAllocatedBytes,
                    averageAllocatedBytes =
                        telemetry.AverageAllocatedBytes,
                    medianAllocatedBytesUpperBound =
                        telemetry
                            .MedianAllocatedBytesUpperBound,
                    p95AllocatedBytesUpperBound =
                        telemetry
                            .P95AllocatedBytesUpperBound,
                    p99AllocatedBytesUpperBound =
                        telemetry
                            .P99AllocatedBytesUpperBound
                },
                performanceBudget =
                    performanceBudget == null
                        ? null
                        : new
                        {
                            status =
                                budgetResult!.Value
                                    .Status.ToString(),
                            violations =
                                budgetResult.Value
                                    .Violations.ToString(),
                            minimumSamples =
                                performanceBudget
                                    .MinimumSampleCount,
                            completedSamples =
                                budgetResult.Value
                                    .CompletedSampleCount,
                            allocationSamples =
                                budgetResult.Value
                                    .AllocationSampleCount,
                            maximumP95DurationMilliseconds =
                                performanceBudget
                                    .MaximumP95Duration?
                                    .TotalMilliseconds,
                            observedP95DurationUpperBoundMilliseconds =
                                budgetResult.Value
                                    .P95DurationUpperBound
                                    .TotalMilliseconds,
                            maximumP95AllocatedBytes =
                                performanceBudget
                                    .MaximumP95AllocatedBytes,
                            observedP95AllocatedBytesUpperBound =
                                budgetResult.Value
                                    .P95AllocatedBytesUpperBound
                        },
                message = snapshot.Message,
                diagnosticsTruncated =
                    snapshot.DiagnosticsTruncated,
                textTruncated =
                    snapshot.TextTruncated,
                diagnostics =
                    ProjectWatchDiagnostics(
                        snapshot.Diagnostics)
            });
            return;
        }

        PrintProjectWatchDiagnostics(
            snapshot.Diagnostics,
            snapshot.DiagnosticsTruncated);
        Console.WriteLine(
            "[watch " +
            snapshot.Sequence.ToString(
                CultureInfo.InvariantCulture) +
            "] " +
            snapshot.Status +
            " generation=" +
            snapshot.CommittedGeneration.ToString(
                    CultureInfo.InvariantCulture) +
            " action=" +
            (snapshot.Action?.ToString() ??
             (snapshot.IsInitial == true
                 ? "Initial"
                 : "None")) +
            " artifact=" +
            (artifactWritten
                ? output
                : "unchanged") +
            " cacheHits=" +
            telemetry.CacheHitCount.ToString(
                CultureInfo.InvariantCulture) +
            " queue=" +
            telemetry.CurrentQueueDepth.ToString(
                CultureInfo.InvariantCulture) +
            "/" +
            telemetry.MaximumQueueDepth.ToString(
                CultureInfo.InvariantCulture) +
            " p95Ms=" +
            telemetry.P95DurationUpperBound
                .TotalMilliseconds.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) +
            " allocP95=" +
            (telemetry.HasAllocationMeasurements
                ? telemetry
                    .P95AllocatedBytesUpperBound
                    .ToString(
                        CultureInfo.InvariantCulture)
                : "unavailable") +
            " budget=" +
            (budgetResult.HasValue
                ? budgetResult.Value.Status.ToString()
                : "disabled") +
            " — " +
            snapshot.Message);
    }

    private sealed class GcWatchAllocationCounter :
        IRoslynXamlProjectWatchAllocationCounter
    {
        public static GcWatchAllocationCounter
            Instance
        {
            get;
        } = new();

        private GcWatchAllocationCounter()
        {
        }

        public long GetTotalAllocatedBytes() =>
            GC.GetTotalAllocatedBytes(
                precise: false);
    }

    private static void WriteWatchHostFailure(
        long sequence,
        string framework,
        string file,
        string output,
        string message,
        bool json)
    {
        if (json)
        {
            WriteJsonLine(new
            {
                command = "watch",
                protocolVersion =
                    RoslynXamlProjectWatchProtocolVersion
                        .Current.ToString(),
                sequence,
                framework,
                path = file,
                status = "HostFailure",
                artifactWritten = false,
                output,
                message
            });
        }
        else
        {
            Console.Error.WriteLine(
                "PGXAMLCLI0004: " +
                message);
        }
    }

    private static object[] ProjectWatchDiagnostics(
        IEnumerable<RoslynXamlProjectWatchDiagnosticSnapshot>
            diagnostics) =>
        diagnostics.Select(
            static diagnostic =>
                (object)new
                {
                    id = diagnostic.Id,
                    severity =
                        diagnostic.Severity.ToString(),
                    message = diagnostic.Message,
                    path = diagnostic.Path,
                    startLine = diagnostic.StartLine,
                    startCharacter =
                        diagnostic.StartCharacter,
                    endLine = diagnostic.EndLine,
                    endCharacter =
                        diagnostic.EndCharacter,
                    textTruncated =
                        diagnostic.TextTruncated
                })
            .ToArray();

    private static void PrintProjectWatchDiagnostics(
        IEnumerable<RoslynXamlProjectWatchDiagnosticSnapshot>
            diagnostics,
        bool truncated)
    {
        foreach (var diagnostic in diagnostics)
        {
            var writer =
                diagnostic.Severity ==
                DiagnosticSeverity.Error
                    ? Console.Error
                    : Console.Out;
            writer.WriteLine(
                (string.IsNullOrEmpty(diagnostic.Path)
                    ? string.Empty
                    : diagnostic.Path +
                      "(" +
                      (diagnostic.StartLine + 1)
                          .ToString(
                              CultureInfo.InvariantCulture) +
                      "," +
                      (diagnostic.StartCharacter + 1)
                          .ToString(
                              CultureInfo.InvariantCulture) +
                      "): ") +
                diagnostic.Severity.ToString()
                    .ToLowerInvariant() +
                " " +
                diagnostic.Id +
                ": " +
                diagnostic.Message);
        }
        if (truncated)
        {
            Console.Error.WriteLine(
                "Additional diagnostics were omitted by the project-watch transport bound.");
        }
    }

    private static void WriteJsonLine(
        object value) =>
        Console.WriteLine(
            System.Text.Json.JsonSerializer.Serialize(
                value));

    private sealed class LoadedWatchProject :
        IDisposable
    {
        public LoadedWatchProject(
            MSBuildWorkspace workspace,
            Project project)
        {
            Workspace = workspace;
            Project = project;
        }

        public MSBuildWorkspace Workspace { get; }
        public Project Project { get; }

        public void Dispose() =>
            Workspace.Dispose();
    }
}
