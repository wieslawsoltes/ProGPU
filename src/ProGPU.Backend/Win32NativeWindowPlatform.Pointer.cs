namespace ProGPU.Backend;

internal sealed partial class Win32NativeWindowPlatform
{
    private NativeMouseButtonPacket? _currentMouseButtonPacket;

    public override NativeMouseButtonPacket? CurrentMouseButtonPacket => _currentMouseButtonPacket;

    internal static bool TryReadMouseButtonPacket(
        uint message, nint wParam, nint lParam, out NativeMouseButtonPacket packet)
    {
        packet = default;
        // Only messages that GLFW forwards as ordinary button down/up callbacks
        // carry this contract. Wheel/nonclient/move messages have separate frames.
        int button = message switch
        {
            0x0201 or 0x0202 => 0, // WM_LBUTTONDOWN / WM_LBUTTONUP
            0x0204 or 0x0205 => 1, // WM_RBUTTONDOWN / WM_RBUTTONUP
            0x0207 or 0x0208 => 2, // WM_MBUTTONDOWN / WM_MBUTTONUP
            0x020b or 0x020c => unchecked((ushort)((nuint)wParam >> 16)) switch
            {
                1 => 3,
                2 => 4,
                _ => -1
            },
            _ => -1
        };
        if (button < 0) return false;

        NativePointerEventKind kind = message is 0x0201 or 0x0204 or 0x0207 or 0x020b
            ? NativePointerEventKind.Down : NativePointerEventKind.Up;
        int x = unchecked((short)(nuint)lParam);
        int y = unchecked((short)((nuint)lParam >> 16));
        packet = new NativeMouseButtonPacket(kind, button, new NativeWindowPoint(x, y));
        return true;
    }

    internal static bool TryGetDesktopPointer(out NativeWindowPoint position)
    {
        if (GetCursorPos(out Point cursor) == 0)
        {
            position = default;
            return false;
        }

        position = new NativeWindowPoint(cursor.X, cursor.Y);
        return true;
    }
}
