using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class DrawingContextDotGridTests
{
    [Fact]
    public void DrawDotGridRecordsOneParameterizedCommand()
    {
        var context = new DrawingContext();
        var brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.08f));
        var bounds = new Rect(2f, 3f, 80f, 60f);
        var phase = new Vector2(-1.25f, 2.5f);

        context.DrawDotGrid(brush, bounds, 10f, 0.75f, phase);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawDotGrid, command.Type);
        Assert.Same(brush, command.Brush);
        Assert.Equal(bounds, command.Rect);
        Assert.Equal(phase, command.Position2);
        Assert.Equal(10f, command.RadiusX);
        Assert.Equal(0.75f, command.RadiusY);
    }

    [Fact]
    public void StaticDotGridCompilationUsesOneQuad()
    {
        var context = new DrawingContext();
        context.DrawDotGrid(
            new SolidColorBrush(Vector4.One),
            new Rect(0f, 0f, 100f, 80f),
            10f,
            0.75f,
            Vector2.Zero);

        using var buffer = HeadlessWindow.Shared.Compositor.CompileStaticDxf(context);

        Assert.Equal(4, buffer.VectorVertices.Length);
        Assert.Equal(6u, buffer.IndexCount);
    }

    [Theory]
    [InlineData(0f, 0.75f)]
    [InlineData(-1f, 0.75f)]
    [InlineData(10f, 0f)]
    [InlineData(10f, float.NaN)]
    public void DrawDotGridRejectsInvalidGeometry(float spacing, float radius)
    {
        var context = new DrawingContext();
        var brush = new SolidColorBrush(Vector4.One);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            context.DrawDotGrid(brush, new Rect(0f, 0f, 10f, 10f), spacing, radius, Vector2.Zero));
    }

    [Fact]
    public void DotGridShaderDrawsPeriodicDotsWithoutFillingCells()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(64, 64);
        window.Content = new DotGridVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            Assert.True(ReadRed(pixels, window.Width, 10, 10) >= 220);
            Assert.True(ReadRed(pixels, window.Width, 20, 20) >= 220);
            Assert.True(ReadRed(pixels, window.Width, 15, 15) <= 20);
        }
        finally
        {
            window.Content = null;
        }
    }

    private static byte ReadRed(byte[] pixels, uint width, int x, int y)
        => pixels[((y * checked((int)width)) + x) * 4];

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
                spacing: 10f,
                radius: 1.5f,
                phase: new Vector2(10f, 10f));
        }
    }
}
