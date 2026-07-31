using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using WinRT;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Xaml.Media.Animation;

public enum FillBehavior
{
    HoldEnd = 0,
    Stop = 1
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public enum ClockState
{
    Active = 0,
    Filling = 1,
    Stopped = 2
}

public enum RepeatBehaviorType
{
    Count = 0,
    Duration = 1,
    Forever = 2
}

public struct RepeatBehavior : IFormattable
{
    public RepeatBehavior(double count)
    {
        if (!double.IsFinite(count) || count < 0d)
            throw new ArgumentOutOfRangeException(nameof(count));

        Count = count;
        Duration = TimeSpan.Zero;
        Type = RepeatBehaviorType.Count;
    }

    public RepeatBehavior(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        Count = 0d;
        Duration = duration;
        Type = RepeatBehaviorType.Duration;
    }

    public static RepeatBehavior Forever => new()
    {
        Type = RepeatBehaviorType.Forever
    };

    public double Count { get; set; }

    public TimeSpan Duration { get; set; }

    public RepeatBehaviorType Type { get; set; }

    public bool HasCount => Type == RepeatBehaviorType.Count;

    public bool HasDuration => Type == RepeatBehaviorType.Duration;

    public bool Equals(RepeatBehavior repeatBehavior) =>
        Type == repeatBehavior.Type &&
        Count.Equals(repeatBehavior.Count) &&
        Duration.Equals(repeatBehavior.Duration);

    public override bool Equals(object? value) =>
        value is RepeatBehavior repeatBehavior && Equals(repeatBehavior);

    public static bool Equals(
        RepeatBehavior repeatBehavior1,
        RepeatBehavior repeatBehavior2) =>
        repeatBehavior1.Equals(repeatBehavior2);

    public override int GetHashCode() =>
        HashCode.Combine((int)Type, Count, Duration);

    public override string ToString() =>
        ToString(CultureInfo.CurrentCulture);

    public string ToString(IFormatProvider? formatProvider) => Type switch
    {
        RepeatBehaviorType.Forever => "Forever",
        RepeatBehaviorType.Duration => Duration.ToString(null, formatProvider),
        _ => Count.ToString(formatProvider) + "x"
    };

    string IFormattable.ToString(
        string? format,
        IFormatProvider? formatProvider) =>
        ToString(formatProvider);

    public static bool operator ==(
        RepeatBehavior repeatBehavior1,
        RepeatBehavior repeatBehavior2) =>
        Equals(repeatBehavior1, repeatBehavior2);

    public static bool operator !=(
        RepeatBehavior repeatBehavior1,
        RepeatBehavior repeatBehavior2) =>
        !Equals(repeatBehavior1, repeatBehavior2);
}

/// <summary>Base object for time-based WinUI animation declarations.</summary>
[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public class Timeline : DependencyObject
{
    public static DependencyProperty AutoReverseProperty { get; } = DependencyProperty.Register(
        nameof(AutoReverse), typeof(bool), typeof(Timeline),
        new PropertyMetadata(false));
    public static DependencyProperty BeginTimeProperty { get; } = DependencyProperty.Register(
        nameof(BeginTime), typeof(TimeSpan?), typeof(Timeline),
        new PropertyMetadata(null));
    public static DependencyProperty DurationProperty { get; } = DependencyProperty.Register(
        nameof(Duration), typeof(Duration), typeof(Timeline),
        new PropertyMetadata(Duration.Automatic));
    public static DependencyProperty FillBehaviorProperty { get; } = DependencyProperty.Register(
        nameof(FillBehavior), typeof(FillBehavior), typeof(Timeline),
        new PropertyMetadata(FillBehavior.HoldEnd));
    public static DependencyProperty RepeatBehaviorProperty { get; } = DependencyProperty.Register(
        nameof(RepeatBehavior), typeof(RepeatBehavior), typeof(Timeline),
        new PropertyMetadata(new RepeatBehavior(1d)));
    public static DependencyProperty SpeedRatioProperty { get; } = DependencyProperty.Register(
        nameof(SpeedRatio), typeof(double), typeof(Timeline),
        new PropertyMetadata(1d));

    protected Timeline()
    {
    }

    protected internal Timeline(IObjectReference objRef)
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected Timeline(DerivedComposed _)
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    public bool AllowDependentAnimations { get; set; }

    public bool AutoReverse
    {
        get => (bool)(GetValue(AutoReverseProperty) ?? false);
        set => SetValue(AutoReverseProperty, value);
    }
    public TimeSpan? BeginTime
    {
        get => (TimeSpan?)GetValue(BeginTimeProperty);
        set => SetValue(BeginTimeProperty, value);
    }
    public Duration Duration
    {
        get => (Duration)(GetValue(DurationProperty) ?? Duration.Automatic);
        set => SetValue(DurationProperty, value);
    }
    public FillBehavior FillBehavior
    {
        get => (FillBehavior)(GetValue(FillBehaviorProperty) ?? FillBehavior.HoldEnd);
        set => SetValue(FillBehaviorProperty, value);
    }
    public RepeatBehavior RepeatBehavior
    {
        get => (RepeatBehavior)(
            GetValue(RepeatBehaviorProperty) ?? new RepeatBehavior(1d));
        set => SetValue(RepeatBehaviorProperty, value);
    }
    public double SpeedRatio
    {
        get => (double)(GetValue(SpeedRatioProperty) ?? 1d);
        set => SetValue(SpeedRatioProperty, value);
    }
    public event EventHandler<object>? Completed;

    internal void RaiseCompleted() => Completed?.Invoke(this, EventArgs.Empty);
}

/// <summary>Identifies a point in an animation timeline.</summary>
public readonly struct KeyTime : IEquatable<KeyTime>
{
    private KeyTime(TimeSpan timeSpan) => TimeSpan = timeSpan;

    public TimeSpan TimeSpan { get; }

    public static KeyTime FromTimeSpan(TimeSpan timeSpan) => new(timeSpan);

    public static implicit operator KeyTime(TimeSpan timeSpan) => FromTimeSpan(timeSpan);

    public bool Equals(KeyTime other) => TimeSpan.Equals(other.TimeSpan);
    public override bool Equals(object? obj) => obj is KeyTime other && Equals(other);
    public override int GetHashCode() => TimeSpan.GetHashCode();
    public override string ToString() => TimeSpan.ToString("c", CultureInfo.InvariantCulture);
}

public abstract class ObjectKeyFrame : DependencyObject
{
    public static readonly DependencyProperty KeyTimeProperty = DependencyProperty.Register(
        nameof(KeyTime), typeof(KeyTime), typeof(ObjectKeyFrame),
        new PropertyMetadata(default(KeyTime)));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(object), typeof(ObjectKeyFrame),
        new PropertyMetadata(null));

    public KeyTime KeyTime
    {
        get => (KeyTime)(GetValue(KeyTimeProperty) ?? default(KeyTime));
        set => SetValue(KeyTimeProperty, value);
    }
    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}

public sealed class DiscreteObjectKeyFrame : ObjectKeyFrame
{
}

[ContentProperty(Name = nameof(KeyFrames))]
public sealed class ObjectAnimationUsingKeyFrames : Timeline
{
    public Collection<ObjectKeyFrame> KeyFrames { get; } = new();
    public bool EnableDependentAnimation { get; set; }
}

public sealed class PointerDownThemeAnimation : Timeline
{
    public string TargetName { get; set; } = string.Empty;
}

public sealed class PointerUpThemeAnimation : Timeline
{
    public string TargetName { get; set; } = string.Empty;
}

public sealed class DoubleAnimation : Timeline
{
    public static readonly DependencyProperty ByProperty = DependencyProperty.Register(
        nameof(By), typeof(double?), typeof(DoubleAnimation), new PropertyMetadata(null));
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(double?), typeof(DoubleAnimation), new PropertyMetadata(null));
    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(double?), typeof(DoubleAnimation), new PropertyMetadata(null));
    public static readonly DependencyProperty EasingFunctionProperty = DependencyProperty.Register(
        nameof(EasingFunction), typeof(object), typeof(DoubleAnimation), new PropertyMetadata(null));
    public static readonly DependencyProperty EnableDependentAnimationProperty = DependencyProperty.Register(
        nameof(EnableDependentAnimation), typeof(bool), typeof(DoubleAnimation), new PropertyMetadata(false));

    public double? By
    {
        get => (double?)GetValue(ByProperty);
        set => SetValue(ByProperty, value);
    }
    public double? From
    {
        get => (double?)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }
    public double? To
    {
        get => (double?)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }
    public object? EasingFunction
    {
        get => GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }
    public bool EnableDependentAnimation
    {
        get => (bool)(GetValue(EnableDependentAnimationProperty) ?? false);
        set => SetValue(EnableDependentAnimationProperty, value);
    }
}

public sealed class ColorAnimation : Timeline
{
    public static readonly DependencyProperty ByProperty = DependencyProperty.Register(
        nameof(By), typeof(Windows.UI.Color?), typeof(ColorAnimation), new PropertyMetadata(null));
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(Windows.UI.Color?), typeof(ColorAnimation), new PropertyMetadata(null));
    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(Windows.UI.Color?), typeof(ColorAnimation), new PropertyMetadata(null));
    public static readonly DependencyProperty EasingFunctionProperty = DependencyProperty.Register(
        nameof(EasingFunction), typeof(object), typeof(ColorAnimation), new PropertyMetadata(null));
    public static readonly DependencyProperty EnableDependentAnimationProperty = DependencyProperty.Register(
        nameof(EnableDependentAnimation), typeof(bool), typeof(ColorAnimation), new PropertyMetadata(false));

    public Windows.UI.Color? By
    {
        get => (Windows.UI.Color?)GetValue(ByProperty);
        set => SetValue(ByProperty, value);
    }
    public Windows.UI.Color? From
    {
        get => (Windows.UI.Color?)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }
    public Windows.UI.Color? To
    {
        get => (Windows.UI.Color?)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }
    public object? EasingFunction
    {
        get => GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }
    public bool EnableDependentAnimation
    {
        get => (bool)(GetValue(EnableDependentAnimationProperty) ?? false);
        set => SetValue(EnableDependentAnimationProperty, value);
    }
}

public abstract class DoubleKeyFrame : DependencyObject
{
    public static readonly DependencyProperty KeyTimeProperty = DependencyProperty.Register(
        nameof(KeyTime), typeof(KeyTime), typeof(DoubleKeyFrame),
        new PropertyMetadata(default(KeyTime)));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(DoubleKeyFrame),
        new PropertyMetadata(0d));

    public KeyTime KeyTime
    {
        get => (KeyTime)(GetValue(KeyTimeProperty) ?? default(KeyTime));
        set => SetValue(KeyTimeProperty, value);
    }
    public double Value
    {
        get => (double)(GetValue(ValueProperty) ?? 0d);
        set => SetValue(ValueProperty, value);
    }
}

public sealed class DiscreteDoubleKeyFrame : DoubleKeyFrame
{
}

public sealed class LinearDoubleKeyFrame : DoubleKeyFrame
{
}

public sealed class SplineDoubleKeyFrame : DoubleKeyFrame
{
    public KeySpline KeySpline { get; set; } = new();
}

public abstract class EasingFunctionBase : DependencyObject
{
}

public sealed class EasingDoubleKeyFrame : DoubleKeyFrame
{
    public EasingFunctionBase? EasingFunction { get; set; }
}

public sealed class FadeInThemeAnimation : Timeline
{
    public string TargetName { get; set; } = string.Empty;
}

public sealed class FadeOutThemeAnimation : Timeline
{
    public string TargetName { get; set; } = string.Empty;
}

public sealed class DragItemThemeAnimation : Timeline
{
    public string TargetName { get; set; } = string.Empty;
}

public sealed class DropTargetItemThemeAnimation : Timeline
{
    public string TargetName { get; set; } = string.Empty;
}

public sealed class DragOverThemeAnimation : Timeline
{
    public static readonly DependencyProperty ToOffsetProperty = DependencyProperty.Register(
        nameof(ToOffset), typeof(double), typeof(DragOverThemeAnimation), new PropertyMetadata(0d));

    public string TargetName { get; set; } = string.Empty;
    public double ToOffset { get => (double)(GetValue(ToOffsetProperty) ?? 0d); set => SetValue(ToOffsetProperty, value); }
    public global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection Direction { get; set; }
}

public sealed class RepositionThemeAnimation : Timeline
{
    public static readonly DependencyProperty FromHorizontalOffsetProperty = DependencyProperty.Register(
        nameof(FromHorizontalOffset), typeof(double), typeof(RepositionThemeAnimation), new PropertyMetadata(0d));
    public static readonly DependencyProperty FromVerticalOffsetProperty = DependencyProperty.Register(
        nameof(FromVerticalOffset), typeof(double), typeof(RepositionThemeAnimation), new PropertyMetadata(0d));

    public string TargetName { get; set; } = string.Empty;
    public double FromHorizontalOffset { get => (double)(GetValue(FromHorizontalOffsetProperty) ?? 0d); set => SetValue(FromHorizontalOffsetProperty, value); }
    public double FromVerticalOffset { get => (double)(GetValue(FromVerticalOffsetProperty) ?? 0d); set => SetValue(FromVerticalOffsetProperty, value); }
}

public sealed class SplitOpenThemeAnimation : Timeline
{
    public static readonly DependencyProperty OpenedTargetNameProperty = Register(nameof(OpenedTargetName), typeof(string), string.Empty);
    public static readonly DependencyProperty OpenedTargetProperty = Register(nameof(OpenedTarget), typeof(DependencyObject), null);
    public static readonly DependencyProperty ClosedTargetNameProperty = Register(nameof(ClosedTargetName), typeof(string), string.Empty);
    public static readonly DependencyProperty ClosedTargetProperty = Register(nameof(ClosedTarget), typeof(DependencyObject), null);
    public static readonly DependencyProperty ContentTargetNameProperty = Register(nameof(ContentTargetName), typeof(string), string.Empty);
    public static readonly DependencyProperty ContentTargetProperty = Register(nameof(ContentTarget), typeof(DependencyObject), null);
    public static readonly DependencyProperty OpenedLengthProperty = Register(nameof(OpenedLength), typeof(double), 0d);
    public static readonly DependencyProperty ClosedLengthProperty = Register(nameof(ClosedLength), typeof(double), 0d);
    public static readonly DependencyProperty OffsetFromCenterProperty = Register(nameof(OffsetFromCenter), typeof(double), 0d);
    public static readonly DependencyProperty ContentTranslationDirectionProperty = Register(
        nameof(ContentTranslationDirection),
        typeof(global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection),
        default(global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection));
    public static readonly DependencyProperty ContentTranslationOffsetProperty = Register(nameof(ContentTranslationOffset), typeof(double), 0d);

    public string OpenedTargetName { get => (string)(GetValue(OpenedTargetNameProperty) ?? string.Empty); set => SetValue(OpenedTargetNameProperty, value ?? string.Empty); }
    public DependencyObject? OpenedTarget { get => GetValue(OpenedTargetProperty) as DependencyObject; set => SetValue(OpenedTargetProperty, value); }
    public string ClosedTargetName { get => (string)(GetValue(ClosedTargetNameProperty) ?? string.Empty); set => SetValue(ClosedTargetNameProperty, value ?? string.Empty); }
    public DependencyObject? ClosedTarget { get => GetValue(ClosedTargetProperty) as DependencyObject; set => SetValue(ClosedTargetProperty, value); }
    public string ContentTargetName { get => (string)(GetValue(ContentTargetNameProperty) ?? string.Empty); set => SetValue(ContentTargetNameProperty, value ?? string.Empty); }
    public DependencyObject? ContentTarget { get => GetValue(ContentTargetProperty) as DependencyObject; set => SetValue(ContentTargetProperty, value); }
    public double OpenedLength { get => (double)(GetValue(OpenedLengthProperty) ?? 0d); set => SetValue(OpenedLengthProperty, value); }
    public double ClosedLength { get => (double)(GetValue(ClosedLengthProperty) ?? 0d); set => SetValue(ClosedLengthProperty, value); }
    public double OffsetFromCenter { get => (double)(GetValue(OffsetFromCenterProperty) ?? 0d); set => SetValue(OffsetFromCenterProperty, value); }
    public global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection ContentTranslationDirection
    {
        get => (global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection)(
            GetValue(ContentTranslationDirectionProperty) ??
            default(global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection));
        set => SetValue(ContentTranslationDirectionProperty, value);
    }
    public double ContentTranslationOffset { get => (double)(GetValue(ContentTranslationOffsetProperty) ?? 0d); set => SetValue(ContentTranslationOffsetProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(SplitOpenThemeAnimation), new PropertyMetadata(defaultValue));
}

public sealed class SplitCloseThemeAnimation : Timeline
{
    public static readonly DependencyProperty OpenedTargetNameProperty = Register(nameof(OpenedTargetName), typeof(string), string.Empty);
    public static readonly DependencyProperty OpenedTargetProperty = Register(nameof(OpenedTarget), typeof(DependencyObject), null);
    public static readonly DependencyProperty ClosedTargetNameProperty = Register(nameof(ClosedTargetName), typeof(string), string.Empty);
    public static readonly DependencyProperty ClosedTargetProperty = Register(nameof(ClosedTarget), typeof(DependencyObject), null);
    public static readonly DependencyProperty ContentTargetNameProperty = Register(nameof(ContentTargetName), typeof(string), string.Empty);
    public static readonly DependencyProperty ContentTargetProperty = Register(nameof(ContentTarget), typeof(DependencyObject), null);
    public static readonly DependencyProperty OpenedLengthProperty = Register(nameof(OpenedLength), typeof(double), 0d);
    public static readonly DependencyProperty ClosedLengthProperty = Register(nameof(ClosedLength), typeof(double), 0d);
    public static readonly DependencyProperty OffsetFromCenterProperty = Register(nameof(OffsetFromCenter), typeof(double), 0d);
    public static readonly DependencyProperty ContentTranslationDirectionProperty = Register(
        nameof(ContentTranslationDirection),
        typeof(global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection),
        default(global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection));
    public static readonly DependencyProperty ContentTranslationOffsetProperty = Register(nameof(ContentTranslationOffset), typeof(double), 0d);

    public string OpenedTargetName { get => (string)(GetValue(OpenedTargetNameProperty) ?? string.Empty); set => SetValue(OpenedTargetNameProperty, value ?? string.Empty); }
    public DependencyObject? OpenedTarget { get => GetValue(OpenedTargetProperty) as DependencyObject; set => SetValue(OpenedTargetProperty, value); }
    public string ClosedTargetName { get => (string)(GetValue(ClosedTargetNameProperty) ?? string.Empty); set => SetValue(ClosedTargetNameProperty, value ?? string.Empty); }
    public DependencyObject? ClosedTarget { get => GetValue(ClosedTargetProperty) as DependencyObject; set => SetValue(ClosedTargetProperty, value); }
    public string ContentTargetName { get => (string)(GetValue(ContentTargetNameProperty) ?? string.Empty); set => SetValue(ContentTargetNameProperty, value ?? string.Empty); }
    public DependencyObject? ContentTarget { get => GetValue(ContentTargetProperty) as DependencyObject; set => SetValue(ContentTargetProperty, value); }
    public double OpenedLength { get => (double)(GetValue(OpenedLengthProperty) ?? 0d); set => SetValue(OpenedLengthProperty, value); }
    public double ClosedLength { get => (double)(GetValue(ClosedLengthProperty) ?? 0d); set => SetValue(ClosedLengthProperty, value); }
    public double OffsetFromCenter { get => (double)(GetValue(OffsetFromCenterProperty) ?? 0d); set => SetValue(OffsetFromCenterProperty, value); }
    public global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection ContentTranslationDirection
    {
        get => (global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection)(
            GetValue(ContentTranslationDirectionProperty) ??
            default(global::Microsoft.UI.Xaml.Controls.Primitives.AnimationDirection));
        set => SetValue(ContentTranslationDirectionProperty, value);
    }
    public double ContentTranslationOffset { get => (double)(GetValue(ContentTranslationOffsetProperty) ?? 0d); set => SetValue(ContentTranslationOffsetProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(SplitCloseThemeAnimation), new PropertyMetadata(defaultValue));
}

public sealed class KeySpline : DependencyObject
{
    public Windows.Foundation.Point ControlPoint1 { get; set; }
    public Windows.Foundation.Point ControlPoint2 { get; set; } = new(1d, 1d);
}

[ContentProperty(Name = nameof(KeyFrames))]
public sealed class DoubleAnimationUsingKeyFrames : Timeline
{
    public Collection<DoubleKeyFrame> KeyFrames { get; } = new();
    public bool EnableDependentAnimation { get; set; }
}

[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class TimelineCollection : IList<Timeline>
{
    private readonly List<Timeline> _items = new();

    [IndexerName("ListItem")]
    public Timeline this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public void Add(Timeline item) => _items.Add(item);

    public void Clear() => _items.Clear();

    public bool Contains(Timeline item) => _items.Contains(item);

    public void CopyTo(Timeline[] array, int arrayIndex) =>
        _items.CopyTo(array, arrayIndex);

    public IEnumerator<Timeline> GetEnumerator() =>
        _items.GetEnumerator();

    public int IndexOf(Timeline item) => _items.IndexOf(item);

    public void Insert(int index, Timeline item) =>
        _items.Insert(index, item);

    public bool Remove(Timeline item) => _items.Remove(item);

    public void RemoveAt(int index) => _items.RemoveAt(index);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

[ContentProperty(Name = nameof(Children))]
[ContractVersion(
    "Microsoft.UI.Xaml.WinUIContract",
    0x00010000)]
public sealed class Storyboard : Timeline
{
    public static DependencyProperty TargetNameProperty { get; } = DependencyProperty.RegisterAttached(
        "TargetName", typeof(string), typeof(Storyboard), new PropertyMetadata(string.Empty));

    public static DependencyProperty TargetPropertyProperty { get; } = DependencyProperty.RegisterAttached(
        "TargetProperty", typeof(string), typeof(Storyboard), new PropertyMetadata(string.Empty));

    public new TimelineCollection Children { get; } = new();

    public static string GetTargetName(Timeline element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(TargetNameProperty) ?? string.Empty;
    }

    public static void SetTargetName(Timeline element, string name)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TargetNameProperty, name ?? string.Empty);
    }

    public static string GetTargetProperty(Timeline element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(TargetPropertyProperty) ?? string.Empty;
    }

    public static void SetTargetProperty(Timeline element, string path)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TargetPropertyProperty, path ?? string.Empty);
    }

    public static void SetTarget(
        Timeline timeline,
        DependencyObject target)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(target);
        timeline.SetValue(TargetProperty, target);
    }

    internal static DependencyObject? GetTarget(Timeline timeline) =>
        timeline.GetValue(TargetProperty) as DependencyObject;

    internal static readonly DependencyProperty TargetProperty =
        DependencyProperty.RegisterAttached(
            "Target",
            typeof(DependencyObject),
            typeof(Storyboard),
            new PropertyMetadata(null));
}
