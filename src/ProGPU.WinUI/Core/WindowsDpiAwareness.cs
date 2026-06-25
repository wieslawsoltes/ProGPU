using System;
using System.Runtime.InteropServices;

namespace Microsoft.UI.Xaml;

internal static class WindowsDpiAwareness
{
    private static bool s_attempted;

    public static void TryEnablePerMonitorV2()
    {
        if (s_attempted || !OperatingSystem.IsWindows())
        {
            return;
        }

        s_attempted = true;

        try
        {
            if (SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2) ||
                SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAware))
            {
                return;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
        catch (BadImageFormatException)
        {
        }

        try
        {
            _ = SetProcessDpiAwareness(ProcessPerMonitorDpiAware);
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
        catch (BadImageFormatException)
        {
        }
    }

    private const int ProcessPerMonitorDpiAware = 2;
    private static readonly nint DpiAwarenessContextPerMonitorAware = new(-3);
    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiContext);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);
}
