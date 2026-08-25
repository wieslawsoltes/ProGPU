using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Windowing;

namespace Avalonia.SilkNet;

internal enum MacOsGestureKind
{
    Magnify,
    Rotate,
    Swipe
}

internal readonly record struct MacOsGestureEvent(
    MacOsGestureKind Kind,
    double DeltaX,
    double DeltaY);

/// <summary>
/// Adds the three public NSResponder gesture methods that GLFW's content view
/// does not implement. Methods remain installed on the process-local GLFW
/// view class and dispatch only while a source is registered for the view.
/// </summary>
internal sealed unsafe class MacOsGestureInputSource : IDisposable
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private static readonly object Gate = new();
    private static readonly Dictionary<nint, Action<MacOsGestureEvent>>
        Handlers = [];
    private static readonly HashSet<nint> InstalledClasses = [];
    private static nint s_magnificationSelector;
    private static nint s_rotationSelector;
    private static nint s_deltaXSelector;
    private static nint s_deltaYSelector;

    private readonly nint _view;
    private bool _disposed;

    private MacOsGestureInputSource(
        nint view,
        Action<MacOsGestureEvent> handler)
    {
        _view = view;
        Handlers[view] = handler;
    }

    internal static MacOsGestureInputSource? TryCreate(
        IWindow window,
        Action<MacOsGestureEvent> handler)
    {
        if (!OperatingSystem.IsMacOS() ||
            window.Native?.Cocoa is not { } nsWindow ||
            nsWindow == 0)
        {
            return null;
        }

        nint contentView = SendObject(
            nsWindow,
            sel_registerName("contentView"));
        nint viewClass = object_getClass(contentView);
        if (contentView == 0 || viewClass == 0)
            return null;

        lock (Gate)
        {
            s_magnificationSelector =
                sel_registerName("magnification");
            s_rotationSelector = sel_registerName("rotation");
            s_deltaXSelector = sel_registerName("deltaX");
            s_deltaYSelector = sel_registerName("deltaY");
            if (!InstalledClasses.Contains(viewClass) &&
                !InstallGestureMethods(viewClass))
            {
                return null;
            }

            InstalledClasses.Add(viewClass);
            return new MacOsGestureInputSource(
                contentView,
                handler);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (Gate)
            Handlers.Remove(_view);
    }

    private static bool InstallGestureMethods(nint viewClass)
    {
        const string signature = "v@:@";
        return EnsureMethod(
                viewClass,
                sel_registerName("magnifyWithEvent:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)
                    &OnMagnify,
                signature) &&
            EnsureMethod(
                viewClass,
                sel_registerName("rotateWithEvent:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)
                    &OnRotate,
                signature) &&
            EnsureMethod(
                viewClass,
                sel_registerName("swipeWithEvent:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)
                    &OnSwipe,
                signature);
    }

    private static bool EnsureMethod(
        nint viewClass,
        nint selector,
        nint implementation,
        string signature)
    {
        if (class_addMethod(
                viewClass,
                selector,
                implementation,
                signature))
        {
            return true;
        }

        nint method = class_getInstanceMethod(viewClass, selector);
        return method != 0 &&
            method_getImplementation(method) == implementation;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMagnify(
        nint view,
        nint selector,
        nint nsevent)
    {
        _ = selector;
        double delta = SendDouble(
            nsevent,
            s_magnificationSelector);
        Dispatch(
            view,
            new MacOsGestureEvent(
                MacOsGestureKind.Magnify,
                delta,
                delta));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRotate(
        nint view,
        nint selector,
        nint nsevent)
    {
        _ = selector;
        double delta = SendDouble(nsevent, s_rotationSelector);
        Dispatch(
            view,
            new MacOsGestureEvent(
                MacOsGestureKind.Rotate,
                delta,
                delta));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSwipe(
        nint view,
        nint selector,
        nint nsevent)
    {
        _ = selector;
        Dispatch(
            view,
            new MacOsGestureEvent(
                MacOsGestureKind.Swipe,
                SendDouble(nsevent, s_deltaXSelector),
                SendDouble(nsevent, s_deltaYSelector)));
    }

    private static void Dispatch(
        nint view,
        MacOsGestureEvent gesture)
    {
        Action<MacOsGestureEvent>? handler;
        lock (Gate)
            Handlers.TryGetValue(view, out handler);
        handler?.Invoke(gesture);
    }

    [DllImport(ObjCLibrary)]
    private static extern nint sel_registerName(string name);

    [DllImport(ObjCLibrary)]
    private static extern nint object_getClass(nint value);

    [DllImport(ObjCLibrary)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(
        nint type,
        nint selector,
        nint implementation,
        string signature);

    [DllImport(ObjCLibrary)]
    private static extern nint class_getInstanceMethod(
        nint type,
        nint selector);

    [DllImport(ObjCLibrary)]
    private static extern nint method_getImplementation(nint method);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(
        nint receiver,
        nint selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern double SendDouble(
        nint receiver,
        nint selector);
}
