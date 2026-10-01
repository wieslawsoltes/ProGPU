using ProGPU.Backend;
using Silk.NET.WebGPU;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;
using Xunit;

namespace ProGPU.Tests;

public unsafe sealed class GpuBufferDeviceLossTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyLostDeviceRejectsBufferAllocation(bool mapped)
    {
        var api = new RecordingApi();
        using var context = CreateContext(api);
        context.ReportDeviceLost(DeviceLostReason.Unknown, "Known device loss");
        Assert.Throws<WgpuDeviceLostException>(() =>
        {
            if (mapped) using (new GpuMappedUploadBufferRing(context, 64, 2)) { }
            else using (new GpuBuffer(context, 64, BufferUsage.Uniform)) { }
        });
        Assert.Equal(0, api.CreateCount);
        Assert.Empty(api.Released);
        Assert.Equal(0, api.MapRangeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LossDuringCreationReleasesErrorHandleWithoutPublishingIt(bool returnNull)
    {
        var api = new RecordingApi { ReturnNull = returnNull };
        using var context = CreateContext(api);
        api.OnCreate = _ => context.ReportDeviceLost(DeviceLostReason.Unknown, "Creation failed");
        Assert.Throws<WgpuDeviceLostException>(() => new GpuBuffer(context, 64, BufferUsage.Uniform));
        Assert.Equal(1, api.CreateCount);
        Assert.Equal(returnNull ? Array.Empty<nuint>() : new nuint[] { 101 }, api.Released);
        Assert.Empty(api.Destroyed);
        Assert.Empty(api.Unmapped);
        Assert.Equal(0, api.MapRangeCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PartiallyCreatedRingReleasesFailedHandleAndOnlyDestroysAdmittedSlots(int failedSlot)
    {
        var api = new RecordingApi();
        using var context = CreateContext(api);
        api.OnCreate = count =>
        {
            if (count == failedSlot)
                context.ReportDeviceLost(DeviceLostReason.Unknown, "Mapped allocation failed");
        };
        Assert.Throws<WgpuDeviceLostException>(() => new GpuMappedUploadBufferRing(context, 64, 3));
        Assert.Equal(failedSlot, api.CreateCount);
        Assert.Equal(failedSlot, api.Released.Count);
        Assert.Equal(failedSlot, api.Released.Distinct().Count());
        Assert.Contains((nuint)(100 + failedSlot), api.Released);
        Assert.DoesNotContain((nuint)(100 + failedSlot), api.Destroyed);
        Assert.DoesNotContain((nuint)(100 + failedSlot), api.Unmapped);
        Assert.Equal(failedSlot - 1, api.Destroyed.Count);
        Assert.Equal(api.Destroyed, api.Unmapped);
        Assert.Equal(0, api.MapRangeCount);
    }

    [Fact]
    public void LostDeviceCannotMapAnAlreadyCreatedUploadRing()
    {
        var api = new RecordingApi();
        using var context = CreateContext(api);
        using var ring = new GpuMappedUploadBufferRing(context, 64, 2);
        context.ReportDeviceLost(DeviceLostReason.Unknown, "Lost after allocation");
        Assert.Throws<WgpuDeviceLostException>(() => ring.TryWrite(new byte[4], out _));
        Assert.Equal(0, api.MapRangeCount);
        Assert.Equal(2, api.CreateCount);
    }

    [Fact]
    public void TextureReadbackRejectsErrorBufferBeforeAnyCopyOrMap()
    {
        var api = new RecordingApi();
        using var context = CreateContext(api);
        using var readback = new GpuTextureReadbackBuffer(context);
        api.OnCreate = _ => context.ReportDeviceLost(DeviceLostReason.Unknown, "Readback allocation failed");
        Assert.Throws<WgpuDeviceLostException>(() => readback.EnsureCapacity(4, 4));
        Assert.Equal(new nuint[] { 101 }, api.Released);
        Assert.Empty(api.Destroyed);
        Assert.Equal(0, api.MapRangeCount);
        Assert.Equal(0u, readback.BufferSize);
    }

    [Theory]
    [InlineData(129u, 96u, 1u, 4u, 768u)]
    [InlineData(96u, 97u, 1u, 4u, 512u)]
    [InlineData(96u, 96u, 2u, 4u, 512u)]
    [InlineData(96u, 96u, 1u, 16u, 1536u)]
    public void TextureReadbackGrowthRetainsTheNewRowLayout(
        uint width, uint height, uint layers, uint bytesPerPixel, uint bytesPerRow)
    {
        var api = new RecordingApi();
        using var context = CreateContext(api);
        using var readback = new GpuTextureReadbackBuffer(context);
        readback.EnsureCapacity(96, 96, 1, 4);
        AssertReadbackLayout(readback, 96, 96, 1, 4, 512);

        readback.EnsureCapacity(width, height, layers, bytesPerPixel);

        AssertReadbackLayout(readback, width, height, layers, bytesPerPixel, bytesPerRow);
        Assert.Equal(new ulong[] { 512 * 96, (ulong)bytesPerRow * height * layers }, api.CreatedSizes);
        Assert.Empty(api.Released); // The previous buffer still follows queued retirement.
        Assert.Empty(api.Unmapped);
        Assert.Equal(0, api.MapRangeCount);
        context.CleanupPendingResources();
        Assert.Equal(new nuint[] { 101 }, api.Released);

        readback.Dispose();
        readback.Dispose();
        context.CleanupPendingResources();
        Assert.Equal(new nuint[] { 101, 102 }, api.Released);
        Assert.Equal(0u, readback.Width);
        Assert.Equal(0u, readback.BytesPerRow);
        Assert.Equal(0u, readback.BufferSize);
    }

    [Fact]
    public void TextureReadbackReuseUpdatesDimensionsAndPixelWidthWithoutAllocating()
    {
        var api = new RecordingApi();
        using var context = CreateContext(api);
        using var readback = new GpuTextureReadbackBuffer(context);
        readback.EnsureCapacity(96, 96, 1, 16);
        AssertReadbackLayout(readback, 96, 96, 1, 16, 1536);

        readback.EnsureCapacity(96, 96, 1, 16);
        AssertReadbackLayout(readback, 96, 96, 1, 16, 1536);
        readback.EnsureCapacity(96, 64, 2, 4);
        AssertReadbackLayout(readback, 96, 64, 2, 4, 512);
        readback.EnsureCapacity(0, 0, 0, 4);
        AssertReadbackLayout(readback, 1, 1, 1, 4, 256);

        Assert.Equal(new ulong[] { 1536 * 96 }, api.CreatedSizes);
        Assert.Empty(api.Released);
        Assert.Empty(api.Unmapped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextureReadbackFailedAllocationDoesNotPublishAUsableLayout(bool replacing)
    {
        var api = new RecordingApi();
        using var context = CreateContext(api);
        using var readback = new GpuTextureReadbackBuffer(context);
        if (replacing) readback.EnsureCapacity(96, 96, 1, 4);
        api.ReturnNull = true;

        Assert.Throws<InvalidOperationException>(() => readback.EnsureCapacity(96, 96, 1, 16));

        Assert.Equal(0u, readback.Width);
        Assert.Equal(0u, readback.Height);
        Assert.Equal(0u, readback.DepthOrArrayLayers);
        Assert.Equal(0u, readback.BytesPerPixel);
        Assert.Equal(0u, readback.BytesPerRow);
        Assert.Equal(0u, readback.BufferSize);
        Assert.Empty(api.Released);
        context.CleanupPendingResources();
        Assert.Equal(replacing ? new nuint[] { 101 } : Array.Empty<nuint>(), api.Released);
    }

    private static void AssertReadbackLayout(GpuTextureReadbackBuffer readback,
        uint width, uint height, uint layers, uint bytesPerPixel, uint bytesPerRow)
    {
        Assert.Equal(width, readback.Width);
        Assert.Equal(height, readback.Height);
        Assert.Equal(layers, readback.DepthOrArrayLayers);
        Assert.Equal(bytesPerPixel, readback.BytesPerPixel);
        Assert.Equal(bytesPerRow, readback.BytesPerRow);
        Assert.Equal(checked(bytesPerRow * height * layers), readback.BufferSize);
    }

    [Fact]
    public void OrdinaryReadbackRejectsErrorBufferBeforeEncodingCopy()
    {
        var api = new RecordingApi();
        using var context = CreateContext(api);
        using var buffer = new GpuBuffer(context, 64, BufferUsage.CopySrc);
        api.OnCreate = _ => context.ReportDeviceLost(DeviceLostReason.Unknown, "Readback allocation failed");
        Assert.Throws<WgpuDeviceLostException>(() => buffer.ReadBytes(new byte[4]));
        Assert.Equal(2, api.CreateCount);
        Assert.Equal(new nuint[] { 102 }, api.Released);
        Assert.Empty(api.Destroyed);
        Assert.Equal(0, api.MapRangeCount);
    }

    [Fact]
    public void OrdinaryNullAllocationIsNotReclassifiedAsDeviceLoss()
    {
        var api = new RecordingApi { ReturnNull = true };
        using var context = CreateContext(api);
        Assert.Throws<InvalidOperationException>(() => new GpuBuffer(context, 64, BufferUsage.Uniform));
        Assert.False(context.IsDeviceLost);
        Assert.Empty(api.Released);
    }

    [Fact]
    public void IndependentLiveContextStillAllocatesAfterAnotherDeviceIsLost()
    {
        var firstApi = new RecordingApi();
        var secondApi = new RecordingApi();
        using var first = CreateContext(firstApi);
        using var second = CreateContext(secondApi);
        first.ReportDeviceLost(DeviceLostReason.Unknown, "Only first device lost");
        using var buffer = new GpuBuffer(second, 64, BufferUsage.Uniform);
        Assert.Equal((nuint)101, (nuint)buffer.BufferPtr);
        Assert.False(second.IsDeviceLost);
        Assert.Equal(0, firstApi.CreateCount);
        Assert.Equal(1, secondApi.CreateCount);
    }

    private static WgpuContext CreateContext(RecordingApi api)
    {
        var context = new WgpuContext();
        context.InitializeExternalNativeDevice(api, new DeviceLifetime(),
            (Device*)1, (Queue*)2, TextureFormat.Bgra8Unorm);
        return context;
    }

    private sealed class DeviceLifetime : IWebGpuExternalDeviceLifetime
    {
        public void Poll(bool wait) { }
        public void Dispose() { }
    }

    // Opaque handles only: unexpected operations fail rather than dereferencing
    // a fake native pointer. OnCreate models the native synchronous error callback.
    private sealed class RecordingApi : IWebGpuApi
    {
        public int CreateCount;
        public int MapRangeCount;
        public bool ReturnNull;
        public Action<int>? OnCreate;
        public readonly List<ulong> CreatedSizes = new();
        public readonly List<nuint> Released = new();
        public readonly List<nuint> Destroyed = new();
        public readonly List<nuint> Unmapped = new();
        public BindGroup* DeviceCreateBindGroup(Device* device, BindGroupDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public BindGroupLayout* DeviceCreateBindGroupLayout(Device* device, BindGroupLayoutDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public WgpuBuffer* DeviceCreateBuffer(Device* device, BufferDescriptor* descriptor) {
            CreateCount++;
            CreatedSizes.Add(descriptor->Size);
            OnCreate?.Invoke(CreateCount);
            return ReturnNull ? null : (WgpuBuffer*)(nuint)(100 + CreateCount);
        }
        public CommandEncoder* DeviceCreateCommandEncoder(Device* device, CommandEncoderDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public ComputePipeline* DeviceCreateComputePipeline(Device* device, ComputePipelineDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public PipelineLayout* DeviceCreatePipelineLayout(Device* device, PipelineLayoutDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public RenderPipeline* DeviceCreateRenderPipeline(Device* device, RenderPipelineDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public Sampler* DeviceCreateSampler(Device* device, SamplerDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public ShaderModule* DeviceCreateShaderModule(Device* device, ShaderModuleDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public Texture* DeviceCreateTexture(Device* device, TextureDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public TextureView* TextureCreateView(Texture* texture, TextureViewDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public BindGroupLayout* ComputePipelineGetBindGroupLayout(ComputePipeline* computePipeline, uint groupIndex) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public BindGroupLayout* RenderPipelineGetBindGroupLayout(RenderPipeline* renderPipeline, uint groupIndex) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public ComputePassEncoder* CommandEncoderBeginComputePass(CommandEncoder* commandEncoder, ComputePassDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public RenderPassEncoder* CommandEncoderBeginRenderPass(CommandEncoder* commandEncoder, RenderPassDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void CommandEncoderCopyBufferToBuffer(CommandEncoder* commandEncoder, WgpuBuffer* source, ulong sourceOffset, WgpuBuffer* destination, ulong destinationOffset, ulong size) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void CommandEncoderCopyBufferToTexture(CommandEncoder* commandEncoder, ImageCopyBuffer* source, ImageCopyTexture* destination, Extent3D* copySize) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void CommandEncoderCopyTextureToBuffer(CommandEncoder* commandEncoder, ImageCopyTexture* source, ImageCopyBuffer* destination, Extent3D* copySize) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void CommandEncoderCopyTextureToTexture(CommandEncoder* commandEncoder, ImageCopyTexture* source, ImageCopyTexture* destination, Extent3D* copySize) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public CommandBuffer* CommandEncoderFinish(CommandEncoder* commandEncoder, CommandBufferDescriptor* descriptor) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void ComputePassEncoderSetPipeline(ComputePassEncoder* pass, ComputePipeline* pipeline) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void ComputePassEncoderSetBindGroup(ComputePassEncoder* pass, uint groupIndex, BindGroup* group, nuint dynamicOffsetCount, uint* dynamicOffsets) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void ComputePassEncoderDispatchWorkgroups(ComputePassEncoder* pass, uint x, uint y, uint z) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void ComputePassEncoderDispatchWorkgroupsIndirect(ComputePassEncoder* pass, WgpuBuffer* indirectBuffer, ulong indirectOffset) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void ComputePassEncoderEnd(ComputePassEncoder* pass) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderSetPipeline(RenderPassEncoder* pass, RenderPipeline* pipeline) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderSetBindGroup(RenderPassEncoder* pass, uint groupIndex, BindGroup* group, nuint dynamicOffsetCount, uint* dynamicOffsets) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderSetVertexBuffer(RenderPassEncoder* pass, uint slot, WgpuBuffer* buffer, ulong offset, ulong size) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderSetIndexBuffer(RenderPassEncoder* pass, WgpuBuffer* buffer, IndexFormat format, ulong offset, ulong size) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderSetScissorRect(RenderPassEncoder* pass, uint x, uint y, uint width, uint height) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderSetStencilReference(RenderPassEncoder* pass, uint reference) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderSetViewport(RenderPassEncoder* pass, float x, float y, float width, float height, float minDepth, float maxDepth) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderDraw(RenderPassEncoder* pass, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderDrawIndexed(RenderPassEncoder* pass, uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderEnd(RenderPassEncoder* pass) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void QueueWriteBuffer(Queue* queue, WgpuBuffer* buffer, ulong bufferOffset, void* data, nuint size) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void QueueWriteTexture(Queue* queue, ImageCopyTexture* destination, void* data, nuint dataSize, TextureDataLayout* dataLayout, Extent3D* writeSize) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void QueueSubmit(Queue* queue, nuint commandCount, CommandBuffer** commands) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void BufferMapAsync(WgpuBuffer* buffer, MapMode mode, nuint offset, nuint size, PfnBufferMapCallback callback, void* userData) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public Task<BufferMapAsyncStatus> BufferMapAsyncTask(WgpuBuffer* buffer, MapMode mode, nuint offset, nuint size) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void* BufferGetMappedRange(WgpuBuffer* buffer, nuint offset, nuint size) { MapRangeCount++; return null; }
        public void* BufferGetConstMappedRange(WgpuBuffer* buffer, nuint offset, nuint size) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void BufferUnmap(WgpuBuffer* buffer) => Unmapped.Add((nuint)buffer);
        public void BufferDestroy(WgpuBuffer* buffer) => Destroyed.Add((nuint)buffer);
        public void SurfaceGetCurrentTexture(Surface* surface, SurfaceTexture* surfaceTexture) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void SurfacePresent(Surface* surface) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void SurfaceRelease(Surface* surface) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void BindGroupRelease(BindGroup* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void BindGroupLayoutRelease(BindGroupLayout* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void BufferRelease(WgpuBuffer* value) => Released.Add((nuint)value);
        public void CommandBufferRelease(CommandBuffer* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void CommandEncoderRelease(CommandEncoder* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void ComputePassEncoderRelease(ComputePassEncoder* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void ComputePipelineRelease(ComputePipeline* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void PipelineLayoutRelease(PipelineLayout* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPassEncoderRelease(RenderPassEncoder* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void RenderPipelineRelease(RenderPipeline* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void SamplerRelease(Sampler* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void ShaderModuleRelease(ShaderModule* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void TextureDestroy(Texture* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void TextureRelease(Texture* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
        public void TextureViewRelease(TextureView* value) => throw new NotSupportedException("Unexpected WebGPU operation in buffer admission test.");
    }
}
