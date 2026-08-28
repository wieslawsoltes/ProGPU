using System.Globalization;
using System.Text;

namespace ProGPU.CAD;

[Flags]
public enum CadMTextDecoration : byte
{
    None = 0,
    Overline = 1 << 0,
    Underline = 1 << 1,
    StrikeThrough = 1 << 2,
}

public enum CadMTextInlineKind : byte
{
    Text = 0,
    ParagraphBreak = 1,
    ColumnBreak = 2,
    Stack = 3,
}

public enum CadMTextStackKind : byte
{
    Horizontal = 0,
    Diagonal = 1,
    Tolerance = 2,
}

public enum CadMTextVerticalAlignment : byte
{
    Bottom = 0,
    Center = 1,
    Top = 2,
}

public enum CadMTextParagraphAlignment : byte
{
    Left = 0,
    Center = 1,
    Right = 2,
    Justify = 3,
    Distributed = 4,
}

public enum CadMTextColorKind : byte
{
    Inherit = 0,
    ByBlock = 1,
    ByLayer = 2,
    Indexed = 3,
    TrueColor = 4,
}

public readonly record struct CadMTextColor(CadMTextColorKind Kind, uint Value)
{
    public static CadMTextColor Inherit => default;
}

public readonly record struct CadMTextHeight(double Value, bool IsRelative)
{
    public static CadMTextHeight Default => new(1.0, true);
}

public readonly record struct CadMTextFontOverride(
    string FamilyName,
    bool IsBold,
    bool IsItalic,
    int CharacterSet,
    int PitchAndFamily)
{
    public bool IsSpecified => !string.IsNullOrEmpty(FamilyName);
}

/// <summary>
/// Immutable paragraph state retained from an MTEXT <c>\p</c> control.
/// The raw payload is kept so no unimplemented indentation or tab contract is
/// silently discarded while the commonly authored paragraph alignment remains typed.
/// </summary>
public readonly record struct CadMTextParagraphFormat(
    CadMTextParagraphAlignment Alignment,
    string RawPayload)
{
    public static CadMTextParagraphFormat Default =>
        new(CadMTextParagraphAlignment.Left, string.Empty);
}

/// <summary>One fully resolved formatting state in an MTEXT content stream.</summary>
public readonly record struct CadMTextRunStyle(
    CadMTextFontOverride Font,
    CadMTextHeight Height,
    double WidthFactor,
    double TrackingFactor,
    double ObliqueDegrees,
    CadMTextVerticalAlignment VerticalAlignment,
    CadMTextColor Color,
    CadMTextDecoration Decorations,
    CadMTextParagraphFormat Paragraph)
{
    public static CadMTextRunStyle Default => new(
        default,
        CadMTextHeight.Default,
        1.0,
        1.0,
        0.0,
        CadMTextVerticalAlignment.Bottom,
        CadMTextColor.Inherit,
        CadMTextDecoration.None,
        CadMTextParagraphFormat.Default);
}

/// <summary>
/// One semantic MTEXT inline. Text uses <see cref="Text"/>; a stack uses
/// <see cref="Text"/> for its upper value and <see cref="SecondaryText"/> for
/// its lower value. Break tokens carry neither string.
/// </summary>
public readonly record struct CadMTextInline(
    CadMTextInlineKind Kind,
    string Text,
    string SecondaryText,
    CadMTextStackKind StackKind,
    CadMTextRunStyle Style,
    int SourceOffset);

public sealed class CadMTextContent
{
    private readonly CadMTextInline[] _inlines;

    public ReadOnlyMemory<CadMTextInline> Inlines => _inlines;
    public int DecodedCodeUnitCount { get; }

    internal CadMTextContent(CadMTextInline[] inlines, int decodedCodeUnitCount)
    {
        _inlines = inlines;
        DecodedCodeUnitCount = decodedCodeUnitCount;
    }
}

public sealed class CadMTextParseOptions
{
    public const int DefaultMaxNestingDepth = 8;
    public const int DefaultMaxInlineCount = 131_072;
    public const int DefaultMaxDecodedCodeUnits = 65_536;

    public int MaxNestingDepth { get; init; } = DefaultMaxNestingDepth;
    public int MaxInlineCount { get; init; } = DefaultMaxInlineCount;
    public int MaxDecodedCodeUnits { get; init; } = DefaultMaxDecodedCodeUnits;
}

public sealed class CadMTextParseException : FormatException
{
    public int SourceOffset { get; }

    public CadMTextParseException(string message, int sourceOffset)
        : base($"{message} (source offset {sourceOffset}).")
    {
        SourceOffset = sourceOffset;
    }
}

/// <summary>
/// Clean-room, bounded parser for the persisted AutoCAD MTEXT content language.
/// </summary>
/// <remarks>
/// Parsing is O(C + R) time and O(D + R) storage for C source code units, D
/// decoded code units, and R semantic inlines. Group nesting, output text, and
/// inline counts are caller bounded. The parser performs no font lookup, shaping,
/// rendering, or mutable ACadSharp graph access.
/// </remarks>
public static class CadMTextParser
{
    public static CadMTextContent Parse(
        string source,
        CadMTextParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new CadMTextParseOptions();
        ValidateOptions(options);

        var inlines = new List<CadMTextInline>(Math.Min(16, options.MaxInlineCount));
        var text = new StringBuilder(Math.Min(source.Length, options.MaxDecodedCodeUnits));
        var states = new CadMTextRunStyle[options.MaxNestingDepth];
        CadMTextRunStyle style = CadMTextRunStyle.Default;
        int depth = 0;
        int textSourceOffset = 0;
        int decodedCodeUnits = 0;

        for (int index = 0; index < source.Length;)
        {
            char value = source[index];
            if (value == '{')
            {
                FlushText(index);
                if (depth >= states.Length)
                {
                    throw Error("MTEXT group nesting exceeds the configured limit", index);
                }

                states[depth++] = style;
                index++;
                continue;
            }

            if (value == '}')
            {
                FlushText(index);
                if (depth == 0)
                {
                    throw Error("MTEXT contains an unmatched closing group", index);
                }

                style = states[--depth];
                index++;
                continue;
            }

            if (value == '\\')
            {
                ParseEscape(ref index);
                continue;
            }

            if (value == '%' && index + 1 < source.Length && source[index + 1] == '<')
            {
                throw new NotSupportedException(
                    $"MTEXT fields require typed field evaluation before snapshot compilation (source offset {index}).");
            }

            if (value == '%' && index + 1 < source.Length && source[index + 1] == '%')
            {
                AppendPercentControl(ref index);
                continue;
            }

            if (value == '^' && index + 1 < source.Length && source[index + 1] == 'J')
            {
                FlushText(index);
                AddInline(new CadMTextInline(
                    CadMTextInlineKind.ParagraphBreak,
                    string.Empty,
                    string.Empty,
                    default,
                    style,
                    index));
                index += 2;
                continue;
            }

            if (value is '\r' or '\n')
            {
                FlushText(index);
                AddInline(new CadMTextInline(
                    CadMTextInlineKind.ParagraphBreak,
                    string.Empty,
                    string.Empty,
                    default,
                    style,
                    index));
                if (value == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
                {
                    index++;
                }
                index++;
                continue;
            }

            Append(value, index);
            index++;
        }

        FlushText(source.Length);
        if (depth != 0)
        {
            throw Error("MTEXT contains an unterminated formatting group", source.Length);
        }

        return new CadMTextContent(inlines.ToArray(), decodedCodeUnits);

        void ParseEscape(ref int index)
        {
            int escapeOffset = index;
            if (++index >= source.Length)
            {
                throw Error("MTEXT ends with a truncated escape", escapeOffset);
            }

            char code = source[index++];
            switch (code)
            {
                case '\\':
                case '{':
                case '}':
                    Append(code, escapeOffset);
                    return;
                case '~':
                    Append('\u00A0', escapeOffset);
                    return;
                case 'P':
                case 'n':
                    FlushText(escapeOffset);
                    AddInline(new CadMTextInline(
                        CadMTextInlineKind.ParagraphBreak,
                        string.Empty,
                        string.Empty,
                        default,
                        style,
                        escapeOffset));
                    return;
                case 'N':
                    FlushText(escapeOffset);
                    AddInline(new CadMTextInline(
                        CadMTextInlineKind.ColumnBreak,
                        string.Empty,
                        string.Empty,
                        default,
                        style,
                        escapeOffset));
                    return;
                case 'L':
                    ChangeStyle(style with { Decorations = style.Decorations | CadMTextDecoration.Underline }, escapeOffset);
                    return;
                case 'l':
                    ChangeStyle(style with { Decorations = style.Decorations & ~CadMTextDecoration.Underline }, escapeOffset);
                    return;
                case 'O':
                    ChangeStyle(style with { Decorations = style.Decorations | CadMTextDecoration.Overline }, escapeOffset);
                    return;
                case 'o':
                    ChangeStyle(style with { Decorations = style.Decorations & ~CadMTextDecoration.Overline }, escapeOffset);
                    return;
                case 'K':
                    ChangeStyle(style with { Decorations = style.Decorations | CadMTextDecoration.StrikeThrough }, escapeOffset);
                    return;
                case 'k':
                    ChangeStyle(style with { Decorations = style.Decorations & ~CadMTextDecoration.StrikeThrough }, escapeOffset);
                    return;
                case 'U':
                case 'u':
                    ParseUnicodeEscape(ref index, escapeOffset);
                    return;
                case 'A':
                case 'a':
                {
                    ReadPayload(ref index, escapeOffset, out ReadOnlySpan<char> payload);
                    if (payload.Length != 1 || payload[0] is < '0' or > '2')
                    {
                        throw Error("MTEXT vertical alignment must be 0, 1, or 2", escapeOffset);
                    }

                    ChangeStyle(style with
                    {
                        VerticalAlignment = (CadMTextVerticalAlignment)(payload[0] - '0'),
                    }, escapeOffset);
                    return;
                }
                case 'C':
                {
                    ReadPayload(ref index, escapeOffset, out ReadOnlySpan<char> payload);
                    int color = ParseInteger(payload, escapeOffset, "indexed color");
                    if (color is < 0 or > 256)
                    {
                        throw Error("MTEXT indexed color must be between 0 and 256", escapeOffset);
                    }

                    CadMTextColor converted = color switch
                    {
                        0 => new CadMTextColor(CadMTextColorKind.ByBlock, 0),
                        256 => new CadMTextColor(CadMTextColorKind.ByLayer, 256),
                        _ => new CadMTextColor(CadMTextColorKind.Indexed, checked((uint)color)),
                    };
                    ChangeStyle(style with { Color = converted }, escapeOffset);
                    return;
                }
                case 'c':
                {
                    ReadPayload(ref index, escapeOffset, out ReadOnlySpan<char> payload);
                    uint color = ParseUnsignedInteger(payload, escapeOffset, "true color");
                    if (color > 0x00FF_FFFFu)
                    {
                        throw Error("MTEXT true color exceeds 24 bits", escapeOffset);
                    }

                    ChangeStyle(style with
                    {
                        Color = new CadMTextColor(CadMTextColorKind.TrueColor, color),
                    }, escapeOffset);
                    return;
                }
                case 'F':
                case 'f':
                {
                    ReadPayload(ref index, escapeOffset, out ReadOnlySpan<char> payload);
                    ChangeStyle(style with { Font = ParseFont(payload, style.Font, escapeOffset) }, escapeOffset);
                    return;
                }
                case 'H':
                case 'h':
                {
                    ReadPayload(ref index, escapeOffset, out ReadOnlySpan<char> payload);
                    bool relative = payload.Length > 0 && payload[^1] is 'x' or 'X';
                    if (relative) payload = payload[..^1];
                    double height = ParsePositiveDouble(payload, escapeOffset, "height");
                    ChangeStyle(style with { Height = new CadMTextHeight(height, relative) }, escapeOffset);
                    return;
                }
                case 'W':
                case 'w':
                {
                    ReadPayload(ref index, escapeOffset, out ReadOnlySpan<char> payload);
                    double factor = ParsePositiveDouble(payload, escapeOffset, "width factor");
                    ChangeStyle(style with { WidthFactor = factor }, escapeOffset);
                    return;
                }
                case 'T':
                case 't':
                {
                    ReadPayload(ref index, escapeOffset, out ReadOnlySpan<char> payload);
                    double factor = ParsePositiveDouble(payload, escapeOffset, "tracking factor");
                    ChangeStyle(style with { TrackingFactor = factor }, escapeOffset);
                    return;
                }
                case 'Q':
                case 'q':
                {
                    ReadPayload(ref index, escapeOffset, out ReadOnlySpan<char> payload);
                    double degrees = ParseDouble(payload, escapeOffset, "oblique angle");
                    if (degrees is <= -85.0 or >= 85.0)
                    {
                        throw Error("MTEXT oblique angle must be greater than -85 and less than 85 degrees", escapeOffset);
                    }

                    ChangeStyle(style with { ObliqueDegrees = degrees }, escapeOffset);
                    return;
                }
                case 'p':
                {
                    ReadPayload(ref index, escapeOffset, out ReadOnlySpan<char> payload);
                    ChangeStyle(style with
                    {
                        Paragraph = ParseParagraph(payload, style.Paragraph, escapeOffset),
                    }, escapeOffset);
                    return;
                }
                case 'S':
                case 's':
                {
                    FlushText(escapeOffset);
                    ReadPayload(ref index, escapeOffset, out ReadOnlySpan<char> payload);
                    ParseStack(payload, escapeOffset);
                    return;
                }
                default:
                    throw new NotSupportedException(
                        $"MTEXT escape '\\{code}' is not represented by the typed content model (source offset {escapeOffset}).");
            }
        }

        void ParseUnicodeEscape(ref int index, int escapeOffset)
        {
            if (index >= source.Length || source[index] != '+' || index + 4 >= source.Length)
            {
                throw Error("MTEXT contains a truncated Unicode escape", escapeOffset);
            }

            index++;
            int scalar = 0;
            for (int digit = 0; digit < 4; digit++)
            {
                int value = HexValue(source[index + digit]);
                if (value < 0)
                {
                    throw Error("MTEXT contains an invalid Unicode escape", escapeOffset);
                }
                scalar = (scalar << 4) | value;
            }
            index += 4;
            if (char.IsSurrogate((char)scalar))
            {
                throw Error("MTEXT Unicode escapes cannot encode an isolated surrogate", escapeOffset);
            }
            Append((char)scalar, escapeOffset);
        }

        void AppendPercentControl(ref int index)
        {
            int offset = index;
            if (index + 2 >= source.Length)
            {
                throw Error("MTEXT contains a truncated percent control", offset);
            }

            char code = char.ToLowerInvariant(source[index + 2]);
            char decoded = code switch
            {
                'd' => '\u00B0',
                'p' => '\u00B1',
                'c' => '\u2205',
                '%' => '%',
                _ => throw new NotSupportedException(
                    $"MTEXT percent control '%%{source[index + 2]}' is unsupported (source offset {offset})."),
            };
            Append(decoded, offset);
            index += 3;
        }

        void ParseStack(ReadOnlySpan<char> payload, int sourceOffset)
        {
            int separator = -1;
            CadMTextStackKind kind = default;
            bool escaped = false;
            for (int i = 0; i < payload.Length; i++)
            {
                char candidate = payload[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (candidate == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (candidate is '/' or '#' or '^')
                {
                    separator = i;
                    kind = candidate switch
                    {
                        '/' => CadMTextStackKind.Horizontal,
                        '#' => CadMTextStackKind.Diagonal,
                        _ => CadMTextStackKind.Tolerance,
                    };
                    break;
                }
            }

            if (separator <= 0 || separator >= payload.Length - 1)
            {
                throw Error("MTEXT stacked text requires non-empty upper and lower values", sourceOffset);
            }

            string upper = DecodeStackPart(payload[..separator], sourceOffset);
            string lower = DecodeStackPart(payload[(separator + 1)..], sourceOffset);
            AddDecodedCount(checked(upper.Length + lower.Length), sourceOffset);
            AddInline(new CadMTextInline(
                CadMTextInlineKind.Stack,
                upper,
                lower,
                kind,
                style,
                sourceOffset));
        }

        void ChangeStyle(CadMTextRunStyle value, int sourceOffset)
        {
            FlushText(sourceOffset);
            style = value;
        }

        void Append(char value, int sourceOffset)
        {
            if (char.IsSurrogate(value))
            {
                if (char.IsHighSurrogate(value) &&
                    sourceOffset + 1 < source.Length &&
                    char.IsLowSurrogate(source[sourceOffset + 1]))
                {
                    // The loop appends the matching low surrogate on its next pass.
                }
                else if (!char.IsLowSurrogate(value) ||
                         text.Length == 0 ||
                         !char.IsHighSurrogate(text[^1]))
                {
                    throw Error("MTEXT contains an unpaired UTF-16 surrogate", sourceOffset);
                }
            }

            if (text.Length == 0) textSourceOffset = sourceOffset;
            AddDecodedCount(1, sourceOffset);
            text.Append(value);
        }

        void AddDecodedCount(int count, int sourceOffset)
        {
            decodedCodeUnits = checked(decodedCodeUnits + count);
            if (decodedCodeUnits > options.MaxDecodedCodeUnits)
            {
                throw Error("MTEXT decoded text exceeds the configured limit", sourceOffset);
            }
        }

        void FlushText(int endOffset)
        {
            if (text.Length == 0) return;
            AddInline(new CadMTextInline(
                CadMTextInlineKind.Text,
                text.ToString(),
                string.Empty,
                default,
                style,
                textSourceOffset));
            text.Clear();
            textSourceOffset = endOffset;
        }

        void AddInline(CadMTextInline value)
        {
            if (inlines.Count >= options.MaxInlineCount)
            {
                throw Error("MTEXT inline count exceeds the configured limit", value.SourceOffset);
            }

            inlines.Add(value);
        }

        void ReadPayload(
            ref int sourceIndex,
            int sourceOffset,
            out ReadOnlySpan<char> payload)
        {
            int end = source.IndexOf(';', sourceIndex);
            if (end < 0)
            {
                throw Error(
                    "MTEXT formatting control is missing its terminating semicolon",
                    sourceOffset);
            }
            payload = source.AsSpan(sourceIndex, end - sourceIndex);
            sourceIndex = end + 1;
        }
    }

    private static CadMTextFontOverride ParseFont(
        ReadOnlySpan<char> payload,
        CadMTextFontOverride current,
        int sourceOffset)
    {
        int separator = payload.IndexOf('|');
        ReadOnlySpan<char> family = separator < 0 ? payload : payload[..separator];
        if (family.IsEmpty)
        {
            throw Error("MTEXT font override requires a family name", sourceOffset);
        }

        bool bold = current.IsBold;
        bool italic = current.IsItalic;
        int characterSet = current.CharacterSet;
        int pitchAndFamily = current.PitchAndFamily;
        int cursor = separator < 0 ? payload.Length : separator + 1;
        while (cursor < payload.Length)
        {
            int next = payload[cursor..].IndexOf('|');
            int end = next < 0 ? payload.Length : cursor + next;
            ReadOnlySpan<char> option = payload[cursor..end];
            if (option.Length < 2)
            {
                throw Error("MTEXT font override contains an invalid option", sourceOffset);
            }

            ReadOnlySpan<char> value = option[1..];
            switch (char.ToLowerInvariant(option[0]))
            {
                case 'b':
                    bold = ParseBoolean(value, sourceOffset, "font bold flag");
                    break;
                case 'i':
                    italic = ParseBoolean(value, sourceOffset, "font italic flag");
                    break;
                case 'c':
                    characterSet = ParseInteger(value, sourceOffset, "font character set");
                    break;
                case 'p':
                    pitchAndFamily = ParseInteger(value, sourceOffset, "font pitch and family");
                    break;
                default:
                    throw new NotSupportedException(
                        $"MTEXT font option '{option.ToString()}' is unsupported (source offset {sourceOffset}).");
            }
            cursor = end + 1;
        }

        return new CadMTextFontOverride(
            family.ToString(),
            bold,
            italic,
            characterSet,
            pitchAndFamily);
    }

    private static CadMTextParagraphFormat ParseParagraph(
        ReadOnlySpan<char> payload,
        CadMTextParagraphFormat current,
        int sourceOffset)
    {
        CadMTextParagraphAlignment alignment = current.Alignment;
        for (int index = 0; index < payload.Length - 1; index++)
        {
            if (payload[index] != 'q') continue;
            alignment = char.ToLowerInvariant(payload[index + 1]) switch
            {
                'l' => CadMTextParagraphAlignment.Left,
                'c' => CadMTextParagraphAlignment.Center,
                'r' => CadMTextParagraphAlignment.Right,
                'j' => CadMTextParagraphAlignment.Justify,
                'd' => CadMTextParagraphAlignment.Distributed,
                '*' => CadMTextParagraphAlignment.Left,
                _ => throw Error("MTEXT paragraph alignment is invalid", sourceOffset),
            };
            break;
        }

        return new CadMTextParagraphFormat(alignment, payload.ToString());
    }

    private static string DecodeStackPart(ReadOnlySpan<char> source, int sourceOffset)
    {
        var result = new StringBuilder(source.Length);
        for (int index = 0; index < source.Length; index++)
        {
            char value = source[index];
            if (value == '\\')
            {
                if (++index >= source.Length)
                {
                    throw Error("MTEXT stack ends with a truncated escape", sourceOffset);
                }

                char escaped = source[index];
                if (escaped is '\\' or '{' or '}' or '/' or '#' or '^')
                {
                    result.Append(escaped);
                    continue;
                }

                if ((escaped is 'U' or 'u') && index + 5 < source.Length && source[index + 1] == '+')
                {
                    int scalar = 0;
                    for (int digit = 0; digit < 4; digit++)
                    {
                        int hex = HexValue(source[index + 2 + digit]);
                        if (hex < 0)
                        {
                            throw Error("MTEXT stack contains an invalid Unicode escape", sourceOffset);
                        }
                        scalar = (scalar << 4) | hex;
                    }
                    if (char.IsSurrogate((char)scalar))
                    {
                        throw Error("MTEXT stack Unicode escape encodes an isolated surrogate", sourceOffset);
                    }
                    result.Append((char)scalar);
                    index += 5;
                    continue;
                }

                throw new NotSupportedException(
                    $"MTEXT stack escape '\\{escaped}' requires nested stack formatting support (source offset {sourceOffset}).");
            }

            if (value == '%' && index + 2 < source.Length && source[index + 1] == '%')
            {
                char code = char.ToLowerInvariant(source[index + 2]);
                result.Append(code switch
                {
                    'd' => '\u00B0',
                    'p' => '\u00B1',
                    'c' => '\u2205',
                    '%' => '%',
                    _ => throw new NotSupportedException(
                        $"MTEXT stack percent control '%%{source[index + 2]}' is unsupported (source offset {sourceOffset})."),
                });
                index += 2;
                continue;
            }

            if (char.IsSurrogate(value))
            {
                if (!char.IsHighSurrogate(value) || index + 1 >= source.Length ||
                    !char.IsLowSurrogate(source[index + 1]))
                {
                    throw Error("MTEXT stack contains an unpaired UTF-16 surrogate", sourceOffset);
                }
                result.Append(value);
                result.Append(source[++index]);
                continue;
            }
            result.Append(value);
        }
        return result.ToString();
    }

    private static double ParsePositiveDouble(
        ReadOnlySpan<char> value,
        int sourceOffset,
        string name)
    {
        double parsed = ParseDouble(value, sourceOffset, name);
        if (parsed <= 0.0)
        {
            throw Error($"MTEXT {name} must be positive", sourceOffset);
        }
        return parsed;
    }

    private static double ParseDouble(ReadOnlySpan<char> value, int sourceOffset, string name)
    {
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed) ||
            !double.IsFinite(parsed))
        {
            throw Error($"MTEXT {name} is not a finite invariant number", sourceOffset);
        }
        return parsed;
    }

    private static int ParseInteger(ReadOnlySpan<char> value, int sourceOffset, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            throw Error($"MTEXT {name} is not an integer", sourceOffset);
        }
        return parsed;
    }

    private static uint ParseUnsignedInteger(ReadOnlySpan<char> value, int sourceOffset, string name)
    {
        if (!uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint parsed))
        {
            throw Error($"MTEXT {name} is not an unsigned integer", sourceOffset);
        }
        return parsed;
    }

    private static bool ParseBoolean(ReadOnlySpan<char> value, int sourceOffset, string name) =>
        value.Length == 1 && value[0] is '0' or '1'
            ? value[0] == '1'
            : throw Error($"MTEXT {name} must be 0 or 1", sourceOffset);

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };

    private static CadMTextParseException Error(string message, int offset) => new(message, offset);

    private static void ValidateOptions(CadMTextParseOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxNestingDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxNestingDepth, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxInlineCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDecodedCodeUnits, 1);
    }

}
