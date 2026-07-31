using Microsoft.Build.Evaluation;
using Microsoft.CodeAnalysis;
using ProGPU.Xaml.Workspaces;
using RoslynProject = Microsoft.CodeAnalysis.Project;

namespace ProGPU.Xaml.Cli;

internal static class CliMsBuildProjectInputs
{
    /// <summary>
    /// Evaluates imports with the installed MSBuild toolset selected by the CLI
    /// and projects them through the compiler-owned reachable-project traversal.
    /// Evaluation is O(P * E + I log I) for P reachable projects, average
    /// per-project evaluation cost E, and I distinct resolved imports.
    /// </summary>
    public static RoslynXamlEvaluatedBuildInputSet
        Resolve(RoslynProject project)
    {
        if (project == null)
            throw new ArgumentNullException(nameof(project));

        using var collection =
            new ProjectCollection(
                CliMsBuildWorkspace
                    .GetGlobalProperties());
        try
        {
            return RoslynXamlEvaluatedBuildInputSet
                .Create(
                    project,
                    current =>
                        ResolveProjectImports(
                            collection,
                            current));
        }
        finally
        {
            collection.UnloadAllProjects();
        }
    }

    private static IEnumerable<string>
        ResolveProjectImports(
            ProjectCollection collection,
            RoslynProject project)
    {
        if (string.IsNullOrWhiteSpace(
                project.FilePath))
        {
            return Array.Empty<string>();
        }

        var evaluated =
            collection.LoadProject(project.FilePath!);
        var imports = evaluated
            .Imports
            .Select(
                static import =>
                    import.ImportedProject.FullPath)
            .Where(
                static path =>
                    !string.IsNullOrWhiteSpace(path));
        var allProjects = evaluated
            .GetPropertyValue("MSBuildAllProjects")
            .Split(
                new[] { ';' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(
                static path => path.Trim())
            .Where(
                static path =>
                    !string.IsNullOrWhiteSpace(path))
            .Select(
                path =>
                    Path.IsPathRooted(path)
                        ? path
                        : Path.Combine(
                            Path.GetDirectoryName(
                                project.FilePath!)!,
                            path));
        return imports
            .Concat(allProjects)
            .ToArray();
    }
}
