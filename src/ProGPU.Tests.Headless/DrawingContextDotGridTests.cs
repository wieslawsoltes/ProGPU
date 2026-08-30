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
}
