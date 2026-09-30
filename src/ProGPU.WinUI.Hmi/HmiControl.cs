using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;
using ProGPU.Scene;
using ProGPU.Text;

namespace ProGPU.WinUI.Hmi;

/// <summary>Retained HMI visual with lazily created operator input; independent of the designer assembly.</summary>
public partial class HmiControl : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(HmiControl), new PropertyMetadata(0d, OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(HmiControl), new PropertyMetadata("Component", OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(HmiControl), new PropertyMetadata("", OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty QualityProperty = DependencyProperty.Register(nameof(Quality), typeof(HmiQuality), typeof(HmiControl), new PropertyMetadata(HmiQuality.Good, OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(HmiControl), new PropertyMetadata(false, OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty VisualToneProperty = DependencyProperty.Register(nameof(VisualTone), typeof(HmiVisualTone), typeof(HmiControl), new PropertyMetadata(HmiVisualTone.Normal, OnDisplayChanged) { AffectsRender = true });
    public static readonly DependencyProperty StateTextProperty = DependencyProperty.Register(nameof(StateText), typeof(string), typeof(HmiControl), new PropertyMetadata("", OnDisplayChanged) { AffectsRender = true });

    private HmiElement _definition;
    private readonly TextBlock _label;
    private readonly TextBlock _value;
    private readonly TextBlock _quality;
    private Button? _command;
    private Grid? _inputRow;
    private TextBox? _input;
    private TextBlock? _applyLabel;
    private TextBlock? _trendAxis;
    private HmiTrendBucket[]? _trendBuckets;
    private bool _commandsEnabled;
    private bool _runtimeEnabled = true;
    private bool _batching;
    private bool _updatingInput;
    private bool _inputDirty;
    private string? _textValue;
    private IReadOnlyList<HmiTagSample> _history = Array.Empty<HmiTagSample>();
    private DateTimeOffset _trendNow = DateTimeOffset.UnixEpoch;
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
    public HmiQuality Quality
    {
        get => (HmiQuality)(GetValue(QualityProperty) ?? HmiQuality.Good);
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); SetValue(QualityProperty, value); }
    }
    public bool IsActive { get => (bool)(GetValue(IsActiveProperty) ?? false); set => SetValue(IsActiveProperty, value); }
    public HmiVisualTone VisualTone
    {
        get => (HmiVisualTone)(GetValue(VisualToneProperty) ?? HmiVisualTone.Normal);
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); SetValue(VisualToneProperty, value); }
    }
    public string StateText { get => (string)(GetValue(StateTextProperty) ?? ""); set => SetValue(StateTextProperty, value ?? ""); }
    public HmiSymbol Symbol => _definition.Symbol;
    public string ElementId => _definition.Id;
    public string TagName => _definition.Tag;
    public bool IsDesignLocked => _definition.IsLocked;
    public bool HasPendingInput => _inputDirty;
    public string DisplayText => _value.Text;
    public new TtfFont? Font { get => base.Font; set => base.Font = value; }
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
        if (HmiSymbolTraits.IsSchematicSymbol(symbol)) _definition = HmiControlCatalog.CreateDefinition(symbol);
        _label = new TextBlock { FontSize = 12, Margin = new Thickness(10, 8, 10, 0), VerticalAlignment = VerticalAlignment.Top, Foreground = HmiDrawing.Text, IsHitTestVisible = false };
        _value = new TextBlock { FontSize = 17, Margin = new Thickness(10, 0, 10, 8), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Foreground = HmiDrawing.Text, IsHitTestVisible = false };
        _quality = new TextBlock { FontSize = 10, Margin = new Thickness(10, 26, 10, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Foreground = HmiDrawing.Warning, IsHitTestVisible = false };
        AddChild(_label); AddChild(_value); AddChild(_quality);
        Font = PopupService.DefaultFont;
        ApplyDefinition(_definition);
    }

    protected override void OnPropertyChanged(DependencyProperty property, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(property, oldValue, newValue);
        if (property == FontProperty && _label != null) UpdateFonts();
    }

    private void UpdateFonts()
    {
        _label.Font = Font; _value.Font = Font; _quality.Font = Font;
        if (_instrumentCode != null) _instrumentCode.Font = Font;
        if (_instrumentLoop != null) _instrumentLoop.Font = Font;
        if (_input != null) _input.Font = Font;
        if (_applyLabel != null) _applyLabel.Font = Font;
        if (_trendAxis != null) _trendAxis.Font = Font;
        if (_tagCaption != null) _tagCaption.Font = Font;
        if (_rangeCaption != null) _rangeCaption.Font = Font;
    }

    public void ApplyDefinition(HmiElement definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Enum.IsDefined(definition.Symbol) || !double.IsFinite(definition.Minimum) || !double.IsFinite(definition.Maximum) ||
            !double.IsFinite(definition.Maximum - definition.Minimum) || definition.Maximum <= definition.Minimum || definition.Decimals is < 0 or > 6 ||
            !float.IsFinite(definition.X) || !float.IsFinite(definition.Y) || !float.IsFinite(definition.Width) || !float.IsFinite(definition.Height) ||
            definition.Width is < 8 or > 16384 || definition.Height is < 8 or > 16384 || definition.Action == null || definition.Trend == null || definition.Appearance == null)
            throw new ArgumentException("Invalid HMI component geometry, symbol, range, precision, trend or action.", nameof(definition));
        definition.Trend.Validate();
        definition.Appearance.ValidateForRange(definition.Minimum, definition.Maximum);
        bool differentInput = _definition.Id != definition.Id || _definition.Tag != definition.Tag || _definition.Symbol != definition.Symbol;
        var copy = definition.Copy();
        _batching = true;
        try
        {
            _definition = copy;
            Name = definition.Name;
            Width = definition.Width; Height = definition.Height;
            Canvas.SetLeft(this, definition.X); Canvas.SetTop(this, definition.Y);
            Label = definition.Label; Unit = definition.Unit;
            Visibility = definition.IsHidden ? Visibility.Collapsed : Visibility.Visible;
            if (differentInput) _inputDirty = false;
            ConfigureInteraction();
            _value.Visibility = Symbol == HmiSymbol.NumericInput ? Visibility.Collapsed : Visibility.Visible;
            _label.FontSize = Symbol == HmiSymbol.Label ? 18 : 12;
            _value.HorizontalAlignment = Symbol == HmiSymbol.Trend ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        }
        finally { _batching = false; }
        UpdateDisplay();
        UpdateTrendAxis();
    }

    private void ConfigureInteraction()
    {
        bool numeric = Symbol == HmiSymbol.NumericInput;
        bool actionable = !numeric && (_definition.Action.Kind != HmiActionKind.None || Symbol is HmiSymbol.PushButton or HmiSymbol.ToggleSwitch or HmiSymbol.NavigationButton or HmiSymbol.RecipeButton);
        if (actionable && _command == null)
        {
            _command = new HmiCommandOverlay();
            _command.Click += (_, _) => { if (CanInteract) Invoked?.Invoke(this); };
            AddChild(_command);
        }
        else if (!actionable && _command != null) { Children.Remove(_command); _command = null; }
        if (numeric && _inputRow == null)
        {
            _inputRow = new Grid { Margin = new Thickness(8, 30, 8, 8), VerticalAlignment = VerticalAlignment.Center };
            _inputRow.ColumnDefinitions.Add(GridLength.Star(1));
            _inputRow.ColumnDefinitions.Add(new GridLength(66));
            _input = new TextBox { Height = 30, FontSize = 13, PlaceholderText = "Setpoint", Font = Font };
            _input.TextChanged += (_, _) => { if (!_updatingInput) _inputDirty = true; };
            _applyLabel = new TextBlock { Text = "Apply", FontSize = 12, Font = Font };
            var apply = new Button { Content = _applyLabel, Height = 30, Margin = new Thickness(4, 0, 0, 0) };
            apply.Click += (_, _) => SubmitInput();
            _inputRow.AddChild(_input); _inputRow.AddChild(apply); SetColumn(apply, 1);
            AddChild(_inputRow);
        }
        else if (!numeric && _inputRow != null)
        {
            Children.Remove(_inputRow); _inputRow = null; _input = null; _applyLabel = null; _inputDirty = false;
        }
        if (Symbol == HmiSymbol.Trend && _trendAxis == null)
        {
            _trendAxis = new TextBlock { Font = Font, FontSize = 10, Foreground = HmiDrawing.Muted, Margin = new Thickness(10, 0, 10, 9), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
            _trendBuckets = new HmiTrendBucket[512];
            AddChild(_trendAxis);
        }
        else if (Symbol != HmiSymbol.Trend && _trendAxis != null)
        {
            Children.Remove(_trendAxis); _trendAxis = null; _trendBuckets = null;
        }
    }

    private bool CanInteract => CommandsEnabled && RuntimeEnabled && Quality == HmiQuality.Good && VisualTone != HmiVisualTone.Unknown;
    private void SubmitInput()
    {
        if (!CanInteract || _input == null) return;
        try
        {
            var value = HmiValue.Parse(_input.Text, HmiTagType.Number);
            if (value.Number < _definition.Minimum || value.Number > _definition.Maximum)
                throw new InvalidDataException("Input is outside the configured component range.");
            ValueSubmitted?.Invoke(this, value);
            // Keep typed text until explicit reset or subsequent feedback. No optimistic Value assignment.
        }
        catch (Exception error) when (error is FormatException or InvalidDataException or InvalidOperationException)
        { InputRejected?.Invoke(error.Message); }
    }

    public void ResetPendingInput()
    {
        _inputDirty = false;
        UpdateInputText();
    }

    public HmiElement CaptureDefinition()
    {
        var copy = _definition.Copy();
        copy.Name = Name ?? "Component"; copy.Label = Label; copy.Unit = Unit;
        copy.X = Canvas.GetLeft(this); copy.Y = Canvas.GetTop(this);
        copy.Width = float.IsFinite(Width) ? Width : Size.X;
        copy.Height = float.IsFinite(Height) ? Height : Size.Y;
        return copy;
    }

    public void UpdateState(HmiVisualState state)
    {
        if (VisualTone == state.Tone && StateText == state.Text) return;
        _batching = true;
        try { VisualTone = state.Tone; StateText = state.Text; }
        finally { _batching = false; }
        UpdateDisplay();
    }

    public void UpdateSample(HmiTagSample sample, IReadOnlyList<HmiTagSample>? history = null, float phase = 0, DateTimeOffset? now = null)
    {
        if (!Enum.IsDefined(sample.Quality) || !Enum.IsDefined(sample.Value.Type) || !double.IsFinite(sample.Value.Number))
            throw new ArgumentException("Invalid HMI sample.", nameof(sample));
        string? text = sample.Value.Type == HmiTagType.Text ? sample.Value.Text : null;
        bool changed = Value != sample.Value.AsNumber() || IsActive != sample.Value.AsBoolean() || Quality != sample.Quality || _textValue != text;
        _batching = true;
        try
        {
            _textValue = text;
            Value = sample.Value.AsNumber(); IsActive = sample.Value.AsBoolean(); Quality = sample.Quality;
            if (history != null) _history = history;
            _trendNow = now ?? sample.Timestamp;
            _phase = float.IsFinite(phase) ? phase - MathF.Floor(phase) : 0;
        }
        finally { _batching = false; }
        if (changed) UpdateDisplay();
        if (Symbol == HmiSymbol.Trend) { UpdateTrendAxis(); Invalidate(); }
        else if (_definition.Appearance.AnimateFlow && IsActive && Quality == HmiQuality.Good &&
            (HmiSymbolTraits.IsRotating(Symbol) || Symbol is HmiSymbol.Pipe or HmiSymbol.Conveyor)) Invalidate();
    }

    public void UpdateAlarms(IReadOnlyList<HmiAlarmState> alarms)
    {
        var visible = alarms.Where(a => a.NeedsAttention).OrderByDescending(a => a.Definition.Severity).ToArray();
        _hasAlarm = visible.Length > 0;
        _alarmSummary = visible.Length == 0 ? "No active or unacknowledged alarms" : Symbol == HmiSymbol.AlarmBanner
            ? $"{visible.Length} alarm(s) · {visible[0].Definition.Message}"
            : string.Join("\n", visible.Take(8).Select(a => $"{a.Definition.Severity} · {a.Definition.Message} · {(a.IsActive ? "ACTIVE" : "RETURNED")} / {(a.IsAcknowledged ? "ACK" : "UNACK")}{(a.IsQualityUnknown ? " / QUALITY UNKNOWN" : "")}")) +
                (visible.Length > 8 ? $"\n+ {visible.Length - 8} more · open Alarm console" : "");
        UpdateDisplay();
    }

    private static void OnDisplayChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var control = (HmiControl)owner;
        if (!control._batching) control.UpdateDisplay();
    }
    private static void SetText(TextBlock target, string text) { if (target.Text != text) target.Text = text; }
    private void UpdateDisplay()
    {
        if (_label == null) return;
        SetText(_label, Label);
        SetText(_quality, Quality == HmiQuality.Good ? StateText : Quality.ToString().ToUpperInvariant());
        _quality.Foreground = HmiEquipmentDrawing.ToneBrush(VisualTone, HmiDrawing.Warning);
        bool unknown = Quality != HmiQuality.Good || VisualTone == HmiVisualTone.Unknown;
        string display = Symbol switch
        {
            HmiSymbol.Label => unknown ? "UNKNOWN" : _textValue ?? "",
            HmiSymbol.AlarmBanner or HmiSymbol.AlarmList => _alarmSummary,
            HmiSymbol.PushButton or HmiSymbol.NavigationButton or HmiSymbol.RecipeButton or HmiSymbol.Rectangle or HmiSymbol.Pipe => "",
            HmiSymbol.Pump or HmiSymbol.Motor or HmiSymbol.Indicator or HmiSymbol.Conveyor or HmiSymbol.Fan or HmiSymbol.Compressor or HmiSymbol.Agitator => unknown ? "UNKNOWN" : IsActive ? "RUNNING" : "STOPPED",
            HmiSymbol.Valve or HmiSymbol.CheckValve or HmiSymbol.ButterflyValve or HmiSymbol.ToggleSwitch => unknown ? "UNKNOWN" : IsActive ? "OPEN / ON" : "CLOSED / OFF",
            _ => unknown ? "—" : Value.ToString("F" + _definition.Decimals, CultureInfo.InvariantCulture) + (Unit.Length > 0 ? " " + Unit : "")
        };
        SetText(_value, display);
        float fontSize = Symbol is HmiSymbol.AlarmList or HmiSymbol.Trend ? 12 : Symbol == HmiSymbol.NumericDisplay ? 27 : 16;
        if (_value.FontSize != fontSize) _value.FontSize = fontSize;
        if (_command != null) { _command.IsHitTestVisible = CanInteract; _command.IsEnabled = CanInteract; }
        if (_inputRow != null) _inputRow.IsHitTestVisible = CanInteract;
        if (_input != null) _input.IsEnabled = CanInteract;
        UpdateInputText();
        UpdatePresentation();
        Invalidate();
    }

    private void UpdateInputText()
    {
        if (_input == null || _inputDirty || ReferenceEquals(InputSystem.FocusedElement, _input)) return;
        string text = Value.ToString("F" + _definition.Decimals, CultureInfo.InvariantCulture);
        if (_input.Text == text) return;
        _updatingInput = true;
        try { _input.Text = text; }
        finally { _updatingInput = false; }
    }
    private void UpdateTrendAxis()
    {
        if (_trendAxis == null) return;
        if (_history.Count < 2) { SetText(_trendAxis, "NO HISTORY"); return; }
        var start = HmiTrendDrawing.WindowStart(_trendNow, _definition.Trend.WindowSeconds);
        // A compact trend reserves a separate value column. Do not let its time labels
        // extend into that column, even on hosts with different font metrics.
        SetText(_trendAxis, Width < 320 ? _trendNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : $"{start:HH:mm:ss} — {_trendNow:HH:mm:ss}");
    }

    public override void OnRender(DrawingContext context)
    {
        bool active = IsActive && Quality == HmiQuality.Good && VisualTone != HmiVisualTone.Unknown;
        HmiDrawing.Draw(context, Symbol, Size, Value, _definition.Minimum, _definition.Maximum, active, Quality,
            Symbol == HmiSymbol.Trend ? Array.Empty<HmiTagSample>() : _history, _hasAlarm, _phase, VisualTone, _definition.Appearance, ColorScheme, StateText.Length > 0);
        if (Symbol == HmiSymbol.Trend && _trendBuckets != null)
            HmiTrendDrawing.Draw(context, Size, _history, _trendNow, _definition.Trend, _definition.Minimum, _definition.Maximum, _trendBuckets, ColorScheme, _definition.Appearance);
        base.OnRender(context);
    }
}
