using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public class PortablePopupMonitorSelectionTests
{
    [Fact]
    public void GreatestOverlapWinsAndPreservesNegativeDesktopOrigin()
    {
        var left = new PortableRect(-1920, 0, 1920, 1080);
        var work = new PortableRect(-1920, 24, 1920, 1056);
        var right = new PortableRect(0, 0, 1920, 1080);
        var selection = new PortablePopupMonitorSelection(new(-100, 20, 120, 80));
        selection.Consider(right, right, true);
        selection.Consider(left, work, false);
        Assert.True(selection.TryGetBounds(out var bounds));
        Assert.Equal(PortablePopupPlacementBoundsKind.NativeScreen, bounds.Kind);
        Assert.Equal(left, bounds.Screen);
        Assert.Equal(work, bounds.WorkArea);
    }

    [Fact]
    public void OffscreenTargetUsesNearestRectangleNotPrimaryMonitor()
    {
        var first = new PortableRect(0, 0, 100, 100);
        var second = new PortableRect(200, 0, 100, 100);
        var selection = new PortablePopupMonitorSelection(new(310, 120, 10, 10));
        selection.Consider(first, first, true);
        selection.Consider(second, second, false);
        Assert.True(selection.TryGetBounds(out var bounds));
        Assert.Equal(second, bounds.Screen);
    }

    [Fact]
    public void ZeroSizedBoundaryTargetTiesPreferPrimaryThenInventoryOrder()
    {
        var first = new PortableRect(-100, 0, 100, 100);
        var second = new PortableRect(0, 0, 100, 100);
        var selection = new PortablePopupMonitorSelection(new(0, 50, 0, 0));
        selection.Consider(first, first, false);
        selection.Consider(second, second, true);
        selection.Consider(first, first, true);
        Assert.True(selection.TryGetBounds(out var bounds));
        Assert.Equal(second, bounds.Screen);
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(double.PositiveInfinity, 10)]
    [InlineData(0, -1)]
    public void InvalidTargetFailsClosed(double x, double width)
    {
        var selection = new PortablePopupMonitorSelection(new(x, 0, width, 10));
        selection.Consider(new(0, 0, 100, 100), new(0, 0, 100, 100), true);
        Assert.False(selection.TryGetBounds(out _));
    }

    [Fact]
    public void InvalidInventoriesFailClosedWithoutManufacturingOwnerBounds()
    {
        var selection = new PortablePopupMonitorSelection(new(0, 0, 10, 10));
        Assert.False(selection.TryGetBounds(out _));
        selection.Consider(PortableRect.Empty, PortableRect.Empty, true);
        selection.Consider(new(0, 0, 100, 100), new(-1, 0, 100, 100), true);
        selection.Consider(new(double.MaxValue, 0, double.MaxValue, 100), new(0, 0, 1, 1), true);
        Assert.False(selection.TryGetBounds(out _));
    }
}
