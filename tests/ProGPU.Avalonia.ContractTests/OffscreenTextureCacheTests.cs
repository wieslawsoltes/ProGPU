using System;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;
using ProGpuRect = ProGPU.Scene.Rect;
using ProGpuVisual = ProGPU.Scene.Visual;

namespace Avalonia.ProGpu.ContractTests
{
    public sealed class OffscreenTextureCacheTests
    {
        [Fact]
        public void CompositionPictureCacheUsesStableIdAndRevision()
        {
            using var cache = new OffscreenTextureCache();
            var first = EmptyPicture();

            cache.StoreCompositionPicture(42, 1, first);

            Assert.True(cache.TryGetCompositionPicture(42, 1, out GpuPicture? cached));
            Assert.Same(first, cached);
            Assert.False(cache.TryGetCompositionPicture(42, 2, out _));
            Assert.Equal(1, cache.CompositionPictureCount);
            Assert.Equal(1, cache.CompositionPictureHits);
            Assert.Equal(1, cache.CompositionPictureMisses);
            Assert.Equal(1, cache.CompositionPictureCompilations);

            var second = EmptyPicture();
            cache.StoreCompositionPicture(42, 2, second);

            Assert.Throws<ObjectDisposedException>(() => first.Clone());
            Assert.True(cache.TryGetCompositionPicture(42, 2, out cached));
            Assert.Same(second, cached);
            Assert.Equal(1, cache.CompositionPictureCount);
            Assert.Equal(2, cache.CompositionPictureCompilations);
        }

        [Fact]
        public void CompositionPictureCacheIsBoundedAndDisposesEvictedPictures()
        {
            using var cache = new OffscreenTextureCache();
            var first = EmptyPicture();
            cache.StoreCompositionPicture(0, 1, first);

            for (var id = 1; id < 2048; id++)
                cache.StoreCompositionPicture(id, 1, EmptyPicture());

            var newest = EmptyPicture();
            cache.StoreCompositionPicture(2048, 1, newest);

            Assert.Throws<ObjectDisposedException>(() => first.Clone());
            Assert.False(cache.TryGetCompositionPicture(0, 1, out _));
            Assert.True(cache.TryGetCompositionPicture(2048, 1, out GpuPicture? cached));
            Assert.Same(newest, cached);
            Assert.Equal(1, cache.CompositionPictureCount);
        }

        [Fact]
        public void RecordedHostVisualReusesExactSupportedCommandSnapshot()
        {
            using var cache = new OffscreenTextureCache();
            var context = new DrawingContext();
            var brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f));
            context.DrawRectangle(brush, null, new ProGpuRect(1f, 2f, 30f, 40f));

            ProGpuVisual first = cache.GetOrUpdateRecordedVisual(
                context,
                new Vector2(100f, 80f));
            long firstVersion = first.ChangeVersion;
            ProGpuVisual reused = cache.GetOrUpdateRecordedVisual(
                context,
                new Vector2(100f, 80f));

            Assert.Same(first, reused);
            Assert.Equal(firstVersion, reused.ChangeVersion);

            context.Clear();
            context.DrawRectangle(brush, null, new ProGpuRect(2f, 2f, 30f, 40f));
            cache.GetOrUpdateRecordedVisual(context, new Vector2(100f, 80f));

            Assert.True(first.ChangeVersion > firstVersion);
        }

        [Fact]
        public void RecordedHostVisualInvalidatesUnsupportedCommandStreamsEveryFrame()
        {
            using var cache = new OffscreenTextureCache();
            var context = new DrawingContext();
            var pen = new Pen(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                1f);
            context.DrawLine(pen, Vector2.Zero, Vector2.One);

            ProGpuVisual visual = cache.GetOrUpdateRecordedVisual(
                context,
                new Vector2(100f, 80f));
            long firstVersion = visual.ChangeVersion;
            cache.GetOrUpdateRecordedVisual(context, new Vector2(100f, 80f));

            Assert.True(visual.ChangeVersion > firstVersion);
        }

        [Fact]
        public void DrawingStatePoolClearsAndReusesBoundedState()
        {
            using var cache = new OffscreenTextureCache();
            AvaloniaDrawingState first = cache.RentDrawingState();
            first.OpacityFrames.Push(0.5d);
            first.GeometryClipFrames.Push(true);

            cache.ReturnDrawingState(first);
            Assert.Equal(1, cache.DrawingStatePoolCount);

            AvaloniaDrawingState reused = cache.RentDrawingState();
            Assert.Same(first, reused);
            Assert.Empty(reused.OpacityFrames);
            Assert.Empty(reused.GeometryClipFrames);
            cache.ReturnDrawingState(reused);
        }

        [Fact]
        public void OversizedDrawingStateIsNotRetained()
        {
            using var cache = new OffscreenTextureCache();
            AvaloniaDrawingState state = cache.RentDrawingState();
            state.OpacityFrames.EnsureCapacity(65);

            cache.ReturnDrawingState(state);

            Assert.Equal(0, cache.DrawingStatePoolCount);
        }

        [Fact]
        public void Gpu_Only_Offscreen_Target_Does_Not_Allocate_Readback_Storage()
        {
            using var owner = new DrawingContextImpl(
                new DrawingContextImpl.CreateInfo
                {
                    Dpi = new Avalonia.Vector(96, 96)
                });
            WgpuContext context = Assert.IsType<WgpuContext>(
                WgpuContext.Current);
            using var cache = new OffscreenTextureCache();

            lock (context.RenderLock)
            {
                GpuTexture texture =
                    DrawingContextImpl.GetOffscreenTexture(
                        cache,
                        context,
                        320,
                        200,
                        TextureFormat.Bgra8Unorm);

                Assert.NotNull(texture);
                Assert.False(cache.HasCachedReadbackBuffer);

                _ = DrawingContextImpl.GetOffscreenReadbackBuffer(
                    cache,
                    context);

                Assert.True(cache.HasCachedReadbackBuffer);
            }
        }

        [Fact]
        public void Offscreen_Resize_Drops_The_Old_Texture_And_Readback_Capacity()
        {
            using var owner = new DrawingContextImpl(
                new DrawingContextImpl.CreateInfo
                {
                    Dpi = new Avalonia.Vector(96, 96)
                });
            WgpuContext context = Assert.IsType<WgpuContext>(
                WgpuContext.Current);
            using var cache = new OffscreenTextureCache();

            GpuTexture first = DrawingContextImpl.GetOffscreenTexture(
                cache,
                context,
                640,
                360,
                TextureFormat.Bgra8Unorm);
            _ = DrawingContextImpl.GetOffscreenReadbackBuffer(
                cache,
                context);

            GpuTexture resized = DrawingContextImpl.GetOffscreenTexture(
                cache,
                context,
                320,
                180,
                TextureFormat.Bgra8Unorm);

            Assert.NotSame(first, resized);
            Assert.True(first.IsDisposed);
            Assert.Equal(320u, resized.Width);
            Assert.Equal(180u, resized.Height);
            Assert.False(cache.HasCachedReadbackBuffer);
        }

        private static GpuPicture EmptyPicture() =>
            new(
                Array.Empty<RenderCommand>(),
                Array.Empty<System.Numerics.Vector2>(),
                Array.Empty<double>(),
                Array.Empty<Line3D>(),
                Array.Empty<float>());
    }
}
