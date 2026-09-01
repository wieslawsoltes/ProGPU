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
    Wipeout = 21,
    RasterImage = 22,
    ModelerGeometry = 23,
    MLine = 24,
    Leader = 25,
    MultiLeader = 26,
    Tolerance = 27,
    Viewport = 28,
}

public readonly record struct CadLayerSnapshot(
    string Name,
    bool IsVisible,
    bool IsPlottable)
{
    public bool IsFrozen { get; init; }
}

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
    CadBounds3D Bounds)
{
    /// <summary>
    /// Whether this retained record participates in ordinary drawing and selection.
    /// Hidden VIEWPORT boundary dependencies remain addressable while false.
    /// </summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>Resolved retained 3D material, or -1 for non-surface records.</summary>
    public int MaterialIndex { get; init; } = -1;

}

/// <summary>
/// One non-overlapping top-level INSERT or MINSERT-cell definition span in the
/// flattened entity stream. Attribute references are intentionally excluded
/// because their persisted values and placement are instance-owned.
/// </summary>
public readonly record struct CadPlanBlockInstanceRange(
    int EntityOffset,
    int EntityCount,
    ulong SemanticHandle,
    ulong DefinitionHandle,
    CadAffineTransform3D LocalToWorld);

public readonly record struct CadLinePrimitive(CadPoint3D Start, CadPoint3D End);

/// <summary>
/// One immutable MLINE whose already-authored element cuts address the shared
/// stroke stream. Element styles remain independent, as required by MLINESTYLE.
/// </summary>
public readonly record struct CadMLinePrimitive(
    int ElementPathOffset,
    int ElementPathCount,
    int StrokeOffset,
    int StrokeCount,
    int FillTriangleOffset,
    int FillTriangleCount);

/// <summary>One visible interval of one MLINESTYLE element.</summary>
public readonly record struct CadMLineStroke(
    CadPoint3D Start,
    CadPoint3D End,
    double PathStart,
    double PathEnd);

/// <summary>
/// One complete MLINESTYLE element path. Visible stroke intervals are ordered
/// by PathStart; authored cuts remain gaps in the full PathLength domain.
/// </summary>
public readonly record struct CadMLineElementPath(
    int StrokeOffset,
    int StrokeCount,
    int StyleIndex,
    double PathLength,
    bool IsClosed);

/// <summary>One triangle from a persisted MLINE area-fill interval.</summary>
public readonly record struct CadMLineFillTriangle(
    CadPoint3D First,
    CadPoint3D Second,
    CadPoint3D Third,
    CadColor32 Color);

/// <summary>
/// One classic LEADER path backed by the shared immutable spline streams.
/// Straight leaders use a degree-one spline; spline-fit leaders retain one
/// piecewise cubic curve. A default closed-filled arrow is stored explicitly,
/// while custom arrow blocks are expanded into ordinary snapshot entities.
/// </summary>
public readonly record struct CadLeaderPrimitive(
    int PathSplineIndex,
    CadPoint3D ArrowTip,
    CadPoint3D ArrowFirstBase,
    CadPoint3D ArrowSecondBase,
    bool HasDefaultArrow,
    bool IsSplineFit,
    bool HasAssociatedAnnotation);

/// <summary>
/// One retained MULTILEADER branch or dogleg backed by the shared immutable
/// spline streams. A source entity can produce several primitives with the same
/// semantic handle. Per-branch style overrides are carried by each entity
/// header, while default arrows are explicit and custom arrow blocks expand as
/// ordinary snapshot entities.
/// </summary>
public readonly record struct CadMultiLeaderPrimitive(
    int PathSplineIndex,
    CadPoint3D ArrowTip,
    CadPoint3D ArrowFirstBase,
    CadPoint3D ArrowSecondBase,
    bool HasDefaultArrow,
    bool IsSplineFit,
    bool IsDogleg,
    int LeaderRootIndex,
    int LeaderLineIndex);

/// <summary>
/// One retained geometric-tolerance feature-control frame. Text fragments are
/// retained through the ordinary TrueType/SHX streams with the same semantic
/// handle, while this primitive owns the exact frame strokes.
/// </summary>
public readonly record struct CadTolerancePrimitive(
    int StrokeOffset,
    int StrokeCount,
    int RowCount,
    int CellCount);

public readonly record struct CadToleranceStroke(
    CadPoint3D Start,
    CadPoint3D End);

/// <summary>
/// One paper-space VIEWPORT and its persisted orthographic camera contract.
/// FrozenLayerOffset/Count address <see cref="CadDocumentSnapshot.ViewportFrozenLayers"/>.
/// The viewport rectangle is expressed in paper-space WCS; ViewCenter is DCS,
/// while ViewTarget and ViewDirection are model-space WCS values.
/// </summary>
public readonly record struct CadViewportPrimitive(
    CadPoint3D Center,
    double Width,
    double Height,
    double ViewCenterX,
    double ViewCenterY,
    CadPoint3D ViewTarget,
    CadPoint3D ViewDirection,
    double ViewHeight,
    double TwistAngle,
    double LensLength,
    double FrontClipPlane,
    double BackClipPlane,
    int FrozenLayerOffset,
    int FrozenLayerCount,
    short ActiveStatus,
    uint StatusFlags,
    int RenderMode,
    int ShadePlotMode,
    ulong BoundaryHandle,
    bool RepresentsPaper)
{
    public bool IsOn => ActiveStatus > 0 && (StatusFlags & 131_072U) == 0;

    public bool IsPerspective => (StatusFlags & 1U) != 0;

    public bool HasFrontClip => (StatusFlags & 2U) != 0;

    public bool HasBackClip => (StatusFlags & 4U) != 0;

    public bool HasNonRectangularBoundary =>
        BoundaryHandle != 0 || (StatusFlags & 65_536U) != 0;
}

public readonly record struct CadViewportFrozenLayer(string Name);

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

/// <summary>
/// One immutable IMAGE/WIPEOUT clipping vertex in persisted pixel space.
/// </summary>
public readonly record struct CadWipeoutClipPoint(double U, double V);

/// <summary>
/// One retained WIPEOUT image frame. Origin is the visual lower-left image
/// corner; U/V are the WCS vectors of one persisted image pixel.
/// </summary>
public readonly record struct CadWipeoutPrimitive(
    CadPoint3D Origin,
    CadPoint3D UVector,
    CadPoint3D VVector,
    double Width,
    double Height,
    int ClipPointOffset,
    int ClipPointCount,
    bool IsClipped,
    bool IsInverted,
    bool DrawMask,
    bool ShowWhenNotAligned,
    bool DrawFrame,
    CadColor32 MaskColor);

/// <summary>
/// Immutable CPU-side identity for one IMAGEDEF shared by retained IMAGE entities.
/// It owns no decoded pixels, GPU object, file handle, or device-domain state.
/// </summary>
public readonly record struct CadRasterImageResource(
    ulong DefinitionHandle,
    string FileName,
    double PixelWidth,
    double PixelHeight,
    bool IsLoaded);

/// <summary>
/// One retained raster IMAGE instance. Origin is the visual lower-left corner;
/// U/V are the WCS vectors of one persisted source pixel.
/// </summary>
public readonly record struct CadRasterImagePrimitive(
    CadPoint3D Origin,
    CadPoint3D UVector,
    CadPoint3D VVector,
    double Width,
    double Height,
    int ClipPointOffset,
    int ClipPointCount,
    int ResourceIndex,
    bool IsClipped,
    bool IsInverted,
    bool DrawImage,
    bool ShowWhenNotAligned,
    bool DrawFrame,
    bool TransparencyIsOn,
    bool IsHighQuality,
    byte Brightness,
    byte Contrast,
    byte Fade,
    CadColor32 FadeColor);

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
    int IndexCount)
{
    /// <summary>
    /// Authored modern-MESH face ordinal, or -1 when the surface does not
    /// expose modern mesh subobjects.
    /// </summary>
    public int FaceSubobjectIndex { get; init; } = -1;

    /// <summary>Resolved retained material shared by this contiguous range.</summary>
    public int MaterialIndex { get; init; } = -1;

    public bool HasTextureCoordinates { get; init; }
}

/// <summary>One authored modern-MESH edge as an ordered display polyline.</summary>
public readonly record struct CadMesh3DSubobjectEdge(
    int PointOffset,
    int PointCount);

/// <summary>One authored modern-MESH face as an ordered authored-edge loop.</summary>
public readonly record struct CadMesh3DSubobjectFace(
    int EdgeIndexOffset,
    int EdgeIndexCount);

/// <summary>
/// One semantic MESH, polygon-mesh, or polyface-mesh instance. Its draw ranges
/// reference the snapshot-wide triangle streams and share this exact WCS bound.
/// </summary>
public readonly record struct CadMesh3DPrimitive(
    int DrawRangeOffset,
    int DrawRangeCount,
    CadBounds3D Bounds)
{
    /// <summary>
    /// Authoritative modern-MESH entity handle. This differs from the semantic
    /// root handle when the retained component is expanded from a block.
    /// </summary>
    public ulong SubobjectSourceHandle { get; init; }

    /// <summary>Maps authoritative MESH control vertices into WCS.</summary>
    public CadAffineTransform3D SubobjectSourceToWorld { get; init; } =
        CadAffineTransform3D.Identity;

    /// <summary>
    /// True only when the source MESH itself is owned by model space. Nested
    /// block-definition geometry requires an explicit reference-editing scope.
    /// </summary>
    public bool IsDirectModelSpaceSubobjectSource { get; init; }

    public int SubobjectVertexPointOffset { get; init; }
    public int SubobjectVertexCount { get; init; }
    public int SubobjectEdgeOffset { get; init; }
    public int SubobjectEdgeCount { get; init; }
    public int SubobjectFaceOffset { get; init; }
    public int SubobjectFaceCount { get; init; }

    public bool HasSubobjectTopology =>
        SubobjectVertexCount > 0 &&
        SubobjectEdgeCount > 0 &&
        SubobjectFaceCount > 0;
}

public enum CadModelerGeometryKind : byte
{
    Body = 1,
    Region = 2,
    Solid3D = 3,
}

/// <summary>
/// One immutable ACIS-backed BODY, REGION, or 3DSOLID resource. The payload
/// addresses <see cref="CadDocumentSnapshot.ModelerGeometryPayloadBytes"/> and
/// remains byte-exact; display wires are independent, bounded topology used for
/// wireframe replay and selection until face tessellation is available.
/// </summary>
public readonly record struct CadModelerGeometryPrimitive(
    CadModelerGeometryKind Kind,
    short ModelerFormatVersion,
    int WireOffset,
    int WireCount,
    int PayloadOffset,
    int PayloadCount,
    bool IsBinaryPayload);

/// <summary>One retained display-wire polyline within a modeler entity.</summary>
public readonly record struct CadModelerGeometryWire(
    int PointOffset,
    int PointCount,
    int SelectionMarker,
    int AcisIndex,
    byte Type);

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

/// <summary>
/// One immutable OCS polyline vertex. Widths describe the segment beginning
/// at this vertex; they are populated only when the owning primitive has a
/// nonuniform profile.
/// </summary>
public readonly record struct CadPolylineVertex(
    double X,
    double Y,
    double Bulge,
    double StartWidth = 0.0,
    double EndWidth = 0.0);

/// <summary>
/// One immutable planar polyline. Uniform profiles use <see cref="ConstantWidth"/>
/// and the analytic stroke fast path; nonuniform straight profiles read start
/// and end widths from their segment-start vertices. <see cref="IsFillEnabled"/>
/// captures the drawing-level FILLMODE policy for exact retained replay.
/// </summary>
public readonly record struct CadPolylinePrimitive(
    CadPoint3D WorldOrigin,
    CadCoordinateSystem CoordinateSystem,
    int VertexOffset,
    int VertexCount,
    bool IsClosed,
    bool IsLineTypeContinuous,
    double ConstantWidth = 0.0,
    bool HasVariableWidth = false,
    bool IsFillEnabled = true)
{
    public bool IsWide => ConstantWidth > 0.0 || HasVariableWidth;
}

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
/// One retained standard or Unicode SHX MTEXT entity. Glyph paths address the shared SHX
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
/// A contiguous standard or Unicode SHX MTEXT glyph range sharing local scale, oblique,
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
    private readonly CadPlanBlockInstanceRange[] _planBlockInstances;
    private readonly CadLinePrimitive[] _lines;
    private readonly CadMLinePrimitive[] _mLines;
    private readonly CadMLineElementPath[] _mLineElementPaths;
    private readonly CadMLineStroke[] _mLineStrokes;
    private readonly CadMLineFillTriangle[] _mLineFillTriangles;
    private readonly CadLeaderPrimitive[] _leaders;
    private readonly CadMultiLeaderPrimitive[] _multiLeaders;
    private readonly CadTolerancePrimitive[] _tolerances;
    private readonly CadToleranceStroke[] _toleranceStrokes;
    private readonly CadViewportPrimitive[] _viewports;
    private readonly CadViewportFrozenLayer[] _viewportFrozenLayers;
    private readonly CadPointPrimitive[] _points;
    private readonly CadConstructionLinePrimitive[] _constructionLines;
    private readonly CadWipeoutPrimitive[] _wipeouts;
    private readonly CadWipeoutClipPoint[] _wipeoutClipPoints;
    private readonly CadRasterImagePrimitive[] _rasterImages;
    private readonly CadRasterImageResource[] _rasterImageResources;
    private readonly CadWipeoutClipPoint[] _rasterImageClipPoints;
    private readonly CadMesh3DMaterial[] _mesh3DMaterials;
    private readonly CadMaterialTextureResource[] _materialTextureResources;
    private readonly CadMesh3DPrimitive[] _meshes3D;
    private readonly CadMesh3DDrawRange[] _mesh3DDrawRanges;
    private readonly CadMesh3DVertex[] _mesh3DVertices;
    private readonly uint[] _mesh3DIndices;
    private readonly int[] _mesh3DVertexSubobjectIndices;
    private readonly int[] _mesh3DEdgeSubobjectIndices;
    private readonly CadPoint3D[] _mesh3DSubobjectPoints;
    private readonly CadMesh3DSubobjectEdge[] _mesh3DSubobjectEdges;
    private readonly CadMesh3DSubobjectFace[] _mesh3DSubobjectFaces;
    private readonly int[] _mesh3DSubobjectFaceEdgeIndices;
    private readonly CadModelerGeometryPrimitive[] _modelerGeometries;
    private readonly CadModelerGeometryWire[] _modelerGeometryWires;
    private readonly CadPoint3D[] _modelerGeometryPoints;
    private readonly byte[] _modelerGeometryPayloadBytes;
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

    /// <summary>
    /// Optional source path/name captured with the mutable document. Raster
    /// resolvers use it only as immutable context for resolving relative IMAGEDEF paths.
    /// </summary>
    public string? SourceName { get; }

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
    public CadPlanGridDisplaySettings PlanGridDisplaySettings { get; }
    public CadPlanGridSnapSettings PlanGridSnapSettings { get; }
    public CadPlanPolarTrackingSettings PlanPolarTrackingSettings { get; }
    public CadPlanAuthoringContext PlanAuthoringContext { get; }
    public bool IsOrthoModeEnabled { get; }
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
    public ReadOnlyMemory<CadPlanBlockInstanceRange> PlanBlockInstances =>
        _planBlockInstances;
    public ReadOnlyMemory<CadLinePrimitive> Lines => _lines;
    public ReadOnlyMemory<CadMLinePrimitive> MLines => _mLines;
    public ReadOnlyMemory<CadMLineElementPath> MLineElementPaths => _mLineElementPaths;
    public ReadOnlyMemory<CadMLineStroke> MLineStrokes => _mLineStrokes;
    public ReadOnlyMemory<CadMLineFillTriangle> MLineFillTriangles => _mLineFillTriangles;
    public ReadOnlyMemory<CadLeaderPrimitive> Leaders => _leaders;
    public ReadOnlyMemory<CadMultiLeaderPrimitive> MultiLeaders => _multiLeaders;
    public ReadOnlyMemory<CadTolerancePrimitive> Tolerances => _tolerances;
    public ReadOnlyMemory<CadToleranceStroke> ToleranceStrokes => _toleranceStrokes;
    public ReadOnlyMemory<CadViewportPrimitive> Viewports => _viewports;
    public ReadOnlyMemory<CadViewportFrozenLayer> ViewportFrozenLayers =>
        _viewportFrozenLayers;
    public ReadOnlyMemory<CadPointPrimitive> Points => _points;
    public ReadOnlyMemory<CadConstructionLinePrimitive> ConstructionLines => _constructionLines;
    public ReadOnlyMemory<CadWipeoutPrimitive> Wipeouts => _wipeouts;
    public ReadOnlyMemory<CadWipeoutClipPoint> WipeoutClipPoints => _wipeoutClipPoints;
    public ReadOnlyMemory<CadRasterImagePrimitive> RasterImages => _rasterImages;
    public ReadOnlyMemory<CadRasterImageResource> RasterImageResources => _rasterImageResources;
    public ReadOnlyMemory<CadWipeoutClipPoint> RasterImageClipPoints => _rasterImageClipPoints;
    public ReadOnlyMemory<CadMesh3DMaterial> Mesh3DMaterials => _mesh3DMaterials;
    public ReadOnlyMemory<CadMaterialTextureResource> MaterialTextureResources =>
        _materialTextureResources;
    public ReadOnlyMemory<CadMesh3DPrimitive> Meshes3D => _meshes3D;
    public ReadOnlyMemory<CadMesh3DDrawRange> Mesh3DDrawRanges => _mesh3DDrawRanges;
    public ReadOnlyMemory<CadMesh3DVertex> Mesh3DVertices => _mesh3DVertices;
    public ReadOnlyMemory<uint> Mesh3DIndices => _mesh3DIndices;
    public ReadOnlyMemory<int> Mesh3DVertexSubobjectIndices =>
        _mesh3DVertexSubobjectIndices;
    public ReadOnlyMemory<int> Mesh3DEdgeSubobjectIndices =>
        _mesh3DEdgeSubobjectIndices;
    public ReadOnlyMemory<CadPoint3D> Mesh3DSubobjectPoints =>
        _mesh3DSubobjectPoints;
    public ReadOnlyMemory<CadMesh3DSubobjectEdge> Mesh3DSubobjectEdges =>
        _mesh3DSubobjectEdges;
    public ReadOnlyMemory<CadMesh3DSubobjectFace> Mesh3DSubobjectFaces =>
        _mesh3DSubobjectFaces;
    public ReadOnlyMemory<int> Mesh3DSubobjectFaceEdgeIndices =>
        _mesh3DSubobjectFaceEdgeIndices;
    public ReadOnlyMemory<CadModelerGeometryPrimitive> ModelerGeometries => _modelerGeometries;
    public ReadOnlyMemory<CadModelerGeometryWire> ModelerGeometryWires => _modelerGeometryWires;
    public ReadOnlyMemory<CadPoint3D> ModelerGeometryPoints => _modelerGeometryPoints;
    public ReadOnlyMemory<byte> ModelerGeometryPayloadBytes => _modelerGeometryPayloadBytes;
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
        string? sourceName,
        CadDrawOrderPurpose drawOrderPurpose,
        bool hasDrawOrderOverrides,
        bool isPlotOrderCompatible,
        double globalLineTypeScale,
        CadPlanGridDisplaySettings planGridDisplaySettings,
        CadPlanGridSnapSettings planGridSnapSettings,
        CadPlanPolarTrackingSettings planPolarTrackingSettings,
        CadPlanAuthoringContext planAuthoringContext,
        bool isOrthoModeEnabled,
        CadBounds3D bounds,
        CadSnapshotStatistics statistics,
        CadLayerSnapshot[] layers,
        CadStrokeStyle[] styles,
        CadLineTypePattern[] lineTypePatterns,
        CadLineTypeElement[] lineTypeElements,
        CadLineTypeTextResource[] lineTypeTextResources,
        CadLineTypeShapeResource[] lineTypeShapeResources,
        CadEntityHeader[] entities,
        CadPlanBlockInstanceRange[] planBlockInstances,
        CadLinePrimitive[] lines,
        CadMLinePrimitive[] mLines,
        CadMLineElementPath[] mLineElementPaths,
        CadMLineStroke[] mLineStrokes,
        CadMLineFillTriangle[] mLineFillTriangles,
        CadLeaderPrimitive[] leaders,
        CadMultiLeaderPrimitive[] multiLeaders,
        CadTolerancePrimitive[] tolerances,
        CadToleranceStroke[] toleranceStrokes,
        CadViewportPrimitive[] viewports,
        CadViewportFrozenLayer[] viewportFrozenLayers,
        CadPointPrimitive[] points,
        CadConstructionLinePrimitive[] constructionLines,
        CadWipeoutPrimitive[] wipeouts,
        CadWipeoutClipPoint[] wipeoutClipPoints,
        CadRasterImagePrimitive[] rasterImages,
        CadRasterImageResource[] rasterImageResources,
        CadWipeoutClipPoint[] rasterImageClipPoints,
        CadMesh3DMaterial[] mesh3DMaterials,
        CadMaterialTextureResource[] materialTextureResources,
        CadMesh3DPrimitive[] meshes3D,
        CadMesh3DDrawRange[] mesh3DDrawRanges,
        CadMesh3DVertex[] mesh3DVertices,
        uint[] mesh3DIndices,
        int[] mesh3DVertexSubobjectIndices,
        int[] mesh3DEdgeSubobjectIndices,
        CadPoint3D[] mesh3DSubobjectPoints,
        CadMesh3DSubobjectEdge[] mesh3DSubobjectEdges,
        CadMesh3DSubobjectFace[] mesh3DSubobjectFaces,
        int[] mesh3DSubobjectFaceEdgeIndices,
        CadModelerGeometryPrimitive[] modelerGeometries,
        CadModelerGeometryWire[] modelerGeometryWires,
        CadPoint3D[] modelerGeometryPoints,
        byte[] modelerGeometryPayloadBytes,
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
        SourceName = sourceName;
        DrawOrderPurpose = drawOrderPurpose;
        HasDrawOrderOverrides = hasDrawOrderOverrides;
        IsPlotOrderCompatible = isPlotOrderCompatible;
        GlobalLineTypeScale = globalLineTypeScale;
        PlanGridDisplaySettings = planGridDisplaySettings;
        PlanGridSnapSettings = planGridSnapSettings;
        PlanPolarTrackingSettings = planPolarTrackingSettings;
        PlanAuthoringContext = planAuthoringContext;
        IsOrthoModeEnabled = isOrthoModeEnabled;
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
        _planBlockInstances = planBlockInstances;
        _lines = lines;
        _mLines = mLines;
        _mLineElementPaths = mLineElementPaths;
        _mLineStrokes = mLineStrokes;
        _mLineFillTriangles = mLineFillTriangles;
        _leaders = leaders;
        _multiLeaders = multiLeaders;
        _tolerances = tolerances;
        _toleranceStrokes = toleranceStrokes;
        _viewports = viewports;
        _viewportFrozenLayers = viewportFrozenLayers;
        _points = points;
        _constructionLines = constructionLines;
        _wipeouts = wipeouts;
        _wipeoutClipPoints = wipeoutClipPoints;
        _rasterImages = rasterImages;
        _rasterImageResources = rasterImageResources;
        _rasterImageClipPoints = rasterImageClipPoints;
        _mesh3DMaterials = mesh3DMaterials;
        _materialTextureResources = materialTextureResources;
        _meshes3D = meshes3D;
        _mesh3DDrawRanges = mesh3DDrawRanges;
        _mesh3DVertices = mesh3DVertices;
        _mesh3DIndices = mesh3DIndices;
        _mesh3DVertexSubobjectIndices = mesh3DVertexSubobjectIndices;
        _mesh3DEdgeSubobjectIndices = mesh3DEdgeSubobjectIndices;
        _mesh3DSubobjectPoints = mesh3DSubobjectPoints;
        _mesh3DSubobjectEdges = mesh3DSubobjectEdges;
        _mesh3DSubobjectFaces = mesh3DSubobjectFaces;
        _mesh3DSubobjectFaceEdgeIndices = mesh3DSubobjectFaceEdgeIndices;
        _modelerGeometries = modelerGeometries;
        _modelerGeometryWires = modelerGeometryWires;
        _modelerGeometryPoints = modelerGeometryPoints;
        _modelerGeometryPayloadBytes = modelerGeometryPayloadBytes;
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
            .Where(index => entities[index].IsVisible && !entities[index].Bounds.IsEmpty)
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
