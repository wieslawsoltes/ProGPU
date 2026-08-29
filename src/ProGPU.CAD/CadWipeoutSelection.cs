namespace ProGPU.CAD;

/// <summary>Exact allocation-free selection for retained WIPEOUT masks and frames.</summary>
internal static class CadWipeoutSelection
{
    public static CadPointHitResult HitTestPoint(
        CadDocumentSnapshot snapshot,
        in CadWipeoutPrimitive wipeout,
        CadPoint3D point,
        double tolerance)
    {
        Span<CadWipeoutClipPoint> outer = stackalloc CadWipeoutClipPoint[4];
        GetOuter(wipeout, outer);
        ReadOnlySpan<CadWipeoutClipPoint> clip = GetClip(snapshot, wipeout);
        ReadOnlySpan<CadWipeoutClipPoint> frame = wipeout.IsClipped ? clip : outer;
        double distance = DistanceToLoop(wipeout, frame, point);
        bool selectMask = HasSelectablePlanMask(wipeout);
        if (selectMask && wipeout.IsInverted)
        {
            distance = Math.Min(distance, DistanceToLoop(wipeout, outer, point));
        }
        if (distance <= tolerance)
        {
            return new CadPointHitResult(CadPointHitStatus.Hit, distance);
        }
        if (!selectMask ||
            !TryToLocal(wipeout, point, out double u, out double v, out double planeDistance) ||
            planeDistance > tolerance ||
            !ContainsMask(wipeout, clip, u, v))
        {
            return new CadPointHitResult(CadPointHitStatus.Miss, distance);
        }
        return new CadPointHitResult(CadPointHitStatus.Hit, planeDistance);
    }

    public static CadBoundsHitResult HitTestBounds(
        CadDocumentSnapshot snapshot,
        in CadWipeoutPrimitive wipeout,
        CadBounds3D exactBounds,
        CadBounds3D selectionBounds,
        CadBoundsSelectionMode mode)
    {
        if (selectionBounds.IsEmpty || !exactBounds.Intersects(selectionBounds))
        {
            return new CadBoundsHitResult(CadBoundsHitStatus.Miss);
        }
        if (mode == CadBoundsSelectionMode.Window)
        {
            bool contains =
                Contains(selectionBounds, exactBounds.Min) &&
                Contains(selectionBounds, exactBounds.Max);
            return new CadBoundsHitResult(
                contains ? CadBoundsHitStatus.Hit : CadBoundsHitStatus.Miss);
        }

        Span<CadWipeoutClipPoint> outer = stackalloc CadWipeoutClipPoint[4];
        GetOuter(wipeout, outer);
        ReadOnlySpan<CadWipeoutClipPoint> clip = GetClip(snapshot, wipeout);
        ReadOnlySpan<CadWipeoutClipPoint> frame = wipeout.IsClipped ? clip : outer;
        if (LoopIntersectsBounds(wipeout, frame, selectionBounds))
        {
            return new CadBoundsHitResult(CadBoundsHitStatus.Hit);
        }
        if (!HasSelectablePlanMask(wipeout))
        {
            return new CadBoundsHitResult(CadBoundsHitStatus.Miss);
        }
        if (wipeout.IsInverted &&
            LoopIntersectsBounds(wipeout, outer, selectionBounds))
        {
            return new CadBoundsHitResult(CadBoundsHitStatus.Hit);
        }

        Span<CadPoint3D> corners = stackalloc CadPoint3D[8];
        GetCorners(selectionBounds, corners);
        for (int i = 0; i < corners.Length; i++)
        {
            if (TryToLocal(wipeout, corners[i], out double u, out double v, out double distance) &&
                distance <= 1e-10 &&
                ContainsMask(wipeout, clip, u, v))
            {
                return new CadBoundsHitResult(CadBoundsHitStatus.Hit);
            }
        }

        ReadOnlySpan<(byte First, byte Second)> edges =
        [
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
        ];
        CadPoint3D normal = CadPoint3D.Cross(wipeout.UVector, wipeout.VVector);
        for (int i = 0; i < edges.Length; i++)
        {
            CadPoint3D start = corners[edges[i].First];
            CadPoint3D end = corners[edges[i].Second];
            double startSide = CadPoint3D.Dot(start - wipeout.Origin, normal);
            double endSide = CadPoint3D.Dot(end - wipeout.Origin, normal);
            if ((startSide < 0.0 && endSide < 0.0) ||
                (startSide > 0.0 && endSide > 0.0) ||
                startSide == endSide)
            {
                continue;
            }
            double parameter = startSide / (startSide - endSide);
            CadPoint3D intersection = start + ((end - start) * parameter);
            if (TryToLocal(wipeout, intersection, out double u, out double v, out _) &&
                ContainsMask(wipeout, clip, u, v))
            {
                return new CadBoundsHitResult(CadBoundsHitStatus.Hit);
            }
        }
        return new CadBoundsHitResult(CadBoundsHitStatus.Miss);
    }

    private static ReadOnlySpan<CadWipeoutClipPoint> GetClip(
        CadDocumentSnapshot snapshot,
        in CadWipeoutPrimitive wipeout) =>
        wipeout.IsClipped
            ? snapshot.WipeoutClipPoints.Span.Slice(
                wipeout.ClipPointOffset,
                wipeout.ClipPointCount)
            : ReadOnlySpan<CadWipeoutClipPoint>.Empty;

    private static bool HasSelectablePlanMask(in CadWipeoutPrimitive wipeout)
    {
        if (!wipeout.DrawMask)
        {
            return false;
        }
        CadPoint3D plane = CadPoint3D.Cross(wipeout.UVector, wipeout.VVector);
        double length = plane.Length;
        return wipeout.ShowWhenNotAligned ||
            (Math.Abs(plane.X) <= length * 1e-10 &&
             Math.Abs(plane.Y) <= length * 1e-10);
    }

    private static void GetOuter(
        in CadWipeoutPrimitive wipeout,
        Span<CadWipeoutClipPoint> points)
    {
        points[0] = new CadWipeoutClipPoint(0.0, 0.0);
        points[1] = new CadWipeoutClipPoint(wipeout.Width, 0.0);
        points[2] = new CadWipeoutClipPoint(wipeout.Width, wipeout.Height);
        points[3] = new CadWipeoutClipPoint(0.0, wipeout.Height);
    }

    private static bool ContainsMask(
        in CadWipeoutPrimitive wipeout,
        ReadOnlySpan<CadWipeoutClipPoint> clip,
        double u,
        double v)
    {
        bool insideOuter = u >= 0.0 && u <= wipeout.Width &&
            v >= 0.0 && v <= wipeout.Height;
        if (!insideOuter)
        {
            return false;
        }
        if (!wipeout.IsClipped)
        {
            return true;
        }
        bool insideClip = PointInPolygon(clip, u, v);
        return wipeout.IsInverted ? !insideClip : insideClip;
    }

    private static bool PointInPolygon(
        ReadOnlySpan<CadWipeoutClipPoint> points,
        double u,
        double v)
    {
        bool inside = false;
        for (int current = 0, previous = points.Length - 1;
             current < points.Length;
             previous = current++)
        {
            CadWipeoutClipPoint first = points[previous];
            CadWipeoutClipPoint second = points[current];
            if ((first.V > v) != (second.V > v) &&
                u < ((second.U - first.U) * (v - first.V) /
                    (second.V - first.V)) + first.U)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static bool TryToLocal(
        in CadWipeoutPrimitive wipeout,
        CadPoint3D point,
        out double u,
        out double v,
        out double planeDistance)
    {
        CadPoint3D delta = point - wipeout.Origin;
        double uu = CadPoint3D.Dot(wipeout.UVector, wipeout.UVector);
        double uv = CadPoint3D.Dot(wipeout.UVector, wipeout.VVector);
        double vv = CadPoint3D.Dot(wipeout.VVector, wipeout.VVector);
        double determinant = (uu * vv) - (uv * uv);
        CadPoint3D normal = CadPoint3D.Cross(wipeout.UVector, wipeout.VVector);
        double normalLength = normal.Length;
        if (!double.IsFinite(determinant) || determinant <= 0.0 ||
            !double.IsFinite(normalLength) || normalLength <= 0.0)
        {
            u = v = planeDistance = double.NaN;
            return false;
        }
        double du = CadPoint3D.Dot(delta, wipeout.UVector);
        double dv = CadPoint3D.Dot(delta, wipeout.VVector);
        u = ((du * vv) - (dv * uv)) / determinant;
        v = ((dv * uu) - (du * uv)) / determinant;
        planeDistance = Math.Abs(CadPoint3D.Dot(delta, normal)) / normalLength;
        return double.IsFinite(u) && double.IsFinite(v) &&
            double.IsFinite(planeDistance);
    }

    private static double DistanceToLoop(
        in CadWipeoutPrimitive wipeout,
        ReadOnlySpan<CadWipeoutClipPoint> points,
        CadPoint3D point)
    {
        double minimum = double.PositiveInfinity;
        for (int i = 0; i < points.Length; i++)
        {
            minimum = Math.Min(
                minimum,
                DistanceToSegment(
                    point,
                    ToWorld(wipeout, points[i]),
                    ToWorld(wipeout, points[(i + 1) % points.Length])));
        }
        return minimum;
    }

    private static bool LoopIntersectsBounds(
        in CadWipeoutPrimitive wipeout,
        ReadOnlySpan<CadWipeoutClipPoint> points,
        CadBounds3D bounds)
    {
        for (int i = 0; i < points.Length; i++)
        {
            if (CadSelectionHitTester.SegmentIntersectsBounds(
                    ToWorld(wipeout, points[i]),
                    ToWorld(wipeout, points[(i + 1) % points.Length]),
                    bounds))
            {
                return true;
            }
        }
        return false;
    }

    private static CadPoint3D ToWorld(
        in CadWipeoutPrimitive wipeout,
        CadWipeoutClipPoint point) =>
        wipeout.Origin +
        (wipeout.UVector * point.U) +
        (wipeout.VVector * point.V);

    private static double DistanceToSegment(
        CadPoint3D point,
        CadPoint3D start,
        CadPoint3D end)
    {
        CadPoint3D segment = end - start;
        double lengthSquared = CadPoint3D.Dot(segment, segment);
        if (lengthSquared == 0.0)
        {
            return (point - start).Length;
        }
        double parameter = Math.Clamp(
            CadPoint3D.Dot(point - start, segment) / lengthSquared,
            0.0,
            1.0);
        return (point - (start + (segment * parameter))).Length;
    }

    private static bool Contains(CadBounds3D bounds, CadPoint3D point) =>
        point.X >= bounds.Min.X && point.X <= bounds.Max.X &&
        point.Y >= bounds.Min.Y && point.Y <= bounds.Max.Y &&
        point.Z >= bounds.Min.Z && point.Z <= bounds.Max.Z;

    private static void GetCorners(CadBounds3D bounds, Span<CadPoint3D> corners)
    {
        corners[0] = new CadPoint3D(bounds.Min.X, bounds.Min.Y, bounds.Min.Z);
        corners[1] = new CadPoint3D(bounds.Max.X, bounds.Min.Y, bounds.Min.Z);
        corners[2] = new CadPoint3D(bounds.Max.X, bounds.Max.Y, bounds.Min.Z);
        corners[3] = new CadPoint3D(bounds.Min.X, bounds.Max.Y, bounds.Min.Z);
        corners[4] = new CadPoint3D(bounds.Min.X, bounds.Min.Y, bounds.Max.Z);
        corners[5] = new CadPoint3D(bounds.Max.X, bounds.Min.Y, bounds.Max.Z);
        corners[6] = new CadPoint3D(bounds.Max.X, bounds.Max.Y, bounds.Max.Z);
        corners[7] = new CadPoint3D(bounds.Min.X, bounds.Max.Y, bounds.Max.Z);
    }
}
