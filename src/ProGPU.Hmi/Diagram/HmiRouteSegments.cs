namespace ProGPU.Hmi;

/// <summary>A source-ordered portion of a computed route, excluding fixed nozzle escape leads.</summary>
public readonly record struct HmiRouteSegment(int Index, HmiPoint Start, HmiPoint End, int StraightSpanIndex = -1)
{
    public bool IsHorizontal => Start.Y == End.Y;
    public HmiPoint Midpoint => new((Start.X + End.X) / 2, (Start.Y + End.Y) / 2);
}

/// <summary>Bounded straight-span configuration and immutable route-edit snapshots. No UI dependency.</summary>
public static class HmiRouteSegments
{
    public static void Validate(IReadOnlyList<HmiPoint> points, IReadOnlyList<int>? segments)
    {
        HmiRouteWaypoints.Validate(points);
        if (segments is null || segments.Count > HmiRouteWaypoints.MaximumCount - 1)
            throw new InvalidDataException("Straight-segment indexes must be a non-null bounded list.");
        int previous = -1;
        foreach (int index in segments)
        {
            if (index <= previous || index < 0 || index >= points.Count - 1)
                throw new InvalidDataException("Straight-segment indexes must be unique, ascending and refer to adjacent waypoints.");
            var a = points[index]; var b = points[index + 1];
            if (a.X != b.X && a.Y != b.Y)
                throw new InvalidDataException("A straight constraint must be horizontal or vertical. Move its segment or release the constraint before moving an individual pin sideways.");
            previous = index;
        }
    }

    public static IReadOnlyList<HmiRouteSegment> GetEditableSegments(HmiRouteResult route,
        HmiRouteTerminal source, HmiRouteTerminal target, float clearance)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!float.IsFinite(clearance) || clearance is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(clearance));
        if (route.Status != HmiRouteStatus.Success) return Array.Empty<HmiRouteSegment>();
        if (route.Points.Count > HmiOrthogonalRouter.MaximumRoutePoints || route.Points.Count < 2 ||
            route.Points[0] != source.Point || route.Points[^1] != target.Point)
            throw new ArgumentException("The route must belong to these terminals.", nameof(route));
        var result = new List<HmiRouteSegment>();
        var sourceExit = HmiOrthogonalRouter.Exit(source, clearance);
        var targetExit = HmiOrthogonalRouter.Exit(target, clearance);
        for (int i = 0; i < route.Points.Count - 1; i++)
        {
            var a = route.Points[i]; var b = route.Points[i + 1];
            if (i == 0) a = sourceExit;
            if (i == route.Points.Count - 2) b = targetExit;
            if (a == b || !OnSegment(a, route.Points[i], route.Points[i + 1]) || !OnSegment(b, route.Points[i], route.Points[i + 1])) continue;
            // A short direct route can have overlapping escape leads; it has no editable middle.
            double dot = ((double)b.X - a.X) * ((double)route.Points[i + 1].X - route.Points[i].X) +
                ((double)b.Y - a.Y) * ((double)route.Points[i + 1].Y - route.Points[i].Y);
            if (dot > 0) result.Add(new(i, a, b));
        }
        return result.AsReadOnly();
    }

    public static void InsertWaypoint(HmiDiagramLink link, int index, HmiPoint point)
    {
        ArgumentNullException.ThrowIfNull(link); Validate(link.Waypoints, link.StraightSegments);
        if ((uint)index > (uint)link.Waypoints.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var points = new List<HmiPoint>(link.Waypoints); points.Insert(index, point);
        var segments = new List<int>();
        foreach (int start in link.StraightSegments)
        {
            if (start == index - 1)
            {
                if (!OnSegment(point, link.Waypoints[start], link.Waypoints[start + 1]))
                    throw new InvalidDataException("Insert on the straight span or release its constraint first.");
                segments.Add(start); segments.Add(start + 1);
            }
            else segments.Add(start >= index ? start + 1 : start);
        }
        Validate(points, segments); link.Waypoints = points; link.StraightSegments = segments;
    }

    /// <summary>Removing a pin explicitly releases its incident straight spans, never silently pins a new connection.</summary>
    public static void RemoveWaypoint(HmiDiagramLink link, int index)
    {
        ArgumentNullException.ThrowIfNull(link); Validate(link.Waypoints, link.StraightSegments);
        if ((uint)index >= (uint)link.Waypoints.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var points = new List<HmiPoint>(link.Waypoints); points.RemoveAt(index);
        var segments = link.StraightSegments.Where(i => i != index && i + 1 != index).Select(i => i > index ? i - 1 : i).ToList();
        Validate(points, segments); link.Waypoints = points; link.StraightSegments = segments;
    }

    internal static bool OnSegment(HmiPoint p, HmiPoint a, HmiPoint b) => a.Y == b.Y
        ? p.Y == a.Y && p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X)
        : a.X == b.X && p.X == a.X && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
}
