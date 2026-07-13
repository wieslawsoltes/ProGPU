using System.Reflection;
using ProGPU.Scene;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkTextPathPositioningCompatibilityTests
{
    [Fact]
    public void CreatePathPositionedPlacesGlyphOriginsAlongPathTangent()
    {
        using var font = new SKFont(SKTypeface.Default, 16f);
        using var path = new SKPath();
        path.MoveTo(10f, 20f);
        path.LineTo(210f, 20f);
        var glyphs = font.GetGlyphs("AB");
        var offsets = font.GetGlyphPositions(glyphs);

        using var blob = SKTextBlob.CreatePathPositioned("AB", font, path);

        Assert.NotNull(blob);
        var run = Assert.Single(blob!.Runs);
        var matrices = Assert.IsType<SKRotationScaleMatrix[]>(run.RotationScaleMatrices);
        Assert.Equal(glyphs, run.GlyphIndices);
        Assert.Equal(2, matrices.Length);
        for (var index = 0; index < matrices.Length; index++)
        {
            AssertNear(1f, matrices[index].SCos);
            AssertNear(0f, matrices[index].SSin);
            AssertNear(10f + offsets[index].X, matrices[index].TX);
            AssertNear(20f + offsets[index].Y, matrices[index].TY);
        }
    }

    [Fact]
    public void CreatePathPositionedRotatesGlyphOriginsWithVerticalPathTangent()
    {
        using var font = new SKFont(SKTypeface.Default, 16f);
        using var path = new SKPath();
        path.MoveTo(10f, 20f);
        path.LineTo(10f, 220f);
        var glyph = Assert.Single(font.GetGlyphs("A"));

        using var blob = SKTextBlob.CreatePathPositioned("A", font, path);

        Assert.NotNull(blob);
        var run = Assert.Single(blob!.Runs);
        var matrix = Assert.Single(Assert.IsType<SKRotationScaleMatrix[]>(run.RotationScaleMatrices));
        Assert.Equal(glyph, Assert.Single(run.GlyphIndices));
        AssertNear(0f, matrix.SCos);
        AssertNear(1f, matrix.SSin);
        AssertNear(10f, matrix.TX);
        AssertNear(20f, matrix.TY);
    }

    [Fact]
    public void CreatePathPositionedAppliesAlignmentAndClipsGlyphCentersOutsidePath()
    {
        using var font = new SKFont(SKTypeface.Default, 16f);
        using var path = new SKPath();
        path.MoveTo(0f, 0f);
        path.LineTo(100f, 0f);
        var glyphs = font.GetGlyphs("AB");
        var widths = font.GetGlyphWidths(glyphs);
        var offsets = font.GetGlyphPositions(glyphs);
        var textWidth = offsets[^1].X + widths[^1];

        using var centered = SKTextBlob.CreatePathPositioned(
            "AB",
            font,
            path,
            SKTextAlign.Center);
        var centeredMatrices = Assert.IsType<SKRotationScaleMatrix[]>(
            Assert.Single(centered!.Runs).RotationScaleMatrices);
        AssertNear((100f - textWidth) * 0.5f, centeredMatrices[0].TX);

        using var outside = SKTextBlob.CreatePathPositioned(
            "AB",
            font,
            path,
            SKTextAlign.Left,
            new SKPoint(200f, 0f));
        Assert.Null(outside);
    }

    [Fact]
    public void DrawTextOnPathSelectsWarpedOrPositionedRetainedPipeline()
    {
        using var font = new SKFont(SKTypeface.Default, 16f);
        using var paint = new SKPaint { Color = SKColors.Red };
        using var path = new SKPath();
        path.MoveTo(0f, 20f);
        path.LineTo(200f, 20f);

        var warpedContext = new DrawingContext();
        using var warpedCanvas = new SKCanvas(warpedContext, 200f, 40f);
        warpedCanvas.DrawTextOnPath(
            "AB",
            path,
            SKPoint.Empty,
            warpGlyphs: true,
            SKTextAlign.Left,
            font,
            paint);
        Assert.Contains(
            warpedContext.Commands,
            command => command.Type == RenderCommandType.DrawPath);
        Assert.DoesNotContain(
            warpedContext.Commands,
            command => command.Type == RenderCommandType.DrawGlyphRun);

        var positionedContext = new DrawingContext();
        using var positionedCanvas = new SKCanvas(positionedContext, 200f, 40f);
        positionedCanvas.DrawTextOnPath(
            "AB",
            path,
            SKPoint.Empty,
            warpGlyphs: false,
            SKTextAlign.Left,
            font,
            paint);
        Assert.Equal(
            2,
            positionedContext.Commands.Count(command => command.Type == RenderCommandType.DrawGlyphRun));
        Assert.DoesNotContain(
            positionedContext.Commands,
            command => command.Type == RenderCommandType.DrawPath);
    }

    [Fact]
    public void TextFamiliesExposeNativeOverloadCounts()
    {
        var canvasMethods = typeof(SKCanvas).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(7, canvasMethods.Count(method => method.Name == nameof(SKCanvas.DrawText)));
        Assert.Equal(9, canvasMethods.Count(method => method.Name == nameof(SKCanvas.DrawTextOnPath)));

        var pathPositioned = typeof(SKTextBlob)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(SKTextBlob.CreatePathPositioned))
            .ToArray();
        Assert.Equal(4, pathPositioned.Length);
        Assert.All(pathPositioned, method =>
        {
            var parameters = method.GetParameters();
            Assert.True(parameters[^2].HasDefaultValue);
            Assert.True(parameters[^1].HasDefaultValue);
        });
    }

    [Fact]
    public void PathPositionedBuilderRejectsMismatchedInputs()
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
        Assert.Throws<ArgumentException>(() => builder.AddRotationScaleRun(
            new ushort[] { 1 },
            font,
            ReadOnlySpan<SKRotationScaleMatrix>.Empty));
    }

    private static void AssertNear(float expected, float actual) =>
        Assert.InRange(actual, expected - 0.001f, expected + 0.001f);
}
