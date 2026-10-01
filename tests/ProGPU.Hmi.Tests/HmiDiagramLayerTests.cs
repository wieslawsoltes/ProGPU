using ProGPU.WinUI.Hmi;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiDiagramLayerTests
{
    public static IEnumerable<object[]> Orientations() => from turn in Enumerable.Range(0, 4) from x in new[] { false, true } from y in new[] { false, true } select new object[] { turn, x, y };

    [Theory] [MemberData(nameof(Orientations))]
    public void NozzleAnchorsUseExactMirroredAndRotatedGlyphCoordinates(int turns, bool mirrorX, bool mirrorY)
    {
        var element = new HmiElement { Id = "tank", Symbol = HmiSymbol.Tank, X = 100, Y = 200, Width = 200, Height = 300,
            Appearance = new() { QuarterTurns = turns, MirrorHorizontal = mirrorX, MirrorVertical = mirrorY } };
        var terminal = HmiPortLayout.Resolve(element, "outlet");
        var bounds = HmiPortLayout.GetBounds(element);
        float x = mirrorX ? 10 : 90, y = mirrorY ? 24 : 76;
        (x, y) = turns switch { 1 => (100 - y, x), 2 => (100 - x, 100 - y), 3 => (y, 100 - x), _ => (x, y) };
        Assert.InRange(Math.Abs(terminal.Point.X - (bounds.Left + x / 100 * (bounds.Right - bounds.Left))), 0, .0001f);
        Assert.InRange(Math.Abs(terminal.Point.Y - (bounds.Top + y / 100 * (bounds.Bottom - bounds.Top))), 0, .0001f);
        var expectedDirection = (HmiPortDirection)(((mirrorX ? 0 : 2) + turns) & 3);
        Assert.Equal(expectedDirection, terminal.Direction);
        var layer = new HmiLinkLayer(); layer.SetScreen(new HmiScreen { Elements = [element] });
        var hit = layer.HitPort(terminal.Point, 2);
        Assert.NotNull(hit); Assert.Equal("tank", hit.ElementId); Assert.Equal("outlet", hit.PortId);
    }

    [Theory]
    [InlineData(HmiSymbol.FlowMeter, "outlet")]
    [InlineData(HmiSymbol.PressureTransmitter, "process")]
    [InlineData(HmiSymbol.LevelTransmitter, "process")]
    [InlineData(HmiSymbol.Tank, "outlet")]
    public void CardPortsProjectToTheBorderRatherThanRoutingBelowOpaqueCaptionAndReadoutInk(HmiSymbol symbol, string port)
    {
        var screen = HmiDiagramModelTests.Project().Screens[0];
        screen.Elements[0].Symbol = symbol; screen.Elements[0].Appearance.Presentation = HmiPresentation.Card;
        screen.Links[0].Source.PortId = port;
        var terminal = HmiPortLayout.Resolve(screen.Elements[0], port);
        var b = terminal.Bounds;
        Assert.True(terminal.Point.X == b.Left || terminal.Point.X == b.Right || terminal.Point.Y == b.Top || terminal.Point.Y == b.Bottom);
        var layer = new HmiLinkLayer(); layer.SetScreen(screen);
        Assert.Equal(HmiRouteStatus.Success, layer.Routes["ab"].Status);
        Assert.Equal(terminal.Point, layer.Routes["ab"].Points[0]);
    }

    [Fact]
    public void EqualDistanceNozzleHitsPreferTheForegroundEquipmentAndRejectNonfiniteInput()
    {
        var screen = HmiDiagramModelTests.Project().Screens[0]; screen.Links.Clear();
        var foreground = screen.Elements[0].Copy(true); screen.Elements.Add(foreground);
        var layer = new HmiLinkLayer(); layer.SetScreen(screen);
        var anchor = HmiPortLayout.Resolve(foreground, "outlet").Point;
        Assert.Equal(foreground.Id, layer.HitPort(anchor, 3)!.ElementId);
        Assert.Throws<ArgumentException>(() => layer.HitPort(new(float.NaN, 0), 4));
        Assert.Throws<ArgumentException>(() => layer.HitLink(new(0, float.PositiveInfinity), 4));
    }

    [Fact]
    public void TelemetryQualityAndPaletteChangesReuseGeometryAndNeverWriteData()
    {
        var project = HmiDiagramModelTests.Project(); var screen = project.Screens[0];
        HmiTagSample sample = new(HmiValue.From(true), HmiQuality.Good, DateTimeOffset.UnixEpoch);
        var layer = new HmiLinkLayer { ReadSample = _ => sample }; layer.SetScreen(screen);
        long count = layer.RoutingPasses; var route = layer.Routes["ab"];
        for (int i = 0; i < 100; i++)
        {
            sample = new(HmiValue.From(i % 2 == 0), i % 3 == 0 ? HmiQuality.Bad : HmiQuality.Good, DateTimeOffset.UnixEpoch.AddSeconds(i));
            layer.RefreshTags(["Running"]); layer.ColorScheme = (HmiColorScheme)(i % 3);
            Assert.Equal(sample.Quality, layer.GetLinkQuality("ab"));
            Assert.Same(route, layer.Routes["ab"]); Assert.Equal(count, layer.RoutingPasses);
        }
        screen.Links[0].Name = "Rename only"; layer.SetScreen(screen);
        Assert.Same(route, layer.Routes["ab"]); Assert.Equal(count, layer.RoutingPasses);
        Assert.True(project.Tags[0].InitialValue.Boolean);
    }

    [Fact]
    public void NewObstacleInvalidatesAnIntersectingRouteButUnrelatedEditsDoNot()
    {
        var screen = HmiDiagramModelTests.Project().Screens[0]; var layer = new HmiLinkLayer(); layer.SetScreen(screen);
        var route = layer.Routes["ab"];
        screen.Elements.Add(new() { Id = "remote", Symbol = HmiSymbol.Filter, X = 800, Y = 650, Width = 100, Height = 100 });
        layer.SetScreen(screen); Assert.Same(route, layer.Routes["ab"]);
        int segment = Enumerable.Range(1, route.Points.Count - 1).MaxBy(i => Math.Abs(route.Points[i].X - route.Points[i - 1].X) + Math.Abs(route.Points[i].Y - route.Points[i - 1].Y));
        var a = route.Points[segment - 1]; var b = route.Points[segment];
        var blocker = new HmiElement { Id = "blocker", Symbol = HmiSymbol.Rectangle, X = (a.X + b.X) / 2 - 15, Y = (a.Y + b.Y) / 2 - 15, Width = 30, Height = 30 };
        // A drawing rectangle is not process equipment and must not become an obstacle.
        screen.Elements.Add(blocker); layer.SetScreen(screen); Assert.Same(route, layer.Routes["ab"]);
        blocker.Symbol = HmiSymbol.Filter; layer.SetScreen(screen);
        Assert.NotSame(route, layer.Routes["ab"]); Assert.Equal(HmiRouteStatus.Success, layer.Routes["ab"].Status);
        var bounds = HmiPortLayout.GetBounds(blocker).Inflate(screen.Links[0].Clearance + screen.Links[0].Thickness / 2 + 5);
        var next = layer.Routes["ab"];
        for (int i = 1; i < next.Points.Count; i++) Assert.False(HmiOrthogonalRouter.Intersects(next.Points[i - 1], next.Points[i], bounds));
    }

    [Fact]
    public void RouteEndpointsFollowGeometryButNotCallerMutationOfTheOwnedSnapshot()
    {
        var screen = HmiDiagramModelTests.Project().Screens[0]; var layer = new HmiLinkLayer(); layer.SetScreen(screen);
        var route = layer.Routes["ab"]; var anchor = route.Points[0];
        screen.Elements[0].X += 20;
        Assert.Equal(anchor, layer.Routes["ab"].Points[0]);
        layer.SetScreen(screen); Assert.NotSame(route, layer.Routes["ab"]);
        Assert.Equal(anchor.X + 20, layer.Routes["ab"].Points[0].X);
    }

    [Fact]
    public void TinyGlyphsRemainEditableAndShowFailureInsteadOfCrashingTheScreen()
    {
        var screen = HmiDiagramModelTests.Project().Screens[0]; screen.Elements[0].Width = 8; screen.Elements[0].Height = 8;
        var layer = new HmiLinkLayer(); layer.SetScreen(screen);
        Assert.Equal(HmiRouteStatus.BlockedTerminal, layer.Routes["ab"].Status); Assert.Empty(layer.Routes["ab"].Points);
        Assert.Null(layer.HitPort(new(64, 204), 3));
        screen.Elements[0].Width = 180; screen.Elements[0].Height = 190; layer.SetScreen(screen);
        Assert.Equal(HmiRouteStatus.Success, layer.Routes["ab"].Status);
    }

    [Fact]
    public void MaximumStyleClearanceAndThicknessStayInsideTheRouterAdmissionBudget()
    {
        var screen = HmiDiagramModelTests.Project().Screens[0];
        // Leave space for both inflated equipment envelopes, including selection and arrow ink.
        screen.Elements[1].X = 800; screen.Width = 1400;
        screen.Links[0].Clearance = 128; screen.Links[0].Thickness = 12;
        var layer = new HmiLinkLayer(); layer.SetScreen(screen);
        Assert.Equal(HmiRouteStatus.Success, layer.Routes["ab"].Status);
    }

    [Fact]
    public void MaximumClearanceDoesNotSilentlyRouteThroughAnAdjacentEquipmentEnvelope()
    {
        var screen = HmiDiagramModelTests.Project().Screens[0];
        screen.Links[0].Clearance = 128; screen.Links[0].Thickness = 12;
        var layer = new HmiLinkLayer(); layer.SetScreen(screen);
        Assert.Equal(HmiRouteStatus.BlockedTerminal, layer.Routes["ab"].Status);
        Assert.Empty(layer.Routes["ab"].Points);
    }

    [Fact]
    public void RuntimeKeepsReadbackRouteIdentityAndReleasesItsLinkViewOnDisposal()
    {
        var project = HmiDiagramModelTests.Project();
        var runtime = new HmiRuntime(project, DateTimeOffset.UnixEpoch); runtime.Start(true);
        var view = new HmiScreenView(project, runtime);
        var route = view.DiagramLayer.Routes["ab"];
        runtime.Write("Running", HmiValue.From(false));
        Assert.Same(route, view.DiagramLayer.Routes["ab"]);
        runtime.AdvanceTime(DateTimeOffset.UnixEpoch.AddSeconds(6));
        Assert.Equal(HmiQuality.Stale, view.DiagramLayer.GetLinkQuality("ab"));
        Assert.Same(route, view.DiagramLayer.Routes["ab"]);
        view.Dispose(); Assert.Empty(view.DiagramLayer.Routes); Assert.Null(view.DiagramLayer.ReadSample);
    }

    [Fact]
    public void HidingEndpointRemovesHitAndInkWithoutInventingATopologyEdit()
    {
        var screen = HmiDiagramModelTests.Project().Screens[0]; var layer = new HmiLinkLayer(); layer.SetScreen(screen);
        var route = layer.Routes["ab"];
        var a = route.Points[0]; var b = route.Points[^1]; var midpoint = new HmiPoint((a.X + b.X) / 2, a.Y);
        // Find a definite retained line point outside the glyph obstacles.
        midpoint = route.Points.Count == 2 ? midpoint : route.Points[1];
        Assert.Equal("ab", layer.HitLink(midpoint, 8));
        layer.SetElementVisible("a", false); Assert.Null(layer.HitLink(midpoint, 8));
        Assert.Same(route, layer.Routes["ab"]); Assert.False(screen.Elements[0].IsHidden);
        layer.SetElementVisible("a", true); Assert.Equal("ab", layer.HitLink(midpoint, 8));
    }
}
