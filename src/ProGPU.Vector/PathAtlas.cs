using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
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
    public float Scale;
    public uint PathIndex;
    public uint AtlasX;
    public uint AtlasY;
    public uint Width;
    public uint Height;
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
    public float Scale { get; }

    public PathCacheKey(int contentHash, float scale)
    {
        ContentHash = contentHash;
        Scale = scale;
    }

    public bool Equals(PathCacheKey other)
    {
        return ContentHash == other.ContentHash && Scale.Equals(other.Scale);
    }

    public override bool Equals(object? obj)
    {
        return obj is PathCacheKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ContentHash, Scale);
    }

    public static bool operator ==(PathCacheKey left, PathCacheKey right) => left.Equals(right);
    public static bool operator !=(PathCacheKey left, PathCacheKey right) => !left.Equals(right);
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
    private const int MaxCompiledFillPathCount = 4096;
    private const int MaxCompiledHitTestPathCount = 4096;

    private readonly WgpuContext _context;
    private readonly GpuTexture _atlasTexture;
    private readonly uint _atlasSize;

    private uint _currentX = 2;
    private uint _currentY = 2;
    private uint _currentRowHeight = 0;
    private uint _frameNumber = 0;

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
    private readonly Dictionary<int, CompiledPathData> _compiledFillPaths = new();
    private readonly Dictionary<int, CompiledPathData> _compiledHitTestPaths = new();
    private readonly List<GpuBuffer> _tempBuffers = new();
    private readonly List<PathInfo> _pendingPaths = new();

    private readonly RenderPipelineCache _pipelineCache;
    private readonly ComputePipeline* _computePipeline;
    private readonly GpuBuffer _uniformRingBuffer;
    private uint _ringOffset;
    private bool _isDisposed;

    public GpuTexture AtlasTexture => _atlasTexture;
    public int CachedPathCount => _paths.Count;
    public int CachedHitTestPathCount => _compiledHitTestPaths.Count;

    public PathAtlas(WgpuContext context, uint atlasSize = 2048)
    {
        _context = context;
        _atlasSize = atlasSize;

        _atlasTexture = new GpuTexture(
            _context,
            _atlasSize,
            _atlasSize,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.StorageBinding,
            "Dynamic Path Atlas"
        );

        ClearAtlasTexture();

        _pipelineCache = new RenderPipelineCache(_context);
        var shaderModule = _pipelineCache.GetOrCreateShader("PathRasterizer", Shaders.PathRasterizerShader, "PathRasterizerShader");
        _computePipeline = _pipelineCache.GetOrCreateComputePipeline("PathRasterizer", shaderModule, "cs_main");

        // Allocate a 256KB uniform ring buffer once at startup to eliminate CPU-to-GPU memory allocation overhead
        _uniformRingBuffer = new GpuBuffer(_context, 256 * 1024, BufferUsage.Uniform | BufferUsage.CopyDst, "Path Atlas Uniform Ring Buffer");
        _ringOffset = 0;
    }


    private static uint DivRoundUp(uint value, uint divisor) => (value + divisor - 1) / divisor;

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
        if (_compiledHitTestPaths.TryGetValue(contentHash, out var cached))
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

        if (_compiledHitTestPaths.Count >= MaxCompiledHitTestPathCount)
        {
            _compiledHitTestPaths.Clear();
        }

        _compiledHitTestPaths[contentHash] = new CompiledPathData(
            records,
            segments,
            localMinX,
            localMinY,
            localMaxX,
            localMaxY);
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
        if (_compiledFillPaths.TryGetValue(contentHash, out var cached))
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

        if (_compiledFillPaths.Count >= MaxCompiledFillPathCount)
        {
            _compiledFillPaths.Clear();
        }

        _compiledFillPaths[contentHash] = new CompiledPathData(
            records,
            segments,
            localMinX,
            localMinY,
            localMaxX,
            localMaxY);
        return segments.Length != 0;
    }

    private readonly record struct CompiledPathData(
        GpuPathRecord[] Records,
        GpuPathSegment[] Segments,
        float LocalMinX,
        float LocalMinY,
        float LocalMaxX,
        float LocalMaxY);

    private void RepackActivePaths()
    {
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
                    TexCoordMin = new Vector2(posX * texelSize, posY * texelSize),
                    TexCoordMax = new Vector2((posX + gW) * texelSize, (posY + gH) * texelSize),
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

    private void ClearAtlasTexture()
    {
        int clearByteCount = checked((int)((ulong)_atlasSize * _atlasSize * 4UL));
        byte[] clearData = ArrayPool<byte>.Shared.Rent(clearByteCount);
        try
        {
            clearData.AsSpan(0, clearByteCount).Clear();
            _atlasTexture.WritePixels(clearData.AsSpan(0, clearByteCount));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(clearData);
        }
    }

    public PathInfo GetOrCreatePath(PathGeometry path, float scale)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(PathAtlas));

        int contentHash = ComputeHash(path);
        var key = new PathCacheKey(contentHash, scale);

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

        float minX = unscaledMinX * scale;
        float minY = unscaledMinY * scale;
        float maxX = unscaledMaxX * scale;
        float maxY = unscaledMaxY * scale;

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

        if (_currentX + gW + 2 > _atlasSize)
        {
            _currentX = 2;
            _currentY += _currentRowHeight + 2;
            _currentRowHeight = 0;
        }

        if (_currentY + gH + 2 > _atlasSize)
        {
            ProGpuVectorDiagnostics.WriteLine("[PathAtlas] Texture Atlas is full! Repacking active paths...");
            RepackActivePaths();

            if (_currentX + gW + 2 > _atlasSize)
            {
                _currentX = 2;
                _currentY += _currentRowHeight + 2;
                _currentRowHeight = 0;
            }

            if (_currentY + gH + 2 > _atlasSize)
            {
                ProGpuVectorDiagnostics.WriteLine("[PathAtlas] Warning: Even active paths exceed atlas size after repack!");
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
            TexCoordMin = new Vector2(posX * texelSize, posY * texelSize),
            TexCoordMax = new Vector2((posX + gW) * texelSize, (posY + gH) * texelSize),
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

        _ringOffset = 0; // Reset offset on each batch pass

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Path Batch Rasterizer Encoder") };
        var encoder = _context.Wgpu.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var passDesc = new ComputePassDescriptor();
        var pass = _context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);

        _context.Wgpu.ComputePassEncoderSetPipeline(pass, _computePipeline);

        var entries = stackalloc BindGroupEntry[4];
        nint[]? bindGroupsToRelease = null;
        int bindGroupToReleaseCount = 0;
        nint[]? layoutsToRelease = null;
        int layoutToReleaseCount = 0;

        for (int i = 0; i < _pendingPaths.Count; i++)
        {
            var info = _pendingPaths[i];
            if (info.Width == 0 || info.Height == 0) continue;

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

            int padding = 4;
            float scale = info.Key.Scale;
            float minX = info.UnscaledMinX * scale;
            float minY = info.UnscaledMinY * scale;

            int xStart = (int)Math.Floor(minX) - padding;
            int yStart = (int)Math.Floor(minY) - padding;

            var recordsBuffer = new GpuBuffer(
                _context,
                (uint)(records.Length * Marshal.SizeOf<GpuPathRecord>()),
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Path Records Buffer"
            );
            recordsBuffer.Write(new ReadOnlySpan<GpuPathRecord>(records));
            _tempBuffers.Add(recordsBuffer);

            var segmentsBuffer = new GpuBuffer(
                _context,
                (uint)(segments.Length * Marshal.SizeOf<GpuPathSegment>()),
                BufferUsage.Storage | BufferUsage.CopyDst,
                "Path Segments Buffer"
            );
            segmentsBuffer.Write(new ReadOnlySpan<GpuPathSegment>(segments));
            _tempBuffers.Add(segmentsBuffer);

            var uniforms = new PathUniforms
            {
                XStart = xStart,
                YStart = yStart,
                Scale = scale,
                PathIndex = 0,
                AtlasX = info.X,
                AtlasY = info.Y,
                Width = info.Width,
                Height = info.Height
            };

            uint alignedSize = (uint)((Marshal.SizeOf<PathUniforms>() + 255) & ~255);
            if (_ringOffset + alignedSize > _uniformRingBuffer.Size)
            {
                _ringOffset = 0;
            }
            uint uniformOffset = _ringOffset;
            _context.Wgpu.QueueWriteBuffer(_context.Queue, _uniformRingBuffer.BufferPtr, uniformOffset, &uniforms, (uint)Marshal.SizeOf<PathUniforms>());

            var bindGroupLayout = _context.Wgpu.ComputePipelineGetBindGroupLayout(_computePipeline, 0);

            entries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniformRingBuffer.BufferPtr, Offset = uniformOffset, Size = (uint)Marshal.SizeOf<PathUniforms>() };
            entries[1] = new BindGroupEntry { Binding = 1, Buffer = recordsBuffer.BufferPtr, Offset = 0, Size = recordsBuffer.Size };
            entries[2] = new BindGroupEntry { Binding = 2, Buffer = segmentsBuffer.BufferPtr, Offset = 0, Size = segmentsBuffer.Size };
            entries[3] = new BindGroupEntry { Binding = 3, TextureView = _atlasTexture.ViewPtr };

            var bgDesc = new BindGroupDescriptor
            {
                Layout = bindGroupLayout,
                EntryCount = 4,
                Entries = entries
            };
            var bg = _context.Wgpu.DeviceCreateBindGroup(_context.Device, &bgDesc);

            _context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

            uint workgroupsX = DivRoundUp(info.Width, 16);
            uint workgroupsY = DivRoundUp(info.Height, 16);
            _context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass, workgroupsX, workgroupsY, 1);

            PooledRemovalBuffer.Add(ref bindGroupsToRelease, ref bindGroupToReleaseCount, _pendingPaths.Count, (nint)bg);
            PooledRemovalBuffer.Add(ref layoutsToRelease, ref layoutToReleaseCount, _pendingPaths.Count, (nint)bindGroupLayout);

            _ringOffset += alignedSize;
        }

        _context.Wgpu.ComputePassEncoderEnd(pass);
        _context.Wgpu.ComputePassEncoderRelease(pass);

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Path Batch Rasterizer Command Buffer") };
        var cmdBuffer = _context.Wgpu.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Wgpu.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Wgpu.CommandBufferRelease(cmdBuffer);
        _context.Wgpu.CommandEncoderRelease(encoder);

        for (int i = 0; i < bindGroupToReleaseCount; i++)
        {
            _context.Wgpu.BindGroupRelease((BindGroup*)bindGroupsToRelease![i]);
        }

        for (int i = 0; i < layoutToReleaseCount; i++)
        {
            _context.Wgpu.BindGroupLayoutRelease((BindGroupLayout*)layoutsToRelease![i]);
        }

        PooledRemovalBuffer.Return(bindGroupsToRelease, bindGroupToReleaseCount);
        PooledRemovalBuffer.Return(layoutsToRelease, layoutToReleaseCount);
        _pendingPaths.Clear();
    }

    public void CleanupFrame()
    {
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
        _uniformRingBuffer.Dispose();
        _pipelineCache.Dispose();
        _atlasTexture.Dispose();
        _paths.Clear();
        _compiledFillPaths.Clear();
        _compiledHitTestPaths.Clear();
        _pendingPaths.Clear();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
