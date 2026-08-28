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
            for (; index <= source.Length - Vector256<short>.Count;
                 index += Vector256<short>.Count)
            {
                Vector256<short> samples = Vector256.LoadUnsafe(
                    ref sourceStart,
                    (nuint)index);
                (Vector256<int> low, Vector256<int> high) =
                    Vector256.Widen(samples);
                (Vector256.ConvertToSingle(low) * scale)
                    .StoreUnsafe(
                        ref destinationStart,
                        (nuint)index);
                (Vector256.ConvertToSingle(high) * scale)
                    .StoreUnsafe(
                        ref destinationStart,
                        (nuint)(index + Vector256<int>.Count));
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            Vector128<float> scale = Vector128.Create(Scale);
            for (; index <= source.Length - Vector128<short>.Count;
                 index += Vector128<short>.Count)
            {
                Vector128<short> samples = Vector128.LoadUnsafe(
                    ref sourceStart,
                    (nuint)index);
                (Vector128<int> low, Vector128<int> high) =
                    Vector128.Widen(samples);
                (Vector128.ConvertToSingle(low) * scale)
                    .StoreUnsafe(
                        ref destinationStart,
                        (nuint)index);
                (Vector128.ConvertToSingle(high) * scale)
                    .StoreUnsafe(
                        ref destinationStart,
                        (nuint)(index + Vector128<int>.Count));
            }
        }

        for (; index < source.Length; index++)
        {
            destination[index] = source[index] / 32_768F;
        }
    }
}
