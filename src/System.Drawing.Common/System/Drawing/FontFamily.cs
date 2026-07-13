using System.Collections.Concurrent;
using System.IO;
using ProGPU.Text;

namespace System.Drawing;

public class FontFamily : IDisposable
{
    private readonly record struct FontCacheEntry(string FilePath, int FaceIndex);

    private static readonly ConcurrentDictionary<string, FontCacheEntry> s_fontCache = new(StringComparer.OrdinalIgnoreCase);
    private static FontCacheEntry? s_fallbackFont;

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

            foreach (var key in new[] { "Arial", "Consolas", "Georgia", "Helvetica", "Roboto", "Courier New" })
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

            s_fallbackFont ??= ProbeFallbackFontFile();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FontFamily] Error initializing system font cache: {ex.Message}");
            s_fallbackFont ??= ProbeFallbackFontFile();
        }
    }

    public string Name { get; }
    internal string FilePath { get; }
    internal int FaceIndex { get; }
    internal string CacheKey => $"{FilePath}\u001f{FaceIndex}";

    public FontFamily(string name)
    {
        Name = name;
        if (s_fontCache.TryGetValue(name, out var entry))
        {
            FilePath = entry.FilePath;
            FaceIndex = entry.FaceIndex;
        }
        else
        {
            FontCacheEntry? fallback = s_fallbackFont ??= ProbeFallbackFontFile();
            FilePath = fallback?.FilePath ?? "";
            FaceIndex = fallback?.FaceIndex ?? 0;
        }
    }

    private static FontCacheEntry? ProbeFallbackFontFile()
    {
        foreach (string path in new[]
        {
            "/System/Library/Fonts/SFNS.ttf",
            "/System/Library/Fonts/HelveticaNeue.ttc",
            "/System/Library/Fonts/SFCompact.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf",
            "C:\\Windows\\Fonts\\segoeui.ttf",
            "C:\\Windows\\Fonts\\arial.ttf"
        })
        {
            if (File.Exists(path))
            {
                return new FontCacheEntry(path, 0);
            }
        }

        return null;
    }

    public static FontFamily GenericSansSerif { get; } = new FontFamily("Arial");
    public static FontFamily GenericSerif { get; } = new FontFamily("Georgia");
    public static FontFamily GenericMonospace { get; } = new FontFamily("Courier New");

    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj)
        || obj is FontFamily family
            && string.Equals(Name, family.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    public override string ToString() => $"[FontFamily: Name={Name}]";

    public void Dispose() {}
}
