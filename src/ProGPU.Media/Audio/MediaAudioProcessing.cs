namespace ProGPU.Media.Audio;

public enum MediaAudioSampleFormat
{
    Float32Interleaved
}

public readonly record struct MediaAudioFormat
{
    public MediaAudioFormat(
        int sampleRate,
        int channelCount,
        MediaAudioSampleFormat sampleFormat =
            MediaAudioSampleFormat.Float32Interleaved)
    {
        if (sampleRate is < 8_000 or > 768_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        if (channelCount is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        }

        SampleRate = sampleRate;
        ChannelCount = channelCount;
        SampleFormat = sampleFormat;
    }

    public int SampleRate { get; }
    public int ChannelCount { get; }
    public MediaAudioSampleFormat SampleFormat { get; }
}

public readonly record struct MediaAudioProcessContext(
    MediaAudioFormat Format,
    int FrameCount,
    TimeSpan PresentationTime);

/// <summary>
/// Finite, format-specific timing declared by an audio processor.
/// </summary>
/// <remarks>
/// Latency is the number of input frames by which audible output is delayed.
/// Tail is the maximum number of additional output frames which may remain
/// audible after the final non-silent input frame, excluding latency.
/// Providers query timing during graph preparation, never from a real-time
/// callback. The zero value describes a block-local processor.
/// </remarks>
public readonly record struct MediaAudioProcessorTiming
{
    public MediaAudioProcessorTiming(
        int latencyFrameCount,
        int tailFrameCount)
    {
        if (latencyFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latencyFrameCount));
        }
        if (tailFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tailFrameCount));
        }

        LatencyFrameCount = latencyFrameCount;
        TailFrameCount = tailFrameCount;
    }

    public static MediaAudioProcessorTiming Zero =>
        default;

    public int LatencyFrameCount { get; }

    public int TailFrameCount { get; }

    internal static MediaAudioProcessorTiming Sum<TProcessor>(
        ReadOnlySpan<TProcessor> processors,
        in MediaAudioFormat format)
        where TProcessor : class, IMediaAudioProcessor
    {
        long latency = 0;
        long tail = 0;
        for (int index = 0;
             index < processors.Length;
             index++)
        {
            if (processors[index] is not
                IMediaAudioProcessorTiming timed)
            {
                continue;
            }

            MediaAudioProcessorTiming timing =
                timed.GetTiming(in format);
            latency = checked(
                latency +
                timing.LatencyFrameCount);
            tail = checked(
                tail +
                timing.TailFrameCount);
            if (latency > int.MaxValue ||
                tail > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "The serial audio processor timing exceeds the supported finite frame range.");
            }
        }

        return new MediaAudioProcessorTiming(
            (int)latency,
            (int)tail);
    }
}

/// <summary>
/// Processes native callback storage in place. Implementations run on a
/// real-time audio thread and must not allocate, block, dispatch, log, perform
/// I/O, acquire locks, or throw.
/// </summary>
public interface IMediaAudioProcessor
{
    void Process(
        Span<float> interleavedSamples,
        in MediaAudioProcessContext context);
}

/// <summary>
/// Optional setup-time timing contract for an audio processor.
/// </summary>
/// <remarks>
/// Implementations must return deterministic finite timing for the supplied
/// format without mutating callback state. Processors which do not implement
/// this interface are block-local and have zero latency and zero tail.
/// </remarks>
public interface IMediaAudioProcessorTiming
{
    MediaAudioProcessorTiming GetTiming(
        in MediaAudioFormat format);
}

/// <summary>
/// Immutable-snapshot audio processor chain. Reconfiguration is O(P) time and
/// storage for P processors and happens off the callback thread. Processing is
/// O(P * F * C) for F frames and C channels, uses O(1) callback storage, and
/// performs no allocation or locking.
/// </summary>
public sealed class MediaAudioProcessorChain
{
    private IMediaAudioProcessor[] _processors = [];

    public int Count => Volatile.Read(ref _processors).Length;

    public void SetProcessors(
        IEnumerable<IMediaAudioProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        IMediaAudioProcessor[] snapshot = processors.ToArray();
        for (int index = 0; index < snapshot.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(snapshot[index]);
        }
        Volatile.Write(ref _processors, snapshot);
    }

    public void Clear() =>
        Volatile.Write(ref _processors, []);

    /// <summary>
    /// Returns the serial sum of the current snapshot's finite latency and
    /// tail declarations. This setup operation is O(P) and is not callback
    /// safe.
    /// </summary>
    public MediaAudioProcessorTiming GetTiming(
        in MediaAudioFormat format)
    {
        IMediaAudioProcessor[] processors =
            Volatile.Read(ref _processors);
        return MediaAudioProcessorTiming.Sum(
            processors,
            in format);
    }

    public void Process(
        Span<float> interleavedSamples,
        in MediaAudioProcessContext context)
    {
        int requiredSamples = checked(
            context.FrameCount *
            context.Format.ChannelCount);
        if (context.FrameCount < 0 ||
            interleavedSamples.Length < requiredSamples)
        {
            throw new ArgumentException(
                "The callback buffer is smaller than the declared frame count.",
                nameof(interleavedSamples));
        }

        Span<float> activeSamples =
            interleavedSamples[..requiredSamples];
        IMediaAudioProcessor[] processors =
            Volatile.Read(ref _processors);
        for (int index = 0; index < processors.Length; index++)
        {
            processors[index].Process(
                activeSamples,
                context);
        }
    }
}

/// <summary>
/// Allocation-free in-place gain processor. Processing is O(F * C) time and
/// O(1) storage.
/// </summary>
public sealed class MediaAudioGainProcessor : IMediaAudioProcessor
{
    private float _gain = 1f;

    public float Gain
    {
        get => Volatile.Read(ref _gain);
        set
        {
            if (!float.IsFinite(value) || value < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            Volatile.Write(ref _gain, value);
        }
    }

    public void Process(
        Span<float> interleavedSamples,
        in MediaAudioProcessContext context)
    {
        float gain = Volatile.Read(ref _gain);
        if (gain == 1f)
        {
            return;
        }

        for (int index = 0;
             index < interleavedSamples.Length;
             index++)
        {
            interleavedSamples[index] *= gain;
        }
    }
}
