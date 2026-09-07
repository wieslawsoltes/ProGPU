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
