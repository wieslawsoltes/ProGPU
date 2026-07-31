using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.HotReload;
using ProGPU.WinUI.Designer;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Tooling;
using ProGPU.Xaml.Workspaces;

namespace ProGPU.Samples;

public static class XamlPlaygroundPage
{
    private const string InitialSource = """
<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      x:Class="ProGPU.Samples.Playground.Document">
  <StackPanel Margin="24">
    <TextBlock Text="Hello from the XAML playground" />
  </StackPanel>
</Page>
""";

    private static readonly Lazy<PlaygroundCompilationHost> CompilationHost =
        new(CreateCompilationHost);

    public static FrameworkElement Create()
    {
        var root = new StackPanel { Margin = new Thickness(20), Orientation = Orientation.Vertical };
        root.Children.Add(new TextBlock { FontSize = 22, Text = "XAML Playground" });
        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 12),
            Text = "Edit source and inspect bounded projections of the same lossless syntax and schema-neutral infoset used by builds and the CLI."
        });
        var editor = new TextBox
        {
            Text = InitialSource,
            AcceptsReturn = true,
            Height = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var status = new TextBlock { Margin = new Thickness(0, 12, 0, 0), Text = "Ready." };
        var syntaxOutput = CreateOutput("Syntax details will appear here.");
        var tokenOutput = CreateOutput("Lossless tokens will appear here.");
        var infosetOutput = CreateOutput("Infoset details will appear here.");
        var boundOutput = CreateOutput("Bound semantic details will appear here.");
        var resourcesOutput = CreateOutput("Resource graph details will appear here.");
        var irOutput = CreateOutput("Construction IR details will appear here.");
        var generatedOutput = CreateOutput("Generated Roslyn C# will appear here.");
        var diagnosticsOutput = CreateOutput("Diagnostics will appear here.");
        var hotReloadOutput = CreateOutput(RenderHotReloadStatus(
            "ready",
            HotReloadManager.LastResult,
            null));
        var previewStatus = new TextBlock
        {
            Text = "Live preview is disabled. Inspection never executes generated code.",
            Margin = new Thickness(0, 0, 0, 8)
        };
        var previewPermission = new Button
        {
            Content = "Enable live preview",
            Margin = new Thickness(0, 0, 0, 8)
        };
        var previewHost = new ContentControl
        {
            Content = CreatePreviewPlaceholder(
                WinUiXamlLivePreviewSession.RuntimeSupportMessage)
        };
        var previewPanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        previewPanel.Children.Add(previewStatus);
        previewPanel.Children.Add(previewPermission);
        previewPanel.Children.Add(previewHost);
        var views = new Pivot { Margin = new Thickness(0, 8, 0, 0), Height = 300 };
        views.Items.Add(new PivotItem("Syntax", syntaxOutput));
        views.Items.Add(new PivotItem("Tokens", tokenOutput));
        views.Items.Add(new PivotItem("Infoset", infosetOutput));
        views.Items.Add(new PivotItem("Bound", boundOutput));
        views.Items.Add(new PivotItem("Resources", resourcesOutput));
        views.Items.Add(new PivotItem("IR", irOutput));
        views.Items.Add(new PivotItem("Generated C#", generatedOutput));
        views.Items.Add(new PivotItem("Diagnostics", diagnosticsOutput));
        views.Items.Add(new PivotItem("Hot Reload", hotReloadOutput));
        views.Items.Add(new PivotItem("Live Preview", previewPanel));
        var inspect = new Button { Margin = new Thickness(0, 12, 0, 0), Content = "Parse and inspect" };
        long requestedVersion = 0;
        HotReloadDiagnostic? latestHotReloadDiagnostic = null;
        var previewSession = new WinUiXamlLivePreviewSession();
        var previewEnabled = false;
        PlaygroundProjectPipeline? projectPipeline = null;

        void PublishHotReloadStatus(string phase, HotReloadResult result)
        {
            void Publish() => hotReloadOutput.Text = RenderHotReloadStatus(
                phase,
                result,
                latestHotReloadDiagnostic);
            var dispatcher =
                Microsoft.UI.Xaml.Input.InputSystem.DispatcherQueue;
            if (dispatcher == null) Publish();
            else dispatcher(Publish);
        }

        void OnHotReloadStarted(HotReloadContext context) =>
            PublishHotReloadStatus(
                "applying generation " + context.Generation,
                HotReloadManager.LastResult);

        void OnHotReloadCompleted(HotReloadResult result) =>
            PublishHotReloadStatus("completed", result);

        void OnHotReloadDiagnostic(HotReloadDiagnostic diagnostic)
        {
            latestHotReloadDiagnostic = diagnostic;
            PublishHotReloadStatus("diagnostic", HotReloadManager.LastResult);
        }

        HotReloadManager.UpdateStarted += OnHotReloadStarted;
        HotReloadManager.UpdateCompleted += OnHotReloadCompleted;
        HotReloadManager.Diagnostic += OnHotReloadDiagnostic;

        void ApplyInspection(
            RoslynXamlProjectPreview accepted,
            RoslynXamlProjectWatchResultSnapshot result)
        {
            var inspection = accepted.SourceInspection;
            var statistics = inspection.Statistics;
            status.Text =
                $"Project watch protocol {result.ProtocolVersion}; " +
                $"generation {result.CommittedGeneration}; " +
                $"root: {inspection.SyntaxTree.GetRoot()?.QualifiedName ?? "<none>"}; " +
                $"tokens: {statistics.Tokens}; syntax objects: {statistics.SyntaxObjects}; " +
                $"infoset objects: {statistics.InfosetObjects}; errors: {statistics.Errors}.";
            syntaxOutput.Text = Render(inspection.Syntax);
            tokenOutput.Text = Render(inspection.Tokens);
            infosetOutput.Text = Render(inspection.InfosetProjection);
            var compiled = accepted.CompilationInspection;
            boundOutput.Text = Render(compiled.Bound);
            resourcesOutput.Text = Render(compiled.Resources);
            irOutput.Text = Render(compiled.Ir);
            generatedOutput.Text = RenderGenerated(compiled);
            diagnosticsOutput.Text = compiled.Diagnostics.TotalEntryCount == 0
                ? "No diagnostics."
                : Render(compiled.Diagnostics);
        }

        Task<bool> PublishPreviewAsync(
            RoslynXamlProjectPreviewUpdate update,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Volatile.Read(ref previewEnabled))
                return Task.FromResult(true);

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Publish()
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                WinUiXamlLivePreviewResult published;
                if (update.Delta != null)
                {
                    published = previewSession.TryApplyProjectDelta(
                        update.Delta,
                        replacement => previewHost.Content = replacement);
                }
                else if (update.TryGetExecutableUpdate(
                             out var peImage,
                             out var typeName))
                {
                    published = previewSession.TryUpdate(
                        peImage,
                        typeName,
                        replacement => previewHost.Content = replacement);
                }
                else
                {
                    previewStatus.Text =
                        update.FailureMessage ??
                        "The project preview has no accepted executable artifact; the last good tree was retained.";
                    completion.TrySetResult(false);
                    return;
                }

                previewStatus.Text = published.Message;
                completion.TrySetResult(published.Success);
            }

            var dispatcher =
                Microsoft.UI.Xaml.Input.InputSystem.DispatcherQueue;
            if (dispatcher == null) Publish();
            else dispatcher(Publish);
            return completion.Task;
        }

        void ResetProjectPipeline()
        {
            Interlocked.Increment(ref requestedVersion);
            projectPipeline?.Dispose();
            projectPipeline = null;
            var host = CompilationHost.Value;
            if (host.Project == null || host.XamlDocumentId == null)
            {
                status.Text =
                    "Compilation-backed inspection is unavailable: " +
                    host.Error;
                return;
            }

            var coordinator =
                new RoslynXamlProjectPreviewCoordinator(
                    new WinUiXamlProfile(),
                    new RoslynXamlProjectPreviewOptions
                    {
                        EmitArtifact = true,
                        InspectionOptions =
                            new RoslynXamlCompilationInspectionOptions
                            {
                                CompilerOptions =
                                    new XamlCompilerOptions
                                    {
                                        Framework = "winui",
                                        ResourceUri = "Playground.xaml",
                                        Strict = false
                                    }
                            }
                    });
            var session = new RoslynXamlProjectWatchSession(
                coordinator,
                PublishPreviewAsync,
                TimeSpan.FromMilliseconds(300));
            projectPipeline = new PlaygroundProjectPipeline(
                coordinator,
                session,
                new RoslynXamlProjectWatchTransport(session));
        }

        void ScheduleInspection(bool immediate)
        {
            var pipeline = projectPipeline;
            var host = CompilationHost.Value;
            if (pipeline == null ||
                host.Project == null ||
                host.XamlDocumentId == null)
            {
                status.Text =
                    "Compilation-backed inspection is unavailable: " +
                    host.Error;
                return;
            }

            var version = Interlocked.Increment(ref requestedVersion);
            status.Text = immediate
                ? "Compiling immutable project snapshot…"
                : "Waiting for project edits to settle…";
            pipeline.Transport.SubmitAsync(
                    new RoslynXamlProjectWatchRequest(
                        version,
                        host.Project,
                        host.XamlDocumentId,
                        SourceText.From(editor.Text ?? string.Empty),
                        immediate))
                .ContinueWith(
                    task =>
                    {
                        if (task.IsCanceled ||
                            version != Volatile.Read(ref requestedVersion) ||
                            !ReferenceEquals(pipeline, projectPipeline))
                            return;
                        var dispatcher =
                            Microsoft.UI.Xaml.Input.InputSystem.DispatcherQueue;
                        void Complete()
                        {
                            if (version != Volatile.Read(ref requestedVersion) ||
                                !ReferenceEquals(pipeline, projectPipeline))
                                return;
                            if (task.IsFaulted)
                            {
                                status.Text = "Inspection failed: " +
                                    task.Exception!.GetBaseException().Message;
                                if (previewEnabled)
                                {
                                    previewStatus.Text =
                                        "Preview inspection failed; the last good tree was retained. " +
                                        task.Exception.GetBaseException().Message;
                                }
                                return;
                            }
                            var result = task.Result;
                            if (result.Status ==
                                RoslynXamlProjectWatchStatus.Superseded)
                            {
                                return;
                            }

                            var accepted = pipeline.Coordinator.LastAccepted;
                            if (result.Accepted && accepted != null)
                            {
                                ApplyInspection(accepted, result);
                                return;
                            }

                            status.Text = result.Message;
                            diagnosticsOutput.Text =
                                RenderTransportDiagnostics(result);
                            if (previewEnabled)
                            {
                                previewStatus.Text =
                                    result.Message +
                                    Environment.NewLine +
                                    "The last good preview tree was retained.";
                            }
                        }
                        if (dispatcher == null) Complete();
                        else dispatcher(Complete);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        ResetProjectPipeline();
        inspect.Click += (_, _) => ScheduleInspection(immediate: true);
        editor.TextChanged += (_, _) => ScheduleInspection(immediate: false);
        previewPermission.Click += (_, _) =>
        {
            if (previewEnabled)
            {
                previewEnabled = false;
                previewPermission.Content = "Enable live preview";
                ResetProjectPipeline();
                previewHost.Content = CreatePreviewPlaceholder(
                    "Live preview permission was revoked.");
                previewSession.Reset();
                previewStatus.Text =
                    "Live preview is disabled. Inspection never executes generated code.";
                return;
            }

            if (!WinUiXamlLivePreviewSession.IsRuntimeSupported)
            {
                previewStatus.Text =
                    WinUiXamlLivePreviewSession.RuntimeSupportMessage;
                return;
            }

            previewEnabled = true;
            previewPermission.Content = "Disable live preview";
            previewStatus.Text =
                "Permission granted for this page session; compiling preview…";
            ResetProjectPipeline();
            ScheduleInspection(immediate: true);
        };
        root.Unloaded += (_, _) =>
        {
            Interlocked.Increment(ref requestedVersion);
            projectPipeline?.Dispose();
            projectPipeline = null;
            HotReloadManager.UpdateStarted -= OnHotReloadStarted;
            HotReloadManager.UpdateCompleted -= OnHotReloadCompleted;
            HotReloadManager.Diagnostic -= OnHotReloadDiagnostic;
            previewHost.Content = null;
            previewSession.Dispose();
        };
        root.Children.Add(editor);
        root.Children.Add(inspect);
        root.Children.Add(status);
        root.Children.Add(views);
        return root;
    }

    private static TextBox CreateOutput(string text) => new TextBox
    {
        AcceptsReturn = true,
        Height = 250,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Text = text
    };

    private static string Render(XamlInspectionProjection projection)
    {
        var builder = new StringBuilder();
        foreach (var entry in projection.Entries)
        {
            builder.Append(' ', entry.Depth * 2);
            builder.Append(entry.Kind);
            builder.Append(' ');
            builder.Append(entry.Name);
            if (entry.Value.Length != 0)
                builder.Append(" = ").Append(entry.Value);
            builder.Append(" [").Append(entry.SourceSpan.Start)
                .Append("..").Append(entry.SourceSpan.End).Append(']');
            if (entry.HasStableId)
                builder.Append(" #").Append(entry.StableId!.Value.ToString("x16"));
            builder.AppendLine();
        }
        if (projection.IsTruncated)
            builder.Append("… ").Append(projection.TotalEntryCount - projection.Entries.Length)
                .AppendLine(" more entries omitted by the inspection bound.");
        return builder.ToString();
    }

    private static string RenderGenerated(
        RoslynXamlCompilationInspection inspection)
    {
        if (inspection.CompilationResult.Sources.Count == 0)
            return "No C# was generated. Inspect Diagnostics for the blocking stage.";
        var builder = new StringBuilder();
        foreach (var source in inspection.CompilationResult.Sources)
        {
            builder.Append("// ").AppendLine(source.HintName);
            builder.AppendLine(source.GeneratedSyntaxTree?
                .GetRoot()
                .ToFullString() ?? source.Source);
        }
        return builder.ToString();
    }

    private static string RenderHotReloadStatus(
        string phase,
        HotReloadResult result,
        HotReloadDiagnostic? diagnostic)
    {
        var builder = new StringBuilder();
        builder.Append("host = ").AppendLine(HotReloadManager.HostName);
        builder.Append("enabled = ").AppendLine(HotReloadManager.IsEnabled.ToString());
        builder.Append("runtimeMetadataUpdates = ")
            .AppendLine(HotReloadManager.IsRuntimeSupported.ToString());
        builder.Append("phase = ").AppendLine(phase);
        builder.Append("generation = ").AppendLine(result.Generation.ToString());
        builder.Append("updatedTypes = ").AppendLine(result.UpdatedTypes.Count.ToString());
        builder.Append("replacedElements = ").AppendLine(result.ReplacedElements.ToString());
        builder.Append("reloadedElements = ").AppendLine(result.ReloadedElements.ToString());
        builder.Append("refreshedFactories = ").AppendLine(result.RefreshedFactories.ToString());
        builder.Append("invalidatedElements = ").AppendLine(result.InvalidatedElements.ToString());
        builder.Append("failedElements = ").AppendLine(result.FailedElements.ToString());
        builder.Append("durationMs = ")
            .AppendLine(result.Duration.TotalMilliseconds.ToString("F1"));
        if (diagnostic != null)
        {
            builder.Append("lastDiagnostic = ")
                .Append(diagnostic.Severity)
                .Append(": ")
                .AppendLine(diagnostic.Message);
        }
        return builder.ToString();
    }

    private static string RenderTransportDiagnostics(
        RoslynXamlProjectWatchResultSnapshot result)
    {
        if (result.Diagnostics.Length == 0)
            return result.Message;
        var builder = new StringBuilder();
        foreach (var diagnostic in result.Diagnostics)
        {
            builder.Append(diagnostic.Severity)
                .Append(' ')
                .Append(diagnostic.Id)
                .Append(": ")
                .AppendLine(diagnostic.Message);
        }
        if (result.DiagnosticsTruncated)
            builder.AppendLine("Additional diagnostics were omitted by the transport bound.");
        return builder.ToString();
    }

    private static FrameworkElement CreatePreviewPlaceholder(
        string message) =>
        new Border
        {
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = message
            }
        };

    private static PlaygroundCompilationHost CreateCompilationHost()
    {
        AdhocWorkspace? workspace = null;
        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var trusted =
                (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ??
                string.Empty;
            foreach (var path in trusted.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(path)) paths.Add(path);
            }
            AddAssemblyPath(paths, typeof(Page).Assembly.Location);
            AddAssemblyPath(
                paths,
                typeof(XamlPlaygroundPage).Assembly.Location);
            if (paths.Count == 0)
                return new PlaygroundCompilationHost(
                    null,
                    null,
                    null,
                    "this runtime does not expose trusted metadata reference paths.");

            workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var xamlDocumentId = DocumentId.CreateNewId(projectId);
            var solution = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "ProGPU.Xaml.Playground",
                    "ProGPU.Xaml.Playground",
                    LanguageNames.CSharp,
                    parseOptions: new CSharpParseOptions(
                        LanguageVersion.Latest),
                    compilationOptions: new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        nullableContextOptions:
                            NullableContextOptions.Enable),
                    metadataReferences:
                        paths.OrderBy(
                                static path => path,
                                StringComparer.Ordinal)
                            .Select(
                                static path =>
                                    MetadataReference.CreateFromFile(path))))
                .AddAdditionalDocument(
                    xamlDocumentId,
                    "Playground.xaml",
                    SourceText.From(InitialSource),
                    filePath: "Playground.xaml");
            var project = solution.GetProject(projectId) ??
                throw new InvalidOperationException(
                    "The immutable playground project could not be created.");
            return new PlaygroundCompilationHost(
                workspace,
                project,
                xamlDocumentId,
                null);
        }
        catch (Exception exception)
        {
            workspace?.Dispose();
            return new PlaygroundCompilationHost(
                null,
                null,
                null,
                exception.Message);
        }
    }

    private static void AddAssemblyPath(
        ISet<string> paths,
        string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            paths.Add(path);
    }

    private sealed class PlaygroundCompilationHost
    {
        public PlaygroundCompilationHost(
            AdhocWorkspace? workspace,
            Project? project,
            DocumentId? xamlDocumentId,
            string? error)
        {
            Workspace = workspace;
            Project = project;
            XamlDocumentId = xamlDocumentId;
            Error = error;
        }

        public AdhocWorkspace? Workspace { get; }
        public Project? Project { get; }
        public DocumentId? XamlDocumentId { get; }
        public string? Error { get; }
    }

    private sealed class PlaygroundProjectPipeline : IDisposable
    {
        public PlaygroundProjectPipeline(
            RoslynXamlProjectPreviewCoordinator coordinator,
            RoslynXamlProjectWatchSession session,
            RoslynXamlProjectWatchTransport transport)
        {
            Coordinator = coordinator;
            Session = session;
            Transport = transport;
        }

        public RoslynXamlProjectPreviewCoordinator Coordinator { get; }
        public RoslynXamlProjectWatchSession Session { get; }
        public RoslynXamlProjectWatchTransport Transport { get; }

        public void Dispose() => Session.Dispose();
    }
}
