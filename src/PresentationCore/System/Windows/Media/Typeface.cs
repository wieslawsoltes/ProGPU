using System;
using System.Collections.Concurrent;
using ProGPU.Text;

namespace System.Windows.Media;

public class FontFamily
{
    private static readonly ConcurrentDictionary<string, string> s_fontCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, TtfFont> s_ttfCache = new(StringComparer.OrdinalIgnoreCase);
    private static string? s_fallbackFontPath;

    static FontFamily()
    {
        try
        {
            var systemFonts = FontApi.GetSystemFonts();
            foreach (var font in systemFonts)
            {
                if (!string.IsNullOrEmpty(font.FamilyName))
                {
                    s_fontCache.TryAdd(font.FamilyName, font.FilePath);
                }
                if (!string.IsNullOrEmpty(font.Name))
                {
                    s_fontCache.TryAdd(font.Name, font.FilePath);
                }
            }

            foreach (var key in new[] { "Arial", "Consolas", "Georgia", "Helvetica", "Roboto", "Courier New", "Times New Roman" })
            {
                if (s_fontCache.TryGetValue(key, out var path))
                {
                    s_fallbackFontPath = path;
                    break;
                }
            }

            if (s_fallbackFontPath == null && systemFonts.Count > 0)
            {
                s_fallbackFontPath = systemFonts[0].FilePath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WPF FontFamily] Error initializing system font cache: {ex.Message}");
        }
    }

    public string Name { get; }
    internal string FilePath { get; }

    public TtfFont? NativeFont
    {
        get
        {
            if (string.IsNullOrEmpty(FilePath)) return null;
            return s_ttfCache.GetOrAdd(FilePath, p => new TtfFont(p));
        }
    }

    public FontFamily(string name)
    {
        Name = name;
        if (s_fontCache.TryGetValue(name, out var path))
        {
            FilePath = path;
        }
        else
        {
            FilePath = s_fallbackFontPath ?? "";
        }
    }
}

public struct FontWeight
{
    public static FontWeight Bold => new FontWeight();
    public static FontWeight Normal => new FontWeight();
}

public struct FontStyle
{
    public static FontStyle Italic => new FontStyle();
    public static FontStyle Normal => new FontStyle();
}

public struct FontStretch
{
    public static FontStretch Normal => new FontStretch();
}

public class Typeface
{
    public FontFamily FontFamily { get; }
    public FontStyle Style { get; }
    public FontWeight Weight { get; }
    public FontStretch Stretch { get; }

    public Typeface(FontFamily fontFamily)
    {
        FontFamily = fontFamily;
    }

    public Typeface(FontFamily fontFamily, FontStyle style, FontWeight weight, FontStretch stretch)
    {
        FontFamily = fontFamily;
        Style = style;
        Weight = weight;
        Stretch = stretch;
    }
}
