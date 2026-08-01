using Microsoft.UI;
using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using Xunit;

namespace ProGPU.Tests;

public sealed class ContentEnvironmentTests
{
    [Fact]
    public void SiteViewRetainsIdentityAndTracksState()
    {
        var environment =
            new ContentSiteEnvironment();
        ContentSiteEnvironmentView view =
            environment.View;

        environment.AppWindowId =
            new WindowId(17);
        environment.DisplayId =
            new DisplayId(23);
        environment.DisplayScale = 2f;

        Assert.Same(view, environment.View);
        Assert.Equal(
            new WindowId(17),
            environment.AppWindowId);
        Assert.Equal(
            new WindowId(17),
            view.AppWindowId);
        Assert.Equal(
            new DisplayId(23),
            view.DisplayId);
        Assert.Equal(2f, view.DisplayScale);
        Assert.Throws<
            ArgumentOutOfRangeException>(
            () => environment.DisplayScale = 0f);
        Assert.Throws<
            ArgumentOutOfRangeException>(
            () =>
                environment.DisplayScale =
                    float.NaN);
        Assert.Equal(2f, view.DisplayScale);
    }

    [Fact]
    public void PropagationUpdatesImmediatelyAndNotifiesAsync()
    {
        RunOnDispatcherThread(
            controller =>
            {
                var site =
                    new ContentSiteEnvironment();
                var island =
                    new ContentIslandEnvironment(
                        controller.DispatcherQueue);
                site.Attach(island);
                ContentEnvironmentStateChangedEventArgs?
                    observed = null;
                int events = 0;
                island.StateChanged +=
                    (_, args) =>
                    {
                        events++;
                        observed = args;
                    };

                site.AppWindowId =
                    new WindowId(31);
                site.DisplayId =
                    new DisplayId(37);
                site.DisplayScale = 1.5f;
                site.PropagateTo(island);

                Assert.Equal(
                    new WindowId(31),
                    island.AppWindowId);
                Assert.Equal(
                    new DisplayId(37),
                    island.DisplayId);
                Assert.Equal(
                    1.5f,
                    island.DisplayScale);
                Assert.Equal(0, events);

                Drain(controller.DispatcherQueue);

                Assert.Equal(1, events);
                Assert.NotNull(observed);
                Assert.True(
                    observed.DidAppWindowIdChange);
                Assert.True(
                    observed.DidDisplayIdChange);
                Assert.True(
                    observed.DidDisplayScaleChange);

                site.PropagateTo(island);
                Drain(controller.DispatcherQueue);
                Assert.Equal(1, events);
            });
    }

    [Fact]
    public void StateNotificationsCoalesceBeforeDispatch()
    {
        RunOnDispatcherThread(
            controller =>
            {
                var site =
                    new ContentSiteEnvironment();
                var island =
                    new ContentIslandEnvironment(
                        controller.DispatcherQueue);
                int events = 0;
                ContentEnvironmentStateChangedEventArgs?
                    observed = null;
                island.StateChanged +=
                    (_, args) =>
                    {
                        events++;
                        observed = args;
                    };

                site.AppWindowId =
                    new WindowId(41);
                site.PropagateTo(island);
                site.DisplayScale = 2f;
                site.PropagateTo(island);

                Drain(controller.DispatcherQueue);

                Assert.Equal(1, events);
                Assert.NotNull(observed);
                Assert.True(
                    observed.DidAppWindowIdChange);
                Assert.False(
                    observed.DidDisplayIdChange);
                Assert.True(
                    observed.DidDisplayScaleChange);
            });
    }

    [Fact]
    public void SettingNotificationsPreserveOrder()
    {
        RunOnDispatcherThread(
            controller =>
            {
                var site =
                    new ContentSiteEnvironment();
                var island =
                    new ContentIslandEnvironment(
                        controller.DispatcherQueue);
                site.Attach(island);
                var settings = new List<string>();
                island.SettingChanged +=
                    (_, args) =>
                        settings.Add(
                            args.SettingName);

                site.NotifySettingChanged("theme");
                site.NotifySettingChanged("animations");
                Assert.Empty(settings);

                Drain(controller.DispatcherQueue);

                Assert.Equal(
                    ["theme", "animations"],
                    settings);
                site.Detach(island);
                site.NotifySettingChanged("contrast");
                Drain(controller.DispatcherQueue);
                Assert.Equal(2, settings.Count);
            });
    }

    [Fact]
    public void EnvironmentReadsAreAllocationFree()
    {
        const int Count = 100_000;
        RunOnDispatcherThread(
            controller =>
            {
                var site =
                    new ContentSiteEnvironment
                    {
                        AppWindowId =
                            new WindowId(47),
                        DisplayId =
                            new DisplayId(53),
                        DisplayScale = 2f
                    };
                var island =
                    new ContentIslandEnvironment(
                        controller.DispatcherQueue);
                site.PropagateTo(island);
                ContentSiteEnvironmentView view =
                    site.View;

                _ = view.DisplayScale;
                _ = island.DisplayScale;
                _ = GC
                    .GetAllocatedBytesForCurrentThread();
                long before =
                    GC.GetAllocatedBytesForCurrentThread();
                ulong values = 0;
                for (int index = 0;
                     index < Count;
                     index++)
                {
                    values += view.AppWindowId.Value;
                    values += view.DisplayId.Value;
                    values +=
                        (ulong)view.DisplayScale;
                    values += island.AppWindowId.Value;
                    values += island.DisplayId.Value;
                    values +=
                        (ulong)island.DisplayScale;
                }
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() -
                    before;

                Assert.Equal(
                    204UL * Count,
                    values);
                Assert.Equal(0, allocated);
            });
    }

    private static void Drain(
        DispatcherQueue queue)
    {
        Assert.True(
            queue.TryEnqueue(
                queue.EnqueueEventLoopExit));
        queue.RunEventLoop();
    }

    private static void RunOnDispatcherThread(
        Action<DispatcherQueueController> action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            DispatcherQueueController controller =
                DispatcherQueueController
                    .CreateOnCurrentThread();
            try
            {
                action(controller);
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                controller.ShutdownQueue();
            }
        });
        thread.Start();
        thread.Join();
        if (exception is not null)
            throw exception;
    }
}
