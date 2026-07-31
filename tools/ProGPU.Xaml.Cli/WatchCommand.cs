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
                debounce);
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
            var initial = await SubmitWatchSnapshotAsync(
                session,
                projectPath,
                file,
                immediate: true,
                cancellation.Token);
            sequence++;
            artifactWritten =
                artifactWritten &&
                initial.Accepted;
            WriteWatchResult(
                initial,
                sequence,
                profile.Id,
                file,
                output,
                artifactWritten,
                json);
            lastAccepted = initial.Accepted;
            if (maximumUpdates == sequence)
                return lastAccepted ? 0 : 1;

            if (useStandardInput)
            {
                return await RunStandardInputWatchAsync(
                    session,
                    projectPath,
                    file,
                    output,
                    profile.Id,
                    maximumUpdates,
                    sequence,
                    json,
                    cancellation.Token,
                    lastAccepted);
            }

            return await RunFileSystemWatchAsync(
                session,
                projectPath,
                file,
                output,
                profile.Id,
                maximumUpdates,
                sequence,
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
            string projectPath,
            string file,
            string output,
            string framework,
            int? maximumUpdates,
            long sequence,
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
                return lastAccepted ? 0 : 1;
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
                var result =
                    await SubmitWatchSnapshotAsync(
                        session,
                        projectPath,
                        file,
                        immediate: true,
                        cancellationToken);
                sequence++;
                var wroteArtifact =
                    result.Accepted &&
                    result.Update?
                        .RequiresRuntimePublication ==
                    true;
                WriteWatchResult(
                    result,
                    sequence,
                    framework,
                    file,
                    output,
                    wroteArtifact,
                    json);
                lastAccepted = result.Accepted;
            }

            if (maximumUpdates == sequence)
                return lastAccepted ? 0 : 1;
        }

        return 0;
    }

    private static async Task<int>
        RunFileSystemWatchAsync(
            RoslynXamlProjectWatchSession session,
            string projectPath,
            string file,
            string output,
            string framework,
            int? maximumUpdates,
            long sequence,
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
            new WatchFileSystemSubscription(
                signals.Writer);
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

            RoslynXamlProjectWatchResult result;
            try
            {
                if (refreshSubscription)
                {
                    var submission =
                        await SubmitWatchSnapshotAndLoadInputSetAsync(
                            session,
                            projectPath,
                            file,
                            cancellationToken);
                    subscription.Update(
                        submission.InputSet);
                    result = submission.Result;
                }
                else
                {
                    result =
                        await SubmitWatchSnapshotAsync(
                            session,
                            projectPath,
                            file,
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
                result.Update?
                    .RequiresRuntimePublication ==
                true;
            WriteWatchResult(
                result,
                sequence,
                framework,
                file,
                output,
                wroteArtifact,
                json);
            lastAccepted = result.Accepted;
            if (maximumUpdates == sequence)
                return lastAccepted ? 0 : 1;
        }

        return 0;
    }

    internal sealed class
        WatchFileSystemSubscription : IDisposable
    {
        private readonly ChannelWriter<string>
            _signals;
        private readonly StringComparer
            _pathComparer;
        private List<FileSystemWatcher>
            _watchers = new();
        private RoslynXamlProjectWatchInputSet?
            _inputSet;
        private volatile HashSet<string>
            _refreshFiles;
        private int _refreshRequested;

        public WatchFileSystemSubscription(
            ChannelWriter<string> signals)
        {
            _signals = signals;
            _pathComparer =
                Path.DirectorySeparatorChar == '\\'
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;
            _refreshFiles =
                new HashSet<string>(_pathComparer);
        }

        public bool Update(
            RoslynXamlProjectWatchInputSet inputSet)
        {
            if (inputSet == null)
            {
                throw new ArgumentNullException(
                    nameof(inputSet));
            }

            if (_inputSet != null &&
                HasSameSubscription(
                    _inputSet,
                    inputSet,
                    _pathComparer))
            {
                return false;
            }

            var next = CreateWatchers(inputSet);
            var refreshFiles = new HashSet<string>(
                inputSet.Inputs
                    .Where(
                        static input =>
                            input.Kind ==
                                RoslynXamlProjectWatchInputKind
                                    .ProjectFile ||
                            input.Kind ==
                                RoslynXamlProjectWatchInputKind
                                    .EvaluatedBuildInput)
                    .Select(
                        static input => input.Path),
                _pathComparer);
            var previous = _watchers;
            _watchers = next;
            _inputSet = inputSet;
            _refreshFiles = refreshFiles;
            DisposeWatchers(previous);
            return true;
        }

        public bool TakeRefreshRequested() =>
            Interlocked.Exchange(
                ref _refreshRequested,
                0) != 0;

        public void Dispose()
        {
            var previous = _watchers;
            _watchers = new List<FileSystemWatcher>();
            _inputSet = null;
            _refreshFiles =
                new HashSet<string>(_pathComparer);
            DisposeWatchers(previous);
        }

        private List<FileSystemWatcher>
            CreateWatchers(
                RoslynXamlProjectWatchInputSet inputSet)
        {
            var exactFiles = new HashSet<string>(
                inputSet.Files,
                _pathComparer);
            var exactGroups = inputSet
                .ExplicitFiles
                .GroupBy(
                    static path =>
                        Path.GetDirectoryName(path) ??
                        string.Empty,
                    _pathComparer)
                .OrderBy(
                    static group => group.Key,
                    StringComparer.Ordinal)
                .ToArray();
            var watchers = new List<FileSystemWatcher>(
                inputSet.RecursiveDirectories.Length +
                exactGroups.Length);
            try
            {
                foreach (var directory in
                         inputSet.RecursiveDirectories)
                {
                    watchers.Add(
                        CreateProjectWatcher(
                            directory,
                            changedPath =>
                                IsWatchInput(
                                    changedPath) ||
                                (!IsBuildOutput(
                                     changedPath) &&
                                 exactFiles.Contains(
                                     Path.GetFullPath(
                                         changedPath))),
                            Signal));
                }

                foreach (var group in exactGroups)
                {
                    var groupFiles =
                        new HashSet<string>(
                            group,
                            _pathComparer);
                    watchers.Add(
                        CreateExactFilesWatcher(
                            group.Key,
                            changedPath =>
                                groupFiles.Contains(
                                    Path.GetFullPath(
                                        changedPath)),
                            Signal));
                }

                return watchers;
            }
            catch
            {
                DisposeWatchers(watchers);
                throw;
            }
        }

        private void Signal(string changedPath)
        {
            var fullPath =
                Path.GetFullPath(changedPath);
            if (_refreshFiles.Contains(fullPath) ||
                IsBuildGraphInput(fullPath))
            {
                Interlocked.Exchange(
                    ref _refreshRequested,
                    1);
            }

            _signals.TryWrite(fullPath);
        }

        private static bool IsBuildGraphInput(
            string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(
                       ".csproj",
                       StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(
                       ".vbproj",
                       StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(
                       ".fsproj",
                       StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(
                       ".props",
                       StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(
                       ".targets",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasSameSubscription(
            RoslynXamlProjectWatchInputSet left,
            RoslynXamlProjectWatchInputSet right,
            StringComparer pathComparer) =>
            left.RecursiveDirectories.SequenceEqual(
                right.RecursiveDirectories,
                pathComparer) &&
            left.Files.SequenceEqual(
                right.Files,
                pathComparer) &&
            left.ExplicitFiles.SequenceEqual(
                right.ExplicitFiles,
                pathComparer);

        private static void DisposeWatchers(
            IEnumerable<FileSystemWatcher> watchers)
        {
            foreach (var watcher in watchers)
                watcher.Dispose();
        }
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
        RoslynXamlProjectWatchResult>
        SubmitWatchSnapshotAsync(
            RoslynXamlProjectWatchSession session,
            string projectPath,
            string file,
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
        return await session.SubmitAsync(
            project,
            documentId,
            immediate: immediate,
                cancellationToken:
                    cancellationToken);
    }

    private static async Task<WatchSnapshotSubmission>
        SubmitWatchSnapshotAndLoadInputSetAsync(
            RoslynXamlProjectWatchSession session,
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
            out var documentId);
        var evaluatedBuildInputs =
            CliMsBuildProjectInputs
                .Resolve(project);
        var inputSet =
            RoslynXamlProjectWatchInputSet.Create(
                project,
                evaluatedBuildInputs.Paths,
                explicitInputs: new[] { file });
        var result = await session.SubmitAsync(
            project,
            documentId,
            immediate: false,
            cancellationToken:
                cancellationToken);
        return new WatchSnapshotSubmission(
            result,
            inputSet);
    }

    private readonly record struct
        WatchSnapshotSubmission(
            RoslynXamlProjectWatchResult Result,
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

    private static FileSystemWatcher
        CreateProjectWatcher(
            string projectDirectory,
            Func<string, bool> isInput,
            Action<string> changed)
    {
        var watcher =
            new FileSystemWatcher(
                projectDirectory)
            {
                IncludeSubdirectories = true,
                Filter = "*",
                NotifyFilter =
                    NotifyFilters.FileName |
                    NotifyFilters.DirectoryName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size
            };
        FileSystemEventHandler onChange =
            (_, eventArgs) =>
            {
                if (isInput(eventArgs.FullPath))
                    changed(eventArgs.FullPath);
            };
        RenamedEventHandler onRename =
            (_, eventArgs) =>
            {
                if (isInput(eventArgs.OldFullPath))
                    changed(eventArgs.OldFullPath);
                if (isInput(eventArgs.FullPath))
                    changed(eventArgs.FullPath);
            };
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += onRename;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static FileSystemWatcher
        CreateExactFilesWatcher(
            string directory,
            Func<string, bool> isInput,
            Action<string> changed)
    {
        var watcher =
            new FileSystemWatcher(
                directory,
                "*")
            {
                IncludeSubdirectories = false,
                NotifyFilter =
                    NotifyFilters.FileName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size
            };
        FileSystemEventHandler onChange =
            (_, eventArgs) =>
            {
                if (isInput(eventArgs.FullPath))
                    changed(eventArgs.FullPath);
            };
        RenamedEventHandler onRename =
            (_, eventArgs) =>
            {
                if (isInput(eventArgs.OldFullPath))
                    changed(eventArgs.OldFullPath);
                if (isInput(eventArgs.FullPath))
                    changed(eventArgs.FullPath);
            };
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += onRename;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static bool IsWatchInput(string path)
    {
        if (IsBuildOutput(path))
            return false;
        var extension = Path.GetExtension(path);
        return extension.Equals(
                   ".cs",
                   StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(
                   ".xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(
                   ".axaml",
                   StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(
                   ".csproj",
                   StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(
                   ".props",
                   StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(
                   ".targets",
                   StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(
                   ".editorconfig",
                   StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(
                   ".resw",
                   StringComparison.OrdinalIgnoreCase);
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

    private static void WriteWatchResult(
        RoslynXamlProjectWatchResult result,
        long sequence,
        string framework,
        string file,
        string output,
        bool artifactWritten,
        bool json)
    {
        var update = result.Update;
        var plan = update?.Delta;
        var diagnostics =
            GetWatchDiagnostics(update)
                .ToArray();
        if (json)
        {
            WriteJsonLine(new
            {
                command = "watch",
                sequence,
                version = result.Version,
                framework,
                path = file,
                status = result.Status.ToString(),
                commitResult =
                    result.CommitResult?.ToString(),
                generation =
                    result.CommittedGeneration,
                mode = plan?.Mode.ToString(),
                action = plan?.Action.ToString(),
                metadataReasons =
                    plan?.MetadataReasons.Select(
                        static reason =>
                            reason.ToString()),
                isInitial = update?.IsInitial,
                requiresRuntimePublication =
                    update?
                        .RequiresRuntimePublication,
                artifactWritten,
                output,
                resourceUri =
                    update?.Current.ResourceUri,
                qualifiedTypeName =
                    update?.Current
                        .QualifiedTypeName,
                targetDocumentChanged =
                    plan?.TargetDocumentChanged,
                targetDependencyChanged =
                    plan?.TargetDependencyChanged,
                metadataChanged =
                    plan?.MetadataChanged,
                durationMilliseconds =
                    result.Duration
                        .TotalMilliseconds,
                message = result.Message,
                diagnostics =
                    ProjectDiagnostics(
                        diagnostics)
            });
            return;
        }

        PrintDiagnostics(diagnostics);
        Console.WriteLine(
            "[watch " +
            sequence.ToString(
                CultureInfo.InvariantCulture) +
            "] " +
            result.Status +
            " generation=" +
            result.CommittedGeneration.ToString(
                    CultureInfo.InvariantCulture) +
            " action=" +
            (plan?.Action.ToString() ??
             (update?.IsInitial == true
                 ? "Initial"
                 : "None")) +
            " artifact=" +
            (artifactWritten
                ? output
                : "unchanged") +
            " — " +
            result.Message);
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

    private static IEnumerable<Diagnostic>
        GetWatchDiagnostics(
            RoslynXamlProjectPreviewUpdate? update)
    {
        if (update == null)
            return Array.Empty<Diagnostic>();
        IEnumerable<Diagnostic> diagnostics =
            update.Delta == null
                ? Array.Empty<Diagnostic>()
                : update.Delta.Diagnostics;
        diagnostics = diagnostics.Concat(
            update.Current.Artifact?.Diagnostics ??
            update.Current.CompilationInspection
                .CompilationResult.Diagnostics);
        return diagnostics
            .Where(
                static diagnostic =>
                    diagnostic.Severity !=
                    DiagnosticSeverity.Hidden)
            .GroupBy(
                static diagnostic =>
                    diagnostic.ToString(),
                StringComparer.Ordinal)
            .Select(
                static group =>
                    group.First());
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
