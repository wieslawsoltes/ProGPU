using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal static unsafe partial class X11NativeSystemMenu
{
    private const string Xlib = "libX11.so.6";
    private const string Xinput = "libXi.so.6";
    private const int MaximumSupportedAtoms = 4096;

    internal static bool TryShow(NativeWindowHandle owner, NativeWindowPoint position)
    {
        var api = new Operations();
        try { return X11SystemMenu.Show(owner, position, ref api); }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private readonly struct Operations : IX11SystemMenuOperations
    {
        public bool TryGetTarget(NativeWindowHandle owner, out nuint root, out nuint message)
        {
            root = message = 0;
            // Caller owns a live window/display on its host thread. This query
            // also orders pending owner-window creation before the second connection.
            if (XGetGeometry(owner.Display, unchecked((nuint)owner.Handle), out root,
                out _, out _, out _, out _, out _, out _) == 0 || root == 0)
                return false;

            // A WM-managed client has WM_STATE; child/override-redirect/withdrawn
            // windows must not submit a titlebar-menu request as another owner.
            nuint stateAtom = Atom(owner.Display, "WM_STATE\0"u8);
            Span<nuint> state = stackalloc nuint[2];
            if (stateAtom == 0 || !Read32(owner.Display, unchecked((nuint)owner.Handle),
                stateAtom, stateAtom, state, out int stateCount) || stateCount != 2 ||
                (state[0] != 1 && state[0] != 3))
                return false;

            nuint supported = Atom(owner.Display, "_NET_SUPPORTED\0"u8);
            message = Atom(owner.Display, "_GTK_SHOW_WINDOW_MENU\0"u8);
            if (supported == 0 || message == 0) return false;
            Span<nuint> atoms = stackalloc nuint[MaximumSupportedAtoms];
            return Read32(owner.Display, root, supported, 4 /* XA_ATOM */, atoms, out int count) &&
                atoms[..count].Contains(message);
        }

        public nint OpenInputConnection(nint ownerDisplay)
        {
            byte* name = XDisplayString(ownerDisplay);
            return name == null ? 0 : XOpenDisplay(name);
        }

        public bool TryGetClientPointer(nint connection, nuint owner, out int device)
        {
            device = 0;
            fixed (byte* extension = "XInputExtension\0"u8)
                if (XQueryExtension(connection, extension, out _, out _, out _) == 0) return false;
            int major = 2, minor = 0;
            // XIQueryVersion changes the client's input protocol. Negotiate only
            // on our short-lived connection, never GLFW/Silk.NET's event connection.
            return XIQueryVersion(connection, ref major, ref minor) == 0 && major >= 2 &&
                XIGetClientPointer(connection, owner, out device) != 0;
        }

        public bool Send(nint connection, nuint root, in X11MenuMessage message)
        {
            Span<nint> storage = stackalloc nint[X11SystemMenu.EventStorageWords];
            storage.Clear();
            fixed (nint* data = storage)
            {
                *(X11MenuMessage*)data = message;
                if (XSendEvent(connection, root, 0, X11SystemMenu.RootEventMask, data) == 0)
                    return false;
            }
            XFlush(connection);
            return true; // Asynchronous request submitted, NOT proof of menu display.
        }

        public void CloseInputConnection(nint connection) => XCloseDisplay(connection);
    }

    private static nuint Atom(nint display, ReadOnlySpan<byte> name)
    {
        fixed (byte* value = name) return XInternAtom(display, value, 1);
    }

    private static bool Read32(nint display, nuint window, nuint property, nuint expectedType,
        Span<nuint> destination, out int count)
    {
        count = 0;
        int status = XGetWindowProperty(display, window, property, 0, destination.Length, 0,
            expectedType, out nuint type, out int format, out nuint items, out nuint remaining, out nint data);
        try
        {
            // Xlib expands format-32 property items into native unsigned-long slots.
            return X11SystemMenu.CopyProperty32(status, expectedType, type, format, items,
                remaining, data, destination, out count);
        }
        finally { if (data != 0) XFree(data); }
    }

    [LibraryImport(Xlib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XGetGeometry(nint display, nuint drawable, out nuint root,
        out int x, out int y, out uint width, out uint height, out uint border, out uint depth);

    [LibraryImport(Xlib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XGetWindowProperty(nint display, nuint window, nuint property,
        nint offset, nint length, int delete, nuint requestedType, out nuint type,
        out int format, out nuint items, out nuint remaining, out nint data);

    [LibraryImport(Xlib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nuint XInternAtom(nint display, byte* name, int onlyIfExists);

    [LibraryImport(Xlib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte* XDisplayString(nint display);

    [LibraryImport(Xlib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint XOpenDisplay(byte* name);

    [LibraryImport(Xlib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport(Xlib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XFree(nint data);

    [LibraryImport(Xlib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XQueryExtension(nint display, byte* name, out int opcode, out int firstEvent, out int firstError);

    [LibraryImport(Xinput)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XIQueryVersion(nint display, ref int major, ref int minor);

    [LibraryImport(Xinput)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XIGetClientPointer(nint display, nuint window, out int device);

    [LibraryImport(Xlib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XSendEvent(nint display, nuint window, int propagate, nint eventMask, nint* data);

    [LibraryImport(Xlib)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XFlush(nint display);
}
