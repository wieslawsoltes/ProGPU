using ProGPU.Text;
using System.Runtime.Serialization;

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

/// <summary>
/// Defines a font using a typed ProGPU typeface and portable drawing metrics.
/// </summary>
[Serializable]
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
    public string Name => FontFamily.Name;
    public float Size { get; }
    public float SizeInPoints => Unit == GraphicsUnit.Point ? Size : Graphics.ConvertFontSizeToPoints(Size, Unit, 96f);
    public FontStyle Style { get; }
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

    public void Dispose() => _disposed = true;

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

        if (unit == GraphicsUnit.Display || unit < GraphicsUnit.World || unit > GraphicsUnit.Millimeter)
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
