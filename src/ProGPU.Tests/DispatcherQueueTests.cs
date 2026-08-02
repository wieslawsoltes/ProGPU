using Microsoft.UI.Dispatching;
using Xunit;

namespace ProGPU.Tests;

public sealed class DispatcherQueueTests
{
    [Fact]
    public void CurrentThreadQueueIsSingletonAndRunsPrioritiesInOrder()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        DispatcherQueue queue = controller.DispatcherQueue;
        try
        {
            Assert.Same(queue, DispatcherQueue.GetForCurrentThread());
            Assert.True(queue.HasThreadAccess);
            Assert.Throws<InvalidOperationException>(
                DispatcherQueueController.CreateOnCurrentThread);

            var observed = new List<string>();
            Assert.True(
                queue.TryEnqueue(
                    DispatcherQueuePriority.Low,
                    () =>
                    {
                        observed.Add("low");
                        queue.EnqueueEventLoopExit();
                    }));
            Assert.True(
                queue.TryEnqueue(
                    DispatcherQueuePriority.Normal,
                    () => observed.Add("normal")));
            Assert.True(
                queue.TryEnqueue(
                    DispatcherQueuePriority.High,
                    () => observed.Add("high")));
            queue.RunEventLoop();

            Assert.Equal(
                ["high", "normal", "low"],
                observed);
        }
        finally
        {
            controller.ShutdownQueue();
        }

        Assert.Null(DispatcherQueue.GetForCurrentThread());
        Assert.False(queue.TryEnqueue(static () => { }));
    }

    [Fact]
    public void NestedEventLoopExitWaitsForItsDeferral()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        try
        {
            DispatcherQueue queue = controller.DispatcherQueue;
            var deferral = new DispatcherExitDeferral();
            var observed = new List<string>();

            queue.EnqueueEventLoopExit();
            Assert.True(
                queue.TryEnqueue(
                    () =>
                    {
                        observed.Add("complete");
                        deferral.Complete();
                    }));

            queue.RunEventLoop(
                DispatcherRunOptions.QuitOnlyLocalLoop,
                deferral);

            Assert.Equal(["complete"], observed);
        }
        finally
        {
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void QuitOptionsDistinguishLocalGlobalAndContinuedNestedLoops()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        try
        {
            DispatcherQueue queue = controller.DispatcherQueue;
            var observed = new List<string>();

            Assert.True(queue.TryEnqueue(() =>
            {
                var localDeferral = new DispatcherExitDeferral();
                localDeferral.Complete();
                Assert.True(queue.TryEnqueue(queue.EnqueueQuit));
                queue.RunEventLoop(
                    DispatcherRunOptions.QuitOnlyLocalLoop,
                    localDeferral);
                observed.Add("local-returned");

                var continuedDeferral = new DispatcherExitDeferral();
                continuedDeferral.Complete();
                Assert.True(queue.TryEnqueue(queue.EnqueueQuit));
                Assert.True(queue.TryEnqueue(queue.EnqueueEventLoopExit));
                queue.RunEventLoop(
                    DispatcherRunOptions.ContinueOnQuit,
                    continuedDeferral);
                observed.Add("continued-returned");

                var globalDeferral = new DispatcherExitDeferral();
                globalDeferral.Complete();
                Assert.True(queue.TryEnqueue(queue.EnqueueQuit));
                queue.RunEventLoop(
                    DispatcherRunOptions.None,
                    globalDeferral);
                observed.Add("global-returned");
            }));

            queue.RunEventLoop();

            Assert.Equal(
                ["local-returned", "continued-returned", "global-returned"],
                observed);
        }
        finally
        {
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void ShutdownUsesDocumentedOrderAndDrainsDeferrals()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        DispatcherQueue queue = controller.DispatcherQueue;
        var observed = new List<string>();

        queue.ShutdownStarting += (_, args) =>
        {
            observed.Add("shutdown-starting");
            Windows.Foundation.Deferral deferral =
                args.GetDeferral();
            Assert.True(
                queue.TryEnqueue(
                    () =>
                    {
                        observed.Add("application-deferral");
                        deferral.Complete();
                    }));
        };
        queue.FrameworkShutdownStarting += (_, args) =>
        {
            observed.Add("framework-starting");
            Windows.Foundation.Deferral deferral =
                args.GetDeferral();
            Assert.True(
                queue.TryEnqueue(
                    () =>
                    {
                        observed.Add("framework-deferral");
                        deferral.Complete();
                    }));
        };
        queue.FrameworkShutdownCompleted +=
            (_, _) => observed.Add("framework-completed");
        queue.ShutdownCompleted +=
            (_, _) => observed.Add("shutdown-completed");

        controller.ShutdownQueue();

        Assert.Equal(
            [
                "shutdown-starting",
                "application-deferral",
                "framework-starting",
                "framework-deferral",
                "framework-completed",
                "shutdown-completed"
            ],
            observed);
        Assert.False(queue.TryEnqueue(static () => { }));
    }

    [Fact]
    public async Task DedicatedQueueRunsSeriallyOnItsOwnedThread()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnDedicatedThread();
        DispatcherQueue queue = controller.DispatcherQueue;
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var threadIds = new List<int>();

        for (int index = 0; index < 64; index++)
        {
            Assert.True(
                queue.TryEnqueue(
                    () =>
                    {
                        Assert.True(queue.HasThreadAccess);
                        threadIds.Add(
                            Environment.CurrentManagedThreadId);
                        if (threadIds.Count == 64)
                            completed.TrySetResult();
                    }));
        }

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller
            .ShutdownQueueAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(threadIds.Distinct());
        Assert.False(queue.HasThreadAccess);
        Assert.False(queue.TryEnqueue(static () => { }));
    }

    [Fact]
    public void DedicatedQueueCanShutDownSynchronouslyFromOwner()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnDedicatedThread();
        DispatcherQueue queue = controller.DispatcherQueue;

        controller.ShutdownQueue();

        Assert.False(queue.TryEnqueue(static () => { }));
    }

    [Fact]
    public async Task OneShotTimerTicksOnQueueAndStops()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnDedicatedThread();
        DispatcherQueue queue = controller.DispatcherQueue;
        try
        {
            DispatcherQueueTimer timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(10);
            var ticked = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            timer.Tick += (_, _) =>
            {
                Assert.True(queue.HasThreadAccess);
                ticked.TrySetResult();
            };

            timer.Start();
            await ticked.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(timer.IsRunning);
        }
        finally
        {
            await controller
                .ShutdownQueueAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RepeatingTimerCoalescesAndCanStopFromTick()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnDedicatedThread();
        DispatcherQueue queue = controller.DispatcherQueue;
        try
        {
            DispatcherQueueTimer timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1);
            timer.IsRepeating = true;
            var stopped = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int count = 0;
            timer.Tick += (_, _) =>
            {
                int current = ++count;
                if (current == 3)
                {
                    timer.Stop();
                    stopped.TrySetResult(current);
                }
            };

            timer.Start();
            Assert.Equal(
                3,
                await stopped.Task.WaitAsync(
                    TimeSpan.FromSeconds(5)));
            Assert.False(timer.IsRunning);
        }
        finally
        {
            await controller
                .ShutdownQueueAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task TimerReconfigurationDoesNotDuplicateQueuedTicks()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnDedicatedThread();
        DispatcherQueue queue = controller.DispatcherQueue;
        try
        {
            DispatcherQueueTimer timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1);
            timer.IsRepeating = true;
            var firstTick = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int count = 0;
            timer.Tick += (_, _) =>
            {
                if (Interlocked.Increment(ref count) == 1)
                    firstTick.TrySetResult();
            };

            using var releaseQueue = new ManualResetEventSlim();
            Assert.True(queue.TryEnqueue(() => releaseQueue.Wait()));
            timer.Start();
            await Task.Delay(20);
            timer.Interval = TimeSpan.FromMilliseconds(250);
            // Release after the first reconfigured period but comfortably
            // before the second. Releasing at exactly two periods races the
            // second legitimate timer callback on loaded CI hosts.
            await Task.Delay(300);
            releaseQueue.Set();

            await firstTick.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(75);
            Assert.Equal(1, Volatile.Read(ref count));
            timer.Stop();
        }
        finally
        {
            await controller
                .ShutdownQueueAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SynchronizationContextMarshalsPostAndSend()
    {
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnDedicatedThread();
        DispatcherQueue queue = controller.DispatcherQueue;
        try
        {
            var context =
                new DispatcherQueueSynchronizationContext(queue);
            var posted = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            context.Post(
                state =>
                {
                    Assert.True(queue.HasThreadAccess);
                    posted.TrySetResult((int)state!);
                },
                42);

            Assert.Equal(
                42,
                await posted.Task.WaitAsync(
                    TimeSpan.FromSeconds(5)));

            int sendThreadId = 0;
            context.Send(
                _ =>
                {
                    Assert.True(queue.HasThreadAccess);
                    sendThreadId =
                        Environment.CurrentManagedThreadId;
                },
                null);
            Assert.NotEqual(
                Environment.CurrentManagedThreadId,
                sendThreadId);

            InvalidOperationException exception =
                Assert.Throws<InvalidOperationException>(
                    () =>
                        context.Send(
                            _ => throw new InvalidOperationException(
                                "sent"),
                            null));
            Assert.Equal("sent", exception.Message);
        }
        finally
        {
            await controller
                .ShutdownQueueAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void WarmedEnqueuePathAllocatesNoManagedMemory()
    {
        const int Count = 2_000;
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnCurrentThread();
        try
        {
            DispatcherQueue queue = controller.DispatcherQueue;
            DispatcherQueueHandler callback = static () => { };

            for (int index = 0; index < Count; index++)
                Assert.True(queue.TryEnqueue(callback));
            queue.EnqueueEventLoopExit();
            queue.RunEventLoop();

            _ = GC.GetAllocatedBytesForCurrentThread();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < Count; index++)
            {
                if (!queue.TryEnqueue(callback))
                    throw new InvalidOperationException();
            }

            long allocated =
                GC.GetAllocatedBytesForCurrentThread() - before;

            queue.EnqueueEventLoopExit();
            queue.RunEventLoop();
            Assert.Equal(0, allocated);
        }
        finally
        {
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public async Task WarmedSynchronizationContextSendReusesWorkItem()
    {
        const int Count = 2_000;
        DispatcherQueueController controller =
            DispatcherQueueController.CreateOnDedicatedThread();
        try
        {
            var context =
                new DispatcherQueueSynchronizationContext(
                    controller.DispatcherQueue);
            SendOrPostCallback callback = static _ => { };
            for (int index = 0; index < 16; index++)
                context.Send(callback, null);

            _ = GC.GetAllocatedBytesForCurrentThread();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < Count; index++)
                context.Send(callback, null);

            long allocated =
                GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
        }
        finally
        {
            await controller
                .ShutdownQueueAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
