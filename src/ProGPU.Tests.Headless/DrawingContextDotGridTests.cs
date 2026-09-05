using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.WinUI.Designer;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests.Headless;

public sealed class DrawingContextDotGridTests
{
    [Fact]
    public void DrawDotGridRecordsOneParameterizedCommand()
    {
        var context = new DrawingContext();
        var brush = new SolidColorBrush(Vector4.One);
        var bounds = new Rect(2f, 3f, 80f, 60f);
        var phase = new Vector2(-1.25f, 2.5f);

        context.DrawDotGrid(brush, bounds, 10f, 0.75f, phase);

        RenderCommand command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawDotGrid, command.Type);
        Assert.Same(brush, command.Brush);
        Assert.Equal(bounds, command.Rect);
        Assert.Equal(phase, command.Position2);
        Assert.Equal(10f, command.RadiusX);
        Assert.Equal(0.75f, command.RadiusY);
    }

    [Fact]
    public void DeviceDotGridRecordsRectangularSpacingRadiusAndAffineTransform()
    {
        var context = new DrawingContext();
        var brush = new SolidColorBrush(Vector4.One);
        var bounds = new Rect(-20f, -10f, 80f, 60f);
        var spacing = new Vector2(7f, 11f);
        Matrix4x4 transform = Matrix4x4.CreateScale(2f, 3f, 1f) *
            Matrix4x4.CreateRotationZ(0.25f) *
            Matrix4x4.CreateTranslation(40f, 30f, 0f);

        context.DrawDeviceDotGrid(brush, bounds, spacing, 0.875f, transform);

        RenderCommand command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawDeviceDotGrid, command.Type);
        Assert.Same(brush, command.Brush);
        Assert.Equal(bounds, command.Rect);
        Assert.Equal(spacing, command.Position2);
        Assert.Equal(0.875f, command.RadiusX);
        Assert.Equal(transform, command.Transform);
    }

    [Fact]
    public void DeviceLineGridRecordsWidthMajorCadenceAndAffineTransform()
    {
        var context = new DrawingContext();
        var brush = new SolidColorBrush(Vector4.One);
        var bounds = new Rect(-20f, -10f, 80f, 60f);
        var spacing = new Vector2(7f, 11f);
        Matrix4x4 transform = Matrix4x4.CreateRotationZ(0.25f);

        context.DrawDeviceLineGrid(
            brush,
            bounds,
            spacing,
            1.25f,
            7,
            transform);

        RenderCommand command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawDeviceDotGrid, command.Type);
        Assert.Same(brush, command.Brush);
        Assert.Equal(bounds, command.Rect);
        Assert.Equal(spacing, command.Position2);
        Assert.Equal(1.25f, command.RadiusX);
        Assert.Equal(7f, command.RadiusY);
        Assert.Equal(transform, command.Transform);
    }

    [Fact]
    public void StaticDotGridCompilationUsesOneQuad()
    {
        var context = new DrawingContext();
        context.DrawDotGrid(
            new SolidColorBrush(Vector4.One),
            new Rect(0f, 0f, 1000f, 800f),
            10f,
            0.75f,
            Vector2.Zero);

        using DxfStaticBuffer buffer =
            HeadlessWindow.Shared.Compositor.CompileStaticDxf(context);

        Assert.Equal(4, buffer.VectorVertices.Length);
        Assert.Equal(6u, buffer.IndexCount);
    }

    [Fact]
    public void StaticDeviceDotGridCompilationUsesOneAffineQuad()
    {
        var context = new DrawingContext();
        context.DrawDeviceDotGrid(
            new SolidColorBrush(Vector4.One),
            new Rect(-100f, -80f, 200f, 160f),
            new Vector2(8f, 12f),
            0.75f,
            Matrix4x4.CreateRotationZ(0.35f));

        using DxfStaticBuffer buffer =
            HeadlessWindow.Shared.Compositor.CompileStaticDxf(context);

        Assert.Equal(4, buffer.VectorVertices.Length);
        Assert.Equal(6u, buffer.IndexCount);
        Assert.All(buffer.VectorVertices, vertex =>
        {
            Assert.Equal(new Vector2(8f, 12f), vertex.ShapeSize);
            Assert.Equal(0.75f, vertex.CornerRadius);
            Assert.Equal(225f, vertex.ShapeType);
        });
    }

    [Fact]
    public void StaticDeviceLineGridCompilationUsesOneAffineQuad()
    {
        var context = new DrawingContext();
        context.DrawDeviceLineGrid(
            new SolidColorBrush(Vector4.One),
            new Rect(-100f, -80f, 200f, 160f),
            new Vector2(8f, 12f),
            1.25f,
            5,
            Matrix4x4.CreateRotationZ(0.35f));

        using DxfStaticBuffer buffer =
            HeadlessWindow.Shared.Compositor.CompileStaticDxf(context);

        Assert.Equal(4, buffer.VectorVertices.Length);
        Assert.Equal(6u, buffer.IndexCount);
        Assert.All(buffer.VectorVertices, vertex =>
        {
            Assert.Equal(new Vector2(8f, 12f), vertex.ShapeSize);
            Assert.Equal(-1.25f, vertex.CornerRadius);
            Assert.Equal(5f, vertex.StrokeThickness);
            Assert.Equal(225f, vertex.ShapeType);
        });
    }

    [Fact]
    public void DesignerCanvasRecordsExactlyOneGridCommand()
    {
        var canvas = new DesignerCanvas
        {
            Size = new Vector2(1000f, 800f),
            GridSize = 10f,
            ZoomScale = 1f,
            GetDpiScale = static () => 1f
        };
        var context = new DrawingContext();

        canvas.OnRender(context);

        Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawDotGrid);
    }

    [Theory]
    [InlineData(0f, 0.75f)]
    [InlineData(-1f, 0.75f)]
    [InlineData(10f, 0f)]
    [InlineData(10f, float.NaN)]
    public void DrawDotGridRejectsInvalidGeometry(float spacing, float radius)
    {
        var context = new DrawingContext();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.DrawDotGrid(
                new SolidColorBrush(Vector4.One),
                new Rect(0f, 0f, 10f, 10f),
                spacing,
                radius,
                Vector2.Zero));
    }

    [Theory]
    [InlineData(0f, 10f, 0.75f)]
    [InlineData(10f, -1f, 0.75f)]
    [InlineData(10f, 10f, 0f)]
    [InlineData(10f, 10f, float.NaN)]
    public void DeviceDotGridRejectsInvalidGeometry(
        float spacingX,
        float spacingY,
        float radius)
    {
        var context = new DrawingContext();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.DrawDeviceDotGrid(
                new SolidColorBrush(Vector4.One),
                new Rect(0f, 0f, 10f, 10f),
                new Vector2(spacingX, spacingY),
                radius));
    }

    [Theory]
    [InlineData(0f, 5)]
    [InlineData(float.NaN, 5)]
    [InlineData(1f, 0)]
    [InlineData(1f, 101)]
    public void DeviceLineGridRejectsInvalidWidthOrCadence(
        float width,
        int cadence)
    {
        var context = new DrawingContext();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.DrawDeviceLineGrid(
                new SolidColorBrush(Vector4.One),
                new Rect(0f, 0f, 10f, 10f),
                new Vector2(10f),
                width,
                cadence));
    }

    [Fact]
    public void DotGridShaderDrawsDotsWithoutFillingCells()
    {
        using var window = new HeadlessWindow(64, 64)
        {
            Content = new DotGridVisual()
        };

        window.Render();

        byte[] pixels = window.ReadPixels();
        Assert.True(ReadRed(pixels, window.Width, 10, 10) >= 220);
        Assert.True(ReadRed(pixels, window.Width, 20, 20) >= 220);
        Assert.True(ReadRed(pixels, window.Width, 15, 15) <= 20);
    }

    [Fact]
    public void DeviceDotGridShaderKeepsDotsCircularUnderAnisotropicTransform()
    {
        using var window = new HeadlessWindow(64, 64)
        {
            Content = new DeviceDotGridVisual()
        };

        window.Render();

        byte[] pixels = window.ReadPixels();
        Assert.True(ReadRed(pixels, window.Width, 32, 32) >= 220);
        Assert.True(ReadRed(pixels, window.Width, 52, 46) >= 220);
        Assert.True(ReadRed(pixels, window.Width, 42, 39) <= 20);
        Assert.True(ReadRed(pixels, window.Width, 35, 32) <= 20);
        Assert.True(ReadRed(pixels, window.Width, 32, 35) <= 20);
    }

    [Fact]
    public void DeviceDotGridShaderRendersExactIsometricLatticeThroughOneAffineQuad()
    {
        using var window = new HeadlessWindow(64, 64)
        {
            Content = new IsometricDeviceDotGridVisual()
        };

        window.Render();

        byte[] pixels = window.ReadPixels();
        Assert.True(ReadRed(pixels, window.Width, 32, 32) >= 220);
        Assert.True(ReadRed(pixels, window.Width, 41, 37) >= 180);
        Assert.True(ReadRed(pixels, window.Width, 23, 37) >= 180);
        Assert.True(ReadRed(pixels, window.Width, 32, 42) >= 220);
        Assert.True(ReadRed(pixels, window.Width, 32, 37) <= 20);
    }

    [Fact]
    public void DeviceLineGridShaderDrawsMinorAndWiderMajorLines()
    {
        using var window = new HeadlessWindow(64, 64)
        {
            Content = new DeviceLineGridVisual()
        };

        window.Render();

        byte[] pixels = window.ReadPixels();
        byte minorCenter = ReadRed(pixels, window.Width, 16, 8);
        byte majorCenter = ReadRed(pixels, window.Width, 32, 8);
        byte majorEdge = ReadRed(pixels, window.Width, 31, 8);
        byte minorEdge = ReadRed(pixels, window.Width, 17, 8);
        byte cellCenter = ReadRed(pixels, window.Width, 24, 8);
        string evidence = $"minor={minorCenter}, major={majorCenter}, " +
            $"majorEdge={majorEdge}, minorEdge={minorEdge}, cell={cellCenter}";
        Assert.True(minorCenter >= 100, evidence);
        Assert.True(majorCenter >= 220, evidence);
        Assert.True(majorEdge >= 220, evidence);
        Assert.True(minorEdge <= 20, evidence);
        Assert.True(cellCenter <= 20, evidence);
    }

    [Fact]
    public void DeviceLineGridShaderKeepsCoverageUnderAffineShear()
    {
        using var window = new HeadlessWindow(64, 64)
        {
            Content = new AffineDeviceLineGridVisual()
        };

        window.Render();

        byte[] pixels = window.ReadPixels();
        Assert.True(ReadRed(pixels, window.Width, 34, 38) >= 180);
        Assert.True(ReadRed(pixels, window.Width, 40, 34) >= 180);
        Assert.True(ReadRed(pixels, window.Width, 42, 40) <= 20);
    }

    private static byte ReadRed(byte[] pixels, uint width, int x, int y) =>
        pixels[((y * checked((int)width)) + x) * 4];

    private sealed class DotGridVisual : FrameworkElement
    {
        public DotGridVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 64f, 64f));
            context.DrawDotGrid(
                new SolidColorBrush(Vector4.One),
                new Rect(1f, 1f, 62f, 62f),
                10f,
                1.5f,
                new Vector2(10f, 10f));
        }
    }

    private sealed class DeviceDotGridVisual : FrameworkElement
    {
        public DeviceDotGridVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 64f, 64f));
            Matrix4x4 transform = Matrix4x4.CreateScale(2f, 1f, 1f) *
                Matrix4x4.CreateTranslation(32f, 32f, 0f);
            context.DrawDeviceDotGrid(
                new SolidColorBrush(Vector4.One),
                new Rect(-16f, -28f, 32f, 56f),
                new Vector2(10f, 14f),
                1.5f,
                transform);
        }
    }

    private sealed class IsometricDeviceDotGridVisual : FrameworkElement
    {
        public IsometricDeviceDotGridVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 64f, 64f));
            float cosine30 = MathF.Sqrt(3f) * 0.5f;
            Matrix4x4 transform = new(
                cosine30, 0.5f, 0f, 0f,
                -cosine30, 0.5f, 0f, 0f,
                0f, 0f, 1f, 0f,
                32f, 32f, 0f, 1f);
            context.DrawDeviceDotGrid(
                new SolidColorBrush(Vector4.One),
                new Rect(-30f, -30f, 60f, 60f),
                new Vector2(10f),
                1.5f,
                transform);
        }
    }

    private sealed class DeviceLineGridVisual : FrameworkElement
    {
        public DeviceLineGridVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 64f, 64f));
            context.DrawDeviceLineGrid(
                new SolidColorBrush(Vector4.One),
                new Rect(1f, 1f, 62f, 62f),
                new Vector2(16f),
                1f,
                2);
        }
    }

    private sealed class AffineDeviceLineGridVisual : FrameworkElement
    {
        public AffineDeviceLineGridVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 64f, 64f));
            var transform = new Matrix4x4(
                2f, 0.5f, 0f, 0f,
                0.5f, 1.5f, 0f, 0f,
                0f, 0f, 1f, 0f,
                32f, 32f, 0f, 1f);
            context.DrawDeviceLineGrid(
                new SolidColorBrush(Vector4.One),
                new Rect(-16f, -16f, 32f, 32f),
                new Vector2(8f),
                1f,
                2,
                transform);
        }
    }
}
