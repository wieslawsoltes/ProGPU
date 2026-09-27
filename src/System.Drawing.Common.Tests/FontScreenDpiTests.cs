using System.Runtime.InteropServices;
using Xunit;

namespace System.Drawing.Tests;

public sealed class FontScreenDpiTests
{
    [Theory]
    [InlineData(GraphicsUnit.Point, 9f)]
    [InlineData(GraphicsUnit.Pixel, 12f)]
    [InlineData(GraphicsUnit.World, 12f)]
    [InlineData(GraphicsUnit.Inch, .125f)]
    [InlineData(GraphicsUnit.Document, 37.5f)]
    [InlineData(GraphicsUnit.Millimeter, 3.175f)]
    public void ImplicitMetricsFollowScreenContextWithoutChangingExplicitTargets(GraphicsUnit unit, float size)
    {
        using FontFamily family = FontFamily.GenericSansSerif;
        using Font font = new(family, size, unit);
        using Bitmap bitmap = new(8, 8);
        bitmap.SetResolution(144, 168);
        using Graphics graphics = Graphics.FromImage(bitmap);
        float explicit96 = font.GetHeight(96), explicit192 = font.GetHeight(192);
        // Reuse the same font while changing and restoring awareness: a process-
        // or font-cached DPI would be wrong on a scaled Windows desktop.
        nint[] contexts = OperatingSystem.IsWindows() ? [-1, -2, -4, -1] : [0];
        foreach (nint context in contexts)
        {
            nint previous = context == 0 ? 0 : SetThreadDpiAwarenessContext(context);
            if (context != 0) Assert.NotEqual(0, previous);
            try
            {
                float dpi = 96;
                if (context != 0)
                {
                    Assert.True(AreDpiAwarenessContextsEqual(context, GetThreadDpiAwarenessContext()));
                    nint dc = GetDC(0);
                    Assert.NotEqual(0, dc);
                    try { dpi = GetDeviceCaps(dc, 90); }
                    finally { Assert.Equal(1, ReleaseDC(0, dc)); }
                    Assert.True(dpi > 0);
                }
                Assert.Equal(font.GetHeight(dpi), font.GetHeight());
                Assert.Equal((int)MathF.Ceiling(font.GetHeight(dpi)), font.Height);
                Assert.Equal(unit is GraphicsUnit.Pixel or GraphicsUnit.World ? size * 72 / dpi : 9f, font.SizeInPoints);
                Assert.Equal(explicit96, font.GetHeight(96));
                Assert.Equal(explicit192, font.GetHeight(192));
                Assert.Equal(font.GetHeight(168), font.GetHeight(graphics));
                Assert.Equal(144, graphics.DpiX);
                Assert.Equal(168, graphics.DpiY);
                Assert.Equal(size, font.Size);
                Assert.Equal(unit, font.Unit);
            }
            finally { if (previous != 0) Assert.NotEqual(0, SetThreadDpiAwarenessContext(previous)); }
        }
    }

    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(nint dc, int index);
}
