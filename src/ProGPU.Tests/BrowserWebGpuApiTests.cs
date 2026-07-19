using ProGPU.Browser;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;

namespace ProGPU.Tests;

public unsafe sealed class BrowserWebGpuApiTests
{
    [Fact]
    public void ExternalBrowserContextReportsInitializedForContextBoundResources()
    {
        using var api = new BrowserWebGpuApi(_ => { });
        using var context = new WgpuContext();

        context.InitializeExternal(
            api,
            BrowserWebGpuApi.DeviceHandle,
            BrowserWebGpuApi.QueueHandle,
            BrowserWebGpuApi.SurfaceHandle,
            TextureFormat.Bgra8Unorm);

        Assert.True(context.IsInitialized);
        Assert.Same(context, WgpuContext.Current);
    }

    [Fact]
    public void ResourceCopyUploadAndSubmitUseOneTypedPacket()
    {
        var packets = new List<byte[]>();
        using var api = new BrowserWebGpuApi(packet => packets.Add(packet.WrittenSpan.ToArray()));
        var bufferDescriptor = new BufferDescriptor
        {
            Size = 256,
            Usage = BufferUsage.CopySrc | BufferUsage.CopyDst
        };
        var source = api.DeviceCreateBuffer(BrowserWebGpuApi.DeviceHandle, &bufferDescriptor);
        var destination = api.DeviceCreateBuffer(BrowserWebGpuApi.DeviceHandle, &bufferDescriptor);
        var encoder = api.DeviceCreateCommandEncoder(BrowserWebGpuApi.DeviceHandle, null);
        api.CommandEncoderCopyBufferToBuffer(encoder, source, 8, destination, 16, 64);
        var commands = api.CommandEncoderFinish(encoder, null);
        Span<byte> upload = stackalloc byte[] { 1, 2, 3, 4 };
        fixed (byte* uploadPointer = upload)
            api.QueueWriteBuffer(BrowserWebGpuApi.QueueHandle, source, 32, uploadPointer, (nuint)upload.Length);
        api.QueueSubmit(BrowserWebGpuApi.QueueHandle, 1, &commands);

        Assert.Single(packets);
        Assert.Equal(
            new[]
            {
                BrowserGpuOpcode.CreateBuffer,
                BrowserGpuOpcode.CreateBuffer,
                BrowserGpuOpcode.CreateCommandEncoder,
                BrowserGpuOpcode.CopyBufferToBuffer,
                BrowserGpuOpcode.FinishCommandEncoder,
                BrowserGpuOpcode.QueueWriteBuffer,
                BrowserGpuOpcode.Submit
            },
            ReadOpcodes(packets[0]));
    }

    [Fact]
    public void SurfaceRenderPassIsEncodedThroughOrdinaryWebGpuFacade()
    {
        var packets = new List<byte[]>();
        using var api = new BrowserWebGpuApi(packet => packets.Add(packet.WrittenSpan.ToArray()));
        var surfaceTexture = new SurfaceTexture();
        api.SurfaceGetCurrentTexture(BrowserWebGpuApi.SurfaceHandle, &surfaceTexture);
        var viewDescriptor = new TextureViewDescriptor
        {
            Format = TextureFormat.Bgra8Unorm,
            Dimension = TextureViewDimension.Dimension2D,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Aspect = TextureAspect.All
        };
        var view = api.TextureCreateView(surfaceTexture.Texture, &viewDescriptor);
        var attachment = new RenderPassColorAttachment
        {
            View = view,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 0.1, G = 0.2, B = 0.3, A = 1 }
        };
        var passDescriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &attachment
        };
        var encoder = api.DeviceCreateCommandEncoder(BrowserWebGpuApi.DeviceHandle, null);
        var pass = api.CommandEncoderBeginRenderPass(encoder, &passDescriptor);
        api.RenderPassEncoderEnd(pass);
        var commands = api.CommandEncoderFinish(encoder, null);
        api.QueueSubmit(BrowserWebGpuApi.QueueHandle, 1, &commands);

        Assert.Single(packets);
        Assert.Equal(
            new[]
            {
                BrowserGpuOpcode.CreateSurfaceTexture,
                BrowserGpuOpcode.CreateTextureView,
                BrowserGpuOpcode.CreateCommandEncoder,
                BrowserGpuOpcode.BeginRenderPass,
                BrowserGpuOpcode.EndRenderPass,
                BrowserGpuOpcode.FinishCommandEncoder,
                BrowserGpuOpcode.Submit
            },
            ReadOpcodes(packets[0]));
    }

    private static BrowserGpuOpcode[] ReadOpcodes(byte[] packet)
    {
        var result = new List<BrowserGpuOpcode>();
        var reader = new BrowserGpuPacketReader(packet);
        while (reader.TryRead(out var command)) result.Add(command.Opcode);
        return result.ToArray();
    }
}
