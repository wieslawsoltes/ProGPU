using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

public static unsafe class GpuTextureBlitter
{
    private static readonly string ShaderSource = ShaderResource.Load(typeof(GpuTextureBlitter), "TextureBlitter.wgsl");

    private static readonly object s_cacheLock = new();
    private static readonly Dictionary<WgpuContext, ResourceCache> s_caches = new();

    static GpuTextureBlitter()
    {
        WgpuContext.Disposing += ReleaseCache;
    }

    public static void Blit(
        GpuTexture source,
        TextureView* destinationView,
        TextureFormat destinationFormat,
        Color clearColor = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(GpuTexture));
        }
        if (destinationView == null)
        {
            throw new ArgumentNullException(nameof(destinationView));
        }
        if (!source.Usage.HasFlag(TextureUsage.TextureBinding))
        {
            throw new InvalidOperationException("GPU texture blit requires TextureBinding usage on the source texture.");
        }
        if (source.Dimension != GpuTextureDimension.Dimension2D || source.DepthOrArrayLayers != 1 || source.SampleCount != 1)
        {
            throw new NotSupportedException("GPU texture blit currently supports single-sample 2D textures with one layer.");
        }

        var context = source.Context;
        lock (context.RenderLock)
        {
            if (context.IsDisposed)
            {
                throw new ObjectDisposedException(nameof(WgpuContext));
            }

            BlitCore(source, destinationView, destinationFormat, clearColor);
        }
    }

    private static void BlitCore(
        GpuTexture source,
        TextureView* destinationView,
        TextureFormat destinationFormat,
        Color clearColor)
    {
        var context = source.Context;
        var resources = GetCache(context).GetOrCreate(destinationFormat);
        var wgpu = context.Api;
        BindGroup* bindGroup = null;
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var entries = stackalloc BindGroupEntry[2];
            entries[0] = new BindGroupEntry
            {
                Binding = 0,
                Sampler = resources.Sampler
            };
            entries[1] = new BindGroupEntry
            {
                Binding = 1,
                TextureView = source.ViewPtr
            };
            var bindGroupDescriptor = new BindGroupDescriptor
            {
                Layout = resources.BindGroupLayout,
                EntryCount = 2,
                Entries = entries
            };
            bindGroup = wgpu.DeviceCreateBindGroup(context.Device, &bindGroupDescriptor);
            if (bindGroup == null)
            {
                throw new InvalidOperationException("Failed to create bind group for GPU texture blit.");
            }

            var encoderDescriptor = new CommandEncoderDescriptor();
            encoder = wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
            if (encoder == null)
            {
                throw new InvalidOperationException("Failed to create command encoder for GPU texture blit.");
            }

            var colorAttachment = new RenderPassColorAttachment
            {
                View = destinationView,
                ResolveTarget = null,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = clearColor
            };
            var passDescriptor = new RenderPassDescriptor
            {
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment
            };
            pass = wgpu.CommandEncoderBeginRenderPass(encoder, &passDescriptor);
            if (pass == null)
            {
                throw new InvalidOperationException("Failed to begin render pass for GPU texture blit.");
            }

            wgpu.RenderPassEncoderSetPipeline(pass, resources.Pipeline);
            wgpu.RenderPassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
            wgpu.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
            wgpu.RenderPassEncoderEnd(pass);
            wgpu.RenderPassEncoderRelease(pass);
            pass = null;

            var commandBufferDescriptor = new CommandBufferDescriptor();
            commandBuffer = wgpu.CommandEncoderFinish(encoder, &commandBufferDescriptor);
            if (commandBuffer == null)
            {
                throw new InvalidOperationException("Failed to finish command buffer for GPU texture blit.");
            }

            wgpu.QueueSubmit(context.Queue, 1, &commandBuffer);
        }
        finally
        {
            if (pass != null)
            {
                wgpu.RenderPassEncoderRelease(pass);
            }
            if (commandBuffer != null)
            {
                wgpu.CommandBufferRelease(commandBuffer);
            }
            if (encoder != null)
            {
                wgpu.CommandEncoderRelease(encoder);
            }
            if (bindGroup != null)
            {
                wgpu.BindGroupRelease(bindGroup);
            }
        }
    }

    private static ResourceCache GetCache(WgpuContext context)
    {
        lock (s_cacheLock)
        {
            if (!s_caches.TryGetValue(context, out var cache))
            {
                cache = new ResourceCache(context);
                s_caches.Add(context, cache);
            }

            return cache;
        }
    }

    private static void ReleaseCache(WgpuContext context)
    {
        ResourceCache? cache;
        lock (s_cacheLock)
        {
            if (!s_caches.Remove(context, out cache))
            {
                return;
            }
        }

        cache.QueueDisposal();
    }

    private readonly struct Resources
    {
        public Resources(Sampler* sampler, BindGroupLayout* bindGroupLayout, RenderPipeline* pipeline)
        {
            Sampler = sampler;
            BindGroupLayout = bindGroupLayout;
            Pipeline = pipeline;
        }

        public Sampler* Sampler { get; }
        public BindGroupLayout* BindGroupLayout { get; }
        public RenderPipeline* Pipeline { get; }
    }

    private sealed class ResourceCache
    {
        private readonly WgpuContext _context;
        private readonly object _lock = new();
        private readonly Dictionary<TextureFormat, IntPtr> _pipelines = new();
        private IntPtr _shader;
        private IntPtr _sampler;
        private IntPtr _bindGroupLayout;
        private IntPtr _pipelineLayout;

        public ResourceCache(WgpuContext context)
        {
            _context = context;
        }

        public Resources GetOrCreate(TextureFormat format)
        {
            lock (_lock)
            {
                EnsureCommonResources();
                if (!_pipelines.TryGetValue(format, out var pipeline))
                {
                    pipeline = (IntPtr)CreatePipeline(
                        _context,
                        (ShaderModule*)_shader,
                        (PipelineLayout*)_pipelineLayout,
                        format);
                    _pipelines.Add(format, pipeline);
                }

                return new Resources(
                    (Sampler*)_sampler,
                    (BindGroupLayout*)_bindGroupLayout,
                    (RenderPipeline*)pipeline);
            }
        }

        public void QueueDisposal()
        {
            lock (_lock)
            {
                if (!_context.IsDisposed)
                {
                    foreach (var pipeline in _pipelines.Values)
                    {
                        _context.QueueRenderPipelineDisposal(pipeline);
                    }
                    _context.QueuePipelineLayoutDisposal(_pipelineLayout);
                    _context.QueueBindGroupLayoutDisposal(_bindGroupLayout);
                    _context.QueueSamplerDisposal(_sampler);
                    _context.QueueShaderModuleDisposal(_shader);
                }

                _pipelines.Clear();
                _pipelineLayout = IntPtr.Zero;
                _bindGroupLayout = IntPtr.Zero;
                _sampler = IntPtr.Zero;
                _shader = IntPtr.Zero;
            }
        }

        private void EnsureCommonResources()
        {
            if (_shader != IntPtr.Zero)
            {
                return;
            }

            ShaderModule* shader = null;
            Sampler* sampler = null;
            BindGroupLayout* bindGroupLayout = null;
            PipelineLayout* pipelineLayout = null;
            try
            {
                shader = CreateShader(_context);
                sampler = CreateSampler(_context);
                bindGroupLayout = CreateBindGroupLayout(_context);
                pipelineLayout = CreatePipelineLayout(_context, bindGroupLayout);

                _shader = (IntPtr)shader;
                _sampler = (IntPtr)sampler;
                _bindGroupLayout = (IntPtr)bindGroupLayout;
                _pipelineLayout = (IntPtr)pipelineLayout;
            }
            catch
            {
                if (pipelineLayout != null) _context.Api.PipelineLayoutRelease(pipelineLayout);
                if (bindGroupLayout != null) _context.Api.BindGroupLayoutRelease(bindGroupLayout);
                if (sampler != null) _context.Api.SamplerRelease(sampler);
                if (shader != null) _context.Api.ShaderModuleRelease(shader);
                throw;
            }
        }
    }

    private static ShaderModule* CreateShader(WgpuContext context)
    {
        var sourcePointer = SilkMarshal.StringToPtr(ShaderSource);
        var labelPointer = SilkMarshal.StringToPtr("ProGPU Texture Blitter Shader");
        try
        {
            var wgslDescriptor = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct
                {
                    Next = null,
                    SType = SType.ShaderModuleWgslDescriptor
                },
                Code = (byte*)sourcePointer
            };
            var descriptor = new ShaderModuleDescriptor
            {
                NextInChain = (ChainedStruct*)&wgslDescriptor,
                Label = (byte*)labelPointer
            };
            var shader = context.Api.DeviceCreateShaderModule(context.Device, &descriptor);
            if (shader == null)
            {
                throw new InvalidOperationException("Failed to create GPU texture blitter shader.");
            }
            return shader;
        }
        finally
        {
            SilkMarshal.Free(sourcePointer);
            SilkMarshal.Free(labelPointer);
        }
    }

    private static Sampler* CreateSampler(WgpuContext context)
    {
        var descriptor = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Linear,
            MinFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Nearest,
            LodMinClamp = 0f,
            LodMaxClamp = 0f,
            MaxAnisotropy = 1
        };
        var sampler = context.Api.DeviceCreateSampler(context.Device, &descriptor);
        if (sampler == null)
        {
            throw new InvalidOperationException("Failed to create GPU texture blitter sampler.");
        }
        return sampler;
    }

    private static BindGroupLayout* CreateBindGroupLayout(WgpuContext context)
    {
        var entries = stackalloc BindGroupLayoutEntry[2];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering }
        };
        entries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout
            {
                SampleType = TextureSampleType.Float,
                ViewDimension = TextureViewDimension.Dimension2D,
                Multisampled = false
            }
        };
        var descriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = 2,
            Entries = entries
        };
        var bindGroupLayout = context.Api.DeviceCreateBindGroupLayout(context.Device, &descriptor);
        if (bindGroupLayout == null)
        {
            throw new InvalidOperationException("Failed to create GPU texture blitter bind-group layout.");
        }
        return bindGroupLayout;
    }

    private static PipelineLayout* CreatePipelineLayout(WgpuContext context, BindGroupLayout* bindGroupLayout)
    {
        var layouts = stackalloc BindGroupLayout*[1];
        layouts[0] = bindGroupLayout;
        var descriptor = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 1,
            BindGroupLayouts = layouts
        };
        var pipelineLayout = context.Api.DeviceCreatePipelineLayout(context.Device, &descriptor);
        if (pipelineLayout == null)
        {
            throw new InvalidOperationException("Failed to create GPU texture blitter pipeline layout.");
        }
        return pipelineLayout;
    }

    private static RenderPipeline* CreatePipeline(
        WgpuContext context,
        ShaderModule* shader,
        PipelineLayout* pipelineLayout,
        TextureFormat format)
    {
        var vertexEntryPointer = SilkMarshal.StringToPtr("vs_main");
        var fragmentEntryPointer = SilkMarshal.StringToPtr("fs_main");
        var labelPointer = SilkMarshal.StringToPtr("ProGPU Texture Blitter Pipeline");
        try
        {
            var vertexState = new VertexState
            {
                Module = shader,
                EntryPoint = (byte*)vertexEntryPointer
            };
            var colorTarget = new ColorTargetState
            {
                Format = format,
                Blend = null,
                WriteMask = ColorWriteMask.All
            };
            var fragmentState = new FragmentState
            {
                Module = shader,
                EntryPoint = (byte*)fragmentEntryPointer,
                TargetCount = 1,
                Targets = &colorTarget
            };
            var descriptor = new RenderPipelineDescriptor
            {
                Label = (byte*)labelPointer,
                Layout = pipelineLayout,
                Vertex = vertexState,
                Primitive = new PrimitiveState
                {
                    Topology = PrimitiveTopology.TriangleList,
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = FrontFace.Ccw,
                    CullMode = CullMode.None
                },
                Multisample = new MultisampleState
                {
                    Count = 1,
                    Mask = uint.MaxValue,
                    AlphaToCoverageEnabled = false
                },
                Fragment = &fragmentState
            };
            var pipeline = context.Api.DeviceCreateRenderPipeline(context.Device, &descriptor);
            if (pipeline == null)
            {
                throw new InvalidOperationException($"Failed to create GPU texture blitter pipeline for {format}.");
            }
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(vertexEntryPointer);
            SilkMarshal.Free(fragmentEntryPointer);
            SilkMarshal.Free(labelPointer);
        }
    }
}
