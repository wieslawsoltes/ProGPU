using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkMaskAndForwardingCanvasContractTests
{
    [Fact]
    public void MaskFactoriesSnapshotCoverageStateAndUseOfficialRadiusConversion()
    {
        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, 4f, respectCTM: false);
        Assert.NotNull(blur);
        Assert.Equal(SKMaskFilter.MaskFilterKind.Blur, blur.Kind);
        Assert.Equal(SKBlurStyle.Outer, blur.BlurStyle);
        Assert.Equal(4f, blur.Sigma);
        Assert.False(blur.RespectCtm);
        Assert.Null(SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0f));

        var table = Enumerable.Range(0, SKMaskFilter.TableMaxLength)
            .Select(static value => (byte)value)
            .ToArray();
        using var tableFilter = SKMaskFilter.CreateTable(table);
        table[42] = 0;
        Assert.Equal(42, tableFilter.Table.Span[42]);

        var sigma = SKMaskFilter.ConvertRadiusToSigma(10f);
        Assert.Equal(6.2735f, sigma, 4);
        Assert.Equal(10f, SKMaskFilter.ConvertSigmaToRadius(sigma), 4);
        Assert.Equal(0f, SKMaskFilter.ConvertRadiusToSigma(-1f));
        Assert.Equal(0f, SKMaskFilter.ConvertSigmaToRadius(0.5f));
    }

    [Fact]
    public void CoverageTablesProvideClipGammaAndOverdrawMappings()
    {
        using var clip = SKMaskFilter.CreateClip(32, 224);
        Assert.Equal(0, clip.Table.Span[32]);
        Assert.InRange(clip.Table.Span[128], (byte)126, (byte)129);
        Assert.Equal(255, clip.Table.Span[224]);

        using var gamma = SKMaskFilter.CreateGamma(2f);
        Assert.Equal(0, gamma.Table.Span[0]);
        Assert.InRange(gamma.Table.Span[128], (byte)63, (byte)65);
        Assert.Equal(255, gamma.Table.Span[255]);

        SKColor[] colors =
        [
            SKColors.Red,
            SKColors.Green,
            SKColors.Blue,
            SKColors.Yellow,
            SKColors.Cyan,
            SKColors.Magenta,
        ];
        using var overdraw = SKColorFilter.CreateOverdraw(colors);
        Assert.Equal(SKColors.Empty, overdraw.Apply(SKColors.Empty));
        Assert.Equal(SKColors.Red, overdraw.Apply(new SKColor(0, 0, 0, 1)));
        Assert.Equal(SKColors.Magenta, overdraw.Apply(new SKColor(0, 0, 0, 255)));
        Assert.Throws<ArgumentException>(() => SKColorFilter.CreateOverdraw(colors[..5]));
    }

    [Fact]
    public void PaintFastBoundsConservativelyIncludesStrokeAndBlur()
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4f,
            StrokeJoin = SKStrokeJoin.Round,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f),
        };

        Assert.True(paint.GetFastBounds(new SKRect(10f, 20f, 30f, 40f), out var bounds));
        Assert.Equal(new SKRect(-1f, 9f, 41f, 51f), bounds);

        paint.ImageFilter = SKImageFilter.CreateBlur(1f, 1f);
        Assert.False(paint.GetFastBounds(SKRect.Create(10f, 10f), out _));
    }

    [Fact]
    public void NWayCanvasForwardsRetainedCommandsAndBufferSlicesImmediately()
    {
        var firstContext = new DrawingContext();
        var secondContext = new DrawingContext();
        using var first = new SKCanvas(firstContext, 64f, 64f);
        using var second = new SKCanvas(secondContext, 64f, 64f);
        using var fanout = new SKNWayCanvas(64, 64);
        using var paint = new SKPaint { Color = SKColors.CornflowerBlue };

        fanout.AddCanvas(first);
        fanout.AddCanvas(second);
        fanout.DrawRect(SKRect.Create(2f, 3f, 10f, 12f), paint);

        Assert.Equal(RenderCommandType.DrawRect, Assert.Single(firstContext.Commands).Type);
        Assert.Equal(RenderCommandType.DrawRect, Assert.Single(secondContext.Commands).Type);

        fanout.RemoveCanvas(second);
        fanout.DrawCircle(12f, 12f, 4f, paint);
        Assert.Equal(2, firstContext.Commands.Count);
        Assert.Single(secondContext.Commands);

        fanout.RemoveAll();
        fanout.DrawOval(SKRect.Create(8f, 8f), paint);
        Assert.Equal(2, firstContext.Commands.Count);
    }

    [Fact]
    public void OverdrawCanvasEmitsAdditiveCoverageCommands()
    {
        var targetContext = new DrawingContext();
        using var target = new SKCanvas(targetContext, 32f, 32f);
        using var overdraw = new SKOverdrawCanvas(target);
        using var paint = new SKPaint { Color = SKColors.Red };

        overdraw.DrawRect(SKRect.Create(1f, 2f, 8f, 9f), paint);

        Assert.Equal(3, targetContext.Commands.Count);
        Assert.Equal(RenderCommandType.PushBlendMode, targetContext.Commands[0].Type);
        Assert.Equal(GpuBlendMode.Plus, (GpuBlendMode)targetContext.Commands[0].IntParam);
        var draw = targetContext.Commands[1];
        Assert.Equal(RenderCommandType.DrawRect, draw.Type);
        var brush = Assert.IsType<SolidColorBrush>(draw.Brush);
        Assert.Equal(1f / 255f, brush.Color.W);
        Assert.Equal(RenderCommandType.PopBlendMode, targetContext.Commands[2].Type);
    }

    [Fact]
    public void MaskBlurExecutesThroughTheGpuLayerGraph()
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var mask = SKMaskFilter.CreateBlur(
            SKBlurStyle.Normal,
            2f,
            respectCTM: false);
        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = false,
            MaskFilter = mask,
        };

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawRect(new SKRect(12f, 12f, 20f, 20f), paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        var centerAlpha = pixels[(16 * 32 + 16) * 4 + 3];
        var tailAlpha = pixels[(16 * 32 + 9) * 4 + 3];
        var distantAlpha = pixels[(16 * 32 + 2) * 4 + 3];

        Assert.True(centerAlpha > tailAlpha);
        Assert.InRange(tailAlpha, (byte)1, (byte)254);
        Assert.Equal((byte)0, distantAlpha);
    }

    [Fact]
    public void OverdrawPaletteExecutesAsAWebGpuComputePass()
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(6, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        byte[] counts = [0, 1, 2, 5, 6, 255];
        for (var index = 0; index < counts.Length; index++)
        {
            bitmap.SetPixel(
                index,
                0,
                new SKColor(255, 255, 255, counts[index]));
        }

        using var image = SKImage.FromBitmap(bitmap);
        SKColor[] colors =
        [
            SKColors.Red,
            SKColors.Green,
            SKColors.Blue,
            SKColors.Yellow,
            SKColors.Cyan,
            SKColors.Magenta,
        ];
        using var filter = SKColorFilter.CreateOverdraw(colors);
        using var paint = new SKPaint { ColorFilter = filter };
        using var surface = SKSurface.Create(
            new SKImageInfo(6, 1, SKColorType.Rgba8888, SKAlphaType.Premul));

        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(image, 0f, 0f, paint);
        surface.Flush();

        using var snapshot = surface.Snapshot();
        var pixels = snapshot.Texture.ReadPixels();
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels[..4]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[4..8]);
        Assert.Equal(new byte[] { 0, 128, 0, 255 }, pixels[8..12]);
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, pixels[20..24]);
    }
}
