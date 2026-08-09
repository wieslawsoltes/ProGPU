using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class AnalyticPrimitiveTransformPaddingTests
{
    private const int LateTransformSurfaceSize = 96;
    private const float TargetTop = 24.75f;
    private const float TargetHeight = 15.5f;

    private static readonly Matrix4x4 LateUniformDownscale =
        Matrix4x4.CreateScale(0.1f, 0.1f, 1f) *
        Matrix4x4.CreateTranslation(32.25f, 40.75f, 0f);

    [Fact]
    public void StrokeScalesReportBothSingularValuesForShear()
    {
        var transform = Matrix4x4.Identity;
        transform.M12 = 1f;

        Assert.True(TransformMetrics.TryGetStrokeScales(
            transform,
            out var maximumScale,
            out var minimumScale));

        var goldenRatio = (1f + MathF.Sqrt(5f)) * 0.5f;
        Assert.Equal(goldenRatio, maximumScale, precision: 6);
        Assert.Equal(1f / goldenRatio, minimumScale, precision: 6);
    }

    [Theory]
    [MemberData(nameof(InvalidTransforms))]
    public void StrokeScalesRejectInvalidOrCollapsedTransforms(Matrix4x4 transform)
    {
        Assert.False(TransformMetrics.TryGetStrokeScales(
            transform,
            out var maximumScale,
            out var minimumScale));
        Assert.Equal(0f, maximumScale);
        Assert.Equal(0f, minimumScale);
    }

    [Theory]
    [InlineData(AnalyticPrimitiveKind.Rectangle)]
    [InlineData(AnalyticPrimitiveKind.Ellipse)]
    [InlineData(AnalyticPrimitiveKind.RoundedRectangle)]
    public void StrongAnisotropicDownscalePreservesFillEdgeCoverage(
        AnalyticPrimitiveKind primitiveKind)
    {
        using var window = new HeadlessWindow(64, 64);
        window.Content = new AnalyticPrimitiveVisual(
            primitiveKind,
            isStroke: false,
            isTransformed: false);
        window.Render();
        var referencePixels = window.ReadPixels();

        window.Content = new AnalyticPrimitiveVisual(
            primitiveKind,
            isStroke: false,
            isTransformed: true);
        window.Render();
        var transformedPixels = window.ReadPixels();

        Assert.InRange(GetRed(referencePixels, x: 32, y: 24), 1, 254);
        AssertCentralScanlineMatches(referencePixels, transformedPixels);
        Assert.InRange(GetPrimitiveMinimumY(window, primitiveKind), 23.24f, 23.26f);
    }

    [Theory]
    [InlineData(AnalyticPrimitiveKind.Rectangle)]
    [InlineData(AnalyticPrimitiveKind.Ellipse)]
    [InlineData(AnalyticPrimitiveKind.RoundedRectangle)]
    public void StrongUniformDownscalePreservesStrokeEdgeCoverage(
        AnalyticPrimitiveKind primitiveKind)
    {
        using var window = new HeadlessWindow(64, 64);
        window.Content = new AnalyticPrimitiveVisual(
            primitiveKind,
            isStroke: true,
            isTransformed: false);
        window.Render();
        var referencePixels = window.ReadPixels();

        window.Content = new AnalyticPrimitiveVisual(
            primitiveKind,
            isStroke: true,
            isTransformed: true);
        window.Render();
        var transformedPixels = window.ReadPixels();

        Assert.InRange(GetRed(referencePixels, x: 32, y: 22), 1, 254);
        AssertCentralScanlineMatches(referencePixels, transformedPixels);
        Assert.InRange(GetPrimitiveMinimumY(window, primitiveKind), 21.24f, 21.26f);
    }

    [Theory]
    [InlineData(AnalyticPrimitiveKind.Rectangle, false, false)]
    [InlineData(AnalyticPrimitiveKind.Rectangle, false, true)]
    [InlineData(AnalyticPrimitiveKind.Rectangle, true, false)]
    [InlineData(AnalyticPrimitiveKind.Rectangle, true, true)]
    [InlineData(AnalyticPrimitiveKind.Ellipse, false, false)]
    [InlineData(AnalyticPrimitiveKind.Ellipse, false, true)]
    [InlineData(AnalyticPrimitiveKind.Ellipse, true, false)]
    [InlineData(AnalyticPrimitiveKind.Ellipse, true, true)]
    [InlineData(AnalyticPrimitiveKind.RoundedRectangle, false, false)]
    [InlineData(AnalyticPrimitiveKind.RoundedRectangle, false, true)]
    [InlineData(AnalyticPrimitiveKind.RoundedRectangle, true, false)]
    [InlineData(AnalyticPrimitiveKind.RoundedRectangle, true, true)]
    public void LateUniformDownscalePreservesAnalyticQuadCoverage(
        AnalyticPrimitiveKind primitiveKind,
        bool isStroke,
        bool useStaticBuffer)
    {
        var expected = RenderLateTransformCommand(
            CreateLateAnalyticCommand(primitiveKind, isStroke),
            useStaticBuffer,
            bakeTransform: true);
        var actual = RenderLateTransformCommand(
            CreateLateAnalyticCommand(primitiveKind, isStroke),
            useStaticBuffer,
            bakeTransform: false);

        AssertLateCoverageMatches(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateUniformDownscalePreservesOrdinaryArcQuadCoverage(
        bool useStaticBuffer)
    {
        var expected = RenderLateTransformCommand(
            CreateLateArcCommand(),
            useStaticBuffer,
            bakeTransform: true);
        var actual = RenderLateTransformCommand(
            CreateLateArcCommand(),
            useStaticBuffer,
            bakeTransform: false);

        AssertLateCoverageMatches(expected, actual);
    }

    public static TheoryData<Matrix4x4> InvalidTransforms => new()
    {
        Matrix4x4.CreateScale(1f, 0f, 1f),
        new Matrix4x4(
            float.NaN, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f)
    };

    private static void AssertCentralScanlineMatches(
        byte[] expected,
        byte[] actual)
    {
        for (var y = 20; y <= 45; y++)
        {
            var expectedRed = GetRed(expected, x: 32, y);
            var actualRed = GetRed(actual, x: 32, y);
            Assert.True(
                Math.Abs(expectedRed - actualRed) <= 3,
                $"Row {y} expected red {expectedRed}, actual red {actualRed}.");
        }
    }

    private static byte GetRed(byte[] pixels, int x, int y) =>
        pixels[(y * 64 + x) * 4];

    private static RenderCommand CreateLateAnalyticCommand(
        AnalyticPrimitiveKind primitiveKind,
        bool isStroke)
    {
        var brush = new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f));
        var command = new RenderCommand
        {
            Brush = isStroke ? null : brush,
            Pen = isStroke ? new Pen(brush, 20f) : null,
            IsPenThicknessLocal = isStroke
        };
        switch (primitiveKind)
        {
            case AnalyticPrimitiveKind.Rectangle:
                command.Type = RenderCommandType.DrawRect;
                command.Rect = new Rect(0f, 0f, 320f, 155f);
                break;
            case AnalyticPrimitiveKind.Ellipse:
                command.Type = RenderCommandType.DrawEllipse;
                command.Position2 = new Vector2(160f, 77.5f);
                command.RadiusX = 160f;
                command.RadiusY = 77.5f;
                break;
            case AnalyticPrimitiveKind.RoundedRectangle:
                command.Type = RenderCommandType.DrawRoundedRect;
                command.Rect = new Rect(0f, 0f, 320f, 155f);
                command.RadiusX = 60f;
                command.RadiusY = 60f;
                break;
        }

        return command;
    }

    private static RenderCommand CreateLateArcCommand()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(0f, 77.5f));
        figure.Segments.Add(new ArcSegment(
            new Vector2(320f, 77.5f),
            new Vector2(160f, 77.5f),
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        path.Figures.Add(figure);
        var brush = new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f));
        return new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = new Pen(brush, 20f),
            IsPenThicknessLocal = true
        };
    }

    private static byte[] RenderLateTransformCommand(
        RenderCommand command,
        bool useStaticBuffer,
        bool bakeTransform)
    {
        using var window = new HeadlessWindow(
            LateTransformSurfaceSize,
            LateTransformSurfaceSize);
        DxfStaticBuffer? buffer = null;
        if (bakeTransform)
        {
            command.Transform = LateUniformDownscale;
        }
        else if (!useStaticBuffer)
        {
            command.UseGpuTransforms = true;
            command.CameraView = LateUniformDownscale;
        }

        if (useStaticBuffer)
        {
            var context = new DrawingContext();
            context.Commands.Add(command);
            buffer = window.Compositor.CompileStaticDxf(context);
            window.Content = new LateStaticVisual(
                buffer,
                bakeTransform ? Matrix4x4.Identity : LateUniformDownscale);
        }
        else
        {
            window.Content = new LateCommandVisual(command);
        }

        try
        {
            window.Render();
            return window.ReadPixels();
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    private static void AssertLateCoverageMatches(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        var expectedBounds = GetLateCoverageBounds(expected);
        var actualBounds = GetLateCoverageBounds(actual);
        var missingPixels = 0;
        for (var index = 2; index < expected.Length; index += 4)
        {
            if (expected[index] > 1 && actual[index] <= 1)
            {
                missingPixels++;
            }
        }

        Assert.True(expectedBounds.MaxX >= expectedBounds.MinX);
        Assert.True(
            expectedBounds == actualBounds && missingPixels == 0,
            $"Late-transform coverage mismatch: expected bounds {expectedBounds}, " +
            $"actual bounds {actualBounds}, missing pixels {missingPixels}.");
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) GetLateCoverageBounds(
        byte[] pixels)
    {
        var minX = LateTransformSurfaceSize;
        var minY = LateTransformSurfaceSize;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < LateTransformSurfaceSize; y++)
        {
            for (var x = 0; x < LateTransformSurfaceSize; x++)
            {
                if (pixels[(y * LateTransformSurfaceSize + x) * 4 + 2] <= 1)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return (minX, minY, maxX, maxY);
    }

    private static float GetPrimitiveMinimumY(
        HeadlessWindow window,
        AnalyticPrimitiveKind primitiveKind)
    {
        var expectedShapeType = (float)primitiveKind;
        var vertices = window.Compositor.VectorVertices
            .Where(vertex => MathF.Abs(vertex.ShapeType - expectedShapeType) < 0.01f)
            .ToArray();
        Assert.Equal(4, vertices.Length);
        return vertices.Min(static vertex => vertex.Position.Y);
    }

    public enum AnalyticPrimitiveKind
    {
        Rectangle = 0,
        Ellipse = 1,
        RoundedRectangle = 2
    }

    private sealed class AnalyticPrimitiveVisual : FrameworkElement
    {
        private readonly Brush _fill = new SolidColorBrush(
            new Vector4(1f, 0f, 0f, 1f));

        private readonly AnalyticPrimitiveKind _primitiveKind;
        private readonly bool _isStroke;
        private readonly bool _isTransformed;

        public AnalyticPrimitiveVisual(
            AnalyticPrimitiveKind primitiveKind,
            bool isStroke,
            bool isTransformed)
        {
            _primitiveKind = primitiveKind;
            _isStroke = isStroke;
            _isTransformed = isTransformed;
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            if (_isTransformed)
            {
                DrawTransformed(context);
                return;
            }

            var targetRect = new Rect(16f, TargetTop, 32f, TargetHeight);
            var brush = _isStroke ? null : _fill;
            var pen = _isStroke ? CreatePen(4f) : null;
            switch (_primitiveKind)
            {
                case AnalyticPrimitiveKind.Rectangle:
                    context.DrawRectangle(brush, pen, targetRect);
                    break;
                case AnalyticPrimitiveKind.Ellipse:
                    context.DrawEllipse(
                        brush,
                        pen,
                        new Vector2(32f, TargetTop + TargetHeight * 0.5f),
                        16f,
                        TargetHeight * 0.5f);
                    break;
                case AnalyticPrimitiveKind.RoundedRectangle:
                    context.DrawRoundedRectangle(brush, pen, targetRect, 6f, 6f);
                    break;
            }
        }

        private void DrawTransformed(DrawingContext context)
        {
            var scaleX = _isStroke ? 0.02f : 0.5f;
            const float scaleY = 0.02f;
            var transform = Matrix4x4.CreateScale(scaleX, scaleY, 1f) *
                Matrix4x4.CreateTranslation(16f, TargetTop, 0f);
            var sourceWidth = 32f / scaleX;
            var sourceHeight = TargetHeight / scaleY;
            var sourceRect = new Rect(0f, 0f, sourceWidth, sourceHeight);
            var brush = _isStroke ? null : _fill;
            var pen = _isStroke ? CreatePen(4f / scaleY) : null;
            var radius = 6f / scaleX;

            switch (_primitiveKind)
            {
                case AnalyticPrimitiveKind.Rectangle:
                    context.DrawRectangle(brush, pen, sourceRect, transform);
                    break;
                case AnalyticPrimitiveKind.Ellipse:
                    context.DrawEllipse(
                        brush,
                        pen,
                        new Vector2(
                            sourceRect.X + sourceRect.Width * 0.5f,
                            sourceRect.Y + sourceRect.Height * 0.5f),
                        sourceWidth * 0.5f,
                        sourceHeight * 0.5f,
                        transform);
                    break;
                case AnalyticPrimitiveKind.RoundedRectangle:
                    context.DrawRoundedRectangle(
                        brush,
                        pen,
                        sourceRect,
                        radius,
                        radius,
                        transform);
                    break;
            }
        }

        private Pen CreatePen(float thickness) => new(_fill, thickness);
    }

    private sealed class LateStaticVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _buffer;

        public LateStaticVisual(DxfStaticBuffer buffer, Matrix4x4 transform)
        {
            _buffer = buffer;
            Transform = transform;
            Width = LateTransformSurfaceSize;
            Height = LateTransformSurfaceSize;
        }

        public override void OnRender(DrawingContext context) =>
            context.DrawStaticDxf(_buffer);
    }

    private sealed class LateCommandVisual : FrameworkElement
    {
        private readonly RenderCommand _command;

        public LateCommandVisual(RenderCommand command)
        {
            _command = command;
            Width = LateTransformSurfaceSize;
            Height = LateTransformSurfaceSize;
        }

        public override void OnRender(DrawingContext context) =>
            context.Commands.Add(_command);
    }
}
