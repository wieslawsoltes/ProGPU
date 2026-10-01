using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Silk.NET.Input;

internal static class HmiRouteEditingProbe
{
    internal static void Run(HeadlessWindow window, TtfFont font, string outputDirectory, HmiColorScheme scheme)
    {
        var project = new HmiProject
        {
            Name = "Routing workbench / transfer train", StartScreenId = "overview",
            Screens = [new() { Id = "overview", Width = 1000, Height = 650, Elements = [
                new() { Id = "heading", Symbol = HmiSymbol.Label, Label = "TRANSFER LINE / ROUTE CONSTRAINTS", X = 40, Y = 20, Width = 820, Height = 50 },
                new() { Id = "a", Symbol = HmiSymbol.Valve, Label = "XV-101 / Feed isolation", X = 60, Y = 240, Width = 180, Height = 190 },
                new() { Id = "b", Symbol = HmiSymbol.Pump, Label = "P-201 / Transfer", X = 650, Y = 240, Width = 180, Height = 190 },
                new() { Id = "filter", Symbol = HmiSymbol.Filter, Label = "F-102 / Clearance obstacle", X = 350, Y = 280, Width = 200, Height = 200 }
            ], Links = [new() { Id = "transfer", Name = "Feed bypass / 2 pinned constraints",
                Source = new() { ElementId = "a", PortId = "outlet" }, Target = new() { ElementId = "b", PortId = "inlet" } }] }]
        };
        window.Resize(1600, 1000);
        using var host = new HmiDesignerHost(project, font) { ColorScheme = scheme };
        window.Content = host;
        InputSystem.Current = new WindowInputState { Root = host };
        try
        {
            window.Render(0); host.Fit(); window.Render(0);
            host.SelectDiagramLink("transfer"); window.Render(0);
            string initial = host.Session.ExportJson();
            host.BeginWaypointPlacement();
            Click(new(300, 130));
            Check(!host.IsEditingRoute && Pins().SequenceEqual([new HmiPoint(300, 130)]), "A canvas click did not commit the ordered waypoint.");
            host.InsertRouteWaypoint(1, new(570, 130));
            window.Render(0);
            string beforeDrag = host.Session.ExportJson();
            var controls = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().ToArray();
            Press(new(300, 130));
            Check(host.IsEditingRoute, "The rendered waypoint handle did not capture the pointer.");
            Move(new(320, 150));
            Check(host.Session.ExportJson() == beforeDrag, "A pointer move entered project history before release.");
            Check(host.DiagramLayer.Routes["transfer"].Points.Contains(new HmiPoint(320, 150)), "The captured drag did not preview its new route.");
            Check(controls.SequenceEqual(host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>()), "Route preview rebuilt equipment visuals.");
            // Viewport-only notifications must not overwrite the detached route preview.
            host.WorkspaceCanvas.NotifyCanvasModified();
            Check(host.IsEditingRoute && host.DiagramLayer.Routes["transfer"].Points.Contains(new HmiPoint(320, 150)), "Viewport notification discarded the detached preview.");
            InputSystem.InjectMouseUp(MouseButton.Left);
            Check(!host.IsEditingRoute && Pins()[0] == new HmiPoint(320, 150), "Pointer release did not commit the final waypoint position.");
            host.Session.Undo();
            Check(host.Session.ExportJson() == beforeDrag, "Waypoint drag was not exactly one undo transaction.");
            host.Session.Redo(); window.Render(0);
            string committed = host.Session.ExportJson();
            var committedPath = host.DiagramLayer.Routes["transfer"].Points.ToArray();
            Press(new(320, 150)); Move(new(360, 100));
            InputSystem.InjectKeyDown(Key.Escape); InputSystem.InjectKeyUp(Key.Escape);
            Check(!host.IsEditingRoute && host.Session.ExportJson() == committed, "Escape did not retire the captured preview.");
            InputSystem.InjectMouseUp(MouseButton.Left);
            Check(host.DiagramLayer.Routes["transfer"].Points.SequenceEqual(committedPath), "Escape failed to restore the committed route.");
            Press(new(320, 150)); Move(new(400, 110));
            InputSystem.ReleasePointerCaptures(host.WorkspaceCanvas);
            Check(!host.IsEditingRoute && host.Session.ExportJson() == committed, "Native capture loss must cancel, not commit.");
            InputSystem.InjectMouseUp(MouseButton.Left);
            // A blocked point must remain a recoverable, visible handle above equipment.
            var a = host.Session.GetProject().Screens[0].Elements.Single(e => e.Id == "a");
            var bounds = HmiPortLayout.GetBounds(a);
            var blocked = new HmiPoint(MathF.Round((bounds.Left + bounds.Right) / 20) * 10, MathF.Round((bounds.Top + bounds.Bottom) / 20) * 10);
            host.MoveRouteWaypoint(0, blocked); window.Render(0);
            Check(host.DiagramLayer.Routes["transfer"].Status == HmiRouteStatus.BlockedWaypoint, "A blocked pin was silently moved around the obstacle.");
            window.SaveScreenshot(Path.Combine(outputDirectory, "waypoint-blocked-" + scheme.ToString().ToLowerInvariant() + ".png"));
            Press(blocked); Check(host.IsEditingRoute, "Equipment intercepted the blocked waypoint's recovery handle.");
            Move(new(320, 150)); InputSystem.InjectMouseUp(MouseButton.Left);
            Check(host.DiagramLayer.Routes["transfer"].Status == HmiRouteStatus.Success, "Dragging a blocked pin back to free space did not restore routing.");
            host.SelectRouteWaypoint(-1); window.Render(0);
            var unselectedPixels = window.ReadPixels();
            host.SelectRouteWaypoint(1); window.Render(0); window.Render(0);
            var selectedPixels = window.ReadPixels();
            var handle = Screen(new(570, 130)); int changedPixels = 0;
            for (int y = (int)handle.Y - 12; y <= (int)handle.Y + 12; y++)
                for (int x = (int)handle.X - 12; x <= (int)handle.X + 12; x++)
                {
                    int offset = (y * 1600 + x) * 4;
                    if (!unselectedPixels.AsSpan(offset, 4).SequenceEqual(selectedPixels.AsSpan(offset, 4))) changedPixels++;
                }
            Check(changedPixels >= 8, "Selected pin geometry was not painted at its real transformed handle position.");
            InputSystem.InjectKeyDown(Key.Right); InputSystem.InjectKeyUp(Key.Right);
            Check(Pins()[1] == new HmiPoint(571, 130), "Arrow keys did not nudge the focused waypoint.");
            host.Session.Undo(); host.SelectRouteWaypoint(1); window.Render(0);
            window.SaveScreenshot(Path.Combine(outputDirectory, "waypoints-" + scheme.ToString().ToLowerInvariant() + ".png"));
            host.Session.Undo(); host.Session.Undo(); host.Session.Undo(); host.Session.Undo(); host.Session.Undo();
            Check(host.Session.ExportJson() == initial, "Gesture history did not return to the original unpinned diagram.");
            Console.WriteLine($"{scheme}: actual waypoint placement, captured preview/release, Escape, capture-loss rollback, blocked-pin recovery and exact history passed.");
        }
        finally { InputSystem.Current = new WindowInputState(); window.Content = null; }

        List<HmiPoint> Pins() => host.Session.GetProject().Screens[0].Links[0].Waypoints;
        Vector2 Screen(HmiPoint point) => host.WorkspaceCanvas.DesignSurface.TransformToVisual(host).TransformPoint(new(point.X, point.Y));
        void Move(HmiPoint point) => InputSystem.InjectMouseMove(Screen(point));
        void Press(HmiPoint point) { Move(point); InputSystem.InjectMouseDown(MouseButton.Left); }
        void Click(HmiPoint point) { Press(point); InputSystem.InjectMouseUp(MouseButton.Left); }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
