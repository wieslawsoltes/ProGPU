using System;
using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkTextBlobTests
{
    [Fact]
    public void BuilderPreservesAllPositionedRuns()
    {
        using var builder = new SKTextBlobBuilder();
        using var font = new SKFont(SKTypeface.Default, 12f);
        using var fallbackFont = new SKFont(SKTypeface.Default, 24f);

        var first = builder.AllocatePositionedRun(font, 2);
        first.SetGlyphs(new ushort[] { 10, 11 });
        first.SetPositions(new[] { new SKPoint(1f, 2f), new SKPoint(3f, 4f) });

        var second = builder.AllocatePositionedRun(fallbackFont, 1);
        second.SetGlyphs(new ushort[] { 20 });
        second.SetPositions(new[] { new SKPoint(5f, 6f) });

        using var blob = builder.Build();

        Assert.NotNull(blob);
        Assert.Equal(2, blob.Runs.Length);
        Assert.Equal(new ushort[] { 10, 11 }, blob.Runs[0].GlyphIndices);
        Assert.Equal(new ushort[] { 20 }, blob.Runs[1].GlyphIndices);
        Assert.Equal(12f, blob.Runs[0].Font.Size);
        Assert.Equal(24f, blob.Runs[1].Font.Size);
        Assert.Equal(new ushort[] { 10, 11, 20 }, blob.GlyphIndices);
        Assert.Equal(3, blob.GlyphPositions.Length);
    }

    [Fact]
    public void BuildSnapshotsRunBuffersAndClearsBuilder()
    {
        using var builder = new SKTextBlobBuilder();
        using var font = new SKFont(SKTypeface.Default, 12f);

        var run = builder.AllocatePositionedRun(font, 1);
        run.SetGlyphs(new ushort[] { 42 });
        run.SetPositions(new[] { new SKPoint(7f, 8f) });

        using var blob = builder.Build();
        run.Glyphs[0] = 99;
        run.Positions[0] = new SKPoint(9f, 10f);

        Assert.NotNull(blob);
        Assert.Equal(new ushort[] { 42 }, blob.Runs[0].GlyphIndices);
        Assert.Equal(7f, blob.Runs[0].GlyphPositions[0].X);
        Assert.Null(builder.Build());
    }

    [Fact]
    public void SingleRunBlobReusesItsImmutableSnapshotAsAggregateStorage()
    {
        using var builder = new SKTextBlobBuilder();
        using var font = new SKFont(SKTypeface.Default, 12f);

        var run = builder.AllocatePositionedRun(font, 2);
        run.SetGlyphs(new ushort[] { 42, 43 });
        run.SetPositions(new[] { new SKPoint(7f, 8f), new SKPoint(9f, 10f) });

        using var blob = builder.Build();

        Assert.NotNull(blob);
        var retainedRun = Assert.Single(blob.Runs);
        Assert.Same(retainedRun.GlyphIndices, blob.GlyphIndices);
        Assert.Same(retainedRun.GlyphPositions, blob.GlyphPositions);
    }

    [Fact]
    public void ReusedBuilderKeepsOneBoundedPositionedScratchRun()
    {
        using var builder = new SKTextBlobBuilder();
        using var font = new SKFont(SKTypeface.Default, 12f);
        var glyphs = new ushort[32];
        var positions = new SKPoint[32];

        _ = BuildPositionedBlob(builder, font, glyphs, positions, 1);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var checksum = BuildPositionedBlob(builder, font, glyphs, positions, 100);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(100, checksum);
        Assert.InRange(allocated / 100, 0, 128);
    }

    private static int BuildPositionedBlob(
        SKTextBlobBuilder builder,
        SKFont font,
        ushort[] glyphs,
        SKPoint[] positions,
        int count)
    {
        var checksum = 0;
        for (var index = 0; index < count; index++)
        {
            var run = builder.AllocatePositionedRun(font, glyphs.Length);
            run.SetGlyphs(glyphs);
            run.SetPositions(positions);
            using var blob = builder.Build();
            checksum += blob is null ? 0 : 1;
        }

        return checksum;
    }

    [Fact]
    public void GetInterceptsPreservesPerGlyphIntervalsAndTruncatesShortSpans()
    {
        using var font = new SKFont(SKTypeface.Default, 40f);
        var glyph = font.Typeface.Font.GetGlyphIndex('H');
        using var glyphPath = font.GetGlyphPath(glyph);
        var bounds = glyphPath!.Bounds;
        var lower = bounds.Top + bounds.Height * 0.25f;
        var upper = bounds.Top + bounds.Height * 0.75f;
        using var blob = new SKTextBlob(
            font,
            new[] { glyph, glyph },
            new[] { SKPoint.Empty, new SKPoint(10f, 0f) });

        var intervals = blob.GetIntercepts(lower, upper);

        Assert.Equal(4, blob.CountIntercepts(lower, upper));
        Assert.Equal(4, intervals.Length);
        Assert.True(intervals[0] < intervals[1]);
        AssertNear(intervals[0] + 10f, intervals[2]);
        AssertNear(intervals[1] + 10f, intervals[3]);
        Assert.True(intervals[2] < intervals[1]);

        var destination = new[] { -999f, -999f, -999f, -999f, -999f, -999f };
        blob.GetIntercepts(lower, upper, destination);
        Assert.Equal(intervals, destination.AsSpan(0, intervals.Length).ToArray());
        Assert.Equal(-999f, destination[4]);
        Assert.Equal(-999f, destination[5]);

        var shortDestination = new[] { -999f, -999f, -999f };
        blob.GetIntercepts(lower, upper, shortDestination);
        Assert.Equal(intervals.AsSpan(0, shortDestination.Length).ToArray(), shortDestination);
    }

    [Fact]
    public void GetInterceptsRejectsInvalidBandsAndSkipsRotationScaleRuns()
    {
        using var font = new SKFont(SKTypeface.Default, 40f);
        var glyph = font.Typeface.Font.GetGlyphIndex('A');
        using var glyphPath = font.GetGlyphPath(glyph);
        using var positioned = new SKTextBlob(
            font,
            new[] { glyph },
            new[] { SKPoint.Empty });

        Assert.Empty(positioned.GetIntercepts(5f, -5f));
        Assert.Empty(positioned.GetIntercepts(float.NaN, 5f));
        Assert.Empty(positioned.GetIntercepts(glyphPath!.Bounds.Bottom + 1f, glyphPath.Bounds.Bottom + 2f));

        using var rotated = SKTextBlob.CreateRotationScale(
            "A",
            font,
            new[] { SKRotationScaleMatrix.Identity });
        Assert.NotNull(rotated);
        Assert.Empty(rotated.GetIntercepts(glyphPath.Bounds.Top, glyphPath.Bounds.Bottom));
    }

    [Fact]
    public void GetInterceptsAppliesFontScaleSkewAndEmbolden()
    {
        using var normalFont = new SKFont(SKTypeface.Default, 40f);
        using var scaledFont = new SKFont(SKTypeface.Default, 40f, scaleX: 1.5f);
        using var skewedFont = new SKFont(SKTypeface.Default, 40f, scaleX: 1f, skewX: 0.25f);
        using var emboldenedFont = new SKFont(SKTypeface.Default, 40f) { Embolden = true };
        var glyph = normalFont.Typeface.Font.GetGlyphIndex('A');
        using var normalPath = normalFont.GetGlyphPath(glyph);
        var lower = normalPath!.Bounds.Top * 0.7f;
        var upper = normalPath.Bounds.Top * 0.5f;

        using var normal = CreateSingleGlyphBlob(normalFont, glyph);
        using var scaled = CreateSingleGlyphBlob(scaledFont, glyph);
        using var skewed = CreateSingleGlyphBlob(skewedFont, glyph);
        using var emboldened = CreateSingleGlyphBlob(emboldenedFont, glyph);
        var normalIntervals = normal.GetIntercepts(lower, upper);
        var scaledIntervals = scaled.GetIntercepts(lower, upper);
        var skewedIntervals = skewed.GetIntercepts(lower, upper);
        var emboldenedIntervals = emboldened.GetIntercepts(lower, upper);

        Assert.Equal(2, normalIntervals.Length);
        AssertNear(normalIntervals[0] * 1.5f, scaledIntervals[0]);
        AssertNear(normalIntervals[1] * 1.5f, scaledIntervals[1]);
        Assert.True(skewedIntervals[0] < normalIntervals[0]);
        Assert.True(skewedIntervals[1] < normalIntervals[1]);
        Assert.True(emboldenedIntervals[0] < normalIntervals[0]);
        Assert.True(emboldenedIntervals[1] > normalIntervals[1]);
    }

    [Fact]
    public void GetInterceptsPaintOverloadMatchesNativeCurrentGeometry()
    {
        using var font = new SKFont(SKTypeface.Default, 40f);
        var glyph = font.Typeface.Font.GetGlyphIndex('A');
        using var path = font.GetGlyphPath(glyph);
        using var blob = CreateSingleGlyphBlob(font, glyph);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4f,
            StrokeJoin = SKStrokeJoin.Round
        };
        var lower = path!.Bounds.Top * 0.7f;
        var upper = path.Bounds.Top * 0.5f;

        Assert.Equal(
            blob.GetIntercepts(lower, upper),
            blob.GetIntercepts(lower, upper, paint));
    }

    [Fact]
    public void PathInterceptsSolveQuadraticAndCubicBandCrossings()
    {
        var method = typeof(SKTextBlob).GetMethod(
            "TryFindPathIntercept",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        using var quadratic = new SKPath();
        quadratic.MoveTo(0f, 0f);
        quadratic.QuadTo(10f, 20f, 20f, 0f);

        var quadraticArguments = new object[] { quadratic, 5f, 5f, 0f, 0f };
        Assert.True((bool)method.Invoke(null, quadraticArguments)!);
        var offset = MathF.Sqrt(0.5f);
        AssertNear(10f * (1f - offset), (float)quadraticArguments[3]);
        AssertNear(10f * (1f + offset), (float)quadraticArguments[4]);

        using var cubic = new SKPath();
        cubic.MoveTo(0f, -8f);
        cubic.CubicTo(10f, 14f, 20f, -14f, 30f, 8f);
        var cubicArguments = new object[] { cubic, 0f, 0f, 0f, 0f };
        Assert.True((bool)method.Invoke(null, cubicArguments)!);
        AssertNear(6f, (float)cubicArguments[3]);
        AssertNear(24f, (float)cubicArguments[4]);
    }

    private static SKTextBlob CreateSingleGlyphBlob(SKFont font, ushort glyph) =>
        new(font, new[] { glyph }, new[] { SKPoint.Empty });

    private static void AssertNear(float expected, float actual) =>
        Assert.InRange(actual, expected - 0.001f, expected + 0.001f);
}
