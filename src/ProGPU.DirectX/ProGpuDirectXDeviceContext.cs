using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

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
    SetShaderResource,
    SetSampler,
    SetUnorderedAccessView,
    CopyTexture,
    CopyBuffer,
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
    public ProGpuDirectXShaderResourceView? ShaderResourceView { get; init; }
    public ProGpuDirectXSamplerState? Sampler { get; init; }
    public ProGpuDirectXUnorderedAccessView? UnorderedAccessView { get; init; }
    public DxBlendStateDescriptor? BlendState { get; init; }
    public DxDepthStencilStateDescriptor? DepthStencilState { get; init; }
    public DxRasterizerStateDescriptor? RasterizerState { get; init; }
    public DxShaderResourceBinding? ResourceBinding { get; init; }
    public ProGpuDirectXTexture2D? SourceTexture { get; init; }
    public ProGpuDirectXTexture2D? DestinationTexture { get; init; }
    public ProGpuDirectXBuffer? SourceBuffer { get; init; }
    public ProGpuDirectXBuffer? DestinationBuffer { get; init; }
    public DxCopyResourceCall? Copy { get; init; }
}

public sealed unsafe class ProGpuDirectXDeviceContext : IDisposable
{
    private readonly ProGpuDirectXDevice _device;
    private readonly List<ProGpuDirectXCommand> _commands = new();
    private ProGpuDirectXTexture2D? _renderTarget;
    private ProGpuDirectXTexture2D? _depthStencil;
    private DxPrimitiveTopology _topology = DxPrimitiveTopology.TriangleList;
    private ProGpuDirectXInputLayout? _inputLayout;
    private ProGpuDirectXShader? _vertexShader;
    private ProGpuDirectXShader? _pixelShader;
    private ProGpuDirectXShader? _geometryShader;
    private ProGpuDirectXShader? _computeShader;
    private ProGpuDirectXGraphicsPipeline? _graphicsPipeline;
    private ProGpuDirectXComputePipeline? _computePipeline;
    private readonly Dictionary<DxShaderResourceBinding, ProGpuDirectXShaderResourceView?> _shaderResourceViews = new();
    private readonly Dictionary<DxShaderResourceBinding, ProGpuDirectXSamplerState?> _samplers = new();
    private readonly Dictionary<uint, ProGpuDirectXUnorderedAccessView?> _unorderedAccessViews = new();
    private bool _isDisposed;

    internal ProGpuDirectXDeviceContext(ProGpuDirectXDevice device)
    {
        _device = device;
    }

    public IReadOnlyList<ProGpuDirectXCommand> Commands => _commands;

    public ProGpuDirectXTexture2D? RenderTarget => _renderTarget;

    public ProGpuDirectXTexture2D? DepthStencil => _depthStencil;

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

    public IReadOnlyDictionary<uint, ProGpuDirectXUnorderedAccessView?> UnorderedAccessViews => _unorderedAccessViews;

    public void SetRenderTargets(ProGpuDirectXTexture2D? renderTarget, ProGpuDirectXTexture2D? depthStencil = null)
    {
        ThrowIfDisposed();
        _renderTarget = renderTarget;
        _depthStencil = depthStencil;
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetRenderTargets,
            Texture = renderTarget
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
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetVertexBuffer,
            Buffer = buffer
        });
    }

    public void SetIndexBuffer(ProGpuDirectXBuffer buffer, DxIndexFormat indexFormat = DxIndexFormat.UInt32)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetIndexBuffer,
            Buffer = buffer
        });
    }

    public void SetConstantBuffer(uint slot, ProGpuDirectXBuffer buffer)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.SetConstantBuffer,
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
        _inputLayout = pipeline.Descriptor.InputLayout;
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

    public void CopyResource(ProGpuDirectXTexture2D destination, ProGpuDirectXTexture2D source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ValidateTextureCopy(destination, source);

        if (destination.BackendTexture is { IsDisposed: false } destinationTexture &&
            source.BackendTexture is { IsDisposed: false } sourceTexture)
        {
            destinationTexture.CopyFrom(sourceTexture);
        }

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

        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.CopyBuffer,
            DestinationBuffer = destination,
            SourceBuffer = source,
            Copy = new DxCopyResourceCall("Buffer")
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

    public void ClearDepthStencil(ProGpuDirectXTexture2D depthStencil, float depth = 1f, byte stencil = 0)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(depthStencil);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.ClearDepthStencil,
            Texture = depthStencil,
            Color = new DxColor(depth, stencil / 255f, 0f, 0f)
        });
    }

    public void Draw(uint vertexCount, uint startVertexLocation = 0, uint instanceCount = 1, uint startInstanceLocation = 0)
    {
        ThrowIfDisposed();
        var draw = new DxDrawCall(_topology, vertexCount, startVertexLocation, instanceCount, startInstanceLocation);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.Draw,
            Topology = _topology,
            Draw = draw,
            GraphicsPipeline = _graphicsPipeline
        });
    }

    public void DrawIndexed(
        uint indexCount,
        uint startIndexLocation = 0,
        int baseVertexLocation = 0,
        uint instanceCount = 1,
        uint startInstanceLocation = 0,
        DxIndexFormat indexFormat = DxIndexFormat.UInt32)
    {
        ThrowIfDisposed();
        var draw = new DxDrawIndexedCall(
            _topology,
            indexCount,
            startIndexLocation,
            baseVertexLocation,
            instanceCount,
            startInstanceLocation,
            indexFormat);
        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.DrawIndexed,
            Topology = _topology,
            DrawIndexed = draw,
            GraphicsPipeline = _graphicsPipeline
        });
    }

    public void Dispatch(uint threadGroupCountX, uint threadGroupCountY, uint threadGroupCountZ)
    {
        ThrowIfDisposed();
        if (threadGroupCountX == 0 || threadGroupCountY == 0 || threadGroupCountZ == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threadGroupCountX), "Dispatch dimensions must be non-zero.");
        }

        _commands.Add(new ProGpuDirectXCommand
        {
            Kind = ProGpuDirectXCommandKind.Dispatch,
            Dispatch = new DxDispatchCall(threadGroupCountX, threadGroupCountY, threadGroupCountZ),
            ComputePipeline = _computePipeline
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
        _commands.Clear();
    }

    public void Flush(bool clearRecordedCommands = true)
    {
        ThrowIfDisposed();
        _device.ThrowIfDisposed();

        if (_device.Context is { } context && _device.IsGpuBacked)
        {
            ExecuteGpuBackedClearCommands(context);
            context.CleanupPendingResources();
        }

        if (clearRecordedCommands)
        {
            _commands.Clear();
        }
    }

    private void ExecuteGpuBackedClearCommands(ProGPU.Backend.WgpuContext context)
    {
        foreach (var command in _commands)
        {
            if (command.Kind != ProGpuDirectXCommandKind.ClearRenderTarget ||
                command.Texture?.BackendTexture is not { IsDisposed: false, ViewPtr: not null } texture)
            {
                continue;
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
                    continue;
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

    public void Dispose()
    {
        _commands.Clear();
        _shaderResourceViews.Clear();
        _samplers.Clear();
        _unorderedAccessViews.Clear();
        _isDisposed = true;
    }
}
