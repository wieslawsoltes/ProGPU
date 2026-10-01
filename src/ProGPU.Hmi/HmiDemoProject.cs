namespace ProGPU.Hmi;

/// <summary>An original, fully local water-treatment demonstration; not a commissioned plant configuration.</summary>
public static class HmiDemoProject
{
    public static HmiProject Create()
    {
        var project = new HmiProject { Name = "Water treatment · local training plant", StartScreenId = "overview" };
        project.Tags.AddRange([
            new() { Name = "Tank.Level", InitialValue = HmiValue.From(62d), Unit = "%", Simulation = HmiSimulationKind.Sine, PeriodSeconds = 36 },
            new() { Name = "Line.Pressure", InitialValue = HmiValue.From(4.2), Minimum = 0, Maximum = 10, Unit = "bar", Simulation = HmiSimulationKind.Sine, PeriodSeconds = 23 },
            new() { Name = "Line.Flow", InitialValue = HmiValue.From(120d), Maximum = 200, Unit = "m³/h", Simulation = HmiSimulationKind.Ramp, PeriodSeconds = 45 },
            new() { Name = "Pump.Running", Type = HmiTagType.Boolean, InitialValue = HmiValue.From(true), Writable = true },
            new() { Name = "Valve.Open", Type = HmiTagType.Boolean, InitialValue = HmiValue.From(true), Writable = true },
            new() { Name = "Pump.Setpoint", InitialValue = HmiValue.From(65d), Unit = "%", Writable = true },
            new() { Name = "Plant.Status", Type = HmiTagType.Text, InitialValue = HmiValue.From("SIMULATION · No equipment connected") }
        ]);
        project.Alarms.AddRange([
            new() { Id = "level-high", Tag = "Tank.Level", Message = "TK-101 high level", Limit = 85, Deadband = 5, DelayMilliseconds = 500, Severity = HmiAlarmSeverity.Warning },
            new() { Id = "pressure-high", Tag = "Line.Pressure", Message = "PT-201 high pressure", Limit = 8.5, Deadband = 0.5, Severity = HmiAlarmSeverity.Critical },
            new() { Id = "pump-stopped", Tag = "Pump.Running", Message = "P-101 stopped", Condition = HmiAlarmCondition.IsFalse, Severity = HmiAlarmSeverity.Information }
        ]);
        project.Recipes.AddRange([
            new() { Name = "Normal production", Values = new() { ["Pump.Setpoint"] = HmiValue.From(65d), ["Pump.Running"] = HmiValue.From(true), ["Valve.Open"] = HmiValue.From(true) } },
            new() { Name = "Low demand", Values = new() { ["Pump.Setpoint"] = HmiValue.From(30d), ["Pump.Running"] = HmiValue.From(true), ["Valve.Open"] = HmiValue.From(true) } }
        ]);
        var screen = new HmiScreen { Id = "overview", Name = "Process overview", Width = 1280, Height = 720 };
        screen.Elements.AddRange([
            E(HmiSymbol.Label, "WATER TREATMENT / PROCESS OVERVIEW", 30, 20, 950, 55),
            E(HmiSymbol.AlarmBanner, "Active process alarms", 30, 80, 1190, 60),
            E(HmiSymbol.Pipe, "Feed line", 235, 280, 630, 44, "Valve.Open"),
            E(HmiSymbol.Tank, "TK-101 · Storage", 55, 165, 220, 320, "Tank.Level", "%"),
            E(HmiSymbol.Valve, "XV-101", 360, 210, 130, 145, "Valve.Open"),
            E(HmiSymbol.Pump, "P-101 · Transfer", 570, 210, 170, 160, "Pump.Running"),
            E(HmiSymbol.Gauge, "PT-201", 875, 170, 270, 215, "Line.Pressure", "bar", 10),
            E(HmiSymbol.NumericDisplay, "Flow rate", 890, 400, 240, 90, "Line.Flow", "m³/h", 200),
            E(HmiSymbol.Trend, "Tank level · recent samples", 300, 430, 550, 220, "Tank.Level", "%"),
            E(HmiSymbol.ToggleSwitch, "Pump run / stop", 560, 365, 200, 58, "Pump.Running"),
            E(HmiSymbol.NavigationButton, "Trends & alarms →", 970, 605, 240, 52),
            E(HmiSymbol.Label, "SIMULATION · No equipment connected", 35, 660, 820, 38, "Plant.Status")
        ]);
        screen.Elements.Single(e => e.Symbol == HmiSymbol.ToggleSwitch).Action = new() { Kind = HmiActionKind.ToggleTag, Target = "Pump.Running" };
        screen.Elements.Single(e => e.Symbol == HmiSymbol.Valve).Action = new() { Kind = HmiActionKind.ToggleTag, Target = "Valve.Open" };
        screen.Elements.Single(e => e.Symbol == HmiSymbol.NavigationButton).Action = new() { Kind = HmiActionKind.Navigate, Target = "operations" };
        var operations = new HmiScreen { Id = "operations", Name = "Trends, alarms & recipes" };
        operations.Elements.AddRange([
            E(HmiSymbol.Label, "OPERATIONS / TRENDS & ALARMS", 30, 20, 950, 55),
            E(HmiSymbol.Trend, "Pressure history", 30, 100, 570, 245, "Line.Pressure", "bar", 10),
            E(HmiSymbol.Trend, "Flow history", 630, 100, 570, 245, "Line.Flow", "m³/h", 200),
            E(HmiSymbol.AlarmList, "Alarm summary", 30, 370, 740, 230),
            E(HmiSymbol.NumericInput, "Pump setpoint", 830, 380, 350, 70, "Pump.Setpoint", "%"),
            E(HmiSymbol.RecipeButton, "Apply low-demand recipe", 830, 475, 350, 60),
            E(HmiSymbol.PushButton, "Acknowledge alarms", 830, 560, 350, 60),
            E(HmiSymbol.NavigationButton, "← Process overview", 30, 635, 270, 55)
        ]);
        operations.Elements.Single(e => e.Symbol == HmiSymbol.RecipeButton).Action = new() { Kind = HmiActionKind.ApplyRecipe, Target = "Low demand" };
        operations.Elements.Single(e => e.Symbol == HmiSymbol.PushButton).Action = new() { Kind = HmiActionKind.AcknowledgeAlarms };
        operations.Elements.Single(e => e.Symbol == HmiSymbol.NavigationButton).Action = new() { Kind = HmiActionKind.Navigate, Target = "overview" };
        project.Screens.AddRange([screen, operations]);
        HmiProjectSerializer.Validate(project);
        return project;
    }
    private static HmiElement E(HmiSymbol symbol, string label, float x, float y, float width, float height, string tag = "", string unit = "", double max = 100)
        => new() { Symbol = symbol, Name = label, Label = label, X = x, Y = y, Width = width, Height = height, Tag = tag, Unit = unit, Maximum = max };
}
