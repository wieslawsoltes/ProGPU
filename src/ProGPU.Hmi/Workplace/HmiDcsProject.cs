namespace ProGPU.Hmi;

/// <summary>Original high-performance process graphics for a standalone DCS workplace demonstration.</summary>
public static class HmiDcsProject
{
    public static HmiProject Create()
    {
        var p = HmiShowcaseProject.Create(); p.Name = "NORTHWATER / Utilities and treatment";
        var train = p.Screens[0]; train.Id = "treatment"; train.Name = "Treatment train";
        train.Elements.RemoveAll(e => e.Symbol == HmiSymbol.ToggleSwitch);
        train.Elements[0].Label = "TREATMENT / TRANSFER AND POLISHING";
        train.Elements[1].Label = "TK-101  →  XV-101  →  P-101  →  F-201    /    Select equipment for object aspects";
        var overview = new HmiScreen { Id = "overview", Name = "Plant overview", Width = 1280, Height = 800 };
        overview.Elements.AddRange([
            E("overview-title", HmiSymbol.Label, "NORTHWATER / PLANT OVERVIEW", 26, 22, 1160, 54),
            E("overview-subtitle", HmiSymbol.Label, "01  STORAGE                    02  TREATMENT                    03  ENERGY & UTILITIES", 30, 90, 1160, 34),
            E("overview-level", HmiSymbol.Tank, "TK-101 / RAW WATER BUFFER", 60, 182, 270, 300, "Tank.Level", "%"),
            E("overview-pump", HmiSymbol.Pump, "P-101 / TRANSFER", 480, 222, 255, 230, "Pump.Running"),
            E("overview-gauge", HmiSymbol.Gauge, "PT-201 / DELIVERY", 890, 205, 280, 252, "Line.Pressure", "bar", 10),
            E("overview-trend", HmiSymbol.Trend, "BUFFER LEVEL / RECENT HISTORY", 32, 592, 795, 178, "Tank.Level", "%"),
            E("overview-alarms", HmiSymbol.AlarmBanner, "PROCESS NOTIFICATIONS", 855, 606, 390, 154)
        ]);
        overview.Elements.Add(Nav("Treatment train →", "treatment", 45, 502, 350));
        overview.Elements.Add(Nav("Pump station →", "pumps", 445, 502, 350));
        overview.Elements.Add(Nav("Energy utilities →", "utilities", 845, 502, 350));
        overview.Links.Add(Link("Raw-water transfer", "overview-level", "overview-pump", "Pump.Running"));
        // The pressure gauge is an instrument, not an invented hydraulic terminal.
        var pumps = new HmiScreen { Id = "pumps", Name = "Pump station", Width = 1280, Height = 800 };
        pumps.Elements.AddRange([
            E("pump-title", HmiSymbol.Label, "PUMP STATION / LOCAL TRAINING", 26, 22, 1180, 54),
            E("pump-run", HmiSymbol.Pump, "P-101 / TRANSFER", 70, 145, 310, 290, "Pump.Running"),
            E("pump-valve", HmiSymbol.Valve, "XV-101 / ISOLATION", 460, 175, 250, 230, "Valve.Open"),
            E("pump-demand", HmiSymbol.NumericDisplay, "SPEED DEMAND / SELECT TO REVIEW", 825, 174, 370, 140, "Pump.Setpoint", "%"),
            E("pump-pressure", HmiSymbol.Trend, "DISCHARGE PRESSURE", 40, 500, 570, 258, "Line.Pressure", "bar", 10),
            E("pump-flow", HmiSymbol.Trend, "DELIVERY FLOW", 650, 500, 570, 258, "Line.Flow", "m³/h", 200)
        ]);
        pumps.Links.Add(Link("Pump discharge", "pump-run", "pump-valve", "Pump.Running"));
        p.Tags.Add(new HmiTagDefinition { Name = "Utility.Temperature", InitialValue = HmiValue.From(62d), Unit = "°C", Maximum = 150,
            Simulation = HmiSimulationKind.Sine, PeriodSeconds = 120 });
        var utilities = new HmiScreen { Id = "utilities", Name = "Energy utilities", Width = 1280, Height = 800 };
        utilities.Elements.AddRange([
            E("utility-title", HmiSymbol.Label, "ENERGY / HEAT RECOVERY", 26, 22, 1180, 54),
            E("utility-heater", HmiSymbol.Heater, "H-301 / HEATING", 60, 170, 280, 300),
            E("utility-exchanger", HmiSymbol.HeatExchanger, "E-301 / HEAT RECOVERY", 490, 175, 300, 285),
            E("utility-temp", HmiSymbol.Thermometer, "TT-301 / OUTLET", 940, 160, 240, 316, "Utility.Temperature", "°C", 150),
            E("utility-trend", HmiSymbol.Trend, "OUTLET TEMPERATURE / RECENT HISTORY", 40, 535, 1190, 225, "Utility.Temperature", "°C", 150)
        ]);
        p.Screens.Insert(0, overview); p.Screens.Add(pumps); p.Screens.Add(utilities); p.StartScreenId = "overview";
        foreach (var e in p.Screens.SelectMany(s => s.Elements))
        {
            e.Appearance.GraphicStyle = HmiGraphicStyle.HighPerformance;
            e.Appearance.ShowTagName = false; e.Appearance.AnimateFlow = false;
            if (e.Tag.Length == 0 && e.Symbol is not (HmiSymbol.Trend or HmiSymbol.AlarmBanner or HmiSymbol.AlarmList)) e.Appearance.ShowValue = false;
        }
        HmiProjectSerializer.Validate(p); return p;
    }
    private static HmiElement E(string id, HmiSymbol symbol, string label, float x, float y, float w, float h,
        string tag = "", string unit = "", double max = 100) => new()
    {
        Id = id, Name = label, Label = label, Symbol = symbol, X = x, Y = y, Width = w, Height = h,
        Tag = tag, Unit = unit, Maximum = max, Appearance = new() { ShowEngineeringRange = true }
    };
    private static HmiElement Nav(string label, string target, float x, float y, float w) => new()
    {
        Symbol = HmiSymbol.NavigationButton, Name = label, Label = label, X = x, Y = y, Width = w, Height = 55,
        Action = new() { Kind = HmiActionKind.Navigate, Target = target }
    };
    private static HmiDiagramLink Link(string name, string from, string to, string feedback) => new()
    {
        Name = name, Source = new() { ElementId = from, PortId = "outlet" }, Target = new() { ElementId = to, PortId = "inlet" }, ActivityTag = feedback
    };
}
