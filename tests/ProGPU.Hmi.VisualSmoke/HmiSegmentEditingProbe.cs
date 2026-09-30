using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Silk.NET.Input;

internal static class HmiSegmentEditingProbe
{
    internal static void Run(HeadlessWindow window, TtfFont font, string output, HmiColorScheme scheme)
    {
        string suffix = scheme.ToString().ToLowerInvariant();
        var project = new HmiProject { Name = "Process line / direct segment editing", Screens = [new()
        {
            Id = "overview", Width = 1000, Height = 680, Elements = [
                new() { Id = "title", Symbol = HmiSymbol.Label, Label = "TRANSFER TRAIN / DIRECT LINE EDITING", X = 30, Y = 20, Width = 900, Height = 45 },
                new() { Id = "a", Symbol = HmiSymbol.Valve, Label = "XV-101 / Feed isolation", X = 60, Y = 270, Width = 180, Height = 190 },
                new() { Id = "b", Symbol = HmiSymbol.Pump, Label = "P-201 / Transfer pump", X = 700, Y = 270, Width = 180, Height = 190 },
                new() { Id = "filter", Symbol = HmiSymbol.Filter, Label = "F-102 / Obstruction", X = 380, Y = 310, Width = 200, Height = 200 }
            ], Links = [new() { Id = "line", Name = "Feed bypass / straight segment", Source = new() { ElementId = "a", PortId = "outlet" }, Target = new() { ElementId = "b", PortId = "inlet" } }]
        }] };
        window.Resize(1600, 1000);
        using var host = new HmiDesignerHost(project, font) { ColorScheme = scheme };
        window.Content = host; window.Render(0); host.SetDataPanelsVisible(false); host.Fit(); window.Render(0);
        InputSystem.Current = new WindowInputState { Root = host };
        host.SelectDiagramLink("line"); window.Render(0);
        try
        {
            var segment = host.GetSelectedRouteSegments().Where(s => s.IsHorizontal).OrderByDescending(s => Math.Abs(s.End.X - s.Start.X)).First();
            var point = segment.Midpoint; string original = host.Session.ExportJson();
            var controls = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().ToArray();
            var before = window.ReadPixels();
            Press(point); Check(host.IsDraggingRouteSegment, "The actual segment capsule did not capture its pointer.");
            Move(point with { Y = point.Y - 80 }); window.Render(0); window.Render(0);
            Check(host.Session.ExportJson() == original && !host.Session.CanUndo, "Segment preview entered document history.");
            Check(controls.SequenceEqual(host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>()), "Segment movement recreated equipment.");
            Check(!before.SequenceEqual(window.ReadPixels()), "The segment preview did not alter actual framebuffer ink.");
            window.SaveScreenshot(Path.Combine(output, "segment-preview-" + suffix + ".png"));
            InputSystem.InjectMouseUp(MouseButton.Left); window.Render(0);
            Check(!host.IsDraggingRouteSegment && Link().StraightSegments.Count == 1, "Release did not persist the straight segment.");
            Check(host.DiagramLayer.Routes["line"].Status == HmiRouteStatus.Success, "A clear segment edit failed to route.");
            host.Session.Undo(); Check(host.Session.ExportJson() == original, "A segment drag was not one exact undo operation.");
            // Capture-loss and Escape keep the original configuration; no pointer-up may resurrect it.
            Press(point); Move(point with { Y = point.Y - 50 }); InputSystem.ReleasePointerCaptures(host.WorkspaceCanvas);
            InputSystem.InjectMouseUp(MouseButton.Left); Check(host.Session.ExportJson() == original && !host.IsEditingRoute, "Lost capture committed the preview.");
            Press(point); Move(point with { Y = point.Y - 60 });
            InputSystem.InjectKeyDown(Key.Escape); InputSystem.InjectKeyUp(Key.Escape); InputSystem.InjectMouseUp(MouseButton.Left);
            Check(host.Session.ExportJson() == original && !host.IsEditingRoute, "Escape did not retire the segment.");
            // Force a span through the filter. It must remain an explicit blocked request, not a new detour.
            Press(point); var bounds = HmiPortLayout.GetBounds(project.Screens[0].Elements.Single(e => e.Id == "filter"));
            Move(point with { Y = (bounds.Top + bounds.Bottom) / 2 }); window.Render(0); window.Render(0);
            Check(host.DiagramLayer.Routes["line"].Status == HmiRouteStatus.BlockedSegment, "Blocked straight segment became a misleading successful path.");
            window.SaveScreenshot(Path.Combine(output, "segment-blocked-preview-" + suffix + ".png"));
            InputSystem.InjectMouseUp(MouseButton.Left); window.Render(0);
            Check(host.DiagramLayer.Routes["line"].Status == HmiRouteStatus.BlockedSegment, "Release silently repaired an exact constraint.");
            var recovery = host.GetSelectedRouteSegments().Single();
            Check(recovery.StraightSpanIndex >= 0, "Blocked segment has no retained recovery grip.");
            // The recovery grip must win over the equipment covering its center.
            Press(recovery.Midpoint); Check(host.IsDraggingRouteSegment, "Equipment intercepted a blocked-segment recovery grip.");
            Move(recovery.Midpoint with { Y = point.Y - 90 }); InputSystem.InjectMouseUp(MouseButton.Left); window.Render(0);
            Check(host.DiagramLayer.Routes["line"].Status == HmiRouteStatus.Success, "Native recovery drag failed.");
            window.SaveScreenshot(Path.Combine(output, "segment-recovered-" + suffix + ".png"));
            host.Session.Undo(); host.Session.Undo(); Check(host.Session.ExportJson() == original, "Blocked/recovered edits were not independently undoable.");
            Console.WriteLine($"{scheme}: native straight-segment grips, retained preview, exact undo, Escape/capture loss, blocked-span pixels and over-equipment recovery passed.");
        }
        finally { InputSystem.Current = new WindowInputState(); window.Content = null; }
        HmiDiagramLink Link() => host.Session.GetProject().Screens[0].Links[0];
        Vector2 Screen(HmiPoint point) => host.WorkspaceCanvas.DesignSurface.TransformToVisual(host).TransformPoint(new(point.X, point.Y));
        void Move(HmiPoint point) => InputSystem.InjectMouseMove(Screen(point));
        void Press(HmiPoint point) { Move(point); InputSystem.InjectMouseDown(MouseButton.Left); }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
