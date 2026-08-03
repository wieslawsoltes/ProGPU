using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Normalized placement and opacity for one GPU texture layer.
/// Coordinates are relative to the destination extent and may extend outside
/// it; WebGPU raster clipping bounds the actual fragment work.
/// </summary>
public readonly record struct GpuTextureLayerPlacement(
    float X,
    float Y,
    float Width,
    float Height,
    float Opacity);

/// <summary>
/// Retained WebGPU source-over compositor for straight-alpha texture layers.
/// </summary>
/// <remarks>
/// Pipeline, sampler, uniform buffer, and one bind group per stable source
/// texture view are retained for the compositor lifetime. A warmed layer
/// performs one 80-byte queue write, one render pass, and one queue
/// submission with no managed allocation or texture creation. Work is O(P)
/// for P covered destination pixels and retained native state is
/// O(min(S, 64)) for S distinct source texture views. The caller controls
/// source and destination lifetimes and must not alias their subresources.
/// </remarks>
public sealed unsafe class GpuTextureLayerCompositor :
    IDisposable
{
    private const int MaxRetainedSourceBindings = 64;
    private const uint UniformByteSize = 80;
    private static readonly string ShaderSource =
        ShaderResource.Load(
            typeof(GpuTextureLayerCompositor),
            "TextureLayerCompositor.wgsl");

    private readonly Dictionary<ulong, SourceBinding>
        _sourceBindings =
            new(MaxRetainedSourceBindings);
    private readonly Queue<ulong> _sourceBindingOrder =
        new(MaxRetainedSourceBindings);
    private readonly WgpuContext _context;
    private readonly TextureFormat _destinationFormat;
    private GpuBuffer? _uniform;
    private nint _shader;
    private nint _sampler;
    private nint _bindGroupLayout;
    private nint _pipelineLayout;
    private nint _pipeline;
    private bool _disposed;

    public GpuTextureLayerCompositor(
        WgpuContext context,
        TextureFormat destinationFormat)
    {
        _context =
            context ??
            throw new ArgumentNullException(
                nameof(context));
        _destinationFormat = destinationFormat;
        lock (_context.RenderLock)
        {
            ObjectDisposedException.ThrowIf(
                _context.IsDisposed,
                _context);
            CreateResources();
        }
    }

    public void Composite(
        GpuTexture source,
        TextureView* destinationView,
        in GpuTextureLayerPlacement placement,
        in GpuTextureColorTransform colorTransform)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
        Validate(
            source,
            destinationView,
            placement);
        if (placement.Opacity == 0f)
        {
            return;
        }

        lock (_context.RenderLock)
        {
            ObjectDisposedException.ThrowIf(
                _context.IsDisposed,
                _context);
            BindGroup* bindGroup =
                GetOrCreateSourceBinding(source);
            CompositeCore(
                destinationView,
                bindGroup,
                placement,
                colorTransform);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_context.RenderLock)
        {
            if (!_context.IsDisposed)
            {
                foreach (SourceBinding binding
                         in _sourceBindings.Values)
                {
                    _context.QueueBindGroupDisposal(
                        binding.BindGroup);
                }
                _context.QueueRenderPipelineDisposal(
                    _pipeline);
                _context.QueuePipelineLayoutDisposal(
                    _pipelineLayout);
                _context.QueueBindGroupLayoutDisposal(
                    _bindGroupLayout);
                _context.QueueSamplerDisposal(
                    _sampler);
                _context.QueueShaderModuleDisposal(
                    _shader);
            }
            _sourceBindings.Clear();
            _sourceBindingOrder.Clear();
            _uniform?.Dispose();
            _uniform = null;
            _pipeline = 0;
            _pipelineLayout = 0;
            _bindGroupLayout = 0;
            _sampler = 0;
            _shader = 0;
        }
    }

    private void Validate(
        GpuTexture source,
        TextureView* destinationView,
        in GpuTextureLayerPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(
            source.IsDisposed,
            source);
        if (destinationView == null)
        {
            throw new ArgumentNullException(
                nameof(destinationView));
        }
        if (!ReferenceEquals(
                source.Context,
                _context))
        {
            throw new ArgumentException(
                "Layer source and destination must belong to the compositor device.",
                nameof(source));
        }
        if (destinationView == source.ViewPtr)
        {
            throw new ArgumentException(
                "A texture layer cannot sample from the destination subresource.",
                nameof(destinationView));
        }
        if ((source.Usage &
             TextureUsage.TextureBinding) == 0)
        {
            throw new ArgumentException(
                "A texture layer source requires TextureBinding usage.",
                nameof(source));
        }
        if (source.Dimension !=
                GpuTextureDimension.Dimension2D ||
            source.DepthOrArrayLayers != 1 ||
            source.SampleCount != 1)
        {
            throw new NotSupportedException(
                "Texture-layer composition supports single-sample 2D textures with one layer.");
        }
        if (source.AlphaMode !=
            GpuTextureAlphaMode.Straight)
        {
            throw new NotSupportedException(
                "Texture-layer composition currently requires a straight-alpha source.");
        }
        if (!float.IsFinite(placement.X) ||
            !float.IsFinite(placement.Y) ||
            !float.IsFinite(placement.Width) ||
            !float.IsFinite(placement.Height) ||
            !float.IsFinite(placement.Opacity) ||
            !float.IsFinite(
                placement.X + placement.Width) ||
            !float.IsFinite(
                placement.Y + placement.Height) ||
            !float.IsFinite(placement.X * 2f) ||
            !float.IsFinite(placement.Y * 2f) ||
            !float.IsFinite(
                (placement.X + placement.Width) *
                2f) ||
            !float.IsFinite(
                (placement.Y + placement.Height) *
                2f) ||
            placement.Width <= 0f ||
            placement.Height <= 0f ||
            placement.Opacity is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement));
        }
    }

    private void CompositeCore(
        TextureView* destinationView,
        BindGroup* bindGroup,
        in GpuTextureLayerPlacement placement,
        in GpuTextureColorTransform colorTransform)
    {
        Span<float> values =
            stackalloc float[20]
            {
                placement.X,
                placement.Y,
                placement.Width,
                placement.Height,
                colorTransform.Red.X,
                colorTransform.Red.Y,
                colorTransform.Red.Z,
                colorTransform.Red.W,
                colorTransform.Green.X,
                colorTransform.Green.Y,
                colorTransform.Green.Z,
                colorTransform.Green.W,
                colorTransform.Blue.X,
                colorTransform.Blue.Y,
                colorTransform.Blue.Z,
                colorTransform.Blue.W,
                placement.Opacity,
                0f,
                0f,
                0f
            };
        _uniform!.Write<float>(values);

        IWebGpuApi wgpu = _context.Api;
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var encoderDescriptor =
                new CommandEncoderDescriptor();
            encoder =
                wgpu.DeviceCreateCommandEncoder(
                    _context.Device,
                    &encoderDescriptor);
            if (encoder == null)
            {
                throw new InvalidOperationException(
                    "Failed to create the texture-layer command encoder.");
            }
            var colorAttachment =
                new RenderPassColorAttachment
                {
                    View = destinationView,
                    ResolveTarget = null,
                    LoadOp = LoadOp.Load,
                    StoreOp = StoreOp.Store,
                };
            var passDescriptor =
                new RenderPassDescriptor
                {
                    ColorAttachmentCount = 1,
                    ColorAttachments =
                        &colorAttachment
                };
            pass =
                wgpu.CommandEncoderBeginRenderPass(
                    encoder,
                    &passDescriptor);
            if (pass == null)
            {
                throw new InvalidOperationException(
                    "Failed to begin the texture-layer render pass.");
            }
            wgpu.RenderPassEncoderSetPipeline(
                pass,
                (RenderPipeline*)_pipeline);
            wgpu.RenderPassEncoderSetBindGroup(
                pass,
                0,
                bindGroup,
                0,
                null);
            wgpu.RenderPassEncoderDraw(
                pass,
                6,
                1,
                0,
                0);
            wgpu.RenderPassEncoderEnd(pass);
            wgpu.RenderPassEncoderRelease(pass);
            pass = null;

            var commandBufferDescriptor =
                new CommandBufferDescriptor();
            commandBuffer =
                wgpu.CommandEncoderFinish(
                    encoder,
                    &commandBufferDescriptor);
            if (commandBuffer == null)
            {
                throw new InvalidOperationException(
                    "Failed to finish the texture-layer command buffer.");
            }
            _context.Submit(
                1,
                &commandBuffer);
        }
        finally
        {
            if (pass != null)
            {
                wgpu.RenderPassEncoderRelease(pass);
            }
            if (commandBuffer != null)
            {
                wgpu.CommandBufferRelease(
                    commandBuffer);
            }
            if (encoder != null)
            {
                wgpu.CommandEncoderRelease(encoder);
            }
        }
    }

    private BindGroup* GetOrCreateSourceBinding(
        GpuTexture source)
    {
        bool bindingExists =
            _sourceBindings.TryGetValue(
                source.Id,
                out SourceBinding binding);
        if (bindingExists &&
            binding.ViewGeneration ==
                source.ViewGeneration)
        {
            return (BindGroup*)binding.BindGroup;
        }
        if (binding.BindGroup != 0)
        {
            _context.QueueBindGroupDisposal(
                binding.BindGroup);
        }
        if (!bindingExists)
        {
            while (_sourceBindings.Count >=
                   MaxRetainedSourceBindings)
            {
                ulong evictedId =
                    _sourceBindingOrder.Dequeue();
                if (_sourceBindings.Remove(
                        evictedId,
                        out SourceBinding evicted))
                {
                    _context.QueueBindGroupDisposal(
                        evicted.BindGroup);
                }
            }
            _sourceBindingOrder.Enqueue(source.Id);
        }

        var entries =
            stackalloc BindGroupEntry[3];
        entries[0] =
            new BindGroupEntry
            {
                Binding = 0,
                Sampler = (Sampler*)_sampler
            };
        entries[1] =
            new BindGroupEntry
            {
                Binding = 1,
                TextureView = source.ViewPtr
            };
        entries[2] =
            new BindGroupEntry
            {
                Binding = 2,
                Buffer = _uniform!.BufferPtr,
                Offset = 0,
                Size = UniformByteSize
            };
        var descriptor =
            new BindGroupDescriptor
            {
                Layout =
                    (BindGroupLayout*)
                    _bindGroupLayout,
                EntryCount = 3,
                Entries = entries
            };
        BindGroup* bindGroup =
            _context.Api.DeviceCreateBindGroup(
                _context.Device,
                &descriptor);
        if (bindGroup == null)
        {
            throw new InvalidOperationException(
                "Failed to create the retained texture-layer bind group.");
        }
        _sourceBindings[source.Id] =
            new SourceBinding(
                source.ViewGeneration,
                (nint)bindGroup);
        return bindGroup;
    }

    private void CreateResources()
    {
        ShaderModule* shader = null;
        Sampler* sampler = null;
        BindGroupLayout* bindGroupLayout = null;
        PipelineLayout* pipelineLayout = null;
        RenderPipeline* pipeline = null;
        try
        {
            shader = CreateShader();
            sampler = CreateSampler();
            bindGroupLayout =
                CreateBindGroupLayout();
            pipelineLayout =
                CreatePipelineLayout(
                    bindGroupLayout);
            pipeline =
                CreatePipeline(
                    shader,
                    pipelineLayout);
            _uniform =
                new GpuBuffer(
                    _context,
                    UniformByteSize,
                    BufferUsage.Uniform |
                        BufferUsage.CopyDst,
                    "Texture Layer Parameters");
            _shader = (nint)shader;
            _sampler = (nint)sampler;
            _bindGroupLayout =
                (nint)bindGroupLayout;
            _pipelineLayout =
                (nint)pipelineLayout;
            _pipeline = (nint)pipeline;
        }
        catch
        {
            _uniform?.Dispose();
            _uniform = null;
            if (pipeline != null)
            {
                _context.Api
                    .RenderPipelineRelease(
                        pipeline);
            }
            if (pipelineLayout != null)
            {
                _context.Api
                    .PipelineLayoutRelease(
                        pipelineLayout);
            }
            if (bindGroupLayout != null)
            {
                _context.Api
                    .BindGroupLayoutRelease(
                        bindGroupLayout);
            }
            if (sampler != null)
            {
                _context.Api.SamplerRelease(
                    sampler);
            }
            if (shader != null)
            {
                _context.Api.ShaderModuleRelease(
                    shader);
            }
            throw;
        }
    }

    private ShaderModule* CreateShader()
    {
        nint sourcePointer =
            SilkMarshal.StringToPtr(
                ShaderSource);
        nint labelPointer =
            SilkMarshal.StringToPtr(
                "ProGPU Texture Layer Shader");
        try
        {
            var wgsl =
                new ShaderModuleWGSLDescriptor
                {
                    Chain =
                        new ChainedStruct
                        {
                            SType =
                                SType
                                    .ShaderModuleWgslDescriptor
                        },
                    Code = (byte*)sourcePointer
                };
            var descriptor =
                new ShaderModuleDescriptor
                {
                    NextInChain =
                        (ChainedStruct*)&wgsl,
                    Label = (byte*)labelPointer
                };
            ShaderModule* shader =
                _context.Api
                    .DeviceCreateShaderModule(
                        _context.Device,
                        &descriptor);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Failed to create the texture-layer shader.");
            }
            return shader;
        }
        finally
        {
            SilkMarshal.Free(sourcePointer);
            SilkMarshal.Free(labelPointer);
        }
    }

    private Sampler* CreateSampler()
    {
        var descriptor =
            new SamplerDescriptor
            {
                AddressModeU =
                    AddressMode.ClampToEdge,
                AddressModeV =
                    AddressMode.ClampToEdge,
                AddressModeW =
                    AddressMode.ClampToEdge,
                MagFilter = FilterMode.Linear,
                MinFilter = FilterMode.Linear,
                MipmapFilter =
                    MipmapFilterMode.Nearest,
                LodMinClamp = 0f,
                LodMaxClamp = 0f,
                MaxAnisotropy = 1
            };
        Sampler* sampler =
            _context.Api.DeviceCreateSampler(
                _context.Device,
                &descriptor);
        if (sampler == null)
        {
            throw new InvalidOperationException(
                "Failed to create the texture-layer sampler.");
        }
        return sampler;
    }

    private BindGroupLayout*
        CreateBindGroupLayout()
    {
        var entries =
            stackalloc BindGroupLayoutEntry[3];
        entries[0] =
            new BindGroupLayoutEntry
            {
                Binding = 0,
                Visibility =
                    ShaderStage.Fragment,
                Sampler =
                    new SamplerBindingLayout
                    {
                        Type =
                            SamplerBindingType
                                .Filtering
                    }
            };
        entries[1] =
            new BindGroupLayoutEntry
            {
                Binding = 1,
                Visibility =
                    ShaderStage.Fragment,
                Texture =
                    new TextureBindingLayout
                    {
                        SampleType =
                            TextureSampleType.Float,
                        ViewDimension =
                            TextureViewDimension
                                .Dimension2D
                    }
            };
        entries[2] =
            new BindGroupLayoutEntry
            {
                Binding = 2,
                Visibility =
                    ShaderStage.Vertex |
                    ShaderStage.Fragment,
                Buffer =
                    new BufferBindingLayout
                    {
                        Type =
                            BufferBindingType.Uniform,
                        MinBindingSize =
                            UniformByteSize
                    }
            };
        var descriptor =
            new BindGroupLayoutDescriptor
            {
                EntryCount = 3,
                Entries = entries
            };
        BindGroupLayout* layout =
            _context.Api
                .DeviceCreateBindGroupLayout(
                    _context.Device,
                    &descriptor);
        if (layout == null)
        {
            throw new InvalidOperationException(
                "Failed to create the texture-layer bind-group layout.");
        }
        return layout;
    }

    private PipelineLayout* CreatePipelineLayout(
        BindGroupLayout* bindGroupLayout)
    {
        var layouts =
            stackalloc BindGroupLayout*[1];
        layouts[0] = bindGroupLayout;
        var descriptor =
            new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 1,
                BindGroupLayouts = layouts
            };
        PipelineLayout* layout =
            _context.Api
                .DeviceCreatePipelineLayout(
                    _context.Device,
                    &descriptor);
        if (layout == null)
        {
            throw new InvalidOperationException(
                "Failed to create the texture-layer pipeline layout.");
        }
        return layout;
    }

    private RenderPipeline* CreatePipeline(
        ShaderModule* shader,
        PipelineLayout* pipelineLayout)
    {
        nint vertexEntry =
            SilkMarshal.StringToPtr("vs_main");
        nint fragmentEntry =
            SilkMarshal.StringToPtr("fs_main");
        nint label =
            SilkMarshal.StringToPtr(
                "ProGPU Texture Layer Pipeline");
        try
        {
            var vertex =
                new VertexState
                {
                    Module = shader,
                    EntryPoint =
                        (byte*)vertexEntry
                };
            var blend =
                new BlendState
                {
                    Color =
                        new BlendComponent
                        {
                            SrcFactor =
                                BlendFactor.One,
                            DstFactor =
                                BlendFactor
                                    .OneMinusSrcAlpha,
                            Operation =
                                BlendOperation.Add
                        },
                    Alpha =
                        new BlendComponent
                        {
                            SrcFactor =
                                BlendFactor.One,
                            DstFactor =
                                BlendFactor
                                    .OneMinusSrcAlpha,
                            Operation =
                                BlendOperation.Add
                        }
                };
            var target =
                new ColorTargetState
                {
                    Format =
                        _destinationFormat,
                    Blend = &blend,
                    WriteMask =
                        ColorWriteMask.All
                };
            var fragment =
                new FragmentState
                {
                    Module = shader,
                    EntryPoint =
                        (byte*)fragmentEntry,
                    TargetCount = 1,
                    Targets = &target
                };
            var descriptor =
                new RenderPipelineDescriptor
                {
                    Label = (byte*)label,
                    Layout = pipelineLayout,
                    Vertex = vertex,
                    Primitive =
                        new PrimitiveState
                        {
                            Topology =
                                PrimitiveTopology
                                    .TriangleList,
                            StripIndexFormat =
                                IndexFormat.Undefined,
                            FrontFace =
                                FrontFace.Ccw,
                            CullMode =
                                CullMode.None
                        },
                    Multisample =
                        new MultisampleState
                        {
                            Count = 1,
                            Mask = uint.MaxValue
                        },
                    Fragment = &fragment
                };
            RenderPipeline* pipeline =
                _context.Api
                    .DeviceCreateRenderPipeline(
                        _context.Device,
                        &descriptor);
            if (pipeline == null)
            {
                throw new InvalidOperationException(
                    $"Failed to create the texture-layer pipeline for {_destinationFormat}.");
            }
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(vertexEntry);
            SilkMarshal.Free(fragmentEntry);
            SilkMarshal.Free(label);
        }
    }

    private readonly record struct SourceBinding(
        uint ViewGeneration,
        nint BindGroup);
}
