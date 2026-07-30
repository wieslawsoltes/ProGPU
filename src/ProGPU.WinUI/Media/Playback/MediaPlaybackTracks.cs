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
}
