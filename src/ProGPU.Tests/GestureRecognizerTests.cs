using Microsoft.UI.Input;
using Windows.Devices.Input;
using Windows.Foundation;
using Xunit;
using InputPointerDeviceType = Microsoft.UI.Input.PointerDeviceType;
using NativePointerDeviceType = Windows.Devices.Input.PointerDeviceType;
using SystemPointerModifiers = Windows.System.VirtualKeyModifiers;

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

    [Fact]
    public void PointerPointPropertiesAreImmutableTypedSnapshots()
    {
        var properties = new PointerPointProperties(
            contactRect: new Rect(1, 2, 3, 4),
            isBarrelButtonPressed: true,
            isHorizontalMouseWheel: true,
            isInRange: false,
            isInverted: true,
            isLeftButtonPressed: true,
            isMiddleButtonPressed: true,
            isRightButtonPressed: true,
            isXButton1Pressed: true,
            isXButton2Pressed: true,
            isPrimary: true,
            isCanceled: true,
            isEraser: true,
            orientation: 15f,
            pointerUpdateKind:
                PointerUpdateKind.RightButtonReleased,
            pressure: 0.75f,
            touchConfidence: false,
            twist: 30f,
            xTilt: 10f,
            yTilt: 20f,
            mouseWheelDelta: 120);

        Assert.All(
            typeof(PointerPointProperties)
                .GetProperties(),
            static property =>
                Assert.Null(property.SetMethod));
        Assert.Equal(
            new Rect(1, 2, 3, 4),
            properties.ContactRect);
        Assert.True(
            properties.IsBarrelButtonPressed);
        Assert.True(
            properties.IsHorizontalMouseWheel);
        Assert.False(properties.IsInRange);
        Assert.True(properties.IsInverted);
        Assert.True(properties.IsLeftButtonPressed);
        Assert.True(properties.IsMiddleButtonPressed);
        Assert.True(properties.IsRightButtonPressed);
        Assert.True(properties.IsXButton1Pressed);
        Assert.True(properties.IsXButton2Pressed);
        Assert.True(properties.IsPrimary);
        Assert.True(properties.IsCanceled);
        Assert.True(properties.IsEraser);
        Assert.Equal(15f, properties.Orientation);
        Assert.Equal(
            PointerUpdateKind.RightButtonReleased,
            properties.PointerUpdateKind);
        Assert.Equal(0.75f, properties.Pressure);
        Assert.False(properties.TouchConfidence);
        Assert.Equal(30f, properties.Twist);
        Assert.Equal(10f, properties.XTilt);
        Assert.Equal(20f, properties.YTilt);
        Assert.Equal(120, properties.MouseWheelDelta);
    }

    [Fact]
    public void PointerPointPropertyReadsAreAllocationFree()
    {
        const int Count = 100_000;
        var properties = new PointerPointProperties(
            contactRect: new Rect(1, 2, 3, 4),
            isPrimary: true,
            pressure: 0.75f,
            mouseWheelDelta: 120);
        _ = ReadPointerProperties(
            properties,
            Count);
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        int checksum = ReadPointerProperties(
            properties,
            Count);
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;

        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PointerEventArgsMetadataMatchesOfficialProjection()
    {
        Type type = typeof(PointerEventArgs);

        Assert.True(type.IsSealed);
        Assert.Equal(typeof(object), type.BaseType);
        Assert.Empty(type.GetConstructors());
        Assert.Equal(
            typeof(PointerPoint),
            type.GetProperty(
                nameof(PointerEventArgs.CurrentPoint))!
                .PropertyType);
        Assert.Null(
            type.GetProperty(
                nameof(PointerEventArgs.CurrentPoint))!
                .SetMethod);
        Assert.Equal(
            typeof(SystemPointerModifiers),
            type.GetProperty(
                nameof(PointerEventArgs.KeyModifiers))!
                .PropertyType);
        Assert.Null(
            type.GetProperty(
                nameof(PointerEventArgs.KeyModifiers))!
                .SetMethod);
        Assert.NotNull(
            type.GetProperty(
                nameof(PointerEventArgs.Handled))!
                .SetMethod);

        var transformedMethod =
            type.GetMethod(
                nameof(PointerEventArgs
                    .GetIntermediateTransformedPoints))!;
        Assert.Equal(
            typeof(IList<PointerPoint>),
            transformedMethod.ReturnType);
        var transformParameter =
            Assert.Single(
                transformedMethod.GetParameters());
        Assert.Equal("transform",
            transformParameter.Name);
        Assert.Equal(
            typeof(IPointerPointTransform),
            transformParameter.ParameterType);
    }

    [Fact]
    public void PointerEventArgsRetainsBoundedChronologicalSnapshot()
    {
        PointerPoint[] history =
            Enumerable.Range(1, 70)
                .Select(index =>
                    Point(
                        (uint)index,
                        index,
                        index * 2,
                        (ulong)index,
                        true))
                .ToArray();
        PointerPoint current =
            Point(71, 71, 142, 71, true);
        var args = new PointerEventArgs(
            current,
            SystemPointerModifiers.Control |
                SystemPointerModifiers.Shift,
            history);

        IList<PointerPoint> points =
            args.GetIntermediatePoints();

        Assert.Same(
            points,
            args.GetIntermediatePoints());
        Assert.Equal(64, points.Count);
        Assert.Equal(8u, points[0].PointerId);
        Assert.Same(current, points[^1]);
        Assert.Same(current, args.CurrentPoint);
        Assert.Equal(
            SystemPointerModifiers.Control |
                SystemPointerModifiers.Shift,
            args.KeyModifiers);
        Assert.False(args.Handled);
        args.Handled = true;
        Assert.True(args.Handled);
        Assert.Throws<NotSupportedException>(
            () => points.Add(current));
    }

    [Fact]
    public void PointerEventArgsTransformsAllPointsOrReturnsEmpty()
    {
        PointerPoint[] history =
        [
            Point(1, 1, 2, 1, true),
            Point(2, 3, 4, 2, true)
        ];
        PointerPoint current =
            Point(3, 5, 6, 3, true);
        var args = new PointerEventArgs(
            current,
            historyBeforeCurrentPoint: history);

        IList<PointerPoint> transformed =
            args.GetIntermediateTransformedPoints(
                new OffsetTransform(10, 20));

        Assert.Equal(3, transformed.Count);
        Assert.Equal(11d,
            transformed[0].Position.X);
        Assert.Equal(22d,
            transformed[0].Position.Y);
        Assert.Equal(15d,
            transformed[^1].Position.X);
        Assert.Equal(26d,
            transformed[^1].Position.Y);
        Assert.Equal(
            history[0].Properties.Pressure,
            transformed[0].Properties.Pressure);
        Assert.Empty(
            args.GetIntermediateTransformedPoints(
                new RejectPointTransform(3)));
        Assert.Throws<ArgumentNullException>(
            () => args
                .GetIntermediateTransformedPoints(
                    null!));
    }

    [Fact]
    public void PointerEventArgsSnapshotReadsAreAllocationFree()
    {
        const int Count = 100_000;
        var args = new PointerEventArgs(
            Point(1, 1, 2, 1, true),
            SystemPointerModifiers.Menu);

        _ = ReadPointerEventArgs(args, Count);
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before =
            GC.GetAllocatedBytesForCurrentThread();
        int checksum =
            ReadPointerEventArgs(args, Count);
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() -
            before;

        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    private static int ReadPointerEventArgs(
        PointerEventArgs args,
        int count)
    {
        int checksum = 0;
        for (int index = 0;
             index < count;
             index++)
        {
            IList<PointerPoint> points =
                args.GetIntermediatePoints();
            checksum ^=
                (int)args.CurrentPoint.PointerId;
            checksum ^= points.Count;
            checksum ^=
                (int)points[^1].PointerId;
            checksum ^=
                (int)args.KeyModifiers;
        }
        return checksum;
    }

    private static int ReadPointerProperties(
        PointerPointProperties properties,
        int count)
    {
        int checksum = 0;
        for (int index = 0;
             index < count;
             index++)
        {
            checksum ^= properties.MouseWheelDelta;
            checksum ^=
                BitConverter.SingleToInt32Bits(
                    properties.Pressure);
            checksum ^=
                properties.IsPrimary ? 1 : 0;
            checksum ^=
                properties.ContactRect.Width
                    .GetHashCode();
        }
        return checksum;
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
            new PointerPointProperties(
                contactRect:
                    new Rect(
                        x - 1,
                        y - 1,
                        2,
                        2),
                isInRange: true,
                isLeftButtonPressed: left,
                isRightButtonPressed: right,
                isPrimary: true,
                mouseWheelDelta: wheel));

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

    private sealed class RejectPointTransform(
        double rejectedX) :
        IPointerPointTransform
    {
        public IPointerPointTransform Inverse =>
            this;

        public bool TryTransform(
            Point inPoint,
            out Point outPoint)
        {
            outPoint = inPoint;
            return inPoint.X != rejectedX;
        }

        public bool TryTransformBounds(
            Rect inRect,
            out Rect outRect)
        {
            outRect = inRect;
            return true;
        }
    }
}
