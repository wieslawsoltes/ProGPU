using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPathMutationCompatibilityTests
{
    [Fact]
    public void CommonLegacyVerbStreamKeepsTightBoundsAllocationBounded()
    {
        static float MeasureOnce()
        {
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            path.MoveTo(0f, 0f);
            for (var segment = 0; segment < 8; segment++)
            {
                var x = segment * 6f;
                var y = segment * 3f;
                path.LineTo(x + 1f, y + 2f);
                path.QuadTo(x + 2f, y - 1f, x + 3f, y + 3f);
                path.CubicTo(x + 3.5f, y + 4f, x + 4.5f, y - 2f, x + 5f, y + 1f);
            }

            path.Close();
            var bounds = path.TightBounds;
            return bounds.Left + bounds.Top + bounds.Right + bounds.Bottom;
        }

        _ = MeasureOnce();
        const int iterations = 1_000;
        var checksum = 0f;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            checksum += MeasureOnce();
        }

        var allocatedPerPath =
            (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;
        Assert.True(float.IsFinite(checksum));
        Assert.InRange(allocatedPerPath, 0, 128);
    }

    [Fact]
    public void TightCurveBoundsPreserveFractionalTranslation()
    {
        static SKRect Build(float offset)
        {
            using var path = new SKPath();
            path.MoveTo(offset, -offset);
            path.QuadTo(2f + offset, -1f - offset, 3f + offset, 3f - offset);
            path.CubicTo(
                3.5f + offset,
                4f - offset,
                4.5f + offset,
                -2f - offset,
                5f + offset,
                1f - offset);
            return path.TightBounds;
        }

        const float offset = 0.125f;
        var baseline = Build(0f);
        var translated = Build(offset);

        Assert.Equal(baseline.Left + offset, translated.Left);
        Assert.Equal(baseline.Right + offset, translated.Right);
        Assert.Equal(baseline.Top - offset, translated.Top);
        Assert.Equal(baseline.Bottom - offset, translated.Bottom);
    }

    [Fact]
    public void RelativeCommandsUseOneNativeCurrentPointPerVerb()
    {
        using var path = new SKPath();
        path.RLineTo(2f, 3f);
        path.RQuadTo(4f, 5f, 6f, 7f);
        path.RCubicTo(8f, 9f, 10f, 11f, 12f, 13f);
        path.RConicTo(14f, 15f, 16f, 17f, 0.25f);

        Assert.Equal(5, path.VerbCount);
        Assert.Equal(
            new[]
            {
                new SKPoint(0f, 0f),
                new SKPoint(2f, 3f),
                new SKPoint(6f, 8f),
                new SKPoint(8f, 10f),
                new SKPoint(16f, 19f),
                new SKPoint(18f, 21f),
                new SKPoint(20f, 23f),
                new SKPoint(34f, 38f),
                new SKPoint(36f, 40f),
            },
            path.Points);
        Assert.Equal(SKPathSegmentMask.Line |
                     SKPathSegmentMask.Quad |
                     SKPathSegmentMask.Cubic |
                     SKPathSegmentMask.Conic,
            path.SegmentMasks);

        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        while (iterator.Next(points) is var verb && verb != SKPathVerb.Conic)
        {
            Assert.NotEqual(SKPathVerb.Done, verb);
        }
        Assert.Equal(0.25f, iterator.ConicWeight());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelativeCommandAfterCloseUsesContourOrigin(bool startsNewMove)
    {
        using var path = new SKPath();
        path.MoveTo(10f, 10f);
        path.LineTo(20f, 10f);
        path.Close();

        if (startsNewMove)
        {
            path.RMoveTo(5f, 6f);
            Assert.Equal(4, path.VerbCount);
            Assert.Equal(
                new[]
                {
                    new SKPoint(10f, 10f),
                    new SKPoint(20f, 10f),
                    new SKPoint(15f, 16f),
                },
                path.Points);
        }
        else
        {
            path.RLineTo(5f, 6f);
            Assert.Equal(5, path.VerbCount);
            Assert.Equal(
                new[]
                {
                    new SKPoint(10f, 10f),
                    new SKPoint(20f, 10f),
                    new SKPoint(10f, 10f),
                    new SKPoint(15f, 16f),
                },
                path.Points);
        }

        Assert.Equal(new SKPoint(15f, 16f), path.LastPoint);
    }

    [Fact]
    public void RelativeArcUsesCurrentPointForEndpoint()
    {
        using var path = new SKPath();
        path.MoveTo(10f, 20f);
        path.RArcTo(
            new SKPoint(20f, 10f),
            0f,
            SKPathArcSize.Small,
            SKPathDirection.Clockwise,
            new SKPoint(30f, 0f));

        Assert.Equal(new SKPoint(40f, 20f), path.LastPoint);
        Assert.Equal(SKPathSegmentMask.Conic, path.SegmentMasks);
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        Assert.Equal(SKPathVerb.Move, iterator.Next(points));
        SKPathVerb verb;
        do
        {
            verb = iterator.Next(points);
        }
        while (verb == SKPathVerb.Conic && points[2] != new SKPoint(40f, 20f));
        Assert.Equal(SKPathVerb.Conic, verb);
        Assert.Equal(new SKPoint(40f, 20f), points[2]);
    }

    [Fact]
    public void OffsetPreservesActiveContourAndConicMetadata()
    {
        using var path = new SKPath();
        path.MoveTo(0f, 0f);
        path.ConicTo(2f, 4f, 6f, 8f, 0.25f);
        path.Offset(10f, 20f);
        path.RLineTo(2f, 3f);

        Assert.Equal(3, path.VerbCount);
        Assert.Equal(
            new[]
            {
                new SKPoint(10f, 20f),
                new SKPoint(12f, 24f),
                new SKPoint(16f, 28f),
                new SKPoint(18f, 31f),
            },
            path.Points);
        Assert.Equal(SKPathSegmentMask.Conic | SKPathSegmentMask.Line, path.SegmentMasks);
    }

    [Fact]
    public void RewindMatchesNativeResetContract()
    {
        using var path = new SKPath
        {
            FillType = SKPathFillType.EvenOdd,
        };
        path.AddRect(new SKRect(1f, 2f, 3f, 4f));

        path.Rewind();

        Assert.True(path.IsEmpty);
        Assert.Equal(0, path.VerbCount);
        Assert.Equal(SKPathFillType.Winding, path.FillType);
    }

    [Fact]
    public void DestinationTransformReplacesTargetWithoutMutatingSource()
    {
        using var source = new SKPath
        {
            FillType = SKPathFillType.EvenOdd,
        };
        source.MoveTo(1f, 2f);
        source.ConicTo(3f, 4f, 5f, 6f, 0.25f);
        using var destination = new SKPath();
        destination.AddRect(new SKRect(100f, 100f, 110f, 110f));
        var translation = SKMatrix.CreateTranslation(10f, 20f);

        source.Transform(in translation, destination);

        Assert.Equal(
            new[] { new SKPoint(1f, 2f), new SKPoint(3f, 4f), new SKPoint(5f, 6f) },
            source.Points);
        Assert.Equal(
            new[] { new SKPoint(11f, 22f), new SKPoint(13f, 24f), new SKPoint(15f, 26f) },
            destination.Points);
        Assert.Equal(SKPathFillType.EvenOdd, destination.FillType);
        Assert.Equal(SKPathSegmentMask.Conic, destination.SegmentMasks);

        using var iterator = destination.CreateRawIterator();
        var points = new SKPoint[4];
        Assert.Equal(SKPathVerb.Move, iterator.Next(points));
        Assert.Equal(SKPathVerb.Conic, iterator.Next(points));
        Assert.Equal(0.25f, iterator.ConicWeight());
    }

    [Fact]
    public void AliasedTransformRunsOnceAndPreservesActiveContour()
    {
        using var path = new SKPath();
        path.MoveTo(0f, 0f);
        path.LineTo(6f, 8f);
        var translation = SKMatrix.CreateTranslation(10f, 20f);

        path.Transform(translation, path);
        path.RLineTo(2f, 3f);

        Assert.Equal(
            new[]
            {
                new SKPoint(10f, 20f),
                new SKPoint(16f, 28f),
                new SKPoint(18f, 31f),
            },
            path.Points);
        Assert.Equal(3, path.VerbCount);
    }
}
