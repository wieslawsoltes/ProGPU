using System.Collections.ObjectModel;
using ProGPU.Media.Playback;
using Windows.Media.Core;

namespace Windows.Media.Playback;

public interface IMediaPlaybackSource
{
}

internal interface IProGpuMediaPlaybackSource : IMediaPlaybackSource
{
    MediaSourceDescriptor ResolveDescriptor();
    MediaPlaybackRange ResolvePlaybackRange();
    event EventHandler? SourceInvalidated;
}

public enum MediaPlaybackItemChangedReason
{
    InitialItem = 0,
    AppRequested = 1,
    EndOfStream = 2,
    Error = 3
}

public sealed class CurrentMediaPlaybackItemChangedEventArgs :
    EventArgs
{
    internal CurrentMediaPlaybackItemChangedEventArgs(
        MediaPlaybackItem? oldItem,
        MediaPlaybackItem? newItem,
        MediaPlaybackItemChangedReason reason)
    {
        OldItem = oldItem;
        NewItem = newItem;
        Reason = reason;
    }

    public MediaPlaybackItem? OldItem { get; }
    public MediaPlaybackItem? NewItem { get; }
    public MediaPlaybackItemChangedReason Reason { get; }
}

public enum MediaPlaybackItemErrorCode
{
    None = 0,
    Aborted = 1,
    NetworkError = 2,
    DecodeError = 3,
    SourceNotSupportedError = 4,
    EncryptionError = 5
}

public sealed class MediaPlaybackItemError
{
    internal MediaPlaybackItemError(
        MediaPlaybackItemErrorCode errorCode,
        Exception? extendedError)
    {
        ErrorCode = errorCode;
        ExtendedError = extendedError;
    }

    public MediaPlaybackItemErrorCode ErrorCode { get; }
    public Exception? ExtendedError { get; }
}

public sealed class MediaPlaybackItemOpenedEventArgs : EventArgs
{
    internal MediaPlaybackItemOpenedEventArgs(
        MediaPlaybackItem item)
    {
        Item = item;
    }

    public MediaPlaybackItem Item { get; }
}

public sealed class MediaPlaybackItemFailedEventArgs : EventArgs
{
    internal MediaPlaybackItemFailedEventArgs(
        MediaPlaybackItem item,
        MediaPlaybackItemError error)
    {
        Item = item;
        Error = error;
    }

    public MediaPlaybackItem Item { get; }
    public MediaPlaybackItemError Error { get; }
}

public sealed class MediaPlaybackItem : IMediaPlaybackSource,
    IProGpuMediaPlaybackSource
{
    private double _totalDownloadProgress;

    public MediaPlaybackItem(MediaSource source)
        : this(source, TimeSpan.Zero, null)
    {
    }

    public MediaPlaybackItem(
        MediaSource source,
        TimeSpan startTime)
        : this(source, startTime, null)
    {
    }

    public MediaPlaybackItem(
        MediaSource source,
        TimeSpan startTime,
        TimeSpan durationLimit)
        : this(source, startTime, (TimeSpan?)durationLimit)
    {
    }

    private MediaPlaybackItem(
        MediaSource source,
        TimeSpan startTime,
        TimeSpan? durationLimit)
    {
        MediaSource validatedSource = source ??
            throw new ArgumentNullException(nameof(source));
        if (startTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startTime));
        }
        if (durationLimit < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationLimit));
        }

        StartTime = startTime;
        DurationLimit = durationLimit;
        validatedSource.AssociatePlaybackItem(this);
        Source = validatedSource;
        _totalDownloadProgress =
            validatedSource.Uri?.IsFile == true ? 1d : 0d;
    }

    public MediaSource Source { get; }
    public TimeSpan StartTime { get; }
    public TimeSpan? DurationLimit { get; }
    public bool CanSkip { get; set; } = true;
    public bool IsDisabledInPlaybackList { get; set; }
    public double TotalDownloadProgress =>
        Volatile.Read(ref _totalDownloadProgress);

    public static MediaPlaybackItem? FindFromMediaSource(
        MediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.FindPlaybackItem();
    }

    internal void SetTotalDownloadProgress(double value) =>
        Volatile.Write(
            ref _totalDownloadProgress,
            double.IsFinite(value)
                ? Math.Clamp(value, 0d, 1d)
                : 0d);

    MediaSourceDescriptor
        IProGpuMediaPlaybackSource.ResolveDescriptor() =>
        ((IProGpuMediaPlaybackSource)Source)
        .ResolveDescriptor();

    MediaPlaybackRange
        IProGpuMediaPlaybackSource.ResolvePlaybackRange() =>
        new(StartTime, DurationLimit);

    event EventHandler?
        IProGpuMediaPlaybackSource.SourceInvalidated
    {
        add { }
        remove { }
    }
}

public sealed class MediaPlaybackList : IMediaPlaybackSource,
    IProGpuMediaPlaybackSource
{
    private readonly ObservableCollection<MediaPlaybackItem> _items =
        [];
    private readonly object _playbackOwnersGate = new();
    private readonly List<PlaybackOwnerEntry> _playbackOwners = [];
    private int _currentIndex = -1;
    private MediaPlaybackItem[] _shuffledItems = [];
    private bool _shuffleEnabled;
    private MediaPlaybackItem? _startingItem;

    public MediaPlaybackList()
    {
        _items.CollectionChanged += (_, _) =>
        {
            if (_shuffleEnabled)
            {
                RegenerateShuffle();
            }
            if (_items.Count == 0)
            {
                SetCurrentIndex(
                    -1,
                    MediaPlaybackItemChangedReason.AppRequested);
            }
            else if (_currentIndex < 0 ||
                     _currentIndex >= _items.Count)
            {
                SetCurrentIndex(
                    0,
                    MediaPlaybackItemChangedReason.InitialItem);
            }
            SourceInvalidated?.Invoke(this, EventArgs.Empty);
        };
    }

    public IList<MediaPlaybackItem> Items => _items;
    public MediaPlaybackItem? CurrentItem =>
        _currentIndex >= 0 && _currentIndex < _items.Count
            ? _items[_currentIndex]
            : null;
    public uint CurrentItemIndex =>
        _currentIndex < 0 ? uint.MaxValue : (uint)_currentIndex;
    public bool AutoRepeatEnabled { get; set; }
    public bool ShuffleEnabled
    {
        get => _shuffleEnabled;
        set
        {
            if (_shuffleEnabled == value)
            {
                return;
            }
            _shuffleEnabled = value;
            if (value && _shuffledItems.Length == 0)
            {
                RegenerateShuffle();
            }
        }
    }
    public MediaPlaybackItem? StartingItem
    {
        get => _startingItem;
        set
        {
            if (value is not null && !_items.Contains(value))
            {
                throw new ArgumentException(
                    "StartingItem must belong to Items.",
                    nameof(value));
            }
            if (value is not null)
            {
                int targetIndex = _items.IndexOf(value);
                EnsureCanChangeCurrentItem(targetIndex);
                _startingItem = value;
                SetCurrentIndex(
                    targetIndex,
                    MediaPlaybackItemChangedReason.AppRequested);
            }
            else
            {
                _startingItem = null;
            }
        }
    }
    public IReadOnlyList<MediaPlaybackItem> ShuffledItems =>
        _shuffledItems;
    public TimeSpan MaxPrefetchTime { get; set; }
    public uint MaxPlayedItemsToKeepOpen { get; set; }

    public event Windows.Foundation.TypedEventHandler<
        MediaPlaybackList,
        CurrentMediaPlaybackItemChangedEventArgs>?
        CurrentItemChanged;
    public event Windows.Foundation.TypedEventHandler<
        MediaPlaybackList,
        MediaPlaybackItemOpenedEventArgs>?
        ItemOpened;
    public event Windows.Foundation.TypedEventHandler<
        MediaPlaybackList,
        MediaPlaybackItemFailedEventArgs>?
        ItemFailed;

    internal event EventHandler? SourceInvalidated;

    event EventHandler?
        IProGpuMediaPlaybackSource.SourceInvalidated
    {
        add => SourceInvalidated += value;
        remove => SourceInvalidated -= value;
    }

    MediaSourceDescriptor
        IProGpuMediaPlaybackSource.ResolveDescriptor()
    {
        MediaPlaybackItem item = CurrentItem ??
            throw new InvalidOperationException(
                "A media playback list has no current item.");
        return ((IProGpuMediaPlaybackSource)item)
            .ResolveDescriptor();
    }

    MediaPlaybackRange
        IProGpuMediaPlaybackSource.ResolvePlaybackRange()
    {
        MediaPlaybackItem item = CurrentItem ??
            throw new InvalidOperationException(
                "A media playback list has no current item.");
        return ((IProGpuMediaPlaybackSource)item)
            .ResolvePlaybackRange();
    }

    public MediaPlaybackItem? MoveNext() =>
        MoveNextCore(
            MediaPlaybackItemChangedReason.AppRequested,
            enforceCanSkip: true)
            ? CurrentItem
            : null;

    public MediaPlaybackItem? MovePrevious()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        IReadOnlyList<MediaPlaybackItem> order =
            GetPlaybackOrder();
        int orderIndex = IndexOf(order, CurrentItem);
        int previous = orderIndex - 1;
        if (previous < 0)
        {
            if (!AutoRepeatEnabled)
            {
                return null;
            }
            previous = order.Count - 1;
        }
        int targetIndex =
            FindEnabled(order, previous, forward: false);
        EnsureCanChangeCurrentItem(targetIndex);
        return SetCurrentIndex(
                targetIndex,
                MediaPlaybackItemChangedReason.AppRequested)
            ? CurrentItem
            : null;
    }

    public MediaPlaybackItem? MoveTo(uint itemIndex)
    {
        if (itemIndex >= _items.Count)
        {
            return null;
        }
        int targetIndex = checked((int)itemIndex);
        EnsureCanChangeCurrentItem(targetIndex);
        return SetCurrentIndex(
                targetIndex,
                MediaPlaybackItemChangedReason.AppRequested)
            ? CurrentItem
            : null;
    }

    public void SetShuffledItems(
        IEnumerable<MediaPlaybackItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        MediaPlaybackItem[] shuffled = items.ToArray();
        if (shuffled.Length != _items.Count ||
            shuffled.Distinct(
                ReferenceEqualityComparer.Instance).Count() !=
            shuffled.Length)
        {
            throw new ArgumentException(
                "The shuffled sequence must contain each playback item exactly once.",
                nameof(items));
        }
        for (int index = 0; index < shuffled.Length; index++)
        {
            if (!_items.Contains(shuffled[index]))
            {
                throw new ArgumentException(
                    "Every shuffled item must belong to Items.",
                    nameof(items));
            }
        }
        _shuffledItems = shuffled;
    }

    internal bool MoveNextAfterEnd() =>
        MoveNextCore(
            MediaPlaybackItemChangedReason.EndOfStream,
            enforceCanSkip: false);

    internal bool CanMoveNextManually() =>
        TryResolveNextIndex(out int targetIndex) &&
        targetIndex != _currentIndex &&
        CanChangeCurrentItem(targetIndex);

    internal bool CanMovePreviousManually()
    {
        if (_items.Count == 0)
        {
            return false;
        }

        IReadOnlyList<MediaPlaybackItem> order =
            GetPlaybackOrder();
        int orderIndex = IndexOf(order, CurrentItem);
        int previous = orderIndex - 1;
        if (previous < 0)
        {
            if (!AutoRepeatEnabled)
            {
                return false;
            }
            previous = order.Count - 1;
        }

        int targetIndex =
            FindEnabled(order, previous, forward: false);
        return targetIndex >= 0 &&
            targetIndex != _currentIndex &&
            CanChangeCurrentItem(targetIndex);
    }

    internal void AttachPlayer(MediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        lock (_playbackOwnersGate)
        {
            for (int index = _playbackOwners.Count - 1;
                 index >= 0;
                 index--)
            {
                PlaybackOwnerEntry entry =
                    _playbackOwners[index];
                if (!entry.Player.TryGetTarget(
                        out MediaPlayer? existing))
                {
                    _playbackOwners.RemoveAt(index);
                }
                else if (ReferenceEquals(existing, player))
                {
                    entry.IsPlaybackActive = false;
                    return;
                }
            }

            _playbackOwners.Add(
                new PlaybackOwnerEntry(player));
        }
    }

    internal void DetachPlayer(MediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        lock (_playbackOwnersGate)
        {
            for (int index = _playbackOwners.Count - 1;
                 index >= 0;
                 index--)
            {
                if (!_playbackOwners[index].Player.TryGetTarget(
                        out MediaPlayer? existing) ||
                    ReferenceEquals(existing, player))
                {
                    _playbackOwners.RemoveAt(index);
                }
            }
        }
    }

    internal void SetPlayerPlaybackActive(
        MediaPlayer player,
        bool isActive)
    {
        lock (_playbackOwnersGate)
        {
            for (int index = _playbackOwners.Count - 1;
                 index >= 0;
                 index--)
            {
                PlaybackOwnerEntry entry =
                    _playbackOwners[index];
                if (!entry.Player.TryGetTarget(
                        out MediaPlayer? existing))
                {
                    _playbackOwners.RemoveAt(index);
                }
                else if (ReferenceEquals(existing, player))
                {
                    entry.IsPlaybackActive = isActive;
                    return;
                }
            }
        }
    }

    internal void RaiseItemOpened()
    {
        if (CurrentItem is { } item)
        {
            ItemOpened?.Invoke(
                this,
                new MediaPlaybackItemOpenedEventArgs(item));
        }
    }

    internal void RaiseItemFailed(
        MediaPlaybackItemError error)
    {
        if (CurrentItem is { } item)
        {
            ItemFailed?.Invoke(
                this,
                new MediaPlaybackItemFailedEventArgs(
                    item,
                    error));
        }
    }

    private bool MoveNextCore(
        MediaPlaybackItemChangedReason reason,
        bool enforceCanSkip)
    {
        if (!TryResolveNextIndex(out int targetIndex))
        {
            return false;
        }
        if (enforceCanSkip)
        {
            EnsureCanChangeCurrentItem(targetIndex);
        }
        return SetCurrentIndex(targetIndex, reason);
    }

    private bool TryResolveNextIndex(out int targetIndex)
    {
        targetIndex = -1;
        if (_items.Count == 0)
        {
            return false;
        }

        IReadOnlyList<MediaPlaybackItem> order =
            GetPlaybackOrder();
        int orderIndex = IndexOf(order, CurrentItem);
        int next = orderIndex + 1;
        if (next >= order.Count)
        {
            if (!AutoRepeatEnabled)
            {
                return false;
            }
            next = 0;
        }
        targetIndex =
            FindEnabled(order, next, forward: true);
        return targetIndex >= 0;
    }

    private void EnsureCanChangeCurrentItem(int targetIndex)
    {
        if (!CanChangeCurrentItem(targetIndex))
        {
            throw new InvalidOperationException(
                "The current MediaPlaybackItem cannot be skipped while it is playing.");
        }
    }

    private bool CanChangeCurrentItem(int targetIndex) =>
        targetIndex < 0 ||
        targetIndex == _currentIndex ||
        CurrentItem?.CanSkip != false ||
        !HasActivePlaybackOwner();

    private bool HasActivePlaybackOwner()
    {
        lock (_playbackOwnersGate)
        {
            bool isActive = false;
            for (int index = _playbackOwners.Count - 1;
                 index >= 0;
                 index--)
            {
                PlaybackOwnerEntry entry =
                    _playbackOwners[index];
                if (!entry.Player.TryGetTarget(out _))
                {
                    _playbackOwners.RemoveAt(index);
                }
                else
                {
                    isActive |= entry.IsPlaybackActive;
                }
            }
            return isActive;
        }
    }

    private int FindEnabled(
        IReadOnlyList<MediaPlaybackItem> order,
        int start,
        bool forward)
    {
        int index = start;
        for (int count = 0; count < order.Count; count++)
        {
            MediaPlaybackItem item = order[index];
            if (!item.IsDisabledInPlaybackList)
            {
                return _items.IndexOf(item);
            }

            index = forward
                ? (index + 1) % order.Count
                : (index - 1 + order.Count) % order.Count;
        }
        return -1;
    }

    private IReadOnlyList<MediaPlaybackItem>
        GetPlaybackOrder() =>
        _shuffleEnabled && _shuffledItems.Length == _items.Count
            ? _shuffledItems
            : _items;

    private void RegenerateShuffle()
    {
        _shuffledItems = _items.ToArray();
        Random.Shared.Shuffle(_shuffledItems);
    }

    private static int IndexOf(
        IReadOnlyList<MediaPlaybackItem> items,
        MediaPlaybackItem? target)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], target))
            {
                return index;
            }
        }
        return -1;
    }

    private bool SetCurrentIndex(
        int value,
        MediaPlaybackItemChangedReason reason)
    {
        if (_currentIndex == value)
        {
            return value >= 0;
        }

        MediaPlaybackItem? oldItem = CurrentItem;
        _currentIndex = value;
        MediaPlaybackItem? newItem = CurrentItem;
        CurrentItemChanged?.Invoke(
            this,
            new CurrentMediaPlaybackItemChangedEventArgs(
                oldItem,
                newItem,
                reason));
        SourceInvalidated?.Invoke(this, EventArgs.Empty);
        return newItem is not null;
    }

    private sealed class PlaybackOwnerEntry
    {
        public PlaybackOwnerEntry(MediaPlayer player)
        {
            Player = new WeakReference<MediaPlayer>(player);
        }

        public WeakReference<MediaPlayer> Player { get; }
        public bool IsPlaybackActive { get; set; }
    }
}
