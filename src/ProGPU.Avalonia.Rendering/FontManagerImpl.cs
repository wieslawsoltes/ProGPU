using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Media;
using Avalonia.Platform;
using ProGPU.Text;

namespace Avalonia.ProGpu
{
    internal class FontManagerImpl : IFontManagerImpl
    {
        private const long GlyphResidentFontFileSizeThreshold = 16L * 1024L * 1024L;
        private const string WholeSystemFontFilesDiagnosticSwitch =
            "ProGPU.Avalonia.Diagnostics.UseWholeSystemFontFiles";

        private readonly record struct FontLocation(string FilePath, int FaceIndex);

        private readonly record struct TypefaceRequest(
            FontLocation Location,
            FontStyle Style,
            FontWeight Weight,
            FontStretch Stretch);

        private readonly record struct CharacterRequest(
            int Codepoint,
            FontStyle Style,
            FontWeight Weight,
            FontStretch Stretch,
            string FamilyName);

        private sealed class CachedFont
        {
            public CachedFont(FontLocation location, FontInfo info, TtfFont font)
            {
                Location = location;
                Info = info;
                Font = font;
            }

            public FontLocation Location { get; }
            public FontInfo Info { get; }
            public TtfFont Font { get; }
        }

        private sealed class SystemFontCatalog
        {
            private readonly Dictionary<string, List<FontInfo>> _byName =
                new(StringComparer.OrdinalIgnoreCase);

            public SystemFontCatalog(IEnumerable<FontInfo> fonts)
            {
                var locations = new HashSet<FontLocation>();
                var all = new List<FontInfo>();
                foreach (var font in fonts)
                {
                    if (string.IsNullOrWhiteSpace(font.FilePath))
                    {
                        continue;
                    }

                    var location = new FontLocation(font.FilePath, font.FaceIndex);
                    if (!locations.Add(location))
                    {
                        continue;
                    }

                    all.Add(font);
                    AddName(font.FamilyName, font);
                    AddName(font.Name, font);
                }

                All = all;
            }

            public IReadOnlyList<FontInfo> All { get; }

            public IReadOnlyList<FontInfo> Find(string familyOrFullName)
            {
                return _byName.TryGetValue(familyOrFullName.Trim(), out var fonts)
                    ? fonts
                    : Array.Empty<FontInfo>();
            }

            private void AddName(string name, FontInfo font)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                if (!_byName.TryGetValue(name.Trim(), out var fonts))
                {
                    fonts = new List<FontInfo>();
                    _byName.Add(name.Trim(), fonts);
                }

                fonts.Add(font);
            }
        }

        private readonly object _sync = new();
        private readonly Func<IReadOnlyList<FontInfo>> _systemFontProvider;
        private readonly IReadOnlyList<FontInfo> _preferredFonts;
        private readonly Dictionary<FontLocation, CachedFont> _fontCache = new();
        private readonly HashSet<FontLocation> _failedFonts = new();
        private readonly Dictionary<TypefaceRequest, ProGpuTypeface> _typefaceCache = new();
        private readonly Dictionary<CharacterRequest, ProGpuTypeface> _characterCache = new();
        private readonly HashSet<CharacterRequest> _failedCharacters = new();
        private Lazy<SystemFontCatalog> _systemFonts;

        public FontManagerImpl()
            : this(FontApi.GetSystemFonts, GetPlatformPreferredFonts())
        {
        }

        internal FontManagerImpl(
            Func<IReadOnlyList<FontInfo>> systemFontProvider,
            IReadOnlyList<FontInfo>? preferredFonts = null,
            bool preloadSystemFonts = false)
        {
            _systemFontProvider = systemFontProvider ?? throw new ArgumentNullException(nameof(systemFontProvider));
            _preferredFonts = preferredFonts ?? Array.Empty<FontInfo>();
            _systemFonts = CreateSystemFontCatalog();

            if (preloadSystemFonts)
            {
                ThreadPool.QueueUserWorkItem(static state => ((FontManagerImpl)state!).PreloadSystemFonts(), this);
            }
        }

        private void PreloadSystemFonts()
        {
            try
            {
                _ = GetSystemFonts();
            }
            catch
            {
                // Speculative preload failures are surfaced by the normal synchronous lookup path.
            }
        }

        public string GetDefaultFontFamilyName()
        {
            if (OperatingSystem.IsWindows())
            {
                return "Segoe UI";
            }

            return OperatingSystem.IsMacOS() ? "Arial" : "DejaVu Sans";
        }

        public string[] GetInstalledFontFamilyNames(bool checkForUpdates = false)
        {
            if (checkForUpdates)
            {
                lock (_sync)
                {
                    _systemFonts = CreateSystemFontCatalog();
                    _characterCache.Clear();
                    _failedCharacters.Clear();
                }
            }

            return GetSystemFonts().All
                .Select(font => font.FamilyName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public bool TryMatchCharacter(
#if AVALONIA11
            int codepoint,
            FontStyle fontStyle,
            FontWeight fontWeight,
            FontStretch fontStretch,
            CultureInfo? culture,
            out Typeface typeface)
        {
            if (TryMatchCharacterCore(
                    codepoint,
                    fontStyle,
                    fontWeight,
                    fontStretch,
                    null,
                    culture,
                    out var glyphTypeface))
            {
                typeface = new Typeface(
                    glyphTypeface.FamilyName,
                    glyphTypeface.Style,
                    glyphTypeface.Weight,
                    glyphTypeface.Stretch);
                return true;
            }

            typeface = default;
            return false;
        }

        private bool TryMatchCharacterCore(
#endif
            int codepoint,
            FontStyle fontStyle,
            FontWeight fontWeight,
            FontStretch fontStretch,
            string? familyName,
            CultureInfo? culture,
            [NotNullWhen(returnValue: true)] out
#if AVALONIA11
            IGlyphTypeface?
#else
            IPlatformTypeface?
#endif
            platformTypeface)
        {
            platformTypeface = null;
            if ((uint)codepoint > 0x10FFFF)
            {
                return false;
            }

            var request = new CharacterRequest(
                codepoint,
                fontStyle,
                fontWeight,
                fontStretch,
                familyName?.Trim() ?? string.Empty);

            lock (_sync)
            {
                if (_characterCache.TryGetValue(request, out var cached))
                {
                    platformTypeface = cached;
                    return true;
                }

                if (_failedCharacters.Contains(request))
                {
                    return false;
                }
            }

            foreach (var candidate in GetCharacterCandidates(request.FamilyName))
            {
                if (!FontApi.TryGetGlyphIndex(candidate, (uint)codepoint, out ushort glyphIndex) ||
                    !TryLoadFont(candidate, glyphIndex, out var cachedFont) ||
                    !cachedFont.Font.HasGlyph((uint)codepoint))
                {
                    continue;
                }

                var typeface = GetOrCreateTypeface(cachedFont, fontStyle, fontWeight, fontStretch);
                lock (_sync)
                {
                    _characterCache[request] = typeface;
                }

                platformTypeface = typeface;
                return true;
            }

            lock (_sync)
            {
                _failedCharacters.Add(request);
            }
            return false;
        }

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
            if (string.IsNullOrWhiteSpace(familyName))
            {
                familyName = GetDefaultFontFamilyName();
            }

            CachedFont? best = null;
            var bestScore = int.MaxValue;
            foreach (var candidate in GetFamilyCandidates(familyName))
            {
                if (!TryLoadFont(candidate, out var cachedFont))
                {
                    continue;
                }

                var score = GetStyleScore(cachedFont.Font, style, weight, stretch);
                if (score < bestScore)
                {
                    best = cachedFont;
                    bestScore = score;
                }
            }

            if (best == null)
            {
                return false;
            }

            platformTypeface = GetOrCreateTypeface(best, style, weight, stretch);
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
            platformTypeface = null;
            try
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var data = memory.ToArray();
                var font = new TtfFont(data);
                if (!IsRenderable(font))
                {
                    return false;
                }

                var familyName = string.IsNullOrWhiteSpace(font.FamilyName)
                    ? "CustomFont"
                    : font.FamilyName;
                platformTypeface = new ProGpuTypeface(
                    font,
                    data,
                    familyName,
                    ToFontWeight(font.WeightClass),
                    font.IsItalic ? FontStyle.Italic : FontStyle.Normal,
                    ToFontStretch(font.WidthClass),
                    fontSimulations);
                return true;
            }
            catch (Exception ex) when (IsFontLoadException(ex))
            {
                return false;
            }
        }

        public bool TryGetFamilyTypefaces(
            string familyName,
            [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        {
            familyTypefaces = null;
            var typefaces = new List<Typeface>();
            var styles = new HashSet<(FontStyle Style, FontWeight Weight, FontStretch Stretch)>();
            foreach (var candidate in GetFamilyCandidates(familyName))
            {
                if (!TryLoadFont(candidate, out var cachedFont))
                {
                    continue;
                }

                var font = cachedFont.Font;
                var style = font.IsItalic ? FontStyle.Italic : FontStyle.Normal;
                var weight = ToFontWeight(font.WeightClass);
                var stretch = ToFontStretch(font.WidthClass);
                if (styles.Add((style, weight, stretch)))
                {
                    typefaces.Add(new Typeface(familyName, style, weight, stretch));
                }
            }

            if (typefaces.Count == 0)
            {
                return false;
            }

            familyTypefaces = typefaces;
            return true;
        }

        private Lazy<SystemFontCatalog> CreateSystemFontCatalog()
        {
            return new Lazy<SystemFontCatalog>(() =>
            {
                IReadOnlyList<FontInfo> systemFonts;
                try
                {
                    systemFonts = _systemFontProvider();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    systemFonts = Array.Empty<FontInfo>();
                }

                return new SystemFontCatalog(_preferredFonts.Concat(systemFonts));
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private SystemFontCatalog GetSystemFonts()
        {
            return _systemFonts.Value;
        }

        private IEnumerable<FontInfo> GetFamilyCandidates(string familyName)
        {
            var locations = new HashSet<FontLocation>();
            foreach (var font in _preferredFonts)
            {
                if ((string.Equals(font.FamilyName, familyName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(font.Name, familyName, StringComparison.OrdinalIgnoreCase)) &&
                    locations.Add(new FontLocation(font.FilePath, font.FaceIndex)))
                {
                    yield return font;
                }
            }

            foreach (var font in GetSystemFonts().Find(familyName))
            {
                if (locations.Add(new FontLocation(font.FilePath, font.FaceIndex)))
                {
                    yield return font;
                }
            }
        }

        private IEnumerable<FontInfo> GetCharacterCandidates(string familyName)
        {
            var locations = new HashSet<FontLocation>();
            if (!string.IsNullOrEmpty(familyName))
            {
                foreach (var font in _preferredFonts)
                {
                    if ((string.Equals(font.FamilyName, familyName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(font.Name, familyName, StringComparison.OrdinalIgnoreCase)) &&
                        locations.Add(new FontLocation(font.FilePath, font.FaceIndex)))
                    {
                        yield return font;
                    }
                }
            }

            foreach (var font in _preferredFonts)
            {
                if (locations.Add(new FontLocation(font.FilePath, font.FaceIndex)))
                {
                    yield return font;
                }
            }

            if (!string.IsNullOrEmpty(familyName))
            {
                foreach (var font in GetSystemFonts().Find(familyName))
                {
                    if (locations.Add(new FontLocation(font.FilePath, font.FaceIndex)))
                    {
                        yield return font;
                    }
                }
            }

            foreach (var font in GetSystemFonts().All)
            {
                if (locations.Add(new FontLocation(font.FilePath, font.FaceIndex)))
                {
                    yield return font;
                }
            }
        }

        private bool TryLoadFont(
            FontInfo info,
            [NotNullWhen(true)] out CachedFont? cachedFont) =>
            TryLoadFont(info, null, out cachedFont);

        private bool TryLoadFont(
            FontInfo info,
            ushort? residentGlyphIndex,
            [NotNullWhen(true)] out CachedFont? cachedFont)
        {
            var location = new FontLocation(info.FilePath, info.FaceIndex);
            lock (_sync)
            {
                if (_fontCache.TryGetValue(location, out cachedFont))
                {
                    return true;
                }

                if (_failedFonts.Contains(location))
                {
                    cachedFont = null;
                    return false;
                }
            }

            try
            {
                bool glyphResident = ShouldUseGlyphResidentFont(info.FilePath);
                TtfFont font = glyphResident
                    ? TtfFont.LoadGlyphResidentFile(
                        info.FilePath,
                        info.FaceIndex,
                        residentGlyphIndex ?? 0)
                    : new TtfFont(info.FilePath, info.FaceIndex);

                if (!IsRenderable(font))
                {
                    lock (_sync)
                    {
                        _failedFonts.Add(location);
                    }
                    cachedFont = null;
                    return false;
                }

                var created = new CachedFont(location, info, font);
                lock (_sync)
                {
                    if (_fontCache.TryGetValue(location, out cachedFont))
                    {
                        return true;
                    }

                    _fontCache[location] = created;
                    cachedFont = created;
                    return true;
                }
            }
            catch (Exception ex) when (IsFontLoadException(ex))
            {
                lock (_sync)
                {
                    _failedFonts.Add(location);
                }
                cachedFont = null;
                return false;
            }
        }

        internal long CachedFontDataBytes
        {
            get
            {
                lock (_sync)
                {
                    long total = 0;
                    foreach (var cached in _fontCache.Values)
                    {
                        if (!cached.Font.UsesMappedFileStorage)
                        {
                            total += cached.Font.FontData.Length;
                        }
                    }
                    return total;
                }
            }
        }

        private static bool ShouldUseGlyphResidentFont(string filePath)
        {
            if (AppContext.TryGetSwitch(WholeSystemFontFilesDiagnosticSwitch, out bool useWholeFonts) &&
                useWholeFonts)
            {
                return false;
            }

            try
            {
                return new FileInfo(filePath).Length > GlyphResidentFontFileSizeThreshold;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        private ProGpuTypeface GetOrCreateTypeface(
            CachedFont cachedFont,
            FontStyle requestedStyle,
            FontWeight requestedWeight,
            FontStretch requestedStretch)
        {
            var request = new TypefaceRequest(
                cachedFont.Location,
                requestedStyle,
                requestedWeight,
                requestedStretch);
            lock (_sync)
            {
                if (_typefaceCache.TryGetValue(request, out var cachedTypeface))
                {
                    return cachedTypeface;
                }

                var font = cachedFont.Font;
                var actualWeight = ToFontWeight(font.WeightClass);
                var actualStyle = font.IsItalic ? FontStyle.Italic : FontStyle.Normal;
                var simulations = FontSimulations.None;
                if ((int)requestedWeight >= 600 && (int)actualWeight < 600)
                {
                    simulations |= FontSimulations.Bold;
                }

                if (requestedStyle != FontStyle.Normal && actualStyle == FontStyle.Normal)
                {
                    simulations |= FontSimulations.Oblique;
                }

                var familyName = string.IsNullOrWhiteSpace(font.FamilyName)
                    ? cachedFont.Info.FamilyName
                    : font.FamilyName;
                var typeface = new ProGpuTypeface(
                    font,
                    font.FontData,
                    familyName,
                    actualWeight,
                    actualStyle,
                    ToFontStretch(font.WidthClass),
                    simulations);
                _typefaceCache.Add(request, typeface);
                return typeface;
            }
        }

        private static int GetStyleScore(
            TtfFont font,
            FontStyle requestedStyle,
            FontWeight requestedWeight,
            FontStretch requestedStretch)
        {
            var actualStyle = font.IsItalic ? FontStyle.Italic : FontStyle.Normal;
            var styleScore = actualStyle == requestedStyle ||
                             (actualStyle == FontStyle.Italic && requestedStyle == FontStyle.Oblique)
                ? 0
                : 10_000;
            return styleScore +
                   Math.Abs((int)ToFontWeight(font.WeightClass) - (int)requestedWeight) * 10 +
                   Math.Abs((int)ToFontStretch(font.WidthClass) - (int)requestedStretch);
        }

        private static bool IsRenderable(TtfFont font)
        {
            return font.HasTrueTypeOutlines || font.HasBitmapGlyphs;
        }

        private static FontWeight ToFontWeight(ushort weightClass)
        {
            return (FontWeight)Math.Clamp(weightClass == 0 ? 400 : weightClass, 1, 1000);
        }

        private static FontStretch ToFontStretch(ushort widthClass)
        {
            return (FontStretch)Math.Clamp(widthClass == 0 ? 5 : widthClass, 1, 9);
        }

        private static bool IsFontLoadException(Exception exception)
        {
            return exception is IOException or
                UnauthorizedAccessException or
                FormatException or
                ArgumentException or
                IndexOutOfRangeException or
                OverflowException;
        }

        private static IReadOnlyList<FontInfo> GetPlatformPreferredFonts()
        {
            var fonts = new List<FontInfo>();
            if (OperatingSystem.IsMacOS())
            {
                AddFont(fonts, "Arial", "Arial", "/System/Library/Fonts/Supplemental/Arial.ttf");
                AddFont(fonts, "Arial", "Arial Bold", "/System/Library/Fonts/Supplemental/Arial Bold.ttf");
                AddFont(fonts, "Arial", "Arial Italic", "/System/Library/Fonts/Supplemental/Arial Italic.ttf");
                AddFont(fonts, "Arial", "Arial Bold Italic", "/System/Library/Fonts/Supplemental/Arial Bold Italic.ttf");
                AddFont(fonts, "Times New Roman", "Times New Roman", "/System/Library/Fonts/Supplemental/Times New Roman.ttf");
                AddFont(fonts, "Times New Roman", "Times New Roman Bold", "/System/Library/Fonts/Supplemental/Times New Roman Bold.ttf");
                AddFont(fonts, "Apple Symbols", "Apple Symbols", "/System/Library/Fonts/Apple Symbols.ttf");
                AddFont(fonts, "Apple Color Emoji", "Apple Color Emoji", "/System/Library/Fonts/Apple Color Emoji.ttc");
            }
            else if (OperatingSystem.IsWindows())
            {
                var windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                AddFont(fonts, "Segoe UI", "Segoe UI", Path.Combine(windowsFonts, "segoeui.ttf"));
                AddFont(fonts, "Segoe UI", "Segoe UI Bold", Path.Combine(windowsFonts, "segoeuib.ttf"));
                AddFont(fonts, "Segoe UI Symbol", "Segoe UI Symbol", Path.Combine(windowsFonts, "seguisym.ttf"));
                AddFont(fonts, "Segoe UI Emoji", "Segoe UI Emoji", Path.Combine(windowsFonts, "seguiemj.ttf"));
                AddFont(fonts, "Arial", "Arial", Path.Combine(windowsFonts, "arial.ttf"));
                AddFont(fonts, "Times New Roman", "Times New Roman", Path.Combine(windowsFonts, "times.ttf"));
            }
            else
            {
                AddFont(fonts, "DejaVu Sans", "DejaVu Sans", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf");
                AddFont(fonts, "Noto Sans", "Noto Sans", "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf");
                AddFont(fonts, "Noto Color Emoji", "Noto Color Emoji", "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf");
            }

            return fonts;
        }

        private static void AddFont(
            ICollection<FontInfo> fonts,
            string familyName,
            string name,
            string filePath,
            int faceIndex = 0)
        {
            if (File.Exists(filePath))
            {
                fonts.Add(new FontInfo
                {
                    FamilyName = familyName,
                    Name = name,
                    FilePath = filePath,
                    FaceIndex = faceIndex
                });
            }
        }
    }
}
