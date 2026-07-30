using System.Collections;

namespace Windows.Foundation.Collections;

public interface IPropertySet : IDictionary<string, object?>
{
}

public sealed class PropertySet : IPropertySet
{
    private readonly Dictionary<string, object?> _values =
        new(StringComparer.Ordinal);

    public object? this[string key]
    {
        get => _values[key];
        set => _values[key] = value;
    }

    public ICollection<string> Keys => _values.Keys;
    public ICollection<object?> Values => _values.Values;
    public int Count => _values.Count;
    public bool IsReadOnly => false;

    public void Add(string key, object? value) =>
        _values.Add(key, value);

    public void Add(KeyValuePair<string, object?> item) =>
        ((ICollection<KeyValuePair<string, object?>>)_values).Add(item);

    public void Clear() => _values.Clear();

    public bool Contains(KeyValuePair<string, object?> item) =>
        ((ICollection<KeyValuePair<string, object?>>)_values)
        .Contains(item);

    public bool ContainsKey(string key) =>
        _values.ContainsKey(key);

    public void CopyTo(
        KeyValuePair<string, object?>[] array,
        int arrayIndex) =>
        ((ICollection<KeyValuePair<string, object?>>)_values)
        .CopyTo(array, arrayIndex);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        _values.GetEnumerator();

    public bool Remove(string key) => _values.Remove(key);

    public bool Remove(KeyValuePair<string, object?> item) =>
        ((ICollection<KeyValuePair<string, object?>>)_values)
        .Remove(item);

    public bool TryGetValue(
        string key,
        out object? value) =>
        _values.TryGetValue(key, out value);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
