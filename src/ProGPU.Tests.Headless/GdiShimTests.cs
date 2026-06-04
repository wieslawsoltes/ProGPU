using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using Xunit;

namespace ProGPU.Tests.Headless;

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
}
