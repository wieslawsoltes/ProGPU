namespace ProGPU.Hmi;

/// <summary>Persisted time-domain settings; freezing a runtime view is deliberately not document state.</summary>
public sealed class HmiTrendOptions
{
    public double WindowSeconds { get; set; } = 60;
    public double MaximumGapSeconds { get; set; } = 5;
    public HmiTrendOptions Copy() => (HmiTrendOptions)MemberwiseClone();
    public void Validate()
    {
        if (!double.IsFinite(WindowSeconds) || WindowSeconds is < 1 or > 86400 ||
            !double.IsFinite(MaximumGapSeconds) || MaximumGapSeconds is < 0 or > 86400)
            throw new InvalidDataException("Trend window must be 1–86400 seconds; maximum gap must be 0–86400 seconds (0 disables time-gap splitting).");
    }
}
