using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace ProGPU.WinUI;

public class Button : Control
{
    private FrameworkElement? _content;

    public FrameworkElement? Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                if (_content != null) RemoveChild(_content);
                _content = value;
                if (_content != null) AddChild(_content);
                Invalidate();
            }
        }
    }

    public event EventHandler? Click;

    public Button()
    {
        CornerRadius = 6f;
        Padding = new Thickness(12, 6, 12, 6);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled && IsPointerPressed && IsPointerOver)
        {
            Click?.Invoke(this, EventArgs.Empty);
        }
        base.OnPointerReleased(e);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float borderH = BorderThickness.Horizontal;
        float borderV = BorderThickness.Vertical;
        float paddingH = Padding.Horizontal;
        float paddingV = Padding.Vertical;

        Vector2 inset = new Vector2(borderH + paddingH, borderV + paddingV);
        Vector2 contentAvail = new Vector2(
            Math.Max(0f, availableSize.X - inset.X),
            Math.Max(0f, availableSize.Y - inset.Y)
        );

        Vector2 contentDesired = Vector2.Zero;
        if (Content != null)
        {
            Content.Measure(contentAvail);
            contentDesired = Content.DesiredSize;
        }

        float minW = 64f;
        float minH = 28f;
        return new Vector2(
            Math.Max(minW, contentDesired.X + inset.X),
            Math.Max(minH, contentDesired.Y + inset.Y)
        );
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        if (Content != null)
        {
            float leftInset = BorderThickness.Left + Padding.Left;
            float topInset = BorderThickness.Top + Padding.Top;
            float rightInset = BorderThickness.Right + Padding.Right;
            float bottomInset = BorderThickness.Bottom + Padding.Bottom;

            float childW = Math.Min(arrangeRect.Width - (leftInset + rightInset), Content.DesiredSize.X);
            float childH = Math.Min(arrangeRect.Height - (topInset + bottomInset), Content.DesiredSize.Y);

            float childX = arrangeRect.X + leftInset + (arrangeRect.Width - (leftInset + rightInset) - childW) / 2f;
            float childY = arrangeRect.Y + topInset + (arrangeRect.Height - (topInset + bottomInset) - childH) / 2f;

            Content.Arrange(new Rect(childX, childY, childW, childH));
        }
    }

    public override void OnRender(DrawingContext context)
    {
        Brush? bg;
        Pen? pen;

        if (!IsEnabled)
        {
            bg = new SolidColorBrush(0x2A2A3540);
            pen = new Pen(new SolidColorBrush(0xFFFFFF08), 1f);
        }
        else if (IsPointerPressed)
        {
            bg = Background != null ? new SolidColorBrush(0x005A9EFF) : new SolidColorBrush(0xFFFFFF0D);
            pen = new Pen(new SolidColorBrush(0xFFFFFF15), 1f);
        }
        else if (IsPointerOver)
        {
            bg = Background != null ? new SolidColorBrush(0x2B88D8FF) : new SolidColorBrush(0xFFFFFF25);
            pen = new Pen(new SolidColorBrush(0xFFFFFF30), 1f);
        }
        else
        {
            bg = Background ?? new SolidColorBrush(0xFFFFFF15);
            pen = BorderBrush != null && BorderThickness.Left > 0 
                ? new Pen(BorderBrush, BorderThickness.Left) 
                : new Pen(new SolidColorBrush(0xFFFFFF15), 1f);
        }

        if (CornerRadius <= 0f)
        {
            context.DrawRectangle(bg, pen, new Rect(Vector2.Zero, Size));
        }
        else
        {
            var roundedPath = CreateRoundedRectPath(new Rect(Vector2.Zero, Size), CornerRadius);
            context.DrawPath(bg, pen, roundedPath);
        }

        // Draw active focus ring indicator
        if (IsEnabled && IsFocused)
        {
            var focusPen = new Pen(new SolidColorBrush(0x0078D4FF), 2f); // Sharp Segoe Blue active focus ring
            // Slightly inset focus ring for clean aesthetics
            float inset = 1.5f;
            var focusRect = new Rect(inset, inset, Size.X - 2 * inset, Size.Y - 2 * inset);
            if (CornerRadius <= 0f)
            {
                context.DrawRectangle(null, focusPen, focusRect);
            }
            else
            {
                float focusR = Math.Max(0f, CornerRadius - inset);
                var focusPath = CreateRoundedRectPath(focusRect, focusR);
                context.DrawPath(null, focusPen, focusPath);
            }
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
