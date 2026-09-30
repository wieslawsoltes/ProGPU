using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Silk.NET.Input;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;

if (args.Length is < 2 or > 4) throw new ArgumentException("Usage: ProGPU.Hmi.VisualSmoke <font-file> <output-directory> [Light|Dark|HighContrast] [--graphics-only]");
var schemes = new[] { args.Length >= 3 ? Enum.Parse<HmiColorScheme>(args[2]) : HmiColorScheme.Light };
var elapsed = System.Diagnostics.Stopwatch.StartNew();
if (JsonSerializer.IsReflectionEnabledByDefault) throw new InvalidOperationException("JSON reflection must remain disabled.");
var font = new TtfFont(args[0]);
PopupService.DefaultFont = font;
Directory.CreateDirectory(args[1]);
uint catalogHeight = (uint)(80 + ((HmiControlCatalog.Items.Count + 7) / 8) * 208);
using var window = new HeadlessWindow(1440, catalogHeight);
if (args.Length == 4)
{
    if (args[3] != "--graphics-only") throw new ArgumentException("Unknown visual probe selector.");
    foreach (var scheme in schemes) HmiGraphicConventionsProbe.Run(window, font, args[1], scheme);
    return;
}
var at = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
foreach (var scheme in schemes)
{
    var canvas = new Canvas { Width = 1440, Height = catalogHeight, Background = HmiThemeResources.GetBrush(scheme, HmiBrushRole.Workspace) };
    var title = new TextBlock { Text = "PROGPU HMI / EQUIPMENT LIBRARY", Font = font, FontSize = 22, Foreground = HmiThemeResources.GetBrush(scheme, HmiBrushRole.Text) };
    canvas.Children.Add(title); Canvas.SetLeft(title, 24); Canvas.SetTop(title, 16);
    int index = 0;
    foreach (var descriptor in HmiControlCatalog.Items)
    {
        var control = HmiControlCatalog.Create(descriptor.Symbol);
        var model = control.CaptureDefinition();
        model.Width = 166; model.Height = 197; model.X = 20 + (index % 8) * 177; model.Y = 66 + (index / 8) * 208;
        model.Label = descriptor.Name; model.Tag = "Plant." + descriptor.Symbol;
        control.ApplyDefinition(model); control.Font = font; control.ColorScheme = scheme;
        if (HmiSymbolTraits.IsBinary(descriptor.Symbol)) control.UpdateSample(new(HmiValue.From(true), HmiQuality.Good, at));
        else control.UpdateSample(new(HmiValue.From(62d), HmiQuality.Good, at));
        canvas.Children.Add(control); index++;
    }
    window.Content = canvas;
    window.Render(0); window.Render(0);
    window.SaveScreenshot(Path.Combine(args[1], "components-" + scheme.ToString().ToLowerInvariant() + ".png"));
    var pixels = window.ReadPixels();
    if (pixels.Distinct().Count() < 24) throw new InvalidOperationException("The component render is blank or unexpectedly flat.");
    Console.WriteLine($"{scheme}: {index} actual retained controls; SHA256 {Convert.ToHexString(SHA256.HashData(pixels))}");
}
// Test glyphs independently of captions and instrument-card backgrounds. A repeated
// generic picture with different text must not satisfy the equipment atlas check.
window.Resize(1440, (uint)(24 + ((HmiControlCatalog.Items.Count + 9) / 10) * 144));
foreach (var scheme in schemes)
{
    var atlas = new Canvas { Background = HmiThemeResources.GetBrush(scheme, HmiBrushRole.Workspace) };
    int index = 0;
    foreach (var descriptor in HmiControlCatalog.Items)
    {
        var icon = new HmiSymbolIcon { Symbol = descriptor.Symbol, ColorScheme = scheme, Width = 110, Height = 110 };
        Canvas.SetLeft(icon, 15 + (index % 10) * 144); Canvas.SetTop(icon, 15 + (index / 10) * 144);
        atlas.Children.Add(icon); index++;
    }
    window.Content = atlas; window.Render(0); window.Render(0);
    var pixels = window.ReadPixels();
    var background = pixels.AsSpan(0, 4).ToArray();
    var equipmentHashes = new HashSet<string>(StringComparer.Ordinal);
    index = 0;
    foreach (var descriptor in HmiControlCatalog.Items)
    {
        int x = 15 + (index % 10) * 144, y = 15 + (index / 10) * 144;
        var crop = Crop(pixels, 1440, x, y, 110, 110);
        int ink = 0;
        for (int i = 0; i < crop.Length; i += 4) if (!crop.AsSpan(i, 4).SequenceEqual(background)) ink++;
        if (ink < 20) throw new InvalidOperationException($"Missing glyph for {descriptor.Symbol} in {scheme}.");
        if (HmiSymbolTraits.IsEquipment(descriptor.Symbol) && !equipmentHashes.Add(Convert.ToHexString(SHA256.HashData(crop))))
            throw new InvalidOperationException($"Duplicate equipment glyph: {descriptor.Symbol} in {scheme}.");
        // Each thumbnail owns a bounded rectangle; its neighbouring whitespace must stay untouched.
        for (int i = -5; i < 115; i++)
        {
            CheckBackground(x + i, y - 5); CheckBackground(x + i, y + 114);
            CheckBackground(x - 5, y + i); CheckBackground(x + 114, y + i);
        }
        void CheckBackground(int px, int py)
        {
            if (!pixels.AsSpan((py * 1440 + px) * 4, 4).SequenceEqual(background))
                throw new InvalidOperationException($"Glyph escaped its owned region: {descriptor.Symbol}.");
        }
        index++;
    }
    window.SaveScreenshot(Path.Combine(args[1], "glyphs-" + scheme.ToString().ToLowerInvariant() + ".png"));
    Console.WriteLine($"{scheme}: {index} nonblank glyphs, {equipmentHashes.Count} distinct equipment silhouettes, bounded ink verified.");
}

// Quality and explicit appearance are rendered, not merely asserted in the model.
window.Resize(1280, 520);
foreach (var scheme in schemes)
{
    var states = new Canvas { Background = HmiThemeResources.GetBrush(scheme, HmiBrushRole.Workspace) };
    var definitions = new[] { ("Stopped", false, HmiQuality.Good, HmiVisualTone.Normal), ("Running", true, HmiQuality.Good, HmiVisualTone.Normal),
        ("Fault / trip", false, HmiQuality.Good, HmiVisualTone.Fault), ("Bad telemetry", true, HmiQuality.Bad, HmiVisualTone.Normal),
        ("Stale telemetry", true, HmiQuality.Stale, HmiVisualTone.Normal), ("Maintenance", false, HmiQuality.Good, HmiVisualTone.Maintenance) };
    int column = 0;
    foreach (var (label, running, quality, tone) in definitions)
    {
        var control = new HmiPump { Font = font, ColorScheme = scheme };
        control.ApplyDefinition(new HmiElement { Symbol = HmiSymbol.Pump, Label = label, Tag = "P-101", X = 10 + column * 210, Y = 10, Width = 195, Height = 225 });
        control.UpdateSample(new(HmiValue.From(running), quality, at));
        control.UpdateState(new HmiVisualState(tone, tone is HmiVisualTone.Fault or HmiVisualTone.Maintenance ? label.ToUpperInvariant() : ""));
        states.Children.Add(control); column++;
    }
    for (int turn = 0; turn < 4; turn++)
    {
        var valve = new HmiControlValve { Font = font, ColorScheme = scheme };
        valve.ApplyDefinition(new HmiElement { Symbol = HmiSymbol.ControlValve, Label = $"Valve / {turn * 90}°", X = 14 + turn * 260, Y = 264, Width = 240, Height = 236,
            Appearance = new HmiAppearance { QuarterTurns = turn, MirrorHorizontal = turn == 3 } });
        valve.UpdateSample(new(HmiValue.From(62d), HmiQuality.Good, at)); states.Children.Add(valve);
    }
    window.Content = states; window.Render(0); window.Render(0);
    var pixels = window.ReadPixels();
    // Remove label/text bands: the visual status itself must distinguish the conditions.
    string HashGlyph(int column) => Convert.ToHexString(SHA256.HashData(Crop(pixels, 1280, 10 + column * 210, 64, 195, 125)));
    if (HashGlyph(0) == HashGlyph(1) || HashGlyph(1) == HashGlyph(3) || HashGlyph(1) == HashGlyph(2))
        throw new InvalidOperationException("Equipment status/quality did not change actual glyph pixels.");
    window.SaveScreenshot(Path.Combine(args[1], "quality-" + scheme.ToString().ToLowerInvariant() + ".png"));
}
window.Resize(1600, 1000);
foreach (var scheme in schemes)
{
    Console.WriteLine($"{scheme}: designer/render/input probe starting at {elapsed.Elapsed}.");
    using var host = new HmiDesignerHost(HmiShowcaseProject.Create(), font) { ColorScheme = scheme };
    window.Content = host;
    window.Render(0); host.Fit(); window.Render(0); window.Render(0);
    window.SaveScreenshot(Path.Combine(args[1], "designer-" + scheme.ToString().ToLowerInvariant() + ".png"));
    host.SetDataPanelsVisible(false);
    window.Render(0); host.Fit(); window.Render(0);
    host.StartPreview(automaticTicks: false);
    for (int i = 0; i < 100; i++) host.AdvancePreview(TimeSpan.FromMilliseconds(100));
    window.Render(0); window.Render(0);
    window.SaveScreenshot(Path.Combine(args[1], "runtime-" + scheme.ToString().ToLowerInvariant() + ".png"));
    // Route actual pointer events through the fitted view's native transform chain.
    InputSystem.Current = new WindowInputState { Root = host };
    var runtimeView = Descendants(host).OfType<HmiScreenView>().Single();
    var toggle = runtimeView.Controls.Single(c => c.Symbol == HmiSymbol.ToggleSwitch);
    bool previous = host.Runtime!.Read("Pump.Running").Value.Boolean;
    string document = host.Session.ExportJson();
    var point = toggle.TransformToVisual(host).TransformPoint(toggle.Size / 2);
    InputSystem.InjectMouseMove(point); InputSystem.InjectMouseDown(MouseButton.Left); InputSystem.InjectMouseUp(MouseButton.Left);
    if (host.Runtime.Read("Pump.Running").Value.Boolean == previous)
        throw new InvalidOperationException("Fitted runtime pointer activation did not reach the command overlay.");
    if (host.Session.ExportJson() != document) throw new InvalidOperationException("Runtime interaction edited the design document.");
    host.StopPreview(); window.Render(0);
    var pump = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Single(c => c.Symbol == HmiSymbol.Pump);
    point = pump.TransformToVisual(host).TransformPoint(pump.Size / 2);
    InputSystem.InjectMouseMove(point); InputSystem.InjectMouseDown(MouseButton.Left); InputSystem.InjectMouseUp(MouseButton.Left);
    if (host.WorkspaceCanvas.SelectedElement is not HmiControl selected || selected.ElementId != pump.ElementId)
        throw new InvalidOperationException("The shared designer's geometric hit test missed the transformed equipment; selected=" + host.WorkspaceCanvas.SelectedElement?.GetType().Name + " / " + (host.WorkspaceCanvas.SelectedElement as HmiControl)?.Label);
    host.ZoomToSelection(); window.Render(0);
    window.SaveScreenshot(Path.Combine(args[1], "selection-" + scheme.ToString().ToLowerInvariant() + ".png"));
    host.Fit(); window.Render(0);
    string beforeConnection = host.Session.ExportJson();
    int originalLinks = host.Session.GetProject().Screens[0].Links.Count;
    if (originalLinks != 3 || host.DiagramLayer.Routes.Values.Any(r => r.Status != HmiRouteStatus.Success))
        throw new InvalidOperationException("The showcase semantic connections did not route.");
    var screen = host.Session.GetProject().Screens[0];
    var from = screen.Elements.Single(e => e.Symbol == HmiSymbol.Filter);
    var to = screen.Elements.Single(e => e.Symbol == HmiSymbol.Tank);
    host.BeginDiagramConnection(); window.Render(0);
    void ClickDocument(HmiPoint logical)
    {
        var target = host.WorkspaceCanvas.DesignSurface.TransformToVisual(host).TransformPoint(new Vector2(logical.X, logical.Y));
        InputSystem.InjectMouseMove(target); InputSystem.InjectMouseDown(MouseButton.Left); InputSystem.InjectMouseUp(MouseButton.Left);
    }
    ClickDocument(HmiPortLayout.Resolve(from, "outlet").Point);
    if (!host.IsConnectingDiagram || host.Session.GetProject().Screens[0].Links.Count != originalLinks)
        throw new InvalidOperationException("First nozzle click must remain an uncommitted authoring gesture.");
    window.Render(0);
    ClickDocument(HmiPortLayout.Resolve(to, "inlet").Point);
    if (host.IsConnectingDiagram || host.Session.GetProject().Screens[0].Links.Count != originalLinks + 1 || host.SelectedLinkId == null)
        throw new InvalidOperationException("The actual two-nozzle pointer gesture did not commit its diagram connection.");
    string created = host.SelectedLinkId;
    var createdRoute = host.DiagramLayer.Routes[created];
    if (createdRoute.Status != HmiRouteStatus.Success) throw new InvalidOperationException(createdRoute.Diagnostic);
    host.SelectDiagramLink(null); window.Render(0);
    bool picked = false;
    for (int i = 1; i < createdRoute.Points.Count; i++)
    {
        var first = createdRoute.Points[i - 1]; var last = createdRoute.Points[i];
        var middle = new HmiPoint((first.X + last.X) / 2, (first.Y + last.Y) / 2);
        if (host.DiagramLayer.HitLink(middle, 3) != created) continue;
        ClickDocument(middle); picked = host.SelectedLinkId == created;
        if (picked) break;
    }
    if (!picked) throw new InvalidOperationException("Actual pointer picking did not select the routed link.");
    host.SetDataPanelsVisible(true);
    var tabs = Descendants(host).OfType<Pivot>().Single(p => p.Items.Any(i => i.Header?.ToString() == "Diagram"));
    tabs.SelectedIndex = tabs.Items.ToList().FindIndex(i => i.Header?.ToString() == "Diagram");
    window.Render(1); host.Fit(); window.Render(0);
    window.SaveScreenshot(Path.Combine(args[1], "diagram-" + scheme.ToString().ToLowerInvariant() + ".png"));
    host.Session.Undo();
    if (host.Session.ExportJson() != beforeConnection) throw new InvalidOperationException("Undo did not restore the pre-gesture diagram and equipment.");
    Console.WriteLine($"{scheme}: semantic nozzle creation, rendered link selection and atomic undo passed.");
    InputSystem.Current = new WindowInputState();
    window.Content = null;
    Console.WriteLine($"{scheme}: fitted runtime command, overlap selection and zoom-to-selection passed at {elapsed.Elapsed}.");
}
// Independent feedback/style/failure atlas: actual runtime views, not renderer stubs.
window.Resize(1600, 720);
foreach (var scheme in schemes)
{
    var sheet = new Canvas { Background = HmiThemeResources.GetBrush(scheme, HmiBrushRole.Workspace) };
    var examples = new[] {
        ("Process / running", HmiLinkKind.Process, HmiQuality.Good, true),
        ("Signal / telemetry", HmiLinkKind.Signal, HmiQuality.Good, true),
        ("Electrical / state", HmiLinkKind.Electrical, HmiQuality.Good, true),
        ("Stopped / false", HmiLinkKind.Process, HmiQuality.Good, false),
        ("Unknown / bad feedback", HmiLinkKind.Process, HmiQuality.Bad, true),
        ("Blocked / overlapping equipment", HmiLinkKind.Process, HmiQuality.Good, true) };
    var views = new List<HmiScreenView>();
    int index = 0;
    foreach (var (name, kind, quality, active) in examples)
    {
        var project = new HmiProject { StartScreenId = "overview", Tags = [new() { Name = "Running", Type = HmiTagType.Boolean, InitialValue = HmiValue.From(active) }],
            Screens = [new() { Id = "overview", Width = 510, Height = 310, Elements = [
                new() { Id = "valve", Symbol = HmiSymbol.Valve, Label = "XV-101", X = 24, Y = 65, Width = 125, Height = 190, Tag = "Running" },
                new() { Id = "pump", Symbol = HmiSymbol.Pump, Label = "P-201", X = index == 5 ? 115 : 355, Y = 65, Width = 125, Height = 190, Tag = "Running" }],
                Links = [new() { Id = "line", Source = new() { ElementId = "valve", PortId = "outlet" }, Target = new() { ElementId = "pump", PortId = "inlet" }, ActivityTag = "Running", Kind = kind }] }] };
        if (index == 2) foreach (var element in project.Screens[0].Elements) element.Appearance.Presentation = HmiPresentation.Card;
        var runtime = new HmiRuntime(project, at);
        if (quality != HmiQuality.Good) runtime.Publish(new Dictionary<string, HmiTagSample> { ["Running"] = new(HmiValue.From(active), quality, at) }, at);
        var view = new HmiScreenView(project, runtime, font: font) { ColorScheme = scheme };
        var label = new TextBlock { Text = name, Font = font, FontSize = 16, Foreground = HmiThemeResources.GetBrush(scheme, HmiBrushRole.Text) };
        Canvas.SetLeft(label, 26 + index % 3 * 530); Canvas.SetTop(label, 16 + index / 3 * 350); sheet.Children.Add(label);
        Canvas.SetLeft(view, 12 + index % 3 * 530); Canvas.SetTop(view, 48 + index / 3 * 350); sheet.Children.Add(view); views.Add(view);
        if (index < 5 && view.DiagramLayer.Routes["line"].Status != HmiRouteStatus.Success)
            throw new InvalidOperationException("A feedback/style fixture did not route.");
        if (index == 5 && view.DiagramLayer.Routes["line"].Status != HmiRouteStatus.BlockedTerminal)
            throw new InvalidOperationException("Overlapping equipment was misleadingly rendered as a routed connection.");
        index++;
    }
    window.Content = sheet; window.Render(0); window.Render(0);
    var pixels = window.ReadPixels();
    // Crop only the inter-equipment line region; captions/equipment must not make these comparisons pass.
    string LinkHash(int tile) => Convert.ToHexString(SHA256.HashData(Crop(pixels, 1600, 192 + tile % 3 * 530, 150 + tile / 3 * 350, 120, 95)));
    if (LinkHash(0) == LinkHash(3) || LinkHash(0) == LinkHash(4) || LinkHash(0) == LinkHash(1) || LinkHash(1) == LinkHash(2))
        throw new InvalidOperationException("Line style or quality did not change the actual connection pixels.");
    window.SaveScreenshot(Path.Combine(args[1], "links-" + scheme.ToString().ToLowerInvariant() + ".png"));
    foreach (var view in views) view.Dispose();
    window.Content = null;
    Console.WriteLine($"{scheme}: independent line style, active/stopped/unknown pixels and blocked terminal checks passed.");
}
foreach (var scheme in schemes) HmiGraphicConventionsProbe.Run(window, font, args[1], scheme);
foreach (var scheme in schemes) HmiRouteEditingProbe.Run(window, font, args[1], scheme);
Console.WriteLine($"PASS: actual ProGPU component, semantic diagram, designer and runtime readback with reflection JSON disabled ({elapsed.Elapsed}).");

static IEnumerable<Visual> Descendants(Visual visual)
{
    yield return visual;
    if (visual is ContainerVisual container)
        foreach (var child in container.Children)
            foreach (var descendant in Descendants(child)) yield return descendant;
}

static byte[] Crop(byte[] pixels, int stride, int x, int y, int width, int height)
{
    var result = new byte[checked(width * height * 4)];
    for (int row = 0; row < height; row++)
        pixels.AsSpan(((y + row) * stride + x) * 4, width * 4).CopyTo(result.AsSpan(row * width * 4));
    return result;
}
