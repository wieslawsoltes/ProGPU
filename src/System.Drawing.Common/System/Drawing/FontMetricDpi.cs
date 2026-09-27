using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace System.Drawing;

// Only implicit Font metrics use the Windows screen DC. Explicit Graphics/image
// and recorder DPI remain independent. Do not cache across thread DPI contexts.
internal static class FontMetricDpi
{
    internal static float GetDefault() => OperatingSystem.IsWindows() ? ReadWindowsScreenDpi() : 96f;

    [SupportedOSPlatform("windows")]
    private static float ReadWindowsScreenDpi()
    {
        nint dc = GetDC(0);
        if (dc == 0) throw new InvalidOperationException("Could not acquire the screen device context for font metrics.");
        try
        {
            int dpi = GetDeviceCaps(dc, 90); // LOGPIXELSY, virtualized for the calling DPI context.
            if (dpi <= 0) throw new InvalidOperationException("The screen device context returned an invalid font DPI.");
            return dpi;
        }
        finally
        {
            if (ReleaseDC(0, dc) != 1)
                throw new InvalidOperationException("Could not release the font-metric screen device context.");
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetDeviceCaps(nint dc, int index);
}
