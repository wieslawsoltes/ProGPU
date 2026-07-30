using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Storage;
using ProGPU.Media.Containers;

namespace Windows.Media.Editing;

/// <summary>
/// WinUI-aligned non-destructive background audio item. ProGPU adds the URI
/// seam used by portable provider snapshots.
/// </summary>
public sealed class BackgroundAudioTrack
{
    private readonly Dictionary<string, string> _userData =
        new(StringComparer.Ordinal);
    private readonly List<IAudioEffectDefinition>
        _audioEffectDefinitions = [];
    private AudioEncodingProperties
        _audioEncodingProperties;
    private TimeSpan _originalDuration;
    private TimeSpan _trimTimeFromStart;
    private TimeSpan _trimTimeFromEnd;
    private TimeSpan _delay;
    private double _volume = 1d;
    private readonly uint _sourceAudioTrackIndex;

    private BackgroundAudioTrack(
        Uri sourceUri,
        TimeSpan originalDuration,
        AudioEncodingProperties?
            encodingProperties = null,
        uint sourceAudioTrackIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        ValidateDuration(
            originalDuration,
            nameof(originalDuration));
        ProGpuSourceUri = sourceUri;
        _originalDuration = originalDuration;
        _sourceAudioTrackIndex =
            sourceAudioTrackIndex;
        _audioEncodingProperties =
            MediaEditingMetadata.Clone(
                encodingProperties ??
                    new AudioEncodingProperties());
    }

    public IList<IAudioEffectDefinition>
        AudioEffectDefinitions =>
        _audioEffectDefinitions;

    public TimeSpan Delay
    {
        get => _delay;
        set => _delay = value;
    }

    public TimeSpan OriginalDuration =>
        _originalDuration;

    public TimeSpan TrimmedDuration =>
        _originalDuration -
        _trimTimeFromStart -
        _trimTimeFromEnd;

    public TimeSpan TrimTimeFromStart
    {
        get => _trimTimeFromStart;
        set
        {
            ValidateTrim(value, nameof(value));
            if (value + _trimTimeFromEnd >
                _originalDuration)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            _trimTimeFromStart = value;
        }
    }

    public TimeSpan TrimTimeFromEnd
    {
        get => _trimTimeFromEnd;
        set
        {
            ValidateTrim(value, nameof(value));
            if (_trimTimeFromStart + value >
                _originalDuration)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            _trimTimeFromEnd = value;
        }
    }

    public IDictionary<string, string> UserData =>
        _userData;

    public double Volume
    {
        get => _volume;
        set
        {
            if (!double.IsFinite(value) || value < 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value));
            }
            _volume = value;
        }
    }

    public Uri ProGpuSourceUri { get; }

    internal uint ProGpuSourceAudioTrackIndex =>
        _sourceAudioTrackIndex;

    public static async Task<BackgroundAudioTrack>
        CreateFromFileAsync(StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Uri source = new(Path.GetFullPath(file.Path));
        try
        {
            MediaFileMetadata metadata =
                await MediaFileMetadataReader
                    .ReadIsoBmffAsync(file.Path)
                    .ConfigureAwait(false);
            if (metadata.AudioStreams.Count != 0)
            {
                MediaAudioStreamMetadata audio =
                    metadata.AudioStreams[0];
                return new BackgroundAudioTrack(
                    source,
                    audio.Duration,
                    new AudioEncodingProperties
                    {
                        Subtype = audio.Subtype,
                        Bitrate = audio.Bitrate,
                        SampleRate = audio.SampleRate,
                        ChannelCount =
                            audio.ChannelCount
                    });
            }
        }
        catch (InvalidDataException)
        {
            // Native providers can populate metadata for non-ISO audio.
        }
        catch (EndOfStreamException)
        {
            // Preserve a provider-discoverable fallback for other formats.
        }

        return new BackgroundAudioTrack(
            source,
            TimeSpan.Zero);
    }

    public static BackgroundAudioTrack CreateFromUri(
        Uri source,
        TimeSpan originalDuration)
        => CreateFromUriCore(
            source,
            originalDuration,
            sourceAudioTrackIndex: 0);

    internal static BackgroundAudioTrack CreateFromUriCore(
        Uri source,
        TimeSpan originalDuration,
        uint sourceAudioTrackIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "A background audio URI must be absolute.",
                nameof(source));
        }
        return new BackgroundAudioTrack(
            source,
            originalDuration,
            sourceAudioTrackIndex:
                sourceAudioTrackIndex);
    }

    public static BackgroundAudioTrack
        CreateFromEmbeddedAudioTrack(
            EmbeddedAudioTrack embeddedAudioTrack)
    {
        ArgumentNullException.ThrowIfNull(
            embeddedAudioTrack);
        return new BackgroundAudioTrack(
            embeddedAudioTrack.ProGpuSourceUri,
            embeddedAudioTrack.OriginalDuration,
            embeddedAudioTrack
                .GetAudioEncodingProperties(),
            embeddedAudioTrack.SourceTrackIndex);
    }

    public BackgroundAudioTrack Clone()
    {
        var clone = new BackgroundAudioTrack(
            ProGpuSourceUri,
            _originalDuration,
            _audioEncodingProperties,
            _sourceAudioTrackIndex)
        {
            _trimTimeFromStart =
                _trimTimeFromStart,
            _trimTimeFromEnd =
                _trimTimeFromEnd,
            _delay = _delay,
            _volume = _volume
        };
        foreach ((string key, string value) in _userData)
        {
            clone._userData.Add(key, value);
        }
        for (int index = 0;
             index < _audioEffectDefinitions.Count;
             index++)
        {
            clone._audioEffectDefinitions.Add(
                MediaEditingEffectClone.Clone(
                    _audioEffectDefinitions[index]));
        }
        return clone;
    }

    public AudioEncodingProperties
        GetAudioEncodingProperties() =>
        MediaEditingMetadata.Clone(
            _audioEncodingProperties);

    public void SetProGpuEncodingProperties(
        AudioEncodingProperties encodingProperties)
    {
        ArgumentNullException.ThrowIfNull(
            encodingProperties);
        _audioEncodingProperties =
            MediaEditingMetadata.Clone(
                encodingProperties);
    }

    public void SetProGpuOriginalDuration(
        TimeSpan originalDuration)
    {
        ValidateDuration(
            originalDuration,
            nameof(originalDuration));
        _originalDuration = originalDuration;
        if (_trimTimeFromStart > originalDuration)
        {
            _trimTimeFromStart = originalDuration;
        }
        TimeSpan available =
            originalDuration -
            _trimTimeFromStart;
        if (_trimTimeFromEnd > available)
        {
            _trimTimeFromEnd = available;
        }
    }

    private void ValidateTrim(
        TimeSpan value,
        string parameterName)
    {
        ValidateDuration(value, parameterName);
        if (_originalDuration == TimeSpan.Zero &&
            value != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName);
        }
    }

    private static void ValidateDuration(
        TimeSpan value,
        string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName);
        }
    }
}
