using System;
using System.Buffers;
using System.Collections.Generic;
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
    public uint AtlasX;
    public uint AtlasY;
    public uint Width;
    public uint Height;
    public uint SampleGrid;
    public uint Pad1;
    public uint Pad2;
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

    public const long DefaultCompiledPathCacheBudgetBytes = 8L * 1024L * 1024L;

    private const int MaxCompiledPathCount = 4096;
    private const int RasterizationStorageOffsetAlignment = 256;
    private const int ExactRecoveryPathLimit = 10;
    private const int ExactRecoveryNodeBudget = 25_000;
    private const int ExactRecoveryCandidateBudget = 250_000;

    private readonly WgpuContext _context;
    private readonly GpuTexture _atlasTexture;
    private readonly uint _atlasSize;
    private readonly long _compiledPathCacheBudgetBytes;

    private uint _currentX = 2;
    private uint _currentY = 2;
    private uint _currentRowHeight = 0;
    private uint _frameNumber = 0;
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
    private readonly BindGroupLayout* _computeBindGroupLayout;
    private readonly PipelineLayout* _computePipelineLayout;
    private readonly ComputePipeline* _computePipeline;
    private bool _isDisposed;

    public GpuTexture AtlasTexture => _atlasTexture;
    public uint AtlasSize => _atlasSize;
    public int CachedPathCount => _paths.Count;
    public int CachedFillPathCount => _compiledFillPaths.Count;
    public int CachedHitTestPathCount => _compiledHitTestPaths.Count;
    public long CompiledPathCacheBytes => _compiledPathCacheBytes;
    public long CompiledPathCacheBudgetBytes => _compiledPathCacheBudgetBytes;
    public ulong Generation { get; private set; }
    public bool CapacityExceeded { get; private set; }
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

        _context = context;
        _atlasSize = atlasSize;
        _compiledPathCacheBudgetBytes = compiledPathCacheBudgetBytes;

        _atlasTexture = new GpuTexture(
            _context,
            _atlasSize,
            _atlasSize,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc |
            TextureUsage.StorageBinding | TextureUsage.RenderAttachment,
            "Dynamic Path Atlas"
        );

        ClearAtlasTexture();

        _pipelineCache = new RenderPipelineCache(_context);
        _computeBindGroupLayout = CreateRasterizationBindGroupLayout();
        _computePipelineLayout = CreateRasterizationPipelineLayout(_computeBindGroupLayout);
        var shaderModule = _pipelineCache.GetOrCreateShader("PathRasterizer", Shaders.PathRasterizerShader, "PathRasterizerShader");
        _computePipeline = _pipelineCache.GetOrCreateComputePipeline(
            "PathRasterizer",
            shaderModule,
            "cs_main",
            _computePipelineLayout);
    }

    private BindGroupLayout* CreateRasterizationBindGroupLayout()
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
            StorageTexture = new StorageTextureBindingLayout
            {
                Access = StorageTextureAccess.WriteOnly,
                Format = TextureFormat.Rgba8Unorm,
                ViewDimension = TextureViewDimension.Dimension2D
            }
        };

        var descriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = 4,
            Entries = entries
        };
        var layout = _context.Api.DeviceCreateBindGroupLayout(_context.Device, &descriptor);
        if (layout == null)
        {
            throw new InvalidOperationException("Failed to create the path rasterization bind group layout.");
        }

        return layout;
    }

    private PipelineLayout* CreateRasterizationPipelineLayout(BindGroupLayout* bindGroupLayout)
    {
        var layouts = stackalloc BindGroupLayout*[1];
        layouts[0] = bindGroupLayout;
        var descriptor = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 1,
            BindGroupLayouts = layouts
        };
        var layout = _context.Api.DeviceCreatePipelineLayout(_context.Device, &descriptor);
        if (layout == null)
        {
            throw new InvalidOperationException("Failed to create the path rasterization pipeline layout.");
        }

        return layout;
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
        GpuPathRecord[] Records,
        GpuPathSegment[] Segments,
        int RecordOffset,
        int SegmentOffset);

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
            int xComparison = DivRoundUp(left.Info.Width, 16).CompareTo(
                DivRoundUp(right.Info.Width, 16));
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

                if (_currentX + gW + 2 > _atlasSize)
                {
                    _currentX = 2;
                    _currentY += _currentRowHeight + 2;
                    _currentRowHeight = 0;
                }

                if (_currentY + gH + 2 > _atlasSize)
                {
                    ProGpuVectorDiagnostics.WriteLine("[PathAtlas] Warning: Even active paths in the current frame exceed the atlas size during repack!");
                    break;
                }

                uint posX = _currentX;
                uint posY = _currentY;

                _currentX += gW + 2;
                _currentRowHeight = Math.Max(_currentRowHeight, gH);

                float texelSize = 1.0f / _atlasSize;
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
                        (posX + info.Key.SubpixelX) * texelSize,
                        (posY + info.Key.SubpixelY) * texelSize),
                    TexCoordMax = new Vector2(
                        (posX + gW + info.Key.SubpixelX) * texelSize,
                        (posY + gH + info.Key.SubpixelY) * texelSize),
                    MinX = info.MinX,
                    MinY = info.MinY,
                    LastUsedFrame = info.LastUsedFrame
                };

                _paths[newInfo.Key] = newInfo;
                _pendingPaths.Add(newInfo);
            }
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
        // MaxRects placement heuristics, splitting and pruning F free regions after
        // every placement. Recovery costs O(S * (P log P + P * F^2)) time and
        // O(P + F) space for S=12 strategies. A final exact search for at most ten
        // paths is capped at 25,000 nodes and 250,000 candidate placements, so an
        // adversarial set cannot stall the render thread. Normal insertion remains
        // the allocation-free O(1) shelf path. Recovery may allocate so a live
        // frame is not rejected merely because one command order or heuristic
        // fragmented otherwise usable atlas space.
        List<RetryPath> livePaths = CollectCurrentFramePathsForRetry();
        ResetCachedPaths();

        if (!TryPackRecoveryPaths(
                livePaths,
                out List<RetryPlacement> placements,
                out List<AtlasFreeRectangle> freeRectangles,
                out RetryPathOrdering ordering,
                out RecoveryPlacementHeuristic heuristic))
        {
            for (int diagnosticIndex = 0; diagnosticIndex < livePaths.Count; diagnosticIndex++)
            {
                RetryPath diagnosticPath = livePaths[diagnosticIndex];
                ProGpuVectorDiagnostics.WriteLine(
                    $"[PathAtlas] Retry rectangle {diagnosticIndex}: {diagnosticPath.Width}x{diagnosticPath.Height}.");
            }
            string exactSearchStatus = LastExactRecoveryBudgetExceeded
                ? $"; exact recovery exhausted its deterministic work budget after " +
                    $"{LastExactRecoveryNodeCount} nodes and {LastExactRecoveryCandidateCount} candidates"
                : string.Empty;
            throw new InvalidOperationException(
                $"PathAtlas could not deterministically pack the live path set in the configured " +
                $"{_atlasSize}x{_atlasSize} atlas after multi-strategy retry packing " +
                $"({livePaths.Count} live paths{exactSearchStatus}).");
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
            $"[PathAtlas] Deterministically packed {livePaths.Count} live paths for render retry " +
            $"using {ordering}/{heuristic}, with {freeRectangles.Count} free rectangles remaining.");
    }

    private bool TryPackRecoveryPaths(
        List<RetryPath> livePaths,
        out List<RetryPlacement> placements,
        out List<AtlasFreeRectangle> freeRectangles,
        out RetryPathOrdering successfulOrdering,
        out RecoveryPlacementHeuristic successfulHeuristic)
    {
        LastExactRecoveryNodeCount = 0;
        LastExactRecoveryCandidateCount = 0;
        LastExactRecoveryBudgetExceeded = false;
        uint availableSize = _atlasSize > 2 ? _atlasSize - 2 : 0;
        RetryPathOrdering[] orderings = Enum.GetValues<RetryPathOrdering>();
        RecoveryPlacementHeuristic[] heuristics = Enum.GetValues<RecoveryPlacementHeuristic>();

        for (int orderingIndex = 0; orderingIndex < orderings.Length; orderingIndex++)
        {
            RetryPathOrdering ordering = orderings[orderingIndex];
            var orderedPaths = new List<RetryPath>(livePaths);
            orderedPaths.Sort((left, right) => CompareRetryPaths(left, right, ordering));

            for (int heuristicIndex = 0; heuristicIndex < heuristics.Length - 1; heuristicIndex++)
            {
                RecoveryPlacementHeuristic heuristic = heuristics[heuristicIndex];
                var trialFreeRectangles = new List<AtlasFreeRectangle>(Math.Max(4, livePaths.Count * 2))
                {
                    new AtlasFreeRectangle(2, 2, availableSize, availableSize)
                };
                var trialPlacements = new List<RetryPlacement>(livePaths.Count);
                bool succeeded = true;

                for (int pathIndex = 0; pathIndex < orderedPaths.Count; pathIndex++)
                {
                    RetryPath retryPath = orderedPaths[pathIndex];
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

        if (TryPackRecoveryPathsExactly(livePaths, out placements, out freeRectangles))
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

    private bool TryPackRecoveryPathsExactly(
        List<RetryPath> livePaths,
        out List<RetryPlacement> placements,
        out List<AtlasFreeRectangle> freeRectangles)
    {
        uint availableSize = _atlasSize > 2 ? _atlasSize - 2 : 0;
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
            if (packedWidth > availableSize || packedHeight > availableSize)
            {
                placements = new List<RetryPlacement>();
                freeRectangles = new List<AtlasFreeRectangle>();
                return false;
            }

            packedArea += (ulong)packedWidth * packedHeight;
            orderedPaths.Add(path);
        }

        if (orderedPaths.Count > ExactRecoveryPathLimit ||
            packedArea > (ulong)availableSize * availableSize)
        {
            placements = new List<RetryPlacement>();
            freeRectangles = new List<AtlasFreeRectangle>();
            return false;
        }

        if (ExceedsExactRecoveryIncompatibilityBound(orderedPaths, availableSize, useWidths: true) ||
            ExceedsExactRecoveryIncompatibilityBound(orderedPaths, availableSize, useWidths: false))
        {
            placements = new List<RetryPlacement>();
            freeRectangles = new List<AtlasFreeRectangle>();
            return false;
        }

        orderedPaths.Sort(static (left, right) =>
            CompareRetryPaths(left, right, RetryPathOrdering.AreaDescending));
        uint[] xCoordinates = BuildExactRecoveryCoordinates(orderedPaths, availableSize, useWidth: true);
        uint[] yCoordinates = BuildExactRecoveryCoordinates(orderedPaths, availableSize, useWidth: false);
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
                availableSize,
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
            new AtlasFreeRectangle(2, 2, availableSize, availableSize)
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
        uint extent,
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
                    if (leftDimension + rightDimension <= extent)
                    {
                        pairwiseIncompatible = false;
                        break;
                    }
                }
            }

            if (pairwiseIncompatible && perpendicularExtent > extent)
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
        uint extent,
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
            if (y > extent - height)
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
                if (x > extent - width)
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
                        extent,
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

        if (!TryGetCompiledFillPath(
                info.Geometry,
                out _,
                out GpuPathSegment[] segments,
                out float unscaledMinX,
                out float unscaledMinY,
                out float unscaledMaxX,
                out float unscaledMaxY) ||
            segments.Length == 0)
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
        float texelSize = 1.0f / _atlasSize;
        source.X = atlasX;
        source.Y = atlasY;
        source.Width = width;
        source.Height = height;
        source.TexCoordMin = new Vector2(
            (atlasX + source.Key.SubpixelX) * texelSize,
            (atlasY + source.Key.SubpixelY) * texelSize);
        source.TexCoordMax = new Vector2(
            (atlasX + width + source.Key.SubpixelX) * texelSize,
            (atlasY + height + source.Key.SubpixelY) * texelSize);
        source.MinX = xStart;
        source.MinY = yStart;
        source.LastUsedFrame = _frameNumber;
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
        for (int index = freeRectangles.Count - 1; index >= 0; index--)
        {
            AtlasFreeRectangle free = freeRectangles[index];
            if (used.X >= free.Right || used.Right <= free.X ||
                used.Y >= free.Bottom || used.Bottom <= free.Y)
            {
                continue;
            }

            freeRectangles.RemoveAt(index);
            if (used.X > free.X)
            {
                freeRectangles.Add(new AtlasFreeRectangle(
                    free.X,
                    free.Y,
                    used.X - free.X,
                    free.Height));
            }
            if (used.Right < free.Right)
            {
                freeRectangles.Add(new AtlasFreeRectangle(
                    used.Right,
                    free.Y,
                    free.Right - used.Right,
                    free.Height));
            }
            if (used.Y > free.Y)
            {
                freeRectangles.Add(new AtlasFreeRectangle(
                    free.X,
                    free.Y,
                    free.Width,
                    used.Y - free.Y));
            }
            if (used.Bottom < free.Bottom)
            {
                freeRectangles.Add(new AtlasFreeRectangle(
                    free.X,
                    used.Bottom,
                    free.Width,
                    free.Bottom - used.Bottom));
            }
        }

        for (int outer = freeRectangles.Count - 1; outer >= 0; outer--)
        {
            AtlasFreeRectangle candidate = freeRectangles[outer];
            for (int inner = 0; inner < freeRectangles.Count; inner++)
            {
                if (outer == inner)
                {
                    continue;
                }

                AtlasFreeRectangle container = freeRectangles[inner];
                if (candidate.X >= container.X && candidate.Y >= container.Y &&
                    candidate.Right <= container.Right && candidate.Bottom <= container.Bottom)
                {
                    freeRectangles.RemoveAt(outer);
                    break;
                }
            }
        }
    }

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

        float unscaledMinX, unscaledMinY, unscaledMaxX, unscaledMaxY;
        int xStart, yStart, width, height;

        if (!TryGetCompiledFillPath(
                path,
                out _,
                out var segments,
                out unscaledMinX,
                out unscaledMinY,
                out unscaledMaxX,
                out unscaledMaxY) ||
            segments.Length == 0)
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
                LastUsedFrame = _frameNumber
            };
            if (!TryPlaceRecoveryRectangle(
                    _recoveryFreeRectangles,
                    checked(gW + 2),
                    checked(gH + 2),
                    out AtlasFreeRectangle placement))
            {
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

        if (_currentX + gW + 2 > _atlasSize)
        {
            _currentX = 2;
            _currentY += _currentRowHeight + 2;
            _currentRowHeight = 0;
        }

        if (_currentY + gH + 2 > _atlasSize)
        {
            if (!HasPathsUsedInCurrentFrame())
            {
                ProGpuVectorDiagnostics.WriteLine("[PathAtlas] Texture Atlas is full! Repacking cached paths before frame compilation...");
                RepackActivePaths();
            }

            if (_currentX + gW + 2 > _atlasSize)
            {
                _currentX = 2;
                _currentY += _currentRowHeight + 2;
                _currentRowHeight = 0;
            }

            if (_currentY + gH + 2 > _atlasSize)
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
                    LastUsedFrame = _frameNumber
                };
                _paths[key] = info;
                return info;
            }
        }

        uint posX = _currentX;
        uint posY = _currentY;

        _currentX += gW + 2;
        _currentRowHeight = Math.Max(_currentRowHeight, gH);

        float texelSize = 1.0f / _atlasSize;
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
                (posX + key.SubpixelX) * texelSize,
                (posY + key.SubpixelY) * texelSize),
            TexCoordMax = new Vector2(
                (posX + gW + key.SubpixelX) * texelSize,
                (posY + gH + key.SubpixelY) * texelSize),
            MinX = xStart,
            MinY = yStart,
            LastUsedFrame = _frameNumber
        };

        _paths[key] = info;
        _pendingPaths.Add(info);

        return info;
    }

    public void RasterizePendingPaths()
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(PathAtlas));
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

                if (!TryGetCompiledFillPath(
                        info.Geometry,
                        out var records,
                        out var segments,
                        out _,
                        out _,
                        out _,
                        out _) ||
                    records.Length == 0 ||
                    segments.Length == 0)
                {
                    continue;
                }

                rasterizations[rasterizationCount++] = new PendingRasterization(
                    info,
                    records,
                    segments,
                    totalRecordCount,
                    totalSegmentCount);
                totalRecordCount = checked(totalRecordCount + records.Length);
                totalSegmentCount = checked(totalSegmentCount + segments.Length);
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
                rasterizations[i] = rasterization with
                {
                    RecordOffset = totalRecordCount,
                    SegmentOffset = totalSegmentCount
                };
                totalRecordCount = checked(totalRecordCount + rasterization.Records.Length);
                totalSegmentCount = checked(totalSegmentCount + rasterization.Segments.Length);
            }

            int uniformSize = Marshal.SizeOf<PathUniforms>();
            dispatches = ArrayPool<RasterizationDispatch>.Shared.Rent(rasterizationCount);
            int totalUniformBytes = 0;
            int groupStart = 0;
            while (groupStart < rasterizationCount)
            {
                var groupInfo = rasterizations[groupStart].Info;
                uint workgroupsX = DivRoundUp(groupInfo.Width, 16);
                uint workgroupsY = DivRoundUp(groupInfo.Height, 16);
                int groupEnd = groupStart + 1;
                while (groupEnd < rasterizationCount)
                {
                    var candidate = rasterizations[groupEnd].Info;
                    if (DivRoundUp(candidate.Width, 16) != workgroupsX ||
                        DivRoundUp(candidate.Height, 16) != workgroupsY)
                    {
                        break;
                    }

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
                groupStart = groupEnd;
            }

            recordData = ArrayPool<GpuPathRecord>.Shared.Rent(totalRecordCount);
            segmentData = ArrayPool<GpuPathSegment>.Shared.Rent(totalSegmentCount);
            uniformData = ArrayPool<byte>.Shared.Rent(totalUniformBytes);
            var uniformSpan = uniformData.AsSpan(0, totalUniformBytes);

            for (int i = 0; i < rasterizationCount; i++)
            {
                var rasterization = rasterizations[i];
                rasterization.Segments.AsSpan().CopyTo(
                    segmentData.AsSpan(rasterization.SegmentOffset, rasterization.Segments.Length));

                for (int recordIndex = 0; recordIndex < rasterization.Records.Length; recordIndex++)
                {
                    var record = rasterization.Records[recordIndex];
                    record.StartSegment = checked(record.StartSegment + (uint)rasterization.SegmentOffset);
                    recordData[rasterization.RecordOffset + recordIndex] = record;
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
                        PathIndex = checked((uint)rasterization.RecordOffset),
                        AtlasX = info.X,
                        AtlasY = info.Y,
                        Width = info.Width,
                        Height = info.Height,
                        SampleGrid = info.Key.SampleGrid
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
                TextureView = _atlasTexture.ViewPtr
            };
            var encoderDescriptor = new CommandEncoderDescriptor
            {
                Label = (byte*)SilkMarshal.StringToPtr("Path Batch Rasterizer Encoder")
            };
            var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
            SilkMarshal.Free((nint)encoderDescriptor.Label);
            var passDescriptor = new ComputePassDescriptor();
            var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
            _context.Api.ComputePassEncoderSetPipeline(pass, _computePipeline);

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
                _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
                _context.Api.ComputePassEncoderDispatchWorkgroups(
                    pass,
                    dispatch.WorkgroupsX,
                    dispatch.WorkgroupsY,
                    checked((uint)dispatch.Count));
            }

            _context.Api.ComputePassEncoderEnd(pass);
            _context.Api.ComputePassEncoderRelease(pass);

            var commandBufferDescriptor = new CommandBufferDescriptor
            {
                Label = (byte*)SilkMarshal.StringToPtr("Path Batch Rasterizer Command Buffer")
            };
            var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandBufferDescriptor);
            SilkMarshal.Free((nint)commandBufferDescriptor.Label);
            _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);

            _context.Api.CommandBufferRelease(commandBuffer);
            _context.Api.CommandEncoderRelease(encoder);
            _pendingPaths.Clear();

            if (diagnosticsEnabled)
            {
                ProGpuVectorDiagnostics.WriteLine(
                    $"[PathAtlas] Rasterized {rasterizationCount} paths ({totalRasterPixels} pixels, " +
                    $"max {maxRasterWidth}x{maxRasterHeight}) in {dispatchCount} dispatches " +
                    "from 3 shared buffer uploads.");
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
        _context.Api.PipelineLayoutRelease(_computePipelineLayout);
        _context.Api.BindGroupLayoutRelease(_computeBindGroupLayout);
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
