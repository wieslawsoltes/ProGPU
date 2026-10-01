using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.Text;

namespace ProGPU.WinUI.Hmi;

public enum HmiAlarmFilter { Attention, All, Active, Unacknowledged, UnknownQuality }

/// <summary>
/// Reusable, reflection-free alarm table and transition journal. No designer dependency,
/// private timer, transport writes or automatic acknowledgement of newly arriving alarms.
/// </summary>
public sealed class HmiAlarmConsole : Grid, IDisposable
{
    private readonly TextBox _search;
    private readonly TextBlock _summary;
    private readonly TextBlock _filterLabel;
    private readonly TextBlock _severityLabel;
    private readonly DataGrid _alarms;
    private readonly DataGrid _events;
    private readonly Button _acknowledge;
    private HmiRuntime? _runtime;
    private HmiAlarmFilter _filter = HmiAlarmFilter.Attention;
    private HmiAlarmSeverity _minimumSeverity;
    private bool _allowAcknowledgement;
    private bool _disposed;
    private IReadOnlyList<string> _visibleAlarmIds = Array.Empty<string>();

    public HmiRuntime? Runtime => _runtime;
    public IReadOnlyList<string> VisibleAlarmIds => _visibleAlarmIds;
    public string SummaryText => _summary.Text;
    public bool AllowAcknowledgement
    {
        get => _allowAcknowledgement;
        set { _allowAcknowledgement = value; RefreshPermissions(); }
    }
    public string Actor { get; set; } = "Local operator";
    public string AcknowledgementComment { get; set; } = "Acknowledged in the HMI alarm console";
    public HmiAlarmFilter Filter
    {
        get => _filter;
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); _filter = value; Refresh(); }
    }
    public HmiAlarmSeverity MinimumSeverity
    {
        get => _minimumSeverity;
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); _minimumSeverity = value; Refresh(); }
    }
    public string SearchText { get => _search.Text; set => _search.Text = value ?? ""; }
    public event Action<string>? Error;
    public event Action<string>? CsvExported;

    public HmiAlarmConsole() : this(null) { }
    public HmiAlarmConsole(TtfFont? font)
    {
        font ??= PopupService.DefaultFont;
        RowDefinitions.Add(GridLength.Auto); RowDefinitions.Add(GridLength.Star(1)); RowDefinitions.Add(GridLength.Auto);
        var tools = new WrapPanel { Orientation = Orientation.Horizontal };
        _search = new TextBox { Font = font, FontSize = 12, Width = 250, Height = 30, Margin = new Thickness(3), PlaceholderText = "Search alarm, tag or message" };
        _filterLabel = Caption("", font); _severityLabel = Caption("", font);
        tools.AddChild(_search);
        tools.AddChild(ActionButton(_filterLabel, () => Filter = (HmiAlarmFilter)(((int)Filter + 1) % Enum.GetValues<HmiAlarmFilter>().Length)));
        tools.AddChild(ActionButton(_severityLabel, () => MinimumSeverity = (HmiAlarmSeverity)(((int)MinimumSeverity + 1) % Enum.GetValues<HmiAlarmSeverity>().Length)));
        _acknowledge = ActionButton(Caption("Acknowledge selected locally", font), AcknowledgeSelected);
        tools.AddChild(_acknowledge);
        tools.AddChild(ActionButton(Caption("Export events CSV", font), () =>
        {
            if (_runtime == null) { Error?.Invoke("No runtime is attached."); return; }
            CsvExported?.Invoke(_runtime.ExportAlarmEventsCsv());
        }));
        AddChild(tools);
        var tabs = new Pivot { Font = font };
        _alarms = Table(font, ("Severity", "90", "Severity"), ("Alarm", "150", "Id"), ("Tag", "150", "Tag"), ("Message", "*", "Message"),
            ("State", "105", "State"), ("Ack", "70", "Ack"), ("Quality", "90", "Quality"), ("Activated (UTC)", "170", "Activated"));
        _events = Table(font, ("Sequence", "75", "Sequence"), ("Time (UTC)", "175", "Time"), ("Event", "120", "Kind"), ("Alarm", "140", "Id"),
            ("Message", "*", "Message"), ("Actor", "140", "Actor"), ("Comment", "200", "Comment"));
        tabs.Items.Add(new PivotItem("Current alarms", _alarms));
        tabs.Items.Add(new PivotItem("Transition journal", _events));
        AddChild(tabs); SetRow(tabs, 1);
        _summary = Caption("No runtime attached. Design values are not process telemetry.", font);
        AddChild(_summary); SetRow(_summary, 2);
        _search.TextChanged += (_, _) => Refresh();
        _alarms.SelectionChanged += (_, _) => RefreshPermissions();
        Refresh();
    }

    public void AttachRuntime(HmiRuntime? runtime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(runtime, _runtime)) return;
        if (_runtime != null) _runtime.AlarmsChanged -= Refresh;
        _runtime = runtime;
        if (_runtime != null) _runtime.AlarmsChanged += Refresh;
        Refresh();
    }
    private static TextBlock Caption(string text, TtfFont? font) => new() { Text = text, Font = font, FontSize = 11, Margin = new Thickness(5), Foreground = HmiDrawing.Text };
    private static Button ActionButton(TextBlock label, Action action)
    {
        var button = new Button { Content = label, Height = 30, Margin = new Thickness(3), Padding = new Thickness(5, 0, 5, 0) };
        button.Click += (_, _) => action();
        return button;
    }
    private static DataGrid Table(TtfFont? font, params (string Title, string Width, string Property)[] columns)
    {
        var grid = new DataGrid { Font = font, FontSize = 11, RowHeight = 28, IsReadOnly = true };
        foreach (var column in columns) grid.Columns.Add(new DataGridColumn(column.Title, column.Width, column.Property));
        return grid;
    }
    private HmiReadOnlyRow? SelectedRow => (uint)_alarms.SelectedIndex < (uint)_alarms.ItemsSource.Count ? _alarms.ItemsSource[_alarms.SelectedIndex] as HmiReadOnlyRow : null;
    private string? SelectedAlarmId => SelectedRow?.Context as string;

    public void SelectAlarm(string id)
    {
        for (int i = 0; i < _alarms.ItemsSource.Count; i++)
            if (_alarms.ItemsSource[i] is HmiReadOnlyRow { Context: string candidate } && candidate == id) { _alarms.SelectedIndex = i; return; }
        _alarms.SelectedIndex = -1;
    }
    public void AcknowledgeSelected()
    {
        try
        {
            if (!AllowAcknowledgement || _runtime?.IsRunning != true) throw new InvalidOperationException("Local acknowledgement is disabled by the host or the runtime is stopped.");
            string id = SelectedAlarmId ?? throw new InvalidOperationException("Select an alarm to acknowledge.");
            _runtime.Acknowledge(id, Actor, AcknowledgementComment);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException)
        { Error?.Invoke(error.Message); }
    }
    private bool Matches(string id, string tag, string message)
    {
        string text = _search.Text.Trim();
        return text.Length == 0 || id.Contains(text, StringComparison.OrdinalIgnoreCase) || tag.Contains(text, StringComparison.OrdinalIgnoreCase) || message.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
    public void Refresh()
    {
        if (_disposed) return;
        string? selected = SelectedAlarmId;
        _filterLabel.Text = "View: " + Filter;
        _severityLabel.Text = "Severity ≥ " + MinimumSeverity;
        _alarms.ClearItems(); _events.ClearItems();
        if (_runtime == null)
        {
            _visibleAlarmIds = Array.Empty<string>();
            _summary.Text = "No runtime attached. Start local simulation or explicit read-only acquisition.";
            RefreshPermissions(); return;
        }
        var matches = _runtime.Alarms.Where(a => a.Definition.Severity >= MinimumSeverity && Matches(a.Definition.Id, a.Definition.Tag, a.Definition.Message) && (Filter switch
        {
            HmiAlarmFilter.Attention => a.NeedsAttention,
            HmiAlarmFilter.Active => a.IsActive,
            HmiAlarmFilter.Unacknowledged => !a.IsAcknowledged,
            HmiAlarmFilter.UnknownQuality => a.IsQualityUnknown,
            _ => true
        })).OrderByDescending(a => a.Definition.Severity).ThenByDescending(a => a.ActivatedAt).ThenBy(a => a.Definition.Id, StringComparer.Ordinal).ToArray();
        _visibleAlarmIds = Array.AsReadOnly(matches.Take(500).Select(a => a.Definition.Id).ToArray());
        foreach (var alarm in matches.Take(500))
            _alarms.AddItem(new HmiReadOnlyRow(new Dictionary<string, object?>
            {
                ["Severity"] = alarm.Definition.Severity.ToString(), ["Id"] = alarm.Definition.Id, ["Tag"] = alarm.Definition.Tag, ["Message"] = alarm.Definition.Message,
                ["State"] = alarm.IsActive ? "ACTIVE" : "RETURNED / NORMAL", ["Ack"] = alarm.IsAcknowledged ? "ACK" : "UNACK", ["Quality"] = alarm.IsQualityUnknown ? "UNKNOWN" : "Good",
                ["Activated"] = alarm.ActivatedAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—"
            }, alarm.Definition.Id));
        foreach (var item in _runtime.AlarmEvents.Reverse().Where(e => e.Severity >= MinimumSeverity && Matches(e.AlarmId, e.Tag, e.Message)).Take(500))
            _events.AddItem(new HmiReadOnlyRow(new Dictionary<string, object?>
            {
                ["Sequence"] = item.Sequence, ["Time"] = item.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"), ["Kind"] = item.Kind.ToString(),
                ["Id"] = item.AlarmId, ["Message"] = item.Message, ["Actor"] = item.Actor, ["Comment"] = item.Comment
            }));
        if (selected != null) SelectAlarm(selected);
        _summary.Text = $"{Math.Min(500, matches.Length)} / {matches.Length} matching alarms · {_runtime.Alarms.Count(a => a.IsActive)} active · {_runtime.Alarms.Count(a => !a.IsAcknowledged)} unacknowledged · {_runtime.Alarms.Count(a => a.IsQualityUnknown)} unknown quality · {_runtime.AlarmEvents.Count} journal entries / {_runtime.EvictedAlarmEvents} evicted · LOCAL ACK ONLY";
        RefreshPermissions();
    }
    private void RefreshPermissions()
    {
        if (_acknowledge != null) _acknowledge.IsEnabled = AllowAcknowledgement && _runtime?.IsRunning == true && SelectedAlarmId != null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (_runtime != null) _runtime.AlarmsChanged -= Refresh;
        _runtime = null; _disposed = true;
        _alarms.ClearItems(); _events.ClearItems(); _visibleAlarmIds = Array.Empty<string>();
    }
}
