using System.Buffers;
using ProGPU.Media.Audio;
using ProGPU.Media.Containers;
using ProGPU.Media.Effects;

namespace ProGPU.Linux.Media;

internal delegate void LinuxPcm16BlockHandler(
    long firstFrame,
    ReadOnlySpan<short> interleavedSamples);

internal interface ILinuxPcm16TimelineSource :
    IDisposable
{
    void ReadFrames(
        long firstFrame,
        Span<short> destination);
}

/// <summary>
/// Streams one ISO-BMFF PCM track through the composition mixer.
/// </summary>
/// <remarks>
/// Opening is O(S) time and storage for S indexed samples. A monotonic read
/// is O(log S + P) for P copied scalar samples, retains one pooled converted
/// media sample, and performs no allocation after the first PCM16 read.
/// Missing presentation intervals are returned as silence.
/// </remarks>
internal sealed class IsoBmffPcm16TimelineSource :
    ILinuxPcm16TimelineSource
{
    private readonly FileStream _stream;
    private readonly IsoBmffTrack _track;
    private readonly IsoBmffPcmSampleReader _reader;
    private readonly long[] _sampleStartFrames;
    private readonly int[] _sampleFrameCounts;
    private readonly int _channelCount;
    private int _currentSampleIndex = -1;
    private bool _disposed;

    internal IsoBmffPcm16TimelineSource(
        in LinuxCompositionAudioSourcePlan plan,
        uint sampleRate,
        uint channelCount)
    {
        if (sampleRate == 0 ||
            channelCount is not (1u or 2u))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }
        string path =
            Path.GetFullPath(
                plan.SourceUri.LocalPath);
        _stream =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.RandomAccess);
        try
        {
            _track =
                SelectTrack(
                    new IsoBmffDemuxer(
                        _stream).Parse(),
                    plan.SourceTrackIndex);
            if (_track.Codec !=
                    IsoBmffCodec.Pcm ||
                _track.PcmEncoding ==
                    IsoBmffPcmEncoding.Unknown ||
                _track.AudioSampleRate !=
                    sampleRate ||
                _track.AudioChannelCount !=
                    channelCount)
            {
                throw new NotSupportedException(
                    "The built-in Linux composition mixer requires signed sowt/twos PCM matching the requested sample rate and mono/stereo channel count.");
            }

            _channelCount =
                checked((int)channelCount);
            _sampleStartFrames =
                new long[_track.Samples.Length];
            _sampleFrameCounts =
                new int[_track.Samples.Length];
            int scalarBytes =
                _track.AudioBitsPerSample / 8;
            int frameBytes =
                checked(
                    scalarBytes *
                    _channelCount);
            long previousEndFrame = 0;
            for (int index = 0;
                 index < _track.Samples.Length;
                 index++)
            {
                IsoBmffSample sample =
                    _track.Samples[index];
                if (sample.PresentationTime < 0 ||
                    sample.Size % frameBytes != 0)
                {
                    throw new InvalidDataException(
                        "The PCM sample index contains a negative timestamp or incomplete interleaved frame.");
                }
                _sampleStartFrames[index] =
                    ScaleFloor(
                        sample.PresentationTime,
                        sampleRate,
                        _track.Timescale);
                _sampleFrameCounts[index] =
                    sample.Size /
                    frameBytes;
                if (_sampleStartFrames[index] <
                    previousEndFrame)
                {
                    throw new InvalidDataException(
                        "The built-in PCM timeline requires nonoverlapping samples in presentation order.");
                }
                previousEndFrame = checked(
                    _sampleStartFrames[index] +
                    _sampleFrameCounts[index]);
            }
            _reader =
                new IsoBmffPcmSampleReader(
                    _stream,
                    _track);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public void ReadFrames(
        long firstFrame,
        Span<short> destination)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
        if (firstFrame < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstFrame));
        }
        if (destination.Length %
                _channelCount !=
            0)
        {
            throw new ArgumentException(
                "The destination must contain complete interleaved frames.",
                nameof(destination));
        }

        destination.Clear();
        long cursor = firstFrame;
        int destinationFrame = 0;
        int remainingFrames =
            destination.Length /
            _channelCount;
        while (remainingFrames > 0)
        {
            int sampleIndex =
                FindSampleAtOrBefore(cursor);
            if (sampleIndex >= 0)
            {
                long sampleStart =
                    _sampleStartFrames[
                        sampleIndex];
                int sampleFrames =
                    _sampleFrameCounts[
                        sampleIndex];
                long sampleEnd =
                    checked(
                        sampleStart +
                        sampleFrames);
                if (cursor < sampleEnd)
                {
                    ReadOnlySpan<short> sample =
                        sampleIndex ==
                            _currentSampleIndex
                            ? _reader.CurrentPcm16
                            : ReadSample(
                                sampleIndex);
                    int sourceFrame =
                        checked(
                            (int)(
                                cursor -
                                sampleStart));
                    int copiedFrames =
                        Math.Min(
                            remainingFrames,
                            sampleFrames -
                            sourceFrame);
                    sample.Slice(
                            sourceFrame *
                                _channelCount,
                            copiedFrames *
                                _channelCount)
                        .CopyTo(
                            destination.Slice(
                                destinationFrame *
                                    _channelCount));
                    cursor = checked(
                        cursor +
                        copiedFrames);
                    destinationFrame +=
                        copiedFrames;
                    remainingFrames -=
                        copiedFrames;
                    continue;
                }
            }

            int nextIndex =
                FindFirstSampleAfter(cursor);
            int silentFrames =
                nextIndex < 0
                    ? remainingFrames
                    : checked(
                        (int)Math.Min(
                            remainingFrames,
                            _sampleStartFrames[
                                nextIndex] -
                            cursor));
            if (silentFrames <= 0)
            {
                // Equal-timestamp or overlapping samples select the latest
                // indexed sample and guarantee forward progress.
                silentFrames = 1;
            }
            cursor = checked(
                cursor +
                silentFrames);
            destinationFrame += silentFrames;
            remainingFrames -= silentFrames;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _reader.Dispose();
        _stream.Dispose();
    }

    private ReadOnlySpan<short> ReadSample(
        int sampleIndex)
    {
        ReadOnlySpan<short> sample =
            _reader.ReadPcm16(
                sampleIndex);
        _currentSampleIndex =
            sampleIndex;
        return sample;
    }

    private int FindSampleAtOrBefore(
        long frame)
    {
        int low = 0;
        int high =
            _sampleStartFrames.Length - 1;
        int result = -1;
        while (low <= high)
        {
            int middle =
                low + (high - low) / 2;
            if (_sampleStartFrames[middle] <= frame)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return result;
    }

    private int FindFirstSampleAfter(
        long frame)
    {
        int low = 0;
        int high =
            _sampleStartFrames.Length;
        while (low < high)
        {
            int middle =
                low + (high - low) / 2;
            if (_sampleStartFrames[middle] <= frame)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low <
            _sampleStartFrames.Length
                ? low
                : -1;
    }

    private static IsoBmffTrack SelectTrack(
        IsoBmffMovie movie,
        uint selectedIndex)
    {
        uint audioIndex = 0;
        for (int index = 0;
             index < movie.Tracks.Length;
             index++)
        {
            IsoBmffTrack track =
                movie.Tracks[index];
            if (track.Kind !=
                IsoBmffTrackKind.Audio)
            {
                continue;
            }
            if (audioIndex == selectedIndex)
            {
                return track;
            }
            audioIndex++;
        }
        throw new InvalidDataException(
            "The selected audio-track index is outside the source track list.");
    }

    private static long ScaleFloor(
        long value,
        uint numerator,
        uint denominator)
    {
        if (value < 0 || denominator == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value));
        }
        return checked(
            value /
                denominator *
                numerator +
            value %
                denominator *
                numerator /
                denominator);
    }
}

/// <summary>
/// Executes a captured Linux composition-audio plan in fixed PCM16 blocks.
/// </summary>
/// <remarks>
/// Work is O(F × A) for F output frames and A active sources in the simple
/// bounded scan. Typed processor work is O(E × F × C) for E block-local
/// effects and C channels. The block workspace is O(1,024 × channels); each
/// source additionally retains its ISO-BMFF sample index, one pooled
/// media-sample conversion buffer, and its prepared effect instances. No PCM
/// storage scales with composition duration, and pooled blocks are returned
/// on every success or failure path.
/// </remarks>
internal static class LinuxPcm16TimelineMixer
{
    internal static void Mix(
        ReadOnlySpan<LinuxCompositionAudioSourcePlan>
            plans,
        long compositionFrameCount,
        uint sampleRate,
        uint channelCount,
        MediaEffectRegistry effects,
        LinuxPcm16BlockHandler handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(handler);
        if (compositionFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(compositionFrameCount));
        }
        if (sampleRate == 0 ||
            channelCount is not (1u or 2u))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }

        var sources =
            new ILinuxPcm16TimelineSource[
                plans.Length];
        var processorChains =
            new MediaAudioEffectProcessorChain?[
                plans.Length];
        try
        {
            for (int index = 0;
                 index < plans.Length;
                 index++)
            {
                sources[index] =
                    new IsoBmffPcm16TimelineSource(
                        plans[index],
                        sampleRate,
                        channelCount);
                if (plans[index]
                        .ProcessorDefinitions
                        .Length != 0 &&
                    !MediaAudioEffectProcessorChain
                        .TryCreate(
                            effects,
                            plans[index]
                                .ProcessorDefinitions,
                            out processorChains[
                                index]))
                {
                    throw new NotSupportedException(
                        "A prepared Linux composition audio effect can no longer be activated through the typed registry.");
                }
            }
            MixCore(
                plans,
                sources,
                compositionFrameCount,
                sampleRate,
                channelCount,
                handler,
                cancellationToken,
                processorChains);
        }
        finally
        {
            for (int index = 0;
                 index < sources.Length;
                 index++)
            {
                sources[index]?.Dispose();
                processorChains[index]
                    ?.Dispose();
            }
        }
    }

    internal static void MixCore(
        ReadOnlySpan<LinuxCompositionAudioSourcePlan>
            plans,
        ReadOnlySpan<ILinuxPcm16TimelineSource>
            sources,
        long compositionFrameCount,
        uint sampleRate,
        uint channelCount,
        LinuxPcm16BlockHandler handler,
        CancellationToken cancellationToken = default,
        ReadOnlySpan<
            MediaAudioEffectProcessorChain?>
            processorChains = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (plans.Length != sources.Length)
        {
            throw new ArgumentException(
                "Every audio plan requires exactly one source.",
                nameof(sources));
        }
        if (!processorChains.IsEmpty &&
            processorChains.Length !=
                plans.Length)
        {
            throw new ArgumentException(
                "Every audio plan requires exactly one processor-chain slot.",
                nameof(processorChains));
        }
        if (compositionFrameCount <= 0 ||
            sampleRate == 0 ||
            channelCount is not (1u or 2u))
        {
            throw new ArgumentOutOfRangeException(
                nameof(compositionFrameCount));
        }

        int channels =
            checked((int)channelCount);
        int maximumSamples =
            checked(
                LinuxPcm16Mixer
                    .FramesPerBlock *
                channels);
        long[] accumulator =
            ArrayPool<long>.Shared.Rent(
                maximumSamples);
        short[] sourceBuffer =
            ArrayPool<short>.Shared.Rent(
                maximumSamples);
        short[] output =
            ArrayPool<short>.Shared.Rent(
                maximumSamples);
        float[]? effectBuffer =
            HasProcessorChain(
                processorChains)
                ? ArrayPool<float>
                    .Shared.Rent(
                        maximumSamples)
                : null;
        try
        {
            for (long blockStart = 0;
                 blockStart <
                    compositionFrameCount;
                 blockStart = checked(
                     blockStart +
                     LinuxPcm16Mixer
                         .FramesPerBlock))
            {
                cancellationToken
                    .ThrowIfCancellationRequested();
                int blockFrames =
                    checked(
                        (int)Math.Min(
                            LinuxPcm16Mixer
                                .FramesPerBlock,
                            compositionFrameCount -
                            blockStart));
                int blockSamples =
                    checked(
                        blockFrames *
                        channels);
                Span<long> blockAccumulator =
                    accumulator.AsSpan(
                        0,
                        blockSamples);
                blockAccumulator.Clear();

                long blockEnd =
                    checked(
                        blockStart +
                        blockFrames);
                for (int index = 0;
                     index < plans.Length;
                     index++)
                {
                    LinuxCompositionAudioSourcePlan
                        plan = plans[index];
                    long overlapStart =
                        Math.Max(
                            blockStart,
                            plan
                                .DestinationStartFrame);
                    long overlapEnd =
                        Math.Min(
                            blockEnd,
                            plan
                                .DestinationEndFrame);
                    if (overlapEnd <= overlapStart)
                    {
                        continue;
                    }

                    int frameCount =
                        checked(
                            (int)(
                                overlapEnd -
                                overlapStart));
                    Span<short> source =
                        sourceBuffer.AsSpan(
                            0,
                            checked(
                                frameCount *
                                channels));
                    long sourceFirstFrame =
                        checked(
                            TicksToFramesCeiling(
                                plan.SourceStartTicks,
                                sampleRate) +
                            overlapStart -
                            plan
                                .DestinationStartFrame);
                    sources[index].ReadFrames(
                        sourceFirstFrame,
                        source);
                    MediaAudioEffectProcessorChain?
                        processorChain =
                            processorChains.IsEmpty
                                ? null
                                : processorChains[
                                    index];
                    if (processorChain is not null)
                    {
                        ReadOnlySpan<float>
                            processed =
                                ProcessEffects(
                            source,
                            effectBuffer!,
                            processorChain,
                            sampleRate,
                            channels,
                            overlapStart);
                        LinuxPcm16Mixer
                            .AccumulateProcessed(
                                processed,
                                channelCount,
                                plan.Levels,
                                blockAccumulator,
                                checked(
                                    (int)(
                                        overlapStart -
                                        blockStart)));
                    }
                    else
                    {
                        LinuxPcm16Mixer.Accumulate(
                            source,
                            channelCount,
                            plan.Levels,
                            blockAccumulator,
                            checked(
                                (int)(
                                    overlapStart -
                                    blockStart)));
                    }
                }

                Span<short> result =
                    output.AsSpan(
                        0,
                        blockSamples);
                LinuxPcm16Mixer.Saturate(
                    blockAccumulator,
                    result);
                handler(
                    blockStart,
                    result);
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(
                accumulator);
            ArrayPool<short>.Shared.Return(
                sourceBuffer);
            ArrayPool<short>.Shared.Return(
                output);
            if (effectBuffer is not null)
            {
                ArrayPool<float>.Shared.Return(
                    effectBuffer);
            }
        }
    }

    private static bool HasProcessorChain(
        ReadOnlySpan<
            MediaAudioEffectProcessorChain?>
            processorChains)
    {
        for (int index = 0;
             index < processorChains.Length;
             index++)
        {
            if (processorChains[index] is not
                null)
            {
                return true;
            }
        }
        return false;
    }

    private static ReadOnlySpan<float>
        ProcessEffects(
        Span<short> source,
        float[] effectBuffer,
        MediaAudioEffectProcessorChain chain,
        uint sampleRate,
        int channelCount,
        long presentationFirstFrame)
    {
        Span<float> samples =
            effectBuffer.AsSpan(
                0,
                source.Length);
        for (int index = 0;
             index < source.Length;
             index++)
        {
            samples[index] =
                source[index] /
                32_768f;
        }

        var context =
            new MediaAudioProcessContext(
                new MediaAudioFormat(
                    checked((int)sampleRate),
                    channelCount),
                source.Length /
                    channelCount,
                TimeSpan.FromTicks(
                    FramesToTicksFloor(
                        presentationFirstFrame,
                        sampleRate)));
        chain.Process(
            samples,
            context);
        return samples;
    }

    private static long FramesToTicksFloor(
        long frame,
        uint sampleRate)
    {
        if (frame < 0 || sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame));
        }
        return checked(
            (long)(
                (Int128)frame *
                TimeSpan.TicksPerSecond /
                sampleRate));
    }

    private static long TicksToFramesCeiling(
        long ticks,
        uint sampleRate)
    {
        if (ticks < 0 || sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ticks));
        }
        return checked(
            ticks /
                TimeSpan.TicksPerSecond *
                sampleRate +
            (ticks %
                 TimeSpan.TicksPerSecond *
                 sampleRate +
             TimeSpan.TicksPerSecond -
             1) /
                TimeSpan.TicksPerSecond);
    }
}
