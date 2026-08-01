using System.Threading;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Dispatching;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class DispatcherQueueTimer
{
    private static readonly TimeSpan MinimumRepeatingInterval =
        TimeSpan.FromMilliseconds(1);

    private readonly object _sync = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueHandler _dispatchTickHandler;
    private readonly Timer _timer;

    private TimeSpan _interval;
    private bool _isRepeating;
    private bool _isRunning;
    private bool _isDisposed;
    private int _generation;
    private int _pendingGeneration;
    private bool _tickPending;

    internal DispatcherQueueTimer(
        DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _dispatchTickHandler = DispatchTick;
        _timer = new Timer(OnTimerElapsed);
    }

    public TimeSpan Interval
    {
        get
        {
            lock (_sync)
                return _interval;
        }
        set
        {
            if (value < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value));

            lock (_sync)
            {
                if (_interval == value)
                    return;
                _interval = value;
                if (_isRunning)
                    ScheduleNoLock();
            }
        }
    }

    public bool IsRepeating
    {
        get
        {
            lock (_sync)
                return _isRepeating;
        }
        set
        {
            lock (_sync)
            {
                if (_isRepeating == value)
                    return;
                _isRepeating = value;
                if (_isRunning)
                    ScheduleNoLock();
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _isRunning;
        }
    }

    public event TypedEventHandler<DispatcherQueueTimer, object>? Tick;

    public void Start()
    {
        lock (_sync)
        {
            if (_isRunning)
                return;
            if (!_dispatcherQueue.IsAcceptingWork)
                return;

            _isRunning = true;
            ScheduleNoLock();
        }
    }

    public void Stop()
    {
        lock (_sync)
            StopNoLock();
    }

    internal void StopForQueueShutdown()
    {
        lock (_sync)
        {
            StopNoLock();
            if (_isDisposed)
                return;

            _isDisposed = true;
            _timer.Dispose();
        }
    }

    private void ScheduleNoLock()
    {
        _generation++;
        TimeSpan dueTime = _interval;
        TimeSpan period = _isRepeating
            ? Max(_interval, MinimumRepeatingInterval)
            : Timeout.InfiniteTimeSpan;
        _timer.Change(dueTime, period);
    }

    private void StopNoLock()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _generation++;
        _timer.Change(
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    private void OnTimerElapsed(object? state)
    {
        bool enqueue;
        lock (_sync)
        {
            if (!_isRunning)
                return;

            _pendingGeneration = _generation;
            enqueue = !_tickPending;
            _tickPending = true;
        }

        if (enqueue && !_dispatcherQueue.TryEnqueue(_dispatchTickHandler))
        {
            lock (_sync)
                _tickPending = false;
            StopForQueueShutdown();
        }
    }

    private void DispatchTick()
    {
        lock (_sync)
        {
            _tickPending = false;
            int generation = _pendingGeneration;
            if (!_isRunning || generation != _generation)
                return;

            if (!_isRepeating)
            {
                _isRunning = false;
                _generation++;
                _timer.Change(
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
            }
        }

        Tick?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) =>
        left >= right ? left : right;
}
