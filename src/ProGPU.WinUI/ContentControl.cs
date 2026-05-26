using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

[ContentProperty(Name = "Content")]
public class ContentControl : Control
{
    public static readonly DependencyProperty ContentProperty =
        DependencyProperty.Register(
            "Content",
            typeof(object),
            typeof(ContentControl),
            new PropertyMetadata(null, OnContentChanged));

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ContentControl)d;
        control.OnContentChanged(e.OldValue, e.NewValue);
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
        if (HasTemplate)
        {
            return base.MeasureOverride(availableSize);
        }

        var inset = BorderThickness + Padding;
        var insetSize = new Vector2(inset.Horizontal, inset.Vertical);
        var contentAvailable = availableSize;
        if (!float.IsInfinity(contentAvailable.X)) contentAvailable.X = Math.Max(0f, contentAvailable.X - insetSize.X);
        if (!float.IsInfinity(contentAvailable.Y)) contentAvailable.Y = Math.Max(0f, contentAvailable.Y - insetSize.Y);

        var contentFe = ContentVisual;
        if (contentFe != null)
        {
            contentFe.Measure(contentAvailable);
            return contentFe.DesiredSize + insetSize;
        }

        return insetSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        if (HasTemplate)
        {
            base.ArrangeOverride(arrangeRect);
            return;
        }

        var inset = BorderThickness + Padding;
        var childRect = new Rect(
            arrangeRect.X + inset.Left,
            arrangeRect.Y + inset.Top,
            Math.Max(0f, arrangeRect.Width - inset.Horizontal),
            Math.Max(0f, arrangeRect.Height - inset.Vertical));

        var contentFe = ContentVisual;
        if (contentFe != null)
        {
            contentFe.Arrange(childRect);
        }
    }
}
