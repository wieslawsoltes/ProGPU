using Avalonia;
using Avalonia.SilkNet;
using Xunit;

namespace ProGPU.Avalonia.SilkNet.ContractTests;

public sealed class ScreenInventoryContractTests
{
    [Fact]
    public void ScreenBoundsUseExclusiveRightAndBottomEdges()
    {
        var bounds = new PixelRect(10, 20, 100, 50);

        Assert.True(
            SilkNetScreenGeometry.Contains(
                bounds,
                new PixelPoint(10, 20)));
        Assert.True(
            SilkNetScreenGeometry.Contains(
                bounds,
                new PixelPoint(109, 69)));
        Assert.False(
            SilkNetScreenGeometry.Contains(
                bounds,
                new PixelPoint(110, 69)));
        Assert.False(
            SilkNetScreenGeometry.Contains(
                bounds,
                new PixelPoint(109, 70)));
    }

    [Fact]
    public void DisjointScreenAndWindowHaveNoIntersection()
    {
        Assert.Equal(
            0,
            SilkNetScreenGeometry.IntersectionArea(
                new PixelRect(0, 0, 100, 100),
                new PixelRect(120, 20, 30, 30)));
    }

    [Fact]
    public void IntersectionUsesOnlyTheVisibleWindowArea()
    {
        Assert.Equal(
            500,
            SilkNetScreenGeometry.IntersectionArea(
                new PixelRect(0, 0, 100, 100),
                new PixelRect(90, 25, 30, 50)));
    }
}
