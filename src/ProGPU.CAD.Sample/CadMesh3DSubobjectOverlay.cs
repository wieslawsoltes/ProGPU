using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD.Sample;

/// <summary>
/// Bounded allocation-free projected highlight overlay for retained modern
/// MESH subobjects.
/// </summary>
internal sealed class CadMesh3DSubobjectOverlay : Control
{
    internal const int MaximumSelectionCount = 64;

    private readonly CadMesh3DSubobjectId[] _selected =
        new CadMesh3DSubobjectId[MaximumSelectionCount];
    private readonly Brush _highlightBrush =
        new ThemeResourceBrush("SystemAccentColor");
    private readonly Brush _gripFillBrush =
        new ThemeResourceBrush("CardBackground");
    private readonly Pen _highlightPen;
    private CadRecordedMesh3DScene? _scene;
    private CadMesh3DViewport? _viewport;
    private int _selectedCount;
    private CadMesh3DSubobjectId? _candidate;

    internal CadMesh3DSubobjectOverlay()
    {
        IsHitTestVisible = false;
        _highlightPen = new Pen(_highlightBrush, 3.0f);
    }

    internal void Update(
        CadRecordedMesh3DScene? scene,
        CadMesh3DViewport? viewport,
        ReadOnlySpan<CadMesh3DSubobjectId> selected,
        CadMesh3DSubobjectId? candidate = null)
    {
        if (selected.Length > MaximumSelectionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(selected));
        }
        _scene = scene;
        _viewport = viewport;
        selected.CopyTo(_selected);
        _selectedCount = selected.Length;
        _candidate = candidate;
        Invalidate();
    }

    public override void OnRender(DrawingContext context)
    {
        CadRecordedMesh3DScene? scene = _scene;
        CadMesh3DViewport? viewport = _viewport;
        if (scene is null || viewport is null ||
            Size.X <= 0.0f || Size.Y <= 0.0f)
        {
            return;
        }
        CadMesh3DProjectionCamera camera =
            viewport.Value.CreateProjectionCamera();
        Matrix4x4 viewProjection = camera.CreateViewMatrix() *
            camera.CreateProjectionMatrix(Size.X / Size.Y);
        for (int index = 0; index < _selectedCount; index++)
        {
            DrawSubobject(context, scene, viewProjection, _selected[index]);
        }
        if (_candidate is CadMesh3DSubobjectId candidate &&
            !Contains(candidate))
        {
            DrawSubobject(context, scene, viewProjection, candidate);
        }
    }

    private bool Contains(in CadMesh3DSubobjectId id)
    {
        for (int index = 0; index < _selectedCount; index++)
        {
            if (_selected[index] == id)
            {
                return true;
            }
        }
        return false;
    }

    private void DrawSubobject(
        DrawingContext context,
        CadRecordedMesh3DScene scene,
        in Matrix4x4 viewProjection,
        in CadMesh3DSubobjectId id)
    {
        if (!scene.TryGetSubobjectComponent(id, out CadMesh3DSubobjectComponent? component) ||
            component is null)
        {
            return;
        }
        switch (id.Kind)
        {
            case CadMesh3DSubobjectKind.Vertex:
                if ((uint)id.Index >= (uint)component.VertexPositions.Length ||
                    !TryProject(
                        component.VertexPositions.Span[id.Index],
                        viewProjection,
                        out Vector2 vertexPoint))
                {
                    return;
                }
                context.DrawCircle(
                    _gripFillBrush,
                    _highlightPen,
                    vertexPoint,
                    5.0f);
                break;
            case CadMesh3DSubobjectKind.Edge:
                DrawEdge(context, component, viewProjection, id.Index);
                break;
            case CadMesh3DSubobjectKind.Face:
                if ((uint)id.Index >= (uint)component.Faces.Length)
                {
                    return;
                }
                CadMesh3DSubobjectFace face = component.Faces.Span[id.Index];
                ReadOnlySpan<int> faceEdges = component.FaceEdgeIndices.Span;
                for (int edge = 0; edge < face.EdgeIndexCount; edge++)
                {
                    DrawEdge(
                        context,
                        component,
                        viewProjection,
                        faceEdges[face.EdgeIndexOffset + edge]);
                }
                break;
        }
    }

    private void DrawEdge(
        DrawingContext context,
        CadMesh3DSubobjectComponent component,
        in Matrix4x4 viewProjection,
        int edgeIndex)
    {
        if ((uint)edgeIndex >= (uint)component.Edges.Length)
        {
            return;
        }
        CadMesh3DSubobjectEdge edge = component.Edges.Span[edgeIndex];
        ReadOnlySpan<Vector3> points = component.EdgePoints.Span;
        for (int point = 1; point < edge.PointCount; point++)
        {
            if (TryProject(
                    points[edge.PointOffset + point - 1],
                    viewProjection,
                    out Vector2 first) &&
                TryProject(
                    points[edge.PointOffset + point],
                    viewProjection,
                    out Vector2 second))
            {
                context.DrawLine(_highlightPen, first, second);
            }
        }
    }

    private bool TryProject(
        Vector3 point,
        in Matrix4x4 viewProjection,
        out Vector2 projected)
    {
        Vector4 clip = Vector4.Transform(new Vector4(point, 1.0f), viewProjection);
        if (!float.IsFinite(clip.X) ||
            !float.IsFinite(clip.Y) ||
            !float.IsFinite(clip.Z) ||
            !float.IsFinite(clip.W) ||
            clip.W <= 0.0f || clip.Z < 0.0f || clip.Z > clip.W)
        {
            projected = default;
            return false;
        }
        float inverseW = 1.0f / clip.W;
        projected = new Vector2(
            (clip.X * inverseW + 1.0f) * 0.5f * Size.X,
            (1.0f - clip.Y * inverseW) * 0.5f * Size.Y);
        return true;
    }
}
