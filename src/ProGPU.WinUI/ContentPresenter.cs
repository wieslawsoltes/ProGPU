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

public class ContentPresenter : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(
            "Background",
            typeof(Brush),
            typeof(ContentPresenter),
            new PropertyMetadata(null, (d, e) => ((ContentPresenter)d).Invalidate()));

    public Brush? Background
    {
        get => GetValue(BackgroundProperty) as Brush;
        set => SetValue(BackgroundProperty, value);
    }

    public static readonly DependencyProperty BorderBrushProperty =
        DependencyProperty.Register(
            "BorderBrush",
            typeof(Brush),
            typeof(ContentPresenter),
            new PropertyMetadata(null, (d, e) => ((ContentPresenter)d).Invalidate()));

    public Brush? BorderBrush
    {
        get => GetValue(BorderBrushProperty) as Brush;
        set => SetValue(BorderBrushProperty, value);
    }

    public static readonly DependencyProperty BorderThicknessProperty =
        DependencyProperty.Register(
            "BorderThickness",
            typeof(Thickness),
            typeof(ContentPresenter),
            new PropertyMetadata(default(Thickness), (d, e) => {
                var cp = (ContentPresenter)d;
                cp.Invalidate();
                cp.InvalidateMeasure();
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
            typeof(ContentPresenter),
            new PropertyMetadata(0f, (d, e) => ((ContentPresenter)d).Invalidate()));

    public float CornerRadius
    {
        get => (float)(GetValue(CornerRadiusProperty) ?? 0f);
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty ContentProperty =
        DependencyProperty.Register(
            "Content",
            typeof(object),
            typeof(ContentPresenter),
            new PropertyMetadata(null, OnContentChanged));

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cp = (ContentPresenter)d;
        cp.OnContentChanged(e.OldValue, e.NewValue);
    }

    protected virtual void OnContentChanged(object? oldValue, object? newValue)
    {
        if (oldValue is FrameworkElement oldFe)
        {
            RemoveChild(oldFe);
        }

        if (newValue != null)
        {
            if (newValue is FrameworkElement newFe)
            {
                AddChild(newFe);
            }
            else
            {
                // Auto-wrap non-FrameworkElement content in a RichTextBlock
                var tb = new RichTextBlock();
                tb.Inlines.Add(new Run { Text = newValue.ToString() ?? string.Empty });
                AddChild(tb);
            }
        }

        Invalidate();
        InvalidateMeasure();
    }

    public static readonly DependencyProperty HorizontalContentAlignmentProperty =
        DependencyProperty.Register(
            "HorizontalContentAlignment",
            typeof(HorizontalAlignment),
            typeof(ContentPresenter),
            new PropertyMetadata(HorizontalAlignment.Stretch, (d, e) => {
                var cp = (ContentPresenter)d;
                cp.InvalidateMeasure();
                cp.InvalidateArrange();
            }));

    public HorizontalAlignment HorizontalContentAlignment
    {
        get => (HorizontalAlignment)(GetValue(HorizontalContentAlignmentProperty) ?? HorizontalAlignment.Stretch);
        set => SetValue(HorizontalContentAlignmentProperty, value);
    }

    public static readonly DependencyProperty VerticalContentAlignmentProperty =
        DependencyProperty.Register(
            "VerticalContentAlignment",
            typeof(VerticalAlignment),
            typeof(ContentPresenter),
            new PropertyMetadata(VerticalAlignment.Stretch, (d, e) => {
                var cp = (ContentPresenter)d;
                cp.InvalidateMeasure();
                cp.InvalidateArrange();
            }));

    public VerticalAlignment VerticalContentAlignment
    {
        get => (VerticalAlignment)(GetValue(VerticalContentAlignmentProperty) ?? VerticalAlignment.Stretch);
        set => SetValue(VerticalContentAlignmentProperty, value);
    }

    protected FrameworkElement? ContentVisual
    {
        get
        {
            if (Content is FrameworkElement fe) return fe;
            foreach (var child in Children)
            {
                if (child is FrameworkElement childFe) return childFe;
            }
            return null;
        }
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
        var contentVisual = ContentVisual;
        if (contentVisual != null)
        {
            contentVisual.Measure(contentAvail);
            contentDesired = contentVisual.DesiredSize;
        }

        // Return desired size with ONLY BorderThickness. LayoutNode automatically adds Padding!
        return contentDesired + new Vector2(borderH, borderV);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        var contentVisual = ContentVisual;
        if (contentVisual != null)
        {
            // Only apply BorderThickness insets. LayoutNode already applied Padding to arrangeRect!
            float leftInset = BorderThickness.Left;
            float topInset = BorderThickness.Top;
            float rightInset = BorderThickness.Right;
            float bottomInset = BorderThickness.Bottom;

            float innerW = Math.Max(0f, arrangeRect.Width - (leftInset + rightInset));
            float innerH = Math.Max(0f, arrangeRect.Height - (topInset + bottomInset));

            var horizAlign = HorizontalContentAlignment;
            var vertAlign = VerticalContentAlignment;

            float childW = contentVisual.DesiredSize.X;
            float childH = contentVisual.DesiredSize.Y;

            if (horizAlign == HorizontalAlignment.Stretch)
            {
                childW = innerW;
            }
            else
            {
                childW = Math.Min(innerW, childW);
            }

            if (vertAlign == VerticalAlignment.Stretch)
            {
                childH = innerH;
            }
            else
            {
                childH = Math.Min(innerH, childH);
            }

            float childX = arrangeRect.X + leftInset;
            if (horizAlign == HorizontalAlignment.Center)
            {
                childX += (innerW - childW) / 2f;
            }
            else if (horizAlign == HorizontalAlignment.Right)
            {
                childX += (innerW - childW);
            }

            float childY = arrangeRect.Y + topInset;
            if (vertAlign == VerticalAlignment.Center)
            {
                childY += (innerH - childH) / 2f;
            }
            else if (vertAlign == VerticalAlignment.Bottom)
            {
                childY += (innerH - childH);
            }

            contentVisual.Arrange(new Rect(childX, childY, childW, childH));
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
