using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal interface IX11SystemMenuOperations
{
    bool TryGetTarget(NativeWindowHandle owner, out nuint root, out nuint message);
    nint OpenInputConnection(nint ownerDisplay);
    bool TryGetClientPointer(nint connection, nuint owner, out int device);
    bool Send(nint connection, nuint root, in X11MenuMessage message);
    void CloseInputConnection(nint connection);
}

// Xlib's C long is pointer-sized on both Linux ILP32 and LP64. This is the
// ClientMessage prefix of XEvent, NOT a packed 32-byte X11 wire event.
[StructLayout(LayoutKind.Sequential)]
internal struct X11MenuMessage
{
    public int Type;
    public nuint Serial;
    public int SendEvent;
    public nint Display;
    public nuint Window;
    public nuint MessageType;
    public int Format;
    public nint Device, X, Y, Reserved0, Reserved1;
}

internal static class X11SystemMenu
{
    internal const int EventStorageWords = 24;
    internal const int RootEventMask = (1 << 19) | (1 << 20);

    internal static unsafe bool CopyProperty32(int status, nuint expectedType, nuint actualType,
        int format, nuint items, nuint remaining, nint data, Span<nuint> destination, out int count)
    {
        count = 0;
        if (status != 0 || expectedType == 0 || actualType != expectedType || format != 32 || remaining != 0 ||
            items > (nuint)destination.Length || (items != 0 && data == 0)) return false;
        new ReadOnlySpan<nuint>((void*)data, (int)items).CopyTo(destination);
        count = (int)items;
        return true;
    }

    // Bounded user-action control flow, not a rendering/CPU-compute fallback.
    internal static bool Show<T>(NativeWindowHandle owner, NativeWindowPoint position, ref T api)
        where T : IX11SystemMenuOperations
    {
        if (owner.Kind != NativeWindowKind.X11 || owner.Display == 0 || !owner.IsValid ||
            unchecked((nuint)owner.Handle) > uint.MaxValue ||
            !api.TryGetTarget(owner, out nuint root, out nuint message) || root == 0 || message == 0)
            return false;

        nint connection = api.OpenInputConnection(owner.Display);
        if (connection == 0) return false;
        try
        {
            if (!api.TryGetClientPointer(connection, unchecked((nuint)owner.Handle), out int device) || device <= 0)
                return false;
            var request = new X11MenuMessage
            {
                Type = 33, Display = connection, Window = unchecked((nuint)owner.Handle),
                MessageType = message, Format = 32,
                Device = device, X = position.X, Y = position.Y
            };
            return api.Send(connection, root, in request);
        }
        finally
        {
            api.CloseInputConnection(connection);
        }
    }
}
