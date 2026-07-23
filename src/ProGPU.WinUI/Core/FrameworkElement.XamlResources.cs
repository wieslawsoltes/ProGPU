using System;
using System.Collections.Generic;

namespace Microsoft.UI.Xaml;

public partial class FrameworkElement
{
    private ResourceDictionary? _resources;

    public ResourceDictionary Resources
    {
        get
        {
            if (_resources != null) return _resources;
            _resources = new ResourceDictionary();
            _resources.Changed += OnResourcesChanged;
            return _resources;
        }
    }

    private void OnResourcesChanged(object? sender, ResourceDictionaryChangedEventArgs args) =>
        NotifyThemeChanged();

    public object? FindName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Microsoft.UI.Xaml.Markup.XamlTemplateFactory.HasNameScope(this))
        {
            return Microsoft.UI.Xaml.Markup.XamlTemplateFactory.FindName(this, name);
        }
        if (string.Equals(Name, name, StringComparison.Ordinal))
        {
            return this;
        }

        for (var index = 0; index < Children.Count; index++)
        {
            if (Children[index] is FrameworkElement child && child.FindName(name) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    public bool TryFindResource(object key, out object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        for (FrameworkElement? current = this; current != null; current = current.Parent as FrameworkElement)
        {
            if (current._resources != null &&
                current._resources.TryLookup(
                    key,
                    current.ActualTheme,
                    ThemeManager.IsHighContrast,
                    out value))
            {
                return true;
            }
        }

        if (Application.Current?.Resources.TryLookup(
                key,
                ActualTheme,
                ThemeManager.IsHighContrast,
                out value) == true)
        {
            return true;
        }

        value = null;
        return false;
    }
}
