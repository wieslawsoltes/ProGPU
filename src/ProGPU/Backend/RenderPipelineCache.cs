using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

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
        uint sampleCount = 1)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(RenderPipelineCache));
        if (_renderPipelines.TryGetValue(key, out var cachedPipeline)) return (RenderPipeline*)cachedPipeline;

        var vsEntryPtr = SilkMarshal.StringToPtr(vertexEntry);
        var fsEntryPtr = SilkMarshal.StringToPtr(fragmentEntry);
        var labelPtr = SilkMarshal.StringToPtr($"Pipeline_{key}");

        // Blending configuration for transparent/translucent UI components
        var blendState = new BlendState
        {
            Color = new BlendComponent
            {
                SrcFactor = BlendFactor.SrcAlpha,
                DstFactor = BlendFactor.OneMinusSrcAlpha,
                Operation = BlendOperation.Add
            },
            Alpha = new BlendComponent
            {
                SrcFactor = BlendFactor.One,
                DstFactor = BlendFactor.OneMinusSrcAlpha,
                Operation = BlendOperation.Add
            }
        };

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

        // Vertex buffer layouts pinning
        VertexBufferLayout* layoutsPtr = null;
        int layoutsCount = 0;
        if (vertexBufferLayouts != null && vertexBufferLayouts.Length > 0)
        {
            layoutsCount = vertexBufferLayouts.Length;
        }

        RenderPipeline* pipeline = null;

        fixed (VertexBufferLayout* pLayouts = vertexBufferLayouts)
        {
            if (vertexBufferLayouts != null && vertexBufferLayouts.Length > 0)
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
                DepthWriteEnabled = false,
                DepthCompare = CompareFunction.Always,
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
                Layout = null, // Auto-layout derived from shaders
                Vertex = vertexState,
                Primitive = new PrimitiveState
                {
                    Topology = topology,
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = FrontFace.Ccw,
                    CullMode = CullMode.None
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

    public void Dispose()
    {
        if (_isDisposed) return;

        foreach (var p in _renderPipelines.Values)
        {
            _context.Wgpu.RenderPipelineRelease((RenderPipeline*)p);
        }
        _renderPipelines.Clear();

        foreach (var p in _computePipelines.Values)
        {
            _context.Wgpu.ComputePipelineRelease((ComputePipeline*)p);
        }
        _computePipelines.Clear();

        foreach (var s in _shaders.Values)
        {
            _context.Wgpu.ShaderModuleRelease((ShaderModule*)s);
        }
        _shaders.Clear();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    ~RenderPipelineCache()
    {
        Dispose();
    }
}
