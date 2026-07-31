using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public sealed class PointerPointCurrentTests
{
    [Fact]
    public void CurrentPointUsesLatestAppContextSnapshot()
    {
        WindowInputState previous =
            InputSystem.Current;
        WindowInputState state =
            InputSystem.CreateExternalState();

        try
        {
            InputSystem.Current = state;
            Assert.Null(
                PointerPoint.GetCurrentPoint(17));

            InputSystem.InjectPointer(
                CreateInput(
                    PointerInputKind.Moved,
                    17,
                    Windows.Devices.Input
                        .PointerDeviceType.Pen,
                    new Vector2(12.5f, 24.25f),
                    0x1_0000_0005UL,
                    isInContact: true));

            PointerPoint point =
                PointerPoint.GetCurrentPoint(17);

            Assert.NotNull(point);
            Assert.Equal(17u, point.PointerId);
            Assert.Equal(5u, point.FrameId);
            Assert.Equal(
                0x1_0000_0005UL,
                point.Timestamp);
            Assert.Equal(12.5, point.Position.X);
            Assert.Equal(24.25, point.Position.Y);
            Assert.Equal(
                PointerDeviceType.Pen,
                point.PointerDeviceType);
            Assert.True(point.IsInContact);
            Assert.Equal(
                new Windows.Foundation.Rect(
                    11.5,
                    23.25,
                    2,
                    3),
                point.Properties.ContactRect);
            Assert.True(
                point.Properties
                    .IsLeftButtonPressed);
            Assert.True(point.Properties.IsPrimary);
            Assert.Equal(
                0.625f,
                point.Properties.Pressure);
        }
        finally
        {
            InputSystem.Current = previous;
        }
    }

    [Fact]
    public void CurrentPointIsIsolatedByAppContext()
    {
        WindowInputState previous =
            InputSystem.Current;
        WindowInputState first =
            InputSystem.CreateExternalState();
        WindowInputState second =
            InputSystem.CreateExternalState();

        try
        {
            InputSystem.Current = first;
            InputSystem.InjectPointer(
                CreateInput(
                    PointerInputKind.Moved,
                    21,
                    Windows.Devices.Input
                        .PointerDeviceType.Mouse,
                    new Vector2(1, 2),
                    10));

            InputSystem.Current = second;
            Assert.Null(
                PointerPoint.GetCurrentPoint(21));
            InputSystem.InjectPointer(
                CreateInput(
                    PointerInputKind.Moved,
                    21,
                    Windows.Devices.Input
                        .PointerDeviceType.Mouse,
                    new Vector2(30, 40),
                    20));

            Assert.Equal(
                30,
                PointerPoint.GetCurrentPoint(21)
                    .Position.X);

            InputSystem.Current = first;
            Assert.Equal(
                1,
                PointerPoint.GetCurrentPoint(21)
                    .Position.X);
        }
        finally
        {
            InputSystem.Current = previous;
        }
    }

    [Fact]
    public void TerminalTouchInputStopsBeingCurrent()
    {
        WindowInputState previous =
            InputSystem.Current;
        WindowInputState state =
            InputSystem.CreateExternalState();

        try
        {
            InputSystem.Current = state;
            InputSystem.InjectPointer(
                CreateInput(
                    PointerInputKind.Pressed,
                    31,
                    Windows.Devices.Input
                        .PointerDeviceType.Touch,
                    new Vector2(4, 5),
                    10,
                    isInContact: true));
            Assert.NotNull(
                PointerPoint.GetCurrentPoint(31));

            InputSystem.InjectPointer(
                CreateInput(
                    PointerInputKind.Released,
                    31,
                    Windows.Devices.Input
                        .PointerDeviceType.Touch,
                    new Vector2(6, 7),
                    20));

            Assert.Null(
                PointerPoint.GetCurrentPoint(31));
        }
        finally
        {
            InputSystem.Current = previous;
        }
    }

    [Fact]
    public void RetainedMouseAndPenSlotsReplaceStaleIds()
    {
        WindowInputState previous =
            InputSystem.Current;
        WindowInputState state =
            InputSystem.CreateExternalState();

        try
        {
            InputSystem.Current = state;
            InputSystem.InjectPointer(
                CreateInput(
                    PointerInputKind.Moved,
                    41,
                    Windows.Devices.Input
                        .PointerDeviceType.Mouse,
                    Vector2.Zero,
                    10));
            InputSystem.InjectPointer(
                CreateInput(
                    PointerInputKind.Moved,
                    42,
                    Windows.Devices.Input
                        .PointerDeviceType.Mouse,
                    Vector2.One,
                    20));
            InputSystem.InjectPointer(
                CreateInput(
                    PointerInputKind.Moved,
                    51,
                    Windows.Devices.Input
                        .PointerDeviceType.Pen,
                    Vector2.Zero,
                    30));
            InputSystem.InjectPointer(
                CreateInput(
                    PointerInputKind.Moved,
                    52,
                    Windows.Devices.Input
                        .PointerDeviceType.Pen,
                    Vector2.One,
                    40));

            Assert.Null(
                PointerPoint.GetCurrentPoint(41));
            Assert.NotNull(
                PointerPoint.GetCurrentPoint(42));
            Assert.Null(
                PointerPoint.GetCurrentPoint(51));
            Assert.NotNull(
                PointerPoint.GetCurrentPoint(52));
        }
        finally
        {
            InputSystem.Current = previous;
        }
    }

    [Fact]
    public void CurrentPointerTrackingIsAllocationFreeAfterWarmup()
    {
        const int Count = 100_000;
        WindowInputState previous =
            InputSystem.Current;
        WindowInputState state =
            InputSystem.CreateExternalState();
        PointerInputEvent input =
            CreateInput(
                PointerInputKind.Moved,
                61,
                Windows.Devices.Input
                    .PointerDeviceType.Mouse,
                new Vector2(8, 9),
                10);

        try
        {
            InputSystem.Current = state;
            for (int index = 0;
                 index < 1_000;
                 index++)
            {
                InputSystem.TrackCurrentPointer(
                    input);
            }

            _ = GC.GetAllocatedBytesForCurrentThread();
            long before =
                GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0;
                 index < Count;
                 index++)
            {
                InputSystem.TrackCurrentPointer(
                    input);
            }
            long allocated =
                GC.GetAllocatedBytesForCurrentThread() -
                before;

            Assert.Equal(0, allocated);
        }
        finally
        {
            InputSystem.Current = previous;
        }
    }

    private static PointerInputEvent CreateInput(
        PointerInputKind kind,
        uint pointerId,
        Windows.Devices.Input.PointerDeviceType
            deviceType,
        Vector2 position,
        ulong timestamp,
        bool isInContact = false) =>
        new(
            kind,
            pointerId,
            deviceType,
            position,
            timestamp,
            IsPrimary: true,
            IsInContact: isInContact,
            IsLeftButtonPressed: isInContact,
            Pressure:
                isInContact ? 0.625f : 0f,
            ContactRect:
                new Rect(
                    position.X - 1,
                    position.Y - 1,
                    2,
                    3));
}
