using System.Numerics;
using System.Security.Cryptography;
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

internal static class HmiGraphicConventionsProbe
{
    internal static void Run(HeadlessWindow window, TtfFont font, string output, HmiColorScheme scheme)
    {
        string suffix = scheme.ToString().ToLowerInvariant();
        var project = HmiConventionsProject.Create();
        window.Resize(1536, 1024);
        var runtime = new HmiRuntime(project, DateTimeOffset.UnixEpoch);
        using (var view = new HmiScreenView(project, runtime, font: font) { ColorScheme = scheme })
        {
            window.Content = view; window.Render(0); window.Render(0);
            window.SaveScreenshot(Path.Combine(output, "conventions-" + suffix + ".png"));
            var pixels = window.ReadPixels();
            // Only the glyph, never captions, makes the neutral profile test pass.
            string GlyphHash(int profile) => Convert.ToHexString(SHA256.HashData(Crop(pixels, 1536, 32 + profile * 506, 274, 200, 180)));
            if (GlyphHash(0) == GlyphHash(1) || GlyphHash(1) == GlyphHash(2)) throw new InvalidOperationException("Graphic profiles did not change actual equipment ink.");
            var pump = view.Controls.First(c => c.Symbol == HmiSymbol.Pump && c.Appearance.GraphicStyle == HmiGraphicStyle.HighPerformance);
            var origin = pump.TransformToVisual(view).TransformPoint(Vector2.Zero);
            var before = Crop(pixels, 1536, (int)origin.X, (int)origin.Y, (int)pump.Width, (int)pump.Height);
            runtime.Publish(new Dictionary<string, HmiTagSample> { ["Demo.Trip"] = new(HmiValue.From(true), HmiQuality.Good, runtime.Now) }, runtime.Now);
            window.Render(0); window.Render(0);
            var fault = Crop(window.ReadPixels(), 1536, (int)origin.X, (int)origin.Y, (int)pump.Width, (int)pump.Height);
            if (before.SequenceEqual(fault)) throw new InvalidOperationException("High-performance convention suppressed the actual fault state.");
            window.SaveScreenshot(Path.Combine(output, "conventions-fault-" + suffix + ".png"));
            window.Content = null;
        }

        window.Resize(1600, 1000);
        using (var host = new HmiDesignerHost(project, font) { ColorScheme = scheme })
        {
            window.Content = host; window.Render(0); host.SetDataPanelsVisible(false); window.Render(0); host.Fit(); window.Render(0);
            InputSystem.Current = new WindowInputState { Root = host };
            var pump = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().First(c => c.Symbol == HmiSymbol.Pump);
            var bodyPoint = pump.TransformToVisual(host).TransformPoint(pump.Size / 2);
            InputSystem.InjectMouseMove(bodyPoint); InputSystem.InjectMouseDown(MouseButton.Left); InputSystem.InjectMouseUp(MouseButton.Left);
            window.Render(0);
            string original = host.Session.ExportJson(); long routes = host.DiagramLayer.RoutingPasses;
            InputSystem.InjectKeyDown(Key.F2); InputSystem.InjectKeyUp(Key.F2);
            if (!host.IsEditingLabel || InputSystem.FocusedElement is not DesignerInlineTextEditor)
                throw new InvalidOperationException("F2 did not start native in-place caption editing.");
            InputSystem.InjectTextInput(TextInputEventKind.InsertText, "P-101 / Feed transfer α");
            window.Render(0); window.Render(0);
            if (host.Session.ExportJson() != original || pump.Label != "P-101 / Feed transfer α" || host.DiagramLayer.RoutingPasses != routes)
                throw new InvalidOperationException("Caption preview changed the document, geometry or wrong visual.");
            window.SaveScreenshot(Path.Combine(output, "inline-edit-" + suffix + ".png"));
            InputSystem.InjectKeyDown(Key.Enter); InputSystem.InjectKeyUp(Key.Enter);
            window.Render(0);
            if (host.IsEditingLabel || host.Session.GetProject().Screens[0].Elements.Single(e => e.Id == pump.ElementId).Label != pump.Label)
                throw new InvalidOperationException("Native Enter did not commit the visible caption.");
            InputSystem.InjectKeyDown(Key.ControlLeft); InputSystem.InjectKeyDown(Key.Z); InputSystem.InjectKeyUp(Key.Z); InputSystem.InjectKeyUp(Key.ControlLeft);
            window.Render(0);
            if (host.Session.ExportJson() != original) throw new InvalidOperationException("Caption undo did not restore the exact original document.");
            // Two real clicks on the actual transformed caption, not a direct method call.
            var b = pump.CaptionBounds;
            var point = pump.TransformToVisual(host).TransformPoint(new Vector2(b.X + b.Width * .5f, b.Y + b.Height * .5f));
            for (int click = 0; click < 2; click++)
            { InputSystem.InjectMouseMove(point); InputSystem.InjectMouseDown(MouseButton.Left); InputSystem.InjectMouseUp(MouseButton.Left); }
            if (!host.IsEditingLabel) throw new InvalidOperationException("Double-click missed the transformed caption area.");
            InputSystem.InjectTextInput(TextInputEventKind.InsertText, "Canceled draft");
            InputSystem.InjectKeyDown(Key.Escape); InputSystem.InjectKeyUp(Key.Escape);
            if (host.IsEditingLabel || host.Session.ExportJson() != original) throw new InvalidOperationException("Native Escape did not cancel caption editing.");
            host.SetGraphicStyle(HmiGraphicStyle.HighPerformance); window.Render(0); window.Render(0);
            window.SaveScreenshot(Path.Combine(output, "format-inspector-" + suffix + ".png"));
            host.Session.Undo();
            InputSystem.Current = new WindowInputState(); window.Content = null;
        }
        VerifyDesignRuntimePixels(window, font, scheme, output);
        Console.WriteLine($"{scheme}: profile/fault pixels, F2/Unicode/Enter, real double-click/Escape, contextual formatting and design/runtime pixel parity passed.");
    }

    private static void VerifyDesignRuntimePixels(HeadlessWindow window, TtfFont font, HmiColorScheme scheme, string output)
    {
        var project = new HmiProject { StartScreenId = "overview", Tags = [new() { Name = "Trip", Type = HmiTagType.Boolean, InitialValue = HmiValue.From(true) }],
            Screens = [new() { Id = "overview", Width = 900, Height = 700, Elements = [new() { Id = "pump", Symbol = HmiSymbol.Pump, Label = "P-100 / WYSIWYG", X = 100, Y = 120, Width = 260, Height = 240,
                Appearance = new() { GraphicStyle = HmiGraphicStyle.HighPerformance, CaptionFontSize = 18, CaptionAlignment = HmiCaptionAlignment.Center },
                States = [new() { Tag = "Trip", Condition = HmiStateCondition.IsTrue, Tone = HmiVisualTone.Fault, Text = "TRIPPED" }] }] }] };
        byte[] design;
        window.Resize(1600, 1000);
        using (var host = new HmiDesignerHost(project, font) { ColorScheme = scheme })
        {
            window.Content = host; window.Render(0);
            host.WorkspaceCanvas.ShowGridLines = false;
            host.WorkspaceCanvas.ZoomScale = 1; host.WorkspaceCanvas.PanOffset = Vector2.Zero; host.WorkspaceCanvas.ApplyTransforms();
            window.Render(0); window.Render(0);
            var control = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Single();
            var at = control.TransformToVisual(host).TransformPoint(Vector2.Zero);
            design = Crop(window.ReadPixels(), 1600, (int)at.X, (int)at.Y, 260, 240);
            Console.WriteLine($"Design comparison origin={at} size={control.Size} caption={control.CaptionBounds} tone={control.VisualTone}");
            PngEncoder.SavePng(Path.Combine(output, "parity-design-" + scheme.ToString().ToLowerInvariant() + ".png"), design, 260, 240);
            window.Content = null;
        }
        var runtime = new HmiRuntime(project, DateTimeOffset.UnixEpoch);
        using var view = new HmiScreenView(project, runtime, font: font) { ColorScheme = scheme };
        window.Content = view; window.Render(0); window.Render(0);
        var liveControl = view.Controls.Single();
        var position = liveControl.TransformToVisual(view).TransformPoint(Vector2.Zero);
        var live = Crop(window.ReadPixels(), 1600, (int)position.X, (int)position.Y, 260, 240);
        Console.WriteLine($"Runtime comparison origin={position} size={liveControl.Size} caption={liveControl.CaptionBounds} tone={liveControl.VisualTone}");
        PngEncoder.SavePng(Path.Combine(output, "parity-runtime-" + scheme.ToString().ToLowerInvariant() + ".png"), live, 260, 240);
        if (!live.SequenceEqual(design))
        {
            int differing = live.Zip(design).Count(p => p.First != p.Second);
            throw new InvalidOperationException($"Design and runtime disagree on identical caption, state or graphics ({differing} bytes).");
        }
        window.Content = null;
    }

    private static byte[] Crop(byte[] pixels, int stride, int x, int y, int width, int height)
    {
        var result = new byte[checked(width * height * 4)];
        for (int row = 0; row < height; row++) pixels.AsSpan(((y + row) * stride + x) * 4, width * 4).CopyTo(result.AsSpan(row * width * 4));
        return result;
    }
}
