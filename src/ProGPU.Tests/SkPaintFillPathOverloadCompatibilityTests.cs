using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPaintFillPathOverloadCompatibilityTests
{
    [Fact]
    public void CollinearReversingCurvesRetainTheirStrokeExtrema()
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
        };
        using var quadratic = new SKPath();
        quadratic.MoveTo(0f, 0f);
        quadratic.QuadTo(100f, 0f, 1f, 0f);
        using var quadraticStroke = paint.GetFillPath(quadratic);
        using var cubic = new SKPath();
        cubic.MoveTo(0f, 0f);
        cubic.CubicTo(100f, 0f, -100f, 0f, 1f, 0f);
        using var cubicStroke = paint.GetFillPath(cubic);

        Assert.NotNull(quadraticStroke);
        Assert.True(quadraticStroke!.Bounds.Right > 50f);
        Assert.NotNull(cubicStroke);
        Assert.True(cubicStroke!.Bounds.Left < -20f);
        Assert.True(cubicStroke.Bounds.Right > 20f);
    }

    [Fact]
    public void ReturnOverloadsTreatCullAsHintAndMatrixAsResolutionScale()
    {
        using var source = CreateCurve(SKPathFillType.EvenOdd);
        using var paint = CreateStrokePaint();
        using var baseline = paint.GetFillPath(source);
        using var culled = paint.GetFillPath(source, new SKRect(1000f, 1000f, 1100f, 1100f));
        using var scaled = paint.GetFillPath(source, 4f);
        using var matrixScaled = paint.GetFillPath(source, SKMatrix.CreateScale(4f, 2f));
        using var cullMatrixScaled = paint.GetFillPath(
            source,
            new SKRect(1000f, 1000f, 1100f, 1100f),
            SKMatrix.CreateScale(4f, 2f));

        Assert.NotNull(baseline);
        Assert.NotNull(culled);
        Assert.NotNull(scaled);
        Assert.NotNull(matrixScaled);
        Assert.NotNull(cullMatrixScaled);
        Assert.Equal(baseline.Bounds, culled.Bounds);
        Assert.Equal(scaled.Bounds, matrixScaled.Bounds);
        Assert.Equal(scaled.PointCount, matrixScaled.PointCount);
        Assert.Equal(scaled.PointCount, cullMatrixScaled.PointCount);
        Assert.True(scaled.PointCount > baseline.PointCount);
    }

    [Fact]
    public void ResolutionScaleFallsBackToOneForInvalidOrSmallValues()
    {
        using var source = CreateCurve();
        using var paint = CreateStrokePaint();
        using var baseline = paint.GetFillPath(source, 1f);
        using var zero = paint.GetFillPath(source, 0f);
        using var negative = paint.GetFillPath(source, -2f);
        using var nan = paint.GetFillPath(source, float.NaN);
        using var infinity = paint.GetFillPath(source, float.PositiveInfinity);

        Assert.NotNull(baseline);
        Assert.Equal(baseline.PointCount, zero!.PointCount);
        Assert.Equal(baseline.PointCount, negative!.PointCount);
        Assert.Equal(baseline.PointCount, nan!.PointCount);
        Assert.Equal(baseline.PointCount, infinity!.PointCount);
    }

    [Fact]
    public void ObsoletePathDestinationIsReplacedOnlyOnSuccess()
    {
        using var source = CreateCurve();
        using var destination = CreateSentinelPath();
        using var hairline = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0f };

#pragma warning disable CS0618
        Assert.False(hairline.GetFillPath(source, destination));
#pragma warning restore CS0618
        Assert.Equal(new SKRect(-100f, -100f, -90f, -90f), destination.Bounds);

        using var stroke = CreateStrokePaint();
#pragma warning disable CS0618
        Assert.True(stroke.GetFillPath(source, destination, 4f));
#pragma warning restore CS0618
        Assert.NotEqual(new SKRect(-100f, -100f, -90f, -90f), destination.Bounds);
        Assert.Equal(SKPathFillType.Winding, destination.FillType);
    }

    [Fact]
    public void BuilderDestinationFallsBackToSourceSnapshotOnFailure()
    {
        using var source = CreateCurve(SKPathFillType.EvenOdd);
        using var builder = new SKPathBuilder();
        builder.AddRect(new SKRect(-100f, -100f, -90f, -90f));
        using var hairline = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0f };

        Assert.False(hairline.GetFillPath(source, builder));

        using var fallback = builder.Snapshot();
        Assert.Equal(source.FillType, fallback.FillType);
        Assert.Equal(source.Bounds, fallback.Bounds);
        Assert.Equal(source.PointCount, fallback.PointCount);

        using var stroke = CreateStrokePaint();
        Assert.True(stroke.GetFillPath(source, builder, 4f));
        using var widened = builder.Snapshot();
        Assert.Equal(SKPathFillType.Winding, widened.FillType);
        Assert.NotEqual(source.Bounds, widened.Bounds);
    }

    [Fact]
    public void FillPaintPreservesFillTypeAndSelfDestinationIsSafe()
    {
        using var source = CreateCurve(SKPathFillType.EvenOdd);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill };
        using var result = fill.GetFillPath(source, SKMatrix.CreateScale(4f, 4f));

        Assert.NotNull(result);
        Assert.Equal(SKPathFillType.EvenOdd, result.FillType);
        Assert.Equal(source.Bounds, result.Bounds);

        using var stroke = CreateStrokePaint();
#pragma warning disable CS0618
        Assert.True(stroke.GetFillPath(source, source));
#pragma warning restore CS0618
        Assert.False(source.IsEmpty);
        Assert.Equal(SKPathFillType.Winding, source.FillType);
    }

    [Fact]
    public void HairlineReturnIsNullButPositiveDegenerateStrokeSucceedsEmpty()
    {
        using var source = new SKPath();
        source.MoveTo(10f, 10f);
        source.LineTo(10f, 10f);
        using var hairline = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0f };
        Assert.Null(hairline.GetFillPath(source));

        using var stroke = CreateStrokePaint();
        stroke.StrokeCap = SKStrokeCap.Butt;
        using var result = stroke.GetFillPath(source);
        Assert.NotNull(result);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void StrokeWidthClampsNegativeAndNanButRetainsPositiveInfinity()
    {
        using var paint = new SKPaint();
        paint.StrokeWidth = -1f;
        Assert.Equal(0f, paint.StrokeWidth);
        paint.StrokeWidth = float.NaN;
        Assert.Equal(0f, paint.StrokeWidth);
        paint.StrokeWidth = float.PositiveInfinity;
        Assert.Equal(float.PositiveInfinity, paint.StrokeWidth);

        paint.Style = SKPaintStyle.Stroke;
        using var source = CreateCurve();
        using var result = paint.GetFillPath(source);
        Assert.NotNull(result);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void WarmedCallerOwnedStrokeDestinationKeepsManagedAllocationBounded()
    {
        using var source = CreateCurve();
        using var destination = new SKPath();
        using var paint = CreateStrokePaint();

#pragma warning disable CS0618
        for (var index = 0; index < 64; index++)
        {
            Assert.True(paint.GetFillPath(source, destination));
        }

        const int iterations = 256;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            paint.GetFillPath(source, destination);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
#pragma warning restore CS0618

        Assert.True(allocated <= iterations * 128L, $"Allocated {allocated} bytes.");
        Assert.False(destination.IsEmpty);
    }

    private static SKPath CreateCurve(SKPathFillType fillType = SKPathFillType.Winding)
    {
        var path = new SKPath { FillType = fillType };
        path.MoveTo(0f, 0f);
        path.QuadTo(50f, 100f, 100f, 0f);
        return path;
    }

    private static SKPath CreateSentinelPath()
    {
        var path = new SKPath();
        path.AddRect(new SKRect(-100f, -100f, -90f, -90f));
        return path;
    }

    private static SKPaint CreateStrokePaint() => new()
    {
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 10f,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };
}
