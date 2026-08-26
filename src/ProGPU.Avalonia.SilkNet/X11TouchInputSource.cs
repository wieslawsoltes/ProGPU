using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.Windowing;

namespace Avalonia.SilkNet;

/// <summary>
/// Selects XI2 touch events on a dedicated X11 connection so GLFW's earlier
/// per-client XI2 negotiation cannot constrain touch support to version 2.0.
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
    private static readonly bool TraceEnabled = string.Equals(
        Environment.GetEnvironmentVariable(
            "PROGPU_AVALONIA_TRACE_TOUCH"),
        "1",
        StringComparison.Ordinal);
    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable(
            "PROGPU_AVALONIA_TRACE_WINDOW_EVENTS_PATH");
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
            Trace(
                $"unavailable native={window.Native is not null} " +
                $"x11={window.Native?.X11 is not null}");
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
                    {
                        Trace($"pump unavailable display={x11.Display}");
                        return null;
                    }
                    Pumps.Add(x11.Display, pump);
                }

                if (!pump.Register(x11.Window, handler))
                {
                    if (pump.Count == 0)
                    {
                        Pumps.Remove(x11.Display);
                        pump.Dispose();
                    }
                    return null;
                }

                Trace($"registered display={x11.Display} window={x11.Window}");

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
            {
                Pumps.Remove(_pump.SharedDisplay);
                _pump.Dispose();
            }
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

        private Pump(
            nint sharedDisplay,
            nint display,
            int extension)
        {
            SharedDisplay = sharedDisplay;
            Display = display;
            _extension = extension;
        }

        internal nint SharedDisplay { get; }
        internal nint Display { get; }
        internal int Count => _handlers.Count;

        internal static Pump? TryCreate(nint sharedDisplay)
        {
            nint displayNamePointer = XDisplayString(sharedDisplay);
            string? displayName = displayNamePointer == 0
                ? null
                : Marshal.PtrToStringAnsi(displayNamePointer);
            nint display = XOpenDisplay(displayName);
            if (display == 0)
            {
                Trace($"XOpenDisplay failed name={displayName}");
                return null;
            }

            if (XQueryExtension(
                    display,
                    "XInputExtension",
                    out int extension,
                    out _,
                    out _) == 0)
            {
                Trace("XInputExtension unavailable");
                _ = XCloseDisplay(display);
                return null;
            }

            int major = 2;
            int minor = 2;
            int status = XIQueryVersion(
                display,
                ref major,
                ref minor);
            Trace($"XIQueryVersion status={status} version={major}.{minor}");
            if (status == 0 &&
                (major > 2 || major == 2 && minor >= 2))
            {
                return new Pump(
                    sharedDisplay,
                    display,
                    extension);
            }

            _ = XCloseDisplay(display);
            return null;
        }

        internal void Dispose() => _ = XCloseDisplay(Display);

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
            int status = XISelectEvents(Display, window, &mask, 1);
            Trace($"XISelectEvents status={status} window={window}");
            if (status != 0)
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

                Trace(
                    $"event cookie type={xevent.Cookie.EventType} " +
                    $"extension={xevent.Cookie.Extension}");

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
                Trace(
                    $"unhandled event window={touch.Event} " +
                    $"root={touch.Root}");
                return;
            }

            Trace(
                $"dispatch type={touch.EventType} id={touch.Detail} " +
                $"window={touch.Event} position={touch.EventX},{touch.EventY}");

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

    private static void Trace(string message)
    {
        if (!TraceEnabled)
            return;
        string line = "[Avalonia.SilkNet] X11 touch: " + message;
        if (string.IsNullOrWhiteSpace(TracePath))
        {
            Console.Error.WriteLine(line);
            return;
        }

        try
        {
            File.AppendAllText(TracePath, line + Environment.NewLine);
        }
        catch (IOException)
        {
            Console.Error.WriteLine(line);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine(line);
        }
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
    private static extern nint XDisplayString(nint display);

    [DllImport(X11Library)]
    private static extern nint XOpenDisplay(string? displayName);

    [DllImport(X11Library)]
    private static extern int XCloseDisplay(nint display);

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
