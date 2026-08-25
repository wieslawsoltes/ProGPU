using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.Windowing;

namespace Avalonia.SilkNet;

/// <summary>
/// Selects XI2 touch events on GLFW's X11 connection and removes only those
/// events before GLFW drains the shared Xlib queue.
/// </summary>
internal sealed unsafe class X11TouchInputSource : IDisposable
{
    private const string X11Library = "libX11.so.6";
    private const string XiLibrary = "libXi.so.6";
    private const int GenericEvent = 35;
    private const int XiAllMasterDevices = 1;
    private const int XiTouchBegin = 18;
    private const int XiTouchUpdate = 19;
    private const int XiTouchEnd = 20;
    private const int XiTouchEmulatingPointer = 1 << 17;
    private static readonly object Gate = new();
    private static readonly Dictionary<nint, Pump> Pumps = [];
    private static readonly XEventPredicate TouchPredicate = IsTouchEvent;

    private readonly Pump _pump;
    private readonly nuint _window;
    private bool _disposed;

    private X11TouchInputSource(
        Pump pump,
        nuint window)
    {
        _pump = pump;
        _window = window;
    }

    internal static X11TouchInputSource? TryCreate(
        IWindow window,
        Action<NativeTouchEvent> handler)
    {
        if (!OperatingSystem.IsLinux() ||
            window.Native?.X11 is not { } x11 ||
            x11.Display == 0 ||
            x11.Window == 0)
        {
            return null;
        }

        try
        {
            lock (Gate)
            {
                if (!Pumps.TryGetValue(x11.Display, out Pump? pump))
                {
                    pump = Pump.TryCreate(x11.Display);
                    if (pump is null)
                        return null;
                    Pumps.Add(x11.Display, pump);
                }

                if (!pump.Register(x11.Window, handler))
                {
                    if (pump.Count == 0)
                        Pumps.Remove(x11.Display);
                    return null;
                }

                return new X11TouchInputSource(pump, x11.Window);
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal void Poll() => _pump.Poll();

    internal static int NativeEventSize => sizeof(XEvent);
    internal static int NativeCookieSize =>
        sizeof(XGenericEventCookie);
    internal static int NativeDeviceEventSize =>
        sizeof(XiDeviceEvent);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (Gate)
        {
            _pump.Unregister(_window);
            if (_pump.Count == 0)
                Pumps.Remove(_pump.Display);
        }
    }

    private static int IsTouchEvent(
        nint display,
        XEvent* xevent,
        nint extensionArgument)
    {
        _ = display;
        ref XGenericEventCookie cookie = ref xevent->Cookie;
        return cookie.Type == GenericEvent &&
            cookie.Extension == (int)extensionArgument &&
            cookie.EventType is >= XiTouchBegin and <= XiTouchEnd
                ? 1
                : 0;
    }

    private sealed class Pump
    {
        private readonly int _extension;
        private readonly Dictionary<nuint, Action<NativeTouchEvent>>
            _handlers = [];

        private Pump(nint display, int extension)
        {
            Display = display;
            _extension = extension;
        }

        internal nint Display { get; }
        internal int Count => _handlers.Count;

        internal static Pump? TryCreate(nint display)
        {
            if (XQueryExtension(
                    display,
                    "XInputExtension",
                    out int extension,
                    out _,
                    out _) == 0)
            {
                return null;
            }

            int major = 2;
            int minor = 2;
            return XIQueryVersion(display, ref major, ref minor) == 0 &&
                (major > 2 || major == 2 && minor >= 2)
                    ? new Pump(display, extension)
                    : null;
        }

        internal bool Register(
            nuint window,
            Action<NativeTouchEvent> handler)
        {
            byte* eventMask = stackalloc byte[3];
            eventMask[0] = 0;
            eventMask[1] = 0;
            eventMask[2] = 0;
            SetMask(eventMask, XiTouchBegin);
            SetMask(eventMask, XiTouchUpdate);
            SetMask(eventMask, XiTouchEnd);
            var mask = new XiEventMask
            {
                DeviceId = XiAllMasterDevices,
                MaskLength = 3,
                Mask = eventMask
            };
            if (XISelectEvents(Display, window, &mask, 1) != 0)
                return false;
            _ = XFlush(Display);
            _handlers[window] = handler;
            return true;
        }

        internal void Unregister(nuint window)
        {
            _handlers.Remove(window);
            var mask = new XiEventMask
            {
                DeviceId = XiAllMasterDevices,
                MaskLength = 0,
                Mask = null
            };
            _ = XISelectEvents(Display, window, &mask, 1);
            _ = XFlush(Display);
        }

        internal void Poll()
        {
            while (true)
            {
                XEvent xevent = default;
                if (XCheckIfEvent(
                        Display,
                        &xevent,
                        TouchPredicate,
                        _extension) == 0)
                {
                    return;
                }

                XGenericEventCookie* cookie = &xevent.Cookie;
                if (XGetEventData(Display, cookie) == 0)
                    continue;
                try
                {
                    Dispatch(*(XiDeviceEvent*)cookie->Data);
                }
                finally
                {
                    XFreeEventData(Display, cookie);
                }
            }
        }

        private void Dispatch(in XiDeviceEvent touch)
        {
            if (!_handlers.TryGetValue(
                    touch.Event,
                    out Action<NativeTouchEvent>? handler))
            {
                return;
            }

            NativeTouchPhase phase = touch.EventType switch
            {
                XiTouchBegin => NativeTouchPhase.Begin,
                XiTouchUpdate => NativeTouchPhase.Update,
                _ => NativeTouchPhase.End
            };
            handler(new NativeTouchEvent(
                unchecked((uint)touch.Detail),
                phase,
                touch.EventX,
                touch.EventY,
                0d,
                0d,
                unchecked((uint)touch.Time),
                (touch.Flags & XiTouchEmulatingPointer) != 0));
        }

        private static void SetMask(byte* mask, int eventType) =>
            mask[eventType >> 3] |=
                (byte)(1 << (eventType & 7));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XEventPredicate(
        nint display,
        XEvent* xevent,
        nint argument);

    [StructLayout(LayoutKind.Sequential)]
    private struct XiEventMask
    {
        public int DeviceId;
        public int MaskLength;
        public byte* Mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XiButtonState
    {
        public int MaskLength;
        public byte* Mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XiValuatorState
    {
        public int MaskLength;
        public byte* Mask;
        public double* Values;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XiModifierState
    {
        public int Base;
        public int Latched;
        public int Locked;
        public int Effective;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XiDeviceEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public int Extension;
        public int EventType;
        public nuint Time;
        public int DeviceId;
        public int SourceId;
        public int Detail;
        public nuint Root;
        public nuint Event;
        public nuint Child;
        public double RootX;
        public double RootY;
        public double EventX;
        public double EventY;
        public int Flags;
        public XiButtonState Buttons;
        public XiValuatorState Valuators;
        public XiModifierState Modifiers;
        public XiModifierState Group;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XGenericEventCookie
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public int Extension;
        public int EventType;
        public uint CookieId;
        public nint Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)]
        public XGenericEventCookie Cookie;
    }

    [DllImport(X11Library)]
    private static extern int XQueryExtension(
        nint display,
        string name,
        out int majorOpcode,
        out int firstEvent,
        out int firstError);

    [DllImport(X11Library)]
    private static extern int XCheckIfEvent(
        nint display,
        XEvent* xevent,
        XEventPredicate predicate,
        nint argument);

    [DllImport(X11Library)]
    private static extern int XGetEventData(
        nint display,
        XGenericEventCookie* cookie);

    [DllImport(X11Library)]
    private static extern void XFreeEventData(
        nint display,
        XGenericEventCookie* cookie);

    [DllImport(X11Library)]
    private static extern int XFlush(nint display);

    [DllImport(XiLibrary)]
    private static extern int XIQueryVersion(
        nint display,
        ref int major,
        ref int minor);

    [DllImport(XiLibrary)]
    private static extern int XISelectEvents(
        nint display,
        nuint window,
        XiEventMask* masks,
        int maskCount);
}
