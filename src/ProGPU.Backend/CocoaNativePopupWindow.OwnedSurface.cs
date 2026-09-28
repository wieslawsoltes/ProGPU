using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal static unsafe partial class CocoaNativePopupWindow
{
    // AppKit's public NSWindowStyleMaskNonactivatingPanel. This is legal only
    // for a real NSPanel; never apply it to or replace a GLFW window's class.
    private const nuint NonactivatingPanelStyle = 1u << 7;

    internal static ICocoaOwnedPopupOperations? TryCreateOwned(NativeWindowHandle owner,
        NativeWindowBounds bounds, bool transparent)
    {
        if (!OperatingSystem.IsMacOS() || owner.Kind != NativeWindowKind.Cocoa ||
            !owner.IsValid || owner.Display != 0 ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64) ||
            pthread_main_np() == 0 ||
            !CocoaNativeWindowGeometry.TryCapture(owner, out var ownerGeometry)) return null;

        using var pool = new Pool();
        if (!TryGetOwnedPopupFrame(bounds, out var frame)) return null;
        if (!EnsureOwnedPopupClasses()) return null;
        nint panel = 0, view = 0, tracking = 0;
        OwnedInputRegistration? registration = null;
        var input = new CocoaPopupInputQueue();
        nint ownerView = ownerGeometry.ContentView;
        nint ownerDelegate = Send(owner.Handle, "delegate\0"u8);
        Retain(owner.Handle); Retain(ownerView); Retain(ownerDelegate);
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
            s_ownedInputs!.Add(view, registration);
            nint trackingClass = Class("NSTrackingArea\0"u8);
            if (trackingClass == 0) return null;
            tracking = OwnedInitTrackingArea(Send(trackingClass, "alloc\0"u8),
                Selector("initWithRect:options:owner:userInfo:\0"u8), default,
                0x01 | 0x02 | 0x80 | 0x200 | 0x400, view, 0);
            if (tracking == 0) return null;
            MessageVoidArgument(view, Selector("addTrackingArea:\0"u8), tracking);

            var operations = new OwnedOperations(owner, ownerView, ownerDelegate,
                ownerGeometry.CocoaWindowNumber, panel, view, tracking, registration);
            if (!GetOwnedBool(panel, "ignoresMouseEvents\0"u8) ||
                !operations.TryGetGeometry(out var geometry) || geometry.ContentBounds != bounds ||
                !NativePopupWindow.TryPrepareOwner(owner, operations.Window)) return null;
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
                Release(ownerDelegate); Release(ownerView); Release(owner.Handle);
            }
        }
    }

    private sealed class OwnedOperations(NativeWindowHandle owner, nint ownerView,
        nint ownerDelegate, long ownerNumber, nint panel, nint view, nint tracking,
        OwnedInputRegistration registration) : ICocoaOwnedPopupOperations
    {
        private bool _released;
        public NativeWindowHandle Window => new(NativeWindowKind.Cocoa, panel, 0, "NSPanel");
        public nint ContentView => view;
        public CocoaPopupInputQueue Input => registration.Input;

        private bool HasPanelIdentity => !_released &&
            OwnedObjectClass(panel) == s_ownedPanelClass && OwnedObjectClass(view) == s_ownedViewClass &&
            Send(panel, "contentView\0"u8) == view && Send(view, "window\0"u8) == panel &&
            Send(panel, "delegate\0"u8) == 0 &&
            OwnedMessageBoolArgument(panel, Selector("isKindOfClass:\0"u8), Class("NSPanel\0"u8)) != 0;

        public bool IsCurrent => HasPanelIdentity &&
            IsOwnedApplicationWindow(owner.Handle) &&
            Send(owner.Handle, "contentView\0"u8) == ownerView &&
            Send(owner.Handle, "delegate\0"u8) == ownerDelegate &&
            OwnedMessageInteger(owner.Handle, Selector("windowNumber\0"u8)) == ownerNumber &&
            Send(ownerView, "window\0"u8) == owner.Handle &&
            GetOwnedBool(panel, "worksWhenModal\0"u8) &&
            !GetOwnedBool(panel, "isFloatingPanel\0"u8) &&
            GetOwnedBool(panel, "becomesKeyOnlyIfNeeded\0"u8) &&
            !GetOwnedBool(panel, "isReleasedWhenClosed\0"u8) &&
            !GetOwnedBool(panel, "canBecomeKeyWindow\0"u8) && !GetOwnedBool(panel, "canBecomeMainWindow\0"u8) &&
            OwnedMessageUnsigned(panel, Selector("styleMask\0"u8)) == NonactivatingPanelStyle;

        public bool Show()
        {
            using var pool = new Pool();
            if (!IsCurrent) return false;
            bool shown = false;
            Input.SetVisible(true);
            try { return shown = NativePopupWindow.TryShowOwned(owner, Window, ShowWithoutActivation) && IsCurrent; }
            finally { if (!shown) Input.SetVisible(false); }
        }

        public bool SetInputAllowed(bool allowed)
        {
            using var pool = new Pool();
            if (!IsCurrent) return false;
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
            if (parent != 0 && parent != owner.Handle) return false;
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
            Release(ownerDelegate); Release(ownerView); Release(owner.Handle);
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
}
