using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ProGPU.Media.Playback;
using Windows.Foundation.Collections;
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
    private readonly object _playbackListsGate = new();
    private readonly List<WeakReference<MediaPlaybackList>>
        _playbackLists = [];
    private MediaItemDisplayProperties _displayProperties = new();
    private double _totalDownloadProgress;
    private AutoLoadedDisplayPropertyKind
        _autoLoadedDisplayProperties;
    private bool _canSkip = true;
    private bool _isDisabledInPlaybackList;

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
    public AutoLoadedDisplayPropertyKind
        AutoLoadedDisplayProperties
    {
        get => _autoLoadedDisplayProperties;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _autoLoadedDisplayProperties = value;
        }
    }
    public bool CanSkip
    {
        get => _canSkip;
        set
        {
            if (_canSkip == value)
            {
                return;
            }
            _canSkip = value;
            NotifyPlaybackLists();
        }
    }
    public bool IsDisabledInPlaybackList
    {
        get => _isDisabledInPlaybackList;
        set
        {
            if (_isDisabledInPlaybackList == value)
            {
                return;
            }
            _isDisabledInPlaybackList = value;
            NotifyPlaybackLists();
        }
    }
    public double TotalDownloadProgress =>
        Volatile.Read(ref _totalDownloadProgress);

    public static MediaPlaybackItem? FindFromMediaSource(
        MediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.FindPlaybackItem();
    }

    public MediaItemDisplayProperties GetDisplayProperties() =>
        _displayProperties.Clone();

    public void ApplyDisplayProperties(
        MediaItemDisplayProperties value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _displayProperties = value.Clone();
    }

    internal void SetTotalDownloadProgress(double value) =>
        Volatile.Write(
            ref _totalDownloadProgress,
            double.IsFinite(value)
                ? Math.Clamp(value, 0d, 1d)
                : 0d);

    internal void AttachPlaybackList(MediaPlaybackList list)
    {
        lock (_playbackListsGate)
        {
            for (int index = _playbackLists.Count - 1;
                 index >= 0;
                 index--)
            {
                if (!_playbackLists[index].TryGetTarget(
                        out MediaPlaybackList? existing))
                {
                    _playbackLists.RemoveAt(index);
                }
                else if (ReferenceEquals(existing, list))
                {
                    return;
                }
            }
            _playbackLists.Add(
                new WeakReference<MediaPlaybackList>(list));
        }
    }

    internal void DetachPlaybackList(MediaPlaybackList list)
    {
        lock (_playbackListsGate)
        {
            for (int index = _playbackLists.Count - 1;
                 index >= 0;
                 index--)
            {
                if (!_playbackLists[index].TryGetTarget(
                        out MediaPlaybackList? existing) ||
                    ReferenceEquals(existing, list))
                {
                    _playbackLists.RemoveAt(index);
                }
            }
        }
    }

    private void NotifyPlaybackLists()
    {
        List<MediaPlaybackList>? targets = null;
        lock (_playbackListsGate)
        {
            for (int index = _playbackLists.Count - 1;
                 index >= 0;
                 index--)
            {
                if (_playbackLists[index].TryGetTarget(
                        out MediaPlaybackList? list))
                {
                    (targets ??=
                        new List<MediaPlaybackList>(
                            _playbackLists.Count))
                        .Add(list);
                }
                else
                {
                    _playbackLists.RemoveAt(index);
                }
            }
        }

        if (targets is null)
        {
            return;
        }
        foreach (MediaPlaybackList list in targets)
        {
            list.NotifyPlaybackItemStateChanged(this);
        }
    }

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
    private readonly MediaPlaybackItemVector _items =
        [];
    private readonly Dictionary<MediaPlaybackItem, int>
        _itemSubscriptionCounts =
            new(ReferenceEqualityComparer.Instance);
    private readonly object _playbackOwnersGate = new();
    private readonly List<PlaybackOwnerEntry> _playbackOwners = [];
    private int _currentIndex = -1;
    private MediaPlaybackItem? _currentItem;
    private MediaPlaybackItem[] _shuffledItems = [];
    private bool _autoRepeatEnabled;
    private bool _shuffleEnabled;
    private MediaPlaybackItem? _startingItem;

    public MediaPlaybackList()
    {
        _items.CollectionChanged += OnItemsCollectionChanged;
    }

    public IObservableVector<MediaPlaybackItem> Items => _items;
    public MediaPlaybackItem? CurrentItem => _currentItem;
    public uint CurrentItemIndex =>
        _currentIndex < 0 ? uint.MaxValue : (uint)_currentIndex;
    public bool AutoRepeatEnabled
    {
        get => _autoRepeatEnabled;
        set
        {
            if (_autoRepeatEnabled == value)
            {
                return;
            }
            _autoRepeatEnabled = value;
            PlaybackOrderChanged?.Invoke(this, EventArgs.Empty);
        }
    }
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
            if (value &&
                _shuffledItems.Length != _items.Count)
            {
                RegenerateShuffle();
            }
            PlaybackOrderChanged?.Invoke(this, EventArgs.Empty);
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
    internal event EventHandler? PlaybackOrderChanged;

    private void OnItemsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        UpdateItemSubscriptions(args);
        if (_shuffleEnabled)
        {
            RegenerateShuffle();
        }
        else if (_shuffledItems.Length != 0)
        {
            _shuffledItems = [];
        }

        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add:
                HandleItemsAdded(args);
                return;
            case NotifyCollectionChangedAction.Remove:
                HandleItemsRemoved(args);
                return;
            case NotifyCollectionChangedAction.Replace:
                HandleItemsReplaced(args);
                return;
            case NotifyCollectionChangedAction.Move:
                SynchronizeCurrentItemIndex();
                PlaybackOrderChanged?.Invoke(
                    this,
                    EventArgs.Empty);
                return;
            default:
                SetCurrentIndex(
                    -1,
                    MediaPlaybackItemChangedReason.AppRequested);
                return;
        }
    }

    private void UpdateItemSubscriptions(
        NotifyCollectionChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add:
                AttachItems(args.NewItems);
                break;
            case NotifyCollectionChangedAction.Remove:
                DetachItems(args.OldItems);
                break;
            case NotifyCollectionChangedAction.Replace:
                DetachItems(args.OldItems);
                AttachItems(args.NewItems);
                break;
            case NotifyCollectionChangedAction.Move:
                break;
            default:
                foreach (MediaPlaybackItem item in
                    _itemSubscriptionCounts.Keys)
                {
                    item.DetachPlaybackList(this);
                }
                _itemSubscriptionCounts.Clear();
                foreach (MediaPlaybackItem item in _items)
                {
                    AttachItem(item);
                }
                break;
        }
    }

    private void AttachItems(
        System.Collections.IList? items)
    {
        if (items is null)
        {
            return;
        }
        foreach (object? value in items)
        {
            if (value is MediaPlaybackItem item)
            {
                AttachItem(item);
            }
        }
    }

    private void AttachItem(MediaPlaybackItem item)
    {
        if (_itemSubscriptionCounts.TryGetValue(
                item,
                out int count))
        {
            _itemSubscriptionCounts[item] =
                checked(count + 1);
            return;
        }
        _itemSubscriptionCounts.Add(item, 1);
        item.AttachPlaybackList(this);
    }

    private void DetachItems(
        System.Collections.IList? items)
    {
        if (items is null)
        {
            return;
        }
        foreach (object? value in items)
        {
            if (value is MediaPlaybackItem item)
            {
                DetachItem(item);
            }
        }
    }

    private void DetachItem(MediaPlaybackItem item)
    {
        if (!_itemSubscriptionCounts.TryGetValue(
                item,
                out int count))
        {
            return;
        }
        if (count > 1)
        {
            _itemSubscriptionCounts[item] = count - 1;
            return;
        }
        _itemSubscriptionCounts.Remove(item);
        item.DetachPlaybackList(this);
    }

    internal void NotifyPlaybackItemStateChanged(
        MediaPlaybackItem item)
    {
        if (_itemSubscriptionCounts.ContainsKey(item))
        {
            PlaybackOrderChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
    }

    private void HandleItemsAdded(
        NotifyCollectionChangedEventArgs args)
    {
        int addedCount = args.NewItems?.Count ?? 0;
        if (_currentItem is null)
        {
            SetCurrentIndex(
                0,
                MediaPlaybackItemChangedReason.InitialItem);
            return;
        }

        if (args.NewStartingIndex >= 0 &&
            args.NewStartingIndex <= _currentIndex)
        {
            _currentIndex = checked(
                _currentIndex + addedCount);
        }
        PlaybackOrderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleItemsRemoved(
        NotifyCollectionChangedEventArgs args)
    {
        if (_currentItem is null)
        {
            PlaybackOrderChanged?.Invoke(
                this,
                EventArgs.Empty);
            return;
        }

        int removedStart = args.OldStartingIndex;
        int removedCount = args.OldItems?.Count ?? 0;
        int removedEnd = checked(removedStart + removedCount);
        if (removedStart < 0)
        {
            SynchronizeCurrentItemIndex();
            PlaybackOrderChanged?.Invoke(
                this,
                EventArgs.Empty);
            return;
        }
        if (_currentIndex < removedStart)
        {
            PlaybackOrderChanged?.Invoke(
                this,
                EventArgs.Empty);
            return;
        }
        if (_currentIndex >= removedEnd)
        {
            _currentIndex -= removedCount;
            PlaybackOrderChanged?.Invoke(
                this,
                EventArgs.Empty);
            return;
        }

        int nextIndex = _items.Count == 0
            ? -1
            : Math.Min(removedStart, _items.Count - 1);
        SetCurrentIndex(
            nextIndex,
            MediaPlaybackItemChangedReason.AppRequested);
    }

    private void HandleItemsReplaced(
        NotifyCollectionChangedEventArgs args)
    {
        int replacedStart = args.OldStartingIndex;
        int replacedCount = args.OldItems?.Count ?? 0;
        if (replacedStart >= 0 &&
            _currentIndex >= replacedStart &&
            _currentIndex <
                checked(replacedStart + replacedCount))
        {
            SetCurrentIndex(
                _currentIndex,
                MediaPlaybackItemChangedReason.AppRequested);
            return;
        }
        PlaybackOrderChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SynchronizeCurrentItemIndex()
    {
        if (_currentItem is null)
        {
            _currentIndex = -1;
            return;
        }

        _currentIndex = _items.IndexOf(_currentItem);
        if (_currentIndex < 0)
        {
            _currentItem = null;
        }
    }

    private sealed class MediaPlaybackItemVector :
        ObservableCollection<MediaPlaybackItem>,
        IObservableVector<MediaPlaybackItem>
    {
        public event VectorChangedEventHandler<
            MediaPlaybackItem>? VectorChanged;

        protected override void OnCollectionChanged(
            NotifyCollectionChangedEventArgs args)
        {
            base.OnCollectionChanged(args);
            VectorChanged?.Invoke(
                this,
                VectorChangedEventArgs.From(args));
        }
    }

    private sealed class VectorChangedEventArgs :
        IVectorChangedEventArgs
    {
        private VectorChangedEventArgs(
            CollectionChange collectionChange,
            uint index)
        {
            CollectionChange = collectionChange;
            Index = index;
        }

        public CollectionChange CollectionChange { get; }
        public uint Index { get; }

        public static VectorChangedEventArgs From(
            NotifyCollectionChangedEventArgs args) =>
            args.Action switch
            {
                NotifyCollectionChangedAction.Add =>
                    new(
                        CollectionChange.ItemInserted,
                        ToIndex(args.NewStartingIndex)),
                NotifyCollectionChangedAction.Remove =>
                    new(
                        CollectionChange.ItemRemoved,
                        ToIndex(args.OldStartingIndex)),
                NotifyCollectionChangedAction.Replace =>
                    new(
                        CollectionChange.ItemChanged,
                        ToIndex(args.NewStartingIndex)),
                _ => new(CollectionChange.Reset, 0)
            };

        private static uint ToIndex(int index) =>
            index < 0 ? 0u : checked((uint)index);
    }

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
        PlaybackOrderChanged?.Invoke(this, EventArgs.Empty);
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
        MediaPlaybackItem? newItem =
            value >= 0 && value < _items.Count
                ? _items[value]
                : null;
        if (_currentIndex == value &&
            ReferenceEquals(_currentItem, newItem))
        {
            return value >= 0;
        }

        MediaPlaybackItem? oldItem = _currentItem;
        _currentIndex = value;
        _currentItem = newItem;
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
