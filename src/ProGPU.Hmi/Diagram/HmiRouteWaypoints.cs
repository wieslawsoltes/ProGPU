namespace ProGPU.Hmi;

/// <summary>Bounded, UI-independent constraints shared by persistence, routing and authoring.</summary>
public static class HmiRouteWaypoints
{
    public const int MaximumCount = 16;
    public const float MaximumCoordinate = 65536;

    public static void Validate(IReadOnlyList<HmiPoint>? points)
    {
        if (points is null || points.Count > MaximumCount)
            throw new InvalidDataException($"A diagram link supports at most {MaximumCount} ordered waypoints.");
        for (int i = 0; i < points.Count; i++)
        {
            ValidatePoint(points[i]);
            // The bounded quadratic check avoids allocating a hash set for every preview.
            for (int j = 0; j < i; j++)
                if (points[i] == points[j]) throw new InvalidDataException("Waypoints must have distinct positions.");
        }
    }

    public static void ValidatePoint(HmiPoint point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
            Math.Abs(point.X) > MaximumCoordinate || Math.Abs(point.Y) > MaximumCoordinate)
            throw new InvalidDataException($"Waypoint coordinates must be finite and within ±{MaximumCoordinate} document units.");
    }

    /// <summary>Create an independent translated list; reject the whole operation before returning on overflow.</summary>
    public static List<HmiPoint> Translate(IReadOnlyList<HmiPoint> points, float dx, float dy)
    {
        Validate(points);
        if (!float.IsFinite(dx) || !float.IsFinite(dy)) throw new ArgumentOutOfRangeException(nameof(dx));
        var result = new List<HmiPoint>(points.Count);
        foreach (var point in points) result.Add(new(point.X + dx, point.Y + dy));
        Validate(result);
        return result;
    }

    /// <summary>
    /// Insert near the closest route segment while retaining the order of existing pins.
    /// The clicked point is not moved onto the route. Unroutable links append a recovery pin.
    /// Ties choose the first source-to-target segment, independent of dictionary order.
    /// </summary>
    public static int FindInsertionIndex(HmiRouteResult? route, IReadOnlyList<HmiPoint> pins, HmiPoint point)
    {
        Validate(pins); ValidatePoint(point);
        if (route?.Status != HmiRouteStatus.Success || route.Points.Count < 2) return pins.Count;
        double best = double.PositiveInfinity, nearestDistance = 0, traveled = 0;
        var arrivals = new double[pins.Count];
        int nextPin = 0;
        if (pins.Count > 0 && pins[0] == route.Points[0]) arrivals[nextPin++] = 0;
        for (int i = 1; i < route.Points.Count; i++)
        {
            var a = route.Points[i - 1]; var b = route.Points[i];
            double dx = (double)b.X - a.X, dy = (double)b.Y - a.Y;
            double squaredLength = dx * dx + dy * dy;
            double length = Math.Sqrt(squaredLength);
            double fraction = squaredLength == 0 ? 0 : Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / squaredLength, 0, 1);
            double ex = point.X - (a.X + fraction * dx), ey = point.Y - (a.Y + fraction * dy);
            double squaredDistance = ex * ex + ey * ey;
            if (squaredDistance < best) { best = squaredDistance; nearestDistance = traveled + fraction * length; }
            traveled += length;
            if (nextPin < pins.Count && b == pins[nextPin]) arrivals[nextPin++] = traveled;
        }
        // A stale route must not reorder constraints whose positions it no longer contains.
        if (nextPin != pins.Count) return pins.Count;
        int index = 0;
        while (index < arrivals.Length && arrivals[index] <= nearestDistance) index++;
        return index;
    }
}
