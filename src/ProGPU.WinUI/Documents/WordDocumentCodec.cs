using System.Globalization;
using System.Numerics;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using ProGPU.Vector;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>
/// Microsoft Word Open XML adapter for the shared rich-document model. Package parsing
/// and serialization are isolated from shaping, layout, rendering, and editing.
/// </summary>
public sealed class WordDocumentCodec : IRichDocumentFormatCodec
{
    private const long EmusPerLogicalPixel = 9_525L;
    private static readonly string[] Extensions = [".docx"];
    private static int s_nextDrawingId;

    public static WordDocumentCodec Default { get; } = new();
    public string FormatId => "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public IReadOnlyList<string> FileExtensions => Extensions;
    public bool CanImport => true;
    public bool CanExport => true;

    public RichDocument Import(ReadOnlySpan<byte> source, in RichDocumentImportContext context)
    {
        using var stream = new MemoryStream(source.ToArray(), writable: false);
        using WordprocessingDocument package = WordprocessingDocument.Open(stream, false);
        MainDocumentPart? mainPart = package.MainDocumentPart;
        W.Body? body = mainPart?.Document?.Body;
        if (mainPart is null || body is null) return CreateEmptyDocument();

        var spans = new List<RichTextSpan>();
        var paragraphs = new List<RichTextRtfCodec.ParagraphSpan>();
        RichTextStyle fallback = new(context.DefaultForeground, context.DefaultFontSize, context.DefaultFont);
        ApplyRunProperties(mainPart.StyleDefinitionsPart?.Styles?.DocDefaults?
            .RunPropertiesDefault?.RunPropertiesBaseStyle, ref fallback);
        RichParagraphFormatState defaultParagraph = new();
        ApplyParagraphProperties(mainPart.StyleDefinitionsPart?.Styles?.DocDefaults?
            .ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle, defaultParagraph);

        foreach (OpenXmlElement element in body.ChildElements)
        {
            switch (element)
            {
                case W.Paragraph paragraph:
                    AppendParagraph(mainPart, paragraph, spans, paragraphs, fallback, defaultParagraph, appendSeparator: true);
                    break;
                case W.Table table:
                    AppendTable(mainPart, table, spans, paragraphs, fallback, defaultParagraph);
                    break;
                case W.SdtBlock structured:
                    foreach (OpenXmlElement child in structured.SdtContentBlock?.ChildElements ?? [])
                    {
                        if (child is W.Paragraph childParagraph)
                            AppendParagraph(mainPart, childParagraph, spans, paragraphs, fallback, defaultParagraph, appendSeparator: true);
                        else if (child is W.Table childTable)
                            AppendTable(mainPart, childTable, spans, paragraphs, fallback, defaultParagraph);
                    }
                    break;
            }
        }

        TrimTerminalSeparator(spans, paragraphs);
        if (paragraphs.Count == 0)
            paragraphs.Add(new RichTextRtfCodec.ParagraphSpan(0, 0, defaultParagraph));
        return RtfDocumentCodec.BuildDocument(new RichTextRtfCodec.DecodedDocument
        {
            Spans = spans.ToArray(),
            Paragraphs = paragraphs.ToArray(),
            DocumentDefaultStyle = fallback,
            DocumentDefaultParagraphFormat = defaultParagraph
        });
    }

    public byte[] Export(RichDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        (RichTextSpan[] spans, RichTextRtfCodec.ParagraphSpan[] paragraphs) =
            RtfDocumentCodec.CollectRtfContent(document);
        using var stream = new MemoryStream();
        using (WordprocessingDocument package = WordprocessingDocument.Create(
                   stream,
                   WordprocessingDocumentType.Document,
                   autoSave: true))
        {
            MainDocumentPart mainPart = package.AddMainDocumentPart();
            mainPart.Document = new W.Document(new W.Body());
            AddStyles(mainPart);
            AddNumbering(mainPart, paragraphs);
            W.Body body = mainPart.Document.Body!;
            for (int index = 0; index < paragraphs.Length;)
            {
                if (paragraphs[index].IsTableRow)
                {
                    var table = new W.Table();
                    table.AppendChild(CreateTableProperties(paragraphs[index].Format));
                    if (paragraphs[index].TableCellRightEdges is { Length: > 0 } rightEdges)
                    {
                        var grid = new W.TableGrid();
                        float left = 0f;
                        foreach (float right in rightEdges)
                        {
                            grid.AppendChild(new W.GridColumn { Width = FormatTwips(Math.Max(1f, right - left)) });
                            left = right;
                        }
                        table.AppendChild(grid);
                    }
                    while (index < paragraphs.Length && paragraphs[index].IsTableRow)
                    {
                        table.AppendChild(CreateTableRow(mainPart, spans, paragraphs[index]));
                        index++;
                    }
                    body.AppendChild(table);
                    continue;
                }
                body.AppendChild(CreateParagraph(mainPart, spans, paragraphs[index]));
                index++;
            }
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static RichDocument CreateEmptyDocument()
    {
        var document = new RichDocument();
        document.Add(new Paragraph { MarginBottom = 0f });
        return document;
    }

    private static void AppendParagraph(
        MainDocumentPart mainPart,
        W.Paragraph paragraph,
        List<RichTextSpan> spans,
        List<RichTextRtfCodec.ParagraphSpan> paragraphs,
        RichTextStyle fallback,
        RichParagraphFormatState defaults,
        bool appendSeparator)
    {
        int start = GetTextLength(spans);
        RichParagraphFormatState format = defaults.Clone();
        ApplyParagraphStyle(mainPart, paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value, format, ref fallback);
        ApplyParagraphProperties(paragraph.ParagraphProperties, format);
        ApplyNumbering(mainPart, paragraph.ParagraphProperties, format);
        RichTextStyle paragraphStyle = fallback;
        ApplyRunProperties(paragraph.ParagraphProperties?.ParagraphMarkRunProperties, ref paragraphStyle);
        AppendParagraphContent(mainPart, paragraph.ChildElements, spans, paragraphStyle);
        int length = GetTextLength(spans) - start;
        paragraphs.Add(new RichTextRtfCodec.ParagraphSpan(start, length, format));
        if (appendSeparator) AppendSpan(spans, "\n", paragraphStyle);
    }

    private static void AppendParagraphContent(
        MainDocumentPart mainPart,
        IEnumerable<OpenXmlElement> children,
        List<RichTextSpan> spans,
        RichTextStyle inherited)
    {
        foreach (OpenXmlElement child in children)
        {
            switch (child)
            {
                case W.Run run:
                    AppendRun(mainPart, run, spans, inherited, null);
                    break;
                case W.Hyperlink hyperlink:
                    string? link = ResolveHyperlink(mainPart, hyperlink);
                    foreach (W.Run run in hyperlink.Elements<W.Run>())
                        AppendRun(mainPart, run, spans, inherited, link);
                    break;
                case W.SimpleField field:
                    AppendParagraphContent(mainPart, field.ChildElements, spans, inherited);
                    break;
                case W.SdtRun structured:
                    AppendParagraphContent(mainPart, structured.SdtContentRun?.ChildElements ?? [], spans, inherited);
                    break;
                case W.CustomXmlRun custom:
                    AppendParagraphContent(mainPart, custom.ChildElements, spans, inherited);
                    break;
            }
        }
    }

    private static void AppendRun(
        MainDocumentPart mainPart,
        W.Run run,
        List<RichTextSpan> spans,
        RichTextStyle inherited,
        string? hyperlink)
    {
        RichTextStyle style = inherited;
        ApplyRunStyle(mainPart, run.RunProperties?.RunStyle?.Val?.Value, ref style);
        ApplyRunProperties(run.RunProperties, ref style);
        if (!string.IsNullOrWhiteSpace(hyperlink)) style = style with { Link = hyperlink, IsUnderline = true };

        foreach (OpenXmlElement content in run.ChildElements)
        {
            switch (content)
            {
                case W.Text text:
                    AppendSpan(spans, text.Text, style);
                    break;
                case W.TabChar:
                    AppendSpan(spans, "\t", style);
                    break;
                case W.Break:
                case W.CarriageReturn:
                    AppendSpan(spans, "\n", style);
                    break;
                case W.NoBreakHyphen:
                    AppendSpan(spans, "\u2011", style);
                    break;
                case W.SoftHyphen:
                    AppendSpan(spans, "\u00AD", style);
                    break;
                case W.SymbolChar symbol:
                    if (int.TryParse(symbol.Char?.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        AppendSpan(spans, char.ConvertFromUtf32(code), style);
                    break;
                case W.Drawing drawing:
                    if (TryReadEmbeddedImage(mainPart, drawing, out RichTextEmbeddedObject? embedded))
                        AppendSpan(spans, "\uFFFC", style with { EmbeddedObject = embedded });
                    break;
            }
        }
    }

    private static void AppendTable(
        MainDocumentPart mainPart,
        W.Table source,
        List<RichTextSpan> spans,
        List<RichTextRtfCodec.ParagraphSpan> paragraphs,
        RichTextStyle fallback,
        RichParagraphFormatState defaults)
    {
        float[] widths = ReadTableWidths(source);
        float cellPadding = ReadCellPadding(source.TableProperties);
        float borderThickness = ReadBorderThickness(source.TableProperties);
        Brush? borderBrush = ReadBorderBrush(source.TableProperties);
        foreach (W.TableRow sourceRow in source.Elements<W.TableRow>())
        {
            int start = GetTextLength(spans);
            var backgrounds = new List<Brush?>();
            var columnSpans = new List<int>();
            var mergeFlags = new List<byte>();
            bool firstCell = true;
            foreach (W.TableCell sourceCell in sourceRow.Elements<W.TableCell>())
            {
                if (!firstCell) AppendSpan(spans, "\t", fallback);
                firstCell = false;
                backgrounds.Add(ParseBrush(sourceCell.TableCellProperties?.Shading?.Fill?.Value));
                columnSpans.Add(Math.Max(1, sourceCell.TableCellProperties?.GridSpan?.Val?.Value ?? 1));
                mergeFlags.Add(ReadVerticalMerge(sourceCell.TableCellProperties?.VerticalMerge));
                bool firstParagraph = true;
                foreach (W.Paragraph cellParagraph in sourceCell.Elements<W.Paragraph>())
                {
                    if (!firstParagraph) AppendSpan(spans, "\n", fallback);
                    firstParagraph = false;
                    RichTextStyle paragraphStyle = fallback;
                    ApplyRunProperties(cellParagraph.ParagraphProperties?.ParagraphMarkRunProperties, ref paragraphStyle);
                    AppendParagraphContent(mainPart, cellParagraph.ChildElements, spans, paragraphStyle);
                }
            }
            int length = GetTextLength(spans) - start;
            var format = defaults.Clone();
            format.IsTableRow = true;
            format.TableCellRightEdges = widths;
            format.TableCellPadding = cellPadding;
            format.TableBorderThickness = borderThickness;
            format.TableBorderBrush = borderBrush;
            format.TableCellBackgrounds = backgrounds.ToArray();
            format.TableCellColumnSpans = columnSpans.ToArray();
            format.TableCellVerticalMergeFlags = mergeFlags.ToArray();
            format.RightToLeft = source.TableProperties?.BiDiVisual is null
                ? FormatEffect.Undefined
                : FormatEffect.On;
            paragraphs.Add(new RichTextRtfCodec.ParagraphSpan(start, length, format)
            {
                IsTableRow = true,
                TableCellRightEdges = widths
            });
            AppendSpan(spans, "\n", fallback);
        }
    }

    private static void ApplyParagraphStyle(
        MainDocumentPart mainPart,
        string? styleId,
        RichParagraphFormatState format,
        ref RichTextStyle textStyle)
    {
        foreach (W.Style style in EnumerateStyleChain(mainPart, styleId))
        {
            ApplyParagraphProperties(style.StyleParagraphProperties, format);
            ApplyRunProperties(style.StyleRunProperties, ref textStyle);
        }
    }

    private static void ApplyRunStyle(MainDocumentPart mainPart, string? styleId, ref RichTextStyle style)
    {
        foreach (W.Style definition in EnumerateStyleChain(mainPart, styleId))
            ApplyRunProperties(definition.StyleRunProperties, ref style);
    }

    private static IEnumerable<W.Style> EnumerateStyleChain(MainDocumentPart mainPart, string? styleId)
    {
        if (string.IsNullOrEmpty(styleId) || mainPart.StyleDefinitionsPart?.Styles is not { } styles)
            yield break;
        var chain = new Stack<W.Style>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(styleId) && seen.Add(styleId))
        {
            W.Style? style = styles.Elements<W.Style>()
                .FirstOrDefault(candidate => candidate.StyleId?.Value == styleId);
            if (style is null) break;
            chain.Push(style);
            styleId = style.BasedOn?.Val?.Value;
        }
        while (chain.Count > 0) yield return chain.Pop();
    }

    private static void ApplyRunProperties(OpenXmlElement? properties, ref RichTextStyle style)
    {
        if (properties is null) return;
        W.RunFonts? fonts = properties.GetFirstChild<W.RunFonts>();
        string? fontName = fonts?.HighAnsi?.Value ?? fonts?.Ascii?.Value ?? fonts?.ComplexScript?.Value;
        W.FontSize? size = properties.GetFirstChild<W.FontSize>();
        W.Color? color = properties.GetFirstChild<W.Color>();
        W.Shading? shading = properties.GetFirstChild<W.Shading>();
        W.VerticalTextAlignment? vertical = properties.GetFirstChild<W.VerticalTextAlignment>();
        W.Underline? underline = properties.GetFirstChild<W.Underline>();
        W.Languages? languages = properties.GetFirstChild<W.Languages>();
        style = style with
        {
            FontName = fontName ?? style.FontName,
            FontSize = TryHalfPoints(size?.Val?.Value, out float fontSize) ? fontSize : style.FontSize,
            Foreground = color?.Val?.Value is { } foreground ? ParseBrush(foreground) : style.Foreground,
            Background = shading?.Fill?.Value is { } background ? ParseBrush(background) : style.Background,
            IsBold = ReadOnOff(properties.GetFirstChild<W.Bold>(), style.IsBold),
            IsItalic = ReadOnOff(properties.GetFirstChild<W.Italic>(), style.IsItalic),
            IsUnderline = underline is not null && underline.Val?.Value != W.UnderlineValues.None,
            UnderlineType = MapUnderline(underline?.Val?.Value),
            IsStrikethrough = ReadOnOff(properties.GetFirstChild<W.Strike>(), style.IsStrikethrough),
            IsHidden = ReadOnOff(properties.GetFirstChild<W.Vanish>(), style.IsHidden),
            IsAllCaps = ReadOnOff(properties.GetFirstChild<W.Caps>(), style.IsAllCaps),
            IsSmallCaps = ReadOnOff(properties.GetFirstChild<W.SmallCaps>(), style.IsSmallCaps),
            IsOutline = ReadOnOff(properties.GetFirstChild<W.Outline>(), style.IsOutline),
            IsSubscript = vertical?.Val?.Value == W.VerticalPositionValues.Subscript,
            IsSuperscript = vertical?.Val?.Value == W.VerticalPositionValues.Superscript,
            LanguageTag = languages?.Val?.Value ?? languages?.Bidi?.Value ?? style.LanguageTag,
            FlowDirection = properties.GetFirstChild<W.RightToLeftText>() is null
                ? style.FlowDirection
                : FlowDirection.RightToLeft,
            CharacterSpacing = TryTwips(properties.GetFirstChild<W.Spacing>()?.Val?.Value, out float spacing)
                ? spacing
                : style.CharacterSpacing
        };
    }

    private static void ApplyParagraphProperties(OpenXmlElement? properties, RichParagraphFormatState format)
    {
        if (properties is null) return;
        W.Justification? justification = properties.GetFirstChild<W.Justification>();
        W.JustificationValues? justificationValue = justification?.Val?.Value;
        if (justificationValue == W.JustificationValues.Center) format.Alignment = ParagraphAlignment.Center;
        else if (justificationValue == W.JustificationValues.Right || justificationValue == W.JustificationValues.End)
            format.Alignment = ParagraphAlignment.Right;
        else if (justificationValue == W.JustificationValues.Both || justificationValue == W.JustificationValues.Distribute)
            format.Alignment = ParagraphAlignment.Justify;
        else if (justificationValue == W.JustificationValues.Left || justificationValue == W.JustificationValues.Start)
            format.Alignment = ParagraphAlignment.Left;
        W.Indentation? indentation = properties.GetFirstChild<W.Indentation>();
        if (TryTwips(indentation?.Left?.Value ?? indentation?.Start?.Value, out float left)) format.LeftIndent = left;
        if (TryTwips(indentation?.Right?.Value ?? indentation?.End?.Value, out float right)) format.RightIndent = right;
        if (TryTwips(indentation?.FirstLine?.Value, out float first)) format.FirstLineIndent = first;
        if (TryTwips(indentation?.Hanging?.Value, out float hanging)) format.FirstLineIndent = -hanging;
        W.SpacingBetweenLines? spacing = properties.GetFirstChild<W.SpacingBetweenLines>();
        if (TryTwips(spacing?.Before?.Value, out float before)) format.SpaceBefore = before;
        if (TryTwips(spacing?.After?.Value, out float after)) format.SpaceAfter = after;
        if (int.TryParse(spacing?.Line?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int line))
        {
            if (spacing?.LineRule?.Value == W.LineSpacingRuleValues.Auto)
            {
                format.LineSpacingRule = LineSpacingRule.Multiple;
                format.LineSpacing = line / 240f;
            }
            else
            {
                format.LineSpacingRule = spacing?.LineRule?.Value == W.LineSpacingRuleValues.Exact
                    ? LineSpacingRule.Exactly
                    : LineSpacingRule.AtLeast;
                format.LineSpacing = line / 20f;
            }
        }
        format.RightToLeft = properties.GetFirstChild<W.BiDi>() is null
            ? format.RightToLeft
            : FormatEffect.On;
        W.NumberingProperties? numbering = properties.GetFirstChild<W.NumberingProperties>();
        if (numbering?.NumberingId?.Val?.Value is int numberingId && numberingId > 0)
        {
            format.ListType = MarkerType.Arabic;
            format.ListLevelIndex = numbering.NumberingLevelReference?.Val?.Value ?? 0;
            format.ListStart = 1;
        }
    }

    private static void ApplyNumbering(
        MainDocumentPart mainPart,
        W.ParagraphProperties? properties,
        RichParagraphFormatState format)
    {
        W.NumberingProperties? numbering = properties?.NumberingProperties;
        int numberingId = numbering?.NumberingId?.Val?.Value ?? 0;
        int levelIndex = numbering?.NumberingLevelReference?.Val?.Value ?? 0;
        if (numberingId <= 0 || mainPart.NumberingDefinitionsPart?.Numbering is not { } definitions) return;
        W.NumberingInstance? instance = definitions.Elements<W.NumberingInstance>()
            .FirstOrDefault(candidate => candidate.NumberID?.Value == numberingId);
        int abstractId = instance?.AbstractNumId?.Val?.Value ?? -1;
        W.AbstractNum? abstractNumbering = definitions.Elements<W.AbstractNum>()
            .FirstOrDefault(candidate => candidate.AbstractNumberId?.Value == abstractId);
        W.Level? level = abstractNumbering?.Elements<W.Level>()
            .FirstOrDefault(candidate => candidate.LevelIndex?.Value == levelIndex);
        format.ListType = MapNumberFormat(level?.NumberingFormat?.Val?.Value);
        format.ListLevelIndex = levelIndex;
        format.ListStart = level?.StartNumberingValue?.Val?.Value ?? 1;
    }

    private static MarkerType MapNumberFormat(W.NumberFormatValues? value)
    {
        if (value == W.NumberFormatValues.Bullet) return MarkerType.Bullet;
        if (value == W.NumberFormatValues.LowerLetter) return MarkerType.LowercaseEnglishLetter;
        if (value == W.NumberFormatValues.UpperLetter) return MarkerType.UppercaseEnglishLetter;
        if (value == W.NumberFormatValues.LowerRoman) return MarkerType.LowercaseRoman;
        if (value == W.NumberFormatValues.UpperRoman) return MarkerType.UppercaseRoman;
        if (value == W.NumberFormatValues.Hebrew1 || value == W.NumberFormatValues.Hebrew2) return MarkerType.Hebrew;
        return MarkerType.Arabic;
    }

    private static W.Paragraph CreateParagraph(
        MainDocumentPart mainPart,
        IReadOnlyList<RichTextSpan> spans,
        RichTextRtfCodec.ParagraphSpan descriptor)
    {
        var paragraph = new W.Paragraph();
        paragraph.AppendChild(CreateParagraphProperties(descriptor.Format));
        AppendSpanRange(mainPart, paragraph, spans, descriptor.Start, descriptor.Length);
        return paragraph;
    }

    private static W.TableRow CreateTableRow(
        MainDocumentPart mainPart,
        IReadOnlyList<RichTextSpan> spans,
        RichTextRtfCodec.ParagraphSpan descriptor)
    {
        List<List<RichTextSpan>> cells = SplitCells(ReadSpanRange(spans, descriptor.Start, descriptor.Length));
        var row = new W.TableRow();
        for (int index = 0; index < cells.Count; index++)
        {
            var properties = new W.TableCellProperties();
            int columnSpan = descriptor.Format.TableCellColumnSpans is { } spansByCell && index < spansByCell.Length
                ? Math.Max(1, spansByCell[index])
                : 1;
            if (columnSpan > 1) properties.AppendChild(new W.GridSpan { Val = columnSpan });
            if (descriptor.Format.TableCellVerticalMergeFlags is { } merges && index < merges.Length && merges[index] != 0)
                properties.AppendChild(new W.VerticalMerge
                {
                    Val = merges[index] == 1 ? W.MergedCellValues.Restart : W.MergedCellValues.Continue
                });
            if (descriptor.Format.TableCellBackgrounds is { } backgrounds && index < backgrounds.Length &&
                TryFormatBrush(backgrounds[index], out string? fill))
            {
                properties.AppendChild(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = fill });
            }
            var cell = new W.TableCell(properties);
            var paragraph = new W.Paragraph();
            AppendRuns(mainPart, paragraph, cells[index]);
            cell.AppendChild(paragraph);
            row.AppendChild(cell);
        }
        return row;
    }

    private static W.ParagraphProperties CreateParagraphProperties(RichParagraphFormatState format)
    {
        var properties = new W.ParagraphProperties();
        if (format.ListType is not (MarkerType.None or MarkerType.Undefined))
        {
            properties.AppendChild(new W.NumberingProperties(
                new W.NumberingLevelReference { Val = Math.Max(0, format.ListLevelIndex) },
                new W.NumberingId
                {
                    Val = format.ListType is MarkerType.Bullet or MarkerType.BlackCircleWingding or MarkerType.WhiteCircleWingding
                        ? 2
                        : 1
                }));
        }
        if (format.RightToLeft == FormatEffect.On) properties.AppendChild(new W.BiDi());
        var spacing = new W.SpacingBetweenLines
        {
            Before = FormatTwips(format.SpaceBefore),
            After = FormatTwips(format.SpaceAfter)
        };
        if (format.LineSpacing > 0f)
        {
            spacing.Line = format.LineSpacingRule == LineSpacingRule.Multiple
                ? Math.Max(1, (int)MathF.Round(format.LineSpacing * 240f)).ToString(CultureInfo.InvariantCulture)
                : FormatTwips(format.LineSpacing);
            spacing.LineRule = format.LineSpacingRule switch
            {
                LineSpacingRule.Exactly => W.LineSpacingRuleValues.Exact,
                LineSpacingRule.AtLeast => W.LineSpacingRuleValues.AtLeast,
                _ => W.LineSpacingRuleValues.Auto
            };
        }
        properties.AppendChild(spacing);
        var indentation = new W.Indentation();
        bool hasIndentation = false;
        if (format.LeftIndent != 0f) { indentation.Left = FormatTwips(format.LeftIndent); hasIndentation = true; }
        if (format.RightIndent != 0f) { indentation.Right = FormatTwips(format.RightIndent); hasIndentation = true; }
        if (format.FirstLineIndent > 0f) { indentation.FirstLine = FormatTwips(format.FirstLineIndent); hasIndentation = true; }
        else if (format.FirstLineIndent < 0f) { indentation.Hanging = FormatTwips(-format.FirstLineIndent); hasIndentation = true; }
        if (hasIndentation) properties.AppendChild(indentation);
        if (format.Alignment != ParagraphAlignment.Undefined)
            properties.AppendChild(new W.Justification { Val = format.Alignment switch
            {
                ParagraphAlignment.Center => W.JustificationValues.Center,
                ParagraphAlignment.Right => W.JustificationValues.Right,
                ParagraphAlignment.Justify => W.JustificationValues.Both,
                _ => W.JustificationValues.Left
            }});
        return properties;
    }

    private static void AppendSpanRange(
        MainDocumentPart mainPart,
        W.Paragraph paragraph,
        IReadOnlyList<RichTextSpan> spans,
        int start,
        int length) => AppendRuns(mainPart, paragraph, ReadSpanRange(spans, start, length));

    private static void AppendRuns(
        MainDocumentPart mainPart,
        W.Paragraph paragraph,
        IReadOnlyList<RichTextSpan> spans)
    {
        foreach (RichTextSpan span in spans)
        {
            if (!string.IsNullOrWhiteSpace(span.Style.Link))
            {
                if (span.Style.Link.StartsWith('#'))
                {
                    var internalLink = new W.Hyperlink { Anchor = span.Style.Link[1..] };
                    AppendRunElements(mainPart, internalLink, span);
                    paragraph.AppendChild(internalLink);
                    continue;
                }
                HyperlinkRelationship relationship = mainPart.AddHyperlinkRelationship(
                    new Uri(span.Style.Link, UriKind.RelativeOrAbsolute),
                    isExternal: true);
                var hyperlink = new W.Hyperlink { Id = relationship.Id };
                AppendRunElements(mainPart, hyperlink, span);
                paragraph.AppendChild(hyperlink);
            }
            else
            {
                AppendRunElements(mainPart, paragraph, span);
            }
        }
    }

    private static void AppendRunElements(MainDocumentPart mainPart, OpenXmlCompositeElement parent, RichTextSpan span)
    {
        if (span.Style.EmbeddedObject is { } embedded && !embedded.Data.IsEmpty)
        {
            var run = new W.Run(CreateRunProperties(span.Style));
            run.AppendChild(CreateDrawing(mainPart, embedded));
            parent.AppendChild(run);
            return;
        }
        int segmentStart = 0;
        for (int index = 0; index <= span.Text.Length; index++)
        {
            bool special = index < span.Text.Length && span.Text[index] is '\n' or '\t';
            if (index > segmentStart && (special || index == span.Text.Length))
            {
                string text = span.Text[segmentStart..index];
                parent.AppendChild(new W.Run(
                    CreateRunProperties(span.Style),
                    new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
            }
            if (special)
            {
                OpenXmlElement marker = span.Text[index] == '\t' ? new W.TabChar() : new W.Break();
                parent.AppendChild(new W.Run(CreateRunProperties(span.Style), marker));
                segmentStart = index + 1;
            }
        }
    }

    private static W.RunProperties CreateRunProperties(RichTextStyle style)
    {
        var properties = new W.RunProperties();
        string? fontName = style.FontName ?? style.Font?.FamilyName;
        if (!string.IsNullOrWhiteSpace(fontName))
            properties.AppendChild(new W.RunFonts { Ascii = fontName, HighAnsi = fontName, ComplexScript = fontName });
        if (style.IsBold) properties.AppendChild(new W.Bold());
        if (style.IsItalic) properties.AppendChild(new W.Italic());
        if (style.IsAllCaps) properties.AppendChild(new W.Caps());
        if (style.IsSmallCaps) properties.AppendChild(new W.SmallCaps());
        if (style.IsStrikethrough) properties.AppendChild(new W.Strike());
        if (style.IsOutline) properties.AppendChild(new W.Outline());
        if (style.IsHidden) properties.AppendChild(new W.Vanish());
        if (TryFormatBrush(style.Foreground, out string? foreground)) properties.AppendChild(new W.Color { Val = foreground });
        if (style.CharacterSpacing != 0f) properties.AppendChild(new W.Spacing { Val = (int)MathF.Round(style.CharacterSpacing * 20f) });
        if (style.FontSize > 0f)
        {
            string halfPoints = Math.Max(1, (int)MathF.Round(style.FontSize * 2f)).ToString(CultureInfo.InvariantCulture);
            properties.Append(new W.FontSize { Val = halfPoints }, new W.FontSizeComplexScript { Val = halfPoints });
        }
        if (style.IsUnderline) properties.AppendChild(new W.Underline { Val = MapUnderline(style.UnderlineType) });
        if (TryFormatBrush(style.Background, out string? background))
            properties.AppendChild(new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = background });
        if (style.IsSubscript) properties.AppendChild(new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Subscript });
        if (style.IsSuperscript) properties.AppendChild(new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Superscript });
        if (!string.IsNullOrWhiteSpace(style.LanguageTag)) properties.AppendChild(new W.Languages { Val = style.LanguageTag });
        if (style.FlowDirection == FlowDirection.RightToLeft) properties.AppendChild(new W.RightToLeftText());
        return properties;
    }

    private static W.TableProperties CreateTableProperties(RichParagraphFormatState format)
    {
        var properties = new W.TableProperties();
        if (format.RightToLeft == FormatEffect.On) properties.AppendChild(new W.BiDiVisual());
        properties.AppendChild(new W.TableWidth { Width = "0", Type = W.TableWidthUnitValues.Auto });
        if (format.TableBorderThickness > 0f)
        {
            UInt32Value size = (uint)Math.Clamp((int)MathF.Round(format.TableBorderThickness * 8f), 1, 96);
            string color = TryFormatBrush(format.TableBorderBrush, out string? borderColor) ? borderColor! : "808080";
            properties.AppendChild(new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Size = size, Color = color },
                new W.LeftBorder { Val = W.BorderValues.Single, Size = size, Color = color },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = size, Color = color },
                new W.RightBorder { Val = W.BorderValues.Single, Size = size, Color = color },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = size, Color = color },
                new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = size, Color = color }));
        }
        properties.AppendChild(new W.TableLayout { Type = W.TableLayoutValues.Fixed });
        if (format.TableCellPadding > 0f)
        {
            string width = FormatTwips(format.TableCellPadding);
            properties.AppendChild(new W.TableCellMarginDefault(
                new W.TopMargin { Width = width, Type = W.TableWidthUnitValues.Dxa },
                new W.TableCellLeftMargin { Width = (short)Math.Clamp((int)MathF.Round(format.TableCellPadding * 20f), short.MinValue, short.MaxValue), Type = W.TableWidthValues.Dxa },
                new W.BottomMargin { Width = width, Type = W.TableWidthUnitValues.Dxa },
                new W.TableCellRightMargin { Width = (short)Math.Clamp((int)MathF.Round(format.TableCellPadding * 20f), short.MinValue, short.MaxValue), Type = W.TableWidthValues.Dxa }));
        }
        return properties;
    }

    private static W.Drawing CreateDrawing(MainDocumentPart mainPart, RichTextEmbeddedObject embedded)
    {
        PartTypeInfo partType = GetImagePartType(embedded.Data.Span);
        ImagePart imagePart = mainPart.AddImagePart(partType);
        using (Stream destination = imagePart.GetStream(FileMode.Create, FileAccess.Write))
            destination.Write(embedded.Data.Span);
        string relationshipId = mainPart.GetIdOfPart(imagePart);
        long width = Math.Max(1, embedded.Width) * EmusPerLogicalPixel;
        long height = Math.Max(1, embedded.Height) * EmusPerLogicalPixel;
        uint drawingId = (uint)Interlocked.Increment(ref s_nextDrawingId);
        return new W.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = width, Cy = height },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = drawingId, Name = $"Image {drawingId}", Description = embedded.AlternateText },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"Image {drawingId}" },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(
                            new A.Blip { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                            new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(
                                new A.Offset { X = 0L, Y = 0L },
                                new A.Extents { Cx = width, Cy = height }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            });
    }

    private static bool TryReadEmbeddedImage(
        MainDocumentPart mainPart,
        W.Drawing drawing,
        out RichTextEmbeddedObject? embedded)
    {
        embedded = null;
        A.Blip? blip = drawing.Descendants<A.Blip>().FirstOrDefault();
        string? relationshipId = blip?.Embed?.Value;
        if (string.IsNullOrEmpty(relationshipId) || mainPart.GetPartById(relationshipId) is not ImagePart imagePart)
            return false;
        using Stream source = imagePart.GetStream(FileMode.Open, FileAccess.Read);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        DW.Extent? extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
        int width = (int)Math.Clamp((extent?.Cx?.Value ?? 0L) / EmusPerLogicalPixel, 0L, int.MaxValue);
        int height = (int)Math.Clamp((extent?.Cy?.Value ?? 0L) / EmusPerLogicalPixel, 0L, int.MaxValue);
        string alternate = drawing.Descendants<DW.DocProperties>().FirstOrDefault()?.Description?.Value ?? string.Empty;
        embedded = new RichTextEmbeddedObject(
            width,
            height,
            height,
            VerticalCharacterAlignment.Baseline,
            alternate,
            buffer.ToArray());
        return true;
    }

    private static string? ResolveHyperlink(MainDocumentPart mainPart, W.Hyperlink hyperlink)
    {
        string? id = hyperlink.Id?.Value;
        if (!string.IsNullOrEmpty(id))
            return mainPart.HyperlinkRelationships.FirstOrDefault(link => link.Id == id)?.Uri.ToString();
        return hyperlink.Anchor?.Value is { Length: > 0 } anchor ? $"#{anchor}" : null;
    }

    private static void AddStyles(MainDocumentPart mainPart)
    {
        StyleDefinitionsPart stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new W.Styles(
            new W.DocDefaults(
                new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                    new W.RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI", ComplexScript = "Segoe UI" },
                    new W.FontSize { Val = "28" },
                    new W.FontSizeComplexScript { Val = "28" })),
                new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle())));
    }

    private static void AddNumbering(MainDocumentPart mainPart, IReadOnlyList<RichTextRtfCodec.ParagraphSpan> paragraphs)
    {
        if (!paragraphs.Any(static paragraph => paragraph.Format.ListType is not (MarkerType.None or MarkerType.Undefined)))
            return;
        NumberingDefinitionsPart part = mainPart.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering = new W.Numbering(
            CreateAbstractNumbering(1, W.NumberFormatValues.Decimal, "%1."),
            CreateAbstractNumbering(2, W.NumberFormatValues.Bullet, "•"),
            new W.NumberingInstance(new W.AbstractNumId { Val = 1 }) { NumberID = 1 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 2 }) { NumberID = 2 });
    }

    private static W.AbstractNum CreateAbstractNumbering(int id, W.NumberFormatValues format, string text)
    {
        var abstractNumbering = new W.AbstractNum { AbstractNumberId = id };
        for (int level = 0; level < 9; level++)
        {
            abstractNumbering.AppendChild(new W.Level(
                new W.StartNumberingValue { Val = 1 },
                new W.NumberingFormat { Val = format },
                new W.LevelText { Val = format == W.NumberFormatValues.Bullet ? text : $"%{level + 1}." },
                new W.LevelJustification { Val = W.LevelJustificationValues.Left },
                new W.PreviousParagraphProperties(new W.Indentation
                {
                    Left = ((level + 1) * 720).ToString(CultureInfo.InvariantCulture),
                    Hanging = "360"
                })) { LevelIndex = level });
        }
        return abstractNumbering;
    }

    private static List<RichTextSpan> ReadSpanRange(IReadOnlyList<RichTextSpan> spans, int start, int length)
    {
        int end = start + length;
        int position = 0;
        var result = new List<RichTextSpan>();
        foreach (RichTextSpan span in spans)
        {
            int spanEnd = position + span.Text.Length;
            if (spanEnd > start && position < end)
            {
                int localStart = Math.Max(0, start - position);
                int localEnd = Math.Min(span.Text.Length, end - position);
                if (localEnd > localStart)
                    result.Add(new RichTextSpan(span.Text[localStart..localEnd], span.Style));
            }
            position = spanEnd;
            if (position >= end) break;
        }
        return result;
    }

    private static List<List<RichTextSpan>> SplitCells(IReadOnlyList<RichTextSpan> spans)
    {
        var cells = new List<List<RichTextSpan>> { new() };
        foreach (RichTextSpan span in spans)
        {
            int start = 0;
            for (int index = 0; index <= span.Text.Length; index++)
            {
                if (index < span.Text.Length && span.Text[index] != '\t') continue;
                if (index > start) cells[^1].Add(new RichTextSpan(span.Text[start..index], span.Style));
                if (index < span.Text.Length) cells.Add(new List<RichTextSpan>());
                start = index + 1;
            }
        }
        return cells;
    }

    private static void AppendSpan(List<RichTextSpan> spans, string text, RichTextStyle style)
    {
        if (text.Length == 0) return;
        if (spans.Count > 0 && spans[^1].Style.Equals(style))
        {
            RichTextSpan prior = spans[^1];
            spans[^1] = new RichTextSpan(prior.Text + text, style);
        }
        else spans.Add(new RichTextSpan(text, style));
    }

    private static void TrimTerminalSeparator(
        List<RichTextSpan> spans,
        List<RichTextRtfCodec.ParagraphSpan> paragraphs)
    {
        if (spans.Count == 0 || spans[^1].Text.Length == 0 || spans[^1].Text[^1] != '\n') return;
        RichTextSpan last = spans[^1];
        string text = last.Text[..^1];
        if (text.Length == 0) spans.RemoveAt(spans.Count - 1);
        else spans[^1] = new RichTextSpan(text, last.Style);
    }

    private static int GetTextLength(IEnumerable<RichTextSpan> spans) => spans.Sum(static span => span.Text.Length);

    private static float[] ReadTableWidths(W.Table table)
    {
        var widths = new List<float>();
        foreach (W.GridColumn column in table.TableGrid?.Elements<W.GridColumn>() ?? [])
            if (TryTwips(column.Width?.Value, out float width)) widths.Add(Math.Max(1f, width));
        if (widths.Count > 0) return ToRightEdges(widths);
        W.TableRow? firstRow = table.Elements<W.TableRow>().FirstOrDefault();
        if (firstRow is not null)
        {
            foreach (W.TableCell cell in firstRow.Elements<W.TableCell>())
            {
                float width = TryTwips(cell.TableCellProperties?.TableCellWidth?.Width?.Value, out float value)
                    ? Math.Max(1f, value)
                    : 120f;
                int span = Math.Max(1, cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1);
                for (int index = 0; index < span; index++) widths.Add(width / span);
            }
        }
        return ToRightEdges(widths.Count > 0 ? widths : [120f]);
    }

    private static float[] ToRightEdges(IReadOnlyList<float> widths)
    {
        var edges = new float[widths.Count];
        float edge = 0f;
        for (int index = 0; index < widths.Count; index++) edges[index] = edge += widths[index];
        return edges;
    }

    private static float ReadCellPadding(W.TableProperties? properties)
    {
        int? value = properties?.TableCellMarginDefault?.TableCellLeftMargin?.Width?.Value;
        return TryTwips(value, out float padding) ? padding : 8f;
    }

    private static float ReadBorderThickness(W.TableProperties? properties)
    {
        var border = properties?.TableBorders?.TopBorder;
        return border?.Size?.Value is { } eighthPoints ? eighthPoints / 8f : 1f;
    }

    private static Brush? ReadBorderBrush(W.TableProperties? properties) =>
        ParseBrush(properties?.TableBorders?.TopBorder?.Color?.Value);

    private static byte ReadVerticalMerge(W.VerticalMerge? merge)
    {
        W.MergedCellValues? value = merge?.Val?.Value;
        if (value == W.MergedCellValues.Restart) return 1;
        if (value == W.MergedCellValues.Continue) return 2;
        return merge is null ? (byte)0 : (byte)2;
    }

    private static bool ReadOnOff(W.OnOffType? property, bool inherited) =>
        property is null ? inherited : property.Val?.Value ?? true;

    private static UnderlineType MapUnderline(W.UnderlineValues? value)
    {
        if (value is null || value == W.UnderlineValues.None) return UnderlineType.None;
        if (value == W.UnderlineValues.Double) return UnderlineType.Double;
        if (value == W.UnderlineValues.Dotted) return UnderlineType.Dotted;
        if (value == W.UnderlineValues.Dash) return UnderlineType.Dash;
        if (value == W.UnderlineValues.Wave) return UnderlineType.Wave;
        return UnderlineType.Single;
    }

    private static W.UnderlineValues MapUnderline(UnderlineType value) => value switch
    {
        UnderlineType.Double => W.UnderlineValues.Double,
        UnderlineType.Dotted => W.UnderlineValues.Dotted,
        UnderlineType.Dash => W.UnderlineValues.Dash,
        UnderlineType.Wave => W.UnderlineValues.Wave,
        _ => W.UnderlineValues.Single
    };

    private static bool TryHalfPoints(string? value, out float points)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int halfPoints))
        {
            points = halfPoints * 0.5f;
            return true;
        }
        points = 0f;
        return false;
    }

    private static bool TryTwips(string? value, out float logical)
    {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float twips))
        {
            logical = twips / 20f;
            return true;
        }
        logical = 0f;
        return false;
    }

    private static bool TryTwips(int? value, out float logical)
    {
        if (value is { } twips)
        {
            logical = twips / 20f;
            return true;
        }
        logical = 0f;
        return false;
    }

    private static string FormatTwips(float logical) =>
        ((int)MathF.Round(logical * 20f)).ToString(CultureInfo.InvariantCulture);

    private static Brush? ParseBrush(string? rgb)
    {
        if (string.IsNullOrWhiteSpace(rgb) || rgb.Equals("auto", StringComparison.OrdinalIgnoreCase) || rgb.Length < 6)
            return null;
        if (!uint.TryParse(rgb.AsSpan(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            return null;
        return new SolidColorBrush((value << 8) | 0xFFu);
    }

    private static bool TryFormatBrush(Brush? brush, out string? rgb)
    {
        if (brush is not SolidColorBrush solid)
        {
            rgb = null;
            return false;
        }
        Vector4 color = solid.Color;
        int red = Math.Clamp((int)MathF.Round(color.X * 255f), 0, 255);
        int green = Math.Clamp((int)MathF.Round(color.Y * 255f), 0, 255);
        int blue = Math.Clamp((int)MathF.Round(color.Z * 255f), 0, 255);
        rgb = $"{red:X2}{green:X2}{blue:X2}";
        return true;
    }

    private static PartTypeInfo GetImagePartType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ImagePartType.Jpeg;
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return ImagePartType.Gif;
        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M') return ImagePartType.Bmp;
        return ImagePartType.Png;
    }
}
