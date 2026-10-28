using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed unsafe class SkImageFinalizerRetirementTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void AbandonedImageOwnershipQueuesWhileRenderLockIsHeld(bool useDawn, int ownerKind)
    {
        using var device = new OwnedDevice(useDawn);
        var observation = new Observation(device.Context);
        GpuTexture.OnDisposedWithDevice += observation.Notice;
        try
        {
            Exception? failure = null;
            var collector = new Thread(() =>
            {
                try { Collect(); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true, Name = "Retained image finalization" };
            bool completedWhileLocked;
            lock (device.Context.RenderLock)
            {
                DropOwner(observation, ownerKind, explicitDispose: false);
                collector.Start();
                completedWhileLocked = collector.Join(Deadline);
            }
            Assert.True(collector.Join(Deadline), "Finalization did not finish after releasing RenderLock.");
            Assert.True(completedWhileLocked, "A retained image finalizer waited for RenderLock.");
            Assert.Null(failure);
            Assert.Equal(1, observation.FinalizerNotifications);
            Assert.Equal(0, observation.CreatingNotifications);
            Assert.True(observation.Texture.IsDisposed);
            Assert.False(observation.Owner.IsAlive);
            lock (device.Context.DisposalLock)
            {
                Assert.Contains(observation.NativeTexture, device.Context.PendingTextures);
                Assert.Contains(observation.NativeView, device.Context.PendingTextureViews);
            }
            device.Context.CleanupPendingResources();
            GC.KeepAlive(observation.Texture);
        }
        finally { GpuTexture.OnDisposedWithDevice -= observation.Notice; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitImageDisposalKeepsCreatingThreadNotification(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        var observation = new Observation(device.Context);
        GpuTexture.OnDisposedWithDevice += observation.Notice;
        try
        {
            lock (device.Context.RenderLock)
                DropOwner(observation, 0, explicitDispose: true);
            Collect();
            Assert.Equal(1, observation.CreatingNotifications);
            Assert.Equal(0, observation.FinalizerNotifications);
            Assert.True(observation.Texture.IsDisposed);
            Assert.False(observation.Owner.IsAlive);
            device.Context.CleanupPendingResources();
            GC.KeepAlive(observation.Texture);
        }
        finally { GpuTexture.OnDisposedWithDevice -= observation.Notice; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonedImagePreservesTheLastRetainedLease(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        var observation = new Observation(device.Context);
        GpuTexture.OnDisposedWithDevice += observation.Notice;
        try
        {
            using var lease = DropLeasedImage(observation);
            Collect();
            Assert.False(observation.Owner.IsAlive);
            Assert.False(lease.Texture.IsDisposed);
            Assert.Equal(0, observation.FinalizerNotifications);
            Assert.Equal(0, observation.CreatingNotifications);
            lease.Dispose();
            lease.Dispose();
            Assert.True(lease.Texture.IsDisposed);
            Assert.Equal(1, observation.CreatingNotifications);
            Assert.Equal(0, observation.FinalizerNotifications);
            device.Context.CleanupPendingResources();
        }
        finally { GpuTexture.OnDisposedWithDevice -= observation.Notice; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonedBorrowedImageLeavesTheOwnerTextureAlive(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var texture = new GpuTexture(device.Context, 2, 2, TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc,
            "Borrowed image finalization control");
        var owner = DropBorrowedImage(texture);
        Collect();
        Assert.False(owner.IsAlive);
        Assert.False(texture.IsDisposed);
        Assert.True(texture.TexturePtr != null);
        Assert.True(texture.ViewPtr != null);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void AbandonedPortableImageQueuesItsActualContextCache(bool useDawn, int ownerKind)
    {
        using var source = new OwnedDevice(useDawn);
        using var target = new OwnedDevice(useDawn);
        WgpuContext.Current = source.Context;
        var observation = new Observation(target.Context);
        GpuTexture.OnDisposedWithDevice += observation.Notice;
        try
        {
            Exception? failure = null;
            var collector = new Thread(() =>
            {
                try { Collect(); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true, Name = "Portable image finalization" };
            bool completedWhileLocked;
            lock (target.Context.RenderLock)
            {
                DropPortableOwner(observation, ownerKind, explicitDispose: false);
                collector.Start();
                completedWhileLocked = collector.Join(Deadline);
            }
            Assert.True(collector.Join(Deadline), "Portable image finalization did not finish after releasing RenderLock.");
            Assert.True(completedWhileLocked, "A portable image cache finalizer waited for its actual context RenderLock.");
            Assert.Null(failure);
            Assert.Equal(1, observation.FinalizerNotifications);
            Assert.Equal(0, observation.CreatingNotifications);
            Assert.True(observation.Texture.IsDisposed);
            Assert.False(observation.Owner.IsAlive);
            lock (target.Context.DisposalLock)
            {
                Assert.Contains(observation.NativeTexture, target.Context.PendingTextures);
                Assert.Contains(observation.NativeView, target.Context.PendingTextureViews);
            }
            target.Context.CleanupPendingResources();
            GC.KeepAlive(observation.Texture);
            GC.KeepAlive(observation.RetainedImage);
        }
        finally { GpuTexture.OnDisposedWithDevice -= observation.Notice; }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public void ExplicitPortableImageDisposalKeepsCreatingThreadNotification(bool useDawn, int ownerKind)
    {
        using var source = new OwnedDevice(useDawn);
        using var target = new OwnedDevice(useDawn);
        WgpuContext.Current = source.Context;
        var observation = new Observation(target.Context);
        GpuTexture.OnDisposedWithDevice += observation.Notice;
        try
        {
            lock (target.Context.RenderLock)
                DropPortableOwner(observation, ownerKind, explicitDispose: true);
            Collect();
            Assert.Equal(1, observation.CreatingNotifications);
            Assert.Equal(0, observation.FinalizerNotifications);
            Assert.True(observation.Texture.IsDisposed);
            Assert.False(observation.Owner.IsAlive);
            target.Context.CleanupPendingResources();
            GC.KeepAlive(observation.Texture);
            GC.KeepAlive(observation.RetainedImage);
        }
        finally { GpuTexture.OnDisposedWithDevice -= observation.Notice; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropPortableOwner(Observation observation, int kind, bool explicitDispose)
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        var image = SKImage.FromBitmap(bitmap);
        SKObject owner = image;
        if (kind >= 2)
        {
            var shader = image.ToShader();
            image.Dispose();
            image = shader.Image!.Image;
            owner = shader;
            if (kind == 3)
            {
                owner = shader.WithLocalMatrix(SKMatrix.CreateTranslation(1, 2));
                shader.Dispose();
            }
            // Keep the actual owned wrapper alive independently. Its own
            // finalizer must not hide the nested shader's release path.
            observation.RetainedImage = image;
        }
        Assert.NotSame(image.Texture.Context, observation.Context);
        observation.Texture = image.GetTextureForContext(observation.Context);
        Assert.Same(observation.Context, observation.Texture.Context);
        Assert.Same(observation.Texture, image.GetTextureForContext(observation.Context));
        observation.NativeTexture = (nint)observation.Texture.TexturePtr;
        observation.NativeView = (nint)observation.Texture.ViewPtr;
        Assert.NotEqual((nint)0, observation.NativeTexture);
        Assert.NotEqual((nint)0, observation.NativeView);
        observation.Owner = new WeakReference(owner, trackResurrection: true);
        if (explicitDispose) owner.Dispose();
        GC.KeepAlive(owner);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RasterImageReleasesItsActualPin(bool useDawn, bool explicitDispose)
    {
        using var device = new OwnedDevice(useDawn);
        var raster = new RasterObservation();
        try
        {
            DropRasterImage(raster, explicitDispose);
            Collect();
            Assert.False(raster.Image.IsAlive);
            Assert.False(raster.Handle.IsAllocated, "The abandoned image retained its actual raster GCHandle.");
            Assert.Null(raster.PixelsField.GetValue(raster.State));
        }
        finally { raster.ReleaseObservedPin(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedPixmapRetainsImageAndPinUntilPixmapDisposal(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        var raster = new RasterObservation();
        using var pixmap = DropRasterImage(raster, explicitDispose: false, retainPixmap: true);
        try
        {
            Collect();
            Assert.True(raster.Image.IsAlive);
            Assert.True(raster.Handle.IsAllocated);
            Assert.Equal(SKColors.Red, pixmap!.GetPixelColor(0, 0));
            pixmap.Dispose();
            Collect();
            Assert.False(raster.Image.IsAlive);
            Assert.False(raster.Handle.IsAllocated);
        }
        finally
        {
            pixmap?.Dispose();
            raster.ReleaseObservedPin();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RasterImageInvokesItsOriginalReleaseCallbackOnce(bool useDawn, bool explicitDispose)
    {
        using var device = new OwnedDevice(useDawn);
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        using var source = bitmap.PeekPixels();
        var observation = new RasterReleaseObservation();
        var image = DropRasterReleaseImage(source, observation, explicitDispose);
        Collect();
        Assert.False(image.IsAlive);
        Assert.Equal(1, observation.Count);
        Assert.Equal(source.GetPixels(), observation.Pixels);
        Assert.Equal(explicitDispose, observation.Thread == Environment.CurrentManagedThreadId);
        GC.KeepAlive(bitmap);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropRasterReleaseImage(SKPixmap source, RasterReleaseObservation observation, bool explicitDispose)
    {
        var image = SKImage.FromPixels(source, static (pixels, context) =>
        {
            var state = (RasterReleaseObservation)context;
            state.Pixels = pixels;
            state.Thread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref state.Count);
        }, observation);
        var weak = new WeakReference(image, trackResurrection: true);
        if (explicitDispose) image.Dispose();
        GC.KeepAlive(image);
        return weak;
    }

    private sealed class RasterReleaseObservation
    {
        internal int Count, Thread;
        internal IntPtr Pixels;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RasterReleaseCallbackExceptionsPreserveDisposalContext(bool useDawn, bool explicitDispose)
    {
        using var device = new OwnedDevice(useDawn);
        using var bitmap = new SKBitmap(2, 2);
        using var source = bitmap.PeekPixels();
        var observation = new RasterReleaseObservation();
        var image = DropThrowingRasterReleaseImage(source, observation, explicitDispose);
        Collect();
        Assert.False(image.IsAlive);
        Assert.Equal(1, observation.Count);
        Assert.Equal(source.GetPixels(), observation.Pixels);
        Assert.Equal(explicitDispose, observation.Thread == Environment.CurrentManagedThreadId);
        GC.KeepAlive(bitmap);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropThrowingRasterReleaseImage(SKPixmap source, RasterReleaseObservation observation, bool explicitDispose)
    {
        var image = SKImage.FromPixels(source, static (pixels, context) =>
        {
            var state = (RasterReleaseObservation)context;
            state.Pixels = pixels;
            state.Thread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref state.Count);
            throw new InvalidOperationException("Owned release callback control");
        }, observation);
        var weak = new WeakReference(image, trackResurrection: true);
        if (explicitDispose)
            Assert.Throws<InvalidOperationException>(() => image.Dispose());
        GC.KeepAlive(image);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SKPixmap? DropRasterImage(RasterObservation observation, bool explicitDispose, bool retainPixmap = false)
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        var image = SKImage.FromBitmap(bitmap);
        var pixmap = image.PeekPixels();
        Assert.NotNull(pixmap);
        Assert.Equal(SKColors.Red, pixmap.GetPixelColor(0, 0));
        // Inspect only in this test. Retain the actual private owner, so its
        // handle cannot be confused with a GC-wide pin count or another image.
        observation.State = typeof(SKImage).GetField("_optionalState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(image)!;
        observation.HandleField = observation.State.GetType().GetField("RasterPixelsHandle")!;
        observation.PixelsField = observation.State.GetType().GetField("RasterPixels")!;
        Assert.True(observation.Handle.IsAllocated);
        observation.Image = new WeakReference(image, trackResurrection: true);
        if (!retainPixmap) pixmap.Dispose();
        if (explicitDispose) image.Dispose();
        GC.KeepAlive(image);
        return retainPixmap ? pixmap : null;
    }

    private sealed class RasterObservation
    {
        internal object State = null!;
        internal FieldInfo HandleField = null!, PixelsField = null!;
        internal WeakReference Image = null!;
        internal GCHandle Handle => (GCHandle)HandleField.GetValue(State)!;
        internal void ReleaseObservedPin()
        {
            // Negative controls must not leak a real pinned buffer after their
            // assertion. This runs after observation and never frees a borrowed
            // pointer while its image or pixmap remains usable.
            if (HandleField is null) return;
            var handle = Handle;
            if (!handle.IsAllocated) return;
            handle.Free();
            HandleField.SetValue(State, default(GCHandle));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IProGpuTextureLease DropLeasedImage(Observation observation)
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        var image = SKImage.FromBitmap(bitmap);
        Assert.True(image.TextureLeaseSource.TryAcquireGpuTextureLease(out var lease));
        observation.Texture = lease.Texture;
        observation.Owner = new WeakReference(image, trackResurrection: true);
        return lease;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropBorrowedImage(GpuTexture texture)
    {
        var image = SKImage.FromTexture(texture);
        return new WeakReference(image, trackResurrection: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropOwner(Observation observation, int kind, bool explicitDispose)
    {
        SKImage image;
        SKObject owner;
        if (kind == 1)
        {
            using var surface = SKSurface.Create(new SKImageInfo(2, 2));
            surface.Canvas.Clear(SKColors.Red);
            image = surface.Snapshot();
            using var pixels = new SKBitmap(new SKImageInfo(2, 2));
            Assert.True(image.ReadPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0, SKImageCachingHint.Allow));
            Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
            owner = image;
        }
        else
        {
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.Red);
            image = SKImage.FromBitmap(bitmap);
            owner = image;
            if (kind >= 2)
            {
                var shader = image.ToShader();
                image.Dispose();
                image = shader.Image!.Image;
                owner = shader;
                if (kind == 3)
                {
                    owner = shader.WithLocalMatrix(SKMatrix.CreateTranslation(1, 2));
                    shader.Dispose();
                }
            }
        }

        // Retain the actual texture independently so its own finalizer cannot
        // run first and hide the owning-image disposal path.
        observation.Texture = image.Texture;
        Assert.Same(observation.Context, observation.Texture.Context);
        observation.NativeTexture = (nint)observation.Texture.TexturePtr;
        observation.NativeView = (nint)observation.Texture.ViewPtr;
        Assert.NotEqual((nint)0, observation.NativeTexture);
        Assert.NotEqual((nint)0, observation.NativeView);
        observation.Owner = new WeakReference(owner, trackResurrection: true);
        if (explicitDispose) owner.Dispose();
        GC.KeepAlive(owner);
    }

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private sealed class Observation(WgpuContext context)
    {
        private readonly int _creatingThread = Environment.CurrentManagedThreadId;
        internal WgpuContext Context { get; } = context;
        internal GpuTexture Texture = null!;
        internal SKImage? RetainedImage;
        internal WeakReference Owner = null!;
        internal nint NativeTexture, NativeView;
        internal int FinalizerNotifications, CreatingNotifications;
        internal void Notice(WgpuDeviceIdentity device, ulong id)
        {
            if (Texture is null || id != Texture.Id || !ReferenceEquals(device, Context.DeviceIdentity)) return;
            if (Environment.CurrentManagedThreadId == _creatingThread) Interlocked.Increment(ref CreatingNotifications);
            else Interlocked.Increment(ref FinalizerNotifications);
        }
    }

    private sealed class OwnedDevice : IDisposable
    {
        private readonly WgpuContext? _previous = WgpuContext.Current;
        private readonly DawnGpuContext? _dawn;
        internal WgpuContext Context { get; }
        internal OwnedDevice(bool dawn)
        {
            if (dawn)
            {
                BackendType backend = OperatingSystem.IsWindows() ? BackendType.D3D12 :
                    OperatingSystem.IsMacOS() ? BackendType.Metal : BackendType.Vulkan;
                _dawn = DawnGpuContext.CreateOffscreen(backend, forceFallbackAdapter: false);
                Context = _dawn.Context;
            }
            else
            {
                Context = new WgpuContext();
                Context.Initialize(null);
            }
        }
        public void Dispose()
        {
            try { if (_dawn is not null) _dawn.Dispose(); else Context.Dispose(); }
            finally { WgpuContext.Current = _previous; }
        }
    }
}
