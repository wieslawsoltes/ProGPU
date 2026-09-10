using Xunit;

namespace System.Drawing.Tests;

public sealed class ImageConvenienceQualityTests
{
    [Fact]
    public void ThumbnailHasRequestedSizeAndScaledPixels()
    {
        using var source = new Bitmap(2, 2);
        source.SetPixel(0, 0, Color.Red);
        source.SetPixel(1, 0, Color.Green);
        source.SetPixel(0, 1, Color.Blue);
        source.SetPixel(1, 1, Color.White);

        using Image thumbnail = source.GetThumbnailImage(8, 6, null, IntPtr.Zero);

        Bitmap bitmap = Assert.IsType<Bitmap>(thumbnail);
        Assert.Equal(new Size(8, 6), thumbnail.Size);
        Assert.NotEqual(0, bitmap.GetPixel(1, 1).A);
        Assert.NotEqual(0, bitmap.GetPixel(6, 4).A);
    }

    [Fact]
    public void ThumbnailCompatibilityCallbackIsNotInvoked()
    {
        using var source = new Bitmap(2, 2);
        int callbackCount = 0;

        using Image thumbnail = source.GetThumbnailImage(
            1,
            1,
            () =>
            {
                callbackCount++;
                return true;
            },
            new IntPtr(42));

        Assert.Equal(0, callbackCount);
        Assert.Equal(new Size(1, 1), thumbnail.Size);
    }

    [Fact]
    public void WarmedThumbnailCreationHasBoundedAllocation()
    {
        using var source = new Bitmap(8, 8);
        using (Image warmup = source.GetThumbnailImage(4, 4, null, IntPtr.Zero))
        {
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 32; index++)
        {
            using Image thumbnail = source.GetThumbnailImage(4, 4, null, IntPtr.Zero);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 4_608 * 32);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void ThumbnailRejectsNonPositiveDimensions(int width, int height)
    {
        using var source = new Bitmap(2, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            source.GetThumbnailImage(width, height, null, IntPtr.Zero));
    }

    [Fact]
    public void ThumbnailRejectsImagesWithoutTypedBitmapPixels()
    {
        using var source = new TestImage();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            source.GetThumbnailImage(1, 1, null, IntPtr.Zero));

        Assert.Contains("bitmap-backed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DrawIconCoordinatesPreserveNativeSizeAndPixels()
    {
        using var source = new Bitmap(2, 2);
        source.SetPixel(0, 0, Color.Red);
        source.SetPixel(1, 0, Color.Green);
        source.SetPixel(0, 1, Color.Blue);
        source.SetPixel(1, 1, Color.White);
        using var encoded = new MemoryStream();
        source.Save(encoded, Imaging.ImageFormat.Png);
        encoded.Position = 0;
        using var icon = new Icon(encoded);
        using var target = new Bitmap(6, 6);

        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawIcon(icon, 2, 3);
        }

        Assert.Equal(0, target.GetPixel(1, 2).A);
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(2, 3).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(3, 3).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(2, 4).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(3, 4).ToArgb());
        Assert.Equal(0, target.GetPixel(4, 5).A);
    }

    [Fact]
    public void DrawIconCoordinatesValidateNullBeforeRecording()
    {
        using var target = new Bitmap(2, 2);
        using Graphics graphics = Graphics.FromImage(target);

        Assert.Throws<ArgumentNullException>(() => graphics.DrawIcon(null!, 0, 0));
        Assert.Empty(graphics.DrawingContext.Commands);
    }

    private sealed class TestImage : Image
    {
        public override int Width => 1;
        public override int Height => 1;
        public override void Dispose()
        {
        }
    }
}
