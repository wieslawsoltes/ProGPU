using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Compute;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;
using Image = Microsoft.UI.Xaml.Controls.Image;

namespace ProGPU.Tests;

public sealed unsafe class OwnedGpuFinalizerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ImageFinalizationQueuesOwnedTextureWithoutRenderLock(bool dawn, bool encoded)
    {
        using var device = new OwnedDevice(dawn);
        var observed = new Observation();
        CollectWhileLocked(device.Context, () => DropImage(observed, encoded, null, false));
        Assert.False(observed.Owner.IsAlive);
        Assert.True(observed.Texture.IsDisposed);
        AssertQueuedOnce(device.Context, observed);
        device.Context.CleanupPendingResources();
        GC.KeepAlive(observed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ImageSourceReplacementRetiresOnCallerAndBorrowedTextureSurvives(bool dawn)
    {
        using var device = new OwnedDevice(dawn);
        var replaced = new Observation();
        DropImage(replaced, true, null, true);
        Assert.True(replaced.Texture.IsDisposed);
        AssertQueuedOnce(device.Context, replaced);
        device.Context.CleanupPendingResources();

        using var borrowed = new GpuTexture(device.Context, 2, 2, TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst, "Borrowed Image control");
        var observed = new Observation();
        CollectWhileLocked(device.Context, () => DropImage(observed, false, borrowed, false));
        Assert.False(observed.Owner.IsAlive);
        Assert.Same(borrowed, observed.Texture);
        Assert.False(borrowed.IsDisposed);
        Assert.DoesNotContain(observed.TextureHandle, device.Context.PendingTextures);
        GC.KeepAlive(observed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WavefrontFinalizationQueuesEveryOwnedResourceWithoutRenderLock(bool dawn)
    {
        using var device = new OwnedDevice(dawn);
        var observed = new Observation();
        int shaders = device.Context.CachedDeviceShaderModuleCount;
        int pipelines = device.Context.CachedDeviceComputePipelineCount;
        CollectWhileLocked(device.Context, () => DropWavefront(device.Context, observed, false));
        Assert.False(observed.Owner.IsAlive);
        Assert.True(observed.Texture.IsDisposed);
        AssertQueuedOnce(device.Context, observed);
        foreach (var buffer in observed.Buffers)
        {
            Assert.True(buffer.BufferPtr == null);
            buffer.Dispose();
        }
        AssertQueuedOnce(device.Context, observed);
        // Finalization transfers the pipeline cache to the normal context drain.
        device.Context.CleanupPendingResources();
        Assert.Equal(shaders, device.Context.CachedDeviceShaderModuleCount);
        Assert.Equal(pipelines, device.Context.CachedDeviceComputePipelineCount);
        Assert.Empty(device.Context.PendingComputePipelines);
        Assert.Empty(device.Context.PendingShaderModules);
        GC.KeepAlive(observed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ContextShutdownDrainsAbandonedWavefrontCacheInTheSamePass(bool dawn)
    {
        using var device = new OwnedDevice(dawn);
        var observed = new Observation();
        CollectWhileLocked(device.Context, () => DropWavefront(device.Context, observed, false));
        device.Dispose();
        Assert.Empty(device.Context.PendingComputePipelines);
        Assert.Empty(device.Context.PendingShaderModules);
        Assert.Empty(device.Context.PendingBuffers);
        Assert.Empty(device.Context.PendingTextures);
        Assert.Empty(device.Context.PendingTextureViews);
        GC.KeepAlive(observed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WavefrontExplicitDisposalReleasesItsPipelineCacheAndQueuesOnce(bool dawn)
    {
        using var device = new OwnedDevice(dawn);
        var observed = new Observation();
        int shaders = device.Context.CachedDeviceShaderModuleCount;
        int pipelines = device.Context.CachedDeviceComputePipelineCount;
        DropWavefront(device.Context, observed, true);
        Assert.True(observed.Texture.IsDisposed);
        AssertQueuedOnce(device.Context, observed);
        Assert.Equal(shaders, device.Context.CachedDeviceShaderModuleCount);
        Assert.Equal(pipelines, device.Context.CachedDeviceComputePipelineCount);
        device.Context.CleanupPendingResources();
        GC.KeepAlive(observed);
    }

    private static void CollectWhileLocked(WgpuContext context, Action abandon)
    {
        Exception? failure = null;
        var collector = new Thread(() =>
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true, Name = "Owned GPU finalizer transfer" };
        bool completed;
        lock (context.RenderLock)
        {
            abandon();
            collector.Start();
            completed = collector.Join(Deadline);
        }
        Assert.True(collector.Join(Deadline), "Finalizers did not finish after RenderLock was released.");
        Assert.True(completed, "An owned resource finalizer waited for RenderLock.");
        Assert.Null(failure);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropImage(Observation observed, bool encoded, GpuTexture? borrowed, bool replace)
    {
        string path = Path.Combine(Path.GetTempPath(), "progpu-image-retirement-" + Guid.NewGuid() + ".bmp");
        var image = new Image();
        try
        {
            if (borrowed is not null) image.Source = borrowed;
            else if (encoded) image.Source = new EncodedImageSource(BitmapBytes(), 2, 2);
            else { File.WriteAllBytes(path, BitmapBytes()); image.Source = path; }
            image.Measure(new Vector2(2, 2));
            image.Arrange(new Rect(0, 0, 2, 2));
            var drawing = new DrawingContext();
            image.OnRender(drawing);
            observed.Capture(Assert.IsType<GpuTexture>(Assert.Single(drawing.Commands).Texture));
            observed.Owner = new WeakReference(image, trackResurrection: true);
            if (replace) image.Source = null;
            GC.KeepAlive(image);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropWavefront(WgpuContext context, Observation observed, bool dispose)
    {
        var engine = new WavefrontVectorEngine(context);
        using var destination = new GpuTexture(context, 32, 32, TextureFormat.Bgra8Unorm,
            TextureUsage.TextureBinding | TextureUsage.StorageBinding | TextureUsage.CopySrc | TextureUsage.CopyDst,
            "Owned wavefront retirement control");
        engine.BeginFrame();
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(4, 4), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(24, 4)));
        figure.Segments.Add(new LineSegment(new Vector2(24, 24)));
        figure.Segments.Add(new LineSegment(new Vector2(4, 24)));
        path.Figures.Add(figure);
        engine.DrawPath(path, Matrix4x4.Identity, new SolidColorBrush(new Vector4(1, 0, 0, 1)));
        var descriptor = new CommandEncoderDescriptor();
        var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &descriptor);
        try
        {
            engine.EndFrame(encoder, destination, 1);
            var bufferDescriptor = new CommandBufferDescriptor();
            var commands = context.Api.CommandEncoderFinish(encoder, &bufferDescriptor);
            try { context.Submit(1, &commands); }
            finally { context.Api.CommandBufferRelease(commands); }
        }
        finally { context.Api.CommandEncoderRelease(encoder); }

        // Diagnostic observation only: retain actual private child owners so their
        // own finalizers cannot run first and conceal the engine's disposal path.
        foreach (var field in typeof(WavefrontVectorEngine).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.GetValue(engine) is GpuBuffer buffer)
            {
                observed.Buffers.Add(buffer);
                observed.BufferHandles.Add((nint)buffer.BufferPtr);
            }
            else if (field.GetValue(engine) is GpuTexture texture) observed.Capture(texture);
        }
        Assert.Equal(7, observed.Buffers.Count);
        Assert.NotNull(observed.Texture);
        observed.Owner = new WeakReference(engine, trackResurrection: true);
        if (dispose) { engine.Dispose(); engine.Dispose(); }
        GC.KeepAlive(engine);
    }

    private static void AssertQueuedOnce(WgpuContext context, Observation observed)
    {
        lock (context.DisposalLock)
        {
            Assert.Single(context.PendingTextures, handle => handle == observed.TextureHandle);
            Assert.Single(context.PendingTextureViews, handle => handle == observed.ViewHandle);
            foreach (nint handle in observed.BufferHandles)
                Assert.Single(context.PendingBuffers, value => value == handle);
        }
    }

    private static byte[] BitmapBytes()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write((ushort)0x4d42); writer.Write(70); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(2); writer.Write(2); writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(16); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(new byte[] { 0, 0, 255, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0 });
        return bytes.ToArray();
    }

    private sealed class Observation
    {
        internal GpuTexture Texture = null!;
        internal nint TextureHandle, ViewHandle;
        internal WeakReference Owner = null!;
        internal List<GpuBuffer> Buffers = [];
        internal List<nint> BufferHandles = [];
        internal void Capture(GpuTexture texture)
        {
            Texture = texture; TextureHandle = (nint)texture.TexturePtr; ViewHandle = (nint)texture.ViewPtr;
            Assert.NotEqual((nint)0, TextureHandle); Assert.NotEqual((nint)0, ViewHandle);
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
            else { Context = new WgpuContext(); Context.Initialize(null); }
        }
        public void Dispose()
        {
            try { if (_dawn is not null) _dawn.Dispose(); else Context.Dispose(); }
            finally { WgpuContext.Current = _previous; }
        }
    }
}
