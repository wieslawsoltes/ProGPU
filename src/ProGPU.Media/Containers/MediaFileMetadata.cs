namespace ProGPU.Media.Containers;

/// <summary>
/// Provider-neutral metadata for one encoded video stream.
/// </summary>
public readonly record struct MediaVideoStreamMetadata(
    string Subtype,
    uint Width,
    uint Height,
    uint Bitrate,
    uint FrameRateNumerator,
    uint FrameRateDenominator,
    TimeSpan Duration);

/// <summary>
/// Provider-neutral metadata for one encoded audio stream.
/// </summary>
public readonly record struct MediaAudioStreamMetadata(
    string Subtype,
    uint Bitrate,
    uint SampleRate,
    uint ChannelCount,
    TimeSpan Duration);

/// <summary>
/// Immutable metadata discovered without initializing a decoder or GPU.
/// </summary>
public sealed record MediaFileMetadata(
    IReadOnlyList<MediaVideoStreamMetadata> VideoStreams,
    IReadOnlyList<MediaAudioStreamMetadata> AudioStreams,
    TimeSpan Duration);

/// <summary>
/// Clean-room, dependency-free container metadata reader. ISO-BMFF parsing is
/// O(B + S + C) for B boxes, S samples, and C chunks, with O(S + C) temporary
/// index storage inherited from the validated demuxer. No media payload is
/// decoded, uploaded, or copied.
/// </summary>
public static class MediaFileMetadataReader
{
    public static MediaFileMetadata ReadIsoBmff(
        Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        IsoBmffMovie movie =
            new IsoBmffDemuxer(stream).Parse();
        var video =
            new List<MediaVideoStreamMetadata>();
        var audio =
            new List<MediaAudioStreamMetadata>();
        TimeSpan duration = TimeSpan.Zero;
        for (int index = 0;
             index < movie.Tracks.Length;
             index++)
        {
            IsoBmffTrack track = movie.Tracks[index];
            TimeSpan trackDuration =
                GetPresentationDuration(track);
            if (trackDuration > duration)
            {
                duration = trackDuration;
            }
            uint bitrate = CalculateBitrate(track);
            if (track.Kind == IsoBmffTrackKind.Video)
            {
                (uint numerator, uint denominator) =
                    CalculateFrameRate(track);
                video.Add(
                    new MediaVideoStreamMetadata(
                        VideoSubtype(track.Codec),
                        track.Width,
                        track.Height,
                        bitrate,
                        numerator,
                        denominator,
                        trackDuration));
            }
            else if (track.Kind ==
                     IsoBmffTrackKind.Audio)
            {
                audio.Add(
                    new MediaAudioStreamMetadata(
                        AudioSubtype(track.Codec),
                        bitrate,
                        track.AudioSampleRate,
                        track.AudioChannelCount,
                        trackDuration));
            }
        }

        return new MediaFileMetadata(
            video.AsReadOnly(),
            audio.AsReadOnly(),
            duration);
    }

    public static async Task<MediaFileMetadata>
        ReadIsoBmffAsync(
            string path,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return await Task.Run(
                () =>
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    using FileStream stream =
                        File.OpenRead(fullPath);
                    MediaFileMetadata metadata =
                        ReadIsoBmff(stream);
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    return metadata;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static TimeSpan ToTimeSpan(
        long duration,
        uint timescale)
    {
        if (duration <= 0 || timescale == 0)
        {
            return TimeSpan.Zero;
        }
        Int128 ticks =
            (Int128)duration *
            TimeSpan.TicksPerSecond /
            timescale;
        return TimeSpan.FromTicks(
            ticks > TimeSpan.MaxValue.Ticks
                ? TimeSpan.MaxValue.Ticks
                : (long)ticks);
    }

    private static TimeSpan GetPresentationDuration(
        IsoBmffTrack track)
    {
        if (track.MovieTimescale != 0 &&
            track.EditList.Length != 0)
        {
            UInt128 duration = 0;
            for (int index = 0;
                 index < track.EditList.Length;
                 index++)
            {
                duration +=
                    track.EditList[index]
                        .SegmentDuration;
            }
            if (duration != 0)
            {
                UInt128 ticks =
                    duration *
                    TimeSpan.TicksPerSecond /
                    track.MovieTimescale;
                return TimeSpan.FromTicks(
                    ticks >
                    (UInt128)TimeSpan
                        .MaxValue
                        .Ticks
                        ? TimeSpan.MaxValue.Ticks
                        : (long)ticks);
            }
        }
        return ToTimeSpan(
            track.Duration,
            track.Timescale);
    }

    private static uint CalculateBitrate(
        IsoBmffTrack track)
    {
        if (track.Duration <= 0 ||
            track.Timescale == 0)
        {
            return 0;
        }
        ulong bytes = 0;
        for (int index = 0;
             index < track.Samples.Length;
             index++)
        {
            bytes = checked(
                bytes +
                (uint)track.Samples[index].Size);
        }
        Int128 bitsPerSecond =
            (Int128)bytes *
            8 *
            track.Timescale /
            track.Duration;
        return bitsPerSecond > uint.MaxValue
            ? uint.MaxValue
            : (uint)bitsPerSecond;
    }

    private static (uint Numerator, uint Denominator)
        CalculateFrameRate(IsoBmffTrack track)
    {
        if (track.Samples.Length == 0 ||
            track.Duration <= 0 ||
            track.Timescale == 0)
        {
            return (0, 1);
        }

        ulong numerator = checked(
            (ulong)track.Samples.Length *
            track.Timescale);
        ulong denominator =
            (ulong)track.Duration;
        ulong divisor = GreatestCommonDivisor(
            numerator,
            denominator);
        numerator /= divisor;
        denominator /= divisor;
        if (numerator > uint.MaxValue ||
            denominator > uint.MaxValue)
        {
            ulong scale = Math.Max(
                DivideRoundUp(
                    numerator,
                    uint.MaxValue),
                DivideRoundUp(
                    denominator,
                    uint.MaxValue));
            numerator = Math.Max(1, numerator / scale);
            denominator =
                Math.Max(1, denominator / scale);
        }
        return (
            (uint)numerator,
            (uint)denominator);
    }

    private static ulong GreatestCommonDivisor(
        ulong left,
        ulong right)
    {
        while (right != 0)
        {
            ulong remainder = left % right;
            left = right;
            right = remainder;
        }
        return Math.Max(1, left);
    }

    private static ulong DivideRoundUp(
        ulong value,
        ulong divisor) =>
        value / divisor +
        (value % divisor == 0 ? 0ul : 1ul);

    private static string VideoSubtype(
        IsoBmffCodec codec) =>
        codec switch
        {
            IsoBmffCodec.H264 => "H264",
            IsoBmffCodec.H265 => "HEVC",
            _ => string.Empty
        };

    private static string AudioSubtype(
        IsoBmffCodec codec) =>
        codec switch
        {
            IsoBmffCodec.Aac => "AAC",
            IsoBmffCodec.Pcm => "PCM",
            _ => string.Empty
        };
}
