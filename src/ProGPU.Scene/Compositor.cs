using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.Compute;
using Color = Silk.NET.WebGPU.Color;

namespace ProGPU.Scene;

[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct GpuBrush
{
    [FieldOffset(0)] public uint Type;             // 0 = Solid, 1 = Linear, 2 = Radial
    [FieldOffset(4)] public float Opacity;
    [FieldOffset(8)] public Vector2 StartPoint;
    [FieldOffset(16)] public Vector2 EndPoint;
    [FieldOffset(24)] public Vector2 Center;
    [FieldOffset(32)] public float Radius;
    [FieldOffset(36)] public uint StopCount;
    [FieldOffset(40)] public uint Pad;
    
    [FieldOffset(48)] public Vector4 Color0;
    [FieldOffset(64)] public Vector4 Color1;
    [FieldOffset(80)] public Vector4 Color2;
    [FieldOffset(96)] public Vector4 Color3;
    [FieldOffset(112)] public Vector4 Offsets;
}

[StructLayout(LayoutKind.Explicit, Size = 192)]
public struct GpuUniforms
{
    [FieldOffset(0)] public Matrix4x4 Projection;
    [FieldOffset(64)] public Matrix4x4 Mvp;
    [FieldOffset(128)] public Matrix4x4 View;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuHatchRecord
{
    public uint StartSegment;
    public uint SegmentCount;
    public float MinX;
    public float MinY;
    public float MaxX;
    public float MaxY;
    public uint Pad0;
    public uint Pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuHatchSegment
{
    public Vector2 P0;
    public Vector2 P1;
    public Vector2 P2;
    public Vector2 P3;
    public uint SegmentType;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuAcisEdge
{
    public Vector4 P0; // p0.xyz = start coordinate, p0.w = unused
    public Vector4 P1; // p1.xyz = end coordinate, p1.w = unused
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuAcisRecord
{
    public Matrix4x4 Transform;
    public Vector4 Color;
    public uint StartEdge;
    public uint EdgeCount;
    public float PenThickness;
    public float Opacity;
}

public struct CompositorMetrics
{
    public double FrameTimeMs;
    public double VisualTreeCompileTimeMs;
    public double GpuUploadTimeMs;
    public double RenderPassTimeMs;
    public int DrawCallsCount;
    public int VectorVerticesCount;
    public int TextVerticesCount;
    public int PathAtlasCachedCount;
}

public unsafe class Compositor : IDisposable
{
    public CompositorMetrics Metrics { get; private set; }

    // Decoupled hooks to remove hard dependency on UI layer
    public event Action<uint, uint>? PreRender;
    public Func<System.Collections.Generic.IReadOnlyList<Visual>>? GetExternalLayers { get; set; }
    public Func<Visual?>? GetTooltip { get; set; }
    public Func<Vector2>? GetMousePosition { get; set; }
    public Action<DrawingContext, uint, uint>? RenderDiagnostics { get; set; }
    public Vector4 ClearColor { get; set; } = new Vector4(0.08f, 0.08f, 0.12f, 1.0f);

    private readonly WgpuContext _context;
    private readonly RenderPipelineCache _pipelineCache;
    private readonly GlyphAtlas _atlas;
    private readonly PathAtlas _pathAtlas;
    private BindGroupLayout* _pathAtlasBindGroupLayout;
    private BindGroup* _pathAtlasBindGroup;
    private BindGroupLayout* _pathAtlasBindGroupLayoutOffscreen;
    private BindGroup* _pathAtlasBindGroupOffscreen;

    // MSAA color target resources
    private Texture* _msaaTexture;
    private TextureView* _msaaTextureView;
    private uint _msaaWidth;
    private uint _msaaHeight;

    // Uniform buffer (Projection Matrix)
    private readonly GpuBuffer _uniformBuffer;
    private BindGroup* _vectorUniformBindGroup;
    private BindGroup* _textUniformBindGroup;
    private BindGroup* _textureUniformBindGroup;
    private BindGroupLayout* _vectorUniformBindGroupLayout;
    private BindGroupLayout* _textUniformBindGroupLayout;
    private BindGroupLayout* _textureUniformBindGroupLayout;

    private BindGroup* _vectorUniformBindGroupOffscreen;
    private BindGroup* _textUniformBindGroupOffscreen;
    private BindGroup* _textureUniformBindGroupOffscreen;
    private BindGroupLayout* _vectorUniformBindGroupLayoutOffscreen;
    private BindGroupLayout* _textUniformBindGroupLayoutOffscreen;
    private BindGroupLayout* _textureUniformBindGroupLayoutOffscreen;
    private bool _useGpuTransformsActive;
    private Matrix4x4 _cameraViewMatrix;
    private bool _hasGpuTransformsInFrame;
    private Matrix4x4 _gpuTransformsCameraView;

    // Sampler & Texture Bind Group for Typography
    private Sampler* _atlasSampler;
    private BindGroup* _atlasBindGroup;
    private BindGroupLayout* _atlasBindGroupLayout;
    private BindGroup* _atlasBindGroupOffscreen;
    private BindGroupLayout* _atlasBindGroupLayoutOffscreen;

    // Render Pipelines
    private RenderPipeline* _vectorPipeline;
    private RenderPipeline* _textPipeline;
    private RenderPipeline* _texturePipeline;
    private RenderPipeline* _vectorPipelineOffscreen;
    private RenderPipeline* _textPipelineOffscreen;
    private RenderPipeline* _texturePipelineOffscreen;
    private BindGroupLayout* _textureBindGroupLayout;
    private BindGroupLayout* _textureBindGroupLayoutOffscreen;

    // High performance Chart GPGPU pipelines
    private RenderPipeline* _chartLinePipeline;
    private RenderPipeline* _chartScatterPipeline;
    private RenderPipeline* _chartLinePipelineOffscreen;
    private RenderPipeline* _chartScatterPipelineOffscreen;
    private BindGroupLayout* _chartLineBindGroupLayout;
    private BindGroupLayout* _chartScatterBindGroupLayout;

    // Captured frame parameters
    private uint _currentWidth;
    private uint _currentHeight;
    private float _currentDpiScale;
    private Matrix4x4 _currentProjection;

    private GpuBuffer _hatchRecordsBuffer;
    private GpuBuffer _hatchSegmentsBuffer;
    private readonly List<GpuHatchRecord> _hatchRecordsList = new();
    private readonly List<GpuHatchSegment> _hatchSegmentsList = new();

    private GpuBuffer _acisRecordsBuffer;
    private GpuBuffer _acisEdgesBuffer;
    private readonly List<GpuAcisRecord> _acisRecordsList = new();
    private readonly List<GpuAcisEdge> _acisEdgesList = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, GpuSeriesBuffer> _dynamicGpuBufferCache = new();

    // Batch buffers (Dynamic GPU vertex & index buffers)
    private GpuBuffer _vectorVertexBuffer;
    private GpuBuffer _vectorIndexBuffer;
    private GpuBuffer _textVertexBuffer;
    private GpuBuffer _textIndexBuffer;
    private GpuBuffer _textureVertexBuffer;
    private GpuBuffer _textureIndexBuffer;

    public enum DrawCallType
    {
        Vector,
        Texture,
        Text,
        StaticDxf,
        ChartLine,
        ChartScatter
    }

    public struct CompositorDrawCall
    {
        public DrawCallType Type;
        public uint IndexStart;
        public uint IndexCount;
        public GpuTexture? Texture;
        public object? StaticBuffer;
        
        // GPU Chart properties
        public Matrix4x4 Transform;
        public float LineThicknessOrRadius;
        public Vector4 Color;
        public Vector2 Scale;
        public Vector2 Translate;
        public Rect? ClipRect;
    }

    private readonly List<VectorVertex> _vectorVerticesList = new();
    private readonly List<uint> _vectorIndicesList = new();
    private readonly List<VectorVertex> _textVerticesList = new();
    private readonly List<uint> _textIndicesList = new();
    private readonly List<VectorVertex> _textureVerticesList = new();
    private readonly List<uint> _textureIndicesList = new();
    public readonly struct TextureCacheKey : IEquatable<TextureCacheKey>
    {
        public readonly ulong TextureId;
        public readonly uint Generation;
        public readonly bool IsOffscreen;

        public TextureCacheKey(ulong textureId, uint generation, bool isOffscreen)
        {
            TextureId = textureId;
            Generation = generation;
            IsOffscreen = isOffscreen;
        }

        public bool Equals(TextureCacheKey other) => TextureId == other.TextureId && Generation == other.Generation && IsOffscreen == other.IsOffscreen;
        public override bool Equals(object? obj) => obj is TextureCacheKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(TextureId, Generation, IsOffscreen);
    }

    public class CachedBindGroup
    {
        public nint BindGroupPtr { get; }
        public ulong LastUsedFrame { get; set; }

        public CachedBindGroup(nint bindGroupPtr, ulong lastUsedFrame)
        {
            BindGroupPtr = bindGroupPtr;
            LastUsedFrame = lastUsedFrame;
        }
    }

    private readonly List<CompositorDrawCall> _drawCalls = new();
    private readonly Dictionary<TextureCacheKey, CachedBindGroup> _persistentTextureBindGroups = new();
    private readonly List<GpuBrush> _activeBrushes = new();
    private readonly GpuBuffer _brushesStorageBuffer;
    private ulong _frameNumber = 0;
    private float _totalTime = 0f;
    private readonly Dictionary<(string Text, TtfFont Font, float Size, TextAlignment Align), TextLayout> _layoutCache = new();
    private uint _pendingVectorStart = 0;
    private uint _pendingTextStart = 0;

    private readonly ComputeAccelerator _compute;
    private readonly Dictionary<Visual, (GpuTexture Source, GpuTexture Temp, GpuTexture Destination)> _effectTextures = new();
    private readonly HashSet<Visual> _elementsRenderingEffects = new();
    private readonly HashSet<Visual> _elementsRenderingLayers = new();
    private readonly HashSet<GpuTexture> _allocatedLayerTextures = new();

    private bool _isDisposed;

    private readonly Stack<Rect> _clipStack = new();
    private Rect? _activeClipRect;

    private readonly Stack<float> _opacityStack = new();
    private float _activeOpacity = 1.0f;

    public static float DefaultTextGamma = 1.43f;
    public static float DefaultTextContrast = 1.15f;
    public static bool IsCacheAsLayerEnabled { get; set; } = true;

    public int VectorVertexCount => _vectorVerticesList.Count;
    public IReadOnlyList<VectorVertex> VectorVertices => _vectorVerticesList;
    public int VectorIndexCount => _vectorIndicesList.Count;
    public int TextVertexCount => _textVerticesList.Count;
    public int TextIndexCount => _textIndicesList.Count;
    public int TextureVertexCount => _textureVerticesList.Count;
    public int TextureIndexCount => _textureIndicesList.Count;
    public int TextureDrawCallCount
    {
        get
        {
            int count = 0;
            foreach (var dc in _drawCalls)
            {
                if (dc.Type == DrawCallType.Texture) count++;
            }
            return count;
        }
    }

    public GlyphAtlas Atlas => _atlas;
    public TextureFormat RenderFormat { get; private set; }

    public Compositor(WgpuContext context, TextureFormat? renderFormat = null)
    {
        _context = context;
        RenderFormat = renderFormat ?? _context.SwapChainFormat;
        _pipelineCache = new RenderPipelineCache(_context);
        _compute = new ComputeAccelerator(_context);
        
        // 1. Initialize Glyph Atlas (4096x4096)
        _atlas = new GlyphAtlas(_context, 4096);
        _pathAtlas = new PathAtlas(_context, 4096);

        // 2. Uniform Buffer allocation (Projection Matrix + MVP - 128 bytes)
        _uniformBuffer = new GpuBuffer(
            _context, 
            (uint)Marshal.SizeOf<GpuUniforms>(), 
            BufferUsage.Uniform | BufferUsage.CopyDst, 
            "Compositor Uniform Projection Buffer"
        );

        // Allocate brushes storage buffer (8192 brushes * 128 bytes = 1,048,576 bytes)
        _brushesStorageBuffer = new GpuBuffer(
            _context,
            8192 * 128,
            BufferUsage.Storage | BufferUsage.CopyDst,
            "Compositor Brushes Storage Buffer"
        );

        // 3. Dynamic mesh buffer setup (Vertex format: VectorVertex)
        uint initialVertexCount = 100000;
        uint initialIndexCount = 150000;
        uint vertexStride = (uint)Marshal.SizeOf<VectorVertex>();

        _vectorVertexBuffer = new GpuBuffer(_context, initialVertexCount * vertexStride, BufferUsage.Vertex | BufferUsage.CopyDst, "Vector Vertex Buffer");
        _vectorIndexBuffer = new GpuBuffer(_context, initialIndexCount * 4, BufferUsage.Index | BufferUsage.CopyDst, "Vector Index Buffer");

        _textVertexBuffer = new GpuBuffer(_context, initialVertexCount * vertexStride, BufferUsage.Vertex | BufferUsage.CopyDst, "Text Vertex Buffer");
        _textIndexBuffer = new GpuBuffer(_context, initialIndexCount * 4, BufferUsage.Index | BufferUsage.CopyDst, "Text Index Buffer");

        _textureVertexBuffer = new GpuBuffer(_context, initialVertexCount * vertexStride, BufferUsage.Vertex | BufferUsage.CopyDst, "Texture Vertex Buffer");
        _textureIndexBuffer = new GpuBuffer(_context, initialIndexCount * 4, BufferUsage.Index | BufferUsage.CopyDst, "Texture Index Buffer");

        // Allocate persistent direct hatch storage buffers
        _hatchRecordsBuffer = new GpuBuffer(
            _context,
            8192 * (uint)Marshal.SizeOf<GpuHatchRecord>(),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "Hatch Records Storage Buffer"
        );
        _hatchSegmentsBuffer = new GpuBuffer(
            _context,
            65536 * (uint)Marshal.SizeOf<GpuHatchSegment>(),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "Hatch Segments Storage Buffer"
        );

        _acisRecordsBuffer = new GpuBuffer(
            _context,
            8192 * (uint)Marshal.SizeOf<GpuAcisRecord>(),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "ACIS Records Storage Buffer"
        );
        _acisEdgesBuffer = new GpuBuffer(
            _context,
            65536 * (uint)Marshal.SizeOf<GpuAcisEdge>(),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "ACIS Edges Storage Buffer"
        );

        InitializePipelinesAndBindGroups();
    }

    private void InitializePipelinesAndBindGroups()
    {
        // 4. Create WebGPU Sampler for font glyph textures (sharp linear bilinear interpolation)
        var samplerDesc = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Linear,
            MinFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Linear,
            LodMaxClamp = 1f,
            LodMinClamp = 0f,
            MaxAnisotropy = 1
        };
        _atlasSampler = _context.Wgpu.DeviceCreateSampler(_context.Device, &samplerDesc);

        // 5. Compile WGSL shaders
        var vecShaderModule = _pipelineCache.GetOrCreateShader("Vector", Shaders.VectorShader, "VectorShader");
        var textShaderModule = _pipelineCache.GetOrCreateShader("Text", Shaders.TextShader, "TextShader");
        var texShaderModule = _pipelineCache.GetOrCreateShader("Texture", Shaders.TextureShader, "TextureShader");
        var chartLineShaderModule = _pipelineCache.GetOrCreateShader("ChartLine", Shaders.ChartLineShader, "ChartLineShader");
        var chartScatterShaderModule = _pipelineCache.GetOrCreateShader("ChartScatter", Shaders.ChartScatterShader, "ChartScatterShader");

        var vertexAttribs = new VertexAttribute[]
        {
            new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }, // Position
            new() { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }, // Color
            new() { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }, // TexCoord
            new() { Format = VertexFormat.Float32, Offset = 32, ShaderLocation = 3 }, // BrushIndex
            new() { Format = VertexFormat.Float32x2, Offset = 36, ShaderLocation = 4 }, // ShapeSize
            new() { Format = VertexFormat.Float32, Offset = 44, ShaderLocation = 5 }, // CornerRadius
            new() { Format = VertexFormat.Float32, Offset = 48, ShaderLocation = 6 }, // StrokeThickness
            new() { Format = VertexFormat.Float32, Offset = 52, ShaderLocation = 7 } // ShapeType
        };

        fixed (VertexAttribute* attribsPtr = vertexAttribs)
        {
            var layoutDesc = new VertexBufferLayout
            {
                ArrayStride = (uint)Marshal.SizeOf<VectorVertex>(),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 8,
                Attributes = attribsPtr
            };

            // Compile primary graphics pipelines with 4x MSAA
            _vectorPipeline = _pipelineCache.GetOrCreateRenderPipeline(
                "Vector", 
                vecShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 4
            );

            _textPipeline = _pipelineCache.GetOrCreateRenderPipeline(
                "Text", 
                textShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 4
            );

            _texturePipeline = _pipelineCache.GetOrCreateRenderPipeline(
                "Texture", 
                texShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 4
            );

            _vectorPipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                "Vector_Offscreen", 
                vecShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 1
            );

            _textPipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                "Text_Offscreen", 
                textShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 1
            );

            _texturePipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                "Texture_Offscreen", 
                texShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 1
            );

            // Compile high performance Chart GPGPU pipelines (with MSAA 4x)
            _chartLinePipeline = _pipelineCache.GetOrCreateRenderPipeline(
                "ChartLine",
                chartLineShaderModule,
                "vs_main",
                "fs_main",
                RenderFormat,
                PrimitiveTopology.TriangleList,
                Array.Empty<VertexBufferLayout>(),
                enableBlend: true,
                sampleCount: 4
            );

            _chartLinePipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                "ChartLine_Offscreen",
                chartLineShaderModule,
                "vs_main",
                "fs_main",
                RenderFormat,
                PrimitiveTopology.TriangleList,
                Array.Empty<VertexBufferLayout>(),
                enableBlend: true,
                sampleCount: 1
            );

            var scatterAttribs = new VertexAttribute[]
            {
                new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }, // center
                new() { Format = VertexFormat.Float32, Offset = 8, ShaderLocation = 1 }   // radiusPx
            };
            fixed (VertexAttribute* scatterAttribsPtr = scatterAttribs)
            {
                var scatterLayoutDesc = new VertexBufferLayout
                {
                    ArrayStride = 12,
                    StepMode = VertexStepMode.Instance,
                    AttributeCount = 2,
                    Attributes = scatterAttribsPtr
                };
                _chartScatterPipeline = _pipelineCache.GetOrCreateRenderPipeline(
                    "ChartScatter",
                    chartScatterShaderModule,
                    "vs_main",
                    "fs_main",
                    RenderFormat,
                    PrimitiveTopology.TriangleList,
                    new[] { scatterLayoutDesc },
                    enableBlend: true,
                    sampleCount: 4
                );

                _chartScatterPipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                    "ChartScatter_Offscreen",
                    chartScatterShaderModule,
                    "vs_main",
                    "fs_main",
                    RenderFormat,
                    PrimitiveTopology.TriangleList,
                    new[] { scatterLayoutDesc },
                    enableBlend: true,
                    sampleCount: 1
                );
            }
        }

        _chartLineBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_chartLinePipeline, 0);
        _chartScatterBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_chartScatterPipeline, 0);

        _textureBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_texturePipeline, 1);
        _textureBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_texturePipelineOffscreen, 1);

        // 7. Uniform bind groups structure configuration
        _vectorUniformBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_vectorPipeline, 0);
        _textUniformBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_textPipeline, 0);
        _textureUniformBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_texturePipeline, 0);
        
        _vectorUniformBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_vectorPipelineOffscreen, 0);
        _textUniformBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_textPipelineOffscreen, 0);
        _textureUniformBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_texturePipelineOffscreen, 0);

        var uBufferEntryVector = new BindGroupEntry
        {
            Binding = 0,
            Buffer = _uniformBuffer.BufferPtr,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<GpuUniforms>()
        };

        var uBufferEntry = new BindGroupEntry
        {
            Binding = 0,
            Buffer = _uniformBuffer.BufferPtr,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<GpuUniforms>()
        };

        var brushesEntry = new BindGroupEntry
        {
            Binding = 1,
            Buffer = _brushesStorageBuffer.BufferPtr,
            Offset = 0,
            Size = _brushesStorageBuffer.Size
        };

        var hatchRecordsEntry = new BindGroupEntry
        {
            Binding = 2,
            Buffer = _hatchRecordsBuffer.BufferPtr,
            Offset = 0,
            Size = _hatchRecordsBuffer.Size
        };

        var hatchSegmentsEntry = new BindGroupEntry
        {
            Binding = 3,
            Buffer = _hatchSegmentsBuffer.BufferPtr,
            Offset = 0,
            Size = _hatchSegmentsBuffer.Size
        };

        var acisRecordsEntry = new BindGroupEntry
        {
            Binding = 4,
            Buffer = _acisRecordsBuffer.BufferPtr,
            Offset = 0,
            Size = _acisRecordsBuffer.Size
        };

        var acisEdgesEntry = new BindGroupEntry
        {
            Binding = 5,
            Buffer = _acisEdgesBuffer.BufferPtr,
            Offset = 0,
            Size = _acisEdgesBuffer.Size
        };

        var vectorEntries = stackalloc BindGroupEntry[6];
        vectorEntries[0] = uBufferEntryVector;
        vectorEntries[1] = brushesEntry;
        vectorEntries[2] = hatchRecordsEntry;
        vectorEntries[3] = hatchSegmentsEntry;
        vectorEntries[4] = acisRecordsEntry;
        vectorEntries[5] = acisEdgesEntry;

        var uDescVector = new BindGroupDescriptor
        {
            Layout = _vectorUniformBindGroupLayout,
            EntryCount = 6,
            Entries = vectorEntries
        };
        _vectorUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescVector);

        var uDescVectorOffscreen = new BindGroupDescriptor
        {
            Layout = _vectorUniformBindGroupLayoutOffscreen,
            EntryCount = 6,
            Entries = vectorEntries
        };
        _vectorUniformBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescVectorOffscreen);

        var uDescText = new BindGroupDescriptor
        {
            Layout = _textUniformBindGroupLayout,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescText);

        var uDescTextOffscreen = new BindGroupDescriptor
        {
            Layout = _textUniformBindGroupLayoutOffscreen,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textUniformBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescTextOffscreen);

        var uDescTexture = new BindGroupDescriptor
        {
            Layout = _textureUniformBindGroupLayout,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textureUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescTexture);

        var uDescTextureOffscreen = new BindGroupDescriptor
        {
            Layout = _textureUniformBindGroupLayoutOffscreen,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textureUniformBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescTextureOffscreen);

        // 8. Atlas bind group structure configuration
        _atlasBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_textPipeline, 1);
        _atlasBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_textPipelineOffscreen, 1);

        var samplerEntry = new BindGroupEntry
        {
            Binding = 0,
            Sampler = _atlasSampler
        };

        var viewEntry = new BindGroupEntry
        {
            Binding = 1,
            TextureView = _atlas.AtlasTexture.ViewPtr
        };

        var atlasEntries = stackalloc BindGroupEntry[2];
        atlasEntries[0] = samplerEntry;
        atlasEntries[1] = viewEntry;

        var atlasDesc = new BindGroupDescriptor
        {
            Layout = _atlasBindGroupLayout,
            EntryCount = 2,
            Entries = atlasEntries
        };
        _atlasBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &atlasDesc);

        var atlasDescOffscreen = new BindGroupDescriptor
        {
            Layout = _atlasBindGroupLayoutOffscreen,
            EntryCount = 2,
            Entries = atlasEntries
        };
        _atlasBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &atlasDescOffscreen);

        // Initialize Path Atlas bind group
        _pathAtlasBindGroupLayout = _context.Wgpu.RenderPipelineGetBindGroupLayout(_vectorPipeline, 1);
        _pathAtlasBindGroupLayoutOffscreen = _context.Wgpu.RenderPipelineGetBindGroupLayout(_vectorPipelineOffscreen, 1);

        var pathViewEntry = new BindGroupEntry
        {
            Binding = 1,
            TextureView = _pathAtlas.AtlasTexture.ViewPtr
        };
        var pathAtlasEntries = stackalloc BindGroupEntry[2];
        pathAtlasEntries[0] = samplerEntry;
        pathAtlasEntries[1] = pathViewEntry;

        var pathAtlasDesc = new BindGroupDescriptor
        {
            Layout = _pathAtlasBindGroupLayout,
            EntryCount = 2,
            Entries = pathAtlasEntries
        };
        _pathAtlasBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &pathAtlasDesc);

        var pathAtlasDescOffscreen = new BindGroupDescriptor
        {
            Layout = _pathAtlasBindGroupLayoutOffscreen,
            EntryCount = 2,
            Entries = pathAtlasEntries
        };
        _pathAtlasBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &pathAtlasDescOffscreen);
    }

    public void RenderScene(Visual root, uint width, uint height, TextureView* targetView)
    {
        if (_isDisposed) return;
        
        _currentWidth = width;
        _currentHeight = height;
        _currentDpiScale = 1.0f;
        if (_context.Window != null)
        {
            _currentDpiScale = (float)_context.Window.FramebufferSize.X / _context.Window.Size.X;
        }

        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var compileSw = System.Diagnostics.Stopwatch.StartNew();
        _pathAtlas.CleanupFrame();

        if (_atlas.IsAlmostFull)
        {
            _atlas.Clear();
        }

        // Invoke pre-render actions (e.g. measure/arrange popups in UI framework)
        PreRender?.Invoke(width, height);

        _useGpuTransformsActive = false;
        _cameraViewMatrix = Matrix4x4.Identity;
        _hasGpuTransformsInFrame = false;
        _gpuTransformsCameraView = Matrix4x4.Identity;

        // 1. Calculate orthographic projection matrix for modern 2D rendering
        // Maps X in [0, width] to [-1, 1], and Y in [0, height] to [1, -1]
        var projection = new Matrix4x4(
            2.0f / width, 0f, 0f, 0f,
            0f, -2.0f / height, 0f, 0f,
            0f, 0f, 1f, 0f,
            -1.0f, 1.0f, 0f, 1.0f
        );
        _currentProjection = projection;

        // 2. Clear CPU collection batch lists and active brushes
        _activeBrushes.Clear();
        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _textIndicesList.Clear();
        _textureVerticesList.Clear();
        _textureIndicesList.Clear();
        _drawCalls.Clear();
        _hatchRecordsList.Clear();
        _hatchSegmentsList.Clear();
        _acisRecordsList.Clear();
        _acisEdgesList.Clear();

        if (_layoutCache.Count > 1000)
        {
            _layoutCache.Clear();
        }

        _clipStack.Clear();
        _activeClipRect = null;

        _opacityStack.Clear();
        _activeOpacity = 1.0f;

        // 3. Compile Layer 0: Root Visual Scene
        _pendingVectorStart = (uint)_vectorIndicesList.Count;
        _pendingTextStart = (uint)_textIndicesList.Count;
        CompileVisualTree(root, Matrix4x4.Identity);
        CommitPendingDrawCalls();

        // 4. Compile Layer 1: Active Popups / External Layers (in proper Z-order)
        var externalLayers = GetExternalLayers?.Invoke();
        if (externalLayers != null)
        {
            for (int i = 0; i < externalLayers.Count; i++)
            {
                var layer = externalLayers[i];
                _pendingVectorStart = (uint)_vectorIndicesList.Count;
                _pendingTextStart = (uint)_textIndicesList.Count;
                
                CompileVisualTree(layer, Matrix4x4.Identity);
                CommitPendingDrawCalls();
            }
        }

        // 5. Compile Layer 2: Tooltips
        var activeToolTip = GetTooltip?.Invoke();
        if (activeToolTip != null)
        {
            _pendingVectorStart = (uint)_vectorIndicesList.Count;
            _pendingTextStart = (uint)_textIndicesList.Count;
            
            CompileVisualTree(activeToolTip, Matrix4x4.Identity);
            CommitPendingDrawCalls();
        }

        // 6. Compile Layer 3: Adorner / DevTools bounds highlights
        if (RenderDiagnostics != null)
        {
            _pendingVectorStart = (uint)_vectorIndicesList.Count;
            _pendingTextStart = (uint)_textIndicesList.Count;

            var diagContext = new DrawingContext();
            RenderDiagnostics(diagContext, width, height);
            foreach (var cmd in diagContext.Commands)
            {
                var activeTransform = Matrix4x4.Identity;
                switch (cmd.Type)
                {
                    case RenderCommandType.DrawRect:
                        CompileRectCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawPath:
                        CompilePathCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawHatch:
                        CompileHatchCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawAcisSolid:
                        CompileAcisCommand(diagContext, cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawText:
                        CompileTextCommand(cmd, null, activeTransform);
                        break;
                    case RenderCommandType.DrawTexture:
                        CompileTextureCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.PushClip:
                        PushClipRect(cmd.Rect, activeTransform);
                        break;
                    case RenderCommandType.PopClip:
                        PopClipRect();
                        break;
                    case RenderCommandType.PushOpacity:
                        PushOpacityValue(cmd.FontSize);
                        break;
                    case RenderCommandType.PopOpacity:
                        PopOpacityValue();
                        break;
                    case RenderCommandType.DrawLine:
                        CompileLineCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawLine3D:
                        CompileLine3DCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawEllipse:
                        CompileEllipseCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawCircle:
                        CompileCircleCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawRoundedRect:
                        CompileRoundedRectCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawBezier:
                        CompileBezierCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawCubicBezier:
                        CompileCubicBezierCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawPolyline:
                        CompilePolylineCommand(diagContext, cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawSpline:
                        CompileSplineCommand(diagContext, cmd, activeTransform);
                        break;
                    case RenderCommandType.FillTriangle:
                        CompileFillTriangleCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.FillQuad:
                        CompileFillQuadCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawStaticDxf:
                        CommitPendingDrawCalls();
                        _drawCalls.Add(new CompositorDrawCall
                        {
                            Type = DrawCallType.StaticDxf,
                            StaticBuffer = cmd.StaticBuffer
                        });
                        _pendingVectorStart = (uint)_vectorIndicesList.Count;
                        _pendingTextStart = (uint)_textIndicesList.Count;
                        break;
                }
            }
            CommitPendingDrawCalls();
        }

        compileSw.Stop();
        var uploadSw = System.Diagnostics.Stopwatch.StartNew();

        // Dynamic buffer writing will happen after uploads to keep logic clear

        // Upload CPU batches to dynamic GPU buffers
        if (_vectorVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _vectorVertexBuffer, (uint)_vectorVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _vectorVertexBuffer.Write(CollectionsMarshal.AsSpan(_vectorVerticesList));
        }
        if (_vectorIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _vectorIndexBuffer, (uint)_vectorIndicesList.Count * 4, BufferUsage.Index);
            _vectorIndexBuffer.Write(CollectionsMarshal.AsSpan(_vectorIndicesList));
        }

        if (_textVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textVertexBuffer, (uint)_textVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _textVertexBuffer.Write(CollectionsMarshal.AsSpan(_textVerticesList));
        }
        if (_textIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _textIndexBuffer, (uint)_textIndicesList.Count * 4, BufferUsage.Index);
            _textIndexBuffer.Write(CollectionsMarshal.AsSpan(_textIndicesList));
        }

        if (_textureVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textureVertexBuffer, (uint)_textureVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _textureVertexBuffer.Write(CollectionsMarshal.AsSpan(_textureVerticesList));
        }
        if (_textureIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _textureIndexBuffer, (uint)_textureIndicesList.Count * 4, BufferUsage.Index);
            _textureIndexBuffer.Write(CollectionsMarshal.AsSpan(_textureIndicesList));
        }

        // Upload unified projection and MVP matrices
        var uniformsData = new GpuUniforms
        {
            Projection = projection,
            Mvp = _hasGpuTransformsInFrame ? Matrix4x4.Identity : projection,
            View = _hasGpuTransformsInFrame ? _gpuTransformsCameraView : Matrix4x4.Identity
        };
        _uniformBuffer.WriteSingle(uniformsData);

        // Upload compiled active brushes to storage buffer
        if (_activeBrushes.Count > 0)
        {
            _brushesStorageBuffer.Write(CollectionsMarshal.AsSpan(_activeBrushes));
        }

        // Upload direct hatch records and segments to storage buffers
        if (_hatchRecordsList.Count > 0)
        {
            _hatchRecordsBuffer.Write(CollectionsMarshal.AsSpan(_hatchRecordsList));
        }
        else
        {
            var dummy = new GpuHatchRecord();
            _hatchRecordsBuffer.Write(new ReadOnlySpan<GpuHatchRecord>(ref dummy));
        }

        if (_hatchSegmentsList.Count > 0)
        {
            _hatchSegmentsBuffer.Write(CollectionsMarshal.AsSpan(_hatchSegmentsList));
        }
        else
        {
            var dummy = new GpuHatchSegment();
            _hatchSegmentsBuffer.Write(new ReadOnlySpan<GpuHatchSegment>(ref dummy));
        }

        if (_acisRecordsList.Count > 0)
        {
            _acisRecordsBuffer.Write(CollectionsMarshal.AsSpan(_acisRecordsList));
        }
        else
        {
            var dummy = new GpuAcisRecord();
            _acisRecordsBuffer.Write(new ReadOnlySpan<GpuAcisRecord>(ref dummy));
        }

        if (_acisEdgesList.Count > 0)
        {
            _acisEdgesBuffer.Write(CollectionsMarshal.AsSpan(_acisEdgesList));
        }
        else
        {
            var dummy = new GpuAcisEdge();
            _acisEdgesBuffer.Write(new ReadOnlySpan<GpuAcisEdge>(ref dummy));
        }

        // Rasterize all pending paths before starting the render pass
        _pathAtlas.RasterizePendingPaths();

        uploadSw.Stop();
        var passSw = System.Diagnostics.Stopwatch.StartNew();

        // Determine physical render target size for MSAA matching the physical FramebufferSize.
        uint renderWidth = width;
        uint renderHeight = height;
        if (_context.Window != null && width == (uint)_context.Window.Size.X && height == (uint)_context.Window.Size.Y)
        {
            renderWidth = (uint)_context.Window.FramebufferSize.X;
            renderHeight = (uint)_context.Window.FramebufferSize.Y;
        }

        // Recreate MSAA resources if needed (handles initialization and window resizing)
        if (_msaaTexture == null || _msaaWidth != renderWidth || _msaaHeight != renderHeight)
        {
            ReleaseMsaaResources();
            CreateMsaaResources(renderWidth, renderHeight);
        }

        // 5. WebGPU Command Encoder and Render Pass Execution
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compositor Command Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var bgColor = ClearColor;
        var colorAttachment = new RenderPassColorAttachment
        {
            View = _msaaTextureView,
            ResolveTarget = targetView,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = bgColor.X, G = bgColor.Y, B = bgColor.Z, A = bgColor.W }
        };

        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = null
        };

        var pass = _context.Wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);

        DrawCallType? currentType = null;
        var textureEntries = stackalloc BindGroupEntry[2];

        foreach (var dc in _drawCalls)
        {
            if (dc.Type == DrawCallType.Vector)
            {
                if (currentType != DrawCallType.Vector)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, _vectorPipeline);
                    fixed (BindGroup** pGrp = &_vectorUniformBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                    }
                    var buffer = _vectorVertexBuffer.BufferPtr;
                    _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _vectorVertexBuffer.Size);
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _vectorIndexBuffer.Size);
                    currentType = DrawCallType.Vector;
                }
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Text)
            {
                if (currentType != DrawCallType.Text)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, _textPipeline);
                    fixed (BindGroup** pGrp = &_textUniformBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pAtlas = &_atlasBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
                    }
                    var buffer = _textVertexBuffer.BufferPtr;
                    _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textVertexBuffer.Size);
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textIndexBuffer.Size);
                    currentType = DrawCallType.Text;
                }
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Texture && dc.Texture != null)
            {
                _context.Wgpu.RenderPassEncoderSetPipeline(pass, _texturePipeline);
                fixed (BindGroup** pGrp = &_textureUniformBindGroup)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                }
                var buffer = _textureVertexBuffer.BufferPtr;
                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textureVertexBuffer.Size);
                _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textureIndexBuffer.Size);
                currentType = DrawCallType.Texture;

                var viewPtr = dc.Texture.ViewPtr;
                var cacheKey = new TextureCacheKey(dc.Texture.Id, dc.Texture.Generation, isOffscreen: false);

                if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out var cachedBg))
                {
                    textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = _atlasSampler };
                    textureEntries[1] = new BindGroupEntry { Binding = 1, TextureView = viewPtr };

                    var bgDesc = new BindGroupDescriptor { Layout = _textureBindGroupLayout, EntryCount = 2, Entries = textureEntries };
                    var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);
                    if (bg == null)
                    {
                        System.Console.WriteLine($"[Compositor Error] Failed to create BindGroup for TextureView {(nint)viewPtr}");
                    }
                    cachedBg = new CachedBindGroup((nint)bg, _frameNumber);
                    _persistentTextureBindGroups[cacheKey] = cachedBg;
                }
                else
                {
                    cachedBg.LastUsedFrame = _frameNumber;
                }

                var bindGroup = (BindGroup*)cachedBg.BindGroupPtr;
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.StaticDxf && dc.StaticBuffer != null)
            {
                DrawStaticDxfBuffer(pass, dc.StaticBuffer, isOffscreen: false);
                currentType = DrawCallType.StaticDxf;
            }
            else if (dc.Type == DrawCallType.ChartLine)
            {
                RenderChartLine(pass, dc, isOffscreen: false);
                currentType = DrawCallType.ChartLine;
            }
            else if (dc.Type == DrawCallType.ChartScatter)
            {
                RenderChartScatter(pass, dc, isOffscreen: false);
                currentType = DrawCallType.ChartScatter;
            }
        }

        _context.Wgpu.RenderPassEncoderEnd(pass);
        _context.Wgpu.RenderPassEncoderRelease(pass);

        // Submit to queue
        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compositor Command Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        _frameNumber++;
        _totalTime += 1f / 60f;
        EvictUnusedBindGroups();
        SweepUnusedEffectTextures(root, externalLayers, activeToolTip);

        passSw.Stop();
        totalSw.Stop();

        Metrics = new CompositorMetrics
        {
            FrameTimeMs = totalSw.Elapsed.TotalMilliseconds,
            VisualTreeCompileTimeMs = compileSw.Elapsed.TotalMilliseconds,
            GpuUploadTimeMs = uploadSw.Elapsed.TotalMilliseconds,
            RenderPassTimeMs = passSw.Elapsed.TotalMilliseconds,
            DrawCallsCount = _drawCalls.Count,
            VectorVerticesCount = _vectorVerticesList.Count,
            TextVerticesCount = _textVerticesList.Count,
            PathAtlasCachedCount = _pathAtlas.CachedPathCount
        };
    }

    private void EvictUnusedBindGroups()
    {
        List<TextureCacheKey>? keysToRemove = null;
        foreach (var kvp in _persistentTextureBindGroups)
        {
            if (_frameNumber - kvp.Value.LastUsedFrame > 60)
            {
                if (kvp.Value.BindGroupPtr != 0)
                {
                    _context.Wgpu.BindGroupRelease((BindGroup*)kvp.Value.BindGroupPtr);
                }
                keysToRemove ??= new List<TextureCacheKey>();
                keysToRemove.Add(kvp.Key);
            }
        }
        if (keysToRemove != null)
        {
            foreach (var key in keysToRemove)
            {
                _persistentTextureBindGroups.Remove(key);
            }
        }
    }

    private bool IsAttachedToAnyActiveRoot(Visual fe, Visual mainRoot, IReadOnlyList<Visual>? externalLayers, Visual? activeToolTip)
    {
        var node = fe;
        while (node != null)
        {
            if (node == mainRoot) return true;
            if (node == activeToolTip) return true;
            if (externalLayers != null)
            {
                for (int i = 0; i < externalLayers.Count; i++)
                {
                    if (node == externalLayers[i]) return true;
                }
            }
            node = node.Parent;
        }
        return false;
    }

    private void SweepUnusedEffectTextures(Visual mainRoot, IReadOnlyList<Visual>? externalLayers, Visual? activeToolTip)
    {
        if (_frameNumber % 60 == 0 && _effectTextures.Count > 0)
        {
            List<Visual>? detached = null;
            foreach (var fe in _effectTextures.Keys)
            {
                if (!IsAttachedToAnyActiveRoot(fe, mainRoot, externalLayers, activeToolTip))
                {
                    detached ??= new List<Visual>();
                    detached.Add(fe);
                }
            }
            if (detached != null)
            {
                foreach (var fe in detached)
                {
                    if (_effectTextures.Remove(fe, out var textures))
                    {
                        textures.Source.Dispose();
                        textures.Temp.Dispose();
                        textures.Destination.Dispose();
                    }
                }
            }
        }
    }

    private void PushClipRect(Rect localClip, Matrix4x4 transform)
    {
        var vTopLeft = Vector2.Transform(new Vector2(localClip.X, localClip.Y), transform);
        var vBottomRight = Vector2.Transform(new Vector2(localClip.X + localClip.Width, localClip.Y + localClip.Height), transform);
        
        float x1 = Math.Min(vTopLeft.X, vBottomRight.X);
        float y1 = Math.Min(vTopLeft.Y, vBottomRight.Y);
        float x2 = Math.Max(vTopLeft.X, vBottomRight.X);
        float y2 = Math.Max(vTopLeft.Y, vBottomRight.Y);
        
        var screenClip = new Rect(x1, y1, x2 - x1, y2 - y1);

        if (_activeClipRect.HasValue)
        {
            float cx1 = Math.Max(_activeClipRect.Value.X, screenClip.X);
            float cy1 = Math.Max(_activeClipRect.Value.Y, screenClip.Y);
            float cx2 = Math.Min(_activeClipRect.Value.X + _activeClipRect.Value.Width, screenClip.X + screenClip.Width);
            float cy2 = Math.Min(_activeClipRect.Value.Y + _activeClipRect.Value.Height, screenClip.Y + screenClip.Height);
            _activeClipRect = new Rect(cx1, cy1, Math.Max(0f, cx2 - cx1), Math.Max(0f, cy2 - cy1));
        }
        else
        {
            _activeClipRect = screenClip;
        }
        _clipStack.Push(_activeClipRect.Value);
    }

    private void PopClipRect()
    {
        if (_clipStack.Count > 0)
        {
            _clipStack.Pop();
            _activeClipRect = _clipStack.Count > 0 ? _clipStack.Peek() : null;
        }
    }

    private void PushOpacityValue(float opacity)
    {
        _activeOpacity *= opacity;
        _opacityStack.Push(opacity);
    }

    private void PopOpacityValue()
    {
        if (_opacityStack.Count > 0)
        {
            float op = _opacityStack.Pop();
            if (op > 0f) _activeOpacity /= op;
        }
        else
        {
            _activeOpacity = 1.0f;
        }
    }

    [ThreadStatic]
    private static List<DrawingContext>? _contextPool;
    
    [ThreadStatic]
    private static int _poolIndex;

    private static DrawingContext GetDrawingContext()
    {
        _contextPool ??= new List<DrawingContext>();
        if (_poolIndex >= _contextPool.Count)
        {
            _contextPool.Add(new DrawingContext());
        }
        var ctx = _contextPool[_poolIndex++];
        ctx.Clear();
        return ctx;
    }

    private static void ReleaseDrawingContext()
    {
        _poolIndex--;
    }

    private void CompileVisualTree(Visual node, Matrix4x4 parentTransform)
    {
        if (node.Opacity <= 0.0001f || _activeOpacity <= 0.0001f)
        {
            node.IsDirty = false;
            return;
        }

        if (node.Effect != null && !_elementsRenderingEffects.Contains(node))
        {
            ApplyAndDrawEffect(node, parentTransform);
            return;
        }

        if (node.CacheAsLayer && IsCacheAsLayerEnabled && !_elementsRenderingLayers.Contains(node))
        {
            ApplyAndDrawLayer(node, parentTransform);
            return;
        }

        // 1. Calculate global transform
        var localTransform = node.GetLocalTransform();
        var globalTransform = localTransform * parentTransform;

        bool pushedClip = false;
        if (node.ClipBounds.HasValue)
        {
            PushClipRect(node.ClipBounds.Value, globalTransform);
            pushedClip = true;
        }

        bool pushedOpacity = false;
        if (node.Opacity < 1.0f)
        {
            PushOpacityValue(node.Opacity);
            pushedOpacity = true;
        }

        // 2. Playback recorded commands
        var ctx = GetDrawingContext();
        node.OnRender(ctx);

        foreach (var cmd in ctx.Commands)
        {
            int vectorStart = _vectorVerticesList.Count;
            int textStart = _textVerticesList.Count;
            var activeTransform = cmd.UseGpuTransforms ? Matrix4x4.Identity : globalTransform;

            bool savedUseGpuTransformsActive = _useGpuTransformsActive;
            Matrix4x4 savedCameraViewMatrix = _cameraViewMatrix;

            if (cmd.UseGpuTransforms)
            {
                _useGpuTransformsActive = true;
                _cameraViewMatrix = cmd.CameraView * globalTransform;
                _hasGpuTransformsInFrame = true;
                _gpuTransformsCameraView = cmd.CameraView * globalTransform;
            }

            switch (cmd.Type)
            {
                case RenderCommandType.DrawRect:
                    CompileRectCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawPath:
                    CompilePathCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawHatch:
                    CompileHatchCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawAcisSolid:
                    CompileAcisCommand(ctx, cmd, activeTransform);
                    break;
                case RenderCommandType.DrawText:
                    CompileTextCommand(cmd, node as ITextLayoutProvider, activeTransform);
                    break;
                case RenderCommandType.DrawTexture:
                    CompileTextureCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.PushClip:
                    PushClipRect(cmd.Rect, globalTransform);
                    break;
                case RenderCommandType.PopClip:
                    PopClipRect();
                    break;
                case RenderCommandType.PushOpacity:
                    PushOpacityValue(cmd.FontSize);
                    break;
                case RenderCommandType.PopOpacity:
                    PopOpacityValue();
                    break;
                case RenderCommandType.DrawLine:
                    CompileLineCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawLine3D:
                    CompileLine3DCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawEllipse:
                    CompileEllipseCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawCircle:
                    CompileCircleCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawRoundedRect:
                    CompileRoundedRectCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawBezier:
                    CompileBezierCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawCubicBezier:
                    CompileCubicBezierCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawPolyline:
                    CompilePolylineCommand(ctx, cmd, activeTransform);
                    break;
                case RenderCommandType.DrawSpline:
                    CompileSplineCommand(ctx, cmd, activeTransform);
                    break;
                case RenderCommandType.FillTriangle:
                    CompileFillTriangleCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.FillQuad:
                    CompileFillQuadCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawStaticDxf:
                    CommitPendingDrawCalls();
                    _drawCalls.Add(new CompositorDrawCall
                    {
                        Type = DrawCallType.StaticDxf,
                        StaticBuffer = cmd.StaticBuffer
                    });
                    _pendingVectorStart = (uint)_vectorIndicesList.Count;
                    _pendingTextStart = (uint)_textIndicesList.Count;
                    break;
                case RenderCommandType.DrawGpuLineSeries:
                    CompileGpuLineSeriesCommand(ctx, cmd, activeTransform);
                    break;
                case RenderCommandType.DrawGpuScatterSeries:
                    CompileGpuScatterSeriesCommand(ctx, cmd, activeTransform);
                    break;
                case RenderCommandType.DrawPicture:
                    CompilePicture(ctx, cmd.Picture, activeTransform);
                    break;
            }

            if (cmd.UseGpuTransforms)
            {
                for (int i = vectorStart; i < _vectorVerticesList.Count; i++)
                {
                    var v = _vectorVerticesList[i];
                    v.ShapeType += 100f;
                    _vectorVerticesList[i] = v;
                }
                for (int i = textStart; i < _textVerticesList.Count; i++)
                {
                    var v = _textVerticesList[i];
                    v.ShapeType += 100f;
                    _textVerticesList[i] = v;
                }
            }

            _useGpuTransformsActive = savedUseGpuTransformsActive;
            _cameraViewMatrix = savedCameraViewMatrix;
        }

        ReleaseDrawingContext();

        if (node is ContainerVisual container)
        {
            var children = container.Children;
            int count = children.Count;
            for (int i = 0; i < count; i++)
            {
                CompileVisualTree(children[i], globalTransform);
            }
        }

        if (pushedOpacity)
        {
            PopOpacityValue();
        }

        if (pushedClip)
        {
            PopClipRect();
        }

        node.IsDirty = false;
    }

    private void CompilePicture(DrawingContext parentContext, GpuPicture? picture, Matrix4x4 globalTransform)
    {
        if (picture == null) return;
        foreach (var cmd in picture.Commands)
        {
            int vectorStart = _vectorVerticesList.Count;
            int textStart = _textVerticesList.Count;
            var activeTransform = cmd.UseGpuTransforms ? Matrix4x4.Identity : globalTransform;
            
            bool savedUseGpuTransformsActive = _useGpuTransformsActive;
            Matrix4x4 savedCameraViewMatrix = _cameraViewMatrix;

            if (cmd.UseGpuTransforms)
            {
                _useGpuTransformsActive = true;
                _cameraViewMatrix = cmd.CameraView * globalTransform;
                _hasGpuTransformsInFrame = true;
                _gpuTransformsCameraView = cmd.CameraView * globalTransform;
            }

            switch (cmd.Type)
            {
                case RenderCommandType.DrawRect:
                    CompileRectCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawPath:
                    CompilePathCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawHatch:
                    CompileHatchCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawAcisSolid:
                    CompileAcisCommand(picture, cmd, activeTransform);
                    break;
                case RenderCommandType.DrawText:
                    CompileTextCommand(cmd, null, activeTransform);
                    break;
                case RenderCommandType.DrawTexture:
                    CompileTextureCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.PushClip:
                    PushClipRect(cmd.Rect, globalTransform);
                    break;
                case RenderCommandType.PopClip:
                    PopClipRect();
                    break;
                case RenderCommandType.PushOpacity:
                    PushOpacityValue(cmd.FontSize);
                    break;
                case RenderCommandType.PopOpacity:
                    PopOpacityValue();
                    break;
                case RenderCommandType.DrawLine:
                    CompileLineCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawLine3D:
                    CompileLine3DCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawEllipse:
                    CompileEllipseCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawCircle:
                    CompileCircleCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawRoundedRect:
                    CompileRoundedRectCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawBezier:
                    CompileBezierCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawCubicBezier:
                    CompileCubicBezierCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawPolyline:
                    CompilePolylineCommand(picture, cmd, activeTransform);
                    break;
                case RenderCommandType.DrawSpline:
                    CompileSplineCommand(picture, cmd, activeTransform);
                    break;
                case RenderCommandType.FillTriangle:
                    CompileFillTriangleCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.FillQuad:
                    CompileFillQuadCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawGpuLineSeries:
                    CompileGpuLineSeriesCommand(picture, cmd, activeTransform);
                    break;
                case RenderCommandType.DrawGpuScatterSeries:
                    CompileGpuScatterSeriesCommand(picture, cmd, activeTransform);
                    break;
                case RenderCommandType.DrawPicture:
                    CompilePicture(parentContext, cmd.Picture, activeTransform);
                    break;
            }

            if (cmd.UseGpuTransforms)
            {
                for (int i = vectorStart; i < _vectorVerticesList.Count; i++)
                {
                    var v = _vectorVerticesList[i];
                    v.ShapeType += 100f;
                    _vectorVerticesList[i] = v;
                }
                for (int i = textStart; i < _textVerticesList.Count; i++)
                {
                    var v = _textVerticesList[i];
                    v.ShapeType += 100f;
                    _textVerticesList[i] = v;
                }
            }

            _useGpuTransformsActive = savedUseGpuTransformsActive;
            _cameraViewMatrix = savedCameraViewMatrix;
        }
    }

    private void CompileRectCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        int startIndex = _vectorVerticesList.Count;
        var r = cmd.Rect;
        float wHalf = r.Width / 2f;
        float hHalf = r.Height / 2f;
        var shapeSize = new Vector2(r.Width, r.Height);

        if (cmd.Brush != null)
        {
            float pad = 1.5f;
            var f0_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y - pad), transform);
            var f1_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y - pad), transform);
            var f2_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y + r.Height + pad), transform);
            var f3_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y + r.Height + pad), transform);

            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(r.X + wHalf, r.Y + hHalf, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(f0_pos, solidColor, new Vector2(-wHalf - pad, -hHalf - pad), bIdx, shapeSize, 0f, 0f, 0f);
            vertexSpan[1] = new VectorVertex(f1_pos, solidColor, new Vector2(wHalf + pad, -hHalf - pad), bIdx, shapeSize, 0f, 0f, 0f);
            vertexSpan[2] = new VectorVertex(f2_pos, solidColor, new Vector2(wHalf + pad, hHalf + pad), bIdx, shapeSize, 0f, 0f, 0f);
            vertexSpan[3] = new VectorVertex(f3_pos, solidColor, new Vector2(-wHalf - pad, hHalf + pad), bIdx, shapeSize, 0f, 0f, 0f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (cmd.Pen != null)
        {
            float pad = cmd.Pen.Thickness / 2f + 1.5f;
            var p0_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y - pad), transform);
            var p1_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y - pad), transform);
            var p2_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y + r.Height + pad), transform);
            var p3_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y + r.Height + pad), transform);

            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solidPen) ? solidPen.Color : new Vector4(r.X + wHalf, r.Y + hHalf, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, new Vector2(-wHalf - pad, -hHalf - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f);
            vertexSpan[1] = new VectorVertex(p1_pos, penSolidColor, new Vector2(wHalf + pad, -hHalf - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f);
            vertexSpan[2] = new VectorVertex(p2_pos, penSolidColor, new Vector2(wHalf + pad, hHalf + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f);
            vertexSpan[3] = new VectorVertex(p3_pos, penSolidColor, new Vector2(-wHalf - pad, hHalf + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 0f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }


    private void CompilePathCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Path == null) return;
        int startIndex = _vectorVerticesList.Count;

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var brush = cmd.Brush as SolidColorBrush;
            var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);

            // Extract scale factor from transform
            var scaleX = new Vector2(transform.M11, transform.M12).Length();
            var scaleY = new Vector2(transform.M21, transform.M22).Length();
            float scale = Math.Max(scaleX, scaleY);
            if (scale < 0.0001f) scale = 1f;

            var info = _pathAtlas.GetOrCreatePath(cmd.Path, scale);
            if (info.Width > 0 && info.Height > 0)
            {
                float unscaledMinX = info.MinX / scale;
                float unscaledMinY = info.MinY / scale;
                float unscaledWidth = info.Width / scale;
                float unscaledHeight = info.Height / scale;

                var v0 = Vector2.Transform(new Vector2(unscaledMinX, unscaledMinY), transform);
                var v1 = Vector2.Transform(new Vector2(unscaledMinX + unscaledWidth, unscaledMinY), transform);
                var v2 = Vector2.Transform(new Vector2(unscaledMinX + unscaledWidth, unscaledMinY + unscaledHeight), transform);
                var v3 = Vector2.Transform(new Vector2(unscaledMinX, unscaledMinY + unscaledHeight), transform);

                var uv0 = new Vector2(info.TexCoordMin.X, info.TexCoordMin.Y);
                var uv1 = new Vector2(info.TexCoordMax.X, info.TexCoordMin.Y);
                var uv2 = new Vector2(info.TexCoordMax.X, info.TexCoordMax.Y);
                var uv3 = new Vector2(info.TexCoordMin.X, info.TexCoordMax.Y);

                var cp0 = new Vector2(unscaledMinX, unscaledMinY);
                var cp1 = new Vector2(unscaledMinX + unscaledWidth, unscaledMinY);
                var cp2 = new Vector2(unscaledMinX + unscaledWidth, unscaledMinY + unscaledHeight);
                var cp3 = new Vector2(unscaledMinX, unscaledMinY + unscaledHeight);

                if (_activeClipRect.HasValue && !_useGpuTransformsActive)
                {
                    float rx1 = v0.X;
                    float ry1 = v0.Y;
                    float rx2 = v2.X;
                    float ry2 = v2.Y;

                    float cx1 = Math.Max(rx1, _activeClipRect.Value.X);
                    float cy1 = Math.Max(ry1, _activeClipRect.Value.Y);
                    float cx2 = Math.Min(rx2, _activeClipRect.Value.X + _activeClipRect.Value.Width);
                    float cy2 = Math.Min(ry2, _activeClipRect.Value.Y + _activeClipRect.Value.Height);

                    if (cx2 > cx1 && cy2 > cy1)
                    {
                        float dx = rx2 - rx1;
                        float dy = ry2 - ry1;

                        uv0 = new Vector2(
                            info.TexCoordMin.X + (cx1 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy1 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );
                        uv1 = new Vector2(
                            info.TexCoordMin.X + (cx2 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy1 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );
                        uv2 = new Vector2(
                            info.TexCoordMin.X + (cx2 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy2 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );
                        uv3 = new Vector2(
                            info.TexCoordMin.X + (cx1 - rx1) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy2 - ry1) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );

                        float cp0_x = dx > 0.0001f ? unscaledMinX + (cx1 - rx1) / dx * unscaledWidth : unscaledMinX;
                        float cp0_y = dy > 0.0001f ? unscaledMinY + (cy1 - ry1) / dy * unscaledHeight : unscaledMinY;
                        float cp2_x = dx > 0.0001f ? unscaledMinX + (cx2 - rx1) / dx * unscaledWidth : unscaledMinX + unscaledWidth;
                        float cp2_y = dy > 0.0001f ? unscaledMinY + (cy2 - ry1) / dy * unscaledHeight : unscaledMinY + unscaledHeight;

                        var cp0_clip = new Vector2(cp0_x, cp0_y);
                        var cp1_clip = new Vector2(cp2_x, cp0_y);
                        var cp2_clip = new Vector2(cp2_x, cp2_y);
                        var cp3_clip = new Vector2(cp0_x, cp2_y);

                        v0 = new Vector2(cx1, cy1);
                        v1 = new Vector2(cx2, cy1);
                        v2 = new Vector2(cx2, cy2);
                        v3 = new Vector2(cx1, cy2);

                        uint idxStart = (uint)_vectorVerticesList.Count;

                        int originalVertexCount = _vectorVerticesList.Count;
                        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
                        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

                        vertexSpan[0] = new VectorVertex(v0, color, uv0, bIdx, shapeSize: cp0_clip, shapeType: 4f);
                        vertexSpan[1] = new VectorVertex(v1, color, uv1, bIdx, shapeSize: cp1_clip, shapeType: 4f);
                        vertexSpan[2] = new VectorVertex(v2, color, uv2, bIdx, shapeSize: cp2_clip, shapeType: 4f);
                        vertexSpan[3] = new VectorVertex(v3, color, uv3, bIdx, shapeSize: cp3_clip, shapeType: 4f);

                        int originalIndexCount = _vectorIndicesList.Count;
                        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
                        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

                        indexSpan[0] = idxStart;
                        indexSpan[1] = idxStart + 1;
                        indexSpan[2] = idxStart + 2;
                        indexSpan[3] = idxStart;
                        indexSpan[4] = idxStart + 2;
                        indexSpan[5] = idxStart + 3;
                    }
                }
                else
                {
                    uint idxStart = (uint)_vectorVerticesList.Count;

                    int originalVertexCount = _vectorVerticesList.Count;
                    CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
                    var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

                    vertexSpan[0] = new VectorVertex(v0, color, uv0, bIdx, shapeSize: cp0, shapeType: 4f);
                    vertexSpan[1] = new VectorVertex(v1, color, uv1, bIdx, shapeSize: cp1, shapeType: 4f);
                    vertexSpan[2] = new VectorVertex(v2, color, uv2, bIdx, shapeSize: cp2, shapeType: 4f);
                    vertexSpan[3] = new VectorVertex(v3, color, uv3, bIdx, shapeSize: cp3, shapeType: 4f);

                    int originalIndexCount = _vectorIndicesList.Count;
                    CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
                    var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

                    indexSpan[0] = idxStart;
                    indexSpan[1] = idxStart + 1;
                    indexSpan[2] = idxStart + 2;
                    indexSpan[3] = idxStart;
                    indexSpan[4] = idxStart + 2;
                    indexSpan[5] = idxStart + 3;
                }
            }
        }

        if (cmd.Pen != null)
        {
            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
            float thickness = cmd.Pen.Thickness;

            int maxVertices = 0;
            int maxIndices = 0;
            foreach (var figure in cmd.Path.Figures)
            {
                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment)
                    {
                        maxVertices += 4;
                        maxIndices += 6;
                    }
                    else if (segment is QuadraticBezierSegment)
                    {
                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        maxVertices += 2 * (N + 1);
                        maxIndices += 6 * N;
                    }
                    else if (segment is CubicBezierSegment)
                    {
                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        maxVertices += 2 * (N + 1);
                        maxIndices += 6 * N;
                    }
                }
                if (figure.IsClosed)
                {
                    maxVertices += 4;
                    maxIndices += 6;
                }
            }

            int vertexStart = _vectorVerticesList.Count;
            int indexStart = _vectorIndicesList.Count;

            CollectionsMarshal.SetCount(_vectorVerticesList, vertexStart + maxVertices);
            CollectionsMarshal.SetCount(_vectorIndicesList, indexStart + maxIndices);

            var verticesSpan = CollectionsMarshal.AsSpan(_vectorVerticesList);
            var indicesSpan = CollectionsMarshal.AsSpan(_vectorIndicesList);

            int currentVertexCount = vertexStart;
            int currentIndexCount = indexStart;

            foreach (var figure in cmd.Path.Figures)
            {
                var currentPoint = figure.StartPoint;

                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line)
                    {
                        var p0_trans = Vector2.Transform(currentPoint, transform);
                        var p1_trans = Vector2.Transform(line.Point, transform);

                        uint idxStart = (uint)currentVertexCount;

                        verticesSpan[currentVertexCount++] = new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f);
                        verticesSpan[currentVertexCount++] = new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f);
                        verticesSpan[currentVertexCount++] = new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f);
                        verticesSpan[currentVertexCount++] = new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f);

                        indicesSpan[currentIndexCount++] = idxStart;
                        indicesSpan[currentIndexCount++] = idxStart + 1;
                        indicesSpan[currentIndexCount++] = idxStart + 2;

                        indicesSpan[currentIndexCount++] = idxStart + 1;
                        indicesSpan[currentIndexCount++] = idxStart + 3;
                        indicesSpan[currentIndexCount++] = idxStart + 2;

                        currentPoint = line.Point;
                    }
                    else if (segment is QuadraticBezierSegment quad)
                    {
                        var p0_trans = Vector2.Transform(currentPoint, transform);
                        var p1_trans = Vector2.Transform(quad.ControlPoint, transform);
                        var p2_trans = Vector2.Transform(quad.Point, transform);

                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        uint idxStart = (uint)currentVertexCount;

                        var baseVertex = new VectorVertex(p0_trans, Vector4.Zero, p1_trans, penBrushIdx, p2_trans, idxStart, thickness, 5f);
                        int vertexToAdd = 2 * (N + 1);
                        verticesSpan.Slice(currentVertexCount, vertexToAdd).Fill(baseVertex);
                        currentVertexCount += vertexToAdd;

                        for (int i = 0; i < N; i++)
                        {
                            uint currentLeft = (uint)(idxStart + 2 * i);
                            uint currentRight = (uint)(idxStart + 2 * i + 1);
                            uint nextLeft = (uint)(idxStart + 2 * i + 2);
                            uint nextRight = (uint)(idxStart + 2 * i + 3);

                            indicesSpan[currentIndexCount++] = currentLeft;
                            indicesSpan[currentIndexCount++] = currentRight;
                            indicesSpan[currentIndexCount++] = nextLeft;

                            indicesSpan[currentIndexCount++] = currentRight;
                            indicesSpan[currentIndexCount++] = nextRight;
                            indicesSpan[currentIndexCount++] = nextLeft;
                        }

                        currentPoint = quad.Point;
                    }
                    else if (segment is CubicBezierSegment cubic)
                    {
                        var p0_trans = Vector2.Transform(currentPoint, transform);
                        var p1_trans = Vector2.Transform(cubic.ControlPoint1, transform);
                        var p2_trans = Vector2.Transform(cubic.ControlPoint2, transform);
                        var p3_trans = Vector2.Transform(cubic.Point, transform);

                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        uint idxStart = (uint)currentVertexCount;

                        var baseVertex = new VectorVertex(p0_trans, new Vector4(p3_trans.X, p3_trans.Y, 0f, 0f), p1_trans, penBrushIdx, p2_trans, idxStart, thickness, 6f);
                        int vertexToAdd = 2 * (N + 1);
                        verticesSpan.Slice(currentVertexCount, vertexToAdd).Fill(baseVertex);
                        currentVertexCount += vertexToAdd;

                        for (int i = 0; i < N; i++)
                        {
                            uint currentLeft = (uint)(idxStart + 2 * i);
                            uint currentRight = (uint)(idxStart + 2 * i + 1);
                            uint nextLeft = (uint)(idxStart + 2 * i + 2);
                            uint nextRight = (uint)(idxStart + 2 * i + 3);

                            indicesSpan[currentIndexCount++] = currentLeft;
                            indicesSpan[currentIndexCount++] = currentRight;
                            indicesSpan[currentIndexCount++] = nextLeft;

                            indicesSpan[currentIndexCount++] = currentRight;
                            indicesSpan[currentIndexCount++] = nextRight;
                            indicesSpan[currentIndexCount++] = nextLeft;
                        }

                        currentPoint = cubic.Point;
                    }
                }

                if (figure.IsClosed && currentPoint != figure.StartPoint)
                {
                    var p0_trans = Vector2.Transform(currentPoint, transform);
                    var p1_trans = Vector2.Transform(figure.StartPoint, transform);

                    uint idxStart = (uint)currentVertexCount;

                    verticesSpan[currentVertexCount++] = new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f);
                    verticesSpan[currentVertexCount++] = new VectorVertex(p0_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f);
                    verticesSpan[currentVertexCount++] = new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, 1f, thickness, 3f);
                    verticesSpan[currentVertexCount++] = new VectorVertex(p1_trans, penSolidColor, p0_trans, penBrushIdx, p1_trans, -1f, thickness, 3f);

                    indicesSpan[currentIndexCount++] = idxStart;
                    indicesSpan[currentIndexCount++] = idxStart + 1;
                    indicesSpan[currentIndexCount++] = idxStart + 2;

                    indicesSpan[currentIndexCount++] = idxStart + 1;
                    indicesSpan[currentIndexCount++] = idxStart + 3;
                    indicesSpan[currentIndexCount++] = idxStart + 2;
                }
            }

            CollectionsMarshal.SetCount(_vectorVerticesList, currentVertexCount);
            CollectionsMarshal.SetCount(_vectorIndicesList, currentIndexCount);

            if (_activeClipRect.HasValue)
            {
                var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
                for (int i = vertexStart; i < vertices.Length; i++)
                {
                    var v = vertices[i];
                    v.Position = ClampToClip(v.Position);
                    vertices[i] = v;
                }
            }
        }
    }

    private void CompileHatchCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Path == null) return;
        int startIndex = _vectorVerticesList.Count;

        if (cmd.Brush != null)
        {
            float bIdx = RegisterBrush(cmd.Brush);

            uint startSegment = (uint)_hatchSegmentsList.Count;
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            void UpdateBounds(Vector2 p)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }

            foreach (var figure in cmd.Path.Figures)
            {
                if (figure.Segments.Count == 0) continue;

                Vector2 currentPoint = figure.StartPoint;
                UpdateBounds(currentPoint);

                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line)
                    {
                        _hatchSegmentsList.Add(new GpuHatchSegment
                        {
                            P0 = currentPoint,
                            P1 = line.Point,
                            SegmentType = 0
                        });
                        UpdateBounds(line.Point);
                        currentPoint = line.Point;
                    }
                    else if (segment is QuadraticBezierSegment quad)
                    {
                        _hatchSegmentsList.Add(new GpuHatchSegment
                        {
                            P0 = currentPoint,
                            P1 = quad.ControlPoint,
                            P2 = quad.Point,
                            SegmentType = 1
                        });
                        UpdateBounds(quad.ControlPoint);
                        UpdateBounds(quad.Point);
                        currentPoint = quad.Point;
                    }
                    else if (segment is CubicBezierSegment cubic)
                    {
                        _hatchSegmentsList.Add(new GpuHatchSegment
                        {
                            P0 = currentPoint,
                            P1 = cubic.ControlPoint1,
                            P2 = cubic.ControlPoint2,
                            P3 = cubic.Point,
                            SegmentType = 2
                        });
                        UpdateBounds(cubic.ControlPoint1);
                        UpdateBounds(cubic.ControlPoint2);
                        UpdateBounds(cubic.Point);
                        currentPoint = cubic.Point;
                    }
                }

                if (figure.IsClosed && currentPoint != figure.StartPoint)
                {
                    _hatchSegmentsList.Add(new GpuHatchSegment
                    {
                        P0 = currentPoint,
                        P1 = figure.StartPoint,
                        SegmentType = 0
                    });
                    UpdateBounds(figure.StartPoint);
                }
            }

            uint segmentCount = (uint)_hatchSegmentsList.Count - startSegment;
            if (segmentCount == 0) return;

            uint hatchRecordIndex = (uint)_hatchRecordsList.Count;
            _hatchRecordsList.Add(new GpuHatchRecord
            {
                StartSegment = startSegment,
                SegmentCount = segmentCount,
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY
            });

            var v0 = Vector2.Transform(new Vector2(minX, minY), transform);
            var v1 = Vector2.Transform(new Vector2(maxX, minY), transform);
            var v2 = Vector2.Transform(new Vector2(maxX, maxY), transform);
            var v3 = Vector2.Transform(new Vector2(minX, maxY), transform);

            var c0 = new Vector4(minX, minY, hatchRecordIndex, 0f);
            var c1 = new Vector4(maxX, minY, hatchRecordIndex, 0f);
            var c2 = new Vector4(maxX, maxY, hatchRecordIndex, 0f);
            var c3 = new Vector4(minX, maxY, hatchRecordIndex, 0f);

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(v0, c0, new Vector2(0f, 0f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);
            vertexSpan[1] = new VectorVertex(v1, c1, new Vector2(1f, 0f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);
            vertexSpan[2] = new VectorVertex(v2, c2, new Vector2(1f, 1f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);
            vertexSpan[3] = new VectorVertex(v3, c3, new Vector2(0f, 1f), bIdx, shapeSize: new Vector2(maxX - minX, maxY - minY), shapeType: 9f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = (uint)startIndex;
            indexSpan[1] = (uint)(startIndex + 1);
            indexSpan[2] = (uint)(startIndex + 2);
            indexSpan[3] = (uint)startIndex;
            indexSpan[4] = (uint)(startIndex + 2);
            indexSpan[5] = (uint)(startIndex + 3);
        }
    }

    private void CompileAcisCommand(IRenderDataProvider? provider, RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Pen == null) return;

        ReadOnlySpan<Line3D> edgesSpan = provider != null ? 
            provider.GetLines3D(cmd.Line3DBufferOffset, cmd.Line3DBufferCount) : 
            CollectionsMarshal.AsSpan(cmd.Edges3D ?? new List<Line3D>());

        if (edgesSpan.IsEmpty) return;

        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
        float thickness = cmd.Pen.Thickness;

        uint startEdge = (uint)_acisEdgesList.Count;
        uint edgeCount = (uint)edgesSpan.Length;

        foreach (var edge in edgesSpan)
        {
            _acisEdgesList.Add(new GpuAcisEdge
            {
                P0 = new Vector4(edge.Start, 0f),
                P1 = new Vector4(edge.End, 0f)
            });
        }

        uint acisRecordIndex = (uint)_acisRecordsList.Count;
        _acisRecordsList.Add(new GpuAcisRecord
        {
            Transform = cmd.Transform * transform,
            Color = penSolidColor,
            StartEdge = startEdge,
            EdgeCount = edgeCount,
            PenThickness = thickness,
            Opacity = cmd.Pen.Brush.Opacity * _activeOpacity
        });

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + (int)edgeCount * 4);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, (int)edgeCount * 4);

        int originalIndexCount = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + (int)edgeCount * 6);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, (int)edgeCount * 6);

        for (int i = 0; i < (int)edgeCount; i++)
        {
            uint edgeIdx = startEdge + (uint)i;
            int vOffset = i * 4;
            int iOffset = i * 6;
            uint vStart = (uint)originalVertexCount + (uint)vOffset;

            vertexSpan[vOffset + 0] = new VectorVertex(new Vector2(edgeIdx, 0f), penSolidColor, new Vector2(0f, 0f), penBrushIdx, shapeSize: new Vector2(acisRecordIndex, 0f), shapeType: 10f);
            vertexSpan[vOffset + 1] = new VectorVertex(new Vector2(edgeIdx, 1f), penSolidColor, new Vector2(0f, 0f), penBrushIdx, shapeSize: new Vector2(acisRecordIndex, 0f), shapeType: 10f);
            vertexSpan[vOffset + 2] = new VectorVertex(new Vector2(edgeIdx, 2f), penSolidColor, new Vector2(0f, 0f), penBrushIdx, shapeSize: new Vector2(acisRecordIndex, 0f), shapeType: 10f);
            vertexSpan[vOffset + 3] = new VectorVertex(new Vector2(edgeIdx, 3f), penSolidColor, new Vector2(0f, 0f), penBrushIdx, shapeSize: new Vector2(acisRecordIndex, 0f), shapeType: 10f);

            indexSpan[iOffset + 0] = vStart;
            indexSpan[iOffset + 1] = vStart + 1;
            indexSpan[iOffset + 2] = vStart + 2;
            indexSpan[iOffset + 3] = vStart + 1;
            indexSpan[iOffset + 4] = vStart + 3;
            indexSpan[iOffset + 5] = vStart + 2;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = originalVertexCount; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileLineCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Pen == null) return;
        int startIndex = _vectorVerticesList.Count;
        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

        var p0_pos = Vector2.Transform(cmd.Position, transform);
        var p1_pos = Vector2.Transform(cmd.Position2, transform);
        float thickness = cmd.Pen.Thickness;

        uint idxStart = (uint)startIndex;

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 1f, thickness, 3f);
        vertexSpan[1] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -1f, thickness, 3f);
        vertexSpan[2] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 2f, thickness, 3f);
        vertexSpan[3] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -2f, thickness, 3f);

        int originalIndexCount = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

        indexSpan[0] = idxStart;
        indexSpan[1] = idxStart + 1;
        indexSpan[2] = idxStart + 2;
        indexSpan[3] = idxStart + 1;
        indexSpan[4] = idxStart + 3;
        indexSpan[5] = idxStart + 2;

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileLine3DCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Pen == null) return;
        int startIndex = _vectorVerticesList.Count;
        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

        var p0_trans = Vector3.Transform(cmd.Position3D1, transform);
        var p1_trans = Vector3.Transform(cmd.Position3D2, transform);
        
        var p0_xy = new Vector2(p0_trans.X, p0_trans.Y);
        var p1_xy = new Vector2(p1_trans.X, p1_trans.Y);
        float thickness = cmd.Pen.Thickness;

        uint idxStart = (uint)startIndex;

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(p0_xy, penSolidColor, new Vector2(p0_trans.Z, 0f), penBrushIdx, p1_xy, 1f, thickness, 8f);
        vertexSpan[1] = new VectorVertex(p0_xy, penSolidColor, new Vector2(p0_trans.Z, 0f), penBrushIdx, p1_xy, -1f, thickness, 8f);
        vertexSpan[2] = new VectorVertex(p1_xy, penSolidColor, new Vector2(p1_trans.Z, 0f), penBrushIdx, p1_xy, 2f, thickness, 8f);
        vertexSpan[3] = new VectorVertex(p1_xy, penSolidColor, new Vector2(p1_trans.Z, 0f), penBrushIdx, p1_xy, -2f, thickness, 8f);

        int originalIndexCount = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

        indexSpan[0] = idxStart;
        indexSpan[1] = idxStart + 1;
        indexSpan[2] = idxStart + 2;
        indexSpan[3] = idxStart + 1;
        indexSpan[4] = idxStart + 3;
        indexSpan[5] = idxStart + 2;

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileBezierCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Pen == null) return;
        int startIndex = _vectorVerticesList.Count;
        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
        float thickness = cmd.Pen.Thickness;

        var p0_trans = Vector2.Transform(cmd.Position, transform);
        var p1_trans = Vector2.Transform(cmd.Position2, transform);
        var p2_trans = Vector2.Transform(cmd.Position3, transform);

        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
        uint idxStart = (uint)startIndex;

        var baseVertex = new VectorVertex(p0_trans, Vector4.Zero, p1_trans, penBrushIdx, p2_trans, idxStart, thickness, 5f);
        
        int originalVertexCount = _vectorVerticesList.Count;
        int vertexToAdd = 2 * (N + 1);
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + vertexToAdd);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, vertexToAdd);
        vertexSpan.Fill(baseVertex);

        int originalIndexCount = _vectorIndicesList.Count;
        int indicesToAdd = 6 * N;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + indicesToAdd);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, indicesToAdd);

        for (int i = 0; i < N; i++)
        {
            uint currentLeft = (uint)(idxStart + 2 * i);
            uint currentRight = (uint)(idxStart + 2 * i + 1);
            uint nextLeft = (uint)(idxStart + 2 * i + 2);
            uint nextRight = (uint)(idxStart + 2 * i + 3);

            int baseIdx = 6 * i;
            indexSpan[baseIdx] = currentLeft;
            indexSpan[baseIdx + 1] = currentRight;
            indexSpan[baseIdx + 2] = nextLeft;
            indexSpan[baseIdx + 3] = currentRight;
            indexSpan[baseIdx + 4] = nextRight;
            indexSpan[baseIdx + 5] = nextLeft;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileCubicBezierCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Pen == null) return;
        int startIndex = _vectorVerticesList.Count;
        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
        float thickness = cmd.Pen.Thickness;

        var p0_trans = Vector2.Transform(cmd.Position, transform);
        var p1_trans = Vector2.Transform(cmd.Position2, transform);
        var p2_trans = Vector2.Transform(cmd.Position3, transform);
        var p3_trans = Vector2.Transform(cmd.Position4, transform);

        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
        uint idxStart = (uint)startIndex;

        var baseVertex = new VectorVertex(p0_trans, new Vector4(p3_trans.X, p3_trans.Y, 0f, 0f), p1_trans, penBrushIdx, p2_trans, idxStart, thickness, 6f);
        
        int originalVertexCount = _vectorVerticesList.Count;
        int vertexToAdd = 2 * (N + 1);
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + vertexToAdd);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, vertexToAdd);
        vertexSpan.Fill(baseVertex);

        int originalIndexCount = _vectorIndicesList.Count;
        int indicesToAdd = 6 * N;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + indicesToAdd);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, indicesToAdd);

        for (int i = 0; i < N; i++)
        {
            uint currentLeft = (uint)(idxStart + 2 * i);
            uint currentRight = (uint)(idxStart + 2 * i + 1);
            uint nextLeft = (uint)(idxStart + 2 * i + 2);
            uint nextRight = (uint)(idxStart + 2 * i + 3);

            int baseIdx = 6 * i;
            indexSpan[baseIdx] = currentLeft;
            indexSpan[baseIdx + 1] = currentRight;
            indexSpan[baseIdx + 2] = nextLeft;
            indexSpan[baseIdx + 3] = currentRight;
            indexSpan[baseIdx + 4] = nextRight;
            indexSpan[baseIdx + 5] = nextLeft;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompilePolylineCommand(IRenderDataProvider? provider, RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Pen == null) return;

        ReadOnlySpan<Vector2> pointsSpan = provider != null ? 
            provider.GetPoints(cmd.PointBufferOffset, cmd.PointBufferCount) : 
            cmd.PolylinePoints;

        int count = pointsSpan.Length;
        if (count < 2) return;

        int startIndex = _vectorVerticesList.Count;
        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
        float thickness = cmd.Pen.Thickness;

        int segmentCount = count - 1;
        if (cmd.IsClosed) segmentCount++;

        // Pre-transform all points to screen space in one batch
        Span<Vector2> transformed = count <= 512 ? stackalloc Vector2[count] : new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            transformed[i] = Vector2.Transform(pointsSpan[i], transform);
        }

        // We will append 4 vertices and 6 indices for each segment
        int totalVerticesToAdd = segmentCount * 4;
        int totalIndicesToAdd = segmentCount * 6;

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + totalVerticesToAdd);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, totalVerticesToAdd);

        int originalIndexCount = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + totalIndicesToAdd);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, totalIndicesToAdd);

        for (int i = 0; i < segmentCount; i++)
        {
            var p0_pos = transformed[i % count];
            var p1_pos = transformed[(i + 1) % count];

            uint idxStart = (uint)(originalVertexCount + i * 4);

            int vIdx = i * 4;
            vertexSpan[vIdx] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 1f, thickness, 3f);
            vertexSpan[vIdx + 1] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -1f, thickness, 3f);
            vertexSpan[vIdx + 2] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 2f, thickness, 3f);
            vertexSpan[vIdx + 3] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -2f, thickness, 3f);

            int iIdx = i * 6;
            indexSpan[iIdx] = idxStart;
            indexSpan[iIdx + 1] = idxStart + 1;
            indexSpan[iIdx + 2] = idxStart + 2;
            indexSpan[iIdx + 3] = idxStart + 1;
            indexSpan[iIdx + 4] = idxStart + 3;
            indexSpan[iIdx + 5] = idxStart + 2;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileSplineCommand(IRenderDataProvider? provider, RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Pen == null) return;

        ReadOnlySpan<Vector2> controlPoints = provider != null ? 
            provider.GetPoints(cmd.PointBufferOffset, cmd.PointBufferCount) : 
            cmd.PolylinePoints;

        ReadOnlySpan<double> knots = provider != null ? 
            provider.GetDoubles(cmd.DoubleBufferOffset, cmd.DoubleBufferCount) : 
            cmd.SplineKnots;

        ReadOnlySpan<double> weights = provider != null && cmd.WeightBufferCount > 0 ? 
            provider.GetDoubles(cmd.WeightBufferOffset, cmd.WeightBufferCount) : 
            cmd.SplineWeights;

        if (controlPoints.Length < 2 || knots.IsEmpty) return;

        int startIndex = _vectorVerticesList.Count;
        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
        float thickness = cmd.Pen.Thickness;

        int degree = cmd.SplineDegree;

        if (knots.Length < controlPoints.Length + degree + 1)
        {
            // Fallback: draw control points as a polyline
            var fallbackCmd = new RenderCommand
            {
                Pen = cmd.Pen,
                PointBufferOffset = cmd.PointBufferOffset,
                PointBufferCount = cmd.PointBufferCount,
                PolylinePoints = cmd.PolylinePoints,
                IsClosed = false
            };
            CompilePolylineCommand(provider, fallbackCmd, transform);
            return;
        }

        double startKnot = knots[degree];
        double endKnot = knots[knots.Length - degree - 1];

        // Calculate screen-space bounding box of control points to determine dynamic LOD
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var cp in controlPoints)
        {
            var sp = Vector2.Transform(cp, transform);
            minX = Math.Min(minX, sp.X);
            minY = Math.Min(minY, sp.Y);
            maxX = Math.Max(maxX, sp.X);
            maxY = Math.Max(maxY, sp.Y);
        }

        var minPt = new Vector2(minX, minY);
        var maxPt = new Vector2(maxX, maxY);

        float sizeOnScreen = Vector2.Distance(minPt, maxPt);
        if (sizeOnScreen < 2f) return; // Too small to see

        // Determine dynamic segment count (LOD) based on screen size
        int numPoints = 100;
        if (sizeOnScreen < 20f) numPoints = 10;
        else if (sizeOnScreen < 80f) numPoints = 25;
        else if (sizeOnScreen < 250f) numPoints = 50;
        else numPoints = 100;

        // Pre-evaluate B-spline points directly to screen space
        Span<Vector2> transformed = numPoints + 1 <= 512 ? stackalloc Vector2[numPoints + 1] : new Vector2[numPoints + 1];
        double delta = (endKnot - startKnot) / numPoints;
        for (int i = 0; i <= numPoints; i++)
        {
            double u = startKnot + i * delta;
            transformed[i] = EvaluateBSpline(degree, controlPoints, knots, weights, u, transform);
        }

        // Compile segments into the vertex/index buffer in exactly one batch operation
        int totalVerticesToAdd = numPoints * 4;
        int totalIndicesToAdd = numPoints * 6;

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + totalVerticesToAdd);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, totalVerticesToAdd);

        int originalIndexCount = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + totalIndicesToAdd);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, totalIndicesToAdd);

        for (int i = 0; i < numPoints; i++)
        {
            var p0_pos = transformed[i];
            var p1_pos = transformed[i + 1];

            uint idxStart = (uint)(originalVertexCount + i * 4);

            int vIdx = i * 4;
            vertexSpan[vIdx] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 1f, thickness, 3f);
            vertexSpan[vIdx + 1] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -1f, thickness, 3f);
            vertexSpan[vIdx + 2] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 2f, thickness, 3f);
            vertexSpan[vIdx + 3] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -2f, thickness, 3f);

            int iIdx = i * 6;
            indexSpan[iIdx] = idxStart;
            indexSpan[iIdx + 1] = idxStart + 1;
            indexSpan[iIdx + 2] = idxStart + 2;
            indexSpan[iIdx + 3] = idxStart + 1;
            indexSpan[iIdx + 4] = idxStart + 3;
            indexSpan[iIdx + 5] = idxStart + 2;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileFillTriangleCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Brush == null) return;
        int startIndex = _vectorVerticesList.Count;
        float brushIdx = RegisterBrush(cmd.Brush);
        var brushColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

        var p1 = Vector2.Transform(cmd.Position, transform);
        var p2 = Vector2.Transform(cmd.Position2, transform);
        var p3 = Vector2.Transform(cmd.Position3, transform);

        uint idxStart = (uint)startIndex;

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 3);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 3);

        vertexSpan[0] = new VectorVertex(p1, brushColor, cmd.Position, brushIdx, default, 0f, 0f, 7f);
        vertexSpan[1] = new VectorVertex(p2, brushColor, cmd.Position2, brushIdx, default, 0f, 0f, 7f);
        vertexSpan[2] = new VectorVertex(p3, brushColor, cmd.Position3, brushIdx, default, 0f, 0f, 7f);

        int originalIndexCount = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 3);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 3);

        indexSpan[0] = idxStart;
        indexSpan[1] = idxStart + 1;
        indexSpan[2] = idxStart + 2;

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileFillQuadCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Brush == null) return;
        int startIndex = _vectorVerticesList.Count;
        float brushIdx = RegisterBrush(cmd.Brush);
        var brushColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

        var p1 = Vector2.Transform(cmd.Position, transform);
        var p2 = Vector2.Transform(cmd.Position2, transform);
        var p3 = Vector2.Transform(cmd.Position3, transform);
        var p4 = Vector2.Transform(cmd.Position4, transform);

        uint idxStart = (uint)startIndex;

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(p1, brushColor, cmd.Position, brushIdx, default, 0f, 0f, 7f);
        vertexSpan[1] = new VectorVertex(p2, brushColor, cmd.Position2, brushIdx, default, 0f, 0f, 7f);
        vertexSpan[2] = new VectorVertex(p3, brushColor, cmd.Position3, brushIdx, default, 0f, 0f, 7f);
        vertexSpan[3] = new VectorVertex(p4, brushColor, cmd.Position4, brushIdx, default, 0f, 0f, 7f);

        int originalIndexCount = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
        var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

        indexSpan[0] = idxStart;
        indexSpan[1] = idxStart + 1;
        indexSpan[2] = idxStart + 2;
        indexSpan[3] = idxStart;
        indexSpan[4] = idxStart + 2;
        indexSpan[5] = idxStart + 3;

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private Vector2 EvaluateBSpline(int degree, ReadOnlySpan<Vector2> controlPoints, ReadOnlySpan<double> knots, ReadOnlySpan<double> weights, double u, Matrix4x4 transform)
    {
        int k = -1;
        if (u < knots[degree]) u = knots[degree];
        if (u > knots[knots.Length - degree - 1]) u = knots[knots.Length - degree - 1];

        for (int i = degree; i < knots.Length - 1; i++)
        {
            if (u >= knots[i] && u <= knots[i + 1])
            {
                k = i;
                break;
            }
        }

        if (k == -1)
        {
            k = knots.Length - degree - 2;
        }

        Span<Vector3> d = stackalloc Vector3[degree + 1];
        for (int j = 0; j <= degree; j++)
        {
            int idx = k - degree + j;
            if (idx >= 0 && idx < controlPoints.Length)
            {
                float w = 1f;
                if (!weights.IsEmpty && idx < weights.Length)
                {
                    w = (float)weights[idx];
                }
                d[j] = new Vector3(controlPoints[idx].X * w, controlPoints[idx].Y * w, w);
            }
            else
            {
                d[j] = Vector3.Zero;
            }
        }

        for (int r = 1; r <= degree; r++)
        {
            for (int j = degree; j >= r; j--)
            {
                int i = k - degree + j;
                double denom = knots[i + degree + 1 - r] - knots[i];
                float alpha = (denom > 1e-9) ? (float)((u - knots[i]) / denom) : 0f;
                d[j] = (1f - alpha) * d[j - 1] + alpha * d[j];
            }
        }

        Vector3 finalH = d[degree];
        Vector2 cartesianPt = (Math.Abs(finalH.Z) > 1e-9f) 
            ? new Vector2(finalH.X / finalH.Z, finalH.Y / finalH.Z) 
            : new Vector2(finalH.X, finalH.Y);

        return Vector2.Transform(cartesianPt, transform);
    }

    private void CompileEllipseCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        int startIndex = _vectorVerticesList.Count;
        var center = cmd.Position2;
        var rx = cmd.RadiusX;
        var ry = cmd.RadiusY;
        var shapeSize = new Vector2(2f * rx, 2f * ry);

        if (cmd.Brush != null)
        {
            float pad = 1.5f;
            var f0_pos = Vector2.Transform(new Vector2(center.X - rx - pad, center.Y - ry - pad), transform);
            var f1_pos = Vector2.Transform(new Vector2(center.X + rx + pad, center.Y - ry - pad), transform);
            var f2_pos = Vector2.Transform(new Vector2(center.X + rx + pad, center.Y + ry + pad), transform);
            var f3_pos = Vector2.Transform(new Vector2(center.X - rx - pad, center.Y + ry + pad), transform);

            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(center.X, center.Y, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(f0_pos, solidColor, new Vector2(-rx - pad, -ry - pad), bIdx, shapeSize, 0f, 0f, 1f);
            vertexSpan[1] = new VectorVertex(f1_pos, solidColor, new Vector2(rx + pad, -ry - pad), bIdx, shapeSize, 0f, 0f, 1f);
            vertexSpan[2] = new VectorVertex(f2_pos, solidColor, new Vector2(rx + pad, ry + pad), bIdx, shapeSize, 0f, 0f, 1f);
            vertexSpan[3] = new VectorVertex(f3_pos, solidColor, new Vector2(-rx - pad, ry + pad), bIdx, shapeSize, 0f, 0f, 1f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (cmd.Pen != null)
        {
            float pad = cmd.Pen.Thickness / 2f + 1.5f;
            var p0_pos = Vector2.Transform(new Vector2(center.X - rx - pad, center.Y - ry - pad), transform);
            var p1_pos = Vector2.Transform(new Vector2(center.X + rx + pad, center.Y - ry - pad), transform);
            var p2_pos = Vector2.Transform(new Vector2(center.X + rx + pad, center.Y + ry + pad), transform);
            var p3_pos = Vector2.Transform(new Vector2(center.X - rx - pad, center.Y + ry + pad), transform);

            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solidPen) ? solidPen.Color : new Vector4(center.X, center.Y, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, new Vector2(-rx - pad, -ry - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f);
            vertexSpan[1] = new VectorVertex(p1_pos, penSolidColor, new Vector2(rx + pad, -ry - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f);
            vertexSpan[2] = new VectorVertex(p2_pos, penSolidColor, new Vector2(rx + pad, ry + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f);
            vertexSpan[3] = new VectorVertex(p3_pos, penSolidColor, new Vector2(-rx - pad, ry + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, 1f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private void CompileCircleCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        cmd.RadiusY = cmd.RadiusX;
        CompileEllipseCommand(cmd, transform);
    }

    private void CompileRoundedRectCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        int startIndex = _vectorVerticesList.Count;
        var r = cmd.Rect;
        var radius = Math.Min(cmd.RadiusX, Math.Min(r.Width / 2f, r.Height / 2f));

        if (radius <= 0f)
        {
            CompileRectCommand(cmd, transform);
            return;
        }

        float wHalf = r.Width / 2f;
        float hHalf = r.Height / 2f;
        var shapeSize = new Vector2(r.Width, r.Height);

        if (cmd.Brush != null)
        {
            float pad = 1.5f;
            var f0_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y - pad), transform);
            var f1_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y - pad), transform);
            var f2_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y + r.Height + pad), transform);
            var f3_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y + r.Height + pad), transform);

            float bIdx = RegisterBrush(cmd.Brush);
            var solidColor = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(r.X + wHalf, r.Y + hHalf, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(f0_pos, solidColor, new Vector2(-wHalf - pad, -hHalf - pad), bIdx, shapeSize, radius, 0f, 2f);
            vertexSpan[1] = new VectorVertex(f1_pos, solidColor, new Vector2(wHalf + pad, -hHalf - pad), bIdx, shapeSize, radius, 0f, 2f);
            vertexSpan[2] = new VectorVertex(f2_pos, solidColor, new Vector2(wHalf + pad, hHalf + pad), bIdx, shapeSize, radius, 0f, 2f);
            vertexSpan[3] = new VectorVertex(f3_pos, solidColor, new Vector2(-wHalf - pad, hHalf + pad), bIdx, shapeSize, radius, 0f, 2f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (cmd.Pen != null)
        {
            float pad = cmd.Pen.Thickness / 2f + 1.5f;
            var p0_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y - pad), transform);
            var p1_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y - pad), transform);
            var p2_pos = Vector2.Transform(new Vector2(r.X + r.Width + pad, r.Y + r.Height + pad), transform);
            var p3_pos = Vector2.Transform(new Vector2(r.X - pad, r.Y + r.Height + pad), transform);

            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solidPen) ? solidPen.Color : new Vector4(r.X + wHalf, r.Y + hHalf, 0f, 0f);

            uint idxStart = (uint)_vectorVerticesList.Count;

            int originalVertexCount = _vectorVerticesList.Count;
            CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
            var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

            vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, new Vector2(-wHalf - pad, -hHalf - pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f);
            vertexSpan[1] = new VectorVertex(p1_pos, penSolidColor, new Vector2(wHalf + pad, -hHalf - pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f);
            vertexSpan[2] = new VectorVertex(p2_pos, penSolidColor, new Vector2(wHalf + pad, hHalf + pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f);
            vertexSpan[3] = new VectorVertex(p3_pos, penSolidColor, new Vector2(-wHalf - pad, hHalf + pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, 2f);

            int originalIndexCount = _vectorIndicesList.Count;
            CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + 6);
            var indexSpan = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, 6);

            indexSpan[0] = idxStart;
            indexSpan[1] = idxStart + 1;
            indexSpan[2] = idxStart + 2;
            indexSpan[3] = idxStart;
            indexSpan[4] = idxStart + 2;
            indexSpan[5] = idxStart + 3;
        }

        if (_activeClipRect.HasValue)
        {
            var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
            for (int i = startIndex; i < vertices.Length; i++)
            {
                var v = vertices[i];
                v.Position = ClampToClip(v.Position);
                vertices[i] = v;
            }
        }
    }

    private float RegisterBrush(Brush? brush)
    {
        if (brush == null) return 0f;
        
        GpuBrush gpuBrush = new GpuBrush();
        gpuBrush.Opacity = brush.Opacity * _activeOpacity;

        if (brush is SolidColorBrush solid)
        {
            gpuBrush.Type = 0;
            gpuBrush.Color0 = solid.Color;
        }
        else if (brush is LinearGradientBrush linear)
        {
            gpuBrush.Type = 1;
            gpuBrush.StartPoint = linear.StartPoint;
            gpuBrush.EndPoint = linear.EndPoint;
            if (linear.Stops != null)
            {
                gpuBrush.StopCount = (uint)Math.Min(4, linear.Stops.Length);
                if (gpuBrush.StopCount > 0) gpuBrush.Color0 = linear.Stops[0].Color;
                if (gpuBrush.StopCount > 1) gpuBrush.Color1 = linear.Stops[1].Color;
                if (gpuBrush.StopCount > 2) gpuBrush.Color2 = linear.Stops[2].Color;
                if (gpuBrush.StopCount > 3) gpuBrush.Color3 = linear.Stops[3].Color;

                float o0 = gpuBrush.StopCount > 0 ? linear.Stops[0].Offset : 0f;
                float o1 = gpuBrush.StopCount > 1 ? linear.Stops[1].Offset : 1f;
                float o2 = gpuBrush.StopCount > 2 ? linear.Stops[2].Offset : 1f;
                float o3 = gpuBrush.StopCount > 3 ? linear.Stops[3].Offset : 1f;
                gpuBrush.Offsets = new Vector4(o0, o1, o2, o3);
            }
        }
        else if (brush is RadialGradientBrush radial)
        {
            gpuBrush.Type = 2;
            gpuBrush.Center = radial.Center;
            gpuBrush.Radius = radial.Radius;
            if (radial.Stops != null)
            {
                gpuBrush.StopCount = (uint)Math.Min(4, radial.Stops.Length);
                if (gpuBrush.StopCount > 0) gpuBrush.Color0 = radial.Stops[0].Color;
                if (gpuBrush.StopCount > 1) gpuBrush.Color1 = radial.Stops[1].Color;
                if (gpuBrush.StopCount > 2) gpuBrush.Color2 = radial.Stops[2].Color;
                if (gpuBrush.StopCount > 3) gpuBrush.Color3 = radial.Stops[3].Color;

                float o0 = gpuBrush.StopCount > 0 ? radial.Stops[0].Offset : 0f;
                float o1 = gpuBrush.StopCount > 1 ? radial.Stops[1].Offset : 1f;
                float o2 = gpuBrush.StopCount > 2 ? radial.Stops[2].Offset : 1f;
                float o3 = gpuBrush.StopCount > 3 ? radial.Stops[3].Offset : 1f;
                gpuBrush.Offsets = new Vector4(o0, o1, o2, o3);
            }
        }
        else if (brush is HatchPatternBrush hatch)
        {
            gpuBrush.Type = 3;
            gpuBrush.Radius = hatch.Angle;
            gpuBrush.Center = new Vector2(hatch.Spacing, hatch.Thickness);
            gpuBrush.Color0 = hatch.Color;
            gpuBrush.StopCount = 1;
        }
        else if (brush is CrossHatchBrush crossHatch)
        {
            gpuBrush.Type = 4;
            gpuBrush.Radius = crossHatch.Angle;
            gpuBrush.Center = new Vector2(crossHatch.Spacing, crossHatch.Thickness);
            gpuBrush.Color0 = crossHatch.Color;
            gpuBrush.StopCount = 1;
        }

        for (int i = 0; i < _activeBrushes.Count; i++)
        {
            if (BrushesEqual(_activeBrushes[i], gpuBrush))
            {
                return (float)i;
            }
        }

        if (_activeBrushes.Count < 64)
        {
            _activeBrushes.Add(gpuBrush);
            return (float)(_activeBrushes.Count - 1);
        }

        return 0f;
    }

    private bool BrushesEqual(GpuBrush a, GpuBrush b)
    {
        return a.Type == b.Type &&
               a.Opacity == b.Opacity &&
               a.StartPoint == b.StartPoint &&
               a.EndPoint == b.EndPoint &&
               a.Center == b.Center &&
               a.Radius == b.Radius &&
               a.StopCount == b.StopCount &&
               a.Color0 == b.Color0 &&
               a.Color1 == b.Color1 &&
               a.Color2 == b.Color2 &&
               a.Color3 == b.Color3 &&
               a.Offsets == b.Offsets;
    }

    private void CompileTextCommand(RenderCommand cmd, ITextLayoutProvider? textNode, Matrix4x4 transform)
    {
        var activeTransform = transform;
        if (MathF.Abs(cmd.Rotation) > 0.0001f)
        {
            var localMat = Matrix4x4.CreateTranslation(-cmd.Position.X, -cmd.Position.Y, 0f) *
                           Matrix4x4.CreateRotationZ(cmd.Rotation) *
                           Matrix4x4.CreateTranslation(cmd.Position.X, cmd.Position.Y, 0f);
            activeTransform = localMat * transform;
        }

        var font = cmd.Font ?? textNode?.Font;
        if (font == null || cmd.Text == null) return;

        TextLayout? layout;
        if (textNode != null)
        {
            layout = textNode.GetOrUpdateLayout(_atlas);
        }
        else
        {
            var key = (cmd.Text, font, cmd.FontSize, TextAlignment.Left);
            if (!_layoutCache.TryGetValue(key, out layout))
            {
                layout = new TextLayout(cmd.Text, font, cmd.FontSize, 10000f, TextAlignment.Left, null);
                _layoutCache[key] = layout;
            }
        }

        if (layout == null) return;

        float bIdx = RegisterBrush(cmd.Brush);
        var brush = cmd.Brush as SolidColorBrush;
        var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);
        color.W *= _activeOpacity;

        int maxGlyphs = layout.Glyphs.Count;
        int maxPassCount = cmd.IsBold ? 2 : 1;
        int maxVertices = maxGlyphs * maxPassCount * 4;
        int maxIndices = maxGlyphs * maxPassCount * 6;

        int vertexStart = _textVerticesList.Count;
        int indexStart = _textIndicesList.Count;

        CollectionsMarshal.SetCount(_textVerticesList, vertexStart + maxVertices);
        CollectionsMarshal.SetCount(_textIndicesList, indexStart + maxIndices);

        var textVerticesSpan = CollectionsMarshal.AsSpan(_textVerticesList);
        var textIndicesSpan = CollectionsMarshal.AsSpan(_textIndicesList);

        int currentVertexCount = vertexStart;
        int currentIndexCount = indexStart;

        foreach (var runGlyph in layout.Glyphs)
        {
            var glyphFont = runGlyph.Font ?? font;
            ushort glyphIdx = glyphFont.GetGlyphIndex(runGlyph.CodePoint);
            var colorLayers = glyphFont.GetColorLayers(glyphIdx);

            if (colorLayers != null && colorLayers.Count > 0)
            {
                foreach (var layer in colorLayers)
                {
                    var layerOutline = glyphFont.GetGlyphOutline(layer.GlyphId);
                    if (layerOutline == null) continue;

                    float emScale = cmd.FontSize / glyphFont.UnitsPerEm;
                    var transformedOutline = new PathGeometry();
                    float x0 = runGlyph.Position.X + cmd.Position.X;
                    float y0 = runGlyph.Position.Y + cmd.Position.Y;

                    foreach (var fig in layerOutline.Figures)
                    {
                        Vector2 startPt = new Vector2(x0 + fig.StartPoint.X * emScale, y0 - fig.StartPoint.Y * emScale);
                        var newFig = new PathFigure(startPt) { IsClosed = fig.IsClosed, IsFilled = fig.IsFilled };
                        foreach (var seg in fig.Segments)
                        {
                            if (seg is LineSegment ls)
                            {
                                newFig.Segments.Add(new LineSegment(new Vector2(x0 + ls.Point.X * emScale, y0 - ls.Point.Y * emScale)));
                            }
                            else if (seg is QuadraticBezierSegment qbs)
                            {
                                newFig.Segments.Add(new QuadraticBezierSegment(
                                    new Vector2(x0 + qbs.ControlPoint.X * emScale, y0 - qbs.ControlPoint.Y * emScale),
                                    new Vector2(x0 + qbs.Point.X * emScale, y0 - qbs.Point.Y * emScale)
                                ));
                            }
                            else if (seg is CubicBezierSegment cbs)
                            {
                                newFig.Segments.Add(new CubicBezierSegment(
                                    new Vector2(x0 + cbs.ControlPoint1.X * emScale, y0 - cbs.ControlPoint1.Y * emScale),
                                    new Vector2(x0 + cbs.ControlPoint2.X * emScale, y0 - cbs.ControlPoint2.Y * emScale),
                                    new Vector2(x0 + cbs.Point.X * emScale, y0 - cbs.Point.Y * emScale)
                                ));
                            }
                        }
                        transformedOutline.Figures.Add(newFig);
                    }

                    var pathCmd = new RenderCommand
                    {
                        Type = RenderCommandType.DrawPath,
                        Path = transformedOutline,
                        Brush = new SolidColorBrush(layer.Color)
                    };
                    CompilePathCommand(pathCmd, activeTransform);
                }
                continue;
            }

            // Retrieve baseline coordinate from layout position by subtracting dummy Bear offset
            float baseCursorX = runGlyph.Position.X - runGlyph.Glyph.BearX;
            float baseCursorY = runGlyph.Position.Y - runGlyph.Glyph.BearY;

            // Compute high-DPI scaling factor dynamically from the window context
            float dpiScale = 1.0f;
            if (_context.Window != null)
            {
                dpiScale = (float)_context.Window.FramebufferSize.X / _context.Window.Size.X;
            }

            float physicalFontSize = cmd.FontSize * dpiScale;
            bool isRotated = MathF.Abs(activeTransform.M12) > 0.0001f ||
                             MathF.Abs(activeTransform.M21) > 0.0001f ||
                             activeTransform.M11 < 0.0f ||
                             activeTransform.M22 < 0.0f;

            // Compute subpixel positioning and snap vertices to integer pixels to avoid bilinear blur.
            Vector2 transPos = Vector2.Transform(new Vector2(baseCursorX + cmd.Position.X, baseCursorY + cmd.Position.Y), activeTransform);
            Vector2 transPosPhysical = transPos * dpiScale;

            float scaleX = new Vector2(activeTransform.M11, activeTransform.M12).Length();
            float scaleY = new Vector2(activeTransform.M21, activeTransform.M22).Length();

            byte subpixelX = 0;
            float ipartX = 0f;
            float snappedY = 0f;

            if (!isRotated)
            {
                float screenX = transPosPhysical.X;
                float screenY = transPosPhysical.Y;

                float ipartX_temp = MathF.Floor(screenX);
                float fpartX = screenX - ipartX_temp;
                int subIdx = (int)MathF.Round(fpartX * 4f);
                if (subIdx == 4)
                {
                    subIdx = 0;
                    ipartX_temp += 1.0f;
                }
                subpixelX = (byte)subIdx;
                ipartX = ipartX_temp;
                snappedY = MathF.Round(screenY);
            }

            // Cap the physical font size rasterized into the atlas to prevent blowout on huge zoom levels.
            // Scale the mapped UV quad coordinates proportionally for perfect high-DPI scaling.
            float rasterFontSize = Math.Clamp(physicalFontSize, 4f, 128f);
            float scaleRatio = physicalFontSize / rasterFontSize;

            var info = _atlas.GetOrCreateGlyph(glyphFont, runGlyph.CodePoint, rasterFontSize, subpixelX);
            if (info.Width == 0 || info.Height == 0) continue;

            int passCount = cmd.IsBold ? 2 : 1;
            float boldOffset = cmd.FontSize * 0.035f;

            for (int pass = 0; pass < passCount; pass++)
            {
                float xOffset = pass * boldOffset;
                Vector2 v0, v1, v2, v3;

                if (!isRotated)
                {
                    // Position the quad in physical screen pixels scaled by scaleRatio
                    float rx0 = ipartX + info.BearX * scaleX * scaleRatio + xOffset * scaleX * dpiScale;
                    float ry0 = snappedY + info.BearY * scaleY * scaleRatio;
                    float rx1 = rx0 + info.Width * scaleX * scaleRatio;
                    float ry1 = ry0 + info.Height * scaleY * scaleRatio;

                    float skewFactor = cmd.IsItalic ? 0.22f : 0f;
                    float yBase = snappedY; // Baseline is snappedY

                    float sx0 = rx0 - (ry0 - yBase) * skewFactor;
                    float sx1 = rx1 - (ry0 - yBase) * skewFactor;
                    float sx2 = rx1 - (ry1 - yBase) * skewFactor;
                    float sx3 = rx0 - (ry1 - yBase) * skewFactor;

                    // Divide by dpiScale to map the physical coordinates back to logical compositor projection space
                    v0 = new Vector2(sx0, ry0) / dpiScale;
                    v1 = new Vector2(sx1, ry0) / dpiScale;
                    v2 = new Vector2(sx2, ry1) / dpiScale;
                    v3 = new Vector2(sx3, ry1) / dpiScale;
                }
                else
                {
                    // Rotated text: transform each vertex individually on the CPU scaled by scaleRatio
                    float lx0 = info.BearX / dpiScale * scaleRatio + xOffset;
                    float ly0 = info.BearY / dpiScale * scaleRatio;
                    float lx1 = lx0 + info.Width / dpiScale * scaleRatio;
                    float ly1 = ly0 + info.Height / dpiScale * scaleRatio;

                    float skewFactor = cmd.IsItalic ? 0.22f : 0f;
                    float yBase = 0f;

                    float lsx0 = lx0 - (ly0 - yBase) * skewFactor;
                    float lsx1 = lx1 - (ly0 - yBase) * skewFactor;
                    float lsx2 = lx1 - (ly1 - yBase) * skewFactor;
                    float lsx3 = lx0 - (ly1 - yBase) * skewFactor;

                    Vector2 localP0 = new Vector2(baseCursorX + cmd.Position.X + lsx0, baseCursorY + cmd.Position.Y + ly0);
                    Vector2 localP1 = new Vector2(baseCursorX + cmd.Position.X + lsx1, baseCursorY + cmd.Position.Y + ly0);
                    Vector2 localP2 = new Vector2(baseCursorX + cmd.Position.X + lsx2, baseCursorY + cmd.Position.Y + ly1);
                    Vector2 localP3 = new Vector2(baseCursorX + cmd.Position.X + lsx3, baseCursorY + cmd.Position.Y + ly1);

                    v0 = Vector2.Transform(localP0, activeTransform);
                    v1 = Vector2.Transform(localP1, activeTransform);
                    v2 = Vector2.Transform(localP2, activeTransform);
                    v3 = Vector2.Transform(localP3, activeTransform);
                }

                uint idxStart = (uint)currentVertexCount;

                // Set dynamic UV texture mappings
                var uv0 = new Vector2(info.TexCoordMin.X, info.TexCoordMin.Y);
                var uv1 = new Vector2(info.TexCoordMax.X, info.TexCoordMin.Y);
                var uv2 = new Vector2(info.TexCoordMax.X, info.TexCoordMax.Y);
                var uv3 = new Vector2(info.TexCoordMin.X, info.TexCoordMax.Y);

                if (_activeClipRect.HasValue && !_useGpuTransformsActive)
                {
                    if (isRotated)
                    {
                        // For rotated text, a simple axis-aligned vertex crop would distort the geometry and scramble the UVs.
                        // Instead, we perform a bounding-box intersection check. If it is completely outside, we discard it.
                        // If it is inside or partially inside, we render it uncropped to preserve its exact rotation and texture alignment.
                        float minX = MathF.Min(MathF.Min(v0.X, v1.X), MathF.Min(v2.X, v3.X));
                        float maxX = MathF.Max(MathF.Max(v0.X, v1.X), MathF.Max(v2.X, v3.X));
                        float minY = MathF.Min(MathF.Min(v0.Y, v1.Y), MathF.Min(v2.Y, v3.Y));
                        float maxY = MathF.Max(MathF.Max(v0.Y, v1.Y), MathF.Max(v2.Y, v3.Y));

                        float clipLeft = _activeClipRect.Value.X;
                        float clipTop = _activeClipRect.Value.Y;
                        float clipRight = clipLeft + _activeClipRect.Value.Width;
                        float clipBottom = clipTop + _activeClipRect.Value.Height;

                        if (maxX <= clipLeft || minX >= clipRight || maxY <= clipTop || minY >= clipBottom)
                        {
                            continue; // Completely clipped!
                        }
                    }
                    else
                    {
                        float rcxStart = v0.X;
                        float rcyStart = v0.Y;
                        float rcxEnd = v2.X;
                        float rcyEnd = v2.Y;

                        float cx1 = Math.Max(rcxStart, _activeClipRect.Value.X);
                        float cy1 = Math.Max(rcyStart, _activeClipRect.Value.Y);
                        float cx2 = Math.Min(rcxEnd, _activeClipRect.Value.X + _activeClipRect.Value.Width);
                        float cy2 = Math.Min(rcyEnd, _activeClipRect.Value.Y + _activeClipRect.Value.Height);

                        if (cx2 <= cx1 || cy2 <= cy1) continue; // Completely clipped!

                        float dx = rcxEnd - rcxStart;
                        float dy = rcyEnd - rcyStart;

                        uv0 = new Vector2(
                            info.TexCoordMin.X + (cx1 - rcxStart) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy1 - rcyStart) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );
                        uv1 = new Vector2(
                            info.TexCoordMin.X + (cx2 - rcxStart) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy1 - rcyStart) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );
                        uv2 = new Vector2(
                            info.TexCoordMin.X + (cx2 - rcxStart) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy2 - rcyStart) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );
                        uv3 = new Vector2(
                            info.TexCoordMin.X + (cx1 - rcxStart) / dx * (info.TexCoordMax.X - info.TexCoordMin.X),
                            info.TexCoordMin.Y + (cy2 - rcyStart) / dy * (info.TexCoordMax.Y - info.TexCoordMin.Y)
                        );

                        v0 = new Vector2(cx1, cy1);
                        v1 = new Vector2(cx2, cy1);
                        v2 = new Vector2(cx2, cy2);
                        v3 = new Vector2(cx1, cy2);
                    }
                }

                textVerticesSpan[currentVertexCount++] = new VectorVertex(v0, color, uv0, bIdx, cornerRadius: DefaultTextGamma, strokeThickness: DefaultTextContrast);
                textVerticesSpan[currentVertexCount++] = new VectorVertex(v1, color, uv1, bIdx, cornerRadius: DefaultTextGamma, strokeThickness: DefaultTextContrast);
                textVerticesSpan[currentVertexCount++] = new VectorVertex(v2, color, uv2, bIdx, cornerRadius: DefaultTextGamma, strokeThickness: DefaultTextContrast);
                textVerticesSpan[currentVertexCount++] = new VectorVertex(v3, color, uv3, bIdx, cornerRadius: DefaultTextGamma, strokeThickness: DefaultTextContrast);

                // Quads Triangle Indices
                textIndicesSpan[currentIndexCount++] = idxStart;
                textIndicesSpan[currentIndexCount++] = idxStart + 1;
                textIndicesSpan[currentIndexCount++] = idxStart + 2;

                textIndicesSpan[currentIndexCount++] = idxStart;
                textIndicesSpan[currentIndexCount++] = idxStart + 2;
                textIndicesSpan[currentIndexCount++] = idxStart + 3;
            }
        }

        CollectionsMarshal.SetCount(_textVerticesList, currentVertexCount);
        CollectionsMarshal.SetCount(_textIndicesList, currentIndexCount);
    }

    private void CompileTextureCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Texture == null) return;

        CommitPendingDrawCalls();

        var r = cmd.Rect;
        var color = new Vector4(1f, 1f, 1f, _activeOpacity);

        var v0 = Vector2.Transform(new Vector2(r.X, r.Y), transform);
        var v1 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y), transform);
        var v2 = Vector2.Transform(new Vector2(r.X + r.Width, r.Y + r.Height), transform);
        var v3 = Vector2.Transform(new Vector2(r.X, r.Y + r.Height), transform);

        uint idxStart = (uint)_textureVerticesList.Count;

        var uv0 = new Vector2(0f, 0f);
        var uv1 = new Vector2(1f, 0f);
        var uv2 = new Vector2(1f, 1f);
        var uv3 = new Vector2(0f, 1f);

        bool isRotated = MathF.Abs(transform.M12) > 0.0001f ||
                         MathF.Abs(transform.M21) > 0.0001f ||
                         transform.M11 < 0.0f ||
                         transform.M22 < 0.0f;
        if (_activeClipRect.HasValue && !_useGpuTransformsActive)
        {
            if (isRotated)
            {
                // For rotated textures, a simple axis-aligned vertex crop would distort the geometry and scramble the UVs.
                // Instead, we perform a bounding-box intersection check. If it is completely outside, we discard it.
                // If it is inside or partially inside, we render it uncropped to preserve its exact rotation and texture alignment.
                float minX = MathF.Min(MathF.Min(v0.X, v1.X), MathF.Min(v2.X, v3.X));
                float maxX = MathF.Max(MathF.Max(v0.X, v1.X), MathF.Max(v2.X, v3.X));
                float minY = MathF.Min(MathF.Min(v0.Y, v1.Y), MathF.Min(v2.Y, v3.Y));
                float maxY = MathF.Max(MathF.Max(v0.Y, v1.Y), MathF.Max(v2.Y, v3.Y));

                float clipLeft = _activeClipRect.Value.X;
                float clipTop = _activeClipRect.Value.Y;
                float clipRight = clipLeft + _activeClipRect.Value.Width;
                float clipBottom = clipTop + _activeClipRect.Value.Height;

                if (maxX <= clipLeft || minX >= clipRight || maxY <= clipTop || minY >= clipBottom)
                {
                    return; // Completely clipped!
                }
            }
            else
            {
                float rx1 = v0.X;
                float ry1 = v0.Y;
                float rx2 = v2.X;
                float ry2 = v2.Y;

                float cx1 = Math.Max(rx1, _activeClipRect.Value.X);
                float cy1 = Math.Max(ry1, _activeClipRect.Value.Y);
                float cx2 = Math.Min(rx2, _activeClipRect.Value.X + _activeClipRect.Value.Width);
                float cy2 = Math.Min(ry2, _activeClipRect.Value.Y + _activeClipRect.Value.Height);

                if (cx2 <= cx1 || cy2 <= cy1) return; // Completely clipped!

                float dx = rx2 - rx1;
                float dy = ry2 - ry1;

                uv0 = new Vector2((cx1 - rx1) / dx, (cy1 - ry1) / dy);
                uv1 = new Vector2((cx2 - rx1) / dx, (cy1 - ry1) / dy);
                uv2 = new Vector2((cx2 - rx1) / dx, (cy2 - ry1) / dy);
                uv3 = new Vector2((cx1 - rx1) / dx, (cy2 - ry1) / dy);

                v0 = new Vector2(cx1, cy1);
                v1 = new Vector2(cx2, cy1);
                v2 = new Vector2(cx2, cy2);
                v3 = new Vector2(cx1, cy2);
            }
        }

        int originalVertexCount = _textureVerticesList.Count;
        CollectionsMarshal.SetCount(_textureVerticesList, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(_textureVerticesList).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(v0, color, uv0);
        vertexSpan[1] = new VectorVertex(v1, color, uv1);
        vertexSpan[2] = new VectorVertex(v2, color, uv2);
        vertexSpan[3] = new VectorVertex(v3, color, uv3);

        int originalIndexCount = _textureIndicesList.Count;
        CollectionsMarshal.SetCount(_textureIndicesList, originalIndexCount + 6);
        var indexSpan = CollectionsMarshal.AsSpan(_textureIndicesList).Slice(originalIndexCount, 6);

        indexSpan[0] = idxStart;
        indexSpan[1] = idxStart + 1;
        indexSpan[2] = idxStart + 2;
        indexSpan[3] = idxStart;
        indexSpan[4] = idxStart + 2;
        indexSpan[5] = idxStart + 3;

        _drawCalls.Add(new CompositorDrawCall
        {
            Type = DrawCallType.Texture,
            IndexStart = (uint)(_textureIndicesList.Count - 6),
            IndexCount = 6,
            Texture = cmd.Texture
        });
    }

    private void CommitPendingDrawCalls()
    {
        uint vecCount = (uint)_vectorIndicesList.Count - _pendingVectorStart;
        if (vecCount > 0)
        {
            _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Vector, IndexStart = _pendingVectorStart, IndexCount = vecCount });
            _pendingVectorStart = (uint)_vectorIndicesList.Count;
        }

        uint textCount = (uint)_textIndicesList.Count - _pendingTextStart;
        if (textCount > 0)
        {
            _drawCalls.Add(new CompositorDrawCall { Type = DrawCallType.Text, IndexStart = _pendingTextStart, IndexCount = textCount });
            _pendingTextStart = (uint)_textIndicesList.Count;
        }
    }

    private void EnsureBufferSize(ref GpuBuffer buffer, uint requiredSize, BufferUsage usage)
    {
        if (buffer.Size >= requiredSize) return;

        uint newSize = Math.Max(buffer.Size * 2, requiredSize);
        buffer.Dispose();
        
        string lbl = usage == BufferUsage.Vertex ? "Vector/Text Resize Vertex Buffer" : "Vector/Text Resize Index Buffer";
        buffer = new GpuBuffer(_context, newSize, usage | BufferUsage.CopyDst, lbl);
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        ReleaseMsaaResources();

        _uniformBuffer.Dispose();
        _brushesStorageBuffer.Dispose();
        _hatchRecordsBuffer.Dispose();
        _hatchSegmentsBuffer.Dispose();
        _acisRecordsBuffer.Dispose();
        _acisEdgesBuffer.Dispose();
        _vectorVertexBuffer.Dispose();
        _vectorIndexBuffer.Dispose();
        _textVertexBuffer.Dispose();
        _textIndexBuffer.Dispose();
        _textureVertexBuffer.Dispose();
        _textureIndexBuffer.Dispose();
        
        _atlas.Dispose();
        _pathAtlas.Dispose();
        if (_pathAtlasBindGroup != null) _context.Wgpu.BindGroupRelease(_pathAtlasBindGroup);
        if (_pathAtlasBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_pathAtlasBindGroupLayout);
        if (_pathAtlasBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_pathAtlasBindGroupOffscreen);
        if (_pathAtlasBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_pathAtlasBindGroupLayoutOffscreen);
        _pipelineCache.Dispose();
        _compute.Dispose();
        foreach (var tuple in _effectTextures.Values)
        {
            tuple.Source.Dispose();
            tuple.Temp.Dispose();
            tuple.Destination.Dispose();
        }
        _effectTextures.Clear();

        foreach (var tex in _allocatedLayerTextures)
        {
            tex.Dispose();
        }
        _allocatedLayerTextures.Clear();

        if (_atlasSampler != null) _context.Wgpu.SamplerRelease(_atlasSampler);

        if (_vectorUniformBindGroup != null) _context.Wgpu.BindGroupRelease(_vectorUniformBindGroup);
        if (_vectorUniformBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_vectorUniformBindGroupOffscreen);
        if (_textUniformBindGroup != null) _context.Wgpu.BindGroupRelease(_textUniformBindGroup);
        if (_textUniformBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_textUniformBindGroupOffscreen);
        if (_textureUniformBindGroup != null) _context.Wgpu.BindGroupRelease(_textureUniformBindGroup);
        if (_textureUniformBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_textureUniformBindGroupOffscreen);

        if (_vectorUniformBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_vectorUniformBindGroupLayout);
        if (_vectorUniformBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_vectorUniformBindGroupLayoutOffscreen);
        if (_textUniformBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_textUniformBindGroupLayout);
        if (_textUniformBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_textUniformBindGroupLayoutOffscreen);
        if (_textureUniformBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_textureUniformBindGroupLayout);
        if (_textureUniformBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_textureUniformBindGroupLayoutOffscreen);

        if (_atlasBindGroup != null) _context.Wgpu.BindGroupRelease(_atlasBindGroup);
        if (_atlasBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_atlasBindGroupOffscreen);
        if (_atlasBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_atlasBindGroupLayout);
        if (_atlasBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_atlasBindGroupLayoutOffscreen);

        if (_texturePipeline != null) _context.Wgpu.RenderPipelineRelease(_texturePipeline);
        if (_textureBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_textureBindGroupLayout);
        if (_textureBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_textureBindGroupLayoutOffscreen);

        if (_chartLinePipeline != null) _context.Wgpu.RenderPipelineRelease(_chartLinePipeline);
        if (_chartScatterPipeline != null) _context.Wgpu.RenderPipelineRelease(_chartScatterPipeline);
        if (_chartLinePipelineOffscreen != null) _context.Wgpu.RenderPipelineRelease(_chartLinePipelineOffscreen);
        if (_chartScatterPipelineOffscreen != null) _context.Wgpu.RenderPipelineRelease(_chartScatterPipelineOffscreen);
        if (_chartLineBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_chartLineBindGroupLayout);
        if (_chartScatterBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_chartScatterBindGroupLayout);

        foreach (var cachedBg in _persistentTextureBindGroups.Values)
        {
            if (cachedBg.BindGroupPtr != 0) _context.Wgpu.BindGroupRelease((BindGroup*)cachedBg.BindGroupPtr);
        }
        _persistentTextureBindGroups.Clear();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void CreateMsaaResources(uint width, uint height)
    {
        _msaaWidth = width > 0 ? width : 1;
        _msaaHeight = height > 0 ? height : 1;

        var labelPtr = SilkMarshal.StringToPtr("MSAA Color Texture");

        var desc = new TextureDescriptor
        {
            Label = (byte*)labelPtr,
            Usage = TextureUsage.RenderAttachment,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = _msaaWidth, Height = _msaaHeight, DepthOrArrayLayers = 1 },
            Format = RenderFormat,
            MipLevelCount = 1,
            SampleCount = 4,
            ViewFormatCount = 0,
            ViewFormats = null
        };

        _msaaTexture = _context.Wgpu.DeviceCreateTexture(_context.Device, &desc);
        SilkMarshal.Free(labelPtr);

        if (_msaaTexture == null)
        {
            throw new InvalidOperationException($"Failed to allocate MSAA Texture {_msaaWidth}x{_msaaHeight}.");
        }

        var viewDesc = new TextureViewDescriptor
        {
            Format = RenderFormat,
            Dimension = TextureViewDimension.Dimension2D,
            BaseMipLevel = 0,
            MipLevelCount = 1,
            BaseArrayLayer = 0,
            ArrayLayerCount = 1,
            Aspect = TextureAspect.All
        };

        _msaaTextureView = _context.Wgpu.TextureCreateView(_msaaTexture, &viewDesc);
        if (_msaaTextureView == null)
        {
            throw new InvalidOperationException($"Failed to create TextureView for MSAA Texture {_msaaWidth}x{_msaaHeight}.");
        }
    }

    private void ReleaseMsaaResources()
    {
        if (_msaaTextureView != null)
        {
            _context.Wgpu.TextureViewRelease(_msaaTextureView);
            _msaaTextureView = null;
        }

        if (_msaaTexture != null)
        {
            _context.Wgpu.TextureDestroy(_msaaTexture);
            _context.Wgpu.TextureRelease(_msaaTexture);
            _msaaTexture = null;
        }
    }

    private Vector2 ClampToClip(Vector2 p)
    {
        if (!_activeClipRect.HasValue || _useGpuTransformsActive) return p;
        var r = _activeClipRect.Value;
        float x = Math.Max(r.X, Math.Min(r.X + r.Width, p.X));
        float y = Math.Max(r.Y, Math.Min(r.Y + r.Height, p.Y));
        return new Vector2(x, y);
    }

    ~Compositor()
    {
        Dispose();
    }

    // Helper methods for real-time drop shadows and Gaussian/backdrop blurs
    private void ApplyAndDrawEffect(Visual fe, Matrix4x4 parentTransform)
    {
        if (fe.Size.X <= 0f || fe.Size.Y <= 0f) return;

        float blurRadius = 0f;
        float padding = 0f;

        if (fe.Effect is BlurEffect blur)
        {
            blurRadius = blur.BlurRadius;
            padding = MathF.Ceiling(blurRadius * 2f);
        }
        else if (fe.Effect is DropShadowEffect shadow)
        {
            blurRadius = shadow.BlurRadius;
            padding = MathF.Ceiling(blurRadius * 2f);
        }


        uint w = (uint)(fe.Size.X + padding * 2f);
        uint h = (uint)(fe.Size.Y + padding * 2f);

        bool hasCached = _effectTextures.TryGetValue(fe, out var textures);
        bool needsUpdate = !hasCached || fe.IsDirty;

        if (needsUpdate)
        {
            if (!hasCached)
            {
                var source = new GpuTexture(_context, w, h, RenderFormat, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, "Effect Source");
                var temp = new GpuTexture(_context, w, h, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.StorageBinding, "Effect Temp");
                var destination = new GpuTexture(_context, w, h, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.StorageBinding, "Effect Destination");
                
                textures = (source, temp, destination);
                _effectTextures[fe] = textures;
            }
            else
            {
                textures.Source.Resize(w, h);
                textures.Temp.Resize(w, h);
                textures.Destination.Resize(w, h);
            }

            _elementsRenderingEffects.Add(fe);
            try
            {
                // 1. Render the subtree of fe offscreen centered into textures.Source (offset by padding)
                RenderOffscreen(fe, w, h, textures.Source, padding);
            }
            finally
            {
                _elementsRenderingEffects.Remove(fe);
            }

            // 2. Apply compute shader accelerator filter
            if (fe.Effect is BlurEffect blurEffect)
            {
                if (blurEffect.BlurRadius > 0.01f)
                {
                    _compute.ApplyGaussianBlur(textures.Source, textures.Temp, textures.Destination, blurEffect.BlurRadius);
                }
            }
            else if (fe.Effect is DropShadowEffect shadowEffect)
            {
                // We pass zero offset to the compute shader because we handle offset dynamically in DrawTextureOnMain on the CPU
                _compute.ApplyDropShadow(textures.Source, textures.Temp, textures.Destination, Vector2.Zero, shadowEffect.Color, shadowEffect.BlurRadius);
            }

        }

        // Draw the cached texture onto the main swapchain
        if (fe.Effect is BlurEffect bEff)
        {
            if (bEff.BlurRadius <= 0.01f)
            {
                // Draw original source directly (no blur!)
                var controlRect = new Rect(fe.Offset - new Vector2(padding, padding), new Vector2(w, h));
                DrawTextureOnMain(textures.Source, controlRect, parentTransform);
            }
            else
            {
                // Draw the blurred result back onto the main screen (shifted back by padding)
                var controlRect = new Rect(fe.Offset - new Vector2(padding, padding), new Vector2(w, h));
                DrawTextureOnMain(textures.Destination, controlRect, parentTransform);
            }
        }
        else if (fe.Effect is DropShadowEffect sEff)
        {
            // Draw blurred shadow first (at offset, shifted back by padding)
            var shadowRect = new Rect(fe.Offset + sEff.Offset - new Vector2(padding, padding), new Vector2(w, h));
            DrawTextureOnMain(textures.Destination, shadowRect, parentTransform);
            
            // Draw original source on top (shifted back by padding)
            var controlRect = new Rect(fe.Offset - new Vector2(padding, padding), new Vector2(w, h));
            DrawTextureOnMain(textures.Source, controlRect, parentTransform);
        }


        fe.IsDirty = false;
    }

    private void ApplyAndDrawLayer(Visual node, Matrix4x4 parentTransform)
    {
        if (node.Size.X <= 0f || node.Size.Y <= 0f) return;

        float dpiScale = 1.0f;
        if (_context.Window != null)
        {
            dpiScale = (float)_context.Window.FramebufferSize.X / _context.Window.Size.X;
        }

        uint w = (uint)MathF.Max(1f, node.Size.X * dpiScale);
        uint h = (uint)MathF.Max(1f, node.Size.Y * dpiScale);

        bool hasCached = node.LayerTexture != null;
        bool needsUpdate = !hasCached || node.IsDirty;

        if (needsUpdate)
        {
            if (node.LayerTexture == null)
            {
                node.LayerTexture = new GpuTexture(_context, w, h, RenderFormat, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, "Layer Cache Texture");
                _allocatedLayerTextures.Add(node.LayerTexture);
            }
            else if (node.LayerTexture.Width != w || node.LayerTexture.Height != h)
            {
                node.LayerTexture.Resize(w, h);
            }

            _elementsRenderingLayers.Add(node);
            try
            {
                // Render the subtree of node offscreen centered with 0 padding into node.LayerTexture
                RenderOffscreen(node, (uint)node.Size.X, (uint)node.Size.Y, node.LayerTexture, 0f);
            }
            finally
            {
                _elementsRenderingLayers.Remove(node);
            }
        }

        // Draw the cached layer texture onto the main swapchain
        var controlRect = new Rect(node.Offset, node.Size);
        DrawTextureOnMain(node.LayerTexture!, controlRect, parentTransform);

        node.IsDirty = false;
    }

    private void DrawTextureOnMain(GpuTexture texture, Rect localRect, Matrix4x4 parentTransform)
    {
        var cmd = new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Texture = texture,
            Rect = localRect
        };
        CompileTextureCommand(cmd, parentTransform);
    }

    public void RenderOffscreen(Visual node, uint width, uint height, GpuTexture targetTexture, float padding)
    {
        var savedWidth = _currentWidth;
        var savedHeight = _currentHeight;
        _currentWidth = width;
        _currentHeight = height;

        // 1. Calculate orthographic projection matrix for offscreen
        var projection = new Matrix4x4(
            2.0f / width, 0f, 0f, 0f,
            0f, -2.0f / height, 0f, 0f,
            0f, 0f, 1f, 0f,
            -1.0f, 1.0f, 0f, 1.0f
        );
        var savedProjection = _currentProjection;
        _currentProjection = projection;

        // 2. Save and clear lists
        var savedVectorVertices = _vectorVerticesList.ToArray();
        var savedVectorIndices = _vectorIndicesList.ToArray();
        var savedTextVertices = _textVerticesList.ToArray();
        var savedTextIndices = _textIndicesList.ToArray();
        var savedTextureVertices = _textureVerticesList.ToArray();
        var savedTextureIndices = _textureIndicesList.ToArray();
        var savedDrawCalls = _drawCalls.ToArray();
        var savedActiveBrushes = _activeBrushes.ToArray();
        var savedClipStack = _clipStack.ToArray();
        var savedActiveClipRect = _activeClipRect;
        var savedOpacityStack = _opacityStack.ToArray();
        var savedActiveOpacity = _activeOpacity;
        var savedPendingVectorStart = _pendingVectorStart;
        var savedPendingTextStart = _pendingTextStart;

        var savedUseGpuTransformsActive = _useGpuTransformsActive;
        var savedCameraViewMatrix = _cameraViewMatrix;
        var savedHasGpuTransformsInFrame = _hasGpuTransformsInFrame;
        var savedGpuTransformsCameraView = _gpuTransformsCameraView;

        _useGpuTransformsActive = false;
        _cameraViewMatrix = Matrix4x4.Identity;
        _hasGpuTransformsInFrame = false;
        _gpuTransformsCameraView = Matrix4x4.Identity;

        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _textIndicesList.Clear();
        _textureVerticesList.Clear();
        _textureIndicesList.Clear();
        _drawCalls.Clear();
        _activeBrushes.Clear();
        _clipStack.Clear();
        _activeClipRect = null;
        _opacityStack.Clear();
        _activeOpacity = 1.0f;

        // Save offset and temporarily set to padding to render centered in the inflated offscreen texture
        var oldOffset = node.Offset;
        node.Offset = new Vector2(padding, padding);

        _pendingVectorStart = 0;
        _pendingTextStart = 0;

        CompileVisualTree(node, Matrix4x4.Identity);

        node.Offset = oldOffset;

        CommitPendingDrawCalls();

        // Upload CPU batches to dynamic GPU buffers
        if (_vectorVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _vectorVertexBuffer, (uint)_vectorVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _vectorVertexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vectorVerticesList));
        }
        if (_vectorIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _vectorIndexBuffer, (uint)_vectorIndicesList.Count * 4, BufferUsage.Index);
            _vectorIndexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vectorIndicesList));
        }

        if (_textVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textVertexBuffer, (uint)_textVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _textVertexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textVerticesList));
        }
        if (_textIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _textIndexBuffer, (uint)_textIndicesList.Count * 4, BufferUsage.Index);
            _textIndexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textIndicesList));
        }

        if (_textureVerticesList.Count > 0)
        {
            EnsureBufferSize(ref _textureVertexBuffer, (uint)_textureVerticesList.Count * (uint)Marshal.SizeOf<VectorVertex>(), BufferUsage.Vertex);
            _textureVertexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textureVerticesList));
        }
        if (_textureIndicesList.Count > 0)
        {
            EnsureBufferSize(ref _textureIndexBuffer, (uint)_textureIndicesList.Count * 4, BufferUsage.Index);
            _textureIndexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textureIndicesList));
        }

        var uniformsData = new GpuUniforms
        {
            Projection = projection,
            Mvp = _hasGpuTransformsInFrame ? Matrix4x4.Identity : projection,
            View = _hasGpuTransformsInFrame ? _gpuTransformsCameraView : Matrix4x4.Identity
        };
        _uniformBuffer.WriteSingle(uniformsData);
        if (_activeBrushes.Count > 0)
        {
            _brushesStorageBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_activeBrushes));
        }
        _pathAtlas.RasterizePendingPaths();

        // Render target view for offscreen GpuTexture
        var targetView = targetTexture.ViewPtr;

        // Render pass for offscreen (1x MSAA)
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Offscreen Compositor Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        // Clear with transparent color
        var colorAttachment = new RenderPassColorAttachment
        {
            View = targetView,
            ResolveTarget = null,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 0, G = 0, B = 0, A = 0 }
        };

        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = null
        };

        var pass = _context.Wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);

        DrawCallType? currentType = null;
        var textureEntries = stackalloc BindGroupEntry[2];

        foreach (var dc in _drawCalls)
        {
            if (dc.Type == DrawCallType.Vector)
            {
                if (currentType != DrawCallType.Vector)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, _vectorPipelineOffscreen);
                    fixed (BindGroup** pGrp = &_vectorUniformBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                    }
                    var buffer = _vectorVertexBuffer.BufferPtr;
                    _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _vectorVertexBuffer.Size);
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _vectorIndexBuffer.Size);
                    currentType = DrawCallType.Vector;
                }
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Text)
            {
                if (currentType != DrawCallType.Text)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, _textPipelineOffscreen);
                    fixed (BindGroup** pGrp = &_textUniformBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pAtlas = &_atlasBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
                    }
                    var buffer = _textVertexBuffer.BufferPtr;
                    _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textVertexBuffer.Size);
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textIndexBuffer.Size);
                    currentType = DrawCallType.Text;
                }
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Texture && dc.Texture != null)
            {
                _context.Wgpu.RenderPassEncoderSetPipeline(pass, _texturePipelineOffscreen);
                fixed (BindGroup** pGrp = &_textureUniformBindGroupOffscreen)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                }
                var buffer = _textureVertexBuffer.BufferPtr;
                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textureVertexBuffer.Size);
                _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textureIndexBuffer.Size);
                currentType = DrawCallType.Texture;

                var viewPtr = dc.Texture.ViewPtr;
                var cacheKey = new TextureCacheKey(dc.Texture.Id, dc.Texture.Generation, isOffscreen: true);

                if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out var cachedBg))
                {
                    textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = _atlasSampler };
                    textureEntries[1] = new BindGroupEntry { Binding = 1, TextureView = viewPtr };

                    var bgDesc = new BindGroupDescriptor { Layout = _textureBindGroupLayoutOffscreen, EntryCount = 2, Entries = textureEntries };
                    var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);
                    cachedBg = new CachedBindGroup((nint)bg, _frameNumber);
                    _persistentTextureBindGroups[cacheKey] = cachedBg;
                }
                else
                {
                    cachedBg.LastUsedFrame = _frameNumber;
                }

                var bindGroup = (BindGroup*)cachedBg.BindGroupPtr;
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.StaticDxf && dc.StaticBuffer != null)
            {
                DrawStaticDxfBuffer(pass, dc.StaticBuffer, isOffscreen: true);
                currentType = DrawCallType.StaticDxf;
            }
            else if (dc.Type == DrawCallType.ChartLine)
            {
                RenderChartLine(pass, dc, isOffscreen: true);
                currentType = DrawCallType.ChartLine;
            }
            else if (dc.Type == DrawCallType.ChartScatter)
            {
                RenderChartScatter(pass, dc, isOffscreen: true);
                currentType = DrawCallType.ChartScatter;
            }
        }

        _context.Wgpu.RenderPassEncoderEnd(pass);
        _context.Wgpu.RenderPassEncoderRelease(pass);

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Offscreen Compositor Command Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        EvictUnusedBindGroups();

        // Restore main lists and state
        _vectorVerticesList.Clear(); _vectorVerticesList.AddRange(savedVectorVertices);
        _vectorIndicesList.Clear(); _vectorIndicesList.AddRange(savedVectorIndices);
        _textVerticesList.Clear(); _textVerticesList.AddRange(savedTextVertices);
        _textIndicesList.Clear(); _textIndicesList.AddRange(savedTextIndices);
        _textureVerticesList.Clear(); _textureVerticesList.AddRange(savedTextureVertices);
        _textureIndicesList.Clear(); _textureIndicesList.AddRange(savedTextureIndices);
        _drawCalls.Clear(); _drawCalls.AddRange(savedDrawCalls);
        _activeBrushes.Clear(); _activeBrushes.AddRange(savedActiveBrushes);
        _clipStack.Clear();
        foreach (var clip in savedClipStack) _clipStack.Push(clip);
        _activeClipRect = savedActiveClipRect;
        _opacityStack.Clear();
        for (int i = savedOpacityStack.Length - 1; i >= 0; i--)
        {
            _opacityStack.Push(savedOpacityStack[i]);
        }
        _activeOpacity = savedActiveOpacity;
        _pendingVectorStart = savedPendingVectorStart;
        _pendingTextStart = savedPendingTextStart;

        _useGpuTransformsActive = savedUseGpuTransformsActive;
        _cameraViewMatrix = savedCameraViewMatrix;
        _hasGpuTransformsInFrame = savedHasGpuTransformsInFrame;
        _gpuTransformsCameraView = savedGpuTransformsCameraView;

        _currentWidth = savedWidth;
        _currentHeight = savedHeight;
        _currentProjection = savedProjection;
    }

    public DxfStaticBuffer CompileStaticDxf(List<RenderCommand> commands)
    {
        // Save current lists
        var savedVectorVertices = _vectorVerticesList.ToArray();
        var savedVectorIndices = _vectorIndicesList.ToArray();
        var savedTextVertices = _textVerticesList.ToArray();
        var savedTextIndices = _textIndicesList.ToArray();
        var savedActiveBrushes = _activeBrushes.ToArray();
        var savedHatchRecords = _hatchRecordsList.ToArray();
        var savedHatchSegments = _hatchSegmentsList.ToArray();
        var savedAcisRecords = _acisRecordsList.ToArray();
        var savedAcisEdges = _acisEdgesList.ToArray();

        var savedActiveClipRect = _activeClipRect;
        var savedClipStack = _clipStack.ToArray();

        _activeClipRect = null;
        _clipStack.Clear();
        
        // Clear for compilation
        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _textIndicesList.Clear();
        _activeBrushes.Clear();
        _hatchRecordsList.Clear();
        _hatchSegmentsList.Clear();
        _acisRecordsList.Clear();
        _acisEdgesList.Clear();
        
        foreach (var cmd in commands)
        {
            bool savedUseGpuTransformsActive = _useGpuTransformsActive;
            if (cmd.UseGpuTransforms)
            {
                _useGpuTransformsActive = true;
            }

            switch (cmd.Type)
            {
                case RenderCommandType.DrawRect:
                    CompileRectCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawPath:
                    CompilePathCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawHatch:
                    CompileHatchCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawAcisSolid:
                    CompileAcisCommand(null, cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawText:
                    CompileTextCommand(cmd, null, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawTexture:
                    CompileTextureCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.PushClip:
                    PushClipRect(cmd.Rect, Matrix4x4.Identity);
                    break;
                case RenderCommandType.PopClip:
                    PopClipRect();
                    break;
                case RenderCommandType.DrawLine:
                    CompileLineCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawLine3D:
                    CompileLine3DCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawEllipse:
                    CompileEllipseCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawCircle:
                    CompileCircleCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawRoundedRect:
                    CompileRoundedRectCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawBezier:
                    CompileBezierCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawCubicBezier:
                    CompileCubicBezierCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawPolyline:
                    CompilePolylineCommand(null, cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawSpline:
                    CompileSplineCommand(null, cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.FillTriangle:
                    CompileFillTriangleCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.FillQuad:
                    CompileFillQuadCommand(cmd, Matrix4x4.Identity);
                    break;
            }

            _useGpuTransformsActive = savedUseGpuTransformsActive;
        }
        for (int i = 0; i < _vectorVerticesList.Count; i++)
        {
            var v = _vectorVerticesList[i];
            v.ShapeType += 200f;
            _vectorVerticesList[i] = v;
        }
        for (int i = 0; i < _textVerticesList.Count; i++)
        {
            var v = _textVerticesList[i];
            v.ShapeType += 200f;
            _textVerticesList[i] = v;
        }

        var staticBuffer = new DxfStaticBuffer(
            _context,
            _vectorVerticesList.ToArray(),
            _vectorIndicesList.ToArray(),
            _textVerticesList.ToArray(),
            _textIndicesList.ToArray(),
            _activeBrushes.ToArray(),
            _hatchRecordsList.ToArray(),
            _hatchSegmentsList.ToArray(),
            _acisRecordsList.ToArray(),
            _acisEdgesList.ToArray()
        );
        
        staticBuffer.InitializeBindGroups(
            _vectorUniformBindGroupLayout,
            _vectorUniformBindGroupLayoutOffscreen,
            _textUniformBindGroupLayout,
            _textUniformBindGroupLayoutOffscreen
        );
        
        // Restore dynamic lists
        _vectorVerticesList.Clear(); _vectorVerticesList.AddRange(savedVectorVertices);
        _vectorIndicesList.Clear(); _vectorIndicesList.AddRange(savedVectorIndices);
        _textVerticesList.Clear(); _textVerticesList.AddRange(savedTextVertices);
        _textIndicesList.Clear(); _textIndicesList.AddRange(savedTextIndices);
        _activeBrushes.Clear(); _activeBrushes.AddRange(savedActiveBrushes);
        _hatchRecordsList.Clear(); _hatchRecordsList.AddRange(savedHatchRecords);
        _hatchSegmentsList.Clear(); _hatchSegmentsList.AddRange(savedHatchSegments);
        _acisRecordsList.Clear(); _acisRecordsList.AddRange(savedAcisRecords);
        _acisEdgesList.Clear(); _acisEdgesList.AddRange(savedAcisEdges);
        
        _activeClipRect = savedActiveClipRect;
        _clipStack.Clear();
        for (int i = savedClipStack.Length - 1; i >= 0; i--)
        {
            _clipStack.Push(savedClipStack[i]);
        }
        
        return staticBuffer;
    }

    public DxfStaticBuffer CompileStaticDxf(DrawingContext context)
    {
        // Save current lists
        var savedVectorVertices = _vectorVerticesList.ToArray();
        var savedVectorIndices = _vectorIndicesList.ToArray();
        var savedTextVertices = _textVerticesList.ToArray();
        var savedTextIndices = _textIndicesList.ToArray();
        var savedActiveBrushes = _activeBrushes.ToArray();
        var savedHatchRecords = _hatchRecordsList.ToArray();
        var savedHatchSegments = _hatchSegmentsList.ToArray();
        var savedAcisRecords = _acisRecordsList.ToArray();
        var savedAcisEdges = _acisEdgesList.ToArray();

        var savedActiveClipRect = _activeClipRect;
        var savedClipStack = _clipStack.ToArray();

        _activeClipRect = null;
        _clipStack.Clear();
        
        // Clear for compilation
        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _textIndicesList.Clear();
        _activeBrushes.Clear();
        _hatchRecordsList.Clear();
        _hatchSegmentsList.Clear();
        _acisRecordsList.Clear();
        _acisEdgesList.Clear();
        
        foreach (var cmd in context.Commands)
        {
            bool savedUseGpuTransformsActive = _useGpuTransformsActive;
            if (cmd.UseGpuTransforms)
            {
                _useGpuTransformsActive = true;
            }

            switch (cmd.Type)
            {
                case RenderCommandType.DrawRect:
                    CompileRectCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawPath:
                    CompilePathCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawHatch:
                    CompileHatchCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawAcisSolid:
                    CompileAcisCommand(context, cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawText:
                    CompileTextCommand(cmd, null, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawTexture:
                    CompileTextureCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.PushClip:
                    PushClipRect(cmd.Rect, Matrix4x4.Identity);
                    break;
                case RenderCommandType.PopClip:
                    PopClipRect();
                    break;
                case RenderCommandType.DrawLine:
                    CompileLineCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawLine3D:
                    CompileLine3DCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawEllipse:
                    CompileEllipseCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawCircle:
                    CompileCircleCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawRoundedRect:
                    CompileRoundedRectCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawBezier:
                    CompileBezierCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawCubicBezier:
                    CompileCubicBezierCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawPolyline:
                    CompilePolylineCommand(context, cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawSpline:
                    CompileSplineCommand(context, cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.FillTriangle:
                    CompileFillTriangleCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.FillQuad:
                    CompileFillQuadCommand(cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawGpuLineSeries:
                    CompileGpuLineSeriesCommand(context, cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawGpuScatterSeries:
                    CompileGpuScatterSeriesCommand(context, cmd, Matrix4x4.Identity);
                    break;
                case RenderCommandType.DrawPicture:
                    CompilePicture(context, cmd.Picture, Matrix4x4.Identity);
                    break;
            }

            _useGpuTransformsActive = savedUseGpuTransformsActive;
        }
        for (int i = 0; i < _vectorVerticesList.Count; i++)
        {
            var v = _vectorVerticesList[i];
            v.ShapeType += 200f;
            _vectorVerticesList[i] = v;
        }
        for (int i = 0; i < _textVerticesList.Count; i++)
        {
            var v = _textVerticesList[i];
            v.ShapeType += 200f;
            _textVerticesList[i] = v;
        }

        var staticBuffer = new DxfStaticBuffer(
            _context,
            _vectorVerticesList.ToArray(),
            _vectorIndicesList.ToArray(),
            _textVerticesList.ToArray(),
            _textIndicesList.ToArray(),
            _activeBrushes.ToArray(),
            _hatchRecordsList.ToArray(),
            _hatchSegmentsList.ToArray(),
            _acisRecordsList.ToArray(),
            _acisEdgesList.ToArray()
        );
        
        staticBuffer.InitializeBindGroups(
            _vectorUniformBindGroupLayout,
            _vectorUniformBindGroupLayoutOffscreen,
            _textUniformBindGroupLayout,
            _textUniformBindGroupLayoutOffscreen
        );
        
        // Restore dynamic lists
        _vectorVerticesList.Clear(); _vectorVerticesList.AddRange(savedVectorVertices);
        _vectorIndicesList.Clear(); _vectorIndicesList.AddRange(savedVectorIndices);
        _textVerticesList.Clear(); _textVerticesList.AddRange(savedTextVertices);
        _textIndicesList.Clear(); _textIndicesList.AddRange(savedTextIndices);
        _activeBrushes.Clear(); _activeBrushes.AddRange(savedActiveBrushes);
        _hatchRecordsList.Clear(); _hatchRecordsList.AddRange(savedHatchRecords);
        _hatchSegmentsList.Clear(); _hatchSegmentsList.AddRange(savedHatchSegments);
        _acisRecordsList.Clear(); _acisRecordsList.AddRange(savedAcisRecords);
        _acisEdgesList.Clear(); _acisEdgesList.AddRange(savedAcisEdges);
        
        _activeClipRect = savedActiveClipRect;
        _clipStack.Clear();
        for (int i = savedClipStack.Length - 1; i >= 0; i--)
        {
            _clipStack.Push(savedClipStack[i]);
        }
        
        return staticBuffer;
    }

    private unsafe void DrawStaticDxfBuffer(RenderPassEncoder* pass, object staticBufferObj, bool isOffscreen)
    {
        if (staticBufferObj is not DxfStaticBuffer sb) return;
        
        // 1. Draw static vectors
        if (sb.VertexBuffer != null && sb.IndexBuffer != null && sb.IndexCount > 0)
        {
            var pipeline = isOffscreen ? _vectorPipelineOffscreen : _vectorPipeline;
            var uniformBg = isOffscreen ? sb.UniformBindGroupOffscreen : sb.UniformBindGroup;
            var pathAtlasBg = isOffscreen ? _pathAtlasBindGroupOffscreen : _pathAtlasBindGroup;
            
            _context.Wgpu.RenderPassEncoderSetPipeline(pass, pipeline);
            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, uniformBg, 0, null);
            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, pathAtlasBg, 0, null);
            
            var buffer = sb.VertexBuffer.BufferPtr;
            _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, sb.VertexBuffer.Size);
            _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, sb.IndexBuffer.BufferPtr, IndexFormat.Uint32, 0, sb.IndexBuffer.Size);
            _context.Wgpu.RenderPassEncoderDrawIndexed(pass, sb.IndexCount, 1, 0, 0, 0);
        }
        
        // 2. Draw static text
        if (sb.TextVertexBuffer != null && sb.TextIndexBuffer != null && sb.TextIndexCount > 0)
        {
            var pipeline = isOffscreen ? _textPipelineOffscreen : _textPipeline;
            var uniformBg = isOffscreen ? sb.TextUniformBindGroupOffscreen : sb.TextUniformBindGroup;
            var atlasBg = isOffscreen ? _atlasBindGroupOffscreen : _atlasBindGroup;
            
            _context.Wgpu.RenderPassEncoderSetPipeline(pass, pipeline);
            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, uniformBg, 0, null);
            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, atlasBg, 0, null);
            
            var buffer = sb.TextVertexBuffer.BufferPtr;
            _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, sb.TextVertexBuffer.Size);
            _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, sb.TextIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, sb.TextIndexBuffer.Size);
            _context.Wgpu.RenderPassEncoderDrawIndexed(pass, sb.TextIndexCount, 1, 0, 0, 0);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LineVsUniforms
    {
        public Matrix4x4 Transform;
        public Vector2 CanvasSize;
        public float DevicePixelRatio;
        public float LineWidthCssPx;
        public Vector2 Scale;
        public Vector2 Translate;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ScatterVsUniforms
    {
        public Matrix4x4 Transform;
        public Vector2 ViewportPx;
        public Vector2 Pad0;
        public Vector2 Scale;
        public Vector2 Translate;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ChartFsUniforms
    {
        public Vector4 Color;
    }

    private void CompileGpuLineSeriesCommand(IRenderDataProvider? provider, RenderCommand cmd, Matrix4x4 transform)
    {
        CommitPendingDrawCalls();
        object? staticBuffer = cmd.StaticBuffer;
        if (staticBuffer == null)
        {
            object? cacheKey = null;
            ReadOnlySpan<float> floatsSpan = default;
            int pointsCount = cmd.GpuPointsCount;

            if (cmd.SeriesCacheKey != null)
            {
                cacheKey = cmd.SeriesCacheKey;
            }
            else if (provider is GpuPicture picture)
            {
                cacheKey = picture.FloatBuffer;
            }
            else if (cmd.GpuPoints != null)
            {
                cacheKey = cmd.GpuPoints;
            }

            if (provider != null)
            {
                floatsSpan = provider.GetFloats(cmd.FloatBufferOffset, cmd.FloatBufferCount);
            }
            else if (cmd.GpuPoints != null)
            {
                floatsSpan = cmd.GpuPoints;
            }

            if (cacheKey != null)
            {
                if (!_dynamicGpuBufferCache.TryGetValue(cacheKey, out var cachedBuffer))
                {
                    cachedBuffer = new GpuSeriesBuffer();
                    _dynamicGpuBufferCache.Add(cacheKey, cachedBuffer);
                }

                int requiredLength = pointsCount * 2;
                bool needsUpload = cachedBuffer.PointsCount != pointsCount || cachedBuffer.Buffer == null;

                if (!needsUpload)
                {
                    if (cachedBuffer.CachedInterleaved == null || cachedBuffer.CachedInterleaved.Length < requiredLength)
                    {
                        needsUpload = true;
                    }
                    else
                    {
                        var cachedSpan = new ReadOnlySpan<float>(cachedBuffer.CachedInterleaved, 0, requiredLength);
                        if (!floatsSpan.Slice(0, requiredLength).SequenceEqual(cachedSpan))
                        {
                            needsUpload = true;
                        }
                    }
                }

                if (needsUpload)
                {
                    if (cachedBuffer.CachedInterleaved == null || cachedBuffer.CachedInterleaved.Length < requiredLength)
                    {
                        cachedBuffer.CachedInterleaved = new float[requiredLength];
                    }
                    floatsSpan.Slice(0, requiredLength).CopyTo(cachedBuffer.CachedInterleaved);
                    cachedBuffer.Upload(cachedBuffer.CachedInterleaved, pointsCount);
                }
                staticBuffer = cachedBuffer;
            }
            else if (!floatsSpan.IsEmpty)
            {
                // Uncached fallback path: upload direct
                var tempBuffer = new GpuSeriesBuffer();
                var array = new float[pointsCount * 2];
                floatsSpan.Slice(0, pointsCount * 2).CopyTo(array);
                tempBuffer.Upload(array, pointsCount);
                staticBuffer = tempBuffer;
            }
        }

        _drawCalls.Add(new CompositorDrawCall
        {
            Type = DrawCallType.ChartLine,
            StaticBuffer = staticBuffer,
            Transform = transform,
            LineThicknessOrRadius = cmd.RadiusX,
            Color = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f),
            Scale = cmd.Scale,
            Translate = cmd.Translate,
            ClipRect = _activeClipRect
        });
        _pendingVectorStart = (uint)_vectorIndicesList.Count;
        _pendingTextStart = (uint)_textIndicesList.Count;
    }

    private void CompileGpuScatterSeriesCommand(IRenderDataProvider? provider, RenderCommand cmd, Matrix4x4 transform)
    {
        CommitPendingDrawCalls();
        object? staticBuffer = cmd.StaticBuffer;
        if (staticBuffer == null)
        {
            object? cacheKey = null;
            ReadOnlySpan<float> floatsSpan = default;
            int pointsCount = cmd.GpuPointsCount;

            if (cmd.SeriesCacheKey != null)
            {
                cacheKey = cmd.SeriesCacheKey;
            }
            else if (provider is GpuPicture picture)
            {
                cacheKey = picture.FloatBuffer;
            }
            else if (cmd.GpuPoints != null)
            {
                cacheKey = cmd.GpuPoints;
            }

            if (provider != null)
            {
                floatsSpan = provider.GetFloats(cmd.FloatBufferOffset, cmd.FloatBufferCount);
            }
            else if (cmd.GpuPoints != null)
            {
                floatsSpan = cmd.GpuPoints;
            }

            if (cacheKey != null)
            {
                if (!_dynamicGpuBufferCache.TryGetValue(cacheKey, out var cachedBuffer))
                {
                    cachedBuffer = new GpuSeriesBuffer();
                    _dynamicGpuBufferCache.Add(cacheKey, cachedBuffer);
                }

                int requiredLength = pointsCount * 3;
                bool needsUpload = cachedBuffer.PointsCount != pointsCount || cachedBuffer.Buffer == null;

                if (!needsUpload)
                {
                    if (cachedBuffer.CachedInterleaved == null || cachedBuffer.CachedInterleaved.Length < requiredLength)
                    {
                        needsUpload = true;
                    }
                    else
                    {
                        // Check if incoming was originally 2D coords but we expand to 3D by adding radius
                        bool isOriginally2D = floatsSpan.Length == pointsCount * 2;
                        if (isOriginally2D)
                        {
                            float radiusVal = cmd.RadiusX;
                            if (cachedBuffer.CachedInterleaved[2] != radiusVal)
                            {
                                needsUpload = true;
                            }
                            else
                            {
                                for (int i = 0; i < pointsCount; i++)
                                {
                                    if (floatsSpan[i * 2] != cachedBuffer.CachedInterleaved[i * 3] ||
                                        floatsSpan[i * 2 + 1] != cachedBuffer.CachedInterleaved[i * 3 + 1])
                                    {
                                        needsUpload = true;
                                        break;
                                    }
                                }
                            }
                        }
                        else
                        {
                            var cachedSpan = new ReadOnlySpan<float>(cachedBuffer.CachedInterleaved, 0, requiredLength);
                            if (!floatsSpan.Slice(0, requiredLength).SequenceEqual(cachedSpan))
                            {
                                needsUpload = true;
                            }
                        }
                    }
                }

                if (needsUpload)
                {
                    if (cachedBuffer.CachedInterleaved == null || cachedBuffer.CachedInterleaved.Length < requiredLength)
                    {
                        cachedBuffer.CachedInterleaved = new float[requiredLength];
                    }

                    bool isOriginally2D = floatsSpan.Length == pointsCount * 2;
                    if (isOriginally2D)
                    {
                        float radiusVal = cmd.RadiusX;
                        int srcIdx = 0;
                        int destIdx = 0;
                        for (int i = 0; i < pointsCount; i++)
                        {
                            cachedBuffer.CachedInterleaved[destIdx++] = floatsSpan[srcIdx++];
                            cachedBuffer.CachedInterleaved[destIdx++] = floatsSpan[srcIdx++];
                            cachedBuffer.CachedInterleaved[destIdx++] = radiusVal;
                        }
                    }
                    else
                    {
                        floatsSpan.Slice(0, requiredLength).CopyTo(cachedBuffer.CachedInterleaved);
                    }

                    cachedBuffer.Upload(cachedBuffer.CachedInterleaved, pointsCount);
                }
                staticBuffer = cachedBuffer;
            }
            else if (!floatsSpan.IsEmpty)
            {
                // Uncached fallback path: upload direct
                var tempBuffer = new GpuSeriesBuffer();
                var array = new float[pointsCount * 3];
                bool isOriginally2D = floatsSpan.Length == pointsCount * 2;
                if (isOriginally2D)
                {
                    float radiusVal = cmd.RadiusX;
                    int srcIdx = 0;
                    int destIdx = 0;
                    for (int i = 0; i < pointsCount; i++)
                    {
                        array[destIdx++] = floatsSpan[srcIdx++];
                        array[destIdx++] = floatsSpan[srcIdx++];
                        array[destIdx++] = radiusVal;
                    }
                }
                else
                {
                    floatsSpan.Slice(0, pointsCount * 3).CopyTo(array);
                }
                tempBuffer.Upload(array, pointsCount);
                staticBuffer = tempBuffer;
            }
        }

        _drawCalls.Add(new CompositorDrawCall
        {
            Type = DrawCallType.ChartScatter,
            StaticBuffer = staticBuffer,
            Transform = transform,
            LineThicknessOrRadius = cmd.RadiusX,
            Color = (cmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f),
            Scale = cmd.Scale,
            Translate = cmd.Translate,
            ClipRect = _activeClipRect
        });
        _pendingVectorStart = (uint)_vectorIndicesList.Count;
        _pendingTextStart = (uint)_textIndicesList.Count;
    }

    private unsafe void RenderChartLine(RenderPassEncoder* pass, CompositorDrawCall dc, bool isOffscreen)
    {
        if (dc.StaticBuffer is not GpuSeriesBuffer seriesBuffer || seriesBuffer.Buffer == null || seriesBuffer.PointsCount < 2) return;

        var wgpu = _context.Wgpu;
        var device = _context.Device;

        if (seriesBuffer.VsUniformBuffer == null)
        {
            seriesBuffer.VsUniformBuffer = new GpuBuffer(_context, 96, BufferUsage.Uniform | BufferUsage.CopyDst, "ChartLine VS Uniforms");
        }
        if (seriesBuffer.FsUniformBuffer == null)
        {
            seriesBuffer.FsUniformBuffer = new GpuBuffer(_context, 16, BufferUsage.Uniform | BufferUsage.CopyDst, "ChartLine FS Uniforms");
        }

        var vsUniforms = new LineVsUniforms
        {
            Transform = dc.Transform * _currentProjection,
            CanvasSize = new Vector2(_currentWidth, _currentHeight),
            DevicePixelRatio = _currentDpiScale,
            LineWidthCssPx = dc.LineThicknessOrRadius,
            Scale = dc.Scale,
            Translate = dc.Translate
        };
        seriesBuffer.VsUniformBuffer.WriteSingle(vsUniforms);

        var fsUniforms = new ChartFsUniforms
        {
            Color = dc.Color
        };
        seriesBuffer.FsUniformBuffer.WriteSingle(fsUniforms);

        if (seriesBuffer.LineBindGroup == 0)
        {
            var entries = stackalloc BindGroupEntry[3];
            entries[0] = new BindGroupEntry
            {
                Binding = 0,
                Buffer = seriesBuffer.VsUniformBuffer.BufferPtr,
                Offset = 0,
                Size = 96
            };
            entries[1] = new BindGroupEntry
            {
                Binding = 1,
                Buffer = seriesBuffer.FsUniformBuffer.BufferPtr,
                Offset = 0,
                Size = 16
            };
            entries[2] = new BindGroupEntry
            {
                Binding = 2,
                Buffer = seriesBuffer.Buffer.BufferPtr,
                Offset = 0,
                Size = seriesBuffer.Buffer.Size
            };

            var bgDesc = new BindGroupDescriptor
            {
                Layout = _chartLineBindGroupLayout,
                EntryCount = 3,
                Entries = entries,
                Label = (byte*)SilkMarshal.StringToPtr("ChartLine BindGroup")
            };
            var bg = wgpu.DeviceCreateBindGroup(device, &bgDesc);
            SilkMarshal.Free((nint)bgDesc.Label);
            seriesBuffer.LineBindGroup = (nint)bg;
        }

        var pipeline = isOffscreen ? _chartLinePipelineOffscreen : _chartLinePipeline;
        wgpu.RenderPassEncoderSetPipeline(pass, pipeline);
        
        var lineBg = (BindGroup*)seriesBuffer.LineBindGroup;
        wgpu.RenderPassEncoderSetBindGroup(pass, 0, lineBg, 0, null);

        if (dc.ClipRect.HasValue)
        {
            var rect = dc.ClipRect.Value;
            float rx = Math.Max(0f, rect.X);
            float ry = Math.Max(0f, rect.Y);
            float rw = Math.Max(0f, rect.Width);
            float rh = Math.Max(0f, rect.Height);

            uint sx = (uint)Math.Round(rx);
            uint sy = (uint)Math.Round(ry);
            uint sw = (uint)Math.Round(rw);
            uint sh = (uint)Math.Round(rh);

            sw = Math.Max(1u, sw);
            sh = Math.Max(1u, sh);

            sw = Math.Min(sw, _currentWidth - sx);
            sh = Math.Min(sh, _currentHeight - sy);

            wgpu.RenderPassEncoderSetScissorRect(pass, sx, sy, sw, sh);
        }

        uint instanceCount = (uint)(seriesBuffer.PointsCount - 1);
        wgpu.RenderPassEncoderDraw(pass, 6, instanceCount, 0, 0);

        if (dc.ClipRect.HasValue)
        {
            wgpu.RenderPassEncoderSetScissorRect(pass, 0, 0, _currentWidth, _currentHeight);
        }
    }

    private unsafe void RenderChartScatter(RenderPassEncoder* pass, CompositorDrawCall dc, bool isOffscreen)
    {
        if (dc.StaticBuffer is not GpuSeriesBuffer seriesBuffer || seriesBuffer.Buffer == null || seriesBuffer.PointsCount < 1) return;

        var wgpu = _context.Wgpu;
        var device = _context.Device;

        if (seriesBuffer.VsUniformBuffer == null)
        {
            seriesBuffer.VsUniformBuffer = new GpuBuffer(_context, 96, BufferUsage.Uniform | BufferUsage.CopyDst, "ChartScatter VS Uniforms");
        }
        if (seriesBuffer.FsUniformBuffer == null)
        {
            seriesBuffer.FsUniformBuffer = new GpuBuffer(_context, 16, BufferUsage.Uniform | BufferUsage.CopyDst, "ChartScatter FS Uniforms");
        }

        var vsUniforms = new ScatterVsUniforms
        {
            Transform = dc.Transform * _currentProjection,
            ViewportPx = new Vector2(_currentWidth, _currentHeight),
            Pad0 = Vector2.Zero,
            Scale = dc.Scale,
            Translate = dc.Translate
        };
        seriesBuffer.VsUniformBuffer.WriteSingle(vsUniforms);

        var fsUniforms = new ChartFsUniforms
        {
            Color = dc.Color
        };
        seriesBuffer.FsUniformBuffer.WriteSingle(fsUniforms);

        if (seriesBuffer.ScatterBindGroup == 0)
        {
            var entries = stackalloc BindGroupEntry[2];
            entries[0] = new BindGroupEntry
            {
                Binding = 0,
                Buffer = seriesBuffer.VsUniformBuffer.BufferPtr,
                Offset = 0,
                Size = 96
            };
            entries[1] = new BindGroupEntry
            {
                Binding = 1,
                Buffer = seriesBuffer.FsUniformBuffer.BufferPtr,
                Offset = 0,
                Size = 16
            };

            var bgDesc = new BindGroupDescriptor
            {
                Layout = _chartScatterBindGroupLayout,
                EntryCount = 2,
                Entries = entries,
                Label = (byte*)SilkMarshal.StringToPtr("ChartScatter BindGroup")
            };
            var bg = wgpu.DeviceCreateBindGroup(device, &bgDesc);
            SilkMarshal.Free((nint)bgDesc.Label);
            seriesBuffer.ScatterBindGroup = (nint)bg;
        }

        var pipeline = isOffscreen ? _chartScatterPipelineOffscreen : _chartScatterPipeline;
        wgpu.RenderPassEncoderSetPipeline(pass, pipeline);
        
        var scatterBg = (BindGroup*)seriesBuffer.ScatterBindGroup;
        wgpu.RenderPassEncoderSetBindGroup(pass, 0, scatterBg, 0, null);

        var buffer = seriesBuffer.Buffer.BufferPtr;
        wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, seriesBuffer.Buffer.Size);

        if (dc.ClipRect.HasValue)
        {
            var rect = dc.ClipRect.Value;
            float rx = Math.Max(0f, rect.X);
            float ry = Math.Max(0f, rect.Y);
            float rw = Math.Max(0f, rect.Width);
            float rh = Math.Max(0f, rect.Height);

            uint sx = (uint)Math.Round(rx);
            uint sy = (uint)Math.Round(ry);
            uint sw = (uint)Math.Round(rw);
            uint sh = (uint)Math.Round(rh);

            sw = Math.Max(1u, sw);
            sh = Math.Max(1u, sh);

            sw = Math.Min(sw, _currentWidth - sx);
            sh = Math.Min(sh, _currentHeight - sy);

            wgpu.RenderPassEncoderSetScissorRect(pass, sx, sy, sw, sh);
        }

        uint instanceCount = (uint)seriesBuffer.PointsCount;
        wgpu.RenderPassEncoderDraw(pass, 6, instanceCount, 0, 0);

        if (dc.ClipRect.HasValue)
        {
            wgpu.RenderPassEncoderSetScissorRect(pass, 0, 0, _currentWidth, _currentHeight);
        }
    }
}
