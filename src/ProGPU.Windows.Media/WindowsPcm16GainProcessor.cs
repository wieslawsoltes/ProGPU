namespace ProGPU.Windows.Media;

/// <summary>
/// Allocation-free signed PCM16 gain for Media Foundation audio samples.
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
        if (!double.IsFinite(gain) ||
            gain is < 0d or > MaximumGain)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gain),
                $"PCM gain must be finite and between zero and {MaximumGain}.");
        }
        if (gain == 1d ||
            samples.IsEmpty)
        {
            return;
        }

        int fixedGain =
            (int)Math.Round(
                gain * 32_768d,
                MidpointRounding.AwayFromZero);
        if (fixedGain == 0)
        {
            samples.Clear();
            return;
        }

        for (int index = 0;
             index < samples.Length;
             index++)
        {
            int scaled =
                samples[index] *
                fixedGain /
                32_768;
            samples[index] =
                (short)Math.Clamp(
                    scaled,
                    short.MinValue,
                    short.MaxValue);
        }
    }
}
