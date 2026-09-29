"""One-time, exact-match integration of reviewed HMI source. Removed after validation."""
from pathlib import Path


def replace(path, old, new, count=1):
    file = Path(path)
    source = file.read_text(encoding='utf-8')
    if source.count(old) != count:
        raise RuntimeError(f'{path}: expected {count} source anchors for {old[:100]!r}, got {source.count(old)}')
    file.write_text(source.replace(old, new), encoding='utf-8', newline='\n')


model = 'src/ProGPU.Hmi/HmiProject.cs'
replace(model, '    public List<HmiStateRule> States { get; set; } = [];', '    public List<HmiStateRule> States { get; set; } = [];\n    public HmiTrendOptions Trend { get; set; } = new();')
replace(model, '        States = States.Select(s => s.Copy()).ToList(),', '        States = States.Select(s => s.Copy()).ToList(),\n        Trend = Trend.Copy(),')
replace('src/ProGPU.Hmi/HmiProjectSerializer.cs', '                Require(Enum.IsDefined(element.Symbol), "Unknown HMI symbol.");', '                Require(Enum.IsDefined(element.Symbol), "Unknown HMI symbol.");\n                Require(element.Trend != null, "Null trend settings.");\n                element.Trend.Validate();')
runtime = 'src/ProGPU.Hmi/HmiRuntime.cs'
replace(runtime, 'public sealed class HmiRuntime', 'public sealed partial class HmiRuntime')
replace(runtime, '            if ((alarmId == null || alarm.Definition.Id == alarmId) && alarm.Acknowledge(Now)) changed = true;', '''            if ((alarmId == null || alarm.Definition.Id == alarmId) && alarm.Acknowledge(Now))
            {
                AddAlarmEvent(alarm, HmiAlarmEventKind.Acknowledged);
                changed = true;
            }''')
replace(runtime, '        foreach (var alarm in _alarms) changed |= alarm.Evaluate(Read(alarm.Definition.Tag), Now);', '''        foreach (var alarm in _alarms)
        {
            bool active = alarm.IsActive, unknown = alarm.IsQualityUnknown;
            if (alarm.Evaluate(Read(alarm.Definition.Tag), Now))
            {
                TrackAlarmEvaluation(alarm, active, unknown);
                changed = true;
            }
        }''')
# Make stale intervals visible to a trend even if there was no incoming value update.
replace(runtime, '            if (quality != _effectiveQuality[name]) { _effectiveQuality[name] = quality; changed.Add(name); }', '''            if (quality != _effectiveQuality[name])
            {
                if (quality == HmiQuality.Stale) _history[name].Add(_samples[name] with { Quality = HmiQuality.Stale });
                _effectiveQuality[name] = quality;
                changed.Add(name);
            }''')

host = 'src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.cs'
replace(host, '            _canvas.DesignSurface.Children.Clear();\n            var screen = Session.ActiveScreen;', '            var screen = Session.ActiveScreen;')
replace(host, '''            foreach (var element in screen.Elements)
            {
                var control = HmiControlCatalog.Create(element.Symbol);
                control.Font = _font; control.ApplyDefinition(element); control.IsHitTestVisible = false;
                if (Session.Document.Tags.SingleOrDefault(t => t.Name == element.Tag) is { } tag)
                    control.UpdateSample(new HmiTagSample(tag.InitialValue, HmiQuality.Good, DateTimeOffset.UnixEpoch));
                _canvas.DesignSurface.Children.Add(control);
                if (selected.Contains(element.Id)) _selection.Select(control, additive: true);
            }''', '''            ReconcileCanvas(screen);
            var selectedIds = selected.ToHashSet(StringComparer.Ordinal);
            foreach (var control in _canvas.DesignSurface.Children.OfType<HmiControl>())
                if (selectedIds.Contains(control.ElementId)) _selection.Select(control, additive: true);''')
replace(host, '        _discardConfirmation = null;\n        RebuildDocumentViews();', '        _discardConfirmation = null;\n        MarkEngineeringDirty();\n        RebuildDocumentViews();')
replace(host, '            if (_canvas.SelectedElement is HmiControl primary && _gestureStart != null', '            if (!_selection.IsExecutingCommand && _canvas.SelectedElement is HmiControl primary && _gestureStart != null')
replace(host, '            try { Session.Edit("Edit canvas",', '            string previousRevision = Session.ExportJson();\n            try { Session.Edit("Edit canvas",')
replace(host, '''            // Reconcile even no-op/rejected mutations (for example deleting a locked item in the shared outline).
            RebuildDocumentViews();''', '''            // Changed documents were already reconciled by Session.Changed; no-op locked edits still need restoration.
            if (ReferenceEquals(previousRevision, Session.ExportJson())) RebuildDocumentViews();''')
replace(host, '        _multiAdorner.Dispose();', '        _multiAdorner.Dispose();\n        _alarmConsole.Dispose();')

inspector = 'src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.Inspector.cs'
replace(inspector, '        _outline.IsHitTestVisible = !IsPreviewing;', '        _alarmConsole?.AttachRuntime(_runtime);\n        _outline.IsHitTestVisible = !IsPreviewing;')
replace(inspector, '        ElementProperty("Hidden",', '''        if (model.Symbol == HmiSymbol.Trend)
        {
            ElementProperty("Trend window (s)", Format(model.Trend.WindowSeconds), (e, v) => e.Trend.WindowSeconds = Number(v));
            ElementProperty("Maximum gap (s)", Format(model.Trend.MaximumGapSeconds), (e, v) => e.Trend.MaximumGapSeconds = Number(v));
        }
        ElementProperty("Hidden",''')
tables = 'src/ProGPU.WinUI.Hmi.Designer/HmiDesignerHost.Tables.cs'
replace(tables, '        tabs.Items.Add(new PivotItem("Help",', '        tabs.Items.Add(new PivotItem("Engineering", BuildEngineeringPane()));\n        tabs.Items.Add(new PivotItem("Alarm console", BuildAlarmConsolePane()));\n        tabs.Items.Add(new PivotItem("Help",')
view = 'src/ProGPU.WinUI.Hmi/HmiScreenView.cs'
replace(view, '(_runtime.Now.ToUnixTimeMilliseconds() % 1000) / 1000f);', '(_runtime.Now.ToUnixTimeMilliseconds() % 1000) / 1000f, now: _runtime.Now);')
service = 'src/ProGPU.WinUI.Designer/DesignerSelectionService.cs'
replace(service, '    public IReadOnlyList<FrameworkElement> Selection => _selection;', '    public IReadOnlyList<FrameworkElement> Selection => _selection;\n    public bool IsExecutingCommand { get; private set; }')
replace(service, '''        _canvas.NotifyCanvasModifying();
        action(items);
        _canvas.UpdateSelectionAdorner();
        _canvas.Invalidate();
        _canvas.NotifyCanvasModified();''', '''        IsExecutingCommand = true;
        try
        {
            _canvas.NotifyCanvasModifying();
            action(items);
            _canvas.UpdateSelectionAdorner();
            _canvas.Invalidate();
            _canvas.NotifyCanvasModified();
        }
        finally { IsExecutingCommand = false; }''')
print('Integrated retained designer reconciliation, bounded engineering diagnostics, alarm journal/console and time-domain trends.')
