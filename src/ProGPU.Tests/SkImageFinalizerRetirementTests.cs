using System.Runtime.CompilerServices;
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
