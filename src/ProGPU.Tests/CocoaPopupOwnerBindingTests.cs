using System.Reflection;
using ProGPU.Backend;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Xunit;
using Operations = ProGPU.Tests.CocoaPopupWindowTests.Operations;

namespace ProGPU.Tests;

public sealed class CocoaPopupOwnerBindingTests
{
    private static readonly NativeWindowHandle First = new(NativeWindowKind.Cocoa, 10, 0, "NSWindow");
    private static readonly NativeWindowHandle Second = new(NativeWindowKind.Cocoa, 20, 0, "NSWindow");

    [Fact]
    public void PublicFactoryDefersNativeCreationAndRequiresTheActualSourceWakeCallback()
    {
        var options = CocoaPopupWindowTests.Options();
        Assert.Throws<ArgumentNullException>(() => NativePopupWindow.CreateCocoaPopupWindow(options, null!));
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(() => NativePopupWindow.CreateCocoaPopupWindow(options, () => { }));
            return;
        }
        int wakes = 0;
        using var window = NativePopupWindow.CreateCocoaPopupWindow(options, () => wakes++);
        Assert.False(window.IsInitialized);
        Assert.Equal(0, window.Handle);
        Assert.Null(window.Native);
        Assert.Null(window.Parent);
        window.ContinueEvents();
        Assert.Equal(1, wakes);
        Assert.False(NativePopupWindow.TryBindCocoaOwner(window, First));
    }

    [Fact]
    public void HiddenOwnerlessPanelPublishesGeometryAndRenderLeaseButCannotReceiveOrShow()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        using var lease = window.AcquireRenderLease();
        using var input = NativeWindowInput.CreateInput(window);
        Assert.Null(window.Parent);
        Assert.Equal(NativeWindowHandle.Empty, window.Owner);
        Assert.Equal(operations.Window, lease.Window);
        Assert.Equal(operations.ContentView, lease.ContentView);
        Assert.Equal(operations.Window.Handle, window.Native!.Cocoa);
        Assert.Null(window.Native.Glfw);
        Assert.True(window.TryGetGeometry(out var geometry));
        Assert.Equal(operations.Geometry, geometry);
        Assert.True(window.SetInputAllowed(true));
        Assert.False(operations.Input.Enabled);
        Assert.False(operations.Input.CanReceive);
        Assert.All(operations.InputRequests, allowed => Assert.False(allowed));
        Assert.Throws<InvalidOperationException>(() => window.IsVisible = true);
        Assert.Empty(operations.Calls);
    }

    [Fact]
    public void BindingRebindingAndClearingPreserveThePanelViewAndPreownerRenderLease()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        using var lease = window.AcquireRenderLease();
        using var input = NativeWindowInput.CreateInput(window);
        using var platform = NativeWindowPlatformFactory.Create(window);
        var native = window.Native;
        var size = window.FramebufferSize;
        Assert.True(NativePopupWindow.TryBindCocoaOwner(window, First));
        Assert.True(operations.Input.Enabled);
        Assert.False(operations.Input.CanReceive);
        window.IsVisible = true;
        Assert.True(operations.Input.CanReceive);
        window.IsVisible = false;
        Assert.True(platform.SetParent(Second));
        Assert.Equal(Second, window.Owner);
        Assert.True(operations.Input.Enabled);
        Assert.True(platform.SetParent(NativeWindowHandle.Empty));
        Assert.Equal(NativeWindowHandle.Empty, window.Owner);
        Assert.False(operations.Input.Enabled);
        Assert.Throws<InvalidOperationException>(() => window.IsVisible = true);
        Assert.True(platform.SetParent(First));
        Assert.Same(native, window.Native);
        Assert.Equal(size, window.FramebufferSize);
        Assert.Equal(operations.Window, lease.Window);
        Assert.Equal(operations.ContentView, lease.ContentView);
        Assert.Equal(new[] { First, Second, NativeWindowHandle.Empty, First }, operations.OwnerRequests);
        Assert.DoesNotContain("release", operations.Calls);
    }

    [Fact]
    public void VisibleSameOwnerVerificationIsNonmutatingButDifferentOwnerIsRejected()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        using var input = NativeWindowInput.CreateInput(window);
        Assert.True(window.BindOwner(First));
        window.IsVisible = true;
        ulong generation = operations.Input.Generation;
        int inputRequests = operations.InputRequests.Count;
        Assert.True(window.BindOwner(First));
        Assert.False(window.BindOwner(Second));
        Assert.False(window.BindOwner(NativeWindowHandle.Empty));
        Assert.Equal(First, window.Owner);
        Assert.Equal(generation, operations.Input.Generation);
        Assert.Equal(inputRequests, operations.InputRequests.Count);
        Assert.Equal(new[] { First }, operations.OwnerRequests);
        Assert.True(window.IsVisible);
    }

    [Fact]
    public void FixedOwnerFactoryKeepsItsParentWakeCallbackAndVisibleVerification()
    {
        var operations = new Operations();
        int wakes = 0;
        using var window = CocoaPopupWindowTests.Create(operations, () => wakes++);
        window.Initialize();
        var owner = window.Owner;
        var parent = window.Parent;
        Assert.False(window.BindOwner(First));
        Assert.False(window.BindOwner(NativeWindowHandle.Empty));
        window.IsVisible = true;
        using var platform = NativeWindowPlatformFactory.Create(window);
        Assert.True(platform.SetParent(owner));
        window.ContinueEvents();
        Assert.Same(parent, window.Parent);
        Assert.Equal(owner, window.Owner);
        Assert.Equal(1, wakes);
        Assert.Empty(operations.OwnerRequests);
    }

    [Fact]
    public void ControllerDisabledIntentSurvivesBindingAndInputContextReplacement()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        using var controller = new SilkWindowController(window);
        Assert.True(controller.Attach());
        Assert.True(controller.SetEnabled(false));
        using (var input = NativeWindowInput.CreateInput(window))
        {
            Assert.True(window.BindOwner(First));
            Assert.False(operations.Input.Enabled);
        }
        using var replacement = NativeWindowInput.CreateInput(window);
        Assert.False(operations.Input.Enabled);
        Assert.True(window.BindOwner(Second));
        Assert.False(operations.Input.Enabled);
        Assert.True(controller.SetEnabled(true));
        Assert.True(operations.Input.Enabled);
    }

    [Fact]
    public void BindingWithoutAnInputContextDoesNotAdmitNativeInput()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        Assert.True(window.BindOwner(First));
        Assert.False(operations.Input.Enabled);
        Assert.All(operations.InputRequests, allowed => Assert.False(allowed));
        window.IsVisible = true;
        Assert.False(operations.Input.CanReceive);
        using var input = NativeWindowInput.CreateInput(window);
        Assert.True(operations.Input.CanReceive);
    }

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData((int)NativeWindowKind.Cocoa, 0, 0)]
    [InlineData((int)NativeWindowKind.Cocoa, 10, 1)]
    public void MalformedOwnersAreRejectedBeforeChangingInputOrNativeOwnership(int kind, int handle, int display)
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        Assert.False(window.BindOwner(new((NativeWindowKind)kind, handle, display, "invalid")));
        Assert.Empty(operations.OwnerRequests);
        Assert.Empty(operations.InputRequests);
        Assert.Equal(NativeWindowHandle.Empty, window.Owner);
    }

    [Fact]
    public void RejectedNativeBindingStopsInputAndDoesNotPublishTheCandidateOwner()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        using var input = NativeWindowInput.CreateInput(window);
        Assert.True(window.BindOwner(First));
        Assert.True(operations.Input.Enabled);
        ulong generation = operations.Input.Generation;
        operations.OwnerAccepted = false;
        Assert.False(window.BindOwner(Second));
        Assert.Equal(First, window.Owner);
        Assert.False(operations.Input.Enabled);
        Assert.NotEqual(generation, operations.Input.Generation);
        Assert.Equal(operations.Input.Generation, ((INativePointerInputContext)input).InputGeneration);
    }

    [Fact]
    public void FailureToBlockNativeInputPreventsAnyOwnerMutation()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        operations.InputAdmission = _ => false;
        Assert.False(window.BindOwner(First));
        Assert.Empty(operations.OwnerRequests);
        Assert.False(operations.Input.Enabled);
        Assert.Equal(NativeWindowHandle.Empty, window.Owner);
    }

    [Fact]
    public void DisposeDuringBindingCannotPublishSuccessOrDestroyALeasedView()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        using var lease = window.AcquireRenderLease();
        operations.OnBindOwner = window.Dispose;
        Assert.False(window.BindOwner(First));
        Assert.Equal(NativeWindowHandle.Empty, window.Owner);
        Assert.False(window.IsReleased);
        Assert.Equal(operations.ContentView, lease.ContentView);
        Assert.DoesNotContain("release", operations.Calls);
        lease.Dispose();
        Assert.True(window.TryCompleteDispose());
        Assert.Equal(1, operations.Calls.Count(value => value == "release"));
    }

    [Fact]
    public void BindingFailureKeepsItsOriginalExceptionWhenRetirementAlsoFails()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        var expected = new InvalidOperationException("owner setup failed");
        operations.OnBindOwner = () =>
        {
            window.Dispose();
            operations.HideAccepted = false;
            throw expected;
        };
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => window.BindOwner(First)));
        Assert.IsType<InvalidOperationException>(expected.Data["PopupOwnerRetirement"]);
        Assert.Equal(NativeWindowHandle.Empty, window.Owner);
        Assert.False(window.IsReleased);
        Assert.DoesNotContain("release", operations.Calls);
        operations.HideAccepted = true;
        Assert.True(window.TryCompleteDispose());
    }

    [Fact]
    public void RebindingDuringPointerDeliveryDiscardsTheOldOwnersCopiedTail()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        using var input = NativeWindowInput.CreateInput(window);
        var native = (INativePointerInputContext)input;
        Assert.True(window.BindOwner(First));
        window.IsVisible = true;
        var events = new List<NativePointerEventKind>();
        native.PointerEvent += value =>
        {
            events.Add(value.Kind);
            if (value.Kind != NativePointerEventKind.Down) return;
            window.IsVisible = false;
            Assert.True(window.BindOwner(Second));
            window.IsVisible = true;
        };
        Assert.True(operations.Input.TryWrite(new(CocoaPopupPointerKind.Down, 2, 3, 1, 0, 1, 0)));
        Assert.True(operations.Input.TryWrite(new(CocoaPopupPointerKind.Up, 2, 3, 2, 0, 1, 0)));
        window.DoEvents();
        Assert.Equal(new[] { NativePointerEventKind.Down, NativePointerEventKind.Cancel }, events);
        Assert.Equal(Second, window.Owner);
        Assert.False(input.Mice[0].IsButtonPressed(MouseButton.Left));
        Assert.True(operations.Input.CanReceive);
        Assert.True(operations.Input.TryWrite(new(CocoaPopupPointerKind.Move, 4, 5, 3, -1, 0, 0)));
        window.DoEvents();
        Assert.Equal(NativePointerEventKind.Move, events[^1]);
    }

    [Fact]
    public void NestedInitializationAndForeignThreadBindingsCannotReachNativeOwnership()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Load += () => Assert.Throws<InvalidOperationException>(() => window.BindOwner(First));
        window.Initialize();
        operations.OnBindOwner = () => Assert.Throws<InvalidOperationException>(() => window.BindOwner(Second));
        Assert.True(window.BindOwner(First));
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() => window.BindOwner(Second)));
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(new[] { First }, operations.OwnerRequests);
    }

    [Fact]
    public void BindingFromANativeCallbackIsRejectedWithoutChangingOwnershipOrInput()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        operations.Input.BeginNativeCallback();
        try { Assert.Throws<InvalidOperationException>(() => window.BindOwner(First)); }
        finally { operations.Input.EndNativeCallback(); }
        Assert.Empty(operations.OwnerRequests);
        Assert.Empty(operations.InputRequests);
        Assert.Equal(NativeWindowHandle.Empty, window.Owner);
    }

    [Fact]
    public void PublicBindingDoesNotProbeOrAdoptAnotherWindowProvider()
    {
        Assert.Throws<ArgumentNullException>(() => NativePopupWindow.TryBindCocoaOwner(null!, First));
        var foreign = DispatchProxy.Create<IWindow, ForeignWindow>();
        Assert.False(NativePopupWindow.TryBindCocoaOwner(foreign, First));
    }

    public class ForeignWindow : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("A foreign window must not be inspected.");
    }

    private static CocoaPopupWindow Create(Operations operations) =>
        CocoaPopupWindowTests.Create(operations, sourceScheduled: true);
}
