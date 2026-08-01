using System.Runtime.InteropServices;
using ProGPU.Backend;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkSwizzleCompatibilityTests
{
    [Fact]
    public void CoreInPlaceSwizzleUsesSimdSafeCompletePixelsAndPreservesTail()
    {
        byte[] pixels = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        PixelChannelSwizzler.SwapRedBlue32(pixels);

        Assert.Equal(new byte[] { 3, 2, 1, 4, 7, 6, 5, 8, 9, 10 }, pixels);
        PixelChannelSwizzler.SwapRedBlue32(pixels);
        Assert.Equal(Enumerable.Range(1, 10).Select(static value => (byte)value), pixels);
    }

    [Fact]
    public void CoreCopySwizzleClampsCountAndSupportsOverlap()
    {
        byte[] source = [1, 2, 3, 4, 5, 6, 7, 8];
        var destination = Enumerable.Repeat((byte)0xcc, 12).ToArray();

        PixelChannelSwizzler.SwapRedBlue32(source, destination, 3);
        Assert.Equal(
            new byte[] { 3, 2, 1, 4, 7, 6, 5, 8, 0xcc, 0xcc, 0xcc, 0xcc },
            destination);

        byte[] overlapping = [1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0];
        PixelChannelSwizzler.SwapRedBlue32(
            overlapping.AsSpan(0, 8),
            overlapping.AsSpan(4, 8),
            2);
        Assert.Equal(
            new byte[] { 1, 2, 3, 4, 3, 2, 1, 4, 7, 6, 5, 8 },
            overlapping);
    }

    [Fact]
    public void SpanAndReadOnlySpanOverloadsMatchNativeValidBufferBehavior()
    {
        byte[] inPlace = [1, 2, 3, 4, 5, 6, 7, 8];
        SKSwizzle.SwapRedBlue(inPlace.AsSpan());
        Assert.Equal(new byte[] { 3, 2, 1, 4, 7, 6, 5, 8 }, inPlace);

        SKSwizzle.SwapRedBlue((ReadOnlySpan<byte>)inPlace, 1);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 7, 6, 5, 8 }, inPlace);
        SKSwizzle.SwapRedBlue((ReadOnlySpan<byte>)inPlace, -1);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 7, 6, 5, 8 }, inPlace);

        byte[] source = [9, 10, 11, 12, 13, 14, 15, 16];
        byte[] destination = new byte[8];
        SKSwizzle.SwapRedBlue(
            (ReadOnlySpan<byte>)destination,
            (ReadOnlySpan<byte>)source,
            2);
        Assert.Equal(new byte[] { 11, 10, 9, 12, 15, 14, 13, 16 }, destination);
    }

    [Fact]
    public unsafe void PointerOverloadsMatchSpanOverloads()
    {
        byte[] source = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] destination = new byte[8];
        fixed (byte* sourcePointer = source)
        fixed (byte* destinationPointer = destination)
        {
            SKSwizzle.SwapRedBlue(
                (IntPtr)destinationPointer,
                (IntPtr)sourcePointer,
                2);
            SKSwizzle.SwapRedBlue((IntPtr)destinationPointer, 2);
        }

        Assert.Equal(source, destination);
        SKSwizzle.SwapRedBlue(IntPtr.Zero, 0);
        SKSwizzle.SwapRedBlue(IntPtr.Zero, IntPtr.Zero, -1);
    }

    [Fact]
    public void StableCoreSwizzleAllocatesNothing()
    {
        var pixels = new byte[4096];
        PixelChannelSwizzler.SwapRedBlue32(pixels);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
            PixelChannelSwizzler.SwapRedBlue32(pixels);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}
