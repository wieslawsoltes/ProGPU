using System;

namespace SkiaSharp;

public partial class SKTypeface
{
    public static SKTypeface CreateDefault() => Default;

    public static SKTypeface FromFamilyName(string? familyName) =>
        FromFamilyName(familyName, SKFontStyle.Normal);

    [Obsolete("Use SKFont directly instead.")]
    public int CountGlyphs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CountGlyphs(text.AsSpan());
    }

    [Obsolete("Use SKFont directly instead.")]
    public int CountGlyphs(ReadOnlySpan<char> text)
    {
        using var font = ToFont();
        return font.CountGlyphs(text);
    }

    [Obsolete("Use SKFont directly instead.")]
    public int CountGlyphs(byte[] text, SKTextEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CountGlyphs(text.AsSpan(), encoding);
    }

    [Obsolete("Use SKFont directly instead.")]
    public int CountGlyphs(ReadOnlySpan<byte> text, SKTextEncoding encoding)
    {
        using var font = ToFont();
        return font.CountGlyphs(text, encoding);
    }

    [Obsolete("Use SKFont directly instead.")]
    public int CountGlyphs(IntPtr text, int length, SKTextEncoding encoding)
    {
        using var font = ToFont();
        return font.CountGlyphs(
            text,
            checked(length * GetCharacterByteSize(encoding)),
            encoding);
    }

    [Obsolete("Use SKFont directly instead.")]
    public ushort GetGlyph(int codepoint)
    {
        using var font = ToFont();
        return font.GetGlyph(codepoint);
    }

    [Obsolete("Use SKFont directly instead.")]
    public ushort[] GetGlyphs(ReadOnlySpan<int> codepoints)
    {
        using var font = ToFont();
        return font.GetGlyphs(codepoints);
    }

    public ushort[] GetGlyphs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return GetGlyphs(text.AsSpan());
    }

    public ushort[] GetGlyphs(ReadOnlySpan<char> text)
    {
        using var font = ToFont();
        return font.GetGlyphs(text);
    }

    public ushort[] GetGlyphs(ReadOnlySpan<byte> text, SKTextEncoding encoding)
    {
        using var font = ToFont();
        return font.GetGlyphs(text, encoding);
    }

    public ushort[] GetGlyphs(IntPtr text, int length, SKTextEncoding encoding)
    {
        using var font = ToFont();
        return font.GetGlyphs(
            text,
            checked(length * GetCharacterByteSize(encoding)),
            encoding);
    }

    [Obsolete("Use SKFont directly instead.")]
    public bool ContainsGlyph(int codepoint) => GetGlyph(codepoint) != 0;

    [Obsolete("Use SKFont directly instead.")]
    public bool ContainsGlyphs(ReadOnlySpan<int> codepoints)
    {
        using var font = ToFont();
        return font.ContainsGlyphs(codepoints);
    }

    [Obsolete("Use SKFont directly instead.")]
    public bool ContainsGlyphs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ContainsGlyphs(text.AsSpan());
    }

    [Obsolete("Use SKFont directly instead.")]
    public bool ContainsGlyphs(ReadOnlySpan<char> text)
    {
        using var font = ToFont();
        return font.ContainsGlyphs(text);
    }

    [Obsolete("Use SKFont directly instead.")]
    public bool ContainsGlyphs(ReadOnlySpan<byte> text, SKTextEncoding encoding)
    {
        using var font = ToFont();
        return font.ContainsGlyphs(text, encoding);
    }

    [Obsolete("Use SKFont directly instead.")]
    public bool ContainsGlyphs(IntPtr text, int length, SKTextEncoding encoding)
    {
        using var font = ToFont();
        return font.ContainsGlyphs(
            text,
            checked(length * GetCharacterByteSize(encoding)),
            encoding);
    }

    public bool HasGetKerningPairAdjustments => false;

    public int[] GetKerningPairAdjustments(ReadOnlySpan<ushort> glyphs)
    {
        var adjustments = new int[glyphs.Length];
        GetKerningPairAdjustments(glyphs, adjustments);
        return adjustments;
    }

    public bool GetKerningPairAdjustments(
        ReadOnlySpan<ushort> glyphs,
        Span<int> adjustments)
    {
        var pairCount = Math.Max(0, glyphs.Length - 1);
        if (adjustments.Length < pairCount)
        {
            throw new ArgumentException(
                "Length of adjustments must be large enough to hold one adjustment per pair of glyphs (or, glyphs.Length - 1).");
        }

        adjustments[..pairCount].Clear();
        return false;
    }

    public uint[] GetTableTags()
    {
        if (!TryGetTableTags(out var tags))
        {
            throw new Exception("Unable to read the tables for the file.");
        }

        return tags;
    }

    public bool TryGetTableTags(out uint[] tags)
    {
        if (_isEmpty || Font.TableTags.Count == 0)
        {
            tags = Array.Empty<uint>();
            return false;
        }

        tags = new uint[Font.TableTags.Count];
        var index = 0;
        foreach (var tag in Font.TableTags)
        {
            tags[index++] = ToTag(tag);
        }

        return true;
    }

    public int GetTableSize(uint tag) =>
        TryGetTable(tag, out var table) ? table.Length : 0;

    public byte[] GetTableData(uint tag)
    {
        if (!TryGetTableData(tag, out var data))
        {
            throw new Exception("Unable to read the data table.");
        }

        return data;
    }

    public unsafe bool TryGetTableData(
        uint tag,
        int offset,
        int length,
        IntPtr tableData)
    {
        if (offset < 0 || length <= 0 || tableData == IntPtr.Zero ||
            !TryGetTable(tag, out var table) ||
            offset > table.Length || length > table.Length - offset)
        {
            return false;
        }

        table.Span.Slice(offset, length).CopyTo(new Span<byte>(tableData.ToPointer(), length));
        return true;
    }

    public SKFont ToFont() => new(this);

    public SKFont ToFont(float size, float scaleX = 1f, float skewX = 0f) =>
        new(this, size, scaleX, skewX);

    private bool TryGetTable(uint tag, out ReadOnlyMemory<byte> table)
    {
        if (_isEmpty)
        {
            table = default;
            return false;
        }

        return Font.TryGetTable(ToTagString(tag), out table);
    }

    private static int GetCharacterByteSize(SKTextEncoding encoding) => encoding switch
    {
        SKTextEncoding.Utf8 => 1,
        SKTextEncoding.Utf16 => 2,
        SKTextEncoding.Utf32 => 4,
        SKTextEncoding.GlyphId => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    private static uint ToTag(string tag)
    {
        if (tag.Length != 4)
        {
            return 0;
        }

        return ((uint)tag[0] << 24) |
               ((uint)tag[1] << 16) |
               ((uint)tag[2] << 8) |
               tag[3];
    }

    private static string ToTagString(uint tag) => string.Create(4, tag, static (characters, value) =>
    {
        characters[0] = (char)((value >> 24) & 0xff);
        characters[1] = (char)((value >> 16) & 0xff);
        characters[2] = (char)((value >> 8) & 0xff);
        characters[3] = (char)(value & 0xff);
    });
}
