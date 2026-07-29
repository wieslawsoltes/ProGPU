using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using System.Numerics;

namespace ProGPU.Backend;

/// <summary>
/// Resamples a BT.709 NV12 frame into renderable NV12 planes while applying
/// fused saturation and grayscale effects entirely on WebGPU.
/// </summary>
/// <remarks>
/// Both plane passes use one command buffer and one queue submission. Work is
/// O(P) for P output luma pixels, storage is bounded by retained pipeline
/// state, and no pixel is read back or uploaded through the CPU.
/// </remarks>
public static unsafe class GpuNv12Processor
{
    public const int MaxInFlightSlots = 3;
    private const uint UniformStride = 256;
    private static readonly string ShaderSource =
        ShaderResource.Load(
            typeof(GpuNv12Processor),
            "Nv12GpuProcessor.wgsl");
    private static readonly object s_cacheLock = new();
    private static readonly Dictionary<WgpuContext, ResourceCache>
        s_caches = new();

    static GpuNv12Processor()
    {
        WgpuContext.Disposing += ReleaseCache;
    }

    /// <summary>
    /// Generates a limited-range BT.709 NV12 solid-color frame directly into
    /// renderable luma/chroma planes.
    /// </summary>
    /// <remarks>
    /// Saturation and grayscale are constant-folded once on the CPU; the
    /// full-frame luma and chroma writes are two attachment clears in one
    /// WebGPU command buffer. CPU work/storage are O(1), GPU bandwidth is
    /// O(P), and no pixel buffer is uploaded, mapped, or read back.
    /// </remarks>
    public static void RenderSolidColor(
        GpuTexture destinationLuma,
        GpuTexture destinationChroma,
        uint argbColor,
        float saturation,
        float grayscale) =>
        RenderSolidColor(
            destinationLuma,
            destinationChroma,
            argbColor,
            GpuTextureColorTransform
                .CreateSaturationGrayscale(
                    saturation,
                    grayscale));

    public static void RenderSolidColor(
        GpuTexture destinationLuma,
        GpuTexture destinationChroma,
        uint argbColor,
        GpuTextureColorTransform transform)
    {
        ArgumentNullException.ThrowIfNull(destinationLuma);
        ArgumentNullException.ThrowIfNull(destinationChroma);
        if (destinationLuma.IsDisposed ||
            destinationChroma.IsDisposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuTexture));
        }
        WgpuContext context = destinationLuma.Context;
        if (!ReferenceEquals(
                context,
                destinationChroma.Context))
        {
            throw new InvalidOperationException(
                "NV12 solid-color rendering requires one WebGPU device domain.");
        }
        if (destinationLuma.Format !=
                TextureFormat.R8Unorm ||
            destinationChroma.Format !=
                TextureFormat.RG8Unorm ||
            destinationLuma.Dimension !=
                GpuTextureDimension.Dimension2D ||
            destinationChroma.Dimension !=
                GpuTextureDimension.Dimension2D ||
            destinationLuma.DepthOrArrayLayers != 1 ||
            destinationChroma.DepthOrArrayLayers != 1 ||
            destinationLuma.SampleCount != 1 ||
            destinationChroma.SampleCount != 1 ||
            destinationChroma.Width !=
                (destinationLuma.Width + 1) / 2 ||
            destinationChroma.Height !=
                (destinationLuma.Height + 1) / 2)
        {
            throw new NotSupportedException(
                "NV12 solid-color rendering requires full-size R8 luma and half-size RG8 chroma planes.");
        }
        if (!destinationLuma.Usage.HasFlag(
                TextureUsage.RenderAttachment) ||
            !destinationChroma.Usage.HasFlag(
                TextureUsage.RenderAttachment))
        {
            throw new InvalidOperationException(
                "NV12 solid-color destinations must be render attachments.");
        }
        GetEncodedSolidColor(
            argbColor,
            transform,
            out Color luma,
            out Color chroma);
        lock (context.RenderLock)
        {
            ObjectDisposedException.ThrowIf(
                context.IsDisposed,
                context);
            RenderSolidColorCore(
                context,
                destinationLuma,
                destinationChroma,
                luma,
                chroma);
        }
    }

    public static void Process(
        GpuTexture sourceLuma,
        GpuTexture sourceChroma,
        GpuTexture destinationLuma,
        GpuTexture destinationChroma,
        float saturation,
        float grayscale,
        int inFlightSlot) =>
        Process(
            sourceLuma,
            sourceChroma,
            destinationLuma,
            destinationChroma,
            GpuTextureColorTransform
                .CreateSaturationGrayscale(
                    saturation,
                    grayscale),
            inFlightSlot);

    public static void Process(
        GpuTexture sourceLuma,
        GpuTexture sourceChroma,
        GpuTexture destinationLuma,
        GpuTexture destinationChroma,
        GpuTextureColorTransform transform,
        int inFlightSlot)
    {
        ArgumentNullException.ThrowIfNull(sourceLuma);
        ArgumentNullException.ThrowIfNull(sourceChroma);
        ArgumentNullException.ThrowIfNull(destinationLuma);
        ArgumentNullException.ThrowIfNull(destinationChroma);
        if (sourceLuma.IsDisposed ||
            sourceChroma.IsDisposed ||
            destinationLuma.IsDisposed ||
            destinationChroma.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(GpuTexture));
        }
        WgpuContext context = sourceLuma.Context;
        if (!ReferenceEquals(context, sourceChroma.Context) ||
            !ReferenceEquals(context, destinationLuma.Context) ||
            !ReferenceEquals(context, destinationChroma.Context))
        {
            throw new InvalidOperationException(
                "NV12 processing requires one WebGPU device domain.");
        }
        if (sourceLuma.Format != TextureFormat.R8Unorm ||
            destinationLuma.Format != TextureFormat.R8Unorm ||
            sourceChroma.Format != TextureFormat.RG8Unorm ||
            destinationChroma.Format != TextureFormat.RG8Unorm ||
            sourceChroma.Width != (sourceLuma.Width + 1) / 2 ||
            sourceChroma.Height != (sourceLuma.Height + 1) / 2 ||
            destinationChroma.Width !=
                (destinationLuma.Width + 1) / 2 ||
            destinationChroma.Height !=
                (destinationLuma.Height + 1) / 2)
        {
            throw new NotSupportedException(
                "NV12 processing requires independently valid full-size R8 luma and half-size RG8 chroma plane pairs.");
        }
        if (!sourceLuma.Usage.HasFlag(TextureUsage.TextureBinding) ||
            !sourceChroma.Usage.HasFlag(TextureUsage.TextureBinding) ||
            !destinationLuma.Usage.HasFlag(
                TextureUsage.RenderAttachment) ||
            !destinationChroma.Usage.HasFlag(
                TextureUsage.RenderAttachment))
        {
            throw new InvalidOperationException(
                "NV12 source planes must be sampleable and destination planes must be render attachments.");
        }
        if ((uint)inFlightSlot >=
            MaxInFlightSlots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inFlightSlot));
        }

        lock (context.RenderLock)
        {
            ObjectDisposedException.ThrowIf(
                context.IsDisposed,
                context);
            ProcessCore(
                context,
                sourceLuma,
                sourceChroma,
                destinationLuma,
                destinationChroma,
                transform,
                inFlightSlot);
        }
    }

    /// <summary>
    /// Resamples limited-range BT.709 NV12 into an RGBA render target while
    /// applying fused saturation and grayscale effects.
    /// </summary>
    /// <remarks>
    /// One fullscreen render pass performs O(P) GPU work for P destination
    /// pixels. State is retained per WebGPU device and no source pixel is
    /// mapped or copied through the CPU.
    /// </remarks>
    public static void ProcessToRgba(
        GpuTexture sourceLuma,
        GpuTexture sourceChroma,
        GpuTexture destination,
        float saturation,
        float grayscale,
        int inFlightSlot) =>
        ProcessToRgba(
            sourceLuma,
            sourceChroma,
            destination,
            GpuTextureColorTransform
                .CreateSaturationGrayscale(
                    saturation,
                    grayscale),
            inFlightSlot);

    public static void ProcessToRgba(
        GpuTexture sourceLuma,
        GpuTexture sourceChroma,
        GpuTexture destination,
        GpuTextureColorTransform transform,
        int inFlightSlot)
    {
        ArgumentNullException.ThrowIfNull(sourceLuma);
        ArgumentNullException.ThrowIfNull(sourceChroma);
        ArgumentNullException.ThrowIfNull(destination);
        if (sourceLuma.IsDisposed ||
            sourceChroma.IsDisposed ||
            destination.IsDisposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuTexture));
        }
        WgpuContext context = sourceLuma.Context;
        if (!ReferenceEquals(context, sourceChroma.Context) ||
            !ReferenceEquals(context, destination.Context))
        {
            throw new InvalidOperationException(
                "NV12-to-RGBA processing requires one WebGPU device domain.");
        }
        if (sourceLuma.Format != TextureFormat.R8Unorm ||
            sourceChroma.Format != TextureFormat.RG8Unorm ||
            sourceChroma.Width !=
                (sourceLuma.Width + 1) / 2 ||
            sourceChroma.Height !=
                (sourceLuma.Height + 1) / 2 ||
            destination.Format !=
                TextureFormat.Rgba8Unorm)
        {
            throw new NotSupportedException(
                "NV12-to-RGBA processing requires R8/RG8 source planes and an RGBA8 destination.");
        }
        if (!sourceLuma.Usage.HasFlag(
                TextureUsage.TextureBinding) ||
            !sourceChroma.Usage.HasFlag(
                TextureUsage.TextureBinding) ||
            !destination.Usage.HasFlag(
                TextureUsage.RenderAttachment))
        {
            throw new InvalidOperationException(
                "NV12 source planes must be sampleable and the RGBA destination must be a render attachment.");
        }
        if ((uint)inFlightSlot >= MaxInFlightSlots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inFlightSlot));
        }

        lock (context.RenderLock)
        {
            ObjectDisposedException.ThrowIf(
                context.IsDisposed,
                context);
            ProcessRgbaCore(
                context,
                sourceLuma,
                sourceChroma,
                destination,
                transform,
                inFlightSlot);
        }
    }

    private static void ProcessCore(
        WgpuContext context,
        GpuTexture sourceLuma,
        GpuTexture sourceChroma,
        GpuTexture destinationLuma,
        GpuTexture destinationChroma,
        GpuTextureColorTransform transform,
        int inFlightSlot)
    {
        Resources resources =
            GetCache(context).GetOrCreate();
        IWebGpuApi wgpu = context.Api;
        BindGroup* bindGroup = null;
        CommandEncoder* encoder = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            Span<float> values =
                stackalloc float[16]
                {
                    1f / sourceLuma.Width,
                    1f / sourceLuma.Height,
                    0f,
                    0f,
                    transform.Red.X,
                    transform.Red.Y,
                    transform.Red.Z,
                    transform.Red.W,
                    transform.Green.X,
                    transform.Green.Y,
                    transform.Green.Z,
                    transform.Green.W,
                    transform.Blue.X,
                    transform.Blue.Y,
                    transform.Blue.Z,
                    transform.Blue.W
                };
            uint uniformOffset = checked(
                (uint)inFlightSlot *
                UniformStride);
            resources.Uniform.Write<float>(
                values,
                uniformOffset);

            BindGroupEntry* entries =
                stackalloc BindGroupEntry[4];
            entries[0] = new BindGroupEntry
            {
                Binding = 0,
                Sampler = resources.Sampler
            };
            entries[1] = new BindGroupEntry
            {
                Binding = 1,
                TextureView = sourceLuma.ViewPtr
            };
            entries[2] = new BindGroupEntry
            {
                Binding = 2,
                TextureView = sourceChroma.ViewPtr
            };
            entries[3] = new BindGroupEntry
            {
                Binding = 3,
                Buffer = resources.Uniform.BufferPtr,
                Offset = uniformOffset,
                Size = 64
            };
            var bindGroupDescriptor =
                new BindGroupDescriptor
                {
                    Layout = resources.BindGroupLayout,
                    EntryCount = 4,
                    Entries = entries
                };
            bindGroup = wgpu.DeviceCreateBindGroup(
                context.Device,
                &bindGroupDescriptor);
            if (bindGroup == null)
            {
                throw new InvalidOperationException(
                    "Failed to create the NV12 processor bind group.");
            }

            var encoderDescriptor =
                new CommandEncoderDescriptor();
            encoder = wgpu.DeviceCreateCommandEncoder(
                context.Device,
                &encoderDescriptor);
            if (encoder == null)
            {
                throw new InvalidOperationException(
                    "Failed to create the NV12 processor command encoder.");
            }

            EncodePlane(
                wgpu,
                encoder,
                bindGroup,
                resources.LumaPipeline,
                destinationLuma.ViewPtr);
            EncodePlane(
                wgpu,
                encoder,
                bindGroup,
                resources.ChromaPipeline,
                destinationChroma.ViewPtr);

            var commandBufferDescriptor =
                new CommandBufferDescriptor();
            commandBuffer = wgpu.CommandEncoderFinish(
                encoder,
                &commandBufferDescriptor);
            if (commandBuffer == null)
            {
                throw new InvalidOperationException(
                    "Failed to finish the NV12 processor command buffer.");
            }
            wgpu.QueueSubmit(
                context.Queue,
                1,
                &commandBuffer);
        }
        finally
        {
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

    private static void ProcessRgbaCore(
        WgpuContext context,
        GpuTexture sourceLuma,
        GpuTexture sourceChroma,
        GpuTexture destination,
        GpuTextureColorTransform transform,
        int inFlightSlot)
    {
        Resources resources =
            GetCache(context).GetOrCreate(
                includeRgbaPipeline: true);
        IWebGpuApi wgpu = context.Api;
        BindGroup* bindGroup = null;
        CommandEncoder* encoder = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            Span<float> values =
                stackalloc float[16]
                {
                    1f / sourceLuma.Width,
                    1f / sourceLuma.Height,
                    0f,
                    0f,
                    transform.Red.X,
                    transform.Red.Y,
                    transform.Red.Z,
                    transform.Red.W,
                    transform.Green.X,
                    transform.Green.Y,
                    transform.Green.Z,
                    transform.Green.W,
                    transform.Blue.X,
                    transform.Blue.Y,
                    transform.Blue.Z,
                    transform.Blue.W
                };
            uint uniformOffset = checked(
                (uint)inFlightSlot * UniformStride);
            resources.Uniform.Write<float>(
                values,
                uniformOffset);

            BindGroupEntry* entries =
                stackalloc BindGroupEntry[4];
            entries[0] = new BindGroupEntry
            {
                Binding = 0,
                Sampler = resources.Sampler
            };
            entries[1] = new BindGroupEntry
            {
                Binding = 1,
                TextureView = sourceLuma.ViewPtr
            };
            entries[2] = new BindGroupEntry
            {
                Binding = 2,
                TextureView = sourceChroma.ViewPtr
            };
            entries[3] = new BindGroupEntry
            {
                Binding = 3,
                Buffer = resources.Uniform.BufferPtr,
                Offset = uniformOffset,
                Size = 64
            };
            var bindGroupDescriptor =
                new BindGroupDescriptor
                {
                    Layout = resources.BindGroupLayout,
                    EntryCount = 4,
                    Entries = entries
                };
            bindGroup = wgpu.DeviceCreateBindGroup(
                context.Device,
                &bindGroupDescriptor);
            if (bindGroup == null)
            {
                throw new InvalidOperationException(
                    "Failed to create the NV12-to-RGBA bind group.");
            }

            var encoderDescriptor =
                new CommandEncoderDescriptor();
            encoder = wgpu.DeviceCreateCommandEncoder(
                context.Device,
                &encoderDescriptor);
            if (encoder == null)
            {
                throw new InvalidOperationException(
                    "Failed to create the NV12-to-RGBA command encoder.");
            }
            EncodePlane(
                wgpu,
                encoder,
                bindGroup,
                resources.RgbaPipeline,
                destination.ViewPtr);

            var commandBufferDescriptor =
                new CommandBufferDescriptor();
            commandBuffer = wgpu.CommandEncoderFinish(
                encoder,
                &commandBufferDescriptor);
            if (commandBuffer == null)
            {
                throw new InvalidOperationException(
                    "Failed to finish the NV12-to-RGBA command buffer.");
            }
            wgpu.QueueSubmit(
                context.Queue,
                1,
                &commandBuffer);
        }
        finally
        {
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

    private static void RenderSolidColorCore(
        WgpuContext context,
        GpuTexture destinationLuma,
        GpuTexture destinationChroma,
        Color luma,
        Color chroma)
    {
        IWebGpuApi wgpu = context.Api;
        CommandEncoder* encoder = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var encoderDescriptor =
                new CommandEncoderDescriptor();
            encoder = wgpu.DeviceCreateCommandEncoder(
                context.Device,
                &encoderDescriptor);
            if (encoder == null)
            {
                throw new InvalidOperationException(
                    "Failed to create the NV12 solid-color command encoder.");
            }

            EncodeClearPlane(
                wgpu,
                encoder,
                destinationLuma.ViewPtr,
                luma);
            EncodeClearPlane(
                wgpu,
                encoder,
                destinationChroma.ViewPtr,
                chroma);

            var commandBufferDescriptor =
                new CommandBufferDescriptor();
            commandBuffer = wgpu.CommandEncoderFinish(
                encoder,
                &commandBufferDescriptor);
            if (commandBuffer == null)
            {
                throw new InvalidOperationException(
                    "Failed to finish the NV12 solid-color command buffer.");
            }
            wgpu.QueueSubmit(
                context.Queue,
                1,
                &commandBuffer);
        }
        finally
        {
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

    private static void GetEncodedSolidColor(
        uint argbColor,
        GpuTextureColorTransform transform,
        out Color luma,
        out Color chroma)
    {
        const float byteScale =
            1f / byte.MaxValue;
        float red =
            ((argbColor >> 16) & 0xff) *
            byteScale;
        float green =
            ((argbColor >> 8) & 0xff) *
            byteScale;
        float blue =
            (argbColor & 0xff) *
            byteScale;
        Vector3 processed =
            transform.Transform(
                new Vector3(
                    red,
                    green,
                    blue));
        red = processed.X;
        green = processed.Y;
        blue = processed.Z;

        float y =
            red * 0.2126f +
            green * 0.7152f +
            blue * 0.0722f;
        float cb =
            (blue - y) / 1.8556f;
        float cr =
            (red - y) / 1.5748f;
        luma = new Color
        {
            R = Math.Clamp(
                (16f + 219f * y) /
                byte.MaxValue,
                0f,
                1f),
            A = 1d
        };
        chroma = new Color
        {
            R = Math.Clamp(
                (128f + 224f * cb) /
                byte.MaxValue,
                0f,
                1f),
            G = Math.Clamp(
                (128f + 224f * cr) /
                byte.MaxValue,
                0f,
                1f),
            A = 1d
        };
    }

    private static void EncodePlane(
        IWebGpuApi wgpu,
        CommandEncoder* encoder,
        BindGroup* bindGroup,
        RenderPipeline* pipeline,
        TextureView* destination)
    {
        RenderPassEncoder* pass = null;
        try
        {
            var attachment =
                new RenderPassColorAttachment
                {
                    View = destination,
                    ResolveTarget = null,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store
                };
            var descriptor =
                new RenderPassDescriptor
                {
                    ColorAttachmentCount = 1,
                    ColorAttachments = &attachment
                };
            pass = wgpu.CommandEncoderBeginRenderPass(
                encoder,
                &descriptor);
            if (pass == null)
            {
                throw new InvalidOperationException(
                    "Failed to begin an NV12 plane render pass.");
            }
            wgpu.RenderPassEncoderSetPipeline(
                pass,
                pipeline);
            wgpu.RenderPassEncoderSetBindGroup(
                pass,
                0,
                bindGroup,
                0,
                null);
            wgpu.RenderPassEncoderDraw(
                pass,
                3,
                1,
                0,
                0);
            wgpu.RenderPassEncoderEnd(pass);
        }
        finally
        {
            if (pass != null)
            {
                wgpu.RenderPassEncoderRelease(pass);
            }
        }
    }

    private static void EncodeClearPlane(
        IWebGpuApi wgpu,
        CommandEncoder* encoder,
        TextureView* destination,
        Color color)
    {
        RenderPassEncoder* pass = null;
        try
        {
            var attachment =
                new RenderPassColorAttachment
                {
                    View = destination,
                    ResolveTarget = null,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = color
                };
            var descriptor =
                new RenderPassDescriptor
                {
                    ColorAttachmentCount = 1,
                    ColorAttachments = &attachment
                };
            pass =
                wgpu.CommandEncoderBeginRenderPass(
                    encoder,
                    &descriptor);
            if (pass == null)
            {
                throw new InvalidOperationException(
                    "Failed to begin an NV12 solid-color render pass.");
            }
            wgpu.RenderPassEncoderEnd(pass);
        }
        finally
        {
            if (pass != null)
            {
                wgpu.RenderPassEncoderRelease(pass);
            }
        }
    }

    private static ResourceCache GetCache(
        WgpuContext context)
    {
        lock (s_cacheLock)
        {
            if (!s_caches.TryGetValue(
                    context,
                    out ResourceCache? cache))
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
        internal Resources(
            Sampler* sampler,
            BindGroupLayout* bindGroupLayout,
            RenderPipeline* lumaPipeline,
            RenderPipeline* chromaPipeline,
            RenderPipeline* rgbaPipeline,
            GpuBuffer uniform)
        {
            Sampler = sampler;
            BindGroupLayout = bindGroupLayout;
            LumaPipeline = lumaPipeline;
            ChromaPipeline = chromaPipeline;
            RgbaPipeline = rgbaPipeline;
            Uniform = uniform;
        }

        internal Sampler* Sampler { get; }
        internal BindGroupLayout* BindGroupLayout { get; }
        internal RenderPipeline* LumaPipeline { get; }
        internal RenderPipeline* ChromaPipeline { get; }
        internal RenderPipeline* RgbaPipeline { get; }
        internal GpuBuffer Uniform { get; }
    }

    private sealed class ResourceCache
    {
        private readonly WgpuContext _context;
        private readonly object _gate = new();
        private nint _shader;
        private nint _sampler;
        private nint _bindGroupLayout;
        private nint _pipelineLayout;
        private nint _lumaPipeline;
        private nint _chromaPipeline;
        private nint _rgbaPipeline;
        private GpuBuffer? _uniform;

        internal ResourceCache(WgpuContext context)
        {
            _context = context;
        }

        internal Resources GetOrCreate(
            bool includeRgbaPipeline = false)
        {
            lock (_gate)
            {
                EnsureResources();
                if (includeRgbaPipeline &&
                    _rgbaPipeline == 0)
                {
                    _rgbaPipeline =
                        (nint)CreatePipeline(
                            _context,
                            (ShaderModule*)_shader,
                            (PipelineLayout*)_pipelineLayout,
                            TextureFormat.Rgba8Unorm,
                            "fs_rgba");
                }
                return new Resources(
                    (Sampler*)_sampler,
                    (BindGroupLayout*)_bindGroupLayout,
                    (RenderPipeline*)_lumaPipeline,
                    (RenderPipeline*)_chromaPipeline,
                    (RenderPipeline*)_rgbaPipeline,
                    _uniform!);
            }
        }

        internal void QueueDisposal()
        {
            lock (_gate)
            {
                if (!_context.IsDisposed)
                {
                    _context.QueueRenderPipelineDisposal(
                        _lumaPipeline);
                    _context.QueueRenderPipelineDisposal(
                        _chromaPipeline);
                    _context.QueueRenderPipelineDisposal(
                        _rgbaPipeline);
                    _context.QueuePipelineLayoutDisposal(
                        _pipelineLayout);
                    _context.QueueBindGroupLayoutDisposal(
                        _bindGroupLayout);
                    _context.QueueSamplerDisposal(_sampler);
                    _context.QueueShaderModuleDisposal(_shader);
                    _uniform?.Dispose();
                }
                _shader = 0;
                _sampler = 0;
                _bindGroupLayout = 0;
                _pipelineLayout = 0;
                _lumaPipeline = 0;
                _chromaPipeline = 0;
                _rgbaPipeline = 0;
                _uniform = null;
            }
        }

        private void EnsureResources()
        {
            if (_shader != 0)
            {
                return;
            }

            ShaderModule* shader = null;
            Sampler* sampler = null;
            BindGroupLayout* bindGroupLayout = null;
            PipelineLayout* pipelineLayout = null;
            RenderPipeline* lumaPipeline = null;
            RenderPipeline* chromaPipeline = null;
            try
            {
                shader = CreateShader(_context);
                sampler = CreateSampler(_context);
                bindGroupLayout =
                    CreateBindGroupLayout(_context);
                pipelineLayout =
                    CreatePipelineLayout(
                        _context,
                        bindGroupLayout);
                lumaPipeline = CreatePipeline(
                    _context,
                    shader,
                    pipelineLayout,
                    TextureFormat.R8Unorm,
                    "fs_luma");
                chromaPipeline = CreatePipeline(
                    _context,
                    shader,
                    pipelineLayout,
                    TextureFormat.RG8Unorm,
                    "fs_chroma");
                _uniform = new GpuBuffer(
                    _context,
                    checked(
                        (uint)MaxInFlightSlots *
                        UniformStride),
                    BufferUsage.Uniform |
                    BufferUsage.CopyDst,
                    "NV12 Processor In-Flight Uniforms");
                _shader = (nint)shader;
                _sampler = (nint)sampler;
                _bindGroupLayout =
                    (nint)bindGroupLayout;
                _pipelineLayout =
                    (nint)pipelineLayout;
                _lumaPipeline = (nint)lumaPipeline;
                _chromaPipeline =
                    (nint)chromaPipeline;
            }
            catch
            {
                _uniform?.Dispose();
                _uniform = null;
                if (chromaPipeline != null)
                {
                    _context.Api.RenderPipelineRelease(
                        chromaPipeline);
                }
                if (lumaPipeline != null)
                {
                    _context.Api.RenderPipelineRelease(
                        lumaPipeline);
                }
                if (pipelineLayout != null)
                {
                    _context.Api.PipelineLayoutRelease(
                        pipelineLayout);
                }
                if (bindGroupLayout != null)
                {
                    _context.Api.BindGroupLayoutRelease(
                        bindGroupLayout);
                }
                if (sampler != null)
                {
                    _context.Api.SamplerRelease(sampler);
                }
                if (shader != null)
                {
                    _context.Api.ShaderModuleRelease(shader);
                }
                throw;
            }
        }
    }

    private static ShaderModule* CreateShader(
        WgpuContext context)
    {
        nint source =
            SilkMarshal.StringToPtr(ShaderSource);
        nint label =
            SilkMarshal.StringToPtr(
                "ProGPU NV12 Processor Shader");
        try
        {
            var wgsl =
                new ShaderModuleWGSLDescriptor
                {
                    Chain = new ChainedStruct
                    {
                        SType =
                            SType.ShaderModuleWgslDescriptor
                    },
                    Code = (byte*)source
                };
            var descriptor =
                new ShaderModuleDescriptor
                {
                    NextInChain =
                        (ChainedStruct*)&wgsl,
                    Label = (byte*)label
                };
            ShaderModule* shader =
                context.Api.DeviceCreateShaderModule(
                    context.Device,
                    &descriptor);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Failed to create the NV12 processor shader.");
            }
            return shader;
        }
        finally
        {
            SilkMarshal.Free(source);
            SilkMarshal.Free(label);
        }
    }

    private static Sampler* CreateSampler(
        WgpuContext context)
    {
        var descriptor =
            new SamplerDescriptor
            {
                AddressModeU = AddressMode.ClampToEdge,
                AddressModeV = AddressMode.ClampToEdge,
                AddressModeW = AddressMode.ClampToEdge,
                MagFilter = FilterMode.Linear,
                MinFilter = FilterMode.Linear,
                MipmapFilter =
                    MipmapFilterMode.Nearest,
                LodMinClamp = 0f,
                LodMaxClamp = 0f,
                MaxAnisotropy = 1
            };
        Sampler* sampler =
            context.Api.DeviceCreateSampler(
                context.Device,
                &descriptor);
        if (sampler == null)
        {
            throw new InvalidOperationException(
                "Failed to create the NV12 processor sampler.");
        }
        return sampler;
    }

    private static BindGroupLayout*
        CreateBindGroupLayout(WgpuContext context)
    {
        BindGroupLayoutEntry* entries =
            stackalloc BindGroupLayoutEntry[4];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout
            {
                Type = SamplerBindingType.Filtering
            }
        };
        for (uint binding = 1;
             binding <= 2;
             binding++)
        {
            entries[binding] =
                new BindGroupLayoutEntry
                {
                    Binding = binding,
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
        }
        entries[3] = new BindGroupLayoutEntry
        {
            Binding = 3,
            Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.Uniform,
                MinBindingSize = 64
            }
        };
        var descriptor =
            new BindGroupLayoutDescriptor
            {
                EntryCount = 4,
                Entries = entries
            };
        BindGroupLayout* layout =
            context.Api
                .DeviceCreateBindGroupLayout(
                    context.Device,
                    &descriptor);
        if (layout == null)
        {
            throw new InvalidOperationException(
                "Failed to create the NV12 processor bind-group layout.");
        }
        return layout;
    }

    private static PipelineLayout* CreatePipelineLayout(
        WgpuContext context,
        BindGroupLayout* bindGroupLayout)
    {
        BindGroupLayout** layouts =
            stackalloc BindGroupLayout*[1];
        layouts[0] = bindGroupLayout;
        var descriptor =
            new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 1,
                BindGroupLayouts = layouts
            };
        PipelineLayout* layout =
            context.Api.DeviceCreatePipelineLayout(
                context.Device,
                &descriptor);
        if (layout == null)
        {
            throw new InvalidOperationException(
                "Failed to create the NV12 processor pipeline layout.");
        }
        return layout;
    }

    private static RenderPipeline* CreatePipeline(
        WgpuContext context,
        ShaderModule* shader,
        PipelineLayout* pipelineLayout,
        TextureFormat format,
        string fragmentEntry)
    {
        nint vertexEntry =
            SilkMarshal.StringToPtr("vs_main");
        nint fragment =
            SilkMarshal.StringToPtr(fragmentEntry);
        nint label =
            SilkMarshal.StringToPtr(
                $"ProGPU NV12 {fragmentEntry} Pipeline");
        try
        {
            var vertexState =
                new VertexState
                {
                    Module = shader,
                    EntryPoint = (byte*)vertexEntry
                };
            var colorTarget =
                new ColorTargetState
                {
                    Format = format,
                    WriteMask = ColorWriteMask.All
                };
            var fragmentState =
                new FragmentState
                {
                    Module = shader,
                    EntryPoint = (byte*)fragment,
                    TargetCount = 1,
                    Targets = &colorTarget
                };
            var descriptor =
                new RenderPipelineDescriptor
                {
                    Label = (byte*)label,
                    Layout = pipelineLayout,
                    Vertex = vertexState,
                    Primitive =
                        new PrimitiveState
                        {
                            Topology =
                                PrimitiveTopology
                                    .TriangleList,
                            FrontFace = FrontFace.Ccw,
                            CullMode = CullMode.None
                        },
                    Multisample =
                        new MultisampleState
                        {
                            Count = 1,
                            Mask = uint.MaxValue
                        },
                    Fragment = &fragmentState
                };
            RenderPipeline* pipeline =
                context.Api.DeviceCreateRenderPipeline(
                    context.Device,
                    &descriptor);
            if (pipeline == null)
            {
                throw new InvalidOperationException(
                    $"Failed to create the NV12 processor {format} pipeline.");
            }
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(vertexEntry);
            SilkMarshal.Free(fragment);
            SilkMarshal.Free(label);
        }
    }
}
