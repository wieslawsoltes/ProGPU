using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ProGPU.Text;

public class FontInfo
{
    public string Name { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int FaceIndex { get; set; }
    public int Weight { get; set; } = 400;
    public int Width { get; set; } = 5;
    public bool IsItalic { get; set; }

    public override string ToString()
    {
        return $"{Name} ({FamilyName})";
    }
}

public static class FontApi
{
    private static readonly object s_cachedSystemFontsLock = new();
    private static List<FontInfo>? s_cachedSystemFonts;
    private static Task? s_systemFontWarmup;
    private static readonly ConcurrentDictionary<(string Path, int FaceIndex), Lazy<byte[]?>> s_characterMaps = new();
    private static TtfFont? s_platformFallbackFont;
    private static readonly object s_platformFallbackFontsLock = new();
    private static TtfFont[] s_platformFallbackFonts = [];
    private static Lazy<TtfFont>[] s_lazyPlatformFallbackFonts = [];

    /// <summary>
    /// Gets the first font registered by a platform host for environments without discoverable system fonts.
    /// </summary>
    public static TtfFont? PlatformFallbackFont => Volatile.Read(ref s_platformFallbackFont);

    /// <summary>
    /// Gets the process-wide font manager used by ProGPU text, WinUI, and compatibility layers.
    /// </summary>
    public static FontManager Manager => FontManager.Default;

    /// <summary>
    /// Registers a process-wide fallback without replacing a font supplied by an earlier platform host.
    /// </summary>
    public static void RegisterPlatformFallbackFont(TtfFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        Manager.RegisterFont(font, isFallback: true);
        Interlocked.CompareExchange(ref s_platformFallbackFont, font, null);

        lock (s_platformFallbackFontsLock)
        {
            var current = s_platformFallbackFonts;
            for (int index = 0; index < current.Length; index++)
            {
                if (ReferenceEquals(current[index], font)) return;
            }

            var updated = new TtfFont[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[^1] = font;
            Volatile.Write(ref s_platformFallbackFonts, updated);
        }
    }

    /// <summary>
    /// Registers a fallback that is loaded only after an already-active font misses a glyph.
    /// </summary>
    public static void RegisterPlatformFallbackFont(Lazy<TtfFont> font)
    {
        ArgumentNullException.ThrowIfNull(font);
        Manager.RegisterFont(font, isFallback: true);
        lock (s_platformFallbackFontsLock)
        {
            var current = s_lazyPlatformFallbackFonts;
            for (int index = 0; index < current.Length; index++)
            {
                if (ReferenceEquals(current[index], font)) return;
            }

            var updated = new Lazy<TtfFont>[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[^1] = font;
            Volatile.Write(ref s_lazyPlatformFallbackFonts, updated);
        }
    }

    /// <summary>
    /// Resolves a glyph from platform-owned fallback faces. The common path calls
    /// this only after the requested font reports glyph zero.
    /// </summary>
    public static bool TryResolvePlatformFallback(
        TtfFont requestedFont,
        int codePoint,
        out TtfFont? fallbackFont,
        out ushort glyphIndex)
    {
        return TryResolvePlatformFallback(
            requestedFont,
            FontStyleRequest.FromFont(requestedFont),
            codePoint,
            out fallbackFont,
            out glyphIndex);
    }

    /// <summary>
    /// Resolves a platform fallback while preserving the caller's original
    /// style coordinates, including values clamped by the requested face.
    /// </summary>
    public static bool TryResolvePlatformFallback(
        TtfFont requestedFont,
        FontStyleRequest requestedStyle,
        int codePoint,
        out TtfFont? fallbackFont,
        out ushort glyphIndex)
    {
        if (Manager.TryMatchCharacter(
                requestedFont.FamilyName,
                requestedStyle,
                languageTags: null,
                codePoint,
                requestedFont,
                out fallbackFont,
                out glyphIndex))
        {
            return true;
        }

        // Retain the legacy arrays for binary/source compatibility with hosts that
        // registered before FontManager became the process-wide authority.
        var fonts = Volatile.Read(ref s_platformFallbackFonts);
        for (int index = 0; index < fonts.Length; index++)
        {
            var candidate = fonts[index];
            if (ReferenceEquals(candidate, requestedFont)) continue;

            TtfFont styledCandidate = Manager.MatchTypeface(
                candidate,
                requestedStyle);
            ushort candidateGlyph = styledCandidate.GetGlyphIndex(
                (uint)codePoint);
            if (candidateGlyph != 0)
            {
                fallbackFont = styledCandidate;
                glyphIndex = candidateGlyph;
                return true;
            }
        }

        var lazyFonts = Volatile.Read(ref s_lazyPlatformFallbackFonts);
        for (int index = 0; index < lazyFonts.Length; index++)
        {
            TtfFont? candidate = TryLoadLazyPlatformFallback(lazyFonts[index]);
            if (candidate is null) continue;
            if (ReferenceEquals(candidate, requestedFont)) continue;

            TtfFont styledCandidate = Manager.MatchTypeface(
                candidate,
                requestedStyle);
            ushort candidateGlyph = styledCandidate.GetGlyphIndex(
                (uint)codePoint);
            if (candidateGlyph != 0)
            {
                fallbackFont = styledCandidate;
                glyphIndex = candidateGlyph;
                return true;
            }
        }

        fallbackFont = null;
        glyphIndex = 0;
        return false;
    }

    private static TtfFont? TryLoadLazyPlatformFallback(Lazy<TtfFont> lazyFont)
    {
        try
        {
            return lazyFont.Value;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProGpuTextDiagnostics.WriteLine($"[FontApi] Failed to load platform fallback font: {exception.Message}");
            RemoveFailedLazyPlatformFallback(lazyFont);
            return null;
        }
    }

    private static void RemoveFailedLazyPlatformFallback(Lazy<TtfFont> failedFont)
    {
        lock (s_platformFallbackFontsLock)
        {
            Lazy<TtfFont>[] current = s_lazyPlatformFallbackFonts;
            int failedIndex = Array.IndexOf(current, failedFont);
            if (failedIndex < 0) return;

            var updated = new Lazy<TtfFont>[current.Length - 1];
            if (failedIndex > 0)
            {
                Array.Copy(current, 0, updated, 0, failedIndex);
            }
            if (failedIndex < current.Length - 1)
            {
                Array.Copy(current, failedIndex + 1, updated, failedIndex, current.Length - failedIndex - 1);
            }
            Volatile.Write(ref s_lazyPlatformFallbackFonts, updated);
        }
    }

    public static List<FontInfo> GetSystemFonts()
    {
        return new List<FontInfo>(GetCachedSystemFonts());
    }

    // FontManager only reads the process-wide catalog. Keep the public copy-on-return
    // ownership contract without cloning the complete catalog during fallback lookup.
    internal static IReadOnlyList<FontInfo> GetSystemFontsView() => GetCachedSystemFonts();

    public static Task WarmUpSystemFontsAsync()
    {
        lock (s_cachedSystemFontsLock)
        {
            return s_systemFontWarmup ??= Task.Run(static () =>
            {
                _ = GetCachedSystemFonts();
            });
        }
    }

    private static List<FontInfo> ScanSystemFonts()
    {
        var list = new List<FontInfo>();
        var scannedFiles = new HashSet<string>(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0
        };

        foreach (var dir in GetSystemFontDirectories())
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.*", enumerationOptions))
                {
                    var ext = Path.GetExtension(file);
                    if ((ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)) &&
                        scannedFiles.Add(file))
                    {
                        list.AddRange(ParseFontInfos(file));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                continue;
            }
        }

        // Font files frequently repeat the same family and path values across
        // faces. Canonicalize equal strings within this immutable process
        // snapshot instead of retaining one managed string per metadata
        // record. This is a local pool, not String.Intern: entries remain
        // collectible with the catalog.
        CanonicalizeCatalogStrings(list);

        // Deduplicate without allocating one concatenated identity string per
        // face during startup.
        var seen = new HashSet<FontCatalogIdentity>(
            FontCatalogIdentityComparer.Instance);
        var uniqueList = new List<FontInfo>();
        foreach (var font in list)
        {
            var key = new FontCatalogIdentity(
                font.Name,
                font.FilePath,
                font.FaceIndex);
            if (seen.Add(key))
            {
                uniqueList.Add(font);
            }
        }

        uniqueList.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return uniqueList;
    }

    internal static void CanonicalizeCatalogStrings(
        IReadOnlyList<FontInfo> fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < fonts.Count; index++)
        {
            FontInfo font = fonts[index];
            font.Name = GetCanonicalString(strings, font.Name);
            font.FamilyName = GetCanonicalString(strings, font.FamilyName);
            font.FilePath = GetCanonicalString(strings, font.FilePath);
        }
    }

    private static string GetCanonicalString(
        Dictionary<string, string> strings,
        string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (strings.TryGetValue(value, out string? canonical))
            return canonical;
        strings.Add(value, value);
        return value;
    }

    private readonly record struct FontCatalogIdentity(
        string Name,
        string FilePath,
        int FaceIndex);

    private sealed class FontCatalogIdentityComparer
        : IEqualityComparer<FontCatalogIdentity>
    {
        internal static FontCatalogIdentityComparer Instance { get; } = new();

        public bool Equals(
            FontCatalogIdentity left,
            FontCatalogIdentity right) =>
            left.FaceIndex == right.FaceIndex &&
            string.Equals(
                left.Name,
                right.Name,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                left.FilePath,
                right.FilePath,
                StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(FontCatalogIdentity value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.FilePath),
                value.FaceIndex);
    }

    public static FontInfo? FindSystemFont(params string[] familyOrFullNames)
    {
        if (familyOrFullNames.Length == 0)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in familyOrFullNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name.Trim());
            }
        }

        if (names.Count == 0)
        {
            return null;
        }

        foreach (var font in GetCachedSystemFonts())
        {
            if (names.Contains(font.FamilyName) || names.Contains(font.Name))
            {
                return font;
            }
        }

        return null;
    }

    public static bool ContainsGlyph(FontInfo font, uint codePoint)
    {
        return TryGetGlyphIndex(font, codePoint, out _);
    }

    internal static bool TryGetGlyphIndex(FontInfo font, uint codePoint, out ushort glyphIndex)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (codePoint > 0x10FFFF || string.IsNullOrWhiteSpace(font.FilePath))
        {
            glyphIndex = 0;
            return false;
        }

        var key = (font.FilePath, font.FaceIndex);
        var characterMap = s_characterMaps.GetOrAdd(
            key,
            static value => new Lazy<byte[]?>(
                () => SfntFontMetadataReader.TryReadCharacterMap(value.Path, value.FaceIndex, out var cmap)
                    ? cmap
                    : null,
                isThreadSafe: true)).Value;
        glyphIndex = 0;
        return characterMap is not null &&
               SfntFontFace.TryGetGlyphIndexFromCmap(characterMap, codePoint, out glyphIndex) &&
               glyphIndex != 0;
    }

    private static IReadOnlyList<FontInfo> GetCachedSystemFonts()
    {
        lock (s_cachedSystemFontsLock)
        {
            s_cachedSystemFonts ??= ScanSystemFonts();
            return s_cachedSystemFonts;
        }
    }

    public static FontInfo? ParseFontInfo(string file)
    {
        var infos = ParseFontInfos(file);
        return infos.Count > 0 ? infos[0] : null;
    }

    public static List<FontInfo> ParseFontInfos(string file)
    {
        try
        {
            if (!SfntFontMetadataReader.TryReadFontInfos(file, out List<FontInfo> infos))
            {
                return new List<FontInfo> { CreateFallbackInfo(file, 0) };
            }

            return infos;
        }
        catch
        {
            return new List<FontInfo> { CreateFallbackInfo(file, 0) };
        }
    }

    public static IEnumerable<string> GetSystemFontDirectories()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/System/Library/Fonts/Supplemental";
            yield return "/Library/Fonts";
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Fonts");
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share",
                "fonts");
        }
    }

    private static FontInfo CreateFallbackInfo(string file, int faceIndex)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return new FontInfo
        {
            Name = name,
            FamilyName = name,
            FilePath = file,
            FaceIndex = faceIndex
        };
    }
}
