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

    [Flags]
    public enum Mesh3DEdgeDisplay : uint
    {
        None = 0,
        Boundary = 1U << 0,
        Crease = 1U << 1,
        Silhouette = 1U << 2,
        Occluded = 1U << 3,
    }

    public readonly record struct Mesh3DEdgeStyle(
        Mesh3DEdgeDisplay Display,
        Vector4 VisibleColor,
        Vector4 OccludedColor,
        float Width,
        float CreaseAngleDegrees,
        float OccludedDashLength,
        float OccludedGapLength)
    {
        /// <summary>
        /// Physical pixels added beyond each projected endpoint. The modifier
        /// is suppressed when the projected edge is shorter than twice this
        /// value.
        /// </summary>
        public float ExtensionLength { get; init; }

        /// <summary>
        /// Maximum physical-pixel displacement of each of two deterministic
        /// auxiliary sketch strokes. Zero retains the ordinary single stroke.
        /// </summary>
        public float JitterAmount { get; init; }

        public static Mesh3DEdgeStyle Disabled { get; } = new(
            Mesh3DEdgeDisplay.None,
            new Vector4(0.85f, 0.85f, 0.9f, 1.0f),
            new Vector4(0.45f, 0.45f, 0.5f, 0.7f),
            1.0f,
            30.0f,
            6.0f,
            4.0f);

        public Mesh3DEdgeStyle Validate()
        {
            const Mesh3DEdgeDisplay known =
                Mesh3DEdgeDisplay.Boundary |
                Mesh3DEdgeDisplay.Crease |
                Mesh3DEdgeDisplay.Silhouette |
                Mesh3DEdgeDisplay.Occluded;
            if ((Display & ~known) != 0 ||
                !IsFinite(VisibleColor) ||
                !IsFinite(OccludedColor) ||
                !IsNormalized(VisibleColor) ||
                !IsNormalized(OccludedColor) ||
                !float.IsFinite(Width) || Width <= 0.0f || Width > 64.0f ||
                !float.IsFinite(CreaseAngleDegrees) ||
                CreaseAngleDegrees < 0.0f || CreaseAngleDegrees > 180.0f ||
                !float.IsFinite(OccludedDashLength) ||
                OccludedDashLength <= 0.0f ||
                !float.IsFinite(OccludedGapLength) ||
                OccludedGapLength < 0.0f ||
                !float.IsFinite(ExtensionLength) ||
                ExtensionLength < 0.0f || ExtensionLength > 64.0f ||
                !float.IsFinite(JitterAmount) ||
                JitterAmount < 0.0f || JitterAmount > 16.0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Mesh3DEdgeStyle),
                    "Mesh edge style values must be finite and within the documented bounds.");
            }
            return this;
        }

        private static bool IsFinite(Vector4 value) =>
            float.IsFinite(value.X) &&
            float.IsFinite(value.Y) &&
            float.IsFinite(value.Z) &&
            float.IsFinite(value.W);

        private static bool IsNormalized(Vector4 value) =>
            value.X is >= 0.0f and <= 1.0f &&
            value.Y is >= 0.0f and <= 1.0f &&
            value.Z is >= 0.0f and <= 1.0f &&
            value.W is >= 0.0f and <= 1.0f;
    }

    public enum MeshEdgeTopology3D : byte
    {
        Manifold = 0,
        Boundary = 1,
        NonManifold = 2,
    }

    public readonly record struct MeshEdge3D(
        Vector3 Start,
        Vector3 End,
        Vector3 FirstFaceNormal,
        Vector3 SecondFaceNormal,
        MeshEdgeTopology3D Topology);

    /// <summary>
    /// Actual managed Mesh3D work observed for the most recently completed
    /// compositor frame. Upload byte counts describe queue buffer writes, and
    /// <see cref="QueueSubmissionCount"/> is the shared extension-frame total.
    /// </summary>
    public readonly record struct Mesh3DFrameMetrics(
        ulong FrameNumber,
        ulong SceneGeneration,
        ulong RecordGeneration,
        bool SceneReused,
        int ViewportCount,
        int MeshCount,
        int DrawCallCount,
        int SceneCompilationCount,
        int ModelVisualVisitCount,
        int GeometryCacheHitCount,
        int GeometryCacheMissCount,
        ulong GeometryVertexUploadBytes,
        ulong RecordUploadBytes,
        ulong RecordIndexUploadBytes,
        ulong EdgeUploadBytes,
        ulong UniformUploadBytes,
        int GeometryResidentCount,
        ulong GeometryBufferResidentBytes,
        int ViewportResourceCount,
        ulong ViewportBufferResidentBytes,
        ulong LogicalTargetTextureBytes,
        int CommandBufferCount,
        int QueueSubmissionCount);

    /// <summary>
    /// Stable target used by a viewport to observe extension-frame metrics
    /// without allocating a per-frame callback or delegate.
    /// </summary>
    public sealed class Mesh3DFrameMetricsTarget
    {
        public Mesh3DFrameMetrics LastFrameMetrics { get; internal set; }
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
        public Vector4 MaterialAmbient;       // rgb = Material Ka, w = self illumination
        public float Opacity;
        public float RenderMode;              // 0.0f = Solid, 1.0f = Wireframe, 2.0f = SolidWireframe
        public float ShadingMode;             // AutoCAD Shading Mode (0=Realistic, 1=Conceptual, 2=Flat, 3=HiddenLine, 4=ShadesOfGray, 5=XRay, 6=Normals)
        public float TextureSamplingMode;      // bit 0 = linear; floor(value / 2) = tiling mode
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
        public Vector4 VisibleEdgeColor;
        public Vector4 OccludedEdgeColor;
        public Vector4 EdgeOptions0; // width, crease cosine, dash, gap
        public Vector4 EdgeOptions1; // display flags, viewport width/height, extension
        public Vector4 EdgeOptions2; // jitter, reserved
    }

    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct GpuMesh3DEdge
    {
        public Vector4 Start;
        public Vector4 End;
        public Vector4 FirstFaceNormal;
        public Vector4 SecondFaceNormal;
        public uint RecordIndex;
        public uint Topology;
        private uint _reserved0;
        private uint _reserved1;
    }

    internal sealed class Mesh3DCompileScratch
    {
        private GpuMesh3DRecord[] _records =
            Array.Empty<GpuMesh3DRecord>();
        private nint[] _textureBindGroups =
            Array.Empty<nint>();
        private uint[] _recordIndices =
            Array.Empty<uint>();
        private byte[] _unfilterableMaterials =
            Array.Empty<byte>();
        private GpuMesh3DEdge[] _edges =
            Array.Empty<GpuMesh3DEdge>();

        internal int Capacity => _records.Length;

        internal Span<GpuMesh3DRecord> Records =>
            _records;

        internal Span<nint> TextureBindGroups =>
            _textureBindGroups;

        internal Span<uint> RecordIndices =>
            _recordIndices;

        internal Span<byte> UnfilterableMaterials =>
            _unfilterableMaterials;

        internal Span<GpuMesh3DEdge> Edges => _edges;

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
            var unfilterableMaterials =
                new byte[capacity];
            _records.AsSpan().CopyTo(records);
            _textureBindGroups.AsSpan().CopyTo(
                textureBindGroups);
            _recordIndices.AsSpan().CopyTo(
                recordIndices);
            _unfilterableMaterials.AsSpan().CopyTo(
                unfilterableMaterials);
            _records = records;
            _textureBindGroups = textureBindGroups;
            _recordIndices = recordIndices;
            _unfilterableMaterials =
                unfilterableMaterials;
        }

        internal void EnsureEdgeCapacity(int requiredCapacity)
        {
            if (requiredCapacity < 0 || requiredCapacity > Array.MaxLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requiredCapacity));
            }
            if (requiredCapacity <= _edges.Length)
            {
                return;
            }

            int capacity = Math.Max(4, _edges.Length);
            while (capacity < requiredCapacity)
            {
                int growth = Math.Max(4, capacity);
                capacity = capacity > Array.MaxLength - growth
                    ? Array.MaxLength
                    : capacity + growth;
            }
            Array.Resize(ref _edges, capacity);
        }
    }

    public class Mesh3DExtensionPipeline : ICompositorExtension
    {


        private static readonly string Mesh3DSolidShaderCode = ShaderResource.Load(typeof(Mesh3DExtensionPipeline), "Mesh3DSolid.wgsl");
 
        private static readonly string Mesh3DWireframeShaderCode = ShaderResource.Load(typeof(Mesh3DExtensionPipeline), "Mesh3DWireframe.wgsl");

        private static readonly string Mesh3DEdgeShaderCode = ShaderResource.Load(typeof(Mesh3DExtensionPipeline), "Mesh3DEdges.wgsl");

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
            public GpuBuffer? EdgeBuffer;
            public uint EdgeCount;
            public unsafe BindGroup* SolidBindGroup;
            public unsafe BindGroup* WireframeBindGroup;
            public int RecordGen = -1;
            public uint SampleCount;
            public ulong UploadedRecordGeneration;
            public int UploadedRecordCount;
            public int UploadedOpacityBits;
            public ulong UploadedEdgeSceneGeneration;
            public int UploadedEdgeCount;

            public ViewportResource(WgpuContext context, uint uniformsSize)
            {
                UniformsBuffer = new GpuBuffer(context, uniformsSize, BufferUsage.Uniform | BufferUsage.CopyDst, "Mesh3D Uniforms Buffer");
            }
            
            public unsafe void Dispose(WgpuContext context)
            {
                UniformsBuffer.Dispose();
                DynamicRecordsBuffer?.Dispose();
                RecordIndexBuffer?.Dispose();
                EdgeBuffer?.Dispose();
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
        private readonly List<Mesh3DFrameMetricsTarget>
            _pendingMetricsTargets = new();
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
        private ulong _frameSceneGeneration;
        private ulong _frameRecordGeneration;
        private bool _frameSceneReused;
        private int _frameViewportCount;
        private int _frameMeshCount;
        private int _frameDrawCallCount;
        private int _frameSceneCompilationCount;
        private int _frameModelVisualVisitCount;
        private int _frameGeometryCacheHitCount;
        private int _frameGeometryCacheMissCount;
        private ulong _frameGeometryVertexUploadBytes;
        private ulong _frameRecordUploadBytes;
        private ulong _frameRecordIndexUploadBytes;
        private ulong _frameEdgeUploadBytes;
        private ulong _frameUniformUploadBytes;
        private ulong _frameLogicalTargetTextureBytes;
        private int _frameCommandBufferCount;
        private ulong _geometryBufferResidentBytes;
        private ulong _viewportBufferResidentBytes;
        private unsafe BindGroupLayout* _solidBindGroupLayout;
        private unsafe BindGroupLayout* _textureBindGroupLayout;
        private unsafe BindGroupLayout*
            _unfilterableTextureBindGroupLayout;
        private unsafe PipelineLayout* _solidPipelineLayout;
        private unsafe PipelineLayout* _edgePipelineLayout;
        private unsafe PipelineLayout*
            _unfilterableSolidPipelineLayout;
        private GpuTexture? _whiteTexture;
        private unsafe BindGroup* _whiteLinearBindGroup;
        private unsafe BindGroup* _whiteNearestBindGroup;
        
        private unsafe RenderPipeline* _cachedPipelineSingle;
        private unsafe RenderPipeline* _cachedBackFacePipelineSingle;
        private unsafe RenderPipeline* _cachedWireframePipelineSingle;
        private unsafe RenderPipeline* _cachedPipelineMsaa;
        private unsafe RenderPipeline* _cachedBackFacePipelineMsaa;
        private unsafe RenderPipeline* _cachedWireframePipelineMsaa;
        private unsafe RenderPipeline* _cachedVisibleEdgePipelineSingle;
        private unsafe RenderPipeline* _cachedVisibleEdgePipelineMsaa;
        private unsafe RenderPipeline* _cachedOccludedEdgePipelineSingle;
        private unsafe RenderPipeline* _cachedOccludedEdgePipelineMsaa;
        private unsafe RenderPipeline*
            _cachedUnfilterablePipelineSingle;
        private unsafe RenderPipeline*
            _cachedUnfilterableBackFacePipelineSingle;
        private unsafe RenderPipeline*
            _cachedUnfilterablePipelineMsaa;
        private unsafe RenderPipeline*
            _cachedUnfilterableBackFacePipelineMsaa;

        internal int LiveMaterialBlurResourceCount =>
            _liveMaterialBlurPool.Count;
        internal int PreparedLiveMaterialCount =>
            _preparedLiveMaterialCount;
        internal int LiveMaterialBlurSubmissionCount =>
            _liveMaterialBlurSubmissionCount;

        /// <summary>
        /// Gets actual managed Mesh3D work for the most recently completed
        /// compositor frame.
        /// </summary>
        public Mesh3DFrameMetrics LastFrameMetrics { get; private set; }

        private unsafe RenderPipeline* CreateMeshPipeline(
            Compositor compositor,
            string shaderKey,
            string shaderCode,
            string shaderLabel,
            string pipelineKey,
            CullMode cullMode,
            uint sampleCount,
            PipelineLayout* pipelineLayout = null,
            string fragmentEntry = "fs_main")
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
                    fragmentEntry: fragmentEntry,
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

        private unsafe RenderPipeline* CreateEdgePipeline(
            Compositor compositor,
            uint sampleCount,
            bool occluded)
        {
            ShaderModule* shaderModule =
                compositor.PipelineCache.GetOrCreateShader(
                    $"Mesh3DEdgeShader_3D_v1_{sampleCount}",
                    Mesh3DEdgeShaderCode,
                    "Mesh3D retained edge WGSL shader");
            Span<VertexAttribute> attributes =
                stackalloc VertexAttribute[6];
            attributes[0] = new VertexAttribute
            {
                Format = VertexFormat.Float32x4,
                Offset = 0,
                ShaderLocation = 0
            };
            attributes[1] = new VertexAttribute
            {
                Format = VertexFormat.Float32x4,
                Offset = 16,
                ShaderLocation = 1
            };
            attributes[2] = new VertexAttribute
            {
                Format = VertexFormat.Float32x4,
                Offset = 32,
                ShaderLocation = 2
            };
            attributes[3] = new VertexAttribute
            {
                Format = VertexFormat.Float32x4,
                Offset = 48,
                ShaderLocation = 3
            };
            attributes[4] = new VertexAttribute
            {
                Format = VertexFormat.Uint32,
                Offset = 64,
                ShaderLocation = 4
            };
            attributes[5] = new VertexAttribute
            {
                Format = VertexFormat.Uint32,
                Offset = 68,
                ShaderLocation = 5
            };

            fixed (VertexAttribute* attributesPointer = attributes)
            {
                Span<VertexBufferLayout> layouts =
                    stackalloc VertexBufferLayout[1];
                layouts[0] = new VertexBufferLayout
                {
                    ArrayStride =
                        (uint)Unsafe.SizeOf<GpuMesh3DEdge>(),
                    StepMode = VertexStepMode.Instance,
                    AttributeCount = 6,
                    Attributes = attributesPointer
                };
                return compositor.PipelineCache
                    .GetOrCreateRenderPipeline(
                        $"Mesh3DEdgePipeline_3D_v1_{sampleCount}_{(occluded ? "occluded" : "visible")}",
                        shaderModule,
                        layouts,
                        fragmentEntry: occluded
                            ? "fs_occluded"
                            : "fs_visible",
                        targetFormat: TextureFormat.Rgba8Unorm,
                        topology: PrimitiveTopology.TriangleList,
                        enableDepthStencil: true,
                        depthFormat:
                            TextureFormat.Depth24PlusStencil8,
                        sampleCount: sampleCount,
                        depthWriteEnabled: false,
                        depthCompare: occluded
                            ? CompareFunction.Greater
                            : CompareFunction.LessEqual,
                        cullMode: CullMode.None,
                        pipelineLayout: _edgePipelineLayout);
            }
        }

        private static void ValidateEdge(MeshEdge3D edge)
        {
            if (!IsFinite(edge.Start) ||
                !IsFinite(edge.End) ||
                !IsFinite(edge.FirstFaceNormal) ||
                !IsFinite(edge.SecondFaceNormal) ||
                edge.Topology is < MeshEdgeTopology3D.Manifold or
                    > MeshEdgeTopology3D.NonManifold)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(edge),
                    "Mesh edge geometry must be finite and use a known topology.");
            }
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) &&
            float.IsFinite(value.Y) &&
            float.IsFinite(value.Z);

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

            var unfilterableTextureEntries =
                stackalloc BindGroupLayoutEntry[2];
            unfilterableTextureEntries[0] =
                new BindGroupLayoutEntry
                {
                    Binding = 3,
                    Visibility = ShaderStage.Fragment,
                    Texture = new TextureBindingLayout
                    {
                        SampleType =
                            TextureSampleType
                                .UnfilterableFloat,
                        ViewDimension =
                            TextureViewDimension.Dimension2D,
                        Multisampled = false
                    }
                };
            unfilterableTextureEntries[1] =
                new BindGroupLayoutEntry
                {
                    Binding = 4,
                    Visibility = ShaderStage.Fragment,
                    Texture = new TextureBindingLayout
                    {
                        SampleType =
                            TextureSampleType
                                .UnfilterableFloat,
                        ViewDimension =
                            TextureViewDimension.Dimension2D,
                        Multisampled = false
                    }
                };
            var unfilterableTextureLayoutDesc =
                new BindGroupLayoutDescriptor
                {
                    EntryCount = 2,
                    Entries =
                        unfilterableTextureEntries
                };
            _unfilterableTextureBindGroupLayout =
                wgpu.DeviceCreateBindGroupLayout(
                    device,
                    &unfilterableTextureLayoutDesc);

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

            var edgeLayouts = stackalloc BindGroupLayout*[1];
            edgeLayouts[0] = _solidBindGroupLayout;
            var edgePipelineLayoutDesc =
                new PipelineLayoutDescriptor
                {
                    BindGroupLayoutCount = 1,
                    BindGroupLayouts = edgeLayouts
                };
            _edgePipelineLayout =
                wgpu.DeviceCreatePipelineLayout(
                    device,
                    &edgePipelineLayoutDesc);

            layouts[1] =
                _unfilterableTextureBindGroupLayout;
            _unfilterableSolidPipelineLayout =
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
            _frameSceneGeneration = 0;
            _frameRecordGeneration = 0;
            _frameSceneReused = true;
            _frameViewportCount = 0;
            _frameMeshCount = 0;
            _frameDrawCallCount = 0;
            _frameSceneCompilationCount = 0;
            _frameModelVisualVisitCount = 0;
            _frameGeometryCacheHitCount = 0;
            _frameGeometryCacheMissCount = 0;
            _frameGeometryVertexUploadBytes = 0;
            _frameRecordUploadBytes = 0;
            _frameRecordIndexUploadBytes = 0;
            _frameEdgeUploadBytes = 0;
            _frameUniformUploadBytes = 0;
            _frameLogicalTargetTextureBytes = 0;
            _frameCommandBufferCount = 0;
            _pendingMetricsTargets.Clear();
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
            _geometryBufferResidentBytes = 0;

            if (_context != null)
            {
                foreach (var res in _viewportResources)
                {
                    res.Dispose(_context);
                }
                _viewportBufferResidentBytes = 0;
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
                if (_edgePipelineLayout != null)
                {
                    wgpu.PipelineLayoutRelease(_edgePipelineLayout);
                    _edgePipelineLayout = null;
                }
                if (_unfilterableSolidPipelineLayout != null)
                {
                    wgpu.PipelineLayoutRelease(
                        _unfilterableSolidPipelineLayout);
                    _unfilterableSolidPipelineLayout = null;
                }
                if (_textureBindGroupLayout != null)
                {
                    wgpu.BindGroupLayoutRelease(
                        _textureBindGroupLayout);
                    _textureBindGroupLayout = null;
                }
                if (_unfilterableTextureBindGroupLayout != null)
                {
                    wgpu.BindGroupLayoutRelease(
                        _unfilterableTextureBindGroupLayout);
                    _unfilterableTextureBindGroupLayout =
                        null;
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
            out bool hasPreparedGaussianBlur,
            out bool usesUnfilterableMaterial)
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
                usesUnfilterableMaterial = false;
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
                else
                {
                    usesUnfilterableMaterial =
                        hasYuvConversion &&
                        texture.Format ==
                            ProGpuTextureFormats
                                .R16Unorm &&
                        chromaTexture.Format ==
                            ProGpuTextureFormats
                                .RG16Unorm;
                }
                return CreateTextureBindGroup(
                    compositor,
                    texture,
                    chromaTexture,
                    entry.TextureSamplingMode,
                    retainUntilSubmit: true,
                    usesUnfilterableMaterial);
            }

            lease?.Dispose();
            chromaLease?.Dispose();
            texture = _whiteTexture ??
                throw new InvalidOperationException(
                    "Mesh3D fallback texture was not initialized.");
            hasSourceTexture = false;
            hasYuvConversion = false;
            hasPreparedGaussianBlur = false;
            usesUnfilterableMaterial = false;
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
                    retainUntilSubmit: false,
                    unfilterable: false);
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
            bool retainUntilSubmit,
            bool unfilterable)
        {
            int entryCount = unfilterable ? 2 : 3;
            var entries =
                stackalloc BindGroupEntry[entryCount];
            if (unfilterable)
            {
                entries[0] = new BindGroupEntry
                {
                    Binding = 3,
                    TextureView = texture.ViewPtr
                };
                entries[1] = new BindGroupEntry
                {
                    Binding = 4,
                    TextureView = chromaTexture.ViewPtr
                };
            }
            else
            {
                entries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Sampler =
                        compositor.GetTextureSampler(
                            samplingMode)
                };
                entries[1] = new BindGroupEntry
                {
                    Binding = 1,
                    TextureView = texture.ViewPtr
                };
                entries[2] = new BindGroupEntry
                {
                    Binding = 2,
                    TextureView =
                        chromaTexture.ViewPtr
                };
            }
            var descriptor = new BindGroupDescriptor
            {
                Layout = unfilterable
                    ? _unfilterableTextureBindGroupLayout
                    : _textureBindGroupLayout,
                EntryCount = (uint)entryCount,
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

            if (_frameViewportCount == 0)
            {
                _frameSceneGeneration = payload.SceneGeneration;
                _frameRecordGeneration = payload.RecordGeneration;
            }
            else
            {
                if (_frameSceneGeneration != payload.SceneGeneration)
                {
                    _frameSceneGeneration = 0;
                }
                if (_frameRecordGeneration != payload.RecordGeneration)
                {
                    _frameRecordGeneration = 0;
                }
            }
            _frameSceneReused &= payload.SceneReused;
            _frameViewportCount++;
            _frameMeshCount += payload.Meshes.Count;
            _frameSceneCompilationCount += payload.SceneCompilationCount;
            _frameModelVisualVisitCount += payload.ModelVisualVisitCount;
            _frameLogicalTargetTextureBytes +=
                payload.LogicalTargetTextureBytes;
            if (payload.MetricsTarget is { } metricsTarget &&
                !_pendingMetricsTargets.Contains(metricsTarget))
            {
                _pendingMetricsTargets.Add(metricsTarget);
            }

            uint uniformsSize = (uint)Marshal.SizeOf<GpuMesh3DUniforms>();

            // Ensure pooled resource exists for current viewport compile index
            while (_viewportResources.Count <= _currentCompileIndex)
            {
                var viewportResource = new ViewportResource(
                    compositor.Context,
                    uniformsSize);
                _viewportResources.Add(viewportResource);
                _viewportBufferResidentBytes +=
                    viewportResource.UniformsBuffer.AllocatedSize;
            }
            var res = _viewportResources[_currentCompileIndex];

            // 1. Create or update dynamic record buffer
            int recordCount = payload.Meshes.Count;

            uint reqRecordsSize = (uint)recordCount * (uint)Marshal.SizeOf<GpuMesh3DRecord>();
            bool recordBufferChanged = false;
            if (res.DynamicRecordsBuffer == null || res.DynamicRecordsBuffer.Size < reqRecordsSize)
            {
                if (res.DynamicRecordsBuffer is { } oldRecordsBuffer)
                {
                    _viewportBufferResidentBytes -=
                        oldRecordsBuffer.AllocatedSize;
                }
                res.DynamicRecordsBuffer?.Dispose();
                res.DynamicRecordsBuffer = new GpuBuffer(compositor.Context, reqRecordsSize * 2, BufferUsage.Storage | BufferUsage.CopyDst, "Dynamic Mesh3D Records Buffer");
                _viewportBufferResidentBytes +=
                    res.DynamicRecordsBuffer.AllocatedSize;
                res.RecordGen = -1; // Force bind group recreation
                recordBufferChanged = true;
            }
            uint reqRecordIndicesSize = (uint)recordCount * sizeof(uint);
            bool recordIndexBufferChanged = false;
            if (res.RecordIndexBuffer == null ||
                res.RecordIndexBuffer.Size < reqRecordIndicesSize)
            {
                if (res.RecordIndexBuffer is { } oldIndexBuffer)
                {
                    _viewportBufferResidentBytes -=
                        oldIndexBuffer.AllocatedSize;
                }
                res.RecordIndexBuffer?.Dispose();
                res.RecordIndexBuffer = new GpuBuffer(
                    compositor.Context,
                    reqRecordIndicesSize * 2,
                    BufferUsage.Vertex | BufferUsage.CopyDst,
                    "Dynamic Mesh3D Record Indices Buffer");
                _viewportBufferResidentBytes +=
                    res.RecordIndexBuffer.AllocatedSize;
                recordIndexBufferChanged = true;
            }

            int edgeCount = 0;
            for (int i = 0; i < recordCount; i++)
            {
                edgeCount = checked(
                    edgeCount + payload.Meshes[i].Edges.Length);
            }
            uint requiredEdgeSize = checked(
                (uint)edgeCount *
                (uint)Unsafe.SizeOf<GpuMesh3DEdge>());
            bool edgeBufferChanged = false;
            if (edgeCount > 0 &&
                (res.EdgeBuffer == null ||
                 res.EdgeBuffer.Size < requiredEdgeSize))
            {
                if (res.EdgeBuffer is { } oldEdgeBuffer)
                {
                    _viewportBufferResidentBytes -=
                        oldEdgeBuffer.AllocatedSize;
                }
                res.EdgeBuffer?.Dispose();
                res.EdgeBuffer = new GpuBuffer(
                    compositor.Context,
                    requiredEdgeSize <= uint.MaxValue / 2
                        ? requiredEdgeSize * 2
                        : requiredEdgeSize,
                    BufferUsage.Vertex | BufferUsage.CopyDst,
                    "Retained Mesh3D Edge Buffer");
                _viewportBufferResidentBytes +=
                    res.EdgeBuffer.AllocatedSize;
                edgeBufferChanged = true;
            }
            res.EdgeCount = (uint)edgeCount;

            bool uploadEdges = edgeCount > 0 &&
                (payload.SceneGeneration == 0 ||
                 edgeBufferChanged ||
                 res.UploadedEdgeSceneGeneration !=
                    payload.SceneGeneration ||
                 res.UploadedEdgeCount != edgeCount);
            if (uploadEdges)
            {
                _compileScratch.EnsureEdgeCapacity(edgeCount);
                Span<GpuMesh3DEdge> gpuEdges =
                    _compileScratch.Edges[..edgeCount];
                int edgeIndex = 0;
                for (int recordIndex = 0;
                     recordIndex < recordCount;
                     recordIndex++)
                {
                    ReadOnlySpan<MeshEdge3D> meshEdges =
                        payload.Meshes[recordIndex].Edges;
                    for (int localEdgeIndex = 0;
                         localEdgeIndex < meshEdges.Length;
                         localEdgeIndex++)
                    {
                        MeshEdge3D edge =
                            meshEdges[localEdgeIndex];
                        ValidateEdge(edge);
                        gpuEdges[edgeIndex++] =
                            new GpuMesh3DEdge
                            {
                                Start = new Vector4(
                                    edge.Start,
                                    1.0f),
                                End = new Vector4(
                                    edge.End,
                                    1.0f),
                                FirstFaceNormal = new Vector4(
                                    edge.FirstFaceNormal,
                                    0.0f),
                                SecondFaceNormal = new Vector4(
                                    edge.SecondFaceNormal,
                                    0.0f),
                                RecordIndex = (uint)recordIndex,
                                Topology = (uint)edge.Topology
                            };
                    }
                }
                res.EdgeBuffer!.Write(gpuEdges);
                res.UploadedEdgeSceneGeneration =
                    payload.SceneGeneration;
                res.UploadedEdgeCount = edgeCount;
                _frameEdgeUploadBytes += requiredEdgeSize;
            }

            // 2. Upload records data
            _compileScratch.EnsureCapacity(recordCount);
            Span<GpuMesh3DRecord> cpuRecords =
                _compileScratch.Records[..recordCount];
            Span<nint> textureBindGroups =
                _compileScratch.TextureBindGroups[..recordCount];
            Span<uint> recordIndices =
                _compileScratch.RecordIndices[..recordCount];
            Span<byte> unfilterableMaterials =
                _compileScratch
                    .UnfilterableMaterials[..recordCount];
            bool hasUnfilterableMaterials = false;
            bool hasDynamicTextureSource = false;
            for (int i = 0; i < recordCount; i++)
            {
                hasDynamicTextureSource |=
                    payload.Meshes[i].TextureSource is not null;
            }
            int activeOpacityBits =
                BitConverter.SingleToInt32Bits(
                    compositor.ActiveOpacity);
            bool uploadRecords =
                payload.RecordGeneration == 0 ||
                recordBufferChanged ||
                recordIndexBufferChanged ||
                res.UploadedRecordGeneration !=
                    payload.RecordGeneration ||
                res.UploadedRecordCount != recordCount ||
                res.UploadedOpacityBits != activeOpacityBits ||
                hasDynamicTextureSource;
            int n = recordCount;
            for (int i = 0; i < n; i++)
            {
                var mesh = payload.Meshes[i];
                textureBindGroups[i] =
                    (nint)GetTextureBindGroup(
                        compositor,
                        mesh,
                        out GpuTexture materialTexture,
                        out bool hasMaterialTexture,
                        out bool hasYuvConversion,
                        out bool hasPreparedGaussianBlur,
                        out bool usesUnfilterableMaterial);
                unfilterableMaterials[i] =
                    usesUnfilterableMaterial
                        ? (byte)1
                        : (byte)0;
                hasUnfilterableMaterials |=
                    usesUnfilterableMaterial;
                if (!uploadRecords &&
                    mesh.TextureSource is null)
                {
                    continue;
                }
                uploadRecords = true;
                recordIndices[i] = (uint)i;
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
                    MaterialAmbient = new Vector4(
                        mesh.AmbientColor,
                        Math.Clamp(mesh.SelfIllumination, 0.0f, 1.0f)),
                    Opacity = mesh.Opacity * compositor.ActiveOpacity,
                    RenderMode = rMode,
                    ShadingMode = (float)payload.ShadingMode,
                    TextureSamplingMode =
                        (mesh.TextureSamplingMode ==
                            TextureSamplingMode.Nearest ? 0f : 1f) +
                        (2f * (float)mesh.TextureTilingMode),
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
            if (uploadRecords)
            {
                res.DynamicRecordsBuffer.Write(cpuRecords);
                res.RecordIndexBuffer.Write(recordIndices);
                res.UploadedRecordGeneration =
                    payload.RecordGeneration;
                res.UploadedRecordCount = recordCount;
                res.UploadedOpacityBits = activeOpacityBits;
                _frameRecordUploadBytes += reqRecordsSize;
                _frameRecordIndexUploadBytes +=
                    reqRecordIndicesSize;
            }

            Matrix4x4.Invert(cmd.CameraView, out var invView);
            Vector3 cameraPos = invView.Translation;

            // 3. Upload uniforms data
            Mesh3DEdgeStyle edgeStyle =
                payload.EdgeStyle.Validate();
            var cpuUniforms = new GpuMesh3DUniforms
            {
                Projection = cmd.Transform, // Perspective projection matrix
                View = cmd.CameraView,      // View matrix
                CameraPosition = cameraPos,
                VisibleEdgeColor =
                    edgeStyle.VisibleColor,
                OccludedEdgeColor =
                    edgeStyle.OccludedColor,
                EdgeOptions0 = new Vector4(
                    edgeStyle.Width,
                    MathF.Cos(
                        edgeStyle.CreaseAngleDegrees *
                        MathF.PI / 180.0f),
                    edgeStyle.OccludedDashLength,
                    edgeStyle.OccludedGapLength),
                EdgeOptions1 = new Vector4(
                    (uint)edgeStyle.Display,
                    payload.ColorTexture.Width,
                    payload.ColorTexture.Height,
                    edgeStyle.ExtensionLength),
                EdgeOptions2 = new Vector4(
                    edgeStyle.JitterAmount,
                    0.0f,
                    0.0f,
                    0.0f)
            };
            res.UniformsBuffer.WriteSingle(cpuUniforms);
            _frameUniformUploadBytes += uniformsSize;

            // 4. Create the physical-resolution or 4x-MSAA pipeline variant on demand.
            RenderPipeline* cachedPipeline = sampleCount == 1 ? _cachedPipelineSingle : _cachedPipelineMsaa;
            RenderPipeline* cachedBackFacePipeline = sampleCount == 1 ? _cachedBackFacePipelineSingle : _cachedBackFacePipelineMsaa;
            RenderPipeline* cachedWireframePipeline = sampleCount == 1 ? _cachedWireframePipelineSingle : _cachedWireframePipelineMsaa;
            RenderPipeline* cachedVisibleEdgePipeline =
                sampleCount == 1
                    ? _cachedVisibleEdgePipelineSingle
                    : _cachedVisibleEdgePipelineMsaa;
            RenderPipeline* cachedOccludedEdgePipeline =
                sampleCount == 1
                    ? _cachedOccludedEdgePipelineSingle
                    : _cachedOccludedEdgePipelineMsaa;
            RenderPipeline* cachedUnfilterablePipeline =
                sampleCount == 1
                    ? _cachedUnfilterablePipelineSingle
                    : _cachedUnfilterablePipelineMsaa;
            RenderPipeline*
                cachedUnfilterableBackFacePipeline =
                    sampleCount == 1
                        ? _cachedUnfilterableBackFacePipelineSingle
                        : _cachedUnfilterableBackFacePipelineMsaa;
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

            if (hasUnfilterableMaterials &&
                cachedUnfilterablePipeline == null)
            {
                cachedUnfilterablePipeline =
                    CreateMeshPipeline(
                        compositor,
                        $"Mesh3DSolidShader_3D_v3_{sampleCount}",
                        Mesh3DSolidShaderCode,
                        "Mesh3D WGSL 3D Solid Shader",
                        $"Mesh3DUnfilterablePipeline_3D_v1_{sampleCount}",
                        CullMode.Back,
                        sampleCount,
                        _unfilterableSolidPipelineLayout,
                        "fs_unfilterable");
                if (sampleCount == 1)
                {
                    _cachedUnfilterablePipelineSingle =
                        cachedUnfilterablePipeline;
                }
                else
                {
                    _cachedUnfilterablePipelineMsaa =
                        cachedUnfilterablePipeline;
                }
            }

            if (hasUnfilterableMaterials &&
                cachedUnfilterableBackFacePipeline == null)
            {
                cachedUnfilterableBackFacePipeline =
                    CreateMeshPipeline(
                        compositor,
                        $"Mesh3DSolidShader_3D_v3_{sampleCount}",
                        Mesh3DSolidShaderCode,
                        "Mesh3D WGSL 3D Solid Shader",
                        $"Mesh3DUnfilterableBackFacePipeline_3D_v1_{sampleCount}",
                        CullMode.Front,
                        sampleCount,
                        _unfilterableSolidPipelineLayout,
                        "fs_unfilterable");
                if (sampleCount == 1)
                {
                    _cachedUnfilterableBackFacePipelineSingle =
                        cachedUnfilterableBackFacePipeline;
                }
                else
                {
                    _cachedUnfilterableBackFacePipelineMsaa =
                        cachedUnfilterableBackFacePipeline;
                }
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

            Mesh3DEdgeDisplay visibleEdgeClasses =
                edgeStyle.Display &
                (Mesh3DEdgeDisplay.Boundary |
                 Mesh3DEdgeDisplay.Crease |
                 Mesh3DEdgeDisplay.Silhouette);
            bool renderVisibleEdges =
                res.EdgeCount > 0 &&
                visibleEdgeClasses != Mesh3DEdgeDisplay.None;
            bool renderOccludedEdges =
                renderVisibleEdges &&
                (edgeStyle.Display & Mesh3DEdgeDisplay.Occluded) != 0;
            if (renderVisibleEdges &&
                cachedVisibleEdgePipeline == null)
            {
                cachedVisibleEdgePipeline =
                    CreateEdgePipeline(
                        compositor,
                        sampleCount,
                        occluded: false);
                if (sampleCount == 1)
                {
                    _cachedVisibleEdgePipelineSingle =
                        cachedVisibleEdgePipeline;
                }
                else
                {
                    _cachedVisibleEdgePipelineMsaa =
                        cachedVisibleEdgePipeline;
                }
            }
            if (renderOccludedEdges &&
                cachedOccludedEdgePipeline == null)
            {
                cachedOccludedEdgePipeline =
                    CreateEdgePipeline(
                        compositor,
                        sampleCount,
                        occluded: true);
                if (sampleCount == 1)
                {
                    _cachedOccludedEdgePipelineSingle =
                        cachedOccludedEdgePipeline;
                }
                else
                {
                    _cachedOccludedEdgePipelineMsaa =
                        cachedOccludedEdgePipeline;
                }
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
            CommandEncoder* encoder;
            ReadOnlySpan<byte> encoderLabel =
                "Mesh3D Offscreen Encoder\0"u8;
            fixed (byte* encoderLabelPointer = encoderLabel)
            {
                var encoderDesc = new CommandEncoderDescriptor
                {
                    Label = encoderLabelPointer
                };
                encoder = wgpu.DeviceCreateCommandEncoder(
                    device,
                    &encoderDesc);
            }

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
                        _geometryBufferResidentBytes -=
                            cache.VertexBuffer.AllocatedSize;
                        cache.VertexBuffer.Dispose();
                        needsRebuild = true;
                        _frameGeometryCacheMissCount++;
                    }
                    else
                    {
                        _frameGeometryCacheHitCount++;
                    }
                }
                else
                {
                    needsRebuild = true;
                    _frameGeometryCacheMissCount++;
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
                    _frameGeometryVertexUploadBytes += vSize;
                    _geometryBufferResidentBytes +=
                        vBuffer.AllocatedSize;

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
                wgpu.RenderPassEncoderSetBindGroup(pass, 0, res.SolidBindGroup, 0, null);
                RenderPipeline* activePipeline = null;
                for (int i = 0; i < payload.Meshes.Count; i++)
                {
                    var entry = payload.Meshes[i];
                    if (entry.Geometry == null || entry.IsBackFace) continue;

                    var cache = _geometryCache[entry.Geometry];
                    RenderPipeline* requiredPipeline =
                        unfilterableMaterials[i] != 0
                            ? cachedUnfilterablePipeline
                            : cachedPipeline;
                    if (requiredPipeline != activePipeline)
                    {
                        wgpu.RenderPassEncoderSetPipeline(
                            pass,
                            requiredPipeline);
                        activePipeline = requiredPipeline;
                    }

                    wgpu.RenderPassEncoderSetBindGroup(
                        pass,
                        1,
                        (BindGroup*)textureBindGroups[i],
                        0,
                        null);
                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, cache.VertexBuffer.BufferPtr, 0, cache.VertexBuffer.Size);
                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 1, res.RecordIndexBuffer.BufferPtr, (ulong)i * sizeof(uint), sizeof(uint));
                    wgpu.RenderPassEncoderDraw(pass, cache.VertexCount, 1, 0, 0);
                    _frameDrawCallCount++;
                }

                wgpu.RenderPassEncoderSetBindGroup(pass, 0, res.SolidBindGroup, 0, null);
                activePipeline = null;
                for (int i = 0; i < payload.Meshes.Count; i++)
                {
                    var entry = payload.Meshes[i];
                    if (entry.Geometry == null || !entry.IsBackFace) continue;

                    var cache = _geometryCache[entry.Geometry];
                    RenderPipeline* requiredPipeline =
                        unfilterableMaterials[i] != 0
                            ? cachedUnfilterableBackFacePipeline
                            : cachedBackFacePipeline;
                    if (requiredPipeline != activePipeline)
                    {
                        wgpu.RenderPassEncoderSetPipeline(
                            pass,
                            requiredPipeline);
                        activePipeline = requiredPipeline;
                    }

                    wgpu.RenderPassEncoderSetBindGroup(
                        pass,
                        1,
                        (BindGroup*)textureBindGroups[i],
                        0,
                        null);
                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, cache.VertexBuffer.BufferPtr, 0, cache.VertexBuffer.Size);
                    wgpu.RenderPassEncoderSetVertexBuffer(pass, 1, res.RecordIndexBuffer.BufferPtr, (ulong)i * sizeof(uint), sizeof(uint));
                    wgpu.RenderPassEncoderDraw(pass, cache.VertexCount, 1, 0, 0);
                    _frameDrawCallCount++;
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
                    _frameDrawCallCount++;
                }
            }

            if (renderVisibleEdges)
            {
                wgpu.RenderPassEncoderSetPipeline(
                    pass,
                    cachedVisibleEdgePipeline);
                wgpu.RenderPassEncoderSetBindGroup(
                    pass,
                    0,
                    res.SolidBindGroup,
                    0,
                    null);
                wgpu.RenderPassEncoderSetVertexBuffer(
                    pass,
                    0,
                    res.EdgeBuffer!.BufferPtr,
                    0,
                    requiredEdgeSize);
                wgpu.RenderPassEncoderDraw(
                    pass,
                    edgeStyle.JitterAmount > 0.0f ? 18U : 6U,
                    res.EdgeCount,
                    0,
                    0);
                _frameDrawCallCount++;
            }
            if (renderOccludedEdges)
            {
                wgpu.RenderPassEncoderSetPipeline(
                    pass,
                    cachedOccludedEdgePipeline);
                wgpu.RenderPassEncoderDraw(
                    pass,
                    edgeStyle.JitterAmount > 0.0f ? 18U : 6U,
                    res.EdgeCount,
                    0,
                    0);
                _frameDrawCallCount++;
            }

            wgpu.RenderPassEncoderEnd(pass);
            wgpu.RenderPassEncoderRelease(pass);

            // 8. Add offscreen command buffer to the deferred submission queue
            CommandBuffer* cmdBuffer;
            ReadOnlySpan<byte> commandBufferLabel =
                "Mesh3D Offscreen Command Buffer\0"u8;
            fixed (byte* commandBufferLabelPointer =
                       commandBufferLabel)
            {
                var cmdDesc = new CommandBufferDescriptor
                {
                    Label = commandBufferLabelPointer
                };
                cmdBuffer = wgpu.CommandEncoderFinish(
                    encoder,
                    &cmdDesc);
            }

            _pendingCommandBuffers.Add((nint)cmdBuffer);
            _frameCommandBufferCount++;

            wgpu.CommandEncoderRelease(encoder);

            _currentCompileIndex++;

            // DrawExtension is now a no-op in the main compositor pass since the offscreen pass is fully complete and
            // the Viewport3D control appends a separate DrawTexture command!
            cmd.PointBufferOffset = 0;
            cmd.PointBufferCount = 0;
        }

        public unsafe void EndFrame(Compositor compositor)
        {
            int queueSubmissionCount = 0;
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

                compositor.Context.Submit((uint)count, buffers);
                queueSubmissionCount = 1;

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

            LastFrameMetrics = new Mesh3DFrameMetrics(
                compositor.FrameNumber,
                _frameSceneGeneration,
                _frameRecordGeneration,
                _frameViewportCount > 0 && _frameSceneReused,
                _frameViewportCount,
                _frameMeshCount,
                _frameDrawCallCount,
                _frameSceneCompilationCount,
                _frameModelVisualVisitCount,
                _frameGeometryCacheHitCount,
                _frameGeometryCacheMissCount,
                _frameGeometryVertexUploadBytes,
                _frameRecordUploadBytes,
                _frameRecordIndexUploadBytes,
                _frameEdgeUploadBytes,
                _frameUniformUploadBytes,
                _geometryCache.Count,
                _geometryBufferResidentBytes,
                _viewportResources.Count,
                _viewportBufferResidentBytes,
                _frameLogicalTargetTextureBytes,
                _frameCommandBufferCount,
                queueSubmissionCount);
            for (int i = 0; i < _pendingMetricsTargets.Count; i++)
            {
                _pendingMetricsTargets[i].LastFrameMetrics =
                    LastFrameMetrics;
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
        /// <summary>
        /// Nonzero for an explicitly retained CPU scene. Zero preserves the
        /// legacy dynamic behavior and forces record uploads on every frame.
        /// </summary>
        public ulong SceneGeneration { get; set; }
        public ulong RecordGeneration { get; set; }
        public bool SceneReused { get; set; }
        public int SceneCompilationCount { get; set; }
        public int ModelVisualVisitCount { get; set; }
        public Mesh3DFrameMetricsTarget? MetricsTarget { get; set; }
        public ulong LogicalTargetTextureBytes { get; set; }
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
        public Mesh3DEdgeStyle EdgeStyle { get; set; } = Mesh3DEdgeStyle.Disabled;
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
        public MeshEdge3D[] Edges { get; set; } = Array.Empty<MeshEdge3D>();
        public IProGpuTextureLeaseSource? TextureSource { get; set; }
        public MeshTextureEffect TextureEffect { get; set; } =
            MeshTextureEffect.Identity;
        public TextureSamplingMode TextureSamplingMode { get; set; } =
            TextureSamplingMode.Linear;
        public MeshTextureTilingMode TextureTilingMode { get; set; } =
            MeshTextureTilingMode.None;
        public ImageEffectYuvConversion? YuvConversion { get; set; }
        public MeshTexturePresentation TexturePresentation { get; set; } =
            MeshTexturePresentation.Identity;
        public Matrix4x4 ModelTransform { get; set; } = Matrix4x4.Identity;
        public Vector4 Color { get; set; } = Vector4.One;
        public Vector3 SpecularColor { get; set; } = new Vector3(0.2f, 0.2f, 0.2f);
        public float Shininess { get; set; } = 32.0f;
        public Vector3 AmbientColor { get; set; } = new Vector3(0.2f, 0.2f, 0.2f);
        public float SelfIllumination { get; set; }
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

    public enum MeshTextureTilingMode
    {
        None = 0,
        Tile = 1,
        Crop = 2,
        Clamp = 3,
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
