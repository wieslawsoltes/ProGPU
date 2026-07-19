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

public class StackPanel : Panel
{
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(
            "Orientation",
            typeof(Orientation),
            typeof(StackPanel),
            new PropertyMetadata(Orientation.Vertical, (d, e) => {
                var sp = (StackPanel)d;
                sp.Invalidate();
                sp.InvalidateMeasure();
            }));

    public Orientation Orientation
    {
        get => (Orientation)(GetValue(OrientationProperty) ?? Orientation.Vertical);
        set => SetValue(OrientationProperty, value);
    }

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(
            nameof(Spacing),
            typeof(float),
            typeof(StackPanel),
            new PropertyMetadata(0f, (d, e) => ((StackPanel)d).InvalidateMeasure()));

    public float Spacing
    {
        get => (float)(GetValue(SpacingProperty) ?? 0f);
        set => SetValue(SpacingProperty, Math.Max(0f, value));
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float totalWidth = 0f;
        float totalHeight = 0f;

        float paddingH = Padding.Horizontal;
        float paddingV = Padding.Vertical;
        var hasPreviousVisibleChild = false;

        foreach (var child in Children)
        {
            if (child is FrameworkElement { Visibility: Visibility.Collapsed }) continue;
            if (child is LayoutNode node)
            {
                if (hasPreviousVisibleChild)
                {
                    if (Orientation == Orientation.Vertical) totalHeight += Spacing;
                    else totalWidth += Spacing;
                }
                if (Orientation == Orientation.Vertical)
                {
                    float availW = float.IsInfinity(availableSize.X) ? availableSize.X : Math.Max(0f, availableSize.X - paddingH);
                    node.Measure(new Vector2(availW, float.PositiveInfinity));
                    var desired = node.DesiredSize;
                    totalWidth = Math.Max(totalWidth, desired.X);
                    totalHeight += desired.Y;
                }
                else
                {
                    float availH = float.IsInfinity(availableSize.Y) ? availableSize.Y : Math.Max(0f, availableSize.Y - paddingV);
                    node.Measure(new Vector2(float.PositiveInfinity, availH));
                    var desired = node.DesiredSize;
                    totalWidth += desired.X;
                    totalHeight = Math.Max(totalHeight, desired.Y);
                }
                hasPreviousVisibleChild = true;
            }
        }

        return new Vector2(totalWidth, totalHeight);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float offset = 0f;

        var hasPreviousVisibleChild = false;
        foreach (var child in Children)
        {
            if (child is FrameworkElement { Visibility: Visibility.Collapsed }) continue;
            if (child is LayoutNode node)
            {
                if (hasPreviousVisibleChild) offset += Spacing;
                var desired = node.DesiredSize;
                
                if (Orientation == Orientation.Vertical)
                {
                    float childHeight = desired.Y;
                    node.Arrange(new Rect(arrangeRect.X, arrangeRect.Y + offset, arrangeRect.Width, childHeight));
                    offset += childHeight;
                }
                else
                {
                    float childWidth = desired.X;
                    node.Arrange(new Rect(arrangeRect.X + offset, arrangeRect.Y, childWidth, arrangeRect.Height));
                    offset += childWidth;
                }
                hasPreviousVisibleChild = true;
            }
        }
    }
}
