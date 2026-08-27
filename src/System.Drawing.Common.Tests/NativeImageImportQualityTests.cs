using System.Drawing;
using ProGPU.SystemDrawing;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeImageImportServiceCollection
{
    public const string Name = "Native image import service";
}

[Collection(NativeImageImportServiceCollection.Name)]
public sealed class NativeImageImportQualityTests
{
    [Fact]
    public void CanonicalBitmapAndIconEntriesUseTypedOwnedImports()
    {
        var service = new TestNativeImageImportService();
        using IDisposable registration = NativeImageImportServices.Register(service);

        using Bitmap iconBitmap = Bitmap.FromHicon((IntPtr)101);
        using Bitmap resourceBitmap = Bitmap.FromResource((IntPtr)202, "APP_BACKGROUND");
        using Icon icon = Icon.FromHandle((IntPtr)303);
        using Bitmap iconCopy = icon.ToBitmap();

        Assert.Equal([(IntPtr)101, (IntPtr)303], service.IconHandles);
        Assert.Equal([((IntPtr)202, "APP_BACKGROUND")], service.Resources);
        Assert.Equal(new Size(2, 2), iconBitmap.Size);
        Assert.Equal(Color.FromArgb(255, 101, 11, 112), iconBitmap.GetPixel(0, 0));
        Assert.Equal(Color.FromArgb(255, 202, 22, 224), resourceBitmap.GetPixel(1, 1));
        Assert.Equal(Color.FromArgb(255, 47, 11, 58), iconCopy.GetPixel(0, 0));
    }

    [Fact]
    public void DestinationCopiesProviderPixelsSynchronously()
    {
        byte[] providerPixels = CreatePixels(1, 1, 7, 8, 9);
        NativeImageImportDestination? retainedDestination = null;
        using IDisposable registration = NativeImageImportServices.Register(
            new DelegateNativeImageImportService(destination =>
            {
                retainedDestination = destination;
                destination.SetRgba(1, 1, providerPixels);
                Array.Clear(providerPixels);
            }));

        using Bitmap bitmap = Bitmap.FromHicon((IntPtr)1);

        Assert.Equal(Color.FromArgb(255, 7, 8, 9), bitmap.GetPixel(0, 0));
        Assert.Throws<InvalidOperationException>(() =>
            retainedDestination!.SetRgba(1, 1, CreatePixels(1, 1, 1, 2, 3)));
    }

    [Fact]
    public void DestinationRequiresOneExactPositiveImage()
    {
        using (NativeImageImportServices.Register(
            new DelegateNativeImageImportService(_ => { })))
        {
            Assert.Throws<InvalidOperationException>(() => Bitmap.FromHicon((IntPtr)1));
        }

        using (NativeImageImportServices.Register(
            new DelegateNativeImageImportService(destination =>
            {
                destination.SetRgba(1, 1, CreatePixels(1, 1, 1, 2, 3));
                destination.SetRgba(1, 1, CreatePixels(1, 1, 4, 5, 6));
            })))
        {
            Assert.Throws<InvalidOperationException>(() => Bitmap.FromHicon((IntPtr)1));
        }

        using (NativeImageImportServices.Register(
            new DelegateNativeImageImportService(destination =>
                destination.SetRgba(2, 2, new byte[15]))))
        {
            Assert.Throws<ArgumentException>(() => Bitmap.FromHicon((IntPtr)1));
        }
    }

    [Fact]
    public void RegistrationHasOneOwnerAndValidationPrecedesCapabilityLookup()
    {
        Assert.False(NativeImageImportServices.IsRegistered);
        using (IDisposable registration = NativeImageImportServices.Register(
            new TestNativeImageImportService()))
        {
            Assert.True(NativeImageImportServices.IsRegistered);
            Assert.Throws<InvalidOperationException>(() =>
                NativeImageImportServices.Register(new TestNativeImageImportService()));
        }

        Assert.False(NativeImageImportServices.IsRegistered);
        Assert.Throws<ArgumentException>(() => Bitmap.FromHicon(IntPtr.Zero));
        Assert.Throws<ArgumentNullException>(() => Bitmap.FromResource(IntPtr.Zero, null!));
        Assert.Throws<PlatformNotSupportedException>(() => Bitmap.FromHicon((IntPtr)1));
        Assert.Throws<PlatformNotSupportedException>(() =>
            Bitmap.FromResource(IntPtr.Zero, "CURRENT_MODULE_RESOURCE"));
    }

    [Fact]
    public void WarmedIconImportHasBoundedManagedAllocation()
    {
        byte[] pixels = CreatePixels(16, 16, 20, 40, 60);
        using IDisposable registration = NativeImageImportServices.Register(
            new DelegateNativeImageImportService(destination =>
                destination.SetRgba(16, 16, pixels)));
        using (Bitmap warmup = Bitmap.FromHicon((IntPtr)1))
        {
            _ = warmup.GetPixel(0, 0);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 16; iteration++)
        {
            using Bitmap bitmap = Bitmap.FromHicon((IntPtr)(iteration + 1));
            _ = bitmap.GetPixel(0, 0);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 32_768);
    }

    private static byte[] CreatePixels(
        int width,
        int height,
        byte red,
        byte green,
        byte blue)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
            pixels[offset + 3] = byte.MaxValue;
        }

        return pixels;
    }

    private sealed class TestNativeImageImportService : INativeImageImportService
    {
        public List<IntPtr> IconHandles { get; } = [];

        public List<(IntPtr Module, string Name)> Resources { get; } = [];

        public void ImportIcon(
            IntPtr iconHandle,
            NativeImageImportDestination destination)
        {
            IconHandles.Add(iconHandle);
            destination.SetRgba(
                2,
                2,
                CreatePixels(
                    2,
                    2,
                    unchecked((byte)iconHandle.ToInt64()),
                    11,
                    unchecked((byte)(iconHandle.ToInt64() + 11))));
        }

        public void ImportBitmapResource(
            IntPtr moduleHandle,
            string resourceName,
            NativeImageImportDestination destination)
        {
            Resources.Add((moduleHandle, resourceName));
            destination.SetRgba(
                2,
                2,
                CreatePixels(
                    2,
                    2,
                    unchecked((byte)moduleHandle.ToInt64()),
                    22,
                    unchecked((byte)(moduleHandle.ToInt64() + 22))));
        }
    }

    private sealed class DelegateNativeImageImportService(
        Action<NativeImageImportDestination> import) : INativeImageImportService
    {
        public void ImportIcon(
            IntPtr iconHandle,
            NativeImageImportDestination destination)
            => import(destination);

        public void ImportBitmapResource(
            IntPtr moduleHandle,
            string resourceName,
            NativeImageImportDestination destination)
            => import(destination);
    }
}
