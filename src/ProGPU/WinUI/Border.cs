using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace ProGPU.WinUI;

public class Border : FrameworkElement
{
    private Brush? _background;
    private Brush? _borderBrush;
    private Thickness _borderThickness;
    private float _cornerRadius;
    private FrameworkElement? _child;

    public Brush? Background
    {
        get => _background;
        set { if (_background != value) { _background = value; Invalidate(); } }
    }

    public Brush? BorderBrush
    {
        get => _borderBrush;
        set { if (_borderBrush != value) { _borderBrush = value; Invalidate(); } }
    }

    public Thickness BorderThickness
    {
        get => _borderThickness;
        set { if (!_borderThickness.Equals(value)) { _borderThickness = value; Invalidate(); } }
    }

    public float CornerRadius
    {
        get => _cornerRadius;
        set { if (_cornerRadius != value) { _cornerRadius = value; Invalidate(); } }
    }

    public FrameworkElement? Child
    {
        get => _child;
        set
        {
            if (_child != value)
            {
                if (_child != null) RemoveChild(_child);
                _child = value;
                if (_child != null) AddChild(_child);
                Invalidate();
            }
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float borderH = BorderThickness.Horizontal;
        float borderV = BorderThickness.Vertical;
        float paddingH = Padding.Horizontal;
        float paddingV = Padding.Vertical;

        Vector2 inset = new Vector2(borderH + paddingH, borderV + paddingV);
        Vector2 childAvailable = new Vector2(
            Math.Max(0f, availableSize.X - inset.X),
            Math.Max(0f, availableSize.Y - inset.Y)
        );

        Vector2 childDesired = Vector2.Zero;
        if (Child != null)
        {
            Child.Measure(childAvailable);
            childDesired = Child.DesiredSize;
        }

        return childDesired + inset;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        if (Child != null)
        {
            float leftInset = BorderThickness.Left + Padding.Left;
            float topInset = BorderThickness.Top + Padding.Top;
            float rightInset = BorderThickness.Right + Padding.Right;
            float bottomInset = BorderThickness.Bottom + Padding.Bottom;

            Rect childRect = new Rect(
                arrangeRect.X + leftInset,
                arrangeRect.Y + topInset,
                Math.Max(0f, arrangeRect.Width - (leftInset + rightInset)),
                Math.Max(0f, arrangeRect.Height - (topInset + bottomInset))
            );
            Child.Arrange(childRect);
        }
    }

    public override void OnRender(DrawingContext context)
    {
        if (Background != null || (BorderBrush != null && BorderThickness.Left > 0))
        {
            var pen = BorderBrush != null && BorderThickness.Left > 0 ? new Pen(BorderBrush, BorderThickness.Left) : null;
            context.DrawRoundedRectangle(Background, pen, new Rect(Vector2.Zero, Size), CornerRadius);
        }
        base.OnRender(context);
    }
}
