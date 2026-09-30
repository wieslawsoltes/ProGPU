using Microsoft.UI.Xaml.Controls;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiWaypointDesignerTests
{
    private static HmiDesignerHost Host()
    {
        var host = new HmiDesignerHost(HmiDiagramModelTests.Project());
        host.SelectDiagramLink("ab");
        return host;
    }
    [Fact]
    public void InsertMoveRemoveAndClearAreIndependentUndoableTransactions()
    {
        using var host = Host(); string initial = host.Session.ExportJson();
        host.InsertRouteWaypoint(0, new(320, 120)); string inserted = host.Session.ExportJson();
        host.MoveRouteWaypoint(0, new(360, 140));
        Assert.Equal(new HmiPoint(360, 140), Pins(host)[0]);
        host.Session.Undo(); Assert.Equal(inserted, host.Session.ExportJson());
        host.SelectRouteWaypoint(0); host.RemoveSelectedWaypoint(); Assert.Empty(Pins(host));
        host.Session.Undo(); Assert.Single(Pins(host));
        host.ClearSelectedWaypoints(); Assert.Empty(Pins(host));
        host.Session.Undo(); Assert.Single(Pins(host));
        host.Session.Undo(); Assert.Equal(initial, host.Session.ExportJson());
    }
    [Fact]
    public void PreviewChangesNoDocumentAndCommitsOnlyOneHistoryEntry()
    {
        using var host = Host(); host.InsertRouteWaypoint(0, new(320, 120));
        string before = host.Session.ExportJson();
        var equipment = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().ToArray();
        host.BeginWaypointDrag(0);
        for (int i = 0; i < 30; i++) host.PreviewWaypointPosition(new(330 + i, 140));
        Assert.Equal(before, host.Session.ExportJson());
        Assert.True(host.IsEditingRoute);
        Assert.Equal(equipment, host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>());
        host.CommitWaypointDrag(); Assert.False(host.IsEditingRoute);
        Assert.Equal(new HmiPoint(359, 140), Pins(host)[0]);
        host.Session.Undo(); Assert.Equal(before, host.Session.ExportJson());
    }
    [Fact]
    public void CancelAndForeignDocumentRevisionRetirePreviewWithoutLosingNewEdits()
    {
        using var host = Host(); host.InsertRouteWaypoint(0, new(320, 120));
        string before = host.Session.ExportJson(); var route = host.DiagramLayer.Routes["ab"].Points.ToArray();
        host.BeginWaypointDrag(0); host.PreviewWaypointPosition(new(400, 150)); host.CancelRouteEdit();
        Assert.Equal(before, host.Session.ExportJson()); Assert.Equal(route, host.DiagramLayer.Routes["ab"].Points);
        host.BeginWaypointDrag(0); host.PreviewWaypointPosition(new(400, 150));
        host.Session.Edit("Independent edit", p => p.Name = "New project name");
        Assert.False(host.IsEditingRoute); host.CommitWaypointDrag();
        Assert.Equal("New project name", host.Session.GetProject().Name);
        Assert.Equal(new HmiPoint(320, 120), Pins(host)[0]);
    }
    [Fact]
    public void NoOpGestureDoesNotCreateAnUndoEntry()
    {
        using var host = Host(); host.InsertRouteWaypoint(0, new(320, 120));
        string before = host.Session.ExportJson();
        host.BeginWaypointDrag(0); host.PreviewWaypointPosition(new(320, 120)); host.CommitWaypointDrag();
        Assert.Equal(before, host.Session.ExportJson());
        host.Session.Undo(); Assert.Empty(Pins(host));
    }
    [Fact]
    public void InvalidPreviewLeavesTheLastValidPreviewAndDocumentUntouched()
    {
        using var host = Host(); host.InsertRouteWaypoint(0, new(320, 120));
        string before = host.Session.ExportJson(); host.BeginWaypointDrag(0);
        host.PreviewWaypointPosition(new(360, 140)); var goodRoute = host.DiagramLayer.Routes["ab"];
        Assert.Throws<InvalidDataException>(() => host.PreviewWaypointPosition(new(float.NaN, 120)));
        Assert.Same(goodRoute, host.DiagramLayer.Routes["ab"]); Assert.Equal(before, host.Session.ExportJson());
        host.CancelRouteEdit();
    }
    [Fact]
    public void BlockedPinsRemainEditableAndDoNotPublishAFalseRoute()
    {
        using var host = Host(); var a = host.Session.GetProject().Screens[0].Elements[0];
        var bounds = HmiPortLayout.GetBounds(a);
        host.InsertRouteWaypoint(0, new((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2));
        Assert.Equal(HmiRouteStatus.BlockedWaypoint, host.DiagramLayer.Routes["ab"].Status);
        Assert.Empty(host.DiagramLayer.Routes["ab"].Points);
        host.MoveRouteWaypoint(0, new(320, 120)); Assert.Equal(HmiRouteStatus.Success, host.DiagramLayer.Routes["ab"].Status);
    }
    [Fact]
    public void LocksAndSimulationRejectEditsAndPreviewTransitionsCancelGestures()
    {
        using var host = Host(); host.InsertRouteWaypoint(0, new(320, 120));
        host.Session.Edit("Lock", p => p.Screens[0].Links[0].IsLocked = true);
        string locked = host.Session.ExportJson();
        Assert.Throws<InvalidOperationException>(host.BeginWaypointPlacement);
        Assert.Throws<InvalidOperationException>(() => host.BeginWaypointDrag(0));
        Assert.Throws<InvalidOperationException>(() => host.MoveRouteWaypoint(0, new(400, 50)));
        Assert.Equal(locked, host.Session.ExportJson());
        host.Session.Undo(); host.BeginWaypointDrag(0); host.PreviewWaypointPosition(new(400, 150));
        host.StartPreview(automaticTicks: false); Assert.False(host.IsEditingRoute);
        Assert.Throws<InvalidOperationException>(() => host.InsertRouteWaypoint(0, new(20, 20)));
        host.StopPreview(); Assert.Equal(new HmiPoint(320, 120), Pins(host)[0]);
    }
    [Fact]
    public void ReversalReversesPinOrderAndClipboardTranslatesPinsWithEquipment()
    {
        using var host = Host(); host.InsertRouteWaypoint(0, new(300, 120)); host.InsertRouteWaypoint(1, new(400, 120));
        host.ReverseSelectedLink(); Assert.Equal(new[] { new HmiPoint(400, 120), new HmiPoint(300, 120) }, Pins(host));
        host.Session.Undo();
        host.Selection.SelectAll(); host.CopySelection(); host.PasteSelection();
        var links = host.Session.GetProject().Screens[0].Links;
        Assert.Equal(2, links.Count);
        Assert.Equal(new[] { new HmiPoint(320, 140), new HmiPoint(420, 140) }, links[1].Waypoints);
        Assert.Equal(new[] { new HmiPoint(300, 120), new HmiPoint(400, 120) }, links[0].Waypoints);
    }
    [Fact]
    public void DuplicatingScreenPreservesPositionsButOwnsPinLists()
    {
        using var host = Host(); host.InsertRouteWaypoint(0, new(320, 120)); host.Session.DuplicateScreen();
        var project = host.Session.GetProject(); Assert.Equal(2, project.Screens.Count);
        Assert.NotEqual(project.Screens[0].Links[0].Id, project.Screens[1].Links[0].Id);
        Assert.Equal(project.Screens[0].Links[0].Waypoints, project.Screens[1].Links[0].Waypoints);
        project.Screens[1].Links[0].Waypoints.Clear(); Assert.Single(project.Screens[0].Links[0].Waypoints);
    }
    [Fact]
    public void PreviewReroutesOnlySelectedConnectionAndSkipsUnchangedPointerPositions()
    {
        var project = HmiDiagramModelTests.Project(); var screen = project.Screens[0];
        var second = screen.Links[0].Copy(true); second.Id = "second"; screen.Links.Add(second);
        using var host = new HmiDesignerHost(project); host.SelectDiagramLink("ab"); host.InsertRouteWaypoint(0, new(320, 120));
        var unrelated = host.DiagramLayer.Routes["second"];
        long before = host.DiagramLayer.RoutingPasses;
        host.BeginWaypointDrag(0); host.PreviewWaypointPosition(new(330, 120)); host.PreviewWaypointPosition(new(330, 120));
        Assert.Equal(before + 1, host.DiagramLayer.RoutingPasses); Assert.Same(unrelated, host.DiagramLayer.Routes["second"]);
        host.CancelRouteEdit();
    }
    [Fact]
    public void CacheCannotSilentlyReuseAnOldRouteAfterObstacleAdmissionIsExceeded()
    {
        var project = HmiDiagramModelTests.Project(); var screen = project.Screens[0]; var layer = new HmiLinkLayer();
        layer.SetScreen(screen); Assert.Equal(HmiRouteStatus.Success, layer.Routes["ab"].Status);
        for (int i = 0; i < HmiOrthogonalRouter.MaximumObstacles - 1; i++)
            screen.Elements.Add(new() { Id = "obstacle-" + i, Symbol = HmiSymbol.NumericDisplay, X = 2000 + i * 20, Y = 5000, Width = 10, Height = 10 });
        layer.SetScreen(screen);
        Assert.Equal(HmiRouteStatus.CapacityExceeded, layer.Routes["ab"].Status);
        Assert.Empty(layer.Routes["ab"].Points);
    }
    [Fact]
    public void PaletteAndTelemetryChangesDoNotRecomputePinnedRoutes()
    {
        var project = HmiDiagramModelTests.Project(); project.Screens[0].Links[0].Waypoints = [new(320, 120)];
        var runtime = new HmiRuntime(project); using var screen = new HmiScreenView(project, runtime);
        var route = screen.DiagramLayer.Routes["ab"]; long passes = screen.DiagramLayer.RoutingPasses;
        runtime.Start(true); runtime.Write("Running", HmiValue.From(false)); screen.ColorScheme = HmiColorScheme.Dark;
        Assert.Same(route, screen.DiagramLayer.Routes["ab"]); Assert.Equal(passes, screen.DiagramLayer.RoutingPasses);
    }
    [Fact]
    public void ViewportNotificationCannotReplaceADetachedPinPreview()
    {
        using var host = Host(); host.InsertRouteWaypoint(0, new(320, 120));
        host.BeginWaypointDrag(0); host.PreviewWaypointPosition(new(360, 140));
        var route = host.DiagramLayer.Routes["ab"]; string document = host.Session.ExportJson();
        host.WorkspaceCanvas.NotifyCanvasModified();
        Assert.True(host.IsEditingRoute); Assert.Same(route, host.DiagramLayer.Routes["ab"]);
        Assert.Equal(document, host.Session.ExportJson()); host.CancelRouteEdit();
    }
    [Fact]
    public void BlockedRouteRemainsFramableWithoutInventingSuccessfulGeometry()
    {
        using var host = Host();
        var bounds = HmiPortLayout.GetBounds(host.Session.GetProject().Screens[0].Elements[0]);
        host.InsertRouteWaypoint(0, new((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2));
        var route = host.DiagramLayer.Routes["ab"]; host.ZoomToSelection();
        Assert.Same(route, host.DiagramLayer.Routes["ab"]); Assert.Empty(route.Points);
        Assert.True(float.IsFinite(host.WorkspaceCanvas.ZoomScale));
        Assert.True(float.IsFinite(host.WorkspaceCanvas.PanOffset.X));
    }
    private static List<HmiPoint> Pins(HmiDesignerHost host) => host.Session.GetProject().Screens[0].Links[0].Waypoints;
}
