using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Windowing;

namespace ProGPU.Backend;

public static class DisplayScaleResolver
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

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
        if (OperatingSystem.IsMacOS())
        {
            return TryResolveMacOsBackingScaleFactor(window);
        }

        return null;
    }

    private static double? TryResolveMacOsBackingScaleFactor(IWindow? window)
    {
        try
        {
            nint screen = TryGetMacOsWindowScreen(window);
            nint backingScaleFactorSelector = sel_registerName("backingScaleFactor");
            if (screen == 0 || backingScaleFactorSelector == 0)
            {
                return null;
            }

            double backingScaleFactor = objc_msgSend_Double(screen, backingScaleFactorSelector);
            return double.IsFinite(backingScaleFactor) && backingScaleFactor > 0.0 && backingScaleFactor <= 8.0
                ? backingScaleFactor
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

    private static nint TryGetMacOsWindowScreen(IWindow? window)
    {
        nint cocoaWindow = TryGetCocoaWindowHandle(window);
        if (cocoaWindow != 0)
        {
            nint screenSelector = sel_registerName("screen");
            if (screenSelector != 0)
            {
                nint screen = objc_msgSend_IntPtr(cocoaWindow, screenSelector);
                if (screen != 0)
                {
                    return screen;
                }
            }
        }

        nint screenClass = objc_getClass("NSScreen");
        if (screenClass == 0)
        {
            return 0;
        }

        nint mainScreenSelector = sel_registerName("mainScreen");
        return mainScreenSelector != 0
            ? objc_msgSend_IntPtr(screenClass, mainScreenSelector)
            : 0;
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
    private static extern double objc_msgSend_Double(nint receiver, nint selector);
}
