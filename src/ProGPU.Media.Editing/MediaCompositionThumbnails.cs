namespace ProGPU.Media.Editing;

public enum MediaCompositionThumbnailPrecision
{
    NearestFrame = 0,
    NearestKeyFrame = 1
}

/// <summary>
/// Immutable batch request for native composition thumbnail generation.
/// Providers receive the same framework-neutral composition snapshot used
/// by export, but no destination file is created.
/// </summary>
public sealed record MediaCompositionThumbnailRequest(
    MediaCompositionExportRequest Composition,
    IReadOnlyList<TimeSpan> Positions,
    uint PixelWidth,
    uint PixelHeight,
    MediaCompositionThumbnailPrecision Precision);

/// <summary>
/// One encoded thumbnail and its exact output dimensions.
/// </summary>
public sealed record MediaCompositionThumbnail(
    byte[] EncodedBytes,
    string ContentType,
    uint PixelWidth,
    uint PixelHeight);

/// <summary>
/// Pluggable native thumbnail renderer. A batch is rendered as one operation
/// so providers can reuse demux, decoder, GPU composition, and native image
/// generator state across all requested timeline positions.
/// </summary>
public interface IMediaCompositionThumbnailProvider
{
    string Id { get; }
    int Priority { get; }

    bool CanRender(MediaCompositionThumbnailRequest request);

    ValueTask<IReadOnlyList<MediaCompositionThumbnail>>
        RenderAsync(
            MediaCompositionThumbnailRequest request,
            CancellationToken cancellationToken);
}

/// <summary>
/// Explicit, reflection-free registry for platform thumbnail providers.
/// Registration is a cold startup operation; request selection is O(P) for
/// P registered providers and does not allocate.
/// </summary>
public sealed class MediaCompositionThumbnailRegistry
{
    private readonly object _gate = new();
    private Entry[] _entries = [];
    private long _nextSequence;

    public static MediaCompositionThumbnailRegistry Default
    {
        get;
    } = new();

    public IDisposable Register(
        IMediaCompositionThumbnailProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        Entry entry;
        lock (_gate)
        {
            entry = new Entry(
                provider,
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

    public ValueTask<IReadOnlyList<
        MediaCompositionThumbnail>> RenderAsync(
        MediaCompositionThumbnailRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Entry[] entries = Volatile.Read(ref _entries);
        for (int index = 0; index < entries.Length; index++)
        {
            IMediaCompositionThumbnailProvider provider =
                entries[index].Provider;
            if (provider.CanRender(request))
            {
                return ValidateAsync(
                    provider,
                    request,
                    cancellationToken);
            }
        }

        return ValueTask.FromException<
            IReadOnlyList<MediaCompositionThumbnail>>(
            new NotSupportedException(
                "No registered media provider can generate thumbnails for this composition."));
    }

    private static async ValueTask<IReadOnlyList<
        MediaCompositionThumbnail>> ValidateAsync(
        IMediaCompositionThumbnailProvider provider,
        MediaCompositionThumbnailRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaCompositionThumbnail> result =
            await provider.RenderAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        if (result.Count != request.Positions.Count)
        {
            throw new InvalidOperationException(
                $"Thumbnail provider '{provider.Id}' returned {result.Count} images for {request.Positions.Count} requested positions.");
        }
        return result;
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
        IMediaCompositionThumbnailProvider Provider,
        long Sequence);

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static EntryComparer Instance { get; } =
            new();

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

            int priority = y.Provider.Priority.CompareTo(
                x.Provider.Priority);
            return priority != 0
                ? priority
                : x.Sequence.CompareTo(y.Sequence);
        }
    }

    private sealed class Registration : IDisposable
    {
        private MediaCompositionThumbnailRegistry? _owner;
        private Entry? _entry;

        public Registration(
            MediaCompositionThumbnailRegistry owner,
            Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public void Dispose()
        {
            MediaCompositionThumbnailRegistry? owner =
                Interlocked.Exchange(ref _owner, null);
            Entry? entry =
                Interlocked.Exchange(ref _entry, null);
            if (owner is not null && entry is not null)
            {
                owner.Unregister(entry);
            }
        }
    }
}
