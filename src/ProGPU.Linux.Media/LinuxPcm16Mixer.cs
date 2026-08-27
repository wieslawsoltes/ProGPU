using ProGPU.Media.Audio;

namespace ProGPU.Linux.Media;

/// <summary>
/// Prequantized left/right levels for the Linux composition mixer.
/// </summary>
internal readonly record struct LinuxPcm16MixLevels(
    int Left,
    int Right)
{
    internal bool IsSilent =>
        Left == 0 && Right == 0;

    internal static bool TryCreate(
        in MediaAudioStereoLevels levels,
        out LinuxPcm16MixLevels result)
    {
        if (!TryQuantize(levels.Left, out int left) ||
            !TryQuantize(levels.Right, out int right))
        {
            result = default;
            return false;
        }
        result =
            new LinuxPcm16MixLevels(
                left,
                right);
        return true;
    }

    private static bool TryQuantize(
        float level,
        out int result)
    {
        if (!float.IsFinite(level) ||
            level is < 0f or >
                (float)MediaPcm16StereoProcessor
                    .MaximumLevel)
        {
            result = 0;
            return false;
        }
        result = checked(
            (int)MathF.Round(
                level * 32_768f,
                MidpointRounding.AwayFromZero));
        return true;
    }
}

/// <summary>
/// Wide fixed-block PCM16 accumulation used before a native codec boundary.
/// </summary>
/// <remarks>
/// Adding S interleaved samples is O(S), uses caller-owned O(S) storage, and
/// performs no allocation. Each contribution is scaled in Q15 into signed
/// 64-bit lanes. Saturation occurs once after all active sources have been
/// accumulated, preserving source-order independence until Int64 overflow.
/// </remarks>
internal static class LinuxPcm16Mixer
{
    internal const int FramesPerBlock = 1_024;

    internal static void Accumulate(
        ReadOnlySpan<short> source,
        uint channelCount,
        in LinuxPcm16MixLevels levels,
        Span<long> accumulator,
        int destinationFrameOffset)
    {
        if (channelCount is not (1u or 2u))
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelCount));
        }
        int channels = checked((int)channelCount);
        if (source.Length % channels != 0)
        {
            throw new ArgumentException(
                "PCM16 input must contain complete interleaved frames.",
                nameof(source));
        }
        if (destinationFrameOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationFrameOffset));
        }

        int firstSample =
            checked(
                destinationFrameOffset *
                channels);
        if (firstSample > accumulator.Length ||
            source.Length >
                accumulator.Length - firstSample)
        {
            throw new ArgumentException(
                "The source interval does not fit in the accumulator block.",
                nameof(accumulator));
        }
        if (source.IsEmpty || levels.IsSilent)
        {
            return;
        }

        if (channels == 1)
        {
            int monoLevel =
                Math.Max(
                    levels.Left,
                    levels.Right);
            MediaPcm16WideAccumulator.AddMono(
                source,
                monoLevel,
                accumulator.Slice(
                    firstSample,
                    source.Length));
            return;
        }

        MediaPcm16WideAccumulator.AddStereo(
            source,
            levels.Left,
            levels.Right,
            accumulator.Slice(
                firstSample,
                source.Length));
    }

    internal static void AccumulateProcessed(
        ReadOnlySpan<float> source,
        uint channelCount,
        in LinuxPcm16MixLevels levels,
        Span<long> accumulator,
        int destinationFrameOffset)
    {
        if (channelCount is not (1u or 2u))
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelCount));
        }
        int channels = checked((int)channelCount);
        if (source.Length % channels != 0)
        {
            throw new ArgumentException(
                "Processed input must contain complete interleaved frames.",
                nameof(source));
        }
        if (destinationFrameOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationFrameOffset));
        }

        int firstSample =
            checked(
                destinationFrameOffset *
                channels);
        if (firstSample > accumulator.Length ||
            source.Length >
                accumulator.Length - firstSample)
        {
            throw new ArgumentException(
                "The processed interval does not fit in the accumulator block.",
                nameof(accumulator));
        }
        if (source.IsEmpty || levels.IsSilent)
        {
            return;
        }

        if (channels == 1)
        {
            int monoLevel =
                Math.Max(
                    levels.Left,
                    levels.Right);
            for (int index = 0;
                 index < source.Length;
                 index++)
            {
                AccumulateProcessedSample(
                    ref accumulator[
                        firstSample + index],
                    source[index],
                    monoLevel);
            }
            return;
        }

        for (int index = 0;
             index < source.Length;
             index += 2)
        {
            AccumulateProcessedSample(
                ref accumulator[
                    firstSample + index],
                source[index],
                levels.Left);
            AccumulateProcessedSample(
                ref accumulator[
                    firstSample + index + 1],
                source[index + 1],
                levels.Right);
        }
    }

    internal static void Saturate(
        ReadOnlySpan<long> accumulator,
        Span<short> destination)
    {
        if (accumulator.Length !=
            destination.Length)
        {
            throw new ArgumentException(
                "Accumulator and output spans must have equal lengths.",
                nameof(destination));
        }
        MediaPcm16WideAccumulator.WriteSaturated(
            accumulator,
            destination);
    }

    private static void AccumulateProcessedSample(
        ref long accumulator,
        float sample,
        int level)
    {
        if (!float.IsFinite(sample))
        {
            throw new InvalidDataException(
                "A typed Linux composition audio effect emitted a non-finite sample.");
        }

        double scaled = (double)sample * level;
        long contribution =
            scaled >= long.MaxValue
                ? long.MaxValue
                : scaled <= long.MinValue
                    ? long.MinValue
                    : checked(
                        (long)Math.Round(
                            scaled,
                            MidpointRounding
                                .AwayFromZero));
        if (contribution > 0 &&
            accumulator >
                long.MaxValue - contribution)
        {
            accumulator = long.MaxValue;
        }
        else if (contribution < 0 &&
                 accumulator <
                    long.MinValue - contribution)
        {
            accumulator = long.MinValue;
        }
        else
        {
            accumulator += contribution;
        }
    }
}
