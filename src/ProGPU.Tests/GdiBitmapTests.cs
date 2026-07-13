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
