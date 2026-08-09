using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfDrawingContext = System.Windows.Media.DrawingContext;
using WpfMatrix = System.Windows.Media.Matrix;
using WpfMatrixTransform = System.Windows.Media.MatrixTransform;
using WpfPen = System.Windows.Media.Pen;
using WpfRect = System.Windows.Rect;
using WpfRectangleGeometry = System.Windows.Media.RectangleGeometry;
using GdiColor = System.Drawing.Color;
using GdiGraphics = System.Drawing.Graphics;
using GdiGraphicsPath = System.Drawing.Drawing2D.GraphicsPath;
using GdiLineCap = System.Drawing.Drawing2D.LineCap;
using GdiLineJoin = System.Drawing.Drawing2D.LineJoin;
using GdiPen = System.Drawing.Pen;
using GdiPoint = System.Drawing.Point;
using GdiPointF = System.Drawing.PointF;

namespace ProGPU.Tests;

public sealed class StrokeTransformProvenanceTests
{
    [Fact]
    public void SceneTransformOverloadsMarkRawPenThicknessAsLocal()
    {
        var context = new DrawingContext();
        var pen = new Pen(new SolidColorBrush(Vector4.One), 2f);
        var path = CreatePath();
        var cache = RenderCommandGeometryCache.ForPath(path);
        var transform = Matrix4x4.CreateScale(2f, 3f, 1f);

        context.DrawRectangle(null, pen, new Rect(1f, 2f, 3f, 4f), transform);
        context.DrawPath(null, pen, path, transform);
        context.DrawPath(null, pen, path, transform, cache);
        context.DrawEllipse(null, pen, new Vector2(5f, 6f), 7f, 8f, transform);
        context.DrawRoundedRectangle(
            null,
            pen,
            new Rect(1f, 2f, 30f, 40f),
            3f,
            4f,
            transform);
        context.PushOpacityMask(path, pen, new Rect(0f, 0f, 20f, 20f), transform);

        Assert.Equal(6, context.Commands.Count);
        Assert.All(
            context.Commands,
            command =>
            {
                Assert.Same(pen, command.Pen);
                Assert.Equal(transform, command.Transform);
                Assert.True(command.IsPenThicknessLocal);
            });
    }

    [Fact]
    public void TransformOverloadsDoNotTagFillOnlyCommandsAsLocalStrokes()
    {
        var context = new DrawingContext();
        var brush = new SolidColorBrush(Vector4.One);
        var path = CreatePath();
        var transform = Matrix4x4.CreateScale(2f, 3f, 1f);

        context.DrawRectangle(brush, null, new Rect(1f, 2f, 3f, 4f), transform);
        context.DrawPath(brush, null, path, transform);
        context.DrawEllipse(brush, null, new Vector2(5f, 6f), 7f, 8f, transform);
        context.DrawRoundedRectangle(
            brush,
            null,
            new Rect(1f, 2f, 30f, 40f),
            3f,
            4f,
            transform);

        Assert.All(
            context.Commands,
            command => Assert.False(command.IsPenThicknessLocal));
    }

    [Fact]
    public void DashedCurveAndIndexedStrokeRecordersRetainGeometryCaches()
    {
        var context = new DrawingContext();
        var pen = new Pen(
            new SolidColorBrush(Vector4.One),
            2f,
            dashArray: [2d, 1d]);

        context.DrawQuadraticBezier(
            pen,
            Vector2.Zero,
            new Vector2(5f, 0f),
            new Vector2(10f, 0f));
        context.DrawCubicBezier(
            pen,
            Vector2.Zero,
            new Vector2(3f, 0f),
            new Vector2(7f, 0f),
            new Vector2(10f, 0f));
        context.DrawPolyline(
            pen,
            [Vector2.Zero, new Vector2(10f, 0f)]);
        context.DrawSpline(
            pen,
            [Vector2.Zero, new Vector2(10f, 0f)],
            [0d, 0d, 1d, 1d],
            degree: 1);

        Assert.Equal(4, context.Commands.Count);
        Assert.All(
            context.Commands,
            static command => Assert.NotNull(command.GeometryCache?.StrokePath));
    }

    [Fact]
    public void WpfGeometryKeepsRawPenAcrossComposedTransforms()
    {
        var nativeContext = new DrawingContext();
        using var context = new WpfDrawingContext(nativeContext);
        var outerTransform = new WpfMatrix
        {
            M11 = 3,
            M22 = 3
        };
        var geometryTransform = new WpfMatrix
        {
            M11 = 2,
            M22 = 2
        };
        var geometry = new WpfRectangleGeometry(
            new WpfRect(0, 0, 20, 10),
            0,
            0,
            new WpfMatrixTransform(geometryTransform));

        context.PushTransform(new WpfMatrixTransform(outerTransform));
        context.DrawGeometry(
            brush: null,
            pen: new WpfPen(WpfBrushes.Black, 2),
            geometry);

        RenderCommand command = Assert.Single(nativeContext.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.Equal(2f, command.Pen!.Thickness);
        Assert.Equal(Matrix4x4.CreateScale(6f, 6f, 1f), command.Transform);
        Assert.True(command.IsPenThicknessLocal);
    }

    [Fact]
    public void SystemDrawingPathKeepsRawPenAcrossAnisotropicTransform()
    {
        var nativeContext = new DrawingContext();
        using var graphics = GdiGraphics.FromProGpuDrawingContext(
            nativeContext,
            Matrix4x4.Identity);
        using var pen = new GdiPen(GdiColor.Red, 2f);
        using var path = new GdiGraphicsPath();
        path.AddLine(1f, 2f, 5f, 2f);

        graphics.ScaleTransform(4f, 1f);
        graphics.DrawPath(pen, path);

        RenderCommand command = Assert.Single(nativeContext.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.Equal(2f, command.Pen!.Thickness);
        Assert.Equal(Matrix4x4.CreateScale(4f, 1f, 1f), command.Transform);
        Assert.True(command.IsPenThicknessLocal);
    }

    [Fact]
    public void SystemDrawingAnalyticStrokesUseLocalPathsUnderAnisotropicTransform()
    {
        var nativeContext = new DrawingContext();
        using var graphics = GdiGraphics.FromProGpuDrawingContext(
            nativeContext,
            Matrix4x4.Identity);
        using var pen = new GdiPen(GdiColor.Red, 2f);

        graphics.ScaleTransform(4f, 1f);
        graphics.DrawRectangle(pen, 1f, 2f, 10f, 8f);
        graphics.DrawEllipse(pen, 3f, 4f, 12f, 6f);

        Assert.Collection(
            nativeContext.Commands,
            command => AssertLocalPathStroke(command, pen.Width),
            command => AssertLocalPathStroke(command, pen.Width));
    }

    [Fact]
    public void SystemDrawingLineKeepsLocalEndpointsAndRawPenUnderShear()
    {
        var nativeContext = new DrawingContext();
        using var graphics = GdiGraphics.FromProGpuDrawingContext(
            nativeContext,
            Matrix4x4.Identity);
        using var pen = new GdiPen(GdiColor.Red, 2f);
        using var transform = new System.Drawing.Drawing2D.Matrix(
            2f,
            0.5f,
            0.75f,
            1f,
            8f,
            9f);
        graphics.Transform = transform;

        graphics.DrawLine(pen, 1f, 2f, 5f, 7f);

        var command = Assert.Single(nativeContext.Commands);
        Assert.Equal(RenderCommandType.DrawLine, command.Type);
        Assert.Equal(new Vector2(1f, 2f), command.Position);
        Assert.Equal(new Vector2(5f, 7f), command.Position2);
        Assert.Equal(2f, command.Pen!.Thickness);
        Assert.Equal(
            new Matrix4x4(
                2f, 0.5f, 0f, 0f,
                0.75f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                8f, 9f, 0f, 1f),
            command.Transform);
        Assert.True(command.IsPenThicknessLocal);
    }

    [Fact]
    public void SystemDrawingLinesRetainOneConnectedPathWithEndpointCapsAndJoins()
    {
        var nativeContext = new DrawingContext();
        using var graphics = GdiGraphics.FromProGpuDrawingContext(
            nativeContext,
            Matrix4x4.Identity);
        using var pen = new GdiPen(GdiColor.Red, 3f)
        {
            StartCap = GdiLineCap.Round,
            EndCap = GdiLineCap.Triangle,
            LineJoin = GdiLineJoin.Bevel
        };
        using var transform = new System.Drawing.Drawing2D.Matrix(
            1.5f,
            0.25f,
            0.5f,
            0.75f,
            4f,
            6f);
        graphics.Transform = transform;

        graphics.DrawLines(
            pen,
            [
                new GdiPointF(1f, 2f),
                new GdiPointF(5f, 8f),
                new GdiPointF(11f, 3f)
            ]);
        graphics.DrawLines(
            pen,
            [
                new GdiPoint(2, 3),
                new GdiPoint(7, 9),
                new GdiPoint(13, 4)
            ]);

        Assert.Collection(
            nativeContext.Commands,
            AssertConnectedSystemDrawingPolyline,
            AssertConnectedSystemDrawingPolyline);
    }

    private static void AssertLocalPathStroke(RenderCommand command, float thickness)
    {
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.NotNull(command.Path);
        Assert.Equal(thickness, command.Pen!.Thickness);
        Assert.Equal(Matrix4x4.CreateScale(4f, 1f, 1f), command.Transform);
        Assert.True(command.IsPenThicknessLocal);
    }

    private static void AssertConnectedSystemDrawingPolyline(RenderCommand command)
    {
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        var figure = Assert.Single(command.Path!.Figures);
        Assert.Equal(2, figure.Segments.Count);
        Assert.All(figure.Segments, static segment => Assert.IsType<LineSegment>(segment));
        Assert.Equal(PenLineCap.Round, command.Pen!.StartLineCap);
        Assert.Equal(PenLineCap.Triangle, command.Pen.EndLineCap);
        Assert.Equal(PenLineJoin.Bevel, command.Pen.LineJoin);
        Assert.Equal(3f, command.Pen.Thickness);
        Assert.True(command.IsPenThicknessLocal);
    }

    private static PathGeometry CreatePath()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero);
        figure.Segments.Add(new LineSegment(new Vector2(10f, 0f)));
        path.Figures.Add(figure);
        return path;
    }
}
