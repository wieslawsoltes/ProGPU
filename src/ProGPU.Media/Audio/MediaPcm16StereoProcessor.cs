namespace ProGPU.Media.Audio;

/// <summary>
/// Allocation-free signed PCM16 gain and stereo-level processing shared by
/// native media providers.
/// </summary>
/// <remarks>
/// Work is O(S) for S samples with O(1) storage. Each level is quantized once
/// to Q15 and every changed sample uses one 32-bit multiply, divide, and
/// saturating clamp. The 0–2× range keeps the PCM16 × Q15 product in Int32.
/// </remarks>
internal static class MediaPcm16StereoProcessor
{
    internal const double MaximumLevel = 2d;

    internal static void Apply(
        Span<short> samples,
        double level)
    {
        int fixedLevel =
            QuantizeLevel(level, nameof(level));
        ApplyFixedLevel(samples, fixedLevel);
    }

    internal static void ApplyStereo(
        Span<short> samples,
        uint channelCount,
        in MediaAudioStereoLevels levels,
        ref int channelOffset)
    {
        if (channelCount is not (1u or 2u))
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelCount));
        }
        if ((uint)channelOffset >= channelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelOffset));
        }

        int leftLevel =
            QuantizeLevel(
                levels.Left,
                nameof(levels));
        int rightLevel =
            QuantizeLevel(
                levels.Right,
                nameof(levels));
        if (samples.IsEmpty)
        {
            return;
        }

        if (channelCount == 1)
        {
            ApplyFixedLevel(
                samples,
                Math.Max(
                    leftLevel,
                    rightLevel));
            channelOffset = 0;
            return;
        }

        int channel = channelOffset;
        for (int index = 0;
             index < samples.Length;
             index++)
        {
            samples[index] =
                ApplyFixedLevel(
                    samples[index],
                    channel == 0
                        ? leftLevel
                        : rightLevel);
            channel ^= 1;
        }
        channelOffset = channel;
    }

    private static int QuantizeLevel(
        double level,
        string parameterName)
    {
        if (!double.IsFinite(level) ||
            level is < 0d or > MaximumLevel)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"PCM level must be finite and between zero and {MaximumLevel}.");
        }

        return (int)Math.Round(
            level * 32_768d,
            MidpointRounding.AwayFromZero);
    }

    private static void ApplyFixedLevel(
        Span<short> samples,
        int fixedLevel)
    {
        if (fixedLevel == 32_768 ||
            samples.IsEmpty)
        {
            return;
        }
        if (fixedLevel == 0)
        {
            samples.Clear();
            return;
        }

        for (int index = 0;
             index < samples.Length;
             index++)
        {
            samples[index] =
                ApplyFixedLevel(
                    samples[index],
                    fixedLevel);
        }
    }

    private static short ApplyFixedLevel(
        short sample,
        int fixedLevel)
    {
        int scaled =
            sample *
            fixedLevel /
            32_768;
        return (short)Math.Clamp(
            scaled,
            short.MinValue,
            short.MaxValue);
    }
}
