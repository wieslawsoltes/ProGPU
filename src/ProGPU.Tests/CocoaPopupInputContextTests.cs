using ProGPU.Backend;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Xunit;
using Operations = ProGPU.Tests.CocoaPopupWindowTests.Operations;

namespace ProGPU.Tests;

public sealed class CocoaPopupInputContextTests
{
    [Fact]
    public void ExplicitFactoryIsDeferredAndRejectsAnOwnerClosedBeforeInitialization()
    {
        var operations = new Operations();
        using var owner = Ready(operations);
        var options = WindowOptions.Default;
        options.API = GraphicsAPI.None;
        options.Title = string.Empty;
        options.IsVisible = false;
        options.WindowBorder = WindowBorder.Hidden;
        options.ShouldSwapAutomatically = false;
        options.IsContextControlDisabled = true;
        options.FramesPerSecond = options.UpdatesPerSecond = 0;
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(() => NativePopupWindow.CreateOwnedCocoaWindow(owner, options));
            return;
        }
        using var popup = NativePopupWindow.CreateOwnedCocoaWindow(owner, options);
        Assert.Same(owner, popup.Parent);
        Assert.False(popup.IsInitialized);
        Assert.Equal(0, popup.Handle);
        owner.Close();
        // The source owner check rejects before any native call on fake handles.
        Assert.Throws<InvalidOperationException>(popup.Initialize);
        Assert.False(popup.IsInitialized);
        Assert.DoesNotContain("release", operations.Calls);
    }

    [Fact]
    public void InputAdmissionRequiresOneLiveContextAndRestoresIntentOnReplacement()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations);
        window.Initialize();
        window.IsVisible = true;
        Assert.True(window.SetInputAllowed(true));
        Assert.False(operations.Input.CanReceive);
        var context = NativeWindowInput.CreateInput(window);
        Assert.Empty(context.Keyboards);
        Assert.Single(context.Mice);
        Assert.True(operations.Input.CanReceive);
        Assert.Throws<InvalidOperationException>(() => NativeWindowInput.CreateInput(window));
        context.Dispose();
        Assert.False(operations.Input.CanReceive);
        using var replacement = NativeWindowInput.CreateInput(window);
        Assert.True(operations.Input.CanReceive);
        Assert.False(replacement.Mice[0].IsButtonPressed(MouseButton.Left));
        Assert.True(window.SetInputAllowed(false));
        replacement.Dispose();
        using var blocked = NativeWindowInput.CreateInput(window);
        Assert.False(operations.Input.CanReceive);
    }

    [Fact]
    public void FullQueueRetainsEveryEventAndItsOwnModifiersWithoutAKeyboard()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        var native = (INativePointerInputContext)input;
        var output = new List<NativePointerEvent>();
        void Capture(NativePointerEvent value)
        {
            Assert.Equal(value, native.CurrentEvent);
            Assert.Equal(value.Kind == NativePointerEventKind.Down, input.Mice[0].IsButtonPressed(MouseButton.Left));
            output.Add(value);
        }
        native.PointerEvent += Capture;
        for (int i = 0; i < 256; i++)
            Assert.True(operations.Input.TryWrite(new(i % 2 == 0 ? CocoaPopupPointerKind.Down : CocoaPopupPointerKind.Up,
                i + 0.125, -i - 0.25, i, 0, 1, i % 2 == 0 ? CocoaPopupModifiers.Shift : CocoaPopupModifiers.Command)));
        window.DoUpdate();
        Assert.Equal(256, output.Count);
        for (int i = 0; i < 256; i++)
            Assert.Equal(new NativePointerEvent(i % 2 == 0 ? NativePointerEventKind.Down : NativePointerEventKind.Up,
                i + 0.125, -i - 0.25, i, 0, 1, i % 2 == 0 ? NativePointerModifiers.Shift : NativePointerModifiers.Super), output[i]);
        Assert.Null(native.CurrentEvent);
        // Do not compare cancellation during fixture cleanup with a physical up.
        native.PointerEvent -= Capture;
    }

    [Fact]
    public void PreciseScrollPreservesUnitsDirectionPhasesAndDoubleCoordinates()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        var output = new List<NativePointerEvent>();
        var native = (INativePointerInputContext)input;
        native.PointerEvent += output.Add;
        var modifiers = CocoaPopupModifiers.Control | CocoaPopupModifiers.Option | CocoaPopupModifiers.CapsLock |
            CocoaPopupModifiers.NumericPad | CocoaPopupModifiers.Help | CocoaPopupModifiers.Function;
        Assert.True(operations.Input.TryWrite(new(CocoaPopupPointerKind.Scroll, 0.123456789, 2.5, 9, -1, 0,
            modifiers, -3.25, 0.125, true, 2, 8)));
        Assert.True(operations.Input.TryWrite(new(CocoaPopupPointerKind.Scroll, 0.123456789, 2.5, 10, -1, 0,
            modifiers, -3.25, 0.125, false, 0, 0)));
        window.DoEvents();
        Assert.Equal(2, output.Count);
        Assert.Equal(0.123456789, output[0].X);
        Assert.Equal(-3.25, output[0].ScrollX);
        Assert.Equal(0.125, output[0].ScrollY);
        Assert.Equal(NativePointerScrollUnit.Points, output[0].ScrollUnit);
        Assert.Equal(NativePointerScrollUnit.Lines, output[1].ScrollUnit);
        Assert.Equal(2u, output[0].ScrollPhase);
        Assert.Equal(8u, output[0].MomentumPhase);
        Assert.Equal(NativePointerScrollProtocol.AppKit, output[0].ScrollProtocol);
        Assert.Equal(NativePointerModifiers.Control | NativePointerModifiers.Alt | NativePointerModifiers.CapsLock |
            NativePointerModifiers.NumericPad | NativePointerModifiers.Help | NativePointerModifiers.Function, output[0].Modifiers);
    }

    [Fact]
    public void HidingDuringDownDiscardsCopiedTailAndCancelsWithoutAReleaseOrClick()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        var native = (INativePointerInputContext)input;
        var kinds = new List<NativePointerEventKind>();
        native.PointerEvent += value => kinds.Add(value.Kind);
        int up = 0, click = 0;
        input.Mice[0].MouseDown += (_, _) => window.IsVisible = false;
        input.Mice[0].MouseUp += (_, _) => up++;
        input.Mice[0].Click += (_, _, _) => click++;
        Assert.True(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Down)));
        Assert.True(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Up)));
        window.DoEvents();
        Assert.Equal(new[] { NativePointerEventKind.Down, NativePointerEventKind.Cancel }, kinds);
        Assert.Equal(0, up);
        Assert.Equal(0, click);
        Assert.False(input.Mice[0].IsButtonPressed(MouseButton.Left));
        window.IsVisible = true;
        window.DoEvents();
        Assert.Equal(2, kinds.Count);
    }

    [Fact]
    public void BlockingCancelsImmediatelyAndKeepsLatestNativeGeneration()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        var native = (INativePointerInputContext)input;
        var kinds = new List<NativePointerEventKind>();
        native.PointerEvent += value => kinds.Add(value.Kind);
        Assert.True(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Down)));
        window.DoEvents();
        ulong before = native.InputGeneration;
        Assert.True(window.SetInputAllowed(false));
        Assert.NotEqual(before, native.InputGeneration);
        Assert.Equal(operations.Input.Generation, native.InputGeneration);
        Assert.Equal(new[] { NativePointerEventKind.Down, NativePointerEventKind.Cancel }, kinds);
        Assert.False(input.Mice[0].IsButtonPressed(MouseButton.Left));
        Assert.False(operations.Input.CanReceive);
    }

    [Fact]
    public void UnmatchedNativeUpIsForwardedButDoesNotInventAClick()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        int up = 0, click = 0;
        input.Mice[0].MouseUp += (_, _) => up++;
        input.Mice[0].Click += (_, _, _) => click++;
        Assert.True(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Up)));
        window.DoEvents();
        Assert.Equal(1, up);
        Assert.Equal(0, click);
    }

    [Fact]
    public void DisposalFromDownCancelsAndDisconnectsBeforeAnotherContextCanAttach()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        var input = NativeWindowInput.CreateInput(window);
        var native = (INativePointerInputContext)input;
        var kinds = new List<NativePointerEventKind>();
        native.PointerEvent += value => kinds.Add(value.Kind);
        input.Mice[0].MouseDown += (_, _) => input.Dispose();
        int disconnected = 0;
        input.ConnectionChanged += (_, connected) => { Assert.False(connected); disconnected++; };
        Assert.True(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Down)));
        Assert.True(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Up)));
        window.DoEvents();
        Assert.Equal(new[] { NativePointerEventKind.Down, NativePointerEventKind.Cancel }, kinds);
        Assert.Equal(1, disconnected);
        Assert.False(input.Mice[0].IsConnected);
        Assert.False(operations.Input.CanReceive);
        using var replacement = NativeWindowInput.CreateInput(window);
        Assert.True(operations.Input.CanReceive);
    }

    [Fact]
    public void ClosingWindowFromCancellationCannotStrandNativeRetirement()
    {
        var operations = new Operations();
        var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        ((INativePointerInputContext)input).PointerEvent += value =>
        {
            if (value.Kind == NativePointerEventKind.Cancel) window.Dispose();
        };
        Assert.True(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Down)));
        window.DoEvents();
        window.IsVisible = false;
        Assert.True(window.IsReleased);
        Assert.Equal(1, operations.Calls.Count(value => value == "release"));
    }

    [Fact]
    public void NativeCallbackDefersCancellationAndRetirementUntilManagedDrain()
    {
        var operations = new Operations();
        var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        int canceled = 0;
        ((INativePointerInputContext)input).PointerEvent += value =>
        {
            Assert.False(operations.Input.IsInNativeCallback);
            if (value.Kind == NativePointerEventKind.Cancel) canceled++;
        };
        Assert.True(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Down)));
        window.DoEvents();
        operations.Input.BeginNativeCallback();
        window.Dispose();
        Assert.Equal(0, canceled);
        Assert.False(window.IsReleased);
        operations.Input.EndNativeCallback();
        window.DoEvents();
        Assert.Equal(1, canceled);
        Assert.True(window.IsReleased);
    }

    [Fact]
    public void OverflowFailsBeforePublishingAnyQueuedInput()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        int published = 0;
        ((INativePointerInputContext)input).PointerEvent += _ => published++;
        for (int i = 0; i < 256; i++) Assert.True(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Down)));
        Assert.False(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Down)));
        Assert.Throws<InvalidOperationException>(window.DoEvents);
        Assert.Equal(0, published);
        Assert.False(input.Mice[0].IsConnected);
        Assert.False(operations.Input.CanReceive);
    }

    [Fact]
    public void UnknownModifiersCannotBeSilentlyLostInThePortableProjection()
    {
        var queue = new CocoaPopupInputQueue();
        queue.SetEnabled(true); queue.SetVisible(true);
        Assert.False(queue.TryWrite(Button(CocoaPopupPointerKind.Down) with { Modifiers = (CocoaPopupModifiers)(1u << 28) }));
        Assert.Throws<InvalidOperationException>(() => queue.Read([], out _));
    }

    [Fact]
    public void HandlerFailureBlocksInputAndRetainsOriginalFailureAcrossLaterDrains()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        var failure = new InvalidOperationException("pointer-handler");
        var cancellationFailure = new InvalidOperationException("cancel-handler");
        ((INativePointerInputContext)input).PointerEvent += value =>
        {
            throw (value.Kind == NativePointerEventKind.Cancel ? cancellationFailure : failure);
        };
        Assert.True(operations.Input.TryWrite(Button(CocoaPopupPointerKind.Down)));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(window.DoEvents));
        Assert.Same(cancellationFailure, failure.Data["PopupInputCancellation"]);
        Assert.False(operations.Input.CanReceive);
        Assert.False(input.Mice[0].IsConnected);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(window.DoEvents));
    }

    [Fact]
    public void CursorsRemainViewOwnedAndRejectedChangesDoNotPublishNewState()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        ICursor cursor = input.Mice[0].Cursor;
        cursor.StandardCursor = StandardCursor.IBeam;
        cursor.CursorMode = CursorMode.Hidden;
        Assert.Equal(new[] { (StandardCursor.Arrow, false), (StandardCursor.IBeam, false), (StandardCursor.IBeam, true) }, operations.CursorRequests);
        operations.CursorAccepted = false;
        Assert.False(cursor.IsSupported(StandardCursor.Hand));
        Assert.Throws<NotSupportedException>(() => cursor.StandardCursor = StandardCursor.Hand);
        Assert.Equal(StandardCursor.IBeam, cursor.StandardCursor);
        Assert.Throws<NotSupportedException>(() => cursor.CursorMode = CursorMode.Normal);
        Assert.Equal(CursorMode.Hidden, cursor.CursorMode);
        Assert.False(cursor.IsSupported(CursorMode.Disabled));
        Assert.Throws<NotSupportedException>(() => cursor.IsConfined = true);
        Assert.Throws<NotSupportedException>(() => cursor.Type = CursorType.Custom);
    }

    private static CocoaPopupWindow Ready(Operations operations)
    {
        var window = CocoaPopupWindowTests.Create(operations);
        window.Initialize();
        window.IsVisible = true;
        return window;
    }

    private static CocoaPopupPointerEvent Button(CocoaPopupPointerKind kind) =>
        new(kind, 3.25, 4.5, 12, 0, 1, CocoaPopupModifiers.None);
}
