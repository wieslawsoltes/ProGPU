using ProGPU.Compute;
using ProGPU.Vector;
using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace ProGPU.Tests;

public class WavefrontBinningTests
{
    [Fact]
    public void StableCellBinsPreservePainterOrderWithoutPerCellCap()
    {
        const int instanceCount = 257;
        var ranges = new (uint MinX, uint MinY, uint MaxX, uint MaxY)[instanceCount];
        Array.Fill(ranges, (0u, 0u, 0u, 0u));
        var cells = new GpuGridCell[1];
        var indices = new uint[instanceCount];

        WavefrontVectorEngine.BuildStableCellBinsCpu(ranges, 1, 1, cells, indices);

        Assert.Equal(0u, cells[0].ShapeStartOffset);
        Assert.Equal((uint)instanceCount, cells[0].ShapeCount);
        for (uint index = 0; index < instanceCount; index++)
        {
            Assert.Equal(index, indices[(int)index]);
        }
    }

    [Fact]
    public void StableCellBinsCreateExactOffsetsForSparseOverlaps()
    {
        var ranges = new (uint MinX, uint MinY, uint MaxX, uint MaxY)[]
        {
            (0, 0, 1, 0),
            (1, 0, 1, 1),
            (0, 1, 1, 1)
        };
        var cells = new GpuGridCell[4];
        var indices = new uint[6];

        WavefrontVectorEngine.BuildStableCellBinsCpu(ranges, 2, 2, cells, indices);

        Assert.Equal(new uint[] { 0 }, Slice(cells[0], indices));
        Assert.Equal(new uint[] { 0, 1 }, Slice(cells[1], indices));
        Assert.Equal(new uint[] { 2 }, Slice(cells[2], indices));
        Assert.Equal(new uint[] { 1, 2 }, Slice(cells[3], indices));
    }

    [Fact]
    public void ActiveCellCompactionPreservesRowMajorOrderAndBuildsIndirectDispatch()
    {
        var cells = new[]
        {
            new GpuGridCell(),
            new GpuGridCell { ShapeStartOffset = 0, ShapeCount = 3 },
            new GpuGridCell(),
            new GpuGridCell { ShapeStartOffset = 3, ShapeCount = 1 },
            new GpuGridCell(),
            new GpuGridCell { ShapeStartOffset = 4, ShapeCount = 2 }
        };
        var activeCells = new uint[3];

        var dispatch = WavefrontVectorEngine.BuildActiveCellListCpu(cells, activeCells);

        Assert.Equal(new uint[] { 1, 3, 5 }, activeCells);
        Assert.Equal(3u, dispatch.X);
        Assert.Equal(1u, dispatch.Y);
        Assert.Equal(1u, dispatch.Z);
    }

    [Fact]
    public void EmptyCellGridBuildsZeroWorkIndirectDispatch()
    {
        var dispatch = WavefrontVectorEngine.BuildActiveCellListCpu(
            new GpuGridCell[4],
            Span<uint>.Empty);

        Assert.Equal(0u, dispatch.X);
        Assert.Equal(1u, dispatch.Y);
        Assert.Equal(1u, dispatch.Z);
    }

    [Fact]
    public void SparseDispatchSplitsWorkAbovePortableDimensionLimit()
    {
        var dispatch = WavefrontVectorEngine.CreateSparseDispatchArgs(
            WavefrontVectorEngine.MaximumPortableDispatchDimension + 1u);

        Assert.Equal(WavefrontVectorEngine.MaximumPortableDispatchDimension, dispatch.X);
        Assert.Equal(2u, dispatch.Y);
        Assert.Equal(1u, dispatch.Z);
    }

    [Fact]
    public void CoarseCellClassificationKeepsUncertainCoverageOnEdgePath()
    {
        Assert.Equal(
            GpuCellShapeClass.Edge,
            WavefrontVectorEngine.ClassifyCellByCenterDistance(
                11.8f, 1f, 1f, 16f, 16f, centerIsInside: true));
        Assert.Equal(
            GpuCellShapeClass.Solid,
            WavefrontVectorEngine.ClassifyCellByCenterDistance(
                12f, 1f, 1f, 16f, 16f, centerIsInside: true));
        Assert.Equal(
            GpuCellShapeClass.Outside,
            WavefrontVectorEngine.ClassifyCellByCenterDistance(
                24f, 0.5f, 1f, 16f, 16f, centerIsInside: false));
        Assert.Equal(
            GpuCellShapeClass.Edge,
            WavefrontVectorEngine.ClassifyCellByCenterDistance(
                float.NaN, 1f, 1f, 16f, 16f, centerIsInside: true));
    }

    [Theory]
    [InlineData(0.99f)]
    [InlineData(1f)]
    [InlineData(1.01f)]
    [InlineData(2f)]
    [InlineData(7.5f)]
    public void FlatteningScaleBucketsRoundUpConservatively(float scale)
    {
        Assert.True(WavefrontVectorEngine.TryGetScaleBucket(scale, out _, out float upperScale));
        Assert.True(upperScale >= scale);
        Assert.True(upperScale / scale <= MathF.Pow(2f, 0.25f) + 0.0001f);
    }

    [Fact]
    public void AdaptiveQuadraticAndCubicFlatteningMeetChordErrorBound()
    {
        const float tolerance = 0.125f;
        GpuBezierCurve[] curves =
        [
            new(new Vector2(0f, 0f), new Vector2(40f, 100f), new Vector2(100f, 0f), Vector2.Zero, 1),
            new(new Vector2(0f, 0f), new Vector2(20f, 140f), new Vector2(80f, -120f), new Vector2(120f, 10f), 2)
        ];

        foreach (var curve in curves)
        {
            Assert.True(BvhBuilder.TryGetAdaptiveSubdivisionCount(curve, tolerance, out uint subdivisions));
            Assert.InRange(subdivisions, 2u, BvhBuilder.MaximumAdaptiveSubdivisions);
            Assert.True(MaximumMatchedParameterChordError(curve, subdivisions) <= tolerance + 0.0001f);
        }
    }

    [Fact]
    public void AdaptiveFlatteningUsesOneLineForLinearCurvesAndRejectsUnboundedWork()
    {
        var line = new GpuBezierCurve(
            new Vector2(0f, 0f),
            new Vector2(10f, 10f),
            Vector2.Zero,
            Vector2.Zero,
            0);
        Assert.True(BvhBuilder.TryGetAdaptiveSubdivisionCount(line, 0.001f, out uint lineCount));
        Assert.Equal(1u, lineCount);

        var extreme = new GpuBezierCurve(
            new Vector2(0f, 0f),
            new Vector2(0f, 1_000_000f),
            new Vector2(1_000_000f, -1_000_000f),
            new Vector2(1_000_000f, 0f),
            2);
        Assert.False(BvhBuilder.TryGetAdaptiveSubdivisionCount(extreme, 0.000001f, out _));
    }

    [Fact]
    public void AdaptiveBvhStoresExactLineOffsetsAndCounts()
    {
        var curves = new List<GpuBezierCurve>
        {
            new(new Vector2(0f, 0f), new Vector2(20f, 30f), new Vector2(40f, 0f), Vector2.Zero, 1),
            new(new Vector2(40f, 0f), new Vector2(60f, 0f), Vector2.Zero, Vector2.Zero, 0)
        };

        Assert.True(BvhBuilder.TryBuildBvh(
            curves,
            0.25f,
            out var nodes,
            out var ordered,
            out uint totalLines));

        Assert.Single(nodes);
        Assert.Equal(2, ordered.Count);
        Assert.Equal(0u, ordered[0].LineOffset);
        Assert.Equal(ordered[0].Subdivisions, ordered[1].LineOffset);
        uint expectedLines = 0;
        foreach (var curve in ordered)
        {
            expectedLines = checked(expectedLines + curve.Subdivisions);
        }
        Assert.Equal(expectedLines, totalLines);
        Assert.Equal(totalLines, nodes[0].PrimitiveCount);
    }

    [Fact]
    public void WavefrontPathExtractionFailsClosedForUnsupportedSegments()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero);
        figure.Segments.Add(new LineSegment(new Vector2(10f, 0f)));
        figure.Segments.Add(new ArcSegment(
            new Vector2(20f, 10f),
            new Vector2(10f, 10f),
            0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        path.Figures.Add(figure);

        Assert.False(BvhBuilder.TryGetPathCurves(path, out var curves));
        Assert.Empty(curves);
    }

    [Fact]
    public void WavefrontInstanceLayoutCarriesTransformIndexWithoutGrowingGpuRecord()
    {
        Assert.Equal(192, Marshal.SizeOf<GpuShapeInstance>());
        Assert.Equal(152, Marshal.OffsetOf<GpuShapeInstance>(nameof(GpuShapeInstance.TransformIndex)).ToInt32());
        Assert.Equal(160, Marshal.OffsetOf<GpuShapeInstance>(nameof(GpuShapeInstance.Color)).ToInt32());
        Assert.Equal(128, Marshal.SizeOf<GpuShapeTransform>());
    }

    [Fact]
    public void RetainedTransformMovesCpuCoverageRangeWithoutRewritingInstance()
    {
        var instance = new GpuShapeInstance
        {
            Transform = Matrix4x4.CreateTranslation(2f, 0f, 0f),
            InvTransform = Matrix4x4.CreateTranslation(-2f, 0f, 0f),
            MinBounds = Vector2.Zero,
            MaxBounds = new Vector2(8f, 8f),
            TransformIndex = 1
        };
        var retained = new GpuShapeTransform(
            Matrix4x4.CreateTranslation(30f, 0f, 0f),
            Matrix4x4.CreateTranslation(-30f, 0f, 0f));

        Assert.True(WavefrontVectorEngine.TryGetCoveredCellRange(
            instance,
            retained,
            width: 64,
            height: 32,
            dpiScale: 1f,
            gridStride: 4,
            gridRows: 2,
            out var min,
            out var max));

        Assert.Equal((2u, 0u), min);
        Assert.Equal((2u, 0u), max);
        Assert.Equal(2f, instance.Transform.M41);
    }

    private static float MaximumMatchedParameterChordError(in GpuBezierCurve curve, uint subdivisions)
    {
        float maximum = 0f;
        const int sampleCount = 16_384;
        for (int sample = 0; sample <= sampleCount; sample++)
        {
            float t = sample / (float)sampleCount;
            float scaled = t * subdivisions;
            uint segment = t >= 1f ? subdivisions - 1u : (uint)scaled;
            float segmentT = t >= 1f ? 1f : scaled - segment;
            float t0 = segment / (float)subdivisions;
            float t1 = (segment + 1u) / (float)subdivisions;
            Vector2 chord = Vector2.Lerp(Evaluate(curve, t0), Evaluate(curve, t1), segmentT);
            maximum = MathF.Max(maximum, Vector2.Distance(Evaluate(curve, t), chord));
        }

        return maximum;
    }

    private static Vector2 Evaluate(in GpuBezierCurve curve, float t)
    {
        float oneMinusT = 1f - t;
        return curve.CurveType switch
        {
            0 => Vector2.Lerp(curve.P0, curve.P1, t),
            1 => oneMinusT * oneMinusT * curve.P0 +
                 2f * oneMinusT * t * curve.P1 +
                 t * t * curve.P2,
            _ => oneMinusT * oneMinusT * oneMinusT * curve.P0 +
                 3f * oneMinusT * oneMinusT * t * curve.P1 +
                 3f * oneMinusT * t * t * curve.P2 +
                 t * t * t * curve.P3
        };
    }

    private static uint[] Slice(GpuGridCell cell, uint[] indices) =>
        indices.AsSpan((int)cell.ShapeStartOffset, (int)cell.ShapeCount).ToArray();
}
