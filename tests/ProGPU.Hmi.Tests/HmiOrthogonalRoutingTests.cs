using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiOrthogonalRoutingTests
{
    private static HmiRouteTerminal Terminal(string id, float x, float y, HmiPortDirection direction)
    {
        var bounds = new HmiRouteBox(x, y, x + 100, y + 100);
        HmiPoint point = direction switch
        {
            HmiPortDirection.Left => new(x, y + 50), HmiPortDirection.Right => new(x + 100, y + 50),
            HmiPortDirection.Top => new(x + 50, y), _ => new(x + 50, y + 100)
        };
        return new(id, point, direction, bounds);
    }
    public static IEnumerable<object[]> Directions() => from a in Enum.GetValues<HmiPortDirection>() from b in Enum.GetValues<HmiPortDirection>() select new object[] { a, b };

    [Theory] [MemberData(nameof(Directions))]
    public void EveryPortDirectionPairEscapesInTheCorrectDirectionAndAvoidsObstacles(HmiPortDirection a, HmiPortDirection b)
    {
        var from = Terminal("a", 0, 0, a); var to = Terminal("b", 500, 270, b);
        HmiRouteObstacle[] obstacles = [new("a", from.Bounds), new("b", to.Bounds), new("obstacle", new(230, 0, 330, 400))];
        var route = HmiOrthogonalRouter.Route(from, to, obstacles, 12);
        Assert.Equal(HmiRouteStatus.Success, route.Status);
        Assert.Equal(from.Point, route.Points[0]); Assert.Equal(to.Point, route.Points[^1]);
        AssertDirection(from.Point, route.Points[1], a);
        AssertDirection(to.Point, route.Points[^2], b);
        AssertClear(route, obstacles, from.ElementId, to.ElementId, 12);
        var repeated = HmiOrthogonalRouter.Route(from, to, obstacles.Reverse().ToArray(), 12);
        Assert.Equal(route.Points, repeated.Points);
    }

    [Fact]
    public void UnobstructedFacingNozzlesProduceOneStraightSegment()
    {
        var a = Terminal("a", 0, 0, HmiPortDirection.Right); var b = Terminal("b", 400, 0, HmiPortDirection.Left);
        var route = HmiOrthogonalRouter.Route(a, b, [new("a", a.Bounds), new("b", b.Bounds)]);
        Assert.Equal(new[] { a.Point, b.Point }, route.Points); Assert.Equal(300, route.Length);
    }

    [Fact]
    public void OverlappingEquipmentProducesAnExplicitFailureNotACrossingLine()
    {
        var a = Terminal("a", 0, 0, HmiPortDirection.Right); var b = Terminal("b", 80, 0, HmiPortDirection.Left);
        var route = HmiOrthogonalRouter.Route(a, b, [new("a", a.Bounds), new("b", b.Bounds)]);
        Assert.Equal(HmiRouteStatus.BlockedTerminal, route.Status); Assert.Empty(route.Points);
    }

    [Fact]
    public void FullAdmittedObstacleCapacityIsSolvedWithoutOmittingAnyObstacle()
    {
        var a = Terminal("a", -300, 0, HmiPortDirection.Right); var b = Terminal("b", 3000, 0, HmiPortDirection.Left);
        var obstacles = new List<HmiRouteObstacle> { new("a", a.Bounds), new("b", b.Bounds) };
        for (int i = 0; i < HmiOrthogonalRouter.MaximumObstacles - 2; i++)
            obstacles.Add(new("box-" + i, new(i % 18 * 130, i / 18 * 130, i % 18 * 130 + 50, i / 18 * 130 + 50)));
        var route = HmiOrthogonalRouter.Route(a, b, obstacles);
        Assert.Equal(HmiRouteStatus.Success, route.Status); AssertClear(route, obstacles, "a", "b", 12);
        obstacles.Add(new("extra", new(4000, 4000, 4010, 4010)));
        route = HmiOrthogonalRouter.Route(a, b, obstacles);
        Assert.Equal(HmiRouteStatus.CapacityExceeded, route.Status); Assert.Empty(route.Points);
    }

    [Fact]
    public void SameEquipmentCanConnectTwoDistinctPortsAroundItsOwnObstacle()
    {
        var a = Terminal("a", 0, 0, HmiPortDirection.Left);
        var b = Terminal("a", 0, 0, HmiPortDirection.Right);
        var route = HmiOrthogonalRouter.Route(a, b, [new("a", a.Bounds)]);
        Assert.Equal(HmiRouteStatus.Success, route.Status); AssertDirection(a.Point, route.Points[1], a.Direction);
        AssertDirection(b.Point, route.Points[^2], b.Direction); AssertClear(route, [new("a", a.Bounds)], "a", "a", 12);
    }

    [Theory] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(-1)] [InlineData(257)]
    public void InvalidClearanceIsRejected(float value)
    {
        var a = Terminal("a", 0, 0, HmiPortDirection.Left);
        Assert.Throws<ArgumentException>(() => HmiOrthogonalRouter.Route(a, a, [new("a", a.Bounds)], value));
    }

    [Fact]
    public void CollinearTerminalBacktrackingDoesNotEraseTheRequiredNozzleEscape()
    {
        var a = Terminal("a", 0, 0, HmiPortDirection.Right) with { Point = new(50, 50) };
        var b = a with { Point = new(75, 50) };
        var route = HmiOrthogonalRouter.Route(a, b, [new("a", a.Bounds)]);
        Assert.Equal(HmiRouteStatus.Success, route.Status);
        Assert.Equal(new[] { a.Point, new HmiPoint(112, 50), b.Point }, route.Points);
        Assert.Throws<ArgumentException>(() => HmiOrthogonalRouter.Route(a, a, [new("a", a.Bounds)]));
    }

    [Fact]
    public void TerminalOwnershipAndGeometryAreVerified()
    {
        var a = Terminal("a", 0, 0, HmiPortDirection.Right); var b = Terminal("b", 400, 0, HmiPortDirection.Left);
        Assert.Throws<ArgumentException>(() => HmiOrthogonalRouter.Route(a, b, [new("a", a.Bounds)]));
        Assert.Throws<ArgumentException>(() => HmiOrthogonalRouter.Route(a with { Point = new(float.NaN, 0) }, b, []));
        Assert.Throws<ArgumentException>(() => HmiOrthogonalRouter.Route(a, b, [new("a", a.Bounds), new("a", b.Bounds)]));
    }

    private static void AssertDirection(HmiPoint anchor, HmiPoint escape, HmiPortDirection direction)
    {
        switch (direction)
        {
            case HmiPortDirection.Left: Assert.Equal(anchor.Y, escape.Y); Assert.True(escape.X < anchor.X); break;
            case HmiPortDirection.Right: Assert.Equal(anchor.Y, escape.Y); Assert.True(escape.X > anchor.X); break;
            case HmiPortDirection.Top: Assert.Equal(anchor.X, escape.X); Assert.True(escape.Y < anchor.Y); break;
            case HmiPortDirection.Bottom: Assert.Equal(anchor.X, escape.X); Assert.True(escape.Y > anchor.Y); break;
        }
    }
    private static void AssertClear(HmiRouteResult route, IReadOnlyList<HmiRouteObstacle> boxes, string from, string to, float clearance)
    {
        Assert.InRange(route.Points.Count, 2, HmiOrthogonalRouter.MaximumRoutePoints);
        for (int i = 1; i < route.Points.Count; i++)
        {
            var a = route.Points[i - 1]; var b = route.Points[i];
            Assert.True(a.X == b.X || a.Y == b.Y);
            Assert.NotEqual(a, b);
            foreach (var box in boxes)
            {
                if (i == 1 && box.ElementId == from || i == route.Points.Count - 1 && box.ElementId == to) continue;
                Assert.False(HmiOrthogonalRouter.Intersects(a, b, box.Bounds.Inflate(clearance)), $"Segment {a}..{b} intersects {box.ElementId}");
            }
        }
    }
}
