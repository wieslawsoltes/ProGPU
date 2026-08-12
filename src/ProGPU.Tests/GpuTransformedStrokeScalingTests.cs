using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Dxf;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class GpuTransformedStrokeScalingTests
{
    private const int SmallSurfaceSize = 64;
    private const int LargeSurfaceSize = 128;

    [Theory]
    [InlineData(DirectStrokeKind.Line)]
    [InlineData(DirectStrokeKind.Quadratic)]
    [InlineData(DirectStrokeKind.Cubic)]
    [InlineData(DirectStrokeKind.Arc)]
    public void StaticViewportTransformScalesDirectStrokeOnGpu(DirectStrokeKind kind)
    {
        using var window = new HeadlessWindow(SmallSurfaceSize, SmallSurfaceSize);
        var context = CreateStrokeContext(kind);
        using var buffer = window.Compositor.CompileStaticDxf(context);
        window.Content = new StaticStrokeVisual(
            buffer,
            ScaleAboutCenter(0.5f),
            SmallSurfaceSize);

        window.Render();

        Assert.InRange(
            CountPaintedRows(window.ReadPixels(), SmallSurfaceSize, x: 32),
            4,
            8);
    }

    [Theory]
    [InlineData(DirectStrokeKind.Line)]
    [InlineData(DirectStrokeKind.Quadratic)]
    [InlineData(DirectStrokeKind.Cubic)]
    [InlineData(DirectStrokeKind.Arc)]
    public void DynamicGpuTransformScalesDirectStrokeOnGpu(DirectStrokeKind kind)
    {
        using var window = new HeadlessWindow(SmallSurfaceSize, SmallSurfaceSize);
        window.Content = new GpuTransformStrokeVisual(
            kind,
            ScaleAboutCenter(0.5f),
            SmallSurfaceSize);

        window.Render();

        Assert.InRange(
            CountPaintedRows(window.ReadPixels(), SmallSurfaceSize, x: 32),
            4,
            8);
    }

    [Theory]
    [InlineData(DirectStrokeKind.Line)]
    [InlineData(DirectStrokeKind.Quadratic)]
    [InlineData(DirectStrokeKind.Cubic)]
    public void StaticAnisotropicTransformRetainsExactHorizontalAndVerticalOutlines(
        DirectStrokeKind kind)
    {
        AssertAnisotropicAxisStrokes(kind, useStaticBuffer: true);
    }

    [Theory]
    [InlineData(DirectStrokeKind.Line)]
    [InlineData(DirectStrokeKind.Quadratic)]
    [InlineData(DirectStrokeKind.Cubic)]
    public void DynamicAnisotropicTransformRetainsExactHorizontalAndVerticalOutlines(
        DirectStrokeKind kind)
    {
        AssertAnisotropicAxisStrokes(kind, useStaticBuffer: false);
    }

    [Fact]
    public void StaticAnisotropicTransformRetainsExactArcOutline()
    {
        AssertAnisotropicArc(useStaticBuffer: true);
    }

    [Fact]
    public void DynamicAnisotropicTransformRetainsExactArcOutline()
    {
        AssertAnisotropicArc(useStaticBuffer: false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShearRetainsLocalHorizontalStrokeOutline(bool useStaticBuffer)
    {
        using var window = new HeadlessWindow(LargeSurfaceSize, LargeSurfaceSize);
        var transform = ShearAboutCenter(0.75f);
        var context = CreateStrokeContext(
            DirectStrokeKind.Line,
            vertical: false,
            LargeSurfaceSize);
        DxfStaticBuffer? buffer = null;
        if (useStaticBuffer)
        {
            buffer = window.Compositor.CompileStaticDxf(context);
            window.Content = new StaticStrokeVisual(
                buffer,
                transform,
                LargeSurfaceSize);
        }
        else
        {
            window.Content = new GpuTransformStrokeVisual(
                DirectStrokeKind.Line,
                transform,
                LargeSurfaceSize,
                vertical: false);
        }

        try
        {
            window.Render();
            Assert.InRange(
                CountPaintedRows(
                    window.ReadPixels(),
                    LargeSurfaceSize,
                    x: LargeSurfaceSize / 2),
                10,
                14);
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    [Theory]
    [InlineData(DirectStrokeKind.Line)]
    [InlineData(DirectStrokeKind.Quadratic)]
    [InlineData(DirectStrokeKind.Cubic)]
    [InlineData(DirectStrokeKind.Arc)]
    public void StaticSingularTransformFailsClosed(DirectStrokeKind kind)
    {
        AssertTransformFailsClosed(
            kind,
            useStaticBuffer: true,
            ScaleAboutCenter(1f, 0f, LargeSurfaceSize));
    }

    [Theory]
    [InlineData(DirectStrokeKind.Line)]
    [InlineData(DirectStrokeKind.Quadratic)]
    [InlineData(DirectStrokeKind.Cubic)]
    [InlineData(DirectStrokeKind.Arc)]
    public void DynamicSingularTransformFailsClosed(DirectStrokeKind kind)
    {
        AssertTransformFailsClosed(
            kind,
            useStaticBuffer: false,
            ScaleAboutCenter(1f, 0f, LargeSurfaceSize));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonFiniteTransformFailsClosed(bool useStaticBuffer)
    {
        var transform = Matrix4x4.Identity;
        transform.M11 = float.NaN;
        AssertTransformFailsClosed(
            DirectStrokeKind.Line,
            useStaticBuffer,
            transform);
    }

    [Theory]
    [InlineData(AffinePolygonKind.Line)]
    [InlineData(AffinePolygonKind.Quadratic)]
    [InlineData(AffinePolygonKind.Cubic)]
    [InlineData(AffinePolygonKind.Polyline)]
    public void StaticLateAffinePolygonMatchesBakedTransform(
        AffinePolygonKind kind)
    {
        var initial = ScaleAboutCenter(1.25f, 0.8f, LargeSurfaceSize);
        var late = ScaleAboutCenter(0.75f, 1.25f, LargeSurfaceSize);
        var actual = RenderStaticAffineCommand(
            CreateAffinePolygonCommand(kind, initial),
            late,
            out var shapeTypes);
        var expected = RenderStaticAffineCommand(
            CreateAffinePolygonCommand(kind, initial * late),
            Matrix4x4.Identity,
            out _);

        AssertExpectedAffinePolygonShapeTypes(kind, shapeTypes);
        Assert.True(CountPaintedPixels(expected) > 0);
        Assert.True(CountPaintedPixels(actual) > 0);
        AssertImagesNear(expected, actual);
    }

    [Theory]
    [InlineData(AffinePolygonKind.Line)]
    [InlineData(AffinePolygonKind.Quadratic)]
    [InlineData(AffinePolygonKind.Cubic)]
    [InlineData(AffinePolygonKind.Polyline)]
    public void DynamicLateAffinePolygonMatchesBakedTransform(
        AffinePolygonKind kind)
    {
        var initial = ScaleAboutCenter(1.25f, 0.8f, LargeSurfaceSize);
        var late = ScaleAboutCenter(0.75f, 1.25f, LargeSurfaceSize);
        var actualCommand = CreateAffinePolygonCommand(kind, initial);
        actualCommand.UseGpuTransforms = true;
        actualCommand.CameraView = late;

        var actual = RenderDynamicAffineCommand(actualCommand);
        var expected = RenderDynamicAffineCommand(
            CreateAffinePolygonCommand(kind, initial * late));

        Assert.True(CountPaintedPixels(expected) > 0);
        Assert.True(CountPaintedPixels(actual) > 0);
        AssertImagesNear(expected, actual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StronglyDownscaledPartialArcMatchesBakedAffineOutline(
        bool useStaticBuffer)
    {
        var late = ScaleAboutCenter(1f, 0.2f, LargeSurfaceSize);
        byte[] actual;
        byte[] expected;
        if (useStaticBuffer)
        {
            actual = RenderStaticAffineCommand(
                CreateQuarterArcCommand(Matrix4x4.Identity),
                late,
                out var actualShapeTypes);
            expected = RenderStaticAffineCommand(
                CreateQuarterArcCommand(late),
                Matrix4x4.Identity,
                out _);
            Assert.Contains(12, actualShapeTypes);
        }
        else
        {
            var actualCommand = CreateQuarterArcCommand(Matrix4x4.Identity);
            actualCommand.UseGpuTransforms = true;
            actualCommand.CameraView = late;
            actual = RenderDynamicAffineCommand(actualCommand);
            expected = RenderDynamicAffineCommand(
                CreateQuarterArcCommand(late));
        }

        Assert.True(CountPaintedPixels(expected) > 0);
        Assert.True(CountPaintedPixels(actual) > 0);
        // The native analytic arc covers three threshold-edge pixels that the
        // 24-section CPU fallback omits. Require identical bounds and prohibit
        // any missing reference coverage so quad clipping cannot hide in AA.
        AssertCoverageEquivalent(
            expected,
            actual,
            maximumMissingPixels: 0,
            maximumExtraPixels: 3);
    }

    [Fact]
    public void VectorShaderScopesExplicitScaleToDirect2DStrokes()
    {
        Assert.Contains(
            "sType == 3u || sType == 5u || sType == 6u || sType == 12u",
            Shaders.VectorShader);
        Assert.Contains(
            "direct_2d_stroke_scales(isStatic, useGpuTransforms)",
            Shaders.VectorShader);
        Assert.Contains(
            "!is_conformal_stroke_transform(directStrokeScales)",
            Shaders.VectorShader);
        Assert.Contains(
            "input.localStrokeMode > 0.5",
            Shaders.VectorShader);
        Assert.Contains(
            "discardLateAffineShape",
            Shaders.VectorShader);
        Assert.Contains(
            "sType >= 13u && sType <= 17u",
            Shaders.VectorShader);
        Assert.Contains(
            "let hairlineCenter = select(",
            Shaders.VectorShader);
        Assert.DoesNotContain(
            "sType == 8u || sType == 12u",
            Shaders.VectorShader);
    }

    [Fact]
    public void StaticStrokeScaleCachePreservesSharedUniformAbi()
    {
        Assert.Equal(224, Marshal.SizeOf<GpuUniforms>());
        Assert.Equal(
            216,
            Marshal.OffsetOf<GpuUniforms>(nameof(GpuUniforms.Pad1)).ToInt32());
    }

    [Theory]
    [InlineData(DirectStrokeKind.Line)]
    [InlineData(DirectStrokeKind.Quadratic)]
    [InlineData(DirectStrokeKind.Cubic)]
    public void FixedDeviceStaticStrokeKeepsPositiveWidthAcrossZoom(
        DirectStrokeKind kind)
    {
        var zoomedOut = RenderStroke(
            kind,
            vertical: false,
            ScaleAboutCenter(0.5f),
            useStaticBuffer: true,
            fixedDeviceStroke: true);
        var zoomedIn = RenderStroke(
            kind,
            vertical: false,
            ScaleAboutCenter(4f, 4f, LargeSurfaceSize),
            useStaticBuffer: true,
            fixedDeviceStroke: true);

        var zoomedOutRows = CountPaintedRows(
            zoomedOut,
            LargeSurfaceSize,
            x: LargeSurfaceSize / 2);
        var zoomedInRows = CountPaintedRows(
            zoomedIn,
            LargeSurfaceSize,
            x: LargeSurfaceSize / 2);

        Assert.InRange(zoomedOutRows, 11, 13);
        Assert.InRange(zoomedInRows, 11, 13);
        Assert.InRange(Math.Abs(zoomedOutRows - zoomedInRows), 0, 1);
    }

    [Fact]
    public void FixedDeviceStaticPolylineCapsAndJoinMatchBakedAffineCenterline()
    {
        var late = ScaleAboutCenter(1.6f, 0.7f, LargeSurfaceSize);
        late.M21 += 0.35f;
        var actual = RenderStaticAffineCommand(
            CreateFixedPolylineCommand(Matrix4x4.Identity),
            late,
            out var shapeTypes);
        var expected = RenderStaticAffineCommand(
            CreateFixedPolylineCommand(late),
            Matrix4x4.Identity,
            out _);

        Assert.Contains(22, shapeTypes);
        Assert.Contains(23, shapeTypes);
        AssertImagesNear(
            expected,
            actual,
            maximumAllowedDelta: 8,
            maximumMateriallyDifferentPixels: 32);
    }

    [Theory]
    [InlineData(FixedAnalyticStrokeKind.Rectangle)]
    [InlineData(FixedAnalyticStrokeKind.Ellipse)]
    [InlineData(FixedAnalyticStrokeKind.RoundedRectangle)]
    public void FixedDeviceStaticAnalyticStrokeKeepsPositiveWidthAcrossZoom(
        FixedAnalyticStrokeKind kind)
    {
        var zoomedOut = RenderFixedAnalyticStroke(kind, 0.75f);
        var zoomedIn = RenderFixedAnalyticStroke(kind, 2f);

        var zoomedOutWidth = GetMaximumPaintedColumnRun(
            zoomedOut,
            LargeSurfaceSize,
            y: LargeSurfaceSize / 2);
        var zoomedInWidth = GetMaximumPaintedColumnRun(
            zoomedIn,
            LargeSurfaceSize,
            y: LargeSurfaceSize / 2);

        Assert.InRange(zoomedOutWidth, 11, 13);
        Assert.InRange(zoomedInWidth, 11, 13);
        Assert.InRange(Math.Abs(zoomedOutWidth - zoomedInWidth), 0, 1);
    }

    [Fact]
    public void DxfStaticLineKeepsCosmeticWidthAcrossZoom()
    {
        var zoomedOut = RenderDxfLine(0.75f);
        var zoomedIn = RenderDxfLine(2f);

        var zoomedOutRows = CountPaintedRows(
            zoomedOut,
            LargeSurfaceSize,
            x: LargeSurfaceSize / 2);
        var zoomedInRows = CountPaintedRows(
            zoomedIn,
            LargeSurfaceSize,
            x: LargeSurfaceSize / 2);

        Assert.InRange(zoomedOutRows, 1, 2);
        Assert.InRange(zoomedInRows, 1, 2);
        Assert.InRange(Math.Abs(zoomedOutRows - zoomedInRows), 0, 1);
    }

    private static Matrix4x4 ScaleAboutCenter(float scale) =>
        ScaleAboutCenter(scale, scale, SmallSurfaceSize);

    private static Matrix4x4 ScaleAboutCenter(
        float scaleX,
        float scaleY,
        int surfaceSize) =>
        Matrix4x4.CreateTranslation(
            -surfaceSize * 0.5f,
            -surfaceSize * 0.5f,
            0f) *
        Matrix4x4.CreateScale(scaleX, scaleY, 1f) *
        Matrix4x4.CreateTranslation(
            surfaceSize * 0.5f,
            surfaceSize * 0.5f,
            0f);

    private static Matrix4x4 ShearAboutCenter(float shearX)
    {
        var shear = Matrix4x4.Identity;
        shear.M21 = shearX;
        return Matrix4x4.CreateTranslation(-64f, -64f, 0f) *
            shear *
            Matrix4x4.CreateTranslation(64f, 64f, 0f);
    }

    private static void AssertAnisotropicAxisStrokes(
        DirectStrokeKind kind,
        bool useStaticBuffer)
    {
        var transform = ScaleAboutCenter(2f, 0.5f, LargeSurfaceSize);
        var horizontalPixels = RenderStroke(
            kind,
            vertical: false,
            transform,
            useStaticBuffer);
        var verticalPixels = RenderStroke(
            kind,
            vertical: true,
            transform,
            useStaticBuffer);

        Assert.InRange(
            CountPaintedRows(
                horizontalPixels,
                LargeSurfaceSize,
                x: LargeSurfaceSize / 2),
            4,
            8);
        Assert.InRange(
            CountPaintedColumns(
                verticalPixels,
                LargeSurfaceSize,
                y: LargeSurfaceSize / 2),
            21,
            27);
    }

    private static void AssertAnisotropicArc(bool useStaticBuffer)
    {
        var pixels = RenderStroke(
            DirectStrokeKind.Arc,
            vertical: false,
            ScaleAboutCenter(2f, 0.5f, LargeSurfaceSize),
            useStaticBuffer,
            fullArc: true);

        Assert.InRange(
            CountPaintedRows(
                pixels,
                LargeSurfaceSize,
                x: 64,
                start: 44,
                endExclusive: 61),
            4,
            8);
        Assert.InRange(
            CountPaintedColumns(
                pixels,
                LargeSurfaceSize,
                y: 64,
                start: 96,
                endExclusive: 128),
            21,
            27);
    }

    private static void AssertTransformFailsClosed(
        DirectStrokeKind kind,
        bool useStaticBuffer,
        Matrix4x4 transform)
    {
        var pixels = RenderStroke(
            kind,
            vertical: false,
            transform,
            useStaticBuffer,
            fullArc: kind == DirectStrokeKind.Arc);
        Assert.Equal(0, CountPaintedPixels(pixels));
    }

    private static RenderCommand CreateAffinePolygonCommand(
        AffinePolygonKind kind,
        Matrix4x4 transform)
    {
        var pen = kind == AffinePolygonKind.Polyline
            ? new Pen(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                12f,
                PenLineJoin.Round,
                startLineCap: PenLineCap.Round,
                endLineCap: PenLineCap.Round)
            : CreatePen();
        var command = new RenderCommand
        {
            Pen = pen,
            Transform = transform,
            IsPenThicknessLocal = true
        };
        switch (kind)
        {
            case AffinePolygonKind.Line:
                command.Type = RenderCommandType.DrawLine;
                command.Position = new Vector2(24f, 64f);
                command.Position2 = new Vector2(104f, 64f);
                break;
            case AffinePolygonKind.Quadratic:
                command.Type = RenderCommandType.DrawBezier;
                command.Position = new Vector2(24f, 72f);
                command.Position2 = new Vector2(64f, 28f);
                command.Position3 = new Vector2(104f, 72f);
                break;
            case AffinePolygonKind.Cubic:
                command.Type = RenderCommandType.DrawCubicBezier;
                command.Position = new Vector2(24f, 76f);
                command.Position2 = new Vector2(40f, 20f);
                command.Position3 = new Vector2(88f, 108f);
                command.Position4 = new Vector2(104f, 52f);
                break;
            case AffinePolygonKind.Polyline:
                command.Type = RenderCommandType.DrawPath;
                command.Path = CreatePolylinePath();
                command.GeometryCache = RenderCommandGeometryCache.ForPath(
                    command.Path);
                break;
        }

        return command;
    }

    private static RenderCommand CreateFixedPolylineCommand(Matrix4x4 transform)
    {
        var path = CreatePolylinePath();
        return new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = new Pen(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                12f,
                PenLineJoin.Round,
                startLineCap: PenLineCap.Round,
                endLineCap: PenLineCap.Round,
                strokeTransformMode: PenStrokeTransformMode.Fixed),
            Transform = transform,
            IsPenThicknessLocal = true
        };
    }

    private static PathGeometry CreatePolylinePath()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(28f, 84f));
        figure.Segments.Add(new LineSegment(new Vector2(64f, 32f)));
        figure.Segments.Add(new LineSegment(new Vector2(100f, 84f)));
        path.Figures.Add(figure);
        return path;
    }

    private static RenderCommand CreateQuarterArcCommand(Matrix4x4 transform)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(64f, 40f));
        figure.Segments.Add(new ArcSegment(
            new Vector2(88f, 64f),
            new Vector2(24f, 24f),
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        path.Figures.Add(figure);
        return new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Pen = CreatePen(),
            Transform = transform,
            IsPenThicknessLocal = true
        };
    }

    private static byte[] RenderStaticAffineCommand(
        RenderCommand command,
        Matrix4x4 lateTransform,
        out HashSet<int> shapeTypes)
    {
        using var window = new HeadlessWindow(LargeSurfaceSize, LargeSurfaceSize);
        using var buffer = window.Compositor.CompileStaticDxf([command]);
        shapeTypes = new HashSet<int>();
        foreach (var vertex in buffer.VectorVertices)
        {
            shapeTypes.Add(DecodeShapeType(vertex.ShapeType));
        }

        window.Content = new StaticStrokeVisual(
            buffer,
            lateTransform,
            LargeSurfaceSize);
        window.Render();
        return window.ReadPixels();
    }

    private static byte[] RenderDynamicAffineCommand(RenderCommand command)
    {
        using var window = new HeadlessWindow(LargeSurfaceSize, LargeSurfaceSize);
        window.Content = new CommandVisual(command);
        window.Render();
        return window.ReadPixels();
    }

    private static int DecodeShapeType(float encodedShapeType)
    {
        if (encodedShapeType >= 1000f)
        {
            encodedShapeType -= 1000f;
        }
        if (encodedShapeType >= 195f)
        {
            encodedShapeType -= 200f;
        }
        else if (encodedShapeType >= 95f)
        {
            encodedShapeType -= 100f;
        }

        return (int)MathF.Round(encodedShapeType);
    }

    private static void AssertExpectedAffinePolygonShapeTypes(
        AffinePolygonKind kind,
        HashSet<int> shapeTypes)
    {
        switch (kind)
        {
            case AffinePolygonKind.Line:
                Assert.Contains(14, shapeTypes);
                break;
            case AffinePolygonKind.Quadratic:
            case AffinePolygonKind.Cubic:
                Assert.Contains(15, shapeTypes);
                Assert.Contains(16, shapeTypes);
                Assert.Contains(17, shapeTypes);
                break;
            case AffinePolygonKind.Polyline:
                Assert.Contains(13, shapeTypes);
                Assert.Contains(14, shapeTypes);
                break;
        }
    }

    private static void AssertImagesNear(
        byte[] expected,
        byte[] actual,
        int maximumAllowedDelta = 24,
        int maximumMateriallyDifferentPixels = 128)
    {
        Assert.Equal(expected.Length, actual.Length);
        var maximumDelta = 0;
        var materiallyDifferentPixels = 0;
        for (var index = 2; index < expected.Length; index += 4)
        {
            var delta = Math.Abs(expected[index] - actual[index]);
            maximumDelta = Math.Max(maximumDelta, delta);
            if (delta > 8)
            {
                materiallyDifferentPixels++;
            }
        }

        Assert.True(
            maximumDelta <= maximumAllowedDelta &&
                materiallyDifferentPixels <= maximumMateriallyDifferentPixels,
            $"Affine render mismatch: max delta {maximumDelta}, " +
            $"materially different pixels {materiallyDifferentPixels}.");
    }

    private static void AssertCoverageEquivalent(
        byte[] expected,
        byte[] actual,
        int maximumMissingPixels,
        int maximumExtraPixels)
    {
        Assert.Equal(expected.Length, actual.Length);
        var expectedOnly = 0;
        var actualOnly = 0;
        var expectedBounds = GetCoverageBounds(expected);
        var actualBounds = GetCoverageBounds(actual);
        for (var index = 2; index < expected.Length; index += 4)
        {
            var expectedCovered = expected[index] > 128;
            var actualCovered = actual[index] > 128;
            if (expectedCovered && !actualCovered)
            {
                expectedOnly++;
            }
            else if (actualCovered && !expectedCovered)
            {
                actualOnly++;
            }
        }

        Assert.True(
            expectedBounds == actualBounds &&
                expectedOnly <= maximumMissingPixels &&
                actualOnly <= maximumExtraPixels,
            $"Coverage mismatch: expected bounds {expectedBounds}, actual bounds {actualBounds}, " +
            $"missing pixels {expectedOnly}, extra pixels {actualOnly}.");
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) GetCoverageBounds(byte[] pixels)
    {
        var minX = LargeSurfaceSize;
        var minY = LargeSurfaceSize;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < LargeSurfaceSize; y++)
        {
            for (var x = 0; x < LargeSurfaceSize; x++)
            {
                if (pixels[(y * LargeSurfaceSize + x) * 4 + 2] <= 128)
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

    private static byte[] RenderStroke(
        DirectStrokeKind kind,
        bool vertical,
        Matrix4x4 transform,
        bool useStaticBuffer,
        bool fullArc = false,
        bool fixedDeviceStroke = false)
    {
        using var window = new HeadlessWindow(LargeSurfaceSize, LargeSurfaceSize);
        DxfStaticBuffer? buffer = null;
        if (useStaticBuffer)
        {
            var context = CreateStrokeContext(
                kind,
                vertical,
                LargeSurfaceSize,
                fullArc,
                fixedDeviceStroke);
            buffer = window.Compositor.CompileStaticDxf(context);
            window.Content = new StaticStrokeVisual(
                buffer,
                transform,
                LargeSurfaceSize);
        }
        else
        {
            window.Content = new GpuTransformStrokeVisual(
                kind,
                transform,
                LargeSurfaceSize,
                vertical,
                fullArc);
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

    private static DrawingContext CreateStrokeContext(
        DirectStrokeKind kind,
        bool vertical = false,
        int surfaceSize = SmallSurfaceSize,
        bool fullArc = false,
        bool fixedDeviceStroke = false)
    {
        var center = surfaceSize * 0.5f;
        var start = surfaceSize == SmallSurfaceSize ? 0f : 16f;
        var end = surfaceSize == SmallSurfaceSize ? 64f : 112f;
        var p0 = vertical
            ? new Vector2(center, start)
            : new Vector2(start, center);
        var p1 = vertical
            ? new Vector2(center, end)
            : new Vector2(end, center);
        var control1 = Vector2.Lerp(p0, p1, 1f / 3f);
        var control2 = Vector2.Lerp(p0, p1, 2f / 3f);

        var context = new DrawingContext();
        var pen = CreatePen(fixedDeviceStroke);
        switch (kind)
        {
            case DirectStrokeKind.Line:
                context.DrawLine(pen, p0, p1);
                break;
            case DirectStrokeKind.Quadratic:
                context.DrawQuadraticBezier(
                    pen,
                    p0,
                    Vector2.Lerp(p0, p1, 0.5f),
                    p1);
                break;
            case DirectStrokeKind.Cubic:
                context.DrawCubicBezier(pen, p0, control1, control2, p1);
                break;
            case DirectStrokeKind.Arc:
                context.DrawPath(
                    null,
                    pen,
                    fullArc ? CreateFullArcPath(center) : CreateArcPath());
                break;
        }

        return context;
    }

    private static Pen CreatePen(bool fixedDeviceStroke = false) => new(
        new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
        12f,
        strokeTransformMode: fixedDeviceStroke
            ? PenStrokeTransformMode.Fixed
            : PenStrokeTransformMode.Normal);

    private static PathGeometry CreateArcPath()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(8f, 32f));
        figure.Segments.Add(new ArcSegment(
            new Vector2(56f, 32f),
            new Vector2(24f, 24f),
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        path.Figures.Add(figure);
        return path;
    }

    private static PathGeometry CreateFullArcPath(float center)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(center - 24f, center));
        figure.Segments.Add(new ArcSegment(
            new Vector2(center + 24f, center),
            new Vector2(24f, 24f),
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        figure.Segments.Add(new ArcSegment(
            new Vector2(center - 24f, center),
            new Vector2(24f, 24f),
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        path.Figures.Add(figure);
        return path;
    }

    private static int CountPaintedRows(
        byte[] pixels,
        int surfaceSize,
        int x,
        int start = 0,
        int? endExclusive = null)
    {
        var count = 0;
        for (var y = start; y < (endExclusive ?? surfaceSize); y++)
        {
            if (pixels[(y * surfaceSize + x) * 4 + 2] > 128)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountPaintedColumns(
        byte[] pixels,
        int surfaceSize,
        int y,
        int start = 0,
        int? endExclusive = null)
    {
        var count = 0;
        for (var x = start; x < (endExclusive ?? surfaceSize); x++)
        {
            if (pixels[(y * surfaceSize + x) * 4 + 2] > 128)
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

    private static int GetMaximumPaintedColumnRun(
        byte[] pixels,
        int surfaceSize,
        int y)
    {
        bool IsPainted(int x)
        {
            var offset = (y * surfaceSize + x) * 4;
            return pixels[offset] > 128 ||
                pixels[offset + 1] > 128 ||
                pixels[offset + 2] > 128;
        }

        var maximum = 0;
        var current = 0;
        for (var x = 0; x < surfaceSize; x++)
        {
            if (IsPainted(x))
            {
                current++;
                maximum = Math.Max(maximum, current);
            }
            else
            {
                current = 0;
            }
        }
        return maximum;
    }

    private static byte[] RenderFixedAnalyticStroke(
        FixedAnalyticStrokeKind kind,
        float zoom)
    {
        const float radius = 20f;
        var center = new Vector2(LargeSurfaceSize * 0.5f);
        var context = new DrawingContext();
        var pen = new Pen(
            new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
            12f,
            strokeTransformMode: PenStrokeTransformMode.Fixed);
        var bounds = new Rect(
            center.X - radius,
            center.Y - radius,
            radius * 2f,
            radius * 2f);
        switch (kind)
        {
            case FixedAnalyticStrokeKind.Rectangle:
                context.DrawRectangle(null, pen, bounds);
                break;
            case FixedAnalyticStrokeKind.Ellipse:
                context.DrawEllipse(null, pen, center, radius, radius);
                break;
            case FixedAnalyticStrokeKind.RoundedRectangle:
                context.DrawRoundedRectangle(null, pen, bounds, 6f, 6f);
                break;
        }

        using var window = new HeadlessWindow(LargeSurfaceSize, LargeSurfaceSize);
        using var buffer = window.Compositor.CompileStaticDxf(context);
        window.Content = new StaticStrokeVisual(
            buffer,
            ScaleAboutCenter(zoom, zoom, LargeSurfaceSize),
            LargeSurfaceSize);
        window.Render();
        return window.ReadPixels();
    }

    private static byte[] RenderDxfLine(float zoom)
    {
        var document = new netDxf.DxfDocument();
        document.AddEntity(new netDxf.Entities.Line(
            new netDxf.Vector2(24d, LargeSurfaceSize * 0.5d),
            new netDxf.Vector2(104d, LargeSurfaceSize * 0.5d)));

        var drawingContext = new DrawingContext();
        var dxfContext = new DxfRenderContext(drawingContext, null!)
        {
            EnableGpuTransforms = true,
            IsCompilingStatic = true,
            Zoom = zoom
        };
        dxfContext.ActiveLayers.Add("0");
        DxfDocumentRenderer.Render(document, dxfContext);

        using var window = new HeadlessWindow(LargeSurfaceSize, LargeSurfaceSize);
        using var buffer = window.Compositor.CompileStaticDxf(drawingContext);
        window.Content = new StaticStrokeVisual(
            buffer,
            ScaleAboutCenter(zoom, zoom, LargeSurfaceSize),
            LargeSurfaceSize);
        window.Render();
        return window.ReadPixels();
    }

    private sealed class StaticStrokeVisual : FrameworkElement
    {
        private readonly DxfStaticBuffer _buffer;

        public StaticStrokeVisual(
            DxfStaticBuffer buffer,
            Matrix4x4 transform,
            int surfaceSize)
        {
            _buffer = buffer;
            Width = surfaceSize;
            Height = surfaceSize;
            Transform = transform;
        }

        public override void OnRender(DrawingContext context) =>
            context.DrawStaticDxf(_buffer);
    }

    public enum FixedAnalyticStrokeKind
    {
        Rectangle,
        Ellipse,
        RoundedRectangle
    }

    private sealed class GpuTransformStrokeVisual : FrameworkElement
    {
        private readonly DirectStrokeKind _kind;
        private readonly Matrix4x4 _cameraView;
        private readonly int _surfaceSize;
        private readonly bool _vertical;
        private readonly bool _fullArc;
        private readonly Pen _pen = CreatePen();

        public GpuTransformStrokeVisual(
            DirectStrokeKind kind,
            Matrix4x4 cameraView,
            int surfaceSize,
            bool vertical = false,
            bool fullArc = false)
        {
            _kind = kind;
            _cameraView = cameraView;
            _surfaceSize = surfaceSize;
            _vertical = vertical;
            _fullArc = fullArc;
            Width = surfaceSize;
            Height = surfaceSize;
        }

        public override void OnRender(DrawingContext context)
        {
            var center = _surfaceSize * 0.5f;
            var start = _surfaceSize == SmallSurfaceSize ? 0f : 16f;
            var end = _surfaceSize == SmallSurfaceSize ? 64f : 112f;
            var p0 = _vertical
                ? new Vector2(center, start)
                : new Vector2(start, center);
            var p1 = _vertical
                ? new Vector2(center, end)
                : new Vector2(end, center);
            var command = new RenderCommand
            {
                Pen = _pen,
                Position = p0,
                UseGpuTransforms = true,
                CameraView = _cameraView
            };
            switch (_kind)
            {
                case DirectStrokeKind.Line:
                    command.Type = RenderCommandType.DrawLine;
                    command.Position2 = p1;
                    break;
                case DirectStrokeKind.Quadratic:
                    command.Type = RenderCommandType.DrawBezier;
                    command.Position2 = Vector2.Lerp(p0, p1, 0.5f);
                    command.Position3 = p1;
                    break;
                case DirectStrokeKind.Cubic:
                    command.Type = RenderCommandType.DrawCubicBezier;
                    command.Position2 = Vector2.Lerp(p0, p1, 1f / 3f);
                    command.Position3 = Vector2.Lerp(p0, p1, 2f / 3f);
                    command.Position4 = p1;
                    break;
                case DirectStrokeKind.Arc:
                    command.Type = RenderCommandType.DrawPath;
                    command.Path = _fullArc
                        ? CreateFullArcPath(center)
                        : CreateArcPath();
                    command.GeometryCache = RenderCommandGeometryCache.ForPath(
                        command.Path);
                    break;
            }

            context.Commands.Add(command);
        }
    }

    private sealed class CommandVisual : FrameworkElement
    {
        private readonly RenderCommand _command;

        public CommandVisual(RenderCommand command)
        {
            _command = command;
            Width = LargeSurfaceSize;
            Height = LargeSurfaceSize;
        }

        public override void OnRender(DrawingContext context) =>
            context.Commands.Add(_command);
    }

    public enum AffinePolygonKind
    {
        Line,
        Quadratic,
        Cubic,
        Polyline
    }

    public enum DirectStrokeKind
    {
        Line,
        Quadratic,
        Cubic,
        Arc
    }
}
