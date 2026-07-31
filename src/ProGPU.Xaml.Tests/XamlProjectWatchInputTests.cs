using System.Threading.Channels;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ProGPU.Xaml.Cli;
using ProGPU.Xaml.Workspaces;
using Xunit;

namespace ProGPU.Xaml.Tests;

public sealed class XamlProjectWatchInputTests
{
    [Fact]
    public void WatchInputSetFollowsReachableRoslynProjectGraph()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "progpu-xaml-watch-" +
            Guid.NewGuid().ToString("N"));
        var appDirectory =
            Path.Combine(root, "projects", "App");
        var dependencyDirectory =
            Path.Combine(appDirectory, "Dependency");
        var unrelatedDirectory =
            Path.Combine(root, "projects", "Unrelated");
        var externalDirectory =
            Path.Combine(root, "shared");
        var appProjectPath =
            Path.Combine(appDirectory, "App.csproj");
        var dependencyProjectPath =
            Path.Combine(
                dependencyDirectory,
                "Dependency.csproj");
        var unrelatedProjectPath =
            Path.Combine(
                unrelatedDirectory,
                "Unrelated.csproj");
        var linkedSourcePath =
            Path.Combine(externalDirectory, "Linked.cs");
        var externalXamlPath =
            Path.Combine(externalDirectory, "Shared.xaml");
        var evaluatedPropsPath =
            Path.Combine(externalDirectory, "Imported.props");

        using var workspace = new AdhocWorkspace();
        var appId = ProjectId.CreateNewId();
        var dependencyId = ProjectId.CreateNewId();
        var unrelatedId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(CreateProjectInfo(
                dependencyId,
                "Dependency",
                dependencyProjectPath))
            .AddDocument(
                DocumentId.CreateNewId(dependencyId),
                "Dependency.cs",
                SourceText.From("namespace Dependency;"),
                filePath:
                    Path.Combine(
                        dependencyDirectory,
                        "Dependency.cs"))
            .AddProject(CreateProjectInfo(
                unrelatedId,
                "Unrelated",
                unrelatedProjectPath))
            .AddDocument(
                DocumentId.CreateNewId(unrelatedId),
                "Unrelated.cs",
                SourceText.From("namespace Unrelated;"),
                filePath:
                    Path.Combine(
                        unrelatedDirectory,
                        "Unrelated.cs"))
            .AddProject(CreateProjectInfo(
                appId,
                "App",
                appProjectPath))
            .AddProjectReference(
                appId,
                new ProjectReference(dependencyId))
            .AddDocument(
                DocumentId.CreateNewId(appId),
                "App.cs",
                SourceText.From("namespace App;"),
                filePath:
                    Path.Combine(
                        appDirectory,
                        "App.cs"))
            .AddDocument(
                DocumentId.CreateNewId(appId),
                "Linked.cs",
                SourceText.From("namespace Shared;"),
                filePath: linkedSourcePath)
            .AddAdditionalDocument(
                DocumentId.CreateNewId(appId),
                "MainPage.xaml",
                SourceText.From("<Page />"),
                filePath:
                    Path.Combine(
                        appDirectory,
                        "MainPage.xaml"))
            .AddAnalyzerConfigDocument(
                DocumentId.CreateNewId(appId),
                ".editorconfig",
                SourceText.From("root = true"),
                filePath:
                    Path.Combine(
                        appDirectory,
                        ".editorconfig"));

        var inputSet =
            RoslynXamlProjectWatchInputSet.Create(
                solution.GetProject(appId)!,
                evaluatedBuildInputs:
                    new[]
                    {
                        evaluatedPropsPath,
                        evaluatedPropsPath
                    },
                explicitInputs:
                    new[] { externalXamlPath });

        Assert.Equal(
            new[] { appDirectory },
            inputSet.RecursiveDirectories);
        Assert.Equal(
            new[]
            {
                evaluatedPropsPath,
                externalXamlPath,
                linkedSourcePath
            }.OrderBy(
                static path => path,
                StringComparer.Ordinal),
            inputSet.ExplicitFiles);
        Assert.DoesNotContain(
            inputSet.Inputs,
            input =>
                string.Equals(
                    input.Path,
                    unrelatedProjectPath,
                    StringComparison.Ordinal));
        Assert.Contains(
            inputSet.Inputs,
            input =>
                input.Kind ==
                    RoslynXamlProjectWatchInputKind
                        .ProjectFile &&
                input.ProjectId == dependencyId &&
                input.Path == dependencyProjectPath);
        Assert.Contains(
            inputSet.Inputs,
            input =>
                input.Kind ==
                    RoslynXamlProjectWatchInputKind
                        .AnalyzerConfigDocument &&
                input.ProjectId == appId);
        Assert.Single(
            inputSet.Inputs,
            input =>
                input.Kind ==
                    RoslynXamlProjectWatchInputKind
                        .EvaluatedBuildInput);
        Assert.True(
            inputSet.Inputs
                .Select(
                    static input =>
                        input.Path + "|" +
                        (int)input.Kind + "|" +
                        input.ProjectIdentity)
                .SequenceEqual(
                    inputSet.Inputs
                        .OrderBy(
                            static input => input.Path,
                            StringComparer.Ordinal)
                        .ThenBy(
                            static input => input.Kind)
                        .ThenBy(
                            static input =>
                                input.ProjectIdentity,
                            StringComparer.Ordinal)
                        .Select(
                            static input =>
                                input.Path + "|" +
                                (int)input.Kind + "|" +
                                input.ProjectIdentity)));
    }

    [Fact]
    public async Task FileSystemSubscriptionSignalsExternalEvaluatedBuildInput()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "progpu-xaml-watch-subscription-" +
            Guid.NewGuid().ToString("N"));
        var projectDirectory =
            Path.Combine(root, "Project");
        var buildDirectory =
            Path.Combine(root, "Build");
        var projectPath =
            Path.Combine(
                projectDirectory,
                "App.csproj");
        var importedPropsPath =
            Path.Combine(
                buildDirectory,
                "Shared.props");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(buildDirectory);
        File.WriteAllText(
            projectPath,
            "<Project />");
        File.WriteAllText(
            importedPropsPath,
            "<Project />");

        try
        {
            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var project = workspace.CurrentSolution
                .AddProject(
                    CreateProjectInfo(
                        projectId,
                        "App",
                        projectPath))
                .GetProject(projectId)!;
            var inputSet =
                RoslynXamlProjectWatchInputSet.Create(
                    project,
                    evaluatedBuildInputs:
                        new[] { importedPropsPath });
            var signals =
                Channel.CreateBounded<string>(
                    new BoundedChannelOptions(1)
                    {
                        FullMode =
                            BoundedChannelFullMode
                                .DropOldest,
                        SingleReader = true,
                        SingleWriter = false
                    });
            using var subscription =
                new RoslynXamlProjectWatchFileSystemSubscription(
                    changedPath =>
                        signals.Writer.TryWrite(
                            changedPath));
            Assert.True(
                subscription.Update(inputSet));
            Assert.False(
                subscription.Update(inputSet));

            File.WriteAllText(
                importedPropsPath,
                "<Project><PropertyGroup />" +
                "</Project>");
            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));
            var changedPath =
                await signals.Reader.ReadAsync(
                    timeout.Token);

            Assert.Equal(
                Path.GetFullPath(importedPropsPath),
                Path.GetFullPath(changedPath));
            Assert.True(
                subscription
                    .TakeRefreshRequested());
            Assert.False(
                subscription
                    .TakeRefreshRequested());
        }
        finally
        {
            Directory.Delete(
                root,
                recursive: true);
        }
    }

    [Fact]
    public async Task TopologyEquivalentUpdateRefreshesBuildClassification()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "progpu-xaml-watch-reclassify-" +
            Guid.NewGuid().ToString("N"));
        var projectDirectory =
            Path.Combine(root, "Project");
        var projectPath = Path.Combine(
            projectDirectory,
            "App.csproj");
        var sharedPath = Path.Combine(
            projectDirectory,
            "Shared.input");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(projectPath, "<Project />");
        File.WriteAllText(sharedPath, "initial");

        try
        {
            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var project = workspace.CurrentSolution
                .AddProject(
                    CreateProjectInfo(
                        projectId,
                        "App",
                        projectPath))
                .GetProject(projectId)!;
            var ordinary =
                RoslynXamlProjectWatchInputSet.Create(
                    project,
                    explicitInputs:
                        new[] { sharedPath });
            var buildClassified =
                RoslynXamlProjectWatchInputSet.Create(
                    project,
                    evaluatedBuildInputs:
                        new[] { sharedPath },
                    explicitInputs:
                        new[] { sharedPath });
            var signals = Channel.CreateUnbounded<string>();
            using var subscription =
                new RoslynXamlProjectWatchFileSystemSubscription(
                    changedPath =>
                        signals.Writer.TryWrite(
                            changedPath));

            Assert.True(subscription.Update(ordinary));
            Assert.False(
                subscription.Update(buildClassified));
            File.WriteAllText(sharedPath, "build");
            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));
            await WaitForPathAsync(
                signals.Reader,
                sharedPath,
                timeout.Token);
            Assert.True(
                subscription.TakeRefreshRequested());

            while (signals.Reader.TryRead(out _))
            {
            }
            Assert.False(subscription.Update(ordinary));
            File.WriteAllText(sharedPath, "ordinary");
            await WaitForPathAsync(
                signals.Reader,
                sharedPath,
                timeout.Token);
            Assert.False(
                subscription.TakeRefreshRequested());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConditionalImportCandidateIsWatchedBeforeItExists()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "progpu-xaml-watch-conditional-import-" +
            Guid.NewGuid().ToString("N"));
        var projectDirectory =
            Path.Combine(root, "Project");
        var generatedDirectory =
            Path.Combine(root, "Generated");
        var projectPath = Path.Combine(
            projectDirectory,
            "App.csproj");
        var optionalPropsPath = Path.Combine(
            generatedDirectory,
            "Optional.props");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            projectPath,
            "<Project><Import Project=\"../Generated/Optional.props\" " +
            "Condition=\"Exists('../Generated/Optional.props')\" />" +
            "</Project>");

        try
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var project = workspace.CurrentSolution
                .AddProject(
                    CreateProjectInfo(
                        projectId,
                        "App",
                        projectPath))
                .GetProject(projectId)!;
            var evaluated =
                CliMsBuildProjectInputs.Resolve(project);
            Assert.Contains(
                Path.GetFullPath(optionalPropsPath),
                evaluated.Paths);

            var inputSet =
                RoslynXamlProjectWatchInputSet.Create(
                    project,
                    evaluated.Paths);
            var signals = Channel.CreateUnbounded<string>();
            using var subscription =
                new RoslynXamlProjectWatchFileSystemSubscription(
                    changedPath =>
                        signals.Writer.TryWrite(
                            changedPath));
            Assert.True(subscription.Update(inputSet));

            Directory.CreateDirectory(generatedDirectory);
            File.WriteAllText(
                optionalPropsPath,
                "<Project><PropertyGroup />" +
                "</Project>");
            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));
            await WaitForPathAsync(
                signals.Reader,
                optionalPropsPath,
                timeout.Token);
            Assert.True(
                subscription.TakeRefreshRequested());

            var refreshed =
                CliMsBuildProjectInputs.Resolve(project);
            Assert.Single(
                refreshed.Paths,
                path =>
                    PathsEqual(
                        path,
                        optionalPropsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CliMsBuildEvaluationSuppliesResolvedImports()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "progpu-xaml-msbuild-inputs-" +
            Guid.NewGuid().ToString("N"));
        var projectDirectory =
            Path.Combine(root, "Project");
        var importDirectory =
            Path.Combine(root, "Build");
        var projectPath =
            Path.Combine(
                projectDirectory,
                "App.csproj");
        var importedPropsPath =
            Path.Combine(
                importDirectory,
                "Shared.props");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(importDirectory);
        File.WriteAllText(
            importedPropsPath,
            "<Project><PropertyGroup>" +
            "<ImportedValue>yes</ImportedValue>" +
            "</PropertyGroup></Project>");
        File.WriteAllText(
            projectPath,
            "<Project><Import Project=\"" +
            importedPropsPath +
            "\" /></Project>");

        try
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var project = workspace.CurrentSolution
                .AddProject(
                    CreateProjectInfo(
                        projectId,
                        "App",
                        projectPath))
                .GetProject(projectId)!;

            var inputs =
                CliMsBuildProjectInputs.Resolve(project);

            Assert.Contains(
                Path.GetFullPath(importedPropsPath),
                inputs.Paths);
        }
        finally
        {
            Directory.Delete(
                root,
                recursive: true);
        }
    }

    [Fact]
    public void EvaluatedBuildInputsFollowReachableProjectGraph()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "progpu-xaml-evaluated-inputs-" +
            Guid.NewGuid().ToString("N"));
        var appProjectPath =
            Path.Combine(root, "App", "App.csproj");
        var dependencyProjectPath =
            Path.Combine(
                root,
                "Dependency",
                "Dependency.csproj");
        var unrelatedProjectPath =
            Path.Combine(
                root,
                "Unrelated",
                "Unrelated.csproj");
        var sharedPropsPath =
            Path.Combine(root, "Shared.props");
        var appTargetsPath =
            Path.Combine(root, "App", "App.targets");

        using var workspace = new AdhocWorkspace();
        var appId = ProjectId.CreateNewId();
        var dependencyId = ProjectId.CreateNewId();
        var unrelatedId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(CreateProjectInfo(
                dependencyId,
                "Dependency",
                dependencyProjectPath))
            .AddProject(CreateProjectInfo(
                unrelatedId,
                "Unrelated",
                unrelatedProjectPath))
            .AddProject(CreateProjectInfo(
                appId,
                "App",
                appProjectPath))
            .AddProjectReference(
                appId,
                new ProjectReference(dependencyId));
        var visited = new List<ProjectId>();

        var inputs =
            RoslynXamlEvaluatedBuildInputSet.Create(
                solution.GetProject(appId)!,
                project =>
                {
                    visited.Add(project.Id);
                    if (project.Id == appId)
                    {
                        return new[]
                        {
                            appTargetsPath,
                            sharedPropsPath
                        };
                    }

                    return new[]
                    {
                        sharedPropsPath,
                        sharedPropsPath
                    };
                });

        Assert.Equal(
            new[] { appId, dependencyId },
            visited);
        Assert.Equal(
            new[]
            {
                appTargetsPath,
                sharedPropsPath
            }.OrderBy(
                static path => path,
                StringComparer.Ordinal),
            inputs.Paths);
        Assert.DoesNotContain(
            unrelatedId,
            visited);
        Assert.Throws<ArgumentNullException>(
            () => RoslynXamlEvaluatedBuildInputSet
                .Create(
                    null!,
                    static _ => Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(
            () => RoslynXamlEvaluatedBuildInputSet
                .Create(
                    solution.GetProject(appId)!,
                    null!));
        Assert.Throws<InvalidOperationException>(
            () => RoslynXamlEvaluatedBuildInputSet
                .Create(
                    solution.GetProject(appId)!,
                    static _ => new[] { " " }));
    }

    [Fact]
    public void WatchInputSetRejectsInvalidCallerPaths()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace
            .AddProject(
                "WatchInputs",
                LanguageNames.CSharp);

        Assert.Throws<ArgumentNullException>(
            () => RoslynXamlProjectWatchInputSet
                .Create(null!));
        Assert.Throws<ArgumentException>(
            () => RoslynXamlProjectWatchInputSet
                .Create(
                    project,
                    explicitInputs:
                        new[] { " " }));
    }

    private static ProjectInfo CreateProjectInfo(
        ProjectId id,
        string name,
        string filePath) =>
        ProjectInfo.Create(
            id,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            filePath: filePath,
            compilationOptions:
                new CSharpCompilationOptions(
                    OutputKind
                        .DynamicallyLinkedLibrary));

    private static async Task WaitForPathAsync(
        ChannelReader<string> reader,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string path = await reader.ReadAsync(
                cancellationToken);
            if (PathsEqual(path, expectedPath))
                return;
        }
    }

    private static bool PathsEqual(
        string left,
        string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
