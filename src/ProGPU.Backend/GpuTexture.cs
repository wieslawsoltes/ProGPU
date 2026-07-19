using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

public enum GpuTextureAlphaMode
{
    Straight = 0,
    Premultiplied = 1
}

public enum GpuTextureDimension
{
    Dimension2D = 0,
    Dimension3D = 1
}

public unsafe class GpuTexture : IDisposable
{
    static GpuTexture()
    {
        WgpuContext.Disposing += ReleaseMipGeneratorCache;
    }

    private static readonly string MipGeneratorShader = ShaderResource.Load(typeof(GpuTexture), "MipGenerator.wgsl");

    private static long s_idCounter = 0;
    private static readonly object s_mipGeneratorCacheLock = new();
    private static readonly Dictionary<WgpuContext, MipGeneratorResourceCache> s_mipGeneratorCaches = new();
    public static event Action<ulong>? OnDisposedWithId;

    private readonly WgpuContext _context;
    private string _label;

    public ulong Id { get; }
    public WgpuContext Context => _context;
    public uint Generation { get; private set; }

    public Texture* TexturePtr { get; private set; }
    public TextureView* ViewPtr { get; private set; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public uint DepthOrArrayLayers { get; private set; } = 1;
    public uint MipLevelCount { get; private set; } = 1;
    public GpuTextureDimension Dimension { get; private set; } = GpuTextureDimension.Dimension2D;
    public TextureFormat Format { get; private set; }
    public TextureUsage Usage { get; private set; }
    public uint SampleCount { get; private set; } = 1;
    public GpuTextureAlphaMode AlphaMode { get; set; }

    private int _disposeState;
    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    private readonly unsafe struct MipGeneratorResources
    {
        public MipGeneratorResources(
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

    private sealed unsafe class MipGeneratorResourceCache
    {
        private readonly WgpuContext _context;
        private readonly object _lock = new();
        private readonly Dictionary<TextureFormat, IntPtr> _pipelines = new();
        private IntPtr _shader;
        private IntPtr _sampler;
        private IntPtr _bindGroupLayout;
        private IntPtr _pipelineLayout;

        public MipGeneratorResourceCache(WgpuContext context)
        {
            _context = context;
        }

        public MipGeneratorResources GetOrCreate(TextureFormat format)
        {
            if (_context.IsDisposed)
            {
                throw new ObjectDisposedException(nameof(WgpuContext));
            }

            lock (_lock)
            {
                EnsureCommonResources();

                if (!_pipelines.TryGetValue(format, out var pipeline))
                {
                    pipeline = (IntPtr)CreateMipGeneratorPipeline(
                        _context,
                        (ShaderModule*)_shader,
                        (PipelineLayout*)_pipelineLayout,
                        format);
                    _pipelines.Add(format, pipeline);
                }

                return new MipGeneratorResources(
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
                    QueuePipelinesForDisposal();

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

        private void QueuePipelinesForDisposal()
        {
            var pipelineEnumerator = _pipelines.Values.GetEnumerator();
            while (pipelineEnumerator.MoveNext())
            {
                _context.QueueRenderPipelineDisposal(pipelineEnumerator.Current);
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
                shader = CreateMipGeneratorShaderModule(_context);
                sampler = CreateMipGeneratorSampler(_context);
                bindGroupLayout = CreateMipGeneratorBindGroupLayout(_context);
                pipelineLayout = CreateMipGeneratorPipelineLayout(_context, bindGroupLayout);

                _shader = (IntPtr)shader;
                _sampler = (IntPtr)sampler;
                _bindGroupLayout = (IntPtr)bindGroupLayout;
                _pipelineLayout = (IntPtr)pipelineLayout;
            }
            catch
            {
                ReleaseMipGeneratorResourcesNow(_context, shader, sampler, null, bindGroupLayout, pipelineLayout);
                throw;
            }
        }
    }

    public GpuTexture(
        WgpuContext context,
        uint width,
        uint height,
        TextureFormat format,
        TextureUsage usage,
        string label = "GpuTexture",
        uint sampleCount = 1,
        GpuTextureAlphaMode alphaMode = GpuTextureAlphaMode.Straight,
        uint depthOrArrayLayers = 1,
        uint mipLevelCount = 1,
        GpuTextureDimension dimension = GpuTextureDimension.Dimension2D)
    {
        Id = (ulong)Interlocked.Increment(ref s_idCounter);
        _context = context;
        Width = width > 0 ? width : 1;
        Height = height > 0 ? height : 1;
        DepthOrArrayLayers = depthOrArrayLayers > 0 ? depthOrArrayLayers : 1;
        MipLevelCount = mipLevelCount > 0 ? mipLevelCount : 1;
        Dimension = dimension;
        Format = format;
        Usage = usage;
        _label = label;
        SampleCount = sampleCount;
        AlphaMode = alphaMode;

        if (SampleCount != 1 && MipLevelCount != 1)
        {
            throw new NotSupportedException("Multisampled GPU textures cannot have mip levels.");
        }

        Allocate();
    }

    private void Allocate()
    {
        Generation++;
        var labelPtr = SilkMarshal.StringToPtr(_label);
        
        var desc = new TextureDescriptor
        {
            Label = (byte*)labelPtr,
            Usage = Usage,
            Dimension = Dimension == GpuTextureDimension.Dimension3D
                ? TextureDimension.Dimension3D
                : TextureDimension.Dimension2D,
            Size = new Extent3D { Width = Width, Height = Height, DepthOrArrayLayers = DepthOrArrayLayers },
            Format = Format,
            MipLevelCount = MipLevelCount,
            SampleCount = SampleCount,
            ViewFormatCount = 0,
            ViewFormats = null
        };

        TexturePtr = _context.Api.DeviceCreateTexture(_context.Device, &desc);
        SilkMarshal.Free(labelPtr);

        if (TexturePtr == null)
        {
            throw new InvalidOperationException($"Failed to allocate GPU Texture {Width}x{Height}.");
        }

        // Automatically create a default texture view
        var viewDesc = new TextureViewDescriptor
        {
            Format = Format,
            Dimension = Dimension == GpuTextureDimension.Dimension3D
                ? TextureViewDimension.Dimension3D
                : DepthOrArrayLayers > 1
                ? TextureViewDimension.Dimension2DArray
                : TextureViewDimension.Dimension2D,
            BaseMipLevel = 0,
            MipLevelCount = MipLevelCount,
            BaseArrayLayer = 0,
            ArrayLayerCount = DepthOrArrayLayers,
            Aspect = TextureAspect.All
        };

        ViewPtr = _context.Api.TextureCreateView(TexturePtr, &viewDesc);
        if (ViewPtr == null)
        {
            throw new InvalidOperationException($"Failed to create TextureView for GPU Texture {Width}x{Height}.");
        }
    }

    public void Resize(uint width, uint height)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        if (Width == width && Height == height) return;

        // Release old texture and view
        ReleaseResources();

        // Reallocate with new dimensions
        Width = width > 0 ? width : 1;
        Height = height > 0 ? height : 1;
        Allocate();
    }

    public void ClearRenderTarget(Color clearColor = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        if (!Usage.HasFlag(TextureUsage.RenderAttachment))
        {
            throw new InvalidOperationException("GPU texture clear requires RenderAttachment usage.");
        }
        if (Dimension != GpuTextureDimension.Dimension2D || DepthOrArrayLayers != 1 || MipLevelCount != 1)
        {
            throw new NotSupportedException("GPU texture clear currently supports a single 2D texture level and layer.");
        }
        if (ViewPtr == null)
        {
            throw new InvalidOperationException("GPU texture does not have a render-target view.");
        }

        var wgpu = _context.Api;
        CommandEncoder* encoder = null;
        RenderPassEncoder* pass = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var encoderDescriptor = new CommandEncoderDescriptor();
            encoder = wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
            if (encoder == null)
            {
                throw new InvalidOperationException("Failed to create command encoder for texture clear.");
            }

            var colorAttachment = new RenderPassColorAttachment
            {
                View = ViewPtr,
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
                throw new InvalidOperationException("Failed to begin render pass for texture clear.");
            }

            wgpu.RenderPassEncoderEnd(pass);
            wgpu.RenderPassEncoderRelease(pass);
            pass = null;

            var commandBufferDescriptor = new CommandBufferDescriptor();
            commandBuffer = wgpu.CommandEncoderFinish(encoder, &commandBufferDescriptor);
            if (commandBuffer == null)
            {
                throw new InvalidOperationException("Failed to finish command buffer for texture clear.");
            }

            wgpu.QueueSubmit(_context.Queue, 1, &commandBuffer);
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
        }

        Generation++;
    }

    public void WritePixels<T>(ReadOnlySpan<T> pixels, uint mipLevel = 0) where T : unmanaged
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        ValidateMipLevel(mipLevel);

        uint bytesPerPixel = GetBytesPerPixel(Format);
        uint mipWidth = GetMipDimension(Width, mipLevel);
        uint mipHeight = GetMipDimension(Height, mipLevel);
        uint mipDepthOrLayers = GetMipDepthOrArrayLayers(mipLevel);

        uint expectedSize = mipWidth * mipHeight * mipDepthOrLayers * bytesPerPixel;
        uint passedSize = (uint)(pixels.Length * sizeof(T));
        if (passedSize < expectedSize)
        {
            throw new ArgumentException($"Pixel span is too small ({passedSize} bytes, expected {expectedSize} bytes).");
        }

        var destination = new ImageCopyTexture
        {
            Texture = TexturePtr,
            MipLevel = mipLevel,
            Origin = new Origin3D { X = 0, Y = 0, Z = 0 },
            Aspect = GetTextureCopyAspect(Format)
        };

        var layout = new TextureDataLayout
        {
            Offset = 0,
            BytesPerRow = mipWidth * bytesPerPixel,
            RowsPerImage = mipHeight
        };

        var extent = new Extent3D
        {
            Width = mipWidth,
            Height = mipHeight,
            DepthOrArrayLayers = mipDepthOrLayers
        };

        fixed (T* ptr = pixels)
        {
            _context.Api.QueueWriteTexture(_context.Queue, &destination, ptr, passedSize, &layout, &extent);
        }

        Generation++;
    }

    public void WritePbgra32(Pbgra32PixelBuffer pixels)
    {
        if (Width > int.MaxValue
            || Height > int.MaxValue
            || pixels.Width != (int)Width
            || pixels.Height != (int)Height)
        {
            throw new ArgumentException("PBgra32 pixel buffer dimensions must match the texture dimensions.", nameof(pixels));
        }

        WritePbgra32SubRect(pixels, 0, 0);
    }

    public void WritePbgra32SubRect(Pbgra32PixelBuffer pixels, uint x, uint y)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        if (!pixels.IsValid)
        {
            throw new ArgumentException("PBgra32 pixel buffer is not valid.", nameof(pixels));
        }

        EnsurePbgra32CompatibleFormat();

        var subWidth = (uint)pixels.Width;
        var subHeight = (uint)pixels.Height;
        if (x > Width
            || y > Height
            || subWidth > Width - x
            || subHeight > Height - y)
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), "PBgra32 pixel buffer does not fit inside the texture bounds.");
        }

        WritePixelsSubRect(pixels.CopyCompactRows(), x, y, subWidth, subHeight);
        AlphaMode = GpuTextureAlphaMode.Premultiplied;
    }

    public void WritePixelsSubRect<T>(
        ReadOnlySpan<T> pixels,
        uint x,
        uint y,
        uint subWidth,
        uint subHeight,
        uint arrayLayer = 0,
        uint mipLevel = 0)
        where T : unmanaged
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        ValidateMipLevel(mipLevel);
        if (subWidth == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subWidth), "Pixel sub-rect width must be greater than zero.");
        }

        if (subHeight == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subHeight), "Pixel sub-rect height must be greater than zero.");
        }

        var mipWidth = GetMipDimension(Width, mipLevel);
        var mipHeight = GetMipDimension(Height, mipLevel);
        if (x > mipWidth
            || y > mipHeight
            || subWidth > mipWidth - x
            || subHeight > mipHeight - y)
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), "Pixel sub-rect does not fit inside the texture bounds.");
        }

        if (arrayLayer >= GetMipDepthOrArrayLayers(mipLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(arrayLayer), "Pixel sub-rect array layer or depth slice is outside the texture.");
        }

        uint bytesPerPixel = GetBytesPerPixel(Format);

        uint expectedSize = subWidth * subHeight * bytesPerPixel;
        uint passedSize = (uint)(pixels.Length * sizeof(T));
        if (passedSize < expectedSize)
        {
            throw new ArgumentException($"Pixel span is too small for sub-rect ({passedSize} bytes, expected {expectedSize} bytes).");
        }

        var destination = new ImageCopyTexture
        {
            Texture = TexturePtr,
            MipLevel = mipLevel,
            Origin = new Origin3D { X = x, Y = y, Z = arrayLayer },
            Aspect = GetTextureCopyAspect(Format)
        };

        var layout = new TextureDataLayout
        {
            Offset = 0,
            BytesPerRow = subWidth * bytesPerPixel,
            RowsPerImage = subHeight
        };

        var extent = new Extent3D
        {
            Width = subWidth,
            Height = subHeight,
            DepthOrArrayLayers = 1
        };

        fixed (T* ptr = pixels)
        {
            _context.Api.QueueWriteTexture(_context.Queue, &destination, ptr, passedSize, &layout, &extent);
        }

        Generation++;
    }

    public void WritePixelsVolume<T>(
        ReadOnlySpan<T> pixels,
        uint x,
        uint y,
        uint z,
        uint subWidth,
        uint subHeight,
        uint subDepth,
        uint mipLevel = 0)
        where T : unmanaged
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        ValidateMipLevel(mipLevel);
        if (Dimension != GpuTextureDimension.Dimension3D)
        {
            throw new InvalidOperationException("Volume uploads require a 3D GPU texture.");
        }

        if (subWidth == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subWidth), "Pixel volume width must be greater than zero.");
        }

        if (subHeight == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subHeight), "Pixel volume height must be greater than zero.");
        }

        if (subDepth == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subDepth), "Pixel volume depth must be greater than zero.");
        }

        var mipWidth = GetMipDimension(Width, mipLevel);
        var mipHeight = GetMipDimension(Height, mipLevel);
        var mipDepth = GetMipDepthOrArrayLayers(mipLevel);
        if (x > mipWidth
            || y > mipHeight
            || z > mipDepth
            || subWidth > mipWidth - x
            || subHeight > mipHeight - y
            || subDepth > mipDepth - z)
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), "Pixel volume does not fit inside the texture bounds.");
        }

        uint bytesPerPixel = GetBytesPerPixel(Format);
        uint expectedSize = subWidth * subHeight * subDepth * bytesPerPixel;
        uint passedSize = (uint)(pixels.Length * sizeof(T));
        if (passedSize < expectedSize)
        {
            throw new ArgumentException($"Pixel span is too small for volume ({passedSize} bytes, expected {expectedSize} bytes).");
        }

        var destination = new ImageCopyTexture
        {
            Texture = TexturePtr,
            MipLevel = mipLevel,
            Origin = new Origin3D { X = x, Y = y, Z = z },
            Aspect = GetTextureCopyAspect(Format)
        };

        var layout = new TextureDataLayout
        {
            Offset = 0,
            BytesPerRow = subWidth * bytesPerPixel,
            RowsPerImage = subHeight
        };

        var extent = new Extent3D
        {
            Width = subWidth,
            Height = subHeight,
            DepthOrArrayLayers = subDepth
        };

        fixed (T* ptr = pixels)
        {
            _context.Api.QueueWriteTexture(_context.Queue, &destination, ptr, passedSize, &layout, &extent);
        }

        Generation++;
    }

    public void MarkContentsDirty()
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));

        Generation++;
    }

    public void GenerateMipmaps2DLinear(
        uint baseMipLevel = 0,
        uint? mipLevelCount = null,
        uint baseArrayLayer = 0,
        uint? arrayLayerCount = null)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        ValidateMipGenerationRange(baseMipLevel, mipLevelCount, baseArrayLayer, arrayLayerCount);

        var levels = mipLevelCount ?? (MipLevelCount - baseMipLevel);
        if (levels <= 1)
        {
            return;
        }

        var layers = arrayLayerCount ?? (DepthOrArrayLayers - baseArrayLayer);
        var wgpu = _context.Api;
        CommandEncoder* encoder = null;
        CommandBuffer* commandBuffer = null;
        try
        {
            var resources = GetMipGeneratorResources();

            var encoderDesc = new CommandEncoderDescriptor();
            encoder = wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
            if (encoder == null)
            {
                throw new InvalidOperationException("Failed to create command encoder for mip generation.");
            }

            for (var arrayLayer = baseArrayLayer; arrayLayer < baseArrayLayer + layers; arrayLayer++)
            {
                for (var destinationMip = baseMipLevel + 1; destinationMip < baseMipLevel + levels; destinationMip++)
                {
                    GenerateMipLevel2DLinear(
                        encoder,
                        resources.Pipeline,
                        resources.BindGroupLayout,
                        resources.Sampler,
                        sourceMipLevel: destinationMip - 1,
                        destinationMipLevel: destinationMip,
                        arrayLayer);
                }
            }

            var commandBufferDesc = new CommandBufferDescriptor();
            commandBuffer = wgpu.CommandEncoderFinish(encoder, &commandBufferDesc);
            if (commandBuffer != null)
            {
                wgpu.QueueSubmit(_context.Queue, 1, &commandBuffer);
            }
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
        }

        Generation++;
    }

    private void GenerateMipLevel2DLinear(
        CommandEncoder* encoder,
        RenderPipeline* pipeline,
        BindGroupLayout* bindGroupLayout,
        Sampler* sampler,
        uint sourceMipLevel,
        uint destinationMipLevel,
        uint arrayLayer)
    {
        var wgpu = _context.Api;
        var sourceView = CreateMipGeneratorTextureView(sourceMipLevel, arrayLayer);
        var destinationView = CreateMipGeneratorTextureView(destinationMipLevel, arrayLayer);
        var bindGroup = CreateMipGeneratorBindGroup(bindGroupLayout, sampler, sourceView);
        RenderPassEncoder* pass = null;
        try
        {
            var colorAttachment = new RenderPassColorAttachment
            {
                View = destinationView,
                ResolveTarget = null,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = new Color()
            };
            var passDesc = new RenderPassDescriptor
            {
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment
            };

            pass = wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);
            if (pass == null)
            {
                throw new InvalidOperationException("Failed to begin render pass for mip generation.");
            }

            wgpu.RenderPassEncoderSetPipeline(pass, pipeline);
            wgpu.RenderPassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
            wgpu.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
            wgpu.RenderPassEncoderEnd(pass);
        }
        finally
        {
            if (pass != null)
            {
                wgpu.RenderPassEncoderRelease(pass);
            }

            if (bindGroup != null)
            {
                wgpu.BindGroupRelease(bindGroup);
            }

            if (sourceView != null)
            {
                wgpu.TextureViewRelease(sourceView);
            }

            if (destinationView != null)
            {
                wgpu.TextureViewRelease(destinationView);
            }
        }
    }

    private MipGeneratorResources GetMipGeneratorResources()
    {
        MipGeneratorResourceCache cache;
        lock (s_mipGeneratorCacheLock)
        {
            if (!s_mipGeneratorCaches.TryGetValue(_context, out cache!))
            {
                cache = new MipGeneratorResourceCache(_context);
                s_mipGeneratorCaches.Add(_context, cache);
            }
        }

        return cache.GetOrCreate(Format);
    }

    private static void ReleaseMipGeneratorCache(WgpuContext context)
    {
        MipGeneratorResourceCache? cache;
        lock (s_mipGeneratorCacheLock)
        {
            if (!s_mipGeneratorCaches.Remove(context, out cache))
            {
                return;
            }
        }

        cache.QueueDisposal();
    }

    private static ShaderModule* CreateMipGeneratorShaderModule(WgpuContext context)
    {
        var sourcePtr = SilkMarshal.StringToPtr(MipGeneratorShader);
        var labelPtr = SilkMarshal.StringToPtr("ProGPU Mip Generator Shader");
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

            var module = context.Api.DeviceCreateShaderModule(context.Device, &moduleDesc);
            if (module == null)
            {
                throw new InvalidOperationException("Failed to create mip generator shader module.");
            }

            return module;
        }
        finally
        {
            SilkMarshal.Free(sourcePtr);
            SilkMarshal.Free(labelPtr);
        }
    }

    private static Sampler* CreateMipGeneratorSampler(WgpuContext context)
    {
        var samplerDesc = new SamplerDescriptor
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

        var sampler = context.Api.DeviceCreateSampler(context.Device, &samplerDesc);
        if (sampler == null)
        {
            throw new InvalidOperationException("Failed to create mip generator sampler.");
        }

        return sampler;
    }

    private static BindGroupLayout* CreateMipGeneratorBindGroupLayout(WgpuContext context)
    {
        var entries = stackalloc BindGroupLayoutEntry[2];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout
            {
                Type = SamplerBindingType.Filtering
            }
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
        var desc = new BindGroupLayoutDescriptor
        {
            EntryCount = 2,
            Entries = entries
        };

        var layout = context.Api.DeviceCreateBindGroupLayout(context.Device, &desc);
        if (layout == null)
        {
            throw new InvalidOperationException("Failed to create mip generator bind-group layout.");
        }

        return layout;
    }

    private static PipelineLayout* CreateMipGeneratorPipelineLayout(WgpuContext context, BindGroupLayout* bindGroupLayout)
    {
        var layouts = stackalloc BindGroupLayout*[1];
        layouts[0] = bindGroupLayout;
        var desc = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 1,
            BindGroupLayouts = layouts
        };

        var layout = context.Api.DeviceCreatePipelineLayout(context.Device, &desc);
        if (layout == null)
        {
            throw new InvalidOperationException("Failed to create mip generator pipeline layout.");
        }

        return layout;
    }

    private static RenderPipeline* CreateMipGeneratorPipeline(
        WgpuContext context,
        ShaderModule* shader,
        PipelineLayout* pipelineLayout,
        TextureFormat format)
    {
        var vertexEntryPtr = SilkMarshal.StringToPtr("vs_main");
        var fragmentEntryPtr = SilkMarshal.StringToPtr("fs_main");
        var labelPtr = SilkMarshal.StringToPtr("ProGPU Mip Generator Pipeline");
        try
        {
            var vertexState = new VertexState
            {
                Module = shader,
                EntryPoint = (byte*)vertexEntryPtr,
                BufferCount = 0,
                Buffers = null
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
                EntryPoint = (byte*)fragmentEntryPtr,
                TargetCount = 1,
                Targets = &colorTarget
            };
            var pipelineDesc = new RenderPipelineDescriptor
            {
                Label = (byte*)labelPtr,
                Layout = pipelineLayout,
                Vertex = vertexState,
                Primitive = new PrimitiveState
                {
                    Topology = PrimitiveTopology.TriangleList,
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = FrontFace.Ccw,
                    CullMode = CullMode.None
                },
                DepthStencil = null,
                Multisample = new MultisampleState
                {
                    Count = 1,
                    Mask = 0xFFFFFFFF,
                    AlphaToCoverageEnabled = false
                },
                Fragment = &fragmentState
            };

            var pipeline = context.Api.DeviceCreateRenderPipeline(context.Device, &pipelineDesc);
            if (pipeline == null)
            {
                throw new InvalidOperationException("Failed to create mip generator render pipeline.");
            }

            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(vertexEntryPtr);
            SilkMarshal.Free(fragmentEntryPtr);
            SilkMarshal.Free(labelPtr);
        }
    }

    private TextureView* CreateMipGeneratorTextureView(uint mipLevel, uint arrayLayer)
    {
        var viewDesc = new TextureViewDescriptor
        {
            Format = Format,
            Dimension = TextureViewDimension.Dimension2D,
            BaseMipLevel = mipLevel,
            MipLevelCount = 1,
            BaseArrayLayer = arrayLayer,
            ArrayLayerCount = 1,
            Aspect = TextureAspect.All
        };

        var view = _context.Api.TextureCreateView(TexturePtr, &viewDesc);
        if (view == null)
        {
            throw new InvalidOperationException($"Failed to create mip generator texture view for mip {mipLevel}, layer {arrayLayer}.");
        }

        return view;
    }

    private BindGroup* CreateMipGeneratorBindGroup(
        BindGroupLayout* bindGroupLayout,
        Sampler* sampler,
        TextureView* sourceView)
    {
        var entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry
        {
            Binding = 0,
            Sampler = sampler
        };
        entries[1] = new BindGroupEntry
        {
            Binding = 1,
            TextureView = sourceView
        };
        var bindGroupDesc = new BindGroupDescriptor
        {
            Layout = bindGroupLayout,
            EntryCount = 2,
            Entries = entries
        };

        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bindGroupDesc);
        if (bindGroup == null)
        {
            throw new InvalidOperationException("Failed to create mip generator bind group.");
        }

        return bindGroup;
    }

    private static void ReleaseMipGeneratorResourcesNow(
        WgpuContext context,
        ShaderModule* shader,
        Sampler* sampler,
        RenderPipeline* pipeline,
        BindGroupLayout* bindGroupLayout,
        PipelineLayout* pipelineLayout)
    {
        var wgpu = context.Api;
        if (pipeline != null)
        {
            wgpu.RenderPipelineRelease(pipeline);
        }

        if (pipelineLayout != null)
        {
            wgpu.PipelineLayoutRelease(pipelineLayout);
        }

        if (bindGroupLayout != null)
        {
            wgpu.BindGroupLayoutRelease(bindGroupLayout);
        }

        if (sampler != null)
        {
            wgpu.SamplerRelease(sampler);
        }

        if (shader != null)
        {
            wgpu.ShaderModuleRelease(shader);
        }
    }

    public void CopyFrom(GpuTexture source)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        if (source.Context != _context)
        {
            throw new ArgumentException("Source texture must belong to the same WebGPU context.", nameof(source));
        }

        if (source.Width != Width
            || source.Height != Height
            || source.DepthOrArrayLayers != DepthOrArrayLayers
            || source.MipLevelCount != MipLevelCount
            || source.Dimension != Dimension
            || source.Format != Format
            || source.SampleCount != SampleCount)
        {
            throw new ArgumentException("Source texture dimensions, format, and sample count must match the destination texture.", nameof(source));
        }

        if (!source.Usage.HasFlag(TextureUsage.CopySrc))
        {
            throw new InvalidOperationException("Source texture was not created with CopySrc usage.");
        }

        if (!Usage.HasFlag(TextureUsage.CopyDst))
        {
            throw new InvalidOperationException("Destination texture was not created with CopyDst usage.");
        }

        var encoderDesc = new CommandEncoderDescriptor();
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        if (encoder == null)
        {
            throw new InvalidOperationException("Failed to create command encoder for texture copy.");
        }

        for (uint mipLevel = 0; mipLevel < MipLevelCount; mipLevel++)
        {
            var copySource = new ImageCopyTexture
            {
                Texture = source.TexturePtr,
                MipLevel = mipLevel,
                Origin = new Origin3D(),
                Aspect = GetTextureCopyAspect(source.Format)
            };

            var copyDestination = new ImageCopyTexture
            {
                Texture = TexturePtr,
                MipLevel = mipLevel,
                Origin = new Origin3D(),
                Aspect = GetTextureCopyAspect(Format)
            };

            var copySize = new Extent3D
            {
                Width = GetMipDimension(Width, mipLevel),
                Height = GetMipDimension(Height, mipLevel),
                DepthOrArrayLayers = GetMipDepthOrArrayLayers(mipLevel)
            };

            _context.Api.CommandEncoderCopyTextureToTexture(encoder, &copySource, &copyDestination, &copySize);
        }

        var commandBufferDesc = new CommandBufferDescriptor();
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandBufferDesc);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);
        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);

        AlphaMode = source.AlphaMode;
        Generation++;
    }

    public void CopyBaseLevelFrom(GpuTexture source)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        if (!ReferenceEquals(source.Context, _context))
        {
            throw new ArgumentException(
                "Source texture must belong to the same WebGPU context.",
                nameof(source));
        }

        if (source.Width != Width
            || source.Height != Height
            || source.DepthOrArrayLayers != DepthOrArrayLayers
            || source.Dimension != Dimension
            || source.Format != Format
            || source.SampleCount != SampleCount)
        {
            throw new ArgumentException(
                "Source texture base dimensions, format, and sample count must match the destination texture.",
                nameof(source));
        }

        if (!source.Usage.HasFlag(TextureUsage.CopySrc))
        {
            throw new InvalidOperationException("Source texture was not created with CopySrc usage.");
        }
        if (!Usage.HasFlag(TextureUsage.CopyDst))
        {
            throw new InvalidOperationException("Destination texture was not created with CopyDst usage.");
        }

        var encoderDescriptor = new CommandEncoderDescriptor();
        var encoder = _context.Api.DeviceCreateCommandEncoder(
            _context.Device,
            &encoderDescriptor);
        if (encoder == null)
        {
            throw new InvalidOperationException(
                "Failed to create a command encoder for the base-level texture copy.");
        }

        var copySource = new ImageCopyTexture
        {
            Texture = source.TexturePtr,
            MipLevel = 0,
            Origin = new Origin3D(),
            Aspect = GetTextureCopyAspect(source.Format)
        };
        var copyDestination = new ImageCopyTexture
        {
            Texture = TexturePtr,
            MipLevel = 0,
            Origin = new Origin3D(),
            Aspect = GetTextureCopyAspect(Format)
        };
        var copySize = new Extent3D
        {
            Width = Width,
            Height = Height,
            DepthOrArrayLayers = DepthOrArrayLayers
        };
        _context.Api.CommandEncoderCopyTextureToTexture(
            encoder,
            &copySource,
            &copyDestination,
            &copySize);

        var commandBufferDescriptor = new CommandBufferDescriptor();
        var commandBuffer = _context.Api.CommandEncoderFinish(
            encoder,
            &commandBufferDescriptor);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);
        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);

        AlphaMode = source.AlphaMode;
        Generation++;
    }

    private void EnsurePbgra32CompatibleFormat()
    {
        if (Format is not (TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb))
        {
            throw new InvalidOperationException($"PBgra32 uploads require a BGRA8 texture format. Actual format: {Format}.");
        }
    }

    private void ValidateMipGenerationRange(
        uint baseMipLevel,
        uint? mipLevelCount,
        uint baseArrayLayer,
        uint? arrayLayerCount)
    {
        if (Dimension != GpuTextureDimension.Dimension2D)
        {
            throw new NotSupportedException("GPU mip generation currently supports only 2D textures.");
        }

        if (SampleCount != 1)
        {
            throw new NotSupportedException("GPU mip generation currently supports only single-sample textures.");
        }

        if (!Usage.HasFlag(TextureUsage.TextureBinding))
        {
            throw new InvalidOperationException("GPU mip generation requires TextureBinding usage.");
        }

        if (!Usage.HasFlag(TextureUsage.RenderAttachment))
        {
            throw new InvalidOperationException("GPU mip generation requires RenderAttachment usage.");
        }

        if (!IsMipGenerationFormat(Format))
        {
            throw new NotSupportedException($"GPU mip generation does not support texture format {Format}.");
        }

        if (baseMipLevel >= MipLevelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(baseMipLevel), "Base mip level is outside the texture mip chain.");
        }

        var levels = mipLevelCount ?? (MipLevelCount - baseMipLevel);
        if (levels == 0 || levels > MipLevelCount - baseMipLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(mipLevelCount), "Mip generation range exceeds the texture mip chain.");
        }

        if (baseArrayLayer >= DepthOrArrayLayers)
        {
            throw new ArgumentOutOfRangeException(nameof(baseArrayLayer), "Base array layer is outside the texture array.");
        }

        var layers = arrayLayerCount ?? (DepthOrArrayLayers - baseArrayLayer);
        if (layers == 0 || layers > DepthOrArrayLayers - baseArrayLayer)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayLayerCount), "Mip generation array range exceeds the texture array.");
        }
    }

    private static bool IsMipGenerationFormat(TextureFormat format)
    {
        return format is
            TextureFormat.Rgba8Unorm or
            TextureFormat.Rgba8UnormSrgb or
            TextureFormat.Bgra8Unorm or
            TextureFormat.Bgra8UnormSrgb;
    }

    private static uint GetBytesPerPixel(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.R8Unorm or
            TextureFormat.R8Snorm or
            TextureFormat.R8Uint or
            TextureFormat.R8Sint => 1,

            TextureFormat.R16Uint or
            TextureFormat.R16Sint or
            TextureFormat.R16float or
            TextureFormat.Depth16Unorm => 2,

            TextureFormat.RG8Unorm or
            TextureFormat.RG8Snorm or
            TextureFormat.RG8Uint or
            TextureFormat.RG8Sint => 2,

            TextureFormat.R32Uint or
            TextureFormat.R32Sint or
            TextureFormat.R32float or
            TextureFormat.RG16Uint or
            TextureFormat.RG16Sint or
            TextureFormat.RG16float or
            TextureFormat.Rgba8Unorm or
            TextureFormat.Rgba8UnormSrgb or
            TextureFormat.Rgba8Snorm or
            TextureFormat.Rgba8Uint or
            TextureFormat.Rgba8Sint or
            TextureFormat.Bgra8Unorm or
            TextureFormat.Bgra8UnormSrgb or
            TextureFormat.Rgb10A2Unorm or
            TextureFormat.Rgb10A2Uint or
            TextureFormat.Depth24Plus or
            TextureFormat.Depth24PlusStencil8 or
            TextureFormat.Depth32float => 4,

            TextureFormat.RG32Uint or
            TextureFormat.RG32Sint or
            TextureFormat.RG32float or
            TextureFormat.Rgba16Uint or
            TextureFormat.Rgba16Sint or
            TextureFormat.Rgba16float => 8,

            TextureFormat.Rgba32Uint or
            TextureFormat.Rgba32Sint or
            TextureFormat.Rgba32float => 16,

            _ => throw new NotSupportedException($"Texture format {format} does not have a supported compact pixel size.")
        };
    }

    private static TextureAspect GetTextureCopyAspect(TextureFormat format)
    {
        return format switch
        {
            TextureFormat.Depth16Unorm or
            TextureFormat.Depth24Plus or
            TextureFormat.Depth24PlusStencil8 or
            TextureFormat.Depth32float => TextureAspect.DepthOnly,
            _ => TextureAspect.All
        };
    }

    private void ValidateMipLevel(uint mipLevel)
    {
        if (mipLevel >= MipLevelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(mipLevel), "Texture mip level is outside the texture mip chain.");
        }
    }

    private static uint GetMipDimension(uint dimension, uint mipLevel)
    {
        if (mipLevel >= 31)
        {
            return 1;
        }

        var shifted = dimension >> checked((int)mipLevel);
        return Math.Max(1u, shifted);
    }

    public static void CleanupPendingResources(WgpuContext context)
    {
        context.CleanupPendingResources();
    }

    public byte[] ReadPixels(uint mipLevel = 0)
    {
        uint requiredBytes = GetReadPixelsByteCount(mipLevel);
        byte[] unpaddedPixels = new byte[checked((int)requiredBytes)];
        ReadPixels(unpaddedPixels, mipLevel);
        return unpaddedPixels;
    }

    public void ReadPixels(
        Span<byte> destination,
        uint mipLevel = 0,
        uint originDepthOrArrayLayer = 0,
        uint depthOrArrayLayers = 0)
    {
        var readbackBuffer = new GpuTextureReadbackBuffer(_context);
        try
        {
            ReadPixels(
                destination,
                readbackBuffer,
                mipLevel,
                originDepthOrArrayLayer,
                depthOrArrayLayers);
        }
        finally
        {
            readbackBuffer.Dispose();
            _context.CleanupPendingResources();
        }
    }

    public void ReadPixels(
        Span<byte> destination,
        GpuTextureReadbackBuffer readbackBuffer,
        uint mipLevel = 0,
        uint originDepthOrArrayLayer = 0,
        uint depthOrArrayLayers = 0)
    {
        ArgumentNullException.ThrowIfNull(readbackBuffer);
        if (IsDisposed) throw new ObjectDisposedException(nameof(GpuTexture));
        if (!Usage.HasFlag(TextureUsage.CopySrc))
        {
            throw new InvalidOperationException("Texture was not created with CopySrc usage.");
        }

        ValidateMipLevel(mipLevel);

        uint bytesPerPixel = GetBytesPerPixel(Format);
        uint mipWidth = GetMipDimension(Width, mipLevel);
        uint mipHeight = GetMipDimension(Height, mipLevel);
        uint mipDepthOrLayers = GetMipDepthOrArrayLayers(mipLevel);
        if (originDepthOrArrayLayer >= mipDepthOrLayers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originDepthOrArrayLayer),
                "Texture readback origin exceeds the mip depth/array-layer range.");
        }

        uint availableDepthOrLayers = mipDepthOrLayers - originDepthOrArrayLayer;
        uint readDepthOrLayers = depthOrArrayLayers == 0 ? availableDepthOrLayers : depthOrArrayLayers;
        if (readDepthOrLayers > availableDepthOrLayers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(depthOrArrayLayers),
                "Texture readback depth/array-layer range exceeds the mip bounds.");
        }

        uint bytesPerRow = checked(mipWidth * bytesPerPixel);
        uint bytesPerImage = checked(bytesPerRow * mipHeight);
        uint requiredBytes = checked(bytesPerImage * readDepthOrLayers);
        if (destination.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"Destination span is too small ({destination.Length} bytes, expected {requiredBytes} bytes).",
                nameof(destination));
        }

        fixed (byte* destinationPtr = destination)
        {
            bool read = readbackBuffer.TryReadTextureRows(
                this,
                mipWidth,
                mipHeight,
                readDepthOrLayers,
                mipLevel,
                originDepthOrArrayLayer,
                GetTextureCopyAspect(Format),
                destinationPtr,
                bytesPerRow,
                bytesPerImage,
                bytesPerPixel);

            if (!read)
            {
                if (readbackBuffer.LastMapTimedOut)
                {
                    throw new TimeoutException($"WebGPU BufferMapAsync timed out after {GpuTextureReadbackBuffer.DefaultMapTimeoutMilliseconds / 1000} seconds during texture readback.");
                }

                if (readbackBuffer.LastMapStatus != BufferMapAsyncStatus.Success)
                {
                    throw new InvalidOperationException($"Failed to map readback buffer. WebGPU Status: {readbackBuffer.LastMapStatus}");
                }

                throw new InvalidOperationException("Failed to copy texture readback rows into the destination buffer.");
            }
        }
    }

    private uint GetReadPixelsByteCount(uint mipLevel)
    {
        ValidateMipLevel(mipLevel);
        uint bytesPerPixel = GetBytesPerPixel(Format);
        uint mipWidth = GetMipDimension(Width, mipLevel);
        uint mipHeight = GetMipDimension(Height, mipLevel);
        uint mipDepthOrLayers = GetMipDepthOrArrayLayers(mipLevel);
        uint bytesPerRow = checked(mipWidth * bytesPerPixel);
        uint bytesPerImage = checked(bytesPerRow * mipHeight);
        return checked(bytesPerImage * mipDepthOrLayers);
    }

    private uint GetMipDepthOrArrayLayers(uint mipLevel)
    {
        return Dimension == GpuTextureDimension.Dimension3D
            ? GetMipDimension(DepthOrArrayLayers, mipLevel)
            : DepthOrArrayLayers;
    }

    private void ReleaseResources(bool immediate = false)
    {
        OnDisposedWithId?.Invoke(Id);

        lock (_context.RenderLock)
        {
            if (_context.IsDisposed)
            {
                ViewPtr = null;
                TexturePtr = null;
                return;
            }

            if (immediate)
            {
                if (ViewPtr != null)
                {
                    _context.Api.TextureViewRelease(ViewPtr);
                    ViewPtr = null;
                }

                if (TexturePtr != null)
                {
                    _context.Api.TextureDestroy(TexturePtr);
                    _context.Api.TextureRelease(TexturePtr);
                    TexturePtr = null;
                }
            }
            else
            {
                if (ViewPtr != null)
                {
                    _context.QueueTextureViewDisposal((IntPtr)ViewPtr);
                    ViewPtr = null;
                }

                if (TexturePtr != null)
                {
                    _context.QueueTextureDisposal((IntPtr)TexturePtr);
                    TexturePtr = null;
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        if (TexturePtr != null || ViewPtr != null)
        {
            ReleaseResources(Environment.HasShutdownStarted || _context.IsDisposed);
        }

        GC.SuppressFinalize(this);
    }

    ~GpuTexture()
    {
        FinalizeResources();
    }

    private void FinalizeResources()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        var view = (IntPtr)ViewPtr;
        var texture = (IntPtr)TexturePtr;
        ViewPtr = null;
        TexturePtr = null;

        try
        {
            OnDisposedWithId?.Invoke(Id);
        }
        catch
        {
        }

        var context = _context;
        if (context is null || context.IsDisposed)
        {
            return;
        }

        try
        {
            if (view != IntPtr.Zero)
            {
                context.QueueTextureViewDisposal(view);
            }

            if (texture != IntPtr.Zero)
            {
                context.QueueTextureDisposal(texture);
            }
        }
        catch
        {
        }
    }
}
