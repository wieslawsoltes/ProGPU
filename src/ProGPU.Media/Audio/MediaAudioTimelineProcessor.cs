namespace ProGPU.Media.Audio;

/// <summary>
/// Immutable processor chain for one half-open interval on a composition
/// timeline.
/// </summary>
public readonly record struct MediaAudioTimelineSegment
{
    public MediaAudioTimelineSegment(
        TimeSpan start,
        TimeSpan duration,
        IReadOnlyList<IMediaAudioProcessor> processors)
    {
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        ArgumentNullException.ThrowIfNull(processors);

        Start = start;
        Duration = duration;
        Processors = processors;
    }

    public TimeSpan Start { get; }

    public TimeSpan Duration { get; }

    public IReadOnlyList<IMediaAudioProcessor> Processors { get; }
}

/// <summary>
/// Applies immutable effect chains to their composition intervals. Setup is
/// O(S + P) time and storage for S segments and P processors. Each callback
/// finds the first overlapping segment in O(log S), then processes
/// O(K + Pk * Fk * C) work for K overlapping segments, their processors Pk,
/// frames Fk, and C channels. Processing is allocation-free and lock-free.
/// </summary>
public sealed class MediaAudioTimelineProcessor :
    IMediaAudioProcessor
{
    private readonly Segment[] _segments;

    public MediaAudioTimelineProcessor(
        IEnumerable<MediaAudioTimelineSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        MediaAudioTimelineSegment[] source =
            segments.ToArray();
        Array.Sort(
            source,
            static (left, right) =>
                left.Start.CompareTo(right.Start));

        _segments = new Segment[source.Length];
        long previousEnd = 0;
        for (int index = 0; index < source.Length; index++)
        {
            MediaAudioTimelineSegment segment =
                source[index];
            long start = segment.Start.Ticks;
            long end = checked(
                start + segment.Duration.Ticks);
            if (index != 0 && start < previousEnd)
            {
                throw new ArgumentException(
                    "Audio timeline segments must not overlap.",
                    nameof(segments));
            }

            var processors =
                new IMediaAudioProcessor[
                    segment.Processors.Count];
            for (int processorIndex = 0;
                 processorIndex < processors.Length;
                 processorIndex++)
            {
                processors[processorIndex] =
                    segment.Processors[processorIndex] ??
                    throw new ArgumentException(
                        "Audio timeline processors cannot be null.",
                        nameof(segments));
            }
            _segments[index] =
                new Segment(start, end, processors);
            previousEnd = end;
        }
    }

    public int SegmentCount => _segments.Length;

    public void Process(
        Span<float> interleavedSamples,
        in MediaAudioProcessContext context)
    {
        int channelCount = context.Format.ChannelCount;
        int frameCount = context.FrameCount;
        int requiredSamples = checked(
            frameCount * channelCount);
        if (frameCount < 0 ||
            interleavedSamples.Length < requiredSamples)
        {
            throw new ArgumentException(
                "The callback buffer is smaller than the declared frame count.",
                nameof(interleavedSamples));
        }
        if (frameCount == 0 || _segments.Length == 0)
        {
            return;
        }

        long bufferStart = context.PresentationTime.Ticks;
        long bufferEnd = checked(
            bufferStart +
            FramesToTicksCeiling(
                frameCount,
                context.Format.SampleRate));
        int segmentIndex =
            FindFirstEndingAfter(bufferStart);
        for (; segmentIndex < _segments.Length; segmentIndex++)
        {
            ref readonly Segment segment =
                ref _segments[segmentIndex];
            if (segment.StartTicks >= bufferEnd)
            {
                break;
            }

            int firstFrame = TimeToFrameCeiling(
                segment.StartTicks - bufferStart,
                context.Format.SampleRate,
                frameCount);
            int endFrame = TimeToFrameCeiling(
                segment.EndTicks - bufferStart,
                context.Format.SampleRate,
                frameCount);
            int activeFrames = endFrame - firstFrame;
            if (activeFrames <= 0 ||
                segment.Processors.Length == 0)
            {
                continue;
            }

            int sampleOffset = checked(
                firstFrame * channelCount);
            int sampleCount = checked(
                activeFrames * channelCount);
            Span<float> activeSamples =
                interleavedSamples.Slice(
                    sampleOffset,
                    sampleCount);
            var activeContext =
                new MediaAudioProcessContext(
                    context.Format,
                    activeFrames,
                    TimeSpan.FromTicks(
                        checked(
                            bufferStart +
                            FramesToTicksFloor(
                                firstFrame,
                                context.Format
                                    .SampleRate))));
            for (int processorIndex = 0;
                 processorIndex <
                    segment.Processors.Length;
                 processorIndex++)
            {
                segment.Processors[processorIndex]
                    .Process(
                        activeSamples,
                        in activeContext);
            }
        }
    }

    private int FindFirstEndingAfter(long ticks)
    {
        int low = 0;
        int high = _segments.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_segments[middle].EndTicks <= ticks)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private static int TimeToFrameCeiling(
        long ticks,
        int sampleRate,
        int maximum)
    {
        if (ticks <= 0)
        {
            return 0;
        }
        Int128 numerator =
            (Int128)ticks * sampleRate;
        Int128 frames =
            (numerator + TimeSpan.TicksPerSecond - 1) /
            TimeSpan.TicksPerSecond;
        return frames >= maximum
            ? maximum
            : (int)frames;
    }

    private static long FramesToTicksFloor(
        int frames,
        int sampleRate) =>
        checked(
            (long)(
                (Int128)frames *
                TimeSpan.TicksPerSecond /
                sampleRate));

    private static long FramesToTicksCeiling(
        int frames,
        int sampleRate) =>
        checked(
            (long)(
                ((Int128)frames *
                 TimeSpan.TicksPerSecond +
                 sampleRate - 1) /
                sampleRate));

    private readonly record struct Segment(
        long StartTicks,
        long EndTicks,
        IMediaAudioProcessor[] Processors);
}
