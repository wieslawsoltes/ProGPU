using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Numerics;
using Microsoft.UI.Xaml.Controls;
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

public sealed class VisualState
{
    public string Name { get; set; } = string.Empty;
    public Collection<Setter> Setters { get; } = new();
    public Collection<StateTriggerBase> StateTriggers { get; } = new();
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

public sealed class VisualStateGroup
{
    public string Name { get; set; } = string.Empty;
    public Collection<VisualState> States { get; } = new();
    public VisualState? CurrentState { get; internal set; }
    public event EventHandler<VisualStateChangedEventArgs>? CurrentStateChanged;

    internal List<SetterSnapshot> Snapshots { get; } = new();

    internal void RaiseCurrentStateChanged(VisualState? oldState, VisualState? newState) =>
        CurrentStateChanged?.Invoke(this, new VisualStateChangedEventArgs(oldState, newState));
}

internal readonly record struct SetterSnapshot(DependencyObject Target, DependencyProperty Property, bool HadLocalValue, object? Value);

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
        foreach (var group in GetVisualStateGroups(control))
        {
            var state = group.States.FirstOrDefault(item => string.Equals(item.Name, stateName, StringComparison.Ordinal));
            if (state != null)
            {
                ApplyState(control, group, state);
                return true;
            }
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
        foreach (var snapshot in group.Snapshots)
        {
            if (snapshot.HadLocalValue) snapshot.Target.SetValue(snapshot.Property, snapshot.Value);
            else snapshot.Target.ClearValue(snapshot.Property);
        }
        group.Snapshots.Clear();

        var oldState = group.CurrentState;
        group.CurrentState = state;
        if (state != null)
        {
            foreach (var setter in state.Setters)
            {
                if (!TryResolveSetter(root, setter, out var target, out var property)) continue;
                group.Snapshots.Add(new SetterSnapshot(target, property, target.IsPropertySetLocally(property), target.GetValue(property)));
                target.SetValue(property, ConvertValue(property.PropertyType, setter.Value));
            }
        }
        group.RaiseCurrentStateChanged(oldState, state);
    }

    private static bool TryResolveSetter(FrameworkElement root, Setter setter, out DependencyObject target, out DependencyProperty property)
    {
        target = root;
        property = null!;
        if (string.IsNullOrWhiteSpace(setter.Property)) return false;
        var separator = setter.Property.LastIndexOf('.');
        var propertyName = separator < 0 ? setter.Property : setter.Property[(separator + 1)..];
        if (separator > 0)
        {
            var targetName = setter.Property[..separator];
            var named = FindName(root, targetName);
            if (named == null) return false;
            target = named;
        }
        property = DependencyProperty.Lookup(target.GetType(), propertyName)!;
        return property != null;
    }

    private static FrameworkElement? FindName(FrameworkElement root, string name)
    {
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

    private static object? ConvertValue(Type targetType, object? value)
    {
        if (value == null || targetType.IsInstanceOfType(value)) return value;
        if (targetType.IsEnum && value is string enumValue) return Enum.Parse(targetType, enumValue, ignoreCase: true);
        if (targetType == typeof(Thickness) && value is string thickness) return Thickness.Parse(thickness);
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }
}
