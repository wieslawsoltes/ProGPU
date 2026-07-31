using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
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
}
