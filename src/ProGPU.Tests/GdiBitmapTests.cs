using System;
using ProGPU.Scene;
using Xunit;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingGraphicsUnit = System.Drawing.GraphicsUnit;
using DrawingImageAttributes = System.Drawing.Imaging.ImageAttributes;
using DrawingColorMatrix = System.Drawing.Imaging.ColorMatrix;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingRectangleF = System.Drawing.RectangleF;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using DrawingImage = System.Drawing.Image;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace ProGPU.Tests;

public sealed class GdiBitmapTests
{
    [Theory]
    [InlineData(0, 1, "width")]
    [InlineData(-1, 1, "width")]
    [InlineData(1, 0, "height")]
    [InlineData(1, -1, "height")]
    public void BitmapConstructorRejectsNonPositiveDimensions(int width, int height, string parameterName)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DrawingBitmap(width, height));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void VisibleClipBoundsMapsBitmapExtentBackToWorldCoordinates()
    {
        using var target = new DrawingBitmap(100, 50);
        using var graphics = DrawingGraphics.FromImage(target);

        Assert.Equal(new DrawingRectangleF(0f, 0f, 100f, 50f), graphics.VisibleClipBounds);

        graphics.TranslateTransform(-25f, 25f);
        Assert.Equal(new DrawingRectangleF(25f, -25f, 100f, 50f), graphics.VisibleClipBounds);

        graphics.ResetTransform();
        graphics.ScaleTransform(2f, 0.5f);
        Assert.Equal(new DrawingRectangleF(0f, 0f, 50f, 100f), graphics.VisibleClipBounds);

        graphics.ResetTransform();
        graphics.RotateTransform(90f);
        AssertRectangleApproximately(new DrawingRectangleF(0f, -100f, 50f, 100f), graphics.VisibleClipBounds);
    }

    [Fact]
    public void VisibleClipBoundsHonorsPageUnitAndSavedState()
    {
        using var target = new DrawingBitmap(192, 96);
        using var graphics = DrawingGraphics.FromImage(target);
        System.Drawing.Drawing2D.GraphicsState state = graphics.Save();

        graphics.PageUnit = DrawingGraphicsUnit.Inch;
        graphics.PageScale = 2f;
        Assert.Equal(new DrawingRectangleF(0f, 0f, 1f, 0.5f), graphics.VisibleClipBounds);

        graphics.Restore(state);
        Assert.Equal(new DrawingRectangleF(0f, 0f, 192f, 96f), graphics.VisibleClipBounds);
    }

    private static void AssertRectangleApproximately(DrawingRectangleF expected, DrawingRectangleF actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Width, actual.Width, 4);
        Assert.Equal(expected.Height, actual.Height, 4);
    }

    [Fact]
    public void BaseTypedImageSaveWritesRoundtrippableBmpPixels()
    {
        using DrawingImage image = new DrawingBitmap(2, 2);
        var bitmap = Assert.IsType<DrawingBitmap>(image);
        bitmap.SetPixel(0, 0, DrawingColor.FromArgb(255, 255, 0, 0));
        bitmap.SetPixel(1, 0, DrawingColor.FromArgb(128, 64, 128, 192));
        bitmap.SetPixel(0, 1, DrawingColor.FromArgb(255, 0, 255, 0));
        bitmap.SetPixel(1, 1, DrawingColor.FromArgb(255, 0, 0, 255));
        DrawingColor storedSemiTransparentPixel = bitmap.GetPixel(1, 0);

        using var stream = new System.IO.MemoryStream();
        image.Save(stream, DrawingImageFormat.Bmp);

        byte[] encoded = stream.ToArray();
        Assert.Equal((byte)'B', encoded[0]);
        Assert.Equal((byte)'M', encoded[1]);
        Assert.Equal(2, BitConverter.ToInt32(encoded, 18));
        Assert.Equal(2, BitConverter.ToInt32(encoded, 22));
        Assert.Equal(32, BitConverter.ToInt16(encoded, 28));

        stream.Position = 0;
        using var decoded = new DrawingBitmap(stream);
        Assert.Equal(DrawingColor.FromArgb(255, 255, 0, 0), decoded.GetPixel(0, 0));
        Assert.Equal(storedSemiTransparentPixel, decoded.GetPixel(1, 0));
        Assert.Equal(DrawingColor.FromArgb(255, 0, 255, 0), decoded.GetPixel(0, 1));
        Assert.Equal(DrawingColor.FromArgb(255, 0, 0, 255), decoded.GetPixel(1, 1));
    }

    [Fact]
    public void DrawImageWithSourceRectRecordsTextureSourceRect()
    {
        using var source = new DrawingBitmap(8, 4);
        using var target = new DrawingBitmap(4, 2);
        using var graphics = DrawingGraphics.FromImage(target);

        graphics.DrawImage(
            source,
            new DrawingRectangle(0, 0, 4, 2),
            new DrawingRectangle(2, 1, 4, 2),
            DrawingGraphicsUnit.Pixel);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawTexture, command.Type);
        Assert.Equal(new Rect(2f, 1f, 4f, 2f), command.SrcRect);
    }

    [Fact]
    public void DrawImageWithColorMatrixRecordsImageEffectCommand()
    {
        using var source = new DrawingBitmap(8, 4);
        using var target = new DrawingBitmap(4, 2);
        using var attributes = new DrawingImageAttributes();
        attributes.SetColorMatrix(new DrawingColorMatrix(new[]
        {
            new[] { 1f, 0f, 0f, 0f, 0f },
            new[] { 0f, 1f, 0f, 0f, 0f },
            new[] { 0f, 0f, 1f, 0f, 0f },
            new[] { 0f, 0f, 0f, 0.5f, 0f },
            new[] { 0f, 0f, 0f, 0f, 1f }
        }));

        using var graphics = DrawingGraphics.FromImage(target);
        graphics.DrawImage(
            source,
            new DrawingRectangle(0, 0, 4, 2),
            2,
            1,
            4,
            2,
            DrawingGraphicsUnit.Pixel,
            attributes);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawExtension, command.Type);
        Assert.Equal(CompositorBuiltInExtensions.ImageEffect, command.ExtensionId);
        var parameters = Assert.IsType<ImageEffectParams>(command.DataParam);
        Assert.Equal(new Rect(2f, 1f, 4f, 2f), parameters.SourceRect);
        Assert.NotNull(parameters.ColorMatrix);
        Assert.Equal(0.5f, parameters.ColorMatrix.Value.Alpha.W);
    }
}
