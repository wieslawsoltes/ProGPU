using System.Numerics;
using Avalonia.Media;
using Avalonia.ProGpu;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Avalonia.ContractTests;

public sealed class AvaloniaCompositionLayoutClipContractTests
{
    [Fact]
    public void LayoutClipUpdatesSizeWithoutRebuildingGeometryClip()
    {
        var visual = new AvaloniaCompositionVisual();
        visual.SynchronizeGeometryClip(
            PrimitivePathGeometry.CreateRectangle(10f, 20f, 80f, 60f));

        visual.SynchronizeLayoutClip(new Vector2(50f, 100f), true);

        Assert.Equal(new Vector2(50f, 100f), visual.Size);
        Assert.Null(visual.GeometryClip);
        Assert.Equal(
            new ProGPU.Scene.Rect(10f, 20f, 40f, 60f),
            visual.ClipBounds);

        visual.SynchronizeLayoutClip(new Vector2(200f, 200f), false);

        Assert.Null(visual.GeometryClip);
        Assert.Equal(
            new ProGPU.Scene.Rect(10f, 20f, 80f, 60f),
            visual.ClipBounds);
    }

    [Fact]
    public void LayoutClipRetainsNonRectangularGeometryIdentity()
    {
        var visual = new AvaloniaCompositionVisual();
        var geometry = PrimitivePathGeometry.CreateRoundedRectangle(
            4f,
            8f,
            40f,
            30f,
            5f,
            5f);
        visual.SynchronizeGeometryClip(geometry);

        visual.SynchronizeLayoutClip(new Vector2(24f, 20f), true);

        Assert.Same(geometry, visual.GeometryClip);
        Assert.Equal(
            new ProGPU.Scene.Rect(0f, 0f, 24f, 20f),
            visual.ClipBounds);
    }

    [Fact]
    public void BitmapCacheScaleAndSnappingUpdateWithoutChangingTextPolicy()
    {
        var visual = new AvaloniaCompositionVisual();

        bool drawingOptionsChanged = visual.SynchronizeBitmapCache(
            hasBitmapCache: true,
            renderScale: 1.75f,
            snapsToDevicePixels: true,
            enableClearType: true);

        Assert.False(drawingOptionsChanged);
        Assert.True(visual.CacheAsLayer);
        Assert.Equal(1.75f, visual.LayerCacheRenderScale);
        Assert.True(visual.LayerCacheSnapsToDevicePixels);

        drawingOptionsChanged = visual.SynchronizeBitmapCache(
            hasBitmapCache: true,
            renderScale: 2f,
            snapsToDevicePixels: false,
            enableClearType: true);

        Assert.False(drawingOptionsChanged);
        Assert.Equal(2f, visual.LayerCacheRenderScale);
        Assert.False(visual.LayerCacheSnapsToDevicePixels);
    }

    [Fact]
    public void BitmapCacheClearTypePolicyRequestsDescendantRefresh()
    {
        var visual = new AvaloniaCompositionVisual();

        Assert.True(visual.SynchronizeBitmapCache(
            hasBitmapCache: true,
            renderScale: 1f,
            snapsToDevicePixels: false,
            enableClearType: false));
        Assert.False(visual.SynchronizeBitmapCache(
            hasBitmapCache: true,
            renderScale: 1.5f,
            snapsToDevicePixels: true,
            enableClearType: false));
        Assert.True(visual.SynchronizeBitmapCache(
            hasBitmapCache: true,
            renderScale: 1.5f,
            snapsToDevicePixels: true,
            enableClearType: true));
    }

    [Fact]
    public void DrawingOptionsUseNearestVisualAndPropagateClearTypePolicy()
    {
        var parent = new AvaloniaCompositionVisual();
        var child = new AvaloniaCompositionVisual();
        parent.AddChild(child);

        var parentText = new TextOptions
        {
            TextRenderingMode = TextRenderingMode.Alias,
            TextHintingMode = TextHintingMode.Strong
        };
        Assert.True(parent.SynchronizeDrawingOptions(
            new RenderOptions
            {
                BitmapInterpolationMode =
                    global::Avalonia.Media.Imaging.BitmapInterpolationMode
                        .LowQuality
            },
            parentText,
            inheritedRenderOptions: default,
            inheritedTextOptions: default,
            inheritedDisablesSubpixelText: false,
            out bool parentOptionsChanged));
        Assert.True(parentOptionsChanged);

        var childText = new TextOptions
        {
            TextRenderingMode = TextRenderingMode.Antialias
        };
        Assert.True(child.SynchronizeDrawingOptions(
            localRenderOptions: default,
            childText,
            parent.EffectiveRenderOptions,
            parent.EffectiveTextOptions,
            parent.DisablesSubpixelText,
            out bool childOptionsChanged));
        Assert.True(childOptionsChanged);
        Assert.Equal(
            TextRenderingMode.Antialias,
            child.EffectiveTextOptions.TextRenderingMode);
        Assert.Equal(
            TextHintingMode.Strong,
            child.EffectiveTextOptions.TextHintingMode);
        Assert.Equal(
            global::Avalonia.Media.Imaging.BitmapInterpolationMode.LowQuality,
            child.EffectiveRenderOptions.BitmapInterpolationMode);

        Assert.True(parent.SynchronizeBitmapCache(
            hasBitmapCache: true,
            renderScale: 1f,
            snapsToDevicePixels: false,
            enableClearType: false));
        Assert.True(parent.SynchronizeDrawingOptions(
            parent.LocalRenderOptions,
            new TextOptions
            {
                TextRenderingMode =
                    TextRenderingMode.SubpixelAntialias
            },
            inheritedRenderOptions: default,
            inheritedTextOptions: default,
            inheritedDisablesSubpixelText: false,
            out parentOptionsChanged));
        Assert.True(parentOptionsChanged);
        Assert.True(parent.DisablesSubpixelText);
        Assert.Equal(
            TextRenderingMode.Antialias,
            parent.EffectiveTextOptions.TextRenderingMode);

        Assert.True(child.SynchronizeDrawingOptions(
            localRenderOptions: default,
            localTextOptions: default,
            parent.EffectiveRenderOptions,
            new TextOptions
            {
                TextRenderingMode =
                    TextRenderingMode.SubpixelAntialias
            },
            parent.DisablesSubpixelText,
            out childOptionsChanged));
        Assert.True(childOptionsChanged);
        Assert.True(child.DisablesSubpixelText);
        Assert.Equal(
            TextRenderingMode.Antialias,
            child.EffectiveTextOptions.TextRenderingMode);
    }

    [Fact]
    public void BlurEffectScalarSnapshotReusesEffectObjectAndRecoversContentBounds()
    {
        var visual = new AvaloniaCompositionVisual();
        var outputBounds = new Vector4(-7f, -7f, 114f, 64f);
        var size = new Vector2(100f, 50f);

        visual.SynchronizeEffect(
            AvaloniaCompositionEffectKind.Blur,
            rawRadius: 6f,
            rawOffset: default,
            packedColor: 0,
            rawOpacity: 0f,
            hasOutputBounds: true,
            outputBounds,
            size);

        var effect = Assert.IsType<ProGPU.Scene.BlurEffect>(visual.Effect);
        Assert.Equal(6f * 0.2886751345948129f + 0.5f, effect.BlurRadius);
        Assert.Equal(7f, visual.EffectRasterPadding);
        Assert.Equal(
            new ProGPU.Scene.Rect(0f, 0f, 100f, 50f),
            visual.EffectContentBounds);

        visual.SynchronizeEffect(
            AvaloniaCompositionEffectKind.Blur,
            rawRadius: 8f,
            rawOffset: default,
            packedColor: 0,
            rawOpacity: 0f,
            hasOutputBounds: true,
            new Vector4(-9f, -9f, 118f, 68f),
            size);

        Assert.Same(effect, visual.Effect);
        Assert.Equal(9f, visual.EffectRasterPadding);
        Assert.Equal(
            new ProGPU.Scene.Rect(0f, 0f, 100f, 50f),
            visual.EffectContentBounds);
    }

    [Fact]
    public void DropShadowScalarSnapshotPreservesColorOpacityOffsetAndBounds()
    {
        var visual = new AvaloniaCompositionVisual();

        visual.SynchronizeEffect(
            AvaloniaCompositionEffectKind.DropShadow,
            rawRadius: 4f,
            rawOffset: new Vector2(-2f, 3f),
            packedColor: 0x80402010,
            rawOpacity: 0.5f,
            hasOutputBounds: true,
            new Vector4(3f, 18f, 110f, 60f),
            new Vector2(100f, 50f));

        var effect =
            Assert.IsType<ProGPU.Scene.DropShadowEffect>(visual.Effect);
        Assert.Equal(new Vector2(-2f, 3f), effect.Offset);
        Assert.Equal(
            new Vector4(
                0x40 / 255f,
                0x20 / 255f,
                0x10 / 255f,
                0x80 / 255f * 0.5f),
            effect.Color);
        Assert.Equal(5f, visual.EffectRasterPadding);
        Assert.Equal(
            new ProGPU.Scene.Rect(10f, 20f, 100f, 50f),
            visual.EffectContentBounds);

        visual.SynchronizeEffect(
            AvaloniaCompositionEffectKind.None,
            rawRadius: 0f,
            rawOffset: default,
            packedColor: 0,
            rawOpacity: 0f,
            hasOutputBounds: false,
            outputBounds: default,
            size: default);

        Assert.Null(visual.Effect);
        Assert.Null(visual.EffectRasterPadding);
        Assert.Null(visual.EffectContentBounds);
    }

    [Fact]
    public void OpacityMaskSnapshotUpdatesBrushAndBoundsWithoutSourceReread()
    {
        using var renderer = NewDrawingContext();
        var visual = new AvaloniaCompositionVisual();

        visual.SynchronizeOpacityMask(
            Brushes.Red,
            hasBounds: true,
            new Vector4(3f, 4f, 80f, 40f),
            renderer);

        Assert.Equal(
            new Vector4(1f, 0f, 0f, 1f),
            Assert.IsType<ProGPU.Vector.SolidColorBrush>(
                visual.OpacityMask).Color);
        Assert.Null(visual.OpacityMaskPicture);
        Assert.Equal(
            new ProGPU.Scene.Rect(3f, 4f, 80f, 40f),
            visual.OpacityMaskBounds);

        visual.SynchronizeOpacityMask(
            Brushes.Blue,
            hasBounds: true,
            new Vector4(5f, 6f, 60f, 30f),
            renderer);

        Assert.Equal(
            new Vector4(0f, 0f, 1f, 1f),
            Assert.IsType<ProGPU.Vector.SolidColorBrush>(
                visual.OpacityMask).Color);
        Assert.Equal(
            new ProGPU.Scene.Rect(5f, 6f, 60f, 30f),
            visual.OpacityMaskBounds);
    }

    [Fact]
    public void OpacityMaskSnapshotClearsRetainedMaskState()
    {
        using var renderer = NewDrawingContext();
        var visual = new AvaloniaCompositionVisual();
        visual.SynchronizeOpacityMask(
            Brushes.White,
            hasBounds: true,
            new Vector4(0f, 0f, 32f, 24f),
            renderer);

        visual.SynchronizeOpacityMask(
            opacityMask: null,
            hasBounds: false,
            bounds: default,
            renderer);

        Assert.Null(visual.OpacityMask);
        Assert.Null(visual.OpacityMaskPicture);
        Assert.Null(visual.OpacityMaskBounds);
    }

    private static DrawingContextImpl NewDrawingContext() =>
        new(
            new DrawingContextImpl.CreateInfo
            {
                Size = new global::Avalonia.PixelSize(64, 48),
                Dpi = new global::Avalonia.Vector(96, 96)
            });
}
