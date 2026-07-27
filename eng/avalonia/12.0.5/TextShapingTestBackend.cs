using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Avalonia.Skia.UnitTests.Media;

/// <summary>
/// Selects the platform font, render, and shaping services for the unchanged
/// upstream text test bodies. The ProGPU lane is enabled at build time so no
/// runtime reflection or service probing is involved.
/// </summary>
internal static class TextShapingTestBackend
{
    internal static IPlatformRenderInterface CreateRenderInterface(long? maxResourceBytes = null)
    {
#if PROGPU_TEXT_SHAPER_TESTS
        return new Avalonia.ProGpu.PlatformRenderInterface(maxResourceBytes);
#else
        return new Avalonia.Skia.PlatformRenderInterface(maxResourceBytes);
#endif
    }

    internal static IFontManagerImpl CreateFontManager()
    {
#if PROGPU_TEXT_SHAPER_TESTS
        return new ProGpuTestFontManager();
#else
        return new CustomFontManagerImpl();
#endif
    }

    internal static ITextShaperImpl CreateTextShaper()
    {
#if PROGPU_TEXT_SHAPER_TESTS
        return new Avalonia.ProGpu.ProGpuTextShaper();
#else
        return new Avalonia.Harfbuzz.HarfBuzzTextShaper();
#endif
    }

    internal static IFontCollection GetSystemFonts(IFontManagerImpl fontManager)
    {
#if PROGPU_TEXT_SHAPER_TESTS
        return new EmbeddedFontCollection(
            FontManager.SystemFontsKey,
            new Uri(
                "resm:Avalonia.Skia.UnitTests.Assets?assembly=Avalonia.Skia.UnitTests"));
#else
        return ((CustomFontManagerImpl)fontManager).SystemFonts;
#endif
    }

#if PROGPU_TEXT_SHAPER_TESTS
    private sealed class ProGpuTestFontManager : IFontManagerImpl
    {
        private sealed record LoadedTypeface(
            IPlatformTypeface PlatformTypeface,
            GlyphTypeface GlyphTypeface);

        private static readonly Uri[] s_fontSources =
        {
            new("resm:Avalonia.Skia.UnitTests.Assets?assembly=Avalonia.Skia.UnitTests"),
            new("resm:Avalonia.Skia.UnitTests.Fonts?assembly=Avalonia.Skia.UnitTests")
        };

        private readonly Avalonia.ProGpu.FontManagerImpl _loader = new();
        private readonly object _sync = new();
        private List<LoadedTypeface>? _fonts;

        public string GetDefaultFontFamilyName() => "Noto Mono";

        public string[] GetInstalledFontFamilyNames(bool checkForUpdates = false)
        {
            var fonts = GetFonts(checkForUpdates);
            var names = new List<string>(fonts.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var font in fonts)
            {
                if (seen.Add(font.PlatformTypeface.FamilyName))
                    names.Add(font.PlatformTypeface.FamilyName);
            }

            return names.ToArray();
        }

        public bool TryMatchCharacter(
            int codepoint,
            FontStyle fontStyle,
            FontWeight fontWeight,
            FontStretch fontStretch,
            string? familyName,
            CultureInfo? culture,
            [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
        {
            var fonts = GetFonts();
            if (!string.IsNullOrWhiteSpace(familyName) &&
                TryMatchCharacter(fonts, codepoint, familyName, out platformTypeface))
            {
                return true;
            }

            return TryMatchCharacter(fonts, codepoint, null, out platformTypeface);
        }

        public bool TryCreateGlyphTypeface(
            string familyName,
            FontStyle style,
            FontWeight weight,
            FontStretch stretch,
            [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
        {
            var fonts = GetFonts();
            LoadedTypeface? best = null;
            var bestScore = int.MaxValue;
            foreach (var font in fonts)
            {
                var candidate = font.PlatformTypeface;
                if (!string.Equals(
                        candidate.FamilyName,
                        familyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var score =
                    Math.Abs((int)candidate.Weight - (int)weight) +
                    Math.Abs((int)candidate.Stretch - (int)stretch) * 100 +
                    (candidate.Style == style ? 0 : 10_000);
                if (score < bestScore)
                {
                    best = font;
                    bestScore = score;
                }
            }

            platformTypeface = best?.PlatformTypeface;
            return platformTypeface != null;
        }

        public bool TryCreateGlyphTypeface(
            Stream stream,
            FontSimulations fontSimulations,
            [NotNullWhen(true)] out IPlatformTypeface? platformTypeface) =>
            _loader.TryCreateGlyphTypeface(stream, fontSimulations, out platformTypeface);

        public bool TryGetFamilyTypefaces(
            string familyName,
            [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        {
            var result = new List<Typeface>();
            foreach (var font in GetFonts())
            {
                var candidate = font.PlatformTypeface;
                if (string.Equals(
                        candidate.FamilyName,
                        familyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new Typeface(
                        candidate.FamilyName,
                        candidate.Style,
                        candidate.Weight,
                        candidate.Stretch));
                }
            }

            familyTypefaces = result.Count == 0 ? null : result;
            return familyTypefaces != null;
        }

        private static bool TryMatchCharacter(
            IReadOnlyList<LoadedTypeface> fonts,
            int codepoint,
            string? familyName,
            [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
        {
            foreach (var font in fonts)
            {
                if (familyName != null &&
                    !string.Equals(
                        font.PlatformTypeface.FamilyName,
                        familyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (font.GlyphTypeface.CharacterToGlyphMap.TryGetGlyph(
                        codepoint,
                        out _))
                {
                    platformTypeface = font.PlatformTypeface;
                    return true;
                }
            }

            platformTypeface = null;
            return false;
        }

        private IReadOnlyList<LoadedTypeface> GetFonts(bool reload = false)
        {
            lock (_sync)
            {
                if (_fonts != null && !reload)
                    return _fonts;

                var assetLoader = AvaloniaLocator.Current.GetRequiredService<IAssetLoader>();
                var fonts = new List<LoadedTypeface>();
                var loadedAssets = new HashSet<Uri>();
                foreach (var source in s_fontSources)
                {
                    foreach (var asset in FontFamilyLoader.LoadFontAssets(source))
                    {
                        if (!loadedAssets.Add(asset))
                            continue;
                        if (asset.OriginalString.Contains(
                                "AdobeBlank",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            // Adobe Blank intentionally maps a very broad
                            // Unicode range to zero-width blank glyphs. It is a
                            // focused font-parser fixture, not a fallback font.
                            continue;
                        }

                        using var stream = assetLoader.Open(asset);
                        if (_loader.TryCreateGlyphTypeface(
                                stream,
                                FontSimulations.None,
                                out var platformTypeface) &&
                            GlyphTypeface.TryCreate(platformTypeface) is { } glyphTypeface)
                        {
                            fonts.Add(new LoadedTypeface(platformTypeface, glyphTypeface));
                        }
                    }
                }

                _fonts = fonts;
                return fonts;
            }
        }
    }
#endif
}
