using Windows.Storage.Streams;

namespace Windows.Graphics.Imaging;

/// <summary>
/// Immutable encoded-image stream returned by media thumbnail APIs. Each
/// clone owns an independent cursor over the same immutable byte array.
/// </summary>
public sealed class ImageStream :
    IRandomAccessStreamWithContentType
{
    private readonly byte[] _bytes;
    private MemoryStream? _stream;

    internal ImageStream(
        byte[] bytes,
        string contentType)
        : this(
            bytes,
            contentType,
            ownsSnapshot: false)
    {
    }

    private ImageStream(
        byte[] bytes,
        string contentType,
        bool ownsSnapshot)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            contentType);
        // Providers own their result buffer. Snapshot it once at the public
        // ownership boundary so later provider mutation cannot alter an
        // already returned ImageStream; clones then share this snapshot.
        _bytes = ownsSnapshot
            ? bytes
            : bytes.ToArray();
        _stream = new MemoryStream(
            _bytes,
            writable: false);
        ContentType = contentType;
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
            "An encoded ImageStream is immutable.");
    }

    public IRandomAccessStream CloneStream() =>
        new ImageStream(
            _bytes,
            ContentType,
            ownsSnapshot: true);

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

    public void Close() => Dispose();

    public void Dispose() =>
        Interlocked.Exchange(
            ref _stream,
            null)?.Dispose();

    private MemoryStream GetStream() =>
        Volatile.Read(ref _stream) ??
        throw new ObjectDisposedException(
            nameof(ImageStream));
}
