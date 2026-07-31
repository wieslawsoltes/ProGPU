using Microsoft.Build.Evaluation;
using Microsoft.Build.Construction;
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
            .Concat(
                ResolveImportCandidates(
                    evaluated))
            .ToArray();
    }

    private static IEnumerable<string>
        ResolveImportCandidates(
            Microsoft.Build.Evaluation.Project evaluated)
    {
        var roots = evaluated.Imports
            .Select(
                static import =>
                    import.ImportedProject)
            .Prepend(evaluated.Xml)
            .Where(
                static root =>
                    !string.IsNullOrWhiteSpace(
                        root.FullPath))
            .GroupBy(
                static root => root.FullPath,
                Path.DirectorySeparatorChar == '\\'
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
            .Select(static group => group.First());

        foreach (ProjectRootElement root in roots)
        {
            string declaringDirectory =
                Path.GetDirectoryName(root.FullPath) ??
                string.Empty;
            foreach (ProjectImportElement import in
                     root.Imports)
            {
                string expanded = evaluated
                    .ExpandString(import.Project)
                    .Trim();
                if (string.IsNullOrEmpty(expanded) ||
                    ContainsUnresolvedExpression(expanded))
                {
                    continue;
                }

                foreach (string candidate in
                         expanded.Split(
                             new[] { ';' },
                             StringSplitOptions
                                 .RemoveEmptyEntries))
                {
                    string trimmed = candidate.Trim();
                    if (string.IsNullOrEmpty(trimmed) ||
                        trimmed.IndexOfAny(
                            new[] { '*', '?' }) >= 0)
                    {
                        continue;
                    }

                    yield return Path.GetFullPath(
                        Path.IsPathRooted(trimmed)
                            ? trimmed
                            : Path.Combine(
                                declaringDirectory,
                                trimmed));
                }
            }
        }
    }

    private static bool ContainsUnresolvedExpression(
        string value) =>
        value.IndexOf("$(", StringComparison.Ordinal) >= 0 ||
        value.IndexOf("@(", StringComparison.Ordinal) >= 0 ||
        value.IndexOf("%(", StringComparison.Ordinal) >= 0;
}
