using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace ProGPU.WinUI;

public class Paragraph
{
    public List<Inline> Inlines { get; } = new();
    public float MarginBottom { get; set; } = 12f;

    public Paragraph() { }
    public Paragraph(params Inline[] inlines)
    {
        Inlines.AddRange(inlines);
    }
}

public class FlowDocument : FrameworkElement
{
    private TtfFont? _font;
    private float _fontSize = 14f;
    private int _columnCount = 2;
    private float _columnGap = 24f;
    private readonly List<PositionedRichChar> _positionedChars = new();

    public List<Paragraph> Paragraphs { get; } = new();

    public TtfFont? Font
    {
        get => _font;
        set { _font = value; Invalidate(); }
    }

    public float FontSize
    {
        get => _fontSize;
        set { _fontSize = value; Invalidate(); }
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
    }

    private void PerformFlowLayout(float width, float height)
    {
        _positionedChars.Clear();
        if (Font == null || Paragraphs.Count == 0 || width <= 0f || height <= 0f) return;

        float scale = FontSize / Font.UnitsPerEm;
        float lineSpacing = (Font.Ascender - Font.Descender + Font.LineGap) * scale;
        float fontAscent = Font.Ascender * scale;

        float availableWidth = width - Padding.Horizontal;
        float colWidth = (availableWidth - (ColumnCount - 1) * ColumnGap) / ColumnCount;
        float colHeight = height - Padding.Vertical;

        int currentColumn = 0;
        float cursorX = Padding.Left;
        float cursorY = Padding.Top;

        var defaultFg = Foreground ?? new SolidColorBrush(0xFFFFFFFF);

        foreach (var paragraph in Paragraphs)
        {
            var charList = new List<RichChar>();
            foreach (var inline in paragraph.Inlines)
            {
                AccumulateInlines(inline, charList, defaultFg, FontSize, false, false);
            }

            if (charList.Count == 0) continue;

            int i = 0;
            while (i < charList.Count)
            {
                if (cursorY + lineSpacing > Padding.Top + colHeight)
                {
                    currentColumn++;
                    if (currentColumn >= ColumnCount)
                    {
                        break;
                    }
                    cursorX = Padding.Left + currentColumn * (colWidth + ColumnGap);
                    cursorY = Padding.Top;
                }

                var lineChars = new List<RichChar>();
                float lineW = 0f;
                int lastWordIdx = -1;

                while (i < charList.Count)
                {
                    var rc = charList[i];
                    ushort gIdx = Font.GetGlyphIndex(rc.Character);
                    float advance = Font.GetAdvanceWidth(gIdx, rc.FontSize);

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

                float runningX = cursorX;
                foreach (var rc in lineChars)
                {
                    ushort gIdx = Font.GetGlyphIndex(rc.Character);
                    float advance = Font.GetAdvanceWidth(gIdx, rc.FontSize);

                    _positionedChars.Add(new PositionedRichChar
                    {
                        Info = rc,
                        Position = new Vector2(runningX, cursorY + fontAscent)
                    });
                    runningX += advance;
                }

                cursorY += lineSpacing;
            }

            cursorY += paragraph.MarginBottom;
            if (currentColumn >= ColumnCount) break;
        }
    }

    private void AccumulateInlines(Inline inline, List<RichChar> list, Brush defaultFg, float defaultSize, bool isBold, bool isItalic)
    {
        Brush fg = inline.Foreground ?? defaultFg;
        float size = inline.FontSize ?? defaultSize;

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
                    IsItalic = isItalic
                });
            }
        }
        else if (inline is Span span)
        {
            bool nextBold = isBold || (span is Bold);
            bool nextItalic = isItalic || (span is Italic);
            foreach (var sub in span.Inlines)
            {
                AccumulateInlines(sub, list, fg, size, nextBold, nextItalic);
            }
        }
    }

    public override void OnRender(DrawingContext context)
    {
        if (Font == null || _positionedChars.Count == 0) return;

        // Group consecutive characters of same style for extreme text composition speed
        string runBuffer = "";
        Vector2 startPos = Vector2.Zero;
        RichChar style = default;

        foreach (var pc in _positionedChars)
        {
            if (runBuffer.Length == 0)
            {
                runBuffer = pc.Info.Character.ToString();
                startPos = pc.Position;
                style = pc.Info;
            }
            else if (pc.Info.IsBold == style.IsBold &&
                     pc.Info.IsItalic == style.IsItalic &&
                     pc.Info.FontSize == style.FontSize &&
                     pc.Info.Foreground.Equals(style.Foreground) &&
                     Math.Abs(pc.Position.Y - startPos.Y) < 1f)
            {
                runBuffer += pc.Info.Character;
            }
            else
            {
                context.DrawText(runBuffer, Font, style.FontSize, style.Foreground, startPos);
                runBuffer = pc.Info.Character.ToString();
                startPos = pc.Position;
                style = pc.Info;
            }
        }

        if (runBuffer.Length > 0)
        {
            context.DrawText(runBuffer, Font, style.FontSize, style.Foreground, startPos);
        }

        base.OnRender(context);
    }
}
