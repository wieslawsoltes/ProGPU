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

        // Base background and border brush/pen
        if (!IsEnabled)
        {
            bg = Background ?? ThemeManager.GetBrush("ControlBackground");
            pen = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), BorderThickness.Left > 0 ? BorderThickness.Left : 1f);
        }
        else
        {
            bg = Background ?? ThemeManager.GetBrush(IsPointerPressed ? "ControlBackgroundPressed" : IsPointerOver ? "ControlBackgroundHover" : "ControlBackground");
            
            // Border brush/pen based on visual states
            if (IsPointerPressed)
            {
                pen = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), BorderThickness.Left > 0 ? BorderThickness.Left : 1f);
            }
            else if (IsPointerOver)
            {
                pen = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorderHover"), BorderThickness.Left > 0 ? BorderThickness.Left : 1f);
            }
            else
            {
                pen = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), BorderThickness.Left > 0 ? BorderThickness.Left : 1f);
            }
        }

        // Draw soft 3D elevation shadows (ambient & penumbra layers)
        if (IsEnabled)
        {
            float shadowR = CornerRadius;
            
            // Ambient shadow (offset Y=2, very soft, low opacity)
            var ambientRect = new Rect(0, 2, Size.X, Size.Y);
            var ambientBrush = new SolidColorBrush(0x0000000A);
            if (shadowR <= 0f)
            {
                context.DrawRectangle(ambientBrush, null, ambientRect);
            }
            else
            {
                var ambientPath = CreateRoundedRectPath(ambientRect, shadowR);
                context.DrawPath(ambientBrush, null, ambientPath);
            }

            // Penumbra shadow (offset Y=1, tighter, slightly higher opacity)
            var penumbraRect = new Rect(0, 1, Size.X, Size.Y);
            var penumbraBrush = new SolidColorBrush(0x00000014);
            if (shadowR <= 0f)
            {
                context.DrawRectangle(penumbraBrush, null, penumbraRect);
            }
            else
            {
                var penumbraPath = CreateRoundedRectPath(penumbraRect, shadowR);
                context.DrawPath(penumbraBrush, null, penumbraPath);
            }
        }

        // Draw main button background
        if (CornerRadius <= 0f)
        {
            context.DrawRectangle(bg, pen, new Rect(Vector2.Zero, Size));
        }
        else
        {
            var roundedPath = CreateRoundedRectPath(new Rect(Vector2.Zero, Size), CornerRadius);
            context.DrawPath(bg, pen, roundedPath);
        }

        // Draw translucent overlays for hover/pressed states on top of the background (Visual State Blending)
        if (IsEnabled)
        {
            Brush? overlayBrush = null;
            if (IsPointerPressed)
            {
                overlayBrush = ThemeManager.CurrentTheme == ElementTheme.Light
                    ? new SolidColorBrush(0x0000001F)
                    : new SolidColorBrush(0x00000015);
            }
            else if (IsPointerOver)
            {
                overlayBrush = ThemeManager.CurrentTheme == ElementTheme.Light
                    ? new SolidColorBrush(0x0000000A)
                    : new SolidColorBrush(0xFFFFFF10);
            }

            if (overlayBrush != null)
            {
                if (CornerRadius <= 0f)
                {
                    context.DrawRectangle(overlayBrush, null, new Rect(Vector2.Zero, Size));
                }
                else
                {
                    var roundedPath = CreateRoundedRectPath(new Rect(Vector2.Zero, Size), CornerRadius);
                    context.DrawPath(overlayBrush, null, roundedPath);
                }
            }
        }

        // Draw active focus ring indicator
        if (IsEnabled && IsFocused)
        {
            var focusPen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 2f); // Sharp Segoe Blue active focus ring
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
