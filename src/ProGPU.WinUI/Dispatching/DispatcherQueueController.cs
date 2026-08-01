using System.Threading;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Dispatching;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class DispatcherQueueController
{
    private readonly bool _ownsDedicatedThread;
    private readonly Task _dedicatedThreadExit;

    private DispatcherQueueController(
        DispatcherQueue dispatcherQueue,
        bool ownsDedicatedThread,
        Task dedicatedThreadExit)
    {
        DispatcherQueue = dispatcherQueue;
        _ownsDedicatedThread = ownsDedicatedThread;
        _dedicatedThreadExit = dedicatedThreadExit;
    }

    public DispatcherQueue DispatcherQueue { get; }

    public static DispatcherQueueController CreateOnCurrentThread() =>
        new(
            DispatcherQueue.CreateForCurrentThread(),
            ownsDedicatedThread: false,
            Task.CompletedTask);

    public static DispatcherQueueController CreateOnDedicatedThread()
    {
        var ready = new TaskCompletionSource<DispatcherQueueController>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () => RunDedicatedThread(ready, exited))
        {
            IsBackground = true,
            Name = "ProGPU DispatcherQueue"
        };
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    public void ShutdownQueue()
    {
        if (!_ownsDedicatedThread ||
            DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.ShutdownSynchronously();
            return;
        }

        ShutdownDedicatedThreadAsync().GetAwaiter().GetResult();
    }

    public IAsyncAction ShutdownQueueAsync() =>
        new TaskAsyncAction(
            _ownsDedicatedThread
                ? ShutdownDedicatedThreadAsync()
                : DispatcherQueue.RequestShutdownAsync());

    private static void RunDedicatedThread(
        TaskCompletionSource<DispatcherQueueController> ready,
        TaskCompletionSource exited)
    {
        DispatcherQueue? queue = null;
        try
        {
            queue = DispatcherQueue.CreateForCurrentThread();
            ready.TrySetResult(
                new DispatcherQueueController(
                    queue,
                    ownsDedicatedThread: true,
                    exited.Task));
            queue.RunEventLoop();
            if (queue.IsAcceptingWork)
                queue.ShutdownSynchronously();
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            exited.TrySetException(exception);
            return;
        }

        exited.TrySetResult();
    }

    private async Task ShutdownDedicatedThreadAsync()
    {
        await DispatcherQueue
            .RequestShutdownAsync()
            .ConfigureAwait(false);
        await _dedicatedThreadExit.ConfigureAwait(false);
    }
}
