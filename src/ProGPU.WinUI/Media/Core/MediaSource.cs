using ProGPU.Media.Playback;
using Windows.Foundation.Collections;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Windows.Media.Core;

public sealed class MediaSource : IMediaPlaybackSource, IDisposable,
    IProGpuMediaPlaybackSource
{
    private readonly MediaSourceDescriptor _descriptor;
    private readonly object _playbackItemGate = new();
    private MediaPlaybackItem? _playbackItem;
    private int _disposed;

    private MediaSource(MediaSourceDescriptor descriptor)
    {
        _descriptor = descriptor;
        CustomProperties = new PropertySet();
    }

    public Uri? Uri => _descriptor.Uri;
    public bool IsOpen => Volatile.Read(ref _disposed) == 0;
    public TimeSpan? Duration { get; internal set; }
    public IPropertySet CustomProperties { get; }

    MediaSourceDescriptor
        IProGpuMediaPlaybackSource.ResolveDescriptor() =>
        ResolveDescriptor();

    MediaPlaybackRange
        IProGpuMediaPlaybackSource.ResolvePlaybackRange() =>
        MediaPlaybackRange.All;

    event EventHandler?
        IProGpuMediaPlaybackSource.SourceInvalidated
    {
        add { }
        remove { }
    }

    internal MediaSourceDescriptor ResolveDescriptor()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        return _descriptor;
    }

    internal void AssociatePlaybackItem(
        MediaPlaybackItem playbackItem)
    {
        ArgumentNullException.ThrowIfNull(playbackItem);
        lock (_playbackItemGate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_playbackItem is not null)
            {
                throw new InvalidOperationException(
                    "A MediaSource can be associated with only one MediaPlaybackItem.");
            }
            _playbackItem = playbackItem;
        }
    }

    internal MediaPlaybackItem? FindPlaybackItem()
    {
        lock (_playbackItemGate)
        {
            return _playbackItem;
        }
    }

    public static MediaSource CreateFromUri(Uri uri) =>
        new(MediaSourceDescriptor.FromUri(uri));

    public static MediaSource CreateFromStream(
        IRandomAccessStream stream,
        string contentType)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new MediaSource(
            MediaSourceDescriptor.FromStream(
                stream.AsStream(),
                contentType,
                leaveOpen: true));
    }

    public void Close() => Dispose();

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (_descriptor.Stream?.CanSeek == true)
        {
            _descriptor.Stream.Position = 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _descriptor.Dispose();
        }
    }
}
