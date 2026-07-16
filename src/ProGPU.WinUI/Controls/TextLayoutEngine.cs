using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls
{
    public static class TextLayoutEngine
    {
        private static readonly SolidColorBrush HyperlinkBrush = new SolidColorBrush(0x0078D4FF);
        private static readonly SolidColorBrush SelectionHighlightBrush = new SolidColorBrush(0x0078D435);
        private static readonly SolidColorBrush HoveredHyperlinkBrush = new SolidColorBrush(0x005A9EFF);

        public static void AccumulateInlines(
            Inline inline, 
            List<RichChar> list, 
            Brush defaultFg, 
            float defaultSize, 
            bool isBold, 
            bool isItalic, 
            bool isUnderline, 
            ElementTheme theme,
            Inline? parentInline = null, 
            float leftIndent = 0f,
            TtfFont? parentFont = null)
        {
            Brush fg = inline.Foreground ?? defaultFg;
            if (fg is ThemeResourceBrush trBrush)
            {
                fg = ThemeManager.GetBrush(trBrush.ResourceKey, theme);
            }
            float size = inline.FontSize ?? defaultSize;
            TtfFont? font = inline.Font ?? parentFont;
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
                        Font = font,
                        IsBold = isBold,
                        IsItalic = isItalic,
                        IsUnderline = isUnderline,
                        SourceInline = source,
                        LeftIndent = leftIndent
                    });
                }
            }
            else if (inline is LineBreak)
            {
                list.Add(new RichChar
                {
                    Character = '\n',
                    Foreground = fg,
                    FontSize = size,
                    Font = font,
                    IsBold = isBold,
                    IsItalic = isItalic,
                    IsUnderline = isUnderline,
                    SourceInline = source,
                    LeftIndent = leftIndent
                });
            }
            else if (inline is InlineUIContainer uic)
            {
                list.Add(new RichChar
                {
                    Character = '\uFFFC',
                    Foreground = fg,
                    FontSize = size,
                    Font = font,
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
                        list.Add(new RichChar { Character = '\n', Foreground = fg, FontSize = size, Font = font, SourceInline = item, LeftIndent = leftIndent });
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
                            Font = font,
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
                        AccumulateInlines(sub, list, fg, size, isBold, isItalic, isUnderline, theme, item, leftIndent + listBlock.Indentation, font);
                    }
                }
            }
            else if (inline is Table table)
            {
                if (list.Count > 0 && list[^1].Character != '\n')
                {
                    list.Add(new RichChar { Character = '\n', Foreground = fg, FontSize = size, Font = font, SourceInline = table, LeftIndent = leftIndent });
                }

                list.Add(new RichChar
                {
                    Character = '\uFFFD',
                    Foreground = fg,
                    FontSize = size,
                    Font = font,
                    SourceInline = table,
                    LeftIndent = leftIndent
                });

                list.Add(new RichChar { Character = '\n', Foreground = fg, FontSize = size, Font = font, SourceInline = table, LeftIndent = leftIndent });
            }
            else if (inline is Span span)
            {
                bool nextBold = isBold || (span is Bold);
                bool nextItalic = isItalic || (span is Italic);
                bool nextUnderline = isUnderline || (span is Underline || span is Hyperlink);

                if (span is Hyperlink && inline.Foreground == null)
                {
                    fg = HyperlinkBrush;
                }

                foreach (var sub in span.Inlines)
                {
                    AccumulateInlines(sub, list, fg, size, nextBold, nextItalic, nextUnderline, theme, span is Hyperlink ? span : source, leftIndent, font);
                }
            }
        }

        public static float LayoutSingleColumn(
            List<Inline> inlines,
            float maxWidth,
            Thickness padding,
            TtfFont activeFont,
            float baseFontSize,
            Brush? defaultFg,
            TextAlignment alignment,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations,
            FrameworkElement parent,
            Action<Visual> addChild,
            Action<Visual> removeChild)
        {
            var blocks = new List<Block> { new Paragraph(inlines.ToArray()) { MarginBottom = 0f } };
            return LayoutSingleColumn(
                blocks,
                maxWidth,
                padding,
                activeFont,
                baseFontSize,
                defaultFg,
                alignment,
                theme,
                positionedChars,
                tableDecorations,
                parent,
                addChild,
                removeChild);
        }        private static int GetInlinesLength(IEnumerable<Inline> inlines)
        {
            int len = 0;
            foreach (var inline in inlines)
            {
                if (inline is Run run) len += run.Text.Length;
                else if (inline is Span span) len += GetInlinesLength(span.Inlines);
                else if (inline is LineBreak) len += 1;
            }
            return len;
        }

        private static int CountLineBreaks(IEnumerable<Inline> inlines)
        {
            int count = 0;
            foreach (var inline in inlines)
            {
                if (inline is LineBreak) count++;
                else if (inline is Span span) count += CountLineBreaks(span.Inlines);
            }
            return count;
        }

        private static float EstimateBlockHeight(Block block, float availableWidth, float baseFontSize, TtfFont activeFont)
        {
            float scale = baseFontSize / activeFont.UnitsPerEm;
            float lineSpacing = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * scale;
            
            if (block is Paragraph paragraph)
            {
                int charCount = GetInlinesLength(paragraph.Inlines);
                if (charCount == 0) return block.MarginBottom;
                
                // Detect the maximum font size in children runs/spans
                float maxFontSize = baseFontSize;
                foreach (var inline in paragraph.Inlines)
                {
                    if (inline.FontSize.HasValue)
                    {
                        maxFontSize = Math.Max(maxFontSize, inline.FontSize.Value);
                    }
                    if (inline is Span span)
                    {
                        foreach (var sub in span.Inlines)
                        {
                            if (sub.FontSize.HasValue)
                            {
                                maxFontSize = Math.Max(maxFontSize, sub.FontSize.Value);
                            }
                        }
                    }
                }

                float avgCharWidth = maxFontSize * 0.49f;
                float charsPerLine = Math.Max(10f, availableWidth / avgCharWidth);
                int estimatedLines = (int)Math.Ceiling(charCount / charsPerLine);
                
                int lineBreaks = CountLineBreaks(paragraph.Inlines);
                estimatedLines = Math.Max(estimatedLines, lineBreaks + 1);
                
                float blockLineSpacing = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * (maxFontSize / activeFont.UnitsPerEm);

                float embeddedHeight = 0f;
                foreach (var inline in paragraph.Inlines)
                {
                    if (inline is InlineUIContainer uic && uic.Child != null)
                    {
                        if (uic.Child.HeightConstraint.HasValue)
                        {
                            embeddedHeight += uic.Child.HeightConstraint.Value;
                        }
                        else if (uic.Child is Border border && border.Child is RichTextBlock rtb)
                        {
                            int codeChars = GetInlinesLength(rtb.Inlines);
                            embeddedHeight += Math.Max(40f, (codeChars / 50f) * blockLineSpacing + 20f);
                        }
                        else if (uic.Child is Border border2 && border2.Child is StackPanel quoteStack)
                        {
                            float quoteHeight = 10f;
                            foreach (var quoteChild in quoteStack.Children)
                            {
                                if (quoteChild is RichTextBlock qRtb)
                                {
                                    int quoteChars = GetInlinesLength(qRtb.Inlines);
                                    quoteHeight += Math.Max(20f, (quoteChars / 60f) * blockLineSpacing + 6f);
                                }
                            }
                            embeddedHeight += quoteHeight;
                        }
                        else
                        {
                            embeddedHeight += 100f;
                        }
                    }
                }
                
                if (embeddedHeight > 0f)
                {
                    return embeddedHeight + block.MarginBottom;
                }
                
                return estimatedLines * blockLineSpacing + block.MarginBottom;
            }
            else if (block is ListBlock listBlock)
            {
                float listHeight = 0f;
                foreach (var item in listBlock.Items)
                {
                    int itemCharCount = GetInlinesLength(item.Inlines);
                    float avgCharWidth = baseFontSize * 0.49f;
                    float charsPerLine = Math.Max(10f, (availableWidth - listBlock.Indentation) / avgCharWidth);
                    int estimatedLines = (int)Math.Ceiling(itemCharCount / charsPerLine);
                    listHeight += Math.Max(1, estimatedLines) * lineSpacing;
                }
                return listHeight + block.MarginBottom;
            }
            else if (block is Table table)
            {
                float tableHeight = 10f;
                foreach (var row in table.Rows)
                {
                    float rowHeight = baseFontSize + table.CellPadding * 2f;
                    int maxCellChars = 0;
                    foreach (var cell in row.Cells)
                    {
                        maxCellChars = Math.Max(maxCellChars, GetInlinesLength(cell.Inlines));
                    }
                    float cellWidth = availableWidth / Math.Max(1, row.Cells.Count);
                    float charsPerLine = Math.Max(5f, cellWidth / (baseFontSize * 0.49f));
                    int cellLines = (int)Math.Ceiling(maxCellChars / charsPerLine);
                    rowHeight = Math.Max(rowHeight, Math.Max(1, cellLines) * lineSpacing + table.CellPadding * 2f);
                    
                    tableHeight += rowHeight;
                }
                return tableHeight + block.MarginBottom;
            }
            
            return 30f + block.MarginBottom;
        }

        private static void LayoutBlock(
            Block block,
            float startY,
            float maxWidth,
            Thickness padding,
            TtfFont activeFont,
            float baseFontSize,
            Brush resolvedFg,
            TextAlignment alignment,
            ElementTheme theme,
            FrameworkElement parent,
            Action<Visual> addChild,
            HashSet<Visual> encounteredChildren,
            List<PositionedRichChar> blockChars,
            List<TableVisualDecoration> blockDecorations)
        {
            blockChars.Clear();
            blockDecorations.Clear();

            float cursorX = padding.Left;
            float cursorY = startY;
            float availableWidth = maxWidth - padding.Horizontal;
            float scale = baseFontSize / activeFont.UnitsPerEm;
            float lineSpacing = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * scale;

            var charList = new List<RichChar>();
            if (block is Paragraph paragraph)
            {
                foreach (var inline in paragraph.Inlines)
                {
                    AccumulateInlines(inline, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
                }
            }
            else if (block is Inline inlineBlock)
            {
                AccumulateInlines(inlineBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
            }
            else if (block is ListBlock listBlock)
            {
                AccumulateInlines(listBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
            }
            else if (block is Table tableBlock)
            {
                AccumulateInlines(tableBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
            }

            if (charList.Count == 0)
            {
                block.CachedHeight = block.MarginBottom;
                return;
            }

            var currentLine = new List<PositionedRichChar>();
            int lastWordStart = -1;
            float lastWordStartCursorX = padding.Left;
            bool hasResetLineIndent = false;

            void CommitLine(List<PositionedRichChar> line, bool isLastLine)
            {
                if (line.Count == 0)
                {
                    cursorY += lineSpacing;
                    return;
                }

                float completedLineHeight = 0f;
                foreach (var pc in line)
                {
                    float h;
                    if (pc.Info.EmbeddedElement != null)
                    {
                        h = pc.Info.EmbeddedElement.DesiredSize.Y;
                    }
                    else
                    {
                        TtfFont charFont = pc.Info.Font ?? activeFont;
                        float charScale = pc.Info.FontSize / charFont.UnitsPerEm;
                        h = (charFont.Ascender - charFont.Descender + charFont.LineGap) * charScale;
                    }
                    completedLineHeight = Math.Max(completedLineHeight, h);
                }
                if (completedLineHeight == 0f) completedLineHeight = lineSpacing;

                foreach (var pc in line)
                {
                    float h;
                    if (pc.Info.EmbeddedElement != null)
                    {
                        h = pc.Info.EmbeddedElement.DesiredSize.Y;
                    }
                    else
                    {
                        TtfFont charFont = pc.Info.Font ?? activeFont;
                        float charScale = pc.Info.FontSize / charFont.UnitsPerEm;
                        h = (charFont.Ascender - charFont.Descender + charFont.LineGap) * charScale;
                    }
                    pc.Position.Y = cursorY + (completedLineHeight - h) / 2f;
                }

                float lineW = 0f;
                var lastPc = line[^1];
                float lastAdv = 0f;
                if (lastPc.Info.EmbeddedElement != null)
                {
                    lastAdv = lastPc.Info.EmbeddedElement.DesiredSize.X + 4f;
                }
                else
                {
                    TtfFont charFont = lastPc.Info.Font ?? activeFont;
                    lastAdv = charFont.GetAdvanceWidth(charFont.GetGlyphIndex(lastPc.Info.Character), lastPc.Info.FontSize);
                }
                lineW = lastPc.Position.X + lastAdv - padding.Left;

                float shiftX = 0f;
                TextAlignment align = (block is Paragraph pBlock) ? pBlock.TextAlignment : alignment;
                if (align == TextAlignment.Right)
                {
                    shiftX = availableWidth - lineW;
                }
                else if (align == TextAlignment.Center)
                {
                    shiftX = (availableWidth - lineW) / 2f;
                }
                else if (align == TextAlignment.Justify)
                {
                    int spaceCount = 0;
                    for (int k = 0; k < line.Count - 1; k++)
                    {
                        if (line[k].Info.Character == ' ' || line[k].Info.Character == '\t')
                            spaceCount++;
                    }

                    if (!isLastLine && spaceCount > 0 && lineW < availableWidth)
                    {
                        float extraW = availableWidth - lineW;
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

                blockChars.AddRange(line);
                cursorY += completedLineHeight;
            }

            for (int i = 0; i < charList.Count; i++)
            {
                var rc = charList[i];
                char c = rc.Character;

                if (c == '\n')
                {
                    CommitLine(currentLine, true);
                    currentLine = new List<PositionedRichChar>();
                    cursorX = padding.Left;
                    lastWordStart = -1;
                    hasResetLineIndent = false;
                    continue;
                }

                if (c == '\uFFFD' && rc.SourceInline is Table table)
                {
                    if (currentLine.Count > 0)
                    {
                        CommitLine(currentLine, true);
                        currentLine = new List<PositionedRichChar>();
                    }
                    cursorX = padding.Left;
                    LayoutTable(table, ref cursorY, availableWidth, rc.LeftIndent, padding, baseFontSize, activeFont, theme, blockChars, blockDecorations);
                    lastWordStart = -1;
                    hasResetLineIndent = false;
                    continue;
                }

                float advance = 0f;
                if (rc.EmbeddedElement != null)
                {
                    var child = rc.EmbeddedElement;
                    encounteredChildren.Add(child);
                    if (child.Parent != parent)
                    {
                        addChild(child);
                    }
                    child.Measure(new Vector2(availableWidth, float.PositiveInfinity));
                    advance = child.DesiredSize.X + 4f;
                }
                else
                {
                    TtfFont charFont = rc.Font ?? activeFont;
                    ushort gIdx = charFont.GetGlyphIndex(c);
                    advance = charFont.GetAdvanceWidth(gIdx, rc.FontSize);
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

                if (cursorX + advance > maxWidth - padding.Right && cursorX > padding.Left + rc.LeftIndent)
                {
                    if (lastWordStart > 0)
                    {
                        int wrapCount = currentLine.Count - lastWordStart;
                        var wrapped = currentLine.GetRange(lastWordStart, wrapCount);
                        currentLine.RemoveRange(lastWordStart, wrapCount);

                        CommitLine(currentLine, false);
                        currentLine = new List<PositionedRichChar>();

                        float wrapStart = padding.Left + (wrapped.Count > 0 ? wrapped[0].Info.LeftIndent : rc.LeftIndent);
                        cursorX = wrapStart;
                        hasResetLineIndent = true;

                        foreach (var wc in wrapped)
                        {
                            var remapped = wc;
                            float shift = wc.Position.X - lastWordStartCursorX;
                            remapped.Position = new Vector2(wrapStart + shift, cursorY);
                            currentLine.Add(remapped);
                            
                            float wAdv = 0f;
                            if (remapped.Info.EmbeddedElement != null)
                            {
                                wAdv = remapped.Info.EmbeddedElement.DesiredSize.X + 4f;
                            }
                            else
                            {
                                TtfFont wrappedFont = remapped.Info.Font ?? activeFont;
                                ushort wIdx = wrappedFont.GetGlyphIndex(remapped.Info.Character);
                                wAdv = wrappedFont.GetAdvanceWidth(wIdx, remapped.Info.FontSize);
                            }
                            cursorX = wrapStart + shift + wAdv;
                        }

                        if (rc.BulletOffset == 0 && !hasResetLineIndent)
                        {
                            cursorX = padding.Left + rc.LeftIndent;
                            hasResetLineIndent = true;
                        }
                        float finalXVal = cursorX;
                        if (rc.BulletOffset > 0)
                        {
                            finalXVal = padding.Left + rc.LeftIndent - rc.BulletOffset + (cursorX - padding.Left);
                        }
                        var pos = new Vector2(finalXVal, cursorY);
                        currentLine.Add(new PositionedRichChar { Info = rc, Position = pos });
                        cursorX += advance;
                        lastWordStart = 0;
                        lastWordStartCursorX = padding.Left + rc.LeftIndent;
                        continue;
                    }
                    else
                    {
                        CommitLine(currentLine, false);
                        currentLine = new List<PositionedRichChar>();
                        float wrapStart = padding.Left + rc.LeftIndent;
                        cursorX = wrapStart;
                        hasResetLineIndent = true;
                    }
                }

                if (rc.BulletOffset == 0 && !hasResetLineIndent)
                {
                    cursorX = padding.Left + rc.LeftIndent;
                    hasResetLineIndent = true;
                }
                float finalX = cursorX;
                if (rc.BulletOffset > 0)
                {
                    finalX = padding.Left + rc.LeftIndent - rc.BulletOffset + (cursorX - padding.Left);
                }
                var charPos = new Vector2(finalX, cursorY);
                currentLine.Add(new PositionedRichChar { Info = rc, Position = charPos });
                cursorX += advance;
            }

            if (currentLine.Count > 0)
            {
                CommitLine(currentLine, true);
            }

            block.CachedHeight = (cursorY - startY) + block.MarginBottom;
        }

        public static float LayoutSingleColumn(
            List<Block> blocks,
            float maxWidth,
            Thickness padding,
            TtfFont activeFont,
            float baseFontSize,
            Brush? defaultFg,
            TextAlignment alignment,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations,
            FrameworkElement parent,
            Action<Visual> addChild,
            Action<Visual> removeChild)
        {
            positionedChars.Clear();
            tableDecorations.Clear();

            var currentChildren = new List<Visual>(parent.Children);
            if (activeFont == null || blocks.Count == 0)
            {
                foreach (var child in currentChildren)
                {
                    removeChild(child);
                }
                return 0f;
            }

            var resolvedFg = defaultFg ?? ThemeManager.GetBrush("TextPrimary", theme);
            var encounteredChildren = new HashSet<Visual>();

            // Global invalidation if width constraint or theme changes
            foreach (var block in blocks)
            {
                if (Math.Abs(block.CachedWidthConstraint - maxWidth) > 0.01f || block.CachedTheme != theme)
                {
                    block.IsLayoutValid = false;
                    block.CachedHeight = -1f;
                    block.CachedChars.Clear();
                    block.CachedChars.TrimExcess();
                    block.CachedDecorations.Clear();
                    block.CachedDecorations.TrimExcess();
                }
            }

            // 1. Locate ScrollViewer ancestor and compute relative Y offset for viewport virtualization
            ScrollViewer? scrollViewer = null;
            float relativeY = 0f;
            var current = parent.Parent;
            var visualChild = (Visual)parent;
            while (current != null)
            {
                if (current is ScrollViewer sv)
                {
                    scrollViewer = sv;
                    break;
                }
                relativeY += visualChild.Offset.Y;
                visualChild = current;
                current = current.Parent;
            }

            float visibleTop = 0f;
            float visibleBottom = float.PositiveInfinity;

            // 2. Anchor scroll offset to prevent jumpiness on dynamic height refinement
            Block? anchorBlock = null;
            float anchorRelativeOffset = 0f;
            float initialAnchorY = 0f;

            float availableWidth = maxWidth - padding.Horizontal;
            float cursorY = padding.Top;

            int iterations = 0;
            while (iterations < 2)
            {
                if (scrollViewer != null)
                {
                    float viewportTop = scrollViewer.VerticalOffset;
                    float viewportHeight = scrollViewer.Size.Y > 0f ? scrollViewer.Size.Y : 800f; // Default fallback to 800px if not arranged yet
                    float buffer = Math.Max(1000f, viewportHeight * 2.0f); // 2.0 viewport pre-fetch buffer
                    visibleTop = Math.Max(0f, viewportTop - buffer);
                    visibleBottom = viewportTop + viewportHeight + buffer;
                }

                if (scrollViewer != null && iterations == 0)
                {
                    float currentScrollY = scrollViewer.VerticalOffset - relativeY;
                    foreach (var block in blocks)
                    {
                        if (block.CachedHeight > 0f)
                        {
                            if (block.CachedYOffset <= currentScrollY && block.CachedYOffset + block.CachedHeight > currentScrollY)
                            {
                                anchorBlock = block;
                                anchorRelativeOffset = currentScrollY - block.CachedYOffset;
                                initialAnchorY = block.CachedYOffset;
                                break;
                            }
                        }
                    }
                }

                cursorY = padding.Top;
                encounteredChildren.Clear();

                // Pass 1: Offset assignment and block-level measurement (lazy / viewport-driven)
                foreach (var block in blocks)
                {
                    block.CachedYOffset = cursorY;

                    // Detect visible intersection using current height (cached actual or estimated fallback) offset by relativeY
                    float absoluteY = relativeY + cursorY;
                    float currentHeight = block.CachedHeight > 0f ? block.CachedHeight : EstimateBlockHeight(block, availableWidth, baseFontSize, activeFont);
                    bool intersects = (absoluteY + currentHeight >= visibleTop) && (absoluteY <= visibleBottom);

                    if (intersects)
                    {
                        bool isCacheValid = block.IsLayoutValid && 
                                           Math.Abs(block.CachedWidthConstraint - maxWidth) < 0.01f && 
                                           block.CachedTheme == theme;

                        if (!isCacheValid)
                        {
                            LayoutBlock(block, cursorY, maxWidth, padding, activeFont, baseFontSize, resolvedFg, alignment, theme, parent, addChild, encounteredChildren, block.CachedChars, block.CachedDecorations);
                            block.IsLayoutValid = true;
                            block.CachedWidthConstraint = maxWidth;
                            block.CachedTheme = theme;
                        }
                        cursorY += block.CachedHeight;
                    }
                    else
                    {
                        // Off-screen: clear cached characters aggressively to free memory immediately!
                        if (block.IsLayoutValid)
                        {
                            block.IsLayoutValid = false;
                            block.CachedChars.Clear();
                            block.CachedChars.TrimExcess();
                            block.CachedDecorations.Clear();
                            block.CachedDecorations.TrimExcess();
                        }
                        if (block.CachedHeight <= 0f)
                        {
                            block.CachedHeight = EstimateBlockHeight(block, availableWidth, baseFontSize, activeFont);
                        }
                        cursorY += block.CachedHeight;
                    }
                }

                // Adjust scroll anchoring if preceding block measurements caused absolute shifting
                bool anchorShifted = false;
                if (scrollViewer != null && anchorBlock != null)
                {
                    float newAnchorY = anchorBlock.CachedYOffset;
                    float deltaY = newAnchorY - initialAnchorY;
                    if (Math.Abs(deltaY) > 0.1f)
                    {
                        scrollViewer.VerticalOffset = newAnchorY + anchorRelativeOffset + relativeY;
                        anchorShifted = true;
                    }
                }

                if (!anchorShifted)
                {
                    break;
                }
                iterations++;
            }

            // Pass 2: Gather visible chars and decorations, and measure any newly visible blocks
            foreach (var block in blocks)
            {
                float blockTop = block.CachedYOffset;
                float blockBottom = blockTop + block.CachedHeight;

                float absoluteTop = relativeY + blockTop;
                float absoluteBottom = relativeY + blockBottom;

                bool intersects = (absoluteBottom >= visibleTop) && (absoluteTop <= visibleBottom);
                if (intersects)
                {
                    if (!block.IsLayoutValid || Math.Abs(block.CachedWidthConstraint - maxWidth) > 0.01f || block.CachedTheme != theme)
                    {
                        LayoutBlock(block, blockTop, maxWidth, padding, activeFont, baseFontSize, resolvedFg, alignment, theme, parent, addChild, encounteredChildren, block.CachedChars, block.CachedDecorations);
                        block.IsLayoutValid = true;
                        block.CachedWidthConstraint = maxWidth;
                        block.CachedTheme = theme;
                    }

                    positionedChars.AddRange(block.CachedChars);
                    tableDecorations.AddRange(block.CachedDecorations);

                    // Ensure all embedded elements in this block are marked as encountered so they are not recycled
                    foreach (var pc in block.CachedChars)
                    {
                        if (pc.Info.EmbeddedElement != null)
                        {
                            var child = pc.Info.EmbeddedElement;
                            encounteredChildren.Add(child);
                            if (child.Parent != parent)
                            {
                                addChild(child);
                            }
                            child.Measure(new Vector2(availableWidth, float.PositiveInfinity));
                        }
                    }
                }
            }

            // Cleanup recycled off-screen UI controls
            foreach (var child in currentChildren)
            {
                if (child is FrameworkElement fe && !encounteredChildren.Contains(fe))
                {
                    removeChild(fe);
                }
            }

            return cursorY + padding.Bottom;
        }

        private static void LayoutTable(
            Table table, 
            ref float cursorY, 
            float availableWidth, 
            float leftIndent,
            Thickness padding,
            float baseFontSize,
            TtfFont activeFont,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations)
        {
            int numCols = 0;
            foreach (var row in table.Rows)
            {
                numCols = Math.Max(numCols, row.Cells.Count);
            }
            if (numCols == 0) return;

            float[] colWidths = new float[numCols];
            float remainingW = availableWidth - leftIndent;
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
                    var pcList = LayoutCellChars(cell, colW, table.CellPadding, out float cHeight, baseFontSize, activeFont, theme);
                    rowCellChars.Add(pcList);
                    cellHeights[col] = cHeight;
                }

                float rowHeight = 0f;
                foreach (float ch in cellHeights)
                {
                    rowHeight = Math.Max(rowHeight, ch);
                }
                if (rowHeight == 0f) rowHeight = baseFontSize + table.CellPadding * 2f;

                float currentCellX = padding.Left + leftIndent;
                for (int col = 0; col < row.Cells.Count; col++)
                {
                    var cell = row.Cells[col];
                    float colW = colWidths[col];
                    var cellRect = new Rect(currentCellX, cursorY, colW, rowHeight);

                    var cellBg = cell.Background;
                    if (cellBg is ThemeResourceBrush trBrush)
                    {
                        cellBg = ThemeManager.GetBrush(trBrush.ResourceKey, theme);
                    }
                    var tableBorderBrush = table.BorderBrush;
                    if (tableBorderBrush is ThemeResourceBrush borderTrBrush)
                    {
                        tableBorderBrush = ThemeManager.GetBrush(borderTrBrush.ResourceKey, theme);
                    }

                    tableDecorations.Add(new TableVisualDecoration
                    {
                        Rect = cellRect,
                        Background = cellBg,
                        BorderThickness = table.BorderThickness,
                        BorderBrush = tableBorderBrush
                    });

                    var pcList = rowCellChars[col];
                    foreach (var pc in pcList)
                    {
                        var remapped = new PositionedRichChar
                        {
                            Info = pc.Info,
                            Position = new Vector2(pc.Position.X + currentCellX, pc.Position.Y + cursorY)
                        };
                        positionedChars.Add(remapped);
                    }

                    currentCellX += colW;
                }

                cursorY += rowHeight;
            }
        }

        private static List<PositionedRichChar> LayoutCellChars(
            TableCell cell, 
            float cellWidth, 
            float cellPadding, 
            out float cellHeight,
            float baseFontSize,
            TtfFont activeFont,
            ElementTheme theme)
        {
            var positionedChars = new List<PositionedRichChar>();
            cellHeight = cellPadding * 2f;
            if (activeFont == null) return positionedChars;

            var charList = new List<RichChar>();
            var defaultFg = ThemeManager.GetBrush("TextPrimary", theme);
            foreach (var inline in cell.Inlines)
            {
                AccumulateInlines(inline, charList, defaultFg, baseFontSize, false, false, false, theme, null, 0f);
            }

            if (charList.Count == 0) return positionedChars;

            float scale = baseFontSize / activeFont.UnitsPerEm;
            float lineSpacing = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * scale;

            float cursorX = cellPadding;
            float cursorY = cellPadding;
            float maxTextW = cellWidth - cellPadding * 2f;

            var currentLine = new List<PositionedRichChar>();
            int lastWordStart = -1;
            float lastWordStartCursorX = cellPadding;

            void CommitCellLine(List<PositionedRichChar> line)
            {
                if (line.Count == 0)
                {
                    cursorY += lineSpacing;
                    return;
                }

                float maxElementHeight = 0f;
                foreach (var pc in line)
                {
                    if (pc.Info.EmbeddedElement != null)
                    {
                        maxElementHeight = Math.Max(maxElementHeight, pc.Info.EmbeddedElement.DesiredSize.Y);
                    }
                }
                float completedLineHeight = Math.Max(lineSpacing, maxElementHeight);

                foreach (var pc in line)
                {
                    float h = pc.Info.EmbeddedElement != null ? pc.Info.EmbeddedElement.DesiredSize.Y : lineSpacing;
                    pc.Position.Y = cursorY + (completedLineHeight - h) / 2f;
                }

                positionedChars.AddRange(line);
                cursorY += completedLineHeight;
            }

            for (int i = 0; i < charList.Count; i++)
            {
                var rc = charList[i];
                char c = rc.Character;

                if (c == '\n')
                {
                    CommitCellLine(currentLine);
                    currentLine = new List<PositionedRichChar>();
                    cursorX = cellPadding;
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
                    TtfFont charFont = rc.Font ?? activeFont;
                    ushort gIdx = charFont.GetGlyphIndex(c);
                    advance = charFont.GetAdvanceWidth(gIdx, rc.FontSize);
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

                        CommitCellLine(currentLine);
                        currentLine = new List<PositionedRichChar>();

                        cursorX = cellPadding;

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
                                TtfFont wrappedFont = remapped.Info.Font ?? activeFont;
                                ushort wIdx = wrappedFont.GetGlyphIndex(remapped.Info.Character);
                                wAdv = wrappedFont.GetAdvanceWidth(wIdx, remapped.Info.FontSize);
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
                        CommitCellLine(currentLine);
                        currentLine = new List<PositionedRichChar>();
                        cursorX = cellPadding;
                    }
                }

                var charPos = new Vector2(cursorX, cursorY);
                currentLine.Add(new PositionedRichChar { Info = rc, Position = charPos });
                cursorX += advance;
            }

            if (currentLine.Count > 0)
            {
                CommitCellLine(currentLine);
            }

            cellHeight = cursorY + cellPadding;
            return positionedChars;
        }

        public static void LayoutMultiColumn(
            List<Block> blocks,
            List<Paragraph> extraParagraphs,
            float width,
            float height,
            Thickness padding,
            int columnCount,
            float columnGap,
            TtfFont activeFont,
            float baseFontSize,
            Brush? defaultFg,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations,
            FrameworkElement parent,
            Action<Visual> addChild,
            Action<Visual> removeChild)
        {
            positionedChars.Clear();
            tableDecorations.Clear();

            var currentChildren = new List<Visual>(parent.Children);

            var allBlocks = new List<Block>();
            allBlocks.AddRange(blocks);
            foreach (var p in extraParagraphs)
            {
                if (!allBlocks.Contains(p)) allBlocks.Add(p);
            }

            if (activeFont == null || allBlocks.Count == 0 || width <= 0f || height <= 0f)
            {
                foreach (var child in currentChildren)
                {
                    removeChild(child);
                }
                return;
            }

            float scale = baseFontSize / activeFont.UnitsPerEm;
            float lineSpacing = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * scale;

            float availableWidth = width - padding.Horizontal;
            float colWidth = (availableWidth - (columnCount - 1) * columnGap) / columnCount;
            float colHeight = height - padding.Vertical;

            int currentColumn = 0;
            float cursorX = padding.Left;
            float cursorY = padding.Top;

            var resolvedFg = defaultFg ?? ThemeManager.GetBrush("TextPrimary", theme);

            var encounteredChildren = new HashSet<Visual>();

            foreach (var block in allBlocks)
            {
                var charList = new List<RichChar>();
                if (block is Paragraph paragraph)
                {
                    foreach (var inline in paragraph.Inlines)
                    {
                        AccumulateInlines(inline, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
                    }
                }
                else if (block is ListBlock listBlock)
                {
                    AccumulateInlines(listBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
                }
                else if (block is Table tableBlock)
                {
                    AccumulateInlines(tableBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
                }
                else if (block is Inline inlineBlock)
                {
                    AccumulateInlines(inlineBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
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

                        if (c == '\uFFFD' && rc.SourceInline is Table)
                        {
                            break;
                        }

                        float advance = 0f;
                        if (rc.EmbeddedElement != null)
                        {
                            var child = rc.EmbeddedElement;
                            encounteredChildren.Add(child);
                            if (child.Parent != parent)
                            {
                                addChild(child);
                            }
                            child.Measure(new Vector2(colWidth, float.PositiveInfinity));
                            advance = child.DesiredSize.X + 4f;
                        }
                        else
                        {
                            TtfFont charFont = rc.Font ?? activeFont;
                            ushort gIdx = charFont.GetGlyphIndex(rc.Character);
                            advance = charFont.GetAdvanceWidth(gIdx, rc.FontSize);
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

                        if (cursorY + lineMaxH > padding.Top + colHeight && cursorY > padding.Top)
                        {
                            currentColumn++;
                            if (currentColumn >= columnCount)
                            {
                                break;
                            }
                            cursorX = padding.Left + currentColumn * (colWidth + columnGap);
                            cursorY = padding.Top;
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
                                TtfFont charFont = rc.Font ?? activeFont;
                                ushort gIdx = charFont.GetGlyphIndex(rc.Character);
                                advance = charFont.GetAdvanceWidth(gIdx, rc.FontSize);
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
                        LayoutTableFlow(tbl, ref currentColumn, ref cursorX, ref cursorY, colWidth, colHeight, padding, columnCount, columnGap, baseFontSize, activeFont, theme, positionedChars, tableDecorations);
                        i++;
                        hasResetLineIndent = false;
                    }
                }

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
                        lastAdv = activeFont.GetAdvanceWidth(activeFont.GetGlyphIndex(lastPc.Info.Character), lastPc.Info.FontSize);
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

                    positionedChars.AddRange(line);
                }

                cursorY += block.MarginBottom;
                if (currentColumn >= columnCount) break;
            }

            foreach (var child in currentChildren)
            {
                if (child is FrameworkElement fe && !encounteredChildren.Contains(fe))
                {
                    removeChild(fe);
                }
            }
        }

        private static void LayoutTableFlow(
            Table table, 
            ref int currentColumn, 
            ref float cursorX, 
            ref float cursorY, 
            float colWidth, 
            float colHeight,
            Thickness padding,
            int columnCount,
            float columnGap,
            float baseFontSize,
            TtfFont activeFont,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations)
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
                    var pcList = LayoutCellChars(cell, colW, table.CellPadding, out float cHeight, baseFontSize, activeFont, theme);
                    rowCellChars.Add(pcList);
                    cellHeights[col] = cHeight;
                }

                float rowHeight = 0f;
                foreach (float ch in cellHeights)
                {
                    rowHeight = Math.Max(rowHeight, ch);
                }
                if (rowHeight == 0f) rowHeight = baseFontSize + table.CellPadding * 2f;

                if (cursorY + rowHeight > padding.Top + colHeight)
                {
                    currentColumn++;
                    if (currentColumn >= columnCount)
                    {
                        break;
                    }
                    cursorX = padding.Left + currentColumn * (colWidth + columnGap);
                    cursorY = padding.Top;
                }

                float currentCellX = cursorX;
                for (int col = 0; col < row.Cells.Count; col++)
                {
                    var cell = row.Cells[col];
                    float colW = colWidths[col];
                    var cellRect = new Rect(currentCellX, cursorY, colW, rowHeight);

                    var cellBg = cell.Background;
                    if (cellBg is ThemeResourceBrush trBrush)
                    {
                        cellBg = ThemeManager.GetBrush(trBrush.ResourceKey, theme);
                    }
                    var tableBorderBrush = table.BorderBrush;
                    if (tableBorderBrush is ThemeResourceBrush borderTrBrush)
                    {
                        tableBorderBrush = ThemeManager.GetBrush(borderTrBrush.ResourceKey, theme);
                    }

                    tableDecorations.Add(new TableVisualDecoration
                    {
                        Rect = cellRect,
                        Background = cellBg,
                        BorderThickness = table.BorderThickness,
                        BorderBrush = tableBorderBrush
                    });

                    var pcList = rowCellChars[col];
                    foreach (var pc in pcList)
                    {
                        var remapped = new PositionedRichChar
                        {
                            Info = pc.Info,
                            Position = new Vector2(pc.Position.X + currentCellX, pc.Position.Y + cursorY)
                        };
                        positionedChars.Add(remapped);
                    }

                    currentCellX += colW;
                }

                cursorY += rowHeight;
            }
        }

        public static void Render(
            DrawingContext context,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations,
            TtfFont activeFont,
            int selectionStart,
            int selectionLength,
            Hyperlink? hoveredHyperlink)
        {
            if (activeFont == null) return;

            foreach (var dec in tableDecorations)
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

            if (positionedChars.Count == 0) return;

            if (selectionStart >= 0 && selectionLength > 0)
            {
                for (int i = 0; i < positionedChars.Count; i++)
                {
                    if (i >= selectionStart && i < selectionStart + selectionLength)
                    {
                        var pc = positionedChars[i];
                        if (pc.Info.EmbeddedElement != null) continue;
                        TtfFont charFont = pc.Info.Font ?? activeFont;
                        ushort gIdx = charFont.GetGlyphIndex(pc.Info.Character);
                        float advance = charFont.GetAdvanceWidth(gIdx, pc.Info.FontSize);
                        context.DrawRectangle(SelectionHighlightBrush, null, new Rect(pc.Position.X, pc.Position.Y, advance, pc.Info.FontSize));
                    }
                }
            }

            var runBuffer = new StringBuilder(Math.Min(positionedChars.Count, 4096));
            Vector2 startPos = Vector2.Zero;
            RichChar style = default;

            void FlushRun()
            {
                if (runBuffer.Length == 0)
                {
                    return;
                }

                RenderRun(context, runBuffer.ToString(), startPos, style, style.Font ?? activeFont);
                runBuffer.Clear();
            }

            foreach (var pc in positionedChars)
            {
                if (pc.Info.EmbeddedElement != null)
                {
                    FlushRun();
                    continue;
                }

                var pcStyle = pc.Info;
                if (pc.Info.SourceInline is Hyperlink hl && hl == hoveredHyperlink)
                {
                    pcStyle.Foreground = HoveredHyperlinkBrush;
                }

                if (pc.Info.Character == ' ' || pc.Info.Character == '\t')
                {
                    FlushRun();
                    RenderRun(context, pc.Info.Character.ToString(), pc.Position, pcStyle, pcStyle.Font ?? activeFont);
                    continue;
                }

                if (runBuffer.Length == 0)
                {
                    runBuffer.Append(pc.Info.Character);
                    startPos = pc.Position;
                    style = pcStyle;
                }
                else if (pcStyle.IsBold == style.IsBold &&
                         pcStyle.IsItalic == style.IsItalic &&
                         pcStyle.IsUnderline == style.IsUnderline &&
                         pcStyle.FontSize == style.FontSize &&
                         pcStyle.Foreground.Equals(style.Foreground) &&
                         pcStyle.Font == style.Font &&
                         Math.Abs(pc.Position.Y - startPos.Y) < 1f)
                {
                    runBuffer.Append(pc.Info.Character);
                }
                else
                {
                    FlushRun();
                    runBuffer.Append(pc.Info.Character);
                    startPos = pc.Position;
                    style = pcStyle;
                }
            }

            FlushRun();
        }

        private static void RenderRun(DrawingContext context, string text, Vector2 pos, RichChar style, TtfFont activeFont)
        {
            if (activeFont == null) return;
            context.DrawText(text, activeFont, style.FontSize, style.Foreground!, pos, style.IsBold, style.IsItalic);
            if (style.IsUnderline)
            {
                float runW = 0f;
                foreach (char c in text)
                {
                    ushort idx = activeFont.GetGlyphIndex(c);
                    runW += activeFont.GetAdvanceWidth(idx, style.FontSize);
                }
                context.DrawRectangle(style.Foreground, null, new Rect(pos.X, pos.Y + style.FontSize - 1f, runW, 1f));
            }
        }
    }
}
