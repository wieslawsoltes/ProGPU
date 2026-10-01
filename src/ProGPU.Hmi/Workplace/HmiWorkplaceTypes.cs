namespace ProGPU.Hmi;

public enum HmiWorkplaceView { Process, Alarms, Events, Trends, System }
public enum HmiWorkplaceMode { Offline, Simulation, ExternalReadOnly }
public readonly record struct HmiObjectAddress(string ScreenId, string ElementId);
public readonly record struct HmiWorkplaceLocation(string ScreenId, string? ElementId, HmiWorkplaceView View);

/// <summary>Immutable alarm row, detached from the runtime's mutable alarm state.</summary>
public sealed record HmiWorkplaceAlarm(string Id, string Tag, string Message, HmiAlarmSeverity Severity,
    bool Active, bool Acknowledged, bool QualityUnknown, DateTimeOffset? ActivatedAt)
{
    public bool NeedsAttention => Active || !Acknowledged || QualityUnknown;
    public string State => QualityUnknown ? "QUALITY UNKNOWN" : Active ? "ACTIVE" : ActivatedAt.HasValue ? "RETURNED" : "NORMAL";
}

/// <summary>A one-use local-simulation review. It grants no external transport authority.</summary>
public sealed class HmiLocalCommandReview
{
    internal HmiLocalCommandReview(HmiObjectAddress address, string tag, HmiValue value, HmiValue expected,
        DateTimeOffset expiresAt, long timestamp, long generation)
    {
        Address = address; Tag = tag; Value = value; ExpectedValue = expected;
        ExpiresAt = expiresAt; CreatedTimestamp = timestamp; Generation = generation;
    }
    public HmiObjectAddress Address { get; }
    public string Tag { get; }
    public HmiValue Value { get; }
    public HmiValue ExpectedValue { get; }
    public DateTimeOffset ExpiresAt { get; }
    internal long CreatedTimestamp { get; }
    internal long Generation { get; }
}
