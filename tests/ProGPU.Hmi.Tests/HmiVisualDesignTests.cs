using System.Numerics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;
using ProGPU.WinUI.Designer;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiVisualDesignTests
{
    public static IEnumerable<object[]> Symbols => Enum.GetValues<HmiSymbol>().Select(symbol => new object[] { symbol });

    [Theory]
    [MemberData(nameof(Symbols))]
    public void EverySymbolRetainsAppearanceThroughGeneratedJsonAndCloning(HmiSymbol symbol)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var project = HmiDemoProject.Create();
        var element = new HmiElement
        {
            Symbol = symbol,
            Appearance = new() { Presentation = HmiPresentation.Process, QuarterTurns = 3, MirrorHorizontal = true,
                MirrorVertical = true, ShowValue = false, ShowTagName = false, ShowEngineeringRange = false,
                ShowConnectionPorts = false, AnimateFlow = true }
        };
        project.Screens[0].Elements.Add(element);
        var clone = HmiProjectSerializer.Clone(project).Screens[0].Elements[^1];
        Assert.True(HmiElementComparer.Equals(element, clone));
        Assert.NotSame(element.Appearance, clone.Appearance);
        clone.Appearance.QuarterTurns = 1;
        Assert.Equal(3, element.Appearance.QuarterTurns);
        Assert.False(HmiElementComparer.Equals(element, clone));
        HmiDesignerRegistration.Register();
        var control = HmiControlCatalog.Create(symbol); control.ApplyDefinition(element); control.ColorScheme = HmiColorScheme.Dark;
        Assert.True(DesignerElementRegistry.TryCreateLike(control, out var duplicate));
        var copiedControl = Assert.IsAssignableFrom<HmiControl>(duplicate);
        Assert.True(HmiElementComparer.Equals(element, copiedControl.CaptureDefinition()));
        Assert.Equal(HmiColorScheme.Dark, copiedControl.ColorScheme);
    }

    [Theory]
    [MemberData(nameof(Symbols))]
    public void LayoutIsFiniteAndContainedAcrossSmallAndLargeSizes(HmiSymbol symbol)
    {
        foreach (var size in new[] { new Vector2(8), new Vector2(16), new Vector2(42), new Vector2(72, 40), new Vector2(128, 70), new Vector2(240, 180), new Vector2(500, 340) })
        {
            var layout = HmiVisualLayout.Calculate(symbol, size.X, size.Y, new());
            foreach (var rect in new[] { layout.Header, layout.Tag, layout.Glyph, layout.Value, layout.Range, layout.Quality })
            {
                Assert.True(float.IsFinite(rect.X) && float.IsFinite(rect.Y) && float.IsFinite(rect.Width) && float.IsFinite(rect.Height));
                Assert.InRange(rect.X, 0, size.X); Assert.InRange(rect.Y, 0, size.Y);
                Assert.InRange(rect.Right, rect.X, size.X + 0.001f); Assert.InRange(rect.Bottom, rect.Y, size.Y + 0.001f);
            }
        }
    }

    [Theory]
    [InlineData(-1)] [InlineData(4)] [InlineData(int.MaxValue)]
    public void InvalidOrientationCannotEnterTheUndoJournal(int turns)
    {
        var session = new HmiDesignerSession(); var before = session.ExportJson();
        Assert.Throws<InvalidDataException>(() => session.Edit("Invalid rotation", p => p.Screens[0].Elements[0].Appearance.QuarterTurns = turns));
        Assert.Equal(before, session.ExportJson()); Assert.False(session.CanUndo);
    }

    [Fact]
    public void LegacyDocumentsGetIndependentAppearanceDefaults()
    {
        var project = HmiProjectSerializer.Deserialize("{\"screens\":[{\"id\":\"overview\",\"elements\":[{\"id\":\"a\"},{\"id\":\"b\"}]}]}");
        Assert.True(project.Screens[0].Elements[0].Appearance.ShowValue);
        Assert.False(project.Screens[0].Elements[0].Appearance.AnimateFlow);
        Assert.NotSame(project.Screens[0].Elements[0].Appearance, project.Screens[0].Elements[1].Appearance);
    }

    [Fact]
    public void NullOrUnknownAppearanceIsRejectedBeforeRendering()
    {
        var project = HmiDemoProject.Create();
        project.Screens[0].Elements[0].Appearance = null!;
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Serialize(project));
        project.Screens[0].Elements[0].Appearance = new() { Presentation = (HmiPresentation)99 };
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Serialize(project));
    }

    [Fact]
    public void ThemeChangesAreInstanceScopedAndNotDocumentEdits()
    {
        var global = ThemeManager.CurrentTheme;
        using var first = new HmiDesignerHost(); using var second = new HmiDesignerHost();
        var before = first.Session.ExportJson();
        var retained = first.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().ToArray();
        first.ColorScheme = HmiColorScheme.HighContrast;
        Assert.Equal(global, ThemeManager.CurrentTheme);
        Assert.Equal(before, first.Session.ExportJson()); Assert.False(first.Session.IsDirty);
        Assert.All(retained, c => Assert.Equal(HmiColorScheme.HighContrast, c.ColorScheme));
        Assert.All(second.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>(), c => Assert.Equal(HmiColorScheme.Light, c.ColorScheme));
        Assert.Equal(retained, first.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>());
    }

    [Fact]
    public void HeaderHeightDoesNotGrowWhenTheWindowNarrows()
    {
        using var host = new HmiDesignerHost();
        foreach (float width in new[] { 720f, 1000f, 1600f })
        {
            host.Measure(new Vector2(width, 800)); host.Arrange(new Rect(0, 0, width, 800));
            var header = host.Children.OfType<FrameworkElement>().Single(c => c.Name == "HmiStudioHeader");
            Assert.InRange(header.Size.Y, 83, 85);
        }
        string original = host.Session.ExportJson();
        host.SetDataPanelHeight(360); host.SetDataPanelsVisible(false); host.SetDataPanelsVisible(true);
        Assert.True(host.AreDataPanelsVisible); Assert.Equal(original, host.Session.ExportJson());
        Assert.Throws<ArgumentOutOfRangeException>(() => host.SetDataPanelHeight(float.NaN));
    }

    [Fact]
    public void FixedArtboardDoesNotShrinkToItsViewportOnSelection()
    {
        var canvas = new DesignerCanvas { DocumentSize = new Vector2(1280, 720) };
        var control = new HmiTank(); canvas.DesignSurface.Children.Add(control);
        canvas.Measure(new Vector2(400, 300)); canvas.Arrange(new Rect(0, 0, 400, 300));
        Assert.Equal(new Vector2(1280, 720), canvas.DesignSurface.Size);
        canvas.SelectElement(control);
        Assert.Equal(new Vector2(1280, 720), canvas.DesignSurface.Size);
        int events = 0; canvas.ViewportChanged += () => events++;
        canvas.ZoomScale = 0.5f; canvas.ApplyTransforms(); Assert.Equal(1, events);
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.DocumentSize = new Vector2(float.NaN, 100));
    }

    [Fact]
    public void SharedSizeMatchingHonorsLocksAndSupportsUndo()
    {
        using var host = new HmiDesignerHost(new HmiProject { Screens = [new() { Id = "overview", Elements =
            [new() { Width = 80, Height = 90 }, new() { Width = 150, Height = 160 }, new() { Width = 100, Height = 110, IsLocked = true }] }] });
        var controls = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().ToArray();
        host.Selection.SelectAll(); host.WorkspaceCanvas.SelectElement(controls[1]); host.Selection.SelectAll();
        string before = host.Session.ExportJson();
        host.Selection.MatchSize(true, true);
        var after = host.Session.GetProject().Screens[0].Elements;
        Assert.Equal(150f, after[0].Width); Assert.Equal(160f, after[0].Height);
        Assert.Equal(100f, after[2].Width); Assert.Equal(110f, after[2].Height);
        host.Session.Undo(); Assert.Equal(before, host.Session.ExportJson());
    }

    [Fact]
    public void RuntimeViewportModeChangesRetainRuntimeAndNavigationTheme()
    {
        var project = HmiDemoProject.Create(); var runtime = new HmiRuntime(project); runtime.Start();
        using var view = new HmiScreenView(project, runtime) { ColorScheme = HmiColorScheme.Dark };
        var viewport = new HmiRuntimeViewport { Screen = view };
        viewport.FitToViewport = false; viewport.FitToViewport = true;
        Assert.Same(view, viewport.Screen);
        runtime.Execute(new HmiAction { Kind = HmiActionKind.Navigate, Target = "operations" });
        Assert.Equal("operations", view.ScreenId);
        Assert.All(view.Controls, c => Assert.Equal(HmiColorScheme.Dark, c.ColorScheme));
    }

    [Fact]
    public void StudioShowcaseHasRealBindingsAndRoundTripsWithoutReflection()
    {
        var project = HmiShowcaseProject.Create();
        var clone = HmiProjectSerializer.Clone(project);
        Assert.Equal(project.Screens[0].Elements.Count, clone.Screens[0].Elements.Count);
        var runtime = new HmiRuntime(project); runtime.Start(true);
        using var view = new HmiScreenView(project, runtime);
        runtime.Write("Pump.Running", HmiValue.From(false));
        Assert.False(view.Controls.Single(c => c.Symbol == HmiSymbol.Pump).IsActive);
    }
    [Fact]
    public void OverlappingSharedCanvasSelectionAndHoverFollowSiblingPaintOrder()
    {
        HmiDesignerRegistration.Register();
        var canvas = new DesignerCanvas { DocumentSize = new Vector2(400, 300), ZoomScale = 0.75f, PanOffset = new Vector2(20, 30) };
        var pipe = new HmiControl(HmiSymbol.Pipe) { Width = 240, Height = 90 };
        var pump = new HmiPump { Width = 120, Height = 120 };
        Canvas.SetLeft(pipe, 40); Canvas.SetTop(pipe, 50);
        Canvas.SetLeft(pump, 90); Canvas.SetTop(pump, 40);
        canvas.DesignSurface.Children.Add(pipe); canvas.DesignSurface.Children.Add(pump);
        canvas.Measure(new Vector2(400, 300)); canvas.Arrange(new Rect(0, 0, 400, 300)); canvas.ApplyTransforms();
        var point = new Vector2(130, 90) * canvas.ZoomScale + canvas.PanOffset;
        canvas.OnPointerMoved(new() { Position = point }); Assert.Same(pump, canvas.HoveredElement);
        canvas.OnPointerPressed(new() { Position = point, IsLeftButtonPressed = true });
        Assert.Same(pump, canvas.SelectedElement);
        canvas.OnPointerReleased(new() { Position = point });
        canvas.SelectElement(null);
        canvas.DesignSurface.Children.Remove(pipe); canvas.DesignSurface.Children.Add(pipe);
        canvas.OnPointerMoved(new() { Position = point }); Assert.Same(pipe, canvas.HoveredElement);
        canvas.OnPointerPressed(new() { Position = point, IsLeftButtonPressed = true });
        Assert.Same(pipe, canvas.SelectedElement);
        canvas.OnPointerReleased(new() { Position = point });
        canvas.SelectElement(null); pipe.Visibility = Visibility.Collapsed;
        canvas.OnPointerPressed(new() { Position = point, IsLeftButtonPressed = true });
        Assert.Same(pump, canvas.SelectedElement);
        canvas.OnPointerReleased(new() { Position = point });
    }

}
