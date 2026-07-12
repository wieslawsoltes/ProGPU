using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ProGPU.Text;

namespace SkiaSharp;

public enum SKFontStyleWeight
{
    Invisible = 0,
    Thin = 100,
    ExtraLight = 200,
    Light = 300,
    Normal = 400,
    Medium = 500,
    SemiBold = 600,
    Bold = 700,
    ExtraBold = 800,
    Black = 900,
    ExtraBlack = 1000,
}

public enum SKFontStyleWidth
{
    UltraCondensed = 1,
    ExtraCondensed = 2,
    Condensed = 3,
    SemiCondensed = 4,
    Normal = 5,
    SemiExpanded = 6,
    Expanded = 7,
    ExtraExpanded = 8,
    UltraExpanded = 9,
}

public class SKFontStyle : IDisposable
{
    public IntPtr Handle { get; } = SKObjectHandle.Create();
    public int Weight { get; }
    public int Width { get; }
    public SKFontStyleSlant Slant { get; }

    public SKFontStyle(SKFontStyleWeight weight, SKFontStyleWidth width, SKFontStyleSlant slant)
        : this((int)weight, (int)width, slant)
    {
    }

    public SKFontStyle(int weight, int width, SKFontStyleSlant slant)
    {
        Weight = weight;
        Width = width;
        Slant = slant;
    }

    public static readonly SKFontStyle Normal = new(SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
    public static readonly SKFontStyle Italic = new(SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Italic);
    public static readonly SKFontStyle Bold = new(SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
    public static readonly SKFontStyle BoldItalic = new(SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Italic);

    public void Dispose() { }
}

public partial class SKTypeface : IDisposable
{
    private static readonly ConcurrentDictionary<(string Path, int FaceIndex), Lazy<TtfFont>> s_systemFonts = new();
    private readonly int? _requestedWeight;
    private readonly int? _requestedWidth;
    private readonly SKFontStyleSlant? _requestedSlant;
    private readonly bool _isEmpty;

    public IntPtr Handle { get; } = SKObjectHandle.Create();
    public TtfFont Font { get; }
    public string FamilyName { get; }
    public bool IsBold { get; }
    public bool IsItalic { get; }
    public int FontWeight => _requestedWeight ??
        (Font.WeightClass == 0 ? (int)SKFontStyleWeight.Normal : Font.WeightClass);
    public int FontWidth => _requestedWidth ??
        (Font.WidthClass == 0 ? (int)SKFontStyleWidth.Normal : Font.WidthClass);
    public SKFontStyleSlant FontSlant => _requestedSlant ??
        (Font.IsItalic || IsItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
    public int UnitsPerEm => _isEmpty ? 0 : Font.UnitsPerEm;
    public int GlyphCount => _isEmpty ? 0 : Font.NumGlyphs;
    public bool IsEmpty => _isEmpty;
    public bool IsFixedPitch => !_isEmpty && Font.IsFixedPitch;
    public string PostScriptName => _isEmpty ? string.Empty : Font.PostScriptName;
    public int TableCount => _isEmpty ? 0 : Font.TableTags.Count;
    public SKFontStyle FontStyle => new(
        FontWeight,
        Math.Clamp(FontWidth, (int)SKFontStyleWidth.UltraCondensed, (int)SKFontStyleWidth.UltraExpanded),
        FontSlant);

    public SKTypeface(
        TtfFont font,
        string familyName,
        bool isBold = false,
        bool isItalic = false,
        int? requestedWeight = null,
        int? requestedWidth = null,
        SKFontStyleSlant? requestedSlant = null,
        bool isEmpty = false)
    {
        Font = font;
        FamilyName = familyName;
        IsBold = isBold;
        IsItalic = isItalic;
        _requestedWeight = requestedWeight;
        _requestedWidth = requestedWidth;
        _requestedSlant = requestedSlant;
        _isEmpty = isEmpty;
    }

    private static SKTypeface? _empty;
    public static SKTypeface Empty => _empty ??= new SKTypeface(
        Default.Font,
        string.Empty,
        isEmpty: true);

    private static SKTypeface? _default;
    public static SKTypeface Default
    {
        get
        {
            if (_default == null)
            {
                var systemFonts = FontApi.GetSystemFonts();
                var selectedFont = FindPreferredFont(
                    systemFonts,
                    GetGenericFamilyPreferences(GenericFontFamily.SansSerif),
                    SKFontStyle.Normal);
                if (selectedFont == null && systemFonts.Count > 0)
                {
                    selectedFont = systemFonts[0];
                }
                if (selectedFont != null && !string.IsNullOrEmpty(selectedFont.FilePath))
                {
                    _default = new SKTypeface(CreateFont(selectedFont), selectedFont.FamilyName);
                }
                else
                {
                    throw new InvalidOperationException("No system fonts found to initialize default typeface.");
                }
            }
            return _default;
        }
    }

    public static SKTypeface? FromStream(Stream stream, int index = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        try
        {
            var font = new TtfFont(ms.ToArray(), index);
            return new SKTypeface(font, font.FamilyName);
        }
        catch (Exception ex) when (IsInvalidFontException(ex))
        {
            return null;
        }
    }

    public static SKTypeface? FromStream(SKStreamAsset stream, int index = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return FromStream(new MemoryStream(stream.Data, writable: false), index);
    }

    public static SKTypeface? FromFile(string path, int index = 0)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            var font = new TtfFont(path, index);
            return new SKTypeface(font, font.FamilyName);
        }
        catch (Exception ex) when (ex is FileNotFoundException || IsInvalidFontException(ex))
        {
            return null;
        }
    }

    private static bool IsInvalidFontException(Exception exception) =>
        exception is FormatException or ArgumentException or OverflowException or IndexOutOfRangeException;

    public static SKTypeface FromFamilyName(string? familyName, SKFontStyle style)
    {
        var matchedTypeface = MatchFamilyName(familyName, style);
        if (matchedTypeface != null)
        {
            return matchedTypeface;
        }

        var defaultTypeface = Default;
        if (OperatingSystem.IsMacOS())
        {
            return new SKTypeface(
                defaultTypeface.Font,
                defaultTypeface.FamilyName,
                defaultTypeface.IsBold,
                defaultTypeface.IsItalic);
        }

        return new SKTypeface(
            defaultTypeface.Font,
            defaultTypeface.FamilyName,
            style.Weight >= (int)SKFontStyleWeight.SemiBold,
            style.Slant != SKFontStyleSlant.Upright,
            style.Weight,
            style.Width,
            style.Slant);
    }

    internal static SKTypeface? MatchFamilyName(string? familyName, SKFontStyle style)
    {
        if (string.IsNullOrWhiteSpace(familyName))
        {
            return null;
        }

        var systemFonts = FontApi.GetSystemFonts();
        if (OperatingSystem.IsLinux() &&
            TryGetGenericFontFamily(familyName, out var genericFamily))
        {
            var genericFont = FindPreferredFont(
                systemFonts,
                GetGenericFamilyPreferences(genericFamily),
                style);
            if (genericFont != null)
            {
                try
                {
                    return CreateSystemTypeface(genericFont, style);
                }
                catch
                {
                    return null;
                }
            }
        }

        var familyFont = FindBestMatchingFont(systemFonts, familyName, style);
        if (familyFont == null)
        {
            return null;
        }

        try
        {
            return CreateSystemTypeface(familyFont, style);
        }
        catch
        {
            return null;
        }
    }

    private enum GenericFontFamily
    {
        SansSerif,
        Serif,
        Monospace
    }

    private static bool TryGetGenericFontFamily(string? familyName, out GenericFontFamily family)
    {
        switch (familyName?.Trim().ToLowerInvariant())
        {
            case "sans-serif":
            case "system-ui":
            case "ui-sans-serif":
                family = GenericFontFamily.SansSerif;
                return true;
            case "serif":
            case "ui-serif":
                family = GenericFontFamily.Serif;
                return true;
            case "monospace":
            case "ui-monospace":
                family = GenericFontFamily.Monospace;
                return true;
            default:
                family = default;
                return false;
        }
    }

    private static string[] GetGenericFamilyPreferences(GenericFontFamily family)
    {
        if (OperatingSystem.IsMacOS())
        {
            return family switch
            {
                GenericFontFamily.Serif => new[] { "Times", "Times New Roman" },
                GenericFontFamily.Monospace => new[] { "Menlo", "Monaco", "Courier" },
                _ => new[] { "Helvetica", "Arial" }
            };
        }

        if (OperatingSystem.IsWindows())
        {
            return family switch
            {
                GenericFontFamily.Serif => new[] { "Times New Roman", "Georgia" },
                GenericFontFamily.Monospace => new[] { "Consolas", "Courier New" },
                _ => new[] { "Segoe UI", "Arial" }
            };
        }

        return family switch
        {
            GenericFontFamily.Serif => new[] { "Noto Serif", "DejaVu Serif", "Liberation Serif" },
            GenericFontFamily.Monospace => new[] { "Noto Sans Mono", "DejaVu Sans Mono", "Liberation Mono" },
            _ => new[] { "Noto Sans", "DejaVu Sans", "Liberation Sans", "Arial" }
        };
    }

    private static FontInfo? FindPreferredFont(
        IReadOnlyList<FontInfo> fonts,
        IReadOnlyList<string> preferences,
        SKFontStyle style)
    {
        for (var preferenceIndex = 0; preferenceIndex < preferences.Count; preferenceIndex++)
        {
            var font = FindBestMatchingFont(fonts, preferences[preferenceIndex], style);
            if (font != null)
            {
                return font;
            }
        }

        return null;
    }

    private static FontInfo? FindBestMatchingFont(
        IReadOnlyList<FontInfo> fonts,
        string familyOrFullName,
        SKFontStyle style)
    {
        FontInfo? best = null;
        var bestDistance = int.MaxValue;
        for (var fontIndex = 0; fontIndex < fonts.Count; fontIndex++)
        {
            var font = fonts[fontIndex];
            if (!font.FamilyName.Equals(familyOrFullName, StringComparison.OrdinalIgnoreCase) &&
                !font.Name.Equals(familyOrFullName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var distance = GetIntrinsicStyleDistance(CreateFont(font), style);
                if (distance < bestDistance)
                {
                    best = font;
                    bestDistance = distance;
                    if (distance == 0)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Skip unreadable font faces.
            }
        }

        return best;
    }

    private static int GetIntrinsicStyleDistance(TtfFont font, SKFontStyle style)
    {
        var actualWeight = font.WeightClass == 0
            ? (int)SKFontStyleWeight.Normal
            : font.WeightClass;
        var actualWidth = font.WidthClass == 0
            ? (int)SKFontStyleWidth.Normal
            : font.WidthClass;
        var wantsItalic = style.Slant != SKFontStyleSlant.Upright;
        var slantDistance = wantsItalic == font.IsItalic ? 0 : 10_000;
        var widthDistance = Math.Abs(actualWidth - style.Width) * 1_000;
        var weightDistance = Math.Abs(actualWeight - style.Weight);
        return slantDistance + widthDistance + weightDistance;
    }

    private static SKTypeface CreateSystemTypeface(FontInfo font, SKFontStyle style)
    {
        return new SKTypeface(
            CreateFont(font),
            font.FamilyName,
            style.Weight >= (int)SKFontStyleWeight.SemiBold,
            style.Slant != SKFontStyleSlant.Upright,
            style.Weight,
            style.Width,
            style.Slant);
    }

    public static SKTypeface FromFamilyName(
        string? familyName,
        SKFontStyleWeight weight,
        SKFontStyleWidth width,
        SKFontStyleSlant slant) =>
        FromFamilyName(familyName, (int)weight, (int)width, slant);

    public static SKTypeface FromFamilyName(
        string? familyName,
        int weight,
        int width,
        SKFontStyleSlant slant)
    {
        if (!string.IsNullOrWhiteSpace(familyName))
        {
            return FromFamilyName(familyName, new SKFontStyle(weight, width, slant));
        }

        var fallback = Default;
        return new SKTypeface(
            fallback.Font,
            fallback.FamilyName,
            weight >= (int)SKFontStyleWeight.SemiBold,
            slant != SKFontStyleSlant.Upright,
            weight,
            width,
            slant);
    }

    public static SKTypeface? FromData(SKData data, int index = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        return FromStream(new MemoryStream(data.Bytes, writable: false), index);
    }

    public SKFont CreateSKFont(float size)
    {
        return new SKFont(this, size);
    }

    public bool TryGetTableData(uint tag, out byte[] data)
    {
        if (_isEmpty)
        {
            data = Array.Empty<byte>();
            return false;
        }

        Span<char> characters = stackalloc char[4]
        {
            (char)((tag >> 24) & 0xff),
            (char)((tag >> 16) & 0xff),
            (char)((tag >> 8) & 0xff),
            (char)(tag & 0xff)
        };

        if (Font.TryGetTable(new string(characters), out var table))
        {
            data = table.ToArray();
            return true;
        }

        data = Array.Empty<byte>();
        return false;
    }

    public SKStreamAsset? OpenStream()
    {
        return _isEmpty ? null : new SKStreamAsset(Font.FontData.ToArray());
    }

    public SKStreamAsset? OpenStream(out int ttcIndex)
    {
        ttcIndex = _isEmpty ? 0 : Font.FaceIndex;
        return OpenStream();
    }

    public void Dispose() { }

    internal static TtfFont CreateFont(FontInfo font)
    {
        var key = (font.FilePath, font.FaceIndex);
        var lazy = s_systemFonts.GetOrAdd(
            key,
            static value => new Lazy<TtfFont>(
                () => new TtfFont(value.Path, value.FaceIndex),
                isThreadSafe: true));
        try
        {
            return lazy.Value;
        }
        catch
        {
            s_systemFonts.TryRemove(key, out _);
            throw;
        }
    }
}

public sealed class SKStreamAsset : SKStream
{
    private readonly byte[] _data;
    private readonly MemoryStream _stream;
    private System.Runtime.InteropServices.GCHandle _pin;

    internal SKStreamAsset(byte[] data)
    {
        _data = data;
        _stream = new MemoryStream(_data, writable: false);
    }

    internal byte[] Data => _data;

    public int Length => checked((int)_stream.Length);

    public int Read(byte[] buffer, int size)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return _stream.Read(buffer, 0, Math.Min(size, buffer.Length));
    }

    public int Read(byte[] buffer, long size)
    {
        return Read(buffer, (int)Math.Min(size, int.MaxValue));
    }

    public int Read(IntPtr buffer, int size)
    {
        if (buffer == IntPtr.Zero || size <= 0)
        {
            return 0;
        }

        var actualSize = Math.Min(size, Length - checked((int)_stream.Position));
        if (actualSize <= 0)
        {
            return 0;
        }

        var temporary = GC.AllocateUninitializedArray<byte>(actualSize);
        var read = _stream.Read(temporary, 0, actualSize);
        System.Runtime.InteropServices.Marshal.Copy(temporary, 0, buffer, read);
        return read;
    }

    public IntPtr GetMemoryBase()
    {
        if (_data.Length == 0)
        {
            return IntPtr.Zero;
        }

        if (!_pin.IsAllocated)
        {
            _pin = System.Runtime.InteropServices.GCHandle.Alloc(
                _data,
                System.Runtime.InteropServices.GCHandleType.Pinned);
        }

        return _pin.AddrOfPinnedObject();
    }

    public override void Dispose()
    {
        if (_pin.IsAllocated)
        {
            _pin.Free();
        }

        _stream.Dispose();
    }
}

public class SKFontManager : IDisposable
{
    public IntPtr Handle { get; } = SKObjectHandle.Create();
    private static readonly SKFontManager _defaultInstance = new();
    public static SKFontManager Default => _defaultInstance;

    public static SKFontManager CreateDefault() => new();

    public string[] GetFontFamilies()
    {
        var list = FontApi.GetSystemFonts();
        var names = new List<string>();
        foreach (var f in list)
        {
            if (!names.Contains(f.FamilyName))
            {
                names.Add(f.FamilyName);
            }
        }
        return names.ToArray();
    }

    public SKTypeface? MatchFamily(string familyName, SKFontStyle style)
    {
        return SKTypeface.MatchFamilyName(familyName, style);
    }

    public SKTypeface? CreateTypeface(string path, int index = 0) => SKTypeface.FromFile(path, index);

    public SKTypeface? CreateTypeface(Stream stream, int index = 0) => SKTypeface.FromStream(stream, index);

    public SKTypeface? CreateTypeface(SKStreamAsset stream, int index = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return SKTypeface.FromStream(new MemoryStream(stream.Data, writable: false), index);
    }

    public SKTypeface? CreateTypeface(SKData data, int index = 0) => SKTypeface.FromData(data, index);

    public SKTypeface? MatchCharacter(string? familyName, SKFontStyle style, string[] bcp47, int codepoint)
    {
        if (codepoint < 0 || codepoint > 0x10FFFF)
        {
            return null;
        }

        var systemFonts = FontApi.GetSystemFonts();
        var visited = new HashSet<(string Path, int FaceIndex)>();
        if (!string.IsNullOrWhiteSpace(familyName) &&
            TryMatchFamily(systemFonts, familyName, style, codepoint, visited, out var requestedTypeface))
        {
            return requestedTypeface;
        }

        IReadOnlyList<string> fallbackFamilies = GetFallbackFamilyPreferences(bcp47, codepoint);
        for (int i = 0; i < fallbackFamilies.Count; i++)
        {
            if (TryMatchFamily(systemFonts, fallbackFamilies[i], style, codepoint, visited, out var localeTypeface))
            {
                return localeTypeface;
            }
        }

        for (int stylePass = 0; stylePass < 2; stylePass++)
        {
            bool requireStyleMatch = stylePass == 0;
            foreach (FontInfo font in systemFonts)
            {
                if ((requireStyleMatch && !MatchesRequestedStyle(font, style)) ||
                    !visited.Add((font.FilePath, font.FaceIndex)))
                {
                    continue;
                }

                if (TryCreateRenderableTypeface(font, style, codepoint, out var typeface))
                {
                    return typeface;
                }
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> GetFallbackFamilyPreferences(
        IReadOnlyList<string>? bcp47,
        int codepoint)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(params string[] families)
        {
            foreach (string family in families)
            {
                if (seen.Add(family))
                {
                    result.Add(family);
                }
            }
        }

        void AddLanguage(string language)
        {
            string normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
            if (normalized == "ar" || normalized.StartsWith("ar-", StringComparison.Ordinal))
            {
                Add(
                    "Geeza Pro",
                    "Noto Naskh Arabic",
                    "Noto Sans Arabic",
                    "Segoe UI",
                    "Traditional Arabic",
                    "Arabic Typesetting",
                    "Tahoma",
                    "Arial",
                    "DejaVu Sans");
                return;
            }

            if (normalized == "ja" || normalized.StartsWith("ja-", StringComparison.Ordinal))
            {
                Add(
                    "Hiragino Sans",
                    "Hiragino Kaku Gothic ProN",
                    "Yu Gothic",
                    "Meiryo",
                    "Noto Sans CJK JP",
                    "Noto Sans JP",
                    ".Aqua Kana");
                return;
            }

            if (normalized == "ko" || normalized.StartsWith("ko-", StringComparison.Ordinal))
            {
                Add(
                    "Apple SD Gothic Neo",
                    "AppleGothic",
                    "Malgun Gothic",
                    "Noto Sans CJK KR",
                    "Noto Sans KR");
                return;
            }

            if (normalized == "zh" || normalized.StartsWith("zh-", StringComparison.Ordinal))
            {
                bool traditional = normalized.Contains("-hant", StringComparison.Ordinal) ||
                                   normalized.EndsWith("-tw", StringComparison.Ordinal) ||
                                   normalized.EndsWith("-hk", StringComparison.Ordinal) ||
                                   normalized.EndsWith("-mo", StringComparison.Ordinal);
                if (traditional)
                {
                    Add(
                        "PingFang TC",
                        "Heiti TC",
                        "Noto Sans CJK TC",
                        "Noto Sans TC",
                        "Microsoft JhengHei",
                        "Songti TC");
                }
                else
                {
                    Add(
                        "PingFang SC",
                        "Hiragino Sans GB",
                        "Heiti SC",
                        "Noto Sans CJK SC",
                        "Noto Sans SC",
                        "Microsoft YaHei",
                        "SimHei",
                        "Songti SC");
                }
            }
        }

        if (bcp47 is not null)
        {
            for (int i = 0; i < bcp47.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(bcp47[i]))
                {
                    AddLanguage(bcp47[i]);
                }
            }
        }

        if (IsArabicCodepoint(codepoint))
        {
            AddLanguage("ar");
        }
        else if (IsHebrewCodepoint(codepoint))
        {
            Add(
                "Arial Hebrew",
                "Lucida Grande",
                "Noto Sans Hebrew",
                "Segoe UI",
                "Arial",
                "DejaVu Sans");
        }
        else if (codepoint is >= 0x3040 and <= 0x30FF)
        {
            AddLanguage("ja");
        }
        else if (codepoint is >= 0xAC00 and <= 0xD7AF ||
                 codepoint is >= 0x1100 and <= 0x11FF)
        {
            AddLanguage("ko");
        }
        else if (codepoint is >= 0x3400 and <= 0x9FFF ||
                 codepoint is >= 0xF900 and <= 0xFAFF ||
                 codepoint is >= 0x20000 and <= 0x323AF)
        {
            AddLanguage("zh-Hans");
        }
        else if (IsLatinGreekOrCyrillicCodepoint(codepoint))
        {
            Add(
                "Helvetica",
                "Arial",
                "Segoe UI",
                "Noto Sans",
                "DejaVu Sans",
                "Liberation Sans");
        }

        return result;
    }

    private static bool IsArabicCodepoint(int codepoint) =>
        codepoint is >= 0x0600 and <= 0x06FF or
                     >= 0x0750 and <= 0x077F or
                     >= 0x0870 and <= 0x089F or
                     >= 0x08A0 and <= 0x08FF or
                     >= 0xFB50 and <= 0xFDFF or
                     >= 0xFE70 and <= 0xFEFF or
                     >= 0x1EE00 and <= 0x1EEFF;

    private static bool IsHebrewCodepoint(int codepoint) =>
        codepoint is >= 0x0590 and <= 0x05FF or
                     >= 0xFB1D and <= 0xFB4F;

    private static bool IsLatinGreekOrCyrillicCodepoint(int codepoint) =>
        codepoint is >= 0x0020 and <= 0x024F or
                     >= 0x0370 and <= 0x052F or
                     >= 0x1E00 and <= 0x1FFF or
                     >= 0x2DE0 and <= 0x2DFF or
                     >= 0xA640 and <= 0xA69F;

    private static bool TryMatchFamily(
        IReadOnlyList<FontInfo> fonts,
        string familyName,
        SKFontStyle style,
        int codepoint,
        HashSet<(string Path, int FaceIndex)> visited,
        out SKTypeface? typeface)
    {
        SKTypeface? bestTypeface = null;
        int bestStyleDistance = int.MaxValue;
        for (int i = 0; i < fonts.Count; i++)
        {
            FontInfo font = fonts[i];
            if (!MatchesFamily(font, familyName) ||
                !visited.Add((font.FilePath, font.FaceIndex)) ||
                !TryCreateRenderableTypeface(font, style, codepoint, out var candidate) ||
                candidate is null)
            {
                continue;
            }

            int styleDistance = GetStyleDistance(candidate.Font, style);
            if (styleDistance < bestStyleDistance)
            {
                bestTypeface = candidate;
                bestStyleDistance = styleDistance;
                if (styleDistance == 0)
                {
                    break;
                }
            }
        }

        typeface = bestTypeface;
        return typeface is not null;
    }

    private static int GetStyleDistance(TtfFont font, SKFontStyle style)
    {
        int actualWeight = font.WeightClass == 0
            ? (int)SKFontStyleWeight.Normal
            : font.WeightClass;
        int actualWidth = font.WidthClass == 0
            ? (int)SKFontStyleWidth.Normal
            : font.WidthClass;
        bool wantsItalic = style.Slant != SKFontStyleSlant.Upright;
        int slantDistance = wantsItalic == font.IsItalic ? 0 : 10_000;
        int widthDistance = Math.Abs(actualWidth - style.Width) * 1_000;
        int weightDistance = Math.Abs(actualWeight - style.Weight);
        return slantDistance + widthDistance + weightDistance;
    }

    private static bool MatchesFamily(FontInfo font, string familyName)
    {
        return font.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase) ||
               font.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRequestedStyle(FontInfo font, SKFontStyle style)
    {
        bool wantsBold = style.Weight >= (int)SKFontStyleWeight.SemiBold;
        bool wantsItalic = style.Slant != SKFontStyleSlant.Upright;
        bool isBold = HasStyleToken(font.Name, "bold") ||
                      HasStyleToken(font.Name, "semibold") ||
                      HasStyleToken(font.Name, "demibold") ||
                      HasStyleToken(font.Name, "black") ||
                      HasStyleToken(font.Name, "heavy");
        bool isItalic = HasStyleToken(font.Name, "italic") || HasStyleToken(font.Name, "oblique");
        return wantsBold == isBold && wantsItalic == isItalic;
    }

    private static bool HasStyleToken(string value, string token) =>
        value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool TryCreateRenderableTypeface(
        FontInfo font,
        SKFontStyle style,
        int codepoint,
        out SKTypeface? typeface)
    {
        typeface = null;
        try
        {
            if (!FontApi.ContainsGlyph(font, (uint)codepoint))
            {
                return false;
            }

            TtfFont ttf = SKTypeface.CreateFont(font);
            ushort glyph = ttf.GetGlyphIndex((uint)codepoint);
            if (glyph == 0 || !CanRenderGlyph(ttf, glyph, codepoint))
            {
                return false;
            }

            typeface = new SKTypeface(
                ttf,
                font.FamilyName,
                style.Weight >= (int)SKFontStyleWeight.SemiBold,
                style.Slant != SKFontStyleSlant.Upright,
                style.Weight,
                style.Width,
                style.Slant);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanRenderGlyph(TtfFont font, ushort glyph, int codepoint)
    {
        if (font.GetGlyphOutline(glyph) is not null ||
            font.HasColorLayers(glyph) ||
            font.TryGetBitmapGlyph(glyph, 64f, out _))
        {
            return true;
        }

        string text = char.ConvertFromUtf32(codepoint);
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, 0);
        return category is UnicodeCategory.Control or
               UnicodeCategory.Format or
               UnicodeCategory.SpaceSeparator or
               UnicodeCategory.LineSeparator or
               UnicodeCategory.ParagraphSeparator;
    }

    public SKTypeface? MatchCharacter(int codepoint) =>
        MatchCharacter(null, SKFontStyle.Normal, Array.Empty<string>(), codepoint);

    public SKTypeface? MatchCharacter(
        string? familyName,
        SKFontStyleWeight weight,
        SKFontStyleWidth width,
        SKFontStyleSlant slant,
        string[]? bcp47,
        int codepoint) =>
        MatchCharacter(familyName, new SKFontStyle(weight, width, slant), bcp47 ?? Array.Empty<string>(), codepoint);

    public List<SKFontStyle> GetFontStyles(string familyName)
    {
        var styles = new List<SKFontStyle>();
        var systemFonts = FontApi.GetSystemFonts();
        foreach (var font in systemFonts)
        {
            if (font.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase))
            {
                // In this basic outline we fallback to normal style or try to parse bold/italic from name
                var slant = SKFontStyleSlant.Upright;
                var weight = SKFontStyleWeight.Normal;
                
                if (font.Name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Name.Contains("Oblique", StringComparison.OrdinalIgnoreCase))
                    slant = SKFontStyleSlant.Italic;
                if (font.Name.Contains("Bold", StringComparison.OrdinalIgnoreCase))
                    weight = SKFontStyleWeight.Bold;
                
                styles.Add(new SKFontStyle(weight, SKFontStyleWidth.Normal, slant));
            }
        }
        if (styles.Count == 0)
        {
            styles.Add(SKFontStyle.Normal);
        }
        return styles;
    }

    public void Dispose() { }

}
