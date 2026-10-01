using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private DataGrid _diagnosticTable = null!;
    private DataGrid _referenceTable = null!;
    private TextBox _engineeringSearch = null!;
    private TextBlock _engineeringSummary = null!;
    private HmiEngineeringReport? _engineeringReport;
    private string? _engineeringRevision;
    private HmiAlarmConsole _alarmConsole = null!;

    private FrameworkElement BuildEngineeringPane()
    {
        var tools = Toolbar();
        _engineeringSearch = Input("Filter diagnostics / tag references", 275);
        tools.AddChild(Command("Analyze project", () => AnalyzeProject()));
        tools.AddChild(_engineeringSearch);
        tools.AddChild(Command("Locate diagnostic", () => LocateEngineeringRow(_diagnosticTable)));
        tools.AddChild(Command("Locate reference", () => LocateEngineeringRow(_referenceTable)));
        _diagnosticTable = Table(("Severity", "90", "Severity"), ("Code", "85", "Code"), ("Message", "*", "Message"), ("Screen", "150", "Screen"), ("Tag", "150", "Tag"));
        _referenceTable = Table(("Tag", "200", "Tag"), ("Usage", "110", "Kind"), ("Owner", "*", "Owner"), ("Screen", "150", "Screen"));
        _diagnosticTable.IsReadOnly = true; _referenceTable.IsReadOnly = true;
        var tabs = new Pivot { Font = _font };
        tabs.Items.Add(new PivotItem("Diagnostics", _diagnosticTable));
        tabs.Items.Add(new PivotItem("Tag cross-references", _referenceTable));
        var pane = new Grid();
        pane.RowDefinitions.Add(GridLength.Auto); pane.RowDefinitions.Add(GridLength.Star(1)); pane.RowDefinitions.Add(GridLength.Auto);
        _engineeringSummary = Text("Analyze the project to check geometry, instrument bindings, commands and live-source coverage.", 10);
        pane.AddChild(tools); pane.AddChild(tabs); SetRow(tabs, 1); pane.AddChild(_engineeringSummary); SetRow(_engineeringSummary, 2);
        _engineeringSearch.TextChanged += (_, _) => PopulateEngineeringTables();
        return pane;
    }

    private FrameworkElement BuildAlarmConsolePane()
    {
        _alarmConsole = new HmiAlarmConsole(_font) { AllowAcknowledgement = true };
        _alarmConsole.Error += message => Status(message, true);
        _alarmConsole.CsvExported += csv => { _monitor.Text = csv; Status("Local alarm events exported to Runtime / audit. The journal is bounded, not a durable historian."); };
        return _alarmConsole;
    }

    public HmiEngineeringReport AnalyzeProject()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engineeringReport = HmiProjectAnalyzer.Analyze(Session.GetProject());
        _engineeringRevision = Session.ExportJson();
        PopulateEngineeringTables();
        return _engineeringReport;
    }

    private void MarkEngineeringDirty()
    {
        if (_engineeringSummary != null && !ReferenceEquals(_engineeringRevision, Session.ExportJson()))
            _engineeringSummary.Text = "Project changed — Analyze project to refresh these results. Existing rows refer to the previous revision.";
    }

    private void PopulateEngineeringTables()
    {
        if (_diagnosticTable == null || _engineeringReport == null) return;
        _diagnosticTable.ClearItems(); _referenceTable.ClearItems();
        string filter = _engineeringSearch.Text.Trim();
        bool Matches(params string[] values) => filter.Length == 0 || values.Any(v => v.Contains(filter, StringComparison.OrdinalIgnoreCase));
        foreach (var diagnostic in _engineeringReport.Diagnostics.Where(d => Matches(d.Code, d.Message, d.Tag, d.ScreenId)))
            _diagnosticTable.AddItem(new HmiReadOnlyRow(new Dictionary<string, object?>
            {
                ["Severity"] = diagnostic.Severity.ToString(), ["Code"] = diagnostic.Code, ["Message"] = diagnostic.Message,
                ["Screen"] = diagnostic.ScreenId, ["Tag"] = diagnostic.Tag
            }, diagnostic));
        foreach (var reference in _engineeringReport.References.Where(r => Matches(r.Tag, r.Owner, r.Kind.ToString(), r.ScreenId)))
            _referenceTable.AddItem(new HmiReadOnlyRow(new Dictionary<string, object?>
            {
                ["Tag"] = reference.Tag, ["Kind"] = reference.Kind.ToString(), ["Owner"] = reference.Owner, ["Screen"] = reference.ScreenId
            }, reference));
        _engineeringSummary.Text = $"{_engineeringReport.Diagnostics.Count(d => d.Severity == HmiDiagnosticSeverity.Error)} errors · {_engineeringReport.Diagnostics.Count(d => d.Severity == HmiDiagnosticSeverity.Warning)} warnings · {_engineeringReport.References.Count} references" +
            (_engineeringReport.IsTruncated ? " · RESULT BUDGET REACHED" : "") + " · Analysis is not commissioning approval.";
        MarkEngineeringDirty();
    }

    private void LocateEngineeringRow(DataGrid table)
    {
        DesignCommand(() =>
        {
            if ((uint)table.SelectedIndex >= (uint)table.ItemsSource.Count || table.ItemsSource[table.SelectedIndex] is not HmiReadOnlyRow row)
                throw new InvalidOperationException("Select a result row first.");
            (string screen, string element) = row.Context switch
            {
                HmiDiagnostic diagnostic => (diagnostic.ScreenId, diagnostic.ElementId),
                HmiTagReference reference => (reference.ScreenId, reference.ElementId),
                _ => ("", "")
            };
            if (screen.Length == 0) { Status("This result belongs to a project-level tag, alarm, recipe, faceplate or connection. Open its corresponding data tab."); return; }
            SelectComponent(screen, element);
        });
    }

    /// <summary>Navigate to an exact screen/component identity, including hidden or locked components.</summary>
    public void SelectComponent(string screenId, string elementId)
    {
        DesignCommand(() =>
        {
            var screen = Session.Document.Screens.SingleOrDefault(s => s.Id == screenId) ?? throw new InvalidOperationException("The referenced screen no longer exists. Analyze the current project.");
            if (elementId.Length > 0 && !screen.Elements.Any(e => e.Id == elementId))
                throw new InvalidOperationException("The referenced component no longer exists. Analyze the current project.");
            _canvas.SelectElement(null); _selection.Select(null);
            _restoreSelection = elementId.Length > 0 ? [elementId] : [];
            Session.SelectScreen(screenId);
            RebuildDocumentViews();
            Fit();
            Status(elementId.Length > 0 ? "Selected component " + elementId : "Selected screen " + screen.Name);
        });
    }
}
