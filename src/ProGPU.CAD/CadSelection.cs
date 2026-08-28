namespace ProGPU.CAD;

/// <summary>One immutable broad-phase selection candidate from a document snapshot.</summary>
public readonly record struct CadSelectionCandidate(
    ulong ContentGeneration,
    int EntityIndex,
    ulong Handle,
    CadEntityKind Kind,
    CadBounds3D Bounds);

public readonly record struct CadSelectionQueryResult(
    ulong ContentGeneration,
    int WrittenCount,
    int TotalCount)
{
    public bool IsTruncated => WrittenCount != TotalCount;
}

public readonly record struct CadSelectionHandleResult(
    ulong ContentGeneration,
    int WrittenCount,
    int TotalCount)
{
    public bool IsTruncated => WrittenCount != TotalCount;
}

public readonly record struct CadBoundsSelectionQueryResult(
    ulong ContentGeneration,
    int CandidateWrittenCount,
    int CandidateTotalCount,
    int MatchedPrimitiveCount,
    int UnsupportedPrimitiveCount,
    int HandleWrittenCount,
    int HandleTotalCount)
{
    public bool AreCandidatesTruncated =>
        CandidateWrittenCount != CandidateTotalCount;

    public bool AreHandlesTruncated =>
        HandleWrittenCount != HandleTotalCount;
}

/// <summary>Caller-buffered broad-phase selection over immutable snapshot bounds.</summary>
public static class CadSelectionQuery
{
    /// <summary>Maps intersecting BVH entries to source primitive candidates.</summary>
    /// <remarks>
    /// Work is O(log E + K) on typical spatial data and O(E + K) worst-case for E
    /// snapshot primitives and K intersecting bounds. Expanded block primitives may
    /// share one semantic root handle and remain separate candidates for exact
    /// geometry testing. The smaller buffer capacity controls the written count.
    /// </remarks>
    public static CadSelectionQueryResult QueryBounds(
        CadDocumentSnapshot snapshot,
        CadBounds3D bounds,
        Span<int> entityIndexScratch,
        Span<CadSelectionCandidate> destination)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int capacity = Math.Min(entityIndexScratch.Length, destination.Length);
        CadSpatialQueryResult spatial = snapshot.SpatialIndex.Query(
            bounds,
            entityIndexScratch[..capacity]);
        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        for (int i = 0; i < spatial.WrittenCount; i++)
        {
            int entityIndex = entityIndexScratch[i];
            CadEntityHeader entity = entities[entityIndex];
            destination[i] = new CadSelectionCandidate(
                snapshot.ContentGeneration,
                entityIndex,
                entity.Handle,
                entity.Kind,
                entity.Bounds);
        }

        return new CadSelectionQueryResult(
            snapshot.ContentGeneration,
            spatial.WrittenCount,
            spatial.TotalCount);
    }

    /// <summary>Returns the caller scratch length required to deduplicate candidates.</summary>
    public static int GetUniqueHandleScratchLength(int candidateCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candidateCount);
        if (candidateCount == 0)
        {
            return 0;
        }
        if (candidateCount > 1 << 29)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidateCount),
                "The candidate count is too large for bounded handle scratch.");
        }

        int required = 2;
        while (required < candidateCount * 2)
        {
            required <<= 1;
        }
        return required;
    }

    /// <summary>
    /// Writes each semantic root handle once, preserving its first candidate order.
    /// </summary>
    /// <remarks>
    /// All candidates must belong to one immutable content generation. Scratch uses
    /// open addressing at a maximum 50% load and is cleared by the operation. Work is
    /// O(K) average and O(K^2) worst-case for K primitive candidates; storage is O(K)
    /// in caller-owned spans. Destination capacity affects only WrittenCount, never
    /// TotalCount, so truncation is explicit.
    /// </remarks>
    public static CadSelectionHandleResult CollectUniqueHandles(
        ReadOnlySpan<CadSelectionCandidate> candidates,
        Span<int> hashScratch,
        Span<ulong> destination)
    {
        if (candidates.IsEmpty)
        {
            return default;
        }

        ulong contentGeneration = candidates[0].ContentGeneration;
        for (int i = 1; i < candidates.Length; i++)
        {
            if (candidates[i].ContentGeneration != contentGeneration)
            {
                throw new InvalidOperationException(
                    "Selection candidates from different snapshot generations cannot be combined.");
            }
        }

        int requiredScratch = GetUniqueHandleScratchLength(candidates.Length);
        if (hashScratch.Length < requiredScratch)
        {
            throw new ArgumentException(
                $"At least {requiredScratch} hash scratch entries are required.",
                nameof(hashScratch));
        }

        Span<int> slots = hashScratch[..requiredScratch];
        slots.Clear();
        int mask = slots.Length - 1;
        int written = 0;
        int total = 0;
        for (int candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        {
            ulong handle = candidates[candidateIndex].Handle;
            int slot = (int)(FoldHandle(handle) & (uint)mask);
            while (slots[slot] != 0)
            {
                if (candidates[slots[slot] - 1].Handle == handle)
                {
                    slot = -1;
                    break;
                }
                slot = (slot + 1) & mask;
            }
            if (slot < 0)
            {
                continue;
            }

            slots[slot] = candidateIndex + 1;
            if (written < destination.Length)
            {
                destination[written++] = handle;
            }
            total++;
        }

        return new CadSelectionHandleResult(
            contentGeneration,
            written,
            total);
    }

    /// <summary>
    /// Runs broad phase, exact box testing, and semantic-handle collection in one
    /// caller-buffered operation.
    /// </summary>
    /// <remarks>
    /// Candidate truncation remains explicit and means the exact result covers only
    /// the written candidate prefix. Matched scratch must cover the candidate capacity,
    /// and handle scratch must use <see cref="GetUniqueHandleScratchLength"/> for that
    /// capacity. Unsupported retained kinds are counted and never accepted as hits.
    /// </remarks>
    public static CadBoundsSelectionQueryResult QueryExactBounds(
        CadDocumentSnapshot snapshot,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode,
        Span<int> entityIndexScratch,
        Span<CadSelectionCandidate> candidateScratch,
        Span<CadSelectionCandidate> matchedCandidateScratch,
        Span<int> handleHashScratch,
        Span<ulong> destinationHandles)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (mode is not CadBoundsSelectionMode.Window and not CadBoundsSelectionMode.Crossing)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        int candidateCapacity = Math.Min(
            entityIndexScratch.Length,
            candidateScratch.Length);
        if (matchedCandidateScratch.Length < candidateCapacity)
        {
            throw new ArgumentException(
                "Matched-candidate scratch must cover the broad-phase candidate capacity.",
                nameof(matchedCandidateScratch));
        }
        int requiredHandleScratch = GetUniqueHandleScratchLength(candidateCapacity);
        if (handleHashScratch.Length < requiredHandleScratch)
        {
            throw new ArgumentException(
                $"At least {requiredHandleScratch} handle hash entries are required.",
                nameof(handleHashScratch));
        }

        CadSelectionQueryResult broadPhase = QueryBounds(
            snapshot,
            bounds,
            entityIndexScratch,
            candidateScratch);
        int matchedCount = 0;
        int unsupportedCount = 0;
        for (int i = 0; i < broadPhase.WrittenCount; i++)
        {
            CadBoundsHitResult hit = CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidateScratch[i],
                bounds,
                mode);
            if (hit.IsHit)
            {
                matchedCandidateScratch[matchedCount++] = candidateScratch[i];
            }
            else if (!hit.IsSupported)
            {
                unsupportedCount++;
            }
        }

        CadSelectionHandleResult handles = CollectUniqueHandles(
            matchedCandidateScratch[..matchedCount],
            handleHashScratch,
            destinationHandles);
        return new CadBoundsSelectionQueryResult(
            snapshot.ContentGeneration,
            broadPhase.WrittenCount,
            broadPhase.TotalCount,
            matchedCount,
            unsupportedCount,
            handles.WrittenCount,
            handles.TotalCount);
    }

    private static uint FoldHandle(ulong handle)
    {
        ulong folded = handle ^ (handle >> 32);
        folded ^= folded >> 16;
        return (uint)(folded ^ (folded >> 8));
    }
}

public enum CadPointHitStatus : byte
{
    Miss = 0,
    Hit = 1,
    UnsupportedKind = 2,
    UnsupportedGeometry = 3,
}

public readonly record struct CadPointHitResult(
    CadPointHitStatus Status,
    double Distance)
{
    public bool IsHit => Status == CadPointHitStatus.Hit;

    public bool IsSupported =>
        Status is CadPointHitStatus.Hit or CadPointHitStatus.Miss;
}

/// <summary>Inclusive world-space box-selection behavior.</summary>
public enum CadBoundsSelectionMode : byte
{
    /// <summary>Select only geometry wholly contained by the box.</summary>
    Window = 0,

    /// <summary>Select geometry with any point inside or on the box.</summary>
    Crossing = 1,
}

/// <summary>Typed result of an exact retained-geometry box test.</summary>
public enum CadBoundsHitStatus : byte
{
    Miss = 0,
    Hit = 1,
    UnsupportedKind = 2,
    UnsupportedGeometry = 3,
}

/// <summary>One exact box-test result without hidden approximation.</summary>
public readonly record struct CadBoundsHitResult(CadBoundsHitStatus Status)
{
    public bool IsHit => Status == CadBoundsHitStatus.Hit;

    public bool IsSupported =>
        Status is CadBoundsHitStatus.Hit or CadBoundsHitStatus.Miss;
}

/// <summary>Exact world-space point proximity tests for supported snapshot primitives.</summary>
public static class CadSelectionHitTester
{
    private const double AxisTolerance = 1e-10;
    private const double TwoPi = Math.PI * 2.0;

    public static CadPointHitResult HitTestPoint(
        CadDocumentSnapshot snapshot,
        CadSelectionCandidate candidate,
        CadPoint3D point,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!AreFinite(point))
        {
            throw new ArgumentException("A hit-test point must be finite.", nameof(point));
        }
        if (!double.IsFinite(tolerance) || tolerance < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tolerance),
                "Hit-test tolerance must be finite and non-negative.");
        }
        CadEntityHeader header = GetValidatedHeader(snapshot, candidate);

        return header.Kind switch
        {
            CadEntityKind.Line => FromDistance(
                DistanceToSegment(
                    point,
                    snapshot.Lines.Span[header.PrimitiveIndex].Start,
                    snapshot.Lines.Span[header.PrimitiveIndex].End),
                tolerance),
            CadEntityKind.Circle => HitCircle(
                snapshot.Circles.Span[header.PrimitiveIndex],
                point,
                tolerance),
            CadEntityKind.Arc => HitArc(
                snapshot.Arcs.Span[header.PrimitiveIndex],
                point,
                tolerance),
            CadEntityKind.LightweightPolyline or CadEntityKind.Polyline2D =>
                HitPolyline2D(snapshot, header, point, tolerance),
            CadEntityKind.Polyline3D =>
                HitPolyline3D(snapshot, header, point, tolerance),
            CadEntityKind.Spline => CadSplineSelection.HitTestPoint(
                snapshot,
                snapshot.Splines.Span[header.PrimitiveIndex],
                point,
                tolerance),
            CadEntityKind.Text => CadTextSelection.HitTestTextPoint(
                snapshot,
                snapshot.Texts.Span[header.PrimitiveIndex],
                point,
                tolerance),
            CadEntityKind.MText => CadTextSelection.HitTestMTextPoint(
                snapshot,
                snapshot.MTexts.Span[header.PrimitiveIndex],
                point,
                tolerance),
            CadEntityKind.ShxText => CadTextSelection.HitTestShxPoint(
                snapshot,
                snapshot.ShxTexts.Span[header.PrimitiveIndex],
                point,
                tolerance),
            CadEntityKind.ShxMText => CadTextSelection.HitTestShxMTextPoint(
                snapshot,
                snapshot.ShxMTexts.Span[header.PrimitiveIndex],
                point,
                tolerance),
            CadEntityKind.Solid =>
                HitSolid(snapshot.Faces.Span[header.PrimitiveIndex], point, tolerance),
            CadEntityKind.Face3D =>
                HitFaceEdges(snapshot.Faces.Span[header.PrimitiveIndex], point, tolerance),
            CadEntityKind.Hatch => CadHatchSelection.HitTestPoint(
                snapshot,
                snapshot.Hatches.Span[header.PrimitiveIndex],
                point,
                tolerance),
            _ => new CadPointHitResult(
                CadPointHitStatus.UnsupportedKind,
                double.NaN),
        };
    }

    /// <summary>
    /// Tests one retained primitive against an inclusive world-space selection box.
    /// </summary>
    /// <remarks>
    /// Window mode requires the complete selectable geometry to lie inside the box.
    /// Crossing mode accepts any geometric intersection. Curved crossing tests partition
    /// their bounded parameter interval at exact box-plane roots; filled SOLIDs use the
    /// convex triangle/box separating axes. Work is O(S) for S polyline segments,
    /// O(B * P^2 * R) for B degree-P spline spans, and O(G * T * R) for G glyphs with
    /// T retained outline segments and bounded root work R. Other supported primitives
    /// are O(1); all paths use bounded stack storage and no warm-query allocation.
    /// </remarks>
    public static CadBoundsHitResult HitTestBounds(
        CadDocumentSnapshot snapshot,
        CadSelectionCandidate candidate,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (mode is not CadBoundsSelectionMode.Window and not CadBoundsSelectionMode.Crossing)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        CadEntityHeader header = GetValidatedHeader(snapshot, candidate);

        return header.Kind switch
        {
            CadEntityKind.Line => HitLineBounds(
                snapshot.Lines.Span[header.PrimitiveIndex],
                bounds,
                mode),
            CadEntityKind.Circle => HitCircleBounds(
                snapshot.Circles.Span[header.PrimitiveIndex],
                header.Bounds,
                bounds,
                mode),
            CadEntityKind.Arc => HitArcBounds(
                snapshot.Arcs.Span[header.PrimitiveIndex],
                header.Bounds,
                bounds,
                mode),
            CadEntityKind.Ellipse => HitEllipseBounds(
                snapshot.Ellipses.Span[header.PrimitiveIndex],
                header.Bounds,
                bounds,
                mode),
            CadEntityKind.LightweightPolyline or CadEntityKind.Polyline2D =>
                HitPolyline2DBounds(snapshot, header, bounds, mode),
            CadEntityKind.Polyline3D =>
                HitPolyline3DBounds(snapshot, header, bounds, mode),
            CadEntityKind.Spline => CadSplineSelection.HitTestBounds(
                snapshot,
                snapshot.Splines.Span[header.PrimitiveIndex],
                header.Bounds,
                bounds,
                mode),
            CadEntityKind.Text => CadTextSelection.HitTestTextBounds(
                snapshot,
                snapshot.Texts.Span[header.PrimitiveIndex],
                bounds,
                mode),
            CadEntityKind.MText => CadTextSelection.HitTestMTextBounds(
                snapshot,
                snapshot.MTexts.Span[header.PrimitiveIndex],
                bounds,
                mode),
            CadEntityKind.ShxText => CadTextSelection.HitTestShxBounds(
                snapshot,
                snapshot.ShxTexts.Span[header.PrimitiveIndex],
                bounds,
                mode),
            CadEntityKind.ShxMText => CadTextSelection.HitTestShxMTextBounds(
                snapshot,
                snapshot.ShxMTexts.Span[header.PrimitiveIndex],
                bounds,
                mode),
            CadEntityKind.Solid => HitSolidBounds(
                snapshot.Faces.Span[header.PrimitiveIndex],
                bounds,
                mode),
            CadEntityKind.Face3D => HitFaceBounds(
                snapshot.Faces.Span[header.PrimitiveIndex],
                bounds,
                mode),
            CadEntityKind.Hatch => CadHatchSelection.HitTestBounds(
                snapshot,
                snapshot.Hatches.Span[header.PrimitiveIndex],
                header.Bounds,
                bounds,
                mode),
            _ => new CadBoundsHitResult(CadBoundsHitStatus.UnsupportedKind),
        };
    }

    private static CadBoundsHitResult HitLineBounds(
        CadLinePrimitive line,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode)
    {
        if (bounds.IsEmpty)
        {
            return BoundsMiss();
        }
        bool hit = mode == CadBoundsSelectionMode.Window
            ? ContainsPoint(bounds, line.Start) && ContainsPoint(bounds, line.End)
            : SegmentIntersectsBounds(line.Start, line.End, bounds);
        return FromBoundsHit(hit);
    }

    private static CadBoundsHitResult HitCircleBounds(
        CadCirclePrimitive circle,
        CadBounds3D exactBounds,
        CadBounds3D selectionBounds,
        CadBoundsSelectionMode mode) =>
        HitParametricBounds(
            circle.Center,
            circle.CoordinateSystem.XAxis * circle.Radius,
            circle.CoordinateSystem.YAxis * circle.Radius,
            0.0,
            TwoPi,
            exactBounds,
            selectionBounds,
            mode);

    private static CadBoundsHitResult HitArcBounds(
        CadArcPrimitive arc,
        CadBounds3D exactBounds,
        CadBounds3D selectionBounds,
        CadBoundsSelectionMode mode) =>
        HitParametricBounds(
            arc.Center,
            arc.CoordinateSystem.XAxis * arc.Radius,
            arc.CoordinateSystem.YAxis * arc.Radius,
            arc.StartAngle,
            arc.SweepAngle,
            exactBounds,
            selectionBounds,
            mode);

    private static CadBoundsHitResult HitEllipseBounds(
        CadEllipsePrimitive ellipse,
        CadBounds3D exactBounds,
        CadBounds3D selectionBounds,
        CadBoundsSelectionMode mode) =>
        HitParametricBounds(
            ellipse.Center,
            ellipse.MajorAxis,
            ellipse.MinorAxis,
            ellipse.StartParameter,
            ellipse.SweepParameter,
            exactBounds,
            selectionBounds,
            mode);

    private static CadBoundsHitResult HitParametricBounds(
        CadPoint3D center,
        CadPoint3D cosineAxis,
        CadPoint3D sineAxis,
        double start,
        double sweep,
        CadBounds3D exactBounds,
        CadBounds3D selectionBounds,
        CadBoundsSelectionMode mode)
    {
        if (selectionBounds.IsEmpty)
        {
            return BoundsMiss();
        }
        if (mode == CadBoundsSelectionMode.Window)
        {
            return FromBoundsHit(ContainsBounds(selectionBounds, exactBounds));
        }
        if (!exactBounds.Intersects(selectionBounds))
        {
            return BoundsMiss();
        }
        if (ContainsBounds(selectionBounds, exactBounds))
        {
            return BoundsHit();
        }
        return TryParametricArcIntersectsBounds(
            center,
            cosineAxis,
            sineAxis,
            start,
            sweep,
            selectionBounds,
            out bool intersects)
            ? FromBoundsHit(intersects)
            : BoundsUnsupportedGeometry();
    }

    private static CadBoundsHitResult HitPolyline2DBounds(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode)
    {
        if (bounds.IsEmpty)
        {
            return BoundsMiss();
        }
        if (mode == CadBoundsSelectionMode.Window)
        {
            return FromBoundsHit(ContainsBounds(bounds, header.Bounds));
        }
        if (!header.Bounds.Intersects(bounds))
        {
            return BoundsMiss();
        }

        CadPolylinePrimitive polyline = snapshot.Polylines.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPolylineVertex> vertices = snapshot.PolylineVertices.Span.Slice(
            polyline.VertexOffset,
            polyline.VertexCount);
        int segmentCount = polyline.IsClosed ? vertices.Length : vertices.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            CadPolylineVertex start = vertices[i];
            CadPolylineVertex end = vertices[(i + 1) % vertices.Length];
            if (start.Bulge == 0.0)
            {
                if (SegmentIntersectsBounds(
                        ToWorld(polyline, start),
                        ToWorld(polyline, end),
                        bounds))
                {
                    return BoundsHit();
                }
                continue;
            }

            if (!TryGetBulgeArc(
                    polyline,
                    start,
                    end,
                    out CadPoint3D center,
                    out CadPoint3D cosineAxis,
                    out CadPoint3D sineAxis,
                    out _,
                    out double startAngle,
                    out double sweep))
            {
                return BoundsUnsupportedGeometry();
            }
            if (!TryParametricArcIntersectsBounds(
                    center,
                    cosineAxis,
                    sineAxis,
                    startAngle,
                    sweep,
                    bounds,
                    out bool intersects))
            {
                return BoundsUnsupportedGeometry();
            }
            if (intersects)
            {
                return BoundsHit();
            }
        }
        return BoundsMiss();
    }

    private static CadBoundsHitResult HitPolyline3DBounds(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode)
    {
        if (bounds.IsEmpty)
        {
            return BoundsMiss();
        }
        if (mode == CadBoundsSelectionMode.Window)
        {
            return FromBoundsHit(ContainsBounds(bounds, header.Bounds));
        }
        if (!header.Bounds.Intersects(bounds))
        {
            return BoundsMiss();
        }

        CadPolyline3DPrimitive polyline = snapshot.Polylines3D.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPoint3D> points = snapshot.Polyline3DPoints.Span.Slice(
            polyline.PointOffset,
            polyline.PointCount);
        int segmentCount = polyline.IsClosed ? points.Length : points.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            if (SegmentIntersectsBounds(
                    points[i],
                    points[(i + 1) % points.Length],
                    bounds))
            {
                return BoundsHit();
            }
        }
        return BoundsMiss();
    }

    private static CadBoundsHitResult HitSolidBounds(
        CadFacePrimitive face,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode)
    {
        if (bounds.IsEmpty)
        {
            return BoundsMiss();
        }
        if (mode == CadBoundsSelectionMode.Window)
        {
            return FromBoundsHit(
                ContainsPoint(bounds, face.First) &&
                ContainsPoint(bounds, face.Second) &&
                ContainsPoint(bounds, face.Third) &&
                ContainsPoint(bounds, face.Fourth));
        }

        bool intersects = TriangleIntersectsBounds(
            face.First,
            face.Second,
            face.Third,
            bounds);
        if (!intersects && face.Fourth != face.Third)
        {
            intersects = TriangleIntersectsBounds(
                face.First,
                face.Third,
                face.Fourth,
                bounds);
        }
        return FromBoundsHit(intersects);
    }

    private static CadBoundsHitResult HitFaceBounds(
        CadFacePrimitive face,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode)
    {
        if (bounds.IsEmpty)
        {
            return BoundsMiss();
        }

        bool hasVisibleEdge = false;
        if (!TestFaceEdge(face.First, face.Second, face.InvisibleEdgeMask, 1, bounds, mode, ref hasVisibleEdge) ||
            !TestFaceEdge(face.Second, face.Third, face.InvisibleEdgeMask, 2, bounds, mode, ref hasVisibleEdge) ||
            !TestFaceEdge(face.Third, face.Fourth, face.InvisibleEdgeMask, 4, bounds, mode, ref hasVisibleEdge) ||
            !TestFaceEdge(face.Fourth, face.First, face.InvisibleEdgeMask, 8, bounds, mode, ref hasVisibleEdge))
        {
            return mode == CadBoundsSelectionMode.Crossing ? BoundsHit() : BoundsMiss();
        }
        return FromBoundsHit(hasVisibleEdge && mode == CadBoundsSelectionMode.Window);
    }

    private static bool TestFaceEdge(
        CadPoint3D start,
        CadPoint3D end,
        byte invisibleEdgeMask,
        byte edgeFlag,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode,
        ref bool hasVisibleEdge)
    {
        if ((invisibleEdgeMask & edgeFlag) != 0 || start == end)
        {
            return true;
        }
        hasVisibleEdge = true;
        return mode == CadBoundsSelectionMode.Window
            ? ContainsPoint(bounds, start) && ContainsPoint(bounds, end)
            : !SegmentIntersectsBounds(start, end, bounds);
    }

    private static CadPointHitResult HitCircle(
        CadCirclePrimitive circle,
        CadPoint3D point,
        double tolerance)
    {
        if (!TryGetCircularBasis(
                circle.CoordinateSystem,
                circle.Radius,
                out CircularBasis basis))
        {
            return UnsupportedGeometry();
        }
        CadPoint3D delta = point - circle.Center;
        double x = CadPoint3D.Dot(delta, basis.XAxis);
        double y = CadPoint3D.Dot(delta, basis.YAxis);
        double radial = new CadPoint3D(x, y, 0.0).Length;
        double plane = Math.Abs(CadPoint3D.Dot(delta, basis.Normal));
        double distance = new CadPoint3D(
            radial - basis.Radius,
            plane,
            0.0).Length;
        return FromDistance(distance, tolerance);
    }

    private static CadPointHitResult HitArc(
        CadArcPrimitive arc,
        CadPoint3D point,
        double tolerance)
    {
        if (!TryGetCircularBasis(
                arc.CoordinateSystem,
                arc.Radius,
                out CircularBasis basis))
        {
            return UnsupportedGeometry();
        }
        return FromDistance(
            DistanceToCircularArc(
                point,
                arc.Center,
                basis,
                arc.StartAngle,
                arc.SweepAngle),
            tolerance);
    }

    private static CadPointHitResult HitPolyline2D(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        CadPoint3D point,
        double tolerance)
    {
        CadPolylinePrimitive polyline = snapshot.Polylines.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPolylineVertex> vertices = snapshot.PolylineVertices.Span.Slice(
            polyline.VertexOffset,
            polyline.VertexCount);
        if (vertices.Length == 0)
        {
            return FromDistance(double.PositiveInfinity, tolerance);
        }
        if (vertices.Length == 1)
        {
            return FromDistance(
                (point - ToWorld(polyline, vertices[0])).Length,
                tolerance);
        }

        double minimum = double.PositiveInfinity;
        bool hasUnsupportedBulge = false;
        int segmentCount = polyline.IsClosed ? vertices.Length : vertices.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            CadPolylineVertex start = vertices[i];
            CadPolylineVertex end = vertices[(i + 1) % vertices.Length];
            if (start.Bulge != 0.0)
            {
                if (TryDistanceToBulge(
                        point,
                        polyline,
                        start,
                        end,
                        out double bulgeDistance))
                {
                    minimum = Math.Min(minimum, bulgeDistance);
                }
                else
                {
                    hasUnsupportedBulge = true;
                }
                continue;
            }
            minimum = Math.Min(
                minimum,
                DistanceToSegment(
                    point,
                    ToWorld(polyline, start),
                    ToWorld(polyline, end)));
        }
        if (minimum <= tolerance)
        {
            return FromDistance(minimum, tolerance);
        }
        return hasUnsupportedBulge
            ? UnsupportedGeometry()
            : FromDistance(minimum, tolerance);
    }

    private static bool TryDistanceToBulge(
        CadPoint3D point,
        CadPolylinePrimitive polyline,
        CadPolylineVertex start,
        CadPolylineVertex end,
        out double distance)
    {
        if (!TryGetBulgeArc(
                polyline,
                start,
                end,
                out CadPoint3D center,
                out CadPoint3D cosineAxis,
                out CadPoint3D sineAxis,
                out double localRadius,
                out double startAngle,
                out double sweep))
        {
            distance = double.NaN;
            return false;
        }
        if (!TryGetCircularBasis(
                polyline.CoordinateSystem,
                localRadius,
                out CircularBasis basis))
        {
            distance = double.NaN;
            return false;
        }
        distance = DistanceToCircularArc(
            point,
            center,
            basis,
            startAngle,
            sweep);
        return double.IsFinite(distance);
    }

    private static bool TryGetBulgeArc(
        CadPolylinePrimitive polyline,
        CadPolylineVertex start,
        CadPolylineVertex end,
        out CadPoint3D center,
        out CadPoint3D cosineAxis,
        out CadPoint3D sineAxis,
        out double radius,
        out double startAngle,
        out double sweep)
    {
        double bulge = start.Bulge;
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double chord = new CadPoint3D(deltaX, deltaY, 0.0).Length;
        if (!double.IsFinite(bulge) || bulge == 0.0 ||
            !double.IsFinite(chord) || chord == 0.0)
        {
            center = default;
            cosineAxis = default;
            sineAxis = default;
            radius = double.NaN;
            startAngle = double.NaN;
            sweep = double.NaN;
            return false;
        }

        double inverseBulge = 1.0 / bulge;
        double centerOffset = (chord * 0.25) * (inverseBulge - bulge);
        double localRadius = (chord * 0.25) *
            (Math.Abs(bulge) + Math.Abs(inverseBulge));
        double centerX = (start.X * 0.5) + (end.X * 0.5) -
            ((deltaY / chord) * centerOffset);
        double centerY = (start.Y * 0.5) + (end.Y * 0.5) +
            ((deltaX / chord) * centerOffset);
        if (!double.IsFinite(centerOffset) || !double.IsFinite(localRadius) ||
            localRadius <= 0.0 ||
            !double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            center = default;
            cosineAxis = default;
            sineAxis = default;
            radius = double.NaN;
            startAngle = double.NaN;
            sweep = double.NaN;
            return false;
        }

        center = ToWorld(polyline, centerX, centerY);
        cosineAxis = polyline.CoordinateSystem.XAxis * localRadius;
        sineAxis = polyline.CoordinateSystem.YAxis * localRadius;
        radius = localRadius;
        startAngle = Math.Atan2(start.Y - centerY, start.X - centerX);
        sweep = 4.0 * Math.Atan(bulge);
        return AreFinite(center) &&
            AreFinite(cosineAxis) &&
            AreFinite(sineAxis) &&
            double.IsFinite(startAngle) &&
            double.IsFinite(sweep);
    }

    private static CadPointHitResult HitPolyline3D(
        CadDocumentSnapshot snapshot,
        CadEntityHeader header,
        CadPoint3D point,
        double tolerance)
    {
        CadPolyline3DPrimitive polyline = snapshot.Polylines3D.Span[header.PrimitiveIndex];
        ReadOnlySpan<CadPoint3D> points = snapshot.Polyline3DPoints.Span.Slice(
            polyline.PointOffset,
            polyline.PointCount);
        if (points.Length == 0)
        {
            return FromDistance(double.PositiveInfinity, tolerance);
        }
        if (points.Length == 1)
        {
            return FromDistance((point - points[0]).Length, tolerance);
        }

        double minimum = double.PositiveInfinity;
        int segmentCount = polyline.IsClosed ? points.Length : points.Length - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            minimum = Math.Min(
                minimum,
                DistanceToSegment(
                    point,
                    points[i],
                    points[(i + 1) % points.Length]));
        }
        return FromDistance(minimum, tolerance);
    }

    private static CadPointHitResult HitSolid(
        CadFacePrimitive face,
        CadPoint3D point,
        double tolerance)
    {
        double distance = DistanceToTriangle(
            point,
            face.First,
            face.Second,
            face.Third);
        if (face.Fourth != face.Third)
        {
            distance = Math.Min(
                distance,
                DistanceToTriangle(
                    point,
                    face.First,
                    face.Third,
                    face.Fourth));
        }
        return FromDistance(distance, tolerance);
    }

    private static CadPointHitResult HitFaceEdges(
        CadFacePrimitive face,
        CadPoint3D point,
        double tolerance)
    {
        double minimum = double.PositiveInfinity;
        IncludeVisibleEdge(ref minimum, point, face.First, face.Second, face.InvisibleEdgeMask, 1);
        IncludeVisibleEdge(ref minimum, point, face.Second, face.Third, face.InvisibleEdgeMask, 2);
        IncludeVisibleEdge(ref minimum, point, face.Third, face.Fourth, face.InvisibleEdgeMask, 4);
        IncludeVisibleEdge(ref minimum, point, face.Fourth, face.First, face.InvisibleEdgeMask, 8);
        return FromDistance(minimum, tolerance);
    }

    private static void IncludeVisibleEdge(
        ref double minimum,
        CadPoint3D point,
        CadPoint3D start,
        CadPoint3D end,
        byte invisibleEdgeMask,
        byte edgeFlag)
    {
        if ((invisibleEdgeMask & edgeFlag) == 0 && start != end)
        {
            minimum = Math.Min(minimum, DistanceToSegment(point, start, end));
        }
    }

    private static CadPoint3D ToWorld(
        CadPolylinePrimitive polyline,
        CadPolylineVertex vertex) => ToWorld(polyline, vertex.X, vertex.Y);

    private static CadPoint3D ToWorld(
        CadPolylinePrimitive polyline,
        double x,
        double y) =>
        polyline.WorldOrigin + polyline.CoordinateSystem.Transform(
            new CadPoint3D(x, y, 0.0));

    internal static bool TryParametricArcIntersectsBounds(
        CadPoint3D center,
        CadPoint3D cosineAxis,
        CadPoint3D sineAxis,
        double start,
        double sweep,
        CadBounds3D bounds,
        out bool intersects)
    {
        intersects = false;
        if (bounds.IsEmpty ||
            !AreFinite(center) ||
            !AreFinite(cosineAxis) ||
            !AreFinite(sineAxis) ||
            !double.IsFinite(start) ||
            !double.IsFinite(sweep) ||
            Math.Abs(sweep) > TwoPi + 1e-12)
        {
            return false;
        }

        double span = Math.Min(Math.Abs(sweep), TwoPi);
        Span<double> partitions = stackalloc double[14];
        int count = 0;
        partitions[count++] = 0.0;
        partitions[count++] = span;

        for (int axis = 0; axis < 3; axis++)
        {
            double centerValue = Component(center, axis);
            double cosine = Component(cosineAxis, axis);
            double sine = Component(sineAxis, axis);
            double amplitude = new CadPoint3D(cosine, sine, 0.0).Length;
            double minimum = Component(bounds.Min, axis);
            double maximum = Component(bounds.Max, axis);
            if (!double.IsFinite(amplitude))
            {
                return false;
            }
            if (amplitude == 0.0)
            {
                if (!ContainsCoordinate(centerValue, minimum, maximum, useTolerance: true))
                {
                    return true;
                }
                continue;
            }

            if (!AddPlaneRoots(
                    centerValue,
                    cosine,
                    sine,
                    amplitude,
                    minimum,
                    start,
                    sweep,
                    span,
                    partitions,
                    ref count) ||
                !AddPlaneRoots(
                    centerValue,
                    cosine,
                    sine,
                    amplitude,
                    maximum,
                    start,
                    sweep,
                    span,
                    partitions,
                    ref count))
            {
                return false;
            }
        }

        InsertionSort(partitions[..count]);
        for (int i = 0; i < count; i++)
        {
            if (ContainsParametricPoint(
                    center,
                    cosineAxis,
                    sineAxis,
                    start,
                    sweep,
                    partitions[i],
                    bounds))
            {
                intersects = true;
                return true;
            }
            if (i + 1 < count && partitions[i + 1] > partitions[i])
            {
                double midpoint =
                    (partitions[i] * 0.5) + (partitions[i + 1] * 0.5);
                if (ContainsParametricPoint(
                        center,
                        cosineAxis,
                        sineAxis,
                        start,
                        sweep,
                        midpoint,
                        bounds))
                {
                    intersects = true;
                    return true;
                }
            }
        }
        return true;
    }

    private static bool AddPlaneRoots(
        double center,
        double cosine,
        double sine,
        double amplitude,
        double boundary,
        double start,
        double sweep,
        double span,
        Span<double> partitions,
        ref int count)
    {
        double normalized = (boundary - center) / amplitude;
        double tolerance = CoordinateTolerance(normalized, -1.0, 1.0);
        if (normalized < -1.0 - tolerance || normalized > 1.0 + tolerance)
        {
            return true;
        }

        normalized = Math.Clamp(normalized, -1.0, 1.0);
        double phase = Math.Atan2(sine, cosine);
        double delta = Math.Acos(normalized);
        return AddPartition(phase + delta, start, sweep, span, partitions, ref count) &&
            AddPartition(phase - delta, start, sweep, span, partitions, ref count);
    }

    private static bool AddPartition(
        double angle,
        double start,
        double sweep,
        double span,
        Span<double> partitions,
        ref int count)
    {
        double progress = sweep >= 0.0
            ? NormalizePositive(angle - start)
            : NormalizePositive(start - angle);
        double tolerance = CoordinateTolerance(progress, 0.0, span);
        if (progress > span + tolerance)
        {
            return true;
        }
        progress = Math.Clamp(progress, 0.0, span);
        if (count >= partitions.Length)
        {
            return false;
        }
        partitions[count++] = progress;
        return true;
    }

    private static bool ContainsParametricPoint(
        CadPoint3D center,
        CadPoint3D cosineAxis,
        CadPoint3D sineAxis,
        double start,
        double sweep,
        double progress,
        CadBounds3D bounds)
    {
        double angle = start + Math.CopySign(progress, sweep == 0.0 ? 1.0 : sweep);
        CadPoint3D point = center +
            (cosineAxis * Math.Cos(angle)) +
            (sineAxis * Math.Sin(angle));
        return ContainsPoint(bounds, point, useTolerance: true);
    }

    private static void InsertionSort(Span<double> values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            double value = values[i];
            int destination = i;
            while (destination > 0 && values[destination - 1] > value)
            {
                values[destination] = values[destination - 1];
                destination--;
            }
            values[destination] = value;
        }
    }

    internal static bool SegmentIntersectsBounds(
        CadPoint3D start,
        CadPoint3D end,
        CadBounds3D bounds)
    {
        if (bounds.IsEmpty)
        {
            return false;
        }

        CadPoint3D direction = end - start;
        double minimumParameter = 0.0;
        double maximumParameter = 1.0;
        for (int axis = 0; axis < 3; axis++)
        {
            double origin = Component(start, axis);
            double delta = Component(direction, axis);
            double minimum = Component(bounds.Min, axis);
            double maximum = Component(bounds.Max, axis);
            if (delta == 0.0)
            {
                if (origin < minimum || origin > maximum)
                {
                    return false;
                }
                continue;
            }

            double first = (minimum - origin) / delta;
            double second = (maximum - origin) / delta;
            if (first > second)
            {
                (first, second) = (second, first);
            }
            minimumParameter = Math.Max(minimumParameter, first);
            maximumParameter = Math.Min(maximumParameter, second);
            if (minimumParameter > maximumParameter)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TriangleIntersectsBounds(
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third,
        CadBounds3D bounds)
    {
        CadPoint3D center = bounds.Center;
        CadPoint3D halfExtent = new(
            (bounds.Max.X * 0.5) - (bounds.Min.X * 0.5),
            (bounds.Max.Y * 0.5) - (bounds.Min.Y * 0.5),
            (bounds.Max.Z * 0.5) - (bounds.Min.Z * 0.5));
        CadPoint3D a = first - center;
        CadPoint3D b = second - center;
        CadPoint3D c = third - center;
        CadPoint3D firstEdge = b - a;
        CadPoint3D secondEdge = c - b;
        CadPoint3D thirdEdge = a - c;

        if (SeparatesTriangleAndBounds(a, b, c, new CadPoint3D(1.0, 0.0, 0.0), halfExtent) ||
            SeparatesTriangleAndBounds(a, b, c, new CadPoint3D(0.0, 1.0, 0.0), halfExtent) ||
            SeparatesTriangleAndBounds(a, b, c, new CadPoint3D(0.0, 0.0, 1.0), halfExtent) ||
            SeparatesTriangleAndBounds(a, b, c, CadPoint3D.Cross(firstEdge, secondEdge), halfExtent))
        {
            return false;
        }

        return !EdgeAxesSeparate(a, b, c, firstEdge, halfExtent) &&
            !EdgeAxesSeparate(a, b, c, secondEdge, halfExtent) &&
            !EdgeAxesSeparate(a, b, c, thirdEdge, halfExtent);
    }

    private static bool EdgeAxesSeparate(
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third,
        CadPoint3D edge,
        CadPoint3D halfExtent) =>
        SeparatesTriangleAndBounds(
            first,
            second,
            third,
            CadPoint3D.Cross(edge, new CadPoint3D(1.0, 0.0, 0.0)),
            halfExtent) ||
        SeparatesTriangleAndBounds(
            first,
            second,
            third,
            CadPoint3D.Cross(edge, new CadPoint3D(0.0, 1.0, 0.0)),
            halfExtent) ||
        SeparatesTriangleAndBounds(
            first,
            second,
            third,
            CadPoint3D.Cross(edge, new CadPoint3D(0.0, 0.0, 1.0)),
            halfExtent);

    private static bool SeparatesTriangleAndBounds(
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third,
        CadPoint3D axis,
        CadPoint3D halfExtent)
    {
        if (axis == CadPoint3D.Zero)
        {
            return false;
        }
        double firstProjection = CadPoint3D.Dot(first, axis);
        double secondProjection = CadPoint3D.Dot(second, axis);
        double thirdProjection = CadPoint3D.Dot(third, axis);
        double minimum = Math.Min(firstProjection, Math.Min(secondProjection, thirdProjection));
        double maximum = Math.Max(firstProjection, Math.Max(secondProjection, thirdProjection));
        double radius =
            (halfExtent.X * Math.Abs(axis.X)) +
            (halfExtent.Y * Math.Abs(axis.Y)) +
            (halfExtent.Z * Math.Abs(axis.Z));
        return minimum > radius || maximum < -radius;
    }

    private static bool ContainsBounds(CadBounds3D outer, CadBounds3D inner) =>
        !outer.IsEmpty && !inner.IsEmpty &&
        ContainsPoint(outer, inner.Min) &&
        ContainsPoint(outer, inner.Max);

    private static bool ContainsPoint(
        CadBounds3D bounds,
        CadPoint3D point,
        bool useTolerance = false) =>
        !bounds.IsEmpty &&
        ContainsCoordinate(point.X, bounds.Min.X, bounds.Max.X, useTolerance) &&
        ContainsCoordinate(point.Y, bounds.Min.Y, bounds.Max.Y, useTolerance) &&
        ContainsCoordinate(point.Z, bounds.Min.Z, bounds.Max.Z, useTolerance);

    private static bool ContainsCoordinate(
        double value,
        double minimum,
        double maximum,
        bool useTolerance)
    {
        if (!useTolerance)
        {
            return value >= minimum && value <= maximum;
        }
        double tolerance = CoordinateTolerance(value, minimum, maximum);
        return value >= minimum - tolerance && value <= maximum + tolerance;
    }

    private static double CoordinateTolerance(
        double value,
        double minimum,
        double maximum) =>
        1.4210854715202004e-14 * Math.Max(
            1.0,
            Math.Max(Math.Abs(value), Math.Max(Math.Abs(minimum), Math.Abs(maximum))));

    private static double Component(CadPoint3D point, int axis) => axis switch
    {
        0 => point.X,
        1 => point.Y,
        _ => point.Z,
    };

    private static double DistanceToCircularArc(
        CadPoint3D point,
        CadPoint3D center,
        CircularBasis basis,
        double startAngle,
        double sweepAngle)
    {
        CadPoint3D delta = point - center;
        double x = CadPoint3D.Dot(delta, basis.XAxis);
        double y = CadPoint3D.Dot(delta, basis.YAxis);
        double radial = new CadPoint3D(x, y, 0.0).Length;
        double angle = radial == 0.0 ? startAngle : Math.Atan2(y, x);
        if (ContainsAngle(startAngle, sweepAngle, angle))
        {
            double plane = Math.Abs(CadPoint3D.Dot(delta, basis.Normal));
            return new CadPoint3D(
                radial - basis.Radius,
                plane,
                0.0).Length;
        }

        CadPoint3D start = PointOnCircle(center, basis, startAngle);
        CadPoint3D end = PointOnCircle(center, basis, startAngle + sweepAngle);
        return Math.Min((point - start).Length, (point - end).Length);
    }

    private static CadPoint3D PointOnCircle(
        CadPoint3D center,
        CircularBasis basis,
        double angle) =>
        center +
        (basis.XAxis * (basis.Radius * Math.Cos(angle))) +
        (basis.YAxis * (basis.Radius * Math.Sin(angle)));

    private static double DistanceToSegment(
        CadPoint3D point,
        CadPoint3D start,
        CadPoint3D end)
    {
        CadPoint3D segment = end - start;
        double length = segment.Length;
        if (length == 0.0)
        {
            return (point - start).Length;
        }
        CadPoint3D direction = segment / length;
        double projection = CadPoint3D.Dot(point - start, direction);
        if (projection <= 0.0)
        {
            return (point - start).Length;
        }
        if (projection >= length)
        {
            return (point - end).Length;
        }
        return (point - (start + (direction * projection))).Length;
    }

    private static double DistanceToTriangle(
        CadPoint3D point,
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third)
    {
        CadPoint3D firstToSecond = second - first;
        CadPoint3D firstToThird = third - first;
        CadPoint3D normal = CadPoint3D.Cross(firstToSecond, firstToThird);
        double normalLength = normal.Length;
        if (!double.IsFinite(normalLength) || normalLength == 0.0)
        {
            return Math.Min(
                DistanceToSegment(point, first, second),
                Math.Min(
                    DistanceToSegment(point, second, third),
                    DistanceToSegment(point, third, first)));
        }

        CadPoint3D unitNormal = normal / normalLength;
        double signedPlaneDistance = CadPoint3D.Dot(point - first, unitNormal);
        if (!double.IsFinite(signedPlaneDistance))
        {
            return double.PositiveInfinity;
        }
        CadPoint3D projected = point - (unitNormal * signedPlaneDistance);
        CadPoint3D fromFirst = projected - first;
        double secondSquared = CadPoint3D.Dot(firstToSecond, firstToSecond);
        double secondThird = CadPoint3D.Dot(firstToSecond, firstToThird);
        double thirdSquared = CadPoint3D.Dot(firstToThird, firstToThird);
        double projectedSecond = CadPoint3D.Dot(fromFirst, firstToSecond);
        double projectedThird = CadPoint3D.Dot(fromFirst, firstToThird);
        double denominator =
            (secondSquared * thirdSquared) - (secondThird * secondThird);
        if (!double.IsFinite(denominator) || denominator <= 0.0)
        {
            return Math.Min(
                DistanceToSegment(point, first, second),
                Math.Min(
                    DistanceToSegment(point, second, third),
                    DistanceToSegment(point, third, first)));
        }
        double secondWeight =
            ((thirdSquared * projectedSecond) - (secondThird * projectedThird)) /
            denominator;
        double thirdWeight =
            ((secondSquared * projectedThird) - (secondThird * projectedSecond)) /
            denominator;
        if (secondWeight >= 0.0 &&
            thirdWeight >= 0.0 &&
            secondWeight + thirdWeight <= 1.0)
        {
            return Math.Abs(signedPlaneDistance);
        }

        return Math.Min(
            DistanceToSegment(point, first, second),
            Math.Min(
                DistanceToSegment(point, second, third),
                DistanceToSegment(point, third, first)));
    }

    private static bool TryGetCircularBasis(
        CadCoordinateSystem coordinateSystem,
        double radius,
        out CircularBasis basis)
    {
        double xLength = coordinateSystem.XAxis.Length;
        double yLength = coordinateSystem.YAxis.Length;
        if (!double.IsFinite(radius) || radius < 0.0 ||
            !double.IsFinite(xLength) || !double.IsFinite(yLength) ||
            xLength == 0.0 || yLength == 0.0)
        {
            basis = default;
            return false;
        }
        CadPoint3D xAxis = coordinateSystem.XAxis / xLength;
        CadPoint3D yAxis = coordinateSystem.YAxis / yLength;
        double scale = Math.Max(xLength, yLength);
        if (Math.Abs(xLength - yLength) > AxisTolerance * scale ||
            Math.Abs(CadPoint3D.Dot(xAxis, yAxis)) > AxisTolerance)
        {
            basis = default;
            return false;
        }
        CadPoint3D normal = CadPoint3D.Cross(xAxis, yAxis);
        double normalLength = normal.Length;
        if (!double.IsFinite(normalLength) || normalLength == 0.0)
        {
            basis = default;
            return false;
        }
        basis = new CircularBasis(
            xAxis,
            yAxis,
            normal / normalLength,
            radius * ((xLength + yLength) * 0.5));
        return double.IsFinite(basis.Radius);
    }

    private static bool ContainsAngle(double start, double sweep, double angle)
    {
        if (Math.Abs(sweep) >= TwoPi)
        {
            return true;
        }
        return sweep >= 0.0
            ? NormalizePositive(angle - start) <= sweep
            : NormalizePositive(start - angle) <= -sweep;
    }

    private static double NormalizePositive(double angle)
    {
        double normalized = angle % TwoPi;
        return normalized < 0.0 ? normalized + TwoPi : normalized;
    }

    private static CadEntityHeader GetValidatedHeader(
        CadDocumentSnapshot snapshot,
        CadSelectionCandidate candidate)
    {
        if (candidate.ContentGeneration != snapshot.ContentGeneration)
        {
            throw new InvalidOperationException(
                "The selection candidate belongs to a different snapshot generation.");
        }

        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        if ((uint)candidate.EntityIndex >= (uint)entities.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidate),
                "The selection candidate entity index is outside the snapshot.");
        }
        CadEntityHeader header = entities[candidate.EntityIndex];
        if (candidate.Handle != header.Handle ||
            candidate.Kind != header.Kind ||
            candidate.Bounds != header.Bounds)
        {
            throw new InvalidOperationException(
                "The selection candidate does not match its snapshot entity.");
        }
        return header;
    }

    private static CadPointHitResult FromDistance(double distance, double tolerance) =>
        new(
            distance <= tolerance ? CadPointHitStatus.Hit : CadPointHitStatus.Miss,
            distance);

    private static CadPointHitResult UnsupportedGeometry() =>
        new(CadPointHitStatus.UnsupportedGeometry, double.NaN);

    private static CadBoundsHitResult FromBoundsHit(bool hit) =>
        new(hit ? CadBoundsHitStatus.Hit : CadBoundsHitStatus.Miss);

    private static CadBoundsHitResult BoundsHit() =>
        new(CadBoundsHitStatus.Hit);

    private static CadBoundsHitResult BoundsMiss() =>
        new(CadBoundsHitStatus.Miss);

    private static CadBoundsHitResult BoundsUnsupportedGeometry() =>
        new(CadBoundsHitStatus.UnsupportedGeometry);

    private static bool AreFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);

    private readonly record struct CircularBasis(
        CadPoint3D XAxis,
        CadPoint3D YAxis,
        CadPoint3D Normal,
        double Radius);
}
