using System.Runtime.InteropServices;
using Microsoft.Win32;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

internal sealed class Win32NativeWindowPlatform : GlfwNativeWindowPlatform
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int GwlWndProc = -4;
    private const int GwlpHwndParent = -8;
    private const int GclStyle = -26;
    private const long CsDropShadow = 0x00020000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsBorder = 0x00800000L;
    private const long WsDlgFrame = 0x00400000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmNcLButtonDown = 0x00A1;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaMicaEffect = 1029;
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableBlurBehind = 3;
    private const int AccentEnableAcrylicBlurBehind = 4;

    private readonly nint _hwnd;
    private readonly WndProc _wndProc;
    private nint _previousWndProc;
    private bool _extended;
    private bool _canResize = true;
    private NativeWindowBackdrop _backdrop;

    public Win32NativeWindowPlatform(IWindow window, nint hwnd)
        : base(window)
    {
        _hwnd = hwnd;
        _wndProc = WindowProcedure;
    }

    public override NativeWindowHandle Handle => new(NativeWindowKind.Win32, _hwnd, 0, "HWND");
    public override NativeWindowCapabilities Capabilities => NativeWindowCapabilities.ForKind(NativeWindowKind.Win32);
    public override bool RequiresManagedDecorations => true;
    public override NativeDrawnDecorationParts RequestedDrawnDecorations => NativeDrawnDecorationParts.TitleBar;
    public override NativeWindowFrameInsets FrameInsets => GetFrameInsets();
    public override double DefaultTitleBarHeight
    {
        get
        {
            var insets = GetFrameInsets();
            return insets.Top > 0 ? insets.Top : 32d;
        }
    }
    public override bool SupportsManagedMove => true;
    public override bool SupportsManagedResize => true;
    public override bool SupportsSystemChromeExtension => true;

    public override bool ApplyChrome(in NativeWindowState state)
    {
        _extended = state.ExtendClientArea &&
            !SilkWindowController.UsesSystemChrome(
                state.ChromeHints);
        _canResize = state.CanResize;
        var nativeResizable = RequiresNativeResizableStyle(state);
        var style = GetWindowLongPtr(_hwnd, GwlStyle).ToInt64();
        style = SetFlag(style, WsSysMenu, state.CanClose);
        style = SetFlag(style, WsThickFrame, nativeResizable);
        style = SetFlag(style, WsMinimizeBox, state.CanMinimize);
        style = SetFlag(style, WsMaximizeBox, state.CanMaximize && state.CanResize);

        switch (state.Decorations)
        {
            case NativeWindowDecorations.None:
                style &= ~(WsCaption | WsBorder | WsDlgFrame);
                break;
            case NativeWindowDecorations.BorderOnly:
                style &= ~(WsCaption | WsDlgFrame);
                style |= WsBorder;
                break;
            default:
                style |= WsCaption;
                break;
        }

        if (_extended)
        {
            style &= ~WsCaption;
        }

        SetWindowLongPtr(_hwnd, GwlStyle, (nint)style);
        EnsureWindowProcedure(_extended);
        RefreshFrame();
        SetBackdrop(_backdrop);
        return true;
    }

    public override bool SetTopMost(bool value)
    {
        return SetWindowPos(
            _hwnd,
            value ? new nint(-1) : new nint(-2),
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public override bool SetEnabled(bool value) => EnableWindow(_hwnd, value);

    public override bool SetShowInTaskbar(bool value)
    {
        var style = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        style = SetFlag(style, WsExAppWindow, value);
        style = SetFlag(style, WsExToolWindow, !value);
        SetWindowLongPtr(_hwnd, GwlExStyle, (nint)style);
        RefreshFrame();
        return true;
    }

    public override bool SetParent(NativeWindowHandle parent)
    {
        if (parent.IsValid && parent.Kind != NativeWindowKind.Win32)
        {
            return false;
        }

        SetWindowLongPtr(_hwnd, GwlpHwndParent, parent.IsValid ? parent.Handle : 0);
        return true;
    }

    public override bool SetClientAreaExtension(bool enabled, double titleBarHeight)
    {
        _extended = enabled;
        EnsureWindowProcedure(enabled);
        var margins = enabled
            ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 }
            : default;
        _ = DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        RefreshFrame();
        return true;
    }

    public override bool SetTheme(NativeWindowTheme theme)
    {
        var dark = theme == NativeWindowTheme.Dark ||
            theme == NativeWindowTheme.Default &&
            SystemUsesDarkTheme()
                ? 1
                : 0;
        return DwmSetWindowAttribute(_hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) >= 0;
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int appsUseLightTheme &&
                appsUseLightTheme == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override bool SetBackdrop(NativeWindowBackdrop backdrop)
    {
        _backdrop = backdrop;
        var systemBackdrop = backdrop switch
        {
            NativeWindowBackdrop.Mica => 2,
            NativeWindowBackdrop.Blur or NativeWindowBackdrop.Acrylic => 3,
            NativeWindowBackdrop.MicaAlt => 4,
            _ => 1
        };
        var result = DwmSetWindowAttribute(
            _hwnd,
            DwmwaSystemBackdropType,
            ref systemBackdrop,
            sizeof(int));

        var accentState = result < 0
            ? backdrop switch
            {
                NativeWindowBackdrop.Blur => AccentEnableBlurBehind,
                NativeWindowBackdrop.Acrylic => AccentEnableAcrylicBlurBehind,
                _ => AccentDisabled
            }
            : AccentDisabled;
        ApplyAccent(accentState);

        if (result < 0 && backdrop is NativeWindowBackdrop.Mica or NativeWindowBackdrop.MicaAlt)
        {
            var enabled = 1;
            result = DwmSetWindowAttribute(_hwnd, DwmwaMicaEffect, ref enabled, sizeof(int));
        }
        else if (backdrop is not NativeWindowBackdrop.Mica and not NativeWindowBackdrop.MicaAlt)
        {
            var disabled = 0;
            _ = DwmSetWindowAttribute(_hwnd, DwmwaMicaEffect, ref disabled, sizeof(int));
        }

        return backdrop == NativeWindowBackdrop.None || result >= 0 || accentState != AccentDisabled;
    }

    public override bool SetWindowShadow(bool enabled)
    {
        long style =
            GetClassLongPtr(_hwnd, GclStyle)
                .ToInt64();
        style = SetFlag(
            style,
            CsDropShadow,
            enabled);
        SetClassLongPtr(
            _hwnd,
            GclStyle,
            new nint(style));
        RefreshFrame();
        return true;
    }

    public override bool TryBeginMove(NativeWindowPoint pointer)
    {
        ReleaseCapture();
        SendMessage(_hwnd, WmNcLButtonDown, HtCaption, 0);
        return true;
    }

    public override bool TryBeginResize(NativeResizeEdge edge, NativeWindowPoint pointer)
    {
        if (!_canResize)
        {
            return false;
        }

        ReleaseCapture();
        SendMessage(_hwnd, WmNcLButtonDown, MapHitTest(edge), 0);
        return true;
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (_extended && message == WmNcCalcSize)
        {
            return 0;
        }

        if (_extended && _canResize && message == WmNcHitTest)
        {
            var hit = HitTestResizeBorder(lParam);
            if (hit != HtClient)
            {
                return hit;
            }
        }

        return _previousWndProc != 0
            ? CallWindowProc(_previousWndProc, hwnd, message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);
    }

    private int HitTestResizeBorder(nint lParam)
    {
        if (!GetWindowRect(_hwnd, out var rect))
        {
            return HtClient;
        }

        var x = unchecked((short)((long)lParam & 0xffff));
        var y = unchecked((short)(((long)lParam >> 16) & 0xffff));
        var border = 8;
        var left = x >= rect.Left && x < rect.Left + border;
        var right = x <= rect.Right && x > rect.Right - border;
        var top = y >= rect.Top && y < rect.Top + border;
        var bottom = y <= rect.Bottom && y > rect.Bottom - border;
        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return HtClient;
    }

    private void EnsureWindowProcedure(bool enabled)
    {
        if (enabled && _previousWndProc == 0)
        {
            _previousWndProc = SetWindowLongPtr(
                _hwnd,
                GwlWndProc,
                Marshal.GetFunctionPointerForDelegate(_wndProc));
        }
        else if (!enabled && _previousWndProc != 0)
        {
            SetWindowLongPtr(_hwnd, GwlWndProc, _previousWndProc);
            _previousWndProc = 0;
        }
    }

    private NativeWindowFrameInsets GetFrameInsets()
    {
        if (!GetWindowRect(_hwnd, out var windowRect) ||
            !GetClientRect(_hwnd, out var clientRect))
        {
            return base.FrameInsets;
        }

        var clientOrigin = new Point();
        if (!ClientToScreen(_hwnd, ref clientOrigin))
        {
            return base.FrameInsets;
        }

        return new NativeWindowFrameInsets(
            clientOrigin.X - windowRect.Left,
            clientOrigin.Y - windowRect.Top,
            windowRect.Right - (clientOrigin.X + clientRect.Right),
            windowRect.Bottom - (clientOrigin.Y + clientRect.Bottom));
    }

    private void ApplyAccent(int accentState)
    {
        var policy = new AccentPolicy
        {
            AccentState = accentState,
            GradientColor = unchecked((int)0xCCFFFFFF)
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = pointer,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(_hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private void RefreshFrame()
    {
        SetWindowPos(
            _hwnd,
            0,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private static long SetFlag(long value, long flag, bool enabled) => enabled ? value | flag : value & ~flag;

    private static int MapHitTest(NativeResizeEdge edge) => edge switch
    {
        NativeResizeEdge.Left => HtLeft,
        NativeResizeEdge.Top => HtTop,
        NativeResizeEdge.Right => HtRight,
        NativeResizeEdge.Bottom => HtBottom,
        NativeResizeEdge.TopLeft => HtTopLeft,
        NativeResizeEdge.TopRight => HtTopRight,
        NativeResizeEdge.BottomLeft => HtBottomLeft,
        _ => HtBottomRight
    };

    public override void Dispose()
    {
        EnsureWindowProcedure(false);
        base.Dispose();
    }

    private static nint GetWindowLongPtr(nint hwnd, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    private static nint SetWindowLongPtr(nint hwnd, int index, nint value) =>
        nint.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, value);

    private static nint GetClassLongPtr(nint hwnd, int index) =>
        nint.Size == 8 ? GetClassLongPtr64(hwnd, index) : new nint(GetClassLong32(hwnd, index));

    private static nint SetClassLongPtr(nint hwnd, int index, nint value) =>
        nint.Size == 8 ? SetClassLongPtr64(hwnd, index, value) : new nint(SetClassLong32(hwnd, index, value.ToInt32()));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern nint GetWindowLong32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern nint SetWindowLong32(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    private static extern nint GetClassLongPtr64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetClassLongW", SetLastError = true)]
    private static extern uint GetClassLong32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern nint SetClassLongPtr64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
    private static extern uint SetClassLong32(nint hwnd, int index, int value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool EnableWindow(nint hwnd, bool enabled);
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint previous, nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
