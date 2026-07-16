using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;

namespace ProGPU.Browser;

/// <summary>
/// Browser implementation of the renderer-facing WebGPU seam. Silk pointer values are opaque,
/// generation-checked browser resource tokens; no browser code dereferences them.
/// </summary>
public unsafe sealed partial class BrowserWebGpuApi : IWebGpuApi, IDisposable
{
    private readonly BrowserGpuHandlePool _handles = new();
    private readonly BrowserGpuCommandEncoder _commands;
    private readonly Action<BrowserGpuCommandEncoder> _dispatch;
    private readonly Dictionary<uint, MappedBuffer> _mappedBuffers = new();
    private bool _disposed;

    public BrowserWebGpuApi(Action<BrowserGpuCommandEncoder>? dispatch = null, int initialCommandCapacity = 256 * 1024)
    {
        _commands = new BrowserGpuCommandEncoder(initialCommandCapacity);
        _dispatch = dispatch ?? BrowserGpuRuntime.Dispatch;
    }

    public static Device* DeviceHandle => (Device*)1;
    public static Queue* QueueHandle => (Queue*)2;
    public static Surface* SurfaceHandle => (Surface*)3;
    public int PendingCommandCount => _commands.CommandCount;
    public ReadOnlySpan<byte> PendingPacket => _commands.WrittenSpan;

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_commands.CommandCount == 0) return;
        _dispatch(_commands);
        _commands.Reset();
    }

    public BindGroup* DeviceCreateBindGroup(Device* device, BindGroupDescriptor* descriptor)
    {
        RequireDescriptor(descriptor);
        var handle = Allocate();
        var count = CheckedCount(descriptor->EntryCount);
        const int entrySize = 32;
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CreateBindGroup, checked(12 + count * entrySize));
        WriteHandle(payload, 0, handle);
        WriteHandle(payload, 4, HandleOf(descriptor->Layout));
        WriteUInt32(payload, 8, (uint)count);
        for (var index = 0; index < count; index++)
        {
            var entry = descriptor->Entries[index];
            var offset = 12 + index * entrySize;
            uint kind;
            BrowserGpuHandle resource;
            if (entry.Buffer != null)
            {
                kind = 1;
                resource = HandleOf(entry.Buffer);
            }
            else if (entry.Sampler != null)
            {
                kind = 2;
                resource = HandleOf(entry.Sampler);
            }
            else if (entry.TextureView != null)
            {
                kind = 3;
                resource = HandleOf(entry.TextureView);
            }
            else
            {
                throw new NotSupportedException("Browser bind group entries must contain a buffer, sampler, or texture view.");
            }
            WriteUInt32(payload, offset, entry.Binding);
            WriteUInt32(payload, offset + 4, kind);
            WriteHandle(payload, offset + 8, resource);
            WriteUInt64(payload, offset + 16, entry.Offset);
            WriteUInt64(payload, offset + 24, entry.Size);
        }
        _commands.CompleteCommand();
        return Pointer<BindGroup>(handle);
    }

    public BindGroupLayout* DeviceCreateBindGroupLayout(Device* device, BindGroupLayoutDescriptor* descriptor)
    {
        RequireDescriptor(descriptor);
        var handle = Allocate();
        var count = CheckedCount(descriptor->EntryCount);
        const int entrySize = 52;
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CreateBindGroupLayout, checked(8 + count * entrySize));
        WriteHandle(payload, 0, handle);
        WriteUInt32(payload, 4, (uint)count);
        for (var index = 0; index < count; index++)
        {
            var entry = descriptor->Entries[index];
            var offset = 8 + index * entrySize;
            WriteUInt32(payload, offset, entry.Binding);
            WriteUInt32(payload, offset + 4, (uint)entry.Visibility);
            WriteUInt32(payload, offset + 8, (uint)entry.Buffer.Type);
            WriteUInt32(payload, offset + 12, entry.Buffer.HasDynamicOffset.Value);
            WriteUInt64(payload, offset + 16, entry.Buffer.MinBindingSize);
            WriteUInt32(payload, offset + 24, (uint)entry.Sampler.Type);
            WriteUInt32(payload, offset + 28, (uint)entry.Texture.SampleType);
            WriteUInt32(payload, offset + 32, (uint)entry.Texture.ViewDimension);
            WriteUInt32(payload, offset + 36, entry.Texture.Multisampled.Value);
            WriteUInt32(payload, offset + 40, (uint)entry.StorageTexture.Access);
            WriteUInt32(payload, offset + 44, (uint)entry.StorageTexture.Format);
            WriteUInt32(payload, offset + 48, (uint)entry.StorageTexture.ViewDimension);
        }
        _commands.CompleteCommand();
        return Pointer<BindGroupLayout>(handle);
    }

    public WgpuBuffer* DeviceCreateBuffer(Device* device, BufferDescriptor* descriptor)
    {
        RequireDescriptor(descriptor);
        var handle = Allocate();
        _commands.CreateBuffer(handle, descriptor->Size, (uint)descriptor->Usage, descriptor->MappedAtCreation);
        return Pointer<WgpuBuffer>(handle);
    }

    public CommandEncoder* DeviceCreateCommandEncoder(Device* device, CommandEncoderDescriptor* descriptor)
    {
        var handle = Allocate();
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CreateCommandEncoder, 4);
        WriteHandle(payload, 0, handle);
        _commands.CompleteCommand();
        return Pointer<CommandEncoder>(handle);
    }

    public ComputePipeline* DeviceCreateComputePipeline(Device* device, ComputePipelineDescriptor* descriptor)
    {
        RequireDescriptor(descriptor);
        var handle = Allocate();
        var writer = new DescriptorWriter();
        writer.Write(handle.Value);
        writer.Write(HandleOf(descriptor->Layout).Value);
        WriteProgrammableStage(writer, descriptor->Compute);
        _commands.WriteRaw(BrowserGpuOpcode.CreateComputePipeline, writer.WrittenSpan);
        return Pointer<ComputePipeline>(handle);
    }

    public PipelineLayout* DeviceCreatePipelineLayout(Device* device, PipelineLayoutDescriptor* descriptor)
    {
        RequireDescriptor(descriptor);
        var handle = Allocate();
        var count = CheckedCount(descriptor->BindGroupLayoutCount);
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CreatePipelineLayout, checked(8 + count * 4));
        WriteHandle(payload, 0, handle);
        WriteUInt32(payload, 4, (uint)count);
        for (var index = 0; index < count; index++)
            WriteHandle(payload, 8 + index * 4, HandleOf(descriptor->BindGroupLayouts[index]));
        _commands.CompleteCommand();
        return Pointer<PipelineLayout>(handle);
    }

    public RenderPipeline* DeviceCreateRenderPipeline(Device* device, RenderPipelineDescriptor* descriptor)
    {
        RequireDescriptor(descriptor);
        var handle = Allocate();
        var writer = new DescriptorWriter();
        writer.Write(handle.Value);
        writer.Write(0x4C4C5546u); // "FULL" distinguishes this descriptor from the progress-harness pipeline.
        writer.Write(HandleOf(descriptor->Layout).Value);
        WriteVertexState(writer, descriptor->Vertex);
        writer.Write((uint)descriptor->Primitive.Topology);
        writer.Write((uint)descriptor->Primitive.StripIndexFormat);
        writer.Write((uint)descriptor->Primitive.FrontFace);
        writer.Write((uint)descriptor->Primitive.CullMode);
        writer.Write(descriptor->DepthStencil != null ? 1u : 0u);
        if (descriptor->DepthStencil != null) WriteDepthStencil(writer, *descriptor->DepthStencil);
        writer.Write(descriptor->Multisample.Count);
        writer.Write(descriptor->Multisample.Mask);
        writer.Write(descriptor->Multisample.AlphaToCoverageEnabled.Value);
        writer.Write(descriptor->Fragment != null ? 1u : 0u);
        if (descriptor->Fragment != null) WriteFragmentState(writer, *descriptor->Fragment);
        _commands.WriteRaw(BrowserGpuOpcode.CreateRenderPipeline, writer.WrittenSpan);
        return Pointer<RenderPipeline>(handle);
    }

    public Sampler* DeviceCreateSampler(Device* device, SamplerDescriptor* descriptor)
    {
        RequireDescriptor(descriptor);
        var handle = Allocate();
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CreateSampler, 44);
        WriteHandle(payload, 0, handle);
        WriteUInt32(payload, 4, (uint)descriptor->AddressModeU);
        WriteUInt32(payload, 8, (uint)descriptor->AddressModeV);
        WriteUInt32(payload, 12, (uint)descriptor->AddressModeW);
        WriteUInt32(payload, 16, (uint)descriptor->MagFilter);
        WriteUInt32(payload, 20, (uint)descriptor->MinFilter);
        WriteUInt32(payload, 24, (uint)descriptor->MipmapFilter);
        WriteSingle(payload, 28, descriptor->LodMinClamp);
        WriteSingle(payload, 32, descriptor->LodMaxClamp);
        WriteUInt32(payload, 36, (uint)descriptor->Compare);
        WriteUInt32(payload, 40, descriptor->MaxAnisotropy);
        _commands.CompleteCommand();
        return Pointer<Sampler>(handle);
    }

    public ShaderModule* DeviceCreateShaderModule(Device* device, ShaderModuleDescriptor* descriptor)
    {
        RequireDescriptor(descriptor);
        if (descriptor->NextInChain == null || descriptor->NextInChain->SType != SType.ShaderModuleWgslDescriptor)
            throw new NotSupportedException("The browser backend accepts WGSL shader modules directly; SPIR-V modules are unsupported by navigator.gpu.");
        var wgsl = (ShaderModuleWGSLDescriptor*)descriptor->NextInChain;
        var source = ReadUtf8(wgsl->Code);
        var handle = Allocate();
        _commands.CreateShaderModule(handle, source);
        return Pointer<ShaderModule>(handle);
    }

    public Texture* DeviceCreateTexture(Device* device, TextureDescriptor* descriptor)
    {
        RequireDescriptor(descriptor);
        var handle = Allocate();
        var viewFormatCount = CheckedCount(descriptor->ViewFormatCount);
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CreateTexture, checked(44 + viewFormatCount * 4));
        WriteHandle(payload, 0, handle);
        WriteUInt32(payload, 4, (uint)descriptor->Usage);
        WriteUInt32(payload, 8, (uint)descriptor->Dimension);
        WriteUInt32(payload, 12, descriptor->Size.Width);
        WriteUInt32(payload, 16, descriptor->Size.Height);
        WriteUInt32(payload, 20, descriptor->Size.DepthOrArrayLayers);
        WriteUInt32(payload, 24, (uint)descriptor->Format);
        WriteUInt32(payload, 28, descriptor->MipLevelCount);
        WriteUInt32(payload, 32, descriptor->SampleCount);
        WriteUInt32(payload, 36, (uint)viewFormatCount);
        WriteUInt32(payload, 40, 0);
        for (var index = 0; index < viewFormatCount; index++)
            WriteUInt32(payload, 44 + index * 4, (uint)descriptor->ViewFormats[index]);
        _commands.CompleteCommand();
        return Pointer<Texture>(handle);
    }

    public TextureView* TextureCreateView(Texture* texture, TextureViewDescriptor* descriptor)
    {
        var handle = Allocate();
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CreateTextureView, 36);
        WriteHandle(payload, 0, handle);
        WriteHandle(payload, 4, HandleOf(texture));
        if (descriptor != null)
        {
            WriteUInt32(payload, 8, (uint)descriptor->Format);
            WriteUInt32(payload, 12, (uint)descriptor->Dimension);
            WriteUInt32(payload, 16, descriptor->BaseMipLevel);
            WriteUInt32(payload, 20, descriptor->MipLevelCount);
            WriteUInt32(payload, 24, descriptor->BaseArrayLayer);
            WriteUInt32(payload, 28, descriptor->ArrayLayerCount);
            WriteUInt32(payload, 32, (uint)descriptor->Aspect);
        }
        else
        {
            WriteUInt32(payload, 20, uint.MaxValue);
            WriteUInt32(payload, 28, uint.MaxValue);
            WriteUInt32(payload, 32, (uint)TextureAspect.All);
        }
        _commands.CompleteCommand();
        return Pointer<TextureView>(handle);
    }

    public BindGroupLayout* ComputePipelineGetBindGroupLayout(ComputePipeline* computePipeline, uint groupIndex) =>
        GetBindGroupLayout(HandleOf(computePipeline), groupIndex, 1);

    public BindGroupLayout* RenderPipelineGetBindGroupLayout(RenderPipeline* renderPipeline, uint groupIndex) =>
        GetBindGroupLayout(HandleOf(renderPipeline), groupIndex, 2);

    private BindGroupLayout* GetBindGroupLayout(BrowserGpuHandle pipeline, uint groupIndex, uint pipelineKind)
    {
        var handle = Allocate();
        var payload = _commands.BeginCommand(BrowserGpuOpcode.GetBindGroupLayout, 16);
        WriteHandle(payload, 0, handle);
        WriteHandle(payload, 4, pipeline);
        WriteUInt32(payload, 8, groupIndex);
        WriteUInt32(payload, 12, pipelineKind);
        _commands.CompleteCommand();
        return Pointer<BindGroupLayout>(handle);
    }

    public ComputePassEncoder* CommandEncoderBeginComputePass(CommandEncoder* commandEncoder, ComputePassDescriptor* descriptor)
    {
        var handle = Allocate();
        var payload = _commands.BeginCommand(BrowserGpuOpcode.BeginComputePass, 8);
        WriteHandle(payload, 0, handle);
        WriteHandle(payload, 4, HandleOf(commandEncoder));
        _commands.CompleteCommand();
        return Pointer<ComputePassEncoder>(handle);
    }

    public RenderPassEncoder* CommandEncoderBeginRenderPass(CommandEncoder* commandEncoder, RenderPassDescriptor* descriptor)
    {
        RequireDescriptor(descriptor);
        var handle = Allocate();
        var colorCount = CheckedCount(descriptor->ColorAttachmentCount);
        var hasDepthStencil = descriptor->DepthStencilAttachment != null;
        const int colorSize = 56;
        var payload = _commands.BeginCommand(BrowserGpuOpcode.BeginRenderPass, checked(16 + colorCount * colorSize + (hasDepthStencil ? 36 : 0)));
        WriteHandle(payload, 0, handle);
        WriteHandle(payload, 4, HandleOf(commandEncoder));
        WriteUInt32(payload, 8, (uint)colorCount);
        WriteUInt32(payload, 12, hasDepthStencil ? 1u : 0u);
        for (var index = 0; index < colorCount; index++)
        {
            var attachment = descriptor->ColorAttachments[index];
            var offset = 16 + index * colorSize;
            WriteHandle(payload, offset, HandleOf(attachment.View));
            WriteHandle(payload, offset + 4, HandleOf(attachment.ResolveTarget));
            WriteUInt32(payload, offset + 8, attachment.DepthSlice);
            WriteUInt32(payload, offset + 12, (uint)attachment.LoadOp);
            WriteUInt32(payload, offset + 16, (uint)attachment.StoreOp);
            WriteUInt32(payload, offset + 20, 0);
            WriteDouble(payload, offset + 24, attachment.ClearValue.R);
            WriteDouble(payload, offset + 32, attachment.ClearValue.G);
            WriteDouble(payload, offset + 40, attachment.ClearValue.B);
            WriteDouble(payload, offset + 48, attachment.ClearValue.A);
        }
        if (hasDepthStencil)
        {
            var attachment = *descriptor->DepthStencilAttachment;
            var offset = 16 + colorCount * colorSize;
            WriteHandle(payload, offset, HandleOf(attachment.View));
            WriteUInt32(payload, offset + 4, (uint)attachment.DepthLoadOp);
            WriteUInt32(payload, offset + 8, (uint)attachment.DepthStoreOp);
            WriteSingle(payload, offset + 12, attachment.DepthClearValue);
            WriteUInt32(payload, offset + 16, attachment.DepthReadOnly.Value);
            WriteUInt32(payload, offset + 20, (uint)attachment.StencilLoadOp);
            WriteUInt32(payload, offset + 24, (uint)attachment.StencilStoreOp);
            WriteUInt32(payload, offset + 28, attachment.StencilClearValue);
            WriteUInt32(payload, offset + 32, attachment.StencilReadOnly.Value);
        }
        _commands.CompleteCommand();
        return Pointer<RenderPassEncoder>(handle);
    }

    public void CommandEncoderCopyBufferToBuffer(CommandEncoder* commandEncoder, WgpuBuffer* source, ulong sourceOffset, WgpuBuffer* destination, ulong destinationOffset, ulong size)
    {
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CopyBufferToBuffer, 40);
        WriteHandle(payload, 0, HandleOf(commandEncoder));
        WriteHandle(payload, 4, HandleOf(source));
        WriteUInt64(payload, 8, sourceOffset);
        WriteHandle(payload, 16, HandleOf(destination));
        WriteUInt32(payload, 20, 0);
        WriteUInt64(payload, 24, destinationOffset);
        WriteUInt64(payload, 32, size);
        _commands.CompleteCommand();
    }

    public void CommandEncoderCopyTextureToBuffer(CommandEncoder* commandEncoder, ImageCopyTexture* source, ImageCopyBuffer* destination, Extent3D* copySize)
    {
        RequireDescriptor(source);
        RequireDescriptor(destination);
        RequireDescriptor(copySize);
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CopyTextureToBuffer, 64);
        WriteHandle(payload, 0, HandleOf(commandEncoder));
        WriteImageCopyTexture(payload, 4, *source);
        WriteImageCopyBuffer(payload, 28, *destination);
        WriteExtent(payload, 52, *copySize);
        _commands.CompleteCommand();
    }

    public void CommandEncoderCopyTextureToTexture(CommandEncoder* commandEncoder, ImageCopyTexture* source, ImageCopyTexture* destination, Extent3D* copySize)
    {
        RequireDescriptor(source);
        RequireDescriptor(destination);
        RequireDescriptor(copySize);
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CopyTextureToTexture, 64);
        WriteHandle(payload, 0, HandleOf(commandEncoder));
        WriteImageCopyTexture(payload, 4, *source);
        WriteImageCopyTexture(payload, 28, *destination);
        WriteExtent(payload, 52, *copySize);
        _commands.CompleteCommand();
    }

    public CommandBuffer* CommandEncoderFinish(CommandEncoder* commandEncoder, CommandBufferDescriptor* descriptor)
    {
        var handle = Allocate();
        var payload = _commands.BeginCommand(BrowserGpuOpcode.FinishCommandEncoder, 8);
        WriteHandle(payload, 0, handle);
        WriteHandle(payload, 4, HandleOf(commandEncoder));
        _commands.CompleteCommand();
        return Pointer<CommandBuffer>(handle);
    }

    public void ComputePassEncoderSetPipeline(ComputePassEncoder* pass, ComputePipeline* pipeline) => WriteTwoHandles(BrowserGpuOpcode.SetComputePipeline, HandleOf(pass), HandleOf(pipeline));
    public void ComputePassEncoderSetBindGroup(ComputePassEncoder* pass, uint groupIndex, BindGroup* group, nuint dynamicOffsetCount, uint* dynamicOffsets) => WriteBindGroup(HandleOf(pass), groupIndex, HandleOf(group), dynamicOffsetCount, dynamicOffsets);
    public void ComputePassEncoderDispatchWorkgroups(ComputePassEncoder* pass, uint x, uint y, uint z)
    {
        var payload = _commands.BeginCommand(BrowserGpuOpcode.DispatchWorkgroups, 16);
        WriteHandle(payload, 0, HandleOf(pass));
        WriteUInt32(payload, 4, x);
        WriteUInt32(payload, 8, y);
        WriteUInt32(payload, 12, z);
        _commands.CompleteCommand();
    }
    public void ComputePassEncoderEnd(ComputePassEncoder* pass) => WriteOneHandle(BrowserGpuOpcode.EndComputePass, HandleOf(pass));
    public void RenderPassEncoderSetPipeline(RenderPassEncoder* pass, RenderPipeline* pipeline) => WriteTwoHandles(BrowserGpuOpcode.SetRenderPipeline, HandleOf(pass), HandleOf(pipeline));
    public void RenderPassEncoderSetBindGroup(RenderPassEncoder* pass, uint groupIndex, BindGroup* group, nuint dynamicOffsetCount, uint* dynamicOffsets) => WriteBindGroup(HandleOf(pass), groupIndex, HandleOf(group), dynamicOffsetCount, dynamicOffsets);
    public void RenderPassEncoderSetVertexBuffer(RenderPassEncoder* pass, uint slot, WgpuBuffer* buffer, ulong offset, ulong size)
    {
        var payload = _commands.BeginCommand(BrowserGpuOpcode.SetVertexBuffer, 32);
        WriteHandle(payload, 0, HandleOf(pass));
        WriteUInt32(payload, 4, slot);
        WriteHandle(payload, 8, HandleOf(buffer));
        WriteUInt32(payload, 12, 0);
        WriteUInt64(payload, 16, offset);
        WriteUInt64(payload, 24, size);
        _commands.CompleteCommand();
    }
    public void RenderPassEncoderSetIndexBuffer(RenderPassEncoder* pass, WgpuBuffer* buffer, IndexFormat format, ulong offset, ulong size)
    {
        var payload = _commands.BeginCommand(BrowserGpuOpcode.SetIndexBuffer, 32);
        WriteHandle(payload, 0, HandleOf(pass));
        WriteHandle(payload, 4, HandleOf(buffer));
        WriteUInt32(payload, 8, (uint)format);
        WriteUInt32(payload, 12, 0);
        WriteUInt64(payload, 16, offset);
        WriteUInt64(payload, 24, size);
        _commands.CompleteCommand();
    }
    public void RenderPassEncoderSetScissorRect(RenderPassEncoder* pass, uint x, uint y, uint width, uint height)
    {
        var payload = _commands.BeginCommand(BrowserGpuOpcode.SetScissorRect, 20);
        WriteHandle(payload, 0, HandleOf(pass));
        WriteUInt32(payload, 4, x); WriteUInt32(payload, 8, y); WriteUInt32(payload, 12, width); WriteUInt32(payload, 16, height);
        _commands.CompleteCommand();
    }
    public void RenderPassEncoderSetStencilReference(RenderPassEncoder* pass, uint reference)
    {
        var payload = _commands.BeginCommand(BrowserGpuOpcode.SetStencilReference, 8);
        WriteHandle(payload, 0, HandleOf(pass)); WriteUInt32(payload, 4, reference); _commands.CompleteCommand();
    }
    public void RenderPassEncoderSetViewport(RenderPassEncoder* pass, float x, float y, float width, float height, float minDepth, float maxDepth)
    {
        var payload = _commands.BeginCommand(BrowserGpuOpcode.SetViewport, 28);
        WriteHandle(payload, 0, HandleOf(pass));
        WriteSingle(payload, 4, x); WriteSingle(payload, 8, y); WriteSingle(payload, 12, width); WriteSingle(payload, 16, height); WriteSingle(payload, 20, minDepth); WriteSingle(payload, 24, maxDepth);
        _commands.CompleteCommand();
    }
    public void RenderPassEncoderDraw(RenderPassEncoder* pass, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        var payload = _commands.BeginCommand(BrowserGpuOpcode.Draw, 20);
        WriteHandle(payload, 0, HandleOf(pass)); WriteUInt32(payload, 4, vertexCount); WriteUInt32(payload, 8, instanceCount); WriteUInt32(payload, 12, firstVertex); WriteUInt32(payload, 16, firstInstance);
        _commands.CompleteCommand();
    }
    public void RenderPassEncoderDrawIndexed(RenderPassEncoder* pass, uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance)
    {
        var payload = _commands.BeginCommand(BrowserGpuOpcode.DrawIndexed, 24);
        WriteHandle(payload, 0, HandleOf(pass)); WriteUInt32(payload, 4, indexCount); WriteUInt32(payload, 8, instanceCount); WriteUInt32(payload, 12, firstIndex); WriteUInt32(payload, 16, unchecked((uint)baseVertex)); WriteUInt32(payload, 20, firstInstance);
        _commands.CompleteCommand();
    }
    public void RenderPassEncoderEnd(RenderPassEncoder* pass) => WriteOneHandle(BrowserGpuOpcode.EndRenderPass, HandleOf(pass));

    public void QueueWriteBuffer(Queue* queue, WgpuBuffer* buffer, ulong bufferOffset, void* data, nuint size)
    {
        if (size > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(size));
        if (data == null && size != 0) throw new ArgumentNullException(nameof(data));
        var length = (int)size;
        var payload = _commands.BeginCommand(BrowserGpuOpcode.QueueWriteBuffer, checked(24 + length));
        WriteHandle(payload, 0, HandleOf(buffer));
        WriteUInt32(payload, 4, 0);
        WriteUInt64(payload, 8, bufferOffset);
        WriteUInt32(payload, 16, (uint)length);
        WriteUInt32(payload, 20, 0);
        if (length != 0) new ReadOnlySpan<byte>(data, length).CopyTo(payload[24..]);
        _commands.CompleteCommand();
    }

    public void QueueWriteTexture(Queue* queue, ImageCopyTexture* destination, void* data, nuint dataSize, TextureDataLayout* dataLayout, Extent3D* writeSize)
    {
        RequireDescriptor(destination); RequireDescriptor(dataLayout); RequireDescriptor(writeSize);
        if (dataSize > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(dataSize));
        if (data == null && dataSize != 0) throw new ArgumentNullException(nameof(data));
        var length = (int)dataSize;
        var payload = _commands.BeginCommand(BrowserGpuOpcode.QueueWriteTexture, checked(56 + length));
        WriteImageCopyTexture(payload, 0, *destination);
        WriteUInt64(payload, 24, dataLayout->Offset);
        WriteUInt32(payload, 32, dataLayout->BytesPerRow);
        WriteUInt32(payload, 36, dataLayout->RowsPerImage);
        WriteExtent(payload, 40, *writeSize);
        WriteUInt32(payload, 52, (uint)length);
        if (length != 0) new ReadOnlySpan<byte>(data, length).CopyTo(payload[56..]);
        _commands.CompleteCommand();
    }

    public void QueueSubmit(Queue* queue, nuint commandCount, CommandBuffer** commands)
    {
        var count = CheckedCount(commandCount);
        var payload = _commands.BeginCommand(BrowserGpuOpcode.Submit, checked(4 + count * 4));
        WriteUInt32(payload, 0, (uint)count);
        for (var index = 0; index < count; index++) WriteHandle(payload, 4 + index * 4, HandleOf(commands[index]));
        _commands.CompleteCommand();
        Flush();
    }
    public void BufferMapAsync(WgpuBuffer* buffer, MapMode mode, nuint offset, nuint size, PfnBufferMapCallback callback, void* userData)
    {
        if (callback.Handle == null) throw new ArgumentNullException(nameof(callback));
        if (size > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(size));
        var handle = HandleOf(buffer);
        Flush();
        var task = MapBufferCoreAsync(handle.Value, checked((int)mode), checked((double)offset), checked((int)size));
        task.GetAwaiter().OnCompleted(() => CompleteMap(task, handle, mode, offset, size, (nint)callback.Handle, (nint)userData));
    }

    public Task<BufferMapAsyncStatus> BufferMapAsyncTask(WgpuBuffer* buffer, MapMode mode, nuint offset, nuint size)
    {
        if (size > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(size));
        var handle = HandleOf(buffer);
        Flush();
        return MapBufferTaskCoreAsync(handle, mode, offset, size);
    }

    private sealed class MapTaskState
    {
        public required BrowserWebGpuApi Owner { get; init; }
        public required BrowserGpuHandle Handle { get; init; }
        public required MapMode Mode { get; init; }
        public required nuint Offset { get; init; }
        public required nuint Size { get; init; }
    }

    private Task<BufferMapAsyncStatus> MapBufferTaskCoreAsync(BrowserGpuHandle handle, MapMode mode, nuint offset, nuint size)
    {
        var state = new MapTaskState
        {
            Owner = this,
            Handle = handle,
            Mode = mode,
            Offset = offset,
            Size = size
        };
        return MapBufferCoreAsync(
                handle.Value,
                checked((int)mode),
                checked((double)offset),
                checked((int)size))
            .ContinueWith(
                static (task, boxedState) => ((MapTaskState)boxedState!).Owner.CompleteMapTask(task, (MapTaskState)boxedState!),
                state,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private BufferMapAsyncStatus CompleteMapTask(Task<bool> task, MapTaskState state)
    {
        try
        {
            _ = task.GetAwaiter().GetResult();
            var bytes = new byte[checked((int)state.Size)];
            if (_mappedBuffers.Remove(state.Handle.Value, out var previous)) previous.Dispose();
            var mapped = new MappedBuffer(bytes, state.Offset, state.Mode);
            _mappedBuffers.Add(state.Handle.Value, mapped);
            CopyMappedBufferCore(state.Handle.Value, mapped.Pointer, bytes.Length);
            return BufferMapAsyncStatus.Success;
        }
        catch (JSException)
        {
            return BufferMapAsyncStatus.ValidationError;
        }
        catch
        {
            return BufferMapAsyncStatus.Unknown;
        }
    }

    private void CompleteMap(Task<bool> task, BrowserGpuHandle handle, MapMode mode, nuint offset, nuint size, nint callback, nint userData)
    {
        var status = BufferMapAsyncStatus.Unknown;
        try
        {
            _ = task.GetAwaiter().GetResult();
            var bytes = new byte[checked((int)size)];
            if (_mappedBuffers.Remove(handle.Value, out var previous)) previous.Dispose();
            var mapped = new MappedBuffer(bytes, offset, mode);
            _mappedBuffers.Add(handle.Value, mapped);
            CopyMappedBufferCore(handle.Value, mapped.Pointer, bytes.Length);
            status = BufferMapAsyncStatus.Success;
        }
        catch (JSException)
        {
            status = BufferMapAsyncStatus.ValidationError;
        }
        catch
        {
            status = BufferMapAsyncStatus.Unknown;
        }
        InvokeMapCallback(callback, status, userData);
    }

    public void* BufferGetConstMappedRange(WgpuBuffer* buffer, nuint offset, nuint size)
    {
        var handle = HandleOf(buffer);
        if (!_mappedBuffers.TryGetValue(handle.Value, out var mapped)) return null;
        if (offset < mapped.Offset || size > (nuint)mapped.Data.Length || offset - mapped.Offset > (nuint)mapped.Data.Length - size)
            return null;
        return (byte*)mapped.Pointer + checked((int)(offset - mapped.Offset));
    }

    public void BufferUnmap(WgpuBuffer* buffer)
    {
        var handle = HandleOf(buffer);
        if (_mappedBuffers.Remove(handle.Value, out var mapped))
        {
            if ((mapped.Mode & MapMode.Write) != 0)
                WriteMappedBufferCore(handle.Value, mapped.Pointer, mapped.Data.Length);
            mapped.Dispose();
        }
        ReleaseMappedBufferCore(handle.Value);
        WriteOneHandle(BrowserGpuOpcode.BufferUnmap, handle);
    }

    private static void InvokeMapCallback(nint callback, BufferMapAsyncStatus status, nint userData)
    {
        ((delegate* unmanaged[Cdecl]<BufferMapAsyncStatus, void*, void>)callback)(status, (void*)userData);
    }

    public void BufferDestroy(WgpuBuffer* buffer) => Destroy(BrowserGpuOpcode.DestroyBuffer, HandleOf(buffer));
    public void SurfaceGetCurrentTexture(Surface* surface, SurfaceTexture* surfaceTexture)
    {
        RequireDescriptor(surfaceTexture);
        var handle = Allocate();
        var payload = _commands.BeginCommand(BrowserGpuOpcode.CreateSurfaceTexture, 4);
        WriteHandle(payload, 0, handle);
        _commands.CompleteCommand();
        surfaceTexture->Texture = Pointer<Texture>(handle);
        surfaceTexture->Suboptimal = false;
        surfaceTexture->Status = SurfaceGetCurrentTextureStatus.Success;
    }
    // Browser canvas presentation is implicit in queue submission. Deferred releases join the
    // next frame packet so steady rendering remains one managed-to-JavaScript dispatch per frame.
    public void SurfacePresent(Surface* surface) { }
    public void SurfaceRelease(Surface* surface) { }
    public void BindGroupRelease(BindGroup* value) => Release(value);
    public void BindGroupLayoutRelease(BindGroupLayout* value) => Release(value);
    public void BufferRelease(WgpuBuffer* value) => Release(value);
    public void CommandBufferRelease(CommandBuffer* value) => Release(value);
    public void CommandEncoderRelease(CommandEncoder* value) => Release(value);
    public void ComputePassEncoderRelease(ComputePassEncoder* value) => Release(value);
    public void ComputePipelineRelease(ComputePipeline* value) => Release(value);
    public void PipelineLayoutRelease(PipelineLayout* value) => Release(value);
    public void RenderPassEncoderRelease(RenderPassEncoder* value) => Release(value);
    public void RenderPipelineRelease(RenderPipeline* value) => Release(value);
    public void SamplerRelease(Sampler* value) => Release(value);
    public void ShaderModuleRelease(ShaderModule* value) => Release(value);
    public void TextureDestroy(Texture* value) => Destroy(BrowserGpuOpcode.DestroyTexture, HandleOf(value));
    public void TextureRelease(Texture* value) => Release(value);
    public void TextureViewRelease(TextureView* value) => Release(value);

    private void Destroy(BrowserGpuOpcode opcode, BrowserGpuHandle handle)
    {
        if (handle.IsNull) return;
        var payload = _commands.BeginCommand(opcode, 4);
        WriteHandle(payload, 0, handle);
        _commands.CompleteCommand();
    }

    private void WriteBindGroup(BrowserGpuHandle pass, uint groupIndex, BrowserGpuHandle group, nuint dynamicOffsetCount, uint* dynamicOffsets)
    {
        var count = CheckedCount(dynamicOffsetCount);
        if (count != 0 && dynamicOffsets == null) throw new ArgumentNullException(nameof(dynamicOffsets));
        var payload = _commands.BeginCommand(BrowserGpuOpcode.SetBindGroup, checked(16 + count * 4));
        WriteHandle(payload, 0, pass); WriteUInt32(payload, 4, groupIndex); WriteHandle(payload, 8, group); WriteUInt32(payload, 12, (uint)count);
        for (var index = 0; index < count; index++) WriteUInt32(payload, 16 + index * 4, dynamicOffsets[index]);
        _commands.CompleteCommand();
    }

    private static void WriteProgrammableStage(DescriptorWriter writer, ProgrammableStageDescriptor stage)
    {
        writer.Write(HandleOf(stage.Module).Value);
        writer.Write(ReadUtf8(stage.EntryPoint));
        WriteConstants(writer, stage.ConstantCount, stage.Constants);
    }

    private static void WriteVertexState(DescriptorWriter writer, VertexState state)
    {
        writer.Write(HandleOf(state.Module).Value);
        writer.Write(ReadUtf8(state.EntryPoint));
        WriteConstants(writer, state.ConstantCount, state.Constants);
        var bufferCount = CheckedCount(state.BufferCount);
        writer.Write((uint)bufferCount);
        for (var bufferIndex = 0; bufferIndex < bufferCount; bufferIndex++)
        {
            var buffer = state.Buffers[bufferIndex];
            writer.Write(buffer.ArrayStride);
            writer.Write((uint)buffer.StepMode);
            var attributeCount = CheckedCount(buffer.AttributeCount);
            writer.Write((uint)attributeCount);
            for (var attributeIndex = 0; attributeIndex < attributeCount; attributeIndex++)
            {
                var attribute = buffer.Attributes[attributeIndex];
                writer.Write((uint)attribute.Format);
                writer.Write(attribute.Offset);
                writer.Write(attribute.ShaderLocation);
            }
        }
    }

    private static void WriteFragmentState(DescriptorWriter writer, FragmentState state)
    {
        writer.Write(HandleOf(state.Module).Value);
        writer.Write(ReadUtf8(state.EntryPoint));
        WriteConstants(writer, state.ConstantCount, state.Constants);
        var targetCount = CheckedCount(state.TargetCount);
        writer.Write((uint)targetCount);
        for (var index = 0; index < targetCount; index++)
        {
            var target = state.Targets[index];
            writer.Write((uint)target.Format);
            writer.Write(target.Blend != null ? 1u : 0u);
            if (target.Blend != null)
            {
                WriteBlendComponent(writer, target.Blend->Color);
                WriteBlendComponent(writer, target.Blend->Alpha);
            }
            writer.Write((uint)target.WriteMask);
        }
    }

    private static void WriteConstants(DescriptorWriter writer, nuint constantCount, ConstantEntry* constants)
    {
        var count = CheckedCount(constantCount);
        if (count != 0 && constants == null) throw new ArgumentNullException(nameof(constants));
        writer.Write((uint)count);
        for (var index = 0; index < count; index++)
        {
            writer.Write(ReadUtf8(constants[index].Key));
            writer.Write(constants[index].Value);
        }
    }

    private static void WriteDepthStencil(DescriptorWriter writer, DepthStencilState state)
    {
        writer.Write((uint)state.Format);
        writer.Write(state.DepthWriteEnabled.Value);
        writer.Write((uint)state.DepthCompare);
        WriteStencilFace(writer, state.StencilFront);
        WriteStencilFace(writer, state.StencilBack);
        writer.Write(state.StencilReadMask);
        writer.Write(state.StencilWriteMask);
        writer.Write(state.DepthBias);
        writer.Write(state.DepthBiasSlopeScale);
        writer.Write(state.DepthBiasClamp);
    }

    private static void WriteStencilFace(DescriptorWriter writer, StencilFaceState state)
    {
        writer.Write((uint)state.Compare);
        writer.Write((uint)state.FailOp);
        writer.Write((uint)state.DepthFailOp);
        writer.Write((uint)state.PassOp);
    }

    private static void WriteBlendComponent(DescriptorWriter writer, BlendComponent component)
    {
        writer.Write((uint)component.Operation);
        writer.Write((uint)component.SrcFactor);
        writer.Write((uint)component.DstFactor);
    }

    private void WriteOneHandle(BrowserGpuOpcode opcode, BrowserGpuHandle first)
    {
        var payload = _commands.BeginCommand(opcode, 4); WriteHandle(payload, 0, first); _commands.CompleteCommand();
    }

    private void WriteTwoHandles(BrowserGpuOpcode opcode, BrowserGpuHandle first, BrowserGpuHandle second)
    {
        var payload = _commands.BeginCommand(opcode, 8); WriteHandle(payload, 0, first); WriteHandle(payload, 4, second); _commands.CompleteCommand();
    }

    private static void WriteImageCopyTexture(Span<byte> payload, int offset, ImageCopyTexture copy)
    {
        WriteHandle(payload, offset, HandleOf(copy.Texture));
        WriteUInt32(payload, offset + 4, copy.MipLevel);
        WriteUInt32(payload, offset + 8, copy.Origin.X);
        WriteUInt32(payload, offset + 12, copy.Origin.Y);
        WriteUInt32(payload, offset + 16, copy.Origin.Z);
        WriteUInt32(payload, offset + 20, (uint)copy.Aspect);
    }

    private static void WriteImageCopyBuffer(Span<byte> payload, int offset, ImageCopyBuffer copy)
    {
        WriteHandle(payload, offset, HandleOf(copy.Buffer));
        WriteUInt32(payload, offset + 4, 0);
        WriteUInt64(payload, offset + 8, copy.Layout.Offset);
        WriteUInt32(payload, offset + 16, copy.Layout.BytesPerRow);
        WriteUInt32(payload, offset + 20, copy.Layout.RowsPerImage);
    }

    private static void WriteExtent(Span<byte> payload, int offset, Extent3D extent)
    {
        WriteUInt32(payload, offset, extent.Width); WriteUInt32(payload, offset + 4, extent.Height); WriteUInt32(payload, offset + 8, extent.DepthOrArrayLayers);
    }

    private void Release(void* pointer)
    {
        if (pointer == null) return;
        var handle = HandleOf(pointer);
        _commands.ReleaseResource(handle);
        _handles.Release(handle);
    }

    private BrowserGpuHandle Allocate() => _handles.Allocate();
    private static T* Pointer<T>(BrowserGpuHandle handle) where T : unmanaged => (T*)(nuint)handle.Value;
    private static BrowserGpuHandle HandleOf(void* pointer) => new(checked((uint)(nuint)pointer));
    private static int CheckedCount(nuint count) => checked((int)count);
    private static string ReadUtf8(byte* pointer) => pointer == null ? string.Empty : Marshal.PtrToStringUTF8((nint)pointer) ?? string.Empty;
    private static void RequireDescriptor<T>(T* descriptor) where T : unmanaged => ArgumentNullException.ThrowIfNull((nint)descriptor);
    private static void WriteHandle(Span<byte> payload, int offset, BrowserGpuHandle handle) => WriteUInt32(payload, offset, handle.Value);
    private static void WriteUInt32(Span<byte> payload, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(payload[offset..], value);
    private static void WriteUInt64(Span<byte> payload, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(payload[offset..], value);
    private static void WriteSingle(Span<byte> payload, int offset, float value) => WriteUInt32(payload, offset, BitConverter.SingleToUInt32Bits(value));
    private static void WriteDouble(Span<byte> payload, int offset, double value) => WriteUInt64(payload, offset, BitConverter.DoubleToUInt64Bits(value));

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        foreach (var mapped in _mappedBuffers.Values) mapped.Dispose();
        _mappedBuffers.Clear();
        _disposed = true;
        _commands.Dispose();
    }

    [JSImport("mapBuffer", "progpu-browser")]
    private static partial Task<bool> MapBufferCoreAsync(double handle, int mode, double offset, int size);

    [JSImport("copyMappedBuffer", "progpu-browser")]
    private static partial void CopyMappedBufferCore(double handle, nint destination, int size);

    [JSImport("writeMappedBuffer", "progpu-browser")]
    private static partial void WriteMappedBufferCore(double handle, nint source, int size);

    [JSImport("releaseMappedBuffer", "progpu-browser")]
    private static partial void ReleaseMappedBufferCore(double handle);

    private sealed class MappedBuffer : IDisposable
    {
        private GCHandle _pin;

        public MappedBuffer(byte[] data, nuint offset, MapMode mode)
        {
            Data = data;
            Offset = offset;
            Mode = mode;
            _pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        }

        public byte[] Data { get; }
        public nuint Offset { get; }
        public MapMode Mode { get; }
        public nint Pointer => _pin.AddrOfPinnedObject();

        public void Dispose()
        {
            if (_pin.IsAllocated) _pin.Free();
        }
    }

    private sealed class DescriptorWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

        public void Write(uint value)
        {
            var span = _buffer.GetSpan(4);
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
            _buffer.Advance(4);
        }

        public void Write(int value) => Write(unchecked((uint)value));

        public void Write(ulong value)
        {
            var span = _buffer.GetSpan(8);
            BinaryPrimitives.WriteUInt64LittleEndian(span, value);
            _buffer.Advance(8);
        }

        public void Write(float value) => Write(BitConverter.SingleToUInt32Bits(value));
        public void Write(double value) => Write(BitConverter.DoubleToUInt64Bits(value));

        public void Write(string value)
        {
            var byteCount = Encoding.UTF8.GetByteCount(value);
            Write((uint)byteCount);
            if (byteCount == 0) return;
            Encoding.UTF8.GetBytes(value, _buffer.GetSpan(byteCount));
            _buffer.Advance(byteCount);
        }
    }
}
