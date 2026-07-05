using System.Buffers;
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
        BytecodeInfo = Descriptor.SourceKind == DxShaderSourceKind.HlslBytecode
            ? ProGpuDirectXShaderBytecodeParser.Parse(Descriptor.Bytecode.Span)
            : null;
        ReflectedBindingRequirements = ResolveReflectedBindingRequirements(
            Descriptor,
            BytecodeInfo,
            out var reflectedBindingRequirementsSupported,
            out var reflectedBindingRequirementsFailureReason);
        ReflectedBindingRequirementsSupported = reflectedBindingRequirementsSupported;
        ReflectedBindingRequirementsFailureReason = reflectedBindingRequirementsFailureReason;
        BackendSource = ResolveBackendSource(Descriptor);

        if (device.Context is { } context &&
            device.IsGpuBacked &&
            BackendSource is not null)
        {
            _backendShaderModule = (IntPtr)CreateWgslShaderModule(context, Descriptor, BackendSource);
        }
    }

    public DxShaderDescriptor Descriptor { get; }

    public string EntryPoint => Descriptor.EntryPoint!;

    public string SourceHash { get; }

    public ProGpuDirectXShaderBytecodeInfo? BytecodeInfo { get; }

    public IReadOnlyList<DxReflectedShaderBindingRequirement> ReflectedBindingRequirements { get; }

    public bool HasReflectedBindingRequirements => ReflectedBindingRequirements.Count > 0;

    public bool ReflectedBindingRequirementsSupported { get; }

    public string? ReflectedBindingRequirementsFailureReason { get; }

    public string? BackendSource { get; }

    internal bool UsesRwByteAddressBufferInterlockedCompareExchange =>
        BackendSource?.Contains("atomicCompareExchangeWeak(", StringComparison.Ordinal) == true;

    internal bool UsesFragmentFrontFacingBuiltin =>
        Descriptor.Stage == DxShaderStage.Pixel &&
        BackendSource?.Contains("@builtin(front_facing)", StringComparison.Ordinal) == true;

    public bool HasBackendShaderModule => _backendShaderModule != IntPtr.Zero;

    public IntPtr BackendShaderModuleHandle => _backendShaderModule;

    internal ShaderModule* BackendShaderModule => (ShaderModule*)_backendShaderModule;

    internal static ShaderModule* CreateWgslShaderModule(
        WgpuContext context,
        DxShaderDescriptor descriptor,
        string source)
    {
        var sourcePtr = SilkMarshal.StringToPtr(source);
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
        return descriptor.SourceKind == DxShaderSourceKind.HlslBytecode
            ? Convert.ToHexString(SHA256.HashData(descriptor.Bytecode.Span))
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.Source ?? string.Empty)));
    }

    private static string? ResolveBackendSource(DxShaderDescriptor descriptor)
    {
        if (descriptor.SourceKind == DxShaderSourceKind.Wgsl)
        {
            return descriptor.Source;
        }

        return ProGpuDirectXHlslTranslator.TryTranslate(descriptor, out var wgsl)
            ? wgsl
            : null;
    }

    private static IReadOnlyList<DxReflectedShaderBindingRequirement> ResolveReflectedBindingRequirements(
        DxShaderDescriptor descriptor,
        ProGpuDirectXShaderBytecodeInfo? bytecodeInfo,
        out bool supported,
        out string? failureReason)
    {
        supported = true;
        failureReason = null;

        if (descriptor.SourceKind != DxShaderSourceKind.HlslBytecode || bytecodeInfo is null)
        {
            return Array.Empty<DxReflectedShaderBindingRequirement>();
        }

        if (!bytecodeInfo.IsValid)
        {
            supported = false;
            failureReason = bytecodeInfo.FailureReason;
            return Array.Empty<DxReflectedShaderBindingRequirement>();
        }

        if (!bytecodeInfo.TryCreateBindingRequirements(descriptor.Stage, out var requirements))
        {
            supported = false;
            failureReason = "Shader bytecode contains unsupported resource binding types.";
            return Array.Empty<DxReflectedShaderBindingRequirement>();
        }

        return requirements;
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
        var elements = CopyInputElements(descriptor.Elements);
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
        for (var i = 0; i < Elements.Count; i++)
        {
            var element = Elements[i];
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

    private static DxInputElementDescriptor[] CopyInputElements(IReadOnlyList<DxInputElementDescriptor> source)
    {
        var count = source.Count;
        var elements = new DxInputElementDescriptor[count];
        for (var i = 0; i < count; i++)
        {
            elements[i] = source[i];
        }

        return elements;
    }

    private static void ValidateElements(IReadOnlyList<DxInputElementDescriptor> elements)
    {
        var usedLocations = new HashSet<uint>();
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (string.IsNullOrWhiteSpace(element.SemanticName))
            {
                throw new ArgumentException("Input elements must provide a semantic name.", nameof(elements));
            }

            if (element.ShaderLocation is not { } shaderLocation)
            {
                throw new ArgumentException("Input elements must provide a shader location derived from shader signature reflection or an explicit adapter mapping.", nameof(elements));
            }

            if (!usedLocations.Add(shaderLocation))
            {
                throw new ArgumentException($"Input elements map more than one semantic to shader location {shaderLocation}.", nameof(elements));
            }

            if (!ProGpuDirectXFormatConverter.IsSupportedVertexFormat(element.Format))
            {
                throw new ArgumentException($"Input element '{element.SemanticName}{element.SemanticIndex}' uses DirectX format {element.Format}, which has no compatible WebGPU vertex format.", nameof(elements));
            }
        }
    }

    private static string CreateLayoutKey(IReadOnlyList<DxInputElementDescriptor> elements)
    {
        var builder = new StringBuilder(elements.Count * 64);
        for (var index = 0; index < elements.Count; index++)
        {
            if (index != 0)
            {
                builder.Append('|');
            }

            var element = elements[index];
            builder
                .Append(index)
                .Append(':')
                .Append(element.SemanticName)
                .Append(element.SemanticIndex)
                .Append(':')
                .Append(element.Format)
                .Append(':')
                .Append(element.InputSlot)
                .Append(':')
                .Append(element.AlignedByteOffset)
                .Append(':')
                .Append(element.InputSlotClass)
                .Append(':')
                .Append(element.InstanceDataStepRate)
                .Append(':')
                .Append(element.ShaderLocation);
        }

        return builder.ToString();
    }
}

public sealed unsafe class ProGpuDirectXGraphicsPipeline : IDisposable
{
    private const int BackendInputSlotStackLimit = 16;
    private readonly ProGpuDirectXDevice _device;
    private readonly IntPtr _backendPipeline;
    private readonly IntPtr _frontFacingFrontPipeline;
    private readonly IntPtr _frontFacingBackPipeline;
    private readonly IntPtr _frontFacingTrueShaderModule;
    private readonly IntPtr _frontFacingFalseShaderModule;
    private readonly string? _frontFacingEmulationFailureReason;
    private bool _isDisposed;

    internal ProGpuDirectXGraphicsPipeline(ProGpuDirectXDevice device, DxGraphicsPipelineDescriptor descriptor)
    {
        _device = device;
        Descriptor = descriptor;
        ValidateDescriptor(descriptor);
        EffectiveInputLayout = ResolveEffectiveInputLayout(
            descriptor,
            out var usesReflectedInputLayout,
            out var reflectedInputLayoutSupported,
            out var reflectedInputLayoutFailureReason);
        UsesReflectedInputLayout = usesReflectedInputLayout;
        ReflectedInputLayoutSupported = reflectedInputLayoutSupported;
        ReflectedInputLayoutFailureReason = reflectedInputLayoutFailureReason;
        InputSlotToBackendSlot = CreateBackendInputSlotMap(EffectiveInputLayout);
        PipelineKey = CreatePipelineKey(descriptor, EffectiveInputLayout);
        ReflectedBindingRequirements = CombineReflectedBindingRequirements(
            descriptor.VertexShader,
            descriptor.PixelShader);
        ReflectedBindingRequirementsSupported = AreReflectedBindingRequirementsSupported(
            descriptor.VertexShader,
            descriptor.PixelShader);
        ReflectedBindingRequirementsFailureReason = GetReflectedBindingRequirementsFailureReason(
            descriptor.VertexShader,
            descriptor.PixelShader);

        if (device.Context is { } context && device.IsGpuBacked)
        {
            if (descriptor.PixelShader is { UsesFragmentFrontFacingBuiltin: true } &&
                !device.Capabilities.SupportsFragmentFrontFacingBuiltin)
            {
                var frontFacingEmulationSupported = TryCreateFrontFacingEmulationPipelines(
                    context,
                    descriptor,
                    EffectiveInputLayout,
                    PipelineKey,
                    out _frontFacingFrontPipeline,
                    out _frontFacingBackPipeline,
                    out _frontFacingTrueShaderModule,
                    out _frontFacingFalseShaderModule,
                    out var frontFacingEmulationFailureReason);
                if (!frontFacingEmulationSupported)
                {
                    _frontFacingEmulationFailureReason = frontFacingEmulationFailureReason;
                }

                return;
            }

            _backendPipeline = (IntPtr)CreateBackendPipeline(context, descriptor, EffectiveInputLayout, PipelineKey);
        }
    }

    public DxGraphicsPipelineDescriptor Descriptor { get; }

    public ProGpuDirectXInputLayout? EffectiveInputLayout { get; }

    public bool UsesReflectedInputLayout { get; }

    public bool ReflectedInputLayoutSupported { get; }

    public string? ReflectedInputLayoutFailureReason { get; }

    public string PipelineKey { get; }

    internal IReadOnlyDictionary<uint, uint> InputSlotToBackendSlot { get; }

    public IReadOnlyList<DxReflectedShaderBindingRequirement> ReflectedBindingRequirements { get; }

    public bool HasReflectedBindingRequirements => ReflectedBindingRequirements.Count > 0;

    public bool ReflectedBindingRequirementsSupported { get; }

    public string? ReflectedBindingRequirementsFailureReason { get; }

    public bool HasBackendPipeline => _backendPipeline != IntPtr.Zero || UsesFragmentFrontFacingEmulation;

    public bool UsesFragmentFrontFacingEmulation =>
        _frontFacingFrontPipeline != IntPtr.Zero ||
        _frontFacingBackPipeline != IntPtr.Zero;

    public string? FrontFacingEmulationFailureReason => _frontFacingEmulationFailureReason;

    public IntPtr BackendPipelineHandle => _backendPipeline;

    internal RenderPipeline* BackendPipeline => (RenderPipeline*)_backendPipeline;

    internal RenderPipeline* FrontFacingFrontPipeline => (RenderPipeline*)_frontFacingFrontPipeline;

    internal RenderPipeline* FrontFacingBackPipeline => (RenderPipeline*)_frontFacingBackPipeline;

    internal bool TryGetBackendVertexBufferSlot(uint directXInputSlot, out uint backendInputSlot)
    {
        return InputSlotToBackendSlot.TryGetValue(directXInputSlot, out backendInputSlot);
    }

    private static RenderPipeline* CreateBackendPipeline(
        WgpuContext context,
        DxGraphicsPipelineDescriptor descriptor,
        ProGpuDirectXInputLayout? effectiveInputLayout,
        string pipelineKey,
        ShaderModule* pixelShaderModuleOverride = null,
        DxRasterizerStateDescriptor? rasterizerStateOverride = null)
    {
        if (!descriptor.VertexShader.HasBackendShaderModule)
        {
            throw new InvalidOperationException("A GPU-backed DirectX graphics pipeline requires a WGSL-backed vertex shader.");
        }

        if (descriptor.PixelShader is { HasBackendShaderModule: false })
        {
            throw new InvalidOperationException("A GPU-backed DirectX graphics pipeline requires a WGSL-backed pixel shader when a pixel shader is supplied.");
        }

        var labelPtr = SilkMarshal.StringToPtr(descriptor.Label);
        var vsEntryPtr = SilkMarshal.StringToPtr(descriptor.VertexShader.EntryPoint);
        var fsEntryPtr = descriptor.PixelShader is null
            ? IntPtr.Zero
            : SilkMarshal.StringToPtr(descriptor.PixelShader.EntryPoint);

        try
        {
            var vertexElements = effectiveInputLayout?.Elements ?? Array.Empty<DxInputElementDescriptor>();
            var inputSlots = CreateBackendInputSlots(effectiveInputLayout);
            var bufferLayoutCount = (uint)inputSlots.Length;

            var attributes = stackalloc VertexAttribute[vertexElements.Count];
            var layouts = stackalloc VertexBufferLayout[(int)bufferLayoutCount];
            var attributeIndex = 0;

            for (var slotIndex = 0u; slotIndex < bufferLayoutCount; slotIndex++)
            {
                var directXInputSlot = inputSlots[(int)slotIndex];
                var firstAttributeIndex = attributeIndex;
                uint stride = 0;
                DxInputClassification classification = DxInputClassification.PerVertexData;
                uint stepRate = 0;

                for (var elementIndex = 0; elementIndex < vertexElements.Count; elementIndex++)
                {
                    var element = vertexElements[elementIndex];
                    if (element.InputSlot != directXInputSlot)
                    {
                        continue;
                    }

                    attributes[attributeIndex] = new VertexAttribute
                    {
                        Format = ProGpuDirectXFormatConverter.ToVertexFormat(element.Format),
                        Offset = element.AlignedByteOffset,
                        ShaderLocation = element.ShaderLocation!.Value
                    };
                    attributeIndex++;

                    stride = Math.Max(
                        stride,
                        element.AlignedByteOffset + ProGpuDirectXFormatConverter.GetVertexFormatSizeInBytes(element.Format));
                    classification = element.InputSlotClass;
                    stepRate = element.InstanceDataStepRate;
                }

                var slotAttributeCount = (uint)(attributeIndex - firstAttributeIndex);
                layouts[slotIndex] = new VertexBufferLayout
                {
                    ArrayStride = stride,
                    StepMode = ProGpuDirectXFormatConverter.ToVertexStepMode(classification),
                    AttributeCount = slotAttributeCount,
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
                BufferCount = bufferLayoutCount,
                Buffers = bufferLayoutCount == 0 ? null : layouts
            };

            FragmentState fragmentState = default;
            BlendState blendState = default;
            ColorTargetState colorTarget = default;
            if (descriptor.PixelShader is not null)
            {
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
                blendState = new BlendState
                {
                    Color = blendComponentColor,
                    Alpha = blendComponentAlpha
                };
                colorTarget = new ColorTargetState
                {
                    Format = ProGpuDirectXFormatConverter.ToTextureFormat(descriptor.RenderTargetFormat),
                    Blend = descriptor.BlendState.EnableBlend ? &blendState : null,
                    WriteMask = ProGpuDirectXFormatConverter.ToColorWriteMask(descriptor.BlendState.WriteMask)
                };
                fragmentState = new FragmentState
                {
                    Module = pixelShaderModuleOverride != null
                        ? pixelShaderModuleOverride
                        : descriptor.PixelShader.BackendShaderModule,
                    EntryPoint = (byte*)fsEntryPtr,
                    TargetCount = 1,
                    Targets = &colorTarget
                };
            }

            var rasterizerState = rasterizerStateOverride ?? descriptor.RasterizerState;
            var depthStencilEnabled = descriptor.DepthStencilFormat != DxResourceFormat.Unknown &&
                (descriptor.DepthStencilState.DepthEnable || descriptor.DepthStencilState.StencilEnable);
            DepthStencilState depthStencilState = default;
            if (depthStencilEnabled)
            {
                depthStencilState = new DepthStencilState
                {
                    Format = ProGpuDirectXFormatConverter.ToTextureFormat(descriptor.DepthStencilFormat),
                    DepthWriteEnabled = descriptor.DepthStencilState.DepthEnable &&
                        descriptor.DepthStencilState.DepthWriteMask == DxDepthWriteMask.All,
                    DepthCompare = descriptor.DepthStencilState.DepthEnable
                        ? ProGpuDirectXFormatConverter.ToCompareFunction(descriptor.DepthStencilState.DepthFunction)
                        : CompareFunction.Always,
                    StencilFront = CreateStencilFaceState(descriptor.DepthStencilState.FrontFace),
                    StencilBack = CreateStencilFaceState(descriptor.DepthStencilState.BackFace),
                    StencilReadMask = descriptor.DepthStencilState.StencilReadMask,
                    StencilWriteMask = descriptor.DepthStencilState.StencilWriteMask,
                    DepthBias = rasterizerState.DepthBias,
                    DepthBiasClamp = rasterizerState.DepthBiasClamp,
                    DepthBiasSlopeScale = rasterizerState.SlopeScaledDepthBias
                };
            }

            var pipelineDesc = new RenderPipelineDescriptor
            {
                Label = (byte*)labelPtr,
                Layout = null,
                Vertex = vertexState,
                Primitive = new PrimitiveState
                {
                    Topology = GetBackendPrimitiveTopology(descriptor),
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = ProGpuDirectXFormatConverter.ToFrontFace(rasterizerState.FrontFace),
                    CullMode = ProGpuDirectXFormatConverter.ToCullMode(rasterizerState.CullMode)
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

    private static bool TryCreateFrontFacingEmulationPipelines(
        WgpuContext context,
        DxGraphicsPipelineDescriptor descriptor,
        ProGpuDirectXInputLayout? effectiveInputLayout,
        string pipelineKey,
        out IntPtr frontPipeline,
        out IntPtr backPipeline,
        out IntPtr trueShaderModule,
        out IntPtr falseShaderModule,
        out string? failureReason)
    {
        frontPipeline = IntPtr.Zero;
        backPipeline = IntPtr.Zero;
        trueShaderModule = IntPtr.Zero;
        falseShaderModule = IntPtr.Zero;
        failureReason = null;

        if (descriptor.PixelShader?.BackendSource is not { } pixelSource)
        {
            failureReason = "Fragment front-facing emulation requires a translated WGSL pixel shader source.";
            return false;
        }

        if (descriptor.Topology is not (DxPrimitiveTopology.TriangleList or DxPrimitiveTopology.TriangleStrip) ||
            descriptor.RasterizerState.FillMode != DxFillMode.Solid)
        {
            failureReason = "Fragment front-facing emulation currently supports solid triangle-list or triangle-strip pipelines.";
            return false;
        }

        if (descriptor.RasterizerState.CullMode == DxCullMode.None &&
            descriptor.BlendState.EnableBlend)
        {
            failureReason = "Order-preserving fragment front-facing emulation for no-cull blended pipelines is not supported.";
            return false;
        }

        if ((descriptor.RasterizerState.CullMode is DxCullMode.None or DxCullMode.Back) &&
            ProGpuDirectXFrontFacingEmulation.TryCreateOverrideSource(pixelSource, isFrontFacing: true, out var trueSource))
        {
            trueShaderModule = (IntPtr)ProGpuDirectXShader.CreateWgslShaderModule(
                context,
                descriptor.PixelShader.Descriptor with { Label = $"{descriptor.PixelShader.Descriptor.Label} FrontFacingTrue" },
                trueSource);
            if (trueShaderModule != IntPtr.Zero)
            {
                frontPipeline = (IntPtr)CreateBackendPipeline(
                    context,
                    descriptor,
                    effectiveInputLayout,
                    $"{pipelineKey}|front-facing-true",
                    (ShaderModule*)trueShaderModule,
                    descriptor.RasterizerState with { CullMode = DxCullMode.Back });
            }
        }

        if ((descriptor.RasterizerState.CullMode is DxCullMode.None or DxCullMode.Front) &&
            ProGpuDirectXFrontFacingEmulation.TryCreateOverrideSource(pixelSource, isFrontFacing: false, out var falseSource))
        {
            falseShaderModule = (IntPtr)ProGpuDirectXShader.CreateWgslShaderModule(
                context,
                descriptor.PixelShader.Descriptor with { Label = $"{descriptor.PixelShader.Descriptor.Label} FrontFacingFalse" },
                falseSource);
            if (falseShaderModule != IntPtr.Zero)
            {
                backPipeline = (IntPtr)CreateBackendPipeline(
                    context,
                    descriptor,
                    effectiveInputLayout,
                    $"{pipelineKey}|front-facing-false",
                    (ShaderModule*)falseShaderModule,
                    descriptor.RasterizerState with { CullMode = DxCullMode.Front });
            }
        }

        if (frontPipeline != IntPtr.Zero || backPipeline != IntPtr.Zero)
        {
            return true;
        }

        failureReason = "Fragment front-facing shader variants could not be created.";
        return false;
    }

    private static uint[] CreateBackendInputSlots(ProGpuDirectXInputLayout? effectiveInputLayout)
    {
        var elements = effectiveInputLayout?.Elements;
        if (elements is null || elements.Count == 0)
        {
            return Array.Empty<uint>();
        }

        var elementCount = elements.Count;
        uint[]? rentedSlots = null;
        Span<uint> inputSlots = elementCount <= BackendInputSlotStackLimit
            ? stackalloc uint[elementCount]
            : (rentedSlots = ArrayPool<uint>.Shared.Rent(elementCount)).AsSpan(0, elementCount);

        try
        {
            var inputSlotCount = 0;
            for (var i = 0; i < elements.Count; i++)
            {
                InsertSortedUniqueInputSlot(inputSlots, ref inputSlotCount, elements[i].InputSlot);
            }

            if (inputSlotCount == 0)
            {
                return Array.Empty<uint>();
            }

            var result = new uint[inputSlotCount];
            inputSlots[..inputSlotCount].CopyTo(result);
            return result;
        }
        finally
        {
            if (rentedSlots != null)
            {
                ArrayPool<uint>.Shared.Return(rentedSlots);
            }
        }
    }

    private static void InsertSortedUniqueInputSlot(Span<uint> slots, ref int slotCount, uint inputSlot)
    {
        for (var i = 0; i < slotCount; i++)
        {
            var existing = slots[i];
            if (existing == inputSlot)
            {
                return;
            }

            if (inputSlot < existing)
            {
                for (var shift = slotCount; shift > i; shift--)
                {
                    slots[shift] = slots[shift - 1];
                }

                slots[i] = inputSlot;
                slotCount++;
                return;
            }
        }

        slots[slotCount++] = inputSlot;
    }

    private static IReadOnlyDictionary<uint, uint> CreateBackendInputSlotMap(ProGpuDirectXInputLayout? effectiveInputLayout)
    {
        var inputSlots = CreateBackendInputSlots(effectiveInputLayout);
        var inputSlotMap = new Dictionary<uint, uint>(inputSlots.Length);
        for (var index = 0; index < inputSlots.Length; index++)
        {
            inputSlotMap[inputSlots[index]] = (uint)index;
        }

        return inputSlotMap;
    }

    private static PrimitiveTopology GetBackendPrimitiveTopology(DxGraphicsPipelineDescriptor descriptor)
    {
        if (descriptor.RasterizerState.FillMode == DxFillMode.Wireframe &&
            descriptor.Topology is DxPrimitiveTopology.TriangleList or DxPrimitiveTopology.TriangleStrip)
        {
            return PrimitiveTopology.LineList;
        }

        return ProGpuDirectXFormatConverter.ToPrimitiveTopology(descriptor.Topology);
    }

    private static StencilFaceState CreateStencilFaceState(DxStencilFaceDescriptor descriptor)
    {
        return new StencilFaceState
        {
            Compare = ProGpuDirectXFormatConverter.ToCompareFunction(descriptor.Function),
            FailOp = ProGpuDirectXFormatConverter.ToStencilOperation(descriptor.FailOperation),
            DepthFailOp = ProGpuDirectXFormatConverter.ToStencilOperation(descriptor.DepthFailOperation),
            PassOp = ProGpuDirectXFormatConverter.ToStencilOperation(descriptor.PassOperation)
        };
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

        if (descriptor.PixelShader is not null &&
            descriptor.RenderTargetFormat == DxResourceFormat.Unknown)
        {
            throw new ArgumentException("Graphics pipelines with a pixel shader require a render-target format.", nameof(descriptor));
        }

        if (descriptor.SampleCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Graphics pipelines must use at least one sample.");
        }

        if (descriptor.DepthStencilState.StencilEnable &&
            descriptor.DepthStencilFormat == DxResourceFormat.Unknown)
        {
            throw new ArgumentException("Stencil enabled pipelines require a depth-stencil format.", nameof(descriptor));
        }

        if (descriptor.DepthStencilState.StencilEnable &&
            descriptor.DepthStencilFormat != DxResourceFormat.D24UnormS8UInt)
        {
            throw new ArgumentException("Stencil enabled pipelines require a stencil-capable depth-stencil format.", nameof(descriptor));
        }
    }

    private static ProGpuDirectXInputLayout? ResolveEffectiveInputLayout(
        DxGraphicsPipelineDescriptor descriptor,
        out bool usesReflectedInputLayout,
        out bool reflectedInputLayoutSupported,
        out string? reflectedInputLayoutFailureReason)
    {
        usesReflectedInputLayout = false;
        reflectedInputLayoutSupported = true;
        reflectedInputLayoutFailureReason = null;

        if (descriptor.InputLayout is not null)
        {
            return descriptor.InputLayout;
        }

        var bytecodeInfo = descriptor.VertexShader.BytecodeInfo;
        if (bytecodeInfo is null)
        {
            return null;
        }

        if (!bytecodeInfo.IsValid)
        {
            reflectedInputLayoutSupported = false;
            reflectedInputLayoutFailureReason = bytecodeInfo.FailureReason;
            return null;
        }

        if (!bytecodeInfo.HasInputSignature)
        {
            return null;
        }

        if (!bytecodeInfo.TryCreateInputLayoutDescriptor(out var inputLayoutDescriptor) ||
            inputLayoutDescriptor is null)
        {
            reflectedInputLayoutSupported = false;
            reflectedInputLayoutFailureReason = "Vertex shader bytecode input signature contains unsupported input-layout metadata.";
            return null;
        }

        usesReflectedInputLayout = true;
        return new ProGpuDirectXInputLayout(inputLayoutDescriptor);
    }

    private static string CreatePipelineKey(
        DxGraphicsPipelineDescriptor descriptor,
        ProGpuDirectXInputLayout? effectiveInputLayout)
    {
        return string.Join(
            "|",
            descriptor.VertexShader.SourceHash,
            descriptor.VertexShader.EntryPoint,
            descriptor.PixelShader?.SourceHash ?? "no-ps",
            descriptor.PixelShader?.EntryPoint ?? "no-ps",
            effectiveInputLayout?.LayoutKey ?? "no-layout",
            descriptor.Topology,
            descriptor.RenderTargetFormat,
            descriptor.DepthStencilFormat,
            descriptor.SampleCount,
            descriptor.RasterizerState,
            descriptor.BlendState,
            descriptor.DepthStencilState);
    }

    private static IReadOnlyList<DxReflectedShaderBindingRequirement> CombineReflectedBindingRequirements(
        ProGpuDirectXShader vertexShader,
        ProGpuDirectXShader? pixelShader)
    {
        var vertexRequirements = vertexShader.ReflectedBindingRequirements;
        var pixelRequirements = pixelShader?.ReflectedBindingRequirements;
        var requirementCount = vertexRequirements.Count + (pixelRequirements?.Count ?? 0);
        if (requirementCount == 0)
        {
            return Array.Empty<DxReflectedShaderBindingRequirement>();
        }

        var requirements = new DxReflectedShaderBindingRequirement[requirementCount];
        var write = 0;
        CopyReflectedBindingRequirements(vertexRequirements, requirements, ref write);
        if (pixelRequirements is not null)
        {
            CopyReflectedBindingRequirements(pixelRequirements, requirements, ref write);
        }

        Array.Sort(requirements, CompareReflectedBindingRequirements);
        return requirements;
    }

    private static void CopyReflectedBindingRequirements(
        IReadOnlyList<DxReflectedShaderBindingRequirement> source,
        DxReflectedShaderBindingRequirement[] destination,
        ref int write)
    {
        for (var i = 0; i < source.Count; i++)
        {
            destination[write++] = source[i];
        }
    }

    private static int CompareReflectedBindingRequirements(
        DxReflectedShaderBindingRequirement left,
        DxReflectedShaderBindingRequirement right)
    {
        var nativeBindingComparison = left.NativeBinding.CompareTo(right.NativeBinding);
        if (nativeBindingComparison != 0)
        {
            return nativeBindingComparison;
        }

        var stageComparison = left.Stage.CompareTo(right.Stage);
        if (stageComparison != 0)
        {
            return stageComparison;
        }

        var kindComparison = left.Kind.CompareTo(right.Kind);
        return kindComparison != 0
            ? kindComparison
            : left.Slot.CompareTo(right.Slot);
    }

    private static bool AreReflectedBindingRequirementsSupported(
        ProGpuDirectXShader vertexShader,
        ProGpuDirectXShader? pixelShader)
    {
        return vertexShader.ReflectedBindingRequirementsSupported &&
            (pixelShader is null || pixelShader.ReflectedBindingRequirementsSupported);
    }

    private static string? GetReflectedBindingRequirementsFailureReason(
        ProGpuDirectXShader vertexShader,
        ProGpuDirectXShader? pixelShader)
    {
        if (!vertexShader.ReflectedBindingRequirementsSupported &&
            !string.IsNullOrWhiteSpace(vertexShader.ReflectedBindingRequirementsFailureReason))
        {
            return vertexShader.ReflectedBindingRequirementsFailureReason;
        }

        if (pixelShader is { ReflectedBindingRequirementsSupported: false } &&
            !string.IsNullOrWhiteSpace(pixelShader.ReflectedBindingRequirementsFailureReason))
        {
            return pixelShader.ReflectedBindingRequirementsFailureReason;
        }

        return null;
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

        if (_frontFacingFrontPipeline != IntPtr.Zero &&
            _device.Context is { IsDisposed: false } frontContext)
        {
            frontContext.QueueRenderPipelineDisposal(_frontFacingFrontPipeline);
        }

        if (_frontFacingBackPipeline != IntPtr.Zero &&
            _device.Context is { IsDisposed: false } backContext)
        {
            backContext.QueueRenderPipelineDisposal(_frontFacingBackPipeline);
        }

        if (_frontFacingTrueShaderModule != IntPtr.Zero &&
            _device.Context is { IsDisposed: false } trueContext)
        {
            trueContext.QueueShaderModuleDisposal(_frontFacingTrueShaderModule);
        }

        if (_frontFacingFalseShaderModule != IntPtr.Zero &&
            _device.Context is { IsDisposed: false } falseContext)
        {
            falseContext.QueueShaderModuleDisposal(_frontFacingFalseShaderModule);
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
        ReflectedBindingRequirements = descriptor.ComputeShader.ReflectedBindingRequirements;
        ReflectedBindingRequirementsSupported = descriptor.ComputeShader.ReflectedBindingRequirementsSupported;
        ReflectedBindingRequirementsFailureReason = descriptor.ComputeShader.ReflectedBindingRequirementsFailureReason;
        if (device.Context is { } context && device.IsGpuBacked)
        {
            if (descriptor.ComputeShader.UsesRwByteAddressBufferInterlockedCompareExchange &&
                !device.Capabilities.SupportsRwByteAddressBufferInterlockedCompareExchange)
            {
                return;
            }

            if (!descriptor.ComputeShader.HasBackendShaderModule)
            {
                throw new InvalidOperationException("A GPU-backed DirectX compute pipeline requires a WGSL-backed compute shader.");
            }

            _backendPipeline = (IntPtr)CreateBackendPipeline(context, descriptor);
        }
    }

    public DxComputePipelineDescriptor Descriptor { get; }

    public string PipelineKey { get; }

    public IReadOnlyList<DxReflectedShaderBindingRequirement> ReflectedBindingRequirements { get; }

    public bool HasReflectedBindingRequirements => ReflectedBindingRequirements.Count > 0;

    public bool ReflectedBindingRequirementsSupported { get; }

    public string? ReflectedBindingRequirementsFailureReason { get; }

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
