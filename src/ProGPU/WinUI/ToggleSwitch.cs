using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace ProGPU.WinUI;

public class ToggleSwitch : Control
{
    private bool _isOn;
    private FrameworkElement? _content;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn != value)
            {
                _isOn = value;
                Invalidate();
                Toggled?.Invoke(this, EventArgs.Empty);
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

    public event EventHandler? Toggled;

    public ToggleSwitch()
    {
        CornerRadius = 10f; // Track height is ~20, so corner radius 10 makes it a capsule.
        Padding = new Thickness(6, 4, 6, 4);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled && IsPointerPressed && IsPointerOver)
        {
            IsOn = !IsOn;
        }
        base.OnPointerReleased(e);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float borderH = BorderThickness.Horizontal;
        float borderV = BorderThickness.Vertical;
        float paddingH = Padding.Horizontal;
        float paddingV = Padding.Vertical;

        float trackW = 40f;
        float trackH = 20f;
        float spacing = 8f;

        Vector2 inset = new Vector2(borderH + paddingH + trackW, borderV + paddingV);
        if (Content != null)
        {
            inset.X += spacing;
        }

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
            Math.Max(trackH, contentDesired.Y) + borderV + paddingV
        );
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float leftInset = BorderThickness.Left + Padding.Left;
        float topInset = BorderThickness.Top + Padding.Top;
        float trackW = 40f;
        float spacing = 8f;

        if (Content != null)
        {
            float contentX = arrangeRect.X + leftInset + trackW + spacing;
            float contentW = arrangeRect.Width - (leftInset + BorderThickness.Right + Padding.Right + trackW + spacing);
            float contentH = Content.DesiredSize.Y;
            float contentY = arrangeRect.Y + topInset + (arrangeRect.Height - (topInset + BorderThickness.Bottom + Padding.Bottom) - contentH) / 2f;

            Content.Arrange(new Rect(contentX, contentY, contentW, contentH));
        }
    }

    public override void OnRender(DrawingContext context)
    {
        float leftInset = BorderThickness.Left + Padding.Left;
        float trackW = 40f;
        float trackH = 20f;
        float trackY = (Size.Y - trackH) / 2f;

        Rect trackRect = new Rect(leftInset, trackY, trackW, trackH);

        Brush? trackBg;
        Pen? trackBorder = null;

        if (!IsEnabled)
        {
            trackBg = Background ?? ThemeManager.GetBrush("ControlBackground");
            trackBorder = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), 1f);
        }
        else if (IsOn)
        {
            // Filled Segoe Blue
            trackBg = IsPointerPressed
                ? ThemeManager.GetBrush("SystemAccentColorDark1")
                : (IsPointerOver ? ThemeManager.GetBrush("SystemAccentColorLight1") : ThemeManager.GetBrush("SystemAccentColor"));
        }
        else
        {
            // Off: Outlined or dark translucent capsule
            trackBg = Background ?? ThemeManager.GetBrush(IsPointerPressed ? "ControlBackgroundPressed" : IsPointerOver ? "ControlBackgroundHover" : "ControlBackground");

            trackBorder = new Pen(BorderBrush ?? ThemeManager.GetBrush(IsPointerOver ? "ControlBorderHover" : "ControlBorder"), 1f);
        }

        // Draw capsule track
        var roundedPath = CreateRoundedRectPath(trackRect, CornerRadius);
        context.DrawPath(trackBg, trackBorder, roundedPath);

        // Draw thumb
        // Thumb size inside track. Track height is 20f, so thumb height/width can be 12f (radius = 6f).
        float thumbRadius = IsPointerPressed ? 5f : 6f; // breathing thumb
        float thumbMargin = 4f;
        float thumbDiameter = thumbRadius * 2f;

        float thumbMinX = trackRect.X + thumbMargin + thumbRadius;
        float thumbMaxX = trackRect.X + trackRect.Width - thumbMargin - thumbRadius;

        float thumbX = IsOn ? thumbMaxX : thumbMinX;
        float thumbY = trackRect.Y + trackRect.Height / 2f;

        Rect thumbRect = new Rect(thumbX - thumbRadius, thumbY - thumbRadius, thumbDiameter, thumbDiameter);

        Brush thumbBg;
        Pen? thumbBorder = null;

        if (!IsEnabled)
        {
            thumbBg = ThemeManager.GetBrush("ControlBackground");
            thumbBorder = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), 1f);
        }
        else if (IsOn)
        {
            // Fluent Switch: White thumb when active On
            thumbBg = ThemeManager.CurrentTheme == ElementTheme.Light ? ThemeManager.GetBrush("CardBackground") : ThemeManager.GetBrush("TextPrimary");
        }
        else
        {
            // Muted white / light grey thumb when Off
            thumbBg = IsPointerOver 
                ? (ThemeManager.CurrentTheme == ElementTheme.Light ? ThemeManager.GetBrush("CardBackground") : ThemeManager.GetBrush("TextPrimary"))
                : ThemeManager.GetBrush("TextSecondary");
            thumbBorder = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), 1f);
        }

        var thumbPath = CreateRoundedRectPath(thumbRect, thumbRadius);
        context.DrawPath(thumbBg, thumbBorder, thumbPath);

        // Draw focus ring around track
        if (IsEnabled && IsFocused)
        {
            var focusPen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 2f);
            Rect focusRect = new Rect(trackRect.X - 2f, trackRect.Y - 2f, trackRect.Width + 4f, trackRect.Height + 4f);
            var focusPath = CreateRoundedRectPath(focusRect, CornerRadius + 2f);
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
