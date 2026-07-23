using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Numerics;
using Microsoft.UI.Xaml.Controls;
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

    internal List<SetterSnapshot> Snapshots { get; } = new();

    internal void RaiseCurrentStateChanged(VisualState? oldState, VisualState? newState) =>
        CurrentStateChanged?.Invoke(this, new VisualStateChangedEventArgs(oldState, newState));
}

internal readonly record struct SetterSnapshot(DependencyObject Target, DependencyProperty Property, bool HadLocalValue, object? Value);
internal readonly record struct VisualStateAssignment(DependencyObject Target, DependencyProperty Property, object? Value);

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

        var oldState = group.CurrentState;
        var oldSnapshots = group.Snapshots.ToArray();
        var oldAppliedValues = new object?[oldSnapshots.Length];
        for (int index = 0; index < oldSnapshots.Length; index++)
        {
            var snapshot = oldSnapshots[index];
            oldAppliedValues[index] = snapshot.Target.GetLocalXamlValue(snapshot.Property);
        }

        RestoreSnapshots(group.Snapshots);
        group.Snapshots.Clear();

        var captured = new HashSet<(DependencyObject Target, DependencyProperty Property)>();
        try
        {
            foreach (var assignment in assignments)
            {
                var key = (assignment.Target, assignment.Property);
                if (captured.Add(key))
                {
                    bool hadLocalValue = assignment.Target.IsPropertySetLocally(assignment.Property);
                    group.Snapshots.Add(new SetterSnapshot(
                        assignment.Target,
                        assignment.Property,
                        hadLocalValue,
                        hadLocalValue ? assignment.Target.GetLocalXamlValue(assignment.Property) : null));
                }
                SetXamlValue(assignment.Target, assignment.Property, assignment.Value);
            }
        }
        catch
        {
            RestoreSnapshots(group.Snapshots);
            group.Snapshots.Clear();
            for (int index = 0; index < oldSnapshots.Length; index++)
            {
                var snapshot = oldSnapshots[index];
                SetXamlValue(snapshot.Target, snapshot.Property, oldAppliedValues[index]);
                group.Snapshots.Add(snapshot);
            }
            throw;
        }

        group.CurrentState = state;
        group.RaiseCurrentStateChanged(oldState, state);
    }

    private static void RestoreSnapshots(IReadOnlyList<SetterSnapshot> snapshots)
    {
        for (int index = snapshots.Count - 1; index >= 0; index--)
        {
            var snapshot = snapshots[index];
            if (snapshot.HadLocalValue)
                SetXamlValue(snapshot.Target, snapshot.Property, snapshot.Value);
            else
                snapshot.Target.ClearValue(snapshot.Property);
        }
    }

    private static void SetXamlValue(
        DependencyObject target,
        DependencyProperty property,
        object? value)
    {
        if (value is ThemeResource or ProGPU.Vector.ThemeResourceBrush)
        {
            target.SetValue(property, value);
            return;
        }

        target.SetValue(property, XamlValueConverter.ConvertTo(property.PropertyType, value));
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
            double current = Convert.ToDouble(
                target.GetValue(property) ?? 0d,
                System.Globalization.CultureInfo.InvariantCulture);
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
