using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class SourceHitTestGeometryTests
{
    [Theory]
    [InlineData(RenderCommandType.DrawRect, SourceHitTestGeometryKind.Rectangle)]
    [InlineData(RenderCommandType.DrawRoundedRect, SourceHitTestGeometryKind.RoundedRectangle)]
    [InlineData(RenderCommandType.DrawEllipse, SourceHitTestGeometryKind.Ellipse)]
    public void RetainedPrimitiveKeepsRasterAndOriginalInputIndependent(RenderCommandType type, SourceHitTestGeometryKind kind)
    {
        var command = new RenderCommand
        {
            Type = type, HitTestId = 601, Brush = new SolidColorBrush(Vector4.One),
            Rect = new Rect(10, 20, 30, 40), Position2 = new(25, 40), RadiusX = 15, RadiusY = 20,
            SourceHitGeometry = new(kind, type == RenderCommandType.DrawEllipse
                ? new Vector4(25.25f, 40.25f, 15, 20) : new Vector4(10.25f, 20.25f, 30, 40))
        };
        using var picture = new GpuPicture([command], [], [], [], []);
        using var clone = picture.Clone();
        var retained = clone.GetCommand(0);
        Assert.Equal(command.SourceHitGeometry, retained.SourceHitGeometry);
        Assert.Equal(command.Rect, retained.Rect);
        Assert.Equal(command.Position2, retained.Position2);
        Assert.Equal(command.SourceHitGeometry, clone.Commands[0].SourceHitGeometry);
        Assert.True(GpuPictureBounds.TryGetBounds(clone, out var rasterBounds));
        Assert.Equal(new Rect(10, 20, 30, 40), rasterBounds);
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddCommand(retained, Matrix4x4.CreateTranslation(5, 6, 0));
        var hit = Assert.Single(capture.BuildIndex().Primitives);
        Assert.Equal(601, hit.Id);
        Assert.Equal(new Vector2(15.25f, 26.25f), hit.BoundsMin);
        Assert.Equal(new Vector2(45.25f, 66.25f), hit.BoundsMax);
    }

    [Theory]
    [InlineData(SourceHitTestGeometryKind.Excluded)]
    [InlineData(SourceHitTestGeometryKind.Rectangle)]
    public void MetadataCannotHideStateScopesOrPublishPartialInput(SourceHitTestGeometryKind kind)
    {
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddCommand(new RenderCommand { Type = RenderCommandType.DrawRect,
            Rect = new Rect(0, 0, 10, 10), Brush = new SolidColorBrush(Vector4.One) }, Matrix4x4.Identity);
        Assert.Throws<NotSupportedException>(() => capture.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.PushClip, Rect = new Rect(1, 2, 3, 4),
            SourceHitGeometry = new(kind, new Vector4(1, 2, 3, 4))
        }, Matrix4x4.Identity));
        Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
        capture.Clear();
        Assert.Empty(capture.BuildIndex().Primitives);
    }

    [Fact]
    public void ArchiveRejectsSourceMetadataInsteadOfSilentlyLosingIt()
    {
        using var picture = new GpuPicture([new RenderCommand
        {
            Type = RenderCommandType.DrawRect, Rect = new Rect(10, 20, 30, 40),
            Brush = new SolidColorBrush(Vector4.One),
            SourceHitGeometry = new(SourceHitTestGeometryKind.Rectangle, new Vector4(10.25f, 20.25f, 30, 40))
        }], [], [], [], []);
        using var wrapper = new SkiaSharp.SKPicture(picture.Clone(), new SkiaSharp.SKRect(10, 20, 40, 60));
        Assert.Throws<NotSupportedException>(() => wrapper.Serialize());
    }

    [Fact]
    public void LogicalImageInputRemainsAuthoritativeOverAnnotatedContents()
    {
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddCommand(new RenderCommand { Type = RenderCommandType.PushClip,
            IsImageHitTestScope = true, Rect = new Rect(1, 2, 3, 4), HitTestId = 602 }, Matrix4x4.Identity);
        capture.AddCommand(new RenderCommand { Type = RenderCommandType.DrawRect,
            Rect = new Rect(50, 60, 70, 80), Brush = new SolidColorBrush(Vector4.One),
            SourceHitGeometry = new(SourceHitTestGeometryKind.Rectangle, new Vector4(50.25f, 60.25f, 70, 80)) }, Matrix4x4.Identity);
        capture.AddCommand(new RenderCommand { Type = RenderCommandType.PopClip }, Matrix4x4.Identity);
        var hit = Assert.Single(capture.BuildIndex().Primitives);
        Assert.Equal(602, hit.Id);
        Assert.Equal(new Vector2(1, 2), hit.BoundsMin);
        Assert.Equal(new Vector2(4, 6), hit.BoundsMax);
    }
}
