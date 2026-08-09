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
            case RenderCommandType.PopClip:
                PopClip();
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
            case RenderCommandType.PushOpacityMask:
            case RenderCommandType.PopOpacityMask:
                // Opacity masks are compositor state, not independently
                // hittable geometry. Precise alpha-mask sampling is outside
                // the retained geometric hit-test contract; preserve the
                // enclosed primitives without adding a phantom bounds hit.
                return;
        }

        if (!IsFiniteInvertibleAffine2D(activeTransform))
        {
            // Preserve clip-stack balance while making an invalid clip reject
            // all descendants. Precise rendering also fails closed for these
            // transforms; substituting Identity here creates ghost hits.
            if (command.Type is RenderCommandType.PushClip or
                RenderCommandType.PushGeometryClip)
            {
                _clipStack.Push(ClipState.Empty);
            }
            return;
        }

        switch (command.Type)
        {
            case RenderCommandType.PushClip:
                PushClip(command.Rect, activeTransform);
                return;
            case RenderCommandType.PushGeometryClip:
                PushGeometryClip(command, activeTransform);
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
            case RenderCommandType.DrawDotGrid:
                AddBounds(command.Rect, activeTransform, primitiveId, zIndex);
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
            case RenderCommandType.DrawVertexMesh:
                AddVertexMesh(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawPointBatch:
                AddPointBatch(command, activeTransform, primitiveId, zIndex);
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

        if (Compositor.IsRenderableStroke(command.Pen) &&
            Compositor.TryResolveLocalStrokeThickness(command, out var localThickness))
        {
            if (command.Pen!.HasDashPattern)
            {
                AddDashedPrimitiveStroke(
                    command,
                    transform,
                    id,
                    zIndex,
                    localThickness);
            }
            else if (command.Pen.IsHairline)
            {
                TryAddDeviceHairlinePathStrokePrimitive(
                    command.GeometryCache?.StrokePath ??
                        PrimitivePathGeometry.CreateRectangle(
                            command.Rect.X,
                            command.Rect.Y,
                            command.Rect.Width,
                            command.Rect.Height),
                    transform,
                    id,
                    zIndex,
                    command.Pen!);
            }
            else
            {
                AddPrimitive(GpuHitTestPrimitive.RectangleStroke(id, min, max, Vector2.Zero, localThickness, 0f, transform, zIndex));
            }
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

        if (Compositor.IsRenderableStroke(command.Pen) &&
            Compositor.TryResolveLocalStrokeThickness(command, out var localThickness))
        {
            if (command.Pen!.HasDashPattern)
            {
                AddDashedPrimitiveStroke(
                    command,
                    transform,
                    id,
                    zIndex,
                    localThickness);
            }
            else if (command.Pen.IsHairline)
            {
                TryAddDeviceHairlinePathStrokePrimitive(
                    command.GeometryCache?.StrokePath ??
                        PrimitivePathGeometry.CreateRoundedRectangle(
                            command.Rect.X,
                            command.Rect.Y,
                            command.Rect.Width,
                            command.Rect.Height,
                            command.RadiusX,
                            command.RadiusY),
                    transform,
                    id,
                    zIndex,
                    command.Pen!);
            }
            else
            {
                AddPrimitive(GpuHitTestPrimitive.RectangleStroke(id, min, max, radius, localThickness, 0f, transform, zIndex));
            }
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

        if (Compositor.IsRenderableStroke(command.Pen) &&
            Compositor.TryResolveLocalStrokeThickness(command, out var localThickness))
        {
            if (command.Pen!.HasDashPattern)
            {
                AddDashedPrimitiveStroke(
                    command,
                    transform,
                    id,
                    zIndex,
                    localThickness);
            }
            else if (command.Pen.IsHairline)
            {
                TryAddDeviceHairlinePathStrokePrimitive(
                    command.GeometryCache?.StrokePath ??
                        PrimitivePathGeometry.CreateEllipse(
                            center,
                            command.RadiusX,
                            command.RadiusY),
                    transform,
                    id,
                    zIndex,
                    command.Pen!);
            }
            else
            {
                AddPrimitive(GpuHitTestPrimitive.EllipseStroke(id, min, max, localThickness, 0f, transform, zIndex));
            }
        }
    }

    private void AddDashedPrimitiveStroke(
        in RenderCommand command,
        Matrix4x4 transform,
        int id,
        float zIndex,
        float localThickness)
    {
        var sourcePath = command.GeometryCache?.StrokePath ??
            RenderCommandGeometryCache.CreatePrimitiveStrokePath(command);
        var pen = command.Pen!;
        if (sourcePath == null ||
            !TryGetDashedStrokePath(
                command,
                sourcePath,
                pen,
                localThickness,
                out var strokePath,
                out var strokePen))
        {
            return;
        }

        if (pen.IsHairline)
        {
            TryAddDeviceHairlinePathStrokePrimitive(
                strokePath,
                transform,
                id,
                zIndex,
                strokePen);
        }
        else
        {
            TryAddPathStrokePrimitive(
                strokePath,
                transform,
                id,
                zIndex,
                strokePen,
                localThickness);
        }
    }

    private void AddCircle(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        command.RadiusY = command.RadiusX;
        AddEllipse(command, transform, id, zIndex);
    }

    private void AddLine(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (!Compositor.IsRenderableStroke(command.Pen))
        {
            return;
        }
        var pen = command.Pen!;
        if (!Compositor.TryResolveLocalStrokeThickness(command, out var localThickness))
        {
            return;
        }

        if (pen.HasDashPattern)
        {
            var linePath = command.GeometryCache?.StrokePath ??
                RenderCommandGeometryCache.CreateLinePath(command.Position, command.Position2);

            if (TryGetDashedStrokePath(command, linePath, pen, localThickness, out var strokePath, out var strokePen))
            {
                if (pen.IsHairline)
                {
                    TryAddDeviceHairlinePathStrokePrimitive(strokePath, transform, id, zIndex, strokePen);
                }
                else
                {
                    TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, strokePen, localThickness);
                }
            }

            return;
        }

        var lineTransform = pen.IsHairline ? Matrix4x4.Identity : transform;
        var lineStart = pen.IsHairline
            ? Vector2.Transform(command.Position, transform)
            : command.Position;
        var lineEnd = pen.IsHairline
            ? Vector2.Transform(command.Position2, transform)
            : command.Position2;
        AddPrimitive(GpuHitTestPrimitive.LineStroke(
            id,
            lineStart,
            lineEnd,
            pen.IsHairline ? 1f : localThickness,
            ToLineGeometryCap(pen.StartLineCap),
            ToLineGeometryCap(pen.EndLineCap),
            0f,
            lineTransform,
            zIndex));
    }

    private void AddQuadraticBezier(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (!Compositor.IsRenderableStroke(command.Pen))
        {
            return;
        }
        var pen = command.Pen!;
        if (!Compositor.TryResolveLocalStrokeThickness(command, out var localThickness))
        {
            return;
        }

        AddBezierPathStroke(
            command.GeometryCache?.StrokePath ??
                RenderCommandGeometryCache.CreateQuadraticBezierPath(command.Position, command.Position2, command.Position3),
            pen,
            localThickness,
            command.GeometryCache,
            transform,
            id,
            zIndex);
    }

    private void AddCubicBezier(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (!Compositor.IsRenderableStroke(command.Pen))
        {
            return;
        }
        var pen = command.Pen!;
        if (!Compositor.TryResolveLocalStrokeThickness(command, out var localThickness))
        {
            return;
        }

        AddBezierPathStroke(
            command.GeometryCache?.StrokePath ??
                RenderCommandGeometryCache.CreateCubicBezierPath(command.Position, command.Position2, command.Position3, command.Position4),
            pen,
            localThickness,
            command.GeometryCache,
            transform,
            id,
            zIndex);
    }

    private void AddBezierPathStroke(
        PathGeometry path,
        Pen pen,
        float localThickness,
        RenderCommandGeometryCache? geometryCache,
        Matrix4x4 transform,
        int id,
        float zIndex)
    {
        if (pen.HasDashPattern)
        {
            if (TryGetDashedStrokePath(geometryCache, path, pen, localThickness, out var strokePath, out var strokePen))
            {
                if (pen.IsHairline)
                {
                    TryAddDeviceHairlinePathStrokePrimitive(strokePath, transform, id, zIndex, strokePen);
                }
                else
                {
                    TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, strokePen, localThickness);
                }
            }

            return;
        }

        if (pen.IsHairline)
        {
            TryAddDeviceHairlinePathStrokePrimitive(path, transform, id, zIndex, pen);
        }
        else
        {
            TryAddPathStrokePrimitive(path, transform, id, zIndex, pen, localThickness);
        }
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
        if (!IsFiniteInvertibleAffine2D(transform))
        {
            return;
        }

        Pen? pen = Compositor.IsRenderableStroke(command.Pen)
            ? command.Pen
            : null;
        var localThickness = 0f;
        var hasLocalStroke = pen != null &&
            Compositor.TryResolveLocalStrokeThickness(command, out localThickness);
        if (pen?.HasDashPattern != true)
        {
            var fillSource = command.GeometryCache?.FillPath ?? commandPath;
            CompiledHitTestPath? solidFillPath = null;
            if (command.Brush != null &&
                TryCompileHitTestPath(fillSource, out var compiledFillPath))
            {
                solidFillPath = compiledFillPath;
                AddPathFillPrimitive(compiledFillPath, transform, id, zIndex);
                zIndex += 0.25f;
            }

            if (hasLocalStroke)
            {
                var strokeSource = command.GeometryCache?.StrokePath ?? commandPath;
                if (pen!.IsHairline)
                {
                    TryAddDeviceHairlinePathStrokePrimitive(
                        strokeSource,
                        transform,
                        id,
                        zIndex,
                        pen);
                }
                else
                {
                    TryAddPathStrokePrimitive(
                        strokeSource,
                        transform,
                        id,
                        zIndex,
                        pen,
                        localThickness,
                        solidFillPath.HasValue && ReferenceEquals(strokeSource, fillSource)
                            ? solidFillPath
                            : null);
                }
            }

            return;
        }

        if (command.Brush != null &&
            TryCompileHitTestPath(command.GeometryCache?.FillPath ?? commandPath, out var fillPath))
        {
            AddPathFillPrimitive(fillPath, transform, id, zIndex);
            zIndex += 0.25f;
        }

        if (!hasLocalStroke ||
            !TryGetDashedStrokePath(command, commandPath, pen, localThickness, out var strokePath, out var strokePen))
        {
            return;
        }

        if (pen.IsHairline)
        {
            TryAddDeviceHairlinePathStrokePrimitive(strokePath, transform, id, zIndex, strokePen);
        }
        else
        {
            TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, strokePen, localThickness);
        }
    }

    private static bool TryGetDashedStrokePath(
        in RenderCommand command,
        PathGeometry fallbackPath,
        Pen pen,
        float localThickness,
        out PathGeometry strokePath,
        out Pen strokePen)
    {
        return TryGetDashedStrokePath(
            command.GeometryCache,
            fallbackPath,
            pen,
            localThickness,
            out strokePath,
            out strokePen);
    }

    private static bool TryGetDashedStrokePath(
        RenderCommandGeometryCache? geometryCache,
        PathGeometry fallbackPath,
        Pen pen,
        float localThickness,
        out PathGeometry strokePath,
        out Pen strokePen)
    {
        if (geometryCache?.TryGetDashedStrokePath(
                pen,
                localThickness,
                out strokePath,
                out strokePen) == true)
        {
            return true;
        }

        if (!Compositor.TryCreateDashedStrokePath(
                fallbackPath,
                pen,
                localThickness,
                out strokePath))
        {
            strokePen = null!;
            return false;
        }

        strokePen = Compositor.CreateUndashedPen(pen, localThickness);
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
        uint segmentCount = checked((uint)segments.Length);
        uint startSegment = AppendPathSegments(segments);
        compiledPath = new CompiledHitTestPath(
            min,
            max,
            startSegment,
            segmentCount,
            (FillRule)records[0].FillRule);
        return true;
    }

    private uint AppendPathSegments(ReadOnlySpan<GpuPathSegment> segments)
    {
        int startSegment = _pathSegments.Count;
        _pathSegments.EnsureCapacity(checked(startSegment + segments.Length));
        for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
        {
            _pathSegments.Add(segments[segmentIndex]);
        }

        return checked((uint)startSegment);
    }

    private void AddPathFillPrimitive(
        CompiledHitTestPath path,
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
            path.FillRule,
            transform,
            zIndex));
    }

    private bool TryAddPathStrokePrimitive(
        PathGeometry path,
        Matrix4x4 transform,
        int id,
        float zIndex,
        Pen pen,
        float localThickness,
        CompiledHitTestPath? precompiledPath = null)
    {
        if (!pen.IsHairline &&
            (HasStrokeCapOverride(path) ||
             pen.StartLineCap != PenLineCap.Round ||
             pen.EndLineCap != PenLineCap.Round))
        {
            if (TryAddLoweredLineStrokePrimitives(path, transform, id, zIndex, pen, localThickness) ||
                TryAddLoweredPathStrokePrimitives(
                    path,
                    transform,
                    id,
                    zIndex,
                    pen,
                    localThickness,
                    precompiledPath))
            {
                return true;
            }
        }

        if (precompiledPath.HasValue)
        {
            AddPathStrokePrimitive(
                precompiledPath.Value,
                transform,
                id,
                zIndex,
                localThickness);
            return true;
        }

        return TryAddPathStrokePrimitive(path, transform, id, zIndex, localThickness);
    }

    private static bool HasStrokeCapOverride(PathGeometry path)
    {
        var figures = path.Figures;
        for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            var figure = figures[figureIndex];
            if (figure.StrokeStartLineCap.HasValue || figure.StrokeEndLineCap.HasValue)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryAddLoweredLineStrokePrimitives(
        PathGeometry path,
        Matrix4x4 transform,
        int id,
        float zIndex,
        Pen pen,
        float localThickness)
    {
        var figures = path.Figures;
        for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            var figure = figures[figureIndex];
            if (figure.IsClosed ||
                figure.Segments.Count != 1 ||
                figure.Segments[0] is not LineSegment { IsStroked: true })
            {
                return false;
            }

        }

        if (figures.Count == 0)
        {
            return false;
        }

        for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            var figure = figures[figureIndex];
            var line = (LineSegment)figure.Segments[0];
            AddPrimitive(GpuHitTestPrimitive.LineStroke(
                id,
                figure.StartPoint,
                line.Point,
                localThickness,
                ToLineGeometryCap(figure.StrokeStartLineCap ?? pen.StartLineCap),
                ToLineGeometryCap(figure.StrokeEndLineCap ?? pen.EndLineCap),
                tolerance: 0f,
                transform: transform,
                zIndex: zIndex));
        }

        return true;
    }

    private bool TryAddLoweredPathStrokePrimitives(
        PathGeometry path,
        Matrix4x4 transform,
        int id,
        float zIndex,
        Pen pen,
        float localThickness,
        CompiledHitTestPath? precompiledPath)
    {
        var figures = path.Figures;
        var expectedSegmentCount = 0;
        for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            expectedSegmentCount += CountCompiledFigureSegments(figures[figureIndex]);
        }

        if (expectedSegmentCount == 0)
        {
            return false;
        }

        CompiledHitTestPath compiledPath;
        if (precompiledPath.HasValue)
        {
            compiledPath = precompiledPath.Value;
        }
        else if (!TryCompileHitTestPath(path, out compiledPath))
        {
            return false;
        }

        if (compiledPath.SegmentCount != (uint)expectedSegmentCount)
        {
            return false;
        }

        uint figureStartSegment = compiledPath.StartSegment;
        for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            var figure = figures[figureIndex];
            var figureSegmentCount = CountCompiledFigureSegments(figure);
            if (figureSegmentCount == 0)
            {
                continue;
            }

            if (!TryGetCompiledFigureBounds(figure, out var figureMin, out var figureMax))
            {
                return false;
            }

            var figurePath = new CompiledHitTestPath(
                figureMin,
                figureMax,
                figureStartSegment,
                checked((uint)figureSegmentCount),
                compiledPath.FillRule);
            AddPathStrokePrimitive(
                figurePath,
                transform,
                id,
                zIndex,
                localThickness,
                figure.IsClosed
                    ? LineGeometryCap.Round
                    : ToLineGeometryCap(figure.StrokeStartLineCap ?? pen.StartLineCap),
                figure.IsClosed
                    ? LineGeometryCap.Round
                    : ToLineGeometryCap(figure.StrokeEndLineCap ?? pen.EndLineCap));
            figureStartSegment += checked((uint)figureSegmentCount);
        }

        return true;
    }

    private static int CountCompiledFigureSegments(PathFigure figure)
    {
        var count = 0;
        var currentPoint = figure.StartPoint;
        var segments = figure.Segments;
        for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            switch (segments[segmentIndex])
            {
                case LineSegment line:
                    count++;
                    currentPoint = line.Point;
                    break;
                case QuadraticBezierSegment quadratic:
                    count++;
                    currentPoint = quadratic.Point;
                    break;
                case CubicBezierSegment cubic:
                    count++;
                    currentPoint = cubic.Point;
                    break;
                case ArcSegment arc:
                    if (ArcSegmentGeometry.TryGetArcCenter(
                            currentPoint,
                            arc.Point,
                            arc.Size,
                            arc.RotationAngle,
                            arc.IsLargeArc,
                            arc.SweepDirection,
                            out _,
                            out _,
                            out _,
                            out _,
                            out _) ||
                        currentPoint != arc.Point)
                    {
                        count++;
                    }

                    currentPoint = arc.Point;
                    break;
            }
        }

        if (figure.IsClosed && currentPoint != figure.StartPoint)
        {
            count++;
        }

        return count;
    }

    private static bool TryGetCompiledFigureBounds(
        PathFigure figure,
        out Vector2 min,
        out Vector2 max)
    {
        var minValue = new Vector2(float.MaxValue);
        var maxValue = new Vector2(float.MinValue);
        var hasBounds = false;

        void Update(Vector2 point)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            {
                return;
            }

            minValue = Vector2.Min(minValue, point);
            maxValue = Vector2.Max(maxValue, point);
            hasBounds = true;
        }

        var currentPoint = figure.StartPoint;
        Update(currentPoint);
        var segments = figure.Segments;
        for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            switch (segments[segmentIndex])
            {
                case LineSegment line:
                    Update(line.Point);
                    currentPoint = line.Point;
                    break;
                case QuadraticBezierSegment quadratic:
                    Update(quadratic.ControlPoint);
                    Update(quadratic.Point);
                    currentPoint = quadratic.Point;
                    break;
                case CubicBezierSegment cubic:
                    Update(cubic.ControlPoint1);
                    Update(cubic.ControlPoint2);
                    Update(cubic.Point);
                    currentPoint = cubic.Point;
                    break;
                case ArcSegment arc:
                    if (ArcSegmentGeometry.TryGetArcBounds(
                            currentPoint,
                            arc,
                            out var arcMin,
                            out var arcMax))
                    {
                        Update(arcMin);
                        Update(arcMax);
                    }
                    else
                    {
                        Update(arc.Point);
                    }

                    currentPoint = arc.Point;
                    break;
            }
        }

        if (figure.IsClosed)
        {
            Update(figure.StartPoint);
        }

        min = hasBounds ? minValue : default;
        max = hasBounds ? maxValue : default;
        return hasBounds;
    }

    private bool TryAddPathStrokePrimitive(
        PathGeometry path,
        Matrix4x4 transform,
        int id,
        float zIndex,
        float localThickness)
    {
        if (!TryCompileHitTestPath(path, out var strokePath))
        {
            return false;
        }

        AddPathStrokePrimitive(strokePath, transform, id, zIndex, localThickness);
        return true;
    }

    private void AddPathStrokePrimitive(
        CompiledHitTestPath path,
        Matrix4x4 transform,
        int id,
        float zIndex,
        float localThickness)
    {
        AddPrimitive(GpuHitTestPrimitive.PathStroke(
            id,
            path.Min,
            path.Max,
            path.StartSegment,
            path.SegmentCount,
            localThickness,
            0f,
            transform,
            zIndex));
    }

    private void AddPathStrokePrimitive(
        CompiledHitTestPath path,
        Matrix4x4 transform,
        int id,
        float zIndex,
        float localThickness,
        LineGeometryCap startCap,
        LineGeometryCap endCap)
    {
        AddPrimitive(GpuHitTestPrimitive.PathStroke(
            id,
            path.Min,
            path.Max,
            path.StartSegment,
            path.SegmentCount,
            localThickness,
            0f,
            startCap,
            endCap,
            transform,
            zIndex));
    }

    /// <summary>
    /// Compiles a retained path into framebuffer coordinates for a Skia
    /// device hairline. This keeps the precise GPU hit-test primitive at one
    /// pixel under anisotropic scale and shear instead of approximating it
    /// with one inverse-scaled local width.
    /// </summary>
    private bool TryAddDeviceHairlinePathStrokePrimitive(
        PathGeometry path,
        Matrix4x4 transform,
        int id,
        float zIndex,
        Pen pen)
    {
        var segmentCheckpoint = _pathSegments.Count;
        var primitiveCheckpoint = _primitives.Count;
        if (!TryCompileHitTestPath(path, out var localPath))
        {
            return false;
        }

        var localStart = checked((int)localPath.StartSegment);
        var localEnd = checked(localStart + (int)localPath.SegmentCount);
        var expectedSegmentCount = 0;
        var figures = path.Figures;
        for (var figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            expectedSegmentCount += CountCompiledFigureSegments(figures[figureIndex]);
        }

        if (localStart != segmentCheckpoint ||
            localEnd != _pathSegments.Count ||
            expectedSegmentCount != (int)localPath.SegmentCount)
        {
            Rollback();
            return false;
        }

        var transformedStart = _pathSegments.Count;
        var localFigureStart = localStart;
        for (var figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            var figure = figures[figureIndex];
            var localFigureCount = CountCompiledFigureSegments(figure);
            if (localFigureCount == 0)
            {
                continue;
            }

            var figureTransformedStart = _pathSegments.Count;
            var transformedMin = new Vector2(float.PositiveInfinity);
            var transformedMax = new Vector2(float.NegativeInfinity);
            var localFigureEnd = localFigureStart + localFigureCount;
            for (var segmentIndex = localFigureStart; segmentIndex < localFigureEnd; segmentIndex++)
            {
                var segment = _pathSegments[segmentIndex];
                switch (segment.SegmentType)
                {
                    case 0u:
                        AppendTransformedHairlineSegment(
                            segment,
                            transform,
                            pointCount: 2,
                            ref transformedMin,
                            ref transformedMax);
                        break;
                    case 1u:
                        AppendTransformedHairlineSegment(
                            segment,
                            transform,
                            pointCount: 3,
                            ref transformedMin,
                            ref transformedMax);
                        break;
                    case 2u:
                        AppendTransformedHairlineSegment(
                            segment,
                            transform,
                            pointCount: 4,
                            ref transformedMin,
                            ref transformedMax);
                        break;
                    case 3u:
                        AppendTransformedHairlineArc(
                            segment,
                            transform,
                            ref transformedMin,
                            ref transformedMax);
                        break;
                }
            }

            var figureTransformedCount = _pathSegments.Count - figureTransformedStart;
            if (figureTransformedCount <= 0 ||
                !float.IsFinite(transformedMin.X) ||
                !float.IsFinite(transformedMin.Y) ||
                !float.IsFinite(transformedMax.X) ||
                !float.IsFinite(transformedMax.Y))
            {
                Rollback();
                return false;
            }

            var finalFigureStart = localStart + (figureTransformedStart - transformedStart);
            AddPathStrokePrimitive(
                new CompiledHitTestPath(
                    transformedMin,
                    transformedMax,
                    checked((uint)finalFigureStart),
                    checked((uint)figureTransformedCount),
                    localPath.FillRule),
                Matrix4x4.Identity,
                id,
                zIndex,
                localThickness: 1f,
                figure.IsClosed
                    ? LineGeometryCap.Round
                    : ToLineGeometryCap(figure.StrokeStartLineCap ?? pen.StartLineCap),
                figure.IsClosed
                    ? LineGeometryCap.Round
                    : ToLineGeometryCap(figure.StrokeEndLineCap ?? pen.EndLineCap));
            localFigureStart = localFigureEnd;
        }

        // The compiler appends local-space source segments before the framebuffer
        // copies. Remove that now-unused range; the precomputed primitive offsets
        // above already target the compacted framebuffer-space segment indices.
        _pathSegments.RemoveRange(localStart, localEnd - localStart);
        return true;

        void Rollback()
        {
            if (_pathSegments.Count > segmentCheckpoint)
            {
                _pathSegments.RemoveRange(
                    segmentCheckpoint,
                    _pathSegments.Count - segmentCheckpoint);
            }

            if (_primitives.Count > primitiveCheckpoint)
            {
                _primitives.RemoveRange(
                    primitiveCheckpoint,
                    _primitives.Count - primitiveCheckpoint);
            }
        }
    }

    private void AppendTransformedHairlineSegment(
        GpuPathSegment segment,
        Matrix4x4 transform,
        int pointCount,
        ref Vector2 min,
        ref Vector2 max)
    {
        segment.P0 = TransformHairlinePoint(segment.P0, transform, ref min, ref max);
        segment.P1 = TransformHairlinePoint(segment.P1, transform, ref min, ref max);
        if (pointCount >= 3)
        {
            segment.P2 = TransformHairlinePoint(segment.P2, transform, ref min, ref max);
        }
        if (pointCount >= 4)
        {
            segment.P3 = TransformHairlinePoint(segment.P3, transform, ref min, ref max);
        }
        _pathSegments.Add(segment);
    }

    private void AppendTransformedHairlineArc(
        GpuPathSegment segment,
        Matrix4x4 transform,
        ref Vector2 min,
        ref Vector2 max)
    {
        const int ArcSubdivisionCount = 32;
        var thetaStart = BitConverter.UInt32BitsToSingle(segment.Pad0);
        var deltaTheta = BitConverter.UInt32BitsToSingle(segment.Pad1);
        var rotation = BitConverter.UInt32BitsToSingle(segment.Pad2);
        var cosine = MathF.Cos(rotation);
        var sine = MathF.Sin(rotation);
        var previous = TransformHairlinePoint(
            EvaluateArc(thetaStart),
            transform,
            ref min,
            ref max);
        for (var subdivision = 1; subdivision <= ArcSubdivisionCount; subdivision++)
        {
            var theta = thetaStart +
                deltaTheta * (subdivision / (float)ArcSubdivisionCount);
            var next = TransformHairlinePoint(
                EvaluateArc(theta),
                transform,
                ref min,
                ref max);
            _pathSegments.Add(new GpuPathSegment
            {
                P0 = previous,
                P1 = next,
                SegmentType = 0u
            });
            previous = next;
        }

        Vector2 EvaluateArc(float theta)
        {
            var local = new Vector2(
                MathF.Cos(theta) * segment.P3.X,
                MathF.Sin(theta) * segment.P3.Y);
            return segment.P2 + new Vector2(
                local.X * cosine - local.Y * sine,
                local.X * sine + local.Y * cosine);
        }
    }

    private static Vector2 TransformHairlinePoint(
        Vector2 point,
        Matrix4x4 transform,
        ref Vector2 min,
        ref Vector2 max)
    {
        var transformed = Vector2.Transform(point, transform);
        min = Vector2.Min(min, transformed);
        max = Vector2.Max(max, transformed);
        return transformed;
    }

    private readonly record struct CompiledHitTestPath(
        Vector2 Min,
        Vector2 Max,
        uint StartSegment,
        uint SegmentCount,
        FillRule FillRule);

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

        int rangeStart = command.GlyphRangeCount > 0
            ? command.GlyphRangeStart
            : 0;
        int rangeCount = command.GlyphRangeCount > 0
            ? command.GlyphRangeCount
            : positions.Length;
        if (rangeStart < 0 ||
            rangeCount <= 0 ||
            rangeStart > positions.Length - rangeCount)
        {
            return;
        }

        Vector2 min = positions[rangeStart];
        Vector2 max = positions[rangeStart];
        int rangeEnd = rangeStart + rangeCount;
        for (int i = rangeStart + 1; i < rangeEnd; i++)
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
            AddPathFillPrimitive(compiledPath, transform, id, zIndex);
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

    private void AddVertexMesh(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (command.Brush is null || command.VertexMesh is not { } mesh)
        {
            return;
        }

        var positions = mesh.PositionArray;
        var triangleCount = mesh.GetTriangleCount();
        for (var triangle = 0; triangle < triangleCount; triangle++)
        {
            mesh.GetTriangle(triangle, out var index0, out var index1, out var index2);
            if ((uint)index0 >= (uint)positions.Length ||
                (uint)index1 >= (uint)positions.Length ||
                (uint)index2 >= (uint)positions.Length)
            {
                continue;
            }

            AddTriangleFill(
                null,
                positions[index0],
                positions[index1],
                positions[index2],
                command.Brush,
                transform,
                id,
                zIndex + triangle * 0.001f);
        }
    }

    private void AddPointBatch(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (command.Brush is null || command.PolylinePoints is not { Length: > 0 } points)
        {
            return;
        }

        var isHairline = command.RadiusX <= 0f;
        var radius = isHairline ? 0.5f : command.RadiusX;
        var diameter = radius * 2f;
        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            if (isHairline)
            {
                point = Vector2.Transform(point, transform);
                if (command.IsEdgeAliased)
                {
                    point = new Vector2(MathF.Floor(point.X) + 0.5f, MathF.Floor(point.Y) + 0.5f);
                }
            }

            var min = new Vector2(point.X - radius, point.Y - radius);
            var max = min + new Vector2(diameter);
            var pointZIndex = zIndex + index * 0.0001f;
            AddPrimitive(command.IntParam != 0
                ? GpuHitTestPrimitive.EllipseFill(
                    id,
                    min,
                    max,
                    isHairline ? Matrix4x4.Identity : transform,
                    pointZIndex)
                : GpuHitTestPrimitive.RectangleFill(
                    id,
                    min,
                    max,
                    Vector2.Zero,
                    isHairline ? Matrix4x4.Identity : transform,
                    pointZIndex));
        }
    }

    private void AddPolyline(
        RenderCommand command,
        Matrix4x4 transform,
        int id,
        float zIndex,
        IRenderDataProvider? provider)
    {
        ReadOnlySpan<Vector2> points = GetPolylinePoints(command, provider);
        if (points.Length < 2 || !Compositor.IsRenderableStroke(command.Pen))
        {
            return;
        }
        var pen = command.Pen!;
        if (!Compositor.TryResolveLocalStrokeThickness(command, out var localThickness))
        {
            return;
        }

        var path = command.GeometryCache?.StrokePath ??
            RenderCommandGeometryCache.CreatePolylinePath(points, command.IsClosed);
        if (pen.HasDashPattern)
        {
            if (TryGetDashedStrokePath(command, path, pen, localThickness, out var strokePath, out var strokePen))
            {
                if (pen.IsHairline)
                {
                    TryAddDeviceHairlinePathStrokePrimitive(strokePath, transform, id, zIndex, strokePen);
                }
                else
                {
                    TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, strokePen, localThickness);
                }
            }

            return;
        }

        if (pen.IsHairline)
        {
            TryAddDeviceHairlinePathStrokePrimitive(path, transform, id, zIndex, pen);
        }
        else
        {
            TryAddPathStrokePrimitive(path, transform, id, zIndex, pen, localThickness);
        }
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
        if (!Compositor.IsRenderableStroke(command.Pen))
        {
            return;
        }
        var pen = command.Pen!;
        if (!Compositor.TryResolveLocalStrokeThickness(command, out var localThickness))
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
            if (TryGetDashedStrokePath(command, path, pen, localThickness, out var strokePath, out var strokePen))
            {
                if (pen.IsHairline)
                {
                    TryAddDeviceHairlinePathStrokePrimitive(strokePath, transform, id, zIndex, strokePen);
                }
                else
                {
                    TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, strokePen, localThickness);
                }
            }

            return;
        }

        if (pen.IsHairline)
        {
            TryAddDeviceHairlinePathStrokePrimitive(path, transform, id, zIndex, pen);
        }
        else
        {
            TryAddPathStrokePrimitive(path, transform, id, zIndex, pen, localThickness);
        }
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
        if (!IsFiniteInvertibleAffine2D(seriesTransform))
        {
            return;
        }

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

            TryAddPathStrokePrimitive(
                path,
                seriesTransform,
                id,
                zIndex + chunkIndex * 0.0001f,
                pen.Thickness);
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
        if (!IsFiniteInvertibleAffine2D(seriesTransform))
        {
            return;
        }

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
        return command.PolylinePoints is { Length: > 0 } points
            ? points
            : provider != null && command.PointBufferCount > 0
                ? provider.GetPoints(command.PointBufferOffset, command.PointBufferCount)
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
                compiledClip.FillRule,
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

    private static bool IsFiniteInvertibleAffine2D(Matrix4x4 transform)
    {
        const float epsilon = 0.0001f;
        if (!float.IsFinite(transform.M11) ||
            !float.IsFinite(transform.M12) ||
            !float.IsFinite(transform.M21) ||
            !float.IsFinite(transform.M22) ||
            !float.IsFinite(transform.M41) ||
            !float.IsFinite(transform.M42) ||
            MathF.Abs(transform.M13) > epsilon ||
            MathF.Abs(transform.M14) > epsilon ||
            MathF.Abs(transform.M23) > epsilon ||
            MathF.Abs(transform.M24) > epsilon ||
            MathF.Abs(transform.M31) > epsilon ||
            MathF.Abs(transform.M32) > epsilon ||
            MathF.Abs(transform.M34) > epsilon ||
            MathF.Abs(transform.M44 - 1f) > epsilon)
        {
            return false;
        }

        var determinant = transform.M11 * transform.M22 -
            transform.M12 * transform.M21;
        return float.IsFinite(determinant) && determinant != 0f;
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
        public static ClipState Empty { get; } = new(
            Vector2.One,
            Vector2.Zero);

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
