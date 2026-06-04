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
    public float Size { get; }
    public FontStyle Style { get; }
    public GraphicsUnit Unit { get; }

    internal TtfFont TtfFont { get; }

    public Font(string familyName, float emSize, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
        : this(new FontFamily(familyName), emSize, style, unit)
    {
    }

    public Font(FontFamily family, float emSize, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
    {
        FontFamily = family;
        Size = emSize;
        Style = style;
        Unit = unit;

        string path = family.FilePath;
        if (string.IsNullOrEmpty(path))
        {
            throw new FileNotFoundException("No system font found for family " + family.Name);
        }

        TtfFont = s_ttfCache.GetOrAdd(path, p => new TtfFont(p));
    }

    public void Dispose() {}
}
