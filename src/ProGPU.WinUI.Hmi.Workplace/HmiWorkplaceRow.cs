using Microsoft.UI.Xaml.Controls;

namespace ProGPU.WinUI.Hmi.Workplace;

/// <summary>Read-only, reflection-free data-grid row.</summary>
internal sealed class HmiWorkplaceRow(string key, Dictionary<string, string> cells) : IDataGridValueProvider
{
    internal string Key { get; } = key;
    public bool TryGetDataGridValue(string propertyName, out object? value)
    {
        bool found = cells.TryGetValue(propertyName, out var text); value = text; return found;
    }
    public bool TrySetDataGridValue(string propertyName, object? value) => false;
    public Type? GetDataGridValueType(string propertyName) => cells.ContainsKey(propertyName) ? typeof(string) : null;
}
