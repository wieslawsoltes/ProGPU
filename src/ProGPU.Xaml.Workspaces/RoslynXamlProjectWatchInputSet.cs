using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ProGPU.Xaml.Workspaces;

public enum RoslynXamlProjectWatchInputKind
{
    ProjectDirectory,
    ProjectFile,
    SourceDocument,
    AdditionalDocument,
    AnalyzerConfigDocument,
    EvaluatedBuildInput,
    ExplicitInput
}

public sealed class RoslynXamlProjectWatchInput
{
    internal RoslynXamlProjectWatchInput(
        string path,
        RoslynXamlProjectWatchInputKind kind,
        ProjectId? projectId,
        string? projectIdentity)
    {
        Path = path;
        Kind = kind;
        ProjectId = projectId;
        ProjectIdentity = projectIdentity;
    }

    public string Path { get; }

    public RoslynXamlProjectWatchInputKind Kind { get; }

    public ProjectId? ProjectId { get; }

    public string? ProjectIdentity { get; }

    public bool IsDirectory =>
        Kind ==
        RoslynXamlProjectWatchInputKind.ProjectDirectory;
}

/// <summary>
/// Captures the deterministic file-system boundary of one immutable Roslyn project
/// graph. Project directories discover SDK-default item additions, while exact document,
/// analyzer-config, evaluated build, linked, and caller-supplied paths preserve inputs
/// outside those roots. Construction is O(P + R + D log D) for P reachable projects,
/// R project-reference edges, and D distinct input declarations.
/// </summary>
public sealed class RoslynXamlProjectWatchInputSet
{
    private RoslynXamlProjectWatchInputSet(
        ImmutableArray<RoslynXamlProjectWatchInput> inputs,
        ImmutableArray<string> recursiveDirectories,
        ImmutableArray<string> files,
        ImmutableArray<string> explicitFiles)
    {
        Inputs = inputs;
        RecursiveDirectories = recursiveDirectories;
        Files = files;
        ExplicitFiles = explicitFiles;
    }

    public ImmutableArray<RoslynXamlProjectWatchInput>
        Inputs { get; }

    public ImmutableArray<string> RecursiveDirectories { get; }

    public ImmutableArray<string> Files { get; }

    public ImmutableArray<string> ExplicitFiles { get; }

    public static RoslynXamlProjectWatchInputSet Create(
        Project project,
        IEnumerable<string>? evaluatedBuildInputs = null,
        IEnumerable<string>? explicitInputs = null)
    {
        if (project == null)
            throw new ArgumentNullException(nameof(project));

        var pathComparer =
            Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        var declarations =
            new Dictionary<
                WatchInputKey,
                RoslynXamlProjectWatchInput>(
                new WatchInputKeyComparer(pathComparer));
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

            AddProjectInputs(
                declarations,
                current);
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

        AddPaths(
            declarations,
            evaluatedBuildInputs,
            RoslynXamlProjectWatchInputKind
                .EvaluatedBuildInput,
            nameof(evaluatedBuildInputs));
        AddPaths(
            declarations,
            explicitInputs,
            RoslynXamlProjectWatchInputKind
                .ExplicitInput,
            nameof(explicitInputs));

        var inputs = declarations.Values
            .OrderBy(
                static item => item.Path,
                StringComparer.Ordinal)
            .ThenBy(static item => item.Kind)
            .ThenBy(
                static item =>
                    item.ProjectIdentity,
                StringComparer.Ordinal)
            .ToImmutableArray();
        var recursiveDirectories = RemoveNestedDirectories(
            inputs
                .Where(static item => item.IsDirectory)
                .Select(static item => item.Path),
            pathComparer);
        var files = inputs
            .Where(static item => !item.IsDirectory)
            .Select(static item => item.Path)
            .Distinct(pathComparer)
            .OrderBy(
                static path => path,
                StringComparer.Ordinal)
            .ToImmutableArray();
        var explicitFiles = files
            .Where(
                path =>
                    !recursiveDirectories.Any(
                        directory =>
                            IsUnderDirectory(
                                path,
                                directory,
                                pathComparer)))
            .OrderBy(
                static path => path,
                StringComparer.Ordinal)
            .ToImmutableArray();

        return new RoslynXamlProjectWatchInputSet(
            inputs,
            recursiveDirectories,
            files,
            explicitFiles);
    }

    private static void AddProjectInputs(
        IDictionary<
            WatchInputKey,
            RoslynXamlProjectWatchInput> declarations,
        Project project)
    {
        var projectIdentity =
            GetProjectIdentity(project);
        if (!string.IsNullOrWhiteSpace(project.FilePath))
        {
            var projectFile =
                NormalizePath(project.FilePath!);
            Add(
                declarations,
                projectFile,
                RoslynXamlProjectWatchInputKind
                    .ProjectFile,
                project.Id,
                projectIdentity);
            var directory =
                Path.GetDirectoryName(projectFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Add(
                    declarations,
                    directory!,
                    RoslynXamlProjectWatchInputKind
                        .ProjectDirectory,
                    project.Id,
                    projectIdentity);
            }
        }

        AddDocuments(
            declarations,
            project.Documents,
            RoslynXamlProjectWatchInputKind
                .SourceDocument,
            project.Id,
            projectIdentity);
        AddDocuments(
            declarations,
            project.AdditionalDocuments,
            RoslynXamlProjectWatchInputKind
                .AdditionalDocument,
            project.Id,
            projectIdentity);
        AddDocuments(
            declarations,
            project.AnalyzerConfigDocuments,
            RoslynXamlProjectWatchInputKind
                .AnalyzerConfigDocument,
            project.Id,
            projectIdentity);
    }

    private static void AddDocuments<TDocument>(
        IDictionary<
            WatchInputKey,
            RoslynXamlProjectWatchInput> declarations,
        IEnumerable<TDocument> documents,
        RoslynXamlProjectWatchInputKind kind,
        ProjectId projectId,
        string projectIdentity)
        where TDocument : TextDocument
    {
        foreach (var document in documents)
        {
            if (!string.IsNullOrWhiteSpace(
                    document.FilePath))
            {
                Add(
                    declarations,
                    NormalizePath(document.FilePath!),
                    kind,
                    projectId,
                    projectIdentity);
            }
        }
    }

    private static void AddPaths(
        IDictionary<
            WatchInputKey,
            RoslynXamlProjectWatchInput> declarations,
        IEnumerable<string>? paths,
        RoslynXamlProjectWatchInputKind kind,
        string parameterName)
    {
        if (paths == null)
            return;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "Watch input paths cannot be empty.",
                    parameterName);
            }
            Add(
                declarations,
                NormalizePath(path),
                kind,
                projectId: null,
                projectIdentity: null);
        }
    }

    private static void Add(
        IDictionary<
            WatchInputKey,
            RoslynXamlProjectWatchInput> declarations,
        string path,
        RoslynXamlProjectWatchInputKind kind,
        ProjectId? projectId,
        string? projectIdentity)
    {
        var key = new WatchInputKey(
            path,
            kind,
            projectIdentity);
        if (!declarations.ContainsKey(key))
        {
            declarations.Add(
                key,
                new RoslynXamlProjectWatchInput(
                    path,
                    kind,
                    projectId,
                    projectIdentity));
        }
    }

    private static ImmutableArray<string>
        RemoveNestedDirectories(
            IEnumerable<string> directories,
            StringComparer pathComparer)
    {
        var ordered = directories
            .Distinct(pathComparer)
            .OrderBy(
                static path => path.Length)
            .ThenBy(
                static path => path,
                StringComparer.Ordinal)
            .ToArray();
        var roots = new List<string>(ordered.Length);
        foreach (var directory in ordered)
        {
            if (!roots.Any(
                    root =>
                        IsUnderDirectory(
                            directory,
                            root,
                            pathComparer)))
            {
                roots.Add(directory);
            }
        }

        roots.Sort(StringComparer.Ordinal);
        return roots.ToImmutableArray();
    }

    private static bool IsUnderDirectory(
        string path,
        string directory,
        StringComparer pathComparer)
    {
        if (pathComparer.Equals(path, directory))
            return true;

        var prefix =
            directory[directory.Length - 1] ==
                Path.DirectorySeparatorChar ||
            directory[directory.Length - 1] ==
                Path.AltDirectorySeparatorChar
                ? directory
                : directory +
                  Path.DirectorySeparatorChar;
        return path.StartsWith(
            prefix,
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path);
        var root = Path.GetPathRoot(normalized) ??
            string.Empty;
        while (normalized.Length > root.Length &&
               (normalized[normalized.Length - 1] ==
                    Path.DirectorySeparatorChar ||
                normalized[normalized.Length - 1] ==
                    Path.AltDirectorySeparatorChar))
        {
            normalized = normalized.Substring(
                0,
                normalized.Length - 1);
        }
        return normalized;
    }

    private static string GetProjectIdentity(
        Project? project)
    {
        if (project == null)
            return string.Empty;
        if (!string.IsNullOrWhiteSpace(project.FilePath))
            return NormalizePath(project.FilePath!);
        return project.Name + "|" +
            project.AssemblyName + "|" +
            project.Language;
    }

    private readonly struct WatchInputKey
    {
        public WatchInputKey(
            string path,
            RoslynXamlProjectWatchInputKind kind,
            string? projectIdentity)
        {
            Path = path;
            Kind = kind;
            ProjectIdentity = projectIdentity;
        }

        public string Path { get; }
        public RoslynXamlProjectWatchInputKind Kind { get; }
        public string? ProjectIdentity { get; }
    }

    private sealed class WatchInputKeyComparer :
        IEqualityComparer<WatchInputKey>
    {
        private readonly StringComparer _pathComparer;

        public WatchInputKeyComparer(
            StringComparer pathComparer) =>
            _pathComparer = pathComparer;

        public bool Equals(
            WatchInputKey x,
            WatchInputKey y) =>
            x.Kind == y.Kind &&
            string.Equals(
                x.ProjectIdentity,
                y.ProjectIdentity,
                StringComparison.Ordinal) &&
            _pathComparer.Equals(x.Path, y.Path);

        public int GetHashCode(WatchInputKey obj)
        {
            unchecked
            {
                var hash = _pathComparer
                    .GetHashCode(obj.Path);
                hash = (hash * 397) ^
                    (int)obj.Kind;
                hash = (hash * 397) ^
                    (obj.ProjectIdentity == null
                        ? 0
                        : StringComparer.Ordinal
                            .GetHashCode(
                                obj.ProjectIdentity));
                return hash;
            }
        }
    }
}
