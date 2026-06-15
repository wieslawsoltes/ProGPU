using System;
using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public class ArcPathCompilerTests
{
    [Fact]
    public void ArcSegmentBoundsIncludeExactExtrema()
    {
        var path = CreatePartialCircleArcPath();
        var figure = path.Figures[0];
        var arc = Assert.IsType<ArcSegment>(figure.Segments[0]);

        Assert.True(ArcSegmentGeometry.TryGetArcBounds(figure.StartPoint, arc, out var min, out var max));

        AssertClose(-3.09017f, min.X);
        AssertClose(0f, min.Y);
        AssertClose(10f, max.X);
        AssertClose(10f, max.Y);

        float sampledMaxY = figure.StartPoint.Y;
        for (int step = 1; step < 8; step++)
        {
            float theta = 0.6f * MathF.PI * step / 8f;
            sampledMaxY = MathF.Max(sampledMaxY, 10f * MathF.Sin(theta));
        }

        Assert.True(sampledMaxY < 9.999f);
    }

    [Fact]
    public void SubArcSegmentPreservesOriginalEllipseAndSweep()
    {
        var start = new Vector2(10f, 0f);
        var arc = new ArcSegment(
            new Vector2(-10f, 0f),
            new Vector2(10f, 10f),
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise);

        Assert.True(ArcSegmentGeometry.TryCreateSubArcSegment(
            start,
            arc,
            0.5f,
            1.0f,
            out var subStart,
            out var subArc));

        AssertClose(0f, subStart.X);
        AssertClose(10f, subStart.Y);
        AssertClose(-10f, subArc.Point.X);
        AssertClose(0f, subArc.Point.Y);
        AssertClose(10f, subArc.Size.X);
        AssertClose(10f, subArc.Size.Y);
        Assert.False(subArc.IsLargeArc);
        Assert.Equal(SweepDirection.Clockwise, subArc.SweepDirection);
    }

    [Fact]
    public void SubArcSegmentSetsLargeArcForLongSpans()
    {
        var start = new Vector2(10f, 0f);
        var arc = new ArcSegment(
            new Vector2(0f, 10f),
            new Vector2(10f, 10f),
            rotationAngle: 0f,
            isLargeArc: true,
            SweepDirection.Clockwise);

        Assert.True(ArcSegmentGeometry.TryCreateSubArcSegment(
            start,
            arc,
            0.0f,
            0.75f,
            out var subStart,
            out var subArc));

        AssertClose(start, subStart);
        Assert.True(subArc.IsLargeArc);
        Assert.Equal(SweepDirection.Clockwise, subArc.SweepDirection);
    }

    [Fact]
    public void DashedArcSegmentsPreserveNativeArcSpansAndAdvanceDashState()
    {
        var start = new Vector2(10f, 0f);
        var arc = new ArcSegment(
            new Vector2(-10f, 0f),
            new Vector2(10f, 10f),
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise);
        var pattern = new[] { 10f, 10f };

        Assert.True(ArcSegmentGeometry.TryCreateDashedArcSegments(
            start,
            arc,
            pattern,
            patternIndex: 0,
            distanceInPattern: 0f,
            out var segments,
            out var finalPatternIndex,
            out var finalDistanceInPattern));

        Assert.Equal(2, segments.Length);
        AssertClose(start, segments[0].Start);
        foreach (var segment in segments)
        {
            AssertClose(10f, segment.Arc.Size.X);
            AssertClose(10f, segment.Arc.Size.Y);
            AssertClose(0f, segment.Arc.RotationAngle);
            Assert.False(segment.Arc.IsLargeArc);
            Assert.Equal(SweepDirection.Clockwise, segment.Arc.SweepDirection);
        }

        Assert.Equal(1, finalPatternIndex);
        Assert.InRange(finalDistanceInPattern, 1.3f, 1.5f);
    }

    [Fact]
    public void DashedArcSegmentsRejectDegenerateArcsWithoutAdvancingDashState()
    {
        var pattern = new[] { 5f, 5f };

        Assert.False(ArcSegmentGeometry.TryCreateDashedArcSegments(
            new Vector2(10f, 0f),
            new ArcSegment(
                new Vector2(20f, 0f),
                Vector2.Zero,
                rotationAngle: 0f,
                isLargeArc: false,
                SweepDirection.Clockwise),
            pattern,
            patternIndex: 1,
            distanceInPattern: 2f,
            out var segments,
            out var finalPatternIndex,
            out var finalDistanceInPattern));

        Assert.Empty(segments);
        Assert.Equal(1, finalPatternIndex);
        Assert.Equal(2f, finalDistanceInPattern);
    }

    [Fact]
    public void ShaderParametersExposeTransformedEllipseAxesForNativeStroke()
    {
        var start = new Vector2(10f, 0f);
        var arc = new ArcSegment(
            new Vector2(-10f, 0f),
            new Vector2(10f, 5f),
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise);
        var transform = new Matrix4x4(
            1.2f, 0.2f, 0f, 0f,
            0.35f, 1.4f, 0f, 0f,
            0f, 0f, 1f, 0f,
            3f, -2f, 0f, 1f);

        Assert.True(ArcSegmentGeometry.TryCreateShaderParameters(
            start,
            arc,
            transform,
            out var parameters));

        AssertClose(new Vector2(3f, -2f), parameters.Center);
        AssertClose(new Vector2(12f, 2f), parameters.AxisX);
        AssertClose(new Vector2(1.75f, 7f), parameters.AxisY);
        AssertClose(0f, parameters.Theta1);
        AssertClose(MathF.PI, parameters.DeltaTheta);
    }

    [Fact]
    public void ShaderParametersRejectDegenerateArcs()
    {
        Assert.False(ArcSegmentGeometry.TryCreateShaderParameters(
            Vector2.Zero,
            new ArcSegment(
                new Vector2(10f, 0f),
                Vector2.Zero,
                rotationAngle: 0f,
                isLargeArc: false,
                SweepDirection.Clockwise),
            Matrix4x4.Identity,
            out var parameters));

        Assert.Equal(default, parameters);
    }

    [Fact]
    public void PathAtlasCompilerPreservesNativeArcSegmentAndExactBounds()
    {
        var (records, segments) = PathAtlas.CompilePath(
            CreatePartialCircleArcPath(),
            out float minX,
            out float minY,
            out float maxX,
            out float maxY);

        var record = Assert.Single(records);
        var segment = Assert.Single(segments);

        Assert.Equal(0u, record.StartSegment);
        Assert.Equal(1u, record.SegmentCount);
        Assert.Equal(3u, segment.SegmentType);
        AssertClose(0f, segment.P2.X);
        AssertClose(0f, segment.P2.Y);
        AssertClose(10f, segment.P3.X);
        AssertClose(10f, segment.P3.Y);
        AssertClose(-3.09017f, minX);
        AssertClose(0f, minY);
        AssertClose(10f, maxX);
        AssertClose(10f, maxY);
        AssertClose(minX, record.MinX);
        AssertClose(minY, record.MinY);
        AssertClose(maxX, record.MaxX);
        AssertClose(maxY, record.MaxY);
    }

    [Fact]
    public void PathAtlasCompilerCarriesFillRuleIntoGpuRecord()
    {
        var (records, _) = PathAtlas.CompilePath(
            CreateFillRulePath(FillRule.EvenOdd),
            out _,
            out _,
            out _,
            out _);

        var record = Assert.Single(records);
        Assert.Equal((uint)FillRule.EvenOdd, record.FillRule);
    }

    [Fact]
    public void PathOperationCompilerPreservesNativeArcSegmentAndExactBounds()
    {
        var (records, segments) = PathOpGeometrySolver.CompilePath(
            CreatePartialCircleArcPath(),
            out float minX,
            out float minY,
            out float maxX,
            out float maxY);

        var record = Assert.Single(records);
        var segment = Assert.Single(segments);

        Assert.Equal(0u, record.StartSegment);
        Assert.Equal(1u, record.SegmentCount);
        Assert.Equal(3u, segment.SegmentType);
        AssertClose(0f, segment.P2.X);
        AssertClose(0f, segment.P2.Y);
        AssertClose(10f, segment.P3.X);
        AssertClose(10f, segment.P3.Y);
        AssertClose(-3.09017f, minX);
        AssertClose(0f, minY);
        AssertClose(10f, maxX);
        AssertClose(10f, maxY);
        AssertClose(minX, record.MinX);
        AssertClose(minY, record.MinY);
        AssertClose(maxX, record.MaxX);
        AssertClose(maxY, record.MaxY);
    }

    [Fact]
    public void PathOperationCompilerCarriesFillRuleIntoGpuRecord()
    {
        var (records, _) = PathOpGeometrySolver.CompilePath(
            CreateFillRulePath(FillRule.Nonzero),
            out _,
            out _,
            out _,
            out _);

        var record = Assert.Single(records);
        Assert.Equal((uint)FillRule.Nonzero, record.FillRule);
    }

    private static PathGeometry CreatePartialCircleArcPath()
    {
        const float radius = 10f;
        const float endTheta = 0.6f * MathF.PI;

        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(radius, 0f));
        figure.Segments.Add(new ArcSegment(
            new Vector2(radius * MathF.Cos(endTheta), radius * MathF.Sin(endTheta)),
            new Vector2(radius, radius),
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        path.Figures.Add(figure);
        return path;
    }

    private static PathGeometry CreateFillRulePath(FillRule fillRule)
    {
        var path = new PathGeometry { FillRule = fillRule };
        var figure = new PathFigure(new Vector2(0f, 0f), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(10f, 0f)));
        figure.Segments.Add(new LineSegment(new Vector2(0f, 10f)));
        path.Figures.Add(figure);
        return path;
    }

    private static void AssertClose(float expected, float actual)
    {
        Assert.InRange(actual, expected - 0.001f, expected + 0.001f);
    }

    private static void AssertClose(Vector2 expected, Vector2 actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
    }
}
