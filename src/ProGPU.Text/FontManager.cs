using System.Collections.Concurrent;
using System.Globalization;

namespace ProGPU.Text;

public enum FontSlant
{
    Upright,
    Italic,
    Oblique
}

public readonly record struct FontStyleRequest(int Weight, int Width, FontSlant Slant)
{
    public static FontStyleRequest Normal { get; } = new(400, 5, FontSlant.Upright);

    public FontStyleRequest(int weight = 400, int width = 5)
        : this(weight, width, FontSlant.Upright)
    {
    }

    public static FontStyleRequest FromFont(TtfFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        return new FontStyleRequest(
            font.WeightClass == 0 ? 400 : font.WeightClass,
            font.WidthClass == 0 ? 5 : font.WidthClass,
            font.IsItalic ? FontSlant.Italic : FontSlant.Upright);
    }
}

public sealed class FontFace
{
    private readonly Func<TtfFont?> _loader;

    internal FontFace(string familyName, string name, FontStyleRequest style, Func<TtfFont?> loader)
    {
        FamilyName = familyName;
        Name = name;
        Style = style;
        _loader = loader;
    }

    public string FamilyName { get; }
    public string Name { get; }
    public FontStyleRequest Style { get; }
    public TtfFont? Load() => _loader();
}

/// <summary>
/// Process-wide, platform-neutral font catalog and matcher. System files and
/// host-registered embedded fonts share the same family, style, and character
/// fallback rules. Metadata and cmaps are inspected before a full face is parsed.
/// </summary>
public sealed class FontManager
{
    private sealed class RegisteredFace
    {
        public required string FamilyName { get; init; }
        public required FontStyleRequest Style { get; init; }
        public required Lazy<TtfFont> Font { get; init; }
        public required bool IsFallback { get; init; }
    }

    private static readonly Lazy<FontManager> s_default = new(
        static () => new FontManager(),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<(string Path, int FaceIndex), Lazy<TtfFont>> s_fileFonts = new();

    private readonly object _registeredLock = new();
    private RegisteredFace[] _registeredFaces = [];
    private readonly ConcurrentDictionary<(TtfFont Font, FontStyleRequest Style), TtfFont> _styleMatches = new();

    public static FontManager Default => s_default.Value;

    public IReadOnlyList<string> FontFamilies
    {
        get
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            RegisteredFace[] registered = Volatile.Read(ref _registeredFaces);
            for (var index = 0; index < registered.Length; index++)
            {
                if (!string.IsNullOrWhiteSpace(registered[index].FamilyName) &&
                    names.Add(registered[index].FamilyName))
                {
                    result.Add(registered[index].FamilyName);
                }
            }

            IReadOnlyList<FontInfo> system = FontApi.GetSystemFonts();
            for (var index = 0; index < system.Count; index++)
            {
                if (names.Add(system[index].FamilyName))
                {
                    result.Add(system[index].FamilyName);
                }
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }

    public int FontFamilyCount => FontFamilies.Count;

    public IReadOnlyList<FontFace> GetFontStyles(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
        {
            return [];
        }

        var result = new List<FontFace>();
        var styles = new HashSet<FontStyleRequest>();
        RegisteredFace[] registered = Volatile.Read(ref _registeredFaces);
        for (var index = 0; index < registered.Length; index++)
        {
            RegisteredFace face = registered[index];
            if (!face.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new FontFace(
                face.FamilyName,
                GetStyleName(face.FamilyName, face.Style),
                face.Style,
                () => TryGetRegisteredFont(face)));
            styles.Add(face.Style);
        }

        IReadOnlyList<FontInfo> system = FontApi.GetSystemFonts();
        for (var index = 0; index < system.Count; index++)
        {
            FontInfo info = system[index];
            if (!info.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FontStyleRequest style = new(info.Weight, info.Width, info.IsItalic ? FontSlant.Italic : FontSlant.Upright);
            if (!styles.Add(style))
            {
                continue;
            }
            result.Add(new FontFace(info.FamilyName, info.Name, style, () => TryLoadSystemFont(info)));
        }

        result.Sort(static (left, right) =>
        {
            int slant = left.Style.Slant.CompareTo(right.Style.Slant);
            if (slant != 0) return slant;
            int width = left.Style.Width.CompareTo(right.Style.Width);
            return width != 0 ? width : left.Style.Weight.CompareTo(right.Style.Weight);
        });
        return result;
    }

    public void RegisterFont(TtfFont font, bool isFallback = false)
    {
        ArgumentNullException.ThrowIfNull(font);
        var lazy = new Lazy<TtfFont>(() => font, LazyThreadSafetyMode.PublicationOnly);
        _ = lazy.Value;
        RegisterFont(
            string.IsNullOrWhiteSpace(font.FamilyName) ? font.FullName : font.FamilyName,
            lazy,
            FontStyleRequest.FromFont(font),
            isFallback);
    }

    public void RegisterFont(Lazy<TtfFont> font, bool isFallback = false)
    {
        ArgumentNullException.ThrowIfNull(font);
        RegisterFontCore(string.Empty, font, FontStyleRequest.Normal, isFallback);
    }

    public void RegisterFont(
        string familyName,
        Lazy<TtfFont> font,
        FontStyleRequest style = default,
        bool isFallback = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        ArgumentNullException.ThrowIfNull(font);
        if (style == default)
        {
            style = FontStyleRequest.Normal;
        }

        RegisterFontCore(familyName.Trim(), font, style, isFallback);
    }

    private void RegisterFontCore(
        string familyName,
        Lazy<TtfFont> font,
        FontStyleRequest style,
        bool isFallback)
    {
        lock (_registeredLock)
        {
            RegisteredFace[] current = _registeredFaces;
            for (var index = 0; index < current.Length; index++)
            {
                RegisteredFace face = current[index];
                if (ReferenceEquals(face.Font, font) ||
                    (font.IsValueCreated && face.Font.IsValueCreated && ReferenceEquals(face.Font.Value, font.Value)))
                {
                    return;
                }
            }

            var updated = new RegisteredFace[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[^1] = new RegisteredFace
            {
                FamilyName = familyName,
                Font = font,
                Style = NormalizeStyle(style),
                IsFallback = isFallback
            };
            Volatile.Write(ref _registeredFaces, updated);
            _styleMatches.Clear();
        }
    }

    public TtfFont? MatchFamily(string? familyName, FontStyleRequest style = default)
    {
        if (string.IsNullOrWhiteSpace(familyName))
        {
            return null;
        }

        style = NormalizeStyle(style);
        RegisteredFace? registered = FindRegisteredFace(familyName, style);
        if (registered is not null)
        {
            return ApplyStyleVariations(TryGetRegisteredFont(registered), style);
        }

        FontInfo? system = FindSystemFace(familyName, style, requiredCodePoint: null);
        return system is null ? null : ApplyStyleVariations(TryLoadSystemFont(system), style);
    }

    public TtfFont MatchTypeface(TtfFont typeface, FontStyleRequest style)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        style = NormalizeStyle(style);
        if (typeface.IsVariableFont)
        {
            return ApplyStyleVariations(typeface, style) ?? typeface;
        }
        if (GetStyleDistance(FontStyleRequest.FromFont(typeface), style) == 0)
        {
            return typeface;
        }
        return _styleMatches.GetOrAdd(
            (typeface, style),
            key => MatchFamily(key.Font.FamilyName, key.Style) ?? key.Font);
    }

    public bool TryMatchCharacter(
        string? familyName,
        FontStyleRequest style,
        IReadOnlyList<string>? languageTags,
        int codePoint,
        TtfFont? excludedFont,
        out TtfFont? font,
        out ushort glyphIndex)
    {
        font = null;
        glyphIndex = 0;
        if ((uint)codePoint > 0x10FFFFu)
        {
            return false;
        }

        style = NormalizeStyle(style);
        var visitedRegistered = new HashSet<RegisteredFace>();
        var visitedSystem = new HashSet<(string Path, int FaceIndex)>();

        if (!string.IsNullOrWhiteSpace(familyName) &&
            TryRegisteredFamilyCharacter(familyName, style, codePoint, excludedFont, visitedRegistered, out font, out glyphIndex))
        {
            return true;
        }

        IReadOnlyList<string> preferredFamilies = GetFallbackFamilyPreferences(languageTags, codePoint);
        for (var index = 0; index < preferredFamilies.Count; index++)
        {
            string preferredFamily = preferredFamilies[index];
            if (TryRegisteredFamilyCharacter(preferredFamily, style, codePoint, excludedFont, visitedRegistered, out font, out glyphIndex) ||
                TrySystemFamilyCharacter(preferredFamily, style, codePoint, excludedFont, visitedSystem, out font, out glyphIndex))
            {
                return true;
            }
        }

        RegisteredFace[] registered = Volatile.Read(ref _registeredFaces);
        for (var pass = 0; pass < 2; pass++)
        {
            bool fallbackOnly = pass == 0;
            for (var index = 0; index < registered.Length; index++)
            {
                RegisteredFace candidate = registered[index];
                if ((fallbackOnly && !candidate.IsFallback) || !visitedRegistered.Add(candidate))
                {
                    continue;
                }

                if (TryGetRenderableGlyph(candidate, style, codePoint, excludedFont, out font, out glyphIndex))
                {
                    return true;
                }
            }
        }

        IReadOnlyList<FontInfo> systemFonts = FontApi.GetSystemFonts();
        for (var stylePass = 0; stylePass < 2; stylePass++)
        {
            bool requireStyle = stylePass == 0;
            for (var index = 0; index < systemFonts.Count; index++)
            {
                FontInfo candidate = systemFonts[index];
                if ((requireStyle && GetStyleDistance(candidate, style) != 0) ||
                    !visitedSystem.Add((candidate.FilePath, candidate.FaceIndex)) ||
                    !FontApi.TryGetGlyphIndex(candidate, (uint)codePoint, out ushort metadataGlyph))
                {
                    continue;
                }

                TtfFont? loaded = ApplyStyleVariations(TryLoadSystemFont(candidate), style);
                if (loaded is null || ReferenceEquals(loaded, excludedFont))
                {
                    continue;
                }

                ushort candidateGlyph = loaded.GetGlyphIndex((uint)codePoint);
                if (candidateGlyph != 0 && CanRenderGlyph(loaded, candidateGlyph, codePoint))
                {
                    font = loaded;
                    glyphIndex = candidateGlyph == 0 ? metadataGlyph : candidateGlyph;
                    return true;
                }
            }
        }

        return false;
    }

    public bool TryMatchCharacter(
        string? familyName,
        FontStyleRequest style,
        IReadOnlyList<string>? languageTags,
        int codePoint,
        out TtfFont? font,
        out ushort glyphIndex) =>
        TryMatchCharacter(familyName, style, languageTags, codePoint, null, out font, out glyphIndex);

    public TtfFont? MatchCharacter(
        string? familyName,
        FontStyleRequest style,
        IReadOnlyList<string>? languageTags,
        int codePoint) =>
        TryMatchCharacter(familyName, style, languageTags, codePoint, out TtfFont? font, out _) ? font : null;

    public static TtfFont LoadSystemFont(FontInfo info)
    {
        var key = (info.FilePath, info.FaceIndex);
        Lazy<TtfFont> lazy = s_fileFonts.GetOrAdd(
            key,
            static value => new Lazy<TtfFont>(
                () => new TtfFont(value.Path, value.FaceIndex),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            s_fileFonts.TryRemove(key, out _);
            throw;
        }
    }

    public static IReadOnlyList<string> GetFallbackFamilyPreferences(
        IReadOnlyList<string>? languageTags,
        int codePoint)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(params string[] families)
        {
            for (var index = 0; index < families.Length; index++)
            {
                if (seen.Add(families[index]))
                {
                    result.Add(families[index]);
                }
            }
        }

        void AddLanguage(string language)
        {
            string normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
            if (normalized == "ar" || normalized.StartsWith("ar-", StringComparison.Ordinal))
            {
                Add("Geeza Pro", "Noto Naskh Arabic", "Noto Sans Arabic", "Segoe UI",
                    "Traditional Arabic", "Arabic Typesetting", "Tahoma", "Arial", "DejaVu Sans");
            }
            else if (normalized == "ja" || normalized.StartsWith("ja-", StringComparison.Ordinal))
            {
                Add("Hiragino Sans", "Hiragino Kaku Gothic ProN", "Yu Gothic", "Meiryo",
                    "Noto Sans CJK JP", "Noto Sans JP", ".Aqua Kana");
            }
            else if (normalized == "ko" || normalized.StartsWith("ko-", StringComparison.Ordinal))
            {
                Add("Apple SD Gothic Neo", "AppleGothic", "Malgun Gothic", "Noto Sans CJK KR", "Noto Sans KR");
            }
            else if (normalized == "zh" || normalized.StartsWith("zh-", StringComparison.Ordinal))
            {
                bool traditional = normalized.Contains("-hant", StringComparison.Ordinal) ||
                                   normalized.EndsWith("-tw", StringComparison.Ordinal) ||
                                   normalized.EndsWith("-hk", StringComparison.Ordinal) ||
                                   normalized.EndsWith("-mo", StringComparison.Ordinal);
                if (traditional)
                {
                    Add("PingFang TC", "Heiti TC", "Noto Sans CJK TC", "Noto Sans TC",
                        "Microsoft JhengHei", "Songti TC");
                }
                else
                {
                    Add("PingFang SC", "Hiragino Sans GB", "Heiti SC", "Noto Sans CJK SC",
                        "Noto Sans SC", "Microsoft YaHei", "SimHei", "Songti SC",
                        "Noto Sans CJK JP", "Noto Sans JP");
                }
            }
        }

        if (languageTags is not null)
        {
            for (var index = 0; index < languageTags.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(languageTags[index]))
                {
                    AddLanguage(languageTags[index]);
                }
            }
        }

        if (IsArabic(codePoint))
        {
            AddLanguage("ar");
        }
        else if (IsHebrew(codePoint))
        {
            Add("Arial Hebrew", "Lucida Grande", "Noto Sans Hebrew", "Segoe UI", "Arial", "DejaVu Sans");
        }
        else if (codePoint is >= 0x3040 and <= 0x30FF)
        {
            AddLanguage("ja");
        }
        else if (codePoint is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF)
        {
            AddLanguage("ko");
        }
        else if (codePoint is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF)
        {
            AddLanguage("zh-Hans");
        }
        else if (codePoint is >= 0x0020 and <= 0x024F or >= 0x0370 and <= 0x052F or
                 >= 0x1E00 and <= 0x1FFF or >= 0x2DE0 and <= 0x2DFF or >= 0xA640 and <= 0xA69F)
        {
            Add("Helvetica", "Arial", "Segoe UI", "Noto Sans", "DejaVu Sans", "Liberation Sans");
        }

        return result;
    }

    private static FontStyleRequest NormalizeStyle(FontStyleRequest style) =>
        style == default
            ? FontStyleRequest.Normal
            : new FontStyleRequest(Math.Clamp(style.Weight, 1, 1000), Math.Clamp(style.Width, 1, 9), style.Slant);

    private static string GetStyleName(string familyName, FontStyleRequest style)
    {
        string weight = style.Weight switch
        {
            <= 100 => "Thin",
            <= 200 => "Extra Light",
            <= 300 => "Light",
            <= 400 => "Regular",
            <= 500 => "Medium",
            <= 600 => "Semi Bold",
            <= 700 => "Bold",
            <= 800 => "Extra Bold",
            _ => "Black"
        };
        return style.Slant == FontSlant.Upright ? weight : $"{weight} Italic";
    }

    private RegisteredFace? FindRegisteredFace(string familyName, FontStyleRequest style)
    {
        RegisteredFace? best = null;
        var bestDistance = int.MaxValue;
        RegisteredFace[] registered = Volatile.Read(ref _registeredFaces);
        for (var index = 0; index < registered.Length; index++)
        {
            RegisteredFace candidate = registered[index];
            if (!candidate.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int distance = GetStyleDistance(candidate.Style, style);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    private FontInfo? FindSystemFace(string familyName, FontStyleRequest style, int? requiredCodePoint)
    {
        FontInfo? best = null;
        var bestDistance = int.MaxValue;
        IReadOnlyList<FontInfo> system = FontApi.GetSystemFonts();
        for (var index = 0; index < system.Count; index++)
        {
            FontInfo candidate = system[index];
            if ((!candidate.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase) &&
                 !candidate.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase)) ||
                (requiredCodePoint.HasValue &&
                 !FontApi.TryGetGlyphIndex(candidate, (uint)requiredCodePoint.Value, out _)))
            {
                continue;
            }

            int distance = GetStyleDistance(candidate, style);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    private bool TryRegisteredFamilyCharacter(
        string familyName,
        FontStyleRequest style,
        int codePoint,
        TtfFont? excludedFont,
        HashSet<RegisteredFace> visited,
        out TtfFont? font,
        out ushort glyphIndex)
    {
        RegisteredFace[] registered = Volatile.Read(ref _registeredFaces);
        var candidates = new List<RegisteredFace>();
        for (var index = 0; index < registered.Length; index++)
        {
            if (registered[index].FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(registered[index]);
            }
        }

        candidates.Sort((left, right) => GetStyleDistance(left.Style, style).CompareTo(GetStyleDistance(right.Style, style)));
        for (var index = 0; index < candidates.Count; index++)
        {
            RegisteredFace candidate = candidates[index];
            if (visited.Add(candidate) &&
                TryGetRenderableGlyph(candidate, style, codePoint, excludedFont, out font, out glyphIndex))
            {
                return true;
            }
        }

        font = null;
        glyphIndex = 0;
        return false;
    }

    private bool TrySystemFamilyCharacter(
        string familyName,
        FontStyleRequest style,
        int codePoint,
        TtfFont? excludedFont,
        HashSet<(string Path, int FaceIndex)> visited,
        out TtfFont? font,
        out ushort glyphIndex)
    {
        FontInfo? candidate = FindSystemFace(familyName, style, codePoint);
        if (candidate is null || !visited.Add((candidate.FilePath, candidate.FaceIndex)))
        {
            font = null;
            glyphIndex = 0;
            return false;
        }

        TtfFont? loaded = ApplyStyleVariations(TryLoadSystemFont(candidate), style);
        if (loaded is null || ReferenceEquals(loaded, excludedFont))
        {
            font = null;
            glyphIndex = 0;
            return false;
        }

        ushort glyph = loaded.GetGlyphIndex((uint)codePoint);
        if (glyph == 0 || !CanRenderGlyph(loaded, glyph, codePoint))
        {
            font = null;
            glyphIndex = 0;
            return false;
        }

        font = loaded;
        glyphIndex = glyph;
        return true;
    }

    private static bool TryGetRenderableGlyph(
        RegisteredFace face,
        FontStyleRequest style,
        int codePoint,
        TtfFont? excludedFont,
        out TtfFont? font,
        out ushort glyphIndex)
    {
        TtfFont? candidate = ApplyStyleVariations(TryGetRegisteredFont(face), style);
        if (candidate is null || ReferenceEquals(candidate, excludedFont))
        {
            font = null;
            glyphIndex = 0;
            return false;
        }

        ushort glyph = candidate.GetGlyphIndex((uint)codePoint);
        if (glyph == 0 || !CanRenderGlyph(candidate, glyph, codePoint))
        {
            font = null;
            glyphIndex = 0;
            return false;
        }

        font = candidate;
        glyphIndex = glyph;
        return true;
    }

    private static TtfFont? TryGetRegisteredFont(RegisteredFace face)
    {
        try
        {
            return face.Font.Value;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProGpuTextDiagnostics.WriteLine($"[FontManager] Failed to load registered font '{face.FamilyName}': {exception.Message}");
            return null;
        }
    }

    private static TtfFont? TryLoadSystemFont(FontInfo info)
    {
        try
        {
            return LoadSystemFont(info);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProGpuTextDiagnostics.WriteLine($"[FontManager] Failed to load system font '{info.Name}': {exception.Message}");
            return null;
        }
    }

    private static TtfFont? ApplyStyleVariations(TtfFont? font, FontStyleRequest style)
    {
        if (font is null || !font.IsVariableFont)
        {
            return font;
        }

        var settings = new List<FontVariationSetting>(3);
        IReadOnlyList<FontVariationAxis> axes = font.VariationAxes;
        for (var index = 0; index < axes.Count; index++)
        {
            FontVariationAxis axis = axes[index];
            switch (axis.Tag)
            {
                case "wght":
                    settings.Add(new FontVariationSetting(axis.Tag, style.Weight));
                    break;
                case "wdth":
                    settings.Add(new FontVariationSetting(axis.Tag, WidthClassToPercent(style.Width)));
                    break;
                case "ital":
                    settings.Add(new FontVariationSetting(
                        axis.Tag,
                        style.Slant == FontSlant.Upright ? axis.Minimum : axis.Maximum));
                    break;
                case "slnt":
                    settings.Add(new FontVariationSetting(
                        axis.Tag,
                        style.Slant == FontSlant.Upright
                            ? Math.Clamp(0f, axis.Minimum, axis.Maximum)
                            : MathF.Abs(axis.Minimum) >= MathF.Abs(axis.Maximum)
                                ? axis.Minimum
                                : axis.Maximum));
                    break;
            }
        }

        return settings.Count == 0 ? font : font.WithVariations(settings);
    }

    private static float WidthClassToPercent(int width) => width switch
    {
        <= 1 => 50f,
        2 => 62.5f,
        3 => 75f,
        4 => 87.5f,
        5 => 100f,
        6 => 112.5f,
        7 => 125f,
        8 => 150f,
        _ => 200f
    };

    private static int GetStyleDistance(FontInfo actual, FontStyleRequest requested) =>
        GetStyleDistance(
            new FontStyleRequest(actual.Weight, actual.Width, actual.IsItalic ? FontSlant.Italic : FontSlant.Upright),
            requested);

    private static int GetStyleDistance(FontStyleRequest actual, FontStyleRequest requested)
    {
        bool actualItalic = actual.Slant != FontSlant.Upright;
        bool requestedItalic = requested.Slant != FontSlant.Upright;
        int slantDistance = actualItalic == requestedItalic ? 0 : 10_000;
        int widthDistance = Math.Abs(actual.Width - requested.Width) * 1_000;
        int weightDistance = Math.Abs(actual.Weight - requested.Weight);
        return slantDistance + widthDistance + weightDistance;
    }

    private static bool CanRenderGlyph(TtfFont font, ushort glyph, int codePoint)
    {
        if (font.GetGlyphOutline(glyph) is not null ||
            font.HasColorLayers(glyph) ||
            font.TryGetBitmapGlyph(glyph, 64f, out _))
        {
            return true;
        }

        string text = char.ConvertFromUtf32(codePoint);
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, 0);
        return category is UnicodeCategory.Control or UnicodeCategory.Format or
               UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or
               UnicodeCategory.ParagraphSeparator;
    }

    private static bool IsArabic(int codePoint) =>
        codePoint is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or
                     >= 0x0870 and <= 0x089F or >= 0x08A0 and <= 0x08FF or
                     >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF or
                     >= 0x1EE00 and <= 0x1EEFF;

    private static bool IsHebrew(int codePoint) =>
        codePoint is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F;
}
