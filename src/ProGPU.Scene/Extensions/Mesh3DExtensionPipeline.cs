using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Silk.NET.WebGPU;
using Silk.NET.Core.Native;
using ProGPU.Vector;
using ProGPU.Backend;

namespace ProGPU.Scene.Extensions
{
    public enum RenderMode3D
    {
        Solid = 0,
        Wireframe = 1,
        SolidWireframe = 2
    }

    public enum ShadingMode3D
    {
        Realistic = 0,
        Conceptual = 1,
        Flat = 2,
        HiddenLine = 3,
        ShadesOfGray = 4,
        XRay = 5,
        Normals = 6
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GpuVertex3D
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TextureCoordinate;

        public GpuVertex3D(
            Vector3 position,
            Vector3 normal,
            Vector2 textureCoordinate)
        {
            Position = position;
            Normal = normal;
            TextureCoordinate = textureCoordinate;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GpuMesh3DRecord
    {
        public Matrix4x4 ModelTransform;      // 3D Model transform for lighting
        public Matrix4x4 NormalTransform;     // Inverse-transpose for normal transformation
        public Vector4 Color;                 // Diffuse Color Kd
        public Vector4 LightDirection;        // xyz = direction, w = intensity
        public Vector4 AmbientColor;          // rgb = color, w = intensity
        public Vector4 SpecularColor;         // rgb = Specular Ks, w = Exponent Ns
        public Vector4 MaterialAmbient;       // rgb = Material Ka, w = unused
        public float Opacity;
        public float RenderMode;              // 0.0f = Solid, 1.0f = Wireframe, 2.0f = SolidWireframe
        public float ShadingMode;             // AutoCAD Shading Mode (0=Realistic, 1=Conceptual, 2=Flat, 3=HiddenLine, 4=ShadesOfGray, 5=XRay, 6=Normals)
        private float _pad2;
        public Vector4 TextureEffects0;        // brightness, contrast, saturation, grayscale
        public Vector4 TextureEffects1;        // sepia, invert, blur sigma, texture enabled
        public Vector4 TextureInfo;            // width, height, premultiplied source, luminance-to-alpha
        public Vector4 ColorMatrixRed;
        public Vector4 ColorMatrixGreen;
        public Vector4 ColorMatrixBlue;
        public Vector4 ColorMatrixAlpha;
        public Vector4 ColorMatrixOffset;
        public Vector4 TextureFlags;           // color matrix, YUV, quarter turns, mirrored
        public Vector4 YuvRange;
        public Vector4 YuvRed;
        public Vector4 YuvGreen;
        public Vector4 YuvBlue;
        public Vector4 TextureSourceRect;      // normalized x, y, width, height
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GpuMesh3DUniforms
    {
        public Matrix4x4 Projection;
        public Matrix4x4 View;
        public Vector3 CameraPosition;
        private float _pad;
    }

    internal sealed class Mesh3DCompileScratch
    {
        private GpuMesh3DRecord[] _records =
            Array.Empty<GpuMesh3DRecord>();
        private nint[] _textureBindGroups =
            Array.Empty<nint>();
        private uint[] _recordIndices =
            Array.Empty<uint>();

        internal int Capacity => _records.Length;

        internal Span<GpuMesh3DRecord> Records =>
            _records;

        internal Span<nint> TextureBindGroups =>
            _textureBindGroups;

        internal Span<uint> RecordIndices =>
            _recordIndices;

        internal void EnsureCapacity(int requiredCapacity)
        {
            if (requiredCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requiredCapacity));
            }
            if (requiredCapacity <= _records.Length)
            {
                return;
            }
            if (requiredCapacity > Array.MaxLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requiredCapacity));
            }

            int capacity = Math.Max(4, _records.Length);
            while (capacity < requiredCapacity)
            {
                int growth = Math.Max(4, capacity);
                capacity = capacity >
                    Array.MaxLength - growth
                        ? Array.MaxLength
                        : capacity + growth;
            }

            var records =
                new GpuMesh3DRecord[capacity];
            var textureBindGroups =
                new nint[capacity];
            var recordIndices =
                new uint[capacity];
            _records.AsSpan().CopyTo(records);
            _textureBindGroups.AsSpan().CopyTo(
                textureBindGroups);
            _recordIndices.AsSpan().CopyTo(
                recordIndices);
            _records = records;
            _textureBindGroups = textureBindGroups;
            _recordIndices = recordIndices;
        }
    }

    public class Mesh3DExtensionPipeline : ICompositorExtension
    {


        private static readonly string Mesh3DSolidShaderCode = ShaderResource.Load(typeof(Mesh3DExtensionPipeline), "Mesh3DSolid.wgsl");
 
        private static readonly string Mesh3DWireframeShaderCode = ShaderResource.Load(typeof(Mesh3DExtensionPipeline), "Mesh3DWireframe.wgsl");

        private class CachedGeometry
        {
            public GpuBuffer VertexBuffer = null!;
            public uint VertexCount;
            public int Version;
        }

        private class ViewportResource
        {
            public GpuBuffer UniformsBuffer;
            public GpuBuffer? DynamicRecordsBuffer;
            public GpuBuffer? RecordIndexBuffer;
            public unsafe BindGroup* SolidBindGroup;
            public unsafe BindGroup* WireframeBindGroup;
            public int RecordGen = -1;
            public uint SampleCount;

            public ViewportResource(WgpuContext context, uint uniformsSize)
            {
                UniformsBuffer = new GpuBuffer(context, uniformsSize, BufferUsage.Uniform | BufferUsage.CopyDst, "Mesh3D Uniforms Buffer");
            }
            
            public unsafe void Dispose(WgpuContext context)
            {
                UniformsBuffer.Dispose();
                DynamicRecordsBuffer?.Dispose();
                RecordIndexBuffer?.Dispose();
                if (SolidBindGroup != null) context.Api.BindGroupRelease(SolidBindGroup);
                if (WireframeBindGroup != null) context.Api.BindGroupRelease(WireframeBindGroup);
            }
        }

        private sealed class LiveMaterialBlurResources :
            IDisposable
        {
            private const TextureUsage Usage =
                TextureUsage.TextureBinding |
                TextureUsage.RenderAttachment;

            public LiveMaterialBlurResources(
                GpuTexture source,
                bool isPlanar,
                int resourceIndex)
            {
                TextureFormat format = isPlanar
                    ? TextureFormat.Rgba16float
                    : source.Format;
                GpuTextureAlphaMode alphaMode = isPlanar
                    ? GpuTextureAlphaMode.Straight
                    : source.AlphaMode;
                IsPlanar = isPlanar;
                Intermediate = new GpuTexture(
                    source.Context,
                    source.Width,
                    source.Height,
                    format,
                    Usage,
                    $"Live Mesh3D material blur intermediate {resourceIndex}",
                    alphaMode: alphaMode);
                try
                {
                    Output = new GpuTexture(
                        source.Context,
                        source.Width,
                        source.Height,
                        format,
                        Usage,
                        $"Live Mesh3D material blur output {resourceIndex}",
                        alphaMode: alphaMode);
                }
                catch
                {
                    Intermediate.Dispose();
                    throw;
                }
            }

            public GpuTexture Intermediate { get; }
            public GpuTexture Output { get; }
            public bool IsPlanar { get; }
            public ulong LastUsedFrame { get; set; }
            public ulong PreparedFrame { get; set; }
            public ulong PreparedSourceId { get; set; }
            public uint PreparedSourceGeneration { get; set; }
            public ulong PreparedChromaId { get; set; }
            public uint PreparedChromaGeneration { get; set; }
            public ImageEffectYuvConversion?
                PreparedYuvConversion { get; set; }
            public int PreparedSigmaBits { get; set; }

            public bool MatchesStorage(
                GpuTexture source,
                bool isPlanar)
            {
                TextureFormat format = isPlanar
                    ? TextureFormat.Rgba16float
                    : source.Format;
                GpuTextureAlphaMode alphaMode = isPlanar
                    ? GpuTextureAlphaMode.Straight
                    : source.AlphaMode;
                return IsPlanar == isPlanar &&
                    ReferenceEquals(
                        Intermediate.Context,
                        source.Context) &&
                    Intermediate.Width == source.Width &&
                    Intermediate.Height == source.Height &&
                    Intermediate.Format == format &&
                    Intermediate.AlphaMode == alphaMode &&
                    !Intermediate.IsDisposed &&
                    !Output.IsDisposed;
            }

            public bool MatchesPrepared(
                GpuTexture source,
                GpuTexture? chroma,
                ImageEffectYuvConversion? yuvConversion,
                float standardDeviation,
                ulong frame)
            {
                return PreparedFrame == frame &&
                    PreparedSourceId == source.Id &&
                    PreparedSourceGeneration ==
                        source.Generation &&
                    PreparedChromaId ==
                        (chroma?.Id ?? 0) &&
                    PreparedChromaGeneration ==
                        (chroma?.Generation ?? 0) &&
                    YuvConversionsEqual(
                        PreparedYuvConversion,
                        yuvConversion) &&
                    PreparedSigmaBits ==
                        BitConverter.SingleToInt32Bits(
                            standardDeviation);
            }

            private static bool YuvConversionsEqual(
                ImageEffectYuvConversion? left,
                ImageEffectYuvConversion? right)
            {
                if (!left.HasValue ||
                    !right.HasValue)
                {
                    return left.HasValue ==
                        right.HasValue;
                }

                return left.Value.Range ==
                        right.Value.Range &&
                    left.Value.Red == right.Value.Red &&
                    left.Value.Green ==
                        right.Value.Green &&
                    left.Value.Blue ==
                        right.Value.Blue;
            }

            public void Dispose()
            {
                Output.Dispose();
                Intermediate.Dispose();
            }
        }

        private readonly Dictionary<object, CachedGeometry> _geometryCache = new();
        private readonly List<ViewportResource> _viewportResources = new();
        private readonly List<nint> _pendingCommandBuffers = new();
        private readonly List<nint> _pendingTextureBindGroups = new();
        private readonly List<IProGpuTextureLease> _pendingTextureLeases =
            new();
        private readonly List<LiveMaterialBlurResources>
            _liveMaterialBlurPool = new();
        private readonly Mesh3DCompileScratch _compileScratch =
            new();
        private int _usedLiveMaterialBlurCount;
        private int _preparedLiveMaterialCount;
        private int _liveMaterialBlurSubmissionCount;
        private int _currentCompileIndex;
        private WgpuContext? _context;
        private unsafe BindGroupLayout* _solidBindGroupLayout;
        private unsafe BindGroupLayout* _textureBindGroupLayout;
        private unsafe PipelineLayout* _solidPipelineLayout;
        private GpuTexture? _whiteTexture;
        private unsafe BindGroup* _whiteLinearBindGroup;
        private unsafe BindGroup* _whiteNearestBindGroup;
        
        private unsafe RenderPipeline* _cachedPipelineSingle;
        private unsafe RenderPipeline* _cachedBackFacePipelineSingle;
        private unsafe RenderPipeline* _cachedWireframePipelineSingle;
        private unsafe RenderPipeline* _cachedPipelineMsaa;
        private unsafe RenderPipeline* _cachedBackFacePipelineMsaa;
        private unsafe RenderPipeline* _cachedWireframePipelineMsaa;

        internal int LiveMaterialBlurResourceCount =>
            _liveMaterialBlurPool.Count;
        internal int PreparedLiveMaterialCount =>
            _preparedLiveMaterialCount;
        internal int LiveMaterialBlurSubmissionCount =>
            _liveMaterialBlurSubmissionCount;

        private unsafe RenderPipeline* CreateMeshPipeline(
            Compositor compositor,
            string shaderKey,
            string shaderCode,
            string shaderLabel,
            string pipelineKey,
            CullMode cullMode,
            uint sampleCount,
            PipelineLayout* pipelineLayout = null)
        {
            var shaderModule = compositor.PipelineCache.GetOrCreateShader(shaderKey, shaderCode, shaderLabel);

            Span<VertexAttribute> attrs = stackalloc VertexAttribute[3];
            attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 }; // Position
            attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 12, ShaderLocation = 1 }; // Normal
            attrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }; // UV

            Span<VertexAttribute> recordIndexAttrs = stackalloc VertexAttribute[1];
            recordIndexAttrs[0] = new VertexAttribute
            {
                Format = VertexFormat.Uint32,
                Offset = 0,
                ShaderLocation = 3
            };

            Span<VertexBufferLayout> layouts = stackalloc VertexBufferLayout[2];
            fixed (VertexAttribute* attrsPtr = attrs)
            fixed (VertexAttribute* recordIndexAttrsPtr = recordIndexAttrs)
            {
                layouts[0] = new VertexBufferLayout
                {
                    ArrayStride = (uint)Unsafe.SizeOf<GpuVertex3D>(),
                    StepMode = VertexStepMode.Vertex,
                    AttributeCount = 3,
                    Attributes = attrsPtr
                };
                layouts[1] = new VertexBufferLayout
                {
                    ArrayStride = sizeof(uint),
                    StepMode = VertexStepMode.Instance,
                    AttributeCount = 1,
                    Attributes = recordIndexAttrsPtr
                };

                return compositor.PipelineCache.GetOrCreateRenderPipeline(
                    pipelineKey,
                    shaderModule,
                    layouts,
                    topology: PrimitiveTopology.TriangleList,
                    targetFormat: TextureFormat.Rgba8Unorm,
                    enableDepthStencil: true,
                    depthFormat: TextureFormat.Depth24PlusStencil8,
                    sampleCount: sampleCount,
                    depthWriteEnabled: true,
                    depthCompare: CompareFunction.LessEqual,
                    cullMode: cullMode,
                    pipelineLayout: pipelineLayout
                );
            }
        }

        private unsafe void EnsureSolidLayouts(Compositor compositor)
        {
            if (_solidPipelineLayout != null)
            {
                return;
            }

            var wgpu = compositor.Context.Api;
            var device = compositor.Context.Device;

            var solidEntries = stackalloc BindGroupLayoutEntry[2];
            solidEntries[0] = new BindGroupLayoutEntry
            {
                Binding = 0,
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Buffer = new BufferBindingLayout
                {
                    Type = BufferBindingType.Uniform,
                    HasDynamicOffset = false,
                    MinBindingSize = 0
                }
            };
            solidEntries[1] = new BindGroupLayoutEntry
            {
                Binding = 1,
                Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
                Buffer = new BufferBindingLayout
                {
                    Type = BufferBindingType.ReadOnlyStorage,
                    HasDynamicOffset = false,
                    MinBindingSize = 0
                }
            };
            var solidLayoutDesc = new BindGroupLayoutDescriptor
            {
                EntryCount = 2,
                Entries = solidEntries
            };
            _solidBindGroupLayout =
                wgpu.DeviceCreateBindGroupLayout(
                    device,
                    &solidLayoutDesc);

            var textureEntries = stackalloc BindGroupLayoutEntry[3];
            textureEntries[0] = new BindGroupLayoutEntry
            {
                Binding = 0,
                Visibility = ShaderStage.Fragment,
                Sampler = new SamplerBindingLayout
                {
                    Type = SamplerBindingType.Filtering
                }
            };
            textureEntries[1] = new BindGroupLayoutEntry
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
            textureEntries[2] = new BindGroupLayoutEntry
            {
                Binding = 2,
                Visibility = ShaderStage.Fragment,
                Texture = new TextureBindingLayout
                {
                    SampleType = TextureSampleType.Float,
                    ViewDimension = TextureViewDimension.Dimension2D,
                    Multisampled = false
                }
            };
            var textureLayoutDesc = new BindGroupLayoutDescriptor
            {
                EntryCount = 3,
                Entries = textureEntries
            };
            _textureBindGroupLayout =
                wgpu.DeviceCreateBindGroupLayout(
                    device,
                    &textureLayoutDesc);

            var layouts = stackalloc BindGroupLayout*[2];
            layouts[0] = _solidBindGroupLayout;
            layouts[1] = _textureBindGroupLayout;
            var pipelineLayoutDesc = new PipelineLayoutDescriptor
            {
                BindGroupLayoutCount = 2,
                BindGroupLayouts = layouts
            };
            _solidPipelineLayout =
                wgpu.DeviceCreatePipelineLayout(
                    device,
                    &pipelineLayoutDesc);

            _whiteTexture = new GpuTexture(
                compositor.Context,
                1,
                1,
                TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding |
                    TextureUsage.CopyDst,
                "Mesh3D white material texture",
                alphaMode: GpuTextureAlphaMode.Straight);
            _whiteTexture.WritePixels(
                new byte[] { 255, 255, 255, 255 });
        }

        public unsafe void BeginFrame(Compositor compositor)
        {
            _currentCompileIndex = 0;
            _usedLiveMaterialBlurCount = 0;
            _preparedLiveMaterialCount = 0;
            _liveMaterialBlurSubmissionCount = 0;
            if (_pendingCommandBuffers.Count > 0)
            {
                var wgpu = compositor.Context.Api;
                for (int i = 0; i < _pendingCommandBuffers.Count; i++)
                {
                    wgpu.CommandBufferRelease((CommandBuffer*)_pendingCommandBuffers[i]);
                }
                _pendingCommandBuffers.Clear();
            }
            ReleasePendingTextureResources(compositor.Context);
        }

        public unsafe void Dispose()
        {
            foreach (var cache in _geometryCache.Values)
            {
                cache.VertexBuffer.Dispose();
            }
            _geometryCache.Clear();

            if (_context != null)
            {
                foreach (var res in _viewportResources)
                {
                    res.Dispose(_context);
                }
                ReleasePendingTextureResources(_context);
                var wgpu = _context.Api;
                if (_whiteLinearBindGroup != null)
                {
                    wgpu.BindGroupRelease(_whiteLinearBindGroup);
                    _whiteLinearBindGroup = null;
                }
                if (_whiteNearestBindGroup != null)
                {
                    wgpu.BindGroupRelease(_whiteNearestBindGroup);
                    _whiteNearestBindGroup = null;
                }
                if (_solidPipelineLayout != null)
                {
                    wgpu.PipelineLayoutRelease(_solidPipelineLayout);
                    _solidPipelineLayout = null;
                }
                if (_textureBindGroupLayout != null)
                {
                    wgpu.BindGroupLayoutRelease(
                        _textureBindGroupLayout);
                    _textureBindGroupLayout = null;
                }
                if (_solidBindGroupLayout != null)
                {
                    wgpu.BindGroupLayoutRelease(
                        _solidBindGroupLayout);
                    _solidBindGroupLayout = null;
                }
            }
            _whiteTexture?.Dispose();
            _whiteTexture = null;
            for (int index = 0;
                 index < _liveMaterialBlurPool.Count;
                 index++)
            {
                _liveMaterialBlurPool[index].Dispose();
            }
            _liveMaterialBlurPool.Clear();
            _viewportResources.Clear();
        }

        private unsafe void ReleasePendingTextureResources(
            WgpuContext context)
        {
            var wgpu = context.Api;
            for (int index = 0;
                 index < _pendingTextureBindGroups.Count;
                 index++)
            {
                wgpu.BindGroupRelease(
                    (BindGroup*)_pendingTextureBindGroups[index]);
            }
            _pendingTextureBindGroups.Clear();

            for (int index = 0;
                 index < _pendingTextureLeases.Count;
                 index++)
            {
                _pendingTextureLeases[index].Dispose();
            }
            _pendingTextureLeases.Clear();
        }

        private unsafe BindGroup* GetTextureBindGroup(
            Compositor compositor,
            MeshCompilationEntry entry,
            out GpuTexture texture,
            out bool hasSourceTexture,
            out bool hasYuvConversion,
            out bool hasPreparedGaussianBlur)
        {
            IProGpuTextureLease? lease = null;
            IProGpuTextureLease? chromaLease = null;
            bool acquired = false;
            bool requestedYuv =
                entry.YuvConversion.HasValue &&
                entry.TextureSource is
                    IProGpuPlanarTextureLeaseSource;
            if (requestedYuv)
            {
                acquired =
                    ((IProGpuPlanarTextureLeaseSource)
                    entry.TextureSource!)
                    .TryAcquireGpuPlaneTextureLeases(
                        compositor.Context,
                        out lease,
                        out chromaLease);
            }
            else if (entry.TextureSource is
                    IProGpuContextTextureLeaseSource contextSource)
            {
                acquired =
                    contextSource.TryAcquireGpuTextureLease(
                    compositor.Context,
                    out lease);
            }
            else
            {
                acquired =
                    entry.TextureSource
                        ?.TryAcquireGpuTextureLease(
                            out lease) == true;
            }

            if (acquired &&
                lease is not null &&
                !lease.Texture.IsDisposed &&
                lease.Texture.Context.SharesDeviceWith(
                    compositor.Context) &&
                (!requestedYuv ||
                 chromaLease is not null &&
                 !chromaLease.Texture.IsDisposed &&
                 chromaLease.Texture.Context.SharesDeviceWith(
                     compositor.Context)))
            {
                texture = lease.Texture;
                GpuTexture chromaTexture =
                    chromaLease?.Texture ?? texture;
                hasSourceTexture = true;
                hasYuvConversion =
                    requestedYuv &&
                    chromaLease is not null;
                _pendingTextureLeases.Add(lease);
                if (chromaLease is not null)
                {
                    _pendingTextureLeases.Add(chromaLease);
                }
                hasPreparedGaussianBlur = false;
                if (CanUseLiveMaterialBlur(
                        compositor.Context,
                        texture,
                        chromaLease?.Texture,
                        hasYuvConversion
                            ? entry.YuvConversion
                            : null,
                        entry.TextureEffect.BlurSigma))
                {
                    texture = PrepareLiveMaterialBlur(
                        compositor,
                        texture,
                        chromaLease?.Texture,
                        entry.YuvConversion,
                        entry.TextureEffect.BlurSigma);
                    chromaTexture = texture;
                    hasYuvConversion = false;
                    hasPreparedGaussianBlur = true;
                    _preparedLiveMaterialCount++;
                }
                return CreateTextureBindGroup(
                    compositor,
                    texture,
                    chromaTexture,
                    entry.TextureSamplingMode,
                    retainUntilSubmit: true);
            }

            lease?.Dispose();
            chromaLease?.Dispose();
            texture = _whiteTexture ??
                throw new InvalidOperationException(
                    "Mesh3D fallback texture was not initialized.");
            hasSourceTexture = false;
            hasYuvConversion = false;
            hasPreparedGaussianBlur = false;
            ref BindGroup* cached = ref (
                entry.TextureSamplingMode ==
                    TextureSamplingMode.Nearest
                    ? ref _whiteNearestBindGroup
                    : ref _whiteLinearBindGroup);
            if (cached == null)
            {
                cached = CreateTextureBindGroup(
                    compositor,
                    texture,
                    texture,
                    entry.TextureSamplingMode,
                    retainUntilSubmit: false);
            }
            return cached;
        }

        private unsafe GpuTexture PrepareLiveMaterialBlur(
            Compositor compositor,
            GpuTexture source,
            GpuTexture? chroma,
            ImageEffectYuvConversion? yuvConversion,
            float standardDeviation)
        {
            ulong frame = compositor.FrameNumber;
            for (int index = 0;
                 index < _usedLiveMaterialBlurCount;
                 index++)
            {
                LiveMaterialBlurResources prepared =
                    _liveMaterialBlurPool[index];
                if (prepared.MatchesPrepared(
                        source,
                        chroma,
                        yuvConversion,
                        standardDeviation,
                        frame))
                {
                    prepared.LastUsedFrame = frame;
                    return prepared.Output;
                }
            }

            LiveMaterialBlurResources resources =
                AcquireLiveMaterialBlurResources(
                    source,
                    chroma is not null);
            if (chroma is not null &&
                yuvConversion.HasValue)
            {
                ImageEffectYuvConversion conversion =
                    yuvConversion.Value;
                var gpuConversion =
                    new GpuPlanarYuvConversion(
                        conversion.Range,
                        conversion.Red,
                        conversion.Green,
                        conversion.Blue);
                GpuTextureGaussianBlur.BlurPlanar(
                    source,
                    chroma,
                    resources.Intermediate,
                    resources.Output.ViewPtr,
                    resources.Output.Format,
                    standardDeviation,
                    in gpuConversion,
                    GpuTextureColorTransform.Identity);
            }
            else
            {
                GpuTextureGaussianBlur.Blur(
                    source,
                    resources.Intermediate,
                    resources.Output.ViewPtr,
                    resources.Output.Format,
                    standardDeviation,
                    GpuTextureColorTransform.Identity);
            }

            resources.Output.MarkContentsDirty();
            resources.LastUsedFrame = frame;
            resources.PreparedFrame = frame;
            resources.PreparedSourceId = source.Id;
            resources.PreparedSourceGeneration =
                source.Generation;
            resources.PreparedChromaId =
                chroma?.Id ?? 0;
            resources.PreparedChromaGeneration =
                chroma?.Generation ?? 0;
            resources.PreparedYuvConversion =
                yuvConversion;
            resources.PreparedSigmaBits =
                BitConverter.SingleToInt32Bits(
                    standardDeviation);
            _liveMaterialBlurSubmissionCount++;
            return resources.Output;
        }

        private LiveMaterialBlurResources
            AcquireLiveMaterialBlurResources(
                GpuTexture source,
                bool isPlanar)
        {
            for (int index = _usedLiveMaterialBlurCount;
                 index < _liveMaterialBlurPool.Count;
                 index++)
            {
                LiveMaterialBlurResources candidate =
                    _liveMaterialBlurPool[index];
                if (!candidate.MatchesStorage(
                        source,
                        isPlanar))
                {
                    continue;
                }

                if (index != _usedLiveMaterialBlurCount)
                {
                    LiveMaterialBlurResources displaced =
                        _liveMaterialBlurPool[
                            _usedLiveMaterialBlurCount];
                    _liveMaterialBlurPool[
                        _usedLiveMaterialBlurCount] =
                            candidate;
                    _liveMaterialBlurPool[index] =
                        displaced;
                }

                return _liveMaterialBlurPool[
                    _usedLiveMaterialBlurCount++];
            }

            var created =
                new LiveMaterialBlurResources(
                    source,
                    isPlanar,
                    _liveMaterialBlurPool.Count);
            _liveMaterialBlurPool.Add(created);
            int createdIndex =
                _liveMaterialBlurPool.Count - 1;
            if (createdIndex !=
                _usedLiveMaterialBlurCount)
            {
                LiveMaterialBlurResources displaced =
                    _liveMaterialBlurPool[
                        _usedLiveMaterialBlurCount];
                _liveMaterialBlurPool[
                    _usedLiveMaterialBlurCount] =
                        created;
                _liveMaterialBlurPool[createdIndex] =
                    displaced;
            }

            return _liveMaterialBlurPool[
                _usedLiveMaterialBlurCount++];
        }

        private static bool CanUseLiveMaterialBlur(
            WgpuContext compositorContext,
            GpuTexture source,
            GpuTexture? chroma,
            ImageEffectYuvConversion? yuvConversion,
            float standardDeviation)
        {
            if (!float.IsFinite(standardDeviation) ||
                standardDeviation <= 0.01f ||
                standardDeviation >
                    GpuTextureGaussianBlur
                        .MaximumStandardDeviation ||
                source.IsDisposed ||
                !source.Context.SharesDeviceWith(
                    compositorContext) ||
                (source.Usage &
                    TextureUsage.TextureBinding) == 0 ||
                source.Dimension !=
                    GpuTextureDimension.Dimension2D ||
                source.DepthOrArrayLayers != 1 ||
                source.SampleCount != 1)
            {
                return false;
            }

            bool hasChroma = chroma is not null;
            if (hasChroma !=
                yuvConversion.HasValue)
            {
                return false;
            }

            if (!hasChroma)
            {
                return source.Format is
                    TextureFormat.Rgba8Unorm or
                    TextureFormat.Rgba8UnormSrgb or
                    TextureFormat.Bgra8Unorm or
                    TextureFormat.Bgra8UnormSrgb or
                    TextureFormat.Rgba16float;
            }

            GpuTexture chromaTexture = chroma!;
            bool supportedPlaneFormats =
                source.Format ==
                    TextureFormat.R8Unorm &&
                chromaTexture.Format ==
                    TextureFormat.RG8Unorm ||
                source.Context
                        .SupportsTextureFormatsTier1 &&
                    source.Format ==
                        ProGpuTextureFormats.R16Unorm &&
                    chromaTexture.Format ==
                        ProGpuTextureFormats.RG16Unorm;
            return supportedPlaneFormats &&
                !chromaTexture.IsDisposed &&
                chromaTexture.Context.SharesDeviceWith(
                    compositorContext) &&
                ReferenceEquals(
                    source.Context,
                    chromaTexture.Context) &&
                chromaTexture.Width ==
                    (source.Width + 1) / 2 &&
                chromaTexture.Height ==
                    (source.Height + 1) / 2 &&
                (chromaTexture.Usage &
                    TextureUsage.TextureBinding) != 0 &&
                chromaTexture.Dimension ==
                    GpuTextureDimension.Dimension2D &&
                chromaTexture.DepthOrArrayLayers == 1 &&
                chromaTexture.SampleCount == 1;
        }

        private unsafe BindGroup* CreateTextureBindGroup(
            Compositor compositor,
            GpuTexture texture,
            GpuTexture chromaTexture,
            TextureSamplingMode samplingMode,
            bool retainUntilSubmit)
        {
            var entries = stackalloc BindGroupEntry[3];
            entries[0] = new BindGroupEntry
            {
                Binding = 0,
                Sampler = compositor.GetTextureSampler(samplingMode)
            };
            entries[1] = new BindGroupEntry
            {
                Binding = 1,
                TextureView = texture.ViewPtr
            };
            entries[2] = new BindGroupEntry
            {
                Binding = 2,
                TextureView = chromaTexture.ViewPtr
            };
            var descriptor = new BindGroupDescriptor
            {
                Layout = _textureBindGroupLayout,
                EntryCount = 3,
                Entries = entries
            };
            BindGroup* bindGroup =
                compositor.Context.Api.DeviceCreateBindGroup(
                    compositor.Context.Device,
                    &descriptor);
            if (bindGroup == null)
            {
                throw new InvalidOperationException(
                    "Failed to create Mesh3D texture bind group.");
            }
            if (retainUntilSubmit)
            {
                _pendingTextureBindGroups.Add((nint)bindGroup);
            }
            return bindGroup;
        }

        public unsafe void Compile(
            Compositor compositor,
            IRenderDataProvider? provider,
            Matrix4x4 transform,
            ref RenderCommand cmd)
        {
            var payload = cmd.DataParam as Viewport3DCompilationPayload;
            if (payload == null || payload.Meshes.Count == 0 || payload.ColorTexture == null || payload.DepthTexture == null) return;

            _context = compositor.Context;
            var wgpu = compositor.Context.Api;
            var device = compositor.Context.Device;
            var queue = compositor.Context.Queue;
            uint sampleCount = payload.SampleCount is 1 or 4 ? payload.SampleCount : 4u;
            EnsureSolidLayouts(compositor);

            uint uniformsSize = (uint)Marshal.SizeOf<GpuMesh3DUniforms>();

            // Ensure pooled resource exists for current viewport compile index
            while (_viewportResources.Count <= _currentCompileIndex)
            {
                _viewportResources.Add(new ViewportResource(compositor.Context, uniformsSize));
            }
            var res = _viewportResources[_currentCompileIndex];

            // 1. Create or update dynamic record buffer
            int recordCount = payload.Meshes.Count;

            uint reqRecordsSize = (uint)recordCount * (uint)Marshal.SizeOf<GpuMesh3DRecord>();
            if (res.DynamicRecordsBuffer == null || res.DynamicRecordsBuffer.Size < reqRecordsSize)
            {
                res.DynamicRecordsBuffer?.Dispose();
                res.DynamicRecordsBuffer = new GpuBuffer(compositor.Context, reqRecordsSize * 2, BufferUsage.Storage | BufferUsage.CopyDst, "Dynamic Mesh3D Records Buffer");
                res.RecordGen = -1; // Force bind group recreation
            }
            uint reqRecordIndicesSize = (uint)recordCount * sizeof(uint);
            if (res.RecordIndexBuffer == null ||
                res.RecordIndexBuffer.Size < reqRecordIndicesSize)
            {
                res.RecordIndexBuffer?.Dispose();
                res.RecordIndexBuffer = new GpuBuffer(
                    compositor.Context,
                    reqRecordIndicesSize * 2,
                    BufferUsage.Vertex | BufferUsage.CopyDst,
                    "Dynamic Mesh3D Record Indices Buffer");
            }

            // 2. Upload records data
            _compileScratch.EnsureCapacity(recordCount);
            Span<GpuMesh3DRecord> cpuRecords =
                _compileScratch.Records[..recordCount];
            Span<nint> textureBindGroups =
                _compileScratch.TextureBindGroups[..recordCount];
            Span<uint> recordIndices =
                _compileScratch.RecordIndices[..recordCount];
            int n = recordCount;
            for (int i = 0; i < n; i++)
            {
                recordIndices[i] = (uint)i;
                var mesh = payload.Meshes[i];
                textureBindGroups[i] =
                    (nint)GetTextureBindGroup(
                        compositor,
                        mesh,
                        out GpuTexture materialTexture,
                        out bool hasMaterialTexture,
                        out bool hasYuvConversion,
                        out bool hasPreparedGaussianBlur);
                MeshTextureEffect textureEffect =
                    hasMaterialTexture
                        ? hasPreparedGaussianBlur
                            ? mesh.TextureEffect
                                .WithoutGaussianBlur()
                            : mesh.TextureEffect
                        : MeshTextureEffect.Identity;
                ImageEffectColorMatrix? colorMatrix =
                    textureEffect.ColorMatrix;
                ImageEffectYuvConversion? yuvConversion =
                    hasYuvConversion
                        ? mesh.YuvConversion
                        : null;
                float rMode = 0.0f; // Solid
                if (payload.RenderMode == RenderMode3D.Wireframe)
                {
                    rMode = 1.0f;
                }
                else if (payload.RenderMode == RenderMode3D.SolidWireframe)
                {
                    rMode = 2.0f;
                }

                Matrix4x4 normalTransform = Matrix4x4.Identity;
                if (Matrix4x4.Invert(mesh.ModelTransform, out var invModel))
                {
                    normalTransform = Matrix4x4.Transpose(invModel);
                }

                cpuRecords[i] = new GpuMesh3DRecord
                {
                    ModelTransform = mesh.ModelTransform,
                    NormalTransform = normalTransform,
                    Color = mesh.Color,
                    LightDirection = new Vector4(payload.LightDirection, payload.LightIntensity),
                    AmbientColor = new Vector4(payload.AmbientColor, payload.AmbientIntensity),
                    SpecularColor = new Vector4(mesh.SpecularColor, mesh.Shininess),
                    MaterialAmbient = new Vector4(mesh.AmbientColor, 1.0f),
                    Opacity = mesh.Opacity * compositor.ActiveOpacity,
                    RenderMode = rMode,
                    ShadingMode = (float)payload.ShadingMode,
                    TextureEffects0 = new Vector4(
                        textureEffect.Brightness,
                        textureEffect.Contrast,
                        textureEffect.Saturation,
                        textureEffect.Grayscale),
                    TextureEffects1 = new Vector4(
                        textureEffect.Sepia,
                        textureEffect.Invert,
                        textureEffect.BlurSigma,
                        hasMaterialTexture ? 1f : 0f),
                    TextureInfo = new Vector4(
                        materialTexture.Width,
                        materialTexture.Height,
                        materialTexture.AlphaMode ==
                            GpuTextureAlphaMode.Premultiplied
                                ? 1f
                                : 0f,
                        textureEffect.LuminanceToAlpha ? 1f : 0f),
                    ColorMatrixRed =
                        colorMatrix?.Red ?? default,
                    ColorMatrixGreen =
                        colorMatrix?.Green ?? default,
                    ColorMatrixBlue =
                        colorMatrix?.Blue ?? default,
                    ColorMatrixAlpha =
                        colorMatrix?.Alpha ?? default,
                    ColorMatrixOffset =
                        colorMatrix?.Offset ?? default,
                    TextureFlags = new Vector4(
                        colorMatrix.HasValue ? 1f : 0f,
                        yuvConversion.HasValue ? 1f : 0f,
                        mesh.TexturePresentation
                            .ClockwiseQuarterTurns,
                        mesh.TexturePresentation.IsMirrored
                            ? 1f
                            : 0f),
                    YuvRange =
                        yuvConversion?.Range ?? default,
                    YuvRed =
                        yuvConversion?.Red ?? default,
                    YuvGreen =
                        yuvConversion?.Green ?? default,
                    YuvBlue =
                        yuvConversion?.Blue ?? default,
                    TextureSourceRect =
                        mesh.TexturePresentation
                            .NormalizedSourceRect
                };
            }
            res.DynamicRecordsBuffer.Write(cpuRecords);
            res.RecordIndexBuffer.Write(recordIndices);

            Matrix4x4.Invert(cmd.CameraView, out var invView);
            Vector3 cameraPos = invView.Translation;

            // 3. Upload uniforms data
            var cpuUniforms = new GpuMesh3DUniforms
            {
                Projection = cmd.Transform, // Perspective projection matrix
                View = cmd.CameraView,      // View matrix
                CameraPosition = cameraPos
            };
            res.UniformsBuffer.WriteSingle(cpuUniforms);

            // 4. Create the physical-resolution or 4x-MSAA pipeline variant on demand.
            RenderPipeline* cachedPipeline = sampleCount == 1 ? _cachedPipelineSingle : _cachedPipelineMsaa;
            RenderPipeline* cachedBackFacePipeline = sampleCount == 1 ? _cachedBackFacePipelineSingle : _cachedBackFacePipelineMsaa;
            RenderPipeline* cachedWireframePipeline = sampleCount == 1 ? _cachedWireframePipelineSingle : _cachedWireframePipelineMsaa;
            if (cachedPipeline == null)
            {
                cachedPipeline = CreateMeshPipeline(
                    compositor,
                    $"Mesh3DSolidShader_3D_v3_{sampleCount}",
                    Mesh3DSolidShaderCode,
                    "Mesh3D WGSL 3D Solid Shader",
                    $"Mesh3DPipeline_3D_v3_{sampleCount}",
                    CullMode.Back,
                    sampleCount,
                    _solidPipelineLayout);
                if (sampleCount == 1) _cachedPipelineSingle = cachedPipeline;
                else _cachedPipelineMsaa = cachedPipeline;
            }

            if (cachedBackFacePipeline == null)
            {
                cachedBackFacePipeline = CreateMeshPipeline(
                    compositor,
                    $"Mesh3DSolidShader_3D_v3_{sampleCount}",
                    Mesh3DSolidShaderCode,
                    "Mesh3D WGSL 3D Solid Shader",
                    $"Mesh3DBackFacePipeline_3D_v3_{sampleCount}",
                    CullMode.Front,
                    sampleCount,
                    _solidPipelineLayout);
                if (sampleCount == 1) _cachedBackFacePipelineSingle = cachedBackFacePipeline;
                else _cachedBackFacePipelineMsaa = cachedBackFacePipeline;
            }

            // Create wireframe pipeline if needed (TriangleList with double sided rendering)
            if (cachedWireframePipeline == null)
            {
                cachedWireframePipeline = CreateMeshPipeline(
                    compositor,
                    $"Mesh3DWireframeShader_3D_v3_{sampleCount}",
                    Mesh3DWireframeShaderCode,
                    "Mesh3D WGSL 3D Wireframe Shader",
                    $"Mesh3DWireframePipeline_3D_v3_{sampleCount}",
                    CullMode.None,
                    sampleCount);
                if (sampleCount == 1) _cachedWireframePipelineSingle = cachedWireframePipeline;
                else _cachedWireframePipelineMsaa = cachedWireframePipeline;
            }

            // 5. Create or get cached BindGroup
            int currentGen = res.DynamicRecordsBuffer.GetHashCode() ^ res.UniformsBuffer.GetHashCode();
            if (res.SolidBindGroup == null ||
                res.WireframeBindGroup == null ||
                currentGen != res.RecordGen ||
                res.SampleCount != sampleCount)
            {
                res.RecordGen = currentGen;
                res.SampleCount = sampleCount;

                var bgEntries = stackalloc BindGroupEntry[2];
                bgEntries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = res.UniformsBuffer.BufferPtr,
                    Offset = 0,
                    Size = uniformsSize
                };
                bgEntries[1] = new BindGroupEntry
                {
                    Binding = 1,
                    Buffer = res.DynamicRecordsBuffer.BufferPtr,
                    Offset = 0,
                    Size = res.DynamicRecordsBuffer.Size
                };

                // Bind group for Solid Pipeline
                var bgDesc = new BindGroupDescriptor
                {
                    Layout = _solidBindGroupLayout,
                    EntryCount = 2,
                    Entries = bgEntries,
                    Label = (byte*)SilkMarshal.StringToPtr("Mesh3D 3D BindGroup")
                };

                if (res.SolidBindGroup != null) wgpu.BindGroupRelease(res.SolidBindGroup);
                res.SolidBindGroup = wgpu.DeviceCreateBindGroup(device, &bgDesc);
                SilkMarshal.Free((nint)bgDesc.Label);

                // Bind group for Wireframe Pipeline
                var wireframeLayout = wgpu.RenderPipelineGetBindGroupLayout(cachedWireframePipeline, 0);
                var wireframeBgDesc = new BindGroupDescriptor
                {
                    Layout = wireframeLayout,
                    EntryCount = 2,
                    Entries = bgEntries,
                    Label = (byte*)SilkMarshal.StringToPtr("Mesh3D Wireframe BindGroup")
                };

                if (res.WireframeBindGroup != null) wgpu.BindGroupRelease(res.WireframeBindGroup);
                res.WireframeBindGroup = wgpu.DeviceCreateBindGroup(device, &wireframeBgDesc);
                SilkMarshal.Free((nint)wireframeBgDesc.Label);
                wgpu.BindGroupLayoutRelease(wireframeLayout);
            }

            // 6. Begin offscreen WebGPU Render Pass targeting the custom color and depth textures!
            var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Mesh3D Offscreen Encoder") };
            var encoder = wgpu.DeviceCreateCommandEncoder(device, &encoderDesc);
            SilkMarshal.Free((nint)encoderDesc.Label);

            var colorAttachment = new RenderPassColorAttachment
            {
                View = payload.MsaaColorTexture != null ? payload.MsaaColorTexture.ViewPtr : payload.ColorTexture.ViewPtr,
                ResolveTarget = payload.MsaaColorTexture != null ? payload.ColorTexture.ViewPtr : null,
                LoadOp = LoadOp.Clear,
                StoreOp = payload.MsaaColorTexture != null ? StoreOp.Discard : StoreOp.Store,
                ClearValue = new Silk.NET.WebGPU.Color { R = 0.05f, G = 0.05f, B = 0.06f, A = 1.0f } // Slate premium dark background
            };

            var depthAttachment = new RenderPassDepthStencilAttachment
            {
                View = payload.DepthTexture.ViewPtr,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.Store,
                DepthClearValue = 1.0f,
                DepthReadOnly = false,
                StencilLoadOp = LoadOp.Clear,
                StencilStoreOp = StoreOp.Store,
                StencilClearValue = 0,
                StencilReadOnly = false
            };

            var passDesc = new RenderPassDescriptor
            {
                ColorAttachmentCount = 1,
                ColorAttachments = &colorAttachment,
                DepthStencilAttachment = &depthAttachment
            };

            var pass = wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);

            // 7. Compile mesh buffers on demand
            for (int i = 0; i < payload.Meshes.Count; i++)
            {
                var entry = payload.Meshes[i];
                if (entry.Geometry == null) continue;

                bool needsRebuild = false;
                if (_geometryCache.TryGetValue(entry.Geometry, out var cache))
                {
                    if (cache.Version != entry.GeometryVersion)
                    {
                        cache.VertexBuffer.Dispose();
                        needsRebuild = true;
                    }
                }
                else
                {
                    needsRebuild = true;
                }

                if (needsRebuild)
                {
                    // Create De-indexed (non-indexed) Vertex Buffer
                    var cpuVertices = new GpuVertex3D[entry.Indices.Length];
                    for (int idx = 0; idx < entry.Indices.Length; idx++)
                    {
                        int vIdx = entry.Indices[idx];
                        var pos = (vIdx >= 0 && vIdx < entry.Positions.Length) ? entry.Positions[vIdx] : Vector3.Zero;
                        var norm = (vIdx >= 0 && vIdx < entry.Normals.Length) ? entry.Normals[vIdx] : Vector3.UnitY;
                        var uv =
                            (vIdx >= 0 &&
                             vIdx <
                                entry.TextureCoordinates.Length)
                                ? entry.TextureCoordinates[vIdx]
                                : Vector2.Zero;
                        cpuVertices[idx] =
                            new GpuVertex3D(pos, norm, uv);
                    }

                    uint vSize = (uint)cpuVertices.Length * (uint)Marshal.SizeOf<GpuVertex3D>();
                    var vBuffer = new GpuBuffer(compositor.Context, vSize, BufferUsage.Vertex | BufferUsage.CopyDst, "3D Mesh Vertex Buffer");
                    vBuffer.Write(cpuVertices);

                    cache = new CachedGeometry
                    {
                        VertexBuffer = vBuffer,
                        VertexCount = (uint)entry.Indices.Length,
                        Version = entry.GeometryVersion
                    };
                    _geometryCache[entry.Geometry] = cache;
                }
            }

            // Draw Passes
            var mode = payload.RenderMode;

            if (mode == RenderMode3D.Solid)
            {
                wgpu.RenderPassEncoderSetPipeline(pass, cachedPipeline);
                wgpu.RenderPassEncoderSetBindGroup(pass, 0, res.SolidBindGroup, 0, null);
                for (int i = 0; i < payload.Meshes.Count; i++)
                {
                    var entry = payload.Meshes[i];
                    if (entry.Geometry == null || entry.IsBackFace) continue;

                    var cache = _geometryCache[entry.Geometry];

                    wgpu.RenderPassEncoderSetBindGroup(
                        pass,
                        1,
                        (BindGroup*)textureBindGroups[i],
                        0,
                        null);
                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, cache.VertexBuffer.BufferPtr, 0, cache.VertexBuffer.Size);
                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 1, res.RecordIndexBuffer.BufferPtr, (ulong)i * sizeof(uint), sizeof(uint));
                    wgpu.RenderPassEncoderDraw(pass, cache.VertexCount, 1, 0, 0);
                }

                wgpu.RenderPassEncoderSetPipeline(pass, cachedBackFacePipeline);
                wgpu.RenderPassEncoderSetBindGroup(pass, 0, res.SolidBindGroup, 0, null);
                for (int i = 0; i < payload.Meshes.Count; i++)
                {
                    var entry = payload.Meshes[i];
                    if (entry.Geometry == null || !entry.IsBackFace) continue;

                    var cache = _geometryCache[entry.Geometry];

                    wgpu.RenderPassEncoderSetBindGroup(
                        pass,
                        1,
                        (BindGroup*)textureBindGroups[i],
                        0,
                        null);
                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, cache.VertexBuffer.BufferPtr, 0, cache.VertexBuffer.Size);
                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 1, res.RecordIndexBuffer.BufferPtr, (ulong)i * sizeof(uint), sizeof(uint));
                    wgpu.RenderPassEncoderDraw(pass, cache.VertexCount, 1, 0, 0);
                }
            }
            else if (mode == RenderMode3D.Wireframe || mode == RenderMode3D.SolidWireframe)
            {
                wgpu.RenderPassEncoderSetPipeline(pass, cachedWireframePipeline);
                wgpu.RenderPassEncoderSetBindGroup(pass, 0, res.WireframeBindGroup, 0, null);
                for (int i = 0; i < payload.Meshes.Count; i++)
                {
                    var entry = payload.Meshes[i];
                    if (entry.Geometry == null) continue;

                    var cache = _geometryCache[entry.Geometry];

                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, cache.VertexBuffer.BufferPtr, 0, cache.VertexBuffer.Size);
                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 1, res.RecordIndexBuffer.BufferPtr, (ulong)i * sizeof(uint), sizeof(uint));
                    wgpu.RenderPassEncoderDraw(pass, cache.VertexCount, 1, 0, 0);
                }
            }

            wgpu.RenderPassEncoderEnd(pass);
            wgpu.RenderPassEncoderRelease(pass);

            // 8. Add offscreen command buffer to the deferred submission queue
            var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Mesh3D Offscreen Command Buffer") };
            var cmdBuffer = wgpu.CommandEncoderFinish(encoder, &cmdDesc);
            SilkMarshal.Free((nint)cmdDesc.Label);

            _pendingCommandBuffers.Add((nint)cmdBuffer);

            wgpu.CommandEncoderRelease(encoder);

            _currentCompileIndex++;

            // DrawExtension is now a no-op in the main compositor pass since the offscreen pass is fully complete and
            // the Viewport3D control appends a separate DrawTexture command!
            cmd.PointBufferOffset = 0;
            cmd.PointBufferCount = 0;
        }

        public unsafe void EndFrame(Compositor compositor)
        {
            if (_pendingCommandBuffers.Count > 0)
            {
                var wgpu = compositor.Context.Api;
                var queue = compositor.Context.Queue;

                int count = _pendingCommandBuffers.Count;
                var buffers = stackalloc CommandBuffer*[count];
                for (int i = 0; i < count; i++)
                {
                    buffers[i] = (CommandBuffer*)_pendingCommandBuffers[i];
                }

                wgpu.QueueSubmit(queue, (uint)count, buffers);

                for (int i = 0; i < count; i++)
                {
                    wgpu.CommandBufferRelease((CommandBuffer*)_pendingCommandBuffers[i]);
                }
                _pendingCommandBuffers.Clear();
                ReleasePendingTextureResources(
                    compositor.Context);
            }
            else
            {
                ReleasePendingTextureResources(
                    compositor.Context);
            }

            ulong frame = compositor.FrameNumber;
            for (int index =
                    _liveMaterialBlurPool.Count - 1;
                 index >= _usedLiveMaterialBlurCount;
                 index--)
            {
                LiveMaterialBlurResources resources =
                    _liveMaterialBlurPool[index];
                if (frame - resources.LastUsedFrame <= 240)
                {
                    continue;
                }

                resources.Dispose();
                _liveMaterialBlurPool.RemoveAt(index);
            }
        }

        public unsafe void Render(
            Compositor compositor,
            void* renderPassEncoder,
            bool isOffscreen,
            in Compositor.CompositorDrawCall dc)
        {
            // Fully no-op
        }
    }

    public class Viewport3DCompilationPayload
    {
        public Vector2 ViewportSize { get; set; } = new Vector2(400f, 300f);
        public Vector3 LightDirection { get; set; } = new Vector3(0.5f, 1f, -0.5f);
        public float LightIntensity { get; set; } = 1.0f;
        public Vector3 AmbientColor { get; set; } = new Vector3(1f, 1f, 1f);
        public float AmbientIntensity { get; set; } = 0.2f;
        public List<MeshCompilationEntry> Meshes { get; } = new();

        public GpuTexture? ColorTexture { get; set; }
        public GpuTexture? MsaaColorTexture { get; set; }
        public GpuTexture? DepthTexture { get; set; }
        public uint SampleCount { get; set; } = 4;
        
        public RenderMode3D RenderMode { get; set; } = RenderMode3D.Solid;
        public ShadingMode3D ShadingMode { get; set; } = ShadingMode3D.Realistic;
    }

    public class MeshCompilationEntry
    {
        public object? Geometry { get; set; }
        public int GeometryVersion { get; set; }
        public Vector3[] Positions { get; set; } = Array.Empty<Vector3>();
        public Vector3[] Normals { get; set; } = Array.Empty<Vector3>();
        public int[] Indices { get; set; } = Array.Empty<int>();
        public Vector2[] TextureCoordinates { get; set; } =
            Array.Empty<Vector2>();
        public IProGpuTextureLeaseSource? TextureSource { get; set; }
        public MeshTextureEffect TextureEffect { get; set; } =
            MeshTextureEffect.Identity;
        public TextureSamplingMode TextureSamplingMode { get; set; } =
            TextureSamplingMode.Linear;
        public ImageEffectYuvConversion? YuvConversion { get; set; }
        public MeshTexturePresentation TexturePresentation { get; set; } =
            MeshTexturePresentation.Identity;
        public Matrix4x4 ModelTransform { get; set; } = Matrix4x4.Identity;
        public Vector4 Color { get; set; } = Vector4.One;
        public Vector3 SpecularColor { get; set; } = new Vector3(0.2f, 0.2f, 0.2f);
        public float Shininess { get; set; } = 32.0f;
        public Vector3 AmbientColor { get; set; } = new Vector3(0.2f, 0.2f, 0.2f);
        public float Opacity { get; set; } = 1.0f;
        public bool IsBackFace { get; set; } = false;
    }

    /// <summary>
    /// Normalized texture presentation state evaluated in the mesh fragment
    /// pass. Rotation is clockwise in quarter turns and mirroring is applied
    /// horizontally after rotation, matching media playback presentation.
    /// </summary>
    public readonly struct MeshTexturePresentation
    {
        public MeshTexturePresentation()
            : this(
                new Vector4(0f, 0f, 1f, 1f),
                clockwiseQuarterTurns: 0,
                isMirrored: false)
        {
        }

        public MeshTexturePresentation(
            Vector4 normalizedSourceRect,
            int clockwiseQuarterTurns = 0,
            bool isMirrored = false)
        {
            if (!float.IsFinite(normalizedSourceRect.X) ||
                !float.IsFinite(normalizedSourceRect.Y) ||
                !float.IsFinite(normalizedSourceRect.Z) ||
                !float.IsFinite(normalizedSourceRect.W))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(normalizedSourceRect));
            }

            float x = Math.Clamp(
                normalizedSourceRect.X,
                0f,
                1f);
            float y = Math.Clamp(
                normalizedSourceRect.Y,
                0f,
                1f);
            float width = Math.Clamp(
                normalizedSourceRect.Z,
                0f,
                1f - x);
            float height = Math.Clamp(
                normalizedSourceRect.W,
                0f,
                1f - y);
            NormalizedSourceRect =
                width > 0f && height > 0f
                    ? new Vector4(x, y, width, height)
                    : new Vector4(0f, 0f, 1f, 1f);
            ClockwiseQuarterTurns =
                ((clockwiseQuarterTurns % 4) + 4) % 4;
            IsMirrored = isMirrored;
        }

        public static MeshTexturePresentation Identity => new();

        public Vector4 NormalizedSourceRect { get; }
        public int ClockwiseQuarterTurns { get; }
        public bool IsMirrored { get; }
    }

    /// <summary>
    /// Immutable shader parameters for texture-backed 3D materials. The source
    /// texture itself is leased separately so decoder-owned frames can be
    /// sampled without a staging texture or CPU readback.
    /// </summary>
    public readonly struct MeshTextureEffect
    {
        public MeshTextureEffect()
            : this(
                brightness: 0f,
                contrast: 1f,
                saturation: 1f)
        {
        }

        public MeshTextureEffect(
            float brightness = 0f,
            float contrast = 1f,
            float saturation = 1f,
            float grayscale = 0f,
            float sepia = 0f,
            float invert = 0f,
            float blurSigma = 0f,
            ImageEffectColorMatrix? colorMatrix = null,
            bool luminanceToAlpha = false)
        {
            Brightness = brightness;
            Contrast = contrast;
            Saturation = saturation;
            Grayscale = grayscale;
            Sepia = sepia;
            Invert = invert;
            BlurSigma = blurSigma;
            ColorMatrix = colorMatrix;
            LuminanceToAlpha = luminanceToAlpha;
        }

        public static MeshTextureEffect Identity => new();

        public float Brightness { get; }
        public float Contrast { get; }
        public float Saturation { get; }
        public float Grayscale { get; }
        public float Sepia { get; }
        public float Invert { get; }
        public float BlurSigma { get; }
        public ImageEffectColorMatrix? ColorMatrix { get; }
        public bool LuminanceToAlpha { get; }

        internal MeshTextureEffect WithoutGaussianBlur() =>
            new(
                Brightness,
                Contrast,
                Saturation,
                Grayscale,
                Sepia,
                Invert,
                blurSigma: 0f,
                colorMatrix: ColorMatrix,
                luminanceToAlpha: LuminanceToAlpha);
    }
}
