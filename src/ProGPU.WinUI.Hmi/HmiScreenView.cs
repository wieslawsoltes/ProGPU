using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.Text;

namespace ProGPU.WinUI.Hmi;

/// <summary>Embeddable runtime-only screen. Subscribes once and updates only controls affected by a tag batch.</summary>
public sealed class HmiScreenView : Grid, IDisposable
{
    private readonly HmiProject _project;
    private readonly HmiRuntime _runtime;
    private readonly Canvas _surface = new();
    private readonly Dictionary<string, List<HmiControl>> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<HmiControl, HmiElement> _definitions = [];
    private readonly List<HmiControl> _alarmControls = [];
    private readonly TtfFont? _font;
    private bool _disposed;
    public string ScreenId { get; private set; } = "";
    public IReadOnlyCollection<HmiControl> Controls => _definitions.Keys;
    public event Action<string>? Error;
    public event Action<string>? ScreenChanged;

    public HmiScreenView(HmiProject project, HmiRuntime runtime, string? screenId = null, TtfFont? font = null)
    {
        _project = HmiProjectSerializer.Clone(project);
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _font = font ?? PopupService.DefaultFont;
        AddChild(_surface);
        LoadScreen(screenId ?? project.StartScreenId);
        _runtime.TagsChanged += OnTagsChanged;
        _runtime.AlarmsChanged += OnAlarmsChanged;
        _runtime.NavigationRequested += LoadScreen;
        Unloaded += (_, _) => Dispose();
    }
    public void LoadScreen(string screenId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var screen = _project.Screens.SingleOrDefault(s => s.Id == screenId) ?? throw new ArgumentException("Unknown HMI screen.", nameof(screenId));
        _surface.Children.Clear(); _bindings.Clear(); _definitions.Clear(); _alarmControls.Clear();
        ScreenId = screenId;
        Width = _surface.Width = screen.Width;
        Height = _surface.Height = screen.Height;
        foreach (var definition in screen.Elements)
        {
            var control = HmiControlCatalog.Create(definition.Symbol);
            control.Font = _font;
            control.ApplyDefinition(definition);
            _definitions.Add(control, definition);
            _surface.Children.Add(control);
            foreach (var tag in new[] { definition.Tag, definition.VisibilityTag, definition.EnabledTag }.Where(t => t.Length > 0).Distinct())
            {
                if (!_bindings.TryGetValue(tag, out var list)) _bindings.Add(tag, list = []);
                list.Add(control);
            }
            control.Invoked += sender => Execute(() => _runtime.Execute(_definitions[sender].Action));
            control.ValueSubmitted += (sender, value) => Execute(() => _runtime.Write(_definitions[sender].Tag, value));
            control.InputRejected += message => Error?.Invoke(message);
            if (definition.Symbol is HmiSymbol.AlarmBanner or HmiSymbol.AlarmList) _alarmControls.Add(control);
            Refresh(control);
        }
        OnAlarmsChanged();
        ScreenChanged?.Invoke(screenId);
        InvalidateMeasure(); Invalidate();
    }
    public void RefreshState()
    {
        if (_disposed) return;
        foreach (var control in _definitions.Keys) Refresh(control);
        OnAlarmsChanged();
    }
    private void OnTagsChanged(IReadOnlyList<string> tags)
    {
        if (_disposed) return;
        var affected = new HashSet<HmiControl>();
        foreach (var tag in tags) if (_bindings.TryGetValue(tag, out var controls)) foreach (var control in controls) affected.Add(control);
        foreach (var control in affected) Refresh(control);
    }
    private void Refresh(HmiControl control)
    {
        var definition = _definitions[control];
        bool visible = !definition.IsHidden;
        if (definition.VisibilityTag.Length > 0)
            visible &= _runtime.TryRead(definition.VisibilityTag, out var visibility) && visibility.Quality == HmiQuality.Good && visibility.Value.AsBoolean();
        control.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        bool enabled = true;
        if (definition.EnabledTag.Length > 0)
            enabled = _runtime.TryRead(definition.EnabledTag, out var condition) && condition.Quality == HmiQuality.Good && condition.Value.AsBoolean();
        control.RuntimeEnabled = enabled;
        control.CommandsEnabled = _runtime.IsRunning;
        if (_runtime.TryRead(definition.Tag, out var sample))
            control.UpdateSample(sample, _runtime.GetHistory(definition.Tag), (_runtime.Now.ToUnixTimeMilliseconds() % 1000) / 1000f);
    }
    private void OnAlarmsChanged()
    {
        if (!_disposed) foreach (var control in _alarmControls) control.UpdateAlarms(_runtime.Alarms);
    }
    private void Execute(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or KeyNotFoundException or ArgumentException)
        { Error?.Invoke(error.Message); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.TagsChanged -= OnTagsChanged;
        _runtime.AlarmsChanged -= OnAlarmsChanged;
        _runtime.NavigationRequested -= LoadScreen;
        _bindings.Clear(); _definitions.Clear(); _alarmControls.Clear();
        _surface.Children.Clear();
    }
}
