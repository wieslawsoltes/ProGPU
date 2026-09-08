using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal static unsafe partial class CocoaNativeSystemMenu
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static nint s_actionClass;

    internal static bool TryShow(nint window, NativeWindowPoint point)
    {
        // AppKit is main-thread-only. Never dispatch synchronously to a different
        // thread while holding a source/host frame or wait for its message pump.
        if (window == 0 || pthread_main_np() == 0 ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64) ||
            typeof(CocoaNativeSystemMenu).IsCollectible) return false;
        nint applicationClass = Class("NSApplication\0"u8);
        nint poolClass = Class("NSAutoreleasePool\0"u8);
        if (applicationClass == 0 || poolClass == 0) return false;
        nint pool = Send(Send(poolClass, "alloc\0"u8), "init\0"u8);
        if (pool == 0) return false;
        try
        {
            nint application = Send(applicationClass, "sharedApplication\0"u8);
            var api = new Operations(application);
            return CocoaSystemMenu.Show(window, point, ref api);
        }
        finally { Release(pool); }
    }

    private readonly struct Operations(nint application) : ICocoaSystemMenuOperations
    {
        public bool TryRetainOwner(nint window, out CocoaMenuOwner owner, out CocoaMenuActions actions)
        {
            owner = default; actions = default;
            if (!IsLiveWindow(application, window)) return false;
            nint view = Send(window, "contentView\0"u8);
            if (view == 0) return false;
            nint windowDelegate = Send(window, "delegate\0"u8);
            actions = ReadActions(window);
            _ = Send(window, "retain\0"u8);
            _ = Send(view, "retain\0"u8);
            if (windowDelegate != 0) _ = Send(windowDelegate, "retain\0"u8);
            owner = new(window, view, windowDelegate);
            return true;
        }

        public bool TryGetPrimaryScreen(out CocoaMenuRect frame)
        {
            frame = default;
            nint screenClass = Class("NSScreen\0"u8);
            if (screenClass == 0) return false;
            // mainScreen follows keyboard focus and is NOT the desktop origin.
            // Read screens anew for each action so display reconfiguration is real.
            nint primary = Send(Send(screenClass, "screens\0"u8), "firstObject\0"u8);
            if (primary == 0) return false;
            nint selector = Selector("frame\0"u8);
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                MessageRectStret(out frame, primary, selector);
            else frame = MessageRect(primary, selector);
            return true;
        }

        public bool TryTrack(in CocoaMenuOwner owner, CocoaMenuActions actions, CocoaMenuPoint point, out CocoaMenuCommand command)
        {
            // AppKit owns flipped-view conversion and popup attachment/appearance.
            // These are screen/window points, never convertFromBacking pixels.
            var windowPoint = MessagePoint(owner.Window, Selector("convertPointFromScreen:\0"u8), point);
            var viewPoint = MessagePointObject(owner.View, Selector("convertPoint:fromView:\0"u8), windowPoint, 0);
            command = CocoaMenuCommand.None;
            if (!double.IsFinite(viewPoint.X) || !double.IsFinite(viewPoint.Y)) return false;
            return Track(owner.View, actions, viewPoint, out command);
        }

        public bool TryRevalidateOwner(in CocoaMenuOwner owner, out CocoaMenuActions actions)
        {
            actions = default;
            if (!IsLiveWindow(application, owner.Window) ||
                Send(owner.Window, "contentView\0"u8) != owner.View ||
                Send(owner.Window, "delegate\0"u8) != owner.Delegate) return false;
            actions = ReadActions(owner.Window);
            return true;
        }

        public void Perform(nint window, CocoaMenuCommand command)
        {
            // Use the button-equivalent methods, especially performClose:, which
            // preserves the window delegate / GLFW / source Closing cancellation.
            ReadOnlySpan<byte> action = command switch
            {
                CocoaMenuCommand.Minimize => "performMiniaturize:\0"u8,
                CocoaMenuCommand.Zoom => "performZoom:\0"u8,
                CocoaMenuCommand.Close => "performClose:\0"u8,
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
            MessageVoidObject(window, Selector(action), 0);
        }

        public void ReleaseOwner(in CocoaMenuOwner owner)
        {
            Release(owner.Delegate);
            Release(owner.View);
            Release(owner.Window);
        }
    }

    private static bool IsLiveWindow(nint application, nint window)
    {
        if (application == 0 || window == 0) return false;
        nint windows = Send(application, "windows\0"u8);
        // Identity lookup avoids messaging a caller-supplied pointer before it is
        // known to be an application window; hidden/closed windows are rejected.
        return windows != 0 && MessageIntegerObject(windows, Selector("indexOfObjectIdenticalTo:\0"u8), window) != nint.MaxValue &&
            MessageBool(window, Selector("isVisible\0"u8)) != 0;
    }

    private static CocoaMenuActions ReadActions(nint window)
    {
        CocoaMenuActions actions = default;
        nuint style = (nuint)MessageInteger(window, Selector("styleMask\0"u8));
        if ((style & 4) != 0 && ButtonEnabled(window, 1)) actions |= CocoaMenuActions.Minimize;
        if ((style & 8) != 0 && ButtonEnabled(window, 2)) actions |= CocoaMenuActions.Zoom;
        if ((style & 2) != 0 && ButtonEnabled(window, 0)) actions |= CocoaMenuActions.Close;
        return actions;
    }

    private static bool ButtonEnabled(nint window, nint kind)
    {
        nint button = MessageIntegerArgument(window, Selector("standardWindowButton:\0"u8), kind);
        return button != 0 && MessageBool(button, Selector("isEnabled\0"u8)) != 0;
    }

    private static bool Track(nint view, CocoaMenuActions actions, CocoaMenuPoint point, out CocoaMenuCommand command)
    {
        command = CocoaMenuCommand.None;
        nint menuClass = Class("NSMenu\0"u8), itemClass = Class("NSMenuItem\0"u8);
        if (menuClass == 0 || itemClass == 0 || !EnsureActionClass()) return false;
        nint empty = String("\0"u8), title = String("Window\0"u8);
        if (empty == 0 || title == 0) return false;
        nint menu = MessageObjectArgument(Send(menuClass, "alloc\0"u8), Selector("initWithTitle:\0"u8), title);
        if (menu == 0) return false;
        nint target = 0;
        CocoaMenuSelection selection = default;
        try
        {
            target = Send(Send(s_actionClass, "alloc\0"u8), "init\0"u8);
            if (target == 0) return false;
            MessageVoidBool(menu, Selector("setAutoenablesItems:\0"u8), 0);
            selection.Target = target;
            selection.MinimizeItem = AddItem(menu, itemClass, target, empty, "Minimize\0"u8,
                (actions & CocoaMenuActions.Minimize) != 0);
            selection.ZoomItem = AddItem(menu, itemClass, target, empty, "Zoom\0"u8,
                (actions & CocoaMenuActions.Zoom) != 0);
            selection.CloseItem = AddItem(menu, itemClass, target, empty, "Close\0"u8,
                (actions & CocoaMenuActions.Close) != 0);
            if (selection.MinimizeItem == 0 || selection.ZoomItem == 0 || selection.CloseItem == 0) return false;
            using var tracking = CocoaMenuSelectionContext.Enter(&selection);
            byte selected = MessagePopUp(menu, Selector("popUpMenuPositioningItem:atLocation:inView:\0"u8), 0, point, view);
            if (selected == 0) return true; // Cancellation; no command is applied.
            command = selection.Command;
            return command != CocoaMenuCommand.None;
        }
        finally
        {
            // Menu owns each added item; its target is borrowed for tracking.
            ClearTarget(selection.MinimizeItem);
            ClearTarget(selection.ZoomItem);
            ClearTarget(selection.CloseItem);
            Release(menu);
            Release(target);
        }
    }

    private static nint AddItem(nint menu, nint itemClass, nint target, nint empty, ReadOnlySpan<byte> label, bool enabled)
    {
        nint title = String(label);
        if (title == 0) return 0;
        nint item = MessageCreateItem(Send(itemClass, "alloc\0"u8), Selector("initWithTitle:action:keyEquivalent:\0"u8),
            title, Selector("selectWindowAction:\0"u8), empty);
        if (item == 0) return 0;
        try
        {
            MessageVoidObject(item, Selector("setTarget:\0"u8), target);
            MessageVoidBool(item, Selector("setEnabled:\0"u8), enabled ? (byte)1 : (byte)0);
            MessageVoidObject(menu, Selector("addItem:\0"u8), item);
            return item;
        }
        finally { Release(item); }
    }

    private static bool EnsureActionClass()
    {
        if (s_actionClass != 0) return true;
        // Main-thread-only initialization. Do not adopt another module's class or
        // swizzle NSObject/NSWindow. The registered callback has process lifetime.
        nint parent = Class("NSObject\0"u8);
        if (parent == 0) return false;
        nint type;
        fixed (byte* name = "ProGPUWindowMenuSelectionV1\0"u8)
            type = objc_allocateClassPair(parent, name, 0);
        if (type == 0) return false;
        fixed (byte* signature = "v@:@\0"u8)
        {
            if (class_addMethod(type, Selector("selectWindowAction:\0"u8), &Select, signature) == 0)
            {
                objc_disposeClassPair(type);
                return false;
            }
        }
        objc_registerClassPair(type);
        s_actionClass = type;
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Select(nint receiver, nint selector, nint item)
    {
        CocoaMenuSelectionContext.Record(receiver, item);
    }

    private static nint String(ReadOnlySpan<byte> text)
    {
        fixed (byte* value = text)
            return MessageObjectArgument(Class("NSString\0"u8), Selector("stringWithUTF8String:\0"u8), (nint)value);
    }

    private static nint Class(ReadOnlySpan<byte> name)
    { fixed (byte* value = name) return objc_getClass(value); }
    private static nint Selector(ReadOnlySpan<byte> name)
    { fixed (byte* value = name) return sel_registerName(value); }
    private static nint Send(nint receiver, ReadOnlySpan<byte> selector) => MessageObject(receiver, Selector(selector));
    private static void Release(nint value)
    { if (value != 0) MessageVoid(value, Selector("release\0"u8)); }
    private static void ClearTarget(nint item)
    { if (item != 0) MessageVoidObject(item, Selector("setTarget:\0"u8), 0); }

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int pthread_main_np();
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint objc_getClass(byte* name);
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint sel_registerName(byte* name);
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint objc_allocateClassPair(nint parent, byte* name, nuint extraBytes);
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte class_addMethod(nint type, nint selector,
        delegate* unmanaged[Cdecl]<nint, nint, nint, void> implementation, byte* signature);
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void objc_registerClassPair(nint type);
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void objc_disposeClassPair(nint type);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageObject(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageObjectArgument(nint receiver, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageInteger(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageIntegerObject(nint receiver, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageIntegerArgument(nint receiver, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte MessageBool(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageVoid(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageVoidObject(nint receiver, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageVoidBool(nint receiver, nint selector, byte value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial CocoaMenuRect MessageRect(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial CocoaMenuPoint MessagePoint(nint receiver, nint selector, CocoaMenuPoint point);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial CocoaMenuPoint MessagePointObject(nint receiver, nint selector, CocoaMenuPoint point, nint view);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageRectStret(out CocoaMenuRect result, nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte MessagePopUp(nint receiver, nint selector, nint item, CocoaMenuPoint point, nint view);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageCreateItem(nint receiver, nint selector, nint title, nint action, nint equivalent);
}
