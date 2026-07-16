using System.Collections.ObjectModel;

namespace Microsoft.UI.Xaml.HotReload;

/// <summary>
/// Describes one metadata update after replacement types have been mapped back to
/// the types already present in the visual tree.
/// </summary>
public sealed class HotReloadContext
{
    private readonly HashSet<Type> _updatedTypes;
    private readonly HashSet<Type> _originalTypes;

    internal HotReloadContext(
        long generation,
        IReadOnlyList<Type> updatedTypes,
        IReadOnlyList<Type> originalTypes,
        bool isAllTypes)
    {
        Generation = generation;
        UpdatedTypes = updatedTypes;
        OriginalTypes = originalTypes;
        IsAllTypes = isAllTypes;
        _updatedTypes = new HashSet<Type>(updatedTypes.Select(HotReloadTypeMappings.Normalize));
        _originalTypes = new HashSet<Type>(originalTypes.Select(HotReloadTypeMappings.Normalize));
    }

    public long Generation { get; }

    public IReadOnlyList<Type> UpdatedTypes { get; }

    public IReadOnlyList<Type> OriginalTypes { get; }

    /// <summary>
    /// Gets whether the runtime omitted its updated-type list, requiring a conservative
    /// refresh of every active application and visual type.
    /// </summary>
    public bool IsAllTypes { get; }

    public bool IsTypeUpdated(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (IsAllTypes)
        {
            return true;
        }

        var normalized = HotReloadTypeMappings.Normalize(type);
        return _updatedTypes.Contains(normalized) || _originalTypes.Contains(normalized);
    }

    public bool IsTypeOrBaseUpdated(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        for (var current = type; current != null; current = current.BaseType)
        {
            if (IsTypeUpdated(current))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Lets a live element rebuild itself without being replaced. This is useful for
/// controls with required constructor arguments or an explicit Build method.
/// </summary>
public interface IHotReloadable
{
    void Reload(HotReloadContext context);
}

/// <summary>
/// Transfers application-specific transient state when an element is recreated.
/// Framework-owned state such as text editing, selection, scrolling, focus, and
/// DataContext is handled automatically.
/// </summary>
public interface IHotReloadStateful
{
    object? CaptureHotReloadState();

    void RestoreHotReloadState(object? state);
}

/// <summary>
/// Lets a custom container keep its backing model synchronized when a direct child
/// is replaced by hot reload.
/// </summary>
public interface IHotReloadChildReplacer
{
    bool TryReplaceHotReloadChild(FrameworkElement oldChild, FrameworkElement newChild);
}

public enum HotReloadDiagnosticSeverity
{
    Trace,
    Information,
    Warning,
    Error
}

public sealed record HotReloadDiagnostic(
    HotReloadDiagnosticSeverity Severity,
    string Message,
    Exception? Exception = null);

public sealed record HotReloadResult(
    long Generation,
    IReadOnlyList<Type> UpdatedTypes,
    int ReplacedElements,
    int ReloadedElements,
    int RefreshedFactories,
    int InvalidatedElements,
    int FailedElements,
    TimeSpan Duration)
{
    public static HotReloadResult Empty { get; } = new(
        0,
        Array.Empty<Type>(),
        0,
        0,
        0,
        0,
        0,
        TimeSpan.Zero);
}

/// <summary>
/// Small typed state bag for custom hot reload handlers.
/// </summary>
public sealed class HotReloadStateBag
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object?> Values => new ReadOnlyDictionary<string, object?>(_values);

    public void Set<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _values[key] = value;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!_values.TryGetValue(key, out var candidate))
        {
            value = default;
            return false;
        }

        if (candidate is null && default(T) is null)
        {
            value = default;
            return true;
        }

        if (candidate is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }
}
