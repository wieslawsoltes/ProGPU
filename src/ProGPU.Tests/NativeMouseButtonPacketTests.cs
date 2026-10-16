using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeMouseButtonPacketTests
{
    [Theory]
    [InlineData(0x201, 0, NativePointerEventKind.Down, 0)]
    [InlineData(0x202, 0, NativePointerEventKind.Up, 0)]
    [InlineData(0x204, 0, NativePointerEventKind.Down, 1)]
    [InlineData(0x205, 0, NativePointerEventKind.Up, 1)]
    [InlineData(0x207, 0, NativePointerEventKind.Down, 2)]
    [InlineData(0x208, 0, NativePointerEventKind.Up, 2)]
    [InlineData(0x20b, 1, NativePointerEventKind.Down, 3)]
    [InlineData(0x20c, 1, NativePointerEventKind.Up, 3)]
    [InlineData(0x20b, 2, NativePointerEventKind.Down, 4)]
    [InlineData(0x20c, 2, NativePointerEventKind.Up, 4)]
    public void EachButtonRetainsOriginalSignedClientCoordinates(
        uint message, int extraButton, NativePointerEventKind kind, int button)
    {
        foreach (var point in new[] { (0, 0), (110, 153), (-41, -73), (-32768, 32767), (32767, -32768) })
        {
            nint coordinates = unchecked((nint)(((uint)(ushort)point.Item2 << 16) | (ushort)point.Item1));
            nint flags = (extraButton << 16) | 0x7f;
            Assert.True(Win32NativeWindowPlatform.TryReadMouseButtonPacket(message, flags, coordinates, out var packet));
            Assert.Equal(new NativeMouseButtonPacket(kind, button, new(point.Item1, point.Item2)), packet);
        }
    }

    [Theory]
    [InlineData(0x200, 0)] // move
    [InlineData(0x20a, 0)] // vertical wheel: screen coordinates
    [InlineData(0x20e, 0)] // horizontal wheel
    [InlineData(0xa1, 0)] // nonclient down: screen coordinates
    [InlineData(0x203, 0)] // separate double-click message, not GLFW button delivery
    [InlineData(0x206, 0)]
    [InlineData(0x209, 0)]
    [InlineData(0x20d, 1)]
    [InlineData(0x20b, 0)]
    [InlineData(0x20c, 3)]
    [InlineData(0x100, 0)] // keyboard
    [InlineData(0x240, 0)] // touch
    public void OtherFramesAndInvalidButtonIdentitiesStayUnavailable(uint message, int extraButton)
    {
        Assert.False(Win32NativeWindowPlatform.TryReadMouseButtonPacket(
            message, extraButton << 16, unchecked((nint)0x89abcdef), out var packet));
        Assert.Equal(default, packet);
    }
}
