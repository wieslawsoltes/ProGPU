using System.Drawing.Imaging;
using Xunit;

namespace System.Drawing.Tests;

public sealed class GraphicsImageOverloadQualityTests
{
    [Fact]
    public void PointAndUnscaledOverloadsPreserveSourceSize()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(6, 2);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImage(source, new Point(0, 0));
            graphics.DrawImage(source, 2, 0);
            graphics.DrawImageUnscaled(source, 4, 0, 1, 1);
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(3, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(4, 1).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(5, 1).ToArgb());
    }

    [Fact]
    public void UnscaledAndClippedRestrictsDestinationAndSource()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(2, 2);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImageUnscaledAndClipped(source, new Rectangle(0, 0, 1, 2));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(0, 1).ToArgb());
        Assert.Equal(0, target.GetPixel(1, 0).A);
        Assert.Equal(0, target.GetPixel(1, 1).A);
    }

    [Fact]
    public void PointSourceRectangleOverloadsCropWithoutScaling()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(4, 1);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImage(
                source,
                0f,
                0f,
                new RectangleF(0f, 1f, 2f, 1f),
                GraphicsUnit.Pixel);
            graphics.DrawImage(
                source,
                2,
                0,
                new Rectangle(0, 0, 2, 1),
                GraphicsUnit.Pixel);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(1, 0).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(2, 0).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(3, 0).ToArgb());
    }

    [Fact]
    public void FloatSourceCallbacksAbortOrApplyImageAttributes()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(2, 1);
        using var attributes = new ImageAttributes();
        attributes.SetRemapTable(new ColorMap
        {
            OldColor = Color.Red,
            NewColor = Color.Yellow,
        });
        int callbackCount = 0;
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, 1, 1),
                0f,
                0f,
                1f,
                1f,
                GraphicsUnit.Pixel,
                attributes,
                _ =>
                {
                    callbackCount++;
                    return false;
                },
                new IntPtr(42));
            graphics.DrawImage(
                source,
                new Rectangle(1, 0, 1, 1),
                0,
                0,
                1,
                1,
                GraphicsUnit.Pixel,
                null,
                _ =>
                {
                    callbackCount++;
                    return true;
                });
        }

        Assert.Equal(2, callbackCount);
        Assert.Equal(Color.Yellow.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(0, target.GetPixel(1, 0).A);
    }

    [Fact]
    public void NewImageOverloadsRejectNullImages()
    {
        using var target = new Bitmap(2, 2);
        using Graphics graphics = Graphics.FromImage(target);

        Assert.Throws<ArgumentNullException>(() => graphics.DrawImage(null!, Point.Empty));
        Assert.Throws<ArgumentNullException>(() =>
            graphics.DrawImageUnscaledAndClipped(null!, new Rectangle(0, 0, 1, 1)));
        Assert.Throws<ArgumentNullException>(() =>
            graphics.DrawImage(
                null!,
                Rectangle.Empty,
                0f,
                0f,
                1f,
                1f,
                GraphicsUnit.Pixel,
                null,
                null,
                IntPtr.Zero));
    }

    private static Bitmap CreateQuadrantSource()
    {
        var source = new Bitmap(2, 2);
        source.SetPixel(0, 0, Color.Red);
        source.SetPixel(1, 0, Color.Green);
        source.SetPixel(0, 1, Color.Blue);
        source.SetPixel(1, 1, Color.White);
        return source;
    }
}
