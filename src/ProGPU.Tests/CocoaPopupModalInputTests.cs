using ProGPU.Backend;
using Silk.NET.Input;
using Xunit;
using PopupOperations = ProGPU.Tests.CocoaPopupWindowTests.Operations;

namespace ProGPU.Tests;

public sealed class CocoaPopupModalInputTests
{
    [Fact]
    public void CapabilityRequiresExactLiveProviderContextNotEqualNativeHandles()
    {
        using var first = Ready(new PopupOperations());
        using var second = Ready(new PopupOperations());
        var input = NativeWindowInput.CreateInput(first);
        using var other = NativeWindowInput.CreateInput(second);
        Assert.Equal(first.Handle, second.Handle); // Deliberately identical fixture handles.
        Assert.True(NativePopupWindow.SupportsModalInput(first, input));
        Assert.False(NativePopupWindow.SupportsModalInput(first, other));
        Assert.False(NativePopupWindow.SupportsModalInput(second, input));
        input.Dispose();
        using var replacement = NativeWindowInput.CreateInput(first);
        Assert.False(NativePopupWindow.SupportsModalInput(first, input));
        Assert.True(NativePopupWindow.SupportsModalInput(first, replacement));
        first.Close();
        Assert.False(NativePopupWindow.SupportsModalInput(first, replacement));
    }

    [Fact]
    public void HiddenOwnerlessCapabilityDoesNotPermitShowOrPointerInput()
    {
        var popup = new PopupOperations();
        using var window = CocoaPopupWindowTests.Create(popup, sourceScheduled: true);
        window.Initialize();
        using var input = NativeWindowInput.CreateInput(window);
        Assert.True(NativePopupWindow.SupportsModalInput(window, input));
        Assert.False(popup.Input.CanReceive);
        Assert.Throws<InvalidOperationException>(() => window.IsVisible = true);
    }

    [Fact]
    public void BeginBlocksUnrelatedPopupBeforeNativeActivationAndRestoresAfterIdentityRelease()
    {
        var popup = new PopupOperations();
        using var window = Ready(popup);
        using var input = NativeWindowInput.CreateInput(window);
        var api = new SessionOperations(22);
        api.OnBegin = () => Assert.False(popup.Input.CanReceive);
        api.OnEnd = api.OnRelease = () => Assert.False(popup.Input.CanReceive);
        Assert.True(NativeWindowModalSession.TryBegin(api, out var session));
        Assert.False(popup.Input.CanReceive);
        session!.Dispose();
        Assert.True(popup.Input.CanReceive);
        Assert.Equal(new[] { "Begin", "End", "Release" }, api.Calls);
    }

    [Fact]
    public void OnlyCurrentOwnerIsAdmittedAcrossNestedAndOutOfOrderRelease()
    {
        var first = new PopupOperations();
        var second = new PopupOperations();
        using var firstWindow = Ready(first, 1);
        using var secondWindow = Ready(second, 22);
        using var firstInput = NativeWindowInput.CreateInput(firstWindow);
        using var secondInput = NativeWindowInput.CreateInput(secondWindow);
        Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(1), out var outer));
        using (outer)
        {
            Assert.True(first.Input.CanReceive);
            Assert.False(second.Input.CanReceive);
            Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(22), out var inner));
            using (inner)
            {
                outer!.Dispose();
                Assert.False(first.Input.CanReceive);
                Assert.True(second.Input.CanReceive);
                Assert.False(outer.IsReleased);
            }
            Assert.True(outer!.IsReleased);
            Assert.True(first.Input.CanReceive);
            Assert.True(second.Input.CanReceive);
        }
    }

    [Fact]
    public void SameOwnerFramesDoNotRestoreInputWhileAnUnrelatedNestedFrameRemains()
    {
        var popup = new PopupOperations();
        using var window = Ready(popup);
        using var input = NativeWindowInput.CreateInput(window);
        Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(1), out var first));
        using (first)
        {
            Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(1), out var second));
            using (second)
            {
                Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(22), out var third));
                using (third)
                {
                    int completed = 0;
                    Assert.True(NativeWindowModalSession.TryReleaseWindow(Owner(1), () => completed++));
                    Assert.False(popup.Input.CanReceive);
                    Assert.Equal(0, completed);
                    third!.Dispose();
                    Assert.Equal(1, completed);
                    Assert.True(popup.Input.CanReceive);
                }
            }
        }
    }

    [Fact]
    public void DeferredPollReleaseDoesNotRestoreInputInsideTheNativeCallback()
    {
        var popup = new PopupOperations();
        using var window = Ready(popup);
        using var input = NativeWindowInput.CreateInput(window);
        var api = new SessionOperations(22);
        Assert.True(NativeWindowModalSession.TryBegin(api, out var session));
        using (session)
        {
            api.OnPoll = () =>
            {
                session!.Dispose();
                Assert.False(popup.Input.CanReceive);
                Assert.False(session.IsReleased);
            };
            Assert.True(NativeWindowModalSession.TryPumpEvents());
            Assert.True(popup.Input.CanReceive);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedBeginRestoresActualStackWithoutOverwritingNewSourceIntent(bool throws)
    {
        var popup = new PopupOperations();
        using var window = Ready(popup);
        using var input = NativeWindowInput.CreateInput(window);
        Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(1), out var outer));
        using (outer)
        {
            var expected = new InvalidOperationException("Begin failed.");
            var candidate = new SessionOperations(22) { Result = 0 };
            candidate.OnBegin = () =>
            {
                Assert.False(popup.Input.CanReceive);
                Assert.True(window.SetInputAllowed(false));
                if (throws) throw expected;
            };
            if (throws) Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => NativeWindowModalSession.TryBegin(candidate, out _)));
            else Assert.False(NativeWindowModalSession.TryBegin(candidate, out _));
            Assert.False(popup.Input.CanReceive);
            Assert.True(NativeWindowModalSession.RetainsWindow(Owner(1)));
            Assert.True(window.SetInputAllowed(true));
            Assert.True(popup.Input.CanReceive);
            Assert.Equal(new[] { "Begin", "Release" }, candidate.Calls);
        }
    }

    [Fact]
    public void NewlyRegisteredPopupContextBindingAndReopenUseTheCurrentSession()
    {
        Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(22), out var session));
        using (session)
        {
            var popup = new PopupOperations();
            using var window = Ready(popup);
            var input = NativeWindowInput.CreateInput(window);
            Assert.False(popup.Input.CanReceive);
            input.Dispose();
            using var replacement = NativeWindowInput.CreateInput(window);
            window.IsVisible = false;
            window.IsVisible = true;
            Assert.False(popup.Input.CanReceive);
            window.IsVisible = false;
            Assert.True(window.BindOwner(Owner(22)));
            window.IsVisible = true;
            Assert.True(popup.Input.CanReceive);
            Assert.True(window.SetInputTransparent(true));
            session!.Dispose();
            Assert.False(popup.Input.CanReceive);
            Assert.True(window.SetInputTransparent(false));
            Assert.True(popup.Input.CanReceive);
        }
    }

    [Fact]
    public void RegistrationInsideBeginUsesCandidateThenRollbackUsesRestoredFrame()
    {
        PopupOperations popup = new();
        CocoaPopupWindow? window = null;
        IInputContext? input = null;
        var api = new SessionOperations(22) { Result = 0 };
        api.OnBegin = () =>
        {
            window = Ready(popup);
            input = NativeWindowInput.CreateInput(window);
            Assert.False(popup.Input.CanReceive);
        };
        try
        {
            Assert.False(NativeWindowModalSession.TryBegin(api, out _));
            Assert.True(popup.Input.CanReceive);
        }
        finally { input?.Dispose(); window?.Dispose(); }
    }

    [Fact]
    public void CompletionEnteringNewSessionCannotBeOverwrittenByOlderRestoration()
    {
        var popup = new PopupOperations();
        using var window = Ready(popup);
        using var input = NativeWindowInput.CreateInput(window);
        Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(22), out var session));
        NativeWindowModalSession? replacement = null;
        try
        {
            Assert.True(NativeWindowModalSession.TryReleaseWindow(Owner(22), () =>
                Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(33), out replacement))));
            Assert.True(session!.IsReleased);
            Assert.False(popup.Input.CanReceive);
            Assert.True(NativeWindowModalSession.RetainsWindow(Owner(33)));
        }
        finally { replacement?.Dispose(); session?.Dispose(); }
        Assert.True(popup.Input.CanReceive);
    }

    [Fact]
    public void NativeBeginCancelsHeldStateImmediatelyButDoesNotCallSourceDuringTransition()
    {
        var popup = new PopupOperations();
        int wakes = 0;
        using var window = Ready(popup, wake: () =>
        {
            Assert.False(NativeWindowModalSession.IsInputPolicyTransitioning);
            wakes++;
        });
        using var input = NativeWindowInput.CreateInput(window);
        var native = (INativePointerInputContext)input;
        var kinds = new List<NativePointerEventKind>();
        native.PointerEvent += value =>
        {
            Assert.False(NativeWindowModalSession.IsInputPolicyTransitioning);
            kinds.Add(value.Kind);
        };
        Assert.True(popup.Input.TryWrite(Button(CocoaPopupPointerKind.Down)));
        window.DoEvents();
        var generation = native.InputGeneration;
        var api = new SessionOperations(22);
        api.OnBegin = () =>
        {
            Assert.False(input.Mice[0].IsButtonPressed(MouseButton.Left));
            Assert.NotEqual(generation, native.InputGeneration);
            Assert.Equal(new[] { NativePointerEventKind.Down }, kinds);
            Assert.Equal(0, wakes);
        };
        Assert.True(NativeWindowModalSession.TryBegin(api, out var session));
        using (session)
        {
            Assert.Equal(1, wakes);
            Assert.Equal(new[] { NativePointerEventKind.Down }, kinds);
            window.DoEvents();
            Assert.Equal(new[] { NativePointerEventKind.Down, NativePointerEventKind.Cancel }, kinds);
        }
    }

    [Fact]
    public void BeginFromDeliveredDownInvalidatesCopiedUpAndNeverSynthesizesClick()
    {
        var popup = new PopupOperations();
        using var window = Ready(popup);
        using var input = NativeWindowInput.CreateInput(window);
        NativeWindowModalSession? session = null;
        var kinds = new List<NativePointerEventKind>();
        int ups = 0, clicks = 0;
        input.Mice[0].MouseUp += (_, _) => ups++;
        input.Mice[0].Click += (_, _, _) => clicks++;
        ((INativePointerInputContext)input).PointerEvent += value =>
        {
            kinds.Add(value.Kind);
            if (value.Kind == NativePointerEventKind.Down)
                Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(22), out session));
        };
        Assert.True(popup.Input.TryWrite(Button(CocoaPopupPointerKind.Down)));
        Assert.True(popup.Input.TryWrite(Button(CocoaPopupPointerKind.Up)));
        try
        {
            window.DoEvents();
            Assert.Equal(new[] { NativePointerEventKind.Down, NativePointerEventKind.Cancel }, kinds);
            Assert.Equal(0, ups);
            Assert.Equal(0, clicks);
        }
        finally { session?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncertainEndOrIdentityReleaseCannotReenableAnyPopup(bool identityRelease)
    {
        OnIsolatedThread(() =>
        {
            var popup = new PopupOperations();
            using var window = Ready(popup);
            using var input = NativeWindowInput.CreateInput(window);
            var failure = new InvalidOperationException("Native release is uncertain.");
            var api = new SessionOperations(22);
            Assert.True(NativeWindowModalSession.TryBegin(api, out var session));
            if (identityRelease) api.OnRelease = () => throw failure;
            else api.OnEnd = () => throw failure;
            int completed = 0;
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                NativeWindowModalSession.TryReleaseWindow(Owner(22), () => completed++)));
            Assert.Equal(identityRelease, session!.IsReleased);
            Assert.Equal(0, completed);
            Assert.False(popup.Input.CanReceive);
            Assert.False(NativePopupWindow.SupportsModalInput(window, input));
            Assert.True(window.SetInputAllowed(true));
            Assert.False(popup.Input.CanReceive);
            var laterPopup = new PopupOperations();
            using var laterWindow = Ready(laterPopup, 22);
            using var laterInput = NativeWindowInput.CreateInput(laterWindow);
            Assert.False(laterPopup.Input.CanReceive);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => NativeWindowModalSession.TryPumpEvents()));
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => NativeWindowModalSession.TryBegin(new SessionOperations(1), out _)));
            Assert.Equal(new[] { "Begin", "End" }.Concat(identityRelease ? new[] { "Release" } : []), api.Calls);
        });
    }

    [Fact]
    public void RejectedNativeBlockingPreventsBeginAndRetainsFailClosedGeneration()
    {
        OnIsolatedThread(() =>
        {
            var popup = new PopupOperations();
            using var window = Ready(popup);
            using var input = NativeWindowInput.CreateInput(window);
            popup.InputAdmission = allowed => allowed;
            var api = new SessionOperations(22);
            Assert.Throws<InvalidOperationException>(() => NativeWindowModalSession.TryBegin(api, out _));
            Assert.Equal(new[] { "Release" }, api.Calls);
            Assert.False(popup.Input.CanReceive);
            popup.InputAdmission = null;
            Assert.True(window.SetInputAllowed(true));
            Assert.False(popup.Input.CanReceive);
        });
    }

    [Fact]
    public void DisposalDuringNativeBlockingWaitsForOwnerDrainAndDoesNotCallSource()
    {
        var popup = new PopupOperations();
        using var window = Ready(popup);
        var input = NativeWindowInput.CreateInput(window);
        int disconnected = 0;
        input.ConnectionChanged += (_, _) =>
        {
            Assert.False(NativeWindowModalSession.IsInputPolicyTransitioning);
            disconnected++;
        };
        popup.InputAdmission = allowed =>
        {
            if (!allowed) window.Dispose();
            return true;
        };
        Assert.True(NativeWindowModalSession.TryBegin(new SessionOperations(22), out var session));
        using (session)
        {
            Assert.False(window.IsReleased);
            Assert.Equal(0, disconnected);
            window.DoEvents();
            Assert.True(window.IsReleased);
            Assert.Equal(1, disconnected);
        }
    }

    private static CocoaPopupWindow Ready(PopupOperations operations, nint owner = 1, Action? wake = null)
    {
        var window = CocoaPopupWindowTests.Create(operations, wake, sourceScheduled: true);
        window.Initialize();
        Assert.True(window.BindOwner(Owner(owner)));
        window.IsVisible = true;
        return window;
    }

    private static NativeWindowHandle Owner(nint handle) => new(NativeWindowKind.Cocoa, handle, 0, "NSWindow");
    private static CocoaPopupPointerEvent Button(CocoaPopupPointerKind kind) => new(kind, 3, 4, 5, 0, 1, CocoaPopupModifiers.None);

    private static void OnIsolatedThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(action));
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private sealed class SessionOperations(nint window) : INativeModalSessionOperations
    {
        public nint Window => window;
        public nint Result { get; init; } = window;
        public List<string> Calls { get; } = [];
        public Action? OnBegin, OnPoll, OnEnd, OnRelease;
        public nint Begin() { Calls.Add("Begin"); OnBegin?.Invoke(); return Result; }
        public nint Poll(nint session) { Calls.Add("Poll"); OnPoll?.Invoke(); return NativeWindowModalSession.ContinueResponse; }
        public void End(nint session) { Calls.Add("End"); OnEnd?.Invoke(); }
        public void Dispose() { Calls.Add("Release"); OnRelease?.Invoke(); }
    }
}
