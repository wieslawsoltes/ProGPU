using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using GdiBitmap = System.Drawing.Bitmap;
using GdiGraphics = System.Drawing.Graphics;
using GdiInterpolationMode = System.Drawing.Drawing2D.InterpolationMode;
using GdiRectangle = System.Drawing.Rectangle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.Transpiler;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using SkiaSharp;
using WpfPixelFormats = System.Windows.Media.Imaging.PixelFormats;
using WpfWriteableBitmap = System.Windows.Media.Imaging.WriteableBitmap;
using Xunit;

namespace ProGPU.Tests;

public sealed class CompositorReviewRegressionTests
{
    private const string SolidShaderToySource = """
fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    return vec4<f32>(1.0, 0.0, 0.0, 1.0);
}
""";

    [Fact]
    public void CombinedPathAtlasBoundsUseGeometryCoordinatesBeforePadding()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);
        var combined = new PathGeometry
        {
            IsCombined = true,
            Op = 2,
            PathA = PrimitivePathGeometry.CreateRectangle(10f, 15f, 20f, 10f),
            PathB = PrimitivePathGeometry.CreateRectangle(40f, 35f, 20f, 5f)
        };

        var info = atlas.GetOrCreatePath(combined, scale: 2f);

        Assert.Equal(10f, info.UnscaledMinX, precision: 3);
        Assert.Equal(15f, info.UnscaledMinY, precision: 3);
        Assert.Equal(60f, info.UnscaledMaxX, precision: 3);
        Assert.Equal(40f, info.UnscaledMaxY, precision: 3);
        Assert.Equal(16f, info.MinX);
        Assert.Equal(26f, info.MinY);
        Assert.Equal(108u, info.Width);
        Assert.Equal(58u, info.Height);
    }

    [Theory]
    [InlineData(GdiInterpolationMode.NearestNeighbor, TextureSamplingMode.Nearest)]
    [InlineData(GdiInterpolationMode.Bicubic, TextureSamplingMode.Cubic)]
    [InlineData(GdiInterpolationMode.HighQualityBicubic, TextureSamplingMode.Cubic)]
    [InlineData(GdiInterpolationMode.Default, TextureSamplingMode.Linear)]
    [InlineData(GdiInterpolationMode.Low, TextureSamplingMode.Linear)]
    [InlineData(GdiInterpolationMode.High, TextureSamplingMode.Linear)]
    [InlineData(GdiInterpolationMode.Bilinear, TextureSamplingMode.Linear)]
    [InlineData(GdiInterpolationMode.HighQualityBilinear, TextureSamplingMode.Linear)]
    public void GdiDrawImageMapsInterpolationModeToTextureSampling(
        GdiInterpolationMode interpolationMode,
        TextureSamplingMode expectedSamplingMode)
    {
        var previous = WgpuContext.Current;
        var window = HeadlessWindow.Shared;

        try
        {
            WgpuContext.Current = window.Context;
            using var destination = new GdiBitmap(4, 4);
            using var source = new GdiBitmap(2, 2);
            using var graphics = GdiGraphics.FromImage(destination);

            graphics.InterpolationMode = interpolationMode;
            graphics.DrawImage(source, new GdiRectangle(0, 0, 4, 4));

            var command = Assert.Single(graphics.DrawingContext.Commands);
            Assert.Equal(RenderCommandType.DrawTexture, command.Type);
            Assert.Equal(expectedSamplingMode, command.TextureSamplingMode);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void ShimGpuProvidersPreferCurrentContextOverFirstActiveContext()
    {
        var previous = WgpuContext.Current;
        using var firstActiveContext = new WgpuContext();
        firstActiveContext.Initialize(null);
        using var currentContext = new WgpuContext();
        currentContext.Initialize(null);
        WpfWriteableBitmap? wpfBitmap = null;

        try
        {
            WgpuContext.Current = currentContext;

            using var gdiBitmap = new GdiBitmap(1, 1);
            wpfBitmap = new WpfWriteableBitmap(
                1,
                1,
                96d,
                96d,
                WpfPixelFormats.Pbgra32,
                palette: null);

            Assert.Same(currentContext, gdiBitmap.GpuTexture.Context);
            Assert.Same(currentContext, wpfBitmap.GpuTexture.Context);
            Assert.NotSame(firstActiveContext, gdiBitmap.GpuTexture.Context);
            Assert.NotSame(firstActiveContext, wpfBitmap.GpuTexture.Context);
        }
        finally
        {
            wpfBitmap?.GpuTexture.Dispose();
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void SkShaderSingleStopGradientsUseFiniteZeroOffset()
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0f, 0f),
            new SKPoint(10f, 0f),
            [new SKColor(255, 0, 0, 255)],
            colorPos: null,
            SKShaderTileMode.Clamp);

        var brush = Assert.IsType<LinearGradientBrush>(shader.ToBrush());
        var stop = Assert.Single(brush.Stops);

        Assert.Equal(0f, stop.Offset);
        Assert.True(float.IsFinite(stop.Offset));
    }

    [Fact]
    public void ShaderToyForLoopPreservesMultiDeclarationInitializers()
    {
        var wgsl = ShaderToyTranspiler.Translate(
            """
            void mainImage(out vec4 fragColor, in vec2 fragCoord)
            {
                int sum = 0;
                for (int i = 0, j = 1; i < 4; i++)
                {
                    sum += i + j;
                }
                fragColor = vec4(float(sum));
            }
            """);

        Assert.Contains("var i: i32 = 0;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("var j: i32 = 1;", wgsl, System.StringComparison.Ordinal);
        Assert.Contains("for (; (i < 4); i = i + 1) {", wgsl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void GdiBitmapFlushUsesOwningContextWhenAmbientContextChanges()
    {
        var previous = WgpuContext.Current;
        using var bitmapContext = new WgpuContext();
        bitmapContext.Initialize(null);
        using var ambientContext = new WgpuContext();
        ambientContext.Initialize(null);

        try
        {
            WgpuContext.Current = bitmapContext;
            using var bitmap = new GdiBitmap(4, 4);
            using (var graphics = GdiGraphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.Red);
            }

            WgpuContext.Current = ambientContext;
            bitmap.Flush();

            Assert.Same(bitmapContext, bitmap.GpuTexture.Context);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void GdiDrawImageRejectsCrossContextBitmapsBeforeRecording()
    {
        var previous = WgpuContext.Current;
        using var sourceContext = new WgpuContext();
        sourceContext.Initialize(null);
        using var targetContext = new WgpuContext();
        targetContext.Initialize(null);

        try
        {
            WgpuContext.Current = sourceContext;
            using var source = new GdiBitmap(1, 1);
            WgpuContext.Current = targetContext;
            using var target = new GdiBitmap(2, 2);
            using var graphics = GdiGraphics.FromImage(target);

            var exception = Assert.Throws<System.InvalidOperationException>(
                () => graphics.DrawImage(source, new GdiRectangle(0, 0, 1, 1)));
            Assert.Contains("different WebGPU context", exception.Message, System.StringComparison.Ordinal);
            Assert.Empty(graphics.DrawingContext.Commands);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void RenderOffscreenRestoresCompositorStateWhenCompilationFails()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            16,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Failing Offscreen Restore Target");
        var beforeWidth = GetCompositorField<uint>(window.Compositor, "_currentWidth");
        var beforeHeight = GetCompositorField<uint>(window.Compositor, "_currentHeight");
        var beforeDpiScale = window.Compositor.CurrentDpiScale;
        var beforeProjection = GetCompositorField<Matrix4x4>(window.Compositor, "_currentProjection");

        var exception = Assert.Throws<System.InvalidOperationException>(
            () => window.Compositor.RenderOffscreen(
                new ThrowingRenderVisual(),
                width: 16,
                height: 16,
                targetTexture: target,
                padding: 0f,
                dpiScale: 2f));

        Assert.Equal("Synthetic offscreen render failure.", exception.Message);
        Assert.Equal(beforeWidth, GetCompositorField<uint>(window.Compositor, "_currentWidth"));
        Assert.Equal(beforeHeight, GetCompositorField<uint>(window.Compositor, "_currentHeight"));
        Assert.Equal(beforeDpiScale, window.Compositor.CurrentDpiScale);
        Assert.Equal(beforeProjection, GetCompositorField<Matrix4x4>(window.Compositor, "_currentProjection"));
    }

    [Fact]
    public void DrawGlyphRunFlushesActualTextCountBeforeColorLayerPaths()
    {
        var font = new TtfFont(BuildColorLayerFont());
        var window = HeadlessWindow.Shared;
        window.Resize(96, 48);
        window.Content = new MixedColorGlyphRunVisual(font);

        try
        {
            window.Render();

            AssertMixedColorGlyphDrawCalls(window.Compositor);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DrawTextFlushesActualTextCountBeforeColorLayerPaths()
    {
        var font = new TtfFont(BuildColorLayerFont());
        var window = HeadlessWindow.Shared;
        window.Resize(96, 48);
        window.Content = new MixedColorTextVisual(font);

        try
        {
            window.Render();

            AssertMixedColorGlyphDrawCalls(window.Compositor);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void GlyphAtlasDoesNotTreatMissingGlyphIdAsWhitespace()
    {
        var font = new TtfFont(BuildMissingGlyphOutlineFont());
        GlyphAtlas atlas = HeadlessWindow.Shared.Compositor.Atlas;

        GlyphInfo tab = atlas.GetOrCreateGlyph(font, '\t', 24f);
        GlyphInfo missing = atlas.GetOrCreateGlyph(font, 0x2603u, 24f);

        Assert.Equal(0u, tab.Width);
        Assert.Equal(0u, tab.Height);
        Assert.True(missing.Width > 0, "Expected missing-glyph ID 0 to keep its outline width.");
        Assert.True(missing.Height > 0, "Expected missing-glyph ID 0 to keep its outline height.");
    }

    [Fact]
    public void GpuSeriesDrawCallColorsIncludeBrushAndActiveOpacity()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(64, 64);
        window.Content = new GpuSeriesOpacityVisual();

        try
        {
            window.Render();

            Compositor.CompositorDrawCall[] drawCalls = GetDrawCalls(window.Compositor);
            Compositor.CompositorDrawCall line = Assert.Single(drawCalls, drawCall => drawCall.Type == Compositor.DrawCallType.ChartLine);
            Compositor.CompositorDrawCall scatter = Assert.Single(drawCalls, drawCall => drawCall.Type == Compositor.DrawCallType.ChartScatter);

            Assert.Equal(0.15f, line.Color.W, precision: 4);
            Assert.Equal(0.1f, scatter.Color.W, precision: 4);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CachedLayerRefreshesWhenPhysicalTextureSizeChanges()
    {
        using var window = new HeadlessWindow(64, 64);
        using var target1x = new GpuTexture(
            window.Context,
            32,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Layer Cache 1x Target");
        using var target2x = new GpuTexture(
            window.Context,
            64,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Layer Cache 2x Target");
        var visual = new CachedLayerResizeVisual();

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 16,
            targetTexture: target1x,
            padding: 0f,
            dpiScale: 1f);

        Assert.NotNull(visual.LayerTexture);
        Assert.Equal(32u, visual.LayerTexture.Width);
        Assert.Equal(16u, visual.LayerTexture.Height);
        Assert.False(visual.IsDirty);

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 16,
            targetTexture: target2x,
            padding: 0f,
            dpiScale: 2f);

        Assert.NotNull(visual.LayerTexture);
        Assert.Equal(64u, visual.LayerTexture.Width);
        Assert.Equal(32u, visual.LayerTexture.Height);
        Assert.False(visual.IsDirty);
    }

    [Fact]
    public void CachedLayerUsesCeilingForFractionalPhysicalTextureSize()
    {
        using var window = new HeadlessWindow(128, 64);
        using var target = new GpuTexture(
            window.Context,
            126,
            26,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Fractional Layer Cache Target");
        var visual = new CachedLayerResizeVisual(new Vector2(100.5f, 20.25f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 101,
            height: 21,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1.25f);

        Assert.NotNull(visual.LayerTexture);
        Assert.Equal(126u, visual.LayerTexture.Width);
        Assert.Equal(26u, visual.LayerTexture.Height);
        Assert.False(visual.IsDirty);
    }

    [Fact]
    public unsafe void ExplicitPhysicalRenderTargetScalesLogicalSceneToFramebuffer()
    {
        using var window = new HeadlessWindow(20, 20);
        using var target = new GpuTexture(
            window.Context,
            20,
            20,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "HiDPI Explicit Render Target");
        var visual = new SolidLogicalSceneVisual();

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 10,
            logicalHeight: 10,
            renderTargetWidth: 20,
            renderTargetHeight: 20,
            dpiScale: 2f,
            target.ViewPtr);

        var pixels = target.ReadPixels();
        var lowerRight = ReadPixel(pixels, target.Width, x: 15, y: 15);

        Assert.True(lowerRight.R >= 220, $"Expected logical scene to fill the physical target width, found {lowerRight}.");
        Assert.True(lowerRight.G <= 35, $"Expected logical scene green channel to stay low, found {lowerRight}.");
        Assert.True(lowerRight.B <= 35, $"Expected logical scene blue channel to stay low, found {lowerRight}.");
        Assert.Equal(255, lowerRight.A);
    }

    [Fact]
    public unsafe void ExplicitPhysicalRenderTargetPinsViewportToPhysicalFramebuffer()
    {
        using var window = new HeadlessWindow(24, 24);
        using var target = new GpuTexture(
            window.Context,
            21,
            17,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            "HiDPI Explicit Viewport Target");
        var visual = new SolidLogicalSceneVisual(new Vector2(10f, 8f));

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 10,
            logicalHeight: 8,
            renderTargetWidth: 21,
            renderTargetHeight: 17,
            dpiScale: 2f,
            target.ViewPtr);

        var pixels = target.ReadPixels();
        var lowerRight = ReadPixel(pixels, target.Width, x: 19, y: 15);

        Assert.True(lowerRight.R >= 220, $"Expected explicit physical viewport to fill target width, found {lowerRight}.");
        Assert.True(lowerRight.G <= 35, $"Expected explicit physical viewport green channel to stay low, found {lowerRight}.");
        Assert.True(lowerRight.B <= 35, $"Expected explicit physical viewport blue channel to stay low, found {lowerRight}.");
        Assert.Equal(255, lowerRight.A);
    }

    [Fact]
    public unsafe void ExplicitPhysicalRenderTargetFeedsFramebufferSizeToCanvasPixelHelpers()
    {
        using var window = new HeadlessWindow(24, 24);
        using var target = new GpuTexture(
            window.Context,
            21,
            17,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "HiDPI Explicit Canvas Size Target");
        var extension = new CanvasSizeRecordingExtension();
        window.Compositor.RegisterExtension(9004, extension);

        var visual = new DrawingVisual
        {
            Size = new Vector2(10f, 8f)
        };
        visual.Context.DrawExtension(9004);

        window.Compositor.RenderScene(
            visual,
            logicalWidth: 10,
            logicalHeight: 8,
            renderTargetWidth: 21,
            renderTargetHeight: 17,
            dpiScale: 2f,
            target.ViewPtr);

        Assert.Equal(1, extension.RenderCount);
        Assert.Equal(21f, extension.CanvasPixelWidth);
        Assert.Equal(17f, extension.CanvasPixelHeight);
    }

    [Fact]
    public void RenderOffscreenUsesOffscreenTargetSizeWhenExplicitOuterTargetIsActive()
    {
        using var window = new HeadlessWindow(24, 24);
        using var target = new GpuTexture(
            window.Context,
            19,
            13,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Offscreen Explicit Outer Target Regression");
        var extension = new CanvasSizeRecordingExtension();
        window.Compositor.RegisterExtension(9005, extension);

        SetCompositorField(window.Compositor, "_explicitRenderTargetWidth", (uint?)211u);
        SetCompositorField(window.Compositor, "_explicitRenderTargetHeight", (uint?)113u);
        SetCompositorField(window.Compositor, "_explicitDpiScale", (float?)3f);

        var visual = new DrawingVisual
        {
            Size = new Vector2(10f, 8f)
        };
        visual.Context.DrawExtension(9005);

        window.Compositor.RenderOffscreen(
            visual,
            width: 10,
            height: 8,
            targetTexture: target,
            padding: 0f,
            dpiScale: 2f);

        Assert.Equal(1, extension.RenderCount);
        Assert.Equal(19f, extension.CanvasPixelWidth);
        Assert.Equal(13f, extension.CanvasPixelHeight);
        Assert.Equal(211u, Assert.IsType<uint>(GetRawCompositorField(window.Compositor, "_explicitRenderTargetWidth")));
        Assert.Equal(113u, Assert.IsType<uint>(GetRawCompositorField(window.Compositor, "_explicitRenderTargetHeight")));
        Assert.Equal(3f, Assert.IsType<float>(GetRawCompositorField(window.Compositor, "_explicitDpiScale")));
    }

    [Fact]
    public void CachedLayerRecreatesTextureForCurrentWebGpuContext()
    {
        using var firstWindow = new HeadlessWindow(64, 64);
        using var secondWindow = new HeadlessWindow(64, 64);
        using var firstTarget = new GpuTexture(
            firstWindow.Context,
            32,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "First Context Layer Target");
        using var secondTarget = new GpuTexture(
            secondWindow.Context,
            32,
            16,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Second Context Layer Target");
        var visual = new CachedLayerResizeVisual();

        firstWindow.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 16,
            targetTexture: firstTarget,
            padding: 0f,
            dpiScale: 1f);

        GpuTexture? firstLayer = visual.LayerTexture;
        Assert.NotNull(firstLayer);
        Assert.Same(firstWindow.Context, firstLayer.Context);
        Assert.False(firstLayer.IsDisposed);

        secondWindow.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 16,
            targetTexture: secondTarget,
            padding: 0f,
            dpiScale: 1f);

        GpuTexture? secondLayer = visual.LayerTexture;
        Assert.NotNull(secondLayer);
        Assert.NotSame(firstLayer, secondLayer);
        Assert.Same(secondWindow.Context, secondLayer.Context);
        Assert.True(firstLayer.IsDisposed);
        Assert.False(secondLayer.IsDisposed);
    }

    [Fact]
    public void CachedLayerTextureIsReleasedWhenVisualLeavesActiveTree()
    {
        using var window = new HeadlessWindow(64, 64);
        var root = new StackPanel
        {
            Width = 64f,
            Height = 64f
        };
        var visual = new CachedLayerResizeVisual();
        root.AddChild(visual);
        window.Content = root;

        window.Render();

        GpuTexture? layer = visual.LayerTexture;
        Assert.NotNull(layer);
        Assert.False(layer.IsDisposed);

        root.RemoveChild(visual);
        window.Render();

        Assert.True(layer.IsDisposed);
        Assert.Null(visual.LayerTexture);
    }

    [Fact]
    public void CachedLayerTextureIsReleasedWhenCacheAsLayerIsDisabled()
    {
        using var window = new HeadlessWindow(64, 64);
        var root = new StackPanel
        {
            Width = 64f,
            Height = 64f
        };
        var visual = new CachedLayerResizeVisual();
        root.AddChild(visual);
        window.Content = root;

        window.Render();

        GpuTexture? layer = visual.LayerTexture;
        Assert.NotNull(layer);
        Assert.False(layer.IsDisposed);

        visual.CacheAsLayer = false;
        window.Render();

        Assert.True(layer.IsDisposed);
        Assert.Null(visual.LayerTexture);
    }

    [Fact]
    public void TransformedEllipticalRoundedRectanglePathFallbackAppliesTransformOnce()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(80, 40);
        window.Content = new TransformedEllipticalRoundedRectangleVisual();

        try
        {
            window.Render();

            byte[] pixels = window.ReadPixels();
            RgbaPixel expected = ReadPixel(pixels, window.Width, x: 32, y: 17);
            RgbaPixel doubleTransformed = ReadPixel(pixels, window.Width, x: 52, y: 22);

            Assert.True(expected.R >= 220, $"Expected once-transformed rounded rectangle center to be red, found {expected}.");
            Assert.True(expected.G <= 35, $"Expected once-transformed rounded rectangle center to keep green low, found {expected}.");
            Assert.True(expected.B <= 35, $"Expected once-transformed rounded rectangle center to keep blue low, found {expected}.");
            Assert.Equal(255, expected.A);

            Assert.True(
                doubleTransformed.R < 80 || doubleTransformed.A < 220,
                $"Expected double-transformed location to remain outside the rounded rectangle fill, found {doubleTransformed}.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RoundedRectangleWithExplicitZeroRadiusYRendersAsRectangle()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 24);
        window.Content = new ExplicitZeroRadiusYRoundedRectangleVisual();

        try
        {
            window.Render();

            byte[] pixels = window.ReadPixels();
            RgbaPixel corner = ReadPixel(pixels, window.Width, x: 5, y: 5);

            Assert.True(corner.R >= 220, $"Expected explicit zero RadiusY to keep the rectangle corner red, found {corner}.");
            Assert.True(corner.G <= 35, $"Expected explicit zero RadiusY to keep green low, found {corner}.");
            Assert.True(corner.B <= 35, $"Expected explicit zero RadiusY to keep blue low, found {corner}.");
            Assert.Equal(255, corner.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void RenderOffscreenKeepsPathAtlasBuffersAliveAfterSubmit()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen PathAtlas Buffer Lifetime Test");
        var visual = new DrawingVisual
        {
            Size = new Vector2(32f, 32f)
        };
        visual.Context.DrawPath(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            pen: null,
            PrimitivePathGeometry.CreateRoundedRectangle(4f, 4f, 20f, 16f, 4f, 4f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        Assert.NotEmpty(GetPathAtlasTempBuffers(window.Compositor));
    }

    [Fact]
    public void RenderOffscreenRunsExtensionFrameScopeForTopLevelPass()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen Extension Frame Scope Test");
        var extension = new CountingExtension();
        window.Compositor.RegisterExtension(9001, extension);

        var visual = new DrawingVisual
        {
            Size = new Vector2(32f, 32f)
        };
        visual.Context.DrawRectangle(
            new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            pen: null,
            new Rect(0f, 0f, 16f, 16f));

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f);

        Assert.Equal(1, extension.BeginFrameCount);
        Assert.Equal(1, extension.EndFrameCount);
    }

    [Fact]
    public void RenderSceneEndsExtensionFrameWhenCompilationThrows()
    {
        using var window = new HeadlessWindow(32, 32);
        var extension = new CountingExtension();
        window.Compositor.RegisterExtension(9002, extension);
        window.Content = new ThrowingVisual();

        Assert.Throws<InvalidOperationException>(() => window.Render());
        Assert.Equal(1, extension.BeginFrameCount);
        Assert.Equal(1, extension.EndFrameCount);
    }

    [Fact]
    public void RenderOffscreenEndsExtensionFrameWhenCompilationThrows()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            32,
            32,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen Extension Frame Exception Test");
        var extension = new CountingExtension();
        window.Compositor.RegisterExtension(9003, extension);

        Assert.Throws<InvalidOperationException>(() => window.Compositor.RenderOffscreen(
            new ThrowingVisual(),
            width: 32,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 1f));
        Assert.Equal(1, extension.BeginFrameCount);
        Assert.Equal(1, extension.EndFrameCount);
    }

    [Fact]
    public void RenderOffscreenAllocatesOpacityMaskTextureAtPhysicalSize()
    {
        using var window = new HeadlessWindow(32, 32);
        using var target = new GpuTexture(
            window.Context,
            64,
            64,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "RenderOffscreen Physical Mask Target");
        var visual = new OpacityMaskedVisual();

        window.Compositor.RenderOffscreen(
            visual,
            width: 32,
            height: 32,
            targetTexture: target,
            padding: 0f,
            dpiScale: 2f);

        var maskTexturePool = GetMaskTexturePool(window.Compositor);
        Assert.Contains(maskTexturePool, texture => texture.Width == 64 && texture.Height == 64);
    }

    [Fact]
    public void OpacityMaskWritesComputedAlphaIntoMaskTarget()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(120, 40);
        window.Content = new OpacityMaskAlphaVisual();

        try
        {
            window.Render();

            var pixels = window.ReadPixels();
            var blackMask = ReadPixel(pixels, window.Width, x: 16, y: 16);
            var blueMask = ReadPixel(pixels, window.Width, x: 56, y: 16);
            var halfOpacityMask = ReadPixel(pixels, window.Width, x: 96, y: 16);

            Assert.True(blackMask.R >= 220, $"Expected opaque black mask to preserve red draw, found {blackMask}.");
            Assert.True(blackMask.G <= 35, $"Expected opaque black mask green channel to stay low, found {blackMask}.");
            Assert.True(blackMask.B <= 35, $"Expected opaque black mask blue channel to stay low, found {blackMask}.");
            Assert.Equal(255, blackMask.A);

            Assert.True(blueMask.R >= 220, $"Expected opaque blue mask to preserve red draw, found {blueMask}.");
            Assert.True(blueMask.G <= 35, $"Expected opaque blue mask green channel to stay low, found {blueMask}.");
            Assert.True(blueMask.B <= 35, $"Expected opaque blue mask blue channel to stay low, found {blueMask}.");
            Assert.Equal(255, blueMask.A);

            Assert.InRange(halfOpacityMask.R, 110, 150);
            Assert.InRange(halfOpacityMask.G, 0, 16);
            Assert.InRange(halfOpacityMask.B, 0, 16);
            Assert.Equal(255, halfOpacityMask.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void CachedTextureBindGroupsAreQueuedWhenSourceTextureIsDisposed()
    {
        using var window = new HeadlessWindow(16, 16);
        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Texture BindGroup Disposal Queue Test");
        texture.WritePixels<byte>(new byte[] { 255, 0, 0, 255 });
        window.Content = new TextureCacheVisual(texture);

        window.Render();

        var textureId = texture.Id;
        var textureBindGroups = GetPersistentTextureBindGroups(window.Compositor);
        Assert.Contains(textureBindGroups.Keys, key => key.TextureId == textureId);
        Assert.Empty(window.Context.PendingBindGroups);

        texture.Dispose();
        window.Content = null;

        Assert.DoesNotContain(textureBindGroups.Keys, key => key.TextureId == textureId);
        lock (window.Context.DisposalLock)
        {
            Assert.Contains(window.Context.PendingBindGroups, ptr => ptr != IntPtr.Zero);
        }

        window.Context.CleanupPendingResources();
    }

    [Fact]
    public void GpuSeriesBufferReleaseBindGroupsQueuesCachedChartBindGroups()
    {
        using var window = new HeadlessWindow(16, 16);
        var previous = WgpuContext.Current;
        WgpuContext.Current = window.Context;

        using var seriesBuffer = new GpuSeriesBuffer();
        var lineBindGroup = (nint)0x1010;
        var scatterBindGroup = (nint)0x2020;
        var lineOffscreenBindGroup = (nint)0x3030;
        var scatterOffscreenBindGroup = (nint)0x4040;

        try
        {
            seriesBuffer.Upload(Array.Empty<float>(), pointsCount: 0);
            seriesBuffer.LineBindGroup = lineBindGroup;
            seriesBuffer.ScatterBindGroup = scatterBindGroup;
            seriesBuffer.LineBindGroupOffscreen = lineOffscreenBindGroup;
            seriesBuffer.ScatterBindGroupOffscreen = scatterOffscreenBindGroup;

            Assert.Empty(window.Context.PendingBindGroups);

            seriesBuffer.ReleaseBindGroups();

            Assert.Equal(0, seriesBuffer.LineBindGroup);
            Assert.Equal(0, seriesBuffer.ScatterBindGroup);
            Assert.Equal(0, seriesBuffer.LineBindGroupOffscreen);
            Assert.Equal(0, seriesBuffer.ScatterBindGroupOffscreen);

            lock (window.Context.DisposalLock)
            {
                Assert.Contains((IntPtr)lineBindGroup, window.Context.PendingBindGroups);
                Assert.Contains((IntPtr)scatterBindGroup, window.Context.PendingBindGroups);
                Assert.Contains((IntPtr)lineOffscreenBindGroup, window.Context.PendingBindGroups);
                Assert.Contains((IntPtr)scatterOffscreenBindGroup, window.Context.PendingBindGroups);
            }
        }
        finally
        {
            lock (window.Context.DisposalLock)
            {
                window.Context.PendingBindGroups.RemoveAll(ptr =>
                    ptr == (IntPtr)lineBindGroup ||
                    ptr == (IntPtr)scatterBindGroup ||
                    ptr == (IntPtr)lineOffscreenBindGroup ||
                    ptr == (IntPtr)scatterOffscreenBindGroup);
            }
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void ImageEffectPipelineDisposeQueuesGpuHandles()
    {
        using var window = new HeadlessWindow(16, 16);
        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Image Effect Disposal Queue Source");
        source.WritePixels(new byte[] { 255, 0, 0, 255 });
        window.Content = new ImageEffectCacheVisual(source);

        try
        {
            window.Render();

            var extension = Assert.IsAssignableFrom<IDisposable>(
                window.Compositor.GetExtension(CompositorBuiltInExtensions.ImageEffect));

            lock (window.Context.DisposalLock)
            {
                Assert.Empty(window.Context.PendingBindGroups);
            }

            extension.Dispose();

            lock (window.Context.DisposalLock)
            {
                Assert.NotEmpty(window.Context.PendingBindGroups);
                Assert.NotEmpty(window.Context.PendingBindGroupLayouts);
                Assert.NotEmpty(window.Context.PendingPipelineLayouts);
            }

            window.Context.CleanupPendingResources();
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void ShaderToyPipelineDisposeQueuesGpuHandles()
    {
        using var window = new HeadlessWindow(16, 16);
        var shader = new ShaderToyParams
        {
            Rect = new Rect(0f, 0f, 16f, 16f),
            ShaderKey = $"review_shadertoy_disposal_{System.Guid.NewGuid():N}",
            ShaderSource = SolidShaderToySource,
            Resolution = new Vector3(16f, 16f, 1f),
            Time = 0f,
            TimeDelta = 0f,
            Frame = 0f,
            FrameRate = 60f,
            Mouse = Vector4.Zero,
            Date = Vector4.Zero
        };
        window.Content = new ShaderToyDisposalVisual(shader);

        try
        {
            window.Render();

            Assert.False(shader.IsFailed);
            var extension = Assert.IsAssignableFrom<IDisposable>(
                window.Compositor.GetExtension(CompositorBuiltInExtensions.ShaderToy));

            lock (window.Context.DisposalLock)
            {
                Assert.Empty(window.Context.PendingBindGroups);
            }

            extension.Dispose();

            lock (window.Context.DisposalLock)
            {
                Assert.NotEmpty(window.Context.PendingBindGroups);
                Assert.NotEmpty(window.Context.PendingBindGroupLayouts);
                Assert.NotEmpty(window.Context.PendingPipelineLayouts);
            }

            window.Context.CleanupPendingResources();
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void WpfShaderEffectPipelineDisposeQueuesGpuHandles()
    {
        using var window = new HeadlessWindow(16, 16);
        using var source = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Disposal Queue Source");
        source.WritePixels(new byte[] { 255, 0, 0, 255 });
        var effect = new WpfShaderEffectParams
        {
            Texture = source,
            Rect = new Rect(0f, 0f, 16f, 16f),
            ShaderKey = $"review_wpf_shader_effect_disposal_{System.Guid.NewGuid():N}"
        };
        window.Content = new WpfShaderEffectDisposalVisual(effect);

        try
        {
            window.Render();

            Assert.False(effect.IsFailed, effect.LastError);
            var extension = Assert.IsAssignableFrom<IDisposable>(
                window.Compositor.GetExtension(CompositorBuiltInExtensions.WpfShaderEffect));

            lock (window.Context.DisposalLock)
            {
                Assert.Empty(window.Context.PendingBindGroups);
            }

            extension.Dispose();

            lock (window.Context.DisposalLock)
            {
                Assert.NotEmpty(window.Context.PendingBindGroups);
                Assert.NotEmpty(window.Context.PendingBindGroupLayouts);
                Assert.NotEmpty(window.Context.PendingPipelineLayouts);
            }

            window.Context.CleanupPendingResources();
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public unsafe void GpuBufferDisposeQueuesNativeBufferDisposal()
    {
        using var window = new HeadlessWindow(16, 16);
        using var buffer = new GpuBuffer(
            window.Context,
            16,
            BufferUsage.Vertex | BufferUsage.CopyDst,
            "Explicit Buffer Disposal Queue Test");
        var bufferPtr = (IntPtr)buffer.BufferPtr;

        Assert.NotEqual(IntPtr.Zero, bufferPtr);
        Assert.Empty(window.Context.PendingBuffers);

        buffer.Dispose();

        Assert.True(buffer.BufferPtr == null);
        lock (window.Context.DisposalLock)
        {
            Assert.Contains(bufferPtr, window.Context.PendingBuffers);
        }

        window.Context.CleanupPendingResources();
    }

    private static void AssertMixedColorGlyphDrawCalls(Compositor compositor)
    {
        Compositor.CompositorDrawCall[] drawCalls = GetDrawCalls(compositor);
        Assert.Contains(drawCalls, drawCall => drawCall.Type == Compositor.DrawCallType.Vector);

        Compositor.CompositorDrawCall textDraw = Assert.Single(
            drawCalls,
            drawCall => drawCall.Type == Compositor.DrawCallType.Text && drawCall.IndexCount > 0);
        Assert.Equal(1u, textDraw.IndexCount);
    }

    private static Compositor.CompositorDrawCall[] GetDrawCalls(Compositor compositor)
    {
        var field = typeof(Compositor).GetField("_drawCalls", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var drawCalls = Assert.IsAssignableFrom<IEnumerable<Compositor.CompositorDrawCall>>(field.GetValue(compositor));
        return drawCalls.ToArray();
    }

    private static T GetCompositorField<T>(Compositor compositor, string fieldName)
    {
        var field = typeof(Compositor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(compositor));
    }

    private static object? GetRawCompositorField(Compositor compositor, string fieldName)
    {
        var field = typeof(Compositor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(compositor);
    }

    private static void SetCompositorField(Compositor compositor, string fieldName, object? value)
    {
        var field = typeof(Compositor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(compositor, value);
    }

    private static IList GetPathAtlasTempBuffers(Compositor compositor)
    {
        var pathAtlasField = typeof(Compositor).GetField("_pathAtlas", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pathAtlasField);
        var pathAtlas = pathAtlasField.GetValue(compositor);
        Assert.NotNull(pathAtlas);

        var tempBuffersField = pathAtlas.GetType().GetField("_tempBuffers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(tempBuffersField);
        return Assert.IsAssignableFrom<IList>(tempBuffersField.GetValue(pathAtlas));
    }

    private static Dictionary<Compositor.TextureCacheKey, Compositor.CachedBindGroup> GetPersistentTextureBindGroups(Compositor compositor)
    {
        var field = typeof(Compositor).GetField("_persistentTextureBindGroups", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<Dictionary<Compositor.TextureCacheKey, Compositor.CachedBindGroup>>(field.GetValue(compositor));
    }

    private static List<GpuTexture> GetMaskTexturePool(Compositor compositor)
    {
        var field = typeof(Compositor).GetField("_maskTexturePool", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<List<GpuTexture>>(field.GetValue(compositor));
    }

    private static RgbaPixel ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        return new RgbaPixel(
            pixels[index + 0],
            pixels[index + 1],
            pixels[index + 2],
            pixels[index + 3]);
    }

    private static byte[] BuildColorLayerFont()
    {
        byte[][] glyphs =
        {
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            BuildRectangleGlyph(0, 0, 500, 500),
            BuildRectangleGlyph(120, 120, 620, 620),
        };

        byte[] glyf = BuildGlyfTable(glyphs, out uint[] glyphOffsets);
        return BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable(glyphs.Length)),
            ("maxp", BuildMaxpTable(glyphs.Length)),
            ("hmtx", BuildHmtxTable(glyphs.Length)),
            ("cmap", BuildCmapFormat12Table()),
            ("loca", BuildLongLoca(glyphOffsets)),
            ("glyf", glyf),
            ("COLR", BuildColrTable()),
            ("CPAL", BuildCpalTable()));
    }

    private static byte[] BuildMissingGlyphOutlineFont()
    {
        byte[][] glyphs =
        {
            BuildRectangleGlyph(0, 0, 500, 500),
            BuildRectangleGlyph(100, 100, 500, 500),
        };

        byte[] glyf = BuildGlyfTable(glyphs, out uint[] glyphOffsets);
        return BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable(glyphs.Length)),
            ("maxp", BuildMaxpTable(glyphs.Length)),
            ("hmtx", BuildHmtxTable(glyphs.Length)),
            ("cmap", BuildSingleMappedGlyphCmapFormat12Table()),
            ("loca", BuildLongLoca(glyphOffsets)),
            ("glyf", glyf));
    }

    private static byte[] BuildSingleMappedGlyphCmapFormat12Table()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUShort(writer, 3);
        WriteUShort(writer, 10);
        WriteUInt(writer, 12);
        WriteUShort(writer, 12);
        WriteUShort(writer, 0);
        WriteUInt(writer, 28);
        WriteUInt(writer, 0);
        WriteUInt(writer, 1);
        WriteUInt(writer, (uint)'A');
        WriteUInt(writer, (uint)'A');
        WriteUInt(writer, 1);
        return stream.ToArray();
    }

    private sealed class TextureCacheVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public TextureCacheVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawTexture(_texture, new Rect(0f, 0f, 16f, 16f));
        }
    }

    private sealed class ImageEffectCacheVisual : FrameworkElement
    {
        private readonly GpuTexture _texture;

        public ImageEffectCacheVisual(GpuTexture texture)
        {
            _texture = texture;
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawImageWithEffect(
                _texture,
                new Rect(0f, 0f, 16f, 16f),
                blurSigma: 1f);
        }
    }

    private static byte[] BuildRectangleGlyph(short xMin, short yMin, short xMax, short yMax)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteShort(writer, 1);
        WriteShort(writer, xMin);
        WriteShort(writer, yMin);
        WriteShort(writer, xMax);
        WriteShort(writer, yMax);
        WriteUShort(writer, 3);
        WriteUShort(writer, 0);
        writer.Write(new byte[] { 1, 1, 1, 1 });
        WriteShort(writer, xMin);
        WriteShort(writer, (short)(xMax - xMin));
        WriteShort(writer, 0);
        WriteShort(writer, (short)(xMin - xMax));
        WriteShort(writer, yMin);
        WriteShort(writer, 0);
        WriteShort(writer, (short)(yMax - yMin));
        WriteShort(writer, 0);
        return stream.ToArray();
    }

    private static byte[] BuildGlyfTable(byte[][] glyphs, out uint[] glyphOffsets)
    {
        glyphOffsets = new uint[glyphs.Length + 1];
        using var stream = new MemoryStream();

        for (int i = 0; i < glyphs.Length; i++)
        {
            glyphOffsets[i] = checked((uint)stream.Position);
            stream.Write(glyphs[i]);
            WritePadding(stream);
        }

        glyphOffsets[^1] = checked((uint)stream.Position);
        return stream.ToArray();
    }

    private static byte[] BuildHeadTable()
    {
        byte[] table = new byte[54];
        using var stream = new MemoryStream(table);
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUInt(writer, 0x00010000);
        stream.Position = 18;
        WriteUShort(writer, 1000);
        stream.Position = 50;
        WriteShort(writer, 1);
        return table;
    }

    private static byte[] BuildHheaTable(int glyphCount)
    {
        byte[] table = new byte[36];
        using var stream = new MemoryStream(table);
        using var writer = new BinaryWriter(stream);

        stream.Position = 4;
        WriteShort(writer, 800);
        WriteShort(writer, -200);
        WriteShort(writer, 0);
        stream.Position = 34;
        WriteUShort(writer, checked((ushort)glyphCount));
        return table;
    }

    private static byte[] BuildMaxpTable(int glyphCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, checked((ushort)glyphCount));
        return stream.ToArray();
    }

    private static byte[] BuildHmtxTable(int glyphCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        for (int i = 0; i < glyphCount; i++)
        {
            WriteUShort(writer, 600);
            WriteShort(writer, 0);
        }

        return stream.ToArray();
    }

    private static byte[] BuildCmapFormat12Table()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUShort(writer, 3);
        WriteUShort(writer, 10);
        WriteUInt(writer, 12);
        WriteUShort(writer, 12);
        WriteUShort(writer, 0);
        WriteUInt(writer, 28);
        WriteUInt(writer, 0);
        WriteUInt(writer, 1);
        WriteUInt(writer, (uint)'A');
        WriteUInt(writer, (uint)'B');
        WriteUInt(writer, 1);
        return stream.ToArray();
    }

    private static byte[] BuildLongLoca(uint[] glyphOffsets)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        foreach (uint offset in glyphOffsets)
        {
            WriteUInt(writer, offset);
        }

        return stream.ToArray();
    }

    private static byte[] BuildColrTable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUInt(writer, 14);
        WriteUInt(writer, 20);
        WriteUShort(writer, 2);
        WriteUShort(writer, 1);
        WriteUShort(writer, 0);
        WriteUShort(writer, 2);
        WriteUShort(writer, 2);
        WriteUShort(writer, 0);
        WriteUShort(writer, 3);
        WriteUShort(writer, 1);
        return stream.ToArray();
    }

    private static byte[] BuildCpalTable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 2);
        WriteUShort(writer, 1);
        WriteUShort(writer, 2);
        WriteUInt(writer, 14);
        WriteUShort(writer, 0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)255);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)255);
        return stream.ToArray();
    }

    private static byte[] BuildSfntWithTables(params (string Tag, byte[] Data)[] tables)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, checked((ushort)tables.Length));
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);

        uint tableOffset = (uint)(12 + tables.Length * 16);
        foreach ((string tag, byte[] data) in tables)
        {
            WriteTag(writer, tag);
            WriteUInt(writer, 0);
            WriteUInt(writer, tableOffset);
            WriteUInt(writer, (uint)data.Length);
            tableOffset += (uint)data.Length;
        }

        foreach ((_, byte[] data) in tables)
        {
            writer.Write(data);
        }

        return stream.ToArray();
    }

    private static void WritePadding(Stream stream)
    {
        while ((stream.Position & 3) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteTag(BinaryWriter writer, string tag)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(tag);
        Assert.Equal(4, bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteShort(BinaryWriter writer, short value)
    {
        WriteUShort(writer, unchecked((ushort)value));
    }

    private static void WriteUShort(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteUInt(BinaryWriter writer, uint value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class CountingExtension : ICompositorExtension
    {
        public int BeginFrameCount { get; private set; }

        public int EndFrameCount { get; private set; }

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
        }

        public void BeginFrame(Compositor compositor)
        {
            BeginFrameCount++;
        }

        public void EndFrame(Compositor compositor)
        {
            EndFrameCount++;
        }
    }

    private sealed class CanvasSizeRecordingExtension : ICompositorExtension
    {
        private static readonly PropertyInfo s_canvasPixelWidthProperty =
            typeof(Compositor).GetProperty("CurrentCanvasPixelWidth", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Compositor).FullName, "CurrentCanvasPixelWidth");

        private static readonly PropertyInfo s_canvasPixelHeightProperty =
            typeof(Compositor).GetProperty("CurrentCanvasPixelHeight", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Compositor).FullName, "CurrentCanvasPixelHeight");

        public int RenderCount { get; private set; }

        public float CanvasPixelWidth { get; private set; }

        public float CanvasPixelHeight { get; private set; }

        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            RenderCount++;
            CanvasPixelWidth = (float)s_canvasPixelWidthProperty.GetValue(compositor)!;
            CanvasPixelHeight = (float)s_canvasPixelHeightProperty.GetValue(compositor)!;
        }
    }

    private sealed class ThrowingVisual : FrameworkElement
    {
        public ThrowingVisual()
        {
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            throw new InvalidOperationException("Synthetic render failure.");
        }
    }

    private sealed class OpacityMaskedVisual : FrameworkElement
    {
        public OpacityMaskedVisual()
        {
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                new Rect(0f, 0f, 32f, 32f));
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                pen: null,
                new Rect(0f, 0f, 32f, 32f));
            context.PopOpacityMask();
        }
    }

    private sealed class OpacityMaskAlphaVisual : FrameworkElement
    {
        private readonly SolidColorBrush _background = new(new Vector4(0f, 0f, 0f, 1f));
        private readonly SolidColorBrush _red = new(new Vector4(1f, 0f, 0f, 1f));

        public OpacityMaskAlphaVisual()
        {
            Width = 120f;
            Height = 40f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(_background, null, new Rect(0f, 0f, 120f, 40f));

            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                new Rect(0f, 0f, 32f, 32f));
            context.DrawRectangle(_red, null, new Rect(0f, 0f, 32f, 32f));
            context.PopOpacityMask();

            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
                new Rect(40f, 0f, 32f, 32f));
            context.DrawRectangle(_red, null, new Rect(40f, 0f, 32f, 32f));
            context.PopOpacityMask();

            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)) { Opacity = 0.5f },
                new Rect(80f, 0f, 32f, 32f));
            context.DrawRectangle(_red, null, new Rect(80f, 0f, 32f, 32f));
            context.PopOpacityMask();
        }
    }

    private sealed class GpuSeriesOpacityVisual : FrameworkElement
    {
        public GpuSeriesOpacityVisual()
        {
            Width = 64f;
            Height = 64f;
        }

        public override void OnRender(DrawingContext context)
        {
            int lineOffset = context.FloatBuffer.Count;
            context.FloatBuffer.AddRange(new[] { 0f, 0f, 20f, 20f });
            int scatterOffset = context.FloatBuffer.Count;
            context.FloatBuffer.AddRange(new[] { 4f, 4f, 6f, 24f, 24f, 6f });

            context.PushOpacity(0.5f);
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawGpuLineSeries,
                FloatBufferOffset = lineOffset,
                FloatBufferCount = 4,
                GpuPointsCount = 2,
                RadiusX = 2f,
                Brush = new SolidColorBrush(new Vector4(0.2f, 0.3f, 0.4f, 0.6f)) { Opacity = 0.5f },
                Scale = Vector2.One
            });
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawGpuScatterSeries,
                FloatBufferOffset = scatterOffset,
                FloatBufferCount = 6,
                GpuPointsCount = 2,
                RadiusX = 6f,
                Brush = new SolidColorBrush(new Vector4(0.8f, 0.7f, 0.6f, 0.8f)) { Opacity = 0.25f },
                Scale = Vector2.One
            });
            context.PopOpacity();
        }
    }

    private sealed class ShaderToyDisposalVisual : FrameworkElement
    {
        private readonly ShaderToyParams _shader;

        public ShaderToyDisposalVisual(ShaderToyParams shader)
        {
            _shader = shader;
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawExtension(
                CompositorBuiltInExtensions.ShaderToy,
                dataParam: _shader);
        }
    }

    private sealed class WpfShaderEffectDisposalVisual : FrameworkElement
    {
        private readonly WpfShaderEffectParams _effect;

        public WpfShaderEffectDisposalVisual(WpfShaderEffectParams effect)
        {
            _effect = effect;
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawWpfShaderEffect(_effect);
        }
    }

    private sealed class CachedLayerResizeVisual : DrawingVisual
    {
        public CachedLayerResizeVisual()
            : this(new Vector2(32f, 16f))
        {
        }

        public CachedLayerResizeVisual(Vector2 size)
        {
            Size = size;
            CacheAsLayer = true;
            Context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                pen: null,
                new Rect(Vector2.Zero, size));
        }
    }

    private sealed class SolidLogicalSceneVisual : DrawingVisual
    {
        public SolidLogicalSceneVisual()
            : this(new Vector2(10f, 10f))
        {
        }

        public SolidLogicalSceneVisual(Vector2 size)
        {
            Size = size;
            Context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                pen: null,
                new Rect(Vector2.Zero, size));
        }
    }

    private sealed class ThrowingRenderVisual : FrameworkElement
    {
        public ThrowingRenderVisual()
        {
            Width = 16f;
            Height = 16f;
        }

        public override void OnRender(DrawingContext context)
        {
            throw new System.InvalidOperationException("Synthetic offscreen render failure.");
        }
    }

    private sealed class MixedColorGlyphRunVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public MixedColorGlyphRunVisual(TtfFont font)
        {
            _font = font;
            Width = 96f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawGlyphRun(
                new ushort[] { 1, 2 },
                new[] { new Vector2(6f, 30f), new Vector2(36f, 30f) },
                _font,
                24f,
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                Vector2.Zero);
        }
    }

    private sealed class MixedColorTextVisual : FrameworkElement
    {
        private readonly TtfFont _font;

        public MixedColorTextVisual(TtfFont font)
        {
            _font = font;
            Width = 96f;
            Height = 48f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawText(
                "AB",
                _font,
                24f,
                new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)),
                new Vector2(6f, 30f));
        }
    }

    private sealed class TransformedEllipticalRoundedRectangleVisual : FrameworkElement
    {
        public TransformedEllipticalRoundedRectangleVisual()
        {
            Width = 80f;
            Height = 40f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawRoundedRect,
                Brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                Rect = new Rect(0f, 0f, 24f, 24f),
                RadiusX = 4f,
                RadiusY = 8f,
                Transform = Matrix4x4.CreateTranslation(20f, 5f, 0f)
            });
        }
    }

    private sealed class ExplicitZeroRadiusYRoundedRectangleVisual : FrameworkElement
    {
        public ExplicitZeroRadiusYRoundedRectangleVisual()
        {
            Width = 32f;
            Height = 24f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawRoundedRect,
                Brush = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
                Rect = new Rect(4f, 4f, 20f, 12f),
                RadiusX = 8f,
                RadiusY = 0f
            });
        }
    }
}
