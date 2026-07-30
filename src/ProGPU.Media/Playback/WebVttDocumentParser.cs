using System.Collections.ObjectModel;
using System.Globalization;

namespace ProGPU.Media.Playback;

/// <summary>
/// Clean-room parser for the WebVTT file structure defined by the W3C WebVTT
/// specification. Parsing is O(N + C) time and O(N + C) retained storage for
/// N UTF-16 input units and C cues. The caller bounds and strictly decodes the
/// byte input before invoking this parser.
/// </summary>
internal static class WebVttDocumentParser
{
    internal const int MaximumCueCount = 100_000;

    internal static WebVttDocument Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string normalized = NormalizeLineEndings(source);
        ReadOnlySpan<char> text = normalized.AsSpan();
        int position = 0;
        ReadOnlySpan<char> signature = ReadLine(
            text,
            ref position);
        if (signature.Length != 0 &&
            signature[0] == '\uFEFF')
        {
            signature = signature[1..];
        }
        if (!IsSignature(signature))
        {
            throw new FormatException(
                "A WebVTT source must begin with the WEBVTT signature.");
        }

        SkipHeader(text, ref position);
        var cues = new List<WebVttDocumentCue>();
        while (position < text.Length)
        {
            SkipBlankLines(text, ref position);
            if (position >= text.Length)
            {
                break;
            }

            int blockStart = position;
            ReadOnlySpan<char> firstLine =
                ReadLine(text, ref position);
            if (IsMetadataBlock(firstLine))
            {
                SkipBlock(text, ref position);
                continue;
            }

            string cueId = string.Empty;
            ReadOnlySpan<char> timingLine = firstLine;
            if (timingLine.IndexOf("-->") < 0)
            {
                cueId = timingLine.ToString();
                if (position >= text.Length)
                {
                    break;
                }
                timingLine = ReadLine(text, ref position);
            }

            if (!TryParseTiming(
                    timingLine,
                    out TimeSpan start,
                    out TimeSpan end,
                    out string settings))
            {
                position = blockStart;
                SkipBlock(text, ref position);
                continue;
            }

            string payload = ReadPayload(text, ref position);
            WebVttCueParser.ParsedCue parsed =
                WebVttCueParser.Parse(payload, settings);
            if (cues.Count == MaximumCueCount)
            {
                throw new FormatException(
                    $"A WebVTT source cannot contain more than {MaximumCueCount} cues.");
            }
            cues.Add(
                new WebVttDocumentCue(
                    cueId,
                    start,
                    end - start,
                    parsed.Text,
                    parsed.Presentation));
        }

        return new WebVttDocument(cues);
    }

    private static string NormalizeLineEndings(string source)
    {
        int firstCarriageReturn = source.IndexOf('\r');
        if (firstCarriageReturn < 0)
        {
            return source;
        }

        var builder =
            new System.Text.StringBuilder(source.Length);
        builder.Append(source, 0, firstCarriageReturn);
        for (int index = firstCarriageReturn;
             index < source.Length;
             index++)
        {
            char current = source[index];
            if (current == '\r')
            {
                builder.Append('\n');
                if (index + 1 < source.Length &&
                    source[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                builder.Append(current);
            }
        }
        return builder.ToString();
    }

    private static bool IsSignature(
        ReadOnlySpan<char> line) =>
        line.SequenceEqual("WEBVTT") ||
        (line.Length > 6 &&
         line.StartsWith("WEBVTT") &&
         (line[6] == ' ' || line[6] == '\t'));

    private static void SkipHeader(
        ReadOnlySpan<char> text,
        ref int position)
    {
        while (position < text.Length)
        {
            if (ReadLine(text, ref position).Length == 0)
            {
                return;
            }
        }
    }

    private static void SkipBlankLines(
        ReadOnlySpan<char> text,
        ref int position)
    {
        while (position < text.Length)
        {
            int lineStart = position;
            if (ReadLine(text, ref position).Length != 0)
            {
                position = lineStart;
                return;
            }
        }
    }

    private static bool IsMetadataBlock(
        ReadOnlySpan<char> line) =>
        StartsBlock(line, "NOTE") ||
        StartsBlock(line, "STYLE") ||
        StartsBlock(line, "REGION");

    private static bool StartsBlock(
        ReadOnlySpan<char> line,
        ReadOnlySpan<char> name) =>
        line.SequenceEqual(name) ||
        (line.Length > name.Length &&
         line.StartsWith(name) &&
         (line[name.Length] == ' ' ||
          line[name.Length] == '\t'));

    private static void SkipBlock(
        ReadOnlySpan<char> text,
        ref int position)
    {
        while (position < text.Length &&
               ReadLine(text, ref position).Length != 0)
        {
        }
    }

    private static string ReadPayload(
        ReadOnlySpan<char> text,
        ref int position)
    {
        int payloadStart = position;
        int payloadEnd = position;
        while (position < text.Length)
        {
            int lineStart = position;
            ReadOnlySpan<char> line =
                ReadLine(text, ref position);
            if (line.Length == 0)
            {
                break;
            }
            payloadEnd =
                position > lineStart &&
                text[position - 1] == '\n'
                    ? position - 1
                    : position;
        }
        return text[payloadStart..payloadEnd].ToString();
    }

    private static ReadOnlySpan<char> ReadLine(
        ReadOnlySpan<char> text,
        ref int position)
    {
        int start = position;
        int newline = text[position..].IndexOf('\n');
        if (newline < 0)
        {
            position = text.Length;
            return text[start..];
        }
        position += newline + 1;
        return text.Slice(start, newline);
    }

    private static bool TryParseTiming(
        ReadOnlySpan<char> line,
        out TimeSpan start,
        out TimeSpan end,
        out string settings)
    {
        start = default;
        end = default;
        settings = string.Empty;
        int arrow = line.IndexOf("-->");
        if (arrow <= 0 ||
            arrow + 3 >= line.Length ||
            !IsAsciiWhitespace(line[arrow - 1]) ||
            !IsAsciiWhitespace(line[arrow + 3]))
        {
            return false;
        }

        ReadOnlySpan<char> startText =
            line[..arrow].Trim();
        ReadOnlySpan<char> remainder =
            line[(arrow + 3)..].TrimStart();
        int separator = remainder.IndexOfAny(' ', '\t');
        ReadOnlySpan<char> endText;
        if (separator < 0)
        {
            endText = remainder;
        }
        else
        {
            endText = remainder[..separator];
            settings = remainder[separator..]
                .Trim()
                .ToString();
        }

        return TryParseTimestamp(startText, out start) &&
               TryParseTimestamp(endText, out end) &&
               end > start;
    }

    private static bool TryParseTimestamp(
        ReadOnlySpan<char> source,
        out TimeSpan timestamp)
    {
        timestamp = default;
        int firstColon = source.IndexOf(':');
        int dot = source.LastIndexOf('.');
        if (firstColon <= 0 ||
            dot <= firstColon ||
            source.Length - dot - 1 != 3)
        {
            return false;
        }

        ReadOnlySpan<char> beforeDot = source[..dot];
        int secondColon =
            beforeDot[(firstColon + 1)..].IndexOf(':');
        long hours = 0;
        ReadOnlySpan<char> minutesText;
        ReadOnlySpan<char> secondsText;
        if (secondColon < 0)
        {
            minutesText = beforeDot[..firstColon];
            secondsText = beforeDot[(firstColon + 1)..];
        }
        else
        {
            secondColon += firstColon + 1;
            if (firstColon < 2 ||
                !TryParseNonNegativeInteger(
                    beforeDot[..firstColon],
                    out hours))
            {
                return false;
            }
            minutesText =
                beforeDot[(firstColon + 1)..secondColon];
            secondsText = beforeDot[(secondColon + 1)..];
        }

        if (minutesText.Length != 2 ||
            secondsText.Length != 2 ||
            !TryParseNonNegativeInteger(
                minutesText,
                out long minutes) ||
            !TryParseNonNegativeInteger(
                secondsText,
                out long seconds) ||
            !TryParseNonNegativeInteger(
                source[(dot + 1)..],
                out long milliseconds) ||
            minutes > 59 ||
            seconds > 59)
        {
            return false;
        }

        try
        {
            long totalMilliseconds = checked(
                (((hours * 60L) + minutes) * 60L +
                 seconds) *
                    1000L +
                milliseconds);
            timestamp =
                TimeSpan.FromMilliseconds(
                    totalMilliseconds);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseNonNegativeInteger(
        ReadOnlySpan<char> source,
        out long value)
    {
        if (source.Length == 0)
        {
            value = 0;
            return false;
        }
        return long.TryParse(
            source,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool IsAsciiWhitespace(char value) =>
        value is ' ' or '\t';
}

internal sealed class WebVttDocument
{
    private readonly ReadOnlyCollection<WebVttDocumentCue>
        _cues;

    internal WebVttDocument(
        IReadOnlyList<WebVttDocumentCue> cues)
    {
        var copy = new WebVttDocumentCue[cues.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = cues[index];
        }
        _cues = Array.AsReadOnly(copy);
    }

    internal IReadOnlyList<WebVttDocumentCue> Cues =>
        _cues;
}

internal readonly record struct WebVttDocumentCue(
    string Id,
    TimeSpan StartTime,
    TimeSpan Duration,
    string Text,
    MediaPlaybackTimedTextCuePresentation Presentation);
