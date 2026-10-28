using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed unsafe class SkSurfaceFinalizerRetirementTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly FieldInfo TextureField = Field("_gpuTexture");
    private static readonly FieldInfo PixelsField = Field("_pixels");
    private static readonly FieldInfo OwnsPixelsField = Field("_ownsCpuPixels");
    private static FieldInfo Field(string name) => typeof(SKSurface).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonedOwnedSurfaceQueuesActualTextureWhileRenderLockIsHeld(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        GpuTexture texture;
        WeakReference owner;
        Exception? failure = null;
        var collector = new Thread(() =>
        {
            try { Collect(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true, Name = "Owned surface finalization" };
        bool completed;
        nint nativeTexture, nativeView;
        lock (device.Context.RenderLock)
        {
            owner = DropOwnedSurface(out texture);
            nativeTexture = (nint)texture.TexturePtr;
            nativeView = (nint)texture.ViewPtr;
            collector.Start();
            completed = collector.Join(Deadline);
        }
        Assert.True(collector.Join(Deadline), "Surface collector did not finish after unlocking.");
        Assert.True(completed, "Surface finalization waited for RenderLock.");
        Assert.Null(failure);
        Assert.False(owner.IsAlive);
        Assert.True(texture.IsDisposed, "The surface left its actual independently retained texture alive.");
        lock (device.Context.DisposalLock)
        {
            Assert.Contains(nativeTexture, device.Context.PendingTextures);
            Assert.Contains(nativeView, device.Context.PendingTextureViews);
        }
        device.Context.CleanupPendingResources();
        GC.KeepAlive(texture);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void SurfaceRetiresOwnedCpuBackingAndOriginalCallerCallback(bool useDawn, bool explicitDispose, bool ownsBacking)
    {
        using var device = new OwnedDevice(useDawn);
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        var observation = new RasterObservation();
        SKSurface? finalized = null;
        try
        {
            DropRasterSurface(bitmap, observation, explicitDispose, ownsBacking);
            // One collection deliberately retains the finalized object's raw
            // ownership fields for inspection; it does not keep the owner alive
            // before finalization or infer native frees from an invalid pointer.
            CollectOnce();
            finalized = Assert.IsType<SKSurface>(observation.Surface.Target);
            Assert.Equal(IntPtr.Zero, finalized.Handle);
            Assert.False((bool)OwnsPixelsField.GetValue(finalized)!);
            if (ownsBacking)
            {
                Assert.Equal(IntPtr.Zero, (IntPtr)PixelsField.GetValue(finalized)!);
                Assert.Equal(0, observation.Callbacks);
            }
            else
            {
                Assert.Equal(1, observation.Callbacks);
                Assert.Equal(bitmap.GetPixels(), observation.CallbackPixels);
                Assert.Equal(explicitDispose, observation.CallbackThread == Environment.CurrentManagedThreadId);
                Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0));
            }
        }
        finally
        {
            // Original negative controls must not leave a real HGlobal behind.
            finalized ??= observation.Surface?.Target as SKSurface;
            ReleaseObservedOwnedPixels(finalized);
            GC.KeepAlive(observation.ExplicitOwner);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedSurfaceTextureOutlivesFinalizedWrapper(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var texture = new GpuTexture(device.Context, 2, 2, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst,
            "Borrowed surface retirement control");
        var weak = DropBorrowedSurface(device.Context, texture);
        Collect();
        Assert.False(weak.IsAlive);
        Assert.False(texture.IsDisposed);
        Assert.True(texture.TexturePtr != null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedSnapshotOutlivesFinalizedSurface(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        var weak = DropSnapshottedSurface(out var snapshot);
        using (snapshot)
        {
            Collect();
            Assert.False(weak.IsAlive);
            Assert.False(snapshot.Texture.IsDisposed);
            using var pixels = new SKBitmap(2, 2);
            Assert.True(snapshot.ReadPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0));
            Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonedSurfaceRetiresItsActualReadbackBuffer(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var bitmap = new SKBitmap(2, 2);
        var weak = DropReadbackSurface(bitmap, out var readback);
        try
        {
            Collect();
            Assert.False(weak.IsAlive);
            Assert.Equal(0u, readback.BufferSize);
        }
        finally { readback.Dispose(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SurfaceCallbackExceptionsPreserveDisposalContext(bool useDawn, bool explicitDispose)
    {
        using var device = new OwnedDevice(useDawn);
        using var bitmap = new SKBitmap(2, 2);
        var observation = new RasterObservation();
        var weak = DropThrowingSurface(bitmap, observation, explicitDispose);
        Collect();
        Assert.False(weak.IsAlive);
        Assert.Equal(1, observation.Callbacks);
        Assert.Equal(bitmap.GetPixels(), observation.CallbackPixels);
        Assert.Equal(explicitDispose, observation.CallbackThread == Environment.CurrentManagedThreadId);
        GC.KeepAlive(bitmap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalizedSurfaceRemovesItsClearedWeakFramebufferEntry(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var bitmap = new SKBitmap(2, 2);
        var observation = new RasterObservation();
        DropRasterSurface(bitmap, observation, explicitDispose: false, ownsBacking: false);
        CollectOnce();
        var finalized = Assert.IsType<SKSurface>(observation.Surface.Target);
        Assert.Equal(IntPtr.Zero, finalized.Handle);
        Assert.False(HasFramebufferEntry(bitmap.GetPixels(), out _));
    }

    [Fact]
    public void FramebufferCleanupPreservesTheLiveReplacementOwner()
    {
        // Registry identity control only; neither presenter can invoke GPU work.
        nint address = Marshal.AllocHGlobal(1);
        var previous = new MetadataOnlyPresenter();
        var replacement = new MetadataOnlyPresenter();
        try
        {
            GpuFramebufferPresentationRegistry.Register(address, previous);
            GpuFramebufferPresentationRegistry.Register(address, replacement);
            GpuFramebufferPresentationRegistry.Unregister(address, previous);
            Assert.True(HasFramebufferEntry(address, out var actual));
            Assert.Same(replacement, actual);
        }
        finally
        {
            GpuFramebufferPresentationRegistry.Unregister(address, replacement);
            Marshal.FreeHGlobal(address);
        }
    }

    private static bool HasFramebufferEntry(nint address, out IGpuFramebufferPresenter? presenter)
    {
        var type = typeof(GpuFramebufferPresentationRegistry);
        var sync = type.GetField("s_sync", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var entries = (Dictionary<IntPtr, WeakReference<IGpuFramebufferPresenter>>)type.GetField(
            "s_presenters", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        lock (sync)
        {
            presenter = null;
            if (!entries.TryGetValue(address, out var weak)) return false;
            _ = weak.TryGetTarget(out presenter);
            return true;
        }
    }

    private sealed class MetadataOnlyPresenter : IGpuFramebufferPresenter
    {
        public void Present(WgpuContext context, IntPtr surfaceHandle) =>
            throw new InvalidOperationException("The registry identity control does not present.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropReadbackSurface(SKBitmap bitmap, out GpuTextureReadbackBuffer readback)
    {
        var surface = SKSurface.Create(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
        surface.Canvas.Clear(SKColors.Red);
        surface.Flush();
        readback = Assert.IsType<GpuTextureReadbackBuffer>(Field("_readbackBuffer").GetValue(surface));
        Assert.True(readback.BufferSize > 0);
        return new WeakReference(surface, trackResurrection: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropThrowingSurface(SKBitmap bitmap, RasterObservation observation, bool explicitDispose)
    {
        var surface = SKSurface.Create(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes,
            static (pixels, context) =>
            {
                var state = (RasterObservation)context;
                state.CallbackPixels = pixels;
                state.CallbackThread = Environment.CurrentManagedThreadId;
                Interlocked.Increment(ref state.Callbacks);
                throw new InvalidOperationException("Surface callback control");
            }, observation);
        var weak = new WeakReference(surface, trackResurrection: true);
        if (explicitDispose) Assert.Throws<InvalidOperationException>(() => surface.Dispose());
        GC.KeepAlive(surface);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropOwnedSurface(out GpuTexture texture)
    {
        var surface = SKSurface.Create(new SKImageInfo(2, 2));
        texture = (GpuTexture)TextureField.GetValue(surface)!;
        return new WeakReference(surface, trackResurrection: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropRasterSurface(SKBitmap bitmap, RasterObservation observation, bool explicitDispose, bool ownsBacking)
    {
        SKSurface surface;
        if (ownsBacking)
        {
            surface = SKSurface.Create(bitmap.Info);
            surface.Canvas.Clear(SKColors.Red);
            using var pixels = surface.PeekPixels();
            Assert.Equal(SKColors.Red, pixels.GetPixelColor(0, 0));
            Assert.True((bool)OwnsPixelsField.GetValue(surface)!);
        }
        else
        {
            surface = SKSurface.Create(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes,
                static (pixels, context) =>
                {
                    var state = (RasterObservation)context;
                    state.CallbackPixels = pixels;
                    state.CallbackThread = Environment.CurrentManagedThreadId;
                    Interlocked.Increment(ref state.Callbacks);
                }, observation);
        }
        observation.Surface = new WeakReference(surface, trackResurrection: true);
        if (explicitDispose)
        {
            surface.Dispose();
            observation.ExplicitOwner = surface;
        }
        GC.KeepAlive(surface);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropBorrowedSurface(WgpuContext context, GpuTexture texture)
    {
        using var recording = new GRContext(context);
        using var backend = new GRBackendTexture(texture);
        var surface = SKSurface.Create(recording, backend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888);
        Assert.NotNull(surface);
        return new WeakReference(surface, trackResurrection: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropSnapshottedSurface(out SKImage snapshot)
    {
        var surface = SKSurface.Create(new SKImageInfo(2, 2));
        surface.Canvas.Clear(SKColors.Red);
        snapshot = surface.Snapshot();
        return new WeakReference(surface, trackResurrection: true);
    }

    private static void ReleaseObservedOwnedPixels(SKSurface? surface)
    {
        if (surface is null || !(bool)OwnsPixelsField.GetValue(surface)!) return;
        Marshal.FreeHGlobal((IntPtr)PixelsField.GetValue(surface)!);
        PixelsField.SetValue(surface, IntPtr.Zero);
        OwnsPixelsField.SetValue(surface, false);
    }

    private static void CollectOnce()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
    private static void Collect() { CollectOnce(); GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true); }

    private sealed class RasterObservation
    {
        internal WeakReference Surface = null!;
        internal SKSurface? ExplicitOwner;
        internal int Callbacks, CallbackThread;
        internal IntPtr CallbackPixels;
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
            else { Context = new WgpuContext(); Context.Initialize(null); }
        }
        public void Dispose()
        {
            try { if (_dawn is not null) _dawn.Dispose(); else Context.Dispose(); }
            finally { WgpuContext.Current = _previous; }
        }
    }
}
