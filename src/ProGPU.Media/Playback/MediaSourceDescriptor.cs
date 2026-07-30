using System.IO;

namespace ProGPU.Media.Playback;

public enum MediaSourceKind
{
    Uri,
    Stream,
    Custom
}

/// <summary>
/// Framework-neutral description passed to registered media providers.
/// Stream ownership stays with the descriptor until it is disposed.
/// </summary>
public sealed class MediaSourceDescriptor : IDisposable
{
    private readonly bool _leaveOpen;
    private int _disposed;

    private MediaSourceDescriptor(
        MediaSourceKind kind,
        Uri? uri,
        Stream? stream,
        object? customSource,
        string? contentType,
        bool leaveOpen)
    {
        Kind = kind;
        Uri = uri;
        Stream = stream;
        CustomSource = customSource;
        ContentType = contentType;
        _leaveOpen = leaveOpen;
    }

    public MediaSourceKind Kind { get; }
    public Uri? Uri { get; }
    public Stream? Stream { get; }
    public object? CustomSource { get; }
    public string? ContentType { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public static MediaSourceDescriptor FromUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new MediaSourceDescriptor(
            MediaSourceKind.Uri,
            uri,
            null,
            null,
            null,
            leaveOpen: true);
    }

    public static MediaSourceDescriptor FromStream(
        Stream stream,
        string? contentType = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException(
                "A media stream must be readable.",
                nameof(stream));
        }

        return new MediaSourceDescriptor(
            MediaSourceKind.Stream,
            null,
            stream,
            null,
            contentType,
            leaveOpen);
    }

    public static MediaSourceDescriptor FromCustom(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new MediaSourceDescriptor(
            MediaSourceKind.Custom,
            null,
            null,
            source,
            null,
            leaveOpen: true);
    }

    public void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 &&
            !_leaveOpen)
        {
            Stream?.Dispose();
        }
    }
}
