using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Animation;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml;

public abstract class StateTriggerBase : DependencyObject
{
    internal abstract bool IsActive(float windowWidth, float windowHeight);
}

public sealed class AdaptiveTrigger : StateTriggerBase
{
    public double MinWindowWidth { get; set; }
    public double MinWindowHeight { get; set; }

    internal override bool IsActive(float windowWidth, float windowHeight) =>
        windowWidth >= MinWindowWidth && windowHeight >= MinWindowHeight;
}

[ContentProperty(Name = nameof(Storyboard))]
public sealed class VisualState : DependencyObject
{
    public string Name { get; set; } = string.Empty;
    public Collection<Setter> Setters { get; } = new();
    public Collection<StateTriggerBase> StateTriggers { get; } = new();
    public Storyboard? Storyboard { get; set; }
}

public sealed class VisualStateChangedEventArgs : EventArgs
{
    internal VisualStateChangedEventArgs(VisualState? oldState, VisualState? newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    public VisualState? OldState { get; }
    public VisualState? NewState { get; }
}

[ContentProperty(Name = nameof(Storyboard))]
public class VisualTransition : DependencyObject
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public Duration GeneratedDuration { get; set; } = Duration.Automatic;
    public object? GeneratedEasingFunction { get; set; }
    public Storyboard? Storyboard { get; set; }
}

[ContentProperty(Name = nameof(States))]
public sealed class VisualStateGroup
{
    public string Name { get; set; } = string.Empty;
    public Collection<VisualState> States { get; } = new();
    public Collection<VisualTransition> Transitions { get; } = new();
    public VisualState? CurrentState { get; internal set; }
    public event EventHandler<VisualStateChangedEventArgs>? CurrentStateChanged;

    internal List<VisualStateAppliedProperty> AppliedProperties { get; } = new();
    internal List<VisualStateBindingRegistration> BindingRegistrations { get; } = new();

    internal void RaiseCurrentStateChanged(VisualState? oldState, VisualState? newState) =>
        CurrentStateChanged?.Invoke(this, new VisualStateChangedEventArgs(oldState, newState));
}

internal readonly record struct VisualStateAssignment(DependencyObject Target, DependencyProperty Property, object? Value);
internal readonly record struct VisualStateAppliedProperty(
    DependencyObject Target,
    DependencyProperty Property);

internal sealed class VisualStateBindingProxy : DependencyObject
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(object),
            typeof(VisualStateBindingProxy),
            new PropertyMetadata(
                null,
                static (dependencyObject, args) =>
                    ((VisualStateBindingProxy)dependencyObject)
                    .ValueChanged(args.NewValue)));

    private readonly Action<object?> _valueChanged;

    public VisualStateBindingProxy(Action<object?> valueChanged) =>
        _valueChanged = valueChanged;

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public void PublishCurrentValue() =>
        _valueChanged(GetValue(ValueProperty));

    private void ValueChanged(object? value) =>
        _valueChanged(value);
}

internal sealed class VisualStateBindingRegistration : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly object? _context;
    private readonly DependencyObject _target;
    private readonly DependencyProperty _property;
    private readonly Binding _binding;
    private readonly VisualStateBindingProxy _proxy;
    private bool _disposed;

    private VisualStateBindingRegistration(
        FrameworkElement root,
        object? context,
        DependencyObject target,
        DependencyProperty property,
        Binding binding)
    {
        _root = root;
        _context = context;
        _target = target;
        _property = property;
        _binding = binding;
        _proxy = new VisualStateBindingProxy(
            value => VisualStateManager.SetAnimatedXamlValue(
                _target,
                _property,
                value));
        BindingOperations.SetBinding(
            _proxy,
            VisualStateBindingProxy.ValueProperty,
            binding,
            context,
            lookupRoot: root);
        _proxy.PublishCurrentValue();
    }

    public static VisualStateBindingRegistration Create(
        FrameworkElement root,
        object? context,
        DependencyObject target,
        DependencyProperty property,
        Binding binding) =>
        new(root, context, target, property, binding);

    public VisualStateBindingRegistration Recreate() =>
        Create(_root, _context, _target, _property, _binding);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        BindingOperations.ClearBinding(
            _proxy,
            VisualStateBindingProxy.ValueProperty);
    }
}

public static class VisualStateManager
{
    private static readonly ConditionalWeakTable<FrameworkElement, Collection<VisualStateGroup>> Groups = new();

    public static IList<VisualStateGroup> GetVisualStateGroups(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Groups.GetOrCreateValue(element);
    }

    public static bool GoToState(Control control, string stateName, bool useTransitions)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);
        if (TryFindState(control, stateName, out var stateRoot, out var group, out var state))
        {
            ApplyState(stateRoot, group, state);
            return true;
        }
        return false;
    }

    internal static void UpdateAdaptiveStates(FrameworkElement root, Vector2 windowSize)
    {
        UpdateElement(root, windowSize.X, windowSize.Y);
    }

    private static void UpdateElement(FrameworkElement element, float width, float height)
    {
        if (Groups.TryGetValue(element, out var groups))
        {
            foreach (var group in groups)
            {
                VisualState? fallback = null;
                VisualState? active = null;
                foreach (var state in group.States)
                {
                    if (state.StateTriggers.Count == 0)
                    {
                        fallback ??= state;
                        continue;
                    }
                    for (int triggerIndex = 0; triggerIndex < state.StateTriggers.Count; triggerIndex++)
                    {
                        if (!state.StateTriggers[triggerIndex].IsActive(width, height)) continue;
                        active = state;
                        break;
                    }
                }
                ApplyState(element, group, active ?? fallback);
            }
        }

        if (element is ContainerVisual container)
        {
            IReadOnlyList<Visual> children = container.Children;
            for (int index = 0; index < children.Count; index++)
            {
                if (children[index] is FrameworkElement childElement)
                    UpdateElement(childElement, width, height);
            }
        }
    }

    private static void ApplyState(FrameworkElement root, VisualStateGroup group, VisualState? state)
    {
        if (ReferenceEquals(group.CurrentState, state)) return;

        var assignments = new List<VisualStateAssignment>();
        if (state != null)
        {
            foreach (var setter in state.Setters)
            {
                if (!TryResolveSetter(root, setter, out var target, out var property))
                    continue;
                assignments.Add(new VisualStateAssignment(target, property, setter.Value));
            }
            if (state.Storyboard != null)
                CollectStoryboardAssignments(root, state.Storyboard, assignments);
        }
        assignments = KeepLastAssignmentPerProperty(assignments);

        var oldState = group.CurrentState;
        var bindingContext =
            XamlTemplateFactory.FindTemplateContext(root) ??
            root;
        var oldProperties = group.AppliedProperties.ToArray();
        var oldValues = new object?[oldProperties.Length];
        for (int index = 0; index < oldProperties.Length; index++)
        {
            var oldProperty = oldProperties[index];
            oldValues[index] =
                oldProperty.Target.GetAnimatedXamlValue(oldProperty.Property);
        }
        var oldBindings = group.BindingRegistrations.ToArray();
        DisposeBindings(oldBindings);
        ClearAnimatedValues(oldProperties);
        group.AppliedProperties.Clear();
        group.BindingRegistrations.Clear();

        var applied = new HashSet<(DependencyObject Target, DependencyProperty Property)>();
        try
        {
            foreach (var assignment in assignments)
            {
                var key = (assignment.Target, assignment.Property);
                if (applied.Add(key))
                {
                    group.AppliedProperties.Add(new VisualStateAppliedProperty(
                        assignment.Target,
                        assignment.Property));
                }
                if (assignment.Value is Binding binding)
                {
                    group.BindingRegistrations.Add(
                        VisualStateBindingRegistration.Create(
                            root,
                            bindingContext,
                            assignment.Target,
                            assignment.Property,
                            binding));
                }
                else
                {
                    SetAnimatedXamlValue(
                        assignment.Target,
                        assignment.Property,
                        assignment.Value);
                }
            }
        }
        catch
        {
            DisposeBindings(group.BindingRegistrations);
            ClearAnimatedValues(group.AppliedProperties);
            group.BindingRegistrations.Clear();
            group.AppliedProperties.Clear();
            for (int index = 0; index < oldProperties.Length; index++)
            {
                var oldProperty = oldProperties[index];
                SetAnimatedXamlValue(
                    oldProperty.Target,
                    oldProperty.Property,
                    oldValues[index]);
                group.AppliedProperties.Add(oldProperty);
            }
            for (int index = 0; index < oldBindings.Length; index++)
                group.BindingRegistrations.Add(oldBindings[index].Recreate());
            throw;
        }

        group.CurrentState = state;
        group.RaiseCurrentStateChanged(oldState, state);
    }

    private static List<VisualStateAssignment> KeepLastAssignmentPerProperty(
        List<VisualStateAssignment> assignments)
    {
        if (assignments.Count < 2)
            return assignments;

        var result = new List<VisualStateAssignment>(assignments.Count);
        var indexes =
            new Dictionary<
                (DependencyObject Target, DependencyProperty Property),
                int>();
        for (int index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            var key = (assignment.Target, assignment.Property);
            if (indexes.TryGetValue(key, out var resultIndex))
                result[resultIndex] = assignment;
            else
            {
                indexes.Add(key, result.Count);
                result.Add(assignment);
            }
        }
        return result;
    }

    private static void ClearAnimatedValues(
        IReadOnlyList<VisualStateAppliedProperty> properties)
    {
        for (int index = properties.Count - 1; index >= 0; index--)
        {
            var property = properties[index];
            property.Target.ClearAnimatedValue(property.Property);
        }
    }

    private static void DisposeBindings(
        IReadOnlyList<VisualStateBindingRegistration> registrations)
    {
        for (int index = registrations.Count - 1; index >= 0; index--)
            registrations[index].Dispose();
    }

    internal static void SetAnimatedXamlValue(
        DependencyObject target,
        DependencyProperty property,
        object? value)
    {
        try
        {
            if (value is ThemeResource or ProGPU.Vector.ThemeResourceBrush)
            {
                target.SetAnimatedValue(property, value);
                return;
            }

            target.SetAnimatedValue(
                property,
                XamlValueConverter.ConvertTo(property.PropertyType, value));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Visual-state value '{value}' ({value?.GetType().FullName ?? "null"}) could not be assigned to " +
                $"'{target.GetType().FullName}.{property.Name}' ({property.PropertyType.FullName}).",
                exception);
        }
    }

    private static bool TryFindState(
        FrameworkElement root,
        string stateName,
        out FrameworkElement stateRoot,
        out VisualStateGroup group,
        out VisualState state)
    {
        if (Groups.TryGetValue(root, out var groups))
        {
            foreach (var candidateGroup in groups)
            {
                var candidateState = candidateGroup.States.FirstOrDefault(
                    item => string.Equals(item.Name, stateName, StringComparison.Ordinal));
                if (candidateState != null)
                {
                    stateRoot = root;
                    group = candidateGroup;
                    state = candidateState;
                    return true;
                }
            }
        }

        if (root is ContainerVisual container)
        {
            IReadOnlyList<Visual> children = container.Children;
            for (int index = 0; index < children.Count; index++)
            {
                if (children[index] is FrameworkElement child &&
                    TryFindState(child, stateName, out stateRoot, out group, out state))
                {
                    return true;
                }
            }
        }

        stateRoot = null!;
        group = null!;
        state = null!;
        return false;
    }

    private static void CollectStoryboardAssignments(
        FrameworkElement root,
        Storyboard storyboard,
        List<VisualStateAssignment> assignments)
    {
        foreach (var timeline in storyboard.Children)
        {
            if (timeline is Storyboard nested)
            {
                CollectStoryboardAssignments(root, nested, assignments);
                continue;
            }

            if (!TryResolveTimelineTarget(root, timeline, out var target, out var property))
                continue;

            switch (timeline)
            {
                case ObjectAnimationUsingKeyFrames objectAnimation:
                {
                    var frame = GetLastKeyFrame(objectAnimation.KeyFrames);
                    if (frame != null)
                    {
                        assignments.Add(new VisualStateAssignment(
                            target,
                            property,
                            frame.GetLocalOrEffectiveXamlValue(ObjectKeyFrame.ValueProperty)));
                    }
                    break;
                }
                case DoubleAnimation doubleAnimation:
                {
                    object? value = GetDoubleAnimationFinalValue(target, property, doubleAnimation);
                    if (value != null)
                        assignments.Add(new VisualStateAssignment(target, property, value));
                    break;
                }
                case DoubleAnimationUsingKeyFrames keyFrameAnimation:
                {
                    var frame = GetLastKeyFrame(keyFrameAnimation.KeyFrames);
                    if (frame != null)
                        assignments.Add(new VisualStateAssignment(target, property, frame.Value));
                    break;
                }
                case ColorAnimation colorAnimation:
                {
                    object? value = GetColorAnimationFinalValue(colorAnimation);
                    if (value != null)
                        assignments.Add(new VisualStateAssignment(target, property, value));
                    break;
                }
            }
        }
    }

    private static object? GetDoubleAnimationFinalValue(
        DependencyObject target,
        DependencyProperty property,
        DoubleAnimation animation)
    {
        if (animation.IsPropertySetLocally(DoubleAnimation.ToProperty))
            return animation.GetLocalXamlValue(DoubleAnimation.ToProperty);
        if (animation.By is double by)
        {
            object? currentValue = target.GetValue(property);
            double current;
            try
            {
                current = Convert.ToDouble(
                    currentValue ?? 0d,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"The current value '{currentValue}' ({currentValue?.GetType().FullName ?? "null"}) of " +
                    $"'{target.GetType().FullName}.{property.Name}' cannot be used as a DoubleAnimation base value.",
                    exception);
            }
            return current + by;
        }
        return animation.From;
    }

    private static object? GetColorAnimationFinalValue(ColorAnimation animation)
    {
        if (animation.IsPropertySetLocally(ColorAnimation.ToProperty))
            return animation.GetLocalXamlValue(ColorAnimation.ToProperty);
        return animation.From ?? animation.By;
    }

    private static TFrame? GetLastKeyFrame<TFrame>(IList<TFrame> frames)
        where TFrame : DependencyObject
    {
        TFrame? result = null;
        TimeSpan resultTime = TimeSpan.MinValue;
        foreach (var frame in frames)
        {
            TimeSpan time = frame switch
            {
                ObjectKeyFrame objectFrame => objectFrame.KeyTime.TimeSpan,
                DoubleKeyFrame doubleFrame => doubleFrame.KeyTime.TimeSpan,
                _ => TimeSpan.Zero
            };
            if (result == null || time >= resultTime)
            {
                result = frame;
                resultTime = time;
            }
        }
        return result;
    }

    private static bool TryResolveTimelineTarget(
        FrameworkElement root,
        Timeline timeline,
        out DependencyObject target,
        out DependencyProperty property)
    {
        string targetName = Storyboard.GetTargetName(timeline);
        var resolvedTarget = string.IsNullOrEmpty(targetName)
            ? root
            : FindName(root, targetName) as DependencyObject;
        if (resolvedTarget == null)
        {
            target = null!;
            property = null!;
            return false;
        }
        target = resolvedTarget;

        string path = Storyboard.GetTargetProperty(timeline);
        if (string.IsNullOrWhiteSpace(path))
        {
            property = null!;
            return false;
        }

        return TryResolvePropertyPath(
            target,
            path,
            out target,
            out property);
    }

    private static bool TryResolvePropertyPath(
        DependencyObject initialTarget,
        string path,
        out DependencyObject target,
        out DependencyProperty property)
    {
        string normalized = path.Trim();
        target = initialTarget;
        property = null!;
        if (normalized.Length == 0)
            return false;

        var segments = normalized.IndexOf(").(", StringComparison.Ordinal) >= 0
            ? normalized.Split(
                new[] { ").(" },
                StringSplitOptions.RemoveEmptyEntries)
            : new[] { normalized };
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index].Trim().Trim('(', ')');
            var separator = segment.LastIndexOf('.');
            var owner = separator < 0
                ? null
                : segment.Substring(0, separator).Trim('(', ')');
            var name = separator < 0
                ? segment
                : segment.Substring(separator + 1).Trim('(', ')');
            var resolved =
                DependencyProperty.Lookup(target.GetType(), name) ??
                (owner == null
                    ? null
                    : DependencyProperty.LookupRegisteredOwner(owner, name));
            if (resolved == null)
                return false;

            if (index == segments.Length - 1)
            {
                property = resolved;
                return true;
            }

            if (target.GetValue(resolved) is not DependencyObject next)
                return false;
            target = next;
        }

        return false;
    }

    private static bool TryResolveSetter(FrameworkElement root, Setter setter, out DependencyObject target, out DependencyProperty property)
    {
        target = root;
        property = null!;
        var setterPath = setter.Target?.Path?.Path ?? setter.Property;
        if (string.IsNullOrWhiteSpace(setterPath)) return false;
        var separator = setterPath.LastIndexOf('.');
        var propertyName = separator < 0 ? setterPath : setterPath[(separator + 1)..];
        if (separator > 0)
        {
            var targetName = setterPath[..separator];
            var named = FindName(root, targetName) as DependencyObject;
            if (named == null) return false;
            target = named;
        }
        property = DependencyProperty.Lookup(target.GetType(), propertyName)!;
        return property != null;
    }

    private static object? FindName(FrameworkElement root, string name)
    {
        if (Microsoft.UI.Xaml.Markup.XamlTemplateFactory.HasNameScope(root))
            return Microsoft.UI.Xaml.Markup.XamlTemplateFactory.FindName(root, name);
        if (string.Equals(root.Name, name, StringComparison.Ordinal)) return root;
        if (root is not ContainerVisual container) return null;
        IReadOnlyList<Visual> children = container.Children;
        for (int index = 0; index < children.Count; index++)
        {
            if (children[index] is not FrameworkElement element) continue;
            var found = FindName(element, name);
            if (found != null) return found;
        }
        return null;
    }

}
