using System;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Backend;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class SourceVisualHitTestTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(2f)]
    public unsafe void CachedSourceInputRetainsUnsnappedGeometryAcrossReuseAndUpdates(float cacheScale)
    {
        using var window = new HeadlessWindow(128, 96);
        using var target = new GpuTexture(window.Context, 128, 96,
            TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Cached source input");
        using var compositor = new Compositor(window.Context, TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = true, EnableCompiledSceneCache = false });
        var root = new SourceVisual { Size = new Vector2(128, 96) };
        var cached = new SourceVisual { HitTestId = 701, Offset = new Vector2(5.25f, 6.5f),
            Size = new Vector2(100, 80), ClipBounds = new Rect(0, 0, 75, 70), CacheAsLayer = true,
            LayerCacheRenderScale = cacheScale, LayerCacheSnapsToDevicePixels = true };
        var brush = new SolidColorBrush(Vector4.One);
        cached.SourceHitTestCommands.DrawRectangle(brush, null, new Rect(8, 10, 32, 24));
        root.AddChild(cached);
        var sibling = new SourceVisual { HitTestId = 703 };
        sibling.SourceHitTestCommands.DrawRectangle(brush, null, new Rect(1, 2, 3, 4));
        root.AddChild(sibling);

        void RenderAndCheck(Vector2? sourceMin, Vector2? sourceMax)
        {
            compositor.RenderScene(root, 128, 96, target.ViewPtr);
            var hits = Assert.IsType<GpuHitTestIndex>(compositor.LastHitTestIndex).Primitives;
            Assert.Equal(sourceMin.HasValue ? 2 : 1, hits.Count);
            if (sourceMin.HasValue)
            {
                Assert.Equal(701, hits[0].Id);
                Assert.Equal(sourceMin.Value, hits[0].BoundsMin);
                Assert.Equal(sourceMax!.Value, hits[0].BoundsMax);
                Assert.Equal(GpuHitTestPrimitiveFlags.Visible | GpuHitTestPrimitiveFlags.HitTestVisible, hits[0].Flags);
            }
            Assert.Equal(703, hits[^1].Id); // outside the cache: hit writes and clipping restored
            Assert.Equal(new Vector2(1, 2), hits[^1].BoundsMin);
            Assert.Equal(new Vector2(4, 6), hits[^1].BoundsMax);
        }

        RenderAndCheck(new(13.25f, 16.5f), new(45.25f, 40.5f));
        var texture = cached.LayerTexture;
        int renderCalls = cached.RenderCalls;
        RenderAndCheck(new(13.25f, 16.5f), new(45.25f, 40.5f));
        Assert.Same(texture, cached.LayerTexture);
        Assert.Equal(renderCalls, cached.RenderCalls);
        if (cacheScale == 0)
        {
            Assert.Null(texture);
            Assert.Equal(0, renderCalls); // input exists even when raster cache production is suppressed
        }
        else
            Assert.NotNull(texture);

        cached.Offset = new Vector2(10.75f, 8.25f);
        RenderAndCheck(new(18.75f, 18.25f), new(50.75f, 42.25f));
        cached.SourceHitTestCommands.Clear();
        cached.SourceHitTestCommands.DrawRectangle(brush, null, new Rect(60, 20, 30, 10));
        cached.Invalidate();
        RenderAndCheck(new(70.75f, 28.25f), new(85.75f, 38.25f)); // actual source clip, not cache bounds
        cached.SourceHitTestCommands.Clear();
        cached.Invalidate();
        RenderAndCheck(null, null);
    }

    [Theory]
    [InlineData(0, false)] // Gaussian blur
    [InlineData(1, false)] // Zero-radius blur still uses the source input policy
    [InlineData(2, false)] // Shadow and source are not two input rectangles
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public unsafe void CompositedSourceEffectsRetainOwnAndChildInputWithoutPadding(int variant, bool cached)
    {
        using var window = new HeadlessWindow(128, 96);
        using var target = new GpuTexture(window.Context, 128, 96,
            TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Source effect input");
        using var compositor = new Compositor(window.Context, TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = true, EnableCompiledSceneCache = true });
        var root = new SourceVisual { Size = new Vector2(128, 96) };
        var effectRoot = new SourceVisual { HitTestId = 701, Offset = new Vector2(5, 6),
            Size = new Vector2(100, 80), ClipBounds = new Rect(0, 0, 75, 70),
            EffectContentBounds = new Rect(0, 0, 90, 60), CacheAsLayer = cached,
            Effect = variant == 2 ? new DropShadowEffect { BlurRadius = 9, Offset = new Vector2(4, 4) }
                : new BlurEffect { BlurRadius = variant == 0 ? 2.5f : 0 } };
        var brush = new SolidColorBrush(Vector4.One);
        effectRoot.SourceHitTestCommands.DrawRectangle(brush, null, new Rect(8, 10, 32, 24));
        var child = new SourceVisual { HitTestId = 702, Offset = new Vector2(30, 30), Size = new Vector2(60, 16) };
        child.SourceHitTestCommands.Commands.Add(new RenderCommand { Type = RenderCommandType.PushOpacity, FontSize = 1,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleBegin, new Vector4(0, 0, 60, 16)) });
        child.SourceHitTestCommands.Commands.Add(new RenderCommand { Type = RenderCommandType.PopOpacity,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleEnd, default) });
        effectRoot.AddChild(child); root.AddChild(effectRoot);
        var sibling = new SourceVisual { HitTestId = 703 };
        sibling.SourceHitTestCommands.DrawRectangle(brush, null, new Rect(1, 2, 3, 4));
        root.AddChild(sibling);

        void RenderAndCheck(float childX, bool ownContent)
        {
            compositor.RenderScene(root, 128, 96, target.ViewPtr);
            var hits = Assert.IsType<GpuHitTestIndex>(compositor.LastHitTestIndex).Primitives;
            Assert.Equal(ownContent ? 3 : 2, hits.Count);
            if (ownContent)
            {
                Assert.Equal(701, hits[0].Id);
                Assert.Equal(new Vector2(13, 16), hits[0].BoundsMin);
                Assert.Equal(new Vector2(45, 40), hits[0].BoundsMax);
                Assert.Equal(GpuHitTestPrimitiveFlags.Visible | GpuHitTestPrimitiveFlags.HitTestVisible, hits[0].Flags);
            }
            var point = hits[^2];
            Assert.Equal(702, point.Id);
            Assert.True(point.Flags.HasFlag(GpuHitTestPrimitiveFlags.PointOnly));
            Assert.Equal(new Vector2(childX, 36), point.BoundsMin);
            Assert.Equal(new Vector2(80, 52), point.BoundsMax);
            Assert.Equal(703, hits[^1].Id);
            Assert.Equal(new Vector2(1, 2), hits[^1].BoundsMin);
            Assert.Equal(new Vector2(4, 6), hits[^1].BoundsMax);
        }

        RenderAndCheck(35, true);
        int rasterCalls = effectRoot.RenderCalls;
        RenderAndCheck(35, true);
        Assert.Equal(rasterCalls, effectRoot.RenderCalls);
        child.Offset = new Vector2(40, 30);
        RenderAndCheck(45, true);
        effectRoot.SourceHitTestCommands.Clear(); effectRoot.Invalidate();
        RenderAndCheck(45, false);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public unsafe void SourcePointRegionDoesNotAlsoPublishVisualSizeForSelection(float opacity)
    {
        using var window = new HeadlessWindow(100, 60);
        using var target = new GpuTexture(window.Context, 100, 60,
            TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Source point region");
        using var compositor = new Compositor(window.Context, TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = true });
        var source = new SourceVisual { HitTestId = 701, Size = new Vector2(100, 60), Opacity = opacity };
        source.SourceHitTestCommands.Commands.Add(new RenderCommand { Type = RenderCommandType.PushOpacity, FontSize = 1,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleBegin, new Vector4(0, 0, 80, 20)) });
        source.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(-2, 2, 12, 8));
        source.SourceHitTestCommands.Commands.Add(new RenderCommand { Type = RenderCommandType.PopOpacity,
            SourceHitGeometry = new(SourceHitTestGeometryKind.PointRectangleEnd, default) });
        compositor.RenderScene(source, 100, 60, target.ViewPtr);
        var hits = Assert.IsType<GpuHitTestIndex>(compositor.LastHitTestIndex).Primitives;
        Assert.Equal(2, hits.Count);
        Assert.Equal(701, hits[0].Id); Assert.Equal(701, hits[1].Id);
        Assert.True(hits[0].Flags.HasFlag(GpuHitTestPrimitiveFlags.PointOnly));
        Assert.True(hits[1].Flags.HasFlag(GpuHitTestPrimitiveFlags.RegionOnly));
        Assert.Equal(new Vector2(80, 20), hits[0].BoundsMax);
        Assert.Equal(new Vector2(-2, 2), hits[1].BoundsMin);
    }

    [Fact]
    public void SourceTriangleClipRetainsWorldEdgesAndRestoresSiblingInput()
    {
        // Native scene 9820; the source harness selects through this triangle.
        var path = new PathGeometry { FillRule = FillRule.Nonzero };
        var figure = new PathFigure(new Vector2(8, 8), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(88, 8)));
        figure.Segments.Add(new LineSegment(new Vector2(8, 88)));
        path.Figures.Add(figure);
        var root = new SourceVisual();
        var clipped = new SourceVisual { HitTestId = 701, Opacity = 0,
            ClipBounds = new Rect(0, 0, 200, 200),
            GeometryClip = path.CreateTransformed(Matrix4x4.CreateTranslation(5, 6, 0)) };
        var command = new RenderCommand { Type = RenderCommandType.DrawRect,
            Rect = new Rect(8, 8, 144, 80), Brush = new SolidColorBrush(Vector4.One),
            Transform = Matrix4x4.CreateTranslation(2, 3, 0) };
        clipped.SourceHitTestCommands.Commands.Add(command);
        clipped.SourceHitTestCommands.Commands.Add(command);
        root.AddChild(clipped);
        var sibling = new SourceVisual { HitTestId = 702 };
        sibling.SourceHitTestCommands.DrawRectangle(command.Brush, null, command.Rect);
        root.AddChild(sibling);
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddSourceVisual(root, Matrix4x4.Identity);
        var index = capture.BuildIndex();
        Assert.Equal(3, index.Primitives.Count);
        var hit = index.Primitives[0];
        Assert.Equal(701, hit.Id);
        Assert.Equal(new Vector2(13, 14), hit.BoundsMin);
        Assert.Equal(new Vector2(93, 91), hit.BoundsMax);
        Assert.Equal(3u, hit.ClipSegmentCount);
        Assert.Equal(hit.ClipStartSegment, index.Primitives[1].ClipStartSegment);
        Assert.Equal(702, index.Primitives[2].Id);
        Assert.Equal(0u, index.Primitives[2].ClipSegmentCount);
        var first = index.PathSegments[(int)hit.ClipStartSegment];
        Assert.Equal(new Vector2(13, 14), first.P0);
        Assert.Equal(new Vector2(93, 14), first.P1);
        Assert.Equal(0, clipped.RenderCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityEffectsPreserveSourceInputAndNestedClips(bool shadow)
    {
        // Paired with native scene 9816. Raster bounds deliberately exceed input.
        EffectBase effect = shadow ? new DropShadowEffect() : new BlurEffect { BlurRadius = 9 };
        var root = new SourceVisual();
        var outer = new SourceVisual { Effect = effect, Opacity = 0,
            ClipBounds = new Rect(15, 26, 10, 10), EffectContentBounds = new Rect(-100, -100, 300, 300) };
        var inner = new SourceVisual { Effect = new BlurEffect(), ClipBounds = new Rect(20, 28, 20, 20) };
        SourceVisual Draw(int id)
        {
            var visual = new SourceVisual { HitTestId = id, Offset = new Vector2(5, 6) };
            visual.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(10, 20, 30, 40));
            return visual;
        }
        inner.AddChild(Draw(501)); outer.AddChild(inner); outer.AddChild(Draw(502));
        root.AddChild(outer); root.AddChild(Draw(503));
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddSourceVisual(root, Matrix4x4.Identity);
        var hits = capture.BuildIndex().Primitives;
        Assert.Equal(3, hits.Count);
        Assert.Equal(new Vector2(20, 28), hits[0].BoundsMin);
        Assert.Equal(new Vector2(25, 36), hits[0].BoundsMax);
        Assert.Equal(new Vector2(15, 26), hits[1].BoundsMin);
        Assert.Equal(new Vector2(25, 36), hits[1].BoundsMax);
        Assert.Equal(new Vector2(15, 26), hits[2].BoundsMin);
        Assert.Equal(new Vector2(45, 66), hits[2].BoundsMax);
        Assert.Equal(0, outer.RenderCalls + inner.RenderCalls);
        outer.Effect = new UnmappedEffect();
        capture.Clear();
        Assert.Throws<NotSupportedException>(() => capture.AddSourceVisual(root, Matrix4x4.Identity));
        Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
    }

    private sealed class UnmappedEffect : EffectBase { }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public unsafe void CompositorCapturesZeroOpacitySourceWithoutRendering(bool enableHitTesting)
    {
        using var window = new HeadlessWindow(64, 64);
        using var target = new GpuTexture(window.Context, 64, 64,
            TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "Source opacity input target");
        using var compositor = new Compositor(window.Context, TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = enableHitTesting });
        var source = new SourceVisual { Opacity = 0, HitTestId = 4321, Size = new Vector2(64) };
        source.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(new Vector4(1, 0, 0, 1)),
            null, new Rect(10, 20, 30, 40));
        compositor.RenderScene(source, 64, 64, target.ViewPtr);
        Assert.Equal(0, source.RenderCalls);
        Assert.Equal(0, compositor.Metrics.VectorVerticesCount);
        if (enableHitTesting)
        {
            var hit = Assert.Single(Assert.IsType<GpuHitTestIndex>(compositor.LastHitTestIndex).Primitives);
            Assert.Equal(4321, hit.Id);
            Assert.Equal(new Vector2(10, 20), hit.BoundsMin);
            Assert.Equal(new Vector2(40, 60), hit.BoundsMax);
        }
        else
        {
            Assert.Null(compositor.LastHitTestIndex);
        }
        byte[] pixels = target.ReadPixels();
        Assert.Equal(0, pixels[(25 * 64 + 15) * 4]); // no red source pixel
        source.SourceHitTestCommands.Clear();
        source.Invalidate();
        compositor.RenderScene(source, 64, 64, target.ViewPtr);
        if (enableHitTesting)
            Assert.Empty(Assert.IsType<GpuHitTestIndex>(compositor.LastHitTestIndex).Primitives);
        var generic = new DrawingVisual { Opacity = 0, HitTestId = 4322, Size = new Vector2(64) };
        generic.Context.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(0, 0, 64, 64));
        compositor.RenderScene(generic, 64, 64, target.ViewPtr);
        if (enableHitTesting)
            Assert.Empty(Assert.IsType<GpuHitTestIndex>(compositor.LastHitTestIndex).Primitives);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void SourceOpacityRetainsRealGeometryClipsAndOwners(float opacity)
    {
        // Matches the source-layer fixture's two owners and transformed clip.
        var root = new SourceVisual { Opacity = opacity, Size = new Vector2(1000) };
        var child = new SourceVisual
        {
            HitTestId = 4321, Opacity = 0, Offset = new Vector2(5, 6),
            ClipBounds = new Rect(10, 20, 10, 10), Size = new Vector2(200)
        };
        child.SourceHitTestCommands.PushOpacity(0, affectsHitTesting: false);
        child.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(10, 20, 30, 40));
        child.SourceHitTestCommands.PopOpacity();
        root.AddChild(child);
        var sibling = new SourceVisual { HitTestId = 4322 };
        sibling.SourceHitTestCommands.DrawEllipse(new SolidColorBrush(Vector4.One), null, new Vector2(30, 40), 10, 5);
        root.AddChild(sibling);
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddSourceVisual(root, Matrix4x4.Identity);
        var index = capture.BuildIndex();
        Assert.Equal(2, index.Primitives.Count); // no root/child Size rectangle
        Assert.Equal(4321, index.Primitives[0].Id);
        Assert.Equal(new Vector2(15, 26), index.Primitives[0].BoundsMin);
        Assert.Equal(new Vector2(25, 36), index.Primitives[0].BoundsMax);
        Assert.Equal(0u, index.Primitives[0].ClipSegmentCount); // exact axis-aligned bounds clip
        Assert.Equal(4322, index.Primitives[1].Id);
        Assert.Equal(GpuHitTestPrimitiveKind.EllipseFill, index.Primitives[1].Kind);
        Assert.Equal(0u, index.Primitives[1].ClipSegmentCount);
        Assert.Equal(opacity, root.Opacity);
        Assert.Equal(0f, child.SourceHitTestCommands.Commands[0].FontSize);
        Assert.Equal(0, root.RenderCalls + child.RenderCalls + sibling.RenderCalls);

        child.IsVisible = false;
        capture.Clear();
        capture.AddSourceVisual(root, Matrix4x4.Identity);
        Assert.Equal(4322, Assert.Single(capture.BuildIndex().Primitives).Id);
        sibling.SourceHitTestCommands.Clear();
        sibling.Invalidate();
        capture.Clear();
        capture.AddSourceVisual(root, Matrix4x4.Identity);
        Assert.Empty(capture.BuildIndex().Primitives);
    }

    [Fact]
    public void SourceCapturePreservesPictureTransformsAndLogicalImages()
    {
        var nested = new DrawingContext();
        nested.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(1, 2, 3, 4));
        using var picture = nested.CreatePictureSnapshot();
        var visual = new SourceVisual { HitTestId = 71, Offset = new Vector2(10, 20) };
        visual.SourceHitTestCommands.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawPicture, Picture = picture,
            Transform = Matrix4x4.CreateTranslation(5, 6, 0)
        });
        visual.SourceHitTestCommands.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushClip, IsImageHitTestScope = true,
            Rect = new Rect(30, 40, 50, 60), HitTestId = 72
        });
        visual.SourceHitTestCommands.Commands.Add(new RenderCommand { Type = RenderCommandType.PushOpacityMask });
        visual.SourceHitTestCommands.Commands.Add(new RenderCommand { Type = RenderCommandType.PopOpacityMask });
        visual.SourceHitTestCommands.PopClip();
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddSourceVisual(visual, Matrix4x4.Identity);
        var index = capture.BuildIndex();
        Assert.Equal(2, index.Primitives.Count);
        Assert.Equal(new Vector2(16, 28), index.Primitives[0].BoundsMin);
        Assert.Equal(new Vector2(19, 32), index.Primitives[0].BoundsMax);
        Assert.Equal(72, index.Primitives[1].Id);
        Assert.Equal(new Vector2(40, 60), index.Primitives[1].BoundsMin);
        Assert.Equal(new Vector2(90, 120), index.Primitives[1].BoundsMax);
    }

    [Fact]
    public unsafe void EmbeddedSourceMutationInvalidatesAnOpacityCulledScene()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(window.Context, 32, 32,
            TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment,
            "Embedded source opacity target");
        using var compositor = new Compositor(window.Context, TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = true, EnableCompiledSceneCache = true });
        var parent = new SourceVisual { Opacity = 0 };
        var child = new SourceVisual { HitTestId = 73 };
        child.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(1, 2, 3, 4));
        parent.SourceHitTestCommands.Commands.Add(new RenderCommand { Type = RenderCommandType.DrawVisual, Visual = child });
        compositor.RenderScene(parent, 32, 32, target.ViewPtr);
        Assert.Single(Assert.IsType<GpuHitTestIndex>(compositor.LastHitTestIndex).Primitives);
        compositor.RenderScene(parent, 32, 32, target.ViewPtr);
        Assert.Single(Assert.IsType<GpuHitTestIndex>(compositor.LastHitTestIndex).Primitives);
        long parentVersion = parent.ChangeVersion;
        child.SourceHitTestCommands.Clear();
        child.Invalidate();
        Assert.Equal(parentVersion, parent.ChangeVersion); // embedded, not parented
        compositor.RenderScene(parent, 32, 32, target.ViewPtr);
        Assert.Empty(Assert.IsType<GpuHitTestIndex>(compositor.LastHitTestIndex).Primitives);
        Assert.Equal(0, parent.RenderCalls + child.RenderCalls);
    }

    [Fact]
    public void RotatedSourceRectangleClipRetainsItsEdges()
    {
        var visual = new SourceVisual
        {
            HitTestId = 74, Opacity = 0,
            LocalCompositeClip = new VisualCompositeClip(new Rect(-5, -5, 10, 10), Matrix4x4.CreateRotationZ(0.5f))
        };
        visual.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(-20, -20, 40, 40));
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddSourceVisual(visual, Matrix4x4.Identity);
        Assert.True(Assert.Single(capture.BuildIndex().Primitives).ClipSegmentCount >= 4);
    }

    [Theory]
    [InlineData(RenderCommandType.PushOpacityMask)]
    [InlineData(RenderCommandType.DrawStaticDxf)]
    [InlineData(RenderCommandType.DrawGlyphRun)]
    public void UnsupportedSourceCommandCannotPublishPartialGeometry(RenderCommandType unsupported)
    {
        var visual = new SourceVisual { HitTestId = 1 };
        visual.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(0, 0, 10, 10));
        visual.SourceHitTestCommands.Commands.Add(new RenderCommand { Type = unsupported });
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        Assert.Throws<NotSupportedException>(() => capture.AddSourceVisual(visual, Matrix4x4.Identity));
        Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
        capture.Clear();
        Assert.Empty(capture.BuildIndex().Primitives);
    }

    [Theory]
    [InlineData(RenderCommandType.PopClip)]
    [InlineData(RenderCommandType.PushClip)]
    [InlineData(RenderCommandType.PopOpacity)]
    public void CommandScopesCannotEscapeTheirVisual(RenderCommandType invalid)
    {
        var visual = new SourceVisual { ClipBounds = new Rect(0, 0, 10, 10) };
        visual.SourceHitTestCommands.Commands.Add(new RenderCommand { Type = invalid, Rect = new Rect(0, 0, 5, 5) });
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        Assert.Throws<InvalidOperationException>(() => capture.AddSourceVisual(visual, Matrix4x4.Identity));
        Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
    }

    [Fact]
    public void MissingSourceContractAndRequiredCacheFailExplicitly()
    {
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        Assert.Throws<NotSupportedException>(() => capture.AddSourceVisual(new DrawingVisual(), Matrix4x4.Identity));
        capture.Clear();
        Assert.Throws<NotSupportedException>(() => capture.AddSourceVisual(new RequiredCacheSourceVisual(), Matrix4x4.Identity));
    }

    [Fact]
    public void ExistingGeometryClipCannotHideAnUnavailableNestedClip()
    {
        var visual = new SourceVisual
        {
            GeometryClip = PrimitivePathGeometry.CreateRectangle(0, 0, 20, 20)
        };
        visual.SourceHitTestCommands.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushGeometryClip, Path = new PathGeometry { IsCombined = true }
        });
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        Assert.Throws<NotSupportedException>(() => capture.AddSourceVisual(visual, Matrix4x4.Identity));
        Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
    }

    [Fact]
    public void UnownedLogicalImageDoesNotInventAnInputOwner()
    {
        var visual = new SourceVisual();
        visual.SourceHitTestCommands.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.PushClip, IsImageHitTestScope = true,
            Rect = new Rect(0, 0, 10, 10)
        });
        visual.SourceHitTestCommands.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(0, 0, 5, 5));
        visual.SourceHitTestCommands.PopClip();
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddSourceVisual(visual, Matrix4x4.Identity);
        Assert.Empty(capture.BuildIndex().Primitives);
    }

    [Fact]
    public void EnclosingImageScopeDoesNotEnterSourceVisualClips()
    {
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        capture.AddCommand(new RenderCommand
        {
            Type = RenderCommandType.PushClip, IsImageHitTestScope = true,
            Rect = new Rect(10, 20, 30, 40), HitTestId = 75
        }, Matrix4x4.Identity);
        capture.AddSourceVisual(new SourceVisual
        {
            Opacity = 0, ClipBounds = new Rect(0, 0, 5, 5), CacheAsLayer = true
        }, Matrix4x4.Identity);
        capture.AddCommand(new RenderCommand { Type = RenderCommandType.PopClip }, Matrix4x4.Identity);
        Assert.Equal(75, Assert.Single(capture.BuildIndex().Primitives).Id);
    }

    [Fact]
    public void RecursiveEmbeddedSourceFailsAtBoundedDepth()
    {
        var visual = new SourceVisual();
        visual.SourceHitTestCommands.Commands.Add(new RenderCommand { Type = RenderCommandType.DrawVisual, Visual = visual });
        using var capture = new GpuRenderCommandHitTestCacheBuilder();
        Assert.Throws<InvalidOperationException>(() => capture.AddSourceVisual(visual, Matrix4x4.Identity));
        Assert.Throws<InvalidOperationException>(() => capture.BuildIndex());
    }

    private sealed class RequiredCacheSourceVisual : SourceVisual
    {
        internal override bool RequiresLayerCache => true;
    }

    internal class SourceVisual : ContainerVisual, ISourceGeometryHitTestCommands
    {
        public DrawingContext SourceHitTestCommands { get; } = new();
        public int RenderCalls { get; private set; }
        public override void OnRender(DrawingContext context)
        {
            RenderCalls++;
            context.Append(SourceHitTestCommands);
        }
    }
}
