using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkTextBlobBuilderApiCompatibilityTests
{
    [Fact]
    public void BuilderExposesNativeRunBufferSignatures()
    {
        AssertParameterNames(
            GetBuilderMethod(
                nameof(SKTextBlobBuilder.AllocateRun),
                typeof(SKFont),
                typeof(int),
                typeof(float),
                typeof(float),
                typeof(SKRect?)),
            "font",
            "count",
            "x",
            "y",
            "bounds");
        AssertParameterNames(
            GetBuilderMethod(
                nameof(SKTextBlobBuilder.AllocateRawHorizontalTextRun),
                typeof(SKFont),
                typeof(int),
                typeof(float),
                typeof(int),
                typeof(SKRect?)),
            "font",
            "count",
            "y",
            "textByteCount",
            "bounds");
        AssertParameterNames(
            GetBuilderMethod(
                nameof(SKTextBlobBuilder.AllocatePositionedTextRun),
                typeof(SKFont),
                typeof(int),
                typeof(int),
                typeof(SKRect?)),
            "font",
            "count",
            "textByteCount",
            "bounds");
        AssertParameterNames(
            GetBuilderMethod(
                nameof(SKTextBlobBuilder.AddHorizontalRun),
                typeof(ReadOnlySpan<ushort>),
                typeof(SKFont),
                typeof(ReadOnlySpan<float>),
                typeof(float)),
            "glyphs",
            "font",
            "positions",
            "y");
    }

    [Fact]
    public void TypedBuffersBuildAllNativePlacementModes()
    {
        using var font = new SKFont(SKTypeface.Default, 20f);
        using var builder = new SKTextBlobBuilder();
        var glyphs = font.GetGlyphs("ABCD");

        var run = builder.AllocateRun(font, 1, 3f, 5f);
        run.SetGlyphs(glyphs.AsSpan(0, 1));

        var horizontal = builder.AllocateHorizontalTextRun(font, 1, 7f, textByteCount: 2);
        horizontal.SetGlyphs(glyphs.AsSpan(1, 1));
        horizontal.SetPositions(new[] { 11f });
        horizontal.SetText(new byte[] { 0x41, 0x42 });
        horizontal.SetClusters(new uint[] { 1 });

        var positioned = builder.AllocatePositionedTextRun(font, 1, textByteCount: 1);
        positioned.SetGlyphs(glyphs.AsSpan(2, 1));
        positioned.SetPositions(new[] { new SKPoint(13f, 17f) });
        positioned.SetText(new byte[] { 0x43 });
        positioned.SetClusters(new uint[] { 2 });

        var placement = SKRotationScaleMatrix.CreateDegrees(1.25f, 30f, 19f, 23f, 0f, 0f);
        var rotation = builder.AllocateRotationScaleTextRun(font, 1, textByteCount: 1);
        rotation.SetGlyphs(glyphs.AsSpan(3, 1));
        rotation.SetPositions(new[] { placement });
        rotation.SetText(new byte[] { 0x44 });
        rotation.SetClusters(new uint[] { 3 });

        using var blob = builder.Build();

        Assert.NotNull(blob);
        Assert.Equal(4, blob.Runs.Length);
        Assert.Equal(new SKPoint(3f, 5f), blob.Runs[0].GlyphPositions[0]);
        Assert.Equal(new SKPoint(11f, 7f), blob.Runs[1].GlyphPositions[0]);
        Assert.Equal(new SKPoint(13f, 17f), blob.Runs[2].GlyphPositions[0]);
        Assert.Equal(placement, Assert.Single(blob.Runs[3].RotationScaleMatrices!));
        Assert.Equal(new SKPoint(placement.TX, placement.TY), blob.Runs[3].GlyphPositions[0]);
        Assert.Null(builder.Build());
    }

    [Fact]
    public void RawBuffersExposeNativeSpanLengthsAndSnapshotOnBuild()
    {
        using var font = new SKFont(SKTypeface.Default, 20f);
        using var builder = new SKTextBlobBuilder();
        var glyphs = font.GetGlyphs("ABC");

        var implicitRun = builder.AllocateRawTextRun(font, 1, 2f, 4f, textByteCount: 3);
        Assert.Equal(1, implicitRun.Glyphs.Length);
        Assert.True(implicitRun.Positions.IsEmpty);
        Assert.Equal(3, implicitRun.Text.Length);
        Assert.Equal(1, implicitRun.Clusters.Length);
        implicitRun.Glyphs[0] = glyphs[0];
        implicitRun.Text.Fill(0x61);
        implicitRun.Clusters[0] = 5;

        var positioned = builder.AllocateRawPositionedTextRun(font, 1, textByteCount: 2);
        positioned.Glyphs[0] = glyphs[1];
        positioned.Positions[0] = new SKPoint(6f, 8f);
        positioned.Text.Fill(0x62);
        positioned.Clusters[0] = 7;

        var rotation = builder.AllocateRawRotationScaleRun(font, 1);
        rotation.Glyphs[0] = glyphs[2];
        rotation.Positions[0] = SKRotationScaleMatrix.CreateTranslation(10f, 12f);

        using var blob = builder.Build();
        implicitRun.Glyphs[0] = 0;
        positioned.Positions[0] = SKPoint.Empty;
        rotation.Positions[0] = SKRotationScaleMatrix.Empty;

        Assert.NotNull(blob);
        Assert.Equal(glyphs, blob.GlyphIndices);
        Assert.Equal(new SKPoint(2f, 4f), blob.Runs[0].GlyphPositions[0]);
        Assert.Equal(new SKPoint(6f, 8f), blob.Runs[1].GlyphPositions[0]);
        Assert.Equal(
            SKRotationScaleMatrix.CreateTranslation(10f, 12f),
            blob.Runs[2].RotationScaleMatrices![0]);
    }

    [Fact]
    public void AddRunConveniencesShareRetainedRunPipeline()
    {
        using var font = new SKFont(SKTypeface.Default, 20f);
        using var builder = new SKTextBlobBuilder();
        var glyphs = font.GetGlyphs("ABCD");

        builder.AddRun(glyphs.AsSpan(0, 1), font, new SKPoint(2f, 3f));
        builder.AddHorizontalRun(glyphs.AsSpan(1, 1), font, new[] { 5f }, 7f);
        builder.AddPositionedRun(
            glyphs.AsSpan(2, 1),
            font,
            new[] { new SKPoint(11f, 13f) });
        builder.AddRotationScaleRun(
            glyphs.AsSpan(3, 1),
            font,
            new[] { SKRotationScaleMatrix.CreateTranslation(17f, 19f) });

        using var blob = builder.Build();

        Assert.NotNull(blob);
        Assert.Equal(new SKPoint(2f, 3f), blob.Runs[0].GlyphPositions[0]);
        Assert.Equal(new SKPoint(5f, 7f), blob.Runs[1].GlyphPositions[0]);
        Assert.Equal(new SKPoint(11f, 13f), blob.Runs[2].GlyphPositions[0]);
        Assert.Equal(new SKPoint(17f, 19f), blob.Runs[3].GlyphPositions[0]);
    }

    [Fact]
    public void BuilderAcceptsUnderspecifiedPlacementSpansLikeNativeSkia()
    {
        using var font = new SKFont(SKTypeface.Default, 20f);
        using var builder = new SKTextBlobBuilder();
        var glyph = font.GetGlyph('A');

        builder.AddHorizontalRun(new[] { glyph }, font, ReadOnlySpan<float>.Empty, 7f);
        builder.AddPositionedRun(new[] { glyph }, font, ReadOnlySpan<SKPoint>.Empty);
        builder.AddRotationScaleRun(
            new[] { glyph },
            font,
            ReadOnlySpan<SKRotationScaleMatrix>.Empty);
        using var blob = builder.Build();

        Assert.NotNull(blob);
        Assert.Equal(new SKPoint(0f, 7f), blob.Runs[0].GlyphPositions[0]);
        Assert.Equal(SKPoint.Empty, blob.Runs[1].GlyphPositions[0]);
        Assert.Equal(SKRotationScaleMatrix.Empty, blob.Runs[2].RotationScaleMatrices![0]);
    }

    [Fact]
    public void PathPositionedBuilderRejectsMismatchedPathInputs()
    {
        using var builder = new SKTextBlobBuilder();
        using var font = new SKFont(SKTypeface.Default, 16f);
        using var path = new SKPath();
        path.MoveTo(0f, 0f);
        path.LineTo(100f, 0f);

        Assert.Throws<ArgumentException>(() => builder.AddPathPositionedRun(
            new ushort[] { 1 },
            font,
            ReadOnlySpan<float>.Empty,
            new SKPoint[] { SKPoint.Empty },
            path));
    }

    private static MethodInfo? GetBuilderMethod(string name, params Type[] parameterTypes) =>
        typeof(SKTextBlobBuilder).GetMethod(name, parameterTypes);

    private static void AssertParameterNames(MethodBase? method, params string[] expected)
    {
        Assert.NotNull(method);
        Assert.Equal(expected, method!.GetParameters().Select(static parameter => parameter.Name));
    }
}
