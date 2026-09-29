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

if (args.Length is < 2 or > 3) throw new ArgumentException("Usage: ProGPU.Hmi.VisualSmoke <font-file> <output-directory> [Light|Dark|HighContrast]");
var schemes = new[] { args.Length == 3 ? Enum.Parse<HmiColorScheme>(args[2]) : HmiColorScheme.Light };
var elapsed = System.Diagnostics.Stopwatch.StartNew();
if (JsonSerializer.IsReflectionEnabledByDefault) throw new InvalidOperationException("JSON reflection must remain disabled.");
var font = new TtfFont(args[0]);
PopupService.DefaultFont = font;
Directory.CreateDirectory(args[1]);
using var window = new HeadlessWindow(1440, 1120);
var at = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
foreach (var scheme in schemes)
{
    var canvas = new Canvas { Width = 1440, Height = 1120, Background = HmiThemeResources.GetBrush(scheme, HmiBrushRole.Workspace) };
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
window.Resize(1440, 600);
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
    InputSystem.Current = new WindowInputState();
    window.Content = null;
    Console.WriteLine($"{scheme}: fitted runtime command, overlap selection and zoom-to-selection passed at {elapsed.Elapsed}.");
}
Console.WriteLine($"PASS: actual ProGPU component, designer and runtime readback with reflection JSON disabled ({elapsed.Elapsed}).");

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
