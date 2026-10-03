using ProGPU.Backend;
using ProGPU.Browser;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public unsafe sealed class BrowserTextureLimitsTests
{
    [Theory]
    [InlineData(0U)]
    [InlineData(1U)]
    [InlineData(7U)]
    public void CustomDispatcherCannotAdvertiseDeviceLimits(uint token)
    {
        int dispatches = 0;
        using var api = new BrowserWebGpuApi(_ => dispatches++);
        IWebGpuTextureLimitsSource limits = api;
        var descriptor = new BufferDescriptor { Size = 16, Usage = BufferUsage.CopyDst };
        api.DeviceCreateBuffer(BrowserWebGpuApi.DeviceHandle, &descriptor);
        byte[] pending = api.PendingPacket.ToArray();
        Assert.False(limits.TryGetTextureLimits((Device*)(nuint)token, out uint width, out uint height));
        Assert.Equal(0U, width);
        Assert.Equal(0U, height);
        Assert.Equal(pending, api.PendingPacket.ToArray());
        Assert.Equal(1, api.PendingCommandCount);
        Assert.Equal(0, dispatches);
    }

    [Fact]
    public void DisposedBrowserProviderRejectsWithoutDispatchOrPointerAccess()
    {
        int dispatches = 0;
        var api = new BrowserWebGpuApi(_ => dispatches++);
        api.Dispose();
        Assert.False(api.TryGetTextureLimits(BrowserWebGpuApi.DeviceHandle, out uint width, out uint height));
        Assert.Equal(0U, width);
        Assert.Equal(0U, height);
        Assert.Equal(0, dispatches);
    }

    [Fact]
    public void UninitializedDefaultRuntimeCannotTreatOpaqueDeviceTokenAsLimits()
    {
        // This managed host control does not initialize JavaScript or a GPU.
        Assert.False(OperatingSystem.IsBrowser());
        using var api = new BrowserWebGpuApi();
        Assert.False(api.TryGetTextureLimits(BrowserWebGpuApi.DeviceHandle, out uint width, out uint height));
        Assert.Equal(0U, width);
        Assert.Equal(0U, height);
        Assert.Equal(0, api.PendingCommandCount);
    }

    [Fact]
    public void CallerCapabilitiesDoNotSupplyAnActualRuntimeDevice()
    {
        Assert.False(OperatingSystem.IsBrowser());
        using var owner = BrowserGpuContext.Create(new BrowserGpuCapabilities
        {
            IsSupported = true,
            CanvasFormat = "bgra8unorm",
            ExecutionMode = BrowserExecutionMode.MainThread,
            MaxBufferSize = 123456789
        });
        Assert.False(owner.Api.TryGetTextureLimits(BrowserWebGpuApi.DeviceHandle, out uint width, out uint height));
        Assert.Equal(0U, width);
        Assert.Equal(0U, height);
    }
}
