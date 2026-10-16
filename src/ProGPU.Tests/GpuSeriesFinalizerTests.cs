using System.Runtime.CompilerServices;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed unsafe class GpuSeriesFinalizerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AbandonedSeriesRetiresActualBindingsAndIndependentlyRetainedBuffers(bool dawn)
    {
        using var device = new Device(dawn);
        var observed = new Observation();
        try
        {
            CollectWhileLocked(device.Context, () => DropSeries(device.Context, observed, false));
            Assert.False(observed.Owner.IsAlive);
            AssertQueuedOnce(device.Context, observed);
            foreach (var buffer in observed.Buffers) Assert.True(buffer.BufferPtr == null);
            foreach (var buffer in observed.Buffers) buffer.Dispose();
            AssertQueuedOnce(device.Context, observed);
        }
        finally { RetireObserved(device.Context, observed); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExplicitSeriesDisposalRetiresActualBindingsOnce(bool dawn)
    {
        using var device = new Device(dawn);
        var observed = new Observation();
        try
        {
            DropSeries(device.Context, observed, true);
            AssertQueuedOnce(device.Context, observed);
            CollectWhileLocked(device.Context, () => { });
            Assert.False(observed.Owner.IsAlive);
            AssertQueuedOnce(device.Context, observed);
        }
        finally { RetireObserved(device.Context, observed); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MovingSeriesToAnotherContextRetiresOnlyItsPreviousResources(bool dawn)
    {
        using var original = new Device(dawn);
        var observed = new Observation();
        using var series = CreateSeries(original.Context, observed);
        using var replacement = new Device(dawn);
        try
        {
            WgpuContext.Current = replacement.Context;
            series.Upload(new float[] { 2, 3, 4, 5 }, 2);
            Assert.True(series.IsOwnedBy(replacement.Context));
            AssertQueuedOnce(original.Context, observed);
            Assert.Empty(replacement.Context.PendingBindGroups);
            Assert.Empty(replacement.Context.PendingBuffers);
            Assert.NotNull(series.Buffer);
            Assert.True(series.Buffer.BufferPtr != null);
            Assert.Equal(0, series.LineBindGroup);
            Assert.Equal(0, series.ScatterBindGroup);
            Assert.Equal(0, series.LineBindGroupOffscreen);
            Assert.Equal(0, series.ScatterBindGroupOffscreen);
        }
        finally
        {
            series.Dispose();
            RetireObserved(original.Context, observed);
        }
    }

    private static void CollectWhileLocked(WgpuContext context, Action abandon)
    {
        var collector = new Thread(() =>
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }) { IsBackground = true, Name = "Series owner finalizer" };
        bool completed;
        lock (context.RenderLock)
        {
            abandon();
            collector.Start();
            completed = collector.Join(Deadline);
        }
        Assert.True(collector.Join(Deadline), "Collection did not finish after releasing RenderLock.");
        Assert.True(completed, "Series finalization waited for RenderLock.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropSeries(WgpuContext context, Observation observed, bool dispose)
    {
        var series = CreateSeries(context, observed);
        if (dispose) { series.Dispose(); series.Dispose(); }
        GC.KeepAlive(series);
    }

    private static GpuSeriesBuffer CreateSeries(WgpuContext context, Observation observed)
    {
        WgpuContext.Current = context;
        var series = new GpuSeriesBuffer();
        series.Upload(new float[] { 1, 2, 3, 4 }, 2);
        series.VsUniformBuffer = new GpuBuffer(context, 96, BufferUsage.Uniform | BufferUsage.CopyDst);
        series.FsUniformBuffer = new GpuBuffer(context, 16, BufferUsage.Uniform | BufferUsage.CopyDst);
        observed.Buffers = [series.Buffer!, series.VsUniformBuffer, series.FsUniformBuffer];
        observed.BufferHandles = observed.Buffers.Select(b => (nint)b.BufferPtr).ToArray();
        var entries = stackalloc BindGroupLayoutEntry[3];
        for (uint i = 0; i < 3; i++)
            entries[i] = new BindGroupLayoutEntry
            {
                Binding = i, Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Buffer = new BufferBindingLayout { Type = i == 2 ? BufferBindingType.ReadOnlyStorage : BufferBindingType.Uniform }
            };
        var descriptor = new BindGroupLayoutDescriptor { Entries = entries, EntryCount = 3 };
        var layout = context.Api.DeviceCreateBindGroupLayout(context.Device, &descriptor);
        Assert.True(layout != null);
        try
        {
            var bindings = stackalloc BindGroupEntry[3];
            bindings[0] = new BindGroupEntry { Binding = 0, Buffer = series.VsUniformBuffer.BufferPtr, Size = 96 };
            bindings[1] = new BindGroupEntry { Binding = 1, Buffer = series.FsUniformBuffer.BufferPtr, Size = 16 };
            bindings[2] = new BindGroupEntry { Binding = 2, Buffer = series.Buffer!.BufferPtr, Size = series.Buffer.Size };
            var bindingDescriptor = new BindGroupDescriptor { Layout = layout, Entries = bindings, EntryCount = 3 };
            for (int i = 0; i < observed.BindGroups.Length; i++)
            {
                observed.BindGroups[i] = (nint)context.Api.DeviceCreateBindGroup(context.Device, &bindingDescriptor);
                Assert.NotEqual((nint)0, observed.BindGroups[i]);
            }
        }
        finally { context.Api.BindGroupLayoutRelease(layout); }
        series.LineBindGroup = observed.BindGroups[0];
        series.ScatterBindGroup = observed.BindGroups[1];
        series.LineBindGroupOffscreen = observed.BindGroups[2];
        series.ScatterBindGroupOffscreen = observed.BindGroups[3];
        observed.Owner = new WeakReference(series, trackResurrection: true);
        return series;
    }

    private static void AssertQueuedOnce(WgpuContext context, Observation observed)
    {
        lock (context.DisposalLock)
        {
            foreach (var handle in observed.BindGroups)
                Assert.Single(context.PendingBindGroups, candidate => candidate == handle);
            foreach (var handle in observed.BufferHandles)
                Assert.Single(context.PendingBuffers, candidate => candidate == handle);
        }
    }

    private static void RetireObserved(WgpuContext context, Observation observed)
    {
        // A failing original implementation must not leak the test's real handles.
        lock (context.DisposalLock)
            foreach (var handle in observed.BindGroups)
                if (handle != 0 && !context.PendingBindGroups.Contains(handle))
                    context.QueueBindGroupDisposal(handle);
        foreach (var buffer in observed.Buffers) buffer.Dispose();
        context.CleanupPendingResources();
        GC.KeepAlive(observed);
    }

    private sealed class Observation
    {
        internal WeakReference Owner = null!;
        internal GpuBuffer[] Buffers = [];
        internal nint[] BufferHandles = [];
        internal nint[] BindGroups = new nint[4];
    }

    private sealed class Device : IDisposable
    {
        private readonly WgpuContext? _previous = WgpuContext.Current;
        private readonly DawnGpuContext? _dawn;
        internal WgpuContext Context { get; }
        internal Device(bool dawn)
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
