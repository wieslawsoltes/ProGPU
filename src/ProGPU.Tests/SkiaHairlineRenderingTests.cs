using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkiaHairlineRenderingTests
{
    private const int SurfaceSize = 128;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnisotropicDirectLineRemainsOneDevicePixel(
        bool vertical,
        bool useStaticBuffer)
    {
        var transform = ScaleAboutCenter(2f, 0.5f);
        var pixels = useStaticBuffer
            ? RenderStatic(CreateLineCommand(vertical), transform)
            : RenderDynamic(CreateGpuLineCommand(vertical, transform));
        var crossSection = vertical
            ? CountPaintedColumns(pixels, y: SurfaceSize / 2)
            : CountPaintedRows(pixels, x: SurfaceSize / 2);

        Assert.InRange(crossSection, 1, 2);
    }

    [Theory]
    [InlineData(HairlinePrimitiveKind.Quadratic)]
    [InlineData(HairlinePrimitiveKind.Cubic)]
    [InlineData(HairlinePrimitiveKind.Rectangle)]
    [InlineData(HairlinePrimitiveKind.Ellipse)]
    [InlineData(HairlinePrimitiveKind.RoundedRectangle)]
    [InlineData(HairlinePrimitiveKind.Arc)]
    public void DynamicGpuHairlineMatchesCpuBakedReference(
        HairlinePrimitiveKind kind)
    {
        var transform = ScaleAndShearAboutCenter(1.75f, 0.55f, 0.4f);
        var actual = CreatePrimitiveCommand(kind);
        actual.UseGpuTransforms = true;
        actual.CameraView = transform;
        var expected = CreatePrimitiveCommand(kind);
        expected.Transform = transform;
        expected.IsPenThicknessLocal = true;

        AssertImagesNear(RenderDynamic(expected), RenderDynamic(actual));
    }

    [Fact]
    public void HairlineRoundedRectangleNeverEntersFillOnlySolidSpecialization()
    {
        using var window = new HeadlessWindow(SurfaceSize, SurfaceSize);
        window.Content = new RoundedSpecializationPrimerVisual();
        window.Render();

        var hairline = CreatePrimitiveCommand(
            HairlinePrimitiveKind.RoundedRectangle);
        window.Content = new CommandVisual(hairline);
        window.Render();
        var pixels = window.ReadPixels();

        Assert.True(CountPaintedPixels(pixels) > 0);
        var center = pixels[((64 * SurfaceSize + 64) * 4) + 2];
        var background = pixels[((8 * SurfaceSize + 8) * 4) + 2];
        Assert.InRange(Math.Abs(center - background), 0, 1);
    }

    [Fact]
    public void DynamicGpuPathHairlineRegeneratesRoundCapsAndJoinInDeviceSpace()
    {
        var transform = ScaleAndShearAboutCenter(2f, 0.5f, 0.45f);
        var actual = CreateJoinedPathCommand();
        actual.UseGpuTransforms = true;
        actual.CameraView = transform;
        var expected = CreateJoinedPathCommand();
        expected.Transform = transform;
        expected.IsPenThicknessLocal = true;

        var actualPixels = RenderDynamic(actual);
        var expectedPixels = RenderDynamic(expected);

        Assert.True(CountPaintedPixels(expectedPixels) > 0);
        AssertImagesNear(expectedPixels, actualPixels, maximumDifferentPixels: 0);
    }

    [Fact]
    public void DynamicGpuPictureRegeneratesNestedHairlinePathInDeviceSpace()
    {
        var transform = ScaleAndShearAboutCenter(1.9f, 0.55f, 0.3f);
        using var picture = new GpuPicture(
            [CreateJoinedPathCommand()],
            [],
            [],
            [],
            []);
        var actual = new RenderCommand
        {
            Type = RenderCommandType.DrawPicture,
            Picture = picture,
            UseGpuTransforms = true,
            CameraView = transform
        };
        var expected = new RenderCommand
        {
            Type = RenderCommandType.DrawPicture,
            Picture = picture,
            Transform = transform
        };

        AssertImagesNear(
            RenderDynamic(expected),
            RenderDynamic(actual),
            maximumDifferentPixels: 0);
    }

    [Fact]
    public void StaticLateAffineArcHairlineMatchesDynamicDeviceHairline()
    {
        var transform = ScaleAndShearAboutCenter(1.8f, 0.6f, 0.35f);
        var staticPixels = RenderStatic(
            CreatePrimitiveCommand(HairlinePrimitiveKind.Arc),
            transform);
        var dynamicCommand = CreatePrimitiveCommand(HairlinePrimitiveKind.Arc);
        dynamicCommand.UseGpuTransforms = true;
        dynamicCommand.CameraView = transform;
        var dynamicPixels = RenderDynamic(dynamicCommand);

        Assert.True(CountPaintedPixels(staticPixels) > 0);
        AssertImagesNear(
            dynamicPixels,
            staticPixels,
            maximumDifferentPixels: 48);
    }

    [Fact]
    public void StaticLateDownscaledArcHairlineRetainsDevicePixelHalo()
    {
        var transform = ScaleAndShearAboutCenter(0.22f, 0.12f, 0.35f);
        var staticPixels = RenderStatic(
            CreatePrimitiveCommand(HairlinePrimitiveKind.Arc),
            transform);
        var dynamicCommand = CreatePrimitiveCommand(HairlinePrimitiveKind.Arc);
        dynamicCommand.Transform = transform;
        dynamicCommand.IsPenThicknessLocal = true;
        var dynamicPixels = RenderDynamic(dynamicCommand);

        Assert.True(CountPaintedPixels(dynamicPixels) > 0);
        AssertImagesNear(
            dynamicPixels,
            staticPixels,
            maximumDifferentPixels: 0);
    }

    [Theory]
    [InlineData(PenLineCap.Flat)]
    [InlineData(PenLineCap.Square)]
    [InlineData(PenLineCap.Round)]
    [InlineData(PenLineCap.Triangle)]
    public void StaticLateHairlineCapMatchesDeviceSpaceReference(
        PenLineCap lineCap)
    {
        var transform = ScaleAndShearAboutCenter(1.9f, 0.45f, 0.55f);
        var command = CreateCappedPathCommand(lineCap);
        var expected = command;
        expected.Transform = transform;
        expected.IsPenThicknessLocal = true;

        var staticPixels = RenderStatic(command, transform);
        var expectedPixels = RenderDynamic(expected);

        Assert.True(CountPaintedPixels(expectedPixels) > 0);
        AssertImagesNear(expectedPixels, staticPixels, maximumDifferentPixels: 0);
    }

    [Theory]
    [InlineData(PenLineCap.Flat)]
    [InlineData(PenLineCap.Square)]
    [InlineData(PenLineCap.Round)]
    [InlineData(PenLineCap.Triangle)]
    public void IdentityHairlineCapMatchesOrdinaryOnePixelGeometry(
        PenLineCap lineCap)
    {
        var hairlinePixels = RenderDynamic(CreateCappedPathCommand(lineCap));
        var onePixelPixels = RenderDynamic(CreateCappedPathCommand(
            lineCap,
            thickness: 1f));

        AssertImagesNear(
            onePixelPixels,
            hairlinePixels,
            maximumDifferentPixels: 12);
    }

    [Theory]
    [InlineData(PenLineJoin.Miter)]
    [InlineData(PenLineJoin.Bevel)]
    [InlineData(PenLineJoin.Round)]
    public void StaticLateHairlineJoinMatchesDeviceSpaceReference(
        PenLineJoin lineJoin)
    {
        var transform = ScaleAndShearAboutCenter(1.85f, 0.5f, 0.5f);
        var command = CreateJoinedPathCommand(lineJoin, PenLineCap.Flat);
        var expected = command;
        expected.Transform = transform;
        expected.IsPenThicknessLocal = true;

        var staticPixels = RenderStatic(command, transform);
        var expectedPixels = RenderDynamic(expected);

        Assert.True(CountPaintedPixels(expectedPixels) > 0);
        AssertImagesNear(expectedPixels, staticPixels, maximumDifferentPixels: 0);
    }

    [Theory]
    [InlineData(PenLineJoin.Miter)]
    [InlineData(PenLineJoin.Bevel)]
    [InlineData(PenLineJoin.Round)]
    public void IdentityHairlineJoinMatchesOrdinaryOnePixelGeometry(
        PenLineJoin lineJoin)
    {
        var hairlinePixels = RenderDynamic(CreateAxisAlignedJoinedPathCommand(
            lineJoin,
            thickness: Pen.HairlineThickness));
        var onePixelPixels = RenderDynamic(CreateAxisAlignedJoinedPathCommand(
            lineJoin,
            thickness: 1f));

        AssertImagesNear(
            onePixelPixels,
            hairlinePixels,
            maximumDifferentPixels: 12);
    }

    [Fact]
    public void DynamicGpuDashedHairlineRegeneratesDashCapsInDeviceSpace()
    {
        var transform = ScaleAndShearAboutCenter(2f, 0.5f, 0.4f);
        var actual = CreateDashedPathCommand();
        actual.UseGpuTransforms = true;
        actual.CameraView = transform;
        var expected = CreateDashedPathCommand();
        expected.Transform = transform;
        expected.IsPenThicknessLocal = true;

        AssertImagesNear(
            RenderDynamic(expected),
            RenderDynamic(actual),
            maximumDifferentPixels: 0);
    }

    [Fact]
    public void DynamicGpuUnequalRoundedRectangleHairlineUsesExactPathFallback()
    {
        var transform = ScaleAndShearAboutCenter(1.8f, 0.5f, 0.45f);
        var actual = new RenderCommand
        {
            Type = RenderCommandType.DrawRoundedRect,
            Rect = new Rect(28f, 34f, 72f, 58f),
            RadiusX = 16f,
            RadiusY = 9f,
            Pen = CreateHairlinePen(),
            IsPenThicknessLocal = true,
            UseGpuTransforms = true,
            CameraView = transform
        };
        var expected = actual;
        expected.UseGpuTransforms = false;
        expected.CameraView = default;
        expected.Transform = transform;

        AssertImagesNear(
            RenderDynamic(expected),
            RenderDynamic(actual),
            maximumDifferentPixels: 0);
    }

    [Fact]
    public void StaticHairlineArcRecordsTwoUnitHaloAndLateScaleExpansion()
    {
        var command = CreatePrimitiveCommand(HairlinePrimitiveKind.Arc);
        var figure = Assert.Single(command.Path!.Figures);
        var arc = Assert.IsType<ArcSegment>(Assert.Single(figure.Segments));
        Assert.True(ArcSegmentGeometry.TryGetArcBounds(
            figure.StartPoint,
            arc,
            out var arcMin,
            out var arcMax));

        using var window = new HeadlessWindow(SurfaceSize, SurfaceSize);
        var context = new DrawingContext();
        context.Commands.Add(command);
        using var buffer = window.Compositor.CompileStaticDxf(context);
        var vertexMin = new Vector2(float.PositiveInfinity);
        var vertexMax = new Vector2(float.NegativeInfinity);
        var arcVertexCount = 0;
        foreach (var vertex in buffer.VectorVertices)
        {
            var shapeType = vertex.ShapeType >= 195f
                ? vertex.ShapeType - 200f
                : vertex.ShapeType;
            if (MathF.Abs(shapeType - 12f) > 0.01f)
            {
                continue;
            }

            Assert.Equal(Pen.HairlineThickness, vertex.StrokeThickness);
            vertexMin = Vector2.Min(vertexMin, vertex.Position);
            vertexMax = Vector2.Max(vertexMax, vertex.Position);
            arcVertexCount++;
        }

        Assert.Equal(4, arcVertexCount);
        Assert.Equal(arcMin.X - 2f, vertexMin.X, 3);
        Assert.Equal(arcMin.Y - 2f, vertexMin.Y, 3);
        Assert.Equal(arcMax.X + 2f, vertexMax.X, 3);
        Assert.Equal(arcMax.Y + 2f, vertexMax.Y, 3);
        Assert.Contains(
            "2.0 / directStrokeScales.y - 2.0",
            Shaders.VectorShader);
    }

    [Fact]
    public void HairlineRoundCapsAndJoinUseOneFixedQuadPerAdornment()
    {
        var command = CreateJoinedPathCommand();
        using var window = new HeadlessWindow(SurfaceSize, SurfaceSize);
        var context = new DrawingContext();
        context.Commands.Add(command);
        using var buffer = window.Compositor.CompileStaticDxf(context);
        var capVertexCount = 0;
        var joinVertexCount = 0;
        foreach (var vertex in buffer.VectorVertices)
        {
            var shapeType = vertex.ShapeType >= 195f
                ? vertex.ShapeType - 200f
                : vertex.ShapeType;
            if (MathF.Abs(shapeType - 22f) <= 0.01f)
            {
                capVertexCount++;
            }
            else if (MathF.Abs(shapeType - 23f) <= 0.01f)
            {
                joinVertexCount++;
            }
        }

        Assert.Equal(8, capVertexCount);
        Assert.Equal(4, joinVertexCount);
    }

    [Fact]
    public void NegativePolygonPayloadCoordinateDoesNotBecomeHairlineSentinel()
    {
        Assert.Contains(
            "(isAnalyticSdf || isDirect2DStroke) &&\n        input.strokeThickness == -1.0;",
            Shaders.VectorShader);

        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(-24f, -12f));
        figure.Segments.Add(new LineSegment(new Vector2(0f, -38f)));
        figure.Segments.Add(new LineSegment(new Vector2(26f, -8f)));
        path.Figures.Add(figure);
        var transform = Matrix4x4.CreateScale(1.6f, 0.7f, 1f) *
            Matrix4x4.CreateTranslation(64f, 64f, 0f);
        var actual = new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = new Pen(
                CreateBrush(),
                8f,
                PenLineJoin.Round,
                startLineCap: PenLineCap.Round,
                endLineCap: PenLineCap.Round),
            IsPenThicknessLocal = true,
            UseGpuTransforms = true,
            CameraView = transform
        };
        var expected = actual;
        expected.UseGpuTransforms = false;
        expected.CameraView = default;
        expected.Transform = transform;

        var actualPixels = RenderDynamic(actual);
        Assert.True(CountPaintedPixels(actualPixels) > 0);
        AssertImagesNear(
            RenderDynamic(expected),
            actualPixels,
            maximumDifferentPixels: 256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GpuHitTestHairlineUsesOnePixelScreenSpaceWidth(bool vertical)
    {
        var transform = Matrix4x4.CreateScale(4f, 0.5f, 1f);
        var command = CreateLineCommand(vertical);
        var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(command, transform, id: 41);
        var index = builder.BuildIndex();

        using var context = new WgpuContext();
        context.Initialize(null);
        var center = vertical
            ? Vector2.Transform(new Vector2(64f, 64f), transform)
            : Vector2.Transform(new Vector2(64f, 64f), transform);
        var inside = center + (vertical
            ? new Vector2(0.49f, 0f)
            : new Vector2(0f, 0.49f));
        var outside = center + (vertical
            ? new Vector2(0.75f, 0f)
            : new Vector2(0f, 0.75f));

        Assert.True(GpuHitTestEngine.TryHitTestPoint(
            context,
            index,
            inside,
            out var result));
        Assert.Equal(41, result.Id);
        Assert.False(GpuHitTestEngine.TryHitTestPoint(
            context,
            index,
            outside,
            out _));
    }

    [Fact]
    public void OrdinaryZeroAndNegativePensRemainNonRenderingAndNonHittable()
    {
        foreach (var thickness in new[] { 0f, -2f })
        {
            var command = new RenderCommand
            {
                Type = RenderCommandType.DrawLine,
                Position = new Vector2(16f, 64f),
                Position2 = new Vector2(112f, 64f),
                Pen = new Pen(CreateBrush(), thickness),
                IsPenThicknessLocal = true
            };
            Assert.Equal(0, CountPaintedPixels(RenderDynamic(command)));

            var builder = new GpuRenderCommandHitTestCacheBuilder();
            builder.AddCommand(command, Matrix4x4.Identity);
            Assert.Equal(0, builder.PrimitiveCount);
        }
    }

    private static RenderCommand CreateLineCommand(bool vertical)
    {
        return new RenderCommand
        {
            Type = RenderCommandType.DrawLine,
            Position = vertical
                ? new Vector2(64f, 20f)
                : new Vector2(20f, 64f),
            Position2 = vertical
                ? new Vector2(64f, 108f)
                : new Vector2(108f, 64f),
            Pen = CreateHairlinePen(),
            IsPenThicknessLocal = true
        };
    }

    private static RenderCommand CreateGpuLineCommand(
        bool vertical,
        Matrix4x4 transform)
    {
        var command = CreateLineCommand(vertical);
        command.UseGpuTransforms = true;
        command.CameraView = transform;
        return command;
    }

    private static RenderCommand CreatePrimitiveCommand(HairlinePrimitiveKind kind)
    {
        var command = new RenderCommand
        {
            Pen = CreateHairlinePen(),
            IsPenThicknessLocal = true
        };
        switch (kind)
        {
            case HairlinePrimitiveKind.Quadratic:
                command.Type = RenderCommandType.DrawBezier;
                command.Position = new Vector2(24f, 76f);
                command.Position2 = new Vector2(64f, 24f);
                command.Position3 = new Vector2(104f, 76f);
                break;
            case HairlinePrimitiveKind.Cubic:
                command.Type = RenderCommandType.DrawCubicBezier;
                command.Position = new Vector2(24f, 80f);
                command.Position2 = new Vector2(40f, 18f);
                command.Position3 = new Vector2(88f, 110f);
                command.Position4 = new Vector2(104f, 48f);
                break;
            case HairlinePrimitiveKind.Rectangle:
                command.Type = RenderCommandType.DrawRect;
                command.Rect = new Rect(30f, 34f, 68f, 56f);
                break;
            case HairlinePrimitiveKind.Ellipse:
                command.Type = RenderCommandType.DrawEllipse;
                command.Position2 = new Vector2(64f, 64f);
                command.RadiusX = 34f;
                command.RadiusY = 25f;
                break;
            case HairlinePrimitiveKind.RoundedRectangle:
                command.Type = RenderCommandType.DrawRoundedRect;
                command.Rect = new Rect(30f, 34f, 68f, 56f);
                command.RadiusX = 12f;
                command.RadiusY = 12f;
                break;
            case HairlinePrimitiveKind.Arc:
                var path = new PathGeometry();
                var figure = new PathFigure(new Vector2(32f, 64f));
                figure.Segments.Add(new ArcSegment(
                    new Vector2(96f, 64f),
                    new Vector2(32f, 26f),
                    rotationAngle: 18f,
                    isLargeArc: false,
                    SweepDirection.Clockwise));
                path.Figures.Add(figure);
                command.Type = RenderCommandType.DrawPath;
                command.Path = path;
                command.GeometryCache = RenderCommandGeometryCache.ForPath(path);
                break;
        }

        return command;
    }

    private static RenderCommand CreateCappedPathCommand(
        PenLineCap lineCap,
        float thickness = Pen.HairlineThickness)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(30f, 64f));
        figure.Segments.Add(new LineSegment(new Vector2(98f, 64f)));
        path.Figures.Add(figure);
        return new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = new Pen(
                CreateBrush(),
                thickness,
                startLineCap: lineCap,
                endLineCap: lineCap),
            IsPenThicknessLocal = true
        };
    }

    private static RenderCommand CreateJoinedPathCommand(
        PenLineJoin lineJoin = PenLineJoin.Round,
        PenLineCap lineCap = PenLineCap.Round,
        float thickness = Pen.HairlineThickness)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(26f, 82f));
        figure.Segments.Add(new LineSegment(new Vector2(62f, 34f)));
        figure.Segments.Add(new LineSegment(new Vector2(102f, 78f)));
        path.Figures.Add(figure);
        return new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = new Pen(
                CreateBrush(),
                thickness,
                lineJoin,
                startLineCap: lineCap,
                endLineCap: lineCap),
            IsPenThicknessLocal = true
        };
    }

    private static RenderCommand CreateDashedPathCommand()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(18f, 64f));
        figure.Segments.Add(new LineSegment(new Vector2(110f, 64f)));
        path.Figures.Add(figure);
        return new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = new Pen(
                CreateBrush(),
                Pen.HairlineThickness,
                dashCap: PenLineCap.Round,
                dashArray: new double[] { 7d, 4d }),
            IsPenThicknessLocal = true
        };
    }

    private static RenderCommand CreateAxisAlignedJoinedPathCommand(
        PenLineJoin lineJoin,
        float thickness)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(28f, 64f));
        figure.Segments.Add(new LineSegment(new Vector2(64f, 64f)));
        figure.Segments.Add(new LineSegment(new Vector2(64f, 28f)));
        path.Figures.Add(figure);
        return new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = new Pen(
                CreateBrush(),
                thickness,
                lineJoin,
                startLineCap: PenLineCap.Flat,
                endLineCap: PenLineCap.Flat),
            IsPenThicknessLocal = true
        };
    }

    private static byte[] RenderDynamic(RenderCommand command)
    {
        using var window = new HeadlessWindow(SurfaceSize, SurfaceSize);
        window.Content = new CommandVisual(command);
        window.Render();
        return window.ReadPixels();
    }

    private static byte[] RenderStatic(RenderCommand command, Matrix4x4 transform)
    {
        using var window = new HeadlessWindow(SurfaceSize, SurfaceSize);
        var context = new DrawingContext();
        context.Commands.Add(command);
        using var buffer = window.Compositor.CompileStaticDxf(context);
        window.Content = new StaticVisual(buffer, transform);
        window.Render();
        return window.ReadPixels();
    }

    private static Matrix4x4 ScaleAboutCenter(float scaleX, float scaleY)
    {
        return Matrix4x4.CreateTranslation(-64f, -64f, 0f) *
            Matrix4x4.CreateScale(scaleX, scaleY, 1f) *
            Matrix4x4.CreateTranslation(64f, 64f, 0f);
    }

    private static Matrix4x4 ScaleAndShearAboutCenter(
        float scaleX,
        float scaleY,
        float shearX)
    {
        var shear = Matrix4x4.Identity;
        shear.M21 = shearX;
        return Matrix4x4.CreateTranslation(-64f, -64f, 0f) *
            Matrix4x4.CreateScale(scaleX, scaleY, 1f) *
            shear *
            Matrix4x4.CreateTranslation(64f, 64f, 0f);
    }

    private static Pen CreateHairlinePen() => new(
        CreateBrush(),
        Pen.HairlineThickness);

    private static SolidColorBrush CreateBrush() => new(
        new Vector4(0f, 0f, 1f, 1f));

    private static int CountPaintedRows(byte[] pixels, int x)
    {
        var count = 0;
        for (var y = 0; y < SurfaceSize; y++)
        {
            if (pixels[(y * SurfaceSize + x) * 4 + 2] > 128)
            {
                count++;
            }
        }
        return count;
    }

    private static int CountPaintedColumns(byte[] pixels, int y)
    {
        var count = 0;
        for (var x = 0; x < SurfaceSize; x++)
        {
            if (pixels[(y * SurfaceSize + x) * 4 + 2] > 128)
            {
                count++;
            }
        }
        return count;
    }

    private static int CountPaintedPixels(byte[] pixels)
    {
        var count = 0;
        for (var index = 2; index < pixels.Length; index += 4)
        {
            if (pixels[index] > 128)
            {
                count++;
            }
        }
        return count;
    }

    private static void AssertImagesNear(
        byte[] expected,
        byte[] actual,
        int maximumDifferentPixels = 24)
    {
        Assert.Equal(expected.Length, actual.Length);
        var differentPixels = 0;
        for (var index = 2; index < expected.Length; index += 4)
        {
            if (Math.Abs(expected[index] - actual[index]) > 8)
            {
                differentPixels++;
            }
        }

        Assert.True(
            differentPixels <= maximumDifferentPixels,
            $"Hairline render mismatch: {differentPixels} pixels differ.");
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

    private sealed class StaticVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _buffer;

        public StaticVisual(DxfStaticBuffer buffer, Matrix4x4 transform)
        {
            _buffer = buffer;
            Transform = transform;
            Width = SurfaceSize;
            Height = SurfaceSize;
        }

        public override void OnRender(DrawingContext context) =>
            context.DrawStaticDxf(_buffer);
    }

    private sealed class RoundedSpecializationPrimerVisual : FrameworkElement
    {
        private readonly Pen _pen = new(CreateBrush(), 2f);

        public RoundedSpecializationPrimerVisual()
        {
            Width = SurfaceSize;
            Height = SurfaceSize;
        }

        public override void OnRender(DrawingContext context)
        {
            for (var index = 0; index < 40; index++)
            {
                var column = index % 8;
                var row = index / 8;
                context.DrawRoundedRectangle(
                    null,
                    _pen,
                    new Rect(4f + column * 15f, 4f + row * 15f, 10f, 10f),
                    3f,
                    3f);
            }
        }
    }

    public enum HairlinePrimitiveKind
    {
        Quadratic,
        Cubic,
        Rectangle,
        Ellipse,
        RoundedRectangle,
        Arc
    }
}
