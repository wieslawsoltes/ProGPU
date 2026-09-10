using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class BitmapResourceQualityTests
{
    [Fact]
    public void TypeScopedResourceConstructorDecodesEmbeddedPng()
    {
        using var bitmap = new Bitmap(
            typeof(BitmapResourceQualityTests),
            "Resources.ProGpuAvaloniaIcon.png");

        Assert.Equal(new Size(256, 256), bitmap.Size);
        Assert.Equal(PixelFormat.Format32bppArgb, bitmap.PixelFormat);
        Assert.NotEqual(Color.Transparent, bitmap.GetPixel(128, 128));
    }

    [Fact]
    public void TypeScopedResourceConstructorOwnsDecodedPixels()
    {
        Bitmap bitmap = CreateFromEmbeddedResource();

        Assert.Equal(new Size(256, 256), bitmap.Size);
        Assert.NotEqual(Color.Transparent, bitmap.GetPixel(128, 128));

        bitmap.Dispose();
    }

    [Fact]
    public void TypeScopedResourceConstructorValidatesLookupInputs()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Bitmap(null!, "Resources.ProGpuAvaloniaIcon.png"));
        Assert.Throws<ArgumentNullException>(
            () => new Bitmap(typeof(BitmapResourceQualityTests), null!));
        Assert.Throws<ArgumentException>(
            () => new Bitmap(typeof(BitmapResourceQualityTests), string.Empty));
        Assert.Throws<ArgumentException>(
            () => new Bitmap(typeof(BitmapResourceQualityTests), "Resources.Missing.png"));
    }

    private static Bitmap CreateFromEmbeddedResource() =>
        new(typeof(BitmapResourceQualityTests), "Resources.ProGpuAvaloniaIcon.png");
}
