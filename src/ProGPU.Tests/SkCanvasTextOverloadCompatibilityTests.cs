using System.Reflection;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCanvasTextOverloadCompatibilityTests
{
    [Fact]
    public void RepeatedTextBlobDrawsReuseConvertedGlyphPositions()
    {
        using var font = new SKFont(SKTypeface.Default, 16f);
        SKPoint[] positions = Enumerable.Range(0, 256)
            .Select(index => new SKPoint(index * 7f, (index & 3) * 0.125f))
            .ToArray();
        using var blob = SKTextBlob.CreatePositioned(
            new string('A', positions.Length),
            font,
            positions) ?? throw new InvalidOperationException();
        using var paint = new SKPaint { Color = SKColors.Black };
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 2048f, 64f);

        canvas.DrawText(blob, 2f, 20f, paint);
        Vector2[] first = Assert.Single(context.Commands).GlyphPositions!;
        context.Clear();
        canvas.DrawText(blob, 4f, 20f, paint);
        Vector2[] second = Assert.Single(context.Commands).GlyphPositions!;

        Assert.Same(first, second);
        Assert.Equal(positions.Length, second.Length);
        Assert.Equal(new Vector2(7f, 0.125f), second[1]);
    }

    [Fact]
    public void PointTextOverloadUsesCanonicalAlignedGlyphRun()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 200f, 80f));
        using var font = new SKFont { Size = 20f };
        using var paint = new SKPaint { Color = SKColors.Black };

        canvas.DrawText("GPU", new SKPoint(100f, 30f), SKTextAlign.Center, font, paint);
        using var picture = recorder.EndRecording();

        Assert.Contains(
            picture.Picture.Commands,
            command => command.Type == RenderCommandType.DrawGlyphRun);
    }

    [Fact]
    public void ShaderTextPathRetainsDeviceAwareVectorRasterizationMetadata()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 200f, 80f));
        using var font = new SKFont { Size = 32f };
        using var shader = SKShader.CreateColor(SKColors.Black);
        using var paint = new SKPaint
        {
            Shader = shader,
            IsAntialias = true
        };

        canvas.DrawText("GPU", 4f, 40f, SKTextAlign.Left, font, paint);
        using var picture = recorder.EndRecording();

        var command = Assert.Single(
            picture.Picture.Commands,
            command => command.Type == RenderCommandType.DrawPath);
        Assert.True(command.UseVectorGlyphRendering);
        Assert.Equal(32f, command.FontSize);
        Assert.Equal(PathAtlas.HighPrecisionCoverageSampleGrid, command.PathSampleGrid);
        Assert.False(command.HasFontTransform);
    }

    [Fact]
    public void WarpedTextPathRetainsTransformedFontCoverageMetadata()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 200f, 80f));
        using var path = new SKPath();
        path.MoveTo(0f, 40f);
        path.LineTo(180f, 40f);
        using var font = new SKFont(SKTypeface.Default, 18f, -1f, 0.2f);
        using var paint = new SKPaint { Color = SKColors.Black };

        canvas.DrawTextOnPath(
            "GPU",
            path,
            SKPoint.Empty,
            warpGlyphs: true,
            SKTextAlign.Left,
            font,
            paint);
        using var picture = recorder.EndRecording();

        var command = Assert.Single(
            picture.Picture.Commands,
            command => command.Type == RenderCommandType.DrawPath);
        Assert.True(command.UseVectorGlyphRendering);
        Assert.Equal(18f, command.FontSize);
        Assert.Equal(new System.Numerics.Vector2(-1f, 0.2f), command.FontTransform);
        Assert.True(command.HasFontTransform);
    }

    [Fact]
    public void WarpFlagSelectsOutlineOrRotationScaleGlyphPath()
    {
        using var path = new SKPath();
        path.MoveTo(0f, 30f);
        path.LineTo(180f, 30f);
        using var font = new SKFont { Size = 18f };
        using var paint = new SKPaint { Color = SKColors.Black };

        using var warpedRecorder = new SKPictureRecorder();
        var warpedCanvas = warpedRecorder.BeginRecording(new SKRect(0f, 0f, 200f, 80f));
        warpedCanvas.DrawTextOnPath(
            "Path",
            path,
            SKPoint.Empty,
            warpGlyphs: true,
            SKTextAlign.Left,
            font,
            paint);
        using var warped = warpedRecorder.EndRecording();
        Assert.Contains(warped.Picture.Commands, command => command.Type == RenderCommandType.DrawPath);

        using var positionedRecorder = new SKPictureRecorder();
        var positionedCanvas = positionedRecorder.BeginRecording(new SKRect(0f, 0f, 200f, 80f));
        positionedCanvas.DrawTextOnPath(
            "Path",
            path,
            SKPoint.Empty,
            warpGlyphs: false,
            SKTextAlign.Left,
            font,
            paint);
        using var positioned = positionedRecorder.EndRecording();
        Assert.DoesNotContain(positioned.Picture.Commands, command => command.Type == RenderCommandType.DrawPath);
        Assert.Contains(positioned.Picture.Commands, command => command.Type == RenderCommandType.DrawGlyphRun);
    }

    [Fact]
    public void CanonicalTextOnPathValidatesNativeArgumentOrder()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));
        using var path = new SKPath();
        using var font = new SKFont();
        using var paint = new SKPaint();

        Assert.Throws<ArgumentNullException>(() => canvas.DrawTextOnPath(
            null!, path, SKPoint.Empty, false, SKTextAlign.Left, font, paint));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawTextOnPath(
            string.Empty, null!, SKPoint.Empty, false, SKTextAlign.Left, font, paint));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawTextOnPath(
            string.Empty, path, SKPoint.Empty, false, SKTextAlign.Left, null!, paint));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawTextOnPath(
            string.Empty, path, SKPoint.Empty, false, SKTextAlign.Left, font, null!));
    }

    [Fact]
    public void LegacyTextOverloadsCarryNativeObsoleteContract()
    {
        var methods = typeof(SKCanvas).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var legacyText = methods.Where(method =>
            method.Name == nameof(SKCanvas.DrawText) &&
            method.GetParameters()[0].ParameterType == typeof(string) &&
            method.GetCustomAttribute<ObsoleteAttribute>() != null);
        var legacyPath = methods.Where(method =>
            method.Name == nameof(SKCanvas.DrawTextOnPath) &&
            method.GetCustomAttribute<ObsoleteAttribute>() != null);

        Assert.Equal(4, legacyText.Count());
        Assert.Equal(6, legacyPath.Count());
    }
}
