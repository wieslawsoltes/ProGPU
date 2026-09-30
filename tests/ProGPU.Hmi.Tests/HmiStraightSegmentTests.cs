using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiStraightSegmentTests
{
    private static readonly HmiRouteTerminal Source = new("a", new(100, 50), HmiPortDirection.Right, new(0, 0, 100, 100));
    private static readonly HmiRouteTerminal Target = new("b", new(500, 50), HmiPortDirection.Left, new(500, 0, 600, 100));
    private static HmiRouteObstacle[] Obstacles => [new("a", Source.Bounds), new("b", Target.Bounds)];

    [Fact]
    public void StraightConstraintIsAnActualRouteEdgeNotJustTwoPoints()
    {
        HmiPoint[] pins = [new(180, -80), new(420, -80)];
        var route = HmiOrthogonalRouter.Route(Source, Target, Obstacles, pins, [0]);
        Assert.Equal(HmiRouteStatus.Success, route.Status);
        int start = route.Points.ToList().IndexOf(pins[0]);
        Assert.Equal(pins[1], route.Points[start + 1]);
        Assert.Equal(Source.Point, route.Points[0]); Assert.Equal(Target.Point, route.Points[^1]);
    }

    [Fact]
    public void ObstacleCrossingCannotTurnIntoAnUnrequestedDetour()
    {
        HmiPoint[] pins = [new(180, 50), new(420, 50)];
        HmiRouteObstacle[] boxes = [.. Obstacles, new("wall", new(270, 20, 330, 80))];
        Assert.Equal(HmiRouteStatus.Success, HmiOrthogonalRouter.Route(Source, Target, boxes, pins).Status);
        var constrained = HmiOrthogonalRouter.Route(Source, Target, boxes, pins, [0]);
        Assert.Equal(HmiRouteStatus.BlockedSegment, constrained.Status); Assert.Empty(constrained.Points);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void SegmentConstraintsPreserveEveryTerminalDirection(int sourceDirection)
    {
        HmiRouteTerminal Terminal(string id, float x, HmiPortDirection d) => new(id, d switch
        {
            HmiPortDirection.Left => new(x, 50), HmiPortDirection.Top => new(x + 50, 0),
            HmiPortDirection.Right => new(x + 100, 50), _ => new(x + 50, 100)
        }, d, new(x, 0, x + 100, 100));
        for (int t = 0; t < 4; t++)
        {
            var source = Terminal("a", 0, (HmiPortDirection)sourceDirection); var target = Terminal("b", 500, (HmiPortDirection)t);
            HmiPoint[] pins = [new(200, -80), new(400, -80)];
            var route = HmiOrthogonalRouter.Route(source, target, [new("a", source.Bounds), new("b", target.Bounds)], pins, [0]);
            Assert.Equal(HmiRouteStatus.Success, route.Status);
            Assert.Equal(source.Point, route.Points[0]); Assert.Equal(target.Point, route.Points[^1]);
            var a = route.Points[1]; var b = route.Points[^2];
            Assert.True(Depart(source, a)); Assert.True(Depart(target, b));
            int pin = route.Points.ToList().IndexOf(pins[0]); Assert.Equal(pins[1], route.Points[pin + 1]);
        }
    }

    [Fact]
    public void DirectRouteGripExcludesBothFixedNozzleLeads()
    {
        var route = HmiOrthogonalRouter.Route(Source, Target, Obstacles);
        var segment = Assert.Single(HmiRouteSegments.GetEditableSegments(route, Source, Target, 12));
        Assert.Equal(new HmiPoint(112, 50), segment.Start); Assert.Equal(new HmiPoint(488, 50), segment.End);
        var plan = new HmiRouteSegmentEdit(route, segment, [], []);
        var moved = plan.Preview(-80);
        Assert.Equal(new[] { new HmiPoint(112, -30), new HmiPoint(488, -30) }, moved.Waypoints);
        Assert.Equal(new[] { 0 }, moved.StraightSegments);
        var rerouted = HmiOrthogonalRouter.Route(Source, Target, Obstacles, moved.Waypoints, moved.StraightSegments);
        Assert.Equal(HmiRouteStatus.Success, rerouted.Status);
        Assert.Equal(Source.Point, rerouted.Points[0]); Assert.Equal(Target.Point, rerouted.Points[^1]);
    }

    [Fact]
    public void PlanAndPreviewOwnSnapshotsAndDoNotAccumulateMotion()
    {
        var route = HmiOrthogonalRouter.Route(Source, Target, Obstacles, [new(200, -80), new(400, -80)]);
        var segment = HmiRouteSegments.GetEditableSegments(route, Source, Target, 12).Single(s => s.Start == new HmiPoint(200, -80) && s.End == new HmiPoint(400, -80));
        var pins = new List<HmiPoint> { segment.Start, segment.End }; var spans = new List<int>();
        var plan = new HmiRouteSegmentEdit(route, segment, pins, spans);
        pins.Clear(); spans.Add(9);
        Assert.Equal(new[] { new HmiPoint(200, -50), new HmiPoint(400, -50) }, plan.Preview(30).Waypoints);
        Assert.Equal(new[] { new HmiPoint(200, -70), new HmiPoint(400, -70) }, plan.Preview(10).Waypoints);
        Assert.Throws<NotSupportedException>(() => ((IList<HmiPoint>)plan.Preview(10).Waypoints).Clear());
    }

    [Theory]
    [InlineData(-1)] [InlineData(1)] [InlineData(20)]
    public void InvalidSpanIndexesAreRejected(int index) => Assert.Throws<InvalidDataException>(() =>
        HmiRouteSegments.Validate([new(1, 0), new(2, 0)], [index]));

    [Fact]
    public void MalformedSpansAreRejectedBeforePublication()
    {
        Assert.Throws<InvalidDataException>(() => HmiRouteSegments.Validate([new(1, 0), new(2, 0)], [0, 0]));
        Assert.Throws<InvalidDataException>(() => HmiRouteSegments.Validate([new(1, 0), new(2, 1)], [0]));
        Assert.Throws<InvalidDataException>(() => HmiRouteSegments.Validate([], null));
        Assert.Throws<InvalidDataException>(() => HmiRouteSegments.Validate([new(1, 0), new(2, 0), new(3, 0)], [1, 0]));
        var link = new HmiDiagramLink { Waypoints = [new(0, 0), new(10, 0)], StraightSegments = [0] };
        Assert.Throws<InvalidDataException>(() => HmiRouteSegments.InsertWaypoint(link, 1, new(5, 5)));
        Assert.Equal(2, link.Waypoints.Count); Assert.Equal(new[] { 0 }, link.StraightSegments);
    }

    [Fact]
    public void InsertionSplitsTheSpanAndRemovalReleasesOnlyIncidentSpans()
    {
        var link = new HmiDiagramLink { Waypoints = [new(0, 0), new(10, 0), new(20, 0)], StraightSegments = [0, 1] };
        HmiRouteSegments.InsertWaypoint(link, 1, new(5, 0));
        Assert.Equal(new[] { 0, 1, 2 }, link.StraightSegments);
        HmiRouteSegments.RemoveWaypoint(link, 1);
        Assert.Equal(new[] { 1 }, link.StraightSegments);
        Assert.Equal(new[] { new HmiPoint(0, 0), new HmiPoint(10, 0), new HmiPoint(20, 0) }, link.Waypoints);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void LegacyJsonCopyAndReflectionDisabledRoundTripAreSafe(string newLine)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var project = HmiDiagramModelTests.Project(); var link = project.Screens[0].Links[0];
        var legacy = JsonNode.Parse(WithLineEndings(HmiProjectSerializer.Serialize(project)))!;
        var legacyLink = legacy["screens"]![0]!["links"]![0]!.AsObject();
        Assert.True(legacyLink.Remove("straightSegments"));
        Assert.False(legacyLink.ContainsKey("straightSegments"));
        Assert.Empty(HmiProjectSerializer.Deserialize(legacy.ToJsonString()).Screens[0].Links[0].StraightSegments);
        link.Waypoints = [new(200, -60), new(400, -60)]; link.StraightSegments = [0];
        var json = HmiProjectSerializer.Serialize(project);
        Assert.Equal(json, HmiProjectSerializer.Serialize(HmiProjectSerializer.Deserialize(WithLineEndings(json))));
        var copy = link.Copy(); copy.StraightSegments.Clear(); Assert.Single(link.StraightSegments);
        // Mutate the actual JSON member, not platform-dependent indentation/newlines.
        var invalid = JsonNode.Parse(WithLineEndings(json))!;
        var invalidLink = invalid["screens"]![0]!["links"]![0]!.AsObject();
        invalidLink["straightSegments"] = null;
        Assert.True(invalidLink.ContainsKey("straightSegments"));
        Assert.Null(invalidLink["straightSegments"]);
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Deserialize(invalid.ToJsonString()));

        string WithLineEndings(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", newLine, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryPlanKeepsNeighborConstraintsAndRejectsDiagonalSharedPins()
    {
        var plan = new HmiRouteSegmentEdit([new(200, 50), new(400, 50)], [0], 0);
        Assert.Equal(new[] { new HmiPoint(200, -50), new HmiPoint(400, -50) }, plan.Preview(-100).Waypoints);
        var chain = new HmiRouteSegmentEdit([new(200, 50), new(300, 50), new(400, 50)], [0, 1], 0);
        Assert.Throws<InvalidDataException>(() => chain.Preview(-100));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.Preview(float.NaN));
        Assert.Throws<InvalidDataException>(() => plan.Preview(100000));
    }

    [Fact]
    public void InsertingEditPinsCannotExceedTheExistingWaypointBudget()
    {
        var pins = Enumerable.Range(0, 16).Select(i => new HmiPoint(150 + i * 20, -80)).ToArray();
        var route = HmiOrthogonalRouter.Route(Source, Target, Obstacles, pins);
        var segment = HmiRouteSegments.GetEditableSegments(route, Source, Target, 12).First();
        Assert.Throws<InvalidDataException>(() => new HmiRouteSegmentEdit(route, segment, pins, []));
    }

    private static bool Depart(HmiRouteTerminal t, HmiPoint p) => t.Direction switch
    {
        HmiPortDirection.Left => p.Y == t.Point.Y && p.X < t.Point.X,
        HmiPortDirection.Right => p.Y == t.Point.Y && p.X > t.Point.X,
        HmiPortDirection.Top => p.X == t.Point.X && p.Y < t.Point.Y,
        _ => p.X == t.Point.X && p.Y > t.Point.Y
    };
}
