using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Diagnostic bindings to the original Windows GDI+ flat API; never product code.
internal static class NativeDriver
{
    internal static Bitmap Draw(string familyName, float points, int hint, string text, PointF[] positions, string policy)
    {
        nint family = 0, font = 0, brush = 0, image = 0, graphics = 0, format = 0;
        nuint token = 0;
        Bitmap? output = null;
        Exception? primary = null;
        try
        {
            Startup startup = new() { Version = 1 };
            Check(GdiplusStartup(out token, ref startup, 0));
            Check(GdipCreateFontFamilyFromName(familyName, 0, out family));
            Check(GdipCreateFont(family, points, 0, 3, out font)); // original point units
            Check(GdipCreateSolidFill(0xff000000, out brush));
            Check(GdipCreateBitmapFromScan0(384, 64, 0, (int)PixelFormat.Format32bppArgb, 0, out image));
            Check(GdipBitmapSetResolution(image, 96, 96));
            Check(GdipGetImageGraphicsContext(image, out graphics));
            Check(GdipGraphicsClear(graphics, 0xffffffff));
            Check(GdipSetTextRenderingHint(graphics, hint));
            Check(GdipSetTextContrast(graphics, 0));
            if (policy == "NativeDrawString")
            {
                Check(GdipStringFormatGetGenericTypographic(out format));
                Check(GdipGetStringFormatFlags(format, out int flags));
                Check(GdipSetStringFormatFlags(format, flags | 0x1000)); // NoWrap
                RectangleF bounds = new(8, 8, 368, 48);
                Check(GdipDrawString(graphics, text, text.Length, font, ref bounds, format, brush));
            }
            else
            {
                int flags = policy == "RealizedAdvance" ? 5 : 1; // CmapLookup | optional RealizedAdvance
                if (positions.Length != (flags == 5 ? 1 : text.Length)) throw new InvalidOperationException("Driver position count changed.");
                ushort[] chars = text.Select(c => (ushort)c).ToArray();
                Check(GdipDrawDriverString(graphics, chars, chars.Length, font, brush, positions, flags, 0));
            }
            // Finish the owned recording before reading actual original bitmap pixels.
            Check(GdipDeleteGraphics(graphics)); graphics = 0;
            output = new Bitmap(384, 64, PixelFormat.Format32bppArgb);
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 384; x++)
            {
                Check(GdipBitmapGetPixel(image, x, y, out uint argb));
                output.SetPixel(x, y, Color.FromArgb(unchecked((int)argb)));
            }
            return output;
        }
        catch (Exception error) { primary = error; output?.Dispose(); throw; }
        finally
        {
            List<int> errors = [];
            if (graphics != 0) errors.Add(GdipDeleteGraphics(graphics));
            if (format != 0) errors.Add(GdipDeleteStringFormat(format));
            if (image != 0) errors.Add(GdipDisposeImage(image));
            if (brush != 0) errors.Add(GdipDeleteBrush(brush));
            if (font != 0) errors.Add(GdipDeleteFont(font));
            if (family != 0) errors.Add(GdipDeleteFontFamily(family));
            if (token != 0) GdiplusShutdown(token);
            if (errors.Any(e => e != 0))
            {
                if (primary != null) primary.Data["OriginalNativeCleanup"] = string.Join(',', errors);
                else { output?.Dispose(); throw new InvalidOperationException("Original native cleanup failed: " + string.Join(',', errors)); }
            }
        }
    }
    private static void Check(int status) { if (status != 0) throw new InvalidOperationException("Original GDI+ status " + status); }
    [StructLayout(LayoutKind.Sequential)] private struct Startup { internal uint Version; internal nint DebugCallback; internal int SuppressBackgroundThread; internal int SuppressExternalCodecs; }
    [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out nuint token, ref Startup input, nint output);
    [DllImport("gdiplus.dll")] private static extern void GdiplusShutdown(nuint token);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipCreateFontFamilyFromName(string name, nint collection, out nint family);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateFont(nint family, float size, int style, int unit, out nint font);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateSolidFill(uint color, out nint brush);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan, out nint bitmap);
    [DllImport("gdiplus.dll")] private static extern int GdipBitmapSetResolution(nint bitmap, float x, float y);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageGraphicsContext(nint image, out nint graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipGraphicsClear(nint graphics, uint color);
    [DllImport("gdiplus.dll")] private static extern int GdipSetTextRenderingHint(nint graphics, int hint);
    [DllImport("gdiplus.dll")] private static extern int GdipSetTextContrast(nint graphics, uint contrast);
    [DllImport("gdiplus.dll")] private static extern int GdipStringFormatGetGenericTypographic(out nint format);
    [DllImport("gdiplus.dll")] private static extern int GdipGetStringFormatFlags(nint format, out int flags);
    [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatFlags(nint format, int flags);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipDrawString(nint graphics, string text, int length, nint font, ref RectangleF layout, nint format, nint brush);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawDriverString(nint graphics, [In] ushort[] text, int length, nint font, nint brush, [In] PointF[] positions, int flags, nint matrix);
    [DllImport("gdiplus.dll")] private static extern int GdipBitmapGetPixel(nint bitmap, int x, int y, out uint argb);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteGraphics(nint graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteStringFormat(nint format);
    [DllImport("gdiplus.dll")] private static extern int GdipDisposeImage(nint image);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteBrush(nint brush);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteFont(nint font);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteFontFamily(nint family);
}
