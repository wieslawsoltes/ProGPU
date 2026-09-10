using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests.Headless;

public sealed class GpuCameraCoverageTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void LateCameraCoverageMatchesOrdinaryTransform(bool glyph, int cameraKind)
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(new Rect(0, -10, 16, 16));
        var white = new SolidColorBrush(Vector4.One);
        if (glyph)
        {
            var font = InterFontFamily.Regular;
            drawing.DrawGlyphRunRange([font.GetGlyphIndex('G')], [Vector2.Zero], 0, 1,
                font, 8, white, Vector2.Zero, useVectorGlyphRendering: true);
        }
        else
        {
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(0, 0), true);
            figure.Segments.Add(new QuadraticBezierSegment(new Vector2(4, -12), new Vector2(8, 0)));
            path.Figures.Add(figure);
            drawing.DrawPath(white, null, path);
        }
        using GpuPicture picture = recorder.EndRecording();
        using var window = new HeadlessWindow(128, 128, new CompositorOptions { PathAtlasSize = 128 });
        Matrix4x4 camera = cameraKind switch
        {
            1 => Matrix4x4.CreateScale(8, 6, 1) * Matrix4x4.CreateTranslation(24.375f, 96.625f, 0),
            2 => Matrix4x4.CreateScale(8, 8, 1) * Matrix4x4.CreateRotationZ(0.2f) * Matrix4x4.CreateTranslation(24.25f, 96.5f, 0),
            _ => Matrix4x4.CreateScale(8, 8, 1) * Matrix4x4.CreateTranslation(24, 96, 0),
        };
        window.Content = new PictureVisual(picture, camera, false);
        window.Render();
        byte[] expected = window.ReadPixels();
        window.Content = new PictureVisual(picture, camera, true);
        window.Render();
        byte[] actual = window.ReadPixels();
        int maximumError = 0;
        int litPixels = 0;
        for (int i = 0; i < expected.Length; i += 4)
        {
            maximumError = Math.Max(maximumError, Math.Abs(expected[i] - actual[i]));
            if (expected[i] > 128) litPixels++;
        }
        Assert.True(litPixels > 100, "The reference contains no meaningful coverage.");
        Assert.True(maximumError <= 2, $"GPU camera coverage differs by {maximumError} channel levels.");

        ulong generation = window.Compositor.PathAtlas.Generation;
        int cachedCount = window.Compositor.PathAtlas.CachedPathCount;
        window.Render();
        Assert.Equal(actual, window.ReadPixels());
        Assert.Equal(generation, window.Compositor.PathAtlas.Generation);
        Assert.Equal(cachedCount, window.Compositor.PathAtlas.CachedPathCount);

        // The native compiler already folds the affine camera before encoding
        // coverage-sensitive records. Both placement routes must stay identical.
        drawing = recorder.BeginRecording(new Rect(0, 0, 128, 128));
        drawing.DrawPictureTransformed(picture, camera);
        using GpuPicture ordinary = recorder.EndRecording();
        drawing = recorder.BeginRecording(new Rect(0, 0, 128, 128));
        drawing.DrawPicture(picture, camera);
        using GpuPicture late = recorder.EndRecording();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(ordinary, 50U, 1U,
            out NativeCompiledPicture? nativeOrdinary, out NativePictureCompileFailure ordinaryFailure), ordinaryFailure.ToString());
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(late, 50U, 1U,
            out NativeCompiledPicture? nativeLate, out NativePictureCompileFailure lateFailure), lateFailure.ToString());
        Assert.Equal(nativeOrdinary!.Stream.ToArray(), nativeLate!.Stream.ToArray());
    }

    [Fact]
    public void FractionalCameraChangesRetainVisibleCoverageWithSmallAtlas()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(new Rect(0, -8, 32, 16));
        var font = InterFontFamily.Regular;
        drawing.DrawGlyphRunRange(
            [font.GetGlyphIndex('C'), font.GetGlyphIndex('A'), font.GetGlyphIndex('D')],
            [Vector2.Zero, new Vector2(6, 0), new Vector2(12, 0)], 0, 3,
            font, 4, new SolidColorBrush(Vector4.One), Vector2.Zero, useVectorGlyphRendering: true);
        using GpuPicture picture = recorder.EndRecording();
        using var window = new HeadlessWindow(128, 128, new CompositorOptions { PathAtlasSize = 64 });
        for (int frame = 0; frame < 64; frame++)
        {
            float scale = 3 + frame / 32f;
            Matrix4x4 camera = Matrix4x4.CreateScale(scale, scale, 1) *
                Matrix4x4.CreateTranslation(20 + frame / 128f, 64 + frame / 256f, 0);
            window.Content = new PictureVisual(picture, camera, true);
            window.Render();
            byte[] pixels = window.ReadPixels();
            int visible = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i] > 128) visible++;
            Assert.True(visible > 50, $"Frame {frame} lost vector text coverage.");
        }
    }

    private sealed class PictureVisual(GpuPicture picture, Matrix4x4 camera, bool lateCamera) : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(new Vector4(0, 0, 0, 1)), null, new Rect(0, 0, 128, 128));
            if (lateCamera) context.DrawPicture(picture, camera);
            else context.DrawPictureTransformed(picture, camera);
        }
    }
}
