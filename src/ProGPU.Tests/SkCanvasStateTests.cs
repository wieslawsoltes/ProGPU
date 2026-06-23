using System;
using System.Collections;
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
        Assert.Equal(RenderCommandType.DrawExtension, command.Type);
        Assert.Equal(CompositorBuiltInExtensions.ImageEffect, command.ExtensionId);
        var parameters = Assert.IsType<ImageEffectParams>(command.DataParam);
        Assert.NotNull(parameters.Texture);
        Assert.Equal(new Rect(0f, 0f, 100f, 100f), parameters.Rect);
        Assert.Equal(4f, parameters.BlurSigma);
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
        Assert.Single(GetOwnedLayerTextures(canvas));
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
        var retainedTexture = Assert.Single(layerContext.Commands).Texture!;
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
        var retainedTexture = Assert.Single(layerContext.Commands).Texture!;
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
    public void DrawRectScalesStrokeWidthWithCanvasMatrix()
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
        AssertNear(6f, command.Pen!.Thickness);
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
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawTexture, draw.Type);
                Assert.NotSame(image.Texture, draw.Texture);
                Assert.Equal(new Rect(10f, 20f, 20f, 20f), draw.Rect);
                Assert.Equal(new Rect(1f, 2f, 2f, 2f), draw.SrcRect);
            },
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

        var command = Assert.Single(context.Commands);
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
        var command = Assert.Single(picture.Commands);
        var retainedTexture = command.Texture!;
        Assert.Equal(1, context.RetainedResourceCount);
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
            recorder.BeginRecording(new Rect(0f, 0f, 16f, 16f));

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

        var command = Assert.Single(target.Commands);
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
    public void SkPaintRejectsShaderColorFilterCombination()
    {
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateColor(SKColors.Red),
            ColorFilter = SKColorFilter.CreateBlendMode(SKColors.Blue, SKBlendMode.Src)
        };

        Assert.Throws<NotSupportedException>(() => paint.ToBrush());
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
