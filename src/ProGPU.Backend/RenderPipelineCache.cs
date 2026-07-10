using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

public enum GpuBlendMode
{
    SrcOver = 0,
    Src,
    Dst,
    SrcIn,
    DstIn,
    SrcOut,
    DstOut,
    SrcAtop,
    DstAtop,
    Xor,
    DstOver,
    Multiply,
    Screen,
    Darken,
    Lighten,
    Exclusion,
    Plus,
    Clear,
    Overlay,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Hue,
    Saturation,
    Color,
    Luminosity
}

public unsafe class RenderPipelineCache : IDisposable
{
    private readonly WgpuContext _context;
    
    private readonly Dictionary<string, nint> _shaders = new();
    private readonly Dictionary<string, nint> _renderPipelines = new();
    private readonly Dictionary<string, nint> _computePipelines = new();
    
    private bool _isDisposed;

    public RenderPipelineCache(WgpuContext context)
    {
        _context = context;
    }

    public ShaderModule* GetOrCreateShader(string key, string wgslCode, string label = "ShaderModule")
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(RenderPipelineCache));
        if (_shaders.TryGetValue(key, out var cachedModule)) return (ShaderModule*)cachedModule;

        var codePtr = SilkMarshal.StringToPtr(wgslCode);
        var labelPtr = SilkMarshal.StringToPtr(label);

        var wgslDesc = new ShaderModuleWGSLDescriptor
        {
            Chain = new ChainedStruct
            {
                Next = null,
                SType = SType.ShaderModuleWgslDescriptor
            },
            Code = (byte*)codePtr
        };

        var desc = new ShaderModuleDescriptor
        {
            NextInChain = (ChainedStruct*)&wgslDesc,
            Label = (byte*)labelPtr
        };

        var module = _context.Wgpu.DeviceCreateShaderModule(_context.Device, &desc);
        
        SilkMarshal.Free(codePtr);
        SilkMarshal.Free(labelPtr);

        if (module == null)
        {
            throw new InvalidOperationException($"Failed to compile WGSL shader '{key}'.");
        }

        _shaders[key] = (nint)module;
        return module;
    }

    public RenderPipeline* GetOrCreateRenderPipeline(
        string key,
        ShaderModule* shaderModule,
        string vertexEntry = "vs_main",
        string fragmentEntry = "fs_main",
        TextureFormat targetFormat = TextureFormat.Bgra8Unorm,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        VertexBufferLayout[]? vertexBufferLayouts = null,
        bool enableBlend = true,
        bool enableDepthStencil = false,
        TextureFormat depthFormat = TextureFormat.Depth24PlusStencil8,
        CompareFunction stencilCompare = CompareFunction.Always,
        StencilOperation stencilFail = StencilOperation.Keep,
        StencilOperation stencilDepthFail = StencilOperation.Keep,
        StencilOperation stencilPass = StencilOperation.Keep,
        uint sampleCount = 1,
        bool depthWriteEnabled = false,
        CompareFunction depthCompare = CompareFunction.Always,
        CullMode cullMode = CullMode.None,
        GpuBlendMode blendMode = GpuBlendMode.SrcOver,
        PipelineLayout* pipelineLayout = null,
        GpuTextureAlphaMode sourceAlphaMode = GpuTextureAlphaMode.Straight)
    {
        return GetOrCreateRenderPipelineCore(
            key,
            shaderModule,
            vertexEntry,
            fragmentEntry,
            targetFormat,
            topology,
            vertexBufferLayouts.AsSpan(),
            enableBlend,
            enableDepthStencil,
            depthFormat,
            stencilCompare,
            stencilFail,
            stencilDepthFail,
            stencilPass,
            sampleCount,
            depthWriteEnabled,
            depthCompare,
            cullMode,
            blendMode,
            pipelineLayout,
            sourceAlphaMode);
    }

    public RenderPipeline* GetOrCreateRenderPipeline(
        string key,
        ShaderModule* shaderModule,
        ReadOnlySpan<VertexBufferLayout> vertexBufferLayouts,
        string vertexEntry = "vs_main",
        string fragmentEntry = "fs_main",
        TextureFormat targetFormat = TextureFormat.Bgra8Unorm,
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        bool enableBlend = true,
        bool enableDepthStencil = false,
        TextureFormat depthFormat = TextureFormat.Depth24PlusStencil8,
        CompareFunction stencilCompare = CompareFunction.Always,
        StencilOperation stencilFail = StencilOperation.Keep,
        StencilOperation stencilDepthFail = StencilOperation.Keep,
        StencilOperation stencilPass = StencilOperation.Keep,
        uint sampleCount = 1,
        bool depthWriteEnabled = false,
        CompareFunction depthCompare = CompareFunction.Always,
        CullMode cullMode = CullMode.None,
        GpuBlendMode blendMode = GpuBlendMode.SrcOver,
        PipelineLayout* pipelineLayout = null,
        GpuTextureAlphaMode sourceAlphaMode = GpuTextureAlphaMode.Straight)
    {
        return GetOrCreateRenderPipelineCore(
            key,
            shaderModule,
            vertexEntry,
            fragmentEntry,
            targetFormat,
            topology,
            vertexBufferLayouts,
            enableBlend,
            enableDepthStencil,
            depthFormat,
            stencilCompare,
            stencilFail,
            stencilDepthFail,
            stencilPass,
            sampleCount,
            depthWriteEnabled,
            depthCompare,
            cullMode,
            blendMode,
            pipelineLayout,
            sourceAlphaMode);
    }

    private RenderPipeline* GetOrCreateRenderPipelineCore(
        string key,
        ShaderModule* shaderModule,
        string vertexEntry,
        string fragmentEntry,
        TextureFormat targetFormat,
        PrimitiveTopology topology,
        ReadOnlySpan<VertexBufferLayout> vertexBufferLayouts,
        bool enableBlend,
        bool enableDepthStencil,
        TextureFormat depthFormat,
        CompareFunction stencilCompare,
        StencilOperation stencilFail,
        StencilOperation stencilDepthFail,
        StencilOperation stencilPass,
        uint sampleCount,
        bool depthWriteEnabled,
        CompareFunction depthCompare,
        CullMode cullMode,
        GpuBlendMode blendMode,
        PipelineLayout* pipelineLayout,
        GpuTextureAlphaMode sourceAlphaMode)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(RenderPipelineCache));
        if (_renderPipelines.TryGetValue(key, out var cachedPipeline)) return (RenderPipeline*)cachedPipeline;

        var vsEntryPtr = SilkMarshal.StringToPtr(vertexEntry);
        var fsEntryPtr = SilkMarshal.StringToPtr(fragmentEntry);
        var labelPtr = SilkMarshal.StringToPtr($"Pipeline_{key}");

        var blendState = CreateBlendState(blendMode, sourceAlphaMode);

        var colorTarget = new ColorTargetState
        {
            Format = targetFormat,
            Blend = enableBlend ? &blendState : null,
            WriteMask = ColorWriteMask.All
        };

        var fragmentState = new FragmentState
        {
            Module = shaderModule,
            EntryPoint = (byte*)fsEntryPtr,
            TargetCount = 1,
            Targets = &colorTarget
        };

        VertexBufferLayout* layoutsPtr = null;
        int layoutsCount = vertexBufferLayouts.Length;

        RenderPipeline* pipeline = null;

        fixed (VertexBufferLayout* pLayouts = vertexBufferLayouts)
        {
            if (vertexBufferLayouts.Length > 0)
            {
                layoutsPtr = pLayouts;
            }

            var vertexState = new VertexState
            {
                Module = shaderModule,
                EntryPoint = (byte*)vsEntryPtr,
                BufferCount = (uint)layoutsCount,
                Buffers = layoutsPtr
            };

            var depthStencilState = new DepthStencilState
            {
                Format = depthFormat,
                DepthWriteEnabled = depthWriteEnabled,
                DepthCompare = depthCompare,
                StencilFront = new StencilFaceState
                {
                    Compare = stencilCompare,
                    FailOp = stencilFail,
                    DepthFailOp = stencilDepthFail,
                    PassOp = stencilPass
                },
                StencilBack = new StencilFaceState
                {
                    Compare = stencilCompare,
                    FailOp = stencilFail,
                    DepthFailOp = stencilDepthFail,
                    PassOp = stencilPass
                },
                StencilReadMask = 0xFF,
                StencilWriteMask = 0xFF,
                DepthBias = 0,
                DepthBiasSlopeScale = 0f,
                DepthBiasClamp = 0f
            };

            var desc = new RenderPipelineDescriptor
            {
                Label = (byte*)labelPtr,
                Layout = pipelineLayout,
                Vertex = vertexState,
                Primitive = new PrimitiveState
                {
                    Topology = topology,
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = FrontFace.Ccw,
                    CullMode = cullMode
                },
                DepthStencil = enableDepthStencil ? &depthStencilState : null,
                Multisample = new MultisampleState
                {
                    Count = sampleCount,
                    Mask = 0xFFFFFFFF,
                    AlphaToCoverageEnabled = false
                },
                Fragment = &fragmentState
            };

            pipeline = _context.Wgpu.DeviceCreateRenderPipeline(_context.Device, &desc);
        }

        SilkMarshal.Free(vsEntryPtr);
        SilkMarshal.Free(fsEntryPtr);
        SilkMarshal.Free(labelPtr);

        if (pipeline == null)
        {
            throw new InvalidOperationException($"Failed to create RenderPipeline '{key}'.");
        }

        _renderPipelines[key] = (nint)pipeline;
        return pipeline;
    }

    internal static BlendState CreateBlendState(
        GpuBlendMode blendMode,
        GpuTextureAlphaMode sourceAlphaMode = GpuTextureAlphaMode.Straight)
    {
        var blendState = new BlendState();
        switch (blendMode)
        {
            case GpuBlendMode.Src:
                var sourceColorFactor = sourceAlphaMode == GpuTextureAlphaMode.Premultiplied
                    ? BlendFactor.One
                    : BlendFactor.SrcAlpha;
                blendState.Color = new BlendComponent { SrcFactor = sourceColorFactor, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.Dst:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.One, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.One, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.SrcIn:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.DstAlpha, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.DstAlpha, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.DstIn:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.SrcAlpha, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.SrcAlpha, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.SrcOut:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.DstOut:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.SrcAtop:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.DstAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.DstAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.DstAtop:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.SrcAlpha, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.SrcAlpha, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.Xor:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.DstOver:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.One, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.OneMinusDstAlpha, DstFactor = BlendFactor.One, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.Multiply:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.Dst, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.Screen:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrc, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrc, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.Darken:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.One, Operation = BlendOperation.Min };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.One, Operation = BlendOperation.Max };
                break;
            case GpuBlendMode.Lighten:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.One, Operation = BlendOperation.Max };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.One, Operation = BlendOperation.Max };
                break;
            case GpuBlendMode.Exclusion:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.OneMinusDst, DstFactor = BlendFactor.OneMinusSrc, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.Plus:
                var plusSourceColorFactor = sourceAlphaMode == GpuTextureAlphaMode.Premultiplied
                    ? BlendFactor.One
                    : BlendFactor.SrcAlpha;
                blendState.Color = new BlendComponent { SrcFactor = plusSourceColorFactor, DstFactor = BlendFactor.One, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.One, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.Clear:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.Zero, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                break;
            case GpuBlendMode.SrcOver:
            default:
                var colorSourceFactor = sourceAlphaMode == GpuTextureAlphaMode.Premultiplied
                    ? BlendFactor.One
                    : BlendFactor.SrcAlpha;
                blendState.Color = new BlendComponent { SrcFactor = colorSourceFactor, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add };
                break;
        }

        return blendState;
    }

    public ComputePipeline* GetOrCreateComputePipeline(string key, ShaderModule* shaderModule, string entryPoint = "main")
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(RenderPipelineCache));
        if (_computePipelines.TryGetValue(key, out var cachedPipeline)) return (ComputePipeline*)cachedPipeline;

        var entryPtr = SilkMarshal.StringToPtr(entryPoint);
        var labelPtr = SilkMarshal.StringToPtr($"Compute_{key}");

        var desc = new ComputePipelineDescriptor
        {
            Label = (byte*)labelPtr,
            Layout = null, // Auto layout
            Compute = new ProgrammableStageDescriptor
            {
                Module = shaderModule,
                EntryPoint = (byte*)entryPtr
            }
        };

        var pipeline = _context.Wgpu.DeviceCreateComputePipeline(_context.Device, &desc);

        SilkMarshal.Free(entryPtr);
        SilkMarshal.Free(labelPtr);

        if (pipeline == null)
        {
            throw new InvalidOperationException($"Failed to create ComputePipeline '{key}'.");
        }

        _computePipelines[key] = (nint)pipeline;
        return pipeline;
    }

    public bool HasRenderPipeline(string key)
    {
        return _renderPipelines.ContainsKey(key);
    }

    public void ReleaseShader(string key)
    {
        lock (_context.RenderLock)
        {
            if (_shaders.Remove(key, out var s))
            {
                if (!_context.IsDisposed)
                {
                    _context.QueueShaderModuleDisposal((nint)s);
                }
            }
        }
    }

    public void ReleaseRenderPipeline(string key)
    {
        lock (_context.RenderLock)
        {
            if (_renderPipelines.Remove(key, out var p))
            {
                if (!_context.IsDisposed)
                {
                    _context.QueueRenderPipelineDisposal((nint)p);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        lock (_context.RenderLock)
        {
            if (!_context.IsDisposed)
            {
                var renderPipelineEnumerator = _renderPipelines.Values.GetEnumerator();
                while (renderPipelineEnumerator.MoveNext())
                {
                    _context.QueueRenderPipelineDisposal((nint)renderPipelineEnumerator.Current);
                }

                var computePipelineEnumerator = _computePipelines.Values.GetEnumerator();
                while (computePipelineEnumerator.MoveNext())
                {
                    _context.QueueComputePipelineDisposal((nint)computePipelineEnumerator.Current);
                }

                var shaderModuleEnumerator = _shaders.Values.GetEnumerator();
                while (shaderModuleEnumerator.MoveNext())
                {
                    _context.QueueShaderModuleDisposal((nint)shaderModuleEnumerator.Current);
                }
            }
            _renderPipelines.Clear();
            _computePipelines.Clear();
            _shaders.Clear();
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    ~RenderPipelineCache()
    {
        // Do not call Dispose() or native WebGPU release APIs during finalization.
    }
}
