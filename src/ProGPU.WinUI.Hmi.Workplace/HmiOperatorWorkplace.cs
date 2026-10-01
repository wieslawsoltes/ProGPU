using System.Globalization;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.WinUI.Hmi.Workplace;

/// <summary>
/// Standalone DCS-style operator workplace with persistent alarm context and linked object aspects.
/// This assembly does not depend on the designer or industrial transport assemblies.
/// </summary>
public sealed partial class HmiOperatorWorkplace : Grid, IDisposable
{
    private readonly TtfFont? _font;
    private readonly HmiProject _project;
    private readonly bool _ownsSession;
    private readonly Grid _deck = new();
    private readonly HmiRuntimeViewport _viewport = new();
    private readonly Grid _main = new();
    private FrameworkElement _explorerPane = null!, _faceplatePane = null!;
    private bool? _explorerOverride;
    private bool _showFaceplate = true;
    public bool IsPlantExplorerVisible => _explorerPane.Visibility == Visibility.Visible;
    public bool IsFaceplateVisible => _faceplatePane.Visibility == Visibility.Visible;
    private readonly StackPanel _explorer = new();
    private ScrollViewer? _pinScroller;
    private readonly StackPanel _pins = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _displayTabs = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _faceplate = new();
    private readonly TextBlock _mode, _status, _breadcrumb, _alarmSummary;
    private readonly TextBox _objectSearch;
    private readonly List<(Button Button, Func<bool> Enabled)> _commands = [];
    private readonly List<(HmiWorkplaceView View, Button Button)> _viewButtons = [];
    private readonly List<(string ScreenId, Button Button)> _screenButtons = [];
    private readonly List<(HmiAlarmSeverity Severity, TextBlock Label)> _alarmCounters = [];
    private HmiScreenView? _screen;
    private HmiObjectOutline? _outline;
    private HmiColorScheme _scheme;
    private bool _disposed;
    private string? _selectedAlarm;
    private string? _faceplateIdentity;
    private HmiObjectAddress[] _shownPins = [];
    private readonly Dictionary<string, Brush> _resourceReferences = new(StringComparer.Ordinal);
    private bool _buildingTables;
    private string _message = "Ready. No equipment connected.";

    public HmiWorkplaceSession Session { get; }
    public HmiScreenView? ProcessView => _screen;
    public bool FitToViewport { get => _viewport.FitToViewport; set => _viewport.FitToViewport = value; }
    public event Action? EngineeringRequested;
    public event Action<string>? Error;
    public HmiColorScheme ColorScheme
    {
        get => _scheme;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _scheme = value; HmiWorkplaceTheme.Apply(this, value);
            if (_screen != null) _screen.ColorScheme = value;
            foreach (var control in _trendControls) control.ColorScheme = value;
            if (_faceplateControl != null) _faceplateControl.ColorScheme = value;
            RefreshOutline(); InvalidateTree(this);
        }
    }

    public HmiOperatorWorkplace(HmiProject project, TtfFont? font = null) : this(new HmiWorkplaceSession(project), font, true) { }
    public HmiOperatorWorkplace(HmiWorkplaceSession session, TtfFont? font = null) : this(session, font, false) { }
    private HmiOperatorWorkplace(HmiWorkplaceSession session, TtfFont? font, bool ownsSession)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session)); _ownsSession = ownsSession;
        _project = session.GetProject(); _font = font ?? PopupService.DefaultFont;
        HmiWorkplaceTheme.Apply(this, HmiColorScheme.Light);
        Background = R("Background");
        RowDefinitions.Add(new GridLength(36)); RowDefinitions.Add(new GridLength(48));
        RowDefinitions.Add(new GridLength(38)); RowDefinitions.Add(new GridLength(34));
        RowDefinitions.Add(GridLength.Star(1)); RowDefinitions.Add(new GridLength(25));
        var identity = new Grid { Background = R("Header"), Padding = new Thickness(16, 0) };
        identity.ColumnDefinitions.Add(GridLength.Star(1)); identity.ColumnDefinitions.Add(GridLength.Auto);
        identity.AddChild(Text("PROGPU  CONTROL SYSTEMS    /    " + _project.Name, 12));
        _mode = Text("", 10, "Muted"); identity.AddChild(_mode); SetColumn(_mode, 1); AddChild(identity);

        var alarmBand = new Grid { Background = R("Surface"), Padding = new Thickness(12, 4) };
        alarmBand.ColumnDefinitions.Add(GridLength.Auto); alarmBand.ColumnDefinitions.Add(GridLength.Star(1)); alarmBand.ColumnDefinitions.Add(GridLength.Auto);
        var counters = Row();
        foreach (var severity in new[] { HmiAlarmSeverity.Critical, HmiAlarmSeverity.Warning, HmiAlarmSeverity.Information })
        {
            var label = Text("", 12); _alarmCounters.Add((severity, label));
            var s = severity; var button = Button("", () => { _severity = s; Session.Show(HmiWorkplaceView.Alarms); RefreshAlarms(); }, "Filter the alarm list by priority");
            button.Content = label; button.Width = 90; button.Height = 38; counters.AddChild(button);
        }
        alarmBand.AddChild(counters);
        _alarmSummary = Text("", 12); _alarmSummary.Margin = new Thickness(14, 0); alarmBand.AddChild(_alarmSummary); SetColumn(_alarmSummary, 1);
        var all = Button("Alarm list", () => { _severity = null; Session.Show(HmiWorkplaceView.Alarms); RefreshAlarms(); });
        alarmBand.AddChild(all); SetColumn(all, 2); AddChild(alarmBand); SetRow(alarmBand, 1);

        var toolbar = Row(); toolbar.Margin = new Thickness(10, 3);
        toolbar.AddChild(Button("‹", Session.Back, "Previous aspect", () => Session.CanGoBack));
        toolbar.AddChild(Button("›", Session.Forward, "Next aspect", () => Session.CanGoForward));
        toolbar.AddChild(Button("Home", () => Session.Navigate(_project.StartScreenId)));
        foreach (var view in Enum.GetValues<HmiWorkplaceView>())
        {
            var selected = view; var button = Button(view.ToString(), () => Session.Show(selected));
            toolbar.AddChild(button); _viewButtons.Add((view, button));
        }
        toolbar.AddChild(Button("Plant tree", () => { _explorerOverride = !IsPlantExplorerVisible; InvalidateMeasure(); }, "Show/hide the plant explorer"));
        toolbar.AddChild(Button("Faceplate", () => { _showFaceplate = !_showFaceplate; InvalidateMeasure(); }, "Show/hide object aspects"));
        toolbar.AddChild(Button("Fit / 1:1", () => FitToViewport = !FitToViewport));
        toolbar.AddChild(Button("Simulate", Session.StartSimulation, "Start local simulation, not a process connection", () => Session.Mode == HmiWorkplaceMode.Offline));
        toolbar.AddChild(Button("Hold / Run", () => Session.SetHold(!Session.IsHeld), "Pause or resume logical simulation time", () => Session.Mode == HmiWorkplaceMode.Simulation));
        toolbar.AddChild(Button("Step", () => Session.Step(TimeSpan.FromMilliseconds(100)), "Advance held simulation by 100 ms", () => Session.Mode == HmiWorkplaceMode.Simulation && Session.IsHeld));
        toolbar.AddChild(Button("Stop", Session.StopSimulation, "Stop simulation and cancel command review", () => Session.Mode == HmiWorkplaceMode.Simulation));
        toolbar.AddChild(Button("Engineering", () => EngineeringRequested?.Invoke()));
        AddChild(HorizontalScroll(toolbar)); SetRow(Children[^1], 2);
        _displayTabs.Margin = new Thickness(10, 1);
        foreach (var screen in _project.Screens)
        {
            string id = screen.Id; var button = Button(screen.Name, () => Session.Navigate(id));
            _displayTabs.AddChild(button); _screenButtons.Add((id, button));
        }
        AddChild(HorizontalScroll(_displayTabs)); SetRow(Children[^1], 3);

        _main.ColumnDefinitions.Add(new GridLength(215)); _main.ColumnDefinitions.Add(GridLength.Star(1)); _main.ColumnDefinitions.Add(new GridLength(305));
        var explorer = new Grid { Background = R("Surface"), Padding = new Thickness(10, 6) };
        _explorerPane = explorer;
        explorer.RowDefinitions.Add(new GridLength(26)); explorer.RowDefinitions.Add(new GridLength(38)); explorer.RowDefinitions.Add(GridLength.Star(1));
        explorer.AddChild(Text("PLANT EXPLORER", 10, "Muted"));
        _objectSearch = Input("Find object / tag…"); _objectSearch.TextChanged += (_, _) => BuildExplorer();
        explorer.AddChild(_objectSearch); SetRow(_objectSearch, 1);
        var explorerScroll = new ScrollViewer { Content = _explorer, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        explorer.AddChild(explorerScroll); SetRow(explorerScroll, 2); _main.AddChild(explorer);
        var center = new Grid(); center.RowDefinitions.Add(new GridLength(30)); center.RowDefinitions.Add(GridLength.Star(1)); center.RowDefinitions.Add(GridLength.Auto);
        _breadcrumb = Text("", 11, "Muted"); _breadcrumb.Margin = new Thickness(12, 0); center.AddChild(_breadcrumb);
        _deck.Margin = new Thickness(9, 0, 9, 6); _deck.AddChild(_viewport);
        BuildAspectViews(); center.AddChild(_deck); SetRow(_deck, 1);
        _pinScroller = HorizontalScroll(_pins); _pinScroller.Height = 34; _pinScroller.Visibility = Visibility.Collapsed; center.AddChild(_pinScroller); SetRow(_pinScroller, 2);
        _main.AddChild(center); SetColumn(center, 1);
        var faceScroll = new ScrollViewer { Content = _faceplate, Background = R("Surface"), Padding = new Thickness(12, 6),
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _faceplatePane = faceScroll;
        _main.AddChild(faceScroll); SetColumn(faceScroll, 2); AddChild(_main); SetRow(_main, 4);
        _status = Text("", 10, "Muted"); _status.Margin = new Thickness(12, 0); AddChild(_status); SetRow(_status, 5);
        Session.NavigationChanged += OnNavigation; Session.StateChanged += OnState;
        Session.TagsChanged += OnTags; Session.AlarmsChanged += OnAlarms;
        Unloaded += OnUnloaded;
        BuildExplorer(); OnNavigation(); OnState(); RefreshAlarms();
    }

    private Brush R(string role)
    {
        if (!_resourceReferences.TryGetValue(role, out var brush)) _resourceReferences.Add(role, brush = HmiWorkplaceTheme.Reference(this, role));
        return brush;
    }
    private TextBlock Text(string text, float size = 12, string role = "Text") => new()
    {
        Text = text, Font = _font, FontSize = size, Foreground = R(role), TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
    };
    private TextBox Input(string placeholder) => new() { Font = _font, FontSize = 12, Height = 30, PlaceholderText = placeholder, Margin = new Thickness(0, 2) };
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal };
    private static ScrollViewer HorizontalScroll(FrameworkElement content) => new()
    {
        Content = content, Height = 32, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private Button Button(string text, Action action, string? tooltip = null, Func<bool>? enabled = null)
    {
        var button = new Button { Content = Text(text, 11), Font = _font, Height = 29, Padding = new Thickness(10, 2), Margin = new Thickness(2, 0),
            Background = R("Surface"), BorderBrush = R("Border"), BorderThickness = new Thickness(1), CornerRadius = 2 };
        button.Click += (_, _) => Guard(action);
        if (tooltip != null) ToolTipService.SetToolTip(button, tooltip);
        if (enabled != null) _commands.Add((button, enabled));
        return button;
    }
    private void Guard(Action action)
    {
        if (_disposed) return;
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or FormatException or KeyNotFoundException)
        { SetMessage(ex.Message, true); }
    }
    private void SetMessage(string message, bool error = false)
    {
        _message = message; RefreshStatus(); if (error) Error?.Invoke(message);
    }
    private void BuildExplorer()
    {
        _explorer.Children.Clear(); _explorer.Width = 175;
        string filter = _objectSearch.Text;
        int shown = 0;
        foreach (var screen in _project.Screens)
        {
            string id = screen.Id;
            var entries = screen.Elements.Where(e => !e.IsHidden && (e.Tag.Length > 0 || HmiSymbolTraits.IsEquipment(e.Symbol)) &&
                (filter.Length == 0 || (e.Name + " " + e.Label + " " + e.Tag).Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (filter.Length > 0 && entries.Length == 0) continue;
            var section = Button("▾  " + screen.Name, () => Session.Navigate(id)); section.Margin = new Thickness(0, 7, 0, 3);
            section.ClipBounds = new Rect(0, 0, 175, 29); section.Width = 175; ((TextBlock)section.Content!).Width = 149; section.HorizontalAlignment = HorizontalAlignment.Stretch; _explorer.AddChild(section);
            foreach (var element in entries)
            {
                if (++shown > 256) break;
                string objectId = element.Id;
                var button = Button(element.Label.Length > 0 ? element.Label : element.Name, () => Session.Navigate(id, objectId), element.Name + "\n" + element.Tag);
                button.ClipBounds = new Rect(0, 0, 171, 29); button.Height = 29; button.Width = 171; ((TextBlock)button.Content!).Width = 145; button.Margin = new Thickness(4, 1, 0, 1); button.BorderThickness = new Thickness(0);
                button.HorizontalAlignment = HorizontalAlignment.Stretch; _explorer.AddChild(button);
            }
            if (shown > 256) { _explorer.AddChild(Text("More objects: narrow the search", 10, "Muted")); break; }
        }
    }
    private void OnNavigation()
    {
        if (_disposed) return;
        var location = Session.Location;
        _exportPanel.Visibility = Visibility.Collapsed;
        if (_screen?.ScreenId != location.ScreenId)
        {
            if (_screen != null) { _screen.ObjectSelected -= OnObjectPicked; _viewport.Screen = null; _screen.Dispose(); }
            _screen = new HmiScreenView(_project, Session.Runtime, location.ScreenId, _font) { ColorScheme = ColorScheme, ObjectSelectionEnabled = true };
            _screen.DiagramLayer.GraphicStyle = HmiGraphicStyle.HighPerformance;
            _screen.ObjectSelected += OnObjectPicked;
            _screen.ScreenChanged += id => { if (!_disposed && Session.Location.ScreenId != id) Session.Navigate(id); };
            _outline = new HmiObjectOutline(); _screen.AddChild(_outline);
            _viewport.Screen = _screen;
        }
        var target = _aspectViews[location.View];
        foreach (var view in _aspectViews.Values) view.Visibility = ReferenceEquals(view, target) ? Visibility.Visible : Visibility.Collapsed;
        _breadcrumb.Text = "NORTHWATER  /  " + _project.Screens.Single(s => s.Id == location.ScreenId).Name.ToUpperInvariant() +
            "  /  " + (location.ElementId == null ? location.View.ToString().ToUpperInvariant() : "OBJECT ASPECTS");
        foreach (var item in _viewButtons) item.Button.BorderBrush = R(item.View == location.View ? "Accent" : "Border");
        foreach (var item in _screenButtons) item.Button.BorderBrush = R(item.ScreenId == location.ScreenId ? "Accent" : "Border");
        BuildFaceplate(); RefreshOutline();
        if (location.View == HmiWorkplaceView.Alarms) RefreshAlarms();
        if (location.View == HmiWorkplaceView.Trends) BuildTrends();
        if (location.View == HmiWorkplaceView.Events) RefreshEvents();
        if (location.View == HmiWorkplaceView.System) RefreshSystem();
    }
    private void OnObjectPicked(HmiElement element)
    {
        if (element.Action.Kind == HmiActionKind.Navigate) Session.Navigate(element.Action.Target);
        else Session.Navigate(Session.Location.ScreenId, element.Id);
    }
    private void RefreshOutline()
    {
        if (_outline == null) return;
        var control = _screen?.Controls.FirstOrDefault(c => c.ElementId == Session.Location.ElementId && c.Visibility == Visibility.Visible);
        _outline.Update(control == null ? null : new Rect(Canvas.GetLeft(control), Canvas.GetTop(control), control.Width, control.Height), (Brush)Resources["Dcs.Accent"]!);
    }
    private void OnState()
    {
        if (_disposed) return;
        _mode.Text = Session.Mode switch { HmiWorkplaceMode.Simulation => Session.IsHeld ? "LOCAL SIMULATION / HELD" : "LOCAL SIMULATION / RUNNING", HmiWorkplaceMode.ExternalReadOnly => "HOST TELEMETRY / READ ONLY", _ => "OFFLINE / INITIAL OR RETAINED VALUES" };
        foreach (var (button, enabled) in _commands) button.IsEnabled = enabled();
        if (!_shownPins.SequenceEqual(Session.PinnedObjects))
        {
            _shownPins = Session.PinnedObjects.ToArray(); _pins.Children.Clear();
            if (_pinScroller != null) _pinScroller.Visibility = _shownPins.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var pin in Session.PinnedObjects)
            {
                var address = pin; var e = Session.GetObject(pin);
                _pins.AddChild(Button("◆ " + e.Label, () => Session.Navigate(address.ScreenId, address.ElementId)));
            }
        }
        RefreshReview(); RefreshFaceplate(); RefreshStatus();
    }
    private void OnTags(IReadOnlyList<string> tags)
    {
        if (_disposed) return;
        RefreshFaceplate(); RefreshTrends(); RefreshAlarmSummary(); RefreshOutline(); RefreshStatus();
    }
    private void OnAlarms()
    {
        if (_disposed) return;
        RefreshAlarms(); if (Session.Location.View == HmiWorkplaceView.Events) RefreshEvents();
    }
    private void RefreshStatus()
    {
        if (_disposed) return;
        _status.Text = $"{_mode.Text}  |  {Session.Runtime.Now:yyyy-MM-dd HH:mm:ss} UTC  |  {_project.Tags.Count} tags  |  {Session.Runtime.EvictedAlarmEvents} older alarm events evicted  |  {_message}";
    }
    private static void InvalidateTree(Visual visual)
    {
        visual.Invalidate();
        if (visual is ContainerVisual container) foreach (var child in container.Children) InvalidateTree(child);
    }
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (!_disposed && _screen == null) OnNavigation();
        bool explorer = _explorerOverride ?? (!float.IsFinite(availableSize.X) || availableSize.X >= 1200);
        SetPane(_explorerPane, 0, explorer, 215);
        SetPane(_faceplatePane, 2, _showFaceplate, 305);
        return base.MeasureOverride(availableSize);
    }
    private void SetPane(FrameworkElement pane, int column, bool visible, float width)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (pane.Visibility == visibility && _main.ColumnDefinitions[column].Width.Value == (visible ? width : 0)) return;
        pane.Visibility = visibility;
        _main.ColumnDefinitions[column].Width = new GridLength(visible ? width : 0);
        _main.InvalidateMeasure(); _main.InvalidateArrange();
    }
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        if (Session.Mode == HmiWorkplaceMode.Simulation) Session.StopSimulation();
        Session.CancelReview();
        // HmiScreenView owns subscriptions only while attached. Preserve session/view navigation
        // but create a new projection if this reusable workplace is reattached later.
        if (_screen != null) { _screen.ObjectSelected -= OnObjectPicked; _screen.Dispose(); _screen = null; }
        _viewport.Screen = null; InvalidateMeasure();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        Session.NavigationChanged -= OnNavigation; Session.StateChanged -= OnState; Session.TagsChanged -= OnTags; Session.AlarmsChanged -= OnAlarms;
        Unloaded -= OnUnloaded;
        if (_screen != null) { _screen.ObjectSelected -= OnObjectPicked; _screen.Dispose(); }
        _viewport.Screen = null;
        if (_ownsSession) Session.Dispose();
        EngineeringRequested = null; Error = null;
    }
}
