namespace ProGPU.Media.Playback;

/// <summary>
/// Supplies timing and lifecycle callbacks for a reusable timed-cue timeline.
/// </summary>
/// <typeparam name="TCue">The caller-owned cue type.</typeparam>
public interface IMediaTimedCueTimelineClient<TCue>
    where TCue : class
{
    TimeSpan GetStartTime(TCue cue);
    TimeSpan GetDuration(TCue cue);
    void OnCueEntered(TCue cue);
    void OnCueExited(TCue cue);
}

/// <summary>
/// Maintains a sorted cue schedule and the cues active at a playback position.
/// </summary>
/// <remarks>
/// Cue insertion is O(C), a steady forward update is O(E + A), and a seek or
/// schedule change is O(C * A), where C is the cue count, E is the number of
/// crossed cue boundaries, and A is the active-cue count. Lists retain their
/// capacity, so steady updates allocate no managed memory after warmup.
/// </remarks>
public sealed class MediaTimedCueTimeline<TCue>
    where TCue : class
{
    private readonly IMediaTimedCueTimelineClient<TCue> _client;
    private readonly List<TCue> _cues = [];
    private readonly List<TCue> _activeCues = [];
    private readonly List<TCue> _desiredCues = [];
    private readonly List<TCue> _enteredCues = [];
    private readonly List<TCue> _exitedCues = [];
    private readonly System.Collections.ObjectModel
        .ReadOnlyCollection<TCue> _readOnlyCues;
    private readonly System.Collections.ObjectModel
        .ReadOnlyCollection<TCue> _readOnlyActiveCues;
    private TimeSpan _position;
    private int _cursor;
    private bool _enabled;
    private bool _hasPosition;
    private bool _requiresReconcile;
    private bool _isSynchronizing;
    private bool _pendingSynchronization;
    private TimeSpan _pendingPosition;
    private bool _pendingEnabled;

    public MediaTimedCueTimeline(
        IMediaTimedCueTimelineClient<TCue> client)
    {
        _client = client ??
            throw new ArgumentNullException(nameof(client));
        _readOnlyCues = _cues.AsReadOnly();
        _readOnlyActiveCues = _activeCues.AsReadOnly();
    }

    public IReadOnlyList<TCue> Cues => _readOnlyCues;
    public IReadOnlyList<TCue> ActiveCues =>
        _readOnlyActiveCues;

    public bool AddCue(TCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        if (_cues.Contains(cue))
        {
            return false;
        }

        TimeSpan startTime = _client.GetStartTime(cue);
        int insertionIndex = FindInsertionIndex(startTime);
        _cues.Insert(insertionIndex, cue);
        _requiresReconcile = true;
        return true;
    }

    public bool RemoveCue(TCue cue)
    {
        ArgumentNullException.ThrowIfNull(cue);
        bool removed = _cues.Remove(cue);
        if (removed)
        {
            _requiresReconcile = true;
        }
        return removed;
    }

    /// <summary>
    /// Marks mutable cue start times or durations for reevaluation.
    /// </summary>
    public void InvalidateSchedule()
    {
        _cues.Sort(CompareCueStart);
        _requiresReconcile = true;
    }

    public void Synchronize(
        TimeSpan position,
        bool enabled)
    {
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (_isSynchronizing)
        {
            _pendingSynchronization = true;
            _pendingPosition = position;
            _pendingEnabled = enabled;
            return;
        }

        _isSynchronizing = true;
        try
        {
            do
            {
                _pendingSynchronization = false;
                SynchronizeCore(position, enabled);
                if (_pendingSynchronization)
                {
                    position = _pendingPosition;
                    enabled = _pendingEnabled;
                }
            }
            while (_pendingSynchronization);
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    /// <summary>
    /// Clears playback state. Schedule ownership is retained.
    /// </summary>
    public void Reset()
    {
        _activeCues.Clear();
        _desiredCues.Clear();
        _enteredCues.Clear();
        _exitedCues.Clear();
        _position = TimeSpan.Zero;
        _cursor = 0;
        _enabled = false;
        _hasPosition = false;
        _requiresReconcile = false;
        _pendingSynchronization = false;
    }

    private void SynchronizeCore(
        TimeSpan position,
        bool enabled)
    {
        if (!enabled)
        {
            // Disabled presentation suppresses cue events by contract.
            _activeCues.Clear();
            _position = position;
            _cursor = FindCursor(position);
            _enabled = false;
            _hasPosition = true;
            _requiresReconcile = false;
            return;
        }

        if (!_enabled ||
            !_hasPosition ||
            position < _position ||
            _requiresReconcile)
        {
            Reconcile(position);
        }
        else if (position > _position)
        {
            Advance(position);
        }

        _position = position;
        _enabled = true;
        _hasPosition = true;
    }

    private void Advance(TimeSpan position)
    {
        _enteredCues.Clear();
        _exitedCues.Clear();

        for (int index = _activeCues.Count - 1;
             index >= 0;
             index--)
        {
            TCue cue = _activeCues[index];
            if (!IsActiveAt(cue, position))
            {
                _activeCues.RemoveAt(index);
                _exitedCues.Add(cue);
            }
        }

        while (_cursor < _cues.Count)
        {
            TCue cue = _cues[_cursor];
            if (_client.GetStartTime(cue) > position)
            {
                break;
            }
            _cursor++;
            if (IsActiveAt(cue, position))
            {
                _activeCues.Add(cue);
                _enteredCues.Add(cue);
            }
        }

        DispatchChanges();
    }

    private void Reconcile(TimeSpan position)
    {
        _desiredCues.Clear();
        _enteredCues.Clear();
        _exitedCues.Clear();
        _cursor = FindCursor(position);

        for (int index = 0; index < _cursor; index++)
        {
            TCue cue = _cues[index];
            if (IsActiveAt(cue, position))
            {
                _desiredCues.Add(cue);
            }
        }

        for (int index = 0;
             index < _activeCues.Count;
             index++)
        {
            TCue cue = _activeCues[index];
            if (!_desiredCues.Contains(cue))
            {
                _exitedCues.Add(cue);
            }
        }
        for (int index = 0;
             index < _desiredCues.Count;
             index++)
        {
            TCue cue = _desiredCues[index];
            if (!_activeCues.Contains(cue))
            {
                _enteredCues.Add(cue);
            }
        }

        _activeCues.Clear();
        _activeCues.AddRange(_desiredCues);
        _requiresReconcile = false;
        DispatchChanges();
    }

    private void DispatchChanges()
    {
        for (int index = _exitedCues.Count - 1;
             index >= 0;
             index--)
        {
            _client.OnCueExited(_exitedCues[index]);
        }
        for (int index = 0;
             index < _enteredCues.Count;
             index++)
        {
            _client.OnCueEntered(_enteredCues[index]);
        }
    }

    private bool IsActiveAt(TCue cue, TimeSpan position)
    {
        TimeSpan start = _client.GetStartTime(cue);
        TimeSpan duration = _client.GetDuration(cue);
        if (duration <= TimeSpan.Zero || position < start)
        {
            return false;
        }

        TimeSpan end;
        try
        {
            end = start + duration;
        }
        catch (OverflowException)
        {
            end = TimeSpan.MaxValue;
        }
        return position < end;
    }

    private int FindInsertionIndex(TimeSpan startTime)
    {
        int low = 0;
        int high = _cues.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_client.GetStartTime(_cues[middle]) <=
                startTime)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private int FindCursor(TimeSpan position)
    {
        int low = 0;
        int high = _cues.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_client.GetStartTime(_cues[middle]) <=
                position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private int CompareCueStart(TCue? left, TCue? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left is null)
        {
            return -1;
        }
        if (right is null)
        {
            return 1;
        }
        return _client.GetStartTime(left).CompareTo(
            _client.GetStartTime(right));
    }
}
