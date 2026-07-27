using System;
using Avalonia;
using Avalonia.Platform;
using Avalonia.ProGpu;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

public sealed class AvaloniaDirtyRegionContractTests
{
    [Fact]
    public void DegenerateRectanglesDoNotChangeTheRegion()
    {
        using var region = new AvaloniaDirtyRegion();

        region.AddRect(Rectangle(4, 4, 4, 9));
        region.AddRect(Rectangle(8, 8, 3, 12));

        Assert.True(region.IsEmpty);
        Assert.Empty(region.Rects);
        Assert.Equal(default, region.Bounds);
    }

    [Fact]
    public void QueriesUseTheOriginalIndependentRectangles()
    {
        using var region = new AvaloniaDirtyRegion();
        region.AddRect(Rectangle(0, 0, 10, 10));
        region.AddRect(Rectangle(20, 20, 30, 30));

        Assert.Equal(Rectangle(0, 0, 30, 30), region.Bounds);
        Assert.True(region.Contains(new Point(10, 10)));
        Assert.False(region.Contains(new Point(15, 15)));
        Assert.True(region.Intersects(RectangleD(9, 9, 12, 12)));
        Assert.False(region.Intersects(RectangleD(10, 10, 20, 20)));
    }

    [Fact]
    public void RectangleInventoryIsReadOnlyToCallers()
    {
        using var region = new AvaloniaDirtyRegion();
        region.AddRect(Rectangle(0, 0, 10, 10));

        Assert.Throws<NotSupportedException>(
            () => region.Rects.Add(Rectangle(20, 20, 30, 30)));
        Assert.Single(region.Rects);
    }

    [Fact]
    public void ExcessiveDirtyRectanglesCollapseToTheirConservativeUnion()
    {
        using var region = new AvaloniaDirtyRegion();

        for (var index = 0; index < 65; index++)
        {
            region.AddRect(Rectangle(index * 2, 0, index * 2 + 1, 1));
        }

        Assert.Single(region.Rects);
        Assert.Equal(Rectangle(0, 0, 129, 1), region.Bounds);
        Assert.Equal(region.Bounds, region.Rects[0]);
    }

    [Fact]
    public void ResetAndDisposeReleaseRetainedRectangles()
    {
        var region = new AvaloniaDirtyRegion();
        region.AddRect(Rectangle(0, 0, 10, 10));

        region.Reset();

        Assert.True(region.IsEmpty);
        Assert.Empty(region.Rects);
        region.AddRect(Rectangle(1, 2, 3, 4));
        region.Dispose();
        Assert.True(region.IsEmpty);
        Assert.Empty(region.Rects);
    }

    private static LtrbPixelRect Rectangle(int left, int top, int right, int bottom) =>
        new()
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };

    private static LtrbRect RectangleD(double left, double top, double right, double bottom) =>
        new()
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };
}
