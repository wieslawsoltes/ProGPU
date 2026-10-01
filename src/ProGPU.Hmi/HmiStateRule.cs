namespace ProGPU.Hmi;

public enum HmiVisualTone { Normal, Running, Warning, Fault, Maintenance, Unknown }
public enum HmiStateCondition { IsTrue, IsFalse, Above, Below, Equal }

/// <summary>Declarative, ordered equipment state; no script execution or reflection.</summary>
public sealed class HmiStateRule
{
    public string Tag { get; set; } = "";
    public HmiStateCondition Condition { get; set; }
    public double Threshold { get; set; }
    public HmiVisualTone Tone { get; set; } = HmiVisualTone.Warning;
    public string Text { get; set; } = "Attention";
    public int Priority { get; set; }
    public HmiStateRule Copy() => (HmiStateRule)MemberwiseClone();
}

public readonly record struct HmiVisualState(HmiVisualTone Tone, string Text);

public static class HmiStateEvaluator
{
    public static HmiVisualState Evaluate(IReadOnlyList<HmiStateRule> rules, Func<string, HmiTagSample?> read)
    {
        HmiStateRule? active = null;
        foreach (var rule in rules)
        {
            var sample = read(rule.Tag);
            // Missing/invalid telemetry overrides all normal equipment colors.
            if (sample is not { Quality: HmiQuality.Good }) return new(HmiVisualTone.Unknown, "UNKNOWN QUALITY");
            bool matches = rule.Condition switch
            {
                HmiStateCondition.IsTrue => sample.Value.Value.AsBoolean(),
                HmiStateCondition.IsFalse => !sample.Value.Value.AsBoolean(),
                HmiStateCondition.Above => sample.Value.Value.AsNumber() > rule.Threshold,
                HmiStateCondition.Below => sample.Value.Value.AsNumber() < rule.Threshold,
                HmiStateCondition.Equal => sample.Value.Value.AsNumber() == rule.Threshold,
                _ => false
            };
            if (matches && (active == null || rule.Priority > active.Priority)) active = rule;
        }
        return active == null ? new(HmiVisualTone.Normal, "") : new(active.Tone, active.Text);
    }
}
