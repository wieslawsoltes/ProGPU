using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using SW = Silk.NET.WebGPU;
using W = WebGpuSharp;
using DawnFfi = WebGpuSharp.FFI;
using SilkBuffer = Silk.NET.WebGPU.Buffer;

namespace ProGPU.Backend.Dawn;

/// <summary>
/// Exact-ABI translation from ProGPU's stable Silk descriptor contract to
/// current Dawn descriptors.
/// </summary>
/// <remarks>
/// Resource operations are O(1). Descriptor creation is O(N) in the number of
/// bindings, attributes, constants, or attachments and uses bounded stack
/// storage. Submission and draw calls forward without managed allocation.
/// </remarks>
public sealed unsafe class DawnWebGpuApi : IWebGpuApi
{
    private const int MaxDescriptorItems = 256;

    private sealed class MapCompletion
    {
        internal TaskCompletionSource<SW.BufferMapAsyncStatus>? Source;
        internal nint Callback;
        internal nint UserData;
        internal GCHandle Handle;
    }

    public SW.BindGroup* DeviceCreateBindGroup(
        SW.Device* device,
        SW.BindGroupDescriptor* descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        int count = DescriptorCount(
            descriptor->EntryCount,
            "bind group entry count");
        DawnFfi.BindGroupEntryFFI* entries =
            stackalloc DawnFfi.BindGroupEntryFFI[count];
        for (int index = 0; index < count; index++)
        {
            SW.BindGroupEntry source = descriptor->Entries[index];
            entries[index] = new DawnFfi.BindGroupEntryFFI
            {
                Binding = source.Binding,
                Buffer = BufferHandle(source.Buffer),
                Offset = source.Offset,
                Size = source.Size,
                Sampler = SamplerHandle(source.Sampler),
                TextureView = TextureViewHandle(source.TextureView)
            };
        }

        var native = new DawnFfi.BindGroupDescriptorFFI
        {
            Label = StringView(descriptor->Label),
            Layout = BindGroupLayoutHandle(descriptor->Layout),
            EntryCount = descriptor->EntryCount,
            Entries = entries
        };
        return Pointer(
            DeviceHandle(device).CreateBindGroup(&native));
    }

    public SW.BindGroupLayout* DeviceCreateBindGroupLayout(
        SW.Device* device,
        SW.BindGroupLayoutDescriptor* descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        int count = DescriptorCount(
            descriptor->EntryCount,
            "bind group layout entry count");
        W.BindGroupLayoutEntry* entries =
            stackalloc W.BindGroupLayoutEntry[count];
        for (int index = 0; index < count; index++)
        {
            SW.BindGroupLayoutEntry source = descriptor->Entries[index];
            entries[index] = new W.BindGroupLayoutEntry
            {
                Binding = source.Binding,
                Visibility =
                    (W.ShaderStage)(ulong)source.Visibility,
                Buffer = new W.BufferBindingLayout
                {
                    Type = BufferBindingType(source.Buffer.Type),
                    HasDynamicOffset = (bool)source.Buffer.HasDynamicOffset,
                    MinBindingSize = source.Buffer.MinBindingSize
                },
                Sampler = new W.SamplerBindingLayout
                {
                    Type = SamplerBindingType(source.Sampler.Type)
                },
                Texture = new W.TextureBindingLayout
                {
                    SampleType =
                        TextureSampleType(source.Texture.SampleType),
                    ViewDimension =
                        TextureViewDimension(
                            source.Texture.ViewDimension),
                    Multisampled = (bool)source.Texture.Multisampled
                },
                StorageTexture = new W.StorageTextureBindingLayout
                {
                    Access =
                        StorageTextureAccess(
                            source.StorageTexture.Access),
                    Format = TextureFormat(
                        source.StorageTexture.Format),
                    ViewDimension =
                        TextureViewDimension(
                            source.StorageTexture.ViewDimension)
                }
            };
        }

        var native = new DawnFfi.BindGroupLayoutDescriptorFFI
        {
            Label = StringView(descriptor->Label),
            EntryCount = descriptor->EntryCount,
            Entries = entries
        };
        return Pointer(
            DeviceHandle(device).CreateBindGroupLayout(&native));
    }

    public SilkBuffer* DeviceCreateBuffer(
        SW.Device* device,
        SW.BufferDescriptor* descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var native = new DawnFfi.BufferDescriptorFFI
        {
            Label = StringView(descriptor->Label),
            Usage = (W.BufferUsage)(ulong)descriptor->Usage,
            Size = descriptor->Size,
            MappedAtCreation = (bool)descriptor->MappedAtCreation
        };
        return Pointer(DeviceHandle(device).CreateBuffer(&native));
    }

    public SW.CommandEncoder* DeviceCreateCommandEncoder(
        SW.Device* device,
        SW.CommandEncoderDescriptor* descriptor)
    {
        var native = new DawnFfi.CommandEncoderDescriptorFFI
        {
            Label = descriptor == null
                ? DawnFfi.StringViewFFI.NullValue
                : StringView(descriptor->Label)
        };
        return Pointer(
            DeviceHandle(device).CreateCommandEncoder(&native));
    }

    public SW.ComputePipeline* DeviceCreateComputePipeline(
        SW.Device* device,
        SW.ComputePipelineDescriptor* descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        SW.ProgrammableStageDescriptor source = descriptor->Compute;
        int constantCount = DescriptorCount(
            source.ConstantCount,
            nameof(source.ConstantCount));
        DawnFfi.ConstantEntryFFI* constants =
            stackalloc DawnFfi.ConstantEntryFFI[constantCount];
        TranslateConstants(
            source.Constants,
            constants,
            constantCount);
        var native = new DawnFfi.ComputePipelineDescriptorFFI
        {
            Label = StringView(descriptor->Label),
            Layout = PipelineLayoutHandle(descriptor->Layout),
            Compute = new DawnFfi.ComputeStateFFI
            {
                Module = ShaderModuleHandle(source.Module),
                EntryPoint = StringView(source.EntryPoint),
                ConstantCount = source.ConstantCount,
                Constants = constants
            }
        };
        return Pointer(
            DeviceHandle(device).CreateComputePipeline(&native));
    }

    public SW.PipelineLayout* DeviceCreatePipelineLayout(
        SW.Device* device,
        SW.PipelineLayoutDescriptor* descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        int count = DescriptorCount(
            descriptor->BindGroupLayoutCount,
            "bind group layout count");
        DawnFfi.BindGroupLayoutHandle* layouts =
            stackalloc DawnFfi.BindGroupLayoutHandle[count];
        for (int index = 0; index < count; index++)
        {
            layouts[index] =
                BindGroupLayoutHandle(
                    descriptor->BindGroupLayouts[index]);
        }

        var native = new DawnFfi.PipelineLayoutDescriptorFFI
        {
            Label = StringView(descriptor->Label),
            BindGroupLayoutCount = descriptor->BindGroupLayoutCount,
            BindGroupLayouts = layouts
        };
        return Pointer(
            DeviceHandle(device).CreatePipelineLayout(&native));
    }

    public SW.RenderPipeline* DeviceCreateRenderPipeline(
        SW.Device* device,
        SW.RenderPipelineDescriptor* descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        SW.VertexState vertex = descriptor->Vertex;
        int vertexConstantCount = DescriptorCount(
            vertex.ConstantCount,
            nameof(vertex.ConstantCount));
        int vertexBufferCount = DescriptorCount(
            vertex.BufferCount,
            nameof(vertex.BufferCount));
        int vertexAttributeCount = 0;
        for (int index = 0; index < vertexBufferCount; index++)
        {
            vertexAttributeCount = checked(
                vertexAttributeCount +
                DescriptorCount(
                    vertex.Buffers[index].AttributeCount,
                    "vertex attribute count"));
        }
        if (vertexAttributeCount > MaxDescriptorItems)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                "Too many vertex attributes.");
        }

        DawnFfi.ConstantEntryFFI* vertexConstants =
            stackalloc DawnFfi.ConstantEntryFFI[vertexConstantCount];
        DawnFfi.VertexBufferLayoutFFI* vertexBuffers =
            stackalloc DawnFfi.VertexBufferLayoutFFI[vertexBufferCount];
        W.VertexAttribute* vertexAttributes =
            stackalloc W.VertexAttribute[vertexAttributeCount];
        TranslateConstants(
            vertex.Constants,
            vertexConstants,
            vertexConstantCount);
        int attributeOffset = 0;
        for (int bufferIndex = 0;
             bufferIndex < vertexBufferCount;
             bufferIndex++)
        {
            SW.VertexBufferLayout source =
                vertex.Buffers[bufferIndex];
            int attributeCount = checked((int)source.AttributeCount);
            W.VertexAttribute* targetAttributes =
                vertexAttributes + attributeOffset;
            for (int attributeIndex = 0;
                 attributeIndex < attributeCount;
                 attributeIndex++)
            {
                SW.VertexAttribute attribute =
                    source.Attributes[attributeIndex];
                targetAttributes[attributeIndex] =
                    new W.VertexAttribute
                    {
                        Format = VertexFormat(attribute.Format),
                        Offset = attribute.Offset,
                        ShaderLocation = attribute.ShaderLocation
                    };
            }

            vertexBuffers[bufferIndex] =
                new DawnFfi.VertexBufferLayoutFFI
                {
                    ArrayStride = source.ArrayStride,
                    StepMode = VertexStepMode(source.StepMode),
                    AttributeCount = source.AttributeCount,
                    Attributes = targetAttributes
                };
            attributeOffset += attributeCount;
        }

        SW.FragmentState fragmentSource =
            descriptor->Fragment == null
                ? default
                : *descriptor->Fragment;
        int fragmentConstantCount =
            descriptor->Fragment == null
                ? 0
                : DescriptorCount(
                    fragmentSource.ConstantCount,
                    "fragment constant count");
        int colorTargetCount =
            descriptor->Fragment == null
                ? 0
                : DescriptorCount(
                    fragmentSource.TargetCount,
                    "color target count");
        DawnFfi.ConstantEntryFFI* fragmentConstants =
            stackalloc DawnFfi.ConstantEntryFFI[fragmentConstantCount];
        DawnFfi.ColorTargetStateFFI* colorTargets =
            stackalloc DawnFfi.ColorTargetStateFFI[colorTargetCount];
        W.BlendState* blendStates =
            stackalloc W.BlendState[colorTargetCount];
        DawnFfi.FragmentStateFFI fragment = default;
        DawnFfi.FragmentStateFFI* fragmentPointer = null;
        if (descriptor->Fragment != null)
        {
            TranslateConstants(
                fragmentSource.Constants,
                fragmentConstants,
                fragmentConstantCount);
            for (int index = 0;
                 index < colorTargetCount;
                 index++)
            {
                SW.ColorTargetState target =
                    fragmentSource.Targets[index];
                W.BlendState* blend = null;
                if (target.Blend != null)
                {
                    blendStates[index] =
                        BlendState(*target.Blend);
                    blend = &blendStates[index];
                }
                colorTargets[index] =
                    new DawnFfi.ColorTargetStateFFI
                    {
                        Format = TextureFormat(target.Format),
                        Blend = blend,
                        WriteMask =
                            (W.ColorWriteMask)
                            (ulong)target.WriteMask
                    };
            }

            fragment = new DawnFfi.FragmentStateFFI
            {
                Module = ShaderModuleHandle(fragmentSource.Module),
                EntryPoint =
                    StringView(fragmentSource.EntryPoint),
                ConstantCount = fragmentSource.ConstantCount,
                Constants = fragmentConstants,
                TargetCount = fragmentSource.TargetCount,
                Targets = colorTargets
            };
            fragmentPointer = &fragment;
        }

        W.DepthStencilState depthStencil = default;
        W.DepthStencilState* depthStencilPointer = null;
        if (descriptor->DepthStencil != null)
        {
            depthStencil =
                DepthStencilState(*descriptor->DepthStencil);
            depthStencilPointer = &depthStencil;
        }

        var native = new DawnFfi.RenderPipelineDescriptorFFI
        {
            Label = StringView(descriptor->Label),
            Layout = PipelineLayoutHandle(descriptor->Layout),
            Vertex = new DawnFfi.VertexStateFFI
            {
                Module = ShaderModuleHandle(vertex.Module),
                EntryPoint = StringView(vertex.EntryPoint),
                ConstantCount = vertex.ConstantCount,
                Constants = vertexConstants,
                BufferCount = vertex.BufferCount,
                Buffers = vertexBuffers
            },
            Primitive = PrimitiveState(descriptor->Primitive),
            DepthStencil = depthStencilPointer,
            Multisample = MultisampleState(
                descriptor->Multisample),
            Fragment = fragmentPointer
        };
        return Pointer(
            DeviceHandle(device).CreateRenderPipeline(&native));
    }

    public SW.Sampler* DeviceCreateSampler(
        SW.Device* device,
        SW.SamplerDescriptor* descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var native = new DawnFfi.SamplerDescriptorFFI
        {
            Label = StringView(descriptor->Label),
            AddressModeU = AddressMode(descriptor->AddressModeU),
            AddressModeV = AddressMode(descriptor->AddressModeV),
            AddressModeW = AddressMode(descriptor->AddressModeW),
            MagFilter = FilterMode(descriptor->MagFilter),
            MinFilter = FilterMode(descriptor->MinFilter),
            MipmapFilter =
                MipmapFilterMode(descriptor->MipmapFilter),
            LodMinClamp = descriptor->LodMinClamp,
            LodMaxClamp = descriptor->LodMaxClamp,
            Compare = CompareFunction(descriptor->Compare),
            MaxAnisotropy = descriptor->MaxAnisotropy
        };
        return Pointer(DeviceHandle(device).CreateSampler(&native));
    }

    public SW.ShaderModule* DeviceCreateShaderModule(
        SW.Device* device,
        SW.ShaderModuleDescriptor* descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor->NextInChain == null ||
            descriptor->NextInChain->SType !=
            SW.SType.ShaderModuleWgslDescriptor)
        {
            throw new NotSupportedException(
                "The Dawn backend currently accepts WGSL shader modules.");
        }

        var source =
            (SW.ShaderModuleWGSLDescriptor*)
            descriptor->NextInChain;
        var wgsl = new DawnFfi.ShaderSourceWGSLFFI
        {
            Chain = new W.ChainedStruct
            {
                SType = W.SType.ShaderSourceWGSL
            },
            Code = StringView(source->Code)
        };
        var native = new DawnFfi.ShaderModuleDescriptorFFI
        {
            NextInChain = &wgsl.Chain,
            Label = StringView(descriptor->Label)
        };
        return Pointer(
            DeviceHandle(device).CreateShaderModule(&native));
    }

    public SW.Texture* DeviceCreateTexture(
        SW.Device* device,
        SW.TextureDescriptor* descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        int viewFormatCount = DescriptorCount(
            descriptor->ViewFormatCount,
            "texture view format count");
        W.TextureFormat* viewFormats =
            stackalloc W.TextureFormat[viewFormatCount];
        for (int index = 0; index < viewFormatCount; index++)
        {
            viewFormats[index] =
                TextureFormat(descriptor->ViewFormats[index]);
        }

        var native = new DawnFfi.TextureDescriptorFFI
        {
            Label = StringView(descriptor->Label),
            Usage = (W.TextureUsage)(ulong)descriptor->Usage,
            Dimension = TextureDimension(descriptor->Dimension),
            Size = Extent(descriptor->Size),
            Format = TextureFormat(descriptor->Format),
            MipLevelCount = descriptor->MipLevelCount,
            SampleCount = descriptor->SampleCount,
            ViewFormatCount = descriptor->ViewFormatCount,
            ViewFormats = viewFormats
        };
        return Pointer(DeviceHandle(device).CreateTexture(&native));
    }

    public SW.TextureView* TextureCreateView(
        SW.Texture* texture,
        SW.TextureViewDescriptor* descriptor)
    {
        if (descriptor == null)
        {
            return Pointer(TextureHandle(texture).CreateView(null));
        }

        var native = new DawnFfi.TextureViewDescriptorFFI
        {
            Label = StringView(descriptor->Label),
            Format = TextureFormat(descriptor->Format),
            Dimension =
                TextureViewDimension(descriptor->Dimension),
            BaseMipLevel = descriptor->BaseMipLevel,
            MipLevelCount = descriptor->MipLevelCount,
            BaseArrayLayer = descriptor->BaseArrayLayer,
            ArrayLayerCount = descriptor->ArrayLayerCount,
            Aspect = TextureAspect(descriptor->Aspect)
        };
        return Pointer(TextureHandle(texture).CreateView(&native));
    }

    public SW.BindGroupLayout* ComputePipelineGetBindGroupLayout(
        SW.ComputePipeline* computePipeline,
        uint groupIndex) =>
        Pointer(
            ComputePipelineHandle(computePipeline)
                .GetBindGroupLayout(groupIndex));

    public SW.BindGroupLayout* RenderPipelineGetBindGroupLayout(
        SW.RenderPipeline* renderPipeline,
        uint groupIndex) =>
        Pointer(
            RenderPipelineHandle(renderPipeline)
                .GetBindGroupLayout(groupIndex));

    public SW.ComputePassEncoder* CommandEncoderBeginComputePass(
        SW.CommandEncoder* commandEncoder,
        SW.ComputePassDescriptor* descriptor)
    {
        var native = new DawnFfi.ComputePassDescriptorFFI
        {
            Label = descriptor == null
                ? DawnFfi.StringViewFFI.NullValue
                : StringView(descriptor->Label)
        };
        return Pointer(
            CommandEncoderHandle(commandEncoder)
                .BeginComputePass(&native));
    }

    public SW.RenderPassEncoder* CommandEncoderBeginRenderPass(
        SW.CommandEncoder* commandEncoder,
        SW.RenderPassDescriptor* descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        int colorCount = DescriptorCount(
            descriptor->ColorAttachmentCount,
            "render pass color attachment count");
        DawnFfi.RenderPassColorAttachmentFFI* colors =
            stackalloc DawnFfi.RenderPassColorAttachmentFFI[colorCount];
        for (int index = 0; index < colorCount; index++)
        {
            SW.RenderPassColorAttachment source =
                descriptor->ColorAttachments[index];
            colors[index] =
                new DawnFfi.RenderPassColorAttachmentFFI
                {
                    View = TextureViewHandle(source.View),
                    ResolveTarget =
                        TextureViewHandle(source.ResolveTarget),
                    LoadOp = LoadOp(source.LoadOp),
                    StoreOp = StoreOp(source.StoreOp),
                    ClearValue = Color(source.ClearValue)
                };
        }

        DawnFfi.RenderPassDepthStencilAttachmentFFI depth = default;
        DawnFfi.RenderPassDepthStencilAttachmentFFI* depthPointer =
            null;
        if (descriptor->DepthStencilAttachment != null)
        {
            SW.RenderPassDepthStencilAttachment source =
                *descriptor->DepthStencilAttachment;
            depth =
                new DawnFfi.RenderPassDepthStencilAttachmentFFI
                {
                    View = TextureViewHandle(source.View),
                    DepthLoadOp = LoadOp(source.DepthLoadOp),
                    DepthStoreOp = StoreOp(source.DepthStoreOp),
                    DepthClearValue = source.DepthClearValue,
                    DepthReadOnly = (bool)source.DepthReadOnly,
                    StencilLoadOp = LoadOp(source.StencilLoadOp),
                    StencilStoreOp = StoreOp(source.StencilStoreOp),
                    StencilClearValue = source.StencilClearValue,
                    StencilReadOnly = (bool)source.StencilReadOnly
                };
            depthPointer = &depth;
        }

        var native = new DawnFfi.RenderPassDescriptorFFI
        {
            Label = StringView(descriptor->Label),
            ColorAttachmentCount =
                descriptor->ColorAttachmentCount,
            ColorAttachments = colors,
            DepthStencilAttachment = depthPointer
        };
        return Pointer(
            CommandEncoderHandle(commandEncoder)
                .BeginRenderPass(&native));
    }

    public void CommandEncoderCopyBufferToBuffer(
        SW.CommandEncoder* commandEncoder,
        SilkBuffer* source,
        ulong sourceOffset,
        SilkBuffer* destination,
        ulong destinationOffset,
        ulong size) =>
        CommandEncoderHandle(commandEncoder).CopyBufferToBuffer(
            BufferHandle(source),
            sourceOffset,
            BufferHandle(destination),
            destinationOffset,
            size);

    public void CommandEncoderCopyBufferToTexture(
        SW.CommandEncoder* commandEncoder,
        SW.ImageCopyBuffer* source,
        SW.ImageCopyTexture* destination,
        SW.Extent3D* copySize)
    {
        DawnFfi.TexelCopyBufferInfoFFI nativeSource =
            TexelCopyBuffer(*source);
        DawnFfi.TexelCopyTextureInfoFFI nativeDestination =
            TexelCopyTexture(*destination);
        W.Extent3D nativeSize = Extent(*copySize);
        CommandEncoderHandle(commandEncoder).CopyBufferToTexture(
            &nativeSource,
            &nativeDestination,
            &nativeSize);
    }

    public void CommandEncoderCopyTextureToBuffer(
        SW.CommandEncoder* commandEncoder,
        SW.ImageCopyTexture* source,
        SW.ImageCopyBuffer* destination,
        SW.Extent3D* copySize)
    {
        DawnFfi.TexelCopyTextureInfoFFI nativeSource =
            TexelCopyTexture(*source);
        DawnFfi.TexelCopyBufferInfoFFI nativeDestination =
            TexelCopyBuffer(*destination);
        W.Extent3D nativeSize = Extent(*copySize);
        CommandEncoderHandle(commandEncoder).CopyTextureToBuffer(
            &nativeSource,
            &nativeDestination,
            &nativeSize);
    }

    public void CommandEncoderCopyTextureToTexture(
        SW.CommandEncoder* commandEncoder,
        SW.ImageCopyTexture* source,
        SW.ImageCopyTexture* destination,
        SW.Extent3D* copySize)
    {
        DawnFfi.TexelCopyTextureInfoFFI nativeSource =
            TexelCopyTexture(*source);
        DawnFfi.TexelCopyTextureInfoFFI nativeDestination =
            TexelCopyTexture(*destination);
        W.Extent3D nativeSize = Extent(*copySize);
        CommandEncoderHandle(commandEncoder).CopyTextureToTexture(
            &nativeSource,
            &nativeDestination,
            &nativeSize);
    }

    public SW.CommandBuffer* CommandEncoderFinish(
        SW.CommandEncoder* commandEncoder,
        SW.CommandBufferDescriptor* descriptor)
    {
        var native = new DawnFfi.CommandBufferDescriptorFFI
        {
            Label = descriptor == null
                ? DawnFfi.StringViewFFI.NullValue
                : StringView(descriptor->Label)
        };
        return Pointer(
            CommandEncoderHandle(commandEncoder).Finish(&native));
    }

    public void ComputePassEncoderSetPipeline(
        SW.ComputePassEncoder* pass,
        SW.ComputePipeline* pipeline) =>
        ComputePassEncoderHandle(pass).SetPipeline(
            ComputePipelineHandle(pipeline));

    public void ComputePassEncoderSetBindGroup(
        SW.ComputePassEncoder* pass,
        uint groupIndex,
        SW.BindGroup* group,
        nuint dynamicOffsetCount,
        uint* dynamicOffsets) =>
        ComputePassEncoderHandle(pass).SetBindGroup(
            groupIndex,
            BindGroupHandle(group),
            dynamicOffsetCount,
            dynamicOffsets);

    public void ComputePassEncoderDispatchWorkgroups(
        SW.ComputePassEncoder* pass,
        uint x,
        uint y,
        uint z) =>
        ComputePassEncoderHandle(pass).DispatchWorkgroups(x, y, z);

    public void ComputePassEncoderEnd(
        SW.ComputePassEncoder* pass) =>
        ComputePassEncoderHandle(pass).End();

    public void RenderPassEncoderSetPipeline(
        SW.RenderPassEncoder* pass,
        SW.RenderPipeline* pipeline) =>
        RenderPassEncoderHandle(pass).SetPipeline(
            RenderPipelineHandle(pipeline));

    public void RenderPassEncoderSetBindGroup(
        SW.RenderPassEncoder* pass,
        uint groupIndex,
        SW.BindGroup* group,
        nuint dynamicOffsetCount,
        uint* dynamicOffsets) =>
        RenderPassEncoderHandle(pass).SetBindGroup(
            groupIndex,
            BindGroupHandle(group),
            dynamicOffsetCount,
            dynamicOffsets);

    public void RenderPassEncoderSetVertexBuffer(
        SW.RenderPassEncoder* pass,
        uint slot,
        SilkBuffer* buffer,
        ulong offset,
        ulong size) =>
        RenderPassEncoderHandle(pass).SetVertexBuffer(
            slot,
            BufferHandle(buffer),
            offset,
            size);

    public void RenderPassEncoderSetIndexBuffer(
        SW.RenderPassEncoder* pass,
        SilkBuffer* buffer,
        SW.IndexFormat format,
        ulong offset,
        ulong size) =>
        RenderPassEncoderHandle(pass).SetIndexBuffer(
            BufferHandle(buffer),
            IndexFormat(format),
            offset,
            size);

    public void RenderPassEncoderSetScissorRect(
        SW.RenderPassEncoder* pass,
        uint x,
        uint y,
        uint width,
        uint height) =>
        RenderPassEncoderHandle(pass).SetScissorRect(
            x,
            y,
            width,
            height);

    public void RenderPassEncoderSetStencilReference(
        SW.RenderPassEncoder* pass,
        uint reference) =>
        RenderPassEncoderHandle(pass).SetStencilReference(reference);

    public void RenderPassEncoderSetViewport(
        SW.RenderPassEncoder* pass,
        float x,
        float y,
        float width,
        float height,
        float minDepth,
        float maxDepth) =>
        RenderPassEncoderHandle(pass).SetViewport(
            x,
            y,
            width,
            height,
            minDepth,
            maxDepth);

    public void RenderPassEncoderDraw(
        SW.RenderPassEncoder* pass,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance) =>
        RenderPassEncoderHandle(pass).Draw(
            vertexCount,
            instanceCount,
            firstVertex,
            firstInstance);

    public void RenderPassEncoderDrawIndexed(
        SW.RenderPassEncoder* pass,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int baseVertex,
        uint firstInstance) =>
        RenderPassEncoderHandle(pass).DrawIndexed(
            indexCount,
            instanceCount,
            firstIndex,
            baseVertex,
            firstInstance);

    public void RenderPassEncoderEnd(
        SW.RenderPassEncoder* pass) =>
        RenderPassEncoderHandle(pass).End();

    public void QueueWriteBuffer(
        SW.Queue* queue,
        SilkBuffer* buffer,
        ulong bufferOffset,
        void* data,
        nuint size) =>
        QueueHandle(queue).WriteBuffer(
            BufferHandle(buffer),
            bufferOffset,
            data,
            size);

    public void QueueWriteTexture(
        SW.Queue* queue,
        SW.ImageCopyTexture* destination,
        void* data,
        nuint dataSize,
        SW.TextureDataLayout* dataLayout,
        SW.Extent3D* writeSize)
    {
        DawnFfi.TexelCopyTextureInfoFFI nativeDestination =
            TexelCopyTexture(*destination);
        W.TexelCopyBufferLayout nativeLayout =
            TexelCopyLayout(*dataLayout);
        W.Extent3D nativeSize = Extent(*writeSize);
        QueueHandle(queue).WriteTexture(
            &nativeDestination,
            data,
            dataSize,
            &nativeLayout,
            &nativeSize);
    }

    public void QueueSubmit(
        SW.Queue* queue,
        nuint commandCount,
        SW.CommandBuffer** commands)
    {
        int count = DescriptorCount(commandCount, nameof(commandCount));
        DawnFfi.CommandBufferHandle* nativeCommands =
            stackalloc DawnFfi.CommandBufferHandle[count];
        for (int index = 0; index < count; index++)
        {
            nativeCommands[index] =
                CommandBufferHandle(commands[index]);
        }
        QueueHandle(queue).Submit(commandCount, nativeCommands);
    }

    public void BufferMapAsync(
        SilkBuffer* buffer,
        SW.MapMode mode,
        nuint offset,
        nuint size,
        SW.PfnBufferMapCallback callback,
        void* userData)
    {
        var completion = new MapCompletion
        {
            Callback = (nint)callback.Handle,
            UserData = (nint)userData
        };
        StartMap(buffer, mode, offset, size, completion);
    }

    public Task<SW.BufferMapAsyncStatus> BufferMapAsyncTask(
        SilkBuffer* buffer,
        SW.MapMode mode,
        nuint offset,
        nuint size)
    {
        var completion = new MapCompletion
        {
            Source =
                new TaskCompletionSource<SW.BufferMapAsyncStatus>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
        };
        StartMap(buffer, mode, offset, size, completion);
        return completion.Source.Task;
    }

    public void* BufferGetMappedRange(
        SilkBuffer* buffer,
        nuint offset,
        nuint size) =>
        BufferHandle(buffer).GetMappedRange(offset, size);

    public void* BufferGetConstMappedRange(
        SilkBuffer* buffer,
        nuint offset,
        nuint size) =>
        BufferHandle(buffer).GetConstMappedRange(offset, size);

    public void BufferUnmap(SilkBuffer* buffer) =>
        BufferHandle(buffer).Unmap();

    public void BufferDestroy(SilkBuffer* buffer) =>
        BufferHandle(buffer).Destroy();

    public void SurfaceGetCurrentTexture(
        SW.Surface* surface,
        SW.SurfaceTexture* surfaceTexture) =>
        throw ExternalSurfaceNotSupported();

    public void SurfacePresent(SW.Surface* surface) =>
        throw ExternalSurfaceNotSupported();

    public void SurfaceRelease(SW.Surface* surface)
    {
        if (surface != null)
        {
            throw ExternalSurfaceNotSupported();
        }
    }

    public void BindGroupRelease(SW.BindGroup* value) =>
        BindGroupHandle(value).Release();

    public void BindGroupLayoutRelease(
        SW.BindGroupLayout* value) =>
        BindGroupLayoutHandle(value).Release();

    public void BufferRelease(SilkBuffer* value) =>
        BufferHandle(value).Release();

    public void CommandBufferRelease(
        SW.CommandBuffer* value) =>
        CommandBufferHandle(value).Release();

    public void CommandEncoderRelease(
        SW.CommandEncoder* value) =>
        CommandEncoderHandle(value).Release();

    public void ComputePassEncoderRelease(
        SW.ComputePassEncoder* value) =>
        ComputePassEncoderHandle(value).Release();

    public void ComputePipelineRelease(
        SW.ComputePipeline* value) =>
        ComputePipelineHandle(value).Release();

    public void PipelineLayoutRelease(
        SW.PipelineLayout* value) =>
        PipelineLayoutHandle(value).Release();

    public void RenderPassEncoderRelease(
        SW.RenderPassEncoder* value) =>
        RenderPassEncoderHandle(value).Release();

    public void RenderPipelineRelease(
        SW.RenderPipeline* value) =>
        RenderPipelineHandle(value).Release();

    public void SamplerRelease(SW.Sampler* value) =>
        SamplerHandle(value).Release();

    public void ShaderModuleRelease(
        SW.ShaderModule* value) =>
        ShaderModuleHandle(value).Release();

    public void TextureDestroy(SW.Texture* value) =>
        TextureHandle(value).Destroy();

    public void TextureRelease(SW.Texture* value) =>
        TextureHandle(value).Release();

    public void TextureViewRelease(SW.TextureView* value) =>
        TextureViewHandle(value).Release();

    private static void StartMap(
        SilkBuffer* buffer,
        SW.MapMode mode,
        nuint offset,
        nuint size,
        MapCompletion completion)
    {
        completion.Handle = GCHandle.Alloc(completion);
        try
        {
            var callbackInfo = new DawnFfi.BufferMapCallbackInfoFFI
            {
                Mode = W.CallbackMode.AllowSpontaneous,
                Callback = &CompleteMap,
                Userdata1 =
                    (void*)GCHandle.ToIntPtr(completion.Handle)
            };
            _ = BufferHandle(buffer).MapAsync(
                (W.MapMode)(ulong)mode,
                offset,
                size,
                callbackInfo);
        }
        catch
        {
            if (completion.Handle.IsAllocated)
            {
                completion.Handle.Free();
            }
            throw;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CompleteMap(
        W.MapAsyncStatus status,
        DawnFfi.StringViewFFI message,
        void* userData1,
        void* userData2)
    {
        GCHandle handle =
            GCHandle.FromIntPtr((nint)userData1);
        if (handle.Target is not MapCompletion completion)
        {
            return;
        }

        SW.BufferMapAsyncStatus translated =
            BufferMapStatus(status);
        try
        {
            completion.Source?.TrySetResult(translated);
            if (completion.Callback != 0)
            {
                var callback =
                    (delegate* unmanaged[Cdecl]<
                        SW.BufferMapAsyncStatus,
                        void*,
                        void>)completion.Callback;
                callback(translated, (void*)completion.UserData);
            }
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    private static void TranslateConstants(
        SW.ConstantEntry* source,
        DawnFfi.ConstantEntryFFI* destination,
        int count)
    {
        for (int index = 0; index < count; index++)
        {
            destination[index] =
                new DawnFfi.ConstantEntryFFI
                {
                    Key = StringView(source[index].Key),
                    Value = source[index].Value
                };
        }
    }

    private static DawnFfi.TexelCopyBufferInfoFFI TexelCopyBuffer(
        SW.ImageCopyBuffer source) =>
        new()
        {
            Buffer = BufferHandle(source.Buffer),
            Layout = TexelCopyLayout(source.Layout)
        };

    private static DawnFfi.TexelCopyTextureInfoFFI TexelCopyTexture(
        SW.ImageCopyTexture source) =>
        new()
        {
            Texture = TextureHandle(source.Texture),
            MipLevel = source.MipLevel,
            Origin = Origin(source.Origin),
            Aspect = TextureAspect(source.Aspect)
        };

    private static W.TexelCopyBufferLayout TexelCopyLayout(
        SW.TextureDataLayout source) =>
        new()
        {
            Offset = source.Offset,
            BytesPerRow = source.BytesPerRow,
            RowsPerImage = source.RowsPerImage
        };

    private static W.Extent3D Extent(SW.Extent3D value) =>
        new(value.Width, value.Height, value.DepthOrArrayLayers);

    private static W.Origin3D Origin(SW.Origin3D value) =>
        new(value.X, value.Y, value.Z);

    private static W.Color Color(SW.Color value) =>
        new(value.R, value.G, value.B, value.A);

    private static W.PrimitiveState PrimitiveState(
        SW.PrimitiveState value) =>
        new()
        {
            Topology = PrimitiveTopology(value.Topology),
            StripIndexFormat = IndexFormat(value.StripIndexFormat),
            FrontFace = FrontFace(value.FrontFace),
            CullMode = CullMode(value.CullMode)
        };

    private static W.MultisampleState MultisampleState(
        SW.MultisampleState value) =>
        new()
        {
            Count = value.Count,
            Mask = value.Mask,
            AlphaToCoverageEnabled =
                (bool)value.AlphaToCoverageEnabled
        };

    private static W.DepthStencilState DepthStencilState(
        SW.DepthStencilState value) =>
        new()
        {
            Format = TextureFormat(value.Format),
            DepthWriteEnabled = (bool)value.DepthWriteEnabled
                ? W.OptionalBool.True
                : W.OptionalBool.False,
            DepthCompare = CompareFunction(value.DepthCompare),
            StencilFront = StencilFaceState(value.StencilFront),
            StencilBack = StencilFaceState(value.StencilBack),
            StencilReadMask = value.StencilReadMask,
            StencilWriteMask = value.StencilWriteMask,
            DepthBias = value.DepthBias,
            DepthBiasSlopeScale = value.DepthBiasSlopeScale,
            DepthBiasClamp = value.DepthBiasClamp
        };

    private static W.StencilFaceState StencilFaceState(
        SW.StencilFaceState value) =>
        new()
        {
            Compare = CompareFunction(value.Compare),
            FailOp = StencilOperation(value.FailOp),
            DepthFailOp = StencilOperation(value.DepthFailOp),
            PassOp = StencilOperation(value.PassOp)
        };

    private static W.BlendState BlendState(
        SW.BlendState value) =>
        new()
        {
            Color = BlendComponent(value.Color),
            Alpha = BlendComponent(value.Alpha)
        };

    private static W.BlendComponent BlendComponent(
        SW.BlendComponent value) =>
        new()
        {
            Operation = BlendOperation(value.Operation),
            SrcFactor = BlendFactor(value.SrcFactor),
            DstFactor = BlendFactor(value.DstFactor)
        };

    private static W.BufferBindingType BufferBindingType(
        SW.BufferBindingType value) => value switch
        {
            SW.BufferBindingType.Undefined =>
                W.BufferBindingType.BindingNotUsed,
            SW.BufferBindingType.Uniform =>
                W.BufferBindingType.Uniform,
            SW.BufferBindingType.Storage =>
                W.BufferBindingType.Storage,
            SW.BufferBindingType.ReadOnlyStorage =>
                W.BufferBindingType.ReadOnlyStorage,
            _ => throw Unsupported(value)
        };

    private static W.SamplerBindingType SamplerBindingType(
        SW.SamplerBindingType value) => value switch
        {
            SW.SamplerBindingType.Undefined =>
                W.SamplerBindingType.BindingNotUsed,
            SW.SamplerBindingType.Filtering =>
                W.SamplerBindingType.Filtering,
            SW.SamplerBindingType.NonFiltering =>
                W.SamplerBindingType.NonFiltering,
            SW.SamplerBindingType.Comparison =>
                W.SamplerBindingType.Comparison,
            _ => throw Unsupported(value)
        };

    private static W.TextureSampleType TextureSampleType(
        SW.TextureSampleType value) => value switch
        {
            SW.TextureSampleType.Undefined =>
                W.TextureSampleType.BindingNotUsed,
            SW.TextureSampleType.Float =>
                W.TextureSampleType.Float,
            SW.TextureSampleType.UnfilterableFloat =>
                W.TextureSampleType.UnfilterableFloat,
            SW.TextureSampleType.Depth =>
                W.TextureSampleType.Depth,
            SW.TextureSampleType.Sint =>
                W.TextureSampleType.Sint,
            SW.TextureSampleType.Uint =>
                W.TextureSampleType.Uint,
            _ => throw Unsupported(value)
        };

    private static W.StorageTextureAccess StorageTextureAccess(
        SW.StorageTextureAccess value) => value switch
        {
            SW.StorageTextureAccess.Undefined =>
                W.StorageTextureAccess.BindingNotUsed,
            SW.StorageTextureAccess.WriteOnly =>
                W.StorageTextureAccess.WriteOnly,
            SW.StorageTextureAccess.ReadOnly =>
                W.StorageTextureAccess.ReadOnly,
            SW.StorageTextureAccess.ReadWrite =>
                W.StorageTextureAccess.ReadWrite,
            _ => throw Unsupported(value)
        };

    private static W.TextureDimension TextureDimension(
        SW.TextureDimension value) => value switch
        {
            SW.TextureDimension.Dimension1D =>
                W.TextureDimension.D1,
            SW.TextureDimension.Dimension2D =>
                W.TextureDimension.D2,
            SW.TextureDimension.Dimension3D =>
                W.TextureDimension.D3,
            _ => throw Unsupported(value)
        };

    private static W.TextureViewDimension TextureViewDimension(
        SW.TextureViewDimension value) => value switch
        {
            SW.TextureViewDimension.DimensionUndefined =>
                W.TextureViewDimension.Undefined,
            SW.TextureViewDimension.Dimension1D =>
                W.TextureViewDimension.D1,
            SW.TextureViewDimension.Dimension2D =>
                W.TextureViewDimension.D2,
            SW.TextureViewDimension.Dimension2DArray =>
                W.TextureViewDimension.D2Array,
            SW.TextureViewDimension.DimensionCube =>
                W.TextureViewDimension.Cube,
            SW.TextureViewDimension.DimensionCubeArray =>
                W.TextureViewDimension.CubeArray,
            SW.TextureViewDimension.Dimension3D =>
                W.TextureViewDimension.D3,
            _ => throw Unsupported(value)
        };

    private static W.TextureFormat TextureFormat(
        SW.TextureFormat value) => value switch
        {
            SW.TextureFormat.Undefined => W.TextureFormat.Undefined,
            SW.TextureFormat.R8Unorm => W.TextureFormat.R8Unorm,
            SW.TextureFormat.R8Snorm => W.TextureFormat.R8Snorm,
            SW.TextureFormat.R8Uint => W.TextureFormat.R8Uint,
            SW.TextureFormat.R8Sint => W.TextureFormat.R8Sint,
            SW.TextureFormat.R16Uint => W.TextureFormat.R16Uint,
            SW.TextureFormat.R16Sint => W.TextureFormat.R16Sint,
            SW.TextureFormat.R16float => W.TextureFormat.R16Float,
            SW.TextureFormat.RG8Unorm => W.TextureFormat.RG8Unorm,
            SW.TextureFormat.RG8Snorm => W.TextureFormat.RG8Snorm,
            SW.TextureFormat.RG8Uint => W.TextureFormat.RG8Uint,
            SW.TextureFormat.RG8Sint => W.TextureFormat.RG8Sint,
            SW.TextureFormat.R32float => W.TextureFormat.R32Float,
            SW.TextureFormat.R32Uint => W.TextureFormat.R32Uint,
            SW.TextureFormat.R32Sint => W.TextureFormat.R32Sint,
            SW.TextureFormat.RG16Uint => W.TextureFormat.RG16Uint,
            SW.TextureFormat.RG16Sint => W.TextureFormat.RG16Sint,
            SW.TextureFormat.RG16float => W.TextureFormat.RG16Float,
            SW.TextureFormat.Rgba8Unorm => W.TextureFormat.RGBA8Unorm,
            SW.TextureFormat.Rgba8UnormSrgb => W.TextureFormat.RGBA8UnormSrgb,
            SW.TextureFormat.Rgba8Snorm => W.TextureFormat.RGBA8Snorm,
            SW.TextureFormat.Rgba8Uint => W.TextureFormat.RGBA8Uint,
            SW.TextureFormat.Rgba8Sint => W.TextureFormat.RGBA8Sint,
            SW.TextureFormat.Bgra8Unorm => W.TextureFormat.BGRA8Unorm,
            SW.TextureFormat.Bgra8UnormSrgb => W.TextureFormat.BGRA8UnormSrgb,
            SW.TextureFormat.Rgb10A2Uint => W.TextureFormat.RGB10A2Uint,
            SW.TextureFormat.Rgb10A2Unorm => W.TextureFormat.RGB10A2Unorm,
            SW.TextureFormat.RG11B10Ufloat => W.TextureFormat.RG11B10Ufloat,
            SW.TextureFormat.Rgb9E5Ufloat => W.TextureFormat.RGB9E5Ufloat,
            SW.TextureFormat.RG32float => W.TextureFormat.RG32Float,
            SW.TextureFormat.RG32Uint => W.TextureFormat.RG32Uint,
            SW.TextureFormat.RG32Sint => W.TextureFormat.RG32Sint,
            SW.TextureFormat.Rgba16Uint => W.TextureFormat.RGBA16Uint,
            SW.TextureFormat.Rgba16Sint => W.TextureFormat.RGBA16Sint,
            SW.TextureFormat.Rgba16float => W.TextureFormat.RGBA16Float,
            SW.TextureFormat.Rgba32float => W.TextureFormat.RGBA32Float,
            SW.TextureFormat.Rgba32Uint => W.TextureFormat.RGBA32Uint,
            SW.TextureFormat.Rgba32Sint => W.TextureFormat.RGBA32Sint,
            SW.TextureFormat.Stencil8 => W.TextureFormat.Stencil8,
            SW.TextureFormat.Depth16Unorm => W.TextureFormat.Depth16Unorm,
            SW.TextureFormat.Depth24Plus => W.TextureFormat.Depth24Plus,
            SW.TextureFormat.Depth24PlusStencil8 => W.TextureFormat.Depth24PlusStencil8,
            SW.TextureFormat.Depth32float => W.TextureFormat.Depth32Float,
            _ => throw Unsupported(value)
        };

    private static W.AddressMode AddressMode(
        SW.AddressMode value) => value switch
        {
            SW.AddressMode.ClampToEdge => W.AddressMode.ClampToEdge,
            SW.AddressMode.Repeat => W.AddressMode.Repeat,
            SW.AddressMode.MirrorRepeat => W.AddressMode.MirrorRepeat,
            _ => throw Unsupported(value)
        };

    private static W.FilterMode FilterMode(
        SW.FilterMode value) => value switch
        {
            SW.FilterMode.Nearest => W.FilterMode.Nearest,
            SW.FilterMode.Linear => W.FilterMode.Linear,
            _ => throw Unsupported(value)
        };

    private static W.MipmapFilterMode MipmapFilterMode(
        SW.MipmapFilterMode value) => value switch
        {
            SW.MipmapFilterMode.Nearest =>
                W.MipmapFilterMode.Nearest,
            SW.MipmapFilterMode.Linear =>
                W.MipmapFilterMode.Linear,
            _ => throw Unsupported(value)
        };

    private static W.CompareFunction CompareFunction(
        SW.CompareFunction value) => value switch
        {
            SW.CompareFunction.Undefined =>
                W.CompareFunction.Undefined,
            SW.CompareFunction.Never => W.CompareFunction.Never,
            SW.CompareFunction.Less => W.CompareFunction.Less,
            SW.CompareFunction.LessEqual =>
                W.CompareFunction.LessEqual,
            SW.CompareFunction.Greater =>
                W.CompareFunction.Greater,
            SW.CompareFunction.GreaterEqual =>
                W.CompareFunction.GreaterEqual,
            SW.CompareFunction.Equal => W.CompareFunction.Equal,
            SW.CompareFunction.NotEqual =>
                W.CompareFunction.NotEqual,
            SW.CompareFunction.Always => W.CompareFunction.Always,
            _ => throw Unsupported(value)
        };

    private static W.PrimitiveTopology PrimitiveTopology(
        SW.PrimitiveTopology value) => value switch
        {
            SW.PrimitiveTopology.PointList =>
                W.PrimitiveTopology.PointList,
            SW.PrimitiveTopology.LineList =>
                W.PrimitiveTopology.LineList,
            SW.PrimitiveTopology.LineStrip =>
                W.PrimitiveTopology.LineStrip,
            SW.PrimitiveTopology.TriangleList =>
                W.PrimitiveTopology.TriangleList,
            SW.PrimitiveTopology.TriangleStrip =>
                W.PrimitiveTopology.TriangleStrip,
            _ => throw Unsupported(value)
        };

    private static W.IndexFormat IndexFormat(
        SW.IndexFormat value) => value switch
        {
            SW.IndexFormat.Undefined => W.IndexFormat.Undefined,
            SW.IndexFormat.Uint16 => W.IndexFormat.Uint16,
            SW.IndexFormat.Uint32 => W.IndexFormat.Uint32,
            _ => throw Unsupported(value)
        };

    private static W.FrontFace FrontFace(
        SW.FrontFace value) => value switch
        {
            SW.FrontFace.Ccw => W.FrontFace.CCW,
            SW.FrontFace.CW => W.FrontFace.CW,
            _ => throw Unsupported(value)
        };

    private static W.CullMode CullMode(
        SW.CullMode value) => value switch
        {
            SW.CullMode.None => W.CullMode.None,
            SW.CullMode.Front => W.CullMode.Front,
            SW.CullMode.Back => W.CullMode.Back,
            _ => throw Unsupported(value)
        };

    private static W.VertexStepMode VertexStepMode(
        SW.VertexStepMode value) => value switch
        {
            SW.VertexStepMode.Vertex => W.VertexStepMode.Vertex,
            SW.VertexStepMode.Instance => W.VertexStepMode.Instance,
            _ => throw Unsupported(value)
        };

    private static W.VertexFormat VertexFormat(
        SW.VertexFormat value) => value switch
        {
            SW.VertexFormat.Float32 => W.VertexFormat.Float32,
            SW.VertexFormat.Float32x2 => W.VertexFormat.Float32x2,
            SW.VertexFormat.Float32x3 => W.VertexFormat.Float32x3,
            SW.VertexFormat.Float32x4 => W.VertexFormat.Float32x4,
            _ => throw Unsupported(value)
        };

    private static W.BlendOperation BlendOperation(
        SW.BlendOperation value) => value switch
        {
            SW.BlendOperation.Add => W.BlendOperation.Add,
            SW.BlendOperation.Subtract =>
                W.BlendOperation.Subtract,
            SW.BlendOperation.ReverseSubtract =>
                W.BlendOperation.ReverseSubtract,
            SW.BlendOperation.Min => W.BlendOperation.Min,
            SW.BlendOperation.Max => W.BlendOperation.Max,
            _ => throw Unsupported(value)
        };

    private static W.BlendFactor BlendFactor(
        SW.BlendFactor value) => value switch
        {
            SW.BlendFactor.Zero => W.BlendFactor.Zero,
            SW.BlendFactor.One => W.BlendFactor.One,
            SW.BlendFactor.Src => W.BlendFactor.Src,
            SW.BlendFactor.OneMinusSrc =>
                W.BlendFactor.OneMinusSrc,
            SW.BlendFactor.SrcAlpha => W.BlendFactor.SrcAlpha,
            SW.BlendFactor.OneMinusSrcAlpha =>
                W.BlendFactor.OneMinusSrcAlpha,
            SW.BlendFactor.Dst => W.BlendFactor.Dst,
            SW.BlendFactor.OneMinusDst =>
                W.BlendFactor.OneMinusDst,
            SW.BlendFactor.DstAlpha => W.BlendFactor.DstAlpha,
            SW.BlendFactor.OneMinusDstAlpha =>
                W.BlendFactor.OneMinusDstAlpha,
            SW.BlendFactor.SrcAlphaSaturated =>
                W.BlendFactor.SrcAlphaSaturated,
            SW.BlendFactor.Constant => W.BlendFactor.Constant,
            SW.BlendFactor.OneMinusConstant =>
                W.BlendFactor.OneMinusConstant,
            _ => throw Unsupported(value)
        };

    private static W.StencilOperation StencilOperation(
        SW.StencilOperation value) => value switch
        {
            SW.StencilOperation.Keep => W.StencilOperation.Keep,
            SW.StencilOperation.Zero => W.StencilOperation.Zero,
            SW.StencilOperation.Replace =>
                W.StencilOperation.Replace,
            SW.StencilOperation.Invert =>
                W.StencilOperation.Invert,
            SW.StencilOperation.IncrementClamp =>
                W.StencilOperation.IncrementClamp,
            SW.StencilOperation.DecrementClamp =>
                W.StencilOperation.DecrementClamp,
            SW.StencilOperation.IncrementWrap =>
                W.StencilOperation.IncrementWrap,
            SW.StencilOperation.DecrementWrap =>
                W.StencilOperation.DecrementWrap,
            _ => throw Unsupported(value)
        };

    private static W.LoadOp LoadOp(SW.LoadOp value) =>
        value switch
        {
            SW.LoadOp.Undefined => W.LoadOp.Undefined,
            SW.LoadOp.Load => W.LoadOp.Load,
            SW.LoadOp.Clear => W.LoadOp.Clear,
            _ => throw Unsupported(value)
        };

    private static W.StoreOp StoreOp(SW.StoreOp value) =>
        value switch
        {
            SW.StoreOp.Undefined => W.StoreOp.Undefined,
            SW.StoreOp.Store => W.StoreOp.Store,
            SW.StoreOp.Discard => W.StoreOp.Discard,
            _ => throw Unsupported(value)
        };

    private static W.TextureAspect TextureAspect(
        SW.TextureAspect value) => value switch
        {
            SW.TextureAspect.All => W.TextureAspect.All,
            SW.TextureAspect.StencilOnly =>
                W.TextureAspect.StencilOnly,
            SW.TextureAspect.DepthOnly =>
                W.TextureAspect.DepthOnly,
            _ => throw Unsupported(value)
        };

    private static SW.BufferMapAsyncStatus BufferMapStatus(
        W.MapAsyncStatus value) => value switch
        {
            W.MapAsyncStatus.Success =>
                SW.BufferMapAsyncStatus.Success,
            W.MapAsyncStatus.CallbackCancelled =>
                SW.BufferMapAsyncStatus.UnmappedBeforeCallback,
            W.MapAsyncStatus.Error =>
                SW.BufferMapAsyncStatus.ValidationError,
            W.MapAsyncStatus.Aborted =>
                SW.BufferMapAsyncStatus.DeviceLost,
            _ => SW.BufferMapAsyncStatus.Unknown
        };

    private static int DescriptorCount(nuint count, string name)
    {
        if (count > MaxDescriptorItems)
        {
            throw new ArgumentOutOfRangeException(
                name,
                count,
                $"Dawn descriptor arrays are limited to {MaxDescriptorItems} items.");
        }
        return checked((int)count);
    }

    private static NotSupportedException Unsupported<T>(T value) =>
        new($"The Dawn backend does not support {typeof(T).Name} value {value}.");

    private static NotSupportedException
        ExternalSurfaceNotSupported() =>
        new(
            "Dawn external-memory contexts render into imported textures, not IWebGpuApi surfaces.");

    private static DawnFfi.StringViewFFI StringView(byte* value)
    {
        if (value == null)
        {
            return DawnFfi.StringViewFFI.NullValue;
        }
        ReadOnlySpan<byte> bytes =
            MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value);
        return DawnFfi.StringViewFFI.CreateExplicitlySized(
            value,
            bytes.Length);
    }

    private static DawnFfi.DeviceHandle DeviceHandle(
        SW.Device* value) => new((nuint)value);
    private static DawnFfi.QueueHandle QueueHandle(
        SW.Queue* value) => new((nuint)value);
    private static DawnFfi.BufferHandle BufferHandle(
        SilkBuffer* value) => new((nuint)value);
    private static DawnFfi.TextureHandle TextureHandle(
        SW.Texture* value) => new((nuint)value);
    private static DawnFfi.TextureViewHandle TextureViewHandle(
        SW.TextureView* value) => new((nuint)value);
    private static DawnFfi.BindGroupHandle BindGroupHandle(
        SW.BindGroup* value) => new((nuint)value);
    private static DawnFfi.BindGroupLayoutHandle BindGroupLayoutHandle(
        SW.BindGroupLayout* value) => new((nuint)value);
    private static DawnFfi.CommandEncoderHandle CommandEncoderHandle(
        SW.CommandEncoder* value) => new((nuint)value);
    private static DawnFfi.CommandBufferHandle CommandBufferHandle(
        SW.CommandBuffer* value) => new((nuint)value);
    private static DawnFfi.ComputePassEncoderHandle
        ComputePassEncoderHandle(
            SW.ComputePassEncoder* value) => new((nuint)value);
    private static DawnFfi.RenderPassEncoderHandle
        RenderPassEncoderHandle(
            SW.RenderPassEncoder* value) => new((nuint)value);
    private static DawnFfi.ComputePipelineHandle ComputePipelineHandle(
        SW.ComputePipeline* value) => new((nuint)value);
    private static DawnFfi.RenderPipelineHandle RenderPipelineHandle(
        SW.RenderPipeline* value) => new((nuint)value);
    private static DawnFfi.PipelineLayoutHandle PipelineLayoutHandle(
        SW.PipelineLayout* value) => new((nuint)value);
    private static DawnFfi.SamplerHandle SamplerHandle(
        SW.Sampler* value) => new((nuint)value);
    private static DawnFfi.ShaderModuleHandle ShaderModuleHandle(
        SW.ShaderModule* value) => new((nuint)value);

    private static SW.BindGroup* Pointer(
        DawnFfi.BindGroupHandle value) =>
        (SW.BindGroup*)value.GetAddress();
    private static SW.BindGroupLayout* Pointer(
        DawnFfi.BindGroupLayoutHandle value) =>
        (SW.BindGroupLayout*)value.GetAddress();
    private static SilkBuffer* Pointer(DawnFfi.BufferHandle value) =>
        (SilkBuffer*)value.GetAddress();
    private static SW.CommandEncoder* Pointer(
        DawnFfi.CommandEncoderHandle value) =>
        (SW.CommandEncoder*)value.GetAddress();
    private static SW.CommandBuffer* Pointer(
        DawnFfi.CommandBufferHandle value) =>
        (SW.CommandBuffer*)value.GetAddress();
    private static SW.ComputePassEncoder* Pointer(
        DawnFfi.ComputePassEncoderHandle value) =>
        (SW.ComputePassEncoder*)value.GetAddress();
    private static SW.RenderPassEncoder* Pointer(
        DawnFfi.RenderPassEncoderHandle value) =>
        (SW.RenderPassEncoder*)value.GetAddress();
    private static SW.ComputePipeline* Pointer(
        DawnFfi.ComputePipelineHandle value) =>
        (SW.ComputePipeline*)value.GetAddress();
    private static SW.RenderPipeline* Pointer(
        DawnFfi.RenderPipelineHandle value) =>
        (SW.RenderPipeline*)value.GetAddress();
    private static SW.PipelineLayout* Pointer(
        DawnFfi.PipelineLayoutHandle value) =>
        (SW.PipelineLayout*)value.GetAddress();
    private static SW.Sampler* Pointer(
        DawnFfi.SamplerHandle value) =>
        (SW.Sampler*)value.GetAddress();
    private static SW.ShaderModule* Pointer(
        DawnFfi.ShaderModuleHandle value) =>
        (SW.ShaderModule*)value.GetAddress();
    private static SW.Texture* Pointer(
        DawnFfi.TextureHandle value) =>
        (SW.Texture*)value.GetAddress();
    private static SW.TextureView* Pointer(
        DawnFfi.TextureViewHandle value) =>
        (SW.TextureView*)value.GetAddress();
}
