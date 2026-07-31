using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ProGPU.Xaml.Workspaces;

/// <summary>
/// Resolves evaluated build inputs for the target and every loaded reachable
/// Roslyn project. The caller owns MSBuild evaluation; this type owns graph
/// traversal, normalization, de-duplication, and deterministic ordering.
/// Construction is O(P + R + I log I) for P reachable projects, R project
/// reference edges, and I distinct evaluated input paths.
/// </summary>
public sealed class RoslynXamlEvaluatedBuildInputSet
{
    private RoslynXamlEvaluatedBuildInputSet(
        ImmutableArray<string> paths)
    {
        Paths = paths;
    }

    public ImmutableArray<string> Paths { get; }

    public static RoslynXamlEvaluatedBuildInputSet Create(
        Project project,
        Func<Project, IEnumerable<string>>
            evaluatedInputProvider)
    {
        if (project == null)
            throw new ArgumentNullException(nameof(project));
        if (evaluatedInputProvider == null)
        {
            throw new ArgumentNullException(
                nameof(evaluatedInputProvider));
        }

        var pathComparer =
            Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        var paths = new HashSet<string>(pathComparer);
        var visited = new HashSet<ProjectId>();
        var pending = new Queue<ProjectId>();
        pending.Enqueue(project.Id);

        while (pending.Count != 0)
        {
            var projectId = pending.Dequeue();
            if (!visited.Add(projectId))
                continue;

            var current =
                project.Solution.GetProject(projectId);
            if (current == null)
                continue;

            var evaluatedInputs =
                evaluatedInputProvider(current) ??
                throw new InvalidOperationException(
                    "The evaluated build input provider " +
                    "returned null for project '" +
                    GetProjectIdentity(current) +
                    "'.");
            foreach (var path in evaluatedInputs)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException(
                        "The evaluated build input provider " +
                        "returned an empty path for project '" +
                        GetProjectIdentity(current) +
                        "'.");
                }

                paths.Add(Path.GetFullPath(path));
            }

            foreach (var reference in current
                         .ProjectReferences
                         .OrderBy(
                             item =>
                                 GetProjectIdentity(
                                     project.Solution
                                         .GetProject(
                                             item.ProjectId)),
                             StringComparer.Ordinal))
            {
                pending.Enqueue(reference.ProjectId);
            }
        }

        return new RoslynXamlEvaluatedBuildInputSet(
            paths
                .OrderBy(
                    static path => path,
                    StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static string GetProjectIdentity(
        Project? project)
    {
        if (project == null)
            return string.Empty;
        if (!string.IsNullOrWhiteSpace(project.FilePath))
            return Path.GetFullPath(project.FilePath!);
        return project.Name + "|" +
            project.AssemblyName + "|" +
            project.Language;
    }
}
