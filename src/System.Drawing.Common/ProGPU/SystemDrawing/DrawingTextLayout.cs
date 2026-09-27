using System.Drawing;
using System.Numerics;
using ProGPU.Text;

namespace ProGPU.SystemDrawing;

/// <summary>
/// One owned horizontal paragraph generation for painting and interaction.
/// Coordinates returned by interaction are relative to the layout rectangle,
/// including its alignment. Recreate after text, font, format, width or DPI changes.
/// </summary>
public sealed class DrawingTextLayout
{
    private readonly TextInteractionSnapshot _interaction;
    private readonly float _emptyLineHeight;

    internal DrawingTextLayout(TextLayout layout, SizeF layoutSize, Vector2 offset,
        float emptyLineHeight, float dpiX, float dpiY, FontStyle style, bool clip,
        PreparedDrawingGlyphRun[] runs, PreparedDrawingTextDecoration[] decorations)
    {
        _interaction = layout.CreateInteractionSnapshot();
        _emptyLineHeight = emptyLineHeight;
        LayoutSize = layoutSize;
        ContentSize = new SizeF(layout.ContentSize.X, layout.ContentSize.Y);
        Offset = offset;
        DpiX = dpiX;
        DpiY = dpiY;
        FontSize = layout.FontSize;
        Style = style;
        Clip = clip;
        Runs = runs;
        Decorations = decorations;
    }

    public static DrawingTextLayout Create(Graphics graphics, string text, Font font,
        SizeF layoutSize, StringFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        return graphics.CreateRetainedTextLayout(text, font, layoutSize, format);
    }

    public int TextLength => _interaction.TextLength;
    public SizeF LayoutSize { get; }
    public SizeF ContentSize { get; }
    public float DpiX { get; }
    public float DpiY { get; }
    internal Vector2 Offset { get; }
    internal float FontSize { get; }
    internal FontStyle Style { get; }
    internal bool Clip { get; }
    internal PreparedDrawingGlyphRun[] Runs { get; }
    internal PreparedDrawingTextDecoration[] Decorations { get; }

    public TextCaretStop GetCaretStop(int textPosition, bool trailingAffinity = false)
        => Translate(_interaction.GetCaretStop(textPosition, trailingAffinity));

    public TextCaretStop MoveCaretVisually(int textPosition, bool trailingAffinity, int direction)
        => Translate(_interaction.MoveCaretVisually(textPosition, trailingAffinity, direction));

    public TextCaretStop GetRowBoundary(int textPosition, bool trailingAffinity, bool end)
        => Translate(_interaction.GetRowBoundary(textPosition, trailingAffinity, end));

    public TextCaretStop MoveCaretVertically(int textPosition, bool trailingAffinity, int direction, float preferredX)
        => Translate(_interaction.MoveCaretVertically(textPosition, trailingAffinity, direction, preferredX - Offset.X));

    public TextHitTestResult HitTestPoint(PointF point)
    {
        TextHitTestResult hit = _interaction.HitTestPoint(new Vector2(point.X, point.Y) - Offset);
        TextBounds bounds = hit.Bounds;
        return hit with { Bounds = new TextBounds(bounds.X + Offset.X, bounds.Y + Offset.Y,
            bounds.Width, TextLength == 0 ? _emptyLineHeight : bounds.Height) };
    }

    public IReadOnlyList<TextBounds> GetSelectionRectangles(int textStart, int textLength)
    {
        IReadOnlyList<TextBounds> boxes = _interaction.GetSelectionRectangles(textStart, textLength);
        if (boxes.Count == 0 || Offset == Vector2.Zero) return boxes;
        var translated = new TextBounds[boxes.Count];
        for (int i = 0; i < translated.Length; i++)
        {
            TextBounds box = boxes[i];
            translated[i] = box with { X = box.X + Offset.X, Y = box.Y + Offset.Y };
        }
        return translated;
    }

    public void Draw(Graphics graphics, Brush brush, PointF location)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(brush);
        graphics.DrawRetainedTextLayout(this, brush, location);
    }

    private TextCaretStop Translate(TextCaretStop caret)
        => caret with { Position = caret.Position + Offset,
            Height = TextLength == 0 ? _emptyLineHeight : caret.Height };
}

internal readonly record struct PreparedDrawingGlyphRun(
    TtfFont Font, ushort[] GlyphIndices, Vector2[] GlyphPositions);

internal readonly record struct PreparedDrawingTextDecoration(
    float Left, float Baseline, float Displacement, float Width, float Height)
{
    // Retain the ordinary drawing arithmetic order, including its rounding.
    internal ProGPU.Scene.Rect Place(Vector2 origin)
        => new(origin.X + Left, origin.Y + Baseline - Displacement, Width, Height);
}
