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
using ProGPU.Scene.Extensions;
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

[StructLayout(LayoutKind.Explicit, Size = 208)]
public struct GpuUniforms
{
    [FieldOffset(0)] public Matrix4x4 Projection;
    [FieldOffset(64)] public Matrix4x4 Mvp;
    [FieldOffset(128)] public Matrix4x4 View;
    [FieldOffset(192)] public Vector2 CanvasSize;
    [FieldOffset(200)] public float DpiScale;
    [FieldOffset(204)] public float Pad0;
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
    public struct StaticTextRecord
    {
        public RenderCommand Command;
        public Matrix4x4 Transform;
    }

    private readonly List<StaticTextRecord> _compiledTextRecords = new();

    public CompositorMetrics Metrics { get; private set; }

    private readonly List<ICompositorExtension> _registeredExtensions = new();
    private readonly Dictionary<int, ICompositorExtension> _extensionsById = new();

    public void RegisterExtension(int id, ICompositorExtension extension)
    {
        lock (_registeredExtensions)
        {
            _registeredExtensions.Add(extension);
            _extensionsById[id] = extension;
        }
    }

    public ICompositorExtension? GetExtension(int id)
    {
        lock (_registeredExtensions)
        {
            return _extensionsById.TryGetValue(id, out var ext) ? ext : null;
        }
    }

    // Decoupled hooks to remove hard dependency on UI layer
    public event Action<uint, uint>? PreRender;
    public Func<System.Collections.Generic.IReadOnlyList<Visual>>? GetExternalLayers { get; set; }
    public Func<Visual?>? GetTooltip { get; set; }
    public Func<Vector2>? GetMousePosition { get; set; }
    public Action<DrawingContext, uint, uint>? RenderDiagnostics { get; set; }
    public Vector4 ClearColor { get; set; } = new Vector4(0.08f, 0.08f, 0.12f, 1.0f);

    public unsafe BindGroupLayout* VectorUniformBindGroupLayout => _vectorUniformBindGroupLayout;
    public unsafe BindGroupLayout* VectorUniformBindGroupLayoutOffscreen => _vectorUniformBindGroupLayoutOffscreen;

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


    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, GpuSeriesBuffer> _dynamicGpuBufferCache = new();

    // Batch buffers (Dynamic GPU vertex & index buffers)
    private GpuBuffer _vectorVertexBuffer;
    private GpuBuffer _vectorIndexBuffer;
    private GpuBuffer _textVertexBuffer;
    private GpuBuffer _textureVertexBuffer;
    private GpuBuffer _textureIndexBuffer;

    // Masking & Blending state
    private readonly List<GpuTexture> _maskTexturePool = new();
    private readonly Stack<GpuTexture> _maskStack = new();
    private readonly Dictionary<GpuTexture, nint> _maskBindGroups = new();
    private readonly Dictionary<GpuTexture, nint> _maskBindGroupsOffscreen = new();

    private PipelineLayout* _vectorPipelineLayout;
    private PipelineLayout* _textPipelineLayout;
    private PipelineLayout* _texturePipelineLayout;
    private PipelineLayout* _vectorPipelineLayoutOffscreen;
    private PipelineLayout* _textPipelineLayoutOffscreen;
    private PipelineLayout* _texturePipelineLayoutOffscreen;

    private GpuTexture? _dummyMaskTexture;
    private BindGroup* _dummyMaskBindGroup;
    private BindGroup* _dummyMaskBindGroupOffscreen;

    private BindGroupLayout* _maskBindGroupLayout;
    private BindGroupLayout* _maskBindGroupLayoutOffscreen;

    private readonly Stack<GpuBlendMode> _blendModeStack = new();
    private GpuBlendMode _activeBlendMode = GpuBlendMode.SrcOver;

    private readonly List<MaskRenderPassInfo> _maskRenderPasses = new();
    private readonly List<GpuTexture> _masksToReturnToPool = new();

    public enum DrawCallType
    {
        Vector,
        Texture,
        Text,
        StaticDxf,
        ChartLine,
        ChartScatter,
        Extension
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
        public GpuTexture? MaskTexture;
        public GpuBlendMode BlendMode;

        // Custom Extension properties
        public int ExtensionId;
        public int IntParam;
        public float FloatParam;
        public object? DataParam;
        public int PointBufferOffset;
        public int PointBufferCount;
        public int DoubleBufferOffset;
        public int DoubleBufferCount;
        public int WeightBufferOffset;
        public int WeightBufferCount;
        public int FloatBufferOffset;
        public int FloatBufferCount;
        public Brush? Brush;
        public Pen? Pen;
        public PathGeometry? Path;
    }

    private readonly List<VectorVertex> _vectorVerticesList = new();
    private readonly List<uint> _vectorIndicesList = new();
    private readonly List<GlyphInstance> _textVerticesList = new();
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
    private enum BatchType
    {
        None,
        Vector,
        Text
    }
    private BatchType _currentBatchType = BatchType.None;
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
    public List<VectorVertex> VectorVertices => _vectorVerticesList;
    public List<uint> VectorIndices => _vectorIndicesList;
    
    internal WgpuContext Context => _context;
    internal RenderPipelineCache PipelineCache => _pipelineCache;
    internal GpuBuffer VectorVertexBuffer => _vectorVertexBuffer;
    internal GpuBuffer VectorIndexBuffer => _vectorIndexBuffer;
    internal GpuBuffer VectorUniformBuffer => _uniformBuffer;
    internal GpuBuffer BrushesStorageBuffer => _brushesStorageBuffer;
    internal uint CurrentWidth => _currentWidth;
    internal uint CurrentHeight => _currentHeight;
    public float CurrentDpiScale => _currentDpiScale;
    internal Matrix4x4 CurrentProjection => _currentProjection;
    internal System.Runtime.CompilerServices.ConditionalWeakTable<object, GpuSeriesBuffer> DynamicGpuBufferCache => _dynamicGpuBufferCache;
    internal List<CompositorDrawCall> DrawCalls => _drawCalls;
    internal Rect? ActiveClipRect => _activeClipRect;
    internal float ActiveOpacity => _activeOpacity;
    internal uint PendingVectorStart { get => _pendingVectorStart; set => _pendingVectorStart = value; }
    internal uint PendingTextStart { get => _pendingTextStart; set => _pendingTextStart = value; }
    internal BindGroup* VectorUniformBindGroup => _vectorUniformBindGroup;
    internal BindGroup* VectorUniformBindGroupOffscreen => _vectorUniformBindGroupOffscreen;
    internal Sampler* AtlasSampler => _atlasSampler;
    internal ulong FrameNumber => _frameNumber;
    
    internal void CompilePolyline(IRenderDataProvider? provider, RenderCommand cmd, in Matrix4x4 transform) => CompilePolylineCommand(provider, cmd, transform);
    public int VectorIndexCount => _vectorIndicesList.Count;
    public int TextVertexCount => _textVerticesList.Count * 6;
    public int TextIndexCount => _textVerticesList.Count * 6;
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
    
    public StaticCompilationContext? ActiveCompilationContext { get; private set; }

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

        _textVertexBuffer = new GpuBuffer(_context, initialVertexCount * (uint)Marshal.SizeOf<GlyphInstance>(), BufferUsage.Vertex | BufferUsage.CopyDst, "Text Vertex Buffer");

        _textureVertexBuffer = new GpuBuffer(_context, initialVertexCount * vertexStride, BufferUsage.Vertex | BufferUsage.CopyDst, "Texture Vertex Buffer");
        _textureIndexBuffer = new GpuBuffer(_context, initialIndexCount * 4, BufferUsage.Index | BufferUsage.CopyDst, "Texture Index Buffer");

        RegisterExtension(CompositorBuiltInExtensions.StaticDxf, new StaticDxfExtensionPipeline());
        RegisterExtension(CompositorBuiltInExtensions.AcisSolid, new AcisSolidExtensionPipeline());
        RegisterExtension(CompositorBuiltInExtensions.Line3D, new Line3DExtensionPipeline());
        RegisterExtension(CompositorBuiltInExtensions.Hatch, new HatchExtensionPipeline());
        RegisterExtension(CompositorBuiltInExtensions.Spline, new SplineExtensionPipeline());
        RegisterExtension(CompositorBuiltInExtensions.GpuLineSeries, new GpuLineSeriesExtensionPipeline());
        RegisterExtension(CompositorBuiltInExtensions.GpuScatterSeries, new GpuScatterSeriesExtensionPipeline());
        RegisterExtension(CompositorBuiltInExtensions.CustomGrid, new CustomGridExtensionPipeline());
        RegisterExtension(CompositorBuiltInExtensions.Mesh3D, new Mesh3DExtensionPipeline());
        RegisterExtension(CompositorBuiltInExtensions.ImageEffect, new ImageEffectExtensionPipeline());
        RegisterExtension(CompositorBuiltInExtensions.ShaderToy, new ShaderToyExtensionPipeline());

        InitializePipelinesAndBindGroups();
        GpuTexture.OnDisposedWithId += HandleTextureDisposed;
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

        // Create explicit BindGroupLayouts
        _vectorUniformBindGroupLayout = CreateVectorUniformLayout();
        _vectorUniformBindGroupLayoutOffscreen = CreateVectorUniformLayout();
        _textUniformBindGroupLayout = CreateUniformOnlyLayout();
        _textUniformBindGroupLayoutOffscreen = CreateUniformOnlyLayout();
        _textureUniformBindGroupLayout = CreateUniformOnlyLayout();
        _textureUniformBindGroupLayoutOffscreen = CreateUniformOnlyLayout();

        _pathAtlasBindGroupLayout = CreateSamplerTextureLayout();
        _pathAtlasBindGroupLayoutOffscreen = CreateSamplerTextureLayout();
        _atlasBindGroupLayout = CreateSamplerTextureLayout();
        _atlasBindGroupLayoutOffscreen = CreateSamplerTextureLayout();
        _textureBindGroupLayout = CreateSamplerTextureLayout();
        _textureBindGroupLayoutOffscreen = CreateSamplerTextureLayout();
        _maskBindGroupLayout = CreateSamplerTextureLayout();
        _maskBindGroupLayoutOffscreen = CreateSamplerTextureLayout();

        // Create explicit PipelineLayouts to share layouts across dynamic blend pipelines
        var vectorLayouts = stackalloc BindGroupLayout*[3];
        vectorLayouts[0] = _vectorUniformBindGroupLayout;
        vectorLayouts[1] = _pathAtlasBindGroupLayout;
        vectorLayouts[2] = _maskBindGroupLayout;
        var vectorPipelineLayoutDesc = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = vectorLayouts
        };
        _vectorPipelineLayout = _context.Wgpu.DeviceCreatePipelineLayout(_context.Device, &vectorPipelineLayoutDesc);

        var vectorLayoutsOffscreen = stackalloc BindGroupLayout*[3];
        vectorLayoutsOffscreen[0] = _vectorUniformBindGroupLayoutOffscreen;
        vectorLayoutsOffscreen[1] = _pathAtlasBindGroupLayoutOffscreen;
        vectorLayoutsOffscreen[2] = _maskBindGroupLayoutOffscreen;
        var vectorPipelineLayoutDescOffscreen = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = vectorLayoutsOffscreen
        };
        _vectorPipelineLayoutOffscreen = _context.Wgpu.DeviceCreatePipelineLayout(_context.Device, &vectorPipelineLayoutDescOffscreen);

        var textLayouts = stackalloc BindGroupLayout*[3];
        textLayouts[0] = _textUniformBindGroupLayout;
        textLayouts[1] = _atlasBindGroupLayout;
        textLayouts[2] = _maskBindGroupLayout;
        var textPipelineLayoutDesc = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = textLayouts
        };
        _textPipelineLayout = _context.Wgpu.DeviceCreatePipelineLayout(_context.Device, &textPipelineLayoutDesc);

        var textLayoutsOffscreen = stackalloc BindGroupLayout*[3];
        textLayoutsOffscreen[0] = _textUniformBindGroupLayoutOffscreen;
        textLayoutsOffscreen[1] = _atlasBindGroupLayoutOffscreen;
        textLayoutsOffscreen[2] = _maskBindGroupLayoutOffscreen;
        var textPipelineLayoutDescOffscreen = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = textLayoutsOffscreen
        };
        _textPipelineLayoutOffscreen = _context.Wgpu.DeviceCreatePipelineLayout(_context.Device, &textPipelineLayoutDescOffscreen);

        var textureLayouts = stackalloc BindGroupLayout*[3];
        textureLayouts[0] = _textureUniformBindGroupLayout;
        textureLayouts[1] = _textureBindGroupLayout;
        textureLayouts[2] = _maskBindGroupLayout;
        var texturePipelineLayoutDesc = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = textureLayouts
        };
        _texturePipelineLayout = _context.Wgpu.DeviceCreatePipelineLayout(_context.Device, &texturePipelineLayoutDesc);

        var textureLayoutsOffscreen = stackalloc BindGroupLayout*[3];
        textureLayoutsOffscreen[0] = _textureUniformBindGroupLayoutOffscreen;
        textureLayoutsOffscreen[1] = _textureBindGroupLayoutOffscreen;
        textureLayoutsOffscreen[2] = _maskBindGroupLayoutOffscreen;
        var texturePipelineLayoutDescOffscreen = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = textureLayoutsOffscreen
        };
        _texturePipelineLayoutOffscreen = _context.Wgpu.DeviceCreatePipelineLayout(_context.Device, &texturePipelineLayoutDescOffscreen);

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
                sampleCount: 4,
                pipelineLayout: _vectorPipelineLayout
            );

            var textVertexAttribs = new VertexAttribute[]
            {
                new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }, // SnappedLogicalPos
                new() { Format = VertexFormat.Float32x2, Offset = 8, ShaderLocation = 1 }, // BasisX
                new() { Format = VertexFormat.Float32x2, Offset = 16, ShaderLocation = 2 }, // BasisY
                new() { Format = VertexFormat.Float32x4, Offset = 24, ShaderLocation = 3 }, // BearSize
                new() { Format = VertexFormat.Float32x4, Offset = 40, ShaderLocation = 4 }, // TexCoords
                new() { Format = VertexFormat.Float32x4, Offset = 56, ShaderLocation = 5 }, // Color
                new() { Format = VertexFormat.Float32x4, Offset = 72, ShaderLocation = 6 }, // ScaleBoldItalicUseMvp
                new() { Format = VertexFormat.Float32, Offset = 88, ShaderLocation = 7 }    // BrushIndex
            };

            fixed (VertexAttribute* textAttribsPtr = textVertexAttribs)
            {
                var textLayoutDesc = new VertexBufferLayout
                {
                    ArrayStride = (uint)Marshal.SizeOf<GlyphInstance>(),
                    StepMode = VertexStepMode.Instance,
                    AttributeCount = 8,
                    Attributes = textAttribsPtr
                };

                _textPipeline = _pipelineCache.GetOrCreateRenderPipeline(
                    "Text", 
                    textShaderModule, 
                    "vs_main", 
                    "fs_main", 
                    RenderFormat, 
                    PrimitiveTopology.TriangleList, 
                    new[] { textLayoutDesc },
                    enableBlend: true,
                    sampleCount: 4,
                    pipelineLayout: _textPipelineLayout
                );

                _textPipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                    "Text_Offscreen", 
                    textShaderModule, 
                    "vs_main", 
                    "fs_main", 
                    RenderFormat, 
                    PrimitiveTopology.TriangleList, 
                    new[] { textLayoutDesc },
                    enableBlend: true,
                    sampleCount: 1,
                    pipelineLayout: _textPipelineLayoutOffscreen
                );
            }

            _texturePipeline = _pipelineCache.GetOrCreateRenderPipeline(
                "Texture", 
                texShaderModule, 
                "vs_main", 
                "fs_main", 
                RenderFormat, 
                PrimitiveTopology.TriangleList, 
                new[] { layoutDesc },
                enableBlend: true,
                sampleCount: 4,
                pipelineLayout: _texturePipelineLayout
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
                sampleCount: 1,
                pipelineLayout: _vectorPipelineLayoutOffscreen
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
                sampleCount: 1,
                pipelineLayout: _texturePipelineLayoutOffscreen
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

        var vectorEntries = stackalloc BindGroupEntry[2];
        vectorEntries[0] = uBufferEntryVector;
        vectorEntries[1] = brushesEntry;

        var uDescVector = new BindGroupDescriptor
        {
            Layout = _vectorUniformBindGroupLayout,
            EntryCount = 2,
            Entries = vectorEntries
        };
        _vectorUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescVector);

        var uDescVectorOffscreen = new BindGroupDescriptor
        {
            Layout = _vectorUniformBindGroupLayoutOffscreen,
            EntryCount = 2,
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

        _dummyMaskTexture = new GpuTexture(
            _context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Dummy Mask Texture"
        );
        byte[] dummyData = new byte[] { 255, 255, 255, 255 };
        _dummyMaskTexture.WritePixels(new ReadOnlySpan<byte>(dummyData));

        var maskEntries = stackalloc BindGroupEntry[2];
        maskEntries[0] = new BindGroupEntry { Binding = 0, Sampler = _atlasSampler };
        maskEntries[1] = new BindGroupEntry { Binding = 1, TextureView = _dummyMaskTexture.ViewPtr };

        var bgDescMask = new BindGroupDescriptor
        {
            Layout = _maskBindGroupLayout,
            EntryCount = 2,
            Entries = maskEntries
        };
        _dummyMaskBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDescMask);

        var bgDescMaskOffscreen = new BindGroupDescriptor
        {
            Layout = _maskBindGroupLayoutOffscreen,
            EntryCount = 2,
            Entries = maskEntries
        };
        _dummyMaskBindGroupOffscreen = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDescMaskOffscreen);
    }

    public void RenderScene(Visual root, uint width, uint height, TextureView* targetView)
    {
        if (_isDisposed) return;
        
        _currentWidth = width;
        _currentHeight = height;
        _currentDpiScale = 1.0f;
        if (_context.Window != null && width == (uint)_context.Window.Size.X && height == (uint)_context.Window.Size.Y)
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
        _textureVerticesList.Clear();
        _textureIndicesList.Clear();
        _drawCalls.Clear();


        if (_layoutCache.Count > 1000)
        {
            _layoutCache.Clear();
        }

        _clipStack.Clear();
        _activeClipRect = null;

        _opacityStack.Clear();
        _activeOpacity = 1.0f;
        _currentBatchType = BatchType.None;

        _blendModeStack.Clear();
        _activeBlendMode = GpuBlendMode.SrcOver;
        _maskStack.Clear();
        _maskRenderPasses.Clear();
        _masksToReturnToPool.Clear();

        lock (_registeredExtensions)
        {
            foreach (var ext in _registeredExtensions)
            {
                ext.BeginFrame(this);
            }
        }

        // 3. Compile Layer 0: Root Visual Scene
        _pendingVectorStart = (uint)_vectorIndicesList.Count;
        _pendingTextStart = (uint)_textVerticesList.Count;
        CompileVisualTree(root, Matrix4x4.Identity);
        CommitPendingDrawCalls();

        // 4. Compile Layer 1: Active Popups / External Layers (in proper Z-order)
        var externalLayers = GetExternalLayers?.Invoke();
        if (externalLayers != null && externalLayers.Count > 0)
        {
            var savedActiveClipRect = _activeClipRect;
            var savedClipStack = _clipStack.ToArray();
            var savedActiveOpacity = _activeOpacity;
            var savedOpacityStack = _opacityStack.ToArray();

            for (int i = 0; i < externalLayers.Count; i++)
            {
                _activeClipRect = null;
                _clipStack.Clear();
                _activeOpacity = 1.0f;
                _opacityStack.Clear();

                var layer = externalLayers[i];
                _pendingVectorStart = (uint)_vectorIndicesList.Count;
                _pendingTextStart = (uint)_textVerticesList.Count;
                
                CompileVisualTree(layer, Matrix4x4.Identity);
                CommitPendingDrawCalls();
            }

            _activeClipRect = savedActiveClipRect;
            _clipStack.Clear();
            for (int j = savedClipStack.Length - 1; j >= 0; j--)
            {
                _clipStack.Push(savedClipStack[j]);
            }
            _activeOpacity = savedActiveOpacity;
            _opacityStack.Clear();
            for (int j = savedOpacityStack.Length - 1; j >= 0; j--)
            {
                _opacityStack.Push(savedOpacityStack[j]);
            }
        }

        // 5. Compile Layer 2: Tooltips
        var activeToolTip = GetTooltip?.Invoke();
        if (activeToolTip != null)
        {
            var savedActiveClipRect = _activeClipRect;
            var savedClipStack = _clipStack.ToArray();
            var savedActiveOpacity = _activeOpacity;
            var savedOpacityStack = _opacityStack.ToArray();

            _activeClipRect = null;
            _clipStack.Clear();
            _activeOpacity = 1.0f;
            _opacityStack.Clear();

            _pendingVectorStart = (uint)_vectorIndicesList.Count;
            _pendingTextStart = (uint)_textVerticesList.Count;
            
            CompileVisualTree(activeToolTip, Matrix4x4.Identity);
            CommitPendingDrawCalls();

            _activeClipRect = savedActiveClipRect;
            _clipStack.Clear();
            for (int j = savedClipStack.Length - 1; j >= 0; j--)
            {
                _clipStack.Push(savedClipStack[j]);
            }
            _activeOpacity = savedActiveOpacity;
            _opacityStack.Clear();
            for (int j = savedOpacityStack.Length - 1; j >= 0; j--)
            {
                _opacityStack.Push(savedOpacityStack[j]);
            }
        }

        // 6. Compile Layer 3: Adorner / DevTools bounds highlights
        if (RenderDiagnostics != null)
        {
            var savedActiveClipRect = _activeClipRect;
            var savedClipStack = _clipStack.ToArray();
            var savedActiveOpacity = _activeOpacity;
            var savedOpacityStack = _opacityStack.ToArray();

            _activeClipRect = null;
            _clipStack.Clear();
            _activeOpacity = 1.0f;
            _opacityStack.Clear();

            _pendingVectorStart = (uint)_vectorIndicesList.Count;
            _pendingTextStart = (uint)_textVerticesList.Count;

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
                            StaticBuffer = cmd.StaticBuffer,
                            ClipRect = _activeClipRect
                        });
                        _pendingVectorStart = (uint)_vectorIndicesList.Count;
                        _pendingTextStart = (uint)_textVerticesList.Count;
                        break;
                }
            }
            CommitPendingDrawCalls();

            _activeClipRect = savedActiveClipRect;
            _clipStack.Clear();
            for (int j = savedClipStack.Length - 1; j >= 0; j--)
            {
                _clipStack.Push(savedClipStack[j]);
            }
            _activeOpacity = savedActiveOpacity;
            _opacityStack.Clear();
            for (int j = savedOpacityStack.Length - 1; j >= 0; j--)
            {
                _opacityStack.Push(savedOpacityStack[j]);
            }
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
            EnsureBufferSize(ref _textVertexBuffer, (uint)_textVerticesList.Count * (uint)Marshal.SizeOf<GlyphInstance>(), BufferUsage.Vertex);
            _textVertexBuffer.Write(CollectionsMarshal.AsSpan(_textVerticesList));
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

        // Determine physical render target size for MSAA matching the physical FramebufferSize.
        uint renderWidth = width;
        uint renderHeight = height;
        if (_context.Window != null && width == (uint)_context.Window.Size.X && height == (uint)_context.Window.Size.Y)
        {
            renderWidth = (uint)_context.Window.FramebufferSize.X;
            renderHeight = (uint)_context.Window.FramebufferSize.Y;
        }

        // Upload unified projection and MVP matrices
        var uniformsData = new GpuUniforms
        {
            Projection = projection,
            Mvp = _hasGpuTransformsInFrame ? Matrix4x4.Identity : projection,
            View = _hasGpuTransformsInFrame ? _gpuTransformsCameraView : Matrix4x4.Identity,
            CanvasSize = new Vector2(renderWidth, renderHeight),
            DpiScale = _currentDpiScale
        };
        _uniformBuffer.WriteSingle(uniformsData);

        // Upload compiled active brushes to storage buffer
        if (_activeBrushes.Count > 0)
        {
            _brushesStorageBuffer.Write(CollectionsMarshal.AsSpan(_activeBrushes));
        }



        // Rasterize all pending paths before starting the render pass
        _pathAtlas.RasterizePendingPaths();

        uploadSw.Stop();
        var passSw = System.Diagnostics.Stopwatch.StartNew();

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

        // Run mask render passes first!
        ExecuteMaskRenderPasses(encoder, isOffscreen: false);

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
        GpuBlendMode? currentBlendMode = null;
        GpuTexture? currentMaskTexture = null;
        var textureEntries = stackalloc BindGroupEntry[2];

        foreach (var dc in _drawCalls)
        {
            ApplyDrawCallScissor(pass, dc);

            if (dc.Type == DrawCallType.Vector)
            {
                var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: false);
                var maskBindGroup = GetMaskBindGroup(dc.MaskTexture, isOffscreen: false);

                if (currentType != DrawCallType.Vector || currentBlendMode != dc.BlendMode)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
                    fixed (BindGroup** pGrp = &_vectorUniformBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                    }
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentType = DrawCallType.Vector;
                    currentBlendMode = dc.BlendMode;
                    currentMaskTexture = dc.MaskTexture;
                }
                else if (currentMaskTexture != dc.MaskTexture)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                var buffer = _vectorVertexBuffer.BufferPtr;
                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _vectorVertexBuffer.Size);
                _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _vectorIndexBuffer.Size);
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Text)
            {
                var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: false);
                var maskBindGroup = GetMaskBindGroup(dc.MaskTexture, isOffscreen: false);

                if (currentType != DrawCallType.Text || currentBlendMode != dc.BlendMode)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
                    fixed (BindGroup** pGrp = &_textUniformBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pAtlas = &_atlasBindGroup)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
                    }
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentType = DrawCallType.Text;
                    currentBlendMode = dc.BlendMode;
                    currentMaskTexture = dc.MaskTexture;
                }
                else if (currentMaskTexture != dc.MaskTexture)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                var buffer = _textVertexBuffer.BufferPtr;
                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textVertexBuffer.Size);
                _context.Wgpu.RenderPassEncoderDraw(pass, 6, dc.IndexCount, 0, dc.IndexStart);
            }
            else if (dc.Type == DrawCallType.Texture && dc.Texture != null)
            {
                var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: false);
                var maskBindGroup = GetMaskBindGroup(dc.MaskTexture, isOffscreen: false);

                _context.Wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
                fixed (BindGroup** pGrp = &_textureUniformBindGroup)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                }
                var buffer = _textureVertexBuffer.BufferPtr;
                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textureVertexBuffer.Size);
                _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textureIndexBuffer.Size);
                if (currentMaskTexture != dc.MaskTexture || currentType != DrawCallType.Texture || currentBlendMode != dc.BlendMode)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                currentType = DrawCallType.Texture;
                currentBlendMode = dc.BlendMode;

                var viewPtr = dc.Texture.ViewPtr;
                var cacheKey = new TextureCacheKey(dc.Texture.Id, dc.Texture.Generation, isOffscreen: false);

                CachedBindGroup? cachedBg;
                lock (_persistentTextureBindGroups)
                {
                    if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out cachedBg))
                    {
                        textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = _atlasSampler };
                        textureEntries[1] = new BindGroupEntry { Binding = 1, TextureView = viewPtr };

                        var bgDesc = new BindGroupDescriptor { Layout = _textureBindGroupLayout, EntryCount = 2, Entries = textureEntries };
                        var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);
                        cachedBg = new CachedBindGroup((nint)bg, _frameNumber);
                        _persistentTextureBindGroups[cacheKey] = cachedBg;
                    }
                    else
                    {
                        cachedBg.LastUsedFrame = _frameNumber;
                    }
                }

                var bindGroup = (BindGroup*)cachedBg.BindGroupPtr;
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.StaticDxf && dc.StaticBuffer != null)
            {
                DrawStaticDxfBuffer(pass, dc.StaticBuffer, isOffscreen: false, dc.MaskTexture);
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
            else if (dc.Type == DrawCallType.Extension)
            {
                var pipeline = GetExtension(dc.ExtensionId);
                if (pipeline != null)
                {
                    if (pipeline is ProGPU.Scene.Extensions.SplineExtensionPipeline)
                    {
                        var maskBindGroup = GetMaskBindGroup(dc.MaskTexture, isOffscreen: false);
                        if (currentType != DrawCallType.Vector || currentBlendMode != dc.BlendMode)
                        {
                            var splinePipeline = GetPipeline(DrawCallType.Vector, dc.BlendMode, isOffscreen: false);
                            _context.Wgpu.RenderPassEncoderSetPipeline(pass, splinePipeline);
                            fixed (BindGroup** pGrp = &_vectorUniformBindGroup)
                            {
                                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                            }
                            fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroup)
                            {
                                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                            }
                            currentType = DrawCallType.Vector;
                            currentBlendMode = dc.BlendMode;
                        }

                        if (currentMaskTexture != dc.MaskTexture)
                        {
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                            currentMaskTexture = dc.MaskTexture;
                        }

                        if (dc.PointBufferCount > 0)
                        {
                            _context.Wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
                        }
                    }
                    else
                    {
                        var localDc = dc;
                        pipeline.Render(this, pass, isOffscreen: false, in localDc);
                        currentType = DrawCallType.Extension;
                    }
                }
            }
        }

        _context.Wgpu.RenderPassEncoderEnd(pass);
        _context.Wgpu.RenderPassEncoderRelease(pass);

        lock (_registeredExtensions)
        {
            foreach (var ext in _registeredExtensions)
            {
                ext.EndFrame(this);
            }
        }

        // Submit to queue
        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compositor Command Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        foreach (var tex in _masksToReturnToPool)
        {
            _maskTexturePool.Add(tex);
        }
        _masksToReturnToPool.Clear();
        _maskRenderPasses.Clear();

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
        lock (_persistentTextureBindGroups)
        {
            List<TextureCacheKey>? keysToRemove = null;
            foreach (var kvp in _persistentTextureBindGroups)
            {
                if (_frameNumber - kvp.Value.LastUsedFrame > 60)
                {
                    if (kvp.Value.BindGroupPtr != 0 && !_context.IsDisposed)
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
    }

    private void HandleTextureDisposed(ulong textureId)
    {
        if (Environment.HasShutdownStarted) return;

        lock (_persistentTextureBindGroups)
        {
            List<TextureCacheKey>? keysToRemove = null;
            foreach (var key in _persistentTextureBindGroups.Keys)
            {
                if (key.TextureId == textureId)
                {
                    keysToRemove ??= new List<TextureCacheKey>();
                    keysToRemove.Add(key);
                }
            }
            if (keysToRemove != null)
            {
                foreach (var key in keysToRemove)
                {
                    if (_persistentTextureBindGroups.TryGetValue(key, out var cachedBg))
                    {
                        if (cachedBg.BindGroupPtr != 0 && !_context.IsDisposed)
                        {
                            _context.Wgpu.BindGroupRelease((BindGroup*)cachedBg.BindGroupPtr);
                        }
                        _persistentTextureBindGroups.Remove(key);
                    }
                }
            }
        }

        lock (_maskBindGroups)
        {
            GpuTexture? maskKeyToRemove = null;
            foreach (var key in _maskBindGroups.Keys)
            {
                if (key.Id == textureId)
                {
                    maskKeyToRemove = key;
                    break;
                }
            }
            if (maskKeyToRemove != null)
            {
                if (!_context.IsDisposed)
                {
                    _context.Wgpu.BindGroupRelease((BindGroup*)_maskBindGroups[maskKeyToRemove]);
                }
                _maskBindGroups.Remove(maskKeyToRemove);
            }
        }

        lock (_maskBindGroupsOffscreen)
        {
            GpuTexture? maskKeyToRemoveOff = null;
            foreach (var key in _maskBindGroupsOffscreen.Keys)
            {
                if (key.Id == textureId)
                {
                    maskKeyToRemoveOff = key;
                    break;
                }
            }
            if (maskKeyToRemoveOff != null)
            {
                if (!_context.IsDisposed)
                {
                    _context.Wgpu.BindGroupRelease((BindGroup*)_maskBindGroupsOffscreen[maskKeyToRemoveOff]);
                }
                _maskBindGroupsOffscreen.Remove(maskKeyToRemoveOff);
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
        CommitPendingDrawCalls();
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
        CommitPendingDrawCalls();
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
        if (!node.IsVisible || node.Opacity <= 0.0001f || _activeOpacity <= 0.0001f)
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

        bool isTemplated = node.HasTemplate;
        if (isTemplated)
        {
            if (node is ContainerVisual container)
            {
                var children = container.Children;
                int count = children.Count;
                for (int i = 0; i < count; i++)
                {
                    CompileVisualTree(children[i], globalTransform);
                }
            }
        }

        // 2. Playback recorded commands
        var ctx = GetDrawingContext();
        node.OnRender(ctx);

        foreach (var cmd in ctx.Commands)
        {
            int vectorStart = _vectorVerticesList.Count;
            int textStart = _textVerticesList.Count;
            var activeTransform = cmd.UseGpuTransforms ? Matrix4x4.Identity : globalTransform;
            if (cmd.Type != RenderCommandType.DrawPath)
            {
                activeTransform = (cmd.Transform == default) ? activeTransform : cmd.Transform * activeTransform;
            }

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
                case RenderCommandType.PushGeometryClip:
                    if (cmd.Path != null) PushGeometryMask(cmd.Path, activeTransform);
                    break;
                case RenderCommandType.PopGeometryClip:
                    PopGeometryMask();
                    break;
                case RenderCommandType.PushOpacityMask:
                    if (cmd.Brush != null) PushOpacityMaskValue(cmd.Brush, cmd.Rect, activeTransform);
                    break;
                case RenderCommandType.PopOpacityMask:
                    PopOpacityMaskValue();
                    break;
                case RenderCommandType.PushBlendMode:
                    CommitPendingDrawCalls();
                    _blendModeStack.Push(_activeBlendMode);
                    _activeBlendMode = (GpuBlendMode)cmd.IntParam;
                    break;
                case RenderCommandType.PopBlendMode:
                    CommitPendingDrawCalls();
                    if (_blendModeStack.Count > 0) _activeBlendMode = _blendModeStack.Pop();
                    else _activeBlendMode = GpuBlendMode.SrcOver;
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
                        StaticBuffer = cmd.StaticBuffer,
                        ClipRect = _activeClipRect
                    });
                    _pendingVectorStart = (uint)_vectorIndicesList.Count;
                    _pendingTextStart = (uint)_textVerticesList.Count;
                    break;
                case RenderCommandType.DrawExtension:
                    {
                        var pipeline = GetExtension(cmd.ExtensionId);
                        if (pipeline != null)
                        {
                            CommitPendingDrawCalls();
                            var localCmd = cmd;
                            pipeline.Compile(this, ctx, activeTransform, ref localCmd);
                            var cmdTransform = localCmd.Transform;
                            if (cmdTransform == default || cmdTransform == new Matrix4x4())
                            {
                                cmdTransform = Matrix4x4.Identity;
                            }
                            _drawCalls.Add(new CompositorDrawCall
                            {
                                Type = DrawCallType.Extension,
                                ExtensionId = localCmd.ExtensionId,
                                IntParam = localCmd.IntParam,
                                FloatParam = localCmd.FloatParam,
                                DataParam = localCmd.DataParam,
                                PointBufferOffset = (int)_pendingVectorStart,
                                PointBufferCount = (int)((uint)_vectorIndicesList.Count - _pendingVectorStart),
                                DoubleBufferOffset = localCmd.DoubleBufferOffset,
                                DoubleBufferCount = localCmd.DoubleBufferCount,
                                WeightBufferOffset = localCmd.WeightBufferOffset,
                                WeightBufferCount = localCmd.WeightBufferCount,
                                FloatBufferOffset = localCmd.FloatBufferOffset,
                                FloatBufferCount = localCmd.FloatBufferCount,
                                StaticBuffer = localCmd.StaticBuffer,
                                Brush = localCmd.Brush,
                                Pen = localCmd.Pen,
                                Path = localCmd.Path,
                                Transform = activeTransform * cmdTransform,
                                LineThicknessOrRadius = localCmd.RadiusX,
                                Scale = localCmd.Scale,
                                Translate = localCmd.Translate,
                                Color = (localCmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f),
                                ClipRect = _activeClipRect,
                                MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
                                BlendMode = _activeBlendMode
                            });
                            _pendingVectorStart = (uint)_vectorIndicesList.Count;
                            _pendingTextStart = (uint)_textVerticesList.Count;
                        }
                    }
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
                case RenderCommandType.DrawGlyphRun:
                    CompileGlyphRunCommand(cmd, activeTransform);
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
                    v.ScaleBoldItalicUseMvp.W = 1.0f;
                    _textVerticesList[i] = v;
                }
            }

            _useGpuTransformsActive = savedUseGpuTransformsActive;
            _cameraViewMatrix = savedCameraViewMatrix;
        }

        ReleaseDrawingContext();

        if (!isTemplated)
        {
            if (node is ContainerVisual container)
            {
                var children = container.Children;
                int count = children.Count;
                for (int i = 0; i < count; i++)
                {
                    CompileVisualTree(children[i], globalTransform);
                }
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
            if (cmd.Type != RenderCommandType.DrawPath)
            {
                activeTransform = (cmd.Transform == default) ? activeTransform : cmd.Transform * activeTransform;
            }
            
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
                case RenderCommandType.PushGeometryClip:
                    if (cmd.Path != null) PushGeometryMask(cmd.Path, activeTransform);
                    break;
                case RenderCommandType.PopGeometryClip:
                    PopGeometryMask();
                    break;
                case RenderCommandType.PushOpacityMask:
                    if (cmd.Brush != null) PushOpacityMaskValue(cmd.Brush, cmd.Rect, activeTransform);
                    break;
                case RenderCommandType.PopOpacityMask:
                    PopOpacityMaskValue();
                    break;
                case RenderCommandType.PushBlendMode:
                    CommitPendingDrawCalls();
                    _blendModeStack.Push(_activeBlendMode);
                    _activeBlendMode = (GpuBlendMode)cmd.IntParam;
                    break;
                case RenderCommandType.PopBlendMode:
                    CommitPendingDrawCalls();
                    if (_blendModeStack.Count > 0) _activeBlendMode = _blendModeStack.Pop();
                    else _activeBlendMode = GpuBlendMode.SrcOver;
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
                case RenderCommandType.DrawExtension:
                    {
                        var pipeline = GetExtension(cmd.ExtensionId);
                        if (pipeline != null)
                        {
                            CommitPendingDrawCalls();
                            var localCmd = cmd;
                            pipeline.Compile(this, picture, activeTransform, ref localCmd);
                            var cmdTransform = localCmd.Transform;
                            if (cmdTransform == default || cmdTransform == new Matrix4x4())
                            {
                                cmdTransform = Matrix4x4.Identity;
                            }
                            _drawCalls.Add(new CompositorDrawCall
                            {
                                Type = DrawCallType.Extension,
                                ExtensionId = localCmd.ExtensionId,
                                IntParam = localCmd.IntParam,
                                FloatParam = localCmd.FloatParam,
                                DataParam = localCmd.DataParam,
                                PointBufferOffset = (int)_pendingVectorStart,
                                PointBufferCount = (int)((uint)_vectorIndicesList.Count - _pendingVectorStart),
                                DoubleBufferOffset = localCmd.DoubleBufferOffset,
                                DoubleBufferCount = localCmd.DoubleBufferCount,
                                WeightBufferOffset = localCmd.WeightBufferOffset,
                                WeightBufferCount = localCmd.WeightBufferCount,
                                FloatBufferOffset = localCmd.FloatBufferOffset,
                                FloatBufferCount = localCmd.FloatBufferCount,
                                StaticBuffer = localCmd.StaticBuffer,
                                Brush = localCmd.Brush,
                                Pen = localCmd.Pen,
                                Path = localCmd.Path,
                                Transform = activeTransform * cmdTransform,
                                LineThicknessOrRadius = localCmd.RadiusX,
                                Scale = localCmd.Scale,
                                Translate = localCmd.Translate,
                                Color = (localCmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f),
                                ClipRect = _activeClipRect,
                                MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
                                BlendMode = _activeBlendMode
                            });
                            _pendingVectorStart = (uint)_vectorIndicesList.Count;
                            _pendingTextStart = (uint)_textVerticesList.Count;
                        }
                    }
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
                case RenderCommandType.DrawGlyphRun:
                    CompileGlyphRunCommand(cmd, activeTransform);
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
                    v.ScaleBoldItalicUseMvp.W = 1.0f;
                    _textVerticesList[i] = v;
                }
            }

            _useGpuTransformsActive = savedUseGpuTransformsActive;
            _cameraViewMatrix = savedCameraViewMatrix;
        }
    }

    private void CompileRectCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        SwitchBatch(BatchType.Vector);
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

        if (cmd.Pen != null && cmd.Pen.Thickness > 0f)
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
        SwitchBatch(BatchType.Vector);
        if (cmd.Path == null) return;

        int startIndex = _vectorVerticesList.Count;

        transform = (cmd.Transform == default) ? transform : cmd.Transform * transform;

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
                    float rx1 = MathF.Min(MathF.Min(v0.X, v1.X), MathF.Min(v2.X, v3.X));
                    float rx2 = MathF.Max(MathF.Max(v0.X, v1.X), MathF.Max(v2.X, v3.X));
                    float ry1 = MathF.Min(MathF.Min(v0.Y, v1.Y), MathF.Min(v2.Y, v3.Y));
                    float ry2 = MathF.Max(MathF.Max(v0.Y, v1.Y), MathF.Max(v2.Y, v3.Y));

                    float clipLeft = _activeClipRect.Value.X;
                    float clipTop = _activeClipRect.Value.Y;
                    float clipRight = clipLeft + _activeClipRect.Value.Width;
                    float clipBottom = clipTop + _activeClipRect.Value.Height;

                    if (rx2 <= clipLeft || rx1 >= clipRight || ry2 <= clipTop || ry1 >= clipBottom)
                    {
                        return; // Completely clipped!
                    }
                }

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

        if (cmd.Pen != null && cmd.Pen.Thickness > 0f)
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
        SwitchBatch(BatchType.Vector);
        var pipeline = GetExtension(CompositorBuiltInExtensions.Hatch);
        if (pipeline != null)
        {
            var localCmd = cmd;
            pipeline.Compile(this, null, transform, ref localCmd);
        }
    }

    private void CompileAcisCommand(IRenderDataProvider? provider, RenderCommand cmd, Matrix4x4 transform)
    {
        SwitchBatch(BatchType.Vector);
        var pipeline = GetExtension(CompositorBuiltInExtensions.AcisSolid);
        if (pipeline != null)
        {
            var localCmd = cmd;
            pipeline.Compile(this, provider, transform, ref localCmd);
        }
    }

    private void CompileLineCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        SwitchBatch(BatchType.Vector);
        if (cmd.Pen == null || cmd.Pen.Thickness <= 0f) return;
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
        SwitchBatch(BatchType.Vector);
        var pipeline = GetExtension(CompositorBuiltInExtensions.Line3D);
        if (pipeline != null)
        {
            var localCmd = cmd;
            pipeline.Compile(this, null, transform, ref localCmd);
        }
    }

    private void CompileBezierCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        SwitchBatch(BatchType.Vector);
        if (cmd.Pen == null || cmd.Pen.Thickness <= 0f) return;
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
        SwitchBatch(BatchType.Vector);
        if (cmd.Pen == null || cmd.Pen.Thickness <= 0f) return;
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
        SwitchBatch(BatchType.Vector);
        if (cmd.Pen == null || cmd.Pen.Thickness <= 0f) return;

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
        SwitchBatch(BatchType.Vector);
        var pipeline = GetExtension(CompositorBuiltInExtensions.Spline);
        if (pipeline != null)
        {
            var localCmd = cmd;
            pipeline.Compile(this, provider, transform, ref localCmd);
        }
    }

    private void CompileFillTriangleCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        SwitchBatch(BatchType.Vector);
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
        SwitchBatch(BatchType.Vector);
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
        SwitchBatch(BatchType.Vector);
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

        if (cmd.Pen != null && cmd.Pen.Thickness > 0f)
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
        SwitchBatch(BatchType.Vector);
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

        if (cmd.Pen != null && cmd.Pen.Thickness > 0f)
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

    internal float RegisterBrush(Brush? brush)
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

        if (_activeBrushes.Count < 8192)
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
        SwitchBatch(BatchType.Text);
        if (ActiveCompilationContext != null && !ActiveCompilationContext.IsRecompiling)
        {
            _compiledTextRecords.Add(new StaticTextRecord { Command = cmd, Transform = transform });
        }

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
        int maxVertices = maxGlyphs * maxPassCount;

        int vertexStart = _textVerticesList.Count;

        CollectionsMarshal.SetCount(_textVerticesList, vertexStart + maxVertices);

        var textVerticesSpan = CollectionsMarshal.AsSpan(_textVerticesList);

        int currentVertexCount = vertexStart;

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

            float baseCursorX = runGlyph.Position.X - runGlyph.Glyph.BearX;
            float baseCursorY = runGlyph.Position.Y - runGlyph.Glyph.BearY;

            float dpiScale = _currentDpiScale;
            if (ActiveCompilationContext != null && ActiveCompilationContext.StaticZoom > 0.0001f)
            {
                dpiScale *= ActiveCompilationContext.StaticZoom;
            }

            float physicalFontSize = cmd.FontSize * dpiScale;
            float rasterFontSize = Math.Clamp(physicalFontSize, 4f, 64f);
            if (rasterFontSize <= 24f)
            {
                rasterFontSize = MathF.Round(rasterFontSize * 2f) / 2f;
            }
            else
            {
                rasterFontSize = MathF.Round(rasterFontSize / 2f) * 2f;
            }

            bool isRotated = MathF.Abs(activeTransform.M12) > 0.0001f ||
                             MathF.Abs(activeTransform.M21) > 0.0001f ||
                             activeTransform.M11 < 0.0f ||
                             activeTransform.M22 < 0.0f;

            Vector2 transPos = Vector2.Transform(new Vector2(baseCursorX + cmd.Position.X, baseCursorY + cmd.Position.Y), activeTransform);
            Vector2 transPosPhysical = transPos * dpiScale;

            byte subpixelX = 0;
            float ipartX = 0f;
            float snappedY = 0f;
            Vector2 snappedLogicalPos;

            if (!isRotated && rasterFontSize <= 24f)
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
                snappedLogicalPos = new Vector2(ipartX, snappedY) / dpiScale;
            }
            else if (!isRotated)
            {
                ipartX = MathF.Round(transPosPhysical.X);
                snappedY = MathF.Round(transPosPhysical.Y);
                snappedLogicalPos = new Vector2(ipartX, snappedY) / dpiScale;
            }
            else
            {
                snappedLogicalPos = transPos;
            }

            float scaleRatio = physicalFontSize / rasterFontSize;

            var info = _atlas.GetOrCreateGlyph(glyphFont, runGlyph.CodePoint, rasterFontSize, subpixelX);
            if (info.Width == 0 || info.Height == 0) continue;

            int passCount = cmd.IsBold ? 2 : 1;
            float boldOffset = cmd.FontSize * 0.035f;

            for (int pass = 0; pass < passCount; pass++)
            {
                float xOffset = pass * boldOffset;

                if (_activeClipRect.HasValue && !_useGpuTransformsActive)
                {
                    float halfSize = cmd.FontSize * 2f;
                    float minX = snappedLogicalPos.X - halfSize;
                    float maxX = snappedLogicalPos.X + halfSize;
                    float minY = snappedLogicalPos.Y - halfSize;
                    float maxY = snappedLogicalPos.Y + halfSize;

                    float clipLeft = _activeClipRect.Value.X;
                    float clipTop = _activeClipRect.Value.Y;
                    float clipRight = clipLeft + _activeClipRect.Value.Width;
                    float clipBottom = clipTop + _activeClipRect.Value.Height;

                    if (maxX <= clipLeft || minX >= clipRight || maxY <= clipTop || minY >= clipBottom)
                    {
                        continue;
                    }
                }

                Vector2 basisX = new Vector2(activeTransform.M11, activeTransform.M12);
                Vector2 basisY = new Vector2(activeTransform.M21, activeTransform.M22);

                textVerticesSpan[currentVertexCount++] = new GlyphInstance
                {
                    SnappedLogicalPos = snappedLogicalPos,
                    BasisX = basisX,
                    BasisY = basisY,
                    BearSize = new Vector4(info.BearX, info.BearY, info.Width, info.Height),
                    TexCoords = new Vector4(info.TexCoordMin.X, info.TexCoordMin.Y, info.TexCoordMax.X, info.TexCoordMax.Y),
                    Color = color,
                    ScaleBoldItalicUseMvp = new Vector4(scaleRatio, xOffset, cmd.IsItalic ? 0.22f : 0f, (ActiveCompilationContext != null) ? 1f : 0f),
                    BrushIndex = bIdx,
                    Padding = 0f
                };
            }
        }

        CollectionsMarshal.SetCount(_textVerticesList, currentVertexCount);
    }

    private void CompileGlyphRunCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        SwitchBatch(BatchType.Text);
        if (ActiveCompilationContext != null && !ActiveCompilationContext.IsRecompiling)
        {
            _compiledTextRecords.Add(new StaticTextRecord { Command = cmd, Transform = transform });
        }

        var activeTransform = transform;
        if (MathF.Abs(cmd.Rotation) > 0.0001f)
        {
            var localMat = Matrix4x4.CreateTranslation(-cmd.Position.X, -cmd.Position.Y, 0f) *
                           Matrix4x4.CreateRotationZ(cmd.Rotation) *
                           Matrix4x4.CreateTranslation(cmd.Position.X, cmd.Position.Y, 0f);
            activeTransform = localMat * activeTransform;
        }

        var font = cmd.Font;
        if (font == null || cmd.GlyphIndices == null || cmd.GlyphPositions == null) return;

        float bIdx = RegisterBrush(cmd.Brush);
        var brush = cmd.Brush as SolidColorBrush;
        var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);
        color.W *= _activeOpacity;

        int maxGlyphs = cmd.GlyphIndices.Length;
        int maxPassCount = cmd.IsBold ? 2 : 1;
        int maxVertices = maxGlyphs * maxPassCount;

        int vertexStart = _textVerticesList.Count;

        CollectionsMarshal.SetCount(_textVerticesList, vertexStart + maxVertices);

        var textVerticesSpan = CollectionsMarshal.AsSpan(_textVerticesList);

        int currentVertexCount = vertexStart;

        float dpiScale = _currentDpiScale;
        if (ActiveCompilationContext != null && ActiveCompilationContext.StaticZoom > 0.0001f)
        {
            dpiScale *= ActiveCompilationContext.StaticZoom;
        }

        float physicalFontSize = cmd.FontSize * dpiScale;
        float rasterFontSize = Math.Clamp(physicalFontSize, 4f, 64f);
        if (rasterFontSize <= 24f)
        {
            rasterFontSize = MathF.Round(rasterFontSize * 2f) / 2f;
        }
        else
        {
            rasterFontSize = MathF.Round(rasterFontSize / 2f) * 2f;
        }

        bool isRotated = MathF.Abs(activeTransform.M12) > 0.0001f ||
                         MathF.Abs(activeTransform.M21) > 0.0001f ||
                         activeTransform.M11 < 0.0f ||
                         activeTransform.M22 < 0.0f;

        for (int i = 0; i < maxGlyphs; i++)
        {
            ushort glyphIdx = cmd.GlyphIndices[i];
            Vector2 position = cmd.GlyphPositions[i];

            var colorLayers = font.GetColorLayers(glyphIdx);

            if (colorLayers != null && colorLayers.Count > 0)
            {
                foreach (var layer in colorLayers)
                {
                    var layerOutline = font.GetGlyphOutline(layer.GlyphId);
                    if (layerOutline == null) continue;

                    float emScale = cmd.FontSize / font.UnitsPerEm;
                    var transformedOutline = new PathGeometry();
                    float x0 = position.X + cmd.Position.X;
                    float y0 = position.Y + cmd.Position.Y;

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

            float baseCursorX = position.X;
            float baseCursorY = position.Y;

            Vector2 transPos = Vector2.Transform(new Vector2(baseCursorX + cmd.Position.X, baseCursorY + cmd.Position.Y), activeTransform);
            Vector2 transPosPhysical = transPos * dpiScale;

            byte subpixelX = 0;
            float ipartX = 0f;
            float snappedY = 0f;
            Vector2 snappedLogicalPos;

            if (!isRotated && rasterFontSize <= 24f)
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
                snappedLogicalPos = new Vector2(ipartX, snappedY) / dpiScale;
            }
            else if (!isRotated)
            {
                ipartX = MathF.Round(transPosPhysical.X);
                snappedY = MathF.Round(transPosPhysical.Y);
                snappedLogicalPos = new Vector2(ipartX, snappedY) / dpiScale;
            }
            else
            {
                snappedLogicalPos = transPos;
            }

            float scaleRatio = physicalFontSize / rasterFontSize;

            var info = _atlas.GetOrCreateGlyphByIndex(font, glyphIdx, rasterFontSize, subpixelX);
            if (info.Width == 0 || info.Height == 0) continue;

            int passCount = cmd.IsBold ? 2 : 1;
            float boldOffset = cmd.FontSize * 0.035f;

            for (int pass = 0; pass < passCount; pass++)
            {
                float xOffset = pass * boldOffset;

                if (_activeClipRect.HasValue && !_useGpuTransformsActive)
                {
                    float halfSize = cmd.FontSize * 2f;
                    float minX = snappedLogicalPos.X - halfSize;
                    float maxX = snappedLogicalPos.X + halfSize;
                    float minY = snappedLogicalPos.Y - halfSize;
                    float maxY = snappedLogicalPos.Y + halfSize;

                    float clipLeft = _activeClipRect.Value.X;
                    float clipTop = _activeClipRect.Value.Y;
                    float clipRight = clipLeft + _activeClipRect.Value.Width;
                    float clipBottom = clipTop + _activeClipRect.Value.Height;

                    if (maxX <= clipLeft || minX >= clipRight || maxY <= clipTop || minY >= clipBottom)
                    {
                        continue;
                    }
                }

                Vector2 basisX = new Vector2(activeTransform.M11, activeTransform.M12);
                Vector2 basisY = new Vector2(activeTransform.M21, activeTransform.M22);

                textVerticesSpan[currentVertexCount++] = new GlyphInstance
                {
                    SnappedLogicalPos = snappedLogicalPos,
                    BasisX = basisX,
                    BasisY = basisY,
                    BearSize = new Vector4(info.BearX, info.BearY, info.Width, info.Height),
                    TexCoords = new Vector4(info.TexCoordMin.X, info.TexCoordMin.Y, info.TexCoordMax.X, info.TexCoordMax.Y),
                    Color = color,
                    ScaleBoldItalicUseMvp = new Vector4(scaleRatio, xOffset, cmd.IsItalic ? 0.22f : 0f, (ActiveCompilationContext != null) ? 1f : 0f),
                    BrushIndex = bIdx,
                    Padding = 0f
                };
            }
        }

        CollectionsMarshal.SetCount(_textVerticesList, currentVertexCount);
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

        Vector2 uv0, uv1, uv2, uv3;
        if (cmd.SrcRect.Width > 0f && cmd.SrcRect.Height > 0f)
        {
            float texW = cmd.Texture.Width;
            float texH = cmd.Texture.Height;
            float l = cmd.SrcRect.X / texW;
            float t = cmd.SrcRect.Y / texH;
            float right = (cmd.SrcRect.X + cmd.SrcRect.Width) / texW;
            float b = (cmd.SrcRect.Y + cmd.SrcRect.Height) / texH;

            uv0 = new Vector2(l, t);
            uv1 = new Vector2(right, t);
            uv2 = new Vector2(right, b);
            uv3 = new Vector2(l, b);
        }
        else
        {
            uv0 = new Vector2(0f, 0f);
            uv1 = new Vector2(1f, 0f);
            uv2 = new Vector2(1f, 1f);
            uv3 = new Vector2(0f, 1f);
        }

        bool isRotated = MathF.Abs(transform.M12) > 0.0001f ||
                         MathF.Abs(transform.M21) > 0.0001f ||
                         transform.M11 < 0.0f ||
                         transform.M22 < 0.0f;
        if (_activeClipRect.HasValue && !_useGpuTransformsActive)
        {
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
            Texture = cmd.Texture,
            ClipRect = _activeClipRect,
            MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
            BlendMode = _activeBlendMode
        });
    }

    private void CommitPendingVectorDrawCall()
    {
        uint vecCount = (uint)_vectorIndicesList.Count - _pendingVectorStart;
        if (vecCount > 0)
        {
            _drawCalls.Add(new CompositorDrawCall 
            { 
                Type = DrawCallType.Vector, 
                IndexStart = _pendingVectorStart, 
                IndexCount = vecCount,
                ClipRect = _activeClipRect,
                MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
                BlendMode = _activeBlendMode
            });
            _pendingVectorStart = (uint)_vectorIndicesList.Count;
        }
    }

    private void CommitPendingTextDrawCall()
    {
        uint textCount = (uint)_textVerticesList.Count - _pendingTextStart;
        if (textCount > 0)
        {
            _drawCalls.Add(new CompositorDrawCall 
            { 
                Type = DrawCallType.Text, 
                IndexStart = _pendingTextStart, 
                IndexCount = textCount,
                ClipRect = _activeClipRect,
                MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
                BlendMode = _activeBlendMode
            });
            _pendingTextStart = (uint)_textVerticesList.Count;
        }
    }

    private void SwitchBatch(BatchType nextType)
    {
        if (_currentBatchType == nextType) return;

        if (_currentBatchType == BatchType.Vector)
        {
            CommitPendingVectorDrawCall();
        }
        else if (_currentBatchType == BatchType.Text)
        {
            CommitPendingTextDrawCall();
        }

        _currentBatchType = nextType;
    }

    private void CommitPendingDrawCalls()
    {
        if (_currentBatchType == BatchType.Vector)
        {
            CommitPendingVectorDrawCall();
        }
        else if (_currentBatchType == BatchType.Text)
        {
            CommitPendingTextDrawCall();
        }
        else
        {
            CommitPendingVectorDrawCall();
            CommitPendingTextDrawCall();
        }
        _currentBatchType = BatchType.None;
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

        lock (_context.RenderLock)
        {
            ReleaseMsaaResources();

            _uniformBuffer.Dispose();
            _brushesStorageBuffer.Dispose();
            _vectorVertexBuffer.Dispose();
            _vectorIndexBuffer.Dispose();
            _textVertexBuffer.Dispose();
            _textureVertexBuffer.Dispose();
            _textureIndexBuffer.Dispose();
            
            _atlas.Dispose();
            _pathAtlas.Dispose();
            
            lock (_registeredExtensions)
            {
                foreach (var ext in _registeredExtensions)
                {
                    if (ext is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
            }
            
            if (!_context.IsDisposed)
            {
                if (_pathAtlasBindGroup != null) _context.Wgpu.BindGroupRelease(_pathAtlasBindGroup);
                if (_pathAtlasBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_pathAtlasBindGroupLayout);
                if (_pathAtlasBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_pathAtlasBindGroupOffscreen);
                if (_pathAtlasBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_pathAtlasBindGroupLayoutOffscreen);
            }
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

            if (!_context.IsDisposed)
            {
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

                if (_vectorPipelineLayout != null) _context.Wgpu.PipelineLayoutRelease(_vectorPipelineLayout);
                if (_textPipelineLayout != null) _context.Wgpu.PipelineLayoutRelease(_textPipelineLayout);
                if (_texturePipelineLayout != null) _context.Wgpu.PipelineLayoutRelease(_texturePipelineLayout);
                if (_vectorPipelineLayoutOffscreen != null) _context.Wgpu.PipelineLayoutRelease(_vectorPipelineLayoutOffscreen);
                if (_textPipelineLayoutOffscreen != null) _context.Wgpu.PipelineLayoutRelease(_textPipelineLayoutOffscreen);
                if (_texturePipelineLayoutOffscreen != null) _context.Wgpu.PipelineLayoutRelease(_texturePipelineLayoutOffscreen);

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
            }

            lock (_persistentTextureBindGroups)
            {
                if (!_context.IsDisposed)
                {
                    foreach (var cachedBg in _persistentTextureBindGroups.Values)
                    {
                        if (cachedBg.BindGroupPtr != 0) _context.Wgpu.BindGroupRelease((BindGroup*)cachedBg.BindGroupPtr);
                    }
                }
                _persistentTextureBindGroups.Clear();
            }
            if (_dummyMaskTexture != null) _dummyMaskTexture.Dispose();
            if (!_context.IsDisposed)
            {
                if (_dummyMaskBindGroup != null) _context.Wgpu.BindGroupRelease(_dummyMaskBindGroup);
                if (_dummyMaskBindGroupOffscreen != null) _context.Wgpu.BindGroupRelease(_dummyMaskBindGroupOffscreen);
                if (_maskBindGroupLayout != null) _context.Wgpu.BindGroupLayoutRelease(_maskBindGroupLayout);
                if (_maskBindGroupLayoutOffscreen != null) _context.Wgpu.BindGroupLayoutRelease(_maskBindGroupLayoutOffscreen);

                foreach (var bg in _maskBindGroups.Values)
                {
                    _context.Wgpu.BindGroupRelease((BindGroup*)bg);
                }
                foreach (var bg in _maskBindGroupsOffscreen.Values)
                {
                    _context.Wgpu.BindGroupRelease((BindGroup*)bg);
                }
            }
            _maskBindGroups.Clear();
            _maskBindGroupsOffscreen.Clear();

            foreach (var tex in _maskTexturePool)
            {
                tex.Dispose();
            }
            _maskTexturePool.Clear();

            GpuTexture.OnDisposedWithId -= HandleTextureDisposed;

            _isDisposed = true;
        }
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
        lock (_context.RenderLock)
        {
            if (_context.IsDisposed)
            {
                _msaaTextureView = null;
                _msaaTexture = null;
                return;
            }

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
    }

    internal Vector2 ClampToClip(Vector2 p)
    {
        return p;
    }

    private unsafe void ApplyDrawCallScissor(RenderPassEncoder* pass, CompositorDrawCall dc)
    {
        uint targetWidth = (uint)Math.Round(_currentWidth * _currentDpiScale);
        uint targetHeight = (uint)Math.Round(_currentHeight * _currentDpiScale);

        if (dc.ClipRect.HasValue)
        {
            var rect = dc.ClipRect.Value;
            float rx = Math.Max(0f, rect.X * _currentDpiScale);
            float ry = Math.Max(0f, rect.Y * _currentDpiScale);
            float rw = Math.Max(0f, rect.Width * _currentDpiScale);
            float rh = Math.Max(0f, rect.Height * _currentDpiScale);

            uint sx = (uint)Math.Round(rx);
            uint sy = (uint)Math.Round(ry);
            uint sw = (uint)Math.Round(rw);
            uint sh = (uint)Math.Round(rh);

            sw = Math.Max(1u, sw);
            sh = Math.Max(1u, sh);

            if (sx < targetWidth && sy < targetHeight)
            {
                sw = Math.Min(sw, targetWidth - sx);
                sh = Math.Min(sh, targetHeight - sy);
                _context.Wgpu.RenderPassEncoderSetScissorRect(pass, sx, sy, sw, sh);
            }
            else
            {
                _context.Wgpu.RenderPassEncoderSetScissorRect(pass, 0, 0, 1, 1);
            }
        }
        else
        {
            _context.Wgpu.RenderPassEncoderSetScissorRect(pass, 0, 0, targetWidth, targetHeight);
        }
    }

    ~Compositor()
    {
        // Do not call Dispose() or native WebGPU release APIs during finalization.
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
                RenderOffscreen(fe, w, h, textures.Source, padding, 1.0f);
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

        // Compute high-DPI scaling factor dynamically from the compositor target context
        float dpiScale = _currentDpiScale;

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
                RenderOffscreen(node, (uint)node.Size.X, (uint)node.Size.Y, node.LayerTexture, 0f, dpiScale);
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

    public void RenderOffscreen(Visual node, uint width, uint height, GpuTexture targetTexture, float padding, float dpiScale, Vector4? clearColor = null, bool loadExistingContents = false)
    {
        var savedWidth = _currentWidth;
        var savedHeight = _currentHeight;
        var savedDpiScale = _currentDpiScale;
        _currentWidth = width;
        _currentHeight = height;
        _currentDpiScale = dpiScale;

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
        var savedCurrentBatchType = _currentBatchType;

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
        _textureVerticesList.Clear();
        _textureIndicesList.Clear();
        _drawCalls.Clear();
        _activeBrushes.Clear();
        _clipStack.Clear();
        _activeClipRect = null;
        _opacityStack.Clear();
        _activeOpacity = 1.0f;
        _currentBatchType = BatchType.None;

        _blendModeStack.Clear();
        _activeBlendMode = GpuBlendMode.SrcOver;
        _maskStack.Clear();
        _maskRenderPasses.Clear();
        _masksToReturnToPool.Clear();

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
            EnsureBufferSize(ref _textVertexBuffer, (uint)_textVerticesList.Count * (uint)Marshal.SizeOf<GlyphInstance>(), BufferUsage.Vertex);
            _textVertexBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textVerticesList));
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
            View = _hasGpuTransformsInFrame ? _gpuTransformsCameraView : Matrix4x4.Identity,
            CanvasSize = new Vector2(targetTexture.Width, targetTexture.Height),
            DpiScale = _currentDpiScale
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

        // Run mask render passes first!
        ExecuteMaskRenderPasses(encoder, isOffscreen: true);

        // Clear with transparent color
        var clearVal = new Color { R = 0, G = 0, B = 0, A = 0 };
        if (clearColor.HasValue)
        {
            clearVal = new Color { R = clearColor.Value.X, G = clearColor.Value.Y, B = clearColor.Value.Z, A = clearColor.Value.W };
        }

        var colorAttachment = new RenderPassColorAttachment
        {
            View = targetView,
            ResolveTarget = null,
            LoadOp = loadExistingContents ? LoadOp.Load : LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = clearVal
        };

        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = null
        };

        var pass = _context.Wgpu.CommandEncoderBeginRenderPass(encoder, &passDesc);

        DrawCallType? currentType = null;
        GpuBlendMode? currentBlendMode = null;
        GpuTexture? currentMaskTexture = null;
        var textureEntries = stackalloc BindGroupEntry[2];

        foreach (var dc in _drawCalls)
        {
            ApplyDrawCallScissor(pass, dc);

            if (dc.Type == DrawCallType.Vector)
            {
                var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: true);
                var maskBindGroup = GetMaskBindGroup(dc.MaskTexture, isOffscreen: true);

                if (currentType != DrawCallType.Vector || currentBlendMode != dc.BlendMode)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
                    fixed (BindGroup** pGrp = &_vectorUniformBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                    }
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentType = DrawCallType.Vector;
                    currentBlendMode = dc.BlendMode;
                    currentMaskTexture = dc.MaskTexture;
                }
                else if (currentMaskTexture != dc.MaskTexture)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                var buffer = _vectorVertexBuffer.BufferPtr;
                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _vectorVertexBuffer.Size);
                _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _vectorIndexBuffer.Size);
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Text)
            {
                var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: true);
                var maskBindGroup = GetMaskBindGroup(dc.MaskTexture, isOffscreen: true);

                if (currentType != DrawCallType.Text || currentBlendMode != dc.BlendMode)
                {
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
                    fixed (BindGroup** pGrp = &_textUniformBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pAtlas = &_atlasBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
                    }
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentType = DrawCallType.Text;
                    currentBlendMode = dc.BlendMode;
                    currentMaskTexture = dc.MaskTexture;
                }
                else if (currentMaskTexture != dc.MaskTexture)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                var buffer = _textVertexBuffer.BufferPtr;
                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textVertexBuffer.Size);
                _context.Wgpu.RenderPassEncoderDraw(pass, 6, dc.IndexCount, 0, dc.IndexStart);
            }
            else if (dc.Type == DrawCallType.Texture && dc.Texture != null)
            {
                var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: true);
                var maskBindGroup = GetMaskBindGroup(dc.MaskTexture, isOffscreen: true);

                _context.Wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
                fixed (BindGroup** pGrp = &_textureUniformBindGroupOffscreen)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                }
                var buffer = _textureVertexBuffer.BufferPtr;
                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textureVertexBuffer.Size);
                _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textureIndexBuffer.Size);
                if (currentMaskTexture != dc.MaskTexture || currentType != DrawCallType.Texture || currentBlendMode != dc.BlendMode)
                {
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                currentType = DrawCallType.Texture;
                currentBlendMode = dc.BlendMode;

                var viewPtr = dc.Texture.ViewPtr;
                var cacheKey = new TextureCacheKey(dc.Texture.Id, dc.Texture.Generation, isOffscreen: true);

                CachedBindGroup? cachedBg;
                lock (_persistentTextureBindGroups)
                {
                    if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out cachedBg))
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
                }

                var bindGroup = (BindGroup*)cachedBg.BindGroupPtr;
                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.StaticDxf && dc.StaticBuffer != null)
            {
                DrawStaticDxfBuffer(pass, dc.StaticBuffer, isOffscreen: true, dc.MaskTexture);
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
            else if (dc.Type == DrawCallType.Extension)
            {
                var pipeline = GetExtension(dc.ExtensionId);
                if (pipeline != null)
                {
                    if (pipeline is ProGPU.Scene.Extensions.SplineExtensionPipeline)
                    {
                        var maskBindGroup = GetMaskBindGroup(dc.MaskTexture, isOffscreen: true);
                        if (currentType != DrawCallType.Vector || currentBlendMode != dc.BlendMode)
                        {
                            var splinePipeline = GetPipeline(DrawCallType.Vector, dc.BlendMode, isOffscreen: true);
                            _context.Wgpu.RenderPassEncoderSetPipeline(pass, splinePipeline);
                            fixed (BindGroup** pGrp = &_vectorUniformBindGroupOffscreen)
                            {
                                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                            }
                            fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroupOffscreen)
                            {
                                _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                            }
                            currentType = DrawCallType.Vector;
                            currentBlendMode = dc.BlendMode;
                        }

                        if (currentMaskTexture != dc.MaskTexture)
                        {
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                            currentMaskTexture = dc.MaskTexture;
                        }

                        if (dc.PointBufferCount > 0)
                        {
                            _context.Wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
                        }
                    }
                    else
                    {
                        var localDc = dc;
                        pipeline.Render(this, pass, isOffscreen: true, in localDc);
                        currentType = DrawCallType.Extension;
                    }
                }
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

        foreach (var tex in _masksToReturnToPool)
        {
            _maskTexturePool.Add(tex);
        }
        _masksToReturnToPool.Clear();
        _maskRenderPasses.Clear();

        _pathAtlas.CleanupFrame();

        EvictUnusedBindGroups();

        // Restore main lists and state
        _vectorVerticesList.Clear(); _vectorVerticesList.AddRange(savedVectorVertices);
        _vectorIndicesList.Clear(); _vectorIndicesList.AddRange(savedVectorIndices);
        _textVerticesList.Clear(); _textVerticesList.AddRange(savedTextVertices);
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
        _currentBatchType = savedCurrentBatchType;

        _useGpuTransformsActive = savedUseGpuTransformsActive;
        _cameraViewMatrix = savedCameraViewMatrix;
        _hasGpuTransformsInFrame = savedHasGpuTransformsInFrame;
        _gpuTransformsCameraView = savedGpuTransformsCameraView;

        _currentWidth = savedWidth;
        _currentHeight = savedHeight;
        _currentDpiScale = savedDpiScale;
        _currentProjection = savedProjection;
    }

    public DxfStaticBuffer CompileStaticDxf(List<RenderCommand> commands, float staticZoom = 1.0f)
    {
        // Save current lists
        var savedVectorVertices = _vectorVerticesList.ToArray();
        var savedVectorIndices = _vectorIndicesList.ToArray();
        var savedTextVertices = _textVerticesList.ToArray();
        var savedActiveBrushes = _activeBrushes.ToArray();

        var savedActiveClipRect = _activeClipRect;
        var savedClipStack = _clipStack.ToArray();

        _activeClipRect = null;
        _clipStack.Clear();
        
        // Clear for compilation
        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _activeBrushes.Clear();
        _compiledTextRecords.Clear();

        ActiveCompilationContext = new StaticCompilationContext { StaticZoom = staticZoom };
        lock (_registeredExtensions)
        {
            foreach (var ext in _registeredExtensions)
            {
                ext.BeginStaticCompile(this, ActiveCompilationContext);
            }
        }

        try
        {
            _atlas.BeginBatch();
            var staticDrawCalls = new List<CompositorDrawCall>();
            uint pendingVectorStart = 0;
            uint pendingTextStart = 0;

            void CommitStaticDrawCalls()
            {
                uint vecCount = (uint)_vectorIndicesList.Count - pendingVectorStart;
                if (vecCount > 0)
                {
                    staticDrawCalls.Add(new CompositorDrawCall 
                    { 
                        Type = DrawCallType.Vector, 
                        IndexStart = pendingVectorStart, 
                        IndexCount = vecCount,
                    });
                    pendingVectorStart = (uint)_vectorIndicesList.Count;
                }

                uint textCount = (uint)_textVerticesList.Count - pendingTextStart;
                if (textCount > 0)
                {
                    staticDrawCalls.Add(new CompositorDrawCall 
                    { 
                        Type = DrawCallType.Text, 
                        IndexStart = pendingTextStart, 
                        IndexCount = textCount,
                    });
                    pendingTextStart = (uint)_textVerticesList.Count;
                }
            }

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
                        CommitStaticDrawCalls();
                        {
                            var pipeline = GetExtension(CompositorBuiltInExtensions.Hatch);
                            if (pipeline != null)
                            {
                                var localCmd = cmd;
                                pipeline.Compile(this, null, Matrix4x4.Identity, ref localCmd);
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = CompositorBuiltInExtensions.Hatch,
                                    Brush = cmd.Brush,
                                    Path = cmd.Path,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart)
                                });
                            }
                        }
                        pendingVectorStart = (uint)_vectorIndicesList.Count;
                        pendingTextStart = (uint)_textVerticesList.Count;
                        break;
                    case RenderCommandType.DrawAcisSolid:
                        CommitStaticDrawCalls();
                        {
                            var pipeline = GetExtension(CompositorBuiltInExtensions.AcisSolid);
                            if (pipeline != null)
                            {
                                var localCmd = cmd;
                                pipeline.Compile(this, null, Matrix4x4.Identity, ref localCmd);
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = CompositorBuiltInExtensions.AcisSolid,
                                    Pen = cmd.Pen,
                                    Transform = cmd.Transform,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart)
                                });
                            }
                        }
                        pendingVectorStart = (uint)_vectorIndicesList.Count;
                        pendingTextStart = (uint)_textVerticesList.Count;
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
                        CommitStaticDrawCalls();
                        {
                            var pipeline = GetExtension(CompositorBuiltInExtensions.Line3D);
                            if (pipeline != null)
                            {
                                var localCmd = cmd;
                                pipeline.Compile(this, null, Matrix4x4.Identity, ref localCmd);
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = CompositorBuiltInExtensions.Line3D,
                                    Pen = cmd.Pen,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart)
                                });
                            }
                        }
                        pendingVectorStart = (uint)_vectorIndicesList.Count;
                        pendingTextStart = (uint)_textVerticesList.Count;
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
                        CommitStaticDrawCalls();
                        {
                            var pipeline = GetExtension(CompositorBuiltInExtensions.Spline);
                            if (pipeline != null)
                            {
                                var localCmd = cmd;
                                pipeline.Compile(this, null, Matrix4x4.Identity, ref localCmd);
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = CompositorBuiltInExtensions.Spline,
                                    Pen = cmd.Pen,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart)
                                });
                            }
                        }
                        pendingVectorStart = (uint)_vectorIndicesList.Count;
                        pendingTextStart = (uint)_textVerticesList.Count;
                        break;
                    case RenderCommandType.DrawExtension:
                        {
                            var pipeline = GetExtension(cmd.ExtensionId);
                            if (pipeline != null)
                            {
                                CommitStaticDrawCalls();
                                var localCmd = cmd;
                                pipeline.Compile(this, null, Matrix4x4.Identity, ref localCmd);
                                var cmdTransform = localCmd.Transform;
                                if (cmdTransform == default || cmdTransform == new Matrix4x4())
                                {
                                    cmdTransform = Matrix4x4.Identity;
                                }
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = localCmd.ExtensionId,
                                    IntParam = localCmd.IntParam,
                                    FloatParam = localCmd.FloatParam,
                                    DataParam = localCmd.DataParam,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart),
                                    DoubleBufferOffset = localCmd.DoubleBufferOffset,
                                    DoubleBufferCount = localCmd.DoubleBufferCount,
                                    WeightBufferOffset = localCmd.WeightBufferOffset,
                                    WeightBufferCount = localCmd.WeightBufferCount,
                                    FloatBufferOffset = localCmd.FloatBufferOffset,
                                    FloatBufferCount = localCmd.FloatBufferCount,
                                    StaticBuffer = localCmd.StaticBuffer,
                                    Brush = localCmd.Brush,
                                    Pen = localCmd.Pen,
                                    Path = localCmd.Path,
                                    Transform = Matrix4x4.Identity * cmdTransform,
                                    LineThicknessOrRadius = localCmd.RadiusX,
                                    Scale = localCmd.Scale,
                                    Translate = localCmd.Translate,
                                    Color = (localCmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f),
                                    ClipRect = _activeClipRect,
                                    MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
                                    BlendMode = _activeBlendMode
                                });
                                pendingVectorStart = (uint)_vectorIndicesList.Count;
                                pendingTextStart = (uint)_textVerticesList.Count;
                            }
                        }
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
                v.ScaleBoldItalicUseMvp.W = 1.0f;
                _textVerticesList[i] = v;
            }

            CommitStaticDrawCalls();

            var staticBuffer = new DxfStaticBuffer(
                _context,
                _vectorVerticesList.ToArray(),
                _vectorIndicesList.ToArray(),
                _textVerticesList.ToArray(),
                _activeBrushes.ToArray(),
                staticDrawCalls.ToArray()
            );

            staticBuffer.TextRecords = _compiledTextRecords.ToArray();

            lock (_registeredExtensions)
            {
                foreach (var ext in _registeredExtensions)
                {
                    ext.EndStaticCompile(this, ActiveCompilationContext, staticBuffer);
                }
            }

            staticBuffer.InitializeBindGroups(
                _vectorUniformBindGroupLayout,
                _vectorUniformBindGroupLayoutOffscreen,
                _textUniformBindGroupLayout,
                _textUniformBindGroupLayoutOffscreen
            );

            return staticBuffer;
        }
        finally
        {
            _atlas.EndBatch();
            ActiveCompilationContext = null;

            // Restore dynamic lists
            _vectorVerticesList.Clear(); _vectorVerticesList.AddRange(savedVectorVertices);
            _vectorIndicesList.Clear(); _vectorIndicesList.AddRange(savedVectorIndices);
            _textVerticesList.Clear(); _textVerticesList.AddRange(savedTextVertices);
            _activeBrushes.Clear(); _activeBrushes.AddRange(savedActiveBrushes);
            
            _activeClipRect = savedActiveClipRect;
            _clipStack.Clear();
            for (int i = savedClipStack.Length - 1; i >= 0; i--)
            {
                _clipStack.Push(savedClipStack[i]);
            }
        }
    }

    public DxfStaticBuffer CompileStaticDxf(DrawingContext context, float staticZoom = 1.0f)
    {
        // Save current lists
        var savedVectorVertices = _vectorVerticesList.ToArray();
        var savedVectorIndices = _vectorIndicesList.ToArray();
        var savedTextVertices = _textVerticesList.ToArray();
        var savedActiveBrushes = _activeBrushes.ToArray();

        var savedActiveClipRect = _activeClipRect;
        var savedClipStack = _clipStack.ToArray();

        _activeClipRect = null;
        _clipStack.Clear();
        
        // Clear for compilation
        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _activeBrushes.Clear();
        _compiledTextRecords.Clear();

        ActiveCompilationContext = new StaticCompilationContext { StaticZoom = staticZoom };
        lock (_registeredExtensions)
        {
            foreach (var ext in _registeredExtensions)
            {
                ext.BeginStaticCompile(this, ActiveCompilationContext);
            }
        }

        try
        {
            _atlas.BeginBatch();
            var staticDrawCalls = new List<CompositorDrawCall>();
            uint pendingVectorStart = 0;
            uint pendingTextStart = 0;

            void CommitStaticDrawCalls()
            {
                uint vecCount = (uint)_vectorIndicesList.Count - pendingVectorStart;
                if (vecCount > 0)
                {
                    staticDrawCalls.Add(new CompositorDrawCall 
                    { 
                        Type = DrawCallType.Vector, 
                        IndexStart = pendingVectorStart, 
                        IndexCount = vecCount,
                    });
                    pendingVectorStart = (uint)_vectorIndicesList.Count;
                }

                uint textCount = (uint)_textVerticesList.Count - pendingTextStart;
                if (textCount > 0)
                {
                    staticDrawCalls.Add(new CompositorDrawCall 
                    { 
                        Type = DrawCallType.Text, 
                        IndexStart = pendingTextStart, 
                        IndexCount = textCount,
                    });
                    pendingTextStart = (uint)_textVerticesList.Count;
                }
            }
            
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
                        CommitStaticDrawCalls();
                        {
                            var pipeline = GetExtension(CompositorBuiltInExtensions.Hatch);
                            if (pipeline != null)
                            {
                                var localCmd = cmd;
                                pipeline.Compile(this, context, Matrix4x4.Identity, ref localCmd);
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = CompositorBuiltInExtensions.Hatch,
                                    Brush = cmd.Brush,
                                    Path = cmd.Path,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart)
                                });
                            }
                        }
                        pendingVectorStart = (uint)_vectorIndicesList.Count;
                        pendingTextStart = (uint)_textVerticesList.Count;
                        break;
                    case RenderCommandType.DrawAcisSolid:
                        CommitStaticDrawCalls();
                        {
                            var pipeline = GetExtension(CompositorBuiltInExtensions.AcisSolid);
                            if (pipeline != null)
                            {
                                var localCmd = cmd;
                                pipeline.Compile(this, context, Matrix4x4.Identity, ref localCmd);
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = CompositorBuiltInExtensions.AcisSolid,
                                    Pen = cmd.Pen,
                                    Transform = cmd.Transform,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart)
                                });
                            }
                        }
                        pendingVectorStart = (uint)_vectorIndicesList.Count;
                        pendingTextStart = (uint)_textVerticesList.Count;
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
                        CommitStaticDrawCalls();
                        {
                            var pipeline = GetExtension(CompositorBuiltInExtensions.Line3D);
                            if (pipeline != null)
                            {
                                var localCmd = cmd;
                                pipeline.Compile(this, context, Matrix4x4.Identity, ref localCmd);
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = CompositorBuiltInExtensions.Line3D,
                                    Pen = cmd.Pen,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart)
                                });
                            }
                        }
                        pendingVectorStart = (uint)_vectorIndicesList.Count;
                        pendingTextStart = (uint)_textVerticesList.Count;
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
                        CommitStaticDrawCalls();
                        {
                            var pipeline = GetExtension(CompositorBuiltInExtensions.Spline);
                            if (pipeline != null)
                            {
                                var localCmd = cmd;
                                pipeline.Compile(this, context, Matrix4x4.Identity, ref localCmd);
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = CompositorBuiltInExtensions.Spline,
                                    Pen = cmd.Pen,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart)
                                });
                            }
                        }
                        pendingVectorStart = (uint)_vectorIndicesList.Count;
                        pendingTextStart = (uint)_textVerticesList.Count;
                        break;
                    case RenderCommandType.DrawExtension:
                        {
                            var pipeline = GetExtension(cmd.ExtensionId);
                            if (pipeline != null)
                            {
                                CommitStaticDrawCalls();
                                var localCmd = cmd;
                                pipeline.Compile(this, context, Matrix4x4.Identity, ref localCmd);
                                var cmdTransform = localCmd.Transform;
                                if (cmdTransform == default || cmdTransform == new Matrix4x4())
                                {
                                    cmdTransform = Matrix4x4.Identity;
                                }
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = localCmd.ExtensionId,
                                    IntParam = localCmd.IntParam,
                                    FloatParam = localCmd.FloatParam,
                                    DataParam = localCmd.DataParam,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart),
                                    DoubleBufferOffset = localCmd.DoubleBufferOffset,
                                    DoubleBufferCount = localCmd.DoubleBufferCount,
                                    WeightBufferOffset = localCmd.WeightBufferOffset,
                                    WeightBufferCount = localCmd.WeightBufferCount,
                                    FloatBufferOffset = localCmd.FloatBufferOffset,
                                    FloatBufferCount = localCmd.FloatBufferCount,
                                    StaticBuffer = localCmd.StaticBuffer,
                                    Brush = localCmd.Brush,
                                    Pen = localCmd.Pen,
                                    Path = localCmd.Path,
                                    Transform = Matrix4x4.Identity * cmdTransform,
                                    LineThicknessOrRadius = localCmd.RadiusX,
                                    Scale = localCmd.Scale,
                                    Translate = localCmd.Translate,
                                    Color = (localCmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f),
                                    ClipRect = _activeClipRect,
                                    MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
                                    BlendMode = _activeBlendMode
                                });
                                pendingVectorStart = (uint)_vectorIndicesList.Count;
                                pendingTextStart = (uint)_textVerticesList.Count;
                            }
                        }
                        break;
                    case RenderCommandType.FillTriangle:
                        CompileFillTriangleCommand(cmd, Matrix4x4.Identity);
                        break;
                    case RenderCommandType.FillQuad:
                        CompileFillQuadCommand(cmd, Matrix4x4.Identity);
                        break;
                    case RenderCommandType.DrawGpuLineSeries:
                        CommitStaticDrawCalls();
                        {
                            var pipeline = GetExtension(CompositorBuiltInExtensions.GpuLineSeries);
                            if (pipeline != null)
                            {
                                var localCmd = cmd;
                                pipeline.Compile(this, context, Matrix4x4.Identity, ref localCmd);
                                var cmdTransform = localCmd.Transform;
                                if (cmdTransform == default || cmdTransform == new Matrix4x4())
                                {
                                    cmdTransform = Matrix4x4.Identity;
                                }
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = CompositorBuiltInExtensions.GpuLineSeries,
                                    LineThicknessOrRadius = cmd.RadiusX,
                                    Brush = cmd.Brush,
                                    Scale = cmd.Scale,
                                    Translate = cmd.Translate,
                                    StaticBuffer = localCmd.StaticBuffer,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart),
                                    Transform = Matrix4x4.Identity * cmdTransform,
                                    Color = (localCmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f),
                                    ClipRect = _activeClipRect
                                });
                            }
                        }
                        pendingVectorStart = (uint)_vectorIndicesList.Count;
                        pendingTextStart = (uint)_textVerticesList.Count;
                        break;
                    case RenderCommandType.DrawGpuScatterSeries:
                        CommitStaticDrawCalls();
                        {
                            var pipeline = GetExtension(CompositorBuiltInExtensions.GpuScatterSeries);
                            if (pipeline != null)
                            {
                                var localCmd = cmd;
                                pipeline.Compile(this, context, Matrix4x4.Identity, ref localCmd);
                                var cmdTransform = localCmd.Transform;
                                if (cmdTransform == default || cmdTransform == new Matrix4x4())
                                {
                                    cmdTransform = Matrix4x4.Identity;
                                }
                                staticDrawCalls.Add(new CompositorDrawCall
                                {
                                    Type = DrawCallType.Extension,
                                    ExtensionId = CompositorBuiltInExtensions.GpuScatterSeries,
                                    LineThicknessOrRadius = cmd.RadiusX,
                                    Brush = cmd.Brush,
                                    Scale = cmd.Scale,
                                    Translate = cmd.Translate,
                                    StaticBuffer = localCmd.StaticBuffer,
                                    PointBufferOffset = (int)pendingVectorStart,
                                    PointBufferCount = (int)((uint)_vectorIndicesList.Count - pendingVectorStart),
                                    Transform = Matrix4x4.Identity * cmdTransform,
                                    Color = (localCmd.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f),
                                    ClipRect = _activeClipRect
                                });
                            }
                        }
                        pendingVectorStart = (uint)_vectorIndicesList.Count;
                        pendingTextStart = (uint)_textVerticesList.Count;
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
                v.ScaleBoldItalicUseMvp.W = 1.0f;
                _textVerticesList[i] = v;
            }

            CommitStaticDrawCalls();

            var staticBuffer = new DxfStaticBuffer(
                _context,
                _vectorVerticesList.ToArray(),
                _vectorIndicesList.ToArray(),
                _textVerticesList.ToArray(),
                _activeBrushes.ToArray(),
                staticDrawCalls.ToArray()
            );

            staticBuffer.TextRecords = _compiledTextRecords.ToArray();

            lock (_registeredExtensions)
            {
                foreach (var ext in _registeredExtensions)
                {
                    ext.EndStaticCompile(this, ActiveCompilationContext, staticBuffer);
                }
            }

            staticBuffer.InitializeBindGroups(
                _vectorUniformBindGroupLayout,
                _vectorUniformBindGroupLayoutOffscreen,
                _textUniformBindGroupLayout,
                _textUniformBindGroupLayoutOffscreen
            );

            return staticBuffer;
        }
        finally
        {
            _atlas.EndBatch();
            ActiveCompilationContext = null;

            // Restore dynamic lists
            _vectorVerticesList.Clear(); _vectorVerticesList.AddRange(savedVectorVertices);
            _vectorIndicesList.Clear(); _vectorIndicesList.AddRange(savedVectorIndices);
            _textVerticesList.Clear(); _textVerticesList.AddRange(savedTextVertices);
            _activeBrushes.Clear(); _activeBrushes.AddRange(savedActiveBrushes);
            
            _activeClipRect = savedActiveClipRect;
            _clipStack.Clear();
            for (int i = savedClipStack.Length - 1; i >= 0; i--)
            {
                _clipStack.Push(savedClipStack[i]);
            }
        }
    }
    
    public void RecompileStaticText(DxfStaticBuffer staticBuffer, float staticZoom)
    {
        var savedTextVertices = _textVerticesList.ToArray();

        _textVerticesList.Clear();

        ActiveCompilationContext = new StaticCompilationContext { StaticZoom = staticZoom, IsRecompiling = true };

        try
        {
            _atlas.BeginBatch();
            foreach (var record in staticBuffer.TextRecords)
            {
                CompileTextCommand(record.Command, null, record.Transform);
            }

            for (int i = 0; i < _textVerticesList.Count; i++)
            {
                var v = _textVerticesList[i];
                v.ScaleBoldItalicUseMvp.W = 1.0f;
                _textVerticesList[i] = v;
            }

            staticBuffer.UpdateTextBuffer(_textVerticesList.ToArray());
        }
        finally
        {
            _atlas.EndBatch();
            ActiveCompilationContext = null;

            _textVerticesList.Clear();
            _textVerticesList.AddRange(savedTextVertices);
        }
    }

    internal unsafe void DrawStaticDxfBuffer(RenderPassEncoder* pass, object staticBufferObj, bool isOffscreen, GpuTexture? maskTexture = null)
    {
        if (staticBufferObj is not DxfStaticBuffer sb) return;
        
        var currentType = DrawCallType.StaticDxf;
        var maskBg = GetMaskBindGroup(maskTexture, isOffscreen);
        
        foreach (var dc in sb.DrawCalls)
        {
            if (dc.Type == DrawCallType.Vector)
            {
                if (currentType != DrawCallType.Vector)
                {
                    var pipeline = isOffscreen ? _vectorPipelineOffscreen : _vectorPipeline;
                    var uniformBg = isOffscreen ? sb.UniformBindGroupOffscreen : sb.UniformBindGroup;
                    var pathAtlasBg = isOffscreen ? _pathAtlasBindGroupOffscreen : _pathAtlasBindGroup;
                    
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, pipeline);
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, uniformBg, 0, null);
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, pathAtlasBg, 0, null);
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBg, 0, null);
                    
                    if (sb.VertexBuffer != null)
                    {
                        var buffer = sb.VertexBuffer.BufferPtr;
                        _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, sb.VertexBuffer.Size);
                        _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, sb.IndexBuffer.BufferPtr, IndexFormat.Uint32, 0, sb.IndexBuffer.Size);
                    }
                    currentType = DrawCallType.Vector;
                }
                _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Text)
            {
                if (currentType != DrawCallType.Text)
                {
                    var pipeline = isOffscreen ? _textPipelineOffscreen : _textPipeline;
                    var uniformBg = isOffscreen ? sb.TextUniformBindGroupOffscreen : sb.TextUniformBindGroup;
                    var atlasBg = isOffscreen ? _atlasBindGroupOffscreen : _atlasBindGroup;
                    
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, pipeline);
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, uniformBg, 0, null);
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, atlasBg, 0, null);
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBg, 0, null);
                    
                    if (sb.TextVertexBuffer != null)
                    {
                        var buffer = sb.TextVertexBuffer.BufferPtr;
                        _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, sb.TextVertexBuffer.Size);
                    }
                    currentType = DrawCallType.Text;
                }
                _context.Wgpu.RenderPassEncoderDraw(pass, 6, dc.IndexCount, 0, dc.IndexStart);
            }
            else if (dc.Type == DrawCallType.Extension)
            {
                var pipeline = GetExtension(dc.ExtensionId);
                if (pipeline != null)
                {
                    if (pipeline is ProGPU.Scene.Extensions.SplineExtensionPipeline)
                    {
                        if (currentType != DrawCallType.Vector)
                        {
                            var vectorPipeline = isOffscreen ? _vectorPipelineOffscreen : _vectorPipeline;
                            var uniformBg = isOffscreen ? sb.UniformBindGroupOffscreen : sb.UniformBindGroup;
                            var pathAtlasBg = isOffscreen ? _pathAtlasBindGroupOffscreen : _pathAtlasBindGroup;
                            
                            _context.Wgpu.RenderPassEncoderSetPipeline(pass, vectorPipeline);
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, uniformBg, 0, null);
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, pathAtlasBg, 0, null);
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBg, 0, null);
                            
                            if (sb.VertexBuffer != null)
                            {
                                var buffer = sb.VertexBuffer.BufferPtr;
                                _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, sb.VertexBuffer.Size);
                                _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, sb.IndexBuffer.BufferPtr, IndexFormat.Uint32, 0, sb.IndexBuffer.Size);
                            }
                            currentType = DrawCallType.Vector;
                        }
                        if (dc.PointBufferCount > 0)
                        {
                            _context.Wgpu.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
                        }
                    }
                    else
                    {
                        var localDc = dc;
                        if (localDc.StaticBuffer == null)
                        {
                            localDc.StaticBuffer = sb;
                        }
                        pipeline.Render(this, pass, isOffscreen, in localDc);
                        currentType = DrawCallType.Extension;
                    }
                }
            }
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
        _pendingTextStart = (uint)_textVerticesList.Count;
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
        _pendingTextStart = (uint)_textVerticesList.Count;
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

        uint instanceCount = (uint)(seriesBuffer.PointsCount - 1);
        wgpu.RenderPassEncoderDraw(pass, 6, instanceCount, 0, 0);
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

        uint instanceCount = (uint)seriesBuffer.PointsCount;
        wgpu.RenderPassEncoderDraw(pass, 6, instanceCount, 0, 0);
    }

    private struct MaskRenderPassInfo
    {
        public GpuTexture MaskTexture;
        public GpuTexture? PreviousMaskTexture;
        public List<CompositorDrawCall> DrawCalls;
    }

    private GpuTexture GetMaskTexture(uint width, uint height)
    {
        for (int i = 0; i < _maskTexturePool.Count; i++)
        {
            var tex = _maskTexturePool[i];
            if (tex.Width == width && tex.Height == height)
            {
                _maskTexturePool.RemoveAt(i);
                return tex;
            }
        }

        return new GpuTexture(
            _context,
            width,
            height,
            TextureFormat.R8Unorm,
            TextureUsage.TextureBinding | TextureUsage.RenderAttachment | TextureUsage.CopyDst,
            "Geometry Mask Texture"
        );
    }

    internal BindGroup* GetMaskBindGroup(GpuTexture? maskTexture, bool isOffscreen)
    {
        if (maskTexture == null)
        {
            return isOffscreen ? _dummyMaskBindGroupOffscreen : _dummyMaskBindGroup;
        }

        var cache = isOffscreen ? _maskBindGroupsOffscreen : _maskBindGroups;
        if (cache.TryGetValue(maskTexture, out var bgNint))
        {
            return (BindGroup*)bgNint;
        }

        var maskEntries = stackalloc BindGroupEntry[2];
        maskEntries[0] = new BindGroupEntry { Binding = 0, Sampler = _atlasSampler };
        maskEntries[1] = new BindGroupEntry { Binding = 1, TextureView = maskTexture.ViewPtr };

        var bgDescMask = new BindGroupDescriptor
        {
            Layout = isOffscreen ? _maskBindGroupLayoutOffscreen : _maskBindGroupLayout,
            EntryCount = 2,
            Entries = maskEntries
        };

        var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDescMask);
        if (bg == null)
        {
            throw new InvalidOperationException("Failed to create mask BindGroup");
        }

        cache[maskTexture] = (nint)bg;
        return bg;
    }

    private RenderPipeline* GetPipeline(DrawCallType type, GpuBlendMode blendMode, bool isOffscreen, TextureFormat? overrideFormat = null)
    {
        string baseName;
        ShaderModule* shaderModule;
        VertexBufferLayout[] layouts;
        uint sampleCount = isOffscreen ? 1u : 4u;

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
            layouts = new[] { layoutDesc };

            PipelineLayout* pipelineLayout = null;
            if (type == DrawCallType.Vector)
            {
                baseName = isOffscreen ? "Vector_Offscreen" : "Vector";
                shaderModule = _pipelineCache.GetOrCreateShader("Vector", Shaders.VectorShader, "VectorShader");
                pipelineLayout = isOffscreen ? _vectorPipelineLayoutOffscreen : _vectorPipelineLayout;
            }
            else if (type == DrawCallType.Text)
            {
                baseName = isOffscreen ? "Text_Offscreen" : "Text";
                shaderModule = _pipelineCache.GetOrCreateShader("Text", Shaders.TextShader, "TextShader");
                pipelineLayout = isOffscreen ? _textPipelineLayoutOffscreen : _textPipelineLayout;

                var textVertexAttribs = new VertexAttribute[]
                {
                    new() { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }, // SnappedLogicalPos
                    new() { Format = VertexFormat.Float32x2, Offset = 8, ShaderLocation = 1 }, // BasisX
                    new() { Format = VertexFormat.Float32x2, Offset = 16, ShaderLocation = 2 }, // BasisY
                    new() { Format = VertexFormat.Float32x4, Offset = 24, ShaderLocation = 3 }, // BearSize
                    new() { Format = VertexFormat.Float32x4, Offset = 40, ShaderLocation = 4 }, // TexCoords
                    new() { Format = VertexFormat.Float32x4, Offset = 56, ShaderLocation = 5 }, // Color
                    new() { Format = VertexFormat.Float32x4, Offset = 72, ShaderLocation = 6 }, // ScaleBoldItalicUseMvp
                    new() { Format = VertexFormat.Float32, Offset = 88, ShaderLocation = 7 }    // BrushIndex
                };

                fixed (VertexAttribute* textAttribsPtr = textVertexAttribs)
                {
                    var textLayoutDesc = new VertexBufferLayout
                    {
                        ArrayStride = (uint)Marshal.SizeOf<GlyphInstance>(),
                        StepMode = VertexStepMode.Instance,
                        AttributeCount = 8,
                        Attributes = textAttribsPtr
                    };

                    string textPipelineKey = overrideFormat.HasValue
                        ? $"{baseName}_{blendMode}_{overrideFormat.Value}"
                        : $"{baseName}_{blendMode}";

                    return _pipelineCache.GetOrCreateRenderPipeline(
                        textPipelineKey,
                        shaderModule,
                        "vs_main",
                        "fs_main",
                        overrideFormat ?? RenderFormat,
                        PrimitiveTopology.TriangleList,
                        new[] { textLayoutDesc },
                        enableBlend: true,
                        enableDepthStencil: false,
                        sampleCount: sampleCount,
                        blendMode: blendMode,
                        pipelineLayout: pipelineLayout
                    );
                }
            }
            else if (type == DrawCallType.Texture)
            {
                baseName = isOffscreen ? "Texture_Offscreen" : "Texture";
                shaderModule = _pipelineCache.GetOrCreateShader("Texture", Shaders.TextureShader, "TextureShader");
                pipelineLayout = isOffscreen ? _texturePipelineLayoutOffscreen : _texturePipelineLayout;
            }
            else
            {
                throw new ArgumentException($"Unsupported pipeline draw call type: {type}");
            }

            string pipelineKey = overrideFormat.HasValue
                ? $"{baseName}_{blendMode}_{overrideFormat.Value}"
                : $"{baseName}_{blendMode}";
            
            return _pipelineCache.GetOrCreateRenderPipeline(
                pipelineKey,
                shaderModule,
                "vs_main",
                "fs_main",
                overrideFormat ?? RenderFormat,
                PrimitiveTopology.TriangleList,
                layouts,
                enableBlend: true,
                enableDepthStencil: false,
                sampleCount: sampleCount,
                blendMode: blendMode,
                pipelineLayout: pipelineLayout
            );
        }
    }

    private void PushGeometryMask(PathGeometry geometry, Matrix4x4 transform)
    {
        CommitPendingDrawCalls();
        int preDrawCallCount = _drawCalls.Count;

        var cmd = new RenderCommand
        {
            Type = RenderCommandType.DrawPath,
            Path = geometry,
            Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
        };
        CompilePathCommand(cmd, transform);
        CommitPendingDrawCalls();

        var maskDrawCalls = new List<CompositorDrawCall>();
        for (int i = preDrawCallCount; i < _drawCalls.Count; i++)
        {
            maskDrawCalls.Add(_drawCalls[i]);
        }
        _drawCalls.RemoveRange(preDrawCallCount, _drawCalls.Count - preDrawCallCount);

        var maskTex = GetMaskTexture(_currentWidth, _currentHeight);
        var prevMask = _maskStack.Count > 0 ? _maskStack.Peek() : null;

        _maskRenderPasses.Add(new MaskRenderPassInfo
        {
            MaskTexture = maskTex,
            PreviousMaskTexture = prevMask,
            DrawCalls = maskDrawCalls
        });

        _maskStack.Push(maskTex);
    }

    private void PushOpacityMaskValue(Brush brush, Rect bounds, Matrix4x4 transform)
    {
        CommitPendingDrawCalls();
        int preDrawCallCount = _drawCalls.Count;

        var cmd = new RenderCommand
        {
            Type = RenderCommandType.DrawRect,
            Rect = bounds,
            Brush = brush
        };
        CompileRectCommand(cmd, transform);
        CommitPendingDrawCalls();

        var maskDrawCalls = new List<CompositorDrawCall>();
        for (int i = preDrawCallCount; i < _drawCalls.Count; i++)
        {
            maskDrawCalls.Add(_drawCalls[i]);
        }
        _drawCalls.RemoveRange(preDrawCallCount, _drawCalls.Count - preDrawCallCount);

        var maskTex = GetMaskTexture(_currentWidth, _currentHeight);
        var prevMask = _maskStack.Count > 0 ? _maskStack.Peek() : null;

        _maskRenderPasses.Add(new MaskRenderPassInfo
        {
            MaskTexture = maskTex,
            PreviousMaskTexture = prevMask,
            DrawCalls = maskDrawCalls
        });

        _maskStack.Push(maskTex);
    }

    private void PopGeometryMask()
    {
        CommitPendingDrawCalls();
        if (_maskStack.Count > 0)
        {
            var popped = _maskStack.Pop();
            _masksToReturnToPool.Add(popped);
        }
    }

    private void PopOpacityMaskValue()
    {
        CommitPendingDrawCalls();
        if (_maskStack.Count > 0)
        {
            var popped = _maskStack.Pop();
            _masksToReturnToPool.Add(popped);
        }
    }

    private void ExecuteMaskRenderPasses(CommandEncoder* encoder, bool isOffscreen)
    {
        if (_maskRenderPasses.Count == 0) return;

        foreach (var maskPass in _maskRenderPasses)
        {
            var targetView = maskPass.MaskTexture.ViewPtr;

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

            var maskBindGroup = GetMaskBindGroup(maskPass.PreviousMaskTexture, isOffscreen: true);

            DrawCallType? currentType = null;
            var textureEntries = stackalloc BindGroupEntry[2];

            foreach (var dc in maskPass.DrawCalls)
            {
                ApplyDrawCallScissor(pass, dc);

                if (dc.Type == DrawCallType.Vector)
                {
                    if (currentType != DrawCallType.Vector)
                    {
                        var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: true, overrideFormat: TextureFormat.R8Unorm);
                        _context.Wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
                        fixed (BindGroup** pGrp = &_vectorUniformBindGroupOffscreen)
                        {
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                        }
                        fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroupOffscreen)
                        {
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                        }
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);

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
                        var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: true, overrideFormat: TextureFormat.R8Unorm);
                        _context.Wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
                        fixed (BindGroup** pGrp = &_textUniformBindGroupOffscreen)
                        {
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                        }
                        fixed (BindGroup** pAtlas = &_atlasBindGroupOffscreen)
                        {
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
                        }
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);

                        var buffer = _textVertexBuffer.BufferPtr;
                        _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textVertexBuffer.Size);
                        currentType = DrawCallType.Text;
                    }
                    _context.Wgpu.RenderPassEncoderDraw(pass, 6, dc.IndexCount, 0, dc.IndexStart);
                }
                else if (dc.Type == DrawCallType.Texture && dc.Texture != null)
                {
                    var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: true, overrideFormat: TextureFormat.R8Unorm);
                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, activePipeline);
                    fixed (BindGroup** pGrp = &_textureUniformBindGroupOffscreen)
                    {
                        _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    var buffer = _textureVertexBuffer.BufferPtr;
                    _context.Wgpu.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textureVertexBuffer.Size);
                    _context.Wgpu.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textureIndexBuffer.Size);
                    currentType = DrawCallType.Texture;

                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);

                    var viewPtr = dc.Texture.ViewPtr;
                    var cacheKey = new TextureCacheKey(dc.Texture.Id, dc.Texture.Generation, isOffscreen: true);

                    CachedBindGroup? cachedBg;
                    lock (_persistentTextureBindGroups)
                    {
                        if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out cachedBg))
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
                    }

                    var bindGroup = (BindGroup*)cachedBg.BindGroupPtr;
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);
                    _context.Wgpu.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
                }
            }

            _context.Wgpu.RenderPassEncoderEnd(pass);
            _context.Wgpu.RenderPassEncoderRelease(pass);
        }
    }

    private unsafe BindGroupLayout* CreateVectorUniformLayout()
    {
        var entries = stackalloc BindGroupLayoutEntry[2];
        entries[0] = new BindGroupLayoutEntry
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
        entries[1] = new BindGroupLayoutEntry
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

        var desc = new BindGroupLayoutDescriptor
        {
            EntryCount = (UIntPtr)2,
            Entries = entries
        };

        return _context.Wgpu.DeviceCreateBindGroupLayout(_context.Device, &desc);
    }

    private unsafe BindGroupLayout* CreateUniformOnlyLayout()
    {
        var entry = new BindGroupLayoutEntry
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

        var desc = new BindGroupLayoutDescriptor
        {
            EntryCount = (UIntPtr)1,
            Entries = &entry
        };

        return _context.Wgpu.DeviceCreateBindGroupLayout(_context.Device, &desc);
    }

    private unsafe BindGroupLayout* CreateSamplerTextureLayout()
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
                ViewDimension = TextureViewDimension.TextureViewDimension2D,
                Multisampled = false
            }
        };

        var desc = new BindGroupLayoutDescriptor
        {
            EntryCount = (UIntPtr)2,
            Entries = entries
        };

        return _context.Wgpu.DeviceCreateBindGroupLayout(_context.Device, &desc);
    }
}
