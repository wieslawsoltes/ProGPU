using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal static unsafe partial class CocoaNativePopupWindow
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    internal static bool TryConfigureOwner(nint owner, nint popup)
    {
        if (pthread_main_np() == 0 ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64)) return false;
        using var pool = new Pool();
        nint application = Send(Class("NSApplication\0"u8), "sharedApplication\0"u8);
        nint windows = Send(application, "windows\0"u8);
        nint lookup = Selector("indexOfObjectIdenticalTo:\0"u8);
        if (windows == 0 || MessageArgument(windows, lookup, owner) == nint.MaxValue ||
            MessageArgument(windows, lookup, popup) == nint.MaxValue) return false;

        // Keep the actual host objects alive across synchronous AppKit callbacks.
        // No native ownership is retained after this configuration operation.
        nint ownerView = Send(owner, "contentView\0"u8), popupView = Send(popup, "contentView\0"u8);
        if (ownerView == 0 || popupView == 0) return false;
        nint ownerDelegate = Send(owner, "delegate\0"u8), popupDelegate = Send(popup, "delegate\0"u8);
        nint previous = Send(popup, "parentWindow\0"u8);
        Retain(owner); Retain(popup); Retain(ownerView); Retain(popupView);
        Retain(ownerDelegate); Retain(popupDelegate); Retain(previous);
        try
        {
            var operations = new Operations(owner, popup, ownerView, popupView, ownerDelegate, popupDelegate);
            return CocoaPopupConfiguration.Apply(owner, popup, ref operations);
        }
        finally
        {
            Release(previous); Release(popupDelegate); Release(ownerDelegate);
            Release(popupView); Release(ownerView); Release(popup); Release(owner);
        }
    }

    private readonly struct Operations(nint owner, nint popup, nint ownerView, nint popupView,
        nint ownerDelegate, nint popupDelegate) : ICocoaPopupOperations
    {
        public bool HasCurrentHostIdentity =>
            Send(owner, "contentView\0"u8) == ownerView && Send(popup, "contentView\0"u8) == popupView &&
            Send(owner, "delegate\0"u8) == ownerDelegate && Send(popup, "delegate\0"u8) == popupDelegate;
        public bool IsVisible(nint window) => MessageBool(window, Selector("isVisible\0"u8)) != 0;
        public nint GetParent(nint window) => Send(window, "parentWindow\0"u8);
        public bool GetHidesOnDeactivate(nint window) => MessageBool(window, Selector("hidesOnDeactivate\0"u8)) != 0;
        public void SetHidesOnDeactivate(nint window, bool value) =>
            MessageVoidBool(window, Selector("setHidesOnDeactivate:\0"u8), value ? (byte)1 : (byte)0);
        public void RemoveChild(nint parent, nint child) =>
            MessageVoidArgument(parent, Selector("removeChildWindow:\0"u8), child);
        public void AddChild(nint parent, nint child) =>
            MessageVoidArgumentInteger(parent, Selector("addChildWindow:ordered:\0"u8), child, 1);
    }

    private readonly ref struct Pool
    {
        private readonly nint _value;
        public Pool()
        {
            _value = Send(Send(Class("NSAutoreleasePool\0"u8), "alloc\0"u8), "init\0"u8);
            if (_value == 0) throw new InvalidOperationException("AppKit autorelease scope creation failed.");
        }
        public void Dispose() => Release(_value);
    }

    private static nint Class(ReadOnlySpan<byte> value) { fixed (byte* p = value) return objc_getClass(p); }
    private static nint Selector(ReadOnlySpan<byte> value) { fixed (byte* p = value) return sel_registerName(p); }
    private static nint Send(nint receiver, ReadOnlySpan<byte> selector) => Message(receiver, Selector(selector));
    private static void Retain(nint value) { if (value != 0) _ = Send(value, "retain\0"u8); }
    private static void Release(nint value) { if (value != 0) MessageVoid(value, Selector("release\0"u8)); }

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
    private static partial nint Message(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint MessageArgument(nint receiver, nint selector, nint argument);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte MessageBool(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageVoid(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageVoidArgument(nint receiver, nint selector, nint argument);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageVoidBool(nint receiver, nint selector, byte value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void MessageVoidArgumentInteger(nint receiver, nint selector, nint argument, nint order);
}
