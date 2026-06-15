using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class PrimitivePathGeometryTests
{
    [Fact]
    public void CreateEllipseUsesNativeArcSegments()
    {
        var path = PrimitivePathGeometry.CreateEllipse(new Vector2(5f, 4f), 5f, 3f);

        var figure = Assert.Single(path.Figures);
        Assert.True(figure.IsClosed);
        Assert.Equal(new Vector2(10f, 4f), figure.StartPoint);
        Assert.Equal(4, figure.Segments.Count);
        Assert.All(figure.Segments, segment =>
        {
            var arc = Assert.IsType<ArcSegment>(segment);
            Assert.Equal(new Vector2(5f, 3f), arc.Size);
            Assert.False(arc.IsLargeArc);
            Assert.Equal(SweepDirection.Clockwise, arc.SweepDirection);
        });
    }

    [Fact]
    public void CreateRoundedRectangleUsesNativeCornerArcSegments()
    {
        var path = PrimitivePathGeometry.CreateRoundedRectangle(0f, 0f, 10f, 6f, 2f, 2f);

        var figure = Assert.Single(path.Figures);
        Assert.True(figure.IsClosed);
        Assert.Equal(new Vector2(2f, 0f), figure.StartPoint);
        Assert.Equal(8, figure.Segments.Count);
        Assert.Equal(4, figure.Segments.OfType<LineSegment>().Count());
        Assert.Equal(4, figure.Segments.OfType<ArcSegment>().Count());
        Assert.All(figure.Segments.OfType<ArcSegment>(), arc =>
        {
            Assert.Equal(new Vector2(2f, 2f), arc.Size);
            Assert.False(arc.IsLargeArc);
            Assert.Equal(SweepDirection.Clockwise, arc.SweepDirection);
        });
    }
}
