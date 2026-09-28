using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Input;

namespace ProGPU.Backend;

internal static unsafe partial class CocoaNativePopupWindow
{
    private static nint ResolveOwnedCursor(StandardCursor cursor)
    {
        nint type = Class("NSCursor\0"u8);
        if (type == 0) return 0;
        ReadOnlySpan<byte> selector = cursor switch
        {
            StandardCursor.Default or StandardCursor.Arrow => "arrowCursor\0"u8,
            StandardCursor.IBeam => "IBeamCursor\0"u8,
            StandardCursor.Crosshair => "crosshairCursor\0"u8,
            StandardCursor.Hand => "pointingHandCursor\0"u8,
            StandardCursor.HResize => "resizeLeftRightCursor\0"u8,
            StandardCursor.VResize => "resizeUpDownCursor\0"u8,
            StandardCursor.NotAllowed => "operationNotAllowedCursor\0"u8,
            _ => default
        };
        if (!selector.IsEmpty) return Send(type, selector);
        if (cursor is not (StandardCursor.NwseResize or StandardCursor.NeswResize)) return 0;
        // Public macOS 15 API only; no private diagonal selectors or replacement
        // hand/arrow cursor reported as the requested shape on older systems.
        nint resize = Selector("frameResizeCursorFromPosition:inDirections:\0"u8);
        if (OwnedMessageBoolArgument(type, Selector("respondsToSelector:\0"u8), resize) == 0) return 0;
        return OwnedCursorResize(type, resize, cursor == StandardCursor.NwseResize ? 3u : 9u, 3);
    }

    private static nint CreateOwnedInvisibleCursor()
    {
        // A real, zero-alpha one-pixel bitmap. Do not modify NSCursor's global
        // hide count, which would also hide the owner's or another window's cursor.
        nint appKit = 0, bitmap = 0, image = 0;
        try
        {
            if (!NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out appKit) ||
                !NativeLibrary.TryGetExport(appKit, "NSDeviceRGBColorSpace", out nint symbol)) return 0;
            nint colorSpace = *(nint*)symbol; // Borrow the public exported constant, not a guessed string value.
            if (colorSpace == 0) return 0;
            bitmap = OwnedCursorBitmap(Send(Class("NSBitmapImageRep\0"u8), "alloc\0"u8),
                Selector("initWithBitmapDataPlanes:pixelsWide:pixelsHigh:bitsPerSample:samplesPerPixel:hasAlpha:isPlanar:colorSpaceName:bytesPerRow:bitsPerPixel:\0"u8),
                0, 1, 1, 8, 4, 1, 0, colorSpace, 4, 32);
            if (bitmap == 0) return 0;
            nint pixels = Send(bitmap, "bitmapData\0"u8);
            if (pixels == 0) return 0;
            new Span<byte>((void*)pixels, 4).Clear();
            image = OwnedCursorInitSize(Send(Class("NSImage\0"u8), "alloc\0"u8),
                Selector("initWithSize:\0"u8), new(1, 1));
            if (image == 0) return 0;
            MessageVoidArgument(image, Selector("addRepresentation:\0"u8), bitmap);
            return OwnedCursorInitImage(Send(Class("NSCursor\0"u8), "alloc\0"u8),
                Selector("initWithImage:hotSpot:\0"u8), image, default);
        }
        finally
        {
            Release(image); Release(bitmap);
            if (appKit != 0) NativeLibrary.Free(appKit);
        }
    }

    private static bool AddOwnedCursorMethod(nint type)
    {
        fixed (byte* signature = "v@:\0"u8)
            return OwnedAddMethod(type, Selector("resetCursorRects\0"u8),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&ResetOwnedCursorRects, signature) != 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ResetOwnedCursorRects(nint receiver, nint selector)
    {
        if (pthread_main_np() == 0) return;
        OwnedInputRegistration? registration = null;
        bool entered = false;
        try
        {
            if (s_ownedInputs is not { } inputs || !inputs.TryGetValue(receiver, out registration)) return;
            registration.Input.BeginNativeCallback();
            entered = true;
            if (registration.Cursor == 0 || Send(receiver, "window\0"u8) != registration.Panel ||
                Send(registration.Panel, "contentView\0"u8) != receiver)
            {
                registration.Input.Fail(CocoaPopupInputFailure.NativeCallback);
                return;
            }
            CocoaMenuRect bounds;
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                OwnedMessageRectStret(out bounds, receiver, Selector("bounds\0"u8));
            else bounds = OwnedMessageRect(receiver, Selector("bounds\0"u8));
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
                !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                bounds.Width <= 0 || bounds.Height <= 0)
            {
                registration.Input.Fail(CocoaPopupInputFailure.NativeCallback);
                return;
            }
            OwnedCursorAddRect(receiver, Selector("addCursorRect:cursor:\0"u8), bounds, registration.Cursor);
        }
        catch { registration?.Input.Fail(CocoaPopupInputFailure.NativeCallback); }
        finally { if (entered) registration!.Input.EndNativeCallback(); }
    }

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OwnedCursorResize(nint receiver, nint selector, nuint position, nuint directions);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void OwnedCursorAddRect(nint receiver, nint selector, CocoaMenuRect bounds, nint cursor);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OwnedCursorBitmap(nint receiver, nint selector, nint planes,
        nint width, nint height, nint bitsPerSample, nint samplesPerPixel, byte alpha, byte planar,
        nint colorSpace, nint bytesPerRow, nint bitsPerPixel);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OwnedCursorInitSize(nint receiver, nint selector, CocoaMenuPoint size);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint OwnedCursorInitImage(nint receiver, nint selector, nint image, CocoaMenuPoint hotspot);
}
