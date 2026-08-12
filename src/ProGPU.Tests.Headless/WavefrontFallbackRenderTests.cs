using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public sealed class WavefrontFallbackRenderTests
{
    private const uint SurfaceSize = 64;

    [Fact]
    public void StrokeOnlyPathPreservesBorderAndDoesNotFillInterior()
    {
        using var window = CreateWavefrontWindow(new StrokeOnlyPathVisual());

        window.Render();

        byte[] pixels = window.ReadPixels();
        AssertRed(ReadPixel(pixels, x: 10, y: 32));
        AssertBlack(ReadPixel(pixels, x: 32, y: 32));
    }

    [Fact]
    public void FilledAndStrokedPathPreservesBothBrushColors()
    {
        using var window = CreateWavefrontWindow(new FilledAndStrokedPathVisual());

        window.Render();

        byte[] pixels = window.ReadPixels();
        AssertRed(ReadPixel(pixels, x: 12, y: 32));
        AssertGreen(ReadPixel(pixels, x: 32, y: 32));
    }

    [Fact]
    public void FilledPathFollowedByDirectRectanglePreservesDrawOrder()
    {
        using var window = CreateWavefrontWindow(new MixedOrderedVisual());

        window.Render();

        byte[] pixels = window.ReadPixels();
        AssertBlue(ReadPixel(pixels, x: 16, y: 32));
        AssertGreen(ReadPixel(pixels, x: 29, y: 32));
        AssertRed(ReadPixel(pixels, x: 36, y: 32));
    }

    [Fact]
    public void GeometryClipDoesNotEscapeItsMask()
    {
        using var window = CreateWavefrontWindow(new GeometryClipVisual());

        window.Render();

        byte[] pixels = window.ReadPixels();
        AssertBlue(ReadPixel(pixels, x: 32, y: 24));
        AssertBlack(ReadPixel(pixels, x: 8, y: 8));
        AssertBlack(ReadPixel(pixels, x: 20, y: 40));
    }

    [Fact]
    public void EvenOddPathPreservesItsHole()
    {
        using var window = CreateWavefrontWindow(new EvenOddPathVisual());

        window.Render();

        byte[] pixels = window.ReadPixels();
        AssertGreen(ReadPixel(pixels, x: 14, y: 32));
        AssertBlack(ReadPixel(pixels, x: 32, y: 32));
    }

    [Fact]
    public void PathOpacityIsAppliedByFallbackRenderer()
    {
        using var window = CreateWavefrontWindow(new TranslucentPathVisual());

        window.Render();

        RgbaPixel pixel = ReadPixel(window.ReadPixels(), x: 32, y: 32);
        Assert.InRange(pixel.Red, (byte)120, (byte)136);
        Assert.InRange(pixel.Green, (byte)0, (byte)3);
        Assert.InRange(pixel.Blue, (byte)0, (byte)3);
        Assert.InRange(pixel.Alpha, (byte)252, byte.MaxValue);
    }

    [Fact]
    public void MoreThanWavefrontCellCapacityDoesNotDropLaterPaths()
    {
        using var window = CreateWavefrontWindow(new WavefrontCapacityVisual());

        window.Render();

        AssertBlue(ReadPixel(window.ReadPixels(), x: 32, y: 32));
    }

    [Fact]
    public void NonConformalTransformUsesTheExactAtlasFallback()
    {
        using var atlasWindow = CreateWindow(
            new NonConformalPathVisual(),
            Compositor.VectorRenderingEngine.Atlas);
        using var wavefrontWindow = CreateWindow(
            new NonConformalPathVisual(),
            Compositor.VectorRenderingEngine.Wavefront);

        atlasWindow.Render();
        wavefrontWindow.Render();

        Assert.Equal(atlasWindow.ReadPixels(), wavefrontWindow.ReadPixels());
    }

    private static HeadlessWindow CreateWavefrontWindow(FrameworkElement content)
        => CreateWindow(content, Compositor.VectorRenderingEngine.Wavefront);

    private static HeadlessWindow CreateWindow(
        FrameworkElement content,
        Compositor.VectorRenderingEngine vectorEngine)
    {
        var window = new HeadlessWindow(
            SurfaceSize,
            SurfaceSize,
            renderFormat: TextureFormat.Bgra8Unorm)
        {
            Content = content
        };
        window.Compositor.ClearColor = new Vector4(0f, 0f, 0f, 1f);
        window.Compositor.VectorEngine = vectorEngine;
        return window;
    }

    private static PathGeometry CreateRectanglePath(
        float left,
        float top,
        float right,
        float bottom)
    {
        var figure = new PathFigure(new Vector2(left, top), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(right, top)));
        figure.Segments.Add(new LineSegment(new Vector2(right, bottom)));
        figure.Segments.Add(new LineSegment(new Vector2(left, bottom)));
        var path = new PathGeometry();
        path.Figures.Add(figure);
        return path;
    }

    private static RgbaPixel ReadPixel(byte[] pixels, int x, int y)
    {
        int offset = ((y * checked((int)SurfaceSize)) + x) * 4;
        return new RgbaPixel(
            pixels[offset],
            pixels[offset + 1],
            pixels[offset + 2],
            pixels[offset + 3]);
    }

    private static void AssertRed(RgbaPixel pixel)
    {
        Assert.InRange(pixel.Red, (byte)220, byte.MaxValue);
        Assert.InRange(pixel.Green, (byte)0, (byte)35);
        Assert.InRange(pixel.Blue, (byte)0, (byte)35);
        Assert.InRange(pixel.Alpha, (byte)245, byte.MaxValue);
    }

    private static void AssertGreen(RgbaPixel pixel)
    {
        Assert.InRange(pixel.Red, (byte)0, (byte)35);
        Assert.InRange(pixel.Green, (byte)220, byte.MaxValue);
        Assert.InRange(pixel.Blue, (byte)0, (byte)35);
        Assert.InRange(pixel.Alpha, (byte)245, byte.MaxValue);
    }

    private static void AssertBlue(RgbaPixel pixel)
    {
        Assert.InRange(pixel.Red, (byte)0, (byte)35);
        Assert.InRange(pixel.Green, (byte)0, (byte)35);
        Assert.InRange(pixel.Blue, (byte)220, byte.MaxValue);
        Assert.InRange(pixel.Alpha, (byte)245, byte.MaxValue);
    }

    private static void AssertBlack(RgbaPixel pixel)
    {
        Assert.InRange(pixel.Red, (byte)0, (byte)3);
        Assert.InRange(pixel.Green, (byte)0, (byte)3);
        Assert.InRange(pixel.Blue, (byte)0, (byte)3);
        Assert.InRange(pixel.Alpha, (byte)252, byte.MaxValue);
    }

    private readonly record struct RgbaPixel(byte Red, byte Green, byte Blue, byte Alpha);

    private sealed class StrokeOnlyPathVisual : FrameworkElement
    {
        private readonly PathGeometry _path = CreateRectanglePath(10f, 10f, 54f, 54f);
        private readonly Pen _pen = new(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            6f);

        public override void OnRender(DrawingContext context) =>
            context.DrawPath(null, _pen, _path);
    }

    private sealed class FilledAndStrokedPathVisual : FrameworkElement
    {
        private readonly PathGeometry _path = CreateRectanglePath(12f, 12f, 52f, 52f);
        private readonly SolidColorBrush _fill = new(new Vector4(0f, 1f, 0f, 1f));
        private readonly Pen _pen = new(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            6f);

        public override void OnRender(DrawingContext context) =>
            context.DrawPath(_fill, _pen, _path);
    }

    private sealed class MixedOrderedVisual : FrameworkElement
    {
        private readonly PathGeometry _path = CreateRectanglePath(8f, 8f, 50f, 56f);
        private readonly SolidColorBrush _blue = new(new Vector4(0f, 0f, 1f, 1f));
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));
        private readonly Pen _greenPen = new(
            new SolidColorBrush(new Vector4(0f, 1f, 0f, 1f)),
            4f);

        public override void OnRender(DrawingContext context)
        {
            context.DrawPath(_blue, null, _path);
            context.DrawRectangle(
                _red,
                _greenPen,
                new Rect(28f, 16f, 28f, 32f));
        }
    }

    private sealed class GeometryClipVisual : FrameworkElement
    {
        private readonly PathGeometry _clip;
        private readonly SolidColorBrush _blue = new(new Vector4(0f, 0f, 1f, 1f));

        public GeometryClipVisual()
        {
            var figure = new PathFigure(new Vector2(16f, 16f), isClosed: true);
            figure.Segments.Add(new LineSegment(new Vector2(48f, 16f)));
            figure.Segments.Add(new LineSegment(new Vector2(32f, 48f)));
            _clip = new PathGeometry();
            _clip.Figures.Add(figure);
        }

        public override void OnRender(DrawingContext context)
        {
            context.PushGeometryClip(_clip);
            context.DrawRectangle(_blue, null, new Rect(0f, 0f, 64f, 64f));
            context.PopGeometryClip();
        }
    }

    private sealed class EvenOddPathVisual : FrameworkElement
    {
        private readonly PathGeometry _path;
        private readonly SolidColorBrush _green = new(new Vector4(0f, 1f, 0f, 1f));

        public EvenOddPathVisual()
        {
            _path = new PathGeometry { FillRule = FillRule.EvenOdd };
            _path.Figures.Add(CreateRectanglePath(8f, 8f, 56f, 56f).Figures[0]);
            _path.Figures.Add(CreateRectanglePath(24f, 24f, 40f, 40f).Figures[0]);
        }

        public override void OnRender(DrawingContext context) =>
            context.DrawPath(_green, null, _path);
    }

    private sealed class TranslucentPathVisual : FrameworkElement
    {
        private readonly PathGeometry _path = CreateRectanglePath(12f, 12f, 52f, 52f);
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));

        public override void OnRender(DrawingContext context)
        {
            context.PushOpacity(0.5f);
            context.DrawPath(_red, null, _path);
            context.PopOpacity();
        }
    }

    private sealed class WavefrontCapacityVisual : FrameworkElement
    {
        private readonly PathGeometry _path = CreateRectanglePath(12f, 12f, 52f, 52f);
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));
        private readonly SolidColorBrush _blue = new(new Vector4(0f, 0f, 1f, 1f));

        public override void OnRender(DrawingContext context)
        {
            for (var index = 0; index < 64; index++)
            {
                context.DrawPath(_red, null, _path);
            }

            context.DrawPath(_blue, null, _path);
        }
    }

    private sealed class NonConformalPathVisual : FrameworkElement
    {
        private readonly PathGeometry _path = CreateRectanglePath(6f, 12f, 26f, 44f);
        private readonly SolidColorBrush _blue = new(new Vector4(0f, 0f, 1f, 1f));

        public override void OnRender(DrawingContext context) =>
            context.DrawPath(
                _blue,
                null,
                _path,
                Matrix4x4.CreateScale(2f, 1f, 1f));
    }
}
