using ProGPU.Media.Audio;

namespace ProGPU.Android.Media;

/// <summary>
/// Prequantized PCM16 levels for the Android composition mixer.
/// </summary>
internal readonly record struct AndroidPcm16MixLevels(
    int Left,
    int Right)
{
    internal static bool TryCreate(
        in MediaAudioStereoLevels levels,
        out AndroidPcm16MixLevels result)
    {
        if (!TryQuantize(levels.Left, out int left) ||
            !TryQuantize(levels.Right, out int right))
        {
            result = default;
            return false;
        }

        result =
            new AndroidPcm16MixLevels(
                left,
                right);
        return true;
    }

    private static bool TryQuantize(
        float level,
        out int fixedLevel)
    {
        if (!float.IsFinite(level) ||
            level is < 0f or >
                (float)MediaPcm16StereoProcessor
                    .MaximumLevel)
        {
            fixedLevel = 0;
            return false;
        }

        fixedLevel =
            checked(
                (int)MathF.Round(
                    level * 32_768f,
                    MidpointRounding.AwayFromZero));
        return true;
    }
}

/// <summary>
/// Fixed-work PCM16 accumulation primitives used by Android native export.
/// </summary>
/// <remarks>
/// Add is O(S) for S interleaved input samples and uses O(S) caller-owned
/// accumulator storage. Inputs are scaled in Q15 into signed 64-bit lanes.
/// Saturation occurs once, after every overlapping source has contributed,
/// so practical composition results do not depend on source order.
/// </remarks>
internal static class AndroidPcm16Mixer
{
    internal const int FramesPerBlock = 1_024;

    internal static void Add(
        ReadOnlySpan<short> source,
        uint channelCount,
        in AndroidPcm16MixLevels levels,
        Span<long> destination,
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

        int destinationSampleOffset =
            checked(destinationFrameOffset * channels);
        if (destinationSampleOffset >
                destination.Length ||
            source.Length >
                destination.Length -
                destinationSampleOffset)
        {
            throw new ArgumentException(
                "The PCM16 source does not fit in the destination block.",
                nameof(destination));
        }
        if (source.IsEmpty ||
            levels.Left == 0 &&
            levels.Right == 0)
        {
            return;
        }

        if (channels == 1)
        {
            int level =
                Math.Max(
                    levels.Left,
                    levels.Right);
            for (int index = 0;
                 index < source.Length;
                 index++)
            {
                destination[
                    destinationSampleOffset + index] +=
                    (long)source[index] *
                    level /
                    32_768;
            }
            return;
        }

        for (int index = 0;
             index < source.Length;
             index += 2)
        {
            destination[
                destinationSampleOffset + index] +=
                (long)source[index] *
                levels.Left /
                32_768;
            destination[
                destinationSampleOffset + index + 1] +=
                (long)source[index + 1] *
                levels.Right /
                32_768;
        }
    }

    internal static void AddProcessed(
        ReadOnlySpan<float> source,
        uint channelCount,
        in AndroidPcm16MixLevels levels,
        Span<long> destination,
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

        int destinationSampleOffset =
            checked(destinationFrameOffset * channels);
        if (destinationSampleOffset >
                destination.Length ||
            source.Length >
                destination.Length -
                destinationSampleOffset)
        {
            throw new ArgumentException(
                "The processed source does not fit in the destination block.",
                nameof(destination));
        }
        if (source.IsEmpty ||
            levels.Left == 0 &&
            levels.Right == 0)
        {
            return;
        }

        if (channels == 1)
        {
            int level =
                Math.Max(
                    levels.Left,
                    levels.Right);
            for (int index = 0;
                 index < source.Length;
                 index++)
            {
                AddProcessedSample(
                    ref destination[
                        destinationSampleOffset +
                        index],
                    source[index],
                    level);
            }
            return;
        }

        for (int index = 0;
             index < source.Length;
             index += 2)
        {
            AddProcessedSample(
                ref destination[
                    destinationSampleOffset +
                    index],
                source[index],
                levels.Left);
            AddProcessedSample(
                ref destination[
                    destinationSampleOffset +
                    index + 1],
                source[index + 1],
                levels.Right);
        }
    }

    internal static void WriteSaturated(
        ReadOnlySpan<long> source,
        Span<short> destination)
    {
        if (source.Length != destination.Length)
        {
            throw new ArgumentException(
                "Accumulator and PCM16 output lengths must match.",
                nameof(destination));
        }

        for (int index = 0;
             index < source.Length;
             index++)
        {
            destination[index] =
                (short)Math.Clamp(
                    source[index],
                    short.MinValue,
                    short.MaxValue);
        }
    }

    private static void AddProcessedSample(
        ref long accumulator,
        float sample,
        int level)
    {
        if (!float.IsFinite(sample))
        {
            throw new InvalidDataException(
                "A typed Android composition audio effect emitted a non-finite sample.");
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
