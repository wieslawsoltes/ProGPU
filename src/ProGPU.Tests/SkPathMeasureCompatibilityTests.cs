using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPathMeasureCompatibilityTests
{
    [Fact]
    public void PathBuilderSnapshotsAndDetachesWithoutSharingState()
    {
        using var builder = new SKPathBuilder
        {
            FillType = SKPathFillType.EvenOdd,
        };
        builder.MoveTo(1f, 2f);
        builder.ConicTo(3f, 4f, 5f, 6f, 0.5f);

        using var snapshot = builder.Snapshot();
        using var detached = builder.Detach();
        using var afterDetach = builder.Snapshot();

        Assert.IsAssignableFrom<SKObject>(builder);
        Assert.Equal(SKPathFillType.EvenOdd, snapshot.FillType);
        Assert.Equal(SKPathFillType.EvenOdd, detached.FillType);
        Assert.Equal([SKPathVerb.Move, SKPathVerb.Conic], GetVerbs(snapshot));
        Assert.Equal([SKPathVerb.Move, SKPathVerb.Conic], GetVerbs(detached));
        Assert.True(afterDetach.IsEmpty);
        Assert.Equal(SKPathFillType.Winding, afterDetach.FillType);

        builder.FillType = SKPathFillType.InverseEvenOdd;
        builder.MoveTo(10f, 20f);
        builder.Reset();
        using var afterReset = builder.Snapshot();
        Assert.True(afterReset.IsEmpty);
        Assert.Equal(SKPathFillType.Winding, afterReset.FillType);
    }

    [Fact]
    public void PathMeasureClampsLineSamplesAndMatchesMatrixFlags()
    {
        using var path = new SKPath();
        path.MoveTo(10f, 20f);
        path.LineTo(40f, 20f);
        using var measure = new SKPathMeasure(path);

        Assert.Equal(30f, measure.Length);
        Assert.Equal(new SKPoint(10f, 20f), measure.GetPosition(-5f));
        Assert.Equal(new SKPoint(40f, 20f), measure.GetPosition(50f));
        Assert.Equal(new SKPoint(1f, 0f), measure.GetTangent(15f));

        var matrix = measure.GetMatrix(15f, SKPathMeasureMatrixFlags.GetPositionAndTangent);
        Assert.Equal(1f, matrix.ScaleX);
        Assert.Equal(1f, matrix.ScaleY);
        Assert.Equal(25f, matrix.TransX);
        Assert.Equal(20f, matrix.TransY);
    }

    [Fact]
    public void FailedPathMeasureQueriesMatchNativeOutputSemantics()
    {
        using var measure = new SKPathMeasure();
        var position = new SKPoint(12f, 34f);
        var tangent = new SKPoint(56f, 78f);
        var matrix = SKMatrix.CreateTranslation(90f, 12f);

        Assert.False(measure.GetPosition(1f, out position));
        Assert.False(measure.GetTangent(1f, out tangent));
        Assert.False(measure.GetMatrix(
            1f,
            out matrix,
            SKPathMeasureMatrixFlags.GetPositionAndTangent));

        Assert.Equal(new SKPoint(12f, 34f), position);
        Assert.Equal(new SKPoint(56f, 78f), tangent);
        Assert.Equal(SKMatrix.Identity, matrix);

        position = new SKPoint(90f, 91f);
        tangent = new SKPoint(92f, 93f);
        Assert.False(measure.GetPositionAndTangent(float.NaN, out position, out tangent));
        Assert.Equal(new SKPoint(90f, 91f), position);
        Assert.Equal(new SKPoint(92f, 93f), tangent);
        Assert.Equal(SKPoint.Empty, measure.GetPosition(1f));
        Assert.Equal(SKPoint.Empty, measure.GetTangent(1f));
        Assert.Equal(SKMatrix.Empty, measure.GetMatrix(
            1f,
            SKPathMeasureMatrixFlags.GetPositionAndTangent));
    }

    [Fact]
    public void PathMeasureTracksContoursAndSetPathClearsForcedClosure()
    {
        using var path = new SKPath();
        path.MoveTo(0f, 0f);
        path.LineTo(10f, 0f);
        path.MoveTo(20f, 20f);
        path.LineTo(25f, 20f);
        using var measure = new SKPathMeasure(path, forceClosed: true);

        Assert.True(measure.IsClosed);
        Assert.Equal(20f, measure.Length);
        Assert.True(measure.NextContour());
        Assert.True(measure.IsClosed);
        Assert.Equal(10f, measure.Length);
        Assert.False(measure.NextContour());
        Assert.Equal(0f, measure.Length);

        measure.SetPath(path);
        Assert.False(measure.IsClosed);
        Assert.Equal(10f, measure.Length);
    }

    [Fact]
    public void SegmentExtractionRetainsCurveVerbsAndNativeRangeRules()
    {
        using var path = CreateMeasuredPath();
        using var measure = new SKPathMeasure(path);
        using var full = measure.GetSegment(0f, measure.Length, startWithMoveTo: true);

        Assert.NotNull(full);
        Assert.Equal(
            [SKPathVerb.Move, SKPathVerb.Line, SKPathVerb.Quad, SKPathVerb.Conic, SKPathVerb.Cubic],
            GetVerbs(full));

        using var equalBuilder = new SKPathBuilder();
        Assert.True(measure.GetSegment(15f, 15f, equalBuilder, startWithMoveTo: true));
        using var equal = equalBuilder.Snapshot();
        Assert.Equal([SKPathVerb.Move, SKPathVerb.Line], GetVerbs(equal));
        Assert.False(measure.GetSegment(-10f, -5f, equalBuilder, startWithMoveTo: true));
        Assert.False(measure.GetSegment(
            measure.Length + 5f,
            measure.Length + 10f,
            equalBuilder,
            startWithMoveTo: true));
        Assert.Null(measure.GetSegment(20f, 10f, startWithMoveTo: true));
    }

    [Fact]
    public void ObsoletePathSegmentOverloadReplacesDestinationLikeNative()
    {
        using var path = new SKPath();
        path.MoveTo(10f, 20f);
        path.LineTo(40f, 20f);
        using var measure = new SKPathMeasure(path);
        using var destination = new SKPath();
        destination.MoveTo(-1f, -2f);
        destination.LineTo(-3f, -4f);

#pragma warning disable CS0618
        Assert.True(measure.GetSegment(0f, 10f, destination, startWithMoveTo: false));
#pragma warning restore CS0618

        Assert.Equal([SKPathVerb.Move, SKPathVerb.Line], GetVerbs(destination));
        Assert.Equal(new SKPoint(0f, 0f), destination[0]);
        Assert.Equal(new SKPoint(20f, 20f), destination[1]);
    }

    [Fact]
    public void PartialConicExtractionMatchesNativeHomogeneousSlice()
    {
        using var path = new SKPath();
        path.MoveTo(0f, 0f);
        path.ConicTo(20f, 40f, 60f, 10f, 0.5f);
        using var measure = new SKPathMeasure(path);
        using var segment = measure.GetSegment(
            measure.Length * 0.2f,
            measure.Length * 0.8f,
            startWithMoveTo: true);

        Assert.NotNull(segment);
        using var iterator = segment.CreateRawIterator();
        var points = new SKPoint[4];
        Assert.Equal(SKPathVerb.Move, iterator.Next(points));
        AssertPointNear(new SKPoint(8.996119f, 9.840842f), points[0], 0.25f);
        Assert.Equal(SKPathVerb.Conic, iterator.Next(points));
        AssertPointNear(new SKPoint(26.272507f, 21.661184f), points[1], 0.25f);
        AssertPointNear(new SKPoint(47.98278f, 15.692864f), points[2], 0.25f);
        Assert.InRange(MathF.Abs(iterator.ConicWeight() - 0.82826424f), 0f, 0.003f);
        Assert.Equal(SKPathVerb.Done, iterator.Next(points));
    }

    [Fact]
    public void ResolutionScaleTracksNativePrecisionWithoutOversampling()
    {
        using var path = CreateMeasuredPath();
        using var defaultMeasure = new SKPathMeasure(path);
        using var preciseMeasure = new SKPathMeasure(path, forceClosed: false, resScale: 4f);

        Assert.InRange(MathF.Abs(defaultMeasure.Length - 133.40717f), 0f, 0.1f);
        Assert.InRange(MathF.Abs(preciseMeasure.Length - 133.7622f), 0f, 0.03f);
    }

    [Fact]
    public void WarmedPackedMeasuresRetainStorageAndRemainMutationIsolated()
    {
        using var path = CreateMeasuredPathWithoutConic();
        using (var warmup = new SKPathMeasure(path))
        {
            Assert.True(warmup.Length > 0f);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var measure = new SKPathMeasure(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var originalLength = measure.Length;

        path.LineTo(500f, 600f);

        Assert.InRange(allocated, 0, 64);
        Assert.Equal(originalLength, measure.Length);
        Assert.True(measure.GetPositionAndTangent(
            originalLength * 0.5f,
            out var position,
            out var tangent));
        Assert.True(float.IsFinite(position.X + position.Y + tangent.X + tangent.Y));
    }

    private static SKPath CreateMeasuredPath()
    {
        var path = new SKPath();
        path.MoveTo(10f, 20f);
        path.LineTo(40f, 20f);
        path.QuadTo(60f, 20f, 60f, 40f);
        path.ConicTo(60f, 60f, 40f, 60f, 0.70710677f);
        path.CubicTo(20f, 60f, 10f, 50f, 10f, 40f);
        return path;
    }

    private static SKPath CreateMeasuredPathWithoutConic()
    {
        var path = new SKPath();
        path.MoveTo(10f, 20f);
        path.LineTo(40f, 20f);
        path.QuadTo(60f, 20f, 60f, 40f);
        path.CubicTo(40f, 60f, 10f, 50f, 10f, 40f);
        return path;
    }

    private static SKPathVerb[] GetVerbs(SKPath path)
    {
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        var verbs = new List<SKPathVerb>();
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            verbs.Add(verb);
        }

        return verbs.ToArray();
    }

    private static void AssertPointNear(SKPoint expected, SKPoint actual, float tolerance)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, tolerance);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, tolerance);
    }
}
