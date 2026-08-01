using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls.Primitives;

/// <summary>
/// A horizontal virtualizing items panel with regular item snap points.
/// </summary>
public sealed class CarouselPanel :
    VirtualizingPanel,
    IScrollSnapPointsInfo
{
    private float _extentWidth;
    private float _itemWidth;

    public override bool IsHorizontal => true;

    public override float TotalVirtualWidth => _extentWidth;

    public bool AreHorizontalSnapPointsRegular => true;

    public bool AreVerticalSnapPointsRegular => false;

    public event EventHandler<object?>? HorizontalSnapPointsChanged;

    public event EventHandler<object?>? VerticalSnapPointsChanged;

    public float GetRegularSnapPoints(
        Orientation orientation,
        SnapPointsAlignment alignment,
        out float offset)
    {
        offset = alignment switch
        {
            SnapPointsAlignment.Center => _itemWidth * 0.5f,
            SnapPointsAlignment.Far => _itemWidth,
            _ => 0f
        };
        return orientation == Orientation.Horizontal ? _itemWidth : 0f;
    }

    public IReadOnlyList<float> GetIrregularSnapPoints(
        Orientation orientation,
        SnapPointsAlignment alignment) =>
        Array.Empty<float>();

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var totalWidth = 0f;
        var maximumHeight = 0f;
        var measuredCount = 0;

        foreach (var child in Children)
        {
            if (child is not LayoutNode node)
                continue;

            node.Measure(new Vector2(float.PositiveInfinity, availableSize.Y));
            totalWidth += node.DesiredSize.X;
            maximumHeight = Math.Max(maximumHeight, node.DesiredSize.Y);
            _itemWidth = Math.Max(_itemWidth, node.DesiredSize.X);
            measuredCount++;
        }

        _extentWidth = totalWidth;
        if (measuredCount == 0)
            _itemWidth = 0f;

        return new Vector2(
            float.IsFinite(availableSize.X) ? Math.Min(totalWidth, availableSize.X) : totalWidth,
            maximumHeight);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        var itemCount = Children.Count;
        base.ArrangeOverride(arrangeRect);

        var x = arrangeRect.X - ScrollOffset;
        for (var index = 0; index < itemCount; index++)
        {
            if (Children[index] is not LayoutNode node)
                continue;

            var width = node.DesiredSize.X;
            node.Arrange(new Rect(x, arrangeRect.Y, width, arrangeRect.Height));
            x += width;
        }

        HorizontalSnapPointsChanged?.Invoke(this, null);
        VerticalSnapPointsChanged?.Invoke(this, null);
    }
}
