using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiSegmentEditingTests
{
    private static HmiDesignerHost Host()
    {
        var host = new HmiDesignerHost(HmiDiagramModelTests.Project());
        host.SelectDiagramLink("ab");
        return host;
    }
    private static HmiRouteSegment Horizontal(HmiDesignerHost host) => host.GetSelectedRouteSegments()
        .Where(s => s.IsHorizontal).OrderByDescending(s => Math.Abs(s.End.X - s.Start.X)).First();

    [Fact]
    public void PreviewRetainsEquipmentAndCommitsExactlyOneUndoRecord()
    {
        using var host = Host(); var segment = Horizontal(host);
        var original = host.Session.ExportJson(); var controls = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().ToArray();
        host.BeginSegmentDrag(segment.Index);
        host.PreviewSegmentOffset(-60); host.PreviewSegmentOffset(-70);
        Assert.True(host.IsDraggingRouteSegment); Assert.Equal(original, host.Session.ExportJson()); Assert.False(host.Session.CanUndo);
        Assert.Equal(controls, host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>());
        long passes = host.DiagramLayer.RoutingPasses; host.PreviewSegmentOffset(-70); Assert.Equal(passes, host.DiagramLayer.RoutingPasses);
        host.CommitSegmentDrag();
        Assert.False(host.IsEditingRoute); Assert.Single(host.Session.GetProject().Screens[0].Links[0].StraightSegments);
        Assert.Equal(HmiRouteStatus.Success, host.DiagramLayer.Routes["ab"].Status);
        host.Session.Undo(); Assert.Equal(original, host.Session.ExportJson()); Assert.False(host.Session.CanUndo);
        host.Session.Redo(); Assert.Single(host.Session.GetProject().Screens[0].Links[0].StraightSegments);
    }

    [Fact]
    public void GripClickAndReturningToOriginalOffsetRemainNoOps()
    {
        using var host = Host(); string original = host.Session.ExportJson();
        host.BeginSegmentDrag(Horizontal(host).Index); host.CommitSegmentDrag();
        Assert.Equal(original, host.Session.ExportJson());
        host.BeginSegmentDrag(Horizontal(host).Index); host.PreviewSegmentOffset(-60); host.PreviewSegmentOffset(0); host.CommitSegmentDrag();
        Assert.Equal(original, host.Session.ExportJson()); Assert.False(host.Session.CanUndo);
    }

    [Theory]
    [InlineData("cancel")] [InlineData("foreign")] [InlineData("viewport")] [InlineData("preview")] [InlineData("dispose")]
    public void ContextChangesRetireTheDraftWithoutPublishingIt(string change)
    {
        using var host = Host(); string initial = host.Session.ExportJson();
        host.BeginSegmentDrag(Horizontal(host).Index); host.PreviewSegmentOffset(-60);
        switch (change)
        {
            case "cancel": host.CancelRouteEdit(); break;
            case "foreign": host.Session.Edit("Rename project", p => p.Name = "Other"); break;
            case "viewport": host.WorkspaceCanvas.ZoomScale = 2; host.WorkspaceCanvas.ApplyTransforms(); break;
            case "preview": host.StartPreview(automaticTicks: false); host.StopPreview(); break;
            case "dispose": host.Dispose(); break;
        }
        Assert.False(host.IsDraggingRouteSegment); host.CommitSegmentDrag();
        Assert.Empty(host.Session.GetProject().Screens[0].Links[0].StraightSegments);
        if (change != "foreign") Assert.Equal(initial, host.Session.ExportJson());
    }

    [Fact]
    public void BlockedPersistedSpanHasAnEditableRecoveryHandle()
    {
        using var host = Host();
        var segment = Horizontal(host);
        host.BeginSegmentDrag(segment.Index); host.PreviewSegmentOffset(-100); host.CommitSegmentDrag();
        var pins = host.Session.GetProject().Screens[0].Links[0].Waypoints;
        float middle = (pins[0].X + pins[1].X) / 2;
        host.Session.Edit("Place obstruction", p => p.Screens[0].Elements.Add(new()
        {
            Id = "wall", Symbol = HmiSymbol.NumericDisplay, X = middle - 15, Y = pins[0].Y - 30, Width = 30, Height = 60,
            Appearance = new() { Presentation = HmiPresentation.Card }
        }));
        Assert.Equal(HmiRouteStatus.BlockedSegment, host.DiagramLayer.Routes["ab"].Status);
        var recovery = Assert.Single(host.GetSelectedRouteSegments()); Assert.True(recovery.StraightSpanIndex >= 0);
        host.BeginSegmentDrag(recovery.Index); host.PreviewSegmentOffset(-80); host.CommitSegmentDrag();
        Assert.Equal(HmiRouteStatus.Success, host.DiagramLayer.Routes["ab"].Status);
        Assert.Single(host.Session.GetProject().Screens[0].Links[0].StraightSegments);
    }

    [Fact]
    public void ExistingPinEditCannotSilentlyBendAStraightSpan()
    {
        using var host = Host(); host.BeginSegmentDrag(Horizontal(host).Index); host.PreviewSegmentOffset(-60); host.CommitSegmentDrag();
        var link = host.Session.GetProject().Screens[0].Links[0]; var original = host.Session.ExportJson();
        Assert.Throws<InvalidDataException>(() => host.MoveRouteWaypoint(0, link.Waypoints[0] with { Y = link.Waypoints[0].Y - 20 }));
        Assert.Equal(original, host.Session.ExportJson());
        host.ReleaseStraightSegments(); host.MoveRouteWaypoint(0, link.Waypoints[0] with { Y = link.Waypoints[0].Y - 20 });
        Assert.Empty(host.Session.GetProject().Screens[0].Links[0].StraightSegments);
    }

    [Fact]
    public void ReverseAndDuplicateRemapConstraintsWithoutSharingLists()
    {
        using var host = Host(); host.BeginSegmentDrag(Horizontal(host).Index); host.PreviewSegmentOffset(-60); host.CommitSegmentDrag();
        var pins = host.Session.GetProject().Screens[0].Links[0].Waypoints.ToArray();
        host.ReverseSelectedLink();
        var link = host.Session.GetProject().Screens[0].Links[0];
        Assert.Equal(pins.Reverse(), link.Waypoints); Assert.Equal(new[] { 0 }, link.StraightSegments);
        host.Session.DuplicateScreen(); var project = host.Session.GetProject();
        Assert.Equal(project.Screens[0].Links[0].StraightSegments, project.Screens[1].Links[0].StraightSegments);
        Assert.NotSame(project.Screens[0].Links[0].StraightSegments, project.Screens[1].Links[0].StraightSegments);
        Assert.NotEqual(project.Screens[0].Links[0].Source.ElementId, project.Screens[1].Links[0].Source.ElementId);
    }

    [Theory]
    [InlineData("lock")] [InlineData("hidden")] [InlineData("runtime")]
    public void EditGatesRemainAuthoritative(string gate)
    {
        using var host = Host(); int index = Horizontal(host).Index;
        if (gate == "runtime") host.StartPreview(automaticTicks: false);
        else host.Session.Edit("Apply gate", p => { if (gate == "lock") p.Screens[0].Links[0].IsLocked = true; else p.Screens[0].Links[0].IsHidden = true; });
        Assert.Throws<InvalidOperationException>(() => host.BeginSegmentDrag(index));
    }

    [Fact]
    public void SpanOnlyChangeInvalidatesRouteCacheAndTelemetryDoesNot()
    {
        var project = HmiDiagramModelTests.Project(); var screen = project.Screens[0];
        var layer = new HmiLinkLayer(); layer.SetScreen(screen);
        var segment = layer.GetEditableSegments("ab").First(s => s.IsHorizontal);
        var constraints = new HmiRouteSegmentEdit(layer.Routes["ab"], segment, [], []).Preview(-60);
        screen.Links[0].Waypoints = constraints.Waypoints.ToList(); layer.SetScreen(screen);
        var previous = layer.Routes["ab"]; long passes = layer.RoutingPasses;
        screen.Links[0].StraightSegments = constraints.StraightSegments.ToList(); layer.SetScreen(screen);
        Assert.NotSame(previous, layer.Routes["ab"]); Assert.Equal(passes + 1, layer.RoutingPasses);
        var retained = layer.Routes["ab"]; layer.ColorScheme = HmiColorScheme.Dark; layer.RefreshTags();
        Assert.Same(retained, layer.Routes["ab"]); Assert.Equal(passes + 1, layer.RoutingPasses);
    }
}
