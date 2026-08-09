using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class DashedAnalyticPrimitiveTests
{
    private const uint SurfaceSize = 96;
    private static readonly Rect PrimitiveBounds = new(16f, 16f, 56f, 48f);
    private static readonly Vector2 PrimitiveCenter = new(44f, 40f);
    private static readonly Matrix4x4 AffineTransform = new(
        1.05f, 0.18f, 0f, 0f,
        -0.22f, 0.78f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 4f, 0f, 1f);
    private static readonly AnalyticPrimitiveKind[] AllKinds =
    [
        AnalyticPrimitiveKind.Rectangle,
        AnalyticPrimitiveKind.Ellipse,
        AnalyticPrimitiveKind.Circle,
        AnalyticPrimitiveKind.CircularRoundedRectangle,
        AnalyticPrimitiveKind.EllipticalRoundedRectangle
    ];

    public static TheoryData<AnalyticPrimitiveKind> PrimitiveKinds => new()
    {
        AnalyticPrimitiveKind.Rectangle,
        AnalyticPrimitiveKind.Ellipse,
        AnalyticPrimitiveKind.Circle,
        AnalyticPrimitiveKind.CircularRoundedRectangle,
        AnalyticPrimitiveKind.EllipticalRoundedRectangle
    };

    public static TheoryData<AnalyticPrimitiveKind, bool> PrimitiveTransformCases => new()
    {
        { AnalyticPrimitiveKind.Rectangle, false },
        { AnalyticPrimitiveKind.Rectangle, true },
        { AnalyticPrimitiveKind.Ellipse, false },
        { AnalyticPrimitiveKind.Ellipse, true },
        { AnalyticPrimitiveKind.Circle, false },
        { AnalyticPrimitiveKind.Circle, true },
        { AnalyticPrimitiveKind.CircularRoundedRectangle, false },
        { AnalyticPrimitiveKind.CircularRoundedRectangle, true },
        { AnalyticPrimitiveKind.EllipticalRoundedRectangle, false },
        { AnalyticPrimitiveKind.EllipticalRoundedRectangle, true }
    };

    [Theory]
    [MemberData(nameof(PrimitiveKinds))]
    public void RecorderRetainsSourcePathForDashedAnalyticPrimitive(
        AnalyticPrimitiveKind kind)
    {
        var context = new DrawingContext();

        RecordPrimitive(context, kind, brush: null, CreateDashPen());

        RenderCommand command = Assert.Single(context.Commands);
        Assert.Equal(GetCommandType(kind), command.Type);
        Assert.NotNull(command.GeometryCache?.StrokePath);
        Assert.NotEmpty(command.GeometryCache!.StrokePath!.Figures);
    }

    [Theory]
    [MemberData(nameof(PrimitiveTransformCases))]
    public void DashedAnalyticPrimitiveMatchesEquivalentPath(
        AnalyticPrimitiveKind kind,
        bool useAffineTransform)
    {
        Matrix4x4 transform = useAffineTransform
            ? AffineTransform
            : Matrix4x4.Identity;
        using var window = CreateWindow();

        window.Content = new AnalyticPrimitiveVisual(kind, transform);
        window.Render();
        byte[] analyticPixels = window.ReadPixels();

        window.Content = new PathPrimitiveVisual(kind, transform);
        window.Render();
        byte[] pathPixels = window.ReadPixels();

        AssertPixelBuffersEqual(pathPixels, analyticPixels, kind, useAffineTransform);
    }

    [Theory]
    [MemberData(nameof(PrimitiveKinds))]
    public void DashedAnalyticPrimitiveUsesPathStrokeHitPrimitives(
        AnalyticPrimitiveKind kind)
    {
        var context = new DrawingContext();
        RecordPrimitive(context, kind, brush: null, CreateDashPen());
        RenderCommand command = Assert.Single(context.Commands);
        using var builder = new GpuRenderCommandHitTestCacheBuilder();

        builder.AddCommand(command, Matrix4x4.Identity, id: 913);
        GpuHitTestIndex index = builder.BuildIndex(
            maxDepth: 2,
            maxPrimitivesPerNode: 1);

        Assert.NotEmpty(index.Primitives);
        Assert.All(index.Primitives, primitive =>
        {
            Assert.Equal(GpuHitTestPrimitiveKind.PathStroke, primitive.Kind);
            Assert.Equal(913, primitive.Id);
        });
        Assert.NotEmpty(index.PathSegments);
    }

    [Fact]
    public void DashedRectangleGpuHitTestRejectsPatternGap()
    {
        using var gpu = new WgpuContext();
        gpu.Initialize(null);
        var context = new DrawingContext();
        RecordPrimitive(
            context,
            AnalyticPrimitiveKind.Rectangle,
            brush: null,
            CreateDashPen());
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(
            Assert.Single(context.Commands),
            Matrix4x4.Identity,
            id: 914);
        GpuHitTestIndex index = builder.BuildIndex(
            maxDepth: 2,
            maxPrimitivesPerNode: 1);

        bool dashHit = GpuHitTestEngine.TryHitTestPoint(
            gpu,
            index,
            new Vector2(24f, 16f),
            out GpuHitTestResult dashResult);
        bool gapHit = GpuHitTestEngine.TryHitTestPoint(
            gpu,
            index,
            new Vector2(36f, 16f),
            out GpuHitTestResult gapResult);

        Assert.True(dashHit);
        Assert.Equal(914, dashResult.Id);
        Assert.False(gapHit);
        Assert.False(gapResult.HasHit);
    }

    [Fact]
    public void PictureArchiveRestoresDashedAnalyticStrokeCaches()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext context = recorder.BeginRecording(
            new Rect(0f, 0f, SurfaceSize, SurfaceSize));
        foreach (AnalyticPrimitiveKind kind in AllKinds)
        {
            RecordPrimitive(context, kind, brush: null, CreateDashPen());
        }

        using GpuPicture gpuPicture = recorder.EndRecording();
        using var picture = new SKPicture(
            gpuPicture,
            new SKRect(0f, 0f, SurfaceSize, SurfaceSize));
        using SKData data = picture.Serialize();
        using SKPicture? copy = SKPicture.Deserialize(data);

        Assert.NotNull(copy);
        IReadOnlyList<RenderCommand> commands = copy.Picture.Commands;
        Assert.Equal(AllKinds.Length, commands.Count);
        for (var index = 0; index < commands.Count; index++)
        {
            RenderCommand command = commands[index];
            Assert.Equal(GetCommandType(AllKinds[index]), command.Type);
            RenderCommandGeometryCache cache = Assert.IsType<RenderCommandGeometryCache>(
                command.GeometryCache);
            Assert.NotNull(cache.StrokePath);
            Assert.True(cache.TryGetDashedStrokePath(
                command.Pen!,
                out PathGeometry firstPath,
                out Pen firstPen));
            Assert.True(cache.TryGetDashedStrokePath(
                command.Pen!,
                out PathGeometry secondPath,
                out Pen secondPen));
            Assert.Same(firstPath, secondPath);
            Assert.Same(firstPen, secondPen);
        }
    }

    [Fact]
    public void FilledDashedRectangleDoesNotEmitContinuousAnalyticStroke()
    {
        using var window = CreateWindow();
        window.Content = new FilledDashedRectangleVisual();

        window.Render();

        Assert.DoesNotContain(
            window.Compositor.VectorVertices,
            static vertex => vertex.ShapeType == 0f && vertex.StrokeThickness > 0f);
        byte[] pixels = window.ReadPixels();
        AssertRed(ReadPixel(pixels, x: 24, y: 17));
        AssertBlue(ReadPixel(pixels, x: 36, y: 17));
    }

    private static HeadlessWindow CreateWindow()
    {
        var options = CompositorOptions.Default with
        {
            EnableGpuHitTesting = false,
            EnableCompiledSceneCache = false,
            EnableIncrementalScenePages = false,
            PrimarySampleCount = 1
        };
        var window = new HeadlessWindow(
            SurfaceSize,
            SurfaceSize,
            options);
        window.Compositor.ClearColor = new Vector4(0f, 0f, 0f, 1f);
        return window;
    }

    private static Pen CreateDashPen() => new(
        new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
        thickness: 4f,
        lineJoin: PenLineJoin.Round,
        miterLimit: 8f,
        startLineCap: PenLineCap.Flat,
        endLineCap: PenLineCap.Flat,
        dashCap: PenLineCap.Flat,
        dashArray: [4d, 2d],
        dashOffset: 0d);

    private static void RecordPrimitive(
        DrawingContext context,
        AnalyticPrimitiveKind kind,
        Brush? brush,
        Pen pen)
    {
        switch (kind)
        {
            case AnalyticPrimitiveKind.Rectangle:
                context.DrawRectangle(brush, pen, PrimitiveBounds);
                break;
            case AnalyticPrimitiveKind.Ellipse:
                context.DrawEllipse(
                    brush,
                    pen,
                    PrimitiveCenter,
                    radiusX: 28f,
                    radiusY: 24f);
                break;
            case AnalyticPrimitiveKind.Circle:
                context.DrawCircle(brush, pen, PrimitiveCenter, radius: 24f);
                break;
            case AnalyticPrimitiveKind.CircularRoundedRectangle:
                context.DrawRoundedRectangle(
                    brush,
                    pen,
                    PrimitiveBounds,
                    radiusX: 10f,
                    radiusY: 10f);
                break;
            case AnalyticPrimitiveKind.EllipticalRoundedRectangle:
                context.DrawRoundedRectangle(
                    brush,
                    pen,
                    PrimitiveBounds,
                    radiusX: 14f,
                    radiusY: 7f);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static PathGeometry CreatePrimitivePath(AnalyticPrimitiveKind kind)
    {
        return kind switch
        {
            AnalyticPrimitiveKind.Rectangle =>
                PrimitivePathGeometry.CreateRectangle(
                    PrimitiveBounds.X,
                    PrimitiveBounds.Y,
                    PrimitiveBounds.Width,
                    PrimitiveBounds.Height),
            AnalyticPrimitiveKind.Ellipse =>
                PrimitivePathGeometry.CreateEllipse(
                    PrimitiveCenter,
                    radiusX: 28f,
                    radiusY: 24f),
            AnalyticPrimitiveKind.Circle =>
                PrimitivePathGeometry.CreateEllipse(
                    PrimitiveCenter,
                    radiusX: 24f,
                    radiusY: 24f),
            AnalyticPrimitiveKind.CircularRoundedRectangle =>
                PrimitivePathGeometry.CreateRoundedRectangle(
                    PrimitiveBounds.X,
                    PrimitiveBounds.Y,
                    PrimitiveBounds.Width,
                    PrimitiveBounds.Height,
                    radiusX: 10f,
                    radiusY: 10f),
            AnalyticPrimitiveKind.EllipticalRoundedRectangle =>
                PrimitivePathGeometry.CreateRoundedRectangle(
                    PrimitiveBounds.X,
                    PrimitiveBounds.Y,
                    PrimitiveBounds.Width,
                    PrimitiveBounds.Height,
                    radiusX: 14f,
                    radiusY: 7f),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static RenderCommandType GetCommandType(AnalyticPrimitiveKind kind)
    {
        return kind switch
        {
            AnalyticPrimitiveKind.Rectangle => RenderCommandType.DrawRect,
            AnalyticPrimitiveKind.Ellipse => RenderCommandType.DrawEllipse,
            AnalyticPrimitiveKind.Circle => RenderCommandType.DrawCircle,
            AnalyticPrimitiveKind.CircularRoundedRectangle or
            AnalyticPrimitiveKind.EllipticalRoundedRectangle =>
                RenderCommandType.DrawRoundedRect,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static void AssertPixelBuffersEqual(
        byte[] expected,
        byte[] actual,
        AnalyticPrimitiveKind kind,
        bool affine)
    {
        Assert.Equal(expected.Length, actual.Length);
        var differingBytes = 0;
        var maximumDifference = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            int difference = Math.Abs(expected[index] - actual[index]);
            if (difference == 0)
            {
                continue;
            }

            differingBytes++;
            maximumDifference = Math.Max(maximumDifference, difference);
        }

        Assert.True(
            differingBytes == 0,
            $"{kind} ({(affine ? "affine" : "identity")}) differed from DrawPath " +
            $"in {differingBytes} bytes; maximum channel difference {maximumDifference}.");
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
    }

    private static void AssertBlue(RgbaPixel pixel)
    {
        Assert.InRange(pixel.Red, (byte)0, (byte)35);
        Assert.InRange(pixel.Green, (byte)0, (byte)35);
        Assert.InRange(pixel.Blue, (byte)220, byte.MaxValue);
    }

    private readonly record struct RgbaPixel(byte Red, byte Green, byte Blue, byte Alpha);

    public enum AnalyticPrimitiveKind
    {
        Rectangle,
        Ellipse,
        Circle,
        CircularRoundedRectangle,
        EllipticalRoundedRectangle
    }

    private sealed class AnalyticPrimitiveVisual : FrameworkElement
    {
        private readonly AnalyticPrimitiveKind _kind;
        private readonly Pen _pen = CreateDashPen();

        public AnalyticPrimitiveVisual(
            AnalyticPrimitiveKind kind,
            Matrix4x4 transform)
        {
            _kind = kind;
            Width = SurfaceSize;
            Height = SurfaceSize;
            Transform = transform;
        }

        public override void OnRender(DrawingContext context) =>
            RecordPrimitive(context, _kind, brush: null, _pen);
    }

    private sealed class PathPrimitiveVisual : FrameworkElement
    {
        private readonly PathGeometry _path;
        private readonly Pen _pen = CreateDashPen();

        public PathPrimitiveVisual(
            AnalyticPrimitiveKind kind,
            Matrix4x4 transform)
        {
            _path = CreatePrimitivePath(kind);
            Width = SurfaceSize;
            Height = SurfaceSize;
            Transform = transform;
        }

        public override void OnRender(DrawingContext context) =>
            context.DrawPath(brush: null, _pen, _path);
    }

    private sealed class FilledDashedRectangleVisual : FrameworkElement
    {
        private readonly SolidColorBrush _fill = new(new Vector4(0f, 0f, 1f, 1f));
        private readonly Pen _pen = CreateDashPen();

        public override void OnRender(DrawingContext context) =>
            context.DrawRectangle(_fill, _pen, PrimitiveBounds);
    }
}
