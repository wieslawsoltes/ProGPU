using System;

namespace Microsoft.UI.Xaml;

public class ThemeResource
{
    public object ResourceKey { get; }
    public object? LookupRoot { get; }

    public ThemeResource(string resourceKey)
        : this(null, (object)resourceKey)
    {
    }

    public ThemeResource(object resourceKey)
        : this(null, resourceKey)
    {
    }

    public ThemeResource(object? lookupRoot, string resourceKey)
        : this(lookupRoot, (object)resourceKey)
    {
    }

    public ThemeResource(object? lookupRoot, object resourceKey)
    {
        LookupRoot = lookupRoot;
        ResourceKey = resourceKey ?? throw new ArgumentNullException(nameof(resourceKey));
    }

    public override string ToString()
    {
        return $"ThemeResource: {ResourceKey}";
    }
}

internal sealed class ThemeColorBrushResource : ThemeResource
{
    public ThemeColorBrushResource(
        object? lookupRoot,
        object resourceKey,
        float opacity)
        : base(lookupRoot, resourceKey)
    {
        Opacity = opacity;
    }

    public float Opacity { get; }
}

public static class ThemeResourceOperations
{
    public static void SetResource(
        DependencyObject target,
        string propertyName,
        ThemeResource resource)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(propertyName);
        ArgumentNullException.ThrowIfNull(resource);
        var property = DependencyProperty.Lookup(target.GetType(), propertyName) ??
            throw new InvalidOperationException(
                $"Dependency property '{propertyName}' was not registered for '{target.GetType().FullName}'.");
        target.SetValue(property, resource);
    }
}
