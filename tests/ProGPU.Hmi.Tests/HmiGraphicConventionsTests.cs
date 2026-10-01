using System.Text.Json;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiGraphicConventionsTests
{
    public static IEnumerable<object[]> Styles => Enum.GetValues<HmiGraphicStyle>().Select(s => new object[] { s });
    public static IEnumerable<object[]> NewSymbols => Enum.GetValues<HmiSymbol>().Where(HmiSymbolTraits.IsSchematicSymbol).Select(s => new object[] { s });

    [Theory, MemberData(nameof(Styles))]
    public void MetadataAndAppearanceRoundTripWithoutReflection(HmiGraphicStyle style)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var project = HmiConventionsProject.Create();
        var original = project.Screens[0].Elements.First(e => e.Symbol == HmiSymbol.InstrumentBubble);
        original.Appearance = new() { GraphicStyle = style, CaptionFontSize = 18, CaptionAlignment = HmiCaptionAlignment.End,
            InstrumentCode = "PDT", InstrumentLoop = "204-A", InstrumentLocation = HmiInstrumentLocation.PanelRear,
            NormalMinimum = 20, NormalMaximum = 60, QuarterTurns = 3, MirrorVertical = true };
        var copy = HmiProjectSerializer.Clone(project).Screens[0].Elements.Single(e => e.Id == original.Id);
        Assert.True(HmiElementComparer.Equals(original, copy));
        Assert.NotSame(original.Appearance, copy.Appearance);
        copy.Appearance.InstrumentLoop = "other";
        Assert.False(HmiElementComparer.Equals(original, copy));
    }
    [Theory, InlineData("{"), InlineData("PT 2"), InlineData("μPT"), InlineData("123456789")]
    public void MalformedInstrumentFunctionCannotEnterTheProject(string function)
    {
        var project = HmiConventionsProject.Create(); project.Screens[0].Elements[0].Appearance.InstrumentCode = function;
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Serialize(project));
    }
    [Theory, InlineData(-1), InlineData(7), InlineData(73), InlineData(float.NaN), InlineData(float.PositiveInfinity)]
    public void CaptionSizesAreBoundedAndFinite(float size) => Assert.Throws<InvalidDataException>(() => new HmiAppearance { CaptionFontSize = size }.Validate());
    [Theory, InlineData(0), InlineData(8), InlineData(72)]
    public void AdmittedCaptionSizesRemainValid(float size) => new HmiAppearance { CaptionFontSize = size }.Validate();
    [Fact]
    public void OperatingBandIsAValidatedPairAndNotAnAlarm()
    {
        var p = HmiConventionsProject.Create(); var e = p.Screens[0].Elements.First(e => e.Symbol == HmiSymbol.Gauge);
        e.Appearance.NormalMinimum = null;
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(p));
        e.Appearance.NormalMinimum = 8; e.Appearance.NormalMaximum = 6;
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(p));
        e.Appearance.NormalMinimum = 3; e.Appearance.NormalMaximum = 20;
        Assert.Throws<InvalidDataException>(() => HmiProjectSerializer.Validate(p));
        e.Appearance.NormalMaximum = 7;
        HmiProjectSerializer.Validate(p);
        var runtime = new HmiRuntime(p);
        Assert.Empty(runtime.Alarms); Assert.Empty(p.Alarms);
    }
    [Theory, MemberData(nameof(NewSymbols))]
    public void SchematicControlsHaveIndependentDefaultsAndStablePorts(HmiSymbol symbol)
    {
        var c = HmiControlCatalog.Create(symbol);
        Assert.Equal(HmiGraphicStyle.Schematic, c.Appearance.GraphicStyle);
        Assert.False(c.Appearance.ShowValue);
        var a = HmiControlCatalog.CreateDefinition(symbol); var b = HmiControlCatalog.CreateDefinition(symbol);
        Assert.NotEqual(a.Id, b.Id); Assert.NotSame(a.Appearance, b.Appearance);
        Assert.NotEmpty(HmiSymbolPorts.GetPorts(symbol));
        foreach (var port in HmiSymbolPorts.GetPorts(symbol))
        {
            for (int turns = 0; turns < 4; turns++)
            {
                a.Appearance.QuarterTurns = turns;
                var terminal = HmiPortLayout.Resolve(a, port.Id);
                Assert.True(float.IsFinite(terminal.Point.X) && float.IsFinite(terminal.Point.Y));
            }
        }
    }
    [Fact]
    public void TypedInstrumentHasCorrectDefaultsWithoutCatalogFactory()
    {
        var c = new HmiInstrumentBubble();
        Assert.Equal(HmiGraphicStyle.Schematic, c.Appearance.GraphicStyle);
        Assert.Equal("PT", c.Appearance.InstrumentCode);
    }
    [Fact]
    public void LegacyAppearanceAndStrictEnumInputRemainCompatible()
    {
        var p = HmiProjectSerializer.Deserialize("{\"screens\":[{\"id\":\"overview\",\"elements\":[{\"appearance\":{}}]}]}");
        Assert.Equal(HmiGraphicStyle.Process, p.Screens[0].Elements[0].Appearance.GraphicStyle);
        Assert.Null(p.Screens[0].Elements[0].Appearance.NormalMinimum);
        Assert.Throws<JsonException>(() => HmiProjectSerializer.Deserialize("{\"screens\":[{\"id\":\"overview\",\"elements\":[{\"appearance\":{\"graphicStyle\":77}}]}]}"));
    }
    [Fact]
    public void FormatPainterDoesNotCopyEngineeringIdentityOrBindings()
    {
        var project = HmiConventionsProject.Create();
        using var host = new HmiDesignerHost(project);
        var controls = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Where(c => c.Symbol == HmiSymbol.Tank).ToArray();
        host.Selection.Select(controls[1]); host.CopyGraphicFormat();
        host.Selection.Select(controls[0]);
        var original = host.Session.GetProject().Screens[0].Elements.Single(e => e.Id == controls[0].ElementId);
        string json = host.Session.ExportJson();
        host.PasteGraphicFormat();
        var result = host.Session.GetProject().Screens[0].Elements.Single(e => e.Id == original.Id);
        Assert.Equal(HmiGraphicStyle.HighPerformance, result.Appearance.GraphicStyle);
        Assert.Equal(original.Tag, result.Tag); Assert.Equal(original.Id, result.Id); Assert.Equal(original.Label, result.Label);
        Assert.Equal(original.Appearance.NormalMinimum, result.Appearance.NormalMinimum);
        Assert.Equal(original.Appearance.InstrumentCode, result.Appearance.InstrumentCode);
        Assert.Same(controls[0], host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Single(c => c.ElementId == original.Id));
        host.Session.Undo(); Assert.Equal(json, host.Session.ExportJson());
    }
    [Fact]
    public void GraphicProfileAndOrientationChangesAreOneUndoTransactionAndHonorLocks()
    {
        using var host = new HmiDesignerHost(); var controls = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Take(2).ToArray();
        host.Selection.Select(controls[0]); host.Selection.Select(controls[1], true);
        string before = host.Session.ExportJson(); host.SetGraphicStyle(HmiGraphicStyle.HighPerformance);
        Assert.All(host.Selection.Selection.OfType<HmiControl>(), c => Assert.Equal(HmiGraphicStyle.HighPerformance, c.Appearance.GraphicStyle));
        host.Session.Undo(); Assert.Equal(before, host.Session.ExportJson());
        host.RotateSelectedGraphics(); host.Session.Undo(); Assert.Equal(before, host.Session.ExportJson());
        var id = controls[0].ElementId;
        host.Session.Edit("Lock", p => p.Screens[0].Elements.Single(e => e.Id == id).IsLocked = true);
        Assert.Throws<InvalidOperationException>(() => host.SetGraphicStyle(HmiGraphicStyle.Schematic));
    }
    [Fact]
    public void DesignUsesTheSameInitialStateRulesAsRuntime()
    {
        var p = HmiConventionsProject.Create(); p.Tags.Single(t => t.Name == "Demo.Trip").InitialValue = HmiValue.From(true);
        using var host = new HmiDesignerHost(p); var runtime = new HmiRuntime(p); using var view = new HmiScreenView(p, runtime);
        foreach (var design in host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Where(c => c.Symbol == HmiSymbol.Pump))
        {
            var live = view.Controls.Single(c => c.ElementId == design.ElementId);
            Assert.Equal(live.VisualTone, design.VisualTone); Assert.Equal(live.StateText, design.StateText);
            Assert.Equal(HmiVisualTone.Fault, design.VisualTone);
        }
    }
}
