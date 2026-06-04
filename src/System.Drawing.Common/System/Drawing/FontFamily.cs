using System.Collections.Concurrent;
using ProGPU.Text;

namespace System.Drawing;

public class FontFamily : IDisposable
{
    private static readonly ConcurrentDictionary<string, string> s_fontCache = new(StringComparer.OrdinalIgnoreCase);
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

            foreach (var key in new[] { "Arial", "Consolas", "Georgia", "Helvetica", "Roboto", "Courier New" })
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
            Console.WriteLine($"[FontFamily] Error initializing system font cache: {ex.Message}");
        }
    }

    public string Name { get; }
    internal string FilePath { get; }

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

    public static FontFamily GenericSansSerif { get; } = new FontFamily("Arial");
    public static FontFamily GenericSerif { get; } = new FontFamily("Georgia");
    public static FontFamily GenericMonospace { get; } = new FontFamily("Courier New");

    public void Dispose() {}
}
