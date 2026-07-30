using System.Globalization;
using System.Text;
using ProGPU.Media.Playback;

namespace ProGPU.Media.Playback;

/// <summary>
/// Clean-room WebVTT cue settings and cue-text parser based on the W3C WebVTT
/// data model. Parsing is O(U + T + R) time and O(U + R) retained storage for
/// U UTF-16 input units, T setting tokens, and R emitted style runs. It runs
/// only while publishing an immutable native cue snapshot.
/// </summary>
internal static class WebVttCueParser
{
    internal static ParsedCue Parse(
        string payload,
        string? settings)
    {
        ArgumentNullException.ThrowIfNull(payload);
        MediaPlaybackTimedTextCueLayout layout =
            ParseSettings(settings);
        return ParseText(payload, in layout);
    }

    private static MediaPlaybackTimedTextCueLayout
        ParseSettings(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings))
        {
            return default;
        }

        string regionName = string.Empty;
        double? linePosition = null;
        MediaPlaybackTimedTextLinePositionUnit lineUnit =
            MediaPlaybackTimedTextLinePositionUnit.Lines;
        MediaPlaybackTimedTextAlignment? lineAlignment =
            null;
        double? textPosition = null;
        MediaPlaybackTimedTextAlignment? positionAlignment =
            null;
        double? size = null;
        MediaPlaybackTimedTextAlignment? textAlignment =
            null;
        MediaPlaybackTimedTextWritingMode? writingMode =
            null;
        bool hasRegion = false;
        bool hasLine = false;
        bool hasPosition = false;
        bool hasSize = false;
        bool hasAlignment = false;
        bool hasVertical = false;

        ReadOnlySpan<char> source = settings.AsSpan();
        int position = 0;
        while (position < source.Length)
        {
            while (position < source.Length &&
                   char.IsWhiteSpace(source[position]))
            {
                position++;
            }
            int tokenStart = position;
            while (position < source.Length &&
                   !char.IsWhiteSpace(source[position]))
            {
                position++;
            }
            ReadOnlySpan<char> token =
                source[tokenStart..position];
            int colon = token.IndexOf(':');
            if (colon <= 0 ||
                colon == token.Length - 1)
            {
                continue;
            }

            ReadOnlySpan<char> name = token[..colon];
            ReadOnlySpan<char> value =
                token[(colon + 1)..];
            if (name.SequenceEqual("vertical") &&
                !hasVertical)
            {
                hasVertical = true;
                if (value.SequenceEqual("rl"))
                {
                    writingMode =
                        MediaPlaybackTimedTextWritingMode
                            .TopBottomRightLeft;
                }
                else if (value.SequenceEqual("lr"))
                {
                    writingMode =
                        MediaPlaybackTimedTextWritingMode
                            .TopBottomLeftRight;
                }
            }
            else if (name.SequenceEqual("line") &&
                     !hasLine)
            {
                hasLine = true;
                ParseLine(
                    value,
                    out linePosition,
                    out lineUnit,
                    out lineAlignment);
            }
            else if (name.SequenceEqual("position") &&
                     !hasPosition)
            {
                hasPosition = true;
                ParsePosition(
                    value,
                    out textPosition,
                    out positionAlignment);
            }
            else if (name.SequenceEqual("size") &&
                     !hasSize)
            {
                hasSize = true;
                if (TryParsePercentage(
                        value,
                        out double parsedSize))
                {
                    size = parsedSize;
                }
            }
            else if (name.SequenceEqual("align") &&
                     !hasAlignment)
            {
                hasAlignment = true;
                textAlignment =
                    ParseAlignment(value);
            }
            else if (name.SequenceEqual("region") &&
                     !hasRegion)
            {
                hasRegion = true;
                regionName = value.ToString();
            }
        }

        return new MediaPlaybackTimedTextCueLayout(
            regionName,
            linePosition,
            lineUnit,
            lineAlignment,
            textPosition,
            positionAlignment,
            size,
            textAlignment,
            writingMode);
    }

    private static void ParseLine(
        ReadOnlySpan<char> value,
        out double? linePosition,
        out MediaPlaybackTimedTextLinePositionUnit unit,
        out MediaPlaybackTimedTextAlignment? alignment)
    {
        SplitSettingValue(
            value,
            out ReadOnlySpan<char> scalar,
            out ReadOnlySpan<char> alignmentValue);
        linePosition = null;
        unit =
            MediaPlaybackTimedTextLinePositionUnit.Lines;
        alignment = ParseAlignment(alignmentValue);
        if (scalar.SequenceEqual("auto"))
        {
            return;
        }
        if (TryParsePercentage(
                scalar,
                out double percentage))
        {
            linePosition = percentage;
            unit =
                MediaPlaybackTimedTextLinePositionUnit
                    .Percentage;
            return;
        }
        if (double.TryParse(
                scalar,
                NumberStyles.AllowLeadingSign |
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double line) &&
            double.IsFinite(line))
        {
            linePosition = line;
        }
    }

    private static void ParsePosition(
        ReadOnlySpan<char> value,
        out double? position,
        out MediaPlaybackTimedTextAlignment? alignment)
    {
        SplitSettingValue(
            value,
            out ReadOnlySpan<char> scalar,
            out ReadOnlySpan<char> alignmentValue);
        position =
            TryParsePercentage(
                scalar,
                out double percentage)
                ? percentage
                : null;
        alignment = ParseAlignment(alignmentValue);
    }

    private static void SplitSettingValue(
        ReadOnlySpan<char> value,
        out ReadOnlySpan<char> scalar,
        out ReadOnlySpan<char> alignment)
    {
        int comma = value.IndexOf(',');
        if (comma < 0)
        {
            scalar = value;
            alignment = default;
            return;
        }
        scalar = value[..comma];
        alignment = value[(comma + 1)..];
    }

    private static bool TryParsePercentage(
        ReadOnlySpan<char> value,
        out double percentage)
    {
        percentage = 0d;
        if (value.Length < 2 ||
            value[^1] != '%' ||
            !double.TryParse(
                value[..^1],
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out percentage) ||
            !double.IsFinite(percentage))
        {
            return false;
        }
        return percentage is >= 0d and <= 100d;
    }

    private static MediaPlaybackTimedTextAlignment?
        ParseAlignment(ReadOnlySpan<char> value)
    {
        if (value.SequenceEqual("start"))
        {
            return MediaPlaybackTimedTextAlignment.Start;
        }
        if (value.SequenceEqual("end"))
        {
            return MediaPlaybackTimedTextAlignment.End;
        }
        if (value.SequenceEqual("center") ||
            value.SequenceEqual("middle"))
        {
            return MediaPlaybackTimedTextAlignment.Center;
        }
        if (value.SequenceEqual("left") ||
            value.SequenceEqual("line-left"))
        {
            return MediaPlaybackTimedTextAlignment.Left;
        }
        if (value.SequenceEqual("right") ||
            value.SequenceEqual("line-right"))
        {
            return MediaPlaybackTimedTextAlignment.Right;
        }
        return null;
    }

    private static ParsedCue ParseText(
        string payload,
        in MediaPlaybackTimedTextCueLayout layout)
    {
        var output = new StringBuilder(payload.Length);
        var runs = new List<GlobalSubformat>();
        int boldDepth = 0;
        int italicDepth = 0;
        int underlineDepth = 0;
        StyleFlags activeStyle = StyleFlags.None;
        int runStart = 0;

        for (int index = 0;
             index < payload.Length;
             index++)
        {
            char current = payload[index];
            if (current == '<')
            {
                int close = payload.IndexOf('>', index + 1);
                if (close >= 0)
                {
                    ReadOnlySpan<char> tag =
                        payload.AsSpan(
                            index + 1,
                            close - index - 1)
                            .Trim();
                    StyleFlags oldStyle = activeStyle;
                    ApplyTag(
                        tag,
                        ref boldDepth,
                        ref italicDepth,
                        ref underlineDepth);
                    activeStyle = GetStyle(
                        boldDepth,
                        italicDepth,
                        underlineDepth);
                    if (oldStyle != activeStyle)
                    {
                        FlushRun(
                            runs,
                            oldStyle,
                            runStart,
                            output.Length);
                        runStart = output.Length;
                    }
                    index = close;
                    continue;
                }
            }
            if (current == '&' &&
                TryDecodeEntity(
                    payload.AsSpan(index),
                    out char decoded,
                    out int entityLength))
            {
                output.Append(decoded);
                index += entityLength - 1;
                continue;
            }
            if (current == '\r')
            {
                output.Append('\n');
                if (index + 1 < payload.Length &&
                    payload[index + 1] == '\n')
                {
                    index++;
                }
                continue;
            }
            output.Append(current);
        }
        FlushRun(
            runs,
            activeStyle,
            runStart,
            output.Length);

        string text = output.ToString();
        IReadOnlyList<
            MediaPlaybackTimedTextLineDescriptor> lines =
                BuildLines(text, runs);
        var presentation =
            new MediaPlaybackTimedTextCuePresentation(
                lines,
                layout: layout);
        return new ParsedCue(text, presentation);
    }

    private static void ApplyTag(
        ReadOnlySpan<char> tag,
        ref int boldDepth,
        ref int italicDepth,
        ref int underlineDepth)
    {
        bool closing =
            tag.Length != 0 && tag[0] == '/';
        if (closing)
        {
            tag = tag[1..].TrimStart();
        }
        int nameLength = 0;
        while (nameLength < tag.Length &&
               !char.IsWhiteSpace(tag[nameLength]) &&
               tag[nameLength] != '.')
        {
            nameLength++;
        }
        ReadOnlySpan<char> name = tag[..nameLength];
        ref int depth = ref boldDepth;
        if (name.SequenceEqual("b"))
        {
            depth = ref boldDepth;
        }
        else if (name.SequenceEqual("i"))
        {
            depth = ref italicDepth;
        }
        else if (name.SequenceEqual("u"))
        {
            depth = ref underlineDepth;
        }
        else
        {
            return;
        }

        if (closing)
        {
            if (depth > 0)
            {
                depth--;
            }
        }
        else
        {
            depth++;
        }
    }

    private static StyleFlags GetStyle(
        int boldDepth,
        int italicDepth,
        int underlineDepth)
    {
        StyleFlags style = StyleFlags.None;
        if (boldDepth != 0)
        {
            style |= StyleFlags.Bold;
        }
        if (italicDepth != 0)
        {
            style |= StyleFlags.Italic;
        }
        if (underlineDepth != 0)
        {
            style |= StyleFlags.Underline;
        }
        return style;
    }

    private static void FlushRun(
        List<GlobalSubformat> runs,
        StyleFlags style,
        int start,
        int end)
    {
        if (style == StyleFlags.None || end <= start)
        {
            return;
        }
        if (runs.Count != 0)
        {
            GlobalSubformat previous = runs[^1];
            if (previous.End == start &&
                previous.Style == style)
            {
                runs[^1] = previous with
                {
                    End = end
                };
                return;
            }
        }
        runs.Add(new GlobalSubformat(start, end, style));
    }

    private static bool TryDecodeEntity(
        ReadOnlySpan<char> source,
        out char decoded,
        out int length)
    {
        if (source.StartsWith("&amp;"))
        {
            decoded = '&';
            length = 5;
            return true;
        }
        if (source.StartsWith("&lt;"))
        {
            decoded = '<';
            length = 4;
            return true;
        }
        if (source.StartsWith("&gt;"))
        {
            decoded = '>';
            length = 4;
            return true;
        }
        if (source.StartsWith("&nbsp;"))
        {
            decoded = '\u00A0';
            length = 6;
            return true;
        }
        if (source.StartsWith("&lrm;"))
        {
            decoded = '\u200E';
            length = 5;
            return true;
        }
        if (source.StartsWith("&rlm;"))
        {
            decoded = '\u200F';
            length = 5;
            return true;
        }
        decoded = default;
        length = 0;
        return false;
    }

    private static IReadOnlyList<
        MediaPlaybackTimedTextLineDescriptor> BuildLines(
            string text,
            List<GlobalSubformat> runs)
    {
        var lines =
            new List<
                MediaPlaybackTimedTextLineDescriptor>();
        int lineStart = 0;
        int runIndex = 0;
        for (int position = 0;
             position <= text.Length;
             position++)
        {
            if (position != text.Length &&
                text[position] != '\n')
            {
                continue;
            }
            int lineEnd = position;
            while (runIndex < runs.Count &&
                   runs[runIndex].End <= lineStart)
            {
                runIndex++;
            }
            var subformats =
                new List<
                    MediaPlaybackTimedTextSubformatDescriptor>();
            for (int index = runIndex;
                 index < runs.Count &&
                 runs[index].Start < lineEnd;
                 index++)
            {
                GlobalSubformat run = runs[index];
                int start = Math.Max(run.Start, lineStart);
                int end = Math.Min(run.End, lineEnd);
                if (end <= start)
                {
                    continue;
                }
                subformats.Add(
                    new MediaPlaybackTimedTextSubformatDescriptor(
                        start - lineStart,
                        end - start,
                        ToStyle(run.Style)));
            }
            lines.Add(
                new MediaPlaybackTimedTextLineDescriptor(
                    text.Substring(
                        lineStart,
                        lineEnd - lineStart),
                    subformats));
            lineStart = position + 1;
        }
        return lines;
    }

    private static MediaPlaybackTimedTextStyle ToStyle(
        StyleFlags style) =>
        new(
            FontStyle:
                (style & StyleFlags.Italic) != 0
                    ? MediaPlaybackTimedTextFontStyle
                        .Italic
                    : null,
            FontWeight:
                (style & StyleFlags.Bold) != 0
                    ? MediaPlaybackTimedTextWeight.Bold
                    : null,
            IsUnderlineEnabled:
                (style & StyleFlags.Underline) != 0
                    ? true
                    : null);

    [Flags]
    private enum StyleFlags
    {
        None = 0,
        Bold = 1,
        Italic = 2,
        Underline = 4
    }

    private readonly record struct GlobalSubformat(
        int Start,
        int End,
        StyleFlags Style);

    internal readonly record struct ParsedCue(
        string Text,
        MediaPlaybackTimedTextCuePresentation
            Presentation);
}
