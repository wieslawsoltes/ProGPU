using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class SourceHitTestGeometryTests
{
    [Fact]
    public void SourceGeometryUnionClearsIndependentlyOfRasterAndTextureState()
    {
        var command = new RenderCommand { Type = RenderCommandType.DrawRect,
            Rect = new Rect(10, 20, 30, 40), Brush = new SolidColorBrush(Vector4.One),
            SourceHitGeometry = new(SourceHitTestGeometryKind.Rectangle, new Vector4(1, 2, 3, 4)) };
        Assert.Equal(default, command.FontTransform);
        Assert.Equal(default, command.TextureCubicCoefficients);
        using var annotated = new GpuPicture([command], [], [], [], []);
        command.SourceHitGeometry = default;
        Assert.Equal(default, command.FontTransform);
        Assert.Equal(default, command.TextureCubicCoefficients);
        Assert.Equal(new Rect(10, 20, 30, 40), command.Rect);
        using var plain = new GpuPicture([command], [], [], [], []);
        Assert.Equal(System.Runtime.CompilerServices.Unsafe.SizeOf<SourceHitTestGeometry>(),
            annotated.CommandStorageBytes - plain.CommandStorageBytes);
        Assert.True(System.Runtime.CompilerServices.Unsafe.SizeOf<RenderCommand>() <= 576);

        var text = new RenderCommand { FontTransform = new(2, 3), HasFontTransform = true };
        text.SourceHitGeometry = default;
        Assert.Equal(new Vector2(2, 3), text.FontTransform);
        var image = new RenderCommand { TextureCubicCoefficients = new(.25f, .75f), HasTextureCubicCoefficients = true };
        image.SourceHitGeometry = default;
        Assert.Equal(new Vector2(.25f, .75f), image.TextureCubicCoefficients);
    }

    [Fact]
    public void PackedCommandFlagsDoNotAliasTextureOptions()
    {
        var command = new RenderCommand {
            IsBold = true, IsItalic = true, HasFontTransform = true,
            UseVectorGlyphRendering = true, PreferGlyphAtlas = true, UseLogicalGlyphAtlasResolution = true,
            IsEdgeAliased = true, IsPenThicknessLocal = true, IsClosed = true, UseGpuTransforms = true,
            HasTextureOpacity = true, AllowExtendedTextureSourceRect = true,
            HasTextureCubicCoefficients = true, SnapTextureToPixels = true, HasImageEffect = true,
            TextureMaxAnisotropy = 255 };
        Assert.True(command.IsBold && command.IsItalic && command.HasFontTransform &&
            command.UseVectorGlyphRendering && command.PreferGlyphAtlas && command.UseLogicalGlyphAtlasResolution &&
            command.IsEdgeAliased && command.IsPenThicknessLocal && command.IsClosed && command.UseGpuTransforms);
        command.IsBold = command.IsItalic = command.HasFontTransform = false;
        command.UseVectorGlyphRendering = command.PreferGlyphAtlas = command.UseLogicalGlyphAtlasResolution = false;
        command.IsEdgeAliased = command.IsPenThicknessLocal = command.IsClosed = command.UseGpuTransforms = false;
        Assert.False(command.IsBold || command.IsItalic || command.HasFontTransform ||
            command.UseVectorGlyphRendering || command.PreferGlyphAtlas || command.UseLogicalGlyphAtlasResolution ||
            command.IsEdgeAliased || command.IsPenThicknessLocal || command.IsClosed || command.UseGpuTransforms);
        Assert.True(command.HasTextureOpacity && command.AllowExtendedTextureSourceRect &&
            command.HasTextureCubicCoefficients && command.SnapTextureToPixels && command.HasImageEffect);
        Assert.Equal(255, command.TextureMaxAnisotropy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyPointScopePreservesRegionDrawingAndRestoresChildPolicy(bool suppressChild)
    {
        var drawing = new DrawingContext();
        void Begin(int id) => drawing.Commands.Add(new RenderCommand {
            Type = RenderCommandType.PushOpacity, FontSize = 1, HitTestId = id,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointEmptyBegin, default) });
        void End() => drawing.Commands.Add(new RenderCommand { Type = RenderCommandType.PopOpacity,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleEnd, default) });
        Begin(701);
        drawing.Commands.Add(new RenderCommand { Type = RenderCommandType.DrawRect,
            HitTestId = 701, Rect = new Rect(-2, 2, 12, 8), Brush = new SolidColorBrush(Vector4.One) });
        End();
        if (suppressChild) Begin(702);
        drawing.Commands.Add(new RenderCommand { Type = RenderCommandType.DrawRect,
            HitTestId = 702, Rect = new Rect(1, 6, 12, 8), Brush = new SolidColorBrush(Vector4.One) });
        if (suppressChild) End();
        using var original = drawing.CreatePictureSnapshot();
        using var picture = original.Clone();
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        for (int i = 0; i < picture.CommandCount; i++)
            capture.AddCommand(picture.GetCommand(i), Matrix4x4.CreateTranslation(10, 20, 0));
        var hits = capture.BuildIndex().Primitives;
        Assert.Equal(2, hits.Count);
        Assert.Equal(701, hits[0].Id);
        Assert.Equal(702, hits[1].Id);
        Assert.Equal(new Vector2(8, 22), hits[0].BoundsMin);
        Assert.True(hits[0].Flags.HasFlag(GpuHitTestPrimitiveFlags.RegionOnly));
        Assert.Equal(suppressChild, hits[1].Flags.HasFlag(GpuHitTestPrimitiveFlags.RegionOnly));
        Assert.False(hits[0].Flags.HasFlag(GpuHitTestPrimitiveFlags.PointOnly));
    }

    [Fact]
    public void EmptyPointScopeRejectsGeometryAndRequiresBalancedClose()
    {
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        var begin = new RenderCommand { Type = RenderCommandType.PushOpacity, FontSize = 1,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointEmptyBegin, Vector4.One) };
        Assert.Throws<NotSupportedException>(() => capture.AddCommand(begin, Matrix4x4.Identity));
        Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
        capture.Clear();
        begin.SourceHitGeometry = new(SourceHitTestGeometryKind.PointEmptyBegin, default);
        capture.AddCommand(begin, Matrix4x4.Identity);
        Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
        capture.AddCommand(new RenderCommand { Type = RenderCommandType.PopOpacity,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleEnd, default) }, Matrix4x4.Identity);
        Assert.Empty(capture.BuildIndex().Primitives);
    }

    [Fact]
    public void PointRegionRejectsOverflowAndRequiresClearAfterFailure()
    {
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        var invalid = new RenderCommand { Type = RenderCommandType.PushOpacity, FontSize = 1,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleBegin,
                new Vector4(float.MaxValue, 0, float.MaxValue, 20)) };
        Assert.Throws<NotSupportedException>(() => capture.AddCommand(invalid, Matrix4x4.Identity));
        Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
        capture.Clear();
        Assert.Empty(capture.BuildIndex().Primitives);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointRegionRetainsBlankSpaceWithoutChangingDrawingOrChildren(bool empty)
    {
        var drawing = new DrawingContext();
        drawing.Commands.Add(new RenderCommand { Type = RenderCommandType.PushOpacity, FontSize = 1,
            HitTestId = 701, SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleBegin, new Vector4(0, 0, 80, 20)) });
        if (!empty) drawing.Commands.Add(new RenderCommand { Type = RenderCommandType.DrawRect,
            HitTestId = 701, Rect = new Rect(-2, 2, 12, 8), Brush = new SolidColorBrush(Vector4.One) });
        drawing.Commands.Add(new RenderCommand { Type = RenderCommandType.PopOpacity,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleEnd, default) });
        drawing.Commands.Add(new RenderCommand { Type = RenderCommandType.DrawRect,
            HitTestId = 702, Rect = new Rect(3, 4, 10, 8), Brush = new SolidColorBrush(Vector4.One) });
        using var original = drawing.CreatePictureSnapshot();
        using var picture = original.Clone();
        Assert.True(GpuPictureBounds.TryGetBounds(picture, out var raster));
        Assert.True(raster.Width < 80); // the input rectangle is not paint or a clip
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        var transform = Matrix4x4.CreateTranslation(10, 20, 0);
        capture.AddCommand(new RenderCommand { Type = RenderCommandType.PushClip, Rect = new Rect(12, 21, 70, 30) }, Matrix4x4.Identity);
        for (int i = 0; i < picture.CommandCount; ++i) capture.AddCommand(picture.GetCommand(i), transform);
        capture.AddCommand(new RenderCommand { Type = RenderCommandType.PopClip }, Matrix4x4.Identity);
        var hits = capture.BuildIndex().Primitives;
        Assert.Equal(empty ? 2 : 3, hits.Count);
        Assert.Equal(701, hits[0].Id);
        Assert.Equal(new Vector2(12, 21), hits[0].BoundsMin);
        Assert.Equal(new Vector2(82, 40), hits[0].BoundsMax);
        Assert.True(hits[0].Flags.HasFlag(GpuHitTestPrimitiveFlags.PointOnly));
        if (!empty) Assert.True(hits[1].Flags.HasFlag(GpuHitTestPrimitiveFlags.RegionOnly));
        Assert.Equal(702, hits[^1].Id);
        Assert.Equal(GpuHitTestPrimitiveFlags.Visible | GpuHitTestPrimitiveFlags.HitTestVisible, hits[^1].Flags);
    }

    [Fact]
    public void PointRegionScopeMustBalanceBeforePublication()
    {
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddCommand(new RenderCommand { Type = RenderCommandType.PushOpacity, FontSize = 1,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleBegin, new Vector4(0, 0, 0, 0)) }, Matrix4x4.Identity);
        Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
        capture.AddCommand(new RenderCommand { Type = RenderCommandType.PopOpacity,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleEnd, default) }, Matrix4x4.Identity);
        Assert.Single(capture.BuildIndex().Primitives);
    }

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
