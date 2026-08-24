using ProGPU.Backend;
using ProGPU.Browser;
using ProGPU.Scene;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public unsafe sealed class GpuBufferLimitTests
{
    [Fact]
    public void ExternalContextRetainsTheReportedBufferLimit()
    {
        using var api = new BrowserWebGpuApi(_ => { });
        using var context = new WgpuContext();
        context.InitializeExternal(
            api,
            BrowserWebGpuApi.DeviceHandle,
            BrowserWebGpuApi.QueueHandle,
            BrowserWebGpuApi.SurfaceHandle,
            TextureFormat.Bgra8Unorm,
            maxBufferSize: 1024);

        Assert.Equal(1024UL, context.MaxBufferSize);
    }

    [Fact]
    public void OversizedBufferIsRejectedBeforeWebGpuCreation()
    {
        var packets = new List<byte[]>();
        using var api = new BrowserWebGpuApi(
            packet => packets.Add(packet.WrittenSpan.ToArray()));
        using var context = new WgpuContext();
        context.InitializeExternal(
            api,
            BrowserWebGpuApi.DeviceHandle,
            BrowserWebGpuApi.QueueHandle,
            BrowserWebGpuApi.SurfaceHandle,
            TextureFormat.Bgra8Unorm,
            maxBufferSize: 64);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new GpuBuffer(
                context,
                68,
                BufferUsage.Vertex,
                "Limit regression buffer"));
        Assert.Contains("68 bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("64 bytes", error.Message, StringComparison.Ordinal);

        api.QueueSubmit(BrowserWebGpuApi.QueueHandle, 0, null);
        Assert.DoesNotContain(
            packets.SelectMany(ReadOpcodes),
            opcode => opcode == BrowserGpuOpcode.CreateBuffer);
    }

    [Fact]
    public void OversizedMappedRingIsRejectedBeforeMappingAnInvalidBuffer()
    {
        var packets = new List<byte[]>();
        using var api = new BrowserWebGpuApi(
            packet => packets.Add(packet.WrittenSpan.ToArray()));
        using var context = new WgpuContext();
        context.InitializeExternalNativeDevice(
            api,
            new NoOpExternalDeviceLifetime(),
            BrowserWebGpuApi.DeviceHandle,
            BrowserWebGpuApi.QueueHandle,
            TextureFormat.Bgra8Unorm,
            maxBufferSize: 64);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new GpuMappedUploadBufferRing(
                context,
                68,
                slotCount: 2));
        Assert.Contains("68 bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("64 bytes", error.Message, StringComparison.Ordinal);

        api.QueueSubmit(BrowserWebGpuApi.QueueHandle, 0, null);
        Assert.DoesNotContain(
            packets.SelectMany(ReadOpcodes),
            opcode => opcode == BrowserGpuOpcode.CreateBuffer);
    }

    [Fact]
    public void GeometryGrowthClampsToTheDeviceLimitAndRejectsOverflow()
    {
        Assert.Equal(
            256U,
            Compositor.CalculateBufferGrowth(
                160,
                220,
                256,
                "geometry"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            Compositor.CalculateBufferGrowth(
                160,
                257,
                256,
                "geometry"));
        Assert.Contains("257 bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("256 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SceneUploadBatchesStayBoundedBelowTheDeviceLimit()
    {
        Assert.Equal(
            64U * 1024U * 1024U,
            Compositor.GetSceneUploadBatchCapacity(256UL * 1024UL * 1024UL));
        Assert.Equal(4096U, Compositor.GetSceneUploadBatchCapacity(4096));
        Assert.Equal(
            4096U,
            Compositor.CalculateSceneUploadBufferCapacity(
                1024,
                3000,
                4096));
        Assert.Throws<InvalidOperationException>(() =>
            Compositor.CalculateSceneUploadBufferCapacity(
                1024,
                4097,
                4096));
    }

    private static BrowserGpuOpcode[] ReadOpcodes(byte[] packet)
    {
        var result = new List<BrowserGpuOpcode>();
        var reader = new BrowserGpuPacketReader(packet);
        while (reader.TryRead(out var command))
        {
            result.Add(command.Opcode);
        }
        return result.ToArray();
    }

    private sealed class NoOpExternalDeviceLifetime
        : IWebGpuExternalDeviceLifetime
    {
        public void Poll(bool wait)
        {
        }

        public void Dispose()
        {
        }
    }
}
