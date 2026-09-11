using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ProGPU.Media.Audio;

/// <summary>
/// Allocation-free signed PCM16 gain and stereo-level processing shared by
/// native media providers.
/// </summary>
/// <remarks>
/// Work is O(S) for S samples with O(1) storage. Each level is quantized once
/// to Q15. Changed independent lanes use fixed-width hardware intrinsics with
/// exact truncate-toward-zero correction, saturating narrowing, and a bounded
/// scalar tail. The 0–2× range keeps the PCM16 × Q15 product in Int32.
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

        if (leftLevel == 32_768 &&
            rightLevel == 32_768)
        {
            channelOffset =
                (channelOffset + samples.Length) & 1;
            return;
        }
        if (leftLevel == 0 &&
            rightLevel == 0)
        {
            samples.Clear();
            channelOffset =
                (channelOffset + samples.Length) & 1;
            return;
        }

        ApplyFixedStereoLevels(
            samples,
            leftLevel,
            rightLevel,
            channelOffset);
        channelOffset =
            (channelOffset + samples.Length) & 1;
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

        ApplyFixedStereoLevels(
            samples,
            fixedLevel,
            fixedLevel,
            channelOffset: 0);
    }

    private static void ApplyFixedStereoLevels(
        Span<short> samples,
        int leftLevel,
        int rightLevel,
        int channelOffset)
    {
        int index = 0;
        ref short start = ref MemoryMarshal.GetReference(samples);
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<int> levels = CreateStereoLevels256(
                leftLevel,
                rightLevel,
                channelOffset);
            for (; index <= samples.Length - Vector256<short>.Count;
                 index += Vector256<short>.Count)
            {
                (Vector256<int> low, Vector256<int> high) =
                    Vector256.Widen(
                        Vector256.LoadUnsafe(
                            ref start,
                            (nuint)index));
                Vector256.Narrow(
                    ScaleAndClamp(low, levels),
                    ScaleAndClamp(high, levels))
                    .StoreUnsafe(
                        ref start,
                        (nuint)index);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<int> levels = CreateStereoLevels128(
                leftLevel,
                rightLevel,
                channelOffset);
            for (; index <= samples.Length - Vector128<short>.Count;
                 index += Vector128<short>.Count)
            {
                (Vector128<int> low, Vector128<int> high) =
                    Vector128.Widen(
                        Vector128.LoadUnsafe(
                            ref start,
                            (nuint)index));
                Vector128.Narrow(
                    ScaleAndClamp(low, levels),
                    ScaleAndClamp(high, levels))
                    .StoreUnsafe(
                        ref start,
                        (nuint)index);
            }
        }

        int channel = (channelOffset + index) & 1;
        for (; index < samples.Length; index++)
        {
            samples[index] =
                ApplyFixedLevel(
                    samples[index],
                    channel == 0
                        ? leftLevel
                        : rightLevel);
            channel ^= 1;
        }
    }

    private static Vector256<int> CreateStereoLevels256(
        int leftLevel,
        int rightLevel,
        int channelOffset) =>
        channelOffset == 0
            ? Vector256.Create(
                leftLevel,
                rightLevel,
                leftLevel,
                rightLevel,
                leftLevel,
                rightLevel,
                leftLevel,
                rightLevel)
            : Vector256.Create(
                rightLevel,
                leftLevel,
                rightLevel,
                leftLevel,
                rightLevel,
                leftLevel,
                rightLevel,
                leftLevel);

    private static Vector128<int> CreateStereoLevels128(
        int leftLevel,
        int rightLevel,
        int channelOffset) =>
        channelOffset == 0
            ? Vector128.Create(
                leftLevel,
                rightLevel,
                leftLevel,
                rightLevel)
            : Vector128.Create(
                rightLevel,
                leftLevel,
                rightLevel,
                leftLevel);

    private static Vector256<int> ScaleAndClamp(
        Vector256<int> samples,
        Vector256<int> levels)
    {
        Vector256<int> products = samples * levels;
        Vector256<int> scaled =
            (products +
             ((products >> 31) & Vector256.Create(32_767))) >> 15;
        return Vector256.Min(
            Vector256.Max(
                scaled,
                Vector256.Create((int)short.MinValue)),
            Vector256.Create((int)short.MaxValue));
    }

    private static Vector128<int> ScaleAndClamp(
        Vector128<int> samples,
        Vector128<int> levels)
    {
        Vector128<int> products = samples * levels;
        Vector128<int> scaled =
            (products +
             ((products >> 31) & Vector128.Create(32_767))) >> 15;
        return Vector128.Min(
            Vector128.Max(
                scaled,
                Vector128.Create((int)short.MinValue)),
            Vector128.Create((int)short.MaxValue));
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
