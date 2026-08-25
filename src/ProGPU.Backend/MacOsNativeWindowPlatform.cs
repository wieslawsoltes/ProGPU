using System.Runtime.InteropServices;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

internal sealed class MacOsNativeWindowPlatform : GlfwNativeWindowPlatform
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const ulong StyleTitled = 1UL << 0;
    private const ulong StyleClosable = 1UL << 1;
    private const ulong StyleMiniaturizable = 1UL << 2;
    private const ulong StyleResizable = 1UL << 3;
    private const ulong StyleFullSizeContentView = 1UL << 15;
    private const ulong ViewWidthSizable = 1UL << 1;
    private const ulong ViewHeightSizable = 1UL << 4;
    private const long WindowBelow = -1;
    private const long NormalWindowLevel = 0;
    private const long FloatingWindowLevel = 3;

    private readonly nint _nsWindow;
    private nint _visualEffectView;
    private nint _toolbar;
    private NativeWindowState _state = NativeWindowState.Default;
    private nint _parentWindow;
    private bool _enabled = true;

    public MacOsNativeWindowPlatform(IWindow window, nint nsWindow)
        : base(window)
    {
        _nsWindow = nsWindow;
    }

    public override NativeWindowHandle Handle => new(NativeWindowKind.Cocoa, _nsWindow, 0, "NSWindow");
    public override NativeWindowCapabilities Capabilities => NativeWindowCapabilities.ForKind(NativeWindowKind.Cocoa);
    public override bool RequiresManagedDecorations => false;
    public override NativeWindowFrameInsets FrameInsets => base.FrameInsets;
    public override double DefaultTitleBarHeight =>
        Math.Max(22d, base.FrameInsets.Top);
    public override bool SupportsManagedMove => true;
    public override bool SupportsManagedResize => true;
    public override bool SupportsSystemChromeExtension => true;

    public override bool ApplyChrome(in NativeWindowState state)
    {
        _state = state;
        var nativeResizable = RequiresNativeResizableStyle(state);
        var style = SendUInt64(_nsWindow, "styleMask");
        style &= ~(StyleTitled | StyleClosable | StyleMiniaturizable | StyleResizable | StyleFullSizeContentView);
        switch (state.Decorations)
        {
            case NativeWindowDecorations.None:
                if (state.IsPopup)
                {
                    // Popup surfaces (ComboBox/ContextMenu/ToolTip drop-downs) must be truly
                    // borderless: NSWindowStyleMaskTitled forces the OS-drawn rounded window
                    // corners even with the title bar hidden/transparent, which real WPF popups
                    // never have. Leave styleMask cleared (NSWindowStyleMaskBorderless = 0) so
                    // the popup renders as a plain rectangle, matching Win32 popup chrome.
                    break;
                }
                style |= StyleTitled | StyleFullSizeContentView;
                if (nativeResizable)
                {
                    style |= StyleResizable;
                }
                break;
            case NativeWindowDecorations.BorderOnly:
                style |= StyleTitled | StyleFullSizeContentView;
                if (nativeResizable && _enabled)
                {
                    style |= StyleResizable;
                }
                break;
            case NativeWindowDecorations.Full:
                style |= StyleTitled | StyleClosable;
                if (state.CanMinimize)
                {
                    style |= StyleMiniaturizable;
                }
                if (nativeResizable && _enabled)
                {
                    style |= StyleResizable;
                }
                break;
        }
        if (state.ExtendClientArea && state.Decorations != NativeWindowDecorations.None)
        {
            style |= StyleFullSizeContentView;
        }

        SendVoidUInt64(_nsWindow, "setStyleMask:", style);
        bool wantsSystemChrome =
            (state.ChromeHints &
             (NativeWindowChromeHints.SystemChrome |
              NativeWindowChromeHints.PreferSystemChrome)) != 0;
        var hideNativeTitle = state.ExtendClientArea || state.Decorations != NativeWindowDecorations.Full;
        SendVoidBool(_nsWindow, "setTitlebarAppearsTransparent:", hideNativeTitle);
        SendVoidInt64(_nsWindow, "setTitleVisibility:", hideNativeTitle ? 1 : 0);
        SendVoidBool(_nsWindow, "setMovableByWindowBackground:", false);
        SendVoidBool(_nsWindow, "setHasShadow:", state.Decorations != NativeWindowDecorations.None);
        var showButtons =
            state.Decorations == NativeWindowDecorations.Full &&
            (!state.ExtendClientArea || wantsSystemChrome);
        SetStandardButtonState(0, showButtons, _enabled);
        SetStandardButtonState(1, showButtons, state.CanMinimize && _enabled);
        SetStandardButtonState(2, showButtons, state.CanMaximize && state.CanResize && _enabled);
        ApplyToolbar(
            state.ExtendClientArea &&
            (state.ChromeHints &
             NativeWindowChromeHints.MacOsThickTitleBar) != 0 &&
            Window.WindowState != WindowState.Fullscreen);
        return true;
    }

    public override bool SetTopMost(bool value)
    {
        SendVoidInt64(_nsWindow, "setLevel:", value ? FloatingWindowLevel : NormalWindowLevel);
        return true;
    }

    public override bool SetEnabled(bool value)
    {
        _enabled = value;
        return ApplyChrome(_state);
    }

    public override bool SetParent(NativeWindowHandle parent)
    {
        if (_parentWindow != 0)
        {
            SendVoidObject(_parentWindow, "removeChildWindow:", _nsWindow);
            _parentWindow = 0;
        }

        if (!parent.IsValid)
        {
            return true;
        }
        if (parent.Kind != NativeWindowKind.Cocoa)
        {
            return false;
        }

        _parentWindow = parent.Handle;
        SendVoidObjectInt64(parent.Handle, "addChildWindow:ordered:", _nsWindow, 1);
        return true;
    }

    public override bool SetClientAreaExtension(bool enabled, double titleBarHeight)
    {
        var style = SendUInt64(_nsWindow, "styleMask");
        style = enabled ? style | StyleFullSizeContentView : style & ~StyleFullSizeContentView;
        SendVoidUInt64(_nsWindow, "setStyleMask:", style);
        SendVoidBool(_nsWindow, "setTitlebarAppearsTransparent:", enabled);
        SendVoidInt64(_nsWindow, "setTitleVisibility:", enabled ? 1 : 0);
        return true;
    }

    public override bool SetTheme(NativeWindowTheme theme)
    {
        if (theme == NativeWindowTheme.Default)
        {
            SendVoidObject(_nsWindow, "setAppearance:", 0);
            return true;
        }

        var appearanceClass = objc_getClass("NSAppearance");
        var appearanceName = CreateNSString(
            theme == NativeWindowTheme.Dark
                ? "NSAppearanceNameDarkAqua"
                : "NSAppearanceNameAqua");
        var appearance = SendObjectObject(appearanceClass, "appearanceNamed:", appearanceName);
        SendVoidObject(_nsWindow, "setAppearance:", appearance);
        return true;
    }

    public override bool SetBackdrop(NativeWindowBackdrop backdrop)
    {
        RemoveVisualEffectView();
        var transparent = backdrop != NativeWindowBackdrop.None;
        SendVoidBool(_nsWindow, "setOpaque:", !transparent);
        if (transparent)
        {
            var colorClass = objc_getClass("NSColor");
            var clearColor = SendObject(colorClass, "clearColor");
            SendVoidObject(_nsWindow, "setBackgroundColor:", clearColor);
        }
        else
        {
            var colorClass = objc_getClass("NSColor");
            var backgroundColor = SendObject(colorClass, "windowBackgroundColor");
            SendVoidObject(_nsWindow, "setBackgroundColor:", backgroundColor);
        }

        if (backdrop is NativeWindowBackdrop.None or NativeWindowBackdrop.Transparent)
        {
            return true;
        }

        var contentView = SendObject(_nsWindow, "contentView");
        var effectClass = objc_getClass("NSVisualEffectView");
        if (contentView == 0 || effectClass == 0)
        {
            return false;
        }

        var bounds = SendRect(contentView, "bounds");
        var allocated = SendObject(effectClass, "alloc");
        _visualEffectView = SendObjectRect(allocated, "initWithFrame:", bounds);
        if (_visualEffectView == 0)
        {
            return false;
        }

        SendVoidUInt64(_visualEffectView, "setAutoresizingMask:", ViewWidthSizable | ViewHeightSizable);
        SendVoidInt64(_visualEffectView, "setBlendingMode:", 0);
        SendVoidInt64(_visualEffectView, "setState:", 1);
        SendVoidInt64(_visualEffectView, "setMaterial:", MapMaterial(backdrop));
        SendVoidObjectInt64Object(
            contentView,
            "addSubview:positioned:relativeTo:",
            _visualEffectView,
            WindowBelow,
            0);
        return true;
    }

    public override bool SetWindowShadow(bool enabled)
    {
        SendVoidBool(
            _nsWindow,
            "setHasShadow:",
            enabled);
        return true;
    }

    public override bool TryBeginMove(NativeWindowPoint pointer)
    {
        var applicationClass = objc_getClass("NSApplication");
        var application = SendObject(applicationClass, "sharedApplication");
        var currentEvent = SendObject(application, "currentEvent");
        if (currentEvent == 0)
        {
            return false;
        }

        SendVoidObject(_nsWindow, "performWindowDragWithEvent:", currentEvent);
        return true;
    }

    public override bool TryBeginResize(NativeResizeEdge edge, NativeWindowPoint pointer) => false;

    private void SetStandardButtonState(long button, bool visible, bool enabled)
    {
        var control = SendObjectInt64(_nsWindow, "standardWindowButton:", button);
        if (control != 0)
        {
            SendVoidBool(control, "setHidden:", !visible);
            SendVoidBool(control, "setEnabled:", enabled);
        }
    }

    private void RemoveVisualEffectView()
    {
        if (_visualEffectView == 0)
        {
            return;
        }

        SendVoid(_visualEffectView, "removeFromSuperview");
        SendVoid(_visualEffectView, "release");
        _visualEffectView = 0;
    }

    private void ApplyToolbar(bool enabled)
    {
        if (enabled && _toolbar == 0)
        {
            nint toolbarClass = objc_getClass("NSToolbar");
            nint allocated = SendObject(toolbarClass, "alloc");
            nint identifier = CreateNSString(
                "ProGPU.Avalonia.ThickTitleBar");
            _toolbar = SendObjectObject(
                allocated,
                "initWithIdentifier:",
                identifier);
            if (_toolbar != 0)
            {
                SendVoidBool(
                    _toolbar,
                    "setShowsBaselineSeparator:",
                    false);
                SendVoidObject(
                    _nsWindow,
                    "setToolbar:",
                    _toolbar);
            }
        }
        else if (!enabled && _toolbar != 0)
        {
            SendVoidObject(_nsWindow, "setToolbar:", 0);
            SendVoid(_toolbar, "release");
            _toolbar = 0;
        }
    }

    private static long MapMaterial(NativeWindowBackdrop backdrop) => backdrop switch
    {
        NativeWindowBackdrop.Blur => 12,
        NativeWindowBackdrop.Acrylic => 18,
        NativeWindowBackdrop.Mica => 21,
        NativeWindowBackdrop.MicaAlt => 22,
        _ => 0
    };

    private static nint CreateNSString(string value)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return objc_msgSend_ObjectObject(
                objc_getClass("NSString"),
                sel_registerName("stringWithUTF8String:"),
                pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static nint SendObject(nint receiver, string selector) =>
        objc_msgSend_Object(receiver, sel_registerName(selector));
    private static nint SendObjectObject(nint receiver, string selector, nint value) =>
        objc_msgSend_ObjectObject(receiver, sel_registerName(selector), value);
    private static nint SendObjectInt64(nint receiver, string selector, long value) =>
        objc_msgSend_ObjectInt64(receiver, sel_registerName(selector), value);
    private static nint SendObjectRect(nint receiver, string selector, NativeRect value) =>
        objc_msgSend_ObjectRect(receiver, sel_registerName(selector), value);
    private static ulong SendUInt64(nint receiver, string selector) =>
        objc_msgSend_UInt64(receiver, sel_registerName(selector));
    private static NativeRect SendRect(nint receiver, string selector)
    {
        var registeredSelector = sel_registerName(selector);
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            objc_msgSend_stret_Rect(out var result, receiver, registeredSelector);
            return result;
        }
        return objc_msgSend_Rect(receiver, registeredSelector);
    }
    private static void SendVoid(nint receiver, string selector) =>
        objc_msgSend_Void(receiver, sel_registerName(selector));
    private static void SendVoidBool(nint receiver, string selector, bool value) =>
        objc_msgSend_VoidBool(receiver, sel_registerName(selector), value);
    private static void SendVoidUInt64(nint receiver, string selector, ulong value) =>
        objc_msgSend_VoidUInt64(receiver, sel_registerName(selector), value);
    private static void SendVoidInt64(nint receiver, string selector, long value) =>
        objc_msgSend_VoidInt64(receiver, sel_registerName(selector), value);
    private static void SendVoidObject(nint receiver, string selector, nint value) =>
        objc_msgSend_VoidObject(receiver, sel_registerName(selector), value);
    private static void SendVoidObjectInt64(nint receiver, string selector, nint value, long order) =>
        objc_msgSend_VoidObjectInt64(receiver, sel_registerName(selector), value, order);
    private static void SendVoidObjectInt64Object(
        nint receiver,
        string selector,
        nint value,
        long order,
        nint relativeTo) =>
        objc_msgSend_VoidObjectInt64Object(
            receiver,
            sel_registerName(selector),
            value,
            order,
            relativeTo);

    public override void Dispose()
    {
        if (_parentWindow != 0)
        {
            SendVoidObject(_parentWindow, "removeChildWindow:", _nsWindow);
            _parentWindow = 0;
        }
        ApplyToolbar(false);
        RemoveVisualEffectView();
        base.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly double X;
        public readonly double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize
    {
        public readonly double Width;
        public readonly double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly NativePoint Origin;
        public readonly NativeSize Size;
    }

    [DllImport(ObjCLibrary)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(ObjCLibrary)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_Object(nint receiver, nint selector);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_ObjectObject(nint receiver, nint selector, nint value);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_ObjectInt64(nint receiver, nint selector, long value);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_ObjectRect(nint receiver, nint selector, NativeRect value);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern ulong objc_msgSend_UInt64(nint receiver, nint selector);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern NativeRect objc_msgSend_Rect(nint receiver, nint selector);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend_stret")]
    private static extern void objc_msgSend_stret_Rect(
        out NativeRect result,
        nint receiver,
        nint selector);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Void(nint receiver, nint selector);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_VoidBool(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_VoidUInt64(nint receiver, nint selector, ulong value);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_VoidInt64(nint receiver, nint selector, long value);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_VoidObject(nint receiver, nint selector, nint value);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_VoidObjectInt64(
        nint receiver,
        nint selector,
        nint value,
        long order);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_VoidObjectInt64Object(
        nint receiver,
        nint selector,
        nint value,
        long order,
        nint relativeTo);
}
