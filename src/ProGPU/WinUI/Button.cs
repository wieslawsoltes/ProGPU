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
            // Ambient shadow (offset Y=2, very soft, low opacity)
            context.FillRoundedRectangle(new SolidColorBrush(0x0000000A), new Rect(0, 2, Size.X, Size.Y), CornerRadius);

            // Penumbra shadow (offset Y=1, tighter, slightly higher opacity)
            context.FillRoundedRectangle(new SolidColorBrush(0x00000014), new Rect(0, 1, Size.X, Size.Y), CornerRadius);
        }

        // Draw main button background and border
        context.DrawRoundedRectangle(bg, pen, new Rect(Vector2.Zero, Size), CornerRadius);

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
                context.FillRoundedRectangle(overlayBrush, new Rect(Vector2.Zero, Size), CornerRadius);
            }
        }

        // Draw active focus ring indicator
        if (IsEnabled && IsFocused)
        {
            var focusPen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 2f); // Sharp Segoe Blue active focus ring
            // Slightly inset focus ring for clean aesthetics
            float inset = 1.5f;
            var focusRect = new Rect(inset, inset, Size.X - 2 * inset, Size.Y - 2 * inset);
            float focusR = Math.Max(0f, CornerRadius - inset);
            context.DrawRoundedRectangle(null, focusPen, focusRect, focusR);
        }

        base.OnRender(context);
    }
}
