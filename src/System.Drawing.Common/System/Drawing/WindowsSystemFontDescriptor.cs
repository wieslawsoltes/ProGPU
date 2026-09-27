using System.ComponentModel;
using System.Drawing.Interop;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace System.Drawing;

// A settings query, not a drawing adapter. No native font/family/graphics object
// survives Read, and no Microsoft managed Drawing object enters this assembly.
[SupportedOSPlatform("windows")]
internal readonly record struct WindowsSystemFontDescriptor(
    string FamilyName, float SizeInPoints, FontStyle Style, byte CharSet, bool Vertical)
{
    internal static unsafe WindowsSystemFontDescriptor Read(string role)
    {
        StartupInput input = new() { Version = 1 };
        Check(GdiplusStartup(out nuint token, in input, 0));
        try
        {
            nint dc = GetDC(0);
            if (dc == 0) throw new Win32Exception();
            try
            {
                if (role == nameof(SystemFonts.DefaultFont)) return ReadDefault(dc);
                if (role == nameof(SystemFonts.DialogFont))
                {
                    // The canonical SystemFonts policy uses these language IDs,
                    // not the UI culture or the thread's current culture.
                    return GetSystemDefaultLCID() == 17 ? ReadDefault(dc) : ReadNamed(dc, "MS Shell Dlg 2", 8);
                }

                LOGFONT font;
                if (role == nameof(SystemFonts.IconTitleFont))
                {
                    font = default;
                    if (SystemParametersInfoW(0x001F, (uint)sizeof(LOGFONT), &font, 0) == 0)
                        throw new Win32Exception();
                }
                else
                {
                    NonClientMetrics metrics = new() { Size = (uint)sizeof(NonClientMetrics) };
                    if (SystemParametersInfoW(0x0029, metrics.Size, &metrics, 0) == 0)
                        throw new Win32Exception();
                    font = role switch
                    {
                        nameof(SystemFonts.CaptionFont) => metrics.CaptionFont,
                        nameof(SystemFonts.SmallCaptionFont) => metrics.SmallCaptionFont,
                        nameof(SystemFonts.MenuFont) => metrics.MenuFont,
                        nameof(SystemFonts.StatusFont) => metrics.StatusFont,
                        nameof(SystemFonts.MessageBoxFont) => metrics.MessageFont,
                        _ => throw new ArgumentOutOfRangeException(nameof(role))
                    };
                }

                return ReadLogFont(dc, in font) ?? ReadDefault(dc);
            }
            finally { ReleaseDC(0, dc); }
        }
        finally { GdiplusShutdown(token); }
    }

    private static unsafe WindowsSystemFontDescriptor ReadDefault(nint dc)
    {
        if (GetSystemDefaultLCID() != 1)
        {
            // DEFAULT_GUI_FONT is borrowed stock state: never DeleteObject it.
            nint stock = GetStockObject(17);
            if (stock != 0 && GetObjectW(stock, sizeof(LOGFONT), out LOGFONT font) == sizeof(LOGFONT))
            {
                WindowsSystemFontDescriptor? descriptor = ReadLogFont(dc, in font);
                if (descriptor.HasValue) return descriptor.Value;
            }
        }

        return ReadNamed(dc, "Tahoma", 8);
    }

    private static WindowsSystemFontDescriptor? ReadLogFont(nint dc, in LOGFONT description)
    {
        nint font = 0;
        try
        {
            // Native GDI+ interprets logical heights (including positive cell
            // heights), styles and font substitution just as Microsoft's API.
            // abs(lfHeight)*72/96 would lose those contracts and double-scale DPI.
            int status = GdipCreateFontFromLogfontW(dc, in description, out font);
            if (status != 0 || font == 0) return null;
            return ReadFont(dc, font, description.lfCharSet, description.GetFaceName().StartsWith('@'));
        }
        finally { if (font != 0) GdipDeleteFont(font); }
    }

    private static WindowsSystemFontDescriptor ReadNamed(nint dc, string name, float size)
    {
        nint family = 0;
        nint font = 0;
        try
        {
            int status = GdipCreateFontFamilyFromName(name, 0, out family);
            if (status != 0)
            {
                if (family != 0) { GdipDeleteFontFamily(family); family = 0; }
                Check(GdipGetGenericFontFamilySansSerif(out family));
            }
            RequireHandle(family);
            Check(GdipCreateFont(family, size, FontStyle.Regular, GraphicsUnit.Point, out font));
            RequireHandle(font);
            return ReadFont(dc, font, 1, false);
        }
        finally
        {
            if (font != 0) GdipDeleteFont(font);
            if (family != 0) GdipDeleteFontFamily(family);
        }
    }

    private static unsafe WindowsSystemFontDescriptor ReadFont(nint dc, nint font, byte charSet, bool vertical)
    {
        nint family = 0;
        nint graphics = 0;
        try
        {
            Check(GdipGetFamily(font, out family));
            RequireHandle(family);
            char* name = stackalloc char[32];
            new Span<char>(name, 32).Clear();
            Check(GdipGetFamilyName(family, name, 0));
            int length = new ReadOnlySpan<char>(name, 32).IndexOf('\0');
            if (length <= 0) throw new InvalidOperationException("Windows returned an invalid system font family.");
            string familyName = new(name, 0, length);
            Check(GdipGetFontStyle(font, out FontStyle style));
            Check(GdipGetFontUnit(font, out GraphicsUnit unit));
            Check(GdipGetFontSize(font, out float size));
            if (unit != GraphicsUnit.Point)
            {
                Check(GdipCreateFromHDC(dc, out graphics));
                RequireHandle(graphics);
                Check(GdipGetDpiY(graphics, out float dpi));
                Check(GdipGetFontHeight(font, graphics, out float height));
                Check(GdipGetEmHeight(family, style, out ushort emHeight));
                Check(GdipGetLineSpacing(family, style, out ushort lineSpacing));
                if (!(dpi > 0) || !float.IsFinite(dpi) || emHeight == 0 || lineSpacing == 0)
                    throw new InvalidOperationException("Windows returned invalid system font metrics.");
                // Preserve the canonical SizeInPoints operation order. The
                // screen DC supplies the current DPI-awareness context.
                float pixelsPerPoint = (float)(dpi / 72.0);
                size = (height * emHeight / lineSpacing) / pixelsPerPoint;
            }
            if (!(size > 0) || !float.IsFinite(size))
                throw new InvalidOperationException("Windows returned an invalid system font size.");
            return new(familyName, size, style, charSet, vertical);
        }
        finally
        {
            if (graphics != 0) GdipDeleteGraphics(graphics);
            if (family != 0) GdipDeleteFontFamily(family);
        }
    }

    private static void Check(int status)
    {
        if (status != 0) throw new ExternalException($"Windows system font metadata query failed (GDI+ status {status}).", status);
    }

    private static void RequireHandle(nint handle)
    {
        if (handle == 0) throw new InvalidOperationException("Windows returned no system font metadata handle.");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NonClientMetrics
    {
        public uint Size;
        public int BorderWidth, ScrollWidth, ScrollHeight, CaptionWidth, CaptionHeight;
        public LOGFONT CaptionFont;
        public int SmallCaptionWidth, SmallCaptionHeight;
        public LOGFONT SmallCaptionFont;
        public int MenuWidth, MenuHeight;
        public LOGFONT MenuFont, StatusFont, MessageFont;
        public int PaddedBorderWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInput
    {
        public uint Version;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread, SuppressExternalCodecs;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern nint GetDC(nint window);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern unsafe int SystemParametersInfoW(uint action, uint parameter, void* data, uint flags);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern int GetObjectW(nint handle, int size, out LOGFONT font);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetSystemDefaultLCID();
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdiplusStartup(out nuint token, in StartupInput input, nint output);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern void GdiplusShutdown(nuint token);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipCreateFontFromLogfontW(nint dc, in LOGFONT description, out nint font);
    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GdipCreateFontFamilyFromName(string name, nint collection, out nint family);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipGetGenericFontFamilySansSerif(out nint family);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipCreateFont(nint family, float size, FontStyle style, GraphicsUnit unit, out nint font);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipGetFamily(nint font, out nint family);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern unsafe int GdipGetFamilyName(nint family, char* name, ushort language);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipGetFontStyle(nint font, out FontStyle style);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipGetFontSize(nint font, out float size);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipGetFontUnit(nint font, out GraphicsUnit unit);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipCreateFromHDC(nint dc, out nint graphics);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipGetDpiY(nint graphics, out float dpi);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipGetFontHeight(nint font, nint graphics, out float height);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipGetEmHeight(nint family, FontStyle style, out ushort height);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipGetLineSpacing(nint family, FontStyle style, out ushort height);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipDeleteFont(nint font);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipDeleteFontFamily(nint family);
    [DllImport("gdiplus.dll", ExactSpelling = true)] private static extern int GdipDeleteGraphics(nint graphics);
}
