using System.Drawing;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.SystemDrawing;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeDrawingInteropServiceCollection
{
    public const string Name = "Native drawing interop services";
}

[Collection(NativeDrawingInteropServiceCollection.Name)]
public sealed class NativeDrawingInteropQualityTests
{
    [Fact]
    public void CanonicalAndInternalEntriesRouteExactNativeInputs()
    {
        var fontService = new TestNativeFontInteropService();
        var graphicsService = new TestNativeGraphicsInteropService();
        using IDisposable fontRegistration = NativeFontInteropServices.Register(fontService);
        using IDisposable graphicsRegistration = NativeGraphicsInteropServices.Register(graphicsService);

        using Font font = Font.FromHdc((IntPtr)10);
        using Graphics hdc = Graphics.FromHdc((IntPtr)11);
        using Graphics hdcAndDevice = Graphics.FromHdc((IntPtr)12, (IntPtr)13);
        using Graphics internalHdc = Graphics.FromHdcInternal((IntPtr)14);
        using Graphics window = Graphics.FromHwnd((IntPtr)21);
        using Graphics desktop = Graphics.FromHwndInternal(IntPtr.Zero);
        IntPtr palette = Graphics.GetHalftonePalette();

        Assert.Equal([(IntPtr)10], fontService.DeviceContexts);
        Assert.Equal(
            [((IntPtr)11, IntPtr.Zero), ((IntPtr)12, (IntPtr)13), ((IntPtr)14, IntPtr.Zero)],
            graphicsService.DeviceContexts);
        Assert.Equal([(IntPtr)21, IntPtr.Zero], graphicsService.Windows);
        Assert.Equal((IntPtr)909, palette);
        Assert.Equal(1, graphicsService.PaletteRequests);
        Assert.Equal(11f, font.Size);
        Assert.All(
            [hdc, hdcAndDevice, internalHdc, window, desktop],
            static graphics => Assert.Empty(graphics.DrawingContext.Commands));
    }

    [Fact]
    public void AdapterSuppliedGraphicsRetainsTypedBoundsTransformAndLifetime()
    {
        var service = new TestNativeGraphicsInteropService();
        using IDisposable registration = NativeGraphicsInteropServices.Register(service);
        using var pen = new Pen(Color.Red, 2f);

        Graphics graphics = Graphics.FromHwnd((IntPtr)44);
        graphics.DrawLine(pen, 1f, 2f, 3f, 4f);

        Assert.Equal(new RectangleF(-4f, -5f, 320f, 200f), graphics.VisibleClipBounds);
        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(Matrix4x4.CreateTranslation(4f, 5f, 0f), command.Transform);
        Assert.Equal(0, service.Completed);

        graphics.Dispose();
        graphics.Dispose();
        Assert.Equal(1, service.Completed);
    }

    [Fact]
    public void RegistrationsHaveSingleOwnersAndRestoreUnsupportedBoundaries()
    {
        Assert.False(NativeFontInteropServices.IsRegistered);
        Assert.False(NativeGraphicsInteropServices.IsRegistered);

        using (NativeFontInteropServices.Register(new TestNativeFontInteropService()))
        {
            Assert.True(NativeFontInteropServices.IsRegistered);
            Assert.Throws<InvalidOperationException>(() =>
                NativeFontInteropServices.Register(new TestNativeFontInteropService()));
        }

        using (NativeGraphicsInteropServices.Register(new TestNativeGraphicsInteropService()))
        {
            Assert.True(NativeGraphicsInteropServices.IsRegistered);
            Assert.Throws<InvalidOperationException>(() =>
                NativeGraphicsInteropServices.Register(new TestNativeGraphicsInteropService()));
        }

        Assert.False(NativeFontInteropServices.IsRegistered);
        Assert.False(NativeGraphicsInteropServices.IsRegistered);
        Assert.Throws<PlatformNotSupportedException>(() => Font.FromHdc((IntPtr)1));
        Assert.Throws<PlatformNotSupportedException>(() => Graphics.FromHdc((IntPtr)1));
        Assert.Throws<PlatformNotSupportedException>(() => Graphics.FromHwnd(IntPtr.Zero));
        Assert.Throws<PlatformNotSupportedException>(() => Graphics.GetHalftonePalette());
    }

    [Fact]
    public void ValidationPrecedesCapabilityLookupAndNullProductsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Font.FromHdc(IntPtr.Zero));
        Assert.Throws<ArgumentException>(() => Graphics.FromHdc(IntPtr.Zero));
        Assert.Throws<ArgumentException>(() => Graphics.FromHdc(IntPtr.Zero, (IntPtr)2));
        Assert.Throws<ArgumentException>(() => Graphics.FromHdcInternal(IntPtr.Zero));

        using (NativeFontInteropServices.Register(new NullNativeFontInteropService()))
        {
            Assert.Throws<InvalidOperationException>(() => Font.FromHdc((IntPtr)1));
        }

        using (NativeGraphicsInteropServices.Register(new NullNativeGraphicsInteropService()))
        {
            Assert.Throws<InvalidOperationException>(() => Graphics.FromHdc((IntPtr)1));
            Assert.Throws<InvalidOperationException>(() => Graphics.FromHwnd(IntPtr.Zero));
        }
    }

    [Fact]
    public void WarmedPaletteDispatchAllocatesNoManagedMemory()
    {
        using IDisposable registration = NativeGraphicsInteropServices.Register(
            new TestNativeGraphicsInteropService());
        _ = Graphics.GetHalftonePalette();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            _ = Graphics.GetHalftonePalette();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private sealed class TestNativeFontInteropService : INativeFontInteropService
    {
        public List<IntPtr> DeviceContexts { get; } = [];

        public Font ImportFromDeviceContext(IntPtr deviceContext)
        {
            DeviceContexts.Add(deviceContext);
            using FontFamily family = FontFamily.GenericSansSerif;
            return new Font(family, 11f);
        }
    }

    private sealed class TestNativeGraphicsInteropService : INativeGraphicsInteropService
    {
        public List<(IntPtr DeviceContext, IntPtr Device)> DeviceContexts { get; } = [];

        public List<IntPtr> Windows { get; } = [];

        public int PaletteRequests { get; private set; }

        public int Completed { get; private set; }

        public Graphics CreateFromDeviceContext(IntPtr deviceContext, IntPtr device)
        {
            DeviceContexts.Add((deviceContext, device));
            return CreateGraphics();
        }

        public Graphics CreateFromWindow(IntPtr window)
        {
            Windows.Add(window);
            return CreateGraphics();
        }

        public IntPtr CreateHalftonePalette()
        {
            PaletteRequests++;
            return (IntPtr)909;
        }

        private Graphics CreateGraphics()
            => Graphics.FromProGpuDrawingContext(
                new DrawingContext(),
                new RectangleF(0f, 0f, 320f, 200f),
                Matrix4x4.CreateTranslation(4f, 5f, 0f),
                () => Completed++);
    }

    private sealed class NullNativeFontInteropService : INativeFontInteropService
    {
        public Font ImportFromDeviceContext(IntPtr deviceContext) => null!;
    }

    private sealed class NullNativeGraphicsInteropService : INativeGraphicsInteropService
    {
        public Graphics CreateFromDeviceContext(IntPtr deviceContext, IntPtr device) => null!;

        public Graphics CreateFromWindow(IntPtr window) => null!;

        public IntPtr CreateHalftonePalette() => IntPtr.Zero;
    }
}
