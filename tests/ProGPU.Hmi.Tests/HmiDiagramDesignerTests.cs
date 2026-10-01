using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.WinUI.Designer;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiDiagramDesignerTests
{
    [Fact]
    public void ConnectingSelectingReversingAndDeletingAreUndoableAndNeverSelectTheUnderlay()
    {
        var project = HmiDiagramModelTests.Project(); project.Screens[0].Links.Clear();
        using var host = new HmiDesignerHost(project);
        string initial = host.Session.ExportJson();
        string id = host.ConnectPorts(new() { ElementId = "a", PortId = "outlet" }, new() { ElementId = "b", PortId = "inlet" });
        Assert.Equal(id, host.SelectedLinkId); Assert.Empty(host.Selection.Selection);
        Assert.Equal(HmiRouteStatus.Success, host.DiagramLayer.Routes[id].Status);
        Assert.DoesNotContain(DesignerElementRegistry.GetLogicalChildren(host.WorkspaceCanvas.DesignSurface), e => ReferenceEquals(e, host.DiagramLayer));
        host.WorkspaceCanvas.SelectElement(host.DiagramLayer); Assert.Null(host.WorkspaceCanvas.SelectedElement);
        host.Selection.SelectAll(); Assert.Equal(2, host.Selection.Selection.Count); Assert.Null(host.SelectedLinkId);
        host.SelectDiagramLink(id); host.ReverseSelectedLink();
        Assert.Equal("b", host.Session.GetProject().Screens[0].Links[0].Source.ElementId);
        host.DeleteSelectedLink(); Assert.Empty(host.Session.GetProject().Screens[0].Links);
        host.Session.Undo(); Assert.Single(host.Session.GetProject().Screens[0].Links);
        host.Session.Undo(); Assert.Equal("a", host.Session.GetProject().Screens[0].Links[0].Source.ElementId);
        host.Session.Undo(); Assert.Equal(initial, host.Session.ExportJson());
    }

    [Fact]
    public void SharedInteractionModeDoesNotEnableHitTestingForDiagramDecorations()
    {
        using var host = new HmiDesignerHost(HmiDiagramModelTests.Project());
        host.WorkspaceCanvas.IsInteractionMode = true;
        Assert.False(host.DiagramLayer.IsHitTestVisible);
        host.WorkspaceCanvas.IsInteractionMode = false;
        Assert.False(host.DiagramLayer.IsHitTestVisible);
    }

    [Fact]
    public void DeletingEquipmentAndUndoRestoreItsIncidentConnectionAtomically()
    {
        using var host = new HmiDesignerHost(HmiDiagramModelTests.Project());
        string original = host.Session.ExportJson();
        var a = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Single(c => c.ElementId == "a");
        host.Selection.Select(a); host.Selection.Delete();
        var screen = host.Session.GetProject().Screens[0];
        Assert.Single(screen.Elements); Assert.Empty(screen.Links); Assert.Empty(host.DiagramLayer.Routes);
        host.Session.Undo(); Assert.Equal(original, host.Session.ExportJson()); Assert.Single(host.DiagramLayer.Routes);
    }

    [Fact]
    public void LockedIncidentConnectionPreventsDanglingTopologyAndRestoresTheVisual()
    {
        var project = HmiDiagramModelTests.Project(); project.Screens[0].Links[0].IsLocked = true;
        using var host = new HmiDesignerHost(project); string original = host.Session.ExportJson();
        var a = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Single(c => c.ElementId == "a");
        host.Selection.Select(a); host.Selection.Delete();
        Assert.Equal(original, host.Session.ExportJson()); Assert.Equal(2, host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Count());
        Assert.Contains("incident", host.StatusText, StringComparison.OrdinalIgnoreCase);
        host.SelectDiagramLink("ab");
        Assert.Throws<InvalidOperationException>(host.ReverseSelectedLink);
        Assert.Throws<InvalidOperationException>(host.DeleteSelectedLink);
        Assert.False(host.Session.CanUndo);
    }

    [Fact]
    public void ClipboardCopiesInternalTopologyWithFreshIdentityAndLeavesSourceControlsRetained()
    {
        using var host = new HmiDesignerHost(HmiDiagramModelTests.Project());
        var original = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().ToArray();
        host.Selection.SelectAll(); host.CopySelection(); host.PasteSelection();
        var screen = host.Session.GetProject().Screens[0];
        Assert.Equal(4, screen.Elements.Count); Assert.Equal(2, screen.Links.Count);
        Assert.NotEqual("ab", screen.Links[1].Id);
        Assert.NotEqual("a", screen.Links[1].Source.ElementId); Assert.NotEqual("b", screen.Links[1].Target.ElementId);
        Assert.All(original, control => Assert.Contains(host.WorkspaceCanvas.DesignSurface.Children, child => ReferenceEquals(control, child)));
        Assert.Same(host.DiagramLayer, host.WorkspaceCanvas.DesignSurface.Children[0]);
        host.Session.Undo(); Assert.Equal(2, host.Session.GetProject().Screens[0].Elements.Count); Assert.Single(host.DiagramLayer.Routes);
    }

    [Fact]
    public void CommandToolsCannotEditWhileLiveOrLeaveAPendingGestureInPreview()
    {
        using var host = new HmiDesignerHost(HmiDiagramModelTests.Project());
        host.BeginDiagramConnection(); Assert.True(host.IsConnectingDiagram); Assert.True(host.DiagramLayer.ShowPortHandles);
        host.StartPreview(automaticTicks: false);
        Assert.False(host.IsConnectingDiagram);
        Assert.Throws<InvalidOperationException>(host.BeginDiagramConnection);
        Assert.Throws<InvalidOperationException>(() => host.ConnectPorts(new() { ElementId = "a", PortId = "inlet" }, new() { ElementId = "b", PortId = "outlet" }));
        host.StopPreview(); Assert.False(host.Session.IsDirty);
    }

    [Fact]
    public void SameDirectedConnectionIsRejectedWithoutChangingHistory()
    {
        using var host = new HmiDesignerHost(HmiDiagramModelTests.Project()); string original = host.Session.ExportJson();
        Assert.Throws<InvalidOperationException>(() => host.ConnectPorts(new() { ElementId = "a", PortId = "outlet" }, new() { ElementId = "b", PortId = "inlet" }));
        Assert.Equal(original, host.Session.ExportJson()); Assert.False(host.Session.CanUndo);
    }
}
