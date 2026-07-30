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
    internal const double MaximumGain =
        MediaPcm16StereoProcessor.MaximumLevel;

    internal static void Apply(
        Span<short> samples,
        double gain) =>
        MediaPcm16StereoProcessor.Apply(
            samples,
            gain);

    internal static void ApplyStereo(
        Span<short> samples,
        uint channelCount,
        in MediaAudioStereoLevels levels,
        ref int channelOffset) =>
        MediaPcm16StereoProcessor.ApplyStereo(
            samples,
            channelCount,
            levels,
            ref channelOffset);
}
