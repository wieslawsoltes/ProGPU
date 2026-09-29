namespace ProGPU.Hmi;

public enum HmiSimulationKind { Constant, Sine, Ramp, Toggle }

public sealed class HmiTagDefinition
{
    public string Name { get; set; } = "Tag";
    public HmiTagType Type { get; set; }
    public HmiValue InitialValue { get; set; } = HmiValue.From(0d);
    public string Unit { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Writable { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    public int StaleAfterMilliseconds { get; set; } = 5000;
    public HmiSimulationKind Simulation { get; set; }
    public double PeriodSeconds { get; set; } = 20;
}

public sealed class HmiRecipe
{
    public string Name { get; set; } = "Recipe";
    public Dictionary<string, HmiValue> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Optional acquisition seam. Applications own transport security, authorization and scheduling.</summary>
public interface IHmiTagSource : IAsyncDisposable
{
    ValueTask<IReadOnlyDictionary<string, HmiTagSample>> ReadAsync(CancellationToken cancellationToken);
}
