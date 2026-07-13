using ProGPU.Text;
using System.Collections.Concurrent;
using System.IO;

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

public class Font : IDisposable
{
    private static readonly ConcurrentDictionary<string, TtfFont> s_ttfCache = new(StringComparer.OrdinalIgnoreCase);

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
    public int Height => (int)MathF.Ceiling(GetHeight());

    internal TtfFont TtfFont { get; }

    public Font(string familyName, float emSize, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
        : this(new FontFamily(familyName), emSize, style, unit)
    {
    }

    public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet)
        : this(new FontFamily(familyName), emSize, style, unit, gdiCharSet)
    {
    }

    public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet, bool gdiVerticalFont)
        : this(new FontFamily(familyName), emSize, style, unit, gdiCharSet, gdiVerticalFont)
    {
    }

    public Font(Font prototype, FontStyle newStyle)
        : this(prototype.FontFamily, prototype.Size, newStyle, prototype.Unit, prototype.GdiCharSet, prototype.GdiVerticalFont)
    {
    }

    public Font(FontFamily family, float emSize, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
        : this(family, emSize, style, unit, 1)
    {
    }

    public Font(FontFamily family, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet)
        : this(family, emSize, style, unit, gdiCharSet, false)
    {
    }

    public Font(FontFamily family, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet, bool gdiVerticalFont)
    {
        FontFamily = family;
        Size = emSize;
        Style = style;
        Unit = unit;
        GdiCharSet = gdiCharSet;
        GdiVerticalFont = gdiVerticalFont;

        string path = family.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            throw new FileNotFoundException("No system font found for family " + family.Name);
        }

        TtfFont = s_ttfCache.GetOrAdd(family.CacheKey, _ => new TtfFont(path, family.FaceIndex));
    }

    public override string ToString()
    {
        return $"[Font: Name={Name}, Size={Size}, Units={Unit}, GdiCharSet={GdiCharSet}, GdiVerticalFont={GdiVerticalFont}]";
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj)
            || obj is Font font
                && font.FontFamily.Equals(FontFamily)
                && font.GdiVerticalFont == GdiVerticalFont
                && font.GdiCharSet == GdiCharSet
                && font.Style == Style
                && font.Size == Size
                && font.Unit == Unit;
    }

    public override int GetHashCode() => HashCode.Combine(Name, Style, Size, Unit);

    public float GetHeight()
    {
        return GetHeight(96f);
    }

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

        return (TtfFont.Ascender - TtfFont.Descender + TtfFont.LineGap)
            * emSize
            / TtfFont.UnitsPerEm;
    }

    public void Dispose() {}
}
