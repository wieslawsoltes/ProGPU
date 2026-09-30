using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Workplace;

public sealed partial class HmiOperatorWorkplace
{
    private readonly Dictionary<HmiWorkplaceView, FrameworkElement> _aspectViews = [];
    private DataGrid _alarmTable = null!, _eventTable = null!, _systemTable = null!;
    private TextBox _alarmSearch = null!, _actor = null!, _ackComment = null!;
    private TextBlock _alarmCount = null!, _eventCount = null!;
    private HmiAlarmSeverity? _severity;
    private bool _unacknowledgedOnly, _allAlarmStates, _objectAlarms;
    private Grid _trendGrid = null!;
    private readonly List<HmiControl> _trendControls = [];
    private bool _freezeTrends;
    private double _trendSeconds = 60;
    private Grid _exportPanel = null!;
    private RichEditBox _exportText = null!;
    private TextBox _exportPath = null!;
    private int _exportBusy;
    public string ExportedText { get; private set; } = "";

    private void BuildAspectViews()
    {
        _aspectViews.Add(HmiWorkplaceView.Process, _viewport);
        _alarmTable = Table(("Priority", "65", "Priority"), ("State", "125", "State"), ("Ack", "65", "Ack"),
            ("Tag / source", "155", "Tag"), ("Description", "*", "Message"), ("Activated UTC", "110", "Time"));
        _alarmTable.SelectionChanged += (_, _) => { if (!_buildingTables) { _selectedAlarm = (_alarmTable.SelectedItem as HmiWorkplaceRow)?.Key; OnState(); } };
        var alarmTools = Row();
        _alarmSearch = Input("Filter alarms / tags…"); _alarmSearch.Width = 225;
        _alarmSearch.TextChanged += (_, _) => RefreshAlarms(); alarmTools.AddChild(_alarmSearch);
        alarmTools.AddChild(Button("All priorities", () => { _severity = null; RefreshAlarms(); }));
        alarmTools.AddChild(Button("Unack / all", () => { _unacknowledgedOnly = !_unacknowledgedOnly; RefreshAlarms(); }));
        alarmTools.AddChild(Button("Active / all states", () => { _allAlarmStates = !_allAlarmStates; RefreshAlarms(); }));
        alarmTools.AddChild(Button("Plant / object", () => { _objectAlarms = !_objectAlarms; RefreshAlarms(); }));
        alarmTools.AddChild(Button("Locate source", LocateSelectedAlarm));
        alarmTools.AddChild(Button("Export CSV", () => ShowExport("alarms.csv", ExportAlarmRows())));
        var ack = Row();
        _actor = Input("Actor (audit context only)"); _actor.Text = "Local simulation"; _actor.Width = 165;
        _ackComment = Input("Acknowledgement comment"); _ackComment.Width = 250;
        ack.AddChild(_actor); ack.AddChild(_ackComment);
        ack.AddChild(Button("Acknowledge selected", () =>
        {
            if (_selectedAlarm == null) throw new InvalidOperationException("Select an alarm first.");
            Session.Acknowledge(_selectedAlarm, _actor.Text, _ackComment.Text);
        }, "Local alarm acknowledgement does not reset the process condition", () => _selectedAlarm != null && Session.Mode == HmiWorkplaceMode.Simulation));
        _alarmCount = Text("", 10, "Muted");
        AddAspect(HmiWorkplaceView.Alarms, "ALARMS / CURRENT CONDITIONS", _alarmTable, alarmTools, ack, _alarmCount);

        _eventTable = Table(("Sequence", "70", "Sequence"), ("Time UTC", "115", "Time"), ("Event", "115", "Kind"),
            ("Tag", "150", "Tag"), ("Description", "*", "Message"), ("Actor", "130", "Actor"));
        var eventTools = Row(); eventTools.AddChild(Button("Refresh", RefreshEvents));
        eventTools.AddChild(Button("Export events", () => ShowExport("alarm-events.csv", Session.Runtime.ExportAlarmEventsCsv())));
        eventTools.AddChild(Button("Locate source", () =>
        {
            if (_eventTable.SelectedItem is not HmiWorkplaceRow row) return;
            var ev = Session.Runtime.AlarmEvents.FirstOrDefault(e => e.Sequence.ToString(CultureInfo.InvariantCulture) == row.Key);
            if (ev != null && Session.LocateAlarm(ev.AlarmId) is { } address) Session.Navigate(address.ScreenId, address.ElementId);
        }));
        _eventCount = Text("", 10, "Muted");
        AddAspect(HmiWorkplaceView.Events, "EVENTS / LOCAL ALARM TRANSITIONS", _eventTable, eventTools, null, _eventCount);

        _trendGrid = new Grid(); _trendGrid.ColumnDefinitions.Add(GridLength.Star(1)); _trendGrid.ColumnDefinitions.Add(GridLength.Star(1));
        _trendGrid.RowDefinitions.Add(GridLength.Star(1)); _trendGrid.RowDefinitions.Add(GridLength.Star(1));
        var trendTools = Row();
        foreach (var span in new[] { 30d, 60d, 300d, 900d })
        {
            double duration = span; trendTools.AddChild(Button(span < 60 ? "30 s" : (span / 60).ToString(CultureInfo.InvariantCulture) + " min", () => { _trendSeconds = duration; BuildTrends(); }));
        }
        trendTools.AddChild(Button("Freeze / live view", () => { _freezeTrends = !_freezeTrends; RefreshTrends(force: true); }));
        trendTools.AddChild(Button("Export selected tag", () =>
        {
            var tag = Session.GetSelectedObject()?.Tag;
            if (string.IsNullOrEmpty(tag)) throw new InvalidOperationException("Select a tagged object before exporting its history.");
            ShowExport("tag-history.csv", Session.Runtime.ExportHistoryCsv(tag));
        }));
        AddAspect(HmiWorkplaceView.Trends, "TRENDS / RECENT IN-MEMORY HISTORY", _trendGrid, trendTools, null,
            Text("Freeze affects this trend view only. Acquisition continues; gaps and bad quality remain visible. History is bounded and not durable.", 10, "Muted"));

        _systemTable = Table(("Tag", "210", "Tag"), ("Type", "75", "Type"), ("Value", "140", "Value"),
            ("Quality", "95", "Quality"), ("Source timestamp UTC", "*", "Time"));
        var systemTools = Row(); systemTools.AddChild(Button("Refresh diagnostics", RefreshSystem));
        systemTools.AddChild(Button("Engineering / connections", () => EngineeringRequested?.Invoke()));
        AddAspect(HmiWorkplaceView.System, "SYSTEM / TELEMETRY AND CONNECTION BOUNDARIES", _systemTable, systemTools, null,
            Text("No PLC endpoint is created by this workplace. Configure adapters and authenticated command review explicitly in Engineering.", 10, "Muted"));
        BuildExportPanel();
    }
    private DataGrid Table(params (string Label, string Width, string Key)[] columns)
    {
        var grid = new DataGrid { Font = _font, FontSize = 11, RowHeight = 29, ShowRowGridLines = true };
        foreach (var c in columns) grid.Columns.Add(new DataGridColumn(c.Label, c.Width, c.Key));
        return grid;
    }
    private void AddAspect(HmiWorkplaceView view, string title, FrameworkElement body, FrameworkElement toolbar, FrameworkElement? secondary, TextBlock footer)
    {
        var pane = new Grid { Background = R("Surface"), Padding = new Thickness(12), Visibility = Visibility.Collapsed };
        pane.RowDefinitions.Add(new GridLength(38)); pane.RowDefinitions.Add(new GridLength(40));
        pane.RowDefinitions.Add(GridLength.Star(1)); pane.RowDefinitions.Add(GridLength.Auto); pane.RowDefinitions.Add(new GridLength(28));
        pane.AddChild(Text(title, 15));
        var bar = HorizontalScroll(toolbar); pane.AddChild(bar); SetRow(bar, 1);
        pane.AddChild(body); SetRow(body, 2);
        if (secondary != null) { var scroll = HorizontalScroll(secondary); scroll.Height = 40; pane.AddChild(scroll); SetRow(scroll, 3); }
        pane.AddChild(footer); SetRow(footer, 4);
        _deck.AddChild(pane); _aspectViews.Add(view, pane);
    }
    private void RefreshAlarms()
    {
        if (_disposed || _alarmTable == null) return;
        RefreshAlarmSummary();
        var rows = CurrentAlarmRows();
        _buildingTables = true;
        try
        {
            _alarmTable.ClearItems();
            foreach (var a in rows) _alarmTable.AddItem(new HmiWorkplaceRow(a.Id, new()
            {
                ["Priority"] = "P" + (3 - (int)a.Severity), ["State"] = a.State,
                ["Ack"] = a.Acknowledged ? "ACK" : "UNACK", ["Tag"] = a.Tag, ["Message"] = a.Message,
                ["Time"] = a.ActivatedAt?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—"
            }));
            _alarmTable.SelectedIndex = rows.ToList().FindIndex(a => a.Id == _selectedAlarm);
            if (_alarmTable.SelectedIndex < 0) _selectedAlarm = null;
        }
        finally { _buildingTables = false; }
        _alarmCount.Text = $"{rows.Count} displayed  |  Priority: {_severity?.ToString() ?? "All"}  |  {(_unacknowledgedOnly ? "Unacknowledged" : "Any acknowledgement")}  |  {(_objectAlarms ? "Selected object" : "Plant scope")}";
        foreach (var (button, enabled) in _commands) button.IsEnabled = enabled();
    }
    private void RefreshAlarmSummary()
    {
        foreach (var (severity, label) in _alarmCounters)
        {
            int count = Session.Runtime.Alarms.Count(a => a.NeedsAttention && a.Definition.Severity == severity);
            label.Text = $"P{3 - (int)severity}   {count:00}";
            label.Foreground = R(count > 0 ? severity == HmiAlarmSeverity.Critical ? "Alarm" : "Warning" : "Muted");
        }
        var attention = Session.GetAlarms();
        int bad = Session.Runtime.TagNames.Count(t => Session.Runtime.Read(t).Quality != HmiQuality.Good);
        _alarmSummary.Text = attention.Count == 0 ? $"No active or unacknowledged alarms    ·    Data quality: {bad} invalid / stale" :
            $"{attention.Count} need attention    ·    {attention[0].Message}    ·    {attention[0].State}";
        _alarmSummary.Foreground = R(attention.Count > 0 ? "Alarm" : "Text");
    }
    private IReadOnlyList<HmiWorkplaceAlarm> CurrentAlarmRows()
    {
        string? tag = _objectAlarms ? Session.GetSelectedObject()?.Tag ?? "" : null;
        return Session.GetAlarms(_alarmSearch.Text, !_allAlarmStates, _unacknowledgedOnly, _severity, tag);
    }
    private void LocateSelectedAlarm()
    {
        if (_selectedAlarm == null) throw new InvalidOperationException("Select an alarm first.");
        if (Session.LocateAlarm(_selectedAlarm) is { } address) Session.Navigate(address.ScreenId, address.ElementId);
        else SetMessage("No graphic object references this alarm tag.");
    }
    private string ExportAlarmRows()
    {
        var csv = new StringBuilder("id,tag,severity,state,acknowledged,qualityUnknown,message\r\n");
        foreach (var a in CurrentAlarmRows()) csv.Append(Csv(a.Id)).Append(',').Append(Csv(a.Tag)).Append(',')
            .Append(a.Severity).Append(',').Append(Csv(a.State)).Append(',').Append(a.Acknowledged).Append(',')
            .Append(a.QualityUnknown).Append(',').Append(Csv(a.Message)).Append("\r\n");
        return csv.ToString();
    }
    private static string Csv(string text)
    {
        if (text.Length > 0 && ("=+-@\t\r\n".Contains(text[0]) || char.IsWhiteSpace(text[0]))) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
    private void RefreshEvents()
    {
        if (_disposed) return;
        _eventTable.ClearItems();
        foreach (var e in Session.Runtime.AlarmEvents.Reverse()) _eventTable.AddItem(new HmiWorkplaceRow(e.Sequence.ToString(CultureInfo.InvariantCulture), new()
        {
            ["Sequence"] = e.Sequence.ToString(CultureInfo.InvariantCulture), ["Time"] = e.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            ["Kind"] = e.Kind.ToString(), ["Tag"] = e.Tag, ["Message"] = e.Message, ["Actor"] = e.Actor
        }));
        _eventCount.Text = $"{Session.Runtime.AlarmEvents.Count} retained transitions · {Session.Runtime.EvictedAlarmEvents} older entries evicted · Local history, not a durable controller event feed";
    }
    private void RefreshSystem()
    {
        _systemTable.ClearItems();
        foreach (var tag in _project.Tags)
        {
            var sample = Session.Runtime.Read(tag.Name);
            _systemTable.AddItem(new HmiWorkplaceRow(tag.Name, new()
            {
                ["Tag"] = tag.Name, ["Type"] = tag.Type.ToString(), ["Value"] = sample.Value.ToString(), ["Quality"] = sample.Quality.ToString(),
                ["Time"] = sample.Timestamp == DateTimeOffset.MinValue ? "No source timestamp" : sample.Timestamp.ToString("O", CultureInfo.InvariantCulture)
            }));
        }
    }
    private void BuildTrends()
    {
        _trendGrid.Children.Clear(); _trendControls.Clear();
        var selectedTag = Session.GetSelectedObject()?.Tag;
        var tags = _project.Tags.Where(t => t.Type == HmiTagType.Number).OrderBy(t => t.Name == selectedTag ? 0 : 1).Take(4).ToArray();
        for (int i = 0; i < tags.Length; i++)
        {
            var tag = tags[i]; var control = new HmiTrend { Font = _font, ColorScheme = ColorScheme };
            var model = HmiControlCatalog.CreateDefinition(HmiSymbol.Trend);
            model.Label = tag.Name; model.Tag = tag.Name; model.Unit = tag.Unit; model.Minimum = tag.Minimum; model.Maximum = tag.Maximum;
            model.Trend.WindowSeconds = _trendSeconds; model.Appearance.GraphicStyle = HmiGraphicStyle.HighPerformance;
            model.Width = 500; model.Height = 280; control.ApplyDefinition(model);
            control.Width = float.NaN; control.Height = float.NaN;
            control.Margin = new Thickness(5); _trendGrid.AddChild(control); SetColumn(control, i % 2); SetRow(control, i / 2);
            _trendControls.Add(control);
        }
        RefreshTrends(force: true);
    }
    private void RefreshTrends(bool force = false)
    {
        if (_freezeTrends && !force) return;
        foreach (var c in _trendControls) if (Session.Runtime.TryRead(c.TagName, out var sample))
            c.UpdateSample(sample, _freezeTrends ? Session.Runtime.GetHistory(c.TagName).ToArray() : Session.Runtime.GetHistory(c.TagName), now: Session.Runtime.Now);
    }
    private void BuildExportPanel()
    {
        _exportPanel = new Grid { Background = R("Panel"), Padding = new Thickness(18), Visibility = Visibility.Collapsed };
        _exportPanel.RowDefinitions.Add(new GridLength(38)); _exportPanel.RowDefinitions.Add(GridLength.Star(1)); _exportPanel.RowDefinitions.Add(new GridLength(42));
        _exportPanel.AddChild(Text("EXPORT / UTF-8 CSV", 15));
        _exportText = new RichEditBox { Font = _font, FontSize = 12, IsReadOnly = true, AcceptsReturn = true };
        _exportPanel.AddChild(_exportText); SetRow(_exportText, 1);
        var tools = Row(); _exportPath = Input("Desktop file path"); _exportPath.Width = 340; tools.AddChild(_exportPath);
        tools.AddChild(Button("Save", () => _ = SaveExportAsync(_exportPath.Text)));
        tools.AddChild(Button("Close", () => _exportPanel.Visibility = Visibility.Collapsed));
        _exportPanel.AddChild(tools); SetRow(tools, 2); _deck.AddChild(_exportPanel);
    }
    private void ShowExport(string fileName, string text)
    {
        ExportedText = text; _exportPath.Text = fileName; _exportText.Text = text; _exportPanel.Visibility = Visibility.Visible;
    }
    public async Task SaveExportAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _exportBusy, 1, 0) != 0) { SetMessage("An export is already in progress."); return; }
        string text = ExportedText;
        string? temporary = null;
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path); string full = Path.GetFullPath(path);
            temporary = Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, full, true); temporary = null;
            UIThread.Post(() => { if (!_disposed) SetMessage("CSV saved to " + full); });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        { UIThread.Post(() => { if (!_disposed) SetMessage("Export failed: " + e.Message, true); }); }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Best-effort cleanup; destination was not replaced. */ }
            }
            Volatile.Write(ref _exportBusy, 0);
        }
    }
}
