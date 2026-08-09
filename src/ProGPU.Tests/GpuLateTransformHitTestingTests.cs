using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class GpuLateTransformHitTestingTests
{
    [Fact]
    public void LateGpuTransformHitsResolvedDeviceCoordinatesOnly()
    {
        using var window = new HeadlessWindow(128, 128);
        window.Content = new CommandVisual(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(0f, 0f, 10f, 10f),
            Brush = WhiteBrush(),
            HitTestId = 701,
            UseGpuTransforms = true,
            CameraView = Matrix4x4.CreateScale(2f, 3f, 1f) *
                Matrix4x4.CreateTranslation(40f, 30f, 0f)
        });

        window.Render();

        Assert.True(window.Compositor.TryHitTestPoint(
            new Vector2(50f, 45f),
            out var transformedHit));
        Assert.Equal(701, transformedHit.Id);
        Assert.False(window.Compositor.TryHitTestPoint(
            new Vector2(5f, 5f),
            out _));
    }

    [Fact]
    public void NestedPictureCarriesResolvedLateTransformIntoChildHitGeometry()
    {
        using var child = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Brush = WhiteBrush(),
                    HitTestId = 702
                }
            ],
            [],
            [],
            [],
            []);
        using var parent = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = child,
                    Transform = Matrix4x4.CreateTranslation(5f, 0f, 0f)
                }
            ],
            [],
            [],
            [],
            []);
        using var window = new HeadlessWindow(128, 128);
        window.Content = new CommandVisual(new RenderCommand
        {
            Type = RenderCommandType.DrawPicture,
            Picture = parent,
            UseGpuTransforms = true,
            CameraView = Matrix4x4.CreateScale(2f, 3f, 1f) *
                Matrix4x4.CreateTranslation(40f, 30f, 0f)
        });

        window.Render();

        Assert.True(window.Compositor.TryHitTestPoint(
            new Vector2(60f, 45f),
            out var transformedHit));
        Assert.Equal(702, transformedHit.Id);
        Assert.False(window.Compositor.TryHitTestPoint(
            new Vector2(5f, 5f),
            out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidAffineTransformDoesNotCreateGhostHitPrimitive(bool nonFinite)
    {
        var transform = nonFinite
            ? new Matrix4x4(
                float.NaN, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f)
            : Matrix4x4.CreateScale(0f, 1f, 1f);
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(0f, 0f, 10f, 10f),
            Brush = WhiteBrush(),
            HitTestId = 703
        }, transform);

        var index = builder.BuildIndex();

        Assert.Empty(index.Primitives);
    }

    [Theory]
    [InlineData(InvalidPathTransformKind.Collapsed)]
    [InlineData(InvalidPathTransformKind.NonFinite)]
    [InlineData(InvalidPathTransformKind.OverflowWhenComposed)]
    public void InvalidComposedPathTransformDoesNotCreateGhostHitGeometry(
        InvalidPathTransformKind kind)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(Vector2.Zero, isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(10f, 0f)));
        figure.Segments.Add(new LineSegment(new Vector2(10f, 10f)));
        figure.Segments.Add(new LineSegment(new Vector2(0f, 10f)));
        path.Figures.Add(figure);
        var commandTransform = kind switch
        {
            InvalidPathTransformKind.Collapsed =>
                Matrix4x4.CreateScale(0f, 1f, 1f),
            InvalidPathTransformKind.NonFinite => new Matrix4x4(
                float.NaN, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, 0f, 0f, 1f),
            _ => Matrix4x4.CreateScale(float.MaxValue, 1f, 1f)
        };
        var activeTransform = kind == InvalidPathTransformKind.OverflowWhenComposed
            ? Matrix4x4.CreateScale(2f, 1f, 1f)
            : Matrix4x4.CreateTranslation(20f, 30f, 0f);
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = path,
            GeometryCache = RenderCommandGeometryCache.ForPath(path),
            Brush = WhiteBrush(),
            Transform = commandTransform,
            HitTestId = 708
        }, activeTransform);

        var index = builder.BuildIndex();

        Assert.Empty(index.Primitives);
        Assert.Empty(index.PathSegments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidComposedSeriesTransformDoesNotCreateGhostHitGeometry(
        bool isScatter)
    {
        var context = new DrawingContext();
        var brush = WhiteBrush();
        if (isScatter)
        {
            context.DrawGpuScatterSeries(
                [5f, 5f, 20f, 20f],
                pointsCount: 2,
                radius: 2f,
                brush);
        }
        else
        {
            context.DrawGpuLineSeries(
                [0f, 0f, 10f, 0f, 10f, 10f],
                pointsCount: 3,
                thickness: 2f,
                brush);
        }

        var command = Assert.Single(context.Commands);
        command.Transform = Matrix4x4.CreateScale(0f, 1f, 1f);
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(command, Matrix4x4.Identity, context, id: 709);

        var index = builder.BuildIndex();

        Assert.Empty(index.Primitives);
        Assert.Empty(index.PathSegments);
    }

    [Fact]
    public void InvalidClipRejectsDescendantsAndPopRestoresFollowingHits()
    {
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.PushClip,
            Rect = new Rect(0f, 0f, 10f, 10f)
        }, Matrix4x4.CreateScale(0f, 1f, 1f));
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(0f, 0f, 10f, 10f),
            Brush = WhiteBrush(),
            HitTestId = 704
        }, Matrix4x4.Identity);
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.PopClip
        }, Matrix4x4.Identity);
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(20f, 0f, 10f, 10f),
            Brush = WhiteBrush(),
            HitTestId = 705
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex();

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(705, primitive.Id);
    }

    [Fact]
    public void OpacityMaskStateDoesNotCreatePhantomBoundsPrimitive()
    {
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.PushOpacityMask,
            Rect = new Rect(0f, 0f, 100f, 100f),
            Brush = WhiteBrush(),
            HitTestId = 706
        }, Matrix4x4.Identity);
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(10f, 10f, 10f, 10f),
            Brush = WhiteBrush(),
            HitTestId = 707
        }, Matrix4x4.Identity);
        builder.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.PopOpacityMask
        }, Matrix4x4.Identity);

        var index = builder.BuildIndex();

        var primitive = Assert.Single(index.Primitives);
        Assert.Equal(707, primitive.Id);
    }

    private static SolidColorBrush WhiteBrush() =>
        new(new Vector4(1f, 1f, 1f, 1f));

    private sealed class CommandVisual : FrameworkElement
    {
        private readonly RenderCommand _command;

        public CommandVisual(RenderCommand command)
        {
            _command = command;
            Width = 128f;
            Height = 128f;
        }

        public override void OnRender(DrawingContext context) =>
            context.Commands.Add(_command);
    }

    public enum InvalidPathTransformKind
    {
        Collapsed,
        NonFinite,
        OverflowWhenComposed
    }
}
