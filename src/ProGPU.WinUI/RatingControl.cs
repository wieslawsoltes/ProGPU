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

namespace Microsoft.UI.Xaml.Controls;

public class RatingControl : Control
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            "Value",
            typeof(double),
            typeof(RatingControl),
            new PropertyMetadata(0.0, (d, e) => ((RatingControl)d).Invalidate()));

    public static readonly DependencyProperty MaxRatingProperty =
        DependencyProperty.Register(
            "MaxRating",
            typeof(int),
            typeof(RatingControl),
            new PropertyMetadata(5, (d, e) => {
                var rating = (RatingControl)d;
                rating.InvalidateMeasure();
                rating.Invalidate();
            }));

    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(
            "IsReadOnly",
            typeof(bool),
            typeof(RatingControl),
            new PropertyMetadata(false, (d, e) => ((RatingControl)d).Invalidate()));

    public static readonly DependencyProperty PlaceholderValueProperty =
        DependencyProperty.Register(
            "PlaceholderValue",
            typeof(double),
            typeof(RatingControl),
            new PropertyMetadata(-1.0, (d, e) => ((RatingControl)d).Invalidate()));

    public double Value
    {
        get => (double)(GetValue(ValueProperty) ?? 0.0);
        set => SetValue(ValueProperty, Math.Clamp(value, 0.0, MaxRating));
    }

    public int MaxRating
    {
        get => (int)(GetValue(MaxRatingProperty) ?? 5);
        set => SetValue(MaxRatingProperty, Math.Max(1, value));
    }

    public bool IsReadOnly
    {
        get => (bool)(GetValue(IsReadOnlyProperty) ?? false);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public double PlaceholderValue
    {
        get => (double)(GetValue(PlaceholderValueProperty) ?? -1.0);
        set => SetValue(PlaceholderValueProperty, value);
    }

    private double _hoverValue = -1.0;
    private const float StarSize = 18f;
    private const float StarSpacing = 4f;

    public event EventHandler<double>? ValueChanged;

    public RatingControl()
    {
        IsTabStop = true;
        Padding = new Thickness(4);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float borderH = BorderThickness.Horizontal;
        float borderV = BorderThickness.Vertical;
        float paddingH = Padding.Horizontal;
        float paddingV = Padding.Vertical;

        float width = MaxRating * StarSize + (MaxRating - 1) * StarSpacing;
        float height = StarSize;

        return new Vector2(
            width + borderH + paddingH,
            height + borderV + paddingV
        );
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled && !IsReadOnly)
        {
            base.OnPointerMoved(e);
            float localX = e.Position.X - BorderThickness.Left - Padding.Left;
            
            // Determine which star is hovered
            double rawVal = (localX / (StarSize + StarSpacing)) + 1.0;
            double clamped = Math.Clamp(Math.Ceiling(rawVal), 1.0, MaxRating);

            if (_hoverValue != clamped)
            {
                _hoverValue = clamped;
                Invalidate();
            }
        }
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        if (IsEnabled && !IsReadOnly)
        {
            base.OnPointerExited(e);
            if (_hoverValue >= 0.0)
            {
                _hoverValue = -1.0;
                Invalidate();
            }
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled && !IsReadOnly && IsPointerPressed && IsPointerOver)
        {
            base.OnPointerReleased(e);
            if (_hoverValue >= 1.0 && _hoverValue <= MaxRating)
            {
                var oldVal = Value;
                Value = _hoverValue;
                if (oldVal != Value)
                {
                    ValueChanged?.Invoke(this, Value);
                }
            }
        }
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused && !IsReadOnly)
        {
            if (e.Key == Silk.NET.Input.Key.Left || e.Key == Silk.NET.Input.Key.Down)
            {
                var oldVal = Value;
                Value = Math.Max(0.0, Value - 1.0);
                if (oldVal != Value)
                {
                    ValueChanged?.Invoke(this, Value);
                }
                e.Handled = true;
                return;
            }
            else if (e.Key == Silk.NET.Input.Key.Right || e.Key == Silk.NET.Input.Key.Up)
            {
                var oldVal = Value;
                Value = Math.Min(MaxRating, Value + 1.0);
                if (oldVal != Value)
                {
                    ValueChanged?.Invoke(this, Value);
                }
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    private static PathGeometry CreateStarGeometry(float cx, float cy, float r)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure(Vector2.Zero) { IsClosed = true };
        int points = 5;
        double innerRadius = r * 0.4;
        
        for (int i = 0; i < 2 * points; i++)
        {
            double angle = i * Math.PI / points - Math.PI / 2;
            double radius = (i % 2 == 0) ? r : innerRadius;
            float x = (float)(cx + radius * Math.Cos(angle));
            float y = (float)(cy + radius * Math.Sin(angle));
            
            if (i == 0)
            {
                fig.StartPoint = new Vector2(x, y);
            }
            else
            {
                fig.Segments.Add(new LineSegment(new Vector2(x, y)));
            }
        }
        geo.Figures.Add(fig);
        return geo;
    }

    public override void OnRender(DrawingContext context)
    {
        float startX = BorderThickness.Left + Padding.Left;
        float startY = BorderThickness.Top + Padding.Top;
        float halfStar = StarSize / 2f;

        var activeTheme = this.ActualTheme;
        var borderBrush = ThemeManager.GetBrush("ControlBorder", activeTheme);
        var accentBrush = ThemeManager.GetBrush("SystemAccentColor", activeTheme);
        var accentHoverBrush = ThemeManager.GetBrush("SystemAccentColorLight1", activeTheme);
        var textSecondary = ThemeManager.GetBrush("TextSecondary", activeTheme);

        var accentPen = new Pen(accentBrush, 1f);
        var accentHoverPen = new Pen(accentHoverBrush, 1f);
        var borderPen = new Pen(borderBrush, 1f);

        // Semi-transparent brushes for placeholder or disabled
        var disabledBrush = new SolidColorBrush(activeTheme == ElementTheme.Light ? new Vector4(0f, 0f, 0f, 0.2f) : new Vector4(1f, 1f, 1f, 0.2f));
        var placeholderBrush = new SolidColorBrush(activeTheme == ElementTheme.Light ? new Vector4(0f, 0f, 0f, 0.15f) : new Vector4(1f, 1f, 1f, 0.15f));

        double activeRating = (_hoverValue >= 0.0) ? _hoverValue : Value;

        for (int i = 0; i < MaxRating; i++)
        {
            float cx = startX + i * (StarSize + StarSpacing) + halfStar;
            float cy = startY + halfStar;

            // Generate high-performance pixel-perfect 5-point star vector geometry
            var starGeo = CreateStarGeometry(cx, cy, halfStar - 0.5f);

            if (!IsEnabled)
            {
                if (i < Value)
                {
                    context.DrawPath(disabledBrush, null, starGeo);
                }
                else
                {
                    context.DrawPath(null, new Pen(disabledBrush, 1f), starGeo);
                }
            }
            else if (_hoverValue >= 0.0)
            {
                // Hover rating highlights up to the hover point
                if (i < _hoverValue)
                {
                    context.DrawPath(accentHoverBrush, null, starGeo);
                }
                else
                {
                    context.DrawPath(null, accentHoverPen, starGeo);
                }
            }
            else if (Value > 0.0)
            {
                // Active persistent rating
                if (i < Value)
                {
                    context.DrawPath(accentBrush, null, starGeo);
                }
                else
                {
                    context.DrawPath(null, accentPen, starGeo);
                }
            }
            else if (PlaceholderValue >= 0.0)
            {
                // Placeholder rating
                if (i < PlaceholderValue)
                {
                    context.DrawPath(placeholderBrush, null, starGeo);
                }
                else
                {
                    context.DrawPath(null, borderPen, starGeo);
                }
            }
            else
            {
                // Empty state border star
                context.DrawPath(null, borderPen, starGeo);
            }
        }

        // Draw dynamic high-contrast active blue focus border snug around all stars
        if (IsEnabled && IsFocused && InputSystem.IsKeyboardFocusActive)
        {
            var focusPen = new Pen(accentBrush, 1.5f);
            float totalW = MaxRating * StarSize + (MaxRating - 1) * StarSpacing;
            Rect focusRect = new Rect(startX - 2f, startY - 2f, totalW + 4f, StarSize + 4f);
            context.DrawRoundedRectangle(null, focusPen, focusRect, 4f);
        }

        base.OnRender(context);
    }
}
