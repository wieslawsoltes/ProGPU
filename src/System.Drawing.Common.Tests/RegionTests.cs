using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class RegionTests
{
    [Fact]
    public void RectangleDifferenceProducesExactDeterministicScans()
    {
        using var region = new Region(new RectangleF(0, 0, 10, 10));
        region.Exclude(new RectangleF(2, 2, 6, 6));

        RectangleF[] scans = region.GetRegionScans(new Matrix());

        Assert.Equal(
            new[]
            {
                new RectangleF(0, 0, 10, 2),
                new RectangleF(0, 2, 2, 6),
                new RectangleF(8, 2, 2, 6),
                new RectangleF(0, 8, 10, 2),
            },
            scans);
        Assert.True(region.IsVisible(1f, 1f));
        Assert.False(region.IsVisible(5f, 5f));
        Assert.False(region.IsVisible(new RectangleF(3, 3, 1, 1)));
        Assert.True(region.IsVisible(new RectangleF(1, 3, 2, 1)));
    }

    [Fact]
    public void InfiniteComplementStaysSymbolicAndHitTestsExactly()
    {
        using var region = new Region();
        region.Exclude(new RectangleF(10, 20, 30, 40));

        Assert.False(region.IsVisible(20f, 30f));
        Assert.True(region.IsVisible(-100f, -100f));
        Assert.False(region.GetRegionScans(new Matrix()).AsSpan().IsEmpty);
    }

    [Fact]
    public void RegionDataRoundTripsCurvesAndBooleanOperations()
    {
        using var path = new GraphicsPath(FillMode.Winding);
        path.AddEllipse(0, 0, 20, 10);
        using var original = new Region(path);
        original.Xor(new RectangleF(5, 2, 3, 4));

        RegionData data = original.GetRegionData();
        using var restored = new Region(data);

        Assert.Equal(original.IsVisible(1f, 5f), restored.IsVisible(1f, 5f));
        Assert.Equal(original.IsVisible(6f, 4f), restored.IsVisible(6f, 4f));
        Assert.Equal(original.IsVisible(30f, 30f), restored.IsVisible(30f, 30f));
    }

    [Fact]
    public void RegionSnapshotsInputPathAndCloneSharesValueSemantics()
    {
        using var path = new GraphicsPath();
        path.AddRectangle(new RectangleF(0, 0, 10, 10));
        using var original = new Region(path);
        using Region clone = original.Clone();

        path.Reset();
        original.Translate(100f, 0f);

        Assert.True(clone.IsVisible(5f, 5f));
        Assert.False(original.IsVisible(5f, 5f));
        Assert.True(original.IsVisible(105f, 5f));
    }

    [Fact]
    public void CurvedScanExtractionDoesNotClaimFalsePrecision()
    {
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, 20, 10);
        using var region = new Region(path);

        Assert.Throws<NotSupportedException>(() => region.GetRegionScans(new Matrix()));
    }

    [Fact]
    public void InvalidPortableRegionDataIsRejected()
    {
        using var source = new Region(new Rectangle(0, 0, 1, 1));
        RegionData data = source.GetRegionData();
        byte[] bytes = data.Data;
        bytes[0] ^= 0xff;
        data.Data = bytes;

        Assert.Throws<ArgumentException>(() => new Region(data));
    }
}
