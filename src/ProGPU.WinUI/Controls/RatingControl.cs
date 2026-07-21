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
            new PropertyMetadata(0.0) { AffectsRender = true });

    public static readonly DependencyProperty MaxRatingProperty =
        DependencyProperty.Register(
            "MaxRating",
            typeof(int),
            typeof(RatingControl),
            new PropertyMetadata(5, (d, e) => {
                ((RatingControl)d)._starGeometries = null;
            }) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(
            "IsReadOnly",
            typeof(bool),
            typeof(RatingControl),
            new PropertyMetadata(false) { AffectsRender = true });

    public static readonly DependencyProperty PlaceholderValueProperty =
        DependencyProperty.Register(
            "PlaceholderValue",
            typeof(double),
            typeof(RatingControl),
            new PropertyMetadata(-1.0) { AffectsRender = true });

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

    private static readonly SolidColorBrush LightDisabledBrush = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.2f));
    private static readonly SolidColorBrush DarkDisabledBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.2f));
    private static readonly SolidColorBrush LightPlaceholderBrush = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.15f));
    private static readonly SolidColorBrush DarkPlaceholderBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.15f));

    private double _hoverValue = -1.0;
    private const float StarSize = 18f;
    private const float StarSpacing = 4f;
    private PathGeometry[]? _starGeometries;
    private FlowDirection _starGeometryFlowDirection = FlowDirection.LeftToRight;

    public event EventHandler<double>? ValueChanged;

    public RatingControl()
    {
        IsTabStop = true;
        Padding = new Thickness(4);
    }

    protected override void OnPropertyChanged(DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FlowDirectionProperty) _starGeometries = null;
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
        var newSize = new Vector2(arrangeRect.Width, arrangeRect.Height);
        if (Size != newSize)
        {
            Size = newSize;
            _starGeometries = null;
        }
    }

    private double GetRatingAtPosition(Vector2 position)
    {
        var localX = position.X - BorderThickness.Left - Padding.Left;
        var pitch = StarSize + StarSpacing;
        var rating = MathF.Floor(Math.Max(0f, localX) / pitch) + 1f;
        return Math.Clamp(rating, 1f, MaxRating);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled && !IsReadOnly)
        {
            _hoverValue = GetRatingAtPosition(e.GetCurrentPoint(this).Position);
            Invalidate();
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled && !IsReadOnly)
        {
            var rating = GetRatingAtPosition(e.GetCurrentPoint(this).Position);

            if (_hoverValue != rating)
            {
                _hoverValue = rating;
                Invalidate();
            }
        }
        base.OnPointerMoved(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        if (IsEnabled && !IsReadOnly)
        {
            if (_hoverValue >= 0.0)
            {
                _hoverValue = -1.0;
                Invalidate();
            }
        }
        base.OnPointerExited(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        var shouldCommit = IsEnabled && !IsReadOnly && IsPointerPressed && IsPointerOver;
        var rating = shouldCommit ? GetRatingAtPosition(e.GetCurrentPoint(this).Position) : -1.0;
        base.OnPointerReleased(e);
        if (shouldCommit)
        {
            var oldVal = Value;
            Value = rating;
            if (oldVal != Value)
            {
                ValueChanged?.Invoke(this, Value);
            }
            e.Handled = true;
        }
    }

    public override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        _hoverValue = -1.0;
        Invalidate();
        base.OnPointerCanceled(e);
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused && !IsReadOnly)
        {
            bool decrease = e.Key == Silk.NET.Input.Key.Down ||
                (e.Key == Silk.NET.Input.Key.Left && FlowDirection != FlowDirection.RightToLeft) ||
                (e.Key == Silk.NET.Input.Key.Right && FlowDirection == FlowDirection.RightToLeft);
            bool increase = e.Key == Silk.NET.Input.Key.Up ||
                (e.Key == Silk.NET.Input.Key.Right && FlowDirection != FlowDirection.RightToLeft) ||
                (e.Key == Silk.NET.Input.Key.Left && FlowDirection == FlowDirection.RightToLeft);
            if (decrease)
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
            else if (increase)
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
        var accentBrush = ThemeManager.GetBrush("SystemAccentColor", activeTheme);
        var accentHoverBrush = ThemeManager.GetBrush("SystemAccentColorLight1", activeTheme);
        var accentPen = ThemeManager.GetPen("SystemAccentColor", 1f, activeTheme);
        var accentHoverPen = ThemeManager.GetPen("SystemAccentColorLight1", 1f, activeTheme);
        var borderPen = ThemeManager.GetPen("ControlBorder", 1f, activeTheme);

        // Semi-transparent brushes for placeholder or disabled
        var disabledBrush = activeTheme == ElementTheme.Light ? LightDisabledBrush : DarkDisabledBrush;
        var placeholderBrush = activeTheme == ElementTheme.Light ? LightPlaceholderBrush : DarkPlaceholderBrush;

        double activeRating = (_hoverValue >= 0.0) ? _hoverValue : Value;

        if (_starGeometries == null || _starGeometries.Length != MaxRating ||
            _starGeometryFlowDirection != FlowDirection)
        {
            _starGeometryFlowDirection = FlowDirection;
            _starGeometries = new PathGeometry[MaxRating];
            for (int i = 0; i < MaxRating; i++)
            {
                float logicalCenter = startX + i * (StarSize + StarSpacing) + halfStar;
                float cx = FlowDirection == FlowDirection.RightToLeft
                    ? Size.X - logicalCenter
                    : logicalCenter;
                float cy = startY + halfStar;
                _starGeometries[i] = CreateStarGeometry(cx, cy, halfStar - 0.5f);
            }
        }

        for (int i = 0; i < MaxRating; i++)
        {
            var starGeo = _starGeometries[i];

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
            var focusPen = ThemeManager.GetPen("SystemAccentColor", 1.5f, activeTheme);
            float totalW = MaxRating * StarSize + (MaxRating - 1) * StarSpacing;
            Rect focusRect = new Rect(startX - 2f, startY - 2f, totalW + 4f, StarSize + 4f);
            context.DrawRoundedRectangle(null, focusPen, focusRect, 4f);
        }

        base.OnRender(context);
    }
}
