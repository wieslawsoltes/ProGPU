namespace ProGPU.Hmi;

/// <summary>
/// Single-owner operator navigation and review state. Uses the existing HMI runtime,
/// historian and alarm transitions; it never connects to equipment or creates a driver.
/// </summary>
public sealed class HmiWorkplaceSession : IDisposable
{
    public const int MaximumNavigationEntries = 64;
    public const int MaximumPinnedObjects = 8;
    public static readonly TimeSpan ReviewLifetime = TimeSpan.FromSeconds(30);
    private readonly HmiProject _project;
    private readonly Dictionary<string, HmiScreen> _screens;
    private readonly Dictionary<string, HmiTagDefinition> _tags;
    private readonly Dictionary<HmiObjectAddress, HmiElement> _objects;
    private readonly List<HmiWorkplaceLocation> _history = [];
    private readonly List<HmiObjectAddress> _pinned = [];
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<HmiObjectAddress> _pinnedView;
    private readonly bool _ownsRuntime;
    private int _position;
    private long _generation;
    private bool _disposed;
    private HmiLocalCommandReview? _review;

    public HmiRuntime Runtime { get; }
    public HmiWorkplaceMode Mode { get; private set; }
    public bool IsHeld { get; private set; }
    public HmiWorkplaceLocation Location => _history[_position];
    public HmiLocalCommandReview? PendingReview => _review;
    public bool IsReviewCurrent => !_disposed && _review is { } r && r.Generation == _generation && _clock.GetElapsedTime(r.CreatedTimestamp) < ReviewLifetime;
    public IReadOnlyList<HmiObjectAddress> PinnedObjects => _pinnedView;
    public bool CanGoBack => _position > 0;
    public bool CanGoForward => _position + 1 < _history.Count;
    public event Action? NavigationChanged;
    public event Action? StateChanged;
    public event Action? AlarmsChanged;
    public event Action<IReadOnlyList<string>>? TagsChanged;

    public HmiWorkplaceSession(HmiProject project, HmiRuntime? runtime = null, TimeProvider? clock = null)
    {
        _project = HmiProjectSerializer.Clone(project);
        _screens = _project.Screens.ToDictionary(s => s.Id, StringComparer.Ordinal);
        _tags = _project.Tags.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _objects = _project.Screens.SelectMany(s => s.Elements.Select(e => (Key: new HmiObjectAddress(s.Id, e.Id), Element: e)))
            .ToDictionary(e => e.Key, e => e.Element);
        _clock = clock ?? TimeProvider.System; _pinnedView = _pinned.AsReadOnly();
        _ownsRuntime = runtime == null;
        Runtime = runtime ?? new HmiRuntime(_project, _clock.GetUtcNow());
        // Borrowed runtimes stay host-owned and read-only through this workplace.
        // Validate observable scalar types without starting/stopping the host runtime.
        foreach (var tag in _project.Tags)
            if (!Runtime.TryRead(tag.Name, out var sample) || sample.Value.Type != tag.Type)
                throw new ArgumentException($"Runtime does not provide project tag '{tag.Name}' with the required type.", nameof(runtime));
        Mode = _ownsRuntime ? HmiWorkplaceMode.Offline : HmiWorkplaceMode.ExternalReadOnly;
        _history.Add(new(_project.StartScreenId, null, HmiWorkplaceView.Process));
        Runtime.TagsChanged += OnTagsChanged;
        Runtime.AlarmsChanged += OnAlarmsChanged;
    }

    public HmiProject GetProject() => HmiProjectSerializer.Clone(_project);
    public HmiElement GetObject(HmiObjectAddress address) => FindObject(address).Copy();
    public HmiElement? GetSelectedObject() => Location.ElementId is { } id ? GetObject(new(Location.ScreenId, id)) : null;

    public void Navigate(string screenId, string? elementId = null, HmiWorkplaceView view = HmiWorkplaceView.Process)
    {
        CheckAlive();
        if (!_screens.ContainsKey(screenId)) throw new ArgumentException("Unknown screen.", nameof(screenId));
        if (!Enum.IsDefined(view)) throw new ArgumentOutOfRangeException(nameof(view));
        if (elementId != null) FindObject(new(screenId, elementId));
        var next = new HmiWorkplaceLocation(screenId, elementId, view);
        if (next == Location) return;
        InvalidateReview();
        if (CanGoForward) _history.RemoveRange(_position + 1, _history.Count - _position - 1);
        _history.Add(next);
        if (_history.Count > MaximumNavigationEntries) _history.RemoveAt(0);
        _position = _history.Count - 1;
        NavigationChanged?.Invoke(); StateChanged?.Invoke();
    }
    public void Show(HmiWorkplaceView view) => Navigate(Location.ScreenId, Location.ElementId, view);
    public void Back() => Traverse(-1);
    public void Forward() => Traverse(1);
    private void Traverse(int delta)
    {
        CheckAlive(); int next = _position + delta;
        if (next < 0 || next >= _history.Count) return;
        InvalidateReview(); _position = next; NavigationChanged?.Invoke(); StateChanged?.Invoke();
    }
    public void TogglePin(HmiObjectAddress address)
    {
        CheckAlive(); FindObject(address);
        if (!_pinned.Remove(address))
        {
            if (_pinned.Count >= MaximumPinnedObjects) throw new InvalidOperationException("At most eight object aspects may be pinned.");
            _pinned.Add(address);
        }
        StateChanged?.Invoke();
    }
    public IReadOnlyList<HmiObjectAddress> FindObjects(string text, int maximum = 256)
    {
        CheckAlive(); ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 4096 || maximum is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(maximum));
        return _objects.Where(p => p.Value.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            p.Value.Label.Contains(text, StringComparison.OrdinalIgnoreCase) || p.Value.Tag.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(maximum).Select(p => p.Key).ToArray();
    }
    public IReadOnlyList<HmiWorkplaceAlarm> GetAlarms(string search = "", bool onlyAttention = true,
        bool unacknowledgedOnly = false, HmiAlarmSeverity? severity = null, string? tag = null)
    {
        CheckAlive(); ArgumentNullException.ThrowIfNull(search);
        if (search.Length > 4096 || severity.HasValue && !Enum.IsDefined(severity.Value)) throw new ArgumentException("Invalid alarm filter.");
        return Runtime.Alarms.Where(a => (!onlyAttention || a.NeedsAttention) &&
            (!unacknowledgedOnly || !a.IsAcknowledged) && (!severity.HasValue || a.Definition.Severity == severity) &&
            (tag == null || a.Definition.Tag == tag) &&
            (a.Definition.Message.Contains(search, StringComparison.OrdinalIgnoreCase) || a.Definition.Tag.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => a.Definition.Severity).ThenBy(a => a.IsAcknowledged).ThenByDescending(a => a.ActivatedAt)
            .Select(a => new HmiWorkplaceAlarm(a.Definition.Id, a.Definition.Tag, a.Definition.Message, a.Definition.Severity,
                a.IsActive, a.IsAcknowledged, a.IsQualityUnknown, a.ActivatedAt)).ToArray();
    }
    public HmiObjectAddress? LocateAlarm(string alarmId)
    {
        CheckAlive();
        var alarm = Runtime.Alarms.SingleOrDefault(a => a.Definition.Id == alarmId) ?? throw new ArgumentException("Unknown alarm.", nameof(alarmId));
        // Prefer actual equipment to a duplicate numeric summary of the same tag.
        var matches = _objects.Where(p => p.Value.Tag == alarm.Definition.Tag || p.Value.States.Any(s => s.Tag == alarm.Definition.Tag));
        return matches.OrderBy(p => p.Value.Symbol is HmiSymbol.NumericDisplay or HmiSymbol.Label or HmiSymbol.Trend ? 1 : 0)
            .Select(p => (HmiObjectAddress?)p.Key).FirstOrDefault();
    }
    public void StartSimulation()
    {
        CheckAlive();
        if (!_ownsRuntime) throw new InvalidOperationException("A supplied process runtime cannot be converted into simulation.");
        InvalidateReview(); Runtime.Start(allowLocalWrites: true); Mode = HmiWorkplaceMode.Simulation; IsHeld = false;
        StateChanged?.Invoke();
    }
    public void StopSimulation()
    {
        CheckAlive(); InvalidateReview();
        if (_ownsRuntime) { Runtime.Stop(); Mode = HmiWorkplaceMode.Offline; }
        IsHeld = false; StateChanged?.Invoke();
    }
    public void SetHold(bool held)
    {
        RequireSimulation(); InvalidateReview(); IsHeld = held; StateChanged?.Invoke();
    }
    public void Advance(TimeSpan elapsed)
    {
        RequireSimulation(); if (!IsHeld) Runtime.AdvanceSimulation(elapsed);
    }
    public void Step(TimeSpan elapsed)
    {
        RequireSimulation(); if (!IsHeld) throw new InvalidOperationException("Hold simulation before single stepping.");
        Runtime.AdvanceSimulation(elapsed);
    }

    public HmiLocalCommandReview ReviewValue(HmiObjectAddress address, HmiValue value)
    {
        RequireSimulation();
        var element = FindObject(address);
        if (Location.ScreenId != address.ScreenId || Location.ElementId != address.ElementId)
            throw new InvalidOperationException("Select the object before reviewing its command.");
        ValidateDestination(element, value);
        InvalidateReview();
        var review = new HmiLocalCommandReview(address, element.Tag, value, Runtime.Read(element.Tag).Value,
            _clock.GetUtcNow() + ReviewLifetime, _clock.GetTimestamp(), _generation);
        _review = review; StateChanged?.Invoke(); return review;
    }
    public void Confirm(HmiLocalCommandReview review)
    {
        CheckAlive(); ArgumentNullException.ThrowIfNull(review);
        if (!ReferenceEquals(review, _review)) throw new InvalidOperationException("The command review is no longer current.");
        // Consume before validation, callbacks or publication. Any failed attempt requires a new review.
        _review = null;
        try
        {
            RequireSimulation();
            if (review.Generation != _generation || _clock.GetElapsedTime(review.CreatedTimestamp) >= ReviewLifetime)
                throw new InvalidOperationException("The command review expired or its session changed.");
            var element = FindObject(review.Address);
            ValidateDestination(element, review.Value);
            if (Runtime.Read(review.Tag).Value != review.ExpectedValue)
                throw new InvalidOperationException("Feedback changed after review. Review the new state before commanding.");
            Runtime.Write(review.Tag, review.Value);
        }
        finally { StateChanged?.Invoke(); }
    }
    public void CancelReview() { CheckAlive(); InvalidateReview(); StateChanged?.Invoke(); }
    public void Acknowledge(string alarmId, string actor, string comment)
    {
        RequireSimulation(); Runtime.Acknowledge(alarmId, actor, comment);
    }
    private void ValidateDestination(HmiElement element, HmiValue value)
    {
        if (!_tags.TryGetValue(element.Tag, out var tag) || !tag.Writable) throw new InvalidOperationException("The selected object's value is not writable.");
        if (element.IsHidden || !BindingAllows(element.VisibilityTag) || !BindingAllows(element.EnabledTag))
            throw new InvalidOperationException("The object's visibility/enabled condition does not permit this command.");
        if (Runtime.Read(element.Tag).Quality != HmiQuality.Good) throw new InvalidOperationException("Good-quality feedback is required.");
        HmiProjectSerializer.ValidateValue(tag, value, true);
    }
    private bool BindingAllows(string tag) => tag.Length == 0 || Runtime.TryRead(tag, out var v) && v.Quality == HmiQuality.Good && v.Value.AsBoolean();
    private HmiElement FindObject(HmiObjectAddress address)
    {
        CheckAlive();
        return _objects.TryGetValue(address, out var element) ? element : throw new ArgumentException("Unknown object address.", nameof(address));
    }
    private void RequireSimulation()
    {
        CheckAlive();
        if (!_ownsRuntime || Mode != HmiWorkplaceMode.Simulation || !Runtime.IsRunning || !Runtime.AllowLocalWrites)
            throw new InvalidOperationException("Explicit local simulation is required. This workplace grants no external-write authority.");
    }
    private void InvalidateReview() { _review = null; _generation++; }
    private void OnTagsChanged(IReadOnlyList<string> tags) { if (!_disposed) TagsChanged?.Invoke(tags); }
    private void OnAlarmsChanged() { if (!_disposed) AlarmsChanged?.Invoke(); }
    private void CheckAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; InvalidateReview();
        Runtime.TagsChanged -= OnTagsChanged; Runtime.AlarmsChanged -= OnAlarmsChanged;
        if (_ownsRuntime) Runtime.Stop();
        NavigationChanged = null; StateChanged = null; TagsChanged = null; AlarmsChanged = null;
    }
}
