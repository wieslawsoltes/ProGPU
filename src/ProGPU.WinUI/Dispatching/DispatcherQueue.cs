using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Dispatching;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public delegate void DispatcherQueueHandler();

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public enum DispatcherQueuePriority
{
    Low = -10,
    Normal = 0,
    High = 10
}

[Flags]
[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public enum DispatcherRunOptions : uint
{
    None = 0,
    ContinueOnQuit = 1,
    QuitOnlyLocalLoop = 2
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class DispatcherExitDeferral
{
    private int _isComplete;

    public DispatcherExitDeferral()
    {
    }

    internal event Action? Completed;

    internal bool IsComplete => Volatile.Read(ref _isComplete) != 0;

    public void Complete()
    {
        if (Interlocked.Exchange(ref _isComplete, 1) == 0)
            Completed?.Invoke();
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class DispatcherQueueShutdownStartingEventArgs
{
    private readonly ShutdownDeferralTracker _tracker;

    internal DispatcherQueueShutdownStartingEventArgs(
        ShutdownDeferralTracker tracker)
    {
        _tracker = tracker;
    }

    public Deferral GetDeferral() => _tracker.GetDeferral();
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class DispatcherQueue
{
    [ThreadStatic]
    private static DispatcherQueue? s_current;

    private readonly object _sync = new();
    private readonly Queue<DispatcherQueueHandler> _highPriority = [];
    private readonly Queue<DispatcherQueueHandler> _normalPriority = [];
    private readonly Queue<DispatcherQueueHandler> _lowPriority = [];
    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly List<EventLoopFrame> _eventLoops = [];
    private readonly List<WeakReference<DispatcherQueueTimer>> _timers = [];
    private readonly DispatcherQueueHandler _exitEventLoopHandler;
    private readonly DispatcherQueueHandler _quitEventLoopHandler;
    private readonly int _ownerThreadId;
    private readonly TaskCompletionSource _shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _acceptingWork = true;
    private bool _shutdownRequested;
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    private DispatcherQueue()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _exitEventLoopHandler = MarkInnermostEventLoopForExit;
        _quitEventLoopHandler = MarkEventLoopsForQuit;
    }

    public bool HasThreadAccess =>
        Environment.CurrentManagedThreadId == _ownerThreadId;

    public event TypedEventHandler<
        DispatcherQueue,
        DispatcherQueueShutdownStartingEventArgs>?
        FrameworkShutdownStarting;

    public event TypedEventHandler<
        DispatcherQueue,
        DispatcherQueueShutdownStartingEventArgs>?
        ShutdownStarting;

    public event TypedEventHandler<DispatcherQueue, object>?
        FrameworkShutdownCompleted;

    public event TypedEventHandler<DispatcherQueue, object>?
        ShutdownCompleted;

    public static DispatcherQueue? GetForCurrentThread() => s_current;

    internal static DispatcherQueue CreateForCurrentThread()
    {
        if (s_current is not null)
        {
            throw new InvalidOperationException(
                "The current thread already owns a DispatcherQueue.");
        }

        return s_current = new DispatcherQueue();
    }

    public DispatcherQueueTimer CreateTimer()
    {
        lock (_sync)
        {
            if (_shutdownStarted || !_acceptingWork)
            {
                throw new InvalidOperationException(
                    "The DispatcherQueue has shut down.");
            }

            var timer = new DispatcherQueueTimer(this);
            PruneTimersNoLock();
            _timers.Add(new WeakReference<DispatcherQueueTimer>(timer));
            return timer;
        }
    }

    public bool TryEnqueue(DispatcherQueueHandler callback) =>
        TryEnqueue(DispatcherQueuePriority.Normal, callback);

    public bool TryEnqueue(
        DispatcherQueuePriority priority,
        DispatcherQueueHandler callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            if (!_acceptingWork)
                return false;

            GetQueue(priority).Enqueue(callback);
        }

        _workAvailable.Set();
        return true;
    }

    public void EnqueueEventLoopExit()
    {
        _ = TryEnqueue(
            DispatcherQueuePriority.Normal,
            _exitEventLoopHandler);
    }

    // Native hosts use this typed seam when their platform message pump
    // observes a quit message. EnqueueEventLoopExit remains a local-loop exit
    // operation, matching the documented nested-loop contract.
    internal void EnqueueQuit()
    {
        _ = TryEnqueue(
            DispatcherQueuePriority.Normal,
            _quitEventLoopHandler);
    }

    public void EnsureSystemDispatcherQueue()
    {
        // ProGPU's platform-neutral queue is already the system dispatch source
        // for its composition and input services. Native hosts can bridge their
        // OS queue to TryEnqueue without a second managed message pump.
    }

    public void RunEventLoop() =>
        RunEventLoopCore(DispatcherRunOptions.None, deferral: null);

    public void RunEventLoop(
        DispatcherRunOptions options,
        DispatcherExitDeferral deferral)
    {
        ArgumentNullException.ThrowIfNull(deferral);
        const DispatcherRunOptions KnownOptions =
            DispatcherRunOptions.ContinueOnQuit |
            DispatcherRunOptions.QuitOnlyLocalLoop;
        if ((options & ~KnownOptions) != 0)
            throw new ArgumentOutOfRangeException(nameof(options));

        RunEventLoopCore(options, deferral);
    }

    internal Task RequestShutdownAsync()
    {
        bool runInline;
        lock (_sync)
        {
            if (_shutdownRequested)
                return _shutdownCompletion.Task;

            _shutdownRequested = true;
            runInline = HasThreadAccess;
            if (!runInline)
            {
                _normalPriority.Enqueue(ShutdownFromQueuedCallback);
            }
        }

        if (runInline)
        {
            ShutdownAndCompleteAsyncRequest();
        }
        else
        {
            _workAvailable.Set();
        }

        return _shutdownCompletion.Task;
    }

    internal void ShutdownSynchronously()
    {
        VerifyAccess();
        lock (_sync)
        {
            if (_shutdownComplete)
                return;
            if (_shutdownStarted)
            {
                throw new InvalidOperationException(
                    "DispatcherQueue shutdown is already in progress.");
            }

            _shutdownRequested = true;
        }

        try
        {
            ShutdownCore();
            _shutdownCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _shutdownCompletion.TrySetException(exception);
            throw;
        }
    }

    internal bool IsAcceptingWork
    {
        get
        {
            lock (_sync)
                return _acceptingWork;
        }
    }

    private void RunEventLoopCore(
        DispatcherRunOptions options,
        DispatcherExitDeferral? deferral)
    {
        VerifyAccess();
        lock (_sync)
        {
            if (_shutdownComplete)
                return;
        }

        var frame = new EventLoopFrame(options, deferral);
        if (deferral is not null)
            deferral.Completed += OnExitDeferralCompleted;
        _eventLoops.Add(frame);
        try
        {
            while (!frame.CanExit)
            {
                if (TryDequeue(out var callback))
                {
                    callback();
                    continue;
                }

                _workAvailable.WaitOne();
            }
        }
        finally
        {
            _eventLoops.Remove(frame);
            if (deferral is not null)
                deferral.Completed -= OnExitDeferralCompleted;
        }
    }

    private void ShutdownFromQueuedCallback() =>
        ShutdownAndCompleteAsyncRequest();

    private void ShutdownAndCompleteAsyncRequest()
    {
        try
        {
            ShutdownCore();
            _shutdownCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _shutdownCompletion.TrySetException(exception);
            RequestAllEventLoopsExit();
        }
    }

    private void ShutdownCore()
    {
        VerifyAccess();
        lock (_sync)
        {
            if (_shutdownComplete)
                return;
            if (_shutdownStarted)
                return;
            _shutdownStarted = true;
        }

        StopTimers();
        RaiseShutdownStarting(ShutdownStarting);
        RaiseShutdownStarting(FrameworkShutdownStarting);

        lock (_sync)
            _acceptingWork = false;

        DrainPendingWork();
        FrameworkShutdownCompleted?.Invoke(this, EventArgs.Empty);
        ShutdownCompleted?.Invoke(this, EventArgs.Empty);

        lock (_sync)
            _shutdownComplete = true;

        RequestAllEventLoopsExit();
        if (ReferenceEquals(s_current, this))
            s_current = null;
    }

    private void RaiseShutdownStarting(
        TypedEventHandler<
            DispatcherQueue,
            DispatcherQueueShutdownStartingEventArgs>? handler)
    {
        var tracker = new ShutdownDeferralTracker(_workAvailable);
        handler?.Invoke(
            this,
            new DispatcherQueueShutdownStartingEventArgs(tracker));
        tracker.Seal();
        while (!tracker.IsComplete || HasPendingWork())
        {
            if (TryDequeue(out var callback))
            {
                callback();
                continue;
            }

            _workAvailable.WaitOne();
        }
    }

    private void DrainPendingWork()
    {
        while (TryDequeue(out var callback))
            callback();
    }

    private bool TryDequeue(
        out DispatcherQueueHandler callback)
    {
        lock (_sync)
        {
            if (_highPriority.Count > 0)
            {
                callback = _highPriority.Dequeue();
                return true;
            }

            if (_normalPriority.Count > 0)
            {
                callback = _normalPriority.Dequeue();
                return true;
            }

            if (_lowPriority.Count > 0)
            {
                callback = _lowPriority.Dequeue();
                return true;
            }
        }

        callback = null!;
        return false;
    }

    private bool HasPendingWork()
    {
        lock (_sync)
        {
            return _highPriority.Count != 0 ||
                _normalPriority.Count != 0 ||
                _lowPriority.Count != 0;
        }
    }

    private Queue<DispatcherQueueHandler> GetQueue(
        DispatcherQueuePriority priority) =>
        priority switch
        {
            DispatcherQueuePriority.High => _highPriority,
            DispatcherQueuePriority.Low => _lowPriority,
            _ => _normalPriority
        };

    private void MarkInnermostEventLoopForExit()
    {
        if (_eventLoops.Count > 0)
            _eventLoops[^1].ExitRequested = true;
    }

    private void MarkEventLoopsForQuit()
    {
        if (_eventLoops.Count == 0)
            return;

        EventLoopFrame innermost = _eventLoops[^1];
        if ((innermost.Options & DispatcherRunOptions.ContinueOnQuit) != 0)
            return;

        if ((innermost.Options & DispatcherRunOptions.QuitOnlyLocalLoop) != 0)
        {
            innermost.ExitRequested = true;
            return;
        }

        foreach (EventLoopFrame frame in _eventLoops)
            frame.ExitRequested = true;
    }

    private void RequestAllEventLoopsExit()
    {
        foreach (var frame in _eventLoops)
            frame.ExitRequested = true;
        _workAvailable.Set();
    }

    private void OnExitDeferralCompleted() => _workAvailable.Set();

    private void StopTimers()
    {
        List<DispatcherQueueTimer> liveTimers = [];
        lock (_sync)
        {
            foreach (var registration in _timers)
            {
                if (registration.TryGetTarget(out var timer))
                    liveTimers.Add(timer);
            }

            _timers.Clear();
        }

        foreach (var timer in liveTimers)
            timer.StopForQueueShutdown();
    }

    private void PruneTimersNoLock()
    {
        for (int index = _timers.Count - 1; index >= 0; index--)
        {
            if (!_timers[index].TryGetTarget(out _))
                _timers.RemoveAt(index);
        }
    }

    private void VerifyAccess()
    {
        if (!HasThreadAccess)
        {
            throw new InvalidOperationException(
                "This operation must run on the DispatcherQueue thread.");
        }
    }

    private sealed class EventLoopFrame
    {
        public EventLoopFrame(
            DispatcherRunOptions options,
            DispatcherExitDeferral? deferral)
        {
            Options = options;
            Deferral = deferral;
        }

        public DispatcherRunOptions Options { get; }

        public DispatcherExitDeferral? Deferral { get; }

        public bool ExitRequested { get; set; }

        public bool CanExit =>
            ExitRequested && (Deferral?.IsComplete ?? true);
    }
}

internal sealed class ShutdownDeferralTracker
{
    private readonly object _sync = new();
    private readonly AutoResetEvent _workAvailable;
    private int _count = 1;
    private bool _isSealed;

    public ShutdownDeferralTracker(AutoResetEvent workAvailable)
    {
        _workAvailable = workAvailable;
    }

    public bool IsComplete => Volatile.Read(ref _count) == 0;

    public Deferral GetDeferral()
    {
        lock (_sync)
        {
            if (_isSealed)
            {
                throw new InvalidOperationException(
                    "The shutdown event has already completed.");
            }

            _count++;
        }

        return new Deferral(CompleteDeferral);
    }

    public void Seal()
    {
        lock (_sync)
            _isSealed = true;
        CompleteDeferral();
    }

    private void CompleteDeferral()
    {
        int remaining;
        lock (_sync)
        {
            remaining = --_count;
            if (remaining < 0)
            {
                throw new InvalidOperationException(
                    "A shutdown deferral was completed more than once.");
            }
        }

        if (remaining == 0)
            _workAvailable.Set();
    }
}

internal sealed class TaskAsyncAction : IAsyncAction
{
    private readonly Task _task;

    public TaskAsyncAction(Task task)
    {
        _task = task;
    }

    public Task AsTask() => _task;

    public TaskAwaiter GetAwaiter() => _task.GetAwaiter();
}

public class DispatcherQueueSynchronizationContext :
    SynchronizationContext
{
    private const int MaximumRetainedWorkItems = 256;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _workItemSync = new();
    private readonly Stack<ContextWorkItem> _workItemPool = [];

    public DispatcherQueueSynchronizationContext(
        DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _dispatcherQueue = dispatcherQueue;
    }

    public override SynchronizationContext CreateCopy() =>
        new DispatcherQueueSynchronizationContext(_dispatcherQueue);

    public override void Post(
        SendOrPostCallback d,
        object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        ContextWorkItem workItem =
            RentWorkItem(d, state, isSynchronous: false);
        if (_dispatcherQueue.TryEnqueue(workItem.Handler))
            return;

        ReturnWorkItem(workItem);
        throw new InvalidOperationException(
            "The DispatcherQueue has shut down.");
    }

    public override void Send(
        SendOrPostCallback d,
        object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (_dispatcherQueue.HasThreadAccess)
        {
            d(state);
            return;
        }

        ContextWorkItem workItem =
            RentWorkItem(d, state, isSynchronous: true);
        if (!_dispatcherQueue.TryEnqueue(workItem.Handler))
        {
            ReturnWorkItem(workItem);
            throw new InvalidOperationException(
                "The DispatcherQueue has shut down.");
        }

        workItem.Wait();
        ExceptionDispatchInfo? failure =
            workItem.Failure;
        ReturnWorkItem(workItem);
        failure?.Throw();
    }

    private ContextWorkItem RentWorkItem(
        SendOrPostCallback callback,
        object? state,
        bool isSynchronous)
    {
        ContextWorkItem workItem;
        lock (_workItemSync)
        {
            workItem = _workItemPool.Count > 0
                ? _workItemPool.Pop()
                : new ContextWorkItem(this);
        }

        workItem.Prepare(
            callback,
            state,
            isSynchronous);
        return workItem;
    }

    private void ReturnWorkItem(ContextWorkItem workItem)
    {
        workItem.Reset();
        lock (_workItemSync)
        {
            if (_workItemPool.Count < MaximumRetainedWorkItems)
                _workItemPool.Push(workItem);
        }
    }

    private sealed class ContextWorkItem
    {
        private readonly DispatcherQueueSynchronizationContext _owner;
        private readonly DispatcherQueueHandler _handler;

        private SendOrPostCallback? _callback;
        private object? _state;
        private AutoResetEvent? _completed;
        private bool _isSynchronous;

        public ContextWorkItem(
            DispatcherQueueSynchronizationContext owner)
        {
            _owner = owner;
            _handler = Execute;
        }

        public DispatcherQueueHandler Handler => _handler;

        public ExceptionDispatchInfo? Failure { get; private set; }

        public void Prepare(
            SendOrPostCallback callback,
            object? state,
            bool isSynchronous)
        {
            _callback = callback;
            _state = state;
            _isSynchronous = isSynchronous;
            if (isSynchronous)
            {
                // Send must have a stable allocation profile after warmup.
                // Create the kernel-backed wait primitive with the pooled item
                // instead of relying on ManualResetEventSlim's deferred runtime
                // transition during a later contended wait.
                _completed ??= new AutoResetEvent(initialState: false);
            }
        }

        public void Wait() => _completed!.WaitOne();

        public void Reset()
        {
            _callback = null;
            _state = null;
            _isSynchronous = false;
            Failure = null;
        }

        private void Execute()
        {
            if (!_isSynchronous)
            {
                try
                {
                    _callback!(_state);
                }
                finally
                {
                    _owner.ReturnWorkItem(this);
                }

                return;
            }

            try
            {
                _callback!(_state);
            }
            catch (Exception exception)
            {
                Failure =
                    ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                _completed!.Set();
            }
        }
    }
}
