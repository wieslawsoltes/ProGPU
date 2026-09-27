using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal static unsafe partial class CocoaNativeWindowGeometry
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    internal static bool TryCapture(NativeWindowHandle window, out NativeWindowGeometrySnapshot snapshot)
    {
        snapshot = default;
        if (!OperatingSystem.IsMacOS() || window.Kind != NativeWindowKind.Cocoa || !window.IsValid ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64) ||
            pthread_main_np() == 0)
            return false;

        nint applicationClass = Class("NSApplication\0"u8);
        nint poolClass = Class("NSAutoreleasePool\0"u8);
        if (applicationClass == 0 || poolClass == 0) return false;
        nint pool = Send(Send(poolClass, "alloc\0"u8), "init\0"u8);
        if (pool == 0) return false;
        NativeWindowGeometrySnapshot candidate;
        bool captured;
        try
        {
            var operations = new Operations(Send(applicationClass, "sharedApplication\0"u8));
            captured = CocoaWindowGeometry.TryCapture(window, ref operations, out candidate);
        }
        finally { Release(pool); }
        if (!captured) return false;
        snapshot = candidate;
        return true;
    }

    private readonly struct Operations(nint application) : ICocoaWindowGeometryOperations
    {
        public bool TryRetain(nint window, out CocoaGeometryOwner owner)
        {
            owner = default;
            // Validate membership before messaging the caller's native pointer.
            // A hidden, materialized owned window is eligible; a retired one is not.
            if (!IsApplicationWindow(application, window)) return false;
            _ = Send(window, "retain\0"u8);
            nint view = 0;
            bool viewRetained = false;
            bool retained = false;
            try
            {
                view = Send(window, "contentView\0"u8);
                if (view == 0) return false;
                _ = Send(view, "retain\0"u8);
                viewRetained = true;
                long number = MessageInteger(window, Selector("windowNumber\0"u8));
                if (number <= 0 || Send(view, "window\0"u8) != window ||
                    Send(window, "contentView\0"u8) != view || !IsApplicationWindow(application, window))
                    return false;
                owner = new(window, view, number);
                retained = true;
                return true;
            }
            finally
            {
                if (!retained)
                {
                    if (viewRetained) CocoaNativeWindowGeometry.Release(view);
                    CocoaNativeWindowGeometry.Release(window);
                }
            }
        }

        public bool TryRead(in CocoaGeometryOwner owner, out CocoaGeometryReading reading)
        {
            reading = default;
            if (!IsCurrent(owner)) return false;
            nint screenClass = Class("NSScreen\0"u8);
            if (screenClass == 0) return false;
            // Read the current primary screen, not focus-dependent mainScreen.
            nint primary = Send(Send(screenClass, "screens\0"u8), "firstObject\0"u8);
            if (primary == 0) return false;
            _ = Send(primary, "retain\0"u8);
            try
            {
                CocoaMenuRect primaryFrame = ReadRectangle(primary, "frame\0"u8);
                CocoaMenuRect viewBounds = ReadRectangle(owner.View, "bounds\0"u8);
                CocoaMenuRect windowBounds = ConvertViewRectangle(owner.View, viewBounds);
                CocoaMenuRect contentScreenBounds = ConvertWindowRectangle(owner.Window, windowBounds);
                CocoaMenuRect frame = ReadRectangle(owner.Window, "frame\0"u8);
                double scale = MessageDouble(owner.Window, Selector("backingScaleFactor\0"u8));
                // A display change during any native getter invalidates this read.
                if (Send(Send(screenClass, "screens\0"u8), "firstObject\0"u8) != primary ||
                    ReadRectangle(primary, "frame\0"u8) != primaryFrame)
                    return false;
                reading = new(contentScreenBounds, frame, primaryFrame, scale);
                return true;
            }
            finally { CocoaNativeWindowGeometry.Release(primary); }
        }

        public bool IsCurrent(in CocoaGeometryOwner owner)
        {
            if (!IsApplicationWindow(application, owner.Window)) return false;
            nint view = Send(owner.Window, "contentView\0"u8);
            // Never dereference a replacement view: only the retained view is ours.
            return view == owner.View && Send(owner.View, "window\0"u8) == owner.Window &&
                CocoaWindowGeometry.IsSameOwner(owner,
                    new(owner.Window, view, MessageInteger(owner.Window, Selector("windowNumber\0"u8))));
        }

        public void Release(in CocoaGeometryOwner owner)
        {
            CocoaNativeWindowGeometry.Release(owner.View);
            CocoaNativeWindowGeometry.Release(owner.Window);
        }
    }

    private static bool IsApplicationWindow(nint application, nint window)
    {
        if (application == 0 || window == 0) return false;
        nint windows = Send(application, "windows\0"u8);
        return windows != 0 && MessageIntegerObject(windows,
            Selector("indexOfObjectIdenticalTo:\0"u8), window) != nint.MaxValue;
    }

    private static CocoaMenuRect ReadRectangle(nint receiver, ReadOnlySpan<byte> name)
    {
        nint selector = Selector(name);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return MessageRect(receiver, selector);
        MessageRectStret(out CocoaMenuRect result, receiver, selector);
        return result;
    }

    private static CocoaMenuRect ConvertViewRectangle(nint view, CocoaMenuRect bounds)
    {
        nint selector = Selector("convertRect:toView:\0"u8);
        // A nil destination converts the real view bounds to its window, including
        // view flipping/transforms. It is not an inferred content/frame inset.
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return MessageRectRectObject(view, selector, bounds, 0);
        MessageRectRectObjectStret(out CocoaMenuRect result, view, selector, bounds, 0);
        return result;
    }

    private static CocoaMenuRect ConvertWindowRectangle(nint window, CocoaMenuRect bounds)
    {
        nint selector = Selector("convertRectToScreen:\0"u8);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return MessageRectRect(window, selector, bounds);
        MessageRectRectStret(out CocoaMenuRect result, window, selector, bounds);
        return result;
    }

    private static nint Class(ReadOnlySpan<byte> name)
    { fixed (byte* value = name) return objc_getClass(value); }
    private static nint Selector(ReadOnlySpan<byte> name)
    { fixed (byte* value = name) return sel_registerName(value); }
    private static nint Send(nint receiver, ReadOnlySpan<byte> selector) => MessageObject(receiver, Selector(selector));
    private static void Release(nint value)
    { if (value != 0) MessageVoid(value, Selector("release\0"u8)); }

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int pthread_main_np();
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint objc_getClass(byte* name);
    [LibraryImport(ObjC)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint sel_registerName(byte* name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageObject(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageInteger(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageIntegerObject(nint receiver, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial double MessageDouble(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageVoid(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial CocoaMenuRect MessageRect(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageRectStret(out CocoaMenuRect result, nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial CocoaMenuRect MessageRectRect(nint receiver, nint selector, CocoaMenuRect bounds);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageRectRectStret(out CocoaMenuRect result, nint receiver, nint selector, CocoaMenuRect bounds);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial CocoaMenuRect MessageRectRectObject(nint receiver, nint selector, CocoaMenuRect bounds, nint view);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageRectRectObjectStret(out CocoaMenuRect result, nint receiver, nint selector, CocoaMenuRect bounds, nint view);
}
