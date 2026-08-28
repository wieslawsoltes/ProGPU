using ACadSharp.Entities;
using CSMath;
using ProGPU.Vector;
using HatchArc = ACadSharp.Entities.Hatch.BoundaryPath.Arc;
using HatchEllipse = ACadSharp.Entities.Hatch.BoundaryPath.Ellipse;
using HatchLine = ACadSharp.Entities.Hatch.BoundaryPath.Line;
using HatchPolyline = ACadSharp.Entities.Hatch.BoundaryPath.Polyline;

namespace ProGPU.CAD;

public sealed partial class CadSnapshotCompiler
{
    private const double HatchClosureToleranceScale = 1e-10;

    private static CadEntityHeader CompileHatch(
        Hatch hatch,
        ulong handle,
        CadAffineTransform3D transform,
        bool hasTransform,
        int layerIndex,
        int styleIndex,
        CadSnapshotOptions options,
        List<CadHatchPrimitive> destination,
        List<CadHatchPattern> patterns,
        List<CadHatchPatternFamily> patternFamilies,
        List<double> patternDashes,
        List<CadHatchLoop> loops,
        List<CadHatchSegment> segments)
    {
        if (hatch.GradientColor?.Enabled == true)
        {
            throw new CadUnsupportedEntityException(
                "Gradient HATCH fills require retained gradient-space lowering.");
        }
        if (hatch.Style != HatchStyleType.Normal)
        {
            throw new CadUnsupportedEntityException(
                $"HATCH style {hatch.Style} requires explicit island-depth classification.");
        }
        if (hatch.Paths.Count == 0)
        {
            throw new ArgumentException("A HATCH must contain at least one boundary loop.");
        }
        if (!double.IsFinite(hatch.Elevation))
        {
            throw new ArgumentException("HATCH elevation must be finite.");
        }

        CadCoordinateSystem coordinateSystem = CadCoordinateSystem.FromNormal(ToPoint(hatch.Normal));
        if (hasTransform)
        {
            coordinateSystem = TransformBasis(transform, coordinateSystem);
        }
        EnsureFinite(coordinateSystem.XAxis);
        EnsureFinite(coordinateSystem.YAxis);
        EnsureFinite(coordinateSystem.ZAxis);

        CompiledHatchPattern? localPattern = hatch.IsSolid
            ? null
            : CompilePattern(hatch);

        var localLoops = new List<CadHatchLoop>(hatch.Paths.Count);
        var localSegments = new List<CadHatchSegment>();
        bool hasCurves = false;
        foreach (Hatch.BoundaryPath path in hatch.Paths)
        {
            BoundaryPathFlags flags = path.Flags;
            if ((flags & BoundaryPathFlags.NotClosed) != 0)
            {
                throw new ArgumentException("A HATCH boundary loop is marked as open.");
            }
            if ((flags & (BoundaryPathFlags.IsAnnotative |
                          BoundaryPathFlags.IsAnnotativeBlock |
                          BoundaryPathFlags.ForceAnnoAllVisible |
                          BoundaryPathFlags.OrientToPaper)) != 0)
            {
                throw new CadUnsupportedEntityException(
                    "Annotative or paper-oriented HATCH loops require viewport-context lowering.");
            }

            int segmentOffset = localSegments.Count;
            if (path.IsPolyline)
            {
                HatchPolyline polyline = path.Edges.OfType<HatchPolyline>().Single();
                AddPolylineLoop(polyline, localSegments, ref hasCurves);
            }
            else
            {
                AddEdgeLoop(path, localSegments, ref hasCurves);
            }

            int segmentCount = localSegments.Count - segmentOffset;
            if (segmentCount == 0)
            {
                throw new ArgumentException("A HATCH boundary loop contains no drawable segments.");
            }
            ValidateClosedLoop(localSegments, segmentOffset, segmentCount);
            localLoops.Add(new CadHatchLoop(segmentOffset, segmentCount));
        }

        if (checked(loops.Count + localLoops.Count) > options.MaxHatchLoops)
        {
            throw new CadUnsupportedEntityException(
                $"The configured {options.MaxHatchLoops}-loop HATCH document limit was reached.");
        }
        if (checked(segments.Count + localSegments.Count) > options.MaxHatchSegments)
        {
            throw new CadUnsupportedEntityException(
                $"The configured {options.MaxHatchSegments}-segment HATCH document limit was reached.");
        }
        if (localPattern.HasValue && checked(patterns.Count + 1) > options.MaxHatchPatterns)
        {
            throw new CadUnsupportedEntityException(
                $"The configured {options.MaxHatchPatterns}-pattern HATCH document limit was reached.");
        }
        if (localPattern is { } boundedPattern &&
            checked(patternFamilies.Count + boundedPattern.Families.Length) >
                options.MaxHatchPatternFamilies)
        {
            throw new CadUnsupportedEntityException(
                $"The configured {options.MaxHatchPatternFamilies}-family HATCH document limit was reached.");
        }
        if (localPattern is { } dashedPattern &&
            checked(patternDashes.Count + dashedPattern.Dashes.Length) >
                options.MaxHatchPatternDashes)
        {
            throw new CadUnsupportedEntityException(
                $"The configured {options.MaxHatchPatternDashes}-dash HATCH document limit was reached.");
        }

        CadHatchSegment first = localSegments[0];
        double localOriginX = first.StartX;
        double localOriginY = first.StartY;
        CadPoint3D localWorldOrigin = CadCoordinateSystem.FromNormal(ToPoint(hatch.Normal)).Transform(
            new CadPoint3D(localOriginX, localOriginY, hatch.Elevation));
        CadPoint3D worldOrigin = TransformPoint(transform, hasTransform, localWorldOrigin);
        EnsureFinite(worldOrigin);

        CadBounds3D bounds = CadBounds3D.Empty;
        for (int i = 0; i < localSegments.Count; i++)
        {
            CadHatchSegment shifted = ShiftSegment(localSegments[i], localOriginX, localOriginY);
            localSegments[i] = shifted;
            bounds = bounds.Union(GetWorldBounds(worldOrigin, coordinateSystem, shifted));
        }
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("A HATCH boundary has no finite area bounds.");
        }

        int loopOffset = loops.Count;
        int segmentBase = segments.Count;
        for (int i = 0; i < localLoops.Count; i++)
        {
            CadHatchLoop loop = localLoops[i];
            loops.Add(loop with { SegmentOffset = checked(segmentBase + loop.SegmentOffset) });
        }
        segments.AddRange(localSegments);
        int patternIndex = -1;
        if (localPattern is { } retainedPattern)
        {
            patternIndex = patterns.Count;
            int familyOffset = patternFamilies.Count;
            int dashOffset = patternDashes.Count;
            for (int i = 0; i < retainedPattern.Families.Length; i++)
            {
                CadHatchPatternFamily family = retainedPattern.Families[i];
                patternFamilies.Add(family with
                {
                    BasePointX = family.BasePointX - localOriginX,
                    BasePointY = family.BasePointY - localOriginY,
                    DashOffset = checked(dashOffset + family.DashOffset),
                });
            }
            patternDashes.AddRange(retainedPattern.Dashes);
            patterns.Add(new CadHatchPattern(familyOffset, retainedPattern.Families.Length));
        }
        int primitiveIndex = destination.Count;
        destination.Add(new CadHatchPrimitive(
            worldOrigin,
            coordinateSystem,
            loopOffset,
            localLoops.Count,
            hasCurves,
            patternIndex));
        return new CadEntityHeader(
            handle,
            CadEntityKind.Hatch,
            layerIndex,
            styleIndex,
            primitiveIndex,
            bounds);
    }

    private readonly record struct CompiledHatchPattern(
        CadHatchPatternFamily[] Families,
        double[] Dashes);

    private static CompiledHatchPattern CompilePattern(Hatch hatch)
    {
        HatchPattern pattern = hatch.Pattern ??
            throw new CadUnsupportedEntityException(
                "A patterned HATCH requires a persisted pattern definition.");
        if (pattern.Lines.Count == 0)
        {
            throw new CadUnsupportedEntityException(
                "A patterned HATCH requires at least one persisted line family.");
        }

        int extraFamilyCount = hatch.IsDouble &&
            hatch.PatternType == HatchPatternType.PatternFill
            ? pattern.Lines.Count
            : 0;
        var families = new CadHatchPatternFamily[checked(pattern.Lines.Count + extraFamilyCount)];
        var dashes = new List<double>(checked(pattern.Lines.Count * HatchPatternSetBrush.MaximumDashCount));
        for (int lineIndex = 0; lineIndex < pattern.Lines.Count; lineIndex++)
        {
            families[lineIndex] = CompilePatternFamily(pattern.Lines[lineIndex], dashes);
        }

        if (extraFamilyCount != 0)
        {
            for (int i = 0; i < pattern.Lines.Count; i++)
            {
                CadHatchPatternFamily source = families[i];
                families[pattern.Lines.Count + i] = source with
                {
                    DirectionX = -source.DirectionY,
                    DirectionY = source.DirectionX,
                };
            }
        }
        return new CompiledHatchPattern(families, dashes.ToArray());
    }

    private static CadHatchPatternFamily CompilePatternFamily(
        HatchPattern.Line line,
        List<double> dashes)
    {
        if (line.DashLengths.Count > HatchPatternSetBrush.MaximumDashCount)
        {
            throw new CadUnsupportedEntityException(
                $"A HATCH pattern family exceeds Autodesk's {HatchPatternSetBrush.MaximumDashCount}-dash PAT definition limit.");
        }
        if (!double.IsFinite(line.Angle) ||
            !double.IsFinite(line.BasePoint.X) ||
            !double.IsFinite(line.BasePoint.Y) ||
            !double.IsFinite(line.Offset.X) ||
            !double.IsFinite(line.Offset.Y))
        {
            throw new ArgumentException("HATCH pattern coordinates must be finite.");
        }

        double cosine = Math.Cos(line.Angle);
        double sine = Math.Sin(line.Angle);
        double normalX = -sine;
        double normalY = cosine;
        double signedSpacing =
            (line.Offset.X * normalX) +
            (line.Offset.Y * normalY);
        double tangentShift =
            (line.Offset.X * cosine) +
            (line.Offset.Y * sine);
        double spacing = signedSpacing;
        if (spacing < 0.0)
        {
            spacing = -spacing;
            tangentShift = -tangentShift;
        }
        if (!double.IsFinite(spacing) || spacing <= 1e-12)
        {
            throw new CadUnsupportedEntityException(
                "A HATCH family requires a finite positive perpendicular spacing.");
        }

        int dashOffset = dashes.Count;
        double dashPeriod = 0.0;
        bool draws = false;
        foreach (double dash in line.DashLengths)
        {
            if (!double.IsFinite(dash))
                throw new ArgumentException("HATCH dash values must be finite.");
            dashPeriod += Math.Abs(dash);
            draws |= dash >= 0.0;
            dashes.Add(dash);
        }
        if (line.DashLengths.Count != 0 &&
            (!draws || !double.IsFinite(dashPeriod) || dashPeriod <= 1e-12))
        {
            throw new CadUnsupportedEntityException(
                "A dashed HATCH family requires a positive finite repeating period and at least one drawn segment or dot.");
        }

        return new CadHatchPatternFamily(
            line.BasePoint.X,
            line.BasePoint.Y,
            cosine,
            sine,
            tangentShift,
            spacing,
            dashOffset,
            line.DashLengths.Count,
            dashPeriod);
    }

    private static void AddPolylineLoop(
        HatchPolyline polyline,
        List<CadHatchSegment> destination,
        ref bool hasCurves)
    {
        if (!polyline.IsClosed)
        {
            throw new ArgumentException("A polyline HATCH boundary must be closed.");
        }
        int count = polyline.Vertices.Count;
        if (count > 1 && PointsCoincide(
                polyline.Vertices[0].X,
                polyline.Vertices[0].Y,
                polyline.Vertices[^1].X,
                polyline.Vertices[^1].Y))
        {
            if (polyline.Vertices[^1].Z != 0.0)
            {
                throw new ArgumentException(
                    "A duplicate terminal HATCH vertex cannot carry a closing bulge.");
            }
            count--;
        }
        if (count < 3)
        {
            throw new ArgumentException("A polyline HATCH boundary requires at least three vertices.");
        }

        for (int i = 0; i < count; i++)
        {
            XYZ start = polyline.Vertices[i];
            XYZ end = polyline.Vertices[(i + 1) % count];
            EnsureFiniteHatchVertex(start);
            EnsureFiniteHatchVertex(end);
            if (start.Z == 0.0)
            {
                AddLineSegment(start.X, start.Y, end.X, end.Y, destination);
                continue;
            }

            var startVertex = new CadPolylineVertex(start.X, start.Y, start.Z);
            var endVertex = new CadPolylineVertex(end.X, end.Y, end.Z);
            GetBulgeArc(
                startVertex,
                endVertex,
                out double centerX,
                out double centerY,
                out double radius,
                out double startParameter,
                out double sweepParameter);
            destination.Add(CreateArcSegment(
                start.X,
                start.Y,
                end.X,
                end.Y,
                centerX,
                centerY,
                radius,
                0.0,
                0.0,
                radius,
                startParameter,
                sweepParameter));
            hasCurves = true;
        }
    }

    private static void AddEdgeLoop(
        Hatch.BoundaryPath path,
        List<CadHatchSegment> destination,
        ref bool hasCurves)
    {
        if (path.Edges.Count == 0)
        {
            throw new ArgumentException("A HATCH edge boundary must contain at least one edge.");
        }

        foreach (Hatch.BoundaryPath.Edge edge in path.Edges)
        {
            switch (edge)
            {
                case HatchLine line:
                    AddLineSegment(line.Start.X, line.Start.Y, line.End.X, line.End.Y, destination);
                    break;
                case HatchArc arc:
                    AddCircularArc(arc, destination);
                    hasCurves = true;
                    break;
                case HatchEllipse ellipse:
                    AddEllipticArc(ellipse, destination);
                    hasCurves = true;
                    break;
                case HatchPolyline:
                    throw new ArgumentException(
                        "A polyline HATCH boundary cannot be mixed with other edge records.");
                case Hatch.BoundaryPath.Spline:
                    throw new CadUnsupportedEntityException(
                        "Spline HATCH edges require an exact rational filled-path segment contract.");
                default:
                    throw new CadUnsupportedEntityException(
                        $"HATCH edge type {edge.Type} has no retained analytic representation.");
            }
        }
    }

    private static void AddLineSegment(
        double startX,
        double startY,
        double endX,
        double endY,
        List<CadHatchSegment> destination)
    {
        EnsureFiniteHatchPoint(startX, startY);
        EnsureFiniteHatchPoint(endX, endY);
        if (PointsCoincide(startX, startY, endX, endY))
        {
            throw new ArgumentException("A HATCH line edge must have distinct endpoints.");
        }
        destination.Add(new CadHatchSegment(
            CadHatchSegmentKind.Line,
            startX,
            startY,
            endX,
            endY,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0));
    }

    private static void AddCircularArc(
        HatchArc arc,
        List<CadHatchSegment> destination)
    {
        EnsureFiniteHatchPoint(arc.Center.X, arc.Center.Y);
        ValidateRadius(arc.Radius);
        double sweep = GetDirectedHatchSweep(
            arc.StartAngle,
            arc.EndAngle,
            arc.CounterClockWise);
        double startX = arc.Center.X + (arc.Radius * Math.Cos(arc.StartAngle));
        double startY = arc.Center.Y + (arc.Radius * Math.Sin(arc.StartAngle));
        double endParameter = arc.StartAngle + sweep;
        double endX = arc.Center.X + (arc.Radius * Math.Cos(endParameter));
        double endY = arc.Center.Y + (arc.Radius * Math.Sin(endParameter));
        destination.Add(CreateArcSegment(
            startX,
            startY,
            endX,
            endY,
            arc.Center.X,
            arc.Center.Y,
            arc.Radius,
            0.0,
            0.0,
            arc.Radius,
            NormalizeAngle(arc.StartAngle),
            sweep));
    }

    private static void AddEllipticArc(
        HatchEllipse ellipse,
        List<CadHatchSegment> destination)
    {
        EnsureFiniteHatchPoint(ellipse.Center.X, ellipse.Center.Y);
        EnsureFiniteHatchPoint(
            ellipse.MajorAxisEndPoint.X,
            ellipse.MajorAxisEndPoint.Y);
        double majorLength = new CadPoint3D(
            ellipse.MajorAxisEndPoint.X,
            ellipse.MajorAxisEndPoint.Y,
            0.0).Length;
        if (!double.IsFinite(majorLength) || majorLength <= 0.0 ||
            !double.IsFinite(ellipse.RadiusRatio) ||
            ellipse.RadiusRatio <= 0.0 || ellipse.RadiusRatio > 1.0)
        {
            throw new ArgumentException(
                "HATCH ellipse axes and ratio must be finite and positive, with ratio at most one.");
        }
        double cosineAxisX = ellipse.MajorAxisEndPoint.X;
        double cosineAxisY = ellipse.MajorAxisEndPoint.Y;
        double sineAxisX = -cosineAxisY * ellipse.RadiusRatio;
        double sineAxisY = cosineAxisX * ellipse.RadiusRatio;
        double sweep = GetDirectedHatchSweep(
            ellipse.StartAngle,
            ellipse.EndAngle,
            ellipse.CounterClockWise);
        double start = NormalizeAngle(ellipse.StartAngle);
        GetEllipsePoint(
            ellipse.Center.X,
            ellipse.Center.Y,
            cosineAxisX,
            cosineAxisY,
            sineAxisX,
            sineAxisY,
            start,
            out double startX,
            out double startY);
        GetEllipsePoint(
            ellipse.Center.X,
            ellipse.Center.Y,
            cosineAxisX,
            cosineAxisY,
            sineAxisX,
            sineAxisY,
            start + sweep,
            out double endX,
            out double endY);
        destination.Add(CreateArcSegment(
            startX,
            startY,
            endX,
            endY,
            ellipse.Center.X,
            ellipse.Center.Y,
            cosineAxisX,
            cosineAxisY,
            sineAxisX,
            sineAxisY,
            start,
            sweep));
    }

    private static CadHatchSegment CreateArcSegment(
        double startX,
        double startY,
        double endX,
        double endY,
        double centerX,
        double centerY,
        double cosineAxisX,
        double cosineAxisY,
        double sineAxisX,
        double sineAxisY,
        double startParameter,
        double sweepParameter)
    {
        EnsureFiniteHatchPoint(startX, startY);
        EnsureFiniteHatchPoint(endX, endY);
        EnsureFiniteHatchPoint(centerX, centerY);
        EnsureFiniteHatchPoint(cosineAxisX, cosineAxisY);
        EnsureFiniteHatchPoint(sineAxisX, sineAxisY);
        if (!double.IsFinite(startParameter) || !double.IsFinite(sweepParameter) ||
            sweepParameter == 0.0 || Math.Abs(sweepParameter) > TwoPi + 1e-12)
        {
            throw new ArgumentException("A HATCH arc must have a finite non-zero sweep of at most one turn.");
        }
        return new CadHatchSegment(
            CadHatchSegmentKind.EllipticArc,
            startX,
            startY,
            endX,
            endY,
            centerX,
            centerY,
            cosineAxisX,
            cosineAxisY,
            sineAxisX,
            sineAxisY,
            startParameter,
            sweepParameter);
    }

    private static double GetDirectedHatchSweep(
        double start,
        double end,
        bool counterClockwise)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end))
        {
            throw new ArgumentException("HATCH arc parameters must be finite.");
        }
        double raw = counterClockwise ? end - start : start - end;
        double magnitude = NormalizePositiveSweep(0.0, raw);
        if (Math.Abs(raw) >= TwoPi - 1e-12 || magnitude <= 1e-12)
        {
            magnitude = TwoPi;
        }
        return counterClockwise ? magnitude : -magnitude;
    }

    private static void ValidateClosedLoop(
        List<CadHatchSegment> segments,
        int offset,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            CadHatchSegment current = segments[offset + i];
            CadHatchSegment next = segments[offset + ((i + 1) % count)];
            if (!PointsCoincide(current.EndX, current.EndY, next.StartX, next.StartY))
            {
                throw new ArgumentException(
                    "A HATCH boundary loop contains disconnected edge endpoints.");
            }
        }
    }

    private static CadHatchSegment ShiftSegment(
        CadHatchSegment segment,
        double originX,
        double originY) =>
        segment with
        {
            StartX = segment.StartX - originX,
            StartY = segment.StartY - originY,
            EndX = segment.EndX - originX,
            EndY = segment.EndY - originY,
            CenterX = segment.Kind == CadHatchSegmentKind.EllipticArc
                ? segment.CenterX - originX
                : 0.0,
            CenterY = segment.Kind == CadHatchSegmentKind.EllipticArc
                ? segment.CenterY - originY
                : 0.0,
        };

    private static CadBounds3D GetWorldBounds(
        CadPoint3D worldOrigin,
        CadCoordinateSystem coordinateSystem,
        CadHatchSegment segment)
    {
        CadPoint3D start = ToHatchWorldPoint(
            worldOrigin,
            coordinateSystem,
            segment.StartX,
            segment.StartY);
        CadPoint3D end = ToHatchWorldPoint(
            worldOrigin,
            coordinateSystem,
            segment.EndX,
            segment.EndY);
        if (segment.Kind == CadHatchSegmentKind.Line)
        {
            return CadBounds3D.FromPoint(start).Include(end);
        }

        CadPoint3D center = ToHatchWorldPoint(
            worldOrigin,
            coordinateSystem,
            segment.CenterX,
            segment.CenterY);
        CadPoint3D cosineAxis = ToHatchWorldVector(
            coordinateSystem,
            segment.CosineAxisX,
            segment.CosineAxisY);
        CadPoint3D sineAxis = ToHatchWorldVector(
            coordinateSystem,
            segment.SineAxisX,
            segment.SineAxisY);
        return CadBounds3D.EllipseArc(
            center,
            cosineAxis,
            sineAxis,
            segment.StartParameter,
            segment.SweepParameter);
    }

    internal static CadPoint3D ToHatchWorldPoint(
        CadPoint3D worldOrigin,
        CadCoordinateSystem coordinateSystem,
        double x,
        double y) =>
        worldOrigin + ToHatchWorldVector(coordinateSystem, x, y);

    internal static CadPoint3D ToHatchWorldVector(
        CadCoordinateSystem coordinateSystem,
        double x,
        double y) =>
        (coordinateSystem.XAxis * x) + (coordinateSystem.YAxis * y);

    private static void GetEllipsePoint(
        double centerX,
        double centerY,
        double cosineAxisX,
        double cosineAxisY,
        double sineAxisX,
        double sineAxisY,
        double parameter,
        out double x,
        out double y)
    {
        double cosine = Math.Cos(parameter);
        double sine = Math.Sin(parameter);
        x = centerX + (cosineAxisX * cosine) + (sineAxisX * sine);
        y = centerY + (cosineAxisY * cosine) + (sineAxisY * sine);
    }

    private static bool PointsCoincide(
        double firstX,
        double firstY,
        double secondX,
        double secondY)
    {
        double scale = Math.Max(
            1.0,
            Math.Max(
                Math.Max(Math.Abs(firstX), Math.Abs(firstY)),
                Math.Max(Math.Abs(secondX), Math.Abs(secondY))));
        double tolerance = HatchClosureToleranceScale * scale;
        return Math.Abs(firstX - secondX) <= tolerance &&
            Math.Abs(firstY - secondY) <= tolerance;
    }

    private static void EnsureFiniteHatchVertex(XYZ vertex)
    {
        if (!double.IsFinite(vertex.X) ||
            !double.IsFinite(vertex.Y) ||
            !double.IsFinite(vertex.Z))
        {
            throw new ArgumentException("HATCH polyline coordinates and bulges must be finite.");
        }
    }

    private static void EnsureFiniteHatchPoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentException("HATCH boundary coordinates must be finite.");
        }
    }
}
