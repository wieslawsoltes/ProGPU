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
    public bool AffectsMeasure { get; set; }
    public bool AffectsArrange { get; set; }
    public bool AffectsRender { get; set; }

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
    private static readonly List<DependencyProperty> RegisteredProperties = new();

    public string Name { get; }
    public Type PropertyType { get; }
    public Type OwnerType { get; }
    public PropertyMetadata? Metadata { get; }
    public int Index { get; }
    public bool IsAttached { get; }

    private DependencyProperty(string name, Type propertyType, Type ownerType, PropertyMetadata? metadata, int index, bool isAttached)
    {
        Name = name;
        PropertyType = propertyType;
        OwnerType = ownerType;
        Metadata = metadata;
        Index = index;
        IsAttached = isAttached;
    }

    public static DependencyProperty Register(string name, Type propertyType, Type ownerType, PropertyMetadata? typeMetadata)
    {
        var key = (ownerType, name);
        if (Registry.TryGetValue(key, out var existing)) return existing;
        
        lock (RegisteredProperties)
        {
            if (Registry.TryGetValue(key, out existing)) return existing;
            
            int index = RegisteredProperties.Count;
            var dp = new DependencyProperty(name, propertyType, ownerType, typeMetadata, index, false);
            RegisteredProperties.Add(dp);
            Registry.TryAdd(key, dp);
            return dp;
        }
    }

    public static DependencyProperty RegisterAttached(string name, Type propertyType, Type ownerType, PropertyMetadata? defaultMetadata)
    {
        var key = (ownerType, name);
        if (Registry.TryGetValue(key, out var existing)) return existing;
        
        lock (RegisteredProperties)
        {
            if (Registry.TryGetValue(key, out existing)) return existing;
            
            int index = RegisteredProperties.Count;
            var dp = new DependencyProperty(name, propertyType, ownerType, defaultMetadata, index, true);
            RegisteredProperties.Add(dp);
            Registry.TryAdd(key, dp);
            return dp;
        }
    }

    public static DependencyProperty? Lookup(Type ownerType, string name)
    {
        var type = ownerType;
        while (type != null)
        {
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch {}
            var key = (type, name);
            if (Registry.TryGetValue(key, out var dp)) return dp;
            type = type.BaseType;
        }
        return null;
    }

    public static IReadOnlyList<DependencyProperty> GetRegisteredProperties(Type ownerType)
    {
        ArgumentNullException.ThrowIfNull(ownerType);

        var type = ownerType;
        while (type != null)
        {
            try
            {
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch {}

            type = type.BaseType;
        }

        lock (RegisteredProperties)
        {
            var properties = new List<DependencyProperty>();
            foreach (var property in RegisteredProperties)
            {
                if (!property.IsAttached && property.OwnerType.IsAssignableFrom(ownerType))
                {
                    properties.Add(property);
                }
            }

            return properties;
        }
    }

    internal static IReadOnlyList<DependencyProperty> GetRegisteredAttachedProperties()
    {
        lock (RegisteredProperties)
        {
            var properties = new List<DependencyProperty>();
            foreach (var property in RegisteredProperties)
            {
                if (property.IsAttached)
                {
                    properties.Add(property);
                }
            }

            return properties;
        }
    }

    public static DependencyProperty? GetPropertyByIndex(int index)
    {
        lock (RegisteredProperties)
        {
            if (index >= 0 && index < RegisteredProperties.Count)
            {
                return RegisteredProperties[index];
            }
            return null;
        }
    }
}

public class DependencyObject : ProGPU.Layout.LayoutNode
{
    public event Action<DependencyObject, DependencyPropertyChangedEventArgs>? Changed;

    public const byte SourceDefault = 0;
    public const byte SourceDefaultStyle = 1;
    public const byte SourceStyle = 2;
    public const byte SourceLocal = 3;

    private object?[] _localValues = Array.Empty<object?>();
    private object?[] _styleValues = Array.Empty<object?>();
    private object?[] _defaultStyleValues = Array.Empty<object?>();
    private object?[] _effectiveValues = Array.Empty<object?>();
    private byte[] _valueSources = Array.Empty<byte>();

    private ThemeResource?[] _localThemeResources = Array.Empty<ThemeResource?>();
    private ThemeResource?[] _styleThemeResources = Array.Empty<ThemeResource?>();
    private ThemeResource?[] _defaultStyleThemeResources = Array.Empty<ThemeResource?>();

    private void EnsureSize(int index)
    {
        if (index >= _effectiveValues.Length)
        {
            int newSize = Math.Max(index + 1, _effectiveValues.Length * 2);
            Array.Resize(ref _localValues, newSize);
            Array.Resize(ref _styleValues, newSize);
            Array.Resize(ref _defaultStyleValues, newSize);
            Array.Resize(ref _effectiveValues, newSize);
            
            Array.Resize(ref _localThemeResources, newSize);
            Array.Resize(ref _styleThemeResources, newSize);
            Array.Resize(ref _defaultStyleThemeResources, newSize);
            
            int oldSize = _valueSources.Length;
            Array.Resize(ref _valueSources, newSize);
            for (int i = oldSize; i < newSize; i++)
            {
                _valueSources[i] = SourceDefault;
            }
        }
    }

    private bool _isThemeDirty = true;

    public bool IsThemeDirty
    {
        get => _isThemeDirty;
        set => _isThemeDirty = value;
    }

    public void NotifyThemeChanged()
    {
        _isThemeDirty = true;
        
        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i] is DependencyObject childDo)
            {
                childDo.NotifyThemeChanged();
            }
        }
        
        OnThemeChanged();
    }

    protected virtual void OnThemeChanged()
    {
        Invalidate();
        InvalidateMeasure();
    }

    protected override void OnParentChanged(ProGPU.Scene.ContainerVisual? oldParent, ProGPU.Scene.ContainerVisual? newParent)
    {
        base.OnParentChanged(oldParent, newParent);
        if (ResolveThemeContext(oldParent) != ResolveThemeContext(newParent))
        {
            NotifyThemeChanged();
        }
    }

    private (ElementTheme Theme, VisualThemeFamily Family) ResolveThemeContext(ProGPU.Scene.ContainerVisual? parent)
    {
        var theme = ThemeManager.CurrentTheme;
        var family = ThemeManager.CurrentThemeFamily;
        var hasTheme = false;
        var hasFamily = false;

        if (this is FrameworkElement element)
        {
            if (element.RequestedTheme != ElementTheme.Default)
            {
                theme = element.RequestedTheme;
                hasTheme = true;
            }

            if (element.RequestedThemeFamily != VisualThemeFamily.Default)
            {
                family = element.RequestedThemeFamily;
                hasFamily = true;
            }
        }

        for (var current = parent; current != null && (!hasTheme || !hasFamily); current = current.Parent)
        {
            if (current is not FrameworkElement ancestor)
            {
                continue;
            }

            if (!hasTheme && ancestor.RequestedTheme != ElementTheme.Default)
            {
                theme = ancestor.RequestedTheme;
                hasTheme = true;
            }

            if (!hasFamily && ancestor.RequestedThemeFamily != VisualThemeFamily.Default)
            {
                family = ancestor.RequestedThemeFamily;
                hasFamily = true;
            }
        }

        return (theme, family);
    }

    public void ReevaluateThemeResources()
    {
        _isThemeDirty = false;
        
        ElementTheme activeTheme = ElementTheme.Dark;
        VisualThemeFamily activeFamily = VisualThemeFamily.WinUI;
        if (this is FrameworkElement fe)
        {
            activeTheme = fe.ActualTheme;
            activeFamily = fe.ActualThemeFamily;
        }
        else
        {
            var p = Parent as DependencyObject;
            while (p != null)
            {
                if (p is FrameworkElement pFe)
                {
                    activeTheme = pFe.ActualTheme;
                    activeFamily = pFe.ActualThemeFamily;
                    break;
                }
                p = p.Parent as DependencyObject;
            }
            if (p == null)
            {
                activeTheme = ThemeManager.CurrentTheme;
                activeFamily = ThemeManager.CurrentThemeFamily;
            }
        }

        int len = _effectiveValues.Length;
        for (int i = 0; i < len; i++)
        {
            bool hasThemeResource = false;
            
            if (i < _localThemeResources.Length && _localThemeResources[i] is ThemeResource localTr)
            {
                _localValues[i] = ThemeManager.GetResource(localTr.ResourceKey, activeTheme, activeFamily);
                hasThemeResource = true;
            }
            if (i < _styleThemeResources.Length && _styleThemeResources[i] is ThemeResource styleTr)
            {
                _styleValues[i] = ThemeManager.GetResource(styleTr.ResourceKey, activeTheme, activeFamily);
                hasThemeResource = true;
            }
            if (i < _defaultStyleThemeResources.Length && _defaultStyleThemeResources[i] is ThemeResource defaultStyleTr)
            {
                _defaultStyleValues[i] = ThemeManager.GetResource(defaultStyleTr.ResourceKey, activeTheme, activeFamily);
                hasThemeResource = true;
            }
            
            if (hasThemeResource)
            {
                var dp = DependencyProperty.GetPropertyByIndex(i);
                if (dp != null)
                {
                    object? oldValue = _effectiveValues[i] ?? dp.Metadata?.DefaultValue;
                    UpdateEffectiveValue(dp, i, oldValue);
                }
            }
        }
    }

    public object? GetValue(DependencyProperty dp)
    {
        if (_isThemeDirty)
        {
            ReevaluateThemeResources();
        }

        int idx = dp.Index;
        if (idx < _effectiveValues.Length)
        {
            var val = _effectiveValues[idx];
            if (val != null) return val;
        }

        if (dp.Metadata?.IsInheritable == true)
        {
            var p = Parent as DependencyObject;
            while (p != null)
            {
                int pIdx = dp.Index;
                if (pIdx < p._effectiveValues.Length)
                {
                    var parentVal = p._effectiveValues[pIdx];
                    if (parentVal != null) return parentVal;
                }
                p = p.Parent as DependencyObject;
            }
        }

        return dp.Metadata?.DefaultValue;
    }

    public void SetValue(DependencyProperty dp, object? value)
    {
        int idx = dp.Index;
        EnsureSize(idx);

        object? oldValue = GetValue(dp);
        
        if (value is ThemeResource themeResource)
        {
            _localThemeResources[idx] = themeResource;
            var resolved = ThemeManager.GetResource(themeResource.ResourceKey, (this is FrameworkElement fe) ? fe.ActualTheme : ThemeManager.CurrentTheme, (this is FrameworkElement feFam) ? feFam.ActualThemeFamily : ThemeManager.CurrentThemeFamily);
            _localValues[idx] = resolved;
        }
        else if (value is ProGPU.Vector.ThemeResourceBrush trBrush)
        {
            var tr = new ThemeResource(trBrush.ResourceKey);
            _localThemeResources[idx] = tr;
            var resolved = ThemeManager.GetResource(tr.ResourceKey, (this is FrameworkElement fe) ? fe.ActualTheme : ThemeManager.CurrentTheme, (this is FrameworkElement feFam) ? feFam.ActualThemeFamily : ThemeManager.CurrentThemeFamily);
            _localValues[idx] = resolved;
        }
        else
        {
            _localThemeResources[idx] = null;
            _localValues[idx] = value;
        }
        
        UpdateEffectiveValue(dp, idx, oldValue);
    }

    public void ClearValue(DependencyProperty dp)
    {
        int idx = dp.Index;
        if (idx < _localValues.Length)
        {
            object? oldValue = GetValue(dp);
            _localThemeResources[idx] = null;
            _localValues[idx] = null;
            UpdateEffectiveValue(dp, idx, oldValue);
        }
    }

    public void SetStyleValue(DependencyProperty dp, object? value)
    {
        int idx = dp.Index;
        EnsureSize(idx);
        object? oldValue = GetValue(dp);
        
        if (value is ThemeResource themeResource)
        {
            _styleThemeResources[idx] = themeResource;
            var resolved = ThemeManager.GetResource(themeResource.ResourceKey, (this is FrameworkElement fe) ? fe.ActualTheme : ThemeManager.CurrentTheme, (this is FrameworkElement feFam) ? feFam.ActualThemeFamily : ThemeManager.CurrentThemeFamily);
            _styleValues[idx] = resolved;
        }
        else if (value is ProGPU.Vector.ThemeResourceBrush trBrush)
        {
            var tr = new ThemeResource(trBrush.ResourceKey);
            _styleThemeResources[idx] = tr;
            var resolved = ThemeManager.GetResource(tr.ResourceKey, (this is FrameworkElement fe) ? fe.ActualTheme : ThemeManager.CurrentTheme, (this is FrameworkElement feFam) ? feFam.ActualThemeFamily : ThemeManager.CurrentThemeFamily);
            _styleValues[idx] = resolved;
        }
        else
        {
            _styleThemeResources[idx] = null;
            _styleValues[idx] = value;
        }
        
        UpdateEffectiveValue(dp, idx, oldValue);
    }

    public void ClearStyleValues()
    {
        for (int i = 0; i < _styleValues.Length; i++)
        {
            if (i < _styleValues.Length && (_styleValues[i] != null || _styleThemeResources[i] != null))
            {
                var dp = DependencyProperty.GetPropertyByIndex(i);
                if (dp != null)
                {
                    object? oldValue = GetValue(dp);
                    _styleThemeResources[i] = null;
                    _styleValues[i] = null;
                    UpdateEffectiveValue(dp, i, oldValue);
                }
            }
        }
    }

    public void SetDefaultStyleValue(DependencyProperty dp, object? value)
    {
        int idx = dp.Index;
        EnsureSize(idx);
        object? oldValue = GetValue(dp);
        
        if (value is ThemeResource themeResource)
        {
            _defaultStyleThemeResources[idx] = themeResource;
            var resolved = ThemeManager.GetResource(themeResource.ResourceKey, (this is FrameworkElement fe) ? fe.ActualTheme : ThemeManager.CurrentTheme, (this is FrameworkElement feFam) ? feFam.ActualThemeFamily : ThemeManager.CurrentThemeFamily);
            _defaultStyleValues[idx] = resolved;
        }
        else if (value is ProGPU.Vector.ThemeResourceBrush trBrush)
        {
            var tr = new ThemeResource(trBrush.ResourceKey);
            _defaultStyleThemeResources[idx] = tr;
            var resolved = ThemeManager.GetResource(tr.ResourceKey, (this is FrameworkElement fe) ? fe.ActualTheme : ThemeManager.CurrentTheme, (this is FrameworkElement feFam) ? feFam.ActualThemeFamily : ThemeManager.CurrentThemeFamily);
            _defaultStyleValues[idx] = resolved;
        }
        else
        {
            _defaultStyleThemeResources[idx] = null;
            _defaultStyleValues[idx] = value;
        }
        
        UpdateEffectiveValue(dp, idx, oldValue);
    }

    public void ClearDefaultStyleValues()
    {
        for (int i = 0; i < _defaultStyleValues.Length; i++)
        {
            if (i < _defaultStyleValues.Length && (_defaultStyleValues[i] != null || _defaultStyleThemeResources[i] != null))
            {
                var dp = DependencyProperty.GetPropertyByIndex(i);
                if (dp != null)
                {
                    object? oldValue = GetValue(dp);
                    _defaultStyleThemeResources[i] = null;
                    _defaultStyleValues[i] = null;
                    UpdateEffectiveValue(dp, i, oldValue);
                }
            }
        }
    }

    private void UpdateEffectiveValue(DependencyProperty dp, int idx, object? oldValue)
    {
        object? newValue;
        byte source;

        if (_localValues[idx] != null)
        {
            newValue = _localValues[idx];
            source = SourceLocal;
        }
        else if (_styleValues[idx] != null)
        {
            newValue = _styleValues[idx];
            source = SourceStyle;
        }
        else if (_defaultStyleValues[idx] != null)
        {
            newValue = _defaultStyleValues[idx];
            source = SourceDefaultStyle;
        }
        else
        {
            newValue = null;
            source = SourceDefault;
        }

        _effectiveValues[idx] = newValue;
        _valueSources[idx] = source;

        var finalValue = newValue ?? dp.Metadata?.DefaultValue;
        if (!Equals(oldValue, finalValue))
        {
            OnPropertyChanged(dp, oldValue, finalValue);
        }
    }

    public bool IsPropertySetLocally(DependencyProperty dp)
    {
        int idx = dp.Index;
        return idx < _localValues.Length && (_localValues[idx] != null || _localThemeResources[idx] != null);
    }

    public bool IsPropertySetInStyle(DependencyProperty dp)
    {
        int idx = dp.Index;
        return idx < _styleValues.Length && (_styleValues[idx] != null || _styleThemeResources[idx] != null);
    }

    internal IReadOnlyList<KeyValuePair<DependencyProperty, object?>> GetLocalAttachedValues()
    {
        var result = new List<KeyValuePair<DependencyProperty, object?>>();
        foreach (var property in DependencyProperty.GetRegisteredAttachedProperties())
        {
            if (IsPropertySetLocally(property))
            {
                result.Add(new KeyValuePair<DependencyProperty, object?>(property, GetValue(property)));
            }
        }

        return result;
    }

    private long _nextToken = 1;
    private Dictionary<DependencyProperty, List<(long Token, Action<DependencyObject, DependencyPropertyChangedEventArgs> Callback)>>? _propertyChangedCallbacks;

    public long RegisterPropertyChangedCallback(DependencyProperty dp, Action<DependencyObject, DependencyPropertyChangedEventArgs> callback)
    {
        _propertyChangedCallbacks ??= new Dictionary<DependencyProperty, List<(long, Action<DependencyObject, DependencyPropertyChangedEventArgs>)>>();
        if (!_propertyChangedCallbacks.TryGetValue(dp, out var list))
        {
            list = new List<(long, Action<DependencyObject, DependencyPropertyChangedEventArgs>)>();
            _propertyChangedCallbacks[dp] = list;
        }
        long token = _nextToken++;
        list.Add((token, callback));
        return token;
    }

    public void UnregisterPropertyChangedCallback(DependencyProperty dp, long token)
    {
        if (_propertyChangedCallbacks != null && _propertyChangedCallbacks.TryGetValue(dp, out var list))
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Token == token)
                {
                    list.RemoveAt(i);
                    break;
                }
            }
        }
    }

    protected virtual void OnPropertyChanged(DependencyProperty dp, object? oldValue, object? newValue)
    {
        var args = new DependencyPropertyChangedEventArgs(dp, oldValue, newValue);
        dp.Metadata?.PropertyChangedCallback?.Invoke(this, args);

        if (this is FrameworkElement fe)
        {
            if (dp.Metadata?.AffectsMeasure == true)
            {
                fe.InvalidateMeasure();
            }
            if (dp.Metadata?.AffectsArrange == true)
            {
                fe.InvalidateArrange();
            }
            if (dp.Metadata?.AffectsRender == true)
            {
                fe.Invalidate();
            }
        }

        Changed?.Invoke(this, args);

        if (_propertyChangedCallbacks != null && _propertyChangedCallbacks.TryGetValue(dp, out var callbacks))
        {
            for (int i = 0; i < callbacks.Count; i++)
            {
                callbacks[i].Callback(this, args);
            }
        }

        RaisePropertyChanged(dp.Name);
    }

    protected virtual void RaisePropertyChanged(string propertyName)
    {
    }
}
