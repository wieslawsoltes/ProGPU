using Microsoft.UI.Input;
using Windows.Devices.Input;
using Windows.Foundation;
using Xunit;
using InputPointerDeviceType = Microsoft.UI.Input.PointerDeviceType;
using NativePointerDeviceType = Windows.Devices.Input.PointerDeviceType;

namespace ProGPU.Tests;

public sealed class GestureRecognizerTests
{
    [Fact]
    public void PublicContractDefaultsAndEnumValuesMatchWindowsAppSdk()
    {
        var recognizer = new GestureRecognizer();

        Assert.True(recognizer.AutoProcessInertia);
        Assert.False(recognizer.CrossSlideExact);
        Assert.False(recognizer.CrossSlideHorizontally);
        Assert.Equal(GestureSettings.None, recognizer.GestureSettings);
        Assert.False(recognizer.IsActive);
        Assert.False(recognizer.IsInertial);
        Assert.False(recognizer.ManipulationExact);
        Assert.True(recognizer.ShowGestureFeedback);
        Assert.Equal(2048u, (uint)GestureSettings.ManipulationScale);
        Assert.Equal(65536u, (uint)GestureSettings.ManipulationMultipleFingerPanning);
        Assert.Equal(3, (int)InputPointerDeviceType.Touchpad);
    }

    [Fact]
    public void ValueMetadataMatchesTheOfficialWinRtProjection()
    {
        Assert.Equal(
            ["_Translation", "_Scale", "_Rotation", "_Expansion"],
            typeof(ManipulationDelta)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(static parameter =>
                    parameter.Name));
        Assert.Equal(
            ["_Linear", "_Angular", "_Expansion"],
            typeof(ManipulationVelocities)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(static parameter =>
                    parameter.Name));
        Assert.Equal(
            [
                "_SelectionStart",
                "_SpeedBumpStart",
                "_SpeedBumpEnd",
                "_RearrangeStart"
            ],
            typeof(CrossSlideThresholds)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(static parameter =>
                    parameter.Name));
        Assert.Equal(
            ["x", "y"],
            typeof(ManipulationDelta)
                .GetMethod("op_Equality")!
                .GetParameters()
                .Select(static parameter =>
                    parameter.Name));
        Assert.Null(
            typeof(GestureRecognizer)
                .GetProperty(
                    nameof(GestureRecognizer.IsInertial))!
                .SetMethod);
    }

    [Fact]
    public void TapAndDoubleTapReportOfficialTapCounts()
    {
        var recognizer = new GestureRecognizer
        {
            GestureSettings = GestureSettings.Tap | GestureSettings.DoubleTap
        };
        var counts = new List<uint>();
        recognizer.Tapped += (_, args) => counts.Add(args.TapCount);

        recognizer.ProcessDownEvent(Point(1, 20, 30, 1_000, true));
        recognizer.ProcessUpEvent(Point(1, 20, 30, 50_000, false));
        recognizer.ProcessDownEvent(Point(2, 22, 30, 180_000, true));
        recognizer.ProcessUpEvent(Point(2, 22, 30, 220_000, false));

        Assert.Equal(new uint[] { 1, 2 }, counts);
        Assert.False(recognizer.IsActive);
    }

    [Fact]
    public void MultiPointerManipulationReportsScaleRotationTranslationAndCumulativeValues()
    {
        var recognizer = new GestureRecognizer
        {
            GestureSettings = GestureSettings.ManipulationTranslateX |
                GestureSettings.ManipulationTranslateY |
                GestureSettings.ManipulationScale |
                GestureSettings.ManipulationRotate,
            ManipulationExact = true
        };
        ManipulationUpdatedEventArgs? updated = null;
        var started = 0;
        var completed = 0;
        recognizer.ManipulationStarted += (_, _) => started++;
        recognizer.ManipulationUpdated += (_, args) => updated = args;
        recognizer.ManipulationCompleted += (_, _) => completed++;

        recognizer.ProcessDownEvent(Point(1, 0, 0, 1_000, true));
        recognizer.ProcessDownEvent(Point(2, 10, 0, 2_000, true));
        recognizer.ProcessMoveEvents([Point(2, 20, 10, 12_000, true)]);

        Assert.Equal(1, started);
        Assert.NotNull(updated);
        Assert.True(updated!.Delta.Scale > 2f);
        Assert.True(updated.Delta.Rotation > 20f);
        Assert.Equal(5f, updated.Delta.Translation.X, 3);
        Assert.Equal(5f, updated.Delta.Translation.Y, 3);
        Assert.Equal(updated.Delta.Scale, updated.Cumulative.Scale, 3);

        recognizer.ProcessUpEvent(Point(1, 0, 0, 20_000, false));
        recognizer.ProcessUpEvent(Point(2, 20, 10, 21_000, false));
        Assert.Equal(1, completed);
    }

    [Fact]
    public void MouseDragAndTouchCrossSlideRaiseCompleteStateSequences()
    {
        var drag = new GestureRecognizer { GestureSettings = GestureSettings.Drag };
        var dragStates = new List<DraggingState>();
        drag.Dragging += (_, args) => dragStates.Add(args.DraggingState);
        drag.ProcessDownEvent(Point(4, 10, 10, 1_000, true, NativePointerDeviceType.Mouse, left: true));
        drag.ProcessMoveEvents([Point(4, 30, 10, 20_000, true, NativePointerDeviceType.Mouse, left: true)]);
        drag.ProcessMoveEvents([Point(4, 40, 10, 30_000, true, NativePointerDeviceType.Mouse, left: true)]);
        drag.ProcessUpEvent(Point(4, 40, 10, 40_000, false, NativePointerDeviceType.Mouse));
        Assert.Equal(new[] { DraggingState.Started, DraggingState.Continuing, DraggingState.Completed }, dragStates);

        var cross = new GestureRecognizer
        {
            GestureSettings = GestureSettings.CrossSlide,
            CrossSlideHorizontally = true,
            CrossSlideExact = true,
            CrossSlideThresholds = new CrossSlideThresholds(5, 10, 20, 30)
        };
        var crossStates = new List<CrossSlidingState>();
        cross.CrossSliding += (_, args) => crossStates.Add(args.CrossSlidingState);
        cross.ProcessDownEvent(Point(5, 0, 0, 1_000, true));
        cross.ProcessMoveEvents([Point(5, 35, 1, 20_000, true)]);
        cross.ProcessUpEvent(Point(5, 35, 1, 30_000, false));
        Assert.Equal(CrossSlidingState.Started, crossStates[0]);
        Assert.Contains(CrossSlidingState.Rearranging, crossStates);
        Assert.Equal(CrossSlidingState.Completed, crossStates[^1]);
    }

    [Fact]
    public void MouseWheelSupportsTranslationAndControlScale()
    {
        var recognizer = new GestureRecognizer
        {
            GestureSettings = GestureSettings.ManipulationTranslateY | GestureSettings.ManipulationScale
        };
        var deltas = new List<ManipulationDelta>();
        recognizer.ManipulationUpdated += (_, args) => deltas.Add(args.Delta);
        var wheel = Point(9, 50, 50, 1_000, false, NativePointerDeviceType.Mouse, wheel: 120);

        recognizer.ProcessMouseWheelEvent(wheel, isShiftKeyDown: false, isControlKeyDown: false);
        recognizer.ProcessMouseWheelEvent(wheel, isShiftKeyDown: false, isControlKeyDown: true);

        Assert.Equal(48f, deltas[0].Translation.Y, 3);
        Assert.Equal(1f, deltas[0].Scale, 3);
        Assert.Equal(1.1f, deltas[1].Scale, 3);
    }

    [Fact]
    public void RightMouseTapAndSinglePointerPivotRotationAreRecognized()
    {
        var rightTap = new GestureRecognizer { GestureSettings = GestureSettings.RightTap };
        RightTappedEventArgs? rightArgs = null;
        rightTap.RightTapped += (_, args) => rightArgs = args;
        rightTap.ProcessDownEvent(Point(10, 5, 5, 1_000, true, NativePointerDeviceType.Mouse, right: true));
        rightTap.ProcessUpEvent(Point(10, 5, 5, 20_000, false, NativePointerDeviceType.Mouse));
        Assert.NotNull(rightArgs);
        Assert.Equal(InputPointerDeviceType.Mouse, rightArgs!.PointerDeviceType);

        var rotate = new GestureRecognizer
        {
            GestureSettings = GestureSettings.ManipulationRotate,
            ManipulationExact = true,
            PivotCenter = new Point(0, 0),
            PivotRadius = 10
        };
        ManipulationUpdatedEventArgs? update = null;
        rotate.ManipulationUpdated += (_, args) => update = args;
        rotate.ProcessDownEvent(Point(11, 10, 0, 1_000, true));
        rotate.ProcessMoveEvents([Point(11, 0, 10, 11_000, true)]);
        Assert.NotNull(update);
        Assert.Equal(90f, update!.Delta.Rotation, 3);
    }

    [Fact]
    public void ManualInertiaRunsUntilCompletion()
    {
        var recognizer = new GestureRecognizer
        {
            AutoProcessInertia = false,
            ManipulationExact = true,
            GestureSettings = GestureSettings.ManipulationTranslateX |
                GestureSettings.ManipulationTranslateInertia,
            InertiaTranslationDeceleration = 0.1f
        };
        var completed = 0;
        recognizer.ManipulationCompleted += (_, _) => completed++;
        recognizer.ProcessDownEvent(Point(8, 0, 0, 1_000, true));
        recognizer.ProcessMoveEvents([Point(8, 20, 0, 11_000, true)]);
        recognizer.ProcessUpEvent(Point(8, 20, 0, 12_000, false));

        Assert.True(recognizer.IsInertial);
        for (var index = 0; index < 20 && recognizer.IsInertial; index++) recognizer.ProcessInertia();
        Assert.False(recognizer.IsInertial);
        Assert.Equal(1, completed);
    }

    [Fact]
    public void FinalUpSampleContributesItsMovementToTheManipulation()
    {
        var recognizer = new GestureRecognizer
        {
            GestureSettings = GestureSettings.ManipulationTranslateX,
            ManipulationExact = true
        };
        ManipulationCompletedEventArgs? completed = null;
        recognizer.ManipulationCompleted += (_, args) => completed = args;

        recognizer.ProcessDownEvent(Point(12, 0, 0, 1_000, true));
        recognizer.ProcessUpEvent(Point(12, 20, 0, 11_000, false));

        Assert.NotNull(completed);
        Assert.Equal(20d, completed!.Cumulative.Translation.X, 3);
    }

    [Fact]
    public void XamlManipulationArgumentsExposeWinUiCompletionAndInertiaContracts()
    {
        var pivot = new Microsoft.UI.Xaml.Input.ManipulationPivot(new Point(12, 18), 24);
        var starting = new Microsoft.UI.Xaml.Input.ManipulationStartingRoutedEventArgs
        {
            Mode = Microsoft.UI.Xaml.Input.ManipulationModes.Scale,
            Pivot = pivot
        };
        var delta = new Microsoft.UI.Xaml.Input.ManipulationDeltaRoutedEventArgs();
        delta.Complete();
        var inertia = new Microsoft.UI.Xaml.Input.ManipulationInertiaStartingRoutedEventArgs();
        inertia.TranslationBehavior.DesiredDeceleration = 0.25;

        Assert.Equal(12d, starting.Pivot!.Center.X);
        Assert.Equal(24d, starting.Pivot.Radius);
        Assert.True(delta.IsCompleteRequested);
        Assert.Equal(0.25, inertia.TranslationBehavior.DesiredDeceleration);
    }

    [Fact]
    public void XamlGestureContractsUseCurrentMicrosoftUiInputTypes()
    {
        Assert.Equal(typeof(Microsoft.UI.Xaml.RoutedEventArgs), typeof(Microsoft.UI.Xaml.Input.TappedRoutedEventArgs).BaseType);
        Assert.Equal(typeof(InputPointerDeviceType),
            typeof(Microsoft.UI.Xaml.Input.TappedRoutedEventArgs).GetProperty("PointerDeviceType")!.PropertyType);
        Assert.Equal(typeof(ManipulationDelta),
            typeof(Microsoft.UI.Xaml.Input.ManipulationDeltaRoutedEventArgs).GetProperty("Delta")!.PropertyType);
        Assert.Equal(typeof(ManipulationVelocities),
            typeof(Microsoft.UI.Xaml.Input.ManipulationDeltaRoutedEventArgs).GetProperty("Velocities")!.PropertyType);
        Assert.Null(typeof(Microsoft.UI.Xaml.Input.ManipulationDeltaRoutedEventArgs).Assembly.GetType(
            "Microsoft.UI.Xaml.Input.ManipulationDelta"));
        Assert.False(typeof(Microsoft.UI.Xaml.Input.ManipulationStartedRoutedEventArgs).IsSealed);
        Assert.Equal(Microsoft.UI.Xaml.Input.ManipulationModes.All,
            new Microsoft.UI.Xaml.Input.ManipulationStartingRoutedEventArgs().Mode);
        Assert.Null(typeof(Microsoft.UI.Xaml.Input.ManipulationInertiaStartingRoutedEventArgs).GetProperty("Position"));
    }

    [Fact]
    public void PointerPointTransformPreservesMetadataAndTransformsContactBounds()
    {
        var point = Point(13, 4, 6, 100, true);
        var transformed = point.GetTransformedPoint(new OffsetTransform(10, 20));

        Assert.NotNull(transformed);
        Assert.Equal(14d, transformed!.Position.X);
        Assert.Equal(26d, transformed.Position.Y);
        Assert.Equal(13d, transformed.Properties.ContactRect.X);
        Assert.Equal(25d, transformed.Properties.ContactRect.Y);
        Assert.Equal(point.PointerId, transformed.PointerId);
        Assert.Equal(point.PointerDeviceType, transformed.PointerDeviceType);
    }

    private static PointerPoint Point(
        uint id,
        float x,
        float y,
        ulong timestamp,
        bool contact,
        NativePointerDeviceType device = NativePointerDeviceType.Touch,
        bool left = false,
        bool right = false,
        int wheel = 0) =>
        new(
            id,
            timestamp,
            new System.Numerics.Vector2(x, y),
            new System.Numerics.Vector2(x, y),
            device,
            contact,
            new PointerPointProperties
            {
                IsPrimary = true,
                IsInRange = true,
                IsLeftButtonPressed = left,
                IsRightButtonPressed = right,
                MouseWheelDelta = wheel,
                ContactRect = new Rect(x - 1, y - 1, 2, 2)
            });

    private sealed class OffsetTransform(double x, double y) : IPointerPointTransform
    {
        public IPointerPointTransform Inverse => new OffsetTransform(-x, -y);

        public bool TryTransform(Point inPoint, out Point outPoint)
        {
            outPoint = new Point(inPoint.X + x, inPoint.Y + y);
            return true;
        }

        public bool TryTransformBounds(Rect inRect, out Rect outRect)
        {
            outRect = new Rect(inRect.X + x, inRect.Y + y, inRect.Width, inRect.Height);
            return true;
        }
    }
}
