namespace ProGPU.Hmi;

/// <summary>A local process-design example with explicit functional links, not a commissioned control configuration.</summary>
public static class HmiShowcaseProject
{
    public static HmiProject Create()
    {
        var project = HmiDemoProject.Create();
        project.Name = "Northwater / Process studio";
        var screen = project.Screens[0];
        screen.Name = "Treatment train"; screen.Width = 1280; screen.Height = 800;
        screen.Elements.Clear();
        HmiElement Add(HmiSymbol symbol, string label, float x, float y, float width, float height, string tag = "", string unit = "", double maximum = 100)
        {
            var element = new HmiElement
            {
                Symbol = symbol, Name = string.IsNullOrWhiteSpace(label) ? symbol.ToString() : label, Label = label, X = x, Y = y, Width = width, Height = height,
                Tag = tag, Unit = unit, Maximum = maximum,
                Appearance = new() { ShowTagName = true, ShowEngineeringRange = true }
            };
            screen.Elements.Add(element); return element;
        }
        Add(HmiSymbol.Label, "NORTHWATER  /  TREATMENT TRAIN", 24, 18, 900, 44);
        Add(HmiSymbol.Label, "LOCAL SIMULATION  ·  NO EQUIPMENT CONNECTED", 28, 67, 1100, 30);
        Add(HmiSymbol.NumericDisplay, "STORAGE LEVEL", 24, 110, 285, 116, "Tank.Level", "%");
        Add(HmiSymbol.NumericDisplay, "DISCHARGE PRESSURE", 325, 110, 285, 116, "Line.Pressure", "bar", 10);
        Add(HmiSymbol.NumericDisplay, "PROCESS FLOW", 626, 110, 285, 116, "Line.Flow", "m³/h", 200);
        Add(HmiSymbol.NumericDisplay, "SPEED DEMAND", 927, 110, 329, 116, "Pump.Setpoint", "%");
        // Pipe is behind the vessel/equipment layer. The rendering does not imply engineering routing or an interlock.
        Add(HmiSymbol.Pipe, "", 161, 367, 883, 28, "Valve.Open").Appearance.ShowTagName = false;
        Add(HmiSymbol.Tank, "TK-101 / BUFFER", 35, 251, 226, 282, "Tank.Level", "%");
        Add(HmiSymbol.Valve, "XV-101 / ISOLATION", 300, 273, 186, 218, "Valve.Open").Action = new() { Kind = HmiActionKind.ToggleTag, Target = "Valve.Open" };
        Add(HmiSymbol.Pump, "P-101 / TRANSFER", 520, 273, 205, 218, "Pump.Running");
        Add(HmiSymbol.Filter, "F-201 / POLISHING", 765, 266, 197, 228).Appearance.ShowValue = false;
        Add(HmiSymbol.Gauge, "PT-201 / DISCHARGE", 1000, 264, 240, 232, "Line.Pressure", "bar", 10);
        var toggle = Add(HmiSymbol.ToggleSwitch, "Pump command", 516, 495, 214, 62, "Pump.Running");
        toggle.Action = new() { Kind = HmiActionKind.ToggleTag, Target = "Pump.Running" };
        Add(HmiSymbol.Trend, "BUFFER LEVEL / RECENT HISTORY", 24, 579, 741, 198, "Tank.Level", "%");
        Add(HmiSymbol.AlarmBanner, "PROCESS NOTIFICATIONS", 787, 579, 469, 102);
        Add(HmiSymbol.NavigationButton, "Operations & alarm console", 787, 702, 469, 64).Action = new() { Kind = HmiActionKind.Navigate, Target = "operations" };
        HmiProjectSerializer.Validate(project);
        return project;
    }
}
