using System.Numerics;

namespace ProGPU.CAD;

/// <summary>Composable running object-snap modes for immutable CAD snapshots.</summary>
[Flags]
public enum CadObjectSnapModes : ushort
{
    None = 0,
    Endpoint = 1 << 0,
    Midpoint = 1 << 1,
    Center = 1 << 2,
    Node = 1 << 3,
    Quadrant = 1 << 4,
    Intersection = 1 << 5,
    Nearest = 1 << 9,
    Standard = Endpoint | Midpoint | Center | Node | Intersection | Quadrant,
}

/// <summary>Exact semantic kind of one accepted object-snap point.</summary>
public enum CadObjectSnapKind : byte
{
    None = 0,
    Endpoint = 1,
    Midpoint = 2,
    Center = 3,
    Node = 4,
    Quadrant = 5,
    Intersection = 6,
    Nearest = 7,
}

/// <summary>
/// Immutable result of one caller-buffered plan-view object-snap query.
/// </summary>
public readonly record struct CadObjectSnapResult(
    ulong ContentGeneration,
    CadObjectSnapKind Kind,
    int EntityIndex,
    ulong Handle,
    CadPoint3D Point,
    double DistancePixels,
    int CandidateWrittenCount,
    int CandidateTotalCount,
    int EvaluatedSnapPointCount,
    int UnsupportedGeometryCount)
{
    public bool IsSnapped => Kind != CadObjectSnapKind.None;

    public bool AreCandidatesTruncated =>
        CandidateWrittenCount != CandidateTotalCount;

    /// <summary>
    /// Second retained entity participating in an Intersection result, or -1.
    /// </summary>
    public int SecondEntityIndex { get; init; } = -1;

    /// <summary>Second source handle participating in an Intersection result.</summary>
    public ulong SecondHandle { get; init; }

    /// <summary>Number of retained entity pairs tested for intersections.</summary>
    public int EvaluatedEntityPairCount { get; init; }

    /// <summary>Total candidate pairs before the fixed intersection-work budget.</summary>
    public long CandidatePairTotalCount { get; init; }

    /// <summary>Number of analytic component pairs tested for intersections.</summary>
    public int EvaluatedIntersectionComponentPairCount { get; init; }

    /// <summary>
    /// Whether the fixed analytic component-pair budget was exhausted.
    /// </summary>
    public bool AreIntersectionComponentsTruncated { get; init; }

    /// <summary>
    /// Whether caller-scratch truncation or the fixed pair-work budget prevented
    /// testing every broad-phase entity pair.
    /// </summary>
    public bool AreIntersectionPairsTruncated =>
        EvaluatedEntityPairCount < CandidatePairTotalCount ||
        AreIntersectionComponentsTruncated;
}

/// <summary>
/// Allocation-free plan-view snapping over exact points derived from one
/// immutable CAD snapshot generation.
/// </summary>
public static partial class CadObjectSnapQuery
{
    /// <summary>
    /// Maximum number of retained entity pairs evaluated by one query.
    /// </summary>
    public const int MaximumIntersectionEntityPairs = 65_536;

    /// <summary>
    /// Maximum number of analytic component pairs evaluated by one query.
    /// </summary>
    public const int MaximumIntersectionComponentPairs = 262_144;

    private const double TwoPi = Math.PI * 2.0;
    private const double FullSweepTolerance = 1e-12;
    private const double SnapParameterTolerance = 1e-12;

    /// <summary>
    /// Finds the closest enabled snap point inside a logical-pixel aperture.
    /// </summary>
    /// <remarks>
    /// The broad phase is O(log E + K) average and O(E + K) worst-case for E
    /// retained entities and K candidates. Intersection mode deterministically
    /// sorts caller scratch in O(K log K), tests at most B entity pairs and C
    /// analytic component pairs. Other candidate evaluation is O(P) for P
    /// exact snap points. B and C are the corresponding public maximums above.
    /// Nearest mode performs exact segment/span closest-point work S. Internal
    /// storage is O(1) plus caller-owned entity-index scratch. Equal device
    /// distances prefer Intersection, Endpoint, Midpoint, Center, Quadrant,
    /// Node, then Nearest, followed by retained entity order, second entity
    /// order, and point ordinal.
    /// </remarks>
    public static CadObjectSnapResult Query(
        CadDocumentSnapshot snapshot,
        CadPlanViewport viewport,
        Vector2 screenPoint,
        float aperturePixels,
        CadObjectSnapModes modes,
        Span<int> entityIndexScratch)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!float.IsFinite(screenPoint.X) || !float.IsFinite(screenPoint.Y))
        {
            throw new ArgumentException(
                "The object-snap screen point must be finite.",
                nameof(screenPoint));
        }
        if (!float.IsFinite(aperturePixels) || aperturePixels < 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aperturePixels),
                "The object-snap aperture must be finite and non-negative.");
        }
        if ((modes & ~(CadObjectSnapModes.Standard |
                       CadObjectSnapModes.Nearest)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modes));
        }
        if (modes == CadObjectSnapModes.None)
        {
            return Empty(snapshot.ContentGeneration);
        }

        Vector2 inflation = new(aperturePixels);
        Vector2 first = screenPoint - inflation;
        Vector2 second = screenPoint + inflation;
        if (!float.IsFinite(first.X) || !float.IsFinite(first.Y) ||
            !float.IsFinite(second.X) || !float.IsFinite(second.Y))
        {
            throw new ArgumentOutOfRangeException(
                nameof(aperturePixels),
                "The object-snap aperture exceeds finite screen coordinates.");
        }

        CadBounds3D queryBounds = viewport.CreatePlanSelectionBounds(
            first,
            second);
        CadSpatialQueryResult spatial = snapshot.SpatialIndex.Query(
            queryBounds,
            entityIndexScratch);
        int candidateWrittenCount = spatial.WrittenCount;
        int candidateTotalCount = spatial.TotalCount;
        if ((modes & (CadObjectSnapModes.Intersection |
                      CadObjectSnapModes.Nearest)) != 0)
        {
            AppendConstructionLineCandidates(
                snapshot,
                entityIndexScratch,
                ref candidateWrittenCount,
                ref candidateTotalCount);
            if ((modes & CadObjectSnapModes.Intersection) != 0)
            {
                entityIndexScratch[..candidateWrittenCount].Sort();
            }
        }

        var search = new SearchState(
            snapshot.ContentGeneration,
            viewport,
            screenPoint,
            aperturePixels,
            modes,
            candidateWrittenCount,
            candidateTotalCount);
        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        if ((modes & ~(CadObjectSnapModes.Intersection |
                       CadObjectSnapModes.Nearest)) != 0)
        {
            for (int i = 0; i < candidateWrittenCount; i++)
            {
                int entityIndex = entityIndexScratch[i];
                EvaluateEntity(
                    snapshot,
                    entities[entityIndex],
                    entityIndex,
                    ref search);
            }
        }
        if ((modes & CadObjectSnapModes.Nearest) != 0)
        {
            CadPoint3D queryPoint = viewport.ScreenToWorld(screenPoint);
            for (int i = 0; i < candidateWrittenCount; i++)
            {
                int entityIndex = entityIndexScratch[i];
                EvaluateNearest(
                    snapshot,
                    entities[entityIndex],
                    entityIndex,
                    queryPoint,
                    ref search);
            }
        }
        if ((modes & CadObjectSnapModes.Intersection) != 0)
        {
            EvaluateIntersections(
                snapshot,
                entityIndexScratch[..candidateWrittenCount],
                ref search);
        }
        return search.CreateResult();
    }

    private static void AppendConstructionLineCandidates(
        CadDocumentSnapshot snapshot,
        Span<int> entityIndexScratch,
        ref int writtenCount,
        ref int totalCount)
    {
        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
        {
            if (entities[entityIndex].Kind is not
                (CadEntityKind.Ray or CadEntityKind.XLine))
            {
                continue;
            }

            if (writtenCount < entityIndexScratch.Length)
            {
                entityIndexScratch[writtenCount++] = entityIndex;
            }
            totalCount++;
        }
    }

    private static void EvaluateEntity(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        ref SearchState search)
    {
        switch (header.Kind)
        {
            case CadEntityKind.Line:
            {
                CadLinePrimitive line = snapshot.Lines.Span[header.PrimitiveIndex];
                search.Consider(
                    CadObjectSnapKind.Endpoint,
                    line.Start,
                    entityIndex,
                    header.Handle,
                    0);
                search.Consider(
                    CadObjectSnapKind.Endpoint,
                    line.End,
                    entityIndex,
                    header.Handle,
                    1);
                search.Consider(
                    CadObjectSnapKind.Midpoint,
                    Midpoint(line.Start, line.End),
                    entityIndex,
                    header.Handle,
                    2);
                break;
            }
            case CadEntityKind.Circle:
            {
                CadCirclePrimitive circle =
                    snapshot.Circles.Span[header.PrimitiveIndex];
                search.Consider(
                    CadObjectSnapKind.Center,
                    circle.Center,
                    entityIndex,
                    header.Handle,
                    0);
                for (int quadrant = 0; quadrant < 4; quadrant++)
                {
                    search.Consider(
                        CadObjectSnapKind.Quadrant,
                        circle.CoordinateSystem.PointOnCircle(
                            circle.Center,
                            circle.Radius,
                            quadrant * (Math.PI * 0.5)),
                        entityIndex,
                        header.Handle,
                        quadrant + 1);
                }
                break;
            }
            case CadEntityKind.Arc:
            {
                CadArcPrimitive arc = snapshot.Arcs.Span[header.PrimitiveIndex];
                bool hasEndpoints = !IsFullSweep(arc.SweepAngle);
                if (hasEndpoints)
                {
                    search.Consider(
                        CadObjectSnapKind.Endpoint,
                        arc.StartPoint,
                        entityIndex,
                        header.Handle,
                        0);
                    search.Consider(
                        CadObjectSnapKind.Endpoint,
                        arc.EndPoint,
                        entityIndex,
                        header.Handle,
                        1);
                }
                search.Consider(
                    CadObjectSnapKind.Midpoint,
                    arc.CoordinateSystem.PointOnCircle(
                        arc.Center,
                        arc.Radius,
                        arc.StartAngle + (arc.SweepAngle * 0.5)),
                    entityIndex,
                    header.Handle,
                    2);
                search.Consider(
                    CadObjectSnapKind.Center,
                    arc.Center,
                    entityIndex,
                    header.Handle,
                    3);
                for (int quadrant = 0; quadrant < 4; quadrant++)
                {
                    double angle = quadrant * (Math.PI * 0.5);
                    if (ContainsAngle(
                            arc.StartAngle,
                            arc.SweepAngle,
                            angle))
                    {
                        search.Consider(
                            CadObjectSnapKind.Quadrant,
                            arc.CoordinateSystem.PointOnCircle(
                                arc.Center,
                                arc.Radius,
                                angle),
                            entityIndex,
                            header.Handle,
                            quadrant + 4);
                    }
                }
                break;
            }
            case CadEntityKind.Ellipse:
            {
                CadEllipsePrimitive ellipse =
                    snapshot.Ellipses.Span[header.PrimitiveIndex];
                bool hasEndpoints = !IsFullSweep(ellipse.SweepParameter);
                if (hasEndpoints)
                {
                    search.Consider(
                        CadObjectSnapKind.Endpoint,
                        ellipse.StartPoint,
                        entityIndex,
                        header.Handle,
                        0);
                    search.Consider(
                        CadObjectSnapKind.Endpoint,
                        ellipse.EndPoint,
                        entityIndex,
                        header.Handle,
                        1);
                }
                search.Consider(
                    CadObjectSnapKind.Midpoint,
                    ellipse.PointAt(
                        ellipse.StartParameter +
                        (ellipse.SweepParameter * 0.5)),
                    entityIndex,
                    header.Handle,
                    2);
                search.Consider(
                    CadObjectSnapKind.Center,
                    ellipse.Center,
                    entityIndex,
                    header.Handle,
                    3);
                for (int quadrant = 0; quadrant < 4; quadrant++)
                {
                    double parameter = quadrant * (Math.PI * 0.5);
                    if (ContainsAngle(
                            ellipse.StartParameter,
                            ellipse.SweepParameter,
                            parameter))
                    {
                        search.Consider(
                            CadObjectSnapKind.Quadrant,
                            ellipse.PointAt(parameter),
                            entityIndex,
                            header.Handle,
                            quadrant + 4);
                    }
                }
                break;
            }
            case CadEntityKind.LightweightPolyline:
            case CadEntityKind.Polyline2D:
                EvaluatePolyline2D(snapshot, header, entityIndex, ref search);
                break;
            case CadEntityKind.Polyline3D:
                EvaluatePolyline3D(snapshot, header, entityIndex, ref search);
                break;
            case CadEntityKind.Spline:
                EvaluateSpline(snapshot, header, entityIndex, ref search);
                break;
            case CadEntityKind.Point:
                search.Consider(
                    CadObjectSnapKind.Node,
                    snapshot.Points.Span[header.PrimitiveIndex].Position,
                    entityIndex,
                    header.Handle,
                    0);
                break;
        }
    }

    private static void EvaluatePolyline2D(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        ref SearchState search)
    {
        CadPolylinePrimitive polyline =
            snapshot.Polylines.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPolylineVertex> vertices =
            snapshot.PolylineVertices.Span.Slice(
                polyline.VertexOffset,
                polyline.VertexCount);
        for (int i = 0; i < vertices.Length; i++)
        {
            search.Consider(
                CadObjectSnapKind.Endpoint,
                ToWorld(polyline, vertices[i]),
                entityIndex,
                header.Handle,
                i);
        }
        if (vertices.Length < 2)
        {
            return;
        }

        int segmentCount = polyline.IsClosed
            ? vertices.Length
            : vertices.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            CadPolylineVertex start = vertices[i];
            CadPolylineVertex end = vertices[(i + 1) % vertices.Length];
            CadPoint3D midpoint;
            if (start.Bulge == 0.0)
            {
                midpoint = Midpoint(
                    ToWorld(polyline, start),
                    ToWorld(polyline, end));
            }
            else if (!TryGetBulgeMidpoint(polyline, start, end, out midpoint))
            {
                search.UnsupportedGeometryCount++;
                continue;
            }
            search.Consider(
                CadObjectSnapKind.Midpoint,
                midpoint,
                entityIndex,
                header.Handle,
                vertices.Length + i);
        }
    }

    private static void EvaluatePolyline3D(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        ref SearchState search)
    {
        CadPolyline3DPrimitive polyline =
            snapshot.Polylines3D.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPoint3D> points =
            snapshot.Polyline3DPoints.Span.Slice(
                polyline.PointOffset,
                polyline.PointCount);
        for (int i = 0; i < points.Length; i++)
        {
            search.Consider(
                CadObjectSnapKind.Endpoint,
                points[i],
                entityIndex,
                header.Handle,
                i);
        }
        if (points.Length < 2)
        {
            return;
        }

        int segmentCount = polyline.IsClosed
            ? points.Length
            : points.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            search.Consider(
                CadObjectSnapKind.Midpoint,
                Midpoint(points[i], points[(i + 1) % points.Length]),
                entityIndex,
                header.Handle,
                points.Length + i);
        }
    }

    private static void EvaluateSpline(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        int entityIndex,
        ref SearchState search)
    {
        CadSplinePrimitive spline =
            snapshot.Splines.Span[header.PrimitiveIndex];
        if (spline.IsClosed || spline.IsPeriodic)
        {
            return;
        }
        if (!CadSplineSelection.TryGetEndpoints(
                snapshot,
                spline,
                out CadPoint3D start,
                out CadPoint3D end))
        {
            search.UnsupportedGeometryCount++;
            return;
        }

        search.Consider(
            CadObjectSnapKind.Endpoint,
            start,
            entityIndex,
            header.Handle,
            0);
        search.Consider(
            CadObjectSnapKind.Endpoint,
            end,
            entityIndex,
            header.Handle,
            1);
    }

    private static bool TryGetBulgeMidpoint(
        CadPolylinePrimitive polyline,
        CadPolylineVertex start,
        CadPolylineVertex end,
        out CadPoint3D midpoint)
    {
        try
        {
            CadSnapshotCompiler.GetBulgeArc(
                start,
                end,
                out double centerX,
                out double centerY,
                out double radius,
                out double startAngle,
                out double sweep);
            double parameter = startAngle + (sweep * 0.5);
            midpoint = ToWorld(
                polyline,
                centerX + (radius * Math.Cos(parameter)),
                centerY + (radius * Math.Sin(parameter)));
            return IsFinite(midpoint);
        }
        catch (ArgumentException)
        {
            midpoint = default;
            return false;
        }
        catch (ArithmeticException)
        {
            midpoint = default;
            return false;
        }
    }

    private static CadPoint3D ToWorld(
        CadPolylinePrimitive polyline,
        CadPolylineVertex vertex) =>
        ToWorld(polyline, vertex.X, vertex.Y);

    private static CadPoint3D ToWorld(
        CadPolylinePrimitive polyline,
        double x,
        double y) =>
        polyline.WorldOrigin +
        (polyline.CoordinateSystem.XAxis * x) +
        (polyline.CoordinateSystem.YAxis * y);

    private static CadPoint3D Midpoint(CadPoint3D first, CadPoint3D second) =>
        new(
            (first.X * 0.5) + (second.X * 0.5),
            (first.Y * 0.5) + (second.Y * 0.5),
            (first.Z * 0.5) + (second.Z * 0.5));

    private static bool IsFullSweep(double sweep) =>
        Math.Abs(sweep) >= TwoPi - FullSweepTolerance;

    private static bool ContainsAngle(
        double start,
        double sweep,
        double angle)
    {
        if (IsFullSweep(sweep))
        {
            return true;
        }
        double extent = Math.Abs(sweep);
        double relative = sweep >= 0.0
            ? NormalizePositive(angle - start)
            : NormalizePositive(start - angle);
        return relative <= extent + SnapParameterTolerance;
    }

    private static double NormalizePositive(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);

    private static CadObjectSnapResult Empty(ulong contentGeneration) =>
        new(
            contentGeneration,
            CadObjectSnapKind.None,
            -1,
            0,
            default,
            double.PositiveInfinity,
            0,
            0,
            0,
            0);

    private struct SearchState
    {
        private readonly ulong _contentGeneration;
        private readonly CadPlanViewport _viewport;
        private readonly Vector2 _screenPoint;
        private readonly double _apertureSquared;
        private readonly CadObjectSnapModes _modes;
        private readonly int _candidateWrittenCount;
        private readonly int _candidateTotalCount;
        private readonly long _candidatePairTotalCount;
        private bool _hasBest;
        private CadObjectSnapKind _bestKind;
        private int _bestEntityIndex;
        private int _bestSecondEntityIndex;
        private int _bestOrdinal;
        private ulong _bestHandle;
        private ulong _bestSecondHandle;
        private CadPoint3D _bestPoint;
        private double _bestDistanceSquared;

        public int EvaluatedSnapPointCount { get; private set; }

        public int UnsupportedGeometryCount { get; set; }

        public int EvaluatedEntityPairCount { get; set; }

        public int EvaluatedIntersectionComponentPairCount { get; set; }

        public bool AreIntersectionComponentsTruncated { get; set; }

        public SearchState(
            ulong contentGeneration,
            CadPlanViewport viewport,
            Vector2 screenPoint,
            float aperturePixels,
            CadObjectSnapModes modes,
            int candidateWrittenCount,
            int candidateTotalCount)
        {
            _contentGeneration = contentGeneration;
            _viewport = viewport;
            _screenPoint = screenPoint;
            _apertureSquared = (double)aperturePixels * aperturePixels;
            _modes = modes;
            _candidateWrittenCount = candidateWrittenCount;
            _candidateTotalCount = candidateTotalCount;
            _candidatePairTotalCount =
                (modes & CadObjectSnapModes.Intersection) != 0
                    ? ((long)candidateTotalCount *
                       (candidateTotalCount - 1)) / 2
                    : 0;
            _hasBest = false;
            _bestKind = CadObjectSnapKind.None;
            _bestEntityIndex = -1;
            _bestSecondEntityIndex = -1;
            _bestOrdinal = -1;
            _bestHandle = 0;
            _bestSecondHandle = 0;
            _bestPoint = default;
            _bestDistanceSquared = double.PositiveInfinity;
            EvaluatedSnapPointCount = 0;
            UnsupportedGeometryCount = 0;
            EvaluatedEntityPairCount = 0;
            EvaluatedIntersectionComponentPairCount = 0;
            AreIntersectionComponentsTruncated = false;
        }

        public void Consider(
            CadObjectSnapKind kind,
            CadPoint3D point,
            int entityIndex,
            ulong handle,
            int ordinal) =>
            Consider(
                kind,
                point,
                entityIndex,
                handle,
                -1,
                0,
                ordinal);

        public void Consider(
            CadObjectSnapKind kind,
            CadPoint3D point,
            int entityIndex,
            ulong handle,
            int secondEntityIndex,
            ulong secondHandle,
            int ordinal)
        {
            if (!IsEnabled(kind) || !IsFinite(point))
            {
                return;
            }
            EvaluatedSnapPointCount++;

            Vector2 projected;
            try
            {
                projected = _viewport.WorldToScreen(point);
            }
            catch (ArgumentException)
            {
                UnsupportedGeometryCount++;
                return;
            }
            double deltaX = (double)projected.X - _screenPoint.X;
            double deltaY = (double)projected.Y - _screenPoint.Y;
            double distanceSquared =
                (deltaX * deltaX) + (deltaY * deltaY);
            if (!double.IsFinite(distanceSquared) ||
                distanceSquared > _apertureSquared ||
                !IsBetter(
                    kind,
                    entityIndex,
                    secondEntityIndex,
                    ordinal,
                    distanceSquared))
            {
                return;
            }

            _hasBest = true;
            _bestKind = kind;
            _bestEntityIndex = entityIndex;
            _bestSecondEntityIndex = secondEntityIndex;
            _bestOrdinal = ordinal;
            _bestHandle = handle;
            _bestSecondHandle = secondHandle;
            _bestPoint = point;
            _bestDistanceSquared = distanceSquared;
        }

        public CadObjectSnapResult CreateResult() =>
            new CadObjectSnapResult(
                _contentGeneration,
                _hasBest ? _bestKind : CadObjectSnapKind.None,
                _hasBest ? _bestEntityIndex : -1,
                _hasBest ? _bestHandle : 0,
                _hasBest ? _bestPoint : default,
                _hasBest
                    ? Math.Sqrt(_bestDistanceSquared)
                    : double.PositiveInfinity,
                _candidateWrittenCount,
                _candidateTotalCount,
                EvaluatedSnapPointCount,
                UnsupportedGeometryCount)
            {
                SecondEntityIndex = _hasBest
                    ? _bestSecondEntityIndex
                    : -1,
                SecondHandle = _hasBest ? _bestSecondHandle : 0,
                EvaluatedEntityPairCount = this.EvaluatedEntityPairCount,
                CandidatePairTotalCount = _candidatePairTotalCount,
                EvaluatedIntersectionComponentPairCount =
                    this.EvaluatedIntersectionComponentPairCount,
                AreIntersectionComponentsTruncated =
                    this.AreIntersectionComponentsTruncated,
            };

        private bool IsEnabled(CadObjectSnapKind kind) => kind switch
        {
            CadObjectSnapKind.Endpoint =>
                (_modes & CadObjectSnapModes.Endpoint) != 0,
            CadObjectSnapKind.Midpoint =>
                (_modes & CadObjectSnapModes.Midpoint) != 0,
            CadObjectSnapKind.Center =>
                (_modes & CadObjectSnapModes.Center) != 0,
            CadObjectSnapKind.Node =>
                (_modes & CadObjectSnapModes.Node) != 0,
            CadObjectSnapKind.Intersection =>
                (_modes & CadObjectSnapModes.Intersection) != 0,
            CadObjectSnapKind.Quadrant =>
                (_modes & CadObjectSnapModes.Quadrant) != 0,
            CadObjectSnapKind.Nearest =>
                (_modes & CadObjectSnapModes.Nearest) != 0,
            _ => false,
        };

        private bool IsBetter(
            CadObjectSnapKind kind,
            int entityIndex,
            int secondEntityIndex,
            int ordinal,
            double distanceSquared)
        {
            if (!_hasBest || distanceSquared < _bestDistanceSquared)
            {
                return true;
            }
            if (distanceSquared > _bestDistanceSquared)
            {
                return false;
            }

            int priority = Priority(kind);
            int bestPriority = Priority(_bestKind);
            return priority < bestPriority ||
                (priority == bestPriority &&
                 (entityIndex < _bestEntityIndex ||
                  (entityIndex == _bestEntityIndex &&
                   (secondEntityIndex < _bestSecondEntityIndex ||
                    (secondEntityIndex == _bestSecondEntityIndex &&
                     ordinal < _bestOrdinal)))));
        }

        private static int Priority(CadObjectSnapKind kind) => kind switch
        {
            CadObjectSnapKind.Intersection => 0,
            CadObjectSnapKind.Endpoint => 1,
            CadObjectSnapKind.Midpoint => 2,
            CadObjectSnapKind.Center => 3,
            CadObjectSnapKind.Quadrant => 4,
            CadObjectSnapKind.Node => 5,
            CadObjectSnapKind.Nearest => 6,
            _ => int.MaxValue,
        };
    }
}
