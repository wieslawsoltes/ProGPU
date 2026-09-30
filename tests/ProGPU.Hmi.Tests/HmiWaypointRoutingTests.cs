using System.Text.Json;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiWaypointRoutingTests
{
    private static readonly HmiRouteTerminal Source = new("a", new(100, 50), HmiPortDirection.Right, new(0, 0, 100, 100));
    private static readonly HmiRouteTerminal Target = new("b", new(500, 50), HmiPortDirection.Left, new(500, 0, 600, 100));
    private static HmiRouteObstacle[] Obstacles => [new("a", Source.Bounds), new("b", Target.Bounds), new("wall", new(260, -30, 340, 130))];

    [Fact]
    public void OrderedPinsAreExactConstraintsAndDoNotCutClearanceEnvelopes()
    {
        HmiPoint[] pins = [new(200, -80), new(400, -80), new(400, 180)];
        var result = HmiOrthogonalRouter.Route(Source, Target, Obstacles, pins);
        Assert.Equal(HmiRouteStatus.Success, result.Status);
        int next = 0;
        foreach (var point in result.Points) if (next < pins.Length && point == pins[next]) next++;
        Assert.Equal(pins.Length, next);
        CheckSegments(result, Obstacles, 12);
        var reversedInput = HmiOrthogonalRouter.Route(Source, Target, Obstacles.Reverse().ToArray(), pins);
        Assert.Equal(result.Points, reversedInput.Points);
    }

    [Theory]
    [InlineData(300, 0)] [InlineData(100, 50)] [InlineData(510, 50)]
    public void PinInAnInflatedObstacleReturnsNoPartialSuccess(float x, float y)
    {
        var result = HmiOrthogonalRouter.Route(Source, Target, Obstacles, [new(x, y)]);
        Assert.Equal(HmiRouteStatus.BlockedWaypoint, result.Status);
        Assert.Empty(result.Points); Assert.Contains("Waypoint 1", result.Diagnostic);
    }

    [Fact]
    public void CollinearPinsAndEscapePinsAreNotErasedBySimplification()
    {
        var obstacles = Obstacles.Take(2).ToArray();
        HmiPoint[] pins = [new(112, 50), new(200, 50), new(488, 50)];
        var result = HmiOrthogonalRouter.Route(Source, Target, obstacles, pins);
        Assert.Equal(HmiRouteStatus.Success, result.Status);
        Assert.Equal(new[] { Source.Point, pins[0], pins[1], pins[2], Target.Point }, result.Points);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void PinOrderingPreservesTerminalDirectionsAcrossRotatedPairs(int direction)
    {
        HmiRouteTerminal T(string id, float x, HmiPortDirection d) => new(id, d switch
        { HmiPortDirection.Left => new(x, 50), HmiPortDirection.Top => new(x + 50, 0), HmiPortDirection.Right => new(x + 100, 50), _ => new(x + 50, 100) }, d, new(x, 0, x + 100, 100));
        for (int other = 0; other < 4; other++)
        {
            var a = T("a", 0, (HmiPortDirection)direction); var b = T("b", 500, (HmiPortDirection)other);
            HmiRouteObstacle[] obstacles = [new("a", a.Bounds), new("b", b.Bounds)];
            var result = HmiOrthogonalRouter.Route(a, b, obstacles, [new(220, -70), new(380, 170)]);
            Assert.Equal(HmiRouteStatus.Success, result.Status);
            Assert.Equal(a.Point, result.Points[0]); Assert.Equal(b.Point, result.Points[^1]);
            AssertEscape(a, result.Points[1]); AssertEscape(b, result.Points[^2]);
        }
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(65537)]
    public void InvalidPinCoordinatesAreRejectedBeforeRouting(float value)
        => Assert.Throws<InvalidDataException>(() => HmiOrthogonalRouter.Route(Source, Target, Obstacles, [new(value, 0)]));

    [Fact]
    public void PinAndObstacleBudgetsAndDuplicateConstraintsAreExplicit()
    {
        Assert.Throws<InvalidDataException>(() => HmiOrthogonalRouter.Route(Source, Target, Obstacles, Enumerable.Range(0, 17).Select(i => new HmiPoint(i, -80)).ToArray()));
        Assert.Throws<InvalidDataException>(() => HmiOrthogonalRouter.Route(Source, Target, Obstacles, [new(200, -80), new(200, -80)]));
        Assert.Throws<InvalidDataException>(() => HmiRouteWaypoints.Validate(null));
        var result = HmiOrthogonalRouter.Route(Source, Target, Obstacles, Enumerable.Range(0, 16).Select(i => new HmiPoint(150 + i * 20, -80)).ToArray());
        Assert.Equal(HmiRouteStatus.Success, result.Status);
    }

    [Fact]
    public void PersistenceAndCopiesOwnPinsWithReflectionSerializationDisabled()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var project = HmiDiagramModelTests.Project();
        project.Screens[0].Links[0].Waypoints = [new(300, 100), new(400, 100)];
        string json = HmiProjectSerializer.Serialize(project);
        var clone = HmiProjectSerializer.Deserialize(json);
        Assert.Equal(json, HmiProjectSerializer.Serialize(clone));
        clone.Screens[0].Links[0].Waypoints[0] = new(0, 0);
        Assert.Equal(new HmiPoint(300, 100), project.Screens[0].Links[0].Waypoints[0]);
        var copied = project.Screens[0].Links[0].Copy(); copied.Waypoints.Clear();
        Assert.Equal(2, project.Screens[0].Links[0].Waypoints.Count);
    }

    [Fact]
    public void LegacyDocumentsGetIndependentEmptyPinsAndNullIsRejected()
    {
        string json = HmiProjectSerializer.Serialize(HmiDiagramModelTests.Project());
        var clone = HmiProjectSerializer.Deserialize(json.Replace(",\n          \"waypoints\": []", ""));
        Assert.Empty(clone.Screens[0].Links[0].Waypoints);
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Deserialize(json.Replace("\"waypoints\": []", "\"waypoints\": null")));
    }

    [Fact]
    public void TranslationIsDetachedAndRejectsOverflow()
    {
        List<HmiPoint> original = [new(40, 70), new(80, 70)];
        Assert.Equal(new[] { new HmiPoint(60, 90), new HmiPoint(100, 90) }, HmiRouteWaypoints.Translate(original, 20, 20));
        Assert.Equal(new HmiPoint(40, 70), original[0]);
        Assert.Throws<InvalidDataException>(() => HmiRouteWaypoints.Translate(original, 65536, 0));
    }

    [Fact]
    public void InsertionOrderUsesSourceToTargetDistanceAndPreservesExistingPins()
    {
        HmiPoint[] pins = [new(200, -80), new(400, -80)];
        var route = HmiOrthogonalRouter.Route(Source, Target, Obstacles, pins);
        Assert.Equal(0, HmiRouteWaypoints.FindInsertionIndex(route, pins, new(150, 50)));
        Assert.Equal(1, HmiRouteWaypoints.FindInsertionIndex(route, pins, new(300, -90)));
        Assert.Equal(2, HmiRouteWaypoints.FindInsertionIndex(route, pins, new(475, 50)));
    }

    [Fact]
    public void WholeRouteBendCostMatchesIndependentUnitGridDijkstra()
    {
        var a = new HmiRouteTerminal("a", new(4, 2), HmiPortDirection.Right, new(0, 0, 4, 4));
        var b = new HmiRouteTerminal("b", new(20, 12), HmiPortDirection.Left, new(20, 10, 24, 14));
        HmiRouteObstacle[] obstacles = [new("a", a.Bounds), new("b", b.Bounds), new("wall", new(11, 0, 13, 14))];
        HmiPoint[][] choices = [[new(8, -4)], [new(8, 20), new(16, -4)], [new(16, -4), new(8, 20)]];
        foreach (var pins in choices)
        {
            var route = HmiOrthogonalRouter.Route(a, b, obstacles, pins, 2);
            Assert.Equal(HmiRouteStatus.Success, route.Status);
            double actual = route.Length;
            for (int i = 2; i < route.Points.Count; i++)
                if ((route.Points[i - 2].X == route.Points[i - 1].X) != (route.Points[i - 1].X == route.Points[i].X)) actual += 12;
            Assert.Equal(UnitGridCost(pins, obstacles) + 4, actual); // The two protected nozzle leads are each two units.
        }
    }

    private static double UnitGridCost(HmiPoint[] pins, HmiRouteObstacle[] obstacles)
    {
        var start = (X: 6, Y: 2, Axis: 0, Stage: 0);
        var queue = new PriorityQueue<(int X, int Y, int Axis, int Stage), double>();
        var costs = new Dictionary<(int, int, int, int), double> { [start] = 0 };
        queue.Enqueue(start, 0);
        double best = double.PositiveInfinity;
        while (queue.TryDequeue(out var state, out double cost))
        {
            if (cost >= best || costs[state] != cost) continue;
            if (state.X == 18 && state.Y == 12 && state.Stage == pins.Length)
            { best = Math.Min(best, cost + (state.Axis == 0 ? 0 : 12)); continue; }
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int x = state.X + dx, y = state.Y + dy, axis = dx == 0 ? 1 : 0;
                if (x < -10 || x > 34 || y < -12 || y > 30) continue;
                // Independent strict-interior segment test on integer-grid half-step centers.
                double mx = state.X + dx * .5, my = state.Y + dy * .5;
                if (obstacles.Any(o => mx > o.Bounds.Left - 2 && mx < o.Bounds.Right + 2 && my > o.Bounds.Top - 2 && my < o.Bounds.Bottom + 2)) continue;
                int stage = state.Stage < pins.Length && pins[state.Stage] == new HmiPoint(x, y) ? state.Stage + 1 : state.Stage;
                var next = (x, y, axis, stage); double nextCost = cost + 1 + (axis == state.Axis ? 0 : 12);
                if (costs.TryGetValue(next, out var previous) && previous <= nextCost) continue;
                costs[next] = nextCost; queue.Enqueue(next, nextCost);
            }
        }
        return best;
    }
    private static void AssertEscape(HmiRouteTerminal t, HmiPoint p)
    {
        Assert.True(t.Direction switch
        { HmiPortDirection.Left => p.X < t.Point.X && p.Y == t.Point.Y,
          HmiPortDirection.Right => p.X > t.Point.X && p.Y == t.Point.Y,
          HmiPortDirection.Top => p.Y < t.Point.Y && p.X == t.Point.X,
          _ => p.Y > t.Point.Y && p.X == t.Point.X });
    }
    private static void CheckSegments(HmiRouteResult route, HmiRouteObstacle[] obstacles, float padding)
    {
        for (int i = 1; i < route.Points.Count; i++)
        {
            var a = route.Points[i - 1]; var b = route.Points[i];
            Assert.NotEqual(a, b); Assert.True(a.X == b.X || a.Y == b.Y);
            foreach (var o in obstacles)
            {
                if (i == 1 && o.ElementId == "a" || i == route.Points.Count - 1 && o.ElementId == "b") continue;
                Assert.False(HmiOrthogonalRouter.Intersects(a, b, o.Bounds.Inflate(padding)));
            }
        }
    }
}
