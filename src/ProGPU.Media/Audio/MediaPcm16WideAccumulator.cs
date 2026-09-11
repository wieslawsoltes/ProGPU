using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ProGPU.Media.Audio;

/// <summary>
/// Allocation-free intrinsic-SIMD PCM16 scaling into a caller-owned wide
/// accumulator and final saturating PCM16 conversion.
/// </summary>
internal static class MediaPcm16WideAccumulator
{
    internal static void AddMono(
        ReadOnlySpan<short> source,
        int level,
        Span<long> destination) =>
        Add(source, level, level, destination);

    internal static void AddStereo(
        ReadOnlySpan<short> source,
        int leftLevel,
        int rightLevel,
        Span<long> destination) =>
        Add(source, leftLevel, rightLevel, destination);

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

        int index = 0;
        ref long sourceStart =
            ref MemoryMarshal.GetReference(source);
        ref short destinationStart =
            ref MemoryMarshal.GetReference(destination);
        if (Vector256.IsHardwareAccelerated)
        {
            for (; index <= source.Length - 16; index += 16)
            {
                Vector256<int> low = Vector256.Narrow(
                    ClampToPcm16(
                        Vector256.LoadUnsafe(
                            ref sourceStart,
                            (nuint)index)),
                    ClampToPcm16(
                        Vector256.LoadUnsafe(
                            ref sourceStart,
                            (nuint)(index + 4))));
                Vector256<int> high = Vector256.Narrow(
                    ClampToPcm16(
                        Vector256.LoadUnsafe(
                            ref sourceStart,
                            (nuint)(index + 8))),
                    ClampToPcm16(
                        Vector256.LoadUnsafe(
                            ref sourceStart,
                            (nuint)(index + 12))));
                Vector256.Narrow(low, high)
                    .StoreUnsafe(
                        ref destinationStart,
                        (nuint)index);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; index <= source.Length - 8; index += 8)
            {
                Vector128<int> low = Vector128.Narrow(
                    ClampToPcm16(
                        Vector128.LoadUnsafe(
                            ref sourceStart,
                            (nuint)index)),
                    ClampToPcm16(
                        Vector128.LoadUnsafe(
                            ref sourceStart,
                            (nuint)(index + 2))));
                Vector128<int> high = Vector128.Narrow(
                    ClampToPcm16(
                        Vector128.LoadUnsafe(
                            ref sourceStart,
                            (nuint)(index + 4))),
                    ClampToPcm16(
                        Vector128.LoadUnsafe(
                            ref sourceStart,
                            (nuint)(index + 6))));
                Vector128.Narrow(low, high)
                    .StoreUnsafe(
                        ref destinationStart,
                        (nuint)index);
            }
        }

        for (; index < source.Length; index++)
        {
            destination[index] =
                (short)Math.Clamp(
                    source[index],
                    short.MinValue,
                    short.MaxValue);
        }
    }

    private static void Add(
        ReadOnlySpan<short> source,
        int leftLevel,
        int rightLevel,
        Span<long> destination)
    {
        if (source.Length > destination.Length)
        {
            throw new ArgumentException(
                "The PCM16 source does not fit in the accumulator span.",
                nameof(destination));
        }
        if (source.IsEmpty ||
            leftLevel == 0 &&
            rightLevel == 0)
        {
            return;
        }

        int index = 0;
        ref short sourceStart =
            ref MemoryMarshal.GetReference(source);
        ref long destinationStart =
            ref MemoryMarshal.GetReference(destination);
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<int> levels = CreateLevels256(
                leftLevel,
                rightLevel);
            for (; index <= source.Length - 16; index += 16)
            {
                (Vector256<int> low, Vector256<int> high) =
                    Vector256.Widen(
                        Vector256.LoadUnsafe(
                            ref sourceStart,
                            (nuint)index));
                AddScaled(
                    Scale(low, levels),
                    ref destinationStart,
                    index);
                AddScaled(
                    Scale(high, levels),
                    ref destinationStart,
                    index + 8);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<int> levels = CreateLevels128(
                leftLevel,
                rightLevel);
            for (; index <= source.Length - 8; index += 8)
            {
                (Vector128<int> low, Vector128<int> high) =
                    Vector128.Widen(
                        Vector128.LoadUnsafe(
                            ref sourceStart,
                            (nuint)index));
                AddScaled(
                    Scale(low, levels),
                    ref destinationStart,
                    index);
                AddScaled(
                    Scale(high, levels),
                    ref destinationStart,
                    index + 4);
            }
        }

        int channel = index & 1;
        for (; index < source.Length; index++)
        {
            int level = channel == 0
                ? leftLevel
                : rightLevel;
            destination[index] +=
                (long)source[index] * level / 32_768;
            channel ^= 1;
        }
    }

    private static void AddScaled(
        Vector256<int> scaled,
        ref long destination,
        int offset)
    {
        (Vector256<long> low, Vector256<long> high) =
            Vector256.Widen(scaled);
        (Vector256.LoadUnsafe(
             ref destination,
             (nuint)offset) + low)
            .StoreUnsafe(
                ref destination,
                (nuint)offset);
        (Vector256.LoadUnsafe(
             ref destination,
             (nuint)(offset + 4)) + high)
            .StoreUnsafe(
                ref destination,
                (nuint)(offset + 4));
    }

    private static void AddScaled(
        Vector128<int> scaled,
        ref long destination,
        int offset)
    {
        (Vector128<long> low, Vector128<long> high) =
            Vector128.Widen(scaled);
        (Vector128.LoadUnsafe(
             ref destination,
             (nuint)offset) + low)
            .StoreUnsafe(
                ref destination,
                (nuint)offset);
        (Vector128.LoadUnsafe(
             ref destination,
             (nuint)(offset + 2)) + high)
            .StoreUnsafe(
                ref destination,
                (nuint)(offset + 2));
    }

    private static Vector256<int> Scale(
        Vector256<int> samples,
        Vector256<int> levels)
    {
        Vector256<int> products = samples * levels;
        return (products +
                ((products >> 31) &
                 Vector256.Create(32_767))) >> 15;
    }

    private static Vector128<int> Scale(
        Vector128<int> samples,
        Vector128<int> levels)
    {
        Vector128<int> products = samples * levels;
        return (products +
                ((products >> 31) &
                 Vector128.Create(32_767))) >> 15;
    }

    private static Vector256<int> CreateLevels256(
        int leftLevel,
        int rightLevel) =>
        Vector256.Create(
            leftLevel,
            rightLevel,
            leftLevel,
            rightLevel,
            leftLevel,
            rightLevel,
            leftLevel,
            rightLevel);

    private static Vector128<int> CreateLevels128(
        int leftLevel,
        int rightLevel) =>
        Vector128.Create(
            leftLevel,
            rightLevel,
            leftLevel,
            rightLevel);

    private static Vector256<long> ClampToPcm16(
        Vector256<long> value) =>
        Vector256.Min(
            Vector256.Max(
                value,
                Vector256.Create((long)short.MinValue)),
            Vector256.Create((long)short.MaxValue));

    private static Vector128<long> ClampToPcm16(
        Vector128<long> value) =>
        Vector128.Min(
            Vector128.Max(
                value,
                Vector128.Create((long)short.MinValue)),
            Vector128.Create((long)short.MaxValue));
}
