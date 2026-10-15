using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.ProGpu;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ProGPU.Avalonia.BitmapOwnershipTests;

public sealed unsafe class BitmapSnapshotOwnershipTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AbandonedSnapshotQueuesItsTextureWithoutRenderLock(bool dawn)
    {
        using var device = new Device(dawn);
        var observed = new Observation();
        CollectWhileLocked(device.Context, () => DropSnapshot(device.Context, observed, false));
        Assert.False(observed.Source.IsAlive);
        Assert.False(observed.Snapshot.IsAlive);
        Assert.True(observed.Texture.IsDisposed);
        AssertQueuedOnce(device.Context, observed);
        observed.Texture.Dispose();
        AssertQueuedOnce(device.Context, observed);
        device.Context.CleanupPendingResources();
        GC.KeepAlive(observed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RetainedSnapshotOutlivesItsOriginalPublicBitmap(bool dawn)
    {
        using var device = new Device(dawn);
        var observed = new Observation();
        using var snapshot = DropSource(device.Context, observed);
        CollectWhileLocked(device.Context, () => { });
        Assert.False(observed.Source.IsAlive);
        Assert.False(observed.Texture.IsDisposed);
        Assert.Same(observed.Texture, snapshot.GetTexture(device.Context));
        Assert.DoesNotContain(observed.TextureHandle, device.Context.PendingTextures);
        GC.KeepAlive(snapshot);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExplicitSnapshotDisposalRetiresOnceBeforeCollection(bool dawn)
    {
        using var device = new Device(dawn);
        var observed = new Observation();
        DropSnapshot(device.Context, observed, true);
        Assert.True(observed.Texture.IsDisposed);
        AssertQueuedOnce(device.Context, observed);
        CollectWhileLocked(device.Context, () => { });
        AssertQueuedOnce(device.Context, observed);
        device.Context.CleanupPendingResources();
        GC.KeepAlive(observed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WriteableSourceReplacementDisposesOnlyItsPreviousSnapshot(bool dawn)
    {
        using var device = new Device(dawn);
        using var source = new WriteableBitmap(new PixelSize(2, 2), new global::Avalonia.Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Premul);
        using (var pixels = source.Lock())
            new Span<byte>((void*)pixels.Address, pixels.RowBytes * pixels.Size.Height).Fill(255);
        var first = Assert.IsAssignableFrom<ImmutableBitmap>(ProGpuImageBrushSource.GetBitmap(source));
        var retired = first.GetTexture(device.Context)!;
        using var second = Assert.IsAssignableFrom<ImmutableBitmap>(ProGpuImageBrushSource.GetBitmap(source));
        Assert.NotSame(first, second);
        Assert.True(retired.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => first.GetTexture(device.Context));
        Assert.False(second.GetTexture(device.Context)!.IsDisposed);
        GC.KeepAlive(source);
    }

    private static void CollectWhileLocked(WgpuContext context, Action abandon)
    {
        Exception? failure = null;
        var collector = new Thread(() =>
        {
            try { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true, Name = "Avalonia snapshot ownership" };
        bool completed;
        lock (context.RenderLock)
        {
            abandon();
            collector.Start();
            completed = collector.Join(Deadline);
        }
        Assert.True(collector.Join(Deadline), "Finalizers did not finish after RenderLock was released.");
        Assert.True(completed, "A snapshot finalizer waited for RenderLock.");
        Assert.Null(failure);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ImmutableBitmap DropSource(WgpuContext context, Observation observed)
    {
        using var encoded = new MemoryStream(BitmapBytes());
        var source = new Bitmap(encoded);
        var snapshot = Assert.IsAssignableFrom<ImmutableBitmap>(ProGpuImageBrushSource.GetBitmap(source));
        observed.Texture = snapshot.GetTexture(context)!;
        observed.TextureHandle = (nint)observed.Texture.TexturePtr;
        observed.ViewHandle = (nint)observed.Texture.ViewPtr;
        Assert.NotEqual((nint)0, observed.TextureHandle);
        Assert.NotEqual((nint)0, observed.ViewHandle);
        observed.Source = new WeakReference(source, trackResurrection: true);
        observed.Snapshot = new WeakReference(snapshot, trackResurrection: true);
        GC.KeepAlive(source);
        return snapshot;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropSnapshot(WgpuContext context, Observation observed, bool dispose)
    {
        var snapshot = DropSource(context, observed);
        if (dispose) { snapshot.Dispose(); snapshot.Dispose(); }
        GC.KeepAlive(snapshot);
    }

    private static void AssertQueuedOnce(WgpuContext context, Observation observed)
    {
        lock (context.DisposalLock)
        {
            Assert.Single(context.PendingTextures, value => value == observed.TextureHandle);
            Assert.Single(context.PendingTextureViews, value => value == observed.ViewHandle);
        }
    }

    private static byte[] BitmapBytes()
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write((ushort)0x4d42); writer.Write(70); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(2); writer.Write(2); writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(16); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(new byte[] { 0, 0, 255, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0 });
        return bytes.ToArray();
    }

    private sealed class Observation
    {
        internal WeakReference Source = null!, Snapshot = null!;
        // Keep the actual child alive so its own finalizer cannot hide the bug.
        internal GpuTexture Texture = null!;
        internal nint TextureHandle, ViewHandle;
    }

    private sealed class Device : IDisposable
    {
        private readonly WgpuContext? _previous = WgpuContext.Current;
        private readonly IDisposable _scope = AvaloniaLocator.EnterScope();
        private readonly DawnGpuContext? _dawn;
        internal WgpuContext Context { get; }
        internal Device(bool dawn)
        {
            AvaloniaLocator.CurrentMutable.Bind<IPlatformRenderInterface>().ToConstant(new PlatformRenderInterface());
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
            finally { WgpuContext.Current = _previous; _scope.Dispose(); }
        }
    }
}
