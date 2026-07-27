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
    Luminosity,
    Modulate
}

public unsafe class RenderPipelineCache : IDisposable
{
    private readonly WgpuContext _context;

    private readonly Dictionary<string, CachedShaderModule> _shaders = new();
    private readonly Dictionary<string, CachedRenderPipeline> _renderPipelines = new();
    private readonly Dictionary<string, CachedComputePipeline> _computePipelines = new();

    private bool _isDisposed;

    public RenderPipelineCache(WgpuContext context)
    {
        _context = context;
    }

    public int ShaderCount => _shaders.Count;

    public int RenderPipelineCount => _renderPipelines.Count;

    public int ComputePipelineCount => _computePipelines.Count;

    public ShaderModule* GetOrCreateShader(string key, string wgslCode, string label = "ShaderModule")
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(RenderPipelineCache));
        if (_shaders.TryGetValue(key, out CachedShaderModule cachedModule))
        {
            if (!string.Equals(
                    cachedModule.CacheKey.WgslCode,
                    wgslCode,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Shader cache key '{key}' was reused with different WGSL source.");
            }

            return (ShaderModule*)cachedModule.Handle;
        }

        ShaderModule* module =
            _context.DeviceResourceDomain.AcquireShaderModule(
                key,
                wgslCode,
                label,
                out WgpuDeviceResourceDomain.ShaderModuleKey cacheKey);
        _shaders.Add(
            key,
            new CachedShaderModule(cacheKey, (nint)module));
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
        if (_renderPipelines.TryGetValue(
                key,
                out CachedRenderPipeline cachedPipeline))
        {
            if (!cachedPipeline.CacheKey.Matches(
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
                    sourceAlphaMode))
            {
                throw new InvalidOperationException(
                    $"Render-pipeline cache key '{key}' was reused with a different descriptor.");
            }
            return (RenderPipeline*)cachedPipeline.Handle;
        }

        var deviceKey = WgpuRenderPipelineResourceKey.Create(
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

        if (_context.DeviceResourceDomain.TryAcquireRenderPipeline(
                deviceKey,
                out RenderPipeline* sharedPipeline))
        {
            _renderPipelines.Add(
                key,
                new CachedRenderPipeline(
                    deviceKey,
                    (nint)sharedPipeline));
            return sharedPipeline;
        }

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

            pipeline = _context.Api.DeviceCreateRenderPipeline(_context.Device, &desc);
        }

        SilkMarshal.Free(vsEntryPtr);
        SilkMarshal.Free(fsEntryPtr);
        SilkMarshal.Free(labelPtr);

        if (pipeline == null)
        {
            throw new InvalidOperationException($"Failed to create RenderPipeline '{key}'.");
        }

        pipeline = _context.DeviceResourceDomain.PublishRenderPipeline(
            deviceKey,
            pipeline);
        _renderPipelines.Add(
            key,
            new CachedRenderPipeline(
                deviceKey,
                (nint)pipeline));
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
            case GpuBlendMode.Modulate:
                blendState.Color = new BlendComponent { SrcFactor = BlendFactor.Dst, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
                blendState.Alpha = new BlendComponent { SrcFactor = BlendFactor.DstAlpha, DstFactor = BlendFactor.Zero, Operation = BlendOperation.Add };
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

    public ComputePipeline* GetOrCreateComputePipeline(
        string key,
        ShaderModule* shaderModule,
        string entryPoint = "main",
        PipelineLayout* pipelineLayout = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(RenderPipelineCache));
        var deviceKey = new WgpuComputePipelineResourceKey(
            key,
            (nint)shaderModule,
            entryPoint,
            (nint)pipelineLayout);
        if (_computePipelines.TryGetValue(
                key,
                out CachedComputePipeline cachedPipeline))
        {
            if (cachedPipeline.CacheKey != deviceKey)
            {
                throw new InvalidOperationException(
                    $"Compute-pipeline cache key '{key}' was reused with a different descriptor.");
            }
            return (ComputePipeline*)cachedPipeline.Handle;
        }

        if (_context.DeviceResourceDomain.TryAcquireComputePipeline(
                deviceKey,
                out ComputePipeline* sharedPipeline))
        {
            _computePipelines.Add(
                key,
                new CachedComputePipeline(
                    deviceKey,
                    (nint)sharedPipeline));
            return sharedPipeline;
        }

        var entryPtr = SilkMarshal.StringToPtr(entryPoint);
        var labelPtr = SilkMarshal.StringToPtr($"Compute_{key}");

        var desc = new ComputePipelineDescriptor
        {
            Label = (byte*)labelPtr,
            Layout = pipelineLayout,
            Compute = new ProgrammableStageDescriptor
            {
                Module = shaderModule,
                EntryPoint = (byte*)entryPtr
            }
        };

        var pipeline = _context.Api.DeviceCreateComputePipeline(_context.Device, &desc);

        SilkMarshal.Free(entryPtr);
        SilkMarshal.Free(labelPtr);

        if (pipeline == null)
        {
            throw new InvalidOperationException($"Failed to create ComputePipeline '{key}'.");
        }

        pipeline = _context.DeviceResourceDomain.PublishComputePipeline(
            deviceKey,
            pipeline);
        _computePipelines.Add(
            key,
            new CachedComputePipeline(
                deviceKey,
                (nint)pipeline));
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
            if (_shaders.Remove(key, out CachedShaderModule shader))
            {
                _context.DeviceResourceDomain.ReleaseShaderModule(
                    shader.CacheKey,
                    (ShaderModule*)shader.Handle,
                    _context);
            }
        }
    }

    public void ReleaseRenderPipeline(string key)
    {
        lock (_context.RenderLock)
        {
            if (_renderPipelines.Remove(
                    key,
                    out CachedRenderPipeline pipeline))
            {
                _context.DeviceResourceDomain.ReleaseRenderPipeline(
                    pipeline.CacheKey,
                    (RenderPipeline*)pipeline.Handle,
                    _context);
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
                    CachedRenderPipeline pipeline =
                        renderPipelineEnumerator.Current;
                    _context.DeviceResourceDomain.ReleaseRenderPipeline(
                        pipeline.CacheKey,
                        (RenderPipeline*)pipeline.Handle,
                        _context);
                }

                var computePipelineEnumerator = _computePipelines.Values.GetEnumerator();
                while (computePipelineEnumerator.MoveNext())
                {
                    CachedComputePipeline pipeline =
                        computePipelineEnumerator.Current;
                    _context.DeviceResourceDomain.ReleaseComputePipeline(
                        pipeline.CacheKey,
                        (ComputePipeline*)pipeline.Handle,
                        _context);
                }

                var shaderModuleEnumerator = _shaders.Values.GetEnumerator();
                while (shaderModuleEnumerator.MoveNext())
                {
                    CachedShaderModule shader = shaderModuleEnumerator.Current;
                    _context.DeviceResourceDomain.ReleaseShaderModule(
                        shader.CacheKey,
                        (ShaderModule*)shader.Handle,
                        _context);
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

    private readonly record struct CachedShaderModule(
        WgpuDeviceResourceDomain.ShaderModuleKey CacheKey,
        nint Handle);

    private readonly record struct CachedRenderPipeline(
        WgpuRenderPipelineResourceKey CacheKey,
        nint Handle);

    private readonly record struct CachedComputePipeline(
        WgpuComputePipelineResourceKey CacheKey,
        nint Handle);
}
