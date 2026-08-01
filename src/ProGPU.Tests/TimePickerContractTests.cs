using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Xunit;

namespace ProGPU.Tests;

public sealed class TimePickerContractTests
{
    [Fact]
    public void DefaultsPreserveTheOfficialUnsetState()
    {
        var picker = new TimePicker();

        Assert.Equal("12HourClock", picker.ClockIdentifier);
        Assert.Equal(1, picker.MinuteIncrement);
        Assert.Equal(TimeSpan.Zero, picker.Time);
        Assert.Null(picker.SelectedTime);
    }

    [Fact]
    public void TimeTruncatesSubMinutePrecisionAndSynchronizesSelectedTime()
    {
        var picker = new TimePicker { MinuteIncrement = 15 };
        TimePickerValueChangedEventArgs? timeChanged = null;
        TimePickerSelectedValueChangedEventArgs? selectedChanged = null;
        picker.TimeChanged += (_, args) => timeChanged = args;
        picker.SelectedTimeChanged += (_, args) => selectedChanged = args;

        picker.Time = new TimeSpan(0, 14, 38, 59, 999);

        var expected = new TimeSpan(14, 30, 0);
        Assert.Equal(expected, picker.Time);
        Assert.Equal(expected, picker.SelectedTime);
        Assert.Equal(TimeSpan.Zero, timeChanged!.OldTime);
        Assert.Equal(expected, timeChanged.NewTime);
        Assert.Null(selectedChanged!.OldTime);
        Assert.Equal(expected, selectedChanged.NewTime);
    }

    [Fact]
    public void ClearingSelectedTimeResetsTimeAndRaisesBothChangeEvents()
    {
        var picker = new TimePicker
        {
            SelectedTime = new TimeSpan(10, 7, 58)
        };
        TimePickerValueChangedEventArgs? timeChanged = null;
        TimePickerSelectedValueChangedEventArgs? selectedChanged = null;
        picker.TimeChanged += (_, args) => timeChanged = args;
        picker.SelectedTimeChanged += (_, args) => selectedChanged = args;

        picker.SelectedTime = null;

        Assert.Null(picker.SelectedTime);
        Assert.Equal(TimeSpan.Zero, picker.Time);
        Assert.Equal(new TimeSpan(10, 7, 0), timeChanged!.OldTime);
        Assert.Equal(TimeSpan.Zero, timeChanged.NewTime);
        Assert.Equal(new TimeSpan(10, 7, 0), selectedChanged!.OldTime);
        Assert.Null(selectedChanged.NewTime);
    }

    [Fact]
    public void ValuesOutsideOneDayFailWithoutChangingState()
    {
        var picker = new TimePicker
        {
            Time = new TimeSpan(12, 34, 0)
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => picker.Time = TimeSpan.FromTicks(-1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => picker.Time = TimeSpan.FromDays(1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => picker.SelectedTime = TimeSpan.FromHours(25));

        Assert.Equal(new TimeSpan(12, 34, 0), picker.Time);
        Assert.Equal(new TimeSpan(12, 34, 0), picker.SelectedTime);
    }

    [Fact]
    public void MinuteIncrementAcceptsDocumentedRangeAndRecoercesTime()
    {
        var picker = new TimePicker
        {
            Time = new TimeSpan(8, 38, 47)
        };

        picker.MinuteIncrement = 15;
        Assert.Equal(new TimeSpan(8, 30, 0), picker.Time);

        picker.MinuteIncrement = 0;
        picker.Time = new TimeSpan(8, 39, 47);
        Assert.Equal(new TimeSpan(8, 39, 0), picker.Time);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => picker.MinuteIncrement = -1);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => picker.MinuteIncrement = 60);
        Assert.Equal(0, picker.MinuteIncrement);
    }

    [Fact]
    public void ClockIdentifierAcceptsOnlyOfficialClockSystems()
    {
        var picker = new TimePicker();

        picker.ClockIdentifier = "24HourClock";
        Assert.Equal("24HourClock", picker.ClockIdentifier);
        picker.ClockIdentifier = "12HourClock";

        Assert.Throws<ArgumentException>(
            () => picker.ClockIdentifier = "GregorianCalendar");
        Assert.Throws<ArgumentException>(
            () => picker.ClockIdentifier = null!);
        Assert.Equal("12HourClock", picker.ClockIdentifier);
    }

    [Fact]
    public void RejectedClockIdentifierDependencyValuesAreTransactional()
    {
        var picker = new TimePicker
        {
            ClockIdentifier = "24HourClock"
        };

        Assert.Throws<ArgumentException>(() => picker.SetValue(
            TimePicker.ClockIdentifierProperty,
            "GregorianCalendar"));
        Assert.Throws<ArgumentException>(() => picker.SetValue(
            TimePicker.ClockIdentifierProperty,
            null));

        Assert.Equal("24HourClock", picker.ClockIdentifier);
        Assert.Equal(
            "24HourClock",
            picker.GetValue(TimePicker.ClockIdentifierProperty));
    }

    [Fact]
    public void ClockIdentifierValidationCoversEveryDependencyValueSource()
    {
        var styled = new TimePicker();
        styled.SetStyleValue(
            TimePicker.ClockIdentifierProperty,
            "24HourClock");
        Assert.Throws<ArgumentException>(() => styled.SetStyleValue(
            TimePicker.ClockIdentifierProperty,
            "GregorianCalendar"));
        Assert.Equal("24HourClock", styled.ClockIdentifier);

        var defaultStyled = new TimePicker();
        defaultStyled.SetDefaultStyleValue(
            TimePicker.ClockIdentifierProperty,
            "24HourClock");
        Assert.Throws<ArgumentException>(() =>
            defaultStyled.SetDefaultStyleValue(
                TimePicker.ClockIdentifierProperty,
                null));
        Assert.Equal("24HourClock", defaultStyled.ClockIdentifier);

        var animated = new TimePicker();
        animated.SetAnimatedValue(
            TimePicker.ClockIdentifierProperty,
            "24HourClock");
        Assert.Throws<ArgumentException>(() => animated.SetAnimatedValue(
            TimePicker.ClockIdentifierProperty,
            "GregorianCalendar"));
        Assert.Equal("24HourClock", animated.ClockIdentifier);
    }

    [Fact]
    public void RejectedThemeReevaluationRetainsThePreviousClockValue()
    {
        var picker = new TimePicker();
        picker.Resources["Clock"] = "24HourClock";
        picker.SetStyleValue(
            TimePicker.ClockIdentifierProperty,
            new ThemeResource(picker, "Clock"));
        Assert.Equal("24HourClock", picker.ClockIdentifier);

        Assert.Throws<ArgumentException>(() =>
            picker.Resources["Clock"] = "GregorianCalendar");
        Assert.Equal(
            "24HourClock",
            ReadResolvedLayer(
                picker,
                "_styleValues",
                TimePicker.ClockIdentifierProperty));

        picker.Resources["Clock"] = "12HourClock";
        Assert.Equal("12HourClock", picker.ClockIdentifier);
    }

    [Fact]
    public void RejectedMultiLayerThemeReevaluationRollsBackEveryLayer()
    {
        var localResources = new ResourceDictionary
        {
            ["LocalClock"] = "12HourClock"
        };
        var styleResources = new ResourceDictionary
        {
            ["StyleClock"] = "24HourClock"
        };
        var picker = new TimePicker();
        picker.SetStyleValue(
            TimePicker.ClockIdentifierProperty,
            new ThemeResource(styleResources, "StyleClock"));
        picker.SetValue(
            TimePicker.ClockIdentifierProperty,
            new ThemeResource(localResources, "LocalClock"));
        Assert.Equal("12HourClock", picker.ClockIdentifier);

        localResources["LocalClock"] = "24HourClock";
        styleResources["StyleClock"] = "GregorianCalendar";

        Assert.Throws<ArgumentException>(picker.ReevaluateThemeResources);
        Assert.Equal(
            "12HourClock",
            ReadResolvedLayer(
                picker,
                "_localValues",
                TimePicker.ClockIdentifierProperty));
        Assert.Equal(
            "24HourClock",
            ReadResolvedLayer(
                picker,
                "_styleValues",
                TimePicker.ClockIdentifierProperty));

        styleResources["StyleClock"] = "24HourClock";
        Assert.Equal("24HourClock", picker.ClockIdentifier);
    }

    [Fact]
    public void RejectedThemeReevaluationRemainsDirtyUntilResourceRecovery()
    {
        var resources = new ResourceDictionary
        {
            ["Clock"] = "24HourClock"
        };
        var picker = new TimePicker();
        picker.SetStyleValue(
            TimePicker.ClockIdentifierProperty,
            new ThemeResource(resources, "Clock"));
        Assert.Equal("24HourClock", picker.ClockIdentifier);

        resources["Clock"] = "GregorianCalendar";
        Assert.Throws<ArgumentException>(
            picker.ReevaluateThemeResources);

        resources["Clock"] = "12HourClock";
        Assert.Equal("12HourClock", picker.ClockIdentifier);
    }

    [Fact]
    public void DependencyPropertyAssignmentUsesTheSameCoercionPath()
    {
        var picker = new TimePicker { MinuteIncrement = 15 };
        TimePickerValueChangedEventArgs? changed = null;
        picker.TimeChanged += (_, args) => changed = args;

        picker.SetValue(
            TimePicker.TimeProperty,
            new TimeSpan(6, 59, 59));

        Assert.Equal(new TimeSpan(6, 45, 0), picker.Time);
        Assert.Equal(TimeSpan.Zero, changed!.OldTime);
        Assert.Equal(new TimeSpan(6, 45, 0), changed.NewTime);
    }

    [Fact]
    public void PublicMetadataMatchesTheOfficialProjectionShape()
    {
        Assert.Equal(typeof(object), typeof(TimePickerValueChangedEventArgs).BaseType);
        Assert.Equal(typeof(object), typeof(TimePickerSelectedValueChangedEventArgs).BaseType);
        Assert.Empty(typeof(TimePickerValueChangedEventArgs).GetConstructors());
        Assert.Empty(typeof(TimePickerSelectedValueChangedEventArgs).GetConstructors());

        EventInfo? selectedTimeChanged =
            typeof(TimePicker).GetEvent(nameof(TimePicker.SelectedTimeChanged));
        Assert.NotNull(selectedTimeChanged);
        Assert.Equal(
            typeof(TypedEventHandler<
                TimePicker,
                TimePickerSelectedValueChangedEventArgs>),
            selectedTimeChanged!.EventHandlerType);

        string[] dependencyPropertyNames =
        [
            nameof(TimePicker.HeaderProperty),
            nameof(TimePicker.HeaderTemplateProperty),
            nameof(TimePicker.ClockIdentifierProperty),
            nameof(TimePicker.MinuteIncrementProperty),
            nameof(TimePicker.TimeProperty),
            nameof(TimePicker.SelectedTimeProperty),
            nameof(TimePicker.LightDismissOverlayModeProperty)
        ];
        foreach (string name in dependencyPropertyNames)
        {
            Assert.Null(typeof(TimePicker).GetField(name));
            PropertyInfo? property = typeof(TimePicker).GetProperty(name);
            Assert.NotNull(property);
            Assert.Equal(typeof(DependencyProperty), property!.PropertyType);
            Assert.NotNull(property.GetMethod);
            Assert.Null(property.SetMethod);
        }

        AssertOfficialContract(typeof(TimePicker));
        AssertOfficialContract(typeof(TimePickerValueChangedEventArgs));
        AssertOfficialContract(typeof(TimePickerSelectedValueChangedEventArgs));
    }

    private static object? ReadResolvedLayer(
        DependencyObject target,
        string fieldName,
        DependencyProperty property)
    {
        var values = Assert.IsType<object?[]>(
            typeof(DependencyObject)
                .GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.NonPublic)!
                .GetValue(target));
        return values[property.Index];
    }

    [Fact]
    public void DependencyPropertyIdentifierReadsAreAllocationFreeAfterWarmup()
    {
        _ = TimePicker.HeaderProperty;
        _ = TimePicker.HeaderTemplateProperty;
        _ = TimePicker.ClockIdentifierProperty;
        _ = TimePicker.MinuteIncrementProperty;
        _ = TimePicker.TimeProperty;
        _ = TimePicker.SelectedTimeProperty;
        _ = TimePicker.LightDismissOverlayModeProperty;
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;

        for (int iteration = 0; iteration < 100_000; iteration++)
        {
            checksum ^= TimePicker.HeaderProperty.Index;
            checksum ^= TimePicker.HeaderTemplateProperty.Index;
            checksum ^= TimePicker.ClockIdentifierProperty.Index;
            checksum ^= TimePicker.MinuteIncrementProperty.Index;
            checksum ^= TimePicker.TimeProperty.Index;
            checksum ^= TimePicker.SelectedTimeProperty.Index;
            checksum ^= TimePicker.LightDismissOverlayModeProperty.Index;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    private static void AssertOfficialContract(Type type)
    {
        CustomAttributeData attribute = Assert.Single(
            type.GetCustomAttributesData(),
            static candidate =>
                candidate.AttributeType == typeof(ContractVersionAttribute));
        Assert.Equal(
            "Microsoft.UI.Xaml.WinUIContract",
            Assert.IsType<string>(attribute.ConstructorArguments[0].Value));
        Assert.Equal(
            0x00010000u,
            Assert.IsType<uint>(attribute.ConstructorArguments[1].Value));
    }
}
