using ProGPU.Media.Audio;

namespace ProGPU.Windows.Media;

/// <summary>
/// Prequantized per-channel PCM16 levels used by the Windows native mixer.
/// </summary>
internal readonly record struct WindowsPcm16MixLevels(
    int Left,
    int Right)
{
    internal static bool TryCreate(
        in MediaAudioStereoLevels levels,
        out WindowsPcm16MixLevels result)
    {
        if (!TryQuantize(levels.Left, out int left) ||
            !TryQuantize(levels.Right, out int right))
        {
            result = default;
            return false;
        }
        result = new WindowsPcm16MixLevels(left, right);
        return true;
    }

    private static bool TryQuantize(
        float level,
        out int fixedLevel)
    {
        if (!float.IsFinite(level) ||
            level is < 0f or >
                (float)WindowsPcm16GainProcessor
                    .MaximumGain)
        {
            fixedLevel = 0;
            return false;
        }
        fixedLevel =
            (int)MathF.Round(
                level * 32_768f,
                MidpointRounding.AwayFromZero);
        return true;
    }
}

/// <summary>
/// Allocation-free wide-accumulator PCM16 mixing primitives.
/// </summary>
/// <remarks>
/// Mixing is O(S) for S interleaved samples with O(S) caller-owned
/// accumulator storage. Each input is scaled once in Q15 and added to a
/// signed 64-bit accumulator. Saturation happens exactly once when the final
/// PCM16 block is written, so the result is independent of source order for
/// every practical composition size.
/// </remarks>
internal static class WindowsPcm16Mixer
{
    internal const int FramesPerBlock = 1_024;

    internal static void Add(
        ReadOnlySpan<short> source,
        uint channelCount,
        in WindowsPcm16MixLevels levels,
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
}
