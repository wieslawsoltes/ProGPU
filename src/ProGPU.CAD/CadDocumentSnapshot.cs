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
    MText = 13,
    ShxMText = 14,
    Hatch = 15,
    ShxShape = 16,
    Point = 17,
    Ray = 18,
    XLine = 19,
    Mesh3D = 20,
}

public readonly record struct CadLayerSnapshot(
    string Name,
    bool IsVisible,
    bool IsPlottable);

public readonly record struct CadColor32(
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha = byte.MaxValue);

public readonly record struct CadStrokeStyle(
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha,
    double LineWeightMillimeters,
    bool IsHairline,
    string LineTypeName,
    double LineTypeScale,
    int LineTypePatternIndex);

public enum CadLineTypePatternKind : byte
{
    Continuous = 0,
    Simple = 1,
    Complex = 2,
    UnsupportedAlignment = 3,
}

public enum CadLineTypeElementKind : byte
{
    Stroke = 0,
    TrueTypeText = 1,
    ShxText = 2,
    ShxShape = 3,
    UnresolvedComplex = 4,
}

public enum CadLineTypeRotationMode : byte
{
    Relative = 0,
    Absolute = 1,
}

/// <summary>
/// An immutable linetype definition whose element lengths address the shared
/// <see cref="CadDocumentSnapshot.LineTypeElements"/> stream.
/// </summary>
public readonly record struct CadLineTypePattern(
    string Name,
    char Alignment,
    int ElementOffset,
    int ElementCount,
    double PatternLength,
    CadLineTypePatternKind Kind);

/// <summary>
/// One signed CAD linetype descriptor. Positive stroke values draw, negative
/// stroke values advance without drawing, and zero stroke values draw a point.
/// Complex descriptors retain their persisted flags and immutable resource index.
/// </summary>
public readonly record struct CadLineTypeElement(
    double Length,
    byte ComplexTypeFlags,
    CadLineTypeElementKind Kind = CadLineTypeElementKind.Stroke,
    CadLineTypeRotationMode RotationMode = CadLineTypeRotationMode.Relative,
    double Rotation = 0.0,
    double OffsetX = 0.0,
    double OffsetY = 0.0,
    int ResourceIndex = -1);

/// <summary>
/// One immutable, definition-shared complex-linetype text payload. TrueType
/// payloads address the snapshot glyph/run streams; SHX payloads address the
/// SHX glyph-instance stream. Axis scales are expressed in unscaled linetype
/// units and are multiplied by the effective entity linetype scale at replay.
/// </summary>
public readonly record struct CadLineTypeTextResource(
    CadLineTypeElementKind Kind,
    int GlyphOffset,
    int GlyphCount,
    int RunOffset,
    int RunCount,
    double XScale,
    double YScale,
    double ObliqueAngle,
    bool IsBackward,
    bool IsUpsideDown,
    bool IsSubstitution = false);

public readonly record struct CadLineTypeShapeResource(
    CadShxGlyph Glyph,
    double Scale,
    bool IsSubstitution = false);

public readonly record struct CadEntityHeader(
    ulong Handle,
    CadEntityKind Kind,
    int LayerIndex,
    int StyleIndex,
    int PrimitiveIndex,
    CadBounds3D Bounds);

public readonly record struct CadLinePrimitive(CadPoint3D Start, CadPoint3D End);

/// <summary>
/// One POINT location plus the drawing-wide regenerated marker contract captured
/// at snapshot time. Marker axes are the affine image of the entity's rotated
/// OCS axes; consumers resolve PDSIZE against their own viewport or page.
/// </summary>
public readonly record struct CadPointPrimitive(
    CadPoint3D Position,
    CadPoint3D MarkerXAxis,
    CadPoint3D MarkerYAxis,
    short DisplayMode,
    double DisplaySize);

/// <summary>
/// One unbounded WCS construction line. Rays use parameters [0,+infinity) and
/// XLINEs use (-infinity,+infinity); Direction is always unit length.
/// </summary>
public readonly record struct CadConstructionLinePrimitive(
    CadPoint3D BasePoint,
    CadPoint3D Direction);

/// <summary>One flat-shaded triangle vertex in rebased-independent WCS.</summary>
public readonly record struct CadMesh3DVertex(
    CadPoint3D Position,
    CadPoint3D Normal,
    Vector2 TextureCoordinate);

/// <summary>One contiguous material/style range within a retained mesh entity.</summary>
public readonly record struct CadMesh3DDrawRange(
    int LayerIndex,
    int StyleIndex,
    int VertexOffset,
    int VertexCount,
    int IndexOffset,
    int IndexCount);

/// <summary>
/// One semantic MESH, polygon-mesh, or polyface-mesh instance. Its draw ranges
/// reference the snapshot-wide triangle streams and share this exact WCS bound.
/// </summary>
public readonly record struct CadMesh3DPrimitive(
    int DrawRangeOffset,
    int DrawRangeCount,
    CadBounds3D Bounds);

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

/// <summary>
/// One triangle or quadrilateral in perimeter order. SOLID's persisted
/// zig-zag third/fourth corner order is normalized when the snapshot is built;
/// 3DFACE invisible-edge bits still address these consecutive perimeter edges.
/// </summary>
public readonly record struct CadFacePrimitive(
    CadPoint3D First,
    CadPoint3D Second,
    CadPoint3D Third,
    CadPoint3D Fourth,
    byte InvisibleEdgeMask)
{
    /// <summary>
    /// Signed WCS displacement from the retained base contour to its top contour.
    /// Zero identifies a flat SOLID or 3DFACE.
    /// </summary>
    public CadPoint3D Extrusion { get; init; }
}

public readonly record struct CadSplinePrimitive(
    int ControlPointOffset,
    int ControlPointCount,
    int KnotOffset,
    int KnotCount,
    int WeightOffset,
    int WeightCount,
    int Degree,
    bool IsClosed,
    bool IsPeriodic);

public readonly record struct CadPolylineVertex(
    double X,
    double Y,
    double Bulge);

public readonly record struct CadPolylinePrimitive(
    CadPoint3D WorldOrigin,
    CadCoordinateSystem CoordinateSystem,
    int VertexOffset,
    int VertexCount,
    bool IsClosed,
    bool IsLineTypeContinuous);

public readonly record struct CadPolyline3DPrimitive(
    int PointOffset,
    int PointCount,
    bool IsClosed);

/// <summary>One immutable HATCH composed of closed boundary loops.</summary>
public readonly record struct CadHatchPrimitive(
    CadPoint3D WorldOrigin,
    CadCoordinateSystem CoordinateSystem,
    int LoopOffset,
    int LoopCount,
    bool HasCurvedSegments,
    int PatternIndex);

/// <summary>
/// One retained HATCH pattern addressing the shared family stream.
/// </summary>
public readonly record struct CadHatchPattern(
    int FamilyOffset,
    int FamilyCount);

/// <summary>
/// One exact DXF/PAT pattern-line family in the owning HATCH's local OCS plane.
/// Direction is the unit tangent; spacing is positive perpendicular row
/// distance; tangent shift and dash values preserve the authored row grammar.
/// </summary>
public readonly record struct CadHatchPatternFamily(
    double BasePointX,
    double BasePointY,
    double DirectionX,
    double DirectionY,
    double TangentShift,
    double Spacing,
    int DashOffset,
    int DashCount,
    double DashPeriod);

/// <summary>
/// A closed source contour addressing the shared hatch-segment stream.
/// ContributesToFill records the owning HATCH style's immutable island
/// decision without discarding ignored source-loop geometry.
/// </summary>
public readonly record struct CadHatchLoop(
    int SegmentOffset,
    int SegmentCount,
    bool ContributesToFill);

public enum CadHatchSegmentKind : byte
{
    Line = 0,
    EllipticArc = 1,
    QuadraticBezier = 2,
    CubicBezier = 3,
    RationalQuadraticBezier = 4,
    RationalCubicBezier = 5,
}

/// <summary>
/// One double-precision HATCH boundary segment in the owning primitive's local
/// OCS plane. Elliptic arcs use center + cosine-axis*cos(t) + sine-axis*sin(t).
/// Quadratic Beziers use CenterX/CenterY as their control point; cubic Beziers
/// additionally use CosineAxisX/CosineAxisY as their second control point.
/// Rational quadratics use the same quadratic control fields and a canonical
/// positive middle Weight with unit endpoint weights. Rational cubics use the
/// cubic control fields and canonical positive Weight/Weight2 values with unit
/// endpoint weights.
/// </summary>
public readonly record struct CadHatchSegment(
    CadHatchSegmentKind Kind,
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    double CenterX,
    double CenterY,
    double CosineAxisX,
    double CosineAxisY,
    double SineAxisX,
    double SineAxisY,
    double StartParameter,
    double SweepParameter,
    double Weight = 1.0,
    double Weight2 = 1.0);

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

/// <summary>
/// One retained MTEXT entity. Glyphs, colored runs, filled rectangles, and
/// separator strokes address immutable snapshot-wide streams.
/// </summary>
public readonly record struct CadMTextPrimitive(
    CadPoint3D Origin,
    CadPoint3D XAxis,
    CadPoint3D YAxis,
    int GlyphOffset,
    int GlyphCount,
    int RunOffset,
    int RunCount,
    int BackgroundOffset,
    int BackgroundCount,
    int DecorationOffset,
    int DecorationCount,
    int StrokeOffset,
    int StrokeCount,
    int ColumnCount,
    float ContentWidth,
    float ContentHeight);

/// <summary>
/// A contiguous MTEXT glyph range sharing typeface, local font transform, and paint.
/// Positions are already retained in the entity's local drawing coordinates.
/// </summary>
public readonly record struct CadMTextGlyphRun(
    int GlyphOffset,
    int GlyphCount,
    int FontIndex,
    float FontSize,
    float WidthScale,
    float SkewX,
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha);

/// <summary>A filled local MTEXT rectangle used by masks and decorations.</summary>
public readonly record struct CadMTextRectangle(
    float X,
    float Y,
    float Width,
    float Height,
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha);

/// <summary>A local MTEXT separator stroke, primarily for stacked fractions.</summary>
public readonly record struct CadMTextStroke(
    float StartX,
    float StartY,
    float EndX,
    float EndY,
    float Thickness,
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha);

public readonly record struct CadShxTextPrimitive(
    CadPoint3D Origin,
    CadPoint3D XAxis,
    CadPoint3D YAxis,
    int GlyphOffset,
    int GlyphCount,
    int DecorationOffset,
    int DecorationCount);

/// <summary>
/// One retained standard-SHX MTEXT entity. Glyph paths address the shared SHX
/// instance stream while paint/transform runs and MTEXT rectangles remain
/// immutable generation-owned data.
/// </summary>
public readonly record struct CadShxMTextPrimitive(
    CadPoint3D Origin,
    CadPoint3D XAxis,
    CadPoint3D YAxis,
    int GlyphOffset,
    int GlyphCount,
    int RunOffset,
    int RunCount,
    int BackgroundOffset,
    int BackgroundCount,
    int DecorationOffset,
    int DecorationCount,
    int StrokeOffset,
    int StrokeCount,
    int ColumnCount,
    float ContentWidth,
    float ContentHeight);

/// <summary>
/// A contiguous standard-SHX MTEXT glyph range sharing local scale, oblique,
/// and paint. Scale is applied to the cached font-unit path at replay time.
/// </summary>
public readonly record struct CadShxMTextGlyphRun(
    int GlyphOffset,
    int GlyphCount,
    float ScaleX,
    float ScaleY,
    float SkewX,
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha);

public readonly record struct CadShxGlyphInstance(
    CadShxGlyph Glyph,
    float X,
    float Y);

/// <summary>
/// One standalone SHAPE retaining a single interpreted SHX path and its full
/// WCS affine placement. Size, relative X scale, oblique, OCS rotation, and
/// enclosing block transforms are baked into the two axes exactly once.
/// </summary>
public readonly record struct CadShxShapePrimitive(
    CadPoint3D Origin,
    CadPoint3D XAxis,
    CadPoint3D YAxis,
    CadShxGlyph Glyph);

/// <summary>
/// A stroked SHX decoration segment in the owning text primitive's local
/// affine coordinate system.
/// </summary>
public readonly record struct CadShxDecorationSegment(
    float StartX,
    float StartY,
    float EndX,
    float EndY);

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
    private readonly CadLineTypePattern[] _lineTypePatterns;
    private readonly CadLineTypeElement[] _lineTypeElements;
    private readonly CadLineTypeTextResource[] _lineTypeTextResources;
    private readonly CadLineTypeShapeResource[] _lineTypeShapeResources;
    private readonly CadEntityHeader[] _entities;
    private readonly CadLinePrimitive[] _lines;
    private readonly CadPointPrimitive[] _points;
    private readonly CadConstructionLinePrimitive[] _constructionLines;
    private readonly CadMesh3DPrimitive[] _meshes3D;
    private readonly CadMesh3DDrawRange[] _mesh3DDrawRanges;
    private readonly CadMesh3DVertex[] _mesh3DVertices;
    private readonly uint[] _mesh3DIndices;
    private readonly CadCirclePrimitive[] _circles;
    private readonly CadArcPrimitive[] _arcs;
    private readonly CadEllipsePrimitive[] _ellipses;
    private readonly CadFacePrimitive[] _faces;
    private readonly CadSplinePrimitive[] _splines;
    private readonly CadPolylinePrimitive[] _polylines;
    private readonly CadPolyline3DPrimitive[] _polylines3D;
    private readonly CadHatchPrimitive[] _hatches;
    private readonly CadHatchPattern[] _hatchPatterns;
    private readonly CadHatchPatternFamily[] _hatchPatternFamilies;
    private readonly double[] _hatchPatternDashes;
    private readonly CadHatchLoop[] _hatchLoops;
    private readonly CadHatchSegment[] _hatchSegments;
    private readonly CadTextPrimitive[] _texts;
    private readonly CadTextGlyphRun[] _textGlyphRuns;
    private readonly CadTextDecoration[] _textDecorations;
    private readonly CadMTextPrimitive[] _mtexts;
    private readonly CadMTextGlyphRun[] _mtextGlyphRuns;
    private readonly CadMTextRectangle[] _mtextBackgrounds;
    private readonly CadMTextRectangle[] _mtextDecorations;
    private readonly CadMTextStroke[] _mtextStrokes;
    private readonly ushort[] _textGlyphIndices;
    private readonly Vector2[] _textGlyphPositions;
    private readonly TtfFont[] _textFonts;
    private readonly CadShxTextPrimitive[] _shxTexts;
    private readonly CadShxMTextPrimitive[] _shxMTexts;
    private readonly CadShxMTextGlyphRun[] _shxMTextGlyphRuns;
    private readonly CadShxGlyphInstance[] _shxGlyphInstances;
    private readonly CadShxShapePrimitive[] _shxShapes;
    private readonly CadShxDecorationSegment[] _shxDecorationSegments;
    private readonly CadPolylineVertex[] _polylineVertices;
    private readonly CadPoint3D[] _polyline3DPoints;
    private readonly CadPoint3D[] _splineControlPoints;
    private readonly double[] _splineKnots;
    private readonly double[] _splineWeights;
    private readonly CadDiagnostic[] _diagnostics;

    public ulong ContentGeneration { get; }

    /// <summary>Gets the ordering purpose captured by this immutable snapshot.</summary>
    public CadDrawOrderPurpose DrawOrderPurpose { get; }

    /// <summary>
    /// Gets whether any visited model/block collection contained active sparse
    /// SORTENTSTABLE overrides.
    /// </summary>
    public bool HasDrawOrderOverrides { get; }

    /// <summary>
    /// Gets whether this snapshot can be consumed by the print planner without
    /// changing persisted object order.
    /// </summary>
    public bool IsPlotOrderCompatible { get; }
    public double GlobalLineTypeScale { get; }
    public CadBounds3D Bounds { get; }
    public CadPoint3D RebaseOrigin { get; }
    public CadSnapshotStatistics Statistics { get; }
    public CadSpatialIndex SpatialIndex { get; }

    public ReadOnlyMemory<CadLayerSnapshot> Layers => _layers;
    public ReadOnlyMemory<CadStrokeStyle> Styles => _styles;
    public ReadOnlyMemory<CadLineTypePattern> LineTypePatterns => _lineTypePatterns;
    public ReadOnlyMemory<CadLineTypeElement> LineTypeElements => _lineTypeElements;
    public ReadOnlyMemory<CadLineTypeTextResource> LineTypeTextResources => _lineTypeTextResources;
    public ReadOnlyMemory<CadLineTypeShapeResource> LineTypeShapeResources => _lineTypeShapeResources;
    public ReadOnlyMemory<CadEntityHeader> Entities => _entities;
    public ReadOnlyMemory<CadLinePrimitive> Lines => _lines;
    public ReadOnlyMemory<CadPointPrimitive> Points => _points;
    public ReadOnlyMemory<CadConstructionLinePrimitive> ConstructionLines => _constructionLines;
    public ReadOnlyMemory<CadMesh3DPrimitive> Meshes3D => _meshes3D;
    public ReadOnlyMemory<CadMesh3DDrawRange> Mesh3DDrawRanges => _mesh3DDrawRanges;
    public ReadOnlyMemory<CadMesh3DVertex> Mesh3DVertices => _mesh3DVertices;
    public ReadOnlyMemory<uint> Mesh3DIndices => _mesh3DIndices;
    public ReadOnlyMemory<CadCirclePrimitive> Circles => _circles;
    public ReadOnlyMemory<CadArcPrimitive> Arcs => _arcs;
    public ReadOnlyMemory<CadEllipsePrimitive> Ellipses => _ellipses;
    public ReadOnlyMemory<CadFacePrimitive> Faces => _faces;
    public ReadOnlyMemory<CadSplinePrimitive> Splines => _splines;
    public ReadOnlyMemory<CadPolylinePrimitive> Polylines => _polylines;
    public ReadOnlyMemory<CadPolyline3DPrimitive> Polylines3D => _polylines3D;
    public ReadOnlyMemory<CadHatchPrimitive> Hatches => _hatches;
    public ReadOnlyMemory<CadHatchPattern> HatchPatterns => _hatchPatterns;
    public ReadOnlyMemory<CadHatchPatternFamily> HatchPatternFamilies => _hatchPatternFamilies;
    public ReadOnlyMemory<double> HatchPatternDashes => _hatchPatternDashes;
    public ReadOnlyMemory<CadHatchLoop> HatchLoops => _hatchLoops;
    public ReadOnlyMemory<CadHatchSegment> HatchSegments => _hatchSegments;
    public ReadOnlyMemory<CadTextPrimitive> Texts => _texts;
    public ReadOnlyMemory<CadTextGlyphRun> TextGlyphRuns => _textGlyphRuns;
    public ReadOnlyMemory<CadTextDecoration> TextDecorations => _textDecorations;
    public ReadOnlyMemory<CadMTextPrimitive> MTexts => _mtexts;
    public ReadOnlyMemory<CadMTextGlyphRun> MTextGlyphRuns => _mtextGlyphRuns;
    public ReadOnlyMemory<CadMTextRectangle> MTextBackgrounds => _mtextBackgrounds;
    public ReadOnlyMemory<CadMTextRectangle> MTextDecorations => _mtextDecorations;
    public ReadOnlyMemory<CadMTextStroke> MTextStrokes => _mtextStrokes;
    public ReadOnlyMemory<ushort> TextGlyphIndices => _textGlyphIndices;
    public ReadOnlyMemory<Vector2> TextGlyphPositions => _textGlyphPositions;
    public ReadOnlyMemory<TtfFont> TextFonts => _textFonts;
    public ReadOnlyMemory<CadShxTextPrimitive> ShxTexts => _shxTexts;
    public ReadOnlyMemory<CadShxMTextPrimitive> ShxMTexts => _shxMTexts;
    public ReadOnlyMemory<CadShxMTextGlyphRun> ShxMTextGlyphRuns => _shxMTextGlyphRuns;
    public ReadOnlyMemory<CadShxGlyphInstance> ShxGlyphInstances => _shxGlyphInstances;
    public ReadOnlyMemory<CadShxShapePrimitive> ShxShapes => _shxShapes;
    public ReadOnlyMemory<CadShxDecorationSegment> ShxDecorationSegments => _shxDecorationSegments;
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
        CadDrawOrderPurpose drawOrderPurpose,
        bool hasDrawOrderOverrides,
        bool isPlotOrderCompatible,
        double globalLineTypeScale,
        CadBounds3D bounds,
        CadSnapshotStatistics statistics,
        CadLayerSnapshot[] layers,
        CadStrokeStyle[] styles,
        CadLineTypePattern[] lineTypePatterns,
        CadLineTypeElement[] lineTypeElements,
        CadLineTypeTextResource[] lineTypeTextResources,
        CadLineTypeShapeResource[] lineTypeShapeResources,
        CadEntityHeader[] entities,
        CadLinePrimitive[] lines,
        CadPointPrimitive[] points,
        CadConstructionLinePrimitive[] constructionLines,
        CadMesh3DPrimitive[] meshes3D,
        CadMesh3DDrawRange[] mesh3DDrawRanges,
        CadMesh3DVertex[] mesh3DVertices,
        uint[] mesh3DIndices,
        CadCirclePrimitive[] circles,
        CadArcPrimitive[] arcs,
        CadEllipsePrimitive[] ellipses,
        CadFacePrimitive[] faces,
        CadSplinePrimitive[] splines,
        CadPolylinePrimitive[] polylines,
        CadPolyline3DPrimitive[] polylines3D,
        CadHatchPrimitive[] hatches,
        CadHatchPattern[] hatchPatterns,
        CadHatchPatternFamily[] hatchPatternFamilies,
        double[] hatchPatternDashes,
        CadHatchLoop[] hatchLoops,
        CadHatchSegment[] hatchSegments,
        CadTextPrimitive[] texts,
        CadTextGlyphRun[] textGlyphRuns,
        CadTextDecoration[] textDecorations,
        CadMTextPrimitive[] mtexts,
        CadMTextGlyphRun[] mtextGlyphRuns,
        CadMTextRectangle[] mtextBackgrounds,
        CadMTextRectangle[] mtextDecorations,
        CadMTextStroke[] mtextStrokes,
        ushort[] textGlyphIndices,
        Vector2[] textGlyphPositions,
        TtfFont[] textFonts,
        CadShxTextPrimitive[] shxTexts,
        CadShxMTextPrimitive[] shxMTexts,
        CadShxMTextGlyphRun[] shxMTextGlyphRuns,
        CadShxGlyphInstance[] shxGlyphInstances,
        CadShxShapePrimitive[] shxShapes,
        CadShxDecorationSegment[] shxDecorationSegments,
        CadPolylineVertex[] polylineVertices,
        CadPoint3D[] polyline3DPoints,
        CadPoint3D[] splineControlPoints,
        double[] splineKnots,
        double[] splineWeights,
        CadDiagnostic[] diagnostics)
    {
        ContentGeneration = contentGeneration;
        DrawOrderPurpose = drawOrderPurpose;
        HasDrawOrderOverrides = hasDrawOrderOverrides;
        IsPlotOrderCompatible = isPlotOrderCompatible;
        GlobalLineTypeScale = globalLineTypeScale;
        Bounds = bounds;
        RebaseOrigin = bounds.Center;
        Statistics = statistics;
        _layers = layers;
        _styles = styles;
        _lineTypePatterns = lineTypePatterns;
        _lineTypeElements = lineTypeElements;
        _lineTypeTextResources = lineTypeTextResources;
        _lineTypeShapeResources = lineTypeShapeResources;
        _entities = entities;
        _lines = lines;
        _points = points;
        _constructionLines = constructionLines;
        _meshes3D = meshes3D;
        _mesh3DDrawRanges = mesh3DDrawRanges;
        _mesh3DVertices = mesh3DVertices;
        _mesh3DIndices = mesh3DIndices;
        _circles = circles;
        _arcs = arcs;
        _ellipses = ellipses;
        _faces = faces;
        _splines = splines;
        _polylines = polylines;
        _polylines3D = polylines3D;
        _hatches = hatches;
        _hatchPatterns = hatchPatterns;
        _hatchPatternFamilies = hatchPatternFamilies;
        _hatchPatternDashes = hatchPatternDashes;
        _hatchLoops = hatchLoops;
        _hatchSegments = hatchSegments;
        _texts = texts;
        _textGlyphRuns = textGlyphRuns;
        _textDecorations = textDecorations;
        _mtexts = mtexts;
        _mtextGlyphRuns = mtextGlyphRuns;
        _mtextBackgrounds = mtextBackgrounds;
        _mtextDecorations = mtextDecorations;
        _mtextStrokes = mtextStrokes;
        _textGlyphIndices = textGlyphIndices;
        _textGlyphPositions = textGlyphPositions;
        _textFonts = textFonts;
        _shxTexts = shxTexts;
        _shxMTexts = shxMTexts;
        _shxMTextGlyphRuns = shxMTextGlyphRuns;
        _shxGlyphInstances = shxGlyphInstances;
        _shxShapes = shxShapes;
        _shxDecorationSegments = shxDecorationSegments;
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

        int[] indices = Enumerable.Range(0, entities.Length)
            .Where(index => !entities[index].Bounds.IsEmpty)
            .ToArray();
        CadBounds3D[] entityBounds = entities.Select(entity => entity.Bounds).ToArray();
        if (indices.Length == 0)
        {
            return new CadSpatialIndex(
                Array.Empty<Node>(),
                indices,
                entityBounds);
        }
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
