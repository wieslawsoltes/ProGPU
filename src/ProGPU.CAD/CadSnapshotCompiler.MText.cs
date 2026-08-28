using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using System.Numerics;
using System.Text;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    private const float MTextStackScale = 0.7f;

    private readonly record struct MTextPaint(byte R, byte G, byte B, byte A);

    private readonly record struct MTextResolvedStyle(
        TtfFont Font,
        float FontSize,
        float WidthScale,
        float TrackingFactor,
        float SkewX,
        float BaselineShift,
        TextAlignment Alignment,
        MTextPaint Paint,
        CadMTextDecoration Decorations,
        bool IsSubstitution);

    private sealed record MTextStackLayout(
        CadMTextStackKind Kind,
        StyledTextLayout Upper,
        StyledTextLayout Lower,
        MTextResolvedStyle Style,
        float Width,
        float Ascent,
        float Descent,
        float Gap);

    private readonly record struct MTextColumnPlacement(
        int Index,
        float OffsetX,
        float OffsetY);

    /// <summary>
    /// Lowers ACadSharp's public MTEXT data contract into original ProGPU
    /// immutable streams. The source parser is bounded before shaping; layout is
    /// paragraph-wide and column placement is line-linear. Work is O(C + G + L)
    /// time and O(C + G + L) temporary storage for decoded code units C, glyphs
    /// G, and lines L. No mutable ACadSharp object is retained.
    /// </summary>
    private static CadEntityHeader CompileMText(
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
        List<CadMTextPrimitive> destination,
        List<CadMTextGlyphRun> retainedRuns,
        List<CadMTextRectangle> retainedBackgrounds,
        List<CadMTextRectangle> retainedDecorations,
        List<CadMTextStroke> retainedStrokes,
        List<ushort> retainedGlyphIndices,
        List<Vector2> retainedGlyphPositions,
        List<TtfFont> retainedFonts,
        Dictionary<TtfFont, int> retainedFontIndices,
        int retainedShxGlyphCount)
    {
        int retainedFontCountBefore = retainedFonts.Count;
        try
        {
            if (string.IsNullOrEmpty(mtext.Value))
            {
                throw new CadUnsupportedEntityException("MTEXT content is empty.");
            }
            if (mtext.Value.Length > options.MaxTextCodeUnitsPerEntity)
            {
                throw new CadSnapshotExpansionLimitException(
                    $"MTEXT path {FormatEntityPath(handle, mtext.Handle)} exceeds the configured per-entity limit of {options.MaxTextCodeUnitsPerEntity} UTF-16 code units.");
            }
            if (!double.IsFinite(mtext.Height) || mtext.Height <= 0.0 ||
                !double.IsFinite(mtext.RectangleWidth) || mtext.RectangleWidth < 0.0 ||
                !double.IsFinite(mtext.RectangleHeight) || mtext.RectangleHeight < 0.0 ||
                !double.IsFinite(mtext.LineSpacing) || mtext.LineSpacing is < 0.25 or > 4.0)
            {
                throw new ArgumentException(
                    "MTEXT height, reference rectangle, and line spacing must be finite and valid.");
            }
            if (mtext.DrawingDirection is DrawingDirectionType.TopToBottom or DrawingDirectionType.BottomToTop)
            {
                throw new CadUnsupportedEntityException(
                    "Vertical MTEXT drawing direction requires vertical shaping and glyph orientation.");
            }

            TextStyle cadStyle = mtext.Style;
            if (cadStyle.IsShapeFile ||
                cadStyle.Filename.EndsWith(".shx", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(cadStyle.BigFontFilename))
            {
                throw new CadUnsupportedEntityException(
                    "SHX and Big Font MTEXT require the retained multi-line SHX layout contract.");
            }
            if (cadStyle.Flags.HasFlag(StyleFlags.VerticalText))
            {
                throw new CadUnsupportedEntityException(
                    "Vertical TrueType STYLE requires vertical shaping and glyph-orientation lowering.");
            }

            ICadTextFontResolver resolver = options.TextFontResolver ??
                throw new CadUnsupportedEntityException(
                    "TrueType MTEXT requires a host text-font resolver.");
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
            var source = new StringBuilder(content.DecodedCodeUnitCount + 8);
            var ranges = new List<StyledTextRange>(content.Inlines.Length + 1);
            var resolvedStyles = new List<MTextResolvedStyle>(content.Inlines.Length + 1);
            var boxes = new List<StyledTextInlineBox>();
            var stacks = new List<MTextStackLayout>();
            var forcedColumnStarts = new HashSet<int>();
            var substitutionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ReadOnlySpan<CadMTextInline> inlines = content.Inlines.Span;
            for (int inlineIndex = 0; inlineIndex < inlines.Length; inlineIndex++)
            {
                CadMTextInline inline = inlines[inlineIndex];
                MTextResolvedStyle style = ResolveMTextStyle(
                    inline.Style,
                    mtext,
                    cadStyle,
                    entityStyle,
                    layerColor,
                    resolver);
                if (style.IsSubstitution)
                {
                    substitutionNames.Add(style.Font.FamilyName);
                }
                if (!IsSupportedMTextParagraph(inline.Style.Paragraph.RawPayload))
                {
                    throw new CadUnsupportedEntityException(
                        $"MTEXT paragraph indentation or tab formatting at source offset {inline.SourceOffset} requires typed tab-stop lowering.");
                }

                int start = source.Length;
                switch (inline.Kind)
                {
                    case CadMTextInlineKind.Text:
                        source.Append(inline.Text);
                        break;
                    case CadMTextInlineKind.ParagraphBreak:
                        source.Append('\n');
                        break;
                    case CadMTextInlineKind.ColumnBreak:
                        source.Append('\n');
                        forcedColumnStarts.Add(source.Length);
                        break;
                    case CadMTextInlineKind.Stack:
                        {
                            int tag = stacks.Count;
                            MTextStackLayout stack = CreateMTextStack(inline, style);
                            stacks.Add(stack);
                            source.Append('\uFFFC');
                            boxes.Add(new StyledTextInlineBox(
                                start,
                                stack.Width,
                                stack.Ascent,
                                stack.Descent,
                                tag));
                            break;
                        }
                    default:
                        throw new InvalidOperationException("Unknown parsed MTEXT inline kind.");
                }

                int length = source.Length - start;
                if (length > 0)
                {
                    int styleTag = resolvedStyles.Count;
                    resolvedStyles.Add(style);
                    ranges.Add(new StyledTextRange(
                        start,
                        length,
                        new StyledTextStyle(
                            style.Font,
                            style.FontSize,
                            style.WidthScale,
                            style.TrackingFactor,
                            style.BaselineShift,
                            style.Alignment,
                            styleTag)));
                }
            }

            if (source.Length == 0)
            {
                throw new CadUnsupportedEntityException("MTEXT contains no drawable content.");
            }
            EnsureMTextRangePartition(source.Length, ranges, resolvedStyles, mtext, cadStyle, entityStyle, layerColor, resolver);

            var layout = new StyledTextLayout(
                source.ToString(),
                ranges.ToArray(),
                boxes.ToArray(),
                new StyledTextLayoutOptions
                {
                    MaxWidth = columnWidth,
                    MinimumLineSpacing = checked((float)(mtext.Height * (5.0 / 3.0) * mtext.LineSpacing)),
                    ExactLineSpacing = mtext.LineSpacingStyle == LineSpacingStyleType.Exact,
                    BaseDirection = mtext.DrawingDirection == DrawingDirectionType.RightToLeft
                        ? ShapingDirection.RightToLeft
                        : ShapingDirection.LeftToRight,
                });

            StyledTextLine[] layoutLines = layout.Lines.ToArray();
            float placementWidth = float.IsInfinity(columnWidth)
                ? (layoutLines.Length == 0 ? 0.0f : layoutLines.Max(static line => line.Width))
                : columnWidth;
            if (!(placementWidth > 0.0f) || !float.IsFinite(placementWidth))
            {
                throw new CadUnsupportedEntityException("MTEXT has no finite positive layout width.");
            }
            MTextColumnPlacement[] linePlacements = PlaceMTextColumns(
                mtext,
                layoutLines,
                forcedColumnStarts,
                placementWidth,
                out int usedColumnCount,
                out float[] usedColumnHeights);
            float totalWidth = checked((usedColumnCount * placementWidth) +
                ((usedColumnCount - 1) * checked((float)mtext.ColumnData.Gutter)));
            float totalHeight = usedColumnHeights.Length == 0 ? 0.0f : usedColumnHeights.Max();
            Vector2 attachmentOffset = ResolveMTextAttachment(
                mtext.AttachmentPoint,
                totalWidth,
                totalHeight);

            StyledTextGlyph[] layoutGlyphs = layout.Glyphs.ToArray();
            StyledTextPositionedBox[] layoutBoxes = layout.Boxes.ToArray();
            var compiledIndices = new List<ushort>(layoutGlyphs.Length + (stacks.Count * 8));
            var compiledPositions = new List<Vector2>(layoutGlyphs.Length + (stacks.Count * 8));
            var compiledRuns = new List<CadMTextGlyphRun>(Math.Max(1, ranges.Count));
            var compiledBackgrounds = new List<CadMTextRectangle>();
            var compiledDecorations = new List<CadMTextRectangle>();
            var compiledStrokes = new List<CadMTextStroke>(stacks.Count);
            var lineByGlyph = BuildMTextLineMap(layoutGlyphs.Length, layoutLines);
            var lineByBox = BuildMTextBoxLineMap(layoutBoxes.Length, layoutLines);
            int retainedGlyphBase = retainedGlyphIndices.Count;

            for (int glyphIndex = 0; glyphIndex < layoutGlyphs.Length; glyphIndex++)
            {
                StyledTextGlyph glyph = layoutGlyphs[glyphIndex];
                MTextColumnPlacement placement = linePlacements[lineByGlyph[glyphIndex]];
                compiledIndices.Add(glyph.GlyphIndex);
                compiledPositions.Add(glyph.Position +
                    new Vector2(placement.OffsetX, placement.OffsetY) + attachmentOffset);
            }
            AppendMTextRuns(
                layoutGlyphs,
                ranges,
                resolvedStyles,
                retainedGlyphBase,
                compiledRuns,
                retainedFonts,
                retainedFontIndices);

            AppendMTextDecorations(
                layout,
                layoutLines,
                linePlacements,
                attachmentOffset,
                ranges,
                resolvedStyles,
                compiledDecorations);
            AppendMTextStacks(
                stacks,
                layoutBoxes,
                lineByBox,
                linePlacements,
                attachmentOffset,
                retainedGlyphBase,
                compiledIndices,
                compiledPositions,
                compiledRuns,
                compiledStrokes,
                retainedFonts,
                retainedFontIndices);
            AppendMTextBackgrounds(
                mtext,
                entityStyle,
                options.DrawingBackgroundColor,
                usedColumnCount,
                usedColumnHeights,
                placementWidth,
                attachmentOffset,
                compiledBackgrounds);

            if (compiledIndices.Count == 0 || compiledRuns.Count == 0)
            {
                throw new CadUnsupportedEntityException(
                    "MTEXT shaping produced no drawable glyph runs.");
            }
            if (compiledIndices.Count > options.MaxTextGlyphs -
                retainedGlyphIndices.Count - retainedShxGlyphCount)
            {
                throw new CadSnapshotExpansionLimitException(
                    $"Retained MTEXT glyph count exceeds the configured document limit of {options.MaxTextGlyphs}.");
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

            CadBounds3D bounds = ComputeMTextBounds(
                origin,
                xAxis,
                yAxis,
                compiledIndices,
                compiledPositions,
                compiledRuns,
                retainedGlyphBase,
                retainedFonts,
                compiledBackgrounds,
                compiledDecorations,
                compiledStrokes);
            if (bounds.IsEmpty)
            {
                throw new CadUnsupportedEntityException("MTEXT contains no renderable geometry.");
            }

            int glyphOffset = retainedGlyphIndices.Count;
            int runOffset = retainedRuns.Count;
            int backgroundOffset = retainedBackgrounds.Count;
            int decorationOffset = retainedDecorations.Count;
            int strokeOffset = retainedStrokes.Count;
            retainedGlyphIndices.AddRange(compiledIndices);
            retainedGlyphPositions.AddRange(compiledPositions);
            retainedRuns.AddRange(compiledRuns);
            retainedBackgrounds.AddRange(compiledBackgrounds);
            retainedDecorations.AddRange(compiledDecorations);
            retainedStrokes.AddRange(compiledStrokes);
            int primitiveIndex = destination.Count;
            destination.Add(new CadMTextPrimitive(
                origin,
                xAxis,
                yAxis,
                glyphOffset,
                compiledIndices.Count,
                runOffset,
                compiledRuns.Count,
                backgroundOffset,
                compiledBackgrounds.Count,
                decorationOffset,
                compiledDecorations.Count,
                strokeOffset,
                compiledStrokes.Count,
                usedColumnCount,
                totalWidth,
                totalHeight));

            if (substitutionNames.Count > 0)
            {
                AddDiagnostic(
                    diagnostics,
                    options.DiagnosticLimit,
                    new CadDiagnostic(
                        CadDiagnosticSeverity.Warning,
                        "CADSNAP006",
                        $"MTEXT path {FormatEntityPath(handle, mtext.Handle)} uses host font substitution: {string.Join(", ", substitutionNames.Order())}."));
            }

            return new CadEntityHeader(
                handle,
                CadEntityKind.MText,
                layerIndex,
                styleIndex,
                primitiveIndex,
                bounds);
        }
        catch
        {
            for (int index = retainedFonts.Count - 1; index >= retainedFontCountBefore; index--)
            {
                retainedFontIndices.Remove(retainedFonts[index]);
            }
            if (retainedFonts.Count > retainedFontCountBefore)
            {
                retainedFonts.RemoveRange(
                    retainedFontCountBefore,
                    retainedFonts.Count - retainedFontCountBefore);
            }
            throw;
        }
    }

    private static float ResolveMTextColumnWidth(MText mtext)
    {
        double width = mtext.HasColumns ? mtext.ColumnData.Width : mtext.RectangleWidth;
        if (!double.IsFinite(width) || (mtext.HasColumns && width <= 0.0) || width < 0.0)
        {
            throw new ArgumentException("MTEXT column width must be finite and positive when columns are enabled.");
        }
        return width > 0.0 ? checked((float)width) : float.PositiveInfinity;
    }

    private static MTextResolvedStyle ResolveMTextStyle(
        CadMTextRunStyle inline,
        MText mtext,
        TextStyle cadStyle,
        CadResolvedStyle entityStyle,
        ACadSharp.Color layerColor,
        ICadTextFontResolver resolver)
    {
        bool bold = inline.Font.IsSpecified
            ? inline.Font.IsBold
            : cadStyle.TrueType.HasFlag(FontFlags.Bold);
        bool italic = inline.Font.IsSpecified
            ? inline.Font.IsItalic
            : cadStyle.TrueType.HasFlag(FontFlags.Italic);
        string styleName = inline.Font.IsSpecified ? inline.Font.FamilyName : cadStyle.Name;
        string filename = inline.Font.IsSpecified ? inline.Font.FamilyName : cadStyle.Filename;
        CadTextFontResolution resolution = resolver.Resolve(new CadTextFontRequest(
            styleName,
            filename,
            inline.Font.IsSpecified ? string.Empty : cadStyle.BigFontFilename,
            bold,
            italic));
        TtfFont font = resolution.Font ?? throw new CadUnsupportedEntityException(
            $"MTEXT font '{filename}' could not resolve a TrueType font.");
        double fontSize = inline.Height.IsRelative
            ? mtext.Height * inline.Height.Value
            : inline.Height.Value;
        double width = inline.HasWidthFactorOverride ? inline.WidthFactor : cadStyle.Width;
        double oblique = inline.HasObliqueOverride
            ? inline.ObliqueDegrees * Math.PI / 180.0
            : cadStyle.ObliqueAngle;
        if (!double.IsFinite(fontSize) || fontSize <= 0.0 ||
            !double.IsFinite(width) || width <= 0.0 ||
            !double.IsFinite(inline.TrackingFactor) || inline.TrackingFactor <= 0.0 ||
            !double.IsFinite(oblique) || Math.Abs(oblique) >= Math.PI * 0.5)
        {
            throw new ArgumentException("MTEXT inline font metrics must be finite and non-degenerate.");
        }
        float size = checked((float)fontSize);
        float baselineShift = inline.VerticalAlignment switch
        {
            CadMTextVerticalAlignment.Center => size * 0.35f,
            CadMTextVerticalAlignment.Top => size * 0.7f,
            _ => 0.0f,
        };
        TextAlignment alignment = inline.Paragraph.Alignment switch
        {
            CadMTextParagraphAlignment.Center => TextAlignment.Center,
            CadMTextParagraphAlignment.Right => TextAlignment.Right,
            CadMTextParagraphAlignment.Justify or CadMTextParagraphAlignment.Distributed => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };
        ACadSharp.Color color = inline.Color.Kind switch
        {
            CadMTextColorKind.Indexed => new ACadSharp.Color(checked((short)inline.Color.Value)),
            CadMTextColorKind.TrueColor => ACadSharp.Color.FromTrueColor(inline.Color.Value),
            CadMTextColorKind.ByLayer => layerColor,
            _ => entityStyle.Color,
        };
        return new MTextResolvedStyle(
            font,
            size,
            checked((float)width),
            checked((float)inline.TrackingFactor),
            checked((float)Math.Tan(oblique)),
            baselineShift,
            alignment,
            new MTextPaint(color.R, color.G, color.B, ResolveAlpha(entityStyle.Transparency)),
            inline.Decorations,
            resolution.IsSubstitution);
    }

    private static byte ResolveAlpha(short transparency) =>
        transparency is < 0 or > 90
            ? byte.MaxValue
            : (byte)Math.Round(255.0 * (100.0 - transparency) / 100.0);

    private static bool IsSupportedMTextParagraph(string payload)
    {
        if (payload.Length == 0) return true;
        for (int index = 0; index < payload.Length; index++)
        {
            if (payload[index] is ',' or ' ') continue;
            if (payload[index] == 'q' && index + 1 < payload.Length &&
                char.ToLowerInvariant(payload[index + 1]) is 'l' or 'c' or 'r' or 'j' or 'd' or '*')
            {
                index++;
                continue;
            }
            return false;
        }
        return true;
    }

    private static void EnsureMTextRangePartition(
        int sourceLength,
        List<StyledTextRange> ranges,
        List<MTextResolvedStyle> styles,
        MText mtext,
        TextStyle cadStyle,
        CadResolvedStyle entityStyle,
        ACadSharp.Color layerColor,
        ICadTextFontResolver resolver)
    {
        if (ranges.Count > 0 && ranges[0].Start == 0 &&
            ranges[^1].Start + ranges[^1].Length == sourceLength)
        {
            return;
        }
        MTextResolvedStyle style = ResolveMTextStyle(
            CadMTextRunStyle.Default,
            mtext,
            cadStyle,
            entityStyle,
            layerColor,
            resolver);
        styles.Add(style);
        ranges.Add(new StyledTextRange(
            0,
            sourceLength,
            new StyledTextStyle(style.Font, style.FontSize, style.WidthScale,
                style.TrackingFactor, style.BaselineShift, style.Alignment, styles.Count - 1)));
    }

    private static MTextStackLayout CreateMTextStack(
        in CadMTextInline inline,
        in MTextResolvedStyle parent)
    {
        MTextResolvedStyle style = parent with
        {
            FontSize = parent.FontSize * MTextStackScale,
            BaselineShift = 0.0f,
            Decorations = CadMTextDecoration.None,
        };
        StyledTextStyle shapedStyle = new(
            style.Font,
            style.FontSize,
            style.WidthScale,
            style.TrackingFactor);
        var upper = new StyledTextLayout(
            inline.Text,
            [new StyledTextRange(0, inline.Text.Length, shapedStyle)]);
        var lower = new StyledTextLayout(
            inline.SecondaryText,
            [new StyledTextRange(0, inline.SecondaryText.Length, shapedStyle)]);
        float gap = Math.Max(parent.FontSize * 0.08f, 0.01f);
        float upperWidth = GetMTextLayoutWidth(upper);
        float lowerWidth = GetMTextLayoutWidth(lower);
        bool diagonal = inline.StackKind == CadMTextStackKind.Diagonal;
        float width = diagonal
            ? upperWidth + lowerWidth + gap
            : Math.Max(upperWidth, lowerWidth) + (gap * 2.0f);
        float height = diagonal
            ? Math.Max(upper.ContentSize.Y, lower.ContentSize.Y) + (parent.FontSize * 0.45f)
            : upper.ContentSize.Y + lower.ContentSize.Y + gap;
        return new MTextStackLayout(
            inline.StackKind,
            upper,
            lower,
            style,
            width,
            height * 0.7f,
            height * 0.3f,
            gap);
    }

    private static float GetMTextLayoutWidth(StyledTextLayout layout) =>
        layout.Lines.Length == 0 ? 0.0f : layout.Lines.Span.ToArray().Max(static line => line.Width);

    private static MTextColumnPlacement[] PlaceMTextColumns(
        MText mtext,
        StyledTextLine[] lines,
        HashSet<int> forcedStarts,
        float columnWidth,
        out int usedColumnCount,
        out float[] usedColumnHeights)
    {
        int capacity = mtext.HasColumns ? mtext.ColumnData.ColumnCount : 1;
        if (capacity <= 0)
        {
            throw new ArgumentException("MTEXT column count must be positive.");
        }
        double gutterValue = mtext.HasColumns ? mtext.ColumnData.Gutter : 0.0;
        if (!double.IsFinite(gutterValue) || gutterValue < 0.0)
        {
            throw new ArgumentException("MTEXT column gutter must be finite and nonnegative.");
        }
        float gutter = checked((float)gutterValue);
        float[] heightLimits = ResolveMTextColumnHeights(
            mtext,
            lines,
            forcedStarts,
            capacity);
        var result = new MTextColumnPlacement[lines.Length];
        var heights = new float[capacity];
        int column = 0;
        float columnTop = lines.Length > 0 ? lines[0].Top : 0.0f;
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            StyledTextLine line = lines[lineIndex];
            bool forced = lineIndex > 0 && forcedStarts.Contains(line.ParagraphStart);
            float localBottom = line.Top + line.Height - columnTop;
            if ((forced || localBottom > heightLimits[column] + 0.0001f) && lineIndex > 0)
            {
                column++;
                if (column >= capacity)
                {
                    throw new CadUnsupportedEntityException(
                        "MTEXT content exceeds its persisted column count and height contract.");
                }
                columnTop = line.Top;
                localBottom = line.Height;
            }
            if (localBottom > heightLimits[column] + 0.0001f)
            {
                throw new CadUnsupportedEntityException(
                    "One MTEXT line exceeds its persisted column height.");
            }
            int visualColumn = mtext.ColumnData.FlowReversed ? capacity - 1 - column : column;
            result[lineIndex] = new MTextColumnPlacement(
                visualColumn,
                visualColumn * (columnWidth + gutter),
                -columnTop);
            heights[visualColumn] = Math.Max(heights[visualColumn], localBottom);
        }
        usedColumnCount = mtext.ColumnData.FlowReversed ? capacity : Math.Max(1, column + 1);
        usedColumnHeights = mtext.ColumnData.FlowReversed
            ? heights
            : heights.AsSpan(0, usedColumnCount).ToArray();
        return result;
    }

    private static float[] ResolveMTextColumnHeights(
        MText mtext,
        StyledTextLine[] lines,
        HashSet<int> forcedStarts,
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
            float automatic = FindMTextAutomaticColumnHeight(lines, forcedStarts, count);
            Array.Fill(result, automatic);
            return result;
        }

        for (int index = 0; index < count; index++)
        {
            double value = index < mtext.ColumnData.Heights.Count
                ? mtext.ColumnData.Heights[index]
                : mtext.RectangleHeight;
            if (!double.IsFinite(value) || value <= 0.0)
            {
                throw new ArgumentException(
                    "MTEXT columns require finite positive persisted or automatic heights.");
            }
            result[index] = checked((float)value);
        }
        return result;
    }

    private static float FindMTextAutomaticColumnHeight(
        StyledTextLine[] lines,
        HashSet<int> forcedStarts,
        int capacity)
    {
        if (lines.Length == 0) return 0.01f;
        float lower = lines.Max(static line => line.Height);
        float upper = lines[^1].Top + lines[^1].Height - lines[0].Top;
        int forcedColumns = 1;
        for (int index = 1; index < lines.Length; index++)
        {
            if (forcedStarts.Contains(lines[index].ParagraphStart)) forcedColumns++;
        }
        if (forcedColumns > capacity)
        {
            throw new CadUnsupportedEntityException(
                "MTEXT explicit column breaks exceed the persisted column count.");
        }

        // Fixed iteration count keeps the auto-height search bounded and
        // deterministic while converging below retained float precision.
        for (int iteration = 0; iteration < 32; iteration++)
        {
            float candidate = lower + ((upper - lower) * 0.5f);
            if (CountMTextColumns(lines, forcedStarts, candidate) <= capacity)
                upper = candidate;
            else
                lower = candidate;
        }
        return Math.Max(upper, lower + Math.Max(1e-5f, upper * 1e-6f));
    }

    private static int CountMTextColumns(
        StyledTextLine[] lines,
        HashSet<int> forcedStarts,
        float height)
    {
        int columns = 1;
        float top = lines[0].Top;
        for (int index = 1; index < lines.Length; index++)
        {
            StyledTextLine line = lines[index];
            if (forcedStarts.Contains(line.ParagraphStart) ||
                line.Top + line.Height - top > height)
            {
                columns++;
                top = line.Top;
            }
        }
        return columns;
    }

    private static Vector2 ResolveMTextAttachment(
        AttachmentPointType attachment,
        float width,
        float height)
    {
        float x = attachment switch
        {
            AttachmentPointType.TopCenter or AttachmentPointType.MiddleCenter or AttachmentPointType.BottomCenter => -width * 0.5f,
            AttachmentPointType.TopRight or AttachmentPointType.MiddleRight or AttachmentPointType.BottomRight => -width,
            _ => 0.0f,
        };
        float y = attachment switch
        {
            AttachmentPointType.MiddleLeft or AttachmentPointType.MiddleCenter or AttachmentPointType.MiddleRight => -height * 0.5f,
            AttachmentPointType.BottomLeft or AttachmentPointType.BottomCenter or AttachmentPointType.BottomRight => -height,
            _ => 0.0f,
        };
        return new Vector2(x, y);
    }

    private static int[] BuildMTextLineMap(int count, StyledTextLine[] lines)
    {
        var result = new int[count];
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            StyledTextLine line = lines[lineIndex];
            result.AsSpan(line.GlyphOffset, line.GlyphCount).Fill(lineIndex);
        }
        return result;
    }

    private static int[] BuildMTextBoxLineMap(int count, StyledTextLine[] lines)
    {
        var result = new int[count];
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            StyledTextLine line = lines[lineIndex];
            result.AsSpan(line.BoxOffset, line.BoxCount).Fill(lineIndex);
        }
        return result;
    }

    private static void AppendMTextRuns(
        StyledTextGlyph[] glyphs,
        List<StyledTextRange> ranges,
        List<MTextResolvedStyle> styles,
        int glyphBase,
        List<CadMTextGlyphRun> destination,
        List<TtfFont> fonts,
        Dictionary<TtfFont, int> fontIndices)
    {
        int start = 0;
        while (start < glyphs.Length)
        {
            StyledTextGlyph first = glyphs[start];
            MTextResolvedStyle style = styles[ranges[first.StyleIndex].Style.Tag];
            int end = start + 1;
            while (end < glyphs.Length)
            {
                StyledTextGlyph next = glyphs[end];
                MTextResolvedStyle nextStyle = styles[ranges[next.StyleIndex].Style.Tag];
                if (!ReferenceEquals(next.Font, first.Font) ||
                    !HasSameMTextRunStyle(style, nextStyle)) break;
                end++;
            }
            destination.Add(CreateMTextRun(
                glyphBase + start,
                end - start,
                first.Font,
                style,
                fonts,
                fontIndices));
            start = end;
        }
    }

    private static bool HasSameMTextRunStyle(in MTextResolvedStyle left, in MTextResolvedStyle right) =>
        left.FontSize == right.FontSize &&
        left.WidthScale == right.WidthScale &&
        left.SkewX == right.SkewX &&
        left.Paint == right.Paint;

    private static CadMTextGlyphRun CreateMTextRun(
        int offset,
        int count,
        TtfFont font,
        in MTextResolvedStyle style,
        List<TtfFont> fonts,
        Dictionary<TtfFont, int> fontIndices) => new(
            offset,
            count,
            InternTextFont(font, fonts, fontIndices),
            style.FontSize,
            style.WidthScale,
            style.SkewX,
            style.Paint.R,
            style.Paint.G,
            style.Paint.B,
            style.Paint.A);

    private static void AppendMTextDecorations(
        StyledTextLayout layout,
        StyledTextLine[] lines,
        MTextColumnPlacement[] placements,
        Vector2 attachment,
        List<StyledTextRange> ranges,
        List<MTextResolvedStyle> styles,
        List<CadMTextRectangle> destination)
    {
        ReadOnlySpan<StyledTextGlyph> glyphs = layout.Glyphs.Span;
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            StyledTextLine line = lines[lineIndex];
            int end = line.GlyphOffset + line.GlyphCount;
            int start = line.GlyphOffset;
            while (start < end)
            {
                StyledTextGlyph first = glyphs[start];
                MTextResolvedStyle style = styles[ranges[first.StyleIndex].Style.Tag];
                int segmentEnd = start + 1;
                while (segmentEnd < end && glyphs[segmentEnd].StyleIndex == first.StyleIndex) segmentEnd++;
                if (style.Decorations != CadMTextDecoration.None)
                {
                    float minimum = float.PositiveInfinity;
                    float maximum = float.NegativeInfinity;
                    for (int index = start; index < segmentEnd; index++)
                    {
                        minimum = Math.Min(minimum, glyphs[index].Position.X);
                        maximum = Math.Max(maximum, glyphs[index].Position.X + glyphs[index].Advance);
                    }
                    MTextColumnPlacement placement = placements[lineIndex];
                    float x = minimum + placement.OffsetX + attachment.X;
                    float baseline = line.Baseline + placement.OffsetY + attachment.Y - style.BaselineShift;
                    float thickness = Math.Max(style.FontSize * 0.05f, 0.01f);
                    if (style.Decorations.HasFlag(CadMTextDecoration.Overline))
                        AddMTextRectangle(destination, x, baseline - (style.FontSize * 0.82f), maximum - minimum, thickness, style.Paint);
                    if (style.Decorations.HasFlag(CadMTextDecoration.StrikeThrough))
                        AddMTextRectangle(destination, x, baseline - (style.FontSize * 0.3f), maximum - minimum, thickness, style.Paint);
                    if (style.Decorations.HasFlag(CadMTextDecoration.Underline))
                        AddMTextRectangle(destination, x, baseline + (style.FontSize * 0.08f), maximum - minimum, thickness, style.Paint);
                }
                start = segmentEnd;
            }
        }
    }

    private static void AppendMTextStacks(
        List<MTextStackLayout> stacks,
        StyledTextPositionedBox[] boxes,
        int[] lineByBox,
        MTextColumnPlacement[] placements,
        Vector2 attachment,
        int retainedGlyphBase,
        List<ushort> indices,
        List<Vector2> positions,
        List<CadMTextGlyphRun> runs,
        List<CadMTextStroke> strokes,
        List<TtfFont> fonts,
        Dictionary<TtfFont, int> fontIndices)
    {
        for (int boxIndex = 0; boxIndex < boxes.Length; boxIndex++)
        {
            StyledTextPositionedBox box = boxes[boxIndex];
            MTextStackLayout stack = stacks[box.Tag];
            MTextColumnPlacement placement = placements[lineByBox[boxIndex]];
            Vector2 origin = box.Position + new Vector2(placement.OffsetX, placement.OffsetY) + attachment;
            float upperWidth = GetMTextLayoutWidth(stack.Upper);
            float lowerWidth = GetMTextLayoutWidth(stack.Lower);
            bool diagonal = stack.Kind == CadMTextStackKind.Diagonal;
            Vector2 upperOrigin = diagonal
                ? origin
                : origin + new Vector2((stack.Width - upperWidth) * 0.5f, 0.0f);
            Vector2 lowerOrigin = diagonal
                ? origin + new Vector2(upperWidth + stack.Gap, box.Height - stack.Lower.ContentSize.Y)
                : origin + new Vector2(
                    (stack.Width - lowerWidth) * 0.5f,
                    stack.Upper.ContentSize.Y + stack.Gap);
            AppendMTextStackGlyphs(stack.Upper, stack.Style, upperOrigin, retainedGlyphBase,
                indices, positions, runs, fonts, fontIndices);
            AppendMTextStackGlyphs(stack.Lower, stack.Style, lowerOrigin, retainedGlyphBase,
                indices, positions, runs, fonts, fontIndices);
            if (stack.Kind == CadMTextStackKind.Horizontal)
            {
                float y = origin.Y + stack.Upper.ContentSize.Y + (stack.Gap * 0.5f);
                strokes.Add(new CadMTextStroke(
                    origin.X,
                    y,
                    origin.X + stack.Width,
                    y,
                    Math.Max(stack.Style.FontSize * 0.06f, 0.01f),
                    stack.Style.Paint.R, stack.Style.Paint.G, stack.Style.Paint.B, stack.Style.Paint.A));
            }
            else if (diagonal)
            {
                strokes.Add(new CadMTextStroke(
                    origin.X + upperWidth,
                    origin.Y + box.Height * 0.75f,
                    origin.X + upperWidth + stack.Gap,
                    origin.Y + box.Height * 0.25f,
                    Math.Max(stack.Style.FontSize * 0.06f, 0.01f),
                    stack.Style.Paint.R, stack.Style.Paint.G, stack.Style.Paint.B, stack.Style.Paint.A));
            }
        }
    }

    private static void AppendMTextStackGlyphs(
        StyledTextLayout layout,
        MTextResolvedStyle style,
        Vector2 origin,
        int retainedGlyphBase,
        List<ushort> indices,
        List<Vector2> positions,
        List<CadMTextGlyphRun> runs,
        List<TtfFont> fonts,
        Dictionary<TtfFont, int> fontIndices)
    {
        ReadOnlySpan<StyledTextGlyph> glyphs = layout.Glyphs.Span;
        int start = 0;
        while (start < glyphs.Length)
        {
            TtfFont font = glyphs[start].Font;
            int runStart = indices.Count;
            int end = start;
            while (end < glyphs.Length && ReferenceEquals(glyphs[end].Font, font))
            {
                indices.Add(glyphs[end].GlyphIndex);
                positions.Add(glyphs[end].Position + origin);
                end++;
            }
            runs.Add(CreateMTextRun(
                retainedGlyphBase + runStart,
                end - start,
                font,
                style,
                fonts,
                fontIndices));
            start = end;
        }
    }

    private static void AppendMTextBackgrounds(
        MText mtext,
        CadResolvedStyle entityStyle,
        CadColor32 drawingBackground,
        int columnCount,
        float[] columnHeights,
        float columnWidth,
        Vector2 attachment,
        List<CadMTextRectangle> destination)
    {
        BackgroundFillFlags flags = mtext.BackgroundFillFlags;
        if (flags == BackgroundFillFlags.None) return;
        if (!double.IsFinite(mtext.BackgroundScale) || mtext.BackgroundScale is < 1.0 or > 5.0)
        {
            throw new ArgumentException("MTEXT background scale must be between 1 and 5.");
        }
        MTextPaint paint;
        if (flags.HasFlag(BackgroundFillFlags.UseDrawingWindowColor))
        {
            paint = new MTextPaint(
                drawingBackground.Red, drawingBackground.Green,
                drawingBackground.Blue, drawingBackground.Alpha);
        }
        else
        {
            ACadSharp.Color color = flags.HasFlag(BackgroundFillFlags.UseBackgroundFillColor)
                ? mtext.BackgroundColor
                : entityStyle.Color;
            short transparency = mtext.BackgroundTransparency.IsByLayer || mtext.BackgroundTransparency.IsByBlock
                ? (short)0
                : mtext.BackgroundTransparency.Value;
            paint = new MTextPaint(color.R, color.G, color.B, ResolveAlpha(transparency));
        }
        float gutter = checked((float)mtext.ColumnData.Gutter);
        float margin = checked((float)((mtext.BackgroundScale - 1.0) * mtext.Height * 0.5));
        float frame = Math.Max(checked((float)mtext.Height * 0.04f), 0.01f);
        for (int column = 0; column < columnCount; column++)
        {
            float contentHeight = columnHeights[Math.Min(column, columnHeights.Length - 1)];
            if (!(contentHeight > 0.0f)) continue;
            float x = attachment.X + (column * (columnWidth + gutter)) - margin;
            float y = attachment.Y - margin;
            float width = columnWidth + (margin * 2.0f);
            float height = contentHeight + (margin * 2.0f);
            if (flags.HasFlag(BackgroundFillFlags.UseBackgroundFillColor) ||
                flags.HasFlag(BackgroundFillFlags.UseDrawingWindowColor))
            {
                AddMTextRectangle(destination, x, y, width, height, paint);
            }
            if (flags.HasFlag(BackgroundFillFlags.TextFrame))
            {
                AddMTextRectangle(destination, x, y, width, frame, paint);
                AddMTextRectangle(destination, x, y + height - frame, width, frame, paint);
                AddMTextRectangle(destination, x, y, frame, height, paint);
                AddMTextRectangle(destination, x + width - frame, y, frame, height, paint);
            }
        }
    }

    private static void AddMTextRectangle(
        List<CadMTextRectangle> destination,
        float x,
        float y,
        float width,
        float height,
        in MTextPaint paint)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            !float.IsFinite(width) || width <= 0.0f ||
            !float.IsFinite(height) || height <= 0.0f)
        {
            throw new ArithmeticException("MTEXT rectangle exceeds the retained numeric range.");
        }
        destination.Add(new CadMTextRectangle(
            x, y, width, height, paint.R, paint.G, paint.B, paint.A));
    }

    private static CadCoordinateSystem CreateMTextBasis(MText mtext)
    {
        CadPoint3D normal = ToPoint(mtext.Normal).Normalize();
        CadPoint3D direction = ToPoint(mtext.AlignmentPoint);
        CadPoint3D projected = direction - (normal * CadPoint3D.Dot(direction, normal));
        CadPoint3D xAxis = projected.Length > 1e-12
            ? projected.Normalize()
            : CadCoordinateSystem.FromNormal(normal).XAxis;
        CadPoint3D yAxis = CadPoint3D.Cross(normal, xAxis).Normalize();
        return new CadCoordinateSystem(xAxis, yAxis, normal);
    }

    private static CadBounds3D ComputeMTextBounds(
        CadPoint3D origin,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        List<ushort> indices,
        List<Vector2> positions,
        List<CadMTextGlyphRun> runs,
        int retainedGlyphBase,
        List<TtfFont> fonts,
        List<CadMTextRectangle> backgrounds,
        List<CadMTextRectangle> decorations,
        List<CadMTextStroke> strokes)
    {
        CadBounds3D bounds = CadBounds3D.Empty;
        for (int runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            CadMTextGlyphRun run = runs[runIndex];
            TtfFont font = fonts[run.FontIndex];
            if (font.UnitsPerEm == 0) continue;
            double scale = run.FontSize / font.UnitsPerEm;
            int localStart = run.GlyphOffset - retainedGlyphBase;
            int localEnd = localStart + run.GlyphCount;
            for (int glyphIndex = localStart; glyphIndex < localEnd; glyphIndex++)
            {
                if (!font.TryGetGlyphBounds(indices[glyphIndex], out short xMin, out short yMin, out short xMax, out short yMax)) continue;
                Vector2 p = positions[glyphIndex];
                IncludeGlyphCorner(xMin, yMin);
                IncludeGlyphCorner(xMin, yMax);
                IncludeGlyphCorner(xMax, yMin);
                IncludeGlyphCorner(xMax, yMax);

                void IncludeGlyphCorner(short x, short y)
                {
                    double localX = p.X + ((x * run.WidthScale) + (y * run.SkewX)) * scale;
                    double localY = p.Y - (y * scale);
                    bounds = bounds.Include(TransformTextPoint(origin, xAxis, yAxis, localX, localY));
                }
            }
        }
        IncludeRectangles(backgrounds);
        IncludeRectangles(decorations);
        for (int index = 0; index < strokes.Count; index++)
        {
            CadMTextStroke stroke = strokes[index];
            double radius = stroke.Thickness * 0.5;
            IncludeLocal(stroke.StartX - radius, stroke.StartY - radius);
            IncludeLocal(stroke.StartX + radius, stroke.StartY + radius);
            IncludeLocal(stroke.EndX - radius, stroke.EndY - radius);
            IncludeLocal(stroke.EndX + radius, stroke.EndY + radius);
        }
        return bounds;

        void IncludeRectangles(List<CadMTextRectangle> rectangles)
        {
            for (int index = 0; index < rectangles.Count; index++)
            {
                CadMTextRectangle rectangle = rectangles[index];
                IncludeLocal(rectangle.X, rectangle.Y);
                IncludeLocal(rectangle.X + rectangle.Width, rectangle.Y);
                IncludeLocal(rectangle.X, rectangle.Y + rectangle.Height);
                IncludeLocal(rectangle.X + rectangle.Width, rectangle.Y + rectangle.Height);
            }
        }

        void IncludeLocal(double x, double y) =>
            bounds = bounds.Include(TransformTextPoint(origin, xAxis, yAxis, x, y));
    }
}
