using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class PathGeometryHitTestingTests
{
    [Fact]
    public void TryContainsFillIncludesPointInsideLineFigure()
    {
        var geometry = CreateRectangle(FillRule.Nonzero, reverseInner: false, includeInner: false);

        Assert.True(PathGeometryHitTesting.TryContainsFill(
            geometry,
            new Vector2(5f, 5f),
            tolerance: 0f,
            relativeTolerance: false,
            out bool contains));
        Assert.True(contains);
    }

    [Fact]
    public void TryContainsFillExcludesPointOutsideLineFigure()
    {
        var geometry = CreateRectangle(FillRule.Nonzero, reverseInner: false, includeInner: false);

        Assert.True(PathGeometryHitTesting.TryContainsFill(
            geometry,
            new Vector2(12f, 5f),
            tolerance: 0f,
            relativeTolerance: false,
            out bool contains));
        Assert.False(contains);
    }

    [Fact]
    public void TryContainsFillHonorsEvenOddHole()
    {
        var geometry = CreateRectangle(FillRule.EvenOdd, reverseInner: false, includeInner: true);

        Assert.True(PathGeometryHitTesting.TryContainsFill(
            geometry,
            new Vector2(5f, 5f),
            tolerance: 0f,
            relativeTolerance: false,
            out bool contains));
        Assert.False(contains);
    }

    [Fact]
    public void TryContainsFillHonorsNonzeroWindingHole()
    {
        var geometry = CreateRectangle(FillRule.Nonzero, reverseInner: true, includeInner: true);

        Assert.True(PathGeometryHitTesting.TryContainsFill(
            geometry,
            new Vector2(5f, 5f),
            tolerance: 0f,
            relativeTolerance: false,
            out bool contains));
        Assert.False(contains);
    }

    [Fact]
    public void TryContainsFillUsesToleranceNearBoundary()
    {
        var geometry = CreateRectangle(FillRule.Nonzero, reverseInner: false, includeInner: false);

        Assert.True(PathGeometryHitTesting.TryContainsFill(
            geometry,
            new Vector2(10.2f, 5f),
            tolerance: 0.25f,
            relativeTolerance: false,
            out bool contains));
        Assert.True(contains);
    }

    [Fact]
    public void TryContainsFillFlattensCurvedSegments()
    {
        var geometry = new PathGeometry
        {
            FillRule = FillRule.Nonzero
        };
        var figure = new PathFigure(new Vector2(0f, 0f), isClosed: true);
        figure.Segments.Add(new QuadraticBezierSegment(new Vector2(10f, 14f), new Vector2(20f, 0f)));
        figure.Segments.Add(new LineSegment(new Vector2(0f, 0f)));
        geometry.Figures.Add(figure);

        Assert.True(PathGeometryHitTesting.TryContainsFill(
            geometry,
            new Vector2(10f, 4f),
            tolerance: 0f,
            relativeTolerance: false,
            out bool contains));
        Assert.True(contains);
    }

    [Fact]
    public void TryContainsFillEvaluatesPositiveWeightRationalQuadratic()
    {
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        var figure = new PathFigure(Vector2.Zero, isClosed: true);
        figure.Segments.Add(new RationalQuadraticBezierSegment(
            new Vector2(10f, 14f),
            new Vector2(20f, 0f),
            0.5f));
        figure.Segments.Add(new LineSegment(Vector2.Zero));
        geometry.Figures.Add(figure);

        Assert.True(PathGeometryHitTesting.TryContainsFill(
            geometry,
            new Vector2(10f, 3f),
            tolerance: 0f,
            relativeTolerance: false,
            out bool inside));
        Assert.True(inside);
        Assert.True(PathGeometryHitTesting.TryContainsFill(
            geometry,
            new Vector2(10f, 5f),
            tolerance: 0f,
            relativeTolerance: false,
            out bool outside));
        Assert.False(outside);
    }

    private static PathGeometry CreateRectangle(FillRule fillRule, bool reverseInner, bool includeInner)
    {
        var geometry = new PathGeometry
        {
            FillRule = fillRule
        };

        geometry.Figures.Add(CreateRectangleFigure(
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            new Vector2(10f, 10f),
            new Vector2(0f, 10f)));

        if (includeInner)
        {
            geometry.Figures.Add(reverseInner
                ? CreateRectangleFigure(
                    new Vector2(3f, 3f),
                    new Vector2(3f, 7f),
                    new Vector2(7f, 7f),
                    new Vector2(7f, 3f))
                : CreateRectangleFigure(
                    new Vector2(3f, 3f),
                    new Vector2(7f, 3f),
                    new Vector2(7f, 7f),
                    new Vector2(3f, 7f)));
        }

        return geometry;
    }

    private static PathFigure CreateRectangleFigure(Vector2 start, Vector2 second, Vector2 third, Vector2 fourth)
    {
        var figure = new PathFigure(start, isClosed: true);
        figure.Segments.Add(new LineSegment(second));
        figure.Segments.Add(new LineSegment(third));
        figure.Segments.Add(new LineSegment(fourth));
        return figure;
    }
}
