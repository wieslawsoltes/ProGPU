using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

public static class DisplayScaleResolver
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

    public static double ResolveWindowDisplayScale(IWindow? window)
    {
        double monitorDpiScale = 1.0;
        if (window != null && window.Size.X > 0 && window.FramebufferSize.X > 0)
        {
            monitorDpiScale = (double)window.FramebufferSize.X / window.Size.X;
        }

        return ResolveWindowDisplayScale(window, monitorDpiScale);
    }

    public static double ResolveWindowDisplayScale(IWindow? window, double monitorDpiScale)
    {
        return ResolveDisplayScaleWithPlatformFallback(
            monitorDpiScale,
            () => TryResolveNativeWindowDisplayScale(window));
    }

    public static double ResolveDisplayScaleWithPlatformFallback(
        double monitorDpiScale,
        Func<double?> platformDpiScaleProvider)
    {
        ArgumentNullException.ThrowIfNull(platformDpiScaleProvider);

        double normalizedMonitorScale = NormalizeDisplayScale(monitorDpiScale);
        if (normalizedMonitorScale > 1.0)
        {
            return normalizedMonitorScale;
        }

        double? platformDpiScale = platformDpiScaleProvider();
        if (!platformDpiScale.HasValue)
        {
            return normalizedMonitorScale;
        }

        return NormalizeDisplayScale(platformDpiScale.Value);
    }

    public static double NormalizeDisplayScale(double dpiScale)
    {
        return double.IsFinite(dpiScale) && dpiScale > 0.0 && dpiScale <= 8.0
            ? dpiScale
            : 1.0;
    }

    public static double? TryResolveNativeWindowDisplayScale(IWindow? window)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryResolveWindowsWindowDisplayScale(window);
        }

        if (OperatingSystem.IsMacOS())
        {
            return TryResolveMacOsBackingScaleFactor(window);
        }

        return null;
    }

    private static double? TryResolveWindowsWindowDisplayScale(IWindow? window)
    {
        try
        {
            nint hwnd = TryGetWin32WindowHandle(window);
            if (hwnd == 0)
            {
                return null;
            }

            uint dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static nint TryGetWin32WindowHandle(IWindow? window)
    {
        if (window is not INativeWindowSource nativeWindowSource)
        {
            return 0;
        }

        (IntPtr Hwnd, IntPtr Hdc, IntPtr HInstance)? win32 = nativeWindowSource.Native?.Win32;
        if (!win32.HasValue || win32.Value.Hwnd == IntPtr.Zero)
        {
            return 0;
        }

        return win32.Value.Hwnd;
    }

    private static double? TryResolveMacOsBackingScaleFactor(IWindow? window)
    {
        try
        {
            nint cocoaObject = TryGetCocoaWindowHandle(window);
            if (TryReadMacOsBackingScaleFactor(cocoaObject, out double objectBackingScaleFactor))
            {
                return objectBackingScaleFactor;
            }

            nint screen = TryGetMacOsObjectScreen(cocoaObject);
            if (TryReadMacOsBackingScaleFactor(screen, out double screenBackingScaleFactor))
            {
                return screenBackingScaleFactor;
            }

            nint mainScreen = TryGetMacOsMainScreen();
            return TryReadMacOsBackingScaleFactor(mainScreen, out double mainScreenBackingScaleFactor)
                ? mainScreenBackingScaleFactor
                : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static nint TryGetMacOsObjectScreen(nint cocoaObject)
    {
        if (cocoaObject == 0)
        {
            return 0;
        }

        if (TrySendMacOsIntPtr(cocoaObject, "screen", out nint screen))
        {
            return screen;
        }

        return TrySendMacOsIntPtr(cocoaObject, "window", out nint cocoaWindow) &&
            TrySendMacOsIntPtr(cocoaWindow, "screen", out screen)
                ? screen
                : 0;
    }

    private static nint TryGetMacOsMainScreen()
    {
        nint screenClass = objc_getClass("NSScreen");
        if (screenClass == 0)
        {
            return 0;
        }

        return TrySendMacOsIntPtr(screenClass, "mainScreen", out nint mainScreen)
            ? mainScreen
            : 0;
    }

    private static bool TryReadMacOsBackingScaleFactor(nint cocoaObject, out double backingScaleFactor)
    {
        backingScaleFactor = 0.0;
        if (cocoaObject == 0 ||
            !TryRespondsToMacOsSelector(cocoaObject, "backingScaleFactor", out nint backingScaleFactorSelector))
        {
            return false;
        }

        backingScaleFactor = objc_msgSend_Double(cocoaObject, backingScaleFactorSelector);
        return double.IsFinite(backingScaleFactor) && backingScaleFactor > 0.0 && backingScaleFactor <= 8.0;
    }

    private static bool TrySendMacOsIntPtr(nint receiver, string selectorName, out nint value)
    {
        value = 0;
        if (receiver == 0 || !TryRespondsToMacOsSelector(receiver, selectorName, out nint selector))
        {
            return false;
        }

        value = objc_msgSend_IntPtr(receiver, selector);
        return value != 0;
    }

    private static bool TryRespondsToMacOsSelector(nint receiver, string selectorName, out nint selector)
    {
        selector = sel_registerName(selectorName);
        nint respondsToSelector = sel_registerName("respondsToSelector:");
        return receiver != 0 &&
            selector != 0 &&
            respondsToSelector != 0 &&
            objc_msgSend_Bool(receiver, respondsToSelector, selector);
    }

    private static nint TryGetCocoaWindowHandle(IWindow? window)
    {
        if (window is not INativeWindowSource nativeWindowSource)
        {
            return 0;
        }

        IntPtr? cocoa = nativeWindowSource.Native?.Cocoa;
        if (!cocoa.HasValue || cocoa.Value == IntPtr.Zero)
        {
            return 0;
        }

        return cocoa.Value;
    }

    [DllImport(ObjCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_IntPtr(nint receiver, nint selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool objc_msgSend_Bool(nint receiver, nint selector, nint argument);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern double objc_msgSend_Double(nint receiver, nint selector);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
