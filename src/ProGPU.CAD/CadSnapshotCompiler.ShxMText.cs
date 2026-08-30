using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using System.Numerics;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    private readonly record struct ShxMTextResolvedStyle(
        CadShxGlyphCache Cache,
        CadShxGlyphCache? BigFontCache,
        string DrawingCodePage,
        float FontSize,
        float ScaleX,
        float ScaleY,
        float TrackingFactor,
        float SkewX,
        float BaselineShift,
        CadMTextParagraphAlignment Alignment,
        MTextPaint Paint,
        CadMTextDecoration Decorations,
        bool IsSubstitution,
        string ResolvedFontName);

    private readonly record struct ShxMTextCandidate(
        CadShxGlyph? Glyph,
        int StackIndex,
        int StyleIndex,
        CadMTextDecoration Decorations,
        bool IsWhitespace,
        float Width,
        float Ascent,
        float Descent)
    {
        public bool IsStack => StackIndex >= 0;
    }

    private sealed class ShxMTextParagraph(int styleIndex, bool forcedColumnStart)
    {
        public readonly List<ShxMTextCandidate> Candidates = new();
        public int StyleIndex = styleIndex;
        public bool ForcedColumnStart = forcedColumnStart;
    }

    private sealed record ShxMTextStackLayout(
        CadMTextStackKind Kind,
        CadShxTextLayout Upper,
        CadShxTextLayout Lower,
        int StyleIndex,
        float Width,
        float Ascent,
        float Descent,
        float UpperWidth,
        float LowerWidth,
        float UpperBaseline,
        float LowerBaseline,
        float Gap);

    private readonly record struct ShxMTextPlacedGlyph(
        CadShxGlyph Glyph,
        float X,
        float Y,
        int StyleIndex);

    private readonly record struct ShxMTextLine(
        bool ForcedColumnStart,
        int GlyphOffset,
        int GlyphCount,
        int DecorationOffset,
        int DecorationCount,
        int StrokeOffset,
        int StrokeCount,
        float Top,
        float Height);

    private struct ShxMTextDecorationAccumulator
    {
        public bool Active;
        public float Start;
        public float End;
        public float Y;
        public float Thickness;
        public MTextPaint Paint;
    }

    /// <summary>
    /// Lowers standard horizontal or top-to-bottom SHX MTEXT into retained
    /// analytic path placements. This is an original SHX specialization of ProGPU's
    /// in-repository styled MTEXT layout: parsing, formatting, wrapping, column
    /// flow, and replay remain separate from immutable cached glyph geometry.
    /// Work is O(C + G + L) time and O(C + G + L) temporary storage for source
    /// units C, SHX glyphs G, and retained lines L. Long unbroken words preserve
    /// the documented MTEXT overflow behavior instead of being split silently.
    /// </summary>
    private static CadEntityHeader CompileShxMText(
        MText mtext,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        CadResolvedStyle entityStyle,
        ACadSharp.Color layerColor,
        CadSnapshotOptions options,
        List<CadDiagnostic> diagnostics,
        List<CadShxMTextPrimitive> destination,
        List<CadShxMTextGlyphRun> retainedRuns,
        List<CadShxGlyphInstance> retainedGlyphs,
        List<CadMTextRectangle> retainedBackgrounds,
        List<CadMTextRectangle> retainedDecorations,
        List<CadMTextStroke> retainedStrokes,
        int retainedTrueTypeGlyphCount,
        ICadShxFontResolver? shxFontResolver,
        string drawingCodePage)
    {
        TextStyle cadStyle = mtext.Style;
        bool styleVertical = cadStyle.Flags.HasFlag(StyleFlags.VerticalText);
        bool isVertical = mtext.DrawingDirection switch
        {
            DrawingDirectionType.TopToBottom => true,
            DrawingDirectionType.ByStyle => styleVertical,
            DrawingDirectionType.LeftToRight => styleVertical,
            DrawingDirectionType.RightToLeft or
                DrawingDirectionType.BottomToTop => false,
            _ => throw new ArgumentException(
                $"SHX MTEXT drawing direction {(short)mtext.DrawingDirection} is not defined."),
        };
        if (mtext.DrawingDirection is DrawingDirectionType.RightToLeft or
            DrawingDirectionType.BottomToTop)
        {
            throw new CadUnsupportedEntityException(
                "SHX MTEXT supports left-to-right and top-to-bottom flow; right-to-left and bottom-to-top require dedicated layout contracts.");
        }
        CadShxOrientation orientation = isVertical
            ? CadShxOrientation.Vertical
            : CadShxOrientation.Horizontal;

        ICadShxFontResolver resolver = shxFontResolver ??
            throw new CadUnsupportedEntityException(
                "SHX MTEXT requires a host SHX font resolver.");
        CadMTextContent content;
        try
        {
            content = CadMTextParser.Parse(
                mtext.Value,
                new CadMTextParseOptions
                {
                    MaxDecodedCodeUnits = options.MaxTextCodeUnitsPerEntity,
                });
        }
        catch (NotSupportedException exception)
        {
            throw new CadUnsupportedEntityException(exception.Message);
        }

        float columnWidth = ResolveMTextColumnWidth(mtext);
        var styles = new List<ShxMTextResolvedStyle>(content.Inlines.Length + 4);
        var stacks = new List<ShxMTextStackLayout>();
        var substitutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int defaultStyleIndex = AddStyle(CadMTextRunStyle.Default);
        var paragraphs = new List<ShxMTextParagraph>
        {
            new(defaultStyleIndex, forcedColumnStart: false),
        };

        ReadOnlySpan<CadMTextInline> inlines = content.Inlines.Span;
        for (int inlineIndex = 0; inlineIndex < inlines.Length; inlineIndex++)
        {
            CadMTextInline inline = inlines[inlineIndex];
            if (!IsSupportedMTextParagraph(inline.Style.Paragraph.RawPayload))
            {
                throw new CadUnsupportedEntityException(
                    $"SHX MTEXT paragraph indentation or tab formatting at source offset {inline.SourceOffset} requires typed tab-stop lowering.");
            }
            int resolvedStyleIndex = AddStyle(inline.Style);
            ShxMTextParagraph paragraph = paragraphs[^1];
            if (paragraph.Candidates.Count == 0)
            {
                paragraph.StyleIndex = resolvedStyleIndex;
            }

            switch (inline.Kind)
            {
                case CadMTextInlineKind.Text:
                    if (inline.Text.IndexOf('\t') >= 0)
                    {
                        throw new CadUnsupportedEntityException(
                            $"SHX MTEXT tab characters at source offset {inline.SourceOffset} require typed tab-stop lowering.");
                    }
                    AppendText(inline.Text, resolvedStyleIndex, paragraph.Candidates);
                    break;
                case CadMTextInlineKind.Stack:
                    {
                        int stackIndex = CreateStack(inline, resolvedStyleIndex);
                        ShxMTextStackLayout stack = stacks[stackIndex];
                        paragraph.Candidates.Add(new ShxMTextCandidate(
                            null,
                            stackIndex,
                            stack.StyleIndex,
                            CadMTextDecoration.None,
                            false,
                            stack.Width,
                            stack.Ascent,
                            stack.Descent));
                        break;
                    }
                case CadMTextInlineKind.ParagraphBreak:
                    paragraphs.Add(new ShxMTextParagraph(
                        resolvedStyleIndex,
                        forcedColumnStart: false));
                    break;
                case CadMTextInlineKind.ColumnBreak:
                    paragraphs.Add(new ShxMTextParagraph(
                        resolvedStyleIndex,
                        forcedColumnStart: true));
                    break;
                default:
                    throw new InvalidOperationException("Unknown parsed SHX MTEXT inline kind.");
            }
        }

        float minimumLineSpacing = checked((float)(
            mtext.Height * (5.0 / 3.0) * mtext.LineSpacing));
        var glyphs = new List<ShxMTextPlacedGlyph>();
        var decorations = new List<CadMTextRectangle>();
        var strokes = new List<CadMTextStroke>();
        var lines = new List<ShxMTextLine>();
        float cursorY = 0.0f;
        float maximumLineWidth = 0.0f;
        for (int paragraphIndex = 0; paragraphIndex < paragraphs.Count; paragraphIndex++)
        {
            ShxMTextParagraph paragraph = paragraphs[paragraphIndex];
            if (paragraph.Candidates.Count == 0)
            {
                PlaceLine(paragraph, 0, 0, paragraphFinal: true);
                continue;
            }
            int candidateStart = 0;
            while (candidateStart < paragraph.Candidates.Count)
            {
                int candidateEnd = FindShxMTextLineEnd(
                    paragraph.Candidates,
                    candidateStart,
                    columnWidth);
                PlaceLine(
                    paragraph,
                    candidateStart,
                    candidateEnd,
                    candidateEnd == paragraph.Candidates.Count);
                candidateStart = candidateEnd;
            }
        }

        if (lines.Count == 0)
        {
            throw new CadUnsupportedEntityException("SHX MTEXT contains no layout lines.");
        }
        float placementWidth = float.IsPositiveInfinity(columnWidth)
            ? maximumLineWidth
            : columnWidth;
        ShxMTextColumnPlacement[] columnPlacements = PlaceShxMTextColumns(
            mtext,
            lines,
            placementWidth,
            out int usedColumnCount,
            out float[] usedColumnHeights);
        float totalWidth = checked((usedColumnCount * placementWidth) +
            ((usedColumnCount - 1) * checked((float)mtext.ColumnData.Gutter)));
        float totalHeight = usedColumnHeights.Length == 0
            ? 0.0f
            : usedColumnHeights.Max();
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            ShxMTextLine line = lines[lineIndex];
            ShxMTextColumnPlacement placement = columnPlacements[lineIndex];
            float shiftX = placement.OffsetX;
            float shiftY = placement.OffsetY;
            for (int index = line.GlyphOffset;
                 index < line.GlyphOffset + line.GlyphCount;
                 index++)
            {
                ShxMTextPlacedGlyph glyph = glyphs[index];
                glyphs[index] = glyph with
                {
                    X = glyph.X + shiftX,
                    Y = glyph.Y + shiftY,
                };
            }
            for (int index = line.DecorationOffset;
                 index < line.DecorationOffset + line.DecorationCount;
                 index++)
            {
                CadMTextRectangle rectangle = decorations[index];
                decorations[index] = rectangle with
                {
                    X = rectangle.X + shiftX,
                    Y = rectangle.Y + shiftY,
                };
            }
            for (int index = line.StrokeOffset;
                 index < line.StrokeOffset + line.StrokeCount;
                 index++)
            {
                CadMTextStroke stroke = strokes[index];
                strokes[index] = stroke with
                {
                    StartX = stroke.StartX + shiftX,
                    StartY = stroke.StartY + shiftY,
                    EndX = stroke.EndX + shiftX,
                    EndY = stroke.EndY + shiftY,
                };
            }
        }

        var backgrounds = new List<CadMTextRectangle>();
        AppendMTextBackgrounds(
            mtext,
            entityStyle,
            options.DrawingBackgroundColor,
            usedColumnCount,
            usedColumnHeights,
            placementWidth,
            Vector2.Zero,
            backgrounds);

        if (isVertical)
        {
            TransformShxMTextVertical(glyphs, decorations, strokes, backgrounds);
            (totalWidth, totalHeight) = (totalHeight, totalWidth);
        }
        Vector2 attachment = ResolveMTextAttachment(
            mtext.AttachmentPoint,
            totalWidth,
            totalHeight);
        if (isVertical)
        {
            // The logical map produces the physical block interval
            // [-width, 0]. Normalize it to the attachment resolver's [0, width]
            // content box before applying the requested anchor.
            attachment.X += totalWidth;
        }
        OffsetShxMTextGeometry(
            glyphs,
            decorations,
            strokes,
            backgrounds,
            attachment);

        if (glyphs.Count == 0)
        {
            throw new CadUnsupportedEntityException(
                "SHX MTEXT formatting produced no character placements.");
        }
        if (glyphs.Count > options.MaxTextGlyphs -
            retainedTrueTypeGlyphCount - retainedGlyphs.Count)
        {
            throw new CadSnapshotExpansionLimitException(
                $"Retained SHX MTEXT glyph count exceeds the configured document limit of {options.MaxTextGlyphs}.");
        }

        var runs = new List<CadShxMTextGlyphRun>();
        int retainedGlyphBase = retainedGlyphs.Count;
        int runStart = 0;
        while (runStart < glyphs.Count)
        {
            int runStyleIndex = glyphs[runStart].StyleIndex;
            int runEnd = runStart + 1;
            while (runEnd < glyphs.Count &&
                   glyphs[runEnd].StyleIndex == runStyleIndex)
            {
                runEnd++;
            }
            ShxMTextResolvedStyle style = styles[runStyleIndex];
            runs.Add(new CadShxMTextGlyphRun(
                retainedGlyphBase + runStart,
                runEnd - runStart,
                style.ScaleX,
                style.ScaleY,
                style.SkewX,
                style.Paint.R,
                style.Paint.G,
                style.Paint.B,
                style.Paint.A));
            runStart = runEnd;
        }

        CadCoordinateSystem entityBasis = CreateMTextBasis(mtext);
        CadPoint3D origin = ToPoint(mtext.InsertPoint);
        CadPoint3D xAxis = entityBasis.XAxis;
        CadPoint3D yAxis = entityBasis.YAxis * -1.0;
        if (hasTransform)
        {
            origin = transform.TransformPoint(origin);
            xAxis = transform.TransformVector(xAxis);
            yAxis = transform.TransformVector(yAxis);
        }
        EnsureFinite(origin);
        EnsureFinite(xAxis);
        EnsureFinite(yAxis);

        CadBounds3D bounds = ComputeShxMTextBounds(
            origin,
            xAxis,
            yAxis,
            glyphs,
            styles,
            backgrounds,
            decorations,
            strokes);
        if (bounds.IsEmpty)
        {
            throw new CadUnsupportedEntityException(
                "SHX MTEXT contains no renderable geometry.");
        }

        int glyphOffset = retainedGlyphs.Count;
        int retainedRunOffset = retainedRuns.Count;
        int backgroundOffset = retainedBackgrounds.Count;
        int decorationOffset = retainedDecorations.Count;
        int strokeOffset = retainedStrokes.Count;
        for (int index = 0; index < glyphs.Count; index++)
        {
            ShxMTextPlacedGlyph glyph = glyphs[index];
            retainedGlyphs.Add(new CadShxGlyphInstance(
                glyph.Glyph,
                glyph.X,
                glyph.Y));
        }
        retainedRuns.AddRange(runs);
        retainedBackgrounds.AddRange(backgrounds);
        retainedDecorations.AddRange(decorations);
        retainedStrokes.AddRange(strokes);

        int primitiveIndex = destination.Count;
        destination.Add(new CadShxMTextPrimitive(
            origin,
            xAxis,
            yAxis,
            glyphOffset,
            glyphs.Count,
            retainedRunOffset,
            runs.Count,
            backgroundOffset,
            backgrounds.Count,
            decorationOffset,
            decorations.Count,
            strokeOffset,
            strokes.Count,
            usedColumnCount,
            totalWidth,
            totalHeight));

        if (substitutions.Count > 0)
        {
            AddDiagnostic(
                diagnostics,
                options.DiagnosticLimit,
                new CadDiagnostic(
                    CadDiagnosticSeverity.Warning,
                    "CADSNAP006",
                    $"SHX MTEXT path {FormatEntityPath(handle, mtext.Handle)} uses host font substitution: {string.Join(", ", substitutions.Order())}."));
        }
        return new CadEntityHeader(
            handle,
            CadEntityKind.ShxMText,
            layerIndex,
            styleIndex,
            primitiveIndex,
            bounds);

        int AddStyle(in CadMTextRunStyle inline)
        {
            ShxMTextResolvedStyle style = ResolveShxMTextStyle(
                inline,
                mtext,
                cadStyle,
                entityStyle,
                layerColor,
                resolver,
                drawingCodePage,
                orientation);
            if (style.IsSubstitution)
            {
                substitutions.Add(style.ResolvedFontName);
            }
            styles.Add(style);
            return styles.Count - 1;
        }

        void AppendText(
            string text,
            int resolvedStyleIndex,
            List<ShxMTextCandidate> candidates)
        {
            if (text.Length == 0) return;
            ShxMTextResolvedStyle style = styles[resolvedStyleIndex];
            CadShxTextLayout layout;
            try
            {
                layout = new CadShxTextLayout(
                    text,
                    style.Cache,
                    orientation,
                    new CadShxTextLayoutOptions
                    {
                        MaxCodeUnits = options.MaxTextCodeUnitsPerEntity,
                        MaxGlyphs = options.MaxTextGlyphs,
                    },
                    style.BigFontCache,
                    style.DrawingCodePage);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or NotSupportedException or
                    KeyNotFoundException or ArgumentOutOfRangeException)
            {
                throw new CadUnsupportedEntityException(exception.Message);
            }

            ReadOnlySpan<CadShxGlyphPlacement> placements = layout.Glyphs.Span;
            for (int index = 0; index < placements.Length; index++)
            {
                CadShxGlyphPlacement placement = placements[index];
                ValidateShxMTextAdvance(
                    style.Cache.Font,
                    placement.Glyph,
                    orientation);
                float width = ResolveShxMTextInlineAdvance(
                    placement.Glyph,
                    style,
                    orientation);
                ResolveShxMTextCrossExtents(
                    placement.Glyph,
                    style,
                    orientation,
                    out float ascent,
                    out float descent);
                candidates.Add(new ShxMTextCandidate(
                    placement.Glyph,
                    -1,
                    resolvedStyleIndex,
                    style.Decorations | FromShxDecoration(placement.Decorations),
                    placement.IsBreakOpportunity,
                    width,
                    orientation == CadShxOrientation.Horizontal
                        ? Math.Max(0.0f, style.FontSize + style.BaselineShift)
                        : ascent,
                    orientation == CadShxOrientation.Horizontal
                        ? Math.Max(0.0f,
                            style.Cache.Font.Below * style.ScaleY - style.BaselineShift)
                        : descent));
            }
        }

        int CreateStack(in CadMTextInline inline, int parentStyleIndex)
        {
            ShxMTextResolvedStyle parent = styles[parentStyleIndex];
            ShxMTextResolvedStyle child = parent with
            {
                FontSize = parent.FontSize * MTextStackScale,
                ScaleX = parent.ScaleX * MTextStackScale,
                ScaleY = parent.ScaleY * MTextStackScale,
                BaselineShift = 0.0f,
                Decorations = CadMTextDecoration.None,
            };
            styles.Add(child);
            int childStyleIndex = styles.Count - 1;
            CadShxTextLayout upper;
            CadShxTextLayout lower;
            try
            {
                upper = new CadShxTextLayout(
                    inline.Text,
                    child.Cache,
                    orientation,
                    null,
                    child.BigFontCache,
                    child.DrawingCodePage);
                lower = new CadShxTextLayout(
                    inline.SecondaryText,
                    child.Cache,
                    orientation,
                    null,
                    child.BigFontCache,
                    child.DrawingCodePage);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or NotSupportedException or
                    KeyNotFoundException or ArgumentOutOfRangeException)
            {
                throw new CadUnsupportedEntityException(exception.Message);
            }
            ValidateLayoutAdvances(upper, child.Cache.Font, orientation);
            ValidateLayoutAdvances(lower, child.Cache.Font, orientation);
            float upperWidth = ResolveShxMTextLayoutAdvance(upper, child, orientation);
            float lowerWidth = ResolveShxMTextLayoutAdvance(lower, child, orientation);
            float childAscent = child.FontSize;
            float childDescent = child.Cache.Font.Below * child.ScaleY;
            float childHeight = childAscent + childDescent;
            float gap = Math.Max(parent.FontSize * 0.08f, 0.01f);
            bool diagonal = inline.StackKind == CadMTextStackKind.Diagonal;
            float width = diagonal
                ? upperWidth + lowerWidth + gap
                : Math.Max(upperWidth, lowerWidth) + (gap * 2.0f);
            float height = diagonal
                ? childHeight + (parent.FontSize * 0.45f)
                : (childHeight * 2.0f) + gap;
            float ascent = height * 0.7f;
            float descent = height - ascent;
            float upperBaseline = diagonal
                ? -ascent + childAscent
                : -ascent + childAscent;
            float lowerBaseline = diagonal
                ? descent - childDescent
                : -ascent + childHeight + gap + childAscent;
            stacks.Add(new ShxMTextStackLayout(
                inline.StackKind,
                upper,
                lower,
                childStyleIndex,
                width,
                ascent,
                descent,
                upperWidth,
                lowerWidth,
                upperBaseline,
                lowerBaseline,
                gap));
            return stacks.Count - 1;
        }

        void PlaceLine(
            ShxMTextParagraph paragraph,
            int start,
            int end,
            bool paragraphFinal)
        {
            float naturalWidth = 0.0f;
            float ascent = 0.0f;
            float descent = 0.0f;
            for (int index = start; index < end; index++)
            {
                ShxMTextCandidate candidate = paragraph.Candidates[index];
                naturalWidth += candidate.Width;
                ascent = Math.Max(ascent, candidate.Ascent);
                descent = Math.Max(descent, candidate.Descent);
            }
            if (start == end)
            {
                ShxMTextResolvedStyle emptyStyle = styles[paragraph.StyleIndex];
                ascent = emptyStyle.FontSize;
                descent = emptyStyle.Cache.Font.Below * emptyStyle.ScaleY;
            }
            float naturalHeight = ascent + descent;
            float lineHeight = mtext.LineSpacingStyle == LineSpacingStyleType.Exact
                ? minimumLineSpacing
                : Math.Max(naturalHeight, minimumLineSpacing);
            float baseline = cursorY + ascent;
            ShxMTextResolvedStyle paragraphStyle = styles[
                start < end
                    ? paragraph.Candidates[start].StyleIndex
                    : paragraph.StyleIndex];
            float available = float.IsPositiveInfinity(columnWidth)
                ? naturalWidth
                : columnWidth;
            float remaining = Math.Max(0.0f, available - naturalWidth);
            float x = paragraphStyle.Alignment switch
            {
                CadMTextParagraphAlignment.Center => remaining * 0.5f,
                CadMTextParagraphAlignment.Right => remaining,
                _ => 0.0f,
            };
            int whitespaceGroups = (paragraphStyle.Alignment is
                    CadMTextParagraphAlignment.Justify or CadMTextParagraphAlignment.Distributed) &&
                !paragraphFinal
                ? CountShxWhitespaceGroups(paragraph.Candidates, start, end)
                : 0;
            float gapExtra = whitespaceGroups > 0 ? remaining / whitespaceGroups : 0.0f;
            bool inWhitespace = false;
            int glyphOffset = glyphs.Count;
            int decorationOffset = decorations.Count;
            int strokeOffset = strokes.Count;
            var overline = new ShxMTextDecorationAccumulator();
            var underline = new ShxMTextDecorationAccumulator();
            var strikeThrough = new ShxMTextDecorationAccumulator();
            for (int index = start; index < end; index++)
            {
                ShxMTextCandidate candidate = paragraph.Candidates[index];
                if (!candidate.IsWhitespace && inWhitespace)
                {
                    x += gapExtra;
                    inWhitespace = false;
                }
                float candidateStart = x;
                if (candidate.IsStack)
                {
                    AppendStack(stacks[candidate.StackIndex], x, baseline);
                }
                else
                {
                    ShxMTextResolvedStyle candidateStyle = styles[candidate.StyleIndex];
                    glyphs.Add(new ShxMTextPlacedGlyph(
                        candidate.Glyph!,
                        x,
                        baseline - candidateStyle.BaselineShift,
                        candidate.StyleIndex));
                }
                x += candidate.Width;
                ShxMTextResolvedStyle style = styles[candidate.StyleIndex];
                UpdateDecoration(
                    ref overline,
                    candidate.Decorations.HasFlag(CadMTextDecoration.Overline),
                    candidateStart,
                    x,
                    baseline - style.BaselineShift - (style.FontSize * 0.82f),
                    style);
                UpdateDecoration(
                    ref strikeThrough,
                    candidate.Decorations.HasFlag(CadMTextDecoration.StrikeThrough),
                    candidateStart,
                    x,
                    baseline - style.BaselineShift - (style.FontSize * 0.3f),
                    style);
                UpdateDecoration(
                    ref underline,
                    candidate.Decorations.HasFlag(CadMTextDecoration.Underline),
                    candidateStart,
                    x,
                    baseline - style.BaselineShift + (style.FontSize * 0.08f),
                    style);
                if (candidate.IsWhitespace) inWhitespace = true;
            }
            if (inWhitespace) x += gapExtra;
            FlushDecoration(ref overline);
            FlushDecoration(ref strikeThrough);
            FlushDecoration(ref underline);
            float recordedWidth = whitespaceGroups > 0 &&
                                  !float.IsPositiveInfinity(columnWidth)
                ? available
                : Math.Max(naturalWidth, x);
            maximumLineWidth = Math.Max(maximumLineWidth, recordedWidth);
            lines.Add(new ShxMTextLine(
                paragraph.ForcedColumnStart,
                glyphOffset,
                glyphs.Count - glyphOffset,
                decorationOffset,
                decorations.Count - decorationOffset,
                strokeOffset,
                strokes.Count - strokeOffset,
                cursorY,
                lineHeight));
            cursorY += lineHeight;
        }

        void AppendStack(
            ShxMTextStackLayout stack,
            float x,
            float baseline)
        {
            bool diagonal = stack.Kind == CadMTextStackKind.Diagonal;
            float upperX = diagonal
                ? x
                : x + ((stack.Width - stack.UpperWidth) * 0.5f);
            float lowerX = diagonal
                ? x + stack.UpperWidth + stack.Gap
                : x + ((stack.Width - stack.LowerWidth) * 0.5f);
            AppendStackPart(
                stack.Upper,
                stack.StyleIndex,
                upperX,
                baseline + stack.UpperBaseline);
            AppendStackPart(
                stack.Lower,
                stack.StyleIndex,
                lowerX,
                baseline + stack.LowerBaseline);
            ShxMTextResolvedStyle style = styles[stack.StyleIndex];
            float thickness = Math.Max(style.FontSize * 0.06f, 0.01f);
            if (stack.Kind == CadMTextStackKind.Horizontal)
            {
                float y = baseline + ((stack.UpperBaseline + stack.LowerBaseline) * 0.5f);
                strokes.Add(new CadMTextStroke(
                        x,
                        y,
                        x + stack.Width,
                        y,
                        thickness,
                        style.Paint.R,
                        style.Paint.G,
                        style.Paint.B,
                        style.Paint.A));
            }
            else if (diagonal)
            {
                strokes.Add(new CadMTextStroke(
                        x + stack.UpperWidth,
                        baseline + stack.Descent * 0.5f,
                        x + stack.UpperWidth + stack.Gap,
                        baseline - stack.Ascent * 0.5f,
                        thickness,
                        style.Paint.R,
                        style.Paint.G,
                        style.Paint.B,
                        style.Paint.A));
            }
        }

        void AppendStackPart(
            CadShxTextLayout layout,
            int childStyleIndex,
            float x,
            float baseline)
        {
            ShxMTextResolvedStyle style = styles[childStyleIndex];
            ReadOnlySpan<CadShxGlyphPlacement> placements = layout.Glyphs.Span;
            for (int index = 0; index < placements.Length; index++)
            {
                CadShxGlyphPlacement placement = placements[index];
                glyphs.Add(new ShxMTextPlacedGlyph(
                    placement.Glyph,
                    x + (orientation == CadShxOrientation.Horizontal
                        ? placement.Origin.X * style.ScaleX * style.TrackingFactor
                        : -placement.Origin.Y * style.ScaleY * style.TrackingFactor),
                    baseline,
                    childStyleIndex));
            }
        }

        void UpdateDecoration(
            ref ShxMTextDecorationAccumulator accumulator,
            bool active,
            float start,
            float end,
            float y,
            in ShxMTextResolvedStyle style)
        {
            float thickness = Math.Max(style.FontSize * 0.05f, 0.01f);
            if (!active)
            {
                FlushDecoration(ref accumulator);
                return;
            }
            if (accumulator.Active &&
                accumulator.End == start &&
                accumulator.Y == y &&
                accumulator.Thickness == thickness &&
                accumulator.Paint == style.Paint)
            {
                accumulator.End = end;
                return;
            }
            FlushDecoration(ref accumulator);
            accumulator = new ShxMTextDecorationAccumulator
            {
                Active = true,
                Start = start,
                End = end,
                Y = y,
                Thickness = thickness,
                Paint = style.Paint,
            };
        }

        void FlushDecoration(ref ShxMTextDecorationAccumulator accumulator)
        {
            if (!accumulator.Active) return;
            decorations.Add(new CadMTextRectangle(
                    accumulator.Start,
                    accumulator.Y,
                    accumulator.End - accumulator.Start,
                    accumulator.Thickness,
                    accumulator.Paint.R,
                    accumulator.Paint.G,
                    accumulator.Paint.B,
                    accumulator.Paint.A));
            accumulator = default;
        }
    }

    /// <summary>
    /// Maps the formatter's logical inline/block coordinates to AutoCAD's
    /// top-to-bottom SHX plane: inline advances become downward Y advances and
    /// successive logical lines advance to the left. Ordinary and reversed
    /// MTEXT columns consequently advance below and above, respectively.
    /// </summary>
    private static void TransformShxMTextVertical(
        List<ShxMTextPlacedGlyph> glyphs,
        List<CadMTextRectangle> decorations,
        List<CadMTextStroke> strokes,
        List<CadMTextRectangle> backgrounds)
    {
        for (int index = 0; index < glyphs.Count; index++)
        {
            ShxMTextPlacedGlyph glyph = glyphs[index];
            glyphs[index] = glyph with { X = -glyph.Y, Y = glyph.X };
        }
        TransformRectangles(decorations);
        TransformRectangles(backgrounds);
        for (int index = 0; index < strokes.Count; index++)
        {
            CadMTextStroke stroke = strokes[index];
            strokes[index] = stroke with
            {
                StartX = -stroke.StartY,
                StartY = stroke.StartX,
                EndX = -stroke.EndY,
                EndY = stroke.EndX,
            };
        }

        static void TransformRectangles(List<CadMTextRectangle> rectangles)
        {
            for (int index = 0; index < rectangles.Count; index++)
            {
                CadMTextRectangle rectangle = rectangles[index];
                rectangles[index] = rectangle with
                {
                    X = -(rectangle.Y + rectangle.Height),
                    Y = rectangle.X,
                    Width = rectangle.Height,
                    Height = rectangle.Width,
                };
            }
        }
    }

    private static void OffsetShxMTextGeometry(
        List<ShxMTextPlacedGlyph> glyphs,
        List<CadMTextRectangle> decorations,
        List<CadMTextStroke> strokes,
        List<CadMTextRectangle> backgrounds,
        Vector2 offset)
    {
        for (int index = 0; index < glyphs.Count; index++)
        {
            ShxMTextPlacedGlyph glyph = glyphs[index];
            glyphs[index] = glyph with
            {
                X = glyph.X + offset.X,
                Y = glyph.Y + offset.Y,
            };
        }
        OffsetRectangles(decorations);
        OffsetRectangles(backgrounds);
        for (int index = 0; index < strokes.Count; index++)
        {
            CadMTextStroke stroke = strokes[index];
            strokes[index] = stroke with
            {
                StartX = stroke.StartX + offset.X,
                StartY = stroke.StartY + offset.Y,
                EndX = stroke.EndX + offset.X,
                EndY = stroke.EndY + offset.Y,
            };
        }

        void OffsetRectangles(List<CadMTextRectangle> rectangles)
        {
            for (int index = 0; index < rectangles.Count; index++)
            {
                CadMTextRectangle rectangle = rectangles[index];
                rectangles[index] = rectangle with
                {
                    X = rectangle.X + offset.X,
                    Y = rectangle.Y + offset.Y,
                };
            }
        }
    }

    private readonly record struct ShxMTextColumnPlacement(
        int ColumnIndex,
        float OffsetX,
        float OffsetY);

    private static ShxMTextResolvedStyle ResolveShxMTextStyle(
        in CadMTextRunStyle inline,
        MText mtext,
        TextStyle cadStyle,
        CadResolvedStyle entityStyle,
        ACadSharp.Color layerColor,
        ICadShxFontResolver resolver,
        string drawingCodePage,
        CadShxOrientation orientation)
    {
        if (inline.Font.IsSpecified && (inline.Font.IsBold || inline.Font.IsItalic))
        {
            throw new CadUnsupportedEntityException(
                $"SHX MTEXT font '{inline.Font.FamilyName}' cannot apply bold or italic formatting.");
        }
        if (inline.Font.IsSpecified &&
            !inline.Font.FamilyName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
        {
            throw new CadUnsupportedEntityException(
                $"SHX MTEXT inline font '{inline.Font.FamilyName}' requires mixed TrueType/SHX run lowering; SHX overrides must name an .shx font.");
        }
        string styleName = inline.Font.IsSpecified
            ? inline.Font.FamilyName
            : cadStyle.Name;
        string filename = inline.Font.IsSpecified
            ? inline.Font.FamilyName
            : cadStyle.Filename;
        string bigFontFilename = inline.Font.IsSpecified
            ? string.Empty
            : cadStyle.BigFontFilename;
        CadShxFontResolution resolution = resolver.Resolve(new CadShxFontRequest(
            styleName,
            filename,
            bigFontFilename));
        if (!string.IsNullOrWhiteSpace(bigFontFilename) &&
            resolution.BigFontGlyphCache is null)
        {
            throw new CadUnsupportedEntityException(
                $"Big Font MTEXT font '{bigFontFilename}' could not resolve an SHX Big Font.");
        }
        CadShxGlyphCache cache = resolution.GlyphCache ??
            throw new CadUnsupportedEntityException(
                $"SHX MTEXT font '{filename}' could not resolve an SHX font.");
        if (!cache.Font.IsTextFont || cache.Font.Above <= 0)
        {
            throw new CadUnsupportedEntityException(
                $"SHX MTEXT font '{filename}' is not a standard text font.");
        }
        if (orientation == CadShxOrientation.Vertical &&
            !cache.Font.SupportsVerticalOrientation)
        {
            throw new CadUnsupportedEntityException(
                $"Vertical SHX MTEXT font '{filename}' does not declare dual-orientation text programs.");
        }
        if (orientation == CadShxOrientation.Vertical &&
            resolution.BigFontGlyphCache is not null &&
            !resolution.BigFontGlyphCache.Font.SupportsVerticalOrientation)
        {
            throw new CadUnsupportedEntityException(
                $"Vertical SHX MTEXT Big Font '{bigFontFilename}' does not declare vertical text programs.");
        }

        double fontSize = inline.Height.IsRelative
            ? mtext.Height * inline.Height.Value
            : inline.Height.Value;
        double width = inline.HasWidthFactorOverride
            ? inline.WidthFactor
            : cadStyle.Width;
        double oblique = inline.HasObliqueOverride
            ? inline.ObliqueDegrees * Math.PI / 180.0
            : cadStyle.ObliqueAngle;
        if (!double.IsFinite(fontSize) || fontSize <= 0.0 ||
            !double.IsFinite(width) || width <= 0.0 ||
            !double.IsFinite(inline.TrackingFactor) || inline.TrackingFactor <= 0.0 ||
            !double.IsFinite(oblique) || Math.Abs(oblique) >= Math.PI * 0.5)
        {
            throw new ArgumentException(
                "SHX MTEXT inline metrics must be finite and non-degenerate.");
        }
        float size = checked((float)fontSize);
        float scaleY = checked((float)(fontSize / cache.Font.Above));
        float baselineShift = inline.VerticalAlignment switch
        {
            CadMTextVerticalAlignment.Center => size * 0.35f,
            CadMTextVerticalAlignment.Top => size * 0.7f,
            _ => 0.0f,
        };
        ACadSharp.Color color = inline.Color.Kind switch
        {
            CadMTextColorKind.Indexed => new ACadSharp.Color(checked((short)inline.Color.Value)),
            CadMTextColorKind.TrueColor => ACadSharp.Color.FromTrueColor(inline.Color.Value),
            CadMTextColorKind.ByLayer => layerColor,
            _ => entityStyle.Color,
        };
        return new ShxMTextResolvedStyle(
            cache,
            resolution.BigFontGlyphCache,
            drawingCodePage,
            size,
            checked(scaleY * (float)width),
            scaleY,
            checked((float)inline.TrackingFactor),
            checked((float)Math.Tan(oblique)),
            baselineShift,
            inline.Paragraph.Alignment,
            new MTextPaint(
                color.R,
                color.G,
                color.B,
                ResolveAlpha(entityStyle.Transparency)),
            inline.Decorations,
            resolution.IsSubstitution,
            string.IsNullOrEmpty(resolution.ResolvedBigFontName)
                ? resolution.ResolvedFontName
                : $"{resolution.ResolvedFontName}, {resolution.ResolvedBigFontName}");
    }

    private static CadMTextDecoration FromShxDecoration(
        CadShxTextDecoration decoration)
    {
        CadMTextDecoration result = CadMTextDecoration.None;
        if (decoration.HasFlag(CadShxTextDecoration.Overline))
            result |= CadMTextDecoration.Overline;
        if (decoration.HasFlag(CadShxTextDecoration.Underline))
            result |= CadMTextDecoration.Underline;
        if (decoration.HasFlag(CadShxTextDecoration.StrikeThrough))
            result |= CadMTextDecoration.StrikeThrough;
        return result;
    }

    private static void ValidateShxMTextAdvance(
        CadShxFont font,
        CadShxGlyph glyph,
        CadShxOrientation orientation)
    {
        if (orientation == CadShxOrientation.Horizontal)
        {
            if (Math.Abs(glyph.Advance.Y) >
                    Math.Max(1.0, Math.Abs(glyph.Advance.X)) * 1e-6 ||
                glyph.Advance.X < 0.0f)
            {
                throw new CadUnsupportedEntityException(
                    $"Horizontal SHX MTEXT requires nonnegative X-only character advances; font '{font.Name}' shape {glyph.ShapeNumber} produced ({glyph.Advance.X:R}, {glyph.Advance.Y:R}).");
            }
            return;
        }
        if (Math.Abs(glyph.Advance.X) >
                Math.Max(1.0, Math.Abs(glyph.Advance.Y)) * 1e-6 ||
            glyph.Advance.Y > 0.0f)
        {
            throw new CadUnsupportedEntityException(
                $"Vertical SHX MTEXT requires nonpositive Y-only character advances; font '{font.Name}' shape {glyph.ShapeNumber} produced ({glyph.Advance.X:R}, {glyph.Advance.Y:R}).");
        }
    }

    private static void ValidateLayoutAdvances(
        CadShxTextLayout layout,
        CadShxFont font,
        CadShxOrientation orientation)
    {
        ReadOnlySpan<CadShxGlyphPlacement> placements = layout.Glyphs.Span;
        for (int index = 0; index < placements.Length; index++)
            ValidateShxMTextAdvance(font, placements[index].Glyph, orientation);
    }

    private static float ResolveShxMTextInlineAdvance(
        CadShxGlyph glyph,
        in ShxMTextResolvedStyle style,
        CadShxOrientation orientation) =>
        orientation == CadShxOrientation.Horizontal
            ? checked(glyph.Advance.X * style.ScaleX * style.TrackingFactor)
            : checked(-glyph.Advance.Y * style.ScaleY * style.TrackingFactor);

    private static float ResolveShxMTextLayoutAdvance(
        CadShxTextLayout layout,
        in ShxMTextResolvedStyle style,
        CadShxOrientation orientation) =>
        orientation == CadShxOrientation.Horizontal
            ? checked(layout.Advance.X * style.ScaleX * style.TrackingFactor)
            : checked(-layout.Advance.Y * style.ScaleY * style.TrackingFactor);

    private static void ResolveShxMTextCrossExtents(
        CadShxGlyph glyph,
        in ShxMTextResolvedStyle style,
        CadShxOrientation orientation,
        out float ascent,
        out float descent)
    {
        if (orientation == CadShxOrientation.Horizontal)
        {
            ascent = Math.Max(0.0f, style.FontSize + style.BaselineShift);
            descent = Math.Max(
                0.0f,
                style.Cache.Font.Below * style.ScaleY - style.BaselineShift);
            return;
        }

        float minimum = 0.0f;
        float maximum = 0.0f;
        if (glyph.HasGeometry)
        {
            Span<Vector2> corners = stackalloc Vector2[4]
            {
                new(glyph.BoundsMin.X, glyph.BoundsMin.Y),
                new(glyph.BoundsMax.X, glyph.BoundsMin.Y),
                new(glyph.BoundsMax.X, glyph.BoundsMax.Y),
                new(glyph.BoundsMin.X, glyph.BoundsMax.Y),
            };
            for (int index = 0; index < corners.Length; index++)
            {
                Vector2 point = corners[index];
                float x = checked(
                    (point.X * style.ScaleX) +
                    (point.Y * style.ScaleY * style.SkewX));
                minimum = Math.Min(minimum, x);
                maximum = Math.Max(maximum, x);
            }
        }
        ascent = Math.Max(0.0f, maximum + style.BaselineShift);
        descent = Math.Max(0.0f, -minimum - style.BaselineShift);
    }

    private static int FindShxMTextLineEnd(
        List<ShxMTextCandidate> candidates,
        int start,
        float maximumWidth)
    {
        if (float.IsPositiveInfinity(maximumWidth)) return candidates.Count;
        float width = 0.0f;
        int lastWhitespaceBreak = -1;
        for (int index = start; index < candidates.Count; index++)
        {
            ShxMTextCandidate candidate = candidates[index];
            if (candidate.IsWhitespace) lastWhitespaceBreak = index + 1;
            if (index > start && width + candidate.Width > maximumWidth)
            {
                if (lastWhitespaceBreak > start) return lastWhitespaceBreak;
                int wordEnd = index;
                while (wordEnd < candidates.Count &&
                       !candidates[wordEnd].IsWhitespace)
                {
                    wordEnd++;
                }
                return Math.Max(start + 1, wordEnd);
            }
            width += candidate.Width;
        }
        return candidates.Count;
    }

    private static int CountShxWhitespaceGroups(
        List<ShxMTextCandidate> candidates,
        int start,
        int end)
    {
        int count = 0;
        bool active = false;
        for (int index = start; index < end; index++)
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

    private static ShxMTextColumnPlacement[] PlaceShxMTextColumns(
        MText mtext,
        List<ShxMTextLine> lines,
        float columnWidth,
        out int usedColumnCount,
        out float[] usedColumnHeights)
    {
        int capacity = mtext.HasColumns ? mtext.ColumnData.ColumnCount : 1;
        if (capacity <= 0)
            throw new ArgumentException("SHX MTEXT column count must be positive.");
        double gutterValue = mtext.HasColumns ? mtext.ColumnData.Gutter : 0.0;
        if (!double.IsFinite(gutterValue) || gutterValue < 0.0)
            throw new ArgumentException("SHX MTEXT column gutter must be finite and nonnegative.");
        float gutter = checked((float)gutterValue);
        float[] heightLimits = ResolveShxMTextColumnHeights(mtext, lines, capacity);
        var result = new ShxMTextColumnPlacement[lines.Count];
        var heights = new float[capacity];
        int column = 0;
        float columnTop = lines[0].Top;
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            ShxMTextLine line = lines[lineIndex];
            float localBottom = line.Top + line.Height - columnTop;
            bool forced = lineIndex > 0 && line.ForcedColumnStart;
            if ((forced || localBottom > heightLimits[column] + 0.0001f) &&
                lineIndex > 0)
            {
                column++;
                if (column >= capacity)
                    throw new CadUnsupportedEntityException(
                        "SHX MTEXT content exceeds its persisted column count and height contract.");
                columnTop = line.Top;
                localBottom = line.Height;
            }
            if (localBottom > heightLimits[column] + 0.0001f)
                throw new CadUnsupportedEntityException(
                    "One SHX MTEXT line exceeds its persisted column height.");
            int visualColumn = mtext.ColumnData.FlowReversed
                ? capacity - 1 - column
                : column;
            result[lineIndex] = new ShxMTextColumnPlacement(
                visualColumn,
                visualColumn * (columnWidth + gutter),
                -columnTop);
            heights[visualColumn] = Math.Max(heights[visualColumn], localBottom);
        }
        usedColumnCount = mtext.ColumnData.FlowReversed
            ? capacity
            : Math.Max(1, column + 1);
        usedColumnHeights = mtext.ColumnData.FlowReversed
            ? heights
            : heights.AsSpan(0, usedColumnCount).ToArray();
        return result;
    }

    private static float[] ResolveShxMTextColumnHeights(
        MText mtext,
        List<ShxMTextLine> lines,
        int count)
    {
        var result = new float[count];
        if (!mtext.HasColumns)
        {
            result[0] = float.PositiveInfinity;
            return result;
        }
        if (mtext.ColumnData.ColumnType == ColumnType.DynamicColumns &&
            mtext.ColumnData.AutoHeight)
        {
            float automatic = FindShxMTextAutomaticColumnHeight(lines, count);
            Array.Fill(result, automatic);
            return result;
        }
        for (int index = 0; index < count; index++)
        {
            double value = index < mtext.ColumnData.Heights.Count
                ? mtext.ColumnData.Heights[index]
                : mtext.RectangleHeight;
            if (!double.IsFinite(value) || value <= 0.0)
                throw new ArgumentException(
                    "SHX MTEXT columns require finite positive persisted or automatic heights.");
            result[index] = checked((float)value);
        }
        return result;
    }

    private static float FindShxMTextAutomaticColumnHeight(
        List<ShxMTextLine> lines,
        int capacity)
    {
        float lower = lines.Max(static line => line.Height);
        float upper = lines[^1].Top + lines[^1].Height - lines[0].Top;
        int forcedColumns = 1;
        for (int index = 1; index < lines.Count; index++)
            if (lines[index].ForcedColumnStart) forcedColumns++;
        if (forcedColumns > capacity)
            throw new CadUnsupportedEntityException(
                "SHX MTEXT explicit column breaks exceed the persisted column count.");
        for (int iteration = 0; iteration < 32; iteration++)
        {
            float candidate = lower + ((upper - lower) * 0.5f);
            if (CountShxMTextColumns(lines, candidate) <= capacity)
                upper = candidate;
            else
                lower = candidate;
        }
        return Math.Max(upper, lower + Math.Max(1e-5f, upper * 1e-6f));
    }

    private static int CountShxMTextColumns(
        List<ShxMTextLine> lines,
        float height)
    {
        int columns = 1;
        float top = lines[0].Top;
        for (int index = 1; index < lines.Count; index++)
        {
            ShxMTextLine line = lines[index];
            if (line.ForcedColumnStart || line.Top + line.Height - top > height)
            {
                columns++;
                top = line.Top;
            }
        }
        return columns;
    }

    private static CadBounds3D ComputeShxMTextBounds(
        CadPoint3D origin,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        List<ShxMTextPlacedGlyph> glyphs,
        List<ShxMTextResolvedStyle> styles,
        List<CadMTextRectangle> backgrounds,
        List<CadMTextRectangle> decorations,
        List<CadMTextStroke> strokes)
    {
        CadBounds3D bounds = CadBounds3D.Empty;
        for (int index = 0; index < glyphs.Count; index++)
        {
            ShxMTextPlacedGlyph glyph = glyphs[index];
            if (!glyph.Glyph.HasGeometry) continue;
            ShxMTextResolvedStyle style = styles[glyph.StyleIndex];
            Span<Vector2> corners = stackalloc Vector2[4]
            {
                new(glyph.Glyph.BoundsMin.X, glyph.Glyph.BoundsMin.Y),
                new(glyph.Glyph.BoundsMax.X, glyph.Glyph.BoundsMin.Y),
                new(glyph.Glyph.BoundsMax.X, glyph.Glyph.BoundsMax.Y),
                new(glyph.Glyph.BoundsMin.X, glyph.Glyph.BoundsMax.Y),
            };
            for (int corner = 0; corner < corners.Length; corner++)
            {
                Vector2 point = corners[corner];
                double localX = glyph.X + (point.X * style.ScaleX) +
                    (point.Y * style.ScaleY * style.SkewX);
                double localY = glyph.Y - (point.Y * style.ScaleY);
                bounds = bounds.Include(TransformTextPoint(
                    origin, xAxis, yAxis, localX, localY));
            }
        }
        IncludeRectangles(backgrounds);
        for (int index = 0; index < decorations.Count; index++)
            IncludeRectangle(decorations[index]);
        for (int index = 0; index < strokes.Count; index++)
        {
            CadMTextStroke stroke = strokes[index];
            double dx = stroke.EndX - stroke.StartX;
            double dy = stroke.EndY - stroke.StartY;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (!(length > 0.0) || !double.IsFinite(length)) continue;
            double halfX = -dy / length * stroke.Thickness * 0.5;
            double halfY = dx / length * stroke.Thickness * 0.5;
            bounds = bounds
                .Include(TransformTextPoint(origin, xAxis, yAxis,
                    stroke.StartX + halfX, stroke.StartY + halfY))
                .Include(TransformTextPoint(origin, xAxis, yAxis,
                    stroke.StartX - halfX, stroke.StartY - halfY))
                .Include(TransformTextPoint(origin, xAxis, yAxis,
                    stroke.EndX + halfX, stroke.EndY + halfY))
                .Include(TransformTextPoint(origin, xAxis, yAxis,
                    stroke.EndX - halfX, stroke.EndY - halfY));
        }
        return bounds;

        void IncludeRectangles(List<CadMTextRectangle> rectangles)
        {
            for (int index = 0; index < rectangles.Count; index++)
                IncludeRectangle(rectangles[index]);
        }

        void IncludeRectangle(in CadMTextRectangle rectangle)
        {
            bounds = bounds
                .Include(TransformTextPoint(origin, xAxis, yAxis,
                    rectangle.X, rectangle.Y))
                .Include(TransformTextPoint(origin, xAxis, yAxis,
                    rectangle.X + rectangle.Width, rectangle.Y))
                .Include(TransformTextPoint(origin, xAxis, yAxis,
                    rectangle.X + rectangle.Width, rectangle.Y + rectangle.Height))
                .Include(TransformTextPoint(origin, xAxis, yAxis,
                    rectangle.X, rectangle.Y + rectangle.Height));
        }
    }
}
