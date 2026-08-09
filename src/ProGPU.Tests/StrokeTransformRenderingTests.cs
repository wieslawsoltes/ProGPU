using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class StrokeTransformRenderingTests
{
    [Theory]
    [InlineData(StrokeKind.Line)]
    [InlineData(StrokeKind.Quadratic)]
    [InlineData(StrokeKind.Cubic)]
    [InlineData(StrokeKind.Polyline)]
    [InlineData(StrokeKind.Spline)]
    [InlineData(StrokeKind.Path)]
    [InlineData(StrokeKind.DashedLine)]
    [InlineData(StrokeKind.DashedQuadratic)]
    [InlineData(StrokeKind.DashedCubic)]
    [InlineData(StrokeKind.DashedPolyline)]
    [InlineData(StrokeKind.DashedSpline)]
    [InlineData(StrokeKind.PictureLine)]
    public void VisualScaleAppliesOnceAcrossEquivalentStrokeCommands(StrokeKind kind)
    {
        using var window = new HeadlessWindow(64, 64);
        using var visual = new StrokeVisual(kind, UniformScaleAboutCenter(0.5f));
        window.Content = visual;

        window.Render();

        var paintedRows = CountPaintedRows(window.ReadPixels(), x: 32);
        Assert.True(
            paintedRows is >= 4 and <= 8,
            $"{kind} painted {paintedRows} rows from {window.Compositor.VectorVertices.Count} vector vertices.");
    }

    [Theory]
    [InlineData(0.5f, 4, 8)]
    [InlineData(2f, 22, 26)]
    [InlineData(4f, 46, 50)]
    public void PictureStrokeScaleIsAppliedExactlyOnce(
        float scale,
        int minimumRows,
        int maximumRows)
    {
        using var window = new HeadlessWindow(64, 64);
        using var visual = new StrokeVisual(
            StrokeKind.PictureLine,
            UniformScaleAboutCenter(scale));
        window.Content = visual;

        window.Render();

        Assert.InRange(
            CountPaintedRows(window.ReadPixels(), x: 32),
            minimumRows,
            maximumRows);
    }

    [Fact]
    public void NonUniformScaleTransformsHorizontalStrokeOutlineInLocalSpace()
    {
        using var window = new HeadlessWindow(64, 64);
        using var visual = new StrokeVisual(
            StrokeKind.Line,
            ScaleAboutCenter(scaleX: 2f, scaleY: 0.5f));
        window.Content = visual;

        window.Render();

        Assert.InRange(CountPaintedRows(window.ReadPixels(), x: 32), 4, 8);
    }

    [Fact]
    public void NonUniformScaleTransformsVerticalStrokeOutlineInLocalSpace()
    {
        using var window = new HeadlessWindow(64, 64);
        using var visual = new VerticalStrokeVisual(
            ScaleAboutCenter(scaleX: 2f, scaleY: 0.5f));
        window.Content = visual;

        window.Render();

        Assert.InRange(CountPaintedColumns(window.ReadPixels(), y: 32), 22, 26);
    }

    [Theory]
    [InlineData(true, 12f)]
    [InlineData(false, 24f)]
    public void LocalAndLegacyRecordedWidthsComposeCommandAndVisualScaleOnce(
        bool isLocal,
        float recordedThickness)
    {
        using var window = new HeadlessWindow(64, 64);
        window.Content = new ProvenanceStrokeVisual(isLocal, recordedThickness);

        window.Render();

        var lineVertex = Assert.Single(
            window.Compositor.VectorVertices
                .Where(static vertex => MathF.Abs(vertex.ShapeType - 3f) < 0.01f)
                .Select(static vertex => vertex.StrokeThickness)
                .Distinct());
        Assert.Equal(12f, lineVertex, precision: 3);
    }

    [Fact]
    public void NonFiniteStrokeTransformDoesNotEmitDirectStrokeVertices()
    {
        using var window = new HeadlessWindow(64, 64);
        window.Content = new NonFiniteStrokeVisual();

        window.Render();

        Assert.DoesNotContain(
            window.Compositor.VectorVertices,
            static vertex => MathF.Abs(vertex.ShapeType - 3f) < 0.01f);
    }

    [Fact]
    public void CollapsedStrokeTransformDoesNotEmitDirectStrokeVertices()
    {
        using var window = new HeadlessWindow(64, 64);
        window.Content = new CollapsedStrokeVisual();

        window.Render();

        Assert.DoesNotContain(
            window.Compositor.VectorVertices,
            static vertex => MathF.Abs(vertex.ShapeType - 3f) < 0.01f);
    }

    [Fact]
    public void InvalidStrokeWidthDoesNotSuppressIndependentFill()
    {
        using var window = new HeadlessWindow(64, 64);
        window.Content = new InvalidStrokeWidthVisual();

        window.Render();

        var rectangleVertices = window.Compositor.VectorVertices
            .Where(static vertex => MathF.Abs(vertex.ShapeType) < 0.01f)
            .ToArray();
        Assert.Equal(4, rectangleVertices.Length);
        Assert.All(
            rectangleVertices,
            static vertex => Assert.Equal(0f, vertex.StrokeThickness));
        var pixels = window.ReadPixels();
        Assert.True(pixels[(32 * 64 + 32) * 4 + 2] > 128);
    }

    private static Matrix4x4 UniformScaleAboutCenter(float scale) =>
        ScaleAboutCenter(scale, scale);

    private static Matrix4x4 ScaleAboutCenter(float scaleX, float scaleY) =>
        Matrix4x4.CreateTranslation(-32f, -32f, 0f) *
        Matrix4x4.CreateScale(scaleX, scaleY, 1f) *
        Matrix4x4.CreateTranslation(32f, 32f, 0f);

    private static Pen CreatePen(bool dashed = false) => new(
        new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
        12f,
        dashArray: dashed ? [1000d, 1d] : null);

    private static PathGeometry CreateHorizontalPath()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(0f, 32f));
        figure.Segments.Add(new LineSegment(new Vector2(64f, 32f)));
        path.Figures.Add(figure);
        return path;
    }

    private static int CountPaintedRows(byte[] pixels, int x)
    {
        var count = 0;
        for (var y = 0; y < 64; y++)
        {
            if (pixels[(y * 64 + x) * 4 + 2] > 128)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountPaintedColumns(byte[] pixels, int y)
    {
        var count = 0;
        for (var x = 0; x < 64; x++)
        {
            if (pixels[(y * 64 + x) * 4 + 2] > 128)
            {
                count++;
            }
        }

        return count;
    }

    private sealed class StrokeVisual : FrameworkElement, IDisposable
    {
        private readonly StrokeKind _kind;
        private readonly GpuPicture? _picture;

        public StrokeVisual(StrokeKind kind, Matrix4x4 transform)
        {
            _kind = kind;
            Width = 64f;
            Height = 64f;
            Transform = transform;
            if (kind == StrokeKind.PictureLine)
            {
                _picture = new GpuPicture(
                    [
                        new RenderCommand
                        {
                            Type = RenderCommandType.DrawLine,
                            Pen = CreatePen(),
                            Position = new Vector2(0f, 32f),
                            Position2 = new Vector2(64f, 32f)
                        }
                    ],
                    [],
                    [],
                    [],
                    []);
            }
        }

        public override void OnRender(DrawingContext context)
        {
            var pen = CreatePen(_kind is
                StrokeKind.DashedLine or
                StrokeKind.DashedQuadratic or
                StrokeKind.DashedCubic or
                StrokeKind.DashedPolyline or
                StrokeKind.DashedSpline);
            switch (_kind)
            {
                case StrokeKind.Line:
                case StrokeKind.DashedLine:
                    context.DrawLine(pen, new Vector2(0f, 32f), new Vector2(64f, 32f));
                    break;
                case StrokeKind.Quadratic:
                case StrokeKind.DashedQuadratic:
                    context.DrawQuadraticBezier(
                        pen,
                        new Vector2(0f, 32f),
                        new Vector2(32f, 32f),
                        new Vector2(64f, 32f));
                    break;
                case StrokeKind.Cubic:
                case StrokeKind.DashedCubic:
                    context.DrawCubicBezier(
                        pen,
                        new Vector2(0f, 32f),
                        new Vector2(20f, 32f),
                        new Vector2(44f, 32f),
                        new Vector2(64f, 32f));
                    break;
                case StrokeKind.Polyline:
                case StrokeKind.DashedPolyline:
                    context.DrawPolyline(
                        pen,
                        [new Vector2(0f, 32f), new Vector2(64f, 32f)]);
                    break;
                case StrokeKind.Spline:
                case StrokeKind.DashedSpline:
                    context.DrawSpline(
                        pen,
                        [new Vector2(0f, 32f), new Vector2(64f, 32f)],
                        [0d, 0d, 1d, 1d],
                        degree: 1);
                    break;
                case StrokeKind.Path:
                    context.DrawPath(null, pen, CreateHorizontalPath());
                    break;
                case StrokeKind.PictureLine:
                    context.DrawPicture(_picture!);
                    break;
            }
        }

        public void Dispose() => _picture?.Dispose();
    }

    private sealed class VerticalStrokeVisual : FrameworkElement, IDisposable
    {
        public VerticalStrokeVisual(Matrix4x4 transform)
        {
            Width = 64f;
            Height = 64f;
            Transform = transform;
        }

        public override void OnRender(DrawingContext context) =>
            context.DrawLine(CreatePen(), new Vector2(32f, 0f), new Vector2(32f, 64f));

        public void Dispose()
        {
        }
    }

    private sealed class ProvenanceStrokeVisual : FrameworkElement
    {
        private readonly bool _isLocal;
        private readonly float _recordedThickness;

        public ProvenanceStrokeVisual(bool isLocal, float recordedThickness)
        {
            _isLocal = isLocal;
            _recordedThickness = recordedThickness;
            Width = 64f;
            Height = 64f;
            Transform = UniformScaleAboutCenter(0.5f);
        }

        public override void OnRender(DrawingContext context)
        {
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawLine,
                Pen = new Pen(
                    new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                    _recordedThickness),
                Position = new Vector2(0f, 32f),
                Position2 = new Vector2(32f, 32f),
                Transform = Matrix4x4.CreateScale(2f, 2f, 1f),
                IsPenThicknessLocal = _isLocal
            });
        }
    }

    private sealed class NonFiniteStrokeVisual : FrameworkElement
    {
        public NonFiniteStrokeVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            var transform = Matrix4x4.Identity;
            transform.M11 = float.NaN;
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawLine,
                Pen = CreatePen(),
                Position = new Vector2(0f, 32f),
                Position2 = new Vector2(64f, 32f),
                Transform = transform,
                IsPenThicknessLocal = true
            });
        }
    }

    private sealed class CollapsedStrokeVisual : FrameworkElement
    {
        public CollapsedStrokeVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawLine,
                Pen = CreatePen(),
                Position = new Vector2(0f, 32f),
                Position2 = new Vector2(64f, 32f),
                Transform = Matrix4x4.CreateScale(0f, 1f, 1f),
                IsPenThicknessLocal = true
            });
        }
    }

    private sealed class InvalidStrokeWidthVisual : FrameworkElement
    {
        public InvalidStrokeWidthVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                new Pen(
                    new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                    float.NaN),
                new Rect(8f, 8f, 48f, 48f));
        }
    }

    public enum StrokeKind
    {
        Line,
        Quadratic,
        Cubic,
        Polyline,
        Spline,
        Path,
        DashedLine,
        DashedQuadratic,
        DashedCubic,
        DashedPolyline,
        DashedSpline,
        PictureLine
    }
}
