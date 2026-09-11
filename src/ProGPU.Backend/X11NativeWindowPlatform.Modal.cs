using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal sealed unsafe partial class X11NativeWindowPlatform
{
    private const int MaximumSupportedAtoms = 4096;
    private const int MaximumStateAtoms = 64;
    private nuint _modalStateAtom;
    private nuint _modalAtom;
    private NativeModalHintSubmission _modalSubmission;

    bool INativeWindowModalHintOperations.TryRead(out bool isModal)
    {
        isModal = false;
        // The current XEvent transport supports the shipped LP64 Linux RIDs.
        if (IntPtr.Size != 8 || _display == 0 || _window == 0 ||
            XGetWindowAttributes(_display, _window, out var attributes) == 0 ||
            attributes.OverrideRedirect != 0 || attributes.Root == 0) return false;
        nuint supported = XInternAtom(_display, "_NET_SUPPORTED", true);
        _modalStateAtom = XInternAtom(_display, "_NET_WM_STATE", true);
        _modalAtom = XInternAtom(_display, "_NET_WM_STATE_MODAL", true);
        if (supported == 0 || _modalStateAtom == 0 || _modalAtom == 0) return false;
        if (!TryReadAtoms(attributes.Root, supported, MaximumSupportedAtoms, false,
            out var data, out int count)) return false;
        try
        {
            // Xlib format-32 properties are native-long arrays, not packed uints.
            // Span search uses runtime-intrinsic SIMD without repacking the data.
            var atoms = new ReadOnlySpan<ulong>(data, count);
            if (!atoms.Contains((ulong)_modalStateAtom) || !atoms.Contains((ulong)_modalAtom))
                return false;
        }
        finally { if (data != null) XFree(data); }
        if (!TryReadAtoms(_window, _modalStateAtom, MaximumStateAtoms, true,
            out data, out count)) return false;
        try { isModal = _modalSubmission.Observe(new ReadOnlySpan<ulong>(data, count).Contains((ulong)_modalAtom)); }
        finally { if (data != null) XFree(data); }
        return true;
    }

    bool INativeWindowModalHintOperations.TrySet(bool modal)
    {
        if (XGetWindowAttributes(_display, _window, out var attributes) == 0 ||
            attributes.OverrideRedirect != 0 || attributes.Root == 0) return false;
        if (attributes.MapState == 0)
        {
            // Withdrawn windows publish their initial property directly. Merge
            // only our bit with the current set, never restore a stale snapshot
            // over concurrently changed topmost/maximized/taskbar state.
            if (!TryReadAtoms(_window, _modalStateAtom, MaximumStateAtoms, true,
                out var data, out int count)) return false;
            try
            {
                Span<nuint> state = stackalloc nuint[MaximumStateAtoms];
                var current = new ReadOnlySpan<nuint>(data, count);
                int used = 0;
                // Bounded protocol record compaction, once per dialog boundary.
                for (int i = 0; i < current.Length; i++)
                    if (current[i] != _modalAtom) state[used++] = current[i];
                if (modal)
                {
                    if (used == state.Length) return false;
                    state[used++] = _modalAtom;
                }
                fixed (nuint* values = state)
                    XChangeProperty(_display, _window, _modalStateAtom, XaAtom, 32,
                        PropModeReplace, (byte*)values, used);
            }
            finally { if (data != null) XFree(data); }
        }

        // Also send after an unmapped update: a previously queued MapRequest may
        // already be in the WM's queue. Mapped windows are changed only by the
        // WM. Submission is not acknowledgment that the WM enforces modality.
        long* message = stackalloc long[5] { modal ? 1 : 0, (long)_modalAtom, 0, 1, 0 };
        if (!SendClientMessage(_modalStateAtom, message, attributes.Root)) return false;
        // Direct property writes are ordered on this display connection. Only
        // mapped-state requests need a pending logical value until WM processing.
        _modalSubmission.Submitted(modal, awaitWindowManager: attributes.MapState != 0);
        return true;
    }

    private bool TryReadAtoms(nuint window, nuint property, int maximum, bool allowMissing,
        out byte* data, out int count)
    {
        int result = XGetWindowProperty(_display, window, property, 0, maximum, 0,
            XaAtom, out nuint type, out int format, out nuint items, out nuint remaining, out data);
        count = 0;
        bool missing = type == 0 && format == 0 && items == 0 && remaining == 0;
        bool valid = result == 0 && remaining == 0 && items <= (nuint)maximum &&
            ((allowMissing && missing) || (type == XaAtom && format == 32 && (items == 0 || data != null)));
        if (!valid)
        {
            if (data != null) XFree(data);
            data = null;
            return false;
        }
        count = (int)items;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XWindowAttributes
    {
        public int X, Y, Width, Height, BorderWidth, Depth;
        public nint Visual;
        public nuint Root;
        public int Class, BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nuint Colormap;
        public int MapInstalled, MapState;
        public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Screen;
    }

    [LibraryImport(X11Library)]
    private static partial int XGetWindowAttributes(nint display, nuint window, out XWindowAttributes attributes);
    [LibraryImport(X11Library)]
    private static partial int XGetWindowProperty(nint display, nuint window, nuint property,
        nint offset, nint length, int delete, nuint requestedType, out nuint actualType,
        out int format, out nuint items, out nuint remaining, out byte* data);
    [LibraryImport(X11Library)]
    private static partial int XFree(void* data);
}
