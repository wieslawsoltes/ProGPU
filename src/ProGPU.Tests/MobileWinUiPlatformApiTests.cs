using Microsoft.UI.Xaml;
using Windows.ApplicationModel;
using Windows.Graphics.Display;
using Windows.UI.Core;
using Xunit;

namespace ProGPU.Tests;

public sealed class MobileWinUiPlatformApiTests
{
    [Fact]
    public async Task ApplicationLifecycleWaitsForOfficialDeferralsAndPreservesEventOrder()
    {
        var application = new Application();
        var events = new List<string>();
        Windows.Foundation.Deferral? enteredDeferral = null;
        SuspendingDeferral? suspendingDeferral = null;
        Windows.Foundation.Deferral? leavingDeferral = null;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(1);

        application.EnteredBackground += (_, args) =>
        {
            events.Add("entered");
            enteredDeferral = args.GetDeferral();
        };
        application.Suspending += (_, args) =>
        {
            events.Add("suspending");
            Assert.Equal(deadline, args.SuspendingOperation.Deadline);
            suspendingDeferral = args.SuspendingOperation.GetDeferral();
        };
        application.Resuming += (_, _) => events.Add("resuming");
        application.LeavingBackground += (_, args) =>
        {
            events.Add("leaving");
            leavingDeferral = args.GetDeferral();
        };

        Task entered = application.NotifyHostEnteredBackgroundAsync();
        Assert.False(entered.IsCompleted);
        enteredDeferral!.Complete();
        enteredDeferral.Complete();
        await entered;

        Task suspending = application.NotifyHostSuspendingAsync(deadline);
        Assert.False(suspending.IsCompleted);
        suspendingDeferral!.Complete();
        suspendingDeferral.Complete();
        await suspending;

        application.NotifyHostResuming();

        Task leaving = application.NotifyHostLeavingBackgroundAsync();
        Assert.False(leaving.IsCompleted);
        leavingDeferral!.Complete();
        await leaving;

        Assert.Equal(["entered", "suspending", "resuming", "leaving"], events);
    }

    [Fact]
    public async Task LifecycleWithoutSubscribersCompletesSynchronously()
    {
        var application = new Application();

        Assert.True(application.NotifyHostEnteredBackgroundAsync().IsCompletedSuccessfully);
        Assert.True(application.NotifyHostLeavingBackgroundAsync().IsCompletedSuccessfully);
        await application.NotifyHostSuspendingAsync(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void SystemNavigationManagerUsesOfficialDefaultsAndReportsHandledBackRequests()
    {
        SystemNavigationManager first = SystemNavigationManager.GetForCurrentView();
        SystemNavigationManager second = SystemNavigationManager.GetForCurrentView();
        Assert.Same(first, second);
        first.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;
        Assert.Equal(AppViewBackButtonVisibility.Collapsed, first.AppViewBackButtonVisibility);

        int requests = 0;
        void Handler(object? sender, BackRequestedEventArgs args)
        {
            Assert.Same(first, sender);
            requests++;
            args.Handled = true;
        }

        first.BackRequested += Handler;
        try
        {
            Assert.True(first.NotifyBackRequested());
            Assert.Equal(1, requests);
        }
        finally
        {
            first.BackRequested -= Handler;
        }

        Assert.False(first.NotifyBackRequested());
    }

    [Fact]
    public void DisplayInformationPublishesAtomicMetricsAndOnlyRelevantEvents()
    {
        DisplayInformation display = DisplayInformation.GetForCurrentView();
        var original = new DisplayInformationMetrics(
            display.CurrentOrientation,
            display.NativeOrientation,
            display.LogicalDpi,
            display.RawPixelsPerViewPixel,
            display.ScreenWidthInRawPixels,
            display.ScreenHeightInRawPixels,
            display.DiagonalSizeInInches);
        int orientationChanges = 0;
        int dpiChanges = 0;
        void OnOrientation(DisplayInformation sender, object args)
        {
            Assert.Same(display, sender);
            orientationChanges++;
        }
        void OnDpi(DisplayInformation sender, object args)
        {
            Assert.Same(display, sender);
            dpiChanges++;
        }

        display.OrientationChanged += OnOrientation;
        display.DpiChanged += OnDpi;
        try
        {
            var portrait = new DisplayInformationMetrics(
                DisplayOrientations.Portrait,
                DisplayOrientations.Portrait,
                264f,
                2.75d,
                1179,
                2556,
                6.1d);

            DisplayInformation.NotifyHostMetricsChanged(portrait);
            DisplayInformation.NotifyHostMetricsChanged(portrait);

            Assert.Equal(DisplayOrientations.Portrait, display.CurrentOrientation);
            Assert.Equal(DisplayOrientations.Portrait, display.NativeOrientation);
            Assert.Equal(264f, display.LogicalDpi);
            Assert.Equal(2.75d, display.RawPixelsPerViewPixel);
            Assert.Equal(ResolutionScale.Scale250Percent, display.ResolutionScale);
            Assert.Equal((uint)1179, display.ScreenWidthInRawPixels);
            Assert.Equal((uint)2556, display.ScreenHeightInRawPixels);
            Assert.Equal(6.1d, display.DiagonalSizeInInches);
            Assert.Equal(1, orientationChanges);
            Assert.Equal(1, dpiChanges);
        }
        finally
        {
            display.OrientationChanged -= OnOrientation;
            display.DpiChanged -= OnDpi;
            DisplayInformation.NotifyHostMetricsChanged(original);
        }
    }

    [Fact]
    public void DisplayInformationRejectsInvalidHostMetricsTransactionally()
    {
        DisplayInformation display = DisplayInformation.GetForCurrentView();
        double oldScale = display.RawPixelsPerViewPixel;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DisplayInformation.NotifyHostMetricsChanged(new DisplayInformationMetrics(
                DisplayOrientations.None,
                DisplayOrientations.Landscape,
                96f,
                1d,
                100,
                100)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DisplayInformation.NotifyHostMetricsChanged(new DisplayInformationMetrics(
                DisplayOrientations.Landscape,
                DisplayOrientations.Landscape,
                96f,
                double.NaN,
                100,
                100)));

        Assert.Equal(oldScale, display.RawPixelsPerViewPixel);
    }
}
