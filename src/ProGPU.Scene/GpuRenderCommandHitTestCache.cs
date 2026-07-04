using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Vector;

namespace ProGPU.Scene;

public sealed class GpuRenderCommandHitTestCacheBuilder : IDisposable
{
    private const int MaxLineSeriesSegmentsPerPathPrimitive = 128;
    private const int IntersectPathOperation = 1;
    private const float OpacityEpsilon = 0.0001f;

    private readonly IPathHitTestCompilationCache? _pathHitTestCompilationCache;
    private readonly List<GpuHitTestPrimitive> _primitives = new();
    private readonly List<GpuPathSegment> _pathSegments = new();
    private SmallValueStack<ClipState> _clipStack;
    private SmallValueStack<float> _opacityStack;
    private float _activeOpacity = 1f;
    private int _nextId;

    public GpuRenderCommandHitTestCacheBuilder()
    {
    }

    public GpuRenderCommandHitTestCacheBuilder(IPathHitTestCompilationCache pathHitTestCompilationCache)
    {
        _pathHitTestCompilationCache = pathHitTestCompilationCache ?? throw new ArgumentNullException(nameof(pathHitTestCompilationCache));
    }

    public int PrimitiveCount => _primitives.Count;

    public void Clear()
    {
        _primitives.Clear();
        _pathSegments.Clear();
        _clipStack.Clear();
        _opacityStack.Clear();
        _activeOpacity = 1f;
        _nextId = 0;
    }

    public void Dispose()
    {
        _clipStack.Dispose();
        _opacityStack.Dispose();
    }

    public void AddCommand(in RenderCommand command, Matrix4x4 activeTransform, int? id = null)
    {
        AddCommand(command, activeTransform, provider: null, id);
    }

    public void AddCommand(
        in RenderCommand command,
        Matrix4x4 activeTransform,
        IRenderDataProvider? provider,
        int? id = null)
    {
        activeTransform = NormalizeTransform(activeTransform);

        switch (command.Type)
        {
            case RenderCommandType.PushClip:
                PushClip(command.Rect, activeTransform);
                return;
            case RenderCommandType.PopClip:
                PopClip();
                return;
            case RenderCommandType.PushGeometryClip:
                PushGeometryClip(command, activeTransform);
                return;
            case RenderCommandType.PopGeometryClip:
                PopClip();
                return;
            case RenderCommandType.PushOpacity:
                PushOpacity(command.FontSize);
                return;
            case RenderCommandType.PopOpacity:
                PopOpacity();
                return;
        }

        if (_activeOpacity <= OpacityEpsilon || command.UseGpuTransforms)
        {
            return;
        }

        var primitiveId = ResolvePrimitiveId(id, command.HitTestId);
        float zIndex = _primitives.Count;
        switch (command.Type)
        {
            case RenderCommandType.DrawRect:
                AddRect(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawRoundedRect:
                AddRoundedRect(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawEllipse:
                AddEllipse(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawCircle:
                AddCircle(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawLine:
                AddLine(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawBezier:
                AddQuadraticBezier(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawCubicBezier:
                AddCubicBezier(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawPath:
                AddPath(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawTexture:
            case RenderCommandType.PushOpacityMask:
                AddBounds(command.Rect, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawText:
                AddTextBounds(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawGlyphRun:
                AddGlyphBounds(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.FillTriangle:
                AddTriangleFill(
                    command.GeometryCache?.FillPath,
                    command.Position,
                    command.Position2,
                    command.Position3,
                    command.Brush,
                    activeTransform,
                    primitiveId,
                    zIndex);
                break;
            case RenderCommandType.FillQuad:
                AddQuadFill(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawPolyline:
                AddPolyline(command, activeTransform, primitiveId, zIndex, provider);
                break;
            case RenderCommandType.DrawGpuLineSeries:
                AddGpuLineSeries(command, activeTransform, primitiveId, zIndex, provider);
                break;
            case RenderCommandType.DrawGpuScatterSeries:
                AddGpuScatterSeries(command, activeTransform, primitiveId, zIndex, provider);
                break;
            case RenderCommandType.DrawExtension:
                AddExtension(command, activeTransform, primitiveId, zIndex, provider);
                break;
        }
    }

    private int ResolvePrimitiveId(int? explicitId, int hitTestId)
    {
        if (explicitId is { } value)
        {
            ReserveGeneratedId(value);
            return value;
        }

        if (hitTestId != 0)
        {
            ReserveGeneratedId(hitTestId);
            return hitTestId;
        }

        return _nextId++;
    }

    private void ReserveGeneratedId(int id)
    {
        if (id >= _nextId)
        {
            _nextId = id + 1;
        }
    }

    public GpuHitTestIndex BuildIndex(int maxDepth = 8, int maxPrimitivesPerNode = 32)
    {
        return GpuHitTestIndex.Build(
            CollectionsMarshal.AsSpan(_primitives),
            CollectionsMarshal.AsSpan(_pathSegments),
            maxDepth,
            maxPrimitivesPerNode);
    }

    private void AddRect(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        var (min, max) = ToMinMax(command.Rect);
        if (command.Brush != null)
        {
            AddPrimitive(GpuHitTestPrimitive.RectangleFill(id, min, max, Vector2.Zero, transform, zIndex));
            zIndex += 0.25f;
        }

        if (command.Pen is { Thickness: > 0f } pen)
        {
            AddPrimitive(GpuHitTestPrimitive.RectangleStroke(id, min, max, Vector2.Zero, pen.Thickness, 0f, transform, zIndex));
        }
    }

    private void AddRoundedRect(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        var (min, max) = ToMinMax(command.Rect);
        var radius = new Vector2(command.RadiusX, command.RadiusY);
        if (command.Brush != null)
        {
            AddPrimitive(GpuHitTestPrimitive.RectangleFill(id, min, max, radius, transform, zIndex));
            zIndex += 0.25f;
        }

        if (command.Pen is { Thickness: > 0f } pen)
        {
            AddPrimitive(GpuHitTestPrimitive.RectangleStroke(id, min, max, radius, pen.Thickness, 0f, transform, zIndex));
        }
    }

    private void AddEllipse(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        var center = command.Position2;
        var min = new Vector2(center.X - command.RadiusX, center.Y - command.RadiusY);
        var max = new Vector2(center.X + command.RadiusX, center.Y + command.RadiusY);
        if (command.Brush != null)
        {
            AddPrimitive(GpuHitTestPrimitive.EllipseFill(id, min, max, transform, zIndex));
            zIndex += 0.25f;
        }

        if (command.Pen is { Thickness: > 0f } pen)
        {
            AddPrimitive(GpuHitTestPrimitive.EllipseStroke(id, min, max, pen.Thickness, 0f, transform, zIndex));
        }
    }

    private void AddCircle(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        command.RadiusY = command.RadiusX;
        AddEllipse(command, transform, id, zIndex);
    }

    private void AddLine(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (command.Pen is not { Thickness: > 0f } pen)
        {
            return;
        }

        if (pen.HasDashPattern)
        {
            var linePath = command.GeometryCache?.StrokePath ??
                RenderCommandGeometryCache.CreateLinePath(command.Position, command.Position2);

            if (TryGetDashedStrokePath(command, linePath, pen, out var strokePath, out var strokePen))
            {
                TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, strokePen);
            }

            return;
        }

        AddPrimitive(GpuHitTestPrimitive.LineStroke(
            id,
            command.Position,
            command.Position2,
            pen.Thickness,
            ToLineGeometryCap(pen.StartLineCap),
            ToLineGeometryCap(pen.EndLineCap),
            0f,
            transform,
            zIndex));
    }

    private void AddQuadraticBezier(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (command.Pen is not { Thickness: > 0f } pen)
        {
            return;
        }

        AddBezierPathStroke(
            command.GeometryCache?.StrokePath ??
                RenderCommandGeometryCache.CreateQuadraticBezierPath(command.Position, command.Position2, command.Position3),
            pen,
            command.GeometryCache,
            transform,
            id,
            zIndex);
    }

    private void AddCubicBezier(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (command.Pen is not { Thickness: > 0f } pen)
        {
            return;
        }

        AddBezierPathStroke(
            command.GeometryCache?.StrokePath ??
                RenderCommandGeometryCache.CreateCubicBezierPath(command.Position, command.Position2, command.Position3, command.Position4),
            pen,
            command.GeometryCache,
            transform,
            id,
            zIndex);
    }

    private void AddBezierPathStroke(
        PathGeometry path,
        Pen pen,
        RenderCommandGeometryCache? geometryCache,
        Matrix4x4 transform,
        int id,
        float zIndex)
    {
        if (pen.HasDashPattern)
        {
            if (TryGetDashedStrokePath(geometryCache, path, pen, out var strokePath, out var strokePen))
            {
                TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, strokePen);
            }

            return;
        }

        TryAddPathStrokePrimitive(path, transform, id, zIndex, pen);
    }

    private void AddPath(RenderCommand command, Matrix4x4 activeTransform, int id, float zIndex)
    {
        var commandPath = command.Path;
        if (commandPath == null || command.Brush == null && command.Pen == null)
        {
            return;
        }

        Matrix4x4 transform = command.Transform == default
            ? activeTransform
            : command.Transform * activeTransform;

        Pen? pen = command.Pen is { Thickness: > 0f } activePen ? activePen : null;
        if (pen?.HasDashPattern != true)
        {
            if (!TryCompileHitTestPath(command.GeometryCache?.FillPath ?? commandPath, out var path))
            {
                return;
            }

            if (command.Brush != null)
            {
                AddPathFillPrimitive(path, commandPath.FillRule, transform, id, zIndex);
                zIndex += 0.25f;
            }

            if (pen != null)
            {
                AddPathStrokePrimitive(path, transform, id, zIndex, pen);
            }

            return;
        }

        if (command.Brush != null &&
            TryCompileHitTestPath(command.GeometryCache?.FillPath ?? commandPath, out var fillPath))
        {
            AddPathFillPrimitive(fillPath, commandPath.FillRule, transform, id, zIndex);
            zIndex += 0.25f;
        }

        if (!TryGetDashedStrokePath(command, commandPath, pen, out var strokePath, out var strokePen))
        {
            return;
        }

        TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, strokePen);
    }

    private static bool TryGetDashedStrokePath(
        in RenderCommand command,
        PathGeometry fallbackPath,
        Pen pen,
        out PathGeometry strokePath,
        out Pen strokePen)
    {
        return TryGetDashedStrokePath(command.GeometryCache, fallbackPath, pen, out strokePath, out strokePen);
    }

    private static bool TryGetDashedStrokePath(
        RenderCommandGeometryCache? geometryCache,
        PathGeometry fallbackPath,
        Pen pen,
        out PathGeometry strokePath,
        out Pen strokePen)
    {
        if (geometryCache?.TryGetDashedStrokePath(pen, out strokePath, out strokePen) == true)
        {
            return true;
        }

        if (!Compositor.TryCreateDashedStrokePath(fallbackPath, pen, out strokePath))
        {
            strokePen = null!;
            return false;
        }

        strokePen = Compositor.CreateUndashedPen(pen);
        return true;
    }

    private bool TryCompileHitTestPath(PathGeometry path, out CompiledHitTestPath compiledPath)
    {
        compiledPath = default;
        GpuPathRecord[] records;
        GpuPathSegment[] segments;
        float minX;
        float minY;
        float maxX;
        float maxY;
        try
        {
            if (_pathHitTestCompilationCache != null)
            {
                if (!_pathHitTestCompilationCache.TryGetCompiledHitTestPath(
                        path,
                        out records,
                        out segments,
                        out minX,
                        out minY,
                        out maxX,
                        out maxY))
                {
                    return false;
                }
            }
            else
            {
                (records, segments) = PathAtlas.CompilePath(
                    path,
                    out minX,
                    out minY,
                    out maxX,
                    out maxY);
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (records.Length == 0 || segments.Length == 0)
        {
            return false;
        }

        var min = new Vector2(minX, minY);
        var max = new Vector2(maxX, maxY);
        uint startSegment = checked((uint)_pathSegments.Count);
        uint segmentCount = checked((uint)segments.Length);
        _pathSegments.AddRange(segments);
        compiledPath = new CompiledHitTestPath(min, max, startSegment, segmentCount);
        return true;
    }

    private void AddPathFillPrimitive(
        CompiledHitTestPath path,
        FillRule fillRule,
        Matrix4x4 transform,
        int id,
        float zIndex)
    {
        AddPrimitive(GpuHitTestPrimitive.PathFill(
            id,
            path.Min,
            path.Max,
            path.StartSegment,
            path.SegmentCount,
            fillRule,
            transform,
            zIndex));
    }

    private bool TryAddPathStrokePrimitive(
        PathGeometry path,
        Matrix4x4 transform,
        int id,
        float zIndex,
        Pen pen)
    {
        if (!TryCompileHitTestPath(path, out var strokePath))
        {
            return false;
        }

        AddPathStrokePrimitive(strokePath, transform, id, zIndex, pen);
        return true;
    }

    private void AddPathStrokePrimitive(
        CompiledHitTestPath path,
        Matrix4x4 transform,
        int id,
        float zIndex,
        Pen pen)
    {
        AddPrimitive(GpuHitTestPrimitive.PathStroke(
            id,
            path.Min,
            path.Max,
            path.StartSegment,
            path.SegmentCount,
            pen.Thickness,
            0f,
            transform,
            zIndex));
    }

    private readonly record struct CompiledHitTestPath(
        Vector2 Min,
        Vector2 Max,
        uint StartSegment,
        uint SegmentCount);

    private void AddBounds(Rect rect, Matrix4x4 transform, int id, float zIndex)
    {
        var (min, max) = ToMinMax(rect);
        AddPrimitive(GpuHitTestPrimitive.Bounds(id, min, max, transform, zIndex));
    }

    private void AddTextBounds(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (string.IsNullOrEmpty(command.Text) || command.FontSize <= 0f)
        {
            return;
        }

        float width = MathF.Max(command.FontSize, command.Text.Length * command.FontSize * 0.6f);
        float height = command.FontSize;
        AddPrimitive(GpuHitTestPrimitive.Bounds(
            id,
            command.Position,
            command.Position + new Vector2(width, height),
            transform,
            zIndex));
    }

    private void AddGlyphBounds(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (!command.Rect.IsEmpty)
        {
            AddBounds(command.Rect, transform, id, zIndex);
            return;
        }

        if (command.GlyphPositions is not { Length: > 0 } positions)
        {
            AddTextBounds(command, transform, id, zIndex);
            return;
        }

        Vector2 min = positions[0];
        Vector2 max = positions[0];
        for (int i = 1; i < positions.Length; i++)
        {
            min = Vector2.Min(min, positions[i]);
            max = Vector2.Max(max, positions[i]);
        }

        float padding = MathF.Max(1f, command.FontSize);
        AddPrimitive(GpuHitTestPrimitive.Bounds(id, min, max + new Vector2(padding), transform, zIndex));
    }

    private void AddTriangleFill(
        PathGeometry? cachedPath,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        Brush? brush,
        Matrix4x4 transform,
        int id,
        float zIndex)
    {
        if (brush == null)
        {
            return;
        }

        var path = cachedPath ?? RenderCommandGeometryCache.CreateTrianglePath(p1, p2, p3);
        if (TryCompileHitTestPath(path, out var compiledPath))
        {
            AddPathFillPrimitive(compiledPath, path.FillRule, transform, id, zIndex);
        }
    }

    private void AddQuadFill(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (command.Brush == null)
        {
            return;
        }

        AddTriangleFill(
            command.GeometryCache?.FillPath,
            command.Position,
            command.Position2,
            command.Position3,
            command.Brush,
            transform,
            id,
            zIndex);
        AddTriangleFill(
            command.GeometryCache?.SecondaryFillPath,
            command.Position,
            command.Position3,
            command.Position4,
            command.Brush,
            transform,
            id,
            zIndex + 0.125f);
    }

    private void AddPolyline(
        RenderCommand command,
        Matrix4x4 transform,
        int id,
        float zIndex,
        IRenderDataProvider? provider)
    {
        ReadOnlySpan<Vector2> points = GetPolylinePoints(command, provider);
        if (points.Length < 2 || command.Pen is not { Thickness: > 0f } pen)
        {
            return;
        }

        var path = command.GeometryCache?.StrokePath ??
            RenderCommandGeometryCache.CreatePolylinePath(points, command.IsClosed);
        if (pen.HasDashPattern)
        {
            if (TryGetDashedStrokePath(command, path, pen, out var strokePath, out var strokePen))
            {
                TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, strokePen);
            }

            return;
        }

        TryAddPathStrokePrimitive(path, transform, id, zIndex, pen);
    }

    private void AddExtension(
        RenderCommand command,
        Matrix4x4 transform,
        int id,
        float zIndex,
        IRenderDataProvider? provider)
    {
        switch (command.ExtensionId)
        {
            case CompositorBuiltInExtensions.Spline:
                AddSpline(command, transform, id, zIndex, provider);
                break;
            case CompositorBuiltInExtensions.GpuLineSeries:
                AddGpuLineSeries(command, transform, id, zIndex, provider);
                break;
            case CompositorBuiltInExtensions.GpuScatterSeries:
                AddGpuScatterSeries(command, transform, id, zIndex, provider);
                break;
        }
    }

    private void AddSpline(
        RenderCommand command,
        Matrix4x4 transform,
        int id,
        float zIndex,
        IRenderDataProvider? provider)
    {
        if (command.Pen is not { Thickness: > 0f } pen)
        {
            return;
        }

        var path = command.GeometryCache?.StrokePath;
        if (path == null)
        {
            ReadOnlySpan<Vector2> controlPoints = GetPointBuffer(command, provider);
            ReadOnlySpan<double> knots = GetDoubleBuffer(
                command.DoubleBufferOffset,
                command.DoubleBufferCount,
                command.SplineKnots,
                provider);
            ReadOnlySpan<double> weights = GetDoubleBuffer(
                command.WeightBufferOffset,
                command.WeightBufferCount,
                command.SplineWeights,
                provider);
            path = RenderCommandGeometryCache.CreateSplinePath(
                controlPoints,
                knots,
                weights,
                command.SplineDegree,
                command.IsClosed);
        }

        if (pen.HasDashPattern)
        {
            if (TryGetDashedStrokePath(command, path, pen, out var strokePath, out var strokePen))
            {
                TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, strokePen);
            }

            return;
        }

        TryAddPathStrokePrimitive(path, transform, id, zIndex, pen);
    }

    private void AddGpuLineSeries(
        RenderCommand command,
        Matrix4x4 transform,
        int id,
        float zIndex,
        IRenderDataProvider? provider)
    {
        ReadOnlySpan<float> floats = GetSeriesFloats(command, provider, out int pointsCount);
        if (pointsCount < 2 || floats.Length < pointsCount * 2)
        {
            return;
        }

        float thickness = MathF.Max(1f, command.RadiusX);
        var pen = new Pen(command.Brush ?? new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), thickness);
        Vector2 scale = NormalizeSeriesScale(command.Scale);
        Vector2 translate = command.Translate;
        Matrix4x4 seriesTransform = GetCommandTransform(command, transform);

        PathGeometry? path = null;
        PathFigure? figure = null;
        Vector2 previous = default;
        bool hasPrevious = false;
        int segmentCount = 0;
        int chunkIndex = 0;

        for (int i = 0; i < pointsCount; i++)
        {
            if (!TryReadSeriesPoint(floats, i, stride: 2, scale, translate, out Vector2 point))
            {
                FlushLineSeriesPath();
                hasPrevious = false;
                continue;
            }

            if (!hasPrevious)
            {
                previous = point;
                hasPrevious = true;
                continue;
            }

            if (path == null || figure == null)
            {
                path = new PathGeometry();
                figure = new PathFigure(previous);
                path.Figures.Add(figure);
            }

            figure.Segments.Add(new LineSegment(point));
            segmentCount++;

            if (segmentCount >= MaxLineSeriesSegmentsPerPathPrimitive)
            {
                FlushLineSeriesPath();
            }

            previous = point;
        }

        FlushLineSeriesPath();

        void FlushLineSeriesPath()
        {
            if (path == null || segmentCount == 0)
            {
                path = null;
                figure = null;
                segmentCount = 0;
                return;
            }

            TryAddPathStrokePrimitive(path, seriesTransform, id, zIndex + chunkIndex * 0.0001f, pen);
            chunkIndex++;
            path = null;
            figure = null;
            segmentCount = 0;
        }
    }

    private void AddGpuScatterSeries(
        RenderCommand command,
        Matrix4x4 transform,
        int id,
        float zIndex,
        IRenderDataProvider? provider)
    {
        ReadOnlySpan<float> floats = GetSeriesFloats(command, provider, out int pointsCount);
        if (pointsCount <= 0)
        {
            return;
        }

        int stride = floats.Length >= pointsCount * 3 ? 3 : 2;
        if (floats.Length < pointsCount * stride)
        {
            return;
        }

        Vector2 scale = NormalizeSeriesScale(command.Scale);
        Vector2 translate = command.Translate;
        Matrix4x4 seriesTransform = GetCommandTransform(command, transform);
        float defaultRadius = command.RadiusX;
        for (int i = 0; i < pointsCount; i++)
        {
            if (!TryReadSeriesPoint(floats, i, stride, scale, translate, out Vector2 center))
            {
                continue;
            }

            float radius = stride == 3 ? floats[i * stride + 2] : defaultRadius;
            if (!float.IsFinite(radius) || radius <= 0f)
            {
                continue;
            }

            var extent = new Vector2(radius);
            AddPrimitive(GpuHitTestPrimitive.EllipseFill(
                id,
                center - extent,
                center + extent,
                seriesTransform,
                zIndex + i * 0.0001f));
        }
    }

    private static ReadOnlySpan<Vector2> GetPolylinePoints(RenderCommand command, IRenderDataProvider? provider)
    {
        return GetPointBuffer(command, provider);
    }

    private static ReadOnlySpan<Vector2> GetPointBuffer(RenderCommand command, IRenderDataProvider? provider)
    {
        return provider != null && command.PointBufferCount > 0
            ? provider.GetPoints(command.PointBufferOffset, command.PointBufferCount)
            : command.PolylinePoints is { Length: > 0 } points
                ? points
                : ReadOnlySpan<Vector2>.Empty;
    }

    private static ReadOnlySpan<double> GetDoubleBuffer(
        int offset,
        int count,
        double[]? inlineValues,
        IRenderDataProvider? provider)
    {
        return provider != null && count > 0
            ? provider.GetDoubles(offset, count)
            : inlineValues is { Length: > 0 } values
                ? values
                : ReadOnlySpan<double>.Empty;
    }

    private static ReadOnlySpan<float> GetSeriesFloats(
        RenderCommand command,
        IRenderDataProvider? provider,
        out int pointsCount)
    {
        if (command.StaticBuffer is GpuSeriesBuffer { CachedInterleaved: { Length: > 0 } cachedInterleaved } seriesBuffer)
        {
            pointsCount = seriesBuffer.PointsCount;
            return cachedInterleaved;
        }

        pointsCount = command.GpuPointsCount;
        if (provider != null && command.FloatBufferCount > 0)
        {
            return provider.GetFloats(command.FloatBufferOffset, command.FloatBufferCount);
        }

        return command.GpuPoints is { Length: > 0 } points
            ? points
            : ReadOnlySpan<float>.Empty;
    }

    private static bool TryReadSeriesPoint(
        ReadOnlySpan<float> floats,
        int pointIndex,
        int stride,
        Vector2 scale,
        Vector2 translate,
        out Vector2 point)
    {
        point = default;
        int offset = pointIndex * stride;
        if (offset + 1 >= floats.Length)
        {
            return false;
        }

        float x = floats[offset];
        float y = floats[offset + 1];
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            return false;
        }

        point = new Vector2(x * scale.X + translate.X, y * scale.Y + translate.Y);
        return float.IsFinite(point.X) && float.IsFinite(point.Y);
    }

    private static Vector2 NormalizeSeriesScale(Vector2 scale)
    {
        return scale == Vector2.Zero ? Vector2.One : scale;
    }

    private static Matrix4x4 GetCommandTransform(RenderCommand command, Matrix4x4 activeTransform)
    {
        return command.Transform == default || command.Transform == Matrix4x4.Identity
            ? activeTransform
            : command.Transform * activeTransform;
    }

    private void AddPrimitive(GpuHitTestPrimitive primitive)
    {
        if (!TryApplyActiveClip(ref primitive))
        {
            return;
        }

        if (!IsFinite(primitive.BoundsMin) || !IsFinite(primitive.BoundsMax) ||
            primitive.BoundsMax.X < primitive.BoundsMin.X ||
            primitive.BoundsMax.Y < primitive.BoundsMin.Y)
        {
            return;
        }

        _primitives.Add(primitive);
    }

    public void PushClip(Rect rect, Matrix4x4 transform)
    {
        var (min, max) = ToMinMax(rect);
        TransformBounds(min, max, transform, out Vector2 clipMin, out Vector2 clipMax);
        if (_clipStack.TryPeek(out ClipState active))
        {
            clipMin = Vector2.Max(clipMin, active.Min);
            clipMax = Vector2.Min(clipMax, active.Max);
            _clipStack.Push(active.WithBounds(clipMin, clipMax));
            return;
        }

        _clipStack.Push(new ClipState(clipMin, clipMax));
    }

    private void PushGeometryClip(RenderCommand command, Matrix4x4 activeTransform)
    {
        if (command.Path == null || !command.Path.TryGetBounds(out Vector2 min, out Vector2 max))
        {
            _clipStack.Push(_clipStack.TryPeek(out ClipState active) ? active : ClipState.Unbounded);
            return;
        }

        TransformBounds(min, max, activeTransform, out Vector2 clipMin, out Vector2 clipMax);
        if (_clipStack.TryPeek(out ClipState activeClip))
        {
            clipMin = Vector2.Max(clipMin, activeClip.Min);
            clipMax = Vector2.Min(clipMax, activeClip.Max);
        }

        PathGeometry clipPath = command.Path.CreateTransformed(activeTransform);
        if (_clipStack.TryPeek(out ClipState inheritedClip) &&
            inheritedClip.HasPath &&
            inheritedClip.Path != null)
        {
            clipPath = new PathGeometry
            {
                IsCombined = true,
                PathA = inheritedClip.Path,
                PathB = clipPath,
                Op = IntersectPathOperation,
                FillRule = FillRule.Nonzero
            };
        }

        if (TryCompileHitTestPath(clipPath, out var compiledClip))
        {
            _clipStack.Push(new ClipState(
                clipMin,
                clipMax,
                compiledClip.StartSegment,
                compiledClip.SegmentCount,
                clipPath.FillRule,
                clipPath,
                HasPath: true));
            return;
        }

        _clipStack.Push(
            _clipStack.TryPeek(out ClipState inherited)
                ? inherited.WithBounds(clipMin, clipMax)
                : new ClipState(clipMin, clipMax));
    }

    public void PopClip()
    {
        if (_clipStack.Count > 0)
        {
            _clipStack.Pop();
        }
    }

    private void PushOpacity(float opacity)
    {
        _opacityStack.Push(_activeOpacity);
        _activeOpacity *= float.IsFinite(opacity) ? opacity : 1f;
    }

    private void PopOpacity()
    {
        _activeOpacity = _opacityStack.Count > 0 ? _opacityStack.Pop() : 1f;
    }

    private bool TryApplyActiveClip(ref GpuHitTestPrimitive primitive)
    {
        if (!_clipStack.TryPeek(out ClipState clip) || clip.IsUnbounded)
        {
            return true;
        }

        Vector2 min = Vector2.Max(primitive.BoundsMin, clip.Min);
        Vector2 max = Vector2.Min(primitive.BoundsMax, clip.Max);
        if (max.X < min.X || max.Y < min.Y)
        {
            return false;
        }

        primitive = primitive.WithWorldBounds(min, max);
        if (clip.HasPath)
        {
            primitive = primitive.WithClip(clip.StartSegment, clip.SegmentCount, clip.FillRule);
        }

        return true;
    }

    private static (Vector2 Min, Vector2 Max) ToMinMax(Rect rect)
    {
        return (new Vector2(rect.X, rect.Y), new Vector2(rect.X + rect.Width, rect.Y + rect.Height));
    }

    private static void TransformBounds(Vector2 min, Vector2 max, Matrix4x4 transform, out Vector2 transformedMin, out Vector2 transformedMax)
    {
        Vector2 p0 = Vector2.Transform(min, transform);
        Vector2 p1 = Vector2.Transform(new Vector2(max.X, min.Y), transform);
        Vector2 p2 = Vector2.Transform(max, transform);
        Vector2 p3 = Vector2.Transform(new Vector2(min.X, max.Y), transform);
        transformedMin = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3));
        transformedMax = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3));
    }

    private static Matrix4x4 NormalizeTransform(Matrix4x4 transform)
    {
        return transform == default ? Matrix4x4.Identity : transform;
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }

    private static LineGeometryCap ToLineGeometryCap(PenLineCap cap)
    {
        return cap switch
        {
            PenLineCap.Square => LineGeometryCap.Square,
            PenLineCap.Round => LineGeometryCap.Round,
            PenLineCap.Triangle => LineGeometryCap.Triangle,
            _ => LineGeometryCap.Flat
        };
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
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                {
                    _items[_count] = default!;
                    if (_count == 0)
                    {
                        _first = default!;
                    }
                }

                return item;
            }

            var first = _first;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                _first = default!;
            }

            return first;
        }

        public readonly bool TryPeek(out T item)
        {
            if (_count == 0)
            {
                item = default!;
                return false;
            }

            item = _items != null
                ? _items[_count - 1]
                : _first;
            return true;
        }

        public void Clear()
        {
            if (_items != null && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                Array.Clear(_items, 0, _count);
                _first = default!;
            }
            else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                _first = default!;
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
    }

    private readonly record struct ClipState(
        Vector2 Min,
        Vector2 Max,
        uint StartSegment = 0,
        uint SegmentCount = 0,
        FillRule FillRule = FillRule.Nonzero,
        PathGeometry? Path = null,
        bool HasPath = false)
    {
        public static ClipState Unbounded { get; } = new(
            new Vector2(float.NegativeInfinity, float.NegativeInfinity),
            new Vector2(float.PositiveInfinity, float.PositiveInfinity));

        public bool IsUnbounded =>
            float.IsNegativeInfinity(Min.X) &&
            float.IsNegativeInfinity(Min.Y) &&
            float.IsPositiveInfinity(Max.X) &&
            float.IsPositiveInfinity(Max.Y);

        public ClipState WithBounds(Vector2 min, Vector2 max)
        {
            return new ClipState(min, max, StartSegment, SegmentCount, FillRule, Path, HasPath);
        }
    }
}
