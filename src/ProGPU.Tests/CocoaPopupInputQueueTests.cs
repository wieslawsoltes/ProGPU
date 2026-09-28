using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class CocoaPopupInputQueueTests
{
    [Fact]
    public void CallbackRecordsInputButCannotDispatchIt()
    {
        var queue = Enabled();
        queue.BeginNativeCallback();
        Assert.True(queue.TryWrite(Move(1)));
        Assert.Throws<InvalidOperationException>(() => queue.Read([], out _));
        queue.EndNativeCallback();
        Span<CocoaPopupPointerEvent> output = stackalloc CocoaPopupPointerEvent[1];
        Assert.Equal(1, queue.Read(output, out _));
        Assert.Equal(Move(1), output[0]);
    }

    private static CocoaPopupPointerEvent Move(double x) =>
        new(CocoaPopupPointerKind.Move, x, -12.25, x + 10, -1, 0, CocoaPopupModifiers.Option);

    private static CocoaPopupInputQueue Enabled(int capacity = 256)
    {
        var queue = new CocoaPopupInputQueue(capacity);
        queue.SetEnabled(true);
        queue.SetVisible(true);
        return queue;
    }

    [Fact]
    public void CreationIsInputClosedUntilBothVisibilityAndNativeAdmission()
    {
        var queue = new CocoaPopupInputQueue();
        Assert.False(queue.TryWrite(Move(1)));
        queue.SetVisible(true);
        Assert.False(queue.TryWrite(Move(2)));
        queue.SetEnabled(true);
        Assert.True(queue.TryWrite(Move(3)));
        Span<CocoaPopupPointerEvent> output = stackalloc CocoaPopupPointerEvent[1];
        Assert.Equal(1, queue.Read(output, out _));
        Assert.Equal(Move(3), output[0]);
    }

    [Fact]
    public void WrappedBatchesPreserveEveryEventAndUntouchedCallerTail()
    {
        var queue = Enabled(4);
        var sentinel = Move(99);
        var output = new[] { sentinel, sentinel, sentinel, sentinel, sentinel };
        Assert.True(queue.TryWrite(Move(1)));
        Assert.True(queue.TryWrite(Move(2)));
        Assert.True(queue.TryWrite(Move(3)));
        Assert.Equal(2, queue.Read(output.AsSpan(0, 2), out var generation));
        Assert.Equal(new[] { Move(1), Move(2), sentinel, sentinel, sentinel }, output);
        Assert.True(queue.TryWrite(Move(4)));
        Assert.True(queue.TryWrite(Move(5)));
        Assert.True(queue.TryWrite(Move(6)));
        Assert.Equal(4, queue.Read(output, out var current));
        Assert.Equal(generation, current);
        Assert.Equal(new[] { Move(3), Move(4), Move(5), Move(6), sentinel }, output);
        Assert.Equal(0, queue.Read(output, out _));
    }

    [Fact]
    public void MotionIsNotCoalescedAcrossOrWithinButtonAndCaptureTransitions()
    {
        var queue = Enabled();
        var events = new[]
        {
            Move(1), Move(2),
            Move(3) with { Kind = CocoaPopupPointerKind.Down, Button = 0, ClickCount = 2 },
            Move(4) with { Kind = CocoaPopupPointerKind.Drag, Button = 0, ClickCount = 2 },
            Move(5) with { Kind = CocoaPopupPointerKind.Up, Button = 0, ClickCount = 2 },
            Move(6) with { Kind = CocoaPopupPointerKind.Cancel }, Move(7)
        };
        foreach (var value in events) Assert.True(queue.TryWrite(value));
        var output = new CocoaPopupPointerEvent[events.Length];
        Assert.Equal(events.Length, queue.Read(output, out _));
        Assert.Equal(events, output);
    }

    [Fact]
    public void ExactCapacityIsUsableButNextEventFaultsBeforeAnyPublication()
    {
        var queue = Enabled();
        for (int index = 0; index < 256; ++index) Assert.True(queue.TryWrite(Move(index)));
        Assert.False(queue.TryWrite(Move(256)));
        var output = new[] { Move(99), Move(99) };
        Assert.Contains(nameof(CocoaPopupInputFailure.CapacityExceeded),
            Assert.Throws<InvalidOperationException>(() => queue.Read(output, out _)).Message);
        Assert.Equal(new[] { Move(99), Move(99) }, output);
        Assert.False(queue.CanReceive);
        queue.SetVisible(false);
        queue.SetEnabled(false);
        Assert.Throws<InvalidOperationException>(() => queue.SetEnabled(true));
        Assert.Throws<InvalidOperationException>(() => queue.SetVisible(true));
    }

    [Fact]
    public void ExactCapacityReadsAllRecordsWithoutFalseOverflow()
    {
        var queue = Enabled();
        for (int index = 0; index < 256; ++index) Assert.True(queue.TryWrite(Move(index)));
        var output = new CocoaPopupPointerEvent[256];
        Assert.Equal(256, queue.Read(output, out _));
        for (int index = 0; index < 256; ++index) Assert.Equal(Move(index), output[index]);
        Assert.True(queue.TryWrite(Move(300)));
    }

    [Fact]
    public void HideAndBlockInvalidatePendingInputAndChangeCaptureGeneration()
    {
        var queue = Enabled();
        Span<CocoaPopupPointerEvent> output = stackalloc CocoaPopupPointerEvent[2];
        Assert.True(queue.TryWrite(Move(1)));
        ulong initial = queue.Generation;
        queue.SetVisible(false);
        Assert.NotEqual(initial, queue.Generation);
        Assert.False(queue.TryWrite(Move(2)));
        Assert.Equal(0, queue.Read(output, out var hidden));
        queue.SetVisible(true);
        Assert.True(queue.TryWrite(Move(3)));
        queue.SetEnabled(false);
        Assert.NotEqual(hidden, queue.Generation);
        Assert.Equal(0, queue.Read(output, out _));
        queue.SetEnabled(true);
        Assert.True(queue.TryWrite(Move(4)));
        Assert.Equal(1, queue.Read(output, out _));
        Assert.Equal(Move(4), output[0]);
    }

    [Fact]
    public void UnchangedPolicyDoesNotDiscardInputOrChangeGeneration()
    {
        var queue = Enabled();
        Assert.True(queue.TryWrite(Move(1)));
        var generation = queue.Generation;
        queue.SetVisible(true);
        queue.SetEnabled(true);
        Span<CocoaPopupPointerEvent> output = stackalloc CocoaPopupPointerEvent[1];
        Assert.Equal(1, queue.Read(output, out var current));
        Assert.Equal(generation, current);
    }

    [Fact]
    public void ClosedQueueCannotBeReenabledOrReadAndLateNativeEventsAreIgnored()
    {
        var queue = Enabled();
        queue.Close();
        ulong generation = queue.Generation;
        queue.Close();
        Assert.Equal(generation, queue.Generation);
        Assert.False(queue.TryWrite(Move(1)));
        Assert.Throws<ObjectDisposedException>(() => queue.SetEnabled(true));
        Assert.Throws<ObjectDisposedException>(() => queue.SetVisible(true));
        Assert.Throws<ObjectDisposedException>(() => queue.Read([], out _));
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 4, 8)]
    public void ScrollRetainsExactPointOrLineUnitsDirectionAndNativePhases(bool precise, uint phase, uint momentum)
    {
        var queue = Enabled();
        var value = Move(1) with { Kind = CocoaPopupPointerKind.Scroll,
            ScrollX = -0.375, ScrollY = 1.25, PreciseScroll = precise,
            ScrollPhase = phase, MomentumPhase = momentum };
        Assert.True(queue.TryWrite(value));
        Span<CocoaPopupPointerEvent> output = stackalloc CocoaPopupPointerEvent[1];
        Assert.Equal(1, queue.Read(output, out _));
        Assert.Equal(value, output[0]);
    }

    [Fact]
    public void InvalidRecordPoisonsEarlierQueuedInputWithoutWritingCallerOutput()
    {
        var queue = Enabled();
        Assert.True(queue.TryWrite(Move(1)));
        Assert.False(queue.TryWrite(Move(2) with { Y = double.NaN }));
        var output = new[] { Move(99) };
        Assert.Contains(nameof(CocoaPopupInputFailure.InvalidEvent),
            Assert.Throws<InvalidOperationException>(() => queue.Read(output, out _)).Message);
        Assert.Equal(Move(99), output[0]);
    }

    [Fact]
    public void NativeCallbackFaultIsStickyAndDoesNotUnwindThroughItsRecorder()
    {
        var queue = Enabled();
        queue.Fail(CocoaPopupInputFailure.NativeCallback);
        queue.Fail(CocoaPopupInputFailure.InvalidEvent);
        Assert.False(queue.TryWrite(Move(1)));
        Assert.Contains(nameof(CocoaPopupInputFailure.NativeCallback),
            Assert.Throws<InvalidOperationException>(() => queue.Read([], out _)).Message);
    }

    [Theory]
    [InlineData(1, CocoaPopupPointerKind.Down)]
    [InlineData(2, CocoaPopupPointerKind.Up)]
    [InlineData(3, CocoaPopupPointerKind.Down)]
    [InlineData(4, CocoaPopupPointerKind.Up)]
    [InlineData(5, CocoaPopupPointerKind.Move)]
    [InlineData(6, CocoaPopupPointerKind.Drag)]
    [InlineData(7, CocoaPopupPointerKind.Drag)]
    [InlineData(8, CocoaPopupPointerKind.Enter)]
    [InlineData(9, CocoaPopupPointerKind.Leave)]
    [InlineData(22, CocoaPopupPointerKind.Scroll)]
    [InlineData(25, CocoaPopupPointerKind.Down)]
    [InlineData(26, CocoaPopupPointerKind.Up)]
    [InlineData(27, CocoaPopupPointerKind.Drag)]
    [InlineData(40, CocoaPopupPointerKind.Cancel)]
    [InlineData(10, CocoaPopupPointerKind.None)]
    [InlineData(0, CocoaPopupPointerKind.None)]
    [InlineData(int.MaxValue, CocoaPopupPointerKind.None)]
    public void NativeEventKindsRemainExplicit(int type, object expected) =>
        Assert.Equal(expected, CocoaPopupInputQueue.Classify((nuint)type));

    [Fact]
    public void RecordValidationRejectsNonfiniteCoordinatesAndInvalidButtonMetadata()
    {
        var good = Move(1);
        var invalid = new[]
        {
            good with { Kind = (CocoaPopupPointerKind)100 },
            good with { X = double.PositiveInfinity }, good with { Y = double.NegativeInfinity },
            good with { Timestamp = double.NaN }, good with { Timestamp = -1 },
            good with { ScrollX = double.NaN }, good with { ScrollY = 1 },
            good with { PreciseScroll = true }, good with { ScrollPhase = 1 },
            good with { MomentumPhase = 1 }, good with { Button = 0 },
            good with { ClickCount = 1 }, good with { Kind = CocoaPopupPointerKind.Down },
            good with { Kind = CocoaPopupPointerKind.Up, Button = 64 },
            good with { Kind = CocoaPopupPointerKind.Drag, Button = 0, ClickCount = -1 }
        };
        foreach (var value in invalid) Assert.False(CocoaPopupInputQueue.IsValid(value));
        Assert.True(CocoaPopupInputQueue.IsValid(good));
        Assert.True(CocoaPopupInputQueue.IsValid(good with { Kind = CocoaPopupPointerKind.Down, Button = 63 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(257)]
    public void QueueCapacityIsBounded(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CocoaPopupInputQueue(capacity));

    [Fact]
    public void EmptyReadDoesNotConsumeEvents()
    {
        var queue = Enabled();
        Assert.True(queue.TryWrite(Move(1)));
        Assert.Equal(0, queue.Read([], out _));
        Span<CocoaPopupPointerEvent> output = stackalloc CocoaPopupPointerEvent[1];
        Assert.Equal(1, queue.Read(output, out _));
        Assert.Equal(Move(1), output[0]);
    }

    [Fact]
    public void ManagedDrainAndPolicyChangesRemainOnTheOwningThread()
    {
        var queue = Enabled();
        Assert.True(queue.TryWrite(Move(1)));
        Exception? readError = null, policyError = null, closeError = null;
        var thread = new Thread(() =>
        {
            readError = Record.Exception(() => queue.Read([], out _));
            policyError = Record.Exception(() => queue.SetEnabled(false));
            closeError = Record.Exception(queue.Close);
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(readError);
        Assert.IsType<InvalidOperationException>(policyError);
        Assert.IsType<InvalidOperationException>(closeError);
        Span<CocoaPopupPointerEvent> output = stackalloc CocoaPopupPointerEvent[1];
        Assert.Equal(1, queue.Read(output, out _));
    }
}
