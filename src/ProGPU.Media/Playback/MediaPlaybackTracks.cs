using System.Collections.ObjectModel;

namespace ProGPU.Media.Playback;

/// <summary>
/// Provider-neutral media-track kind. Values intentionally match WinUI's
/// MediaTrackKind projection without introducing a framework dependency.
/// </summary>
public enum MediaPlaybackTrackKind
{
    Audio = 0,
    Video = 1,
    TimedMetadata = 2
}

/// <summary>
/// Describes whether the active native provider can decode and present a
/// track. Unknown is retained for providers whose platform API does not expose
/// a reliable support query.
/// </summary>
public enum MediaPlaybackTrackSupport
{
    Unknown = 0,
    Supported = 1,
    Degraded = 2,
    Unsupported = 3
}

/// <summary>
/// Immutable encoding metadata reported by a native playback provider.
/// Unknown numeric values are zero. Construction and reads are O(1).
/// </summary>
public readonly record struct MediaPlaybackTrackEncoding(
    string Subtype,
    uint Bitrate = 0,
    uint Width = 0,
    uint Height = 0,
    uint FrameRateNumerator = 0,
    uint FrameRateDenominator = 0,
    uint SampleRate = 0,
    uint ChannelCount = 0)
{
    public static MediaPlaybackTrackEncoding Empty { get; } =
        new(string.Empty);
}

/// <summary>
/// Immutable provider-neutral description of one native media track.
/// ProviderTrackId must be stable for the lifetime of an opened source.
/// </summary>
public readonly record struct MediaPlaybackTrackDescriptor(
    string ProviderTrackId,
    MediaPlaybackTrackKind Kind,
    string Name,
    string Label,
    string Language,
    MediaPlaybackTrackEncoding Encoding,
    MediaPlaybackTrackSupport Support =
        MediaPlaybackTrackSupport.Unknown);

/// <summary>
/// Immutable snapshot of the tracks associated with the currently opened
/// source. Provider publication performs O(T) bounded copying for T tracks;
/// reads and selected-index queries are O(1).
/// </summary>
public sealed class MediaPlaybackTracksSnapshot
{
    private static readonly ReadOnlyCollection<
        MediaPlaybackTrackDescriptor> s_emptyTracks =
            Array.AsReadOnly(
                Array.Empty<MediaPlaybackTrackDescriptor>());

    private readonly ReadOnlyCollection<
        MediaPlaybackTrackDescriptor> _audioTracks;
    private readonly ReadOnlyCollection<
        MediaPlaybackTrackDescriptor> _videoTracks;
    private readonly ReadOnlyCollection<
        MediaPlaybackTrackDescriptor> _timedMetadataTracks;

    public MediaPlaybackTracksSnapshot(
        IReadOnlyList<MediaPlaybackTrackDescriptor>? audioTracks,
        int selectedAudioTrackIndex,
        IReadOnlyList<MediaPlaybackTrackDescriptor>? videoTracks,
        int selectedVideoTrackIndex,
        IReadOnlyList<MediaPlaybackTrackDescriptor>?
            timedMetadataTracks = null)
    {
        _audioTracks = CopyAndValidate(
            audioTracks,
            MediaPlaybackTrackKind.Audio,
            nameof(audioTracks));
        _videoTracks = CopyAndValidate(
            videoTracks,
            MediaPlaybackTrackKind.Video,
            nameof(videoTracks));
        _timedMetadataTracks = CopyAndValidate(
            timedMetadataTracks,
            MediaPlaybackTrackKind.TimedMetadata,
            nameof(timedMetadataTracks));
        ValidateSelectedIndex(
            selectedAudioTrackIndex,
            _audioTracks.Count,
            nameof(selectedAudioTrackIndex));
        ValidateSelectedIndex(
            selectedVideoTrackIndex,
            _videoTracks.Count,
            nameof(selectedVideoTrackIndex));
        SelectedAudioTrackIndex = selectedAudioTrackIndex;
        SelectedVideoTrackIndex = selectedVideoTrackIndex;
    }

    private MediaPlaybackTracksSnapshot(
        ReadOnlyCollection<MediaPlaybackTrackDescriptor>
            audioTracks,
        int selectedAudioTrackIndex,
        ReadOnlyCollection<MediaPlaybackTrackDescriptor>
            videoTracks,
        int selectedVideoTrackIndex,
        ReadOnlyCollection<MediaPlaybackTrackDescriptor>
            timedMetadataTracks)
    {
        _audioTracks = audioTracks;
        _videoTracks = videoTracks;
        _timedMetadataTracks = timedMetadataTracks;
        SelectedAudioTrackIndex = selectedAudioTrackIndex;
        SelectedVideoTrackIndex = selectedVideoTrackIndex;
    }

    public static MediaPlaybackTracksSnapshot Empty { get; } =
        new(
            s_emptyTracks,
            -1,
            s_emptyTracks,
            -1,
            s_emptyTracks);

    public IReadOnlyList<MediaPlaybackTrackDescriptor>
        AudioTracks => _audioTracks;
    public IReadOnlyList<MediaPlaybackTrackDescriptor>
        VideoTracks => _videoTracks;
    public IReadOnlyList<MediaPlaybackTrackDescriptor>
        TimedMetadataTracks => _timedMetadataTracks;
    public int SelectedAudioTrackIndex { get; }
    public int SelectedVideoTrackIndex { get; }

    public IReadOnlyList<MediaPlaybackTrackDescriptor> GetTracks(
        MediaPlaybackTrackKind kind) =>
        kind switch
        {
            MediaPlaybackTrackKind.Audio => _audioTracks,
            MediaPlaybackTrackKind.Video => _videoTracks,
            MediaPlaybackTrackKind.TimedMetadata =>
                _timedMetadataTracks,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind))
        };

    public int GetSelectedIndex(
        MediaPlaybackTrackKind kind) =>
        kind switch
        {
            MediaPlaybackTrackKind.Audio =>
                SelectedAudioTrackIndex,
            MediaPlaybackTrackKind.Video =>
                SelectedVideoTrackIndex,
            MediaPlaybackTrackKind.TimedMetadata => -1,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind))
        };

    public MediaPlaybackTracksSnapshot WithSelectedIndex(
        MediaPlaybackTrackKind kind,
        int index)
    {
        IReadOnlyList<MediaPlaybackTrackDescriptor> tracks =
            GetTracks(kind);
        ValidateSelectedIndex(index, tracks.Count, nameof(index));
        return kind switch
        {
            MediaPlaybackTrackKind.Audio => new(
                _audioTracks,
                index,
                _videoTracks,
                SelectedVideoTrackIndex,
                _timedMetadataTracks),
            MediaPlaybackTrackKind.Video => new(
                _audioTracks,
                SelectedAudioTrackIndex,
                _videoTracks,
                index,
                _timedMetadataTracks),
            _ => throw new NotSupportedException(
                "Timed metadata tracks use presentation modes rather than a single selected index.")
        };
    }

    internal bool ContentEquals(
        MediaPlaybackTracksSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return SelectedAudioTrackIndex ==
                   other.SelectedAudioTrackIndex &&
               SelectedVideoTrackIndex ==
                   other.SelectedVideoTrackIndex &&
               SequenceEqual(_audioTracks, other._audioTracks) &&
               SequenceEqual(_videoTracks, other._videoTracks) &&
               SequenceEqual(
                   _timedMetadataTracks,
                   other._timedMetadataTracks);
    }

    private static ReadOnlyCollection<
        MediaPlaybackTrackDescriptor> CopyAndValidate(
        IReadOnlyList<MediaPlaybackTrackDescriptor>? source,
        MediaPlaybackTrackKind expectedKind,
        string parameterName)
    {
        if (source is null || source.Count == 0)
        {
            return s_emptyTracks;
        }

        var result =
            new MediaPlaybackTrackDescriptor[source.Count];
        for (int index = 0; index < result.Length; index++)
        {
            MediaPlaybackTrackDescriptor descriptor =
                source[index];
            if (descriptor.Kind != expectedKind)
            {
                throw new ArgumentException(
                    $"Track {index} has kind {descriptor.Kind}; expected {expectedKind}.",
                    parameterName);
            }
            if (string.IsNullOrWhiteSpace(
                    descriptor.ProviderTrackId))
            {
                throw new ArgumentException(
                    $"Track {index} does not have a provider identifier.",
                    parameterName);
            }
            result[index] = descriptor with
            {
                Name = descriptor.Name ?? string.Empty,
                Label = descriptor.Label ?? string.Empty,
                Language = descriptor.Language ?? string.Empty,
                Encoding = descriptor.Encoding with
                {
                    Subtype =
                        descriptor.Encoding.Subtype ??
                        string.Empty
                }
            };
        }
        return Array.AsReadOnly(result);
    }

    private static void ValidateSelectedIndex(
        int index,
        int count,
        string parameterName)
    {
        if (index < -1 || index >= count)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                index,
                "A selected track index must be -1 or identify an existing track.");
        }
    }

    private static bool SequenceEqual(
        IReadOnlyList<MediaPlaybackTrackDescriptor> left,
        IReadOnlyList<MediaPlaybackTrackDescriptor> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }
        return true;
    }
}

public sealed class MediaPlaybackTracksChangedEventArgs :
    EventArgs
{
    public MediaPlaybackTracksChangedEventArgs(
        MediaPlaybackTracksSnapshot tracks)
    {
        Tracks = tracks ??
            throw new ArgumentNullException(nameof(tracks));
    }

    public MediaPlaybackTracksSnapshot Tracks { get; }
}

/// <summary>
/// Optional provider capability for selecting one audio or video track.
/// Implementations return false without mutating native state when the
/// requested selection is unsupported.
/// </summary>
public interface IMediaPlaybackTrackProvider
{
    bool TrySelectTrack(
        MediaPlaybackTrackKind kind,
        int index);
}
