using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class PathOpReconstructionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpposingFilledLobesSurviveZeroSignedArea(bool reverse)
    {
        // The two filled triangles cancel in the signed-area sum. Their
        // nonzero/even-odd fill is not empty.
        AssertContourRetained(
            [new(0, 0), new(4, 4), new(0, 4), new(4, 0)], reverse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallPositiveAreaSurvivesReconstruction(bool reverse)
    {
        // Exact dyadic coordinates enclose 2^-15 square units. A later target
        // transform can make this visible; reconstruction must retain it.
        AssertContourRetained(
            [new(-1024, 0), new(1024, 0), new(0, 1f / 33554432f)], reverse);
    }

    private static void AssertContourRetained(Vector2[] points, bool reverse)
    {
        if (reverse) Array.Reverse(points);
        var segments = new GpuPathSegment[points.Length];
        for (int index = 0; index < points.Length; index++)
        {
            segments[index] = new GpuPathSegment
            {
                P0 = points[index],
                P1 = points[(index + 1) % points.Length],
                SegmentType = 0
            };
        }

        PathFigure figure = Assert.Single(PathOpGeometrySolver.ReconstructFigures(segments));
        Assert.True(figure.IsClosed);
        Assert.Equal(points[0], figure.StartPoint);
        Assert.Equal(points.Length, figure.Segments.Count);
        for (int index = 0; index < points.Length; index++)
        {
            Assert.Equal(points[(index + 1) % points.Length],
                Assert.IsType<LineSegment>(figure.Segments[index]).Point);
        }
    }
}
