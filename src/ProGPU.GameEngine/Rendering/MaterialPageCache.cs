namespace ProGPU.GameEngine.Rendering;

/// <summary>
/// CPU residency table for immutable material pages. The caller pins every existing
/// visible page before reserving misses, then commits a reservation only after its
/// GPU bake is submitted. One render-thread owner; no locks or per-frame allocation.
/// Lookup is expected O(1); a miss scans at most Capacity slots for the oldest unpinned
/// page. Pending and pinned pages cannot be evicted. Capacity never grows.
/// </summary>
public sealed class MaterialPageCache<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private readonly Dictionary<TKey, int> _lookup;
    private readonly Entry[] _entries;
    private ulong _frame;
    private struct Entry
    {
        public TKey Key;
        public ulong LastFrame;
        public uint Generation;
        public bool Occupied, Ready;
    }
    public int Capacity => _entries.Length;
    public int Count => _lookup.Count;
    public long Evictions { get; private set; }

    public MaterialPageCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _lookup = new(capacity); _entries = new Entry[capacity];
    }

    public void BeginFrame()
    {
        if (_frame == ulong.MaxValue) throw new InvalidOperationException("Material frame counter exhausted.");
        _frame++;
    }

    public bool TryPin(in TKey key, out MaterialPageHandle handle)
    {
        if (_lookup.TryGetValue(key, out int slot))
        {
            ref var entry = ref _entries[slot];
            entry.LastFrame = _frame;
            handle = new(slot, entry.Generation);
            return entry.Ready;
        }
        handle = default; return false;
    }

    public bool TryReserve(in TKey key, out MaterialPageHandle handle)
    {
        if (_frame == 0) throw new InvalidOperationException("Begin a frame before reserving pages.");
        if (_lookup.TryGetValue(key, out int existing))
        {
            ref var entry = ref _entries[existing];
            entry.LastFrame = _frame; handle = new(existing, entry.Generation);
            return false;
        }
        int victim = -1; ulong oldest = ulong.MaxValue;
        for (int i = 0; i < _entries.Length; i++)
        {
            ref var entry = ref _entries[i];
            if (!entry.Occupied) { victim = i; break; }
            if (entry.Ready && entry.LastFrame < _frame && entry.LastFrame < oldest)
            { oldest = entry.LastFrame; victim = i; }
        }
        if (victim < 0) { handle = default; return false; }
        ref var target = ref _entries[victim];
        if (target.Generation == uint.MaxValue) throw new InvalidOperationException("Material page generation exhausted.");
        if (target.Occupied) { _lookup.Remove(target.Key); Evictions++; }
        target = new() { Key = key, LastFrame = _frame, Occupied = true, Generation = target.Generation + 1 };
        _lookup.Add(key, victim); handle = new(victim, target.Generation); return true;
    }

    public void Commit(MaterialPageHandle handle)
    {
        if (!IsCurrent(handle)) throw new InvalidOperationException("Stale material page reservation.");
        _entries[handle.Slot].Ready = true;
    }

    public void Cancel(MaterialPageHandle handle)
    {
        if (!IsCurrent(handle)) return;
        ref var entry = ref _entries[handle.Slot];
        if (entry.Ready) throw new InvalidOperationException("Cannot cancel a resident page.");
        _lookup.Remove(entry.Key); entry.Occupied = false;
    }

    public bool IsReady(MaterialPageHandle handle) => IsCurrent(handle) && _entries[handle.Slot].Ready;
    private bool IsCurrent(MaterialPageHandle handle) => (uint)handle.Slot < (uint)Capacity &&
        _entries[handle.Slot].Occupied && _entries[handle.Slot].Generation == handle.Generation;
}

/// <summary>A generation-checked page identity; a default handle is never resident.</summary>
public readonly record struct MaterialPageHandle(int Slot, uint Generation);
