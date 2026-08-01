using System;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using WinRT;

namespace Microsoft.UI.Xaml.Controls;

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class TimePickerValueChangedEventArgs
{
    internal TimePickerValueChangedEventArgs(TimeSpan oldTime, TimeSpan newTime)
    {
        OldTime = oldTime;
        NewTime = newTime;
    }

    public TimeSpan OldTime { get; }
    public TimeSpan NewTime { get; }
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class TimePickerSelectedValueChangedEventArgs
{
    internal TimePickerSelectedValueChangedEventArgs(TimeSpan? oldTime, TimeSpan? newTime)
    {
        OldTime = oldTime;
        NewTime = newTime;
    }

    public TimeSpan? OldTime { get; }
    public TimeSpan? NewTime { get; }
}

/// <summary>
/// Selects a time-of-day using a minute-resolution value model.
/// </summary>
[ContentProperty(Name = nameof(Header))]
[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public class TimePicker : Control
{
    private const string TwelveHourClock = "12HourClock";
    private const string TwentyFourHourClock = "24HourClock";

    public static DependencyProperty HeaderProperty { get; } = Register<object?>(nameof(Header), null);
    public static DependencyProperty HeaderTemplateProperty { get; } = Register<DataTemplate?>(nameof(HeaderTemplate), null);
    public static DependencyProperty ClockIdentifierProperty { get; } = Register(nameof(ClockIdentifier), TwelveHourClock, OnClockIdentifierChanged);
    public static DependencyProperty MinuteIncrementProperty { get; } = Register(nameof(MinuteIncrement), 1, OnMinuteIncrementChanged);
    public static DependencyProperty TimeProperty { get; } = Register(nameof(Time), TimeSpan.Zero, OnTimeChanged);
    public static DependencyProperty SelectedTimeProperty { get; } = Register<TimeSpan?>(nameof(SelectedTime), null, OnSelectedTimeChanged);
    public static DependencyProperty LightDismissOverlayModeProperty { get; } =
        Register(nameof(LightDismissOverlayMode), LightDismissOverlayMode.Auto);

    private bool _synchronizing;
    private bool _coercingTime;
    private bool _coercingSelectedTime;
    private TimeSpan _timeBeforeCoercion;
    private TimeSpan? _selectedTimeBeforeCoercion;

    public TimePicker()
    {
    }

    protected internal TimePicker(IObjectReference objRef)
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected TimePicker(DerivedComposed _)
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public DataTemplate? HeaderTemplate { get => GetValue(HeaderTemplateProperty) as DataTemplate; set => SetValue(HeaderTemplateProperty, value); }
    public string ClockIdentifier
    {
        get => GetValue(ClockIdentifierProperty) as string ?? TwelveHourClock;
        set
        {
            ValidateClockIdentifier(value);
            SetValue(ClockIdentifierProperty, value);
        }
    }

    public int MinuteIncrement
    {
        get => (int)(GetValue(MinuteIncrementProperty) ?? 1);
        set
        {
            ValidateMinuteIncrement(value, nameof(value));
            SetValue(MinuteIncrementProperty, value);
        }
    }

    public TimeSpan Time
    {
        get => (TimeSpan)(GetValue(TimeProperty) ?? TimeSpan.Zero);
        set => SetValue(TimeProperty, CoerceTime(value, MinuteIncrement));
    }

    public TimeSpan? SelectedTime
    {
        get => GetValue(SelectedTimeProperty) as TimeSpan?;
        set => SetValue(
            SelectedTimeProperty,
            value.HasValue
                ? CoerceTime(value.Value, MinuteIncrement)
                : null);
    }

    public LightDismissOverlayMode LightDismissOverlayMode
    {
        get => (LightDismissOverlayMode)(GetValue(LightDismissOverlayModeProperty) ?? LightDismissOverlayMode.Auto);
        set => SetValue(LightDismissOverlayModeProperty, value);
    }

    public event EventHandler<TimePickerValueChangedEventArgs>? TimeChanged;
    public event TypedEventHandler<TimePicker, TimePickerSelectedValueChangedEventArgs>? SelectedTimeChanged;

    private static void OnClockIdentifierChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ValidateClockIdentifier(args.NewValue as string);

    private static void OnMinuteIncrementChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var value = (int)(args.NewValue ?? 1);
        ValidateMinuteIncrement(value, nameof(MinuteIncrement));
        var picker = (TimePicker)dependencyObject;
        picker.Time = picker.Time;
    }

    private static void OnTimeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var picker = (TimePicker)dependencyObject;
        var oldTime = picker._coercingTime
            ? picker._timeBeforeCoercion
            : (TimeSpan)(args.OldValue ?? TimeSpan.Zero);
        var newTime = CoerceTime(
            (TimeSpan)(args.NewValue ?? TimeSpan.Zero),
            picker.MinuteIncrement);
        if (!Equals(args.NewValue, newTime))
        {
            picker._coercingTime = true;
            picker._timeBeforeCoercion = oldTime;
            try
            {
                picker.SetValue(TimeProperty, newTime);
            }
            finally
            {
                picker._coercingTime = false;
            }
            return;
        }

        if (!picker._synchronizing)
        {
            picker._synchronizing = true;
            try
            {
                picker.SelectedTime = newTime;
            }
            finally
            {
                picker._synchronizing = false;
            }
        }

        picker.TimeChanged?.Invoke(picker, new TimePickerValueChangedEventArgs(oldTime, newTime));
    }

    private static void OnSelectedTimeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var picker = (TimePicker)dependencyObject;
        var oldTime = picker._coercingSelectedTime
            ? picker._selectedTimeBeforeCoercion
            : args.OldValue as TimeSpan?;
        var newTime = args.NewValue as TimeSpan?;
        if (newTime.HasValue)
        {
            var normalized = CoerceTime(newTime.Value, picker.MinuteIncrement);
            if (normalized != newTime.Value)
            {
                picker._coercingSelectedTime = true;
                picker._selectedTimeBeforeCoercion = oldTime;
                try
                {
                    picker.SetValue(SelectedTimeProperty, normalized);
                }
                finally
                {
                    picker._coercingSelectedTime = false;
                }
                return;
            }
        }

        if (!picker._synchronizing)
        {
            picker._synchronizing = true;
            try
            {
                picker.Time = newTime ?? TimeSpan.Zero;
            }
            finally
            {
                picker._synchronizing = false;
            }
        }

        picker.SelectedTimeChanged?.Invoke(
            picker,
            new TimePickerSelectedValueChangedEventArgs(oldTime, newTime));
    }

    private static void ValidateClockIdentifier(string? value)
    {
        if (value is not TwelveHourClock and not TwentyFourHourClock)
        {
            throw new ArgumentException(
                "ClockIdentifier must be either 12HourClock or 24HourClock.",
                nameof(value));
        }
    }

    private static void ValidateMinuteIncrement(int value, string parameterName)
    {
        if (value is < 0 or > 59)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "MinuteIncrement must be between 0 and 59.");
        }
    }

    private static TimeSpan CoerceTime(TimeSpan value, int increment)
    {
        if (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Time must be between 00:00 and 23:59.");
        }

        int wholeMinutes = (int)(value.Ticks / TimeSpan.TicksPerMinute);
        int effectiveIncrement = increment == 0 ? 1 : increment;
        int minute = wholeMinutes % 60;
        wholeMinutes -= minute % effectiveIncrement;
        return TimeSpan.FromMinutes(wholeMinutes);
    }

    private static DependencyProperty Register<T>(
        string name,
        T defaultValue,
        PropertyChangedCallback? callback = null) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(TimePicker),
            new PropertyMetadata(defaultValue, callback) { AffectsMeasure = true, AffectsRender = true });
}
