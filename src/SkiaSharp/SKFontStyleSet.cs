using System.Collections;
using ProGPU.Text;

namespace SkiaSharp;

public class SKFontStyleSet : SKObject, IEnumerable<SKFontStyle>, IReadOnlyList<SKFontStyle>
{
    private readonly Entry[] _entries;

    public int Count => _entries.Length;
    public SKFontStyle this[int index] => GetStyle(index);

    public SKFontStyleSet()
        : this(Array.Empty<FontInfo>())
    {
    }

    internal SKFontStyleSet(IReadOnlyList<FontInfo> fonts)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _entries = new Entry[fonts.Count];
        for (var index = 0; index < fonts.Count; index++)
        {
            var font = fonts[index];
            var style = ParseStyle(font.Name);
            _entries[index] = new Entry(
                style,
                GetStyleName(font, style),
                () => SKTypeface.CreateFont(font));
        }
    }

    internal SKFontStyleSet(IReadOnlyList<FontFace> fonts)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _entries = new Entry[fonts.Count];
        for (var index = 0; index < fonts.Count; index++)
        {
            FontFace font = fonts[index];
            var style = new StyleData(
                font.Style.Weight,
                font.Style.Width,
                font.Style.Slant switch
                {
                    FontSlant.Italic => SKFontStyleSlant.Italic,
                    FontSlant.Oblique => SKFontStyleSlant.Oblique,
                    _ => SKFontStyleSlant.Upright
                });
            _entries[index] = new Entry(style, GetStyleName(font, style), font.Load);
        }
    }

    public string GetStyleName(int index) => _entries[index].Name;

    public SKTypeface CreateTypeface(int index)
    {
        if ((uint)index >= (uint)_entries.Length)
        {
            throw new ArgumentOutOfRangeException(
                "Index was out of range. Must be non-negative and less than the size of the set.",
                "index");
        }

        TtfFont? font = _entries[index].Load();
        return font is null ? null! : new SKTypeface(font, font.FamilyName);
    }

    public SKTypeface CreateTypeface(SKFontStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (_entries.Length == 0)
        {
            return null!;
        }

        var bestIndex = 0;
        var bestDistance = int.MaxValue;
        for (var index = 0; index < _entries.Length; index++)
        {
            var candidate = _entries[index].Style;
            var slantDistance = candidate.Slant == style.Slant ? 0 : 10_000;
            var widthDistance = Math.Abs(candidate.Width - style.Width) * 1_000;
            var weightDistance = Math.Abs(candidate.Weight - style.Weight);
            var distance = slantDistance + widthDistance + weightDistance;
            if (distance < bestDistance)
            {
                bestIndex = index;
                bestDistance = distance;
                if (distance == 0)
                {
                    break;
                }
            }
        }

        return CreateTypeface(bestIndex);
    }

    public SKTypeface MatchStyle(SKFontStyle style) => CreateTypeface(style);

    public IEnumerator<SKFontStyle> GetEnumerator()
    {
        for (var index = 0; index < _entries.Length; index++)
        {
            yield return GetStyle(index);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    private SKFontStyle GetStyle(int index)
    {
        var style = _entries[index].Style;
        return new SKFontStyle(style.Weight, style.Width, style.Slant);
    }

    private static StyleData ParseStyle(string name)
    {
        var normalized = name.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var weight = normalized switch
        {
            var value when value.Contains("extralight", StringComparison.Ordinal) ||
                           value.Contains("ultralight", StringComparison.Ordinal) => SKFontStyleWeight.ExtraLight,
            var value when value.Contains("semibold", StringComparison.Ordinal) ||
                           value.Contains("demibold", StringComparison.Ordinal) => SKFontStyleWeight.SemiBold,
            var value when value.Contains("extrabold", StringComparison.Ordinal) ||
                           value.Contains("ultrabold", StringComparison.Ordinal) => SKFontStyleWeight.ExtraBold,
            var value when value.Contains("thin", StringComparison.Ordinal) => SKFontStyleWeight.Thin,
            var value when value.Contains("light", StringComparison.Ordinal) => SKFontStyleWeight.Light,
            var value when value.Contains("medium", StringComparison.Ordinal) => SKFontStyleWeight.Medium,
            var value when value.Contains("black", StringComparison.Ordinal) ||
                           value.Contains("heavy", StringComparison.Ordinal) => SKFontStyleWeight.Black,
            var value when value.Contains("bold", StringComparison.Ordinal) => SKFontStyleWeight.Bold,
            _ => SKFontStyleWeight.Normal,
        };
        var width = normalized switch
        {
            var value when value.Contains("ultracondensed", StringComparison.Ordinal) => SKFontStyleWidth.UltraCondensed,
            var value when value.Contains("extracondensed", StringComparison.Ordinal) => SKFontStyleWidth.ExtraCondensed,
            var value when value.Contains("semicondensed", StringComparison.Ordinal) => SKFontStyleWidth.SemiCondensed,
            var value when value.Contains("condensed", StringComparison.Ordinal) => SKFontStyleWidth.Condensed,
            var value when value.Contains("ultraexpanded", StringComparison.Ordinal) => SKFontStyleWidth.UltraExpanded,
            var value when value.Contains("extraexpanded", StringComparison.Ordinal) => SKFontStyleWidth.ExtraExpanded,
            var value when value.Contains("semiexpanded", StringComparison.Ordinal) => SKFontStyleWidth.SemiExpanded,
            var value when value.Contains("expanded", StringComparison.Ordinal) => SKFontStyleWidth.Expanded,
            _ => SKFontStyleWidth.Normal,
        };
        var slant = normalized.Contains("oblique", StringComparison.Ordinal)
            ? SKFontStyleSlant.Oblique
            : normalized.Contains("italic", StringComparison.Ordinal)
                ? SKFontStyleSlant.Italic
                : SKFontStyleSlant.Upright;
        return new StyleData((int)weight, (int)width, slant);
    }

    private static string GetStyleName(FontInfo font, StyleData style)
    {
        if (font.Name.StartsWith(font.FamilyName, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = font.Name[font.FamilyName.Length..].Trim(' ', '-', '_');
            if (suffix.Length > 0)
            {
                return suffix;
            }
        }

        if (style.Weight == (int)SKFontStyleWeight.Normal &&
            style.Width == (int)SKFontStyleWidth.Normal &&
            style.Slant == SKFontStyleSlant.Upright)
        {
            return "Regular";
        }

        return font.Name;
    }

    private static string GetStyleName(FontFace font, StyleData style)
    {
        if (font.Name.StartsWith(font.FamilyName, StringComparison.OrdinalIgnoreCase))
        {
            string suffix = font.Name[font.FamilyName.Length..].Trim(' ', '-', '_');
            if (suffix.Length > 0)
            {
                return suffix;
            }
        }

        return font.Name;
    }

    private readonly record struct StyleData(int Weight, int Width, SKFontStyleSlant Slant);
    private sealed record Entry(StyleData Style, string Name, Func<TtfFont?> Load);
}
