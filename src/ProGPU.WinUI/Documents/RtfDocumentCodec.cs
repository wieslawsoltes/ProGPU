using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>RTF adapter over the same semantic document and styled-run model used by the editor.</summary>
public sealed class RtfDocumentCodec : IRichDocumentFormatCodec
{
    private readonly record struct RtfParagraphSeed(
        Microsoft.UI.Text.RichParagraphFormatState Format,
        bool IsTableRow = false,
        float[]? TableCellRightEdges = null);

    private readonly record struct SerializedTableCell(
        TableCell? Cell,
        int Column,
        int ColumnSpan,
        byte VerticalMergeFlag)
    {
        public Brush? Background => Cell?.Background;
    }

    private static List<SerializedTableCell>[] ExpandTableRows(Table table)
    {
        var rows = new List<SerializedTableCell>[table.Rows.Count];
        for (int row = 0; row < rows.Length; row++) rows[row] = new List<SerializedTableCell>();
        var occupiedUntilRow = new List<int>();
        for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            int logicalColumn = 0;
            foreach (TableCell cell in table.Rows[rowIndex].Cells)
            {
                int columnSpan = Math.Max(1, cell.ColumnSpan);
                while (true)
                {
                    while (logicalColumn < occupiedUntilRow.Count &&
                           occupiedUntilRow[logicalColumn] > rowIndex)
                    {
                        logicalColumn++;
                    }
                    while (occupiedUntilRow.Count < logicalColumn + columnSpan)
                        occupiedUntilRow.Add(0);
                    bool free = true;
                    for (int offset = 0; offset < columnSpan; offset++)
                    {
                        if (occupiedUntilRow[logicalColumn + offset] <= rowIndex) continue;
                        logicalColumn += offset + 1;
                        free = false;
                        break;
                    }
                    if (free) break;
                }

                int rowSpan = Math.Min(Math.Max(1, cell.RowSpan), table.Rows.Count - rowIndex);
                rows[rowIndex].Add(new SerializedTableCell(
                    cell,
                    logicalColumn,
                    columnSpan,
                    rowSpan > 1 ? (byte)1 : (byte)0));
                for (int continuation = 1; continuation < rowSpan; continuation++)
                {
                    rows[rowIndex + continuation].Add(new SerializedTableCell(
                        null,
                        logicalColumn,
                        columnSpan,
                        continuation == rowSpan - 1 ? (byte)3 : (byte)2));
                }
                for (int offset = 0; offset < columnSpan; offset++)
                    occupiedUntilRow[logicalColumn + offset] = rowIndex + rowSpan;
                logicalColumn += columnSpan;
            }
        }
        for (int row = 0; row < rows.Length; row++)
            rows[row].Sort(static (left, right) => left.Column.CompareTo(right.Column));
        return rows;
    }

    private static readonly string[] Extensions = [".rtf"];
    public static RtfDocumentCodec Default { get; } = new();

    public string FormatId => "application/rtf";
    public IReadOnlyList<string> FileExtensions => Extensions;
    public bool CanImport => true;
    public bool CanExport => true;

    public RichDocument Import(ReadOnlySpan<byte> source, in RichDocumentImportContext context)
    {
        string rtf = Encoding.UTF8.GetString(source);
        var fallback = new RichTextStyle(
            context.DefaultForeground,
            context.DefaultFontSize,
            context.DefaultFont);
        RichTextRtfCodec.DecodedDocument decoded = RichTextRtfCodec.DecodeDocument(rtf, fallback);
        return BuildDocument(decoded);
    }

    public byte[] Export(RichDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        (RichTextSpan[] spans, RichTextRtfCodec.ParagraphSpan[] paragraphs) = CollectRtfContent(document);
        return Encoding.UTF8.GetBytes(RichTextRtfCodec.Encode(spans, paragraphs));
    }

    internal static RichDocument BuildDocument(RichTextRtfCodec.DecodedDocument decoded)
    {
        IReadOnlyList<RichTextSpan> spans = decoded.Spans;
        var blocks = new List<Block>();
        ListBlock? activeList = null;
        Table? activeTable = null;
        int sourceSpanIndex = 0;
        int sourceSpanStart = 0;
        for (int paragraphIndex = 0; paragraphIndex < decoded.Paragraphs.Length; paragraphIndex++)
        {
            RichTextRtfCodec.ParagraphSpan descriptor = decoded.Paragraphs[paragraphIndex];
            if (descriptor.Length == 0 && paragraphIndex == decoded.Paragraphs.Length - 1 &&
                activeTable is not null)
            {
                break;
            }
            List<RichTextSpan> paragraphSpans = ReadRangeSpans(
                spans,
                descriptor.Start,
                descriptor.Length,
                ref sourceSpanIndex,
                ref sourceSpanStart);
            if (descriptor.IsTableRow)
            {
                activeList = null;
                if (activeTable is null)
                {
                    activeTable = new Table
                    {
                        ColumnWidths = GetColumnWidths(descriptor.TableCellRightEdges),
                        CellPadding = descriptor.Format.TableCellPadding,
                        BorderThickness = descriptor.Format.TableBorderThickness,
                        BorderBrush = descriptor.Format.TableBorderBrush,
                        FlowDirection = descriptor.Format.RightToLeft switch
                        {
                            Microsoft.UI.Text.FormatEffect.On => FlowDirection.RightToLeft,
                            Microsoft.UI.Text.FormatEffect.Off => FlowDirection.LeftToRight,
                            _ => null
                        }
                    };
                    blocks.Add(activeTable);
                }
                activeTable.Rows.Add(CreateTableRow(
                    paragraphSpans,
                    descriptor.Format.TableCellBackgrounds,
                    descriptor.Format.TableCellColumnSpans,
                    descriptor.Format.TableCellVerticalMergeFlags));
                continue;
            }

            if (activeTable is not null) CollapseVerticalMerges(activeTable);
            activeTable = null;
            if (descriptor.Format.ListType is not (Microsoft.UI.Text.MarkerType.None or Microsoft.UI.Text.MarkerType.Undefined))
            {
                bool ordered = descriptor.Format.ListType is not (
                    Microsoft.UI.Text.MarkerType.Bullet or
                    Microsoft.UI.Text.MarkerType.BlackCircleWingding or
                    Microsoft.UI.Text.MarkerType.WhiteCircleWingding);
                if (activeList is null || activeList.IsOrdered != ordered)
                {
                    activeList = new ListBlock
                    {
                        IsOrdered = ordered,
                        Indentation = Math.Max(0f, descriptor.Format.LeftIndent)
                    };
                    blocks.Add(activeList);
                }
                var item = new ListItem();
                AddSpansToInlines(paragraphSpans, item.Inlines);
                activeList.Items.Add(item);
                continue;
            }

            activeList = null;
            Paragraph paragraph = CreateParagraph(descriptor);
            AddSpansToInlines(paragraphSpans, paragraph.Inlines);
            blocks.Add(paragraph);
        }
        if (activeTable is not null) CollapseVerticalMerges(activeTable);
        if (blocks.Count == 0) blocks.Add(new Paragraph { MarginBottom = 0f });
        var document = new RichDocument();
        document.ReplaceBlocks(blocks);
        return document;
    }

    private static Paragraph CreateParagraph(RichTextRtfCodec.ParagraphSpan paragraphSpan)
    {
        Microsoft.UI.Text.RichParagraphFormatState state = paragraphSpan.Format.Clone();
        TextAlignment alignment = state.Alignment switch
        {
            Microsoft.UI.Text.ParagraphAlignment.Center => TextAlignment.Center,
            Microsoft.UI.Text.ParagraphAlignment.Right => TextAlignment.Right,
            Microsoft.UI.Text.ParagraphAlignment.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left
        };
        FlowDirection? direction = state.RightToLeft switch
        {
            Microsoft.UI.Text.FormatEffect.On => FlowDirection.RightToLeft,
            Microsoft.UI.Text.FormatEffect.Off => FlowDirection.LeftToRight,
            _ => null
        };
        var paragraph = new Paragraph();
        paragraph.ApplyEditorFormat(
            state,
            alignment,
            direction,
            state.Alignment != Microsoft.UI.Text.ParagraphAlignment.Undefined);
        return paragraph;
    }

    private static List<RichTextSpan> ReadRangeSpans(
        IReadOnlyList<RichTextSpan> spans,
        int start,
        int length,
        ref int spanIndex,
        ref int spanStart)
    {
        int end = start + length;
        while (spanIndex < spans.Count && spanStart + spans[spanIndex].Text.Length <= start)
            spanStart += spans[spanIndex++].Text.Length;
        var result = new List<RichTextSpan>();
        int index = spanIndex;
        int absolute = spanStart;
        while (index < spans.Count && absolute < end)
        {
            RichTextSpan span = spans[index];
            int localStart = Math.Max(0, start - absolute);
            int localEnd = Math.Min(span.Text.Length, end - absolute);
            if (localEnd > localStart)
                result.Add(new RichTextSpan(span.Text[localStart..localEnd], span.Style));
            absolute += span.Text.Length;
            index++;
        }
        return result;
    }

    private static void AddSpansToInlines(
        IEnumerable<RichTextSpan> spans,
        RichElementCollection<Inline> inlines)
    {
        foreach (RichTextSpan span in spans)
        {
            if (span.Text.Length > 0) inlines.Add(CreateInline(span.Text, span.Style));
        }
    }

    private static TableRow CreateTableRow(
        IEnumerable<RichTextSpan> spans,
        Brush?[]? backgrounds,
        int[]? columnSpans,
        byte[]? verticalMergeFlags)
    {
        var row = new TableRow();
        var cell = new TableCell();
        int cellIndex = 0;
        foreach (RichTextSpan span in spans)
        {
            int segmentStart = 0;
            for (int index = 0; index < span.Text.Length; index++)
            {
                if (span.Text[index] != '\t') continue;
                if (index > segmentStart)
                    cell.Inlines.Add(CreateInline(span.Text[segmentStart..index], span.Style));
                if (backgrounds is not null && cellIndex < backgrounds.Length)
                    cell.Background = backgrounds[cellIndex];
                if (columnSpans is not null && cellIndex < columnSpans.Length)
                    cell.ColumnSpan = columnSpans[cellIndex];
                if (verticalMergeFlags is not null && cellIndex < verticalMergeFlags.Length)
                    cell.VerticalMergeFlag = verticalMergeFlags[cellIndex];
                row.Cells.Add(cell);
                cell = new TableCell();
                cellIndex++;
                segmentStart = index + 1;
            }
            if (segmentStart < span.Text.Length)
                cell.Inlines.Add(CreateInline(span.Text[segmentStart..], span.Style));
        }
        if (backgrounds is not null && cellIndex < backgrounds.Length)
            cell.Background = backgrounds[cellIndex];
        if (columnSpans is not null && cellIndex < columnSpans.Length)
            cell.ColumnSpan = columnSpans[cellIndex];
        if (verticalMergeFlags is not null && cellIndex < verticalMergeFlags.Length)
            cell.VerticalMergeFlag = verticalMergeFlags[cellIndex];
        row.Cells.Add(cell);
        return row;
    }

    private static void CollapseVerticalMerges(Table table)
    {
        var active = new Dictionary<(int Column, int Span), TableCell>();
        for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            TableRow row = table.Rows[rowIndex];
            int logicalColumn = 0;
            var remove = new List<TableCell>();
            foreach (TableCell cell in row.Cells)
            {
                int span = Math.Max(1, cell.ColumnSpan);
                var key = (logicalColumn, span);
                if (cell.VerticalMergeFlag == 1)
                {
                    cell.RowSpan = 1;
                    active[key] = cell;
                }
                else if (cell.VerticalMergeFlag >= 2 && active.TryGetValue(key, out TableCell? owner))
                {
                    owner.RowSpan++;
                    remove.Add(cell);
                }
                else
                {
                    for (int column = logicalColumn; column < logicalColumn + span; column++)
                    {
                        foreach ((int start, int width) in active.Keys.ToArray())
                        {
                            if (column >= start && column < start + width) active.Remove((start, width));
                        }
                    }
                }
                logicalColumn += span;
            }
            for (int index = 0; index < remove.Count; index++) row.Cells.Remove(remove[index]);
        }
    }

    private static IList<float>? GetColumnWidths(float[]? rightEdges)
    {
        if (rightEdges is null || rightEdges.Length == 0) return null;
        var widths = new List<float>(rightEdges.Length);
        float left = 0f;
        for (int index = 0; index < rightEdges.Length; index++)
        {
            float right = Math.Max(left + 1f, rightEdges[index]);
            widths.Add(right - left);
            left = right;
        }
        return widths;
    }

    internal static (RichTextSpan[] Spans, RichTextRtfCodec.ParagraphSpan[] Paragraphs)
        CollectRtfContent(RichDocument document)
    {
        RichTextSpan[] spans = CollectSpans(document);
        var seeds = new Dictionary<int, RtfParagraphSeed>();
        int prefixLength = 0;
        for (int index = 0; index < document.Blocks.Count; index++)
        {
            if (index > 0) prefixLength++;
            Block block = document.Blocks[index];
            AddBlockSeeds(block, prefixLength, seeds);
            prefixLength += GetBlockTextLength(block);
        }

        var paragraphs = new List<RichTextRtfCodec.ParagraphSpan>();
        int paragraphStart = 0;
        int absolute = 0;
        RtfParagraphSeed activeSeed = new(new Microsoft.UI.Text.RichParagraphFormatState());
        foreach (RichTextSpan span in spans)
        {
            foreach (char character in span.Text)
            {
                if (character == '\n')
                {
                    if (seeds.TryGetValue(paragraphStart, out RtfParagraphSeed seed)) activeSeed = seed;
                    paragraphs.Add(new RichTextRtfCodec.ParagraphSpan(
                        paragraphStart,
                        absolute - paragraphStart,
                        activeSeed.Format.Clone())
                    {
                        IsTableRow = activeSeed.IsTableRow,
                        TableCellRightEdges = activeSeed.TableCellRightEdges is null
                            ? null
                            : (float[])activeSeed.TableCellRightEdges.Clone()
                    });
                    paragraphStart = absolute + 1;
                }
                absolute++;
            }
        }
        if (seeds.TryGetValue(paragraphStart, out RtfParagraphSeed finalSeed)) activeSeed = finalSeed;
        paragraphs.Add(new RichTextRtfCodec.ParagraphSpan(
            paragraphStart,
            absolute - paragraphStart,
            activeSeed.Format.Clone())
        {
            IsTableRow = activeSeed.IsTableRow,
            TableCellRightEdges = activeSeed.TableCellRightEdges is null
                ? null
                : (float[])activeSeed.TableCellRightEdges.Clone()
        });
        return (spans, paragraphs.ToArray());
    }

    private static void AddBlockSeeds(
        Block block,
        int start,
        Dictionary<int, RtfParagraphSeed> seeds)
    {
        switch (block)
        {
            case Paragraph paragraph:
                seeds[start] = new RtfParagraphSeed(GetParagraphFormat(paragraph));
                break;
            case ListBlock list:
            {
                int position = start;
                for (int index = 0; index < list.Items.Count; index++)
                {
                    var format = new Microsoft.UI.Text.RichParagraphFormatState
                    {
                        ListType = list.IsOrdered
                            ? Microsoft.UI.Text.MarkerType.Arabic
                            : Microsoft.UI.Text.MarkerType.Bullet,
                        ListStart = index + 1,
                        ListLevelIndex = 0,
                        LeftIndent = list.Indentation,
                        ListTab = list.Indentation
                    };
                    seeds[position] = new RtfParagraphSeed(format);
                    position += GetInlinesTextLength(list.Items[index].Inlines) + 1;
                }
                break;
            }
            case Table table:
            {
                float[] rightEdges = GetTableCellRightEdges(table);
                List<SerializedTableCell>[] serializedRows = ExpandTableRows(table);
                int position = start;
                for (int row = 0; row < serializedRows.Length; row++)
                {
                    List<SerializedTableCell> serializedCells = serializedRows[row];
                    var format = new Microsoft.UI.Text.RichParagraphFormatState
                    {
                        IsTableRow = true,
                        TableCellRightEdges = (float[])rightEdges.Clone(),
                        TableCellPadding = table.CellPadding,
                        TableBorderThickness = table.BorderThickness,
                        TableBorderBrush = table.BorderBrush,
                        RightToLeft = table.FlowDirection switch
                        {
                            FlowDirection.RightToLeft => Microsoft.UI.Text.FormatEffect.On,
                            FlowDirection.LeftToRight => Microsoft.UI.Text.FormatEffect.Off,
                            _ => Microsoft.UI.Text.FormatEffect.Undefined
                        },
                        TableCellBackgrounds = serializedCells
                            .Select(static cell => cell.Background)
                            .ToArray(),
                        TableCellColumnSpans = serializedCells
                            .Select(static cell => cell.ColumnSpan)
                            .ToArray(),
                        TableCellVerticalMergeFlags = serializedCells
                            .Select(static cell => cell.VerticalMergeFlag)
                            .ToArray()
                    };
                    seeds[position] = new RtfParagraphSeed(
                        format,
                        IsTableRow: true,
                        TableCellRightEdges: rightEdges);
                    position += serializedCells.Sum(static cell =>
                            cell.Cell is { } sourceCell ? GetInlinesTextLength(sourceCell.Inlines) : 0) +
                        Math.Max(0, serializedCells.Count - 1) + 1;
                }
                break;
            }
        }
    }

    private static float[] GetTableCellRightEdges(Table table)
    {
        List<SerializedTableCell>[] rows = ExpandTableRows(table);
        int columnCount = rows.Length == 0
            ? 0
            : rows.Max(static row => row.Count == 0
                ? 0
                : row.Max(static cell => cell.Column + cell.ColumnSpan));
        var result = new float[columnCount];
        float edge = 0f;
        for (int column = 0; column < columnCount; column++)
        {
            float width = table.ColumnWidths is { } widths && column < widths.Count
                ? Math.Max(1f, widths[column])
                : 120f;
            edge += width;
            result[column] = edge;
        }
        return result;
    }

    private static int GetBlockTextLength(Block block)
    {
        return block switch
        {
            Paragraph paragraph => GetInlinesTextLength(paragraph.Inlines),
            ListBlock list => list.Items.Sum(static item => GetInlinesTextLength(item.Inlines)) +
                Math.Max(0, list.Items.Count - 1),
            Table table => GetSerializedTableTextLength(table),
            Inline inline => GetInlineTextLength(inline),
            _ => 0
        };
    }

    private static int GetSerializedTableTextLength(Table table)
    {
        List<SerializedTableCell>[] rows = ExpandTableRows(table);
        int length = Math.Max(0, rows.Length - 1);
        for (int row = 0; row < rows.Length; row++)
        {
            length += Math.Max(0, rows[row].Count - 1);
            for (int cell = 0; cell < rows[row].Count; cell++)
            {
                if (rows[row][cell].Cell is { } sourceCell)
                    length += GetInlinesTextLength(sourceCell.Inlines);
            }
        }
        return length;
    }

    private static int GetInlinesTextLength(IEnumerable<Inline> inlines) =>
        inlines.Sum(static inline => GetInlineTextLength(inline));

    private static int GetInlineTextLength(Inline inline) => inline switch
    {
        Run run => run.Text.Length,
        LineBreak => 1,
        Span span => GetInlinesTextLength(span.Inlines),
        InlineUIContainer => 1,
        _ => 0
    };

    private static Microsoft.UI.Text.RichParagraphFormatState GetParagraphFormat(Paragraph paragraph)
    {
        if (paragraph.EditorFormatState is { } retained) return retained.Clone();
        return new Microsoft.UI.Text.RichParagraphFormatState
        {
            Alignment = paragraph.TextAlignment switch
            {
                TextAlignment.Center => Microsoft.UI.Text.ParagraphAlignment.Center,
                TextAlignment.Right => Microsoft.UI.Text.ParagraphAlignment.Right,
                TextAlignment.Justify => Microsoft.UI.Text.ParagraphAlignment.Justify,
                _ => Microsoft.UI.Text.ParagraphAlignment.Left
            },
            FirstLineIndent = paragraph.FirstLineIndent,
            LeftIndent = paragraph.LeftIndent,
            RightIndent = paragraph.RightIndent,
            SpaceBefore = paragraph.SpaceBefore,
            SpaceAfter = paragraph.MarginBottom,
            LineSpacing = paragraph.LineSpacing,
            LineSpacingRule = paragraph.LineSpacingRule,
            RightToLeft = paragraph.FlowDirection switch
            {
                FlowDirection.RightToLeft => Microsoft.UI.Text.FormatEffect.On,
                FlowDirection.LeftToRight => Microsoft.UI.Text.FormatEffect.Off,
                _ => Microsoft.UI.Text.FormatEffect.Undefined
            }
        };
    }

    private static RichTextSpan[] CollectSpans(RichDocument document)
    {
        var spans = new List<RichTextSpan>();
        for (int blockIndex = 0; blockIndex < document.Blocks.Count; blockIndex++)
        {
            if (blockIndex > 0) Append(spans, "\n", default);
            CollectBlock(document.Blocks[blockIndex], spans);
        }
        return spans.ToArray();
    }

    private static void CollectBlock(Block block, List<RichTextSpan> spans)
    {
        switch (block)
        {
            case Paragraph paragraph:
                CollectInlines(paragraph.Inlines, spans, default);
                break;
            case ListBlock list:
                for (int index = 0; index < list.Items.Count; index++)
                {
                    if (index > 0) Append(spans, "\n", default);
                    CollectInlines(list.Items[index].Inlines, spans, default);
                }
                break;
            case Table table:
                List<SerializedTableCell>[] serializedRows = ExpandTableRows(table);
                for (int row = 0; row < serializedRows.Length; row++)
                {
                    if (row > 0) Append(spans, "\n", default);
                    for (int cell = 0; cell < serializedRows[row].Count; cell++)
                    {
                        if (cell > 0) Append(spans, "\t", default);
                        if (serializedRows[row][cell].Cell is { } sourceCell)
                            CollectInlines(sourceCell.Inlines, spans, default);
                    }
                }
                break;
            case Inline inline:
                CollectInline(inline, spans, default);
                break;
        }
    }

    private static void CollectInlines(
        IEnumerable<Inline> inlines,
        List<RichTextSpan> spans,
        RichTextStyle inherited)
    {
        foreach (Inline inline in inlines) CollectInline(inline, spans, inherited);
    }

    private static void CollectInline(Inline inline, List<RichTextSpan> spans, RichTextStyle inherited)
    {
        RichTextStyle style = inherited with
        {
            Foreground = inline.Foreground ?? inherited.Foreground,
            FontSize = inline.FontSize ?? (inherited.FontSize > 0f ? inherited.FontSize : 14f),
            Font = inline.Font ?? inherited.Font,
            IsBold = inherited.IsBold || inline is Bold,
            IsItalic = inherited.IsItalic || inline is Italic,
            IsUnderline = inherited.IsUnderline || inline is Underline or Hyperlink,
            Link = inline is Hyperlink hyperlink ? hyperlink.Uri : inherited.Link
        };
        if (inline is Run runStyle)
        {
            if (runStyle.RetainedStyle is { } retained) style = retained;
            if (runStyle.HasExplicitFlowDirection)
                style = style with { FlowDirection = runStyle.FlowDirection };
        }
        switch (inline)
        {
            case Run run:
                Append(spans, run.Text, style);
                break;
            case LineBreak:
                Append(spans, "\n", style);
                break;
            case Span span:
                CollectInlines(span.Inlines, spans, style);
                break;
            case InlineUIContainer container:
                Append(spans, "\uFFFC", style with { EmbeddedObject = container.RetainedEmbeddedObject });
                break;
        }
    }

    private static void Append(List<RichTextSpan> spans, string text, RichTextStyle style)
    {
        if (text.Length == 0) return;
        if (spans.Count > 0 && spans[^1].Style.Equals(style))
        {
            RichTextSpan prior = spans[^1];
            spans[^1] = new RichTextSpan(prior.Text + text, style);
        }
        else
        {
            spans.Add(new RichTextSpan(text, style));
        }
    }

    private static Inline CreateInline(string text, RichTextStyle style)
    {
        Inline inline;
        if (text.Length == 1 && text[0] == '\uFFFC' && style.EmbeddedObject is { } embedded)
        {
            FrameworkElement child = embedded.ImageSource is { } source
                ? new Image
                {
                    Source = source,
                    Width = embedded.Width > 0 ? embedded.Width : float.NaN,
                    Height = embedded.Height > 0 ? embedded.Height : float.NaN
                }
                : new TextBlock { Text = embedded.AlternateText };
            inline = new InlineUIContainer(child)
            {
                Foreground = style.Foreground,
                FontSize = style.FontSize,
                Font = style.Font,
                RetainedEmbeddedObject = embedded
            };
        }
        else
        {
            var run = new Run(text)
            {
                Foreground = style.Foreground,
                FontSize = style.FontSize,
                Font = style.Font,
                RetainedStyle = style
            };
            run.ApplyRetainedFlowDirection(style.FlowDirection);
            inline = run;
        }
        if (style.IsBold) inline = new Bold(inline);
        if (style.IsItalic) inline = new Italic(inline);
        if (style.IsUnderline) inline = new Underline(inline);
        if (!string.IsNullOrEmpty(style.Link)) inline = new Hyperlink(inline) { Uri = style.Link };
        return inline;
    }
}
