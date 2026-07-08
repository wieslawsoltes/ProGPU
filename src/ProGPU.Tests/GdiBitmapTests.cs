using System;
using ProGPU.Scene;
using Xunit;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingGraphicsUnit = System.Drawing.GraphicsUnit;
using DrawingImageAttributes = System.Drawing.Imaging.ImageAttributes;
using DrawingColorMatrix = System.Drawing.Imaging.ColorMatrix;
using DrawingRectangle = System.Drawing.Rectangle;
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
