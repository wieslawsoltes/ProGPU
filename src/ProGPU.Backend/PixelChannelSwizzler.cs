using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

/// <summary>
/// Provides allocation-free channel transforms for tightly packed pixel data.
/// </summary>
public static class PixelChannelSwizzler
{
    // Every index is deliberately in [0, 15]. ShuffleNative can therefore map
    // one complete RGBA/BGRA vector with the hardware's native byte-table
    // instruction without the index normalization required by Shuffle.
    private static readonly Vector128<byte> s_redBlueShuffle = Vector128.Create(
        (byte)2, 1, 0, 3,
        6, 5, 4, 7,
        10, 9, 8, 11,
        14, 13, 12, 15);

    /// <summary>
    /// Swaps the first and third bytes of each complete four-byte pixel.
    /// </summary>
    /// <remarks>
    /// The operation is O(N) for N pixels, uses O(1) auxiliary storage, and
    /// preserves any incomplete trailing pixel bytes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SwapRedBlue32(Span<byte> pixels)
    {
        var byteCount = pixels.Length & ~3;
        if (byteCount == 0)
            return;

        SwapInPlace(pixels[..byteCount]);
    }

    /// <summary>
    /// Swaps up to <paramref name="pixelCount"/> four-byte pixels in place.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SwapRedBlue32(Span<byte> pixels, int pixelCount)
    {
        var byteCount = GetByteCount(pixelCount, pixels.Length);
        if (byteCount == 0)
            return;

        SwapInPlace(pixels[..byteCount]);
    }

    /// <summary>
    /// Copies and swaps up to <paramref name="pixelCount"/> four-byte pixels.
    /// Overlapping source and destination spans are supported.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SwapRedBlue32(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int pixelCount)
    {
        var byteCount = GetByteCount(
            pixelCount,
            Math.Min(source.Length, destination.Length));
        if (byteCount == 0)
            return;

        source = source[..byteCount];
        destination = destination[..byteCount];
        if (source.Overlaps(destination, out var destinationOffset) &&
            destinationOffset > 0)
        {
            SwapBackward(source, destination);
            return;
        }

        SwapForward(source, destination);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetByteCount(int pixelCount, int availableBytes)
    {
        if (pixelCount <= 0 || availableBytes < 4)
            return 0;

        return Math.Min(pixelCount, availableBytes >> 2) << 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SwapInPlace(Span<byte> pixels)
    {
        ref var start = ref MemoryMarshal.GetReference(pixels);
        var offset = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            var shuffle = s_redBlueShuffle;
            for (; offset <= pixels.Length - 64; offset += 64)
            {
                var first = Vector128.LoadUnsafe(ref start, (nuint)offset);
                var second = Vector128.LoadUnsafe(ref start, (nuint)(offset + 16));
                var third = Vector128.LoadUnsafe(ref start, (nuint)(offset + 32));
                var fourth = Vector128.LoadUnsafe(ref start, (nuint)(offset + 48));
                Vector128.ShuffleNative(first, shuffle).StoreUnsafe(ref start, (nuint)offset);
                Vector128.ShuffleNative(second, shuffle).StoreUnsafe(ref start, (nuint)(offset + 16));
                Vector128.ShuffleNative(third, shuffle).StoreUnsafe(ref start, (nuint)(offset + 32));
                Vector128.ShuffleNative(fourth, shuffle).StoreUnsafe(ref start, (nuint)(offset + 48));
            }

            for (; offset <= pixels.Length - Vector128<byte>.Count; offset += Vector128<byte>.Count)
            {
                var value = Vector128.LoadUnsafe(ref start, (nuint)offset);
                Vector128.ShuffleNative(value, shuffle).StoreUnsafe(ref start, (nuint)offset);
            }
        }

        for (; offset < pixels.Length; offset += 4)
        {
            ref var red = ref Unsafe.Add(ref start, offset);
            ref var blue = ref Unsafe.Add(ref start, offset + 2);
            (red, blue) = (blue, red);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SwapForward(
        ReadOnlySpan<byte> source,
        Span<byte> destination)
    {
        ref var sourceStart = ref MemoryMarshal.GetReference(source);
        ref var destinationStart = ref MemoryMarshal.GetReference(destination);
        var offset = 0;

        if (Vector128.IsHardwareAccelerated)
        {
            var shuffle = s_redBlueShuffle;
            for (; offset <= source.Length - 64; offset += 64)
            {
                var first = Vector128.LoadUnsafe(ref sourceStart, (nuint)offset);
                var second = Vector128.LoadUnsafe(ref sourceStart, (nuint)(offset + 16));
                var third = Vector128.LoadUnsafe(ref sourceStart, (nuint)(offset + 32));
                var fourth = Vector128.LoadUnsafe(ref sourceStart, (nuint)(offset + 48));
                Vector128.ShuffleNative(first, shuffle)
                    .StoreUnsafe(ref destinationStart, (nuint)offset);
                Vector128.ShuffleNative(second, shuffle)
                    .StoreUnsafe(ref destinationStart, (nuint)(offset + 16));
                Vector128.ShuffleNative(third, shuffle)
                    .StoreUnsafe(ref destinationStart, (nuint)(offset + 32));
                Vector128.ShuffleNative(fourth, shuffle)
                    .StoreUnsafe(ref destinationStart, (nuint)(offset + 48));
            }

            for (; offset <= source.Length - Vector128<byte>.Count; offset += Vector128<byte>.Count)
            {
                var pixels = Vector128.LoadUnsafe(ref sourceStart, (nuint)offset);
                Vector128.ShuffleNative(pixels, shuffle)
                    .StoreUnsafe(ref destinationStart, (nuint)offset);
            }
        }

        for (; offset < source.Length; offset += 4)
        {
            var red = Unsafe.Add(ref sourceStart, offset);
            var green = Unsafe.Add(ref sourceStart, offset + 1);
            var blue = Unsafe.Add(ref sourceStart, offset + 2);
            var alpha = Unsafe.Add(ref sourceStart, offset + 3);
            Unsafe.Add(ref destinationStart, offset) = blue;
            Unsafe.Add(ref destinationStart, offset + 1) = green;
            Unsafe.Add(ref destinationStart, offset + 2) = red;
            Unsafe.Add(ref destinationStart, offset + 3) = alpha;
        }
    }

    private static void SwapBackward(
        ReadOnlySpan<byte> source,
        Span<byte> destination)
    {
        ref var sourceStart = ref MemoryMarshal.GetReference(source);
        ref var destinationStart = ref MemoryMarshal.GetReference(destination);
        for (var offset = source.Length - 4; offset >= 0; offset -= 4)
        {
            var red = Unsafe.Add(ref sourceStart, offset);
            var green = Unsafe.Add(ref sourceStart, offset + 1);
            var blue = Unsafe.Add(ref sourceStart, offset + 2);
            var alpha = Unsafe.Add(ref sourceStart, offset + 3);
            Unsafe.Add(ref destinationStart, offset) = blue;
            Unsafe.Add(ref destinationStart, offset + 1) = green;
            Unsafe.Add(ref destinationStart, offset + 2) = red;
            Unsafe.Add(ref destinationStart, offset + 3) = alpha;
        }
    }
}
