namespace ProGPU.Hmi;

public enum HmiAlarmCondition { High, Low, IsTrue, IsFalse }
public enum HmiAlarmSeverity { Information, Warning, Critical }

public sealed class HmiAlarmDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Tag { get; set; } = "";
    public string Message { get; set; } = "Alarm";
    public HmiAlarmCondition Condition { get; set; }
    public HmiAlarmSeverity Severity { get; set; } = HmiAlarmSeverity.Warning;
    public double Limit { get; set; } = 80;
    public double Deadband { get; set; } = 2;
    public int DelayMilliseconds { get; set; }
}

public sealed class HmiAlarmState
{
    public HmiAlarmDefinition Definition { get; }
    public bool IsActive { get; private set; }
    public bool IsAcknowledged { get; private set; } = true;
    public bool IsQualityUnknown { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? ReturnedAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public bool NeedsAttention => IsActive || !IsAcknowledged || IsQualityUnknown;
    private DateTimeOffset? _pendingSince;

    internal HmiAlarmState(HmiAlarmDefinition definition) => Definition = definition;

    internal bool Evaluate(HmiTagSample sample, DateTimeOffset now)
    {
        var previous = (IsActive, IsAcknowledged, IsQualityUnknown);
        IsQualityUnknown = sample.Quality != HmiQuality.Good;
        if (IsQualityUnknown)
        {
            // Bad or stale telemetry must never silently clear an active alarm.
            _pendingSince = null;
            return previous != (IsActive, IsAcknowledged, IsQualityUnknown);
        }
        double value = sample.Value.AsNumber();
        bool condition = Definition.Condition switch
        {
            HmiAlarmCondition.High => IsActive ? value >= Definition.Limit - Definition.Deadband : value >= Definition.Limit,
            HmiAlarmCondition.Low => IsActive ? value <= Definition.Limit + Definition.Deadband : value <= Definition.Limit,
            HmiAlarmCondition.IsTrue => sample.Value.AsBoolean(),
            HmiAlarmCondition.IsFalse => !sample.Value.AsBoolean(),
            _ => false
        };
        if (condition && !IsActive)
        {
            _pendingSince ??= now;
            if ((now - _pendingSince.Value).TotalMilliseconds >= Definition.DelayMilliseconds)
            {
                IsActive = true;
                IsAcknowledged = false;
                ActivatedAt = now;
                ReturnedAt = null;
                AcknowledgedAt = null;
                _pendingSince = null;
            }
        }
        else if (!condition)
        {
            _pendingSince = null;
            if (IsActive)
            {
                IsActive = false;
                ReturnedAt = now;
            }
        }
        return previous != (IsActive, IsAcknowledged, IsQualityUnknown);
    }

    internal bool Acknowledge(DateTimeOffset now)
    {
        if (IsAcknowledged) return false;
        IsAcknowledged = true;
        AcknowledgedAt = now;
        return true;
    }
}
