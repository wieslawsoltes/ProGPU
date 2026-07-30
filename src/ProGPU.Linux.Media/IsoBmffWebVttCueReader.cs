using System.Buffers.Binary;
using System.Text;
using ProGPU.Media.Containers;
using ProGPU.Media.Playback;

namespace ProGPU.Linux.Media;

/// <summary>
/// Reads ISO/IEC 14496-30 WebVTT cue boxes from an already indexed ISO-BMFF
/// track. Work is O(S + B + U) for S samples, B nested boxes, and U UTF-8
/// payload bytes. Retained storage is O(C + U) for C cues; one reusable
/// sample-sized buffer is bounded independently of the source size.
/// </summary>
internal static class IsoBmffWebVttCueReader
{
    private const int MaximumSampleBytes = 16 * 1024 * 1024;
    private const int MaximumCues = 1_000_000;
    private const long MaximumRetainedUtf8Bytes =
        256L * 1024 * 1024;
    private static readonly UTF8Encoding s_utf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    internal static MediaPlaybackTimedMetadataCueSnapshot
        ReadAll(
            Stream stream,
            IsoBmffTrack track,
            string providerTrackId)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            providerTrackId);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "WebVTT sample reading requires a readable, seekable stream.",
                nameof(stream));
        }
        if (track.Kind !=
                IsoBmffTrackKind.TimedMetadata ||
            track.Codec != IsoBmffCodec.WebVtt)
        {
            throw new ArgumentException(
                "The selected ISO-BMFF track is not WebVTT timed metadata.",
                nameof(track));
        }

        var cues =
            new List<
                MediaPlaybackTimedMetadataCueDescriptor>();
        var cueIds =
            new HashSet<string>(StringComparer.Ordinal);
        int maximumSampleSize = 0;
        for (int sampleIndex = 0;
             sampleIndex < track.Samples.Length;
             sampleIndex++)
        {
            IsoBmffSample sample =
                track.Samples[sampleIndex];
            ValidateSample(stream, in sample);
            maximumSampleSize = Math.Max(
                maximumSampleSize,
                sample.Size);
        }
        byte[] sampleBuffer =
            maximumSampleSize == 0
                ? []
                : new byte[maximumSampleSize];
        long retainedUtf8Bytes = 0;
        for (int sampleIndex = 0;
             sampleIndex < track.Samples.Length;
             sampleIndex++)
        {
            IsoBmffSample sample =
                track.Samples[sampleIndex];
            if (sample.Size == 0)
            {
                continue;
            }
            stream.Position = sample.Offset;
            stream.ReadExactly(
                sampleBuffer.AsSpan(0, sample.Size));
            ParseSample(
                sampleBuffer.AsSpan(0, sample.Size),
                track.Timescale,
                in sample,
                sampleIndex,
                providerTrackId,
                cueIds,
                cues,
                ref retainedUtf8Bytes);
        }

        return new MediaPlaybackTimedMetadataCueSnapshot(
            providerTrackId,
            cues);
    }

    private static void ParseSample(
        ReadOnlySpan<byte> source,
        uint timescale,
        in IsoBmffSample sample,
        int sampleIndex,
        string providerTrackId,
        HashSet<string> cueIds,
        List<MediaPlaybackTimedMetadataCueDescriptor> cues,
        ref long retainedUtf8Bytes)
    {
        int position = 0;
        int cueOrdinal = 0;
        while (TryReadBox(
            source,
            ref position,
            out SampleBox box))
        {
            if (box.Type != BoxType.Vttc)
            {
                continue;
            }
            if (cues.Count >= MaximumCues)
            {
                throw new InvalidDataException(
                    $"The WebVTT cue count exceeds the bounded limit {MaximumCues}.");
            }

            string? cueId = null;
            string? cueText = null;
            int childPosition = box.PayloadStart;
            ReadOnlySpan<byte> children =
                source[..box.End];
            while (TryReadBox(
                children,
                ref childPosition,
                out SampleBox child))
            {
                if (child.End > box.End)
                {
                    throw new InvalidDataException(
                        "A WebVTT cue child box exceeds its vttc parent.");
                }
                ReadOnlySpan<byte> payload =
                    source[
                        child.PayloadStart..child.End];
                if (child.Type == BoxType.Iden)
                {
                    if (cueId is not null)
                    {
                        throw new InvalidDataException(
                            "A WebVTT cue contains more than one iden box.");
                    }
                    ReserveRetainedUtf8(
                        payload.Length,
                        ref retainedUtf8Bytes);
                    cueId = DecodeUtf8(
                        payload,
                        "WebVTT cue identifier");
                }
                else if (child.Type == BoxType.Payl)
                {
                    if (cueText is not null)
                    {
                        throw new InvalidDataException(
                            "A WebVTT cue contains more than one payl box.");
                    }
                    ReserveRetainedUtf8(
                        payload.Length,
                        ref retainedUtf8Bytes);
                    cueText = DecodeUtf8(
                        payload,
                        "WebVTT cue payload");
                }
            }
            if (childPosition != box.End)
            {
                throw new InvalidDataException(
                    "A WebVTT cue box is malformed.");
            }
            if (cueText is null)
            {
                throw new InvalidDataException(
                    "A WebVTT vttc box has no cue payload.");
            }

            string stableId = GetUniqueCueId(
                cueId,
                providerTrackId,
                sampleIndex,
                cueOrdinal,
                cueIds);
            cues.Add(
                new MediaPlaybackTimedMetadataCueDescriptor(
                    stableId,
                    FromMediaTime(
                        sample.PresentationTime,
                        timescale),
                    FromMediaTime(
                        sample.Duration,
                        timescale),
                    cueText));
            cueOrdinal++;
        }
    }

    private static bool TryReadBox(
        ReadOnlySpan<byte> source,
        ref int position,
        out SampleBox box)
    {
        if (position == source.Length)
        {
            box = default;
            return false;
        }
        if ((uint)position >
                (uint)source.Length ||
            source.Length - position < 8)
        {
            throw new InvalidDataException(
                "A WebVTT sample contains a truncated box header.");
        }

        uint shortSize =
            BinaryPrimitives.ReadUInt32BigEndian(
                source[position..]);
        uint type =
            BinaryPrimitives.ReadUInt32BigEndian(
                source[(position + 4)..]);
        int headerSize = 8;
        ulong size = shortSize;
        if (shortSize == 1)
        {
            if (source.Length - position < 16)
            {
                throw new InvalidDataException(
                    "A WebVTT sample contains a truncated extended-size box.");
            }
            size = BinaryPrimitives
                .ReadUInt64BigEndian(
                    source[(position + 8)..]);
            headerSize = 16;
        }
        else if (shortSize == 0)
        {
            size = checked(
                (ulong)(source.Length - position));
        }

        ulong remaining = checked(
            (ulong)(source.Length - position));
        if (size < (ulong)headerSize ||
            size > remaining ||
            size > int.MaxValue)
        {
            throw new InvalidDataException(
                "A WebVTT sample box has an invalid size.");
        }
        int end = checked(position + (int)size);
        box = new SampleBox(
            type,
            position + headerSize,
            end);
        position = end;
        return true;
    }

    private static void ValidateSample(
        Stream stream,
        in IsoBmffSample sample)
    {
        if (sample.Offset < 0 ||
            sample.Size is < 0 or > MaximumSampleBytes)
        {
            throw new InvalidDataException(
                $"A WebVTT sample exceeds the bounded {MaximumSampleBytes}-byte payload limit.");
        }
        if (sample.Offset >
            stream.Length - sample.Size)
        {
            throw new EndOfStreamException(
                "A WebVTT sample extends beyond the source stream.");
        }
    }

    private static string DecodeUtf8(
        ReadOnlySpan<byte> payload,
        string field)
    {
        try
        {
            return s_utf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"{field} is not valid UTF-8.",
                exception);
        }
    }

    private static void ReserveRetainedUtf8(
        int byteCount,
        ref long retainedUtf8Bytes)
    {
        retainedUtf8Bytes = checked(
            retainedUtf8Bytes + byteCount);
        if (retainedUtf8Bytes >
            MaximumRetainedUtf8Bytes)
        {
            throw new InvalidDataException(
                $"WebVTT retained UTF-8 data exceeds the bounded {MaximumRetainedUtf8Bytes}-byte limit.");
        }
    }

    private static string GetUniqueCueId(
        string? sourceId,
        string providerTrackId,
        int sampleIndex,
        int cueOrdinal,
        HashSet<string> cueIds)
    {
        if (!string.IsNullOrWhiteSpace(sourceId) &&
            cueIds.Add(sourceId))
        {
            return sourceId;
        }

        string fallback = string.Concat(
            providerTrackId,
            ":",
            sampleIndex.ToString(
                System.Globalization.CultureInfo
                    .InvariantCulture),
            ":",
            cueOrdinal.ToString(
                System.Globalization.CultureInfo
                    .InvariantCulture));
        int collision = 0;
        while (!cueIds.Add(fallback))
        {
            collision++;
            fallback = string.Concat(
                providerTrackId,
                ":",
                sampleIndex.ToString(
                    System.Globalization.CultureInfo
                        .InvariantCulture),
                ":",
                cueOrdinal.ToString(
                    System.Globalization.CultureInfo
                        .InvariantCulture),
                ":",
                collision.ToString(
                    System.Globalization.CultureInfo
                        .InvariantCulture));
        }
        return fallback;
    }

    private static TimeSpan FromMediaTime(
        long value,
        uint timescale)
    {
        if (value <= 0 || timescale == 0)
        {
            return TimeSpan.Zero;
        }
        Int128 ticks =
            (Int128)value *
            TimeSpan.TicksPerSecond /
            timescale;
        return TimeSpan.FromTicks(
            ticks > TimeSpan.MaxValue.Ticks
                ? TimeSpan.MaxValue.Ticks
                : (long)ticks);
    }

    private readonly record struct SampleBox(
        uint Type,
        int PayloadStart,
        int End);

    private static class BoxType
    {
        internal const uint Vttc = 0x7674_7463;
        internal const uint Iden = 0x6964_656E;
        internal const uint Payl = 0x7061_796C;
    }
}
