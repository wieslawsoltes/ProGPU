namespace ProGPU.Hmi;

/// <summary>Owned immutable output of a perpendicular segment preview.</summary>
public sealed class HmiRouteConstraints
{
    public IReadOnlyList<HmiPoint> Waypoints { get; }
    public IReadOnlyList<int> StraightSegments { get; }
    internal HmiRouteConstraints(HmiPoint[] points, int[] segments)
    { Waypoints = Array.AsReadOnly(points); StraightSegments = Array.AsReadOnly(segments); }
}

/// <summary>
/// Maps one computed source-ordered edge into exact adjacent waypoint constraints.
/// Original pins outside that edge retain their positions and relative order.
/// A plan belongs to one route/document generation; the authoring owner enforces that lifetime.
/// </summary>
public sealed class HmiRouteSegmentEdit
{
    private readonly HmiPoint[] _points;
    private readonly int[] _segments;
    private readonly int _first, _last;
    public HmiRouteSegment Segment { get; }

    public HmiRouteSegmentEdit(HmiRouteResult route, HmiRouteSegment segment,
        IReadOnlyList<HmiPoint> waypoints, IReadOnlyList<int> straightSegments)
    {
        ArgumentNullException.ThrowIfNull(route); HmiRouteSegments.Validate(waypoints, straightSegments);
        if (route.Status != HmiRouteStatus.Success || route.Points.Count > HmiOrthogonalRouter.MaximumRoutePoints ||
            segment.Index < 0 || segment.Index >= route.Points.Count - 1 || segment.Start == segment.End ||
            !HmiRouteSegments.OnSegment(segment.Start, route.Points[segment.Index], route.Points[segment.Index + 1]) ||
            !HmiRouteSegments.OnSegment(segment.End, route.Points[segment.Index], route.Points[segment.Index + 1]))
            throw new ArgumentException("Select an existing nonempty routed segment.", nameof(segment));
        Segment = segment;
        var arrivals = new double[waypoints.Count];
        double traveled = 0, edgeStart = 0;
        int next = 0;
        for (int i = 0; i < route.Points.Count; i++)
        {
            if (i > 0) traveled += Distance(route.Points[i - 1], route.Points[i]);
            if (i == segment.Index) edgeStart = traveled;
            if (next < waypoints.Count && route.Points[i] == waypoints[next]) arrivals[next++] = traveled;
        }
        if (next != waypoints.Count) throw new ArgumentException("The route does not contain this ordered waypoint generation.", nameof(route));
        double start = edgeStart + Distance(route.Points[segment.Index], segment.Start);
        double end = edgeStart + Distance(route.Points[segment.Index], segment.End);
        if (end <= start) throw new ArgumentException("Segment direction must follow source-to-target route order.", nameof(segment));
        var points = new List<HmiPoint>(); var mapping = new int[waypoints.Count];
        int pin = 0;
        while (pin < waypoints.Count && arrivals[pin] < start) { mapping[pin] = points.Count; points.Add(waypoints[pin++]); }
        _first = points.Count; points.Add(segment.Start);
        if (pin < waypoints.Count && arrivals[pin] == start) mapping[pin++] = _first;
        while (pin < waypoints.Count && arrivals[pin] < end) { mapping[pin] = points.Count; points.Add(waypoints[pin++]); }
        _last = points.Count; points.Add(segment.End);
        if (pin < waypoints.Count && arrivals[pin] == end) mapping[pin++] = _last;
        while (pin < waypoints.Count) { mapping[pin] = points.Count; points.Add(waypoints[pin++]); }
        var segments = new SortedSet<int>();
        foreach (int old in straightSegments)
            for (int index = mapping[old]; index < mapping[old + 1]; index++) segments.Add(index);
        for (int index = _first; index < _last; index++) segments.Add(index);
        _points = points.ToArray(); _segments = segments.ToArray();
        HmiRouteSegments.Validate(_points, _segments);
    }

    /// <summary>Capture a persisted straight span for recovery when no successful computed route exists.</summary>
    public HmiRouteSegmentEdit(IReadOnlyList<HmiPoint> waypoints, IReadOnlyList<int> straightSegments, int spanIndex)
    {
        HmiRouteSegments.Validate(waypoints, straightSegments);
        if (!straightSegments.Contains(spanIndex)) throw new ArgumentOutOfRangeException(nameof(spanIndex));
        _points = waypoints.ToArray(); _segments = straightSegments.ToArray();
        _first = spanIndex; _last = spanIndex + 1;
        Segment = new(HmiOrthogonalRouter.MaximumRoutePoints + spanIndex, _points[_first], _points[_last], spanIndex);
    }

    /// <summary>Offset is perpendicular to the captured edge: Y for horizontal, X for vertical.</summary>
    public HmiRouteConstraints Preview(float offset)
    {
        if (!float.IsFinite(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        var points = (HmiPoint[])_points.Clone();
        for (int i = _first; i <= _last; i++) points[i] = Segment.IsHorizontal
            ? points[i] with { Y = points[i].Y + offset } : points[i] with { X = points[i].X + offset };
        // Adjacent pre-existing straight constraints also remain authoritative. They cannot become diagonal.
        HmiRouteSegments.Validate(points, _segments);
        return new(points, (int[])_segments.Clone());
    }

    private static double Distance(HmiPoint a, HmiPoint b) => Math.Abs((double)a.X - b.X) + Math.Abs((double)a.Y - b.Y);
}
