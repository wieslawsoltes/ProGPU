using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.Text;
using ProGPU.WinUI.Hmi.Workplace;

namespace ProGPU.WinUI.Hmi.Designer;

/// <summary>
/// Reusable standalone DCS studio. Operator UI is independently shippable;
/// engineering reuses the existing HmiDesignerHost and is constructed on demand.
/// Switching workplaces retires command reviews and stops local/live previews.
/// </summary>
public sealed class HmiDcsStudio : Grid, IDisposable
{
    private readonly TtfFont? _font;
    private readonly Grid _body = new();
    private readonly Button _operatorButton, _engineeringButton;
    private readonly TextBlock _mode;
    private HmiWorkplaceSession _session;
    private HmiOperatorWorkplace _operator;
    private HmiDesignerHost? _designer;
    private bool _operatorStale, _disposed;
    private int _generation = 1, _pendingGeneration;
    private System.Threading.Timer? _timer;
    private readonly bool _automaticTicks;
    private HmiColorScheme _scheme;
    private Func<HmiConnectionProfile, IHmiConnection>? _connectionFactory;
    public bool IsEngineering { get; private set; }
    public HmiOperatorWorkplace Operator => _operator;
    public HmiDesignerHost? Engineering => _designer;
    public new bool IsDirty => _designer?.Session.IsDirty ?? false;
    public event Action<string>? Error;

    /// <summary>Only passed to the original engineering commissioning UI. Never invoked on startup.</summary>
    public Func<HmiConnectionProfile, IHmiConnection>? ConnectionFactory
    {
        get => _connectionFactory;
        set { _connectionFactory = value; if (_designer != null) _designer.ConnectionFactory = value; }
    }
    public HmiColorScheme ColorScheme
    {
        get => _scheme;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _scheme = value; HmiWorkplaceTheme.Apply(this, value);
            _operator.ColorScheme = value; if (_designer != null) _designer.ColorScheme = value;
            UpdateMode(); Invalidate();
        }
    }
    public HmiDcsStudio(HmiProject? project = null, TtfFont? font = null, bool automaticTicks = true)
    {
        _font = font ?? PopupService.DefaultFont;
        _session = new HmiWorkplaceSession(project ?? HmiDcsProject.Create());
        _operator = new HmiOperatorWorkplace(_session, _font);
        _operator.EngineeringRequested += ShowEngineering; _operator.Error += OnError;
        HmiWorkplaceTheme.Apply(this, HmiColorScheme.Light);
        Background = HmiWorkplaceTheme.Reference(this, "Background");
        RowDefinitions.Add(new GridLength(36)); RowDefinitions.Add(GridLength.Star(1));
        var top = new Grid { Background = HmiWorkplaceTheme.Reference(this, "Header"), Padding = new Thickness(8, 3) };
        top.ColumnDefinitions.Add(GridLength.Auto); top.ColumnDefinitions.Add(GridLength.Star(1)); top.ColumnDefinitions.Add(GridLength.Auto);
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        _operatorButton = Command("Operator Workplace", ShowOperator);
        _engineeringButton = Command("Engineering Workplace", ShowEngineering);
        modes.AddChild(_operatorButton); modes.AddChild(_engineeringButton); top.AddChild(modes);
        _mode = new TextBlock { Font = _font, FontSize = 10, Text = "", Foreground = HmiWorkplaceTheme.Reference(this, "Muted"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        top.AddChild(_mode); SetColumn(_mode, 1);
        var themes = new StackPanel { Orientation = Orientation.Horizontal };
        themes.AddChild(Command("Light", () => ColorScheme = HmiColorScheme.Light));
        themes.AddChild(Command("Dark", () => ColorScheme = HmiColorScheme.Dark));
        themes.AddChild(Command("Contrast", () => ColorScheme = HmiColorScheme.HighContrast));
        top.AddChild(themes); SetColumn(themes, 2); AddChild(top);
        _body.AddChild(_operator); AddChild(_body); SetRow(_body, 1);
        _automaticTicks = automaticTicks;
        Loading += OnLoading; Unloaded += OnUnloaded;
        UpdateMode();

    }
    private Button Command(string text, Action action)
    {
        var b = new Button { Height = 29, Padding = new Thickness(10, 1), Margin = new Thickness(2, 0), CornerRadius = 2,
            BorderThickness = new Thickness(1), Background = HmiWorkplaceTheme.Reference(this, "Surface"),
            BorderBrush = HmiWorkplaceTheme.Reference(this, "Border"),
            Content = new TextBlock { Text = text, Font = _font, FontSize = 11, Foreground = HmiWorkplaceTheme.Reference(this, "Text") } };
        b.Click += (_, _) => { try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException) { OnError(e.Message); } };
        return b;
    }
    public void ShowEngineering()
    {
        CheckAlive(); if (IsEngineering) return;
        RetireTicks(); _session.StopSimulation();
        if (_designer == null)
        {
            _designer = new HmiDesignerHost(_session.GetProject(), _font) { ColorScheme = _scheme, ConnectionFactory = _connectionFactory };
            _designer.DiagramLayer.GraphicStyle = HmiGraphicStyle.HighPerformance;
            _designer.Error += OnError; _designer.Session.Changed += OnDesignChanged;
            _body.AddChild(_designer);
        }
        // Carry operator context into the existing editor without changing the document.
        var context = _session.Location;
        if (_designer.Session.GetProject().Screens.Any(s => s.Id == context.ScreenId))
        {
            _designer.Session.SelectScreen(context.ScreenId);
            if (context.ElementId != null)
                _designer.WorkspaceCanvas.SelectElement(_designer.WorkspaceCanvas.DesignSurface.Children
                    .OfType<HmiControl>().FirstOrDefault(c => c.ElementId == context.ElementId));
        }
        IsEngineering = true; _operator.Visibility = Visibility.Collapsed; _designer.Visibility = Visibility.Visible;
        UpdateMode(); _designer.Fit();
    }
    public void ShowOperator()
    {
        CheckAlive(); if (!IsEngineering) return;
        RetireTicks(); _designer!.StopPreview();
        if (_operatorStale)
        {
            var old = _session.Location; var project = _designer.Session.GetProject();
            _operator.EngineeringRequested -= ShowEngineering; _operator.Error -= OnError;
            _body.Children.Remove(_operator); _operator.Dispose(); _session.Dispose();
            _session = new HmiWorkplaceSession(project);
            if (project.Screens.FirstOrDefault(s => s.Id == old.ScreenId) is { } screen)
                _session.Navigate(screen.Id, screen.Elements.Any(e => e.Id == old.ElementId) ? old.ElementId : null, old.View);
            _operator = new HmiOperatorWorkplace(_session, _font) { ColorScheme = _scheme };
            _operator.EngineeringRequested += ShowEngineering; _operator.Error += OnError; _body.AddChild(_operator);
            _operatorStale = false;
        }
        IsEngineering = false; _designer.Visibility = Visibility.Collapsed; _operator.Visibility = Visibility.Visible; UpdateMode();
    }
    public HmiProject GetProject() => _designer?.Session.GetProject() ?? _session.GetProject();
    private void OnDesignChanged() { _operatorStale = true; UpdateMode(); }
    private void OnError(string message) { if (!_disposed) { _mode.Text = message; Error?.Invoke(message); } }
    private void UpdateMode()
    {
        if (_mode == null) return;
        _mode.Text = IsEngineering ? "ENGINEERING / OFFLINE AUTHORING" + (IsDirty ? "  •  UNSAVED" : "") : "NORTHWATER / STANDALONE CONTROL-ROOM TRAINING";
        _operatorButton.BorderBrush = HmiWorkplaceTheme.Reference(this, IsEngineering ? "Border" : "Accent");
        _engineeringButton.BorderBrush = HmiWorkplaceTheme.Reference(this, IsEngineering ? "Accent" : "Border");
    }
    private void QueueTick()
    {
        int generation = Volatile.Read(ref _generation);
        if (_disposed || Interlocked.CompareExchange(ref _pendingGeneration, generation, 0) != 0) return;
        try
        {
            UIThread.Post(() =>
            {
                try
                {
                    if (_disposed || generation != Volatile.Read(ref _generation) || IsEngineering) return;
                    if (_session.Mode == HmiWorkplaceMode.Simulation) _session.Advance(TimeSpan.FromMilliseconds(100));
                    _operator.RefreshClock();
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException) { OnError(e.Message); }
                finally { Interlocked.CompareExchange(ref _pendingGeneration, 0, generation); }
            });
        }
        catch (InvalidOperationException) { Interlocked.CompareExchange(ref _pendingGeneration, 0, generation); }
    }
    private void RetireTicks() { Interlocked.Increment(ref _generation); Volatile.Write(ref _pendingGeneration, 0); }
    private void OnLoading(FrameworkElement sender, object args) => EnsureTimer();
    protected override void ArrangeOverride(ProGPU.Scene.Rect arrangeRect)
    {
        base.ArrangeOverride(arrangeRect);
        EnsureTimer();
    }
    private void EnsureTimer()
    {
        if (!_disposed && _automaticTicks && _timer == null)
            _timer = new System.Threading.Timer(_ => QueueTick(), null, 100, 100);
    }
    private void OnUnloaded(object? sender, RoutedEventArgs args)
    {
        if (_disposed) return;
        _timer?.Dispose(); _timer = null;
        RetireTicks(); _session.StopSimulation(); _designer?.StopPreview();
    }
    private void CheckAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; RetireTicks(); _timer?.Dispose(); Loading -= OnLoading; Unloaded -= OnUnloaded;
        _operator.EngineeringRequested -= ShowEngineering; _operator.Error -= OnError;
        if (_designer != null) { _designer.Error -= OnError; _designer.Session.Changed -= OnDesignChanged; _designer.Dispose(); }
        _operator.Dispose(); _session.Dispose(); Error = null;
    }
}
