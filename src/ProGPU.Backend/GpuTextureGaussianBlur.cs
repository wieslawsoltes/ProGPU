using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Describes one normalized planar YUV-to-straight-RGB conversion for a GPU
/// image-processing pass.
/// </summary>
public readonly record struct GpuPlanarYuvConversion(
    Vector4 Range,
    Vector4 Red,
    Vector4 Green,
    Vector4 Blue);

/// <summary>
/// Applies a normalized, clamp-to-edge separable Gaussian blur entirely on
/// WebGPU and optionally fuses an affine color transform into the final pass.
/// </summary>
/// <remarks>
/// The caller owns one reusable intermediate texture from the same
/// <see cref="WgpuContext"/>. Both axes are encoded into one command buffer;
/// no readback, cross-device copy, or per-frame GPU resource allocation is
/// performed apart from two short-lived bind groups and the command encoders.
/// For P output pixels and radius R = ceil(3 * sigma), work is O(P * R) and
/// fixed residency is O(P) for the explicit intermediate.
/// </remarks>
public static unsafe class GpuTextureGaussianBlur
{
    public const float MaximumStandardDeviation = 32f;

    private const int MaximumRadius = 96;
    private const int MaximumPairCount = 48;
    private const int UniformFloatCount =
        4 + 12 + 20 + MaximumPairCount * 4;
    private const uint UniformByteSize =
        UniformFloatCount * sizeof(float);

    private static readonly string ShaderSource =
        ShaderResource.Load(
            typeof(GpuTextureGaussianBlur),
            "TextureGaussianBlur.wgsl");
    private static readonly string UnfilterableShaderSource =
        ShaderResource.Load(
            typeof(GpuTextureGaussianBlur),
            "TextureGaussianBlurUnfilterable.wgsl");
    private static readonly object s_cacheLock = new();
    private static readonly Dictionary<WgpuContext, ResourceCache>
        s_caches = new();

    static GpuTextureGaussianBlur()
    {
        WgpuContext.Disposing += ReleaseCache;
    }

    /// <summary>
    /// Encodes horizontal and vertical Gaussian passes into one submission.
    /// The destination must not alias either sampled texture.
    /// </summary>
    public static void Blur(
        GpuTexture source,
        GpuTexture intermediate,
        TextureView* destinationView,
        TextureFormat destinationFormat,
        float standardDeviation,
        in GpuTextureColorTransform colorTransform,
        Color clearColor = default)
    {
        Validate(
            source,
            intermediate,
            destinationView,
            standardDeviation);

        WgpuContext context = source.Context;
        lock (context.RenderLock)
        {
            if (context.IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(WgpuContext));
            }

            BlurCore(
                source,
                source,
                intermediate,
                destinationView,
                destinationFormat,
                standardDeviation,
                colorTransform,
                decodePlanarYuv: false,
                default,
                clearColor);
        }
    }

    /// <summary>
    /// Decodes R8/RG8 or Tier-1 R16/RG16 planar YUV samples into straight RGB
    /// while evaluating the horizontal Gaussian axis, then evaluates the
    /// vertical RGB axis.
    /// Both axes use one command buffer and one queue submission.
    /// </summary>
    public static void BlurPlanar(
        GpuTexture sourceLuma,
        GpuTexture sourceChroma,
        GpuTexture intermediate,
        TextureView* destinationView,
        TextureFormat destinationFormat,
        float standardDeviation,
        in GpuPlanarYuvConversion conversion,
        in GpuTextureColorTransform colorTransform,
        Color clearColor = default)
    {
        Validate(
            sourceLuma,
            intermediate,
            destinationView,
            standardDeviation);
        ValidatePlanar(
            sourceLuma,
            sourceChroma,
            intermediate);

        WgpuContext context = sourceLuma.Context;
        lock (context.RenderLock)
        {
            if (context.IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(WgpuContext));
            }

            BlurCore(
                sourceLuma,
                sourceChroma,
                intermediate,
                destinationView,
                destinationFormat,
                standardDeviation,
                colorTransform,
                decodePlanarYuv: true,
                conversion,
                clearColor);
        }
    }

    private static void Validate(
        GpuTexture source,
        GpuTexture intermediate,
        TextureView* destinationView,
        float standardDeviation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(intermediate);
        ObjectDisposedException.ThrowIf(
            source.IsDisposed,
            source);
        ObjectDisposedException.ThrowIf(
            intermediate.IsDisposed,
            intermediate);
        if (destinationView == null)
        {
            throw new ArgumentNullException(
                nameof(destinationView));
        }
        if (!float.IsFinite(standardDeviation) ||
            standardDeviation <= 0f ||
            standardDeviation >
                MaximumStandardDeviation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(standardDeviation));
        }
        if (ReferenceEquals(source, intermediate) ||
            destinationView == source.ViewPtr ||
            destinationView == intermediate.ViewPtr)
        {
            throw new ArgumentException(
                "Gaussian source, intermediate, and destination texture subresources must be distinct.");
        }
        if (!ReferenceEquals(
                source.Context,
                intermediate.Context))
        {
            throw new ArgumentException(
                "Gaussian textures must belong to the same WebGPU device.",
                nameof(intermediate));
        }
        if (source.Width != intermediate.Width ||
            source.Height != intermediate.Height)
        {
            throw new ArgumentException(
                "The Gaussian intermediate must match the source dimensions.",
                nameof(intermediate));
        }
        ValidateTexture(
            source,
            TextureUsage.TextureBinding,
            nameof(source));
        ValidateTexture(
            intermediate,
            TextureUsage.TextureBinding |
                TextureUsage.RenderAttachment,
            nameof(intermediate));
    }

    private static void ValidateTexture(
        GpuTexture texture,
        TextureUsage requiredUsage,
        string parameterName)
    {
        if ((texture.Usage & requiredUsage) !=
            requiredUsage)
        {
            throw new ArgumentException(
                $"Gaussian texture requires {requiredUsage} usage.",
                parameterName);
        }
        if (texture.Dimension !=
                GpuTextureDimension.Dimension2D ||
            texture.DepthOrArrayLayers != 1 ||
            texture.SampleCount != 1)
        {
            throw new NotSupportedException(
                "Gaussian blur supports single-sample 2D textures with one layer.");
        }
    }

    private static void ValidatePlanar(
        GpuTexture sourceLuma,
        GpuTexture sourceChroma,
        GpuTexture intermediate)
    {
        ArgumentNullException.ThrowIfNull(sourceChroma);
        ObjectDisposedException.ThrowIf(
            sourceChroma.IsDisposed,
            sourceChroma);
        if (!ReferenceEquals(
                sourceLuma.Context,
                sourceChroma.Context))
        {
            throw new ArgumentException(
                "Planar Gaussian textures must belong to the same WebGPU device.",
                nameof(sourceChroma));
        }
        bool is8Bit =
            sourceLuma.Format ==
                TextureFormat.R8Unorm &&
            sourceChroma.Format ==
                TextureFormat.RG8Unorm;
        bool is16Bit =
            sourceLuma.Format ==
                ProGpuTextureFormats.R16Unorm &&
            sourceChroma.Format ==
                ProGpuTextureFormats.RG16Unorm &&
            sourceLuma.Context
                .SupportsTextureFormatsTier1;
        if ((!is8Bit && !is16Bit) ||
            sourceChroma.Width !=
                (sourceLuma.Width + 1) / 2 ||
            sourceChroma.Height !=
                (sourceLuma.Height + 1) / 2 ||
            intermediate.Format is not
                (TextureFormat.Rgba8Unorm or
                 TextureFormat.Rgba16float))
        {
            throw new NotSupportedException(
                "Planar Gaussian blur requires matching R8/RG8 or Tier-1 R16/RG16 luma/chroma planes and an RGBA8 or RGBA16F intermediate.");
        }
        ValidateTexture(
            sourceChroma,
            TextureUsage.TextureBinding,
            nameof(sourceChroma));
    }

    private static void BlurCore(
        GpuTexture source,
        GpuTexture chroma,
        GpuTexture intermediate,
        TextureView* destinationView,
        TextureFormat destinationFormat,
        float standardDeviation,
        in GpuTextureColorTransform colorTransform,
        bool decodePlanarYuv,
        in GpuPlanarYuvConversion yuvConversion,
        Color clearColor)
    {
        WgpuContext context = source.Context;
        ResourceCache cache = GetCache(context);
        bool unfilterableSource =
            source.Format ==
                ProGpuTextureFormats.R16Unorm;
        Resources horizontalResources =
            cache.GetOrCreate(
                intermediate.Format,
                unfilterableSource);
        Resources verticalResources =
            cache.GetOrCreate(
                destinationFormat,
                unfilterableSource: false);
        Span<float> weights =
            stackalloc float[MaximumRadius + 1];
        int radius = Math.Min(
            MaximumRadius,
            (int)MathF.Ceiling(
                3f * standardDeviation));
        BuildWeights(
            standardDeviation,
            radius,
            weights,
            out float centerWeight,
            out int pairCount);

        Span<float> horizontalValues =
            stackalloc float[UniformFloatCount];
        Span<float> verticalValues =
            stackalloc float[UniformFloatCount];
        BuildUniform(
            horizontalValues,
            1f / source.Width,
            0f,
            centerWeight,
            pairCount,
            GpuTextureColorTransform.Identity,
            decodePlanarYuv,
            yuvConversion,
            weights,
            radius);
        BuildUniform(
            verticalValues,
            0f,
            1f / source.Height,
            centerWeight,
            pairCount,
            colorTransform,
            decodePlanarYuv: false,
            default,
            weights,
            radius);
        cache.HorizontalUniform.Write<float>(
            horizontalValues);
        cache.VerticalUniform.Write<float>(
            verticalValues);

        IWebGpuApi wgpu = context.Api;
        BindGroup* horizontalBindGroup = null;
        BindGroup* verticalBindGroup = null;
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            horizontalBindGroup = CreateBindGroup(
                context,
                horizontalResources,
                source.ViewPtr,
                chroma.ViewPtr,
                cache.HorizontalUniform);
            verticalBindGroup = CreateBindGroup(
                context,
                verticalResources,
                intermediate.ViewPtr,
                intermediate.ViewPtr,
                cache.VerticalUniform);

            var encoderDescriptor =
                new CommandEncoderDescriptor();
            encoder =
                wgpu.DeviceCreateCommandEncoder(
                    context.Device,
                    &encoderDescriptor);
            if (encoder == null)
            {
                throw new InvalidOperationException(
                    "Failed to create the Gaussian command encoder.");
            }

            pass = BeginPass(
                wgpu,
                encoder,
                intermediate.ViewPtr,
                default);
            EncodePass(
                wgpu,
                pass,
                horizontalResources.Pipeline,
                horizontalBindGroup);
            wgpu.RenderPassEncoderRelease(pass);
            pass = null;

            pass = BeginPass(
                wgpu,
                encoder,
                destinationView,
                clearColor);
            EncodePass(
                wgpu,
                pass,
                verticalResources.Pipeline,
                verticalBindGroup);
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
                    "Failed to finish the Gaussian command buffer.");
            }

            wgpu.QueueSubmit(
                context.Queue,
                1,
                &commandBuffer);
            intermediate.MarkContentsDirty();
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
            if (verticalBindGroup != null)
            {
                wgpu.BindGroupRelease(
                    verticalBindGroup);
            }
            if (horizontalBindGroup != null)
            {
                wgpu.BindGroupRelease(
                    horizontalBindGroup);
            }
        }
    }

    private static void BuildWeights(
        float standardDeviation,
        int radius,
        Span<float> weights,
        out float centerWeight,
        out int pairCount)
    {
        float denominator =
            2f *
            standardDeviation *
            standardDeviation;
        weights[0] = 1f;
        double total = 1d;
        for (int index = 1;
             index <= radius;
             index++)
        {
            float weight = MathF.Exp(
                -(index * index) / denominator);
            weights[index] = weight;
            total += 2d * weight;
        }

        float inverseTotal = (float)(1d / total);
        for (int index = 0;
             index <= radius;
             index++)
        {
            weights[index] *= inverseTotal;
        }
        centerWeight = weights[0];
        pairCount = (radius + 1) / 2;
    }

    private static void BuildUniform(
        Span<float> destination,
        float directionX,
        float directionY,
        float centerWeight,
        int pairCount,
        in GpuTextureColorTransform colorTransform,
        bool decodePlanarYuv,
        in GpuPlanarYuvConversion yuvConversion,
        ReadOnlySpan<float> weights,
        int radius)
    {
        destination.Clear();
        destination[0] = directionX;
        destination[1] = directionY;
        destination[2] = centerWeight;
        destination[3] = pairCount;
        WriteRow(destination, 4, colorTransform.Red);
        WriteRow(destination, 8, colorTransform.Green);
        WriteRow(destination, 12, colorTransform.Blue);
        destination[16] = decodePlanarYuv ? 1f : 0f;
        WriteRow(destination, 20, yuvConversion.Range);
        WriteRow(destination, 24, yuvConversion.Red);
        WriteRow(destination, 28, yuvConversion.Green);
        WriteRow(destination, 32, yuvConversion.Blue);

        for (int pair = 0;
             pair < pairCount;
             pair++)
        {
            int firstIndex = pair * 2 + 1;
            int secondIndex = firstIndex + 1;
            float firstWeight = weights[firstIndex];
            float secondWeight =
                secondIndex <= radius
                    ? weights[secondIndex]
                    : 0f;
            float combinedWeight =
                firstWeight + secondWeight;
            float offset = combinedWeight > 0f
                ? (firstIndex * firstWeight +
                   secondIndex * secondWeight) /
                  combinedWeight
                : firstIndex;
            int uniformIndex = 36 + pair * 4;
            destination[uniformIndex] = offset;
            destination[uniformIndex + 1] =
                combinedWeight;
            destination[uniformIndex + 2] =
                firstWeight;
            destination[uniformIndex + 3] =
                secondWeight;
        }
    }

    private static void WriteRow(
        Span<float> destination,
        int offset,
        System.Numerics.Vector4 row)
    {
        destination[offset] = row.X;
        destination[offset + 1] = row.Y;
        destination[offset + 2] = row.Z;
        destination[offset + 3] = row.W;
    }

    private static BindGroup* CreateBindGroup(
        WgpuContext context,
        Resources resources,
        TextureView* sourceView,
        TextureView* chromaView,
        GpuBuffer uniform)
    {
        var entries = stackalloc BindGroupEntry[4];
        entries[0] = new BindGroupEntry
        {
            Binding = 0,
            Sampler = resources.Sampler
        };
        entries[1] = new BindGroupEntry
        {
            Binding = 1,
            TextureView = sourceView
        };
        entries[2] = new BindGroupEntry
        {
            Binding = 2,
            TextureView = chromaView
        };
        entries[3] = new BindGroupEntry
        {
            Binding = 3,
            Buffer = uniform.BufferPtr,
            Offset = 0,
            Size = UniformByteSize
        };
        var descriptor = new BindGroupDescriptor
        {
            Layout = resources.BindGroupLayout,
            EntryCount = 4,
            Entries = entries
        };
        BindGroup* bindGroup =
            context.Api.DeviceCreateBindGroup(
                context.Device,
                &descriptor);
        if (bindGroup == null)
        {
            throw new InvalidOperationException(
                "Failed to create a Gaussian bind group.");
        }
        return bindGroup;
    }

    private static RenderPassEncoder* BeginPass(
        IWebGpuApi wgpu,
        CommandEncoder* encoder,
        TextureView* destinationView,
        Color clearColor)
    {
        var colorAttachment =
            new RenderPassColorAttachment
            {
                View = destinationView,
                ResolveTarget = null,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = clearColor
            };
        var descriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment
        };
        RenderPassEncoder* pass =
            wgpu.CommandEncoderBeginRenderPass(
                encoder,
                &descriptor);
        if (pass == null)
        {
            throw new InvalidOperationException(
                "Failed to begin a Gaussian render pass.");
        }
        return pass;
    }

    private static void EncodePass(
        IWebGpuApi wgpu,
        RenderPassEncoder* pass,
        RenderPipeline* pipeline,
        BindGroup* bindGroup)
    {
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

    private static void ReleaseCache(
        WgpuContext context)
    {
        ResourceCache? cache;
        lock (s_cacheLock)
        {
            if (!s_caches.Remove(
                    context,
                    out cache))
            {
                return;
            }
        }
        cache.QueueDisposal();
    }

    private readonly struct Resources
    {
        public Resources(
            Sampler* sampler,
            BindGroupLayout* bindGroupLayout,
            RenderPipeline* pipeline)
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
        private readonly Dictionary<TextureFormat, IntPtr>
            _pipelines = new();
        private readonly Dictionary<TextureFormat, IntPtr>
            _unfilterablePipelines = new();
        private IntPtr _shader;
        private IntPtr _sampler;
        private IntPtr _bindGroupLayout;
        private IntPtr _pipelineLayout;
        private IntPtr _unfilterableShader;
        private IntPtr _unfilterableSampler;
        private IntPtr _unfilterableBindGroupLayout;
        private IntPtr _unfilterablePipelineLayout;

        public ResourceCache(
            WgpuContext context)
        {
            _context = context;
            HorizontalUniform = new GpuBuffer(
                context,
                UniformByteSize,
                BufferUsage.Uniform |
                    BufferUsage.CopyDst,
                "Gaussian Horizontal Uniform");
            VerticalUniform = new GpuBuffer(
                context,
                UniformByteSize,
                BufferUsage.Uniform |
                    BufferUsage.CopyDst,
                "Gaussian Vertical Uniform");
        }

        public GpuBuffer HorizontalUniform { get; }
        public GpuBuffer VerticalUniform { get; }

        public Resources GetOrCreate(
            TextureFormat format,
            bool unfilterableSource)
        {
            lock (_lock)
            {
                if (unfilterableSource)
                {
                    EnsureUnfilterableResources();
                    if (!_unfilterablePipelines
                            .TryGetValue(
                                format,
                                out IntPtr
                                    unfilterablePipeline))
                    {
                        unfilterablePipeline =
                            (IntPtr)CreatePipeline(
                                _context,
                                (ShaderModule*)
                                    _unfilterableShader,
                                (PipelineLayout*)
                                    _unfilterablePipelineLayout,
                                format);
                        _unfilterablePipelines.Add(
                            format,
                            unfilterablePipeline);
                    }
                    return new Resources(
                        (Sampler*)_unfilterableSampler,
                        (BindGroupLayout*)
                            _unfilterableBindGroupLayout,
                        (RenderPipeline*)
                            unfilterablePipeline);
                }

                EnsureFilterableResources();
                if (!_pipelines.TryGetValue(
                        format,
                        out IntPtr pipeline))
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
                    foreach (IntPtr pipeline
                             in _pipelines.Values)
                    {
                        _context.QueueRenderPipelineDisposal(
                            pipeline);
                    }
                    foreach (IntPtr pipeline
                             in _unfilterablePipelines
                                 .Values)
                    {
                        _context
                            .QueueRenderPipelineDisposal(
                                pipeline);
                    }
                    _context.QueuePipelineLayoutDisposal(
                        _pipelineLayout);
                    _context.QueuePipelineLayoutDisposal(
                        _unfilterablePipelineLayout);
                    _context.QueueBindGroupLayoutDisposal(
                        _bindGroupLayout);
                    _context.QueueBindGroupLayoutDisposal(
                        _unfilterableBindGroupLayout);
                    _context.QueueSamplerDisposal(
                        _sampler);
                    _context.QueueSamplerDisposal(
                        _unfilterableSampler);
                    _context.QueueShaderModuleDisposal(
                        _shader);
                    _context.QueueShaderModuleDisposal(
                        _unfilterableShader);
                    HorizontalUniform.Dispose();
                    VerticalUniform.Dispose();
                }
                _pipelines.Clear();
                _unfilterablePipelines.Clear();
                _pipelineLayout = IntPtr.Zero;
                _bindGroupLayout = IntPtr.Zero;
                _sampler = IntPtr.Zero;
                _shader = IntPtr.Zero;
                _unfilterablePipelineLayout =
                    IntPtr.Zero;
                _unfilterableBindGroupLayout =
                    IntPtr.Zero;
                _unfilterableSampler = IntPtr.Zero;
                _unfilterableShader = IntPtr.Zero;
            }
        }

        private void EnsureFilterableResources()
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
                shader = CreateShader(
                    _context,
                    ShaderSource,
                    "ProGPU Gaussian Blur Shader");
                sampler = CreateSampler(
                    _context,
                    unfilterableSource: false);
                bindGroupLayout =
                    CreateBindGroupLayout(
                        _context,
                        unfilterableSource: false);
                pipelineLayout =
                    CreatePipelineLayout(
                        _context,
                        bindGroupLayout);
                _shader = (IntPtr)shader;
                _sampler = (IntPtr)sampler;
                _bindGroupLayout =
                    (IntPtr)bindGroupLayout;
                _pipelineLayout =
                    (IntPtr)pipelineLayout;
            }
            catch
            {
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

        private void EnsureUnfilterableResources()
        {
            if (_unfilterableShader != IntPtr.Zero)
            {
                return;
            }

            ShaderModule* shader = null;
            Sampler* sampler = null;
            BindGroupLayout* bindGroupLayout = null;
            PipelineLayout* pipelineLayout = null;
            try
            {
                shader = CreateShader(
                    _context,
                    UnfilterableShaderSource,
                    "ProGPU P010 Gaussian Blur Shader");
                sampler = CreateSampler(
                    _context,
                    unfilterableSource: true);
                bindGroupLayout =
                    CreateBindGroupLayout(
                        _context,
                        unfilterableSource: true);
                pipelineLayout =
                    CreatePipelineLayout(
                        _context,
                        bindGroupLayout);
                _unfilterableShader =
                    (IntPtr)shader;
                _unfilterableSampler =
                    (IntPtr)sampler;
                _unfilterableBindGroupLayout =
                    (IntPtr)bindGroupLayout;
                _unfilterablePipelineLayout =
                    (IntPtr)pipelineLayout;
            }
            catch
            {
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
        WgpuContext context,
        string source,
        string label)
    {
        IntPtr sourcePointer =
            SilkMarshal.StringToPtr(source);
        IntPtr labelPointer =
            SilkMarshal.StringToPtr(label);
        try
        {
            var wgslDescriptor =
                new ShaderModuleWGSLDescriptor
                {
                    Chain = new ChainedStruct
                    {
                        Next = null,
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
                        (ChainedStruct*)&wgslDescriptor,
                    Label = (byte*)labelPointer
                };
            ShaderModule* shader =
                context.Api.DeviceCreateShaderModule(
                    context.Device,
                    &descriptor);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Failed to create the Gaussian shader.");
            }
            return shader;
        }
        finally
        {
            SilkMarshal.Free(sourcePointer);
            SilkMarshal.Free(labelPointer);
        }
    }

    private static Sampler* CreateSampler(
        WgpuContext context,
        bool unfilterableSource)
    {
        var descriptor = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MagFilter = unfilterableSource
                ? FilterMode.Nearest
                : FilterMode.Linear,
            MinFilter = unfilterableSource
                ? FilterMode.Nearest
                : FilterMode.Linear,
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
                "Failed to create the Gaussian sampler.");
        }
        return sampler;
    }

    private static BindGroupLayout*
        CreateBindGroupLayout(
            WgpuContext context,
            bool unfilterableSource)
    {
        var entries =
            stackalloc BindGroupLayoutEntry[4];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout
            {
                Type = unfilterableSource
                    ? SamplerBindingType.NonFiltering
                    : SamplerBindingType.Filtering
            }
        };
        entries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout
            {
                SampleType =
                    unfilterableSource
                        ? TextureSampleType
                            .UnfilterableFloat
                        : TextureSampleType.Float,
                ViewDimension =
                    TextureViewDimension.Dimension2D,
                Multisampled = false
            }
        };
        entries[2] = new BindGroupLayoutEntry
        {
            Binding = 2,
            Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout
            {
                SampleType =
                    unfilterableSource
                        ? TextureSampleType
                            .UnfilterableFloat
                        : TextureSampleType.Float,
                ViewDimension =
                    TextureViewDimension.Dimension2D,
                Multisampled = false
            }
        };
        entries[3] = new BindGroupLayoutEntry
        {
            Binding = 3,
            Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.Uniform,
                HasDynamicOffset = false,
                MinBindingSize = UniformByteSize
            }
        };
        var descriptor =
            new BindGroupLayoutDescriptor
            {
                EntryCount = 4,
                Entries = entries
            };
        BindGroupLayout* layout =
            context.Api.DeviceCreateBindGroupLayout(
                context.Device,
                &descriptor);
        if (layout == null)
        {
            throw new InvalidOperationException(
                "Failed to create the Gaussian bind-group layout.");
        }
        return layout;
    }

    private static PipelineLayout*
        CreatePipelineLayout(
            WgpuContext context,
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
            context.Api.DeviceCreatePipelineLayout(
                context.Device,
                &descriptor);
        if (layout == null)
        {
            throw new InvalidOperationException(
                "Failed to create the Gaussian pipeline layout.");
        }
        return layout;
    }

    private static RenderPipeline* CreatePipeline(
        WgpuContext context,
        ShaderModule* shader,
        PipelineLayout* pipelineLayout,
        TextureFormat format)
    {
        IntPtr vertexEntryPointer =
            SilkMarshal.StringToPtr("vs_main");
        IntPtr fragmentEntryPointer =
            SilkMarshal.StringToPtr("fs_main");
        IntPtr labelPointer =
            SilkMarshal.StringToPtr(
                "ProGPU Gaussian Blur Pipeline");
        try
        {
            var vertexState = new VertexState
            {
                Module = shader,
                EntryPoint =
                    (byte*)vertexEntryPointer
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
                EntryPoint =
                    (byte*)fragmentEntryPointer,
                TargetCount = 1,
                Targets = &colorTarget
            };
            var descriptor =
                new RenderPipelineDescriptor
                {
                    Label = (byte*)labelPointer,
                    Layout = pipelineLayout,
                    Vertex = vertexState,
                    Primitive = new PrimitiveState
                    {
                        Topology =
                            PrimitiveTopology
                                .TriangleList,
                        StripIndexFormat =
                            IndexFormat.Undefined,
                        FrontFace = FrontFace.Ccw,
                        CullMode = CullMode.None
                    },
                    Multisample =
                        new MultisampleState
                        {
                            Count = 1,
                            Mask = uint.MaxValue,
                            AlphaToCoverageEnabled =
                                false
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
                    $"Failed to create the Gaussian pipeline for {format}.");
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
