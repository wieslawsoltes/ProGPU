using Windows.Storage.Streams;

namespace Windows.Storage;

/// <summary>
/// Platform-neutral StorageFile contract. Hosts may provide virtual-file
/// callbacks for mobile and browser targets; ordinary desktop paths use
/// direct asynchronous file I/O.
/// </summary>
public sealed class StorageFile : IStorageFile
{
    public StorageFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; }

    public string Name =>
        System.IO.Path.GetFileName(Path);

    public string FileType =>
        System.IO.Path.GetExtension(Path);

    public string ContentType =>
        RandomAccessStreamReference.InferContentType(FileType);

    public async Task<string> ReadTextAsync()
    {
        if (StoragePlatformServices.ReadTextAsync is
            { } platformRead)
        {
            return await platformRead(Path)
                .ConfigureAwait(false);
        }
        return await File.ReadAllTextAsync(Path)
            .ConfigureAwait(false);
    }

    public async Task<byte[]> ReadBytesAsync()
    {
        if (StoragePlatformServices.ReadBytesAsync is
            { } platformRead)
        {
            return await platformRead(Path)
                .ConfigureAwait(false);
        }
        return await File.ReadAllBytesAsync(Path)
            .ConfigureAwait(false);
    }

    public async Task<IRandomAccessStreamWithContentType>
        OpenReadAsync()
    {
        byte[] bytes = await ReadBytesAsync()
            .ConfigureAwait(false);
        return new ImmutableRandomAccessStreamWithContentType(
            bytes,
            ContentType);
    }

    public async Task WriteTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (StoragePlatformServices.WriteTextAsync is
                { } platformWrite &&
            await platformWrite(Path, text)
                .ConfigureAwait(false))
        {
            return;
        }
        await File.WriteAllTextAsync(Path, text)
            .ConfigureAwait(false);
    }

    public async Task WriteBytesAsync(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (StoragePlatformServices.WriteBytesAsync is
                { } platformWrite &&
            await platformWrite(Path, bytes)
                .ConfigureAwait(false))
        {
            return;
        }
        await File.WriteAllBytesAsync(Path, bytes)
            .ConfigureAwait(false);
    }

    public static Task<StorageFile> GetFileFromPathAsync(
        string path) =>
        Task.FromResult(new StorageFile(path));
}

/// <summary>
/// Typed host seams for virtual files and native pickers. Assignments happen
/// during platform bootstrap, never in media processing hot paths.
/// </summary>
public static class StoragePlatformServices
{
    public static Func<
        int,
        IReadOnlyList<string>?,
        string?,
        Task<string?>>? PickPathAsync { get; set; }

    public static Func<string, Task<string>>?
        ReadTextAsync { get; set; }

    public static Func<string, Task<byte[]>>?
        ReadBytesAsync { get; set; }

    /// <summary>
    /// Resolves a host-backed storage path to an absolute content URI that
    /// native media APIs can consume without assuming desktop file access.
    /// </summary>
    public static Func<string, Uri?>?
        ResolveContentUri { get; set; }

    public static Func<string, string, Task<bool>>?
        WriteTextAsync { get; set; }

    public static Func<string, byte[], Task<bool>>?
        WriteBytesAsync { get; set; }

    public static Func<string, Task<IReadOnlyList<string>>>?
        EnumerateFilesAsync { get; set; }

    public static Func<string, Task<IReadOnlyList<string>>>?
        EnumerateFoldersAsync { get; set; }

    public static Func<string, string, Task<string>>?
        CreateFileAsync { get; set; }

    public static Func<string, string, Task<string>>?
        CreateFolderAsync { get; set; }

    internal static Uri GetContentUri(IStorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (ResolveContentUri?.Invoke(file.Path) is
            { IsAbsoluteUri: true } resolved)
        {
            return resolved;
        }

        if (Uri.TryCreate(
                file.Path,
                UriKind.Absolute,
                out Uri? source) &&
            !string.IsNullOrEmpty(source.Scheme))
        {
            return source;
        }

        return new Uri(Path.GetFullPath(file.Path));
    }
}
