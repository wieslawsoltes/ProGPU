using System.Buffers;

namespace ProGPU.Hmi;

/// <summary>
/// Deterministic, bounded, original rectilinear visibility-grid A* for document authoring.
/// All obstacle boundaries enter the grid. Search state retains arrival axis to penalize bends.
/// This dependent graph search is performed on geometry changes, never in a render or telemetry callback.
/// </summary>
public static class HmiOrthogonalRouter
{
    public const int MaximumObstacles = 128;
    public const int MaximumRoutePoints = 1024;
    public const int MaximumSearchStates = 3_000_000;
    public const int MaximumFrontierEntries = 262_144;
    private const double BendPenalty = 12;

    public static HmiRouteResult Route(HmiRouteTerminal source, HmiRouteTerminal target,
        IReadOnlyList<HmiRouteObstacle> obstacles, float clearance = 12)
        => Route(source, target, obstacles, Array.Empty<HmiPoint>(), clearance);

    /// <summary>
    /// Find a route through each waypoint in order. Search retains both arrival axis and
    /// waypoint stage, so an earlier leg cannot greedily discard a better continuation.
    /// Waypoints are exact constraints, never suggestions silently moved around obstacles.
    /// </summary>
    public static HmiRouteResult Route(HmiRouteTerminal source, HmiRouteTerminal target,
        IReadOnlyList<HmiRouteObstacle> obstacles, IReadOnlyList<HmiPoint> waypoints, float clearance = 12)
    {
        ArgumentNullException.ThrowIfNull(obstacles);
        HmiRouteWaypoints.Validate(waypoints);
        // Search consumes one owned immutable constraint generation.
        HmiPoint[] pins = waypoints.ToArray();
        if (!float.IsFinite(clearance) || clearance is < 2 or > 256 || !Valid(source) || !Valid(target))
            throw new ArgumentException("Finite nozzle geometry and 2..256 clearance are required.");
        if (source.ElementId == target.ElementId && source.Point == target.Point)
            throw new ArgumentException("A route requires two distinct terminal positions.");
        // Report bounded admission, never return a plausible-looking route through omitted obstacles.
        if (obstacles.Count > MaximumObstacles) return Fail(HmiRouteStatus.CapacityExceeded, $"Routing supports at most {MaximumObstacles} visible obstacles per screen.");
        var boxes = new HmiRouteObstacle[obstacles.Count];
        var owners = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < boxes.Length; i++)
        {
            var obstacle = obstacles[i];
            if (string.IsNullOrWhiteSpace(obstacle.ElementId) || !obstacle.Bounds.IsValid || !owners.Add(obstacle.ElementId))
                throw new ArgumentException("Routing obstacles require unique identities and finite positive extents.", nameof(obstacles));
            boxes[i] = obstacle with { Bounds = obstacle.Bounds.Inflate(clearance) };
        }
        if (!obstacles.Any(o => o.ElementId == source.ElementId && o.Bounds == source.Bounds) ||
            !obstacles.Any(o => o.ElementId == target.ElementId && o.Bounds == target.Bounds))
            throw new ArgumentException("Both terminals must reference their exact owned obstacle bounds.", nameof(obstacles));
        HmiPoint start = Exit(source, clearance), finish = Exit(target, clearance);
        foreach (var box in boxes)
        {
            if (box.ElementId != source.ElementId && Intersects(source.Point, start, box.Bounds) ||
                box.ElementId != target.ElementId && Intersects(target.Point, finish, box.Bounds) || box.Bounds.Contains(start) || box.Bounds.Contains(finish))
                return Fail(HmiRouteStatus.BlockedTerminal, "A nozzle escape is blocked by overlapping equipment. Move the equipment or reduce clearance.");
        }
        for (int i = 0; i < pins.Length; i++)
            if (boxes.Any(o => o.Bounds.Contains(pins[i])))
                return Fail(HmiRouteStatus.BlockedWaypoint, $"Waypoint {i + 1} is inside an equipment clearance envelope. Move the pin or the equipment.");
        var xs = new SortedSet<float> { start.X, finish.X };
        var ys = new SortedSet<float> { start.Y, finish.Y };
        foreach (var pin in pins) { xs.Add(pin.X); ys.Add(pin.Y); }
        foreach (var box in boxes) { xs.Add(box.Bounds.Left); xs.Add(box.Bounds.Right); ys.Add(box.Bounds.Top); ys.Add(box.Bounds.Bottom); }
        xs.Add(xs.Min - clearance); xs.Add(xs.Max + clearance); ys.Add(ys.Min - clearance); ys.Add(ys.Max + clearance);
        float[] x = xs.ToArray(), y = ys.ToArray();
        int nx = x.Length, ny = y.Length, nodeCount = checked(nx * ny), stageSize = checked(nodeCount * 2);
        int stateCount = checked(stageSize * (pins.Length + 1));
        if (stateCount > MaximumSearchStates)
            return Fail(HmiRouteStatus.CapacityExceeded, "The waypoint visibility graph exceeds the bounded search-state budget.");
        var pinNodes = pins.Select(p => Array.BinarySearch(y, p.Y) * nx + Array.BinarySearch(x, p.X)).ToArray();
        var remaining = new double[pins.Length + 1];
        for (int i = pins.Length - 1; i >= 0; i--)
            remaining[i] = remaining[i + 1] + Distance(pins[i], i + 1 < pins.Length ? pins[i + 1] : finish);
        double Estimate(int node, int stage)
        {
            var next = stage < pins.Length ? pins[stage] : finish;
            return Math.Abs((double)x[node % nx] - next.X) + Math.Abs((double)y[node / nx] - next.Y) + remaining[stage];
        }
        var edges = ArrayPool<byte>.Shared.Rent(nodeCount);
        var closed = ArrayPool<byte>.Shared.Rent(stateCount);
        var costs = ArrayPool<double>.Shared.Rent(stateCount);
        var parents = ArrayPool<int>.Shared.Rent(stateCount);
        try
        {
            edges.AsSpan(0, nodeCount).Clear(); closed.AsSpan(0, stateCount).Clear();
            costs.AsSpan(0, stateCount).Fill(double.PositiveInfinity); parents.AsSpan(0, stateCount).Fill(-1);
            foreach (var box in boxes)
            {
                int l = Array.BinarySearch(x, box.Bounds.Left), r = Array.BinarySearch(x, box.Bounds.Right);
                int t = Array.BinarySearch(y, box.Bounds.Top), b = Array.BinarySearch(y, box.Bounds.Bottom);
                // Boundary contact is allowed: clearance was already applied to physical bounds.
                for (int iy = t + 1; iy < b; iy++) for (int ix = l; ix < r; ix++) edges[iy * nx + ix] |= 1;
                for (int iy = t; iy < b; iy++) for (int ix = l + 1; ix < r; ix++) edges[iy * nx + ix] |= 2;
            }
            int origin = Array.BinarySearch(y, start.Y) * nx + Array.BinarySearch(x, start.X);
            int destination = Array.BinarySearch(y, finish.Y) * nx + Array.BinarySearch(x, finish.X);
            int axis = source.Direction is HmiPortDirection.Left or HmiPortDirection.Right ? 0 : 1;
            int lastAxis = target.Direction is HmiPortDirection.Left or HmiPortDirection.Right ? 0 : 1;
            int initialStage = pins.Length > 0 && pinNodes[0] == origin ? 1 : 0;
            int initial = initialStage * stageSize + origin * 2 + axis;
            costs[initial] = 0;
            var queue = new PriorityQueue<int, (double Score, int Tie)>();
            queue.Enqueue(initial, (Estimate(origin, initialStage), initial));
            int goal = -1; double best = double.PositiveInfinity;
            while (queue.TryDequeue(out int state, out var priority))
            {
                if (priority.Score >= best) break;
                if (closed[state] != 0) continue;
                closed[state] = 1;
                int stage = state / stageSize, node = (state % stageSize) / 2;
                int ix = node % nx, iy = node / nx, direction = state & 1;
                if (node == destination && stage == pins.Length)
                {
                    double total = costs[state] + (direction == lastAxis ? 0 : BendPenalty);
                    if (total < best) { best = total; goal = state; }
                    continue;
                }
                // At most four enqueues below; exhaustion is explicit, not a partial route.
                if (queue.Count > MaximumFrontierEntries - 4)
                    return Fail(HmiRouteStatus.CapacityExceeded, "The waypoint search frontier exceeded its bounded queue budget.");
                if (ix > 0 && (edges[node - 1] & 1) == 0) Visit(node - 1, 0, x[ix] - x[ix - 1]);
                if (ix + 1 < nx && (edges[node] & 1) == 0) Visit(node + 1, 0, x[ix + 1] - x[ix]);
                if (iy > 0 && (edges[node - nx] & 2) == 0) Visit(node - nx, 1, y[iy] - y[iy - 1]);
                if (iy + 1 < ny && (edges[node] & 2) == 0) Visit(node + nx, 1, y[iy + 1] - y[iy]);
                void Visit(int nextNode, int nextAxis, float length)
                {
                    int nextStage = stage < pins.Length && nextNode == pinNodes[stage] ? stage + 1 : stage;
                    int next = nextStage * stageSize + nextNode * 2 + nextAxis;
                    double cost = costs[state] + length + (direction == nextAxis ? 0 : BendPenalty);
                    if (closed[next] != 0 || cost >= costs[next]) return;
                    costs[next] = cost; parents[next] = state;
                    double estimate = Estimate(nextNode, nextStage);
                    queue.Enqueue(next, (cost + estimate, next));
                }
            }
            if (goal < 0) return Fail(HmiRouteStatus.NoRoute, "No obstacle-free orthogonal route exists for these nozzle escapes.");
            var path = new List<HmiPoint>();
            for (int state = goal; state >= 0; state = parents[state])
            {
                int node = (state % stageSize) / 2;
                path.Add(new(x[node % nx], y[node / nx]));
            }
            path.Reverse(); path.Insert(0, source.Point); path.Add(target.Point);
            var simplified = new List<HmiPoint>();
            foreach (var point in path)
            {
                if (simplified.Count > 0 && simplified[^1] == point) continue;
                while (simplified.Count > 1 && !pins.Contains(simplified[^1]) && Collinear(simplified[^2], simplified[^1], point)) simplified.RemoveAt(simplified.Count - 1);
                simplified.Add(point);
            }
            if (simplified.Count > MaximumRoutePoints)
                return Fail(HmiRouteStatus.CapacityExceeded, "The routed path exceeds the retained geometry budget.");
            return new(HmiRouteStatus.Success, simplified.ToArray(), pins.Length == 0 ? "Routed" : $"Routed via {pins.Length} waypoint(s)");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(edges); ArrayPool<byte>.Shared.Return(closed);
            ArrayPool<double>.Shared.Return(costs); ArrayPool<int>.Shared.Return(parents);
        }
    }

    public static bool Intersects(HmiPoint a, HmiPoint b, HmiRouteBox bounds) => a.X == b.X
        ? a.X > bounds.Left && a.X < bounds.Right && Math.Max(a.Y, b.Y) > bounds.Top && Math.Min(a.Y, b.Y) < bounds.Bottom
        : a.Y == b.Y
            ? a.Y > bounds.Top && a.Y < bounds.Bottom && Math.Max(a.X, b.X) > bounds.Left && Math.Min(a.X, b.X) < bounds.Right
            : throw new ArgumentException("Routing segments must be axis-aligned.");

    private static bool Valid(HmiRouteTerminal t) => !string.IsNullOrWhiteSpace(t.ElementId) && t.Bounds.IsValid &&
        Enum.IsDefined(t.Direction) && float.IsFinite(t.Point.X) && float.IsFinite(t.Point.Y) &&
        t.Point.X >= t.Bounds.Left && t.Point.X <= t.Bounds.Right && t.Point.Y >= t.Bounds.Top && t.Point.Y <= t.Bounds.Bottom;
    private static HmiPoint Exit(HmiRouteTerminal t, float clearance) => t.Direction switch
    {
        HmiPortDirection.Left => new(t.Bounds.Left - clearance, t.Point.Y),
        HmiPortDirection.Right => new(t.Bounds.Right + clearance, t.Point.Y),
        HmiPortDirection.Top => new(t.Point.X, t.Bounds.Top - clearance),
        _ => new(t.Point.X, t.Bounds.Bottom + clearance)
    };
    private static bool Collinear(HmiPoint a, HmiPoint b, HmiPoint c) =>
        (a.X == b.X && b.X == c.X && ((double)b.Y - a.Y) * ((double)c.Y - b.Y) >= 0) ||
        (a.Y == b.Y && b.Y == c.Y && ((double)b.X - a.X) * ((double)c.X - b.X) >= 0);
    private static double Distance(HmiPoint a, HmiPoint b) => Math.Abs((double)a.X - b.X) + Math.Abs((double)a.Y - b.Y);
    private static HmiRouteResult Fail(HmiRouteStatus status, string diagnostic) => new(status, [], diagnostic);
}
