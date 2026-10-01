using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.WinUI.Designer;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Silk.NET.Input;
using Button = Microsoft.UI.Xaml.Controls.Button;

internal static class HmiCanvasAuthoringProbe
{
    internal static void Run(HeadlessWindow window, TtfFont font, string output, HmiColorScheme scheme)
    {
        string suffix = scheme.ToString().ToLowerInvariant();
        var project = new HmiProject { Name = "Direct authoring / process equipment", Screens = [new() {
            Id = "overview", Name = "Assembly workbench", Width = 1280, Height = 820, Elements = [
                new() { Id = "heading", Symbol = HmiSymbol.Label, Label = "PROCESS EQUIPMENT / DIRECT AUTHORING", X = 40, Y = 30, Width = 1100, Height = 50 },
                new() { Id = "tank", Symbol = HmiSymbol.Tank, Label = "TK-101 / Buffer vessel", X = 80, Y = 150, Width = 230, Height = 310 },
                new() { Id = "pump", Symbol = HmiSymbol.Pump, Label = "P-101 / Feed pump", X = 400, Y = 235, Width = 210, Height = 200 },
                new() { Id = "gauge", Symbol = HmiSymbol.Gauge, Label = "PT-201 / Header pressure", X = 860, Y = 150, Width = 270, Height = 225, Unit = "bar", Maximum = 10 }
            ] }] };
        window.Resize(1600, 1000);
        using var host = new HmiDesignerHost(project, font) { ColorScheme = scheme };
        window.Content = host;
        Frame(); host.SetDataPanelsVisible(false); Frame(); host.Fit(); Frame();
        InputSystem.Current = new WindowInputState { Root = host };
        var canvas = host.WorkspaceCanvas;
        string original = host.Session.ExportJson();
        long routingPasses = host.DiagramLayer.RoutingPasses;
        int originalCount = project.Screens[0].Elements.Count;
        var originals = canvas.DesignSurface.Children.OfType<HmiControl>().ToArray();

        host.BeginComponentPlacement(HmiSymbol.HeatExchanger);
        Move(670, 460); InputSystem.InjectMouseDown(MouseButton.Left);
        Move(1050, 730); Frame();
        var ghost = canvas.AdornerSurface.Children.OfType<HmiControl>().Single();
        if (ghost.Symbol != HmiSymbol.HeatExchanger || ghost.IsHitTestVisible || ghost.CommandsEnabled || !host.IsPlacingComponent)
            throw new InvalidOperationException("Draw-to-size did not expose the actual noninteractive component.");
        if (host.Session.ExportJson() != original || host.Session.CanUndo || !originals.SequenceEqual(canvas.DesignSurface.Children.OfType<HmiControl>()) || host.DiagramLayer.RoutingPasses != routingPasses)
            throw new InvalidOperationException("Placement preview modified the document, equipment identities, routes or history.");
        window.SaveScreenshot(Path.Combine(output, "placement-preview-" + suffix + ".png"));
        var bounds = host.AuthoringBounds!.Value;
        InputSystem.InjectMouseUp(MouseButton.Left); Frame();
        var inserted = host.Session.GetProject().Screens[0].Elements[^1];
        if (host.IsPlacingComponent || inserted.Symbol != HmiSymbol.HeatExchanger || !bounds.Equals(new Rect(inserted.X, inserted.Y, inserted.Width, inserted.Height)) ||
            canvas.DesignSurface.Children.OfType<HmiControl>().Count() != originalCount + 1)
            throw new InvalidOperationException("Pointer release did not commit the exact preview geometry.");
        window.SaveScreenshot(Path.Combine(output, "placement-completed-" + suffix + ".png"));
        host.Session.Undo(); Frame();
        if (host.Session.ExportJson() != original || host.Session.CanUndo) throw new InvalidOperationException("Draw-to-size was not a single undo transaction.");

        ClearSelection();
        Move(50, 120); InputSystem.InjectMouseDown(MouseButton.Left); Move(650, 490); Frame();
        if (!host.IsSelectingArea || host.Selection.Selection.Count != 0 || host.Session.ExportJson() != original)
            throw new InvalidOperationException("Window-selection preview changed the real selection or document.");
        window.SaveScreenshot(Path.Combine(output, "marquee-window-" + suffix + ".png"));
        InputSystem.InjectMouseUp(MouseButton.Left); Frame();
        VerifySelection("tank", "pump");

        ClearSelection();
        Move(660, 470); InputSystem.InjectMouseDown(MouseButton.Left); Move(500, 310); Frame();
        if (!host.IsSelectingArea) throw new InvalidOperationException("Empty-canvas crossing selection did not capture its pointer.");
        window.SaveScreenshot(Path.Combine(output, "marquee-crossing-" + suffix + ".png"));
        InputSystem.InjectMouseUp(MouseButton.Left); Frame(); VerifySelection("pump");
        if (host.Session.ExportJson() != original || host.Session.CanUndo) throw new InvalidOperationException("Marquee selection entered project history.");

        // Shift adds; Ctrl toggles. Modifier meaning is captured at the start of the selection gesture.
        InputSystem.InjectKeyDown(Key.ShiftLeft);
        Move(50, 120); InputSystem.InjectMouseDown(MouseButton.Left); Move(330, 480); InputSystem.InjectMouseUp(MouseButton.Left);
        InputSystem.InjectKeyUp(Key.ShiftLeft); Frame(); VerifySelection("pump", "tank");
        InputSystem.InjectKeyDown(Key.ControlLeft);
        Move(50, 120); InputSystem.InjectMouseDown(MouseButton.Left); Move(330, 480); InputSystem.InjectMouseUp(MouseButton.Left);
        InputSystem.InjectKeyUp(Key.ControlLeft); Frame(); VerifySelection("pump");

        // Escape preserves the prior selection, and external capture loss cannot commit a ghost.
        Move(50, 120); InputSystem.InjectMouseDown(MouseButton.Left); Move(650, 490);
        InputSystem.InjectKeyDown(Key.Escape); InputSystem.InjectKeyUp(Key.Escape); InputSystem.InjectMouseUp(MouseButton.Left); Frame();
        VerifySelection("pump");
        host.BeginComponentPlacement(HmiSymbol.Filter);
        Move(670, 460); InputSystem.InjectMouseDown(MouseButton.Left); Move(1050, 730);
        canvas.ReleasePointerCaptures(); InputSystem.InjectMouseUp(MouseButton.Left); Frame();
        if (host.IsPlacingComponent || host.Session.ExportJson() != original || canvas.AdornerSurface.Children.OfType<HmiControl>().Any())
            throw new InvalidOperationException("Capture loss left or committed a detached placement preview.");

        // A chorded secondary button cancels; releasing it cannot masquerade as primary completion.
        host.BeginComponentPlacement(HmiSymbol.Filter);
        Move(670, 460); InputSystem.InjectMouseDown(MouseButton.Left); Move(900, 650);
        InputSystem.InjectMouseDown(MouseButton.Right); InputSystem.InjectMouseUp(MouseButton.Right); InputSystem.InjectMouseUp(MouseButton.Left); Frame();
        if (host.IsPlacingComponent || host.Session.ExportJson() != original)
            throw new InvalidOperationException("Secondary-button input completed a primary placement gesture.");

        // Real clicks on the library's draw button arm the tool; this is not a direct host API surrogate.
        var drawButton = Descendants(host).OfType<Button>().First(b => b.Name == "HmiDrawInstrumentBubble");
        var buttonPoint = drawButton.TransformToVisual(host).TransformPoint(drawButton.Size / 2);
        InputSystem.InjectMouseMove(buttonPoint); InputSystem.InjectMouseDown(MouseButton.Left); InputSystem.InjectMouseUp(MouseButton.Left);
        if (host.PlacementSymbol != HmiSymbol.InstrumentBubble) throw new InvalidOperationException("Library draw-button activation did not arm component placement.");
        InputSystem.InjectKeyDown(Key.Escape); InputSystem.InjectKeyUp(Key.Escape);
        if (host.IsPlacingComponent) throw new InvalidOperationException("Escape did not retire the armed palette tool.");
        // The neighboring + affordance must remain inside the pane and respond to actual input.
        var insertButton = Descendants(host).OfType<Button>().First(b => b.Name == "HmiInsertInstrumentBubble");
        var insertPoint = insertButton.TransformToVisual(host).TransformPoint(insertButton.Size / 2);
        if (insertPoint.X >= canvas.TransformToVisual(host).TransformPoint(Vector2.Zero).X)
            throw new InvalidOperationException("Library insertion chrome overflows its pane.");
        InputSystem.InjectMouseMove(insertPoint); InputSystem.InjectMouseDown(MouseButton.Left); InputSystem.InjectMouseUp(MouseButton.Left);
        if (host.Session.GetProject().Screens[0].Elements.Count != originalCount + 1 || host.Session.GetProject().Screens[0].Elements[^1].Symbol != HmiSymbol.InstrumentBubble)
            throw new InvalidOperationException("The visible library + button did not insert its component.");
        host.Session.Undo();
        if (host.Session.ExportJson() != original) throw new InvalidOperationException("Library insertion did not undo exactly.");
        InputSystem.Current = new WindowInputState(); window.Content = null;
        Console.WriteLine($"{scheme}: real draw-tool activation, captured component previews, exact one-step placement, window/crossing selection, modifiers, Escape, capture loss and secondary-button cancellation passed.");

        void Frame() { window.Render(0); window.Render(0); }
        void Move(float x, float y)
        {
            var viewportPoint = new Vector2(x, y) * canvas.ZoomScale + canvas.PanOffset;
            InputSystem.InjectMouseMove(canvas.TransformToVisual(host).TransformPoint(viewportPoint));
        }
        void ClearSelection() { canvas.SelectElement(null); host.Selection.SelectRange([]); Frame(); }
        void VerifySelection(params string[] expected)
        {
            var actual = host.Selection.Selection.OfType<HmiControl>().Select(c => c.ElementId).ToArray();
            if (!actual.SequenceEqual(expected)) throw new InvalidOperationException($"Selection differs: expected {string.Join(',', expected)}, actual {string.Join(',', actual)}.");
        }
    }
    private static IEnumerable<Visual> Descendants(Visual visual)
    {
        yield return visual;
        if (visual is ContainerVisual container)
            foreach (var child in container.Children)
                foreach (var nested in Descendants(child)) yield return nested;
    }
}
