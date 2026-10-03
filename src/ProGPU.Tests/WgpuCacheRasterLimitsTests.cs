using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Browser;
using Silk.NET.WebGPU;
using Xunit;
using W = WebGpuSharp;
using DawnFfi = WebGpuSharp.FFI;

namespace ProGPU.Tests;

[Collection(WgpuContextLossCollection.Name)]
public sealed class WgpuCacheRasterLimitsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UninitializedAndDisposedContextsHaveNoInventedLimits(bool disposed)
    {
        using var context = new WgpuContext();
        if (disposed) context.Dispose();
        Assert.False(context.TryGetCacheRasterLimits(out uint width, out uint height));
        Assert.Equal(0u, width);
        Assert.Equal(0u, height);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void UnknownExternalDevicesCannotBorrowNativeOrDefaultLimits(bool lost)
    {
        int dispatches = 0;
        using var api = new BrowserWebGpuApi(_ => dispatches++);
        using var context = new WgpuContext();
        context.InitializeExternal(api, BrowserWebGpuApi.DeviceHandle,
            BrowserWebGpuApi.QueueHandle, BrowserWebGpuApi.SurfaceHandle, TextureFormat.Bgra8Unorm);
        if (lost) context.ReportDeviceLost(DeviceLostReason.Unknown, "Cache raster limits ownership control.");
        Assert.False(context.TryGetCacheRasterLimits(out uint width, out uint height));
        Assert.Equal(0u, width);
        Assert.Equal(0u, height);
        Assert.Equal(0, dispatches);
    }

    [Fact]
    public async Task LimitQueriesUseTheExistingRenderingLock()
    {
        using var context = new WgpuContext();
        using var started = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        Task<bool> query;
        Monitor.Enter(context.RenderLock);
        try
        {
            query = Task.Run(() =>
            {
                started.Set();
                bool available = context.TryGetCacheRasterLimits(out _, out _);
                returned.Set();
                return available;
            });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(returned.Wait(TimeSpan.FromMilliseconds(250)));
        }
        finally { Monitor.Exit(context.RenderLock); }
        Assert.False(await query.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(returned.IsSet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void BothOwnedProvidersReportTheirExactDeviceLimits(bool useDawn)
    {
        BackendType backend = OperatingSystem.IsWindows() ? BackendType.D3D12 :
            OperatingSystem.IsMacOS() ? BackendType.Metal : BackendType.Vulkan;
        using var dawn = useDawn ? DawnGpuContext.CreateOffscreen(backend, forceFallbackAdapter: false) : null;
        using var silk = useDawn ? null : new WgpuContext();
        WgpuContext context = dawn?.Context ?? silk!;
        if (!useDawn) context.Initialize(null);
        var identity = context.DeviceIdentity;
        uint expected;
        if (useDawn)
        {
            var reference = new W.Limits();
            Assert.Equal(W.Status.Success, new DawnFfi.DeviceHandle((nuint)context.Device).GetLimits(&reference));
            expected = reference.MaxTextureDimension2D;
        }
        else
        {
            var reference = new SupportedLimits();
            Assert.True(context.Wgpu.DeviceGetLimits(context.Device, &reference));
            expected = reference.Limits.MaxTextureDimension2D;
        }
        Assert.NotEqual(0u, expected);
        Assert.True(context.TryGetCacheRasterLimits(out uint width, out uint height));
        Assert.Equal(expected, width);
        Assert.Equal(expected, height);
        Assert.Same(identity, context.DeviceIdentity);
        context.ReportDeviceLost(DeviceLostReason.Unknown, "Retire exact cache-raster query owner.");
        Assert.False(context.TryGetCacheRasterLimits(out width, out height));
        Assert.Equal(0u, width);
        Assert.Equal(0u, height);
    }
}
