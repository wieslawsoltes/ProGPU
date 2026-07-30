using Windows.Media.MediaProperties;

namespace Windows.Media.Editing;

/// <summary>
/// Selects the timestamp precision used for composition thumbnails. Values
/// match the official Windows.Media.Editing contract.
/// </summary>
public enum VideoFramePrecision
{
    NearestFrame = 0,
    NearestKeyFrame = 1
}

/// <summary>
/// Describes one audio stream embedded in a <see cref="MediaClip"/>.
/// Instances are owned by their clip and expose a detached encoding-property
/// snapshot at the public ownership boundary.
/// </summary>
public sealed class EmbeddedAudioTrack
{
    private readonly AudioEncodingProperties
        _encodingProperties;

    internal EmbeddedAudioTrack(
        Uri sourceUri,
        TimeSpan originalDuration,
        AudioEncodingProperties encodingProperties)
    {
        ProGpuSourceUri =
            sourceUri ??
            throw new ArgumentNullException(nameof(sourceUri));
        if (originalDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originalDuration));
        }
        OriginalDuration = originalDuration;
        _encodingProperties =
            MediaEditingMetadata.Clone(encodingProperties);
    }

    internal Uri ProGpuSourceUri { get; }

    internal TimeSpan OriginalDuration { get; }

    public AudioEncodingProperties
        GetAudioEncodingProperties() =>
        MediaEditingMetadata.Clone(_encodingProperties);

    internal EmbeddedAudioTrack Clone() =>
        new(
            ProGpuSourceUri,
            OriginalDuration,
            _encodingProperties);
}

internal static class MediaEditingMetadata
{
    public static VideoEncodingProperties Clone(
        VideoEncodingProperties source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = new VideoEncodingProperties
        {
            Subtype = source.Subtype,
            Width = source.Width,
            Height = source.Height,
            Bitrate = source.Bitrate
        };
        clone.FrameRate.Numerator =
            source.FrameRate.Numerator;
        clone.FrameRate.Denominator =
            source.FrameRate.Denominator;
        return clone;
    }

    public static AudioEncodingProperties Clone(
        AudioEncodingProperties source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AudioEncodingProperties
        {
            Subtype = source.Subtype,
            Bitrate = source.Bitrate,
            SampleRate = source.SampleRate,
            ChannelCount = source.ChannelCount
        };
    }
}
