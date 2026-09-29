using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace ProGPU.Hmi;

/// <summary>
/// Single-owner runtime. Marshal acquisition batches to its owning UI/application thread.
/// Commands affect local memory only; there is deliberately no implicit PLC write transport.
/// </summary>
public sealed class HmiRuntime
{
    private readonly HmiProject _project;
    private readonly Dictionary<string, HmiTagDefinition> _definitions;
    private readonly Dictionary<string, HmiTagSample> _samples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HmiHistory<HmiTagSample>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HmiQuality> _effectiveQuality = new(StringComparer.Ordinal);
    private readonly List<HmiAlarmState> _alarms;
    private readonly HmiHistory<HmiAuditEntry> _audit = new(1024);
    private double _simulationSeconds;

    public DateTimeOffset Now { get; private set; }
    public bool IsRunning { get; private set; }
    public bool AllowLocalWrites { get; private set; }
    public IReadOnlyList<HmiAlarmState> Alarms { get; }
    public IReadOnlyList<HmiAuditEntry> Audit => _audit;
    public IReadOnlyCollection<string> TagNames => _samples.Keys;
    public event Action<IReadOnlyList<string>>? TagsChanged;
    public event Action? AlarmsChanged;
    public event Action<string>? NavigationRequested;

    public HmiRuntime(HmiProject project, DateTimeOffset? startTime = null, int historyCapacity = 600)
    {
        if (historyCapacity is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        _project = HmiProjectSerializer.Clone(project);
        _definitions = _project.Tags.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Now = startTime ?? DateTimeOffset.UtcNow;
        // Bound aggregate historian storage, not just each individual series.
        int capacity = Math.Min(historyCapacity, 1_000_000 / Math.Max(1, _definitions.Count));
        foreach (var tag in _project.Tags)
        {
            var sample = new HmiTagSample(tag.InitialValue, HmiQuality.Good, Now);
            _samples.Add(tag.Name, sample);
            _effectiveQuality.Add(tag.Name, HmiQuality.Good);
            var history = new HmiHistory<HmiTagSample>(capacity);
            history.Add(sample);
            _history.Add(tag.Name, history);
        }
        _alarms = _project.Alarms.Select(a => new HmiAlarmState(a)).ToList();
        Alarms = new ReadOnlyCollection<HmiAlarmState>(_alarms);
        EvaluateAlarms();
    }

    public void Start(bool allowLocalWrites = false)
    {
        IsRunning = true;
        AllowLocalWrites = allowLocalWrites;
        Record("Start", "Runtime", allowLocalWrites ? "Local simulation writes enabled" : "Read-only");
    }
    public void Stop()
    {
        IsRunning = false;
        AllowLocalWrites = false;
        Record("Stop", "Runtime", "Commands disabled");
    }
    public HmiTagSample Read(string tag)
    {
        var sample = _samples.TryGetValue(tag, out var found) ? found : throw new KeyNotFoundException($"Unknown HMI tag '{tag}'.");
        if (sample.Quality == HmiQuality.Good && (Now - sample.Timestamp).TotalMilliseconds > _definitions[tag].StaleAfterMilliseconds)
            return sample with { Quality = HmiQuality.Stale };
        return sample;
    }
    public bool TryRead(string tag, out HmiTagSample sample)
    {
        if (!_samples.ContainsKey(tag)) { sample = default; return false; }
        sample = Read(tag);
        return true;
    }
    public IReadOnlyList<HmiTagSample> GetHistory(string tag) => _history.TryGetValue(tag, out var history) ? history : Array.Empty<HmiTagSample>();

    /// <summary>Validate the entire batch before publication. Out-of-order samples are rejected, not silently applied.</summary>
    public void Publish(IReadOnlyDictionary<string, HmiTagSample> batch, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (now < Now) throw new ArgumentOutOfRangeException(nameof(now), "The runtime clock must be monotonic.");
        if (batch.Count > _definitions.Count) throw new InvalidDataException("Acquisition batch exceeds the configured tag count.");
        var validated = batch.ToArray();
        foreach (var pair in validated)
        {
            if (!_definitions.TryGetValue(pair.Key, out var definition)) throw new InvalidDataException($"Unknown acquisition tag: {pair.Key}.");
            HmiProjectSerializer.ValidateValue(definition, pair.Value.Value, enforceRange: false);
            if (!Enum.IsDefined(pair.Value.Quality) || pair.Value.Timestamp > now || pair.Value.Timestamp < _samples[pair.Key].Timestamp)
                throw new InvalidDataException($"Invalid quality or non-monotonic timestamp for {pair.Key}.");
        }
        Now = now;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in validated)
        {
            if (_samples[pair.Key] != pair.Value)
            {
                _samples[pair.Key] = pair.Value;
                _history[pair.Key].Add(pair.Value);
                changed.Add(pair.Key);
            }
        }
        foreach (var name in _definitions.Keys)
        {
            var quality = Read(name).Quality;
            if (quality != _effectiveQuality[name]) { _effectiveQuality[name] = quality; changed.Add(name); }
        }
        EvaluateAlarms();
        if (changed.Count > 0) TagsChanged?.Invoke(changed.ToArray());
    }
    public void AdvanceTime(DateTimeOffset now) => Publish(new Dictionary<string, HmiTagSample>(), now);

    public void Write(string tag, HmiValue value)
    {
        RequireWrite(tag, value);
        Publish(new Dictionary<string, HmiTagSample> { [tag] = new(value, HmiQuality.Good, Now) }, Now);
        Record("Write", tag, value.ToString());
    }
    public void ApplyRecipe(string name)
    {
        var recipe = _project.Recipes.SingleOrDefault(r => r.Name == name) ?? throw new KeyNotFoundException($"Unknown recipe '{name}'.");
        RequireCommandPermission();
        // Validate every destination before changing any tag.
        foreach (var pair in recipe.Values) RequireWrite(pair.Key, pair.Value);
        Publish(recipe.Values.ToDictionary(p => p.Key, p => new HmiTagSample(p.Value, HmiQuality.Good, Now), StringComparer.Ordinal), Now);
        Record("Recipe", name, $"{recipe.Values.Count} local values applied atomically");
    }
    public void Acknowledge(string? alarmId = null)
    {
        RequireCommandPermission();
        if (alarmId != null && !_alarms.Any(a => a.Definition.Id == alarmId)) throw new KeyNotFoundException("Unknown alarm ID.");
        bool changed = false;
        foreach (var alarm in _alarms)
            if ((alarmId == null || alarm.Definition.Id == alarmId) && alarm.Acknowledge(Now)) changed = true;
        if (changed)
        {
            Record("Acknowledge", alarmId ?? "All alarms", "Local acknowledgement; does not clear active conditions");
            AlarmsChanged?.Invoke();
        }
    }
    public void Execute(HmiAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsRunning) throw new InvalidOperationException("Commands are disabled in design mode.");
        switch (action.Kind)
        {
            case HmiActionKind.None: return;
            case HmiActionKind.ToggleTag:
                var sample = Read(action.Target);
                if (sample.Value.Type != HmiTagType.Boolean || sample.Quality != HmiQuality.Good) throw new InvalidOperationException("Toggle requires a good-quality Boolean tag.");
                Write(action.Target, HmiValue.From(!sample.Value.Boolean)); break;
            case HmiActionKind.WriteTag: Write(action.Target, action.Value); break;
            case HmiActionKind.Navigate:
                if (!_project.Screens.Any(s => s.Id == action.Target)) throw new InvalidDataException("Unknown navigation target.");
                NavigationRequested?.Invoke(action.Target); break;
            case HmiActionKind.AcknowledgeAlarms: Acknowledge(); break;
            case HmiActionKind.ApplyRecipe: ApplyRecipe(action.Target); break;
            default: throw new InvalidDataException("Unknown HMI action.");
        }
    }
    public void AdvanceSimulation(TimeSpan elapsed)
    {
        if (!IsRunning) return;
        if (elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(elapsed));
        _simulationSeconds += elapsed.TotalSeconds;
        var now = Now + elapsed;
        var batch = new Dictionary<string, HmiTagSample>(StringComparer.Ordinal);
        foreach (var tag in _project.Tags)
        {
            double phase = _simulationSeconds / tag.PeriodSeconds;
            var value = tag.Simulation switch
            {
                HmiSimulationKind.Sine => HmiValue.From(tag.Minimum + (tag.Maximum - tag.Minimum) * (0.5 + 0.5 * Math.Sin(phase * Math.Tau))),
                HmiSimulationKind.Ramp => HmiValue.From(tag.Minimum + (tag.Maximum - tag.Minimum) * (phase - Math.Floor(phase))),
                HmiSimulationKind.Toggle => HmiValue.From((long)Math.Floor(phase * 2) % 2 == 0),
                _ => _samples[tag.Name].Value
            };
            batch.Add(tag.Name, new(value, HmiQuality.Good, now));
        }
        Publish(batch, now);
    }
    public string ExportHistoryCsv(string tag)
    {
        if (!_history.TryGetValue(tag, out var history)) throw new KeyNotFoundException(tag);
        var output = new StringBuilder("timestamp,tag,value,quality\r\n");
        foreach (var sample in history)
            output.Append(sample.Timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',').Append(Csv(tag)).Append(',')
                .Append(Csv(sample.Value.ToString())).Append(',').Append(sample.Quality).Append("\r\n");
        return output.ToString();
    }
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private void RequireCommandPermission()
    {
        if (!IsRunning || !AllowLocalWrites) throw new InvalidOperationException("Local commands require an explicitly enabled running simulation.");
    }
    private void RequireWrite(string tag, HmiValue value)
    {
        RequireCommandPermission();
        if (!_definitions.TryGetValue(tag, out var definition) || !definition.Writable) throw new InvalidOperationException($"Tag '{tag}' is read-only or unknown.");
        if (Read(tag).Quality != HmiQuality.Good) throw new InvalidOperationException($"Tag '{tag}' has invalid or stale quality.");
        HmiProjectSerializer.ValidateValue(definition, value, true);
    }
    private void EvaluateAlarms()
    {
        bool changed = false;
        foreach (var alarm in _alarms) changed |= alarm.Evaluate(Read(alarm.Definition.Tag), Now);
        if (changed) AlarmsChanged?.Invoke();
    }
    private void Record(string operation, string target, string detail) => _audit.Add(new(Now, operation, target, detail));
}
