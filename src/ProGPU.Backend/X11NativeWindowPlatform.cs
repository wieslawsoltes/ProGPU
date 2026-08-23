using System.Runtime.InteropServices;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

internal sealed unsafe class X11NativeWindowPlatform : GlfwNativeWindowPlatform
{
    private const string X11Library = "libX11.so.6";
    private const int ClientMessage = 33;
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;
    private const int PropModeReplace = 0;
    private const nuint XaAtom = 4;
    private const nuint XaCardinal = 6;
    private const nuint MotifHintsFunctions = 1;
    private const nuint MotifHintsDecorations = 2;
    private const nuint MotifFunctionResize = (nuint)1 << 1;
    private const nuint MotifFunctionMove = (nuint)1 << 2;
    private const nuint MotifFunctionMinimize = (nuint)1 << 3;
    private const nuint MotifFunctionMaximize = (nuint)1 << 4;
    private const nuint MotifFunctionClose = (nuint)1 << 5;
    private const nuint MotifDecorationBorder = (nuint)1 << 1;
    private const nuint MotifDecorationResize = (nuint)1 << 2;
    private const nuint MotifDecorationTitle = (nuint)1 << 3;
    private const nuint MotifDecorationMenu = (nuint)1 << 4;
    private const nuint MotifDecorationMinimize = (nuint)1 << 5;
    private const nuint MotifDecorationMaximize = (nuint)1 << 6;

    private readonly nint _display;
    private readonly nuint _window;

    public X11NativeWindowPlatform(IWindow silkWindow, nint display, nuint window)
        : base(silkWindow)
    {
        _display = display;
        _window = window;
    }

    public override NativeWindowHandle Handle => new(NativeWindowKind.X11, (nint)_window, _display, "XID");
    public override NativeWindowCapabilities Capabilities => NativeWindowCapabilities.ForKind(NativeWindowKind.X11);
    public override bool RequiresManagedDecorations => true;
    public override NativeDrawnDecorationParts RequestedDrawnDecorations =>
        NativeDrawnDecorationParts.TitleBar |
        NativeDrawnDecorationParts.Border |
        NativeDrawnDecorationParts.ResizeGrips |
        NativeDrawnDecorationParts.Shadow;
    public override bool SupportsManagedMove => true;
    public override bool SupportsManagedResize => true;

    public override bool ApplyChrome(in NativeWindowState state)
    {
        base.ApplyChrome(state);
        var nativeResizable = RequiresNativeResizableStyle(state);
        var functions = MotifFunctionMove;
        if (state.CanClose)
        {
            functions |= MotifFunctionClose;
        }
        if (nativeResizable)
        {
            functions |= MotifFunctionResize;
        }
        if (state.CanMinimize)
        {
            functions |= MotifFunctionMinimize;
        }
        if (state.CanMaximize && state.CanResize)
        {
            functions |= MotifFunctionMaximize;
        }

        nuint decorations = 0;
        if (!state.ExtendClientArea ||
            SilkWindowController.UsesSystemChrome(
                state.ChromeHints))
        {
            decorations = state.Decorations switch
            {
                NativeWindowDecorations.BorderOnly => MotifDecorationBorder |
                    (nativeResizable ? MotifDecorationResize : 0),
                NativeWindowDecorations.Full => MotifDecorationBorder |
                    MotifDecorationTitle |
                    (state.CanClose ? MotifDecorationMenu : 0) |
                    (nativeResizable ? MotifDecorationResize : 0) |
                    (state.CanMinimize ? MotifDecorationMinimize : 0) |
                    (state.CanMaximize && state.CanResize ? MotifDecorationMaximize : 0),
                _ => 0
            };
        }

        var hints = new MotifHints
        {
            Flags = MotifHintsFunctions | MotifHintsDecorations,
            Functions = functions,
            Decorations = decorations
        };
        var atom = XInternAtom(_display, "_MOTIF_WM_HINTS", false);
        XChangeProperty(
            _display,
            _window,
            atom,
            atom,
            32,
            PropModeReplace,
            (byte*)&hints,
            5);
        XFlush(_display);
        return true;
    }

    public override bool SetTopMost(bool value)
    {
        base.SetTopMost(value);
        return SendWindowState("_NET_WM_STATE_ABOVE", value);
    }

    public override bool SetZOrder(NativeWindowZOrder value)
    {
        int result = value switch
        {
            NativeWindowZOrder.Front => XRaiseWindow(_display, _window),
            NativeWindowZOrder.Back => XLowerWindow(_display, _window),
            _ => 0
        };
        XFlush(_display);
        return result != 0;
    }

    public override bool SetShowInTaskbar(bool value) =>
        SendWindowState("_NET_WM_STATE_SKIP_TASKBAR", !value);

    public override bool SetParent(NativeWindowHandle parent)
    {
        if (!parent.IsValid)
        {
            var transientFor = XInternAtom(_display, "WM_TRANSIENT_FOR", false);
            XDeleteProperty(_display, _window, transientFor);
            XFlush(_display);
            return true;
        }

        return parent.Kind == NativeWindowKind.X11 &&
            XSetTransientForHint(_display, _window, (nuint)parent.Handle) != 0;
    }

    public override bool SetClientAreaExtension(bool enabled, double titleBarHeight) => true;

    public override bool SetTheme(NativeWindowTheme theme)
    {
        var atom = XInternAtom(_display, "_GTK_THEME_VARIANT", false);
        if (theme == NativeWindowTheme.Default)
        {
            XDeleteProperty(_display, _window, atom);
            XFlush(_display);
            return true;
        }

        var utf8 = XInternAtom(_display, "UTF8_STRING", false);
        var value = theme == NativeWindowTheme.Dark ? "dark" : "light";
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        fixed (byte* data = bytes)
        {
            XChangeProperty(
                _display,
                _window,
                atom,
                utf8,
                8,
                PropModeReplace,
                data,
                bytes.Length);
        }
        XFlush(_display);
        return true;
    }

    public override bool SetBackdrop(NativeWindowBackdrop backdrop)
    {
        var blurAtom = XInternAtom(_display, "_KDE_NET_WM_BLUR_BEHIND_REGION", false);
        if (backdrop is NativeWindowBackdrop.Blur or NativeWindowBackdrop.Acrylic)
        {
            XChangeProperty(
                _display,
                _window,
                blurAtom,
                XaCardinal,
                32,
                PropModeReplace,
                null,
                0);
            XFlush(_display);
            return true;
        }

        XDeleteProperty(_display, _window, blurAtom);
        XFlush(_display);
        return backdrop is NativeWindowBackdrop.None or NativeWindowBackdrop.Transparent;
    }

    public override bool TryBeginMove(NativeWindowPoint pointer) => SendMoveResize(pointer, 8);

    public override bool TryBeginResize(NativeResizeEdge edge, NativeWindowPoint pointer)
    {
        var direction = edge switch
        {
            NativeResizeEdge.TopLeft => 0,
            NativeResizeEdge.Top => 1,
            NativeResizeEdge.TopRight => 2,
            NativeResizeEdge.Right => 3,
            NativeResizeEdge.BottomRight => 4,
            NativeResizeEdge.Bottom => 5,
            NativeResizeEdge.BottomLeft => 6,
            NativeResizeEdge.Left => 7,
            _ => 4
        };
        return SendMoveResize(pointer, direction);
    }

    private bool SendWindowState(string stateName, bool enabled)
    {
        var stateAtom = XInternAtom(_display, "_NET_WM_STATE", false);
        var requestedState = XInternAtom(_display, stateName, false);
        var data = stackalloc long[5];
        data[0] = enabled ? 1 : 0;
        data[1] = (long)requestedState;
        data[2] = 0;
        data[3] = 1;
        data[4] = 0;
        return SendClientMessage(stateAtom, data);
    }

    private bool SendMoveResize(NativeWindowPoint pointer, int direction)
    {
        XUngrabPointer(_display, 0);
        var atom = XInternAtom(_display, "_NET_WM_MOVERESIZE", false);
        var data = stackalloc long[5];
        data[0] = pointer.X;
        data[1] = pointer.Y;
        data[2] = direction;
        data[3] = 1;
        data[4] = 1;
        return SendClientMessage(atom, data);
    }

    private bool SendClientMessage(nuint messageType, long* values)
    {
        var root = XDefaultRootWindow(_display);
        var clientMessage = new XClientMessageEvent
        {
            Type = ClientMessage,
            SendEvent = 1,
            Display = _display,
            Window = _window,
            MessageType = messageType,
            Format = 32
        };
        for (var index = 0; index < 5; index++)
        {
            clientMessage.Data[index] = values[index];
        }

        var xevent = new XEvent { ClientMessage = clientMessage };
        var result = XSendEvent(
            _display,
            root,
            false,
            SubstructureRedirectMask | SubstructureNotifyMask,
            ref xevent);
        XFlush(_display);
        return result != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MotifHints
    {
        public nuint Flags;
        public nuint Functions;
        public nuint Decorations;
        public nint InputMode;
        public nuint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct XClientMessageData
    {
        public fixed long Values[5];

        public long this[int index]
        {
            get
            {
                fixed (long* values = Values)
                {
                    return values[index];
                }
            }
            set
            {
                fixed (long* values = Values)
                {
                    values[index] = value;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClientMessageEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public nuint Window;
        public nuint MessageType;
        public int Format;
        public XClientMessageData Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)]
        public XClientMessageEvent ClientMessage;
    }

    [DllImport(X11Library)]
    private static extern nuint XInternAtom(nint display, string atomName, bool onlyIfExists);
    [DllImport(X11Library)]
    private static extern int XChangeProperty(
        nint display,
        nuint window,
        nuint property,
        nuint type,
        int format,
        int mode,
        byte* data,
        int elementCount);
    [DllImport(X11Library)]
    private static extern int XDeleteProperty(nint display, nuint window, nuint property);
    [DllImport(X11Library)]
    private static extern int XSetTransientForHint(nint display, nuint window, nuint parent);
    [DllImport(X11Library)]
    private static extern nuint XDefaultRootWindow(nint display);
    [DllImport(X11Library)]
    private static extern int XSendEvent(
        nint display,
        nuint window,
        bool propagate,
        long eventMask,
        ref XEvent sendEvent);
    [DllImport(X11Library)]
    private static extern int XUngrabPointer(nint display, nuint time);
    [DllImport(X11Library)]
    private static extern int XRaiseWindow(nint display, nuint window);
    [DllImport(X11Library)]
    private static extern int XLowerWindow(nint display, nuint window);
    [DllImport(X11Library)]
    private static extern int XFlush(nint display);
}
