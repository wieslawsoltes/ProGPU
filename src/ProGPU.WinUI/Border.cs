using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

[ContentProperty(Name = "Child")]
public class Border : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(
            "Background",
            typeof(Brush),
            typeof(Border),
            new PropertyMetadata(null, (d, e) => ((Border)d).Invalidate()));

    public Brush? Background
    {
        get => GetValue(BackgroundProperty) as Brush;
        set => SetValue(BackgroundProperty, value);
    }

    public static readonly DependencyProperty BorderBrushProperty =
        DependencyProperty.Register(
            "BorderBrush",
            typeof(Brush),
            typeof(Border),
            new PropertyMetadata(null, (d, e) => ((Border)d).Invalidate()));

    public Brush? BorderBrush
    {
        get => GetValue(BorderBrushProperty) as Brush;
        set => SetValue(BorderBrushProperty, value);
    }

    public static readonly DependencyProperty BorderThicknessProperty =
        DependencyProperty.Register(
            "BorderThickness",
            typeof(Thickness),
            typeof(Border),
            new PropertyMetadata(default(Thickness), (d, e) => {
                var b = (Border)d;
                b.Invalidate();
                b.InvalidateMeasure();
            }));

    public Thickness BorderThickness
    {
        get => (Thickness)(GetValue(BorderThicknessProperty) ?? default(Thickness));
        set => SetValue(BorderThicknessProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            "CornerRadius",
            typeof(float),
            typeof(Border),
            new PropertyMetadata(0f, (d, e) => ((Border)d).Invalidate()));

    public float CornerRadius
    {
        get => (float)(GetValue(CornerRadiusProperty) ?? 0f);
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty ChildProperty =
        DependencyProperty.Register(
            "Child",
            typeof(FrameworkElement),
            typeof(Border),
            new PropertyMetadata(null, (d, e) => ((Border)d).OnChildChanged(e.OldValue as FrameworkElement, e.NewValue as FrameworkElement)));

    public FrameworkElement? Child
    {
        get => GetValue(ChildProperty) as FrameworkElement;
        set => SetValue(ChildProperty, value);
    }

    private void OnChildChanged(FrameworkElement? oldValue, FrameworkElement? newValue)
    {
        if (oldValue != null) RemoveChild(oldValue);
        if (newValue != null) AddChild(newValue);
        Invalidate();
        InvalidateMeasure();
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

        // Return desired size with ONLY BorderThickness. LayoutNode automatically adds Padding!
        return childDesired + new Vector2(borderH, borderV);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        if (Child != null)
        {
            // Only apply BorderThickness insets. LayoutNode already applied Padding to arrangeRect!
            float leftInset = BorderThickness.Left;
            float topInset = BorderThickness.Top;
            float rightInset = BorderThickness.Right;
            float bottomInset = BorderThickness.Bottom;

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
        var activeFamily = ActualThemeFamily;
        var activeTheme = ActualTheme;
        var parentButton = Parent as Button;
        var parentCombo = Parent as ComboBox;

        if (parentButton != null && activeFamily == VisualThemeFamily.macOS)
        {
            // Draw macOS premium Aqua button
            bool isPressed = parentButton.IsPointerPressed && parentButton.IsPointerOver;
            bool isHovered = parentButton.IsPointerOver && !isPressed;
            bool isEnabled = parentButton.IsEnabled;
            
            bool isAccent = false;
            if (parentButton.Style?.Setters != null)
            {
                foreach (var setter in parentButton.Style.Setters)
                {
                    if (setter.Value?.ToString()?.Contains("AccentButton") == true || 
                        (setter.Property == "Background" && setter.Value?.ToString()?.Contains("SystemAccentColor") == true))
                    {
                        isAccent = true;
                        break;
                    }
                }
            }

            Brush? bg = null;
            Pen? pen = null;

            var startPt = new Vector2(Size.X / 2f, 0f);
            var endPt = new Vector2(Size.X / 2f, Size.Y);

            if (!isEnabled)
            {
                Vector4 disabledBg = activeTheme == ElementTheme.Light 
                    ? new Vector4(0.95f, 0.95f, 0.95f, 1f) 
                    : new Vector4(0.2f, 0.2f, 0.2f, 1f);
                bg = new SolidColorBrush(disabledBg);
                
                Vector4 disabledBorder = activeTheme == ElementTheme.Light
                    ? new Vector4(0.85f, 0.85f, 0.85f, 1f)
                    : new Vector4(0.15f, 0.15f, 0.15f, 1f);
                pen = new Pen(new SolidColorBrush(disabledBorder), 1f);
            }
            else if (isAccent)
            {
                Vector4 topColor, bottomColor;
                if (isPressed)
                {
                    topColor = new Vector4(0f, 0.35f, 0.75f, 1f);
                    bottomColor = new Vector4(0f, 0.28f, 0.62f, 1f);
                }
                else if (isHovered)
                {
                    topColor = new Vector4(0.35f, 0.68f, 1.0f, 1f);
                    bottomColor = new Vector4(0.0f, 0.52f, 1.0f, 1f);
                }
                else
                {
                    topColor = new Vector4(0.25f, 0.6f, 1.0f, 1f);
                    bottomColor = new Vector4(0.0f, 0.478f, 1.0f, 1f);
                }
                bg = new LinearGradientBrush(startPt, endPt, new GradientStop[] {
                    new GradientStop(topColor, 0f),
                    new GradientStop(bottomColor, 1f)
                });
                
                Vector4 borderCol = activeTheme == ElementTheme.Light
                    ? new Vector4(0.0f, 0.35f, 0.8f, 1f)
                    : new Vector4(0.0f, 0.3f, 0.7f, 1f);
                pen = new Pen(new SolidColorBrush(borderCol), 1f);
            }
            else
            {
                Vector4 topColor, bottomColor;
                if (activeTheme == ElementTheme.Light)
                {
                    if (isPressed)
                    {
                        topColor = new Vector4(0.84f, 0.84f, 0.84f, 1f);
                        bottomColor = new Vector4(0.75f, 0.75f, 0.75f, 1f);
                    }
                    else if (isHovered)
                    {
                        topColor = new Vector4(1f, 1f, 1f, 1f);
                        bottomColor = new Vector4(0.92f, 0.92f, 0.94f, 1f);
                    }
                    else
                    {
                        topColor = new Vector4(1f, 1f, 1f, 1f);
                        bottomColor = new Vector4(0.88f, 0.88f, 0.90f, 1f);
                    }
                }
                else
                {
                    if (isPressed)
                    {
                        topColor = new Vector4(0.22f, 0.22f, 0.22f, 1f);
                        bottomColor = new Vector4(0.18f, 0.18f, 0.18f, 1f);
                    }
                    else if (isHovered)
                    {
                        topColor = new Vector4(0.35f, 0.35f, 0.35f, 1f);
                        bottomColor = new Vector4(0.28f, 0.28f, 0.28f, 1f);
                    }
                    else
                    {
                        topColor = new Vector4(0.3f, 0.3f, 0.3f, 1f);
                        bottomColor = new Vector4(0.24f, 0.24f, 0.24f, 1f);
                    }
                }
                
                bg = new LinearGradientBrush(startPt, endPt, new GradientStop[] {
                    new GradientStop(topColor, 0f),
                    new GradientStop(bottomColor, 1f)
                });
                
                Vector4 borderCol = activeTheme == ElementTheme.Light
                    ? new Vector4(0.7f, 0.7f, 0.7f, 1f)
                    : new Vector4(0.18f, 0.18f, 0.18f, 1f);
                pen = new Pen(new SolidColorBrush(borderCol), 1f);
            }

            if (activeTheme == ElementTheme.Light && isEnabled)
            {
                var shadowColor = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.05f));
                context.FillRoundedRectangle(shadowColor, new Rect(0f, 1f, Size.X, Size.Y), CornerRadius);
            }

            context.DrawRoundedRectangle(bg, pen, new Rect(Vector2.Zero, Size), CornerRadius);

            if (activeTheme == ElementTheme.Light && isEnabled && !isPressed)
            {
                var highlightPen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.45f)), 1f);
                context.DrawLine(highlightPen, new Vector2(CornerRadius, 1f), new Vector2(Size.X - CornerRadius, 1f));
            }
        }
        else if (parentCombo != null && activeFamily == VisualThemeFamily.macOS)
        {
            // Draw macOS premium ComboBox background
            bool isPressed = parentCombo.IsPointerPressed || parentCombo.IsDropDownOpen;
            bool isHovered = parentCombo.IsPointerOver && !isPressed;
            bool isEnabled = parentCombo.IsEnabled;

            Brush? bg = null;
            Pen? pen = null;

            var startPt = new Vector2(Size.X / 2f, 0f);
            var endPt = new Vector2(Size.X / 2f, Size.Y);

            if (!isEnabled)
            {
                Vector4 disabledBg = activeTheme == ElementTheme.Light 
                    ? new Vector4(0.95f, 0.95f, 0.95f, 1f) 
                    : new Vector4(0.2f, 0.2f, 0.2f, 1f);
                bg = new SolidColorBrush(disabledBg);
                
                Vector4 disabledBorder = activeTheme == ElementTheme.Light
                    ? new Vector4(0.85f, 0.85f, 0.85f, 1f)
                    : new Vector4(0.15f, 0.15f, 0.15f, 1f);
                pen = new Pen(new SolidColorBrush(disabledBorder), 1f);
            }
            else
            {
                Vector4 topColor, bottomColor;
                if (activeTheme == ElementTheme.Light)
                {
                    if (isPressed)
                    {
                        topColor = new Vector4(0.9f, 0.9f, 0.92f, 1f);
                        bottomColor = new Vector4(0.82f, 0.82f, 0.85f, 1f);
                    }
                    else if (isHovered)
                    {
                        topColor = new Vector4(1f, 1f, 1f, 1f);
                        bottomColor = new Vector4(0.94f, 0.94f, 0.96f, 1f);
                    }
                    else
                    {
                        topColor = new Vector4(1f, 1f, 1f, 1f);
                        bottomColor = new Vector4(0.90f, 0.90f, 0.92f, 1f);
                    }
                }
                else
                {
                    if (isPressed)
                    {
                        topColor = new Vector4(0.25f, 0.25f, 0.27f, 1f);
                        bottomColor = new Vector4(0.2f, 0.2f, 0.22f, 1f);
                    }
                    else if (isHovered)
                    {
                        topColor = new Vector4(0.32f, 0.32f, 0.34f, 1f);
                        bottomColor = new Vector4(0.26f, 0.26f, 0.28f, 1f);
                    }
                    else
                    {
                        topColor = new Vector4(0.28f, 0.28f, 0.30f, 1f);
                        bottomColor = new Vector4(0.22f, 0.22f, 0.24f, 1f);
                    }
                }
                
                bg = new LinearGradientBrush(startPt, endPt, new[] {
                    new GradientStop(topColor, 0f),
                    new GradientStop(bottomColor, 1f)
                });
                
                Vector4 borderCol = activeTheme == ElementTheme.Light
                    ? new Vector4(0.76f, 0.76f, 0.76f, 1f)
                    : new Vector4(0.33f, 0.33f, 0.33f, 1f);
                pen = new Pen(new SolidColorBrush(borderCol), 1f);
            }

            if (activeTheme == ElementTheme.Light && isEnabled)
            {
                var shadowColor = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.04f));
                context.FillRoundedRectangle(shadowColor, new Rect(0f, 1f, Size.X, Size.Y), CornerRadius);
            }

            context.DrawRoundedRectangle(bg, pen, new Rect(Vector2.Zero, Size), CornerRadius);

            if (activeTheme == ElementTheme.Light && isEnabled && !isPressed)
            {
                var highlightPen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.45f)), 1f);
                context.DrawLine(highlightPen, new Vector2(CornerRadius, 1f), new Vector2(Size.X - CornerRadius, 1f));
            }
        }
        else if (parentButton != null)
        {
            if (IsEnabled)
            {
                context.FillRoundedRectangle(ThemeManager.GetBrush("ButtonAmbientShadow", activeTheme, activeFamily), new Rect(0, 2, Size.X, Size.Y), CornerRadius);
                context.FillRoundedRectangle(ThemeManager.GetBrush("ButtonPenumbraShadow", activeTheme, activeFamily), new Rect(0, 1, Size.X, Size.Y), CornerRadius);
            }

            Brush? bg = parentButton.GetCurrentBackground() ?? Background;
            var borderBrush = parentButton.GetCurrentBorderBrush() ?? BorderBrush;
            Pen? pen = borderBrush != null && BorderThickness.Left > 0 ? new Pen(borderBrush, BorderThickness.Left) : null;
            context.DrawRoundedRectangle(bg, pen, new Rect(Vector2.Zero, Size), CornerRadius);
        }
        else
        {
            // Standard general-purpose Border rendering
            if (Background != null || (BorderBrush != null && BorderThickness.Left > 0))
            {
                var pen = BorderBrush != null && BorderThickness.Left > 0 ? new Pen(BorderBrush, BorderThickness.Left) : null;
                context.DrawRoundedRectangle(Background, pen, new Rect(Vector2.Zero, Size), CornerRadius);
            }
        }

        // Draw context-aware cascading focus rings
        if (Parent is Control pControl && pControl.IsFocused && pControl.IsEnabled)
        {
            var accentColor = ThemeManager.GetBrush("SystemAccentColor", activeTheme, activeFamily);
            if (activeFamily == VisualThemeFamily.macOS)
            {
                // 2px thick outer blue glow matching macOS
                var accentVec = (accentColor as SolidColorBrush)?.Color ?? new Vector4(0f, 0.478f, 1f, 1f);
                var focusPen = new Pen(new SolidColorBrush(new Vector4(accentVec.X, accentVec.Y, accentVec.Z, 0.5f)), 2f);
                Rect focusRect = new Rect(-2.5f, -2.5f, Size.X + 5f, Size.Y + 5f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, CornerRadius + 2.5f);
            }
            else
            {
                // Sharp inset focus ring matching Fluent
                var focusPen = new Pen(accentColor, 1.5f);
                float inset = 1.5f;
                var focusRect = new Rect(inset, inset, Size.X - 2 * inset, Size.Y - 2 * inset);
                context.DrawRoundedRectangle(null, focusPen, focusRect, Math.Max(0f, CornerRadius - inset));
            }
        }

        base.OnRender(context);
    }
}
