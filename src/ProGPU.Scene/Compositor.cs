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

[StructLayout(LayoutKind.Explicit, Size = 256)]
public struct GpuBrush
{
    [FieldOffset(0)] public uint Type;             // 0 = Solid, 1 = Linear, 2 = Radial, 5 = Two-point conical
    [FieldOffset(4)] public float Opacity;
    [FieldOffset(8)] public Vector2 StartPoint;
    [FieldOffset(16)] public Vector2 EndPoint;
    [FieldOffset(24)] public Vector2 Center;
    [FieldOffset(32)] public float Radius;
    [FieldOffset(36)] public uint StopCount;
    [FieldOffset(40)] public float RadiusY;
    [FieldOffset(44)] public uint SpreadMethod;
    [FieldOffset(48)] public uint ColorInterpolationMode;
    [FieldOffset(52)] public uint StopOffset;

    [FieldOffset(64)] public Vector4 Color0;
    [FieldOffset(80)] public Vector4 Color1;
    [FieldOffset(96)] public Vector4 Color2;
    [FieldOffset(112)] public Vector4 Color3;
    [FieldOffset(128)] public Vector4 Color4;
    [FieldOffset(144)] public Vector4 Color5;
    [FieldOffset(160)] public Vector4 Color6;
    [FieldOffset(176)] public Vector4 Color7;
    [FieldOffset(192)] public Vector4 Offsets;
    [FieldOffset(208)] public Vector4 Offsets1;
    [FieldOffset(224)] public Vector4 CoordinateTransform0;
    [FieldOffset(240)] public Vector4 CoordinateTransform1;
}

[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct GpuGradientStop
{
    [FieldOffset(0)] public Vector4 Color;
    [FieldOffset(16)] public float Offset;
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

public readonly record struct RenderTargetViewport(float X, float Y, float Width, float Height)
{
    public static RenderTargetViewport Full(uint width, uint height)
    {
        return new RenderTargetViewport(
            0f,
            0f,
            Math.Max(1u, width),
            Math.Max(1u, height));
    }
}

public unsafe class Compositor : IDisposable
{
    internal const int MaxGradientStops = 65536;
    private const float StrokeEpsilon = 0.0001f;
    private const float AliasedShapeTypeOffset = 1000f;
    private const float ArcSdfShapeType = 12f;

    public struct StaticTextRecord
    {
        public RenderCommand Command;
        public Matrix4x4 Transform;
    }

    private static float EncodeShapeType(RenderCommand command, float shapeType)
    {
        return command.IsEdgeAliased ? shapeType + AliasedShapeTypeOffset : shapeType;
    }

    private static float EncodeShapeType(bool isEdgeAliased, float shapeType)
    {
        return isEdgeAliased ? shapeType + AliasedShapeTypeOffset : shapeType;
    }

    private static float EncodeTextFlags(bool useMvp, TextRenderingMode textRenderingMode)
    {
        return textRenderingMode switch
        {
            TextRenderingMode.Aliased => useMvp ? -2f : -1f,
            TextRenderingMode.ClearType => useMvp ? 3f : 2f,
            _ => useMvp ? 1f : 0f
        };
    }

    private static float ForceTextUseMvp(float encodedFlags)
    {
        return EncodeTextFlags(useMvp: true, DecodeTextRenderingMode(encodedFlags));
    }

    private static TextRenderingMode DecodeTextRenderingMode(float encodedFlags)
    {
        if (encodedFlags < -0.5f)
        {
            return TextRenderingMode.Aliased;
        }

        if (encodedFlags > 1.5f)
        {
            return TextRenderingMode.ClearType;
        }

        return TextRenderingMode.Grayscale;
    }

    private static (byte SubpixelX, Vector2 SnappedLogicalPos) ResolveTextPlacement(
        Vector2 transformedPosition,
        float dpiScale,
        float rasterFontSize,
        bool isRotated,
        TextHintingMode textHintingMode)
    {
        if (textHintingMode == TextHintingMode.Animated)
        {
            return (0, transformedPosition);
        }

        var transformedPhysicalPosition = transformedPosition * dpiScale;
        if (!isRotated && rasterFontSize <= 24f)
        {
            float screenX = transformedPhysicalPosition.X;
            float screenY = transformedPhysicalPosition.Y;
            float ipartX = MathF.Floor(screenX);
            float fpartX = screenX - ipartX;
            int subpixelIndex = (int)MathF.Round(fpartX * 4f);
            if (subpixelIndex == 4)
            {
                subpixelIndex = 0;
                ipartX += 1.0f;
            }

            var snapped = new Vector2(ipartX, MathF.Round(screenY)) / dpiScale;
            return ((byte)subpixelIndex, snapped);
        }

        if (!isRotated)
        {
            var snapped = new Vector2(
                MathF.Round(transformedPhysicalPosition.X),
                MathF.Round(transformedPhysicalPosition.Y)) / dpiScale;
            return (0, snapped);
        }

        return (0, transformedPosition);
    }

    private readonly List<StaticTextRecord> _compiledTextRecords = new();

    public CompositorMetrics Metrics { get; private set; }

    private readonly List<ICompositorExtension> _registeredExtensions = new();
    private readonly Dictionary<int, ICompositorExtension> _extensionsById = new();
    private int _extensionFrameDepth;

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

    private bool BeginExtensionFrame()
    {
        bool ownsFrame = _extensionFrameDepth++ == 0;
        if (!ownsFrame)
        {
            return false;
        }

        try
        {
            lock (_registeredExtensions)
            {
                foreach (var ext in _registeredExtensions)
                {
                    ext.BeginFrame(this);
                }
            }

            return true;
        }
        catch
        {
            _extensionFrameDepth--;
            throw;
        }
    }

    private void EndExtensionFrame(bool ownsFrame)
    {
        try
        {
            if (ownsFrame)
            {
                lock (_registeredExtensions)
                {
                    foreach (var ext in _registeredExtensions)
                    {
                        ext.EndFrame(this);
                    }
                }
            }
        }
        finally
        {
            _extensionFrameDepth--;
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
    internal unsafe BindGroupLayout* MaskBindGroupLayout => _maskBindGroupLayout;
    internal unsafe BindGroupLayout* MaskBindGroupLayoutOffscreen => _maskBindGroupLayoutOffscreen;

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
    private uint? _explicitRenderTargetWidth;
    private uint? _explicitRenderTargetHeight;
    private RenderTargetViewport? _explicitRenderTargetViewport;
    private float? _explicitDpiScale;

    // Sampler & Texture Bind Group for Typography
    private Sampler* _atlasSampler;
    private Sampler* _nearestTextureSampler;
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
        public TextureSamplingMode TextureSamplingMode;
        public GpuTextureAlphaMode TextureAlphaMode;

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
        public readonly TextureSamplingMode SamplingMode;

        public TextureCacheKey(ulong textureId, uint generation, bool isOffscreen, TextureSamplingMode samplingMode)
        {
            TextureId = textureId;
            Generation = generation;
            IsOffscreen = isOffscreen;
            SamplingMode = samplingMode;
        }

        public bool Equals(TextureCacheKey other) => TextureId == other.TextureId && Generation == other.Generation && IsOffscreen == other.IsOffscreen && SamplingMode == other.SamplingMode;
        public override bool Equals(object? obj) => obj is TextureCacheKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(TextureId, Generation, IsOffscreen, SamplingMode);
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
    private readonly List<GpuGradientStop> _activeGradientStops = new();
    private readonly GpuBuffer _brushesStorageBuffer;
    private readonly GpuBuffer _gradientStopsStorageBuffer;
    private ulong _frameNumber = 0;
    private float _totalTime = 0f;
    private readonly Dictionary<(string Text, TtfFont Font, float Size, TextAlignment Align), TextLayout> _layoutCache = new();
    private enum BatchType
    {
        None,
        Vector,
        Text
    }

    [Flags]
    private enum VisualCompositeScope
    {
        None = 0,
        Clip = 1,
        Opacity = 2,
        OpacityMask = 4
    }

    private BatchType _currentBatchType = BatchType.None;
    private uint _pendingVectorStart = 0;
    private uint _pendingTextStart = 0;

    private readonly ComputeAccelerator _compute;
    private readonly Dictionary<Visual, (GpuTexture Source, GpuTexture Temp, GpuTexture Destination)> _effectTextures = new();
    private readonly Dictionary<Visual, int> _effectCacheKeys = new();
    private readonly Dictionary<Visual, WpfShaderEffectParams> _wpfShaderEffectDrawParams = new();
    private readonly HashSet<Visual> _elementsRenderingEffects = new();
    private readonly HashSet<Visual> _elementsRenderingLayers = new();
    private readonly Dictionary<Visual, GpuTexture> _allocatedLayerTextures = new();
    private readonly HashSet<Visual> _activeLayerTextureOwners = new();

    private bool _isDisposed;
    public bool IsDisposed => _isDisposed;

    private readonly Stack<Rect> _clipStack = new();
    private readonly Stack<bool> _clipScopeIsGeometryMask = new();
    private Rect? _activeClipRect;

    private readonly Stack<float> _opacityStack = new();
    private float _activeOpacity = 1.0f;

    public static float DefaultTextGamma = 1.43f;
    public static float DefaultTextContrast = 1.15f;
    public static bool IsCacheAsLayerEnabled { get; set; } = true;

    public int VectorVertexCount => _vectorVerticesList.Count;
    public List<VectorVertex> VectorVertices => _vectorVerticesList;
    public List<uint> VectorIndices => _vectorIndicesList;

    public WgpuContext Context => _context;
    internal RenderPipelineCache PipelineCache => _pipelineCache;
    internal GpuBuffer VectorVertexBuffer => _vectorVertexBuffer;
    internal GpuBuffer VectorIndexBuffer => _vectorIndexBuffer;
    internal GpuBuffer VectorUniformBuffer => _uniformBuffer;
    internal GpuBuffer BrushesStorageBuffer => _brushesStorageBuffer;
    internal GpuBuffer GradientStopsStorageBuffer => _gradientStopsStorageBuffer;
    internal uint CurrentWidth => _currentWidth;
    internal uint CurrentHeight => _currentHeight;
    internal float CurrentCanvasPixelX => _explicitRenderTargetViewport.HasValue
        ? _explicitRenderTargetViewport.Value.X
        : 0f;

    internal float CurrentCanvasPixelY => _explicitRenderTargetViewport.HasValue
        ? _explicitRenderTargetViewport.Value.Y
        : 0f;

    internal float CurrentCanvasPixelWidth => _explicitRenderTargetViewport.HasValue
        ? _explicitRenderTargetViewport.Value.Width
        : _explicitRenderTargetWidth.HasValue
        ? _explicitRenderTargetWidth.Value
        : MathF.Max(1f, _currentWidth * (_currentDpiScale > 0f ? _currentDpiScale : 1f));

    internal float CurrentCanvasPixelHeight => _explicitRenderTargetViewport.HasValue
        ? _explicitRenderTargetViewport.Value.Height
        : _explicitRenderTargetHeight.HasValue
        ? _explicitRenderTargetHeight.Value
        : MathF.Max(1f, _currentHeight * (_currentDpiScale > 0f ? _currentDpiScale : 1f));
    private uint CurrentCanvasPixelXUInt => RoundNonNegativeToUInt(CurrentCanvasPixelX);
    private uint CurrentCanvasPixelYUInt => RoundNonNegativeToUInt(CurrentCanvasPixelY);
    private uint CurrentCanvasPixelWidthUInt => Math.Max(1u, RoundNonNegativeToUInt(CurrentCanvasPixelWidth));
    private uint CurrentCanvasPixelHeightUInt => Math.Max(1u, RoundNonNegativeToUInt(CurrentCanvasPixelHeight));
    private uint CurrentMaskTargetPixelWidthUInt => _explicitRenderTargetViewport.HasValue && _explicitRenderTargetWidth.HasValue
        ? Math.Max(1u, _explicitRenderTargetWidth.Value)
        : CurrentCanvasPixelWidthUInt;

    private uint CurrentMaskTargetPixelHeightUInt => _explicitRenderTargetViewport.HasValue && _explicitRenderTargetHeight.HasValue
        ? Math.Max(1u, _explicitRenderTargetHeight.Value)
        : CurrentCanvasPixelHeightUInt;
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

    private Vector4 ResolveDrawCallBrushColor(Brush? brush)
    {
        Vector4 color = brush is SolidColorBrush solid
            ? solid.Color
            : new Vector4(1f, 1f, 1f, 1f);
        color.W *= (brush?.Opacity ?? 1f) * _activeOpacity;
        return color;
    }

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

        // Allocate brushes storage buffer for the fixed GPU brush ABI.
        _brushesStorageBuffer = new GpuBuffer(
            _context,
            8192u * (uint)Marshal.SizeOf<GpuBrush>(),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "Compositor Brushes Storage Buffer"
        );
        _gradientStopsStorageBuffer = new GpuBuffer(
            _context,
            (uint)MaxGradientStops * (uint)Marshal.SizeOf<GpuGradientStop>(),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "Compositor Gradient Stops Storage Buffer"
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
        RegisterExtension(CompositorBuiltInExtensions.WpfShaderEffect, new WpfShaderEffectExtensionPipeline());

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

        var nearestSamplerDesc = samplerDesc;
        nearestSamplerDesc.MagFilter = FilterMode.Nearest;
        nearestSamplerDesc.MinFilter = FilterMode.Nearest;
        nearestSamplerDesc.MipmapFilter = MipmapFilterMode.Nearest;
        _nearestTextureSampler = _context.Wgpu.DeviceCreateSampler(_context.Device, &nearestSamplerDesc);

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
                pipelineLayout: _texturePipelineLayout,
                sourceAlphaMode: GpuTextureAlphaMode.Premultiplied
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
                pipelineLayout: _texturePipelineLayoutOffscreen,
                sourceAlphaMode: GpuTextureAlphaMode.Premultiplied
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

        var gradientStopsEntry = new BindGroupEntry
        {
            Binding = 2,
            Buffer = _gradientStopsStorageBuffer.BufferPtr,
            Offset = 0,
            Size = _gradientStopsStorageBuffer.Size
        };

        var vectorEntries = stackalloc BindGroupEntry[3];
        vectorEntries[0] = uBufferEntryVector;
        vectorEntries[1] = brushesEntry;
        vectorEntries[2] = gradientStopsEntry;

        var uDescVector = new BindGroupDescriptor
        {
            Layout = _vectorUniformBindGroupLayout,
            EntryCount = 3,
            Entries = vectorEntries
        };
        _vectorUniformBindGroup = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &uDescVector);

        var uDescVectorOffscreen = new BindGroupDescriptor
        {
            Layout = _vectorUniformBindGroupLayoutOffscreen,
            EntryCount = 3,
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

    public void RenderScene(
        Visual root,
        uint logicalWidth,
        uint logicalHeight,
        uint renderTargetWidth,
        uint renderTargetHeight,
        float dpiScale,
        TextureView* targetView)
    {
        RenderScene(
            root,
            logicalWidth,
            logicalHeight,
            renderTargetWidth,
            renderTargetHeight,
            RenderTargetViewport.Full(renderTargetWidth, renderTargetHeight),
            dpiScale,
            targetView);
    }

    public void RenderScene(
        Visual root,
        uint logicalWidth,
        uint logicalHeight,
        uint renderTargetWidth,
        uint renderTargetHeight,
        RenderTargetViewport renderTargetViewport,
        float dpiScale,
        TextureView* targetView)
    {
        var previousRenderTargetWidth = _explicitRenderTargetWidth;
        var previousRenderTargetHeight = _explicitRenderTargetHeight;
        var previousRenderTargetViewport = _explicitRenderTargetViewport;
        var previousDpiScale = _explicitDpiScale;
        renderTargetWidth = Math.Max(1u, renderTargetWidth);
        renderTargetHeight = Math.Max(1u, renderTargetHeight);
        _explicitRenderTargetWidth = renderTargetWidth;
        _explicitRenderTargetHeight = renderTargetHeight;
        _explicitRenderTargetViewport = NormalizeRenderTargetViewport(
            renderTargetViewport,
            renderTargetWidth,
            renderTargetHeight);
        _explicitDpiScale = float.IsFinite(dpiScale) && dpiScale > 0f ? dpiScale : 1f;

        try
        {
            RenderScene(root, logicalWidth, logicalHeight, targetView);
        }
        finally
        {
            _explicitRenderTargetWidth = previousRenderTargetWidth;
            _explicitRenderTargetHeight = previousRenderTargetHeight;
            _explicitRenderTargetViewport = previousRenderTargetViewport;
            _explicitDpiScale = previousDpiScale;
        }
    }

    public void RenderScene(Visual root, uint width, uint height, TextureView* targetView)
    {
        if (_isDisposed) return;

        using var currentContextScope = WgpuContext.PushCurrent(_context);

        _context.CleanupPendingResources();

        _currentWidth = width;
        _currentHeight = height;
        _currentDpiScale = _explicitDpiScale ?? 1.0f;
        if (!_explicitDpiScale.HasValue &&
            _context.Window != null &&
            width == (uint)_context.Window.Size.X &&
            height == (uint)_context.Window.Size.Y)
        {
            _currentDpiScale = (float)_context.Window.FramebufferSize.X / _context.Window.Size.X;
        }

        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var compileSw = System.Diagnostics.Stopwatch.StartNew();
        _pathAtlas.CleanupFrame();
        _activeLayerTextureOwners.Clear();

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
        _activeGradientStops.Clear();
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
        _clipScopeIsGeometryMask.Clear();
        _activeClipRect = null;

        _opacityStack.Clear();
        _activeOpacity = 1.0f;
        _currentBatchType = BatchType.None;

        _blendModeStack.Clear();
        _activeBlendMode = GpuBlendMode.SrcOver;
        _maskStack.Clear();
        _maskRenderPasses.Clear();
        _masksToReturnToPool.Clear();

        var extensionFrame = BeginExtensionFrame();
        CommandEncoder* encoder = null;
        IReadOnlyList<Visual>? externalLayers = null;
        Visual? activeToolTip = null;
        System.Diagnostics.Stopwatch uploadSw = null!;
        System.Diagnostics.Stopwatch passSw = null!;
        try
        {

        // 3. Compile Layer 0: Root Visual Scene
        _pendingVectorStart = (uint)_vectorIndicesList.Count;
        _pendingTextStart = (uint)_textVerticesList.Count;
        CompileVisualTree(root, Matrix4x4.Identity);
        CommitPendingDrawCalls();

        // 4. Compile Layer 1: Active Popups / External Layers (in proper Z-order)
        externalLayers = GetExternalLayers?.Invoke();
        if (externalLayers != null && externalLayers.Count > 0)
        {
            var savedActiveClipRect = _activeClipRect;
            var savedClipStack = _clipStack.ToArray();
            var savedClipScopeIsGeometryMask = _clipScopeIsGeometryMask.ToArray();
            var savedActiveOpacity = _activeOpacity;
            var savedOpacityStack = _opacityStack.ToArray();

            for (int i = 0; i < externalLayers.Count; i++)
            {
                _activeClipRect = null;
                _clipStack.Clear();
                _clipScopeIsGeometryMask.Clear();
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
            RestoreClipScopeStack(savedClipScopeIsGeometryMask);
            _activeOpacity = savedActiveOpacity;
            _opacityStack.Clear();
            for (int j = savedOpacityStack.Length - 1; j >= 0; j--)
            {
                _opacityStack.Push(savedOpacityStack[j]);
            }
        }

        // 5. Compile Layer 2: Tooltips
        activeToolTip = GetTooltip?.Invoke();
        if (activeToolTip != null)
        {
            var savedActiveClipRect = _activeClipRect;
            var savedClipStack = _clipStack.ToArray();
            var savedClipScopeIsGeometryMask = _clipScopeIsGeometryMask.ToArray();
            var savedActiveOpacity = _activeOpacity;
            var savedOpacityStack = _opacityStack.ToArray();

            _activeClipRect = null;
            _clipStack.Clear();
            _clipScopeIsGeometryMask.Clear();
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
            RestoreClipScopeStack(savedClipScopeIsGeometryMask);
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
            var savedClipScopeIsGeometryMask = _clipScopeIsGeometryMask.ToArray();
            var savedActiveOpacity = _activeOpacity;
            var savedOpacityStack = _opacityStack.ToArray();

            _activeClipRect = null;
            _clipStack.Clear();
            _clipScopeIsGeometryMask.Clear();
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
                            ClipRect = _activeClipRect,
                            MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
                            BlendMode = _activeBlendMode
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
            RestoreClipScopeStack(savedClipScopeIsGeometryMask);
            _activeOpacity = savedActiveOpacity;
            _opacityStack.Clear();
            for (int j = savedOpacityStack.Length - 1; j >= 0; j--)
            {
                _opacityStack.Push(savedOpacityStack[j]);
            }
        }

        compileSw.Stop();
        uploadSw = System.Diagnostics.Stopwatch.StartNew();

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
        uint renderWidth = _explicitRenderTargetWidth ?? width;
        uint renderHeight = _explicitRenderTargetHeight ?? height;
        if (!_explicitRenderTargetWidth.HasValue &&
            _context.Window != null &&
            width == (uint)_context.Window.Size.X &&
            height == (uint)_context.Window.Size.Y)
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
        if (_activeGradientStops.Count > 0)
        {
            _gradientStopsStorageBuffer.Write(CollectionsMarshal.AsSpan(_activeGradientStops));
        }



        // Rasterize all pending paths before starting the render pass
        _pathAtlas.RasterizePendingPaths();

        uploadSw.Stop();
        passSw = System.Diagnostics.Stopwatch.StartNew();

        // Recreate MSAA resources if needed (handles initialization and window resizing)
        if (_msaaTexture == null || _msaaWidth != renderWidth || _msaaHeight != renderHeight)
        {
            ReleaseMsaaResources();
            CreateMsaaResources(renderWidth, renderHeight);
        }

        // 5. WebGPU Command Encoder and Render Pass Execution
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compositor Command Encoder") };
        encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
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
        ApplyRenderPassViewport(pass, renderWidth, renderHeight, useRenderTargetViewport: true);

        DrawCallType? currentType = null;
        GpuBlendMode? currentBlendMode = null;
        GpuTexture? currentMaskTexture = null;
        var textureEntries = stackalloc BindGroupEntry[2];

        foreach (var dc in _drawCalls)
        {
            if (!ApplyDrawCallScissor(pass, dc, useRenderTargetViewport: true))
            {
                continue;
            }

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
            else if (dc.Type == DrawCallType.Texture && IsTextureBindable(dc.Texture))
            {
                var texture = dc.Texture!;
                var activePipeline = GetPipeline(
                    dc.Type,
                    dc.BlendMode,
                    isOffscreen: false,
                    textureAlphaMode: dc.TextureAlphaMode);
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

                var viewPtr = texture.ViewPtr;
                var cacheKey = new TextureCacheKey(texture.Id, texture.Generation, isOffscreen: false, dc.TextureSamplingMode);

                CachedBindGroup? cachedBg;
                lock (_persistentTextureBindGroups)
                {
                    if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out cachedBg))
                    {
                        textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = GetTextureSampler(dc.TextureSamplingMode) };
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
                DrawStaticDxfBuffer(pass, dc.StaticBuffer, isOffscreen: false, dc.MaskTexture, dc.BlendMode);
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
                        var selectedVectorPipeline = currentType != DrawCallType.Vector || currentBlendMode != dc.BlendMode;
                        if (selectedVectorPipeline)
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

                        if (selectedVectorPipeline || currentMaskTexture != dc.MaskTexture)
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
        }
        finally
        {
            EndExtensionFrame(extensionFrame);
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
        SweepUnusedLayerTextures(root, externalLayers, activeToolTip);
        SweepUnusedEffectTextures(root, externalLayers, activeToolTip);
        _activeLayerTextureOwners.Clear();

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
                    QueueBindGroupRelease(kvp.Value.BindGroupPtr);
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

    private bool IsTextureBindable(GpuTexture? texture)
    {
        return texture != null
            && !texture.IsDisposed
            && !texture.Context.IsDisposed
            && ReferenceEquals(texture.Context, _context)
            && texture.TexturePtr != null
            && texture.ViewPtr != null;
    }

    private void HandleTextureDisposed(ulong textureId)
    {
        if (Environment.HasShutdownStarted) return;

        RemoveMaskTexturePoolEntries(textureId);

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
                        QueueBindGroupRelease(cachedBg.BindGroupPtr);
                        _persistentTextureBindGroups.Remove(key);
                    }
                }
            }
        }

        RemoveMaskBindGroups(_maskBindGroups, textureId);
        RemoveMaskBindGroups(_maskBindGroupsOffscreen, textureId);
    }

    private void RemoveMaskTexturePoolEntries(ulong textureId)
    {
        for (int i = _maskTexturePool.Count - 1; i >= 0; i--)
        {
            if (_maskTexturePool[i].Id == textureId)
            {
                _maskTexturePool.RemoveAt(i);
            }
        }
    }

    private void RemoveMaskBindGroups(Dictionary<GpuTexture, nint> cache, ulong textureId)
    {
        lock (cache)
        {
            List<GpuTexture>? keysToRemove = null;
            foreach (var key in cache.Keys)
            {
                if (key.Id == textureId)
                {
                    keysToRemove ??= new List<GpuTexture>();
                    keysToRemove.Add(key);
                }
            }

            if (keysToRemove != null)
            {
                foreach (var key in keysToRemove)
                {
                    if (cache.TryGetValue(key, out var bindGroupPtr))
                    {
                        QueueBindGroupRelease(bindGroupPtr);
                        cache.Remove(key);
                    }
                }
            }
        }
    }

    private void QueueBindGroupRelease(nint bindGroupPtr)
    {
        if (bindGroupPtr != 0 && !_context.IsDisposed)
        {
            _context.QueueBindGroupDisposal((IntPtr)bindGroupPtr);
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
                    _effectCacheKeys.Remove(fe);
                    _wpfShaderEffectDrawParams.Remove(fe);
                }
            }
        }
    }

    private void SweepUnusedLayerTextures(Visual mainRoot, IReadOnlyList<Visual>? externalLayers, Visual? activeToolTip)
    {
        if (_allocatedLayerTextures.Count == 0)
        {
            return;
        }

        List<Visual>? stale = null;
        foreach (var entry in _allocatedLayerTextures)
        {
            var owner = entry.Key;
            var texture = entry.Value;
            if (texture.IsDisposed
                || !ReferenceEquals(owner.LayerTexture, texture)
                || !_activeLayerTextureOwners.Contains(owner)
                || !owner.IsVisible
                || !owner.CacheAsLayer
                || !IsCacheAsLayerEnabled
                || !IsAttachedToAnyActiveRoot(owner, mainRoot, externalLayers, activeToolTip))
            {
                stale ??= new List<Visual>();
                stale.Add(owner);
            }
        }

        if (stale == null)
        {
            return;
        }

        foreach (var owner in stale)
        {
            if (_allocatedLayerTextures.TryGetValue(owner, out var texture))
            {
                ReleaseLayerTexture(owner, texture);
            }
        }
    }

    private void ReleaseLayerTexture(Visual owner, GpuTexture texture)
    {
        if (_allocatedLayerTextures.TryGetValue(owner, out var allocated)
            && ReferenceEquals(allocated, texture))
        {
            _allocatedLayerTextures.Remove(owner);
        }

        if (ReferenceEquals(owner.LayerTexture, texture))
        {
            owner.LayerTexture = null;
        }

        texture.Dispose();
    }

    private void PushClipRect(Rect localClip, Matrix4x4 transform)
    {
        CommitPendingDrawCalls();
        if (!IsAxisAlignedClipTransform(transform))
        {
            PushGeometryMask(
                PrimitivePathGeometry.CreateRectangle(localClip.X, localClip.Y, localClip.Width, localClip.Height),
                transform);
            _clipScopeIsGeometryMask.Push(true);
            return;
        }

        var p0 = Vector2.Transform(new Vector2(localClip.X, localClip.Y), transform);
        var p1 = Vector2.Transform(new Vector2(localClip.X + localClip.Width, localClip.Y), transform);
        var p2 = Vector2.Transform(new Vector2(localClip.X + localClip.Width, localClip.Y + localClip.Height), transform);
        var p3 = Vector2.Transform(new Vector2(localClip.X, localClip.Y + localClip.Height), transform);

        float x1 = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        float y1 = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        float x2 = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        float y2 = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));

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
        _clipScopeIsGeometryMask.Push(false);
    }

    private void PopClipRect()
    {
        CommitPendingDrawCalls();
        if (_clipScopeIsGeometryMask.Count > 0 && _clipScopeIsGeometryMask.Pop())
        {
            PopGeometryMask();
            return;
        }

        if (_clipStack.Count > 0)
        {
            _clipStack.Pop();
            _activeClipRect = _clipStack.Count > 0 ? _clipStack.Peek() : null;
        }
    }

    private static bool IsAxisAlignedClipTransform(Matrix4x4 transform)
    {
        const float epsilon = 0.0001f;
        return MathF.Abs(transform.M12) <= epsilon && MathF.Abs(transform.M21) <= epsilon;
    }

    private void RestoreClipScopeStack(bool[] savedClipScopeIsGeometryMask)
    {
        _clipScopeIsGeometryMask.Clear();
        for (int i = savedClipScopeIsGeometryMask.Length - 1; i >= 0; i--)
        {
            _clipScopeIsGeometryMask.Push(savedClipScopeIsGeometryMask[i]);
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
            _opacityStack.Pop();
            _activeOpacity = 1.0f;
            foreach (var opacity in _opacityStack)
            {
                _activeOpacity *= opacity;
            }
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
        CompileVisualTree(node, parentTransform, offsetOverride: null);
    }

    private void CompileVisualTree(
        Visual node,
        Matrix4x4 parentTransform,
        Vector2? offsetOverride,
        bool includeLocalTransform = true,
        bool includeLocalVisualState = true)
    {
        if (!node.IsVisible
            || (includeLocalVisualState && node.Opacity <= 0.0001f)
            || _activeOpacity <= 0.0001f)
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
        var localTransform = includeLocalTransform
            ? offsetOverride.HasValue
                ? node.GetLocalTransform(offsetOverride.Value)
                : node.GetLocalTransform()
            : Matrix4x4.CreateTranslation(offsetOverride.GetValueOrDefault().X, offsetOverride.GetValueOrDefault().Y, 0f);
        var globalTransform = localTransform * parentTransform;

        var visualScope = includeLocalVisualState
            ? PushVisualCompositeScope(node, globalTransform)
            : VisualCompositeScope.None;

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
        try
        {
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
                            ClipRect = _activeClipRect,
                            MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
                            BlendMode = _activeBlendMode
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
                                    Color = ResolveDrawCallBrushColor(localCmd.Brush),
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
                        v.ScaleBoldItalicUseMvp.W = ForceTextUseMvp(v.ScaleBoldItalicUseMvp.W);
                        _textVerticesList[i] = v;
                    }
                }

                _useGpuTransformsActive = savedUseGpuTransformsActive;
                _cameraViewMatrix = savedCameraViewMatrix;
            }
        }
        finally
        {
            ctx.Clear();
            ReleaseDrawingContext();
        }

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

        PopVisualCompositeScope(visualScope);

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
                                Color = ResolveDrawCallBrushColor(localCmd.Brush),
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
                    v.ScaleBoldItalicUseMvp.W = ForceTextUseMvp(v.ScaleBoldItalicUseMvp.W);
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
        var rectShapeType = EncodeShapeType(cmd, 0f);

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

            vertexSpan[0] = new VectorVertex(f0_pos, solidColor, new Vector2(-wHalf - pad, -hHalf - pad), bIdx, shapeSize, 0f, 0f, rectShapeType);
            vertexSpan[1] = new VectorVertex(f1_pos, solidColor, new Vector2(wHalf + pad, -hHalf - pad), bIdx, shapeSize, 0f, 0f, rectShapeType);
            vertexSpan[2] = new VectorVertex(f2_pos, solidColor, new Vector2(wHalf + pad, hHalf + pad), bIdx, shapeSize, 0f, 0f, rectShapeType);
            vertexSpan[3] = new VectorVertex(f3_pos, solidColor, new Vector2(-wHalf - pad, hHalf + pad), bIdx, shapeSize, 0f, 0f, rectShapeType);

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

            vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, new Vector2(-wHalf - pad, -hHalf - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, rectShapeType);
            vertexSpan[1] = new VectorVertex(p1_pos, penSolidColor, new Vector2(wHalf + pad, -hHalf - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, rectShapeType);
            vertexSpan[2] = new VectorVertex(p2_pos, penSolidColor, new Vector2(wHalf + pad, hHalf + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, rectShapeType);
            vertexSpan[3] = new VectorVertex(p3_pos, penSolidColor, new Vector2(-wHalf - pad, hHalf + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, rectShapeType);

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
                var pathShapeType = EncodeShapeType(cmd, 4f);

                vertexSpan[0] = new VectorVertex(v0, color, uv0, bIdx, shapeSize: cp0, shapeType: pathShapeType);
                vertexSpan[1] = new VectorVertex(v1, color, uv1, bIdx, shapeSize: cp1, shapeType: pathShapeType);
                vertexSpan[2] = new VectorVertex(v2, color, uv2, bIdx, shapeSize: cp2, shapeType: pathShapeType);
                vertexSpan[3] = new VectorVertex(v3, color, uv3, bIdx, shapeSize: cp3, shapeType: pathShapeType);

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
            if (cmd.Pen.HasDashPattern)
            {
                if (!TryCreateDashedStrokePath(cmd.Path, cmd.Pen, out var dashedPath))
                {
                    throw new NotSupportedException("Dashed strokes are not supported for this path geometry.");
                }

                var dashedCmd = cmd;
                dashedCmd.Brush = null;
                dashedCmd.Path = dashedPath;
                dashedCmd.Pen = CreateUndashedPen(cmd.Pen);
                dashedCmd.Transform = default;
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

                CompilePathCommand(dashedCmd, transform);
                return;
            }

            float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
            var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
            float thickness = cmd.Pen.Thickness;

            int maxVertices = 0;
            int maxIndices = 0;
            foreach (var figure in cmd.Path.Figures)
            {
                int maxJoinTriangles = CountStrokeSegmentJoinTriangleBudget(figure);
                maxVertices += maxJoinTriangles * 3;
                maxIndices += maxJoinTriangles * 3;

                int maxCapTriangles = CountStrokeSegmentCapTriangleBudget(figure, cmd.Pen.StartLineCap, cmd.Pen.EndLineCap);
                maxVertices += maxCapTriangles * 3;
                maxIndices += maxCapTriangles * 3;

                var currentPoint = figure.StartPoint;
                foreach (var segment in figure.Segments)
                {
                    var segmentStart = currentPoint;
                    if (!segment.IsStroked)
                    {
                        if (TryGetPathSegmentEndPoint(segment, out var skippedSegmentEndPoint))
                        {
                            currentPoint = skippedSegmentEndPoint;
                        }

                        continue;
                    }

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
                    else if (segment is ArcSegment arc)
                    {
                        if (CanUseArcSdfStroke(segmentStart, arc, transform))
                        {
                            maxVertices += 4;
                            maxIndices += 6;
                        }
                        else
                        {
                            var segmentCount = ArcSegmentGeometry.CountFlattenedSegments(segmentStart, arc);
                            maxVertices += 4 * segmentCount;
                            maxIndices += 6 * segmentCount;
                        }
                    }

                    if (TryGetPathSegmentEndPoint(segment, out var segmentEndPoint))
                    {
                        currentPoint = segmentEndPoint;
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
                var firstSegmentStartDirection = default(Vector2);
                var previousSegmentEndDirection = default(Vector2);
                var firstSegmentSmoothJoin = false;
                var hasFirstSegmentStartDirection = false;
                var hasPreviousSegmentEndDirection = false;
                var hasLastCapCandidate = false;
                var lastCapCenter = default(Vector2);
                var lastCapDirection = default(Vector2);
                var isFirstSegment = true;

                foreach (var segment in figure.Segments)
                {
                    var segmentStart = currentPoint;
                    if (!segment.IsStroked)
                    {
                        if (!figure.IsClosed && hasLastCapCandidate)
                        {
                            AppendStrokeSegmentCapTriangles(
                                verticesSpan,
                                indicesSpan,
                                ref currentVertexCount,
                                ref currentIndexCount,
                                cmd.Pen,
                                penSolidColor,
                                penBrushIdx,
                                Vector2.Transform(lastCapCenter, transform),
                                TransformDirection(lastCapDirection, transform),
                                cmd.IsEdgeAliased,
                                isStart: false);
                        }

                        if (TryGetPathSegmentEndPoint(segment, out var skippedSegmentEndPoint))
                        {
                            currentPoint = skippedSegmentEndPoint;
                        }

                        firstSegmentStartDirection = default;
                        previousSegmentEndDirection = default;
                        firstSegmentSmoothJoin = false;
                        hasFirstSegmentStartDirection = false;
                        hasPreviousSegmentEndDirection = false;
                        hasLastCapCandidate = false;
                        lastCapCenter = default;
                        lastCapDirection = default;
                        isFirstSegment = true;
                        continue;
                    }

                    var hasSegmentStartDirection = TryGetPathSegmentStartDirection(segment, segmentStart, out var segmentStartDirection);
                    if (isFirstSegment && hasSegmentStartDirection)
                    {
                        firstSegmentStartDirection = segmentStartDirection;
                        firstSegmentSmoothJoin = segment.IsSmoothJoin;
                        hasFirstSegmentStartDirection = true;
                    }

                    if (!figure.IsClosed &&
                        isFirstSegment &&
                        hasSegmentStartDirection)
                    {
                        AppendStrokeSegmentCapTriangles(
                            verticesSpan,
                            indicesSpan,
                            ref currentVertexCount,
                            ref currentIndexCount,
                            cmd.Pen,
                            penSolidColor,
                            penBrushIdx,
                            Vector2.Transform(segmentStart, transform),
                            TransformDirection(segmentStartDirection, transform),
                            cmd.IsEdgeAliased,
                            isStart: true);
                    }

                    if (!isFirstSegment &&
                        hasPreviousSegmentEndDirection &&
                        hasSegmentStartDirection)
                    {
                        AppendStrokeSegmentJoinTriangles(
                            verticesSpan,
                            indicesSpan,
                            ref currentVertexCount,
                            ref currentIndexCount,
                            cmd.Pen,
                            penSolidColor,
                            penBrushIdx,
                            Vector2.Transform(segmentStart, transform),
                            TransformDirection(previousSegmentEndDirection, transform),
                            TransformDirection(segmentStartDirection, transform),
                            cmd.IsEdgeAliased,
                            segment.IsSmoothJoin);
                    }

                    if (segment is LineSegment line)
                    {
                        var lineStart = segmentStart;
                        var lineEnd = line.Point;

                        AppendStrokeLineVertices(
                            verticesSpan,
                            indicesSpan,
                            ref currentVertexCount,
                            ref currentIndexCount,
                            penSolidColor,
                            penBrushIdx,
                            thickness,
                            Vector2.Transform(lineStart, transform),
                            Vector2.Transform(lineEnd, transform),
                            cmd.IsEdgeAliased);

                        currentPoint = lineEnd;
                        hasLastCapCandidate = TryGetPathSegmentEndDirection(segment, segmentStart, out lastCapDirection);
                        hasPreviousSegmentEndDirection = hasLastCapCandidate;
                        previousSegmentEndDirection = lastCapDirection;
                        lastCapCenter = lineEnd;
                        isFirstSegment = false;
                    }
                    else if (segment is QuadraticBezierSegment quad)
                    {
                        var p0_trans = Vector2.Transform(segmentStart, transform);
                        var p1_trans = Vector2.Transform(quad.ControlPoint, transform);
                        var p2_trans = Vector2.Transform(quad.Point, transform);

                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        uint idxStart = (uint)currentVertexCount;

                        var baseVertex = new VectorVertex(p0_trans, Vector4.Zero, p1_trans, penBrushIdx, p2_trans, idxStart, thickness, EncodeShapeType(cmd, 5f));
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
                        hasLastCapCandidate = TryGetPathSegmentEndDirection(segment, segmentStart, out lastCapDirection);
                        hasPreviousSegmentEndDirection = hasLastCapCandidate;
                        previousSegmentEndDirection = lastCapDirection;
                        lastCapCenter = quad.Point;
                        isFirstSegment = false;
                    }
                    else if (segment is CubicBezierSegment cubic)
                    {
                        var p0_trans = Vector2.Transform(segmentStart, transform);
                        var p1_trans = Vector2.Transform(cubic.ControlPoint1, transform);
                        var p2_trans = Vector2.Transform(cubic.ControlPoint2, transform);
                        var p3_trans = Vector2.Transform(cubic.Point, transform);

                        int N = Math.Clamp((int)(thickness * 1.5f) + 8, 8, 24);
                        uint idxStart = (uint)currentVertexCount;

                        var baseVertex = new VectorVertex(p0_trans, new Vector4(p3_trans.X, p3_trans.Y, 0f, 0f), p1_trans, penBrushIdx, p2_trans, idxStart, thickness, EncodeShapeType(cmd, 6f));
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
                        hasLastCapCandidate = TryGetPathSegmentEndDirection(segment, segmentStart, out lastCapDirection);
                        hasPreviousSegmentEndDirection = hasLastCapCandidate;
                        previousSegmentEndDirection = lastCapDirection;
                        lastCapCenter = cubic.Point;
                        isFirstSegment = false;
                    }
                    else if (segment is ArcSegment arc)
                    {
                        if (!AppendStrokeArcVertices(
                                verticesSpan,
                                indicesSpan,
                                ref currentVertexCount,
                                ref currentIndexCount,
                                penBrushIdx,
                                thickness,
                                segmentStart,
                                arc,
                                transform,
                                cmd.IsEdgeAliased))
                        {
                            var points = ArcSegmentGeometry.FlattenArc(segmentStart, arc);
                            for (int i = 0; i < points.Length - 1; i++)
                            {
                                var arcLineStart = points[i];
                                var arcLineEnd = points[i + 1];
                                if (arcLineStart == arcLineEnd)
                                {
                                    continue;
                                }

                                AppendStrokeLineVertices(
                                    verticesSpan,
                                    indicesSpan,
                                    ref currentVertexCount,
                                    ref currentIndexCount,
                                    penSolidColor,
                                    penBrushIdx,
                                    thickness,
                                    Vector2.Transform(arcLineStart, transform),
                                    Vector2.Transform(arcLineEnd, transform),
                                    cmd.IsEdgeAliased);
                            }
                        }

                        currentPoint = arc.Point;
                        hasLastCapCandidate = TryGetPathSegmentEndDirection(segment, segmentStart, out lastCapDirection);
                        hasPreviousSegmentEndDirection = hasLastCapCandidate;
                        previousSegmentEndDirection = lastCapDirection;
                        lastCapCenter = arc.Point;
                        isFirstSegment = false;
                    }
                    else if (TryGetPathSegmentEndPoint(segment, out var segmentEndPoint))
                    {
                        currentPoint = segmentEndPoint;
                        hasPreviousSegmentEndDirection = false;
                        hasLastCapCandidate = false;
                        isFirstSegment = false;
                    }
                }

                if (!figure.IsClosed && hasLastCapCandidate)
                {
                    AppendStrokeSegmentCapTriangles(
                        verticesSpan,
                        indicesSpan,
                        ref currentVertexCount,
                        ref currentIndexCount,
                        cmd.Pen,
                        penSolidColor,
                        penBrushIdx,
                        Vector2.Transform(lastCapCenter, transform),
                        TransformDirection(lastCapDirection, transform),
                        cmd.IsEdgeAliased,
                        isStart: false);
                }
                else if (figure.IsClosed && currentPoint != figure.StartPoint)
                {
                    var closeLineStart = currentPoint;
                    var closeLineEnd = figure.StartPoint;
                    var closeLineDirection = closeLineEnd - closeLineStart;

                    if (hasPreviousSegmentEndDirection)
                    {
                        AppendStrokeSegmentJoinTriangles(
                            verticesSpan,
                            indicesSpan,
                            ref currentVertexCount,
                            ref currentIndexCount,
                            cmd.Pen,
                            penSolidColor,
                            penBrushIdx,
                            Vector2.Transform(closeLineStart, transform),
                            TransformDirection(previousSegmentEndDirection, transform),
                            TransformDirection(closeLineDirection, transform),
                            cmd.IsEdgeAliased,
                            isSmoothJoin: false);
                    }

                    AppendStrokeLineVertices(
                        verticesSpan,
                        indicesSpan,
                        ref currentVertexCount,
                        ref currentIndexCount,
                        penSolidColor,
                        penBrushIdx,
                        thickness,
                        Vector2.Transform(closeLineStart, transform),
                        Vector2.Transform(closeLineEnd, transform),
                        cmd.IsEdgeAliased);

                    if (hasFirstSegmentStartDirection)
                    {
                        AppendStrokeSegmentJoinTriangles(
                            verticesSpan,
                            indicesSpan,
                            ref currentVertexCount,
                            ref currentIndexCount,
                            cmd.Pen,
                            penSolidColor,
                            penBrushIdx,
                            Vector2.Transform(closeLineEnd, transform),
                            TransformDirection(closeLineDirection, transform),
                            TransformDirection(firstSegmentStartDirection, transform),
                            cmd.IsEdgeAliased,
                            firstSegmentSmoothJoin);
                    }
                }
                else if (figure.IsClosed &&
                         hasPreviousSegmentEndDirection &&
                         hasFirstSegmentStartDirection &&
                         currentPoint == figure.StartPoint)
                {
                    AppendStrokeSegmentJoinTriangles(
                        verticesSpan,
                        indicesSpan,
                        ref currentVertexCount,
                        ref currentIndexCount,
                        cmd.Pen,
                        penSolidColor,
                        penBrushIdx,
                        Vector2.Transform(figure.StartPoint, transform),
                        TransformDirection(previousSegmentEndDirection, transform),
                        TransformDirection(firstSegmentStartDirection, transform),
                        cmd.IsEdgeAliased,
                        firstSegmentSmoothJoin);
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

    private static bool TryCreateDashedStrokePath(PathGeometry source, Pen pen, out PathGeometry dashedPath)
    {
        dashedPath = new PathGeometry
        {
            FillRule = source.FillRule
        };

        if (source.IsCombined)
        {
            return false;
        }

        var dashArray = pen.DashArray;
        if (dashArray is not { Length: > 0 } ||
            !DashPattern.TryCreate(dashArray, pen.DashOffset, pen.Thickness, out var pattern))
        {
            return false;
        }

        foreach (var figure in source.Figures)
        {
            var patternIndex = pattern.InitialIndex;
            var distanceInPattern = pattern.InitialDistance;
            var currentPoint = figure.StartPoint;

            foreach (var segment in figure.Segments)
            {
                var segmentStart = currentPoint;
                if (!segment.IsStroked)
                {
                    if (TryGetPathSegmentEndPoint(segment, out var skippedEndPoint))
                    {
                        currentPoint = skippedEndPoint;
                    }

                    patternIndex = pattern.InitialIndex;
                    distanceInPattern = pattern.InitialDistance;
                    continue;
                }

                switch (segment)
                {
                    case LineSegment line:
                        AddDashedLineFigures(
                            dashedPath,
                            pattern,
                            segmentStart,
                            line.Point,
                            ref patternIndex,
                            ref distanceInPattern);
                        currentPoint = line.Point;
                        break;

                    case QuadraticBezierSegment quadratic:
                        if (BezierSegmentGeometry.TryCreateDashedQuadraticBezierSegments(
                                segmentStart,
                                quadratic,
                                pattern.Intervals,
                                patternIndex,
                                distanceInPattern,
                                out var quadraticSegments,
                                out patternIndex,
                                out distanceInPattern))
                        {
                            foreach (var dashSegment in quadraticSegments)
                            {
                                AddDashedSegmentFigure(dashedPath, dashSegment.Start, dashSegment.Segment);
                            }
                        }

                        currentPoint = quadratic.Point;
                        break;

                    case CubicBezierSegment cubic:
                        if (BezierSegmentGeometry.TryCreateDashedCubicBezierSegments(
                                segmentStart,
                                cubic,
                                pattern.Intervals,
                                patternIndex,
                                distanceInPattern,
                                out var cubicSegments,
                                out patternIndex,
                                out distanceInPattern))
                        {
                            foreach (var dashSegment in cubicSegments)
                            {
                                AddDashedSegmentFigure(dashedPath, dashSegment.Start, dashSegment.Segment);
                            }
                        }

                        currentPoint = cubic.Point;
                        break;

                    case ArcSegment arc:
                        if (ArcSegmentGeometry.TryCreateDashedArcSegments(
                                segmentStart,
                                arc,
                                pattern.Intervals,
                                patternIndex,
                                distanceInPattern,
                                out var arcSegments,
                                out patternIndex,
                                out distanceInPattern))
                        {
                            foreach (var dashSegment in arcSegments)
                            {
                                AddDashedSegmentFigure(dashedPath, dashSegment.Start, dashSegment.Arc);
                            }
                        }

                        currentPoint = arc.Point;
                        break;

                    default:
                        return false;
                }
            }

            if (figure.IsClosed && Vector2.DistanceSquared(currentPoint, figure.StartPoint) > StrokeEpsilon * StrokeEpsilon)
            {
                AddDashedLineFigures(
                    dashedPath,
                    pattern,
                    currentPoint,
                    figure.StartPoint,
                    ref patternIndex,
                    ref distanceInPattern);
            }
        }

        return true;
    }

    private static void AddDashedLineFigures(
        PathGeometry dashedPath,
        DashPattern pattern,
        Vector2 start,
        Vector2 end,
        ref int patternIndex,
        ref float distanceInPattern)
    {
        if (!pattern.TryCreateLineSegments(
                start,
                end,
                patternIndex,
                distanceInPattern,
                out var dashSegments,
                out patternIndex,
                out distanceInPattern))
        {
            return;
        }

        foreach (var dashSegment in dashSegments)
        {
            AddDashedSegmentFigure(dashedPath, dashSegment.Start, new LineSegment(dashSegment.End));
        }
    }

    private static void AddDashedSegmentFigure(PathGeometry dashedPath, Vector2 start, PathSegment segment)
    {
        if (TryGetPathSegmentEndPoint(segment, out var endPoint) &&
            Vector2.DistanceSquared(start, endPoint) <= StrokeEpsilon * StrokeEpsilon)
        {
            return;
        }

        segment.IsSmoothJoin = false;
        segment.IsStroked = true;
        var figure = new PathFigure(start)
        {
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(segment);
        dashedPath.Figures.Add(figure);
    }

    private static Pen CreateUndashedPen(Pen pen)
    {
        return new Pen(
            pen.Brush,
            pen.Thickness,
            pen.LineJoin,
            pen.MiterLimit,
            pen.StartLineCap,
            pen.EndLineCap,
            pen.DashCap);
    }

    private static int CountStrokeSegmentJoinTriangleBudget(PathFigure figure)
    {
        int joinCount = 0;
        var currentPoint = figure.StartPoint;
        var hasFirstSegmentStartDirection = false;
        var hasPreviousSegmentEndDirection = false;
        var isFirstSegment = true;

        foreach (var segment in figure.Segments)
        {
            var segmentStart = currentPoint;
            if (!segment.IsStroked)
            {
                if (TryGetPathSegmentEndPoint(segment, out var skippedSegmentEndPoint))
                {
                    currentPoint = skippedSegmentEndPoint;
                }

                hasFirstSegmentStartDirection = false;
                hasPreviousSegmentEndDirection = false;
                isFirstSegment = true;
                continue;
            }

            var hasSegmentStartDirection = TryGetPathSegmentStartDirection(segment, segmentStart, out var segmentStartDirection);
            if (isFirstSegment && hasSegmentStartDirection)
            {
                hasFirstSegmentStartDirection = true;
            }

            if (!isFirstSegment &&
                hasPreviousSegmentEndDirection &&
                hasSegmentStartDirection)
            {
                joinCount++;
            }

            if (TryGetPathSegmentEndDirection(segment, segmentStart, out _))
            {
                hasPreviousSegmentEndDirection = true;
            }
            else
            {
                hasPreviousSegmentEndDirection = false;
            }

            if (TryGetPathSegmentEndPoint(segment, out var segmentEndPoint))
            {
                currentPoint = segmentEndPoint;
            }

            isFirstSegment = false;
        }

        if (figure.IsClosed && hasFirstSegmentStartDirection)
        {
            if (currentPoint != figure.StartPoint)
            {
                if (TrySelectDirection(out _, figure.StartPoint - currentPoint))
                {
                    if (hasPreviousSegmentEndDirection)
                    {
                        joinCount++;
                    }

                    joinCount++;
                }
            }
            else if (hasPreviousSegmentEndDirection)
            {
                joinCount++;
            }
        }

        return joinCount * StrokeJoinGeometry.MaxTrianglesPerJoin;
    }

    private static int CountStrokeSegmentCapTriangleBudget(PathFigure figure, PenLineCap startLineCap, PenLineCap endLineCap)
    {
        if (figure.IsClosed || (startLineCap == PenLineCap.Flat && endLineCap == PenLineCap.Flat))
        {
            return 0;
        }

        var currentPoint = figure.StartPoint;
        var isFirstSegmentInSubpath = true;
        var pendingEndCap = false;
        var capCount = 0;

        foreach (var segment in figure.Segments)
        {
            if (!segment.IsStroked)
            {
                if (pendingEndCap && endLineCap != PenLineCap.Flat)
                {
                    capCount++;
                }

                pendingEndCap = false;
                isFirstSegmentInSubpath = true;
                if (TryGetPathSegmentEndPoint(segment, out var skippedSegmentEndPoint))
                {
                    currentPoint = skippedSegmentEndPoint;
                }

                continue;
            }

            if (isFirstSegmentInSubpath &&
                startLineCap != PenLineCap.Flat &&
                TryGetPathSegmentStartDirection(segment, currentPoint, out _))
            {
                capCount++;
            }

            pendingEndCap = TryGetPathSegmentEndDirection(segment, currentPoint, out _);
            if (TryGetPathSegmentEndPoint(segment, out var segmentEndPoint))
            {
                currentPoint = segmentEndPoint;
            }

            isFirstSegmentInSubpath = false;
        }

        if (pendingEndCap && endLineCap != PenLineCap.Flat)
        {
            capCount++;
        }

        return capCount * StrokeCapGeometry.MaxTrianglesPerCap;
    }

    private static void AppendStrokeLineVertices(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        Vector4 penSolidColor,
        float penBrushIdx,
        float thickness,
        Vector2 startPoint,
        Vector2 endPoint,
        bool isEdgeAliased)
    {
        uint idxStart = (uint)currentVertexCount;
        var lineShapeType = EncodeShapeType(isEdgeAliased, 3f);

        verticesSpan[currentVertexCount++] = new VectorVertex(startPoint, penSolidColor, startPoint, penBrushIdx, endPoint, 1f, thickness, lineShapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(startPoint, penSolidColor, startPoint, penBrushIdx, endPoint, -1f, thickness, lineShapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(endPoint, penSolidColor, startPoint, penBrushIdx, endPoint, 1f, thickness, lineShapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(endPoint, penSolidColor, startPoint, penBrushIdx, endPoint, -1f, thickness, lineShapeType);

        indicesSpan[currentIndexCount++] = idxStart;
        indicesSpan[currentIndexCount++] = idxStart + 1;
        indicesSpan[currentIndexCount++] = idxStart + 2;

        indicesSpan[currentIndexCount++] = idxStart + 1;
        indicesSpan[currentIndexCount++] = idxStart + 3;
        indicesSpan[currentIndexCount++] = idxStart + 2;
    }

    private static bool AppendStrokeArcVertices(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        float penBrushIdx,
        float thickness,
        Vector2 segmentStart,
        ArcSegment arc,
        Matrix4x4 transform,
        bool isEdgeAliased)
    {
        return AppendStrokeArcSdfVertices(
            verticesSpan,
            indicesSpan,
            ref currentVertexCount,
            ref currentIndexCount,
            penBrushIdx,
            thickness,
            segmentStart,
            arc,
            transform,
            isEdgeAliased);
    }

    private static bool AppendStrokeArcSdfVertices(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        float penBrushIdx,
        float thickness,
        Vector2 segmentStart,
        ArcSegment arc,
        Matrix4x4 transform,
        bool isEdgeAliased)
    {
        if (!CanUseArcSdfStroke(segmentStart, arc, transform) ||
            !ArcSegmentGeometry.TryCreateShaderParameters(
                segmentStart,
                arc,
                transform,
                out var arcParameters))
        {
            return false;
        }

        float pad = thickness * 0.5f + 2.0f;
        if (!TryGetTransformedArcBounds(segmentStart, arc, transform, pad, out Vector2 min, out Vector2 max))
        {
            Vector2 extent = new(
                MathF.Abs(arcParameters.AxisX.X) + MathF.Abs(arcParameters.AxisY.X) + pad,
                MathF.Abs(arcParameters.AxisX.Y) + MathF.Abs(arcParameters.AxisY.Y) + pad);
            min = arcParameters.Center - extent;
            max = arcParameters.Center + extent;
        }

        if (!IsFinite(min) ||
            !IsFinite(max) ||
            min.X >= max.X ||
            min.Y >= max.Y)
        {
            return false;
        }

        uint idxStart = (uint)currentVertexCount;
        var arcShapeType = EncodeShapeType(isEdgeAliased, ArcSdfShapeType);
        var shaderParameters = new Vector4(
            arcParameters.Center.X,
            arcParameters.Center.Y,
            arcParameters.Theta1,
            arcParameters.DeltaTheta);

        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(min.X, min.Y),
            shaderParameters,
            arcParameters.AxisX,
            penBrushIdx,
            arcParameters.AxisY,
            0f,
            thickness,
            arcShapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(max.X, min.Y),
            shaderParameters,
            arcParameters.AxisX,
            penBrushIdx,
            arcParameters.AxisY,
            0f,
            thickness,
            arcShapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(max.X, max.Y),
            shaderParameters,
            arcParameters.AxisX,
            penBrushIdx,
            arcParameters.AxisY,
            0f,
            thickness,
            arcShapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(min.X, max.Y),
            shaderParameters,
            arcParameters.AxisX,
            penBrushIdx,
            arcParameters.AxisY,
            0f,
            thickness,
            arcShapeType);

        indicesSpan[currentIndexCount++] = idxStart;
        indicesSpan[currentIndexCount++] = idxStart + 1;
        indicesSpan[currentIndexCount++] = idxStart + 2;

        indicesSpan[currentIndexCount++] = idxStart;
        indicesSpan[currentIndexCount++] = idxStart + 2;
        indicesSpan[currentIndexCount++] = idxStart + 3;

        return true;
    }

    private static bool CanUseArcSdfStroke(Vector2 segmentStart, ArcSegment arc, Matrix4x4 transform)
    {
        if (!ArcSegmentGeometry.TryCreateShaderParameters(segmentStart, arc, transform, out var arcParameters))
        {
            return false;
        }

        float determinant = arcParameters.AxisX.X * arcParameters.AxisY.Y -
                            arcParameters.AxisX.Y * arcParameters.AxisY.X;
        return float.IsFinite(determinant) && MathF.Abs(determinant) > 0.0001f;
    }

    private static bool TryGetTransformedArcBounds(
        Vector2 segmentStart,
        ArcSegment arc,
        Matrix4x4 transform,
        float padding,
        out Vector2 min,
        out Vector2 max)
    {
        min = default;
        max = default;
        if (!ArcSegmentGeometry.TryTransformArcSegment(segmentStart, arc, transform, out var transformedStart, out var transformedArc) ||
            !ArcSegmentGeometry.TryGetArcBounds(transformedStart, transformedArc, out min, out max))
        {
            return false;
        }

        var pad = new Vector2(padding, padding);
        min -= pad;
        max += pad;
        return true;
    }

    private static void AppendStrokeSegmentJoinTriangles(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        Pen pen,
        Vector4 penSolidColor,
        float penBrushIdx,
        Vector2 joinPoint,
        Vector2 incomingDirection,
        Vector2 outgoingDirection,
        bool isEdgeAliased,
        bool isSmoothJoin)
    {
        var triangles = StrokeJoinGeometry.CreateDirectionalJoin(
            pen.LineJoin,
            pen.Thickness,
            pen.MiterLimit,
            joinPoint,
            incomingDirection,
            outgoingDirection,
            isSmoothJoin);

        foreach (var triangle in triangles)
        {
            uint idxStart = (uint)currentVertexCount;
            var fillShapeType = EncodeShapeType(isEdgeAliased, 7f);

            verticesSpan[currentVertexCount++] = new VectorVertex(triangle.P0, penSolidColor, triangle.P0, penBrushIdx, default, 0f, 0f, fillShapeType);
            verticesSpan[currentVertexCount++] = new VectorVertex(triangle.P1, penSolidColor, triangle.P1, penBrushIdx, default, 0f, 0f, fillShapeType);
            verticesSpan[currentVertexCount++] = new VectorVertex(triangle.P2, penSolidColor, triangle.P2, penBrushIdx, default, 0f, 0f, fillShapeType);

            indicesSpan[currentIndexCount++] = idxStart;
            indicesSpan[currentIndexCount++] = idxStart + 1;
            indicesSpan[currentIndexCount++] = idxStart + 2;
        }
    }

    private static void AppendStrokeSegmentCapTriangles(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        Pen pen,
        Vector4 penSolidColor,
        float penBrushIdx,
        Vector2 center,
        Vector2 directionAlongPath,
        bool isEdgeAliased,
        bool isStart)
    {
        var triangles = StrokeCapGeometry.CreateDirectionalCap(
            isStart ? pen.StartLineCap : pen.EndLineCap,
            pen.Thickness,
            center,
            directionAlongPath,
            isStart);

        foreach (var triangle in triangles)
        {
            uint idxStart = (uint)currentVertexCount;
            var fillShapeType = EncodeShapeType(isEdgeAliased, 7f);

            verticesSpan[currentVertexCount++] = new VectorVertex(triangle.P0, penSolidColor, triangle.P0, penBrushIdx, default, 0f, 0f, fillShapeType);
            verticesSpan[currentVertexCount++] = new VectorVertex(triangle.P1, penSolidColor, triangle.P1, penBrushIdx, default, 0f, 0f, fillShapeType);
            verticesSpan[currentVertexCount++] = new VectorVertex(triangle.P2, penSolidColor, triangle.P2, penBrushIdx, default, 0f, 0f, fillShapeType);

            indicesSpan[currentIndexCount++] = idxStart;
            indicesSpan[currentIndexCount++] = idxStart + 1;
            indicesSpan[currentIndexCount++] = idxStart + 2;
        }
    }

    private static Vector2 TransformDirection(Vector2 direction, Matrix4x4 transform)
    {
        return Vector2.Transform(direction, transform) - Vector2.Transform(Vector2.Zero, transform);
    }

    private static bool TryGetPathSegmentStartDirection(PathSegment segment, Vector2 segmentStart, out Vector2 direction)
    {
        switch (segment)
        {
            case LineSegment line:
                return TrySelectDirection(out direction, line.Point - segmentStart);
            case QuadraticBezierSegment quadratic:
                return TrySelectDirection(
                    out direction,
                    quadratic.ControlPoint - segmentStart,
                    quadratic.Point - segmentStart);
            case CubicBezierSegment cubic:
                return TrySelectDirection(
                    out direction,
                    cubic.ControlPoint1 - segmentStart,
                    cubic.ControlPoint2 - segmentStart,
                    cubic.Point - segmentStart);
            case ArcSegment arc:
                return TryGetArcDirection(segmentStart, arc, isStart: true, out direction);
            default:
                direction = default;
                return false;
        }
    }

    private static bool TryGetPathSegmentEndDirection(PathSegment segment, Vector2 segmentStart, out Vector2 direction)
    {
        switch (segment)
        {
            case LineSegment line:
                return TrySelectDirection(out direction, line.Point - segmentStart);
            case QuadraticBezierSegment quadratic:
                return TrySelectDirection(
                    out direction,
                    quadratic.Point - quadratic.ControlPoint,
                    quadratic.Point - segmentStart);
            case CubicBezierSegment cubic:
                return TrySelectDirection(
                    out direction,
                    cubic.Point - cubic.ControlPoint2,
                    cubic.Point - cubic.ControlPoint1,
                    cubic.Point - segmentStart);
            case ArcSegment arc:
                return TryGetArcDirection(segmentStart, arc, isStart: false, out direction);
            default:
                direction = default;
                return false;
        }
    }

    private static bool TryGetArcDirection(Vector2 segmentStart, ArcSegment arc, bool isStart, out Vector2 direction)
    {
        direction = default;
        if (!ArcSegmentGeometry.TryGetArcCenter(
                segmentStart,
                arc.Point,
                arc.Size,
                arc.RotationAngle,
                arc.IsLargeArc,
                arc.SweepDirection,
                out _,
                out float theta1,
                out float deltaTheta,
                out float radiusX,
                out float radiusY))
        {
            return false;
        }

        float phi = arc.RotationAngle * MathF.PI / 180f;
        float cosPhi = MathF.Cos(phi);
        float sinPhi = MathF.Sin(phi);
        var axisX = new Vector2(radiusX * cosPhi, radiusX * sinPhi);
        var axisY = new Vector2(-radiusY * sinPhi, radiusY * cosPhi);
        float theta = isStart ? theta1 : theta1 + deltaTheta;
        float sweepSign = deltaTheta < 0f ? -1f : 1f;
        var tangent = (-axisX * MathF.Sin(theta) + axisY * MathF.Cos(theta)) * sweepSign;

        return TrySelectDirection(out direction, tangent);
    }

    private static bool TrySelectDirection(out Vector2 direction, params Vector2[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var length = candidate.Length();
            if (float.IsFinite(length) && length > StrokeEpsilon)
            {
                direction = candidate;
                return true;
            }
        }

        direction = default;
        return false;
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }

    private static bool TryGetPathSegmentEndPoint(PathSegment segment, out Vector2 endPoint)
    {
        switch (segment)
        {
            case LineSegment line:
                endPoint = line.Point;
                return true;
            case QuadraticBezierSegment quadratic:
                endPoint = quadratic.Point;
                return true;
            case CubicBezierSegment cubic:
                endPoint = cubic.Point;
                return true;
            case ArcSegment arc:
                endPoint = arc.Point;
                return true;
            default:
                endPoint = default;
                return false;
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
        if (cmd.Pen.HasDashPattern)
        {
            var path = new PathGeometry();
            var figure = new PathFigure(cmd.Position);
            figure.Segments.Add(new LineSegment(cmd.Position2));
            path.Figures.Add(figure);

            var pathCmd = cmd;
            pathCmd.Type = RenderCommandType.DrawPath;
            pathCmd.Path = path;
            pathCmd.Brush = null;
            pathCmd.Transform = default;
            CompilePathCommand(pathCmd, transform);
            return;
        }

        int startIndex = _vectorVerticesList.Count;
        float penBrushIdx = RegisterBrush(cmd.Pen.Brush);
        var penSolidColor = (cmd.Pen.Brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);

        var p0_pos = Vector2.Transform(cmd.Position, transform);
        var p1_pos = Vector2.Transform(cmd.Position2, transform);
        float thickness = cmd.Pen.Thickness;
        var lineShapeType = EncodeShapeType(cmd, 3f);

        uint idxStart = (uint)startIndex;

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 1f, thickness, lineShapeType);
        vertexSpan[1] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -1f, thickness, lineShapeType);
        vertexSpan[2] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 2f, thickness, lineShapeType);
        vertexSpan[3] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -2f, thickness, lineShapeType);

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

        var baseVertex = new VectorVertex(p0_trans, Vector4.Zero, p1_trans, penBrushIdx, p2_trans, idxStart, thickness, EncodeShapeType(cmd, 5f));

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

        var baseVertex = new VectorVertex(p0_trans, new Vector4(p3_trans.X, p3_trans.Y, 0f, 0f), p1_trans, penBrushIdx, p2_trans, idxStart, thickness, EncodeShapeType(cmd, 6f));

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
        var lineShapeType = EncodeShapeType(cmd, 3f);

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
            vertexSpan[vIdx] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 1f, thickness, lineShapeType);
            vertexSpan[vIdx + 1] = new VectorVertex(p0_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -1f, thickness, lineShapeType);
            vertexSpan[vIdx + 2] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, 2f, thickness, lineShapeType);
            vertexSpan[vIdx + 3] = new VectorVertex(p1_pos, penSolidColor, p0_pos, penBrushIdx, p1_pos, -2f, thickness, lineShapeType);

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
        var fillShapeType = EncodeShapeType(cmd, 7f);

        uint idxStart = (uint)startIndex;

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 3);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 3);

        vertexSpan[0] = new VectorVertex(p1, brushColor, cmd.Position, brushIdx, default, 0f, 0f, fillShapeType);
        vertexSpan[1] = new VectorVertex(p2, brushColor, cmd.Position2, brushIdx, default, 0f, 0f, fillShapeType);
        vertexSpan[2] = new VectorVertex(p3, brushColor, cmd.Position3, brushIdx, default, 0f, 0f, fillShapeType);

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
        var fillShapeType = EncodeShapeType(cmd, 7f);

        uint idxStart = (uint)startIndex;

        int originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(p1, brushColor, cmd.Position, brushIdx, default, 0f, 0f, fillShapeType);
        vertexSpan[1] = new VectorVertex(p2, brushColor, cmd.Position2, brushIdx, default, 0f, 0f, fillShapeType);
        vertexSpan[2] = new VectorVertex(p3, brushColor, cmd.Position3, brushIdx, default, 0f, 0f, fillShapeType);
        vertexSpan[3] = new VectorVertex(p4, brushColor, cmd.Position4, brushIdx, default, 0f, 0f, fillShapeType);

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
        var ellipseShapeType = EncodeShapeType(cmd, 1f);

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

            vertexSpan[0] = new VectorVertex(f0_pos, solidColor, new Vector2(-rx - pad, -ry - pad), bIdx, shapeSize, 0f, 0f, ellipseShapeType);
            vertexSpan[1] = new VectorVertex(f1_pos, solidColor, new Vector2(rx + pad, -ry - pad), bIdx, shapeSize, 0f, 0f, ellipseShapeType);
            vertexSpan[2] = new VectorVertex(f2_pos, solidColor, new Vector2(rx + pad, ry + pad), bIdx, shapeSize, 0f, 0f, ellipseShapeType);
            vertexSpan[3] = new VectorVertex(f3_pos, solidColor, new Vector2(-rx - pad, ry + pad), bIdx, shapeSize, 0f, 0f, ellipseShapeType);

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

            vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, new Vector2(-rx - pad, -ry - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, ellipseShapeType);
            vertexSpan[1] = new VectorVertex(p1_pos, penSolidColor, new Vector2(rx + pad, -ry - pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, ellipseShapeType);
            vertexSpan[2] = new VectorVertex(p2_pos, penSolidColor, new Vector2(rx + pad, ry + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, ellipseShapeType);
            vertexSpan[3] = new VectorVertex(p3_pos, penSolidColor, new Vector2(-rx - pad, ry + pad), penBrushIdx, shapeSize, 0f, cmd.Pen.Thickness, ellipseShapeType);

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
        var r = cmd.Rect;
        var radiusX = Math.Min(MathF.Abs(cmd.RadiusX), r.Width / 2f);
        var radiusY = Math.Min(MathF.Abs(cmd.RadiusY), r.Height / 2f);

        if (radiusX <= 0f || radiusY <= 0f)
        {
            CompileRectCommand(cmd, transform);
            return;
        }

        if (MathF.Abs(radiusX - radiusY) > 0.0001f)
        {
            var pathCommand = cmd;
            pathCommand.Type = RenderCommandType.DrawPath;
            pathCommand.Path = PrimitivePathGeometry.CreateRoundedRectangle(r.X, r.Y, r.Width, r.Height, radiusX, radiusY);
            pathCommand.Transform = default;
            CompilePathCommand(pathCommand, transform);
            return;
        }

        SwitchBatch(BatchType.Vector);
        int startIndex = _vectorVerticesList.Count;
        var radius = radiusX;
        float wHalf = r.Width / 2f;
        float hHalf = r.Height / 2f;
        var shapeSize = new Vector2(r.Width, r.Height);
        var roundedRectShapeType = EncodeShapeType(cmd, 2f);

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

            vertexSpan[0] = new VectorVertex(f0_pos, solidColor, new Vector2(-wHalf - pad, -hHalf - pad), bIdx, shapeSize, radius, 0f, roundedRectShapeType);
            vertexSpan[1] = new VectorVertex(f1_pos, solidColor, new Vector2(wHalf + pad, -hHalf - pad), bIdx, shapeSize, radius, 0f, roundedRectShapeType);
            vertexSpan[2] = new VectorVertex(f2_pos, solidColor, new Vector2(wHalf + pad, hHalf + pad), bIdx, shapeSize, radius, 0f, roundedRectShapeType);
            vertexSpan[3] = new VectorVertex(f3_pos, solidColor, new Vector2(-wHalf - pad, hHalf + pad), bIdx, shapeSize, radius, 0f, roundedRectShapeType);

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

            vertexSpan[0] = new VectorVertex(p0_pos, penSolidColor, new Vector2(-wHalf - pad, -hHalf - pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, roundedRectShapeType);
            vertexSpan[1] = new VectorVertex(p1_pos, penSolidColor, new Vector2(wHalf + pad, -hHalf - pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, roundedRectShapeType);
            vertexSpan[2] = new VectorVertex(p2_pos, penSolidColor, new Vector2(wHalf + pad, hHalf + pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, roundedRectShapeType);
            vertexSpan[3] = new VectorVertex(p3_pos, penSolidColor, new Vector2(-wHalf - pad, hHalf + pad), penBrushIdx, shapeSize, radius, cmd.Pen.Thickness, roundedRectShapeType);

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
        SetBrushCoordinateTransform(ref gpuBrush, Matrix4x4.Identity);
        var gradientStopStart = _activeGradientStops.Count;

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
            SetBrushCoordinateTransform(ref gpuBrush, linear.CoordinateTransform);
            gpuBrush.SpreadMethod = (uint)linear.SpreadMethod;
            gpuBrush.ColorInterpolationMode = (uint)linear.ColorInterpolationMode;
            ApplyGradientStops(ref gpuBrush, linear.Stops);
        }
        else if (brush is RadialGradientBrush radial)
        {
            gpuBrush.Type = 2;
            gpuBrush.StartPoint = radial.GradientOrigin;
            gpuBrush.Center = radial.Center;
            gpuBrush.Radius = radial.RadiusX;
            gpuBrush.RadiusY = radial.RadiusY;
            SetBrushCoordinateTransform(ref gpuBrush, radial.CoordinateTransform);
            gpuBrush.SpreadMethod = (uint)radial.SpreadMethod;
            gpuBrush.ColorInterpolationMode = (uint)radial.ColorInterpolationMode;
            ApplyGradientStops(ref gpuBrush, radial.Stops);
        }
        else if (brush is TwoPointConicalGradientBrush conical)
        {
            gpuBrush.Type = 5;
            gpuBrush.StartPoint = conical.StartCenter;
            gpuBrush.Center = conical.EndCenter;
            gpuBrush.Radius = conical.StartRadius;
            gpuBrush.RadiusY = conical.EndRadius;
            SetBrushCoordinateTransform(ref gpuBrush, conical.CoordinateTransform);
            gpuBrush.SpreadMethod = (uint)conical.SpreadMethod;
            gpuBrush.ColorInterpolationMode = (uint)conical.ColorInterpolationMode;
            ApplyGradientStops(ref gpuBrush, conical.Stops);
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
                TrimGradientStops((uint)gradientStopStart);
                return (float)i;
            }
        }

        if (_activeBrushes.Count < 8192)
        {
            _activeBrushes.Add(gpuBrush);
            return (float)(_activeBrushes.Count - 1);
        }

        TrimGradientStops((uint)gradientStopStart);
        return 0f;
    }

    private bool BrushesEqual(GpuBrush a, GpuBrush b)
    {
        if (a.Type != b.Type ||
            a.Opacity != b.Opacity ||
            a.StartPoint != b.StartPoint ||
            a.EndPoint != b.EndPoint ||
            a.Center != b.Center ||
            a.Radius != b.Radius ||
            a.RadiusY != b.RadiusY ||
            a.SpreadMethod != b.SpreadMethod ||
            a.ColorInterpolationMode != b.ColorInterpolationMode ||
            a.StopCount != b.StopCount ||
            a.Color0 != b.Color0 ||
            a.Color1 != b.Color1 ||
            a.Color2 != b.Color2 ||
            a.Color3 != b.Color3 ||
            a.Color4 != b.Color4 ||
            a.Color5 != b.Color5 ||
            a.Color6 != b.Color6 ||
            a.Color7 != b.Color7 ||
            a.Offsets != b.Offsets ||
            a.Offsets1 != b.Offsets1 ||
            a.CoordinateTransform0 != b.CoordinateTransform0 ||
            a.CoordinateTransform1 != b.CoordinateTransform1)
        {
            return false;
        }

        return !IsGradientBrushType(a.Type) || GradientStopsEqual(a, b);
    }

    private bool GradientStopsEqual(GpuBrush a, GpuBrush b)
    {
        for (var i = 0; i < a.StopCount; i++)
        {
            var left = _activeGradientStops[(int)(a.StopOffset + i)];
            var right = _activeGradientStops[(int)(b.StopOffset + i)];
            if (left.Color != right.Color || left.Offset != right.Offset)
            {
                return false;
            }
        }

        return true;
    }

    private void TrimGradientStops(uint start)
    {
        if (start < _activeGradientStops.Count)
        {
            _activeGradientStops.RemoveRange((int)start, _activeGradientStops.Count - (int)start);
        }
    }

    private static bool IsGradientBrushType(uint brushType)
    {
        return brushType == 1 || brushType == 2 || brushType == 5;
    }

    private static void SetBrushCoordinateTransform(ref GpuBrush gpuBrush, Matrix4x4 transform)
    {
        gpuBrush.CoordinateTransform0 = new Vector4(transform.M11, transform.M21, transform.M41, 0f);
        gpuBrush.CoordinateTransform1 = new Vector4(transform.M12, transform.M22, transform.M42, 0f);
    }

    private void ApplyGradientStops(ref GpuBrush gpuBrush, GradientStop[]? stops)
    {
        if (stops == null)
        {
            return;
        }

        var available = Math.Max(0, MaxGradientStops - _activeGradientStops.Count);
        var stopCount = Math.Min(stops.Length, available);
        gpuBrush.StopOffset = (uint)_activeGradientStops.Count;
        gpuBrush.StopCount = (uint)stopCount;
        for (var i = 0; i < stopCount; i++)
        {
            _activeGradientStops.Add(new GpuGradientStop
            {
                Color = stops[i].Color,
                Offset = stops[i].Offset
            });
        }

        if (stopCount > 0) gpuBrush.Color0 = stops[0].Color;
        if (stopCount > 1) gpuBrush.Color1 = stops[1].Color;
        if (stopCount > 2) gpuBrush.Color2 = stops[2].Color;
        if (stopCount > 3) gpuBrush.Color3 = stops[3].Color;
        if (stopCount > 4) gpuBrush.Color4 = stops[4].Color;
        if (stopCount > 5) gpuBrush.Color5 = stops[5].Color;
        if (stopCount > 6) gpuBrush.Color6 = stops[6].Color;
        if (stopCount > 7) gpuBrush.Color7 = stops[7].Color;

        var o0 = stopCount > 0 ? stops[0].Offset : 0f;
        var o1 = stopCount > 1 ? stops[1].Offset : 1f;
        var o2 = stopCount > 2 ? stops[2].Offset : 1f;
        var o3 = stopCount > 3 ? stops[3].Offset : 1f;
        var o4 = stopCount > 4 ? stops[4].Offset : 1f;
        var o5 = stopCount > 5 ? stops[5].Offset : 1f;
        var o6 = stopCount > 6 ? stops[6].Offset : 1f;
        var o7 = stopCount > 7 ? stops[7].Offset : 1f;
        gpuBrush.Offsets = new Vector4(o0, o1, o2, o3);
        gpuBrush.Offsets1 = new Vector4(o4, o5, o6, o7);
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

        EnsureTextVertexCapacity(layout.Glyphs.Count * (cmd.IsBold ? 2 : 1));

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
                        Brush = new SolidColorBrush(layer.Color),
                        IsEdgeAliased = cmd.TextRenderingMode == TextRenderingMode.Aliased
                    };
                    CompilePathCommand(pathCmd, activeTransform);
                }

                SwitchBatch(BatchType.Text);
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
            var (subpixelX, snappedLogicalPos) = ResolveTextPlacement(
                transPos,
                dpiScale,
                rasterFontSize,
                isRotated,
                cmd.TextHintingMode);

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

                _textVerticesList.Add(new GlyphInstance
                {
                    SnappedLogicalPos = snappedLogicalPos,
                    BasisX = basisX,
                    BasisY = basisY,
                    BearSize = new Vector4(info.BearX, info.BearY, info.Width, info.Height),
                    TexCoords = new Vector4(info.TexCoordMin.X, info.TexCoordMin.Y, info.TexCoordMax.X, info.TexCoordMax.Y),
                    Color = color,
                    ScaleBoldItalicUseMvp = new Vector4(scaleRatio, xOffset, cmd.IsItalic ? 0.22f : 0f, EncodeTextFlags(ActiveCompilationContext != null, cmd.TextRenderingMode)),
                    BrushIndex = bIdx,
                    Padding = 0f
                });
            }
        }
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

        EnsureTextVertexCapacity(cmd.GlyphIndices.Length * (cmd.IsBold ? 2 : 1));

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

        for (int i = 0; i < cmd.GlyphIndices.Length; i++)
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
                        Brush = new SolidColorBrush(layer.Color),
                        IsEdgeAliased = cmd.TextRenderingMode == TextRenderingMode.Aliased
                    };
                    CompilePathCommand(pathCmd, activeTransform);
                }

                SwitchBatch(BatchType.Text);
                continue;
            }

            float baseCursorX = position.X;
            float baseCursorY = position.Y;

            Vector2 transPos = Vector2.Transform(new Vector2(baseCursorX + cmd.Position.X, baseCursorY + cmd.Position.Y), activeTransform);
            var (subpixelX, snappedLogicalPos) = ResolveTextPlacement(
                transPos,
                dpiScale,
                rasterFontSize,
                isRotated,
                cmd.TextHintingMode);

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

                _textVerticesList.Add(new GlyphInstance
                {
                    SnappedLogicalPos = snappedLogicalPos,
                    BasisX = basisX,
                    BasisY = basisY,
                    BearSize = new Vector4(info.BearX, info.BearY, info.Width, info.Height),
                    TexCoords = new Vector4(info.TexCoordMin.X, info.TexCoordMin.Y, info.TexCoordMax.X, info.TexCoordMax.Y),
                    Color = color,
                    ScaleBoldItalicUseMvp = new Vector4(scaleRatio, xOffset, cmd.IsItalic ? 0.22f : 0f, EncodeTextFlags(ActiveCompilationContext != null, cmd.TextRenderingMode)),
                    BrushIndex = bIdx,
                    Padding = 0f
                });
            }
        }
    }

    private void EnsureTextVertexCapacity(int additionalCapacity)
    {
        if (additionalCapacity <= 0)
        {
            return;
        }

        int requiredCapacity = _textVerticesList.Count + additionalCapacity;
        if (_textVerticesList.Capacity < requiredCapacity)
        {
            _textVerticesList.Capacity = requiredCapacity;
        }
    }

    private void CompileTextureCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Texture == null) return;

        CommitPendingDrawCalls();

        var r = cmd.Rect;
        var textureOpacity = cmd.TextureSamplingMode == TextureSamplingMode.Cubic
            ? -_activeOpacity
            : _activeOpacity;
        var isPremultiplied = cmd.Texture.AlphaMode == GpuTextureAlphaMode.Premultiplied;
        var premultipliedOpacityScale = isPremultiplied ? _activeOpacity : 1f;
        var color = new Vector4(
            premultipliedOpacityScale,
            isPremultiplied ? 1f : 0f,
            premultipliedOpacityScale,
            textureOpacity);

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
            BlendMode = _activeBlendMode,
            TextureSamplingMode = cmd.TextureSamplingMode,
            TextureAlphaMode = cmd.Texture.AlphaMode
        });
    }

    internal Sampler* GetTextureSampler(TextureSamplingMode samplingMode)
    {
        return samplingMode == TextureSamplingMode.Nearest && _nearestTextureSampler != null
            ? _nearestTextureSampler
            : _atlasSampler;
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
            _gradientStopsStorageBuffer.Dispose();
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
                if (_pathAtlasBindGroup != null) _context.QueueBindGroupDisposal((IntPtr)_pathAtlasBindGroup);
                if (_pathAtlasBindGroupLayout != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_pathAtlasBindGroupLayout);
                if (_pathAtlasBindGroupOffscreen != null) _context.QueueBindGroupDisposal((IntPtr)_pathAtlasBindGroupOffscreen);
                if (_pathAtlasBindGroupLayoutOffscreen != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_pathAtlasBindGroupLayoutOffscreen);
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

            foreach (var entry in _allocatedLayerTextures)
            {
                if (ReferenceEquals(entry.Key.LayerTexture, entry.Value))
                {
                    entry.Key.LayerTexture = null;
                }

                entry.Value.Dispose();
            }
            _allocatedLayerTextures.Clear();
            _activeLayerTextureOwners.Clear();

            if (!_context.IsDisposed)
            {
                if (_atlasSampler != null) _context.QueueSamplerDisposal((IntPtr)_atlasSampler);
                if (_nearestTextureSampler != null) _context.QueueSamplerDisposal((IntPtr)_nearestTextureSampler);

                if (_vectorUniformBindGroup != null) _context.QueueBindGroupDisposal((IntPtr)_vectorUniformBindGroup);
                if (_vectorUniformBindGroupOffscreen != null) _context.QueueBindGroupDisposal((IntPtr)_vectorUniformBindGroupOffscreen);
                if (_textUniformBindGroup != null) _context.QueueBindGroupDisposal((IntPtr)_textUniformBindGroup);
                if (_textUniformBindGroupOffscreen != null) _context.QueueBindGroupDisposal((IntPtr)_textUniformBindGroupOffscreen);
                if (_textureUniformBindGroup != null) _context.QueueBindGroupDisposal((IntPtr)_textureUniformBindGroup);
                if (_textureUniformBindGroupOffscreen != null) _context.QueueBindGroupDisposal((IntPtr)_textureUniformBindGroupOffscreen);

                if (_vectorUniformBindGroupLayout != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_vectorUniformBindGroupLayout);
                if (_vectorUniformBindGroupLayoutOffscreen != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_vectorUniformBindGroupLayoutOffscreen);
                if (_textUniformBindGroupLayout != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_textUniformBindGroupLayout);
                if (_textUniformBindGroupLayoutOffscreen != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_textUniformBindGroupLayoutOffscreen);
                if (_textureUniformBindGroupLayout != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_textureUniformBindGroupLayout);
                if (_textureUniformBindGroupLayoutOffscreen != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_textureUniformBindGroupLayoutOffscreen);

                if (_vectorPipelineLayout != null) _context.QueuePipelineLayoutDisposal((IntPtr)_vectorPipelineLayout);
                if (_textPipelineLayout != null) _context.QueuePipelineLayoutDisposal((IntPtr)_textPipelineLayout);
                if (_texturePipelineLayout != null) _context.QueuePipelineLayoutDisposal((IntPtr)_texturePipelineLayout);
                if (_vectorPipelineLayoutOffscreen != null) _context.QueuePipelineLayoutDisposal((IntPtr)_vectorPipelineLayoutOffscreen);
                if (_textPipelineLayoutOffscreen != null) _context.QueuePipelineLayoutDisposal((IntPtr)_textPipelineLayoutOffscreen);
                if (_texturePipelineLayoutOffscreen != null) _context.QueuePipelineLayoutDisposal((IntPtr)_texturePipelineLayoutOffscreen);

                if (_atlasBindGroup != null) _context.QueueBindGroupDisposal((IntPtr)_atlasBindGroup);
                if (_atlasBindGroupOffscreen != null) _context.QueueBindGroupDisposal((IntPtr)_atlasBindGroupOffscreen);
                if (_atlasBindGroupLayout != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_atlasBindGroupLayout);
                if (_atlasBindGroupLayoutOffscreen != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_atlasBindGroupLayoutOffscreen);

                if (_texturePipeline != null) _context.QueueRenderPipelineDisposal((IntPtr)_texturePipeline);
                if (_textureBindGroupLayout != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_textureBindGroupLayout);
                if (_textureBindGroupLayoutOffscreen != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_textureBindGroupLayoutOffscreen);

                if (_chartLinePipeline != null) _context.QueueRenderPipelineDisposal((IntPtr)_chartLinePipeline);
                if (_chartScatterPipeline != null) _context.QueueRenderPipelineDisposal((IntPtr)_chartScatterPipeline);
                if (_chartLinePipelineOffscreen != null) _context.QueueRenderPipelineDisposal((IntPtr)_chartLinePipelineOffscreen);
                if (_chartScatterPipelineOffscreen != null) _context.QueueRenderPipelineDisposal((IntPtr)_chartScatterPipelineOffscreen);

                if (_chartLineBindGroupLayout != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_chartLineBindGroupLayout);
                if (_chartScatterBindGroupLayout != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_chartScatterBindGroupLayout);
            }

            lock (_persistentTextureBindGroups)
            {
                if (!_context.IsDisposed)
                {
                    foreach (var cachedBg in _persistentTextureBindGroups.Values)
                    {
                        if (cachedBg.BindGroupPtr != 0) _context.QueueBindGroupDisposal((IntPtr)cachedBg.BindGroupPtr);
                    }
                }
                _persistentTextureBindGroups.Clear();
            }

            GpuTexture.OnDisposedWithId -= HandleTextureDisposed;

            if (_dummyMaskTexture != null) _dummyMaskTexture.Dispose();
            if (!_context.IsDisposed)
            {
                if (_dummyMaskBindGroup != null) _context.QueueBindGroupDisposal((IntPtr)_dummyMaskBindGroup);
                if (_dummyMaskBindGroupOffscreen != null) _context.QueueBindGroupDisposal((IntPtr)_dummyMaskBindGroupOffscreen);
                if (_maskBindGroupLayout != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_maskBindGroupLayout);
                if (_maskBindGroupLayoutOffscreen != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_maskBindGroupLayoutOffscreen);

                foreach (var bg in _maskBindGroups.Values)
                {
                    _context.QueueBindGroupDisposal((IntPtr)bg);
                }
                foreach (var bg in _maskBindGroupsOffscreen.Values)
                {
                    _context.QueueBindGroupDisposal((IntPtr)bg);
                }
            }
            _maskBindGroups.Clear();
            _maskBindGroupsOffscreen.Clear();

            var pooledMaskTextures = _maskTexturePool.ToArray();
            _maskTexturePool.Clear();
            foreach (var tex in pooledMaskTextures)
            {
                tex.Dispose();
            }

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

    private unsafe bool ApplyDrawCallScissor(
        RenderPassEncoder* pass,
        CompositorDrawCall dc,
        bool useRenderTargetViewport)
    {
        uint viewportX = useRenderTargetViewport ? CurrentCanvasPixelXUInt : 0u;
        uint viewportY = useRenderTargetViewport ? CurrentCanvasPixelYUInt : 0u;
        uint targetWidth = CurrentCanvasPixelWidthUInt;
        uint targetHeight = CurrentCanvasPixelHeightUInt;

        if (dc.ClipRect.HasValue)
        {
            if (!TryComputeScissorRect(
                    dc.ClipRect.Value,
                    _currentDpiScale,
                    viewportX,
                    viewportY,
                    targetWidth,
                    targetHeight,
                    out uint sx,
                    out uint sy,
                    out uint sw,
                    out uint sh))
            {
                return false;
            }

            _context.Wgpu.RenderPassEncoderSetScissorRect(pass, sx, sy, sw, sh);
            return true;
        }

        _context.Wgpu.RenderPassEncoderSetScissorRect(pass, viewportX, viewportY, targetWidth, targetHeight);
        return true;
    }

    private static bool TryComputeScissorRect(
        Rect rect,
        float dpiScale,
        uint viewportX,
        uint viewportY,
        uint targetWidth,
        uint targetHeight,
        out uint sx,
        out uint sy,
        out uint sw,
        out uint sh)
    {
        sx = 0;
        sy = 0;
        sw = 0;
        sh = 0;

        if (dpiScale <= 0f ||
            targetWidth == 0 ||
            targetHeight == 0)
        {
            return false;
        }

        float clipLeft = rect.X * dpiScale;
        float clipTop = rect.Y * dpiScale;
        float clipRight = (rect.X + rect.Width) * dpiScale;
        float clipBottom = (rect.Y + rect.Height) * dpiScale;

        if (!float.IsFinite(clipLeft) ||
            !float.IsFinite(clipTop) ||
            !float.IsFinite(clipRight) ||
            !float.IsFinite(clipBottom) ||
            clipRight <= clipLeft ||
            clipBottom <= clipTop)
        {
            return false;
        }

        float viewportLeft = 0f;
        float viewportTop = 0f;
        float viewportRight = targetWidth;
        float viewportBottom = targetHeight;

        float clippedLeft = Math.Max(viewportLeft, clipLeft);
        float clippedTop = Math.Max(viewportTop, clipTop);
        float clippedRight = Math.Min(viewportRight, clipRight);
        float clippedBottom = Math.Min(viewportBottom, clipBottom);

        if (clippedRight <= clippedLeft ||
            clippedBottom <= clippedTop)
        {
            return false;
        }

        uint localX = (uint)Math.Floor(clippedLeft);
        uint localY = (uint)Math.Floor(clippedTop);
        uint localRight = (uint)Math.Ceiling(clippedRight);
        uint localBottom = (uint)Math.Ceiling(clippedBottom);

        if (localRight <= localX ||
            localBottom <= localY)
        {
            return false;
        }

        sx = viewportX + localX;
        sy = viewportY + localY;
        sw = Math.Min(localRight, targetWidth) - localX;
        sh = Math.Min(localBottom, targetHeight) - localY;
        return sw > 0 && sh > 0;
    }

    private unsafe void ApplyRenderPassViewport(
        RenderPassEncoder* pass,
        uint targetWidth,
        uint targetHeight,
        bool useRenderTargetViewport)
    {
        targetWidth = Math.Max(1u, targetWidth);
        targetHeight = Math.Max(1u, targetHeight);
        var viewport = useRenderTargetViewport
            ? NormalizeRenderTargetViewport(
                _explicitRenderTargetViewport ?? RenderTargetViewport.Full(targetWidth, targetHeight),
                targetWidth,
                targetHeight)
            : RenderTargetViewport.Full(targetWidth, targetHeight);
        _context.Wgpu.RenderPassEncoderSetViewport(
            pass,
            viewport.X,
            viewport.Y,
            viewport.Width,
            viewport.Height,
            0f,
            1f);
    }

    private static RenderTargetViewport NormalizeRenderTargetViewport(
        RenderTargetViewport viewport,
        uint targetWidth,
        uint targetHeight)
    {
        float targetWidthF = Math.Max(1u, targetWidth);
        float targetHeightF = Math.Max(1u, targetHeight);
        float x = ClampFinite(viewport.X, 0f, MathF.Max(0f, targetWidthF - 1f));
        float y = ClampFinite(viewport.Y, 0f, MathF.Max(0f, targetHeightF - 1f));
        float width = IsPositiveFinite(viewport.Width)
            ? viewport.Width
            : targetWidthF - x;
        float height = IsPositiveFinite(viewport.Height)
            ? viewport.Height
            : targetHeightF - y;

        width = Math.Clamp(width, 1f, MathF.Max(1f, targetWidthF - x));
        height = Math.Clamp(height, 1f, MathF.Max(1f, targetHeightF - y));
        return new RenderTargetViewport(x, y, width, height);
    }

    private static float ClampFinite(float value, float min, float max)
    {
        return float.IsFinite(value)
            ? Math.Clamp(value, min, max)
            : min;
    }

    private static bool IsPositiveFinite(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    private static uint RoundNonNegativeToUInt(float value)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            return 0u;
        }

        return (uint)Math.Round(value);
    }

    ~Compositor()
    {
        // Do not call Dispose() or native WebGPU release APIs during finalization.
    }

    // Helper methods for real-time drop shadows and Gaussian/backdrop blurs
    private void ApplyAndDrawEffect(Visual fe, Matrix4x4 parentTransform)
    {
        if (fe.Size.X <= 0f || fe.Size.Y <= 0f) return;
        var effect = fe.Effect;
        if (effect == null) return;

        float blurRadius = 0f;
        float padding = 0f;

        if (effect is BlurEffect blur)
        {
            blurRadius = blur.BlurRadius;
            padding = MathF.Ceiling(blurRadius * 2f);
        }
        else if (effect is DropShadowEffect shadow)
        {
            blurRadius = shadow.BlurRadius;
            padding = MathF.Ceiling(blurRadius * 2f);
        }
        else if (effect is WpfShaderEffect shaderEffect)
        {
            padding = MathF.Ceiling(MathF.Max(0f, shaderEffect.Padding));
        }

        float dpiScale = _currentDpiScale > 0f ? _currentDpiScale : 1f;
        float logicalWidth = MathF.Max(1f, fe.Size.X + padding * 2f);
        float logicalHeight = MathF.Max(1f, fe.Size.Y + padding * 2f);
        uint logicalRenderWidth = (uint)MathF.Ceiling(logicalWidth);
        uint logicalRenderHeight = (uint)MathF.Ceiling(logicalHeight);
        uint w = (uint)MathF.Ceiling(logicalWidth * dpiScale);
        uint h = (uint)MathF.Ceiling(logicalHeight * dpiScale);

        bool hasCached = _effectTextures.TryGetValue(fe, out var textures);
        int effectCacheKey = effect.GetRenderCacheKey();
        bool hasCachedEffectKey = _effectCacheKeys.TryGetValue(fe, out var cachedEffectKey);
        bool needsUpdate = !hasCached ||
            fe.IsDirty ||
            !hasCachedEffectKey ||
            cachedEffectKey != effectCacheKey ||
            textures.Source.Width != w ||
            textures.Source.Height != h;

        if (needsUpdate)
        {
            if (!hasCached)
            {
                var source = new GpuTexture(_context, w, h, RenderFormat, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, "Effect Source", alphaMode: GpuTextureAlphaMode.Premultiplied);
                var temp = new GpuTexture(_context, w, h, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.StorageBinding, "Effect Temp", alphaMode: GpuTextureAlphaMode.Premultiplied);
                var destination = new GpuTexture(_context, w, h, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.StorageBinding, "Effect Destination", alphaMode: GpuTextureAlphaMode.Premultiplied);

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
                RenderOffscreen(
                    fe,
                    logicalRenderWidth,
                    logicalRenderHeight,
                    textures.Source,
                    padding,
                    dpiScale,
                    includeRootTransform: false,
                    includeRootVisualState: false);
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
                    _compute.ApplyGaussianBlur(textures.Source, textures.Temp, textures.Destination, blurEffect.BlurRadius * dpiScale);
                }
            }
            else if (fe.Effect is DropShadowEffect shadowEffect)
            {
                // We pass zero offset to the compute shader because we handle offset dynamically in DrawTextureOnMain on the CPU
                _compute.ApplyDropShadow(textures.Source, textures.Temp, textures.Destination, Vector2.Zero, shadowEffect.Color, shadowEffect.BlurRadius * dpiScale);
            }

            _effectCacheKeys[fe] = effectCacheKey;
        }

        var compositeTransform = fe.GetLocalTransform() * parentTransform;
        var paddedRect = new Rect(-padding, -padding, logicalWidth, logicalHeight);
        var compositeScope = PushVisualCompositeScope(fe, compositeTransform);
        try
        {
            // Draw the cached texture onto the main swapchain.
            if (fe.Effect is BlurEffect bEff)
            {
                if (bEff.BlurRadius <= 0.01f)
                {
                    // Draw original source directly (no blur!)
                    DrawTextureOnMain(textures.Source, paddedRect, compositeTransform);
                }
                else
                {
                    // Draw the blurred result back onto the main screen (shifted back by padding)
                    DrawTextureOnMain(textures.Destination, paddedRect, compositeTransform);
                }
            }
            else if (fe.Effect is DropShadowEffect sEff)
            {
                // Draw blurred shadow first (at offset, shifted back by padding)
                var shadowRect = new Rect(
                    sEff.Offset - new Vector2(padding, padding),
                    new Vector2(logicalWidth, logicalHeight));
                DrawTextureOnMain(textures.Destination, shadowRect, compositeTransform);

                // Draw original source on top (shifted back by padding)
                DrawTextureOnMain(textures.Source, paddedRect, compositeTransform);
            }
            else if (fe.Effect is WpfShaderEffect shaderEffect)
            {
                DrawWpfShaderEffectOnMain(fe, shaderEffect, textures.Source, paddedRect, compositeTransform);
            }
        }
        finally
        {
            PopVisualCompositeScope(compositeScope);
        }

        fe.IsDirty = false;
    }

    private void ApplyAndDrawLayer(Visual node, Matrix4x4 parentTransform)
    {
        if (node.Size.X <= 0f || node.Size.Y <= 0f) return;

        // Compute high-DPI scaling factor dynamically from the compositor target context
        float dpiScale = _currentDpiScale > 0f ? _currentDpiScale : 1f;

        uint logicalRenderWidth = (uint)MathF.Max(1f, MathF.Ceiling(node.Size.X));
        uint logicalRenderHeight = (uint)MathF.Max(1f, MathF.Ceiling(node.Size.Y));
        uint w = (uint)MathF.Max(1f, MathF.Ceiling(node.Size.X * dpiScale));
        uint h = (uint)MathF.Max(1f, MathF.Ceiling(node.Size.Y * dpiScale));

        if (node.LayerTexture != null
            && (node.LayerTexture.IsDisposed || !ReferenceEquals(node.LayerTexture.Context, _context)))
        {
            ReleaseLayerTexture(node, node.LayerTexture);
        }

        _activeLayerTextureOwners.Add(node);

        bool hasCached = node.LayerTexture != null;
        bool cachedTextureSizeChanged = hasCached
            && (node.LayerTexture!.Width != w || node.LayerTexture.Height != h);
        bool needsUpdate = !hasCached || node.IsDirty || cachedTextureSizeChanged;

        if (needsUpdate)
        {
            if (node.LayerTexture == null)
            {
                node.LayerTexture = new GpuTexture(_context, w, h, RenderFormat, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, "Layer Cache Texture", alphaMode: GpuTextureAlphaMode.Premultiplied);
                _allocatedLayerTextures[node] = node.LayerTexture;
            }
            else if (node.LayerTexture.Width != w || node.LayerTexture.Height != h)
            {
                node.LayerTexture.Resize(w, h);
                _allocatedLayerTextures[node] = node.LayerTexture;
            }

            _elementsRenderingLayers.Add(node);
            try
            {
                // Render the subtree of node offscreen centered with 0 padding into node.LayerTexture
                RenderOffscreen(
                    node,
                    logicalRenderWidth,
                    logicalRenderHeight,
                    node.LayerTexture,
                    0f,
                    dpiScale,
                    includeRootTransform: false,
                    includeRootVisualState: false);
            }
            finally
            {
                _elementsRenderingLayers.Remove(node);
            }
        }

        var controlRect = new Rect(Vector2.Zero, node.Size);
        var compositeTransform = node.GetLocalTransform() * parentTransform;
        var compositeScope = PushVisualCompositeScope(node, compositeTransform);
        try
        {
            // Draw the cached layer texture onto the main swapchain.
            DrawTextureOnMain(node.LayerTexture!, controlRect, compositeTransform);
        }
        finally
        {
            PopVisualCompositeScope(compositeScope);
        }

        node.IsDirty = false;
    }

    private VisualCompositeScope PushVisualCompositeScope(Visual node, Matrix4x4 compositeTransform)
    {
        var scope = VisualCompositeScope.None;
        if (node.ClipBounds.HasValue)
        {
            PushClipRect(node.ClipBounds.Value, compositeTransform);
            scope |= VisualCompositeScope.Clip;
        }

        if (node.Opacity < 1.0f)
        {
            PushOpacityValue(node.Opacity);
            scope |= VisualCompositeScope.Opacity;
        }

        if (node.OpacityMask != null && node.OpacityMaskBounds.HasValue)
        {
            PushOpacityMaskValue(node.OpacityMask, node.OpacityMaskBounds.Value, compositeTransform);
            scope |= VisualCompositeScope.OpacityMask;
        }

        return scope;
    }

    private void PopVisualCompositeScope(VisualCompositeScope scope)
    {
        if ((scope & VisualCompositeScope.OpacityMask) != 0)
        {
            PopOpacityMaskValue();
        }

        if ((scope & VisualCompositeScope.Opacity) != 0)
        {
            PopOpacityValue();
        }

        if ((scope & VisualCompositeScope.Clip) != 0)
        {
            PopClipRect();
        }
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

    private void DrawWpfShaderEffectOnMain(
        Visual visual,
        WpfShaderEffect effect,
        GpuTexture sourceTexture,
        Rect localRect,
        Matrix4x4 parentTransform)
    {
        var pipeline = GetExtension(CompositorBuiltInExtensions.WpfShaderEffect);
        if (pipeline == null)
        {
            DrawTextureOnMain(sourceTexture, localRect, parentTransform);
            return;
        }

        CommitPendingDrawCalls();

        if (!_wpfShaderEffectDrawParams.TryGetValue(visual, out var parameters))
        {
            parameters = new WpfShaderEffectParams();
            _wpfShaderEffectDrawParams[visual] = parameters;
        }

        effect.UpdateDrawParameters(parameters, sourceTexture, localRect);

        var cmd = new RenderCommand
        {
            Type = RenderCommandType.DrawExtension,
            ExtensionId = CompositorBuiltInExtensions.WpfShaderEffect,
            DataParam = parameters
        };

        pipeline.Compile(this, null, parentTransform, ref cmd);
        var cmdTransform = cmd.Transform;
        if (cmdTransform == default || cmdTransform == new Matrix4x4())
        {
            cmdTransform = Matrix4x4.Identity;
        }

        _drawCalls.Add(new CompositorDrawCall
        {
            Type = DrawCallType.Extension,
            ExtensionId = cmd.ExtensionId,
            IntParam = cmd.IntParam,
            FloatParam = cmd.FloatParam,
            DataParam = cmd.DataParam,
            PointBufferOffset = (int)_pendingVectorStart,
            PointBufferCount = (int)((uint)_vectorIndicesList.Count - _pendingVectorStart),
            DoubleBufferOffset = cmd.DoubleBufferOffset,
            DoubleBufferCount = cmd.DoubleBufferCount,
            WeightBufferOffset = cmd.WeightBufferOffset,
            WeightBufferCount = cmd.WeightBufferCount,
            FloatBufferOffset = cmd.FloatBufferOffset,
            FloatBufferCount = cmd.FloatBufferCount,
            StaticBuffer = cmd.StaticBuffer,
            Brush = cmd.Brush,
            Pen = cmd.Pen,
            Path = cmd.Path,
            Transform = parentTransform * cmdTransform,
            LineThicknessOrRadius = cmd.RadiusX,
            Scale = cmd.Scale,
            Translate = cmd.Translate,
            Color = ResolveDrawCallBrushColor(cmd.Brush),
            ClipRect = _activeClipRect,
            MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
            BlendMode = _activeBlendMode
        });

        _pendingVectorStart = (uint)_vectorIndicesList.Count;
        _pendingTextStart = (uint)_textVerticesList.Count;
    }

    public void RenderOffscreen(
        Visual node,
        uint width,
        uint height,
        GpuTexture targetTexture,
        float padding,
        float dpiScale,
        Vector4? clearColor = null,
        bool loadExistingContents = false,
        bool includeRootTransform = true,
        bool includeRootVisualState = true)
    {
        using var currentContextScope = WgpuContext.PushCurrent(_context);

        var savedWidth = _currentWidth;
        var savedHeight = _currentHeight;
        var savedDpiScale = _currentDpiScale;
        var savedExplicitRenderTargetWidth = _explicitRenderTargetWidth;
        var savedExplicitRenderTargetHeight = _explicitRenderTargetHeight;
        var savedExplicitRenderTargetViewport = _explicitRenderTargetViewport;
        var savedExplicitDpiScale = _explicitDpiScale;
        _currentWidth = width;
        _currentHeight = height;
        _currentDpiScale = float.IsFinite(dpiScale) && dpiScale > 0f ? dpiScale : 1f;
        _explicitRenderTargetWidth = Math.Max(1u, targetTexture.Width);
        _explicitRenderTargetHeight = Math.Max(1u, targetTexture.Height);
        _explicitRenderTargetViewport = null;
        _explicitDpiScale = _currentDpiScale;

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
        var savedActiveGradientStops = _activeGradientStops.ToArray();
        var savedClipStack = _clipStack.ToArray();
        var savedClipScopeIsGeometryMask = _clipScopeIsGeometryMask.ToArray();
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

        var savedBlendModeStack = _blendModeStack.ToArray();
        var savedActiveBlendMode = _activeBlendMode;
        var savedMaskStack = _maskStack.ToArray();
        var savedMaskRenderPasses = _maskRenderPasses.ToArray();
        var savedMasksToReturnToPool = _masksToReturnToPool.ToArray();

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
        _activeGradientStops.Clear();
        _clipStack.Clear();
        _clipScopeIsGeometryMask.Clear();
        _activeClipRect = null;
        _opacityStack.Clear();
        _activeOpacity = 1.0f;
        _currentBatchType = BatchType.None;

        _blendModeStack.Clear();
        _activeBlendMode = GpuBlendMode.SrcOver;
        _maskStack.Clear();
        _maskRenderPasses.Clear();
        _masksToReturnToPool.Clear();

        _pendingVectorStart = 0;
        _pendingTextStart = 0;

        var extensionFrame = BeginExtensionFrame();
        CommandEncoder* encoder = null;
        var extensionFrameEnded = false;
        try
        {

        CompileVisualTree(
            node,
            Matrix4x4.Identity,
            new Vector2(padding, padding),
            includeRootTransform,
            includeRootVisualState);

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
        if (_activeGradientStops.Count > 0)
        {
            _gradientStopsStorageBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_activeGradientStops));
        }
        _pathAtlas.RasterizePendingPaths();

        // Render target view for offscreen GpuTexture
        var targetView = targetTexture.ViewPtr;

        // Render pass for offscreen (1x MSAA)
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Offscreen Compositor Encoder") };
        encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
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
        ApplyRenderPassViewport(pass, targetTexture.Width, targetTexture.Height, useRenderTargetViewport: false);

        DrawCallType? currentType = null;
        GpuBlendMode? currentBlendMode = null;
        GpuTexture? currentMaskTexture = null;
        var textureEntries = stackalloc BindGroupEntry[2];

        foreach (var dc in _drawCalls)
        {
            if (!ApplyDrawCallScissor(pass, dc, useRenderTargetViewport: false))
            {
                continue;
            }

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
            else if (dc.Type == DrawCallType.Texture && IsTextureBindable(dc.Texture))
            {
                var texture = dc.Texture!;
                var activePipeline = GetPipeline(
                    dc.Type,
                    dc.BlendMode,
                    isOffscreen: true,
                    textureAlphaMode: dc.TextureAlphaMode);
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

                var viewPtr = texture.ViewPtr;
                var cacheKey = new TextureCacheKey(texture.Id, texture.Generation, isOffscreen: true, dc.TextureSamplingMode);

                CachedBindGroup? cachedBg;
                lock (_persistentTextureBindGroups)
                {
                    if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out cachedBg))
                    {
                        textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = GetTextureSampler(dc.TextureSamplingMode) };
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
                DrawStaticDxfBuffer(pass, dc.StaticBuffer, isOffscreen: true, dc.MaskTexture, dc.BlendMode);
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
                        var selectedVectorPipeline = currentType != DrawCallType.Vector || currentBlendMode != dc.BlendMode;
                        if (selectedVectorPipeline)
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

                        if (selectedVectorPipeline || currentMaskTexture != dc.MaskTexture)
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
        EndExtensionFrame(extensionFrame);
        extensionFrameEnded = true;

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Offscreen Compositor Command Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);
        targetTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;
        targetTexture.MarkContentsDirty();

        foreach (var tex in _masksToReturnToPool)
        {
            _maskTexturePool.Add(tex);
        }
        _masksToReturnToPool.Clear();
        _maskRenderPasses.Clear();

        EvictUnusedBindGroups();
        }
        finally
        {
            if (!extensionFrameEnded)
            {
                EndExtensionFrame(extensionFrame);
            }

            // Restore main lists and state
            _vectorVerticesList.Clear(); _vectorVerticesList.AddRange(savedVectorVertices);
            _vectorIndicesList.Clear(); _vectorIndicesList.AddRange(savedVectorIndices);
            _textVerticesList.Clear(); _textVerticesList.AddRange(savedTextVertices);
            _textureVerticesList.Clear(); _textureVerticesList.AddRange(savedTextureVertices);
            _textureIndicesList.Clear(); _textureIndicesList.AddRange(savedTextureIndices);
            _drawCalls.Clear(); _drawCalls.AddRange(savedDrawCalls);
            _activeBrushes.Clear(); _activeBrushes.AddRange(savedActiveBrushes);
            _activeGradientStops.Clear(); _activeGradientStops.AddRange(savedActiveGradientStops);
            _clipStack.Clear();
            for (int i = savedClipStack.Length - 1; i >= 0; i--)
            {
                _clipStack.Push(savedClipStack[i]);
            }
            RestoreClipScopeStack(savedClipScopeIsGeometryMask);
            _activeClipRect = savedActiveClipRect;

            _opacityStack.Clear();
            for (int i = savedOpacityStack.Length - 1; i >= 0; i--)
            {
                _opacityStack.Push(savedOpacityStack[i]);
            }
            _activeOpacity = savedActiveOpacity;

            _blendModeStack.Clear();
            for (int i = savedBlendModeStack.Length - 1; i >= 0; i--)
            {
                _blendModeStack.Push(savedBlendModeStack[i]);
            }
            _activeBlendMode = savedActiveBlendMode;

            _maskStack.Clear();
            for (int i = savedMaskStack.Length - 1; i >= 0; i--)
            {
                _maskStack.Push(savedMaskStack[i]);
            }

            _maskRenderPasses.Clear();
            _maskRenderPasses.AddRange(savedMaskRenderPasses);

            _masksToReturnToPool.Clear();
            _masksToReturnToPool.AddRange(savedMasksToReturnToPool);

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
            _explicitRenderTargetWidth = savedExplicitRenderTargetWidth;
            _explicitRenderTargetHeight = savedExplicitRenderTargetHeight;
            _explicitRenderTargetViewport = savedExplicitRenderTargetViewport;
            _explicitDpiScale = savedExplicitDpiScale;
            _currentProjection = savedProjection;
        }
    }

    public DxfStaticBuffer CompileStaticDxf(List<RenderCommand> commands, float staticZoom = 1.0f)
    {
        // Save current lists and states
        var dxfSavedVectorVertices = _vectorVerticesList.ToArray();
        var dxfSavedVectorIndices = _vectorIndicesList.ToArray();
        var dxfSavedTextVertices = _textVerticesList.ToArray();
        var dxfSavedTextureVertices = _textureVerticesList.ToArray();
        var dxfSavedTextureIndices = _textureIndicesList.ToArray();
        var dxfSavedDrawCalls = _drawCalls.ToArray();
        var dxfSavedActiveBrushes = _activeBrushes.ToArray();
        var dxfSavedActiveGradientStops = _activeGradientStops.ToArray();
        var dxfSavedCompiledTextRecords = _compiledTextRecords.ToArray();

        var dxfSavedActiveClipRect = _activeClipRect;
        var dxfSavedClipStack = _clipStack.ToArray();
        var dxfSavedClipScopeIsGeometryMask = _clipScopeIsGeometryMask.ToArray();

        var dxfSavedOpacityStack = _opacityStack.ToArray();
        var dxfSavedActiveOpacity = _activeOpacity;

        var dxfSavedPendingVectorStart = _pendingVectorStart;
        var dxfSavedPendingTextStart = _pendingTextStart;
        var dxfSavedCurrentBatchType = _currentBatchType;

        var dxfSavedUseGpuTransformsActive = _useGpuTransformsActive;
        var dxfSavedCameraViewMatrix = _cameraViewMatrix;
        var dxfSavedHasGpuTransformsInFrame = _hasGpuTransformsInFrame;
        var dxfSavedGpuTransformsCameraView = _gpuTransformsCameraView;

        var dxfSavedBlendModeStack = _blendModeStack.ToArray();
        var dxfSavedActiveBlendMode = _activeBlendMode;
        var dxfSavedMaskStack = _maskStack.ToArray();
        var dxfSavedMaskRenderPasses = _maskRenderPasses.ToArray();
        var dxfSavedMasksToReturnToPool = _masksToReturnToPool.ToArray();

        // Clear for compilation
        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _textureVerticesList.Clear();
        _textureIndicesList.Clear();
        _drawCalls.Clear();
        _activeBrushes.Clear();
        _activeGradientStops.Clear();
        _compiledTextRecords.Clear();

        _activeClipRect = null;
        _clipStack.Clear();
        _clipScopeIsGeometryMask.Clear();

        _opacityStack.Clear();
        _activeOpacity = 1.0f;

        _pendingVectorStart = 0;
        _pendingTextStart = 0;
        _currentBatchType = BatchType.None;

        _useGpuTransformsActive = false;
        _cameraViewMatrix = Matrix4x4.Identity;
        _hasGpuTransformsInFrame = false;
        _gpuTransformsCameraView = Matrix4x4.Identity;

        _blendModeStack.Clear();
        _activeBlendMode = GpuBlendMode.SrcOver;
        _maskStack.Clear();
        _maskRenderPasses.Clear();
        _masksToReturnToPool.Clear();

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
                    case RenderCommandType.DrawGlyphRun:
                        CompileGlyphRunCommand(cmd, Matrix4x4.Identity);
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
                                    Color = ResolveDrawCallBrushColor(localCmd.Brush),
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
                v.ScaleBoldItalicUseMvp.W = ForceTextUseMvp(v.ScaleBoldItalicUseMvp.W);
                _textVerticesList[i] = v;
            }

            CommitStaticDrawCalls();

            var staticBuffer = new DxfStaticBuffer(
                _context,
                _vectorVerticesList.ToArray(),
                _vectorIndicesList.ToArray(),
                _textVerticesList.ToArray(),
                _activeBrushes.ToArray(),
                _activeGradientStops.ToArray(),
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

            // Restore dynamic lists and states
            _vectorVerticesList.Clear(); _vectorVerticesList.AddRange(dxfSavedVectorVertices);
            _vectorIndicesList.Clear(); _vectorIndicesList.AddRange(dxfSavedVectorIndices);
            _textVerticesList.Clear(); _textVerticesList.AddRange(dxfSavedTextVertices);
            _textureVerticesList.Clear(); _textureVerticesList.AddRange(dxfSavedTextureVertices);
            _textureIndicesList.Clear(); _textureIndicesList.AddRange(dxfSavedTextureIndices);
            _drawCalls.Clear(); _drawCalls.AddRange(dxfSavedDrawCalls);
            _activeBrushes.Clear(); _activeBrushes.AddRange(dxfSavedActiveBrushes);
            _activeGradientStops.Clear(); _activeGradientStops.AddRange(dxfSavedActiveGradientStops);
            _compiledTextRecords.Clear(); _compiledTextRecords.AddRange(dxfSavedCompiledTextRecords);

            _activeClipRect = dxfSavedActiveClipRect;
            _clipStack.Clear();
            for (int i = dxfSavedClipStack.Length - 1; i >= 0; i--)
            {
                _clipStack.Push(dxfSavedClipStack[i]);
            }
            RestoreClipScopeStack(dxfSavedClipScopeIsGeometryMask);

            _opacityStack.Clear();
            for (int i = dxfSavedOpacityStack.Length - 1; i >= 0; i--)
            {
                _opacityStack.Push(dxfSavedOpacityStack[i]);
            }
            _activeOpacity = dxfSavedActiveOpacity;

            _pendingVectorStart = dxfSavedPendingVectorStart;
            _pendingTextStart = dxfSavedPendingTextStart;
            _currentBatchType = dxfSavedCurrentBatchType;

            _useGpuTransformsActive = dxfSavedUseGpuTransformsActive;
            _cameraViewMatrix = dxfSavedCameraViewMatrix;
            _hasGpuTransformsInFrame = dxfSavedHasGpuTransformsInFrame;
            _gpuTransformsCameraView = dxfSavedGpuTransformsCameraView;

            _blendModeStack.Clear();
            for (int i = dxfSavedBlendModeStack.Length - 1; i >= 0; i--)
            {
                _blendModeStack.Push(dxfSavedBlendModeStack[i]);
            }
            _activeBlendMode = dxfSavedActiveBlendMode;

            _maskStack.Clear();
            for (int i = dxfSavedMaskStack.Length - 1; i >= 0; i--)
            {
                _maskStack.Push(dxfSavedMaskStack[i]);
            }

            _maskRenderPasses.Clear();
            _maskRenderPasses.AddRange(dxfSavedMaskRenderPasses);

            _masksToReturnToPool.Clear();
            _masksToReturnToPool.AddRange(dxfSavedMasksToReturnToPool);
        }
    }

    public DxfStaticBuffer CompileStaticDxf(DrawingContext context, float staticZoom = 1.0f)
    {
        // Save current lists and states
        var dxfSavedVectorVertices = _vectorVerticesList.ToArray();
        var dxfSavedVectorIndices = _vectorIndicesList.ToArray();
        var dxfSavedTextVertices = _textVerticesList.ToArray();
        var dxfSavedTextureVertices = _textureVerticesList.ToArray();
        var dxfSavedTextureIndices = _textureIndicesList.ToArray();
        var dxfSavedDrawCalls = _drawCalls.ToArray();
        var dxfSavedActiveBrushes = _activeBrushes.ToArray();
        var dxfSavedActiveGradientStops = _activeGradientStops.ToArray();
        var dxfSavedCompiledTextRecords = _compiledTextRecords.ToArray();

        var dxfSavedActiveClipRect = _activeClipRect;
        var dxfSavedClipStack = _clipStack.ToArray();
        var dxfSavedClipScopeIsGeometryMask = _clipScopeIsGeometryMask.ToArray();

        var dxfSavedOpacityStack = _opacityStack.ToArray();
        var dxfSavedActiveOpacity = _activeOpacity;

        var dxfSavedPendingVectorStart = _pendingVectorStart;
        var dxfSavedPendingTextStart = _pendingTextStart;
        var dxfSavedCurrentBatchType = _currentBatchType;

        var dxfSavedUseGpuTransformsActive = _useGpuTransformsActive;
        var dxfSavedCameraViewMatrix = _cameraViewMatrix;
        var dxfSavedHasGpuTransformsInFrame = _hasGpuTransformsInFrame;
        var dxfSavedGpuTransformsCameraView = _gpuTransformsCameraView;

        var dxfSavedBlendModeStack = _blendModeStack.ToArray();
        var dxfSavedActiveBlendMode = _activeBlendMode;
        var dxfSavedMaskStack = _maskStack.ToArray();
        var dxfSavedMaskRenderPasses = _maskRenderPasses.ToArray();
        var dxfSavedMasksToReturnToPool = _masksToReturnToPool.ToArray();

        // Clear for compilation
        _vectorVerticesList.Clear();
        _vectorIndicesList.Clear();
        _textVerticesList.Clear();
        _textureVerticesList.Clear();
        _textureIndicesList.Clear();
        _drawCalls.Clear();
        _activeBrushes.Clear();
        _activeGradientStops.Clear();
        _compiledTextRecords.Clear();

        _activeClipRect = null;
        _clipStack.Clear();
        _clipScopeIsGeometryMask.Clear();

        _opacityStack.Clear();
        _activeOpacity = 1.0f;

        _pendingVectorStart = 0;
        _pendingTextStart = 0;
        _currentBatchType = BatchType.None;

        _useGpuTransformsActive = false;
        _cameraViewMatrix = Matrix4x4.Identity;
        _hasGpuTransformsInFrame = false;
        _gpuTransformsCameraView = Matrix4x4.Identity;

        _blendModeStack.Clear();
        _activeBlendMode = GpuBlendMode.SrcOver;
        _maskStack.Clear();
        _maskRenderPasses.Clear();
        _masksToReturnToPool.Clear();

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
                    case RenderCommandType.DrawGlyphRun:
                        CompileGlyphRunCommand(cmd, Matrix4x4.Identity);
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
                                    Color = ResolveDrawCallBrushColor(localCmd.Brush),
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
                                    Color = ResolveDrawCallBrushColor(localCmd.Brush),
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
                                    Color = ResolveDrawCallBrushColor(localCmd.Brush),
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
                v.ScaleBoldItalicUseMvp.W = ForceTextUseMvp(v.ScaleBoldItalicUseMvp.W);
                _textVerticesList[i] = v;
            }

            CommitStaticDrawCalls();

            var staticBuffer = new DxfStaticBuffer(
                _context,
                _vectorVerticesList.ToArray(),
                _vectorIndicesList.ToArray(),
                _textVerticesList.ToArray(),
                _activeBrushes.ToArray(),
                _activeGradientStops.ToArray(),
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

            // Restore dynamic lists and states
            _vectorVerticesList.Clear(); _vectorVerticesList.AddRange(dxfSavedVectorVertices);
            _vectorIndicesList.Clear(); _vectorIndicesList.AddRange(dxfSavedVectorIndices);
            _textVerticesList.Clear(); _textVerticesList.AddRange(dxfSavedTextVertices);
            _textureVerticesList.Clear(); _textureVerticesList.AddRange(dxfSavedTextureVertices);
            _textureIndicesList.Clear(); _textureIndicesList.AddRange(dxfSavedTextureIndices);
            _drawCalls.Clear(); _drawCalls.AddRange(dxfSavedDrawCalls);
            _activeBrushes.Clear(); _activeBrushes.AddRange(dxfSavedActiveBrushes);
            _activeGradientStops.Clear(); _activeGradientStops.AddRange(dxfSavedActiveGradientStops);
            _compiledTextRecords.Clear(); _compiledTextRecords.AddRange(dxfSavedCompiledTextRecords);

            _activeClipRect = dxfSavedActiveClipRect;
            _clipStack.Clear();
            for (int i = dxfSavedClipStack.Length - 1; i >= 0; i--)
            {
                _clipStack.Push(dxfSavedClipStack[i]);
            }
            RestoreClipScopeStack(dxfSavedClipScopeIsGeometryMask);

            _opacityStack.Clear();
            for (int i = dxfSavedOpacityStack.Length - 1; i >= 0; i--)
            {
                _opacityStack.Push(dxfSavedOpacityStack[i]);
            }
            _activeOpacity = dxfSavedActiveOpacity;

            _pendingVectorStart = dxfSavedPendingVectorStart;
            _pendingTextStart = dxfSavedPendingTextStart;
            _currentBatchType = dxfSavedCurrentBatchType;

            _useGpuTransformsActive = dxfSavedUseGpuTransformsActive;
            _cameraViewMatrix = dxfSavedCameraViewMatrix;
            _hasGpuTransformsInFrame = dxfSavedHasGpuTransformsInFrame;
            _gpuTransformsCameraView = dxfSavedGpuTransformsCameraView;

            _blendModeStack.Clear();
            for (int i = dxfSavedBlendModeStack.Length - 1; i >= 0; i--)
            {
                _blendModeStack.Push(dxfSavedBlendModeStack[i]);
            }
            _activeBlendMode = dxfSavedActiveBlendMode;

            _maskStack.Clear();
            for (int i = dxfSavedMaskStack.Length - 1; i >= 0; i--)
            {
                _maskStack.Push(dxfSavedMaskStack[i]);
            }

            _maskRenderPasses.Clear();
            _maskRenderPasses.AddRange(dxfSavedMaskRenderPasses);

            _masksToReturnToPool.Clear();
            _masksToReturnToPool.AddRange(dxfSavedMasksToReturnToPool);
        }
    }

    public void RecompileStaticText(DxfStaticBuffer staticBuffer, float staticZoom)
    {
        var savedTextVertices = _textVerticesList.ToArray();
        var savedDrawCalls = _drawCalls.ToArray();
        var savedPendingTextStart = _pendingTextStart;
        var savedCurrentBatchType = _currentBatchType;
        var savedActiveOpacity = _activeOpacity;

        _textVerticesList.Clear();
        _drawCalls.Clear();
        _pendingTextStart = 0;
        _currentBatchType = BatchType.None;
        _activeOpacity = 1.0f;

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
                v.ScaleBoldItalicUseMvp.W = ForceTextUseMvp(v.ScaleBoldItalicUseMvp.W);
                _textVerticesList[i] = v;
            }

            staticBuffer.UpdateTextBuffer(_textVerticesList.ToArray());
        }
        finally
        {
            _atlas.EndBatch();
            ActiveCompilationContext = null;

            _textVerticesList.Clear(); _textVerticesList.AddRange(savedTextVertices);
            _drawCalls.Clear(); _drawCalls.AddRange(savedDrawCalls);
            _pendingTextStart = savedPendingTextStart;
            _currentBatchType = savedCurrentBatchType;
            _activeOpacity = savedActiveOpacity;
        }
    }

    internal unsafe void DrawStaticDxfBuffer(
        RenderPassEncoder* pass,
        object staticBufferObj,
        bool isOffscreen,
        GpuTexture? maskTexture = null,
        GpuBlendMode blendMode = GpuBlendMode.SrcOver)
    {
        if (staticBufferObj is not DxfStaticBuffer sb) return;

        sb.UpdateDefaultViewport(_currentProjection, new Vector2(_currentWidth, _currentHeight), _currentDpiScale);

        var currentType = DrawCallType.StaticDxf;
        var maskBg = GetMaskBindGroup(maskTexture, isOffscreen);

        foreach (var dc in sb.DrawCalls)
        {
            if (dc.Type == DrawCallType.Vector)
            {
                if (currentType != DrawCallType.Vector)
                {
                    var pipeline = GetPipeline(DrawCallType.Vector, blendMode, isOffscreen);
                    var uniformBg = isOffscreen ? sb.UniformBindGroupOffscreen : sb.UniformBindGroup;
                    var pathAtlasBg = isOffscreen ? _pathAtlasBindGroupOffscreen : _pathAtlasBindGroup;

                    _context.Wgpu.RenderPassEncoderSetPipeline(pass, pipeline);
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, uniformBg, 0, null);
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, pathAtlasBg, 0, null);
                    _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBg, 0, null);

                    if (sb.VertexBuffer != null && sb.IndexBuffer != null)
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
                    var pipeline = GetPipeline(DrawCallType.Text, blendMode, isOffscreen);
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
                            var vectorPipeline = GetPipeline(DrawCallType.Vector, blendMode, isOffscreen);
                            var uniformBg = isOffscreen ? sb.UniformBindGroupOffscreen : sb.UniformBindGroup;
                            var pathAtlasBg = isOffscreen ? _pathAtlasBindGroupOffscreen : _pathAtlasBindGroup;

                            _context.Wgpu.RenderPassEncoderSetPipeline(pass, vectorPipeline);
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 0, uniformBg, 0, null);
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 1, pathAtlasBg, 0, null);
                            _context.Wgpu.RenderPassEncoderSetBindGroup(pass, 2, maskBg, 0, null);

                            if (sb.VertexBuffer != null && sb.IndexBuffer != null)
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
                bool needsUpload =
                    !cachedBuffer.IsOwnedBy(_context) ||
                    cachedBuffer.PointsCount != pointsCount ||
                    cachedBuffer.Buffer == null;

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
            Color = ResolveDrawCallBrushColor(cmd.Brush),
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
                bool needsUpload =
                    !cachedBuffer.IsOwnedBy(_context) ||
                    cachedBuffer.PointsCount != pointsCount ||
                    cachedBuffer.Buffer == null;

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
            Color = ResolveDrawCallBrushColor(cmd.Brush),
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
            if (!CanReuseMaskTexture(tex))
            {
                _maskTexturePool.RemoveAt(i);
                i--;
                continue;
            }

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

        if (!CanReuseMaskTexture(maskTexture))
        {
            throw new ObjectDisposedException(nameof(GpuTexture), "Cannot bind a disposed or foreign-context mask texture.");
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

    private bool CanReuseMaskTexture(GpuTexture texture)
    {
        return !texture.IsDisposed
            && ReferenceEquals(texture.Context, _context)
            && texture.TexturePtr != null
            && texture.ViewPtr != null;
    }

    private static bool BlendModeRequiresPremultipliedSource(GpuBlendMode blendMode)
    {
        return blendMode is GpuBlendMode.DstOver or GpuBlendMode.Multiply or GpuBlendMode.Screen;
    }

    private static string GetFragmentEntryPoint(
        DrawCallType type,
        GpuBlendMode blendMode,
        GpuTextureAlphaMode textureAlphaMode,
        bool writesOpacityMask)
    {
        if (writesOpacityMask)
        {
            return "fs_mask";
        }

        if (!BlendModeRequiresPremultipliedSource(blendMode))
        {
            return "fs_main";
        }

        return type == DrawCallType.Texture && textureAlphaMode == GpuTextureAlphaMode.Premultiplied
            ? "fs_main"
            : "fs_main_premultiplied";
    }

    private static GpuTextureAlphaMode GetPipelineSourceAlphaMode(
        DrawCallType type,
        GpuBlendMode blendMode,
        GpuTextureAlphaMode textureAlphaMode)
    {
        if (BlendModeRequiresPremultipliedSource(blendMode))
        {
            return GpuTextureAlphaMode.Premultiplied;
        }

        return type == DrawCallType.Texture
            ? textureAlphaMode
            : GpuTextureAlphaMode.Straight;
    }

    private RenderPipeline* GetPipeline(
        DrawCallType type,
        GpuBlendMode blendMode,
        bool isOffscreen,
        TextureFormat? overrideFormat = null,
        GpuTextureAlphaMode textureAlphaMode = GpuTextureAlphaMode.Premultiplied)
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

                    bool writesOpacityMask = overrideFormat == TextureFormat.R8Unorm;
                    var textFragmentEntryPoint = GetFragmentEntryPoint(type, blendMode, GpuTextureAlphaMode.Straight, writesOpacityMask);
                    var textSourceAlphaMode = GetPipelineSourceAlphaMode(type, blendMode, GpuTextureAlphaMode.Straight);
                    string textFragmentKey = textFragmentEntryPoint == "fs_main" ? string.Empty : $"_{textFragmentEntryPoint}";
                    string textPipelineKey = overrideFormat.HasValue
                        ? $"{baseName}_{blendMode}_{overrideFormat.Value}{textFragmentKey}"
                        : $"{baseName}_{blendMode}{textFragmentKey}";

                    return _pipelineCache.GetOrCreateRenderPipeline(
                        textPipelineKey,
                        shaderModule,
                        "vs_main",
                        textFragmentEntryPoint,
                        overrideFormat ?? RenderFormat,
                        PrimitiveTopology.TriangleList,
                        new[] { textLayoutDesc },
                        enableBlend: true,
                        enableDepthStencil: false,
                        sampleCount: sampleCount,
                        blendMode: blendMode,
                        pipelineLayout: pipelineLayout,
                        sourceAlphaMode: textSourceAlphaMode
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

            bool writesMaskTarget = overrideFormat == TextureFormat.R8Unorm;
            var fragmentEntryPoint = GetFragmentEntryPoint(type, blendMode, textureAlphaMode, writesMaskTarget);
            var sourceAlphaMode = GetPipelineSourceAlphaMode(type, blendMode, textureAlphaMode);
            string alphaModeKey = type == DrawCallType.Texture ? $"_{textureAlphaMode}" : string.Empty;
            string fragmentKey = fragmentEntryPoint == "fs_main" ? string.Empty : $"_{fragmentEntryPoint}";
            string pipelineKey = overrideFormat.HasValue
                ? $"{baseName}_{blendMode}_{overrideFormat.Value}{alphaModeKey}{fragmentKey}"
                : $"{baseName}_{blendMode}{alphaModeKey}{fragmentKey}";

            return _pipelineCache.GetOrCreateRenderPipeline(
                pipelineKey,
                shaderModule,
                "vs_main",
                fragmentEntryPoint,
                overrideFormat ?? RenderFormat,
                PrimitiveTopology.TriangleList,
                layouts,
                enableBlend: true,
                enableDepthStencil: false,
                sampleCount: sampleCount,
                blendMode: blendMode,
                pipelineLayout: pipelineLayout,
                sourceAlphaMode: sourceAlphaMode
            );
        }
    }

    private void PushGeometryMask(PathGeometry geometry, Matrix4x4 transform)
    {
        CommitPendingDrawCalls();
        int preDrawCallCount = _drawCalls.Count;
        var savedState = ResetStateForMaskCompilation();

        try
        {
            var cmd = new RenderCommand
            {
                Type = RenderCommandType.DrawPath,
                Path = geometry,
                Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f))
            };
            CompilePathCommand(cmd, transform);
            CommitPendingDrawCalls();
        }
        finally
        {
            RestoreStateAfterMaskCompilation(savedState);
        }

        var maskDrawCalls = new List<CompositorDrawCall>();
        for (int i = preDrawCallCount; i < _drawCalls.Count; i++)
        {
            maskDrawCalls.Add(_drawCalls[i]);
        }
        _drawCalls.RemoveRange(preDrawCallCount, _drawCalls.Count - preDrawCallCount);

        var maskTex = GetMaskTexture(CurrentMaskTargetPixelWidthUInt, CurrentMaskTargetPixelHeightUInt);
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
        var savedState = ResetStateForMaskCompilation();

        try
        {
            var cmd = new RenderCommand
            {
                Type = RenderCommandType.DrawRect,
                Rect = bounds,
                Brush = brush
            };
            CompileRectCommand(cmd, transform);
            CommitPendingDrawCalls();
        }
        finally
        {
            RestoreStateAfterMaskCompilation(savedState);
        }

        var maskDrawCalls = new List<CompositorDrawCall>();
        for (int i = preDrawCallCount; i < _drawCalls.Count; i++)
        {
            maskDrawCalls.Add(_drawCalls[i]);
        }
        _drawCalls.RemoveRange(preDrawCallCount, _drawCalls.Count - preDrawCallCount);

        var maskTex = GetMaskTexture(CurrentMaskTargetPixelWidthUInt, CurrentMaskTargetPixelHeightUInt);
        var prevMask = _maskStack.Count > 0 ? _maskStack.Peek() : null;

        _maskRenderPasses.Add(new MaskRenderPassInfo
        {
            MaskTexture = maskTex,
            PreviousMaskTexture = prevMask,
            DrawCalls = maskDrawCalls
        });

        _maskStack.Push(maskTex);
    }

    private MaskCompilationState ResetStateForMaskCompilation()
    {
        var savedState = new MaskCompilationState(
            _activeOpacity,
            _opacityStack.ToArray(),
            _activeBlendMode,
            _blendModeStack.ToArray());
        _activeOpacity = 1.0f;
        _opacityStack.Clear();
        _activeBlendMode = GpuBlendMode.SrcOver;
        _blendModeStack.Clear();
        return savedState;
    }

    private void RestoreStateAfterMaskCompilation(MaskCompilationState savedState)
    {
        _activeOpacity = savedState.ActiveOpacity;
        _opacityStack.Clear();
        for (int i = savedState.OpacityStack.Length - 1; i >= 0; i--)
        {
            _opacityStack.Push(savedState.OpacityStack[i]);
        }

        _activeBlendMode = savedState.ActiveBlendMode;
        _blendModeStack.Clear();
        for (int i = savedState.BlendModeStack.Length - 1; i >= 0; i--)
        {
            _blendModeStack.Push(savedState.BlendModeStack[i]);
        }
    }

    private readonly record struct MaskCompilationState(
        float ActiveOpacity,
        float[] OpacityStack,
        GpuBlendMode ActiveBlendMode,
        GpuBlendMode[] BlendModeStack);

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
            ApplyRenderPassViewport(
                pass,
                maskPass.MaskTexture.Width,
                maskPass.MaskTexture.Height,
                useRenderTargetViewport: !isOffscreen);

            var maskBindGroup = GetMaskBindGroup(maskPass.PreviousMaskTexture, isOffscreen: true);

            DrawCallType? currentType = null;
            var textureEntries = stackalloc BindGroupEntry[2];

            foreach (var dc in maskPass.DrawCalls)
            {
                if (!ApplyDrawCallScissor(pass, dc, useRenderTargetViewport: !isOffscreen))
                {
                    continue;
                }

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
                else if (dc.Type == DrawCallType.Texture && IsTextureBindable(dc.Texture))
                {
                    var texture = dc.Texture!;
                    var activePipeline = GetPipeline(
                        dc.Type,
                        dc.BlendMode,
                        isOffscreen: true,
                        overrideFormat: TextureFormat.R8Unorm,
                        textureAlphaMode: dc.TextureAlphaMode);
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

                    var viewPtr = texture.ViewPtr;
                    var cacheKey = new TextureCacheKey(texture.Id, texture.Generation, isOffscreen: true, dc.TextureSamplingMode);

                    CachedBindGroup? cachedBg;
                    lock (_persistentTextureBindGroups)
                    {
                        if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out cachedBg))
                        {
                            textureEntries[0] = new BindGroupEntry { Binding = 0, Sampler = GetTextureSampler(dc.TextureSamplingMode) };
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
        var entries = stackalloc BindGroupLayoutEntry[3];
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
        entries[2] = new BindGroupLayoutEntry
        {
            Binding = 2,
            Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.ReadOnlyStorage,
                HasDynamicOffset = false,
                MinBindingSize = 0
            }
        };

        var desc = new BindGroupLayoutDescriptor
        {
            EntryCount = (UIntPtr)3,
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
