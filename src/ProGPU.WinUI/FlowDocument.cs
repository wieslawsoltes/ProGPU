using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class Paragraph : Block
{
    public List<Inline> Inlines { get; } = new();
    public TextAlignment TextAlignment { get; set; } = TextAlignment.Left;

    public Paragraph() { }
    public Paragraph(params Inline[] inlines)
    {
        Inlines.AddRange(inlines);
    }
}

public class FlowDocument : FrameworkElement
{
    private float _fontSize = 14f;
    private int _columnCount = 2;
    private float _columnGap = 24f;
    private readonly List<Paragraph> _paragraphs = new();
    public List<Paragraph> Paragraphs => _paragraphs;
    public List<Block> Blocks { get; } = new();
    private readonly List<TableVisualDecoration> _tableDecorations = new();

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty)
        {
            Invalidate();
        }
    }

    public float FontSize
    {
        get => _fontSize;
        set { _fontSize = value; Invalidate(); }
    }

    private Brush? _foreground;
    public Brush? Foreground
    {
        get => _foreground;
        set { _foreground = value; Invalidate(); }
    }

    public int ColumnCount
    {
        get => _columnCount;
        set { _columnCount = Math.Max(1, value); Invalidate(); }
    }

    public float ColumnGap
    {
        get => _columnGap;
        set { _columnGap = value; Invalidate(); }
    }

    public FlowDocument()
    {
        Padding = new Thickness(16);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsEnabled) return;

        var localPos = InputSystem.GetLocalPosition(this, e.Position);
        Hyperlink? foundLink = null;

        foreach (var pc in _positionedChars)
        {
            if (pc.Info.SourceInline is Hyperlink hl && Font != null)
            {
                ushort gIdx = Font.GetGlyphIndex(pc.Info.Character);
                float advance = Font.GetAdvanceWidth(gIdx, pc.Info.FontSize);
                Rect charRect = new Rect(pc.Position.X, pc.Position.Y, advance, pc.Info.FontSize);
                if (charRect.Contains(localPos))
                {
                    foundLink = hl;
                    break;
                }
            }
        }

        if (_hoveredHyperlink != foundLink)
        {
            _hoveredHyperlink = foundLink;
            Invalidate();
        }
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsEnabled && _hoveredHyperlink != null)
        {
            _hoveredHyperlink.RaiseClick();
            e.Handled = true;
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? availableSize.X;
        float h = HeightConstraint ?? availableSize.Y;
        if (float.IsInfinity(w)) w = 600f;
        if (float.IsInfinity(h)) h = 400f;

        PerformFlowLayout(w, h);
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        PerformFlowLayout(arrangeRect.Width, arrangeRect.Height);

        // Arrange nested child controls
        foreach (var pc in _positionedChars)
        {
            if (pc.Info.EmbeddedElement != null)
            {
                var child = pc.Info.EmbeddedElement;
                child.Arrange(new Rect(pc.Position.X, pc.Position.Y, child.DesiredSize.X, child.DesiredSize.Y));
            }
        }
    }

    private readonly List<PositionedRichChar> _positionedChars = new();
    private Hyperlink? _hoveredHyperlink = null;

    private void PerformFlowLayout(float width, float height)
    {
        _positionedChars.Clear();
        _tableDecorations.Clear();

        var allBlocks = new List<Block>();
        allBlocks.AddRange(Blocks);
        foreach (var p in Paragraphs)
        {
            if (!allBlocks.Contains(p)) allBlocks.Add(p);
        }

        if (Font == null || allBlocks.Count == 0 || width <= 0f || height <= 0f) return;

        float scale = FontSize / Font.UnitsPerEm;
        float lineSpacing = (Font.Ascender - Font.Descender + Font.LineGap) * scale;

        float availableWidth = width - Padding.Horizontal;
        float colWidth = (availableWidth - (ColumnCount - 1) * ColumnGap) / ColumnCount;
        float colHeight = height - Padding.Vertical;

        int currentColumn = 0;
        float cursorX = Padding.Left;
        float cursorY = Padding.Top;

        var defaultFg = Foreground ?? ThemeManager.GetBrush("TextPrimary", this.ActualTheme);

        var currentChildren = new List<Visual>(Children);
        var encounteredChildren = new HashSet<Visual>();

        foreach (var block in allBlocks)
        {
            var charList = new List<RichChar>();
            if (block is Paragraph paragraph)
            {
                foreach (var inline in paragraph.Inlines)
                {
                    AccumulateInlines(inline, charList, defaultFg, FontSize, false, false, false, null, 0f);
                }
            }
            else if (block is Inline inlineBlock)
            {
                AccumulateInlines(inlineBlock, charList, defaultFg, FontSize, false, false, false, null, 0f);
            }

            if (charList.Count == 0) continue;

            var paragraphLines = new List<List<PositionedRichChar>>();
            int i = 0;
            bool hasResetLineIndent = false;

            while (i < charList.Count)
            {
                var lineChars = new List<RichChar>();
                float lineW = 0f;
                int lastWordIdx = -1;

                while (i < charList.Count)
                {
                    var rc = charList[i];
                    char c = rc.Character;

                    if (c == '\n')
                    {
                        i++;
                        break;
                    }

                    if (c == '\uFFFD' && rc.SourceInline is Table table)
                    {
                        break;
                    }

                    float advance = 0f;
                    if (rc.EmbeddedElement != null)
                    {
                        var child = rc.EmbeddedElement;
                        encounteredChildren.Add(child);
                        if (child.Parent != this)
                        {
                            AddChild(child);
                        }
                        child.Measure(new Vector2(colWidth, float.PositiveInfinity));
                        advance = child.DesiredSize.X + 4f;
                    }
                    else
                    {
                        ushort gIdx = Font.GetGlyphIndex(rc.Character);
                        advance = Font.GetAdvanceWidth(gIdx, rc.FontSize);
                    }

                    if (rc.Character == ' ' || rc.Character == '\t')
                    {
                        lastWordIdx = lineChars.Count;
                    }

                    if (lineW + advance > colWidth && lineChars.Count > 0)
                    {
                        if (lastWordIdx > 0 && lastWordIdx < lineChars.Count)
                        {
                            int diff = lineChars.Count - lastWordIdx;
                            lineChars.RemoveRange(lastWordIdx, diff);
                            i -= diff;
                        }
                        break;
                    }

                    lineChars.Add(rc);
                    lineW += advance;
                    i++;
                }

                if (lineChars.Count > 0)
                {
                    // 1. Calculate dynamic line height
                    float lineMaxH = lineSpacing;
                    foreach (var rc in lineChars)
                    {
                        if (rc.EmbeddedElement != null)
                        {
                            lineMaxH = Math.Max(lineMaxH, rc.EmbeddedElement.DesiredSize.Y);
                        }
                        else
                        {
                            lineMaxH = Math.Max(lineMaxH, rc.FontSize);
                        }
                    }

                    // 2. Perform column overflow check using dynamic height
                    if (cursorY + lineMaxH > Padding.Top + colHeight && cursorY > Padding.Top)
                    {
                        currentColumn++;
                        if (currentColumn >= ColumnCount)
                        {
                            break;
                        }
                        cursorX = Padding.Left + currentColumn * (colWidth + ColumnGap);
                        cursorY = Padding.Top;
                    }

                    var currentLine = new List<PositionedRichChar>();
                    float runningX = cursorX;
                    hasResetLineIndent = false;

                    foreach (var rc in lineChars)
                    {
                        float advance = 0f;
                        float elementH = 0f;
                        if (rc.EmbeddedElement != null)
                        {
                            advance = rc.EmbeddedElement.DesiredSize.X + 4f;
                            elementH = rc.EmbeddedElement.DesiredSize.Y;
                        }
                        else
                        {
                            ushort gIdx = Font.GetGlyphIndex(rc.Character);
                            advance = Font.GetAdvanceWidth(gIdx, rc.FontSize);
                            elementH = rc.FontSize;
                        }

                        if (rc.BulletOffset == 0 && !hasResetLineIndent)
                        {
                            runningX = cursorX + rc.LeftIndent;
                            hasResetLineIndent = true;
                        }

                        float finalX = runningX;
                        if (rc.BulletOffset > 0)
                        {
                            finalX = cursorX + rc.LeftIndent - rc.BulletOffset + (runningX - cursorX);
                        }

                        // Align standard text/embedded elements symmetrically within the computed line height
                        float yOffset = (lineMaxH - elementH) / 2f;

                        currentLine.Add(new PositionedRichChar
                        {
                            Info = rc,
                            Position = new Vector2(finalX, cursorY + yOffset)
                        });
                        runningX += advance;
                    }

                    paragraphLines.Add(currentLine);
                    cursorY += lineMaxH;
                }

                if (i < charList.Count && charList[i].Character == '\uFFFD' && charList[i].SourceInline is Table tbl)
                {
                    LayoutTableFlow(tbl, ref currentColumn, ref cursorX, ref cursorY, colWidth, colHeight);
                    i++;
                    hasResetLineIndent = false;
                }
            }

            // Apply horizontal alignments inside this paragraph's lines
            for (int l = 0; l < paragraphLines.Count; l++)
            {
                var line = paragraphLines[l];
                if (line.Count == 0) continue;

                float lineW = 0f;
                var lastPc = line[^1];
                float lastAdv = 0f;
                if (lastPc.Info.EmbeddedElement != null)
                {
                    lastAdv = lastPc.Info.EmbeddedElement.DesiredSize.X + 4f;
                }
                else
                {
                    lastAdv = Font.GetAdvanceWidth(Font.GetGlyphIndex(lastPc.Info.Character), lastPc.Info.FontSize);
                }
                lineW = lastPc.Position.X + lastAdv - cursorX;

                float shiftX = 0f;
                TextAlignment align = (block is Paragraph pBlock) ? pBlock.TextAlignment : TextAlignment.Left;
                if (align == TextAlignment.Right)
                {
                    shiftX = colWidth - lineW;
                }
                else if (align == TextAlignment.Center)
                {
                    shiftX = (colWidth - lineW) / 2f;
                }
                else if (align == TextAlignment.Justify)
                {
                    bool isLastLine = (l == paragraphLines.Count - 1);
                    int spaceCount = 0;
                    for (int k = 0; k < line.Count - 1; k++)
                    {
                        if (line[k].Info.Character == ' ' || line[k].Info.Character == '\t')
                            spaceCount++;
                    }

                    if (!isLastLine && spaceCount > 0 && lineW < colWidth)
                    {
                        float extraW = colWidth - lineW;
                        float spaceAddition = extraW / spaceCount;
                        float runningAddition = 0f;
                        for (int k = 0; k < line.Count; k++)
                        {
                            var pc = line[k];
                            pc.Position.X += runningAddition;
                            if (pc.Info.Character == ' ' || pc.Info.Character == '\t')
                            {
                                runningAddition += spaceAddition;
                            }
                        }
                    }
                }

                if (shiftX > 0f && !float.IsInfinity(shiftX))
                {
                    foreach (var pc in line)
                    {
                        pc.Position.X += shiftX;
                    }
                }

                _positionedChars.AddRange(line);
            }

            cursorY += block.MarginBottom;
            if (currentColumn >= ColumnCount) break;
        }

        // Clean up children that are no longer referenced
        foreach (var child in currentChildren)
        {
            if (child is FrameworkElement fe && !encounteredChildren.Contains(fe))
            {
                RemoveChild(fe);
            }
        }
    }

    private void AccumulateInlines(Inline inline, List<RichChar> list, Brush defaultFg, float defaultSize, bool isBold, bool isItalic, bool isUnderline, Inline? parentInline, float leftIndent = 0f)
    {
        Brush fg = inline.Foreground ?? defaultFg;
        float size = inline.FontSize ?? defaultSize;
        Inline source = parentInline ?? inline;

        if (inline is Run run)
        {
            foreach (char c in run.Text)
            {
                list.Add(new RichChar
                {
                    Character = c,
                    Foreground = fg,
                    FontSize = size,
                    IsBold = isBold,
                    IsItalic = isItalic,
                    IsUnderline = isUnderline,
                    SourceInline = source,
                    LeftIndent = leftIndent
                });
            }
        }
        else if (inline is InlineUIContainer uic)
        {
            list.Add(new RichChar
            {
                Character = '\uFFFC',
                Foreground = fg,
                FontSize = size,
                IsBold = isBold,
                IsItalic = isItalic,
                IsUnderline = isUnderline,
                SourceInline = uic,
                EmbeddedElement = uic.Child,
                LeftIndent = leftIndent
            });
        }
        else if (inline is ListBlock listBlock)
        {
            int itemIdx = 1;
            foreach (var item in listBlock.Items)
            {
                if (list.Count > 0 && list[^1].Character != '\n')
                {
                    list.Add(new RichChar { Character = '\n', Foreground = fg, FontSize = size, SourceInline = item, LeftIndent = leftIndent });
                }

                string prefix = listBlock.IsOrdered ? $"{itemIdx}. " : "• ";
                itemIdx++;

                foreach (char bulletChar in prefix)
                {
                    list.Add(new RichChar
                    {
                        Character = bulletChar,
                        Foreground = fg,
                        FontSize = size,
                        IsBold = isBold,
                        IsItalic = isItalic,
                        IsUnderline = isUnderline,
                        SourceInline = item,
                        LeftIndent = leftIndent + listBlock.Indentation,
                        BulletOffset = listBlock.Indentation - 8f
                    });
                }

                foreach (var sub in item.Inlines)
                {
                    AccumulateInlines(sub, list, fg, size, isBold, isItalic, isUnderline, item, leftIndent + listBlock.Indentation);
                }
            }
        }
        else if (inline is Table table)
        {
            if (list.Count > 0 && list[^1].Character != '\n')
            {
                list.Add(new RichChar { Character = '\n', Foreground = fg, FontSize = size, SourceInline = table, LeftIndent = leftIndent });
            }

            list.Add(new RichChar
            {
                Character = '\uFFFD',
                Foreground = fg,
                FontSize = size,
                SourceInline = table,
                LeftIndent = leftIndent
            });

            list.Add(new RichChar { Character = '\n', Foreground = fg, FontSize = size, SourceInline = table, LeftIndent = leftIndent });
        }
        else if (inline is Span span)
        {
            bool nextBold = isBold || (span is Bold);
            bool nextItalic = isItalic || (span is Italic);
            bool nextUnderline = isUnderline || (span is Underline || span is Hyperlink);

            if (span is Hyperlink && inline.Foreground == null)
            {
                fg = new SolidColorBrush(0x0078D4FF);
            }

            foreach (var sub in span.Inlines)
            {
                AccumulateInlines(sub, list, fg, size, nextBold, nextItalic, nextUnderline, span is Hyperlink ? span : source, leftIndent);
            }
        }
    }

    public override void OnRender(DrawingContext context)
    {
        if (Font == null) return;

        // Draw table decorations (backgrounds and borders)
        foreach (var dec in _tableDecorations)
        {
            if (dec.Background != null)
            {
                context.DrawRectangle(dec.Background, null, dec.Rect);
            }
            if (dec.BorderBrush != null && dec.BorderThickness > 0f)
            {
                context.DrawRectangle(null, new Pen(dec.BorderBrush, dec.BorderThickness), dec.Rect);
            }
        }

        if (_positionedChars.Count == 0) return;

        // Group consecutive characters of same style for extreme text composition speed
        string runBuffer = "";
        Vector2 startPos = Vector2.Zero;
        RichChar style = default;

        foreach (var pc in _positionedChars)
        {
            if (pc.Info.EmbeddedElement != null)
            {
                if (runBuffer.Length > 0)
                {
                    RenderRun(context, runBuffer, startPos, style);
                    runBuffer = "";
                }
                continue;
            }

            var pcStyle = pc.Info;
            if (pc.Info.SourceInline is Hyperlink hl && hl == _hoveredHyperlink)
            {
                pcStyle.Foreground = new SolidColorBrush(0x005A9EFF);
            }

            if (runBuffer.Length == 0)
            {
                runBuffer = pc.Info.Character.ToString();
                startPos = pc.Position;
                style = pcStyle;
            }
            else if (pcStyle.IsBold == style.IsBold &&
                     pcStyle.IsItalic == style.IsItalic &&
                     pcStyle.IsUnderline == style.IsUnderline &&
                     pcStyle.FontSize == style.FontSize &&
                     pcStyle.Foreground.Equals(style.Foreground) &&
                     pcStyle.SourceInline == style.SourceInline &&
                     Math.Abs(pc.Position.Y - startPos.Y) < 1f)
            {
                runBuffer += pc.Info.Character;
            }
            else
            {
                RenderRun(context, runBuffer, startPos, style);
                runBuffer = pc.Info.Character.ToString();
                startPos = pc.Position;
                style = pcStyle;
            }
        }

        if (runBuffer.Length > 0)
        {
            RenderRun(context, runBuffer, startPos, style);
        }

        base.OnRender(context);
    }

    private void RenderRun(DrawingContext context, string runBuffer, Vector2 startPos, RichChar style)
    {
        if (Font == null) return;
        context.DrawText(runBuffer, Font, style.FontSize, style.Foreground, startPos, style.IsBold, style.IsItalic);
        if (style.IsUnderline)
        {
            float runW = 0f;
            foreach (char c in runBuffer)
            {
                runW += Font.GetAdvanceWidth(Font.GetGlyphIndex(c), style.FontSize);
            }
            context.DrawRectangle(style.Foreground, null, new Rect(startPos.X, startPos.Y + style.FontSize - 1f, runW, 1f));
        }
    }

    private List<PositionedRichChar> LayoutCellChars(TableCell cell, float cellWidth, float cellPadding, out float cellHeight)
    {
        var positionedChars = new List<PositionedRichChar>();
        cellHeight = cellPadding * 2f;
        if (Font == null) return positionedChars;

        var charList = new List<RichChar>();
        var defaultFg = Foreground ?? ThemeManager.GetBrush("TextPrimary", this.ActualTheme);
        foreach (var inline in cell.Inlines)
        {
            AccumulateInlines(inline, charList, defaultFg, FontSize, false, false, false, null, 0f);
        }

        if (charList.Count == 0) return positionedChars;

        float scale = FontSize / Font.UnitsPerEm;
        float lineSpacing = (Font.Ascender - Font.Descender + Font.LineGap) * scale;

        float cursorX = cellPadding;
        float cursorY = cellPadding;
        float maxTextW = cellWidth - cellPadding * 2f;

        var lines = new List<List<PositionedRichChar>>();
        var currentLine = new List<PositionedRichChar>();
        int lastWordStart = -1;
        float lastWordStartCursorX = cellPadding;

        for (int i = 0; i < charList.Count; i++)
        {
            var rc = charList[i];
            char c = rc.Character;

            if (c == '\n')
            {
                lines.Add(currentLine);
                currentLine = new List<PositionedRichChar>();
                cursorX = cellPadding;
                cursorY += lineSpacing;
                lastWordStart = -1;
                continue;
            }

            float advance = 0f;
            if (rc.EmbeddedElement != null)
            {
                rc.EmbeddedElement.Measure(new Vector2(maxTextW, float.PositiveInfinity));
                advance = rc.EmbeddedElement.DesiredSize.X + 4f;
            }
            else
            {
                ushort gIdx = Font.GetGlyphIndex(c);
                advance = Font.GetAdvanceWidth(gIdx, rc.FontSize);
            }

            if (c == ' ' || c == '\t')
            {
                lastWordStart = -1;
            }
            else if (lastWordStart == -1)
            {
                lastWordStart = currentLine.Count;
                lastWordStartCursorX = cursorX;
            }

            if (cursorX + advance > cellWidth - cellPadding && cursorX > cellPadding)
            {
                if (lastWordStart > 0)
                {
                    int wrapCount = currentLine.Count - lastWordStart;
                    var wrapped = currentLine.GetRange(lastWordStart, wrapCount);
                    currentLine.RemoveRange(lastWordStart, wrapCount);

                    lines.Add(currentLine);
                    currentLine = new List<PositionedRichChar>();

                    cursorX = cellPadding;
                    cursorY += lineSpacing;

                    foreach (var wc in wrapped)
                    {
                        var remapped = wc;
                        float shift = wc.Position.X - lastWordStartCursorX;
                        remapped.Position = new Vector2(cellPadding + shift, cursorY);
                        currentLine.Add(remapped);

                        float wAdv = 0f;
                        if (remapped.Info.EmbeddedElement != null)
                        {
                            wAdv = remapped.Info.EmbeddedElement.DesiredSize.X + 4f;
                        }
                        else
                        {
                            ushort wIdx = Font.GetGlyphIndex(remapped.Info.Character);
                            wAdv = Font.GetAdvanceWidth(wIdx, remapped.Info.FontSize);
                        }
                        cursorX = cellPadding + shift + wAdv;
                    }

                    var pos = new Vector2(cursorX, cursorY);
                    currentLine.Add(new PositionedRichChar { Info = rc, Position = pos });
                    cursorX += advance;
                    lastWordStart = 0;
                    lastWordStartCursorX = cellPadding;
                    continue;
                }
                else
                {
                    lines.Add(currentLine);
                    currentLine = new List<PositionedRichChar>();
                    cursorX = cellPadding;
                    cursorY += lineSpacing;
                }
            }

            var charPos = new Vector2(cursorX, cursorY);
            currentLine.Add(new PositionedRichChar { Info = rc, Position = charPos });
            cursorX += advance;
        }

        if (currentLine.Count > 0)
        {
            lines.Add(currentLine);
        }

        foreach (var line in lines)
        {
            positionedChars.AddRange(line);
        }

        if (positionedChars.Count > 0)
        {
            float maxCharY = 0f;
            foreach (var pc in positionedChars)
            {
                maxCharY = Math.Max(maxCharY, pc.Position.Y + pc.Info.FontSize);
            }
            cellHeight = maxCharY + cellPadding;
        }

        return positionedChars;
    }

    private void LayoutTableFlow(Table table, ref int currentColumn, ref float cursorX, ref float cursorY, float colWidth, float colHeight)
    {
        int numCols = 0;
        foreach (var row in table.Rows)
        {
            numCols = Math.Max(numCols, row.Cells.Count);
        }
        if (numCols == 0) return;

        float[] colWidths = new float[numCols];
        float remainingW = colWidth;
        if (table.ColumnWidths != null && table.ColumnWidths.Count > 0)
        {
            for (int col = 0; col < numCols; col++)
            {
                if (col < table.ColumnWidths.Count)
                {
                    colWidths[col] = table.ColumnWidths[col];
                }
                else
                {
                    colWidths[col] = remainingW / (numCols - col);
                }
                remainingW -= colWidths[col];
            }
        }
        else
        {
            float eqW = remainingW / numCols;
            for (int col = 0; col < numCols; col++)
            {
                colWidths[col] = eqW;
            }
        }

        foreach (var row in table.Rows)
        {
            var rowCellChars = new List<List<PositionedRichChar>>();
            float[] cellHeights = new float[row.Cells.Count];

            for (int col = 0; col < row.Cells.Count; col++)
            {
                var cell = row.Cells[col];
                float colW = colWidths[col];
                var pcList = LayoutCellChars(cell, colW, table.CellPadding, out float cHeight);
                rowCellChars.Add(pcList);
                cellHeights[col] = cHeight;
            }

            float rowHeight = 0f;
            foreach (float ch in cellHeights)
            {
                rowHeight = Math.Max(rowHeight, ch);
            }
            if (rowHeight == 0f) rowHeight = FontSize + table.CellPadding * 2f;

            if (cursorY + rowHeight > Padding.Top + colHeight)
            {
                currentColumn++;
                if (currentColumn >= ColumnCount)
                {
                    break;
                }
                cursorX = Padding.Left + currentColumn * (colWidth + ColumnGap);
                cursorY = Padding.Top;
            }

            float currentCellX = cursorX;
            for (int col = 0; col < row.Cells.Count; col++)
            {
                var cell = row.Cells[col];
                float colW = colWidths[col];
                var cellRect = new Rect(currentCellX, cursorY, colW, rowHeight);

                _tableDecorations.Add(new TableVisualDecoration
                {
                    Rect = cellRect,
                    Background = cell.Background,
                    BorderThickness = table.BorderThickness,
                    BorderBrush = table.BorderBrush
                });

                var pcList = rowCellChars[col];
                foreach (var pc in pcList)
                {
                    var remapped = new PositionedRichChar
                    {
                        Info = pc.Info,
                        Position = new Vector2(pc.Position.X + currentCellX, pc.Position.Y + cursorY)
                    };
                    _positionedChars.Add(remapped);
                }

                currentCellX += colW;
            }

            cursorY += rowHeight;
        }
    }
}
