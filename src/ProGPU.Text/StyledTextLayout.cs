using System.Numerics;
using ProGPU.Text.Bidi;
using ProGPU.Text.Shaping;

namespace ProGPU.Text;

/// <summary>One caller-authored style used by a <see cref="StyledTextLayout"/> range.</summary>
public readonly record struct StyledTextStyle(
    TtfFont Font,
    float FontSize,
    float WidthScale = 1.0f,
    float TrackingFactor = 1.0f,
    float BaselineShift = 0.0f,
    TextAlignment Alignment = TextAlignment.Left,
    int Tag = 0);

/// <summary>A half-open UTF-16 range carrying one styled-layout state.</summary>
public readonly record struct StyledTextRange(
    int Start,
    int Length,
    StyledTextStyle Style);

/// <summary>
/// An inline object occupying one U+FFFC code unit in the styled source.
/// Ascent and descent are positive distances from its baseline.
/// </summary>
public readonly record struct StyledTextInlineBox(
    int TextPosition,
    float Width,
    float Ascent,
    float Descent,
    int Tag = 0);

public readonly record struct StyledTextGlyph(
    ushort GlyphIndex,
    Vector2 Position,
    float Advance,
    TtfFont Font,
    int StyleIndex,
    int Cluster,
    sbyte BidiLevel,
    uint CodePoint,
    ShapingGlyphFlags ShapingFlags);

public readonly record struct StyledTextPositionedBox(
    Vector2 Position,
    float Width,
    float Height,
    float Baseline,
    int TextPosition,
    int StyleIndex,
    int Tag);

public readonly record struct StyledTextLine(
    int GlyphOffset,
    int GlyphCount,
    int BoxOffset,
    int BoxCount,
    float Top,
    float Baseline,
    float Width,
    float Height,
    bool IsParagraphFinal,
    int ParagraphStart);

public sealed class StyledTextLayoutOptions
{
    public float MaxWidth { get; init; } = float.PositiveInfinity;
    public float MinimumLineSpacing { get; init; }
    public bool ExactLineSpacing { get; init; }
    public ShapingDirection BaseDirection { get; init; } = ShapingDirection.Unspecified;
    public TextShapingOptions ShapingOptions { get; init; } = TextShapingOptions.Default;
}

/// <summary>
/// Immutable positioned result for Unicode text carrying caller-defined style
/// ranges and inline objects.
/// </summary>
/// <remarks>
/// This is an original generalization of ProGPU's authoritative
/// <c>TextLayout.GenerateShapedLayout</c> pipeline. It preserves paragraph-wide
/// UAX #9 resolution, fallback-aware OpenType shaping, cluster-safe wrapping,
/// and visual reordering while adding variable font metrics, width/tracking,
/// justification, and inline boxes. Layout is O(T + G + B) average time and
/// O(T + G + B) storage for T UTF-16 units, G glyphs, and B boxes; adversarial
/// fallback discovery retains the existing TextLayout platform-font cost.
/// </remarks>
public sealed class StyledTextLayout
{
    private readonly StyledTextGlyph[] _glyphs;
    private readonly StyledTextPositionedBox[] _boxes;
    private readonly StyledTextLine[] _lines;

    public string Text { get; }
    public ReadOnlyMemory<StyledTextRange> Ranges { get; }
    public ReadOnlyMemory<StyledTextGlyph> Glyphs => _glyphs;
    public ReadOnlyMemory<StyledTextPositionedBox> Boxes => _boxes;
    public ReadOnlyMemory<StyledTextLine> Lines => _lines;
    public Vector2 ContentSize { get; }

    public StyledTextLayout(
        string text,
        ReadOnlySpan<StyledTextRange> ranges,
        ReadOnlySpan<StyledTextInlineBox> inlineBoxes = default,
        StyledTextLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new StyledTextLayoutOptions();
        ValidateOptions(options);
        ValidateUtf16(text);

        StyledTextRange[] retainedRanges = ranges.ToArray();
        int[] styleMap = BuildStyleMap(text, retainedRanges);
        StyledTextInlineBox[] retainedBoxes = inlineBoxes.ToArray();
        int[] boxMap = BuildBoxMap(text, retainedBoxes);
        var glyphs = new List<StyledTextGlyph>(Math.Max(1, text.Length));
        var boxes = new List<StyledTextPositionedBox>(retainedBoxes.Length);
        var lines = new List<StyledTextLine>(EstimateLineCount(text));
        float cursorY = 0.0f;
        float maximumWidth = 0.0f;
        float maximumBottom = 0.0f;

        int paragraphStart = 0;
        while (paragraphStart <= text.Length)
        {
            int newline = text.IndexOf('\n', paragraphStart);
            int paragraphEnd = newline >= 0 ? newline : text.Length;
            BidiParagraph paragraph = BidiParagraph.Resolve(
                text.AsSpan(paragraphStart, paragraphEnd - paragraphStart),
                options.BaseDirection);
            List<Candidate> candidates = ShapeParagraph(
                text,
                paragraphStart,
                paragraphEnd,
                paragraph,
                retainedRanges,
                styleMap,
                retainedBoxes,
                boxMap,
                options.ShapingOptions);

            if (candidates.Count == 0)
            {
                int styleIndex = ResolveEmptyParagraphStyle(styleMap, paragraphStart, text.Length);
                StyledTextStyle style = retainedRanges[styleIndex].Style;
                float ascent = GetAscent(style);
                float descent = GetDescent(style);
                float natural = ascent + descent;
                float lineHeight = ResolveLineHeight(natural, options);
                float baseline = cursorY + ascent;
                lines.Add(new StyledTextLine(
                    glyphs.Count,
                    0,
                    boxes.Count,
                    0,
                    cursorY,
                    baseline,
                    0.0f,
                    lineHeight,
                    true,
                    paragraphStart));
                maximumBottom = Math.Max(maximumBottom, baseline + descent);
                cursorY += lineHeight;
            }
            else
            {
                int candidateStart = 0;
                while (candidateStart < candidates.Count)
                {
                    int candidateEnd = FindLineEnd(candidates, candidateStart, options.MaxWidth);
                    bool paragraphFinal = candidateEnd == candidates.Count;
                    PlaceLine(
                        candidates,
                        candidateStart,
                        candidateEnd,
                        paragraph.ParagraphLevel,
                        paragraphFinal,
                        paragraphStart,
                        retainedBoxes,
                        retainedRanges,
                        options,
                        glyphs,
                        boxes,
                        lines,
                        ref cursorY,
                        ref maximumWidth,
                        ref maximumBottom);
                    candidateStart = candidateEnd;
                }
            }

            if (newline < 0) break;
            paragraphStart = newline + 1;
        }

        Text = text;
        Ranges = retainedRanges;
        _glyphs = glyphs.ToArray();
        _boxes = boxes.ToArray();
        _lines = lines.ToArray();
        ContentSize = new Vector2(
            float.IsInfinity(options.MaxWidth) ? maximumWidth : options.MaxWidth,
            Math.Max(cursorY, maximumBottom));
    }

    private readonly record struct Candidate(
        bool IsBox,
        int BoxIndex,
        TtfFont? Font,
        ushort GlyphIndex,
        int StyleIndex,
        int Cluster,
        uint CodePoint,
        sbyte BidiLevel,
        float Advance,
        float OffsetX,
        float OffsetY,
        float Ascent,
        float Descent,
        ShapingGlyphFlags Flags)
    {
        public bool IsWhitespace => !IsBox && CodePoint is ' ' or '\t' or '\u00A0';
    }

    private static List<Candidate> ShapeParagraph(
        string text,
        int paragraphStart,
        int paragraphEnd,
        BidiParagraph paragraph,
        StyledTextRange[] ranges,
        int[] styleMap,
        StyledTextInlineBox[] boxes,
        int[] boxMap,
        TextShapingOptions shapingOptions)
    {
        var candidates = new List<Candidate>(Math.Max(1, paragraphEnd - paragraphStart));
        for (int bidiRunIndex = 0; bidiRunIndex < paragraph.Runs.Length; bidiRunIndex++)
        {
            BidiRun bidiRun = paragraph.Runs[bidiRunIndex];
            int runStart = paragraphStart + bidiRun.Start;
            int runEnd = runStart + bidiRun.Length;
            int segmentStart = runStart;
            while (segmentStart < runEnd)
            {
                int boxIndex = boxMap[segmentStart];
                if (boxIndex >= 0)
                {
                    StyledTextInlineBox box = boxes[boxIndex];
                    candidates.Add(new Candidate(
                        true,
                        boxIndex,
                        null,
                        0,
                        styleMap[segmentStart],
                        segmentStart,
                        0xFFFC,
                        bidiRun.Level,
                        box.Width,
                        0.0f,
                        0.0f,
                        box.Ascent,
                        box.Descent,
                        ShapingGlyphFlags.None));
                    segmentStart++;
                    continue;
                }

                int styleIndex = styleMap[segmentStart];
                StyledTextStyle style = ranges[styleIndex].Style;
                TtfFont resolvedFont = ResolveFont(text, segmentStart, runEnd, style.Font, null, out int scalarLength);
                int fontRunStart = segmentStart;
                segmentStart += scalarLength;
                while (segmentStart < runEnd && boxMap[segmentStart] < 0 && styleMap[segmentStart] == styleIndex)
                {
                    TtfFont next = ResolveFont(
                        text,
                        segmentStart,
                        runEnd,
                        style.Font,
                        resolvedFont,
                        out scalarLength);
                    if (!ReferenceEquals(next, resolvedFont)) break;
                    segmentStart += scalarLength;
                }

                AppendShapedSegment(
                    candidates,
                    text,
                    fontRunStart,
                    segmentStart,
                    paragraphStart,
                    paragraphEnd,
                    bidiRun.Level,
                    styleIndex,
                    style,
                    resolvedFont,
                    shapingOptions);
            }
        }
        return candidates;
    }

    private static TtfFont ResolveFont(
        string text,
        int start,
        int end,
        TtfFont requested,
        TtfFont? previous,
        out int scalarLength)
    {
        char first = text[start];
        uint codePoint = first;
        scalarLength = 1;
        if (char.IsHighSurrogate(first) && start + 1 < end && char.IsLowSurrogate(text[start + 1]))
        {
            codePoint = checked((uint)char.ConvertToUtf32(first, text[start + 1]));
            scalarLength = 2;
        }

        if (requested.GetGlyphIndex(codePoint) != 0 || codePoint is ' ' or '\t')
        {
            return requested;
        }
        if (previous is not null && OpenTypeTextShaper.IsDefaultIgnorableCodePoint(codePoint))
        {
            return previous;
        }
        return TextLayout.TryResolveFallback(requested, codePoint, out TtfFont? fallback, out _) &&
               fallback is not null
            ? fallback
            : requested;
    }

    private static void AppendShapedSegment(
        List<Candidate> destination,
        string text,
        int start,
        int end,
        int paragraphStart,
        int paragraphEnd,
        sbyte bidiLevel,
        int styleIndex,
        StyledTextStyle style,
        TtfFont font,
        TextShapingOptions shapingOptions)
    {
        string segment = text.Substring(start, end - start);
        ShapingBufferFlags flags = shapingOptions.BufferFlags;
        if (start == paragraphStart) flags |= ShapingBufferFlags.BeginningOfText;
        if (end == paragraphEnd) flags |= ShapingBufferFlags.EndOfText;
        TextShapingOptions contextual = shapingOptions
            .WithBufferFlags(flags)
            .WithDirection((bidiLevel & 1) == 0
                ? ShapingDirection.LeftToRight
                : ShapingDirection.RightToLeft);
        IReadOnlyList<ShapedGlyph> shaped = OpenTypeTextShaper.Shape(
            segment,
            font,
            style.FontSize,
            contextual,
            text.AsMemory(paragraphStart, start - paragraphStart),
            text.AsMemory(end, paragraphEnd - end));
        float advanceScale = checked(style.WidthScale * style.TrackingFactor);
        float ascent = GetAscent(style, font);
        float descent = GetDescent(style, font);
        var run = new List<Candidate>(shaped.Count);
        for (int index = 0; index < shaped.Count; index++)
        {
            ShapedGlyph glyph = shaped[index];
            run.Add(new Candidate(
                false,
                -1,
                font,
                glyph.GlyphIndex,
                styleIndex,
                start + glyph.Cluster,
                glyph.CodePoint,
                bidiLevel,
                glyph.AdvanceX * advanceScale,
                glyph.OffsetX * style.WidthScale,
                glyph.OffsetY,
                ascent,
                descent,
                glyph.Flags));
        }
        AppendLogicalClusterOrder(destination, run, (bidiLevel & 1) != 0);
    }

    private static void AppendLogicalClusterOrder(
        List<Candidate> destination,
        List<Candidate> run,
        bool rightToLeft)
    {
        if (!rightToLeft || run.Count < 2)
        {
            destination.AddRange(run);
            return;
        }

        int groupEnd = run.Count;
        while (groupEnd > 0)
        {
            int groupStart = groupEnd - 1;
            int cluster = run[groupStart].Cluster;
            while (groupStart > 0 && run[groupStart - 1].Cluster == cluster) groupStart--;
            for (int index = groupStart; index < groupEnd; index++) destination.Add(run[index]);
            groupEnd = groupStart;
        }
    }

    private static int FindLineEnd(List<Candidate> candidates, int start, float maxWidth)
    {
        if (float.IsInfinity(maxWidth)) return candidates.Count;
        float width = 0.0f;
        int lastWhitespaceBreak = -1;
        int lastSafeBreak = -1;
        for (int index = start; index < candidates.Count; index++)
        {
            if (index > start && IsSafeBreakBefore(candidates, index)) lastSafeBreak = index;
            Candidate candidate = candidates[index];
            if (candidate.IsWhitespace && IsSafeBreakBefore(candidates, index + 1))
            {
                lastWhitespaceBreak = index + 1;
            }
            if (index > start && width + candidate.Advance > maxWidth)
            {
                if (lastWhitespaceBreak > start) return lastWhitespaceBreak;
                if (lastSafeBreak > start) return lastSafeBreak;
                int safe = index + 1;
                while (safe < candidates.Count && !IsSafeBreakBefore(candidates, safe)) safe++;
                return safe;
            }
            width += candidate.Advance;
        }
        return candidates.Count;
    }

    private static bool IsSafeBreakBefore(List<Candidate> candidates, int index)
    {
        if (index <= 0 || index >= candidates.Count) return true;
        if (candidates[index - 1].Cluster == candidates[index].Cluster) return false;
        return (candidates[index].Flags & ShapingGlyphFlags.UnsafeToBreak) == 0;
    }

    private static void PlaceLine(
        List<Candidate> logical,
        int start,
        int end,
        sbyte paragraphLevel,
        bool paragraphFinal,
        int paragraphStart,
        StyledTextInlineBox[] inlineBoxes,
        StyledTextRange[] ranges,
        StyledTextLayoutOptions options,
        List<StyledTextGlyph> glyphs,
        List<StyledTextPositionedBox> boxes,
        List<StyledTextLine> lines,
        ref float cursorY,
        ref float maximumWidth,
        ref float maximumBottom)
    {
        List<Candidate> visual = GetVisualCandidates(logical, start, end, paragraphLevel);
        float naturalWidth = 0.0f;
        float ascent = 0.0f;
        float descent = 0.0f;
        for (int index = 0; index < visual.Count; index++)
        {
            Candidate candidate = visual[index];
            naturalWidth += candidate.Advance;
            ascent = Math.Max(ascent, candidate.Ascent);
            descent = Math.Max(descent, candidate.Descent);
        }

        float lineHeight = ResolveLineHeight(ascent + descent, options);
        float baseline = cursorY + ascent;
        TextAlignment alignment = ranges[visual[0].StyleIndex].Style.Alignment;
        float available = float.IsInfinity(options.MaxWidth)
            ? naturalWidth
            : options.MaxWidth;
        float remaining = Math.Max(0.0f, available - naturalWidth);
        float cursorX = alignment switch
        {
            TextAlignment.Center => remaining * 0.5f,
            TextAlignment.Right => remaining,
            _ => 0.0f,
        };
        int expandableGapCount = alignment switch
        {
            TextAlignment.Justify when !paragraphFinal => CountWhitespaceGroups(visual),
            TextAlignment.Justify => 0,
            _ => 0,
        };
        float gapExtra = expandableGapCount > 0 ? remaining / expandableGapCount : 0.0f;
        bool insideWhitespace = false;
        int glyphOffset = glyphs.Count;
        int boxOffset = boxes.Count;

        for (int index = 0; index < visual.Count; index++)
        {
            Candidate candidate = visual[index];
            StyledTextStyle style = ranges[candidate.StyleIndex].Style;
            if (!candidate.IsWhitespace && insideWhitespace)
            {
                cursorX += gapExtra;
                insideWhitespace = false;
            }
            if (candidate.IsBox)
            {
                StyledTextInlineBox box = inlineBoxes[candidate.BoxIndex];
                boxes.Add(new StyledTextPositionedBox(
                    new Vector2(cursorX, baseline - box.Ascent),
                    box.Width,
                    box.Ascent + box.Descent,
                    box.Ascent,
                    box.TextPosition,
                    candidate.StyleIndex,
                    box.Tag));
            }
            else
            {
                glyphs.Add(new StyledTextGlyph(
                    candidate.GlyphIndex,
                    new Vector2(
                        cursorX + candidate.OffsetX,
                        baseline + candidate.OffsetY - style.BaselineShift),
                    candidate.Advance,
                    candidate.Font!,
                    candidate.StyleIndex,
                    candidate.Cluster,
                    candidate.BidiLevel,
                    candidate.CodePoint,
                    candidate.Flags));
            }

            cursorX += candidate.Advance;
            if (candidate.IsWhitespace)
            {
                insideWhitespace = true;
            }
        }

        // Trailing whitespace is still one expandable group for justified lines.
        if (insideWhitespace) cursorX += gapExtra;
        float recordedWidth = alignment == TextAlignment.Justify &&
                              !paragraphFinal &&
                              !float.IsInfinity(options.MaxWidth)
            ? available
            : Math.Max(naturalWidth, cursorX);
        maximumWidth = Math.Max(maximumWidth, recordedWidth);
        maximumBottom = Math.Max(maximumBottom, baseline + descent);
        lines.Add(new StyledTextLine(
            glyphOffset,
            glyphs.Count - glyphOffset,
            boxOffset,
            boxes.Count - boxOffset,
            cursorY,
            baseline,
            recordedWidth,
            lineHeight,
            paragraphFinal,
            paragraphStart));
        cursorY += lineHeight;
    }

    private static List<Candidate> GetVisualCandidates(
        List<Candidate> logical,
        int start,
        int end,
        sbyte paragraphLevel)
    {
        var starts = new List<int>();
        var levels = new List<sbyte>();
        for (int index = start; index < end;)
        {
            starts.Add(index);
            levels.Add(logical[index].BidiLevel);
            int cluster = logical[index].Cluster;
            index++;
            while (index < end && logical[index].Cluster == cluster) index++;
        }
        starts.Add(end);

        for (int group = levels.Count - 1; group >= 0; group--)
        {
            bool whitespace = true;
            for (int index = starts[group]; index < starts[group + 1]; index++)
            {
                whitespace &= logical[index].IsWhitespace;
            }
            if (!whitespace) break;
            levels[group] = paragraphLevel;
        }

        int[] order = BidiParagraph.GetVisualOrder(levels.ToArray());
        var result = new List<Candidate>(end - start);
        for (int visualIndex = 0; visualIndex < order.Length; visualIndex++)
        {
            int group = order[visualIndex];
            for (int index = starts[group]; index < starts[group + 1]; index++)
            {
                result.Add(logical[index]);
            }
        }
        return result;
    }

    private static int CountWhitespaceGroups(List<Candidate> candidates)
    {
        int count = 0;
        bool active = false;
        for (int index = 0; index < candidates.Count; index++)
        {
            if (candidates[index].IsWhitespace)
            {
                if (!active) count++;
                active = true;
            }
            else
            {
                active = false;
            }
        }
        return count;
    }

    private static float ResolveLineHeight(float natural, StyledTextLayoutOptions options)
    {
        if (options.MinimumLineSpacing <= 0.0f) return natural;
        return options.ExactLineSpacing
            ? options.MinimumLineSpacing
            : Math.Max(natural, options.MinimumLineSpacing);
    }

    private static float GetAscent(StyledTextStyle style) => GetAscent(style, style.Font);

    private static float GetAscent(StyledTextStyle style, TtfFont font) =>
        Math.Max(0.0f, font.Ascender * style.FontSize / font.UnitsPerEm + style.BaselineShift);

    private static float GetDescent(StyledTextStyle style) => GetDescent(style, style.Font);

    private static float GetDescent(StyledTextStyle style, TtfFont font) =>
        Math.Max(0.0f, -font.Descender * style.FontSize / font.UnitsPerEm - style.BaselineShift);

    private static int[] BuildStyleMap(string text, StyledTextRange[] ranges)
    {
        if (text.Length == 0)
        {
            if (ranges.Length != 1 || ranges[0].Start != 0 || ranges[0].Length != 0)
            {
                throw new ArgumentException("Empty styled text requires one zero-length default range.", nameof(ranges));
            }
            ValidateStyle(ranges[0].Style);
            return [];
        }
        if (ranges.Length == 0) throw new ArgumentException("Styled text requires at least one range.", nameof(ranges));

        var result = new int[text.Length];
        int expected = 0;
        for (int index = 0; index < ranges.Length; index++)
        {
            StyledTextRange range = ranges[index];
            if (range.Start != expected || range.Length <= 0 || range.Start > text.Length - range.Length)
            {
                throw new ArgumentException("Styled ranges must form one ordered, gap-free partition of the UTF-16 text.", nameof(ranges));
            }
            int rangeEnd = range.Start + range.Length;
            if ((range.Start > 0 && char.IsLowSurrogate(text[range.Start])) ||
                (rangeEnd < text.Length && char.IsLowSurrogate(text[rangeEnd])))
            {
                throw new ArgumentException("Styled range boundaries cannot split a UTF-16 surrogate pair.", nameof(ranges));
            }
            ValidateStyle(range.Style);
            result.AsSpan(range.Start, range.Length).Fill(index);
            expected += range.Length;
        }
        if (expected != text.Length)
        {
            throw new ArgumentException("Styled ranges must cover the complete UTF-16 text.", nameof(ranges));
        }
        return result;
    }

    private static int[] BuildBoxMap(string text, StyledTextInlineBox[] boxes)
    {
        var result = new int[text.Length];
        Array.Fill(result, -1);
        for (int index = 0; index < boxes.Length; index++)
        {
            StyledTextInlineBox box = boxes[index];
            if (box.TextPosition < 0 || box.TextPosition >= text.Length ||
                text[box.TextPosition] != '\uFFFC' || result[box.TextPosition] >= 0 ||
                !float.IsFinite(box.Width) || box.Width < 0.0f ||
                !float.IsFinite(box.Ascent) || box.Ascent < 0.0f ||
                !float.IsFinite(box.Descent) || box.Descent < 0.0f)
            {
                throw new ArgumentException("Inline boxes require a unique U+FFFC position and finite nonnegative metrics.", nameof(boxes));
            }
            result[box.TextPosition] = index;
        }

        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\uFFFC' && result[index] < 0)
            {
                throw new ArgumentException("Every U+FFFC source position requires an inline box.", nameof(boxes));
            }
        }
        return result;
    }

    private static int ResolveEmptyParagraphStyle(int[] styleMap, int paragraphStart, int textLength)
    {
        if (styleMap.Length == 0) return 0;
        return styleMap[Math.Min(paragraphStart, textLength - 1)];
    }

    private static void ValidateStyle(StyledTextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style.Font);
        if (!float.IsFinite(style.FontSize) || style.FontSize <= 0.0f ||
            !float.IsFinite(style.WidthScale) || style.WidthScale <= 0.0f ||
            !float.IsFinite(style.TrackingFactor) || style.TrackingFactor <= 0.0f ||
            !float.IsFinite(style.BaselineShift) ||
            style.Alignment is < TextAlignment.Left or > TextAlignment.Justify)
        {
            throw new ArgumentException("Styled text metrics and alignment must be finite, positive where required, and valid.", nameof(style));
        }
    }

    private static void ValidateOptions(StyledTextLayoutOptions options)
    {
        if ((!float.IsPositiveInfinity(options.MaxWidth) &&
             (!float.IsFinite(options.MaxWidth) || options.MaxWidth <= 0.0f)) ||
            !float.IsFinite(options.MinimumLineSpacing) || options.MinimumLineSpacing < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        ArgumentNullException.ThrowIfNull(options.ShapingOptions);
    }

    private static void ValidateUtf16(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    throw new ArgumentException("Styled text contains an unpaired UTF-16 surrogate.", nameof(text));
                index++;
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                throw new ArgumentException("Styled text contains an unpaired UTF-16 surrogate.", nameof(text));
            }
        }
    }

    private static int EstimateLineCount(string text)
    {
        int count = 1;
        for (int index = 0; index < text.Length; index++) if (text[index] == '\n') count++;
        return count;
    }
}
