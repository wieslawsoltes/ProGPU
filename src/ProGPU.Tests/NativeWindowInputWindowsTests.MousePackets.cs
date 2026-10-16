using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Xunit;

namespace ProGPU.Tests;

public partial class NativeWindowInputWindowsTests
{
    [WindowsFact]
    public void OriginalButtonPacketSurvivesNestedCallbacksWithoutLeakingIntoKeyboardOrLaterQueries()
    {
        var options = WindowOptions.Default;
        options.API = GraphicsAPI.None;
        options.IsVisible = false;
        using var window = Window.Create(options);
        window.Initialize();
        using var controller = new SilkWindowController(window);
        Assert.True(controller.Attach());
        using var input = NativeWindowInput.CreateInput(window);
        var mouse = Assert.Single(input.Mice);
        var keyboard = Assert.Single(input.Keyboards);
        nint handle = controller.Handle.Handle;
        var packets = new List<NativeMouseButtonPacket?>();
        NativeMouseButtonPacket? Read() => controller.TryGetCurrentMouseButtonPacket(out var packet) ? packet : null;
        bool? keyboardHasPacket = null;
        bool? wrongThreadHasPacket = null;
        bool nested = false;
        mouse.MouseDown += (device, button) =>
        {
            packets.Add(Read());
            if (button != MouseButton.Left || nested) return;
            nested = true;
            wrongThreadHasPacket = Task.Run(() => controller.TryGetCurrentMouseButtonPacket(out _)).GetAwaiter().GetResult();
            SendMousePacket(handle, 0x0204, 0, PackMouseCoordinates(-17, 29));
            packets.Add(Read());
            SendMousePacket(handle, 0x0205, 0, PackMouseCoordinates(31, -43));
            packets.Add(Read());
            SendMousePacket(handle, 0x0100, 0x41, 0x001e0001); // ordinary A key callback
            packets.Add(Read());
            SendMousePacket(handle, 0x0101, 0x41, unchecked((nint)0xc01e0001));
        };
        mouse.MouseUp += (_, _) => packets.Add(Read());
        keyboard.KeyDown += (_, _, _) => keyboardHasPacket = controller.TryGetCurrentMouseButtonPacket(out _);

        Assert.Null(Read());
        SendMousePacket(handle, 0x0201, 0, PackMouseCoordinates(-32768, 32767));
        Assert.Null(Read());
        SendMousePacket(handle, 0x0202, 0, PackMouseCoordinates(110, 153));
        Assert.Null(Read());
        NativeMouseButtonPacket outer = new(NativePointerEventKind.Down, 0, new(-32768, 32767));
        Assert.Equal(new NativeMouseButtonPacket?[]
        {
            outer, new(NativePointerEventKind.Down, 1, new(-17, 29)), outer,
            new(NativePointerEventKind.Up, 1, new(31, -43)), outer, outer,
            new(NativePointerEventKind.Up, 0, new(110, 153))
        }, packets);
        Assert.False(keyboardHasPacket);
        Assert.False(wrongThreadHasPacket);
    }

    private static nint PackMouseCoordinates(int x, int y) =>
        unchecked((nint)(((uint)(ushort)y << 16) | (ushort)x));

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMousePacket(nint window, uint message, nint wParam, nint lParam);
}
