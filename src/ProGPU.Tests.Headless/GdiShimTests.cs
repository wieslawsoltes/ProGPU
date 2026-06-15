using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class GdiShimTests
{
    [Fact]
    public void TestGdiDrawAndSave()
    {
        // 1. Create a bitmap
        using var bitmap = new Bitmap(200, 200);

        // 2. Create Graphics context from bitmap
        using (var g = Graphics.FromImage(bitmap))
        {
            // Clear to light gray
            g.Clear(Color.LightGray);

            // Draw a red rectangle
            using (var pen = new Pen(Color.Red, 4f))
            {
                g.DrawRectangle(pen, 10, 10, 180, 180);
            }

            // Fill a blue ellipse
            using (var brush = new SolidBrush(Color.Blue))
            {
                g.FillEllipse(brush, 50, 50, 100, 100);
            }

            // Draw a line
            using (var pen = new Pen(Color.Green, 2f))
            {
                g.DrawLine(pen, 10, 10, 190, 190);
            }
        }

        // 3. Save to PNG
        string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gdi_shim_test.png");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        bitmap.Save(outputPath);

        // 4. Assertions
        Assert.True(File.Exists(outputPath), "The output image was not saved successfully.");
        
        // Assert color of central pixel is Green (the diagonal line)
        Color centerColor = bitmap.GetPixel(100, 100);
        Assert.Equal(Color.Green.ToArgb(), centerColor.ToArgb());

        // Assert color of a pixel inside the ellipse but off the line is Blue
        Color ellipseColor = bitmap.GetPixel(100, 80);
        Assert.Equal(Color.Blue.ToArgb(), ellipseColor.ToArgb());

        // Assert corner pixel is LightGray
        Color cornerColor = bitmap.GetPixel(2, 2);
        Assert.Equal(Color.LightGray.ToArgb(), cornerColor.ToArgb());

        // Assert border pixel on the red rectangle is Red
        Color rectBorderColor = bitmap.GetPixel(10, 50);
        Assert.Equal(Color.Red.ToArgb(), rectBorderColor.ToArgb());
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(9, 0, 2, 1)]
    [InlineData(0, 9, 1, 2)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    public void LockBitsRejectsRectanglesOutsideBitmapBounds(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(10, 10);

        Assert.Throws<ArgumentException>(
            () => bitmap.LockBits(
                new Rectangle(x, y, width, height),
                ImageLockMode.ReadWrite,
                PixelFormat.Format32bppArgb));
    }

    [Fact]
    public void LockBitsAcceptsRectangleInsideBitmapBounds()
    {
        using var bitmap = new Bitmap(10, 10);

        BitmapData data = bitmap.LockBits(
            new Rectangle(2, 3, 4, 5),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            Assert.Equal(4, data.Width);
            Assert.Equal(5, data.Height);
            Assert.Equal(16, data.Stride);
            Assert.Equal(PixelFormat.Format32bppArgb, data.PixelFormat);
            Assert.NotEqual(IntPtr.Zero, data.Scan0);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [Fact]
    public void DrawImageRecordsFullTransformForRotatedImages()
    {
        using var source = new Bitmap(4, 6);
        using var target = new Bitmap(40, 40);
        using var graphics = Graphics.FromImage(target);

        graphics.RotateTransform(90f);
        graphics.DrawImage(source, new RectangleF(2f, 3f, 4f, 5f));

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawTexture, command.Type);
        Assert.Same(source.GpuTexture, command.Texture);
        Assert.Equal(new Rect(2f, 3f, 4f, 5f), command.Rect);
        Assert.Equal(TextureSamplingMode.Linear, command.TextureSamplingMode);
        AssertNear(0f, command.Transform.M11);
        AssertNear(1f, command.Transform.M12);
        AssertNear(-1f, command.Transform.M21);
        AssertNear(0f, command.Transform.M22);
    }

    private static void AssertNear(float expected, float actual)
    {
        Assert.InRange(MathF.Abs(expected - actual), 0f, 0.0001f);
    }
}
