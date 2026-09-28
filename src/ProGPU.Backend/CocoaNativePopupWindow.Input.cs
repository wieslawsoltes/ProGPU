using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal static unsafe partial class CocoaNativePopupWindow
{
    private static nint s_ownedPanelClass, s_ownedViewClass;
    private static Dictionary<nint, OwnedInputRegistration>? s_ownedInputs;

    private sealed class OwnedInputRegistration(nint panel, nint view, CocoaPopupInputQueue input) : IDisposable
    {
        internal nint Panel { get; } = panel;
        internal CocoaPopupInputQueue Input { get; } = input;

        public void Dispose()
        {
            Input.Close();
            if (s_ownedInputs is { } inputs && inputs.TryGetValue(view, out var current) && ReferenceEquals(current, this))
                s_ownedInputs.Remove(view);
        }
    }

    private static bool EnsureOwnedPopupClasses()
    {
        if (typeof(CocoaNativePopupWindow).IsCollectible || pthread_main_np() == 0) return false;
        if (s_ownedPanelClass == 0)
        {
            nint parent = Class("NSPanel\0"u8);
            if (parent == 0) return false;
            nint type;
            fixed (byte* name = "ProGPUOwnedPopupPanelV1\0"u8)
                type = OwnedAllocateClassPair(parent, name, 0);
            if (type == 0) return false;
            if (!AddOwnedBoolMethod(type, "canBecomeKeyWindow\0"u8, &OwnedFalse) ||
                !AddOwnedBoolMethod(type, "canBecomeMainWindow\0"u8, &OwnedFalse))
            {
                OwnedDisposeClassPair(type);
                return false;
            }
            OwnedRegisterClassPair(type);
            s_ownedPanelClass = type;
        }
        if (s_ownedViewClass == 0)
        {
            nint parent = Class("NSView\0"u8);
            if (parent == 0) return false;
            nint type;
            fixed (byte* name = "ProGPUOwnedPopupViewV1\0"u8)
                type = OwnedAllocateClassPair(parent, name, 0);
            if (type == 0) return false;
            if (!AddOwnedBoolMethod(type, "isFlipped\0"u8, &OwnedTrue) ||
                !AddOwnedBoolMethod(type, "acceptsFirstResponder\0"u8, &OwnedFalse) ||
                !AddOwnedFirstMouseMethod(type) ||
                !AddOwnedEventMethod(type, "mouseDown:\0"u8) ||
                !AddOwnedEventMethod(type, "mouseUp:\0"u8) ||
                !AddOwnedEventMethod(type, "rightMouseDown:\0"u8) ||
                !AddOwnedEventMethod(type, "rightMouseUp:\0"u8) ||
                !AddOwnedEventMethod(type, "otherMouseDown:\0"u8) ||
                !AddOwnedEventMethod(type, "otherMouseUp:\0"u8) ||
                !AddOwnedEventMethod(type, "mouseMoved:\0"u8) ||
                !AddOwnedEventMethod(type, "mouseDragged:\0"u8) ||
                !AddOwnedEventMethod(type, "rightMouseDragged:\0"u8) ||
                !AddOwnedEventMethod(type, "otherMouseDragged:\0"u8) ||
                !AddOwnedEventMethod(type, "mouseEntered:\0"u8) ||
                !AddOwnedEventMethod(type, "mouseExited:\0"u8) ||
                !AddOwnedEventMethod(type, "scrollWheel:\0"u8) ||
                !AddOwnedEventMethod(type, "mouseCancelled:\0"u8))
            {
                OwnedDisposeClassPair(type);
                return false;
            }
            OwnedRegisterClassPair(type);
            s_ownedViewClass = type;
        }
        s_ownedInputs ??= new();
        return true;
    }

    private static bool AddOwnedBoolMethod(nint type, ReadOnlySpan<byte> name,
        delegate* unmanaged[Cdecl]<nint, nint, byte> callback)
    {
        ReadOnlySpan<byte> encoding = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:\0"u8 : "c@:\0"u8;
        fixed (byte* signature = encoding)
            return OwnedAddMethod(type, Selector(name), (nint)callback, signature) != 0;
    }

    private static bool AddOwnedFirstMouseMethod(nint type)
    {
        ReadOnlySpan<byte> encoding = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:@\0"u8 : "c@:@\0"u8;
        fixed (byte* signature = encoding)
            return OwnedAddMethod(type, Selector("acceptsFirstMouse:\0"u8),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&OwnedAcceptsFirstMouse, signature) != 0;
    }

    private static bool AddOwnedEventMethod(nint type, ReadOnlySpan<byte> name)
    {
        fixed (byte* signature = "v@:@\0"u8)
            return OwnedAddMethod(type, Selector(name),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ReceiveOwnedPointer, signature) != 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OwnedTrue(nint receiver, nint selector) => 1;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OwnedFalse(nint receiver, nint selector) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OwnedAcceptsFirstMouse(nint receiver, nint selector, nint nativeEvent)
    {
        if (pthread_main_np() == 0) return 0;
        OwnedInputRegistration? registration = null;
        try { return s_ownedInputs is { } inputs && inputs.TryGetValue(receiver, out registration) && registration.Input.CanReceive ? (byte)1 : (byte)0; }
        catch { registration?.Input.Fail(CocoaPopupInputFailure.NativeCallback); return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReceiveOwnedPointer(nint receiver, nint selector, nint nativeEvent)
    {
        if (pthread_main_np() == 0) return;
        OwnedInputRegistration? registration = null;
        bool entered = false;
        try
        {
            if (s_ownedInputs is not { } inputs || !inputs.TryGetValue(receiver, out registration) || !registration.Input.CanReceive) return;
            registration.Input.BeginNativeCallback();
            entered = true;
            if (nativeEvent == 0 || Send(nativeEvent, "window\0"u8) != registration.Panel ||
                Send(receiver, "window\0"u8) != registration.Panel ||
                Send(registration.Panel, "contentView\0"u8) != receiver)
            {
                registration.Input.Fail(CocoaPopupInputFailure.InvalidEvent);
                return;
            }
            nuint nativeType = OwnedMessageUnsigned(nativeEvent, Selector("type\0"u8));
            var kind = CocoaPopupInputQueue.Classify(nativeType);
            if (kind == CocoaPopupPointerKind.None)
            {
                registration.Input.Fail(CocoaPopupInputFailure.InvalidEvent);
                return;
            }
            var point = OwnedInputPoint(nativeEvent, Selector("locationInWindow\0"u8));
            point = OwnedConvertInputPoint(receiver, Selector("convertPoint:fromView:\0"u8), point, 0);
            bool buttonEvent = kind is CocoaPopupPointerKind.Down or CocoaPopupPointerKind.Up or CocoaPopupPointerKind.Drag;
            bool scroll = kind == CocoaPopupPointerKind.Scroll;
            var value = new CocoaPopupPointerEvent(kind, point.X, point.Y,
                OwnedInputDouble(nativeEvent, Selector("timestamp\0"u8)),
                buttonEvent ? checked((int)OwnedMessageInteger(nativeEvent, Selector("buttonNumber\0"u8))) : -1,
                buttonEvent ? checked((int)OwnedMessageInteger(nativeEvent, Selector("clickCount\0"u8))) : 0,
                (CocoaPopupModifiers)(OwnedMessageUnsigned(nativeEvent, Selector("modifierFlags\0"u8)) & 0xffff0000u),
                scroll ? OwnedInputDouble(nativeEvent, Selector("scrollingDeltaX\0"u8)) : 0,
                scroll ? OwnedInputDouble(nativeEvent, Selector("scrollingDeltaY\0"u8)) : 0,
                scroll && GetOwnedBool(nativeEvent, "hasPreciseScrollingDeltas\0"u8),
                scroll ? checked((uint)OwnedMessageUnsigned(nativeEvent, Selector("phase\0"u8))) : 0,
                scroll ? checked((uint)OwnedMessageUnsigned(nativeEvent, Selector("momentumPhase\0"u8))) : 0);
            registration.Input.TryWrite(value);
        }
        catch
        {
            // Never unwind a managed exception through AppKit. The next managed
            // host drain reports a persistent fault, before publishing any input.
            registration?.Input.Fail(CocoaPopupInputFailure.NativeCallback);
        }
        finally
        {
            if (entered) registration!.Input.EndNativeCallback();
        }
    }

    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OwnedAllocateClassPair(nint parent, byte* name, nuint extraBytes);
    [LibraryImport(ObjC, EntryPoint = "objc_disposeClassPair")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void OwnedDisposeClassPair(nint type);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void OwnedRegisterClassPair(nint type);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte OwnedAddMethod(nint type, nint selector, nint callback, byte* signature);
    [LibraryImport(ObjC, EntryPoint = "object_getClass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OwnedObjectClass(nint instance);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OwnedInitTrackingArea(nint receiver, nint selector, CocoaMenuRect rect,
        nuint options, nint owner, nint userInfo);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial CocoaMenuPoint OwnedInputPoint(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial CocoaMenuPoint OwnedConvertInputPoint(nint receiver, nint selector, CocoaMenuPoint point, nint view);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial double OwnedInputDouble(nint receiver, nint selector);
}
