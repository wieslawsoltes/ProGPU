using System.Globalization;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.WinUI.Designer;

namespace ProGPU.WinUI.Hmi.Designer;

/// <summary>
/// Embeddable HMI workbench. The shared designer owns pointer interaction, snapping,
/// selection adorners, logical outline, toolbox drag/drop and layout property editing.
/// </summary>
public sealed partial class HmiDesignerHost : Grid, IDisposable
{
    private readonly TtfFont? _font;
    private readonly HmiDesignerCanvas _canvas = new();
    private readonly DesignerSelectionService _selection;
    private readonly VisualTreeOutline _outline;
    private readonly PropertyGrid _layout;
    private readonly DataGrid _properties;
    private readonly StackPanel _screenList = new();
    private readonly StackPanel _palette = new();
    private readonly Grid _workspace = new();
    private readonly ScrollViewer _canvasScroll;
    private readonly HmiRuntimeViewport _previewViewport = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(16) };
    private readonly TextBlock _status;
    private readonly TextBlock _title;
    private readonly TextBlock _selectionLabel;
    private readonly TextBox _filePath;
    private readonly TextBox _json;
    private readonly Grid _dataArea;
    private bool _rebuilding;
    private bool _disposed;
    private bool _ioBusy;
    private string? _discardConfirmation;
    private string[] _restoreSelection = [];
    private List<HmiElement> _clipboard = [];
    private Dictionary<string, HmiElement>? _gestureStart;
    private readonly DesignerMultiSelectionAdorner _multiAdorner;

    public HmiDesignerSession Session { get; }
    public DesignerCanvas WorkspaceCanvas => _canvas;
    public DesignerSelectionService Selection => _selection;
    public bool IsPreviewing => _runtime != null;
    public HmiRuntime? Runtime => _runtime;
    public string StatusText => _status.Text;
    public Func<float>? GetDpiScale { get => _canvas.GetDpiScale; set => _canvas.GetDpiScale = value; }
    public event Action<string>? Error;

    public HmiDesignerHost() : this(null, null) { }
    public HmiDesignerHost(HmiProject? project, TtfFont? font = null)
    {
        _font = font ?? PopupService.DefaultFont;
        _canvas.RulerFont = _font;
        HmiDesignerRegistration.Register();
        Session = new HmiDesignerSession(project);
        _selection = new DesignerSelectionService(_canvas) { CanEdit = e => e is HmiControl { IsDesignLocked: false } && !IsPreviewing };
        _outline = new VisualTreeOutline(_font);
        _layout = new PropertyGrid(_font);
        _properties = Table(("Property", "135", "Name"), ("Value", "*", "Value"));
        _status = Text("Design mode · Local simulation only", 11);
        _title = Text("HMI DESIGNER", 16);
        _selectionLabel = Text("No selection", 11);
        _filePath = Input("Project path (.hmi.json)", 310);
        _json = new TextBox { Font = _font, FontSize = 12, AcceptsReturn = true, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        RowDefinitions.Add(GridLength.Auto);
        RowDefinitions.Add(GridLength.Star(1));
        RowDefinitions.Add(GridLength.Auto);
        RowDefinitions.Add(GridLength.Auto);

        var header = BuildStudioHeader();
        AddChild(header); SetRow(header, 0);

        var leftSplit = new ResponsiveSplitView { OpenPaneLength = 264, CompactModeThreshold = 950, PanePlacement = PanePlacement.Left, IsPaneScrollEnabled = false };
        var rightSplit = new ResponsiveSplitView { OpenPaneLength = 284, CompactModeThreshold = 760, PanePlacement = PanePlacement.Right, IsPaneScrollEnabled = false };
        leftSplit.MainContent = rightSplit;
        var left = _libraryTabs = new Pivot { Font = _font };
        var screens = new StackPanel { Padding = new Thickness(8) };
        var screenTools = Toolbar();
        screenTools.AddChild(Command("Add", () => DesignCommand(Session.AddScreen)));
        screenTools.AddChild(Command("Clone", () => DesignCommand(Session.DuplicateScreen)));
        screenTools.AddChild(Command("Delete", () => DesignCommand(Session.DeleteScreen)));
        screens.AddChild(screenTools); screens.AddChild(_screenList);
        left.Items.Add(new PivotItem("Screens", new ScrollViewer { Content = screens }));
        var toolbox = new Grid(); toolbox.RowDefinitions.Add(GridLength.Auto); toolbox.RowDefinitions.Add(GridLength.Star(1));
        var search = Input("Search HMI components", 210);
        search.TextChanged += (_, _) => BuildPalette(search.Text);
        toolbox.AddChild(search);
        var toolsScroll = new ScrollViewer { Content = _palette };
        toolbox.AddChild(toolsScroll); SetRow(toolsScroll, 1);
        left.Items.Add(new PivotItem("Components", toolbox));
        left.Items.Add(new PivotItem("Outline", _outline));
        left.SelectedIndex = 1;
        leftSplit.PaneContent = left;
        var inspector = new Pivot { Font = _font, Margin = new Thickness(30, 0, 0, 0) };
        var properties = new Grid(); properties.RowDefinitions.Add(GridLength.Auto); properties.RowDefinitions.Add(GridLength.Star(1));
        properties.AddChild(_selectionLabel); properties.AddChild(_properties); SetRow(_properties, 1);
        inspector.Items.Add(new PivotItem("HMI", properties));
        inspector.Items.Add(new PivotItem("Layout", _layout));
        rightSplit.PaneContent = inspector;
        rightSplit.MainContent = _workspace;
        _canvasScroll = new ScrollViewer { Content = _canvas, VerticalScrollMode = ScrollMode.Disabled, HorizontalScrollMode = ScrollMode.Disabled, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _workspace.AddChild(_canvasScroll); _workspace.AddChild(_previewViewport);
        AddChild(leftSplit); SetRow(leftSplit, 1);
        _dataArea = BuildDataArea(); _dataArea.Height = 190;
        AddChild(_dataArea); SetRow(_dataArea, 2);
        _status.Margin = new Thickness(12, 5, 12, 5);
        AddChild(_status); SetRow(_status, 3);

        _multiAdorner = new DesignerMultiSelectionAdorner(_canvas, _selection);
        _canvas.AdornerSurface.Children.Add(_multiAdorner);
        InitializeRouteEditing();
        _canvas.ViewportChanged += UpdateStudioState;
        _canvas.CanvasModifying += OnCanvasModifying;
        _canvas.CanvasModified += OnCanvasModified;
        _canvas.SelectionChanged += OnCanvasSelectionChanged;
        _selection.SelectionChanged += OnModelSelectionChanged;
        _canvas.PortPicked = endpoint => Guard(() => PickDiagramPort(endpoint));
        _canvas.LinkPicked = SelectDiagramLink;
        _canvas.GeometryPreviewChanged = PreviewDiagramGeometry;
        _layout.PropertyChanged += OnCanvasModified;
        _outline.SelectionChanged += element => _canvas.SelectElement(element);
        _outline.CanvasModifying += OnCanvasModifying;
        _outline.CanvasModified += OnCanvasModified;
        _outline.ModeChanged += _ => { _outline.IsLogicalMode = true; _canvas.IsLogicalMode = true; Status("HMI components expose their logical design tree, not internal visuals."); };
        Session.Changed += OnSessionChanged;
        Session.ScreenChanged += OnScreenChanged;
        Unloaded += (_, _) => StopPreview();
        BuildPalette("");
        RebuildDocumentViews();
        InitializeStudio();
    }

    private TextBlock Text(string text, float size = 12) => new() { Text = text, Font = _font, FontSize = size, Foreground = new ThemeResourceBrush("TextPrimary"), Margin = new Thickness(4) };
    private TextBox Input(string placeholder, float width) => new() { PlaceholderText = placeholder, PlaceholderForeground = new ThemeResourceBrush("TextSecondary"), Font = _font, FontSize = 12, Width = width, Height = 30, Margin = new Thickness(3) };
    private static WrapPanel Toolbar() => new() { Orientation = Orientation.Horizontal };
    private Button Command(string text, Action action)
    {
        var button = new Button { Content = Text(text, 11), Height = 30, Padding = new Thickness(7, 0, 7, 0), Margin = new Thickness(2), CornerRadius = 4 };
        button.Click += (_, _) => Guard(action);
        return button;
    }
    private DataGrid Table(params (string Header, string Width, string Property)[] columns)
    {
        var table = new DataGrid { Font = _font, FontSize = 11, RowHeight = 27 };
        foreach (var column in columns) table.Columns.Add(new DataGridColumn(column.Header, column.Width, column.Property));
        return table;
    }
    private void Guard(Action action)
    {
        if (_disposed) return;
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        { Status(error.Message, true); }
    }
    private void Status(string message, bool error = false)
    {
        _status.Text = message;
        UpdateStudioState();
        _status.Foreground = new ThemeResourceBrush(error ? "SystemAccentColor" : "TextSecondary");
        if (error) Error?.Invoke(message);
    }
    private void DesignCommand(Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPreviewing) throw new InvalidOperationException("Stop simulation before changing the design.");
        action();
    }
    private void ConfirmReplace(string key, Action action)
    {
        if (IsPreviewing) StopPreview();
        if (Session.IsDirty && _discardConfirmation != key)
        {
            _discardConfirmation = key;
            Status("Unsaved changes: save first, or click the same command again to discard them.");
            return;
        }
        _discardConfirmation = null;
        action();
    }
    private void BuildPalette(string search)
    {
        _paletteQuery = search;
        _palette.Children.Clear(); _paletteIcons.Clear();
        var items = HmiControlCatalog.Items.Where(d => (d.Name + " " + d.Category + " " + d.Symbol).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        _paletteSummary = Text($"{items.Length} COMPONENTS  /  Drag to place", 10);
        _paletteSummary.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Muted);
        _palette.AddChild(_paletteSummary);
        foreach (var group in items.GroupBy(d => d.Category))
        {
            var category = Text(group.Key.ToUpperInvariant(), 10);
            category.Margin = new Thickness(10, 16, 6, 5);
            category.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Muted);
            _palette.AddChild(category);
            foreach (var descriptor in group)
            {
                var row = new Grid { Height = 54, Margin = new Thickness(6, 2, 16, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
                row.ColumnDefinitions.Add(GridLength.Star(1)); row.ColumnDefinitions.Add(new GridLength(32));
                var glyph = new HmiSymbolIcon { Symbol = descriptor.Symbol, ColorScheme = ColorScheme, Width = 38, Height = 38, Margin = new Thickness(2, 4) };
                _paletteIcons.Add(glyph);
                var content = new Grid(); content.ColumnDefinitions.Add(new GridLength(46)); content.ColumnDefinitions.Add(GridLength.Star(1));
                var caption = Text(descriptor.Name, 11); caption.VerticalAlignment = VerticalAlignment.Center;
                caption.TextWrapping = TextWrapping.NoWrap; caption.TextTrimming = TextTrimming.CharacterEllipsis;
                content.AddChild(glyph); content.AddChild(caption); SetColumn(caption, 1);
                var item = new ToolboxItem(HmiDesignerRegistration.ToolboxKey(descriptor.Symbol), descriptor.Name, "", _font)
                { Child = content, Padding = new Thickness(3, 0), Margin = new Thickness(1), Height = 50 };
                row.AddChild(item);
                var symbol = descriptor.Symbol;
                var add = Command("+", () => AddComponent(symbol)); row.AddChild(add); SetColumn(add, 1);
                ToolTipService.SetToolTip(add, "Insert " + descriptor.Name);
                _palette.AddChild(row);
            }
        }
        if (items.Length == 0) _palette.AddChild(Text("No matching components. Try a type or category name.", 12));
    }
    public void AddComponent(HmiSymbol symbol, float x = 60, float y = 80)
    {
        DesignCommand(() =>
        {
            var control = HmiControlCatalog.Create(symbol);
            var element = control.CaptureDefinition();
            element.X = x; element.Y = y;
            element.Name = symbol + "_" + (Session.ActiveScreen.Elements.Count + 1);
            _restoreSelection = [element.Id];
            Session.Edit("Add " + symbol, p => p.Screens.Single(s => s.Id == Session.ActiveScreenId).Elements.Add(element));
        });
    }
    private void RebuildDocumentViews()
    {
        if (_disposed || _rebuilding) return;
        _rebuilding = true;
        try
        {
            var selected = _restoreSelection.Length > 0 ? _restoreSelection : _selection.Selection.OfType<HmiControl>().Select(c => c.CaptureDefinition().Id).ToArray();
            _restoreSelection = [];
            _canvas.SelectElement(null);
            _selection.Select(null);
            var screen = Session.ActiveScreen;
            _canvas.DesignSurface.Width = screen.Width; _canvas.DesignSurface.Height = screen.Height;
            _canvas.DocumentSize = new Vector2(screen.Width, screen.Height);
            ReconcileCanvas(screen);
            var selectedIds = selected.ToHashSet(StringComparer.Ordinal);
            foreach (var control in _canvas.DesignSurface.Children.OfType<HmiControl>())
                if (selectedIds.Contains(control.ElementId)) _selection.Select(control, additive: true);
            _canvas.SelectElement(_selection.Selection.LastOrDefault());
            _outline.RootElement = _canvas.DesignSurface; _outline.SelectedElement = _canvas.SelectedElement; _outline.RefreshTree();
            _screenList.Children.Clear();
            foreach (var item in Session.Document.Screens)
            {
                string id = item.Id;
                _screenList.AddChild(Command((id == Session.ActiveScreenId ? "● " : "") + item.Name, () => DesignCommand(() => Session.SelectScreen(id))));
            }
            _title.Text = Session.Document.Name + (Session.IsDirty ? "  • unsaved" : "");
            RefreshTables();
            _canvas.InvalidateMeasure(); _canvas.Invalidate(); _multiAdorner.Invalidate();
        }
        finally { _rebuilding = false; }
        UpdateInspector();
    }
    private void OnSessionChanged()
    {
        CancelRouteEdit();
        _selectedWaypointIndex = -1;
        CancelDiagramConnection();
        _discardConfirmation = null;
        MarkEngineeringDirty();
        RebuildDocumentViews();
    }
    private void OnScreenChanged() { CancelRouteEdit(); CancelDiagramConnection(); SelectDiagramLink(null); RebuildDocumentViews(); Fit(); }
    private void OnCanvasSelectionChanged()
    {
        if (_rebuilding) return;
        _selection.Select(_canvas.SelectedElement, InputSystem.Current.IsControlPressed);
        if (_canvas.SelectedElement is HmiControl selected && selected.CaptureDefinition().Group is { Length: > 0 } group && !InputSystem.Current.IsControlPressed)
            foreach (var control in _canvas.DesignSurface.Children.OfType<HmiControl>())
                if (control != selected && control.CaptureDefinition().Group == group) _selection.Select(control, additive: true);
        _outline.SelectedElement = _canvas.SelectedElement;
        _outline.RefreshTree();
    }
    private void OnCanvasModifying()
    {
        if (!_rebuilding && !IsPreviewing) _gestureStart = _canvas.DesignSurface.Children.OfType<HmiControl>().ToDictionary(c => c.CaptureDefinition().Id, c => c.CaptureDefinition());
    }
    private void OnCanvasModified()
    {
        // Wheel zoom also raises this shared notification. A route gesture owns its
        // detached preview until release; do not replace it with the committed model.
        if (_rebuilding || IsPreviewing || _waypointGesture != null) return;
        Guard(() =>
        {
            var original = Session.ActiveScreen.Elements.ToDictionary(e => e.Id);
            var controls = _canvas.DesignSurface.Children.OfType<HmiControl>().ToArray();
            if (!_selection.IsExecutingCommand && _canvas.SelectedElement is HmiControl primary && _gestureStart != null && _gestureStart.TryGetValue(primary.CaptureDefinition().Id, out var before))
            {
                float dx = Canvas.GetLeft(primary) - before.X, dy = Canvas.GetTop(primary) - before.Y;
                foreach (var other in _selection.Selection.OfType<HmiControl>().Where(c => c != primary && !c.IsDesignLocked))
                {
                    var element = other.CaptureDefinition();
                    if (_gestureStart.TryGetValue(element.Id, out var start) && element.X == start.X && element.Y == start.Y)
                    { Canvas.SetLeft(other, start.X + dx); Canvas.SetTop(other, start.Y + dy); }
                }
            }
            _gestureStart = null;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var elements = new List<HmiElement>();
            foreach (var control in controls)
            {
                var element = control.CaptureDefinition();
                element.IsHidden = control.Visibility != Visibility.Visible;
                if (original.TryGetValue(element.Id, out var saved) && saved.IsLocked)
                { element = saved.Copy(); control.ApplyDefinition(element); }
                if (!ids.Add(element.Id)) { element = element.Copy(newIdentity: true); ids.Add(element.Id); }
                elements.Add(element);
            }
            // Locked components cannot be deleted through the shared outline's context menu.
            for (int index = 0; index < Session.ActiveScreen.Elements.Count; index++)
            {
                var locked = Session.ActiveScreen.Elements[index];
                if (locked.IsLocked && !ids.Contains(locked.Id))
                    elements.Insert(Math.Min(index, elements.Count), locked.Copy());
            }
            _restoreSelection = _selection.Selection.OfType<HmiControl>().Select(c => c.CaptureDefinition().Id).ToArray();
            string previousRevision = Session.ExportJson();
            try
            {
                Session.Edit("Edit canvas", p =>
                {
                    var screen = p.Screens.Single(s => s.Id == Session.ActiveScreenId);
                    screen.Elements = elements;
                    HmiDiagram.RemoveDanglingLinks(screen);
                });
            }
            catch { RebuildDocumentViews(); throw; }
            // Changed documents were already reconciled by Session.Changed; no-op locked edits still need restoration.
            if (ReferenceEquals(previousRevision, Session.ExportJson())) RebuildDocumentViews();
            UpdateInspector();
            _multiAdorner.Invalidate();
        });
    }
    public void Fit()
    {
        var screen = Session.ActiveScreen;
        float width = _workspace.Size.X > 100 ? _workspace.Size.X : 900;
        float height = _workspace.Size.Y > 100 ? _workspace.Size.Y : 550;
        _canvas.ZoomScale = Math.Clamp(Math.Min((width - 40) / screen.Width, (height - 40) / screen.Height), 0.15f, 4);
        _canvas.PanOffset = new Vector2(20, 20); _canvas.ApplyTransforms(); _canvas.Invalidate();
        Status($"{screen.Width:0} × {screen.Height:0} · {_canvas.ZoomScale:P0} · Middle-drag to pan; Ctrl+wheel to zoom");
    }
    private void Zoom(float factor)
    {
        _canvas.ZoomScale = Math.Clamp(_canvas.ZoomScale * factor, 0.15f, 4);
        _canvas.ApplyTransforms(); _canvas.Invalidate();
        UpdateStudioState();
        Status($"Zoom {_canvas.ZoomScale:P0}");
    }
    public void CopySelection()
    {
        _clipboard = _selection.Selection.OfType<HmiControl>().Select(c => c.CaptureDefinition()).ToList();
        var identities = _clipboard.ToDictionary(e => e.Id, e => e.Id, StringComparer.Ordinal);
        _clipboardLinks = Session.ActiveScreen.Links.Where(l => identities.ContainsKey(l.Source.ElementId) && identities.ContainsKey(l.Target.ElementId)).Select(l => l.Copy()).ToList();
        Status($"Copied {_clipboard.Count} component(s) and {_clipboardLinks.Count} internal link(s).");
    }
    public void PasteSelection()
    {
        DesignCommand(() =>
        {
            if (_clipboard.Count == 0) return;
            var groups = new Dictionary<string, string>(StringComparer.Ordinal);
            var identities = new Dictionary<string, string>(StringComparer.Ordinal);
            var elements = _clipboard.Select(source =>
            {
                var copy = source.Copy(newIdentity: true); copy.X += 20; copy.Y += 20; copy.IsLocked = false; copy.Name += " copy";
                identities.Add(source.Id, copy.Id);
                if (copy.Group.Length > 0)
                {
                    if (!groups.TryGetValue(copy.Group, out var id)) groups[copy.Group] = id = Guid.NewGuid().ToString("N");
                    copy.Group = id;
                }
                return copy;
            }).ToList();
            _restoreSelection = elements.Select(e => e.Id).ToArray();
            var links = HmiDiagram.CopyInternalLinks(_clipboardLinks, identities);
            foreach (var link in links)
            {
                link.IsLocked = false;
                link.Waypoints = HmiRouteWaypoints.Translate(link.Waypoints, 20, 20);
            }
            Session.Edit("Paste components and internal links", p =>
            {
                var screen = p.Screens.Single(s => s.Id == Session.ActiveScreenId);
                screen.Elements.AddRange(elements); screen.Links.AddRange(links);
            });
        });
    }
    private void GroupSelection(bool grouped)
    {
        var ids = _selection.Selection.OfType<HmiControl>().Where(c => !c.IsDesignLocked).Select(c => c.CaptureDefinition().Id).ToHashSet();
        string group = grouped ? Guid.NewGuid().ToString("N") : "";
        DesignCommand(() => Session.Edit(grouped ? "Group components" : "Ungroup components", p =>
        {
            foreach (var element in p.Screens.Single(s => s.Id == Session.ActiveScreenId).Elements.Where(e => ids.Contains(e.Id))) element.Group = group;
        }));
    }
    private void ToggleLock()
    {
        var selected = _selection.Selection.OfType<HmiControl>().Select(c => c.CaptureDefinition()).ToArray();
        var ids = selected.Select(e => e.Id).ToHashSet();
        bool locked = selected.Any(e => !e.IsLocked);
        DesignCommand(() => Session.Edit(locked ? "Lock components" : "Unlock components", p =>
        {
            foreach (var element in p.Screens.Single(s => s.Id == Session.ActiveScreenId).Elements.Where(e => ids.Contains(e.Id))) element.IsLocked = locked;
        }));
    }
    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        for (var focused = InputSystem.FocusedElement; focused != null; focused = focused.Parent as FrameworkElement)
            if (focused is TextBox or RichEditBox or PasswordBox or VirtualizedCodeEditor) { base.OnKeyDown(e); return; }
        if (IsPreviewing) { base.OnKeyDown(e); return; }
        bool control = InputSystem.Current.IsControlPressed;
        float step = InputSystem.Current.IsShiftPressed ? 10 : 1;
        Action? action = e.Key switch
        {
            Silk.NET.Input.Key.Z when control => Session.Undo,
            Silk.NET.Input.Key.Y when control => Session.Redo,
            Silk.NET.Input.Key.A when control => _selection.SelectAll,
            Silk.NET.Input.Key.C when control => CopySelection,
            Silk.NET.Input.Key.V when control => PasteSelection,
            Silk.NET.Input.Key.L when control && InputSystem.Current.IsShiftPressed => BeginWaypointPlacement,
            Silk.NET.Input.Key.L when control => BeginDiagramConnection,
            Silk.NET.Input.Key.X when control => () => { CopySelection(); _selection.Delete(); },
            Silk.NET.Input.Key.D when control => () => { CopySelection(); PasteSelection(); },
            Silk.NET.Input.Key.Delete => DeleteDesignSelection,
            Silk.NET.Input.Key.Escape => () => { CancelRouteEdit(); CancelDiagramConnection(); },
            Silk.NET.Input.Key.Left => () => NudgeDesignSelection(-step, 0),
            Silk.NET.Input.Key.Right => () => NudgeDesignSelection(step, 0),
            Silk.NET.Input.Key.Up => () => NudgeDesignSelection(0, -step),
            Silk.NET.Input.Key.Down => () => NudgeDesignSelection(0, step),
            _ => null
        };
        if (action != null) { Guard(action); e.Handled = true; return; }
        base.OnKeyDown(e);
    }
    public void Dispose()
    {
        if (_disposed) return;
        StopPreview(); _disposed = true;
        Session.Changed -= OnSessionChanged; Session.ScreenChanged -= OnScreenChanged;
        _canvas.CanvasModifying -= OnCanvasModifying; _canvas.CanvasModified -= OnCanvasModified; _canvas.SelectionChanged -= OnCanvasSelectionChanged;
        _selection.SelectionChanged -= OnModelSelectionChanged;
        _canvas.PortPicked = null; _canvas.LinkPicked = null; _canvas.GeometryPreviewChanged = null;
        _canvas.RoutePointerPressed = null; _canvas.RoutePointerMoved = null;
        _canvas.RoutePointerReleased = null; _canvas.RoutePointerCanceled = null;
        DiagramLayer.Clear();
        _canvas.ViewportChanged -= UpdateStudioState;
        _multiAdorner.Dispose();
        _alarmConsole.Dispose();
    }
}
