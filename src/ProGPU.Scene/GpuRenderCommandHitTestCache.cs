using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Scene;

public sealed class GpuRenderCommandHitTestCacheBuilder
{
    private const float OpacityEpsilon = 0.0001f;

    private readonly List<GpuHitTestPrimitive> _primitives = new();
    private readonly List<GpuPathSegment> _pathSegments = new();
    private readonly Stack<ClipState> _clipStack = new();
    private readonly Stack<float> _opacityStack = new();
    private float _activeOpacity = 1f;
    private int _nextId;

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

    public void AddCommand(in RenderCommand command, Matrix4x4 activeTransform, int? id = null)
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

        int primitiveId = id ?? (command.HitTestId != 0 ? command.HitTestId : _nextId++);
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
            case RenderCommandType.DrawLine:
                AddLine(command, activeTransform, primitiveId, zIndex);
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
                AddTriangleBounds(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.FillQuad:
                AddQuadBounds(command, activeTransform, primitiveId, zIndex);
                break;
            case RenderCommandType.DrawPolyline:
                AddPolylineBounds(command, activeTransform, primitiveId, zIndex);
                break;
        }
    }

    public GpuHitTestIndex BuildIndex(int maxDepth = 8, int maxPrimitivesPerNode = 32)
    {
        return GpuHitTestIndex.Build(_primitives.ToArray(), _pathSegments.ToArray(), maxDepth, maxPrimitivesPerNode);
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

    private void AddLine(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (command.Pen is not { Thickness: > 0f } pen)
        {
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

    private void AddPath(RenderCommand command, Matrix4x4 activeTransform, int id, float zIndex)
    {
        if (command.Path == null || command.Brush == null && command.Pen == null)
        {
            return;
        }

        Matrix4x4 transform = command.Transform == default
            ? activeTransform
            : command.Transform * activeTransform;

        Pen? pen = command.Pen is { Thickness: > 0f } activePen ? activePen : null;
        if (pen?.HasDashPattern != true)
        {
            if (!TryCompileHitTestPath(command.Path, out var path))
            {
                return;
            }

            if (command.Brush != null)
            {
                AddPathFillPrimitive(path, command.Path.FillRule, transform, id, zIndex);
                zIndex += 0.25f;
            }

            if (pen != null)
            {
                AddPathStrokePrimitive(path, transform, id, zIndex, pen);
            }

            return;
        }

        if (command.Brush != null &&
            TryCompileHitTestPath(command.Path, out var fillPath))
        {
            AddPathFillPrimitive(fillPath, command.Path.FillRule, transform, id, zIndex);
            zIndex += 0.25f;
        }

        if (!Compositor.TryCreateDashedStrokePath(command.Path, pen, out var strokePath))
        {
            return;
        }

        TryAddPathStrokePrimitive(strokePath, transform, id, zIndex, Compositor.CreateUndashedPen(pen));
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
            (records, segments) = PathAtlas.CompilePath(
                path,
                out minX,
                out minY,
                out maxX,
                out maxY);
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

    private void AddTriangleBounds(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        Vector2 min = Vector2.Min(command.Position, Vector2.Min(command.Position2, command.Position3));
        Vector2 max = Vector2.Max(command.Position, Vector2.Max(command.Position2, command.Position3));
        AddPrimitive(GpuHitTestPrimitive.Bounds(id, min, max, transform, zIndex));
    }

    private void AddQuadBounds(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        Vector2 min = Vector2.Min(Vector2.Min(command.Position, command.Position2), Vector2.Min(command.Position3, command.Position4));
        Vector2 max = Vector2.Max(Vector2.Max(command.Position, command.Position2), Vector2.Max(command.Position3, command.Position4));
        AddPrimitive(GpuHitTestPrimitive.Bounds(id, min, max, transform, zIndex));
    }

    private void AddPolylineBounds(RenderCommand command, Matrix4x4 transform, int id, float zIndex)
    {
        if (command.PolylinePoints is not { Length: > 0 } points)
        {
            return;
        }

        Vector2 min = points[0];
        Vector2 max = points[0];
        for (int i = 1; i < points.Length; i++)
        {
            min = Vector2.Min(min, points[i]);
            max = Vector2.Max(max, points[i]);
        }

        if (command.Pen is { Thickness: > 0f } pen)
        {
            var padding = new Vector2(pen.Thickness * 0.5f);
            min -= padding;
            max += padding;
        }

        AddPrimitive(GpuHitTestPrimitive.Bounds(id, min, max, transform, zIndex));
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

        _clipStack.Push(new ClipState(clipMin, clipMax));
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

    private readonly record struct ClipState(Vector2 Min, Vector2 Max)
    {
        public static ClipState Unbounded { get; } = new(
            new Vector2(float.NegativeInfinity, float.NegativeInfinity),
            new Vector2(float.PositiveInfinity, float.PositiveInfinity));

        public bool IsUnbounded =>
            float.IsNegativeInfinity(Min.X) &&
            float.IsNegativeInfinity(Min.Y) &&
            float.IsPositiveInfinity(Max.X) &&
            float.IsPositiveInfinity(Max.Y);
    }
}
