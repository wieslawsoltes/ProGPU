using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Scene;
using ProGPU.WinUI.Designer;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiCanvasAuthoringTests
{
    [Fact]
    public void SharedAdornerValidatesBoundedSnapshotsBeforeShowingThem()
    {
        Assert.Throws<ArgumentNullException>(() => new DesignerRegionAdorner(null!));
        var adorner = new DesignerRegionAdorner(new DesignerCanvas());
        Assert.False(adorner.IsHitTestVisible); Assert.False(adorner.IsVisible);
        Assert.Throws<ArgumentNullException>(() => adorner.Show(new(0, 0, 10, 10), false, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => adorner.Show(new(0, 0, 10, 10), false, new string('x', 257)));
        Assert.Throws<ArgumentOutOfRangeException>(() => adorner.Show(new(0, 0, 10, 10), false, "Region", Enumerable.Repeat(new Rect(0, 0, 10, 10), 5001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => adorner.Show(new(0, 0, 10, 10), false, "Region", [new(float.NaN, 0, 10, 10)]));
        Assert.False(adorner.IsVisible);
        adorner.Show(new(0, 0, 10, 10), false, "Region"); Assert.True(adorner.IsVisible);
        adorner.Hide(); Assert.False(adorner.IsVisible);
    }

    public HmiCanvasAuthoringTests() => InputSystem.Current = new WindowInputState();
    private static HmiDesignerHost Host()
    {
        var project = new HmiProject { Screens = [new() { Id = "overview", Width = 1200, Height = 800, Elements = [
            new() { Id = "a", Name = "A", Symbol = HmiSymbol.Pump, X = 100, Y = 100, Width = 100, Height = 100 },
            new() { Id = "b", Name = "B", Symbol = HmiSymbol.Valve, X = 300, Y = 100, Width = 100, Height = 100 },
            new() { Id = "hidden", Name = "Hidden", Symbol = HmiSymbol.Tank, X = 100, Y = 100, Width = 100, Height = 100, IsHidden = true }
        ] }] };
        var host = new HmiDesignerHost(project);
        host.Measure(new Vector2(1600, 1000)); host.Arrange(new Rect(0, 0, 1600, 1000));
        host.WorkspaceCanvas.ZoomScale = 1; host.WorkspaceCanvas.PanOffset = Vector2.Zero; host.WorkspaceCanvas.ApplyTransforms();
        InputSystem.Current.Root = host;
        return host;
    }
    private static string[] Selected(HmiDesignerHost host) => host.Selection.Selection.OfType<HmiControl>().Select(c => c.ElementId).ToArray();
    private static HmiControl Ghost(HmiDesignerHost host) => host.WorkspaceCanvas.AdornerSurface.Children.OfType<HmiControl>().Single();

    [Fact]
    public void PlacementPreviewIsRetainedAndCommitsOneExactUndoTransaction()
    {
        using var host = Host(); string before = host.Session.ExportJson();
        var original = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().ToArray();
        long routes = host.DiagramLayer.RoutingPasses;
        host.BeginComponentPlacement(HmiSymbol.HeatExchanger); host.BeginPlacementDrag(new(450, 300));
        var preview = Ghost(host);
        for (int i = 0; i < 50; i++) host.PreviewPlacement(new(600 + i * 2, 550));
        Assert.Equal(before, host.Session.ExportJson()); Assert.False(host.Session.CanUndo);
        Assert.Equal(original, host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>());
        Assert.Same(preview, Ghost(host)); Assert.Equal(routes, host.DiagramLayer.RoutingPasses);
        Assert.False(preview.CommandsEnabled); Assert.False(preview.IsHitTestVisible);
        host.CommitPlacement(); Assert.False(host.IsPlacingComponent);
        var added = host.Session.GetProject().Screens[0].Elements[^1];
        Assert.Equal(450f, added.X); Assert.Equal(300f, added.Y); Assert.Equal(250f, added.Width); Assert.Equal(250f, added.Height);
        Assert.Equal(new[] { added.Id }, Selected(host));
        Assert.Empty(host.WorkspaceCanvas.AdornerSurface.Children.OfType<HmiControl>());
        host.Session.Undo(); Assert.Equal(before, host.Session.ExportJson()); Assert.False(host.Session.CanUndo);
        host.Session.Redo(); Assert.Equal(added.Id, host.Session.GetProject().Screens[0].Elements[^1].Id);
    }

    public static IEnumerable<object[]> Symbols() => Enum.GetValues<HmiSymbol>().Select(symbol => new object[] { symbol });
    [Theory, MemberData(nameof(Symbols))]
    public void ClickPlacementUsesTheExactCatalogDefaults(HmiSymbol symbol)
    {
        using var host = Host();
        host.BeginComponentPlacement(symbol); host.BeginPlacementDrag(new(504, 304));
        // Alt changing on release must not turn a click into an 8x8 rectangle.
        host.PreviewPlacement(new(504, 304), bypassSnap: true); host.CommitPlacement();
        var added = host.Session.GetProject().Screens[0].Elements[^1]; var expected = HmiControlCatalog.CreateDefinition(symbol);
        Assert.Equal(500f, added.X); Assert.Equal(300f, added.Y);
        Assert.Equal(expected.Width, added.Width); Assert.Equal(expected.Height, added.Height);
        Assert.Equal(expected.Appearance.GraphicStyle, added.Appearance.GraphicStyle);
        Assert.Empty(added.Tag); Assert.Equal(HmiActionKind.None, added.Action.Kind);
    }

    [Fact]
    public void CapturedForeignPointerCannotMoveOrCommitTheOwnersPreview()
    {
        using var host = Host(); string before = host.Session.ExportJson();
        host.BeginComponentPlacement(HmiSymbol.Tank);
        PointerEvent(host, PointerInputKind.Pressed, 41, 450, 300, true);
        PointerEvent(host, PointerInputKind.Moved, 41, 650, 550, true);
        Assert.True(host.IsPlacingComponent); var bounds = host.AuthoringBounds;
        PointerEvent(host, PointerInputKind.Pressed, 42, 500, 350, true);
        PointerEvent(host, PointerInputKind.Moved, 42, 900, 600, true);
        PointerEvent(host, PointerInputKind.Released, 42, 900, 600, false);
        Assert.Equal(bounds, host.AuthoringBounds); Assert.Equal(before, host.Session.ExportJson());
        PointerEvent(host, PointerInputKind.Released, 41, 650, 550, false);
        Assert.False(host.IsPlacingComponent); Assert.Single(host.Session.GetProject().Screens[0].Elements, e => !new[] { "a", "b", "hidden" }.Contains(e.Id));
    }

    [Fact]
    public void ReentrantCaptureReleaseCannotPublishTheRetiredPlacement()
    {
        using var host = Host(); string before = host.Session.ExportJson();
        host.BeginComponentPlacement(HmiSymbol.Tank);
        PointerEvent(host, PointerInputKind.Pressed, 41, 450, 300, true);
        PointerEvent(host, PointerInputKind.Moved, 41, 650, 550, true);
        bool replaced = false;
        host.WorkspaceCanvas.PointerCaptureLost += (_, _) =>
        {
            if (replaced) return;
            replaced = true; host.BeginComponentPlacement(HmiSymbol.Gauge); host.BeginPlacementDrag(new(750, 400));
        };
        PointerEvent(host, PointerInputKind.Released, 41, 650, 550, false);
        Assert.True(replaced); Assert.Equal(before, host.Session.ExportJson()); Assert.False(host.Session.CanUndo);
        Assert.True(host.IsPlacingComponent); Assert.Equal(HmiSymbol.Gauge, Ghost(host).Symbol);
        host.CommitPlacement(); Assert.Equal(HmiSymbol.Gauge, host.Session.GetProject().Screens[0].Elements[^1].Symbol);
    }

    [Fact]
    public void NativeCanceledPointerRestoresSelectionAndDoesNotChangeTheDocument()
    {
        using var host = Host(); string before = host.Session.ExportJson();
        var b = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Single(c => c.ElementId == "b");
        host.Selection.SelectRange([b]);
        PointerEvent(host, PointerInputKind.Pressed, 51, 80, 80, true);
        PointerEvent(host, PointerInputKind.Moved, 51, 420, 220, true);
        Assert.True(host.IsSelectingArea); Assert.Equal(new[] { "b" }, Selected(host));
        PointerEvent(host, PointerInputKind.Canceled, 51, 420, 220, false);
        Assert.False(host.IsSelectingArea); Assert.Equal(new[] { "b" }, Selected(host)); Assert.Equal(before, host.Session.ExportJson());
    }

    private static void PointerEvent(HmiDesignerHost host, PointerInputKind kind, uint id, float x, float y, bool down)
    {
        var canvas = host.WorkspaceCanvas;
        var point = canvas.TransformToVisual(host).TransformPoint(new Vector2(x, y) * canvas.ZoomScale + canvas.PanOffset);
        InputSystem.InjectPointer(new PointerInputEvent(kind, id, Windows.Devices.Input.PointerDeviceType.Touch, point,
            Timestamp: 1, IsInContact: down, IsLeftButtonPressed: down));
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(1, -1)] [InlineData(-1, 1)] [InlineData(-1, -1)]
    public void AspectConstraintRetainsAnchorAndPointerQuadrant(int x, int y)
    {
        using var host = Host(); host.BeginComponentPlacement(HmiSymbol.Pump); host.BeginPlacementDrag(new(500, 400));
        host.PreviewPlacement(new(500 + x * 200, 400 + y * 100), preserveAspect: true); host.CommitPlacement();
        var added = host.Session.GetProject().Screens[0].Elements[^1];
        Assert.Equal(170d / 155, added.Width / added.Height, 5);
        Assert.Equal(x < 0 ? 300f : 500f, added.X);
        Assert.Equal(y < 0 ? 400 - added.Height : 400, added.Y);
    }
    [Fact]
    public void AltBypassesGridAndInvalidCoordinatesDoNotPublish()
    {
        using var host = Host(); string before = host.Session.ExportJson();
        host.BeginComponentPlacement(HmiSymbol.Tank); host.BeginPlacementDrag(new(403, 307), bypassSnap: true);
        host.PreviewPlacement(new(634, 596), bypassSnap: true);
        var bounds = host.AuthoringBounds;
        Assert.Throws<ArgumentOutOfRangeException>(() => host.PreviewPlacement(new(float.NaN, 300)));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.PreviewPlacement(new(30000, 30000)));
        Assert.True(host.AuthoringBounds!.Value.Equals(bounds!.Value)); Assert.Equal(before, host.Session.ExportJson());
        host.CommitPlacement(); var added = host.Session.GetProject().Screens[0].Elements[^1];
        Assert.Equal(403f, added.X); Assert.Equal(307f, added.Y); Assert.Equal(231f, added.Width); Assert.Equal(289f, added.Height);
    }
    [Fact]
    public void CancellationForeignEditsAndViewportChangesCannotCommitDrafts()
    {
        using var host = Host(); string initial = host.Session.ExportJson();
        void Begin() { host.BeginComponentPlacement(HmiSymbol.Filter); host.BeginPlacementDrag(new(450, 300)); host.PreviewPlacement(new(700, 600)); }
        Begin(); host.CancelCanvasAuthoring(); host.CommitPlacement(); Assert.Equal(initial, host.Session.ExportJson());
        Begin(); host.Session.Edit("Other change", p => p.Name = "Updated"); host.CommitPlacement();
        Assert.Equal("Updated", host.Session.GetProject().Name); Assert.Equal(3, host.Session.GetProject().Screens[0].Elements.Count);
        Begin(); host.WorkspaceCanvas.ZoomScale = .75f; host.WorkspaceCanvas.ApplyTransforms(); host.CommitPlacement();
        Assert.False(host.IsPlacingComponent); Assert.Equal(3, host.Session.GetProject().Screens[0].Elements.Count);
        Begin(); host.StartPreview(automaticTicks: false); Assert.False(host.IsPlacingComponent); host.StopPreview();
        Begin(); host.Dispose(); Assert.Empty(host.WorkspaceCanvas.AdornerSurface.Children.OfType<HmiControl>());
    }
    [Fact]
    public void PaletteChangesRetainTheSameDraftWithoutEditingTheProject()
    {
        using var host = Host(); host.BeginComponentPlacement(HmiSymbol.Valve); host.BeginPlacementDrag(new(450, 300));
        var preview = Ghost(host); string before = host.Session.ExportJson();
        host.ColorScheme = HmiColorScheme.Dark;
        Assert.Same(preview, Ghost(host)); Assert.Equal(HmiColorScheme.Dark, preview.ColorScheme); Assert.Equal(before, host.Session.ExportJson());
    }
    [Fact]
    public void AreaPreviewDoesNotChangeSelectionOrHistoryAndWindowContainsFullBounds()
    {
        using var host = Host(); string before = host.Session.ExportJson();
        host.BeginAreaSelection(new(90, 90)); host.PreviewAreaSelection(new(350, 210));
        Assert.Empty(Selected(host)); Assert.Equal(before, host.Session.ExportJson());
        host.CommitAreaSelection(); Assert.Equal(new[] { "a" }, Selected(host));
        Assert.False(host.Session.CanUndo); Assert.Equal(before, host.Session.ExportJson());
        Assert.Same(host.Selection.Selection[0], host.WorkspaceCanvas.SelectedElement);
    }
    [Fact]
    public void CrossingSelectsIntersectedComponentsInPainterOrder()
    {
        using var host = Host();
        host.BeginAreaSelection(new(350, 210)); host.PreviewAreaSelection(new(150, 150)); host.CommitAreaSelection();
        Assert.Equal(new[] { "a", "b" }, Selected(host));
        Assert.Equal("b", ((HmiControl)host.WorkspaceCanvas.SelectedElement!).ElementId);
    }
    [Theory]
    [InlineData(DesignerSelectionOperation.Add, "a,b")]
    [InlineData(DesignerSelectionOperation.Remove, "a")]
    [InlineData(DesignerSelectionOperation.Toggle, "a")]
    [InlineData(DesignerSelectionOperation.Replace, "b")]
    public void AreaOperationsAreAppliedOnce(DesignerSelectionOperation operation, string expected)
    {
        using var host = Host();
        host.Selection.SelectRange(host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Where(c => c.ElementId != "hidden"));
        host.BeginAreaSelection(new(290, 90), operation);
        for (int i = 0; i < 20; i++) host.PreviewAreaSelection(new(410, 210));
        Assert.Equal(new[] { "a", "b" }, Selected(host));
        host.CommitAreaSelection(); Assert.Equal(expected.Split(','), Selected(host));
    }
    [Fact]
    public void GroupExpansionDoesNotSelectHiddenMembersAndDoesNotUnlockEquipment()
    {
        using var host = Host();
        host.Session.Edit("Group", p => { foreach (var e in p.Screens[0].Elements) e.Group = "station"; p.Screens[0].Elements[1].IsLocked = true; });
        string before = host.Session.ExportJson();
        host.BeginAreaSelection(new(90, 90)); host.PreviewAreaSelection(new(210, 210)); host.CommitAreaSelection();
        Assert.Equal(new[] { "a", "b" }, Selected(host)); Assert.Equal(before, host.Session.ExportJson());
        host.Selection.Translate(10, 0); var elements = host.Session.GetProject().Screens[0].Elements;
        Assert.Equal(110f, elements[0].X); Assert.Equal(300f, elements[1].X); Assert.True(elements[1].IsLocked);
    }
    [Fact]
    public void ForeignSelectionRetiresAnAreaPreviewInsteadOfOverwritingIt()
    {
        using var host = Host(); host.BeginAreaSelection(new(90, 90)); host.PreviewAreaSelection(new(410, 210));
        var b = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Single(c => c.ElementId == "b");
        host.Selection.Select(b); host.CommitAreaSelection();
        Assert.False(host.IsSelectingArea); Assert.Equal(new[] { "b" }, Selected(host));
    }
    [Fact]
    public void SwitchingToolsRetiresThePreviousDraft()
    {
        using var host = Host();
        host.BeginComponentPlacement(HmiSymbol.Filter); host.BeginPlacementDrag(new(450, 300));
        host.BeginDiagramConnection(); Assert.False(host.IsPlacingComponent); Assert.True(host.IsConnectingDiagram);
        host.BeginComponentPlacement(HmiSymbol.Gauge); Assert.False(host.IsConnectingDiagram);
        host.BeginAreaSelection(new(90, 90)); Assert.False(host.IsPlacingComponent); Assert.True(host.IsSelectingArea);
        host.CancelCanvasAuthoring(); Assert.False(host.IsSelectingArea); Assert.False(host.Session.CanUndo);
    }
    [Fact]
    public void RegionQueryUsesDetachedBoundsAndAtomicSelectionNotification()
    {
        using var host = Host(); var canvas = host.WorkspaceCanvas;
        var query = new DesignerRegionQuery(canvas);
        var a = canvas.DesignSurface.Children.OfType<HmiControl>().Single(c => c.ElementId == "a");
        Canvas.SetLeft(a, 800); canvas.Measure(new(1000, 700)); canvas.Arrange(new(0, 0, 1000, 700));
        Assert.Equal(new[] { a }, query.Query(new(90, 90, 120, 120), false));
        int changed = 0; host.Selection.SelectionChanged += () => changed++;
        host.Selection.SelectRange([a, a, host.DiagramLayer]);
        Assert.Equal(1, changed); Assert.Single(host.Selection.Selection);
        host.Selection.SelectRange([a]); Assert.Equal(1, changed);
        Assert.Throws<ArgumentException>(() => host.Selection.SelectRange([a, new Button()]));
        Assert.Equal(1, changed); Assert.Single(host.Selection.Selection);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(100001)]
    public void RegionQueryRejectsInvalidBudgets(int budget)
    {
        using var host = Host(); Assert.Throws<ArgumentOutOfRangeException>(() => new DesignerRegionQuery(host.WorkspaceCanvas, budget));
    }
    [Fact]
    public void RegionBudgetCannotSilentlyOmitVisibleEquipment()
    {
        using var host = Host(); Assert.Throws<InvalidOperationException>(() => new DesignerRegionQuery(host.WorkspaceCanvas, 1));
    }
    [Theory]
    [InlineData(1,1)] [InlineData(-1,1)] [InlineData(1,-1)] [InlineData(-1,-1)]
    public void SharedRectangleConstructionHandlesAllQuadrants(int dx, int dy)
    {
        var rect = DesignerDragRectangle.Create(new(10,20),new(10+dx*30,20+dy*40));
        Assert.Equal(dx<0?-20:10,rect.X); Assert.Equal(dy<0?-20:20,rect.Y); Assert.Equal(30,rect.Width); Assert.Equal(40,rect.Height);
        Assert.Throws<ArgumentOutOfRangeException>(() => DesignerDragRectangle.Create(new(float.MaxValue,0),new(-float.MaxValue,1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DesignerDragRectangle.Create(Vector2.Zero,Vector2.One,aspectRatio:0));
    }
}
