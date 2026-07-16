using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    [FieldOffset(0)] public uint Type;             // 0 = Solid, 1 = Linear, 2 = Radial, 5 = Two-point conical, 6 = Sweep, 7 = Perlin noise
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
    public bool SceneCacheHit;
    public string? SceneCacheMissReason;
}

public readonly record struct RenderTargetViewport(float X, float Y, float Width, float Height)
{
    public bool IsValid =>
        float.IsFinite(X) &&
        float.IsFinite(Y) &&
        float.IsFinite(Width) &&
        float.IsFinite(Height) &&
        Width > 0f &&
        Height > 0f;

    public static RenderTargetViewport Full(uint width, uint height)
    {
        return new RenderTargetViewport(
            0f,
            0f,
            Math.Max(1u, width),
            Math.Max(1u, height));
    }

    public RenderTargetViewport Clamp(uint renderTargetWidth, uint renderTargetHeight)
    {
        float targetWidth = Math.Max(1u, renderTargetWidth);
        float targetHeight = Math.Max(1u, renderTargetHeight);
        float x = ClampFinite(X, 0f, MathF.Max(0f, targetWidth - 1f));
        float y = ClampFinite(Y, 0f, MathF.Max(0f, targetHeight - 1f));
        float width = IsPositiveFinite(Width)
            ? Width
            : targetWidth - x;
        float height = IsPositiveFinite(Height)
            ? Height
            : targetHeight - y;

        width = Math.Clamp(width, 1f, MathF.Max(1f, targetWidth - x));
        height = Math.Clamp(height, 1f, MathF.Max(1f, targetHeight - y));

        return new RenderTargetViewport(x, y, width, height);
    }

    private static bool IsPositiveFinite(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    private static float ClampFinite(float value, float min, float max)
    {
        return float.IsFinite(value) ? Math.Clamp(value, min, max) : min;
    }
}

public unsafe class Compositor : IDisposable
{
    public enum VectorRenderingEngine
    {
        Atlas,
        Wavefront
    }

    private VectorRenderingEngine _vectorEngine;
    private WavefrontVectorEngine? _wavefrontEngine;
    private GpuTexture? _wavefrontColorTexture;
    private GpuBuffer? _wavefrontQuadVertexBuffer;
    private GpuBuffer? _wavefrontQuadIndexBuffer;
    private BindGroup* _wavefrontTextureBindGroup;
    private uint _wavefrontTextureBindGroupGeneration;

    public VectorRenderingEngine VectorEngine
    {
        get => _vectorEngine;
        set
        {
            if (_vectorEngine == value)
            {
                return;
            }

            _vectorEngine = value;
            _compiledSceneReusable = false;
        }
    }

    private sealed class PathAtlasCapacityExceededException : InvalidOperationException
    {
        public PathAtlasCapacityExceededException(PathAtlas atlas)
            : base(
                $"PathAtlas could not preserve valid coordinates while compiling the frame. " +
                $"The live path set does not fit in the configured {atlas.AtlasSize}x{atlas.AtlasSize} atlas " +
                $"after a reset ({atlas.CachedPathCount} cached paths).")
        {
        }
    }

    internal const int MaxGradientStops = 65536;
    private const int PerlinNoiseTableEntryCount = 512;
    private const int MaxCachedPerlinNoiseTables = 16;
    private const float StrokeEpsilon = 0.0001f;
    private const float AliasedShapeTypeOffset = 1000f;
    private const float ArcSdfShapeType = 12f;
    private const float TriangleSdfShapeType = 13f;
    private const float QuadrilateralSdfShapeType = 14f;
    private const float QuadrilateralStripSdfShapeType = 15f;
    private const float QuadrilateralStripStartSdfShapeType = 16f;
    private const float QuadrilateralStripEndSdfShapeType = 17f;
    private const float VertexMeshShapeType = 18f;
    private const float SquarePointHairlineShapeType = 19f;
    private const float RoundPointHairlineShapeType = 20f;
    private const int DirectRoundedMinimumCornerSegmentCount = 8;
    private const int DirectRoundedMaximumCornerSegmentCount = 128;
    private const float DirectRoundedMaximumDeviceChordError = 0.25f;
    private const float AffineStrokeArcMaxAngleRadians = MathF.PI / 24f;
    // Matches Skia grayscale edge weight for axis-aligned vector glyphs rasterized at 8x8 coverage.
    private const float SmallTextPathCoverageGamma = 0.72f;
    private const float LargeTextPathCoverageGamma = 0.5f;
    private const float LargeTextPathCoveragePixelThreshold = 24f;
    private const float TransformedTextPathCoverageGamma = 0.875f;
    private const int MaxCachedVectorGlyphPaths = 4096;
    // Baked local phases retain Skia-compatible coverage; device phases bound moving-text churn.
    private const float VectorGlyphSubpixelPhaseGrid = 128f;
    private const uint VectorGlyphDeviceSubpixelPhaseGrid = 4;

    private readonly record struct DirectRoundedRectangleContour(
        float Left,
        float Top,
        float Right,
        float Bottom,
        Vector4 CornerRadii)
    {
        public float Width => Right - Left;
        public float Height => Bottom - Top;
    }

    private readonly record struct VectorGlyphPathCacheKey(
        PathGeometry Outline,
        float EmScale,
        Vector2 RasterPhase,
        float ItalicSkew,
        float ScaleX,
        bool UsesSvgCoordinates);

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

    private static float EncodeTextFlags(
        bool useMvp,
        TextRenderingMode textRenderingMode,
        bool isColorGlyph = false)
    {
        var flags = textRenderingMode switch
        {
            TextRenderingMode.Aliased => useMvp ? -2f : -1f,
            TextRenderingMode.ClearType => useMvp ? 3f : 2f,
            _ => useMvp ? 1f : 0f
        };
        return isColorGlyph ? flags + 8f : flags;
    }

    private static float ForceTextUseMvp(float encodedFlags)
    {
        return EncodeTextFlags(
            useMvp: true,
            DecodeTextRenderingMode(encodedFlags),
            IsColorTextFlags(encodedFlags));
    }

    private static TextRenderingMode DecodeTextRenderingMode(float encodedFlags)
    {
        encodedFlags = DecodeBaseTextFlags(encodedFlags);
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

    private static bool IsColorTextFlags(float encodedFlags) => encodedFlags > 5.5f;

    private static float DecodeBaseTextFlags(float encodedFlags) =>
        IsColorTextFlags(encodedFlags) ? encodedFlags - 8f : encodedFlags;

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

    private static (float DpiScale, float RasterFontSize, float AtlasToLogicalScale) ResolveTextRasterization(
        float fontSize,
        Matrix4x4 transform,
        float dpiScale,
        float staticZoom)
    {
        dpiScale = float.IsFinite(dpiScale) && dpiScale > 0f ? dpiScale : 1f;
        staticZoom = float.IsFinite(staticZoom) && staticZoom > 0f ? staticZoom : 1f;

        var transformScale = TransformMetrics.GetStrokeScale(transform);
        var untransformedPhysicalFontSize = fontSize * dpiScale;
        var targetRasterFontSize = untransformedPhysicalFontSize * transformScale * staticZoom;
        var rasterFontSize = QuantizeGlyphRasterSize(targetRasterFontSize);
        var atlasToLogicalScale = untransformedPhysicalFontSize / MathF.Max(rasterFontSize, 0.0001f);
        return (dpiScale * staticZoom, rasterFontSize, atlasToLogicalScale);
    }

    private static float QuantizeGlyphRasterSize(float targetRasterFontSize)
    {
        var rasterFontSize = Math.Clamp(targetRasterFontSize, 4f, 64f);
        if (rasterFontSize <= 24f)
        {
            rasterFontSize = MathF.Round(rasterFontSize * 2f) / 2f;
        }
        else
        {
            rasterFontSize = MathF.Round(rasterFontSize / 2f) * 2f;
        }

        return rasterFontSize;
    }

    private readonly List<StaticTextRecord> _compiledTextRecords = new();
    private readonly GpuRenderCommandHitTestCacheBuilder _hitTestCacheBuilder;
    private GpuHitTestDeviceIndex? _lastHitTestDeviceIndex;
    private bool _suspendHitTestCacheWrites;

    public CompositorMetrics Metrics { get; private set; }

    public GpuHitTestIndex? LastHitTestIndex { get; private set; }
    public GpuHitTestDeviceIndex? LastHitTestDeviceIndex => _lastHitTestDeviceIndex;

    public bool TryHitTestPoint(Vector2 point, out GpuHitTestResult result)
    {
        if (_lastHitTestDeviceIndex == null)
        {
            result = default;
            return false;
        }

        return GpuHitTestEngine.TryHitTestPoint(_context, _pipelineCache, _lastHitTestDeviceIndex, point, out result);
    }

    public bool TryHitTestPointAll(
        Vector2 point,
        Span<GpuHitTestResult> results,
        out int hitCount,
        out GpuHitTestResult summary)
    {
        if (_lastHitTestDeviceIndex == null)
        {
            hitCount = 0;
            summary = default;
            return false;
        }

        return GpuHitTestEngine.TryHitTestPointAll(
            _context,
            _pipelineCache,
            _lastHitTestDeviceIndex,
            point,
            results,
            out hitCount,
            out summary);
    }

    public bool TryQueryHitTestBoundsAll(
        Vector2 min,
        Vector2 max,
        Span<GpuHitTestResult> results,
        out int hitCount,
        out GpuHitTestResult summary)
    {
        if (_lastHitTestDeviceIndex == null)
        {
            hitCount = 0;
            summary = default;
            return false;
        }

        return GpuHitTestEngine.TryQueryBoundsAll(
            _context,
            _pipelineCache,
            _lastHitTestDeviceIndex,
            min,
            max,
            results,
            out hitCount,
            out summary);
    }

    public bool TryQueryHitTestEllipseAll(
        Vector2 min,
        Vector2 max,
        Span<GpuHitTestResult> results,
        out int hitCount,
        out GpuHitTestResult summary)
    {
        if (_lastHitTestDeviceIndex == null)
        {
            hitCount = 0;
            summary = default;
            return false;
        }

        return GpuHitTestEngine.TryQueryEllipseAll(
            _context,
            _pipelineCache,
            _lastHitTestDeviceIndex,
            min,
            max,
            results,
            out hitCount,
            out summary);
    }

    private void SetLastHitTestIndex(GpuHitTestIndex index)
    {
        _lastHitTestDeviceIndex?.Dispose();
        _lastHitTestDeviceIndex = null;
        LastHitTestIndex = index;

        if (GpuHitTestDeviceIndex.TryCreate(_context, index, out GpuHitTestDeviceIndex? deviceIndex))
        {
            _lastHitTestDeviceIndex = deviceIndex;
        }
    }

    private void ClearLastHitTestIndex()
    {
        _lastHitTestDeviceIndex?.Dispose();
        _lastHitTestDeviceIndex = null;
        LastHitTestIndex = null;
    }

    private void AddHitTestCommand(RenderCommand command, Matrix4x4 transform)
    {
        if (Options.EnableGpuHitTesting && !_suspendHitTestCacheWrites)
        {
            _hitTestCacheBuilder.AddCommand(command, transform);
        }
    }

    private void AddHitTestCommand(RenderCommand command, Matrix4x4 transform, IRenderDataProvider provider)
    {
        if (Options.EnableGpuHitTesting && !_suspendHitTestCacheWrites)
        {
            _hitTestCacheBuilder.AddCommand(command, transform, provider);
        }
    }

    private void AddHitTestCommand(RenderCommand command, Matrix4x4 transform, int id)
    {
        if (Options.EnableGpuHitTesting && !_suspendHitTestCacheWrites)
        {
            _hitTestCacheBuilder.AddCommand(command, transform, id);
        }
    }

    private void PushHitTestClip(Rect clipBounds, Matrix4x4 transform)
    {
        if (Options.EnableGpuHitTesting && !_suspendHitTestCacheWrites)
        {
            _hitTestCacheBuilder.PushClip(clipBounds, transform);
        }
    }

    private void PopHitTestClip()
    {
        if (Options.EnableGpuHitTesting && !_suspendHitTestCacheWrites)
        {
            _hitTestCacheBuilder.PopClip();
        }
    }

    private void AddHitTestStateCommand(RenderCommand command, Matrix4x4 transform)
    {
        if (!IsHitTestStateCommand(command.Type))
        {
            return;
        }

        AddHitTestCommand(command, transform);
    }

    private void AddHitTestDrawCommand(RenderCommand command, Matrix4x4 transform)
    {
        if (IsHitTestStateCommand(command.Type))
        {
            return;
        }

        AddHitTestCommand(command, transform);
    }

    private void AddHitTestDrawCommand(RenderCommand command, Matrix4x4 transform, IRenderDataProvider provider)
    {
        if (IsHitTestStateCommand(command.Type))
        {
            return;
        }

        AddHitTestCommand(command, transform, provider);
    }

    private static bool IsHitTestStateCommand(RenderCommandType type)
    {
        return type is
            RenderCommandType.PushClip or
            RenderCommandType.PopClip or
            RenderCommandType.PushGeometryClip or
            RenderCommandType.PopGeometryClip or
            RenderCommandType.PushOpacity or
            RenderCommandType.PopOpacity;
    }

    private void EnsurePathHitTestCompilation(PathGeometry path)
    {
        try
        {
            _pathAtlas.TryGetCompiledHitTestPath(
                path,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private readonly List<ICompositorExtension> _registeredExtensions = new();
    private readonly Dictionary<int, ICompositorExtension> _extensionsById = new();
    private int _extensionFrameDepth;

    public void RegisterExtension(int id, ICompositorExtension extension)
    {
        lock (_registeredExtensions)
        {
            _registeredExtensions.Add(extension);
            _extensionsById[id] = extension;
            _compiledSceneReusable = false;
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
                var extensionCount = _registeredExtensions.Count;
                for (var extensionIndex = 0; extensionIndex < extensionCount; extensionIndex++)
                {
                    var ext = _registeredExtensions[extensionIndex];
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
                    var extensionCount = _registeredExtensions.Count;
                    for (var extensionIndex = 0; extensionIndex < extensionCount; extensionIndex++)
                    {
                        var ext = _registeredExtensions[extensionIndex];
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
    public Func<bool>? HasDynamicDiagnostics { get; set; }
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
    private GpuTexture? _advancedBlendScratchTexture;
    private GpuTexture? _advancedBlendSourceTexture;
    private BindGroupLayout* _advancedBlendBindGroupLayout;
    private PipelineLayout* _advancedBlendPipelineLayout;

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
    private Sampler* _mipmapTextureSampler;
    private readonly Dictionary<byte, nint> _anisotropicTextureSamplers = new();
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
    private SmallValueStack<GpuTexture> _maskStack;
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

    private SmallValueStack<GpuBlendMode> _blendModeStack;
    private GpuBlendMode _activeBlendMode = GpuBlendMode.SrcOver;

    private readonly List<MaskRenderPassInfo> _maskRenderPasses = new();
    private readonly List<GpuTexture> _masksToReturnToPool = new();
    private SmallValueStack<List<CompositorDrawCall>> _drawCallListPool;
    private const int MaxPooledDrawCallLists = 64;
    private const int MaxPooledDrawCallListCapacity = 4096;

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
        public byte TextureMaxAnisotropy;
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
        public readonly byte MaxAnisotropy;

        public TextureCacheKey(
            ulong textureId,
            uint generation,
            bool isOffscreen,
            TextureSamplingMode samplingMode,
            byte maxAnisotropy)
        {
            TextureId = textureId;
            Generation = generation;
            IsOffscreen = isOffscreen;
            SamplingMode = samplingMode;
            MaxAnisotropy = samplingMode == TextureSamplingMode.LinearMipmap && maxAnisotropy > 1
                ? (byte)Math.Clamp((int)maxAnisotropy, 2, 16)
                : (byte)1;
        }

        public bool Equals(TextureCacheKey other) =>
            TextureId == other.TextureId &&
            Generation == other.Generation &&
            IsOffscreen == other.IsOffscreen &&
            SamplingMode == other.SamplingMode &&
            MaxAnisotropy == other.MaxAnisotropy;
        public override bool Equals(object? obj) => obj is TextureCacheKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(TextureId, Generation, IsOffscreen, SamplingMode, MaxAnisotropy);
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
    private readonly List<CompiledVisualVersion> _compiledExternalLayers = new();
    private readonly List<CompiledLayerVersion> _compiledLayerOwners = new();
    private readonly Dictionary<TextureCacheKey, CachedBindGroup> _persistentTextureBindGroups = new();
    private readonly List<GpuBrush> _activeBrushes = new();
    private readonly List<GpuGradientStop> _activeGradientStops = new();
    private readonly Dictionary<int, GpuGradientStop[]> _perlinNoiseTableCache = new();
    private readonly GpuBuffer _brushesStorageBuffer;
    private readonly GpuBuffer _gradientStopsStorageBuffer;
    private ulong _frameNumber = 0;
    private bool _compiledSceneReusable;
    private string _compiledSceneCacheStateReason = "No compiled scene";
    private string? _currentSceneCacheMissReason;
    private Visual? _compiledSceneRoot;
    private long _compiledSceneRootVersion;
    private uint _compiledSceneWidth;
    private uint _compiledSceneHeight;
    private uint? _compiledSceneRenderTargetWidth;
    private uint? _compiledSceneRenderTargetHeight;
    private RenderTargetViewport? _compiledSceneRenderTargetViewport;
    private float _compiledSceneDpiScale;
    private Visual? _compiledSceneToolTip;
    private long _compiledSceneToolTipVersion;
    private ulong _compiledSceneGlyphAtlasGeneration;
    private ulong _compiledScenePathAtlasGeneration;
    private bool _compiledSceneHasGpuTransforms;
    private Matrix4x4 _compiledSceneGpuTransformsCameraView;
    private bool _compiledSceneContainsDrawingVisual;
    private readonly object _offscreenRenderLock = new();
    private int _offscreenRenderDepth;
    private float _totalTime = 0f;
    private readonly Dictionary<(string Text, TtfFont Font, float Size, float MaxWidth, TextAlignment Align), TextLayout> _layoutCache = new();
    private readonly Dictionary<VectorGlyphPathCacheKey, PathGeometry> _vectorGlyphPathCache = new();
    private enum BatchType
    {
        None,
        Vector,
        Text
    }

    private readonly record struct CompiledVisualVersion(Visual Visual, long ChangeVersion);
    private readonly record struct CompiledLayerVersion(Visual Visual, GpuTexture Texture);

    [Flags]
    private enum VisualCompositeScope
    {
        None = 0,
        Clip = 1,
        OuterClip = 2,
        Opacity = 4,
        OpacityMask = 8,
        GeometryClip = 16
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

    private SmallValueStack<Rect> _clipStack;
    private SmallValueStack<bool> _clipScopeIsGeometryMask;
    private Rect? _activeClipRect;

    private SmallValueStack<float> _opacityStack;
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
            var drawCallCount = _drawCalls.Count;
            for (var drawCallIndex = 0; drawCallIndex < drawCallCount; drawCallIndex++)
            {
                var dc = _drawCalls[drawCallIndex];
                if (dc.Type == DrawCallType.Texture) count++;
            }
            return count;
        }
    }

    public GlyphAtlas Atlas => _atlas;
    public PathAtlas PathAtlas => _pathAtlas;
    public TextureFormat RenderFormat { get; private set; }
    public CompositorOptions Options { get; }

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
        : this(context, renderFormat, CompositorOptions.Default)
    {
    }

    public Compositor(WgpuContext context, TextureFormat? renderFormat, CompositorOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _context = context;
        Options = options;
        RenderFormat = renderFormat ?? _context.SwapChainFormat;
        _pipelineCache = new RenderPipelineCache(_context);
        _compute = new ComputeAccelerator(_context);

        // 1. Initialize GPU atlases.
        _atlas = new GlyphAtlas(_context, options.GlyphAtlasSize);
        _pathAtlas = new PathAtlas(_context, options.PathAtlasSize);
        _hitTestCacheBuilder = new GpuRenderCommandHitTestCacheBuilder(_pathAtlas);

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
        uint initialVertexCount = options.InitialVertexCount;
        uint initialIndexCount = options.InitialIndexCount;
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
        RegisterExtension(CompositorBuiltInExtensions.BackdropMaterial, new BackdropMaterialExtensionPipeline());

        InitializePipelinesAndBindGroups();
        GpuTexture.OnDisposedWithId += HandleTextureDisposed;
    }

    public void ApplyGaussianBlur(
        GpuTexture source,
        GpuTexture temporary,
        GpuTexture destination,
        float sigmaX,
        float sigmaY)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyGaussianBlur(source, temporary, destination, sigmaX, sigmaY);
        }
    }

    public void ApplyGaussianBlur(
        GpuTexture source,
        GpuTexture temporary,
        GpuTexture destination,
        float sigma) =>
        ApplyGaussianBlur(source, temporary, destination, sigma, sigma);

    public void ApplyDropShadow(
        GpuTexture source,
        GpuTexture temporary,
        GpuTexture destination,
        Vector2 offset,
        Vector4 color,
        float blurRadius)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyDropShadow(source, temporary, destination, offset, color, blurRadius);
        }
    }

    public void ApplyMorphology(
        GpuTexture source,
        GpuTexture temporary,
        GpuTexture destination,
        float radiusX,
        float radiusY,
        bool dilate)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyMorphology(source, temporary, destination, radiusX, radiusY, dilate);
        }
    }

    public void ApplyImageBlend(
        GpuTexture background,
        GpuTexture foreground,
        GpuTexture destination,
        GpuBlendMode blendMode,
        bool linearRgb = true)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyImageBlend(
                background,
                foreground,
                destination,
                blendMode,
                linearRgb);
        }
    }

    public void ApplyImageLighting(
        GpuTexture source,
        GpuTexture destination,
        Vector3 lightPosition,
        uint lightType,
        Vector3 lightTarget,
        float spotExponent,
        Vector4 lightColor,
        float surfaceScale,
        float lightingConstant,
        float shininess,
        float cutoffAngle,
        bool specular)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyImageLighting(
                source,
                destination,
                lightPosition,
                lightType,
                lightTarget,
                spotExponent,
                lightColor,
                surfaceScale,
                lightingConstant,
                shininess,
                cutoffAngle,
                specular);
        }
    }

    public void ApplyMatrixConvolution(
        GpuTexture source,
        GpuTexture destination,
        int kernelWidth,
        int kernelHeight,
        ReadOnlySpan<float> kernel,
        float gain,
        float bias,
        int kernelOffsetX,
        int kernelOffsetY,
        uint tileMode,
        bool convolveAlpha,
        int tileOriginX,
        int tileOriginY,
        int tileWidth,
        int tileHeight)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyMatrixConvolution(
                source,
                destination,
                kernelWidth,
                kernelHeight,
                kernel,
                gain,
                bias,
                kernelOffsetX,
                kernelOffsetY,
                tileMode,
                convolveAlpha,
                tileOriginX,
                tileOriginY,
                tileWidth,
                tileHeight);
        }
    }

    public void ApplyDisplacementMap(
        GpuTexture source,
        GpuTexture displacement,
        GpuTexture destination,
        float scale,
        uint xChannel,
        uint yChannel) =>
        ApplyDisplacementMap(
            source,
            displacement,
            destination,
            new Vector4(scale, 0f, 0f, scale),
            xChannel,
            yChannel);

    public void ApplyDisplacementMap(
        GpuTexture source,
        GpuTexture displacement,
        GpuTexture destination,
        Vector4 transform,
        uint xChannel,
        uint yChannel)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyDisplacementMap(
                source,
                displacement,
                destination,
                transform,
                xChannel,
                yChannel);
        }
    }

    public void ApplyMagnifier(
        GpuTexture source,
        GpuTexture destination,
        Vector4 lensBounds,
        Vector4 outputBounds,
        Vector4 zoomTransform,
        Vector2 inverseInset,
        uint samplingMode,
        Vector2 cubic)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyMagnifier(
                source,
                destination,
                lensBounds,
                outputBounds,
                zoomTransform,
                inverseInset,
                samplingMode,
                cubic);
        }
    }

    public void ApplyArithmeticComposite(
        GpuTexture background,
        GpuTexture foreground,
        GpuTexture destination,
        float k1,
        float k2,
        float k3,
        float k4,
        bool enforcePremultipliedColor)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyArithmeticComposite(
                background,
                foreground,
                destination,
                k1,
                k2,
                k3,
                k4,
                enforcePremultipliedColor);
        }
    }

    public void ApplyColorTable(
        GpuTexture source,
        GpuTexture destination,
        ReadOnlySpan<byte> alpha,
        ReadOnlySpan<byte> red,
        ReadOnlySpan<byte> green,
        ReadOnlySpan<byte> blue)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyColorTable(source, destination, alpha, red, green, blue);
        }
    }

    public void ApplyNonlinearColorFilter(
        GpuTexture source,
        GpuTexture destination,
        ReadOnlySpan<float> matrix,
        bool hsla,
        bool grayscale,
        uint invertStyle,
        float contrast)
    {
        lock (_context.RenderLock)
        {
            _compute.ApplyNonlinearColorFilter(
                source,
                destination,
                matrix,
                hsla,
                grayscale,
                invertStyle,
                contrast);
        }
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
        _atlasSampler = _context.Api.DeviceCreateSampler(_context.Device, &samplerDesc);

        var nearestSamplerDesc = samplerDesc;
        nearestSamplerDesc.MagFilter = FilterMode.Nearest;
        nearestSamplerDesc.MinFilter = FilterMode.Nearest;
        nearestSamplerDesc.MipmapFilter = MipmapFilterMode.Nearest;
        _nearestTextureSampler = _context.Api.DeviceCreateSampler(_context.Device, &nearestSamplerDesc);

        var mipmapSamplerDesc = samplerDesc;
        mipmapSamplerDesc.LodMaxClamp = 32f;
        _mipmapTextureSampler = _context.Api.DeviceCreateSampler(_context.Device, &mipmapSamplerDesc);

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
        _vectorPipelineLayout = _context.Api.DeviceCreatePipelineLayout(_context.Device, &vectorPipelineLayoutDesc);

        var vectorLayoutsOffscreen = stackalloc BindGroupLayout*[3];
        vectorLayoutsOffscreen[0] = _vectorUniformBindGroupLayoutOffscreen;
        vectorLayoutsOffscreen[1] = _pathAtlasBindGroupLayoutOffscreen;
        vectorLayoutsOffscreen[2] = _maskBindGroupLayoutOffscreen;
        var vectorPipelineLayoutDescOffscreen = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = vectorLayoutsOffscreen
        };
        _vectorPipelineLayoutOffscreen = _context.Api.DeviceCreatePipelineLayout(_context.Device, &vectorPipelineLayoutDescOffscreen);

        var textLayouts = stackalloc BindGroupLayout*[3];
        textLayouts[0] = _textUniformBindGroupLayout;
        textLayouts[1] = _atlasBindGroupLayout;
        textLayouts[2] = _maskBindGroupLayout;
        var textPipelineLayoutDesc = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = textLayouts
        };
        _textPipelineLayout = _context.Api.DeviceCreatePipelineLayout(_context.Device, &textPipelineLayoutDesc);

        var textLayoutsOffscreen = stackalloc BindGroupLayout*[3];
        textLayoutsOffscreen[0] = _textUniformBindGroupLayoutOffscreen;
        textLayoutsOffscreen[1] = _atlasBindGroupLayoutOffscreen;
        textLayoutsOffscreen[2] = _maskBindGroupLayoutOffscreen;
        var textPipelineLayoutDescOffscreen = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = textLayoutsOffscreen
        };
        _textPipelineLayoutOffscreen = _context.Api.DeviceCreatePipelineLayout(_context.Device, &textPipelineLayoutDescOffscreen);

        var textureLayouts = stackalloc BindGroupLayout*[3];
        textureLayouts[0] = _textureUniformBindGroupLayout;
        textureLayouts[1] = _textureBindGroupLayout;
        textureLayouts[2] = _maskBindGroupLayout;
        var texturePipelineLayoutDesc = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = textureLayouts
        };
        _texturePipelineLayout = _context.Api.DeviceCreatePipelineLayout(_context.Device, &texturePipelineLayoutDesc);

        var textureLayoutsOffscreen = stackalloc BindGroupLayout*[3];
        textureLayoutsOffscreen[0] = _textureUniformBindGroupLayoutOffscreen;
        textureLayoutsOffscreen[1] = _textureBindGroupLayoutOffscreen;
        textureLayoutsOffscreen[2] = _maskBindGroupLayoutOffscreen;
        var texturePipelineLayoutDescOffscreen = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 3,
            BindGroupLayouts = textureLayoutsOffscreen
        };
        _texturePipelineLayoutOffscreen = _context.Api.DeviceCreatePipelineLayout(_context.Device, &texturePipelineLayoutDescOffscreen);

        Span<VertexAttribute> vectorAttrs = stackalloc VertexAttribute[8];
        vectorAttrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // Position
        vectorAttrs[1] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }; // Color
        vectorAttrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }; // TexCoord
        vectorAttrs[3] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 32, ShaderLocation = 3 }; // BrushIndex
        vectorAttrs[4] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 36, ShaderLocation = 4 }; // ShapeSize
        vectorAttrs[5] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 44, ShaderLocation = 5 }; // CornerRadius
        vectorAttrs[6] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 48, ShaderLocation = 6 }; // StrokeThickness
        vectorAttrs[7] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 52, ShaderLocation = 7 }; // ShapeType

        fixed (VertexAttribute* attribsPtr = vectorAttrs)
        {
            Span<VertexBufferLayout> vectorVertexLayouts = stackalloc VertexBufferLayout[1];
            vectorVertexLayouts[0] = new VertexBufferLayout
            {
                ArrayStride = (uint)Unsafe.SizeOf<VectorVertex>(),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 8,
                Attributes = attribsPtr
            };

            // Compile primary graphics pipelines with 4x MSAA
            _vectorPipeline = _pipelineCache.GetOrCreateRenderPipeline(
                "Vector",
                vecShaderModule,
                vectorVertexLayouts,
                "vs_main",
                "fs_main",
                RenderFormat,
                PrimitiveTopology.TriangleList,
                enableBlend: true,
                sampleCount: Options.PrimarySampleCount,
                pipelineLayout: _vectorPipelineLayout
            );

            Span<VertexAttribute> textAttrs = stackalloc VertexAttribute[8];
            textAttrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // SnappedLogicalPos
            textAttrs[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 8, ShaderLocation = 1 }; // BasisX
            textAttrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 16, ShaderLocation = 2 }; // BasisY
            textAttrs[3] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 24, ShaderLocation = 3 }; // BearSize
            textAttrs[4] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 40, ShaderLocation = 4 }; // TexCoords
            textAttrs[5] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 56, ShaderLocation = 5 }; // Color
            textAttrs[6] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 72, ShaderLocation = 6 }; // ScaleBoldItalicUseMvp
            textAttrs[7] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 88, ShaderLocation = 7 }; // BrushIndex

            fixed (VertexAttribute* textAttribsPtr = textAttrs)
            {
                Span<VertexBufferLayout> textVertexLayouts = stackalloc VertexBufferLayout[1];
                textVertexLayouts[0] = new VertexBufferLayout
                {
                    ArrayStride = (uint)Unsafe.SizeOf<GlyphInstance>(),
                    StepMode = VertexStepMode.Instance,
                    AttributeCount = 8,
                    Attributes = textAttribsPtr
                };

                _textPipeline = _pipelineCache.GetOrCreateRenderPipeline(
                    "Text",
                    textShaderModule,
                    textVertexLayouts,
                    "vs_main",
                    "fs_main",
                    RenderFormat,
                    PrimitiveTopology.TriangleList,
                    enableBlend: true,
                    sampleCount: Options.PrimarySampleCount,
                    pipelineLayout: _textPipelineLayout
                );

                _textPipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                    "Text_Offscreen",
                    textShaderModule,
                    textVertexLayouts,
                    "vs_main",
                    "fs_main",
                    RenderFormat,
                    PrimitiveTopology.TriangleList,
                    enableBlend: true,
                    sampleCount: 1,
                    pipelineLayout: _textPipelineLayoutOffscreen
                );
            }

            _texturePipeline = _pipelineCache.GetOrCreateRenderPipeline(
                "Texture",
                texShaderModule,
                vectorVertexLayouts,
                "vs_main",
                "fs_main",
                RenderFormat,
                PrimitiveTopology.TriangleList,
                enableBlend: true,
                sampleCount: Options.PrimarySampleCount,
                pipelineLayout: _texturePipelineLayout,
                sourceAlphaMode: GpuTextureAlphaMode.Premultiplied
            );

            _vectorPipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                "Vector_Offscreen",
                vecShaderModule,
                vectorVertexLayouts,
                "vs_main",
                "fs_main",
                RenderFormat,
                PrimitiveTopology.TriangleList,
                enableBlend: true,
                sampleCount: 1,
                pipelineLayout: _vectorPipelineLayoutOffscreen
            );

            _texturePipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                "Texture_Offscreen",
                texShaderModule,
                vectorVertexLayouts,
                "vs_main",
                "fs_main",
                RenderFormat,
                PrimitiveTopology.TriangleList,
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
                sampleCount: Options.PrimarySampleCount
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

            Span<VertexAttribute> scatterAttrs = stackalloc VertexAttribute[2];
            scatterAttrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // center
            scatterAttrs[1] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 8, ShaderLocation = 1 }; // radiusPx
            fixed (VertexAttribute* scatterAttribsPtr = scatterAttrs)
            {
                Span<VertexBufferLayout> scatterVertexLayouts = stackalloc VertexBufferLayout[1];
                scatterVertexLayouts[0] = new VertexBufferLayout
                {
                    ArrayStride = (uint)Unsafe.SizeOf<Vector3>(),
                    StepMode = VertexStepMode.Instance,
                    AttributeCount = 2,
                    Attributes = scatterAttribsPtr
                };
                _chartScatterPipeline = _pipelineCache.GetOrCreateRenderPipeline(
                    "ChartScatter",
                    chartScatterShaderModule,
                    scatterVertexLayouts,
                    "vs_main",
                    "fs_main",
                    RenderFormat,
                    PrimitiveTopology.TriangleList,
                    enableBlend: true,
                    sampleCount: Options.PrimarySampleCount
                );

                _chartScatterPipelineOffscreen = _pipelineCache.GetOrCreateRenderPipeline(
                    "ChartScatter_Offscreen",
                    chartScatterShaderModule,
                    scatterVertexLayouts,
                    "vs_main",
                    "fs_main",
                    RenderFormat,
                    PrimitiveTopology.TriangleList,
                    enableBlend: true,
                    sampleCount: 1
                );
            }
        }

        _chartLineBindGroupLayout = _context.Api.RenderPipelineGetBindGroupLayout(_chartLinePipeline, 0);
        _chartScatterBindGroupLayout = _context.Api.RenderPipelineGetBindGroupLayout(_chartScatterPipeline, 0);

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
        _vectorUniformBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &uDescVector);

        var uDescVectorOffscreen = new BindGroupDescriptor
        {
            Layout = _vectorUniformBindGroupLayoutOffscreen,
            EntryCount = 3,
            Entries = vectorEntries
        };
        _vectorUniformBindGroupOffscreen = _context.Api.DeviceCreateBindGroup(_context.Device, &uDescVectorOffscreen);

        var uDescText = new BindGroupDescriptor
        {
            Layout = _textUniformBindGroupLayout,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textUniformBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &uDescText);

        var uDescTextOffscreen = new BindGroupDescriptor
        {
            Layout = _textUniformBindGroupLayoutOffscreen,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textUniformBindGroupOffscreen = _context.Api.DeviceCreateBindGroup(_context.Device, &uDescTextOffscreen);

        var uDescTexture = new BindGroupDescriptor
        {
            Layout = _textureUniformBindGroupLayout,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textureUniformBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &uDescTexture);

        var uDescTextureOffscreen = new BindGroupDescriptor
        {
            Layout = _textureUniformBindGroupLayoutOffscreen,
            EntryCount = 1,
            Entries = &uBufferEntry
        };
        _textureUniformBindGroupOffscreen = _context.Api.DeviceCreateBindGroup(_context.Device, &uDescTextureOffscreen);

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
        _atlasBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &atlasDesc);

        var atlasDescOffscreen = new BindGroupDescriptor
        {
            Layout = _atlasBindGroupLayoutOffscreen,
            EntryCount = 2,
            Entries = atlasEntries
        };
        _atlasBindGroupOffscreen = _context.Api.DeviceCreateBindGroup(_context.Device, &atlasDescOffscreen);

        var pathViewEntry = new BindGroupEntry
        {
            Binding = 1,
            TextureView = _pathAtlas.AtlasTexture.ViewPtr
        };
        var pathAtlasEntries = stackalloc BindGroupEntry[2];
        pathAtlasEntries[0] = new BindGroupEntry { Binding = 0, Sampler = _atlasSampler };
        pathAtlasEntries[1] = pathViewEntry;

        var pathAtlasDesc = new BindGroupDescriptor
        {
            Layout = _pathAtlasBindGroupLayout,
            EntryCount = 2,
            Entries = pathAtlasEntries
        };
        _pathAtlasBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &pathAtlasDesc);

        var pathAtlasDescOffscreen = new BindGroupDescriptor
        {
            Layout = _pathAtlasBindGroupLayoutOffscreen,
            EntryCount = 2,
            Entries = pathAtlasEntries
        };
        _pathAtlasBindGroupOffscreen = _context.Api.DeviceCreateBindGroup(_context.Device, &pathAtlasDescOffscreen);

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
        _dummyMaskBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDescMask);

        var bgDescMaskOffscreen = new BindGroupDescriptor
        {
            Layout = _maskBindGroupLayoutOffscreen,
            EntryCount = 2,
            Entries = maskEntries
        };
        _dummyMaskBindGroupOffscreen = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDescMaskOffscreen);
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
        CompositorHostFrame hostFrame,
        TextureView* targetView)
    {
        if (!hostFrame.IsValid)
        {
            return;
        }

        RenderScene(
            root,
            hostFrame.LogicalPixelWidth,
            hostFrame.LogicalPixelHeight,
            hostFrame.RenderTargetWidth,
            hostFrame.RenderTargetHeight,
            hostFrame.RenderTargetViewport,
            hostFrame.DpiScale,
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
        var retriedAfterPathAtlasReset = false;
        while (true)
        {
            try
            {
                RenderSceneCore(root, width, height, targetView);
                return;
            }
            catch (PathAtlasCapacityExceededException)
            {
                if (retriedAfterPathAtlasReset)
                {
                    throw;
                }

                retriedAfterPathAtlasReset = true;
                _pathAtlas.ResetForRenderRetry();
                _compiledSceneReusable = false;
                ReturnPendingMaskTexturesToPool();
                ProGpuSceneDiagnostics.WriteLine(
                    "[Compositor] Retrying frame compilation after recoverable PathAtlas capacity exhaustion.");
            }
        }
    }

    private void RenderSceneCore(Visual root, uint width, uint height, TextureView* targetView)
    {
        if (_isDisposed) return;

        using var currentContextScope = WgpuContext.PushCurrent(_context);

        _context.CleanupPendingResources();

        var wavefrontEnabled = VectorEngine == VectorRenderingEngine.Wavefront;
        if (wavefrontEnabled)
        {
            (_wavefrontEngine ??= new WavefrontVectorEngine(_context)).BeginFrame();
        }

        _currentWidth = width;
        _currentHeight = height;
        _currentDpiScale = _explicitDpiScale ?? 1.0f;
        if (!_explicitDpiScale.HasValue &&
            _context.Window != null &&
            width == (uint)_context.Window.Size.X &&
            height == (uint)_context.Window.Size.Y)
        {
            _currentDpiScale = (float)DisplayScaleResolver.ResolveWindowDisplayScale(_context.Window);
        }

        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var compileSw = System.Diagnostics.Stopwatch.StartNew();
        _pathAtlas.CleanupFrame(
            _explicitRenderTargetWidth ?? width,
            _explicitRenderTargetHeight ?? height);
        var pathAtlasGenerationAtCompilationStart = _pathAtlas.Generation;
        _activeLayerTextureOwners.Clear();

        // Invoke pre-render actions (e.g. measure/arrange popups in UI framework)
        PreRender?.Invoke(width, height);

        IReadOnlyList<Visual>? externalLayers = GetExternalLayers?.Invoke();
        Visual? activeToolTip = GetTooltip?.Invoke();
        bool hasDynamicDiagnostics = RenderDiagnostics != null && (HasDynamicDiagnostics?.Invoke() ?? true);

        // 1. Calculate orthographic projection matrix for modern 2D rendering
        // Maps X in [0, width] to [-1, 1], and Y in [0, height] to [1, -1]
        var projection = new Matrix4x4(
            2.0f / width, 0f, 0f, 0f,
            0f, -2.0f / height, 0f, 0f,
            0f, 0f, 1f, 0f,
            -1.0f, 1.0f, 0f, 1.0f
        );
        _currentProjection = projection;

        bool reuseCompiledScene = !wavefrontEnabled && CanReuseCompiledScene(
            root,
            width,
            height,
            externalLayers,
            activeToolTip,
            hasDynamicDiagnostics);

        _useGpuTransformsActive = false;
        _cameraViewMatrix = Matrix4x4.Identity;
        if (reuseCompiledScene)
        {
            _hasGpuTransformsInFrame = _compiledSceneHasGpuTransforms;
            _gpuTransformsCameraView = _compiledSceneGpuTransformsCameraView;
            MarkCompiledSceneResourcesUsed();
        }
        else
        {
            _hasGpuTransformsInFrame = false;
            _gpuTransformsCameraView = Matrix4x4.Identity;
            _compiledSceneContainsDrawingVisual = false;
        }

        // 2. Clear CPU collection batch lists and active brushes
        if (!reuseCompiledScene)
        {
            _activeBrushes.Clear();
            _activeGradientStops.Clear();
            _vectorVerticesList.Clear();
            _vectorIndicesList.Clear();
            _textVerticesList.Clear();
            _textureVerticesList.Clear();
            _textureIndicesList.Clear();
            _drawCalls.Clear();
            _hitTestCacheBuilder.Clear();
            ClearLastHitTestIndex();

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
            ReturnMaskRenderPassDrawCallLists();
            _masksToReturnToPool.Clear();
        }

        var extensionFrame = BeginExtensionFrame();
        bool glyphBatchActive = false;
        CommandEncoder* encoder = null;
        System.Diagnostics.Stopwatch uploadSw = null!;
        System.Diagnostics.Stopwatch passSw = null!;
        try
        {

        if (reuseCompiledScene)
        {
            goto SceneCompilationComplete;
        }

        _atlas.BeginBatch();
        glyphBatchActive = true;

        // 3. Compile Layer 0: Root Visual Scene
        _pendingVectorStart = (uint)_vectorIndicesList.Count;
        _pendingTextStart = (uint)_textVerticesList.Count;
        CompileVisualTree(root, Matrix4x4.Identity);
        CommitPendingDrawCalls();

        // 4. Compile Layer 1: Active Popups / External Layers (in proper Z-order)
        if (externalLayers != null && externalLayers.Count > 0)
        {
            var savedActiveClipRect = _activeClipRect;
            var savedClipStack = RentStackSnapshot(_clipStack, out var savedClipStackCount);
            var savedClipScopeIsGeometryMask = RentStackSnapshot(_clipScopeIsGeometryMask, out var savedClipScopeIsGeometryMaskCount);
            var savedActiveOpacity = _activeOpacity;
            var savedOpacityStack = RentStackSnapshot(_opacityStack, out var savedOpacityStackCount);

            try
            {
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
            }
            finally
            {
                _activeClipRect = savedActiveClipRect;
                RestoreStack(ref _clipStack, savedClipStack, savedClipStackCount);
                RestoreClipScopeStack(savedClipScopeIsGeometryMask, savedClipScopeIsGeometryMaskCount);
                _activeOpacity = savedActiveOpacity;
                RestoreStack(ref _opacityStack, savedOpacityStack, savedOpacityStackCount);
                ReturnStackSnapshot(savedClipStack, savedClipStackCount);
                ReturnStackSnapshot(savedClipScopeIsGeometryMask, savedClipScopeIsGeometryMaskCount);
                ReturnStackSnapshot(savedOpacityStack, savedOpacityStackCount);
            }
        }

        // 5. Compile Layer 2: Tooltips
        if (activeToolTip != null)
        {
            var savedActiveClipRect = _activeClipRect;
            var savedClipStack = RentStackSnapshot(_clipStack, out var savedClipStackCount);
            var savedClipScopeIsGeometryMask = RentStackSnapshot(_clipScopeIsGeometryMask, out var savedClipScopeIsGeometryMaskCount);
            var savedActiveOpacity = _activeOpacity;
            var savedOpacityStack = RentStackSnapshot(_opacityStack, out var savedOpacityStackCount);

            try
            {
                _activeClipRect = null;
                _clipStack.Clear();
                _clipScopeIsGeometryMask.Clear();
                _activeOpacity = 1.0f;
                _opacityStack.Clear();

                _pendingVectorStart = (uint)_vectorIndicesList.Count;
                _pendingTextStart = (uint)_textVerticesList.Count;

                CompileVisualTree(activeToolTip, Matrix4x4.Identity);
                CommitPendingDrawCalls();
            }
            finally
            {
                _activeClipRect = savedActiveClipRect;
                RestoreStack(ref _clipStack, savedClipStack, savedClipStackCount);
                RestoreClipScopeStack(savedClipScopeIsGeometryMask, savedClipScopeIsGeometryMaskCount);
                _activeOpacity = savedActiveOpacity;
                RestoreStack(ref _opacityStack, savedOpacityStack, savedOpacityStackCount);
                ReturnStackSnapshot(savedClipStack, savedClipStackCount);
                ReturnStackSnapshot(savedClipScopeIsGeometryMask, savedClipScopeIsGeometryMaskCount);
                ReturnStackSnapshot(savedOpacityStack, savedOpacityStackCount);
            }
        }

        if (Options.EnableGpuHitTesting)
        {
            SetLastHitTestIndex(_hitTestCacheBuilder.BuildIndex());
        }

        // 6. Compile Layer 3: Adorner / DevTools bounds highlights
        if (RenderDiagnostics != null)
        {
            var savedActiveClipRect = _activeClipRect;
            var savedClipStack = RentStackSnapshot(_clipStack, out var savedClipStackCount);
            var savedClipScopeIsGeometryMask = RentStackSnapshot(_clipScopeIsGeometryMask, out var savedClipScopeIsGeometryMaskCount);
            var savedActiveOpacity = _activeOpacity;
            var savedOpacityStack = RentStackSnapshot(_opacityStack, out var savedOpacityStackCount);

            try
            {
                _activeClipRect = null;
                _clipStack.Clear();
                _clipScopeIsGeometryMask.Clear();
                _activeOpacity = 1.0f;
                _opacityStack.Clear();

                _pendingVectorStart = (uint)_vectorIndicesList.Count;
                _pendingTextStart = (uint)_textVerticesList.Count;

                var diagContext = new DrawingContext();
                RenderDiagnostics(diagContext, width, height);
                var diagnosticCommands = diagContext.Commands;
                var diagnosticCommandCount = diagnosticCommands.Count;
                for (var commandIndex = 0; commandIndex < diagnosticCommandCount; commandIndex++)
                {
                    var cmd = diagnosticCommands[commandIndex];
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
                        case RenderCommandType.DrawVertexMesh:
                            CompileVertexMeshCommand(cmd, activeTransform);
                            break;
                        case RenderCommandType.DrawPointBatch:
                            CompilePointBatchCommand(cmd, activeTransform);
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
            }
            finally
            {
                _activeClipRect = savedActiveClipRect;
                RestoreStack(ref _clipStack, savedClipStack, savedClipStackCount);
                RestoreClipScopeStack(savedClipScopeIsGeometryMask, savedClipScopeIsGeometryMaskCount);
                _activeOpacity = savedActiveOpacity;
                RestoreStack(ref _opacityStack, savedOpacityStack, savedOpacityStackCount);
                ReturnStackSnapshot(savedClipStack, savedClipStackCount);
                ReturnStackSnapshot(savedClipScopeIsGeometryMask, savedClipScopeIsGeometryMaskCount);
                ReturnStackSnapshot(savedOpacityStack, savedOpacityStackCount);
            }
        }

SceneCompilationComplete:
        if (glyphBatchActive)
        {
            glyphBatchActive = false;
            _atlas.EndBatch();
        }

        if (_pathAtlas.CapacityExceeded ||
            _pathAtlas.Generation != pathAtlasGenerationAtCompilationStart)
        {
            throw new PathAtlasCapacityExceededException(_pathAtlas);
        }

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

        if (wavefrontEnabled)
        {
            PrepareWavefrontComposite(width, height, renderWidth, renderHeight);
        }

        compileSw.Stop();
        uploadSw = System.Diagnostics.Stopwatch.StartNew();

        // Dynamic buffer writing will happen after uploads to keep logic clear

        // Upload CPU batches to dynamic GPU buffers
        if (reuseCompiledScene)
        {
            goto DynamicBufferUploadComplete;
        }

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

DynamicBufferUploadComplete:
        if (reuseCompiledScene)
        {
            goto SceneStateUploadComplete;
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

        // Rasterize all pending paths before starting the render pass.
        _pathAtlas.RasterizePendingPaths();

        CaptureCompiledScene(
            root,
            width,
            height,
            externalLayers,
            activeToolTip,
            hasDynamicDiagnostics);

SceneStateUploadComplete:
        uploadSw.Stop();
        passSw = System.Diagnostics.Stopwatch.StartNew();

        // Recreate MSAA resources if needed (handles initialization and window resizing).
        // Single-sample compositors render directly into the acquired target and retain no
        // redundant full-window color texture.
        if (Options.PrimarySampleCount > 1 &&
            (_msaaTexture == null || _msaaWidth != renderWidth || _msaaHeight != renderHeight))
        {
            ReleaseMsaaResources();
            _advancedBlendScratchTexture?.Dispose();
            _advancedBlendScratchTexture = null;
            _advancedBlendSourceTexture?.Dispose();
            _advancedBlendSourceTexture = null;
            CreateMsaaResources(renderWidth, renderHeight);
        }

        // 5. WebGPU Command Encoder and Render Pass Execution
        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compositor Command Encoder") };
        encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        // Run mask render passes first!
        ExecuteMaskRenderPasses(encoder, isOffscreen: false);

        var bgColor = ClearColor;
        var colorAttachment = new RenderPassColorAttachment
        {
            View = Options.PrimarySampleCount > 1
                ? _msaaTextureView
                : wavefrontEnabled && _wavefrontColorTexture != null
                    ? _wavefrontColorTexture.ViewPtr
                    : targetView,
            ResolveTarget = Options.PrimarySampleCount > 1
                ? wavefrontEnabled && _wavefrontColorTexture != null
                    ? _wavefrontColorTexture.ViewPtr
                    : targetView
                : null,
            LoadOp = LoadOp.Clear,
            StoreOp = Options.PrimarySampleCount > 1 ? StoreOp.Discard : StoreOp.Store,
            ClearValue = new Color { R = bgColor.X, G = bgColor.Y, B = bgColor.Z, A = bgColor.W }
        };

        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = null
        };

        var pass = _context.Api.CommandEncoderBeginRenderPass(encoder, &passDesc);
        ApplyRenderPassViewport(pass, renderWidth, renderHeight, useRenderTargetViewport: true);

        DrawCallType? currentType = null;
        GpuBlendMode? currentBlendMode = null;
        GpuTexture? currentMaskTexture = null;
        var textureEntries = stackalloc BindGroupEntry[2];

        var drawCallCount = _drawCalls.Count;
        for (var drawCallIndex = 0; drawCallIndex < drawCallCount; drawCallIndex++)
        {
            var dc = _drawCalls[drawCallIndex];
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
                    _context.Api.RenderPassEncoderSetPipeline(pass, activePipeline);
                    fixed (BindGroup** pGrp = &_vectorUniformBindGroup)
                    {
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroup)
                    {
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                    }
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentType = DrawCallType.Vector;
                    currentBlendMode = dc.BlendMode;
                    currentMaskTexture = dc.MaskTexture;
                }
                else if (currentMaskTexture != dc.MaskTexture)
                {
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                var buffer = _vectorVertexBuffer.BufferPtr;
                _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _vectorVertexBuffer.Size);
                _context.Api.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _vectorIndexBuffer.Size);
                _context.Api.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Text)
            {
                var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: false);
                var maskBindGroup = GetMaskBindGroup(dc.MaskTexture, isOffscreen: false);

                if (currentType != DrawCallType.Text || currentBlendMode != dc.BlendMode)
                {
                    _context.Api.RenderPassEncoderSetPipeline(pass, activePipeline);
                    fixed (BindGroup** pGrp = &_textUniformBindGroup)
                    {
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pAtlas = &_atlasBindGroup)
                    {
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
                    }
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentType = DrawCallType.Text;
                    currentBlendMode = dc.BlendMode;
                    currentMaskTexture = dc.MaskTexture;
                }
                else if (currentMaskTexture != dc.MaskTexture)
                {
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                var buffer = _textVertexBuffer.BufferPtr;
                _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textVertexBuffer.Size);
                _context.Api.RenderPassEncoderDraw(pass, 6, dc.IndexCount, 0, dc.IndexStart);
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

                _context.Api.RenderPassEncoderSetPipeline(pass, activePipeline);
                fixed (BindGroup** pGrp = &_textureUniformBindGroup)
                {
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                }
                var buffer = _textureVertexBuffer.BufferPtr;
                _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textureVertexBuffer.Size);
                _context.Api.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textureIndexBuffer.Size);
                if (currentMaskTexture != dc.MaskTexture || currentType != DrawCallType.Texture || currentBlendMode != dc.BlendMode)
                {
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                currentType = DrawCallType.Texture;
                currentBlendMode = dc.BlendMode;

                var viewPtr = texture.ViewPtr;
                var cacheKey = new TextureCacheKey(
                    texture.Id,
                    texture.Generation,
                    isOffscreen: false,
                    dc.TextureSamplingMode,
                    dc.TextureMaxAnisotropy);

                CachedBindGroup? cachedBg;
                lock (_persistentTextureBindGroups)
                {
                    if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out cachedBg))
                    {
                        textureEntries[0] = new BindGroupEntry
                        {
                            Binding = 0,
                            Sampler = GetTextureSampler(dc.TextureSamplingMode, dc.TextureMaxAnisotropy)
                        };
                        textureEntries[1] = new BindGroupEntry { Binding = 1, TextureView = viewPtr };

                        var bgDesc = new BindGroupDescriptor { Layout = _textureBindGroupLayout, EntryCount = 2, Entries = textureEntries };
                        var bg = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDesc);
                        cachedBg = new CachedBindGroup((nint)bg, _frameNumber);
                        _persistentTextureBindGroups[cacheKey] = cachedBg;
                    }
                    else
                    {
                        cachedBg.LastUsedFrame = _frameNumber;
                    }
                }

                var bindGroup = (BindGroup*)cachedBg.BindGroupPtr;
                _context.Api.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);
                _context.Api.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
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
                            _context.Api.RenderPassEncoderSetPipeline(pass, splinePipeline);
                            fixed (BindGroup** pGrp = &_vectorUniformBindGroup)
                            {
                                _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                            }
                            fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroup)
                            {
                                _context.Api.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                            }
                            currentType = DrawCallType.Vector;
                            currentBlendMode = dc.BlendMode;
                        }

                        if (selectedVectorPipeline || currentMaskTexture != dc.MaskTexture)
                        {
                            _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                            currentMaskTexture = dc.MaskTexture;
                        }

                        if (dc.PointBufferCount > 0)
                        {
                            _context.Api.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
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

        _context.Api.RenderPassEncoderEnd(pass);
        _context.Api.RenderPassEncoderRelease(pass);

        if (wavefrontEnabled &&
            _wavefrontEngine != null &&
            _wavefrontColorTexture != null &&
            _wavefrontQuadVertexBuffer != null &&
            _wavefrontQuadIndexBuffer != null &&
            _wavefrontTextureBindGroup != null)
        {
            _wavefrontEngine.EndFrame(encoder, _wavefrontColorTexture, _currentDpiScale);

            var finalAttachment = new RenderPassColorAttachment
            {
                View = targetView,
                ResolveTarget = null,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearValue = new Color { R = bgColor.X, G = bgColor.Y, B = bgColor.Z, A = bgColor.W }
            };
            var finalPassDescriptor = new RenderPassDescriptor
            {
                ColorAttachmentCount = 1,
                ColorAttachments = &finalAttachment,
                DepthStencilAttachment = null
            };
            var finalPass = _context.Api.CommandEncoderBeginRenderPass(encoder, &finalPassDescriptor);
            ApplyRenderPassViewport(finalPass, renderWidth, renderHeight, useRenderTargetViewport: true);
            _context.Api.RenderPassEncoderSetPipeline(finalPass, _texturePipelineOffscreen);
            _context.Api.RenderPassEncoderSetBindGroup(finalPass, 0, _textureUniformBindGroupOffscreen, 0, null);
            _context.Api.RenderPassEncoderSetBindGroup(finalPass, 1, _wavefrontTextureBindGroup, 0, null);
            _context.Api.RenderPassEncoderSetBindGroup(finalPass, 2, _dummyMaskBindGroupOffscreen, 0, null);
            _context.Api.RenderPassEncoderSetVertexBuffer(
                finalPass,
                0,
                _wavefrontQuadVertexBuffer.BufferPtr,
                0,
                _wavefrontQuadVertexBuffer.Size);
            _context.Api.RenderPassEncoderSetIndexBuffer(
                finalPass,
                _wavefrontQuadIndexBuffer.BufferPtr,
                IndexFormat.Uint32,
                0,
                _wavefrontQuadIndexBuffer.Size);
            _context.Api.RenderPassEncoderDrawIndexed(finalPass, 6, 1, 0, 0, 0);
            _context.Api.RenderPassEncoderEnd(finalPass);
            _context.Api.RenderPassEncoderRelease(finalPass);
        }
        }
        finally
        {
            if (glyphBatchActive)
            {
                glyphBatchActive = false;
                _atlas.EndBatch();
            }
            EndExtensionFrame(extensionFrame);
        }

        // Submit to queue
        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compositor Command Buffer") };
        var cmdBuffer = _context.Api.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Api.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Api.CommandBufferRelease(cmdBuffer);
        _context.Api.CommandEncoderRelease(encoder);

        ReturnPendingMaskTexturesToPool();

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
            PathAtlasCachedCount = _pathAtlas.CachedPathCount,
            SceneCacheHit = reuseCompiledScene,
            SceneCacheMissReason = reuseCompiledScene ? null : _currentSceneCacheMissReason
        };
    }

    private void EvictUnusedBindGroups()
    {
        lock (_persistentTextureBindGroups)
        {
            TextureCacheKey[]? keysToRemove = null;
            int keysToRemoveCount = 0;
            try
            {
                var bindGroupEnumerator = _persistentTextureBindGroups.GetEnumerator();
                while (bindGroupEnumerator.MoveNext())
                {
                    var kvp = bindGroupEnumerator.Current;
                    if (_frameNumber - kvp.Value.LastUsedFrame > 60)
                    {
                        QueueBindGroupRelease(kvp.Value.BindGroupPtr);
                        AddRemovalItem(ref keysToRemove, ref keysToRemoveCount, _persistentTextureBindGroups.Count, kvp.Key);
                    }
                }

                for (int i = 0; i < keysToRemoveCount; i++)
                {
                    _persistentTextureBindGroups.Remove(keysToRemove![i]);
                }
            }
            finally
            {
                ReturnRemovalBuffer(keysToRemove, keysToRemoveCount);
            }
        }
    }

    private bool CanReuseCompiledScene(
        Visual root,
        uint width,
        uint height,
        IReadOnlyList<Visual>? externalLayers,
        Visual? activeToolTip,
        bool hasDynamicDiagnostics)
    {
        _currentSceneCacheMissReason = null;
        if (!Options.EnableCompiledSceneCache) return MissCompiledSceneCache("Compiled scene cache disabled");
        if (!_compiledSceneReusable) return MissCompiledSceneCache(_compiledSceneCacheStateReason);
        if (hasDynamicDiagnostics) return MissCompiledSceneCache("Dynamic diagnostics active");
        if (!ReferenceEquals(_compiledSceneRoot, root)) return MissCompiledSceneCache("Root changed");
        if (_compiledSceneRootVersion != root.ChangeVersion) return MissCompiledSceneCache("Root version changed");
        if (_compiledSceneWidth != width || _compiledSceneHeight != height)
            return MissCompiledSceneCache("Logical target changed");
        if (_compiledSceneRenderTargetWidth != _explicitRenderTargetWidth ||
            _compiledSceneRenderTargetHeight != _explicitRenderTargetHeight ||
            _compiledSceneRenderTargetViewport != _explicitRenderTargetViewport ||
            _compiledSceneDpiScale != _currentDpiScale)
            return MissCompiledSceneCache("Physical target changed");
        if (_compiledSceneGlyphAtlasGeneration != _atlas.Generation)
            return MissCompiledSceneCache("Glyph atlas changed");
        if (_compiledScenePathAtlasGeneration != _pathAtlas.Generation)
            return MissCompiledSceneCache("Path atlas changed");
        if (!ReferenceEquals(_compiledSceneToolTip, activeToolTip) ||
            (activeToolTip != null && _compiledSceneToolTipVersion != activeToolTip.ChangeVersion))
            return MissCompiledSceneCache("Tooltip changed");

        int externalLayerCount = externalLayers?.Count ?? 0;
        if (_compiledExternalLayers.Count != externalLayerCount)
        {
            return MissCompiledSceneCache("External layer count changed");
        }

        for (int i = 0; i < externalLayerCount; i++)
        {
            var layer = externalLayers![i];
            var compiled = _compiledExternalLayers[i];
            if (!ReferenceEquals(compiled.Visual, layer) || compiled.ChangeVersion != layer.ChangeVersion)
            {
                return MissCompiledSceneCache("External layer changed");
            }
        }

        for (int i = 0; i < _compiledLayerOwners.Count; i++)
        {
            var compiled = _compiledLayerOwners[i];
            if (!compiled.Visual.CacheAsLayer ||
                !compiled.Visual.IsVisible ||
                compiled.Visual.IsDirty ||
                compiled.Texture.IsDisposed ||
                !ReferenceEquals(compiled.Visual.LayerTexture, compiled.Texture))
            {
                return MissCompiledSceneCache("Cached layer changed");
            }
        }

        return true;
    }

    private bool MissCompiledSceneCache(string reason)
    {
        _currentSceneCacheMissReason = reason;
        return false;
    }

    private void CaptureCompiledScene(
        Visual root,
        uint width,
        uint height,
        IReadOnlyList<Visual>? externalLayers,
        Visual? activeToolTip,
        bool hasDynamicDiagnostics)
    {
        _compiledSceneCacheStateReason =
            !Options.EnableCompiledSceneCache ? "Compiled scene cache disabled" :
            hasDynamicDiagnostics ? "Dynamic diagnostics active" :
            _compiledSceneContainsDrawingVisual ? "Drawing visuals active" :
            _maskRenderPasses.Count != 0 ? "Mask render passes active" :
            _effectTextures.Count != 0 ? "Effects active" :
            string.Empty;
        _compiledSceneReusable = _compiledSceneCacheStateReason.Length == 0;

        if (!_compiledSceneReusable)
        {
            _compiledExternalLayers.Clear();
            _compiledLayerOwners.Clear();
            return;
        }

        _compiledSceneRoot = root;
        _compiledSceneRootVersion = root.ChangeVersion;
        _compiledSceneWidth = width;
        _compiledSceneHeight = height;
        _compiledSceneRenderTargetWidth = _explicitRenderTargetWidth;
        _compiledSceneRenderTargetHeight = _explicitRenderTargetHeight;
        _compiledSceneRenderTargetViewport = _explicitRenderTargetViewport;
        _compiledSceneDpiScale = _currentDpiScale;
        _compiledSceneToolTip = activeToolTip;
        _compiledSceneToolTipVersion = activeToolTip?.ChangeVersion ?? 0;
        _compiledSceneGlyphAtlasGeneration = _atlas.Generation;
        _compiledScenePathAtlasGeneration = _pathAtlas.Generation;
        _compiledSceneHasGpuTransforms = _hasGpuTransformsInFrame;
        _compiledSceneGpuTransformsCameraView = _gpuTransformsCameraView;
        _compiledSceneCacheStateReason = "Compiled scene available";

        _compiledExternalLayers.Clear();
        int externalLayerCount = externalLayers?.Count ?? 0;
        for (int i = 0; i < externalLayerCount; i++)
        {
            var layer = externalLayers![i];
            _compiledExternalLayers.Add(new CompiledVisualVersion(layer, layer.ChangeVersion));
        }

        _compiledLayerOwners.Clear();
        foreach (var owner in _activeLayerTextureOwners)
        {
            if (owner.LayerTexture is { IsDisposed: false } texture)
            {
                _compiledLayerOwners.Add(new CompiledLayerVersion(owner, texture));
            }
        }
    }

    private void MarkCompiledSceneResourcesUsed()
    {
        for (int i = 0; i < _compiledLayerOwners.Count; i++)
        {
            _activeLayerTextureOwners.Add(_compiledLayerOwners[i].Visual);
        }
    }

    private bool IsTextureBindable(GpuTexture? texture)
    {
        var textureContext = texture?.Context;
        return texture != null
            && !texture.IsDisposed
            && textureContext != null
            && !textureContext.IsDisposed
            && ReferenceEquals(textureContext, _context)
            && texture.TexturePtr != null
            && texture.ViewPtr != null;
    }

    private void HandleTextureDisposed(ulong textureId)
    {
        if (Environment.HasShutdownStarted) return;

        _compiledSceneReusable = false;

        RemoveMaskTexturePoolEntries(textureId);

        lock (_persistentTextureBindGroups)
        {
            TextureCacheKey[]? keysToRemove = null;
            int keysToRemoveCount = 0;
            try
            {
                var bindGroupEnumerator = _persistentTextureBindGroups.GetEnumerator();
                while (bindGroupEnumerator.MoveNext())
                {
                    var key = bindGroupEnumerator.Current.Key;
                    if (key.TextureId == textureId)
                    {
                        AddRemovalItem(ref keysToRemove, ref keysToRemoveCount, _persistentTextureBindGroups.Count, key);
                    }
                }

                for (int i = 0; i < keysToRemoveCount; i++)
                {
                    var key = keysToRemove![i];
                    if (_persistentTextureBindGroups.TryGetValue(key, out var cachedBg))
                    {
                        QueueBindGroupRelease(cachedBg.BindGroupPtr);
                        _persistentTextureBindGroups.Remove(key);
                    }
                }
            }
            finally
            {
                ReturnRemovalBuffer(keysToRemove, keysToRemoveCount);
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

    private void DisposeMaskTexturePool()
    {
        var pooledMaskTextures = RentListSnapshot(_maskTexturePool, out var pooledMaskTextureCount);
        _maskTexturePool.Clear();

        try
        {
            for (int i = 0; i < pooledMaskTextureCount; i++)
            {
                pooledMaskTextures[i].Dispose();
            }
        }
        finally
        {
            ReturnListSnapshot(pooledMaskTextures, pooledMaskTextureCount);
        }
    }

    private void RemoveMaskBindGroups(Dictionary<GpuTexture, nint> cache, ulong textureId)
    {
        lock (cache)
        {
            GpuTexture[]? keysToRemove = null;
            int keysToRemoveCount = 0;
            try
            {
                var maskBindGroupEnumerator = cache.GetEnumerator();
                while (maskBindGroupEnumerator.MoveNext())
                {
                    var key = maskBindGroupEnumerator.Current.Key;
                    if (key.Id == textureId)
                    {
                        AddRemovalItem(ref keysToRemove, ref keysToRemoveCount, cache.Count, key);
                    }
                }

                for (int i = 0; i < keysToRemoveCount; i++)
                {
                    var key = keysToRemove![i];
                    if (cache.TryGetValue(key, out var bindGroupPtr))
                    {
                        QueueBindGroupRelease(bindGroupPtr);
                        cache.Remove(key);
                    }
                }
            }
            finally
            {
                ReturnRemovalBuffer(keysToRemove, keysToRemoveCount);
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
            Visual[]? detached = null;
            int detachedCount = 0;
            try
            {
                var effectTextureEnumerator = _effectTextures.GetEnumerator();
                while (effectTextureEnumerator.MoveNext())
                {
                    var fe = effectTextureEnumerator.Current.Key;
                    if (!IsAttachedToAnyActiveRoot(fe, mainRoot, externalLayers, activeToolTip))
                    {
                        AddRemovalItem(ref detached, ref detachedCount, _effectTextures.Count, fe);
                    }
                }

                for (int i = 0; i < detachedCount; i++)
                {
                    var fe = detached![i];
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
            finally
            {
                ReturnRemovalBuffer(detached, detachedCount);
            }
        }
    }

    private void SweepUnusedLayerTextures(Visual mainRoot, IReadOnlyList<Visual>? externalLayers, Visual? activeToolTip)
    {
        if (_allocatedLayerTextures.Count == 0)
        {
            return;
        }

        Visual[]? stale = null;
        int staleCount = 0;
        try
        {
            var layerTextureEnumerator = _allocatedLayerTextures.GetEnumerator();
            while (layerTextureEnumerator.MoveNext())
            {
                var entry = layerTextureEnumerator.Current;
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
                    AddRemovalItem(ref stale, ref staleCount, _allocatedLayerTextures.Count, owner);
                }
            }

            for (int i = 0; i < staleCount; i++)
            {
                var owner = stale![i];
                if (_allocatedLayerTextures.TryGetValue(owner, out var texture))
                {
                    ReleaseLayerTexture(owner, texture);
                }
            }
        }
        finally
        {
            ReturnRemovalBuffer(stale, staleCount);
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

    private void RestoreClipScopeStack(bool[] savedClipScopeIsGeometryMask, int count)
    {
        RestoreStack(ref _clipScopeIsGeometryMask, savedClipScopeIsGeometryMask, count);
    }

    private static T[] RentListSnapshot<T>(List<T> list, out int count)
    {
        count = list.Count;
        if (count == 0)
        {
            return Array.Empty<T>();
        }

        var snapshot = ArrayPool<T>.Shared.Rent(count);
        CollectionsMarshal.AsSpan(list).CopyTo(snapshot);
        return snapshot;
    }

    private static T[] RentStackSnapshot<T>(in SmallValueStack<T> stack, out int count)
    {
        count = stack.Count;
        if (count == 0)
        {
            return Array.Empty<T>();
        }

        var snapshot = ArrayPool<T>.Shared.Rent(count);
        stack.CopyToStackOrder(snapshot);
        return snapshot;
    }

    private static void RestoreStack<T>(ref SmallValueStack<T> stack, T[] snapshot, int count)
    {
        stack.Clear();
        for (int i = count - 1; i >= 0; i--)
        {
            stack.Push(snapshot[i]);
        }
    }

    private static void RestoreList<T>(List<T> list, T[] snapshot, int count)
    {
        list.Clear();
        if (count == 0)
        {
            return;
        }

        list.EnsureCapacity(count);
        CollectionsMarshal.SetCount(list, count);
        snapshot.AsSpan(0, count).CopyTo(CollectionsMarshal.AsSpan(list));
    }

    private static void ReturnStackSnapshot<T>(T[] snapshot, int count)
    {
        ReturnSnapshot(snapshot, count);
    }

    private static void ReturnListSnapshot<T>(T[] snapshot, int count)
    {
        ReturnSnapshot(snapshot, count);
    }

    private struct SmallValueStack<T> : IDisposable
    {
        private const int InitialArrayCapacity = 4;

        private T _first;
        private T[]? _items;
        private int _count;

        public readonly int Count => _count;

        public void Push(T item)
        {
            if (_count == 0)
            {
                _first = item;
                if (_items != null)
                {
                    _items[0] = item;
                }

                _count = 1;
                return;
            }

            var items = EnsureArray(_count + 1);
            items[_count] = item;
            _count++;
        }

        public T Pop()
        {
            if (_count == 0)
            {
                throw new InvalidOperationException("Cannot pop an empty stack.");
            }

            _count--;
            if (_items != null)
            {
                var item = _items[_count];
                ClearSlot(_count);
                return item;
            }

            var first = _first;
            ClearFirst();
            return first;
        }

        public readonly T Peek()
        {
            if (_count == 0)
            {
                throw new InvalidOperationException("Cannot peek an empty stack.");
            }

            return _items != null
                ? _items[_count - 1]
                : _first;
        }

        public readonly void CopyToStackOrder(T[] destination)
        {
            if (_count == 0)
            {
                return;
            }

            if (_items != null)
            {
                for (int i = 0, source = _count - 1; source >= 0; i++, source--)
                {
                    destination[i] = _items[source];
                }

                return;
            }

            destination[0] = _first;
        }

        public void Clear()
        {
            if (_items != null)
            {
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                {
                    Array.Clear(_items, 0, _count);
                    _first = default!;
                }
            }
            else
            {
                ClearFirst();
            }

            _count = 0;
        }

        public void Dispose()
        {
            var items = _items;
            _items = null;
            _count = 0;
            _first = default!;

            if (items != null)
            {
                ArrayPool<T>.Shared.Return(items, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            }
        }

        private T[] EnsureArray(int capacity)
        {
            var items = _items;
            if (items == null)
            {
                items = ArrayPool<T>.Shared.Rent(Math.Max(InitialArrayCapacity, capacity));
                items[0] = _first;
                _items = items;
                return items;
            }

            if (capacity <= items.Length)
            {
                return items;
            }

            var larger = ArrayPool<T>.Shared.Rent(Math.Max(capacity, items.Length * 2));
            Array.Copy(items, larger, _count);
            ArrayPool<T>.Shared.Return(items, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            _items = larger;
            return larger;
        }

        private void ClearSlot(int index)
        {
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                return;
            }

            _items![index] = default!;
            if (_count == 0)
            {
                _first = default!;
            }
        }

        private void ClearFirst()
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                _first = default!;
            }
        }
    }

    private static void AddRemovalItem<T>(ref T[]? buffer, ref int count, int capacity, T item)
    {
        buffer ??= ArrayPool<T>.Shared.Rent(Math.Max(1, capacity));
        if (count >= buffer.Length)
        {
            var larger = ArrayPool<T>.Shared.Rent(buffer.Length * 2);
            buffer.AsSpan(0, count).CopyTo(larger);
            ReturnSnapshot(buffer, count);
            buffer = larger;
        }

        buffer[count++] = item;
    }

    private static void ReturnRemovalBuffer<T>(T[]? buffer, int count)
    {
        if (buffer == null)
        {
            return;
        }

        if (count == 0)
        {
            ArrayPool<T>.Shared.Return(buffer);
        }
        else
        {
            ReturnSnapshot(buffer, count);
        }
    }

    private static void ReturnSnapshot<T>(T[] snapshot, int count)
    {
        if (count == 0)
        {
            return;
        }

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(snapshot, 0, count);
        }
        ArrayPool<T>.Shared.Return(snapshot);
    }

    private List<CompositorDrawCall> RentDrawCallList(int capacity)
    {
        if (_drawCallListPool.Count > 0)
        {
            var list = _drawCallListPool.Pop();
            if (capacity > list.Capacity)
            {
                list.Capacity = capacity;
            }

            return list;
        }

        return new List<CompositorDrawCall>(capacity);
    }

    private void ReturnDrawCallList(List<CompositorDrawCall> list)
    {
        list.Clear();
        if (list.Capacity > MaxPooledDrawCallListCapacity ||
            _drawCallListPool.Count >= MaxPooledDrawCallLists)
        {
            return;
        }

        _drawCallListPool.Push(list);
    }

    private List<CompositorDrawCall> RentMaskDrawCallList(int capacity)
    {
        return RentDrawCallList(capacity);
    }

    private void ReturnMaskDrawCallList(List<CompositorDrawCall> list)
    {
        ReturnDrawCallList(list);
    }

    private void ReturnMaskRenderPassDrawCallLists()
    {
        var maskPassCount = _maskRenderPasses.Count;
        for (var maskPassIndex = 0; maskPassIndex < maskPassCount; maskPassIndex++)
        {
            var maskPass = _maskRenderPasses[maskPassIndex];
            ReturnMaskDrawCallList(maskPass.DrawCalls);
        }

        _maskRenderPasses.Clear();
    }

    private void ReturnPendingMaskTexturesToPool()
    {
        var maskTextureCount = _masksToReturnToPool.Count;
        for (var maskTextureIndex = 0; maskTextureIndex < maskTextureCount; maskTextureIndex++)
        {
            _maskTexturePool.Add(_masksToReturnToPool[maskTextureIndex]);
        }

        _masksToReturnToPool.Clear();
        ReturnMaskRenderPassDrawCallLists();
    }

    private void PushOpacityValue(float opacity)
    {
        _opacityStack.Push(_activeOpacity);
        _activeOpacity *= opacity;
    }

    private void PopOpacityValue()
    {
        if (_opacityStack.Count > 0)
        {
            _activeOpacity = _opacityStack.Pop();
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
        if (node is DrawingVisual)
        {
            _compiledSceneContainsDrawingVisual = true;
        }

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
            ? PushVisualCompositeScope(node, globalTransform, parentTransform)
            : VisualCompositeScope.None;

        AddVisualHitTestBounds(node, globalTransform);

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

            var commands = ctx.Commands;
            var commandCount = commands.Count;
            for (var commandIndex = 0; commandIndex < commandCount; commandIndex++)
            {
                var cmd = commands[commandIndex];
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

                AddHitTestStateCommand(cmd, activeTransform);

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
                        if (cmd.Picture != null)
                            PushOpacityMaskValue(cmd.Picture, cmd.Rect, activeTransform);
                        else if (cmd.Brush != null)
                            PushOpacityMaskValue(cmd.Brush, cmd.Rect, activeTransform);
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
                    case RenderCommandType.DrawVertexMesh:
                        CompileVertexMeshCommand(cmd, activeTransform);
                        break;
                    case RenderCommandType.DrawPointBatch:
                        CompilePointBatchCommand(cmd, activeTransform);
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
                        CompilePicture(cmd.Picture, activeTransform);
                        break;
                    case RenderCommandType.DrawGlyphRun:
                        CompileGlyphRunCommand(cmd, activeTransform);
                        break;
                }

                AddHitTestDrawCommand(cmd, activeTransform, ctx);

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

    private void AddVisualHitTestBounds(Visual node, Matrix4x4 globalTransform)
    {
        if (node.HitTestId == 0 || node.Size.X <= 0f || node.Size.Y <= 0f)
        {
            return;
        }

        AddHitTestCommand(
            new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Rect = new Rect(Vector2.Zero, node.Size)
            },
            globalTransform,
            node.HitTestId);
    }

    private void CompilePicture(GpuPicture? picture, Matrix4x4 globalTransform)
    {
        if (picture == null) return;
        var pictureStrokeScale = TransformMetrics.GetStrokeScale(globalTransform);
        var commands = picture.Commands;
        for (var commandIndex = 0; commandIndex < commands.Length; commandIndex++)
        {
            var cmd = commands[commandIndex];
            int vectorStart = _vectorVerticesList.Count;
            int textStart = _textVerticesList.Count;
            var activeTransform = cmd.UseGpuTransforms ? Matrix4x4.Identity : globalTransform;
            if (cmd.Type != RenderCommandType.DrawPath)
            {
                activeTransform = (cmd.Transform == default) ? activeTransform : cmd.Transform * activeTransform;
            }

            var brushTransform = cmd.Type == RenderCommandType.DrawPath && cmd.Transform != default
                ? cmd.Transform * activeTransform
                : activeTransform;
            if (!UsesLocalBrushCoordinates(cmd.Type))
            {
                TransformCommandBrushes(ref cmd, brushTransform, pictureStrokeScale);
            }
            else
            {
                ScaleCommandPen(ref cmd, pictureStrokeScale);
                if (cmd.Type == RenderCommandType.DrawPath)
                {
                    TransformCommandPenBrush(ref cmd, brushTransform);
                }
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

            AddHitTestStateCommand(cmd, activeTransform);

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
                    if (cmd.Picture != null)
                        PushOpacityMaskValue(cmd.Picture, cmd.Rect, activeTransform);
                    else if (cmd.Brush != null)
                        PushOpacityMaskValue(cmd.Brush, cmd.Rect, activeTransform);
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
                case RenderCommandType.DrawVertexMesh:
                    CompileVertexMeshCommand(cmd, activeTransform);
                    break;
                case RenderCommandType.DrawPointBatch:
                    CompilePointBatchCommand(cmd, activeTransform);
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
                    CompilePicture(cmd.Picture, activeTransform);
                    break;
                case RenderCommandType.DrawGlyphRun:
                    CompileGlyphRunCommand(cmd, activeTransform);
                    break;
            }

            AddHitTestDrawCommand(cmd, activeTransform, picture);

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

    private static void TransformCommandBrushes(
        ref RenderCommand command,
        Matrix4x4 commandTransform,
        float strokeScale)
    {
        if (!Matrix4x4.Invert(commandTransform, out var inverseCommandTransform))
        {
            return;
        }

        command.Brush = TransformCommandBrush(command.Brush, inverseCommandTransform);
        if (command.Pen != null)
        {
            var penScale = command.IsPenThicknessLocal ? 1f : strokeScale;
            var penThickness = command.Pen.Thickness * penScale;
            command.Pen = new Pen(
                TransformCommandBrush(command.Pen.Brush, inverseCommandTransform)!,
                penThickness,
                command.Pen.LineJoin,
                command.Pen.MiterLimit,
                command.Pen.StartLineCap,
                command.Pen.EndLineCap,
                command.Pen.DashCap,
                ScaleRelativeDashArray(command.Pen.DashArray, penScale),
                command.Pen.DashOffset / penScale);
        }
    }

    private static bool UsesLocalBrushCoordinates(RenderCommandType commandType)
    {
        return commandType is
            RenderCommandType.DrawRect or
            RenderCommandType.DrawEllipse or
            RenderCommandType.DrawCircle or
            RenderCommandType.DrawRoundedRect or
            RenderCommandType.DrawPath or
            RenderCommandType.DrawPointBatch;
    }

    private static void ScaleCommandPen(ref RenderCommand command, float strokeScale)
    {
        if (command.Pen == null || command.IsPenThicknessLocal)
        {
            return;
        }

        command.Pen = new Pen(
            command.Pen.Brush,
            command.Pen.Thickness * strokeScale,
            command.Pen.LineJoin,
            command.Pen.MiterLimit,
            command.Pen.StartLineCap,
            command.Pen.EndLineCap,
            command.Pen.DashCap,
            ScaleRelativeDashArray(command.Pen.DashArray, strokeScale),
            command.Pen.DashOffset / strokeScale);
    }

    private static double[]? ScaleRelativeDashArray(double[]? dashArray, float strokeScale)
    {
        if (dashArray == null || strokeScale == 1f)
        {
            return dashArray;
        }

        for (var i = 0; i < dashArray.Length; i++)
        {
            dashArray[i] /= strokeScale;
        }

        return dashArray;
    }

    private static void TransformCommandPenBrush(ref RenderCommand command, Matrix4x4 commandTransform)
    {
        if (command.Pen == null || !Matrix4x4.Invert(commandTransform, out var inverseCommandTransform))
        {
            return;
        }

        command.Pen = new Pen(
            TransformCommandBrush(command.Pen.Brush, inverseCommandTransform)!,
            command.Pen.Thickness,
            command.Pen.LineJoin,
            command.Pen.MiterLimit,
            command.Pen.StartLineCap,
            command.Pen.EndLineCap,
            command.Pen.DashCap,
            command.Pen.DashArray,
            command.Pen.DashOffset);
    }

    private static Brush? TransformCommandBrush(Brush? brush, Matrix4x4 inverseCommandTransform)
    {
        return brush switch
        {
            LinearGradientBrush linear => new LinearGradientBrush(linear.StartPoint, linear.EndPoint, linear.Stops)
            {
                Opacity = linear.Opacity,
                SpreadMethod = linear.SpreadMethod,
                ColorInterpolationMode = linear.ColorInterpolationMode,
                CoordinateTransform = inverseCommandTransform * linear.CoordinateTransform
            },
            RadialGradientBrush radial => new RadialGradientBrush(
                radial.Center,
                radial.GradientOrigin,
                radial.RadiusX,
                radial.RadiusY,
                radial.Stops)
            {
                Opacity = radial.Opacity,
                SpreadMethod = radial.SpreadMethod,
                ColorInterpolationMode = radial.ColorInterpolationMode,
                CoordinateTransform = inverseCommandTransform * radial.CoordinateTransform
            },
            TwoPointConicalGradientBrush conical => new TwoPointConicalGradientBrush(
                conical.StartCenter,
                conical.StartRadius,
                conical.EndCenter,
                conical.EndRadius,
                conical.Stops)
            {
                Opacity = conical.Opacity,
                SpreadMethod = conical.SpreadMethod,
                ColorInterpolationMode = conical.ColorInterpolationMode,
                CoordinateTransform = inverseCommandTransform * conical.CoordinateTransform
            },
            SweepGradientBrush sweep => new SweepGradientBrush(sweep.Center, sweep.Stops)
            {
                Opacity = sweep.Opacity,
                StartAngle = sweep.StartAngle,
                EndAngle = sweep.EndAngle,
                SpreadMethod = sweep.SpreadMethod,
                ColorInterpolationMode = sweep.ColorInterpolationMode,
                CoordinateTransform = inverseCommandTransform * sweep.CoordinateTransform
            },
            PerlinNoiseBrush perlin => new PerlinNoiseBrush(
                perlin.IsTurbulence,
                perlin.BaseFrequency,
                perlin.NumOctaves,
                perlin.Seed,
                perlin.TileSize)
            {
                Opacity = perlin.Opacity,
                CoordinateTransform = inverseCommandTransform * perlin.CoordinateTransform
            },
            _ => brush
        };
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


    private static float GetSubpixelPhase(float value)
    {
        return float.IsFinite(value) ? value - MathF.Floor(value) : 0f;
    }

    private static float QuantizeVectorGlyphPhase(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0f;
        }

        var quantized = MathF.Round(value * VectorGlyphSubpixelPhaseGrid) /
            VectorGlyphSubpixelPhaseGrid;
        return quantized >= 1f ? 0f : quantized;
    }

    private bool TryCompileDirectRoundedRectanglePathFill(
        RenderCommand command,
        Matrix4x4 transform)
    {
        if (command.Path == null || command.Brush == null || command.UseVectorGlyphRendering ||
            !IsAxisAlignedClipTransform(transform) ||
            !TryReadDirectRoundedRectanglePath(
                command.Path,
                out DirectRoundedRectangleContour outer,
                out DirectRoundedRectangleContour inner,
                out bool hasInner))
        {
            return false;
        }

        // Algorithm: recognize the typed line/arc topology of a partial-corner
        // rounded rectangle, sample each quarter arc to a bounded device-space
        // chord error, then emit a balanced convex triangulation or an outer/inner
        // triangle band. Each triangle uses the existing analytic edge-distance
        // shader with deterministic ownership for shared edges, so page-sized
        // Border geometry does not reserve or rasterize a PathAtlas tile. Time
        // and temporary space are O(C), where C is the bounded contour size.
        int cornerSegmentCount = GetDirectRoundedCornerSegmentCount(outer, inner, hasInner, transform);
        int contourPointCount = 4 * (cornerSegmentCount + 1);
        Span<Vector2> outerPoints = stackalloc Vector2[contourPointCount];
        BuildDirectRoundedRectangleContour(outer, outerPoints, cornerSegmentCount);
        Span<Vector2> innerPoints = stackalloc Vector2[contourPointCount];
        if (hasInner)
        {
            BuildDirectRoundedRectangleContour(inner, innerPoints, cornerSegmentCount);
        }

        int outerPointCount = hasInner
            ? contourPointCount
            : CompactDirectRoundedContour(outerPoints);

        int triangleCapacity = hasInner
            ? contourPointCount * 2
            : Math.Max(0, outerPointCount - 2);
        int vertexStart = _vectorVerticesList.Count;
        int indexStart = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, vertexStart + triangleCapacity * 4);
        CollectionsMarshal.SetCount(_vectorIndicesList, indexStart + triangleCapacity * 6);
        Span<VectorVertex> vertices = CollectionsMarshal.AsSpan(_vectorVerticesList);
        Span<uint> indices = CollectionsMarshal.AsSpan(_vectorIndicesList);
        int currentVertexCount = vertexStart;
        int currentIndexCount = indexStart;
        float brushIndex = RegisterBrush(command.Brush);

        if (hasInner)
        {
            for (int pointIndex = 0; pointIndex < contourPointCount; pointIndex++)
            {
                int nextIndex = (pointIndex + 1) % contourPointCount;
                Vector2 outerStart = outerPoints[pointIndex];
                Vector2 outerEnd = outerPoints[nextIndex];
                Vector2 innerStart = innerPoints[pointIndex];
                Vector2 innerEnd = innerPoints[nextIndex];

                AppendDirectFillTriangleVertices(
                    vertices,
                    indices,
                    ref currentVertexCount,
                    ref currentIndexCount,
                    brushIndex,
                    outerStart,
                    outerEnd,
                    innerEnd,
                    exteriorEdgeMask: 1u,
                    ownedInternalEdgeMask: 4u,
                    transform,
                    command.IsEdgeAliased);
                AppendDirectFillTriangleVertices(
                    vertices,
                    indices,
                    ref currentVertexCount,
                    ref currentIndexCount,
                    brushIndex,
                    outerStart,
                    innerEnd,
                    innerStart,
                    exteriorEdgeMask: 2u,
                    ownedInternalEdgeMask: 4u,
                    transform,
                    command.IsEdgeAliased);
            }
        }
        else
        {
            AppendBalancedDirectFillTriangles(
                outerPoints[..outerPointCount],
                startIndex: 0,
                endIndex: outerPointCount - 1,
                vertices,
                indices,
                ref currentVertexCount,
                ref currentIndexCount,
                brushIndex,
                transform,
                command.IsEdgeAliased);
        }

        CollectionsMarshal.SetCount(_vectorVerticesList, currentVertexCount);
        CollectionsMarshal.SetCount(_vectorIndicesList, currentIndexCount);
        return currentVertexCount > vertexStart;
    }

    private static int CompactDirectRoundedContour(Span<Vector2> points)
    {
        int outputCount = 0;
        for (int inputIndex = 0; inputIndex < points.Length; inputIndex++)
        {
            Vector2 point = points[inputIndex];
            if (outputCount > 0 &&
                Vector2.DistanceSquared(points[outputCount - 1], point) <= StrokeEpsilon * StrokeEpsilon)
            {
                continue;
            }

            points[outputCount++] = point;
        }

        if (outputCount > 1 &&
            Vector2.DistanceSquared(points[0], points[outputCount - 1]) <= StrokeEpsilon * StrokeEpsilon)
        {
            outputCount--;
        }

        return outputCount;
    }

    private static void AppendBalancedDirectFillTriangles(
        ReadOnlySpan<Vector2> contourPoints,
        int startIndex,
        int endIndex,
        Span<VectorVertex> vertices,
        Span<uint> indices,
        ref int currentVertexCount,
        ref int currentIndexCount,
        float brushIndex,
        Matrix4x4 transform,
        bool isEdgeAliased)
    {
        if (endIndex - startIndex < 2)
        {
            return;
        }

        // Algorithm: recursively split the convex contour at its midpoint. This
        // produces N-2 non-overlapping triangles with logarithmic diagonal depth,
        // avoiding the center fan's N large center-to-arc AABBs. Boundary edges
        // retain analytic AA; exactly one directed copy owns every internal edge.
        // Time is O(N), recursion space is O(log N), and output space is O(N).
        int middleIndex = startIndex + (endIndex - startIndex) / 2;
        GetDirectFillTriangleEdgeMasks(
            startIndex,
            middleIndex,
            endIndex,
            contourPoints.Length,
            out uint exteriorEdgeMask,
            out uint ownedInternalEdgeMask);
        AppendDirectFillTriangleVertices(
            vertices,
            indices,
            ref currentVertexCount,
            ref currentIndexCount,
            brushIndex,
            contourPoints[startIndex],
            contourPoints[middleIndex],
            contourPoints[endIndex],
            exteriorEdgeMask,
            ownedInternalEdgeMask,
            transform,
            isEdgeAliased);

        AppendBalancedDirectFillTriangles(
            contourPoints,
            startIndex,
            middleIndex,
            vertices,
            indices,
            ref currentVertexCount,
            ref currentIndexCount,
            brushIndex,
            transform,
            isEdgeAliased);
        AppendBalancedDirectFillTriangles(
            contourPoints,
            middleIndex,
            endIndex,
            vertices,
            indices,
            ref currentVertexCount,
            ref currentIndexCount,
            brushIndex,
            transform,
            isEdgeAliased);
    }

    private static void GetDirectFillTriangleEdgeMasks(
        int point0,
        int point1,
        int point2,
        int contourPointCount,
        out uint exteriorEdgeMask,
        out uint ownedInternalEdgeMask)
    {
        uint exterior = 0;
        uint ownedInternal = 0;
        ClassifyEdge(point0, point1, 1u);
        ClassifyEdge(point1, point2, 2u);
        ClassifyEdge(point2, point0, 4u);
        exteriorEdgeMask = exterior;
        ownedInternalEdgeMask = ownedInternal;

        void ClassifyEdge(int start, int end, uint bit)
        {
            bool isBoundary = (start + 1) % contourPointCount == end ||
                (end + 1) % contourPointCount == start;
            if (isBoundary)
            {
                exterior |= bit;
            }
            else if (start < end)
            {
                ownedInternal |= bit;
            }
        }
    }

    private static void AppendDirectFillTriangleVertices(
        Span<VectorVertex> vertices,
        Span<uint> indices,
        ref int currentVertexCount,
        ref int currentIndexCount,
        float brushIndex,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        uint exteriorEdgeMask,
        uint ownedInternalEdgeMask,
        Matrix4x4 transform,
        bool isEdgeAliased)
    {
        Vector2 edge0 = p1 - p0;
        Vector2 edge1 = p2 - p0;
        float area = edge0.X * edge1.Y - edge0.Y * edge1.X;
        if (!float.IsFinite(area) || MathF.Abs(area) <= StrokeEpsilon)
        {
            return;
        }

        float scaleX = new Vector2(transform.M11, transform.M12).Length();
        float scaleY = new Vector2(transform.M21, transform.M22).Length();
        float minimumScale = MathF.Min(scaleX, scaleY);
        if (!float.IsFinite(minimumScale) || minimumScale <= StrokeEpsilon)
        {
            return;
        }

        float padding = 1.5f / minimumScale;
        Vector2 min = Vector2.Min(p0, Vector2.Min(p1, p2)) - new Vector2(padding);
        Vector2 max = Vector2.Max(p0, Vector2.Max(p1, p2)) + new Vector2(padding);
        if (!IsFinite(min) || !IsFinite(max))
        {
            return;
        }

        Vector2 position0 = Vector2.Transform(new Vector2(min.X, min.Y), transform);
        Vector2 position1 = Vector2.Transform(new Vector2(max.X, min.Y), transform);
        Vector2 position2 = Vector2.Transform(new Vector2(max.X, max.Y), transform);
        Vector2 position3 = Vector2.Transform(new Vector2(min.X, max.Y), transform);
        var firstPoints = new Vector4(p0.X, p0.Y, p1.X, p1.Y);
        float shapeType = EncodeShapeType(isEdgeAliased, TriangleSdfShapeType);
        uint vertexIndex = (uint)currentVertexCount;

        vertices[currentVertexCount++] = new VectorVertex(
            position0, firstPoints, new Vector2(min.X, min.Y), brushIndex, p2, exteriorEdgeMask, ownedInternalEdgeMask, shapeType);
        vertices[currentVertexCount++] = new VectorVertex(
            position1, firstPoints, new Vector2(max.X, min.Y), brushIndex, p2, exteriorEdgeMask, ownedInternalEdgeMask, shapeType);
        vertices[currentVertexCount++] = new VectorVertex(
            position2, firstPoints, new Vector2(max.X, max.Y), brushIndex, p2, exteriorEdgeMask, ownedInternalEdgeMask, shapeType);
        vertices[currentVertexCount++] = new VectorVertex(
            position3, firstPoints, new Vector2(min.X, max.Y), brushIndex, p2, exteriorEdgeMask, ownedInternalEdgeMask, shapeType);

        indices[currentIndexCount++] = vertexIndex;
        indices[currentIndexCount++] = vertexIndex + 1;
        indices[currentIndexCount++] = vertexIndex + 2;
        indices[currentIndexCount++] = vertexIndex;
        indices[currentIndexCount++] = vertexIndex + 2;
        indices[currentIndexCount++] = vertexIndex + 3;
    }

    private static void BuildDirectRoundedRectangleContour(
        DirectRoundedRectangleContour contour,
        Span<Vector2> points,
        int cornerSegmentCount)
    {
        int pointIndex = 0;
        AppendDirectRoundedCorner(
            points, ref pointIndex,
            contour.Left + contour.CornerRadii.X,
            contour.Top + contour.CornerRadii.X,
            contour.CornerRadii.X,
            MathF.PI,
            MathF.PI * 1.5f,
            cornerSegmentCount);
        AppendDirectRoundedCorner(
            points, ref pointIndex,
            contour.Right - contour.CornerRadii.Y,
            contour.Top + contour.CornerRadii.Y,
            contour.CornerRadii.Y,
            MathF.PI * 1.5f,
            MathF.PI * 2f,
            cornerSegmentCount);
        AppendDirectRoundedCorner(
            points, ref pointIndex,
            contour.Right - contour.CornerRadii.Z,
            contour.Bottom - contour.CornerRadii.Z,
            contour.CornerRadii.Z,
            0f,
            MathF.PI * 0.5f,
            cornerSegmentCount);
        AppendDirectRoundedCorner(
            points, ref pointIndex,
            contour.Left + contour.CornerRadii.W,
            contour.Bottom - contour.CornerRadii.W,
            contour.CornerRadii.W,
            MathF.PI * 0.5f,
            MathF.PI,
            cornerSegmentCount);
    }

    private static void AppendDirectRoundedCorner(
        Span<Vector2> points,
        ref int pointIndex,
        float centerX,
        float centerY,
        float radius,
        float startAngle,
        float endAngle,
        int cornerSegmentCount)
    {
        for (int segmentIndex = 0; segmentIndex <= cornerSegmentCount; segmentIndex++)
        {
            float t = segmentIndex / (float)cornerSegmentCount;
            float angle = startAngle + (endAngle - startAngle) * t;
            points[pointIndex++] = new Vector2(
                centerX + MathF.Cos(angle) * radius,
                centerY + MathF.Sin(angle) * radius);
        }
    }

    private static int GetDirectRoundedCornerSegmentCount(
        DirectRoundedRectangleContour outer,
        DirectRoundedRectangleContour inner,
        bool hasInner,
        Matrix4x4 transform)
    {
        float maximumRadius = MathF.Max(
            MathF.Max(outer.CornerRadii.X, outer.CornerRadii.Y),
            MathF.Max(outer.CornerRadii.Z, outer.CornerRadii.W));
        if (hasInner)
        {
            maximumRadius = MathF.Max(
                maximumRadius,
                MathF.Max(
                    MathF.Max(inner.CornerRadii.X, inner.CornerRadii.Y),
                    MathF.Max(inner.CornerRadii.Z, inner.CornerRadii.W)));
        }

        float deviceScale = MathF.Max(
            new Vector2(transform.M11, transform.M12).Length(),
            new Vector2(transform.M21, transform.M22).Length());
        float deviceRadius = maximumRadius * deviceScale;
        if (!float.IsFinite(deviceRadius) || deviceRadius <= DirectRoundedMaximumDeviceChordError)
        {
            return DirectRoundedMinimumCornerSegmentCount;
        }

        float cosine = 1f - DirectRoundedMaximumDeviceChordError / deviceRadius;
        float halfAngle = MathF.Acos(Math.Clamp(cosine, -1f, 1f));
        if (!float.IsFinite(halfAngle) || halfAngle <= StrokeEpsilon)
        {
            return DirectRoundedMaximumCornerSegmentCount;
        }

        int segmentCount = (int)MathF.Ceiling(MathF.PI / (4f * halfAngle));
        return Math.Clamp(
            segmentCount,
            DirectRoundedMinimumCornerSegmentCount,
            DirectRoundedMaximumCornerSegmentCount);
    }

    private static bool TryReadDirectRoundedRectanglePath(
        PathGeometry path,
        out DirectRoundedRectangleContour outer,
        out DirectRoundedRectangleContour inner,
        out bool hasInner)
    {
        outer = default;
        inner = default;
        hasInner = false;
        if (path.IsCombined || path.Figures.Count is < 1 or > 2 ||
            !TryReadDirectRoundedRectangleContour(path.Figures[0], out DirectRoundedRectangleContour first))
        {
            return false;
        }

        if (path.Figures.Count == 1)
        {
            if (!HasPartialRoundedCorners(first))
            {
                return false;
            }

            outer = first;
            return true;
        }

        if (path.FillRule != FillRule.EvenOdd ||
            !TryReadDirectRoundedRectangleContour(path.Figures[1], out DirectRoundedRectangleContour second))
        {
            return false;
        }

        float firstArea = first.Width * first.Height;
        float secondArea = second.Width * second.Height;
        outer = firstArea >= secondArea ? first : second;
        inner = firstArea >= secondArea ? second : first;
        float tolerance = GetDirectRoundedTolerance(outer.Width, outer.Height);
        if (!HasPartialRoundedCorners(outer) ||
            inner.Left < outer.Left - tolerance ||
            inner.Top < outer.Top - tolerance ||
            inner.Right > outer.Right + tolerance ||
            inner.Bottom > outer.Bottom + tolerance ||
            inner.Width <= tolerance || inner.Height <= tolerance ||
            inner.Width >= outer.Width - tolerance && inner.Height >= outer.Height - tolerance ||
            !DirectRoundedRectangleContainsContour(outer, inner, tolerance))
        {
            return false;
        }

        hasInner = true;
        return true;
    }

    private static bool DirectRoundedRectangleContainsContour(
        DirectRoundedRectangleContour outer,
        DirectRoundedRectangleContour inner,
        float tolerance)
    {
        // A rounded rectangle is the intersection of its bounding box and the
        // tangent half-planes of its four circular corner arcs. For each outer
        // arc normal, the matching inner corner supplies the exact support point.
        // Comparing the two support functions over each normal quadrant proves
        // full convex-contour containment without sampling.
        return IsCornerSupportContained(
                new Vector2(outer.Left + outer.CornerRadii.X, outer.Top + outer.CornerRadii.X),
                outer.CornerRadii.X,
                new Vector2(inner.Left + inner.CornerRadii.X, inner.Top + inner.CornerRadii.X),
                inner.CornerRadii.X,
                -1f,
                -1f,
                tolerance) &&
            IsCornerSupportContained(
                new Vector2(outer.Right - outer.CornerRadii.Y, outer.Top + outer.CornerRadii.Y),
                outer.CornerRadii.Y,
                new Vector2(inner.Right - inner.CornerRadii.Y, inner.Top + inner.CornerRadii.Y),
                inner.CornerRadii.Y,
                1f,
                -1f,
                tolerance) &&
            IsCornerSupportContained(
                new Vector2(outer.Right - outer.CornerRadii.Z, outer.Bottom - outer.CornerRadii.Z),
                outer.CornerRadii.Z,
                new Vector2(inner.Right - inner.CornerRadii.Z, inner.Bottom - inner.CornerRadii.Z),
                inner.CornerRadii.Z,
                1f,
                1f,
                tolerance) &&
            IsCornerSupportContained(
                new Vector2(outer.Left + outer.CornerRadii.W, outer.Bottom - outer.CornerRadii.W),
                outer.CornerRadii.W,
                new Vector2(inner.Left + inner.CornerRadii.W, inner.Bottom - inner.CornerRadii.W),
                inner.CornerRadii.W,
                -1f,
                1f,
                tolerance);
    }

    private static bool IsCornerSupportContained(
        Vector2 outerCenter,
        float outerRadius,
        Vector2 innerCenter,
        float innerRadius,
        float normalXSign,
        float normalYSign,
        float tolerance)
    {
        if (outerRadius <= tolerance)
        {
            return true;
        }

        Vector2 centerDelta = innerCenter - outerCenter;
        float xSupport = normalXSign * centerDelta.X;
        float ySupport = normalYSign * centerDelta.Y;
        float maximumCenterSupport;
        if (xSupport > 0f && ySupport > 0f)
        {
            maximumCenterSupport = MathF.Sqrt(xSupport * xSupport + ySupport * ySupport);
        }
        else
        {
            maximumCenterSupport = MathF.Max(xSupport, ySupport);
        }

        return maximumCenterSupport + innerRadius <= outerRadius + tolerance;
    }

    private static bool TryReadDirectRoundedRectangleContour(
        PathFigure figure,
        out DirectRoundedRectangleContour contour)
    {
        contour = default;
        if (!figure.IsClosed || !figure.IsFilled || figure.Segments.Count is < 4 or > 12 ||
            !IsFinite(figure.StartPoint))
        {
            return false;
        }

        float left = figure.StartPoint.X;
        float top = figure.StartPoint.Y;
        float right = figure.StartPoint.X;
        float bottom = figure.StartPoint.Y;
        Vector2 current = figure.StartPoint;
        List<PathSegment> segments = figure.Segments;
        for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            if (!TryGetPathSegmentEndPoint(segments[segmentIndex], out Vector2 end) || !IsFinite(end))
            {
                return false;
            }

            left = MathF.Min(left, end.X);
            top = MathF.Min(top, end.Y);
            right = MathF.Max(right, end.X);
            bottom = MathF.Max(bottom, end.Y);
            current = end;
        }

        float width = right - left;
        float height = bottom - top;
        float tolerance = GetDirectRoundedTolerance(width, height);
        if (!float.IsFinite(width) || !float.IsFinite(height) ||
            width <= tolerance || height <= tolerance)
        {
            return false;
        }

        var radii = Vector4.Zero;
        uint cornerMask = 0;
        current = figure.StartPoint;
        for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            PathSegment segment = segments[segmentIndex];
            _ = TryGetPathSegmentEndPoint(segment, out Vector2 end);
            switch (segment)
            {
                case LineSegment:
                    if (!IsDirectRoundedBoundaryLine(current, end, left, top, right, bottom, tolerance))
                    {
                        return false;
                    }
                    break;
                case ArcSegment arc:
                    if (!TryClassifyDirectRoundedCornerArc(
                            current,
                            end,
                            arc,
                            left,
                            top,
                            right,
                            bottom,
                            tolerance,
                            out int cornerIndex,
                            out float radius) ||
                        (cornerMask & (1u << cornerIndex)) != 0)
                    {
                        return false;
                    }

                    cornerMask |= 1u << cornerIndex;
                    switch (cornerIndex)
                    {
                        case 0: radii.X = radius; break;
                        case 1: radii.Y = radius; break;
                        case 2: radii.Z = radius; break;
                        default: radii.W = radius; break;
                    }
                    break;
                default:
                    return false;
            }

            current = end;
        }

        if (!DirectRoundedPointsEqual(current, figure.StartPoint, tolerance) &&
            !IsDirectRoundedBoundaryLine(current, figure.StartPoint, left, top, right, bottom, tolerance))
        {
            return false;
        }

        if (radii.X + radii.Y > width + tolerance ||
            radii.W + radii.Z > width + tolerance ||
            radii.X + radii.W > height + tolerance ||
            radii.Y + radii.Z > height + tolerance ||
            !MatchesDirectRoundedCanonicalTopology(
                figure,
                left,
                top,
                right,
                bottom,
                radii,
                tolerance))
        {
            return false;
        }

        contour = new DirectRoundedRectangleContour(left, top, right, bottom, radii);
        return true;
    }

    private static bool MatchesDirectRoundedCanonicalTopology(
        PathFigure figure,
        float left,
        float top,
        float right,
        float bottom,
        Vector4 radii,
        float tolerance)
    {
        if (!DirectRoundedPointsEqual(
                figure.StartPoint,
                new Vector2(left + radii.X, top),
                tolerance))
        {
            return false;
        }

        List<PathSegment> segments = figure.Segments;
        int segmentIndex = 0;
        return MatchLine(new Vector2(right - radii.Y, top)) &&
            MatchOptionalArc(radii.Y, new Vector2(right, top + radii.Y)) &&
            MatchLine(new Vector2(right, bottom - radii.Z)) &&
            MatchOptionalArc(radii.Z, new Vector2(right - radii.Z, bottom)) &&
            MatchLine(new Vector2(left + radii.W, bottom)) &&
            MatchOptionalArc(radii.W, new Vector2(left, bottom - radii.W)) &&
            MatchLine(new Vector2(left, top + radii.X)) &&
            MatchOptionalArc(radii.X, figure.StartPoint) &&
            segmentIndex == segments.Count;

        bool MatchLine(Vector2 expectedEnd)
        {
            if (segmentIndex >= segments.Count ||
                segments[segmentIndex++] is not LineSegment line)
            {
                return false;
            }

            return DirectRoundedPointsEqual(line.Point, expectedEnd, tolerance);
        }

        bool MatchOptionalArc(float radius, Vector2 expectedEnd)
        {
            if (radius <= tolerance)
            {
                return true;
            }

            if (segmentIndex >= segments.Count ||
                segments[segmentIndex++] is not ArcSegment arc)
            {
                return false;
            }

            return DirectRoundedPointsEqual(arc.Point, expectedEnd, tolerance);
        }
    }

    private static bool TryClassifyDirectRoundedCornerArc(
        Vector2 start,
        Vector2 end,
        ArcSegment arc,
        float left,
        float top,
        float right,
        float bottom,
        float tolerance,
        out int cornerIndex,
        out float radius)
    {
        cornerIndex = -1;
        radius = arc.Size.X;
        if (!float.IsFinite(radius) || radius <= tolerance ||
            !float.IsFinite(arc.Size.Y) || MathF.Abs(arc.Size.Y - radius) > tolerance ||
            !float.IsFinite(arc.RotationAngle) || MathF.Abs(arc.RotationAngle) > tolerance ||
            arc.IsLargeArc || arc.SweepDirection != SweepDirection.Clockwise)
        {
            return false;
        }

        if (DirectRoundedPointsEqual(start, new Vector2(left, top + radius), tolerance) &&
            DirectRoundedPointsEqual(end, new Vector2(left + radius, top), tolerance))
        {
            cornerIndex = 0;
        }
        else if (DirectRoundedPointsEqual(start, new Vector2(right - radius, top), tolerance) &&
            DirectRoundedPointsEqual(end, new Vector2(right, top + radius), tolerance))
        {
            cornerIndex = 1;
        }
        else if (DirectRoundedPointsEqual(start, new Vector2(right, bottom - radius), tolerance) &&
            DirectRoundedPointsEqual(end, new Vector2(right - radius, bottom), tolerance))
        {
            cornerIndex = 2;
        }
        else if (DirectRoundedPointsEqual(start, new Vector2(left + radius, bottom), tolerance) &&
            DirectRoundedPointsEqual(end, new Vector2(left, bottom - radius), tolerance))
        {
            cornerIndex = 3;
        }

        return cornerIndex >= 0;
    }

    private static bool IsDirectRoundedBoundaryLine(
        Vector2 start,
        Vector2 end,
        float left,
        float top,
        float right,
        float bottom,
        float tolerance)
    {
        if (DirectRoundedPointsEqual(start, end, tolerance))
        {
            return true;
        }

        bool horizontal = MathF.Abs(start.Y - end.Y) <= tolerance &&
            (MathF.Abs(start.Y - top) <= tolerance || MathF.Abs(start.Y - bottom) <= tolerance) &&
            start.X >= left - tolerance && start.X <= right + tolerance &&
            end.X >= left - tolerance && end.X <= right + tolerance;
        bool vertical = MathF.Abs(start.X - end.X) <= tolerance &&
            (MathF.Abs(start.X - left) <= tolerance || MathF.Abs(start.X - right) <= tolerance) &&
            start.Y >= top - tolerance && start.Y <= bottom + tolerance &&
            end.Y >= top - tolerance && end.Y <= bottom + tolerance;
        return horizontal || vertical;
    }

    private static bool DirectRoundedPointsEqual(Vector2 left, Vector2 right, float tolerance) =>
        MathF.Abs(left.X - right.X) <= tolerance && MathF.Abs(left.Y - right.Y) <= tolerance;

    private static float GetDirectRoundedTolerance(float width, float height) =>
        MathF.Max(0.0001f, MathF.Max(MathF.Abs(width), MathF.Abs(height)) * 0.00001f);

    private static bool HasPartialRoundedCorners(DirectRoundedRectangleContour contour)
    {
        int roundedCornerCount = 0;
        if (contour.CornerRadii.X > 0f) roundedCornerCount++;
        if (contour.CornerRadii.Y > 0f) roundedCornerCount++;
        if (contour.CornerRadii.Z > 0f) roundedCornerCount++;
        if (contour.CornerRadii.W > 0f) roundedCornerCount++;
        return roundedCornerCount is > 0 and < 4;
    }

    private void CompilePathCommand(
        RenderCommand cmd,
        Matrix4x4 transform,
        uint subpixelPhaseGrid = PathAtlas.DefaultSubpixelPhaseGrid,
        bool quantizeScale = false)
    {
        SwitchBatch(BatchType.Vector);
        if (cmd.Path == null) return;

        if (VectorEngine == VectorRenderingEngine.Wavefront)
        {
            var activeTransform = cmd.Transform == default ? transform : cmd.Transform * transform;
            (_wavefrontEngine ??= new WavefrontVectorEngine(_context)).DrawPath(
                cmd.Path,
                activeTransform,
                cmd.Brush ?? new SolidColorBrush(Vector4.One));
            return;
        }

        int startIndex = _vectorVerticesList.Count;

        transform = (cmd.Transform == default) ? transform : cmd.Transform * transform;
        var pathCoverageGamma = cmd.PathCoverageGamma;
        if (cmd.UseVectorGlyphRendering && cmd.FontSize > 0f)
        {
            var staticZoom = ActiveCompilationContext?.StaticZoom ?? 1f;
            var (effectiveDpiScale, _, _) = ResolveTextRasterization(
                cmd.FontSize,
                transform,
                _currentDpiScale,
                staticZoom);
            var fontScaleX = cmd.HasFontTransform && float.IsFinite(cmd.FontTransform.X)
                ? cmd.FontTransform.X
                : 1f;
            var fontSkewX = cmd.HasFontTransform && float.IsFinite(cmd.FontTransform.Y)
                ? cmd.FontTransform.Y
                : 0f;
            pathCoverageGamma = MathF.Abs(fontSkewX) > 0.0001f || fontScaleX < 0f
                ? TransformedTextPathCoverageGamma
                : GetTextPathCoverageGamma(
                    cmd.FontSize,
                    transform,
                    TransformMetrics.GetStrokeScale(transform),
                    effectiveDpiScale);
        }
        if (Options.EnableGpuHitTesting && !_suspendHitTestCacheWrites)
        {
            EnsurePathHitTestCompilation(cmd.Path);
        }

        bool compiledDirectRoundedFill = cmd.Brush != null &&
            TryCompileDirectRoundedRectanglePathFill(cmd, transform);
        if (cmd.Brush != null && !compiledDirectRoundedFill)
        {
            float bIdx = RegisterBrush(cmd.Brush);
            var brush = cmd.Brush as SolidColorBrush;
            var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);

            // Extract scale factor from transform
            var scaleX = new Vector2(transform.M11, transform.M12).Length();
            var scaleY = new Vector2(transform.M21, transform.M22).Length();
            if (scaleX < 0.0001f) scaleX = 1f;
            if (scaleY < 0.0001f) scaleY = 1f;
            if (!IsAxisAlignedClipTransform(transform))
            {
                scaleX = scaleY = Math.Max(scaleX, scaleY);
            }

            var info = _pathAtlas.GetOrCreatePath(
                cmd.Path,
                scaleX,
                scaleY,
                GetSubpixelPhase(transform.M41),
                GetSubpixelPhase(transform.M42),
                cmd.PathSampleGrid,
                subpixelPhaseGrid,
                quantizeScale);
            if (info.Width > 0 && info.Height > 0)
            {
                float unscaledMinX = info.MinX / info.Key.ScaleX;
                float unscaledMinY = info.MinY / info.Key.ScaleY;
                float unscaledWidth = info.Width / info.Key.ScaleX;
                float unscaledHeight = info.Height / info.Key.ScaleY;

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

                vertexSpan[0] = new VectorVertex(v0, color, uv0, bIdx, shapeSize: cp0, cornerRadius: pathCoverageGamma, shapeType: pathShapeType);
                vertexSpan[1] = new VectorVertex(v1, color, uv1, bIdx, shapeSize: cp1, cornerRadius: pathCoverageGamma, shapeType: pathShapeType);
                vertexSpan[2] = new VectorVertex(v2, color, uv2, bIdx, shapeSize: cp2, cornerRadius: pathCoverageGamma, shapeType: pathShapeType);
                vertexSpan[3] = new VectorVertex(v3, color, uv3, bIdx, shapeSize: cp3, cornerRadius: pathCoverageGamma, shapeType: pathShapeType);

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
                PathGeometry dashedPath;
                Pen undashedPen;
                if (cmd.GeometryCache?.TryGetDashedStrokePath(cmd.Pen, out dashedPath, out undashedPen) != true)
                {
                    if (!TryCreateDashedStrokePath(cmd.Path, cmd.Pen, out dashedPath))
                    {
                        throw new NotSupportedException("Dashed strokes are not supported for this path geometry.");
                    }

                    undashedPen = CreateUndashedPen(cmd.Pen);
                }

                var dashedCmd = cmd;
                dashedCmd.Brush = null;
                dashedCmd.Path = dashedPath;
                dashedCmd.Pen = undashedPen;
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
            var useAffineStrokeGeometry = RequiresAffineStrokeGeometry(transform);
            var localAffineThickness = useAffineStrokeGeometry
                ? thickness / TransformMetrics.GetStrokeScale(transform)
                : thickness;

            int maxVertices = 0;
            int maxIndices = 0;
            var pathFigures = cmd.Path.Figures;
            for (int figureIndex = 0; figureIndex < pathFigures.Count; figureIndex++)
            {
                var figure = pathFigures[figureIndex];
                int maxJoinTriangles = CountStrokeSegmentJoinTriangleBudget(figure);
                maxVertices += maxJoinTriangles * 4;
                maxIndices += maxJoinTriangles * 6;

                int maxCapTriangles = CountStrokeSegmentCapTriangleBudget(figure, cmd.Pen.StartLineCap, cmd.Pen.EndLineCap);
                maxVertices += maxCapTriangles * 4;
                maxIndices += maxCapTriangles * 6;

                var currentPoint = figure.StartPoint;
                var figureSegments = figure.Segments;
                for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
                {
                    var segment = figureSegments[segmentIndex];
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
                        const int N = 24;
                        maxVertices += useAffineStrokeGeometry ? 4 * N : 2 * (N + 1);
                        maxIndices += 6 * N;
                    }
                    else if (segment is CubicBezierSegment)
                    {
                        const int N = 24;
                        maxVertices += useAffineStrokeGeometry ? 4 * N : 2 * (N + 1);
                        maxIndices += 6 * N;
                    }
                    else if (segment is ArcSegment arc)
                    {
                        if (!useAffineStrokeGeometry && CanUseArcSdfStroke(segmentStart, arc, transform))
                        {
                            maxVertices += 4;
                            maxIndices += 6;
                        }
                        else
                        {
                            var maxAngle = useAffineStrokeGeometry
                                ? AffineStrokeArcMaxAngleRadians
                                : ArcSegmentGeometry.DefaultFlattenAngleRadians;
                            var segmentCount = ArcSegmentGeometry.CountFlattenedSegments(segmentStart, arc, maxAngle);
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

            for (int figureIndex = 0; figureIndex < pathFigures.Count; figureIndex++)
            {
                var figure = pathFigures[figureIndex];
                var currentPoint = figure.StartPoint;
                var firstSegmentStartDirection = default(Vector2);
                var previousSegmentEndDirection = default(Vector2);
                var firstSegmentSmoothJoin = false;
                var hasFirstSegmentStartDirection = false;
                var hasPreviousSegmentEndDirection = false;
                var hasLastCapCandidate = false;
                var hasDegenerateStrokeCandidate = false;
                var degenerateStrokeCenter = default(Vector2);
                var lastCapCenter = default(Vector2);
                var lastCapDirection = default(Vector2);
                var isFirstSegment = true;

                var figureSegments = figure.Segments;
                for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
                {
                    var segment = figureSegments[segmentIndex];
                    var segmentStart = currentPoint;
                    if (!segment.IsStroked)
                    {
                        if (!figure.IsClosed && hasLastCapCandidate)
                        {
                            AppendPathStrokeSegmentCapTriangles(
                                verticesSpan,
                                indicesSpan,
                                ref currentVertexCount,
                                ref currentIndexCount,
                                cmd.Pen,
                                penSolidColor,
                                penBrushIdx,
                                localAffineThickness,
                                lastCapCenter,
                                lastCapDirection,
                                transform,
                                useAffineStrokeGeometry,
                                cmd.IsEdgeAliased,
                                isStart: false);
                        }
                        else if (!figure.IsClosed && hasDegenerateStrokeCandidate)
                        {
                            AppendDegenerateStrokeCapVertices(
                                verticesSpan,
                                indicesSpan,
                                ref currentVertexCount,
                                ref currentIndexCount,
                                cmd.Pen,
                                penSolidColor,
                                penBrushIdx,
                                thickness,
                                degenerateStrokeCenter,
                                transform,
                                cmd.IsEdgeAliased);
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
                        hasDegenerateStrokeCandidate = false;
                        degenerateStrokeCenter = default;
                        lastCapCenter = default;
                        lastCapDirection = default;
                        isFirstSegment = true;
                        continue;
                    }

                    var hasSegmentStartDirection = TryGetPathSegmentStartDirection(segment, segmentStart, out var segmentStartDirection);
                    var hasSegmentEndDirection = TryGetPathSegmentEndDirection(segment, segmentStart, out var segmentEndDirection);
                    if (!hasSegmentStartDirection &&
                        !hasSegmentEndDirection &&
                        TryGetPathSegmentEndPoint(segment, out var degenerateEndPoint) &&
                        Vector2.DistanceSquared(segmentStart, degenerateEndPoint) <= StrokeEpsilon * StrokeEpsilon)
                    {
                        if (isFirstSegment)
                        {
                            hasDegenerateStrokeCandidate = true;
                            degenerateStrokeCenter = segmentStart;
                        }

                        currentPoint = degenerateEndPoint;
                        continue;
                    }

                    hasDegenerateStrokeCandidate = false;
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
                        AppendPathStrokeSegmentCapTriangles(
                            verticesSpan,
                            indicesSpan,
                            ref currentVertexCount,
                            ref currentIndexCount,
                            cmd.Pen,
                            penSolidColor,
                            penBrushIdx,
                            localAffineThickness,
                            segmentStart,
                            segmentStartDirection,
                            transform,
                            useAffineStrokeGeometry,
                            cmd.IsEdgeAliased,
                            isStart: true);
                    }

                    if (!isFirstSegment &&
                        hasPreviousSegmentEndDirection &&
                        hasSegmentStartDirection)
                    {
                        AppendPathStrokeSegmentJoinTriangles(
                            verticesSpan,
                            indicesSpan,
                            ref currentVertexCount,
                            ref currentIndexCount,
                            cmd.Pen,
                            penSolidColor,
                            penBrushIdx,
                            localAffineThickness,
                            segmentStart,
                            previousSegmentEndDirection,
                            segmentStartDirection,
                            transform,
                            useAffineStrokeGeometry,
                            cmd.IsEdgeAliased,
                            segment.IsSmoothJoin);
                    }

                    if (segment is LineSegment line)
                    {
                        var lineStart = segmentStart;
                        var lineEnd = line.Point;

                        if (useAffineStrokeGeometry)
                        {
                            AppendAffineStrokeLineVertices(
                                verticesSpan,
                                indicesSpan,
                                ref currentVertexCount,
                                ref currentIndexCount,
                                penSolidColor,
                                penBrushIdx,
                                localAffineThickness,
                                lineStart,
                                lineEnd,
                                transform,
                                cmd.IsEdgeAliased);
                        }
                        else
                        {
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
                        }

                        currentPoint = lineEnd;
                        hasLastCapCandidate = hasSegmentEndDirection;
                        lastCapDirection = segmentEndDirection;
                        hasPreviousSegmentEndDirection = hasLastCapCandidate;
                        previousSegmentEndDirection = lastCapDirection;
                        lastCapCenter = lineEnd;
                        isFirstSegment = false;
                    }
                    else if (segment is QuadraticBezierSegment quad)
                    {
                        if (useAffineStrokeGeometry)
                        {
                            AppendAffineStrokeQuadraticVertices(
                                verticesSpan,
                                indicesSpan,
                                ref currentVertexCount,
                                ref currentIndexCount,
                                penBrushIdx,
                                localAffineThickness,
                                segmentStart,
                                quad,
                                transform,
                                cmd.IsEdgeAliased);
                        }
                        else
                        {
                            var p0_trans = Vector2.Transform(segmentStart, transform);
                            var p1_trans = Vector2.Transform(quad.ControlPoint, transform);
                            var p2_trans = Vector2.Transform(quad.Point, transform);

                            const int N = 24;
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
                        }

                        currentPoint = quad.Point;
                        hasLastCapCandidate = hasSegmentEndDirection;
                        lastCapDirection = segmentEndDirection;
                        hasPreviousSegmentEndDirection = hasLastCapCandidate;
                        previousSegmentEndDirection = lastCapDirection;
                        lastCapCenter = quad.Point;
                        isFirstSegment = false;
                    }
                    else if (segment is CubicBezierSegment cubic)
                    {
                        if (useAffineStrokeGeometry)
                        {
                            AppendAffineStrokeCubicVertices(
                                verticesSpan,
                                indicesSpan,
                                ref currentVertexCount,
                                ref currentIndexCount,
                                penBrushIdx,
                                localAffineThickness,
                                segmentStart,
                                cubic,
                                transform,
                                cmd.IsEdgeAliased);
                        }
                        else
                        {
                            var p0_trans = Vector2.Transform(segmentStart, transform);
                            var p1_trans = Vector2.Transform(cubic.ControlPoint1, transform);
                            var p2_trans = Vector2.Transform(cubic.ControlPoint2, transform);
                            var p3_trans = Vector2.Transform(cubic.Point, transform);

                            const int N = 24;
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
                        }

                        currentPoint = cubic.Point;
                        hasLastCapCandidate = hasSegmentEndDirection;
                        lastCapDirection = segmentEndDirection;
                        hasPreviousSegmentEndDirection = hasLastCapCandidate;
                        previousSegmentEndDirection = lastCapDirection;
                        lastCapCenter = cubic.Point;
                        isFirstSegment = false;
                    }
                    else if (segment is ArcSegment arc)
                    {
                        var appendedArc = useAffineStrokeGeometry
                            ? AppendAffineStrokeArcVertices(
                                verticesSpan,
                                indicesSpan,
                                ref currentVertexCount,
                                ref currentIndexCount,
                                penBrushIdx,
                                localAffineThickness,
                                segmentStart,
                                arc,
                                transform,
                                cmd.IsEdgeAliased)
                            : AppendStrokeArcVertices(
                                verticesSpan,
                                indicesSpan,
                                ref currentVertexCount,
                                ref currentIndexCount,
                                penBrushIdx,
                                thickness,
                                segmentStart,
                                arc,
                                transform,
                                cmd.IsEdgeAliased);
                        if (!appendedArc)
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

                                if (useAffineStrokeGeometry)
                                {
                                    AppendAffineStrokeLineVertices(
                                        verticesSpan,
                                        indicesSpan,
                                        ref currentVertexCount,
                                        ref currentIndexCount,
                                        penSolidColor,
                                        penBrushIdx,
                                        localAffineThickness,
                                        arcLineStart,
                                        arcLineEnd,
                                        transform,
                                        cmd.IsEdgeAliased);
                                }
                                else
                                {
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
                        }

                        currentPoint = arc.Point;
                        hasLastCapCandidate = hasSegmentEndDirection;
                        lastCapDirection = segmentEndDirection;
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
                    AppendPathStrokeSegmentCapTriangles(
                        verticesSpan,
                        indicesSpan,
                        ref currentVertexCount,
                        ref currentIndexCount,
                        cmd.Pen,
                        penSolidColor,
                        penBrushIdx,
                        localAffineThickness,
                        lastCapCenter,
                        lastCapDirection,
                        transform,
                        useAffineStrokeGeometry,
                        cmd.IsEdgeAliased,
                        isStart: false);
                }
                else if (!figure.IsClosed && hasDegenerateStrokeCandidate)
                {
                    AppendDegenerateStrokeCapVertices(
                        verticesSpan,
                        indicesSpan,
                        ref currentVertexCount,
                        ref currentIndexCount,
                        cmd.Pen,
                        penSolidColor,
                        penBrushIdx,
                        thickness,
                        degenerateStrokeCenter,
                        transform,
                        cmd.IsEdgeAliased);
                }
                else if (figure.IsClosed && currentPoint != figure.StartPoint)
                {
                    var closeLineStart = currentPoint;
                    var closeLineEnd = figure.StartPoint;
                    var closeLineDirection = closeLineEnd - closeLineStart;

                    if (hasPreviousSegmentEndDirection)
                    {
                        AppendPathStrokeSegmentJoinTriangles(
                            verticesSpan,
                            indicesSpan,
                            ref currentVertexCount,
                            ref currentIndexCount,
                            cmd.Pen,
                            penSolidColor,
                            penBrushIdx,
                            localAffineThickness,
                            closeLineStart,
                            previousSegmentEndDirection,
                            closeLineDirection,
                            transform,
                            useAffineStrokeGeometry,
                            cmd.IsEdgeAliased,
                            isSmoothJoin: false);
                    }

                    if (useAffineStrokeGeometry)
                    {
                        AppendAffineStrokeLineVertices(
                            verticesSpan,
                            indicesSpan,
                            ref currentVertexCount,
                            ref currentIndexCount,
                            penSolidColor,
                            penBrushIdx,
                            localAffineThickness,
                            closeLineStart,
                            closeLineEnd,
                            transform,
                            cmd.IsEdgeAliased);
                    }
                    else
                    {
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
                    }

                    if (hasFirstSegmentStartDirection)
                    {
                        AppendPathStrokeSegmentJoinTriangles(
                            verticesSpan,
                            indicesSpan,
                            ref currentVertexCount,
                            ref currentIndexCount,
                            cmd.Pen,
                            penSolidColor,
                            penBrushIdx,
                            localAffineThickness,
                            closeLineEnd,
                            closeLineDirection,
                            firstSegmentStartDirection,
                            transform,
                            useAffineStrokeGeometry,
                            cmd.IsEdgeAliased,
                            firstSegmentSmoothJoin);
                    }
                }
                else if (figure.IsClosed &&
                         hasPreviousSegmentEndDirection &&
                         hasFirstSegmentStartDirection &&
                         currentPoint == figure.StartPoint)
                {
                    AppendPathStrokeSegmentJoinTriangles(
                        verticesSpan,
                        indicesSpan,
                        ref currentVertexCount,
                        ref currentIndexCount,
                        cmd.Pen,
                        penSolidColor,
                        penBrushIdx,
                        localAffineThickness,
                        figure.StartPoint,
                        previousSegmentEndDirection,
                        firstSegmentStartDirection,
                        transform,
                        useAffineStrokeGeometry,
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

    internal static bool TryCreateDashedStrokePath(PathGeometry source, Pen pen, out PathGeometry dashedPath)
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

        var sourceFigures = source.Figures;
        for (int figureIndex = 0; figureIndex < sourceFigures.Count; figureIndex++)
        {
            var figure = sourceFigures[figureIndex];
            var patternIndex = pattern.InitialIndex;
            var distanceInPattern = pattern.InitialDistance;
            PathFigure? activeDashFigure = null;
            var activeDashEnd = default(Vector2);
            var currentPoint = figure.StartPoint;
            var figureSegments = figure.Segments;

            for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
            {
                var segment = figureSegments[segmentIndex];
                var segmentStart = currentPoint;
                if (!segment.IsStroked)
                {
                    if (TryGetPathSegmentEndPoint(segment, out var skippedEndPoint))
                    {
                        currentPoint = skippedEndPoint;
                    }

                    patternIndex = pattern.InitialIndex;
                    distanceInPattern = pattern.InitialDistance;
                    activeDashFigure = null;
                    activeDashEnd = default;
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
                            ref distanceInPattern,
                            ref activeDashFigure,
                            ref activeDashEnd);
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
                            for (int dashIndex = 0; dashIndex < quadraticSegments.Length; dashIndex++)
                            {
                                var dashSegment = quadraticSegments[dashIndex];
                                AppendDashedSegment(
                                    dashedPath,
                                    ref activeDashFigure,
                                    ref activeDashEnd,
                                    dashSegment.Start,
                                    dashSegment.Segment);
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
                            for (int dashIndex = 0; dashIndex < cubicSegments.Length; dashIndex++)
                            {
                                var dashSegment = cubicSegments[dashIndex];
                                AppendDashedSegment(
                                    dashedPath,
                                    ref activeDashFigure,
                                    ref activeDashEnd,
                                    dashSegment.Start,
                                    dashSegment.Segment);
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
                            for (int dashIndex = 0; dashIndex < arcSegments.Length; dashIndex++)
                            {
                                var dashSegment = arcSegments[dashIndex];
                                AppendDashedSegment(
                                    dashedPath,
                                    ref activeDashFigure,
                                    ref activeDashEnd,
                                    dashSegment.Start,
                                    dashSegment.Arc);
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
                    ref distanceInPattern,
                    ref activeDashFigure,
                    ref activeDashEnd);
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
        ref float distanceInPattern,
        ref PathFigure? activeDashFigure,
        ref Vector2 activeDashEnd)
    {
        var intervals = pattern.Intervals;
        if (!DashPattern.TryValidateState(intervals, patternIndex, distanceInPattern))
        {
            return;
        }

        var localPatternIndex = patternIndex;
        var localDistanceInPattern = distanceInPattern;
        DashPattern.NormalizeState(intervals, ref localPatternIndex, ref localDistanceInPattern);

        var delta = end - start;
        var length = delta.Length();
        if (length <= StrokeEpsilon)
        {
            return;
        }

        var direction = delta / length;
        var distance = 0.0f;
        while (distance < length - StrokeEpsilon)
        {
            var remainingInElement = intervals[localPatternIndex] - localDistanceInPattern;
            var step = MathF.Min(remainingInElement, length - distance);
            if ((localPatternIndex % 2) == 0 && step > StrokeEpsilon)
            {
                AppendDashedSegment(
                    dashedPath,
                    ref activeDashFigure,
                    ref activeDashEnd,
                    start + direction * distance,
                    new LineSegment(start + direction * (distance + step)));
            }

            DashPattern.Advance(
                intervals,
                ref localPatternIndex,
                ref localDistanceInPattern,
                remainingInElement,
                step);
            distance += step;
        }

        patternIndex = localPatternIndex;
        distanceInPattern = localDistanceInPattern;
    }

    private static void AppendDashedSegment(
        PathGeometry dashedPath,
        ref PathFigure? activeDashFigure,
        ref Vector2 activeDashEnd,
        Vector2 start,
        PathSegment segment)
    {
        if (TryGetPathSegmentEndPoint(segment, out var endPoint) &&
            Vector2.DistanceSquared(start, endPoint) <= StrokeEpsilon * StrokeEpsilon)
        {
            return;
        }

        segment.IsStroked = true;
        if (activeDashFigure != null &&
            Vector2.DistanceSquared(activeDashEnd, start) <= StrokeEpsilon * StrokeEpsilon)
        {
            activeDashFigure.Segments.Add(segment);
            activeDashEnd = endPoint;
            return;
        }

        segment.IsSmoothJoin = false;
        var figure = new PathFigure(start)
        {
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(segment);
        dashedPath.Figures.Add(figure);
        activeDashFigure = figure;
        activeDashEnd = endPoint;
    }

    internal static Pen CreateUndashedPen(Pen pen)
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

        var figureSegments = figure.Segments;
        for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
        {
            var segment = figureSegments[segmentIndex];
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
            var hasSegmentEndDirection = TryGetPathSegmentEndDirection(segment, segmentStart, out _);
            if (!hasSegmentStartDirection &&
                !hasSegmentEndDirection &&
                TryGetPathSegmentEndPoint(segment, out var degenerateEndPoint) &&
                Vector2.DistanceSquared(segmentStart, degenerateEndPoint) <= StrokeEpsilon * StrokeEpsilon)
            {
                currentPoint = degenerateEndPoint;
                continue;
            }

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

            if (hasSegmentEndDirection)
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
        var hasDegenerateStrokeCandidate = false;
        var capCount = 0;

        var figureSegments = figure.Segments;
        for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
        {
            var segment = figureSegments[segmentIndex];
            if (!segment.IsStroked)
            {
                if (pendingEndCap && endLineCap != PenLineCap.Flat)
                {
                    capCount++;
                }
                else if (hasDegenerateStrokeCandidate)
                {
                    if (startLineCap != PenLineCap.Flat)
                    {
                        capCount++;
                    }

                    if (endLineCap != PenLineCap.Flat)
                    {
                        capCount++;
                    }
                }

                pendingEndCap = false;
                hasDegenerateStrokeCandidate = false;
                isFirstSegmentInSubpath = true;
                if (TryGetPathSegmentEndPoint(segment, out var skippedSegmentEndPoint))
                {
                    currentPoint = skippedSegmentEndPoint;
                }

                continue;
            }

            var hasSegmentStartDirection = TryGetPathSegmentStartDirection(segment, currentPoint, out _);
            var hasSegmentEndDirection = TryGetPathSegmentEndDirection(segment, currentPoint, out _);
            if (!hasSegmentStartDirection &&
                !hasSegmentEndDirection &&
                TryGetPathSegmentEndPoint(segment, out var degenerateEndPoint) &&
                Vector2.DistanceSquared(currentPoint, degenerateEndPoint) <= StrokeEpsilon * StrokeEpsilon)
            {
                if (isFirstSegmentInSubpath)
                {
                    hasDegenerateStrokeCandidate = true;
                }

                currentPoint = degenerateEndPoint;
                continue;
            }

            hasDegenerateStrokeCandidate = false;
            if (isFirstSegmentInSubpath &&
                startLineCap != PenLineCap.Flat &&
                hasSegmentStartDirection)
            {
                capCount++;
            }

            pendingEndCap = hasSegmentEndDirection;
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
        else if (hasDegenerateStrokeCandidate)
        {
            if (startLineCap != PenLineCap.Flat)
            {
                capCount++;
            }

            if (endLineCap != PenLineCap.Flat)
            {
                capCount++;
            }
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

    private static void AppendAffineStrokeLineVertices(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        Vector4 penSolidColor,
        float penBrushIdx,
        float localThickness,
        Vector2 localStart,
        Vector2 localEnd,
        Matrix4x4 transform,
        bool isEdgeAliased)
    {
        var delta = localEnd - localStart;
        var length = delta.Length();
        if (!float.IsFinite(length) || length <= StrokeEpsilon)
        {
            return;
        }

        var direction = delta / length;
        var normal = new Vector2(-direction.Y, direction.X) * (localThickness * 0.5f);
        var p0 = Vector2.Transform(localStart + normal, transform);
        var p1 = Vector2.Transform(localEnd + normal, transform);
        var p2 = Vector2.Transform(localEnd - normal, transform);
        var p3 = Vector2.Transform(localStart - normal, transform);

        AppendStrokeQuadrilateralVertices(
            verticesSpan,
            indicesSpan,
            ref currentVertexCount,
            ref currentIndexCount,
            penBrushIdx,
            p0,
            p1,
            p2,
            p3,
            isEdgeAliased);
    }

    private static void AppendAffineStrokeQuadraticVertices(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        float penBrushIdx,
        float localThickness,
        Vector2 start,
        QuadraticBezierSegment segment,
        Matrix4x4 transform,
        bool isEdgeAliased)
    {
        const int segmentCount = 24;
        var previousPoint = start;
        var previousTangent = GetQuadraticTangent(start, segment.ControlPoint, segment.Point, 0f);

        for (var i = 1; i <= segmentCount; i++)
        {
            var t = (float)i / segmentCount;
            var point = BezierSegmentGeometry.EvaluateQuadratic(start, segment.ControlPoint, segment.Point, t);
            var tangent = GetQuadraticTangent(start, segment.ControlPoint, segment.Point, t);
            AppendAffineStrokeCurveSection(
                verticesSpan,
                indicesSpan,
                ref currentVertexCount,
                ref currentIndexCount,
                penBrushIdx,
                localThickness,
                previousPoint,
                point,
                previousTangent,
                tangent,
                transform,
                isEdgeAliased,
                isFirstSection: i == 1,
                isLastSection: i == segmentCount);
            previousPoint = point;
            previousTangent = tangent;
        }
    }

    private static void AppendAffineStrokeCubicVertices(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        float penBrushIdx,
        float localThickness,
        Vector2 start,
        CubicBezierSegment segment,
        Matrix4x4 transform,
        bool isEdgeAliased)
    {
        const int segmentCount = 24;
        var previousPoint = start;
        var previousTangent = GetCubicTangent(start, segment.ControlPoint1, segment.ControlPoint2, segment.Point, 0f);

        for (var i = 1; i <= segmentCount; i++)
        {
            var t = (float)i / segmentCount;
            var point = BezierSegmentGeometry.EvaluateCubic(start, segment.ControlPoint1, segment.ControlPoint2, segment.Point, t);
            var tangent = GetCubicTangent(start, segment.ControlPoint1, segment.ControlPoint2, segment.Point, t);
            AppendAffineStrokeCurveSection(
                verticesSpan,
                indicesSpan,
                ref currentVertexCount,
                ref currentIndexCount,
                penBrushIdx,
                localThickness,
                previousPoint,
                point,
                previousTangent,
                tangent,
                transform,
                isEdgeAliased,
                isFirstSection: i == 1,
                isLastSection: i == segmentCount);
            previousPoint = point;
            previousTangent = tangent;
        }
    }

    private static void AppendAffineStrokeCurveSection(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        float penBrushIdx,
        float localThickness,
        Vector2 localStart,
        Vector2 localEnd,
        Vector2 startTangent,
        Vector2 endTangent,
        Matrix4x4 transform,
        bool isEdgeAliased,
        bool isFirstSection,
        bool isLastSection)
    {
        var chord = localEnd - localStart;
        if (!TryNormalizeStrokeDirection(startTangent, chord, out var startDirection) ||
            !TryNormalizeStrokeDirection(endTangent, chord, out var endDirection))
        {
            return;
        }

        var radius = localThickness * 0.5f;
        var startNormal = new Vector2(-startDirection.Y, startDirection.X) * radius;
        var endNormal = new Vector2(-endDirection.Y, endDirection.X) * radius;
        var p0 = Vector2.Transform(localStart + startNormal, transform);
        var p1 = Vector2.Transform(localEnd + endNormal, transform);
        var p2 = Vector2.Transform(localEnd - endNormal, transform);
        var p3 = Vector2.Transform(localStart - startNormal, transform);
        var shapeType = isFirstSection
            ? isLastSection
                ? QuadrilateralSdfShapeType
                : QuadrilateralStripStartSdfShapeType
            : isLastSection
                ? QuadrilateralStripEndSdfShapeType
                : QuadrilateralStripSdfShapeType;

        AppendStrokeQuadrilateralVertices(
            verticesSpan,
            indicesSpan,
            ref currentVertexCount,
            ref currentIndexCount,
            penBrushIdx,
            p0,
            p1,
            p2,
            p3,
            isEdgeAliased,
            shapeType);
    }

    private static bool TryNormalizeStrokeDirection(
        Vector2 direction,
        Vector2 fallback,
        out Vector2 normalized)
    {
        var length = direction.Length();
        if (!float.IsFinite(length) || length <= StrokeEpsilon)
        {
            direction = fallback;
            length = direction.Length();
        }

        if (!float.IsFinite(length) || length <= StrokeEpsilon)
        {
            normalized = default;
            return false;
        }

        normalized = direction / length;
        return true;
    }

    private static Vector2 GetQuadraticTangent(Vector2 p0, Vector2 p1, Vector2 p2, float t)
    {
        return 2f * ((1f - t) * (p1 - p0) + t * (p2 - p1));
    }

    private static Vector2 GetCubicTangent(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        var inverse = 1f - t;
        return 3f * (
            inverse * inverse * (p1 - p0) +
            2f * inverse * t * (p2 - p1) +
            t * t * (p3 - p2));
    }

    private static void AppendStrokeQuadrilateralVertices(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        float penBrushIdx,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        bool isEdgeAliased,
        float shapeType = QuadrilateralSdfShapeType)
    {
        var edge0 = p1 - p0;
        var edge1 = p2 - p0;
        var area = edge0.X * edge1.Y - edge0.Y * edge1.X;
        if (!float.IsFinite(area) || MathF.Abs(area) <= StrokeEpsilon)
        {
            return;
        }

        const float padding = 1.5f;
        var min = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3)) - new Vector2(padding);
        var max = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3)) + new Vector2(padding);
        if (!IsFinite(min) || !IsFinite(max))
        {
            return;
        }

        var firstPoints = new Vector4(p0.X, p0.Y, p1.X, p1.Y);
        shapeType = EncodeShapeType(isEdgeAliased, shapeType);
        var index = (uint)currentVertexCount;

        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(min.X, min.Y), firstPoints, new Vector2(min.X, min.Y), penBrushIdx, p2, p3.X, p3.Y, shapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(max.X, min.Y), firstPoints, new Vector2(max.X, min.Y), penBrushIdx, p2, p3.X, p3.Y, shapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(max.X, max.Y), firstPoints, new Vector2(max.X, max.Y), penBrushIdx, p2, p3.X, p3.Y, shapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(min.X, max.Y), firstPoints, new Vector2(min.X, max.Y), penBrushIdx, p2, p3.X, p3.Y, shapeType);

        indicesSpan[currentIndexCount++] = index;
        indicesSpan[currentIndexCount++] = index + 1;
        indicesSpan[currentIndexCount++] = index + 2;
        indicesSpan[currentIndexCount++] = index;
        indicesSpan[currentIndexCount++] = index + 2;
        indicesSpan[currentIndexCount++] = index + 3;
    }

    private static bool RequiresAffineStrokeGeometry(Matrix4x4 transform)
    {
        var axisX = new Vector2(transform.M11, transform.M12);
        var axisY = new Vector2(transform.M21, transform.M22);
        var lengthX = axisX.Length();
        var lengthY = axisY.Length();
        if (!float.IsFinite(lengthX) || !float.IsFinite(lengthY) ||
            lengthX <= StrokeEpsilon || lengthY <= StrokeEpsilon)
        {
            return false;
        }

        var scale = MathF.Max(lengthX, lengthY);
        var lengthTolerance = scale * 0.0001f;
        var dotTolerance = lengthX * lengthY * 0.0001f;
        return MathF.Abs(lengthX - lengthY) > lengthTolerance ||
            MathF.Abs(Vector2.Dot(axisX, axisY)) > dotTolerance;
    }

    private static bool AppendAffineStrokeArcVertices(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        float penBrushIdx,
        float localThickness,
        Vector2 segmentStart,
        ArcSegment arc,
        Matrix4x4 transform,
        bool isEdgeAliased)
    {
        if (!ArcSegmentGeometry.TryCreateShaderParameters(
                segmentStart,
                arc,
                Matrix4x4.Identity,
                out var parameters))
        {
            return false;
        }

        var segmentCount = ArcSegmentGeometry.CountFlattenedSegments(
            segmentStart,
            arc,
            AffineStrokeArcMaxAngleRadians);
        if (segmentCount <= 0)
        {
            return false;
        }

        var traversalDirection = MathF.CopySign(1f, parameters.DeltaTheta);
        var previousPoint = segmentStart;
        var previousTangent = EvaluateArcTangent(parameters, parameters.Theta1) * traversalDirection;

        for (var i = 1; i <= segmentCount; i++)
        {
            var t = (float)i / segmentCount;
            var theta = parameters.Theta1 + t * parameters.DeltaTheta;
            var point = i == segmentCount ? arc.Point : EvaluateArcPoint(parameters, theta);
            var tangent = EvaluateArcTangent(parameters, theta) * traversalDirection;
            AppendAffineStrokeCurveSection(
                verticesSpan,
                indicesSpan,
                ref currentVertexCount,
                ref currentIndexCount,
                penBrushIdx,
                localThickness,
                previousPoint,
                point,
                previousTangent,
                tangent,
                transform,
                isEdgeAliased,
                isFirstSection: i == 1,
                isLastSection: i == segmentCount);
            previousPoint = point;
            previousTangent = tangent;
        }

        return true;
    }

    private static Vector2 EvaluateArcPoint(ArcShaderParameters parameters, float theta) =>
        parameters.Center + parameters.AxisX * MathF.Cos(theta) + parameters.AxisY * MathF.Sin(theta);

    private static Vector2 EvaluateArcTangent(ArcShaderParameters parameters, float theta) =>
        -parameters.AxisX * MathF.Sin(theta) + parameters.AxisY * MathF.Cos(theta);

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

    private static void AppendPathStrokeSegmentJoinTriangles(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        Pen pen,
        Vector4 penSolidColor,
        float penBrushIdx,
        float localThickness,
        Vector2 localJoinPoint,
        Vector2 localIncomingDirection,
        Vector2 localOutgoingDirection,
        Matrix4x4 transform,
        bool useAffineStrokeGeometry,
        bool isEdgeAliased,
        bool isSmoothJoin)
    {
        if (useAffineStrokeGeometry)
        {
            AppendAffineStrokeSegmentJoinTriangles(
                verticesSpan,
                indicesSpan,
                ref currentVertexCount,
                ref currentIndexCount,
                pen,
                penBrushIdx,
                localThickness,
                localJoinPoint,
                localIncomingDirection,
                localOutgoingDirection,
                transform,
                isEdgeAliased,
                isSmoothJoin);
            return;
        }

        AppendStrokeSegmentJoinTriangles(
            verticesSpan,
            indicesSpan,
            ref currentVertexCount,
            ref currentIndexCount,
            pen,
            penSolidColor,
            penBrushIdx,
            Vector2.Transform(localJoinPoint, transform),
            TransformDirection(localIncomingDirection, transform),
            TransformDirection(localOutgoingDirection, transform),
            isEdgeAliased,
            isSmoothJoin);
    }

    private static void AppendAffineStrokeSegmentJoinTriangles(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        Pen pen,
        float penBrushIdx,
        float localThickness,
        Vector2 localJoinPoint,
        Vector2 localIncomingDirection,
        Vector2 localOutgoingDirection,
        Matrix4x4 transform,
        bool isEdgeAliased,
        bool isSmoothJoin)
    {
        var localTriangles = StrokeJoinGeometry.CreateDirectionalJoin(
            pen.LineJoin,
            localThickness,
            pen.MiterLimit,
            localJoinPoint,
            localIncomingDirection,
            localOutgoingDirection,
            isSmoothJoin);

        for (var triangleIndex = 0; triangleIndex < localTriangles.Length; triangleIndex++)
        {
            var localTriangle = localTriangles[triangleIndex];
            var transformedTriangle = new StrokeJoinTriangle(
                Vector2.Transform(localTriangle.P0, transform),
                Vector2.Transform(localTriangle.P1, transform),
                Vector2.Transform(localTriangle.P2, transform));
            var edgeMasks = GetStrokeTriangleEdgeMasks(localTriangles, triangleIndex);
            AppendStrokeTriangleVertices(
                verticesSpan,
                indicesSpan,
                ref currentVertexCount,
                ref currentIndexCount,
                penBrushIdx,
                transformedTriangle,
                edgeMasks.Exterior,
                edgeMasks.OwnedInternal,
                isEdgeAliased);
        }
    }

    private static void AppendPathStrokeSegmentCapTriangles(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        Pen pen,
        Vector4 penSolidColor,
        float penBrushIdx,
        float localThickness,
        Vector2 localCenter,
        Vector2 localDirectionAlongPath,
        Matrix4x4 transform,
        bool useAffineStrokeGeometry,
        bool isEdgeAliased,
        bool isStart)
    {
        if (useAffineStrokeGeometry)
        {
            AppendAffineStrokeSegmentCapTriangles(
                verticesSpan,
                indicesSpan,
                ref currentVertexCount,
                ref currentIndexCount,
                pen,
                penBrushIdx,
                localThickness,
                localCenter,
                localDirectionAlongPath,
                transform,
                isEdgeAliased,
                isStart);
            return;
        }

        AppendStrokeSegmentCapTriangles(
            verticesSpan,
            indicesSpan,
            ref currentVertexCount,
            ref currentIndexCount,
            pen,
            penSolidColor,
            penBrushIdx,
            Vector2.Transform(localCenter, transform),
            TransformDirection(localDirectionAlongPath, transform),
            isEdgeAliased,
            isStart);
    }

    private static void AppendAffineStrokeSegmentCapTriangles(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        Pen pen,
        float penBrushIdx,
        float localThickness,
        Vector2 localCenter,
        Vector2 localDirectionAlongPath,
        Matrix4x4 transform,
        bool isEdgeAliased,
        bool isStart)
    {
        var directionLength = localDirectionAlongPath.Length();
        if (!float.IsFinite(directionLength) || directionLength <= StrokeEpsilon)
        {
            return;
        }

        var localTriangles = StrokeCapGeometry.CreateDirectionalCap(
            isStart ? pen.StartLineCap : pen.EndLineCap,
            localThickness,
            localCenter,
            localDirectionAlongPath,
            isStart);
        var normalizedLocalDirection = localDirectionAlongPath / directionLength;

        for (var triangleIndex = 0; triangleIndex < localTriangles.Length; triangleIndex++)
        {
            var localTriangle = localTriangles[triangleIndex];
            var transformedTriangle = new StrokeJoinTriangle(
                Vector2.Transform(localTriangle.P0, transform),
                Vector2.Transform(localTriangle.P1, transform),
                Vector2.Transform(localTriangle.P2, transform));
            var edgeMasks = GetStrokeCapTriangleEdgeMasks(
                localTriangles,
                triangleIndex,
                localCenter,
                normalizedLocalDirection);
            AppendStrokeTriangleVertices(
                verticesSpan,
                indicesSpan,
                ref currentVertexCount,
                ref currentIndexCount,
                penBrushIdx,
                transformedTriangle,
                edgeMasks.Exterior,
                edgeMasks.OwnedInternal,
                isEdgeAliased);
        }
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

        for (var triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex++)
        {
            var edgeMasks = GetStrokeTriangleEdgeMasks(triangles, triangleIndex);
            AppendStrokeTriangleVertices(
                verticesSpan,
                indicesSpan,
                ref currentVertexCount,
                ref currentIndexCount,
                penBrushIdx,
                triangles[triangleIndex],
                edgeMasks.Exterior,
                edgeMasks.OwnedInternal,
                isEdgeAliased);
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
        var normalizedDirection = Vector2.Normalize(directionAlongPath);

        for (var triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex++)
        {
            var edgeMasks = GetStrokeCapTriangleEdgeMasks(
                triangles,
                triangleIndex,
                center,
                normalizedDirection);
            AppendStrokeTriangleVertices(
                verticesSpan,
                indicesSpan,
                ref currentVertexCount,
                ref currentIndexCount,
                penBrushIdx,
                triangles[triangleIndex],
                edgeMasks.Exterior,
                edgeMasks.OwnedInternal,
                isEdgeAliased);
        }
    }

    private static (uint Exterior, uint OwnedInternal) GetStrokeCapTriangleEdgeMasks(
        StrokeJoinTriangle[] triangles,
        int triangleIndex,
        Vector2 capCenter,
        Vector2 directionAlongPath)
    {
        return GetStrokeTriangleEdgeMasks(
            triangles,
            triangleIndex,
            hasCapBodyInterface: true,
            capCenter: capCenter,
            capDirection: directionAlongPath);
    }

    private static bool IsPointOnStrokeCapBodyInterface(
        Vector2 point,
        Vector2 capCenter,
        Vector2 directionAlongPath)
    {
        var offset = point - capCenter;
        var tolerance = StrokeEpsilon * MathF.Max(1f, offset.Length());
        return MathF.Abs(Vector2.Dot(offset, directionAlongPath)) <= tolerance;
    }

    private static (uint Exterior, uint OwnedInternal) GetStrokeTriangleEdgeMasks(
        StrokeJoinTriangle[] triangles,
        int triangleIndex,
        bool hasCapBodyInterface = false,
        Vector2 capCenter = default,
        Vector2 capDirection = default)
    {
        var triangle = triangles[triangleIndex];
        uint exteriorMask = 0;
        uint ownedInternalMask = 0;
        ClassifyStrokeTriangleEdge(triangle.P0, triangle.P1, 1u);
        ClassifyStrokeTriangleEdge(triangle.P1, triangle.P2, 2u);
        ClassifyStrokeTriangleEdge(triangle.P2, triangle.P0, 4u);
        return (exteriorMask, ownedInternalMask);

        void ClassifyStrokeTriangleEdge(Vector2 edgeStart, Vector2 edgeEnd, uint bit)
        {
            if (hasCapBodyInterface &&
                IsPointOnStrokeCapBodyInterface(edgeStart, capCenter, capDirection) &&
                IsPointOnStrokeCapBodyInterface(edgeEnd, capCenter, capDirection))
            {
                return;
            }

            var sharedTriangleIndex = FindSharedStrokeTriangleEdge(
                triangles,
                triangleIndex,
                edgeStart,
                edgeEnd);
            if (sharedTriangleIndex < 0)
            {
                exteriorMask |= bit;
            }
            else if (triangleIndex < sharedTriangleIndex)
            {
                ownedInternalMask |= bit;
            }
        }
    }

    private static int FindSharedStrokeTriangleEdge(
        StrokeJoinTriangle[] triangles,
        int triangleIndex,
        Vector2 edgeStart,
        Vector2 edgeEnd)
    {
        for (var index = 0; index < triangles.Length; index++)
        {
            if (index == triangleIndex)
            {
                continue;
            }

            var candidate = triangles[index];
            if (StrokeTriangleEdgesMatch(edgeStart, edgeEnd, candidate.P0, candidate.P1) ||
                StrokeTriangleEdgesMatch(edgeStart, edgeEnd, candidate.P1, candidate.P2) ||
                StrokeTriangleEdgesMatch(edgeStart, edgeEnd, candidate.P2, candidate.P0))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool StrokeTriangleEdgesMatch(
        Vector2 firstStart,
        Vector2 firstEnd,
        Vector2 secondStart,
        Vector2 secondEnd)
    {
        return (StrokeTrianglePointsMatch(firstStart, secondStart) &&
                StrokeTrianglePointsMatch(firstEnd, secondEnd)) ||
               (StrokeTrianglePointsMatch(firstStart, secondEnd) &&
                StrokeTrianglePointsMatch(firstEnd, secondStart));
    }

    private static bool StrokeTrianglePointsMatch(Vector2 first, Vector2 second) =>
        Vector2.DistanceSquared(first, second) <= StrokeEpsilon * StrokeEpsilon;

    private static void AppendStrokeTriangleVertices(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        float penBrushIdx,
        StrokeJoinTriangle triangle,
        uint exteriorEdgeMask,
        uint ownedInternalEdgeMask,
        bool isEdgeAliased)
    {
        var edge0 = triangle.P1 - triangle.P0;
        var edge1 = triangle.P2 - triangle.P0;
        var area = edge0.X * edge1.Y - edge0.Y * edge1.X;
        if (!float.IsFinite(area) || MathF.Abs(area) <= StrokeEpsilon)
        {
            return;
        }

        const float padding = 1.5f;
        var min = Vector2.Min(triangle.P0, Vector2.Min(triangle.P1, triangle.P2)) - new Vector2(padding);
        var max = Vector2.Max(triangle.P0, Vector2.Max(triangle.P1, triangle.P2)) + new Vector2(padding);
        if (!IsFinite(min) || !IsFinite(max))
        {
            return;
        }

        var trianglePoints = new Vector4(triangle.P0.X, triangle.P0.Y, triangle.P1.X, triangle.P1.Y);
        var shapeType = EncodeShapeType(isEdgeAliased, TriangleSdfShapeType);
        var index = (uint)currentVertexCount;

        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(min.X, min.Y), trianglePoints, new Vector2(min.X, min.Y), penBrushIdx, triangle.P2, exteriorEdgeMask, ownedInternalEdgeMask, shapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(max.X, min.Y), trianglePoints, new Vector2(max.X, min.Y), penBrushIdx, triangle.P2, exteriorEdgeMask, ownedInternalEdgeMask, shapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(max.X, max.Y), trianglePoints, new Vector2(max.X, max.Y), penBrushIdx, triangle.P2, exteriorEdgeMask, ownedInternalEdgeMask, shapeType);
        verticesSpan[currentVertexCount++] = new VectorVertex(
            new Vector2(min.X, max.Y), trianglePoints, new Vector2(min.X, max.Y), penBrushIdx, triangle.P2, exteriorEdgeMask, ownedInternalEdgeMask, shapeType);

        indicesSpan[currentIndexCount++] = index;
        indicesSpan[currentIndexCount++] = index + 1;
        indicesSpan[currentIndexCount++] = index + 2;
        indicesSpan[currentIndexCount++] = index;
        indicesSpan[currentIndexCount++] = index + 2;
        indicesSpan[currentIndexCount++] = index + 3;
    }

    private static void AppendDegenerateStrokeCapVertices(
        Span<VectorVertex> verticesSpan,
        Span<uint> indicesSpan,
        ref int currentVertexCount,
        ref int currentIndexCount,
        Pen pen,
        Vector4 penSolidColor,
        float penBrushIdx,
        float thickness,
        Vector2 localCenter,
        Matrix4x4 transform,
        bool isEdgeAliased)
    {
        if (pen.StartLineCap == PenLineCap.Flat && pen.EndLineCap == PenLineCap.Flat)
        {
            return;
        }

        if (pen.StartLineCap == pen.EndLineCap && pen.StartLineCap is PenLineCap.Round or PenLineCap.Square)
        {
            var strokeScale = TransformMetrics.GetStrokeScale(transform);
            var localThickness = float.IsFinite(strokeScale) && strokeScale > StrokeEpsilon
                ? thickness / strokeScale
                : thickness;
            var radius = localThickness * 0.5f;
            var padding = pen.StartLineCap == PenLineCap.Round && strokeScale > StrokeEpsilon
                ? 1.5f / strokeScale
                : 0f;
            var extent = radius + padding;
            var local0 = localCenter + new Vector2(-extent, -extent);
            var local1 = localCenter + new Vector2(extent, -extent);
            var local2 = localCenter + new Vector2(extent, extent);
            var local3 = localCenter + new Vector2(-extent, extent);
            var p0 = Vector2.Transform(local0, transform);
            var p1 = Vector2.Transform(local1, transform);
            var p2 = Vector2.Transform(local2, transform);
            var p3 = Vector2.Transform(local3, transform);

            var shapeType = EncodeShapeType(
                isEdgeAliased,
                pen.StartLineCap == PenLineCap.Round ? 1f : 7f);
            var shapeSize = new Vector2(radius * 2f, radius * 2f);
            var shapeColor = pen.Brush is SolidColorBrush
                ? penSolidColor
                : new Vector4(localCenter.X, localCenter.Y, 0f, 0f);
            var texCoord0 = pen.StartLineCap == PenLineCap.Round ? new Vector2(-extent, -extent) : local0;
            var texCoord1 = pen.StartLineCap == PenLineCap.Round ? new Vector2(extent, -extent) : local1;
            var texCoord2 = pen.StartLineCap == PenLineCap.Round ? new Vector2(extent, extent) : local2;
            var texCoord3 = pen.StartLineCap == PenLineCap.Round ? new Vector2(-extent, extent) : local3;
            var index = (uint)currentVertexCount;

            verticesSpan[currentVertexCount++] = new VectorVertex(p0, shapeColor, texCoord0, penBrushIdx, shapeSize: shapeSize, shapeType: shapeType);
            verticesSpan[currentVertexCount++] = new VectorVertex(p1, shapeColor, texCoord1, penBrushIdx, shapeSize: shapeSize, shapeType: shapeType);
            verticesSpan[currentVertexCount++] = new VectorVertex(p2, shapeColor, texCoord2, penBrushIdx, shapeSize: shapeSize, shapeType: shapeType);
            verticesSpan[currentVertexCount++] = new VectorVertex(p3, shapeColor, texCoord3, penBrushIdx, shapeSize: shapeSize, shapeType: shapeType);

            indicesSpan[currentIndexCount++] = index;
            indicesSpan[currentIndexCount++] = index + 1;
            indicesSpan[currentIndexCount++] = index + 2;
            indicesSpan[currentIndexCount++] = index;
            indicesSpan[currentIndexCount++] = index + 2;
            indicesSpan[currentIndexCount++] = index + 3;
            return;
        }

        var centerPoint = Vector2.Transform(localCenter, transform);
        var direction = TransformDirection(Vector2.UnitX, transform);
        if (direction.LengthSquared() <= StrokeEpsilon * StrokeEpsilon)
        {
            direction = Vector2.UnitX;
        }

        AppendStrokeSegmentCapTriangles(
            verticesSpan,
            indicesSpan,
            ref currentVertexCount,
            ref currentIndexCount,
            pen,
            penSolidColor,
            penBrushIdx,
            centerPoint,
            direction,
            isEdgeAliased,
            isStart: true);
        AppendStrokeSegmentCapTriangles(
            verticesSpan,
            indicesSpan,
            ref currentVertexCount,
            ref currentIndexCount,
            pen,
            penSolidColor,
            penBrushIdx,
            centerPoint,
            direction,
            isEdgeAliased,
            isStart: false);
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
            var path = cmd.GeometryCache?.StrokePath ??
                RenderCommandGeometryCache.CreateLinePath(cmd.Position, cmd.Position2);

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

        const int N = 24;
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

        const int N = 24;
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

    private void CompileVertexMeshCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Brush is null || cmd.VertexMesh is not { } mesh)
        {
            return;
        }

        var triangleCount = mesh.GetTriangleCount();
        if (triangleCount == 0)
        {
            return;
        }

        SwitchBatch(BatchType.Vector);
        var positions = mesh.PositionArray;
        var textureCoordinates = mesh.TextureCoordinateArray;
        var colors = mesh.ColorArray;
        var brushIndex = RegisterBrush(cmd.Brush);
        var shapeType = EncodeShapeType(cmd, VertexMeshShapeType);
        var startVertex = _vectorVerticesList.Count;
        var originalVertexCount = _vectorVerticesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, checked(originalVertexCount + positions.Length));
        var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, positions.Length);

        for (var index = 0; index < positions.Length; index++)
        {
            var localPosition = positions[index];
            var color = colors.Length > 0 ? colors[index] : Vector4.One;
            color.X *= color.W;
            color.Y *= color.W;
            color.Z *= color.W;
            vertices[index] = new VectorVertex(
                Vector2.Transform(localPosition, transform),
                color,
                textureCoordinates.Length > 0 ? textureCoordinates[index] : localPosition,
                brushIndex,
                default,
                (float)cmd.VertexColorBlendMode,
                0f,
                shapeType);
        }

        var originalIndexCount = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorIndicesList, checked(originalIndexCount + triangleCount * 3));
        var indices = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, triangleCount * 3);
        var baseVertex = checked((uint)startVertex);
        var writtenIndexCount = 0;
        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            mesh.GetTriangle(triangle, out var index0, out var index1, out var index2);
            if ((uint)index0 >= (uint)positions.Length ||
                (uint)index1 >= (uint)positions.Length ||
                (uint)index2 >= (uint)positions.Length)
            {
                continue;
            }

            var offset = writtenIndexCount;
            indices[offset] = checked(baseVertex + (uint)index0);
            indices[offset + 1] = checked(baseVertex + (uint)index1);
            indices[offset + 2] = checked(baseVertex + (uint)index2);
            writtenIndexCount += 3;
        }

        CollectionsMarshal.SetCount(_vectorIndicesList, originalIndexCount + writtenIndexCount);

        if (_activeClipRect.HasValue)
        {
            for (var index = 0; index < vertices.Length; index++)
            {
                var vertex = vertices[index];
                vertex.Position = ClampToClip(vertex.Position);
                vertices[index] = vertex;
            }
        }
    }

    private void CompilePointBatchCommand(RenderCommand cmd, Matrix4x4 transform)
    {
        if (cmd.Brush is null || cmd.PolylinePoints is not { Length: > 0 } points)
        {
            return;
        }

        SwitchBatch(BatchType.Vector);
        var isHairline = cmd.RadiusX <= 0f;
        var radius = isHairline ? 0.5f : cmd.RadiusX;
        var diameter = radius * 2f;
        var shapeSize = new Vector2(diameter, diameter);
        var padding = cmd.IsEdgeAliased ? 0f : 1.5f;
        var round = cmd.IntParam != 0;
        var shapeType = EncodeShapeType(
            cmd,
            isHairline
                ? round ? RoundPointHairlineShapeType : SquarePointHairlineShapeType
                : round ? 1f : 0f);
        var brushIndex = RegisterBrush(cmd.Brush);
        var isSolid = cmd.Brush is SolidColorBrush;
        var solidColor = isSolid ? ((SolidColorBrush)cmd.Brush).Color : default;
        var originalVertexCount = _vectorVerticesList.Count;
        var originalIndexCount = _vectorIndicesList.Count;
        CollectionsMarshal.SetCount(_vectorVerticesList, checked(originalVertexCount + points.Length * 4));
        CollectionsMarshal.SetCount(_vectorIndicesList, checked(originalIndexCount + points.Length * 6));
        var vertices = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, points.Length * 4);
        var indices = CollectionsMarshal.AsSpan(_vectorIndicesList).Slice(originalIndexCount, points.Length * 6);

        for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
        {
            var center = points[pointIndex];
            var extent = radius + padding;
            var color = isSolid ? solidColor : new Vector4(center.X, center.Y, 0f, 0f);
            var vertexOffset = pointIndex * 4;
            Vector2 position0;
            Vector2 position1;
            Vector2 position2;
            Vector2 position3;
            if (isHairline)
            {
                var transformedCenter = Vector2.Transform(center, transform);
                position0 = transformedCenter;
                position1 = transformedCenter;
                position2 = transformedCenter;
                position3 = transformedCenter;
            }
            else
            {
                position0 = Vector2.Transform(center + new Vector2(-extent, -extent), transform);
                position1 = Vector2.Transform(center + new Vector2(extent, -extent), transform);
                position2 = Vector2.Transform(center + new Vector2(extent, extent), transform);
                position3 = Vector2.Transform(center + new Vector2(-extent, extent), transform);
            }

            vertices[vertexOffset] = new VectorVertex(
                position0,
                color,
                new Vector2(-extent, -extent),
                brushIndex,
                shapeSize,
                0f,
                0f,
                shapeType);
            vertices[vertexOffset + 1] = new VectorVertex(
                position1,
                color,
                new Vector2(extent, -extent),
                brushIndex,
                shapeSize,
                0f,
                0f,
                shapeType);
            vertices[vertexOffset + 2] = new VectorVertex(
                position2,
                color,
                new Vector2(extent, extent),
                brushIndex,
                shapeSize,
                0f,
                0f,
                shapeType);
            vertices[vertexOffset + 3] = new VectorVertex(
                position3,
                color,
                new Vector2(-extent, extent),
                brushIndex,
                shapeSize,
                0f,
                0f,
                shapeType);

            var baseVertex = checked((uint)(originalVertexCount + vertexOffset));
            var indexOffset = pointIndex * 6;
            indices[indexOffset] = baseVertex;
            indices[indexOffset + 1] = baseVertex + 1;
            indices[indexOffset + 2] = baseVertex + 2;
            indices[indexOffset + 3] = baseVertex;
            indices[indexOffset + 4] = baseVertex + 2;
            indices[indexOffset + 5] = baseVertex + 3;
        }

        if (_activeClipRect.HasValue)
        {
            for (var index = 0; index < vertices.Length; index++)
            {
                var vertex = vertices[index];
                vertex.Position = ClampToClip(vertex.Position);
                vertices[index] = vertex;
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
        PerlinNoiseBrush? perlinNoiseBrush = null;
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
            if (conical.OutsideColor is { } outsideColor)
            {
                gpuBrush.SpreadMethod |= 0x80000000u;
                gpuBrush.Color0 = outsideColor;
            }
        }
        else if (brush is SweepGradientBrush sweep)
        {
            gpuBrush.Type = 6;
            gpuBrush.Center = sweep.Center;
            gpuBrush.StartPoint = new Vector2(sweep.StartAngle, sweep.EndAngle);
            SetBrushCoordinateTransform(ref gpuBrush, sweep.CoordinateTransform);
            gpuBrush.SpreadMethod = (uint)sweep.SpreadMethod;
            gpuBrush.ColorInterpolationMode = (uint)sweep.ColorInterpolationMode;
            ApplyGradientStops(ref gpuBrush, sweep.Stops);
        }
        else if (brush is PerlinNoiseBrush perlin)
        {
            perlinNoiseBrush = perlin;
            gpuBrush.Type = 7;
            gpuBrush.StartPoint = ResolvePerlinNoiseFrequency(perlin.BaseFrequency, perlin.TileSize);
            gpuBrush.EndPoint = ResolvePerlinNoiseStitchData(gpuBrush.StartPoint, perlin.TileSize);
            gpuBrush.Center = perlin.TileSize;
            gpuBrush.Radius = NormalizePerlinNoiseSeed(perlin.Seed);
            gpuBrush.StopCount = (uint)Math.Clamp(perlin.NumOctaves, 0, 255);
            gpuBrush.SpreadMethod = perlin.IsTurbulence ? 1u : 0u;
            gpuBrush.ColorInterpolationMode = 1u;
            SetBrushCoordinateTransform(ref gpuBrush, perlin.CoordinateTransform);
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

        if (perlinNoiseBrush != null && !ApplyPerlinNoiseTable(ref gpuBrush, perlinNoiseBrush.Seed))
        {
            gpuBrush.ColorInterpolationMode = 0u;
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
        return brushType == 1 || brushType == 2 || brushType == 5 || brushType == 6;
    }

    private static void SetBrushCoordinateTransform(ref GpuBrush gpuBrush, Matrix4x4 transform)
    {
        gpuBrush.CoordinateTransform0 = new Vector4(transform.M11, transform.M21, transform.M41, 0f);
        gpuBrush.CoordinateTransform1 = new Vector4(transform.M12, transform.M22, transform.M42, 0f);
    }

    private bool ApplyPerlinNoiseTable(ref GpuBrush gpuBrush, float seed)
    {
        if (MaxGradientStops - _activeGradientStops.Count < PerlinNoiseTableEntryCount)
        {
            return false;
        }

        var normalizedSeed = NormalizePerlinNoiseSeed(seed);
        if (!_perlinNoiseTableCache.TryGetValue(normalizedSeed, out var table))
        {
            if (_perlinNoiseTableCache.Count >= MaxCachedPerlinNoiseTables)
            {
                _perlinNoiseTableCache.Clear();
            }

            table = CreatePerlinNoiseTable(normalizedSeed);
            _perlinNoiseTableCache.Add(normalizedSeed, table);
        }

        gpuBrush.StopOffset = (uint)_activeGradientStops.Count;
        _activeGradientStops.AddRange(table);
        return true;
    }

    private static GpuGradientStop[] CreatePerlinNoiseTable(int seed)
    {
        const int blockSize = 256;
        var latticeSelector = new byte[blockSize];
        var noise = new ushort[4][];
        for (var channel = 0; channel < noise.Length; channel++)
        {
            noise[channel] = new ushort[blockSize * 2];
            for (var i = 0; i < blockSize; i++)
            {
                latticeSelector[i] = (byte)i;
                noise[channel][i * 2] = (ushort)(NextPerlinNoiseRandom(ref seed) % (2 * blockSize));
                noise[channel][i * 2 + 1] = (ushort)(NextPerlinNoiseRandom(ref seed) % (2 * blockSize));
            }
        }

        for (var i = blockSize - 1; i > 0; i--)
        {
            var selected = NextPerlinNoiseRandom(ref seed) % blockSize;
            (latticeSelector[i], latticeSelector[selected]) = (latticeSelector[selected], latticeSelector[i]);
        }

        for (var channel = 0; channel < noise.Length; channel++)
        {
            var source = (ushort[])noise[channel].Clone();
            for (var i = 0; i < blockSize; i++)
            {
                var sourceIndex = latticeSelector[i] * 2;
                noise[channel][i * 2] = source[sourceIndex];
                noise[channel][i * 2 + 1] = source[sourceIndex + 1];
            }
        }

        var table = new GpuGradientStop[PerlinNoiseTableEntryCount];
        for (var i = 0; i < blockSize; i++)
        {
            var gradient0 = CreatePerlinNoiseGradient(noise[0][i * 2], noise[0][i * 2 + 1]);
            var gradient1 = CreatePerlinNoiseGradient(noise[1][i * 2], noise[1][i * 2 + 1]);
            var gradient2 = CreatePerlinNoiseGradient(noise[2][i * 2], noise[2][i * 2 + 1]);
            var gradient3 = CreatePerlinNoiseGradient(noise[3][i * 2], noise[3][i * 2 + 1]);
            table[i * 2] = new GpuGradientStop
            {
                Color = new Vector4(gradient0.X, gradient0.Y, gradient1.X, gradient1.Y),
                Offset = latticeSelector[i]
            };
            table[i * 2 + 1] = new GpuGradientStop
            {
                Color = new Vector4(gradient2.X, gradient2.Y, gradient3.X, gradient3.Y)
            };
        }

        return table;
    }

    private static Vector2 CreatePerlinNoiseGradient(ushort x, ushort y)
    {
        var gradient = new Vector2(
            (x - 256f) / 256f,
            (y - 256f) / 256f);
        var length = gradient.Length();
        if (length > float.Epsilon)
        {
            gradient /= length;
        }

        return new Vector2(
            QuantizePerlinNoiseGradient(gradient.X),
            QuantizePerlinNoiseGradient(gradient.Y));
    }

    private static float QuantizePerlinNoiseGradient(float value)
    {
        var encoded = Math.Clamp(
            (int)MathF.Floor((value + 1f) * 32767.5f + 0.5f),
            0,
            ushort.MaxValue);
        return encoded * (2f / ushort.MaxValue) - 1f;
    }

    private static int NextPerlinNoiseRandom(ref int seed)
    {
        const int amplitude = 16807;
        const int quotient = 127773;
        const int remainder = 2836;
        const int maximum = int.MaxValue;
        var result = amplitude * (seed % quotient) - remainder * (seed / quotient);
        if (result <= 0)
        {
            result += maximum;
        }

        seed = result;
        return result;
    }

    private static int NormalizePerlinNoiseSeed(float value)
    {
        const long maximumSeed = int.MaxValue - 1L;
        var truncated = Math.Truncate((double)value);
        var seed = truncated >= int.MaxValue
            ? (long)int.MaxValue
            : truncated <= int.MinValue
                ? (long)int.MinValue
                : (long)truncated;
        if (seed <= 0)
        {
            seed = -(seed % maximumSeed) + 1;
        }
        if (seed > maximumSeed)
        {
            seed = maximumSeed;
        }

        return (int)seed;
    }

    private static Vector2 ResolvePerlinNoiseFrequency(Vector2 frequency, Vector2 tileSize)
    {
        if (tileSize.X <= 0f || tileSize.Y <= 0f)
        {
            return frequency;
        }

        return new Vector2(
            ResolvePerlinNoiseFrequency(frequency.X, tileSize.X),
            ResolvePerlinNoiseFrequency(frequency.Y, tileSize.Y));
    }

    private static float ResolvePerlinNoiseFrequency(float frequency, float tileSize)
    {
        if (frequency == 0f)
        {
            return 0f;
        }

        var low = MathF.Floor(tileSize * frequency) / tileSize;
        var high = MathF.Ceiling(tileSize * frequency) / tileSize;
        return frequency / low < high / frequency ? low : high;
    }

    private static Vector2 ResolvePerlinNoiseStitchData(Vector2 frequency, Vector2 tileSize)
    {
        if (tileSize.X <= 0f || tileSize.Y <= 0f)
        {
            return Vector2.Zero;
        }

        return new Vector2(
            MathF.Floor(tileSize.X * frequency.X + 0.5f),
            MathF.Floor(tileSize.Y * frequency.Y + 0.5f));
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
            float maxWidth = cmd.Rect.Width > 0f && float.IsFinite(cmd.Rect.Width)
                ? cmd.Rect.Width
                : 10000f;
            var key = (cmd.Text, font, cmd.FontSize, maxWidth, TextAlignment.Left);
            if (!_layoutCache.TryGetValue(key, out layout))
            {
                layout = new TextLayout(cmd.Text, font, cmd.FontSize, maxWidth, TextAlignment.Left, null);
                _layoutCache[key] = layout;
            }
        }

        if (layout == null) return;

        float bIdx = RegisterBrush(cmd.Brush);
        var brush = cmd.Brush as SolidColorBrush;
        var color = brush?.Color ?? new Vector4(1f, 1f, 1f, 1f);
        color.W *= (brush?.Opacity ?? 1f) * _activeOpacity;

        var layoutGlyphs = layout.Glyphs;
        var layoutGlyphCount = layoutGlyphs.Count;
        EnsureTextVertexCapacity(layoutGlyphCount * (cmd.IsBold ? 2 : 1));

        var staticZoom = ActiveCompilationContext?.StaticZoom ?? 1f;
        var (dpiScale, rasterFontSize, atlasToLogicalScale) = ResolveTextRasterization(
            cmd.FontSize,
            activeTransform,
            _currentDpiScale,
            staticZoom);
        if (cmd.UseLogicalGlyphAtlasResolution)
        {
            var logicalTargetSize = cmd.FontSize *
                TransformMetrics.GetStrokeScale(activeTransform) * staticZoom;
            rasterFontSize = QuantizeGlyphRasterSize(logicalTargetSize);
            atlasToLogicalScale = cmd.FontSize / MathF.Max(rasterFontSize, 0.0001f);
        }
        var fontScaleX = cmd.HasFontTransform && float.IsFinite(cmd.FontTransform.X)
            ? cmd.FontTransform.X
            : 1f;
        var fontSkewX = cmd.HasFontTransform && float.IsFinite(cmd.FontTransform.Y)
            ? cmd.FontTransform.Y
            : 0f;
        var glyphItalicSkew = (cmd.IsItalic ? 0.22f : 0f) - fontSkewX;
        var transformScale = TransformMetrics.GetStrokeScale(activeTransform);
        var atlasUpscale = atlasToLogicalScale
            * transformScale
            * staticZoom
            * MathF.Max(1f, MathF.Abs(fontScaleX));
        var textPathCoverageGamma = MathF.Abs(fontSkewX) > 0.0001f || fontScaleX < 0f
            ? TransformedTextPathCoverageGamma
            : GetTextPathCoverageGamma(
                cmd.FontSize,
                activeTransform,
                transformScale,
                dpiScale);
        bool isRotated = MathF.Abs(activeTransform.M12) > 0.0001f ||
                         MathF.Abs(activeTransform.M21) > 0.0001f ||
                         activeTransform.M11 < 0.0f ||
                         activeTransform.M22 < 0.0f ||
                         MathF.Abs(fontSkewX) > 0.0001f ||
                         fontScaleX < 0f;
        var basisX = new Vector2(activeTransform.M11, activeTransform.M12);
        var basisY = new Vector2(activeTransform.M21, activeTransform.M22);
        int passCount = cmd.IsBold ? 2 : 1;
        float boldOffset = cmd.FontSize * 0.035f;

        for (int glyphIndex = 0; glyphIndex < layoutGlyphCount; glyphIndex++)
        {
            var runGlyph = layoutGlyphs[glyphIndex];
            var glyphFont = runGlyph.Font ?? font;
            ushort glyphIdx = runGlyph.GlyphIndex;
            var colorLayers = glyphFont.HasColorGlyphs
                ? glyphFont.GetColorLayers(glyphIdx)
                : null;

            if (colorLayers != null && colorLayers.Count > 0)
            {
                for (int layerIndex = 0; layerIndex < colorLayers.Count; layerIndex++)
                {
                    var layer = colorLayers[layerIndex];
                    var layerOutline = layer.Geometry ?? glyphFont.GetGlyphOutline(layer.GlyphId);
                    if (layerOutline == null) continue;

                    var layerPosition = runGlyph.Position + cmd.Position;
                    var layerScale = cmd.FontSize / glyphFont.UnitsPerEm;

                    var transformedOutline = CreatePositionedGlyphOutline(
                        layerOutline,
                        layerScale,
                        layerPosition,
                        italicSkew: glyphItalicSkew,
                        scaleX: fontScaleX,
                        usesSvgCoordinates: layer.UsesSvgCoordinates);

                    var pathCmd = new RenderCommand
                    {
                        Type = RenderCommandType.DrawPath,
                        Path = transformedOutline,
                        Brush = CreatePositionedColorLayerBrush(layer, layerScale, layerPosition),
                        IsEdgeAliased = cmd.TextRenderingMode == TextRenderingMode.Aliased,
                        PathSampleGrid = cmd.TextRenderingMode == TextRenderingMode.Aliased
                            ? PathAtlas.StandardCoverageSampleGrid
                            : PathAtlas.HighPrecisionCoverageSampleGrid,
                        PathCoverageGamma = textPathCoverageGamma
                    };
                    CompilePathCommand(pathCmd, activeTransform);
                }

                continue;
            }

            float baseCursorX = runGlyph.Position.X - runGlyph.Glyph.BearX;
            float baseCursorY = runGlyph.Position.Y - runGlyph.Glyph.BearY;

            if (VectorEngine == VectorRenderingEngine.Wavefront)
            {
                var glyphBaselinePosition = new Vector2(
                    baseCursorX + cmd.Position.X,
                    baseCursorY + cmd.Position.Y);
                var glyphTransform = Matrix4x4.CreateTranslation(
                    glyphBaselinePosition.X,
                    glyphBaselinePosition.Y,
                    0f) * activeTransform;
                (_wavefrontEngine ??= new WavefrontVectorEngine(_context)).DrawGlyph(
                    glyphFont,
                    glyphIdx,
                    cmd.FontSize,
                    glyphTransform,
                    cmd.Brush ?? new SolidColorBrush(Vector4.One));
                continue;
            }

            var hasBitmapGlyph = glyphFont.HasBitmapGlyphs &&
                glyphFont.TryGetBitmapGlyph(glyphIdx, rasterFontSize, out _);

            if (!hasBitmapGlyph &&
                (cmd.UseVectorGlyphRendering || glyphFont.HasCffOutlines || atlasUpscale > 1.0001f))
            {
                CompileVectorGlyphFallback(
                    glyphFont,
                    glyphIdx,
                    cmd,
                    runGlyph.Position + cmd.Position,
                    glyphItalicSkew,
                    textPathCoverageGamma,
                    activeTransform,
                    fontScaleX);

                continue;
            }

            SwitchBatch(BatchType.Text);
            Vector2 transPos = Vector2.Transform(new Vector2(baseCursorX + cmd.Position.X, baseCursorY + cmd.Position.Y), activeTransform);
            var (subpixelX, snappedLogicalPos) = ResolveTextPlacement(
                transPos,
                dpiScale,
                rasterFontSize,
                isRotated,
                cmd.TextHintingMode);

            var info = _atlas.GetOrCreateGlyphByIndex(
                glyphFont,
                glyphIdx,
                rasterFontSize,
                subpixelX,
                cmd.PreferGlyphAtlas);
            if (info.Width == 0 || info.Height == 0)
            {
                CompileVectorGlyphFallback(
                    glyphFont,
                    glyphIdx,
                    cmd,
                    runGlyph.Position + cmd.Position,
                    glyphItalicSkew,
                    textPathCoverageGamma,
                    activeTransform,
                    fontScaleX);
                continue;
            }
            var glyphAtlasScale = atlasToLogicalScale * (info.RasterScale > 0f ? info.RasterScale : 1f);
            var glyphRenderWidth = info.RenderWidth > 0f ? info.RenderWidth : info.Width;
            var glyphRenderHeight = info.RenderHeight > 0f ? info.RenderHeight : info.Height;

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

                _textVerticesList.Add(new GlyphInstance
                {
                    SnappedLogicalPos = snappedLogicalPos,
                    BasisX = basisX,
                    BasisY = basisY,
                    BearSize = new Vector4(
                        info.BearX * fontScaleX,
                        info.BearY,
                        glyphRenderWidth * fontScaleX,
                        glyphRenderHeight),
                    TexCoords = new Vector4(info.TexCoordMin.X, info.TexCoordMin.Y, info.TexCoordMax.X, info.TexCoordMax.Y),
                    Color = color,
                    ScaleBoldItalicUseMvp = new Vector4(
                        glyphAtlasScale,
                        xOffset,
                        glyphItalicSkew,
                        EncodeTextFlags(
                            ActiveCompilationContext != null,
                            cmd.TextRenderingMode,
                            info.IsColorBitmap)),
                    BrushIndex = bIdx,
                    Padding = 0f
                });
            }
        }
    }

    private void CompileGlyphRunCommand(RenderCommand cmd, Matrix4x4 transform)
    {
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
        color.W *= (brush?.Opacity ?? 1f) * _activeOpacity;

        EnsureTextVertexCapacity(cmd.GlyphIndices.Length * (cmd.IsBold ? 2 : 1));

        var staticZoom = ActiveCompilationContext?.StaticZoom ?? 1f;
        var (dpiScale, rasterFontSize, atlasToLogicalScale) = ResolveTextRasterization(
            cmd.FontSize,
            activeTransform,
            _currentDpiScale,
            staticZoom);
        if (cmd.UseLogicalGlyphAtlasResolution)
        {
            var logicalTargetSize = cmd.FontSize *
                TransformMetrics.GetStrokeScale(activeTransform) * staticZoom;
            rasterFontSize = QuantizeGlyphRasterSize(logicalTargetSize);
            atlasToLogicalScale = cmd.FontSize / MathF.Max(rasterFontSize, 0.0001f);
        }
        var fontScaleX = cmd.HasFontTransform && float.IsFinite(cmd.FontTransform.X)
            ? cmd.FontTransform.X
            : 1f;
        var fontSkewX = cmd.HasFontTransform && float.IsFinite(cmd.FontTransform.Y)
            ? cmd.FontTransform.Y
            : 0f;
        var glyphItalicSkew = (cmd.IsItalic ? 0.22f : 0f) - fontSkewX;
        var transformScale = TransformMetrics.GetStrokeScale(activeTransform);
        var atlasUpscale = atlasToLogicalScale
            * transformScale
            * staticZoom
            * MathF.Max(1f, MathF.Abs(fontScaleX));
        var textPathCoverageGamma = MathF.Abs(fontSkewX) > 0.0001f || fontScaleX < 0f
            ? TransformedTextPathCoverageGamma
            : GetTextPathCoverageGamma(
                cmd.FontSize,
                activeTransform,
                transformScale,
                dpiScale);

        bool isRotated = MathF.Abs(activeTransform.M12) > 0.0001f ||
                         MathF.Abs(activeTransform.M21) > 0.0001f ||
                         activeTransform.M11 < 0.0f ||
                         activeTransform.M22 < 0.0f ||
                         MathF.Abs(fontSkewX) > 0.0001f ||
                         fontScaleX < 0f;
        var basisX = new Vector2(activeTransform.M11, activeTransform.M12);
        var basisY = new Vector2(activeTransform.M21, activeTransform.M22);
        int passCount = cmd.IsBold ? 2 : 1;
        float boldOffset = cmd.FontSize * 0.035f;

        for (int i = 0; i < cmd.GlyphIndices.Length; i++)
        {
            ushort glyphIdx = cmd.GlyphIndices[i];
            Vector2 position = cmd.GlyphPositions[i];

            var colorLayers = font.HasColorGlyphs
                ? font.GetColorLayers(glyphIdx)
                : null;

            if (colorLayers != null && colorLayers.Count > 0)
            {
                for (int layerIndex = 0; layerIndex < colorLayers.Count; layerIndex++)
                {
                    var layer = colorLayers[layerIndex];
                    var layerOutline = layer.Geometry ?? font.GetGlyphOutline(layer.GlyphId);
                    if (layerOutline == null) continue;

                    var layerPosition = position + cmd.Position;
                    var layerScale = cmd.FontSize / font.UnitsPerEm;

                    var transformedOutline = CreatePositionedGlyphOutline(
                        layerOutline,
                        layerScale,
                        layerPosition,
                        italicSkew: glyphItalicSkew,
                        scaleX: fontScaleX,
                        usesSvgCoordinates: layer.UsesSvgCoordinates);

                    var pathCmd = new RenderCommand
                    {
                        Type = RenderCommandType.DrawPath,
                        Path = transformedOutline,
                        Brush = CreatePositionedColorLayerBrush(layer, layerScale, layerPosition),
                        IsEdgeAliased = cmd.TextRenderingMode == TextRenderingMode.Aliased,
                        PathSampleGrid = cmd.TextRenderingMode == TextRenderingMode.Aliased
                            ? PathAtlas.StandardCoverageSampleGrid
                            : PathAtlas.HighPrecisionCoverageSampleGrid,
                        PathCoverageGamma = textPathCoverageGamma
                    };
                    CompilePathCommand(pathCmd, activeTransform);
                }

                continue;
            }

            var hasBitmapGlyph = font.HasBitmapGlyphs &&
                font.TryGetBitmapGlyph(glyphIdx, rasterFontSize, out _);
            if (!hasBitmapGlyph &&
                (cmd.UseVectorGlyphRendering ||
                 (!cmd.PreferGlyphAtlas &&
                  (font.HasCffOutlines || atlasUpscale > 1.0001f))))
            {
                CompileVectorGlyphFallback(
                    font,
                    glyphIdx,
                    cmd,
                    position + cmd.Position,
                    glyphItalicSkew,
                    textPathCoverageGamma,
                    activeTransform,
                    fontScaleX);

                continue;
            }

            SwitchBatch(BatchType.Text);
            float baseCursorX = position.X;
            float baseCursorY = position.Y;

            Vector2 transPos = Vector2.Transform(new Vector2(baseCursorX + cmd.Position.X, baseCursorY + cmd.Position.Y), activeTransform);
            var (subpixelX, snappedLogicalPos) = ResolveTextPlacement(
                transPos,
                dpiScale,
                rasterFontSize,
                isRotated,
                cmd.TextHintingMode);

            var info = _atlas.GetOrCreateGlyphByIndex(
                font,
                glyphIdx,
                rasterFontSize,
                subpixelX,
                cmd.PreferGlyphAtlas);
            if (info.Width == 0 || info.Height == 0)
            {
                CompileVectorGlyphFallback(
                    font,
                    glyphIdx,
                    cmd,
                    position + cmd.Position,
                    glyphItalicSkew,
                    textPathCoverageGamma,
                    activeTransform,
                    fontScaleX);
                continue;
            }
            var glyphAtlasScale = atlasToLogicalScale * (info.RasterScale > 0f ? info.RasterScale : 1f);
            var glyphRenderWidth = info.RenderWidth > 0f ? info.RenderWidth : info.Width;
            var glyphRenderHeight = info.RenderHeight > 0f ? info.RenderHeight : info.Height;

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

                _textVerticesList.Add(new GlyphInstance
                {
                    SnappedLogicalPos = snappedLogicalPos,
                    BasisX = basisX,
                    BasisY = basisY,
                    BearSize = new Vector4(
                        info.BearX * fontScaleX,
                        info.BearY,
                        glyphRenderWidth * fontScaleX,
                        glyphRenderHeight),
                    TexCoords = new Vector4(info.TexCoordMin.X, info.TexCoordMin.Y, info.TexCoordMax.X, info.TexCoordMax.Y),
                    Color = color,
                    ScaleBoldItalicUseMvp = new Vector4(
                        glyphAtlasScale,
                        xOffset,
                        glyphItalicSkew,
                        EncodeTextFlags(
                            ActiveCompilationContext != null,
                            cmd.TextRenderingMode,
                            info.IsColorBitmap)),
                    BrushIndex = bIdx,
                    Padding = 0f
                });
            }
        }
    }

    private void CompileVectorGlyphFallback(
        TtfFont font,
        ushort glyphIndex,
        RenderCommand command,
        Vector2 position,
        float italicSkew,
        float pathCoverageGamma,
        Matrix4x4 activeTransform,
        float scaleX)
    {
        var outline = font.GetGlyphOutline(glyphIndex);
        if (outline is null)
        {
            return;
        }

        int passCount = command.IsBold ? 2 : 1;
        float boldOffset = command.FontSize * 0.035f;
        for (int pass = 0; pass < passCount; pass++)
        {
            CompileVectorGlyphPath(
                outline,
                command,
                command.FontSize / font.UnitsPerEm,
                position,
                italicSkew,
                pass * boldOffset,
                pathCoverageGamma,
                activeTransform,
                scaleX);
        }
    }

    private void CompileVectorGlyphPath(
        PathGeometry outline,
        RenderCommand textCommand,
        float emScale,
        Vector2 position,
        float italicSkew,
        float xOffset,
        float pathCoverageGamma,
        Matrix4x4 activeTransform,
        float scaleX = 1f)
    {
        var positioned = position + new Vector2(xOffset, 0f);
        PathGeometry positionedOutline;
        Matrix4x4 placementTransform;

        if (IsFinite(positioned))
        {
            var integralPosition = new Vector2(MathF.Floor(positioned.X), MathF.Floor(positioned.Y));
            var fractionalPosition = positioned - integralPosition;
            var rasterPhase = new Vector2(
                QuantizeVectorGlyphPhase(fractionalPosition.X),
                QuantizeVectorGlyphPhase(fractionalPosition.Y));
            var key = new VectorGlyphPathCacheKey(
                outline,
                emScale,
                rasterPhase,
                italicSkew,
                scaleX,
                UsesSvgCoordinates: false);

            if (!_vectorGlyphPathCache.TryGetValue(key, out positionedOutline!))
            {
                if (_vectorGlyphPathCache.Count >= MaxCachedVectorGlyphPaths)
                {
                    _vectorGlyphPathCache.Clear();
                }

                positionedOutline = CreatePositionedGlyphOutline(
                    outline,
                    emScale,
                    rasterPhase,
                    italicSkew,
                    scaleX: scaleX);
                _vectorGlyphPathCache[key] = positionedOutline;
            }

            placementTransform = Matrix4x4.CreateTranslation(
                positioned.X - rasterPhase.X,
                positioned.Y - rasterPhase.Y,
                0f) * activeTransform;
        }
        else
        {
            positionedOutline = CreatePositionedGlyphOutline(
                outline,
                emScale,
                positioned,
                italicSkew,
                scaleX: scaleX);
            placementTransform = activeTransform;
        }

        CompilePathCommand(
            new RenderCommand
            {
                Type = RenderCommandType.DrawPath,
                Path = positionedOutline,
                Brush = textCommand.Brush,
                IsEdgeAliased = textCommand.TextRenderingMode == TextRenderingMode.Aliased,
                PathSampleGrid = textCommand.TextRenderingMode == TextRenderingMode.Aliased
                    ? PathAtlas.StandardCoverageSampleGrid
                    : PathAtlas.HighPrecisionCoverageSampleGrid,
                PathCoverageGamma = pathCoverageGamma
            },
            placementTransform,
            subpixelPhaseGrid: VectorGlyphDeviceSubpixelPhaseGrid,
            quantizeScale: true);
    }

    private static float GetTextPathCoverageGamma(
        float fontSize,
        Matrix4x4 transform,
        float transformScale,
        float effectiveDpiScale)
    {
        const float epsilon = 0.0001f;
        var hasNonAxisAlignedBasis = MathF.Abs(transform.M12) > epsilon ||
            MathF.Abs(transform.M21) > epsilon;
        var hasReflectedBasis = transform.M11 < 0f || transform.M22 < 0f;
        if (hasNonAxisAlignedBasis || hasReflectedBasis)
        {
            return TransformedTextPathCoverageGamma;
        }

        var pixelSize = MathF.Abs(fontSize) * transformScale * effectiveDpiScale;
        return float.IsFinite(pixelSize) && pixelSize >= LargeTextPathCoveragePixelThreshold
            ? LargeTextPathCoverageGamma
            : SmallTextPathCoverageGamma;
    }

    private static PathGeometry CreatePositionedGlyphOutline(
        PathGeometry outline,
        float emScale,
        Vector2 position,
        float italicSkew = 0f,
        float xOffset = 0f,
        bool usesSvgCoordinates = false,
        float scaleX = 1f)
    {
        var orientedItalicSkew = usesSvgCoordinates ? -italicSkew : italicSkew;
        Vector2 TransformPoint(Vector2 point) => new(
            position.X + (point.X * scaleX + point.Y * orientedItalicSkew) * emScale + xOffset,
            position.Y + point.Y * emScale * (usesSvgCoordinates ? 1f : -1f));

        var transformedOutline = new PathGeometry { FillRule = outline.FillRule };
        var figures = outline.Figures;
        for (var figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            var figure = figures[figureIndex];
            var transformedFigure = new PathFigure(TransformPoint(figure.StartPoint), figure.IsClosed)
            {
                IsFilled = figure.IsFilled
            };
            var segments = figure.Segments;
            for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                switch (segments[segmentIndex])
                {
                    case LineSegment line:
                        transformedFigure.Segments.Add(new LineSegment(
                            TransformPoint(line.Point),
                            line.IsSmoothJoin,
                            line.IsStroked));
                        break;
                    case QuadraticBezierSegment quadratic:
                        transformedFigure.Segments.Add(new QuadraticBezierSegment(
                            TransformPoint(quadratic.ControlPoint),
                            TransformPoint(quadratic.Point),
                            quadratic.IsSmoothJoin,
                            quadratic.IsStroked));
                        break;
                    case CubicBezierSegment cubic:
                        transformedFigure.Segments.Add(new CubicBezierSegment(
                            TransformPoint(cubic.ControlPoint1),
                            TransformPoint(cubic.ControlPoint2),
                            TransformPoint(cubic.Point),
                            cubic.IsSmoothJoin,
                            cubic.IsStroked));
                        break;
                    case ArcSegment arc:
                        transformedFigure.Segments.Add(new ArcSegment(
                            TransformPoint(arc.Point),
                            new Vector2(
                                MathF.Abs(arc.Size.X * emScale * scaleX),
                                MathF.Abs(arc.Size.Y * emScale)),
                            arc.RotationAngle,
                            arc.IsLargeArc,
                            arc.SweepDirection,
                            arc.IsSmoothJoin,
                            arc.IsStroked));
                        break;
                }
            }

            transformedOutline.Figures.Add(transformedFigure);
        }

        return transformedOutline;
    }

    private static Brush CreatePositionedColorLayerBrush(
        FontColorLayer layer,
        float emScale,
        Vector2 position)
    {
        if (layer.Brush is null)
        {
            return new SolidColorBrush(layer.Color);
        }

        if (!layer.UsesSvgCoordinates)
        {
            return layer.Brush;
        }

        Vector2 PositionPoint(Vector2 point) => position + point * emScale;
        switch (layer.Brush)
        {
            case SolidColorBrush solid:
                return new SolidColorBrush(solid.Color) { Opacity = solid.Opacity };

            case LinearGradientBrush linear:
                return new LinearGradientBrush(
                    PositionPoint(linear.StartPoint),
                    PositionPoint(linear.EndPoint),
                    linear.Stops)
                {
                    Opacity = linear.Opacity,
                    SpreadMethod = linear.SpreadMethod,
                    ColorInterpolationMode = linear.ColorInterpolationMode
                };

            case RadialGradientBrush radial:
                return new RadialGradientBrush(
                    PositionPoint(radial.Center),
                    PositionPoint(radial.GradientOrigin),
                    radial.RadiusX * emScale,
                    radial.RadiusY * emScale,
                    radial.Stops)
                {
                    Opacity = radial.Opacity,
                    SpreadMethod = radial.SpreadMethod,
                    ColorInterpolationMode = radial.ColorInterpolationMode
                };

            default:
                return layer.Brush;
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
        var cubicCoefficients = cmd.HasTextureCubicCoefficients &&
            float.IsFinite(cmd.TextureCubicCoefficients.X) &&
            float.IsFinite(cmd.TextureCubicCoefficients.Y)
                ? cmd.TextureCubicCoefficients
                : new Vector2(0f, 0.5f);
        var indexStart = _textureIndicesList.Count;
        var patches = cmd.TexturePatches;
        var patchCount = patches?.Length ?? 1;
        _textureVerticesList.EnsureCapacity(_textureVerticesList.Count + patchCount * 4);
        _textureIndicesList.EnsureCapacity(_textureIndicesList.Count + patchCount * 6);

        if (patches == null)
        {
            AppendTextureQuad(
                cmd.Texture,
                cmd.SrcRect,
                cmd.Rect,
                color,
                patchKind: 0f,
                cubicCoefficients,
                transform,
                default,
                hasDestinationTransform: false,
                colorBlendMode: 0f,
                patchOpacity: 1f);
        }
        else
        {
            for (var patchIndex = 0; patchIndex < patches.Length; patchIndex++)
            {
                var patch = patches[patchIndex];
                if (patch.Kind == TexturePatchKind.FixedColor)
                {
                    var alpha = patch.Color.W * _activeOpacity;
                    var fixedColor = isPremultiplied
                        ? new Vector4(
                            patch.Color.X * alpha,
                            patch.Color.Y * alpha,
                            patch.Color.Z * alpha,
                            alpha)
                        : new Vector4(patch.Color.X, patch.Color.Y, patch.Color.Z, alpha);
                    AppendTextureQuad(
                        cmd.Texture,
                        default,
                        patch.Destination,
                        fixedColor,
                        isPremultiplied ? 2f : 1f,
                        cubicCoefficients,
                        transform,
                        default,
                        hasDestinationTransform: false,
                        colorBlendMode: 0f,
                        patchOpacity: 1f);
                }
                else if (patch.Kind == TexturePatchKind.AtlasColor)
                {
                    var alpha = patch.Color.W;
                    var atlasColor = new Vector4(
                        patch.Color.X * alpha,
                        patch.Color.Y * alpha,
                        patch.Color.Z * alpha,
                        alpha);
                    AppendTextureQuad(
                        cmd.Texture,
                        patch.Source,
                        patch.Destination,
                        atlasColor,
                        isPremultiplied ? 4f : 3f,
                        cubicCoefficients,
                        transform,
                        patch.DestinationTransform,
                        patch.HasDestinationTransform,
                        (float)patch.ColorBlendMode,
                        cmd.TextureSamplingMode == TextureSamplingMode.Cubic
                            ? -_activeOpacity
                            : _activeOpacity);
                }
                else
                {
                    AppendTextureQuad(
                        cmd.Texture,
                        patch.Source,
                        patch.Destination,
                        color,
                        patchKind: 0f,
                        cubicCoefficients,
                        transform,
                        patch.DestinationTransform,
                        patch.HasDestinationTransform,
                        colorBlendMode: 0f,
                        patchOpacity: 1f);
                }
            }
        }

        var indexCount = _textureIndicesList.Count - indexStart;
        if (indexCount == 0)
        {
            return;
        }

        _drawCalls.Add(new CompositorDrawCall
        {
            Type = DrawCallType.Texture,
            IndexStart = (uint)indexStart,
            IndexCount = (uint)indexCount,
            Texture = cmd.Texture,
            ClipRect = _activeClipRect,
            MaskTexture = _maskStack.Count > 0 ? _maskStack.Peek() : null,
            BlendMode = _activeBlendMode,
            TextureSamplingMode = cmd.TextureSamplingMode,
            TextureMaxAnisotropy = cmd.TextureMaxAnisotropy,
            TextureAlphaMode = cmd.Texture.AlphaMode
        });
    }

    private void AppendTextureQuad(
        GpuTexture texture,
        Rect source,
        Rect destination,
        Vector4 color,
        float patchKind,
        Vector2 cubicCoefficients,
        Matrix4x4 transform,
        Matrix3x2 destinationTransform,
        bool hasDestinationTransform,
        float colorBlendMode,
        float patchOpacity)
    {
        var r = destination;
        var v0 = new Vector2(r.X, r.Y);
        var v1 = new Vector2(r.X + r.Width, r.Y);
        var v2 = new Vector2(r.X + r.Width, r.Y + r.Height);
        var v3 = new Vector2(r.X, r.Y + r.Height);
        if (hasDestinationTransform)
        {
            v0 = Vector2.Transform(v0, destinationTransform);
            v1 = Vector2.Transform(v1, destinationTransform);
            v2 = Vector2.Transform(v2, destinationTransform);
            v3 = Vector2.Transform(v3, destinationTransform);
        }

        v0 = Vector2.Transform(v0, transform);
        v1 = Vector2.Transform(v1, transform);
        v2 = Vector2.Transform(v2, transform);
        v3 = Vector2.Transform(v3, transform);

        Vector2 uv0, uv1, uv2, uv3;
        if ((patchKind == 0f || patchKind >= 3f) && source.Width > 0f && source.Height > 0f)
        {
            float texW = texture.Width;
            float texH = texture.Height;
            float l = source.X / texW;
            float t = source.Y / texH;
            float right = (source.X + source.Width) / texW;
            float b = (source.Y + source.Height) / texH;

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
                return;
            }

            if (!QuadClipper.TryClipAxisAlignedQuad(
                    _activeClipRect.Value,
                    ref v0,
                    ref v1,
                    ref v2,
                    ref v3,
                    ref uv0,
                    ref uv1,
                    ref uv2,
                    ref uv3))
            {
                return;
            }
        }

        uint idxStart = (uint)_textureVerticesList.Count;
        int originalVertexCount = _textureVerticesList.Count;
        CollectionsMarshal.SetCount(_textureVerticesList, originalVertexCount + 4);
        var vertexSpan = CollectionsMarshal.AsSpan(_textureVerticesList).Slice(originalVertexCount, 4);

        vertexSpan[0] = new VectorVertex(
            v0, color, uv0, patchKind, cubicCoefficients, colorBlendMode, patchOpacity);
        vertexSpan[1] = new VectorVertex(
            v1, color, uv1, patchKind, cubicCoefficients, colorBlendMode, patchOpacity);
        vertexSpan[2] = new VectorVertex(
            v2, color, uv2, patchKind, cubicCoefficients, colorBlendMode, patchOpacity);
        vertexSpan[3] = new VectorVertex(
            v3, color, uv3, patchKind, cubicCoefficients, colorBlendMode, patchOpacity);

        int originalIndexCount = _textureIndicesList.Count;
        CollectionsMarshal.SetCount(_textureIndicesList, originalIndexCount + 6);
        var indexSpan = CollectionsMarshal.AsSpan(_textureIndicesList).Slice(originalIndexCount, 6);

        indexSpan[0] = idxStart;
        indexSpan[1] = idxStart + 1;
        indexSpan[2] = idxStart + 2;
        indexSpan[3] = idxStart;
        indexSpan[4] = idxStart + 2;
        indexSpan[5] = idxStart + 3;
    }

    internal Sampler* GetTextureSampler(TextureSamplingMode samplingMode, byte maxAnisotropy = 1)
    {
        if (samplingMode == TextureSamplingMode.LinearMipmap && maxAnisotropy > 1)
        {
            return GetAnisotropicTextureSampler(maxAnisotropy);
        }

        return samplingMode switch
        {
            TextureSamplingMode.Nearest when _nearestTextureSampler != null =>
                _nearestTextureSampler,
            TextureSamplingMode.LinearMipmap when _mipmapTextureSampler != null =>
                _mipmapTextureSampler,
            _ => _atlasSampler
        };
    }

    private Sampler* GetAnisotropicTextureSampler(byte requestedMaxAnisotropy)
    {
        var maxAnisotropy = (byte)Math.Clamp((int)requestedMaxAnisotropy, 2, 16);
        lock (_anisotropicTextureSamplers)
        {
            if (_anisotropicTextureSamplers.TryGetValue(maxAnisotropy, out var existing))
            {
                return (Sampler*)existing;
            }

            var descriptor = new SamplerDescriptor
            {
                AddressModeU = AddressMode.ClampToEdge,
                AddressModeV = AddressMode.ClampToEdge,
                AddressModeW = AddressMode.ClampToEdge,
                MagFilter = FilterMode.Linear,
                MinFilter = FilterMode.Linear,
                MipmapFilter = MipmapFilterMode.Linear,
                LodMaxClamp = 32f,
                LodMinClamp = 0f,
                MaxAnisotropy = maxAnisotropy
            };
            var sampler = _context.Api.DeviceCreateSampler(_context.Device, &descriptor);
            _anisotropicTextureSamplers.Add(maxAnisotropy, (nint)sampler);
            return sampler;
        }
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

    private void PrepareWavefrontComposite(
        uint logicalWidth,
        uint logicalHeight,
        uint renderWidth,
        uint renderHeight)
    {
        if (_wavefrontColorTexture == null ||
            _wavefrontColorTexture.Width != renderWidth ||
            _wavefrontColorTexture.Height != renderHeight)
        {
            _wavefrontColorTexture?.Dispose();
            _wavefrontColorTexture = new GpuTexture(
                _context,
                renderWidth,
                renderHeight,
                RenderFormat,
                TextureUsage.RenderAttachment |
                TextureUsage.TextureBinding |
                TextureUsage.StorageBinding |
                TextureUsage.CopySrc,
                "Wavefront Intermediate Color Texture",
                alphaMode: GpuTextureAlphaMode.Premultiplied);

            if (_wavefrontTextureBindGroup != null)
            {
                QueueBindGroupRelease((nint)_wavefrontTextureBindGroup);
                _wavefrontTextureBindGroup = null;
            }
        }

        _wavefrontQuadVertexBuffer ??= new GpuBuffer(
            _context,
            (uint)(4 * Marshal.SizeOf<VectorVertex>()),
            BufferUsage.Vertex | BufferUsage.CopyDst,
            "Wavefront Quad Vertex Buffer");

        Span<VectorVertex> vertices = stackalloc VectorVertex[4];
        var color = Vector4.One;
        vertices[0] = new VectorVertex(new Vector2(0f, 0f), color, new Vector2(0f, 0f));
        vertices[1] = new VectorVertex(new Vector2(logicalWidth, 0f), color, new Vector2(1f, 0f));
        vertices[2] = new VectorVertex(new Vector2(logicalWidth, logicalHeight), color, new Vector2(1f, 1f));
        vertices[3] = new VectorVertex(new Vector2(0f, logicalHeight), color, new Vector2(0f, 1f));

        _wavefrontQuadVertexBuffer.Write(vertices);

        if (_wavefrontQuadIndexBuffer == null)
        {
            _wavefrontQuadIndexBuffer = new GpuBuffer(
                _context,
                6u * sizeof(uint),
                BufferUsage.Index | BufferUsage.CopyDst,
                "Wavefront Quad Index Buffer");
            ReadOnlySpan<uint> indices = [0, 1, 2, 0, 2, 3];
            _wavefrontQuadIndexBuffer.Write(indices);
        }

        if (_wavefrontTextureBindGroup == null ||
            _wavefrontTextureBindGroupGeneration != _wavefrontColorTexture.Generation)
        {
            if (_wavefrontTextureBindGroup != null)
            {
                QueueBindGroupRelease((nint)_wavefrontTextureBindGroup);
            }

            var entries = stackalloc BindGroupEntry[2];
            entries[0] = new BindGroupEntry { Binding = 0, Sampler = _atlasSampler };
            entries[1] = new BindGroupEntry { Binding = 1, TextureView = _wavefrontColorTexture.ViewPtr };
            var descriptor = new BindGroupDescriptor
            {
                Layout = _textureBindGroupLayoutOffscreen,
                EntryCount = 2,
                Entries = entries
            };
            _wavefrontTextureBindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &descriptor);
            _wavefrontTextureBindGroupGeneration = _wavefrontColorTexture.Generation;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        lock (_context.RenderLock)
        {
            _wavefrontEngine?.Dispose();
            _wavefrontEngine = null;
            _wavefrontColorTexture?.Dispose();
            _wavefrontColorTexture = null;
            _wavefrontQuadVertexBuffer?.Dispose();
            _wavefrontQuadVertexBuffer = null;
            _wavefrontQuadIndexBuffer?.Dispose();
            _wavefrontQuadIndexBuffer = null;
            if (_wavefrontTextureBindGroup != null)
            {
                QueueBindGroupRelease((nint)_wavefrontTextureBindGroup);
                _wavefrontTextureBindGroup = null;
            }

            ReleaseMsaaResources();

            _uniformBuffer.Dispose();
            _brushesStorageBuffer.Dispose();
            _gradientStopsStorageBuffer.Dispose();
            _vectorVertexBuffer.Dispose();
            _vectorIndexBuffer.Dispose();
            _textVertexBuffer.Dispose();
            _textureVertexBuffer.Dispose();
            _textureIndexBuffer.Dispose();
            _lastHitTestDeviceIndex?.Dispose();
            _hitTestCacheBuilder.Dispose();
            _lastHitTestDeviceIndex = null;
            LastHitTestIndex = null;

            _atlas.Dispose();
            _pathAtlas.Dispose();

            lock (_registeredExtensions)
            {
                var extensionCount = _registeredExtensions.Count;
                for (var extensionIndex = 0; extensionIndex < extensionCount; extensionIndex++)
                {
                    var ext = _registeredExtensions[extensionIndex];
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
            var effectTextureEnumerator = _effectTextures.Values.GetEnumerator();
            while (effectTextureEnumerator.MoveNext())
            {
                var tuple = effectTextureEnumerator.Current;
                tuple.Source.Dispose();
                tuple.Temp.Dispose();
                tuple.Destination.Dispose();
            }
            _effectTextures.Clear();

            var allocatedLayerTextureEnumerator = _allocatedLayerTextures.GetEnumerator();
            while (allocatedLayerTextureEnumerator.MoveNext())
            {
                var entry = allocatedLayerTextureEnumerator.Current;
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
                if (_mipmapTextureSampler != null) _context.QueueSamplerDisposal((IntPtr)_mipmapTextureSampler);
                foreach (var sampler in _anisotropicTextureSamplers.Values)
                {
                    _context.QueueSamplerDisposal(sampler);
                }
                _anisotropicTextureSamplers.Clear();

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
                if (_advancedBlendPipelineLayout != null) _context.QueuePipelineLayoutDisposal((IntPtr)_advancedBlendPipelineLayout);
                if (_advancedBlendBindGroupLayout != null) _context.QueueBindGroupLayoutDisposal((IntPtr)_advancedBlendBindGroupLayout);

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
                    var cachedBindGroupEnumerator = _persistentTextureBindGroups.Values.GetEnumerator();
                    while (cachedBindGroupEnumerator.MoveNext())
                    {
                        var cachedBg = cachedBindGroupEnumerator.Current;
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

                var maskBindGroupEnumerator = _maskBindGroups.Values.GetEnumerator();
                while (maskBindGroupEnumerator.MoveNext())
                {
                    var bg = maskBindGroupEnumerator.Current;
                    _context.QueueBindGroupDisposal((IntPtr)bg);
                }

                var offscreenMaskBindGroupEnumerator = _maskBindGroupsOffscreen.Values.GetEnumerator();
                while (offscreenMaskBindGroupEnumerator.MoveNext())
                {
                    var bg = offscreenMaskBindGroupEnumerator.Current;
                    _context.QueueBindGroupDisposal((IntPtr)bg);
                }
            }
            _maskBindGroups.Clear();
            _maskBindGroupsOffscreen.Clear();
            ReturnMaskRenderPassDrawCallLists();
            _drawCallListPool.Dispose();
            _clipStack.Dispose();
            _clipScopeIsGeometryMask.Dispose();
            _opacityStack.Dispose();
            _blendModeStack.Dispose();
            _maskStack.Dispose();

            DisposeMaskTexturePool();

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
            SampleCount = Options.PrimarySampleCount,
            ViewFormatCount = 0,
            ViewFormats = null
        };

        _msaaTexture = _context.Api.DeviceCreateTexture(_context.Device, &desc);
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

        _msaaTextureView = _context.Api.TextureCreateView(_msaaTexture, &viewDesc);
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
                _context.QueueTextureViewDisposal((IntPtr)_msaaTextureView);
                _msaaTextureView = null;
            }

            if (_msaaTexture != null)
            {
                _context.QueueTextureDisposal((IntPtr)_msaaTexture);
                _msaaTexture = null;
            }
        }
    }

    private static bool RequiresDestinationSampling(GpuBlendMode blendMode)
    {
        return blendMode is
            GpuBlendMode.Multiply or
            GpuBlendMode.Screen or
            GpuBlendMode.Darken or
            GpuBlendMode.Lighten or
            GpuBlendMode.Exclusion or
            GpuBlendMode.Overlay or
            GpuBlendMode.ColorDodge or
            GpuBlendMode.ColorBurn or
            GpuBlendMode.HardLight or
            GpuBlendMode.SoftLight or
            GpuBlendMode.Difference or
            GpuBlendMode.Hue or
            GpuBlendMode.Saturation or
            GpuBlendMode.Color or
            GpuBlendMode.Luminosity;
    }

    private bool CanEncodeAdvancedBlend(in CompositorDrawCall drawCall, GpuTexture targetTexture)
    {
        return drawCall.Type == DrawCallType.Texture &&
            IsTextureBindable(drawCall.Texture) &&
            RequiresDestinationSampling(drawCall.BlendMode) &&
            targetTexture.Usage.HasFlag(TextureUsage.TextureBinding);
    }

    private void EnsureAdvancedBlendResources(uint width, uint height, TextureFormat format)
    {
        if (_advancedBlendScratchTexture != null &&
            _advancedBlendSourceTexture != null &&
            _advancedBlendScratchTexture.Width == width &&
            _advancedBlendScratchTexture.Height == height &&
            _advancedBlendScratchTexture.Format == format)
        {
            return;
        }

        _advancedBlendScratchTexture?.Dispose();
        _advancedBlendSourceTexture?.Dispose();
        const TextureUsage usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding;
        _advancedBlendScratchTexture = new GpuTexture(
            _context,
            width,
            height,
            format,
            usage,
            "Advanced blend scratch",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        _advancedBlendSourceTexture = new GpuTexture(
            _context,
            width,
            height,
            format,
            usage,
            "Advanced blend source",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
    }

    private void EnsureAdvancedBlendLayout()
    {
        if (_advancedBlendBindGroupLayout != null)
        {
            return;
        }

        var entries = stackalloc BindGroupLayoutEntry[2];
        for (var index = 0; index < 2; index++)
        {
            entries[index] = new BindGroupLayoutEntry
            {
                Binding = (uint)index,
                Visibility = ShaderStage.Fragment,
                Texture = new TextureBindingLayout
                {
                    SampleType = TextureSampleType.Float,
                    ViewDimension = TextureViewDimension.Dimension2D,
                    Multisampled = false
                }
            };
        }

        var bindGroupLayoutDescriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = 2,
            Entries = entries
        };
        _advancedBlendBindGroupLayout = _context.Api.DeviceCreateBindGroupLayout(
            _context.Device,
            &bindGroupLayoutDescriptor);
        if (_advancedBlendBindGroupLayout == null)
        {
            throw new InvalidOperationException("Failed to create the advanced blend bind group layout.");
        }

        var layouts = stackalloc BindGroupLayout*[1];
        layouts[0] = _advancedBlendBindGroupLayout;
        var pipelineLayoutDescriptor = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 1,
            BindGroupLayouts = layouts
        };
        _advancedBlendPipelineLayout = _context.Api.DeviceCreatePipelineLayout(
            _context.Device,
            &pipelineLayoutDescriptor);
        if (_advancedBlendPipelineLayout == null)
        {
            throw new InvalidOperationException("Failed to create the advanced blend pipeline layout.");
        }
    }

    private RenderPipeline* GetAdvancedBlendPipeline(GpuBlendMode blendMode)
    {
        EnsureAdvancedBlendLayout();
        var shaderKey = $"AdvancedBlend_{blendMode}";
        var shaderCode = Shaders.AdvancedBlendShader.Replace(
            "__BLEND_MODE__",
            ((int)blendMode).ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        var shader = _pipelineCache.GetOrCreateShader(
            shaderKey,
            shaderCode,
            $"Advanced blend {blendMode} shader");
        return _pipelineCache.GetOrCreateRenderPipeline(
            $"AdvancedBlendPipeline_{blendMode}_{RenderFormat}",
            shader,
            ReadOnlySpan<VertexBufferLayout>.Empty,
            targetFormat: RenderFormat,
            enableBlend: false,
            sampleCount: 1,
            pipelineLayout: _advancedBlendPipelineLayout);
    }

    private RenderPassEncoder* BeginOffscreenTexturePass(
        CommandEncoder* encoder,
        GpuTexture target,
        LoadOp loadOperation,
        Color clearColor)
    {
        var colorAttachment = new RenderPassColorAttachment
        {
            View = target.ViewPtr,
            ResolveTarget = null,
            LoadOp = loadOperation,
            StoreOp = StoreOp.Store,
            ClearValue = clearColor
        };
        var passDescriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = null
        };
        var pass = _context.Api.CommandEncoderBeginRenderPass(encoder, &passDescriptor);
        if (pass == null)
        {
            throw new InvalidOperationException("Failed to begin an offscreen texture pass.");
        }

        ApplyRenderPassViewport(pass, target.Width, target.Height, useRenderTargetViewport: false);
        return pass;
    }

    private void EncodeAdvancedBlendSource(
        CommandEncoder* encoder,
        GpuTexture sourceTarget,
        in CompositorDrawCall drawCall)
    {
        var pass = BeginOffscreenTexturePass(
            encoder,
            sourceTarget,
            LoadOp.Clear,
            new Color());
        try
        {
            if (!ApplyDrawCallScissor(pass, drawCall, useRenderTargetViewport: false))
            {
                return;
            }

            var texture = drawCall.Texture!;
            var pipeline = GetPipeline(
                DrawCallType.Texture,
                GpuBlendMode.SrcOver,
                isOffscreen: true,
                textureAlphaMode: drawCall.TextureAlphaMode);
            var maskBindGroup = GetMaskBindGroup(drawCall.MaskTexture, isOffscreen: true);
            _context.Api.RenderPassEncoderSetPipeline(pass, pipeline);
            _context.Api.RenderPassEncoderSetBindGroup(
                pass,
                0,
                _textureUniformBindGroupOffscreen,
                0,
                null);
            _context.Api.RenderPassEncoderSetVertexBuffer(
                pass,
                0,
                _textureVertexBuffer.BufferPtr,
                0,
                _textureVertexBuffer.Size);
            _context.Api.RenderPassEncoderSetIndexBuffer(
                pass,
                _textureIndexBuffer.BufferPtr,
                IndexFormat.Uint32,
                0,
                _textureIndexBuffer.Size);
            _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);

            var cacheKey = new TextureCacheKey(
                texture.Id,
                texture.Generation,
                isOffscreen: true,
                drawCall.TextureSamplingMode,
                drawCall.TextureMaxAnisotropy);
            CachedBindGroup? cachedBindGroup;
            lock (_persistentTextureBindGroups)
            {
                if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out cachedBindGroup))
                {
                    var entries = stackalloc BindGroupEntry[2];
                    entries[0] = new BindGroupEntry
                    {
                        Binding = 0,
                        Sampler = GetTextureSampler(
                            drawCall.TextureSamplingMode,
                            drawCall.TextureMaxAnisotropy)
                    };
                    entries[1] = new BindGroupEntry
                    {
                        Binding = 1,
                        TextureView = texture.ViewPtr
                    };
                    var descriptor = new BindGroupDescriptor
                    {
                        Layout = _textureBindGroupLayoutOffscreen,
                        EntryCount = 2,
                        Entries = entries
                    };
                    var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &descriptor);
                    cachedBindGroup = new CachedBindGroup((nint)bindGroup, _frameNumber);
                    _persistentTextureBindGroups[cacheKey] = cachedBindGroup;
                }
                else
                {
                    cachedBindGroup.LastUsedFrame = _frameNumber;
                }
            }

            _context.Api.RenderPassEncoderSetBindGroup(
                pass,
                1,
                (BindGroup*)cachedBindGroup.BindGroupPtr,
                0,
                null);
            _context.Api.RenderPassEncoderDrawIndexed(
                pass,
                drawCall.IndexCount,
                1,
                drawCall.IndexStart,
                0,
                0);
        }
        finally
        {
            _context.Api.RenderPassEncoderEnd(pass);
            _context.Api.RenderPassEncoderRelease(pass);
        }
    }

    private void EncodeAdvancedBlendFullscreen(
        CommandEncoder* encoder,
        GpuTexture destination,
        GpuTexture source,
        GpuTexture output,
        GpuBlendMode blendMode)
    {
        EnsureAdvancedBlendLayout();
        var entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry
        {
            Binding = 0,
            TextureView = destination.ViewPtr
        };
        entries[1] = new BindGroupEntry
        {
            Binding = 1,
            TextureView = source.ViewPtr
        };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = _advancedBlendBindGroupLayout,
            EntryCount = 2,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(
            _context.Device,
            &bindGroupDescriptor);
        if (bindGroup == null)
        {
            throw new InvalidOperationException("Failed to bind advanced blend textures.");
        }

        var pass = BeginOffscreenTexturePass(
            encoder,
            output,
            LoadOp.Clear,
            new Color());
        try
        {
            _context.Api.RenderPassEncoderSetPipeline(
                pass,
                GetAdvancedBlendPipeline(blendMode));
            _context.Api.RenderPassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
            _context.Api.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
        }
        finally
        {
            _context.Api.RenderPassEncoderEnd(pass);
            _context.Api.RenderPassEncoderRelease(pass);
            _context.Api.BindGroupRelease(bindGroup);
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

            _context.Api.RenderPassEncoderSetScissorRect(pass, sx, sy, sw, sh);
            return true;
        }

        _context.Api.RenderPassEncoderSetScissorRect(pass, viewportX, viewportY, targetWidth, targetHeight);
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

        clippedLeft = SnapScissorCoordinate(clippedLeft);
        clippedTop = SnapScissorCoordinate(clippedTop);
        clippedRight = SnapScissorCoordinate(clippedRight);
        clippedBottom = SnapScissorCoordinate(clippedBottom);

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

    private static float SnapScissorCoordinate(float value)
    {
        var rounded = MathF.Round(value);
        return MathF.Abs(value - rounded) <= 0.0001f ? rounded : value;
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
        _context.Api.RenderPassEncoderSetViewport(
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
        return viewport.Clamp(targetWidth, targetHeight);
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
        var compositeScope = PushVisualCompositeScope(fe, compositeTransform, parentTransform);
        try
        {
            // Draw the cached texture onto the main swapchain.
            if (fe.Effect is BlurEffect bEff)
            {
                if (bEff.BlurRadius <= 0.01f)
                {
                    // Draw original source directly (no blur!)
                    DrawTextureOnMain(textures.Source, paddedRect, compositeTransform, fe.HitTestId);
                }
                else
                {
                    // Draw the blurred result back onto the main screen (shifted back by padding)
                    DrawTextureOnMain(textures.Destination, paddedRect, compositeTransform, fe.HitTestId);
                }
            }
            else if (fe.Effect is DropShadowEffect sEff)
            {
                // Draw blurred shadow first (at offset, shifted back by padding)
                var shadowRect = new Rect(
                    sEff.Offset - new Vector2(padding, padding),
                    new Vector2(logicalWidth, logicalHeight));
                DrawTextureOnMain(textures.Destination, shadowRect, compositeTransform, fe.HitTestId);

                // Draw original source on top (shifted back by padding)
                DrawTextureOnMain(textures.Source, paddedRect, compositeTransform, fe.HitTestId);
            }
            else if (fe.Effect is WpfShaderEffect shaderEffect)
            {
                DrawWpfShaderEffectOnMain(fe, shaderEffect, textures.Source, paddedRect, compositeTransform);
            }

            AddDescendantVisualHitTestBounds(fe, compositeTransform);
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
        var compositeScope = PushVisualCompositeScope(node, compositeTransform, parentTransform);
        try
        {
            // Draw the cached layer texture onto the main swapchain.
            DrawTextureOnMain(node.LayerTexture!, controlRect, compositeTransform, node.HitTestId);
            AddDescendantVisualHitTestBounds(node, compositeTransform);
        }
        finally
        {
            PopVisualCompositeScope(compositeScope);
        }

        node.IsDirty = false;
    }

    private void AddDescendantVisualHitTestBounds(Visual visual, Matrix4x4 globalTransform)
    {
        if (!Options.EnableGpuHitTesting ||
            _suspendHitTestCacheWrites ||
            visual is not ContainerVisual container)
        {
            return;
        }

        // Effect and layer subtrees are rendered offscreen with hit-test cache writes
        // suspended. Rebuild their retained visual-owner bounds in the main scene so
        // host input routing keeps the same owner topology as direct composition.
        var children = container.Children;
        for (int index = 0; index < children.Count; index++)
        {
            AddVisualHitTestBoundsSubtree(children[index], globalTransform);
        }
    }

    private void AddVisualHitTestBoundsSubtree(Visual visual, Matrix4x4 parentTransform)
    {
        if (!visual.IsVisible || visual.Opacity <= 0.0001f)
        {
            return;
        }

        Matrix4x4 globalTransform = visual.GetLocalTransform() * parentTransform;
        bool hasClip = visual.ClipBounds.HasValue;
        bool hasOuterClip = visual.OuterClipBounds.HasValue;
        bool hasGeometryClip = visual.GeometryClip != null;

        if (hasClip)
        {
            PushHitTestClip(visual.ClipBounds.GetValueOrDefault(), globalTransform);
        }

        if (hasOuterClip)
        {
            PushHitTestClip(visual.OuterClipBounds.GetValueOrDefault(), parentTransform);
        }

        if (hasGeometryClip)
        {
            AddHitTestStateCommand(
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = visual.GeometryClip
                },
                globalTransform);
        }

        try
        {
            AddVisualHitTestBounds(visual, globalTransform);

            if (visual is ContainerVisual container)
            {
                var children = container.Children;
                for (int index = 0; index < children.Count; index++)
                {
                    AddVisualHitTestBoundsSubtree(children[index], globalTransform);
                }
            }
        }
        finally
        {
            if (hasGeometryClip)
            {
                AddHitTestStateCommand(
                    new RenderCommand { Type = RenderCommandType.PopGeometryClip },
                    Matrix4x4.Identity);
            }

            if (hasOuterClip)
            {
                PopHitTestClip();
            }

            if (hasClip)
            {
                PopHitTestClip();
            }
        }
    }

    private VisualCompositeScope PushVisualCompositeScope(
        Visual node,
        Matrix4x4 compositeTransform,
        Matrix4x4 parentTransform)
    {
        var scope = VisualCompositeScope.None;
        if (node.ClipBounds.HasValue)
        {
            PushClipRect(node.ClipBounds.Value, compositeTransform);
            PushHitTestClip(node.ClipBounds.Value, compositeTransform);
            scope |= VisualCompositeScope.Clip;
        }

        if (node.OuterClipBounds.HasValue)
        {
            PushClipRect(node.OuterClipBounds.Value, parentTransform);
            PushHitTestClip(node.OuterClipBounds.Value, parentTransform);
            scope |= VisualCompositeScope.OuterClip;
        }

        if (node.GeometryClip != null)
        {
            PushGeometryMask(node.GeometryClip, compositeTransform);
            AddHitTestStateCommand(
                new RenderCommand
                {
                    Type = RenderCommandType.PushGeometryClip,
                    Path = node.GeometryClip
                },
                compositeTransform);
            scope |= VisualCompositeScope.GeometryClip;
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

        if ((scope & VisualCompositeScope.GeometryClip) != 0)
        {
            AddHitTestStateCommand(
                new RenderCommand
                {
                    Type = RenderCommandType.PopGeometryClip
                },
                Matrix4x4.Identity);
            PopGeometryMask();
        }

        if ((scope & VisualCompositeScope.OuterClip) != 0)
        {
            PopHitTestClip();
            PopClipRect();
        }

        if ((scope & VisualCompositeScope.Clip) != 0)
        {
            PopHitTestClip();
            PopClipRect();
        }
    }

    private void DrawTextureOnMain(GpuTexture texture, Rect localRect, Matrix4x4 parentTransform, int hitTestId = 0)
    {
        var cmd = new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Texture = texture,
            Rect = localRect
        };
        if (hitTestId != 0)
        {
            AddHitTestCommand(cmd, parentTransform, hitTestId);
        }

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
            DrawTextureOnMain(sourceTexture, localRect, parentTransform, visual.HitTestId);
            return;
        }

        if (visual.HitTestId != 0)
        {
            AddHitTestCommand(
                new RenderCommand
                {
                    Type = RenderCommandType.DrawTexture,
                    Rect = localRect
                },
                parentTransform,
                visual.HitTestId);
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
        CompositorHostFrame hostFrame,
        GpuTexture targetTexture,
        float padding,
        Vector4? clearColor = null,
        bool loadExistingContents = false,
        bool includeRootTransform = true,
        bool includeRootVisualState = true)
    {
        if (!hostFrame.IsValid)
        {
            return;
        }

        RenderOffscreen(
            node,
            hostFrame.LogicalPixelWidth,
            hostFrame.LogicalPixelHeight,
            targetTexture,
            padding,
            hostFrame.DpiScale,
            clearColor,
            loadExistingContents,
            includeRootTransform,
            includeRootVisualState);
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
        _compiledSceneReusable = false;
        lock (_offscreenRenderLock)
        {
            var ownsOffscreenFrame = _offscreenRenderDepth++ == 0;
            try
            {
                if (ownsOffscreenFrame)
                {
                    _context.CleanupPendingResources();
                    _pathAtlas.CleanupFrame(targetTexture.Width, targetTexture.Height);
                }

                var retriedAfterPathAtlasReset = false;
                while (true)
                {
                    try
                    {
                        RenderOffscreenCore(
                            node,
                            width,
                            height,
                            targetTexture,
                            padding,
                            dpiScale,
                            clearColor,
                            loadExistingContents,
                            includeRootTransform,
                            includeRootVisualState);
                        break;
                    }
                    catch (PathAtlasCapacityExceededException)
                    {
                        if (!ownsOffscreenFrame || retriedAfterPathAtlasReset)
                        {
                            throw;
                        }

                        retriedAfterPathAtlasReset = true;
                        _pathAtlas.ResetForRenderRetry();
                        ProGpuSceneDiagnostics.WriteLine(
                            "[Compositor] Retrying offscreen compilation after recoverable PathAtlas capacity exhaustion.");
                    }
                }
            }
            finally
            {
                _offscreenRenderDepth--;
                if (ownsOffscreenFrame)
                {
                    _frameNumber++;
                    EvictUnusedBindGroups();
                }
            }
        }
    }

    private void RenderOffscreenCore(
        Visual node,
        uint width,
        uint height,
        GpuTexture targetTexture,
        float padding,
        float dpiScale,
        Vector4? clearColor,
        bool loadExistingContents,
        bool includeRootTransform,
        bool includeRootVisualState)
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
        var savedVectorVertices = RentListSnapshot(_vectorVerticesList, out var savedVectorVerticesCount);
        var savedVectorIndices = RentListSnapshot(_vectorIndicesList, out var savedVectorIndicesCount);
        var savedTextVertices = RentListSnapshot(_textVerticesList, out var savedTextVerticesCount);
        var savedTextureVertices = RentListSnapshot(_textureVerticesList, out var savedTextureVerticesCount);
        var savedTextureIndices = RentListSnapshot(_textureIndicesList, out var savedTextureIndicesCount);
        var savedDrawCalls = RentListSnapshot(_drawCalls, out var savedDrawCallsCount);
        var savedActiveBrushes = RentListSnapshot(_activeBrushes, out var savedActiveBrushesCount);
        var savedActiveGradientStops = RentListSnapshot(_activeGradientStops, out var savedActiveGradientStopsCount);
        var savedClipStack = RentStackSnapshot(_clipStack, out var savedClipStackCount);
        var savedClipScopeIsGeometryMask = RentStackSnapshot(_clipScopeIsGeometryMask, out var savedClipScopeIsGeometryMaskCount);
        var savedActiveClipRect = _activeClipRect;
        var savedOpacityStack = RentStackSnapshot(_opacityStack, out var savedOpacityStackCount);
        var savedActiveOpacity = _activeOpacity;
        var savedPendingVectorStart = _pendingVectorStart;
        var savedPendingTextStart = _pendingTextStart;
        var savedCurrentBatchType = _currentBatchType;

        var savedUseGpuTransformsActive = _useGpuTransformsActive;
        var savedCameraViewMatrix = _cameraViewMatrix;
        var savedHasGpuTransformsInFrame = _hasGpuTransformsInFrame;
        var savedGpuTransformsCameraView = _gpuTransformsCameraView;

        var savedBlendModeStack = RentStackSnapshot(_blendModeStack, out var savedBlendModeStackCount);
        var savedActiveBlendMode = _activeBlendMode;
        var savedMaskStack = RentStackSnapshot(_maskStack, out var savedMaskStackCount);
        var savedMaskRenderPasses = RentListSnapshot(_maskRenderPasses, out var savedMaskRenderPassesCount);
        var savedMasksToReturnToPool = RentListSnapshot(_masksToReturnToPool, out var savedMasksToReturnToPoolCount);
        var savedSuspendHitTestCacheWrites = _suspendHitTestCacheWrites;

        _useGpuTransformsActive = false;
        _cameraViewMatrix = Matrix4x4.Identity;
        _hasGpuTransformsInFrame = false;
        _gpuTransformsCameraView = Matrix4x4.Identity;
        _suspendHitTestCacheWrites = true;

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
        ReturnMaskRenderPassDrawCallLists();
        _masksToReturnToPool.Clear();

        _pendingVectorStart = 0;
        _pendingTextStart = 0;

        var extensionFrame = BeginExtensionFrame();
        var pathAtlasGenerationAtCompilationStart = _pathAtlas.Generation;
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

        if (_pathAtlas.CapacityExceeded ||
            _pathAtlas.Generation != pathAtlasGenerationAtCompilationStart)
        {
            throw new PathAtlasCapacityExceededException(_pathAtlas);
        }

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
        encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
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

        var pass = _context.Api.CommandEncoderBeginRenderPass(encoder, &passDesc);
        ApplyRenderPassViewport(pass, targetTexture.Width, targetTexture.Height, useRenderTargetViewport: false);
        var currentRenderTarget = targetTexture;

        DrawCallType? currentType = null;
        GpuBlendMode? currentBlendMode = null;
        GpuTexture? currentMaskTexture = null;
        var textureEntries = stackalloc BindGroupEntry[2];

        var drawCallCount = _drawCalls.Count;
        for (var drawCallIndex = 0; drawCallIndex < drawCallCount; drawCallIndex++)
        {
            var dc = _drawCalls[drawCallIndex];
            if (CanEncodeAdvancedBlend(in dc, targetTexture))
            {
                _context.Api.RenderPassEncoderEnd(pass);
                _context.Api.RenderPassEncoderRelease(pass);

                EnsureAdvancedBlendResources(
                    targetTexture.Width,
                    targetTexture.Height,
                    targetTexture.Format);
                var output = ReferenceEquals(currentRenderTarget, targetTexture)
                    ? _advancedBlendScratchTexture!
                    : targetTexture;
                EncodeAdvancedBlendSource(
                    encoder,
                    _advancedBlendSourceTexture!,
                    in dc);
                EncodeAdvancedBlendFullscreen(
                    encoder,
                    currentRenderTarget,
                    _advancedBlendSourceTexture!,
                    output,
                    dc.BlendMode);
                currentRenderTarget = output;

                pass = BeginOffscreenTexturePass(
                    encoder,
                    currentRenderTarget,
                    LoadOp.Load,
                    new Color());
                currentType = null;
                currentBlendMode = null;
                currentMaskTexture = null;
                continue;
            }

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
                    _context.Api.RenderPassEncoderSetPipeline(pass, activePipeline);
                    fixed (BindGroup** pGrp = &_vectorUniformBindGroupOffscreen)
                    {
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroupOffscreen)
                    {
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                    }
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentType = DrawCallType.Vector;
                    currentBlendMode = dc.BlendMode;
                    currentMaskTexture = dc.MaskTexture;
                }
                else if (currentMaskTexture != dc.MaskTexture)
                {
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                var buffer = _vectorVertexBuffer.BufferPtr;
                _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _vectorVertexBuffer.Size);
                _context.Api.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _vectorIndexBuffer.Size);
                _context.Api.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Text)
            {
                var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: true);
                var maskBindGroup = GetMaskBindGroup(dc.MaskTexture, isOffscreen: true);

                if (currentType != DrawCallType.Text || currentBlendMode != dc.BlendMode)
                {
                    _context.Api.RenderPassEncoderSetPipeline(pass, activePipeline);
                    fixed (BindGroup** pGrp = &_textUniformBindGroupOffscreen)
                    {
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    fixed (BindGroup** pAtlas = &_atlasBindGroupOffscreen)
                    {
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
                    }
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentType = DrawCallType.Text;
                    currentBlendMode = dc.BlendMode;
                    currentMaskTexture = dc.MaskTexture;
                }
                else if (currentMaskTexture != dc.MaskTexture)
                {
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                var buffer = _textVertexBuffer.BufferPtr;
                _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textVertexBuffer.Size);
                _context.Api.RenderPassEncoderDraw(pass, 6, dc.IndexCount, 0, dc.IndexStart);
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

                _context.Api.RenderPassEncoderSetPipeline(pass, activePipeline);
                fixed (BindGroup** pGrp = &_textureUniformBindGroupOffscreen)
                {
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                }
                var buffer = _textureVertexBuffer.BufferPtr;
                _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textureVertexBuffer.Size);
                _context.Api.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textureIndexBuffer.Size);
                if (currentMaskTexture != dc.MaskTexture || currentType != DrawCallType.Texture || currentBlendMode != dc.BlendMode)
                {
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                    currentMaskTexture = dc.MaskTexture;
                }

                currentType = DrawCallType.Texture;
                currentBlendMode = dc.BlendMode;

                var viewPtr = texture.ViewPtr;
                var cacheKey = new TextureCacheKey(
                    texture.Id,
                    texture.Generation,
                    isOffscreen: true,
                    dc.TextureSamplingMode,
                    dc.TextureMaxAnisotropy);

                CachedBindGroup? cachedBg;
                lock (_persistentTextureBindGroups)
                {
                    if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out cachedBg))
                    {
                        textureEntries[0] = new BindGroupEntry
                        {
                            Binding = 0,
                            Sampler = GetTextureSampler(dc.TextureSamplingMode, dc.TextureMaxAnisotropy)
                        };
                        textureEntries[1] = new BindGroupEntry { Binding = 1, TextureView = viewPtr };

                        var bgDesc = new BindGroupDescriptor { Layout = _textureBindGroupLayoutOffscreen, EntryCount = 2, Entries = textureEntries };
                        var bg = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDesc);
                        cachedBg = new CachedBindGroup((nint)bg, _frameNumber);
                        _persistentTextureBindGroups[cacheKey] = cachedBg;
                    }
                    else
                    {
                        cachedBg.LastUsedFrame = _frameNumber;
                    }
                }

                var bindGroup = (BindGroup*)cachedBg.BindGroupPtr;
                _context.Api.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);
                _context.Api.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
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
                            _context.Api.RenderPassEncoderSetPipeline(pass, splinePipeline);
                            fixed (BindGroup** pGrp = &_vectorUniformBindGroupOffscreen)
                            {
                                _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                            }
                            fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroupOffscreen)
                            {
                                _context.Api.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                            }
                            currentType = DrawCallType.Vector;
                            currentBlendMode = dc.BlendMode;
                        }

                        if (selectedVectorPipeline || currentMaskTexture != dc.MaskTexture)
                        {
                            _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);
                            currentMaskTexture = dc.MaskTexture;
                        }

                        if (dc.PointBufferCount > 0)
                        {
                            _context.Api.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
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

        _context.Api.RenderPassEncoderEnd(pass);
        _context.Api.RenderPassEncoderRelease(pass);
        if (!ReferenceEquals(currentRenderTarget, targetTexture))
        {
            EncodeAdvancedBlendFullscreen(
                encoder,
                currentRenderTarget,
                currentRenderTarget,
                targetTexture,
                GpuBlendMode.Src);
        }
        EndExtensionFrame(extensionFrame);
        extensionFrameEnded = true;

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Offscreen Compositor Command Buffer") };
        var cmdBuffer = _context.Api.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Api.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Api.CommandBufferRelease(cmdBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        targetTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;
        targetTexture.MarkContentsDirty();

        ReturnPendingMaskTexturesToPool();

        EvictUnusedBindGroups();
        }
        catch (PathAtlasCapacityExceededException)
        {
            ReturnPendingMaskTexturesToPool();
            throw;
        }
        finally
        {
            if (!extensionFrameEnded)
            {
                EndExtensionFrame(extensionFrame);
            }

            // Restore main lists and state
            RestoreList(_vectorVerticesList, savedVectorVertices, savedVectorVerticesCount);
            RestoreList(_vectorIndicesList, savedVectorIndices, savedVectorIndicesCount);
            RestoreList(_textVerticesList, savedTextVertices, savedTextVerticesCount);
            RestoreList(_textureVerticesList, savedTextureVertices, savedTextureVerticesCount);
            RestoreList(_textureIndicesList, savedTextureIndices, savedTextureIndicesCount);
            RestoreList(_drawCalls, savedDrawCalls, savedDrawCallsCount);
            RestoreList(_activeBrushes, savedActiveBrushes, savedActiveBrushesCount);
            RestoreList(_activeGradientStops, savedActiveGradientStops, savedActiveGradientStopsCount);
            RestoreStack(ref _clipStack, savedClipStack, savedClipStackCount);
            RestoreClipScopeStack(savedClipScopeIsGeometryMask, savedClipScopeIsGeometryMaskCount);
            _activeClipRect = savedActiveClipRect;

            RestoreStack(ref _opacityStack, savedOpacityStack, savedOpacityStackCount);
            _activeOpacity = savedActiveOpacity;

            RestoreStack(ref _blendModeStack, savedBlendModeStack, savedBlendModeStackCount);
            _activeBlendMode = savedActiveBlendMode;

            RestoreStack(ref _maskStack, savedMaskStack, savedMaskStackCount);

            RestoreList(_maskRenderPasses, savedMaskRenderPasses, savedMaskRenderPassesCount);

            RestoreList(_masksToReturnToPool, savedMasksToReturnToPool, savedMasksToReturnToPoolCount);

            _pendingVectorStart = savedPendingVectorStart;
            _pendingTextStart = savedPendingTextStart;
            _currentBatchType = savedCurrentBatchType;

            _useGpuTransformsActive = savedUseGpuTransformsActive;
            _cameraViewMatrix = savedCameraViewMatrix;
            _hasGpuTransformsInFrame = savedHasGpuTransformsInFrame;
            _gpuTransformsCameraView = savedGpuTransformsCameraView;
            _suspendHitTestCacheWrites = savedSuspendHitTestCacheWrites;

            _currentWidth = savedWidth;
            _currentHeight = savedHeight;
            _currentDpiScale = savedDpiScale;
            _explicitRenderTargetWidth = savedExplicitRenderTargetWidth;
            _explicitRenderTargetHeight = savedExplicitRenderTargetHeight;
            _explicitRenderTargetViewport = savedExplicitRenderTargetViewport;
            _explicitDpiScale = savedExplicitDpiScale;
            _currentProjection = savedProjection;

            ReturnStackSnapshot(savedClipStack, savedClipStackCount);
            ReturnStackSnapshot(savedClipScopeIsGeometryMask, savedClipScopeIsGeometryMaskCount);
            ReturnStackSnapshot(savedOpacityStack, savedOpacityStackCount);
            ReturnStackSnapshot(savedBlendModeStack, savedBlendModeStackCount);
            ReturnStackSnapshot(savedMaskStack, savedMaskStackCount);
            ReturnListSnapshot(savedVectorVertices, savedVectorVerticesCount);
            ReturnListSnapshot(savedVectorIndices, savedVectorIndicesCount);
            ReturnListSnapshot(savedTextVertices, savedTextVerticesCount);
            ReturnListSnapshot(savedTextureVertices, savedTextureVerticesCount);
            ReturnListSnapshot(savedTextureIndices, savedTextureIndicesCount);
            ReturnListSnapshot(savedDrawCalls, savedDrawCallsCount);
            ReturnListSnapshot(savedActiveBrushes, savedActiveBrushesCount);
            ReturnListSnapshot(savedActiveGradientStops, savedActiveGradientStopsCount);
            ReturnListSnapshot(savedMaskRenderPasses, savedMaskRenderPassesCount);
            ReturnListSnapshot(savedMasksToReturnToPool, savedMasksToReturnToPoolCount);
        }
    }

    public DxfStaticBuffer CompileStaticDxf(List<RenderCommand> commands, float staticZoom = 1.0f)
    {
        // Save current lists and states
        var dxfSavedVectorVertices = RentListSnapshot(_vectorVerticesList, out var dxfSavedVectorVerticesCount);
        var dxfSavedVectorIndices = RentListSnapshot(_vectorIndicesList, out var dxfSavedVectorIndicesCount);
        var dxfSavedTextVertices = RentListSnapshot(_textVerticesList, out var dxfSavedTextVerticesCount);
        var dxfSavedTextureVertices = RentListSnapshot(_textureVerticesList, out var dxfSavedTextureVerticesCount);
        var dxfSavedTextureIndices = RentListSnapshot(_textureIndicesList, out var dxfSavedTextureIndicesCount);
        var dxfSavedDrawCalls = RentListSnapshot(_drawCalls, out var dxfSavedDrawCallsCount);
        var dxfSavedActiveBrushes = RentListSnapshot(_activeBrushes, out var dxfSavedActiveBrushesCount);
        var dxfSavedActiveGradientStops = RentListSnapshot(_activeGradientStops, out var dxfSavedActiveGradientStopsCount);
        var dxfSavedCompiledTextRecords = RentListSnapshot(_compiledTextRecords, out var dxfSavedCompiledTextRecordsCount);

        var dxfSavedActiveClipRect = _activeClipRect;
        var dxfSavedClipStack = RentStackSnapshot(_clipStack, out var dxfSavedClipStackCount);
        var dxfSavedClipScopeIsGeometryMask = RentStackSnapshot(_clipScopeIsGeometryMask, out var dxfSavedClipScopeIsGeometryMaskCount);

        var dxfSavedOpacityStack = RentStackSnapshot(_opacityStack, out var dxfSavedOpacityStackCount);
        var dxfSavedActiveOpacity = _activeOpacity;

        var dxfSavedPendingVectorStart = _pendingVectorStart;
        var dxfSavedPendingTextStart = _pendingTextStart;
        var dxfSavedCurrentBatchType = _currentBatchType;

        var dxfSavedUseGpuTransformsActive = _useGpuTransformsActive;
        var dxfSavedCameraViewMatrix = _cameraViewMatrix;
        var dxfSavedHasGpuTransformsInFrame = _hasGpuTransformsInFrame;
        var dxfSavedGpuTransformsCameraView = _gpuTransformsCameraView;

        var dxfSavedBlendModeStack = RentStackSnapshot(_blendModeStack, out var dxfSavedBlendModeStackCount);
        var dxfSavedActiveBlendMode = _activeBlendMode;
        var dxfSavedMaskStack = RentStackSnapshot(_maskStack, out var dxfSavedMaskStackCount);
        var dxfSavedMaskRenderPasses = RentListSnapshot(_maskRenderPasses, out var dxfSavedMaskRenderPassesCount);
        var dxfSavedMasksToReturnToPool = RentListSnapshot(_masksToReturnToPool, out var dxfSavedMasksToReturnToPoolCount);

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
            var extensionCount = _registeredExtensions.Count;
            for (var extensionIndex = 0; extensionIndex < extensionCount; extensionIndex++)
            {
                var ext = _registeredExtensions[extensionIndex];
                ext.BeginStaticCompile(this, ActiveCompilationContext);
            }
        }

        List<CompositorDrawCall>? staticDrawCalls = null;
        try
        {
            _atlas.BeginBatch();
            var staticDrawCallList = RentDrawCallList(commands.Count);
            staticDrawCalls = staticDrawCallList;
            uint pendingVectorStart = 0;
            uint pendingTextStart = 0;

            void CommitStaticDrawCalls()
            {
                uint vecCount = (uint)_vectorIndicesList.Count - pendingVectorStart;
                if (vecCount > 0)
                {
                    staticDrawCallList.Add(new CompositorDrawCall
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
                    staticDrawCallList.Add(new CompositorDrawCall
                    {
                        Type = DrawCallType.Text,
                        IndexStart = pendingTextStart,
                        IndexCount = textCount,
                    });
                    pendingTextStart = (uint)_textVerticesList.Count;
                }
            }

            var commandCount = commands.Count;
            for (var commandIndex = 0; commandIndex < commandCount; commandIndex++)
            {
                var cmd = commands[commandIndex];
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
                                staticDrawCallList.Add(new CompositorDrawCall
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
                                pipeline.Compile(this, null, localCmd.Transform, ref localCmd);
                                staticDrawCallList.Add(new CompositorDrawCall
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
                                staticDrawCallList.Add(new CompositorDrawCall
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
                                staticDrawCallList.Add(new CompositorDrawCall
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
                                var compileTransform = localCmd.ExtensionId == CompositorBuiltInExtensions.AcisSolid
                                    ? localCmd.Transform
                                    : Matrix4x4.Identity;
                                pipeline.Compile(this, null, compileTransform, ref localCmd);
                                var cmdTransform = localCmd.Transform;
                                if (cmdTransform == default || cmdTransform == new Matrix4x4())
                                {
                                    cmdTransform = Matrix4x4.Identity;
                                }
                                staticDrawCallList.Add(new CompositorDrawCall
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
                    case RenderCommandType.DrawVertexMesh:
                        CompileVertexMeshCommand(cmd, Matrix4x4.Identity);
                        break;
                    case RenderCommandType.DrawPointBatch:
                        CompilePointBatchCommand(cmd, Matrix4x4.Identity);
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
                staticDrawCallList.ToArray()
            );

            staticBuffer.TextRecords = _compiledTextRecords.ToArray();

            lock (_registeredExtensions)
            {
                var extensionCount = _registeredExtensions.Count;
                for (var extensionIndex = 0; extensionIndex < extensionCount; extensionIndex++)
                {
                    var ext = _registeredExtensions[extensionIndex];
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
            RestoreList(_vectorVerticesList, dxfSavedVectorVertices, dxfSavedVectorVerticesCount);
            RestoreList(_vectorIndicesList, dxfSavedVectorIndices, dxfSavedVectorIndicesCount);
            RestoreList(_textVerticesList, dxfSavedTextVertices, dxfSavedTextVerticesCount);
            RestoreList(_textureVerticesList, dxfSavedTextureVertices, dxfSavedTextureVerticesCount);
            RestoreList(_textureIndicesList, dxfSavedTextureIndices, dxfSavedTextureIndicesCount);
            RestoreList(_drawCalls, dxfSavedDrawCalls, dxfSavedDrawCallsCount);
            RestoreList(_activeBrushes, dxfSavedActiveBrushes, dxfSavedActiveBrushesCount);
            RestoreList(_activeGradientStops, dxfSavedActiveGradientStops, dxfSavedActiveGradientStopsCount);
            RestoreList(_compiledTextRecords, dxfSavedCompiledTextRecords, dxfSavedCompiledTextRecordsCount);

            _activeClipRect = dxfSavedActiveClipRect;
            RestoreStack(ref _clipStack, dxfSavedClipStack, dxfSavedClipStackCount);
            RestoreClipScopeStack(dxfSavedClipScopeIsGeometryMask, dxfSavedClipScopeIsGeometryMaskCount);

            RestoreStack(ref _opacityStack, dxfSavedOpacityStack, dxfSavedOpacityStackCount);
            _activeOpacity = dxfSavedActiveOpacity;

            _pendingVectorStart = dxfSavedPendingVectorStart;
            _pendingTextStart = dxfSavedPendingTextStart;
            _currentBatchType = dxfSavedCurrentBatchType;

            _useGpuTransformsActive = dxfSavedUseGpuTransformsActive;
            _cameraViewMatrix = dxfSavedCameraViewMatrix;
            _hasGpuTransformsInFrame = dxfSavedHasGpuTransformsInFrame;
            _gpuTransformsCameraView = dxfSavedGpuTransformsCameraView;

            RestoreStack(ref _blendModeStack, dxfSavedBlendModeStack, dxfSavedBlendModeStackCount);
            _activeBlendMode = dxfSavedActiveBlendMode;

            RestoreStack(ref _maskStack, dxfSavedMaskStack, dxfSavedMaskStackCount);

            ReturnMaskRenderPassDrawCallLists();
            RestoreList(_maskRenderPasses, dxfSavedMaskRenderPasses, dxfSavedMaskRenderPassesCount);

            RestoreList(_masksToReturnToPool, dxfSavedMasksToReturnToPool, dxfSavedMasksToReturnToPoolCount);

            ReturnStackSnapshot(dxfSavedClipStack, dxfSavedClipStackCount);
            ReturnStackSnapshot(dxfSavedClipScopeIsGeometryMask, dxfSavedClipScopeIsGeometryMaskCount);
            ReturnStackSnapshot(dxfSavedOpacityStack, dxfSavedOpacityStackCount);
            ReturnStackSnapshot(dxfSavedBlendModeStack, dxfSavedBlendModeStackCount);
            ReturnStackSnapshot(dxfSavedMaskStack, dxfSavedMaskStackCount);
            ReturnListSnapshot(dxfSavedVectorVertices, dxfSavedVectorVerticesCount);
            ReturnListSnapshot(dxfSavedVectorIndices, dxfSavedVectorIndicesCount);
            ReturnListSnapshot(dxfSavedTextVertices, dxfSavedTextVerticesCount);
            ReturnListSnapshot(dxfSavedTextureVertices, dxfSavedTextureVerticesCount);
            ReturnListSnapshot(dxfSavedTextureIndices, dxfSavedTextureIndicesCount);
            ReturnListSnapshot(dxfSavedDrawCalls, dxfSavedDrawCallsCount);
            ReturnListSnapshot(dxfSavedActiveBrushes, dxfSavedActiveBrushesCount);
            ReturnListSnapshot(dxfSavedActiveGradientStops, dxfSavedActiveGradientStopsCount);
            ReturnListSnapshot(dxfSavedCompiledTextRecords, dxfSavedCompiledTextRecordsCount);
            ReturnListSnapshot(dxfSavedMaskRenderPasses, dxfSavedMaskRenderPassesCount);
            ReturnListSnapshot(dxfSavedMasksToReturnToPool, dxfSavedMasksToReturnToPoolCount);
            if (staticDrawCalls != null)
            {
                ReturnDrawCallList(staticDrawCalls);
            }
        }
    }

    public DxfStaticBuffer CompileStaticDxf(DrawingContext context, float staticZoom = 1.0f)
    {
        // Save current lists and states
        var dxfSavedVectorVertices = RentListSnapshot(_vectorVerticesList, out var dxfSavedVectorVerticesCount);
        var dxfSavedVectorIndices = RentListSnapshot(_vectorIndicesList, out var dxfSavedVectorIndicesCount);
        var dxfSavedTextVertices = RentListSnapshot(_textVerticesList, out var dxfSavedTextVerticesCount);
        var dxfSavedTextureVertices = RentListSnapshot(_textureVerticesList, out var dxfSavedTextureVerticesCount);
        var dxfSavedTextureIndices = RentListSnapshot(_textureIndicesList, out var dxfSavedTextureIndicesCount);
        var dxfSavedDrawCalls = RentListSnapshot(_drawCalls, out var dxfSavedDrawCallsCount);
        var dxfSavedActiveBrushes = RentListSnapshot(_activeBrushes, out var dxfSavedActiveBrushesCount);
        var dxfSavedActiveGradientStops = RentListSnapshot(_activeGradientStops, out var dxfSavedActiveGradientStopsCount);
        var dxfSavedCompiledTextRecords = RentListSnapshot(_compiledTextRecords, out var dxfSavedCompiledTextRecordsCount);

        var dxfSavedActiveClipRect = _activeClipRect;
        var dxfSavedClipStack = RentStackSnapshot(_clipStack, out var dxfSavedClipStackCount);
        var dxfSavedClipScopeIsGeometryMask = RentStackSnapshot(_clipScopeIsGeometryMask, out var dxfSavedClipScopeIsGeometryMaskCount);

        var dxfSavedOpacityStack = RentStackSnapshot(_opacityStack, out var dxfSavedOpacityStackCount);
        var dxfSavedActiveOpacity = _activeOpacity;

        var dxfSavedPendingVectorStart = _pendingVectorStart;
        var dxfSavedPendingTextStart = _pendingTextStart;
        var dxfSavedCurrentBatchType = _currentBatchType;

        var dxfSavedUseGpuTransformsActive = _useGpuTransformsActive;
        var dxfSavedCameraViewMatrix = _cameraViewMatrix;
        var dxfSavedHasGpuTransformsInFrame = _hasGpuTransformsInFrame;
        var dxfSavedGpuTransformsCameraView = _gpuTransformsCameraView;

        var dxfSavedBlendModeStack = RentStackSnapshot(_blendModeStack, out var dxfSavedBlendModeStackCount);
        var dxfSavedActiveBlendMode = _activeBlendMode;
        var dxfSavedMaskStack = RentStackSnapshot(_maskStack, out var dxfSavedMaskStackCount);
        var dxfSavedMaskRenderPasses = RentListSnapshot(_maskRenderPasses, out var dxfSavedMaskRenderPassesCount);
        var dxfSavedMasksToReturnToPool = RentListSnapshot(_masksToReturnToPool, out var dxfSavedMasksToReturnToPoolCount);

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
            var extensionCount = _registeredExtensions.Count;
            for (var extensionIndex = 0; extensionIndex < extensionCount; extensionIndex++)
            {
                var ext = _registeredExtensions[extensionIndex];
                ext.BeginStaticCompile(this, ActiveCompilationContext);
            }
        }

        List<CompositorDrawCall>? staticDrawCalls = null;
        try
        {
            _atlas.BeginBatch();
            var staticDrawCallList = RentDrawCallList(context.Commands.Count);
            staticDrawCalls = staticDrawCallList;
            uint pendingVectorStart = 0;
            uint pendingTextStart = 0;

            void CommitStaticDrawCalls()
            {
                uint vecCount = (uint)_vectorIndicesList.Count - pendingVectorStart;
                if (vecCount > 0)
                {
                    staticDrawCallList.Add(new CompositorDrawCall
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
                    staticDrawCallList.Add(new CompositorDrawCall
                    {
                        Type = DrawCallType.Text,
                        IndexStart = pendingTextStart,
                        IndexCount = textCount,
                    });
                    pendingTextStart = (uint)_textVerticesList.Count;
                }
            }

            var commands = context.Commands;
            var commandCount = commands.Count;
            for (var commandIndex = 0; commandIndex < commandCount; commandIndex++)
            {
                var cmd = commands[commandIndex];
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
                                staticDrawCallList.Add(new CompositorDrawCall
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
                                pipeline.Compile(this, context, localCmd.Transform, ref localCmd);
                                staticDrawCallList.Add(new CompositorDrawCall
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
                                staticDrawCallList.Add(new CompositorDrawCall
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
                                staticDrawCallList.Add(new CompositorDrawCall
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
                                var compileTransform = localCmd.ExtensionId == CompositorBuiltInExtensions.AcisSolid
                                    ? localCmd.Transform
                                    : Matrix4x4.Identity;
                                pipeline.Compile(this, context, compileTransform, ref localCmd);
                                var cmdTransform = localCmd.Transform;
                                if (cmdTransform == default || cmdTransform == new Matrix4x4())
                                {
                                    cmdTransform = Matrix4x4.Identity;
                                }
                                staticDrawCallList.Add(new CompositorDrawCall
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
                    case RenderCommandType.DrawVertexMesh:
                        CompileVertexMeshCommand(cmd, Matrix4x4.Identity);
                        break;
                    case RenderCommandType.DrawPointBatch:
                        CompilePointBatchCommand(cmd, Matrix4x4.Identity);
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
                                staticDrawCallList.Add(new CompositorDrawCall
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
                                staticDrawCallList.Add(new CompositorDrawCall
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
                        CompilePicture(cmd.Picture, Matrix4x4.Identity);
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
                staticDrawCallList.ToArray()
            );

            staticBuffer.TextRecords = _compiledTextRecords.ToArray();

            lock (_registeredExtensions)
            {
                var extensionCount = _registeredExtensions.Count;
                for (var extensionIndex = 0; extensionIndex < extensionCount; extensionIndex++)
                {
                    var ext = _registeredExtensions[extensionIndex];
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
            RestoreList(_vectorVerticesList, dxfSavedVectorVertices, dxfSavedVectorVerticesCount);
            RestoreList(_vectorIndicesList, dxfSavedVectorIndices, dxfSavedVectorIndicesCount);
            RestoreList(_textVerticesList, dxfSavedTextVertices, dxfSavedTextVerticesCount);
            RestoreList(_textureVerticesList, dxfSavedTextureVertices, dxfSavedTextureVerticesCount);
            RestoreList(_textureIndicesList, dxfSavedTextureIndices, dxfSavedTextureIndicesCount);
            RestoreList(_drawCalls, dxfSavedDrawCalls, dxfSavedDrawCallsCount);
            RestoreList(_activeBrushes, dxfSavedActiveBrushes, dxfSavedActiveBrushesCount);
            RestoreList(_activeGradientStops, dxfSavedActiveGradientStops, dxfSavedActiveGradientStopsCount);
            RestoreList(_compiledTextRecords, dxfSavedCompiledTextRecords, dxfSavedCompiledTextRecordsCount);

            _activeClipRect = dxfSavedActiveClipRect;
            RestoreStack(ref _clipStack, dxfSavedClipStack, dxfSavedClipStackCount);
            RestoreClipScopeStack(dxfSavedClipScopeIsGeometryMask, dxfSavedClipScopeIsGeometryMaskCount);

            RestoreStack(ref _opacityStack, dxfSavedOpacityStack, dxfSavedOpacityStackCount);
            _activeOpacity = dxfSavedActiveOpacity;

            _pendingVectorStart = dxfSavedPendingVectorStart;
            _pendingTextStart = dxfSavedPendingTextStart;
            _currentBatchType = dxfSavedCurrentBatchType;

            _useGpuTransformsActive = dxfSavedUseGpuTransformsActive;
            _cameraViewMatrix = dxfSavedCameraViewMatrix;
            _hasGpuTransformsInFrame = dxfSavedHasGpuTransformsInFrame;
            _gpuTransformsCameraView = dxfSavedGpuTransformsCameraView;

            RestoreStack(ref _blendModeStack, dxfSavedBlendModeStack, dxfSavedBlendModeStackCount);
            _activeBlendMode = dxfSavedActiveBlendMode;

            RestoreStack(ref _maskStack, dxfSavedMaskStack, dxfSavedMaskStackCount);

            ReturnMaskRenderPassDrawCallLists();
            RestoreList(_maskRenderPasses, dxfSavedMaskRenderPasses, dxfSavedMaskRenderPassesCount);

            RestoreList(_masksToReturnToPool, dxfSavedMasksToReturnToPool, dxfSavedMasksToReturnToPoolCount);

            ReturnStackSnapshot(dxfSavedClipStack, dxfSavedClipStackCount);
            ReturnStackSnapshot(dxfSavedClipScopeIsGeometryMask, dxfSavedClipScopeIsGeometryMaskCount);
            ReturnStackSnapshot(dxfSavedOpacityStack, dxfSavedOpacityStackCount);
            ReturnStackSnapshot(dxfSavedBlendModeStack, dxfSavedBlendModeStackCount);
            ReturnStackSnapshot(dxfSavedMaskStack, dxfSavedMaskStackCount);
            ReturnListSnapshot(dxfSavedVectorVertices, dxfSavedVectorVerticesCount);
            ReturnListSnapshot(dxfSavedVectorIndices, dxfSavedVectorIndicesCount);
            ReturnListSnapshot(dxfSavedTextVertices, dxfSavedTextVerticesCount);
            ReturnListSnapshot(dxfSavedTextureVertices, dxfSavedTextureVerticesCount);
            ReturnListSnapshot(dxfSavedTextureIndices, dxfSavedTextureIndicesCount);
            ReturnListSnapshot(dxfSavedDrawCalls, dxfSavedDrawCallsCount);
            ReturnListSnapshot(dxfSavedActiveBrushes, dxfSavedActiveBrushesCount);
            ReturnListSnapshot(dxfSavedActiveGradientStops, dxfSavedActiveGradientStopsCount);
            ReturnListSnapshot(dxfSavedCompiledTextRecords, dxfSavedCompiledTextRecordsCount);
            ReturnListSnapshot(dxfSavedMaskRenderPasses, dxfSavedMaskRenderPassesCount);
            ReturnListSnapshot(dxfSavedMasksToReturnToPool, dxfSavedMasksToReturnToPoolCount);
            if (staticDrawCalls != null)
            {
                ReturnDrawCallList(staticDrawCalls);
            }
        }
    }

    public void RecompileStaticText(DxfStaticBuffer staticBuffer, float staticZoom)
    {
        var savedTextVertices = RentListSnapshot(_textVerticesList, out var savedTextVerticesCount);
        var savedDrawCalls = RentListSnapshot(_drawCalls, out var savedDrawCallsCount);
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
            var textRecords = staticBuffer.TextRecords;
            for (var recordIndex = 0; recordIndex < textRecords.Length; recordIndex++)
            {
                var record = textRecords[recordIndex];
                CompileTextCommand(record.Command, null, record.Transform);
            }

            for (int i = 0; i < _textVerticesList.Count; i++)
            {
                var v = _textVerticesList[i];
                v.ScaleBoldItalicUseMvp.W = ForceTextUseMvp(v.ScaleBoldItalicUseMvp.W);
                _textVerticesList[i] = v;
            }

            staticBuffer.UpdateTextBuffer(CollectionsMarshal.AsSpan(_textVerticesList));
        }
        finally
        {
            _atlas.EndBatch();
            ActiveCompilationContext = null;

            RestoreList(_textVerticesList, savedTextVertices, savedTextVerticesCount);
            RestoreList(_drawCalls, savedDrawCalls, savedDrawCallsCount);
            _pendingTextStart = savedPendingTextStart;
            _currentBatchType = savedCurrentBatchType;
            _activeOpacity = savedActiveOpacity;
            ReturnListSnapshot(savedTextVertices, savedTextVerticesCount);
            ReturnListSnapshot(savedDrawCalls, savedDrawCallsCount);
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

        var drawCalls = sb.DrawCalls;
        for (var drawCallIndex = 0; drawCallIndex < drawCalls.Length; drawCallIndex++)
        {
            var dc = drawCalls[drawCallIndex];
            if (dc.Type == DrawCallType.Vector)
            {
                if (currentType != DrawCallType.Vector)
                {
                    var pipeline = GetPipeline(DrawCallType.Vector, blendMode, isOffscreen);
                    var uniformBg = isOffscreen ? sb.UniformBindGroupOffscreen : sb.UniformBindGroup;
                    var pathAtlasBg = isOffscreen ? _pathAtlasBindGroupOffscreen : _pathAtlasBindGroup;

                    _context.Api.RenderPassEncoderSetPipeline(pass, pipeline);
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 0, uniformBg, 0, null);
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 1, pathAtlasBg, 0, null);
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBg, 0, null);

                    if (sb.VertexBuffer != null && sb.IndexBuffer != null)
                    {
                        var buffer = sb.VertexBuffer.BufferPtr;
                        _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, sb.VertexBuffer.Size);
                        _context.Api.RenderPassEncoderSetIndexBuffer(pass, sb.IndexBuffer.BufferPtr, IndexFormat.Uint32, 0, sb.IndexBuffer.Size);
                    }
                    currentType = DrawCallType.Vector;
                }
                _context.Api.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
            }
            else if (dc.Type == DrawCallType.Text)
            {
                if (currentType != DrawCallType.Text)
                {
                    var pipeline = GetPipeline(DrawCallType.Text, blendMode, isOffscreen);
                    var uniformBg = isOffscreen ? sb.TextUniformBindGroupOffscreen : sb.TextUniformBindGroup;
                    var atlasBg = isOffscreen ? _atlasBindGroupOffscreen : _atlasBindGroup;

                    _context.Api.RenderPassEncoderSetPipeline(pass, pipeline);
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 0, uniformBg, 0, null);
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 1, atlasBg, 0, null);
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBg, 0, null);

                    if (sb.TextVertexBuffer != null)
                    {
                        var buffer = sb.TextVertexBuffer.BufferPtr;
                        _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, sb.TextVertexBuffer.Size);
                    }
                    currentType = DrawCallType.Text;
                }
                _context.Api.RenderPassEncoderDraw(pass, 6, dc.IndexCount, 0, dc.IndexStart);
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

                            _context.Api.RenderPassEncoderSetPipeline(pass, vectorPipeline);
                            _context.Api.RenderPassEncoderSetBindGroup(pass, 0, uniformBg, 0, null);
                            _context.Api.RenderPassEncoderSetBindGroup(pass, 1, pathAtlasBg, 0, null);
                            _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBg, 0, null);

                            if (sb.VertexBuffer != null && sb.IndexBuffer != null)
                            {
                                var buffer = sb.VertexBuffer.BufferPtr;
                                _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, sb.VertexBuffer.Size);
                                _context.Api.RenderPassEncoderSetIndexBuffer(pass, sb.IndexBuffer.BufferPtr, IndexFormat.Uint32, 0, sb.IndexBuffer.Size);
                            }
                            currentType = DrawCallType.Vector;
                        }
                        if (dc.PointBufferCount > 0)
                        {
                            _context.Api.RenderPassEncoderDrawIndexed(pass, (uint)dc.PointBufferCount, 1, (uint)dc.PointBufferOffset, 0, 0);
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
                    cachedBuffer.Upload(cachedBuffer.CachedInterleaved.AsSpan(0, requiredLength), pointsCount);
                }
                staticBuffer = cachedBuffer;
            }
            else if (!floatsSpan.IsEmpty)
            {
                // Uncached fallback path: upload direct
                var tempBuffer = new GpuSeriesBuffer();
                tempBuffer.Upload(floatsSpan.Slice(0, pointsCount * 2), pointsCount);
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

                    cachedBuffer.Upload(cachedBuffer.CachedInterleaved.AsSpan(0, requiredLength), pointsCount);
                }
                staticBuffer = cachedBuffer;
            }
            else if (!floatsSpan.IsEmpty)
            {
                // Uncached fallback path: upload direct
                var tempBuffer = new GpuSeriesBuffer();
                bool isOriginally2D = floatsSpan.Length == pointsCount * 2;
                if (isOriginally2D)
                {
                    var array = ArrayPool<float>.Shared.Rent(pointsCount * 3);
                    float radiusVal = cmd.RadiusX;
                    int srcIdx = 0;
                    int destIdx = 0;
                    try
                    {
                        for (int i = 0; i < pointsCount; i++)
                        {
                            array[destIdx++] = floatsSpan[srcIdx++];
                            array[destIdx++] = floatsSpan[srcIdx++];
                            array[destIdx++] = radiusVal;
                        }

                        tempBuffer.Upload(array.AsSpan(0, pointsCount * 3), pointsCount);
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(array);
                    }
                }
                else
                {
                    tempBuffer.Upload(floatsSpan.Slice(0, pointsCount * 3), pointsCount);
                }
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

        var wgpu = _context.Api;
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

        var wgpu = _context.Api;
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

        var bg = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDescMask);
        if (bg == null)
        {
            throw new InvalidOperationException("Failed to create mask BindGroup");
        }

        cache[maskTexture] = (nint)bg;
        return bg;
    }

    private bool CanReuseMaskTexture(GpuTexture texture)
    {
        var textureContext = texture.Context;
        return !texture.IsDisposed
            && textureContext != null
            && ReferenceEquals(textureContext, _context)
            && texture.TexturePtr != null
            && texture.ViewPtr != null;
    }

    private static bool BlendModeRequiresPremultipliedSource(GpuBlendMode blendMode)
    {
        return blendMode is
            GpuBlendMode.SrcIn or
            GpuBlendMode.SrcOut or
            GpuBlendMode.SrcAtop or
            GpuBlendMode.DstAtop or
            GpuBlendMode.Xor or
            GpuBlendMode.DstOver or
            GpuBlendMode.Modulate or
            GpuBlendMode.Multiply or
            GpuBlendMode.Screen or
            GpuBlendMode.Darken or
            GpuBlendMode.Lighten or
            GpuBlendMode.Exclusion;
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
        uint sampleCount = isOffscreen ? 1u : Options.PrimarySampleCount;

        if (type == DrawCallType.Text)
        {
            string textBaseName = isOffscreen ? "Text_Offscreen" : "Text";
            var textShaderModule = _pipelineCache.GetOrCreateShader("Text", Shaders.TextShader, "TextShader");
            var textPipelineLayout = isOffscreen ? _textPipelineLayoutOffscreen : _textPipelineLayout;

            Span<VertexAttribute> textAttrs = stackalloc VertexAttribute[8];
            textAttrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // SnappedLogicalPos
            textAttrs[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 8, ShaderLocation = 1 }; // BasisX
            textAttrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 16, ShaderLocation = 2 }; // BasisY
            textAttrs[3] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 24, ShaderLocation = 3 }; // BearSize
            textAttrs[4] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 40, ShaderLocation = 4 }; // TexCoords
            textAttrs[5] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 56, ShaderLocation = 5 }; // Color
            textAttrs[6] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 72, ShaderLocation = 6 }; // ScaleBoldItalicUseMvp
            textAttrs[7] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 88, ShaderLocation = 7 }; // BrushIndex

            fixed (VertexAttribute* textAttribsPtr = textAttrs)
            {
                Span<VertexBufferLayout> textVertexLayouts = stackalloc VertexBufferLayout[1];
                textVertexLayouts[0] = new VertexBufferLayout
                {
                    ArrayStride = (uint)Unsafe.SizeOf<GlyphInstance>(),
                    StepMode = VertexStepMode.Instance,
                    AttributeCount = 8,
                    Attributes = textAttribsPtr
                };

                bool writesOpacityMask = overrideFormat == TextureFormat.R8Unorm;
                var textFragmentEntryPoint = GetFragmentEntryPoint(type, blendMode, GpuTextureAlphaMode.Straight, writesOpacityMask);
                var textSourceAlphaMode = GetPipelineSourceAlphaMode(type, blendMode, GpuTextureAlphaMode.Straight);
                string textFragmentKey = textFragmentEntryPoint == "fs_main" ? string.Empty : $"_{textFragmentEntryPoint}";
                string textPipelineKey = overrideFormat.HasValue
                    ? $"{textBaseName}_{blendMode}_{overrideFormat.Value}{textFragmentKey}"
                    : $"{textBaseName}_{blendMode}{textFragmentKey}";

                return _pipelineCache.GetOrCreateRenderPipeline(
                    textPipelineKey,
                    textShaderModule,
                    textVertexLayouts,
                    "vs_main",
                    textFragmentEntryPoint,
                    overrideFormat ?? RenderFormat,
                    PrimitiveTopology.TriangleList,
                    enableBlend: true,
                    enableDepthStencil: false,
                    sampleCount: sampleCount,
                    blendMode: blendMode,
                    pipelineLayout: textPipelineLayout,
                    sourceAlphaMode: textSourceAlphaMode
                );
            }
        }

        string baseName;
        ShaderModule* shaderModule;
        PipelineLayout* pipelineLayout;
        if (type == DrawCallType.Vector)
        {
            baseName = isOffscreen ? "Vector_Offscreen" : "Vector";
            shaderModule = _pipelineCache.GetOrCreateShader("Vector", Shaders.VectorShader, "VectorShader");
            pipelineLayout = isOffscreen ? _vectorPipelineLayoutOffscreen : _vectorPipelineLayout;
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

        Span<VertexAttribute> vectorAttrs = stackalloc VertexAttribute[8];
        vectorAttrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }; // Position
        vectorAttrs[1] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 8, ShaderLocation = 1 }; // Color
        vectorAttrs[2] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 24, ShaderLocation = 2 }; // TexCoord
        vectorAttrs[3] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 32, ShaderLocation = 3 }; // BrushIndex
        vectorAttrs[4] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 36, ShaderLocation = 4 }; // ShapeSize
        vectorAttrs[5] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 44, ShaderLocation = 5 }; // CornerRadius
        vectorAttrs[6] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 48, ShaderLocation = 6 }; // StrokeThickness
        vectorAttrs[7] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 52, ShaderLocation = 7 }; // ShapeType

        fixed (VertexAttribute* attribsPtr = vectorAttrs)
        {
            Span<VertexBufferLayout> vertexLayouts = stackalloc VertexBufferLayout[1];
            vertexLayouts[0] = new VertexBufferLayout
            {
                ArrayStride = (uint)Unsafe.SizeOf<VectorVertex>(),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 8,
                Attributes = attribsPtr
            };

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
                vertexLayouts,
                "vs_main",
                fragmentEntryPoint,
                overrideFormat ?? RenderFormat,
                PrimitiveTopology.TriangleList,
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

        int maskDrawCallCount = _drawCalls.Count - preDrawCallCount;
        var maskDrawCalls = RentMaskDrawCallList(maskDrawCallCount);
        for (int i = preDrawCallCount; i < _drawCalls.Count; i++)
        {
            maskDrawCalls.Add(_drawCalls[i]);
        }
        _drawCalls.RemoveRange(preDrawCallCount, maskDrawCallCount);

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

        int maskDrawCallCount = _drawCalls.Count - preDrawCallCount;
        var maskDrawCalls = RentMaskDrawCallList(maskDrawCallCount);
        for (int i = preDrawCallCount; i < _drawCalls.Count; i++)
        {
            maskDrawCalls.Add(_drawCalls[i]);
        }
        _drawCalls.RemoveRange(preDrawCallCount, maskDrawCallCount);

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

    private void PushOpacityMaskValue(GpuPicture picture, Rect bounds, Matrix4x4 transform)
    {
        CommitPendingDrawCalls();
        int preDrawCallCount = _drawCalls.Count;
        var savedState = ResetStateForMaskCompilation();

        try
        {
            PushClipRect(bounds, transform);
            try
            {
                CompilePicture(picture, transform);
            }
            finally
            {
                PopClipRect();
            }
            CommitPendingDrawCalls();
        }
        finally
        {
            RestoreStateAfterMaskCompilation(savedState);
        }

        int maskDrawCallCount = _drawCalls.Count - preDrawCallCount;
        var maskDrawCalls = RentMaskDrawCallList(maskDrawCallCount);
        for (int i = preDrawCallCount; i < _drawCalls.Count; i++)
        {
            maskDrawCalls.Add(_drawCalls[i]);
        }
        _drawCalls.RemoveRange(preDrawCallCount, maskDrawCallCount);

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
        var savedOpacityStack = RentStackSnapshot(_opacityStack, out var savedOpacityStackCount);
        var savedBlendModeStack = RentStackSnapshot(_blendModeStack, out var savedBlendModeStackCount);
        var savedState = new MaskCompilationState(
            _activeOpacity,
            savedOpacityStack,
            savedOpacityStackCount,
            _activeBlendMode,
            savedBlendModeStack,
            savedBlendModeStackCount);
        _activeOpacity = 1.0f;
        _opacityStack.Clear();
        _activeBlendMode = GpuBlendMode.SrcOver;
        _blendModeStack.Clear();
        return savedState;
    }

    private void RestoreStateAfterMaskCompilation(MaskCompilationState savedState)
    {
        try
        {
            _activeOpacity = savedState.ActiveOpacity;
            RestoreStack(ref _opacityStack, savedState.OpacityStack, savedState.OpacityStackCount);

            _activeBlendMode = savedState.ActiveBlendMode;
            RestoreStack(ref _blendModeStack, savedState.BlendModeStack, savedState.BlendModeStackCount);
        }
        finally
        {
            ReturnStackSnapshot(savedState.OpacityStack, savedState.OpacityStackCount);
            ReturnStackSnapshot(savedState.BlendModeStack, savedState.BlendModeStackCount);
        }
    }

    private readonly record struct MaskCompilationState(
        float ActiveOpacity,
        float[] OpacityStack,
        int OpacityStackCount,
        GpuBlendMode ActiveBlendMode,
        GpuBlendMode[] BlendModeStack,
        int BlendModeStackCount);

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

        var maskPassCount = _maskRenderPasses.Count;
        for (var maskPassIndex = 0; maskPassIndex < maskPassCount; maskPassIndex++)
        {
            var maskPass = _maskRenderPasses[maskPassIndex];
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

            var pass = _context.Api.CommandEncoderBeginRenderPass(encoder, &passDesc);
            ApplyRenderPassViewport(
                pass,
                maskPass.MaskTexture.Width,
                maskPass.MaskTexture.Height,
                useRenderTargetViewport: !isOffscreen);

            var maskBindGroup = GetMaskBindGroup(maskPass.PreviousMaskTexture, isOffscreen: true);

            DrawCallType? currentType = null;
            var textureEntries = stackalloc BindGroupEntry[2];

            var maskDrawCalls = maskPass.DrawCalls;
            var maskDrawCallCount = maskDrawCalls.Count;
            for (var drawCallIndex = 0; drawCallIndex < maskDrawCallCount; drawCallIndex++)
            {
                var dc = maskDrawCalls[drawCallIndex];
                if (!ApplyDrawCallScissor(pass, dc, useRenderTargetViewport: !isOffscreen))
                {
                    continue;
                }

                if (dc.Type == DrawCallType.Vector)
                {
                    if (currentType != DrawCallType.Vector)
                    {
                        var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: true, overrideFormat: TextureFormat.R8Unorm);
                        _context.Api.RenderPassEncoderSetPipeline(pass, activePipeline);
                        fixed (BindGroup** pGrp = &_vectorUniformBindGroupOffscreen)
                        {
                            _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                        }
                        fixed (BindGroup** pPathAtlas = &_pathAtlasBindGroupOffscreen)
                        {
                            _context.Api.RenderPassEncoderSetBindGroup(pass, 1, *pPathAtlas, 0, null);
                        }
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);

                        var buffer = _vectorVertexBuffer.BufferPtr;
                        _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _vectorVertexBuffer.Size);
                        _context.Api.RenderPassEncoderSetIndexBuffer(pass, _vectorIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _vectorIndexBuffer.Size);
                        currentType = DrawCallType.Vector;
                    }
                    _context.Api.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
                }
                else if (dc.Type == DrawCallType.Text)
                {
                    if (currentType != DrawCallType.Text)
                    {
                        var activePipeline = GetPipeline(dc.Type, dc.BlendMode, isOffscreen: true, overrideFormat: TextureFormat.R8Unorm);
                        _context.Api.RenderPassEncoderSetPipeline(pass, activePipeline);
                        fixed (BindGroup** pGrp = &_textUniformBindGroupOffscreen)
                        {
                            _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                        }
                        fixed (BindGroup** pAtlas = &_atlasBindGroupOffscreen)
                        {
                            _context.Api.RenderPassEncoderSetBindGroup(pass, 1, *pAtlas, 0, null);
                        }
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);

                        var buffer = _textVertexBuffer.BufferPtr;
                        _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textVertexBuffer.Size);
                        currentType = DrawCallType.Text;
                    }
                    _context.Api.RenderPassEncoderDraw(pass, 6, dc.IndexCount, 0, dc.IndexStart);
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
                    _context.Api.RenderPassEncoderSetPipeline(pass, activePipeline);
                    fixed (BindGroup** pGrp = &_textureUniformBindGroupOffscreen)
                    {
                        _context.Api.RenderPassEncoderSetBindGroup(pass, 0, *pGrp, 0, null);
                    }
                    var buffer = _textureVertexBuffer.BufferPtr;
                    _context.Api.RenderPassEncoderSetVertexBuffer(pass, 0, buffer, 0, _textureVertexBuffer.Size);
                    _context.Api.RenderPassEncoderSetIndexBuffer(pass, _textureIndexBuffer.BufferPtr, IndexFormat.Uint32, 0, _textureIndexBuffer.Size);
                    currentType = DrawCallType.Texture;

                    _context.Api.RenderPassEncoderSetBindGroup(pass, 2, maskBindGroup, 0, null);

                    var viewPtr = texture.ViewPtr;
                    var cacheKey = new TextureCacheKey(
                        texture.Id,
                        texture.Generation,
                        isOffscreen: true,
                        dc.TextureSamplingMode,
                        dc.TextureMaxAnisotropy);

                    CachedBindGroup? cachedBg;
                    lock (_persistentTextureBindGroups)
                    {
                        if (!_persistentTextureBindGroups.TryGetValue(cacheKey, out cachedBg))
                        {
                            textureEntries[0] = new BindGroupEntry
                            {
                                Binding = 0,
                                Sampler = GetTextureSampler(dc.TextureSamplingMode, dc.TextureMaxAnisotropy)
                            };
                            textureEntries[1] = new BindGroupEntry { Binding = 1, TextureView = viewPtr };

                            var bgDesc = new BindGroupDescriptor { Layout = _textureBindGroupLayoutOffscreen, EntryCount = 2, Entries = textureEntries };
                            var bg = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDesc);
                            cachedBg = new CachedBindGroup((nint)bg, _frameNumber);
                            _persistentTextureBindGroups[cacheKey] = cachedBg;
                        }
                        else
                        {
                            cachedBg.LastUsedFrame = _frameNumber;
                        }
                    }

                    var bindGroup = (BindGroup*)cachedBg.BindGroupPtr;
                    _context.Api.RenderPassEncoderSetBindGroup(pass, 1, bindGroup, 0, null);
                    _context.Api.RenderPassEncoderDrawIndexed(pass, dc.IndexCount, 1, dc.IndexStart, 0, 0);
                }
            }

            _context.Api.RenderPassEncoderEnd(pass);
            _context.Api.RenderPassEncoderRelease(pass);
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

        return _context.Api.DeviceCreateBindGroupLayout(_context.Device, &desc);
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

        return _context.Api.DeviceCreateBindGroupLayout(_context.Device, &desc);
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
                ViewDimension = TextureViewDimension.Dimension2D,
                Multisampled = false
            }
        };

        var desc = new BindGroupLayoutDescriptor
        {
            EntryCount = (UIntPtr)2,
            Entries = entries
        };

        return _context.Api.DeviceCreateBindGroupLayout(_context.Device, &desc);
    }
}
