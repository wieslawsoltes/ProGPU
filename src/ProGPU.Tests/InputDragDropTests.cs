using System.Numerics;
using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Input.DragDrop;
using ProGPU.WinUI.Platform;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Xunit;

namespace ProGPU.Tests;

public sealed class InputDragDropTests
{
    [Fact]
    public void ModifierAndContentModeValuesMatchContract()
    {
        Assert.Equal(
            0u,
            (uint)DragDropModifiers.None);
        Assert.Equal(
            1u,
            (uint)DragDropModifiers.Shift);
        Assert.Equal(
            2u,
            (uint)DragDropModifiers.Control);
        Assert.Equal(
            4u,
            (uint)DragDropModifiers.Alt);
        Assert.Equal(
            8u,
            (uint)DragDropModifiers.LeftButton);
        Assert.Equal(
            16u,
            (uint)DragDropModifiers.MiddleButton);
        Assert.Equal(
            32u,
            (uint)DragDropModifiers.RightButton);
        Assert.Equal(0, (int)DragUIContentMode.Auto);
        Assert.Equal(
            1,
            (int)DragUIContentMode.Deferred);
    }

    [Fact]
    public void ManagerFactoryReturnsNewIslandAssociations()
    {
        RunOnDispatcherThread(() =>
        {
            var island = new TestContentIsland();
            DragDropManager first =
                DragDropManager.GetForIsland(island);
            DragDropManager second =
                DragDropManager.GetForIsland(island);

            Assert.NotSame(first, second);
            first.Dispose();
            first.Dispose();
            island.Dispose();
            Assert.Null(
                DragDropManager.GetForIsland(island));
            second.Dispose();
        });
    }

    [Fact]
    public void OperationWithoutTargetCompletesWithNone()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using DragDropManager manager =
                DragDropManager.GetForIsland(island);
            using var operation =
                new DragOperation();

            DataPackageOperation result =
                operation.StartAsync(
                        manager,
                        Point(7))
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

            Assert.Equal(
                DataPackageOperation.None,
                result);
        });
    }

    [Fact]
    public void HostNotificationsDriveOrderedTargetLifecycle()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using DragDropManager manager =
                DragDropManager.GetForIsland(island);
            var target = new RecordingTarget();
            manager.TargetRequested +=
                (_, args) =>
                    args.SetTarget(target);
            using var operation =
                new DragOperation
                {
                    AllowedOperations =
                        DataPackageOperation.Copy |
                        DataPackageOperation.Move
                };
            operation.Data.SetText("payload");

            Task<DataPackageOperation> completion =
                operation.StartAsync(
                        manager,
                        Point(9))
                    .AsTask();
            Assert.False(completion.IsCompleted);

            DataPackageOperation over =
                DragDropManagerRegistration
                    .NotifyOverAsync(
                        manager,
                        9,
                        new Point(30, 40),
                        DragDropModifiers.Control)
                    .GetAwaiter()
                    .GetResult();
            DragDropManagerRegistration
                .NotifyLeaveAsync(
                    manager,
                    9,
                    new Point(35, 45),
                    DragDropModifiers.Alt)
                .GetAwaiter()
                .GetResult();
            DataPackageOperation dropped =
                DragDropManagerRegistration
                    .NotifyDropAsync(
                        manager,
                        9,
                        new Point(50, 60),
                        DragDropModifiers.Shift)
                    .GetAwaiter()
                    .GetResult();

            Assert.Equal(
                DataPackageOperation.Move,
                over);
            Assert.Equal(
                DataPackageOperation.Copy,
                dropped);
            Assert.Equal(
                dropped,
                completion.GetAwaiter().GetResult());
            Assert.Equal(
                ["enter", "over", "leave", "drop"],
                target.Order);
            Assert.Equal(
                "payload",
                target.LastInfo!.Data
                    .GetTextAsync()
                    .GetAwaiter()
                    .GetResult());
            Assert.Equal(
                new Point(50, 60),
                target.LastInfo.Position);
            Assert.Equal(
                DragDropModifiers.Shift,
                target.LastInfo.Modifiers);
        });
    }

    [Fact]
    public void QueuedNotificationsPreserveCallTimeState()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using DragDropManager manager =
                DragDropManager.GetForIsland(island);
            var target = new DelayedTarget();
            manager.TargetRequested +=
                (_, args) =>
                    args.SetTarget(target);
            using var operation =
                new DragOperation
                {
                    AllowedOperations =
                        DataPackageOperation.Copy
                };

            Task<DataPackageOperation> completion =
                operation.StartAsync(
                        manager,
                        Point(10))
                    .AsTask();
            Task<DataPackageOperation> over =
                DragDropManagerRegistration
                    .NotifyOverAsync(
                        manager,
                        10,
                        new Point(20, 30),
                        DragDropModifiers.Control);
            Task<DataPackageOperation> drop =
                DragDropManagerRegistration
                    .NotifyDropAsync(
                        manager,
                        10,
                        new Point(40, 50),
                        DragDropModifiers.Shift);

            target.ReleaseEnter();

            Assert.Equal(
                DataPackageOperation.Copy,
                over.GetAwaiter().GetResult());
            Assert.Equal(
                DataPackageOperation.Copy,
                drop.GetAwaiter().GetResult());
            Assert.Equal(
                DataPackageOperation.Copy,
                completion.GetAwaiter().GetResult());
            Assert.Equal(
                [
                    new Point(10, 20),
                    new Point(20, 30),
                    new Point(40, 50)
                ],
                target.Positions);
            Assert.Equal(
                [
                    DragDropModifiers.LeftButton,
                    DragDropModifiers.Control,
                    DragDropModifiers.Shift
                ],
                target.Modifiers);
        });
    }

    [Fact]
    public void TargetResultsAreLimitedToAllowedOperations()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using DragDropManager manager =
                DragDropManager.GetForIsland(island);
            var target = new RecordingTarget
            {
                DropResult =
                    DataPackageOperation.Link
            };
            manager.TargetRequested +=
                (_, args) =>
                    args.SetTarget(target);
            using var operation =
                new DragOperation
                {
                    AllowedOperations =
                        DataPackageOperation.Copy
                };

            Task<DataPackageOperation> completion =
                operation.StartAsync(
                        manager,
                        Point(11))
                    .AsTask();
            DataPackageOperation dropped =
                DragDropManagerRegistration
                    .NotifyDropAsync(
                        manager,
                        11,
                        default,
                        DragDropModifiers.None)
                    .GetAwaiter()
                    .GetResult();

            Assert.Equal(
                DataPackageOperation.None,
                dropped);
            Assert.Equal(
                DataPackageOperation.None,
                completion.GetAwaiter().GetResult());
        });
    }

    [Fact]
    public void ConcurrencyPolicyIsEnforcedByPointer()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using DragDropManager manager =
                DragDropManager.GetForIsland(island);
            manager.TargetRequested +=
                (_, args) =>
                    args.SetTarget(
                        new RecordingTarget());
            using var first = new DragOperation();
            using var second = new DragOperation();
            first.AllowedOperations =
                DataPackageOperation.Copy;
            second.AllowedOperations =
                DataPackageOperation.Copy;
            _ = first.StartAsync(manager, Point(13));

            Assert.Throws<InvalidOperationException>(
                () => second.StartAsync(
                    manager,
                    Point(14)));
            Assert.True(
                DragDropManagerRegistration.Cancel(
                    manager,
                    13));
        });
    }

    [Fact]
    public void ConcurrentOperationsUseIndependentPointers()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using DragDropManager manager =
                DragDropManager.GetForIsland(island);
            manager.AreConcurrentOperationsEnabled =
                true;
            manager.TargetRequested +=
                (_, args) =>
                    args.SetTarget(
                        new RecordingTarget());
            using var first = new DragOperation
            {
                AllowedOperations =
                    DataPackageOperation.Copy
            };
            using var second = new DragOperation
            {
                AllowedOperations =
                    DataPackageOperation.Copy
            };

            Task<DataPackageOperation> firstTask =
                first.StartAsync(
                        manager,
                        Point(15))
                    .AsTask();
            Task<DataPackageOperation> secondTask =
                second.StartAsync(
                        manager,
                        Point(16))
                    .AsTask();
            _ = DragDropManagerRegistration
                .NotifyDropAsync(
                    manager,
                    16,
                    default,
                    DragDropModifiers.None)
                .GetAwaiter()
                .GetResult();
            Assert.True(
                DragDropManagerRegistration.Cancel(
                    manager,
                    15));

            Assert.Equal(
                DataPackageOperation.None,
                firstTask.GetAwaiter().GetResult());
            Assert.Equal(
                DataPackageOperation.Copy,
                secondTask.GetAwaiter().GetResult());
        });
    }

    [Fact]
    public void BitmapVisualValidatesAndOwnsAnchor()
    {
        using var bitmap =
            new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                64,
                32,
                BitmapAlphaMode.Premultiplied);
        using var operation =
            new DragOperation();

        operation.SetDragUIContentFromSoftwareBitmap(
            bitmap,
            new Point(12, 8));
        DragUIOverride visual =
            operation.CreateOverride();
        DragDropVisualSnapshot snapshot =
            visual.GetSnapshot();

        Assert.Same(bitmap, snapshot.Bitmap);
        Assert.Equal(
            new Point(12, 8),
            snapshot.AnchorPoint);
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                operation
                    .SetDragUIContentFromSoftwareBitmap(
                        bitmap,
                        new Point(65, 0)));

        visual.Caption = "Copy";
        visual.Clear();
        snapshot = visual.GetSnapshot();
        Assert.Null(snapshot.Bitmap);
        Assert.Equal(string.Empty, visual.Caption);
        Assert.False(visual.IsCaptionVisible);
        Assert.False(visual.IsContentVisible);
        Assert.False(visual.IsGlyphVisible);
        bitmap.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () =>
                operation
                    .SetDragUIContentFromSoftwareBitmap(
                        bitmap));
    }

    [Fact]
    public void IslandClosureCancelsActiveOperation()
    {
        RunOnDispatcherThread(() =>
        {
            var island = new TestContentIsland();
            DragDropManager manager =
                DragDropManager.GetForIsland(island);
            var target = new RecordingTarget();
            manager.TargetRequested +=
                (_, args) =>
                    args.SetTarget(target);
            using var operation =
                new DragOperation
                {
                    AllowedOperations =
                        DataPackageOperation.Copy
                };
            Task<DataPackageOperation> completion =
                operation.StartAsync(
                        manager,
                        Point(19))
                    .AsTask();

            island.Dispose();

            Assert.Equal(
                DataPackageOperation.None,
                completion.GetAwaiter().GetResult());
            Assert.Contains("leave", target.Order);
            Assert.Throws<ObjectDisposedException>(
                () =>
                    DragDropManagerRegistration
                        .NotifyOverAsync(
                            manager,
                            19,
                            default,
                            DragDropModifiers.None)
                        .GetAwaiter()
                        .GetResult());
        });
    }

    [Fact]
    public void FaultedTargetReleasesPointerSession()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using DragDropManager manager =
                DragDropManager.GetForIsland(island);
            manager.TargetRequested +=
                (_, args) =>
                    args.SetTarget(
                        new FaultingTarget());
            using var operation =
                new DragOperation
                {
                    AllowedOperations =
                        DataPackageOperation.Copy
                };

            Task<DataPackageOperation> completion =
                operation.StartAsync(
                        manager,
                        Point(21))
                    .AsTask();

            Assert.Throws<InvalidOperationException>(
                () =>
                    completion
                        .GetAwaiter()
                        .GetResult());
            Assert.True(
                SpinWait.SpinUntil(
                    () =>
                        !DragDropManagerRegistration
                            .TryGetVisual(
                                manager,
                                21,
                                out _),
                    TimeSpan.FromSeconds(1)));
        });
    }

    [Fact]
    public void HostReadsTypedVisualSnapshotWithoutAdapters()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using DragDropManager manager =
                DragDropManager.GetForIsland(island);
            manager.TargetRequested +=
                (_, args) =>
                    args.SetTarget(
                        new RecordingTarget());
            using var bitmap =
                new SoftwareBitmap(
                    BitmapPixelFormat.Bgra8,
                    48,
                    24);
            using var operation =
                new DragOperation
                {
                    AllowedOperations =
                        DataPackageOperation.Copy
                };
            operation.SetDragUIContentFromSoftwareBitmap(
                bitmap,
                new Point(6, 4));
            _ = operation.StartAsync(
                manager,
                Point(23));

            Assert.True(
                DragDropManagerRegistration
                    .TryGetVisual(
                        manager,
                        23,
                        out DragDropVisualSnapshot
                            visual));
            Assert.Same(bitmap, visual.Bitmap);
            Assert.Equal(
                new Point(6, 4),
                visual.AnchorPoint);
            Assert.True(visual.IsContentVisible);
            Assert.True(
                DragDropManagerRegistration.Cancel(
                    manager,
                    23));
        });
    }

    [Fact]
    public void ManagerPropertyReadsAreAllocationFree()
    {
        const int Count = 100_000;
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using DragDropManager manager =
                DragDropManager.GetForIsland(island);
            manager.AreConcurrentOperationsEnabled =
                true;

            _ = manager.AreConcurrentOperationsEnabled;
            _ = GC.GetAllocatedBytesForCurrentThread();
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            int enabled = 0;
            for (int index = 0;
                 index < Count;
                 index++)
            {
                if (manager
                    .AreConcurrentOperationsEnabled)
                {
                    enabled++;
                }
            }
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;

            Assert.Equal(Count, enabled);
            Assert.Equal(0, allocated);
        });
    }

    private static PointerPoint Point(
        uint pointerId) =>
        new(
            pointerId,
            100,
            new Vector2(10, 20),
            new Vector2(10, 20),
            Windows.Devices.Input
                .PointerDeviceType.Mouse,
            true,
            new PointerPointProperties(
                isLeftButtonPressed: true,
                isPrimary: true,
                pressure: 0.5f));

    private static void RunOnDispatcherThread(
        Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            DispatcherQueueController controller =
                DispatcherQueueController
                    .CreateOnCurrentThread();
            try
            {
                action();
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

    private sealed class TestContentIsland :
        ContentIsland
    {
        public TestContentIsland()
            : base(new WinRT.DerivedComposed())
        {
        }
    }

    private sealed class RecordingTarget :
        IDropOperationTarget
    {
        public List<string> Order { get; } = [];

        public DragInfo? LastInfo { get; private set; }

        public DataPackageOperation DropResult
        {
            get;
            set;
        } = DataPackageOperation.Copy;

        public IAsyncOperation<
            DataPackageOperation> EnterAsync(
            DragInfo dragInfo,
            DragUIOverride dragUIOverride)
        {
            LastInfo = dragInfo;
            Order.Add("enter");
            return Operation(
                DataPackageOperation.Copy |
                DataPackageOperation.Move);
        }

        public IAsyncOperation<
            DataPackageOperation> OverAsync(
            DragInfo dragInfo,
            DragUIOverride dragUIOverride)
        {
            LastInfo = dragInfo;
            Order.Add("over");
            return Operation(
                DataPackageOperation.Move);
        }

        public IAsyncAction LeaveAsync(
            DragInfo dragInfo)
        {
            LastInfo = dragInfo;
            Order.Add("leave");
            return new TestAsyncAction(
                Task.CompletedTask);
        }

        public IAsyncOperation<
            DataPackageOperation> DropAsync(
            DragInfo dragInfo)
        {
            LastInfo = dragInfo;
            Order.Add("drop");
            return Operation(DropResult);
        }

        private static IAsyncOperation<
            DataPackageOperation> Operation(
            DataPackageOperation result) =>
            new TestAsyncOperation<
                DataPackageOperation>(
                    Task.FromResult(result));
    }

    private sealed class DelayedTarget :
        IDropOperationTarget
    {
        private readonly TaskCompletionSource
            _enterRelease =
                new(TaskCreationOptions
                    .RunContinuationsAsynchronously);

        public List<Point> Positions { get; } = [];

        public List<DragDropModifiers> Modifiers
        {
            get;
        } = [];

        public void ReleaseEnter() =>
            _enterRelease.TrySetResult();

        public IAsyncOperation<
            DataPackageOperation> EnterAsync(
            DragInfo dragInfo,
            DragUIOverride dragUIOverride)
        {
            Record(dragInfo);
            return new TestAsyncOperation<
                DataPackageOperation>(
                    CompleteEnterAsync());
        }

        public IAsyncOperation<
            DataPackageOperation> OverAsync(
            DragInfo dragInfo,
            DragUIOverride dragUIOverride)
        {
            Record(dragInfo);
            return new TestAsyncOperation<
                DataPackageOperation>(
                    Task.FromResult(
                        DataPackageOperation.Copy));
        }

        public IAsyncAction LeaveAsync(
            DragInfo dragInfo)
        {
            Record(dragInfo);
            return new TestAsyncAction(
                Task.CompletedTask);
        }

        public IAsyncOperation<
            DataPackageOperation> DropAsync(
            DragInfo dragInfo)
        {
            Record(dragInfo);
            return new TestAsyncOperation<
                DataPackageOperation>(
                    Task.FromResult(
                        DataPackageOperation.Copy));
        }

        private async Task<DataPackageOperation>
            CompleteEnterAsync()
        {
            await _enterRelease.Task
                .ConfigureAwait(false);
            return DataPackageOperation.Copy;
        }

        private void Record(
            DragInfo dragInfo)
        {
            Positions.Add(dragInfo.Position);
            Modifiers.Add(dragInfo.Modifiers);
        }
    }

    private sealed class FaultingTarget :
        IDropOperationTarget
    {
        public IAsyncOperation<
            DataPackageOperation> EnterAsync(
            DragInfo dragInfo,
            DragUIOverride dragUIOverride) =>
            new TestAsyncOperation<
                DataPackageOperation>(
                    Task.FromException<
                        DataPackageOperation>(
                        new InvalidOperationException(
                            "Target failed.")));

        public IAsyncOperation<
            DataPackageOperation> OverAsync(
            DragInfo dragInfo,
            DragUIOverride dragUIOverride) =>
            throw new NotSupportedException();

        public IAsyncAction LeaveAsync(
            DragInfo dragInfo) =>
            throw new NotSupportedException();

        public IAsyncOperation<
            DataPackageOperation> DropAsync(
            DragInfo dragInfo) =>
            throw new NotSupportedException();
    }

    private sealed class TestAsyncOperation<TResult>(
        Task<TResult> task) :
        IAsyncOperation<TResult>
    {
        public Task<TResult> AsTask() => task;

        public System.Runtime.CompilerServices
            .TaskAwaiter<TResult> GetAwaiter() =>
            task.GetAwaiter();
    }

    private sealed class TestAsyncAction(
        Task task) :
        IAsyncAction
    {
        public Task AsTask() => task;

        public System.Runtime.CompilerServices
            .TaskAwaiter GetAwaiter() =>
            task.GetAwaiter();
    }
}
