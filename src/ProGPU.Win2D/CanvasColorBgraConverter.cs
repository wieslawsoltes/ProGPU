using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Windows.UI;

namespace Microsoft.Graphics.Canvas;

/// <summary>
/// Allocation-free Win2D-compatible ARGB Color to BGRA8 conversion.
/// </summary>
/// <remarks>
/// Upstream Win2D reorders channels but does not premultiply them. The SIMD
/// lanes perform the same four-byte reversal per pixel. Only a tail of at most
/// three pixels is scalar on Vector128 hardware, or seven after AVX2.
/// </remarks>
internal static class CanvasColorBgraConverter
{
    private static readonly Vector128<byte> ShuffleMask128 = Vector128.Create(
        (byte)3, 2, 1, 0,
        7, 6, 5, 4,
        11, 10, 9, 8,
        15, 14, 13, 12);

    private static readonly Vector256<byte> ShuffleMask256 = Vector256.Create(
        ShuffleMask128,
        ShuffleMask128);

    internal static ProGpuCanvasCpuConversionPath Convert(
        ReadOnlySpan<Color> source,
        Span<byte> destination,
        ProGpuCanvasCpuConversionMode mode)
    {
        int requiredBytes = checked(source.Length * 4);
        if (destination.Length < requiredBytes)
        {
            throw new ArgumentException(
                "The BGRA8 destination is smaller than the Color source.",
                nameof(destination));
        }
        if (mode is < ProGpuCanvasCpuConversionMode.Automatic or
            > ProGpuCanvasCpuConversionMode.ScalarReference)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        int pixelIndex = 0;
        if (mode != ProGpuCanvasCpuConversionMode.ScalarReference)
        {
            ReadOnlySpan<byte> sourceBytes = MemoryMarshal.AsBytes(source);
            ref byte sourceStart = ref MemoryMarshal.GetReference(sourceBytes);
            ref byte destinationStart = ref MemoryMarshal.GetReference(destination);

            if (Avx2.IsSupported && source.Length >= 8)
            {
                const int PixelsPerVector = 8;
                for (; pixelIndex <= source.Length - PixelsPerVector;
                     pixelIndex += PixelsPerVector)
                {
                    int byteOffset = pixelIndex * 4;
                    Avx2.Shuffle(
                            Vector256.LoadUnsafe(
                                ref sourceStart,
                                (nuint)byteOffset),
                            ShuffleMask256)
                        .StoreUnsafe(
                            ref destinationStart,
                            (nuint)byteOffset);
                }

                ConvertScalar(source, destination, pixelIndex);
                return ProGpuCanvasCpuConversionPath.Vector256;
            }

            if (Vector128.IsHardwareAccelerated &&
                (source.Length >= 4 ||
                 mode == ProGpuCanvasCpuConversionMode.IntrinsicSimd))
            {
                const int PixelsPerVector = 4;
                for (; pixelIndex <= source.Length - PixelsPerVector;
                     pixelIndex += PixelsPerVector)
                {
                    int byteOffset = pixelIndex * 4;
                    Vector128.Shuffle(
                            Vector128.LoadUnsafe(
                                ref sourceStart,
                                (nuint)byteOffset),
                            ShuffleMask128)
                        .StoreUnsafe(
                            ref destinationStart,
                            (nuint)byteOffset);
                }

                ConvertScalar(source, destination, pixelIndex);
                return ProGpuCanvasCpuConversionPath.Vector128;
            }

            if (mode == ProGpuCanvasCpuConversionMode.IntrinsicSimd)
            {
                throw new PlatformNotSupportedException(
                    "Forced Canvas intrinsic SIMD conversion requires Vector128 hardware acceleration.");
            }
        }

        ConvertScalar(source, destination, pixelIndex);
        return ProGpuCanvasCpuConversionPath.ScalarReference;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertScalar(
        ReadOnlySpan<Color> source,
        Span<byte> destination,
        int pixelIndex)
    {
        for (; pixelIndex < source.Length; pixelIndex++)
        {
            Color color = source[pixelIndex];
            int byteOffset = pixelIndex * 4;
            destination[byteOffset] = color.B;
            destination[byteOffset + 1] = color.G;
            destination[byteOffset + 2] = color.R;
            destination[byteOffset + 3] = color.A;
        }
    }
}
