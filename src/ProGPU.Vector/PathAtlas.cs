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

namespace ProGPU.Vector;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct PathUniforms
{
    public float XStart;
    public float YStart;
    public float ScaleX;
    public float ScaleY;
    public uint PathIndex;
    public uint OutputOffsetWords;
    public uint OutputRowWords;
    public uint Width;
    public uint Height;
    public uint SampleGrid;
    public uint PathIndexB;
    public uint PathOpKind;
}

[StructLayout(LayoutKind.Sequential)]
public struct PathOpUniforms
{
    public uint Op;
    public uint DestX;
    public uint DestY;
    public uint DestWidth;
    public uint DestHeight;
    public uint SrcAX;
    public uint SrcAY;
    public uint SrcAWidth;
    public uint SrcAHeight;
    public uint SrcBX;
    public uint SrcBY;
    public uint SrcBWidth;
    public uint SrcBHeight;
    public int DestMinX;
    public int DestMinY;
    public int SrcAMinX;
    public int SrcAMinY;
    public int SrcBMinX;
    public int SrcBMinY;
    public uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuPathRecord
{
    public uint StartSegment;
    public uint SegmentCount;
    public float MinX;
    public float MinY;
    public float MaxX;
    public float MaxY;
    public uint FillRule;
    public uint Pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuPathSegment
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

public readonly struct PathCacheKey : IEquatable<PathCacheKey>
{
    public int ContentHash { get; }
    public float ScaleX { get; }
    public float ScaleY { get; }
    public float Scale => Math.Max(ScaleX, ScaleY);
    public float SubpixelX { get; }
    public float SubpixelY { get; }
    public uint SampleGrid { get; }

    public PathCacheKey(
        int contentHash,
        float scale,
        float subpixelX = 0f,
        float subpixelY = 0f,
        uint sampleGrid = PathAtlas.StandardCoverageSampleGrid)
        : this(
            contentHash,
            scale,
            subpixelX,
            subpixelY,
            sampleGrid,
            PathAtlas.DefaultSubpixelPhaseGrid,
            quantizeScale: false)
    {
    }

    public PathCacheKey(
        int contentHash,
        float scale,
        float subpixelX,
        float subpixelY,
        uint sampleGrid,
        uint subpixelPhaseGrid,
        bool quantizeScale)
        : this(
            contentHash,
            scale,
            scale,
            subpixelX,
            subpixelY,
            sampleGrid,
            subpixelPhaseGrid,
            quantizeScale)
    {
    }

    public PathCacheKey(
        int contentHash,
        float scaleX,
        float scaleY,
        float subpixelX,
        float subpixelY,
        uint sampleGrid = PathAtlas.StandardCoverageSampleGrid)
        : this(
            contentHash,
            scaleX,
            scaleY,
            subpixelX,
            subpixelY,
            sampleGrid,
            PathAtlas.DefaultSubpixelPhaseGrid,
            quantizeScale: false)
    {
    }

    public PathCacheKey(
        int contentHash,
        float scaleX,
        float scaleY,
        float subpixelX,
        float subpixelY,
        uint sampleGrid,
        uint subpixelPhaseGrid,
        bool quantizeScale)
    {
        ContentHash = contentHash;
        ScaleX = quantizeScale ? QuantizeScale(scaleX) : scaleX;
        ScaleY = quantizeScale ? QuantizeScale(scaleY) : scaleY;
        SubpixelX = QuantizeSubpixel(subpixelX, subpixelPhaseGrid);
        SubpixelY = QuantizeSubpixel(subpixelY, subpixelPhaseGrid);
        SampleGrid = NormalizeSampleGrid(sampleGrid);
    }

    public bool Equals(PathCacheKey other)
    {
        return ContentHash == other.ContentHash &&
               ScaleX.Equals(other.ScaleX) &&
               ScaleY.Equals(other.ScaleY) &&
               SubpixelX.Equals(other.SubpixelX) &&
               SubpixelY.Equals(other.SubpixelY) &&
               SampleGrid == other.SampleGrid;
    }

    public override bool Equals(object? obj)
    {
        return obj is PathCacheKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ContentHash, ScaleX, ScaleY, SubpixelX, SubpixelY, SampleGrid);
    }

    public static bool operator ==(PathCacheKey left, PathCacheKey right) => left.Equals(right);
    public static bool operator !=(PathCacheKey left, PathCacheKey right) => !left.Equals(right);

    private static float QuantizeScale(float value)
    {
        if (!float.IsFinite(value) || value == 0f)
        {
            return value;
        }

        var magnitude = MathF.Abs(value);
        var exponent = MathF.ILogB(magnitude);
        var step = MathF.ScaleB(1f, exponent - 10);
        if (!float.IsFinite(step) || step <= 0f)
        {
            return value;
        }

        var quantized = MathF.Round(value / step) * step;
        return float.IsFinite(quantized) ? quantized : value;
    }

    private static float QuantizeSubpixel(float value, uint phaseGrid)
    {
        if (!float.IsFinite(value))
        {
            return 0f;
        }

        phaseGrid = Math.Clamp(phaseGrid, 1u, PathAtlas.DefaultSubpixelPhaseGrid);
        value -= MathF.Floor(value);
        var quantized = MathF.Round(value * phaseGrid) / phaseGrid;
        return quantized >= 1f ? 0f : quantized;
    }

    private static uint NormalizeSampleGrid(uint value) =>
        value >= PathAtlas.HighPrecisionCoverageSampleGrid
            ? PathAtlas.HighPrecisionCoverageSampleGrid
            : PathAtlas.StandardCoverageSampleGrid;
}

public interface IPathHitTestCompilationCache
{
    bool TryGetCompiledHitTestPath(
        PathGeometry path,
        out GpuPathRecord[] records,
        out GpuPathSegment[] segments,
        out float localMinX,
        out float localMinY,
        out float localMaxX,
        out float localMaxY);
}

public unsafe class PathAtlas : IDisposable
    , IPathHitTestCompilationCache
{
    public const uint StandardCoverageSampleGrid = 4;
    public const uint HighPrecisionCoverageSampleGrid = 8;
    public const uint DefaultSubpixelPhaseGrid = 64;
    public const uint DefaultInitialAtlasSize = 512;
    public const uint DefaultRasterStagingChunkBytes = 256 * 1024;
    public const uint DefaultAtlasShrinkDelayFrames = 240;

    public const long DefaultCompiledPathCacheBudgetBytes = 8L * 1024L * 1024L;

    private const ulong MinimumAtlasShrinkAreaNumerator = 4;
    private const ulong MinimumAtlasShrinkAreaDenominator = 3;
    private const uint AtlasShrinkDimensionStep = 256;
    private const int MaxCompiledPathCount = 4096;
    private const int RasterizationStorageOffsetAlignment = 256;
    private const int ExactRecoveryPathLimit = 10;
    private const int ExactRecoveryNodeBudget = 25_000;
    private const int ExactRecoveryCandidateBudget = 250_000;

    private readonly WgpuContext _context;
    private GpuTexture _atlasTexture;
    private uint _atlasWidth;
    private uint _atlasHeight;
    private readonly uint _maxAtlasSize;
    private readonly uint _initialAtlasSize;
    private readonly long _compiledPathCacheBudgetBytes;

    private uint _currentX = 2;
    private uint _currentY = 2;
    private uint _currentRowHeight = 0;
    private uint _frameNumber = 0;
    private uint _framesSinceAtlasResize;
    private bool _retainedPathReplayObserved;
    private List<AtlasFreeRectangle>? _recoveryFreeRectangles;

    public struct PathInfo
    {
        public PathCacheKey Key;
        public PathGeometry Geometry;
        public float UnscaledMinX;
        public float UnscaledMinY;
        public float UnscaledMaxX;
        public float UnscaledMaxY;

        public uint X;
        public uint Y;
        public uint Width;
        public uint Height;
        public Vector2 TexCoordMin;
        public Vector2 TexCoordMax;
        public float MinX;
        public float MinY;
        public uint LastUsedFrame;
        // Failed first-pass placements keep their public Width/Height at zero
        // so invalid UVs cannot be consumed. These internal dimensions retain
        // the complete live rectangle set for the deterministic render retry.
        internal int RetryXStart;
        internal int RetryYStart;
        internal uint RetryWidth;
        internal uint RetryHeight;
    }

    private readonly Dictionary<PathCacheKey, PathInfo> _paths = new();
    private readonly Dictionary<int, CompiledPathCacheEntry> _compiledFillPaths = new();
    private readonly Dictionary<int, CompiledPathCacheEntry> _compiledHitTestPaths = new();
    private readonly LinkedList<CompiledPathCacheToken> _compiledPathCacheLru = new();
    private long _compiledPathCacheBytes;
    private readonly List<GpuBuffer> _tempBuffers = new();
    private readonly List<PathInfo> _pendingPaths = new();

    // MaxRects state exists only after a capacity-triggered retry. The fragmented
    // free list intentionally remains active until the next reset because the
    // monotonic shelf cursors cannot safely resume inside a MaxRects layout.
    private readonly record struct AtlasFreeRectangle(
        uint X,
        uint Y,
        uint Width,
        uint Height)
    {
        public uint Right => X + Width;
        public uint Bottom => Y + Height;
    }

    private readonly record struct RetryPath(
        PathInfo Info,
        int XStart,
        int YStart,
        uint Width,
        uint Height);

    private readonly record struct RetryPlacement(
        RetryPath Path,
        AtlasFreeRectangle Rectangle);

    private struct ExactRecoverySearchState
    {
        public int NodeCount;
        public int CandidateCount;
        public bool BudgetExceeded;

        public bool TryEnterNode()
        {
            if (NodeCount >= ExactRecoveryNodeBudget)
            {
                BudgetExceeded = true;
                return false;
            }

            NodeCount++;
            return true;
        }

        public bool TryVisitCandidate()
        {
            if (CandidateCount >= ExactRecoveryCandidateBudget)
            {
                BudgetExceeded = true;
                return false;
            }

            CandidateCount++;
            return true;
        }
    }

    private enum RetryPathOrdering
    {
        AreaDescending,
        WidthDescending,
        HeightDescending,
        MaxSideDescending
    }

    private enum RecoveryPlacementHeuristic
    {
        BestShortSideFit,
        BestAreaFit,
        BottomLeft,
        ExactBranchAndBound
    }

    private readonly RenderPipelineCache _pipelineCache;
    private readonly WgpuBindGroupLayoutLease _computeBindGroupLayoutLease;
    private readonly WgpuPipelineLayoutLease _computePipelineLayoutLease;
    private readonly BindGroupLayout* _computeBindGroupLayout;
    private readonly PipelineLayout* _computePipelineLayout;
    private readonly ComputePipeline* _computePipeline;
    private bool _isDisposed;

    public GpuTexture AtlasTexture => _atlasTexture;
    public uint AtlasWidth => _atlasWidth;
    public uint AtlasHeight => _atlasHeight;
    public uint AtlasSize => Math.Max(_atlasWidth, _atlasHeight);
    public uint MaxAtlasSize => _maxAtlasSize;
    public ulong TextureRevision { get; private set; }
    public int CachedPathCount => _paths.Count;
    public int CachedPathStorageCapacity => _paths.EnsureCapacity(0);
    public int CachedFillPathCount => _compiledFillPaths.Count;
    public int CachedHitTestPathCount => _compiledHitTestPaths.Count;
    public long CompiledPathCacheBytes => _compiledPathCacheBytes;
    public long CompiledPathCacheBudgetBytes => _compiledPathCacheBudgetBytes;
    public ulong Generation { get; private set; }
    public bool CapacityExceeded { get; private set; }
    public ulong PersistentTextureBytes => (ulong)_atlasWidth * _atlasHeight;
    public uint AtlasGrowthCount { get; private set; }
    public uint AtlasAvoidedGrowthCount { get; private set; }
    public uint AtlasShrinkCount { get; private set; }
    public uint FramesSinceAtlasResize => _framesSinceAtlasResize;
    public int CurrentFramePathCount
    {
        get
        {
            int count = 0;
            foreach (PathInfo info in _paths.Values)
            {
                if (info.LastUsedFrame == _frameNumber)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public ulong CurrentFrameCoverageBytes
    {
        get
        {
            ulong bytes = 0;
            foreach (PathInfo info in _paths.Values)
            {
                if (info.LastUsedFrame == _frameNumber)
                {
                    bytes += (ulong)info.Width * info.Height;
                }
            }

            return bytes;
        }
    }

    public string DescribeCurrentFrameRasterRectangles()
    {
        var description = new System.Text.StringBuilder();
        foreach (PathInfo info in _paths.Values)
        {
            if (info.LastUsedFrame != _frameNumber)
            {
                continue;
            }

            uint width = info.Width > 0 ? info.Width : info.RetryWidth;
            uint height = info.Height > 0 ? info.Height : info.RetryHeight;
            if (description.Length > 0)
            {
                description.Append(", ");
            }

            description.Append(width);
            description.Append('x');
            description.Append(height);
            description.Append('@');
            description.Append(info.X);
            description.Append(',');
            description.Append(info.Y);
            if (info.Width == 0 && info.RetryWidth > 0)
            {
                description.Append(" unplaced");
            }
            description.Append(" geometry=");
            description.Append(info.Geometry.IsCombined ? "combined" : "path");
            description.Append('[');
            for (int figureIndex = 0;
                 figureIndex < info.Geometry.Figures.Count;
                 figureIndex++)
            {
                if (figureIndex > 0)
                {
                    description.Append(';');
                }

                PathFigure figure = info.Geometry.Figures[figureIndex];
                description.Append(figure.IsClosed ? "closed:" : "open:");
                description.Append(figure.Segments.Count);
                description.Append(':');
                for (int segmentIndex = 0;
                     segmentIndex < figure.Segments.Count;
                     segmentIndex++)
                {
                    if (segmentIndex > 0)
                    {
                        description.Append(',');
                    }

                    description.Append(
                        figure.Segments[segmentIndex].GetType().Name);
                }
            }
            description.Append(']');
        }

        return description.ToString();
    }

    public ulong CachedCoverageBytes
    {
        get
        {
            ulong bytes = 0;
            foreach (PathInfo info in _paths.Values)
            {
                bytes += (ulong)info.Width * info.Height;
            }

            return bytes;
        }
    }
    public ulong CachedPaddedCoverageBytes
    {
        get
        {
            ulong bytes = 0;
            foreach (PathInfo info in _paths.Values)
            {
                if (info.Width > 0 && info.Height > 0)
                {
                    bytes += (ulong)(info.Width + 2) * (info.Height + 2);
                }
            }

            return bytes;
        }
    }
    public uint LastRasterStagingBytes { get; private set; }
    public int LastDirectBooleanRasterizationCount { get; private set; }
    public uint PeakRasterStagingBytes { get; private set; }
    public uint PeakRasterWidth { get; private set; }
    public uint PeakRasterHeight { get; private set; }
    public int LastExactRecoveryNodeCount { get; private set; }
    public int LastExactRecoveryCandidateCount { get; private set; }
    public bool LastExactRecoveryBudgetExceeded { get; private set; }

    public PathAtlas(
        WgpuContext context,
        uint atlasSize = 2048,
        long compiledPathCacheBudgetBytes = DefaultCompiledPathCacheBudgetBytes)
    {
        if (compiledPathCacheBudgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(compiledPathCacheBudgetBytes));
        }
        if (atlasSize <= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(atlasSize));
        }

        _context = context;
        _maxAtlasSize = atlasSize;
        _initialAtlasSize = Math.Min(atlasSize, DefaultInitialAtlasSize);
        _atlasWidth = _initialAtlasSize;
        _atlasHeight = _atlasWidth;
        _compiledPathCacheBudgetBytes = compiledPathCacheBudgetBytes;

        _atlasTexture = new GpuTexture(
            _context,
            _atlasWidth,
            _atlasHeight,
            TextureFormat.R8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc |
            TextureUsage.RenderAttachment,
            "Dynamic Path Coverage Atlas"
        );

        ClearAtlasTexture();

        _pipelineCache = new RenderPipelineCache(_context);
        _computeBindGroupLayoutLease =
            CreateRasterizationBindGroupLayout();
        _computeBindGroupLayout =
            _computeBindGroupLayoutLease.Handle;
        _computePipelineLayoutLease =
            CreateRasterizationPipelineLayout(
                _computeBindGroupLayout);
        _computePipelineLayout =
            _computePipelineLayoutLease.Handle;
        var shaderModule = _pipelineCache.GetOrCreateShader("PathRasterizer", Shaders.PathRasterizerShader, "PathRasterizerShader");
        _computePipeline = _pipelineCache.GetOrCreateComputePipeline(
            "PathRasterizer",
            shaderModule,
            "cs_main",
            _computePipelineLayout);
    }

    private WgpuBindGroupLayoutLease CreateRasterizationBindGroupLayout()
    {
        var entries = stackalloc BindGroupLayoutEntry[4];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Compute,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.ReadOnlyStorage,
                HasDynamicOffset = false,
                MinBindingSize = (ulong)Marshal.SizeOf<PathUniforms>()
            }
        };
        entries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Compute,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.ReadOnlyStorage,
                HasDynamicOffset = false,
                MinBindingSize = (ulong)Marshal.SizeOf<GpuPathRecord>()
            }
        };
        entries[2] = new BindGroupLayoutEntry
        {
            Binding = 2,
            Visibility = ShaderStage.Compute,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.ReadOnlyStorage,
                HasDynamicOffset = false,
                MinBindingSize = (ulong)Marshal.SizeOf<GpuPathSegment>()
            }
        };
        entries[3] = new BindGroupLayoutEntry
        {
            Binding = 3,
            Visibility = ShaderStage.Compute,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.Storage,
                HasDynamicOffset = false,
                MinBindingSize = 4
            }
        };

        var descriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = 4,
            Entries = entries
        };
        return _context.AcquireSharedBindGroupLayout(
            new WgpuDeviceResourceKey(
                "ProGPU.Vector.PathAtlas",
                "RasterizationBindings"),
            &descriptor);
    }

    private WgpuPipelineLayoutLease CreateRasterizationPipelineLayout(BindGroupLayout* bindGroupLayout)
    {
        var layouts = stackalloc BindGroupLayout*[1];
        layouts[0] = bindGroupLayout;
        var descriptor = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 1,
            BindGroupLayouts = layouts
        };
        return _context.AcquireSharedPipelineLayout(
            new WgpuDeviceResourceKey(
                "ProGPU.Vector.PathAtlas",
                "RasterizationPipeline"),
            &descriptor);
    }


    private static uint DivRoundUp(uint value, uint divisor) => (value + divisor - 1) / divisor;

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

    public static int ComputeHash(PathGeometry path)
    {
        if (path == null) return 0;
        if (path.IsCombined)
        {
            return HashCode.Combine(
                ComputeHash(path.PathA!),
                ComputeHash(path.PathB!),
                path.Op,
                path.FillRule);
        }
        var hash = new HashCode();
        hash.Add(path.FillRule);
        var figures = path.Figures;
        for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            var figure = figures[figureIndex];
            var segments = figure.Segments;
            hash.Add(figure.StartPoint.X);
            hash.Add(figure.StartPoint.Y);
            hash.Add(figure.IsClosed);
            hash.Add(figure.IsFilled);
            hash.Add(segments.Count);
            for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                var segment = segments[segmentIndex];
                if (segment is LineSegment line)
                {
                    hash.Add(0); // Segment type: Line
                    hash.Add(line.IsStroked);
                    hash.Add(line.Point.X);
                    hash.Add(line.Point.Y);
                }
                else if (segment is QuadraticBezierSegment quad)
                {
                    hash.Add(1); // Segment type: Quadratic
                    hash.Add(quad.IsStroked);
                    hash.Add(quad.ControlPoint.X);
                    hash.Add(quad.ControlPoint.Y);
                    hash.Add(quad.Point.X);
                    hash.Add(quad.Point.Y);
                }
                else if (segment is CubicBezierSegment cubic)
                {
                    hash.Add(2); // Segment type: Cubic
                    hash.Add(cubic.IsStroked);
                    hash.Add(cubic.ControlPoint1.X);
                    hash.Add(cubic.ControlPoint1.Y);
                    hash.Add(cubic.ControlPoint2.X);
                    hash.Add(cubic.ControlPoint2.Y);
                    hash.Add(cubic.Point.X);
                    hash.Add(cubic.Point.Y);
                }
                else if (segment is ArcSegment arc)
                {
                    hash.Add(3); // Segment type: Arc
                    hash.Add(arc.IsStroked);
                    hash.Add(arc.Point.X);
                    hash.Add(arc.Point.Y);
                    hash.Add(arc.Size.X);
                    hash.Add(arc.Size.Y);
                    hash.Add(arc.RotationAngle);
                    hash.Add(arc.IsLargeArc);
                    hash.Add((int)arc.SweepDirection);
                }
            }
        }
        return hash.ToHashCode();
    }

    public static (GpuPathRecord[] Records, GpuPathSegment[] Segments) CompilePath(
        PathGeometry path,
        out float localMinX,
        out float localMinY,
        out float localMaxX,
        out float localMaxY)
    {
        return CompilePathCore(
            path,
            fillOnly: false,
            out localMinX,
            out localMinY,
            out localMaxX,
            out localMaxY);
    }

    public static (GpuPathRecord[] Records, GpuPathSegment[] Segments) CompileFillPath(
        PathGeometry path,
        out float localMinX,
        out float localMinY,
        out float localMaxX,
        out float localMaxY)
    {
        return CompilePathCore(
            path,
            fillOnly: true,
            out localMinX,
            out localMinY,
            out localMaxX,
            out localMaxY);
    }

    private static (GpuPathRecord[] Records, GpuPathSegment[] Segments) CompilePathCore(
        PathGeometry path,
        bool fillOnly,
        out float localMinX,
        out float localMinY,
        out float localMaxX,
        out float localMaxY)
    {
        if (path.IsCombined)
        {
            if (path.PathA == null || path.PathB == null)
            {
                localMinX = localMinY = localMaxX = localMaxY = 0f;
                return (Array.Empty<GpuPathRecord>(), Array.Empty<GpuPathSegment>());
            }

            if (path.CombinedQueryKind == CombinedPathQueryKind.Empty)
            {
                localMinX = localMinY = localMaxX = localMaxY = 0f;
                return (Array.Empty<GpuPathRecord>(), Array.Empty<GpuPathSegment>());
            }

            if (path.CombinedQueryKind == CombinedPathQueryKind.ResultOperandA &&
                CanCompileDeferredOperand(path, path.PathA))
            {
                return CompilePathCore(
                    path.PathA,
                    fillOnly,
                    out localMinX,
                    out localMinY,
                    out localMaxX,
                    out localMaxY);
            }

            if (path.CombinedQueryKind == CombinedPathQueryKind.ResultOperandB &&
                CanCompileDeferredOperand(path, path.PathB))
            {
                return CompilePathCore(
                    path.PathB,
                    fillOnly,
                    out localMinX,
                    out localMinY,
                    out localMaxX,
                    out localMaxY);
            }

            var combined = PathOpGeometrySolver.Combine(path.PathA, path.PathB, path.Op);
            return CompilePathCore(
                combined,
                fillOnly,
                out localMinX,
                out localMinY,
                out localMaxX,
                out localMaxY);
        }

        var figures = path.Figures;
        var segments = new List<GpuPathSegment>(EstimateSegmentCapacity(figures, fillOnly));
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

        for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            var figure = figures[figureIndex];
            var figureSegments = figure.Segments;
            if ((fillOnly && !figure.IsFilled) || figureSegments.Count == 0)
            {
                continue;
            }

            Vector2 currentPoint = figure.StartPoint;
            UpdateBounds(currentPoint);

            for (int segmentIndex = 0; segmentIndex < figureSegments.Count; segmentIndex++)
            {
                var segment = figureSegments[segmentIndex];
                if (segment is LineSegment line)
                {
                    segments.Add(new GpuPathSegment
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
                    segments.Add(new GpuPathSegment
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
                    segments.Add(new GpuPathSegment
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
                else if (segment is ArcSegment arc)
                {
                    if (!ArcSegmentGeometry.TryGetArcCenter(
                        currentPoint, arc.Point, arc.Size, arc.RotationAngle, arc.IsLargeArc, arc.SweepDirection,
                        out Vector2 center, out float theta1, out float deltaTheta, out float rx, out float ry
                    ))
                    {
                        if (currentPoint != arc.Point)
                        {
                            segments.Add(new GpuPathSegment
                            {
                                P0 = currentPoint,
                                P1 = arc.Point,
                                SegmentType = 0
                            });
                        }

                        UpdateBounds(arc.Point);
                        currentPoint = arc.Point;
                        continue;
                    }

                    segments.Add(new GpuPathSegment
                    {
                        P0 = currentPoint,
                        P1 = arc.Point,
                        P2 = center,
                        P3 = new Vector2(rx, ry),
                        SegmentType = 3,
                        Pad0 = BitConverter.SingleToUInt32Bits(theta1),
                        Pad1 = BitConverter.SingleToUInt32Bits(deltaTheta),
                        Pad2 = BitConverter.SingleToUInt32Bits(arc.RotationAngle * MathF.PI / 180.0f)
                    });

                    if (ArcSegmentGeometry.TryGetArcBounds(currentPoint, arc, out Vector2 min, out Vector2 max))
                    {
                        UpdateBounds(min);
                        UpdateBounds(max);
                    }
                    else
                    {
                        UpdateBounds(currentPoint);
                        UpdateBounds(arc.Point);
                    }

                    currentPoint = arc.Point;
                }
            }

            if ((fillOnly || figure.IsClosed) && currentPoint != figure.StartPoint)
            {
                segments.Add(new GpuPathSegment
                {
                    P0 = currentPoint,
                    P1 = figure.StartPoint,
                    SegmentType = 0
                });
                UpdateBounds(figure.StartPoint);
            }
        }

        if (segments.Count == 0)
        {
            localMinX = localMinY = localMaxX = localMaxY = 0f;
            return (Array.Empty<GpuPathRecord>(), Array.Empty<GpuPathSegment>());
        }

        localMinX = minX;
        localMinY = minY;
        localMaxX = maxX;
        localMaxY = maxY;

        var records = new GpuPathRecord[1];
        records[0] = new GpuPathRecord
        {
            StartSegment = 0,
            SegmentCount = (uint)segments.Count,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            FillRule = (uint)path.FillRule
        };

        return (records, CopySegments(segments));
    }

    private static bool CanCompileDeferredOperand(
        PathGeometry combined,
        PathGeometry operand) =>
        combined.FillRule == operand.FillRule || operand.Figures.Count == 1;

    private static int EstimateSegmentCapacity(List<PathFigure> figures, bool fillOnly)
    {
        int capacity = 0;
        for (int i = 0; i < figures.Count; i++)
        {
            var figure = figures[i];
            int segmentCount = figure.Segments.Count;
            if ((fillOnly && !figure.IsFilled) || segmentCount == 0)
            {
                continue;
            }

            capacity += segmentCount;
            if (fillOnly || figure.IsClosed)
            {
                capacity++;
            }
        }

        return capacity;
    }

    private static GpuPathSegment[] CopySegments(List<GpuPathSegment> segments)
    {
        if (segments.Count == 0)
        {
            return Array.Empty<GpuPathSegment>();
        }

        var result = new GpuPathSegment[segments.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = segments[i];
        }

        return result;
    }

    public bool TryGetCompiledHitTestPath(
        PathGeometry path,
        out GpuPathRecord[] records,
        out GpuPathSegment[] segments,
        out float localMinX,
        out float localMinY,
        out float localMaxX,
        out float localMaxY)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(PathAtlas));
        ArgumentNullException.ThrowIfNull(path);

        int contentHash = ComputeHash(path);
        if (TryGetCachedCompiledPath(
                _compiledHitTestPaths,
                contentHash,
                out var cached))
        {
            records = cached.Records;
            segments = cached.Segments;
            localMinX = cached.LocalMinX;
            localMinY = cached.LocalMinY;
            localMaxX = cached.LocalMaxX;
            localMaxY = cached.LocalMaxY;
            return segments.Length != 0;
        }

        try
        {
            (records, segments) = CompilePath(path, out localMinX, out localMinY, out localMaxX, out localMaxY);
        }
        catch (InvalidOperationException)
        {
            records = Array.Empty<GpuPathRecord>();
            segments = Array.Empty<GpuPathSegment>();
            localMinX = 0f;
            localMinY = 0f;
            localMaxX = 0f;
            localMaxY = 0f;
        }

        CacheCompiledPath(
            _compiledHitTestPaths,
            CompiledPathCacheKind.HitTest,
            contentHash,
            new CompiledPathData(
                records,
                segments,
                localMinX,
                localMinY,
                localMaxX,
                localMaxY));
        return segments.Length != 0;
    }

    private bool TryGetCompiledFillPath(
        PathGeometry path,
        out GpuPathRecord[] records,
        out GpuPathSegment[] segments,
        out float localMinX,
        out float localMinY,
        out float localMaxX,
        out float localMaxY)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(PathAtlas));
        ArgumentNullException.ThrowIfNull(path);

        int contentHash = ComputeHash(path);
        if (TryGetCachedCompiledPath(
                _compiledFillPaths,
                contentHash,
                out var cached))
        {
            records = cached.Records;
            segments = cached.Segments;
            localMinX = cached.LocalMinX;
            localMinY = cached.LocalMinY;
            localMaxX = cached.LocalMaxX;
            localMaxY = cached.LocalMaxY;
            return segments.Length != 0;
        }

        try
        {
            (records, segments) = CompileFillPath(
                path,
                out localMinX,
                out localMinY,
                out localMaxX,
                out localMaxY);
        }
        catch (InvalidOperationException)
        {
            records = Array.Empty<GpuPathRecord>();
            segments = Array.Empty<GpuPathSegment>();
            localMinX = 0f;
            localMinY = 0f;
            localMaxX = 0f;
            localMaxY = 0f;
        }

        CacheCompiledPath(
            _compiledFillPaths,
            CompiledPathCacheKind.Fill,
            contentHash,
            new CompiledPathData(
                records,
                segments,
                localMinX,
                localMinY,
                localMaxX,
                localMaxY));
        return segments.Length != 0;
    }

    private bool TryGetRasterBounds(
        PathGeometry path,
        out float localMinX,
        out float localMinY,
        out float localMaxX,
        out float localMaxY)
    {
        if (TryGetDirectBooleanOperands(path, out var pathA, out var pathB, out _) &&
            TryGetCompiledFillPath(pathA, out var recordsA, out var segmentsA, out _, out _, out _, out _) &&
            TryGetCompiledFillPath(pathB, out var recordsB, out var segmentsB, out _, out _, out _, out _) &&
            recordsA.Length == 1 && segmentsA.Length != 0 &&
            recordsB.Length == 1 && segmentsB.Length != 0 &&
            path.TryGetBounds(out var min, out var max))
        {
            localMinX = min.X;
            localMinY = min.Y;
            localMaxX = max.X;
            localMaxY = max.Y;
            return true;
        }

        return TryGetCompiledFillPath(
            path,
            out _,
            out var segments,
            out localMinX,
            out localMinY,
            out localMaxX,
            out localMaxY) && segments.Length != 0;
    }

    private bool TryGetRasterizationData(
        PathGeometry path,
        out GpuPathRecord[] recordsA,
        out GpuPathSegment[] segmentsA,
        out GpuPathRecord[] recordsB,
        out GpuPathSegment[] segmentsB,
        out uint pathOpKind)
    {
        if (TryGetDirectBooleanOperands(path, out var pathA, out var pathB, out var op) &&
            TryGetCompiledFillPath(pathA, out recordsA, out segmentsA, out _, out _, out _, out _) &&
            TryGetCompiledFillPath(pathB, out recordsB, out segmentsB, out _, out _, out _, out _) &&
            recordsA.Length == 1 && segmentsA.Length != 0 &&
            recordsB.Length == 1 && segmentsB.Length != 0)
        {
            pathOpKind = checked((uint)op + 1u);
            return true;
        }

        recordsB = Array.Empty<GpuPathRecord>();
        segmentsB = Array.Empty<GpuPathSegment>();
        pathOpKind = 0;
        return TryGetCompiledFillPath(
            path,
            out recordsA,
            out segmentsA,
            out _,
            out _,
            out _,
            out _) &&
            recordsA.Length != 0 &&
            segmentsA.Length != 0;
    }

    private static bool TryGetDirectBooleanOperands(
        PathGeometry path,
        out PathGeometry pathA,
        out PathGeometry pathB,
        out int op)
    {
        if (path.IsCombined &&
            path.CombinedQueryKind is not CombinedPathQueryKind.Empty and
                not CombinedPathQueryKind.ResultOperandA and
                not CombinedPathQueryKind.ResultOperandB &&
            path.PathA is { IsCombined: false } first &&
            path.PathB is { IsCombined: false } second &&
            (uint)path.Op <= 4u)
        {
            pathA = first;
            pathB = second;
            op = path.Op;
            return true;
        }

        pathA = null!;
        pathB = null!;
        op = 0;
        return false;
    }

    private bool TryGetCachedCompiledPath(
        Dictionary<int, CompiledPathCacheEntry> cache,
        int contentHash,
        out CompiledPathData data)
    {
        if (!cache.TryGetValue(contentHash, out CompiledPathCacheEntry? entry))
        {
            data = default;
            return false;
        }

        _compiledPathCacheLru.Remove(entry.Node);
        _compiledPathCacheLru.AddFirst(entry.Node);
        data = entry.Data;
        return true;
    }

    private void CacheCompiledPath(
        Dictionary<int, CompiledPathCacheEntry> cache,
        CompiledPathCacheKind kind,
        int contentHash,
        CompiledPathData data)
    {
        // Average lookup and recency updates are O(1). A miss evicts E entries
        // in O(E), retains O(B) payload for byte budget B, and never keeps an
        // oversize entry. This bounds complex emoji independently of path count.
        long sizeBytes = EstimateCompiledPathBytes(data);
        if (sizeBytes > _compiledPathCacheBudgetBytes)
        {
            return;
        }

        while (_compiledPathCacheBytes + sizeBytes > _compiledPathCacheBudgetBytes ||
               _compiledPathCacheLru.Count >= MaxCompiledPathCount)
        {
            EvictLeastRecentlyUsedCompiledPath();
        }

        var node = new LinkedListNode<CompiledPathCacheToken>(
            new CompiledPathCacheToken(kind, contentHash));
        cache[contentHash] = new CompiledPathCacheEntry(data, sizeBytes, node);
        _compiledPathCacheLru.AddFirst(node);
        _compiledPathCacheBytes += sizeBytes;
    }

    private void EvictLeastRecentlyUsedCompiledPath()
    {
        LinkedListNode<CompiledPathCacheToken>? node = _compiledPathCacheLru.Last;
        if (node == null)
        {
            return;
        }

        _compiledPathCacheLru.Remove(node);
        CompiledPathCacheToken token = node.Value;
        Dictionary<int, CompiledPathCacheEntry> cache = token.Kind == CompiledPathCacheKind.Fill
            ? _compiledFillPaths
            : _compiledHitTestPaths;
        if (cache.Remove(token.ContentHash, out CompiledPathCacheEntry? entry))
        {
            _compiledPathCacheBytes -= entry.SizeBytes;
        }
    }

    private static long EstimateCompiledPathBytes(CompiledPathData data)
    {
        const int arrayAndEntryOverhead = 128;
        return checked(
            (long)data.Records.Length * Unsafe.SizeOf<GpuPathRecord>() +
            (long)data.Segments.Length * Unsafe.SizeOf<GpuPathSegment>() +
            arrayAndEntryOverhead);
    }

    private enum CompiledPathCacheKind : byte
    {
        Fill,
        HitTest
    }

    private readonly record struct CompiledPathCacheToken(
        CompiledPathCacheKind Kind,
        int ContentHash);

    private sealed record CompiledPathCacheEntry(
        CompiledPathData Data,
        long SizeBytes,
        LinkedListNode<CompiledPathCacheToken> Node);

    private readonly record struct CompiledPathData(
        GpuPathRecord[] Records,
        GpuPathSegment[] Segments,
        float LocalMinX,
        float LocalMinY,
        float LocalMaxX,
        float LocalMaxY);

    private readonly record struct PendingRasterization(
        PathInfo Info,
        GpuPathRecord[] RecordsA,
        GpuPathSegment[] SegmentsA,
        GpuPathRecord[] RecordsB,
        GpuPathSegment[] SegmentsB,
        int RecordOffsetA,
        int SegmentOffsetA,
        int RecordOffsetB,
        int SegmentOffsetB,
        uint PathOpKind,
        int OutputByteOffset,
        uint OutputBytesPerRow);

    private readonly record struct RasterizationDispatch(
        int StartIndex,
        int Count,
        uint WorkgroupsX,
        uint WorkgroupsY,
        int UniformByteOffset,
        int UniformByteSize);

    private sealed class PendingRasterizationComparer : IComparer<PendingRasterization>
    {
        public static readonly PendingRasterizationComparer Instance = new();

        public int Compare(PendingRasterization left, PendingRasterization right)
        {
            int xComparison = DivRoundUp(DivRoundUp(left.Info.Width, 4), 16).CompareTo(
                DivRoundUp(DivRoundUp(right.Info.Width, 4), 16));
            return xComparison != 0
                ? xComparison
                : DivRoundUp(left.Info.Height, 16).CompareTo(
                    DivRoundUp(right.Info.Height, 16));
        }
    }

    private void RepackActivePaths()
    {
        ProGpuVectorDiagnostics.WriteLine(
            $"[PathAtlas] Repacking generation {Generation} with {_paths.Count} cached paths at frame {_frameNumber}.");
        Generation++;
        PathInfo[]? activePaths = null;
        int activePathCount = 0;

        try
        {
            var pathEnumerator = _paths.GetEnumerator();
            while (pathEnumerator.MoveNext())
            {
                var kvp = pathEnumerator.Current;
                if (kvp.Value.LastUsedFrame == _frameNumber)
                {
                    PooledRemovalBuffer.Add(ref activePaths, ref activePathCount, _paths.Count, kvp.Value);
                }
            }

            _paths.Clear();
            _currentX = 2;
            _currentY = 2;
            _currentRowHeight = 0;

            ClearAtlasTexture();

            _pendingPaths.Clear();

            for (int i = 0; i < activePathCount; i++)
            {
                var info = activePaths![i];
                uint gW = info.Width;
                uint gH = info.Height;

                if (_currentX + gW + 2 > _atlasWidth)
                {
                    _currentX = 2;
                    _currentY += _currentRowHeight + 2;
                    _currentRowHeight = 0;
                }

                if (_currentY + gH + 2 > _atlasHeight)
                {
                    ProGpuVectorDiagnostics.WriteLine("[PathAtlas] Warning: Even active paths in the current frame exceed the atlas size during repack!");
                    break;
                }

                uint posX = _currentX;
                uint posY = _currentY;

                _currentX += gW + 2;
                _currentRowHeight = Math.Max(_currentRowHeight, gH);

                float texelSizeX = 1.0f / _atlasWidth;
                float texelSizeY = 1.0f / _atlasHeight;
                var newInfo = new PathInfo
                {
                    Key = info.Key,
                    Geometry = info.Geometry,
                    UnscaledMinX = info.UnscaledMinX,
                    UnscaledMinY = info.UnscaledMinY,
                    UnscaledMaxX = info.UnscaledMaxX,
                    UnscaledMaxY = info.UnscaledMaxY,
                    X = posX,
                    Y = posY,
                    Width = gW,
                    Height = gH,
                    TexCoordMin = new Vector2(
                        (posX + info.Key.SubpixelX) * texelSizeX,
                        (posY + info.Key.SubpixelY) * texelSizeY),
                    TexCoordMax = new Vector2(
                        (posX + gW + info.Key.SubpixelX) * texelSizeX,
                        (posY + gH + info.Key.SubpixelY) * texelSizeY),
                    MinX = info.MinX,
                    MinY = info.MinY,
                    LastUsedFrame = info.LastUsedFrame
                };

                _paths[newInfo.Key] = newInfo;
                _pendingPaths.Add(newInfo);
            }

            // Repacking is already O(P) and invalidates every cached UV. Fold
            // the dictionary's former high-water capacity into the same rare
            // maintenance pass so a short-lived path burst does not pin a
            // large managed entry array after only a small live set remains.
            _paths.TrimExcess();
        }
        finally
        {
            PooledRemovalBuffer.Return(activePaths, activePathCount);
        }
    }

    private void ResetCachedPaths()
    {
        ProGpuVectorDiagnostics.WriteLine(
            $"[PathAtlas] Resetting generation {Generation} with {_paths.Count} cached and {_pendingPaths.Count} pending paths at frame {_frameNumber}.");
        Generation++;
        _paths.Clear();
        _pendingPaths.Clear();
        _currentX = 2;
        _currentY = 2;
        _currentRowHeight = 0;
        _recoveryFreeRectangles = null;
        CapacityExceeded = false;
        ClearAtlasTexture();
    }

    private bool TryResetAfterCapacityExceeded()
    {
        if (!CapacityExceeded)
        {
            return false;
        }

        ResetCachedPaths();
        return true;
    }

    public void ResetForRenderRetry()
    {
        // Algorithm: try four stable rectangle orderings against three deterministic
        // MaxRects placement heuristics. Each placement splits all intersecting free
        // regions, then compares only those new regions with the already-pruned set.
        // Recovery costs O(S * (P log P + P * F^2)) time in the adversarial case and
        // O(S * (P log P + P * F)) when each placement intersects a bounded number of
        // free regions, with O(P + F) retained space for S=12 strategies. A final exact search for at most ten
        // paths is capped at 25,000 nodes and 250,000 candidate placements, so an
        // adversarial set cannot stall the render thread. Normal insertion remains
        // the allocation-free O(1) shelf path. Recovery may allocate so a live
        // frame is not rejected merely because one command order or heuristic
        // fragmented otherwise usable atlas space. An interrupted incremental
        // compilation has not necessarily touched every path that the forced full
        // retry will consume, so first retain the most recently used older path
        // set as well (nested transactions may leave gaps in frame numbering).
        // If that conservative union cannot fit, retry the authoritative partial
        // live set alone rather than letting stale content force atlas growth.
        List<RetryPath> currentPaths = CollectCurrentFramePathsForRetry();
        List<RetryPath> retryPaths = CollectCurrentAndMostRecentFramePathsForRetry();
        bool packed = TryPackRecoveryPathsAtAvailableSize(
                retryPaths,
                out uint packedAtlasWidth,
                out uint packedAtlasHeight,
                out List<RetryPlacement> placements,
                out List<AtlasFreeRectangle> freeRectangles,
                out RetryPathOrdering ordering,
                out RecoveryPlacementHeuristic heuristic);
        if (!packed && retryPaths.Count != currentPaths.Count)
        {
            retryPaths = currentPaths;
            packed = TryPackRecoveryPathsAtAvailableSize(
                retryPaths,
                out packedAtlasWidth,
                out packedAtlasHeight,
                out placements,
                out freeRectangles,
                out ordering,
                out heuristic);
        }

        if (!packed)
        {
            for (int diagnosticIndex = 0; diagnosticIndex < retryPaths.Count; diagnosticIndex++)
            {
                RetryPath diagnosticPath = retryPaths[diagnosticIndex];
                ProGpuVectorDiagnostics.WriteLine(
                    $"[PathAtlas] Retry rectangle {diagnosticIndex}: {diagnosticPath.Width}x{diagnosticPath.Height}.");
            }
            string exactSearchStatus = LastExactRecoveryBudgetExceeded
                ? $"; exact recovery exhausted its deterministic work budget after " +
                    $"{LastExactRecoveryNodeCount} nodes and {LastExactRecoveryCandidateCount} candidates"
                : string.Empty;
            throw new InvalidOperationException(
                $"PathAtlas could not deterministically pack the live path set in the configured " +
                $"{_atlasWidth}x{_atlasHeight} atlas after multi-strategy retry packing " +
                $"({retryPaths.Count} live paths{exactSearchStatus}).");
        }

        ResetCachedPaths();
        if (packedAtlasWidth != _atlasWidth ||
            packedAtlasHeight != _atlasHeight)
        {
            ResizeEmptyAtlasForRecovery(
                packedAtlasWidth,
                packedAtlasHeight);
        }

        for (int index = 0; index < placements.Count; index++)
        {
            RetryPlacement retryPlacement = placements[index];
            RetryPath retryPath = retryPlacement.Path;
            PathInfo info = retryPath.Info;
            if (retryPath.Width == 0 || retryPath.Height == 0)
            {
                info.LastUsedFrame = _frameNumber;
                _paths[info.Key] = info;
                continue;
            }

            info = CreatePlacedPathInfo(
                info,
                retryPath.XStart,
                retryPath.YStart,
                retryPath.Width,
                retryPath.Height,
                retryPlacement.Rectangle.X,
                retryPlacement.Rectangle.Y);
            _paths[info.Key] = info;
            _pendingPaths.Add(info);
        }

        _recoveryFreeRectangles = freeRectangles;
        ProGpuVectorDiagnostics.WriteLine(
            $"[PathAtlas] Deterministically packed {retryPaths.Count} live paths for render retry " +
            $"using {ordering}/{heuristic}, with {freeRectangles.Count} free rectangles remaining.");
    }

    private bool TryPackRecoveryPathsAtAvailableSize(
        List<RetryPath> paths,
        out uint packedAtlasWidth,
        out uint packedAtlasHeight,
        out List<RetryPlacement> placements,
        out List<AtlasFreeRectangle> freeRectangles,
        out RetryPathOrdering ordering,
        out RecoveryPlacementHeuristic heuristic)
    {
        packedAtlasWidth = _atlasWidth;
        packedAtlasHeight = _atlasHeight;
        while (true)
        {
            if (TryPackRecoveryPaths(
                    paths,
                    packedAtlasWidth,
                    packedAtlasHeight,
                    allowExactSearch: true,
                    out placements,
                    out freeRectangles,
                    out ordering,
                    out heuristic))
            {
                return true;
            }

            if (!TryGetNextRecoveryAtlasSize(
                    packedAtlasWidth,
                    packedAtlasHeight,
                    out packedAtlasWidth,
                    out packedAtlasHeight))
            {
                placements = new List<RetryPlacement>();
                freeRectangles = new List<AtlasFreeRectangle>();
                ordering = default;
                heuristic = default;
                return false;
            }
        }
    }

    private bool TryGetNextRecoveryAtlasSize(
        uint width,
        uint height,
        out uint nextWidth,
        out uint nextHeight)
    {
        nextWidth = width;
        nextHeight = height;
        if (width >= _maxAtlasSize && height >= _maxAtlasSize)
        {
            return false;
        }

        if (height < _maxAtlasSize &&
            (height <= width || width >= _maxAtlasSize))
        {
            nextHeight = Math.Min(_maxAtlasSize, checked(height * 2));
        }
        else if (width < _maxAtlasSize)
        {
            nextWidth = Math.Min(_maxAtlasSize, checked(width * 2));
        }

        return nextWidth != width || nextHeight != height;
    }

    private void ResizeEmptyAtlasForRecovery(uint width, uint height)
    {
        GpuTexture replacement = new(
            _context,
            width,
            height,
            TextureFormat.R8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc |
            TextureUsage.RenderAttachment,
            "Dynamic Path Coverage Atlas");
        replacement.ClearRenderTarget();

        GpuTexture previous = _atlasTexture;
        uint previousWidth = _atlasWidth;
        uint previousHeight = _atlasHeight;
        _atlasTexture = replacement;
        _atlasWidth = width;
        _atlasHeight = height;
        previous.Dispose();
        TextureRevision++;
        Generation++;
        AtlasGrowthCount++;
        _framesSinceAtlasResize = 0;
        CapacityExceeded = false;
        ProGpuVectorDiagnostics.WriteLine(
            $"[PathAtlas] Grew empty recovery atlas from {previousWidth}x{previousHeight} " +
            $"to {width}x{height} without copying invalidated coverage.");
    }

    private bool TryPackRecoveryPaths(
        List<RetryPath> livePaths,
        uint atlasWidth,
        uint atlasHeight,
        bool allowExactSearch,
        out List<RetryPlacement> placements,
        out List<AtlasFreeRectangle> freeRectangles,
        out RetryPathOrdering successfulOrdering,
        out RecoveryPlacementHeuristic successfulHeuristic)
    {
        LastExactRecoveryNodeCount = 0;
        LastExactRecoveryCandidateCount = 0;
        LastExactRecoveryBudgetExceeded = false;
        uint availableWidth = atlasWidth > 2 ? atlasWidth - 2 : 0;
        uint availableHeight = atlasHeight > 2 ? atlasHeight - 2 : 0;
        var trialFreeRectangles = new List<AtlasFreeRectangle>(Math.Max(4, livePaths.Count * 2));
        var trialPlacements = new List<RetryPlacement>(livePaths.Count);

        for (int orderingIndex = 0;
             orderingIndex <= (int)RetryPathOrdering.MaxSideDescending;
             orderingIndex++)
        {
            RetryPathOrdering ordering = (RetryPathOrdering)orderingIndex;
            SortRetryPaths(livePaths, ordering);

            for (int heuristicIndex = 0;
                 heuristicIndex < (int)RecoveryPlacementHeuristic.ExactBranchAndBound;
                 heuristicIndex++)
            {
                RecoveryPlacementHeuristic heuristic = (RecoveryPlacementHeuristic)heuristicIndex;
                trialFreeRectangles.Clear();
                trialFreeRectangles.Add(new AtlasFreeRectangle(
                    2,
                    2,
                    availableWidth,
                    availableHeight));
                trialPlacements.Clear();
                bool succeeded = true;

                for (int pathIndex = 0; pathIndex < livePaths.Count; pathIndex++)
                {
                    RetryPath retryPath = livePaths[pathIndex];
                    if (retryPath.Width == 0 || retryPath.Height == 0)
                    {
                        trialPlacements.Add(new RetryPlacement(retryPath, default));
                        continue;
                    }

                    if (!TryPlaceRecoveryRectangle(
                            trialFreeRectangles,
                            checked(retryPath.Width + 2),
                            checked(retryPath.Height + 2),
                            heuristic,
                            out AtlasFreeRectangle rectangle))
                    {
                        succeeded = false;
                        break;
                    }

                    trialPlacements.Add(new RetryPlacement(retryPath, rectangle));
                }

                if (succeeded)
                {
                    placements = trialPlacements;
                    freeRectangles = trialFreeRectangles;
                    successfulOrdering = ordering;
                    successfulHeuristic = heuristic;
                    return true;
                }
            }
        }

        if (allowExactSearch &&
            TryPackRecoveryPathsExactly(
                livePaths,
                atlasWidth,
                atlasHeight,
                out placements,
                out freeRectangles))
        {
            successfulOrdering = RetryPathOrdering.AreaDescending;
            successfulHeuristic = RecoveryPlacementHeuristic.ExactBranchAndBound;
            return true;
        }

        placements = new List<RetryPlacement>();
        freeRectangles = new List<AtlasFreeRectangle>();
        successfulOrdering = default;
        successfulHeuristic = default;
        return false;
    }

    private static void SortRetryPaths(List<RetryPath> paths, RetryPathOrdering ordering)
    {
        // Each comparison is non-capturing so recovery reuses the runtime-cached
        // delegate. The total key ordering makes an in-place re-sort independent
        // of the preceding strategy, avoiding one PathInfo-heavy array copy per
        // attempted ordering without changing deterministic placement order.
        switch (ordering)
        {
            case RetryPathOrdering.WidthDescending:
                paths.Sort(static (left, right) =>
                    CompareRetryPaths(left, right, RetryPathOrdering.WidthDescending));
                break;
            case RetryPathOrdering.HeightDescending:
                paths.Sort(static (left, right) =>
                    CompareRetryPaths(left, right, RetryPathOrdering.HeightDescending));
                break;
            case RetryPathOrdering.MaxSideDescending:
                paths.Sort(static (left, right) =>
                    CompareRetryPaths(left, right, RetryPathOrdering.MaxSideDescending));
                break;
            default:
                paths.Sort(static (left, right) =>
                    CompareRetryPaths(left, right, RetryPathOrdering.AreaDescending));
                break;
        }
    }

    private bool TryPackRecoveryPathsExactly(
        List<RetryPath> livePaths,
        uint atlasWidth,
        uint atlasHeight,
        out List<RetryPlacement> placements,
        out List<AtlasFreeRectangle> freeRectangles)
    {
        uint availableWidth = atlasWidth > 2 ? atlasWidth - 2 : 0;
        uint availableHeight = atlasHeight > 2 ? atlasHeight - 2 : 0;
        var orderedPaths = new List<RetryPath>(livePaths.Count);
        var emptyPaths = new List<RetryPath>();
        ulong packedArea = 0;
        for (int pathIndex = 0; pathIndex < livePaths.Count; pathIndex++)
        {
            RetryPath path = livePaths[pathIndex];
            if (path.Width == 0 || path.Height == 0)
            {
                emptyPaths.Add(path);
                continue;
            }

            uint packedWidth = checked(path.Width + 2);
            uint packedHeight = checked(path.Height + 2);
            if (packedWidth > availableWidth || packedHeight > availableHeight)
            {
                placements = new List<RetryPlacement>();
                freeRectangles = new List<AtlasFreeRectangle>();
                return false;
            }

            packedArea += (ulong)packedWidth * packedHeight;
            orderedPaths.Add(path);
        }

        if (orderedPaths.Count > ExactRecoveryPathLimit ||
            packedArea > (ulong)availableWidth * availableHeight)
        {
            placements = new List<RetryPlacement>();
            freeRectangles = new List<AtlasFreeRectangle>();
            return false;
        }

        if (ExceedsExactRecoveryIncompatibilityBound(
                orderedPaths,
                availableWidth,
                availableHeight,
                useWidths: true) ||
            ExceedsExactRecoveryIncompatibilityBound(
                orderedPaths,
                availableHeight,
                availableWidth,
                useWidths: false))
        {
            placements = new List<RetryPlacement>();
            freeRectangles = new List<AtlasFreeRectangle>();
            return false;
        }

        orderedPaths.Sort(static (left, right) =>
            CompareRetryPaths(left, right, RetryPathOrdering.AreaDescending));
        uint[] xCoordinates = BuildExactRecoveryCoordinates(
            orderedPaths,
            availableWidth,
            useWidth: true);
        uint[] yCoordinates = BuildExactRecoveryCoordinates(
            orderedPaths,
            availableHeight,
            useWidth: false);
        var placedRectangles = new AtlasFreeRectangle[orderedPaths.Count];

        // Algorithm: every integral orthogonal packing can be translated into a
        // bottom-left-stable packing. Each stable x/y origin is therefore a sum
        // of a chain of rectangle widths/heights ending at the corresponding
        // atlas edge. Enumerating those finite subset-sum coordinates and
        // backtracking over non-overlapping placements is exact for this bounded
        // recovery set. Time is O(P * X * Y * B) in each search node with an
        // exponential O((X*Y)^P) theoretical worst case, but the deterministic
        // node/candidate budgets cap actual work; space is O(P + X + Y), P <= 10.
        var searchState = new ExactRecoverySearchState();
        bool packed = TryPlaceExactRecoveryPath(
                orderedPaths,
                xCoordinates,
                yCoordinates,
                availableWidth,
                availableHeight,
                placedRectangles,
                pathIndex: 0,
                ref searchState);
        LastExactRecoveryNodeCount = searchState.NodeCount;
        LastExactRecoveryCandidateCount = searchState.CandidateCount;
        LastExactRecoveryBudgetExceeded = searchState.BudgetExceeded;
        if (!packed)
        {
            placements = new List<RetryPlacement>();
            freeRectangles = new List<AtlasFreeRectangle>();
            return false;
        }

        placements = new List<RetryPlacement>(livePaths.Count);
        freeRectangles = new List<AtlasFreeRectangle>(Math.Max(4, orderedPaths.Count * 2))
        {
            new AtlasFreeRectangle(2, 2, availableWidth, availableHeight)
        };
        for (int pathIndex = 0; pathIndex < orderedPaths.Count; pathIndex++)
        {
            AtlasFreeRectangle local = placedRectangles[pathIndex];
            var atlasRectangle = new AtlasFreeRectangle(
                checked(local.X + 2),
                checked(local.Y + 2),
                local.Width,
                local.Height);
            placements.Add(new RetryPlacement(orderedPaths[pathIndex], atlasRectangle));
            SplitRecoveryFreeRectangles(freeRectangles, atlasRectangle);
        }

        for (int pathIndex = 0; pathIndex < emptyPaths.Count; pathIndex++)
        {
            placements.Add(new RetryPlacement(emptyPaths[pathIndex], default));
        }

        return true;
    }

    private static bool ExceedsExactRecoveryIncompatibilityBound(
        List<RetryPath> paths,
        uint parallelExtent,
        uint perpendicularExtentLimit,
        bool useWidths)
    {
        int subsetCount = 1 << paths.Count;
        for (int subset = 3; subset < subsetCount; subset++)
        {
            ulong perpendicularExtent = 0;
            bool pairwiseIncompatible = true;
            for (int leftIndex = 0; leftIndex < paths.Count && pairwiseIncompatible; leftIndex++)
            {
                if ((subset & (1 << leftIndex)) == 0)
                {
                    continue;
                }

                RetryPath left = paths[leftIndex];
                perpendicularExtent += (useWidths ? left.Height : left.Width) + 2UL;
                ulong leftDimension = (useWidths ? left.Width : left.Height) + 2UL;
                for (int rightIndex = leftIndex + 1; rightIndex < paths.Count; rightIndex++)
                {
                    if ((subset & (1 << rightIndex)) == 0)
                    {
                        continue;
                    }

                    RetryPath right = paths[rightIndex];
                    ulong rightDimension = (useWidths ? right.Width : right.Height) + 2UL;
                    if (leftDimension + rightDimension <= parallelExtent)
                    {
                        pairwiseIncompatible = false;
                        break;
                    }
                }
            }

            if (pairwiseIncompatible && perpendicularExtent > perpendicularExtentLimit)
            {
                return true;
            }
        }

        return false;
    }

    private static uint[] BuildExactRecoveryCoordinates(
        List<RetryPath> paths,
        uint extent,
        bool useWidth)
    {
        var coordinates = new HashSet<uint> { 0 };
        for (int pathIndex = 0; pathIndex < paths.Count; pathIndex++)
        {
            uint dimension = checked((useWidth ? paths[pathIndex].Width : paths[pathIndex].Height) + 2);
            var existing = new uint[coordinates.Count];
            coordinates.CopyTo(existing);
            for (int coordinateIndex = 0; coordinateIndex < existing.Length; coordinateIndex++)
            {
                uint coordinate = existing[coordinateIndex];
                if (coordinate <= extent - dimension)
                {
                    coordinates.Add(coordinate + dimension);
                }
            }
        }

        var result = new uint[coordinates.Count];
        coordinates.CopyTo(result);
        Array.Sort(result);
        return result;
    }

    private static bool TryPlaceExactRecoveryPath(
        List<RetryPath> paths,
        uint[] xCoordinates,
        uint[] yCoordinates,
        uint widthExtent,
        uint heightExtent,
        AtlasFreeRectangle[] placedRectangles,
        int pathIndex,
        ref ExactRecoverySearchState searchState)
    {
        if (!searchState.TryEnterNode())
        {
            return false;
        }

        if (pathIndex >= paths.Count)
        {
            return true;
        }

        RetryPath path = paths[pathIndex];
        uint width = checked(path.Width + 2);
        uint height = checked(path.Height + 2);
        for (int yIndex = 0; yIndex < yCoordinates.Length; yIndex++)
        {
            uint y = yCoordinates[yIndex];
            if (y > heightExtent - height)
            {
                break;
            }

            for (int xIndex = 0; xIndex < xCoordinates.Length; xIndex++)
            {
                if (!searchState.TryVisitCandidate())
                {
                    return false;
                }

                uint x = xCoordinates[xIndex];
                if (x > widthExtent - width)
                {
                    break;
                }

                var candidate = new AtlasFreeRectangle(x, y, width, height);
                if (OverlapsExactRecoveryPlacement(candidate, placedRectangles, pathIndex))
                {
                    continue;
                }

                placedRectangles[pathIndex] = candidate;
                if (TryPlaceExactRecoveryPath(
                        paths,
                        xCoordinates,
                        yCoordinates,
                        widthExtent,
                        heightExtent,
                        placedRectangles,
                        pathIndex + 1,
                        ref searchState))
                {
                    return true;
                }

                if (searchState.BudgetExceeded)
                {
                    return false;
                }
            }
        }

        placedRectangles[pathIndex] = default;
        return false;
    }

    private static bool OverlapsExactRecoveryPlacement(
        AtlasFreeRectangle candidate,
        AtlasFreeRectangle[] placedRectangles,
        int placedCount)
    {
        for (int placedIndex = 0; placedIndex < placedCount; placedIndex++)
        {
            AtlasFreeRectangle placed = placedRectangles[placedIndex];
            if (candidate.X < placed.Right && candidate.Right > placed.X &&
                candidate.Y < placed.Bottom && candidate.Bottom > placed.Y)
            {
                return true;
            }
        }

        return false;
    }

    private List<RetryPath> CollectCurrentFramePathsForRetry()
    {
        var livePaths = new List<RetryPath>(_paths.Count);
        foreach (PathInfo info in _paths.Values)
        {
            if (info.LastUsedFrame != _frameNumber)
            {
                continue;
            }

            if (TryResolveRasterRectangle(
                    info,
                    out int xStart,
                    out int yStart,
                    out uint width,
                    out uint height))
            {
                livePaths.Add(new RetryPath(info, xStart, yStart, width, height));
            }
            else
            {
                livePaths.Add(new RetryPath(info, 0, 0, 0, 0));
            }
        }

        return livePaths;
    }

    private List<RetryPath> CollectCurrentAndMostRecentFramePathsForRetry()
    {
        uint mostRecentOlderFrame = 0;
        bool hasOlderFrame = false;
        foreach (PathInfo info in _paths.Values)
        {
            if (info.LastUsedFrame >= _frameNumber)
            {
                continue;
            }

            if (!hasOlderFrame || info.LastUsedFrame > mostRecentOlderFrame)
            {
                mostRecentOlderFrame = info.LastUsedFrame;
                hasOlderFrame = true;
            }
        }

        var livePaths = new List<RetryPath>(_paths.Count);
        foreach (PathInfo info in _paths.Values)
        {
            if (info.LastUsedFrame != _frameNumber &&
                (!hasOlderFrame || info.LastUsedFrame != mostRecentOlderFrame))
            {
                continue;
            }

            if (TryResolveRasterRectangle(
                    info,
                    out int xStart,
                    out int yStart,
                    out uint width,
                    out uint height))
            {
                livePaths.Add(new RetryPath(info, xStart, yStart, width, height));
            }
            else
            {
                livePaths.Add(new RetryPath(info, 0, 0, 0, 0));
            }
        }

        return livePaths;
    }

    private List<RetryPath> CollectMostRecentlyUsedPathsForRetry()
    {
        uint mostRecentFrame = 0;
        bool found = false;
        foreach (PathInfo info in _paths.Values)
        {
            if (!found || info.LastUsedFrame > mostRecentFrame)
            {
                mostRecentFrame = info.LastUsedFrame;
                found = true;
            }
        }

        var livePaths = new List<RetryPath>(_paths.Count);
        if (!found)
        {
            return livePaths;
        }

        foreach (PathInfo info in _paths.Values)
        {
            if (info.LastUsedFrame != mostRecentFrame)
            {
                continue;
            }

            if (TryResolveRasterRectangle(
                    info,
                    out int xStart,
                    out int yStart,
                    out uint width,
                    out uint height))
            {
                livePaths.Add(new RetryPath(
                    info,
                    xStart,
                    yStart,
                    width,
                    height));
            }
            else
            {
                livePaths.Add(new RetryPath(info, 0, 0, 0, 0));
            }
        }

        return livePaths;
    }

    /// <summary>
    /// Marks that the current presentation reused compiled vertices containing
    /// path-atlas coordinates. The next shrink probe conservatively retains the
    /// most recently compiled path set even though CPU compilation did not call
    /// <see cref="GetOrCreatePath"/> again.
    /// </summary>
    public void MarkRetainedPathReplay()
    {
        _retainedPathReplayObserved = true;
    }

    private void TryShrinkAtlas()
    {
        if (_framesSinceAtlasResize < DefaultAtlasShrinkDelayFrames ||
            _pendingPaths.Count != 0 ||
            (_atlasWidth <= _initialAtlasSize && _atlasHeight <= _initialAtlasSize))
        {
            return;
        }

        // Shrink evaluation is deliberately infrequent. Reset the delay even
        // when the preceding frame's active set cannot fit a materially smaller
        // texture, avoiding per-frame packing work after the hysteresis interval.
        _framesSinceAtlasResize = 0;
        List<RetryPath> activePaths = CollectCurrentFramePathsForRetry();
        if (activePaths.Count == 0 && _retainedPathReplayObserved)
        {
            activePaths = CollectMostRecentlyUsedPathsForRetry();
        }
        uint desiredWidth = _atlasWidth;
        uint desiredHeight = _atlasHeight;
        List<RetryPlacement>? selectedPlacements = null;
        List<AtlasFreeRectangle>? selectedFreeRectangles = null;

        while (true)
        {
            uint halfWidth = desiredWidth > _initialAtlasSize
                ? Math.Max(_initialAtlasSize, desiredWidth / 2)
                : desiredWidth;
            uint halfHeight = desiredHeight > _initialAtlasSize
                ? Math.Max(_initialAtlasSize, desiredHeight / 2)
                : desiredHeight;
            bool packed = false;

            if (halfWidth != desiredWidth && halfHeight != desiredHeight &&
                TryPackShrinkCandidate(
                    activePaths,
                    halfWidth,
                    halfHeight,
                    out List<RetryPlacement> bothPlacements,
                    out List<AtlasFreeRectangle> bothFreeRectangles))
            {
                desiredWidth = halfWidth;
                desiredHeight = halfHeight;
                selectedPlacements = bothPlacements;
                selectedFreeRectangles = bothFreeRectangles;
                packed = true;
            }
            else if (halfWidth != desiredWidth &&
                     TryPackShrinkCandidate(
                         activePaths,
                         halfWidth,
                         desiredHeight,
                         out List<RetryPlacement> widthPlacements,
                         out List<AtlasFreeRectangle> widthFreeRectangles))
            {
                desiredWidth = halfWidth;
                selectedPlacements = widthPlacements;
                selectedFreeRectangles = widthFreeRectangles;
                packed = true;
            }
            else if (halfHeight != desiredHeight &&
                     TryPackShrinkCandidate(
                         activePaths,
                         desiredWidth,
                         halfHeight,
                         out List<RetryPlacement> heightPlacements,
                         out List<AtlasFreeRectangle> heightFreeRectangles))
            {
                desiredHeight = halfHeight;
                selectedPlacements = heightPlacements;
                selectedFreeRectangles = heightFreeRectangles;
                packed = true;
            }

            if (!packed)
            {
                break;
            }
        }

        TryRefineShrinkCandidate(
            activePaths,
            desiredWidth,
            desiredHeight,
            shrinkWidthFirst: true,
            out uint widthFirstWidth,
            out uint widthFirstHeight,
            out List<RetryPlacement>? widthFirstPlacements,
            out List<AtlasFreeRectangle>? widthFirstFreeRectangles);
        TryRefineShrinkCandidate(
            activePaths,
            desiredWidth,
            desiredHeight,
            shrinkWidthFirst: false,
            out uint heightFirstWidth,
            out uint heightFirstHeight,
            out List<RetryPlacement>? heightFirstPlacements,
            out List<AtlasFreeRectangle>? heightFirstFreeRectangles);

        ulong selectedArea = (ulong)desiredWidth * desiredHeight;
        ulong widthFirstArea = (ulong)widthFirstWidth * widthFirstHeight;
        if (widthFirstPlacements is not null &&
            widthFirstFreeRectangles is not null &&
            widthFirstArea < selectedArea)
        {
            desiredWidth = widthFirstWidth;
            desiredHeight = widthFirstHeight;
            selectedPlacements = widthFirstPlacements;
            selectedFreeRectangles = widthFirstFreeRectangles;
            selectedArea = widthFirstArea;
        }

        ulong heightFirstArea = (ulong)heightFirstWidth * heightFirstHeight;
        if (heightFirstPlacements is not null &&
            heightFirstFreeRectangles is not null &&
            heightFirstArea < selectedArea)
        {
            desiredWidth = heightFirstWidth;
            desiredHeight = heightFirstHeight;
            selectedPlacements = heightFirstPlacements;
            selectedFreeRectangles = heightFirstFreeRectangles;
        }

        ulong currentArea = (ulong)_atlasWidth * _atlasHeight;
        ulong desiredArea = (ulong)desiredWidth * desiredHeight;
        // Repacking invalidates every cached UV, so require at least a 25%
        // residency reduction before paying that cost.
        if (selectedPlacements == null ||
            selectedFreeRectangles == null ||
            desiredArea * MinimumAtlasShrinkAreaNumerator >
                currentArea * MinimumAtlasShrinkAreaDenominator)
        {
            return;
        }

        var newTexture = new GpuTexture(
            _context,
            desiredWidth,
            desiredHeight,
            TextureFormat.R8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc |
            TextureUsage.RenderAttachment,
            "Dynamic Path Coverage Atlas");
        newTexture.ClearRenderTarget();

        GpuTexture oldTexture = _atlasTexture;
        uint oldWidth = _atlasWidth;
        uint oldHeight = _atlasHeight;
        _atlasTexture = newTexture;
        _atlasWidth = desiredWidth;
        _atlasHeight = desiredHeight;
        _paths.Clear();
        _pendingPaths.Clear();
        _currentX = 2;
        _currentY = 2;
        _currentRowHeight = 0;

        for (int index = 0; index < selectedPlacements.Count; index++)
        {
            RetryPlacement placement = selectedPlacements[index];
            RetryPath path = placement.Path;
            PathInfo info = path.Info;
            if (path.Width > 0 && path.Height > 0)
            {
                info = CreatePlacedPathInfo(
                    info,
                    path.XStart,
                    path.YStart,
                    path.Width,
                    path.Height,
                    placement.Rectangle.X,
                    placement.Rectangle.Y);
                _pendingPaths.Add(info);
            }

            _paths[info.Key] = info;
        }

        // The old atlas may have admitted many transient phase variants before
        // this stable-set shrink. Compact the CPU lookup alongside the GPU
        // repack; steady lookup remains O(1), while retained storage is O(P)
        // for the P paths that survived the maintenance pass.
        _paths.TrimExcess();

        _recoveryFreeRectangles = selectedFreeRectangles;
        CapacityExceeded = false;
        TextureRevision++;
        Generation++;
        AtlasShrinkCount++;
        oldTexture.Dispose();
        ProGpuVectorDiagnostics.WriteLine(
            $"[PathAtlas] Shrunk stable atlas residency from {oldWidth}x{oldHeight} to " +
            $"{desiredWidth}x{desiredHeight}; retained {activePaths.Count} paths from the preceding " +
            $"frame and invalidated stale UVs for generation {Generation}.");
        if (ProGpuVectorDiagnostics.IsEnabled)
        {
            ProGpuVectorDiagnostics.WriteLine(
                $"[PathAtlas] Stable raster rectangles: {DescribeCurrentFrameRasterRectangles()}.");
        }
    }

    private void TryRefineShrinkCandidate(
        List<RetryPath> activePaths,
        uint startingWidth,
        uint startingHeight,
        bool shrinkWidthFirst,
        out uint selectedWidth,
        out uint selectedHeight,
        out List<RetryPlacement>? selectedPlacements,
        out List<AtlasFreeRectangle>? selectedFreeRectangles)
    {
        // Algorithm: after the power-of-two probe, greedily trim each axis in
        // 256-texel steps and try both axis orders. For atlas dimensions W/H,
        // step A, P live paths, and F MaxRects free regions, this performs at
        // most 2 * ((W + H) / A) bounded packing probes. Each probe uses the
        // recovery packer's O(P log P + P * F^2) worst-case work and O(P + F)
        // temporary storage; the default 4096 atlas has at most 28 probes per
        // order. Shrink runs only after the 240-frame hysteresis interval.
        uint refinedWidth = startingWidth;
        uint refinedHeight = startingHeight;
        List<RetryPlacement>? refinedPlacements = null;
        List<AtlasFreeRectangle>? refinedFreeRectangles = null;

        void ShrinkWidth()
        {
            while (refinedWidth > _initialAtlasSize)
            {
                uint candidateWidth = refinedWidth > _initialAtlasSize + AtlasShrinkDimensionStep
                    ? refinedWidth - AtlasShrinkDimensionStep
                    : _initialAtlasSize;
                if (!TryPackShrinkCandidate(
                        activePaths,
                        candidateWidth,
                        refinedHeight,
                        out List<RetryPlacement> placements,
                        out List<AtlasFreeRectangle> freeRectangles))
                {
                    break;
                }

                refinedWidth = candidateWidth;
                refinedPlacements = placements;
                refinedFreeRectangles = freeRectangles;
            }
        }

        void ShrinkHeight()
        {
            while (refinedHeight > _initialAtlasSize)
            {
                uint candidateHeight = refinedHeight > _initialAtlasSize + AtlasShrinkDimensionStep
                    ? refinedHeight - AtlasShrinkDimensionStep
                    : _initialAtlasSize;
                if (!TryPackShrinkCandidate(
                        activePaths,
                        refinedWidth,
                        candidateHeight,
                        out List<RetryPlacement> placements,
                        out List<AtlasFreeRectangle> freeRectangles))
                {
                    break;
                }

                refinedHeight = candidateHeight;
                refinedPlacements = placements;
                refinedFreeRectangles = freeRectangles;
            }
        }

        if (shrinkWidthFirst)
        {
            ShrinkWidth();
            ShrinkHeight();
        }
        else
        {
            ShrinkHeight();
            ShrinkWidth();
        }

        selectedWidth = refinedWidth;
        selectedHeight = refinedHeight;
        selectedPlacements = refinedPlacements;
        selectedFreeRectangles = refinedFreeRectangles;
    }

    private bool TryPackShrinkCandidate(
        List<RetryPath> activePaths,
        uint width,
        uint height,
        out List<RetryPlacement> placements,
        out List<AtlasFreeRectangle> freeRectangles)
    {
        return TryPackRecoveryPaths(
            activePaths,
            width,
            height,
            allowExactSearch: false,
            out placements,
            out freeRectangles,
            out _,
            out _);
    }

    private bool TryResolveRasterRectangle(
        PathInfo info,
        out int xStart,
        out int yStart,
        out uint width,
        out uint height)
    {
        if (info.Width > 0 && info.Height > 0)
        {
            xStart = checked((int)info.MinX);
            yStart = checked((int)info.MinY);
            width = info.Width;
            height = info.Height;
            return true;
        }

        if (info.RetryWidth > 0 && info.RetryHeight > 0)
        {
            xStart = info.RetryXStart;
            yStart = info.RetryYStart;
            width = info.RetryWidth;
            height = info.RetryHeight;
            return true;
        }

        if (!TryGetRasterBounds(
                info.Geometry,
                out float unscaledMinX,
                out float unscaledMinY,
                out float unscaledMaxX,
                out float unscaledMaxY))
        {
            xStart = 0;
            yStart = 0;
            width = 0;
            height = 0;
            return false;
        }

        float minX = unscaledMinX * info.Key.ScaleX;
        float minY = unscaledMinY * info.Key.ScaleY;
        float maxX = unscaledMaxX * info.Key.ScaleX;
        float maxY = unscaledMaxY * info.Key.ScaleY;
        const int padding = 4;
        xStart = checked((int)Math.Floor(minX) - padding);
        int xEnd = checked((int)Math.Ceiling(maxX) + padding);
        yStart = checked((int)Math.Floor(minY) - padding);
        int yEnd = checked((int)Math.Ceiling(maxY) + padding);
        int resolvedWidth = xEnd - xStart;
        int resolvedHeight = yEnd - yStart;
        if (resolvedWidth <= 0 || resolvedHeight <= 0)
        {
            width = 0;
            height = 0;
            return false;
        }

        width = checked((uint)resolvedWidth);
        height = checked((uint)resolvedHeight);
        return true;
    }

    private PathInfo CreatePlacedPathInfo(
        PathInfo source,
        int xStart,
        int yStart,
        uint width,
        uint height,
        uint atlasX,
        uint atlasY)
    {
        float texelSizeX = 1.0f / _atlasWidth;
        float texelSizeY = 1.0f / _atlasHeight;
        source.X = atlasX;
        source.Y = atlasY;
        source.Width = width;
        source.Height = height;
        source.TexCoordMin = new Vector2(
            (atlasX + source.Key.SubpixelX) * texelSizeX,
            (atlasY + source.Key.SubpixelY) * texelSizeY);
        source.TexCoordMax = new Vector2(
            (atlasX + width + source.Key.SubpixelX) * texelSizeX,
            (atlasY + height + source.Key.SubpixelY) * texelSizeY);
        source.MinX = xStart;
        source.MinY = yStart;
        source.LastUsedFrame = _frameNumber;
        source.RetryXStart = 0;
        source.RetryYStart = 0;
        source.RetryWidth = 0;
        source.RetryHeight = 0;
        return source;
    }

    private static int CompareRetryPaths(
        RetryPath left,
        RetryPath right,
        RetryPathOrdering ordering)
    {
        ulong leftArea = (ulong)(left.Width + 2) * (left.Height + 2);
        ulong rightArea = (ulong)(right.Width + 2) * (right.Height + 2);
        uint leftMaxSide = Math.Max(left.Width, left.Height);
        uint rightMaxSide = Math.Max(right.Width, right.Height);
        int comparison = ordering switch
        {
            RetryPathOrdering.WidthDescending => right.Width.CompareTo(left.Width),
            RetryPathOrdering.HeightDescending => right.Height.CompareTo(left.Height),
            RetryPathOrdering.MaxSideDescending => rightMaxSide.CompareTo(leftMaxSide),
            _ => rightArea.CompareTo(leftArea)
        };
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = rightArea.CompareTo(leftArea);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = rightMaxSide.CompareTo(leftMaxSide);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.Height.CompareTo(left.Height);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.Width.CompareTo(left.Width);
        if (comparison != 0)
        {
            return comparison;
        }

        return CompareRetryPathKeys(left.Info.Key, right.Info.Key);
    }

    private static int CompareRetryPathKeys(PathCacheKey left, PathCacheKey right)
    {
        // PathCacheKey equality covers content, both scales, both phases, and the
        // sample grid. Comparing every field therefore gives a total order for
        // the distinct keys held by _paths, even when rectangles have equal size.
        int comparison = left.ContentHash.CompareTo(right.ContentHash);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = BitConverter.SingleToInt32Bits(left.ScaleX)
            .CompareTo(BitConverter.SingleToInt32Bits(right.ScaleX));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = BitConverter.SingleToInt32Bits(left.ScaleY)
            .CompareTo(BitConverter.SingleToInt32Bits(right.ScaleY));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = BitConverter.SingleToInt32Bits(left.SubpixelX)
            .CompareTo(BitConverter.SingleToInt32Bits(right.SubpixelX));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = BitConverter.SingleToInt32Bits(left.SubpixelY)
            .CompareTo(BitConverter.SingleToInt32Bits(right.SubpixelY));
        return comparison != 0
            ? comparison
            : left.SampleGrid.CompareTo(right.SampleGrid);
    }

    private static bool TryPlaceRecoveryRectangle(
        List<AtlasFreeRectangle> freeRectangles,
        uint width,
        uint height,
        out AtlasFreeRectangle placement) =>
        TryPlaceRecoveryRectangle(
            freeRectangles,
            width,
            height,
            RecoveryPlacementHeuristic.BestShortSideFit,
            out placement);

    private static bool TryPlaceRecoveryRectangle(
        List<AtlasFreeRectangle> freeRectangles,
        uint width,
        uint height,
        RecoveryPlacementHeuristic heuristic,
        out AtlasFreeRectangle placement)
    {
        int bestIndex = -1;
        ulong bestPrimary = ulong.MaxValue;
        ulong bestSecondary = ulong.MaxValue;
        ulong bestTertiary = ulong.MaxValue;
        ulong bestQuaternary = ulong.MaxValue;
        ulong bestQuinary = ulong.MaxValue;

        for (int index = 0; index < freeRectangles.Count; index++)
        {
            AtlasFreeRectangle free = freeRectangles[index];
            if (width > free.Width || height > free.Height)
            {
                continue;
            }

            ulong remainingWidth = free.Width - width;
            ulong remainingHeight = free.Height - height;
            ulong shortSide = Math.Min(remainingWidth, remainingHeight);
            ulong longSide = Math.Max(remainingWidth, remainingHeight);
            ulong areaWaste = (ulong)free.Width * free.Height - (ulong)width * height;
            ulong primary;
            ulong secondary;
            ulong tertiary;
            ulong quaternary;
            ulong quinary;
            switch (heuristic)
            {
                case RecoveryPlacementHeuristic.BestAreaFit:
                    primary = areaWaste;
                    secondary = shortSide;
                    tertiary = longSide;
                    quaternary = free.Y;
                    quinary = free.X;
                    break;
                case RecoveryPlacementHeuristic.BottomLeft:
                    primary = (ulong)free.Y + height;
                    secondary = free.X;
                    tertiary = shortSide;
                    quaternary = longSide;
                    quinary = areaWaste;
                    break;
                default:
                    primary = shortSide;
                    secondary = longSide;
                    tertiary = areaWaste;
                    quaternary = free.Y;
                    quinary = free.X;
                    break;
            }

            if (primary < bestPrimary ||
                (primary == bestPrimary && secondary < bestSecondary) ||
                (primary == bestPrimary && secondary == bestSecondary && tertiary < bestTertiary) ||
                (primary == bestPrimary && secondary == bestSecondary && tertiary == bestTertiary && quaternary < bestQuaternary) ||
                (primary == bestPrimary && secondary == bestSecondary && tertiary == bestTertiary && quaternary == bestQuaternary && quinary < bestQuinary))
            {
                bestIndex = index;
                bestPrimary = primary;
                bestSecondary = secondary;
                bestTertiary = tertiary;
                bestQuaternary = quaternary;
                bestQuinary = quinary;
            }
        }

        if (bestIndex < 0)
        {
            placement = default;
            return false;
        }

        AtlasFreeRectangle selected = freeRectangles[bestIndex];
        placement = new AtlasFreeRectangle(selected.X, selected.Y, width, height);
        SplitRecoveryFreeRectangles(freeRectangles, placement);
        return true;
    }

    private static void SplitRecoveryFreeRectangles(
        List<AtlasFreeRectangle> freeRectangles,
        AtlasFreeRectangle used)
    {
        int originalCount = freeRectangles.Count;
        AtlasFreeRectangle[] generated = ArrayPool<AtlasFreeRectangle>.Shared.Rent(
            Math.Max(4, checked(originalCount * 4)));
        int generatedCount = 0;
        int survivorCount = 0;
        try
        {
            // The incoming list is already containment-pruned. Preserve unaffected
            // rectangles in stable order and collect only the split rectangles that
            // can introduce new containment relationships.
            for (int index = 0; index < originalCount; index++)
            {
                AtlasFreeRectangle free = freeRectangles[index];
                if (used.X >= free.Right || used.Right <= free.X ||
                    used.Y >= free.Bottom || used.Bottom <= free.Y)
                {
                    freeRectangles[survivorCount++] = free;
                    continue;
                }

                if (used.X > free.X)
                {
                    generated[generatedCount++] = new AtlasFreeRectangle(
                        free.X,
                        free.Y,
                        used.X - free.X,
                        free.Height);
                }
                if (used.Right < free.Right)
                {
                    generated[generatedCount++] = new AtlasFreeRectangle(
                        used.Right,
                        free.Y,
                        free.Right - used.Right,
                        free.Height);
                }
                if (used.Y > free.Y)
                {
                    generated[generatedCount++] = new AtlasFreeRectangle(
                        free.X,
                        free.Y,
                        free.Width,
                        used.Y - free.Y);
                }
                if (used.Bottom < free.Bottom)
                {
                    generated[generatedCount++] = new AtlasFreeRectangle(
                        free.X,
                        used.Bottom,
                        free.Width,
                        free.Bottom - used.Bottom);
                }
            }

            if (survivorCount < originalCount)
            {
                freeRectangles.RemoveRange(survivorCount, originalCount - survivorCount);
            }

            for (int generatedIndex = 0; generatedIndex < generatedCount; generatedIndex++)
            {
                AtlasFreeRectangle candidate = generated[generatedIndex];
                bool contained = false;
                for (int existingIndex = freeRectangles.Count - 1; existingIndex >= 0; existingIndex--)
                {
                    AtlasFreeRectangle existing = freeRectangles[existingIndex];
                    if (Contains(existing, candidate))
                    {
                        contained = true;
                        break;
                    }
                    if (Contains(candidate, existing))
                    {
                        freeRectangles.RemoveAt(existingIndex);
                    }
                }

                if (!contained)
                {
                    freeRectangles.Add(candidate);
                }
            }
        }
        finally
        {
            ArrayPool<AtlasFreeRectangle>.Shared.Return(generated);
        }
    }

    private static bool Contains(AtlasFreeRectangle container, AtlasFreeRectangle candidate) =>
        candidate.X >= container.X &&
        candidate.Y >= container.Y &&
        candidate.Right <= container.Right &&
        candidate.Bottom <= container.Bottom;

    private bool HasPathsUsedInCurrentFrame()
    {
        foreach (var info in _paths.Values)
        {
            if (info.LastUsedFrame == _frameNumber)
            {
                return true;
            }
        }

        return false;
    }

    private void ClearAtlasTexture()
    {
        _atlasTexture.ClearRenderTarget();
    }

    public PathInfo GetOrCreatePath(
        PathGeometry path,
        float scale,
        float subpixelX = 0f,
        float subpixelY = 0f,
        uint sampleGrid = StandardCoverageSampleGrid)
    {
        return GetOrCreatePath(
            path,
            scale,
            subpixelX,
            subpixelY,
            sampleGrid,
            DefaultSubpixelPhaseGrid,
            quantizeScale: false);
    }

    public PathInfo GetOrCreatePath(
        PathGeometry path,
        float scale,
        float subpixelX,
        float subpixelY,
        uint sampleGrid,
        uint subpixelPhaseGrid,
        bool quantizeScale)
    {
        return GetOrCreatePath(
            path,
            scale,
            scale,
            subpixelX,
            subpixelY,
            sampleGrid,
            subpixelPhaseGrid,
            quantizeScale);
    }

    public PathInfo GetOrCreatePath(
        PathGeometry path,
        float scaleX,
        float scaleY,
        float subpixelX,
        float subpixelY,
        uint sampleGrid = StandardCoverageSampleGrid)
    {
        return GetOrCreatePath(
            path,
            scaleX,
            scaleY,
            subpixelX,
            subpixelY,
            sampleGrid,
            DefaultSubpixelPhaseGrid,
            quantizeScale: false);
    }

    public PathInfo GetOrCreatePath(
        PathGeometry path,
        float scaleX,
        float scaleY,
        float subpixelX,
        float subpixelY,
        uint sampleGrid,
        uint subpixelPhaseGrid,
        bool quantizeScale)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(PathAtlas));

        int contentHash = ComputeHash(path);
        var key = new PathCacheKey(
            contentHash,
            scaleX,
            scaleY,
            subpixelX,
            subpixelY,
            sampleGrid,
            subpixelPhaseGrid,
            quantizeScale);
        scaleX = key.ScaleX;
        scaleY = key.ScaleY;

        if (_paths.TryGetValue(key, out var info))
        {
            info.LastUsedFrame = _frameNumber;
            _paths[key] = info;
            return info;
        }

        if (ProGpuVectorDiagnostics.IsEnabled)
        {
            WriteCacheMissDiagnostic(path, key);
        }

        float unscaledMinX, unscaledMinY, unscaledMaxX, unscaledMaxY;
        int xStart, yStart, width, height;

        if (!TryGetRasterBounds(
                path,
                out unscaledMinX,
                out unscaledMinY,
                out unscaledMaxX,
                out unscaledMaxY))
        {
            info = new PathInfo
            {
                Key = key,
                Geometry = path,
                UnscaledMinX = 0f,
                UnscaledMinY = 0f,
                UnscaledMaxX = 0f,
                UnscaledMaxY = 0f,
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                TexCoordMin = Vector2.Zero,
                TexCoordMax = Vector2.Zero,
                MinX = 0f,
                MinY = 0f,
                LastUsedFrame = _frameNumber
            };
            _paths[key] = info;
            return info;
        }

        float minX = unscaledMinX * scaleX;
        float minY = unscaledMinY * scaleY;
        float maxX = unscaledMaxX * scaleX;
        float maxY = unscaledMaxY * scaleY;

        int padding = 4;
        xStart = (int)Math.Floor(minX) - padding;
        int xEnd = (int)Math.Ceiling(maxX) + padding;
        yStart = (int)Math.Floor(minY) - padding;
        int yEnd = (int)Math.Ceiling(maxY) + padding;

        width = xEnd - xStart;
        height = yEnd - yStart;

        if (width <= 0 || height <= 0)
        {
            info = new PathInfo
            {
                Key = key,
                Geometry = path,
                UnscaledMinX = unscaledMinX,
                UnscaledMinY = unscaledMinY,
                UnscaledMaxX = unscaledMaxX,
                UnscaledMaxY = unscaledMaxY,
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                TexCoordMin = Vector2.Zero,
                TexCoordMax = Vector2.Zero,
                MinX = 0f,
                MinY = 0f,
                LastUsedFrame = _frameNumber
            };
            _paths[key] = info;
            return info;
        }

        uint gW = (uint)width;
        uint gH = (uint)height;
        PeakRasterWidth = Math.Max(PeakRasterWidth, gW);
        PeakRasterHeight = Math.Max(PeakRasterHeight, gH);

        while ((gW + 4 > _atlasWidth || gH + 4 > _atlasHeight) &&
               TryGrowAtlas(gW, gH))
        {
        }

        if (gW + 4 > _atlasWidth || gH + 4 > _atlasHeight)
        {
            PathFigure? firstFigure = path.Figures.Count > 0 ? path.Figures[0] : null;
            ProGpuVectorDiagnostics.WriteLine(
                $"[PathAtlas] Warning: Path raster {gW}x{gH} cannot fit in the {_atlasWidth}x{_atlasHeight} atlas " +
                $"(combined={path.IsCombined}, figures={path.Figures.Count}, firstClosed={firstFigure?.IsClosed}, " +
                $"firstFilled={firstFigure?.IsFilled}, firstSegments={firstFigure?.Segments.Count}).");
            CapacityExceeded = true;
            info = new PathInfo
            {
                Key = key,
                Geometry = path,
                UnscaledMinX = unscaledMinX,
                UnscaledMinY = unscaledMinY,
                UnscaledMaxX = unscaledMaxX,
                UnscaledMaxY = unscaledMaxY,
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                TexCoordMin = Vector2.Zero,
                TexCoordMax = Vector2.Zero,
                MinX = 0f,
                MinY = 0f,
                LastUsedFrame = _frameNumber,
                RetryXStart = xStart,
                RetryYStart = yStart,
                RetryWidth = gW,
                RetryHeight = gH
            };
            _paths[key] = info;
            return info;
        }

        if (_recoveryFreeRectangles != null)
        {
            info = new PathInfo
            {
                Key = key,
                Geometry = path,
                UnscaledMinX = unscaledMinX,
                UnscaledMinY = unscaledMinY,
                UnscaledMaxX = unscaledMaxX,
                UnscaledMaxY = unscaledMaxY,
                LastUsedFrame = _frameNumber,
                RetryXStart = xStart,
                RetryYStart = yStart,
                RetryWidth = gW,
                RetryHeight = gH
            };
            if (CapacityExceeded)
            {
                // The current compilation transaction is already guaranteed to
                // abort. Preserve the rest of its live set for the authoritative
                // retry without packing, allocating recovery scratch, or queuing
                // raster work that cannot be submitted.
                _paths[key] = info;
                return info;
            }

            if (!TryPlaceRecoveryRectangle(
                    _recoveryFreeRectangles,
                    checked(gW + 2),
                    checked(gH + 2),
                    out AtlasFreeRectangle placement))
            {
                if (CanCompactCurrentFrameWithoutGrowth(info))
                {
                    CapacityExceeded = true;
                    AtlasAvoidedGrowthCount++;
                    _paths[key] = info;
                    ProGpuVectorDiagnostics.WriteLine(
                        $"[PathAtlas] Current {_atlasWidth}x{_atlasHeight} atlas can pack the live frame; " +
                        "retrying without retaining stale paths or growing texture residency.");
                    return info;
                }

                if (TryGrowAtlas(gW, gH))
                {
                    return GetOrCreatePath(
                        path,
                        scaleX,
                        scaleY,
                        subpixelX,
                        subpixelY,
                        sampleGrid,
                        subpixelPhaseGrid,
                        quantizeScale);
                }

                ProGpuVectorDiagnostics.WriteLine(
                    "[PathAtlas] Warning: The recovery-packed atlas cannot fit a new current-frame path; preserving existing path coordinates.");
                CapacityExceeded = true;
                _paths[key] = info;
                return info;
            }

            info = CreatePlacedPathInfo(
                info,
                xStart,
                yStart,
                gW,
                gH,
                placement.X,
                placement.Y);
            _paths[key] = info;
            _pendingPaths.Add(info);
            return info;
        }

        if (_currentX + gW + 2 > _atlasWidth)
        {
            _currentX = 2;
            _currentY += _currentRowHeight + 2;
            _currentRowHeight = 0;
        }

        if (_currentY + gH + 2 > _atlasHeight)
        {
            if (TryGrowAtlas(gW, gH))
            {
                return GetOrCreatePath(
                    path,
                    scaleX,
                    scaleY,
                    subpixelX,
                    subpixelY,
                    sampleGrid,
                    subpixelPhaseGrid,
                    quantizeScale);
            }

            if (!HasPathsUsedInCurrentFrame())
            {
                ProGpuVectorDiagnostics.WriteLine("[PathAtlas] Texture Atlas is full! Repacking cached paths before frame compilation...");
                RepackActivePaths();
            }

            if (_currentX + gW + 2 > _atlasWidth)
            {
                _currentX = 2;
                _currentY += _currentRowHeight + 2;
                _currentRowHeight = 0;
            }

            if (_currentY + gH + 2 > _atlasHeight)
            {
                ProGpuVectorDiagnostics.WriteLine("[PathAtlas] Warning: The current frame exceeds the atlas size; preserving existing path coordinates.");
                CapacityExceeded = true;
                info = new PathInfo
                {
                    Key = key,
                    Geometry = path,
                    UnscaledMinX = unscaledMinX,
                    UnscaledMinY = unscaledMinY,
                    UnscaledMaxX = unscaledMaxX,
                    UnscaledMaxY = unscaledMaxY,
                    X = 0,
                    Y = 0,
                    Width = 0,
                    Height = 0,
                    TexCoordMin = Vector2.Zero,
                    TexCoordMax = Vector2.Zero,
                    MinX = 0f,
                    MinY = 0f,
                    LastUsedFrame = _frameNumber,
                    RetryXStart = xStart,
                    RetryYStart = yStart,
                    RetryWidth = gW,
                    RetryHeight = gH
                };
                _paths[key] = info;
                return info;
            }
        }

        uint posX = _currentX;
        uint posY = _currentY;

        _currentX += gW + 2;
        _currentRowHeight = Math.Max(_currentRowHeight, gH);

        float texelSizeX = 1.0f / _atlasWidth;
        float texelSizeY = 1.0f / _atlasHeight;
        info = new PathInfo
        {
            Key = key,
            Geometry = path,
            UnscaledMinX = unscaledMinX,
            UnscaledMinY = unscaledMinY,
            UnscaledMaxX = unscaledMaxX,
            UnscaledMaxY = unscaledMaxY,
            X = posX,
            Y = posY,
            Width = gW,
            Height = gH,
            TexCoordMin = new Vector2(
                (posX + key.SubpixelX) * texelSizeX,
                (posY + key.SubpixelY) * texelSizeY),
            TexCoordMax = new Vector2(
                (posX + gW + key.SubpixelX) * texelSizeX,
                (posY + gH + key.SubpixelY) * texelSizeY),
            MinX = xStart,
            MinY = yStart,
            LastUsedFrame = _frameNumber
        };

        _paths[key] = info;
        _pendingPaths.Add(info);

        return info;
    }

    private bool CanCompactCurrentFrameWithoutGrowth(PathInfo candidate)
    {
        uint availableWidth = _atlasWidth > 2 ? _atlasWidth - 2 : 0;
        uint availableHeight = _atlasHeight > 2 ? _atlasHeight - 2 : 0;
        if (candidate.RetryWidth + 2 > availableWidth ||
            candidate.RetryHeight + 2 > availableHeight)
        {
            return false;
        }

        ulong paddedArea =
            (ulong)(candidate.RetryWidth + 2) *
            (candidate.RetryHeight + 2);
        foreach (PathInfo info in _paths.Values)
        {
            if (info.LastUsedFrame != _frameNumber)
            {
                continue;
            }

            uint width = info.Width > 0 ? info.Width : info.RetryWidth;
            uint height = info.Height > 0 ? info.Height : info.RetryHeight;
            if (width + 2 > availableWidth || height + 2 > availableHeight)
            {
                return false;
            }

            paddedArea += (ulong)(width + 2) * (height + 2);
        }

        // This allocation-free conservative probe runs only after the recovery
        // free-space map is exhausted. At no more than one-half occupancy, the
        // miss is dominated by stale phase variants or free-space fragmentation,
        // so abort CPU compilation and let the compositor's one same-frame retry
        // perform the authoritative bounded pack. The generous headroom avoids
        // speculative retries for geometrically difficult live sets. Time is
        // O(C) for C cached paths, space is O(1), and steady insertion stays O(1).
        ulong availableArea = (ulong)availableWidth * availableHeight;
        return paddedArea <= availableArea / 2;
    }

    private static void WriteCacheMissDiagnostic(
        PathGeometry path,
        PathCacheKey key)
    {
        var description = new System.Text.StringBuilder(256);
        description.Append("[PathAtlas] cache-miss hash=");
        description.Append(key.ContentHash);
        description.Append(" scale=");
        description.Append(key.ScaleX.ToString(CultureInfo.InvariantCulture));
        description.Append('x');
        description.Append(key.ScaleY.ToString(CultureInfo.InvariantCulture));
        if (path.TryGetBounds(out Vector2 minimum, out Vector2 maximum))
        {
            description.Append(" bounds=");
            description.Append(minimum.X.ToString(CultureInfo.InvariantCulture));
            description.Append(',');
            description.Append(minimum.Y.ToString(CultureInfo.InvariantCulture));
            description.Append('-');
            description.Append(maximum.X.ToString(CultureInfo.InvariantCulture));
            description.Append(',');
            description.Append(maximum.Y.ToString(CultureInfo.InvariantCulture));
        }

        description.Append(" geometry=");
        description.Append(path.IsCombined ? "combined" : "path");
        description.Append('[');
        for (int figureIndex = 0;
             figureIndex < path.Figures.Count;
             figureIndex++)
        {
            if (figureIndex > 0)
            {
                description.Append(';');
            }

            PathFigure figure = path.Figures[figureIndex];
            description.Append(figure.IsClosed ? "closed:" : "open:");
            description.Append(
                figure.StartPoint.X.ToString(CultureInfo.InvariantCulture));
            description.Append(',');
            description.Append(
                figure.StartPoint.Y.ToString(CultureInfo.InvariantCulture));
            description.Append(':');
            for (int segmentIndex = 0;
                 segmentIndex < figure.Segments.Count;
                 segmentIndex++)
            {
                if (segmentIndex > 0)
                {
                    description.Append(',');
                }

                PathSegment segment = figure.Segments[segmentIndex];
                description.Append(segment.GetType().Name);
                Vector2? endPoint = segment switch
                {
                    LineSegment line => line.Point,
                    QuadraticBezierSegment quadratic =>
                        quadratic.Point,
                    CubicBezierSegment cubic => cubic.Point,
                    ArcSegment arc => arc.Point,
                    _ => null
                };
                if (endPoint is Vector2 point)
                {
                    description.Append('(');
                    description.Append(
                        point.X.ToString(CultureInfo.InvariantCulture));
                    description.Append(',');
                    description.Append(
                        point.Y.ToString(CultureInfo.InvariantCulture));
                    description.Append(')');
                }
            }
        }

        description.Append(']');
        ProGpuVectorDiagnostics.WriteLine(description.ToString());
    }

    private bool TryGrowAtlas(uint requiredWidth, uint requiredHeight)
    {
        if (_atlasWidth >= _maxAtlasSize && _atlasHeight >= _maxAtlasSize)
        {
            return false;
        }

        uint requiredAtlasWidth = checked(requiredWidth + 4);
        uint requiredAtlasHeight = checked(requiredHeight + 4);
        uint oldWidth = _atlasWidth;
        uint oldHeight = _atlasHeight;
        uint newWidth = GrowAtlasDimension(
            oldWidth,
            requiredAtlasWidth,
            _maxAtlasSize);
        uint newHeight = GrowAtlasDimension(
            oldHeight,
            requiredAtlasHeight,
            _maxAtlasSize);
        bool requiredWidthGrowth = requiredAtlasWidth > oldWidth;
        bool requiredHeightGrowth = requiredAtlasHeight > oldHeight;

        // Calls made after the requested raster already fits mean the shelf packer
        // exhausted its current rows. Alternate the shorter axis so a dense workload
        // stays at most 2:1 instead of growing a tall, sparsely useful texture. Width
        // growth exposes a right-hand strip and preserves every resident texel.
        bool enterRightHandStrip = false;
        if (newWidth == oldWidth && newHeight == oldHeight)
        {
            if (oldHeight < _maxAtlasSize &&
                (oldHeight <= oldWidth || oldWidth >= _maxAtlasSize))
            {
                newHeight = Math.Min(_maxAtlasSize, checked(oldHeight * 2));
            }
            else if (oldWidth < _maxAtlasSize)
            {
                newWidth = Math.Min(_maxAtlasSize, checked(oldWidth * 2));
                enterRightHandStrip = true;
            }
            else if (oldHeight < _maxAtlasSize)
            {
                newHeight = Math.Min(_maxAtlasSize, checked(oldHeight * 2));
            }
        }

        if (newWidth < requiredAtlasWidth ||
            newHeight < requiredAtlasHeight ||
            (newWidth == oldWidth && newHeight == oldHeight))
        {
            return false;
        }

        var newTexture = new GpuTexture(
            _context,
            newWidth,
            newHeight,
            TextureFormat.R8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc |
            TextureUsage.RenderAttachment,
            "Dynamic Path Coverage Atlas");
        newTexture.ClearRenderTarget();
        newTexture.CopyBaseLevelRegionFrom(_atlasTexture, oldWidth, oldHeight);
        GpuTexture oldTexture = _atlasTexture;
        _atlasTexture = newTexture;
        _atlasWidth = newWidth;
        _atlasHeight = newHeight;
        oldTexture.Dispose();

        if (_recoveryFreeRectangles != null)
        {
            if (newWidth > oldWidth)
            {
                _recoveryFreeRectangles.Add(new AtlasFreeRectangle(
                    oldWidth,
                    2,
                    newWidth - oldWidth,
                    oldHeight - 2));
            }
            if (newHeight > oldHeight)
            {
                _recoveryFreeRectangles.Add(new AtlasFreeRectangle(
                    2,
                    oldHeight,
                    newWidth - 2,
                    newHeight - oldHeight));
            }
        }
        else if (enterRightHandStrip &&
                 !requiredWidthGrowth &&
                 !requiredHeightGrowth)
        {
            _currentX = oldWidth;
            _currentY = 2;
            _currentRowHeight = 0;
        }

        RefreshNormalizedTextureCoordinates();
        TextureRevision++;
        Generation++;
        AtlasGrowthCount++;
        _framesSinceAtlasResize = 0;
        CapacityExceeded = false;
        return true;
    }

    private static uint GrowAtlasDimension(
        uint current,
        uint required,
        uint maximum)
    {
        uint grown = current;
        while (grown < required && grown < maximum)
        {
            grown = Math.Min(maximum, checked(grown * 2));
        }
        return grown;
    }

    private void RefreshNormalizedTextureCoordinates()
    {
        float texelSizeX = 1f / _atlasWidth;
        float texelSizeY = 1f / _atlasHeight;
        foreach (KeyValuePair<PathCacheKey, PathInfo> pair in _paths)
        {
            ref PathInfo info = ref CollectionsMarshal.GetValueRefOrNullRef(
                _paths,
                pair.Key);
            if (info.Width == 0 || info.Height == 0)
            {
                continue;
            }

            info.TexCoordMin = new Vector2(
                (info.X + info.Key.SubpixelX) * texelSizeX,
                (info.Y + info.Key.SubpixelY) * texelSizeY);
            info.TexCoordMax = new Vector2(
                (info.X + info.Width + info.Key.SubpixelX) * texelSizeX,
                (info.Y + info.Height + info.Key.SubpixelY) * texelSizeY);
        }

        for (int pendingIndex = 0; pendingIndex < _pendingPaths.Count; pendingIndex++)
        {
            PathInfo info = _pendingPaths[pendingIndex];
            info.TexCoordMin = new Vector2(
                (info.X + info.Key.SubpixelX) * texelSizeX,
                (info.Y + info.Key.SubpixelY) * texelSizeY);
            info.TexCoordMax = new Vector2(
                (info.X + info.Width + info.Key.SubpixelX) * texelSizeX,
                (info.Y + info.Height + info.Key.SubpixelY) * texelSizeY);
            _pendingPaths[pendingIndex] = info;
        }
    }

    public void RasterizePendingPaths()
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(PathAtlas));
        LastRasterStagingBytes = 0;
        LastDirectBooleanRasterizationCount = 0;
        if (_pendingPaths.Count == 0) return;

        PendingRasterization[]? rasterizations = null;
        RasterizationDispatch[]? dispatches = null;
        GpuPathRecord[]? recordData = null;
        GpuPathSegment[]? segmentData = null;
        byte[]? uniformData = null;
        nint[]? bindGroupsToRelease = null;
        int bindGroupToReleaseCount = 0;
        int rasterizationCount = 0;
        int dispatchCount = 0;
        int totalRecordCount = 0;
        int totalSegmentCount = 0;
        bool diagnosticsEnabled = ProGpuVectorDiagnostics.IsEnabled;
        ulong totalRasterPixels = 0;
        uint maxRasterWidth = 0;
        uint maxRasterHeight = 0;

        try
        {
            rasterizations = ArrayPool<PendingRasterization>.Shared.Rent(_pendingPaths.Count);
            for (int i = 0; i < _pendingPaths.Count; i++)
            {
                var info = _pendingPaths[i];
                if (info.Width == 0 || info.Height == 0)
                {
                    continue;
                }

                if (!TryGetRasterizationData(
                        info.Geometry,
                        out var recordsA,
                        out var segmentsA,
                        out var recordsB,
                        out var segmentsB,
                        out var pathOpKind))
                {
                    continue;
                }

                rasterizations[rasterizationCount++] = new PendingRasterization(
                    info,
                    recordsA,
                    segmentsA,
                    recordsB,
                    segmentsB,
                    totalRecordCount,
                    totalSegmentCount,
                    recordsB.Length == 0
                        ? totalRecordCount
                        : checked(totalRecordCount + recordsA.Length),
                    checked(totalSegmentCount + segmentsA.Length),
                    pathOpKind,
                    0,
                    0);
                if (pathOpKind != 0)
                {
                    LastDirectBooleanRasterizationCount++;
                }
                totalRecordCount = checked(
                    totalRecordCount + recordsA.Length + recordsB.Length);
                totalSegmentCount = checked(
                    totalSegmentCount + segmentsA.Length + segmentsB.Length);
                if (diagnosticsEnabled)
                {
                    totalRasterPixels += (ulong)info.Width * info.Height;
                    maxRasterWidth = Math.Max(maxRasterWidth, info.Width);
                    maxRasterHeight = Math.Max(maxRasterHeight, info.Height);
                }
            }

            if (rasterizationCount == 0)
            {
                _pendingPaths.Clear();
                return;
            }

            Array.Sort(
                rasterizations,
                0,
                rasterizationCount,
                PendingRasterizationComparer.Instance);

            totalRecordCount = 0;
            totalSegmentCount = 0;
            for (int i = 0; i < rasterizationCount; i++)
            {
                var rasterization = rasterizations[i];
                uint outputBytesPerRow = GpuCoverageUpload.GetBytesPerRow(
                    rasterization.Info.Width);
                rasterizations[i] = rasterization with
                {
                    RecordOffsetA = totalRecordCount,
                    SegmentOffsetA = totalSegmentCount,
                    RecordOffsetB = rasterization.RecordsB.Length == 0
                        ? totalRecordCount
                        : checked(totalRecordCount + rasterization.RecordsA.Length),
                    SegmentOffsetB = checked(totalSegmentCount + rasterization.SegmentsA.Length),
                    OutputBytesPerRow = outputBytesPerRow
                };
                totalRecordCount = checked(
                    totalRecordCount +
                    rasterization.RecordsA.Length +
                    rasterization.RecordsB.Length);
                totalSegmentCount = checked(
                    totalSegmentCount +
                    rasterization.SegmentsA.Length +
                    rasterization.SegmentsB.Length);
            }

            int uniformSize = Marshal.SizeOf<PathUniforms>();
            dispatches = ArrayPool<RasterizationDispatch>.Shared.Rent(rasterizationCount);
            int totalUniformBytes = 0;
            int maxCoverageBytes = 0;
            int groupStart = 0;
            while (groupStart < rasterizationCount)
            {
                var groupInfo = rasterizations[groupStart].Info;
                uint workgroupsX = DivRoundUp(DivRoundUp(groupInfo.Width, 4), 16);
                uint workgroupsY = DivRoundUp(groupInfo.Height, 16);
                int groupCoverageBytes = 0;
                int groupEnd = groupStart;
                while (groupEnd < rasterizationCount)
                {
                    var candidate = rasterizations[groupEnd].Info;
                    if (DivRoundUp(DivRoundUp(candidate.Width, 4), 16) != workgroupsX ||
                        DivRoundUp(candidate.Height, 16) != workgroupsY)
                    {
                        break;
                    }

                    PendingRasterization candidateRasterization = rasterizations[groupEnd];
                    int outputByteOffset = AlignUp(
                        groupCoverageBytes,
                        (int)GpuCoverageUpload.CopyRowAlignment);
                    int candidateEnd = checked(
                        outputByteOffset +
                        checked((int)(candidateRasterization.OutputBytesPerRow * candidate.Height)));
                    if (groupEnd > groupStart && candidateEnd > DefaultRasterStagingChunkBytes)
                    {
                        break;
                    }

                    rasterizations[groupEnd] = candidateRasterization with
                    {
                        OutputByteOffset = outputByteOffset
                    };
                    groupCoverageBytes = candidateEnd;
                    groupEnd++;
                }

                int uniformByteOffset = AlignUp(
                    totalUniformBytes,
                    RasterizationStorageOffsetAlignment);
                int uniformByteSize = checked((groupEnd - groupStart) * uniformSize);
                dispatches[dispatchCount++] = new RasterizationDispatch(
                    groupStart,
                    groupEnd - groupStart,
                    workgroupsX,
                    workgroupsY,
                    uniformByteOffset,
                    uniformByteSize);
                totalUniformBytes = checked(uniformByteOffset + uniformByteSize);
                maxCoverageBytes = Math.Max(maxCoverageBytes, groupCoverageBytes);
                groupStart = groupEnd;
            }
            LastRasterStagingBytes = checked((uint)maxCoverageBytes);
            PeakRasterStagingBytes = Math.Max(PeakRasterStagingBytes, LastRasterStagingBytes);

            recordData = ArrayPool<GpuPathRecord>.Shared.Rent(totalRecordCount);
            segmentData = ArrayPool<GpuPathSegment>.Shared.Rent(totalSegmentCount);
            uniformData = ArrayPool<byte>.Shared.Rent(totalUniformBytes);
            var uniformSpan = uniformData.AsSpan(0, totalUniformBytes);

            for (int i = 0; i < rasterizationCount; i++)
            {
                var rasterization = rasterizations[i];
                rasterization.SegmentsA.AsSpan().CopyTo(
                    segmentData.AsSpan(
                        rasterization.SegmentOffsetA,
                        rasterization.SegmentsA.Length));
                rasterization.SegmentsB.AsSpan().CopyTo(
                    segmentData.AsSpan(
                        rasterization.SegmentOffsetB,
                        rasterization.SegmentsB.Length));

                for (int recordIndex = 0; recordIndex < rasterization.RecordsA.Length; recordIndex++)
                {
                    var record = rasterization.RecordsA[recordIndex];
                    record.StartSegment = checked(
                        record.StartSegment + (uint)rasterization.SegmentOffsetA);
                    recordData[rasterization.RecordOffsetA + recordIndex] = record;
                }

                for (int recordIndex = 0; recordIndex < rasterization.RecordsB.Length; recordIndex++)
                {
                    var record = rasterization.RecordsB[recordIndex];
                    record.StartSegment = checked(
                        record.StartSegment + (uint)rasterization.SegmentOffsetB);
                    recordData[rasterization.RecordOffsetB + recordIndex] = record;
                }
            }

            for (int dispatchIndex = 0; dispatchIndex < dispatchCount; dispatchIndex++)
            {
                var dispatch = dispatches[dispatchIndex];
                for (int localIndex = 0; localIndex < dispatch.Count; localIndex++)
                {
                    var rasterization = rasterizations[dispatch.StartIndex + localIndex];
                    var info = rasterization.Info;
                    const int padding = 4;
                    float scaleX = info.Key.ScaleX;
                    float scaleY = info.Key.ScaleY;
                    int xStart = (int)Math.Floor(info.UnscaledMinX * scaleX) - padding;
                    int yStart = (int)Math.Floor(info.UnscaledMinY * scaleY) - padding;
                    var uniforms = new PathUniforms
                    {
                        XStart = xStart - info.Key.SubpixelX,
                        YStart = yStart - info.Key.SubpixelY,
                        ScaleX = scaleX,
                        ScaleY = scaleY,
                        PathIndex = checked((uint)rasterization.RecordOffsetA),
                        OutputOffsetWords = checked((uint)rasterization.OutputByteOffset / 4),
                        OutputRowWords = rasterization.OutputBytesPerRow / 4,
                        Width = info.Width,
                        Height = info.Height,
                        SampleGrid = info.Key.SampleGrid,
                        PathIndexB = checked((uint)rasterization.RecordOffsetB),
                        PathOpKind = rasterization.PathOpKind,
                    };
                    MemoryMarshal.Write(
                        uniformSpan.Slice(
                            dispatch.UniformByteOffset + localIndex * uniformSize,
                            uniformSize),
                        in uniforms);
                }
            }

            var uniformBuffer = new GpuBuffer(
                _context,
                checked((uint)totalUniformBytes),
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Path Rasterization Uniforms");
            uniformBuffer.WriteBytes(uniformSpan);
            _tempBuffers.Add(uniformBuffer);
            var recordsBuffer = new GpuBuffer(
                _context,
                checked((uint)(totalRecordCount * Marshal.SizeOf<GpuPathRecord>())),
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Path Rasterization Records");
            recordsBuffer.Write(recordData.AsSpan(0, totalRecordCount));
            _tempBuffers.Add(recordsBuffer);
            var segmentsBuffer = new GpuBuffer(
                _context,
                checked((uint)(totalSegmentCount * Marshal.SizeOf<GpuPathSegment>())),
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Path Rasterization Segments");
            segmentsBuffer.Write(segmentData.AsSpan(0, totalSegmentCount));
            _tempBuffers.Add(segmentsBuffer);
            var coverageBuffer = new GpuBuffer(
                _context,
                checked((uint)maxCoverageBytes),
                BufferUsage.Storage | BufferUsage.CopySrc,
                "Path Coverage Staging Buffer");
            _tempBuffers.Add(coverageBuffer);

            var bindGroupEntries = stackalloc BindGroupEntry[4];
            bindGroupEntries[1] = new BindGroupEntry
            {
                Binding = 1,
                Buffer = recordsBuffer.BufferPtr,
                Offset = 0,
                Size = recordsBuffer.Size
            };
            bindGroupEntries[2] = new BindGroupEntry
            {
                Binding = 2,
                Buffer = segmentsBuffer.BufferPtr,
                Offset = 0,
                Size = segmentsBuffer.Size
            };
            bindGroupEntries[3] = new BindGroupEntry
            {
                Binding = 3,
                Buffer = coverageBuffer.BufferPtr,
                Offset = 0,
                Size = coverageBuffer.Size
            };
            var encoderDescriptor = new CommandEncoderDescriptor
            {
                Label = (byte*)SilkMarshal.StringToPtr("Path Batch Rasterizer Encoder")
            };
            var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
            SilkMarshal.Free((nint)encoderDescriptor.Label);
            for (int dispatchIndex = 0; dispatchIndex < dispatchCount; dispatchIndex++)
            {
                var dispatch = dispatches[dispatchIndex];
                bindGroupEntries[0] = new BindGroupEntry
                {
                    Binding = 0,
                    Buffer = uniformBuffer.BufferPtr,
                    Offset = checked((ulong)dispatch.UniformByteOffset),
                    Size = checked((ulong)dispatch.UniformByteSize)
                };
                var bindGroupDescriptor = new BindGroupDescriptor
                {
                    Layout = _computeBindGroupLayout,
                    EntryCount = 4,
                    Entries = bindGroupEntries
                };
                var bindGroup = _context.Api.DeviceCreateBindGroup(
                    _context.Device,
                    &bindGroupDescriptor);
                if (bindGroup == null)
                {
                    throw new InvalidOperationException("Failed to create the path rasterization bind group.");
                }

                PooledRemovalBuffer.Add(
                    ref bindGroupsToRelease,
                    ref bindGroupToReleaseCount,
                    dispatchCount,
                    (nint)bindGroup);
                var passDescriptor = new ComputePassDescriptor();
                var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
                _context.Api.ComputePassEncoderSetPipeline(pass, _computePipeline);
                _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
                _context.Api.ComputePassEncoderDispatchWorkgroups(
                    pass,
                    dispatch.WorkgroupsX,
                    dispatch.WorkgroupsY,
                    checked((uint)dispatch.Count));
                _context.Api.ComputePassEncoderEnd(pass);
                _context.Api.ComputePassEncoderRelease(pass);

                for (int localIndex = 0; localIndex < dispatch.Count; localIndex++)
                {
                    var rasterization = rasterizations[dispatch.StartIndex + localIndex];
                    var info = rasterization.Info;
                    GpuCoverageUpload.RecordCopy(
                        _context,
                        encoder,
                        coverageBuffer,
                        checked((uint)rasterization.OutputByteOffset),
                        rasterization.OutputBytesPerRow,
                        _atlasTexture,
                        info.X,
                        info.Y,
                        info.Width,
                        info.Height);
                }
            }

            var commandBufferDescriptor = new CommandBufferDescriptor
            {
                Label = (byte*)SilkMarshal.StringToPtr("Path Batch Rasterizer Command Buffer")
            };
            var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandBufferDescriptor);
            SilkMarshal.Free((nint)commandBufferDescriptor.Label);
            _context.Submit(1, &commandBuffer);

            _context.Api.CommandBufferRelease(commandBuffer);
            _context.Api.CommandEncoderRelease(encoder);
            _pendingPaths.Clear();

            if (diagnosticsEnabled)
            {
                ProGpuVectorDiagnostics.WriteLine(
                    $"[PathAtlas] Rasterized {rasterizationCount} paths ({totalRasterPixels} pixels, " +
                    $"max {maxRasterWidth}x{maxRasterHeight}) in {dispatchCount} dispatches " +
                    "from 3 shared buffer uploads.");
                if (_frameNumber <= 2)
                {
                    ProGpuVectorDiagnostics.WriteLine(
                        $"[PathAtlas] Current frame raster rectangles: " +
                        $"{DescribeCurrentFrameRasterRectangles()}.");
                }
            }
        }
        finally
        {
            for (int i = 0; i < bindGroupToReleaseCount; i++)
            {
                _context.Api.BindGroupRelease((BindGroup*)bindGroupsToRelease![i]);
            }
            PooledRemovalBuffer.Return(bindGroupsToRelease, bindGroupToReleaseCount);

            if (rasterizations != null)
            {
                ArrayPool<PendingRasterization>.Shared.Return(rasterizations, clearArray: true);
            }

            if (dispatches != null)
            {
                ArrayPool<RasterizationDispatch>.Shared.Return(dispatches);
            }

            if (recordData != null)
            {
                ArrayPool<GpuPathRecord>.Shared.Return(recordData);
            }

            if (segmentData != null)
            {
                ArrayPool<GpuPathSegment>.Shared.Return(segmentData);
            }

            if (uniformData != null)
            {
                ArrayPool<byte>.Shared.Return(uniformData);
            }
        }
    }

    public void CleanupFrame(uint anticipatedWidth = 0, uint anticipatedHeight = 0)
    {
        _ = anticipatedWidth;
        _ = anticipatedHeight;
        TryResetAfterCapacityExceeded();
        if (_framesSinceAtlasResize < DefaultAtlasShrinkDelayFrames)
        {
            _framesSinceAtlasResize++;
        }
        TryShrinkAtlas();
        _retainedPathReplayObserved = false;
        _frameNumber++;
        for (int i = 0; i < _tempBuffers.Count; i++)
        {
            _tempBuffers[i].Dispose();
        }
        _tempBuffers.Clear();
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        CleanupFrame();
        _pipelineCache.Dispose();
        _computePipelineLayoutLease.Dispose();
        _computeBindGroupLayoutLease.Dispose();
        _atlasTexture.Dispose();
        _paths.Clear();
        _compiledFillPaths.Clear();
        _compiledHitTestPaths.Clear();
        _compiledPathCacheLru.Clear();
        _compiledPathCacheBytes = 0;
        _pendingPaths.Clear();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
