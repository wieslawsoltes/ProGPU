using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ProGPU.Media.Audio;

/// <summary>
/// Allocation-free intrinsic-SIMD accumulation for normalized float samples
/// emitted by typed media effects.
/// </summary>
/// <remarks>
/// Independent lanes widen to double before level scaling, round away from
/// zero, clamp contributions to Int64, and add with saturating overflow. A
/// non-finite vector resumes at the scalar lane that preserves the established
/// validation and partial-write semantics. Only the bounded tail is scalar on
/// valid input.
/// </remarks>
internal static class MediaPcm16ProcessedAccumulator
{
    private const double MaximumConvertibleInt64 =
        9_223_372_036_854_774_784D;
    private const int SingleExponentMask = 0x7F80_0000;

    internal static void AddMono(
        ReadOnlySpan<float> source,
        int level,
        Span<long> destination,
        string nonFiniteMessage) =>
        Add(
            source,
            level,
            level,
            destination,
            nonFiniteMessage);

    internal static void AddStereo(
        ReadOnlySpan<float> source,
        int leftLevel,
        int rightLevel,
        Span<long> destination,
        string nonFiniteMessage) =>
        Add(
            source,
            leftLevel,
            rightLevel,
            destination,
            nonFiniteMessage);

    private static void Add(
        ReadOnlySpan<float> source,
        int leftLevel,
        int rightLevel,
        Span<long> destination,
        string nonFiniteMessage)
    {
        if (source.Length > destination.Length)
        {
            throw new ArgumentException(
                "The processed source does not fit in the accumulator span.",
                nameof(destination));
        }
        if (source.IsEmpty || leftLevel == 0 && rightLevel == 0)
        {
            return;
        }

        int index = 0;
        ref float sourceStart = ref MemoryMarshal.GetReference(source);
        ref long destinationStart = ref MemoryMarshal.GetReference(destination);
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<double> levels = Vector256.Create(
                (double)leftLevel,
                rightLevel,
                leftLevel,
                rightLevel);
            for (; index <= source.Length - Vector256<float>.Count;
                 index += Vector256<float>.Count)
            {
                Vector256<float> samples = Vector256.LoadUnsafe(
                    ref sourceStart,
                    (nuint)index);
                if (!AllFinite(samples))
                {
                    AddScalar(
                        source[index..],
                        leftLevel,
                        rightLevel,
                        destination[index..],
                        nonFiniteMessage,
                        index & 1);
                    return;
                }

                (Vector256<double> low, Vector256<double> high) =
                    Vector256.Widen(samples);
                AddScaled(
                    low * levels,
                    ref destinationStart,
                    index);
                AddScaled(
                    high * levels,
                    ref destinationStart,
                    index + Vector256<double>.Count);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<double> levels = Vector128.Create(
                (double)leftLevel,
                rightLevel);
            for (; index <= source.Length - Vector128<float>.Count;
                 index += Vector128<float>.Count)
            {
                Vector128<float> samples = Vector128.LoadUnsafe(
                    ref sourceStart,
                    (nuint)index);
                if (!AllFinite(samples))
                {
                    AddScalar(
                        source[index..],
                        leftLevel,
                        rightLevel,
                        destination[index..],
                        nonFiniteMessage,
                        index & 1);
                    return;
                }

                (Vector128<double> low, Vector128<double> high) =
                    Vector128.Widen(samples);
                AddScaled(
                    low * levels,
                    ref destinationStart,
                    index);
                AddScaled(
                    high * levels,
                    ref destinationStart,
                    index + Vector128<double>.Count);
            }
        }

        AddScalar(
            source[index..],
            leftLevel,
            rightLevel,
            destination[index..],
            nonFiniteMessage,
            index & 1);
    }

    private static bool AllFinite(Vector256<float> samples) =>
        !Vector256.EqualsAny(
            samples.AsInt32() & Vector256.Create(SingleExponentMask),
            Vector256.Create(SingleExponentMask));

    private static bool AllFinite(Vector128<float> samples) =>
        !Vector128.EqualsAny(
            samples.AsInt32() & Vector128.Create(SingleExponentMask),
            Vector128.Create(SingleExponentMask));

    private static void AddScaled(
        Vector256<double> scaled,
        ref long destination,
        int offset)
    {
        Vector256<long> contribution = ConvertSaturated(scaled);
        Vector256<long> accumulator = Vector256.LoadUnsafe(
            ref destination,
            (nuint)offset);
        SaturatingAdd(accumulator, contribution)
            .StoreUnsafe(ref destination, (nuint)offset);
    }

    private static void AddScaled(
        Vector128<double> scaled,
        ref long destination,
        int offset)
    {
        Vector128<long> contribution = ConvertSaturated(scaled);
        Vector128<long> accumulator = Vector128.LoadUnsafe(
            ref destination,
            (nuint)offset);
        SaturatingAdd(accumulator, contribution)
            .StoreUnsafe(ref destination, (nuint)offset);
    }

    private static Vector256<long> ConvertSaturated(
        Vector256<double> scaled)
    {
        Vector256<double> maximum = Vector256.Create((double)long.MaxValue);
        Vector256<double> minimum = Vector256.Create((double)long.MinValue);
        Vector256<double> above = Vector256.GreaterThanOrEqual(scaled, maximum);
        Vector256<double> below = Vector256.LessThanOrEqual(scaled, minimum);
        Vector256<double> safe = Vector256.Min(
            Vector256.Max(scaled, minimum),
            Vector256.Create(MaximumConvertibleInt64));
        Vector256<long> result = Vector256.ConvertToInt64(
            Vector256.Round(safe, MidpointRounding.AwayFromZero));
        result = Vector256.ConditionalSelect(
            above.AsInt64(),
            Vector256.Create(long.MaxValue),
            result);
        return Vector256.ConditionalSelect(
            below.AsInt64(),
            Vector256.Create(long.MinValue),
            result);
    }

    private static Vector128<long> ConvertSaturated(
        Vector128<double> scaled)
    {
        Vector128<double> maximum = Vector128.Create((double)long.MaxValue);
        Vector128<double> minimum = Vector128.Create((double)long.MinValue);
        Vector128<double> above = Vector128.GreaterThanOrEqual(scaled, maximum);
        Vector128<double> below = Vector128.LessThanOrEqual(scaled, minimum);
        Vector128<double> safe = Vector128.Min(
            Vector128.Max(scaled, minimum),
            Vector128.Create(MaximumConvertibleInt64));
        Vector128<long> result = Vector128.ConvertToInt64(
            Vector128.Round(safe, MidpointRounding.AwayFromZero));
        result = Vector128.ConditionalSelect(
            above.AsInt64(),
            Vector128.Create(long.MaxValue),
            result);
        return Vector128.ConditionalSelect(
            below.AsInt64(),
            Vector128.Create(long.MinValue),
            result);
    }

    private static Vector256<long> SaturatingAdd(
        Vector256<long> accumulator,
        Vector256<long> contribution)
    {
        Vector256<long> sum = accumulator + contribution;
        Vector256<long> zero = Vector256<long>.Zero;
        Vector256<long> positiveOverflow =
            Vector256.GreaterThan(contribution, zero) &
            Vector256.LessThan(sum, accumulator);
        Vector256<long> negativeOverflow =
            Vector256.LessThan(contribution, zero) &
            Vector256.GreaterThan(sum, accumulator);
        sum = Vector256.ConditionalSelect(
            positiveOverflow,
            Vector256.Create(long.MaxValue),
            sum);
        return Vector256.ConditionalSelect(
            negativeOverflow,
            Vector256.Create(long.MinValue),
            sum);
    }

    private static Vector128<long> SaturatingAdd(
        Vector128<long> accumulator,
        Vector128<long> contribution)
    {
        Vector128<long> sum = accumulator + contribution;
        Vector128<long> zero = Vector128<long>.Zero;
        Vector128<long> positiveOverflow =
            Vector128.GreaterThan(contribution, zero) &
            Vector128.LessThan(sum, accumulator);
        Vector128<long> negativeOverflow =
            Vector128.LessThan(contribution, zero) &
            Vector128.GreaterThan(sum, accumulator);
        sum = Vector128.ConditionalSelect(
            positiveOverflow,
            Vector128.Create(long.MaxValue),
            sum);
        return Vector128.ConditionalSelect(
            negativeOverflow,
            Vector128.Create(long.MinValue),
            sum);
    }

    private static void AddScalar(
        ReadOnlySpan<float> source,
        int leftLevel,
        int rightLevel,
        Span<long> destination,
        string nonFiniteMessage,
        int channel)
    {
        for (int index = 0; index < source.Length; index++)
        {
            float sample = source[index];
            if (!float.IsFinite(sample))
            {
                throw new InvalidDataException(nonFiniteMessage);
            }

            int level = channel == 0 ? leftLevel : rightLevel;
            double scaled = (double)sample * level;
            long contribution =
                scaled >= long.MaxValue
                    ? long.MaxValue
                    : scaled <= long.MinValue
                        ? long.MinValue
                        : checked((long)Math.Round(
                            scaled,
                            MidpointRounding.AwayFromZero));
            long accumulator = destination[index];
            if (contribution > 0 &&
                accumulator > long.MaxValue - contribution)
            {
                destination[index] = long.MaxValue;
            }
            else if (contribution < 0 &&
                     accumulator < long.MinValue - contribution)
            {
                destination[index] = long.MinValue;
            }
            else
            {
                destination[index] = accumulator + contribution;
            }
            channel ^= 1;
        }
    }
}
