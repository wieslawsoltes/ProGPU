using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Text;

namespace ProGPU.WinUI.Hmi;

/// <summary>A retained, reusable HMI visual, independent of the designer assembly.</summary>
public class HmiControl : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(HmiControl), new PropertyMetadata(0d, OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(HmiControl), new PropertyMetadata("Component", OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(HmiControl), new PropertyMetadata("", OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty QualityProperty = DependencyProperty.Register(nameof(Quality), typeof(HmiQuality), typeof(HmiControl), new PropertyMetadata(HmiQuality.Good, OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(HmiControl), new PropertyMetadata(false, OnDisplayChanged) { AffectsRender = true });
    private HmiElement _definition;
    private readonly TextBlock _label;
    private readonly TextBlock _value;
    private readonly TextBlock _quality;
    private readonly TextBlock _applyLabel;
    private readonly Button _command;
    private readonly Grid _inputRow;
    private readonly TextBox _input;
    private bool _commandsEnabled;
    private bool _runtimeEnabled = true;
    private bool _batching;
    private string? _textValue;
    private TtfFont? _font;
    private IReadOnlyList<HmiTagSample> _history = Array.Empty<HmiTagSample>();
    private string _alarmSummary = "No active alarms";
    private bool _hasAlarm;
    private float _phase;

    public double Value
    {
        get => (double)(GetValue(ValueProperty) ?? 0d);
        set { if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); SetValue(ValueProperty, value); }
    }
    public string Label { get => (string)(GetValue(LabelProperty) ?? ""); set => SetValue(LabelProperty, value ?? ""); }
    public string Unit { get => (string)(GetValue(UnitProperty) ?? ""); set => SetValue(UnitProperty, value ?? ""); }
    public HmiQuality Quality { get => (HmiQuality)(GetValue(QualityProperty) ?? HmiQuality.Good); set => SetValue(QualityProperty, value); }
    public bool IsActive { get => (bool)(GetValue(IsActiveProperty) ?? false); set => SetValue(IsActiveProperty, value); }
    public HmiSymbol Symbol => _definition.Symbol;
    public string TagName => _definition.Tag;
    public bool IsDesignLocked => _definition.IsLocked;
    public new TtfFont? Font
    {
        get => _font;
        set
        {
            _font = value; _label.Font = value; _value.Font = value; _quality.Font = value;
            _input.Font = value; _applyLabel.Font = value;
        }
    }
    public bool CommandsEnabled
    {
        get => _commandsEnabled;
        set { if (_commandsEnabled == value) return; _commandsEnabled = value; UpdateDisplay(); }
    }
    public bool RuntimeEnabled
    {
        get => _runtimeEnabled;
        set { if (_runtimeEnabled == value) return; _runtimeEnabled = value; UpdateDisplay(); }
    }
    public event Action<HmiControl>? Invoked;
    public event Action<HmiControl, HmiValue>? ValueSubmitted;
    public event Action<string>? InputRejected;

    public HmiControl() : this(HmiSymbol.NumericDisplay) { }
    public HmiControl(HmiSymbol symbol)
    {
        _definition = new HmiElement { Symbol = symbol, Label = symbol.ToString(), Name = symbol.ToString(), Width = 180, Height = symbol == HmiSymbol.Pipe ? 44 : 130 };
        _label = new TextBlock { FontSize = 12, Margin = new Thickness(10, 8, 10, 0), VerticalAlignment = VerticalAlignment.Top, Foreground = HmiDrawing.Text, IsHitTestVisible = false };
        _value = new TextBlock { FontSize = 17, Margin = new Thickness(10, 0, 10, 8), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Foreground = HmiDrawing.Text, IsHitTestVisible = false };
        _quality = new TextBlock { FontSize = 10, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Foreground = HmiDrawing.Warning, IsHitTestVisible = false };
        AddChild(_label); AddChild(_value); AddChild(_quality);
        _command = new Button { Background = HmiDrawing.Transparent, BorderBrush = HmiDrawing.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _command.Click += (_, _) => { if (CommandsEnabled && RuntimeEnabled && Quality == HmiQuality.Good) Invoked?.Invoke(this); };
        AddChild(_command);
        _inputRow = new Grid { Margin = new Thickness(8, 28, 8, 8), VerticalAlignment = VerticalAlignment.Center };
        _inputRow.ColumnDefinitions.Add(GridLength.Star(1));
        _inputRow.ColumnDefinitions.Add(new GridLength(66));
        _input = new TextBox { Height = 30, FontSize = 13, PlaceholderText = "Setpoint" };
        _applyLabel = new TextBlock { Text = "Apply", FontSize = 12 };
        var apply = new Button { Content = _applyLabel, Height = 30, Margin = new Thickness(4, 0, 0, 0) };
        apply.Click += (_, _) =>
        {
            if (!CommandsEnabled || !RuntimeEnabled || Quality != HmiQuality.Good) return;
            try { ValueSubmitted?.Invoke(this, HmiValue.Parse(_input.Text, HmiTagType.Number)); }
            catch (Exception error) when (error is FormatException or InvalidDataException or InvalidOperationException) { InputRejected?.Invoke(error.Message); }
        };
        _inputRow.AddChild(_input); _inputRow.AddChild(apply); SetColumn(apply, 1);
        AddChild(_inputRow);
        Font = PopupService.DefaultFont;
        ApplyDefinition(_definition);
    }
    public void ApplyDefinition(HmiElement definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Enum.IsDefined(definition.Symbol) || !double.IsFinite(definition.Minimum) || !double.IsFinite(definition.Maximum) || definition.Maximum <= definition.Minimum || definition.Decimals is < 0 or > 6 ||
            !float.IsFinite(definition.Width) || !float.IsFinite(definition.Height) || definition.Width is < 8 or > 16384 || definition.Height is < 8 or > 16384 || definition.Action == null)
            throw new ArgumentException("Invalid HMI component dimensions, symbol, range, precision or action.", nameof(definition));
        _batching = true;
        try
        {
            _definition = definition.Copy();
            Name = definition.Name;
            Width = definition.Width; Height = definition.Height;
            Canvas.SetLeft(this, definition.X); Canvas.SetTop(this, definition.Y);
            Label = definition.Label; Unit = definition.Unit;
            Visibility = definition.IsHidden ? Visibility.Collapsed : Visibility.Visible;
            _inputRow.Visibility = Symbol == HmiSymbol.NumericInput ? Visibility.Visible : Visibility.Collapsed;
            _value.Visibility = Symbol == HmiSymbol.NumericInput ? Visibility.Collapsed : Visibility.Visible;
            _command.Visibility = Symbol == HmiSymbol.NumericInput ? Visibility.Collapsed : Visibility.Visible;
            _label.FontSize = Symbol == HmiSymbol.Label ? 18 : 12;
        }
        finally { _batching = false; }
        UpdateDisplay();
    }
    public HmiElement CaptureDefinition()
    {
        var copy = _definition.Copy();
        copy.Name = Name ?? "Component";
        copy.Label = Label; copy.Unit = Unit;
        copy.X = Canvas.GetLeft(this); copy.Y = Canvas.GetTop(this);
        copy.Width = float.IsFinite(Width) ? Width : Size.X;
        copy.Height = float.IsFinite(Height) ? Height : Size.Y;
        return copy;
    }
    public void UpdateSample(HmiTagSample sample, IReadOnlyList<HmiTagSample>? history = null, float phase = 0)
    {
        string? text = sample.Value.Type == HmiTagType.Text ? sample.Value.Text : null;
        bool changed = Value != sample.Value.AsNumber() || IsActive != sample.Value.AsBoolean() || Quality != sample.Quality || _textValue != text;
        _batching = true;
        try
        {
            _textValue = text;
            Value = sample.Value.AsNumber(); IsActive = sample.Value.AsBoolean(); Quality = sample.Quality;
            if (history != null) _history = history;
            _phase = phase;
        }
        finally { _batching = false; }
        if (changed) UpdateDisplay();
        if (Symbol == HmiSymbol.Trend || IsActive && Symbol is (HmiSymbol.Pump or HmiSymbol.Motor or HmiSymbol.Pipe or HmiSymbol.Conveyor)) Invalidate();
    }
    public void UpdateAlarms(IReadOnlyList<HmiAlarmState> alarms)
    {
        var visible = alarms.Where(a => a.NeedsAttention).OrderByDescending(a => a.Definition.Severity).ToArray();
        _hasAlarm = visible.Length > 0;
        _alarmSummary = visible.Length == 0 ? "No active or unacknowledged alarms" : Symbol == HmiSymbol.AlarmBanner
            ? $"{visible.Length} alarm(s) · {visible[0].Definition.Message}"
            : string.Join("\n", visible.Take(8).Select(a => $"{a.Definition.Severity} · {a.Definition.Message} · {(a.IsActive ? "ACTIVE" : "RETURNED")} / {(a.IsAcknowledged ? "ACK" : "UNACK")}{(a.IsQualityUnknown ? " / QUALITY UNKNOWN" : "")}"));
        UpdateDisplay();
    }
    private static void OnDisplayChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var control = (HmiControl)owner;
        if (!control._batching) control.UpdateDisplay();
    }
    private void UpdateDisplay()
    {
        if (_label == null || _input == null) return;
        _label.Text = Label;
        _quality.Text = Quality == HmiQuality.Good ? "" : Quality.ToString().ToUpperInvariant();
        _value.Text = Symbol switch
        {
            HmiSymbol.Label => _textValue ?? "",
            HmiSymbol.AlarmBanner or HmiSymbol.AlarmList => _alarmSummary,
            HmiSymbol.PushButton or HmiSymbol.NavigationButton or HmiSymbol.RecipeButton or HmiSymbol.Rectangle or HmiSymbol.Pipe => "",
            HmiSymbol.Pump or HmiSymbol.Motor or HmiSymbol.Indicator or HmiSymbol.Conveyor => IsActive ? "RUNNING" : "STOPPED",
            HmiSymbol.Valve or HmiSymbol.ToggleSwitch => IsActive ? "OPEN / ON" : "CLOSED / OFF",
            _ => Quality == HmiQuality.Good ? Value.ToString("F" + _definition.Decimals, CultureInfo.InvariantCulture) + (Unit.Length > 0 ? " " + Unit : "") : "—"
        };
        _value.FontSize = Symbol == HmiSymbol.AlarmList ? 12 : Symbol == HmiSymbol.NumericDisplay ? 27 : 16;
        _command.IsHitTestVisible = CommandsEnabled && RuntimeEnabled && Quality == HmiQuality.Good;
        _inputRow.IsHitTestVisible = CommandsEnabled && RuntimeEnabled && Quality == HmiQuality.Good;
        if (!ReferenceEquals(InputSystem.FocusedElement, _input)) _input.Text = Value.ToString("F" + _definition.Decimals, CultureInfo.InvariantCulture);
        Invalidate();
    }
    public override void OnRender(DrawingContext context)
    {
        HmiDrawing.Draw(context, Symbol, Size, Value, _definition.Minimum, _definition.Maximum, IsActive, Quality, _history, _hasAlarm, _phase);
        base.OnRender(context);
    }
}
