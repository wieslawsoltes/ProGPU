using System.Collections;

namespace ProGPU.Hmi;

/// <summary>Fixed-storage chronological ring. Appending never shifts or reallocates the history.</summary>
public sealed class HmiHistory<T> : IReadOnlyList<T>
{
    private readonly T[] _items;
    private int _next;
    public int Count { get; private set; }
    public int Capacity => _items.Length;
    public HmiHistory(int capacity)
    {
        if (capacity is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_next - Count + index + Capacity) % Capacity];
        }
    }
    public void Add(T item)
    {
        _items[_next] = item;
        _next = (_next + 1) % Capacity;
        Count = Math.Min(Count + 1, Capacity);
    }
    public void Clear() { Array.Clear(_items); _next = 0; Count = 0; }
    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public readonly record struct HmiAuditEntry(DateTimeOffset Timestamp, string Operation, string Target, string Detail);
