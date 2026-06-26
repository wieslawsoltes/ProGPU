using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using System.Runtime.InteropServices;

namespace ProGPU.DirectX;

public enum ProGpuDirectXCommandKind
{
    SetRenderTargets,
    SetViewport,
    SetScissorRect,
    SetPrimitiveTopology,
    SetVertexBuffer,
    SetIndexBuffer,
    SetConstantBuffer,
    SetInputLayout,
    SetVertexShader,
    SetPixelShader,
    SetGeometryShader,
    SetComputeShader,
    SetBlendState,
    SetDepthStencilState,
    SetRasterizerState,
    SetGraphicsPipeline,
    SetComputePipeline,
    SetBindingSnapshot,
    SetShaderResource,
    SetSampler,
    SetUnorderedAccessView,
    CopyTexture,
    CopyBuffer,
    ResolveTexture,
    ClearRenderTarget,
    ClearDepthStencil,
    Draw,
    DrawIndexed,
    Dispatch,
    Present
}

public sealed record ProGpuDirectXCommand
{
    public required ProGpuDirectXCommandKind Kind { get; init; }
    public ProGpuDirectXTexture2D? Texture { get; init; }
    public ProGpuDirectXBuffer? Buffer { get; init; }
    public DxColor Color { get; init; }
    public DxViewport Viewport { get; init; }
    public DxRect Rect { get; init; }
    public DxPrimitiveTopology Topology { get; init; }
    public DxDrawCall? Draw { get; init; }
    public DxDrawIndexedCall? DrawIndexed { get; init; }
    public DxDispatchCall? Dispatch { get; init; }
    public ProGpuDirectXShader? Shader { get; init; }
    public ProGpuDirectXInputLayout? InputLayout { get; init; }
    public ProGpuDirectXGraphicsPipeline? GraphicsPipeline { get; init; }
    public ProGpuDirectXComputePipeline? ComputePipeline { get; init; }
    public IReadOnlyDictionary<uint, ProGpuDirectXBuffer>? VertexBuffers { get; init; }
    public IReadOnlyDictionary<uint, DxVertexBufferBinding>? VertexBufferBindings { get; init; }
    public DxVertexBufferBinding? VertexBufferBinding { get; init; }
    public ProGpuDirectXBuffer? IndexBuffer { get; init; }
    public ProGpuDirectXTexture2D? DepthStencilTexture { get; init; }
    public ProGpuDirectXRenderTargetView? RenderTargetView { get; init; }
    public ProGpuDirectXDepthStencilView? DepthStencilView { get; init; }
    public ProGpuDirectXShaderResourceView? ShaderResourceView { get; init; }
    public ProGpuDirectXSamplerState? Sampler { get; init; }
    public ProGpuDirectXUnorderedAccessView? UnorderedAccessView { get; init; }
    public ProGpuDirectXBindingSnapshot? BindingSnapshot { get; init; }
    public ProGpuDirectXBindingValidationResult? BindingValidation { get; init; }
    public DxBlendStateDescriptor? BlendState { get; init; }
    public DxDepthStencilStateDescriptor? DepthStencilState { get; init; }
    public DxRasterizerStateDescriptor? RasterizerState { get; init; }
    public DxConstantBufferBinding? ConstantBufferBinding { get; init; }
    public DxShaderResourceBinding? ResourceBinding { get; init; }
    public DxDepthStencilClearFlags DepthStencilClearFlags { get; init; }
    public uint BufferSlot { get; init; }
    public DxIndexFormat IndexFormat { get; init; }
    public ProGpuDirectXTexture2D? SourceTexture { get; init; }
    public ProGpuDirectXTexture2D? DestinationTexture { get; init; }
    public ProGpuDirectXBuffer? SourceBuffer { get; init; }
    public ProGpuDirectXBuffer? DestinationBuffer { get; init; }
    public DxCopyResourceCall? Copy { get; init; }
    public DxResolveSubresourceCall? Resolve { get; init; }
}

public sealed unsafe class ProGpuDirectXDeviceContext : IDisposable
{
    private const int MaxPipelineBindGroupCacheEntries = 512;
    private readonly ProGpuDirectXDevice _device;
    private readonly List<ProGpuDirectXCommand> _commands = new();
    private ProGpuDirectXTexture2D? _renderTarget;
    private ProGpuDirectXTexture2D? _depthStencil;
    private ProGpuDirectXRenderTargetView? _renderTargetView;
    private ProGpuDirectXDepthStencilView? _depthStencilView;
    private DxPrimitiveTopology _topology = DxPrimitiveTopology.TriangleList;
    private ProGpuDirectXInputLayout? _inputLayout;
    private ProGpuDirectXShader? _vertexShader;
    private ProGpuDirectXShader? _pixelShader;
    private ProGpuDirectXShader? _geometryShader;
    private ProGpuDirectXShader? _computeShader;
    private ProGpuDirectXGraphicsPipeline? _graphicsPipeline;
    private ProGpuDirectXComputePipeline? _computePipeline;
    private readonly Dictionary<uint, ProGpuDirectXBuffer> _vertexBuffers = new();
    private readonly Dictionary<uint, DxVertexBufferBinding> _vertexBufferBindings = new();
    private ProGpuDirectXBuffer? _indexBuffer;
    private DxIndexFormat _indexFormat = DxIndexFormat.UInt32;
    private readonly Dictionary<DxConstantBufferBinding, ProGpuDirectXBuffer?> _constantBuffers = new();
    private readonly Dictionary<DxShaderResourceBinding, ProGpuDirectXShaderResourceView?> _shaderResourceViews = new();
    private readonly Dictionary<DxShaderResourceBinding, ProGpuDirectXSamplerState?> _samplers = new();
    private readonly Dictionary<uint, ProGpuDirectXUnorderedAccessView?> _unorderedAccessViews = new();
    private readonly Dictionary<WireframeIndexCacheKey, WireframeIndexCacheEntry> _wireframeIndexBuffers = new();
    private readonly Dictionary<PipelineBindGroupCacheKey, CachedPipelineBindGroup> _pipelineBindGroupCache = new();
    private bool _isDisposed;

    private readonly record struct PipelineBindGroupCacheKey(bool IsCompute, IntPtr Pipeline, string BindingKey);

    private sealed class CachedPipelineBindGroup
    {
        public CachedPipelineBindGroup(BindGroup* bindGroup)
        {
            BindGroup = (IntPtr)bindGroup;
        }

        public IntPtr BindGroup { get; }

        public BindGroup* BindGroupPointer => (BindGroup*)BindGroup;

        public void QueueDisposal(ProGPU.Backend.WgpuContext? context)
        {
            if (context is { IsDisposed: false })
            {
                context.QueueBindGroupDisposal(BindGroup);
            }
        }
    }

    private readonly record struct WireframeIndexCacheKey(
        ProGpuDirectXBuffer? SourceIndexBuffer,
        ulong SourceGeneration,
        bool IsIndexed,
        DxPrimitiveTopology Topology,
        DxIndexFormat SourceIndexFormat,
        uint Count,
        uint StartLocation);

    private sealed class WireframeIndexCacheEntry : IDisposable
    {
        public WireframeIndexCacheEntry(ProGPU.Backend.GpuBuffer buffer, uint indexCount)
        {
            Buffer = buffer;
            IndexCount = indexCount;
        }

        public ProGPU.Backend.GpuBuffer Buffer { get; }

        public uint IndexCount { get; }

        public void Dispose()
        {
            Buffer.Dispose();
        }
    }

    internal ProGpuDirectXDeviceContext(ProGpuDirectXDevice device)
    {
        _device = device;
    }

    public IReadOnlyList<ProGpuDirectXCommand> Commands => _commands;

    public ProGpuDirectXTexture2D? RenderTarget => _renderTarget;

    public ProGpuDirectXTexture2D? DepthStencil => _depthStencil;

    public ProGpuDirectXRenderTargetView? RenderTargetView => _renderTargetView;

    public ProGpuDirectXDepthStencilView? DepthStencilView => _depthStencilView;

    public DxViewport Viewport { get; private set; }

    public DxRect ScissorRect { get; private set; }

    public ProGpuDirectXInputLayout? InputLayout => _inputLayout;

    public ProGpuDirectXShader? VertexShader => _vertexShader;

    public ProGpuDirectXShader? PixelShader => _pixelShader;

    public ProGpuDirectXShader? GeometryShader => _geometryShader;

    public ProGpuDirectXShader? ComputeShader => _computeShader;

    public ProGpuDirectXGraphicsPipeline? GraphicsPipeline => _graphicsPipeline;

    public ProGpuDirectXComputePipeline? ComputePipeline => _computePipeline;

    public IReadOnlyDictionary<DxShaderResourceBinding, ProGpuDirectXShaderResourceView?> ShaderResourceViews => _shaderResourceViews;

    public IReadOnlyDictionary<DxShaderResourceBinding, ProGpuDirectXSamplerState?> Samplers => _samplers;

    public IReadOnlyDictionary<uint, ProGpuDirectXBuffer> VertexBuffers => _vertexBuffers;

    public IReadOnlyDictionary<uint, DxVertexBufferBinding> VertexBufferBindings => _vertexBufferBindings;

    public ProGpuDirectXBuffer? IndexBuffer => _indexBuffer;

    public IReadOnlyDictionary<DxConstantBufferBinding, ProGpuDirectXBuffer?> ConstantBuffers => _constantBuffers;

    public IReadOnlyDictionary<uint, ProGpuDirectXUnorderedAccessView?> UnorderedAccessViews => _unorderedAccessViews;

    public ulong SubmittedDrawCount { get; private set; }

    public ulong SubmittedWireframeDrawCount { get; private set; }

    public ulong SubmittedDispatchCount { get; private set; }

    public ulong SubmittedCopyCount { get; private set; }

    public ulong SubmittedResolveCount { get; private set; }

    public ulong SubmittedClearCount { get; private set; }

    public ulong DrawBindGroupCacheHitCount { get; private set; }

    public ulong DrawBindGroupCacheMissCount { get; private set; }

    public ulong DispatchBindGroupCacheHitCount { get; private set; }

    public ulong DispatchBindGroupCacheMissCount { get; private set; }

    public int CachedPipelineBindGroupCount => _pipelineBindGroupCache.Count;

    public void SetRenderTargets(ProGpuDirectXTexture2D? renderTarget, ProGpuDirectXTexture2D? depthStencil = null)
    {
        ThrowIfDisposed();
        _renderTarget = renderTarget;
        _depthStencil = depthStencil;
        _renderTargetView = null;
        _depthStencilView = null;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetRenderTargets,
            Texture = renderTarget,
            DepthStencilTexture = depthStencil
        });
    }

    public void SetRenderTargets(
        ProGpuDirectXRenderTargetView? renderTargetView,
        ProGpuDirectXDepthStencilView? depthStencilView = null)
    {
        ThrowIfDisposed();
        _renderTargetView = renderTargetView;
        _depthStencilView = depthStencilView;
        _renderTarget = renderTargetView?.Texture;
        _depthStencil = depthStencilView?.Texture;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetRenderTargets,
            Texture = _renderTarget,
            DepthStencilTexture = _depthStencil,
            RenderTargetView = renderTargetView,
            DepthStencilView = depthStencilView
        });
    }

    public void SetViewport(DxViewport viewport)
    {
        ThrowIfDisposed();
        Viewport = viewport;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetViewport,
            Viewport = viewport
        });
    }

    public void SetScissorRect(DxRect rect)
    {
        ThrowIfDisposed();
        ScissorRect = rect;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetScissorRect,
            Rect = rect
        });
    }

    public void SetPrimitiveTopology(DxPrimitiveTopology topology)
    {
        ThrowIfDisposed();
        _topology = topology;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetPrimitiveTopology,
            Topology = topology
        });
    }

    public void SetVertexBuffer(ProGpuDirectXBuffer buffer)
    {
        SetVertexBuffer(0, buffer);
    }

    public void SetVertexBuffer(ProGpuDirectXBuffer buffer, uint strideInBytes, ulong offsetBytes = 0)
    {
        SetVertexBuffer(0, buffer, strideInBytes, offsetBytes);
    }

    public void SetVertexBuffer(uint slot, ProGpuDirectXBuffer buffer)
    {
        SetVertexBuffer(slot, buffer, 0, 0);
    }

    public void SetVertexBuffer(uint slot, ProGpuDirectXBuffer buffer, uint strideInBytes, ulong offsetBytes = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        if ((buffer.Descriptor.Usage & DxBufferUsage.Vertex) == 0)
        {
            throw new ArgumentException("Buffer was not created with vertex-buffer usage.", nameof(buffer));
        }

        if (offsetBytes >= buffer.Descriptor.SizeInBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetBytes), "Vertex-buffer offset must be inside the buffer.");
        }

        if (_device.IsGpuBacked && (offsetBytes % 4ul) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetBytes), "GPU-backed vertex-buffer offsets must be aligned to 4 bytes.");
        }

        var binding = new DxVertexBufferBinding(
            buffer,
            ResolveVertexStride(slot, buffer, strideInBytes),
            offsetBytes);

        _vertexBuffers[slot] = buffer;
        _vertexBufferBindings[slot] = binding;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetVertexBuffer,
            BufferSlot = slot,
            Buffer = buffer,
            VertexBufferBinding = binding
        });
    }

    public void SetIndexBuffer(ProGpuDirectXBuffer buffer, DxIndexFormat indexFormat = DxIndexFormat.UInt32)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        if ((buffer.Descriptor.Usage & DxBufferUsage.Index) == 0)
        {
            throw new ArgumentException("Buffer was not created with index-buffer usage.", nameof(buffer));
        }

        _indexBuffer = buffer;
        _indexFormat = indexFormat;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetIndexBuffer,
            IndexFormat = indexFormat,
            Buffer = buffer
        });
    }

    public void SetConstantBuffer(uint slot, ProGpuDirectXBuffer buffer)
    {
        SetConstantBuffer(DxShaderStage.Vertex, slot, buffer);
    }

    public void SetConstantBuffer(DxShaderStage stage, uint slot, ProGpuDirectXBuffer? buffer)
    {
        ThrowIfDisposed();
        if (buffer is not null && (buffer.Descriptor.Usage & DxBufferUsage.Constant) == 0)
        {
            throw new ArgumentException("Buffer was not created with constant-buffer usage.", nameof(buffer));
        }

        var binding = new DxConstantBufferBinding(stage, slot);
        _constantBuffers[binding] = buffer;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetConstantBuffer,
            ConstantBufferBinding = binding,
            Buffer = buffer
        });
    }

    public void SetInputLayout(ProGpuDirectXInputLayout? inputLayout)
    {
        ThrowIfDisposed();
        _inputLayout = inputLayout;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetInputLayout,
            InputLayout = inputLayout
        });
    }

    public void SetVertexShader(ProGpuDirectXShader? shader)
    {
        ThrowIfDisposed();
        ValidateShaderStage(shader, DxShaderStage.Vertex);
        _vertexShader = shader;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetVertexShader,
            Shader = shader
        });
    }

    public void SetPixelShader(ProGpuDirectXShader? shader)
    {
        ThrowIfDisposed();
        ValidateShaderStage(shader, DxShaderStage.Pixel);
        _pixelShader = shader;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetPixelShader,
            Shader = shader
        });
    }

    public void SetGeometryShader(ProGpuDirectXShader? shader)
    {
        ThrowIfDisposed();
        ValidateShaderStage(shader, DxShaderStage.Geometry);
        _geometryShader = shader;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetGeometryShader,
            Shader = shader
        });
    }

    public void SetComputeShader(ProGpuDirectXShader? shader)
    {
        ThrowIfDisposed();
        ValidateShaderStage(shader, DxShaderStage.Compute);
        _computeShader = shader;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetComputeShader,
            Shader = shader
        });
    }

    public void SetBlendState(DxBlendStateDescriptor blendState)
    {
        ThrowIfDisposed();
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetBlendState,
            BlendState = blendState
        });
    }

    public void SetDepthStencilState(DxDepthStencilStateDescriptor depthStencilState)
    {
        ThrowIfDisposed();
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetDepthStencilState,
            DepthStencilState = depthStencilState
        });
    }

    public void SetRasterizerState(DxRasterizerStateDescriptor rasterizerState)
    {
        ThrowIfDisposed();
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetRasterizerState,
            RasterizerState = rasterizerState
        });
    }

    public void SetGraphicsPipeline(ProGpuDirectXGraphicsPipeline pipeline)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pipeline);
        _graphicsPipeline = pipeline;
        _topology = pipeline.Descriptor.Topology;
        _inputLayout = pipeline.EffectiveInputLayout;
        _vertexShader = pipeline.Descriptor.VertexShader;
        _pixelShader = pipeline.Descriptor.PixelShader;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetGraphicsPipeline,
            GraphicsPipeline = pipeline,
            Topology = _topology
        });
    }

    public void SetComputePipeline(ProGpuDirectXComputePipeline pipeline)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pipeline);
        _computePipeline = pipeline;
        _computeShader = pipeline.Descriptor.ComputeShader;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetComputePipeline,
            ComputePipeline = pipeline
        });
    }

    public void SetShaderResource(DxShaderStage stage, uint slot, ProGpuDirectXShaderResourceView? view)
    {
        ThrowIfDisposed();
        var binding = new DxShaderResourceBinding(stage, slot);
        _shaderResourceViews[binding] = view;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetShaderResource,
            ResourceBinding = binding,
            ShaderResourceView = view
        });
    }

    public void SetSampler(DxShaderStage stage, uint slot, ProGpuDirectXSamplerState? sampler)
    {
        ThrowIfDisposed();
        var binding = new DxShaderResourceBinding(stage, slot);
        _samplers[binding] = sampler;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetSampler,
            ResourceBinding = binding,
            Sampler = sampler
        });
    }

    public void SetUnorderedAccessView(uint slot, ProGpuDirectXUnorderedAccessView? view)
    {
        ThrowIfDisposed();
        _unorderedAccessViews[slot] = view;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetUnorderedAccessView,
            UnorderedAccessView = view,
            ResourceBinding = new DxShaderResourceBinding(DxShaderStage.Compute, slot)
        });
    }

    public ProGpuDirectXMappedSubresource Map(
        ProGpuDirectXBuffer buffer,
        DxMapMode mode,
        DxMapFlags flags = DxMapFlags.None,
        uint offsetBytes = 0,
        uint? sizeInBytes = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        return buffer.Map(mode, flags, offsetBytes, sizeInBytes);
    }

    public ProGpuDirectXMappedSubresource Map(
        ProGpuDirectXTexture2D texture,
        DxMapMode mode,
        DxMapFlags flags = DxMapFlags.None,
        uint subresource = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(texture);
        return texture.Map(mode, flags, subresource);
    }

    public void Unmap(ProGpuDirectXMappedSubresource mapping)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mapping);
        mapping.Unmap();
    }

    public ProGpuDirectXBindingSnapshot CreateBindingSnapshot(DxShaderStage stage, string? label = null)
    {
        return CreateBindingSnapshot(ToStageFlags(stage), label);
    }

    public ProGpuDirectXBindingSnapshot CreateBindingSnapshot(DxShaderStageFlags stages, string? label = null)
    {
        ThrowIfDisposed();
        return CreateBindingSnapshotCore(stages, label ?? "ProGPU DirectX Binding Snapshot");
    }

    public ProGpuDirectXBindingValidationResult ValidateGraphicsPipelineBindings()
    {
        return ValidateGraphicsPipelineBindings(_graphicsPipeline);
    }

    public ProGpuDirectXBindingValidationResult ValidateGraphicsPipelineBindings(ProGpuDirectXGraphicsPipeline? pipeline)
    {
        ThrowIfDisposed();
        if (pipeline is null)
        {
            return ProGpuDirectXBindingValidationResult.Success;
        }

        return ValidateBindingRequirements(
            pipeline.ReflectedBindingRequirementsSupported,
            pipeline.ReflectedBindingRequirementsFailureReason,
            pipeline.ReflectedBindingRequirements);
    }

    public ProGpuDirectXBindingValidationResult ValidateComputePipelineBindings()
    {
        return ValidateComputePipelineBindings(_computePipeline);
    }

    public ProGpuDirectXBindingValidationResult ValidateComputePipelineBindings(ProGpuDirectXComputePipeline? pipeline)
    {
        ThrowIfDisposed();
        if (pipeline is null)
        {
            return ProGpuDirectXBindingValidationResult.Success;
        }

        return ValidateBindingRequirements(
            pipeline.ReflectedBindingRequirementsSupported,
            pipeline.ReflectedBindingRequirementsFailureReason,
            pipeline.ReflectedBindingRequirements);
    }

    public void ApplyBindings(DxShaderStageFlags stages, string? label = null)
    {
        ThrowIfDisposed();
        var snapshot = CreateBindingSnapshotCore(stages, label ?? "ProGPU DirectX Applied Bindings");
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetBindingSnapshot,
            BindingSnapshot = snapshot
        });
    }

    public void CopyResource(ProGpuDirectXTexture2D destination, ProGpuDirectXTexture2D source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ValidateTextureCopy(destination, source);

        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.CopyTexture,
            DestinationTexture = destination,
            SourceTexture = source,
            Copy = new DxCopyResourceCall("Texture2D")
        });
    }

    public void CopyResource(ProGpuDirectXBuffer destination, ProGpuDirectXBuffer source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        if (destination.Descriptor.SizeInBytes < source.Descriptor.SizeInBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), "Destination buffer is smaller than the source buffer.");
        }

        if ((destination.Descriptor.Usage & DxBufferUsage.CopyDestination) == 0)
        {
            throw new ArgumentException("Destination buffer was not created with copy-destination usage.", nameof(destination));
        }

        if ((source.Descriptor.Usage & DxBufferUsage.CopySource) == 0)
        {
            throw new ArgumentException("Source buffer was not created with copy-source usage.", nameof(source));
        }

        destination.CopyCpuShadowFrom(source);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.CopyBuffer,
            DestinationBuffer = destination,
            SourceBuffer = source,
            Copy = new DxCopyResourceCall("Buffer")
        });
    }

    public void ResolveResource(ProGpuDirectXTexture2D destination, ProGpuDirectXTexture2D source)
    {
        ResolveSubresource(destination, 0, source, 0, source.Descriptor.Format);
    }

    public void ResolveSubresource(
        ProGpuDirectXTexture2D destination,
        uint destinationSubresource,
        ProGpuDirectXTexture2D source,
        uint sourceSubresource,
        DxResourceFormat format)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ValidateTextureResolve(destination, source, destinationSubresource, sourceSubresource, format);

        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.ResolveTexture,
            DestinationTexture = destination,
            SourceTexture = source,
            Resolve = new DxResolveSubresourceCall(destinationSubresource, sourceSubresource, format)
        });
    }

    public void ClearRenderTarget(ProGpuDirectXTexture2D renderTarget, DxColor color)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(renderTarget);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.ClearRenderTarget,
            Texture = renderTarget,
            Color = color
        });
    }

    public void ClearRenderTarget(ProGpuDirectXRenderTargetView renderTargetView, DxColor color)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(renderTargetView);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.ClearRenderTarget,
            Texture = renderTargetView.Texture,
            RenderTargetView = renderTargetView,
            Color = color
        });
    }

    public void ClearDepthStencil(ProGpuDirectXTexture2D depthStencil, float depth = 1f, byte stencil = 0)
    {
        ClearDepthStencil(depthStencil, DxDepthStencilClearFlags.DepthStencil, depth, stencil);
    }

    public void ClearDepthStencil(
        ProGpuDirectXDepthStencilView depthStencilView,
        float depth = 1f,
        byte stencil = 0)
    {
        ClearDepthStencil(depthStencilView, DxDepthStencilClearFlags.DepthStencil, depth, stencil);
    }

    public void ClearDepthStencil(
        ProGpuDirectXDepthStencilView depthStencilView,
        DxDepthStencilClearFlags clearFlags,
        float depth = 1f,
        byte stencil = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(depthStencilView);
        if (clearFlags == DxDepthStencilClearFlags.None)
        {
            throw new ArgumentOutOfRangeException(nameof(clearFlags), "At least one depth-stencil clear flag is required.");
        }

        var depthStencil = depthStencilView.Texture ?? throw new ArgumentException("Depth-stencil view does not reference a texture.", nameof(depthStencilView));
        ValidateDepthStencilTexture(depthStencil);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.ClearDepthStencil,
            Texture = depthStencil,
            DepthStencilView = depthStencilView,
            DepthStencilClearFlags = clearFlags,
            Color = new DxColor(depth, stencil / 255f, 0f, 0f)
        });
    }

    public void ClearDepthStencil(
        ProGpuDirectXTexture2D depthStencil,
        DxDepthStencilClearFlags clearFlags,
        float depth = 1f,
        byte stencil = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(depthStencil);
        if (clearFlags == DxDepthStencilClearFlags.None)
        {
            throw new ArgumentOutOfRangeException(nameof(clearFlags), "At least one depth-stencil clear flag is required.");
        }

        ValidateDepthStencilTexture(depthStencil);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.ClearDepthStencil,
            Texture = depthStencil,
            DepthStencilClearFlags = clearFlags,
            Color = new DxColor(depth, stencil / 255f, 0f, 0f)
        });
    }

    public void Draw(uint vertexCount, uint startVertexLocation = 0, uint instanceCount = 1, uint startInstanceLocation = 0)
    {
        ThrowIfDisposed();
        var draw = new DxDrawCall(_topology, vertexCount, startVertexLocation, instanceCount, startInstanceLocation);
        var bindingValidation = ValidateGraphicsPipelineBindings(_graphicsPipeline);
        ThrowIfBindingValidationFails(bindingValidation);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.Draw,
            Texture = _renderTarget,
            DepthStencilTexture = _depthStencil,
            Topology = _topology,
            Viewport = Viewport,
            Rect = ScissorRect,
            Draw = draw,
            GraphicsPipeline = _graphicsPipeline,
            VertexBuffers = SnapshotVertexBuffers(),
            VertexBufferBindings = SnapshotVertexBufferBindings(),
            BindingSnapshot = CreateBindingSnapshotCore(
                DxShaderStageFlags.AllGraphics,
                "ProGPU DirectX Draw Bindings",
                createStandaloneBackendBindGroup: false),
            BindingValidation = bindingValidation
        });
    }

    public void DrawInstanced(
        uint vertexCountPerInstance,
        uint instanceCount,
        uint startVertexLocation = 0,
        uint startInstanceLocation = 0)
    {
        Draw(vertexCountPerInstance, startVertexLocation, instanceCount, startInstanceLocation);
    }

    public void DrawIndexed(
        uint indexCount,
        uint startIndexLocation = 0,
        int baseVertexLocation = 0,
        uint instanceCount = 1,
        uint startInstanceLocation = 0,
        DxIndexFormat? indexFormat = null)
    {
        ThrowIfDisposed();
        var effectiveIndexFormat = indexFormat ?? _indexFormat;
        var draw = new DxDrawIndexedCall(
            _topology,
            indexCount,
            startIndexLocation,
            baseVertexLocation,
            instanceCount,
            startInstanceLocation,
            effectiveIndexFormat);
        var bindingValidation = ValidateGraphicsPipelineBindings(_graphicsPipeline);
        ThrowIfBindingValidationFails(bindingValidation);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.DrawIndexed,
            Texture = _renderTarget,
            DepthStencilTexture = _depthStencil,
            Topology = _topology,
            Viewport = Viewport,
            Rect = ScissorRect,
            DrawIndexed = draw,
            GraphicsPipeline = _graphicsPipeline,
            VertexBuffers = SnapshotVertexBuffers(),
            VertexBufferBindings = SnapshotVertexBufferBindings(),
            IndexBuffer = _indexBuffer,
            IndexFormat = effectiveIndexFormat,
            BindingSnapshot = CreateBindingSnapshotCore(
                DxShaderStageFlags.AllGraphics,
                "ProGPU DirectX DrawIndexed Bindings",
                createStandaloneBackendBindGroup: false),
            BindingValidation = bindingValidation
        });
    }

    public void DrawIndexedInstanced(
        uint indexCountPerInstance,
        uint instanceCount,
        uint startIndexLocation = 0,
        int baseVertexLocation = 0,
        uint startInstanceLocation = 0,
        DxIndexFormat? indexFormat = null)
    {
        DrawIndexed(
            indexCountPerInstance,
            startIndexLocation,
            baseVertexLocation,
            instanceCount,
            startInstanceLocation,
            indexFormat);
    }

    public void Dispatch(uint threadGroupCountX, uint threadGroupCountY, uint threadGroupCountZ)
    {
        ThrowIfDisposed();
        if (threadGroupCountX == 0 || threadGroupCountY == 0 || threadGroupCountZ == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threadGroupCountX), "Dispatch dimensions must be non-zero.");
        }

        var bindingValidation = ValidateComputePipelineBindings(_computePipeline);
        ThrowIfBindingValidationFails(bindingValidation);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.Dispatch,
            Dispatch = new DxDispatchCall(threadGroupCountX, threadGroupCountY, threadGroupCountZ),
            ComputePipeline = _computePipeline,
            BindingSnapshot = CreateBindingSnapshotCore(
                DxShaderStageFlags.Compute,
                "ProGPU DirectX Dispatch Bindings",
                createStandaloneBackendBindGroup: false),
            BindingValidation = bindingValidation
        });
    }

    public void Present(ProGpuDirectXSwapChain swapChain)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(swapChain);
        swapChain.Present();
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.Present,
            Texture = swapChain.BackBuffer
        });
    }

    public void ClearRecordedCommands()
    {
        ThrowIfDisposed();
        ClearRecordedCommandResources();
    }

    public void ClearCachedPipelineBindGroups()
    {
        ThrowIfDisposed();
        ClearCachedPipelineBindGroupsCore();
    }

    public void Flush(bool clearRecordedCommands = true)
    {
        ThrowIfDisposed();
        _device.ThrowIfDisposed();

        if (_device.Context is { } context && _device.IsGpuBacked)
        {
            ExecuteGpuBackedCommands(context);
            context.CleanupPendingResources();
        }

        if (clearRecordedCommands)
        {
            ClearRecordedCommandResources();
        }
    }

    private ProGpuDirectXBindingSnapshot CreateBindingSnapshotCore(
        DxShaderStageFlags stages,
        string label,
        bool createStandaloneBackendBindGroup = true)
    {
        var entries = BuildBindingEntries(stages);
        return new ProGpuDirectXBindingSnapshot(_device, stages, entries, label, createStandaloneBackendBindGroup);
    }

    private ProGpuDirectXBindingValidationResult ValidateBindingRequirements(
        bool requirementsSupported,
        string? failureReason,
        IReadOnlyList<DxReflectedShaderBindingRequirement> requirements)
    {
        if (!requirementsSupported)
        {
            return new ProGpuDirectXBindingValidationResult(
            [
                new ProGpuDirectXBindingValidationIssue(
                    ProGpuDirectXBindingValidationIssueKind.UnsupportedReflectedRequirements,
                    $"DirectX reflected binding requirements are unsupported: {failureReason ?? "unknown failure"}.")
            ]);
        }

        if (requirements.Count == 0)
        {
            return ProGpuDirectXBindingValidationResult.Success;
        }

        var issues = new List<ProGpuDirectXBindingValidationIssue>();
        foreach (var requirement in requirements)
        {
            var count = requirement.Count == 0 ? 1u : requirement.Count;
            for (uint offset = 0; offset < count; offset++)
            {
                var slot = requirement.Slot + offset;
                if (HasBoundResource(requirement.Stage, requirement.Kind, slot))
                {
                    continue;
                }

                var nativeBinding = ProGpuDirectXNativeBindingMap.GetNativeBinding(
                    requirement.Stage,
                    requirement.Kind,
                    slot);
                issues.Add(new ProGpuDirectXBindingValidationIssue(
                    ProGpuDirectXBindingValidationIssueKind.MissingBinding,
                    $"Missing DirectX {requirement.Kind} binding '{requirement.Name}' for {requirement.Stage} slot {slot} (native binding {nativeBinding}).",
                    requirement.Name,
                    requirement.Stage,
                    requirement.Kind,
                    slot,
                    nativeBinding));
            }
        }

        return issues.Count == 0
            ? ProGpuDirectXBindingValidationResult.Success
            : new ProGpuDirectXBindingValidationResult(issues);
    }

    private bool HasBoundResource(DxShaderStage stage, ProGpuDirectXBindingKind kind, uint slot)
    {
        return kind switch
        {
            ProGpuDirectXBindingKind.ConstantBuffer =>
                _constantBuffers.TryGetValue(new DxConstantBufferBinding(stage, slot), out var buffer) &&
                buffer is not null,
            ProGpuDirectXBindingKind.ShaderResourceView =>
                _shaderResourceViews.TryGetValue(new DxShaderResourceBinding(stage, slot), out var view) &&
                view is not null,
            ProGpuDirectXBindingKind.Sampler =>
                _samplers.TryGetValue(new DxShaderResourceBinding(stage, slot), out var sampler) &&
                sampler is not null,
            ProGpuDirectXBindingKind.UnorderedAccessView =>
                stage == DxShaderStage.Compute &&
                _unorderedAccessViews.TryGetValue(slot, out var unorderedAccessView) &&
                unorderedAccessView is not null,
            _ => false
        };
    }

    private void ThrowIfBindingValidationFails(ProGpuDirectXBindingValidationResult validation)
    {
        if (_device.Options.EnableValidation && !validation.IsValid)
        {
            throw new InvalidOperationException(validation.ToExceptionMessage());
        }
    }

    private IReadOnlyDictionary<uint, ProGpuDirectXBuffer> SnapshotVertexBuffers()
    {
        return _vertexBuffers.Count == 0
            ? new Dictionary<uint, ProGpuDirectXBuffer>()
            : _vertexBuffers.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private IReadOnlyDictionary<uint, DxVertexBufferBinding> SnapshotVertexBufferBindings()
    {
        return _vertexBufferBindings.Count == 0
            ? new Dictionary<uint, DxVertexBufferBinding>()
            : _vertexBufferBindings.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private uint ResolveVertexStride(uint slot, ProGpuDirectXBuffer buffer, uint strideInBytes)
    {
        if (strideInBytes > 0)
        {
            return strideInBytes;
        }

        if (buffer.Descriptor.StrideInBytes > 0)
        {
            return buffer.Descriptor.StrideInBytes;
        }

        var inferredStride = _inputLayout?.GetInferredStride(slot) ??
            _graphicsPipeline?.EffectiveInputLayout?.GetInferredStride(slot) ??
            0;
        if (inferredStride > 0)
        {
            return inferredStride;
        }

        throw new ArgumentOutOfRangeException(nameof(strideInBytes), "Vertex-buffer stride must be non-zero or inferable from the active input layout.");
    }

    private IReadOnlyList<ProGpuDirectXBindingEntry> BuildBindingEntries(DxShaderStageFlags stages)
    {
        var entries = new List<ProGpuDirectXBindingEntry>();
        foreach (var stage in EnumerateStages(stages))
        {
            AddStageBindings(entries, stage);
        }

        return entries
            .Select(entry => entry with
            {
                NativeBinding = ProGpuDirectXNativeBindingMap.GetNativeBinding(entry.Stage, entry.Kind, entry.Slot)
            })
            .OrderBy(entry => entry.NativeBinding)
            .ThenBy(entry => entry.Stage)
            .ToArray();
    }

    private void AddStageBindings(List<ProGpuDirectXBindingEntry> entries, DxShaderStage stage)
    {
        foreach (var pair in _constantBuffers)
        {
            if (pair.Key.Stage == stage && pair.Value is { } buffer)
            {
                entries.Add(new ProGpuDirectXBindingEntry
                {
                    Kind = ProGpuDirectXBindingKind.ConstantBuffer,
                    Stage = stage,
                    Slot = pair.Key.Slot,
                    ConstantBuffer = buffer
                });
            }
        }

        foreach (var pair in _shaderResourceViews)
        {
            if (pair.Key.Stage == stage && pair.Value is { } view)
            {
                entries.Add(new ProGpuDirectXBindingEntry
                {
                    Kind = ProGpuDirectXBindingKind.ShaderResourceView,
                    Stage = stage,
                    Slot = pair.Key.Slot,
                    ShaderResourceView = view
                });
            }
        }

        foreach (var pair in _samplers)
        {
            if (pair.Key.Stage == stage && pair.Value is { } sampler)
            {
                entries.Add(new ProGpuDirectXBindingEntry
                {
                    Kind = ProGpuDirectXBindingKind.Sampler,
                    Stage = stage,
                    Slot = pair.Key.Slot,
                    Sampler = sampler
                });
            }
        }

        if (stage != DxShaderStage.Compute)
        {
            return;
        }

        foreach (var pair in _unorderedAccessViews)
        {
            if (pair.Value is { } view)
            {
                entries.Add(new ProGpuDirectXBindingEntry
                {
                    Kind = ProGpuDirectXBindingKind.UnorderedAccessView,
                    Stage = stage,
                    Slot = pair.Key,
                    UnorderedAccessView = view
                });
            }
        }
    }

    private static IEnumerable<DxShaderStage> EnumerateStages(DxShaderStageFlags stages)
    {
        if ((stages & DxShaderStageFlags.Vertex) != 0)
        {
            yield return DxShaderStage.Vertex;
        }

        if ((stages & DxShaderStageFlags.Pixel) != 0)
        {
            yield return DxShaderStage.Pixel;
        }

        if ((stages & DxShaderStageFlags.Geometry) != 0)
        {
            yield return DxShaderStage.Geometry;
        }

        if ((stages & DxShaderStageFlags.Compute) != 0)
        {
            yield return DxShaderStage.Compute;
        }
    }

    private static DxShaderStageFlags ToStageFlags(DxShaderStage stage)
    {
        return stage switch
        {
            DxShaderStage.Vertex => DxShaderStageFlags.Vertex,
            DxShaderStage.Pixel => DxShaderStageFlags.Pixel,
            DxShaderStage.Geometry => DxShaderStageFlags.Geometry,
            DxShaderStage.Compute => DxShaderStageFlags.Compute,
            _ => DxShaderStageFlags.None
        };
    }

    private void ClearRecordedCommandResources()
    {
        foreach (var command in _commands)
        {
            command.BindingSnapshot?.Dispose();
        }

        _commands.Clear();
    }

    private void ExecuteGpuBackedCommands(ProGPU.Backend.WgpuContext context)
    {
        foreach (var command in _commands)
        {
            switch (command.Kind)
            {
                case ProGpuDirectXCommandKind.ClearRenderTarget:
                    ExecuteGpuBackedClearCommand(context, command);
                    break;
                case ProGpuDirectXCommandKind.ClearDepthStencil:
                    ExecuteGpuBackedClearDepthStencilCommand(context, command);
                    break;
                case ProGpuDirectXCommandKind.CopyTexture:
                    ExecuteGpuBackedCopyTextureCommand(command);
                    break;
                case ProGpuDirectXCommandKind.CopyBuffer:
                    ExecuteGpuBackedCopyBufferCommand(context, command);
                    break;
                case ProGpuDirectXCommandKind.ResolveTexture:
                    ExecuteGpuBackedResolveTextureCommand(context, command);
                    break;
                case ProGpuDirectXCommandKind.Draw:
                case ProGpuDirectXCommandKind.DrawIndexed:
                    ExecuteGpuBackedDrawCommand(context, command);
                    break;
                case ProGpuDirectXCommandKind.Dispatch:
                    ExecuteGpuBackedDispatchCommand(context, command);
                    break;
            }
        }
    }

    private void ExecuteGpuBackedClearCommand(ProGPU.Backend.WgpuContext context, ProGpuDirectXCommand command)
    {
        if (command.Texture?.BackendTexture is not { IsDisposed: false, ViewPtr: not null } texture)
        {
            return;
        }

        var labelPtr = SilkMarshal.StringToPtr("ProGPU DirectX Clear Encoder");
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)labelPtr };
            encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
            if (encoder == null)
            {
                return;
            }

            var clearColor = command.Color;
            var colorAttachment = new RenderPassColorAttachment
            {
                View = texture.ViewPtr,
                ResolveTarget = null,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = new Silk.NET.WebGPU.Color
                {
                    R = clearColor.R,
                    G = clearColor.G,
                    B = clearColor.B,
                    A = clearColor.A
                }
            };

            var passDesc = new RenderPassDescriptor
            {
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment
            };

            pass = context.Wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);
            if (pass != null)
            {
                context.Wgpu.RenderPassEncoderEnd(pass);
                context.Wgpu.RenderPassEncoderRelease(pass);
                pass = null;
            }

            var commandBufferDesc = new CommandBufferDescriptor();
            commandBuffer = context.Wgpu.CommandEncoderFinish(encoder, &commandBufferDesc);
            if (commandBuffer != null)
            {
                context.Wgpu.QueueSubmit(context.Queue, 1, &commandBuffer);
                SubmittedClearCount++;
            }
        }
        finally
        {
            if (commandBuffer != null)
            {
                context.Wgpu.CommandBufferRelease(commandBuffer);
            }

            if (pass != null)
            {
                context.Wgpu.RenderPassEncoderRelease(pass);
            }

            if (encoder != null)
            {
                context.Wgpu.CommandEncoderRelease(encoder);
            }

            SilkMarshal.Free(labelPtr);
        }
    }

    private void ExecuteGpuBackedCopyBufferCommand(ProGPU.Backend.WgpuContext context, ProGpuDirectXCommand command)
    {
        if (command.SourceBuffer?.BackendBuffer is not { BufferPtr: not null } source ||
            command.DestinationBuffer?.BackendBuffer is not { BufferPtr: not null } destination)
        {
            return;
        }

        var labelPtr = SilkMarshal.StringToPtr("ProGPU DirectX CopyBuffer Encoder");
        CommandEncoder* encoder = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)labelPtr };
            encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
            if (encoder == null)
            {
                return;
            }

            context.Wgpu.CommandEncoderCopyBufferToBuffer(
                encoder,
                source.BufferPtr,
                0,
                destination.BufferPtr,
                0,
                source.Size);

            var commandBufferDesc = new CommandBufferDescriptor();
            commandBuffer = context.Wgpu.CommandEncoderFinish(encoder, &commandBufferDesc);
            if (commandBuffer != null)
            {
                context.Wgpu.QueueSubmit(context.Queue, 1, &commandBuffer);
                SubmittedCopyCount++;
            }
        }
        finally
        {
            if (commandBuffer != null)
            {
                context.Wgpu.CommandBufferRelease(commandBuffer);
            }

            if (encoder != null)
            {
                context.Wgpu.CommandEncoderRelease(encoder);
            }

            SilkMarshal.Free(labelPtr);
        }
    }

    private void ExecuteGpuBackedCopyTextureCommand(ProGpuDirectXCommand command)
    {
        var sourceResource = command.SourceTexture;
        var destinationResource = command.DestinationTexture;
        if (sourceResource?.BackendTexture is not { IsDisposed: false } source ||
            destinationResource?.BackendTexture is not { IsDisposed: false } destination)
        {
            return;
        }

        destination.CopyFrom(source);
        destinationResource.MarkBackendContentsChanged();
        SubmittedCopyCount++;
    }

    private void ExecuteGpuBackedResolveTextureCommand(ProGPU.Backend.WgpuContext context, ProGpuDirectXCommand command)
    {
        var destinationResource = command.DestinationTexture;
        if (command.SourceTexture?.BackendTexture is not { IsDisposed: false, ViewPtr: not null } source ||
            destinationResource?.BackendTexture is not { IsDisposed: false, ViewPtr: not null } destination)
        {
            return;
        }

        var labelPtr = SilkMarshal.StringToPtr("ProGPU DirectX ResolveTexture Encoder");
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)labelPtr };
            encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
            if (encoder == null)
            {
                return;
            }

            var colorAttachment = new RenderPassColorAttachment
            {
                View = source.ViewPtr,
                ResolveTarget = destination.ViewPtr,
                LoadOp = LoadOp.Load,
                StoreOp = StoreOp.Store,
                ClearValue = new Silk.NET.WebGPU.Color()
            };

            var passDesc = new RenderPassDescriptor
            {
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment
            };

            pass = context.Wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);
            if (pass == null)
            {
                return;
            }

            context.Wgpu.RenderPassEncoderEnd(pass);
            context.Wgpu.RenderPassEncoderRelease(pass);
            pass = null;

            var commandBufferDesc = new CommandBufferDescriptor();
            commandBuffer = context.Wgpu.CommandEncoderFinish(encoder, &commandBufferDesc);
            if (commandBuffer != null)
            {
                context.Wgpu.QueueSubmit(context.Queue, 1, &commandBuffer);
                destinationResource.MarkBackendContentsChanged();
                SubmittedResolveCount++;
            }
        }
        finally
        {
            if (commandBuffer != null)
            {
                context.Wgpu.CommandBufferRelease(commandBuffer);
            }

            if (pass != null)
            {
                context.Wgpu.RenderPassEncoderRelease(pass);
            }

            if (encoder != null)
            {
                context.Wgpu.CommandEncoderRelease(encoder);
            }

            SilkMarshal.Free(labelPtr);
        }
    }

    private void ExecuteGpuBackedClearDepthStencilCommand(ProGPU.Backend.WgpuContext context, ProGpuDirectXCommand command)
    {
        if (command.Texture?.BackendTexture is not { IsDisposed: false, ViewPtr: not null } texture)
        {
            return;
        }

        var clearDepth = (command.DepthStencilClearFlags & DxDepthStencilClearFlags.Depth) != 0;
        var clearStencil = (command.DepthStencilClearFlags & DxDepthStencilClearFlags.Stencil) != 0;
        var labelPtr = SilkMarshal.StringToPtr("ProGPU DirectX ClearDepthStencil Encoder");
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)labelPtr };
            encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
            if (encoder == null)
            {
                return;
            }

            var depthAttachment = new RenderPassDepthStencilAttachment
            {
                View = texture.ViewPtr,
                DepthLoadOp = clearDepth ? LoadOp.Clear : LoadOp.Load,
                DepthStoreOp = StoreOp.Store,
                DepthClearValue = command.Color.R,
                DepthReadOnly = false,
                StencilLoadOp = clearStencil ? LoadOp.Clear : LoadOp.Load,
                StencilStoreOp = StoreOp.Store,
                StencilClearValue = checked((uint)Math.Clamp(command.Color.G * 255f, 0f, 255f)),
                StencilReadOnly = false
            };

            var passDesc = new RenderPassDescriptor
            {
                ColorAttachmentCount = 0,
                ColorAttachments = null,
                DepthStencilAttachment = &depthAttachment
            };

            pass = context.Wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);
            if (pass != null)
            {
                context.Wgpu.RenderPassEncoderEnd(pass);
                context.Wgpu.RenderPassEncoderRelease(pass);
                pass = null;
            }

            var commandBufferDesc = new CommandBufferDescriptor();
            commandBuffer = context.Wgpu.CommandEncoderFinish(encoder, &commandBufferDesc);
            if (commandBuffer != null)
            {
                context.Wgpu.QueueSubmit(context.Queue, 1, &commandBuffer);
                SubmittedClearCount++;
            }
        }
        finally
        {
            if (commandBuffer != null)
            {
                context.Wgpu.CommandBufferRelease(commandBuffer);
            }

            if (pass != null)
            {
                context.Wgpu.RenderPassEncoderRelease(pass);
            }

            if (encoder != null)
            {
                context.Wgpu.CommandEncoderRelease(encoder);
            }

            SilkMarshal.Free(labelPtr);
        }
    }

    private void ExecuteGpuBackedDrawCommand(ProGPU.Backend.WgpuContext context, ProGpuDirectXCommand command)
    {
        if (command.Texture?.BackendTexture is not { IsDisposed: false, ViewPtr: not null } renderTarget)
        {
            throw new InvalidOperationException("GPU-backed DirectX draw requires a render target with a backend texture view.");
        }

        if (command.GraphicsPipeline is not { HasBackendPipeline: true } pipeline)
        {
            throw new InvalidOperationException("GPU-backed DirectX draw requires a backend graphics pipeline.");
        }

        RenderPassDepthStencilAttachment depthAttachment = default;
        var depthTexture = command.DepthStencilTexture?.BackendTexture;
        var hasDepthAttachment = depthTexture is { IsDisposed: false, ViewPtr: not null };
        if (command.GraphicsPipeline.Descriptor.DepthStencilFormat != DxResourceFormat.Unknown &&
            (command.GraphicsPipeline.Descriptor.DepthStencilState.DepthEnable ||
                command.GraphicsPipeline.Descriptor.DepthStencilState.StencilEnable) &&
            !hasDepthAttachment)
        {
            throw new InvalidOperationException("GPU-backed DirectX draw requires a backend depth-stencil texture for depth-enabled pipelines.");
        }

        if (hasDepthAttachment)
        {
            depthAttachment = new RenderPassDepthStencilAttachment
            {
                View = depthTexture!.ViewPtr,
                DepthLoadOp = LoadOp.Load,
                DepthStoreOp = StoreOp.Store,
                DepthClearValue = 1f,
                DepthReadOnly = false,
                StencilLoadOp = LoadOp.Load,
                StencilStoreOp = StoreOp.Store,
                StencilClearValue = 0,
                StencilReadOnly = false
            };
        }

        var labelPtr = SilkMarshal.StringToPtr("ProGPU DirectX Draw Encoder");
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)labelPtr };
            encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
            if (encoder == null)
            {
                return;
            }

            var colorAttachment = new RenderPassColorAttachment
            {
                View = renderTarget.ViewPtr,
                ResolveTarget = null,
                LoadOp = LoadOp.Load,
                StoreOp = StoreOp.Store,
                ClearValue = new Silk.NET.WebGPU.Color()
            };

            var passDesc = new RenderPassDescriptor
            {
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment,
                DepthStencilAttachment = hasDepthAttachment ? &depthAttachment : null
            };

            pass = context.Wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);
            if (pass == null)
            {
                return;
            }

            ApplyRenderState(context, pass, command, renderTarget.Width, renderTarget.Height);
            if (command.GraphicsPipeline.Descriptor.DepthStencilState.StencilEnable)
            {
                context.Wgpu.RenderPassEncoderSetStencilReference(
                    pass,
                    command.GraphicsPipeline.Descriptor.DepthStencilState.StencilReference);
            }

            if (command.VertexBufferBindings is { Count: > 0 } vertexBufferBindings)
            {
                foreach (var pair in vertexBufferBindings.OrderBy(pair => pair.Key))
                {
                    if (pair.Value.Buffer.BackendBuffer is not { BufferPtr: not null } buffer)
                    {
                        throw new InvalidOperationException("GPU-backed DirectX draw requires backend vertex buffers.");
                    }

                    var offsetBytes = pair.Value.OffsetBytes;
                    if (offsetBytes >= buffer.Size)
                    {
                        throw new InvalidOperationException("GPU-backed DirectX vertex-buffer binding offset is outside the backend buffer.");
                    }

                    context.Wgpu.RenderPassEncoderSetVertexBuffer(
                        pass,
                        pair.Key,
                        buffer.BufferPtr,
                        offsetBytes,
                        buffer.Size - offsetBytes);
                }
            }
            else if (command.VertexBuffers is { Count: > 0 } vertexBuffers)
            {
                foreach (var pair in vertexBuffers.OrderBy(pair => pair.Key))
                {
                    if (pair.Value.BackendBuffer is not { BufferPtr: not null } buffer)
                    {
                        throw new InvalidOperationException("GPU-backed DirectX draw requires backend vertex buffers.");
                    }

                    context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, pair.Key, buffer.BufferPtr, 0, buffer.Size);
                }
            }

            if (pipeline.UsesFragmentFrontFacingEmulation)
            {
                if (pipeline.FrontFacingFrontPipeline != null)
                {
                    ExecuteGpuBackedDrawPass(
                        context,
                        pass,
                        command,
                        pipeline.FrontFacingFrontPipeline);
                }

                if (pipeline.FrontFacingBackPipeline != null)
                {
                    ExecuteGpuBackedDrawPass(
                        context,
                        pass,
                        command,
                        pipeline.FrontFacingBackPipeline);
                }
            }
            else
            {
                ExecuteGpuBackedDrawPass(
                    context,
                    pass,
                    command,
                    pipeline.BackendPipeline);
            }

            context.Wgpu.RenderPassEncoderEnd(pass);
            context.Wgpu.RenderPassEncoderRelease(pass);
            pass = null;

            var commandBufferDesc = new CommandBufferDescriptor();
            commandBuffer = context.Wgpu.CommandEncoderFinish(encoder, &commandBufferDesc);
            if (commandBuffer != null)
            {
                context.Wgpu.QueueSubmit(context.Queue, 1, &commandBuffer);
                SubmittedDrawCount++;
            }
        }
        finally
        {
            if (commandBuffer != null)
            {
                context.Wgpu.CommandBufferRelease(commandBuffer);
            }

            if (pass != null)
            {
                context.Wgpu.RenderPassEncoderRelease(pass);
            }

            if (encoder != null)
            {
                context.Wgpu.CommandEncoderRelease(encoder);
            }

            SilkMarshal.Free(labelPtr);
        }
    }

    private void ExecuteGpuBackedDrawPass(
        ProGPU.Backend.WgpuContext context,
        RenderPassEncoder* pass,
        ProGpuDirectXCommand command,
        RenderPipeline* renderPipeline)
    {
        context.Wgpu.RenderPassEncoderSetPipeline(pass, renderPipeline);
        if (command.BindingSnapshot is { Entries.Count: > 0 } snapshot)
        {
            var pipelineBindGroup = GetOrCreateRenderPipelineBindGroup(
                context,
                renderPipeline,
                snapshot);
            context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, pipelineBindGroup, 0, null);
        }

        var isWireframeTriangleDraw = IsWireframeTriangleDraw(command);
        if (TryGetWireframeIndexBuffer(context, command, out var wireframeIndexBuffer, out var wireframeBaseVertex, out var wireframeInstanceCount, out var wireframeStartInstance))
        {
            context.Wgpu.RenderPassEncoderSetIndexBuffer(
                pass,
                wireframeIndexBuffer.Buffer.BufferPtr,
                IndexFormat.Uint32,
                0,
                wireframeIndexBuffer.Buffer.Size);

            context.Wgpu.RenderPassEncoderDrawIndexed(
                pass,
                wireframeIndexBuffer.IndexCount,
                wireframeInstanceCount,
                0,
                wireframeBaseVertex,
                wireframeStartInstance);

            SubmittedWireframeDrawCount++;
        }
        else if (isWireframeTriangleDraw)
        {
            // A triangle-list/strip wireframe draw with fewer than one source triangle has no edges.
        }
        else if (command.Kind == ProGpuDirectXCommandKind.DrawIndexed)
        {
            if (command.IndexBuffer?.BackendBuffer is not { BufferPtr: not null } indexBuffer ||
                command.DrawIndexed is null)
            {
                throw new InvalidOperationException("GPU-backed DirectX DrawIndexed requires a backend index buffer.");
            }

            context.Wgpu.RenderPassEncoderSetIndexBuffer(
                pass,
                indexBuffer.BufferPtr,
                ProGpuDirectXFormatConverter.ToIndexFormat(command.DrawIndexed.IndexFormat),
                0,
                indexBuffer.Size);

            context.Wgpu.RenderPassEncoderDrawIndexed(
                pass,
                command.DrawIndexed.IndexCount,
                command.DrawIndexed.InstanceCount,
                command.DrawIndexed.StartIndexLocation,
                command.DrawIndexed.BaseVertexLocation,
                command.DrawIndexed.StartInstanceLocation);
        }
        else if (command.Draw is { } draw)
        {
            context.Wgpu.RenderPassEncoderDraw(
                pass,
                draw.VertexCount,
                draw.InstanceCount,
                draw.StartVertexLocation,
                draw.StartInstanceLocation);
        }
    }

    private BindGroup* GetOrCreateRenderPipelineBindGroup(
        ProGPU.Backend.WgpuContext context,
        RenderPipeline* renderPipeline,
        ProGpuDirectXBindingSnapshot snapshot)
    {
        var key = new PipelineBindGroupCacheKey(
            IsCompute: false,
            Pipeline: (IntPtr)renderPipeline,
            BindingKey: snapshot.BackendBindingKey);

        if (_pipelineBindGroupCache.TryGetValue(key, out var cached))
        {
            DrawBindGroupCacheHitCount++;
            return cached.BindGroupPointer;
        }

        var pipelineBindGroupLayout = context.Wgpu.RenderPipelineGetBindGroupLayout(renderPipeline, 0);
        if (pipelineBindGroupLayout == null)
        {
            throw new InvalidOperationException("GPU-backed DirectX draw could not resolve the pipeline bind-group layout.");
        }

        try
        {
            var pipelineBindGroup = snapshot.CreateBackendBindGroupFromLayout(
                context,
                pipelineBindGroupLayout,
                "ProGPU DirectX Draw Pipeline Bindings");
            if (pipelineBindGroup == null)
            {
                throw new InvalidOperationException("GPU-backed DirectX draw could not create a pipeline-compatible bind group.");
            }

            DrawBindGroupCacheMissCount++;
            AddCachedPipelineBindGroup(context, key, pipelineBindGroup);
            return pipelineBindGroup;
        }
        finally
        {
            context.Wgpu.BindGroupLayoutRelease(pipelineBindGroupLayout);
        }
    }

    private static void ApplyRenderState(
        ProGPU.Backend.WgpuContext context,
        RenderPassEncoder* pass,
        ProGpuDirectXCommand command,
        uint targetWidth,
        uint targetHeight)
    {
        var viewport = command.Viewport.Width > 0 && command.Viewport.Height > 0
            ? command.Viewport
            : new DxViewport(0, 0, targetWidth, targetHeight);

        context.Wgpu.RenderPassEncoderSetViewport(
            pass,
            viewport.X,
            viewport.Y,
            viewport.Width,
            viewport.Height,
            viewport.MinDepth,
            viewport.MaxDepth);

        if (command.Rect.Width > 0 && command.Rect.Height > 0)
        {
            context.Wgpu.RenderPassEncoderSetScissorRect(
                pass,
                checked((uint)Math.Max(0, command.Rect.X)),
                checked((uint)Math.Max(0, command.Rect.Y)),
                checked((uint)command.Rect.Width),
                checked((uint)command.Rect.Height));
        }
        else
        {
            context.Wgpu.RenderPassEncoderSetScissorRect(pass, 0, 0, targetWidth, targetHeight);
        }
    }

    private bool TryGetWireframeIndexBuffer(
        ProGPU.Backend.WgpuContext context,
        ProGpuDirectXCommand command,
        out WireframeIndexCacheEntry entry,
        out int baseVertexLocation,
        out uint instanceCount,
        out uint startInstanceLocation)
    {
        entry = null!;
        baseVertexLocation = 0;
        instanceCount = 1;
        startInstanceLocation = 0;

        if (command.GraphicsPipeline?.Descriptor is not { } descriptor ||
            descriptor.RasterizerState.FillMode != DxFillMode.Wireframe ||
            descriptor.Topology is not (DxPrimitiveTopology.TriangleList or DxPrimitiveTopology.TriangleStrip))
        {
            return false;
        }

        uint[] lineIndices;
        WireframeIndexCacheKey key;
        if (command.Draw is { } draw)
        {
            key = new WireframeIndexCacheKey(
                SourceIndexBuffer: null,
                SourceGeneration: 0,
                IsIndexed: false,
                Topology: descriptor.Topology,
                SourceIndexFormat: DxIndexFormat.UInt32,
                Count: draw.VertexCount,
                StartLocation: 0);
            baseVertexLocation = checked((int)draw.StartVertexLocation);
            instanceCount = draw.InstanceCount;
            startInstanceLocation = draw.StartInstanceLocation;
            if (_wireframeIndexBuffers.TryGetValue(key, out entry!))
            {
                return true;
            }

            lineIndices = CreateWireframeLineIndicesForSequentialVertices(descriptor.Topology, draw.VertexCount);
            if (lineIndices.Length == 0)
            {
                return false;
            }
        }
        else if (command.DrawIndexed is { } drawIndexed)
        {
            if (command.IndexBuffer is not { } sourceIndexBuffer)
            {
                throw new InvalidOperationException("GPU-backed DirectX wireframe DrawIndexed requires an index buffer.");
            }

            key = new WireframeIndexCacheKey(
                SourceIndexBuffer: sourceIndexBuffer,
                SourceGeneration: sourceIndexBuffer.Generation,
                IsIndexed: true,
                Topology: descriptor.Topology,
                SourceIndexFormat: drawIndexed.IndexFormat,
                Count: drawIndexed.IndexCount,
                StartLocation: drawIndexed.StartIndexLocation);
            baseVertexLocation = drawIndexed.BaseVertexLocation;
            instanceCount = drawIndexed.InstanceCount;
            startInstanceLocation = drawIndexed.StartInstanceLocation;
            if (_wireframeIndexBuffers.TryGetValue(key, out entry!))
            {
                return true;
            }

            var sourceIndices = ReadSourceIndices(
                sourceIndexBuffer,
                drawIndexed.IndexFormat,
                drawIndexed.StartIndexLocation,
                drawIndexed.IndexCount);
            lineIndices = CreateWireframeLineIndices(descriptor.Topology, sourceIndices);
            if (lineIndices.Length == 0)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        var buffer = new ProGPU.Backend.GpuBuffer(
            context,
            checked((uint)(lineIndices.Length * sizeof(uint))),
            BufferUsage.Index | BufferUsage.CopyDst,
            "ProGPU DirectX Wireframe Index Buffer");
        buffer.Write(lineIndices);
        entry = new WireframeIndexCacheEntry(buffer, checked((uint)lineIndices.Length));
        _wireframeIndexBuffers.Add(key, entry);

        return true;
    }

    private static bool IsWireframeTriangleDraw(ProGpuDirectXCommand command)
    {
        return command.GraphicsPipeline?.Descriptor is { } descriptor &&
            descriptor.RasterizerState.FillMode == DxFillMode.Wireframe &&
            descriptor.Topology is DxPrimitiveTopology.TriangleList or DxPrimitiveTopology.TriangleStrip;
    }

    private static uint[] ReadSourceIndices(
        ProGpuDirectXBuffer sourceIndexBuffer,
        DxIndexFormat format,
        uint startIndexLocation,
        uint indexCount)
    {
        var bytesPerIndex = format == DxIndexFormat.UInt16 ? 2u : 4u;
        var bytes = sourceIndexBuffer.ReadWriteShadowBytes(
            checked(startIndexLocation * bytesPerIndex),
            checked(indexCount * bytesPerIndex));

        if (format == DxIndexFormat.UInt16)
        {
            var source = MemoryMarshal.Cast<byte, ushort>(bytes);
            var result = new uint[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                result[i] = source[i];
            }

            return result;
        }

        return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    private static uint[] CreateWireframeLineIndicesForSequentialVertices(DxPrimitiveTopology topology, uint vertexCount)
    {
        if (vertexCount == 0)
        {
            return [];
        }

        var indices = new uint[vertexCount];
        for (uint i = 0; i < vertexCount; i++)
        {
            indices[i] = i;
        }

        return CreateWireframeLineIndices(topology, indices);
    }

    private static uint[] CreateWireframeLineIndices(DxPrimitiveTopology topology, ReadOnlySpan<uint> sourceIndices)
    {
        var triangleCount = topology switch
        {
            DxPrimitiveTopology.TriangleList => sourceIndices.Length / 3,
            DxPrimitiveTopology.TriangleStrip => Math.Max(0, sourceIndices.Length - 2),
            _ => 0
        };
        if (triangleCount == 0)
        {
            return [];
        }

        var lineIndices = new uint[checked(triangleCount * 6)];
        var write = 0;
        if (topology == DxPrimitiveTopology.TriangleList)
        {
            for (var i = 0; i + 2 < sourceIndices.Length; i += 3)
            {
                WriteTriangleEdges(lineIndices, ref write, sourceIndices[i], sourceIndices[i + 1], sourceIndices[i + 2]);
            }
        }
        else
        {
            for (var i = 0; i + 2 < sourceIndices.Length; i++)
            {
                WriteTriangleEdges(lineIndices, ref write, sourceIndices[i], sourceIndices[i + 1], sourceIndices[i + 2]);
            }
        }

        return lineIndices;
    }

    private static void WriteTriangleEdges(uint[] destination, ref int offset, uint a, uint b, uint c)
    {
        destination[offset++] = a;
        destination[offset++] = b;
        destination[offset++] = b;
        destination[offset++] = c;
        destination[offset++] = c;
        destination[offset++] = a;
    }

    private void ExecuteGpuBackedDispatchCommand(ProGPU.Backend.WgpuContext context, ProGpuDirectXCommand command)
    {
        if (command.ComputePipeline is not { BackendPipeline: not null } pipeline)
        {
            throw new InvalidOperationException("GPU-backed DirectX dispatch requires a backend compute pipeline.");
        }

        if (command.Dispatch is not { } dispatch)
        {
            return;
        }

        var labelPtr = SilkMarshal.StringToPtr("ProGPU DirectX Dispatch Encoder");
        CommandEncoder* encoder = null;
        ComputePassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)labelPtr };
            encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
            if (encoder == null)
            {
                return;
            }

            var passDesc = new ComputePassDescriptor();
            pass = context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
            if (pass == null)
            {
                return;
            }

            context.Wgpu.ComputePassEncoderSetPipeline(pass, pipeline.BackendPipeline);
            if (command.BindingSnapshot is { Entries.Count: > 0 } snapshot)
            {
                var pipelineBindGroup = GetOrCreateComputePipelineBindGroup(
                    context,
                    pipeline.BackendPipeline,
                    snapshot);
                context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, pipelineBindGroup, 0, null);
            }

            context.Wgpu.ComputePassEncoderDispatchWorkgroups(
                pass,
                dispatch.ThreadGroupCountX,
                dispatch.ThreadGroupCountY,
                dispatch.ThreadGroupCountZ);

            context.Wgpu.ComputePassEncoderEnd(pass);
            context.Wgpu.ComputePassEncoderRelease(pass);
            pass = null;

            var commandBufferDesc = new CommandBufferDescriptor();
            commandBuffer = context.Wgpu.CommandEncoderFinish(encoder, &commandBufferDesc);
            if (commandBuffer != null)
            {
                context.Wgpu.QueueSubmit(context.Queue, 1, &commandBuffer);
                SubmittedDispatchCount++;
            }
        }
        finally
        {
            if (commandBuffer != null)
            {
                context.Wgpu.CommandBufferRelease(commandBuffer);
            }

            if (pass != null)
            {
                context.Wgpu.ComputePassEncoderRelease(pass);
            }

            if (encoder != null)
            {
                context.Wgpu.CommandEncoderRelease(encoder);
            }

            SilkMarshal.Free(labelPtr);
        }
    }

    private BindGroup* GetOrCreateComputePipelineBindGroup(
        ProGPU.Backend.WgpuContext context,
        ComputePipeline* computePipeline,
        ProGpuDirectXBindingSnapshot snapshot)
    {
        var key = new PipelineBindGroupCacheKey(
            IsCompute: true,
            Pipeline: (IntPtr)computePipeline,
            BindingKey: snapshot.BackendBindingKey);

        if (_pipelineBindGroupCache.TryGetValue(key, out var cached))
        {
            DispatchBindGroupCacheHitCount++;
            return cached.BindGroupPointer;
        }

        var pipelineBindGroupLayout = context.Wgpu.ComputePipelineGetBindGroupLayout(computePipeline, 0);
        if (pipelineBindGroupLayout == null)
        {
            throw new InvalidOperationException("GPU-backed DirectX dispatch could not resolve the pipeline bind-group layout.");
        }

        try
        {
            var pipelineBindGroup = snapshot.CreateBackendBindGroupFromLayout(
                context,
                pipelineBindGroupLayout,
                "ProGPU DirectX Dispatch Pipeline Bindings");
            if (pipelineBindGroup == null)
            {
                throw new InvalidOperationException("GPU-backed DirectX dispatch could not create a pipeline-compatible bind group.");
            }

            DispatchBindGroupCacheMissCount++;
            AddCachedPipelineBindGroup(context, key, pipelineBindGroup);
            return pipelineBindGroup;
        }
        finally
        {
            context.Wgpu.BindGroupLayoutRelease(pipelineBindGroupLayout);
        }
    }

    private void AddCachedPipelineBindGroup(
        ProGPU.Backend.WgpuContext context,
        PipelineBindGroupCacheKey key,
        BindGroup* bindGroup)
    {
        _pipelineBindGroupCache[key] = new CachedPipelineBindGroup(bindGroup);
        if (_pipelineBindGroupCache.Count <= MaxPipelineBindGroupCacheEntries)
        {
            return;
        }

        PipelineBindGroupCacheKey? keyToEvict = null;
        foreach (var candidate in _pipelineBindGroupCache.Keys)
        {
            if (!candidate.Equals(key))
            {
                keyToEvict = candidate;
                break;
            }
        }

        if (keyToEvict is { } evictedKey &&
            _pipelineBindGroupCache.Remove(evictedKey, out var evicted))
        {
            evicted.QueueDisposal(context);
        }
    }

    private void ClearCachedPipelineBindGroupsCore()
    {
        var context = _device.Context;
        foreach (var cached in _pipelineBindGroupCache.Values)
        {
            cached.QueueDisposal(context);
        }

        _pipelineBindGroupCache.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ProGpuDirectXDeviceContext));
        }
    }

    private static void ValidateShaderStage(ProGpuDirectXShader? shader, DxShaderStage stage)
    {
        if (shader is not null && shader.Descriptor.Stage != stage)
        {
            throw new ArgumentException($"Expected a {stage} shader.", nameof(shader));
        }
    }

    private static void ValidateTextureCopy(ProGpuDirectXTexture2D destination, ProGpuDirectXTexture2D source)
    {
        if (destination.Width != source.Width || destination.Height != source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), "Texture copies require matching dimensions.");
        }

        if (destination.Descriptor.Format != source.Descriptor.Format)
        {
            throw new ArgumentException("Texture copies require matching resource formats.", nameof(destination));
        }

        if ((destination.Descriptor.Usage & DxTextureUsage.CopyDestination) == 0)
        {
            throw new ArgumentException("Destination texture was not created with copy-destination usage.", nameof(destination));
        }

        if ((source.Descriptor.Usage & DxTextureUsage.CopySource) == 0)
        {
            throw new ArgumentException("Source texture was not created with copy-source usage.", nameof(source));
        }
    }

    private static void ValidateTextureResolve(
        ProGpuDirectXTexture2D destination,
        ProGpuDirectXTexture2D source,
        uint destinationSubresource,
        uint sourceSubresource,
        DxResourceFormat format)
    {
        if (destinationSubresource != 0 || sourceSubresource != 0)
        {
            throw new NotSupportedException("DirectX texture resolve currently supports only subresource 0.");
        }

        if (destination.Width != source.Width || destination.Height != source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), "Texture resolves require matching dimensions.");
        }

        if (destination.Descriptor.Format != source.Descriptor.Format)
        {
            throw new ArgumentException("Texture resolves require matching resource formats.", nameof(destination));
        }

        if (format != DxResourceFormat.Unknown && format != source.Descriptor.Format)
        {
            throw new ArgumentException("Texture resolve format must match the source resource format.", nameof(format));
        }

        if (source.Descriptor.SampleCount <= 1)
        {
            throw new ArgumentException("Texture resolve source must be multisampled.", nameof(source));
        }

        if (destination.Descriptor.SampleCount != 1)
        {
            throw new ArgumentException("Texture resolve destination must be single-sampled.", nameof(destination));
        }

        if ((source.Descriptor.Usage & DxTextureUsage.RenderTarget) == 0)
        {
            throw new ArgumentException("Texture resolve source must be a render-target texture.", nameof(source));
        }

        if ((destination.Descriptor.Usage & DxTextureUsage.RenderTarget) == 0)
        {
            throw new ArgumentException("Texture resolve destination must be a render-target texture.", nameof(destination));
        }

        if (source.Descriptor.Format is DxResourceFormat.D24UnormS8UInt or DxResourceFormat.D32Float)
        {
            throw new ArgumentException("Texture resolve currently supports color render-target formats only.", nameof(source));
        }
    }

    private static void ValidateDepthStencilTexture(ProGpuDirectXTexture2D depthStencil)
    {
        if ((depthStencil.Descriptor.Usage & DxTextureUsage.DepthStencil) == 0)
        {
            throw new ArgumentException("Depth-stencil clear requires a texture created with depth-stencil usage.", nameof(depthStencil));
        }

        if (depthStencil.Descriptor.Format is not (DxResourceFormat.D24UnormS8UInt or DxResourceFormat.D32Float))
        {
            throw new ArgumentException("Depth-stencil clear requires a depth-stencil resource format.", nameof(depthStencil));
        }
    }

    public void Dispose()
    {
        ClearRecordedCommandResources();
        ClearCachedPipelineBindGroupsCore();
        _vertexBuffers.Clear();
        _indexBuffer = null;
        _constantBuffers.Clear();
        _shaderResourceViews.Clear();
        _samplers.Clear();
        _unorderedAccessViews.Clear();
        foreach (var entry in _wireframeIndexBuffers.Values)
        {
            entry.Dispose();
        }

        _wireframeIndexBuffers.Clear();
        _isDisposed = true;
    }
}
