namespace ProGPU.Hmi;

/// <summary>Original presentation study. Diagram/electrical shapes are reference vocabulary, not live controller logic.</summary>
public static class HmiConventionsProject
{
    public static HmiProject Create()
    {
        var project = new HmiProject { Name = "HMI graphics / presentation conventions", StartScreenId = "conventions" };
        project.Tags.AddRange([
            new() { Name = "Demo.Level", InitialValue = HmiValue.From(62d), Unit = "%" },
            new() { Name = "Demo.Pressure", InitialValue = HmiValue.From(5.8), Minimum = 0, Maximum = 10, Unit = "bar" },
            new() { Name = "Demo.Running", Type = HmiTagType.Boolean, InitialValue = HmiValue.From(true) },
            new() { Name = "Demo.Trip", Type = HmiTagType.Boolean, InitialValue = HmiValue.From(false) }
        ]);
        var screen = new HmiScreen { Id = "conventions", Name = "Graphics conventions", Width = 1536, Height = 1024 };
        screen.Elements.Add(Label("OPERATOR GRAPHICS / CONVENTIONS", 28, 18, 1250, 42, 24));
        screen.Elements.Add(Label("Original symbol vocabulary · No certification implied · No equipment connection", 28, 67, 1250, 35, 13));
        foreach (var style in Enum.GetValues<HmiGraphicStyle>())
        {
            float x = 24 + (int)style * 506;
            screen.Elements.Add(Label(style switch { HmiGraphicStyle.Process => "01  PROCESS", HmiGraphicStyle.HighPerformance => "02  HIGH PERFORMANCE", _ => "03  SCHEMATIC" }, x + 12, 124, 466, 34, 17));
            screen.Elements.Add(Label(style switch { HmiGraphicStyle.Process => "Mechanical detail / separate state", HmiGraphicStyle.HighPerformance => "Neutral normal state / abnormal color", _ => "Reduced fill / diagram vocabulary" }, x + 12, 162, 466, 30, 12));
            screen.Elements.Add(new HmiElement
            {
                Symbol = HmiSymbol.Tank, Name = "TK-101-" + style, Label = "TK-101 / Buffer", Tag = "Demo.Level", Unit = "%",
                X = x + 8, Y = 213, Width = 200, Height = 300,
                Appearance = new() { GraphicStyle = style, NormalMinimum = 35, NormalMaximum = 75, CaptionFontSize = 14 }
            });
            screen.Elements.Add(new HmiElement
            {
                Symbol = HmiSymbol.Gauge, Name = "PT-201-" + style, Label = "PT-201 / Pressure", Tag = "Demo.Pressure", Unit = "bar", Maximum = 10,
                X = x + 222, Y = 213, Width = 246, Height = 180,
                Appearance = new() { GraphicStyle = style, NormalMinimum = 3, NormalMaximum = 7, CaptionFontSize = 13 }
            });
            screen.Elements.Add(new HmiElement
            {
                Symbol = HmiSymbol.Pump, Name = "P-101-" + style, Label = "P-101 / Transfer", Tag = "Demo.Running", X = x + 245, Y = 408, Width = 210, Height = 176,
                Appearance = new() { GraphicStyle = style, CaptionFontSize = 13 },
                States = [new() { Tag = "Demo.Trip", Condition = HmiStateCondition.IsTrue, Tone = HmiVisualTone.Fault, Text = "TRIPPED", Priority = 100 }]
            });
        }
        screen.Elements.Add(Label("INSTRUMENT IDENTIFICATION / ELECTRICAL REFERENCE STATES", 36, 610, 1350, 38, 19));
        var symbols = new[] { HmiSymbol.InstrumentBubble, HmiSymbol.ControlFunction, HmiSymbol.NormallyOpenContact,
            HmiSymbol.NormallyClosedContact, HmiSymbol.RelayCoil, HmiSymbol.CircuitBreaker, HmiSymbol.Transformer, HmiSymbol.ProtectiveEarth };
        for (int i = 0; i < symbols.Length; i++)
        {
            screen.Elements.Add(new HmiElement
            {
                Symbol = symbols[i], Name = "Reference" + i,
                Label = new[] { "PT-101 / field", "FIC-101 / panel", "K1 / NO contact", "K2 / NC contact", "K3 / relay coil", "Q1 / breaker", "T1 / transformer", "PE / earth" }[i],
                X = 27 + i * 188, Y = 674, Width = 170, Height = 210,
                Appearance = new() { GraphicStyle = HmiGraphicStyle.Schematic, ShowValue = false, ShowTagName = false, ShowEngineeringRange = false,
                    CaptionFontSize = 12, InstrumentCode = i == 0 ? "PT" : i == 1 ? "FIC" : "", InstrumentLoop = i < 2 ? "101" : "",
                    InstrumentLocation = i == 1 ? HmiInstrumentLocation.PanelFront : HmiInstrumentLocation.Field }
            });
        }
        screen.Elements.Add(Label("Display bands are visual references, not alarm setpoints. Contacts show their named reference state, not verified live position.", 36, 931, 1430, 32, 13));
        project.Screens.Add(screen);
        HmiProjectSerializer.Validate(project);
        return project;
    }

    private static HmiElement Label(string label, float x, float y, float width, float height, float font) => new()
    {
        Symbol = HmiSymbol.Label, Name = label, Label = label, X = x, Y = y, Width = width, Height = height,
        Appearance = new() { CaptionFontSize = font, ShowValue = false, ShowEngineeringRange = false, ShowTagName = false }
    };
}
