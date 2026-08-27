using System.Numerics;
using ProGPU.Text;

namespace ProGPU.CAD;

public enum CadEntityKind : byte
{
    Line = 1,
    Circle = 2,
    Arc = 3,
    Spline = 4,
    LightweightPolyline = 5,
    Ellipse = 6,
    Solid = 7,
    Face3D = 8,
    Polyline2D = 9,
    Polyline3D = 10,
    Text = 11,
    ShxText = 12,
}

public readonly record struct CadLayerSnapshot(
    string Name,
    bool IsVisible,
    bool IsPlottable);

public readonly record struct CadStrokeStyle(
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha,
    double LineWeightMillimeters,
    bool IsHairline,
    string LineTypeName,
    double LineTypeScale);

public readonly record struct CadEntityHeader(
    ulong Handle,
    CadEntityKind Kind,
    int LayerIndex,
    int StyleIndex,
    int PrimitiveIndex,
    CadBounds3D Bounds);

public readonly record struct CadLinePrimitive(CadPoint3D Start, CadPoint3D End);

public readonly record struct CadCirclePrimitive(
    CadPoint3D Center,
    CadCoordinateSystem CoordinateSystem,
    double Radius);

public readonly record struct CadArcPrimitive(
    CadPoint3D Center,
    CadCoordinateSystem CoordinateSystem,
    double Radius,
    double StartAngle,
    double SweepAngle)
{
    public CadPoint3D StartPoint =>
        CoordinateSystem.PointOnCircle(Center, Radius, StartAngle);

    public CadPoint3D EndPoint =>
        CoordinateSystem.PointOnCircle(Center, Radius, StartAngle + SweepAngle);
}

public readonly record struct CadEllipsePrimitive(
    CadPoint3D Center,
    CadPoint3D MajorAxis,
    CadPoint3D MinorAxis,
    double StartParameter,
    double SweepParameter)
{
    public CadPoint3D PointAt(double parameter) =>
        Center + (MajorAxis * Math.Cos(parameter)) + (MinorAxis * Math.Sin(parameter));

    public CadPoint3D StartPoint => PointAt(StartParameter);

    public CadPoint3D EndPoint => PointAt(StartParameter + SweepParameter);
}

public readonly record struct CadFacePrimitive(
    CadPoint3D First,
    CadPoint3D Second,
    CadPoint3D Third,
    CadPoint3D Fourth,
    byte InvisibleEdgeMask);

public readonly record struct CadSplinePrimitive(
    int ControlPointOffset,
    int ControlPointCount,
    int KnotOffset,
    int KnotCount,
    int WeightOffset,
    int WeightCount,
    int Degree,
    bool IsClosed);

public readonly record struct CadPolylineVertex(
    double X,
    double Y,
    double Bulge);

public readonly record struct CadPolylinePrimitive(
    CadPoint3D WorldOrigin,
    CadCoordinateSystem CoordinateSystem,
    int VertexOffset,
    int VertexCount,
    bool IsClosed);

public readonly record struct CadPolyline3DPrimitive(
    int PointOffset,
    int PointCount,
    bool IsClosed);

public readonly record struct CadTextPrimitive(
    CadPoint3D Origin,
    CadPoint3D XAxis,
    CadPoint3D YAxis,
    int GlyphOffset,
    int GlyphCount,
    int RunOffset,
    int RunCount,
    int DecorationOffset,
    int DecorationCount);

public readonly record struct CadTextGlyphRun(
    int GlyphOffset,
    int GlyphCount,
    int FontIndex);

/// <summary>
/// A normalized, filled decoration rectangle in the owning text primitive's
/// local affine coordinate system.
/// </summary>
public readonly record struct CadTextDecoration(
    float X,
    float Y,
    float Width,
    float Height);

public readonly record struct CadShxTextPrimitive(
    CadPoint3D Origin,
    CadPoint3D XAxis,
    CadPoint3D YAxis,
    int GlyphOffset,
    int GlyphCount);

public readonly record struct CadShxGlyphInstance(
    CadShxGlyph Glyph,
    float X,
    float Y);

public readonly record struct CadSnapshotStatistics(
    int SourceEntityCount,
    int VisibleEntityCount,
    int ExpandedEntityCount,
    int UnsupportedEntityCount,
    int InvalidEntityCount);

/// <summary>
/// An immutable, generation-tagged, double-precision rendering snapshot.
/// </summary>
/// <remarks>
/// All planar entity coordinates are normalized to WCS during construction. Strings
/// and styles are interned into indexed tables; hot entity records contain only fixed
/// fields and table indices. Snapshot creation is O(E log E) for E visible primitives
/// after bounded block expansion because it also builds the balanced spatial index.
/// Stable traversal and visibility queries do not touch the mutable ACadSharp graph.
/// </remarks>
public sealed class CadDocumentSnapshot
{
    private readonly CadLayerSnapshot[] _layers;
    private readonly CadStrokeStyle[] _styles;
    private readonly CadEntityHeader[] _entities;
    private readonly CadLinePrimitive[] _lines;
    private readonly CadCirclePrimitive[] _circles;
    private readonly CadArcPrimitive[] _arcs;
    private readonly CadEllipsePrimitive[] _ellipses;
    private readonly CadFacePrimitive[] _faces;
    private readonly CadSplinePrimitive[] _splines;
    private readonly CadPolylinePrimitive[] _polylines;
    private readonly CadPolyline3DPrimitive[] _polylines3D;
    private readonly CadTextPrimitive[] _texts;
    private readonly CadTextGlyphRun[] _textGlyphRuns;
    private readonly CadTextDecoration[] _textDecorations;
    private readonly ushort[] _textGlyphIndices;
    private readonly Vector2[] _textGlyphPositions;
    private readonly TtfFont[] _textFonts;
    private readonly CadShxTextPrimitive[] _shxTexts;
    private readonly CadShxGlyphInstance[] _shxGlyphInstances;
    private readonly CadPolylineVertex[] _polylineVertices;
    private readonly CadPoint3D[] _polyline3DPoints;
    private readonly CadPoint3D[] _splineControlPoints;
    private readonly double[] _splineKnots;
    private readonly double[] _splineWeights;
    private readonly CadDiagnostic[] _diagnostics;

    public ulong ContentGeneration { get; }
    public CadBounds3D Bounds { get; }
    public CadPoint3D RebaseOrigin { get; }
    public CadSnapshotStatistics Statistics { get; }
    public CadSpatialIndex SpatialIndex { get; }

    public ReadOnlyMemory<CadLayerSnapshot> Layers => _layers;
    public ReadOnlyMemory<CadStrokeStyle> Styles => _styles;
    public ReadOnlyMemory<CadEntityHeader> Entities => _entities;
    public ReadOnlyMemory<CadLinePrimitive> Lines => _lines;
    public ReadOnlyMemory<CadCirclePrimitive> Circles => _circles;
    public ReadOnlyMemory<CadArcPrimitive> Arcs => _arcs;
    public ReadOnlyMemory<CadEllipsePrimitive> Ellipses => _ellipses;
    public ReadOnlyMemory<CadFacePrimitive> Faces => _faces;
    public ReadOnlyMemory<CadSplinePrimitive> Splines => _splines;
    public ReadOnlyMemory<CadPolylinePrimitive> Polylines => _polylines;
    public ReadOnlyMemory<CadPolyline3DPrimitive> Polylines3D => _polylines3D;
    public ReadOnlyMemory<CadTextPrimitive> Texts => _texts;
    public ReadOnlyMemory<CadTextGlyphRun> TextGlyphRuns => _textGlyphRuns;
    public ReadOnlyMemory<CadTextDecoration> TextDecorations => _textDecorations;
    public ReadOnlyMemory<ushort> TextGlyphIndices => _textGlyphIndices;
    public ReadOnlyMemory<Vector2> TextGlyphPositions => _textGlyphPositions;
    public ReadOnlyMemory<TtfFont> TextFonts => _textFonts;
    public ReadOnlyMemory<CadShxTextPrimitive> ShxTexts => _shxTexts;
    public ReadOnlyMemory<CadShxGlyphInstance> ShxGlyphInstances => _shxGlyphInstances;
    public ReadOnlyMemory<CadPolylineVertex> PolylineVertices => _polylineVertices;
    public ReadOnlyMemory<CadPoint3D> Polyline3DPoints => _polyline3DPoints;
    public ReadOnlyMemory<CadPoint3D> SplineControlPoints => _splineControlPoints;
    public ReadOnlyMemory<double> SplineKnots => _splineKnots;
    public ReadOnlyMemory<double> SplineWeights => _splineWeights;
    public ReadOnlyMemory<CadDiagnostic> Diagnostics => _diagnostics;

    internal ushort[] TextGlyphIndexArray => _textGlyphIndices;
    internal Vector2[] TextGlyphPositionArray => _textGlyphPositions;

    internal CadDocumentSnapshot(
        ulong contentGeneration,
        CadBounds3D bounds,
        CadSnapshotStatistics statistics,
        CadLayerSnapshot[] layers,
        CadStrokeStyle[] styles,
        CadEntityHeader[] entities,
        CadLinePrimitive[] lines,
        CadCirclePrimitive[] circles,
        CadArcPrimitive[] arcs,
        CadEllipsePrimitive[] ellipses,
        CadFacePrimitive[] faces,
        CadSplinePrimitive[] splines,
        CadPolylinePrimitive[] polylines,
        CadPolyline3DPrimitive[] polylines3D,
        CadTextPrimitive[] texts,
        CadTextGlyphRun[] textGlyphRuns,
        CadTextDecoration[] textDecorations,
        ushort[] textGlyphIndices,
        Vector2[] textGlyphPositions,
        TtfFont[] textFonts,
        CadShxTextPrimitive[] shxTexts,
        CadShxGlyphInstance[] shxGlyphInstances,
        CadPolylineVertex[] polylineVertices,
        CadPoint3D[] polyline3DPoints,
        CadPoint3D[] splineControlPoints,
        double[] splineKnots,
        double[] splineWeights,
        CadDiagnostic[] diagnostics)
    {
        ContentGeneration = contentGeneration;
        Bounds = bounds;
        RebaseOrigin = bounds.Center;
        Statistics = statistics;
        _layers = layers;
        _styles = styles;
        _entities = entities;
        _lines = lines;
        _circles = circles;
        _arcs = arcs;
        _ellipses = ellipses;
        _faces = faces;
        _splines = splines;
        _polylines = polylines;
        _polylines3D = polylines3D;
        _texts = texts;
        _textGlyphRuns = textGlyphRuns;
        _textDecorations = textDecorations;
        _textGlyphIndices = textGlyphIndices;
        _textGlyphPositions = textGlyphPositions;
        _textFonts = textFonts;
        _shxTexts = shxTexts;
        _shxGlyphInstances = shxGlyphInstances;
        _polylineVertices = polylineVertices;
        _polyline3DPoints = polyline3DPoints;
        _splineControlPoints = splineControlPoints;
        _splineKnots = splineKnots;
        _splineWeights = splineWeights;
        _diagnostics = diagnostics;
        SpatialIndex = CadSpatialIndex.Build(entities);
    }
}

public readonly record struct CadSpatialQueryResult(int WrittenCount, int TotalCount)
{
    public bool IsTruncated => WrittenCount != TotalCount;
}

/// <summary>A balanced immutable AABB hierarchy over snapshot entities.</summary>
public sealed class CadSpatialIndex
{
    private const int LeafCapacity = 8;

    private readonly Node[] _nodes;
    private readonly int[] _entityIndices;
    private readonly CadBounds3D[] _entityBounds;

    private CadSpatialIndex(
        Node[] nodes,
        int[] entityIndices,
        CadBounds3D[] entityBounds)
    {
        _nodes = nodes;
        _entityIndices = entityIndices;
        _entityBounds = entityBounds;
    }

    public int EntityCount => _entityIndices.Length;
    public int NodeCount => _nodes.Length;

    /// <summary>
    /// Returns source-order entity indices intersecting <paramref name="bounds"/>.
    /// Query work is O(log N + K) on typical spatial data and O(N + K) worst-case,
    /// with no managed allocation. Results are deterministic but not source ordered.
    /// </summary>
    public CadSpatialQueryResult Query(CadBounds3D bounds, Span<int> destination)
    {
        if (bounds.IsEmpty || _nodes.Length == 0)
        {
            return default;
        }

        Span<int> pending = stackalloc int[64];
        int pendingCount = 1;
        pending[0] = 0;
        int written = 0;
        int total = 0;

        while (pendingCount > 0)
        {
            Node node = _nodes[pending[--pendingCount]];
            if (!node.Bounds.Intersects(bounds))
            {
                continue;
            }

            if (node.Count > 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    int entityIndex = _entityIndices[i];
                    if (!_entityBounds[entityIndex].Intersects(bounds))
                    {
                        continue;
                    }

                    if (written < destination.Length)
                    {
                        destination[written++] = entityIndex;
                    }

                    total++;
                }

                continue;
            }

            pending[pendingCount++] = node.Left;
            pending[pendingCount++] = node.Right;
        }

        return new CadSpatialQueryResult(written, total);
    }

    internal static CadSpatialIndex Build(CadEntityHeader[] entities)
    {
        if (entities.Length == 0)
        {
            return new CadSpatialIndex(
                Array.Empty<Node>(),
                Array.Empty<int>(),
                Array.Empty<CadBounds3D>());
        }

        int[] indices = Enumerable.Range(0, entities.Length).ToArray();
        CadBounds3D[] entityBounds = entities.Select(entity => entity.Bounds).ToArray();
        var nodes = new List<Node>(checked(entities.Length * 2));
        BuildNode(nodes, indices, entities, 0, indices.Length);
        return new CadSpatialIndex(nodes.ToArray(), indices, entityBounds);
    }

    private static int BuildNode(
        List<Node> nodes,
        int[] indices,
        CadEntityHeader[] entities,
        int start,
        int count)
    {
        CadBounds3D bounds = CadBounds3D.Empty;
        CadBounds3D centroidBounds = CadBounds3D.Empty;
        for (int i = start; i < start + count; i++)
        {
            CadBounds3D entityBounds = entities[indices[i]].Bounds;
            bounds = bounds.Union(entityBounds);
            centroidBounds = centroidBounds.Include(entityBounds.Center);
        }

        int nodeIndex = nodes.Count;
        nodes.Add(default);
        if (count <= LeafCapacity)
        {
            nodes[nodeIndex] = new Node(bounds, start, count, -1, -1);
            return nodeIndex;
        }

        CadPoint3D extent = centroidBounds.Max - centroidBounds.Min;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(indices, start, count, new CentroidComparer(entities, axis));
        int leftCount = count / 2;
        int left = BuildNode(nodes, indices, entities, start, leftCount);
        int right = BuildNode(nodes, indices, entities, start + leftCount, count - leftCount);
        nodes[nodeIndex] = new Node(bounds, 0, 0, left, right);
        return nodeIndex;
    }

    private readonly record struct Node(
        CadBounds3D Bounds,
        int Start,
        int Count,
        int Left,
        int Right);

    private sealed class CentroidComparer : IComparer<int>
    {
        private readonly CadEntityHeader[] _entities;
        private readonly int _axis;

        public CentroidComparer(CadEntityHeader[] entities, int axis)
        {
            _entities = entities;
            _axis = axis;
        }

        public int Compare(int left, int right)
        {
            CadPoint3D l = _entities[left].Bounds.Center;
            CadPoint3D r = _entities[right].Bounds.Center;
            double lv = _axis == 0 ? l.X : _axis == 1 ? l.Y : l.Z;
            double rv = _axis == 0 ? r.X : _axis == 1 ? r.Y : r.Z;
            int comparison = lv.CompareTo(rv);
            return comparison != 0 ? comparison : left.CompareTo(right);
        }
    }
}
