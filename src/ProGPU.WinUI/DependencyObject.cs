using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Microsoft.UI.Xaml;

public delegate void PropertyChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e);

public class PropertyMetadata
{
    public object? DefaultValue { get; }
    public PropertyChangedCallback? PropertyChangedCallback { get; }
    public bool IsInheritable { get; }

    public PropertyMetadata(object? defaultValue = null, PropertyChangedCallback? propertyChangedCallback = null)
    {
        DefaultValue = defaultValue;
        PropertyChangedCallback = propertyChangedCallback;
        IsInheritable = false;
    }

    public PropertyMetadata(object? defaultValue, PropertyChangedCallback? propertyChangedCallback, bool isInheritable)
    {
        DefaultValue = defaultValue;
        PropertyChangedCallback = propertyChangedCallback;
        IsInheritable = isInheritable;
    }
}

public class DependencyPropertyChangedEventArgs
{
    public DependencyProperty Property { get; }
    public object? OldValue { get; }
    public object? NewValue { get; }

    public DependencyPropertyChangedEventArgs(DependencyProperty property, object? oldValue, object? newValue)
    {
        Property = property;
        OldValue = oldValue;
        NewValue = newValue;
    }
}

public class DependencyProperty
{
    private static readonly ConcurrentDictionary<(Type OwnerType, string Name), DependencyProperty> Registry = new();

    public string Name { get; }
    public Type PropertyType { get; }
    public Type OwnerType { get; }
    public PropertyMetadata? Metadata { get; }

    private DependencyProperty(string name, Type propertyType, Type ownerType, PropertyMetadata? metadata)
    {
        Name = name;
        PropertyType = propertyType;
        OwnerType = ownerType;
        Metadata = metadata;
    }

    public static DependencyProperty Register(string name, Type propertyType, Type ownerType, PropertyMetadata? typeMetadata)
    {
        var key = (ownerType, name);
        var dp = new DependencyProperty(name, propertyType, ownerType, typeMetadata);
        return Registry.GetOrAdd(key, dp);
    }
}

public class DependencyObject : ProGPU.Layout.LayoutNode
{
    private readonly Dictionary<DependencyProperty, object?> _localValues = new();

    public object? GetValue(DependencyProperty dp)
    {
        if (_localValues.TryGetValue(dp, out var localValue))
        {
            return localValue;
        }

        // Handle value inheritance down the layout tree
        if (dp.Metadata?.IsInheritable == true)
        {
            var p = Parent as DependencyObject;
            while (p != null)
            {
                if (p._localValues.TryGetValue(dp, out var parentValue))
                {
                    return parentValue;
                }
                p = p.Parent as DependencyObject;
            }
        }

        return dp.Metadata?.DefaultValue;
    }

    public void SetValue(DependencyProperty dp, object? value)
    {
        object? oldValue = GetValue(dp);
        if (!Equals(oldValue, value))
        {
            _localValues[dp] = value;
            OnPropertyChanged(dp, oldValue, value);
        }
    }

    public void ClearValue(DependencyProperty dp)
    {
        if (_localValues.ContainsKey(dp))
        {
            object? oldValue = GetValue(dp);
            _localValues.Remove(dp);
            object? newValue = GetValue(dp);
            if (!Equals(oldValue, newValue))
            {
                OnPropertyChanged(dp, oldValue, newValue);
            }
        }
    }

    protected virtual void OnPropertyChanged(DependencyProperty dp, object? oldValue, object? newValue)
    {
        dp.Metadata?.PropertyChangedCallback?.Invoke(this, new DependencyPropertyChangedEventArgs(dp, oldValue, newValue));
        RaisePropertyChanged(dp.Name);
    }

    protected virtual void RaisePropertyChanged(string propertyName)
    {
    }
}
