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
    private readonly ExternalTimedTextSourceVector
        _externalTimedTextSources;
    private MediaPlaybackItem? _playbackItem;
    private int _disposed;

    private MediaSource(MediaSourceDescriptor descriptor)
    {
        _descriptor = descriptor;
        CustomProperties = new PropertySet();
        _externalTimedMetadataTracks =
            new ExternalTimedMetadataTrackVector(this);
        _externalTimedTextSources =
            new ExternalTimedTextSourceVector(this);
    }

    public Uri? Uri => _descriptor.Uri;
    public bool IsOpen => Volatile.Read(ref _disposed) == 0;
    public TimeSpan? Duration { get; internal set; }
    public IPropertySet CustomProperties { get; }
    public IObservableVector<TimedMetadataTrack>
        ExternalTimedMetadataTracks =>
        _externalTimedMetadataTracks;
    public IObservableVector<TimedTextSource>
        ExternalTimedTextSources =>
        _externalTimedTextSources;

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
            _externalTimedTextSources
                .DetachAllForOwnerDisposal();
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

    internal bool PublishResolvedTimedTextTracks(
        TimedTextSource source,
        IReadOnlyList<TimedMetadataTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tracks);
        if (Volatile.Read(ref _disposed) != 0 ||
            !_externalTimedTextSources.Contains(source))
        {
            return false;
        }

        var added = new List<TimedMetadataTrack>(
            tracks.Count);
        try
        {
            for (int index = 0;
                 index < tracks.Count;
                 index++)
            {
                TimedMetadataTrack track =
                    tracks[index] ??
                    throw new ArgumentException(
                        "Resolved track collections cannot contain null.",
                        nameof(tracks));
                _externalTimedMetadataTracks.Add(track);
                added.Add(track);
            }
            if (!_externalTimedTextSources
                    .TrySetResolvedTracks(
                        source,
                        added))
            {
                for (int index = added.Count - 1;
                     index >= 0;
                     index--)
                {
                    _externalTimedMetadataTracks.Remove(
                        added[index]);
                }
                return false;
            }
            return true;
        }
        catch
        {
            for (int index = added.Count - 1;
                 index >= 0;
                 index--)
            {
                _externalTimedMetadataTracks.Remove(
                    added[index]);
            }
            throw;
        }
    }

    private void RemoveResolvedTimedTextTracks(
        IReadOnlyList<TimedMetadataTrack> tracks)
    {
        for (int index = tracks.Count - 1;
             index >= 0;
             index--)
        {
            _externalTimedMetadataTracks.Remove(
                tracks[index]);
        }
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

    private sealed class ExternalTimedTextSourceVector :
        Collection<TimedTextSource>,
        IObservableVector<TimedTextSource>
    {
        private readonly MediaSource _owner;
        private readonly Dictionary<
            TimedTextSource,
            IReadOnlyList<TimedMetadataTrack>>
            _resolvedTracks =
                new(
                    ReferenceEqualityComparer.Instance);

        internal ExternalTimedTextSourceVector(
            MediaSource owner)
        {
            _owner = owner;
        }

        public event VectorChangedEventHandler<
            TimedTextSource>? VectorChanged;

        internal bool TrySetResolvedTracks(
            TimedTextSource source,
            IReadOnlyList<TimedMetadataTrack> tracks)
        {
            if (!Contains(source))
            {
                return false;
            }
            _resolvedTracks[source] = tracks;
            return true;
        }

        internal void DetachAllForOwnerDisposal()
        {
            for (int index = 0;
                 index < Count;
                 index++)
            {
                this[index].DetachFromSource(_owner);
            }
            _resolvedTracks.Clear();
        }

        protected override void InsertItem(
            int index,
            TimedTextSource item)
        {
            ArgumentNullException.ThrowIfNull(item);
            _owner.EnsureOpen();
            if (Contains(item))
            {
                throw new InvalidOperationException(
                    "A timed-text source cannot be inserted into the same MediaSource more than once.");
            }
            item.AttachToSource(_owner);
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
            item.BeginResolve(_owner);
        }

        protected override void SetItem(
            int index,
            TimedTextSource item)
        {
            ArgumentNullException.ThrowIfNull(item);
            _owner.EnsureOpen();
            TimedTextSource previous = this[index];
            if (ReferenceEquals(previous, item))
            {
                return;
            }
            if (Contains(item))
            {
                throw new InvalidOperationException(
                    "A timed-text source cannot be inserted into the same MediaSource more than once.");
            }

            item.AttachToSource(_owner);
            try
            {
                base.SetItem(index, item);
            }
            catch
            {
                item.DetachFromSource(_owner);
                throw;
            }
            Detach(previous);
            RaiseChanged(
                CollectionChange.ItemChanged,
                checked((uint)index));
            item.BeginResolve(_owner);
        }

        protected override void RemoveItem(int index)
        {
            _owner.EnsureOpen();
            TimedTextSource previous = this[index];
            base.RemoveItem(index);
            Detach(previous);
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
            var previous = new TimedTextSource[Count];
            CopyTo(previous, 0);
            base.ClearItems();
            for (int index = 0;
                 index < previous.Length;
                 index++)
            {
                Detach(previous[index]);
            }
            RaiseChanged(CollectionChange.Reset, 0);
        }

        private void Detach(TimedTextSource source)
        {
            source.DetachFromSource(_owner);
            if (_resolvedTracks.Remove(
                    source,
                    out IReadOnlyList<
                        TimedMetadataTrack>? tracks))
            {
                _owner.RemoveResolvedTimedTextTracks(
                    tracks);
            }
        }

        private void RaiseChanged(
            CollectionChange change,
            uint index) =>
            VectorChanged?.Invoke(
                this,
                new PlaybackTrackVectorChangedEventArgs(
                    change,
                    index));
    }
}
