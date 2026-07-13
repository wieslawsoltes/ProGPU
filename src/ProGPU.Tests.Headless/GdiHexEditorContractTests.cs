using System;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class GdiHexEditorContractTests
{
    [Fact]
    public void BitmapGraphicsConstructorCreatesHexEditorBackingBitmap()
    {
        using var compatibleSource = new Bitmap(8, 8);
        using var graphics = Graphics.FromImage(compatibleSource);
        using var backingBitmap = new Bitmap(320, 180, graphics);

        Assert.Equal(320, backingBitmap.Width);
        Assert.Equal(180, backingBitmap.Height);
        Assert.Equal(new Size(320, 180), backingBitmap.Size);
        Assert.Equal(PixelFormat.Format32bppArgb, backingBitmap.PixelFormat);
    }

    [Fact]
    public void BitmapGraphicsConstructorValidatesTheSourceGraphicsFirst()
    {
        Assert.Throws<ArgumentNullException>("g", () => new Bitmap(16, 16, null!));

        using var compatibleSource = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(compatibleSource);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bitmap(0, 16, graphics));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bitmap(16, 0, graphics));
    }

    [Fact]
    public void FontGetHeightMatchesHexEditorRowHeightContract()
    {
        using var font = new Font("Courier New", 9.5f, FontStyle.Regular);
        using var compatibleSource = new Bitmap(8, 8);
        using var graphics = Graphics.FromImage(compatibleSource);

        float defaultHeight = font.GetHeight();
        float graphicsHeight = font.GetHeight(graphics);
        int hexEditorHeight = (int)Math.Ceiling(defaultHeight) + 1;

        Assert.True(defaultHeight > 0f);
        Assert.Equal(defaultHeight, graphicsHeight, precision: 5);
        Assert.Equal((int)MathF.Ceiling(defaultHeight), font.Height);
        Assert.True(hexEditorHeight > font.Size);
    }

    [Fact]
    public void FontGetHeightRejectsNullGraphics()
    {
        using var font = new Font("Courier New", 9.5f, FontStyle.Regular);

        Assert.Throws<ArgumentNullException>("graphics", () => font.GetHeight(null!));
    }

    [Fact]
    public void FontGetHeightDpiPreservesGdiFloatingPointInputs()
    {
        using var font = new Font("Courier New", 9.5f, FontStyle.Regular);
        float oneDpiHeight = font.GetHeight(1f);

        Assert.Equal(0f, font.GetHeight(0f));
        Assert.Equal(-oneDpiHeight, font.GetHeight(-1f), precision: 5);
        Assert.True(float.IsNaN(font.GetHeight(float.NaN)));
        Assert.True(float.IsPositiveInfinity(font.GetHeight(float.PositiveInfinity)));
        Assert.True(float.IsNegativeInfinity(font.GetHeight(float.NegativeInfinity)));
    }
}
