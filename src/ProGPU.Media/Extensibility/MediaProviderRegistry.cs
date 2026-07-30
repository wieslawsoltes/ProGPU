using ProGPU.Media.Playback;

namespace ProGPU.Media.Extensibility;

/// <summary>
/// Explicit, reflection-free provider registry. Registration is expected
/// during application startup rather than from playback hot paths.
/// </summary>
public sealed class MediaProviderRegistry
{
    private readonly object _gate = new();
    private Entry[] _entries = [];
    private long _nextSequence;

    public static MediaProviderRegistry Default { get; } = new();

    public IDisposable Register(
        IMediaPlaybackProviderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Entry entry;
        lock (_gate)
        {
            entry = new Entry(
                factory,
                Interlocked.Increment(ref _nextSequence));
            Entry[] current = _entries;
            var next = new Entry[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = entry;
            Array.Sort(next, EntryComparer.Instance);
            Volatile.Write(ref _entries, next);
        }

        return new Registration(this, entry);
    }

    internal IMediaPlaybackProviderFactory? Select(
        MediaSourceDescriptor source)
    {
        Entry[] entries = Volatile.Read(ref _entries);
        for (int index = 0; index < entries.Length; index++)
        {
            if (entries[index].Factory.CanOpen(source))
            {
                return entries[index].Factory;
            }
        }

        return null;
    }

    private void Unregister(Entry entry)
    {
        lock (_gate)
        {
            Entry[] current = _entries;
            int index = Array.IndexOf(current, entry);
            if (index < 0)
            {
                return;
            }

            var next = new Entry[current.Length - 1];
            if (index > 0)
            {
                Array.Copy(current, 0, next, 0, index);
            }
            if (index < current.Length - 1)
            {
                Array.Copy(
                    current,
                    index + 1,
                    next,
                    index,
                    current.Length - index - 1);
            }
            Volatile.Write(ref _entries, next);
        }
    }

    private sealed record Entry(
        IMediaPlaybackProviderFactory Factory,
        long Sequence);

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static EntryComparer Instance { get; } = new();

        public int Compare(Entry? x, Entry? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }
            if (x is null)
            {
                return 1;
            }
            if (y is null)
            {
                return -1;
            }

            int priority = y.Factory.Priority.CompareTo(
                x.Factory.Priority);
            return priority != 0
                ? priority
                : x.Sequence.CompareTo(y.Sequence);
        }
    }

    private sealed class Registration : IDisposable
    {
        private MediaProviderRegistry? _owner;
        private Entry? _entry;

        public Registration(
            MediaProviderRegistry owner,
            Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public void Dispose()
        {
            MediaProviderRegistry? owner =
                Interlocked.Exchange(ref _owner, null);
            Entry? entry = Interlocked.Exchange(ref _entry, null);
            if (owner is not null && entry is not null)
            {
                owner.Unregister(entry);
            }
        }
    }
}
