using System.Globalization;
using System.Text;

namespace ProGPU.Hmi;

public sealed partial class HmiRuntime
{
    private readonly HmiHistory<HmiAlarmEvent> _alarmEvents = new(2048);
    private long _alarmEventSequence;
    public IReadOnlyList<HmiAlarmEvent> AlarmEvents => _alarmEvents;
    public long EvictedAlarmEvents => Math.Max(0, _alarmEventSequence - _alarmEvents.Capacity);

    private void TrackAlarmEvaluation(HmiAlarmState alarm, bool wasActive, bool wasUnknown)
    {
        if (wasUnknown != alarm.IsQualityUnknown)
            AddAlarmEvent(alarm, alarm.IsQualityUnknown ? HmiAlarmEventKind.QualityLost : HmiAlarmEventKind.QualityRestored);
        if (wasActive != alarm.IsActive)
            AddAlarmEvent(alarm, alarm.IsActive ? HmiAlarmEventKind.Activated : HmiAlarmEventKind.Returned);
    }

    private void AddAlarmEvent(HmiAlarmState alarm, HmiAlarmEventKind kind, string actor = "", string comment = "") =>
        _alarmEvents.Add(new(++_alarmEventSequence, Now, alarm.Definition.Id, alarm.Definition.Tag,
            alarm.Definition.Message, alarm.Definition.Severity, kind, alarm.IsActive,
            alarm.IsAcknowledged, alarm.IsQualityUnknown, actor, comment));

    /// <summary>Records a local HMI acknowledgement, never a PLC write or condition reset.</summary>
    public void Acknowledge(string? alarmId, string actor, string comment)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256 || comment == null || comment.Length > 1024)
            throw new ArgumentException("Acknowledgement requires a bounded actor and comment.");
        if (!IsRunning) throw new InvalidOperationException("Run the local alarm session before acknowledging.");
        if (alarmId != null && !_alarms.Any(a => a.Definition.Id == alarmId)) throw new KeyNotFoundException("Unknown alarm ID.");
        bool changed = false;
        foreach (var alarm in _alarms)
        {
            if ((alarmId == null || alarm.Definition.Id == alarmId) && alarm.Acknowledge(Now))
            {
                AddAlarmEvent(alarm, HmiAlarmEventKind.Acknowledged, actor, comment);
                changed = true;
            }
        }
        if (changed)
        {
            Record("Acknowledge", alarmId ?? "All alarms", $"Local acknowledgement by {actor}: {comment}");
            AlarmsChanged?.Invoke();
        }
    }

    public string ExportAlarmEventsCsv()
    {
        var csv = new StringBuilder("sequence,timestamp,alarm,tag,severity,event,active,acknowledged,qualityUnknown,actor,comment,message\r\n");
        foreach (var item in _alarmEvents)
        {
            csv.Append(item.Sequence.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.Timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvText(item.AlarmId)).Append(',').Append(CsvText(item.Tag)).Append(',')
                .Append(item.Severity).Append(',').Append(item.Kind).Append(',')
                .Append(item.IsActive).Append(',').Append(item.IsAcknowledged).Append(',')
                .Append(item.IsQualityUnknown).Append(',').Append(CsvText(item.Actor)).Append(',')
                .Append(CsvText(item.Comment)).Append(',').Append(CsvText(item.Message)).Append("\r\n");
        }
        return csv.ToString();
    }

    private static string CsvText(string text)
    {
        // Preserve RFC-style CSV quoting and avoid spreadsheet formula interpretation of operator text.
        if (text.Length > 0 && ("=+-@\t\r\n".Contains(text[0]) || char.IsWhiteSpace(text[0]))) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
