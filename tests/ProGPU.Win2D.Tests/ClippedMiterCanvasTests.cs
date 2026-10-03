using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Win2D.Tests;

public sealed class ClippedMiterCanvasTests
{
    [Theory]
    [InlineData(CanvasStrokeTransformBehavior.Normal)]
    [InlineData(CanvasStrokeTransformBehavior.Fixed)]
    [InlineData(CanvasStrokeTransformBehavior.Hairline)]
    public void SourceMiterSelectsClippingForEachWidthMode(CanvasStrokeTransformBehavior mode)
    {
        using var style = new CanvasStrokeStyle { TransformBehavior = mode, MiterLimit = 1 };
        var brush = new SolidColorBrush(Vector4.One);
        Pen clipped = style.GetOrCreatePen(brush, 4);
        Assert.True(clipped.ClipMiterAtLimit);
        Assert.Equal(PenLineJoin.Miter, clipped.LineJoin);
        Assert.Equal(mode == CanvasStrokeTransformBehavior.Hairline, clipped.IsHairline);
        Assert.Equal(mode == CanvasStrokeTransformBehavior.Fixed, clipped.IsFixed);
        Assert.Same(clipped, style.GetOrCreatePen(brush, 4));

        style.LineJoin = CanvasLineJoin.MiterOrBevel;
        Pen bevel = style.GetOrCreatePen(brush, 4);
        Assert.False(bevel.ClipMiterAtLimit);
        Assert.Equal(PenLineJoin.MiterOrBevel, bevel.LineJoin);
        Assert.NotSame(clipped, bevel);
        Assert.True(clipped.ClipMiterAtLimit);
    }

    [Fact]
    public void SourceMutationRetainsEarlierPenAndMaterialIdentity()
    {
        using var style = new CanvasStrokeStyle();
        var brush = new SolidColorBrush(Vector4.One);
        Pen original = style.GetOrCreatePen(brush, 2);
        foreach (CanvasLineJoin join in new[] { CanvasLineJoin.Bevel, CanvasLineJoin.Round, CanvasLineJoin.MiterOrBevel })
        {
            style.LineJoin = join;
            Pen changed = style.GetOrCreatePen(brush, 2);
            Assert.False(changed.ClipMiterAtLimit);
            Assert.Same(changed, style.GetOrCreatePen(brush, 2));
            Assert.NotSame(original, changed);
        }
        style.LineJoin = CanvasLineJoin.Miter;
        Pen restored = style.GetOrCreatePen(brush, 2);
        Assert.True(restored.ClipMiterAtLimit);
        Assert.NotSame(original, restored);
        Assert.True(original.ClipMiterAtLimit);
        var otherBrush = new SolidColorBrush(Vector4.One);
        Pen newMaterial = style.GetOrCreatePen(otherBrush, 2);
        Assert.NotSame(restored, newMaterial);
        Assert.Same(otherBrush, newMaterial.Brush);
        Assert.True(newMaterial.ClipMiterAtLimit);
    }

    [Fact]
    public void ActualCanvasRecordingPreservesBothDefaultCachesAndExplicitStyle()
    {
        // Uses a real Canvas device. The target owns recorded pictures only;
        // this is source recording evidence, not GPU pixel qualification.
        using var device = new CanvasDevice();
        using var target = new RecordingTarget(device);
        using var brush = new CanvasSolidColorBrush(device, Colors.Red);
        using var geometry = CanvasGeometry.CreateRectangle(device, 4, 4, 16, 16);
        using var style = new CanvasStrokeStyle();
        using (var session = new CanvasDrawingSession(target))
        {
            session.DrawRectangle(4, 4, 16, 16, Colors.Red, 2);
            session.DrawRectangle(4, 4, 16, 16, Colors.Red, 2);
            session.DrawGeometry(geometry, 0, 0, brush, 2);
            session.DrawGeometry(geometry, 0, 0, brush, 2);
            session.DrawGeometry(geometry, 0, 0, brush, 2, style);
            session.DrawGeometry(geometry, 0, 0, brush, 2, style);
        }
        Assert.Equal(1, target.EndCount);
        Assert.NotNull(target.Picture);
        var commands = target.Picture.Commands;
        Assert.Equal(6, commands.Length);
        foreach (RenderCommand command in commands)
        {
            Assert.NotNull(command.Pen);
            Assert.True(command.Pen.ClipMiterAtLimit);
            Assert.Equal(PenLineJoin.Miter, command.Pen.LineJoin);
            Assert.Equal(2f, command.Pen.Thickness);
        }
        Assert.Same(commands[0].Pen, commands[1].Pen);
        Assert.Same(commands[2].Pen, commands[3].Pen);
        Assert.Same(commands[4].Pen, commands[5].Pen);
        Assert.NotSame(commands[2].Pen, commands[4].Pen);
        Assert.Same(commands[2].Pen!.Brush, commands[4].Pen!.Brush);
    }

    [Fact]
    public void InvalidJoinDoesNotDiscardClippedGeneration()
    {
        using var style = new CanvasStrokeStyle();
        var brush = new SolidColorBrush(Vector4.One);
        Pen original = style.GetOrCreatePen(brush, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => style.LineJoin = (CanvasLineJoin)4);
        Assert.Same(original, style.GetOrCreatePen(brush, 2));
        Assert.True(original.ClipMiterAtLimit);
    }

    private sealed class RecordingTarget(CanvasDevice device) : ICanvasDrawingSessionTarget, IDisposable
    {
        public CanvasDevice Device => device;
        public float Dpi => 96;
        public Windows.Foundation.Rect DrawingBounds => new(0, 0, 64, 64);
        public GpuPicture? Picture { get; private set; }
        public int EndCount { get; private set; }
        public void ValidateClear() => throw new InvalidOperationException("No clear is recorded by this control.");
        public void Commit(GpuPicture sessionPicture, bool hasClear, Vector4 clearColor)
        {
            Assert.False(hasClear);
            Assert.Null(Picture);
            Picture = sessionPicture;
        }
        public void EndSession() => EndCount++;
        public void Dispose() => Picture?.Dispose();
    }
}
