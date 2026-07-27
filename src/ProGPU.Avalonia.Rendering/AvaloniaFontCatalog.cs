using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using Avalonia.Platform;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
using Avalonia.Platform.Internal;
#endif
using ProGPU.Text;
using CoreFontManager = ProGPU.Text.FontManager;

namespace Avalonia.ProGpu;

/// <summary>
/// Thin Avalonia projection over ProGPU.Text's process-wide font catalog.
/// Typeface wrappers are bounded; parsed system fonts remain owned by the
/// shared catalog.
/// </summary>
internal sealed class FontManagerImpl : IFontManagerImpl
{
    private const int TypefaceCacheLimit = 256;
    private static readonly string[] s_windowsDefaultFamilies =
        ["Segoe UI", "Arial"];
    private static readonly string[] s_macDefaultFamilies =
        ["SF Pro Text", "Helvetica Neue", "Helvetica"];
    private static readonly string[] s_linuxDefaultFamilies =
        ["Noto Sans", "DejaVu Sans", "Liberation Sans"];

    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<FontInfo>> _fontProvider;
    private readonly IReadOnlyList<FontInfo> _preferredFonts;
    private readonly bool _useSharedMatcher;
    private readonly Dictionary<TypefaceKey, ProGpuTypeface> _typefaces = new();
    private readonly Queue<TypefaceKey> _typefaceOrder = new();
    private FontInfo[]? _fontSnapshot;

    public FontManagerImpl()
        : this(
            FontApi.GetSystemFonts,
            preferredFonts: null,
            preloadSystemFonts: false,
            useSharedMatcher: true)
    {
    }

    internal FontManagerImpl(
        Func<IReadOnlyList<FontInfo>> systemFontProvider,
        IReadOnlyList<FontInfo>? preferredFonts = null,
        bool preloadSystemFonts = false)
        : this(
            systemFontProvider,
            preferredFonts,
            preloadSystemFonts,
            useSharedMatcher: false)
    {
    }

    private FontManagerImpl(
        Func<IReadOnlyList<FontInfo>> systemFontProvider,
        IReadOnlyList<FontInfo>? preferredFonts,
        bool preloadSystemFonts,
        bool useSharedMatcher)
    {
        _fontProvider = systemFontProvider ??
            throw new ArgumentNullException(nameof(systemFontProvider));
        _preferredFonts = preferredFonts ?? Array.Empty<FontInfo>();
        _useSharedMatcher = useSharedMatcher;
        if (preloadSystemFonts)
            _ = GetFonts(refresh: false);
    }

    public string GetDefaultFontFamilyName()
    {
        if (_preferredFonts.Count > 0 &&
            !string.IsNullOrWhiteSpace(_preferredFonts[0].FamilyName))
        {
            return _preferredFonts[0].FamilyName;
        }

        IReadOnlyList<string> preferences = OperatingSystem.IsWindows()
            ? s_windowsDefaultFamilies
            : OperatingSystem.IsMacOS()
                ? s_macDefaultFamilies
                : s_linuxDefaultFamilies;
        if (_useSharedMatcher)
        {
            IReadOnlyList<string> families = FontApi.Manager.FontFamilies;
            for (int preferenceIndex = 0;
                 preferenceIndex < preferences.Count;
                 preferenceIndex++)
            {
                for (int familyIndex = 0;
                     familyIndex < families.Count;
                     familyIndex++)
                {
                    if (string.Equals(
                        families[familyIndex],
                        preferences[preferenceIndex],
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return families[familyIndex];
                    }
                }
            }

            return families.Count > 0 &&
                   !string.IsNullOrWhiteSpace(families[0])
                ? families[0]
                : "sans-serif";
        }

        FontInfo[] fonts = GetFonts(refresh: false);
        for (int preferenceIndex = 0;
             preferenceIndex < preferences.Count;
             preferenceIndex++)
        {
            for (int fontIndex = 0; fontIndex < fonts.Length; fontIndex++)
            {
                if (string.Equals(
                    fonts[fontIndex].FamilyName,
                    preferences[preferenceIndex],
                    StringComparison.OrdinalIgnoreCase))
                {
                    return fonts[fontIndex].FamilyName;
                }
            }
        }

        return fonts.Length > 0 &&
               !string.IsNullOrWhiteSpace(fonts[0].FamilyName)
            ? fonts[0].FamilyName
            : "sans-serif";
    }

    public string[] GetInstalledFontFamilyNames(
        bool checkForUpdates = false)
    {
        if (_useSharedMatcher)
        {
            IReadOnlyList<string> families = FontApi.Manager.FontFamilies;
            var sharedResult = new string[families.Count];
            for (int index = 0; index < sharedResult.Length; index++)
                sharedResult[index] = families[index];
            return sharedResult;
        }

        FontInfo[] fonts = GetFonts(checkForUpdates);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < fonts.Length; index++)
        {
            if (!string.IsNullOrWhiteSpace(fonts[index].FamilyName))
                unique.Add(fonts[index].FamilyName);
        }

        var result = new string[unique.Count];
        unique.CopyTo(result);
        Array.Sort(result, StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public bool TryMatchCharacter(
        int codepoint,
        FontStyle fontStyle,
        FontWeight fontWeight,
        FontStretch fontStretch,
        string? familyName,
        CultureInfo? culture,
        [NotNullWhen(true)] out
#if AVALONIA11
        IGlyphTypeface?
#else
        IPlatformTypeface?
#endif
        platformTypeface)
    {
        platformTypeface = null;
        if ((uint)codepoint > 0x10ffffu)
            return false;

        string? requestedFamily = NormalizeFamilyName(familyName);
        FontStyleRequest style = ToStyleRequest(
            fontStyle,
            fontWeight,
            fontStretch);
        TtfFont? font = null;

        if (_useSharedMatcher)
        {
            IReadOnlyList<string>? languages =
                culture is null || string.IsNullOrWhiteSpace(culture.Name)
                    ? null
                    : new[] { culture.Name };
            if (!FontApi.Manager.TryMatchCharacter(
                requestedFamily,
                style,
                languages,
                codepoint,
                out font,
                out ushort glyph) ||
                glyph == 0)
            {
                return false;
            }
        }
        else
        {
            font = FindCharacterFont(
                requestedFamily,
                style,
                codepoint);
            if (font is null)
                return false;
        }

        if (font is null)
            return false;

        platformTypeface = GetOrCreateTypeface(
            font,
            string.IsNullOrWhiteSpace(font.FamilyName)
                ? requestedFamily ?? "sans-serif"
                : font.FamilyName,
            fontStyle,
            fontWeight,
            fontStretch);
        return true;
    }

#if AVALONIA11
    public bool TryMatchCharacter(
        int codepoint,
        FontStyle fontStyle,
        FontWeight fontWeight,
        FontStretch fontStretch,
        CultureInfo? culture,
        out Typeface typeface)
    {
        if (TryMatchCharacter(
            codepoint,
            fontStyle,
            fontWeight,
            fontStretch,
            familyName: null,
            culture,
            out IGlyphTypeface? platformTypeface))
        {
            typeface = new Typeface(
                platformTypeface.FamilyName,
                platformTypeface.Style,
                platformTypeface.Weight,
                platformTypeface.Stretch);
            return true;
        }

        typeface = default;
        return false;
    }
#endif

    public bool TryCreateGlyphTypeface(
        string familyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out
#if AVALONIA11
        IGlyphTypeface?
#else
        IPlatformTypeface?
#endif
        platformTypeface)
    {
        platformTypeface = null;
        string requestedFamily = NormalizeFamilyName(familyName)
            ?? GetDefaultFontFamilyName();
        FontStyleRequest request = ToStyleRequest(style, weight, stretch);

        TtfFont? font = _useSharedMatcher
            ? FontApi.Manager.MatchFamily(requestedFamily, request)
            : FindFamilyFont(requestedFamily, request);
        if (font is null)
            return false;

        platformTypeface = GetOrCreateTypeface(
            font,
            requestedFamily,
            style,
            weight,
            stretch);
        return true;
    }

    public bool TryCreateGlyphTypeface(
        Stream stream,
        FontSimulations fontSimulations,
        [NotNullWhen(true)] out
#if AVALONIA11
        IGlyphTypeface?
#else
        IPlatformTypeface?
#endif
        platformTypeface)
    {
        ArgumentNullException.ThrowIfNull(stream);
        platformTypeface = null;
        try
        {
            TtfFont font;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
            if (stream is AssemblyResourceSliceStream slice)
            {
                font = TtfFont.LoadEmbeddedResourceSlice(
                    slice.OpenResourceStream(),
                    slice.ResourceOffset,
                    slice.ResourceLength);
            }
            else
#endif
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                font = new TtfFont(copy.ToArray());
            }

            if (!IsRenderable(font))
                return false;

            string family = string.IsNullOrWhiteSpace(font.FamilyName)
                ? "CustomFont"
                : font.FamilyName;
            platformTypeface = new ProGpuTypeface(
                font,
                font.FontData,
                family,
                ToFontWeight(font.WeightClass),
                font.IsItalic ? FontStyle.Italic : FontStyle.Normal,
                ToFontStretch(font.WidthClass),
                fontSimulations);
            return true;
        }
        catch (Exception exception) when (IsFontLoadFailure(exception))
        {
            return false;
        }
    }

    public bool TryGetFamilyTypefaces(
        string familyName,
        [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
    {
        familyTypefaces = null;
        string? requestedFamily = NormalizeFamilyName(familyName);
        if (requestedFamily is null)
            return false;

        var result = new List<Typeface>();
        var styles = new HashSet<FontStyleRequest>();
        if (_useSharedMatcher)
        {
            IReadOnlyList<FontFace> faces =
                FontApi.Manager.GetFontStyles(requestedFamily);
            for (int index = 0; index < faces.Count; index++)
            {
                FontStyleRequest style = faces[index].Style;
                if (!styles.Add(style))
                    continue;
                result.Add(new Typeface(
                    requestedFamily,
                    ToFontStyle(style.Slant),
                    (FontWeight)style.Weight,
                    (FontStretch)style.Width));
            }
        }
        else
        {
            FontInfo[] fonts = GetFonts(refresh: false);
            for (int index = 0; index < fonts.Length; index++)
            {
                FontInfo info = fonts[index];
                if (!MatchesFamily(info, requestedFamily))
                    continue;
                var style = new FontStyleRequest(
                    info.Weight,
                    info.Width,
                    info.IsItalic
                        ? FontSlant.Italic
                        : FontSlant.Upright);
                if (!styles.Add(style))
                    continue;
                result.Add(new Typeface(
                    requestedFamily,
                    ToFontStyle(style.Slant),
                    (FontWeight)style.Weight,
                    (FontStretch)style.Width));
            }
        }

        if (result.Count == 0)
            return false;
        familyTypefaces = result;
        return true;
    }

    private FontInfo[] GetFonts(bool refresh)
    {
        lock (_gate)
        {
            if (!refresh && _fontSnapshot is not null)
                return _fontSnapshot;

            IReadOnlyList<FontInfo> systemFonts;
            try
            {
                systemFonts = _fontProvider();
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                systemFonts = Array.Empty<FontInfo>();
            }

            var seen = new HashSet<(string Path, int Face)>();
            var merged = new List<FontInfo>(
                checked(_preferredFonts.Count + systemFonts.Count));
            AddFonts(_preferredFonts, seen, merged);
            AddFonts(systemFonts, seen, merged);
            _fontSnapshot = merged.ToArray();
            return _fontSnapshot;
        }
    }

    private TtfFont? FindFamilyFont(
        string familyName,
        FontStyleRequest request)
    {
        FontInfo[] fonts = GetFonts(refresh: false);
        FontInfo? best = null;
        int bestScore = int.MaxValue;
        for (int index = 0; index < fonts.Length; index++)
        {
            FontInfo candidate = fonts[index];
            if (!MatchesFamily(candidate, familyName))
                continue;
            int score = StyleDistance(candidate, request);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best is null ? null : TryLoad(best);
    }

    private TtfFont? FindCharacterFont(
        string? familyName,
        FontStyleRequest request,
        int codepoint)
    {
        FontInfo[] fonts = GetFonts(refresh: false);
        TtfFont? preferred = familyName is null
            ? null
            : FindFamilyFont(familyName, request);
        if (preferred is not null &&
            preferred.GetGlyphIndex((uint)codepoint) != 0)
        {
            return preferred;
        }

        for (int index = 0; index < fonts.Length; index++)
        {
            TtfFont? candidate = TryLoad(fonts[index]);
            if (candidate is not null &&
                candidate.GetGlyphIndex((uint)codepoint) != 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private ProGpuTypeface GetOrCreateTypeface(
        TtfFont font,
        string familyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch)
    {
        var key = new TypefaceKey(font, familyName, style, weight, stretch);
        lock (_gate)
        {
            if (_typefaces.TryGetValue(key, out ProGpuTypeface? existing))
                return existing;

            FontSimulations simulations = FontSimulations.None;
            if ((int)weight >= 600 && font.WeightClass < 600)
                simulations |= FontSimulations.Bold;
            if (style != FontStyle.Normal && !font.IsItalic)
                simulations |= FontSimulations.Oblique;

            var created = new ProGpuTypeface(
                font,
                font.FontData,
                familyName,
                weight,
                style,
                stretch,
                simulations);
            _typefaces.Add(key, created);
            _typefaceOrder.Enqueue(key);

            while (_typefaces.Count > TypefaceCacheLimit &&
                   _typefaceOrder.TryDequeue(out TypefaceKey oldest))
            {
                _typefaces.Remove(oldest);
            }

            return created;
        }
    }

    private static TtfFont? TryLoad(FontInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.FilePath))
            return null;
        try
        {
            TtfFont font = CoreFontManager.LoadSystemFont(info);
            return IsRenderable(font) ? font : null;
        }
        catch (Exception exception) when (IsFontLoadFailure(exception))
        {
            return null;
        }
    }

    private static bool IsRenderable(TtfFont font) =>
        font.UnitsPerEm > 0 && font.NumGlyphs > 0;

    private static bool IsFontLoadFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException or
            OverflowException;

    private static FontStyleRequest ToStyleRequest(
        FontStyle style,
        FontWeight weight,
        FontStretch stretch) =>
        new(
            (int)weight,
            (int)stretch,
            style switch
            {
                FontStyle.Italic => FontSlant.Italic,
                FontStyle.Oblique => FontSlant.Oblique,
                _ => FontSlant.Upright
            });

    private static FontStyle ToFontStyle(FontSlant slant) =>
        slant switch
        {
            FontSlant.Italic => FontStyle.Italic,
            FontSlant.Oblique => FontStyle.Oblique,
            _ => FontStyle.Normal
        };

    private static FontWeight ToFontWeight(ushort weight) =>
        (FontWeight)Math.Clamp(weight == 0 ? 400 : weight, 1, 1000);

    private static FontStretch ToFontStretch(ushort width) =>
        (FontStretch)Math.Clamp(width == 0 ? 5 : width, 1, 9);

    private static int StyleDistance(
        FontInfo font,
        FontStyleRequest request)
    {
        int slant = font.IsItalic ==
                    (request.Slant != FontSlant.Upright)
            ? 0
            : 10_000;
        return slant +
               Math.Abs(font.Width - request.Width) * 1_000 +
               Math.Abs(font.Weight - request.Weight);
    }

    private static string? NormalizeFamilyName(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return null;
        string result = familyName.Trim();
        int fragment = result.LastIndexOf('#');
        if (fragment >= 0 && fragment + 1 < result.Length)
            result = result[(fragment + 1)..].Trim();
        int comma = result.IndexOf(',');
        if (comma > 0)
            result = result[..comma].Trim();
        return result.Length == 0 ? null : result;
    }

    private static bool MatchesFamily(
        FontInfo font,
        string familyName) =>
        string.Equals(
            font.FamilyName,
            familyName,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            font.Name,
            familyName,
            StringComparison.OrdinalIgnoreCase);

    private static void AddFonts(
        IReadOnlyList<FontInfo> source,
        HashSet<(string Path, int Face)> seen,
        List<FontInfo> destination)
    {
        for (int index = 0; index < source.Count; index++)
        {
            FontInfo font = source[index];
            if (string.IsNullOrWhiteSpace(font.FilePath) ||
                !seen.Add((font.FilePath, font.FaceIndex)))
            {
                continue;
            }
            destination.Add(font);
        }
    }

    private readonly record struct TypefaceKey(
        TtfFont Font,
        string FamilyName,
        FontStyle Style,
        FontWeight Weight,
        FontStretch Stretch);
}
