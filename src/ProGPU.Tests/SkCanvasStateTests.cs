using System;
using System.Numerics;
using ProGPU.Scene;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCanvasStateTests
{
    [Fact]
    public void SaveLayerReturnsRestoreCountAndPushesRelativeOpacity()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var halfAlpha = new SKPaint { Color = new SKColor(0, 0, 0, 128) };

        var outerRestoreCount = canvas.SaveLayer(halfAlpha);
        var innerRestoreCount = canvas.SaveLayer(halfAlpha);

        Assert.Equal(0, outerRestoreCount);
        Assert.Equal(1, innerRestoreCount);

        Assert.Equal(2, context.Commands.Count);
        Assert.Equal(RenderCommandType.PushOpacity, context.Commands[0].Type);
        Assert.Equal(RenderCommandType.PushOpacity, context.Commands[1].Type);
        AssertNear(128f / 255f, context.Commands[0].FontSize);
        AssertNear(128f / 255f, context.Commands[1].FontSize);

        canvas.RestoreToCount(innerRestoreCount);

        Assert.Equal(3, context.Commands.Count);
        Assert.Equal(RenderCommandType.PopOpacity, context.Commands[2].Type);

        canvas.RestoreToCount(outerRestoreCount);

        Assert.Equal(4, context.Commands.Count);
        Assert.Equal(RenderCommandType.PopOpacity, context.Commands[3].Type);
    }

    [Fact]
    public void ClipRectRecordsCurrentCanvasMatrix()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);

        canvas.Translate(50f, 10f);
        canvas.ClipRect(new SKRect(2f, 4f, 22f, 14f));

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.PushClip, command.Type);
        Assert.Equal(new Rect(2f, 4f, 20f, 10f), command.Rect);
        AssertMatrixNear(Matrix4x4.CreateTranslation(50f, 10f, 0f), command.Transform);
    }

    [Fact]
    public void ClipPathRecordsCurrentCanvasMatrix()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var path = new SKPath();
        path.AddRect(new SKRect(0f, 0f, 20f, 20f));
        var matrix = new SKMatrix
        {
            ScaleX = 1.25f,
            SkewX = 0.5f,
            TransX = 7f,
            SkewY = 0.25f,
            ScaleY = 2f,
            TransY = 11f,
            Persp2 = 1f
        };

        canvas.SetMatrix(matrix);
        canvas.ClipPath(path);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.PushGeometryClip, command.Type);
        Assert.Same(path.Geometry, command.Path);
        AssertMatrixNear(matrix.ToMatrix4x4(), command.Transform);
    }

    [Fact]
    public void DrawImagePushesPaintAlphaOpacity()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var bitmap = new SKBitmap(4, 4);
        using var image = SKImage.FromBitmap(bitmap);
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, 128) };

        canvas.DrawImage(
            image,
            new SKRect(1f, 2f, 3f, 4f),
            new SKRect(10f, 20f, 30f, 40f),
            paint);

        Assert.Collection(
            context.Commands,
            push =>
            {
                Assert.Equal(RenderCommandType.PushOpacity, push.Type);
                AssertNear(128f / 255f, push.FontSize);
            },
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawTexture, draw.Type);
                Assert.Same(image.Texture, draw.Texture);
                Assert.Equal(new Rect(10f, 20f, 20f, 20f), draw.Rect);
                Assert.Equal(new Rect(1f, 2f, 2f, 2f), draw.SrcRect);
            },
            pop => Assert.Equal(RenderCommandType.PopOpacity, pop.Type));
    }

    [Fact]
    public void TranslateAppliesFullLinearMatrix()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        canvas.SetMatrix(new SKMatrix
        {
            ScaleX = 2f,
            SkewX = 3f,
            SkewY = 5f,
            ScaleY = 7f,
            TransX = 11f,
            TransY = 13f,
            Persp2 = 1f
        });

        canvas.Translate(17f, 19f);

        var matrix = canvas.TotalMatrix;
        AssertNear(11f + 17f * 2f + 19f * 3f, matrix.TransX);
        AssertNear(13f + 17f * 5f + 19f * 7f, matrix.TransY);
    }

    private static void AssertMatrixNear(Matrix4x4 expected, Matrix4x4 actual)
    {
        AssertNear(expected.M11, actual.M11);
        AssertNear(expected.M12, actual.M12);
        AssertNear(expected.M21, actual.M21);
        AssertNear(expected.M22, actual.M22);
        AssertNear(expected.M41, actual.M41);
        AssertNear(expected.M42, actual.M42);
    }

    private static void AssertNear(float expected, float actual)
    {
        Assert.InRange(MathF.Abs(expected - actual), 0f, 0.0001f);
    }
}
