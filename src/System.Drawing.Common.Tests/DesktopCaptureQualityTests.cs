using System.ComponentModel;
using System.Drawing;
using ProGPU.SystemDrawing;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopCaptureServiceCollection
{
    public const string Name = "Desktop capture service";
}

[Collection(DesktopCaptureServiceCollection.Name)]
public sealed class DesktopCaptureQualityTests
{
    [Fact]
    public void EveryCanonicalOverloadUsesTheTypedOwnedCapturePath()
    {
        var service = new TestDesktopCaptureService();
        using IDisposable registration = DesktopCaptureServices.Register(service);
        using var target = new Bitmap(10, 10);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.CopyFromScreen(new Point(10, 20), new Point(0, 0), new Size(2, 2));
            graphics.CopyFromScreen(
                new Point(30, 40),
                new Point(2, 0),
                new Size(2, 2),
                CopyPixelOperation.SourceCopy);
            graphics.CopyFromScreen(50, 60, 0, 2, new Size(2, 2));
            graphics.CopyFromScreen(
                70,
                80,
                2,
                2,
                new Size(2, 2),
                CopyPixelOperation.SourceCopy |
                CopyPixelOperation.CaptureBlt |
                CopyPixelOperation.NoMirrorBitmap);
        }

        Assert.Equal(
            [
                new Rectangle(10, 20, 2, 2),
                new Rectangle(30, 40, 2, 2),
                new Rectangle(50, 60, 2, 2),
                new Rectangle(70, 80, 2, 2),
            ],
            service.Requests);
        Assert.Equal(Color.FromArgb(255, 10, 20, 30), target.GetPixel(0, 0));
        Assert.Equal(Color.FromArgb(255, 31, 41, 72), target.GetPixel(3, 1));
        Assert.Equal(Color.FromArgb(255, 51, 61, 112), target.GetPixel(1, 3));
        Assert.Equal(Color.FromArgb(255, 71, 81, 152), target.GetPixel(3, 3));
    }

    [Fact]
    public void CaptureOwnsPixelsAfterProviderReturns()
    {
        var service = new TestDesktopCaptureService();
        using IDisposable registration = DesktopCaptureServices.Register(service);
        using var target = new Bitmap(3, 3);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.CopyFromScreen(5, 7, 1, 1, new Size(1, 1));
            service.ClearLastSource();
        }

        Assert.Equal(Color.FromArgb(255, 5, 7, 12), target.GetPixel(1, 1));
    }

    [Fact]
    public void RegistrationHasSingleOwnerAndRestoresUnsupportedBoundary()
    {
        Assert.False(DesktopCaptureServices.IsRegistered);
        var first = new TestDesktopCaptureService();
        using (IDisposable registration = DesktopCaptureServices.Register(first))
        {
            Assert.True(DesktopCaptureServices.IsRegistered);
            Assert.Throws<InvalidOperationException>(() =>
                DesktopCaptureServices.Register(new TestDesktopCaptureService()));
        }

        Assert.False(DesktopCaptureServices.IsRegistered);
        using var target = new Bitmap(1, 1);
        using Graphics graphics = Graphics.FromImage(target);
        Assert.Throws<PlatformNotSupportedException>(() =>
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(1, 1)));
    }

    [Fact]
    public void ValidationOccursBeforeCaptureAndUnsupportedRopsStayExplicit()
    {
        using var target = new Bitmap(2, 2);
        using Graphics graphics = Graphics.FromImage(target);

        Assert.Throws<InvalidEnumArgumentException>(() =>
            graphics.CopyFromScreen(0, 0, 0, 0, Size.Empty, (CopyPixelOperation)123));
        Assert.Throws<ArgumentException>(() =>
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(-1, 1)));
        Assert.Throws<NotSupportedException>(() =>
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(1, 1), CopyPixelOperation.SourceInvert));

        graphics.CopyFromScreen(0, 0, 0, 0, Size.Empty);
        graphics.Dispose();
        Assert.Throws<ArgumentException>(() =>
            graphics.CopyFromScreen(0, 0, 0, 0, Size.Empty));
    }

    [Fact]
    public void WarmedSourceCopyHasBoundedManagedAllocation()
    {
        using IDisposable registration = DesktopCaptureServices.Register(new FillingDesktopCaptureService());
        using var target = new Bitmap(16, 16);
        using Graphics graphics = Graphics.FromImage(target);
        graphics.CopyFromScreen(0, 0, 0, 0, target.Size);
        _ = target.GetPixel(0, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 16; iteration++)
        {
            graphics.CopyFromScreen(iteration, iteration, 0, 0, target.Size);
            _ = target.GetPixel(0, 0);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 65_536);
    }

    private sealed class TestDesktopCaptureService : IDesktopCaptureService
    {
        private byte[]? _lastSource;

        public List<Rectangle> Requests { get; } = [];

        public void Capture(Rectangle sourceRectangle, Span<byte> destinationRgba)
        {
            Requests.Add(sourceRectangle);
            byte[] source = new byte[destinationRgba.Length];
            for (int y = 0; y < sourceRectangle.Height; y++)
            {
                for (int x = 0; x < sourceRectangle.Width; x++)
                {
                    int offset = (y * sourceRectangle.Width + x) * 4;
                    byte red = checked((byte)(sourceRectangle.X + x));
                    byte green = checked((byte)(sourceRectangle.Y + y));
                    source[offset] = red;
                    source[offset + 1] = green;
                    source[offset + 2] = checked((byte)(red + green));
                    source[offset + 3] = byte.MaxValue;
                }
            }

            source.CopyTo(destinationRgba);
            _lastSource = source;
        }

        public void ClearLastSource() => Array.Clear(_lastSource!);
    }

    private sealed class FillingDesktopCaptureService : IDesktopCaptureService
    {
        public void Capture(Rectangle sourceRectangle, Span<byte> destinationRgba)
        {
            for (int offset = 0; offset < destinationRgba.Length; offset += 4)
            {
                destinationRgba[offset] = 20;
                destinationRgba[offset + 1] = 40;
                destinationRgba[offset + 2] = 60;
                destinationRgba[offset + 3] = byte.MaxValue;
            }
        }
    }
}
