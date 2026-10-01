namespace ProGPU.Hmi;

public enum HmiAlarmEventKind { Activated, Returned, Acknowledged, QualityLost, QualityRestored }

/// <summary>Immutable local alarm transition. It does not claim an event-complete PLC alarm feed.</summary>
public sealed record HmiAlarmEvent(
    long Sequence, DateTimeOffset Timestamp, string AlarmId, string Tag, string Message,
    HmiAlarmSeverity Severity, HmiAlarmEventKind Kind, bool IsActive, bool IsAcknowledged,
    bool IsQualityUnknown, string Actor, string Comment);
