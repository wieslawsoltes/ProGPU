using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Layout;
using ProGPU.Scene;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Controls.Primitives;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public interface IScrollSnapPointsInfo
{
    bool AreHorizontalSnapPointsRegular { get; }

    bool AreVerticalSnapPointsRegular { get; }

    event EventHandler<object?>? HorizontalSnapPointsChanged;

    event EventHandler<object?>? VerticalSnapPointsChanged;

    IReadOnlyList<float> GetIrregularSnapPoints(
        Orientation orientation,
        SnapPointsAlignment alignment);

    float GetRegularSnapPoints(
        Orientation orientation,
        SnapPointsAlignment alignment,
        out float offset);
}

/// <summary>
/// Lays out Pivot content pages horizontally and publishes regular snap points.
/// </summary>
public sealed class PivotPanel : Panel, IScrollSnapPointsInfo
{
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
            SnapPointsAlignment.Center => Size.X * 0.5f,
            SnapPointsAlignment.Far => Size.X,
            _ => 0f
        };
        return orientation == Orientation.Horizontal ? Size.X : 0f;
    }

    public IReadOnlyList<float> GetIrregularSnapPoints(
        Orientation orientation,
        SnapPointsAlignment alignment) =>
        Array.Empty<float>();

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var desired = Vector2.Zero;
        foreach (var child in Children)
        {
            if (child is not LayoutNode node)
                continue;

            node.Measure(availableSize);
            desired.X = Math.Max(desired.X, node.DesiredSize.X);
            desired.Y = Math.Max(desired.Y, node.DesiredSize.Y);
        }

        return desired;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        var index = 0;
        foreach (var child in Children)
        {
            if (child is LayoutNode node)
            {
                node.Arrange(new Rect(
                    arrangeRect.X + index * arrangeRect.Width,
                    arrangeRect.Y,
                    arrangeRect.Width,
                    arrangeRect.Height));
                index++;
            }
        }

        HorizontalSnapPointsChanged?.Invoke(this, null);
        VerticalSnapPointsChanged?.Invoke(this, null);
    }
}

/// <summary>
/// Canvas used by the Pivot template to position header items.
/// </summary>
public sealed class PivotHeaderPanel : Canvas
{
}
