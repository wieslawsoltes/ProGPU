using System;
using System.Collections.Generic;

namespace ProGPU.Scene;

/// <summary>
/// Rendering-thread lookup for shared live sources. Keys must include all
/// capture-context identity. Entries exist only while caller/recording leases
/// exist; closing the lookup does not invalidate outstanding recordings.
/// </summary>
public sealed class CachedPictureSourceCache<TKey> : IDisposable where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly HashSet<TKey> _creating;
    private bool _disposed;

    public CachedPictureSourceCache(IEqualityComparer<TKey>? comparer = null)
    {
        _entries = new(comparer);
        _creating = new(comparer);
    }

    public int Count => _entries.Count;

    public CachedPictureLease Acquire<TState>(TKey key, TState state, Func<TState, ICachedPictureSource> factory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(factory);
        if (_entries.TryGetValue(key, out var found)) return new(found);
        if (_creating.Count >= 256 || !_creating.Add(key))
            throw new InvalidOperationException("A cached source cannot recursively acquire its own capture.");
        try
        {
            var picture = new CachedPicture(factory(state), ownsSource: true);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var entry = new Entry(this, key, picture);
                _entries.Add(key, entry);
                return new CachedPictureLease(entry);
            }
            catch { _entries.Remove(key); picture.Dispose(); throw; }
        }
        finally { _creating.Remove(key); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _entries.Clear();
    }

    private sealed class Entry(CachedPictureSourceCache<TKey> cache, TKey key, CachedPicture picture)
        : CachedPictureLeaseOwner(picture)
    {
        protected override void OnReleased()
        {
            cache._entries.Remove(key);
            Picture.Dispose();
        }
    }
}

/// <summary>One source lease. Clone before retaining independently; dispose on the rendering thread.</summary>
public sealed class CachedPictureLease : IDisposable
{
    private CachedPictureLeaseOwner? _owner;
    internal CachedPictureLease(CachedPictureLeaseOwner owner)
    {
        owner.AddRef();
        _owner = owner;
    }
    /// <summary>Borrowed source; dispose the lease, not this shared picture.</summary>
    public CachedPicture Picture => (_owner ?? throw new ObjectDisposedException(nameof(CachedPictureLease))).Picture;
    public CachedPictureLease Clone() => new(_owner ?? throw new ObjectDisposedException(nameof(CachedPictureLease)));
    public void Dispose()
    {
        var owner = _owner;
        _owner = null;
        owner?.Release();
    }
}

internal abstract class CachedPictureLeaseOwner(CachedPicture picture)
{
    private int _references;
    public CachedPicture Picture { get; } = picture;
    internal void AddRef() => _references = checked(_references + 1);
    internal void Release()
    {
        if (--_references == 0) OnReleased();
    }
    protected abstract void OnReleased();
}
