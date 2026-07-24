using System;
using System.Threading;
using Avalonia.SilkNet;
using Xunit;

namespace Avalonia.IntegrationTests.SilkNet;

public class DispatcherTests
{
    [Fact]
    public void RenderTimerStartsWithConfiguredFrameInterval()
    {
        var timer = new SilkNetRenderTimer(60);

        Assert.False(timer.RunsInBackground);
        Assert.Equal(TimeSpan.FromSeconds(1.0 / 60), timer.Interval);
    }

    [Fact]
    public void DispatcherThreadIsTrackedPerInstance()
    {
        var dispatcher = new SilkNetDispatcherImpl(() => { });
        SilkNetDispatcherImpl? otherDispatcher = null;
        var otherDispatcherOwnsThread = false;

        var thread = new Thread(() =>
        {
            otherDispatcher = new SilkNetDispatcherImpl(() => { });
            otherDispatcherOwnsThread = otherDispatcher.CurrentThreadIsLoopThread;
        });
        thread.Start();
        thread.Join();

        Assert.True(dispatcher.CurrentThreadIsLoopThread);
        Assert.True(otherDispatcherOwnsThread);
        Assert.False(otherDispatcher!.CurrentThreadIsLoopThread);
    }

    [Fact]
    public void SignalRaisedDuringCallbackIsNotLost()
    {
        var dispatcher = new SilkNetDispatcherImpl(() => { });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var callbackCount = 0;
        dispatcher.Signaled += () =>
        {
            callbackCount++;
            if (callbackCount == 1)
            {
                dispatcher.Signal();
            }
            else
            {
                cancellation.Cancel();
            }
        };

        dispatcher.Signal();
        dispatcher.RunLoop(cancellation.Token);

        Assert.Equal(2, callbackCount);
    }

    [Fact]
    public void TimerFiresAtRequestedDeadline()
    {
        var dispatcher = new SilkNetDispatcherImpl(() => { });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        long? firedAt = null;
        dispatcher.Timer += () =>
        {
            firedAt = dispatcher.Now;
            cancellation.Cancel();
        };

        var dueTime = dispatcher.Now + 25;
        dispatcher.UpdateTimer(dueTime);
        dispatcher.RunLoop(cancellation.Token);

        Assert.NotNull(firedAt);
        Assert.True(firedAt >= dueTime);
        Assert.True(firedAt - dueTime < 250);
    }

    [Fact]
    public void TimerIsNotStarvedByContinuousSignals()
    {
        SilkNetDispatcherImpl? dispatcher = null;
        dispatcher = new SilkNetDispatcherImpl(() => dispatcher!.Signal());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        long? firedAt = null;
        dispatcher.Signaled += () => { };
        dispatcher.Timer += () =>
        {
            firedAt = dispatcher.Now;
            cancellation.Cancel();
        };

        var dueTime = dispatcher.Now + 25;
        dispatcher.UpdateTimer(dueTime);
        dispatcher.Signal();
        dispatcher.RunLoop(cancellation.Token);

        Assert.NotNull(firedAt);
        Assert.True(firedAt >= dueTime);
        Assert.True(firedAt - dueTime < 250);
    }
}
