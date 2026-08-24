using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;

namespace ProGPU.Backend;

/// <summary>
/// Typed WebGPU operation seam used by ProGPU renderers. The native implementation is a
/// zero-policy forwarding layer; browser implementations serialize the same descriptors
/// into a batched command stream.
/// </summary>
public unsafe interface IWebGpuApi
{
    BindGroup* DeviceCreateBindGroup(Device* device, BindGroupDescriptor* descriptor);
    BindGroupLayout* DeviceCreateBindGroupLayout(Device* device, BindGroupLayoutDescriptor* descriptor);
    WgpuBuffer* DeviceCreateBuffer(Device* device, BufferDescriptor* descriptor);
    CommandEncoder* DeviceCreateCommandEncoder(Device* device, CommandEncoderDescriptor* descriptor);
    ComputePipeline* DeviceCreateComputePipeline(Device* device, ComputePipelineDescriptor* descriptor);
    PipelineLayout* DeviceCreatePipelineLayout(Device* device, PipelineLayoutDescriptor* descriptor);
    RenderPipeline* DeviceCreateRenderPipeline(Device* device, RenderPipelineDescriptor* descriptor);
    Sampler* DeviceCreateSampler(Device* device, SamplerDescriptor* descriptor);
    ShaderModule* DeviceCreateShaderModule(Device* device, ShaderModuleDescriptor* descriptor);
    Texture* DeviceCreateTexture(Device* device, TextureDescriptor* descriptor);
    TextureView* TextureCreateView(Texture* texture, TextureViewDescriptor* descriptor);
    BindGroupLayout* ComputePipelineGetBindGroupLayout(ComputePipeline* computePipeline, uint groupIndex);
    BindGroupLayout* RenderPipelineGetBindGroupLayout(RenderPipeline* renderPipeline, uint groupIndex);

    ComputePassEncoder* CommandEncoderBeginComputePass(CommandEncoder* commandEncoder, ComputePassDescriptor* descriptor);
    RenderPassEncoder* CommandEncoderBeginRenderPass(CommandEncoder* commandEncoder, RenderPassDescriptor* descriptor);
    void CommandEncoderCopyBufferToBuffer(CommandEncoder* commandEncoder, WgpuBuffer* source, ulong sourceOffset, WgpuBuffer* destination, ulong destinationOffset, ulong size);
    void CommandEncoderCopyBufferToTexture(CommandEncoder* commandEncoder, ImageCopyBuffer* source, ImageCopyTexture* destination, Extent3D* copySize);
    void CommandEncoderCopyTextureToBuffer(CommandEncoder* commandEncoder, ImageCopyTexture* source, ImageCopyBuffer* destination, Extent3D* copySize);
    void CommandEncoderCopyTextureToTexture(CommandEncoder* commandEncoder, ImageCopyTexture* source, ImageCopyTexture* destination, Extent3D* copySize);
    CommandBuffer* CommandEncoderFinish(CommandEncoder* commandEncoder, CommandBufferDescriptor* descriptor);

    void ComputePassEncoderSetPipeline(ComputePassEncoder* pass, ComputePipeline* pipeline);
    void ComputePassEncoderSetBindGroup(ComputePassEncoder* pass, uint groupIndex, BindGroup* group, nuint dynamicOffsetCount, uint* dynamicOffsets);
    void ComputePassEncoderDispatchWorkgroups(ComputePassEncoder* pass, uint x, uint y, uint z);
    void ComputePassEncoderEnd(ComputePassEncoder* pass);

    void RenderPassEncoderSetPipeline(RenderPassEncoder* pass, RenderPipeline* pipeline);
    void RenderPassEncoderSetBindGroup(RenderPassEncoder* pass, uint groupIndex, BindGroup* group, nuint dynamicOffsetCount, uint* dynamicOffsets);
    void RenderPassEncoderSetVertexBuffer(RenderPassEncoder* pass, uint slot, WgpuBuffer* buffer, ulong offset, ulong size);
    void RenderPassEncoderSetIndexBuffer(RenderPassEncoder* pass, WgpuBuffer* buffer, IndexFormat format, ulong offset, ulong size);
    void RenderPassEncoderSetScissorRect(RenderPassEncoder* pass, uint x, uint y, uint width, uint height);
    void RenderPassEncoderSetStencilReference(RenderPassEncoder* pass, uint reference);
    void RenderPassEncoderSetViewport(RenderPassEncoder* pass, float x, float y, float width, float height, float minDepth, float maxDepth);
    void RenderPassEncoderDraw(RenderPassEncoder* pass, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);
    void RenderPassEncoderDrawIndexed(RenderPassEncoder* pass, uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance);
    void RenderPassEncoderEnd(RenderPassEncoder* pass);

    void QueueWriteBuffer(Queue* queue, WgpuBuffer* buffer, ulong bufferOffset, void* data, nuint size);
    void QueueWriteTexture(Queue* queue, ImageCopyTexture* destination, void* data, nuint dataSize, TextureDataLayout* dataLayout, Extent3D* writeSize);
    void QueueSubmit(Queue* queue, nuint commandCount, CommandBuffer** commands);

    void BufferMapAsync(WgpuBuffer* buffer, MapMode mode, nuint offset, nuint size, PfnBufferMapCallback callback, void* userData);
    Task<BufferMapAsyncStatus> BufferMapAsyncTask(WgpuBuffer* buffer, MapMode mode, nuint offset, nuint size);
    void* BufferGetMappedRange(WgpuBuffer* buffer, nuint offset, nuint size);
    void* BufferGetConstMappedRange(WgpuBuffer* buffer, nuint offset, nuint size);
    void BufferUnmap(WgpuBuffer* buffer);
    void BufferDestroy(WgpuBuffer* buffer);

    void SurfaceGetCurrentTexture(Surface* surface, SurfaceTexture* surfaceTexture);
    void SurfacePresent(Surface* surface);
    void SurfaceRelease(Surface* surface);

    void BindGroupRelease(BindGroup* value);
    void BindGroupLayoutRelease(BindGroupLayout* value);
    void BufferRelease(WgpuBuffer* value);
    void CommandBufferRelease(CommandBuffer* value);
    void CommandEncoderRelease(CommandEncoder* value);
    void ComputePassEncoderRelease(ComputePassEncoder* value);
    void ComputePipelineRelease(ComputePipeline* value);
    void PipelineLayoutRelease(PipelineLayout* value);
    void RenderPassEncoderRelease(RenderPassEncoder* value);
    void RenderPipelineRelease(RenderPipeline* value);
    void SamplerRelease(Sampler* value);
    void ShaderModuleRelease(ShaderModule* value);
    void TextureDestroy(Texture* value);
    void TextureRelease(Texture* value);
    void TextureViewRelease(TextureView* value);
}

/// <summary>
/// Optional render-bundle operations for providers that can retain encoded
/// draw state across otherwise identical frames.
/// </summary>
/// <remarks>
/// The core renderer continues to use <see cref="IWebGpuApi"/> when this
/// capability is unavailable. Render bundles retain the buffers, bind groups,
/// and pipelines referenced while recording, so callers must release a bundle
/// before replacing any captured scene resource.
/// </remarks>
public unsafe interface IWebGpuRenderBundleApi
{
    RenderBundleEncoder* DeviceCreateRenderBundleEncoder(
        Device* device,
        RenderBundleEncoderDescriptor* descriptor);
    void RenderBundleEncoderSetPipeline(
        RenderBundleEncoder* encoder,
        RenderPipeline* pipeline);
    void RenderBundleEncoderSetBindGroup(
        RenderBundleEncoder* encoder,
        uint groupIndex,
        BindGroup* group,
        nuint dynamicOffsetCount,
        uint* dynamicOffsets);
    void RenderBundleEncoderSetVertexBuffer(
        RenderBundleEncoder* encoder,
        uint slot,
        WgpuBuffer* buffer,
        ulong offset,
        ulong size);
    void RenderBundleEncoderSetIndexBuffer(
        RenderBundleEncoder* encoder,
        WgpuBuffer* buffer,
        IndexFormat format,
        ulong offset,
        ulong size);
    void RenderBundleEncoderDraw(
        RenderBundleEncoder* encoder,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance);
    void RenderBundleEncoderDrawIndexed(
        RenderBundleEncoder* encoder,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int baseVertex,
        uint firstInstance);
    RenderBundle* RenderBundleEncoderFinish(
        RenderBundleEncoder* encoder,
        RenderBundleDescriptor* descriptor);
    void RenderBundleEncoderRelease(RenderBundleEncoder* encoder);
    void RenderPassEncoderExecuteBundles(
        RenderPassEncoder* pass,
        nuint bundleCount,
        RenderBundle** bundles);
    void RenderBundleRelease(RenderBundle* bundle);
}

/// <summary>
/// Optional native-presentation contract for an exact-ABI external WebGPU
/// backend. It lets a host resize an already selected external device without
/// routing surface descriptors through a second ABI.
/// </summary>
public unsafe interface IWebGpuExternalSurfaceApi
{
    void ConfigureExternalSurface(
        Surface* surface,
        uint width,
        uint height);

    void UnconfigureExternalSurface(Surface* surface);
}

/// <summary>
/// Owns the instance, adapter, device, and queue behind an externally created
/// native <see cref="IWebGpuApi"/> implementation.
/// </summary>
/// <remarks>
/// The context releases every ProGPU resource before disposing this lifetime.
/// Polling is allocation-free and must service callbacks when
/// <paramref name="wait"/> is false and wait for submitted work when it is
/// true.
/// </remarks>
public interface IWebGpuExternalDeviceLifetime : IDisposable
{
    void Poll(bool wait);
}

internal unsafe sealed class SilkWebGpuApi(
    WebGPU api,
    object synchronizationRoot) : IWebGpuApi, IWebGpuRenderBundleApi
{
    private sealed class MapCompletion
    {
        public readonly TaskCompletionSource<BufferMapAsyncStatus> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GCHandle Handle;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void CompleteBufferMap(BufferMapAsyncStatus status, void* userData)
    {
        var handle = GCHandle.FromIntPtr((nint)userData);
        if (handle.Target is MapCompletion completion)
        {
            completion.Source.TrySetResult(status);
        }
        if (handle.IsAllocated)
        {
            handle.Free();
        }
    }

    public BindGroup* DeviceCreateBindGroup(Device* d, BindGroupDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreateBindGroup(d, x);
    }
    public BindGroupLayout* DeviceCreateBindGroupLayout(Device* d, BindGroupLayoutDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreateBindGroupLayout(d, x);
    }
    public WgpuBuffer* DeviceCreateBuffer(Device* d, BufferDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreateBuffer(d, x);
    }
    public CommandEncoder* DeviceCreateCommandEncoder(Device* d, CommandEncoderDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreateCommandEncoder(d, x);
    }
    public ComputePipeline* DeviceCreateComputePipeline(Device* d, ComputePipelineDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreateComputePipeline(d, x);
    }
    public PipelineLayout* DeviceCreatePipelineLayout(Device* d, PipelineLayoutDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreatePipelineLayout(d, x);
    }
    public RenderPipeline* DeviceCreateRenderPipeline(Device* d, RenderPipelineDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreateRenderPipeline(d, x);
    }
    public Sampler* DeviceCreateSampler(Device* d, SamplerDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreateSampler(d, x);
    }
    public ShaderModule* DeviceCreateShaderModule(Device* d, ShaderModuleDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreateShaderModule(d, x);
    }
    public Texture* DeviceCreateTexture(Device* d, TextureDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreateTexture(d, x);
    }
    public TextureView* TextureCreateView(Texture* t, TextureViewDescriptor* x)
    {
        lock (synchronizationRoot) return api.TextureCreateView(t, x);
    }
    public BindGroupLayout* ComputePipelineGetBindGroupLayout(ComputePipeline* p, uint i)
    {
        lock (synchronizationRoot) return api.ComputePipelineGetBindGroupLayout(p, i);
    }
    public BindGroupLayout* RenderPipelineGetBindGroupLayout(RenderPipeline* p, uint i)
    {
        lock (synchronizationRoot) return api.RenderPipelineGetBindGroupLayout(p, i);
    }
    public ComputePassEncoder* CommandEncoderBeginComputePass(CommandEncoder* e, ComputePassDescriptor* x)
    {
        lock (synchronizationRoot) return api.CommandEncoderBeginComputePass(e, x);
    }
    public RenderPassEncoder* CommandEncoderBeginRenderPass(CommandEncoder* e, RenderPassDescriptor* x)
    {
        lock (synchronizationRoot) return api.CommandEncoderBeginRenderPass(e, x);
    }
    public RenderBundleEncoder* DeviceCreateRenderBundleEncoder(Device* d, RenderBundleEncoderDescriptor* x)
    {
        lock (synchronizationRoot) return api.DeviceCreateRenderBundleEncoder(d, x);
    }
    public void CommandEncoderCopyBufferToBuffer(CommandEncoder* e, WgpuBuffer* s, ulong so, WgpuBuffer* d, ulong @do, ulong z) => api.CommandEncoderCopyBufferToBuffer(e, s, so, d, @do, z);
    public void CommandEncoderCopyBufferToTexture(CommandEncoder* e, ImageCopyBuffer* s, ImageCopyTexture* d, Extent3D* z) => api.CommandEncoderCopyBufferToTexture(e, s, d, z);
    public void CommandEncoderCopyTextureToBuffer(CommandEncoder* e, ImageCopyTexture* s, ImageCopyBuffer* d, Extent3D* z) => api.CommandEncoderCopyTextureToBuffer(e, s, d, z);
    public void CommandEncoderCopyTextureToTexture(CommandEncoder* e, ImageCopyTexture* s, ImageCopyTexture* d, Extent3D* z) => api.CommandEncoderCopyTextureToTexture(e, s, d, z);
    public CommandBuffer* CommandEncoderFinish(CommandEncoder* e, CommandBufferDescriptor* x)
    {
        lock (synchronizationRoot) return api.CommandEncoderFinish(e, x);
    }
    public void ComputePassEncoderSetPipeline(ComputePassEncoder* p, ComputePipeline* x) => api.ComputePassEncoderSetPipeline(p, x);
    public void ComputePassEncoderSetBindGroup(ComputePassEncoder* p, uint i, BindGroup* g, nuint c, uint* o) => api.ComputePassEncoderSetBindGroup(p, i, g, c, o);
    public void ComputePassEncoderDispatchWorkgroups(ComputePassEncoder* p, uint x, uint y, uint z) => api.ComputePassEncoderDispatchWorkgroups(p, x, y, z);
    public void ComputePassEncoderEnd(ComputePassEncoder* p) => api.ComputePassEncoderEnd(p);
    public void RenderPassEncoderSetPipeline(RenderPassEncoder* p, RenderPipeline* x) => api.RenderPassEncoderSetPipeline(p, x);
    public void RenderPassEncoderSetBindGroup(RenderPassEncoder* p, uint i, BindGroup* g, nuint c, uint* o) => api.RenderPassEncoderSetBindGroup(p, i, g, c, o);
    public void RenderPassEncoderSetVertexBuffer(RenderPassEncoder* p, uint s, WgpuBuffer* b, ulong o, ulong z) => api.RenderPassEncoderSetVertexBuffer(p, s, b, o, z);
    public void RenderPassEncoderSetIndexBuffer(RenderPassEncoder* p, WgpuBuffer* b, IndexFormat f, ulong o, ulong z) => api.RenderPassEncoderSetIndexBuffer(p, b, f, o, z);
    public void RenderPassEncoderSetScissorRect(RenderPassEncoder* p, uint x, uint y, uint w, uint h) => api.RenderPassEncoderSetScissorRect(p, x, y, w, h);
    public void RenderPassEncoderSetStencilReference(RenderPassEncoder* p, uint r) => api.RenderPassEncoderSetStencilReference(p, r);
    public void RenderPassEncoderSetViewport(RenderPassEncoder* p, float x, float y, float w, float h, float n, float f) => api.RenderPassEncoderSetViewport(p, x, y, w, h, n, f);
    public void RenderPassEncoderDraw(RenderPassEncoder* p, uint v, uint i, uint fv, uint fi) => api.RenderPassEncoderDraw(p, v, i, fv, fi);
    public void RenderPassEncoderDrawIndexed(RenderPassEncoder* p, uint i, uint c, uint f, int b, uint fi) => api.RenderPassEncoderDrawIndexed(p, i, c, f, b, fi);
    public void RenderPassEncoderEnd(RenderPassEncoder* p) => api.RenderPassEncoderEnd(p);
    public void RenderBundleEncoderSetPipeline(RenderBundleEncoder* e, RenderPipeline* p) => api.RenderBundleEncoderSetPipeline(e, p);
    public void RenderBundleEncoderSetBindGroup(RenderBundleEncoder* e, uint i, BindGroup* g, nuint c, uint* o) => api.RenderBundleEncoderSetBindGroup(e, i, g, c, o);
    public void RenderBundleEncoderSetVertexBuffer(RenderBundleEncoder* e, uint s, WgpuBuffer* b, ulong o, ulong z) => api.RenderBundleEncoderSetVertexBuffer(e, s, b, o, z);
    public void RenderBundleEncoderSetIndexBuffer(RenderBundleEncoder* e, WgpuBuffer* b, IndexFormat f, ulong o, ulong z) => api.RenderBundleEncoderSetIndexBuffer(e, b, f, o, z);
    public void RenderBundleEncoderDraw(RenderBundleEncoder* e, uint v, uint i, uint fv, uint fi) => api.RenderBundleEncoderDraw(e, v, i, fv, fi);
    public void RenderBundleEncoderDrawIndexed(RenderBundleEncoder* e, uint i, uint c, uint f, int b, uint fi) => api.RenderBundleEncoderDrawIndexed(e, i, c, f, b, fi);
    public RenderBundle* RenderBundleEncoderFinish(RenderBundleEncoder* e, RenderBundleDescriptor* d)
    {
        lock (synchronizationRoot) return api.RenderBundleEncoderFinish(e, d);
    }
    public void RenderPassEncoderExecuteBundles(RenderPassEncoder* p, nuint c, RenderBundle** b) => api.RenderPassEncoderExecuteBundles(p, c, b);
    public void QueueWriteBuffer(Queue* q, WgpuBuffer* b, ulong o, void* d, nuint z)
    {
        lock (synchronizationRoot) api.QueueWriteBuffer(q, b, o, d, z);
    }
    public void QueueWriteTexture(Queue* q, ImageCopyTexture* d, void* p, nuint z, TextureDataLayout* l, Extent3D* s)
    {
        lock (synchronizationRoot) api.QueueWriteTexture(q, d, p, z, l, s);
    }
    public void QueueSubmit(Queue* q, nuint c, CommandBuffer** b)
    {
        lock (synchronizationRoot) api.QueueSubmit(q, c, b);
    }
    public void BufferMapAsync(WgpuBuffer* b, MapMode m, nuint o, nuint z, PfnBufferMapCallback c, void* u)
    {
        lock (synchronizationRoot) api.BufferMapAsync(b, m, o, z, c, u);
    }
    public Task<BufferMapAsyncStatus> BufferMapAsyncTask(WgpuBuffer* b, MapMode m, nuint o, nuint z)
    {
        var completion = new MapCompletion();
        completion.Handle = GCHandle.Alloc(completion);
        try
        {
            lock (synchronizationRoot)
            {
                api.BufferMapAsync(
                    b,
                    m,
                    o,
                    z,
                    new PfnBufferMapCallback(&CompleteBufferMap),
                    (void*)GCHandle.ToIntPtr(completion.Handle));
            }
        }
        catch
        {
            if (completion.Handle.IsAllocated)
            {
                completion.Handle.Free();
            }
            throw;
        }
        return completion.Source.Task;
    }
    public void* BufferGetMappedRange(WgpuBuffer* b, nuint o, nuint z)
    {
        lock (synchronizationRoot) return api.BufferGetMappedRange(b, o, z);
    }
    public void* BufferGetConstMappedRange(WgpuBuffer* b, nuint o, nuint z)
    {
        lock (synchronizationRoot) return api.BufferGetConstMappedRange(b, o, z);
    }
    public void BufferUnmap(WgpuBuffer* b)
    {
        lock (synchronizationRoot) api.BufferUnmap(b);
    }
    public void BufferDestroy(WgpuBuffer* b)
    {
        lock (synchronizationRoot) api.BufferDestroy(b);
    }
    public void SurfaceGetCurrentTexture(Surface* s, SurfaceTexture* t)
    {
        lock (synchronizationRoot) api.SurfaceGetCurrentTexture(s, t);
    }
    public void SurfacePresent(Surface* s)
    {
        lock (synchronizationRoot) api.SurfacePresent(s);
    }
    public void SurfaceRelease(Surface* s)
    {
        lock (synchronizationRoot) api.SurfaceRelease(s);
    }
    public void BindGroupRelease(BindGroup* v)
    {
        lock (synchronizationRoot) api.BindGroupRelease(v);
    }
    public void BindGroupLayoutRelease(BindGroupLayout* v)
    {
        lock (synchronizationRoot) api.BindGroupLayoutRelease(v);
    }
    public void BufferRelease(WgpuBuffer* v)
    {
        lock (synchronizationRoot) api.BufferRelease(v);
    }
    public void CommandBufferRelease(CommandBuffer* v)
    {
        lock (synchronizationRoot) api.CommandBufferRelease(v);
    }
    public void CommandEncoderRelease(CommandEncoder* v)
    {
        lock (synchronizationRoot) api.CommandEncoderRelease(v);
    }
    public void ComputePassEncoderRelease(ComputePassEncoder* v)
    {
        lock (synchronizationRoot) api.ComputePassEncoderRelease(v);
    }
    public void ComputePipelineRelease(ComputePipeline* v)
    {
        lock (synchronizationRoot) api.ComputePipelineRelease(v);
    }
    public void PipelineLayoutRelease(PipelineLayout* v)
    {
        lock (synchronizationRoot) api.PipelineLayoutRelease(v);
    }
    public void RenderPassEncoderRelease(RenderPassEncoder* v)
    {
        lock (synchronizationRoot) api.RenderPassEncoderRelease(v);
    }
    public void RenderBundleEncoderRelease(RenderBundleEncoder* v)
    {
        lock (synchronizationRoot) api.RenderBundleEncoderRelease(v);
    }
    public void RenderBundleRelease(RenderBundle* v)
    {
        lock (synchronizationRoot) api.RenderBundleRelease(v);
    }
    public void RenderPipelineRelease(RenderPipeline* v)
    {
        lock (synchronizationRoot) api.RenderPipelineRelease(v);
    }
    public void SamplerRelease(Sampler* v)
    {
        lock (synchronizationRoot) api.SamplerRelease(v);
    }
    public void ShaderModuleRelease(ShaderModule* v)
    {
        lock (synchronizationRoot) api.ShaderModuleRelease(v);
    }
    public void TextureDestroy(Texture* v)
    {
        lock (synchronizationRoot) api.TextureDestroy(v);
    }
    public void TextureRelease(Texture* v)
    {
        lock (synchronizationRoot) api.TextureRelease(v);
    }
    public void TextureViewRelease(TextureView* v)
    {
        lock (synchronizationRoot) api.TextureViewRelease(v);
    }
}
