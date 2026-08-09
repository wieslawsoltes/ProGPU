using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class DirectStrokeCapRenderingTests
{
    private const int SurfaceSize = 128;

    [Theory]
    [InlineData(DirectCapStrokeKind.Line, PenLineCap.Round, false)]
    [InlineData(DirectCapStrokeKind.Line, PenLineCap.Round, true)]
    [InlineData(DirectCapStrokeKind.Line, PenLineCap.Square, false)]
    [InlineData(DirectCapStrokeKind.Line, PenLineCap.Square, true)]
    [InlineData(DirectCapStrokeKind.Line, PenLineCap.Triangle, false)]
    [InlineData(DirectCapStrokeKind.Line, PenLineCap.Triangle, true)]
    [InlineData(DirectCapStrokeKind.Quadratic, PenLineCap.Round, false)]
    [InlineData(DirectCapStrokeKind.Quadratic, PenLineCap.Round, true)]
    [InlineData(DirectCapStrokeKind.Quadratic, PenLineCap.Square, false)]
    [InlineData(DirectCapStrokeKind.Quadratic, PenLineCap.Square, true)]
    [InlineData(DirectCapStrokeKind.Quadratic, PenLineCap.Triangle, false)]
    [InlineData(DirectCapStrokeKind.Quadratic, PenLineCap.Triangle, true)]
    [InlineData(DirectCapStrokeKind.Cubic, PenLineCap.Round, false)]
    [InlineData(DirectCapStrokeKind.Cubic, PenLineCap.Round, true)]
    [InlineData(DirectCapStrokeKind.Cubic, PenLineCap.Square, false)]
    [InlineData(DirectCapStrokeKind.Cubic, PenLineCap.Square, true)]
    [InlineData(DirectCapStrokeKind.Cubic, PenLineCap.Triangle, false)]
    [InlineData(DirectCapStrokeKind.Cubic, PenLineCap.Triangle, true)]
    public void DirectPrimitiveCapsMatchEquivalentRetainedPath(
        DirectCapStrokeKind kind,
        PenLineCap cap,
        bool useAffineTransform)
    {
        var transform = useAffineTransform
            ? CreateAffineTransform()
            : Matrix4x4.Identity;
        var pen = new Pen(
            new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
            thickness: 10f,
            startLineCap: cap,
            endLineCap: cap);
        var path = CreatePath(kind);
        var direct = CreateDirectCommand(kind, pen, transform);
        var expected = new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = pen,
            Transform = transform,
            IsPenThicknessLocal = true
        };

        var expectedPixels = Render(expected);
        var actualPixels = Render(direct);

        Assert.Equal(expectedPixels, actualPixels);
    }

    [Theory]
    [InlineData(DirectCapStrokeKind.Line)]
    [InlineData(DirectCapStrokeKind.Quadratic)]
    [InlineData(DirectCapStrokeKind.Cubic)]
    public void NonFlatDirectPrimitiveRetainsItsPathAtRecordingTime(
        DirectCapStrokeKind kind)
    {
        var pen = new Pen(
            new SolidColorBrush(Vector4.One),
            thickness: 4f,
            startLineCap: PenLineCap.Round,
            endLineCap: PenLineCap.Triangle);

        var command = CreateRecordedCommand(kind, pen);

        Assert.NotNull(command.GeometryCache);
        Assert.NotNull(command.GeometryCache!.StrokePath);
    }

    [Theory]
    [InlineData(PenLineJoin.Miter, false)]
    [InlineData(PenLineJoin.Miter, true)]
    [InlineData(PenLineJoin.Bevel, false)]
    [InlineData(PenLineJoin.Bevel, true)]
    [InlineData(PenLineJoin.Round, false)]
    [InlineData(PenLineJoin.Round, true)]
    public void ConnectedPolylineCapsAndJoinsMatchEquivalentRetainedPath(
        PenLineJoin lineJoin,
        bool useAffineTransform)
    {
        Vector2[] points =
        [
            new(30f, 88f),
            new(64f, 30f),
            new(98f, 88f)
        ];
        var pen = new Pen(
            new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
            thickness: 10f,
            lineJoin: lineJoin,
            miterLimit: 4f,
            startLineCap: PenLineCap.Round,
            endLineCap: PenLineCap.Triangle);
        var transform = useAffineTransform
            ? CreateAffineTransform()
            : Matrix4x4.Identity;
        var context = new DrawingContext();
        context.DrawPolyline(pen, points);
        var direct = Assert.Single(context.Commands);
        direct.Transform = transform;
        direct.IsPenThicknessLocal = true;

        var path = RenderCommandGeometryCache.CreatePolylinePath(
            points,
            isClosed: false);
        var expected = new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = pen,
            Transform = transform,
            IsPenThicknessLocal = true
        };

        Assert.NotNull(direct.GeometryCache?.StrokePath);
        Assert.Equal(Render(expected), Render(direct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedPolylineJoinsMatchEquivalentRetainedPath(
        bool useAffineTransform)
    {
        Vector2[] points =
        [
            new(32f, 88f),
            new(64f, 28f),
            new(96f, 88f)
        ];
        var pen = new Pen(
            new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
            thickness: 10f,
            lineJoin: PenLineJoin.Round);
        var transform = useAffineTransform
            ? CreateAffineTransform()
            : Matrix4x4.Identity;
        var context = new DrawingContext();
        context.DrawPolyline(pen, points, isClosed: true);
        var direct = Assert.Single(context.Commands);
        direct.Transform = transform;
        direct.IsPenThicknessLocal = true;

        var path = RenderCommandGeometryCache.CreatePolylinePath(
            points,
            isClosed: true);
        var expected = new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = pen,
            Transform = transform,
            IsPenThicknessLocal = true
        };

        Assert.NotNull(direct.GeometryCache?.StrokePath);
        Assert.Equal(Render(expected), Render(direct));
    }

    [Theory]
    [InlineData(PenLineJoin.Miter, false)]
    [InlineData(PenLineJoin.Miter, true)]
    [InlineData(PenLineJoin.Bevel, false)]
    [InlineData(PenLineJoin.Bevel, true)]
    [InlineData(PenLineJoin.Round, false)]
    [InlineData(PenLineJoin.Round, true)]
    public void SplineCapsAndJoinsMatchItsRetainedCenterline(
        PenLineJoin lineJoin,
        bool useAffineTransform)
    {
        Vector2[] controlPoints =
        [
            new(24f, 82f),
            new(42f, 24f),
            new(82f, 108f),
            new(104f, 46f)
        ];
        double[] knots = [0d, 0d, 0d, 1d, 2d, 2d, 2d];
        var pen = new Pen(
            new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
            thickness: 9f,
            lineJoin: lineJoin,
            miterLimit: 4f,
            startLineCap: PenLineCap.Round,
            endLineCap: PenLineCap.Triangle);
        var transform = useAffineTransform
            ? CreateAffineTransform()
            : Matrix4x4.Identity;
        var context = new DrawingContext();
        context.DrawSpline(pen, controlPoints, knots, degree: 2);
        var spline = Assert.Single(context.Commands);
        spline.Transform = transform;
        spline.IsPenThicknessLocal = true;
        var retainedPath = Assert.IsType<PathGeometry>(
            spline.GeometryCache?.StrokePath);

        var expected = new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = retainedPath,
            GeometryCache = RenderCommandGeometryCache.ForPath(retainedPath),
            Pen = pen,
            Transform = transform,
            IsPenThicknessLocal = true
        };

        Assert.Equal(Render(expected), Render(spline));
    }

    [Fact]
    public void AppendTranslationRebuildsConnectedPolylineCacheOnce()
    {
        Vector2[] points =
        [
            new(2f, 4f),
            new(8f, 2f),
            new(14f, 6f)
        ];
        var pen = new Pen(
            new SolidColorBrush(Vector4.One),
            thickness: 3f,
            lineJoin: PenLineJoin.Round,
            startLineCap: PenLineCap.Round,
            endLineCap: PenLineCap.Triangle);
        var source = new DrawingContext();
        source.DrawPolyline(pen, points);
        var sourceCache = Assert.IsType<RenderCommandGeometryCache>(
            source.Commands[0].GeometryCache);

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        var command = Assert.Single(target.Commands);
        var translatedCache = Assert.IsType<RenderCommandGeometryCache>(
            command.GeometryCache);
        Assert.NotSame(sourceCache, translatedCache);
        var figure = Assert.Single(translatedCache.StrokePath!.Figures);
        Assert.Equal(new Vector2(22f, 34f), figure.StartPoint);
        Assert.Equal(
            new Vector2(28f, 32f),
            Assert.IsType<LineSegment>(figure.Segments[0]).Point);
        Assert.Equal(
            new Vector2(34f, 36f),
            Assert.IsType<LineSegment>(figure.Segments[1]).Point);
    }

    [Fact]
    public void AppendTranslationPreservesCacheWhenItComposesTheTransform()
    {
        var pen = new Pen(
            new SolidColorBrush(Vector4.One),
            thickness: 3f,
            startLineCap: PenLineCap.Round,
            endLineCap: PenLineCap.Triangle);
        var source = new DrawingContext();
        source.DrawLine(
            pen,
            new Vector2(2f, 4f),
            new Vector2(14f, 6f),
            Matrix4x4.CreateScale(2f, 3f, 1f));
        var sourceCache = source.Commands[0].GeometryCache;

        var target = new DrawingContext();
        target.Append(source, new Vector2(20f, 30f));

        var command = Assert.Single(target.Commands);
        Assert.Same(sourceCache, command.GeometryCache);
        Assert.Equal(
            Matrix4x4.CreateScale(2f, 3f, 1f) *
            Matrix4x4.CreateTranslation(20f, 30f, 0f),
            command.Transform);
    }

    private static RenderCommand CreateDirectCommand(
        DirectCapStrokeKind kind,
        Pen pen,
        Matrix4x4 transform)
    {
        var command = CreateRecordedCommand(kind, pen);
        command.Transform = transform;
        command.IsPenThicknessLocal = true;
        return command;
    }

    private static RenderCommand CreateRecordedCommand(
        DirectCapStrokeKind kind,
        Pen pen)
    {
        var context = new DrawingContext();
        switch (kind)
        {
            case DirectCapStrokeKind.Line:
                context.DrawLine(pen, new Vector2(28f, 68f), new Vector2(100f, 60f));
                break;
            case DirectCapStrokeKind.Quadratic:
                context.DrawQuadraticBezier(
                    pen,
                    new Vector2(28f, 76f),
                    new Vector2(64f, 24f),
                    new Vector2(100f, 68f));
                break;
            case DirectCapStrokeKind.Cubic:
                context.DrawCubicBezier(
                    pen,
                    new Vector2(28f, 80f),
                    new Vector2(42f, 20f),
                    new Vector2(84f, 108f),
                    new Vector2(100f, 52f));
                break;
        }

        return Assert.Single(context.Commands);
    }

    private static PathGeometry CreatePath(DirectCapStrokeKind kind)
    {
        var path = new PathGeometry();
        PathFigure figure;
        switch (kind)
        {
            case DirectCapStrokeKind.Line:
                figure = new PathFigure(new Vector2(28f, 68f));
                figure.Segments.Add(new LineSegment(new Vector2(100f, 60f)));
                break;
            case DirectCapStrokeKind.Quadratic:
                figure = new PathFigure(new Vector2(28f, 76f));
                figure.Segments.Add(new QuadraticBezierSegment(
                    new Vector2(64f, 24f),
                    new Vector2(100f, 68f)));
                break;
            default:
                figure = new PathFigure(new Vector2(28f, 80f));
                figure.Segments.Add(new CubicBezierSegment(
                    new Vector2(42f, 20f),
                    new Vector2(84f, 108f),
                    new Vector2(100f, 52f)));
                break;
        }

        path.Figures.Add(figure);
        return path;
    }

    private static Matrix4x4 CreateAffineTransform()
    {
        var affine = Matrix4x4.CreateScale(1.25f, 0.7f, 1f);
        affine.M21 += 0.45f;
        return Matrix4x4.CreateTranslation(-64f, -64f, 0f) *
            affine *
            Matrix4x4.CreateTranslation(64f, 64f, 0f);
    }

    private static byte[] Render(RenderCommand command)
    {
        using var window = new HeadlessWindow(SurfaceSize, SurfaceSize);
        window.Content = new CommandVisual(command);
        window.Render();
        return window.ReadPixels();
    }

    private sealed class CommandVisual : FrameworkElement
    {
        private readonly RenderCommand _command;

        public CommandVisual(RenderCommand command)
        {
            _command = command;
            Width = SurfaceSize;
            Height = SurfaceSize;
        }

        public override void OnRender(DrawingContext context) =>
            context.Commands.Add(_command);
    }

    public enum DirectCapStrokeKind
    {
        Line,
        Quadratic,
        Cubic
    }
}
