using System.Runtime.CompilerServices;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed unsafe class PipelineCacheFinalizerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const string Shader = """
        @vertex fn vs_main() -> @builtin(position) vec4<f32> {
            return vec4<f32>(0.0, 0.0, 0.0, 1.0);
        }
        @fragment fn fs_main() -> @location(0) vec4<f32> {
            return vec4<f32>(1.0, 0.0, 0.0, 1.0);
        }
        @compute @workgroup_size(1) fn main() { }
        """;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AbandonedCacheRetiresShaderRenderAndComputeHandlesWithoutRenderLock(bool dawn)
    {
        using var device = new Device(dawn);
        CollectWhileLocked(device.Context, () => DropCache(device.Context, false));
        // Finalization transfers ownership; the creating context performs release.
        AssertCounts(device.Context, 1);
        device.Context.CleanupPendingResources();
        AssertCounts(device.Context, 0);
        Assert.Empty(device.Context.PendingShaderModules);
        Assert.Empty(device.Context.PendingRenderPipelines);
        Assert.Empty(device.Context.PendingComputePipelines);
        device.Context.CleanupPendingResources();
        AssertCounts(device.Context, 0);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AbandonedCachePreservesLiveSharedHandleOwnership(bool dawn)
    {
        using var device = new Device(dawn);
        using var retained = new RenderPipelineCache(device.Context);
        var original = Populate(retained);
        CollectWhileLocked(device.Context, () => DropCache(device.Context, false));
        device.Context.CleanupPendingResources();
        AssertCounts(device.Context, 1);
        Assert.Equal(original, Populate(retained));
        Assert.Empty(device.Context.PendingShaderModules);
        Assert.Empty(device.Context.PendingRenderPipelines);
        Assert.Empty(device.Context.PendingComputePipelines);
        retained.Dispose();
        AssertCounts(device.Context, 0);
        Assert.Single(device.Context.PendingShaderModules, p => p == original.Shader);
        Assert.Single(device.Context.PendingRenderPipelines, p => p == original.Render);
        Assert.Single(device.Context.PendingComputePipelines, p => p == original.Compute);
        retained.Dispose();
        device.Context.CleanupPendingResources();
        AssertCounts(device.Context, 0);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExplicitDisposalAndShutdownRetainTheirOwnership(bool dawn)
    {
        using var device = new Device(dawn);
        CollectWhileLocked(device.Context, () => DropCache(device.Context, true));
        AssertCounts(device.Context, 0);
        Assert.Single(device.Context.PendingShaderModules);
        Assert.Single(device.Context.PendingRenderPipelines);
        Assert.Single(device.Context.PendingComputePipelines);
        device.Context.CleanupPendingResources();
        CollectWhileLocked(device.Context, () => DropAfterContextShutdown(device));
        Assert.Empty(device.Context.PendingShaderModules);
        Assert.Empty(device.Context.PendingRenderPipelines);
        Assert.Empty(device.Context.PendingComputePipelines);
    }

    private static void AssertCounts(WgpuContext context, int count)
    {
        Assert.Equal(count, context.CachedDeviceShaderModuleCount);
        Assert.Equal(count, context.CachedDeviceRenderPipelineCount);
        Assert.Equal(count, context.CachedDeviceComputePipelineCount);
    }

    private static (nint Shader, nint Render, nint Compute) Populate(RenderPipelineCache cache)
    {
        var shader = cache.GetOrCreateShader("FinalizedCacheShader", Shader);
        var render = cache.GetOrCreateRenderPipeline("FinalizedCacheRender", shader);
        var compute = cache.GetOrCreateComputePipeline("FinalizedCacheCompute", shader);
        Assert.True(shader != null && render != null && compute != null);
        return ((nint)shader, (nint)render, (nint)compute);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropCache(WgpuContext context, bool dispose)
    {
        var cache = new RenderPipelineCache(context);
        Populate(cache);
        if (dispose) { cache.Dispose(); cache.Dispose(); }
        return new WeakReference(cache);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropAfterContextShutdown(Device device)
    {
        var cache = new RenderPipelineCache(device.Context);
        Populate(cache);
        device.Dispose();
        return new WeakReference(cache);
    }

    private static void CollectWhileLocked(WgpuContext context, Func<WeakReference> abandon)
    {
        var collector = new Thread(() =>
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }) { IsBackground = true, Name = "Pipeline cache finalizer" };
        bool completed;
        WeakReference owner;
        lock (context.RenderLock)
        {
            owner = abandon();
            collector.Start();
            completed = collector.Join(Deadline);
        }
        Assert.True(collector.Join(Deadline), "Collection did not finish after releasing RenderLock.");
        Assert.True(completed, "Pipeline cache finalization waited for RenderLock.");
        Assert.False(owner.IsAlive);
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
