using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal sealed unsafe partial class X11NativeWindowPlatform
{
    internal static bool TryConfigurePopupOwner(nint display, nuint owner, nuint popup)
    {
        if (IntPtr.Size != 8 || display == 0 || owner == 0 || popup == 0 || owner == popup ||
            owner > uint.MaxValue || popup > uint.MaxValue)
            return false;
        // Borrowed live windows/display: their owning host must serialize this
        // operation with native destruction. Never replace the Xlib error handler.
        try
        {
            var operations = new PopupOperations(display, owner, popup);
            return X11PopupConfiguration.Apply(ref operations);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private struct PopupOperations(nint display, nuint owner, nuint popup) : IX11PopupOperations
    {
        private nuint _root, _type, _dropdown, _menu;
        private bool _overrideRedirect;

        public bool AdmitHiddenPair()
        {
            if (XGetWindowAttributes(display, owner, out var ownerState) == 0 ||
                XGetWindowAttributes(display, popup, out var popupState) == 0 ||
                ownerState.Class != 1 || popupState.Class != 1 || popupState.MapState != 0 ||
                ownerState.Root == 0 || ownerState.Root != popupState.Root ||
                (_root != 0 && _root != popupState.Root)) return false;
            int result = XQueryTree(display, popup, out nuint root, out nuint parent,
                out nuint* children, out _);
            try
            {
                if (result == 0 || root != popupState.Root || parent != root) return false;
            }
            finally { if (children != null) XFree(children); }
            _root = root;
            _overrideRedirect = popupState.OverrideRedirect != 0;
            return true;
        }

        public bool SetOwner()
        {
            _ = XSetTransientForHint(display, popup, owner);
            return XGetTransientForHint(display, popup, out nuint actualOwner) != 0 && actualOwner == owner;
        }

        public bool SetOverrideRedirect()
        {
            // Preserve source desktop placement; the WM must not reposition this
            // popup when it is mapped. Only this attribute is modified.
            var attributes = new PopupWindowAttributes { OverrideRedirect = 1 };
            _ = XChangeWindowAttributes(display, popup, (nuint)1 << 9, in attributes);
            return XGetWindowAttributes(display, popup, out var state) != 0 &&
                state.MapState == 0 && state.OverrideRedirect != 0;
        }

        public bool SetMenuTypes()
        {
            _type = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
            _dropdown = XInternAtom(display, "_NET_WM_WINDOW_TYPE_DROPDOWN_MENU", false);
            _menu = XInternAtom(display, "_NET_WM_WINDOW_TYPE_POPUP_MENU", false);
            if (_type == 0 || _dropdown == 0 || _menu == 0) return false;
            // Format-32 Xlib properties carry native-long elements on LP64.
            // Retain the existing popup type preference without managed repacking.
            nuint* types = stackalloc nuint[2] { _dropdown, _menu };
            _ = XChangeProperty(display, popup, _type, XaAtom, 32, PropModeReplace, (byte*)types, 2);
            return true; // Confirmation below consumes the ordered server reply.
        }

        public bool ConfirmHiddenConfiguration()
        {
            if (!AdmitHiddenPair() || !_overrideRedirect ||
                XGetTransientForHint(display, popup, out nuint actualOwner) == 0 || actualOwner != owner)
                return false;
            int result = XGetWindowProperty(display, popup, _type, 0, 2, 0, XaAtom,
                out nuint type, out int format, out nuint count, out nuint remaining, out byte* data);
            try
            {
                return result == 0 && type == XaAtom && format == 32 && count == 2 && remaining == 0 &&
                    data != null && ((nuint*)data)[0] == _dropdown && ((nuint*)data)[1] == _menu;
            }
            finally { if (data != null) XFree(data); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PopupWindowAttributes
    {
        public nuint BackgroundPixmap, BackgroundPixel, BorderPixmap, BorderPixel;
        public int BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nint EventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nuint Colormap, Cursor;
    }

    [LibraryImport(X11Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XQueryTree(nint display, nuint window, out nuint root,
        out nuint parent, out nuint* children, out uint count);
    [LibraryImport(X11Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XGetTransientForHint(nint display, nuint window, out nuint owner);
    [LibraryImport(X11Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int XChangeWindowAttributes(nint display, nuint window,
        nuint mask, in PopupWindowAttributes attributes);
}
