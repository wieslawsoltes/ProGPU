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
