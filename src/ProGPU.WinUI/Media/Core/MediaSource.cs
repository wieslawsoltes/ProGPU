using ProGPU.Media.Playback;
using System.Collections.ObjectModel;
using Windows.Foundation.Collections;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Windows.Media.Core;

public sealed class MediaSource : IMediaPlaybackSource, IDisposable,
    IProGpuMediaPlaybackSource
{
    private readonly MediaSourceDescriptor _descriptor;
    private readonly object _playbackItemGate = new();
    private readonly ExternalTimedMetadataTrackVector
        _externalTimedMetadataTracks;
    private MediaPlaybackItem? _playbackItem;
    private int _disposed;

    private MediaSource(MediaSourceDescriptor descriptor)
    {
        _descriptor = descriptor;
        CustomProperties = new PropertySet();
        _externalTimedMetadataTracks =
            new ExternalTimedMetadataTrackVector(this);
    }

    public Uri? Uri => _descriptor.Uri;
    public bool IsOpen => Volatile.Read(ref _disposed) == 0;
    public TimeSpan? Duration { get; internal set; }
    public IPropertySet CustomProperties { get; }
    public IObservableVector<TimedMetadataTrack>
        ExternalTimedMetadataTracks =>
        _externalTimedMetadataTracks;

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
        EnsureOpen();
        return _descriptor;
    }

    private void EnsureOpen() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

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
            for (int index = 0;
                 index < _externalTimedMetadataTracks.Count;
                 index++)
            {
                _externalTimedMetadataTracks[index]
                    .SetPlaybackItem(playbackItem);
            }
            playbackItem.ApplyExternalTimedMetadataTracks(
                _externalTimedMetadataTracks);
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

    private void OnExternalTimedMetadataTracksChanged(
        CollectionChange change,
        uint index)
    {
        MediaPlaybackItem? playbackItem;
        lock (_playbackItemGate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            playbackItem = _playbackItem;
        }

        playbackItem?.ApplyExternalTimedMetadataTracks(
            _externalTimedMetadataTracks,
            new PlaybackTrackVectorChangedEventArgs(
                change,
                index));
    }

    private sealed class ExternalTimedMetadataTrackVector :
        Collection<TimedMetadataTrack>,
        IObservableVector<TimedMetadataTrack>,
        IReadOnlyList<TimedMetadataTrack>
    {
        private readonly MediaSource _owner;

        public ExternalTimedMetadataTrackVector(
            MediaSource owner)
        {
            _owner = owner;
        }

        public event VectorChangedEventHandler<
            TimedMetadataTrack>? VectorChanged;

        protected override void InsertItem(
            int index,
            TimedMetadataTrack item)
        {
            ArgumentNullException.ThrowIfNull(item);
            _owner.EnsureOpen();
            if (Contains(item))
            {
                throw new InvalidOperationException(
                    "A timed-metadata track cannot be inserted into the same MediaSource more than once.");
            }
            item.AttachToSource(
                _owner,
                _owner.FindPlaybackItem());
            try
            {
                base.InsertItem(index, item);
            }
            catch
            {
                item.DetachFromSource(_owner);
                throw;
            }

            RaiseChanged(
                CollectionChange.ItemInserted,
                checked((uint)index));
        }

        protected override void SetItem(
            int index,
            TimedMetadataTrack item)
        {
            ArgumentNullException.ThrowIfNull(item);
            _owner.EnsureOpen();
            TimedMetadataTrack previous = this[index];
            if (ReferenceEquals(previous, item))
            {
                return;
            }
            if (Contains(item))
            {
                throw new InvalidOperationException(
                    "A timed-metadata track cannot be inserted into the same MediaSource more than once.");
            }

            item.AttachToSource(
                _owner,
                _owner.FindPlaybackItem());
            try
            {
                base.SetItem(index, item);
            }
            catch
            {
                item.DetachFromSource(_owner);
                throw;
            }

            previous.DetachFromSource(_owner);
            RaiseChanged(
                CollectionChange.ItemChanged,
                checked((uint)index));
        }

        protected override void RemoveItem(int index)
        {
            _owner.EnsureOpen();
            TimedMetadataTrack previous = this[index];
            base.RemoveItem(index);
            previous.DetachFromSource(_owner);
            RaiseChanged(
                CollectionChange.ItemRemoved,
                checked((uint)index));
        }

        protected override void ClearItems()
        {
            _owner.EnsureOpen();
            if (Count == 0)
            {
                return;
            }

            var previous = new TimedMetadataTrack[Count];
            CopyTo(previous, 0);
            base.ClearItems();
            for (int index = 0;
                 index < previous.Length;
                 index++)
            {
                previous[index].DetachFromSource(_owner);
            }
            RaiseChanged(CollectionChange.Reset, 0);
        }

        private void RaiseChanged(
            CollectionChange change,
            uint index)
        {
            _owner.OnExternalTimedMetadataTracksChanged(
                change,
                index);
            VectorChanged?.Invoke(
                this,
                new PlaybackTrackVectorChangedEventArgs(
                    change,
                    index));
        }
    }
}
