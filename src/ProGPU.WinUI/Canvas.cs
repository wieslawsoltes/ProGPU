using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public class Canvas : Panel
{
    public static readonly DependencyProperty LeftProperty =
        DependencyProperty.RegisterAttached(
            "Left",
            typeof(float),
            typeof(Canvas),
            new PropertyMetadata(0f, OnLeftTopChanged));

    public static readonly DependencyProperty TopProperty =
        DependencyProperty.RegisterAttached(
            "Top",
            typeof(float),
            typeof(Canvas),
            new PropertyMetadata(0f, OnLeftTopChanged));

    private static void OnLeftTopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Visual child && child.Parent is Canvas canvas)
        {
            canvas.InvalidateArrange();
            canvas.Invalidate();
        }
    }

    public static void SetLeft(Visual child, float left)
    {
        if (child is DependencyObject d)
        {
            d.SetValue(LeftProperty, left);
        }
        else if (child.Parent is Canvas canvas)
        {
            canvas.InvalidateArrange();
            canvas.Invalidate();
        }
    }

    public static void SetTop(Visual child, float top)
    {
        if (child is DependencyObject d)
        {
            d.SetValue(TopProperty, top);
        }
        else if (child.Parent is Canvas canvas)
        {
            canvas.InvalidateArrange();
            canvas.Invalidate();
        }
    }

    public static float GetLeft(Visual child)
    {
        if (child is DependencyObject d)
        {
            return (float)(d.GetValue(LeftProperty) ?? 0f);
        }
        return 0f;
    }

    public static float GetTop(Visual child)
    {
        if (child is DependencyObject d)
        {
            return (float)(d.GetValue(TopProperty) ?? 0f);
        }
        return 0f;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        foreach (var child in Children)
        {
            if (child is LayoutNode node)
            {
                node.Measure(new Vector2(float.PositiveInfinity, float.PositiveInfinity));
            }
        }
        return Vector2.Zero;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        foreach (var child in Children)
        {
            if (child is LayoutNode node)
            {
                float left = GetLeft(node);
                float top = GetTop(node);
                node.Arrange(new Rect(arrangeRect.X + left, arrangeRect.Y + top, node.DesiredSize.X, node.DesiredSize.Y));
            }
        }
    }
}
