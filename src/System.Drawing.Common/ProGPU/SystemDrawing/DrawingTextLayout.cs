using System.Drawing;
using System.Numerics;
using ProGPU.Backend.Native;
using ProGPU.Text;
using ProGPU.Text.Shaping;

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
    private readonly string _source;
    private readonly int _paragraphLevel;
    private Lazy<DrawingEditWordBoundaryCapture>? _editWordBoundaries;
    private readonly Lazy<TextEditInteractionSnapshot> _editInteraction;

    internal DrawingTextLayout(TextLayout layout, SizeF layoutSize, Vector2 offset,
        float emptyLineHeight, float dpiX, float dpiY, FontStyle style, bool clip,
        PreparedDrawingGlyphRun[] runs, PreparedDrawingTextDecoration[] decorations)
    {
        _interaction = layout.CreateInteractionSnapshot();
        _editInteraction = new(_interaction.CreateEditInteractionSnapshot,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _source = layout.Text;
        _paragraphLevel = layout.ShapingOptions.Direction switch
        {
            ShapingDirection.LeftToRight => 0,
            ShapingDirection.RightToLeft => 1,
            _ => throw new NotSupportedException("Retained source interaction requires an explicit horizontal paragraph direction.")
        };
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
    public int RowCount => _interaction.RowCount;

    /// <summary>
    /// Captures the original EDIT word-selection inventory for this exact source
    /// generation. Unsupported source policies and missing owned dependencies
    /// return their exact native status/error with a null snapshot, never a
    /// substitute grapheme or wrapping inventory. The completed result is cached.
    /// </summary>
    /// <remarks>
    /// This explicit query makes at most one native batch call. Ordinary layout
    /// construction/painting does not load the native classifier. Legacy EDIT
    /// endpoints can lie inside modern graphemes; this does not admit an interior
    /// caret geometry contract or ordinary editor UI by itself.
    /// </remarks>
    public NativeEditWordBoundaryResult GetEditWordBoundaries(out DrawingEditWordBoundarySnapshot? snapshot)
        => GetEditWordBoundaries(NativeEditWordBoundaryInterop.Resolve, out snapshot);

    // The typed managed seam exercises the actual retained-source ownership and
    // publication path in device-free tests; the public path always uses native.
    internal NativeEditWordBoundaryResult GetEditWordBoundaries(EditWordBoundaryResolver resolver,
        out DrawingEditWordBoundarySnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Lazy<DrawingEditWordBoundaryCapture>? capture = Volatile.Read(ref _editWordBoundaries);
        if (capture is null)
        {
            var candidate = new Lazy<DrawingEditWordBoundaryCapture>(
                () => DrawingEditWordBoundarySnapshot.Capture(_source, _paragraphLevel, resolver),
                LazyThreadSafetyMode.ExecutionAndPublication);
            capture = Interlocked.CompareExchange(ref _editWordBoundaries, candidate, null) ?? candidate;
        }
        DrawingEditWordBoundaryCapture result = capture.Value;
        snapshot = result.Snapshot;
        return result.Result;
    }

    public int GetRowSourceStart(int rowIndex) => _interaction.GetRowSourceStart(rowIndex);

    public int GetRowIndexFromTextPosition(int textPosition)
        => _interaction.GetRowIndexFromTextPosition(textPosition);

    public int GetCaretRowIndex(int textPosition, bool trailingAffinity = false)
        => _interaction.GetCaretRowIndex(textPosition, trailingAffinity);

    public PointF GetSourcePositionPoint(int textPosition)
    {
        Vector2 position = _interaction.GetSourcePositionPoint(textPosition) + Offset;
        return new PointF(position.X, position.Y);
    }

    /// <summary>EDIT source point for the same original owner; qualified boundaries retain ordinary source mapping.</summary>
    public PointF GetEditSourcePositionPoint(int textPosition)
    {
        Vector2 position = _editInteraction.Value.GetSourcePositionPoint(textPosition) + Offset;
        return new PointF(position.X, position.Y);
    }

    internal Vector2 Offset { get; }
    internal float FontSize { get; }
    internal FontStyle Style { get; }
    internal bool Clip { get; }
    internal PreparedDrawingGlyphRun[] Runs { get; }
    internal PreparedDrawingTextDecoration[] Decorations { get; }

    public TextCaretStop GetCaretStop(int textPosition, bool trailingAffinity = false)
        => Translate(_interaction.GetCaretStop(textPosition, trailingAffinity));

    /// <summary>EDIT endpoint geometry; the exact source index is retained inside an original grapheme.</summary>
    public TextCaretStop GetEditCaretStop(int textPosition, bool trailingAffinity = false)
        => Translate(_editInteraction.Value.GetCaretStop(textPosition, trailingAffinity));

    public TextCaretStop MoveCaretVisually(int textPosition, bool trailingAffinity, int direction)
        => Translate(_interaction.MoveCaretVisually(textPosition, trailingAffinity, direction));

    public TextCaretStop GetRowBoundary(int textPosition, bool trailingAffinity, bool end)
        => Translate(_interaction.GetRowBoundary(textPosition, trailingAffinity, end));

    public TextCaretStop MoveCaretVertically(int textPosition, bool trailingAffinity, int direction, float preferredX)
        => Translate(_interaction.MoveCaretVertically(textPosition, trailingAffinity, direction, preferredX - Offset.X));

    public TextHitTestResult HitTestPoint(PointF point)
    {
        TextHitTestResult hit = _interaction.HitTestPoint(new Vector2(point.X, point.Y) - Offset);
        return Translate(hit);
    }

    /// <summary>
    /// Returns the selected original UTF-16 cluster from this layout generation,
    /// with the same aligned bounds and caret affinity as <see cref="HitTestPoint"/>.
    /// Empty rows have a zero-length range at their retained insertion position.
    /// </summary>
    public TextClusterHitTestResult HitTestCluster(PointF point)
    {
        TextClusterHitTestResult result = _interaction.HitTestCluster(new Vector2(point.X, point.Y) - Offset);
        return result with { Hit = Translate(result.Hit) };
    }

    /// <summary>EDIT pointer geometry over original whole-grapheme owners, not word-boundary endpoints.</summary>
    public TextHitTestResult HitTestEditPoint(PointF point)
        => Translate(_editInteraction.Value.HitTestPoint(new Vector2(point.X, point.Y) - Offset));

    private TextHitTestResult Translate(TextHitTestResult hit)
    {
        TextBounds bounds = hit.Bounds;
        return hit with { Bounds = new TextBounds(bounds.X + Offset.X, bounds.Y + Offset.Y,
            bounds.Width, TextLength == 0 ? _emptyLineHeight : bounds.Height) };
    }

    public IReadOnlyList<TextBounds> GetSelectionRectangles(int textStart, int textLength)
        => Translate(_interaction.GetSelectionRectangles(textStart, textLength));

    /// <summary>EDIT selection covers every original grapheme intersected by the unchanged source range.</summary>
    public IReadOnlyList<TextBounds> GetEditSelectionRectangles(int textStart, int textLength)
        => Translate(_editInteraction.Value.GetSelectionRectangles(textStart, textLength));

    private IReadOnlyList<TextBounds> Translate(IReadOnlyList<TextBounds> boxes)
    {
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
