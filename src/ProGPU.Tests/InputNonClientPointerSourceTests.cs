using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using ProGPU.WinUI.Platform;
using Windows.Foundation;
using Windows.Graphics;
using Xunit;

namespace ProGPU.Tests;

public sealed class InputNonClientPointerSourceTests
{
    [Fact]
    public void SourceIsStableDispatcherOwnedAndDetachesWithWindow()
    {
        DispatcherQueueController controller =
            DispatcherQueueController
                .CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create();
            InputNonClientPointerSource source =
                InputNonClientPointerSource
                    .GetForWindowId(window.Id);

            Assert.Same(
                source,
                InputNonClientPointerSource
                    .GetForWindowId(window.Id));
            Assert.Same(
                window.DispatcherQueue,
                source.DispatcherQueue);
            Assert.Null(
                InputNonClientPointerSource
                    .GetForWindowId(default));

            InputNonClientPointerSource?
                crossThreadSource = source;
            var thread = new Thread(() =>
            {
                crossThreadSource =
                    InputNonClientPointerSource
                        .GetForWindowId(window.Id);
            });
            thread.Start();
            thread.Join();
            Assert.Null(crossThreadSource);

            WindowId windowId = window.Id;
            window.Destroy();
            window = null;

            Assert.Null(
                InputNonClientPointerSource
                    .GetForWindowId(windowId));
            Assert.False(
                InputNonClientPointerSourceRegistration
                    .NotifyPointerMoved(
                        windowId,
                        NonClientRegionKind.Caption,
                        PointerDeviceType.Mouse,
                        true,
                        new Point(1, 2)));
            Assert.Throws<ObjectDisposedException>(
                () => source.GetRegionRects(
                    NonClientRegionKind.Caption));
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void RegionRectsAreOwnedSnapshotsWithPreciseChangeBatches()
    {
        DispatcherQueueController controller =
            DispatcherQueueController
                .CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create();
            InputNonClientPointerSource source =
                InputNonClientPointerSource
                    .GetForWindowId(window.Id);
            var changed =
                new List<NonClientRegionKind[]>();
            NonClientRegionsChangedEventArgs?
                firstChangedArgs = null;
            source.RegionsChanged += (_, args) =>
            {
                firstChangedArgs ??= args;
                changed.Add(args.ChangedRegions);
            };

            RectInt32[] captionRects =
            [
                new(10, 20, 100, 40),
                new(120, 20, 80, 40)
            ];
            source.SetRegionRects(
                NonClientRegionKind.Caption,
                captionRects);
            Assert.NotNull(firstChangedArgs);
            NonClientRegionKind[] changedSnapshot =
                firstChangedArgs.ChangedRegions;
            changedSnapshot[0] =
                NonClientRegionKind.Close;
            Assert.Equal(
                NonClientRegionKind.Caption,
                firstChangedArgs.ChangedRegions[0]);
            captionRects[0] =
                new RectInt32(0, 0, 1, 1);

            RectInt32[] firstRead =
                source.GetRegionRects(
                    NonClientRegionKind.Caption);
            Assert.Equal(
                new RectInt32(10, 20, 100, 40),
                firstRead[0]);
            firstRead[0] =
                new RectInt32(0, 0, 2, 2);
            Assert.Equal(
                new RectInt32(10, 20, 100, 40),
                source.GetRegionRects(
                    NonClientRegionKind.Caption)[0]);

            source.SetRegionRects(
                NonClientRegionKind.Caption,
                source.GetRegionRects(
                    NonClientRegionKind.Caption));
            Assert.Single(changed);

            source.SetRegionRects(
                NonClientRegionKind.Close,
                [new RectInt32(200, 0, 40, 40)]);
            source.ClearAllRegionRects();

            Assert.Equal(3, changed.Count);
            Assert.Equal(
                [NonClientRegionKind.Caption],
                changed[0]);
            Assert.Equal(
                [NonClientRegionKind.Close],
                changed[1]);
            Assert.Equal(
                [
                    NonClientRegionKind.Close,
                    NonClientRegionKind.Caption
                ],
                changed[2]);
            Assert.Empty(
                source.GetRegionRects(
                    NonClientRegionKind.Caption));
            Assert.Empty(
                source.GetRegionRects(
                    NonClientRegionKind.Close));

            source.ClearAllRegionRects();
            source.ClearRegionRects(
                NonClientRegionKind.Caption);
            Assert.Equal(3, changed.Count);
            Assert.Throws<ArgumentNullException>(
                () => source.SetRegionRects(
                    NonClientRegionKind.Caption,
                    null!));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => source.GetRegionRects(
                    (NonClientRegionKind)10));
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void PointerAndCaptionNotificationsPreserveNativeState()
    {
        DispatcherQueueController controller =
            DispatcherQueueController
                .CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create();
            InputNonClientPointerSource source =
                InputNonClientPointerSource
                    .GetForWindowId(window.Id);
            var order = new List<string>();
            NonClientPointerEventArgs? pointerArgs =
                null;
            NonClientCaptionTappedEventArgs?
                captionArgs = null;
            source.PointerEntered += (_, args) =>
            {
                order.Add("entered");
                pointerArgs = args;
            };
            source.PointerPressed += (_, _) =>
                order.Add("pressed");
            source.PointerMoved += (_, _) =>
                order.Add("moved");
            source.PointerReleased += (_, _) =>
                order.Add("released");
            source.PointerExited += (_, _) =>
                order.Add("exited");
            source.CaptionTapped += (_, args) =>
            {
                order.Add("caption");
                captionArgs = args;
            };

            WindowId id = window.Id;
            var point = new Point(24.5, 12.25);
            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyPointerEntered(
                        id,
                        NonClientRegionKind.Caption,
                        PointerDeviceType.Touchpad,
                        true,
                        point));
            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyPointerPressed(
                        id,
                        NonClientRegionKind.Caption,
                        PointerDeviceType.Touchpad,
                        true,
                        point));
            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyPointerMoved(
                        id,
                        NonClientRegionKind.Caption,
                        PointerDeviceType.Touchpad,
                        false,
                        point));
            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyPointerReleased(
                        id,
                        NonClientRegionKind.Caption,
                        PointerDeviceType.Touchpad,
                        false,
                        point));
            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyPointerExited(
                        id,
                        NonClientRegionKind.Caption,
                        PointerDeviceType.Touchpad,
                        false,
                        point));
            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyCaptionTapped(
                        id,
                        PointerDeviceType.Mouse,
                        point));

            Assert.Equal(
                [
                    "entered",
                    "pressed",
                    "moved",
                    "released",
                    "exited",
                    "caption"
                ],
                order);
            Assert.NotNull(pointerArgs);
            Assert.Equal(
                NonClientRegionKind.Caption,
                pointerArgs.RegionKind);
            Assert.Equal(
                PointerDeviceType.Touchpad,
                pointerArgs.PointerDeviceType);
            Assert.True(pointerArgs.IsPointInRegion);
            Assert.Equal(point, pointerArgs.Point);
            Assert.NotNull(captionArgs);
            Assert.Equal(
                PointerDeviceType.Mouse,
                captionArgs.PointerDeviceType);
            Assert.Equal(point, captionArgs.Point);
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void MoveSizeNotificationsReturnHandlerDecisions()
    {
        DispatcherQueueController controller =
            DispatcherQueueController
                .CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create();
            InputNonClientPointerSource source =
                InputNonClientPointerSource
                    .GetForWindowId(window.Id);
            var pointer = new PointInt32(300, 200);
            var oldRect =
                new RectInt32(100, 100, 640, 480);
            var proposedRect =
                new RectInt32(110, 120, 650, 490);
            var replacementWindowId =
                new WindowId(9_999);
            var replacementRect =
                new RectInt32(90, 80, 700, 520);
            var order = new List<string>();

            Assert.False(
                InputNonClientPointerSourceRegistration
                    .NotifyEnteringMoveSize(
                        window.Id,
                        MoveSizeOperation.Move,
                        pointer,
                        out WindowId defaultMoveWindowId));
            Assert.Equal(
                window.Id,
                defaultMoveWindowId);
            RectInt32 defaultRect = proposedRect;
            bool defaultShowWindow = false;
            Assert.False(
                InputNonClientPointerSourceRegistration
                    .NotifyWindowRectChanging(
                        window.Id,
                        MoveSizeOperation.Move,
                        pointer,
                        oldRect,
                        ref defaultRect,
                        out bool defaultAllowRectChange,
                        ref defaultShowWindow));
            Assert.True(defaultAllowRectChange);
            Assert.False(defaultShowWindow);
            Assert.Equal(proposedRect, defaultRect);

            source.EnteringMoveSize += (_, args) =>
            {
                order.Add("entering");
                Assert.Equal(
                    MoveSizeOperation.Move,
                    args.MoveSizeOperation);
                Assert.Equal(pointer, args.PointerScreenPoint);
                Assert.Equal(
                    window.Id,
                    args.MoveSizeWindowId);
                args.MoveSizeWindowId =
                    replacementWindowId;
            };
            source.EnteredMoveSize += (_, args) =>
            {
                order.Add("entered");
                Assert.Equal(
                    MoveSizeOperation.Move,
                    args.MoveSizeOperation);
            };
            source.WindowRectChanging += (_, args) =>
            {
                order.Add("changing");
                Assert.True(args.AllowRectChange);
                Assert.True(args.ShowWindow);
                Assert.Equal(oldRect, args.OldWindowRect);
                Assert.Equal(
                    proposedRect,
                    args.NewWindowRect);
                args.NewWindowRect =
                    replacementRect;
                args.AllowRectChange = false;
                args.ShowWindow = false;
            };
            source.WindowRectChanged += (_, args) =>
            {
                order.Add("changed");
                Assert.Equal(oldRect, args.OldWindowRect);
                Assert.Equal(
                    replacementRect,
                    args.NewWindowRect);
            };
            source.ExitedMoveSize += (_, args) =>
            {
                order.Add("exited");
                Assert.Equal(pointer, args.PointerScreenPoint);
            };

            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyEnteringMoveSize(
                        window.Id,
                        MoveSizeOperation.Move,
                        pointer,
                        out WindowId moveWindowId));
            Assert.Equal(
                replacementWindowId,
                moveWindowId);
            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyEnteredMoveSize(
                        window.Id,
                        MoveSizeOperation.Move,
                        pointer));

            bool showWindow = true;
            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyWindowRectChanging(
                        window.Id,
                        MoveSizeOperation.Move,
                        pointer,
                        oldRect,
                        ref proposedRect,
                        out bool allowRectChange,
                        ref showWindow));
            Assert.Equal(
                replacementRect,
                proposedRect);
            Assert.False(allowRectChange);
            Assert.False(showWindow);
            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyWindowRectChanged(
                        window.Id,
                        MoveSizeOperation.Move,
                        pointer,
                        oldRect,
                        replacementRect));
            Assert.True(
                InputNonClientPointerSourceRegistration
                    .NotifyExitedMoveSize(
                        window.Id,
                        MoveSizeOperation.Move,
                        pointer));

            Assert.Equal(
                [
                    "entering",
                    "entered",
                    "changing",
                    "changed",
                    "exited"
                ],
                order);
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void SubscriberFreeDispatchAndRegionHitTestingAllocateNothing()
    {
        const int Count = 100_000;
        DispatcherQueueController controller =
            DispatcherQueueController
                .CreateOnCurrentThread();
        AppWindow? window = null;
        try
        {
            window = AppWindow.Create();
            InputNonClientPointerSource source =
                InputNonClientPointerSource
                    .GetForWindowId(window.Id);
            source.SetRegionRects(
                NonClientRegionKind.Caption,
                [new RectInt32(0, 0, 400, 48)]);
            WindowId id = window.Id;
            var point = new Point(100, 20);

            _ = InputNonClientPointerSourceRegistration
                .NotifyPointerMoved(
                    id,
                    NonClientRegionKind.Caption,
                    PointerDeviceType.Mouse,
                    true,
                    point);
            _ = InputNonClientPointerSourceRegistration
                .IsPointInRegion(
                    id,
                    NonClientRegionKind.Caption,
                    point);
            _ = GC.GetAllocatedBytesForCurrentThread();
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            int hits = 0;
            for (int index = 0;
                 index < Count;
                 index++)
            {
                if (InputNonClientPointerSourceRegistration
                    .NotifyPointerMoved(
                        id,
                        NonClientRegionKind.Caption,
                        PointerDeviceType.Mouse,
                        true,
                        point))
                {
                    hits++;
                }

                if (InputNonClientPointerSourceRegistration
                    .IsPointInRegion(
                        id,
                        NonClientRegionKind.Caption,
                        point))
                {
                    hits++;
                }
            }

            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;
            Assert.Equal(Count, hits);
            Assert.Equal(0, allocated);
        }
        finally
        {
            window?.Destroy();
            controller.ShutdownQueue();
        }
    }
}
