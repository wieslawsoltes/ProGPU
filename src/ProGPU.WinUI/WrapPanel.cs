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

public class WrapPanel : Panel
{
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(
            "Orientation",
            typeof(Orientation),
            typeof(WrapPanel),
            new PropertyMetadata(Orientation.Horizontal, (d, e) => {
                var wp = (WrapPanel)d;
                wp.Invalidate();
                wp.InvalidateMeasure();
            }));

    public Orientation Orientation
    {
        get => (Orientation)(GetValue(OrientationProperty) ?? Orientation.Horizontal);
        set => SetValue(OrientationProperty, value);
    }

    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            "ItemWidth",
            typeof(float),
            typeof(WrapPanel),
            new PropertyMetadata(float.NaN, (d, e) => {
                var wp = (WrapPanel)d;
                wp.Invalidate();
                wp.InvalidateMeasure();
            }));

    public float ItemWidth
    {
        get => (float)(GetValue(ItemWidthProperty) ?? float.NaN);
        set => SetValue(ItemWidthProperty, value);
    }

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            "ItemHeight",
            typeof(float),
            typeof(WrapPanel),
            new PropertyMetadata(float.NaN, (d, e) => {
                var wp = (WrapPanel)d;
                wp.Invalidate();
                wp.InvalidateMeasure();
            }));

    public float ItemHeight
    {
        get => (float)(GetValue(ItemHeightProperty) ?? float.NaN);
        set => SetValue(ItemHeightProperty, value);
    }

    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            "HorizontalSpacing",
            typeof(float),
            typeof(WrapPanel),
            new PropertyMetadata(0f, (d, e) => {
                var wp = (WrapPanel)d;
                wp.Invalidate();
                wp.InvalidateMeasure();
            }));

    public float HorizontalSpacing
    {
        get => (float)(GetValue(HorizontalSpacingProperty) ?? 0f);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            "VerticalSpacing",
            typeof(float),
            typeof(WrapPanel),
            new PropertyMetadata(0f, (d, e) => {
                var wp = (WrapPanel)d;
                wp.Invalidate();
                wp.InvalidateMeasure();
            }));

    public float VerticalSpacing
    {
        get => (float)(GetValue(VerticalSpacingProperty) ?? 0f);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float itemWidth = ItemWidth;
        float itemHeight = ItemHeight;
        bool hasItemWidth = !float.IsNaN(itemWidth);
        bool hasItemHeight = !float.IsNaN(itemHeight);

        float horizontalSpacing = HorizontalSpacing;
        float verticalSpacing = VerticalSpacing;

        float panelWidth = 0f;
        float panelHeight = 0f;

        float currentLineWidth = 0f;
        float currentLineHeight = 0f;

        bool isHorizontal = Orientation == Orientation.Horizontal;

        foreach (var child in Children)
        {
            if (child is LayoutNode node)
            {
                // Measure child
                float childAvailW = hasItemWidth ? itemWidth : availableSize.X;
                float childAvailH = hasItemHeight ? itemHeight : availableSize.Y;
                node.Measure(new Vector2(childAvailW, childAvailH));

                float childW = hasItemWidth ? itemWidth : node.DesiredSize.X;
                float childH = hasItemHeight ? itemHeight : node.DesiredSize.Y;

                if (isHorizontal)
                {
                    // Check if we need to wrap
                    if (currentLineWidth + childW > availableSize.X && currentLineWidth > 0f)
                    {
                        panelWidth = Math.Max(panelWidth, currentLineWidth - horizontalSpacing);
                        panelHeight += currentLineHeight + verticalSpacing;
                        currentLineWidth = 0f;
                        currentLineHeight = 0f;
                    }

                    currentLineWidth += childW + horizontalSpacing;
                    currentLineHeight = Math.Max(currentLineHeight, childH);
                }
                else
                {
                    // Check if we need to wrap vertically
                    if (currentLineHeight + childH > availableSize.Y && currentLineHeight > 0f)
                    {
                        panelHeight = Math.Max(panelHeight, currentLineHeight - verticalSpacing);
                        panelWidth += currentLineWidth + horizontalSpacing;
                        currentLineWidth = 0f;
                        currentLineHeight = 0f;
                    }

                    currentLineHeight += childH + verticalSpacing;
                    currentLineWidth = Math.Max(currentLineWidth, childW);
                }
            }
        }

        // Add the last line
        if (isHorizontal)
        {
            panelWidth = Math.Max(panelWidth, currentLineWidth - horizontalSpacing);
            panelHeight += currentLineHeight;
        }
        else
        {
            panelHeight = Math.Max(panelHeight, currentLineHeight - verticalSpacing);
            panelWidth += currentLineWidth;
        }

        return new Vector2(Math.Max(0f, panelWidth), Math.Max(0f, panelHeight));
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float itemWidth = ItemWidth;
        float itemHeight = ItemHeight;
        bool hasItemWidth = !float.IsNaN(itemWidth);
        bool hasItemHeight = !float.IsNaN(itemHeight);

        float horizontalSpacing = HorizontalSpacing;
        float verticalSpacing = VerticalSpacing;

        float currentX = arrangeRect.X;
        float currentY = arrangeRect.Y;

        float currentLineSize = 0f;

        bool isHorizontal = Orientation == Orientation.Horizontal;

        var lineNodes = new System.Collections.Generic.List<LayoutNode>();

        foreach (var child in Children)
        {
            if (child is LayoutNode node)
            {
                float childW = hasItemWidth ? itemWidth : node.DesiredSize.X;
                float childH = hasItemHeight ? itemHeight : node.DesiredSize.Y;

                if (isHorizontal)
                {
                    if (currentX + childW - arrangeRect.X > arrangeRect.Width && lineNodes.Count > 0)
                    {
                        ArrangeLine(lineNodes, arrangeRect.X, currentY, currentLineSize, isHorizontal, hasItemWidth, itemWidth, hasItemHeight, itemHeight, horizontalSpacing);
                        currentY += currentLineSize + verticalSpacing;
                        currentX = arrangeRect.X;
                        currentLineSize = 0f;
                        lineNodes.Clear();
                    }
                    currentX += childW + horizontalSpacing;
                    currentLineSize = Math.Max(currentLineSize, childH);
                    lineNodes.Add(node);
                }
                else
                {
                    if (currentY + childH - arrangeRect.Y > arrangeRect.Height && lineNodes.Count > 0)
                    {
                        ArrangeLine(lineNodes, currentX, arrangeRect.Y, currentLineSize, isHorizontal, hasItemWidth, itemWidth, hasItemHeight, itemHeight, verticalSpacing);
                        currentX += currentLineSize + horizontalSpacing;
                        currentY = arrangeRect.Y;
                        currentLineSize = 0f;
                        lineNodes.Clear();
                    }
                    currentY += childH + verticalSpacing;
                    currentLineSize = Math.Max(currentLineSize, childW);
                    lineNodes.Add(node);
                }
            }
        }

        if (lineNodes.Count > 0)
        {
            ArrangeLine(lineNodes, isHorizontal ? arrangeRect.X : currentX, isHorizontal ? currentY : arrangeRect.Y, currentLineSize, isHorizontal, hasItemWidth, itemWidth, hasItemHeight, itemHeight, isHorizontal ? horizontalSpacing : verticalSpacing);
        }
    }

    private void ArrangeLine(System.Collections.Generic.List<LayoutNode> nodes, float startX, float startY, float lineSize, bool isHorizontal, bool hasItemWidth, float itemWidth, bool hasItemHeight, float itemHeight, float spacing)
    {
        float x = startX;
        float y = startY;

        foreach (var node in nodes)
        {
            float childW = hasItemWidth ? itemWidth : node.DesiredSize.X;
            float childH = hasItemHeight ? itemHeight : node.DesiredSize.Y;

            if (isHorizontal)
            {
                node.Arrange(new Rect(x, y, childW, childH));
                x += childW + spacing;
            }
            else
            {
                node.Arrange(new Rect(x, y, childW, childH));
                y += childH + spacing;
            }
        }
    }
}
