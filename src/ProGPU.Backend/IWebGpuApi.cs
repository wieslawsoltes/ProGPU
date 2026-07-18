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
    QuerySet* DeviceCreateQuerySet(Device* device, QuerySetDescriptor* descriptor);
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
    void CommandEncoderCopyTextureToBuffer(CommandEncoder* commandEncoder, ImageCopyTexture* source, ImageCopyBuffer* destination, Extent3D* copySize);
    void CommandEncoderCopyTextureToTexture(CommandEncoder* commandEncoder, ImageCopyTexture* source, ImageCopyTexture* destination, Extent3D* copySize);
    void CommandEncoderWriteTimestamp(CommandEncoder* commandEncoder, QuerySet* querySet, uint queryIndex);
    void CommandEncoderResolveQuerySet(CommandEncoder* commandEncoder, QuerySet* querySet, uint firstQuery, uint queryCount, WgpuBuffer* destination, ulong destinationOffset);
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
    void QueueOnSubmittedWorkDone(Queue* queue, PfnQueueWorkDoneCallback callback, void* userData);

    void BufferMapAsync(WgpuBuffer* buffer, MapMode mode, nuint offset, nuint size, PfnBufferMapCallback callback, void* userData);
    Task<BufferMapAsyncStatus> BufferMapAsyncTask(WgpuBuffer* buffer, MapMode mode, nuint offset, nuint size);
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
    void QuerySetRelease(QuerySet* value);
    void PipelineLayoutRelease(PipelineLayout* value);
    void RenderPassEncoderRelease(RenderPassEncoder* value);
    void RenderPipelineRelease(RenderPipeline* value);
    void SamplerRelease(Sampler* value);
    void ShaderModuleRelease(ShaderModule* value);
    void TextureDestroy(Texture* value);
    void TextureRelease(Texture* value);
    void TextureViewRelease(TextureView* value);
}

internal unsafe sealed class SilkWebGpuApi(WebGPU api) : IWebGpuApi
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

    public BindGroup* DeviceCreateBindGroup(Device* d, BindGroupDescriptor* x) => api.DeviceCreateBindGroup(d, x);
    public BindGroupLayout* DeviceCreateBindGroupLayout(Device* d, BindGroupLayoutDescriptor* x) => api.DeviceCreateBindGroupLayout(d, x);
    public WgpuBuffer* DeviceCreateBuffer(Device* d, BufferDescriptor* x) => api.DeviceCreateBuffer(d, x);
    public CommandEncoder* DeviceCreateCommandEncoder(Device* d, CommandEncoderDescriptor* x) => api.DeviceCreateCommandEncoder(d, x);
    public ComputePipeline* DeviceCreateComputePipeline(Device* d, ComputePipelineDescriptor* x) => api.DeviceCreateComputePipeline(d, x);
    public QuerySet* DeviceCreateQuerySet(Device* d, QuerySetDescriptor* x) => api.DeviceCreateQuerySet(d, x);
    public PipelineLayout* DeviceCreatePipelineLayout(Device* d, PipelineLayoutDescriptor* x) => api.DeviceCreatePipelineLayout(d, x);
    public RenderPipeline* DeviceCreateRenderPipeline(Device* d, RenderPipelineDescriptor* x) => api.DeviceCreateRenderPipeline(d, x);
    public Sampler* DeviceCreateSampler(Device* d, SamplerDescriptor* x) => api.DeviceCreateSampler(d, x);
    public ShaderModule* DeviceCreateShaderModule(Device* d, ShaderModuleDescriptor* x) => api.DeviceCreateShaderModule(d, x);
    public Texture* DeviceCreateTexture(Device* d, TextureDescriptor* x) => api.DeviceCreateTexture(d, x);
    public TextureView* TextureCreateView(Texture* t, TextureViewDescriptor* x) => api.TextureCreateView(t, x);
    public BindGroupLayout* ComputePipelineGetBindGroupLayout(ComputePipeline* p, uint i) => api.ComputePipelineGetBindGroupLayout(p, i);
    public BindGroupLayout* RenderPipelineGetBindGroupLayout(RenderPipeline* p, uint i) => api.RenderPipelineGetBindGroupLayout(p, i);
    public ComputePassEncoder* CommandEncoderBeginComputePass(CommandEncoder* e, ComputePassDescriptor* x) => api.CommandEncoderBeginComputePass(e, x);
    public RenderPassEncoder* CommandEncoderBeginRenderPass(CommandEncoder* e, RenderPassDescriptor* x) => api.CommandEncoderBeginRenderPass(e, x);
    public void CommandEncoderCopyBufferToBuffer(CommandEncoder* e, WgpuBuffer* s, ulong so, WgpuBuffer* d, ulong @do, ulong z) => api.CommandEncoderCopyBufferToBuffer(e, s, so, d, @do, z);
    public void CommandEncoderCopyTextureToBuffer(CommandEncoder* e, ImageCopyTexture* s, ImageCopyBuffer* d, Extent3D* z) => api.CommandEncoderCopyTextureToBuffer(e, s, d, z);
    public void CommandEncoderCopyTextureToTexture(CommandEncoder* e, ImageCopyTexture* s, ImageCopyTexture* d, Extent3D* z) => api.CommandEncoderCopyTextureToTexture(e, s, d, z);
    public void CommandEncoderWriteTimestamp(CommandEncoder* e, QuerySet* q, uint i) => api.CommandEncoderWriteTimestamp(e, q, i);
    public void CommandEncoderResolveQuerySet(CommandEncoder* e, QuerySet* q, uint f, uint c, WgpuBuffer* d, ulong o) => api.CommandEncoderResolveQuerySet(e, q, f, c, d, o);
    public CommandBuffer* CommandEncoderFinish(CommandEncoder* e, CommandBufferDescriptor* x) => api.CommandEncoderFinish(e, x);
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
    public void QueueWriteBuffer(Queue* q, WgpuBuffer* b, ulong o, void* d, nuint z) => api.QueueWriteBuffer(q, b, o, d, z);
    public void QueueWriteTexture(Queue* q, ImageCopyTexture* d, void* p, nuint z, TextureDataLayout* l, Extent3D* s) => api.QueueWriteTexture(q, d, p, z, l, s);
    public void QueueSubmit(Queue* q, nuint c, CommandBuffer** b) => api.QueueSubmit(q, c, b);
    public void QueueOnSubmittedWorkDone(Queue* q, PfnQueueWorkDoneCallback c, void* u) => api.QueueOnSubmittedWorkDone(q, c, u);
    public void BufferMapAsync(WgpuBuffer* b, MapMode m, nuint o, nuint z, PfnBufferMapCallback c, void* u) => api.BufferMapAsync(b, m, o, z, c, u);
    public Task<BufferMapAsyncStatus> BufferMapAsyncTask(WgpuBuffer* b, MapMode m, nuint o, nuint z)
    {
        var completion = new MapCompletion();
        completion.Handle = GCHandle.Alloc(completion);
        try
        {
            api.BufferMapAsync(
                b,
                m,
                o,
                z,
                new PfnBufferMapCallback(&CompleteBufferMap),
                (void*)GCHandle.ToIntPtr(completion.Handle));
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
    public void* BufferGetConstMappedRange(WgpuBuffer* b, nuint o, nuint z) => api.BufferGetConstMappedRange(b, o, z);
    public void BufferUnmap(WgpuBuffer* b) => api.BufferUnmap(b);
    public void BufferDestroy(WgpuBuffer* b) => api.BufferDestroy(b);
    public void SurfaceGetCurrentTexture(Surface* s, SurfaceTexture* t) => api.SurfaceGetCurrentTexture(s, t);
    public void SurfacePresent(Surface* s) => api.SurfacePresent(s);
    public void SurfaceRelease(Surface* s) => api.SurfaceRelease(s);
    public void BindGroupRelease(BindGroup* v) => api.BindGroupRelease(v);
    public void BindGroupLayoutRelease(BindGroupLayout* v) => api.BindGroupLayoutRelease(v);
    public void BufferRelease(WgpuBuffer* v) => api.BufferRelease(v);
    public void CommandBufferRelease(CommandBuffer* v) => api.CommandBufferRelease(v);
    public void CommandEncoderRelease(CommandEncoder* v) => api.CommandEncoderRelease(v);
    public void ComputePassEncoderRelease(ComputePassEncoder* v) => api.ComputePassEncoderRelease(v);
    public void ComputePipelineRelease(ComputePipeline* v) => api.ComputePipelineRelease(v);
    public void QuerySetRelease(QuerySet* v) => api.QuerySetRelease(v);
    public void PipelineLayoutRelease(PipelineLayout* v) => api.PipelineLayoutRelease(v);
    public void RenderPassEncoderRelease(RenderPassEncoder* v) => api.RenderPassEncoderRelease(v);
    public void RenderPipelineRelease(RenderPipeline* v) => api.RenderPipelineRelease(v);
    public void SamplerRelease(Sampler* v) => api.SamplerRelease(v);
    public void ShaderModuleRelease(ShaderModule* v) => api.ShaderModuleRelease(v);
    public void TextureDestroy(Texture* v) => api.TextureDestroy(v);
    public void TextureRelease(Texture* v) => api.TextureRelease(v);
    public void TextureViewRelease(TextureView* v) => api.TextureViewRelease(v);
}
