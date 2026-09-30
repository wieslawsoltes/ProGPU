using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private DataGrid? _diagramTable;
    private Button? _linkToolButton;
    private TextBlock? _diagramStatus;
    private HmiLinkEndpoint? _pendingLinkSource;
    private string? _selectedLinkId;
    private bool _selectingLink;
    private bool _refreshingDiagram;
    private bool _restorePorts;
    private List<HmiDiagramLink> _clipboardLinks = [];

    public HmiLinkLayer DiagramLayer => _canvas.DiagramLayer;
    public string? SelectedLinkId => _selectedLinkId;
    public bool IsConnectingDiagram => _canvas.IsConnecting;

    /// <summary>Start an explicit authoring gesture. This never connects an acquisition endpoint or submits a command.</summary>
    public void BeginDiagramConnection()
    {
        CancelLabelEdit();
        DesignCommand(() =>
        {
            CancelRouteEdit();
            if (!_canvas.IsConnecting) _restorePorts = DiagramLayer.ShowPortHandles;
            _pendingLinkSource = null;
            DiagramLayer.PendingPort = null;
            DiagramLayer.ShowPortHandles = true;
            _canvas.IsConnecting = true;
            Status("Connect diagram: click a source nozzle, then a destination nozzle. Escape cancels. No equipment command is sent.");
        });
    }

    public void CancelDiagramConnection()
    {
        if (!_canvas.IsConnecting) return;
        _canvas.IsConnecting = false;
        _pendingLinkSource = null;
        DiagramLayer.PendingPort = null;
        DiagramLayer.ShowPortHandles = _restorePorts;
        UpdateStudioState();
    }

    private void PickDiagramPort(HmiLinkEndpoint endpoint)
    {
        if (IsPreviewing || !_canvas.IsConnecting) return;
        if (_pendingLinkSource == null)
        {
            _pendingLinkSource = endpoint.Copy();
            DiagramLayer.PendingPort = endpoint;
            var source = Session.ActiveScreen.Elements.Single(e => e.Id == endpoint.ElementId);
            Status($"Source: {source.Name} / {endpoint.PortId}. Click a destination nozzle; Escape cancels without changing the project.");
            return;
        }
        if (_pendingLinkSource.ElementId == endpoint.ElementId && _pendingLinkSource.PortId == endpoint.PortId)
        {
            _pendingLinkSource = null;
            DiagramLayer.PendingPort = null;
            Status("Source cleared. Choose a nozzle to begin a new connection.");
            return;
        }
        ConnectPorts(_pendingLinkSource, endpoint);
        CancelDiagramConnection();
    }

    /// <summary>Atomically add a same-screen semantic link, with detached endpoint identities and read-only feedback.</summary>
    public string ConnectPorts(HmiLinkEndpoint source, HmiLinkEndpoint target, HmiLinkKind kind = HmiLinkKind.Process)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var link = new HmiDiagramLink { Source = source.Copy(), Target = target.Copy(), Kind = kind };
        DesignCommand(() => Session.Edit("Connect equipment nozzles", project =>
        {
            var screen = project.Screens.Single(s => s.Id == Session.ActiveScreenId);
            var from = screen.Elements.SingleOrDefault(e => e.Id == source.ElementId) ?? throw new InvalidOperationException("The source equipment is not on this screen.");
            var to = screen.Elements.SingleOrDefault(e => e.Id == target.ElementId) ?? throw new InvalidOperationException("The destination equipment is not on this screen.");
            if (from.IsLocked || to.IsLocked) throw new InvalidOperationException("Unlock the endpoint equipment before changing its connections.");
            if (screen.Links.Any(l => l.Source.ElementId == source.ElementId && l.Source.PortId == source.PortId &&
                l.Target.ElementId == target.ElementId && l.Target.PortId == target.PortId && l.Kind == kind))
                throw new InvalidOperationException("This directed nozzle connection already exists.");
            link.Name = "Link " + (screen.Links.Count + 1);
            screen.Links.Add(link.Copy());
        }));
        SelectDiagramLink(link.Id);
        bool blocked = DiagramLayer.Routes.TryGetValue(link.Id, out var route) && route.Status != HmiRouteStatus.Success;
        Status(blocked ? "Connection saved, but routing needs attention: " + route!.Diagnostic
            : "Nozzle connection created. Geometry follows equipment edits; feedback never changes topology.", error: blocked);
        return link.Id;
    }

    public void SelectDiagramLink(string? id)
    {
        if (id != null && !Session.ActiveScreen.Links.Any(l => l.Id == id)) throw new ArgumentException("Unknown diagram link.", nameof(id));
        if (IsPreviewing) return;
        if (id == _selectedLinkId) return;
        CancelRouteEdit();
        _selectedWaypointIndex = -1;
        _selectingLink = true;
        try
        {
            if (id != null) { _canvas.SelectElement(null); _selection.Select(null); }
            _selectedLinkId = id;
            DiagramLayer.SelectedLinkId = id;
        }
        finally { _selectingLink = false; }
        UpdateInspector();
    }

    public void ReverseSelectedLink() => EditSelectedLink("Reverse diagram link", link =>
    {
        (link.Source, link.Target) = (link.Target, link.Source);
        link.Waypoints.Reverse();
    });

    public void DeleteSelectedLink()
    {
        if (_selectedLinkId == null) return;
        string id = _selectedLinkId;
        DesignCommand(() => Session.Edit("Delete diagram link", project =>
        {
            var screen = project.Screens.Single(s => s.Id == Session.ActiveScreenId);
            var link = screen.Links.Single(l => l.Id == id);
            if (link.IsLocked) throw new InvalidOperationException("Unlock this diagram link before deleting it.");
            screen.Links.Remove(link);
        }));
        SelectDiagramLink(null);
    }

    private void DeleteDesignSelection()
    {
        if (_selectedLinkId != null && _selectedWaypointIndex >= 0) RemoveSelectedWaypoint();
        else if (_selectedLinkId != null) DeleteSelectedLink();
        else _selection.Delete();
    }

    private void EditSelectedLink(string description, Action<HmiDiagramLink> edit, bool allowLocked = false)
    {
        if (_selectedLinkId == null) return;
        EditLink(_selectedLinkId, description, edit, allowLocked);
    }

    private void EditLink(string id, string description, Action<HmiDiagramLink> edit, bool allowLocked)
    {
        DesignCommand(() => Session.Edit(description, project =>
        {
            var link = project.Screens.Single(s => s.Id == Session.ActiveScreenId).Links.Single(l => l.Id == id);
            if (link.IsLocked && !allowLocked) throw new InvalidOperationException("Unlock this diagram link to edit it.");
            edit(link);
        }));
    }

    private void OnModelSelectionChanged()
    {
        if (!_rebuilding && _labelGesture != null && (_selection.Selection.Count != 1 || !ReferenceEquals(_selection.Selection[0], _labelGesture.Control))) CancelLabelEdit();
        if (!_selectingLink && !_rebuilding && _selection.Selection.Count > 0)
        { CancelRouteEdit(); _selectedWaypointIndex = -1; _selectedLinkId = null; DiagramLayer.SelectedLinkId = null; }
        UpdateInspector();
    }

    private void PreviewDiagramGeometry()
    {
        if (_rebuilding || IsPreviewing || Session.ActiveScreen.Links.Count == 0) return;
        // Shared canvas owns manipulation. Preview routing reads the current retained geometry,
        // but publication into project history still occurs once at the end of that gesture.
        var screen = Session.ActiveScreen;
        var elements = _canvas.DesignSurface.Children.OfType<HmiControl>().Select(c => c.CaptureDefinition()).ToList();
        if (elements.Any(e => !float.IsFinite(e.Width) || !float.IsFinite(e.Height) || e.Width is < 8 or > 16384 || e.Height is < 8 or > 16384 ||
            !float.IsFinite(e.X) || !float.IsFinite(e.Y) || Math.Abs(e.X) > 32768 || Math.Abs(e.Y) > 32768)) return;
        var ids = elements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != elements.Count || screen.Links.Any(l => !ids.Contains(l.Source.ElementId) || !ids.Contains(l.Target.ElementId))) return;
        DiagramLayer.SetScreen(new HmiScreen { Id = screen.Id, Name = screen.Name, Width = screen.Width, Height = screen.Height,
            Elements = elements, Links = screen.Links });
    }

    private FrameworkElement BuildDiagramPane()
    {
        _diagramTable = Table(("Link", "145", "Name"), ("Source nozzle", "205", "Source"), ("Target nozzle", "205", "Target"),
            ("Kind", "80", "Kind"), ("Feedback tag", "150", "Tag"), ("Route", "*", "Route"), ("Lock", "55", "Locked"));
        _diagramTable.IsReadOnly = true;
        _diagramTable.SelectionChanged += (_, _) =>
        {
            if (_refreshingDiagram || _rebuilding || IsPreviewing) return;
            if (_diagramTable.SelectedItem is HmiEditorRow row && row.TryGetDataGridValue("Id", out var value) && value is string id)
                SelectDiagramLink(id);
        };
        var tools = Toolbar();
        tools.AddChild(Command("Connect nozzles", BeginDiagramConnection));
        tools.AddChild(Command("Add waypoint", BeginWaypointPlacement));
        tools.AddChild(Command("Remove pin", RemoveSelectedWaypoint));
        tools.AddChild(Command("Auto route", ClearSelectedWaypoints));
        tools.AddChild(Command("Cancel", () => { CancelRouteEdit(); CancelDiagramConnection(); }));
        tools.AddChild(Command("Reverse", ReverseSelectedLink));
        tools.AddChild(Command("Delete", DeleteSelectedLink));
        tools.AddChild(Command("Lock / unlock", () => EditSelectedLink("Toggle link lock", link => link.IsLocked = !link.IsLocked, allowLocked: true)));
        _diagramStatus = Text("Select a link to edit its properties. Diagram links are not transport connections.", 10);
        tools.AddChild(_diagramStatus);
        return TablePane(tools, _diagramTable);
    }

    private void RefreshDiagramTable()
    {
        if (_diagramTable == null) return;
        _refreshingDiagram = true;
        try
        {
            _diagramTable.ClearItems();
            var screen = Session.ActiveScreen;
            if (_selectedLinkId != null && !screen.Links.Any(l => l.Id == _selectedLinkId))
            { _selectedLinkId = null; DiagramLayer.SelectedLinkId = null; }
            var names = screen.Elements.ToDictionary(e => e.Id, e => e.Name, StringComparer.Ordinal);
            int blocked = 0;
            foreach (var link in screen.Links)
            {
                var route = DiagramLayer.Routes.GetValueOrDefault(link.Id);
                if (route != null && route.Status != HmiRouteStatus.Success) blocked++;
                _diagramTable.AddItem(new HmiEditorRow(new()
                {
                    ["Id"] = link.Id, ["Name"] = link.Name,
                    ["Source"] = names[link.Source.ElementId] + ":" + link.Source.PortId,
                    ["Target"] = names[link.Target.ElementId] + ":" + link.Target.PortId,
                    ["Kind"] = link.Kind.ToString(), ["Tag"] = link.ActivityTag,
                    ["Route"] = route?.Diagnostic ?? "Hidden", ["Locked"] = link.IsLocked.ToString()
                }));
            }
            _diagramStatus!.Text = $"{screen.Links.Count} link(s) · {blocked} require routing attention · Select a row for properties";
        }
        finally { _refreshingDiagram = false; }
    }

    private bool BuildLinkInspector()
    {
        var link = Session.ActiveScreen.Links.SingleOrDefault(l => l.Id == _selectedLinkId);
        if (link == null) return false;
        _selectionLabel.Text = "Diagram link · " + link.Name;
        ReadOnlyProperty("Link ID", link.Id);
        ReadOnlyProperty("Source nozzle", link.Source.ElementId + ":" + link.Source.PortId);
        ReadOnlyProperty("Target nozzle", link.Target.ElementId + ":" + link.Target.PortId);
        LinkProperty("Locked", link.IsLocked.ToString(), (l, v) => l.IsLocked = Boolean(v), allowLocked: true);
        if (!link.IsLocked)
        {
            LinkProperty("Name", link.Name, (l, v) => l.Name = v);
            LinkProperty("Kind", link.Kind.ToString(), (l, v) => l.Kind = Choice<HmiLinkKind>(v));
            LinkProperty("Activity tag", link.ActivityTag, (l, v) => l.ActivityTag = v.Trim());
            LinkProperty("Thickness", Format(link.Thickness), (l, v) => l.Thickness = Coordinate(v));
            LinkProperty("Clearance", Format(link.Clearance), (l, v) => l.Clearance = Coordinate(v));
            LinkProperty("Direction arrow", link.ShowDirection.ToString(), (l, v) => l.ShowDirection = Boolean(v));
            LinkProperty("Hidden", link.IsHidden.ToString(), (l, v) => l.IsHidden = Boolean(v));
        }
        BuildWaypointInspector(link);
        var route = DiagramLayer.Routes.GetValueOrDefault(link.Id);
        ReadOnlyProperty("Routing", route?.Diagnostic ?? "Hidden");
        if (route?.Status == HmiRouteStatus.Success) ReadOnlyProperty("Route length", Format(route.Length));
        ReadOnlyProperty("Feedback semantics", "Boolean state/quality only. Arrows describe topology, not measured flow or a controller interlock.");
        return true;
    }

    private void LinkProperty(string name, string value, Action<HmiDiagramLink, string> write, bool allowLocked = false)
    {
        string id = _selectedLinkId!;
        _properties.AddItem(new HmiEditorRow(new() { ["Name"] = name, ["Value"] = value }, (property, text) =>
        {
            if (property != "Value") throw new InvalidOperationException("Property names are read-only.");
            EditLink(id, "Edit link " + name, link => write(link, text), allowLocked);
        }, error => Status(error, true)));
    }
}
