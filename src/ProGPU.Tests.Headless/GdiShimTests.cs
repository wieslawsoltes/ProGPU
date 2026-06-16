using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Scene;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class GdiShimTests
{
    [Fact]
    public void ClearUsesSourceBlendMode()
    {
        using var bitmap = new Bitmap(12, 34);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.Clear(Color.FromArgb(128, 10, 20, 30));

        Assert.Collection(
            graphics.DrawingContext.Commands,
            push =>
            {
                Assert.Equal(RenderCommandType.PushBlendMode, push.Type);
                Assert.Equal((int)GpuBlendMode.Src, push.IntParam);
            },
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawRect, draw.Type);
                Assert.Equal(new Rect(0f, 0f, 12f, 34f), draw.Rect);
            },
            pop => Assert.Equal(RenderCommandType.PopBlendMode, pop.Type));
    }

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

    [Fact]
    public void SaveUnpremultipliesPremultipliedBitmapPixels()
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.GpuTexture.WritePixels(new byte[] { 128, 0, 0, 128 });
        bitmap.GpuTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;

        var outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gdi_premultiplied_save.png");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        bitmap.Save(outputPath);

        using var stream = File.OpenRead(outputPath);
        using var data = SKData.Create(stream);
        using var decoded = SKBitmap.Decode(data);
        var pixel = decoded.GetPixels();

        Assert.Equal(255, Marshal.ReadByte(pixel, 0));
        Assert.Equal(0, Marshal.ReadByte(pixel, 1));
        Assert.Equal(0, Marshal.ReadByte(pixel, 2));
        Assert.Equal(128, Marshal.ReadByte(pixel, 3));
    }

    [Fact]
    public void GetPixelUnpremultipliesPremultipliedBitmapPixels()
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.GpuTexture.WritePixels(new byte[] { 128, 0, 0, 128 });
        bitmap.GpuTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;

        var color = bitmap.GetPixel(0, 0);

        Assert.Equal(Color.FromArgb(128, 255, 0, 0).ToArgb(), color.ToArgb());
    }

    [Fact]
    public void SetPixelPreservesPremultipliedBitmapAlphaMode()
    {
        using var bitmap = new Bitmap(2, 1);
        bitmap.GpuTexture.WritePixels(new byte[]
        {
            128, 0, 0, 128,
            0, 0, 128, 128
        });
        bitmap.GpuTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;

        bitmap.SetPixel(0, 0, Color.FromArgb(128, 0, 255, 0));

        Assert.Equal(GpuTextureAlphaMode.Premultiplied, bitmap.GpuTexture.AlphaMode);
        Assert.Equal(Color.FromArgb(128, 0, 255, 0).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(128, 0, 0, 255).ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
    }

    [Fact]
    public void UnlockBitsPreservesPremultipliedBitmapAlphaModeForSubRect()
    {
        using var bitmap = new Bitmap(2, 1);
        bitmap.GpuTexture.WritePixels(new byte[]
        {
            128, 0, 0, 128,
            0, 0, 128, 128
        });
        bitmap.GpuTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            WriteBgra(data.Scan0, Color.FromArgb(128, 0, 255, 0));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.Equal(GpuTextureAlphaMode.Premultiplied, bitmap.GpuTexture.AlphaMode);
        Assert.Equal(Color.FromArgb(128, 0, 255, 0).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(128, 0, 0, 255).ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
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
    public void LockBitsUsesRequested24BppRgbLayout()
    {
        using var bitmap = new Bitmap(2, 1);
        bitmap.SetPixel(0, 0, Color.FromArgb(255, 10, 20, 30));

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadWrite,
            PixelFormat.Format24bppRgb);

        try
        {
            Assert.Equal(PixelFormat.Format24bppRgb, data.PixelFormat);
            Assert.Equal(4, data.Stride);
            Assert.Equal(30, Marshal.ReadByte(data.Scan0, 0));
            Assert.Equal(20, Marshal.ReadByte(data.Scan0, 1));
            Assert.Equal(10, Marshal.ReadByte(data.Scan0, 2));
            Marshal.WriteByte(data.Scan0, 0, 255);
            Marshal.WriteByte(data.Scan0, 1, 0);
            Marshal.WriteByte(data.Scan0, 2, 0);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void LockBitsUsesRequested16BppRgb565Layout()
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetPixel(0, 0, Color.Red);

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadWrite,
            PixelFormat.Format16bppRgb565);

        try
        {
            Assert.Equal(PixelFormat.Format16bppRgb565, data.PixelFormat);
            Assert.Equal(4, data.Stride);
            Assert.Equal(0x00, Marshal.ReadByte(data.Scan0, 0));
            Assert.Equal(0xF8, Marshal.ReadByte(data.Scan0, 1));
            Marshal.WriteByte(data.Scan0, 0, 0xE0);
            Marshal.WriteByte(data.Scan0, 1, 0x07);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.Equal(Color.Lime.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void FillRectangleNormalizesReflectedBounds()
    {
        using var graphics = Graphics.FromHwnd(IntPtr.Zero);
        graphics.ScaleTransform(-1f, 1f);

        graphics.FillRectangle(Brushes.Blue, 1f, 2f, 3f, 4f);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawRect, command.Type);
        Assert.Equal(new Rect(-4f, 2f, 3f, 4f), command.Rect);
    }

    [Fact]
    public void UnlockBitsDoesNotWriteBackReadOnlyBuffer()
    {
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.Red);

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            WriteBgra(data.Scan0, Color.Blue);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void UnlockBitsWritesBackReadWriteBuffer()
    {
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.Red);

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            WriteBgra(data.Scan0, Color.Blue);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void FlushPreservesExistingBitmapPixels()
    {
        using var bitmap = new Bitmap(4, 4);
        bitmap.SetPixel(0, 0, Color.Red);

        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.Blue))
        {
            graphics.FillRectangle(brush, 2, 2, 2, 2);
        }

        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(3, 3).ToArgb());
    }

    [Fact]
    public void FirstFlushClearsUndefinedBitmapPixels()
    {
        using var bitmap = new Bitmap(4, 4);

        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.Blue))
        {
            graphics.FillRectangle(brush, 2, 2, 2, 2);
        }

        Assert.Equal(Color.FromArgb(0, 0, 0, 0).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(3, 3).ToArgb());
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

    private static void WriteBgra(IntPtr scan0, Color color)
    {
        Marshal.WriteByte(scan0, 0, color.B);
        Marshal.WriteByte(scan0, 1, color.G);
        Marshal.WriteByte(scan0, 2, color.R);
        Marshal.WriteByte(scan0, 3, color.A);
    }
}
