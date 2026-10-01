using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Input;

namespace ProGPU.Backend;

internal static unsafe partial class CocoaNativePopupWindow
{
    // AppKit's public NSWindowStyleMaskNonactivatingPanel. This is legal only
    // for a real NSPanel; never apply it to or replace a GLFW window's class.
    private const nuint NonactivatingPanelStyle = 1u << 7;
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CocoaPopupSize(double Width, double Height);

    internal static ICocoaOwnedPopupOperations? TryCreateOwned(NativeWindowHandle owner,
        NativeWindowBounds bounds, bool transparent)
        => owner.Kind == NativeWindowKind.Cocoa && owner.IsValid && owner.Display == 0
            ? TryCreatePanel(owner, bounds, transparent) : null;

    internal static ICocoaOwnedPopupOperations? TryCreateUnbound(NativeWindowBounds bounds, bool transparent)
        => TryCreatePanel(NativeWindowHandle.Empty, bounds, transparent);

    private static ICocoaOwnedPopupOperations? TryCreatePanel(NativeWindowHandle owner,
        NativeWindowBounds bounds, bool transparent)
    {
        if (!OperatingSystem.IsMacOS() ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64) ||
            pthread_main_np() == 0) return null;

        // Match the geometry adapter's explicit AppKit admission. The deferred
        // owner does not prove that a source host has loaded AppKit already.
        nint applicationClass = Class("NSApplication\0"u8);
        if (applicationClass == 0 || Class("NSAutoreleasePool\0"u8) == 0) return null;
        using var pool = new Pool();
        if (Send(applicationClass, "sharedApplication\0"u8) == 0) return null;
        if (!TryGetOwnedPopupFrame(bounds, out var frame)) return null;
        if (!EnsureOwnedPopupClasses()) return null;
        nint panel = 0, view = 0, tracking = 0;
        OwnedInputRegistration? registration = null;
        var input = new CocoaPopupInputQueue();
        OwnerLease? ownerLease = owner == NativeWindowHandle.Empty ? null : OwnerLease.TryCapture(owner);
        if (owner != NativeWindowHandle.Empty && ownerLease is null) return null;
        bool transferred = false;
        try
        {
            panel = OwnedMessageInitPanel(Send(s_ownedPanelClass, "alloc\0"u8),
                Selector("initWithContentRect:styleMask:backing:defer:\0"u8), frame,
                NonactivatingPanelStyle, 2, 0);
            if (panel == 0) return null;
            SetOwnedBool(panel, "setReleasedWhenClosed:\0"u8, false);
            view = OwnedMessageInitView(Send(s_ownedViewClass, "alloc\0"u8),
                Selector("initWithFrame:\0"u8), new(0, 0, bounds.Width, bounds.Height));
            if (view == 0) return null;
            MessageVoidArgument(panel, Selector("setContentView:\0"u8), view);
            OwnedMessageVoidUnsigned(view, Selector("setAutoresizingMask:\0"u8), (1u << 1) | (1u << 4));
            SetOwnedBool(panel, "setWorksWhenModal:\0"u8, true);
            SetOwnedBool(panel, "setFloatingPanel:\0"u8, false);
            SetOwnedBool(panel, "setBecomesKeyOnlyIfNeeded:\0"u8, true);
            SetOwnedBool(panel, "setHidesOnDeactivate:\0"u8, false);
            SetOwnedBool(panel, "setAcceptsMouseMovedEvents:\0"u8, true);
            SetOwnedBool(panel, "setIgnoresMouseEvents:\0"u8, true);
            SetOwnedBool(panel, "setOpaque:\0"u8, !transparent);
            MessageVoidArgument(panel, Selector("setBackgroundColor:\0"u8),
                Send(Class("NSColor\0"u8), transparent ? "clearColor\0"u8 : "windowBackgroundColor\0"u8));

            registration = new(panel, view, input);
            registration.Cursor = ResolveOwnedCursor(StandardCursor.Arrow);
            Retain(registration.Cursor);
            if (registration.Cursor == 0) return null;
            s_ownedInputs!.Add(view, registration);
            nint trackingClass = Class("NSTrackingArea\0"u8);
            if (trackingClass == 0) return null;
            tracking = OwnedInitTrackingArea(Send(trackingClass, "alloc\0"u8),
                Selector("initWithRect:options:owner:userInfo:\0"u8), default,
                0x01 | 0x02 | 0x80 | 0x200 | 0x400, view, 0);
            if (tracking == 0) return null;
            MessageVoidArgument(view, Selector("addTrackingArea:\0"u8), tracking);

            var operations = new OwnedOperations(ownerLease, panel, view, tracking, registration);
            if (!GetOwnedBool(panel, "ignoresMouseEvents\0"u8) ||
                !operations.TryGetGeometry(out var geometry) || geometry.ContentBounds != bounds ||
                ownerLease is not null && !NativePopupWindow.TryPrepareOwner(owner, operations.Window)) return null;
            transferred = true;
            return operations;
        }
        finally
        {
            if (!transferred)
            {
                registration?.Dispose();
                if (view != 0 && tracking != 0)
                    MessageVoidArgument(view, Selector("removeTrackingArea:\0"u8), tracking);
                if (panel != 0)
                {
                    MessageVoidArgument(panel, Selector("orderOut:\0"u8), 0);
                    MessageVoid(panel, Selector("close\0"u8));
                }
                Release(tracking); Release(view); Release(panel);
                ownerLease?.Dispose();
            }
        }
    }

    private sealed class OwnerLease(NativeWindowHandle window, nint view, nint windowDelegate, long number) : IDisposable
    {
        private bool _released;
        internal NativeWindowHandle Window => window;
        internal bool IsCurrent => !_released && IsOwnedApplicationWindow(window.Handle) &&
            Send(window.Handle, "contentView\0"u8) == view &&
            Send(window.Handle, "delegate\0"u8) == windowDelegate &&
            OwnedMessageInteger(window.Handle, Selector("windowNumber\0"u8)) == number &&
            Send(view, "window\0"u8) == window.Handle;

        internal static OwnerLease? TryCapture(NativeWindowHandle window)
        {
            if (window.Kind != NativeWindowKind.Cocoa || !window.IsValid || window.Display != 0 ||
                !CocoaNativeWindowGeometry.TryCapture(window, out var geometry)) return null;
            nint windowDelegate = Send(window.Handle, "delegate\0"u8);
            Retain(window.Handle); Retain(geometry.ContentView); Retain(windowDelegate);
            var lease = new OwnerLease(window, geometry.ContentView, windowDelegate, geometry.CocoaWindowNumber);
            if (lease.IsCurrent) return lease;
            lease.Dispose();
            return null;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            Release(windowDelegate); Release(view); Release(window.Handle);
        }
    }

    private sealed class OwnedOperations(OwnerLease? initialOwner, nint panel, nint view, nint tracking,
        OwnedInputRegistration registration) : ICocoaOwnedPopupOperations
    {
        private bool _released;
        private OwnerLease? _owner = initialOwner;
        public NativeWindowHandle Window => new(NativeWindowKind.Cocoa, panel, 0, "NSPanel");
        public nint ContentView => view;
        public CocoaPopupInputQueue Input => registration.Input;

        private bool HasPanelIdentity => !_released &&
            OwnedObjectClass(panel) == s_ownedPanelClass && OwnedObjectClass(view) == s_ownedViewClass &&
            Send(panel, "contentView\0"u8) == view && Send(view, "window\0"u8) == panel &&
            Send(panel, "delegate\0"u8) == 0 &&
            OwnedMessageBoolArgument(panel, Selector("isKindOfClass:\0"u8), Class("NSPanel\0"u8)) != 0;

        public bool IsCurrent => HasPanelIdentity &&
            (_owner is null || _owner.IsCurrent) &&
            GetOwnedBool(panel, "worksWhenModal\0"u8) &&
            !GetOwnedBool(panel, "isFloatingPanel\0"u8) &&
            GetOwnedBool(panel, "becomesKeyOnlyIfNeeded\0"u8) &&
            !GetOwnedBool(panel, "isReleasedWhenClosed\0"u8) &&
            !GetOwnedBool(panel, "canBecomeKeyWindow\0"u8) && !GetOwnedBool(panel, "canBecomeMainWindow\0"u8) &&
            OwnedMessageUnsigned(panel, Selector("styleMask\0"u8)) == NonactivatingPanelStyle;

        public bool BindOwner(NativeWindowHandle owner)
        {
            using var pool = new Pool();
            if (!IsCurrent || GetOwnedBool(panel, "isVisible\0"u8) ||
                Send(panel, "parentWindow\0"u8) != 0 || NativeWindowModalSession.RetainsWindow(Window)) return false;
            OwnerLease? candidate = owner == NativeWindowHandle.Empty ? null : OwnerLease.TryCapture(owner);
            if (owner != NativeWindowHandle.Empty && candidate is null) return false;
            try
            {
                if (candidate is not null && !NativePopupWindow.TryPrepareOwner(owner, Window)) return false;
                if (!IsCurrent || GetOwnedBool(panel, "isVisible\0"u8) || Send(panel, "parentWindow\0"u8) != 0 ||
                    NativeWindowModalSession.RetainsWindow(Window) ||
                    candidate is not null && !candidate.IsCurrent) return false;
                OwnerLease? previous = _owner;
                _owner = candidate;
                candidate = null;
                previous?.Dispose();
                return IsCurrent && !GetOwnedBool(panel, "isVisible\0"u8) && Send(panel, "parentWindow\0"u8) == 0;
            }
            finally { candidate?.Dispose(); }
        }

        public bool Show()
        {
            using var pool = new Pool();
            if (!IsCurrent || _owner is null) return false;
            bool shown = false;
            Input.SetVisible(true);
            try { return shown = NativePopupWindow.TryShowOwned(_owner.Window, Window, ShowWithoutActivation) && IsCurrent; }
            finally { if (!shown) Input.SetVisible(false); }
        }

        public bool SetInputAllowed(bool allowed)
        {
            using var pool = new Pool();
            if (!IsCurrent || allowed && _owner is null) return false;
            SetOwnedBool(panel, "setIgnoresMouseEvents:\0"u8, !allowed);
            return IsCurrent && GetOwnedBool(panel, "ignoresMouseEvents\0"u8) == !allowed;
        }

        private void ShowWithoutActivation() =>
            MessageVoidArgument(panel, Selector("orderFront:\0"u8), 0);

        public bool Hide()
        {
            Input.SetVisible(false);
            if (!HasPanelIdentity || NativeWindowModalSession.RetainsWindow(Window)) return false;
            using var pool = new Pool();
            nint parent = Send(panel, "parentWindow\0"u8);
            if (parent != 0 && (_owner is null || parent != _owner.Window.Handle)) return false;
            MessageVoidArgument(panel, Selector("orderOut:\0"u8), 0);
            return HasPanelIdentity && !GetOwnedBool(panel, "isVisible\0"u8) &&
                Send(panel, "parentWindow\0"u8) == 0;
        }

        public bool SetBounds(NativeWindowBounds bounds)
        {
            using var pool = new Pool();
            if (!IsCurrent || !TryGetOwnedPopupFrame(bounds, out var frame)) return false;
            OwnedMessageSetFrame(panel, Selector("setFrame:display:\0"u8), frame, 0);
            return TryGetGeometry(out var geometry) && geometry.ContentBounds == bounds;
        }

        public bool SetTopMost(bool value)
        {
            using var pool = new Pool();
            if (!TryCapturePresentationState(out bool visible, out nint parent)) return false;
            nint level = (nint)(value ? MacOsNativeWindowPlatform.FloatingWindowLevel : MacOsNativeWindowPlatform.NormalWindowLevel);
            MessageVoidArgument(panel, Selector("setLevel:\0"u8), level);
            return OwnedMessageInteger(panel, Selector("level\0"u8)) == level && HasPresentationState(visible, parent);
        }

        public bool SetOpacity(double value)
        {
            if (!double.IsFinite(value) || value is < 0 or > 1) return false;
            using var pool = new Pool();
            if (!TryCapturePresentationState(out bool visible, out nint parent)) return false;
            OwnedMessageSetDouble(panel, Selector("setAlphaValue:\0"u8), value);
            return OwnedMessageDouble(panel, Selector("alphaValue\0"u8)) == value && HasPresentationState(visible, parent);
        }

        public bool SetZOrder(NativeWindowZOrder value)
        {
            if (value is not NativeWindowZOrder.Front and not NativeWindowZOrder.Back) return false;
            using var pool = new Pool();
            // AppKit orderFront/orderBack can show a hidden window. Ordering is
            // available only after Show attached this panel to its live owner.
            if (!TryCapturePresentationState(out bool visible, out nint parent) || !visible) return false;
            MessageVoidArgument(panel, Selector(value == NativeWindowZOrder.Front ? "orderFront:\0"u8 : "orderBack:\0"u8), 0);
            // This verifies identity/visibility/ownership, not an observable
            // global stack rank: AppKit exposes no synchronous ordering receipt.
            return HasPresentationState(visible, parent);
        }

        public bool SetSizeConstraints(NativeWindowSize minimum, NativeWindowSize maximum)
        {
            if (minimum.Width < 0 || minimum.Height < 0 || maximum.Width < minimum.Width || maximum.Height < minimum.Height)
                return false;
            using var pool = new Pool();
            if (!TryCapturePresentationState(out bool visible, out nint parent)) return false;
            // NativeWindowSize uses content points, independently of backing
            // scale. Both zero minima and the exact int.MaxValue bound survive.
            var min = new CocoaPopupSize(minimum.Width, minimum.Height);
            var max = new CocoaPopupSize(maximum.Width, maximum.Height);
            OwnedMessageSetSize(panel, Selector("setContentMinSize:\0"u8), min);
            if (!HasPresentationState(visible, parent)) return false;
            OwnedMessageSetSize(panel, Selector("setContentMaxSize:\0"u8), max);
            return OwnedMessageSize(panel, Selector("contentMinSize\0"u8)) == min &&
                OwnedMessageSize(panel, Selector("contentMaxSize\0"u8)) == max && HasPresentationState(visible, parent);
        }

        private bool TryCapturePresentationState(out bool visible, out nint parent)
        {
            visible = false;
            parent = 0;
            if (!IsCurrent) return false;
            visible = GetOwnedBool(panel, "isVisible\0"u8);
            parent = Send(panel, "parentWindow\0"u8);
            return HasPresentationState(visible, parent);
        }

        private bool HasPresentationState(bool visible, nint parent) => IsCurrent &&
            (visible ? _owner is not null && parent == _owner.Window.Handle : parent == 0) &&
            GetOwnedBool(panel, "isVisible\0"u8) == visible && Send(panel, "parentWindow\0"u8) == parent && IsCurrent;

        public bool SupportsCursor(StandardCursor cursor)
        {
            using var pool = new Pool();
            return IsCurrent && ResolveOwnedCursor(cursor) != 0;
        }

        public bool SetCursor(StandardCursor cursor, bool hidden)
        {
            using var pool = new Pool();
            if (!IsCurrent) return false;
            nint next = ResolveOwnedCursor(cursor);
            if (next == 0) return false;
            if (hidden)
            {
                if (registration.InvisibleCursor == 0)
                    registration.InvisibleCursor = CreateOwnedInvisibleCursor();
                next = registration.InvisibleCursor;
                if (next == 0) return false;
            }
            Retain(next);
            nint previous = registration.Cursor;
            registration.Cursor = next;
            Release(previous);
            MessageVoidArgument(panel, Selector("invalidateCursorRectsForView:\0"u8), view);
            Input.EnsureHealthy();
            return IsCurrent && registration.Cursor == next;
        }

        public bool TryGetGeometry(out NativeWindowGeometrySnapshot snapshot)
        {
            snapshot = default;
            if (!IsCurrent || !CocoaNativeWindowGeometry.TryCapture(Window, out var candidate) ||
                candidate.ContentView != view || !IsCurrent) return false;
            snapshot = candidate;
            return true;
        }

        public void Dispose()
        {
            if (_released) return;
            if (!Hide()) throw new InvalidOperationException("The owned NSPanel cannot be retired.");
            using var pool = new Pool();
            registration.Dispose();
            MessageVoidArgument(view, Selector("removeTrackingArea:\0"u8), tracking);
            MessageVoid(panel, Selector("close\0"u8));
            _released = true;
            Release(tracking); Release(view); Release(panel);
            _owner?.Dispose();
            _owner = null;
        }
    }

    private static bool IsOwnedApplicationWindow(nint window)
    {
        nint windows = Send(Send(Class("NSApplication\0"u8), "sharedApplication\0"u8), "windows\0"u8);
        return windows != 0 && MessageArgument(windows, Selector("indexOfObjectIdenticalTo:\0"u8), window) != nint.MaxValue;
    }

    private static bool TryGetOwnedPopupFrame(NativeWindowBounds bounds, out CocoaMenuRect frame)
    {
        frame = default;
        nint screens = Send(Class("NSScreen\0"u8), "screens\0"u8);
        nint primary = Send(screens, "firstObject\0"u8);
        if (primary == 0) return false;
        Retain(primary);
        try
        {
            CocoaMenuRect primaryFrame = ReadOwnedPopupScreenFrame(primary);
            if (!CocoaWindowGeometry.TryMapDesktopRectangle(bounds, primaryFrame, out var candidate) ||
                Send(Send(Class("NSScreen\0"u8), "screens\0"u8), "firstObject\0"u8) != primary ||
                ReadOwnedPopupScreenFrame(primary) != primaryFrame) return false;
            frame = candidate;
            return true;
        }
        finally { Release(primary); }
    }

    private static CocoaMenuRect ReadOwnedPopupScreenFrame(nint primary)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            OwnedMessageRectStret(out var result, primary, Selector("frame\0"u8));
            return result;
        }
        return OwnedMessageRect(primary, Selector("frame\0"u8));
    }

    private static bool GetOwnedBool(nint receiver, ReadOnlySpan<byte> name) => MessageBool(receiver, Selector(name)) != 0;
    private static void SetOwnedBool(nint receiver, ReadOnlySpan<byte> name, bool value) =>
        MessageVoidBool(receiver, Selector(name), value ? (byte)1 : (byte)0);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OwnedMessageInitPanel(nint receiver, nint selector, CocoaMenuRect rectangle,
        nuint style, nuint backing, byte defer);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OwnedMessageInitView(nint receiver, nint selector, CocoaMenuRect rectangle);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nuint OwnedMessageUnsigned(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OwnedMessageInteger(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void OwnedMessageVoidUnsigned(nint receiver, nint selector, nuint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte OwnedMessageBoolArgument(nint receiver, nint selector, nint argument);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial CocoaMenuRect OwnedMessageRect(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void OwnedMessageRectStret(out CocoaMenuRect result, nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void OwnedMessageSetFrame(nint receiver, nint selector, CocoaMenuRect rectangle, byte display);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial double OwnedMessageDouble(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void OwnedMessageSetDouble(nint receiver, nint selector, double value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial CocoaPopupSize OwnedMessageSize(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void OwnedMessageSetSize(nint receiver, nint selector, CocoaPopupSize value);
}
