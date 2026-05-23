using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace ProGPU.WinUI;

public class CheckBox : Control
{
    private bool _isChecked;
    private FrameworkElement? _content;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnCheckedChanged();
            }
        }
    }

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

    public event EventHandler? Checked;
    public event EventHandler? Unchecked;
    public event EventHandler? CheckedChanged;

    public CheckBox()
    {
        CornerRadius = 4f;
        Padding = new Thickness(8, 4, 8, 4);
    }

    private void OnCheckedChanged()
    {
        Invalidate();
        CheckedChanged?.Invoke(this, EventArgs.Empty);
        if (_isChecked)
        {
            Checked?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Unchecked?.Invoke(this, EventArgs.Empty);
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled && IsPointerPressed && IsPointerOver)
        {
            IsChecked = !IsChecked;
        }
        base.OnPointerReleased(e);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float borderH = BorderThickness.Horizontal;
        float borderV = BorderThickness.Vertical;
        float paddingH = Padding.Horizontal;
        float paddingV = Padding.Vertical;

        float boxSize = 18f;
        float spacing = 8f;

        Vector2 inset = new Vector2(borderH + paddingH + boxSize + spacing, borderV + paddingV);
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

        return new Vector2(
            contentDesired.X + inset.X,
            Math.Max(boxSize, contentDesired.Y) + borderV + paddingV
        );
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float leftInset = BorderThickness.Left + Padding.Left;
        float topInset = BorderThickness.Top + Padding.Top;
        float boxSize = 18f;
        float spacing = 8f;

        // Vertically center the box in the arrange area
        float boxY = arrangeRect.Y + topInset + (arrangeRect.Height - (topInset + BorderThickness.Bottom + Padding.Bottom) - boxSize) / 2f;

        if (Content != null)
        {
            float contentX = arrangeRect.X + leftInset + boxSize + spacing;
            float contentW = arrangeRect.Width - (leftInset + BorderThickness.Right + Padding.Right + boxSize + spacing);
            float contentH = Content.DesiredSize.Y;
            float contentY = arrangeRect.Y + topInset + (arrangeRect.Height - (topInset + BorderThickness.Bottom + Padding.Bottom) - contentH) / 2f;

            Content.Arrange(new Rect(contentX, contentY, contentW, contentH));
        }
    }

    public override void OnRender(DrawingContext context)
    {
        float leftInset = BorderThickness.Left + Padding.Left;
        float topInset = BorderThickness.Top + Padding.Top;
        float boxSize = 18f;
        float boxY = (Size.Y - boxSize) / 2f;

        Rect boxRect = new Rect(leftInset, boxY, boxSize, boxSize);

        // Styling brushes
        Brush? boxBg;
        Pen? boxBorder;

        if (!IsEnabled)
        {
            boxBg = Background ?? ThemeManager.GetBrush("ControlBackground");
            boxBorder = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), 1f);
        }
        else if (IsChecked)
        {
            // Filled accent color for checked state (Segoe Accent / Hover Accent / Pressed Accent)
            boxBg = IsPointerPressed 
                ? ThemeManager.GetBrush("SystemAccentColorDark1") 
                : (IsPointerOver ? ThemeManager.GetBrush("SystemAccentColorLight1") : ThemeManager.GetBrush("SystemAccentColor"));
            boxBorder = null;
        }
        else
        {
            boxBg = Background ?? ThemeManager.GetBrush(IsPointerPressed ? "ControlBackgroundPressed" : IsPointerOver ? "ControlBackgroundHover" : "ControlBackground");

            boxBorder = new Pen(BorderBrush ?? ThemeManager.GetBrush(IsPointerOver ? "ControlBorderHover" : "ControlBorder"), 1f);
        }

        // Draw check box frame
        if (CornerRadius <= 0f)
        {
            context.DrawRectangle(boxBg, boxBorder, boxRect);
        }
        else
        {
            var roundedPath = CreateRoundedRectPath(boxRect, CornerRadius);
            context.DrawPath(boxBg, boxBorder, roundedPath);
        }

        // Draw checkmark vector if checked
        if (IsChecked)
        {
            var checkGeometry = new PathGeometry();
            var checkFigure = new PathFigure(new Vector2(boxRect.X + 4.5f, boxRect.Y + 9f), isClosed: false);
            checkFigure.Segments.Add(new LineSegment(new Vector2(boxRect.X + 8f, boxRect.Y + 12.5f)));
            checkFigure.Segments.Add(new LineSegment(new Vector2(boxRect.X + 13.5f, boxRect.Y + 5f)));
            checkGeometry.Figures.Add(checkFigure);

            // Draw white/muted checkmark stroke
            var checkBrush = IsEnabled 
                ? (ThemeManager.CurrentTheme == ElementTheme.Light ? ThemeManager.GetBrush("CardBackground") : ThemeManager.GetBrush("TextPrimary"))
                : ThemeManager.GetBrush("TextSecondary");
            var checkPen = new Pen(checkBrush, 2f);
            context.DrawPath(null, checkPen, checkGeometry);
        }

        // Draw active focus ring indicator around the checkbox box frame
        if (IsEnabled && IsFocused)
        {
            var focusPen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 2f); // Segoe Blue active focus ring
            Rect focusRect = new Rect(boxRect.X - 2f, boxRect.Y - 2f, boxRect.Width + 4f, boxRect.Height + 4f);
            if (CornerRadius <= 0f)
            {
                context.DrawRectangle(null, focusPen, focusRect);
            }
            else
            {
                var focusPath = CreateRoundedRectPath(focusRect, CornerRadius + 2f);
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
