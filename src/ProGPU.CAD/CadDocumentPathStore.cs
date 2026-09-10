using System.Runtime.Versioning;

namespace ProGPU.CAD;

/// <summary>
/// Opens and atomically stages DXF/DWG documents through filesystem paths.
/// </summary>
[UnsupportedOSPlatform("browser")]
public interface ICadDocumentPathStore
{
    ValueTask<CadLoadResult> LoadAsync(
        string sourcePath,
        CadDocumentFormat format = CadDocumentFormat.Auto,
        CadLoadOptions? options = null,
        IProgress<CadOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask<CadSaveResult> SaveAsync(
        CadDocumentSession session,
        string destinationPath,
        CadDocumentFormat format = CadDocumentFormat.Auto,
        CadSaveOptions? options = null,
        IProgress<CadOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Desktop/server path adapter over the caller-owned-stream CAD store.
/// </summary>
/// <remarks>
/// Save writes a uniquely named file in the destination directory, flushes its
/// contents through the operating-system buffers, and performs one same-volume
/// replacement only after serialization and cancellation checks succeed. The
/// serialized generation is marked saved only after that replacement. Work is
/// O(N) time and O(N) temporary filesystem storage for N output bytes, with
/// bounded O(1) managed state; the final same-directory move is one filesystem
/// namespace operation where the host filesystem supports it.
/// </remarks>
[UnsupportedOSPlatform("browser")]
public sealed class CadDocumentPathStore : ICadDocumentPathStore
{
    private const int FileBufferSize = 128 * 1024;
    private const string StagingPrefix = ".progpu-cad-";
    private const string StagingSuffix = ".tmp";

    private readonly ICadDocumentStore _streamStore;

    public CadDocumentPathStore(ICadDocumentStore? streamStore = null)
    {
        _streamStore = streamStore ?? new CadDocumentStore();
    }

    public async ValueTask<CadLoadResult> LoadAsync(
        string sourcePath,
        CadDocumentFormat format = CadDocumentFormat.Auto,
        CadLoadOptions? options = null,
        IProgress<CadOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string fullPath = ResolveFilePath(sourcePath, nameof(sourcePath));
        cancellationToken.ThrowIfCancellationRequested();
        await using var source = new FileStream(
            fullPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = FileBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        return await _streamStore.LoadAsync(
                source,
                format,
                options,
                fullPath,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<CadSaveResult> SaveAsync(
        CadDocumentSession session,
        string destinationPath,
        CadDocumentFormat format = CadDocumentFormat.Auto,
        CadSaveOptions? options = null,
        IProgress<CadOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        string fullPath = ResolveFilePath(
            destinationPath,
            nameof(destinationPath));
        CadDocumentFormat resolvedFormat = ResolveSaveFormat(fullPath, format);
        string directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException(
            "The destination path must have a parent directory.",
            nameof(destinationPath));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The destination directory '{directory}' does not exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CadSaveOptions();
        var stagedOptions = new CadSaveOptions
        {
            BinaryDxf = options.BinaryDxf,
            AllowUncertifiedWrite = options.AllowUncertifiedWrite,
            DeferSavedGenerationCommit = true,
        };
        string stagedPath = Path.Combine(
            directory,
            $"{StagingPrefix}{Guid.NewGuid():N}{StagingSuffix}");
        bool destinationCommitted = false;
        try
        {
            CadSaveResult result;
            var deferredProgress = new DeferredCompletionProgress(progress);
            await using (var destination = new FileStream(
                stagedPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = FileBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }))
            {
                result = await _streamStore.SaveAsync(
                        session,
                        destination,
                        resolvedFormat,
                        stagedOptions,
                        deferredProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!result.RequiresSavedGenerationCommit)
                {
                    throw new InvalidOperationException(
                        "The stream store did not defer its saved-generation commit.");
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(fullPath))
            {
                throw new IOException(
                    $"The destination path '{fullPath}' identifies a directory.");
            }
            File.Move(stagedPath, fullPath, overwrite: true);
            destinationCommitted = true;
            _ = result.CommitSavedGeneration();
            progress?.Report(new CadOperationProgress(
                CadOperationStage.Completed,
                0,
                null));
            return result;
        }
        finally
        {
            if (!destinationCommitted)
            {
                TryDelete(stagedPath);
            }
        }
    }

    private static string ResolveFilePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        string fullPath = Path.GetFullPath(path);
        if (Path.GetFileName(fullPath).Length == 0)
        {
            throw new ArgumentException(
                "A file path, not a directory path, is required.",
                parameterName);
        }
        return fullPath;
    }

    private static CadDocumentFormat ResolveSaveFormat(
        string destinationPath,
        CadDocumentFormat format)
    {
        if (format is CadDocumentFormat.Dxf or CadDocumentFormat.Dwg)
        {
            return format;
        }
        if (format != CadDocumentFormat.Auto)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }

        return Path.GetExtension(destinationPath) switch
        {
            string extension when extension.Equals(
                ".dxf",
                StringComparison.OrdinalIgnoreCase) => CadDocumentFormat.Dxf,
            string extension when extension.Equals(
                ".dwg",
                StringComparison.OrdinalIgnoreCase) => CadDocumentFormat.Dwg,
            _ => throw new ArgumentException(
                "Auto save format requires a .dxf or .dwg destination extension.",
                nameof(destinationPath)),
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class DeferredCompletionProgress :
        IProgress<CadOperationProgress>
    {
        private readonly IProgress<CadOperationProgress>? _inner;

        internal DeferredCompletionProgress(
            IProgress<CadOperationProgress>? inner)
        {
            _inner = inner;
        }

        public void Report(CadOperationProgress value)
        {
            if (value.Stage != CadOperationStage.Completed)
            {
                _inner?.Report(value);
            }
        }
    }
}
