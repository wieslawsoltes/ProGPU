using System.Numerics;
using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Xunit;

namespace ProGPU.Tests;

public sealed class PointerPredictorTests
{
    [Fact]
    public void DefaultPredictionRequiresTenSamplesAndFollowsCadence()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            InputPointerSource source =
                InputPointerSource.GetForIsland(
                    island);
            using PointerPredictor predictor =
                PointerPredictor
                    .CreateForInputPointerSource(
                        source);

            Assert.Equal(
                TimeSpan.FromMilliseconds(15),
                predictor.PredictionTime);

            for (int index = 0;
                 index < 9;
                 index++)
            {
                Assert.Empty(
                    predictor.GetPredictedPoints(
                        CreatePoint(
                            pointerId: 7,
                            timestamp:
                                (ulong)index *
                                1_000,
                            x: index * 2,
                            y: index * 3,
                            pressure:
                                0.2f +
                                index * 0.01f,
                            xTilt: index,
                            yTilt: -index)));
            }

            PointerPoint current =
                CreatePoint(
                    pointerId: 7,
                    timestamp: 9_000,
                    x: 18,
                    y: 27,
                    pressure: 0.29f,
                    xTilt: 9,
                    yTilt: -9);
            PointerPoint[] predicted =
                predictor.GetPredictedPoints(
                    current);

            Assert.Equal(15, predicted.Length);
            PointerPoint first = predicted[0];
            Assert.Equal(10_000ul, first.Timestamp);
            Assert.Equal(20d, first.Position.X, 4);
            Assert.Equal(30d, first.Position.Y, 4);
            Assert.Equal(
                0.3f,
                first.Properties.Pressure,
                4);
            Assert.Equal(
                10f,
                first.Properties.XTilt,
                4);
            Assert.Equal(
                -10f,
                first.Properties.YTilt,
                4);
            Assert.Equal(
                current.PointerId,
                first.PointerId);
            Assert.Equal(
                current.FrameId,
                first.FrameId);
            Assert.Equal(
                current.PointerDeviceType,
                first.PointerDeviceType);
            Assert.Same(
                current.PointerDevice,
                first.PointerDevice);
            Assert.Equal(
                current.IsInContact,
                first.IsInContact);
            Assert.Equal(
                current.Properties.ContactRect,
                first.Properties.ContactRect);
            Assert.Equal(
                current.Properties
                    .IsBarrelButtonPressed,
                first.Properties
                    .IsBarrelButtonPressed);
            Assert.Equal(
                current.Properties
                    .PointerUpdateKind,
                first.Properties
                    .PointerUpdateKind);
            Assert.Equal(
                current.Properties
                    .MouseWheelDelta,
                first.Properties
                    .MouseWheelDelta);
        });
    }

    [Fact]
    public void PredictionTimeControlsCountAndCapsOutput()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using PointerPredictor predictor =
                PointerPredictor
                    .CreateForInputPointerSource(
                        InputPointerSource
                            .GetForIsland(island));
            predictor.PredictionTime =
                TimeSpan.FromMilliseconds(3);

            PointerPoint[] predicted =
                FeedLinearHistory(
                    predictor,
                    pointerId: 1);
            Assert.Equal(3, predicted.Length);

            predictor.PredictionTime =
                TimeSpan.FromDays(1);
            predicted =
                predictor.GetPredictedPoints(
                    CreatePoint(
                        1,
                        10_000,
                        10,
                        20));
            Assert.Equal(64, predicted.Length);

            predictor.PredictionTime =
                TimeSpan.Zero;
            Assert.Empty(
                predictor.GetPredictedPoints(
                    CreatePoint(
                        1,
                        11_000,
                        11,
                        22)));
            Assert.Throws<
                ArgumentOutOfRangeException>(
                () =>
                    predictor.PredictionTime =
                        TimeSpan.FromTicks(-1));
        });
    }

    [Fact]
    public void PointerAndTimestampDiscontinuitiesResetHistory()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            using PointerPredictor predictor =
                PointerPredictor
                    .CreateForInputPointerSource(
                        InputPointerSource
                            .GetForIsland(island));
            predictor.PredictionTime =
                TimeSpan.FromMilliseconds(2);
            Assert.Equal(
                2,
                FeedLinearHistory(
                    predictor,
                    pointerId: 1).Length);

            Assert.Empty(
                predictor.GetPredictedPoints(
                    CreatePoint(
                        2,
                        10_000,
                        10,
                        20)));
            for (int index = 1;
                 index < 10;
                 index++)
            {
                PointerPoint[] points =
                    predictor.GetPredictedPoints(
                        CreatePoint(
                            2,
                            10_000 +
                                (ulong)index *
                                1_000,
                            10 + index,
                            20 + index * 2));
                Assert.Equal(
                    index == 9 ? 2 : 0,
                    points.Length);
            }

            Assert.Empty(
                predictor.GetPredictedPoints(
                    CreatePoint(
                        2,
                        5_000,
                        5,
                        10)));
        });
    }

    [Fact]
    public void DisposeIsIdempotentAndRejectsFurtherUse()
    {
        RunOnDispatcherThread(() =>
        {
            using var island =
                new TestContentIsland();
            PointerPredictor predictor =
                PointerPredictor
                    .CreateForInputPointerSource(
                        InputPointerSource
                            .GetForIsland(island));

            predictor.Dispose();
            predictor.Dispose();

            Assert.Throws<
                ObjectDisposedException>(
                () => _ =
                    predictor.PredictionTime);
            Assert.Throws<
                ObjectDisposedException>(
                () =>
                    predictor.PredictionTime =
                        TimeSpan.Zero);
            Assert.Throws<
                ObjectDisposedException>(
                () =>
                    predictor.GetPredictedPoints(
                        CreatePoint(
                            1,
                            0,
                            0,
                            0)));
        });
    }

    [Fact]
    public void RepeatedPrehistorySamplingIsAllocationFree()
    {
        RunOnDispatcherThread(() =>
        {
            const int Count = 100_000;
            using var island =
                new TestContentIsland();
            using PointerPredictor predictor =
                PointerPredictor
                    .CreateForInputPointerSource(
                        InputPointerSource
                            .GetForIsland(island));
            PointerPoint point =
                CreatePoint(
                    1,
                    1_000,
                    1,
                    2);
            _ = predictor.GetPredictedPoints(point);

            _ = GC
                .GetAllocatedBytesForCurrentThread();
            long before = GC
                .GetAllocatedBytesForCurrentThread();
            int resultCount = 0;
            for (int index = 0;
                 index < Count;
                 index++)
            {
                resultCount += predictor
                    .GetPredictedPoints(point)
                    .Length;
            }
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;

            Assert.Equal(0, resultCount);
            Assert.Equal(0, allocated);
        });
    }

    private static PointerPoint[]
        FeedLinearHistory(
            PointerPredictor predictor,
            uint pointerId)
    {
        PointerPoint[] predicted = [];
        for (int index = 0;
             index < 10;
             index++)
        {
            predicted =
                predictor.GetPredictedPoints(
                    CreatePoint(
                        pointerId,
                        (ulong)index * 1_000,
                        index,
                        index * 2));
        }
        return predicted;
    }

    private static PointerPoint CreatePoint(
        uint pointerId,
        ulong timestamp,
        float x,
        float y,
        float pressure = 0.5f,
        float xTilt = 4,
        float yTilt = -3) =>
        new(
            pointerId,
            timestamp,
            new Vector2(x, y),
            new Vector2(x, y),
            Windows.Devices.Input
                .PointerDeviceType.Pen,
            isInContact: true,
            new PointerPointProperties(
                contactRect:
                    new Windows.Foundation.Rect(
                        1,
                        2,
                        3,
                        4),
                isBarrelButtonPressed: true,
                isPrimary: true,
                pointerUpdateKind:
                    PointerUpdateKind
                        .LeftButtonPressed,
                pressure: pressure,
                xTilt: xTilt,
                yTilt: yTilt,
                mouseWheelDelta: 120));

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
}
