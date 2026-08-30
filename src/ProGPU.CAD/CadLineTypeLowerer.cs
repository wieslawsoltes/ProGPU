using System.Buffers;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.CAD;

internal enum CadLineTypeLoweringStatus : byte
{
    Lowered = 0,
    Continuous = 1,
    UnsupportedEntity = 2,
    FigureLimitExceeded = 3,
    PatternStepLimitExceeded = 4,
    SourceSegmentLimitExceeded = 5,
    ArcMapLimitExceeded = 6,
    PlacementLimitExceeded = 7,
    UnresolvedComplexElement = 8,
}

internal readonly record struct CadLineTypePlacement(
    int ElementIndex,
    Vector2 Origin,
    Vector2 Tangent);

internal readonly record struct CadLineTypeSplineFragment(
    int ControlPointOffset,
    int ControlPointCount,
    int KnotOffset,
    int KnotCount,
    int WeightOffset,
    int WeightCount,
    int Degree);

internal readonly record struct CadLineTypeLoweringResult(
    CadLineTypeLoweringStatus Status,
    PathGeometry? Path,
    Matrix4x4 Transform,
    int FigureCount,
    int PatternStepCount,
    int SourceSegmentCount,
    CadLineTypePlacement[]? Placements = null,
    int PlacementCount = 0,
    CadLineTypeSplineFragment[]? SplineFragments = null,
    Vector2[]? SplineControlPoints = null,
    double[]? SplineKnots = null,
    double[]? SplineWeights = null);

/// <summary>
/// Lowers AutoCAD A-aligned model-space patterns into retained analytic path
/// figures and tangent-aware complex-element placements. This is an original
/// ProGPU implementation based on Autodesk's public linetype contract; it does
/// not call or reproduce another renderer's linetype expansion implementation.
/// </summary>
/// <remarks>
/// For S source segments, Q visited pattern descriptors, F emitted figures, and
/// P complex placements, work is O(S + Q + (F + P) log S) and retained storage
/// is O(F + P). Circular and elliptic source
/// arcs remain analytic <see cref="ArcSegment"/> values. Arc-length inversion
/// uses a fixed 128-bin Gauss-Legendre map per source arc, giving bounded O(1)
/// work per emitted endpoint without viewport tessellation. Figure counting is
/// performed before allocation and stops at the caller's configured figure or
/// pattern-descriptor traversal limit; source-segment and arc-map limits are
/// checked before proportional scratch storage is rented.
/// Higher-degree open rational splines delegate to
/// <see cref="CadNurbsLineTypeLowerer"/>, whose separate complexity contract
/// accounts for exact rational subcurve storage.
/// </remarks>
internal static class CadLineTypeLowerer
{
    private enum CountStatus : byte
    {
        Success,
        FigureLimitExceeded,
        PatternStepLimitExceeded,
        PlacementLimitExceeded,
    }

    private const double TwoPi = Math.PI * 2.0;
    private const int ArcLengthBinCount = 128;
    private const int ArcLengthMapSize = ArcLengthBinCount + 1;

    public static CadLineTypeLoweringResult Lower(
        CadDocumentSnapshot snapshot,
        in CadEntityHeader entity,
        in CadStrokeStyle style,
        in CadLineTypePattern pattern,
        int maxFigures,
        int maxPatternSteps,
        int maxSourceSegments,
        int maxArcMapsPerEntity,
        int maxPlacements)
    {
        ReadOnlySpan<CadLineTypeElement> elements = snapshot.LineTypeElements.Span.Slice(
            pattern.ElementOffset,
            pattern.ElementCount);
        if (HasUnresolvedComplexElement(elements))
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.UnresolvedComplexElement,
                null,
                Matrix4x4.Identity,
                0,
                0,
                0);
        }

        if (!NeedsLowering(elements))
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.Continuous,
                null,
                Matrix4x4.Identity,
                0,
                0,
                0);
        }

        if (maxFigures <= 0)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.FigureLimitExceeded,
                null,
                Matrix4x4.Identity,
                0,
                0,
                0);
        }

        if (maxPatternSteps <= 0)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.PatternStepLimitExceeded,
                null,
                Matrix4x4.Identity,
                0,
                0,
                0);
        }

        if (HasComplexElement(elements) && maxPlacements <= 0)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.PlacementLimitExceeded,
                null,
                Matrix4x4.Identity,
                0,
                0,
                0);
        }

        int sourceSegmentCount = GetSourceSegmentCount(snapshot, entity);
        if (sourceSegmentCount > maxSourceSegments)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.SourceSegmentLimitExceeded,
                null,
                Matrix4x4.Identity,
                0,
                0,
                sourceSegmentCount);
        }

        if (entity.Kind is CadEntityKind.LightweightPolyline or CadEntityKind.Polyline2D &&
            CountPolylineArcMaps(snapshot, entity) > maxArcMapsPerEntity)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.ArcMapLimitExceeded,
                null,
                Matrix4x4.Identity,
                0,
                0,
                sourceSegmentCount);
        }

        if (entity.Kind == CadEntityKind.Spline &&
            sourceSegmentCount > maxArcMapsPerEntity)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.ArcMapLimitExceeded,
                null,
                Matrix4x4.Identity,
                0,
                0,
                sourceSegmentCount);
        }

        CadLineTypeLoweringResult result = entity.Kind switch
        {
            CadEntityKind.Line => LowerLine(
                snapshot.Lines.Span[entity.PrimitiveIndex],
                snapshot.RebaseOrigin,
                elements,
                pattern.PatternLength,
                style.LineTypeScale,
                maxFigures,
                maxPatternSteps,
                maxPlacements),
            CadEntityKind.Circle => LowerCircle(
                snapshot.Circles.Span[entity.PrimitiveIndex],
                snapshot.RebaseOrigin,
                elements,
                pattern.PatternLength,
                style.LineTypeScale,
                maxFigures,
                maxPatternSteps,
                maxPlacements),
            CadEntityKind.Arc => LowerArc(
                snapshot.Arcs.Span[entity.PrimitiveIndex],
                snapshot.RebaseOrigin,
                elements,
                pattern.PatternLength,
                style.LineTypeScale,
                maxFigures,
                maxPatternSteps,
                maxPlacements),
            CadEntityKind.Ellipse => LowerEllipse(
                snapshot.Ellipses.Span[entity.PrimitiveIndex],
                snapshot.RebaseOrigin,
                elements,
                pattern.PatternLength,
                style.LineTypeScale,
                maxFigures,
                maxPatternSteps,
                maxPlacements),
            CadEntityKind.LightweightPolyline or CadEntityKind.Polyline2D => LowerPolyline(
                snapshot,
                snapshot.Polylines.Span[entity.PrimitiveIndex],
                elements,
                pattern.PatternLength,
                style.LineTypeScale,
                maxFigures,
                maxPatternSteps,
                maxPlacements),
            CadEntityKind.Spline => LowerSpline(
                snapshot,
                snapshot.Splines.Span[entity.PrimitiveIndex],
                elements,
                pattern.PatternLength,
                style.LineTypeScale,
                maxFigures,
                maxPatternSteps,
                maxArcMapsPerEntity,
                maxPlacements),
            CadEntityKind.Polyline3D => LowerPolyline3D(
                snapshot,
                snapshot.Polylines3D.Span[entity.PrimitiveIndex],
                elements,
                pattern.PatternLength,
                style.LineTypeScale,
                maxFigures,
                maxPatternSteps,
                maxPlacements),
            CadEntityKind.Face3D => LowerFace(
                snapshot.Faces.Span[entity.PrimitiveIndex],
                snapshot.RebaseOrigin,
                elements,
                pattern.PatternLength,
                style.LineTypeScale,
                maxFigures,
                maxPatternSteps,
                maxPlacements),
            CadEntityKind.Wipeout => LowerWipeoutFrame(
                snapshot,
                snapshot.Wipeouts.Span[entity.PrimitiveIndex],
                elements,
                pattern.PatternLength,
                style.LineTypeScale,
                maxFigures,
                maxPatternSteps,
                maxPlacements),
            CadEntityKind.RasterImage => LowerRasterImageFrame(
                snapshot,
                snapshot.RasterImages.Span[entity.PrimitiveIndex],
                elements,
                pattern.PatternLength,
                style.LineTypeScale,
                maxFigures,
                maxPatternSteps,
                maxPlacements),
            _ => new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.UnsupportedEntity,
                null,
                Matrix4x4.Identity,
                0,
                0,
                0),
        };
        return result with { SourceSegmentCount = sourceSegmentCount };
    }

    private static int GetSourceSegmentCount(
        CadDocumentSnapshot snapshot,
        in CadEntityHeader entity) =>
        entity.Kind switch
        {
            CadEntityKind.Line or CadEntityKind.Circle or CadEntityKind.Arc or
                CadEntityKind.Ellipse => 1,
            CadEntityKind.LightweightPolyline or CadEntityKind.Polyline2D =>
                GetPolylineSegmentCount(snapshot.Polylines.Span[entity.PrimitiveIndex]),
            CadEntityKind.Spline => GetSplineSegmentCount(
                snapshot,
                snapshot.Splines.Span[entity.PrimitiveIndex]),
            CadEntityKind.Polyline3D =>
                GetPolylineSegmentCount(snapshot.Polylines3D.Span[entity.PrimitiveIndex]),
            CadEntityKind.Face3D => CountVisibleFaceEdges(
                snapshot.Faces.Span[entity.PrimitiveIndex]),
            CadEntityKind.Wipeout =>
                snapshot.Wipeouts.Span[entity.PrimitiveIndex].IsClipped
                    ? snapshot.Wipeouts.Span[entity.PrimitiveIndex].ClipPointCount
                    : 4,
            CadEntityKind.RasterImage =>
                snapshot.RasterImages.Span[entity.PrimitiveIndex].IsClipped
                    ? snapshot.RasterImages.Span[entity.PrimitiveIndex].ClipPointCount
                    : 4,
            _ => 0,
        };

    private static int GetPolylineSegmentCount(in CadPolylinePrimitive polyline) =>
        polyline.IsClosed ? polyline.VertexCount : Math.Max(0, polyline.VertexCount - 1);

    private static int GetPolylineSegmentCount(in CadPolyline3DPrimitive polyline) =>
        polyline.IsClosed ? polyline.PointCount : Math.Max(0, polyline.PointCount - 1);

    private static int GetSplineSegmentCount(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline) =>
        CadNurbsLineTypeLowerer.TryValidate(snapshot, spline, out int spanCount)
            ? spanCount
            : 0;

    private static int CountPolylineArcMaps(
        CadDocumentSnapshot snapshot,
        in CadEntityHeader entity)
    {
        CadPolylinePrimitive polyline = snapshot.Polylines.Span[entity.PrimitiveIndex];
        ReadOnlySpan<CadPolylineVertex> vertices = snapshot.PolylineVertices.Span.Slice(
            polyline.VertexOffset,
            polyline.VertexCount);
        int segmentCount = GetPolylineSegmentCount(polyline);
        int count = 0;
        for (int i = 0; i < segmentCount; i++)
        {
            if (vertices[i].Bulge != 0.0)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountVisibleFaceEdges(in CadFacePrimitive face)
    {
        Span<CadPoint3D> points = stackalloc CadPoint3D[5]
        {
            face.First,
            face.Second,
            face.Third,
            face.Fourth,
            face.First,
        };
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            if ((face.InvisibleEdgeMask & (1 << i)) == 0 && points[i] != points[i + 1])
            {
                count++;
            }
        }

        return count;
    }

    private static CadLineTypeLoweringResult LowerLine(
        in CadLinePrimitive line,
        CadPoint3D rebaseOrigin,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements)
    {
        Span<MeasuredSegment> segments = stackalloc MeasuredSegment[1];
        segments[0] = MeasuredSegment.Line(
            Project(line.Start, rebaseOrigin),
            Project(line.End, rebaseOrigin),
            (line.End - line.Start).Length);
        return LowerMeasuredPath(
            segments,
            ReadOnlySpan<double>.Empty,
            isClosed: false,
            resetAtEverySegment: false,
            elements,
            patternLength,
            scale,
            maxFigures,
            maxPatternSteps,
            maxPlacements,
            Matrix4x4.Identity);
    }

    private static CadLineTypeLoweringResult LowerCircle(
        in CadCirclePrimitive circle,
        CadPoint3D rebaseOrigin,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements)
    {
        Span<MeasuredSegment> segments = stackalloc MeasuredSegment[1];
        Span<double> arcMap = stackalloc double[ArcLengthMapSize];
        CadPoint3D axisX = circle.CoordinateSystem.XAxis * circle.Radius;
        CadPoint3D axisY = circle.CoordinateSystem.YAxis * circle.Radius;
        double length = BuildArcLengthMap(axisX, axisY, 0.0, TwoPi, arcMap);
        float radius = ToFloat(circle.Radius);
        segments[0] = MeasuredSegment.Arc(
            Vector2.Zero,
            radius,
            0.0,
            TwoPi,
            length,
            0);
        return LowerMeasuredPath(
            segments,
            arcMap,
            isClosed: true,
            resetAtEverySegment: false,
            elements,
            patternLength,
            scale,
            maxFigures,
            maxPatternSteps,
            maxPlacements,
            CreateProjectionTransform(
                circle.Center,
                circle.CoordinateSystem.XAxis,
                circle.CoordinateSystem.YAxis,
                rebaseOrigin));
    }

    private static CadLineTypeLoweringResult LowerArc(
        in CadArcPrimitive arc,
        CadPoint3D rebaseOrigin,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements)
    {
        Span<MeasuredSegment> segments = stackalloc MeasuredSegment[1];
        Span<double> arcMap = stackalloc double[ArcLengthMapSize];
        CadPoint3D axisX = arc.CoordinateSystem.XAxis * arc.Radius;
        CadPoint3D axisY = arc.CoordinateSystem.YAxis * arc.Radius;
        double length = BuildArcLengthMap(
            axisX,
            axisY,
            arc.StartAngle,
            arc.SweepAngle,
            arcMap);
        segments[0] = MeasuredSegment.Arc(
            Vector2.Zero,
            ToFloat(arc.Radius),
            arc.StartAngle,
            arc.SweepAngle,
            length,
            0);
        return LowerMeasuredPath(
            segments,
            arcMap,
            isClosed: false,
            resetAtEverySegment: false,
            elements,
            patternLength,
            scale,
            maxFigures,
            maxPatternSteps,
            maxPlacements,
            CreateProjectionTransform(
                arc.Center,
                arc.CoordinateSystem.XAxis,
                arc.CoordinateSystem.YAxis,
                rebaseOrigin));
    }

    private static CadLineTypeLoweringResult LowerEllipse(
        in CadEllipsePrimitive ellipse,
        CadPoint3D rebaseOrigin,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements)
    {
        Span<MeasuredSegment> segments = stackalloc MeasuredSegment[1];
        Span<double> arcMap = stackalloc double[ArcLengthMapSize];
        double length = BuildArcLengthMap(
            ellipse.MajorAxis,
            ellipse.MinorAxis,
            ellipse.StartParameter,
            ellipse.SweepParameter,
            arcMap);
        segments[0] = MeasuredSegment.Arc(
            Vector2.Zero,
            1.0f,
            ellipse.StartParameter,
            ellipse.SweepParameter,
            length,
            0);
        return LowerMeasuredPath(
            segments,
            arcMap,
            isClosed: ellipse.SweepParameter >= TwoPi - 1e-12,
            resetAtEverySegment: false,
            elements,
            patternLength,
            scale,
            maxFigures,
            maxPatternSteps,
            maxPlacements,
            CreateProjectionTransform(
                ellipse.Center,
                ellipse.MajorAxis,
                ellipse.MinorAxis,
                rebaseOrigin));
    }

    private static CadLineTypeLoweringResult LowerPolyline(
        CadDocumentSnapshot snapshot,
        in CadPolylinePrimitive polyline,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements)
    {
        ReadOnlySpan<CadPolylineVertex> vertices = snapshot.PolylineVertices.Span.Slice(
            polyline.VertexOffset,
            polyline.VertexCount);
        int segmentCount = polyline.IsClosed ? vertices.Length : vertices.Length - 1;
        MeasuredSegment[]? rentedSegments = null;
        double[]? rentedArcMaps = null;
        Span<MeasuredSegment> segments = segmentCount <= 256
            ? stackalloc MeasuredSegment[segmentCount]
            : (rentedSegments = ArrayPool<MeasuredSegment>.Shared.Rent(segmentCount))
                .AsSpan(0, segmentCount);
        int arcCount = 0;
        for (int i = 0; i < segmentCount; i++)
        {
            if (vertices[i].Bulge != 0.0)
            {
                arcCount++;
            }
        }

        int mapLength = checked(arcCount * ArcLengthMapSize);
        Span<double> arcMaps = mapLength == 0
            ? Span<double>.Empty
            : mapLength <= 1024
                ? stackalloc double[mapLength]
                : (rentedArcMaps = ArrayPool<double>.Shared.Rent(mapLength))
                    .AsSpan(0, mapLength);
        try
        {
            int arcIndex = 0;
            double pathOffset = 0.0;
            for (int i = 0; i < segmentCount; i++)
            {
                CadPolylineVertex start = vertices[i];
                CadPolylineVertex end = vertices[(i + 1) % vertices.Length];
                Vector2 startPoint = ToVector(start);
                Vector2 endPoint = ToVector(end);
                if (start.Bulge == 0.0)
                {
                    CadPoint3D delta =
                        (polyline.CoordinateSystem.XAxis * (end.X - start.X)) +
                        (polyline.CoordinateSystem.YAxis * (end.Y - start.Y));
                    MeasuredSegment measured = MeasuredSegment.Line(
                        startPoint,
                        endPoint,
                        delta.Length);
                    segments[i] = measured with { PathOffset = pathOffset };
                    pathOffset += measured.Length;
                    continue;
                }

                CadSnapshotCompiler.GetBulgeArc(
                    start,
                    end,
                    out double centerX,
                    out double centerY,
                    out double radius,
                    out double startAngle,
                    out double sweep);
                int mapOffset = checked(arcIndex++ * ArcLengthMapSize);
                Span<double> map = arcMaps.Slice(mapOffset, ArcLengthMapSize);
                double length = BuildArcLengthMap(
                    polyline.CoordinateSystem.XAxis * radius,
                    polyline.CoordinateSystem.YAxis * radius,
                    startAngle,
                    sweep,
                    map);
                MeasuredSegment measuredArc = MeasuredSegment.Arc(
                    new Vector2(ToFloat(centerX), ToFloat(centerY)),
                    ToFloat(radius),
                    startAngle,
                    sweep,
                    length,
                    mapOffset);
                segments[i] = measuredArc with { PathOffset = pathOffset };
                pathOffset += measuredArc.Length;
            }

            return LowerMeasuredPath(
                segments,
                arcMaps,
                polyline.IsClosed,
                resetAtEverySegment: !polyline.IsLineTypeContinuous,
                elements,
                patternLength,
                scale,
                maxFigures,
                maxPatternSteps,
                maxPlacements,
                CreateProjectionTransform(
                    polyline.WorldOrigin,
                    polyline.CoordinateSystem.XAxis,
                    polyline.CoordinateSystem.YAxis,
                    snapshot.RebaseOrigin));
        }
        finally
        {
            if (rentedSegments is not null)
            {
                ArrayPool<MeasuredSegment>.Shared.Return(rentedSegments);
            }

            if (rentedArcMaps is not null)
            {
                ArrayPool<double>.Shared.Return(rentedArcMaps);
            }
        }
    }

    private static CadLineTypeLoweringResult LowerPolyline3D(
        CadDocumentSnapshot snapshot,
        in CadPolyline3DPrimitive polyline,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements)
    {
        ReadOnlySpan<CadPoint3D> points = snapshot.Polyline3DPoints.Span.Slice(
            polyline.PointOffset,
            polyline.PointCount);
        int segmentCount = polyline.IsClosed ? points.Length : points.Length - 1;
        MeasuredSegment[]? rented = null;
        Span<MeasuredSegment> segments = segmentCount <= 256
            ? stackalloc MeasuredSegment[segmentCount]
            : (rented = ArrayPool<MeasuredSegment>.Shared.Rent(segmentCount))
                .AsSpan(0, segmentCount);
        try
        {
            double pathOffset = 0.0;
            for (int i = 0; i < segmentCount; i++)
            {
                CadPoint3D start = points[i];
                CadPoint3D end = points[(i + 1) % points.Length];
                MeasuredSegment measured = MeasuredSegment.Line(
                    Project(start, snapshot.RebaseOrigin),
                    Project(end, snapshot.RebaseOrigin),
                    (end - start).Length);
                segments[i] = measured with { PathOffset = pathOffset };
                pathOffset += measured.Length;
            }

            return LowerMeasuredPath(
                segments,
                ReadOnlySpan<double>.Empty,
                polyline.IsClosed,
                // Autodesk exposes continuous linetype generation only for
                // lightweight/2D polylines; retained 3D edges are A-aligned
                // independently at each vertex.
                resetAtEverySegment: true,
                elements,
                patternLength,
                scale,
                maxFigures,
                maxPatternSteps,
                maxPlacements,
                Matrix4x4.Identity);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<MeasuredSegment>.Shared.Return(rented);
            }
        }
    }

    private static CadLineTypeLoweringResult LowerLinearSpline(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements)
    {
        if (!IsSupportedLinearSpline(snapshot, spline))
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.UnsupportedEntity,
                null,
                Matrix4x4.Identity,
                0,
                0,
                0);
        }

        ReadOnlySpan<CadPoint3D> points = snapshot.SplineControlPoints.Span.Slice(
            spline.ControlPointOffset,
            spline.ControlPointCount);
        int segmentCount = points.Length - 1;
        MeasuredSegment[]? rented = null;
        Span<MeasuredSegment> segments = segmentCount <= 256
            ? stackalloc MeasuredSegment[segmentCount]
            : (rented = ArrayPool<MeasuredSegment>.Shared.Rent(segmentCount))
                .AsSpan(0, segmentCount);
        try
        {
            double pathOffset = 0.0;
            for (int i = 0; i < segmentCount; i++)
            {
                CadPoint3D start = points[i];
                CadPoint3D end = points[i + 1];
                MeasuredSegment measured = MeasuredSegment.Line(
                    Project(start, snapshot.RebaseOrigin),
                    Project(end, snapshot.RebaseOrigin),
                    (end - start).Length);
                segments[i] = measured with { PathOffset = pathOffset };
                pathOffset += measured.Length;
            }

            return LowerMeasuredPath(
                segments,
                ReadOnlySpan<double>.Empty,
                isClosed: false,
                resetAtEverySegment: false,
                elements,
                patternLength,
                scale,
                maxFigures,
                maxPatternSteps,
                maxPlacements,
                Matrix4x4.Identity);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<MeasuredSegment>.Shared.Return(rented);
            }
        }
    }

    private static CadLineTypeLoweringResult LowerSpline(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxArcMapsPerEntity,
        int maxPlacements)
    {
        if (spline.Degree == 1 && !spline.IsClosed && !spline.IsPeriodic)
        {
            return LowerLinearSpline(
                snapshot,
                spline,
                elements,
                patternLength,
                scale,
                maxFigures,
                maxPatternSteps,
                maxPlacements);
        }

        return CadNurbsLineTypeLowerer.Lower(
            snapshot,
            spline,
            elements,
            patternLength,
            scale,
            maxFigures,
            maxPatternSteps,
            maxArcMapsPerEntity,
            maxPlacements);
    }

    private static bool IsSupportedLinearSpline(
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline)
    {
        if (spline.Degree != 1 || spline.IsClosed || spline.IsPeriodic ||
            spline.ControlPointCount < 2 ||
            spline.KnotCount != spline.ControlPointCount + 2)
        {
            return false;
        }

        ReadOnlySpan<double> knots = snapshot.SplineKnots.Span.Slice(
            spline.KnotOffset,
            spline.KnotCount);
        for (int i = 1; i < knots.Length; i++)
        {
            if (knots[i] < knots[i - 1])
            {
                return false;
            }
        }

        // For degree one, each positive active knot span is exactly the line
        // between consecutive control points. Repeated active knots can encode
        // a discontinuity, which is not a single uninterrupted path contract.
        for (int i = 1; i < spline.ControlPointCount; i++)
        {
            if (!(knots[i + 1] > knots[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static CadLineTypeLoweringResult LowerFace(
        in CadFacePrimitive face,
        CadPoint3D rebaseOrigin,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements)
    {
        Span<CadPoint3D> points = stackalloc CadPoint3D[5]
        {
            face.First,
            face.Second,
            face.Third,
            face.Fourth,
            face.First,
        };
        Span<MeasuredSegment> segments = stackalloc MeasuredSegment[4];
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            if ((face.InvisibleEdgeMask & (1 << i)) != 0 || points[i] == points[i + 1])
            {
                continue;
            }

            segments[count++] = MeasuredSegment.Line(
                Project(points[i], rebaseOrigin),
                Project(points[i + 1], rebaseOrigin),
                (points[i + 1] - points[i]).Length);
        }

        return LowerMeasuredPath(
            segments[..count],
            ReadOnlySpan<double>.Empty,
            isClosed: false,
            resetAtEverySegment: true,
            elements,
            patternLength,
            scale,
            maxFigures,
            maxPatternSteps,
            maxPlacements,
            Matrix4x4.Identity);
    }

    private static CadLineTypeLoweringResult LowerWipeoutFrame(
        CadDocumentSnapshot snapshot,
        in CadWipeoutPrimitive wipeout,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements)
    {
        Span<CadWipeoutClipPoint> outer = stackalloc CadWipeoutClipPoint[4]
        {
            new(0.0, 0.0),
            new(wipeout.Width, 0.0),
            new(wipeout.Width, wipeout.Height),
            new(0.0, wipeout.Height),
        };
        ReadOnlySpan<CadWipeoutClipPoint> points = wipeout.IsClipped
            ? snapshot.WipeoutClipPoints.Span.Slice(
                wipeout.ClipPointOffset,
                wipeout.ClipPointCount)
            : outer;
        MeasuredSegment[]? rented = null;
        Span<MeasuredSegment> segments = points.Length <= 256
            ? stackalloc MeasuredSegment[points.Length]
            : (rented = ArrayPool<MeasuredSegment>.Shared.Rent(points.Length))
                .AsSpan(0, points.Length);
        try
        {
            double pathOffset = 0.0;
            for (int i = 0; i < points.Length; i++)
            {
                CadWipeoutClipPoint start = points[i];
                CadWipeoutClipPoint end = points[(i + 1) % points.Length];
                CadPoint3D delta =
                    (wipeout.UVector * (end.U - start.U)) +
                    (wipeout.VVector * (end.V - start.V));
                MeasuredSegment segment = MeasuredSegment.Line(
                    new Vector2(ToFloat(start.U), ToFloat(start.V)),
                    new Vector2(ToFloat(end.U), ToFloat(end.V)),
                    delta.Length);
                segments[i] = segment with { PathOffset = pathOffset };
                pathOffset += segment.Length;
            }

            return LowerMeasuredPath(
                segments,
                ReadOnlySpan<double>.Empty,
                isClosed: true,
                resetAtEverySegment: false,
                elements,
                patternLength,
                scale,
                maxFigures,
                maxPatternSteps,
                maxPlacements,
                CreateProjectionTransform(
                    wipeout.Origin,
                    wipeout.UVector,
                    wipeout.VVector,
                    snapshot.RebaseOrigin));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<MeasuredSegment>.Shared.Return(rented);
            }
        }
    }

    /// <remarks>
    /// Original ProGPU port of the repository-owned WIPEOUT frame splitter;
    /// IMAGE uses the same persisted pixel-plane perimeter contract.
    /// </remarks>
    private static CadLineTypeLoweringResult LowerRasterImageFrame(
        CadDocumentSnapshot snapshot,
        in CadRasterImagePrimitive image,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements)
    {
        Span<CadWipeoutClipPoint> outer = stackalloc CadWipeoutClipPoint[4]
        {
            new(0.0, 0.0),
            new(image.Width, 0.0),
            new(image.Width, image.Height),
            new(0.0, image.Height),
        };
        ReadOnlySpan<CadWipeoutClipPoint> points = image.IsClipped
            ? snapshot.RasterImageClipPoints.Span.Slice(
                image.ClipPointOffset,
                image.ClipPointCount)
            : outer;
        MeasuredSegment[]? rented = null;
        Span<MeasuredSegment> segments = points.Length <= 256
            ? stackalloc MeasuredSegment[points.Length]
            : (rented = ArrayPool<MeasuredSegment>.Shared.Rent(points.Length))
                .AsSpan(0, points.Length);
        try
        {
            double pathOffset = 0.0;
            for (int i = 0; i < points.Length; i++)
            {
                CadWipeoutClipPoint start = points[i];
                CadWipeoutClipPoint end = points[(i + 1) % points.Length];
                CadPoint3D delta =
                    (image.UVector * (end.U - start.U)) +
                    (image.VVector * (end.V - start.V));
                MeasuredSegment segment = MeasuredSegment.Line(
                    new Vector2(ToFloat(start.U), ToFloat(start.V)),
                    new Vector2(ToFloat(end.U), ToFloat(end.V)),
                    delta.Length);
                segments[i] = segment with { PathOffset = pathOffset };
                pathOffset += segment.Length;
            }

            return LowerMeasuredPath(
                segments,
                ReadOnlySpan<double>.Empty,
                isClosed: true,
                resetAtEverySegment: false,
                elements,
                patternLength,
                scale,
                maxFigures,
                maxPatternSteps,
                maxPlacements,
                CreateProjectionTransform(
                    image.Origin,
                    image.UVector,
                    image.VVector,
                    snapshot.RebaseOrigin));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<MeasuredSegment>.Shared.Return(rented);
            }
        }
    }

    private static CadLineTypeLoweringResult LowerMeasuredPath(
        ReadOnlySpan<MeasuredSegment> segments,
        ReadOnlySpan<double> arcMaps,
        bool isClosed,
        bool resetAtEverySegment,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int maxFigures,
        int maxPatternSteps,
        int maxPlacements,
        Matrix4x4 transform)
    {
        if (segments.IsEmpty)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.Continuous,
                null,
                transform,
                0,
                0,
                0);
        }

        int figureCount = 0;
        int placementCount = 0;
        int patternStepCount = 0;
        if (resetAtEverySegment)
        {
            for (int i = 0; i < segments.Length; i++)
            {
                CountStatus countStatus = TryCountFigures(
                        segments[i].Length,
                        isClosed: false,
                        elements,
                        patternLength,
                        scale,
                        maxFigures - figureCount,
                        maxPatternSteps - patternStepCount,
                        maxPlacements - placementCount,
                        out int count,
                        out int placements,
                        out int steps);
                if (countStatus != CountStatus.Success)
                {
                    return new CadLineTypeLoweringResult(
                        countStatus == CountStatus.FigureLimitExceeded
                            ? CadLineTypeLoweringStatus.FigureLimitExceeded
                            : countStatus == CountStatus.PlacementLimitExceeded
                                ? CadLineTypeLoweringStatus.PlacementLimitExceeded
                                : CadLineTypeLoweringStatus.PatternStepLimitExceeded,
                        null,
                        transform,
                        checked(figureCount + count),
                        checked(patternStepCount + steps),
                        0,
                        null,
                        checked(placementCount + placements));
                }

                figureCount = checked(figureCount + count);
                placementCount = checked(placementCount + placements);
                patternStepCount = checked(patternStepCount + steps);
            }
        }
        else
        {
            double totalLength = 0.0;
            for (int i = 0; i < segments.Length; i++)
            {
                totalLength += segments[i].Length;
            }

            CountStatus countStatus = TryCountFigures(
                    totalLength,
                    isClosed,
                    elements,
                    patternLength,
                    scale,
                    maxFigures,
                    maxPatternSteps,
                    maxPlacements,
                    out figureCount,
                    out placementCount,
                    out patternStepCount);
            if (countStatus != CountStatus.Success)
            {
                return new CadLineTypeLoweringResult(
                    countStatus == CountStatus.FigureLimitExceeded
                        ? CadLineTypeLoweringStatus.FigureLimitExceeded
                        : countStatus == CountStatus.PlacementLimitExceeded
                            ? CadLineTypeLoweringStatus.PlacementLimitExceeded
                            : CadLineTypeLoweringStatus.PatternStepLimitExceeded,
                    null,
                    transform,
                    figureCount,
                    patternStepCount,
                    0,
                    null,
                    placementCount);
            }
        }

        if (figureCount == 0 && placementCount == 0)
        {
            return new CadLineTypeLoweringResult(
                CadLineTypeLoweringStatus.Continuous,
                null,
                transform,
                0,
                patternStepCount,
                0);
        }

        var path = new PathGeometry();
        CadLineTypePlacement[] placementsBuffer = placementCount == 0
            ? []
            : new CadLineTypePlacement[placementCount];
        int placementIndex = 0;
        if (resetAtEverySegment)
        {
            for (int i = 0; i < segments.Length; i++)
            {
                AppendSingleSegmentPattern(
                    path,
                    segments[i],
                    arcMaps,
                    elements,
                    patternLength,
                    scale,
                    placementsBuffer,
                    ref placementIndex);
            }
        }
        else
        {
            AppendCompositePattern(
                path,
                segments,
                arcMaps,
                isClosed,
                elements,
                patternLength,
                scale,
                placementsBuffer,
                ref placementIndex);
        }

        return new CadLineTypeLoweringResult(
            CadLineTypeLoweringStatus.Lowered,
            path,
            transform,
            figureCount,
            patternStepCount,
            0,
            placementsBuffer,
            placementCount);
    }

    private static CountStatus TryCountFigures(
        double pathLength,
        bool isClosed,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        int figureLimit,
        int patternStepLimit,
        int placementLimit,
        out int count,
        out int placementCount,
        out int patternStepCount)
    {
        count = 0;
        placementCount = 0;
        var spans = new PatternSpanEnumerator(
            pathLength,
            isClosed,
            elements,
            patternLength,
            scale,
            patternStepLimit);
        while (spans.MoveNext())
        {
            if (spans.Current.IsContent)
            {
                placementCount++;
                if (placementCount > placementLimit)
                {
                    patternStepCount = spans.PatternStepCount;
                    return CountStatus.PlacementLimitExceeded;
                }
            }
            else
            {
                count++;
                if (count > figureLimit)
                {
                    patternStepCount = spans.PatternStepCount;
                    return CountStatus.FigureLimitExceeded;
                }
            }
        }

        patternStepCount = spans.PatternStepCount;
        return spans.PatternStepLimitExceeded
            ? CountStatus.PatternStepLimitExceeded
            : CountStatus.Success;
    }

    private static void AppendSingleSegmentPattern(
        PathGeometry path,
        in MeasuredSegment segment,
        ReadOnlySpan<double> arcMaps,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        Span<CadLineTypePlacement> placements,
        ref int placementIndex)
    {
        MeasuredSegment localSegment = segment with { PathOffset = 0.0 };
        var spans = new PatternSpanEnumerator(
            segment.Length,
            isClosed: false,
            elements,
            patternLength,
            scale,
            int.MaxValue);
        while (spans.MoveNext())
        {
            ReadOnlySpan<MeasuredSegment> localSegments =
                new ReadOnlySpan<MeasuredSegment>(in localSegment);
            if (spans.Current.IsContent)
            {
                placements[placementIndex++] = CreatePlacement(
                    localSegments,
                    arcMaps,
                    spans.Current.Start,
                    spans.Current.ElementIndex);
            }
            else
            {
                AppendMeasuredSpan(
                    path,
                    localSegments,
                    arcMaps,
                    spans.Current.Start,
                    spans.Current.End,
                    spans.Current.IsPoint);
            }
        }
    }

    private static void AppendCompositePattern(
        PathGeometry path,
        ReadOnlySpan<MeasuredSegment> segments,
        ReadOnlySpan<double> arcMaps,
        bool isClosed,
        ReadOnlySpan<CadLineTypeElement> elements,
        double patternLength,
        double scale,
        Span<CadLineTypePlacement> placements,
        ref int placementIndex)
    {
        double totalLength = 0.0;
        for (int i = 0; i < segments.Length; i++)
        {
            totalLength += segments[i].Length;
        }

        var spans = new PatternSpanEnumerator(
            totalLength,
            isClosed,
            elements,
            patternLength,
            scale,
            int.MaxValue);
        while (spans.MoveNext())
        {
            if (spans.Current.IsContent)
            {
                placements[placementIndex++] = CreatePlacement(
                    segments,
                    arcMaps,
                    spans.Current.Start,
                    spans.Current.ElementIndex);
            }
            else
            {
                AppendMeasuredSpan(
                    path,
                    segments,
                    arcMaps,
                    spans.Current.Start,
                    spans.Current.End,
                    spans.Current.IsPoint);
            }
        }
    }

    private static CadLineTypePlacement CreatePlacement(
        ReadOnlySpan<MeasuredSegment> segments,
        ReadOnlySpan<double> arcMaps,
        double distance,
        int elementIndex)
    {
        int segmentIndex = FindSegment(segments, distance, out double segmentBase);
        MeasuredSegment segment = segments[segmentIndex];
        double localDistance = Math.Clamp(distance - segmentBase, 0.0, segment.Length);
        return new CadLineTypePlacement(
            elementIndex,
            Evaluate(segment, localDistance, arcMaps),
            EvaluateTangent(segment, localDistance, arcMaps));
    }

    private static void AppendMeasuredSpan(
        PathGeometry path,
        ReadOnlySpan<MeasuredSegment> segments,
        ReadOnlySpan<double> arcMaps,
        double startDistance,
        double endDistance,
        bool isPoint)
    {
        int segmentIndex = FindSegment(segments, startDistance, out double segmentBase);

        MeasuredSegment first = segments[segmentIndex];
        double localStart = Math.Clamp(startDistance - segmentBase, 0.0, first.Length);
        Vector2 start = Evaluate(first, localStart, arcMaps);
        var figure = new PathFigure(start)
        {
            IsFilled = false,
            IsClosed = false,
        };
        path.Figures.Add(figure);
        if (isPoint)
        {
            figure.Segments.Add(new LineSegment(start));
            return;
        }

        double current = startDistance;
        while (current < endDistance && segmentIndex < segments.Length)
        {
            MeasuredSegment segment = segments[segmentIndex];
            double localFrom = Math.Clamp(current - segmentBase, 0.0, segment.Length);
            double localTo = Math.Min(segment.Length, endDistance - segmentBase);
            if (localTo > localFrom)
            {
                AppendMeasuredPiece(figure, segment, localFrom, localTo, arcMaps);
            }

            current = segmentBase + localTo;
            if (localTo >= segment.Length)
            {
                segmentBase += segment.Length;
                segmentIndex++;
            }
            else
            {
                break;
            }
        }
    }

    private static void AppendMeasuredPiece(
        PathFigure figure,
        in MeasuredSegment segment,
        double from,
        double to,
        ReadOnlySpan<double> arcMaps)
    {
        Vector2 end = Evaluate(segment, to, arcMaps);
        if (!segment.IsArc)
        {
            figure.Segments.Add(new LineSegment(end));
            return;
        }

        double fromUnit = InvertArcDistance(segment, from, arcMaps);
        double toUnit = InvertArcDistance(segment, to, arcMaps);
        double sweep = segment.Sweep * (toUnit - fromUnit);
        figure.Segments.Add(new ArcSegment(
            end,
            new Vector2(segment.Radius, segment.Radius),
            rotationAngle: 0.0f,
            isLargeArc: Math.Abs(sweep) > Math.PI,
            sweepDirection: sweep >= 0.0
                ? SweepDirection.Counterclockwise
                : SweepDirection.Clockwise));
    }

    private static Vector2 Evaluate(
        in MeasuredSegment segment,
        double distance,
        ReadOnlySpan<double> arcMaps)
    {
        if (!segment.IsArc)
        {
            float amount = segment.Length == 0.0
                ? 0.0f
                : ToFloat(Math.Clamp(distance / segment.Length, 0.0, 1.0));
            return Vector2.Lerp(segment.Start, segment.End, amount);
        }

        double unit = InvertArcDistance(segment, distance, arcMaps);
        double angle = segment.StartAngle + (segment.Sweep * unit);
        return segment.Center + new Vector2(
            segment.Radius * MathF.Cos(ToFloat(angle)),
            segment.Radius * MathF.Sin(ToFloat(angle)));
    }

    private static Vector2 EvaluateTangent(
        in MeasuredSegment segment,
        double distance,
        ReadOnlySpan<double> arcMaps)
    {
        Vector2 tangent;
        if (!segment.IsArc)
        {
            tangent = segment.End - segment.Start;
        }
        else
        {
            double unit = InvertArcDistance(segment, distance, arcMaps);
            double angle = segment.StartAngle + (segment.Sweep * unit);
            float direction = segment.Sweep >= 0.0 ? 1.0f : -1.0f;
            tangent = new Vector2(
                -MathF.Sin(ToFloat(angle)) * direction,
                MathF.Cos(ToFloat(angle)) * direction);
        }

        float length = tangent.Length();
        return length > 0.0f && float.IsFinite(length)
            ? tangent / length
            : Vector2.Zero;
    }

    private static int FindSegment(
        ReadOnlySpan<MeasuredSegment> segments,
        double distance,
        out double segmentBase)
    {
        int low = 0;
        int high = segments.Length;
        while (low + 1 < high)
        {
            int middle = (low + high) >> 1;
            if (segments[middle].PathOffset <= distance)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        int segmentIndex = low;
        segmentBase = segments[segmentIndex].PathOffset;
        while (segmentIndex < segments.Length - 1 &&
            distance >= segmentBase + segments[segmentIndex].Length)
        {
            segmentIndex++;
            segmentBase = segments[segmentIndex].PathOffset;
        }
        return segmentIndex;
    }

    private static double InvertArcDistance(
        in MeasuredSegment segment,
        double distance,
        ReadOnlySpan<double> arcMaps)
    {
        if (segment.Length <= 0.0 || distance <= 0.0)
        {
            return 0.0;
        }

        if (distance >= segment.Length)
        {
            return 1.0;
        }

        ReadOnlySpan<double> map = arcMaps.Slice(
            segment.ArcMapOffset,
            ArcLengthMapSize);
        int low = 0;
        int high = ArcLengthBinCount;
        while (low + 1 < high)
        {
            int middle = (low + high) >> 1;
            if (map[middle] <= distance)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        double binLength = map[low + 1] - map[low];
        double fraction = binLength <= 0.0
            ? 0.0
            : (distance - map[low]) / binLength;
        return Math.Clamp((low + fraction) / ArcLengthBinCount, 0.0, 1.0);
    }

    private static double BuildArcLengthMap(
        CadPoint3D axisX,
        CadPoint3D axisY,
        double startAngle,
        double sweep,
        Span<double> destination)
    {
        destination[0] = 0.0;
        double cumulative = 0.0;
        for (int i = 0; i < ArcLengthBinCount; i++)
        {
            double unitStart = (double)i / ArcLengthBinCount;
            double unitEnd = (double)(i + 1) / ArcLengthBinCount;
            cumulative += IntegrateArcLength(
                axisX,
                axisY,
                startAngle,
                sweep,
                unitStart,
                unitEnd);
            destination[i + 1] = cumulative;
        }

        return cumulative;
    }

    private static double IntegrateArcLength(
        CadPoint3D axisX,
        CadPoint3D axisY,
        double startAngle,
        double sweep,
        double unitStart,
        double unitEnd)
    {
        // Eight-point Gauss-Legendre quadrature on one fixed map bin. The
        // nodes and weights are the standard roots and weights on [-1, 1].
        ReadOnlySpan<double> nodes =
        [
            0.1834346424956498,
            0.5255324099163290,
            0.7966664774136267,
            0.9602898564975363,
        ];
        ReadOnlySpan<double> weights =
        [
            0.3626837833783620,
            0.3137066458778873,
            0.2223810344533745,
            0.1012285362903763,
        ];
        double midpoint = (unitStart + unitEnd) * 0.5;
        double half = (unitEnd - unitStart) * 0.5;
        double sum = 0.0;
        for (int i = 0; i < nodes.Length; i++)
        {
            double delta = half * nodes[i];
            sum += weights[i] *
                (ArcSpeed(axisX, axisY, startAngle, sweep, midpoint - delta) +
                 ArcSpeed(axisX, axisY, startAngle, sweep, midpoint + delta));
        }

        return half * sum;
    }

    private static double ArcSpeed(
        CadPoint3D axisX,
        CadPoint3D axisY,
        double startAngle,
        double sweep,
        double unit)
    {
        double angle = startAngle + (sweep * unit);
        CadPoint3D derivative =
            (axisX * -Math.Sin(angle)) +
            (axisY * Math.Cos(angle));
        return Math.Abs(sweep) * derivative.Length;
    }

    private static bool NeedsLowering(ReadOnlySpan<CadLineTypeElement> elements)
    {
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].Length < 0.0 ||
                elements[i].Kind != CadLineTypeElementKind.Stroke)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasComplexElement(ReadOnlySpan<CadLineTypeElement> elements)
    {
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].Kind is not CadLineTypeElementKind.Stroke)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasUnresolvedComplexElement(ReadOnlySpan<CadLineTypeElement> elements)
    {
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].Kind == CadLineTypeElementKind.UnresolvedComplex)
            {
                return true;
            }
        }
        return false;
    }

    private static Matrix4x4 CreateProjectionTransform(
        CadPoint3D center,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        CadPoint3D origin) =>
        new(
            ToFloat(xAxis.X), ToFloat(xAxis.Y), 0.0f, 0.0f,
            ToFloat(yAxis.X), ToFloat(yAxis.Y), 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            ToFloat(center.X - origin.X), ToFloat(center.Y - origin.Y), 0.0f, 1.0f);

    private static Vector2 Project(CadPoint3D point, CadPoint3D origin) =>
        new(ToFloat(point.X - origin.X), ToFloat(point.Y - origin.Y));

    private static Vector2 ToVector(CadPolylineVertex vertex) =>
        new(ToFloat(vertex.X), ToFloat(vertex.Y));

    private static float ToFloat(double value)
    {
        float converted = (float)value;
        if (!float.IsFinite(converted))
        {
            throw new InvalidOperationException(
                "A rebased CAD linetype coordinate exceeds the retained float range.");
        }

        return converted;
    }

    private readonly record struct MeasuredSegment(
        Vector2 Start,
        Vector2 End,
        Vector2 Center,
        float Radius,
        double StartAngle,
        double Sweep,
        double Length,
        int ArcMapOffset,
        bool IsArc,
        double PathOffset)
    {
        public static MeasuredSegment Line(Vector2 start, Vector2 end, double length) =>
            new(start, end, default, 0.0f, 0.0, 0.0, length, -1, false, 0.0);

        public static MeasuredSegment Arc(
            Vector2 center,
            float radius,
            double startAngle,
            double sweep,
            double length,
            int arcMapOffset)
        {
            Vector2 start = center + new Vector2(
                radius * MathF.Cos(ToFloat(startAngle)),
                radius * MathF.Sin(ToFloat(startAngle)));
            double endAngle = startAngle + sweep;
            Vector2 end = center + new Vector2(
                radius * MathF.Cos(ToFloat(endAngle)),
                radius * MathF.Sin(ToFloat(endAngle)));
            return new MeasuredSegment(
                start,
                end,
                center,
                radius,
                startAngle,
                sweep,
                length,
                arcMapOffset,
                true,
                0.0);
        }
    }

    internal readonly record struct PatternSpan(
        double Start,
        double End,
        bool IsPoint,
        bool IsContent = false,
        int ElementIndex = -1);

    internal ref struct PatternSpanEnumerator
    {
        private enum LayoutMode : byte
        {
            Empty,
            ShortContinuous,
            Open,
            Periodic,
        }

        private readonly ReadOnlySpan<CadLineTypeElement> _elements;
        private readonly double _pathLength;
        private readonly double _elementScale;
        private readonly double _middleEnd;
        private readonly bool _includeTerminalPoint;
        private readonly int _maxPatternSteps;
        private LayoutMode _mode;
        private int _stage;
        private int _elementIndex;
        private double _position;

        public PatternSpan Current { get; private set; }
        public int PatternStepCount { get; private set; }
        public bool PatternStepLimitExceeded { get; private set; }

        public PatternSpanEnumerator(
            double pathLength,
            bool isClosed,
            ReadOnlySpan<CadLineTypeElement> elements,
            double patternLength,
            double scale,
            int maxPatternSteps)
        {
            _elements = elements;
            _pathLength = pathLength;
            _elementScale = scale;
            _middleEnd = 0.0;
            _includeTerminalPoint = false;
            _maxPatternSteps = Math.Max(0, maxPatternSteps);
            _mode = LayoutMode.Empty;
            _stage = 0;
            _elementIndex = 0;
            _position = 0.0;
            Current = default;
            PatternStepCount = 0;
            PatternStepLimitExceeded = false;
            if (!double.IsFinite(pathLength) || pathLength <= 0.0 ||
                !double.IsFinite(patternLength) || patternLength <= 0.0 ||
                !double.IsFinite(scale) || scale <= 0.0 ||
                elements.IsEmpty)
            {
                return;
            }

            double scaledPatternLength = patternLength * scale;
            if (!double.IsFinite(scaledPatternLength) || scaledPatternLength <= 0.0)
            {
                return;
            }

            double firstLength = elements[0].Length * scale;
            if (isClosed || firstLength == 0.0)
            {
                double repeats = Math.Max(1.0, Math.Round(pathLength / scaledPatternLength));
                _elementScale = scale * (pathLength / (repeats * scaledPatternLength));
                _mode = LayoutMode.Periodic;
                _includeTerminalPoint = !isClosed && firstLength == 0.0 &&
                    elements[0].Kind == CadLineTypeElementKind.Stroke;
                return;
            }

            double cycles = Math.Floor(pathLength / scaledPatternLength);
            if (cycles < 1.0)
            {
                _mode = LayoutMode.ShortContinuous;
                return;
            }

            double remainder = pathLength - (cycles * scaledPatternLength);
            double endpointDash = (firstLength * 0.5) + (remainder * 0.5);
            _middleEnd = pathLength - endpointDash;
            _position = endpointDash;
            _elementIndex = 1 % elements.Length;
            _mode = LayoutMode.Open;
        }

        public bool MoveNext()
        {
            switch (_mode)
            {
                case LayoutMode.Empty:
                    return false;
                case LayoutMode.ShortContinuous:
                    if (_stage++ != 0)
                    {
                        return false;
                    }

                    Current = new PatternSpan(0.0, _pathLength, false);
                    return true;
                case LayoutMode.Open:
                    return MoveNextOpen();
                case LayoutMode.Periodic:
                    return MoveNextPeriodic();
                default:
                    return false;
            }
        }

        private bool MoveNextOpen()
        {
            if (_stage == 0)
            {
                _stage = 1;
                Current = new PatternSpan(0.0, _position, false);
                return true;
            }

            while (_stage == 1 && _position < _middleEnd)
            {
                if (!TryVisitPatternElement())
                {
                    return false;
                }

                int elementIndex = _elementIndex;
                CadLineTypeElement element = _elements[elementIndex];
                _elementIndex = (_elementIndex + 1) % _elements.Length;
                double start = _position;
                if (element.Kind != CadLineTypeElementKind.Stroke)
                {
                    Current = new PatternSpan(
                        start,
                        start,
                        false,
                        true,
                        elementIndex);
                    return true;
                }
                double length = Math.Abs(element.Length) * _elementScale;
                if (length == 0.0)
                {
                    if (element.Length == 0.0)
                    {
                        Current = new PatternSpan(start, start, true);
                        return true;
                    }

                    continue;
                }

                double end = Math.Min(_middleEnd, start + length);
                if (end <= start)
                {
                    _stage = 2;
                    break;
                }

                _position = end;
                if (element.Length > 0.0)
                {
                    Current = new PatternSpan(start, end, false);
                    return true;
                }
            }

            if (_stage <= 1)
            {
                _stage = 2;
            }

            if (_stage++ == 2)
            {
                Current = new PatternSpan(_middleEnd, _pathLength, false);
                return true;
            }

            return false;
        }

        private bool MoveNextPeriodic()
        {
            while (_position < _pathLength)
            {
                if (!TryVisitPatternElement())
                {
                    return false;
                }

                int elementIndex = _elementIndex;
                CadLineTypeElement element = _elements[elementIndex];
                _elementIndex = (_elementIndex + 1) % _elements.Length;
                double start = _position;
                if (element.Kind != CadLineTypeElementKind.Stroke)
                {
                    Current = new PatternSpan(
                        start,
                        start,
                        false,
                        true,
                        elementIndex);
                    return true;
                }
                double length = Math.Abs(element.Length) * _elementScale;
                if (length == 0.0)
                {
                    if (element.Length == 0.0)
                    {
                        Current = new PatternSpan(start, start, true);
                        return true;
                    }

                    continue;
                }

                double end = Math.Min(_pathLength, start + length);
                if (end <= start)
                {
                    _position = _pathLength;
                    break;
                }

                _position = end;
                if (element.Length > 0.0)
                {
                    Current = new PatternSpan(start, end, false);
                    return true;
                }
            }

            if (_includeTerminalPoint && _stage++ == 0)
            {
                Current = new PatternSpan(_pathLength, _pathLength, true);
                return true;
            }

            return false;
        }

        private bool TryVisitPatternElement()
        {
            if (PatternStepCount >= _maxPatternSteps)
            {
                PatternStepLimitExceeded = true;
                return false;
            }

            PatternStepCount++;
            return true;
        }
    }
}
