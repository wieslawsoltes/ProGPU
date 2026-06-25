using System.Security.Cryptography;
using System.Text;
using ProGPU.Backend;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.DirectX;

public sealed unsafe class ProGpuDirectXShader : IDisposable
{
    private readonly ProGpuDirectXDevice _device;
    private readonly IntPtr _backendShaderModule;
    private bool _isDisposed;

    internal ProGpuDirectXShader(ProGpuDirectXDevice device, DxShaderDescriptor descriptor)
    {
        _device = device;
        Descriptor = descriptor with { EntryPoint = ResolveEntryPoint(descriptor) };
        ValidateDescriptor(Descriptor);
        SourceHash = ComputeSourceHash(Descriptor);

        if (device.Context is { } context &&
            device.IsGpuBacked &&
            Descriptor.SourceKind == DxShaderSourceKind.Wgsl)
        {
            _backendShaderModule = (IntPtr)CreateWgslShaderModule(context, Descriptor);
        }
    }

    public DxShaderDescriptor Descriptor { get; }

    public string EntryPoint => Descriptor.EntryPoint!;

    public string SourceHash { get; }

    public bool HasBackendShaderModule => _backendShaderModule != IntPtr.Zero;

    public IntPtr BackendShaderModuleHandle => _backendShaderModule;

    internal ShaderModule* BackendShaderModule => (ShaderModule*)_backendShaderModule;

    private static ShaderModule* CreateWgslShaderModule(WgpuContext context, DxShaderDescriptor descriptor)
    {
        var sourcePtr = SilkMarshal.StringToPtr(descriptor.Source!);
        var labelPtr = SilkMarshal.StringToPtr(descriptor.Label);
        try
        {
            var wgslDesc = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct
                {
                    Next = null,
                    SType = SType.ShaderModuleWgslDescriptor
                },
                Code = (byte*)sourcePtr
            };

            var moduleDesc = new ShaderModuleDescriptor
            {
                NextInChain = (ChainedStruct*)&wgslDesc,
                Label = (byte*)labelPtr
            };

            var module = context.Wgpu.DeviceCreateShaderModule(context.Device, &moduleDesc);
            if (module == null)
            {
                throw new InvalidOperationException($"Failed to create DirectX shader module '{descriptor.Label}'.");
            }

            return module;
        }
        finally
        {
            SilkMarshal.Free(sourcePtr);
            SilkMarshal.Free(labelPtr);
        }
    }

    private static void ValidateDescriptor(DxShaderDescriptor descriptor)
    {
        if (descriptor.SourceKind is DxShaderSourceKind.Wgsl or DxShaderSourceKind.HlslText &&
            string.IsNullOrWhiteSpace(descriptor.Source))
        {
            throw new ArgumentException("Text shader descriptors must provide shader source.", nameof(descriptor));
        }

        if (descriptor.SourceKind == DxShaderSourceKind.HlslBytecode &&
            descriptor.Bytecode.IsEmpty)
        {
            throw new ArgumentException("Bytecode shader descriptors must provide shader bytecode.", nameof(descriptor));
        }
    }

    private static string ResolveEntryPoint(DxShaderDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.EntryPoint))
        {
            return descriptor.EntryPoint!;
        }

        return descriptor.Stage switch
        {
            DxShaderStage.Vertex => "vs_main",
            DxShaderStage.Pixel => "fs_main",
            DxShaderStage.Compute => "cs_main",
            _ => "main"
        };
    }

    private static string ComputeSourceHash(DxShaderDescriptor descriptor)
    {
        byte[] bytes = descriptor.SourceKind == DxShaderSourceKind.HlslBytecode
            ? descriptor.Bytecode.ToArray()
            : Encoding.UTF8.GetBytes(descriptor.Source ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_backendShaderModule != IntPtr.Zero &&
            _device.Context is { IsDisposed: false } context)
        {
            context.QueueShaderModuleDisposal(_backendShaderModule);
        }

        _isDisposed = true;
    }
}

public sealed class ProGpuDirectXInputLayout
{
    internal ProGpuDirectXInputLayout(DxInputLayoutDescriptor descriptor)
    {
        var elements = descriptor.Elements.ToArray();
        ValidateElements(elements);
        Descriptor = descriptor with { Elements = elements };
        Elements = elements;
        LayoutKey = CreateLayoutKey(elements);
    }

    public DxInputLayoutDescriptor Descriptor { get; }

    public IReadOnlyList<DxInputElementDescriptor> Elements { get; }

    public string LayoutKey { get; }

    public uint GetInferredStride(uint inputSlot)
    {
        uint stride = 0;
        foreach (var element in Elements)
        {
            if (element.InputSlot != inputSlot)
            {
                continue;
            }

            stride = Math.Max(
                stride,
                element.AlignedByteOffset + ProGpuDirectXFormatConverter.GetVertexFormatSizeInBytes(element.Format));
        }

        return stride;
    }

    private static void ValidateElements(IReadOnlyList<DxInputElementDescriptor> elements)
    {
        foreach (var element in elements)
        {
            if (string.IsNullOrWhiteSpace(element.SemanticName))
            {
                throw new ArgumentException("Input elements must provide a semantic name.", nameof(elements));
            }
        }
    }

    private static string CreateLayoutKey(IReadOnlyList<DxInputElementDescriptor> elements)
    {
        return string.Join(
            "|",
            elements.Select(
                (e, index) =>
                    $"{index}:{e.SemanticName}{e.SemanticIndex}:{e.Format}:{e.InputSlot}:{e.AlignedByteOffset}:{e.InputSlotClass}:{e.InstanceDataStepRate}:{e.ShaderLocation}"));
    }
}

public sealed unsafe class ProGpuDirectXGraphicsPipeline : IDisposable
{
    private readonly ProGpuDirectXDevice _device;
    private readonly IntPtr _backendPipeline;
    private bool _isDisposed;

    internal ProGpuDirectXGraphicsPipeline(ProGpuDirectXDevice device, DxGraphicsPipelineDescriptor descriptor)
    {
        _device = device;
        Descriptor = descriptor;
        ValidateDescriptor(descriptor);
        PipelineKey = CreatePipelineKey(descriptor);

        if (device.Context is { } context && device.IsGpuBacked)
        {
            _backendPipeline = (IntPtr)CreateBackendPipeline(context, descriptor, PipelineKey);
        }
    }

    public DxGraphicsPipelineDescriptor Descriptor { get; }

    public string PipelineKey { get; }

    public bool HasBackendPipeline => _backendPipeline != IntPtr.Zero;

    public IntPtr BackendPipelineHandle => _backendPipeline;

    internal RenderPipeline* BackendPipeline => (RenderPipeline*)_backendPipeline;

    private static RenderPipeline* CreateBackendPipeline(
        WgpuContext context,
        DxGraphicsPipelineDescriptor descriptor,
        string pipelineKey)
    {
        if (!descriptor.VertexShader.HasBackendShaderModule)
        {
            throw new InvalidOperationException("A GPU-backed DirectX graphics pipeline requires a WGSL-backed vertex shader.");
        }

        if (descriptor.PixelShader is { HasBackendShaderModule: false })
        {
            throw new InvalidOperationException("A GPU-backed DirectX graphics pipeline requires a WGSL-backed pixel shader when a pixel shader is supplied.");
        }

        if (descriptor.RasterizerState.FillMode == DxFillMode.Wireframe)
        {
            throw new NotSupportedException("GPU-backed wireframe rasterizer state requires a backend wireframe emulation path.");
        }

        var labelPtr = SilkMarshal.StringToPtr(descriptor.Label);
        var vsEntryPtr = SilkMarshal.StringToPtr(descriptor.VertexShader.EntryPoint);
        var fsEntryPtr = descriptor.PixelShader is null
            ? IntPtr.Zero
            : SilkMarshal.StringToPtr(descriptor.PixelShader.EntryPoint);

        try
        {
            var vertexElements = descriptor.InputLayout?.Elements ?? Array.Empty<DxInputElementDescriptor>();
            var inputSlots = vertexElements
                .Select(e => e.InputSlot)
                .Distinct()
                .OrderBy(slot => slot)
                .ToArray();

            var attributes = stackalloc VertexAttribute[vertexElements.Count];
            var layouts = stackalloc VertexBufferLayout[inputSlots.Length];
            var attributeIndex = 0;

            for (var slotIndex = 0; slotIndex < inputSlots.Length; slotIndex++)
            {
                var slot = inputSlots[slotIndex];
                var firstAttributeIndex = attributeIndex;
                uint stride = 0;
                DxInputClassification classification = DxInputClassification.PerVertexData;
                uint stepRate = 0;

                for (var elementIndex = 0; elementIndex < vertexElements.Count; elementIndex++)
                {
                    var element = vertexElements[elementIndex];
                    if (element.InputSlot != slot)
                    {
                        continue;
                    }

                    attributes[attributeIndex] = new VertexAttribute
                    {
                        Format = ProGpuDirectXFormatConverter.ToVertexFormat(element.Format),
                        Offset = element.AlignedByteOffset,
                        ShaderLocation = element.ShaderLocation ?? (uint)elementIndex
                    };
                    attributeIndex++;

                    stride = Math.Max(
                        stride,
                        element.AlignedByteOffset + ProGpuDirectXFormatConverter.GetVertexFormatSizeInBytes(element.Format));
                    classification = element.InputSlotClass;
                    stepRate = element.InstanceDataStepRate;
                }

                layouts[slotIndex] = new VertexBufferLayout
                {
                    ArrayStride = stride,
                    StepMode = ProGpuDirectXFormatConverter.ToVertexStepMode(classification),
                    AttributeCount = (uint)(attributeIndex - firstAttributeIndex),
                    Attributes = &attributes[firstAttributeIndex]
                };

                if (classification == DxInputClassification.PerInstanceData && stepRate > 1)
                {
                    throw new NotSupportedException("WebGPU supports per-instance step mode but not Direct3D-style instance step rates greater than one.");
                }
            }

            var vertexState = new VertexState
            {
                Module = descriptor.VertexShader.BackendShaderModule,
                EntryPoint = (byte*)vsEntryPtr,
                BufferCount = (uint)inputSlots.Length,
                Buffers = inputSlots.Length == 0 ? null : layouts
            };

            var blendComponentColor = new BlendComponent
            {
                SrcFactor = ProGpuDirectXFormatConverter.ToBlendFactor(descriptor.BlendState.SourceColor),
                DstFactor = ProGpuDirectXFormatConverter.ToBlendFactor(descriptor.BlendState.DestinationColor),
                Operation = ProGpuDirectXFormatConverter.ToBlendOperation(descriptor.BlendState.ColorOperation)
            };
            var blendComponentAlpha = new BlendComponent
            {
                SrcFactor = ProGpuDirectXFormatConverter.ToBlendFactor(descriptor.BlendState.SourceAlpha),
                DstFactor = ProGpuDirectXFormatConverter.ToBlendFactor(descriptor.BlendState.DestinationAlpha),
                Operation = ProGpuDirectXFormatConverter.ToBlendOperation(descriptor.BlendState.AlphaOperation)
            };
            var blendState = new BlendState
            {
                Color = blendComponentColor,
                Alpha = blendComponentAlpha
            };
            var colorTarget = new ColorTargetState
            {
                Format = ProGpuDirectXFormatConverter.ToTextureFormat(descriptor.RenderTargetFormat),
                Blend = descriptor.BlendState.EnableBlend ? &blendState : null,
                WriteMask = ProGpuDirectXFormatConverter.ToColorWriteMask(descriptor.BlendState.WriteMask)
            };

            FragmentState fragmentState = default;
            if (descriptor.PixelShader is not null)
            {
                fragmentState = new FragmentState
                {
                    Module = descriptor.PixelShader.BackendShaderModule,
                    EntryPoint = (byte*)fsEntryPtr,
                    TargetCount = 1,
                    Targets = &colorTarget
                };
            }

            var depthStencilEnabled = descriptor.DepthStencilFormat != DxResourceFormat.Unknown &&
                (descriptor.DepthStencilState.DepthEnable || descriptor.DepthStencilState.StencilEnable);
            var depthStencilState = new DepthStencilState
            {
                Format = ProGpuDirectXFormatConverter.ToTextureFormat(descriptor.DepthStencilFormat),
                DepthWriteEnabled = descriptor.DepthStencilState.DepthWriteMask == DxDepthWriteMask.All,
                DepthCompare = ProGpuDirectXFormatConverter.ToCompareFunction(descriptor.DepthStencilState.DepthFunction),
                StencilFront = new StencilFaceState(),
                StencilBack = new StencilFaceState(),
                StencilReadMask = 0xFF,
                StencilWriteMask = 0xFF,
                DepthBias = descriptor.RasterizerState.DepthBias,
                DepthBiasClamp = descriptor.RasterizerState.DepthBiasClamp,
                DepthBiasSlopeScale = descriptor.RasterizerState.SlopeScaledDepthBias
            };

            var pipelineDesc = new RenderPipelineDescriptor
            {
                Label = (byte*)labelPtr,
                Layout = null,
                Vertex = vertexState,
                Primitive = new PrimitiveState
                {
                    Topology = ProGpuDirectXFormatConverter.ToPrimitiveTopology(descriptor.Topology),
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = ProGpuDirectXFormatConverter.ToFrontFace(descriptor.RasterizerState.FrontFace),
                    CullMode = ProGpuDirectXFormatConverter.ToCullMode(descriptor.RasterizerState.CullMode)
                },
                DepthStencil = depthStencilEnabled ? &depthStencilState : null,
                Multisample = new MultisampleState
                {
                    Count = descriptor.SampleCount,
                    Mask = 0xFFFFFFFF,
                    AlphaToCoverageEnabled = false
                },
                Fragment = descriptor.PixelShader is null ? null : &fragmentState
            };

            var pipeline = context.Wgpu.DeviceCreateRenderPipeline(context.Device, &pipelineDesc);
            if (pipeline == null)
            {
                throw new InvalidOperationException($"Failed to create DirectX graphics pipeline '{pipelineKey}'.");
            }

            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(labelPtr);
            SilkMarshal.Free(vsEntryPtr);
            if (fsEntryPtr != IntPtr.Zero)
            {
                SilkMarshal.Free(fsEntryPtr);
            }
        }
    }

    private static void ValidateDescriptor(DxGraphicsPipelineDescriptor descriptor)
    {
        if (descriptor.VertexShader.Descriptor.Stage != DxShaderStage.Vertex)
        {
            throw new ArgumentException("Graphics pipelines require a vertex shader.", nameof(descriptor));
        }

        if (descriptor.PixelShader is not null &&
            descriptor.PixelShader.Descriptor.Stage != DxShaderStage.Pixel)
        {
            throw new ArgumentException("Graphics pipeline pixel shader must use the pixel shader stage.", nameof(descriptor));
        }

        if (descriptor.SampleCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Graphics pipelines must use at least one sample.");
        }
    }

    private static string CreatePipelineKey(DxGraphicsPipelineDescriptor descriptor)
    {
        return string.Join(
            "|",
            descriptor.VertexShader.SourceHash,
            descriptor.VertexShader.EntryPoint,
            descriptor.PixelShader?.SourceHash ?? "no-ps",
            descriptor.PixelShader?.EntryPoint ?? "no-ps",
            descriptor.InputLayout?.LayoutKey ?? "no-layout",
            descriptor.Topology,
            descriptor.RenderTargetFormat,
            descriptor.DepthStencilFormat,
            descriptor.SampleCount,
            descriptor.RasterizerState,
            descriptor.BlendState,
            descriptor.DepthStencilState);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_backendPipeline != IntPtr.Zero &&
            _device.Context is { IsDisposed: false } context)
        {
            context.QueueRenderPipelineDisposal(_backendPipeline);
        }

        _isDisposed = true;
    }
}

public sealed unsafe class ProGpuDirectXComputePipeline : IDisposable
{
    private readonly ProGpuDirectXDevice _device;
    private readonly IntPtr _backendPipeline;
    private bool _isDisposed;

    internal ProGpuDirectXComputePipeline(ProGpuDirectXDevice device, DxComputePipelineDescriptor descriptor)
    {
        _device = device;
        Descriptor = descriptor;
        if (descriptor.ComputeShader.Descriptor.Stage != DxShaderStage.Compute)
        {
            throw new ArgumentException("Compute pipelines require a compute shader.", nameof(descriptor));
        }

        PipelineKey = $"{descriptor.ComputeShader.SourceHash}|{descriptor.ComputeShader.EntryPoint}";
        if (device.Context is { } context && device.IsGpuBacked)
        {
            if (!descriptor.ComputeShader.HasBackendShaderModule)
            {
                throw new InvalidOperationException("A GPU-backed DirectX compute pipeline requires a WGSL-backed compute shader.");
            }

            _backendPipeline = (IntPtr)CreateBackendPipeline(context, descriptor);
        }
    }

    public DxComputePipelineDescriptor Descriptor { get; }

    public string PipelineKey { get; }

    public bool HasBackendPipeline => _backendPipeline != IntPtr.Zero;

    public IntPtr BackendPipelineHandle => _backendPipeline;

    internal ComputePipeline* BackendPipeline => (ComputePipeline*)_backendPipeline;

    private static ComputePipeline* CreateBackendPipeline(WgpuContext context, DxComputePipelineDescriptor descriptor)
    {
        var labelPtr = SilkMarshal.StringToPtr(descriptor.Label);
        var entryPtr = SilkMarshal.StringToPtr(descriptor.ComputeShader.EntryPoint);
        try
        {
            var pipelineDesc = new ComputePipelineDescriptor
            {
                Label = (byte*)labelPtr,
                Layout = null,
                Compute = new ProgrammableStageDescriptor
                {
                    Module = descriptor.ComputeShader.BackendShaderModule,
                    EntryPoint = (byte*)entryPtr
                }
            };

            var pipeline = context.Wgpu.DeviceCreateComputePipeline(context.Device, &pipelineDesc);
            if (pipeline == null)
            {
                throw new InvalidOperationException($"Failed to create DirectX compute pipeline '{descriptor.Label}'.");
            }

            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(labelPtr);
            SilkMarshal.Free(entryPtr);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_backendPipeline != IntPtr.Zero &&
            _device.Context is { IsDisposed: false } context)
        {
            context.QueueComputePipelineDisposal(_backendPipeline);
        }

        _isDisposed = true;
    }
}
