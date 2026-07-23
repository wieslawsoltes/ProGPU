using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Markup;

namespace Microsoft.UI.Xaml;

/// <summary>Typed, reflection-free resource lookup seam used by generated XAML construction code.</summary>
public static class XamlResourceResolver
{
    public static ProGPU.Vector.Brush? ResolveThemeBrush(
        ProGPU.Vector.ThemeResourceBrush resource,
        DependencyObject? target,
        ElementTheme theme,
        VisualThemeFamily themeFamily = VisualThemeFamily.Default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return ResolveTheme(resource.LookupRoot, target, resource.ResourceKey, theme, themeFamily)
            as ProGPU.Vector.Brush;
    }

    public static T Resolve<T>(object? lookupRoot, object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        object? value = null;
        var found = lookupRoot is ResourceDictionary dictionary &&
            dictionary.TryLookup(key, ElementTheme.Default, out value);
        if (!found && lookupRoot is FrameworkElement element)
            found = element.TryFindResource(key, out value);
        if (!found && Application.Current?.Resources.TryLookup(
                key,
                lookupRoot is FrameworkElement themed ? themed.ActualTheme : ElementTheme.Default,
                out value) == true)
            found = true;
        if (!found)
            found = XamlPlatformResourceCatalog.TryResolve(
                key,
                lookupRoot is FrameworkElement platformThemed
                    ? platformThemed.ActualTheme
                    : ElementTheme.Default,
                lookupRoot is FrameworkElement platformFamily
                    ? platformFamily.ActualThemeFamily
                    : ThemeManager.CurrentThemeFamily,
                out value);
        if (!found)
            throw new KeyNotFoundException($"XAML resource '{key}' was not found.");
        if (value is T typed) return typed;
        if (value == null && default(T) == null) return default!;
        try
        {
            return (T)XamlValueConverter.ConvertTo(typeof(T), value)!;
        }
        catch (Exception exception)
        {
            throw new InvalidCastException(
                $"XAML resource '{key}' with type '{value?.GetType().FullName ?? "null"}' " +
                $"cannot be converted to '{typeof(T).FullName}'.",
                exception);
        }
    }

    public static object? ResolveTheme(
        object? lookupRoot,
        DependencyObject? target,
        object key,
        ElementTheme theme,
        VisualThemeFamily themeFamily)
    {
        ArgumentNullException.ThrowIfNull(key);
        HashSet<object>? visited = null;
        var currentRoot = lookupRoot;
        var currentKey = key;
        while (true)
        {
            if (TryLookupThemeValue(currentRoot, target, currentKey, theme, out var value))
            {
                if (value is ThemeResource nested)
                {
                    visited ??= new HashSet<object>();
                    if (!visited.Add(currentKey))
                        throw new InvalidOperationException($"A ThemeResource cycle was detected for '{currentKey}'.");
                    currentRoot = nested.LookupRoot ?? currentRoot;
                    currentKey = nested.ResourceKey;
                    continue;
                }
                if (value is ProGPU.Vector.ThemeResourceBrush nestedBrush)
                {
                    visited ??= new HashSet<object>();
                    if (!visited.Add(currentKey))
                        throw new InvalidOperationException($"A ThemeResource cycle was detected for '{currentKey}'.");
                    currentRoot = nestedBrush.LookupRoot ?? currentRoot;
                    currentKey = nestedBrush.ResourceKey;
                    continue;
                }
                return value;
            }

            value = ThemeManager.GetResource(currentKey.ToString() ?? string.Empty, theme, themeFamily);
            if (value is ThemeResource fallback)
            {
                visited ??= new HashSet<object>();
                if (!visited.Add(currentKey))
                    throw new InvalidOperationException($"A ThemeResource cycle was detected for '{currentKey}'.");
                currentRoot = fallback.LookupRoot ?? currentRoot;
                currentKey = fallback.ResourceKey;
                continue;
            }
            if (value is ProGPU.Vector.ThemeResourceBrush fallbackBrush)
            {
                visited ??= new HashSet<object>();
                if (!visited.Add(currentKey))
                    throw new InvalidOperationException($"A ThemeResource cycle was detected for '{currentKey}'.");
                currentRoot = fallbackBrush.LookupRoot ?? currentRoot;
                currentKey = fallbackBrush.ResourceKey;
                continue;
            }
            return value;
        }
    }

    private static bool TryLookupThemeValue(
        object? lookupRoot,
        DependencyObject? target,
        object key,
        ElementTheme theme,
        out object? value)
    {
        if (lookupRoot is ResourceDictionary dictionary &&
            dictionary.TryLookup(key, theme, ThemeManager.IsHighContrast, out value))
            return true;
        if (lookupRoot is FrameworkElement root && root.TryFindResource(key, out value))
            return true;
        if (target is FrameworkElement element && element.TryFindResource(key, out value))
            return true;
        for (var current = target?.Parent as DependencyObject; current != null; current = current.Parent as DependencyObject)
        {
            if (current is FrameworkElement ancestor && ancestor.TryFindResource(key, out value))
                return true;
        }
        if (Application.Current?.Resources.TryLookup(
                key,
                theme,
                ThemeManager.IsHighContrast,
                out value) == true)
            return true;
        value = null;
        return false;
    }
}

/// <summary>Authoritative WinUI platform resources that sit after application resources.</summary>
internal static class XamlPlatformResourceCatalog
{
    public static bool TryResolve(
        object key,
        ElementTheme theme,
        VisualThemeFamily themeFamily,
        out object? value)
    {
        var actualTheme = theme == ElementTheme.Default
            ? ThemeManager.CurrentTheme
            : theme;
        var actualFamily = themeFamily == VisualThemeFamily.Default
            ? ThemeManager.CurrentThemeFamily
            : themeFamily;
        var context = new XamlPlatformResourceContext(
            actualTheme,
            actualFamily,
            ThemeManager.IsHighContrast);
        if (XamlPlatformResources.TryGetResource(key, context, out value))
        {
            return true;
        }

        if (key is string text &&
            ThemeManager.TryGetBuiltInPlatformColor(text, actualTheme, out var color))
        {
            value = new ProGPU.Vector.Color(
                ToByte(color.X),
                ToByte(color.Y),
                ToByte(color.Z),
                ToByte(color.W));
            return true;
        }
        value = null;
        return false;
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
