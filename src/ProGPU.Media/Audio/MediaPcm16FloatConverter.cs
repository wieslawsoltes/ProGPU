using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ProGPU.Media.Audio;

/// <summary>
/// Allocation-free intrinsic-SIMD conversion from signed PCM16 samples to
/// normalized floating-point samples.
/// </summary>
/// <remarks>
/// PCM16 normalization is an exact power-of-two scale. Widening every Int16
/// lane to Int32 before converting to Single therefore remains bit-identical
/// to the scalar <c>sample / 32768f</c> oracle. Only the bounded tail is
/// scalar on supported hardware.
/// </remarks>
internal static class MediaPcm16FloatConverter
{
    private const float Scale = 1F / 32_768F;

    internal static void ConvertToNormalizedFloat(
        ReadOnlySpan<short> source,
        Span<float> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException(
                "The float destination is smaller than the PCM16 source.",
                nameof(destination));
        }

        int index = 0;
        ref short sourceStart = ref MemoryMarshal.GetReference(source);
        ref float destinationStart = ref MemoryMarshal.GetReference(destination);
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> scale = Vector256.Create(Scale);
            int unrolledCount = Vector256<short>.Count * 2;
            for (; index <= source.Length - unrolledCount;
                 index += unrolledCount)
            {
                ConvertVector256(
                    ref sourceStart,
                    ref destinationStart,
                    index,
                    scale);
                ConvertVector256(
                    ref sourceStart,
                    ref destinationStart,
                    index + Vector256<short>.Count,
                    scale);
            }
            for (; index <= source.Length - Vector256<short>.Count;
                 index += Vector256<short>.Count)
            {
                ConvertVector256(
                    ref sourceStart,
                    ref destinationStart,
                    index,
                    scale);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<float> scale = Vector128.Create(Scale);
            int unrolledCount = Vector128<short>.Count * 2;
            for (; index <= source.Length - unrolledCount;
                 index += unrolledCount)
            {
                ConvertVector128(
                    ref sourceStart,
                    ref destinationStart,
                    index,
                    scale);
                ConvertVector128(
                    ref sourceStart,
                    ref destinationStart,
                    index + Vector128<short>.Count,
                    scale);
            }
            for (; index <= source.Length - Vector128<short>.Count;
                 index += Vector128<short>.Count)
            {
                ConvertVector128(
                    ref sourceStart,
                    ref destinationStart,
                    index,
                    scale);
            }
        }

        for (; index < source.Length; index++)
        {
            destination[index] = source[index] / 32_768F;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertVector256(
        ref short source,
        ref float destination,
        int index,
        Vector256<float> scale)
    {
        Vector256<short> samples = Vector256.LoadUnsafe(
            ref source,
            (nuint)index);
        (Vector256<int> low, Vector256<int> high) =
            Vector256.Widen(samples);
        (Vector256.ConvertToSingle(low) * scale)
            .StoreUnsafe(
                ref destination,
                (nuint)index);
        (Vector256.ConvertToSingle(high) * scale)
            .StoreUnsafe(
                ref destination,
                (nuint)(index + Vector256<int>.Count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertVector128(
        ref short source,
        ref float destination,
        int index,
        Vector128<float> scale)
    {
        Vector128<short> samples = Vector128.LoadUnsafe(
            ref source,
            (nuint)index);
        (Vector128<int> low, Vector128<int> high) =
            Vector128.Widen(samples);
        (Vector128.ConvertToSingle(low) * scale)
            .StoreUnsafe(
                ref destination,
                (nuint)index);
        (Vector128.ConvertToSingle(high) * scale)
            .StoreUnsafe(
                ref destination,
                (nuint)(index + Vector128<int>.Count));
    }
}
