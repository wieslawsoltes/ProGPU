using System;
using System.Collections;
using System.Collections.Generic;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>A small notifying value collection used by retained document metadata.</summary>
public sealed class RichValueCollection<T> : IList<T>, IReadOnlyList<T>
{
    private readonly List<T> _items = new();
    private readonly Action _changed;

    internal RichValueCollection(Action changed) => _changed = changed;

    public int Count => _items.Count;
    public bool IsReadOnly => false;

    public T this[int index]
    {
        get => _items[index];
        set
        {
            if (EqualityComparer<T>.Default.Equals(_items[index], value)) return;
            _items[index] = value;
            _changed();
        }
    }

    public void Add(T item)
    {
        _items.Add(item);
        _changed();
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        int count = _items.Count;
        _items.AddRange(items);
        if (_items.Count != count) _changed();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        _items.AddRange(items);
        _changed();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        _changed();
    }

    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public int IndexOf(T item) => _items.IndexOf(item);

    public void Insert(int index, T item)
    {
        _items.Insert(index, item);
        _changed();
    }

    public bool Remove(T item)
    {
        bool removed = _items.Remove(item);
        if (removed) _changed();
        return removed;
    }

    public void RemoveAt(int index)
    {
        _items.RemoveAt(index);
        _changed();
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
