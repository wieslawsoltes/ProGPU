using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using ProGPU.WinUI.Platform;
using Windows.Foundation;
using Windows.Graphics;
using Xunit;

namespace ProGPU.Tests;

public sealed class ContentSiteTests
{
    [Fact]
    public void LiveViewRetainsIdentityAndTracksAllState()
    {
        RunOnDispatcherThread(
            controller =>
            {
                var site = new ContentSite(
                    controller.DispatcherQueue);
                ContentSiteView view = site.View;
                var parentTransform =
                    Matrix4x4.CreateTranslation(
                        7f,
                        11f,
                        0f);
                var clientTransform =
                    Matrix4x4.CreateTranslation(
                        13f,
                        17f,
                        0f);

                Assert.Same(view, site.View);
                Assert.Same(
                    site.CoordinateConverter,
                    view.CoordinateConverter);
                Assert.Same(
                    site.Environment.View,
                    view.EnvironmentView);
                Assert.Same(
                    controller.DispatcherQueue,
                    view.DispatcherQueue);
                Assert.True(site.IsSiteEnabled);
                Assert.True(site.IsSiteVisible);
                Assert.True(
                    site.ProcessesKeyboardInput);
                Assert.True(
                    site.ProcessesPointerInput);
                Assert.True(
                    site.ShouldApplyRasterizationScale);
                Assert.Equal(
                    ContentLayoutDirection.LeftToRight,
                    view.LayoutDirection);
                Assert.Equal(1f, view.RasterizationScale);

                site.ActualSize =
                    new Vector2(320f, 180f);
                site.ClientSize =
                    new SizeInt32(640, 360);
                site.IsSiteEnabled = false;
                site.IsSiteVisible = false;
                site.LayoutDirection =
                    ContentLayoutDirection.RightToLeft;
                site.LocalToParentTransformMatrix =
                    parentTransform;
                site.OverrideScale = 1.5f;
                site.ParentScale = 2f;
                site.ProcessesKeyboardInput = false;
                site.ProcessesPointerInput = false;
                site.ShouldApplyRasterizationScale =
                    false;
                site.SetAutomationOption(
                    ContentAutomationOptions
                        .FragmentBased);
                site.SetLocalToClientTransformMatrix(
                    clientTransform);
                site.SetConnected(true);
                site.SetRequestedSize(
                    new Vector2(400f, 225f));

                Assert.Equal(
                    new Vector2(320f, 180f),
                    view.ActualSize);
                Assert.Equal(
                    new SizeInt32(640, 360),
                    view.ClientSize);
                Assert.False(view.IsSiteEnabled);
                Assert.False(view.IsSiteVisible);
                Assert.True(view.IsConnected);
                Assert.Equal(
                    ContentLayoutDirection.RightToLeft,
                    view.LayoutDirection);
                Assert.Equal(
                    parentTransform,
                    view.LocalToParentTransformMatrix);
                Assert.Equal(
                    clientTransform,
                    view.LocalToClientTransformMatrix);
                Assert.Equal(1.5f, view.OverrideScale);
                Assert.Equal(2f, view.ParentScale);
                Assert.Equal(1.5f, view.RasterizationScale);
                Assert.False(
                    view.ProcessesKeyboardInput);
                Assert.False(
                    view.ProcessesPointerInput);
                Assert.False(
                    view.ShouldApplyRasterizationScale);
                Assert.Equal(
                    ContentAutomationOptions.FragmentBased,
                    view.AutomationOption);
                Assert.Equal(
                    new Vector2(400f, 225f),
                    view.RequestedSize);

                site.Dispose();
            });
    }

    [Fact]
    public void ScaleSizeAndTransformValidationIsTransactional()
    {
        RunOnDispatcherThread(
            controller =>
            {
                var site = new ContentSite(
                    controller.DispatcherQueue);
                site.ParentScale = 2f;
                Assert.Equal(2f, site.RasterizationScale);
                site.OverrideScale = 1.25f;
                Assert.Equal(
                    1.25f,
                    site.RasterizationScale);
                site.OverrideScale = 0f;
                Assert.Equal(2f, site.RasterizationScale);

                Assert.Throws<
                    ArgumentOutOfRangeException>(
                    () => site.ParentScale = 0f);
                Assert.Throws<
                    ArgumentOutOfRangeException>(
                    () =>
                        site.OverrideScale =
                            float.NaN);
                Assert.Throws<
                    ArgumentOutOfRangeException>(
                    () =>
                        site.ActualSize =
                            new Vector2(-1f, 2f));
                Assert.Throws<
                    ArgumentOutOfRangeException>(
                    () =>
                        site.ClientSize =
                            new SizeInt32(-1, 2));
                Assert.Throws<ArgumentException>(
                    () =>
                    {
                        Matrix4x4 perspective =
                            Matrix4x4.Identity;
                        perspective.M14 = 0.1f;
                        site
                            .SetLocalToClientTransformMatrix(
                                perspective);
                    });
                Assert.Throws<
                    ArgumentOutOfRangeException>(
                    () =>
                    {
                        Matrix4x4 invalid =
                            Matrix4x4.Identity;
                        invalid.M11 = float.NaN;
                        site
                            .LocalToParentTransformMatrix =
                            invalid;
                    });

                Assert.Equal(2f, site.ParentScale);
                Assert.Equal(0f, site.OverrideScale);
                Assert.Equal(Vector2.Zero, site.ActualSize);
                Assert.Equal(
                    default,
                    site.ClientSize);
                Assert.Equal(
                    Matrix4x4.Identity,
                    site.LocalToClientTransformMatrix);
                Assert.Equal(
                    Matrix4x4.Identity,
                    site.LocalToParentTransformMatrix);
                site.Dispose();
            });
    }

    [Fact]
    public void SiteConverterCombinesClientAndWindowTransforms()
    {
        RunOnDispatcherThread(
            controller =>
            {
                IContentCoordinatePlatformProvider?
                    previous =
                        WindowingPlatformServices
                            .ContentCoordinates;
                try
                {
                    WindowingPlatformServices
                        .ContentCoordinates =
                        new TestCoordinateProvider(
                            Matrix3x2
                                .CreateTranslation(
                                    100f,
                                    50f));
                    var site = new ContentSite(
                        controller.DispatcherQueue);
                    site.Environment.AppWindowId =
                        new WindowId(71);
                    site.SetLocalToClientTransformMatrix(
                        Matrix4x4.CreateTranslation(
                            5f,
                            7f,
                            0f));

                    Assert.Equal(
                        new PointInt32(106, 59),
                        site.CoordinateConverter
                            .ConvertLocalToScreen(
                                new Point(1d, 2d)));
                    Assert.Equal(
                        new Point(1d, 2d),
                        site.CoordinateConverter
                            .ConvertScreenToLocal(
                                new PointInt32(
                                    106,
                                    59)));
                    site.Dispose();
                }
                finally
                {
                    WindowingPlatformServices
                        .ContentCoordinates =
                        previous;
                }
            });
    }

    [Fact]
    public void RequestedSizeRaisesOnlyForChanges()
    {
        RunOnDispatcherThread(
            controller =>
            {
                var site = new ContentSite(
                    controller.DispatcherQueue);
                int events = 0;
                ContentSiteRequestedStateChangedEventArgs?
                    observed = null;
                site.RequestedStateChanged +=
                    (_, args) =>
                    {
                        events++;
                        observed = args;
                        Assert.Equal(
                            new Vector2(200f, 120f),
                            site.RequestedSize);
                    };

                site.SetRequestedSize(
                    new Vector2(200f, 120f));
                site.SetRequestedSize(
                    new Vector2(200f, 120f));

                Assert.Equal(1, events);
                Assert.NotNull(observed);
                Assert.True(
                    observed.DidRequestedSizeChange);
                site.Dispose();
            });
    }

    [Fact]
    public void IslandStateDeferralsCoalesceAndCancel()
    {
        RunOnDispatcherThread(
            controller =>
            {
                var site = new ContentSite(
                    controller.DispatcherQueue);
                Assert.Null(
                    site.GetIslandStateChangeDeferral());
                site.SetConnected(true);
                int events = 0;
                ContentSiteChangeFlags changes =
                    ContentSiteChangeFlags.None;
                site.StateChanged +=
                    (_, flags) =>
                    {
                        events++;
                        changes |= flags;
                    };

                ContentDeferral first =
                    site.GetIslandStateChangeDeferral();
                ContentDeferral second =
                    site.GetIslandStateChangeDeferral();
                site.IsSiteEnabled = false;
                site.IsSiteVisible = false;
                Assert.Equal(0, events);
                first.Complete();
                Assert.Equal(0, events);
                second.Complete();

                Assert.Equal(1, events);
                Assert.True(
                    changes.HasFlag(
                        ContentSiteChangeFlags
                            .IsSiteEnabled));
                Assert.True(
                    changes.HasFlag(
                        ContentSiteChangeFlags
                            .IsSiteVisible));

                changes = ContentSiteChangeFlags.None;
                ContentDeferral cancelled =
                    site.GetIslandStateChangeDeferral();
                site.ProcessesPointerInput = false;
                site.SetConnected(false);
                Assert.Equal(2, events);
                Assert.Equal(
                    ContentSiteChangeFlags.IsConnected,
                    changes);
                cancelled.Complete();
                Assert.Equal(2, events);
                Assert.Null(
                    site.GetIslandStateChangeDeferral());
                site.Dispose();
            });
    }

    [Fact]
    public void CloseIsOrderedIdempotentAndTerminal()
    {
        RunOnDispatcherThread(
            controller =>
            {
                var site = new ContentSite(
                    controller.DispatcherQueue);
                site.SetConnected(true);
                var events = new List<string>();
                site.FrameworkClosed +=
                    () => events.Add("framework");
                site.Closed +=
                    () => events.Add("closed");

                site.Dispose();
                site.Dispose();

                Assert.Equal(
                    ["framework", "closed"],
                    events);
                Assert.True(site.IsClosed);
                Assert.False(site.IsConnected);
                Assert.Throws<ObjectDisposedException>(
                    () => site.IsSiteVisible = false);
                Assert.Throws<ObjectDisposedException>(
                    () =>
                        site.SetRequestedSize(
                            new Vector2(1f, 1f)));
                Assert.Null(
                    site.GetIslandStateChangeDeferral());
            });
    }

    [Fact]
    public void LiveViewAndNoOpSettersAreAllocationFree()
    {
        const int Count = 100_000;
        RunOnDispatcherThread(
            controller =>
            {
                var site = new ContentSite(
                    controller.DispatcherQueue);
                ContentSiteView view = site.View;
                site.ActualSize =
                    new Vector2(10f, 20f);
                site.ClientSize =
                    new SizeInt32(30, 40);
                site.ParentScale = 2f;
                site.IsSiteVisible = true;
                _ = view.ActualSize;
                site.IsSiteVisible = true;
                _ = GC
                    .GetAllocatedBytesForCurrentThread();
                long before =
                    GC.GetAllocatedBytesForCurrentThread();
                long checksum = 0;
                for (int index = 0;
                     index < Count;
                     index++)
                {
                    Vector2 actual = view.ActualSize;
                    SizeInt32 client = view.ClientSize;
                    checksum += (long)actual.X;
                    checksum += (long)actual.Y;
                    checksum += client.Width;
                    checksum += client.Height;
                    checksum +=
                        (long)view.RasterizationScale;
                    checksum += view.IsSiteVisible
                        ? 1
                        : 0;
                    site.IsSiteVisible = true;
                }
                long allocated =
                    GC.GetAllocatedBytesForCurrentThread() -
                    before;

                Assert.Equal(
                    103L * Count,
                    checksum);
                Assert.Equal(0, allocated);
                site.Dispose();
            });
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

    private sealed class TestCoordinateProvider :
        IContentCoordinatePlatformProvider
    {
        private readonly Matrix3x2 _transform;

        internal TestCoordinateProvider(
            Matrix3x2 transform)
        {
            _transform = transform;
        }

        public bool TryGetLocalToScreenTransform(
            WindowId windowId,
            out Matrix3x2 localToScreen)
        {
            Assert.Equal(
                new WindowId(71),
                windowId);
            localToScreen = _transform;
            return true;
        }
    }
}
