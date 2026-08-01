using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ProGPU.Xaml.Workspaces;

/// <summary>
/// Owns the file-system watchers for one immutable project-watch input set.
/// Updates replace the complete watcher topology transactionally, while a
/// topology-equivalent update refreshes build-graph classification without
/// recreating operating-system watchers. Callers serialize <see cref="Update"/>
/// and <see cref="Dispose"/>; watcher callbacks may arrive concurrently.
/// </summary>
/// <remarks>
/// Creating or replacing a subscription is O(D + F log F) time and O(D + F)
/// storage for D recursive roots and F exact files. An event is expected O(1)
/// and does not enumerate the project graph. The callback owns coalescing and
/// scheduling compilation work.
/// </remarks>
public sealed class RoslynXamlProjectWatchFileSystemSubscription :
    IDisposable
{
    private readonly Action<string> _signal;
    private readonly StringComparer _pathComparer;
    private readonly StringComparison _pathComparison;
    private readonly HashSet<string> _excludedFiles;
    private readonly string[] _excludedTemporaryPrefixes;
    private readonly object _gate = new object();
    private List<FileSystemWatcher> _watchers =
        new List<FileSystemWatcher>();
    private RoslynXamlProjectWatchInputSet? _inputSet;
    private volatile HashSet<string> _refreshFiles;
    private int _refreshRequested;
    private int _disposed;

    public RoslynXamlProjectWatchFileSystemSubscription(
        Action<string> signal,
        IEnumerable<string>? excludedFiles = null)
    {
        _signal = signal ??
            throw new ArgumentNullException(nameof(signal));
        _pathComparer =
            Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        _pathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _excludedFiles = new HashSet<string>(
            (excludedFiles ?? Array.Empty<string>())
                .Select(Path.GetFullPath),
            _pathComparer);
        _excludedTemporaryPrefixes = _excludedFiles
            .Select(static path => path + ".tmp.")
            .ToArray();
        _refreshFiles =
            new HashSet<string>(_pathComparer);
    }

    /// <summary>
    /// Applies a complete immutable input set. Returns <see langword="true"/>
    /// only when operating-system watcher topology changed.
    /// </summary>
    public bool Update(
        RoslynXamlProjectWatchInputSet inputSet)
    {
        if (inputSet == null)
        {
            throw new ArgumentNullException(
                nameof(inputSet));
        }

        var refreshFiles = CreateRefreshFiles(inputSet);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_inputSet != null &&
                HasSameTopology(
                    _inputSet,
                    inputSet,
                    _pathComparer))
            {
                _inputSet = inputSet;
                _refreshFiles = refreshFiles;
                return false;
            }

            var next = CreateWatchers(inputSet);
            var previous = _watchers;
            var previousRefreshFiles = _refreshFiles;
            _refreshFiles = refreshFiles;
            try
            {
                EnableWatchers(next);
            }
            catch
            {
                _refreshFiles = previousRefreshFiles;
                DisposeWatchers(next);
                throw;
            }

            _watchers = next;
            _inputSet = inputSet;
            DisposeWatchers(previous);
            return true;
        }
    }

    /// <summary>
    /// Atomically consumes whether a project/import/topology event requires a
    /// new project graph and evaluated-build-input snapshot.
    /// </summary>
    public bool TakeRefreshRequested() =>
        Interlocked.Exchange(
            ref _refreshRequested,
            0) != 0;

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            var previous = _watchers;
            _watchers = new List<FileSystemWatcher>();
            _inputSet = null;
            _refreshFiles =
                new HashSet<string>(_pathComparer);
            DisposeWatchers(previous);
        }
    }

    private HashSet<string> CreateRefreshFiles(
        RoslynXamlProjectWatchInputSet inputSet) =>
        new HashSet<string>(
            inputSet.Inputs
                .Where(
                    static input =>
                        input.Kind ==
                            RoslynXamlProjectWatchInputKind
                                .ProjectFile ||
                        input.Kind ==
                            RoslynXamlProjectWatchInputKind
                                .EvaluatedBuildInput)
                .Select(static input => input.Path),
            _pathComparer);

    private List<FileSystemWatcher> CreateWatchers(
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
                        exactFiles));
            }

            foreach (var group in exactGroups)
            {
                watchers.Add(
                    CreateExactFilesWatcher(
                        group.Key,
                        new HashSet<string>(
                            group,
                            _pathComparer)));
            }

            return watchers;
        }
        catch
        {
            DisposeWatchers(watchers);
            throw;
        }
    }

    private FileSystemWatcher CreateProjectWatcher(
        string projectDirectory,
        HashSet<string> exactFiles)
    {
        var watchDirectory =
            FindExistingDirectory(projectDirectory);
        var watcher = new FileSystemWatcher(
            watchDirectory)
        {
            IncludeSubdirectories = true,
            Filter = "*",
            NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size
        };

        bool IsRelevantPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (IsExcludedPath(fullPath))
                return false;
            return IsUnderDirectory(
                       fullPath,
                       projectDirectory) &&
                   (IsWatchInput(fullPath) ||
                    (!IsBuildOutput(fullPath) &&
                     exactFiles.Contains(fullPath)));
        }

        FileSystemEventHandler onChanged =
            (_, eventArgs) =>
            {
                if (IsRelevantPath(eventArgs.FullPath))
                    Signal(eventArgs.FullPath, false);
            };
        FileSystemEventHandler onCreated =
            (_, eventArgs) =>
            {
                bool directory =
                    Directory.Exists(eventArgs.FullPath);
                if (directory ||
                    IsRelevantPath(eventArgs.FullPath))
                {
                    Signal(
                        eventArgs.FullPath,
                        directory);
                }
            };
        FileSystemEventHandler onDeleted =
            (_, eventArgs) =>
            {
                bool possibleDirectory =
                    string.IsNullOrEmpty(
                        Path.GetExtension(
                            eventArgs.FullPath));
                if (possibleDirectory ||
                    IsRelevantPath(eventArgs.FullPath))
                {
                    Signal(
                        eventArgs.FullPath,
                        possibleDirectory);
                }
            };
        RenamedEventHandler onRenamed =
            (_, eventArgs) =>
            {
                var oldFullPath =
                    Path.GetFullPath(
                        eventArgs.OldFullPath);
                var fullPath =
                    Path.GetFullPath(
                        eventArgs.FullPath);
                if (IsExcludedPath(oldFullPath) &&
                    IsExcludedPath(fullPath))
                {
                    return;
                }
                if (IsBuildOutput(oldFullPath) &&
                    IsBuildOutput(fullPath))
                {
                    return;
                }

                if (IsUnderDirectory(
                        oldFullPath,
                        projectDirectory) ||
                    IsUnderDirectory(
                        fullPath,
                        projectDirectory))
                {
                    Signal(
                        oldFullPath,
                        true);
                    Signal(
                        fullPath,
                        true);
                }
            };
        ErrorEventHandler onError =
            (_, _) => Signal(
                projectDirectory,
                true);

        watcher.Changed += onChanged;
        watcher.Created += onCreated;
        watcher.Deleted += onDeleted;
        watcher.Renamed += onRenamed;
        watcher.Error += onError;
        return watcher;
    }

    private FileSystemWatcher CreateExactFilesWatcher(
        string requestedDirectory,
        HashSet<string> groupFiles)
    {
        var watchDirectory =
            FindExistingDirectory(requestedDirectory);
        var watcher = new FileSystemWatcher(
            watchDirectory,
            "*")
        {
            IncludeSubdirectories =
                !_pathComparer.Equals(
                    watchDirectory,
                    requestedDirectory),
            NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size
        };

        bool IsExact(string path) =>
            groupFiles.Contains(Path.GetFullPath(path));
        FileSystemEventHandler onChange =
            (_, eventArgs) =>
            {
                if (IsExact(eventArgs.FullPath))
                    Signal(eventArgs.FullPath, false);
            };
        RenamedEventHandler onRename =
            (_, eventArgs) =>
            {
                if (IsExact(eventArgs.OldFullPath))
                    Signal(eventArgs.OldFullPath, false);
                if (IsExact(eventArgs.FullPath))
                    Signal(eventArgs.FullPath, false);
            };
        ErrorEventHandler onError =
            (_, _) => Signal(
                requestedDirectory,
                true);

        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += onRename;
        watcher.Error += onError;
        return watcher;
    }

    private void Signal(
        string changedPath,
        bool forceRefresh)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var fullPath = Path.GetFullPath(changedPath);
        if (forceRefresh ||
            _refreshFiles.Contains(fullPath) ||
            IsBuildGraphInput(fullPath))
        {
            Interlocked.Exchange(
                ref _refreshRequested,
                1);
        }

        _signal(fullPath);
    }

    private string FindExistingDirectory(string directory)
    {
        var candidate = Path.GetFullPath(directory);
        while (!Directory.Exists(candidate))
        {
            var parent = Directory.GetParent(candidate);
            if (parent == null)
            {
                throw new DirectoryNotFoundException(
                    "No existing ancestor can host a watcher for '" +
                    directory + "'.");
            }
            candidate = parent.FullName;
        }
        return candidate;
    }

    private static bool IsBuildGraphInput(string path)
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
               IsBuildGraphInput(path) ||
               extension.Equals(
                   ".editorconfig",
                   StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(
                   ".resw",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuildOutput(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.IndexOf(
                   "/obj/",
                   StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.EndsWith(
                   "/obj",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.IndexOf(
                   "/bin/",
                   StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.EndsWith(
                   "/bin",
                   StringComparison.OrdinalIgnoreCase);
    }

    private bool IsExcludedPath(string path)
    {
        if (_excludedFiles.Contains(path))
            return true;

        foreach (var prefix in _excludedTemporaryPrefixes)
        {
            if (path.StartsWith(prefix, _pathComparison))
                return true;
        }

        return false;
    }

    private bool IsUnderDirectory(
        string path,
        string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory);
        if (_pathComparer.Equals(
                fullPath,
                fullDirectory))
        {
            return true;
        }

        var prefix =
            fullDirectory[fullDirectory.Length - 1] ==
                Path.DirectorySeparatorChar ||
            fullDirectory[fullDirectory.Length - 1] ==
                Path.AltDirectorySeparatorChar
                ? fullDirectory
                : fullDirectory +
                  Path.DirectorySeparatorChar;
        return fullPath.StartsWith(
            prefix,
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static bool HasSameTopology(
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

    private static void EnableWatchers(
        IEnumerable<FileSystemWatcher> watchers)
    {
        foreach (var watcher in watchers)
            watcher.EnableRaisingEvents = true;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(
                nameof(
                    RoslynXamlProjectWatchFileSystemSubscription));
        }
    }
}
