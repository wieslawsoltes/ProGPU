using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.WinUI.Designer;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private HmiColorScheme _colorScheme = HmiColorScheme.Light;
    private readonly List<(Button Button, Func<bool> Enabled)> _studioCommands = [];
    private readonly List<HmiCommandIcon> _commandIcons = [];
    private readonly List<HmiSymbolIcon> _paletteIcons = [];
    private readonly List<(FrameworkElement Element, HmiBrushRole Role)> _studioSurfaces = [];
    private readonly List<TextBlock> _studioLabels = [];
    private readonly List<MenuFlyout> _studioMenus = [];
    private Grid? _fileLocationRow;
    private TextBlock? _modeLabel;
    private TextBlock? _canvasContext;
    private TextBlock? _runCaption;
    private TextBlock? _paletteSummary;
    private Button? _runButton;
    private Pivot? _libraryTabs;
    private string _paletteQuery = "";
    private bool _studioInitialized;

    /// <summary>Instance-scoped editor and runtime palette; never changes the hosting application's theme.</summary>
    public HmiColorScheme ColorScheme
    {
        get => _colorScheme;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_colorScheme == value) return;
            _colorScheme = value;
            ApplyStudioTheme();
        }
    }

    public bool AreDataPanelsVisible => _dataArea.Visibility == Visibility.Visible;

    public void SetDataPanelsVisible(bool visible)
    {
        _dataArea.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InvalidateMeasure();
    }

    public void SetDataPanelHeight(float height)
    {
        if (!float.IsFinite(height) || height is < 140 or > 600) throw new ArgumentOutOfRangeException(nameof(height));
        _dataArea.Height = height;
        InvalidateMeasure();
    }

    private FrameworkElement BuildStudioHeader()
    {
        var header = new Grid { Name = "HmiStudioHeader" };
        header.RowDefinitions.Add(new GridLength(44));
        header.RowDefinitions.Add(new GridLength(40));
        header.RowDefinitions.Add(GridLength.Auto);
        header.RowDefinitions.Add(GridLength.Auto);
        _studioSurfaces.Add((header, HmiBrushRole.Surface));
        var identity = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        identity.ColumnDefinitions.Add(new GridLength(144));
        identity.ColumnDefinitions.Add(GridLength.Star(1));
        identity.ColumnDefinitions.Add(new GridLength(154));
        var brand = StudioText("PROGPU  /  HMI", 13);
        identity.AddChild(brand);
        _title.FontSize = 12;
        _title.Margin = new Thickness(12, 0);
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.TextWrapping = TextWrapping.NoWrap;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        identity.AddChild(_title); SetColumn(_title, 1);
        _modeLabel = StudioText("DESIGN  ·  OFFLINE", 10);
        _modeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        identity.AddChild(_modeLabel); SetColumn(_modeLabel, 2);
        header.AddChild(identity);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Padding = new Thickness(8, 3) };
        tools.AddChild(StudioMenu("File", [
            ("New project", () => ConfirmReplace("new", () => Session.Open(new HmiProject { Screens = [new HmiScreen { Id = "overview", Name = "Overview" }] }))),
            ("Water treatment sample", () => ConfirmReplace("demo", () => Session.Open(HmiDemoProject.Create()))),
            ("Graphics convention sample", () => ConfirmReplace("conventions-demo", () => Session.Open(HmiConventionsProject.Create()))),
            ("Process studio sample", () => ConfirmReplace("studio-demo", () => Session.Open(HmiShowcaseProject.Create()))),
            ("Open / save location…", ToggleFileLocation),
            ("Save project", () => { if (string.IsNullOrWhiteSpace(_filePath.Text)) ToggleFileLocation(); else _ = SaveFileAsync(_filePath.Text); }),
            ("Validate project", () => { HmiProjectSerializer.Validate(Session.GetProject()); Status("The current project is valid."); })
        ]));
        tools.AddChild(StudioMenu("Edit", [
            ("Edit caption in place (F2)", () => BeginLabelEdit()),
            ("Copy graphic format", CopyGraphicFormat), ("Paste graphic format", PasteGraphicFormat),
            ("Undo", () => DesignCommand(Session.Undo)), ("Redo", () => DesignCommand(Session.Redo)),
            ("Selection tool (V)", CancelCanvasAuthoring),
            ("Draw last component", () => BeginComponentPlacement(_lastPlacementSymbol)),
            ("Select all", () => DesignCommand(_selection.SelectAll)), ("Copy", CopySelection),
            ("Cut", () => DesignCommand(() => { CopySelection(); _selection.Delete(); })),
            ("Paste", PasteSelection), ("Duplicate", () => { CopySelection(); PasteSelection(); }),
            ("Delete selection", () => DesignCommand(DeleteDesignSelection)),
            ("Group selection", () => GroupSelection(true)), ("Ungroup selection", () => GroupSelection(false)),
            ("Lock / unlock selection", ToggleLock)
        ]));
        var arrange = new List<(string, Action)>();
        foreach (var alignment in Enum.GetValues<DesignerAlignment>())
        {
            var selected = alignment;
            arrange.Add(("Align " + alignment.ToString().ToLowerInvariant(), () => DesignCommand(() => _selection.Align(selected))));
        }
        arrange.AddRange([
            ("Distribute horizontally", () => DesignCommand(() => _selection.Distribute(true))),
            ("Distribute vertically", () => DesignCommand(() => _selection.Distribute(false))),
            ("Match width to primary", () => DesignCommand(() => _selection.MatchSize(true, false))),
            ("Match height to primary", () => DesignCommand(() => _selection.MatchSize(false, true))),
            ("Match size to primary", () => DesignCommand(() => _selection.MatchSize(true, true))),
            ("Bring to front", () => DesignCommand(() => _selection.Reorder(true))),
            ("Send to back", () => DesignCommand(() => _selection.Reorder(false)))
        ]);
        tools.AddChild(StudioMenu("Arrange", arrange));
        tools.AddChild(StudioMenu("Diagram", [
            ("Connect nozzles (click two ports)", BeginDiagramConnection),
            ("Cancel nozzle connection (Escape)", CancelDiagramConnection),
            ("Add waypoint to selected link", BeginWaypointPlacement),
            ("Remove selected waypoint (Delete)", RemoveSelectedWaypoint),
            ("Release straight segments (keep pins)", ReleaseStraightSegments),
            ("Clear waypoints / automatic route", ClearSelectedWaypoints),
            ("Reverse selected link", ReverseSelectedLink),
            ("Delete selected link", DeleteSelectedLink),
            ("Show / hide nozzles", () => DiagramLayer.ShowPortHandles = !DiagramLayer.ShowPortHandles)
        ]));
        tools.AddChild(StudioMenu("Format", [
            ("Process graphics", () => SetGraphicStyle(HmiGraphicStyle.Process)),
            ("High-performance graphics", () => SetGraphicStyle(HmiGraphicStyle.HighPerformance)),
            ("Schematic graphics", () => SetGraphicStyle(HmiGraphicStyle.Schematic)),
            ("Rotate glyph clockwise", RotateSelectedGraphics), ("Mirror glyph horizontally", MirrorSelectedGraphics),
            ("Copy visual format", CopyGraphicFormat), ("Paste visual format", PasteGraphicFormat)
        ]));
        tools.AddChild(StudioMenu("View", [
            ("Fit screen", Fit), ("Zoom to selection", ZoomToSelection), ("Zoom in", () => Zoom(1.25f)), ("Zoom out", () => Zoom(0.8f)),
            ("Toggle design grid", ToggleDesignGrid), ("Toggle snapping", ToggleDesignSnap),
            ("Toggle rulers", () => { _canvas.ShowRulers = !_canvas.ShowRulers; _canvas.Invalidate(); }),
            ("Preview initial state (read only)", () => StartPreview(allowLocalWrites: false, automaticTicks: false)),
            ("Toggle runtime fit / 1:1", () => _previewViewport.FitToViewport = !_previewViewport.FitToViewport),
            ("Toggle data panels", ToggleDataPanels),
            ("Compact data panels", () => SetDataPanelHeight(190)),
            ("Expanded data panels", () => SetDataPanelHeight(360)),
            ("Light palette", () => ColorScheme = HmiColorScheme.Light),
            ("Dark palette", () => ColorScheme = HmiColorScheme.Dark),
            ("High-contrast palette", () => ColorScheme = HmiColorScheme.HighContrast)
        ]));
        tools.AddChild(StudioSeparator());
        tools.AddChild(StudioButton("Save", "Save project", () => { if (string.IsNullOrWhiteSpace(_filePath.Text)) ToggleFileLocation(); else _ = SaveFileAsync(_filePath.Text); }));
        tools.AddChild(StudioButton("Undo", "Undo", () => DesignCommand(Session.Undo), () => !IsPreviewing && Session.CanUndo));
        tools.AddChild(StudioButton("Redo", "Redo", () => DesignCommand(Session.Redo), () => !IsPreviewing && Session.CanRedo));
        tools.AddChild(StudioSeparator());
        _selectionToolButton = StudioButton("Select", "Selection tool (V). Drag empty canvas: window/crossing. Shift adds; Ctrl toggles; Ctrl+Shift removes.", CancelCanvasAuthoring, () => !IsPreviewing);
        _placementToolButton = StudioButton("Draw", "Draw the selected component type, or the last used type. Shift preserves its aspect ratio; Alt bypasses grid snapping.",
            () => BeginComponentPlacement((_selection.Selection.LastOrDefault() as HmiControl)?.Symbol ?? _lastPlacementSymbol), () => !IsPreviewing);
        tools.AddChild(_selectionToolButton); tools.AddChild(_placementToolButton);
        tools.AddChild(StudioButton("Duplicate", "Duplicate selection", () => { CopySelection(); PasteSelection(); }, () => !IsPreviewing && _selection.Selection.Count > 0));
        tools.AddChild(StudioButton("Delete", "Delete selection", () => DesignCommand(DeleteDesignSelection), () => !IsPreviewing && (_selection.Selection.Count > 0 || SelectedLinkId != null)));
        tools.AddChild(StudioSeparator());
        tools.AddChild(StudioButton("Fit", "Fit screen", Fit));
        tools.AddChild(StudioButton("Minus", "Zoom out", () => Zoom(0.8f)));
        tools.AddChild(StudioButton("Plus", "Zoom in", () => Zoom(1.25f)));
        _linkToolButton = StudioButton("Link", "Connect two equipment nozzles (Ctrl+L)", () =>
        {
            if (IsConnectingDiagram) { CancelDiagramConnection(); UpdateStudioState(); }
            else BeginDiagramConnection();
        }, () => !IsPreviewing);
        tools.AddChild(_linkToolButton);
        tools.AddChild(StudioButton("Grid", "Toggle grid", ToggleDesignGrid));
        tools.AddChild(StudioButton("Snap", "Toggle snapping", ToggleDesignSnap));
        tools.AddChild(StudioButton("Panels", "Show / hide data panels", ToggleDataPanels));
        tools.AddChild(StudioSeparator());
        _runButton = StudioButton("Run", "Run simulation / stop runtime", () => { if (IsPreviewing) StopPreview(); else StartPreview(); });
        var runContent = new StackPanel { Orientation = Orientation.Horizontal };
        var runIcon = new HmiCommandIcon("Run"); _commandIcons.Add(runIcon);
        runIcon.Margin = new Thickness(0, 0, 6, 0);
        _runCaption = StudioText("Simulate", 11);
        runContent.AddChild(runIcon); runContent.AddChild(_runCaption);
        _runButton.Content = runContent; _runButton.Width = 102;
        tools.AddChild(_runButton);
        tools.AddChild(StudioButton("Pause", "Pause / resume simulation", TogglePause, () => IsPreviewing && _acquisition == null));
        tools.AddChild(StudioButton("Step", "Advance simulation 100 ms", () => AdvancePreview(TimeSpan.FromMilliseconds(100)), () => IsPreviewing && _acquisition == null));
        var toolScroll = new ScrollViewer { Content = tools, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        header.AddChild(toolScroll); SetRow(toolScroll, 1);

        _fileLocationRow = new Grid { Visibility = Visibility.Collapsed, Height = 38, Margin = new Thickness(12, 0) };
        _fileLocationRow.ColumnDefinitions.Add(GridLength.Star(1));
        _fileLocationRow.ColumnDefinitions.Add(GridLength.Auto); _fileLocationRow.ColumnDefinitions.Add(GridLength.Auto);
        _filePath.Width = float.NaN; _filePath.HorizontalAlignment = HorizontalAlignment.Stretch;
        _fileLocationRow.AddChild(_filePath);
        var open = Command("Open", () => ConfirmReplace("open:" + _filePath.Text, () => _ = OpenFileAsync(_filePath.Text)));
        var save = Command("Save", () => _ = SaveFileAsync(_filePath.Text));
        _fileLocationRow.AddChild(open); SetColumn(open, 1); _fileLocationRow.AddChild(save); SetColumn(save, 2);
        header.AddChild(_fileLocationRow); SetRow(_fileLocationRow, 2);
        var setup = BuildSetupNavigation();
        header.AddChild(setup); SetRow(setup, 3);
        return header;
    }

    private TextBlock StudioText(string text, float fontSize)
    {
        var label = Text(text, fontSize);
        label.Margin = new Thickness(0);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        _studioLabels.Add(label);
        return label;
    }
    private FrameworkElement StudioSeparator()
    {
        var separator = new Border { Width = 1, Height = 18, Margin = new Thickness(7, 6) };
        _studioSurfaces.Add((separator, HmiBrushRole.Border));
        return separator;
    }
    private Button StudioButton(string icon, string tooltip, Action action, Func<bool>? enabled = null)
    {
        var glyph = new HmiCommandIcon(icon); _commandIcons.Add(glyph);
        var button = new Button { Content = glyph, Width = 31, Height = 31, Padding = new Thickness(6), Margin = new Thickness(1, 0), CornerRadius = 5 };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => Guard(action);
        _studioCommands.Add((button, enabled ?? (() => true)));
        return button;
    }
    private Button StudioMenu(string text, IEnumerable<(string Text, Action Action)> commands)
    {
        var menu = new MenuFlyout();
        foreach (var command in commands)
        {
            var item = new MenuFlyoutItem { Text = command.Text, Font = _font };
            item.Click += (_, _) => Guard(command.Action);
            menu.Items.Add(item);
        }
        _studioMenus.Add(menu);
        return new Button { Content = StudioText(text, 12), Flyout = menu, Height = 31, Padding = new Thickness(10, 0), Margin = new Thickness(1, 0), CornerRadius = 4 };
    }
    private void ToggleFileLocation()
    {
        if (_fileLocationRow == null) return;
        _fileLocationRow.Visibility = _fileLocationRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        InvalidateMeasure();
    }
    private void ToggleDesignGrid() { _canvas.ShowGridLines = !_canvas.ShowGridLines; _canvas.Invalidate(); UpdateStudioState(); }
    private void ToggleDesignSnap() { _canvas.GridSnappingEnabled = !_canvas.GridSnappingEnabled; UpdateStudioState(); }

    private void InitializeStudio()
    {
        _studioInitialized = true;
        _canvasContext = StudioText("", 10);
        _canvasContext.Margin = new Thickness(12, 6);
        _canvasContext.VerticalAlignment = VerticalAlignment.Bottom;
        _canvasContext.HorizontalAlignment = HorizontalAlignment.Left;
        _canvasContext.IsHitTestVisible = false;
        _workspace.AddChild(_canvasContext);
        ApplyStudioTheme();
        UpdateStudioState();
    }
    private void ApplyStudioTheme()
    {
        foreach (var (key, role) in new (string Key, HmiBrushRole Role)[]
        {
            ("TextPrimary", HmiBrushRole.Text), ("TextSecondary", HmiBrushRole.Muted),
            ("PageBackground", HmiBrushRole.Workspace), ("CardBackground", HmiBrushRole.Surface),
            ("HeaderBackground", HmiBrushRole.Workspace), ("ControlBackground", HmiBrushRole.Surface),
            ("ControlBackgroundHover", HmiBrushRole.Body), ("ControlBorder", HmiBrushRole.Border),
            ("SelectionHighlight", HmiBrushRole.Body), ("SystemAccentColor", HmiBrushRole.Accent)
        }) Resources[key] = HmiThemeResources.GetBrush(ColorScheme, role);
        RequestedTheme = ColorScheme == HmiColorScheme.Light ? ElementTheme.Light : ElementTheme.Dark;
        Background = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Surface);
        _workspace.Background = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Workspace);
        _canvas.Background = HmiThemeResources.GetBrush(ColorScheme, HmiBrushRole.Workspace);
        _canvas.ColorScheme = ColorScheme;
        DiagramLayer.ColorScheme = ColorScheme;
        _canvas.DesignSurface.Background = null;
        _canvas.DocumentBackground = HmiThemeResources.GetBrush(ColorScheme, HmiBrushRole.Surface);
        foreach (var (element, role) in _studioSurfaces)
        {
            var brush = HmiThemeResources.GetReference(ColorScheme, role);
            if (element is Panel panel) panel.Background = brush;
            else if (element is Border border) border.Background = brush;
        }
        foreach (var label in _studioLabels) label.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Text);
        _title.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Text);
        _selectionLabel.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Text);
        _status.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Muted);
        foreach (var icon in _commandIcons) { icon.ColorScheme = ColorScheme; icon.Invalidate(); }
        foreach (var caption in _palette.Children.OfType<TextBlock>()) caption.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Muted);
        foreach (var icon in _paletteIcons) icon.ColorScheme = ColorScheme;
        foreach (var control in _canvas.DesignSurface.Children.OfType<HmiControl>()) control.ColorScheme = ColorScheme;
        foreach (var menu in _studioMenus)
            foreach (var item in menu.Items) item.RequestedTheme = RequestedTheme;
        if (_preview != null) _preview.ColorScheme = ColorScheme;
        RefreshGraphicInspector();
        ApplyAuthoringTheme();
        InvalidateStudioTree(this);
        _canvas.Invalidate(); Invalidate();
    }
    private static void InvalidateStudioTree(Visual visual)
    {
        visual.Invalidate();
        if (visual is ContainerVisual container)
            foreach (var child in container.Children) InvalidateStudioTree(child);
    }
    private void UpdateStudioState()
    {
        if (!_studioInitialized) return;
        RefreshWaypointAdorner();
        foreach (var command in _studioCommands) command.Button.IsEnabled = command.Enabled();
        if (_linkToolButton != null)
        {
            _linkToolButton.BorderBrush = HmiThemeResources.GetReference(ColorScheme, IsConnectingDiagram ? HmiBrushRole.Accent : HmiBrushRole.Border);
            _linkToolButton.BorderThickness = new Thickness(IsConnectingDiagram ? 2 : 1);
        }
        foreach (var button in _palettePlacementButtons) button.IsEnabled = !IsPreviewing;
        SetToolState(_selectionToolButton, !IsPlacingComponent && !IsConnectingDiagram && !IsEditingRoute);
        SetToolState(_placementToolButton, IsPlacingComponent);
        void SetToolState(Button? button, bool active)
        {
            if (button == null) return;
            button.BorderBrush = HmiThemeResources.GetReference(ColorScheme, active ? HmiBrushRole.Accent : HmiBrushRole.Border);
            button.BorderThickness = new Thickness(active ? 2 : 1);
        }
        bool live = _acquisition != null;
        _modeLabel!.Text = live ? "LIVE  ·  READ ONLY*" : IsPreviewing ? "SIMULATION  ·  LOCAL" : "DESIGN  ·  OFFLINE";
        ToolTipService.SetToolTip(_modeLabel, live ? "Acquisition is live. External commands require separate review and authenticated host authorization." : "No equipment connection is created by opening a project.");
        _modeLabel.Foreground = HmiThemeResources.GetReference(ColorScheme, live ? HmiBrushRole.Warning : HmiBrushRole.Muted);
        _runCaption!.Text = IsPreviewing ? "Stop" : "Simulate";
        _canvasContext!.Text = $"{Session.ActiveScreen.Name}  /  {Session.ActiveScreen.Width:0} × {Session.ActiveScreen.Height:0}   ·   {_canvas.ZoomScale:P0}   ·   {_selection.Selection.Count} selected   ·   Snap {(_canvas.GridSnappingEnabled ? "on" : "off")}";
        _canvasContext.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Muted);
        _canvasContext.Visibility = IsPreviewing ? Visibility.Collapsed : Visibility.Visible;
        if (_preview != null) _preview.ColorScheme = ColorScheme;
    }
}
