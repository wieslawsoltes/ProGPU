using System;
using System.Collections;
using System.Collections.Generic;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>
/// Typed retained-document collection that propagates structural and descendant
/// mutations without reflection. Batches such as <see cref="AddRange"/> publish one
/// change notification.
/// </summary>
public sealed class RichElementCollection<T> : IList<T>, IReadOnlyList<T>
    where T : class
{
    private readonly List<T> _items = new();
    private readonly Action _changed;

    public event EventHandler? Changed;

    internal RichElementCollection(Action changed)
    {
        _changed = changed;
    }

    public int Count => _items.Count;
    public bool IsReadOnly => false;

    public T this[int index]
    {
        get => _items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            T previous = _items[index];
            if (ReferenceEquals(previous, value)) return;
            Unsubscribe(previous);
            _items[index] = value;
            Subscribe(value);
            NotifyChanged();
        }
    }

    public void Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
        Subscribe(item);
        NotifyChanged();
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        int originalCount = _items.Count;
        foreach (T item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            _items.Add(item);
            Subscribe(item);
        }
        if (_items.Count != originalCount) NotifyChanged();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (int index = 0; index < _items.Count; index++) Unsubscribe(_items[index]);
        _items.Clear();
        foreach (T item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            _items.Add(item);
            Subscribe(item);
        }
        NotifyChanged();
    }

    internal void ReplaceRange(int index, int count, IReadOnlyList<T> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if ((uint)index > (uint)_items.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (count < 0 || index + count > _items.Count) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0 && replacement.Count == 0) return;
        for (int offset = 0; offset < count; offset++) Unsubscribe(_items[index + offset]);
        _items.RemoveRange(index, count);
        for (int offset = 0; offset < replacement.Count; offset++)
        {
            T item = replacement[offset] ?? throw new ArgumentNullException(nameof(replacement));
            _items.Insert(index + offset, item);
            Subscribe(item);
        }
        NotifyChanged();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        for (int index = 0; index < _items.Count; index++) Unsubscribe(_items[index]);
        _items.Clear();
        NotifyChanged();
    }

    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public int IndexOf(T item) => _items.IndexOf(item);

    public void Insert(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Insert(index, item);
        Subscribe(item);
        NotifyChanged();
    }

    public bool Remove(T item)
    {
        int index = _items.IndexOf(item);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        T item = _items[index];
        _items.RemoveAt(index);
        Unsubscribe(item);
        NotifyChanged();
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void Subscribe(T item)
    {
        if (item is Block block) block.Changed += OnDescendantChanged;
        else if (item is TableRow row) row.Changed += OnDescendantChanged;
    }

    private void Unsubscribe(T item)
    {
        if (item is Block block) block.Changed -= OnDescendantChanged;
        else if (item is TableRow row) row.Changed -= OnDescendantChanged;
    }

    private void OnDescendantChanged() => NotifyChanged();

    private void NotifyChanged()
    {
        _changed();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
