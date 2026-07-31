using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using Xunit;

namespace ProGPU.Tests;

public sealed class ContentStateContractTests
{
    [Fact]
    public void SiteCapabilityInterfacesMatchOfficialShape()
    {
        var inputProperties =
            typeof(IContentSiteInput)
                .GetProperties();

        Assert.Equal(
            new[]
            {
                nameof(IContentSiteInput
                    .ProcessesKeyboardInput),
                nameof(IContentSiteInput
                    .ProcessesPointerInput)
            },
            inputProperties
                .Select(property => property.Name)
                .Order());
        Assert.All(
            inputProperties,
            property =>
            {
                Assert.Equal(
                    typeof(bool),
                    property.PropertyType);
                Assert.True(property.CanRead);
                Assert.True(property.CanWrite);
            });

        var automationProperties =
            typeof(IContentSiteAutomation)
                .GetProperties();
        Assert.Equal(
            new[]
            {
                nameof(IContentSiteAutomation
                    .AutomationOption),
                nameof(IContentSiteAutomation
                    .AutomationProvider)
            },
            automationProperties
                .Select(property => property.Name)
                .Order());
        Assert.True(
            Assert.Single(
                automationProperties,
                property =>
                    property.Name ==
                    nameof(IContentSiteAutomation
                        .AutomationOption))
                .CanWrite);
        Assert.False(
            Assert.Single(
                automationProperties,
                property =>
                    property.Name ==
                    nameof(IContentSiteAutomation
                        .AutomationProvider))
                .CanWrite);

        Type handlerType = typeof(
            Windows.Foundation.TypedEventHandler<
                IContentSiteAutomation,
                ContentSiteAutomationProviderRequestedEventArgs>);
        var events =
            typeof(IContentSiteAutomation).GetEvents();
        Assert.Equal(
            new[]
            {
                nameof(IContentSiteAutomation
                    .FragmentRootAutomationProviderRequested),
                nameof(IContentSiteAutomation
                    .NextSiblingAutomationProviderRequested),
                nameof(IContentSiteAutomation
                    .ParentAutomationProviderRequested),
                nameof(IContentSiteAutomation
                    .PreviousSiblingAutomationProviderRequested)
            },
            events
                .Select(@event => @event.Name)
                .Order());
        Assert.All(
            events,
            @event => Assert.Equal(
                handlerType,
                @event.EventHandlerType));
    }

    [Fact]
    public void EnumValuesMatchOfficialContract()
    {
        Assert.Equal(
            0,
            (int)ContentAutomationOptions.None);
        Assert.Equal(
            1,
            (int)ContentAutomationOptions
                .FrameworkBased);
        Assert.Equal(
            2,
            (int)ContentAutomationOptions
                .FragmentBased);
        Assert.Equal(
            0,
            (int)ContentCoordinateRoundingMode.Auto);
        Assert.Equal(
            1,
            (int)ContentCoordinateRoundingMode.Floor);
        Assert.Equal(
            2,
            (int)ContentCoordinateRoundingMode.Round);
        Assert.Equal(
            3,
            (int)ContentCoordinateRoundingMode.Ceiling);
        Assert.Equal(
            0,
            (int)ContentSizePolicy.None);
        Assert.Equal(
            1,
            (int)ContentSizePolicy
                .ResizeContentToParentWindow);
        Assert.Equal(
            2,
            (int)ContentSizePolicy
                .ResizeParentWindowToContent);
        Assert.Equal(0, (int)PopupAnchor.None);
        Assert.Equal(
            1,
            (int)PopupAnchor.TopLevelWindow);
        Assert.Equal(
            2,
            (int)PopupAnchor.ParentIsland);
    }

    [Fact]
    public void EnvironmentChangeSnapshotPreservesFlags()
    {
        var args =
            new ContentEnvironmentStateChangedEventArgs(
                didAppWindowIdChange: true,
                didDisplayIdChange: false,
                didDisplayScaleChange: true);

        Assert.True(args.DidAppWindowIdChange);
        Assert.False(args.DidDisplayIdChange);
        Assert.True(args.DidDisplayScaleChange);
    }

    [Fact]
    public void IslandChangeSnapshotPreservesFlags()
    {
        var args =
            new ContentIslandStateChangedEventArgs(
                didActualSizeChange: true,
                didLayoutDirectionChange: false,
                didLocalToClientTransformMatrixChange:
                    true,
                didLocalToParentTransformMatrixChange:
                    false,
                didRasterizationScaleChange: true,
                didSiteEnabledChange: false,
                didSiteVisibleChange: true);

        Assert.True(args.DidActualSizeChange);
        Assert.False(
            args.DidLayoutDirectionChange);
        Assert.True(
            args
                .DidLocalToClientTransformMatrixChange);
        Assert.False(
            args
                .DidLocalToParentTransformMatrixChange);
        Assert.True(
            args.DidRasterizationScaleChange);
        Assert.False(args.DidSiteEnabledChange);
        Assert.True(args.DidSiteVisibleChange);
    }

    [Fact]
    public void EventDataRetainsValuesAndMutableResponse()
    {
        var setting =
            new ContentEnvironmentSettingChangedEventArgs(
                "animations");
        var islandAutomation =
            new ContentIslandAutomationProviderRequestedEventArgs();
        var siteAutomation =
            new ContentSiteAutomationProviderRequestedEventArgs();
        var requested =
            new ContentSiteRequestedStateChangedEventArgs(
                didRequestedSizeChange: true);
        var islandProvider = new object();
        var siteProvider = new object();

        islandAutomation.AutomationProvider =
            islandProvider;
        islandAutomation.Handled = true;
        siteAutomation.AutomationProvider =
            siteProvider;
        siteAutomation.Handled = true;

        Assert.Equal("animations", setting.SettingName);
        Assert.Same(
            islandProvider,
            islandAutomation.AutomationProvider);
        Assert.True(islandAutomation.Handled);
        Assert.Same(
            siteProvider,
            siteAutomation.AutomationProvider);
        Assert.True(siteAutomation.Handled);
        Assert.True(
            requested.DidRequestedSizeChange);
    }

    [Fact]
    public void DeferralCompletesExactlyOnce()
    {
        RunOnDispatcherThread(
            controller =>
            {
                int completions = 0;
                var deferral =
                    new ContentDeferral(
                        controller.DispatcherQueue,
                        () => completions++);

                deferral.Complete();
                deferral.Complete();

                Assert.Equal(1, completions);
            });
    }

    [Fact]
    public void DeferralEnforcesOwnerThread()
    {
        ContentDeferral? deferral = null;
        int completions = 0;
        RunOnDispatcherThread(
            controller =>
            {
                deferral = new ContentDeferral(
                    controller.DispatcherQueue,
                    () => completions++);
                Exception? completionException =
                    null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        deferral.Complete();
                    }
                    catch (Exception exception)
                    {
                        completionException =
                            exception;
                    }
                });
                thread.Start();
                thread.Join();

                Assert.IsType<
                    InvalidOperationException>(
                    completionException);
                deferral.Complete();
            });

        Assert.Equal(1, completions);
    }

    [Fact]
    public void ChangeSnapshotReadsAreAllocationFree()
    {
        const int Count = 100_000;
        var args =
            new ContentIslandStateChangedEventArgs(
                didActualSizeChange: true,
                didLayoutDirectionChange: false,
                didLocalToClientTransformMatrixChange:
                    true,
                didLocalToParentTransformMatrixChange:
                    false,
                didRasterizationScaleChange: true,
                didSiteEnabledChange: false,
                didSiteVisibleChange: true);

        _ = args.DidActualSizeChange;
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        int changed = 0;
        for (int index = 0;
             index < Count;
             index++)
        {
            if (args.DidActualSizeChange)
                changed++;
            if (args.DidLocalToClientTransformMatrixChange)
                changed++;
            if (args.DidRasterizationScaleChange)
                changed++;
            if (args.DidSiteVisibleChange)
                changed++;
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;

        Assert.Equal(Count * 4, changed);
        Assert.Equal(0, allocated);
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
