using System;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Scene;
using ProGPU.Fonts.Inter;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class CachedPictureTests
{
    private static readonly Rect Bounds = new(10, 20, 20, 10);

    [Fact]
    public void FilledStrokeCoverageRecordsOnePathAndRetainsMaskAndSource()
    {
        using var input = CreatePicture(Vector4.One);
        var provider = new PictureSource(input);
        using var cache = new CachedPictureSourceCache<object>();
        using var source = cache.Acquire(new object(), provider, static value => value);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 1, dashCap: PenLineCap.Round, dashArray: [2, 2]);
        Assert.True(StrokeCoverageGeometry.TryPrepareLine(Vector2.Zero, new(4, 0), pen,
            out _, out _, out var bounds, out var outline));
        Assert.NotNull(outline);
        var recorder = new GpuPictureRecorder();
        var commands = recorder.BeginRecording(bounds);
        var parent = Matrix4x4.CreateScale(2, 3, 1);
        commands.DrawCachedPictureFillCoverage(source, outline, bounds, opacity: 0.5f,
            transform: parent, isEdgeAliased: true);
        using var picture = recorder.EndRecording();
        using var clone = picture.Clone();
        commands.Clear(); picture.Dispose(); source.Dispose();
        Assert.Equal(5, clone.CommandCount);
        var mask = clone.GetCommand(0);
        Assert.Equal(RenderCommandType.PushOpacityMask, mask.Type);
        Assert.Equal(parent, mask.Transform);
        Assert.NotNull(mask.Picture);
        Assert.Equal(1, mask.Picture.CommandCount);
        var fill = mask.Picture.GetCommand(0);
        Assert.Equal(RenderCommandType.DrawPath, fill.Type);
        Assert.Same(outline, fill.Path);
        Assert.Null(fill.Pen);
        Assert.True(fill.IsEdgeAliased);
        Assert.Equal(Vector4.One, Assert.IsType<SolidColorBrush>(fill.Brush).Color);
        Assert.Equal(0, provider.DisposeCount);
        clone.Dispose();
        Assert.Equal(1, provider.DisposeCount);
    }

    [Theory]
    [InlineData(PenStrokeTransformMode.Normal, 4f)]
    [InlineData(PenStrokeTransformMode.Fixed, 4f)]
    [InlineData(PenStrokeTransformMode.Normal, Pen.HairlineThickness)]
    public void CachedStrokeSnapshotsPenAndRetainsSource(PenStrokeTransformMode mode, float thickness)
    {
        using var input = CreatePicture(Vector4.One);
        var provider = new PictureSource(input);
        using var cache = new CachedPictureSourceCache<object>();
        using var source = cache.Acquire(new object(), provider, static value => value);
        var path = PrimitivePathGeometry.CreateRectangle(12, 22, 16, 6);
        var pen = new Pen(new SolidColorBrush(new Vector4(0, 0, 0.2f, 0.2f)), thickness,
            PenLineJoin.Bevel, 7, PenLineCap.Square, PenLineCap.Triangle, PenLineCap.Round,
            [2, 1, 3], 0.25, mode);
        var recorder = new GpuPictureRecorder();
        var commands = recorder.BeginRecording(Bounds);
        var mapping = Matrix4x4.CreateTranslation(3, 4, 0);
        var parent = Matrix4x4.CreateScale(2, 3, 1);
        commands.DrawCachedPictureStroke(source, path, pen, Bounds, mapping, 0.5f, parent, true);
        using var picture = recorder.EndRecording();
        using var clone = picture.Clone();
        pen.Thickness = 99;
        pen.DashArray = [9, 9];
        pen.DashOffset = 99;
        source.Dispose();
        picture.Dispose();
        Assert.Equal(5, clone.CommandCount);
        var mask = clone.GetCommand(0);
        Assert.Equal(RenderCommandType.PushOpacityMask, mask.Type);
        Assert.Null(mask.Picture);
        Assert.Same(path, mask.Path);
        Assert.Same(path, mask.GeometryCache!.StrokePath);
        Assert.True(mask.IsEdgeAliased);
        Assert.Equal(parent, mask.Transform);
        Assert.Equal(Bounds, mask.Rect);
        Assert.True(mask.IsPenThicknessLocal);
        var coveragePen = mask.Pen!;
        Assert.NotSame(pen, coveragePen);
        Assert.Equal(Vector4.One, Assert.IsType<SolidColorBrush>(coveragePen.Brush).Color);
        Assert.Equal(1f, coveragePen.Brush.Opacity);
        Assert.Equal(thickness, coveragePen.Thickness);
        Assert.Equal(mode, coveragePen.StrokeTransformMode);
        Assert.Equal(PenLineJoin.Bevel, coveragePen.LineJoin);
        Assert.Equal(7f, coveragePen.MiterLimit);
        Assert.Equal(PenLineCap.Square, coveragePen.StartLineCap);
        Assert.Equal(PenLineCap.Triangle, coveragePen.EndLineCap);
        Assert.Equal(PenLineCap.Round, coveragePen.DashCap);
        Assert.Equal(new double[] { 2, 1, 3 }, coveragePen.DashArray);
        Assert.Equal(0.25, coveragePen.DashOffset);
        Assert.Equal(RenderCommandType.PushOpacity, clone.GetCommand(1).Type);
        Assert.Equal(0.5f, clone.GetCommand(1).FontSize);
        Assert.Equal(RenderCommandType.DrawVisual, clone.GetCommand(2).Type);
        Assert.Equal(mapping * parent, clone.GetCommand(2).Transform);
        Assert.Equal(RenderCommandType.PopOpacity, clone.GetCommand(3).Type);
        Assert.Equal(RenderCommandType.PopOpacityMask, clone.GetCommand(4).Type);
        Assert.Equal(0, provider.DisposeCount);
        clone.Dispose();
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public void CachedStrokeRejectsInvalidStateAndSkipsEmptyPaintBeforeRecording()
    {
        using var input = CreatePicture(Vector4.One);
        using var cache = new CachedPictureSourceCache<object>();
        using var source = cache.Acquire(new object(), new PictureSource(input), static value => value);
        var path = PrimitivePathGeometry.CreateRectangle(12, 22, 16, 6);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4);
        var commands = new DrawingContext();
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.DrawCachedPictureStroke(source, path, pen, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.DrawCachedPictureStroke(source, path, pen, Bounds, opacity: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.DrawCachedPictureStroke(source, path, pen, Bounds,
            Matrix4x4.CreateScale(float.MaxValue), transform: Matrix4x4.CreateScale(2)));
        pen.Thickness = float.NaN;
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.DrawCachedPictureStroke(source, path, pen, Bounds));
        pen.Thickness = 0;
        commands.DrawCachedPictureStroke(source, path, pen, Bounds);
        pen.Thickness = 4;
        commands.DrawCachedPictureStroke(source, path, pen, Bounds, opacity: 0);
        Assert.Empty(commands.Commands);
        Assert.Equal(0, commands.RetainedResourceCount);
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => commands.DrawCachedPictureStroke(source, path, pen, Bounds));
        Assert.Empty(commands.Commands);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CachedStrokeMatchesOrdinaryCoverageAndReusesSource(bool rectangle, bool dashed)
    {
        using var window = new HeadlessWindow(64, 64);
        var recorder = new GpuPictureRecorder();
        var fullBounds = new Rect(0, 0, 64, 64);
        recorder.BeginRecording(fullBounds).DrawRectangle(new SolidColorBrush(new Vector4(1, 0, 0, 1)), null, fullBounds);
        using var input = recorder.EndRecording();
        var provider = new PictureSource(input) { CaptureBounds = fullBounds };
        using var cache = new CachedPictureSourceCache<object>();
        using var source = cache.Acquire(new object(), provider, static value => value);
        var reference = new CachedStrokeHost(null, rectangle, dashed);
        var cached = new CachedStrokeHost(source, rectangle, dashed);
        try
        {
            window.Content = reference;
            window.Render();
            var expected = window.ReadPixels();
            window.Content = cached;
            window.Render();
            var actual = window.ReadPixels();
            Assert.Equal(expected.Length, actual.Length);
            Assert.True(window.Compositor.Metrics.MaskRenderDrawCallCount > 0,
                $"Stroke mask has no draw calls; passes={window.Compositor.Metrics.MaskRenderPassCount}, source texture={source.Picture.GetVisual().LayerTexture != null}");
            int painted = 0;
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.True(Math.Abs(expected[index] - actual[index]) <= 2,
                    $"Pixel ({index / 4 % 64}, {index / 4 / 64}) channel {index % 4}: expected {expected[index]}, actual {actual[index]}");
                if (index % 4 != 3 && actual[index] > 32) painted++;
            }
            Assert.True(painted > 16);
            var texture = source.Picture.GetVisual().LayerTexture;
            window.Render();
            Assert.Same(texture, source.Picture.GetVisual().LayerTexture);
            Assert.Equal(1, provider.CaptureCount);
        }
        finally
        {
            window.Content = null;
            reference.Commands.Clear();
            cached.Commands.Clear();
        }
    }

    [Fact]
    public void CachedStrokeMaskPreservesEarlierRoundJoinEdgeCoverage()
    {
        using var window = new HeadlessWindow(64, 64);
        var recorder = new GpuPictureRecorder();
        var bounds = new Rect(0, 0, 64, 64);
        recorder.BeginRecording(bounds).DrawRectangle(new SolidColorBrush(new Vector4(1, 0, 0, 1)), null, bounds);
        using var input = recorder.EndRecording();
        using var cache = new CachedPictureSourceCache<object>();
        using var source = cache.Acquire(new object(), new PictureSource(input) { CaptureBounds = bounds }, static value => value);
        var reference = new CachedStrokeHost(null, true, false);
        var cached = new CachedStrokeHost(source, true, false);
        try
        {
            window.Content = reference;
            window.Render();
            var expected = window.ReadPixels();
            window.Content = cached;
            window.Render();
            var actual = window.ReadPixels();
            // A later join's zero-coverage padding used to overwrite this edge
            // with alpha=1, reducing the red channel from 137 to 20.
            const int edge = (6 * 64 + 8) * 4;
            Assert.True(expected[edge] > 100);
            Assert.InRange(Math.Abs(expected[edge] - actual[edge]), 0, 2);
            var texture = source.Picture.GetVisual().LayerTexture;
            window.Render();
            Assert.Same(texture, source.Picture.GetVisual().LayerTexture);
            Assert.Equal(actual, window.ReadPixels());
        }
        finally
        {
            window.Content = null;
            reference.Commands.Clear();
            cached.Commands.Clear();
        }
    }

    private sealed class CachedStrokeHost : FrameworkElement, IOwnedRenderCommandCache
    {
        internal readonly DrawingContext Commands = new();
        internal CachedStrokeHost(CachedPictureLease? source, bool rectangle, bool dashed)
        {
            Width = Height = 64;
            var path = rectangle ? PrimitivePathGeometry.CreateRectangle(8, 8, 48, 48) : new PathGeometry();
            if (!rectangle)
            {
                var figure = new PathFigure { StartPoint = new Vector2(8, 16), IsFilled = false };
                figure.Segments.Add(new LineSegment(new Vector2(56, 48)));
                path.Figures.Add(figure);
            }
            var pen = new Pen(new SolidColorBrush(new Vector4(1, 0, 0, 1)), 4,
                PenLineJoin.Round, 10, PenLineCap.Round, PenLineCap.Triangle, PenLineCap.Square,
                dashed ? [2, 1] : null, 0.25);
            if (source == null)
            {
                // Cached-source opacity applies once to completed coverage.
                // Render the ordinary stroke opaque in an independent layer,
                // then composite it once; PushOpacity multiplies every piece.
                CacheAsLayer = true;
                Opacity = 0.5f;
                Commands.DrawPath(null, pen, path);
            }
            else Commands.DrawCachedPictureStroke(source, path, pen, new Rect(0, 0, 64, 64), opacity: 0.5f);
        }
        DrawingContext IOwnedRenderCommandCache.GetOrUpdateRenderCommandCache() => Commands;
    }

    [Fact]
    public void CachedCoverageKeepsOneGlyphCommandAndIndependentOwners()
    {
        using var input = CreatePicture(Vector4.One);
        var provider = new PictureSource(input);
        using var cache = new CachedPictureSourceCache<object>();
        using var source = cache.Acquire(new object(), provider, static value => value);
        var font = InterFontFamily.Regular;
        ushort[] indices = [font.GetGlyphIndex('A'), font.GetGlyphIndex('g')];
        Vector2[] positions = [Vector2.Zero, new Vector2(12, 0)];
        var maskRecorder = new GpuPictureRecorder();
        maskRecorder.BeginRecording(Bounds).DrawGlyphRun(indices, positions, font, 16,
            new SolidColorBrush(Vector4.One), new Vector2(10, 25));
        using var coverage = maskRecorder.EndRecording();
        var recorder = new GpuPictureRecorder();
        var commands = recorder.BeginRecording(Bounds);
        var mapping = Matrix4x4.CreateTranslation(2, 3, 0);
        var parent = Matrix4x4.CreateScale(2, 3, 1);
        commands.DrawCachedPictureWithCoverage(source, coverage, Bounds, mapping, 0.5f, parent);
        using var picture = recorder.EndRecording();
        using var clone = picture.Clone();
        coverage.Dispose();
        source.Dispose();
        picture.Dispose();
        var mask = clone.GetCommand(0);
        Assert.Equal(RenderCommandType.PushOpacityMask, mask.Type);
        Assert.Equal(parent, mask.Transform);
        Assert.False(mask.Picture!.IsDisposed);
        Assert.Equal(1, mask.Picture.CommandCount);
        Assert.Same(indices, mask.Picture.GetCommand(0).GlyphIndices);
        Assert.Same(positions, mask.Picture.GetCommand(0).GlyphPositions);
        Assert.Equal(RenderCommandType.DrawGlyphRun, mask.Picture.GetCommand(0).Type);
        Assert.Equal(mapping * parent, clone.GetCommand(2).Transform);
        Assert.Equal(RenderCommandType.PopOpacityMask, clone.GetCommand(4).Type);
        Assert.Equal(0, provider.DisposeCount);
        clone.Dispose();
        Assert.True(mask.Picture.IsDisposed);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public void CachedMaskOwnsSourceThroughRecordingClonesAndPreservesMapping()
    {
        using var input = CreatePicture(Vector4.One);
        var provider = new PictureSource(input);
        using var cache = new CachedPictureSourceCache<object>();
        using var source = cache.Acquire(new object(), provider, static value => value);
        var recorder = new GpuPictureRecorder();
        var commands = recorder.BeginRecording(Bounds);
        var mapping = Matrix4x4.CreateTranslation(3, 4, 0);
        var parent = Matrix4x4.CreateScale(2, 3, 1);
        commands.PushCachedPictureOpacityMask(source, Bounds, mapping, 0.5f, parent);
        commands.PopOpacityMask();
        using var picture = recorder.EndRecording();
        using var clone = picture.Clone();
        var mask = picture.GetCommand(0);
        Assert.Equal(RenderCommandType.PushOpacityMask, mask.Type);
        Assert.Equal(Bounds, mask.Rect);
        Assert.Equal(parent, mask.Transform);
        Assert.NotNull(mask.Picture);
        Assert.Equal(3, mask.Picture!.CommandCount);
        Assert.Equal(0.5f, mask.Picture.GetCommand(0).FontSize);
        var draw = mask.Picture.GetCommand(1);
        Assert.Equal(RenderCommandType.DrawVisual, draw.Type);
        Assert.Same(source.Picture.GetVisual(), draw.Visual);
        Assert.Equal(mapping, draw.Transform);
        source.Dispose();
        picture.Dispose();
        Assert.False(mask.Picture.IsDisposed);
        Assert.Equal(0, provider.DisposeCount);
        clone.Dispose();
        Assert.True(mask.Picture.IsDisposed);
        Assert.Equal(0, cache.Count);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public void CachedMaskRejectsInvalidStateBeforeRetainingSource()
    {
        using var input = CreatePicture(Vector4.One);
        using var cache = new CachedPictureSourceCache<object>();
        using var source = cache.Acquire(new object(), new PictureSource(input), static value => value);
        var commands = new DrawingContext();
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.PushCachedPictureOpacityMask(source, Bounds, opacity: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.PushCachedPictureOpacityMask(source, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.PushCachedPictureOpacityMask(source, Bounds,
            Matrix4x4.CreateTranslation(float.PositiveInfinity, 0, 0)));
        Assert.Empty(commands.Commands);
        Assert.Equal(0, commands.RetainedResourceCount);
    }

    [Fact]
    public void CachedMaskMatchesAlphaOracleAndRefreshesWithoutReRecording()
    {
        using var window = new HeadlessWindow(64, 64);
        window.Compositor.ClearColor = Vector4.Zero;
        using var input = CreatePicture(new Vector4(0, 0, 0.5f, 0.5f));
        var provider = new PictureSource(input);
        using var cache = new CachedPictureSourceCache<object>();
        using var source = cache.Acquire(new object(), provider, static value => value);
        var reference = new CachedMaskHost(null);
        var cached = new CachedMaskHost(source);
        try
        {
            window.Content = reference;
            window.Render();
            var expected = window.ReadPixels();
            window.Content = cached;
            window.Render();
            var actual = window.ReadPixels();
            Assert.Equal(expected.AsSpan((25 * 64 + 15) * 4, 4).ToArray(), actual.AsSpan((25 * 64 + 15) * 4, 4).ToArray());
            Assert.Equal(expected.AsSpan((5 * 64 + 5) * 4, 4).ToArray(), actual.AsSpan((5 * 64 + 5) * 4, 4).ToArray());
            var texture = source.Picture.GetVisual().LayerTexture;
            window.Render();
            Assert.Same(texture, source.Picture.GetVisual().LayerTexture);
            Assert.Equal(1, provider.CaptureCount);
            provider.Scale = 0;
            provider.Change();
            window.Render();
            Assert.Equal(2, provider.CaptureCount);
            Assert.Null(source.Picture.GetVisual().LayerTexture);
            Assert.Equal(0, window.ReadPixels()[(25 * 64 + 15) * 4]);
        }
        finally
        {
            window.Content = null;
            reference.Commands.Clear();
            cached.Commands.Clear();
        }
    }

    private sealed class CachedMaskHost : FrameworkElement, IOwnedRenderCommandCache
    {
        internal readonly DrawingContext Commands = new();
        internal CachedMaskHost(CachedPictureLease? source)
        {
            Width = Height = 64;
            if (source == null) Commands.PushOpacityMask(new SolidColorBrush(new Vector4(0, 0, 0.25f, 0.25f)), Bounds);
            else Commands.PushCachedPictureOpacityMask(source, Bounds, opacity: 0.5f);
            Commands.DrawRectangle(new SolidColorBrush(new Vector4(1, 0, 0, 1)), null, new Rect(0, 0, 64, 64));
            Commands.PopOpacityMask();
        }
        DrawingContext IOwnedRenderCommandCache.GetOrUpdateRenderCommandCache() => Commands;
    }

    [Fact]
    public void EllipseClipRecordsAnalyticArcsAndPreservesTransform()
    {
        var commands = new DrawingContext();
        var transform = Matrix4x4.CreateScale(2, 3, 1);
        commands.PushEllipseClip(new Vector2(10, 12), 4, 6, transform);
        var clip = Assert.Single(commands.Commands);
        Assert.Equal(RenderCommandType.PushGeometryClip, clip.Type);
        Assert.Equal(transform, clip.Transform);
        var figure = Assert.Single(clip.Path!.Figures);
        Assert.Equal(new Vector2(14, 12), figure.StartPoint);
        Assert.Equal(4, figure.Segments.Count);
        Assert.All(figure.Segments, segment => Assert.IsType<ArcSegment>(segment));
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.PushEllipseClip(Vector2.Zero, float.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.PushEllipseClip(Vector2.Zero, 1, 0));
        Assert.Single(commands.Commands);
        commands.PopGeometryClip();
        Assert.Equal(RenderCommandType.PopGeometryClip, commands.Commands[1].Type);
    }

    [Theory]
    [InlineData(2f, 3f, 2f, 3f)]
    [InlineData(float.MaxValue, float.MaxValue, 10f, 5f)]
    [InlineData(0.00001f, 0.00002f, 0.00001f, 0.00002f)]
    public void RoundedClipRetainsClampedAnalyticCorners(float radiusX, float radiusY, float expectedX, float expectedY)
    {
        var commands = new DrawingContext();
        var transform = Matrix4x4.CreateScale(2, 3, 1);
        commands.PushRoundedRectangleClip(Bounds, radiusX, radiusY, transform);
        var clip = Assert.Single(commands.Commands);
        Assert.Equal(RenderCommandType.PushGeometryClip, clip.Type);
        Assert.Equal(transform, clip.Transform);
        var figure = Assert.Single(clip.Path!.Figures);
        Assert.True(figure.IsClosed);
        Assert.Equal(8, figure.Segments.Count);
        for (int index = 0; index < 8; index++)
        {
            if (index % 2 == 0) Assert.IsType<LineSegment>(figure.Segments[index]);
            else Assert.Equal(new Vector2(expectedX, expectedY), Assert.IsType<ArcSegment>(figure.Segments[index]).Size);
        }
        Assert.False(PrimitivePathGeometry.TryGetAxisAlignedRectangleBounds(clip.Path, out _, out _));
        commands.PopGeometryClip();
        Assert.Equal(RenderCommandType.PopGeometryClip, commands.Commands[1].Type);
    }

    [Theory]
    [InlineData(0f, 3f)]
    [InlineData(2f, 0f)]
    public void RoundedClipZeroAxisIsSquare(float radiusX, float radiusY)
    {
        var commands = new DrawingContext();
        commands.PushRoundedRectangleClip(Bounds, radiusX, radiusY);
        Assert.True(PrimitivePathGeometry.TryGetAxisAlignedRectangleBounds(
            Assert.Single(commands.Commands).Path!, out var min, out var max));
        Assert.Equal(new Vector2(10, 20), min);
        Assert.Equal(new Vector2(30, 30), max);
    }

    [Fact]
    public void InvalidRoundedClipDoesNotRecordPartialCommands()
    {
        var commands = new DrawingContext();
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.PushRoundedRectangleClip(Bounds, -1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.PushRoundedRectangleClip(Bounds, float.NaN, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.PushRoundedRectangleClip(Bounds, 1, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.PushRoundedRectangleClip(new Rect(0, 0, 0, 10), 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => commands.PushRoundedRectangleClip(new Rect(float.MaxValue, 0, float.MaxValue, 10), 1, 1));
        Assert.Empty(commands.Commands);
    }

    [Fact]
    public void SharedSourceLookupRetainsOneSourceThroughIndependentRecordings()
    {
        using var picture = CreatePicture(Vector4.One);
        var source = new PictureSource(picture);
        using var cache = new CachedPictureSourceCache<object>(ReferenceEqualityComparer.Instance);
        object key = new();
        using var first = cache.Acquire(key, source, static value => value);
        using var second = cache.Acquire(key, source, static value => value);
        Assert.Same(first.Picture, second.Picture);
        Assert.Equal(1, source.CaptureCount);
        var recorder = new GpuPictureRecorder();
        var commands = recorder.BeginRecording(Bounds);
        commands.DrawCachedPicture(first);
        commands.DrawCachedPicture(second);
        Assert.Equal(1, commands.RetainedResourceCount);
        using var recorded = recorder.EndRecording();
        using var clone = recorded.Clone();
        commands.Clear();
        first.Dispose();
        second.Dispose();
        recorded.Dispose();
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, source.DisposeCount);
        clone.Dispose();
        Assert.Equal(0, cache.Count);
        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(0, source.SubscriptionCount);
    }

    [Fact]
    public void ClosingLookupPreservesExistingLeasesButRejectsNewAcquisitions()
    {
        using var picture = CreatePicture(Vector4.One);
        var source = new PictureSource(picture);
        using var cache = new CachedPictureSourceCache<object>();
        using var lease = cache.Acquire(new object(), source, static value => value);
        cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.Acquire(new object(), source, static value => value));
        source.Change();
        lease.Picture.Refresh();
        Assert.Equal(2, source.CaptureCount);
        lease.Dispose();
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public void LiveSourceCoalescesChangesAndReleasesOwnedSubscriptions()
    {
        using var picture = CreatePicture(Vector4.One);
        var source = new PictureSource(picture);
        using var cached = new CachedPicture(source, ownsSource: true);
        Assert.Equal(1, source.CaptureCount);
        Assert.Equal(1, source.SubscriptionCount);
        Assert.True(source.LastCapture!.IsDisposed);
        var owner = cached.GetVisual();
        long version = owner.ChangeVersion;
        source.Change();
        source.Change();
        Assert.True(cached.IsSourceDirty);
        Assert.NotEqual(version, owner.ChangeVersion);
        Assert.Equal(1, source.CaptureCount);
        cached.Refresh();
        cached.Refresh();
        Assert.Equal(2, source.CaptureCount);
        Assert.False(cached.IsSourceDirty);
        Assert.Throws<InvalidOperationException>(() => cached.Update(picture, Bounds));
        cached.Dispose();
        Assert.Equal(0, source.SubscriptionCount);
        Assert.Equal(1, source.DisposeCount);
        owner.PrepareLayerCache(); // Recorded disposed references remain empty.
    }

    [Fact]
    public void FailedLiveCapturePreservesOwnershipAndRetriesWithoutAnotherEvent()
    {
        using var picture = CreatePicture(Vector4.One);
        using var source = new PictureSource(picture);
        using var cached = new CachedPicture(source);
        source.CaptureBounds = new Rect(0, 0, -1, 2);
        source.Change();
        Assert.Throws<ArgumentOutOfRangeException>(() => cached.Refresh());
        Assert.Equal(Bounds, cached.Bounds);
        Assert.True(cached.IsSourceDirty);
        Assert.True(source.LastCapture!.IsDisposed);
        source.CaptureBounds = new Rect(0, 0, 5, 6);
        cached.Refresh();
        Assert.Equal(source.CaptureBounds, cached.Bounds);
        Assert.False(cached.IsSourceDirty);
        source.DuringCapture = source.Change;
        source.Change();
        Assert.Throws<InvalidOperationException>(() => cached.Refresh());
        Assert.True(cached.IsSourceDirty);
        Assert.True(source.LastCapture!.IsDisposed);
        source.DuringCapture = () => cached.Refresh();
        Assert.Throws<InvalidOperationException>(() => cached.Refresh());
        source.DuringCapture = null;
        cached.Refresh();
        Assert.False(cached.IsSourceDirty);
    }

    [Fact]
    public void FailedLiveConstructionUnsubscribesAndHonorsSourceOwnership()
    {
        using var picture = CreatePicture(Vector4.One);
        var source = new PictureSource(picture) { CaptureBounds = new Rect(0, 0, -1, 1) };
        Assert.Throws<ArgumentOutOfRangeException>(() => new CachedPicture(source, ownsSource: true));
        Assert.Equal(0, source.SubscriptionCount);
        Assert.Equal(1, source.DisposeCount);
        Assert.True(source.LastCapture!.IsDisposed);
    }

    [Fact]
    public void RenderingRefreshesLiveSourcesBeforeSizingIncludingZeroScaleRecovery()
    {
        using var window = new HeadlessWindow(64, 64);
        using var picture = CreatePicture(new Vector4(1, 0, 0, 1));
        using var source = new PictureSource(picture) { Scale = 0 };
        using var cached = new CachedPicture(source);
        window.Content = new CachedPictureHost(cached);
        try
        {
            window.Render();
            Assert.Null(cached.GetVisual().LayerTexture);
            source.Scale = 2;
            source.Change();
            window.Render();
            Assert.Equal(2, source.CaptureCount);
            Assert.Equal(40u, cached.GetVisual().LayerTexture!.Width);
            AssertChannel(window.ReadPixels(), 15, 25, 0);
            window.Render();
            Assert.Equal(2, source.CaptureCount);
        }
        finally { window.Content = null; }
    }

    private sealed class PictureSource(GpuPicture picture) : ICachedPictureSource
    {
        private EventHandler? _invalidated;
        public int SubscriptionCount { get; private set; }
        public int CaptureCount { get; private set; }
        public int DisposeCount { get; private set; }
        public GpuPicture? LastCapture { get; private set; }
        public Rect CaptureBounds { get; set; } = Bounds;
        public float Scale { get; set; } = 1;
        public Action? DuringCapture { get; set; }
        public event EventHandler? Invalidated
        {
            add { _invalidated += value; SubscriptionCount++; }
            remove { _invalidated -= value; SubscriptionCount--; }
        }
        public void Change() => _invalidated?.Invoke(this, EventArgs.Empty);
        public CachedPictureSnapshot Capture()
        {
            CaptureCount++;
            DuringCapture?.Invoke();
            LastCapture = picture.Clone();
            return new(LastCapture, CaptureBounds, Scale);
        }
        public void Dispose() => DisposeCount++;
    }

    [Theory]
    [InlineData(TextRenderingMode.ClearType, true, TextRenderingMode.Grayscale)]
    [InlineData(TextRenderingMode.ClearType, false, TextRenderingMode.ClearType)]
    [InlineData(TextRenderingMode.Aliased, true, TextRenderingMode.Aliased)]
    [InlineData(TextRenderingMode.Grayscale, true, TextRenderingMode.Grayscale)]
    public void CachedTextPolicyOnlySuppressesSubpixelRendering(TextRenderingMode source, bool suppress, TextRenderingMode expected)
    {
        Assert.Equal(expected, Compositor.ResolveCachedTextRenderingMode(source, suppress));
    }

    [Fact]
    public void ClearTypePolicyChangeInvalidatesSourceAndIsPreservedByOrdinaryUpdates()
    {
        using var picture = CreatePicture(Vector4.One);
        using var cached = new CachedPicture(picture, Bounds, 1, enableClearType: false);
        Assert.False(cached.EnableClearType);
        long version = cached.GetVisual().ChangeVersion;
        cached.Update(picture, Bounds);
        Assert.Equal(version, cached.GetVisual().ChangeVersion);
        cached.Update(picture, Bounds, 1, enableClearType: true);
        Assert.True(cached.EnableClearType);
        Assert.NotEqual(version, cached.GetVisual().ChangeVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SuppressedClearTypeCaptureMatchesExplicitGrayscaleAndSurvivesPolicySwitches(int nestedKind)
    {
        using var window = new HeadlessWindow(64, 64);
        var bounds = new Rect(0, 0, 64, 32);
        using var clearType = CreateTextPicture(bounds, TextRenderingMode.ClearType, nestedKind);
        using var grayscale = CreateTextPicture(bounds, TextRenderingMode.Grayscale, nestedKind);
        using var cached = new CachedPicture(clearType, bounds, 1, enableClearType: false);
        window.Content = new CachedPictureHost(cached);
        try
        {
            window.Render();
            byte[] expected = window.ReadPixels();
            // Scalar image oracle: equality of two blank captures is not proof
            // that the glyph path rendered.
            bool hasWhiteInk = false;
            for (int index = 0; index < expected.Length; index += 4)
                hasWhiteInk |= expected[index] > 200 && expected[index + 1] > 200 && expected[index + 2] > 200;
            Assert.True(hasWhiteInk);
            cached.Update(grayscale, bounds, 1, enableClearType: true);
            window.Render();
            Assert.Equal(expected, window.ReadPixels());
            cached.Update(clearType, bounds, 1, enableClearType: true);
            window.Render();
            cached.Update(clearType, bounds, 1, enableClearType: false);
            window.Render();
            Assert.Equal(expected, window.ReadPixels());
        }
        finally
        {
            window.Content = null;
        }
    }

    private static GpuPicture CreateTextPicture(Rect bounds, TextRenderingMode mode, int nestedKind)
    {
        var recorder = new GpuPictureRecorder();
        var commands = recorder.BeginRecording(bounds);
        if (nestedKind != 0)
            commands.DrawVisual(new CachedTextVisual(bounds, mode, nestedKind));
        else
            commands.DrawText("Cache", InterFontFamily.Regular, 18,
                new SolidColorBrush(Vector4.One), new Vector2(2, 3), textRenderingMode: mode);
        return recorder.EndRecording();
    }

    private sealed class CachedTextVisual : Visual, IOwnedRenderCommandCache
    {
        private readonly DrawingContext _commands = new();
        internal CachedTextVisual(Rect bounds, TextRenderingMode mode, int nestedKind)
        {
            Size = new Vector2(bounds.Width, bounds.Height);
            CacheAsLayer = nestedKind == 1;
            if (nestedKind == 2) Effect = new BlurEffect { BlurRadius = 0 };
            _commands.DrawText("Cache", InterFontFamily.Regular, 18,
                new SolidColorBrush(Vector4.One), new Vector2(2, 3), textRenderingMode: mode);
        }
        public DrawingContext GetOrUpdateRenderCommandCache() => _commands;
    }

    [Fact]
    public void RecordingSharesOneOwnerAndNormalizesCaptureWithoutChangingPlacement()
    {
        using var picture = CreatePicture(new(1, 0, 0, 1));
        using var cached = new CachedPicture(picture, Bounds, 2);
        var context = new DrawingContext();
        context.DrawCachedPicture(cached);
        context.DrawCachedPicture(cached, Matrix4x4.CreateTranslation(24, 0, 0));
        Assert.Equal(2, context.Commands.Count);
        var owner = Assert.IsAssignableFrom<Visual>(context.Commands[0].Visual);
        Assert.Same(owner, context.Commands[1].Visual);
        Assert.Equal(new Vector2(10, 20), owner.Offset);
        Assert.Equal(new Vector2(20, 10), owner.Size);
        Assert.Equal(2, owner.LayerCacheRenderScale);
        Assert.True(owner.RequiresLayerCache);
        Assert.False(owner.LayerCacheSnapsToDevicePixels);
        Assert.Null(owner.LayerTexture);
        var capture = Assert.Single(((IOwnedRenderCommandCache)owner).GetOrUpdateRenderCommandCache().Commands);
        Assert.Equal(Matrix4x4.CreateTranslation(-10, -20, 0), capture.Transform);
        Assert.True(picture.SharesRetainedCommandStorageWith(capture.Picture));
        picture.Dispose();
        using var stillOwned = capture.Picture!.Clone();
    }

    [Fact]
    public void UpdatesAreTransactionalAndUnchangedStorageDoesNotInvalidate()
    {
        using var picture = CreatePicture(new(1, 0, 0, 1));
        using var cached = new CachedPicture(picture, Bounds);
        Visual owner = cached.GetVisual();
        long version = owner.ChangeVersion;
        using var clone = picture.Clone();
        cached.Update(clone, Bounds);
        Assert.Equal(version, owner.ChangeVersion);
        Assert.Throws<ArgumentOutOfRangeException>(() => cached.Update(picture, Bounds, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => cached.Update(picture, new(0, 0, -1, 10)));
        Assert.Equal(version, owner.ChangeVersion);
        clone.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cached.Update(clone, Bounds));
        Assert.Equal(version, owner.ChangeVersion);
        cached.Invalidate();
        Assert.NotEqual(version, owner.ChangeVersion);
        cached.Dispose();
        Assert.False(owner.IsVisible);
        Assert.Empty(((IOwnedRenderCommandCache)owner).GetOrUpdateRenderCommandCache().Commands);
        Assert.Throws<ObjectDisposedException>(() => new DrawingContext().DrawCachedPicture(cached));
    }

    [Fact]
    public void SharedSourceReusesTextureUpdatesBothConsumersAndHonorsRasterScale()
    {
        using var window = new HeadlessWindow(64, 64);
        using var red = CreatePicture(new(1, 0, 0, 1));
        using var green = CreatePicture(new(0, 1, 0, 1));
        using var cached = new CachedPicture(red, Bounds);
        Visual owner = cached.GetVisual();
        window.Content = new CachedPictureHost(cached);
        try
        {
            window.Render();
            var texture = owner.LayerTexture;
            Assert.NotNull(texture);
            Assert.Equal(20u, texture!.Width);
            Assert.Equal(10u, texture.Height);
            AssertChannel(window.ReadPixels(), 15, 25, 0);
            AssertChannel(window.ReadPixels(), 39, 25, 0);
            window.Render();
            Assert.Same(texture, owner.LayerTexture);
            Assert.True(window.Compositor.Metrics.SceneCacheHit);
            cached.Update(green, Bounds);
            window.Render();
            Assert.Same(texture, owner.LayerTexture);
            AssertChannel(window.ReadPixels(), 15, 25, 1);
            AssertChannel(window.ReadPixels(), 39, 25, 1);
            cached.Update(green, Bounds, 0);
            window.Render();
            Assert.Null(owner.LayerTexture);
            cached.Update(green, Bounds, 2);
            window.Render();
            Assert.Equal(40u, owner.LayerTexture!.Width);
            Assert.Equal(20u, owner.LayerTexture.Height);
            cached.Dispose();
            window.Render();
            Assert.Null(owner.LayerTexture);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void FractionalCaptureExtentDoesNotCompressSourceCoordinates()
    {
        using var window = new HeadlessWindow(64, 64);
        var bounds = new Rect(10.25f, 20.25f, 20.25f, 10.25f);
        var recorder = new GpuPictureRecorder();
        var context = recorder.BeginRecording(bounds);
        context.DrawRectangle(new SolidColorBrush(new Vector4(0, 1, 0, 1)), null, bounds);
        context.DrawRectangle(new SolidColorBrush(new Vector4(1, 0, 0, 1)), null,
            new Rect(29.25f, 22, 0.5f, 6));
        using var picture = recorder.EndRecording();
        using var cached = new CachedPicture(picture, bounds, 8);
        window.Content = new CachedPictureHost(cached);
        try
        {
            window.Render();
            Assert.Equal(162u, cached.GetVisual().LayerTexture!.Width);
            Assert.Equal(82u, cached.GetVisual().LayerTexture!.Height);
            AssertChannel(window.ReadPixels(), 29, 24, 0);
            AssertChannel(window.ReadPixels(), 27, 24, 1);
        }
        finally
        {
            window.Content = null;
        }
    }

    private static void AssertChannel(byte[] pixels, int x, int y, int channel)
    {
        int offset = (y * 64 + x) * 4;
        Assert.True(pixels[offset + channel] > 220);
        Assert.True(pixels[offset + (channel == 0 ? 1 : 0)] < 30);
    }

    private static GpuPicture CreatePicture(Vector4 color)
    {
        var recorder = new GpuPictureRecorder();
        recorder.BeginRecording(Bounds).DrawRectangle(new SolidColorBrush(color), null, Bounds);
        return recorder.EndRecording();
    }

    private sealed class CachedPictureHost : FrameworkElement, IOwnedRenderCommandCache
    {
        private readonly DrawingContext _commands = new();
        internal CachedPictureHost(CachedPicture picture)
        {
            Width = Height = 64;
            _commands.DrawCachedPicture(picture);
            _commands.DrawCachedPicture(picture, Matrix4x4.CreateTranslation(24, 0, 0));
        }
        public DrawingContext GetOrUpdateRenderCommandCache() => _commands;
    }
}
