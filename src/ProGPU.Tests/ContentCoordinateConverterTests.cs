using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using ProGPU.WinUI.Platform;
using Windows.Foundation;
using Windows.Graphics;
using Xunit;

namespace ProGPU.Tests;

public sealed class ContentCoordinateConverterTests
{
    [Fact]
    public void WindowConverterRequiresTopLevelIdentity()
    {
        Assert.Throws<ArgumentException>(
            () =>
                ContentCoordinateConverter
                    .CreateForWindowId(default));
    }

    [Fact]
    public void ConverterUsesLiveTypedPlatformTransform()
    {
        IContentCoordinatePlatformProvider? previous =
            WindowingPlatformServices.ContentCoordinates;
        try
        {
            var provider =
                new TestCoordinateProvider
                {
                    Transform =
                        Matrix3x2.CreateScale(2f) *
                        Matrix3x2.CreateTranslation(
                            100f,
                            50f)
                };
            WindowingPlatformServices.ContentCoordinates =
                provider;
            ContentCoordinateConverter converter =
                ContentCoordinateConverter
                    .CreateForWindowId(
                        new WindowId(17));

            Assert.Equal(
                new PointInt32(106, 58),
                converter.ConvertLocalToScreen(
                    new Point(3d, 4d)));
            Assert.Equal(
                new Point(3d, 4d),
                converter.ConvertScreenToLocal(
                    new PointInt32(106, 58)));

            provider.Transform =
                Matrix3x2.CreateTranslation(
                    -20f,
                    30f);
            Assert.Equal(
                new PointInt32(-17, 34),
                converter.ConvertLocalToScreen(
                    new Point(3d, 4d)));
        }
        finally
        {
            WindowingPlatformServices.ContentCoordinates =
                previous;
        }
    }

    [Fact]
    public void ProGpuWindowFallbackTracksLivePosition()
    {
        DispatcherQueueController controller =
            DispatcherQueueController
                .CreateOnCurrentThread();
        IContentCoordinatePlatformProvider? previous =
            WindowingPlatformServices.ContentCoordinates;
        AppWindow? window = null;
        try
        {
            WindowingPlatformServices.ContentCoordinates =
                null;
            window = AppWindow.Create();
            window.Move(new PointInt32(40, 60));
            ContentCoordinateConverter converter =
                ContentCoordinateConverter
                    .CreateForWindowId(window.Id);

            Assert.Equal(
                new PointInt32(41, 62),
                converter.ConvertLocalToScreen(
                    new Point(1d, 2d)));

            window.Move(new PointInt32(70, 80));
            Assert.Equal(
                new PointInt32(71, 82),
                converter.ConvertLocalToScreen(
                    new Point(1d, 2d)));
        }
        finally
        {
            window?.Destroy();
            WindowingPlatformServices.ContentCoordinates =
                previous;
            controller.ShutdownQueue();
        }
    }

    [Fact]
    public void ConverterAppliesEveryRoundingMode()
    {
        IContentCoordinatePlatformProvider? previous =
            WindowingPlatformServices.ContentCoordinates;
        try
        {
            WindowingPlatformServices.ContentCoordinates =
                new TestCoordinateProvider();
            ContentCoordinateConverter converter =
                ContentCoordinateConverter
                    .CreateForWindowId(
                        new WindowId(23));
            Point[] points =
            [
                new Point(1.5d, -1.5d)
            ];

            Assert.Equal(
                [new PointInt32(1, -1)],
                converter.ConvertLocalToScreen(
                    points,
                    ContentCoordinateRoundingMode.Auto));
            Assert.Equal(
                [new PointInt32(1, -2)],
                converter.ConvertLocalToScreen(
                    points,
                    ContentCoordinateRoundingMode.Floor));
            Assert.Equal(
                [new PointInt32(2, -2)],
                converter.ConvertLocalToScreen(
                    points,
                    ContentCoordinateRoundingMode.Round));
            Assert.Equal(
                [new PointInt32(2, -1)],
                converter.ConvertLocalToScreen(
                    points,
                    ContentCoordinateRoundingMode.Ceiling));
            Assert.Equal(
                new PointInt32(1, -1),
                converter.ConvertLocalToScreen(
                    points[0]));
        }
        finally
        {
            WindowingPlatformServices.ContentCoordinates =
                previous;
        }
    }

    [Fact]
    public void ArrayConversionsUseOneTransformSnapshot()
    {
        IContentCoordinatePlatformProvider? previous =
            WindowingPlatformServices.ContentCoordinates;
        try
        {
            var provider =
                new TestCoordinateProvider
                {
                    Transform =
                        Matrix3x2.CreateScale(2f)
                };
            WindowingPlatformServices.ContentCoordinates =
                provider;
            ContentCoordinateConverter converter =
                ContentCoordinateConverter
                    .CreateForWindowId(
                        new WindowId(29));

            PointInt32[] screen =
                converter.ConvertLocalToScreen(
                    [
                        new Point(1d, 2d),
                        new Point(-3d, 4d)
                    ]);

            Assert.Equal(
                [
                    new PointInt32(2, 4),
                    new PointInt32(-6, 8)
                ],
                screen);
            Assert.Equal(1, provider.Reads);

            Point[] local =
                converter.ConvertScreenToLocal(
                    screen);
            Assert.Equal(
                [
                    new Point(1d, 2d),
                    new Point(-3d, 4d)
                ],
                local);
            Assert.Equal(2, provider.Reads);
        }
        finally
        {
            WindowingPlatformServices.ContentCoordinates =
                previous;
        }
    }

    [Fact]
    public void RectanglesReturnTransformedAxisAlignedBounds()
    {
        IContentCoordinatePlatformProvider? previous =
            WindowingPlatformServices.ContentCoordinates;
        try
        {
            WindowingPlatformServices.ContentCoordinates =
                new TestCoordinateProvider
                {
                    Transform =
                        new Matrix3x2(
                            0f,
                            1f,
                            -1f,
                            0f,
                            0f,
                            0f)
                };
            ContentCoordinateConverter converter =
                ContentCoordinateConverter
                    .CreateForWindowId(
                        new WindowId(31));

            RectInt32 screen =
                converter.ConvertLocalToScreen(
                    new Rect(0d, 0d, 2d, 3d));
            Assert.Equal(
                new RectInt32(-3, 0, 3, 2),
                screen);

            Rect local =
                converter.ConvertScreenToLocal(
                    new RectInt32(-3, 0, 3, 2));
            Assert.InRange(
                local.X,
                -0.000001d,
                0.000001d);
            Assert.InRange(
                local.Y,
                -0.000001d,
                0.000001d);
            Assert.InRange(
                local.Width,
                1.999999d,
                2.000001d);
            Assert.InRange(
                local.Height,
                2.999999d,
                3.000001d);
        }
        finally
        {
            WindowingPlatformServices.ContentCoordinates =
                previous;
        }
    }

    [Fact]
    public void InvalidInputAndTransformsFailExplicitly()
    {
        IContentCoordinatePlatformProvider? previous =
            WindowingPlatformServices.ContentCoordinates;
        try
        {
            var provider =
                new TestCoordinateProvider();
            WindowingPlatformServices.ContentCoordinates =
                provider;
            ContentCoordinateConverter converter =
                ContentCoordinateConverter
                    .CreateForWindowId(
                        new WindowId(37));

            Assert.Throws<ArgumentNullException>(
                () =>
                    converter.ConvertLocalToScreen(
                        null!));
            Assert.Throws<ArgumentNullException>(
                () =>
                    converter.ConvertScreenToLocal(
                        null!));
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                    converter.ConvertLocalToScreen(
                        [new Point()],
                        (ContentCoordinateRoundingMode)4));
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                    converter.ConvertLocalToScreen(
                        new Rect(
                            0d,
                            0d,
                            -1d,
                            1d)));
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                    converter.ConvertScreenToLocal(
                        new RectInt32(
                            0,
                            0,
                            1,
                            -1)));

            provider.Transform =
                Matrix3x2.CreateScale(0f);
            Assert.Throws<InvalidOperationException>(
                () =>
                    converter.ConvertScreenToLocal(
                        new PointInt32()));

            provider.Transform =
                new Matrix3x2(
                    float.NaN,
                    0f,
                    0f,
                    1f,
                    0f,
                    0f);
            Assert.Throws<InvalidOperationException>(
                () =>
                    converter.ConvertLocalToScreen(
                        new Point()));
        }
        finally
        {
            WindowingPlatformServices.ContentCoordinates =
                previous;
        }
    }

    [Fact]
    public void ScalarConversionsAreAllocationFree()
    {
        const int Count = 100_000;
        IContentCoordinatePlatformProvider? previous =
            WindowingPlatformServices.ContentCoordinates;
        try
        {
            WindowingPlatformServices.ContentCoordinates =
                new TestCoordinateProvider
                {
                    Transform =
                        Matrix3x2.CreateScale(
                            1.5f) *
                        Matrix3x2.CreateTranslation(
                            20f,
                            -10f)
                };
            ContentCoordinateConverter converter =
                ContentCoordinateConverter
                    .CreateForWindowId(
                        new WindowId(41));
            Point local = new(8d, 12d);
            PointInt32 screen =
                converter.ConvertLocalToScreen(
                    local);
            _ = converter.ConvertScreenToLocal(
                screen);
            _ = GC
                .GetAllocatedBytesForCurrentThread();
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            long checksum = 0;
            for (int index = 0;
                 index < Count;
                 index++)
            {
                PointInt32 converted =
                    converter.ConvertLocalToScreen(
                        local);
                Point restored =
                    converter.ConvertScreenToLocal(
                        converted);
                checksum += converted.X;
                checksum += converted.Y;
                checksum += (long)restored.X;
                checksum += (long)restored.Y;
            }
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;

            Assert.Equal(
                60L * Count,
                checksum);
            Assert.Equal(0, allocated);
        }
        finally
        {
            WindowingPlatformServices.ContentCoordinates =
                previous;
        }
    }

    private sealed class TestCoordinateProvider :
        IContentCoordinatePlatformProvider
    {
        public Matrix3x2 Transform { get; set; } =
            Matrix3x2.Identity;

        public int Reads { get; private set; }

        public WindowId LastWindowId { get; private set; }

        public bool TryGetLocalToScreenTransform(
            WindowId windowId,
            out Matrix3x2 localToScreen)
        {
            LastWindowId = windowId;
            Reads++;
            localToScreen = Transform;
            return true;
        }
    }
}
