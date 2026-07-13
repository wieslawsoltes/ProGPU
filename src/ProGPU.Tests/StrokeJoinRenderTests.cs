using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class StrokeJoinRenderTests
{
    [Fact]
    public void ClosedRectangleStrokeRendersMiterCornerTriangles()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(64, 64);
        window.Content = new RectangleStrokeVisual();

        try
        {
            window.Render();

            var joinVertices = window.Compositor.VectorVertices
                .Where(vertex => DecodeShapeType(vertex.ShapeType) == 13)
                .ToArray();
            Assert.Equal(32, joinVertices.Length);
            Assert.All(joinVertices, vertex => Assert.Equal(3f, vertex.CornerRadius));
            Assert.Equal(16, joinVertices.Count(vertex => vertex.StrokeThickness == 4f));
            Assert.Equal(16, joinVertices.Count(vertex => vertex.StrokeThickness == 0f));
            var pixels = window.ReadPixels();
            AssertOpaqueRed(pixels, window.Width, 52, 8);
            AssertOpaqueRed(pixels, window.Width, 53, 9);
            AssertRedCoverage(pixels, window.Width, 52, 9);
            AssertRedCoverage(pixels, window.Width, 53, 7);
            AssertRedCoverage(pixels, window.Width, 52, 51);
            AssertRedCoverage(pixels, window.Width, 53, 53);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void TransformedPictureStrokeRendersMiterCornerTriangles()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(256, 256);
        using var visual = new PictureRectangleStrokeVisual();
        window.Content = visual;

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            AssertRed(pixels, window.Width, 212, 28);
            AssertRed(pixels, window.Width, 212, 212);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void OffscreenSurfaceStrokeRendersMiterCornerTriangles()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var path = new SKPath();
        path.AddRect(new SKRect(10f, 10f, 50f, 50f));
        using var paint = new SKPaint
        {
            Color = SKColors.Red,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 8f,
            StrokeJoin = SKStrokeJoin.Miter,
            StrokeMiter = 10f
        };

        surface.Canvas.DrawPath(path, paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertOpaqueRed(pixels, 64, 52, 8);
        AssertOpaqueRed(pixels, 64, 53, 9);
        AssertRedCoverage(pixels, 64, 52, 9);
        AssertRedCoverage(pixels, 64, 53, 7);
        AssertRedCoverage(pixels, 64, 52, 51);
        AssertRedCoverage(pixels, 64, 53, 53);
    }

    [Fact]
    public void MiterJoinSharedEdgesDoNotDoubleBlendTransparentStroke()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var path = new SKPath();
        path.AddRect(new SKRect(10f, 10f, 52f, 52f));
        using var paint = new SKPaint
        {
            Color = new SKColor(255, 0, 0, 128),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4f,
            StrokeJoin = SKStrokeJoin.Miter,
            IsAntialias = true
        };

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawPath(path, paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertHalfAlphaRed(pixels, 64, 52, 8);
        AssertHalfAlphaRed(pixels, 64, 53, 9);
    }

    [Fact]
    public void RoundJoinSharedEdgesDoNotLeaveTransparentSeams()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(128, 96, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var path = new SKPath();
        path.AddRect(new SKRect(20f, 20f, 108f, 60f));
        using var paint = new SKPaint
        {
            Color = SKColors.Blue,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 20f,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawPath(path, paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertColorNear(pixels, 128, 64, 40, 255, 255, 255);
        for (var offset = 0; offset < 7; offset++)
        {
            AssertOpaqueBlue(pixels, 128, 19 - offset, 60 + offset);
        }
    }

    [Fact]
    public void PictureReplayKeepsStrokeOnlyPathInteriorUnfilled()
    {
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 128f, 96f));
        using var path = new SKPath();
        path.AddRect(new SKRect(20f, 20f, 108f, 60f));
        using var paint = new SKPaint
        {
            Color = SKColors.Blue,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 20f,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        recordingCanvas.DrawPath(path, paint);
        using var picture = recorder.EndRecording();
        using var surface = SKSurface.Create(
            new SKImageInfo(128, 96, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawPicture(picture);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertColorNear(pixels, 128, 64, 40, 255, 255, 255);
        AssertOpaqueBlue(pixels, 128, 64, 20);
    }

    [Fact]
    public void RoundCapTrianglesOnlyAntialiasCurvedBoundary()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(64, 64);
        window.Content = new RoundCapStrokeVisual();

        try
        {
            window.Render();

            var capVertices = window.Compositor.VectorVertices
                .Where(vertex => DecodeShapeType(vertex.ShapeType) == 13)
                .ToArray();
            Assert.Equal(64, capVertices.Length);
            Assert.All(capVertices, vertex => Assert.Equal(2f, vertex.CornerRadius));
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void ConcaveClosedGradientStrokeLeavesGlyphInteriorTransparent()
    {
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 64f, 96f));
        using var path = new SKPath();
        path.MoveTo(4f, 70f);
        path.LineTo(4f, 0f);
        path.LineTo(51f, 0f);
        path.LineTo(51f, 38.3f);
        path.LineTo(29.5f, 38.3f);
        path.LineTo(29.5f, 25.3f);
        path.LineTo(36f, 25.3f);
        path.LineTo(36f, 15f);
        path.LineTo(19f, 15f);
        path.LineTo(19f, 55f);
        path.LineTo(51f, 55f);
        path.LineTo(51f, 70f);
        path.Close();
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0f, 0f),
            new SKPoint(64f, 0f),
            new[] { SKColors.Blue, SKColors.Lime },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint
        {
            Shader = shader,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            StrokeJoin = SKStrokeJoin.Miter,
            StrokeMiter = 4f,
            IsAntialias = true
        };

        recordingCanvas.Scale(1f, 1.25f);
        recordingCanvas.DrawPath(path, paint);
        using var picture = recorder.EndRecording();
        using var surface = SKSurface.Create(
            new SKImageInfo(64, 96, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawPicture(picture);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertTransparent(pixels, 64, 10, 40);
        AssertTransparent(pixels, 64, 45, 25);
        var partialAlphaCount = Enumerable.Range(0, pixels.Length / 4)
            .Count(index => pixels[index * 4 + 3] is > 0 and < 255);
        Assert.True(
            partialAlphaCount >= 300,
            $"Expected broad antialiased gradient-stroke coverage, but found {partialAlphaCount} partial pixels.");
    }

    [Fact]
    public void PictureShaderPreservesRecordedMiterCorners()
    {
        const float scale = 400f / 56f;
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 400f, 400f));
        var matrix = new SKMatrix
        {
            ScaleX = scale,
            ScaleY = scale,
            TransX = -22f * scale,
            TransY = -22f * scale,
            Persp2 = 1f
        };
        recordingCanvas.SetMatrix(in matrix);
        using var path = new SKPath();
        path.AddRect(new SKRect(50f, 25f, 75f, 50f));
        path.AddRect(new SKRect(25f, 50f, 50f, 75f));
        using var strokeShader = SKShader.CreateLinearGradient(
            new SKPoint(22f, 22f),
            new SKPoint(78f, 78f),
            new[] { SKColors.Red, SKColors.Red },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        using var stroke = new SKPaint
        {
            Color = SKColors.White,
            Shader = strokeShader,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5f,
            StrokeJoin = SKStrokeJoin.Miter,
            StrokeMiter = 10f
        };
        using var pathFill = new SKPaint { Color = new SKColor(255, 255, 0, 255) };
        recordingCanvas.DrawPath(path, pathFill);
        recordingCanvas.DrawPath(path, stroke);
        using var picture = recorder.EndRecording();
        using var shader = picture.ToShader(
            SKShaderTileMode.Decal,
            SKShaderTileMode.Decal,
            SKMatrix.Identity,
            new SKRect(0f, 0f, 400f, 400f));
        using var fill = new SKPaint { Shader = shader };
        using var surface = SKSurface.Create(
            new SKImageInfo(420, 420, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Translate(10f, 10f);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 400f, 400f), fill);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertRed(pixels, 420, 390, 20);
        AssertRed(pixels, 420, 400, 20);
        AssertRed(pixels, 420, 20, 390);
        AssertRed(pixels, 420, 20, 400);
        AssertRed(pixels, 420, 206, 206);
    }

    [Fact]
    public void PictureShaderStrokePreservesMiterCorners()
    {
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 2f, 2f));
        using (var tilePaint = new SKPaint { Color = SKColors.Red })
        {
            recordingCanvas.DrawRect(new SKRect(0f, 0f, 2f, 2f), tilePaint);
        }

        using var picture = recorder.EndRecording();
        using var shader = picture.ToShader(
            SKShaderTileMode.Repeat,
            SKShaderTileMode.Repeat,
            SKMatrix.Identity,
            new SKRect(0f, 0f, 2f, 2f));
        using var stroke = new SKPaint
        {
            Shader = shader,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 20f,
            StrokeJoin = SKStrokeJoin.Miter,
            StrokeMiter = 4f
        };
        using var path = new SKPath();
        path.AddRect(new SKRect(30f, 30f, 66f, 66f));
        using var surface = SKSurface.Create(
            new SKImageInfo(96, 96, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawPath(path, stroke);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertRed(pixels, 96, 21, 21);
        AssertRed(pixels, 96, 74, 74);
        AssertTransparent(pixels, 96, 19, 19);
    }

    [Fact]
    public void PictureShaderRasterizesSubcommandsBeforeTiling()
    {
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 4f, 5f));
        using (var roundRect = new SKRoundRect(new SKRect(0f, 0f, 4f, 5f), 0f, 0f))
        {
            recordingCanvas.ClipRoundRect(roundRect, antialias: true);
        }

        using var background = new SKPaint
        {
            Color = new SKColor(245, 245, 245, 255),
            IsAntialias = true
        };
        using var red = new SKPaint { Color = SKColors.Red, IsAntialias = true };
        recordingCanvas.DrawRect(new SKRect(0f, 0f, 4f, 5f), background);
        DrawRectPath(recordingCanvas, new SKRect(2f, 0f, 3f, 1f), red);
        DrawRectPath(recordingCanvas, new SKRect(0f, 2f, 1f, 3f), red);
        DrawRectPath(recordingCanvas, new SKRect(2f, 4f, 3f, 5f), red);
        using var picture = recorder.EndRecording();
        using var shader = picture.ToShader(
            SKShaderTileMode.Repeat,
            SKShaderTileMode.Repeat,
            SKMatrix.Identity,
            new SKRect(0f, 0f, 4f, 5f));
        using var fill = new SKPaint { Shader = shader, IsAntialias = true };
        using var surface = SKSurface.Create(
            new SKImageInfo(12, 8, SKColorType.Rgba8888, SKAlphaType.Premul));

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(1.5f, 1.5f);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 8f, 5f), fill);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertColorNear(pixels, 12, 3, 0, 255, 0, 0);
        AssertColorNear(pixels, 12, 4, 0, 250, 122, 122);
        AssertColorNear(pixels, 12, 3, 1, 250, 122, 122);
        AssertColorNear(pixels, 12, 4, 1, 247, 183, 183);
        AssertColorNear(pixels, 12, 0, 3, 253, 62, 62);
        AssertColorNear(pixels, 12, 1, 3, 249, 153, 153);
        AssertColorNear(pixels, 12, 6, 3, 253, 62, 62);
        AssertColorNear(pixels, 12, 2, 3, 245, 245, 245);
    }

    private static void DrawRectPath(SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        using var path = new SKPath();
        path.AddRect(rect);
        canvas.DrawPath(path, paint);
    }

    private static void AssertColorNear(
        byte[] pixels,
        int width,
        int x,
        int y,
        byte red,
        byte green,
        byte blue)
    {
        var offset = (y * width + x) * 4;
        Assert.InRange(pixels[offset], Math.Max(0, red - 3), Math.Min(255, red + 3));
        Assert.InRange(pixels[offset + 1], Math.Max(0, green - 3), Math.Min(255, green + 3));
        Assert.InRange(pixels[offset + 2], Math.Max(0, blue - 3), Math.Min(255, blue + 3));
        Assert.InRange(pixels[offset + 3], 252, 255);
    }

    private static void AssertRed(byte[] pixels, uint width, int x, int y)
    {
        var offset = (y * (int)width + x) * 4;
        Assert.InRange(pixels[offset], 200, 255);
        Assert.InRange(pixels[offset + 1], 0, 20);
        Assert.InRange(pixels[offset + 2], 0, 20);
        Assert.InRange(pixels[offset + 3], 200, 255);
    }

    private static void AssertRedCoverage(byte[] pixels, uint width, int x, int y)
    {
        var offset = (y * (int)width + x) * 4;
        Assert.InRange(pixels[offset], 100, 255);
        Assert.InRange(pixels[offset + 1], 0, 20);
        Assert.InRange(pixels[offset + 2], 0, 20);
        Assert.InRange(pixels[offset + 3], 100, 255);
    }

    private static void AssertOpaqueRed(byte[] pixels, uint width, int x, int y)
    {
        var offset = (y * (int)width + x) * 4;
        Assert.InRange(pixels[offset], 245, 255);
        Assert.InRange(pixels[offset + 1], 0, 5);
        Assert.InRange(pixels[offset + 2], 0, 5);
        Assert.InRange(pixels[offset + 3], 245, 255);
    }

    private static void AssertOpaqueBlue(byte[] pixels, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        Assert.InRange(pixels[offset], 0, 5);
        Assert.InRange(pixels[offset + 1], 0, 5);
        Assert.InRange(pixels[offset + 2], 245, 255);
        Assert.InRange(pixels[offset + 3], 245, 255);
    }

    private static void AssertHalfAlphaRed(byte[] pixels, uint width, int x, int y)
    {
        var offset = (y * (int)width + x) * 4;
        Assert.InRange(pixels[offset], 120, 136);
        Assert.InRange(pixels[offset + 1], 0, 3);
        Assert.InRange(pixels[offset + 2], 0, 3);
        Assert.InRange(pixels[offset + 3], 120, 136);
    }

    private static void AssertTransparent(byte[] pixels, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        Assert.InRange(pixels[offset + 3], 0, 2);
    }

    private static int DecodeShapeType(float shapeType)
    {
        if (shapeType >= 1000f)
        {
            shapeType -= 1000f;
        }

        if (shapeType >= 195f)
        {
            shapeType -= 200f;
        }
        else if (shapeType >= 95f)
        {
            shapeType -= 100f;
        }

        return (int)MathF.Round(shapeType);
    }

    private sealed class RectangleStrokeVisual : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(10f, 10f), isClosed: true);
            figure.Segments.Add(new LineSegment(new Vector2(50f, 10f)));
            figure.Segments.Add(new LineSegment(new Vector2(50f, 50f)));
            figure.Segments.Add(new LineSegment(new Vector2(10f, 50f)));
            path.Figures.Add(figure);

            context.DrawPath(
                null,
                new Pen(new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)), 8f),
                path);
        }
    }

    private sealed class RoundCapStrokeVisual : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(16f, 32f));
            figure.Segments.Add(new LineSegment(new Vector2(48f, 32f)));
            path.Figures.Add(figure);

            context.DrawPath(
                null,
                new Pen(
                    new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                    thickness: 16f,
                    startLineCap: PenLineCap.Round,
                    endLineCap: PenLineCap.Round),
                path);
        }
    }

    private sealed class PictureRectangleStrokeVisual : FrameworkElement, IDisposable
    {
        private readonly GpuPicture _picture;

        public PictureRectangleStrokeVisual()
        {
            var recorder = new GpuPictureRecorder();
            var context = recorder.BeginRecording(new Rect(0f, 0f, 64f, 64f));
            var red = new Vector4(1f, 0f, 0f, 1f);
            context.DrawPath(
                null,
                new Pen(
                    new LinearGradientBrush(
                        Vector2.Zero,
                        new Vector2(64f, 64f),
                        new[] { new GradientStop(red, 0f), new GradientStop(red, 1f) }),
                    8f),
                CreateRectanglePath());
            _picture = recorder.EndRecording();
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawPictureTransformed(_picture, Matrix4x4.CreateScale(4f, 4f, 1f));
        }

        public void Dispose()
        {
            _picture.Dispose();
        }

        private static PathGeometry CreateRectanglePath()
        {
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(10f, 10f), isClosed: true);
            figure.Segments.Add(new LineSegment(new Vector2(50f, 10f)));
            figure.Segments.Add(new LineSegment(new Vector2(50f, 50f)));
            figure.Segments.Add(new LineSegment(new Vector2(10f, 50f)));
            path.Figures.Add(figure);
            return path;
        }
    }
}
