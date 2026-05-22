using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace ProGPU.WinUI;

public abstract class TextElement
{
    public Brush? Foreground { get; set; }
    public float? FontSize { get; set; }
}

public abstract class Inline : TextElement
{
}

public class Run : Inline
{
    public string Text { get; set; } = string.Empty;

    public Run() { }
    public Run(string text) { Text = text; }
}

public class Span : Inline
{
    public List<Inline> Inlines { get; } = new();

    public Span() { }
    public Span(params Inline[] inlines)
    {
        Inlines.AddRange(inlines);
    }
}

public class Bold : Span
{
    public Bold() { }
    public Bold(params Inline[] inlines) : base(inlines) { }
}

public class Italic : Span
{
    public Italic() { }
    public Italic(params Inline[] inlines) : base(inlines) { }
}

public class Underline : Span
{
    public Underline() { }
    public Underline(params Inline[] inlines) : base(inlines) { }
}

public struct RichChar
{
    public char Character;
    public Brush Foreground;
    public float FontSize;
    public bool IsBold;
    public bool IsItalic;
}

public class PositionedRichChar
{
    public RichChar Info;
    public Vector2 Position;
}

public class RichTextBlock : FrameworkElement
{
    private TtfFont? _font;
    private float _fontSize = 14f;
    private TextAlignment _textAlignment = TextAlignment.Left;
    private readonly List<PositionedRichChar> _positionedChars = new();

    public List<Inline> Inlines { get; } = new();

    public TtfFont? Font
    {
        get => _font;
        set { if (_font != value) { _font = value; Invalidate(); } }
    }

    public float FontSize
    {
        get => _fontSize;
        set { if (_fontSize != value) { _fontSize = value; Invalidate(); } }
    }

    public TextAlignment TextAlignment
    {
        get => _textAlignment;
        set { if (_textAlignment != value) { _textAlignment = value; Invalidate(); } }
    }

    public List<PositionedRichChar> PositionedChars => _positionedChars;

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (Font == null || Inlines.Count == 0) return Vector2.Zero;

        float maxW = WidthConstraint ?? availableSize.X;
        if (float.IsInfinity(maxW)) maxW = 800f; // reasonable fallback bound

        PerformRichLayout(maxW);

        float measuredH = 0f;
        float measuredW = 0f;
        foreach (var pc in _positionedChars)
        {
            measuredW = Math.Max(measuredW, pc.Position.X + pc.Info.FontSize / 2f);
            measuredH = Math.Max(measuredH, pc.Position.Y + pc.Info.FontSize);
        }

        return new Vector2(measuredW, measuredH + 4f);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        PerformRichLayout(arrangeRect.Width);
    }

    public void PerformRichLayout(float maxWidth)
    {
        _positionedChars.Clear();
        if (Font == null) return;

        var charList = new List<RichChar>();
        var defaultFg = Foreground ?? new SolidColorBrush(0xFFFFFFFF);

        foreach (var inline in Inlines)
        {
            AccumulateInlines(inline, charList, defaultFg, FontSize, false, false);
        }

        if (charList.Count == 0) return;

        // Structured paragraph word wrapping
        float scale = FontSize / Font.UnitsPerEm;
        float lineSpacing = (Font.Ascender - Font.Descender + Font.LineGap) * scale;
        float fontAscent = Font.Ascender * scale;

        float cursorX = Padding.Left;
        float cursorY = Padding.Top;

        var lines = new List<List<PositionedRichChar>>();
        var currentLine = new List<PositionedRichChar>();
        int lastWordStart = -1;
        float lastWordStartCursorX = Padding.Left;

        for (int i = 0; i < charList.Count; i++)
        {
            var rc = charList[i];
            char c = rc.Character;

            if (c == '\n')
            {
                lines.Add(currentLine);
                currentLine = new List<PositionedRichChar>();
                cursorX = Padding.Left;
                cursorY += lineSpacing;
                lastWordStart = -1;
                continue;
            }

            ushort gIdx = Font.GetGlyphIndex(c);
            float advance = Font.GetAdvanceWidth(gIdx, rc.FontSize);

            // Word bounds tracking
            if (c == ' ' || c == '\t')
            {
                lastWordStart = -1;
            }
            else if (lastWordStart == -1)
            {
                lastWordStart = currentLine.Count;
                lastWordStartCursorX = cursorX;
            }

            // Word wrap
            if (cursorX + advance > maxWidth - Padding.Right && cursorX > Padding.Left)
            {
                if (lastWordStart > 0)
                {
                    // wrap word
                    int wrapCount = currentLine.Count - lastWordStart;
                    var wrapped = currentLine.GetRange(lastWordStart, wrapCount);
                    currentLine.RemoveRange(lastWordStart, wrapCount);

                    lines.Add(currentLine);
                    currentLine = new List<PositionedRichChar>();

                    cursorX = Padding.Left;
                    cursorY += lineSpacing;

                    foreach (var wc in wrapped)
                    {
                        var remapped = wc;
                        float shift = wc.Position.X - lastWordStartCursorX;
                        remapped.Position = new Vector2(Padding.Left + shift, cursorY + fontAscent);
                        currentLine.Add(remapped);
                        
                        ushort wIdx = Font.GetGlyphIndex(remapped.Info.Character);
                        cursorX = Padding.Left + shift + Font.GetAdvanceWidth(wIdx, remapped.Info.FontSize);
                    }

                    // Add current character
                    var pos = new Vector2(cursorX, cursorY + fontAscent);
                    currentLine.Add(new PositionedRichChar { Info = rc, Position = pos });
                    cursorX += advance;
                    lastWordStart = 0;
                    lastWordStartCursorX = Padding.Left;
                    continue;
                }
                else
                {
                    // hard wrap
                    lines.Add(currentLine);
                    currentLine = new List<PositionedRichChar>();
                    cursorX = Padding.Left;
                    cursorY += lineSpacing;
                }
            }

            var charPos = new Vector2(cursorX, cursorY + fontAscent);
            currentLine.Add(new PositionedRichChar { Info = rc, Position = charPos });
            cursorX += advance;
        }

        if (currentLine.Count > 0)
        {
            lines.Add(currentLine);
        }

        // Add back to final rendering coordinates
        foreach (var line in lines)
        {
            _positionedChars.AddRange(line);
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

        // Group same-style adjacent characters into single runs
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

public class RichEditBox : Control
{
    private TtfFont? _font;
    private float _fontSize = 14f;
    private int _caretIndex;
    private readonly RichTextBlock _blockView;

    public List<Inline> Inlines => _blockView.Inlines;

    public TtfFont? Font
    {
        get => _font;
        set { _font = value; _blockView.Font = value; Invalidate(); }
    }

    public float FontSize
    {
        get => _fontSize;
        set { _fontSize = value; _blockView.FontSize = value; Invalidate(); }
    }

    public int CaretIndex
    {
        get => _caretIndex;
        set
        {
            int total = GetTotalCharacters();
            int clamped = Math.Clamp(value, 0, total);
            if (_caretIndex != clamped)
            {
                _caretIndex = clamped;
                Invalidate();
            }
        }
    }

    public RichEditBox()
    {
        Padding = new Thickness(8);
        CornerRadius = 4f;
        _blockView = new RichTextBlock { Padding = new Thickness(0) };
        AddChild(_blockView);
        
        // Initial text run
        _blockView.Inlines.Add(new Run("Type here in "));
        _blockView.Inlines.Add(new Bold(new Run("Bold")));
        _blockView.Inlines.Add(new Run(" or "));
        _blockView.Inlines.Add(new Italic(new Run("Italic")));
        _blockView.Inlines.Add(new Run("..."));
    }

    private int GetTotalCharacters()
    {
        int count = 0;
        foreach (var inline in Inlines)
        {
            count += GetCharCount(inline);
        }
        return count;
    }

    private int GetCharCount(Inline inline)
    {
        if (inline is Run r) return r.Text.Length;
        if (inline is Span s)
        {
            int c = 0;
            foreach (var sub in s.Inlines) c += GetCharCount(sub);
            return c;
        }
        return 0;
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerPressed(e);

            // Locate clicked character offset for caret positioning
            float clickX = e.Position.X - Padding.Left;
            float clickY = e.Position.Y - Padding.Top;
            
            _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
            var pcs = _blockView.PositionedChars;

            if (pcs.Count == 0)
            {
                CaretIndex = 0;
                return;
            }

            int bestIdx = 0;
            float bestDist = float.PositiveInfinity;

            for (int i = 0; i < pcs.Count; i++)
            {
                var dist = Vector2.Distance(pcs[i].Position, new Vector2(clickX, clickY));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }

            CaretIndex = bestIdx;
        }
    }

    public override void OnCharacterReceived(CharacterReceivedRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused)
        {
            InsertChar(e.Character);
            CaretIndex++;
            e.Handled = true;
        }
        base.OnCharacterReceived(e);
    }

    private void InsertChar(char c)
    {
        // Simple insert into first run or appropriate segment
        if (Inlines.Count == 0)
        {
            Inlines.Add(new Run(c.ToString()));
            return;
        }

        int index = CaretIndex;
        foreach (var inline in Inlines)
        {
            if (inline is Run run)
            {
                if (index <= run.Text.Length)
                {
                    run.Text = run.Text.Insert(index, c.ToString());
                    _blockView.Invalidate();
                    return;
                }
                index -= run.Text.Length;
            }
            else if (inline is Span span)
            {
                foreach (var sub in span.Inlines)
                {
                    if (sub is Run subRun)
                    {
                        if (index <= subRun.Text.Length)
                        {
                            subRun.Text = subRun.Text.Insert(index, c.ToString());
                            _blockView.Invalidate();
                            return;
                        }
                        index -= subRun.Text.Length;
                    }
                }
            }
        }

        // fallback append
        if (Inlines[Inlines.Count - 1] is Run lastRun)
        {
            lastRun.Text += c;
        }
        else
        {
            Inlines.Add(new Run(c.ToString()));
        }
        _blockView.Invalidate();
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused)
        {
            if (e.Key == Key.Backspace)
            {
                if (CaretIndex > 0)
                {
                    DeleteChar(CaretIndex - 1);
                    CaretIndex--;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Delete)
            {
                if (CaretIndex < GetTotalCharacters())
                {
                    DeleteChar(CaretIndex);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Left)
            {
                if (CaretIndex > 0)
                {
                    CaretIndex--;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Right)
            {
                if (CaretIndex < GetTotalCharacters())
                {
                    CaretIndex++;
                    e.Handled = true;
                }
            }
        }
        base.OnKeyDown(e);
    }

    private void DeleteChar(int idx)
    {
        int index = idx;
        foreach (var inline in Inlines)
        {
            if (inline is Run run)
            {
                if (index < run.Text.Length)
                {
                    run.Text = run.Text.Remove(index, 1);
                    _blockView.Invalidate();
                    return;
                }
                index -= run.Text.Length;
            }
            else if (inline is Span span)
            {
                foreach (var sub in span.Inlines)
                {
                    if (sub is Run subRun)
                    {
                        if (index < subRun.Text.Length)
                        {
                            subRun.Text = subRun.Text.Remove(index, 1);
                            _blockView.Invalidate();
                            return;
                        }
                        index -= subRun.Text.Length;
                    }
                }
            }
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? Math.Max(200f, availableSize.X);
        float h = HeightConstraint ?? 120f;
        _blockView.Measure(new Vector2(w - Padding.Horizontal, float.PositiveInfinity));
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        _blockView.Arrange(new Rect(Padding.Left, Padding.Top, arrangeRect.Width - Padding.Horizontal, arrangeRect.Height - Padding.Vertical));
    }

    public override void OnRender(DrawingContext context)
    {
        // Draw glassmorphic border card
        Brush bg = IsFocused ? new SolidColorBrush(0x151520FF) : new SolidColorBrush(0xFFFFFF0D);
        Pen borderPen = IsFocused 
            ? new Pen(new SolidColorBrush(0x0078D7FF), 1.5f) 
            : new Pen(new SolidColorBrush(IsPointerOver ? 0xFFFFFF50 : 0xFFFFFF20), 1f);

        var roundedPath = CreateRoundedRectPath(new Rect(Vector2.Zero, Size), CornerRadius);
        context.DrawPath(bg, borderPen, roundedPath);

        base.OnRender(context);

        // Draw caret
        if (IsFocused && Font != null && (DateTime.Now.Millisecond / 500) % 2 == 0)
        {
            _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
            var pcs = _blockView.PositionedChars;

            Vector2 caretPos = new Vector2(Padding.Left, Padding.Top + FontSize);
            if (pcs.Count > 0)
            {
                int cIdx = Math.Clamp(CaretIndex, 0, pcs.Count - 1);
                var pc = pcs[cIdx];
                caretPos = pc.Position;
                if (CaretIndex >= pcs.Count)
                {
                    // place caret at end of last char
                    ushort lastG = Font.GetGlyphIndex(pc.Info.Character);
                    caretPos.X += Font.GetAdvanceWidth(lastG, pc.Info.FontSize);
                }
            }

            Rect caretRect = new Rect(caretPos.X, caretPos.Y - FontSize + 2f, 1.5f, FontSize + 2f);
            context.DrawRectangle(new SolidColorBrush(0xFFFFFFFF), null, caretRect);
        }
    }

    private static PathGeometry CreateRoundedRectPath(Rect rect, float r)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure(new Vector2(rect.X + r, rect.Y), isClosed: true);
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width - r, rect.Y)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y), new Vector2(rect.X + rect.Width, rect.Y + r)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height - r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height), new Vector2(rect.X + rect.Width - r, rect.Y + rect.Height)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + r, rect.Y + rect.Height)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y + rect.Height), new Vector2(rect.X, rect.Y + rect.Height - r)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X, rect.Y + r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y), new Vector2(rect.X + r, rect.Y)));
        geo.Figures.Add(fig);
        return geo;
    }
}
