using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace ProGPU.WinUI;

public class Slider : Control
{
    private float _minimum = 0f;
    private float _maximum = 100f;
    private float _value = 0f;
    private bool _isDragging;

    public float Minimum
    {
        get => _minimum;
        set { _minimum = value; Value = Math.Clamp(Value, _minimum, _maximum); Invalidate(); }
    }

    public float Maximum
    {
        get => _maximum;
        set { _maximum = value; Value = Math.Clamp(Value, _minimum, _maximum); Invalidate(); }
    }

    public float Value
    {
        get => _value;
        set
        {
            float clamped = Math.Clamp(value, _minimum, _maximum);
            if (_value != clamped)
            {
                _value = clamped;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? ValueChanged;

    public Slider()
    {
        HeightConstraint = 32f;
        WidthConstraint = 200f;
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            _isDragging = true;
            UpdateValueFromPos(e.Position.X);
            base.OnPointerPressed(e);
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        _isDragging = false;
        base.OnPointerReleased(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (_isDragging && IsEnabled)
        {
            UpdateValueFromPos(e.Position.X);
        }
        base.OnPointerMoved(e);
    }

    private void UpdateValueFromPos(float localX)
    {
        float thumbRadius = 8f;
        float width = Size.X;
        float trackWidth = width - 2 * thumbRadius;
        if (trackWidth <= 0f) return;

        float pct = (localX - thumbRadius) / trackWidth;
        pct = Math.Clamp(pct, 0f, 1f);
        Value = Minimum + pct * (Maximum - Minimum);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? Math.Max(120f, availableSize.X);
        float h = HeightConstraint ?? 32f;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
    }

    public override void OnRender(DrawingContext context)
    {
        float baseThumbRadius = 8f;
        float trackHeight = 4f;
        float yCenter = Size.Y / 2f;

        float width = Size.X;
        float trackWidth = width - 2 * baseThumbRadius;

        float pct = 0f;
        if (Maximum > Minimum)
        {
            pct = (Value - Minimum) / (Maximum - Minimum);
        }

        float thumbX = baseThumbRadius + pct * trackWidth;

        // Micro-animated breathing thumb: 7f normal, 9f on hover/drag
        float drawThumbRadius = (IsPointerOver || _isDragging) && IsEnabled ? 9f : 7f;

        // 1. Draw Inactive Track (Right side)
        Rect inactiveRect = new Rect(thumbX, yCenter - trackHeight / 2f, Math.Max(0f, width - baseThumbRadius - thumbX), trackHeight);
        Brush inactiveBg = new SolidColorBrush(0xFFFFFF20); // translucent grey
        context.DrawRectangle(inactiveBg, null, inactiveRect);

        // 2. Draw Active Track (Left side)
        if (thumbX > baseThumbRadius)
        {
            Rect activeRect = new Rect(baseThumbRadius, yCenter - trackHeight / 2f, thumbX - baseThumbRadius, trackHeight);
            Brush activeBg;
            if (!IsEnabled)
            {
                activeBg = new SolidColorBrush(0x2A2A3540);
            }
            else if (_isDragging)
            {
                activeBg = new SolidColorBrush(0x005A9EFF); // pressed accent
            }
            else if (IsPointerOver)
            {
                activeBg = new SolidColorBrush(0x2B88D8FF); // hover accent
            }
            else
            {
                activeBg = new SolidColorBrush(0x0078D4FF); // Segoe Blue
            }
            context.DrawRectangle(activeBg, null, activeRect);
        }

        // 3. Draw Thumb (Circle)
        Rect thumbRect = new Rect(thumbX - drawThumbRadius, yCenter - drawThumbRadius, drawThumbRadius * 2f, drawThumbRadius * 2f);
        Brush thumbBg;
        Pen? thumbBorder = null;

        if (!IsEnabled)
        {
            thumbBg = new SolidColorBrush(0x2A2A35FF);
            thumbBorder = new Pen(new SolidColorBrush(0xFFFFFF08), 1f);
        }
        else if (_isDragging)
        {
            thumbBg = new SolidColorBrush(0x005A9EFF); // Pressed Accent Segoe Blue
            thumbBorder = new Pen(new SolidColorBrush(0xFFFFFFFF), 1.5f);
        }
        else if (IsPointerOver)
        {
            thumbBg = new SolidColorBrush(0xFFFFFFFF); // bright white
            thumbBorder = new Pen(new SolidColorBrush(0x2B88D8FF), 1f); // hover accent border
        }
        else
        {
            thumbBg = new SolidColorBrush(0xFFFFFFFF); // bright white
            thumbBorder = new Pen(new SolidColorBrush(0xFFFFFF20), 1f);
        }

        // Standard Circle rendering using rounded rect path (radius = drawThumbRadius)
        var circlePath = CreateRoundedRectPath(thumbRect, drawThumbRadius);
        context.DrawPath(thumbBg, thumbBorder, circlePath);

        // Draw active focus ring indicator around thumb
        if (IsEnabled && IsFocused)
        {
            var focusPen = new Pen(new SolidColorBrush(0x0078D4FF), 1.5f);
            Rect focusRect = new Rect(thumbRect.X - 2.5f, thumbRect.Y - 2.5f, thumbRect.Width + 5f, thumbRect.Height + 5f);
            var focusPath = CreateRoundedRectPath(focusRect, drawThumbRadius + 2.5f);
            context.DrawPath(null, focusPen, focusPath);
        }

        base.OnRender(context);
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
