using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ProGPU.Text;
using System.Windows;

namespace System.Windows.Media;

public class FontFamily
{
    private readonly record struct FontCacheEntry(string FilePath, int FaceIndex);

    private static readonly ConcurrentDictionary<string, FontCacheEntry> s_fontCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, TtfFont> s_ttfCache = new(StringComparer.OrdinalIgnoreCase);
    private static FontCacheEntry? s_fallbackFont;
    private static readonly string[] s_fallbackFamilyCandidates =
    [
        "Arial",
        "Segoe UI",
        "Helvetica Neue",
        "Helvetica",
        "SF Pro Text",
        ".SF NS Text",
        "Roboto",
        "Liberation Sans",
        "DejaVu Sans",
        "Noto Sans",
        "Georgia",
        "Times New Roman"
    ];

    private static readonly Dictionary<string, string[]> s_familyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Segoe UI"] =
        [
            "Segoe UI",
            "SF Pro Text",
            ".SF NS Text",
            "Helvetica Neue",
            "Helvetica",
            "Arial",
            "Roboto",
            "Liberation Sans",
            "DejaVu Sans"
        ],
        ["Segoe UI Light"] =
        [
            "Segoe UI Light",
            "SF Pro Display Light",
            "Helvetica Neue Light",
            "Helvetica Neue",
            "Arial"
        ],
        ["Segoe UI Semibold"] =
        [
            "Segoe UI Semibold",
            "SF Pro Text Semibold",
            "Helvetica Neue Medium",
            "Helvetica Neue",
            "Arial Bold",
            "Arial"
        ],
        ["Calibri"] =
        [
            "Calibri",
            "Helvetica Neue",
            "Helvetica",
            "Arial",
            "Aptos",
            "Roboto",
            "Liberation Sans",
            "DejaVu Sans"
        ],
        ["Cambria"] =
        [
            "Cambria",
            "Georgia",
            "Times New Roman",
            "Times",
            "Liberation Serif",
            "DejaVu Serif"
        ],
        ["Consolas"] =
        [
            "Consolas",
            "Menlo",
            "SF Mono",
            "Courier New",
            "Courier",
            "Liberation Mono",
            "DejaVu Sans Mono"
        ],
        ["Courier New"] =
        [
            "Courier New",
            "Courier",
            "Menlo",
            "SF Mono",
            "Liberation Mono",
            "DejaVu Sans Mono"
        ],
        ["Microsoft Sans Serif"] =
        [
            "Microsoft Sans Serif",
            "Arial",
            "Helvetica Neue",
            "Helvetica",
            "Liberation Sans",
            "DejaVu Sans"
        ],
        ["Tahoma"] =
        [
            "Tahoma",
            "Arial",
            "Helvetica Neue",
            "Helvetica",
            "Liberation Sans",
            "DejaVu Sans"
        ],
        ["Verdana"] =
        [
            "Verdana",
            "Arial",
            "Helvetica Neue",
            "Helvetica",
            "Liberation Sans",
            "DejaVu Sans"
        ],
        ["Times New Roman"] =
        [
            "Times New Roman",
            "Times",
            "Georgia",
            "Liberation Serif",
            "DejaVu Serif"
        ]
    };

    static FontFamily()
    {
        try
        {
            var systemFonts = FontApi.GetSystemFonts();
            foreach (var font in systemFonts)
            {
                if (!string.IsNullOrEmpty(font.FamilyName))
                {
                    s_fontCache.TryAdd(font.FamilyName, new FontCacheEntry(font.FilePath, font.FaceIndex));
                }
                if (!string.IsNullOrEmpty(font.Name))
                {
                    s_fontCache.TryAdd(font.Name, new FontCacheEntry(font.FilePath, font.FaceIndex));
                }
            }

            RegisterPortableAliases();

            foreach (var key in s_fallbackFamilyCandidates)
            {
                if (s_fontCache.TryGetValue(key, out var entry))
                {
                    s_fallbackFont = entry;
                    break;
                }
            }

            if (s_fallbackFont == null && systemFonts.Count > 0)
            {
                s_fallbackFont = new FontCacheEntry(systemFonts[0].FilePath, systemFonts[0].FaceIndex);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WPF FontFamily] Error initializing system font cache: {ex.Message}");
        }
    }

    public string Name { get; }
    internal string FilePath { get; }
    internal int FaceIndex { get; }
    private string CacheKey => $"{FilePath}\u001f{FaceIndex}";

    public TtfFont? NativeFont
    {
        get
        {
            if (string.IsNullOrEmpty(FilePath)) return null;
            return s_ttfCache.GetOrAdd(CacheKey, _ => new TtfFont(FilePath, FaceIndex));
        }
    }

    public FontFamily(string name)
    {
        Name = name;
        if (TryResolveFontCacheEntry(name, out var entry))
        {
            FilePath = entry.FilePath;
            FaceIndex = entry.FaceIndex;
        }
        else
        {
            FilePath = s_fallbackFont?.FilePath ?? "";
            FaceIndex = s_fallbackFont?.FaceIndex ?? 0;
        }
    }

    private static void RegisterPortableAliases()
    {
        foreach (var alias in s_familyAliases)
        {
            if (s_fontCache.ContainsKey(alias.Key))
            {
                continue;
            }

            foreach (var candidate in alias.Value)
            {
                if (s_fontCache.TryGetValue(candidate, out var entry))
                {
                    s_fontCache.TryAdd(alias.Key, entry);
                    break;
                }
            }
        }
    }

    private static bool TryResolveFontCacheEntry(string name, out FontCacheEntry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (s_fontCache.TryGetValue(name, out entry))
        {
            return true;
        }

        foreach (var candidate in EnumerateFamilyCandidates(name))
        {
            if (s_fontCache.TryGetValue(candidate, out entry))
            {
                s_fontCache.TryAdd(name, entry);
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFamilyCandidates(string name)
    {
        foreach (var part in name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part;

            if (s_familyAliases.TryGetValue(part, out var aliases))
            {
                foreach (var alias in aliases)
                {
                    yield return alias;
                }
            }
        }
    }
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
