using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

/// <summary>
/// Actual-device lifetime controls for abandoned cache generations. These do
/// not replace the independent pixel controls in CacheSamplerRasterRenderTests.
/// Every device is exclusively owned: no forced collection or shutdown relies
/// on the shared headless context retaining somebody else's resources.
/// </summary>
public sealed class CacheSamplerRetirementTests
{
    private static readonly TimeSpan ThreadTimeout = TimeSpan.FromSeconds(5);
    private static readonly Rect SourceBounds = new(12, -6, 8, 4);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AbandonedGenerationTransfersToOwnerQueueAndReleasesWeakSourceKey(
        bool useDawn, bool abandonParameter)
    {
        using var device = new OwnedDevice(useDawn);
        using var compositor = CreateCompositor(device.Context);
        var sourceCache = new ConditionalWeakTable<object, CacheSamplerRaster>();
        var callback = new SourceCallback();
        AbandonedCapture abandoned = AbandonCapture(compositor, device.Context,
            sourceCache, callback, abandonParameter);

        CollectAndFinishFinalizers();

        // Both possible finalizers only transfer ownership. A live context's
        // short queue must now own this generation, not a finalizer-side source
        // callback or a permanent strong context/source-key history.
        Assert.True(PendingOwnerCount(device.Context) > 0);
        Assert.Equal(0, callback.Count);
        Assert.False(abandoned.Texture.IsDisposed);
        Assert.True(IsAlive(abandoned.Raster));
        Assert.True(IsAlive(abandoned.SourceKey));

        DrainRetirement(device.Context);

        Assert.True(abandoned.Texture.IsDisposed);
        Assert.Equal(1, callback.Count);
        Assert.Equal(Environment.CurrentManagedThreadId, callback.Thread);
        Assert.Equal(0, PendingOwnerCount(device.Context));
        CollectAndFinishFinalizers();
        Assert.False(IsAlive(abandoned.Raster));
        Assert.False(IsAlive(abandoned.SourceKey));
        Assert.Equal(1, callback.Count);
        GC.KeepAlive(sourceCache);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FinalizerTransferCompletesWhileCreatingThreadOwnsRenderLock(
        bool useDawn, bool abandonParameter)
    {
        using var device = new OwnedDevice(useDawn);
        using var compositor = CreateCompositor(device.Context);
        var sourceCache = new ConditionalWeakTable<object, CacheSamplerRaster>();
        var callback = new SourceCallback();
        AbandonedCapture abandoned;
        Exception? collectorFailure = null;
        var collector = new Thread(() =>
            collectorFailure = Record.Exception(CollectAndFinishFinalizers))
        {
            IsBackground = true,
            Name = "Cache sampler finalizer transfer"
        };
        bool completedWhileLocked;
        lock (device.Context.RenderLock)
        {
            // Construction also takes this reentrant creating-thread lock.
            // The last strong owner is dropped only after it is held, so the
            // ensuing finalization must not need RenderLock to queue retirement.
            abandoned = AbandonCapture(compositor, device.Context, sourceCache,
                callback, abandonParameter);
            collector.Start();
            completedWhileLocked = collector.Join(ThreadTimeout);
        }

        // Release the lock even on failure, then join again before any device
        // cleanup. This is a deadlock regression, not an unbounded test hang or
        // a retry that turns a missed while-locked deadline into success.
        Assert.True(collector.Join(ThreadTimeout), "The finalizer observer did not finish after RenderLock was released.");
        Assert.True(completedWhileLocked, "Finalizer transfer waited for RenderLock owned by the creating thread.");
        Assert.Null(collectorFailure);
        Assert.True(PendingOwnerCount(device.Context) > 0);
        Assert.Equal(0, callback.Count);
        Assert.False(abandoned.Texture.IsDisposed);

        DrainRetirement(device.Context);
        Assert.True(abandoned.Texture.IsDisposed);
        Assert.Equal(1, callback.Count);
        Assert.Equal(Environment.CurrentManagedThreadId, callback.Thread);
        CollectAndFinishFinalizers();
        Assert.False(IsAlive(abandoned.Raster));
        Assert.False(IsAlive(abandoned.SourceKey));
        GC.KeepAlive(sourceCache);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParameterFinalizationAfterShutdownCannotEnqueueReleasedPayload(bool useDawn)
    {
        using var device = new OwnedDevice(useDawn);
        using var compositor = CreateCompositor(device.Context);
        var sourceCache = new ConditionalWeakTable<object, CacheSamplerRaster>();
        var callback = new SourceCallback();
        AbandonedCapture abandoned = ShutdownBeforeAbandoningParameter(
            compositor, device, sourceCache, callback);

        Assert.Equal(1, callback.Count);
        Assert.Equal(Environment.CurrentManagedThreadId, callback.Thread);
        Assert.True(abandoned.Texture.IsDisposed);
        Assert.Equal(0, PendingOwnerCount(device.Context));
        CollectAndFinishFinalizers();
        Assert.Equal(0, PendingOwnerCount(device.Context));
        Assert.Equal(1, callback.Count);
        Assert.False(IsAlive(abandoned.Raster));
        Assert.False(IsAlive(abandoned.SourceKey));
        GC.KeepAlive(sourceCache);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AbandonedCapture AbandonCapture(Compositor compositor,
        WgpuContext context, ConditionalWeakTable<object, CacheSamplerRaster> sourceCache,
        SourceCallback callback, bool abandonParameter)
    {
        object key = new();
        using GpuPicture picture = RecordSource(callback);
        CacheSamplerRaster raster = compositor.CaptureCacheSampler(picture,
            CreateFrame(context), key, 37, enableClearType: false);
        sourceCache.Add(key, raster);
        var observation = new AbandonedCapture(raster);
        if (abandonParameter)
        {
            WpfShaderEffectSampler parameter = WpfShaderEffectSampler.FromCacheRaster(1, raster);
            raster.Dispose(); // The parameter now owns the final counted lease.
            Assert.False(observation.Texture.IsDisposed);
            GC.KeepAlive(parameter);
        }

        // The direct case deliberately does not Dispose the raster. The other
        // case deliberately does not Dispose the parameter. No returned field
        // retains either object or the weak source-cache key.
        return observation;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AbandonedCapture ShutdownBeforeAbandoningParameter(
        Compositor compositor, OwnedDevice device,
        ConditionalWeakTable<object, CacheSamplerRaster> sourceCache, SourceCallback callback)
    {
        object key = new();
        using GpuPicture picture = RecordSource(callback);
        CacheSamplerRaster raster = compositor.CaptureCacheSampler(picture,
            CreateFrame(device.Context), key, 41, enableClearType: false);
        sourceCache.Add(key, raster);
        var observation = new AbandonedCapture(raster);
        WpfShaderEffectSampler parameter = WpfShaderEffectSampler.FromCacheRaster(1, raster);
        picture.Dispose();
        raster.Dispose();
        Assert.Equal(0, callback.Count);
        compositor.Dispose();
        device.Dispose();
        Assert.Equal(1, callback.Count);
        Assert.True(observation.Texture.IsDisposed);
        GC.KeepAlive(parameter); // Its actual finalizer runs only after shutdown.
        return observation;
    }

    private static Compositor CreateCompositor(WgpuContext context) => new(context,
        TextureFormat.Rgba8Unorm,
        CompositorOptions.Default with { EnableGpuHitTesting = false, PrimarySampleCount = 1 });

    private static CacheSamplerRasterFrame CreateFrame(WgpuContext context)
    {
        Assert.True(context.TryGetCacheRasterLimits(out uint maximumWidth, out uint maximumHeight));
        Assert.NotEqual(0U, maximumWidth);
        Assert.NotEqual(0U, maximumHeight);
        Assert.True(CacheSamplerRasterFrame.TryCreate(12, -6, 8, 4, 1, 1, 1,
            maximumWidth, maximumHeight, out CacheSamplerRasterFrame frame));
        return frame;
    }

    private static GpuPicture RecordSource(IDisposable callback)
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(SourceBounds);
        drawing.RetainResource(callback);
        drawing.DrawRectangle(new SolidColorBrush(new Vector4(1, 0, 0, 1)), null, SourceBounds);
        return recorder.EndRecording();
    }

    private static void DrainRetirement(WgpuContext context)
    {
        context.CleanupPendingResources(); // Retires the queued source owner.
        context.CleanupPendingResources(); // Drains handles queued by that retirement.
    }

    private static int PendingOwnerCount(WgpuContext context)
    {
        lock (context.DisposalLock) return context.PendingExternalTextureOwners.Count;
    }

    private static void CollectAndFinishFinalizers()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference) where T : class => reference.TryGetTarget(out _);

    private sealed class AbandonedCapture(CacheSamplerRaster raster)
    {
        // Long weak handles observe queued/finalized generations as well as
        // ordinary live objects without themselves keeping either alive.
        internal WeakReference<CacheSamplerRaster> Raster { get; } = new(raster, trackResurrection: true);
        internal WeakReference<object> SourceKey { get; } = new(raster.SourceIdentity, trackResurrection: true);
        internal GpuTexture Texture { get; } = raster.Texture;
    }

    private sealed class SourceCallback : IDisposable
    {
        internal int Count { get; private set; }
        internal int Thread { get; private set; }
        public void Dispose()
        {
            Count++;
            Thread = Environment.CurrentManagedThreadId;
        }
    }

    private sealed class OwnedDevice : IDisposable
    {
        private readonly DawnGpuContext? _dawn;
        internal WgpuContext Context { get; }

        internal OwnedDevice(bool useDawn)
        {
            if (useDawn)
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
            if (_dawn is not null) _dawn.Dispose();
            else Context.Dispose();
        }
    }
}
