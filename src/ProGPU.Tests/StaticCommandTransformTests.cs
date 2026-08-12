using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class StaticCommandTransformTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticCompilationAppliesRecordedLineTransformOnce(bool useCommandList)
    {
        using var window = new HeadlessWindow(64, 64);
        var command = new RenderCommand
        {
            Type = RenderCommandType.DrawLine,
            Pen = new Pen(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                12f),
            Position = new Vector2(4f, 16f),
            Position2 = new Vector2(12f, 16f),
            Transform = Matrix4x4.CreateScale(2f, 2f, 1f),
            IsPenThicknessLocal = true
        };

        using var buffer = useCommandList
            ? window.Compositor.CompileStaticDxf(new List<RenderCommand> { command })
            : CompileContext(window.Compositor, command);

        var vertices = buffer.VectorVertices
            .Where(static vertex => MathF.Abs(vertex.ShapeType - 203f) < 0.01f)
            .ToArray();
        Assert.Equal(4, vertices.Length);
        Assert.All(vertices, static vertex => Assert.Equal(24f, vertex.StrokeThickness, 3));
        Assert.Equal(8f, vertices.Min(static vertex => vertex.Position.X), 3);
        Assert.Equal(24f, vertices.Max(static vertex => vertex.Position.X), 3);
        Assert.All(vertices, static vertex => Assert.Equal(32f, vertex.Position.Y, 3));
    }

    [Fact]
    public void DiagnosticCompilationAppliesRecordedLineTransformOnce()
    {
        using var window = new HeadlessWindow(64, 64);
        window.Content = new EmptyVisual();
        window.Compositor.RenderDiagnostics = (context, _, _) =>
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawLine,
                Pen = new Pen(
                    new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                    12f),
                Position = new Vector2(4f, 16f),
                Position2 = new Vector2(12f, 16f),
                Transform = Matrix4x4.CreateScale(2f, 2f, 1f),
                IsPenThicknessLocal = true
            });

        window.Render();

        var vertices = window.Compositor.VectorVertices
            .Where(static vertex => MathF.Abs(vertex.ShapeType - 3f) < 0.01f)
            .ToArray();
        Assert.Equal(4, vertices.Length);
        Assert.All(vertices, static vertex => Assert.Equal(24f, vertex.StrokeThickness, 3));
        Assert.Equal(8f, vertices.Min(static vertex => vertex.Position.X), 3);
        Assert.Equal(24f, vertices.Max(static vertex => vertex.Position.X), 3);
        Assert.All(vertices, static vertex => Assert.Equal(32f, vertex.Position.Y, 3));
    }

    [Fact]
    public void StaticSplineExtensionAppliesRecordedTransformOnce()
    {
        using var window = new HeadlessWindow(64, 64);
        var context = new DrawingContext();
        context.DrawSpline(
            new Pen(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                12f),
            [new Vector2(4f, 16f), new Vector2(12f, 16f)],
            [0d, 0d, 1d, 1d],
            degree: 1);
        var command = context.Commands[0];
        command.Transform = Matrix4x4.CreateScale(2f, 2f, 1f);
        command.IsPenThicknessLocal = true;
        context.Commands[0] = command;

        using var buffer = window.Compositor.CompileStaticDxf(context);

        var vertices = buffer.VectorVertices
            .Where(static vertex => MathF.Abs(vertex.ShapeType - 203f) < 0.01f)
            .ToArray();
        Assert.NotEmpty(vertices);
        Assert.All(vertices, static vertex => Assert.Equal(24f, vertex.StrokeThickness, 3));
        Assert.Equal(8f, vertices.Min(static vertex => vertex.Position.X), 3);
        Assert.Equal(24f, vertices.Max(static vertex => vertex.Position.X), 3);
        Assert.All(vertices, static vertex => Assert.Equal(32f, vertex.Position.Y, 3));
    }

    private static DxfStaticBuffer CompileContext(
        Compositor compositor,
        RenderCommand command)
    {
        var context = new DrawingContext();
        context.Commands.Add(command);
        return compositor.CompileStaticDxf(context);
    }

    private sealed class EmptyVisual : FrameworkElement
    {
        public EmptyVisual()
        {
            Width = 64f;
            Height = 64f;
        }
    }
}
