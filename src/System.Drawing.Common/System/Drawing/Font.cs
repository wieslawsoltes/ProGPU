using System.ComponentModel;
using System.Drawing.Interop;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using ProGPU.Text;
using ProGPU.SystemDrawing;

namespace System.Drawing;

[Flags]
public enum FontStyle
{
    Regular = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikeout = 8
}

public enum GraphicsUnit
{
    World = 0,
    Display = 1,
    Pixel = 2,
    Point = 3,
    Inch = 4,
    Document = 5,
    Millimeter = 6
}

public enum StringUnit
{
    World = 0,
    Display = 1,
    Pixel = 2,
    Point = 3,
    Inch = 4,
    Document = 5,
    Millimeter = 6,
    Em = 32,
}

/// <summary>
/// Defines a font using a typed ProGPU typeface and portable drawing metrics.
/// </summary>
[Serializable]
[TypeConverter(typeof(FontConverter))]
public sealed class Font : MarshalByRefObject, ICloneable, IDisposable, ISerializable
{
    private bool _disposed;

#pragma warning disable SYSLIB0050
    private Font(SerializationInfo info, StreamingContext context)
        : this(
            info.GetString("Name")!,
            info.GetSingle("Size"),
            (FontStyle)info.GetValue("Style", typeof(FontStyle))!,
            (GraphicsUnit)info.GetValue("Unit", typeof(GraphicsUnit))!)
    {
    }
#pragma warning restore SYSLIB0050

    public FontFamily FontFamily { get; }
    [TypeConverter(typeof(FontConverter.FontNameConverter))]
    public string Name => FontFamily.Name;
    public float Size { get; }
    public float SizeInPoints => Unit == GraphicsUnit.Point ? Size : Graphics.ConvertFontSizeToPoints(Size, Unit, 96f);
    public FontStyle Style { get; }
    [TypeConverter(typeof(FontConverter.FontUnitConverter))]
    public GraphicsUnit Unit { get; }
    public GraphicsUnit OriginalUnit => Unit;
    public byte GdiCharSet { get; }
    public bool GdiVerticalFont { get; }
    public bool Bold => (Style & FontStyle.Bold) != 0;
    public bool Italic => (Style & FontStyle.Italic) != 0;
    public bool Underline => (Style & FontStyle.Underline) != 0;
    public bool Strikeout => (Style & FontStyle.Strikeout) != 0;
    public bool IsSystemFont => false;
    public string SystemFontName => string.Empty;
    public string? OriginalFontName { get; }
    public int Height => (int)MathF.Ceiling(GetHeight());

    internal TtfFont TtfFont { get; }

    public Font(string familyName, float emSize)
        : this(familyName, emSize, FontStyle.Regular, GraphicsUnit.Point, 1, false)
    {
    }

    public Font(string familyName, float emSize, FontStyle style)
        : this(familyName, emSize, style, GraphicsUnit.Point, 1, false)
    {
    }

    public Font(string familyName, float emSize, GraphicsUnit unit)
        : this(familyName, emSize, FontStyle.Regular, unit, 1, false)
    {
    }

    public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit)
        : this(familyName, emSize, style, unit, 1, false)
    {
    }

    public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet)
        : this(familyName, emSize, style, unit, gdiCharSet, false)
    {
    }

    public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet, bool gdiVerticalFont)
        : this(CreateFamilyForFont(familyName), emSize, style, unit, gdiCharSet, gdiVerticalFont, familyName)
    {
    }

    public Font(Font prototype, FontStyle newStyle)
        : this(
            prototype.FontFamily.Snapshot(),
            prototype.Size,
            newStyle,
            prototype.Unit,
            prototype.GdiCharSet,
            prototype.GdiVerticalFont,
            prototype.OriginalFontName)
    {
    }

    /// <summary>
    /// Creates a font from an already loaded ProGPU typeface without platform discovery.
    /// </summary>
    public Font(TtfFont typeface, float emSize, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        Validate(emSize, unit);

        FontFamily = new FontFamily(typeface);
        Size = emSize;
        Style = style;
        Unit = unit;
        GdiCharSet = 1;
        GdiVerticalFont = false;
        OriginalFontName = FontFamily.Name;
        TtfFont = FontApi.Manager.MatchTypeface(typeface, FontFamily.CreateStyleRequest(style));
    }

    public Font(FontFamily family, float emSize)
        : this(family, emSize, FontStyle.Regular, GraphicsUnit.Point, 1, false)
    {
    }

    public Font(FontFamily family, float emSize, FontStyle style)
        : this(family, emSize, style, GraphicsUnit.Point, 1, false)
    {
    }

    public Font(FontFamily family, float emSize, GraphicsUnit unit)
        : this(family, emSize, FontStyle.Regular, unit, 1, false)
    {
    }

    public Font(FontFamily family, float emSize, FontStyle style, GraphicsUnit unit)
        : this(family, emSize, style, unit, 1, false)
    {
    }

    public Font(FontFamily family, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet)
        : this(family, emSize, style, unit, gdiCharSet, false)
    {
    }

    public Font(FontFamily family, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet, bool gdiVerticalFont)
        : this(family, emSize, style, unit, gdiCharSet, gdiVerticalFont, family?.Name)
    {
    }

    private Font(
        FontFamily family,
        float emSize,
        FontStyle style,
        GraphicsUnit unit,
        byte gdiCharSet,
        bool gdiVerticalFont,
        string? originalFontName)
    {
        ArgumentNullException.ThrowIfNull(family);
        Validate(emSize, unit);

        FontFamily = family.Snapshot();
        Size = emSize;
        Style = style;
        Unit = unit;
        GdiCharSet = gdiCharSet;
        GdiVerticalFont = gdiVerticalFont;
        OriginalFontName = originalFontName;
        TtfFont = FontFamily.ResolveTypeface(style);
    }

    public object Clone()
    {
        ThrowIfDisposed();
        return new Font(FontFamily, Size, Style, Unit, GdiCharSet, GdiVerticalFont, OriginalFontName);
    }

#pragma warning disable SYSLIB0050
    void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
    {
        ArgumentNullException.ThrowIfNull(info);
        info.AddValue("Name", string.IsNullOrEmpty(OriginalFontName) ? Name : OriginalFontName);
        info.AddValue("Size", Size);
        info.AddValue("Style", Style);
        info.AddValue("Unit", Unit);
    }
#pragma warning restore SYSLIB0050

    public override string ToString() =>
        $"[Font: Name={Name}, Size={Size}, Units={Unit}, GdiCharSet={GdiCharSet}, GdiVerticalFont={GdiVerticalFont}]";

    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) ||
        obj is Font font &&
        font.FontFamily.Equals(FontFamily) &&
        font.GdiVerticalFont == GdiVerticalFont &&
        font.GdiCharSet == GdiCharSet &&
        font.Style == Style &&
        font.Size == Size &&
        font.Unit == Unit;

    public override int GetHashCode() => HashCode.Combine(Name, Style, Size, Unit);

    public float GetHeight() => GetHeight(96f);

    public float GetHeight(Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        return GetHeight(graphics.DpiY);
    }

    public float GetHeight(float dpi)
    {
        float emSize = Graphics.ConvertFontSizeToPixels(Size, Unit, dpi);
        if (TtfFont.UnitsPerEm == 0)
        {
            return emSize;
        }

        return (TtfFont.Ascender - TtfFont.Descender + TtfFont.LineGap) * emSize / TtfFont.UnitsPerEm;
    }

    public IntPtr ToHfont() =>
        throw new PlatformNotSupportedException("HFONT export requires the explicit Windows GDI font adapter.");

    public static Font FromHfont(IntPtr hfont) =>
        throw new PlatformNotSupportedException("HFONT import requires the explicit Windows GDI font adapter.");

    public static Font FromHdc(IntPtr hdc)
        => NativeFontInteropServices.ImportFromDeviceContext(hdc);

    public static Font FromLogFont(in LOGFONT logFont)
        => CreateFromLogFont(in logFont);

    public static Font FromLogFont(in LOGFONT logFont, IntPtr hdc)
    {
        if (hdc == IntPtr.Zero)
        {
            throw new ArgumentException("A nonzero HDC is required.", nameof(hdc));
        }

        throw new PlatformNotSupportedException(
            "HDC-aware LOGFONT import requires the explicit Windows GDI font adapter.");
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Font FromLogFont(object lf)
    {
        ArgumentNullException.ThrowIfNull(lf);
        if (lf is not LOGFONT logFont)
        {
            throw new ArgumentException(
                "Portable LOGFONT import accepts the canonical System.Drawing.Interop.LOGFONT value.",
                nameof(lf));
        }

        return CreateFromLogFont(in logFont);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Font FromLogFont(object lf, IntPtr hdc)
    {
        ArgumentNullException.ThrowIfNull(lf);
        if (lf is not LOGFONT logFont)
        {
            throw new ArgumentException(
                "Portable LOGFONT import accepts the canonical System.Drawing.Interop.LOGFONT value.",
                nameof(lf));
        }

        return FromLogFont(in logFont, hdc);
    }

    public void ToLogFont(out LOGFONT logFont)
    {
        ThrowIfDisposed();
        WriteLogFont(out logFont, dpi: 96f);
    }

    public void ToLogFont(out LOGFONT logFont, Graphics graphics)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(graphics);
        graphics.EnsureNotDisposed();
        WriteLogFont(out logFont, graphics.DpiY);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ToLogFont(object logFont)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(logFont);
        WriteCanonicalLogFontBox(logFont, graphics: null);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ToLogFont(object logFont, Graphics graphics)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(logFont);
        ArgumentNullException.ThrowIfNull(graphics);
        graphics.EnsureNotDisposed();
        WriteCanonicalLogFontBox(logFont, graphics);
    }

    public void Dispose() => _disposed = true;

    private static Font CreateFromLogFont(in LOGFONT logFont)
    {
        string faceName = logFont.GetFaceName();
        if (faceName.Length == 0 && IsEmptyLogFont(in logFont))
        {
            throw new ArgumentException("The LOGFONT does not describe a font.", nameof(logFont));
        }

        bool vertical = faceName.Length > 1 && faceName[0] == '@';
        if (vertical)
        {
            faceName = faceName[1..];
        }

        if (string.IsNullOrWhiteSpace(faceName))
        {
            using FontFamily fallback = FontFamily.GenericSansSerif;
            faceName = fallback.Name;
        }

        FontStyle style = FontStyle.Regular;
        if (logFont.lfWeight >= 550)
        {
            style |= FontStyle.Bold;
        }

        if (logFont.lfItalic != 0)
        {
            style |= FontStyle.Italic;
        }

        if (logFont.lfUnderline != 0)
        {
            style |= FontStyle.Underline;
        }

        if (logFont.lfStrikeOut != 0)
        {
            style |= FontStyle.Strikeout;
        }

        float size = MathF.Abs((float)logFont.lfHeight);
        if (!(size > 0f) || !float.IsFinite(size))
        {
            size = 12f;
        }

        return new Font(
            faceName,
            size,
            style,
            GraphicsUnit.World,
            logFont.lfCharSet,
            vertical);
    }

    private static bool IsEmptyLogFont(in LOGFONT logFont)
        => logFont.lfHeight == 0
            && logFont.lfWidth == 0
            && logFont.lfEscapement == 0
            && logFont.lfOrientation == 0
            && logFont.lfWeight == 0
            && logFont.lfItalic == 0
            && logFont.lfUnderline == 0
            && logFont.lfStrikeOut == 0
            && logFont.lfCharSet == 0
            && logFont.lfOutPrecision == 0
            && logFont.lfClipPrecision == 0
            && logFont.lfQuality == 0
            && logFont.lfPitchAndFamily == 0;

    private void WriteCanonicalLogFontBox(object destination, Graphics? graphics)
    {
        if (destination is not LOGFONT)
        {
            throw new ArgumentException(
                "Portable LOGFONT export accepts a boxed System.Drawing.Interop.LOGFONT value.",
                nameof(destination));
        }

        WriteLogFont(out LOGFONT result, graphics?.DpiY ?? 96f);
        Unsafe.Unbox<LOGFONT>(destination) = result;
    }

    private void WriteLogFont(out LOGFONT logFont, float dpi)
    {
        logFont = default;
        float pixelHeight = Graphics.ConvertFontSizeToPixels(Size, Unit, dpi);
        double negativeHeight = -Math.Truncate(pixelHeight);
        logFont.lfHeight = negativeHeight < int.MinValue
            ? int.MinValue
            : negativeHeight > -1d
                ? -1
                : (int)negativeHeight;
        logFont.lfWeight = Bold ? 700 : 400;
        logFont.lfItalic = Italic ? (byte)1 : (byte)0;
        logFont.lfUnderline = Underline ? (byte)1 : (byte)0;
        logFont.lfStrikeOut = Strikeout ? (byte)1 : (byte)0;
        logFont.lfCharSet = GdiCharSet;
        logFont.SetFaceName(GdiVerticalFont ? $"@{Name}" : Name);
    }

    private static FontFamily CreateFamilyForFont(string familyName)
    {
        ArgumentNullException.ThrowIfNull(familyName);
        return FontFamily.CreateDefault(familyName);
    }

    private static void Validate(float emSize, GraphicsUnit unit)
    {
        if (!(emSize > 0) || !float.IsFinite(emSize))
        {
            throw new ArgumentException("Font size must be finite and greater than zero.", nameof(emSize));
        }

        if (unit is GraphicsUnit.Display or < GraphicsUnit.World or > GraphicsUnit.Millimeter)
        {
            throw new ArgumentException("The graphics unit is not valid for a font.", nameof(unit));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ArgumentException("Parameter is not valid.");
        }
    }
}
