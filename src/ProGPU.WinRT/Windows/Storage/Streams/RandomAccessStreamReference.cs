using Windows.Storage;

namespace Windows.Storage.Streams;

/// <summary>
/// WinRT-shaped reference to a random-access readable stream.
/// </summary>
public interface IRandomAccessStreamReference
{
    Task<IRandomAccessStreamWithContentType> OpenReadAsync();
}

/// <summary>
/// Reopenable reference used by media thumbnails and other WinRT-shaped
/// metadata contracts. The referenced payload is materialized once and each
/// open creates an independent cursor over the immutable bytes.
/// </summary>
/// <remarks>
/// Creating from an existing stream is O(B) time and storage for B bytes.
/// File and URI references defer that O(B) work until the first open and
/// share the resulting immutable payload. Subsequent opens are O(1).
/// </remarks>
public sealed class RandomAccessStreamReference :
    IRandomAccessStreamReference
{
    private static readonly HttpClient s_httpClient = new();
    private readonly Lazy<Task<StreamPayload>> _payload;

    private RandomAccessStreamReference(
        Func<Task<StreamPayload>> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        _payload = new Lazy<Task<StreamPayload>>(
            open,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static RandomAccessStreamReference CreateFromFile(
        IStorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new RandomAccessStreamReference(
            async () =>
            {
                using IRandomAccessStreamWithContentType stream =
                    await file.OpenReadAsync()
                        .ConfigureAwait(false);
                return new StreamPayload(
                    Snapshot(stream),
                    stream.ContentType);
            });
    }

    public static RandomAccessStreamReference CreateFromStream(
        IRandomAccessStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = Snapshot(stream);
        string contentType =
            (stream as IContentTypeProvider)?.ContentType ??
            "application/octet-stream";
        var payload = new StreamPayload(bytes, contentType);
        return new RandomAccessStreamReference(
            () => Task.FromResult(payload));
    }

    public static RandomAccessStreamReference CreateFromUri(
        Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The thumbnail URI must be absolute.",
                nameof(uri));
        }

        return new RandomAccessStreamReference(
            () => LoadUriAsync(uri));
    }

    public async Task<IRandomAccessStreamWithContentType>
        OpenReadAsync()
    {
        StreamPayload payload =
            await _payload.Value.ConfigureAwait(false);
        return new ImmutableRandomAccessStreamWithContentType(
            payload.Bytes,
            payload.ContentType);
    }

    private static byte[] Snapshot(
        IRandomAccessStream source)
    {
        Stream stream = source.AsStream();
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "A referenced random-access stream must be readable and seekable.",
                nameof(source));
        }

        lock (stream)
        {
            long position = stream.Position;
            try
            {
                stream.Position = 0;
                using var snapshot = stream.Length <= int.MaxValue
                    ? new MemoryStream(
                        checked((int)stream.Length))
                    : new MemoryStream();
                stream.CopyTo(snapshot);
                return snapshot.ToArray();
            }
            finally
            {
                stream.Position = position;
            }
        }
    }

    private static async Task<StreamPayload> LoadUriAsync(
        Uri uri)
    {
        if (uri.IsFile)
        {
            byte[] fileBytes =
                await File.ReadAllBytesAsync(uri.LocalPath)
                    .ConfigureAwait(false);
            return new StreamPayload(
                fileBytes,
                InferContentType(
                    Path.GetExtension(uri.LocalPath)));
        }

        using HttpResponseMessage response =
            await s_httpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] bytes =
            await response.Content.ReadAsByteArrayAsync()
                .ConfigureAwait(false);
        string contentType =
            response.Content.Headers.ContentType?.MediaType ??
            InferContentType(Path.GetExtension(uri.AbsolutePath));
        return new StreamPayload(bytes, contentType);
    }

    internal static string InferContentType(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".jpeg" or ".jpg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

    private readonly record struct StreamPayload(
        byte[] Bytes,
        string ContentType);

}

internal sealed class ImmutableRandomAccessStreamWithContentType :
    IRandomAccessStreamWithContentType
{
    private readonly byte[] _bytes;
    private MemoryStream? _stream;

    public ImmutableRandomAccessStreamWithContentType(
        byte[] bytes,
        string contentType)
    {
        _bytes =
            bytes ??
            throw new ArgumentNullException(nameof(bytes));
        ContentType =
            string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType;
        _stream = new MemoryStream(
            _bytes,
            writable: false);
    }

    public bool CanRead => GetStream().CanRead;
    public bool CanWrite => false;
    public string ContentType { get; }
    public ulong Position =>
        checked((ulong)GetStream().Position);
    public ulong Size
    {
        get => checked((ulong)_bytes.LongLength);
        set => throw new NotSupportedException(
            "A referenced read stream is immutable.");
    }

    public IRandomAccessStream CloneStream() =>
        new ImmutableRandomAccessStreamWithContentType(
            _bytes,
            ContentType);

    public Stream AsStream() => GetStream();

    public void Seek(ulong position)
    {
        if (position > (ulong)_bytes.LongLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position));
        }
        GetStream().Position = checked((long)position);
    }

    public void Dispose() =>
        Interlocked.Exchange(
            ref _stream,
            null)?.Dispose();

    private MemoryStream GetStream() =>
        Volatile.Read(ref _stream) ??
        throw new ObjectDisposedException(
            nameof(ImmutableRandomAccessStreamWithContentType));
}
