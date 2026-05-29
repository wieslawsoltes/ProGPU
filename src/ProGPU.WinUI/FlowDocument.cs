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

    protected override void OnThemeChanged()
    {
        base.OnThemeChanged();
        Invalidate();
        InvalidateMeasure();
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsEnabled) return;

        var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
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
        TextLayoutEngine.LayoutMultiColumn(
            Blocks,
            Paragraphs,
            width,
            height,
            Padding,
            ColumnCount,
            ColumnGap,
            Font!,
            FontSize,
            Foreground,
            this.ActualTheme,
            _positionedChars,
            _tableDecorations,
            this,
            AddChild,
            RemoveChild);
    }

    public override void OnRender(DrawingContext context)
    {
        if (Font == null || _positionedChars.Count == 0) return;

        TextLayoutEngine.Render(
            context, 
            _positionedChars, 
            _tableDecorations, 
            Font, 
            -1, 
            0, 
            _hoveredHyperlink);

        base.OnRender(context);
    }
}
