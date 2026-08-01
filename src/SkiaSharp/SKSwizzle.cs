using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;

namespace SkiaSharp;

public static class SKSwizzle
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void SwapRedBlue(IntPtr dest, IntPtr src, int count)
    {
        if (count <= 0)
            return;

        var byteCount = checked(count * 4);
        PixelChannelSwizzler.SwapRedBlue32(
            new ReadOnlySpan<byte>((void*)src, byteCount),
            new Span<byte>((void*)dest, byteCount),
            count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void SwapRedBlue(IntPtr pixels, int count)
    {
        if (count <= 0)
            return;

        var byteCount = checked(count * 4);
        PixelChannelSwizzler.SwapRedBlue32(
            new Span<byte>((void*)pixels, byteCount),
            count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SwapRedBlue(
        ReadOnlySpan<byte> dest,
        ReadOnlySpan<byte> src,
        int count) =>
        PixelChannelSwizzler.SwapRedBlue32(
            src,
            AsWritable(dest),
            count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SwapRedBlue(ReadOnlySpan<byte> pixels, int count) =>
        PixelChannelSwizzler.SwapRedBlue32(AsWritable(pixels), count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SwapRedBlue(Span<byte> pixels) =>
        PixelChannelSwizzler.SwapRedBlue32(pixels);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Span<byte> AsWritable(ReadOnlySpan<byte> value) =>
        MemoryMarshal.CreateSpan(
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(value)),
            value.Length);
}
