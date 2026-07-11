using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class PathOperationRenderTests
{
    [Fact]
    public void ChainedUnionAndIntersectionCloseEvenOddHoleContours()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(200, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var circle2 = new SKPath();
        circle2.AddCircle(150f, 150f, 50f);
        using var ring = new SKPath { FillType = SKPathFillType.EvenOdd };
        ring.AddRect(new SKRect(10f, 10f, 110f, 110f), SKPathDirection.Clockwise);
        ring.AddRect(new SKRect(50f, 50f, 90f, 90f), SKPathDirection.Clockwise);
        using var union = circle2.Op(ring, SKPathOp.Union);
        using var circle1 = new SKPath();
        circle1.AddCircle(100f, 100f, 50f);
        using var intersection = union.Op(circle1, SKPathOp.Intersect);
        using var paint = new SKPaint { Color = SKColors.Blue };

        surface.Canvas.Clear(SKColors.Red);
        surface.Canvas.DrawPath(intersection, paint);
        surface.Flush();

        Assert.Equal(SKPathFillType.EvenOdd, intersection.FillType);
        Assert.All(intersection.Geometry.Figures, figure => Assert.True(figure.IsClosed));
        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertPixel(pixels, width: 200, x: 100, y: 60, red: 0, green: 0, blue: 255);
        AssertPixel(pixels, width: 200, x: 80, y: 80, red: 255, green: 0, blue: 0);
        AssertPixel(pixels, width: 200, x: 120, y: 120, red: 0, green: 0, blue: 255);
    }

    [Fact]
    public void UnionWithEvenOddSecondOperandKeepsSameWindingHole()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var island = new SKPath();
        island.AddCircle(54f, 24f, 7f);
        using var donut = new SKPath { FillType = SKPathFillType.EvenOdd };
        donut.AddRect(new SKRect(4f, 4f, 44f, 44f), SKPathDirection.Clockwise);
        donut.AddRect(new SKRect(14f, 14f, 34f, 34f), SKPathDirection.Clockwise);
        using var union = island.Op(donut, SKPathOp.Union);
        using var paint = new SKPaint { Color = SKColors.Blue };

        surface.Canvas.Clear(SKColors.Red);
        surface.Canvas.DrawPath(union, paint);
        surface.Flush();

        Assert.Equal(SKPathFillType.EvenOdd, union.FillType);
        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        AssertPixel(pixels, width: 64, x: 8, y: 24, red: 0, green: 0, blue: 255);
        AssertPixel(pixels, width: 64, x: 24, y: 24, red: 255, green: 0, blue: 0);
        AssertPixel(pixels, width: 64, x: 54, y: 24, red: 0, green: 0, blue: 255);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DifferenceWithCoincidentEdgesRendersOneSidedBorder(bool borderOnLeft)
    {
        var window = HeadlessWindow.Shared;
        window.Resize(64, 40);
        window.Content = new OneSidedBorderVisual(borderOnLeft);

        try
        {
            window.Render();
            var pixels = window.ReadPixels();
            var center = ReadPixel(pixels, window.Width, x: 27, y: 20);
            var border = ReadPixel(pixels, window.Width, x: borderOnLeft ? 10 : 43, y: 20);

            AssertWhite(center);
            Assert.True(border.R <= 120 && border.G <= 120 && border.B <= 120,
                $"Expected the one-sided border to be dark, found {border}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    private static RgbaPixel ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        return new RgbaPixel(pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]);
    }

    private static void AssertWhite(RgbaPixel pixel)
    {
        Assert.True(pixel.R >= 245 && pixel.G >= 245 && pixel.B >= 245,
            $"Expected the excluded center to remain white, found {pixel}.");
        Assert.Equal(255, pixel.A);
    }

    private static void AssertPixel(
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

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class OneSidedBorderVisual : FrameworkElement
    {
        private readonly PathGeometry _border;

        public OneSidedBorderVisual(bool borderOnLeft)
        {
            Width = 64f;
            Height = 40f;
            _border = new PathGeometry
            {
                IsCombined = true,
                PathA = CreateRectangleWithDegenerateCornerArc(10f, 8f, 34f, 24f),
                PathB = CreateRectangleWithDegenerateCornerArc(borderOnLeft ? 11f : 10f, 8f, 33f, 24f),
                Op = 0
            };
        }

        private static PathGeometry CreateRectangleWithDegenerateCornerArc(float x, float y, float width, float height)
        {
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(x, y), isClosed: true);
            figure.Segments.Add(new LineSegment(new Vector2(x + width, y)));
            figure.Segments.Add(new ArcSegment(
                new Vector2(x + width, y),
                Vector2.Zero,
                rotationAngle: 0f,
                isLargeArc: false,
                SweepDirection.Clockwise));
            figure.Segments.Add(new LineSegment(new Vector2(x + width, y + height)));
            figure.Segments.Add(new LineSegment(new Vector2(x, y + height)));
            figure.Segments.Add(new LineSegment(new Vector2(x, y)));
            path.Figures.Add(figure);
            return path;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                pen: null,
                new Rect(0f, 0f, 64f, 40f));
            context.DrawPath(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.6f)),
                pen: null,
                _border);
        }
    }
}
