using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.XData;
using CSMath;
using ProGPU.Text;
using System.Numerics;
using System.Text;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    private readonly record struct CadToleranceContract(
        double ScaleFactor,
        double TextHeight,
        double Gap,
        TextStyle TextStyle,
        ACadSharp.Color FrameColor,
        ACadSharp.Color TextColor);

    private readonly record struct CadToleranceRun(string Text, bool IsGdtSymbol);

    private sealed class CadToleranceCell
    {
        public List<CadToleranceRun> Runs { get; } = new();
        public List<CadToleranceFragment> Fragments { get; } = new();
        public double ContentWidth { get; set; }
        public double Width { get; set; }
    }

    private sealed class CadToleranceRow
    {
        public List<CadToleranceCell> Cells { get; } = new();
        public double Width { get; set; }
    }

    private readonly record struct CadToleranceFragment(
        CadEntityHeader Header,
        double Advance);

    private static void ResolveToleranceStyles(
        Tolerance source,
        Layer effectiveLayer,
        in CadResolvedStyle entityStyle,
        CadSnapshotOptions options,
        out CadToleranceContract contract,
        out CadResolvedStyle frameStyle,
        out CadResolvedStyle textStyle)
    {
        DimensionStyle dimensionStyle = source.Style ?? throw new ArgumentException(
            "TOLERANCE has no dimension style.");
        double scaleFactor = dimensionStyle.ScaleFactor;
        double textHeight = dimensionStyle.Style.Height > 0.0
            ? dimensionStyle.Style.Height
            : dimensionStyle.TextHeight;
        double gap = dimensionStyle.DimensionLineGap;
        TextStyle fontStyle = dimensionStyle.Style;
        ACadSharp.Color frameColor = dimensionStyle.DimensionLineColor;
        ACadSharp.Color textColor = dimensionStyle.TextColor;

        if (source.ExtendedData.TryGet(AppId.DefaultName, out ExtendedData data) &&
            data.Records.Count != 0 &&
            data.Records[0] is ExtendedDataString header &&
            header.Value.Equals(DimensionStyle.StyleOverrideEntryName, StringComparison.Ordinal))
        {
            int index = 1;
            if (index >= data.Records.Count ||
                data.Records[index] is not ExtendedDataControlString { IsClosing: false })
            {
                throw new ArgumentException(
                    "TOLERANCE DSTYLE override has no opening control record.");
            }
            index++;
            bool closed = false;
            while (index < data.Records.Count)
            {
                if (data.Records[index] is ExtendedDataControlString { IsClosing: true })
                {
                    closed = true;
                    index++;
                    break;
                }
                if (index + 1 >= data.Records.Count ||
                    data.Records[index] is not ExtendedDataInteger16 code)
                {
                    throw new ArgumentException(
                        "TOLERANCE DSTYLE override must contain code/value pairs.");
                }

                ExtendedDataRecord value = data.Records[index + 1];
                switch (code.Value)
                {
                    case 40:
                        scaleFactor = ReadToleranceOverrideReal(code.Value, value);
                        break;
                    case 140:
                        textHeight = ReadToleranceOverrideReal(code.Value, value);
                        break;
                    case 147:
                        gap = ReadToleranceOverrideReal(code.Value, value);
                        break;
                    case 176:
                        frameColor = new ACadSharp.Color(
                            ReadToleranceOverrideInteger(code.Value, value));
                        break;
                    case 178:
                        textColor = new ACadSharp.Color(
                            ReadToleranceOverrideInteger(code.Value, value));
                        break;
                    case 340:
                        fontStyle = ResolveToleranceOverrideReference<TextStyle>(
                            source,
                            code.Value,
                            value) ?? throw new ArgumentException(
                                "TOLERANCE DIMTXSTY override resolves to no text style.");
                        break;
                }
                index += 2;
            }
            if (!closed || index != data.Records.Count)
            {
                throw new ArgumentException(
                    "TOLERANCE DSTYLE override has an invalid closing control record.");
            }
        }

        if (!double.IsFinite(scaleFactor) || scaleFactor < 0.0 ||
            !double.IsFinite(textHeight) || textHeight <= 0.0 ||
            !double.IsFinite(gap))
        {
            throw new ArgumentException(
                "TOLERANCE dimension scale, text height, and frame gap must be finite and valid.");
        }
        if (scaleFactor == 0.0)
        {
            throw new CadUnsupportedEntityException(
                "Annotative TOLERANCE requires a synchronized active annotation context.");
        }

        double effectiveHeight = textHeight * scaleFactor;
        double effectiveGap = Math.Abs(gap) * scaleFactor;
        if (!double.IsFinite(effectiveHeight) || effectiveHeight <= 0.0 ||
            !double.IsFinite(effectiveGap))
        {
            throw new ArithmeticException(
                "TOLERANCE scaled text height or frame gap exceeds the finite CAD range.");
        }

        frameColor = ResolveToleranceColor(
            frameColor,
            effectiveLayer,
            entityStyle,
            options);
        textColor = ResolveToleranceColor(
            textColor,
            effectiveLayer,
            entityStyle,
            options);
        frameStyle = entityStyle with
        {
            Color = frameColor,
            LineType = LineType.Continuous,
        };
        textStyle = entityStyle with
        {
            Color = textColor,
            LineType = LineType.Continuous,
        };
        contract = new CadToleranceContract(
            scaleFactor,
            effectiveHeight,
            effectiveGap,
            fontStyle,
            frameColor,
            textColor);
    }

    private static CadEntityHeader[] CompileTolerance(
        Tolerance source,
        ulong rootHandle,
        CadAffineTransform3D parentTransform,
        bool hasParentTransform,
        int layerIndex,
        int frameStyleIndex,
        int textStyleIndex,
        in CadToleranceContract contract,
        CadSnapshotOptions options,
        List<CadDiagnostic> diagnostics,
        List<CadTolerancePrimitive> tolerances,
        List<CadToleranceStroke> strokes,
        List<CadTextPrimitive> texts,
        List<CadTextGlyphRun> textGlyphRuns,
        List<CadTextDecoration> textDecorations,
        List<ushort> textGlyphIndices,
        List<Vector2> textGlyphPositions,
        List<TtfFont> textFonts,
        Dictionary<TtfFont, int> textFontIndices,
        List<CadShxTextPrimitive> shxTexts,
        List<CadShxGlyphInstance> shxGlyphInstances,
        List<CadShxDecorationSegment> shxDecorationSegments,
        ICadShxFontResolver? shxFontResolver,
        string drawingCodePage,
        ref int retainedCellCount)
    {
        List<CadToleranceRow> rows = ParseToleranceRows(source, options);
        int cellCount = rows.Sum(row => row.Cells.Count);
        if (cellCount > options.MaxToleranceCells - retainedCellCount)
        {
            throw new CadSnapshotExpansionLimitException(
                $"Retained TOLERANCE cells exceed the configured document limit of {options.MaxToleranceCells}.");
        }

        CadPoint3D normal = ToPoint(source.Normal);
        CadPoint3D direction = ToPoint(source.Direction);
        EnsureFinite(normal);
        EnsureFinite(direction);
        normal = normal.Normalize();
        direction = direction.Normalize();
        double perpendicularity = Math.Abs(CadPoint3D.Dot(normal, direction));
        if (perpendicularity > 1e-10)
        {
            throw new ArgumentException(
                "TOLERANCE direction must be perpendicular to its plane normal.");
        }
        CadPoint3D vertical = CadPoint3D.Cross(normal, direction).Normalize();
        var localTransform = new CadAffineTransform3D(
            direction,
            vertical,
            normal,
            ToPoint(source.InsertionPoint));
        CadAffineTransform3D worldTransform = hasParentTransform
            ? parentTransform.Compose(localTransform)
            : localTransform;
        EnsureFinite(worldTransform);

        int toleranceStart = tolerances.Count;
        int strokeStart = strokes.Count;
        int textStart = texts.Count;
        int runStart = textGlyphRuns.Count;
        int decorationStart = textDecorations.Count;
        int glyphStart = textGlyphIndices.Count;
        int fontStart = textFonts.Count;
        int shxTextStart = shxTexts.Count;
        int shxGlyphStart = shxGlyphInstances.Count;
        int shxDecorationStart = shxDecorationSegments.Count;
        int diagnosticStart = diagnostics.Count;
        try
        {
            TextStyle gdtStyle = CreateGdtTextStyle(contract.TextStyle);
            var textHeaders = new List<CadEntityHeader>();
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                CadToleranceRow row = rows[rowIndex];
                for (int cellIndex = 0; cellIndex < row.Cells.Count; cellIndex++)
                {
                    CadToleranceCell cell = row.Cells[cellIndex];
                    for (int runIndex = 0; runIndex < cell.Runs.Count; runIndex++)
                    {
                        CadToleranceRun run = cell.Runs[runIndex];
                        var text = new TextEntity
                        {
                            Value = run.Text,
                            Height = contract.TextHeight,
                            WidthFactor = contract.TextStyle.Width,
                            ObliqueAngle = contract.TextStyle.ObliqueAngle,
                            Mirror = contract.TextStyle.MirrorFlag,
                            Style = run.IsGdtSymbol ? gdtStyle : contract.TextStyle,
                            InsertPoint = XYZ.Zero,
                            AlignmentPoint = XYZ.Zero,
                            Normal = XYZ.AxisZ,
                            HorizontalAlignment = TextHorizontalAlignment.Left,
                            VerticalAlignment = TextVerticalAlignmentType.Middle,
                        };
                        CadEntityHeader header = CompileText(
                            text,
                            rootHandle,
                            worldTransform,
                            hasTransform: true,
                            layerIndex,
                            textStyleIndex,
                            options,
                            diagnostics,
                            texts,
                            textGlyphRuns,
                            textDecorations,
                            textGlyphIndices,
                            textGlyphPositions,
                            textFonts,
                            textFontIndices,
                            shxTexts,
                            shxGlyphInstances,
                            shxDecorationSegments,
                            shxFontResolver,
                            drawingCodePage,
                            out CadCompiledTextMetrics metrics);
                        if (!double.IsFinite(metrics.Advance) || metrics.Advance <= 0.0)
                        {
                            throw new CadUnsupportedEntityException(
                                "TOLERANCE text run produced no finite positive advance.");
                        }
                        cell.Fragments.Add(new CadToleranceFragment(header, metrics.Advance));
                        cell.ContentWidth += metrics.Advance;
                        textHeaders.Add(header);
                    }
                    cell.Width = Math.Max(
                        contract.TextHeight + (2.0 * contract.Gap),
                        cell.ContentWidth + (2.0 * contract.Gap));
                    row.Width += cell.Width;
                }
            }

            double frameWidth = rows.Max(row => row.Width);
            double rowHeight = contract.TextHeight + (2.0 * contract.Gap);
            if (!double.IsFinite(frameWidth) || frameWidth <= 0.0 ||
                !double.IsFinite(rowHeight) || rowHeight <= 0.0)
            {
                throw new ArithmeticException(
                    "TOLERANCE frame dimensions exceed the finite CAD range.");
            }

            int headerIndex = 0;
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                CadToleranceRow row = rows[rowIndex];
                row.Cells[^1].Width += frameWidth - row.Width;
                double rowCenterY = -rowIndex * rowHeight;
                double cellLeft = 0.0;
                for (int cellIndex = 0; cellIndex < row.Cells.Count; cellIndex++)
                {
                    CadToleranceCell cell = row.Cells[cellIndex];
                    double runX = cellLeft + ((cell.Width - cell.ContentWidth) * 0.5);
                    for (int fragmentIndex = 0;
                        fragmentIndex < cell.Fragments.Count;
                        fragmentIndex++)
                    {
                        CadToleranceFragment fragment = cell.Fragments[fragmentIndex];
                        CadPoint3D worldShift = worldTransform.TransformVector(
                            new CadPoint3D(runX, rowCenterY, 0.0));
                        CadEntityHeader shifted = TranslateTextFragment(
                            fragment.Header,
                            worldShift,
                            texts,
                            shxTexts);
                        cell.Fragments[fragmentIndex] = fragment with { Header = shifted };
                        textHeaders[headerIndex++] = shifted;
                        runX += fragment.Advance;
                    }
                    cellLeft += cell.Width;
                }
            }

            int requiredStrokes = checked(
                rows.Count + 3 + rows.Sum(row => row.Cells.Count - 1));
            if (requiredStrokes > options.MaxToleranceStrokes - strokes.Count)
            {
                throw new CadSnapshotExpansionLimitException(
                    $"Retained TOLERANCE frame strokes exceed the configured document limit of {options.MaxToleranceStrokes}.");
            }

            double top = rowHeight * 0.5;
            double bottom = -((rows.Count - 0.5) * rowHeight);
            AppendToleranceStroke(strokes, worldTransform, 0.0, top, frameWidth, top);
            for (int boundary = 1; boundary <= rows.Count; boundary++)
            {
                double y = top - (boundary * rowHeight);
                AppendToleranceStroke(strokes, worldTransform, 0.0, y, frameWidth, y);
            }
            AppendToleranceStroke(strokes, worldTransform, 0.0, top, 0.0, bottom);
            AppendToleranceStroke(
                strokes,
                worldTransform,
                frameWidth,
                top,
                frameWidth,
                bottom);
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                CadToleranceRow row = rows[rowIndex];
                double yTop = top - (rowIndex * rowHeight);
                double yBottom = yTop - rowHeight;
                double x = 0.0;
                for (int cellIndex = 0; cellIndex < row.Cells.Count - 1; cellIndex++)
                {
                    x += row.Cells[cellIndex].Width;
                    AppendToleranceStroke(strokes, worldTransform, x, yTop, x, yBottom);
                }
            }
            if (strokes.Count - strokeStart != requiredStrokes)
            {
                throw new InvalidOperationException(
                    "TOLERANCE frame stroke accounting is inconsistent.");
            }

            CadBounds3D frameBounds = CadBounds3D.Empty;
            for (int index = strokeStart; index < strokes.Count; index++)
            {
                frameBounds = frameBounds
                    .Include(strokes[index].Start)
                    .Include(strokes[index].End);
            }
            int primitiveIndex = tolerances.Count;
            tolerances.Add(new CadTolerancePrimitive(
                strokeStart,
                requiredStrokes,
                rows.Count,
                cellCount));
            retainedCellCount = checked(retainedCellCount + cellCount);
            var headers = new CadEntityHeader[textHeaders.Count + 1];
            headers[0] = new CadEntityHeader(
                rootHandle,
                CadEntityKind.Tolerance,
                layerIndex,
                frameStyleIndex,
                primitiveIndex,
                frameBounds);
            textHeaders.CopyTo(headers, 1);
            return headers;
        }
        catch
        {
            RemoveRange(tolerances, toleranceStart);
            RemoveRange(strokes, strokeStart);
            RemoveRange(texts, textStart);
            RemoveRange(textGlyphRuns, runStart);
            RemoveRange(textDecorations, decorationStart);
            RemoveRange(textGlyphIndices, glyphStart);
            RemoveRange(textGlyphPositions, glyphStart);
            if (textFonts.Count > fontStart)
            {
                for (int index = textFonts.Count - 1; index >= fontStart; index--)
                {
                    textFontIndices.Remove(textFonts[index]);
                }
                textFonts.RemoveRange(fontStart, textFonts.Count - fontStart);
            }
            RemoveRange(shxTexts, shxTextStart);
            RemoveRange(shxGlyphInstances, shxGlyphStart);
            RemoveRange(shxDecorationSegments, shxDecorationStart);
            RemoveRange(diagnostics, diagnosticStart);
            throw;
        }
    }

    private static List<CadToleranceRow> ParseToleranceRows(
        Tolerance source,
        CadSnapshotOptions options)
    {
        string value = source.Text ?? string.Empty;
        if (value.Length == 0)
        {
            throw new CadUnsupportedEntityException("TOLERANCE text is empty.");
        }
        if (value.Length > options.MaxTextCodeUnitsPerEntity)
        {
            throw new CadSnapshotExpansionLimitException(
                $"TOLERANCE path {FormatEntityPath(source.Handle, source.Handle)} exceeds the configured per-entity limit of {options.MaxTextCodeUnitsPerEntity} UTF-16 code units.");
        }
        EnsureValidUtf16(value);

        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        if (lines.Length == 0)
        {
            throw new CadUnsupportedEntityException(
                "TOLERANCE contains no feature-control-frame rows.");
        }

        var rows = new List<CadToleranceRow>(lines.Length);
        int cellCount = 0;
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            CadToleranceRow row = ParseToleranceRow(lines[lineIndex]);
            cellCount = checked(cellCount + row.Cells.Count);
            if (cellCount > options.MaxToleranceCellsPerEntity)
            {
                throw new CadSnapshotExpansionLimitException(
                    $"TOLERANCE cell count exceeds the configured per-entity limit of {options.MaxToleranceCellsPerEntity}.");
            }
            rows.Add(row);
        }
        return rows;
    }

    private static CadToleranceRow ParseToleranceRow(string source)
    {
        var row = new CadToleranceRow();
        var cell = new CadToleranceCell();
        var plain = new StringBuilder(source.Length);
        for (int index = 0; index < source.Length;)
        {
            if (index + 2 < source.Length && source[index] == '%' &&
                source[index + 1] == '%' &&
                char.ToLowerInvariant(source[index + 2]) == 'v')
            {
                FlushTolerancePlainRun(plain, cell);
                row.Cells.Add(cell);
                cell = new CadToleranceCell();
                index += 3;
                continue;
            }

            if (index + 8 < source.Length && source[index] == '{' &&
                source[index + 1] == '\\' &&
                source.AsSpan(index + 2, 5).Equals(
                    "Fgdt;".AsSpan(),
                    StringComparison.OrdinalIgnoreCase) &&
                source[index + 8] == '}')
            {
                FlushTolerancePlainRun(plain, cell);
                char symbol = MapGdtSymbol(source[index + 7]);
                cell.Runs.Add(new CadToleranceRun(symbol.ToString(), IsGdtSymbol: true));
                index += 9;
                continue;
            }

            if (index + 2 < source.Length && source[index] == '{' &&
                source[index + 1] == '\\' &&
                (source[index + 2] is 'F' or 'f'))
            {
                throw new CadUnsupportedEntityException(
                    "TOLERANCE contains an unsupported inline font token.");
            }

            plain.Append(source[index++]);
        }
        FlushTolerancePlainRun(plain, cell);
        row.Cells.Add(cell);
        return row;
    }

    private static void FlushTolerancePlainRun(
        StringBuilder plain,
        CadToleranceCell cell)
    {
        if (plain.Length == 0)
        {
            return;
        }
        string decoded = DecodeTextContent(plain.ToString()).Text;
        if (decoded.Length != 0)
        {
            cell.Runs.Add(new CadToleranceRun(decoded, IsGdtSymbol: false));
        }
        plain.Clear();
    }

    private static char MapGdtSymbol(char value) => char.ToLowerInvariant(value) switch
    {
        'j' => '\u2316',
        'r' => '\u25CE',
        'i' => '\u232F',
        'f' => '\u2225',
        'b' => '\u27C2',
        'a' => '\u2220',
        'g' => '\u232D',
        'c' => '\u23E5',
        'e' => '\u25CB',
        'u' => '\u23E4',
        'd' => '\u2313',
        'k' => '\u2312',
        'h' => '\u2197',
        't' => '\u2330',
        'n' => '\u2300',
        'm' => '\u24C2',
        'l' => '\u24C1',
        's' => '\u24C8',
        'p' => '\u24C5',
        _ => throw new CadUnsupportedEntityException(
            $"TOLERANCE contains unsupported GDT symbol code '{value}'."),
    };

    private static TextStyle CreateGdtTextStyle(TextStyle source) => new("$PROGPU_GDT")
    {
        Filename = "Noto Sans Symbols 2.ttf",
        Width = source.Width,
        ObliqueAngle = source.ObliqueAngle,
        MirrorFlag = source.MirrorFlag,
        TrueType = source.TrueType,
    };

    private static CadEntityHeader TranslateTextFragment(
        CadEntityHeader header,
        CadPoint3D shift,
        List<CadTextPrimitive> texts,
        List<CadShxTextPrimitive> shxTexts)
    {
        switch (header.Kind)
        {
            case CadEntityKind.Text:
                CadTextPrimitive text = texts[header.PrimitiveIndex];
                texts[header.PrimitiveIndex] = text with { Origin = text.Origin + shift };
                break;
            case CadEntityKind.ShxText:
                CadShxTextPrimitive shx = shxTexts[header.PrimitiveIndex];
                shxTexts[header.PrimitiveIndex] = shx with { Origin = shx.Origin + shift };
                break;
            default:
                throw new InvalidOperationException(
                    "TOLERANCE text fragment did not compile to a retained text primitive.");
        }

        return header with
        {
            Bounds = new CadBounds3D(
                header.Bounds.Min + shift,
                header.Bounds.Max + shift),
        };
    }

    private static void AppendToleranceStroke(
        List<CadToleranceStroke> destination,
        CadAffineTransform3D transform,
        double startX,
        double startY,
        double endX,
        double endY)
    {
        CadPoint3D start = transform.TransformPoint(
            new CadPoint3D(startX, startY, 0.0));
        CadPoint3D end = transform.TransformPoint(
            new CadPoint3D(endX, endY, 0.0));
        EnsureFinite(start);
        EnsureFinite(end);
        destination.Add(new CadToleranceStroke(start, end));
    }

    private static ACadSharp.Color ResolveToleranceColor(
        ACadSharp.Color authored,
        Layer effectiveLayer,
        in CadResolvedStyle entityStyle,
        CadSnapshotOptions options)
    {
        ACadSharp.Color color = authored.IsByLayer
            ? effectiveLayer.Color
            : authored.IsByBlock
                ? entityStyle.Color
                : authored;
        return ResolveBackgroundAdaptiveColor(color, options.DrawingBackgroundColor);
    }

    private static double ReadToleranceOverrideReal(
        short code,
        ExtendedDataRecord value) =>
        value is ExtendedDataReal real && double.IsFinite(real.Value)
            ? real.Value
            : throw new ArgumentException(
                $"TOLERANCE DSTYLE code {code} requires a finite real value.");

    private static short ReadToleranceOverrideInteger(
        short code,
        ExtendedDataRecord value) =>
        value is ExtendedDataInteger16 integer
            ? integer.Value
            : throw new ArgumentException(
                $"TOLERANCE DSTYLE code {code} requires a 16-bit integer value.");

    private static T? ResolveToleranceOverrideReference<T>(
        Tolerance source,
        short code,
        ExtendedDataRecord value)
        where T : CadObject
    {
        if (value is not ExtendedDataHandle handle)
        {
            throw new ArgumentException(
                $"TOLERANCE DSTYLE code {code} requires an object-handle value.");
        }
        if (handle.Value == 0)
        {
            return null;
        }
        if (source.Document is null ||
            !source.Document.TryGetCadObject(handle.Value, out T resolved))
        {
            throw new ArgumentException(
                $"TOLERANCE DSTYLE code {code} references unavailable handle {handle.Value:X}.");
        }
        return resolved;
    }

    private static void RemoveRange<T>(List<T> destination, int start)
    {
        if (destination.Count > start)
        {
            destination.RemoveRange(start, destination.Count - start);
        }
    }
}
