using ProGPU.Media.Containers;

namespace ProGPU.Media.Editing;

/// <summary>
/// Builds an exact presentation timeline from compatible compressed AAC
/// access units. ISO-BMFF edit-list entries trim partial boundary access
/// units and represent silent clip spans without decoding or generating PCM.
/// </summary>
/// <remarks>
/// Planning is O(C + S) time and O(S + C) metadata for C clips and S selected
/// AAC samples. Compressed payload bytes remain in their source files and are
/// copied only by <see cref="IsoBmffCompositionWriter"/>.
/// </remarks>
internal static class IsoBmffPreciseAacTimelinePlanner
{
    internal static IsoBmffCompositionTrack Create(
        MediaCompositionExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken
            .ThrowIfCancellationRequested();
        if (!string.Equals(
                request.EncodingProfile.AudioSubtype,
                "AAC",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Precise AAC planning requires an AAC output profile.",
                nameof(request));
        }

        var samples =
            new List<IsoBmffCompositionSample>();
        var edits = new EditBuilder();
        IsoBmffTrack? template = null;
        long mediaCursor = 0;

        for (int clipIndex = 0;
             clipIndex < request.Clips.Count;
             clipIndex++)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            MediaCompositionExportClip clip =
                request.Clips[clipIndex];
            TimeSpan clipDuration =
                GetTrimmedDuration(clip);
            if (clip.SourceUri is null)
            {
                edits.AppendEmpty(clipDuration);
                continue;
            }

            string sourcePath = Path.GetFullPath(
                clip.SourceUri.LocalPath);
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.RandomAccess);
            IsoBmffMovie movie =
                new IsoBmffDemuxer(source)
                    .Parse();
            IsoBmffTrack? audio =
                SelectAudioTrack(
                    movie,
                    clip.SourceAudioTrackIndex);
            if (audio is null)
            {
                edits.AppendEmpty(clipDuration);
                continue;
            }

            ValidateProfile(
                request.EncodingProfile,
                audio,
                cancellationToken);
            ValidateTemplate(
                template,
                audio);
            template ??= audio;

            long sourceStart =
                ToTrackTime(
                    clip.TrimTimeFromStart,
                    audio.Timescale);
            long sourceEnd =
                checked(
                    sourceStart +
                    ToTrackTime(
                        clipDuration,
                        audio.Timescale));
            (int first, int last) =
                SelectOverlappingRange(
                    audio,
                    sourceStart,
                    sourceEnd,
                    cancellationToken);
            if (first < 0)
            {
                edits.AppendEmpty(clipDuration);
                continue;
            }

            IsoBmffSample firstSample =
                audio.Samples[first];
            IsoBmffSample lastSample =
                audio.Samples[last];
            long audibleStart =
                Math.Max(
                    sourceStart,
                    firstSample.PresentationTime);
            long audibleEnd =
                Math.Min(
                    sourceEnd,
                    checked(
                        lastSample.PresentationTime +
                        lastSample.Duration));
            if (audibleEnd <= audibleStart)
            {
                edits.AppendEmpty(clipDuration);
                continue;
            }

            long leadingTicks =
                FromTrackTime(
                    audibleStart - sourceStart,
                    audio.Timescale).Ticks;
            long audibleTicks =
                FromTrackTime(
                    audibleEnd - audibleStart,
                    audio.Timescale).Ticks;
            leadingTicks = Math.Clamp(
                leadingTicks,
                0,
                clipDuration.Ticks);
            audibleTicks = Math.Clamp(
                audibleTicks,
                0,
                clipDuration.Ticks -
                    leadingTicks);
            long trailingTicks = checked(
                clipDuration.Ticks -
                leadingTicks -
                audibleTicks);

            long segmentMediaStart =
                mediaCursor;
            AppendSamples(
                sourcePath,
                audio.Samples,
                first,
                last,
                samples,
                ref mediaCursor,
                cancellationToken);
            long editMediaTime = checked(
                segmentMediaStart +
                audibleStart -
                firstSample.PresentationTime);
            edits.AppendEmpty(
                TimeSpan.FromTicks(
                    leadingTicks));
            edits.AppendMedia(
                TimeSpan.FromTicks(
                    audibleTicks),
                editMediaTime);
            edits.AppendEmpty(
                TimeSpan.FromTicks(
                    trailingTicks));
        }

        if (template is null ||
            samples.Count == 0)
        {
            throw new NotSupportedException(
                "AAC output was requested but the composition contains no compatible selected AAC track.");
        }
        if (template.SampleEntryType == 0 ||
            template.SampleEntryPayload.Length == 0)
        {
            throw new InvalidDataException(
                "The selected AAC sample entry is unavailable.");
        }

        IsoBmffCompositionEdit[] editList =
            edits.Build();
        if (editList.Length == 0)
        {
            throw new InvalidDataException(
                "The selected AAC timeline has no audible presentation segment.");
        }
        return new IsoBmffCompositionTrack(
            IsoBmffTrackKind.Audio,
            template.Timescale,
            Width: 0,
            Height: 0,
            template.SampleEntryType,
            template.SampleEntryPayload,
            samples.ToArray())
        {
            Edits = editList
        };
    }

    private static IsoBmffTrack? SelectAudioTrack(
        IsoBmffMovie movie,
        uint selectedIndex)
    {
        uint index = 0;
        bool hasAudio = false;
        for (int trackIndex = 0;
             trackIndex < movie.Tracks.Length;
             trackIndex++)
        {
            IsoBmffTrack track =
                movie.Tracks[trackIndex];
            if (track.Kind !=
                IsoBmffTrackKind.Audio)
            {
                continue;
            }
            hasAudio = true;
            if (index == selectedIndex)
            {
                return track.Codec ==
                        IsoBmffCodec.Aac
                    ? track
                    : throw new NotSupportedException(
                        "The selected embedded audio track is not AAC.");
            }
            index++;
        }

        if (selectedIndex != 0 &&
            hasAudio)
        {
            throw new InvalidDataException(
                "The selected embedded audio-track index is outside the source track list.");
        }
        return null;
    }

    private static void ValidateProfile(
        MediaCompositionEncodingProfile profile,
        IsoBmffTrack track,
        CancellationToken cancellationToken)
    {
        if (track.Timescale == 0 ||
            track.AudioSampleRate == 0 ||
            track.AudioChannelCount == 0 ||
            track.AudioSampleRate !=
                profile.AudioSampleRate ||
            track.AudioChannelCount !=
                profile.AudioChannelCount)
        {
            throw new NotSupportedException(
                "Compressed AAC composition requires the source sample rate and channel count to match the output profile.");
        }

        uint bitrate = CalculateBitrate(
            track,
            cancellationToken);
        if (profile.AudioBitrate != 0 &&
            bitrate != profile.AudioBitrate)
        {
            throw new NotSupportedException(
                "Compressed AAC composition preserves the source bitrate, which does not match the output profile.");
        }
    }

    private static void ValidateTemplate(
        IsoBmffTrack? expected,
        IsoBmffTrack actual)
    {
        if (expected is null)
        {
            return;
        }
        if (expected.Codec != actual.Codec ||
            expected.Timescale !=
                actual.Timescale ||
            expected.AudioSampleRate !=
                actual.AudioSampleRate ||
            expected.AudioChannelCount !=
                actual.AudioChannelCount ||
            expected.SampleEntryType !=
                actual.SampleEntryType ||
            !expected.SampleEntryPayload
                .AsSpan()
                .SequenceEqual(
                    actual.SampleEntryPayload))
        {
            throw new NotSupportedException(
                "Compressed AAC composition requires identical selected AAC sample entries and timescales.");
        }
    }

    private static (
        int First,
        int Last)
        SelectOverlappingRange(
            IsoBmffTrack track,
            long start,
            long end,
            CancellationToken cancellationToken)
    {
        int first = -1;
        int last = -1;
        long previousPresentation =
            long.MinValue;
        for (int index = 0;
             index < track.Samples.Length;
             index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
            }
            IsoBmffSample sample =
                track.Samples[index];
            if (sample.Duration <= 0 ||
                sample.Size <= 0 ||
                sample.PresentationTime <
                    previousPresentation ||
                sample.PresentationTime !=
                    sample.DecodeTime)
            {
                throw new NotSupportedException(
                    "Compressed AAC composition requires positive, presentation-ordered AAC samples without composition offsets.");
            }
            previousPresentation =
                sample.PresentationTime;
            long sampleEnd = checked(
                sample.PresentationTime +
                sample.Duration);
            if (sampleEnd <= start)
            {
                continue;
            }
            if (sample.PresentationTime >= end)
            {
                break;
            }
            first = first < 0
                ? index
                : first;
            last = index;
        }
        return (first, last);
    }

    private static void AppendSamples(
        string sourcePath,
        IsoBmffSample[] source,
        int first,
        int last,
        List<IsoBmffCompositionSample> destination,
        ref long mediaCursor,
        CancellationToken cancellationToken)
    {
        long firstDecode =
            source[first].DecodeTime;
        long firstPresentation =
            source[first].PresentationTime;
        for (int index = first;
             index <= last;
             index++)
        {
            if (((index - first) & 1023) == 0)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
            }
            IsoBmffSample sample =
                source[index];
            long relativeDecode = checked(
                sample.DecodeTime -
                firstDecode);
            long relativePresentation = checked(
                sample.PresentationTime -
                firstPresentation);
            long compositionOffset = checked(
                relativePresentation -
                relativeDecode);
            if (compositionOffset is
                < int.MinValue or
                > int.MaxValue)
            {
                throw new InvalidDataException(
                    "An AAC composition offset exceeds the ISO-BMFF signed range.");
            }
            destination.Add(
                new IsoBmffCompositionSample(
                    sourcePath,
                    sample.Offset,
                    sample.Size,
                    sample.Duration,
                    checked(
                        (int)compositionOffset),
                    IsSync: true));
            mediaCursor = checked(
                mediaCursor +
                sample.Duration);
        }
    }

    private static TimeSpan GetTrimmedDuration(
        MediaCompositionExportClip clip)
    {
        TimeSpan duration =
            clip.OriginalDuration -
            clip.TrimTimeFromStart -
            clip.TrimTimeFromEnd;
        if (duration <= TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "A precise AAC clip has an empty trim interval.");
        }
        return duration;
    }

    private static uint CalculateBitrate(
        IsoBmffTrack track,
        CancellationToken cancellationToken)
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
            if ((index & 1023) == 0)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
            }
            bytes = checked(
                bytes +
                (uint)track.Samples[index]
                    .Size);
        }
        Int128 bitrate =
            (Int128)bytes *
            8 *
            track.Timescale /
            track.Duration;
        return bitrate > uint.MaxValue
            ? uint.MaxValue
            : (uint)bitrate;
    }

    private static long ToTrackTime(
        TimeSpan value,
        uint timescale) =>
        checked(
            (long)Math.Round(
                value.Ticks *
                (double)timescale /
                TimeSpan.TicksPerSecond,
                MidpointRounding
                    .AwayFromZero));

    private static TimeSpan FromTrackTime(
        long value,
        uint timescale) =>
        TimeSpan.FromTicks(
            checked(
                (long)Math.Round(
                    value *
                    ((double)TimeSpan
                        .TicksPerSecond /
                     timescale),
                    MidpointRounding
                        .AwayFromZero)));

    private sealed class EditBuilder
    {
        private readonly List<IsoBmffCompositionEdit>
            _edits = [];
        private long _timelineTicks;
        private ulong _movieBoundary;

        internal void AppendEmpty(
            TimeSpan duration) =>
            Append(duration, mediaTime: -1);

        internal void AppendMedia(
            TimeSpan duration,
            long mediaTime)
        {
            if (mediaTime < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(mediaTime));
            }
            Append(duration, mediaTime);
        }

        internal IsoBmffCompositionEdit[]
            Build()
        {
            while (_edits.Count != 0 &&
                   _edits[^1].MediaTime == -1)
            {
                _edits.RemoveAt(
                    _edits.Count - 1);
            }
            return _edits.ToArray();
        }

        private void Append(
            TimeSpan duration,
            long mediaTime)
        {
            if (duration <= TimeSpan.Zero)
            {
                return;
            }
            _timelineTicks = checked(
                _timelineTicks +
                duration.Ticks);
            ulong nextBoundary = checked(
                (ulong)Math.Round(
                    _timelineTicks *
                    ((double)
                        IsoBmffCompositionWriter
                            .MovieTimescale /
                     TimeSpan.TicksPerSecond),
                    MidpointRounding
                        .AwayFromZero));
            ulong segmentDuration = checked(
                nextBoundary -
                _movieBoundary);
            _movieBoundary =
                nextBoundary;
            if (segmentDuration == 0)
            {
                return;
            }
            if (mediaTime == -1 &&
                _edits.Count != 0 &&
                _edits[^1].MediaTime == -1)
            {
                IsoBmffCompositionEdit previous =
                    _edits[^1];
                _edits[^1] = previous with
                {
                    SegmentDuration = checked(
                        previous.SegmentDuration +
                        segmentDuration)
                };
                return;
            }
            _edits.Add(
                new IsoBmffCompositionEdit(
                    segmentDuration,
                    mediaTime));
        }
    }
}
