using Microsoft.UI.Xaml.Controls;

namespace ProGPU.WinUI.Hmi.Designer;

/// <summary>Explicit editable-table binding with validate-before-publication semantics.</summary>
internal sealed class HmiEditorRow : IDataGridValueProvider
{
    private readonly Dictionary<string, string> _values;
    private readonly Action<string, string>? _write;
    private readonly Action<string>? _rejected;
    internal HmiEditorRow(Dictionary<string, string> values, Action<string, string>? write = null, Action<string>? rejected = null)
    {
        _values = values; _write = write; _rejected = rejected;
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
        try
        {
            _write(propertyName, text);
            _values[propertyName] = text;
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        {
            _rejected?.Invoke(error.Message);
            return false;
        }
    }
    public Type? GetDataGridValueType(string propertyName) => _values.ContainsKey(propertyName) ? typeof(string) : null;
}
