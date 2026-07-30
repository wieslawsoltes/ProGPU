using System.Collections;
using ProGPU.Media.Playback;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace Windows.Media.Core
{
    public enum MediaTrackKind
    {
        Audio = 0,
        Video = 1,
        TimedMetadata = 2
    }

    public enum MediaDecoderStatus
    {
        FullySupported = 0,
        UnsupportedSubtype = 1,
        UnsupportedEncoderProperties = 2,
        Degraded = 3
    }

    public enum MediaSourceStatus
    {
        FullySupported = 0,
        Unknown = 1
    }

    public enum AudioDecoderDegradation
    {
        None = 0,
        DownmixTo2Channels = 1,
        DownmixTo6Channels = 2,
        DownmixTo8Channels = 3
    }

    public enum AudioDecoderDegradationReason
    {
        None = 0,
        LicensingRequirement = 1,
        SpatialAudioNotSupported = 2
    }

    public enum TimedMetadataKind
    {
        Caption = 0,
        Chapter = 1,
        Custom = 2,
        Data = 3,
        Description = 4,
        Subtitle = 5,
        ImageSubtitle = 6,
        Speech = 7
    }

    public interface IMediaCue
    {
        TimeSpan Duration { get; set; }
        string Id { get; set; }
        TimeSpan StartTime { get; set; }
    }

    public interface IMediaTrack
    {
        string Id { get; }
        string Label { get; set; }
        string Language { get; }
        MediaTrackKind TrackKind { get; }
    }

    public interface ISingleSelectMediaTrackList
    {
        int SelectedIndex { get; set; }

        event TypedEventHandler<
            ISingleSelectMediaTrackList,
            object>? SelectedIndexChanged;
    }

    public sealed class AudioTrackSupportInfo
    {
        internal AudioTrackSupportInfo(
            MediaPlaybackTrackSupport support)
        {
            DecoderStatus =
                TrackSupportMapping.ToDecoderStatus(support);
            MediaSourceStatus =
                TrackSupportMapping.ToSourceStatus(support);
        }

        public MediaDecoderStatus DecoderStatus { get; }
        public AudioDecoderDegradation Degradation =>
            AudioDecoderDegradation.None;
        public AudioDecoderDegradationReason DegradationReason =>
            AudioDecoderDegradationReason.None;
        public MediaSourceStatus MediaSourceStatus { get; }
    }

    public sealed class VideoTrackSupportInfo
    {
        internal VideoTrackSupportInfo(
            MediaPlaybackTrackSupport support)
        {
            DecoderStatus =
                TrackSupportMapping.ToDecoderStatus(support);
            MediaSourceStatus =
                TrackSupportMapping.ToSourceStatus(support);
        }

        public MediaDecoderStatus DecoderStatus { get; }
        public MediaSourceStatus MediaSourceStatus { get; }
    }

    public sealed class AudioTrackOpenFailedEventArgs : EventArgs
    {
        internal AudioTrackOpenFailedEventArgs(
            Exception extendedError)
        {
            ExtendedError = extendedError ??
                throw new ArgumentNullException(
                    nameof(extendedError));
        }

        public Exception ExtendedError { get; }
    }

    public sealed class VideoTrackOpenFailedEventArgs : EventArgs
    {
        internal VideoTrackOpenFailedEventArgs(
            Exception extendedError)
        {
            ExtendedError = extendedError ??
                throw new ArgumentNullException(
                    nameof(extendedError));
        }

        public Exception ExtendedError { get; }
    }

    public sealed class AudioTrack : IMediaTrack
    {
        private MediaPlaybackTrackDescriptor _descriptor;
        private string _label;

        internal AudioTrack(
            MediaPlaybackItem playbackItem,
            in MediaPlaybackTrackDescriptor descriptor)
        {
            PlaybackItem = playbackItem;
            _descriptor = descriptor;
            _label = descriptor.Label;
            SupportInfo =
                new AudioTrackSupportInfo(descriptor.Support);
        }

        public string Id => _descriptor.ProviderTrackId;
        public string Label
        {
            get => _label;
            set => _label = value ?? string.Empty;
        }
        public string Language => _descriptor.Language;
        public string Name => _descriptor.Name;
        public MediaPlaybackItem PlaybackItem { get; }
        public AudioTrackSupportInfo SupportInfo { get; private set; }
        public MediaTrackKind TrackKind => MediaTrackKind.Audio;

        public event TypedEventHandler<
            AudioTrack,
            AudioTrackOpenFailedEventArgs>? OpenFailed;

        public AudioEncodingProperties GetEncodingProperties() =>
            new()
            {
                Subtype = _descriptor.Encoding.Subtype,
                Bitrate = _descriptor.Encoding.Bitrate,
                SampleRate = _descriptor.Encoding.SampleRate,
                ChannelCount =
                    _descriptor.Encoding.ChannelCount
            };

        internal bool Update(
            in MediaPlaybackTrackDescriptor descriptor)
        {
            if (_descriptor == descriptor)
            {
                return false;
            }
            _descriptor = descriptor;
            SupportInfo =
                new AudioTrackSupportInfo(descriptor.Support);
            return true;
        }

        internal void RaiseOpenFailed(Exception error) =>
            OpenFailed?.Invoke(
                this,
                new AudioTrackOpenFailedEventArgs(error));
    }

    public sealed class VideoTrack : IMediaTrack
    {
        private MediaPlaybackTrackDescriptor _descriptor;
        private string _label;

        internal VideoTrack(
            MediaPlaybackItem playbackItem,
            in MediaPlaybackTrackDescriptor descriptor)
        {
            PlaybackItem = playbackItem;
            _descriptor = descriptor;
            _label = descriptor.Label;
            SupportInfo =
                new VideoTrackSupportInfo(descriptor.Support);
        }

        public string Id => _descriptor.ProviderTrackId;
        public string Label
        {
            get => _label;
            set => _label = value ?? string.Empty;
        }
        public string Language => _descriptor.Language;
        public string Name => _descriptor.Name;
        public MediaPlaybackItem PlaybackItem { get; }
        public VideoTrackSupportInfo SupportInfo { get; private set; }
        public MediaTrackKind TrackKind => MediaTrackKind.Video;

        public event TypedEventHandler<
            VideoTrack,
            VideoTrackOpenFailedEventArgs>? OpenFailed;

        public VideoEncodingProperties GetEncodingProperties() =>
            new()
            {
                Subtype = _descriptor.Encoding.Subtype,
                Bitrate = _descriptor.Encoding.Bitrate,
                Width = _descriptor.Encoding.Width,
                Height = _descriptor.Encoding.Height,
                FrameRate =
                {
                    Numerator =
                        _descriptor.Encoding
                            .FrameRateNumerator,
                    Denominator =
                        _descriptor.Encoding
                            .FrameRateDenominator
                }
            };

        internal bool Update(
            in MediaPlaybackTrackDescriptor descriptor)
        {
            if (_descriptor == descriptor)
            {
                return false;
            }
            _descriptor = descriptor;
            SupportInfo =
                new VideoTrackSupportInfo(descriptor.Support);
            return true;
        }

        internal void RaiseOpenFailed(Exception error) =>
            OpenFailed?.Invoke(
                this,
                new VideoTrackOpenFailedEventArgs(error));
    }

    public sealed class MediaCueEventArgs : EventArgs
    {
        internal MediaCueEventArgs(IMediaCue cue)
        {
            Cue = cue ??
                throw new ArgumentNullException(nameof(cue));
        }

        public IMediaCue Cue { get; }
    }

    public sealed class TimedMetadataTrackFailedEventArgs :
        EventArgs
    {
        internal TimedMetadataTrackFailedEventArgs(
            Exception error)
        {
            Error = error ??
                throw new ArgumentNullException(nameof(error));
        }

        public Exception Error { get; }
    }

    public sealed class TimedMetadataTrack :
        IMediaTrack,
        IMediaTimedCueTimelineClient<IMediaCue>
    {
        private readonly MediaTimedCueTimeline<IMediaCue>
            _timeline;
        private readonly Dictionary<string, TimedTextCue>
            _providerCues = new(StringComparer.Ordinal);
        private readonly bool _providerOwned;
        private MediaPlaybackTrackDescriptor _descriptor;
        private MediaSource? _sourceOwner;
        private MediaPlaybackItem? _playbackItem;
        private string _label;
        private bool _applyingProviderCues;

        public TimedMetadataTrack(
            string id,
            string language,
            TimedMetadataKind kind)
            : this(
                playbackItem: null,
                new MediaPlaybackTrackDescriptor(
                    id ?? throw new ArgumentNullException(
                        nameof(id)),
                    MediaPlaybackTrackKind.TimedMetadata,
                    string.Empty,
                    string.Empty,
                    language ?? throw new ArgumentNullException(
                        nameof(language)),
                    MediaPlaybackTrackEncoding.Empty,
                    MediaPlaybackTrackSupport.Unknown,
                    ToProviderKind(kind)),
                providerOwned: false)
        {
        }

        internal TimedMetadataTrack(
            MediaPlaybackItem? playbackItem,
            in MediaPlaybackTrackDescriptor descriptor,
            bool providerOwned = true)
        {
            if (descriptor.Kind !=
                MediaPlaybackTrackKind.TimedMetadata)
            {
                throw new ArgumentException(
                    "A timed metadata track requires a timed-metadata descriptor.",
                    nameof(descriptor));
            }

            _playbackItem = playbackItem;
            _providerOwned = providerOwned;
            _descriptor = descriptor;
            _label = descriptor.Label;
            _timeline =
                new MediaTimedCueTimeline<IMediaCue>(this);
        }

        public IReadOnlyList<IMediaCue> ActiveCues =>
            _timeline.ActiveCues;
        public IReadOnlyList<IMediaCue> Cues =>
            _timeline.Cues;
        public string DispatchType =>
            _descriptor.DispatchType;
        public string Id => _descriptor.ProviderTrackId;
        public string Label
        {
            get => _label;
            set => _label = value ?? string.Empty;
        }
        public string Language => _descriptor.Language;
        public string Name => _descriptor.Name;
        public MediaPlaybackItem? PlaybackItem =>
            _playbackItem;
        public TimedMetadataKind TimedMetadataKind =>
            ToPublicKind(_descriptor.TimedMetadataKind);
        public MediaTrackKind TrackKind =>
            MediaTrackKind.TimedMetadata;

        public event TypedEventHandler<
            TimedMetadataTrack,
            MediaCueEventArgs>? CueEntered;
        public event TypedEventHandler<
            TimedMetadataTrack,
            MediaCueEventArgs>? CueExited;
        public event TypedEventHandler<
            TimedMetadataTrack,
            TimedMetadataTrackFailedEventArgs>? TrackFailed;

        public void AddCue(IMediaCue cue)
        {
            ArgumentNullException.ThrowIfNull(cue);
            if (_timeline.AddCue(cue))
            {
                SubscribeCueTiming(cue);
                _playbackItem?.RequestTimedMetadataRefresh();
            }
        }

        public void RemoveCue(IMediaCue cue)
        {
            ArgumentNullException.ThrowIfNull(cue);
            if (_timeline.RemoveCue(cue))
            {
                UnsubscribeCueTiming(cue);
                if (cue is TimedTextCue timedTextCue &&
                    _providerOwned)
                {
                    string? providerCueId = null;
                    foreach (KeyValuePair<string, TimedTextCue>
                                 pair in _providerCues)
                    {
                        if (ReferenceEquals(
                                pair.Value,
                                timedTextCue))
                        {
                            providerCueId = pair.Key;
                            break;
                        }
                    }
                    if (providerCueId is not null)
                    {
                        _providerCues.Remove(providerCueId);
                    }
                }
                _playbackItem?.RequestTimedMetadataRefresh();
            }
        }

        internal void ApplyProviderCues(
            MediaPlaybackTimedMetadataCueSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (!_providerOwned ||
                !StringComparer.Ordinal.Equals(
                    Id,
                    snapshot.ProviderTrackId))
            {
                return;
            }

            var retainedIds = new HashSet<string>(
                StringComparer.Ordinal);
            bool changed = false;
            _applyingProviderCues = true;
            try
            {
                for (int index = 0;
                     index < snapshot.Cues.Count;
                     index++)
                {
                    MediaPlaybackTimedMetadataCueDescriptor
                        descriptor = snapshot.Cues[index];
                    retainedIds.Add(descriptor.CueId);
                    if (!_providerCues.TryGetValue(
                            descriptor.CueId,
                            out TimedTextCue? cue))
                    {
                        cue = new TimedTextCue
                        {
                            Id = descriptor.CueId
                        };
                        cue.ApplyProviderState(
                            descriptor.StartTime,
                            descriptor.Duration,
                            descriptor.Text);
                        _providerCues.Add(
                            descriptor.CueId,
                            cue);
                        _timeline.AddCue(cue);
                        SubscribeCueTiming(cue);
                        changed = true;
                    }
                    else
                    {
                        changed |= cue.ApplyProviderState(
                            descriptor.StartTime,
                            descriptor.Duration,
                            descriptor.Text);
                    }
                }

                if (_providerCues.Count != retainedIds.Count)
                {
                    string[] removedIds = _providerCues.Keys
                        .Where(id => !retainedIds.Contains(id))
                        .ToArray();
                    for (int index = 0;
                         index < removedIds.Length;
                         index++)
                    {
                        TimedTextCue cue =
                            _providerCues[removedIds[index]];
                        _providerCues.Remove(removedIds[index]);
                        _timeline.RemoveCue(cue);
                        UnsubscribeCueTiming(cue);
                        changed = true;
                    }
                }
            }
            finally
            {
                _applyingProviderCues = false;
            }

            if (changed)
            {
                _timeline.InvalidateSchedule();
                _playbackItem?.RequestTimedMetadataRefresh();
            }
        }

        internal bool Update(
            in MediaPlaybackTrackDescriptor descriptor)
        {
            if (_descriptor == descriptor)
            {
                return false;
            }
            _descriptor = descriptor;
            return true;
        }

        internal void RaiseCueEntered(IMediaCue cue) =>
            CueEntered?.Invoke(
                this,
                new MediaCueEventArgs(cue));

        internal void RaiseCueExited(IMediaCue cue) =>
            CueExited?.Invoke(
                this,
                new MediaCueEventArgs(cue));

        internal void RaiseTrackFailed(Exception error) =>
            TrackFailed?.Invoke(
                this,
                new TimedMetadataTrackFailedEventArgs(error));

        internal void AttachToSource(
            MediaSource source,
            MediaPlaybackItem? playbackItem)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (_sourceOwner is not null &&
                !ReferenceEquals(_sourceOwner, source))
            {
                throw new InvalidOperationException(
                    "A timed-metadata track can belong to only one MediaSource.");
            }
            _sourceOwner = source;
            _playbackItem = playbackItem;
        }

        internal void SetPlaybackItem(
            MediaPlaybackItem? playbackItem) =>
            _playbackItem = playbackItem;

        internal void DetachFromSource(MediaSource source)
        {
            if (!ReferenceEquals(_sourceOwner, source))
            {
                return;
            }
            _timeline.Reset();
            _sourceOwner = null;
            _playbackItem = null;
        }

        internal void Synchronize(
            TimeSpan position,
            bool enabled) =>
            _timeline.Synchronize(position, enabled);

        internal void ResetTimeline() =>
            _timeline.Reset();

        TimeSpan
            IMediaTimedCueTimelineClient<IMediaCue>.GetStartTime(
                IMediaCue cue) =>
            cue.StartTime;

        TimeSpan
            IMediaTimedCueTimelineClient<IMediaCue>.GetDuration(
                IMediaCue cue) =>
            cue.Duration;

        void
            IMediaTimedCueTimelineClient<IMediaCue>.OnCueEntered(
                IMediaCue cue) =>
            RaiseCueEntered(cue);

        void
            IMediaTimedCueTimelineClient<IMediaCue>.OnCueExited(
                IMediaCue cue) =>
            RaiseCueExited(cue);

        private void OnCueTimingChanged(
            object? sender,
            EventArgs args)
        {
            _timeline.InvalidateSchedule();
            if (!_applyingProviderCues)
            {
                _playbackItem?.RequestTimedMetadataRefresh();
            }
        }

        private void SubscribeCueTiming(IMediaCue cue)
        {
            if (cue is DataCue dataCue)
            {
                dataCue.TimingChanged += OnCueTimingChanged;
            }
            else if (cue is TimedTextCue timedTextCue)
            {
                timedTextCue.TimingChanged += OnCueTimingChanged;
            }
        }

        private void UnsubscribeCueTiming(IMediaCue cue)
        {
            if (cue is DataCue dataCue)
            {
                dataCue.TimingChanged -= OnCueTimingChanged;
            }
            else if (cue is TimedTextCue timedTextCue)
            {
                timedTextCue.TimingChanged -= OnCueTimingChanged;
            }
        }

        private static TimedMetadataKind ToPublicKind(
            MediaPlaybackTimedMetadataKind kind) =>
            (TimedMetadataKind)(int)kind;

        private static MediaPlaybackTimedMetadataKind
            ToProviderKind(TimedMetadataKind kind)
        {
            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(kind));
            }
            return (MediaPlaybackTimedMetadataKind)(int)kind;
        }
    }

    internal static class TrackSupportMapping
    {
        internal static MediaDecoderStatus ToDecoderStatus(
            MediaPlaybackTrackSupport support) =>
            support switch
            {
                MediaPlaybackTrackSupport.Supported =>
                    MediaDecoderStatus.FullySupported,
                MediaPlaybackTrackSupport.Degraded =>
                    MediaDecoderStatus.Degraded,
                MediaPlaybackTrackSupport.Unsupported =>
                    MediaDecoderStatus.UnsupportedSubtype,
                _ =>
                    MediaDecoderStatus
                        .UnsupportedEncoderProperties
            };

        internal static MediaSourceStatus ToSourceStatus(
            MediaPlaybackTrackSupport support) =>
            support == MediaPlaybackTrackSupport.Unknown
                ? MediaSourceStatus.Unknown
                : MediaSourceStatus.FullySupported;
    }
}

namespace Windows.Media.Playback
{
    using Windows.Media.Core;

    public enum TimedMetadataTrackPresentationMode
    {
        Disabled = 0,
        Hidden = 1,
        ApplicationPresented = 2,
        PlatformPresented = 3
    }

    public sealed class
        TimedMetadataPresentationModeChangedEventArgs :
        EventArgs
    {
        internal TimedMetadataPresentationModeChangedEventArgs(
            TimedMetadataTrack track,
            TimedMetadataTrackPresentationMode
                oldPresentationMode,
            TimedMetadataTrackPresentationMode
                newPresentationMode)
        {
            Track = track ??
                throw new ArgumentNullException(nameof(track));
            OldPresentationMode = oldPresentationMode;
            NewPresentationMode = newPresentationMode;
        }

        public TimedMetadataTrack Track { get; }
        public TimedMetadataTrackPresentationMode
            OldPresentationMode { get; }
        public TimedMetadataTrackPresentationMode
            NewPresentationMode { get; }
    }

    internal sealed class PlaybackTrackSelectionRequestedEventArgs :
        EventArgs
    {
        public PlaybackTrackSelectionRequestedEventArgs(
            MediaPlaybackTrackKind kind,
            int index)
        {
            Kind = kind;
            Index = index;
        }

        public MediaPlaybackTrackKind Kind { get; }
        public int Index { get; }
    }

    internal sealed class
        PlaybackTimedMetadataPresentationModeRequestedEventArgs :
        EventArgs
    {
        public
            PlaybackTimedMetadataPresentationModeRequestedEventArgs(
                int index,
                MediaPlaybackTimedMetadataPresentationMode mode)
        {
            Index = index;
            Mode = mode;
        }

        public int Index { get; }
        public MediaPlaybackTimedMetadataPresentationMode Mode
        {
            get;
        }
    }

    internal sealed class PlaybackTrackVectorChangedEventArgs :
        IVectorChangedEventArgs
    {
        internal PlaybackTrackVectorChangedEventArgs(
            CollectionChange collectionChange,
            uint index)
        {
            CollectionChange = collectionChange;
            Index = index;
        }

        public CollectionChange CollectionChange { get; }
        public uint Index { get; }
    }

    internal sealed class MediaPlaybackTrackListState<TTrack>
        where TTrack : class
    {
        private readonly MediaPlaybackItem _owner;
        private readonly MediaPlaybackTrackKind _kind;
        private readonly ISingleSelectMediaTrackList _sender;
        private readonly Func<
            MediaPlaybackTrackDescriptor,
            TTrack> _createTrack;
        private readonly Func<
            TTrack,
            MediaPlaybackTrackDescriptor,
            bool> _updateTrack;
        private readonly Func<TTrack, string> _getTrackId;
        private TTrack[] _items = [];
        private int _selectedIndex = -1;

        internal MediaPlaybackTrackListState(
            MediaPlaybackItem owner,
            MediaPlaybackTrackKind kind,
            ISingleSelectMediaTrackList sender,
            Func<MediaPlaybackTrackDescriptor, TTrack>
                createTrack,
            Func<
                TTrack,
                MediaPlaybackTrackDescriptor,
                bool> updateTrack,
            Func<TTrack, string> getTrackId)
        {
            _owner = owner;
            _kind = kind;
            _sender = sender;
            _createTrack = createTrack;
            _updateTrack = updateTrack;
            _getTrackId = getTrackId;
        }

        internal int Count => _items.Length;
        internal uint Size => checked((uint)_items.Length);
        internal TTrack this[int index] =>
            index >= 0 && index < _items.Length
                ? _items[index]
                : throw new ArgumentOutOfRangeException(
                    nameof(index));
        internal int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                ValidateSelectedIndex(value, _items.Length);
                if (_selectedIndex == value)
                {
                    return;
                }

                _owner.RequestTrackSelection(_kind, value);
                SetSelectedIndex(value);
            }
        }

        internal event TypedEventHandler<
            ISingleSelectMediaTrackList,
            object>? SelectedIndexChanged;

        internal TTrack GetAt(uint index) =>
            index < (uint)_items.Length
                ? _items[(int)index]
                : throw new ArgumentOutOfRangeException(
                    nameof(index));

        internal uint GetMany(
            uint startIndex,
            TTrack[] items)
        {
            ArgumentNullException.ThrowIfNull(items);
            int start = checked((int)startIndex);
            if (start >= _items.Length || items.Length == 0)
            {
                return 0;
            }

            int count = Math.Min(
                items.Length,
                _items.Length - start);
            Array.Copy(_items, start, items, 0, count);
            return checked((uint)count);
        }

        internal bool IndexOf(TTrack value, out uint index)
        {
            int found = Array.IndexOf(_items, value);
            index = found < 0 ? 0u : checked((uint)found);
            return found >= 0;
        }

        internal IEnumerator<TTrack> GetEnumerator() =>
            ((IEnumerable<TTrack>)_items).GetEnumerator();

        internal IReadOnlyList<IVectorChangedEventArgs> Update(
            IReadOnlyList<MediaPlaybackTrackDescriptor>
                descriptors,
            int selectedIndex)
        {
            ArgumentNullException.ThrowIfNull(descriptors);
            ValidateSelectedIndex(
                selectedIndex,
                descriptors.Count);

            var changes = new List<IVectorChangedEventArgs>();
            if (_items.Length == 0)
            {
                _items = CreateTracks(descriptors);
                for (int index = 0;
                     index < _items.Length;
                     index++)
                {
                    changes.Add(
                        new PlaybackTrackVectorChangedEventArgs(
                            CollectionChange.ItemInserted,
                            checked((uint)index)));
                }
            }
            else if (HaveSameIdentity(descriptors))
            {
                for (int index = 0;
                     index < _items.Length;
                     index++)
                {
                    if (_updateTrack(
                            _items[index],
                            descriptors[index]))
                    {
                        changes.Add(
                            new PlaybackTrackVectorChangedEventArgs(
                                CollectionChange.ItemChanged,
                                checked((uint)index)));
                    }
                }
            }
            else
            {
                _items = CreateTracks(descriptors);
                changes.Add(
                    new PlaybackTrackVectorChangedEventArgs(
                        CollectionChange.Reset,
                        0));
            }

            SetSelectedIndex(selectedIndex);
            return changes;
        }

        private TTrack[] CreateTracks(
            IReadOnlyList<MediaPlaybackTrackDescriptor>
                descriptors)
        {
            var result = new TTrack[descriptors.Count];
            for (int index = 0;
                 index < result.Length;
                 index++)
            {
                MediaPlaybackTrackDescriptor descriptor =
                    descriptors[index];
                result[index] =
                    _createTrack(descriptor);
            }
            return result;
        }

        private bool HaveSameIdentity(
            IReadOnlyList<MediaPlaybackTrackDescriptor>
                descriptors)
        {
            if (_items.Length != descriptors.Count)
            {
                return false;
            }
            for (int index = 0;
                 index < _items.Length;
                 index++)
            {
                if (!StringComparer.Ordinal.Equals(
                        _getTrackId(_items[index]),
                        descriptors[index].ProviderTrackId))
                {
                    return false;
                }
            }
            return true;
        }

        private void SetSelectedIndex(int value)
        {
            if (_selectedIndex == value)
            {
                return;
            }
            _selectedIndex = value;
            SelectedIndexChanged?.Invoke(
                _sender,
                EventArgs.Empty);
        }

        private static void ValidateSelectedIndex(
            int value,
            int count)
        {
            if (value < -1 || value >= count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
        }
    }

    public sealed class MediaPlaybackAudioTrackList :
        IReadOnlyList<AudioTrack>,
        ISingleSelectMediaTrackList
    {
        private readonly MediaPlaybackTrackListState<AudioTrack>
            _state;

        internal MediaPlaybackAudioTrackList(
            MediaPlaybackItem owner)
        {
            _state = new(
                owner,
                MediaPlaybackTrackKind.Audio,
                this,
                descriptor =>
                    new AudioTrack(owner, in descriptor),
                static (track, descriptor) =>
                    track.Update(in descriptor),
                static track => track.Id);
        }

        public int Count => _state.Count;
        public uint Size => _state.Size;
        public AudioTrack this[int index] => _state[index];
        public int SelectedIndex
        {
            get => _state.SelectedIndex;
            set => _state.SelectedIndex = value;
        }
        public event TypedEventHandler<
            ISingleSelectMediaTrackList,
            object>? SelectedIndexChanged
        {
            add => _state.SelectedIndexChanged += value;
            remove => _state.SelectedIndexChanged -= value;
        }
        public AudioTrack GetAt(uint index) =>
            _state.GetAt(index);
        public uint GetMany(
            uint startIndex,
            AudioTrack[] items) =>
            _state.GetMany(startIndex, items);
        public bool IndexOf(
            AudioTrack value,
            out uint index) =>
            _state.IndexOf(value, out index);
        public IEnumerator<AudioTrack> GetEnumerator() =>
            _state.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() =>
            GetEnumerator();

        internal IReadOnlyList<IVectorChangedEventArgs> Update(
            IReadOnlyList<MediaPlaybackTrackDescriptor>
                descriptors,
            int selectedIndex) =>
            _state.Update(descriptors, selectedIndex);
    }

    public sealed class MediaPlaybackVideoTrackList :
        IReadOnlyList<VideoTrack>,
        ISingleSelectMediaTrackList
    {
        private readonly MediaPlaybackTrackListState<VideoTrack>
            _state;

        internal MediaPlaybackVideoTrackList(
            MediaPlaybackItem owner)
        {
            _state = new(
                owner,
                MediaPlaybackTrackKind.Video,
                this,
                descriptor =>
                    new VideoTrack(owner, in descriptor),
                static (track, descriptor) =>
                    track.Update(in descriptor),
                static track => track.Id);
        }

        public int Count => _state.Count;
        public uint Size => _state.Size;
        public VideoTrack this[int index] => _state[index];
        public int SelectedIndex
        {
            get => _state.SelectedIndex;
            set => _state.SelectedIndex = value;
        }
        public event TypedEventHandler<
            ISingleSelectMediaTrackList,
            object>? SelectedIndexChanged
        {
            add => _state.SelectedIndexChanged += value;
            remove => _state.SelectedIndexChanged -= value;
        }
        public VideoTrack GetAt(uint index) =>
            _state.GetAt(index);
        public uint GetMany(
            uint startIndex,
            VideoTrack[] items) =>
            _state.GetMany(startIndex, items);
        public bool IndexOf(
            VideoTrack value,
            out uint index) =>
            _state.IndexOf(value, out index);
        public IEnumerator<VideoTrack> GetEnumerator() =>
            _state.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() =>
            GetEnumerator();

        internal IReadOnlyList<IVectorChangedEventArgs> Update(
            IReadOnlyList<MediaPlaybackTrackDescriptor>
                descriptors,
            int selectedIndex) =>
            _state.Update(descriptors, selectedIndex);
    }

    public sealed class MediaPlaybackTimedMetadataTrackList :
        IReadOnlyList<TimedMetadataTrack>
    {
        private readonly MediaPlaybackItem _owner;
        private TimedMetadataTrack[] _providerItems = [];
        private TimedMetadataTrack[] _externalItems = [];
        private TimedMetadataTrack[] _items = [];
        private TimedMetadataTrackPresentationMode[] _modes = [];

        internal MediaPlaybackTimedMetadataTrackList(
            MediaPlaybackItem owner)
        {
            _owner = owner;
        }

        public int Count => _items.Length;
        public uint Size => checked((uint)_items.Length);
        public TimedMetadataTrack this[int index] =>
            index >= 0 && index < _items.Length
                ? _items[index]
                : throw new ArgumentOutOfRangeException(
                    nameof(index));

        public event TypedEventHandler<
            MediaPlaybackTimedMetadataTrackList,
            TimedMetadataPresentationModeChangedEventArgs>?
            PresentationModeChanged;

        public TimedMetadataTrack GetAt(uint index) =>
            index < (uint)_items.Length
                ? _items[(int)index]
                : throw new ArgumentOutOfRangeException(
                    nameof(index));

        public uint GetMany(
            uint startIndex,
            TimedMetadataTrack[] items)
        {
            ArgumentNullException.ThrowIfNull(items);
            int start = checked((int)startIndex);
            if (start >= _items.Length || items.Length == 0)
            {
                return 0;
            }

            int count = Math.Min(
                items.Length,
                _items.Length - start);
            Array.Copy(_items, start, items, 0, count);
            return checked((uint)count);
        }

        public TimedMetadataTrackPresentationMode
            GetPresentationMode(uint index) =>
            index < (uint)_modes.Length
                ? _modes[(int)index]
                : throw new ArgumentOutOfRangeException(
                    nameof(index));

        public bool IndexOf(
            TimedMetadataTrack value,
            out uint index)
        {
            int found = Array.IndexOf(_items, value);
            index = found < 0 ? 0u : checked((uint)found);
            return found >= 0;
        }

        public void SetPresentationMode(
            uint index,
            TimedMetadataTrackPresentationMode value)
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            if (index >= (uint)_items.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index));
            }

            int itemIndex = checked((int)index);
            TimedMetadataTrackPresentationMode oldValue =
                _modes[itemIndex];
            if (oldValue == value)
            {
                return;
            }

            if (itemIndex < _providerItems.Length)
            {
                _owner.RequestTimedMetadataPresentationMode(
                    itemIndex,
                    (MediaPlaybackTimedMetadataPresentationMode)
                        (int)value);
            }
            _modes[itemIndex] = value;
            PresentationModeChanged?.Invoke(
                this,
                new TimedMetadataPresentationModeChangedEventArgs(
                    _items[itemIndex],
                    oldValue,
                    value));
            _owner.RequestTimedMetadataRefresh();
        }

        public IEnumerator<TimedMetadataTrack> GetEnumerator() =>
            ((IEnumerable<TimedMetadataTrack>)_items)
            .GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() =>
            GetEnumerator();

        internal IReadOnlyList<IVectorChangedEventArgs> Update(
            IReadOnlyList<MediaPlaybackTrackDescriptor>
                descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptors);
            var changes = new List<IVectorChangedEventArgs>();
            if (HaveSameProviderIdentity(descriptors))
            {
                for (int index = 0;
                     index < _providerItems.Length;
                     index++)
                {
                    MediaPlaybackTrackDescriptor descriptor =
                        descriptors[index];
                    if (_providerItems[index].Update(
                            in descriptor))
                    {
                        changes.Add(
                            new PlaybackTrackVectorChangedEventArgs(
                                CollectionChange.ItemChanged,
                                checked((uint)index)));
                    }
                }
            }
            else
            {
                int previousProviderCount =
                    _providerItems.Length;
                for (int index = 0;
                     index < _providerItems.Length;
                     index++)
                {
                    _providerItems[index].ResetTimeline();
                }
                _providerItems =
                    CreateProviderTracks(descriptors);
                RebuildCombinedItems();

                if (previousProviderCount == 0 &&
                    _externalItems.Length == 0)
                {
                    for (int index = 0;
                         index < _providerItems.Length;
                         index++)
                    {
                        changes.Add(
                            new
                                PlaybackTrackVectorChangedEventArgs(
                                    CollectionChange.ItemInserted,
                                    checked((uint)index)));
                    }
                }
                else
                {
                    changes.Add(
                        new PlaybackTrackVectorChangedEventArgs(
                            CollectionChange.Reset,
                            0));
                }
            }
            return changes;
        }

        internal IVectorChangedEventArgs
            UpdateExternalTracks(
                IReadOnlyList<TimedMetadataTrack> tracks,
                IVectorChangedEventArgs change)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            ArgumentNullException.ThrowIfNull(change);
            _externalItems = new TimedMetadataTrack[tracks.Count];
            for (int index = 0;
                 index < _externalItems.Length;
                 index++)
            {
                _externalItems[index] = tracks[index];
            }
            RebuildCombinedItems();
            return new PlaybackTrackVectorChangedEventArgs(
                change.CollectionChange,
                change.CollectionChange ==
                    CollectionChange.Reset
                    ? 0u
                    : checked(
                        (uint)_providerItems.Length +
                        change.Index));
        }

        internal void InitializeExternalTracks(
            IReadOnlyList<TimedMetadataTrack> tracks)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            _externalItems = new TimedMetadataTrack[tracks.Count];
            for (int index = 0;
                 index < _externalItems.Length;
                 index++)
            {
                _externalItems[index] = tracks[index];
            }
            RebuildCombinedItems();
        }

        internal void Synchronize(TimeSpan position)
        {
            for (int index = 0;
                 index < _items.Length;
                 index++)
            {
                _items[index].Synchronize(
                    position,
                    _modes[index] !=
                        TimedMetadataTrackPresentationMode
                            .Disabled);
            }
        }

        internal void ApplyProviderCues(
            MediaPlaybackTimedMetadataCueSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            for (int index = 0;
                 index < _providerItems.Length;
                 index++)
            {
                TimedMetadataTrack track =
                    _providerItems[index];
                if (StringComparer.Ordinal.Equals(
                        track.Id,
                        snapshot.ProviderTrackId))
                {
                    track.ApplyProviderCues(snapshot);
                    return;
                }
            }
        }

        internal void ResetTimelines()
        {
            for (int index = 0;
                 index < _items.Length;
                 index++)
            {
                _items[index].ResetTimeline();
            }
        }

        private TimedMetadataTrack[] CreateProviderTracks(
            IReadOnlyList<MediaPlaybackTrackDescriptor>
                descriptors)
        {
            var result =
                new TimedMetadataTrack[descriptors.Count];
            for (int index = 0;
                 index < result.Length;
                 index++)
            {
                MediaPlaybackTrackDescriptor descriptor =
                    descriptors[index];
                result[index] =
                    new TimedMetadataTrack(
                        _owner,
                        in descriptor);
            }
            return result;
        }

        private bool HaveSameProviderIdentity(
            IReadOnlyList<MediaPlaybackTrackDescriptor>
                descriptors)
        {
            if (_providerItems.Length != descriptors.Count)
            {
                return false;
            }
            for (int index = 0;
                 index < _providerItems.Length;
                 index++)
            {
                if (!StringComparer.Ordinal.Equals(
                        _providerItems[index].Id,
                        descriptors[index].ProviderTrackId))
                {
                    return false;
                }
            }
            return true;
        }

        private void RebuildCombinedItems()
        {
            TimedMetadataTrack[] previousItems = _items;
            TimedMetadataTrackPresentationMode[] previousModes =
                _modes;
            _items = new TimedMetadataTrack[
                _providerItems.Length + _externalItems.Length];
            Array.Copy(
                _providerItems,
                0,
                _items,
                0,
                _providerItems.Length);
            Array.Copy(
                _externalItems,
                0,
                _items,
                _providerItems.Length,
                _externalItems.Length);
            _modes =
                new TimedMetadataTrackPresentationMode[
                    _items.Length];
            for (int index = 0;
                 index < _items.Length;
                 index++)
            {
                int previousIndex =
                    Array.IndexOf(previousItems, _items[index]);
                if (previousIndex >= 0)
                {
                    _modes[index] =
                        previousModes[previousIndex];
                }
            }
        }
    }
}
