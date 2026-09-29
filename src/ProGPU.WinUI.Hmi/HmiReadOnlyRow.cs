using Microsoft.UI.Xaml.Controls;

namespace ProGPU.WinUI.Hmi;

/// <summary>Explicit table schema, so runtime tooling never reflects over private row types.</summary>
public sealed class HmiReadOnlyRow : IDataGridValueProvider
{
    private readonly IReadOnlyDictionary<string, object?> _values;
    public object? Context { get; }
    public HmiReadOnlyRow(IReadOnlyDictionary<string, object?> values, object? context = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        Context = context;
    }
    public bool TryGetDataGridValue(string propertyName, out object? value) => _values.TryGetValue(propertyName, out value);
    public bool TrySetDataGridValue(string propertyName, object? value) => false;
    public Type? GetDataGridValueType(string propertyName) => _values.TryGetValue(propertyName, out var value) ? value?.GetType() ?? typeof(string) : null;
}
