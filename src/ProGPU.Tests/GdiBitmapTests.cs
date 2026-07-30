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
using DrawingIcon = System.Drawing.Icon;

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

    [Fact]
    public void GraphicsDisposeDefersBitmapFlushUntilBitmapIsConsumed()
    {
        var previous = ProGPU.Backend.WgpuContext.Current;
        using var hostContext = new ProGPU.Backend.WgpuContext();

        try
        {
            hostContext.Initialize(null);
            ProGPU.Backend.WgpuContext.Current = hostContext;
            using var bitmap = new DrawingBitmap(2, 2);

            using (DrawingGraphics graphics = DrawingGraphics.FromImage(bitmap))
            {
                graphics.Clear(DrawingColor.Red);
            }

            Assert.NotEmpty(bitmap.RecordedContext.Commands);

            Assert.True(bitmap.TryGetGpuTexture(hostContext, out _));
            Assert.Empty(bitmap.RecordedContext.Commands);
            Assert.Equal(DrawingColor.Red.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        }
        finally
        {
            ProGPU.Backend.WgpuContext.Current = previous;
        }
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
    public void IconSaveWritesRoundtrippablePngIconAndLeavesStreamOpen()
    {
        using var source = new DrawingBitmap(2, 2);
        source.SetPixel(0, 0, DrawingColor.FromArgb(255, 255, 0, 0));
        source.SetPixel(1, 0, DrawingColor.FromArgb(128, 64, 128, 192));
        source.SetPixel(0, 1, DrawingColor.FromArgb(255, 0, 255, 0));
        source.SetPixel(1, 1, DrawingColor.FromArgb(0, 0, 0, 0));

        using var icon = DrawingIcon.FromHandle(source.GetHicon());
        using var stream = new System.IO.MemoryStream();
        icon.Save(stream);

        Assert.True(stream.CanWrite);
        byte[] encoded = stream.ToArray();
        Assert.Equal(0, BitConverter.ToUInt16(encoded, 0));
        Assert.Equal(1, BitConverter.ToUInt16(encoded, 2));
        Assert.Equal(1, BitConverter.ToUInt16(encoded, 4));
        Assert.Equal(2, encoded[6]);
        Assert.Equal(2, encoded[7]);
        Assert.Equal(1, BitConverter.ToUInt16(encoded, 10));
        Assert.Equal(32, BitConverter.ToUInt16(encoded, 12));
        Assert.Equal(22u, BitConverter.ToUInt32(encoded, 18));

        int imageLength = checked((int)BitConverter.ToUInt32(encoded, 14));
        Assert.Equal(encoded.Length - 22, imageLength);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, encoded[22..26]);

        using var imageStream = new System.IO.MemoryStream(encoded, 22, imageLength, writable: false);
        using var decoded = new DrawingBitmap(imageStream);
        Assert.Equal(source.GetPixel(0, 0), decoded.GetPixel(0, 0));
        Assert.Equal(source.GetPixel(1, 0), decoded.GetPixel(1, 0));
        Assert.Equal(source.GetPixel(0, 1), decoded.GetPixel(0, 1));
        Assert.Equal(source.GetPixel(1, 1), decoded.GetPixel(1, 1));

        stream.Position = 0;
        using var decodedIcon = new DrawingIcon(stream);
        using var decodedIconBitmap = decodedIcon.ToBitmap();
        Assert.Equal(source.GetPixel(0, 0), decodedIconBitmap.GetPixel(0, 0));
        Assert.Equal(source.GetPixel(1, 0), decodedIconBitmap.GetPixel(1, 0));
        Assert.Equal(source.GetPixel(0, 1), decodedIconBitmap.GetPixel(0, 1));
        Assert.Equal(source.GetPixel(1, 1), decodedIconBitmap.GetPixel(1, 1));
    }

    [Fact]
    public void IconSaveEncodes256PixelDirectoryDimensionsAsZero()
    {
        using var source = new DrawingBitmap(256, 256);
        using var icon = DrawingIcon.FromHandle(source.GetHicon());
        using var stream = new System.IO.MemoryStream();

        icon.Save(stream);

        byte[] encoded = stream.ToArray();
        Assert.Equal(0, encoded[6]);
        Assert.Equal(0, encoded[7]);
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
        Assert.True(command.HasImageEffect);
        Assert.Null(command.DataParam);
        Assert.Equal(new Rect(2f, 1f, 4f, 2f), command.SrcRect);
        Assert.NotNull(command.ImageEffect.ColorMatrix);
        Assert.Equal(
            0.5f,
            command.ImageEffect.ColorMatrix.Value.Alpha.W);
    }
}
