using Microsoft.UI.Xaml.Controls;

namespace ProGPU.WinUI.Hmi.Designer;

/// <summary>Explicit editable-table binding, avoiding reflective model mutation during a transaction.</summary>
internal sealed class HmiEditorRow : IDataGridValueProvider
{
    private readonly Dictionary<string, string> _values;
    private readonly Action<string, string>? _write;
    internal HmiEditorRow(Dictionary<string, string> values, Action<string, string>? write = null)
    {
        _values = values;
        _write = write;
    }
    public bool TryGetDataGridValue(string propertyName, out object? value)
    {
        bool found = _values.TryGetValue(propertyName, out var text);
        value = text;
        return found;
    }
    public bool TrySetDataGridValue(string propertyName, object? value)
    {
        if (_write == null || !_values.ContainsKey(propertyName)) return false;
        string text = value?.ToString() ?? "";
        _write(propertyName, text);
        _values[propertyName] = text;
        return true;
    }
    public Type? GetDataGridValueType(string propertyName) => _values.ContainsKey(propertyName) ? typeof(string) : null;
}
