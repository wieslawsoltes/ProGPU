namespace ProGPU.Windows.Media;

using ProGPU.Media.Audio;

/// <summary>
/// Allocation-free signed PCM16 gain and stereo levels for Media Foundation
/// audio samples.
/// </summary>
/// <remarks>
/// Work is O(S) for S samples with O(1) storage. Gain is quantized once to
/// Q15 and every sample uses one 32-bit multiply, divide, and saturating
/// clamp. The 0–2× range keeps the signed PCM16 × Q15 product within Int32.
/// </remarks>
internal static class WindowsPcm16GainProcessor
{
    internal const double MaximumGain = 2d;

    internal static void Apply(
        Span<short> samples,
        double gain)
    {
        int fixedGain =
            QuantizeGain(gain, nameof(gain));
        ApplyFixedGain(samples, fixedGain);
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

        int leftGain =
            QuantizeGain(
                levels.Left,
                nameof(levels));
        int rightGain =
            QuantizeGain(
                levels.Right,
                nameof(levels));
        if (samples.IsEmpty)
        {
            return;
        }

        if (channelCount == 1)
        {
            ApplyFixedGain(
                samples,
                Math.Max(leftGain, rightGain));
            channelOffset = 0;
            return;
        }

        int channel = channelOffset;
        for (int index = 0;
             index < samples.Length;
             index++)
        {
            samples[index] =
                ApplyFixedGain(
                    samples[index],
                    channel == 0
                        ? leftGain
                        : rightGain);
            channel ^= 1;
        }
        channelOffset = channel;
    }

    private static int QuantizeGain(
        double gain,
        string parameterName)
    {
        if (!double.IsFinite(gain) ||
            gain is < 0d or > MaximumGain)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"PCM gain must be finite and between zero and {MaximumGain}.");
        }

        return (int)Math.Round(
            gain * 32_768d,
            MidpointRounding.AwayFromZero);
    }

    private static void ApplyFixedGain(
        Span<short> samples,
        int fixedGain)
    {
        if (fixedGain == 32_768 ||
            samples.IsEmpty)
        {
            return;
        }
        if (fixedGain == 0)
        {
            samples.Clear();
            return;
        }

        for (int index = 0;
             index < samples.Length;
             index++)
        {
            samples[index] =
                ApplyFixedGain(
                    samples[index],
                    fixedGain);
        }
    }

    private static short ApplyFixedGain(
        short sample,
        int fixedGain)
    {
        int scaled =
            sample *
            fixedGain /
            32_768;
        return (short)Math.Clamp(
            scaled,
            short.MinValue,
            short.MaxValue);
    }
}
