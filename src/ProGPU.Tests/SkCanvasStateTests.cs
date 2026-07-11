using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using SkiaSharp;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCanvasStateTests
{
    [Fact]
    public void SurfaceAndCanvasReuseIsolatedCompositorScopes()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));

        var surfaceCompositor = GetSurfaceCompositor(surface);
        var canvasCompositor = GetCanvasCompositor(surface);

        Assert.Same(surfaceCompositor, GetSurfaceCompositor(surface));
        Assert.Same(canvasCompositor, GetCanvasCompositor(surface));
        Assert.NotSame(surfaceCompositor, canvasCompositor);
    }

    [Fact]
    public void ClearUsesSourceBlendMode()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 12f, 34f);

        canvas.Clear(new SKColor(10, 20, 30, 128));

        Assert.Collection(
            context.Commands,
            push =>
            {
                Assert.Equal(RenderCommandType.PushBlendMode, push.Type);
                Assert.Equal((int)GpuBlendMode.Src, push.IntParam);
            },
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawRect, draw.Type);
                Assert.Equal(new Rect(0f, 0f, 12f, 34f), draw.Rect);
                var brush = Assert.IsType<SolidColorBrush>(draw.Brush);
                AssertNear(10f / 255f, brush.Color.X);
                AssertNear(20f / 255f, brush.Color.Y);
                AssertNear(30f / 255f, brush.Color.Z);
                AssertNear(128f / 255f, brush.Color.W);
                AssertMatrixNear(Matrix4x4.Identity, draw.Transform);
            },
            pop => Assert.Equal(RenderCommandType.PopBlendMode, pop.Type));
    }

    [Fact]
    public void SaveLayerAppliesPaintOpacityWhenLayerIsRestored()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var halfAlpha = new SKPaint { Color = new SKColor(0, 0, 0, 128) };
        using var fill = new SKPaint { Color = SKColors.Red };

        var outerRestoreCount = canvas.SaveLayer(halfAlpha);
        canvas.DrawRect(new SKRect(10f, 10f, 40f, 40f), fill);

        Assert.Equal(0, outerRestoreCount);
        Assert.Empty(context.Commands);

        canvas.RestoreToCount(outerRestoreCount);

        Assert.Collection(
            context.Commands,
            push =>
            {
                Assert.Equal(RenderCommandType.PushOpacity, push.Type);
                AssertNear(128f / 255f, push.FontSize);
            },
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawTexture, draw.Type);
                Assert.Equal(new Rect(0f, 0f, 100f, 100f), draw.Rect);
                Assert.NotNull(draw.Texture);
            },
            pop => Assert.Equal(RenderCommandType.PopOpacity, pop.Type));
    }

    [Fact]
    public void SaveLayerAppliesPaintBlendModeWhenLayerIsRestored()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var sourcePaint = new SKPaint { BlendMode = SKBlendMode.Src };
        using var fill = new SKPaint { Color = SKColors.Red };

        var restoreCount = canvas.SaveLayer(sourcePaint);
        canvas.DrawRect(new SKRect(10f, 10f, 40f, 40f), fill);

        Assert.Empty(context.Commands);

        canvas.RestoreToCount(restoreCount);

        Assert.Collection(
            context.Commands,
            push =>
            {
                Assert.Equal(RenderCommandType.PushBlendMode, push.Type);
                Assert.Equal((int)GpuBlendMode.Src, push.IntParam);
            },
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawTexture, draw.Type);
                Assert.NotNull(draw.Texture);
            },
            pop => Assert.Equal(RenderCommandType.PopBlendMode, pop.Type));
    }

    [Fact]
    public void SaveLayerWithDefaultPaintStillRendersIsolatedTexture()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var layerPaint = new SKPaint();
        using var fill = new SKPaint { Color = SKColors.Red };

        var restoreCount = canvas.SaveLayer(layerPaint);
        canvas.DrawRect(new SKRect(10f, 10f, 40f, 40f), fill);

        Assert.Empty(context.Commands);

        canvas.RestoreToCount(restoreCount);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawTexture, command.Type);
        Assert.Equal(new Rect(0f, 0f, 100f, 100f), command.Rect);
        Assert.NotNull(command.Texture);
    }

    [Fact]
    public void SaveLayerBoundsClipRestoredLayerComposition()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var layerPaint = new SKPaint();
        using var fill = new SKPaint { Color = SKColors.Red };

        var restoreCount = canvas.SaveLayer(new SKRect(5f, 6f, 25f, 26f), layerPaint);
        canvas.DrawRect(new SKRect(50f, 50f, 60f, 60f), fill);

        canvas.RestoreToCount(restoreCount);

        Assert.Collection(
            context.Commands,
            push =>
            {
                Assert.Equal(RenderCommandType.PushClip, push.Type);
                Assert.Equal(new Rect(5f, 6f, 20f, 20f), push.Rect);
                AssertMatrixNear(Matrix4x4.Identity, push.Transform);
            },
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawTexture, draw.Type);
                Assert.Equal(new Rect(0f, 0f, 100f, 100f), draw.Rect);
                Assert.NotNull(draw.Texture);
            },
            pop => Assert.Equal(RenderCommandType.PopClip, pop.Type));
    }

    [Fact]
    public void SaveLayerAppliesBlurImageFilterWhenLayerIsRestored()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var layerPaint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(3f, 4f) };
        using var fill = new SKPaint { Color = SKColors.Red };

        var restoreCount = canvas.SaveLayer(layerPaint);
        canvas.DrawRect(new SKRect(10f, 10f, 40f, 40f), fill);

        canvas.RestoreToCount(restoreCount);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawTexture, command.Type);
        Assert.NotNull(command.Texture);
        Assert.Equal(new Rect(0f, 0f, 100f, 100f), command.Rect);
        Assert.Equal(1, context.RetainedResourceCount);
        Assert.Empty(GetOwnedLayerTextures(canvas));
    }

    [Fact]
    public void SaveLayerBlurImageFilterClipsOffscreenSourceBeforeFiltering()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var layerPaint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(3f, 3f) };
        using var fill = new SKPaint { Color = SKColors.Red };

        surface.Canvas.Save();
        surface.Canvas.ClipRect(new SKRect(16f, 8f, 28f, 24f));
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(8f, 8f, 15f, 24f), fill);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Canvas.Restore();
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var clippedEdgePixelOffset = (16 * 32 + 16) * 4;

        Assert.InRange(pixels[clippedEdgePixelOffset], 0, 4);
        Assert.InRange(pixels[clippedEdgePixelOffset + 1], 0, 4);
        Assert.InRange(pixels[clippedEdgePixelOffset + 2], 0, 4);
        Assert.InRange(pixels[clippedEdgePixelOffset + 3], 0, 4);
    }

    [Fact]
    public void SaveLayerBlurImageFilterKeepsHorizontalAndVerticalSigmaIndependent()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var layerPaint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(0f, 2f) };
        using var fill = new SKPaint { Color = SKColors.Red, IsAntialias = false };

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(14f, 14f, 18f, 18f), fill);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var verticalTailAlpha = pixels[(11 * 32 + 16) * 4 + 3];
        var horizontalTailAlpha = pixels[(16 * 32 + 11) * 4 + 3];
        var centerAlpha = pixels[(16 * 32 + 16) * 4 + 3];

        Assert.InRange(verticalTailAlpha, 1, 254);
        Assert.Equal((byte)0, horizontalTailAlpha);
        Assert.True(centerAlpha > verticalTailAlpha);
    }

    [Fact]
    public void FilledIntegerAlignedRectangleKeepsInteriorCornerOpaque()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var fill = new SKPaint { Color = SKColors.Red, IsAntialias = true };

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawRect(new SKRect(5f, 5f, 25f, 15f), fill);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();

        Assert.Equal((byte)255, pixels[(14 * 32 + 24) * 4 + 3]);
        Assert.Equal((byte)0, pixels[(15 * 32 + 24) * 4 + 3]);
        Assert.Equal((byte)0, pixels[(14 * 32 + 25) * 4 + 3]);
    }

    [Fact]
    public void SaveLayerAppliesDropShadowImageFilterWithNativeEffectTexture()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var layerPaint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateDropShadow(2f, 3f, 4f, 4f, SKColors.Black)
        };
        using var fill = new SKPaint { Color = SKColors.Red };

        var restoreCount = canvas.SaveLayer(layerPaint);
        canvas.DrawRect(new SKRect(10f, 10f, 40f, 40f), fill);

        canvas.RestoreToCount(restoreCount);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawTexture, command.Type);
        Assert.NotNull(command.Texture);
        Assert.Equal(new Rect(0f, 0f, 100f, 100f), command.Rect);
        Assert.Equal(1, context.RetainedResourceCount);
        Assert.Empty(GetOwnedLayerTextures(canvas));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SaveLayerExecutesMorphologyOnGpu(bool dilate)
    {
        using var surface = SKSurface.Create(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var layerPaint = new SKPaint
        {
            ImageFilter = dilate
                ? SKImageFilter.CreateDilate(1f, 1f)
                : SKImageFilter.CreateErode(1f, 1f)
        };
        using var fill = new SKPaint { Color = SKColors.Red };

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(2f, 2f, 6f, 6f), fill);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var edgeAlpha = pixels[(3 * 8 + 1) * 4 + 3];
        var centerAlpha = pixels[(3 * 8 + 3) * 4 + 3];

        Assert.Equal(dilate ? (byte)255 : (byte)0, edgeAlpha);
        Assert.Equal((byte)255, centerAlpha);
    }

    [Fact]
    public void SaveLayerBlendImageFilterCompositesInLinearRgbOnGpu()
    {
        using var surface = SKSurface.Create(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var floodShader = SKShader.CreateColor(new SKColor(0, 255, 0, 128));
        using var flood = SKImageFilter.CreateShader(floodShader, dither: false);
        using var blend = SKImageFilter.CreateBlendMode(SKBlendMode.Multiply, flood);
        using var layerPaint = new SKPaint { ImageFilter = blend };
        using var fill = new SKPaint { Color = new SKColor(0, 0, 255, 128) };

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 4f, 4f), fill);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();

        Assert.InRange(pixels[0], (byte)0, (byte)1);
        Assert.InRange(pixels[1], (byte)115, (byte)119);
        Assert.InRange(pixels[2], (byte)115, (byte)119);
        Assert.InRange(pixels[3], (byte)190, (byte)193);
    }

    [Fact]
    public void SaveLayerArithmeticImageFilterCompositesOnGpu()
    {
        using var surface = SKSurface.Create(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var backgroundShader = SKShader.CreateColor(SKColors.Blue);
        using var background = SKImageFilter.CreateShader(backgroundShader, dither: false);
        using var arithmetic = SKImageFilter.CreateArithmetic(
            k1: 0f,
            k2: 0.75f,
            k3: 0.25f,
            k4: 0f,
            enforcePremultipliedColor: true,
            background);
        using var layerPaint = new SKPaint { ImageFilter = arithmetic };
        using var foregroundPaint = new SKPaint { Color = SKColors.Red };

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 4f, 4f), foregroundPaint);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.InRange(pixels[0], (byte)190, (byte)192);
        Assert.Equal((byte)0, pixels[1]);
        Assert.InRange(pixels[2], (byte)63, (byte)65);
        Assert.Equal((byte)255, pixels[3]);
    }

    [Fact]
    public void SaveLayerDisplacementMapSamplesShiftedInputOnGpu()
    {
        using var surface = SKSurface.Create(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var displacementShader = SKShader.CreateColor(new SKColor(255, 128, 0, 255));
        using var displacementInput = SKImageFilter.CreateShader(displacementShader, dither: false);
        using var displacement = SKImageFilter.CreateDisplacementMapEffect(
            SKColorChannel.R,
            SKColorChannel.G,
            scale: 2f,
            displacementInput);
        using var layerPaint = new SKPaint { ImageFilter = displacement };
        using var red = new SKPaint { Color = SKColors.Red };
        using var blue = new SKPaint { Color = SKColors.Blue };

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 2f, 4f), red);
        surface.Canvas.DrawRect(new SKRect(2f, 0f, 4f, 4f), blue);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var shiftedPixel = (1 * 4 + 1) * 4;
        Assert.InRange(pixels[shiftedPixel], (byte)0, (byte)2);
        Assert.InRange(pixels[shiftedPixel + 2], (byte)253, (byte)255);
        Assert.Equal((byte)255, pixels[shiftedPixel + 3]);

        var outsidePixel = (1 * 4 + 3) * 4;
        Assert.InRange(pixels[outsidePixel + 3], (byte)0, (byte)2);
    }

    [Fact]
    public void SaveLayerDisplacementMapPreservesLinearPictureSamples()
    {
        using var linearColorSpace = SKColorSpace.CreateSrgbLinear();
        using var bitmap = new SKBitmap(new SKImageInfo(
            1,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            linearColorSpace));
        bitmap.SetPixel(0, 0, new SKColor(64, 128, 0, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 4f, 4f));
        using var imagePaint = new SKPaint { IsAntialias = false };
        recordingCanvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(0f, 0f, 4f, 4f),
            imagePaint);
        using var picture = recorder.EndRecording();
        using var displacementInput = SKImageFilter.CreatePicture(
            picture,
            new SKRect(0f, 0f, 4f, 4f));
        using var displacement = SKImageFilter.CreateDisplacementMapEffect(
            SKColorChannel.R,
            SKColorChannel.G,
            scale: 4f,
            displacementInput);
        using var layerPaint = new SKPaint { ImageFilter = displacement };
        using var red = new SKPaint { Color = SKColors.Red };
        using var blue = new SKPaint { Color = SKColors.Blue };
        using var surface = SKSurface.Create(new SKImageInfo(
            4,
            4,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 2f, 4f), red);
        surface.Canvas.DrawRect(new SKRect(2f, 0f, 4f, 4f), blue);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var displacedPixel = (1 * 4 + 2) * 4;
        Assert.InRange(pixels[displacedPixel], (byte)253, (byte)255);
        Assert.InRange(pixels[displacedPixel + 2], (byte)0, (byte)2);
        Assert.Equal((byte)255, pixels[displacedPixel + 3]);
    }

    [Fact]
    public void SaveLayerMatrixConvolutionUsesKernelOriginOnGpu()
    {
        using var surface = SKSurface.Create(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var convolution = SKImageFilter.CreateMatrixConvolution(
            new SKSizeI(3, 1),
            new[] { 0f, 0f, 1f },
            gain: 1f,
            bias: 0f,
            new SKPointI(1, 0),
            SKShaderTileMode.Decal,
            convolveAlpha: false);
        using var layerPaint = new SKPaint { ImageFilter = convolution };
        using var red = new SKPaint { Color = SKColors.Red };
        using var blue = new SKPaint { Color = SKColors.Blue };

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 2f, 4f), red);
        surface.Canvas.DrawRect(new SKRect(2f, 0f, 4f, 4f), blue);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var shiftedPixel = (1 * 4 + 1) * 4;
        Assert.Equal((byte)0, pixels[shiftedPixel]);
        Assert.Equal((byte)0, pixels[shiftedPixel + 1]);
        Assert.Equal((byte)255, pixels[shiftedPixel + 2]);
        Assert.Equal((byte)255, pixels[shiftedPixel + 3]);
    }

    [Fact]
    public void SaveLayerDistantDiffuseLightingUsesGpuHeightMap()
    {
        using var surface = SKSurface.Create(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var lighting = SKImageFilter.CreateDistantLitDiffuse(
            new SKPoint3(0f, 0f, 1f),
            SKColors.White,
            surfaceScale: 1f,
            kd: 0.5f);
        using var layerPaint = new SKPaint { ImageFilter = lighting };
        using var heightMap = new SKPaint { Color = SKColors.White };

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 4f, 4f), heightMap);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.InRange(pixels[0], (byte)127, (byte)128);
        Assert.InRange(pixels[1], (byte)127, (byte)128);
        Assert.InRange(pixels[2], (byte)127, (byte)128);
        Assert.Equal((byte)255, pixels[3]);
    }

    [Fact]
    public void SaveLayerDistantSpecularLightingWritesSpecularAlphaOnGpu()
    {
        using var surface = SKSurface.Create(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var lighting = SKImageFilter.CreateDistantLitSpecular(
            new SKPoint3(0f, 0f, 1f),
            SKColors.White,
            surfaceScale: 1f,
            ks: 0.25f,
            shininess: 8f);
        using var layerPaint = new SKPaint { ImageFilter = lighting };
        using var heightMap = new SKPaint { Color = SKColors.White };

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 4f, 4f), heightMap);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.InRange(pixels[0], (byte)63, (byte)64);
        Assert.InRange(pixels[1], (byte)63, (byte)64);
        Assert.InRange(pixels[2], (byte)63, (byte)64);
        Assert.InRange(pixels[3], (byte)63, (byte)64);
    }

    [Fact]
    public void SaveLayerPointLightingMapsLocalLightThroughLayerTransform()
    {
        using var surface = SKSurface.Create(new SKImageInfo(16, 8, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var lighting = SKImageFilter.CreatePointLitDiffuse(
            new SKPoint3(2f, 2f, 10f),
            SKColors.White,
            surfaceScale: 1f,
            kd: 1f);
        using var layerPaint = new SKPaint { ImageFilter = lighting };
        using var heightMap = new SKPaint { Color = SKColors.White };

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(8f, 2f);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 4f, 4f), heightMap);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var centerPixel = (4 * 16 + 10) * 4;
        Assert.InRange(pixels[centerPixel], (byte)250, (byte)255);
        Assert.Equal((byte)255, pixels[centerPixel + 3]);
    }

    [Fact]
    public void SaveLayerSpotLightingMapsLocalTargetThroughLayerTransform()
    {
        using var surface = SKSurface.Create(new SKImageInfo(16, 8, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var lighting = SKImageFilter.CreateSpotLitDiffuse(
            new SKPoint3(2f, 0f, 35f),
            new SKPoint3(2f, 4f, 0f),
            specularExponent: 1f,
            cutoffAngle: 90f,
            SKColors.White,
            surfaceScale: 1f,
            kd: 1f);
        using var layerPaint = new SKPaint { ImageFilter = lighting };
        using var heightMap = new SKPaint { Color = SKColors.White };

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Translate(8f, 2f);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 4f, 4f), heightMap);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var centerPixel = (4 * 16 + 10) * 4;
        Assert.InRange(pixels[centerPixel], (byte)240, (byte)255);
        Assert.Equal((byte)255, pixels[centerPixel + 3]);
    }

    [Fact]
    public void SaveLayerPerlinNoiseUsesSkiaSeedAndLocalTransformSemantics()
    {
        using var surface = SKSurface.Create(new SKImageInfo(12, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var sourcePaint = new SKPaint { Color = SKColors.White };
        var seeds = new[] { -0.8f, 1.5f };
        var offsets = new[] { 0f, 6f };

        surface.Canvas.Clear(SKColors.Transparent);
        for (var i = 0; i < offsets.Length; i++)
        {
            using var shader = SKShader.CreatePerlinNoiseTurbulence(
                0.1f,
                0.1f,
                2,
                seeds[i],
                SKPointI.Empty);
            using var filter = SKImageFilter.CreateShader(shader, dither: false, new SKRect(0f, 0f, 4f, 4f));
            using var layerPaint = new SKPaint { ImageFilter = filter };
            var outerCount = surface.Canvas.Save();
            surface.Canvas.Translate(offsets[i], 0f);
            var layerCount = surface.Canvas.SaveLayer(new SKRect(0f, 0f, 4f, 4f), layerPaint);
            surface.Canvas.DrawRect(new SKRect(0f, 0f, 4f, 4f), sourcePaint);
            surface.Canvas.RestoreToCount(layerCount);
            surface.Canvas.RestoreToCount(outerCount);
        }
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var hasVariation = false;
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var left = (y * 12 + x) * 4;
                var right = (y * 12 + x + 6) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    Assert.Equal(pixels[left + channel], pixels[right + channel]);
                }
                hasVariation |= pixels[left] != pixels[1 * 4];
            }
        }
        Assert.True(hasVariation);
    }

    [Fact]
    public void SaveLayerLuminanceMaskMultipliesSourceAlphaOnGpu()
    {
        using var surface = SKSurface.Create(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var layerPaint = new SKPaint { ColorFilter = SKColorFilter.CreateLumaColor() };
        using var fill = new SKPaint { Color = new SKColor(255, 255, 255, 128) };

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 4f, 4f), fill);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.InRange(pixels[3], (byte)126, (byte)130);
    }

    [Fact]
    public void SaveLayerColorTableTransformsStraightChannelsOnGpu()
    {
        var identity = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        var inverted = Enumerable.Range(0, 256).Select(static value => (byte)(255 - value)).ToArray();
        var halved = Enumerable.Range(0, 256).Select(static value => (byte)(value / 2)).ToArray();
        var opaque = Enumerable.Repeat((byte)255, 256).ToArray();
        using var surface = SKSurface.Create(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var colorFilter = SKColorFilter.CreateTable(opaque, inverted, halved, identity);
        using var imageFilter = SKImageFilter.CreateColorFilter(colorFilter);
        using var layerPaint = new SKPaint { ImageFilter = imageFilter };
        using var fill = new SKPaint { Color = new SKColor(64, 128, 192, 128) };

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 4f, 4f), fill);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.InRange(pixels[0], (byte)189, (byte)192);
        Assert.InRange(pixels[1], (byte)62, (byte)65);
        Assert.InRange(pixels[2], (byte)190, (byte)193);
        Assert.Equal((byte)255, pixels[3]);
    }

    [Fact]
    public void SaveLayerTransformsImageFilterCropIntoCanvasCoordinates()
    {
        var identity = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        using var surface = SKSurface.Create(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var colorFilter = SKColorFilter.CreateTable(identity, identity, identity, identity);
        using var imageFilter = SKImageFilter.CreateColorFilter(
            colorFilter,
            input: null,
            cropRect: new SKRect(0f, 20f, 128f, 60f));
        using var layerPaint = new SKPaint { ImageFilter = imageFilter };
        using var fill = new SKPaint { Color = SKColors.Red };

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(0.5f, 0.5f);
        var restoreCount = surface.Canvas.SaveLayer(new SKRect(0f, 0f, 128f, 128f), layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 20f, 128f, 60f), fill);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.Equal((byte)255, pixels[(12 * 64 + 20) * 4 + 3]);
        Assert.Equal((byte)0, pixels[(35 * 64 + 20) * 4 + 3]);
    }

    [Fact]
    public void SaveLayerTransformsPictureImageFilterInputIntoCanvasCoordinates()
    {
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 128f, 128f));
        using var red = new SKPaint { Color = SKColors.Red };
        recordingCanvas.DrawRect(new SKRect(0f, 20f, 128f, 60f), red);
        using var picture = recorder.EndRecording();
        using var pictureFilter = SKImageFilter.CreatePicture(picture, new SKRect(0f, 20f, 128f, 60f));
        var identity = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        using var colorFilter = SKColorFilter.CreateTable(identity, identity, identity, identity);
        using var imageFilter = SKImageFilter.CreateColorFilter(colorFilter, pictureFilter);
        using var layerPaint = new SKPaint { ImageFilter = imageFilter };
        using var transparent = new SKPaint { Color = SKColors.Transparent };
        using var surface = SKSurface.Create(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(0.5f, 0.5f);
        var restoreCount = surface.Canvas.SaveLayer(new SKRect(0f, 0f, 128f, 128f), layerPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 1f, 1f), transparent);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.Equal((byte)255, pixels[(12 * 64 + 20) * 4]);
        Assert.Equal((byte)255, pixels[(12 * 64 + 20) * 4 + 3]);
        Assert.Equal((byte)0, pixels[(35 * 64 + 20) * 4 + 3]);
    }

    [Fact]
    public void SaveLayerConvertsLinearPictureImageFilterOutputToSrgb()
    {
        using var linearColorSpace = SKColorSpace.CreateSrgbLinear();
        using var bitmap = new SKBitmap(new SKImageInfo(
            1,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            linearColorSpace));
        bitmap.SetPixel(0, 0, new SKColor(64, 0, 0, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 4f, 4f));
        using var imagePaint = new SKPaint { IsAntialias = false };
        recordingCanvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(0f, 0f, 4f, 4f),
            imagePaint);
        using var picture = recorder.EndRecording();
        using var pictureFilter = SKImageFilter.CreatePicture(picture, new SKRect(0f, 0f, 4f, 4f));
        using var layerPaint = new SKPaint { ImageFilter = pictureFilter };
        using var srgbColorSpace = SKColorSpace.CreateSrgb();
        using var surface = SKSurface.Create(new SKImageInfo(
            4,
            4,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            srgbColorSpace));

        surface.Canvas.Clear(SKColors.Transparent);
        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.RestoreToCount(restoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.InRange(pixels[0], (byte)136, (byte)138);
        Assert.Equal((byte)0, pixels[1]);
        Assert.Equal((byte)0, pixels[2]);
        Assert.Equal((byte)255, pixels[3]);
    }

    [Fact]
    public void DrawImageConvertsLinearSourceToSrgb()
    {
        using var linearColorSpace = SKColorSpace.CreateSrgbLinear();
        using var bitmap = new SKBitmap(new SKImageInfo(
            1,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            linearColorSpace));
        bitmap.SetPixel(0, 0, new SKColor(64, 0, 0, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var srgbColorSpace = SKColorSpace.CreateSrgb();
        using var surface = SKSurface.Create(new SKImageInfo(
            4,
            4,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            srgbColorSpace));
        using var paint = new SKPaint { IsAntialias = false };

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(0f, 0f, 4f, 4f),
            paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.InRange(pixels[0], (byte)136, (byte)138);
        Assert.Equal((byte)0, pixels[1]);
        Assert.Equal((byte)0, pixels[2]);
        Assert.Equal((byte)255, pixels[3]);
    }

    [Fact]
    public void ImageShaderConvertsLinearSourceToSrgb()
    {
        using var linearColorSpace = SKColorSpace.CreateSrgbLinear();
        using var bitmap = new SKBitmap(new SKImageInfo(
            1,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            linearColorSpace));
        bitmap.SetPixel(0, 0, new SKColor(64, 0, 0, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var paint = new SKPaint
        {
            IsAntialias = false,
            Shader = shader
        };
        using var srgbColorSpace = SKColorSpace.CreateSrgb();
        using var surface = SKSurface.Create(new SKImageInfo(
            4,
            4,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            srgbColorSpace));

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 4f, 4f), paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.InRange(pixels[0], (byte)136, (byte)138);
        Assert.Equal((byte)0, pixels[1]);
        Assert.Equal((byte)0, pixels[2]);
        Assert.Equal((byte)255, pixels[3]);
    }

    [Fact]
    public void PictureRecordingPreservesLinearImageSamples()
    {
        using var linearColorSpace = SKColorSpace.CreateSrgbLinear();
        using var bitmap = new SKBitmap(new SKImageInfo(
            1,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            linearColorSpace));
        bitmap.SetPixel(0, 0, new SKColor(64, 0, 0, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 4f, 4f));
        using var paint = new SKPaint { IsAntialias = false };
        canvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(0f, 0f, 4f, 4f),
            paint);
        using var picture = recorder.EndRecording();

        var command = Assert.Single(
            picture.Picture.Commands,
            static command => command.Type == RenderCommandType.DrawTexture);
        Assert.Equal((byte)64, command.Texture!.ReadPixels()[0]);
    }

    [Fact]
    public void PicturePlaybackConvertsLinearImagesToSrgb()
    {
        using var linearColorSpace = SKColorSpace.CreateSrgbLinear();
        using var bitmap = new SKBitmap(new SKImageInfo(
            1,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            linearColorSpace));
        bitmap.SetPixel(0, 0, new SKColor(64, 0, 0, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 4f, 4f));
        using var paint = new SKPaint { IsAntialias = false };
        recordingCanvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(0f, 0f, 4f, 4f),
            paint);
        using var picture = recorder.EndRecording();
        using var srgbColorSpace = SKColorSpace.CreateSrgb();
        using var surface = SKSurface.Create(new SKImageInfo(
            4,
            4,
            SKColorType.Rgba8888,
            SKAlphaType.Premul,
            srgbColorSpace));

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawPicture(picture);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.InRange(pixels[0], (byte)136, (byte)138);
        Assert.Equal((byte)0, pixels[1]);
        Assert.Equal((byte)0, pixels[2]);
        Assert.Equal((byte)255, pixels[3]);
    }

    [Fact]
    public void TransformedPicturePathKeepsGradientInLocalCoordinates()
    {
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 128f, 32f));
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0f, 0f),
            new SKPoint(128f, 0f),
            new[] { SKColors.Red, SKColors.Blue },
            colorPos: null,
            SKShaderTileMode.Clamp);
        using var gradient = new SKPaint { Shader = shader };
        using var path = new SKPath();
        path.AddRect(new SKRect(0f, 0f, 128f, 32f));
        recordingCanvas.DrawPath(path, gradient);
        using var picture = recorder.EndRecording();
        using var surface = SKSurface.Create(new SKImageInfo(64, 16, SKColorType.Rgba8888, SKAlphaType.Premul));

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(0.5f, 0.5f);
        surface.Canvas.DrawPicture(picture);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var offset = (8 * 64 + 32) * 4;
        Assert.InRange(pixels[offset], (byte)122, (byte)132);
        Assert.InRange(pixels[offset + 2], (byte)123, (byte)133);
        Assert.Equal((byte)255, pixels[offset + 3]);
    }

    [Fact]
    public void TransformedPicturePathStrokeKeepsGradientInLocalCoordinates()
    {
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 16f, 24f));
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(6.5f, 5f),
            new SKPoint(6.5f, 17f),
            new[] { new SKColor(255, 255, 41), SKColors.Black },
            colorPos: null,
            SKShaderTileMode.Clamp);
        using var gradient = new SKPaint
        {
            Shader = shader,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f
        };
        using var path = new SKPath();
        path.MoveTo(6.5f, 5f);
        path.LineTo(6.5f, 17f);
        recordingCanvas.DrawPath(path, gradient);
        using var picture = recorder.EndRecording();
        using var surface = SKSurface.Create(new SKImageInfo(64, 96, SKColorType.Rgba8888, SKAlphaType.Premul));

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(4f, 4f);
        surface.Canvas.DrawPicture(picture);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var offset = (22 * 64 + 26) * 4;
        Assert.True(pixels[offset] > 180);
        Assert.True(pixels[offset + 1] > 170);
        Assert.True(pixels[offset + 2] < 100);
        Assert.True(pixels[offset + 3] > 180);
    }

    [Fact]
    public void TransformedPicturePathKeepsDashIntervalsInLocalCoordinates()
    {
        using var recorder = new SKPictureRecorder();
        var recordingCanvas = recorder.BeginRecording(new SKRect(0f, 0f, 100f, 20f));
        using var dash = SKPathEffect.CreateDash(new[] { 10f, 20f }, 0f);
        using var paint = new SKPaint
        {
            Color = SKColors.Green,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4f,
            PathEffect = dash,
            IsAntialias = false
        };
        using var path = new SKPath();
        path.MoveTo(0f, 10f);
        path.LineTo(100f, 10f);
        recordingCanvas.DrawPath(path, paint);
        using var picture = recorder.EndRecording();
        using var surface = SKSurface.Create(new SKImageInfo(200, 40, SKColorType.Rgba8888, SKAlphaType.Premul));

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(2f, 2f);
        surface.Canvas.DrawPicture(picture);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.Equal((byte)255, pixels[(20 * 200 + 5) * 4 + 3]);
        Assert.Equal((byte)0, pixels[(20 * 200 + 30) * 4 + 3]);
        Assert.Equal((byte)255, pixels[(20 * 200 + 65) * 4 + 3]);
    }

    [Fact]
    public void ShearedPathStrokeTransformsItsLocalOutline()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var paint = new SKPaint
        {
            Color = SKColors.Green,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 80f,
            StrokeCap = SKStrokeCap.Butt,
            IsAntialias = false
        };
        using var path = new SKPath();
        path.MoveTo(60f, 140f);
        path.LineTo(140f, 60f);

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.SetMatrix(new SKMatrix(
            1f, -1f, 100f,
            0f, 1f, 0f,
            0f, 0f, 1f));
        surface.Canvas.DrawPath(path, paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.Equal((byte)0, pixels[(100 * 200 + 20) * 4 + 3]);
        Assert.Equal((byte)255, pixels[(120 * 200 + 20) * 4 + 3]);
        Assert.Equal((byte)255, pixels[(80 * 200 + 179) * 4 + 3]);
        Assert.Equal((byte)0, pixels[(40 * 200 + 180) * 4 + 3]);
    }

    [Fact]
    public void DrawTextWithStrokePaintRendersGlyphOutlines()
    {
        using var surface = SKSurface.Create(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var font = new SKFont(SKTypeface.Default, 32f);
        using var paint = new SKPaint
        {
            Color = SKColors.Blue,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f
        };

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawText("A", 8f, 40f, font, paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var foundBlueStroke = false;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 2] > pixels[offset] && pixels[offset + 3] > 0)
            {
                foundBlueStroke = true;
                break;
            }
        }

        Assert.True(foundBlueStroke);
    }

    [Fact]
    public void DrawTextFillRequestsVectorGlyphRenderingForSkiaFidelity()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 64f, 64f);
        using var font = new SKFont(SKTypeface.Default, 24f);
        using var paint = new SKPaint { Color = SKColors.Black };

        canvas.DrawText("A", 4f, 32f, font, paint);

        var command = Assert.Single(context.Commands, static command => command.Type == RenderCommandType.DrawGlyphRun);
        Assert.True(command.UseVectorGlyphRendering);
    }

    [Fact]
    public void ImageShaderAppliesPaintColorFilterOnGpuBeforeTiling()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.SetPixel(0, 0, SKColors.Red);
        using var shader = SKShader.CreateBitmap(bitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        using var colorFilter = SKColorFilter.CreateColorMatrix(new[]
        {
            0f, 0f, 1f, 0f, 0f,
            0f, 1f, 0f, 0f, 0f,
            1f, 0f, 0f, 0f, 0f,
            0f, 0f, 0f, 1f, 0f
        });
        using var paint = new SKPaint
        {
            Shader = shader,
            ColorFilter = colorFilter,
            IsAntialias = false
        };
        using var surface = SKSurface.Create(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul));

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 8f, 8f), paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var center = (4 * 8 + 4) * 4;
        Assert.Equal((byte)0, pixels[center]);
        Assert.Equal((byte)0, pixels[center + 1]);
        Assert.Equal((byte)255, pixels[center + 2]);
        Assert.Equal((byte)255, pixels[center + 3]);
    }

    [Fact]
    public void SaveLayerDstInHonorsTransformedMaskClip()
    {
        using var surface = SKSurface.Create(new SKImageInfo(20, 20, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var targetPaint = new SKPaint();
        using var maskPaint = new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            ColorFilter = SKColorFilter.CreateLumaColor()
        };
        using var red = new SKPaint { Color = SKColors.Red };
        using var white = new SKPaint { Color = SKColors.White };

        surface.Canvas.Clear(SKColors.Transparent);
        var targetRestoreCount = surface.Canvas.SaveLayer(targetPaint);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, 20f, 20f), red);
        var maskRestoreCount = surface.Canvas.SaveLayer(maskPaint);
        surface.Canvas.Save();
        surface.Canvas.SetMatrix(SKMatrix.CreateScale(20f, 20f));
        surface.Canvas.ClipRect(new SKRect(0.24999999f, 0f, 0.74999994f, 0.99999994f));
        using var maskPath = new SKPath();
        maskPath.AddRect(new SKRect(0f, 0f, 1f, 1f));
        surface.Canvas.DrawPath(maskPath, white);
        surface.Canvas.Restore();
        surface.Canvas.RestoreToCount(maskRestoreCount);
        surface.Canvas.RestoreToCount(targetRestoreCount);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.True(pixels[(10 * 20 + 4) * 4 + 3] < 20);
        Assert.True(pixels[(10 * 20 + 10) * 4 + 3] > 240);
        Assert.True(pixels[(10 * 20 + 16) * 4 + 3] < 20);
    }

    [Fact]
    public void SurfaceFlushReleasesSaveLayerTexturesAfterConsumingCommands()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var layerPaint = new SKPaint();
        using var fill = new SKPaint { Color = SKColors.Red };

        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(2f, 2f, 20f, 20f), fill);
        surface.Canvas.RestoreToCount(restoreCount);

        var drawingContext = GetSurfaceDrawingContext(surface);
        var layerTexture = Assert.IsType<GpuTexture>(Assert.Single(drawingContext.Commands).Texture);
        Assert.Equal(1, drawingContext.RetainedResourceCount);
        Assert.Empty(GetOwnedLayerTextures(surface.Canvas));

        surface.Flush();

        Assert.Empty(drawingContext.Commands);
        Assert.Equal(0, drawingContext.RetainedResourceCount);
        Assert.True(layerTexture.IsDisposed);
    }

    [Fact]
    public void SurfaceFlushReleasesRecordedResourcesWhenRenderFails()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var layerPaint = new SKPaint();
        using var fill = new SKPaint { Color = SKColors.Red };
        using var bitmap = new SKBitmap(1, 1);
        using var image = SKImage.FromBitmap(bitmap);

        surface.Canvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(1f, 1f, 8f, 8f),
            null!);

        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(2f, 2f, 20f, 20f), fill);
        surface.Canvas.RestoreToCount(restoreCount);

        var drawingContext = GetSurfaceDrawingContext(surface);
        GpuTexture? retainedImageTexture = null;
        GpuTexture? layerTexture = null;
        foreach (var command in drawingContext.Commands)
        {
            if (command.Texture != null)
            {
                if (retainedImageTexture == null)
                {
                    retainedImageTexture = command.Texture;
                }
                else
                {
                    layerTexture = command.Texture;
                    break;
                }
            }
        }

        Assert.NotNull(retainedImageTexture);
        Assert.NotNull(layerTexture);
        Assert.Equal(2, drawingContext.RetainedResourceCount);
        Assert.Empty(GetOwnedLayerTextures(surface.Canvas));

        var compositor = GetSurfaceCompositor(surface);
        compositor.RegisterExtension(9901, new ThrowingCompileExtension());
        drawingContext.DrawExtension(9901);

        var retainedTextureDisposed = false;
        var layerTextureDisposed = false;
        void OnTextureDisposed(ulong id)
        {
            if (id == retainedImageTexture.Id)
            {
                retainedTextureDisposed = true;
            }

            if (id == layerTexture!.Id)
            {
                layerTextureDisposed = true;
            }
        }

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => surface.Flush());
            Assert.Contains("Synthetic SKSurface flush failure", exception.Message, StringComparison.Ordinal);

            Assert.Empty(drawingContext.Commands);
            Assert.Equal(0, drawingContext.RetainedResourceCount);
            Assert.True(retainedTextureDisposed);
            Assert.True(layerTextureDisposed);
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
        }
    }

    [Fact]
    public void RestoreLayerDisposesOffscreenTextureWhenRenderFails()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var layerPaint = new SKPaint();
        using var fill = new SKPaint { Color = SKColors.Red };

        var compositor = GetCanvasCompositor(surface);
        compositor.RegisterExtension(9902, new ThrowingCompileExtension());

        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(2f, 2f, 20f, 20f), fill);
        var layerContext = GetCurrentDrawingContext(surface.Canvas);
        layerContext.DrawExtension(9902);

        var disposedTextureCount = 0;
        void OnTextureDisposed(ulong _) => disposedTextureCount++;

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => surface.Canvas.RestoreToCount(restoreCount));
            Assert.Contains("Synthetic SKSurface flush failure", exception.Message, StringComparison.Ordinal);

            Assert.True(disposedTextureCount > 0);
            Assert.Empty(GetOwnedLayerTextures(surface.Canvas));
            Assert.Empty(layerContext.Commands);
            Assert.Equal(0, layerContext.RetainedResourceCount);
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
            ResetCanvasCompositor(surface);
        }
    }

    [Fact]
    public void SurfaceDisposeReleasesOwnedTextureWhenFlushFails()
    {
        var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        var texture = GetSurfaceTexture(surface);
        var compositor = GetSurfaceCompositor(surface);
        var drawingContext = GetSurfaceDrawingContext(surface);
        compositor.RegisterExtension(9907, new ThrowingCompileExtension());
        drawingContext.DrawExtension(9907);

        var textureDisposed = false;
        void OnTextureDisposed(ulong id)
        {
            if (id == texture.Id)
            {
                textureDisposed = true;
            }
        }

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => surface.Dispose());
            Assert.Contains("Synthetic SKSurface flush failure", exception.Message, StringComparison.Ordinal);

            Assert.True(textureDisposed);
            Assert.True(texture.IsDisposed);
            Assert.Empty(drawingContext.Commands);
            Assert.Equal(0, drawingContext.RetainedResourceCount);
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
            ResetSurfaceCompositor(surface);
        }
    }

    [Fact]
    public void RestoreDropShadowLayerDisposesFilteredTextureWhenRenderFails()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var current = WgpuContext.PushCurrent(context);
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var layerPaint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateDropShadow(2f, 3f, 4f, 4f, SKColors.Black)
        };
        using var fill = new SKPaint { Color = SKColors.Red };

        DisposeCanvasCompositorCompute(surface);

        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawRect(new SKRect(2f, 2f, 20f, 20f), fill);

        var disposedTextureCount = 0;
        void OnTextureDisposed(ulong _) => disposedTextureCount++;

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            Assert.Throws<ObjectDisposedException>(() => surface.Canvas.RestoreToCount(restoreCount));

            Assert.True(disposedTextureCount > 0);
            Assert.Empty(GetSurfaceDrawingContext(surface).Commands);
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
            ResetCanvasCompositor(surface);
        }
    }

    [Fact]
    public void CanvasDisposeKeepsSaveLayerTextureAliveForDeferredDrawingContext()
    {
        var context = new DrawingContext();
        var canvas = new SKCanvas(context, 32f, 32f);
        using var layerPaint = new SKPaint();
        using var fill = new SKPaint { Color = SKColors.Red };

        var restoreCount = canvas.SaveLayer(layerPaint);
        canvas.DrawRect(new SKRect(2f, 2f, 20f, 20f), fill);
        canvas.RestoreToCount(restoreCount);

        var command = Assert.Single(context.Commands);
        var layerTexture = Assert.IsType<GpuTexture>(command.Texture);
        Assert.Equal(1, context.RetainedResourceCount);
        Assert.Empty(GetOwnedLayerTextures(canvas));

        canvas.Dispose();

        Assert.False(layerTexture.IsDisposed);

        context.Clear();

        Assert.True(layerTexture.IsDisposed);
        Assert.Equal(0, context.RetainedResourceCount);
    }

    [Fact]
    public void RestoreLayerReleasesRetainedImageTexturesAfterOffscreenRender()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var layerPaint = new SKPaint();
        using var bitmap = new SKBitmap(1, 1);
        using var image = SKImage.FromBitmap(bitmap);

        var restoreCount = surface.Canvas.SaveLayer(layerPaint);
        surface.Canvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(2f, 2f, 12f, 12f),
            null!);

        var layerContext = GetCurrentDrawingContext(surface.Canvas);
        var retainedTexture = GetDrawTextureCommand(layerContext.Commands).Texture!;
        Assert.Equal(1, layerContext.RetainedResourceCount);

        var retainedTextureDisposed = false;
        void OnTextureDisposed(ulong id)
        {
            if (id == retainedTexture.Id)
            {
                retainedTextureDisposed = true;
            }
        }

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            surface.Canvas.RestoreToCount(restoreCount);

            Assert.True(retainedTextureDisposed);
            Assert.Empty(layerContext.Commands);
            Assert.Equal(0, layerContext.RetainedResourceCount);
            Assert.Equal(1, GetSurfaceDrawingContext(surface).RetainedResourceCount);
            Assert.Empty(GetOwnedLayerTextures(surface.Canvas));
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
        }
    }

    [Fact]
    public void RestoreLayerClearsRetainedImageTexturesWhenLayerBoundsAreSkipped()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var layerPaint = new SKPaint();
        using var bitmap = new SKBitmap(1, 1);
        using var image = SKImage.FromBitmap(bitmap);

        var restoreCount = surface.Canvas.SaveLayer(new SKRect(0f, 0f, 0f, 0f), layerPaint);
        surface.Canvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(2f, 2f, 12f, 12f),
            null!);

        var layerContext = GetCurrentDrawingContext(surface.Canvas);
        var retainedTexture = GetDrawTextureCommand(layerContext.Commands).Texture!;
        Assert.Equal(1, layerContext.RetainedResourceCount);

        var retainedTextureDisposed = false;
        void OnTextureDisposed(ulong id)
        {
            if (id == retainedTexture.Id)
            {
                retainedTextureDisposed = true;
            }
        }

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            surface.Canvas.RestoreToCount(restoreCount);

            Assert.True(retainedTextureDisposed);
            Assert.Empty(layerContext.Commands);
            Assert.Equal(0, layerContext.RetainedResourceCount);
            Assert.Empty(GetOwnedLayerTextures(surface.Canvas));
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
        }
    }

    [Fact]
    public void ClipRectRecordsCurrentCanvasMatrix()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);

        canvas.Translate(50f, 10f);
        canvas.ClipRect(new SKRect(2f, 4f, 22f, 14f));

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.PushClip, command.Type);
        Assert.Equal(new Rect(2f, 4f, 20f, 10f), command.Rect);
        AssertMatrixNear(Matrix4x4.CreateTranslation(50f, 10f, 0f), command.Transform);
    }

    [Fact]
    public void CreateTwoPointConicalGradientCreatesNativeBrush()
    {
        using var shader = SKShader.CreateTwoPointConicalGradient(
            new SKPoint(1f, 2f),
            4f,
            new SKPoint(10f, 20f),
            8f,
            new[] { SKColors.Red, SKColors.Blue },
            new[] { 0.25f, 0.75f },
            SKShaderTileMode.Repeat);

        var brush = Assert.IsType<TwoPointConicalGradientBrush>(shader.ToBrush());
        Assert.Equal(new Vector2(1f, 2f), brush.StartCenter);
        Assert.Equal(4f, brush.StartRadius);
        Assert.Equal(new Vector2(10f, 20f), brush.EndCenter);
        Assert.Equal(8f, brush.EndRadius);
        Assert.Equal(GradientSpreadMethod.Repeat, brush.SpreadMethod);
        Assert.Equal(2, brush.Stops.Length);
        Assert.Equal(0.25f, brush.Stops[0].Offset);
        Assert.Equal(0.75f, brush.Stops[1].Offset);
    }

    [Fact]
    public void ComposedConicalGradientUsesSingleBrushWithOutsideColor()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var destination = SKShader.CreateColor(SKColors.Blue);
        using var source = SKShader.CreateTwoPointConicalGradient(
            new SKPoint(10f, 10f),
            0f,
            new SKPoint(50f, 50f),
            40f,
            new[] { new SKColor(255, 0, 0, 128), new SKColor(0, 255, 0, 128) },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        using var composed = SKShader.CreateCompose(destination, source);
        using var paint = new SKPaint
        {
            Shader = composed,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 10f
        };

        canvas.DrawLine(10f, 50f, 90f, 50f, paint);

        var command = Assert.Single(context.Commands);
        var brush = Assert.IsType<TwoPointConicalGradientBrush>(command.Brush);
        Assert.Null(command.Pen);
        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), brush.OutsideColor);
        AssertNear(128f / 255f, brush.Stops[0].Color.X);
        AssertNear(1f - 128f / 255f, brush.Stops[0].Color.Z);
        AssertNear(1f, brush.Stops[0].Color.W);
    }

    [Fact]
    public void DrawRectAppliesPaintBlendMode()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Multiply };

        canvas.DrawRect(new SKRect(10f, 20f, 40f, 60f), paint);

        Assert.Collection(
            context.Commands,
            push =>
            {
                Assert.Equal(RenderCommandType.PushBlendMode, push.Type);
                Assert.Equal((int)GpuBlendMode.Multiply, push.IntParam);
            },
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawRect, draw.Type);
                Assert.Equal(new Rect(10f, 20f, 30f, 40f), draw.Rect);
            },
            pop => Assert.Equal(RenderCommandType.PopBlendMode, pop.Type));
    }

    [Fact]
    public void DrawRectKeepsStrokeWidthInLocalCoordinatesWithCanvasMatrix()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f
        };

        canvas.Scale(3f, 3f);
        canvas.DrawRect(new SKRect(10f, 20f, 40f, 60f), paint);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawRect, command.Type);
        Assert.NotNull(command.Pen);
        AssertNear(2f, command.Pen!.Thickness);
        Assert.True(command.IsPenThicknessLocal);
        AssertMatrixNear(Matrix4x4.CreateScale(3f, 3f, 1f), command.Transform);
    }

    [Fact]
    public void DrawPathScalesStrokeWidthWithSetMatrix()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var path = new SKPath();
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f
        };
        var matrix = new SKMatrix
        {
            ScaleX = 2f,
            ScaleY = 5f,
            Persp2 = 1f
        };

        path.MoveTo(0f, 0f);
        path.LineTo(10f, 0f);
        canvas.SetMatrix(matrix);
        canvas.DrawPath(path, paint);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.NotNull(command.Pen);
        AssertNear(7.5f, command.Pen!.Thickness);
        AssertMatrixNear(matrix.ToMatrix4x4(), command.Transform);
    }

    [Fact]
    public void DrawRectMapsZeroStrokeWidthToRenderableHairline()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f
        };

        canvas.Scale(4f, 4f);
        canvas.DrawRect(new SKRect(10f, 20f, 40f, 60f), paint);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawRect, command.Type);
        Assert.NotNull(command.Pen);
        AssertNear(0.25f, command.Pen!.Thickness);
        Assert.True(command.IsPenThicknessLocal);
        AssertMatrixNear(Matrix4x4.CreateScale(4f, 4f, 1f), command.Transform);
    }

    [Fact]
    public void ClipPathRecordsCurrentCanvasMatrix()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var path = new SKPath();
        path.AddRect(new SKRect(0f, 0f, 20f, 20f));
        var matrix = new SKMatrix
        {
            ScaleX = 1.25f,
            SkewX = 0.5f,
            TransX = 7f,
            SkewY = 0.25f,
            ScaleY = 2f,
            TransY = 11f,
            Persp2 = 1f
        };

        canvas.SetMatrix(matrix);
        canvas.ClipPath(path);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.PushGeometryClip, command.Type);
        Assert.Same(path.Geometry, command.Path);
        AssertMatrixNear(matrix.ToMatrix4x4(), command.Transform);
    }

    [Fact]
    public void ClipRectDifferenceRecordsNativeDifferenceGeometry()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 80f);

        canvas.Translate(50f, 10f);
        canvas.ClipRect(new SKRect(2f, 4f, 22f, 14f), SKClipOperation.Difference);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.PushGeometryClip, command.Type);
        AssertMatrixNear(Matrix4x4.Identity, command.Transform);

        var clip = command.Path!;
        Assert.True(clip.IsCombined);
        Assert.Equal((int)SKPathOp.Difference, clip.Op);
        AssertPathBounds(clip.PathA!, 0f, 0f, 100f, 80f);
        AssertPathBounds(clip.PathB!, 52f, 14f, 72f, 24f);
    }

    [Fact]
    public void ClipPathDifferenceRecordsNativeDifferenceGeometry()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 80f);
        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRect(new SKRect(0f, 0f, 20f, 20f));

        canvas.Translate(5f, 7f);
        canvas.ClipPath(path, SKClipOperation.Difference);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.PushGeometryClip, command.Type);
        AssertMatrixNear(Matrix4x4.Identity, command.Transform);

        var clip = command.Path!;
        Assert.True(clip.IsCombined);
        Assert.Equal((int)SKPathOp.Difference, clip.Op);
        AssertPathBounds(clip.PathA!, 0f, 0f, 100f, 80f);
        AssertPathBounds(clip.PathB!, 5f, 7f, 25f, 27f);
        Assert.Equal(FillRule.EvenOdd, clip.PathB!.FillRule);
    }

    [Fact]
    public void ClipPathInverseFillRecordsNativeDifferenceGeometry()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 80f);
        using var path = new SKPath { FillType = SKPathFillType.InverseEvenOdd };
        path.AddRect(new SKRect(10f, 10f, 30f, 30f));

        canvas.ClipPath(path);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.PushGeometryClip, command.Type);

        var clip = command.Path!;
        Assert.True(clip.IsCombined);
        Assert.Equal((int)SKPathOp.Difference, clip.Op);
        AssertPathBounds(clip.PathA!, 0f, 0f, 100f, 80f);
        AssertPathBounds(clip.PathB!, 10f, 10f, 30f, 30f);
        Assert.Equal(FillRule.EvenOdd, clip.PathB!.FillRule);
    }

    [Fact]
    public void DrawRoundRectRecordsBothUniformRadii()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var paint = new SKPaint();

        canvas.DrawRoundRect(new SKRect(1f, 2f, 21f, 12f), rx: 3f, ry: 5f, paint);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawRoundedRect, command.Type);
        Assert.Equal(new Rect(1f, 2f, 20f, 10f), command.Rect);
        Assert.Equal(3f, command.RadiusX);
        Assert.Equal(5f, command.RadiusY);
    }

    [Fact]
    public void DrawRoundRectWithPerCornerRadiiRecordsNativePath()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var roundRect = new SKRoundRect();
        using var paint = new SKPaint();
        roundRect.SetRectRadii(
            new SKRect(0f, 0f, 20f, 10f),
            new[]
            {
                new SKPoint(2f, 3f),
                new SKPoint(4f, 3f),
                new SKPoint(4f, 5f),
                new SKPoint(2f, 5f)
            });

        canvas.DrawRoundRect(roundRect, paint);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.NotNull(command.Path);
        var figure = Assert.Single(command.Path!.Figures);
        var arcs = figure.Segments.OfType<ArcSegment>().ToArray();
        Assert.Equal(4, arcs.Length);
        Assert.Equal(new Vector2(4f, 3f), arcs[0].Size);
        Assert.Equal(new Vector2(4f, 5f), arcs[1].Size);
        Assert.Equal(new Vector2(2f, 5f), arcs[2].Size);
        Assert.Equal(new Vector2(2f, 3f), arcs[3].Size);
    }

    [Fact]
    public void RoundRectSetRectClearsPreviousCornerRadii()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var roundRect = new SKRoundRect();
        using var paint = new SKPaint();
        roundRect.SetRectRadii(
            new SKRect(0f, 0f, 20f, 10f),
            new[]
            {
                new SKPoint(2f, 3f),
                new SKPoint(4f, 3f),
                new SKPoint(4f, 5f),
                new SKPoint(2f, 5f)
            });

        roundRect.SetRect(new SKRect(1f, 2f, 21f, 12f));
        canvas.DrawRoundRect(roundRect, paint);

        Assert.All(roundRect.CornerRadii, radius => Assert.Equal(default, radius));
        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawRoundedRect, command.Type);
        Assert.Equal(new Rect(1f, 2f, 20f, 10f), command.Rect);
        Assert.Equal(0f, command.RadiusX);
        Assert.Equal(0f, command.RadiusY);
    }

    [Fact]
    public void DrawPathInverseFillRecordsNativeDifferenceGeometry()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 80f);
        using var paint = new SKPaint { Style = SKPaintStyle.Fill };
        using var path = new SKPath { FillType = SKPathFillType.InverseEvenOdd };
        path.AddRect(new SKRect(10f, 10f, 30f, 30f));

        canvas.Translate(5f, 7f);
        canvas.DrawPath(path, paint);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.NotNull(command.Brush);
        Assert.Null(command.Pen);
        AssertMatrixNear(Matrix4x4.Identity, command.Transform);

        var drawnPath = command.Path!;
        Assert.True(drawnPath.IsCombined);
        Assert.Equal((int)SKPathOp.Difference, drawnPath.Op);
        AssertPathBounds(drawnPath.PathA!, 0f, 0f, 100f, 80f);
        AssertPathBounds(drawnPath.PathB!, 15f, 17f, 35f, 37f);
        Assert.Equal(FillRule.EvenOdd, drawnPath.PathB!.FillRule);
    }

    [Fact]
    public void DrawImagePushesPaintAlphaOpacity()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        using var bitmap = new SKBitmap(4, 4);
        using var image = SKImage.FromBitmap(bitmap);
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, 128) };

        canvas.DrawImage(
            image,
            new SKRect(1f, 2f, 3f, 4f),
            new SKRect(10f, 20f, 30f, 40f),
            paint);

        Assert.Collection(
            context.Commands,
            push =>
            {
                Assert.Equal(RenderCommandType.PushOpacity, push.Type);
                AssertNear(128f / 255f, push.FontSize);
            },
            pushClip => Assert.Equal(RenderCommandType.PushGeometryClip, pushClip.Type),
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawTexture, draw.Type);
                Assert.NotSame(image.Texture, draw.Texture);
                AssertNear(10f, draw.Rect.X);
                AssertNear(20f, draw.Rect.Y);
                AssertNear(20.5f, draw.Rect.Width);
                AssertNear(20.5f, draw.Rect.Height);
                AssertNear(1f, draw.SrcRect.X);
                AssertNear(2f, draw.SrcRect.Y);
                AssertNear(2.05f, draw.SrcRect.Width);
                AssertNear(2.05f, draw.SrcRect.Height);
            },
            popClip => Assert.Equal(RenderCommandType.PopGeometryClip, popClip.Type),
            pop => Assert.Equal(RenderCommandType.PopOpacity, pop.Type));
        Assert.Equal(1, context.RetainedResourceCount);
    }

    [Fact]
    public void DrawImageRetainsSourceTextureForDeferredFlush()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 16f, 16f);
        using var bitmap = new SKBitmap(1, 1);
        var image = SKImage.FromBitmap(bitmap);

        canvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(0f, 0f, 1f, 1f),
            null!);

        var command = GetDrawTextureCommand(context.Commands);
        var retainedTexture = command.Texture!;
        Assert.NotSame(image.Texture, retainedTexture);
        Assert.Equal(1, context.RetainedResourceCount);

        var retainedTextureDisposed = false;
        void OnTextureDisposed(ulong id)
        {
            if (id == retainedTexture.Id)
            {
                retainedTextureDisposed = true;
            }
        }

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            image.Dispose();

            Assert.False(retainedTextureDisposed);

            context.Clear();

            Assert.True(retainedTextureDisposed);
            Assert.Equal(0, context.RetainedResourceCount);
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
        }
    }

    [Fact]
    public void GpuPictureKeepsRetainedImageTextureAfterRecorderReuse()
    {
        var recorder = new GpuPictureRecorder();
        var context = recorder.BeginRecording(new Rect(0f, 0f, 16f, 16f));
        using var canvas = new SKCanvas(context, 16f, 16f);
        using var bitmap = new SKBitmap(1, 1);
        using var image = SKImage.FromBitmap(bitmap);

        canvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(0f, 0f, 1f, 1f),
            null!);

        using var picture = recorder.EndRecording();
        var command = GetDrawTextureCommand(picture.Commands);
        var retainedTexture = command.Texture!;
        Assert.Equal(0, context.RetainedResourceCount);
        Assert.Equal(1, picture.RetainedResourceCount);

        var retainedTextureDisposed = false;
        void OnTextureDisposed(ulong id)
        {
            if (id == retainedTexture.Id)
            {
                retainedTextureDisposed = true;
            }
        }

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            Assert.False(retainedTextureDisposed);
            Assert.Equal(0, context.RetainedResourceCount);

            picture.Dispose();

            Assert.True(retainedTextureDisposed);
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
        }
    }

    [Fact]
    public void AppendKeepsRetainedImageTextureAfterSourceContextClears()
    {
        var source = new DrawingContext();
        var target = new DrawingContext();
        using var canvas = new SKCanvas(source, 16f, 16f);
        using var bitmap = new SKBitmap(1, 1);
        using var image = SKImage.FromBitmap(bitmap);

        canvas.DrawImage(
            image,
            new SKRect(0f, 0f, 1f, 1f),
            new SKRect(0f, 0f, 1f, 1f),
            null!);
        target.Append(source);

        var command = GetDrawTextureCommand(target.Commands);
        var retainedTexture = command.Texture!;
        Assert.Equal(1, source.RetainedResourceCount);
        Assert.Equal(1, target.RetainedResourceCount);

        var retainedTextureDisposed = false;
        void OnTextureDisposed(ulong id)
        {
            if (id == retainedTexture.Id)
            {
                retainedTextureDisposed = true;
            }
        }

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            source.Clear();

            Assert.False(retainedTextureDisposed);
            Assert.Equal(0, source.RetainedResourceCount);
            Assert.Equal(1, target.RetainedResourceCount);

            target.Clear();

            Assert.True(retainedTextureDisposed);
            Assert.Equal(0, target.RetainedResourceCount);
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
        }
    }

    [Fact]
    public void DrawImageRejectsSourceTexturesFromDifferentContext()
    {
        using var sourceContext = new WgpuContext();
        sourceContext.Initialize(null);
        using var targetContext = new WgpuContext();
        targetContext.Initialize(null);
        using var sourceTexture = new GpuTexture(
            sourceContext,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "Cross-context SKImage source");
        using var image = SKImage.FromTexture(sourceTexture);
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 16f, 16f, targetContext);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            canvas.DrawImage(
                image,
                new SKRect(0f, 0f, 1f, 1f),
                new SKRect(0f, 0f, 1f, 1f),
                null!));

        Assert.Contains("different WebGPU context", exception.Message, StringComparison.Ordinal);
        Assert.Empty(context.Commands);
        Assert.Equal(0, context.RetainedResourceCount);
    }

    [Fact]
    public void DrawImageUsesCurrentContextForDeferredDrawingContext()
    {
        using var sourceContext = new WgpuContext();
        sourceContext.Initialize(null);
        using var targetContext = new WgpuContext();
        targetContext.Initialize(null);
        using var sourceTexture = new GpuTexture(
            sourceContext,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "Cross-context SKImage current target source");
        using var image = SKImage.FromTexture(sourceTexture);
        var context = new DrawingContext();
        var previous = WgpuContext.Current;
        WgpuContext.Current = targetContext;

        try
        {
            using var canvas = new SKCanvas(context, 16f, 16f);
            var exception = Assert.Throws<InvalidOperationException>(() =>
                canvas.DrawImage(
                    image,
                    new SKRect(0f, 0f, 1f, 1f),
                    new SKRect(0f, 0f, 1f, 1f),
                    null!));

            Assert.Contains("different WebGPU context", exception.Message, StringComparison.Ordinal);
            Assert.Empty(context.Commands);
            Assert.Equal(0, context.RetainedResourceCount);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void DrawImageDoesNotLeakBlendModeWhenImageRetentionFails()
    {
        using var sourceContext = new WgpuContext();
        sourceContext.Initialize(null);
        using var targetContext = new WgpuContext();
        targetContext.Initialize(null);
        using var sourceTexture = new GpuTexture(
            sourceContext,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "Cross-context SKImage blend failure source");
        using var image = SKImage.FromTexture(sourceTexture);
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 16f, 16f, targetContext);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Multiply };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            canvas.DrawImage(
                image,
                new SKRect(0f, 0f, 1f, 1f),
                new SKRect(0f, 0f, 1f, 1f),
                paint));

        Assert.Contains("different WebGPU context", exception.Message, StringComparison.Ordinal);
        Assert.Empty(context.Commands);
        Assert.Equal(0, context.RetainedResourceCount);
    }

    [Fact]
    public void TranslateAppliesFullLinearMatrix()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        canvas.SetMatrix(new SKMatrix
        {
            ScaleX = 2f,
            SkewX = 3f,
            SkewY = 5f,
            ScaleY = 7f,
            TransX = 11f,
            TransY = 13f,
            Persp2 = 1f
        });

        canvas.Translate(17f, 19f);

        var matrix = canvas.TotalMatrix;
        AssertNear(11f + 17f * 2f + 19f * 3f, matrix.TransX);
        AssertNear(13f + 17f * 5f + 19f * 7f, matrix.TransY);
    }

    [Fact]
    public void ScaleAppliesToSkewedMatrixTerms()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 100f);
        canvas.SetMatrix(new SKMatrix
        {
            ScaleX = 2f,
            SkewX = 3f,
            SkewY = 5f,
            ScaleY = 7f,
            TransX = 11f,
            TransY = 13f,
            Persp2 = 1f
        });

        canvas.Scale(17f, 19f);

        var matrix = canvas.TotalMatrix;
        AssertNear(34f, matrix.ScaleX);
        AssertNear(57f, matrix.SkewX);
        AssertNear(85f, matrix.SkewY);
        AssertNear(133f, matrix.ScaleY);
        AssertNear(11f, matrix.TransX);
        AssertNear(13f, matrix.TransY);
    }

    [Fact]
    public void SkPaintToPenPreservesCapsJoinsAndMiter()
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Bevel,
            StrokeMiter = 2.5f,
            StrokeWidth = 3f
        };

        var pen = Assert.IsType<Pen>(paint.ToPen());

        Assert.Equal(PenLineCap.Round, pen.StartLineCap);
        Assert.Equal(PenLineCap.Round, pen.EndLineCap);
        Assert.Equal(PenLineCap.Round, pen.DashCap);
        Assert.Equal(PenLineJoin.Bevel, pen.LineJoin);
        AssertNear(2.5f, pen.MiterLimit);
        AssertNear(3f, pen.Thickness);
    }

    [Fact]
    public void SkPaintToPenMapsDashPathEffectToVectorPen()
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            PathEffect = SKPathEffect.CreateDash(new[] { 6f, 2f, 4f, 8f }, 3f)
        };

        var pen = Assert.IsType<Pen>(paint.ToPen());

        Assert.Equal(new[] { 3.0, 1.0, 2.0, 4.0 }, pen.DashArray);
        AssertNear(1.5f, (float)pen.DashOffset);
    }

    [Fact]
    public void SkPaintGetFillPathAppliesDashPathEffect()
    {
        using var source = new SKPath();
        source.MoveTo(0f, 0f);
        source.LineTo(100f, 0f);
        using var destination = new SKPath();
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 10f,
            PathEffect = SKPathEffect.CreateDash(new[] { 10f, 10f }, 0f)
        };

        Assert.True(paint.GetFillPath(source, destination));
        Assert.Equal(5, destination.Geometry.Figures.Count);
    }

    [Fact]
    public void SkPaintToBrushAppliesBlendModeColorFilter()
    {
        using var paint = new SKPaint
        {
            Color = SKColors.Red,
            ColorFilter = SKColorFilter.CreateBlendMode(SKColors.Blue, SKBlendMode.Src)
        };

        var brush = Assert.IsType<SolidColorBrush>(paint.ToBrush());

        AssertNear(0f, brush.Color.X);
        AssertNear(0f, brush.Color.Y);
        AssertNear(1f, brush.Color.Z);
        AssertNear(1f, brush.Color.W);
    }

    [Fact]
    public void SkPaintToPenAppliesBlendModeColorFilter()
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Red,
            ColorFilter = SKColorFilter.CreateBlendMode(SKColors.Green, SKBlendMode.Src)
        };

        var pen = Assert.IsType<Pen>(paint.ToPen());
        var brush = Assert.IsType<SolidColorBrush>(pen.Brush);

        AssertNear(0f, brush.Color.X);
        AssertNear(1f, brush.Color.Y);
        AssertNear(0f, brush.Color.Z);
        AssertNear(1f, brush.Color.W);
    }

    [Fact]
    public void SkPaintAppliesShaderColorFilterCombination()
    {
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateColor(SKColors.Red),
            ColorFilter = SKColorFilter.CreateBlendMode(SKColors.Blue, SKBlendMode.Src)
        };

        var brush = Assert.IsType<SolidColorBrush>(paint.ToBrush());
        AssertNear(0f, brush.Color.X);
        AssertNear(0f, brush.Color.Y);
        AssertNear(1f, brush.Color.Z);
        AssertNear(1f, brush.Color.W);
    }

    [Fact]
    public void SkShaderMapsTileModesToGradientSpreadMethods()
    {
        using var repeatShader = SKShader.CreateLinearGradient(
            new SKPoint(0f, 0f),
            new SKPoint(10f, 0f),
            new[] { SKColors.Red, SKColors.Blue },
            null,
            SKShaderTileMode.Repeat);
        using var mirrorShader = SKShader.CreateRadialGradient(
            new SKPoint(5f, 5f),
            10f,
            new[] { SKColors.Red, SKColors.Blue },
            null,
            SKShaderTileMode.Mirror);

        var linear = Assert.IsType<LinearGradientBrush>(repeatShader.ToBrush());
        var radial = Assert.IsType<RadialGradientBrush>(mirrorShader.ToBrush());

        Assert.Equal(GradientSpreadMethod.Repeat, linear.SpreadMethod);
        Assert.Equal(GradientSpreadMethod.Reflect, radial.SpreadMethod);
    }

    [Fact]
    public void SkShaderRejectsUnsupportedDecalTileMode()
    {
        Assert.Throws<NotSupportedException>(() =>
            SKShader.CreateLinearGradient(
                new SKPoint(0f, 0f),
                new SKPoint(10f, 0f),
                new[] { SKColors.Red, SKColors.Blue },
                null,
                SKShaderTileMode.Decal));
    }

    private static void AssertMatrixNear(Matrix4x4 expected, Matrix4x4 actual)
    {
        AssertNear(expected.M11, actual.M11);
        AssertNear(expected.M12, actual.M12);
        AssertNear(expected.M21, actual.M21);
        AssertNear(expected.M22, actual.M22);
        AssertNear(expected.M41, actual.M41);
        AssertNear(expected.M42, actual.M42);
    }

    private static void AssertPathBounds(PathGeometry path, float minX, float minY, float maxX, float maxY)
    {
        Assert.True(path.TryGetBounds(out var min, out var max));
        AssertNear(minX, min.X);
        AssertNear(minY, min.Y);
        AssertNear(maxX, max.X);
        AssertNear(maxY, max.Y);
    }

    private static void AssertNear(float expected, float actual)
    {
        Assert.InRange(MathF.Abs(expected - actual), 0f, 0.0001f);
    }

    private static RenderCommand GetDrawTextureCommand(IReadOnlyList<RenderCommand> commands)
    {
        RenderCommand? result = null;
        for (int i = 0; i < commands.Count; i++)
        {
            if (commands[i].Type != RenderCommandType.DrawTexture)
            {
                continue;
            }

            Assert.Null(result);
            result = commands[i];
        }

        return Assert.IsType<RenderCommand>(result);
    }

    private static IList GetOwnedLayerTextures(SKCanvas canvas)
    {
        var field = typeof(SKCanvas).GetField(
            "_ownedLayerTextures",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (IList)field!.GetValue(canvas)!;
    }

    private static DrawingContext GetCurrentDrawingContext(SKCanvas canvas)
    {
        var field = typeof(SKCanvas).GetField(
            "_context",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (DrawingContext)field!.GetValue(canvas)!;
    }

    private static DrawingContext GetSurfaceDrawingContext(SKSurface surface)
    {
        var field = typeof(SKSurface).GetField(
            "_drawingContext",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (DrawingContext)field!.GetValue(surface)!;
    }

    private static GpuTexture GetSurfaceTexture(SKSurface surface)
    {
        var textureField = typeof(SKSurface).GetField(
            "_gpuTexture",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (GpuTexture)textureField!.GetValue(surface)!;
    }

    private static Compositor GetSurfaceCompositor(SKSurface surface)
    {
        var contextField = typeof(SKSurface).GetField(
            "_context",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var textureField = typeof(SKSurface).GetField(
            "_gpuTexture",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var method = typeof(SKSurface).GetMethod(
            "GetCompositorForContext",
            BindingFlags.Static | BindingFlags.NonPublic);

        var context = (WgpuContext)contextField!.GetValue(surface)!;
        var texture = (GpuTexture)textureField!.GetValue(surface)!;
        return (Compositor)method!.Invoke(null, new object[] { context, texture.Format })!;
    }

    private static Compositor GetCanvasCompositor(SKSurface surface)
    {
        var contextField = typeof(SKSurface).GetField(
            "_context",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var method = typeof(SKCanvas).GetMethod(
            "GetCompositorForContext",
            BindingFlags.Static | BindingFlags.NonPublic);

        var context = (WgpuContext)contextField!.GetValue(surface)!;
        return (Compositor)method!.Invoke(null, new object[] { context })!;
    }

    private static void ResetSurfaceCompositor(SKSurface surface)
    {
        var contextField = typeof(SKSurface).GetField(
            "_context",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var removeMethod = typeof(SKSurface).GetMethod(
            "RemoveCachedCompositor",
            BindingFlags.Static | BindingFlags.NonPublic);

        removeMethod!.Invoke(null, new[] { contextField!.GetValue(surface)! });
    }

    private static void DisposeCanvasCompositorCompute(SKSurface surface)
    {
        var computeField = typeof(Compositor).GetField(
            "_compute",
            BindingFlags.Instance | BindingFlags.NonPublic);
        ((IDisposable)computeField!.GetValue(GetCanvasCompositor(surface))!).Dispose();
    }

    private static void ResetCanvasCompositor(SKSurface surface)
    {
        var contextField = typeof(SKSurface).GetField(
            "_context",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var removeMethod = typeof(SKCanvas).GetMethod(
            "RemoveCachedCompositor",
            BindingFlags.Static | BindingFlags.NonPublic);

        removeMethod!.Invoke(null, new[] { contextField!.GetValue(surface)! });
    }

    private sealed class ThrowingCompileExtension : ICompositorExtension
    {
        public void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            throw new InvalidOperationException("Synthetic SKSurface flush failure.");
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
        }
    }
}
