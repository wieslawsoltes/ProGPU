using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Vector;
using ProGPU.Text;

namespace ProGPU.Compute;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct RayState
{
    public uint PixelCoordX;
    public uint PixelCoordY;
    public uint LeafNodeId;
    public uint ShapeId;
    public Vector4 AccumulatedColor;
    public float AccumulatedAlpha;
    private float _pad1;
    private float _pad2;
    private float _pad3;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuShapeInstance
{
    public Matrix4x4 Transform;
    public Matrix4x4 InvTransform;
    public Vector2 MinBounds;
    public Vector2 MaxBounds;
    public uint BvhRootIdx;
    public uint ShapeId;
    public uint TransformIndex;
    public uint StructPad1;
    public Vector4 Color;
    public uint IsText;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuShapeTransform
{
    public Matrix4x4 Transform;
    public Matrix4x4 InvTransform;

    public GpuShapeTransform(Matrix4x4 transform, Matrix4x4 inverse)
    {
        Transform = transform;
        InvTransform = inverse;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuGridCell
{
    public uint ShapeStartOffset;
    public uint ShapeCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuDispatchIndirectArgs
{
    public uint X;
    public uint Y;
    public uint Z;
}

public enum GpuCellShapeClass : uint
{
    Edge = 0,
    Outside = 1,
    Solid = 2
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuWavefrontUniforms
{
    public uint ScreenWidth;
    public uint ScreenHeight;
    public uint GridStride;
    public uint InstanceCount;
    public uint MaxQueueSize;
    public uint CurrentFrameIndex;
    public float FontWeightOffset;
    public float DpiScale;
    public uint CurveCount;
    public uint CoverageWordCount;
    public uint WordsPerCell;
    public uint CellCount;
    public uint PairCount;
    public uint CurveStart;
    public uint Pad1;
    public uint Pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuSortParams
{
    public uint Stage;
    public uint Step;
}

[StructLayout(LayoutKind.Sequential, Size = 256)]
public struct GpuSortParamsAligned
{
    public uint Stage;
    public uint Step;
}

public unsafe class WavefrontVectorEngine : IDisposable
{
    public const uint MaximumPortableDispatchDimension = 65535;
    public const float DeviceFlatteningTolerance = 0.125f;
    private const float MinimumCoverageScale = 1f / 65536f;
    private const float MaximumCoverageScale = 65536f;
    private const int ScaleBucketsPerOctave = 4;

    private readonly record struct PathGeometryKey(int ContentHash, int ScaleBucket);
    private readonly record struct GlyphGeometryKey(TtfFont Font, ushort GlyphId, int ScaleBucket);

    private readonly WgpuContext _context;
    private readonly RenderPipelineCache _pipelineCache;

    private ComputePipeline* _flattenPipeline;
    private ComputePipeline* _clearBinWordsPipeline;
    private ComputePipeline* _buildBinCoveragePipeline;
    private ComputePipeline* _countBinWordsPipeline;
    private ComputePipeline* _scatterBinWordsPipeline;
    private ComputePipeline* _markActiveCellsPipeline;
    private ComputePipeline* _scatterActiveCellsPipeline;
    private ComputePipeline* _finalizeActiveDispatchPipeline;
    private ComputePipeline* _classifyCellShapesPipeline;
    private ComputePipeline* _renderPipeline;
    private RenderPipeline* _compositePipeline;
    private BindGroupLayout* _flattenLayout;
    private BindGroupLayout* _clearBinWordsLayout;
    private BindGroupLayout* _buildBinCoverageLayout;
    private BindGroupLayout* _countBinWordsLayout;
    private BindGroupLayout* _scatterBinWordsLayout;
    private BindGroupLayout* _markActiveCellsLayout;
    private BindGroupLayout* _scatterActiveCellsLayout;
    private BindGroupLayout* _finalizeActiveDispatchLayout;
    private BindGroupLayout* _classifyCellShapesLayout;
    private BindGroupLayout* _renderLayout;
    private BindGroupLayout* _compositeLayout;

    // Global BVH Cache
    private readonly List<GpuBvhNode> _bvhNodes = new();
    private readonly List<GpuBezierCurve> _rawCurves = new();
    private uint _totalLineSegmentsCount = 0;
    
    // Cache maps to offsets inside _bvhNodes and _rawCurves
    private readonly Dictionary<PathGeometryKey, (uint BvhOffset, uint CurveOffset, Vector2 Min, Vector2 Max)> _pathCache = new();
    private readonly Dictionary<GlyphGeometryKey, (uint BvhOffset, uint CurveOffset, Vector2 Min, Vector2 Max)> _glyphCache = new();

    // GPU Buffers (Reallocated/resized when frame sizes grow)
    private GpuBuffer? _bvhBuffer;
    private GpuBuffer? _rawCurvesBuffer;
    private GpuBuffer? _linesBuffer;
    private GpuBuffer? _instancesBuffer;
    private GpuBuffer? _shapeTransformsBuffer;
    private GpuBuffer? _gridCellsBuffer;
    private GpuBuffer? _gridIndicesBuffer;
    private GpuBuffer? _coverageWordsBuffer;
    private GpuBuffer? _activeCellIndicesBuffer;
    private GpuBuffer? _activeDispatchBuffer;
    private GpuBuffer? _activeDrawBuffer;
    private GpuBuffer? _cellShapeClassesBuffer;
    private GpuBuffer? _uniformsBuffer;
    private readonly GpuPrefixScan _binWordScan;
    private readonly GpuPrefixScan _activeCellScan;

    private BindGroup* _flattenBindGroup;
    private BindGroup* _clearBinWordsBindGroup;
    private BindGroup* _buildBinCoverageBindGroup;
    private BindGroup* _countBinWordsBindGroup;
    private BindGroup* _scatterBinWordsBindGroup;
    private BindGroup* _markActiveCellsBindGroup;
    private BindGroup* _scatterActiveCellsBindGroup;
    private BindGroup* _finalizeActiveDispatchBindGroup;
    private BindGroup* _classifyCellShapesBindGroup;
    private BindGroup* _renderBindGroup;
    private BindGroup* _compositeBindGroup;
    private TextureView* _renderBaseView;

    private GpuTexture? _sparseOutputTexture;

    private readonly List<GpuShapeInstance> _frameInstances = new();
    private readonly List<GpuShapeTransform> _shapeTransforms =
    [
        new GpuShapeTransform(Matrix4x4.Identity, Matrix4x4.Identity)
    ];
    private readonly List<uint> _dirtyShapeTransformIndices = [0u];
    private readonly Stack<uint> _freeShapeTransformIndices = new();
    private readonly HashSet<uint> _freeShapeTransformSet = new();
    private uint _frameNumber = 0;
    private float _frameDpiScale = 1f;
    private bool _hasSparseFrameWork;
    private bool _reuseRetainedInstances;
    private int _retainedPathCount;
    private int _retainedGlyphCount;
    private int _retainedFallbackCount;
    private uint _uploadedBvhNodeCount;
    private uint _uploadedRawCurveCount;
    private bool _isDisposed;

    public uint LastUploadedBvhNodeCount { get; private set; }

    public uint LastUploadedRawCurveCount { get; private set; }

    public uint LastFlattenedCurveCount { get; private set; }

    public uint LastUploadedInstanceCount { get; private set; }

    public uint LastUploadedTransformCount { get; private set; }

    public bool LastGeometryArenaReplay { get; private set; }

    public int FramePathCount { get; private set; }

    public int FrameGlyphCount { get; private set; }

    public int FrameFallbackCount { get; private set; }

    public int FrameGeometryCacheHits { get; private set; }

    public int FrameGeometryCacheMisses { get; private set; }

    public uint RetainedLineSegmentCount => _totalLineSegmentsCount;

    public uint RetainedTransformCount => checked((uint)_shapeTransforms.Count - (uint)_freeShapeTransformIndices.Count);

    public WavefrontVectorEngine(WgpuContext context)
    {
        _context = context;
        _pipelineCache = new RenderPipelineCache(_context);
        InitializePipelines();
        _binWordScan = new GpuPrefixScan(context);
        _activeCellScan = new GpuPrefixScan(context);
    }

    private void InitializePipelines()
    {
        var shaderModule = _pipelineCache.GetOrCreateShader("WavefrontShaders", WavefrontShaders.ShadersSource, "WavefrontShaders");
        _flattenPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontFlatten", shaderModule, "flatten_curves");
        _clearBinWordsPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontBinClear", shaderModule, "clear_bin_words");
        _buildBinCoveragePipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontBinBuild", shaderModule, "build_bin_coverage");
        _countBinWordsPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontBinCount", shaderModule, "count_bin_words");
        _scatterBinWordsPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontBinScatter", shaderModule, "scatter_bin_words");
        _markActiveCellsPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontMarkActiveCells", shaderModule, "mark_active_cells");
        _scatterActiveCellsPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontScatterActiveCells", shaderModule, "scatter_active_cells");
        _finalizeActiveDispatchPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontFinalizeActiveDispatch", shaderModule, "finalize_active_dispatch");
        _classifyCellShapesPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontClassifyCellShapes", shaderModule, "classify_cell_shapes");
        _renderPipeline = _pipelineCache.GetOrCreateComputePipeline("WavefrontRender", shaderModule, "wavefront_render");
        var compositeModule = _pipelineCache.GetOrCreateShader(
            "WavefrontComposite",
            ComputeShaders.WavefrontComposite,
            "WavefrontComposite");
        _compositePipeline = _pipelineCache.GetOrCreateRenderPipeline(
            "WavefrontCompositeBgra8Unorm",
            compositeModule,
            vertexEntry: "vs_sparse_cell",
            fragmentEntry: "fs_sparse_cell",
            targetFormat: TextureFormat.Bgra8Unorm,
            topology: PrimitiveTopology.TriangleList,
            enableBlend: false);
        _flattenLayout = _context.Api.ComputePipelineGetBindGroupLayout(_flattenPipeline, 0);
        _clearBinWordsLayout = _context.Api.ComputePipelineGetBindGroupLayout(_clearBinWordsPipeline, 0);
        _buildBinCoverageLayout = _context.Api.ComputePipelineGetBindGroupLayout(_buildBinCoveragePipeline, 0);
        _countBinWordsLayout = _context.Api.ComputePipelineGetBindGroupLayout(_countBinWordsPipeline, 0);
        _scatterBinWordsLayout = _context.Api.ComputePipelineGetBindGroupLayout(_scatterBinWordsPipeline, 0);
        _markActiveCellsLayout = _context.Api.ComputePipelineGetBindGroupLayout(_markActiveCellsPipeline, 0);
        _scatterActiveCellsLayout = _context.Api.ComputePipelineGetBindGroupLayout(_scatterActiveCellsPipeline, 0);
        _finalizeActiveDispatchLayout = _context.Api.ComputePipelineGetBindGroupLayout(_finalizeActiveDispatchPipeline, 0);
        _classifyCellShapesLayout = _context.Api.ComputePipelineGetBindGroupLayout(_classifyCellShapesPipeline, 0);
        _renderLayout = _context.Api.ComputePipelineGetBindGroupLayout(_renderPipeline, 0);
        _compositeLayout = _context.Api.RenderPipelineGetBindGroupLayout(_compositePipeline, 0);
    }

    private bool TryGetOrAddPathGeometry(
        PathGeometry path,
        in Matrix4x4 transform,
        out (uint BvhOffset, uint CurveOffset, Vector2 Min, Vector2 Max) geometry)
    {
        geometry = default;
        if (!TryGetToleranceBucket(transform, 1f, out int scaleBucket, out float localTolerance))
        {
            return false;
        }

        int contentHash = PathAtlas.ComputeHash(path);
        var key = new PathGeometryKey(contentHash, scaleBucket);

        if (_pathCache.TryGetValue(key, out var cached))
        {
            FrameGeometryCacheHits++;
            geometry = cached;
            return true;
        }

        if (!BvhBuilder.TryGetPathCurves(path, out var curves))
        {
            return false;
        }
        if (!BvhBuilder.TryBuildBvh(
                curves,
                localTolerance,
                out var nodes,
                out var orderedCurves,
                out var totalLines))
        {
            return false;
        }

        uint bvhOffset = (uint)_bvhNodes.Count;
        uint curveOffset = (uint)_rawCurves.Count;
        uint lineOffset = _totalLineSegmentsCount;

        // Bounding box
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in orderedCurves)
        {
            var (cMin, cMax) = BvhBuilder.GetCurveBounds(c);
            minX = Math.Min(minX, cMin.X);
            minY = Math.Min(minY, cMin.Y);
            maxX = Math.Max(maxX, cMax.X);
            maxY = Math.Max(maxY, cMax.Y);
        }

        // Adjust left child / first line offsets relative to the global buffer
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.PrimitiveCount > 0)
            {
                node.LeftChildOrFirstLine += lineOffset;
            }
            else
            {
                node.LeftChildOrFirstLine += bvhOffset;
                node.RightChild += bvhOffset;
            }
            _bvhNodes.Add(node);
        }

        for (int i = 0; i < orderedCurves.Count; i++)
        {
            var curve = orderedCurves[i];
            curve.LineOffset += lineOffset;
            _rawCurves.Add(curve);
        }

        _totalLineSegmentsCount += totalLines;

        var boundsMin = new Vector2(minX, minY);
        var boundsMax = new Vector2(maxX, maxY);

        geometry = (bvhOffset, curveOffset, boundsMin, boundsMax);
        _pathCache[key] = geometry;
        FrameGeometryCacheMisses++;
        return true;
    }

    private bool TryGetOrAddGlyphGeometry(
        TtfFont font,
        ushort glyphId,
        in Matrix4x4 glyphTransform,
        out (uint BvhOffset, uint CurveOffset, Vector2 Min, Vector2 Max) geometry)
    {
        geometry = default;
        if (!TryGetToleranceBucket(glyphTransform, 1f, out int scaleBucket, out float localTolerance))
        {
            return false;
        }

        var key = new GlyphGeometryKey(font, glyphId, scaleBucket);
        if (_glyphCache.TryGetValue(key, out var cached))
        {
            FrameGeometryCacheHits++;
            geometry = cached;
            return true;
        }

        if (!BvhBuilder.TryGetGlyphCurves(font, glyphId, out var curves))
        {
            return false;
        }
        if (!BvhBuilder.TryBuildBvh(
                curves,
                localTolerance,
                out var nodes,
                out var orderedCurves,
                out var totalLines))
        {
            return false;
        }

        uint bvhOffset = (uint)_bvhNodes.Count;
        uint curveOffset = (uint)_rawCurves.Count;
        uint lineOffset = _totalLineSegmentsCount;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in orderedCurves)
        {
            var (cMin, cMax) = BvhBuilder.GetCurveBounds(c);
            minX = Math.Min(minX, cMin.X);
            minY = Math.Min(minY, cMin.Y);
            maxX = Math.Max(maxX, cMax.X);
            maxY = Math.Max(maxY, cMax.Y);
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.PrimitiveCount > 0)
            {
                node.LeftChildOrFirstLine += lineOffset;
            }
            else
            {
                node.LeftChildOrFirstLine += bvhOffset;
                node.RightChild += bvhOffset;
            }
            _bvhNodes.Add(node);
        }

        for (int i = 0; i < orderedCurves.Count; i++)
        {
            var curve = orderedCurves[i];
            curve.LineOffset += lineOffset;
            _rawCurves.Add(curve);
        }

        _totalLineSegmentsCount += totalLines;

        var boundsMin = new Vector2(minX, minY);
        var boundsMax = new Vector2(maxX, maxY);

        geometry = (bvhOffset, curveOffset, boundsMin, boundsMax);
        _glyphCache[key] = geometry;
        FrameGeometryCacheMisses++;
        return true;
    }

    public void BeginFrame(float dpiScale = 1f, bool reuseRetainedInstances = false)
    {
        if (!reuseRetainedInstances)
        {
            _frameInstances.Clear();
            _retainedPathCount = 0;
            _retainedGlyphCount = 0;
            _retainedFallbackCount = 0;
        }
        _frameNumber++;
        _frameDpiScale = NormalizeDpiScale(dpiScale);
        _reuseRetainedInstances = reuseRetainedInstances;
        _hasSparseFrameWork = false;
        LastUploadedBvhNodeCount = 0;
        LastUploadedRawCurveCount = 0;
        LastFlattenedCurveCount = 0;
        LastUploadedInstanceCount = 0;
        LastUploadedTransformCount = 0;
        LastGeometryArenaReplay = false;
        FramePathCount = reuseRetainedInstances ? _retainedPathCount : 0;
        FrameGlyphCount = reuseRetainedInstances ? _retainedGlyphCount : 0;
        FrameFallbackCount = reuseRetainedInstances ? _retainedFallbackCount : 0;
        FrameGeometryCacheHits = 0;
        FrameGeometryCacheMisses = 0;
    }

    /// <summary>
    /// Allocates a stable transform-table slot for retained scene placement. Slot zero is the
    /// permanent identity transform used by ordinary instances.
    /// </summary>
    public uint AllocateTransform()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        uint index;
        if (_freeShapeTransformIndices.TryPop(out uint recycled))
        {
            index = recycled;
            _freeShapeTransformSet.Remove(index);
            _shapeTransforms[(int)index] = new GpuShapeTransform(Matrix4x4.Identity, Matrix4x4.Identity);
        }
        else
        {
            index = checked((uint)_shapeTransforms.Count);
            _shapeTransforms.Add(new GpuShapeTransform(Matrix4x4.Identity, Matrix4x4.Identity));
        }
        MarkShapeTransformDirty(index);
        return index;
    }

    public void ReleaseTransform(uint transformIndex)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (transformIndex == 0 || transformIndex >= (uint)_shapeTransforms.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(transformIndex));
        }
        if (_freeShapeTransformSet.Contains(transformIndex))
        {
            throw new InvalidOperationException("Wavefront retained transform was already released.");
        }

        _shapeTransforms[(int)transformIndex] = new GpuShapeTransform(Matrix4x4.Identity, Matrix4x4.Identity);
        MarkShapeTransformDirty(transformIndex);
        _freeShapeTransformIndices.Push(transformIndex);
        _freeShapeTransformSet.Add(transformIndex);
    }

    public bool UpdateTransform(uint transformIndex, in Matrix4x4 transform)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (transformIndex >= (uint)_shapeTransforms.Count ||
            _freeShapeTransformSet.Contains(transformIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(transformIndex));
        }
        if (transformIndex == 0)
        {
            return transform == Matrix4x4.Identity;
        }
        if (!IsFinite(transform) ||
            !IsTranslationOnly2D(transform))
        {
            return false;
        }

        var current = _shapeTransforms[(int)transformIndex];
        if (current.Transform == transform)
        {
            return true;
        }

        Matrix4x4 inverse = Matrix4x4.CreateTranslation(-transform.M41, -transform.M42, 0f);
        _shapeTransforms[(int)transformIndex] = new GpuShapeTransform(transform, inverse);
        MarkShapeTransformDirty(transformIndex);
        return true;
    }

    private void MarkShapeTransformDirty(uint transformIndex)
    {
        for (int index = 0; index < _dirtyShapeTransformIndices.Count; index++)
        {
            if (_dirtyShapeTransformIndices[index] == transformIndex)
            {
                return;
            }
        }
        _dirtyShapeTransformIndices.Add(transformIndex);
    }

    public bool TryDrawPath(
        PathGeometry path,
        in Matrix4x4 transform,
        Brush brush,
        uint transformIndex = 0)
    {
        if (brush is not SolidColorBrush solid ||
            transformIndex >= (uint)_shapeTransforms.Count ||
            _freeShapeTransformSet.Contains(transformIndex) ||
            !Matrix4x4.Invert(transform, out var inv) ||
            !TryGetOrAddPathGeometry(path, transform, out var geom))
        {
            RecordFallback();
            return false;
        }

        _frameInstances.Add(new GpuShapeInstance
        {
            Transform = transform,
            InvTransform = inv,
            MinBounds = geom.Min,
            MaxBounds = geom.Max,
            BvhRootIdx = geom.BvhOffset,
            ShapeId = (uint)_frameInstances.Count,
            TransformIndex = transformIndex,
            Color = solid.Color,
            IsText = 0
        });
        FramePathCount++;
        _retainedPathCount++;
        return true;
    }

    public void DrawPath(PathGeometry path, in Matrix4x4 transform, Brush brush)
    {
        if (!TryDrawPath(path, transform, brush))
        {
            throw new InvalidOperationException(
                "Wavefront path geometry cannot satisfy the configured device-space flattening tolerance or brush contract.");
        }
    }

    public void RecordFallback()
    {
        FrameFallbackCount++;
        _retainedFallbackCount++;
    }

    public bool TryDrawGlyph(
        TtfFont font,
        ushort glyphId,
        float fontSize,
        in Matrix4x4 transform,
        Brush brush,
        uint transformIndex = 0)
    {
        if (brush is not SolidColorBrush solid ||
            transformIndex >= (uint)_shapeTransforms.Count ||
            _freeShapeTransformSet.Contains(transformIndex))
        {
            RecordFallback();
            return false;
        }

        float fontScale = fontSize / font.UnitsPerEm;

        var glyphTransform = Matrix4x4.CreateScale(fontScale, -fontScale, 1f) * transform;
        if (!Matrix4x4.Invert(glyphTransform, out var inv) ||
            !TryGetOrAddGlyphGeometry(font, glyphId, glyphTransform, out var geom))
        {
            RecordFallback();
            return false;
        }

        _frameInstances.Add(new GpuShapeInstance
        {
            Transform = glyphTransform,
            InvTransform = inv,
            MinBounds = geom.Min,
            MaxBounds = geom.Max,
            BvhRootIdx = geom.BvhOffset,
            ShapeId = (uint)_frameInstances.Count,
            TransformIndex = transformIndex,
            Color = solid.Color,
            IsText = 1
        });
        FrameGlyphCount++;
        _retainedGlyphCount++;
        return true;
    }

    public void DrawGlyph(TtfFont font, ushort glyphId, float fontSize, in Matrix4x4 transform, Brush brush)
    {
        if (!TryDrawGlyph(font, glyphId, fontSize, transform, brush))
        {
            throw new InvalidOperationException(
                "Wavefront glyph geometry cannot satisfy the configured device-space flattening tolerance or brush contract.");
        }
    }

    public void EndFrame(CommandEncoder* encoder, GpuTexture destination, float dpiScale, float fontWeightOffset = 0f)
    {
        dpiScale = NormalizeDpiScale(dpiScale);
        if (MathF.Abs(dpiScale - _frameDpiScale) > MathF.Max(dpiScale, _frameDpiScale) * 0.0001f)
        {
            throw new InvalidOperationException(
                "Wavefront BeginFrame and EndFrame must use the same DPI scale so the retained flattening tolerance remains valid.");
        }

        uint width = destination.Width;
        uint height = destination.Height;

        if (_frameInstances.Count == 0 || width == 0 || height == 0) return;
        _hasSparseFrameWork = true;

        // Build exact, uncapped cell-list capacity from the same transformed bounds used by the
        // GPU. This is O(I), performs no readback, and makes capacity failure explicit before any
        // command can observe a partial bin list.
        uint gridCols = (width + 15) / 16;
        uint gridRows = (height + 15) / 16;
        uint gridStride = gridCols;
        uint cellCount = checked(gridCols * gridRows);
        uint wordsPerCell = checked(((uint)_frameInstances.Count + 31u) / 32u);
        uint coverageWordCount = checked(cellCount * wordsPerCell);
        uint pairCount = CountCellOverlaps(width, height, dpiScale, gridStride, gridRows);

        EnsureGpuResources(
            width,
            height,
            cellCount,
            coverageWordCount,
            Math.Max(1u, pairCount),
            destination,
            out bool instancesReallocated,
            out bool transformsReallocated);

        uint curveStart = _uploadedRawCurveCount;
        uint pendingCurveCount = checked((uint)_rawCurves.Count - curveStart);
        bool geometryChanged =
            _uploadedBvhNodeCount != (uint)_bvhNodes.Count ||
            pendingCurveCount != 0;
        if (geometryChanged)
        {
            var bvhNodes = CollectionsMarshal.AsSpan(_bvhNodes);
            if (_uploadedBvhNodeCount < (uint)bvhNodes.Length)
            {
                LastUploadedBvhNodeCount = (uint)bvhNodes.Length - _uploadedBvhNodeCount;
                _bvhBuffer!.Write(
                    bvhNodes[(int)_uploadedBvhNodeCount..],
                    checked(_uploadedBvhNodeCount * (uint)sizeof(GpuBvhNode)));
            }

            var rawCurves = CollectionsMarshal.AsSpan(_rawCurves);
            if (pendingCurveCount != 0)
            {
                LastUploadedRawCurveCount = pendingCurveCount;
                _rawCurvesBuffer!.Write(
                    rawCurves[(int)curveStart..],
                    checked(curveStart * (uint)sizeof(GpuBezierCurve)));
            }
        }
        if (!_reuseRetainedInstances || instancesReallocated)
        {
            _instancesBuffer!.Write(CollectionsMarshal.AsSpan(_frameInstances));
            LastUploadedInstanceCount = checked((uint)_frameInstances.Count);
        }

        if (transformsReallocated)
        {
            _shapeTransformsBuffer!.Write(CollectionsMarshal.AsSpan(_shapeTransforms));
            LastUploadedTransformCount = checked((uint)_shapeTransforms.Count);
            _dirtyShapeTransformIndices.Clear();
        }
        else if (_dirtyShapeTransformIndices.Count != 0)
        {
            for (int index = 0; index < _dirtyShapeTransformIndices.Count; index++)
            {
                uint transformIndex = _dirtyShapeTransformIndices[index];
                _shapeTransformsBuffer!.WriteSingle(
                    _shapeTransforms[(int)transformIndex],
                    checked(transformIndex * (uint)sizeof(GpuShapeTransform)));
            }
            LastUploadedTransformCount = checked((uint)_dirtyShapeTransformIndices.Count);
            _dirtyShapeTransformIndices.Clear();
        }

        var uniforms = new GpuWavefrontUniforms
        {
            ScreenWidth = width,
            ScreenHeight = height,
            GridStride = gridStride,
            InstanceCount = (uint)_frameInstances.Count,
            MaxQueueSize = 0, // Unused
            CurrentFrameIndex = _frameNumber,
            FontWeightOffset = fontWeightOffset,
            DpiScale = dpiScale,
            CurveCount = pendingCurveCount,
            CoverageWordCount = coverageWordCount,
            WordsPerCell = wordsPerCell,
            CellCount = cellCount,
            PairCount = pairCount,
            CurveStart = curveStart
        };
        _uniformsBuffer!.WriteSingle(uniforms);

        var passDesc = new ComputePassDescriptor();

        _context.BeginGpuTimestampStage(encoder, GpuTimestampStage.WavefrontGeometry);
        if (pendingCurveCount > 0)
        {
            LastFlattenedCurveCount = pendingCurveCount;
            var passFlatten = _context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);
            _context.Api.ComputePassEncoderSetPipeline(passFlatten, _flattenPipeline);
            _context.Api.ComputePassEncoderSetBindGroup(passFlatten, 0, _flattenBindGroup, 0, null);
            _context.Api.ComputePassEncoderDispatchWorkgroups(passFlatten, DivRoundUp(pendingCurveCount, 64u), 1, 1);
            _context.Api.ComputePassEncoderEnd(passFlatten);
            _context.Api.ComputePassEncoderRelease(passFlatten);
        }
        _context.EndGpuTimestampStage(encoder, GpuTimestampStage.WavefrontGeometry);
        if (geometryChanged)
        {
            _uploadedBvhNodeCount = (uint)_bvhNodes.Count;
            _uploadedRawCurveCount = (uint)_rawCurves.Count;
        }

        // Instance-driven bitmap construction performs O(overlap) writes. Word popcount followed
        // by the reusable hierarchical scan reserves an exact, stable list for every cell.
        _context.BeginGpuTimestampStage(encoder, GpuTimestampStage.WavefrontBinning);
        var passBin = _context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Api.ComputePassEncoderSetPipeline(passBin, _clearBinWordsPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(passBin, 0, _clearBinWordsBindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(passBin, DivRoundUp(coverageWordCount, 256u), 1, 1);
        _context.Api.ComputePassEncoderSetPipeline(passBin, _buildBinCoveragePipeline);
        _context.Api.ComputePassEncoderSetBindGroup(passBin, 0, _buildBinCoverageBindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(passBin, DivRoundUp((uint)_frameInstances.Count, 64u), 1, 1);
        _context.Api.ComputePassEncoderSetPipeline(passBin, _countBinWordsPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(passBin, 0, _countBinWordsBindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(passBin, DivRoundUp(coverageWordCount, 256u), 1, 1);
        _context.Api.ComputePassEncoderEnd(passBin);
        _context.Api.ComputePassEncoderRelease(passBin);

        _binWordScan.RecordExclusiveScan(encoder, coverageWordCount);
        _context.EndGpuTimestampStage(encoder, GpuTimestampStage.WavefrontBinning);

        _context.BeginGpuTimestampStage(encoder, GpuTimestampStage.WavefrontCompaction);
        var passScatter = _context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Api.ComputePassEncoderSetPipeline(passScatter, _scatterBinWordsPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(passScatter, 0, _scatterBinWordsBindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(passScatter, DivRoundUp(coverageWordCount, 256u), 1, 1);
        _context.Api.ComputePassEncoderSetPipeline(passScatter, _markActiveCellsPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(passScatter, 0, _markActiveCellsBindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(passScatter, DivRoundUp(cellCount, 256u), 1, 1);
        _context.Api.ComputePassEncoderEnd(passScatter);
        _context.Api.ComputePassEncoderRelease(passScatter);

        _activeCellScan.RecordExclusiveScan(encoder, cellCount);

        var passActiveCells = _context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Api.ComputePassEncoderSetPipeline(passActiveCells, _scatterActiveCellsPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(passActiveCells, 0, _scatterActiveCellsBindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(passActiveCells, DivRoundUp(cellCount, 256u), 1, 1);
        _context.Api.ComputePassEncoderSetPipeline(passActiveCells, _finalizeActiveDispatchPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(passActiveCells, 0, _finalizeActiveDispatchBindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(passActiveCells, 1, 1, 1);
        _context.Api.ComputePassEncoderEnd(passActiveCells);
        _context.Api.ComputePassEncoderRelease(passActiveCells);
        _context.EndGpuTimestampStage(encoder, GpuTimestampStage.WavefrontCompaction);

        // Coarse work classifies each active cell's candidates once. Only uncertain edge pairs
        // retain per-pixel BVH traversal; conservative solid/outside pairs become constant work.
        // The exact active workgroup count is reused without CPU readback.
        _context.BeginGpuTimestampStage(encoder, GpuTimestampStage.WavefrontCoarseFine);
        var passRender = _context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Api.ComputePassEncoderSetPipeline(passRender, _classifyCellShapesPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(passRender, 0, _classifyCellShapesBindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroupsIndirect(
            passRender,
            _activeDispatchBuffer!.BufferPtr,
            0);
        _context.Api.ComputePassEncoderSetPipeline(passRender, _renderPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(passRender, 0, _renderBindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroupsIndirect(
            passRender,
            _activeDispatchBuffer!.BufferPtr,
            0);
        _context.Api.ComputePassEncoderEnd(passRender);
        _context.Api.ComputePassEncoderRelease(passRender);
        _context.EndGpuTimestampStage(encoder, GpuTimestampStage.WavefrontCoarseFine);
    }

    public static bool TryGetScaleBucket(
        float deviceScale,
        out int scaleBucket,
        out float bucketUpperScale)
    {
        scaleBucket = 0;
        bucketUpperScale = 0f;
        if (!float.IsFinite(deviceScale) ||
            deviceScale <= 0f ||
            deviceScale > MaximumCoverageScale)
        {
            return false;
        }

        deviceScale = MathF.Max(deviceScale, MinimumCoverageScale);
        scaleBucket = checked((int)Math.Ceiling(Math.Log2(deviceScale) * ScaleBucketsPerOctave));
        bucketUpperScale = MathF.Pow(2f, scaleBucket / (float)ScaleBucketsPerOctave);
        return float.IsFinite(bucketUpperScale) && bucketUpperScale >= deviceScale;
    }

    private bool TryGetToleranceBucket(
        in Matrix4x4 transform,
        float localScale,
        out int scaleBucket,
        out float localTolerance)
    {
        float deviceScale = TransformMetrics.GetStrokeScale(transform) * _frameDpiScale * localScale;
        if (!TryGetScaleBucket(deviceScale, out scaleBucket, out float bucketUpperScale))
        {
            localTolerance = 0f;
            return false;
        }

        localTolerance = DeviceFlatteningTolerance / bucketUpperScale;
        return true;
    }

    private static float NormalizeDpiScale(float dpiScale) =>
        float.IsFinite(dpiScale) && dpiScale > 0f ? dpiScale : 1f;

    private static bool IsTranslationOnly2D(in Matrix4x4 value) =>
        value.M11 == 1f && value.M12 == 0f && value.M13 == 0f && value.M14 == 0f &&
        value.M21 == 0f && value.M22 == 1f && value.M23 == 0f && value.M24 == 0f &&
        value.M31 == 0f && value.M32 == 0f && value.M33 == 1f && value.M34 == 0f &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && value.M43 == 0f && value.M44 == 1f;

    /// <summary>
    /// Replaces only compacted active tiles with the exact pixels produced by the compute stage.
    /// The caller first draws the unchanged base texture, then records this indirect draw in the
    /// same render pass. Inactive cells incur no fragment work and require no destination copy.
    /// </summary>
    public void RecordSparseComposite(RenderPassEncoder* pass)
    {
        if (!_hasSparseFrameWork || pass == null || _compositeBindGroup == null || _activeDrawBuffer == null)
        {
            return;
        }

        _context.Api.RenderPassEncoderSetPipeline(pass, _compositePipeline);
        _context.Api.RenderPassEncoderSetBindGroup(pass, 0, _compositeBindGroup, 0, null);
        _context.Api.RenderPassEncoderDrawIndirect(pass, _activeDrawBuffer.BufferPtr, 0);
    }

    private void EnsureGpuResources(
        uint width,
        uint height,
        uint cellCount,
        uint coverageWordCount,
        uint indexCount,
        GpuTexture destination,
        out bool instancesReallocated,
        out bool transformsReallocated)
    {
        bool bindingsDirty = false;
        bool bvhArenaReallocated = EnsureBuffer(ref _bvhBuffer, ByteSize(_bvhNodes.Count, sizeof(GpuBvhNode)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront BVH Nodes Buffer");
        bool rawCurveArenaReallocated = EnsureBuffer(ref _rawCurvesBuffer, ByteSize(_rawCurves.Count, sizeof(GpuBezierCurve)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Raw Curves Buffer");
        bool lineArenaReallocated = EnsureBuffer(ref _linesBuffer, ByteSize(_totalLineSegmentsCount, sizeof(GpuLineSegment)), BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst, "Wavefront Lines Buffer");
        bindingsDirty |= bvhArenaReallocated | rawCurveArenaReallocated | lineArenaReallocated;
        if (bvhArenaReallocated || rawCurveArenaReallocated || lineArenaReallocated)
        {
            // A grow replaces storage, so replay the retained source once into the fresh arenas.
            // Normal geometry misses append only their new ranges and preserve prior flattening.
            _uploadedBvhNodeCount = 0;
            _uploadedRawCurveCount = 0;
            LastGeometryArenaReplay = true;
        }
        instancesReallocated = EnsureBuffer(ref _instancesBuffer, ByteSize(_frameInstances.Count, sizeof(GpuShapeInstance)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Instances Buffer");
        transformsReallocated = EnsureBuffer(ref _shapeTransformsBuffer, ByteSize(_shapeTransforms.Count, sizeof(GpuShapeTransform)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Shape Transforms Buffer");
        bindingsDirty |= instancesReallocated | transformsReallocated;
        bindingsDirty |= EnsureBuffer(ref _gridCellsBuffer, ByteSize(cellCount, sizeof(GpuGridCell)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Grid Cells Buffer");
        bindingsDirty |= EnsureBuffer(ref _gridIndicesBuffer, ByteSize(indexCount, sizeof(uint)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Grid Indices Buffer");
        bindingsDirty |= EnsureBuffer(ref _coverageWordsBuffer, ByteSize(coverageWordCount, sizeof(uint)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Coverage Words Buffer");
        bindingsDirty |= EnsureBuffer(ref _activeCellIndicesBuffer, ByteSize(checked(cellCount + 1u), sizeof(uint)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Active Cell Indices Buffer");
        bindingsDirty |= EnsureBuffer(ref _cellShapeClassesBuffer, ByteSize(indexCount, sizeof(uint)), BufferUsage.Storage | BufferUsage.CopyDst, "Wavefront Cell Shape Classes Buffer");
        bindingsDirty |= EnsureBuffer(
            ref _activeDispatchBuffer,
            (uint)sizeof(GpuDispatchIndirectArgs),
            BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.Indirect,
            "Wavefront Active Cell Indirect Dispatch Buffer");
        bindingsDirty |= EnsureBuffer(
            ref _activeDrawBuffer,
            (uint)sizeof(GpuDrawIndirectArgs),
            BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.Indirect,
            "Wavefront Active Cell Indirect Draw Buffer");
        bindingsDirty |= EnsureBuffer(ref _uniformsBuffer, (uint)sizeof(GpuWavefrontUniforms), BufferUsage.Uniform | BufferUsage.CopyDst, "Wavefront Uniforms Buffer");

        uint oldScanCapacity = _binWordScan.Capacity;
        _binWordScan.EnsureCapacity(coverageWordCount);
        bindingsDirty |= oldScanCapacity != _binWordScan.Capacity;
        uint oldActiveScanCapacity = _activeCellScan.Capacity;
        _activeCellScan.EnsureCapacity(cellCount);
        bindingsDirty |= oldActiveScanCapacity != _activeCellScan.Capacity;

        if (destination.Format != TextureFormat.Bgra8Unorm)
        {
            throw new NotSupportedException(
                $"Wavefront sparse compositing currently requires {TextureFormat.Bgra8Unorm}; received {destination.Format}.");
        }

        if (_sparseOutputTexture == null ||
            _sparseOutputTexture.Width != width ||
            _sparseOutputTexture.Height != height ||
            _sparseOutputTexture.Format != destination.Format)
        {
            _sparseOutputTexture?.Dispose();
            _sparseOutputTexture = new GpuTexture(
                _context,
                width,
                height,
                destination.Format,
                TextureUsage.TextureBinding | TextureUsage.StorageBinding,
                "Wavefront Sparse Output Texture",
                alphaMode: GpuTextureAlphaMode.Premultiplied);
            bindingsDirty = true;
        }

        if (_renderBaseView != destination.ViewPtr)
        {
            _renderBaseView = destination.ViewPtr;
            bindingsDirty = true;
        }

        if (bindingsDirty)
        {
            RebuildBindGroups();
        }
    }

    private uint CountCellOverlaps(uint width, uint height, float dpiScale, uint gridStride, uint gridRows)
    {
        ulong count = 0;
        foreach (var instance in _frameInstances)
        {
            if (instance.TransformIndex >= (uint)_shapeTransforms.Count ||
                _freeShapeTransformSet.Contains(instance.TransformIndex))
            {
                throw new InvalidOperationException(
                    "Wavefront instance references a released retained-transform slot.");
            }
            if (!TryGetCoveredCellRange(
                    instance,
                    _shapeTransforms[(int)instance.TransformIndex],
                    width,
                    height,
                    dpiScale,
                    gridStride,
                    gridRows,
                    out var min,
                    out var max))
            {
                continue;
            }

            count += (ulong)(max.X - min.X + 1u) * (max.Y - min.Y + 1u);
            if (count > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    "Wavefront cell overlap output exceeds WebGPU's 32-bit storage-buffer addressing. " +
                    "The frame was rejected before encoding; route this workload to retained atlas/static geometry rendering.");
            }
        }
        return (uint)count;
    }

    public static bool TryGetCoveredCellRange(
        in GpuShapeInstance instance,
        uint width,
        uint height,
        float dpiScale,
        uint gridStride,
        uint gridRows,
        out (uint X, uint Y) minCell,
        out (uint X, uint Y) maxCell)
    {
        var identity = new GpuShapeTransform(Matrix4x4.Identity, Matrix4x4.Identity);
        return TryGetCoveredCellRange(
            instance,
            identity,
            width,
            height,
            dpiScale,
            gridStride,
            gridRows,
            out minCell,
            out maxCell);
    }

    public static bool TryGetCoveredCellRange(
        in GpuShapeInstance instance,
        in GpuShapeTransform retainedTransform,
        uint width,
        uint height,
        float dpiScale,
        uint gridStride,
        uint gridRows,
        out (uint X, uint Y) minCell,
        out (uint X, uint Y) maxCell)
    {
        minCell = default;
        maxCell = default;
        if (width == 0 || height == 0 || gridStride == 0 || gridRows == 0 || !float.IsFinite(dpiScale) || dpiScale <= 0f)
        {
            return false;
        }

        Matrix4x4 transform = instance.Transform * retainedTransform.Transform;
        Vector4 p0 = Vector4.Transform(new Vector4(instance.MinBounds, 0f, 1f), transform) * dpiScale;
        Vector4 p1 = Vector4.Transform(new Vector4(instance.MaxBounds.X, instance.MinBounds.Y, 0f, 1f), transform) * dpiScale;
        Vector4 p2 = Vector4.Transform(new Vector4(instance.MinBounds.X, instance.MaxBounds.Y, 0f, 1f), transform) * dpiScale;
        Vector4 p3 = Vector4.Transform(new Vector4(instance.MaxBounds, 0f, 1f), transform) * dpiScale;
        if (!IsFinite(p0) || !IsFinite(p1) || !IsFinite(p2) || !IsFinite(p3))
        {
            return false;
        }

        float minX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
        float minY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
        float maxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        float maxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
        if (maxX < 0f || maxY < 0f || minX > width || minY > height)
        {
            return false;
        }

        float clippedMinX = Math.Clamp(minX, 0f, width - 1f);
        float clippedMinY = Math.Clamp(minY, 0f, height - 1f);
        float clippedMaxX = Math.Clamp(maxX, 0f, width - 1f);
        float clippedMaxY = Math.Clamp(maxY, 0f, height - 1f);
        minCell = ((uint)clippedMinX / 16u, (uint)clippedMinY / 16u);
        maxCell = (
            Math.Min((uint)clippedMaxX / 16u, gridStride - 1u),
            Math.Min((uint)clippedMaxY / 16u, gridRows - 1u));
        return minCell.X <= maxCell.X && minCell.Y <= maxCell.Y;
    }

    public static void BuildStableCellBinsCpu(
        ReadOnlySpan<(uint MinX, uint MinY, uint MaxX, uint MaxY)> ranges,
        uint gridStride,
        uint gridRows,
        Span<GpuGridCell> cells,
        Span<uint> indices)
    {
        uint cellCount = checked(gridStride * gridRows);
        if ((uint)cells.Length < cellCount)
        {
            throw new ArgumentException("Cell output is too short.", nameof(cells));
        }

        cells[..(int)cellCount].Clear();
        foreach (var range in ranges)
        {
            if (range.MinX > range.MaxX || range.MinY > range.MaxY || range.MaxX >= gridStride || range.MaxY >= gridRows)
            {
                throw new ArgumentOutOfRangeException(nameof(ranges), "A cell range lies outside the grid.");
            }
            for (uint y = range.MinY; y <= range.MaxY; y++)
            {
                for (uint x = range.MinX; x <= range.MaxX; x++)
                {
                    cells[(int)(y * gridStride + x)].ShapeCount++;
                }
            }
        }

        uint total = 0;
        for (uint cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            ref var cell = ref cells[(int)cellIndex];
            cell.ShapeStartOffset = total;
            total = checked(total + cell.ShapeCount);
        }
        if ((uint)indices.Length < total)
        {
            throw new ArgumentException("Index output is too short.", nameof(indices));
        }

        Span<uint> cursors = cellCount <= 256u
            ? stackalloc uint[(int)cellCount]
            : new uint[(int)cellCount];
        for (uint instanceIndex = 0; instanceIndex < (uint)ranges.Length; instanceIndex++)
        {
            var range = ranges[(int)instanceIndex];
            for (uint y = range.MinY; y <= range.MaxY; y++)
            {
                for (uint x = range.MinX; x <= range.MaxX; x++)
                {
                    uint cellIndex = y * gridStride + x;
                    uint destination = cells[(int)cellIndex].ShapeStartOffset + cursors[(int)cellIndex]++;
                    indices[(int)destination] = instanceIndex;
                }
            }
        }
    }

    /// <summary>
    /// CPU oracle for the GPU mark/scan/scatter/finalize sequence. Non-empty cells retain
    /// row-major order and map one-to-one to indirect 16x16 workgroups.
    /// </summary>
    public static GpuDispatchIndirectArgs BuildActiveCellListCpu(
        ReadOnlySpan<GpuGridCell> cells,
        Span<uint> activeCellIndices)
    {
        uint activeCount = 0;
        for (uint cellIndex = 0; cellIndex < (uint)cells.Length; cellIndex++)
        {
            if (cells[(int)cellIndex].ShapeCount == 0)
            {
                continue;
            }
            if (activeCount >= (uint)activeCellIndices.Length)
            {
                throw new ArgumentException("Active-cell output is too short.", nameof(activeCellIndices));
            }
            activeCellIndices[(int)activeCount++] = cellIndex;
        }

        return CreateSparseDispatchArgs(activeCount);
    }

    public static GpuDispatchIndirectArgs CreateSparseDispatchArgs(uint activeCount)
    {
        if (activeCount == 0)
        {
            return new GpuDispatchIndirectArgs { Y = 1, Z = 1 };
        }

        uint width = Math.Min(activeCount, MaximumPortableDispatchDimension);
        return new GpuDispatchIndirectArgs
        {
            X = width,
            Y = ((activeCount - 1u) / width) + 1u,
            Z = 1
        };
    }

    /// <summary>
    /// CPU oracle for the conservative coarse-cell classification used by the shader. Distance is
    /// measured at the cell center in local coordinates and scaled by the affine transform's
    /// minimum singular value, which is a lower bound in device pixels.
    /// </summary>
    public static GpuCellShapeClass ClassifyCellByCenterDistance(
        float minimumLocalDistance,
        float minimumDeviceScale,
        float dpiScale,
        float cellWidth,
        float cellHeight,
        bool centerIsInside)
    {
        if (!float.IsFinite(minimumLocalDistance) || minimumLocalDistance < 0f ||
            !float.IsFinite(minimumDeviceScale) || minimumDeviceScale < 0f ||
            !float.IsFinite(dpiScale) || dpiScale <= 0f ||
            !float.IsFinite(cellWidth) || cellWidth <= 0f ||
            !float.IsFinite(cellHeight) || cellHeight <= 0f)
        {
            return GpuCellShapeClass.Edge;
        }

        float lowerDeviceDistance = minimumLocalDistance * minimumDeviceScale * dpiScale;
        float safeRadius = MathF.Sqrt(
            cellWidth * cellWidth * 0.25f + cellHeight * cellHeight * 0.25f) + 0.5f;
        if (lowerDeviceDistance <= safeRadius)
        {
            return GpuCellShapeClass.Edge;
        }
        return centerIsInside ? GpuCellShapeClass.Solid : GpuCellShapeClass.Outside;
    }

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(in Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static uint ByteSize(int count, int elementSize) =>
        ByteSize(checked((uint)Math.Max(1, count)), elementSize);

    private static uint ByteSize(uint count, int elementSize)
    {
        ulong bytes = (ulong)Math.Max(1u, count) * (uint)elementSize;
        if (bytes > uint.MaxValue)
        {
            throw new InvalidOperationException("Wavefront GPU arena exceeds WebGPU's 32-bit buffer size supported by this backend.");
        }
        return (uint)bytes;
    }

    private bool EnsureBuffer(ref GpuBuffer? buffer, uint requiredBytes, BufferUsage usage, string label)
    {
        requiredBytes = Math.Max(4u, requiredBytes);
        if (buffer != null && buffer.Size >= requiredBytes)
        {
            return false;
        }

        uint capacity = buffer?.Size ?? 256u;
        while (capacity < requiredBytes)
        {
            uint next = capacity <= uint.MaxValue / 2u ? capacity * 2u : uint.MaxValue;
            if (next == capacity)
            {
                throw new InvalidOperationException($"{label} cannot grow to {requiredBytes} bytes.");
            }
            capacity = next;
        }

        buffer?.Dispose();
        buffer = new GpuBuffer(_context, capacity, usage, label);
        return true;
    }

    private void RebuildBindGroups()
    {
        ReleaseBindGroups();

        var flattenEntries = stackalloc BindGroupEntry[3];
        flattenEntries[0] = BufferEntry(0, _uniformsBuffer!);
        flattenEntries[1] = BufferEntry(12, _rawCurvesBuffer!);
        flattenEntries[2] = BufferEntry(9, _linesBuffer!);
        _flattenBindGroup = CreateBindGroup(_flattenLayout, flattenEntries, 3, "wavefront flatten");

        var clearEntries = stackalloc BindGroupEntry[2];
        clearEntries[0] = BufferEntry(0, _uniformsBuffer!);
        clearEntries[1] = BufferEntry(7, _coverageWordsBuffer!);
        _clearBinWordsBindGroup = CreateBindGroup(_clearBinWordsLayout, clearEntries, 2, "wavefront bin clear");

        var buildEntries = stackalloc BindGroupEntry[4];
        buildEntries[0] = BufferEntry(0, _uniformsBuffer!);
        buildEntries[1] = BufferEntry(4, _instancesBuffer!);
        buildEntries[2] = BufferEntry(7, _coverageWordsBuffer!);
        buildEntries[3] = BufferEntry(21, _shapeTransformsBuffer!);
        _buildBinCoverageBindGroup = CreateBindGroup(_buildBinCoverageLayout, buildEntries, 4, "wavefront bin coverage");

        var countEntries = stackalloc BindGroupEntry[3];
        countEntries[0] = BufferEntry(0, _uniformsBuffer!);
        countEntries[1] = BufferEntry(7, _coverageWordsBuffer!);
        countEntries[2] = BufferEntry(13, _binWordScan.InputBuffer);
        _countBinWordsBindGroup = CreateBindGroup(_countBinWordsLayout, countEntries, 3, "wavefront bin count");

        var scatterEntries = stackalloc BindGroupEntry[5];
        scatterEntries[0] = BufferEntry(0, _uniformsBuffer!);
        scatterEntries[1] = BufferEntry(5, _gridCellsBuffer!);
        scatterEntries[2] = BufferEntry(6, _gridIndicesBuffer!);
        scatterEntries[3] = BufferEntry(7, _coverageWordsBuffer!);
        scatterEntries[4] = BufferEntry(14, _binWordScan.OutputBuffer);
        _scatterBinWordsBindGroup = CreateBindGroup(_scatterBinWordsLayout, scatterEntries, 5, "wavefront bin scatter");

        var markActiveEntries = stackalloc BindGroupEntry[3];
        markActiveEntries[0] = BufferEntry(0, _uniformsBuffer!);
        markActiveEntries[1] = BufferEntry(5, _gridCellsBuffer!);
        markActiveEntries[2] = BufferEntry(15, _activeCellScan.InputBuffer);
        _markActiveCellsBindGroup = CreateBindGroup(_markActiveCellsLayout, markActiveEntries, 3, "wavefront active-cell mark");

        var scatterActiveEntries = stackalloc BindGroupEntry[4];
        scatterActiveEntries[0] = BufferEntry(0, _uniformsBuffer!);
        scatterActiveEntries[1] = BufferEntry(15, _activeCellScan.InputBuffer);
        scatterActiveEntries[2] = BufferEntry(16, _activeCellScan.OutputBuffer);
        scatterActiveEntries[3] = BufferEntry(17, _activeCellIndicesBuffer!);
        _scatterActiveCellsBindGroup = CreateBindGroup(_scatterActiveCellsLayout, scatterActiveEntries, 4, "wavefront active-cell scatter");

        var finalizeActiveEntries = stackalloc BindGroupEntry[6];
        finalizeActiveEntries[0] = BufferEntry(0, _uniformsBuffer!);
        finalizeActiveEntries[1] = BufferEntry(15, _activeCellScan.InputBuffer);
        finalizeActiveEntries[2] = BufferEntry(16, _activeCellScan.OutputBuffer);
        finalizeActiveEntries[3] = BufferEntry(17, _activeCellIndicesBuffer!);
        finalizeActiveEntries[4] = BufferEntry(18, _activeDispatchBuffer!);
        finalizeActiveEntries[5] = BufferEntry(20, _activeDrawBuffer!);
        _finalizeActiveDispatchBindGroup = CreateBindGroup(_finalizeActiveDispatchLayout, finalizeActiveEntries, 6, "wavefront active-cell indirect finalize");

        var classifyEntries = stackalloc BindGroupEntry[9];
        classifyEntries[0] = BufferEntry(0, _uniformsBuffer!);
        classifyEntries[1] = BufferEntry(3, _bvhBuffer!);
        classifyEntries[2] = BufferEntry(4, _instancesBuffer!);
        classifyEntries[3] = BufferEntry(5, _gridCellsBuffer!);
        classifyEntries[4] = BufferEntry(6, _gridIndicesBuffer!);
        classifyEntries[5] = BufferEntry(9, _linesBuffer!);
        classifyEntries[6] = BufferEntry(17, _activeCellIndicesBuffer!);
        classifyEntries[7] = BufferEntry(19, _cellShapeClassesBuffer!);
        classifyEntries[8] = BufferEntry(21, _shapeTransformsBuffer!);
        _classifyCellShapesBindGroup = CreateBindGroup(_classifyCellShapesLayout, classifyEntries, 9, "wavefront cell-shape classify");

        var renderEntries = stackalloc BindGroupEntry[11];
        renderEntries[0] = BufferEntry(0, _uniformsBuffer!);
        renderEntries[1] = BufferEntry(3, _bvhBuffer!);
        renderEntries[2] = BufferEntry(4, _instancesBuffer!);
        renderEntries[3] = BufferEntry(5, _gridCellsBuffer!);
        renderEntries[4] = BufferEntry(6, _gridIndicesBuffer!);
        renderEntries[5] = new BindGroupEntry { Binding = 8, TextureView = _renderBaseView };
        renderEntries[6] = BufferEntry(9, _linesBuffer!);
        renderEntries[7] = new BindGroupEntry { Binding = 11, TextureView = _sparseOutputTexture!.ViewPtr };
        renderEntries[8] = BufferEntry(17, _activeCellIndicesBuffer!);
        renderEntries[9] = BufferEntry(19, _cellShapeClassesBuffer!);
        renderEntries[10] = BufferEntry(21, _shapeTransformsBuffer!);
        _renderBindGroup = CreateBindGroup(_renderLayout, renderEntries, 11, "wavefront render");

        var compositeEntries = stackalloc BindGroupEntry[3];
        compositeEntries[0] = BufferEntry(0, _uniformsBuffer!);
        compositeEntries[1] = BufferEntry(1, _activeCellIndicesBuffer!);
        compositeEntries[2] = new BindGroupEntry { Binding = 2, TextureView = _sparseOutputTexture.ViewPtr };
        _compositeBindGroup = CreateBindGroup(_compositeLayout, compositeEntries, 3, "wavefront sparse composite");
    }

    private BindGroup* CreateBindGroup(BindGroupLayout* layout, BindGroupEntry* entries, uint count, string operation)
    {
        var descriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = count,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &descriptor);
        if (bindGroup == null)
        {
            throw new InvalidOperationException($"Failed to create the {operation} bind group.");
        }
        return bindGroup;
    }

    private static BindGroupEntry BufferEntry(uint binding, GpuBuffer buffer) => new()
    {
        Binding = binding,
        Buffer = buffer.BufferPtr,
        Offset = 0,
        Size = buffer.Size
    };

    private void ReleaseBindGroups()
    {
        ReleaseBindGroup(ref _flattenBindGroup);
        ReleaseBindGroup(ref _clearBinWordsBindGroup);
        ReleaseBindGroup(ref _buildBinCoverageBindGroup);
        ReleaseBindGroup(ref _countBinWordsBindGroup);
        ReleaseBindGroup(ref _scatterBinWordsBindGroup);
        ReleaseBindGroup(ref _markActiveCellsBindGroup);
        ReleaseBindGroup(ref _scatterActiveCellsBindGroup);
        ReleaseBindGroup(ref _finalizeActiveDispatchBindGroup);
        ReleaseBindGroup(ref _classifyCellShapesBindGroup);
        ReleaseBindGroup(ref _renderBindGroup);
        ReleaseBindGroup(ref _compositeBindGroup);
    }

    private void ReleaseBindGroup(ref BindGroup* bindGroup)
    {
        if (bindGroup == null) return;
        _context.Api.BindGroupRelease(bindGroup);
        bindGroup = null;
    }

    private static uint DivRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1u) / divisor);

    public void Dispose()
    {
        if (_isDisposed) return;

        ReleaseBindGroups();
        _binWordScan.Dispose();
        _activeCellScan.Dispose();
        _bvhBuffer?.Dispose();
        _rawCurvesBuffer?.Dispose();
        _linesBuffer?.Dispose();
        _instancesBuffer?.Dispose();
        _shapeTransformsBuffer?.Dispose();
        _gridCellsBuffer?.Dispose();
        _gridIndicesBuffer?.Dispose();
        _coverageWordsBuffer?.Dispose();
        _activeCellIndicesBuffer?.Dispose();
        _activeDispatchBuffer?.Dispose();
        _activeDrawBuffer?.Dispose();
        _cellShapeClassesBuffer?.Dispose();
        _uniformsBuffer?.Dispose();
        _sparseOutputTexture?.Dispose();
        _context.Api.BindGroupLayoutRelease(_flattenLayout);
        _context.Api.BindGroupLayoutRelease(_clearBinWordsLayout);
        _context.Api.BindGroupLayoutRelease(_buildBinCoverageLayout);
        _context.Api.BindGroupLayoutRelease(_countBinWordsLayout);
        _context.Api.BindGroupLayoutRelease(_scatterBinWordsLayout);
        _context.Api.BindGroupLayoutRelease(_markActiveCellsLayout);
        _context.Api.BindGroupLayoutRelease(_scatterActiveCellsLayout);
        _context.Api.BindGroupLayoutRelease(_finalizeActiveDispatchLayout);
        _context.Api.BindGroupLayoutRelease(_classifyCellShapesLayout);
        _context.Api.BindGroupLayoutRelease(_renderLayout);
        _context.Api.BindGroupLayoutRelease(_compositeLayout);
        _pipelineCache.Dispose();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    ~WavefrontVectorEngine()
    {
        Dispose();
    }
}
