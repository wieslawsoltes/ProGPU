namespace ProGPU.Hmi;

public static class HmiBuiltInFaceplates
{
    public static HmiFaceplateTemplate PumpStation()
    {
        var template = new HmiFaceplateTemplate { Id = "builtin-pump-station", Name = "Variable-speed pump station", Width = 540, Height = 340 };
        template.Slots.AddRange([
            new() { Name = "Running", Type = HmiTagType.Boolean, InitialValue = HmiValue.From(false), Writable = true },
            new() { Name = "Trip", Type = HmiTagType.Boolean, InitialValue = HmiValue.From(false) },
            new() { Name = "Permissive", Type = HmiTagType.Boolean, InitialValue = HmiValue.From(true) },
            new() { Name = "Speed", Unit = "%", InitialValue = HmiValue.From(60d), Writable = true },
            new() { Name = "Current", Unit = "A", Maximum = 40, InitialValue = HmiValue.From(12d) }
        ]);
        var pump = new HmiElement { Id = "pump", Name = "Pump", Label = "Variable-speed drive", Symbol = HmiSymbol.Pump, Tag = "$Running", X = 20, Y = 20, Width = 190, Height = 170 };
        pump.States.AddRange([
            new() { Tag = "$Trip", Condition = HmiStateCondition.IsTrue, Tone = HmiVisualTone.Fault, Text = "TRIPPED", Priority = 100 },
            new() { Tag = "$Permissive", Condition = HmiStateCondition.IsFalse, Tone = HmiVisualTone.Warning, Text = "INHIBITED", Priority = 50 },
            new() { Tag = "$Running", Condition = HmiStateCondition.IsTrue, Tone = HmiVisualTone.Running, Text = "RUNNING", Priority = 10 }
        ]);
        template.Elements.AddRange([
            pump,
            new() { Id = "current", Name = "Current", Label = "Motor current", Symbol = HmiSymbol.NumericDisplay, Tag = "$Current", Unit = "A", Maximum = 40, X = 230, Y = 20, Width = 285, Height = 95 },
            new() { Id = "speed", Name = "Speed", Label = "Speed setpoint", Symbol = HmiSymbol.NumericInput, Tag = "$Speed", EnabledTag = "$Permissive", Unit = "%", X = 230, Y = 130, Width = 285, Height = 95 },
            new() { Id = "start", Name = "Start", Label = "START", Symbol = HmiSymbol.PushButton, EnabledTag = "$Permissive", X = 20, Y = 265, Width = 220, Height = 55, Action = new() { Kind = HmiActionKind.WriteTag, Target = "$Running", Value = HmiValue.From(true) } },
            new() { Id = "stop", Name = "Stop", Label = "STOP", Symbol = HmiSymbol.PushButton, X = 290, Y = 265, Width = 220, Height = 55, Action = new() { Kind = HmiActionKind.WriteTag, Target = "$Running", Value = HmiValue.From(false) } }
        ]);
        return template;
    }
}
