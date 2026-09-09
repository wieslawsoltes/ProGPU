using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeWindowModalSessionTests
{
    [Fact]
    public void NestedSessionsOwnPollingAndRestorePreviousSession()
    {
        var outer = new Operations(11);
        var inner = new Operations(22);
        Assert.True(NativeWindowModalSession.TryBegin(outer, out var first));
        using (first)
        {
            Assert.True(NativeWindowModalSession.TryPumpEvents());
            Assert.True(NativeWindowModalSession.TryBegin(inner, out var second));
            using (second) Assert.True(NativeWindowModalSession.TryPumpEvents());
            Assert.True(second!.IsReleased);
            Assert.True(NativeWindowModalSession.TryPumpEvents());
        }
        Assert.False(NativeWindowModalSession.TryPumpEvents());
        Assert.Equal(new[] { "Begin", "Poll", "Poll", "End", "Release" }, outer.Calls);
        Assert.Equal(new[] { "Begin", "Poll", "End", "Release" }, inner.Calls);
    }

    [Fact]
    public void CallbackReleaseWaitsForNativePollReturnAndBlocksOrdinaryPolling()
    {
        var api = new Operations(11);
        var window = new NativeWindowHandle(NativeWindowKind.Cocoa, 11, 0, "Fixture");
        Assert.True(NativeWindowModalSession.TryBegin(api, out var session));
        using (session)
        {
            api.OnPoll = () =>
            {
                session!.Dispose();
                Assert.False(session.IsReleased);
                Assert.True(NativeWindowModalSession.RetainsWindow(window));
                Assert.True(NativeWindowModalSession.TryPumpEvents());
                Assert.Equal(new[] { "Begin", "Poll" }, api.Calls);
            };
            Assert.True(NativeWindowModalSession.TryPumpEvents());
            Assert.True(session!.IsReleased);
            Assert.False(NativeWindowModalSession.RetainsWindow(window));
        }
        Assert.Equal(new[] { "Begin", "Poll", "End", "Release" }, api.Calls);
        Assert.False(NativeWindowModalSession.IsActive);
    }

    [Fact]
    public void OuterReleaseWaitsForNestedSessionAndOuterCallbackToUnwind()
    {
        var outer = new Operations(11);
        var inner = new Operations(22);
        Assert.True(NativeWindowModalSession.TryBegin(outer, out var first));
        using (first)
        {
            outer.OnPoll = () =>
            {
                Assert.True(NativeWindowModalSession.TryBegin(inner, out var second));
                using (second)
                {
                    first!.Dispose();
                    Assert.False(first.IsReleased);
                    Assert.True(NativeWindowModalSession.TryPumpEvents());
                }
                Assert.False(first!.IsReleased);
                Assert.True(NativeWindowModalSession.TryPumpEvents()); // Outer native poll still owns dispatch.
            };
            Assert.True(NativeWindowModalSession.TryPumpEvents());
            Assert.True(first!.IsReleased);
        }
        Assert.Equal(new[] { "Begin", "Poll", "End", "Release" }, outer.Calls);
        Assert.Equal(new[] { "Begin", "Poll", "End", "Release" }, inner.Calls);
    }

    [Fact]
    public void BeginRejectionAndBeginFailureReleaseOwnershipWithoutChangingCurrentSession()
    {
        var api = new Operations(0);
        Assert.False(NativeWindowModalSession.TryBegin(api, out var rejected));
        Assert.Null(rejected);
        Assert.Equal(new[] { "Begin", "Release" }, api.Calls);
        Assert.False(NativeWindowModalSession.IsActive);
        api = new Operations(11) { OnBegin = () => throw new InvalidOperationException("Begin failed.") };
        Assert.Throws<InvalidOperationException>(() => NativeWindowModalSession.TryBegin(api, out _));
        Assert.Equal(new[] { "Begin", "Release" }, api.Calls);
        Assert.False(NativeWindowModalSession.IsActive);
    }

    [Fact]
    public void StoppedNativeSessionReportsActualResponseWithoutFallingBackToDefaultPoll()
    {
        var api = new Operations(11) { Response = -1000 };
        Assert.True(NativeWindowModalSession.TryBegin(api, out var session));
        using (session)
        {
            Assert.Throws<InvalidOperationException>(() => NativeWindowModalSession.TryPumpEvents());
            Assert.Equal((nint)(-1000), session!.LastResponse);
            Assert.True(NativeWindowModalSession.IsActive);
        }
        Assert.Equal(new[] { "Begin", "Poll", "End", "Release" }, api.Calls);
    }

    [Fact]
    public void ReentrantBeginIsRejectedAndReleasesItsUnpublishedLease()
    {
        var nested = new Operations(22);
        var api = new Operations(11)
        {
            OnBegin = () =>
            {
                Assert.True(NativeWindowModalSession.TryPumpEvents());
                Assert.True(NativeWindowModalSession.RetainsWindow(new(NativeWindowKind.Cocoa, 11, 0, "Fixture")));
                Assert.Throws<InvalidOperationException>(() => NativeWindowModalSession.TryBegin(nested, out _));
            }
        };
        Assert.True(NativeWindowModalSession.TryBegin(api, out var session));
        using (session) Assert.Equal(new[] { "Release" }, nested.Calls);
    }

    [Fact]
    public void ForeignThreadCannotReleaseTheNativeLease()
    {
        Assert.True(NativeWindowModalSession.TryBegin(new Operations(11), out var session));
        using (session)
        {
            Exception? failure = null;
            var thread = new Thread(() => failure = Record.Exception(session!.Dispose));
            thread.Start();
            thread.Join();
            Assert.IsType<InvalidOperationException>(failure);
            Assert.False(session!.IsReleased);
        }
    }

    [Fact]
    public void WindowReleaseCompletesAfterPollEndAndNativeIdentityRelease()
    {
        var window = CocoaWindow(11);
        var api = new Operations(11);
        Assert.True(NativeWindowModalSession.TryBegin(api, out var session));
        using (session)
        {
            api.OnEnd = api.OnRelease = () => Assert.True(NativeWindowModalSession.RetainsWindow(window));
            api.OnPoll = () =>
            {
                Assert.True(NativeWindowModalSession.TryReleaseWindow(window, () =>
                {
                    Assert.True(session!.IsReleased);
                    Assert.False(NativeWindowModalSession.RetainsWindow(window));
                    Assert.False(NativeWindowModalSession.TryPumpEvents());
                    api.Calls.Add("Hidden");
                }));
                Assert.Equal(new[] { "Begin", "Poll" }, api.Calls);
            };
            Assert.True(NativeWindowModalSession.TryPumpEvents());
        }
        Assert.Equal(new[] { "Begin", "Poll", "End", "Release", "Hidden" }, api.Calls);
    }

    [Fact]
    public void WindowReleaseWaitsForAllMatchingLeasesAndUnrelatedNestedWindow()
    {
        Assert.True(NativeWindowModalSession.TryBegin(new Operations(11), out var first));
        using (first)
        {
            Assert.True(NativeWindowModalSession.TryBegin(new Operations(11), out var second));
            using (second)
            {
                Assert.True(NativeWindowModalSession.TryBegin(new Operations(22), out var third));
                int completed = 0;
                using (third)
                {
                    Assert.True(NativeWindowModalSession.TryReleaseWindow(CocoaWindow(11), () =>
                    {
                        Assert.True(first!.IsReleased && second!.IsReleased);
                        completed++;
                    }));
                    Assert.Equal(0, completed);
                    Assert.False(third!.IsReleased);
                }
                Assert.Equal(1, completed);
            }
        }
        Assert.False(NativeWindowModalSession.IsActive);
    }

    [Fact]
    public void CallbackFailureDoesNotSkipOtherCallbacksOrReadyParentRelease()
    {
        Assert.True(NativeWindowModalSession.TryBegin(new Operations(11), out var first));
        using (first)
        {
            var inner = new Operations(22);
            Assert.True(NativeWindowModalSession.TryBegin(inner, out var second));
            using (second)
            {
                var calls = new List<string>();
                var failure = new InvalidOperationException("Callback failed.");
                inner.OnPoll = () =>
                {
                    Assert.True(NativeWindowModalSession.TryReleaseWindow(CocoaWindow(11), () => calls.Add("Outer")));
                    Assert.True(NativeWindowModalSession.TryReleaseWindow(CocoaWindow(22), () => throw failure));
                    Assert.True(NativeWindowModalSession.TryReleaseWindow(CocoaWindow(22), () => calls.Add("Inner")));
                };
                Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => NativeWindowModalSession.TryPumpEvents()));
                Assert.Equal(new[] { "Inner", "Outer" }, calls);
                Assert.True(first!.IsReleased && second!.IsReleased);
            }
        }
    }

    [Fact]
    public void FailedNativeEndRetainsHostAndNeverRetriesAnUncertainNativeToken()
    {
        // A native End failure faults that thread's coordinator deliberately.
        // Isolate the fake failure rather than resetting product lifetime state.
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() =>
        {
            var api = new Operations(11);
            Assert.True(NativeWindowModalSession.TryBegin(api, out var session));
            int completed = 0;
            var nativeFailure = new InvalidOperationException("End failed.");
            api.OnEnd = () => throw nativeFailure;
            Assert.Same(nativeFailure, Assert.Throws<InvalidOperationException>(() =>
                NativeWindowModalSession.TryReleaseWindow(CocoaWindow(11), () => completed++)));
            Assert.False(session!.IsReleased);
            Assert.True(NativeWindowModalSession.RetainsWindow(CocoaWindow(11)));
            Assert.Same(nativeFailure, Assert.Throws<InvalidOperationException>(() => NativeWindowModalSession.TryPumpEvents()));
            Assert.Same(nativeFailure, Assert.Throws<InvalidOperationException>(session.Dispose));
            Assert.Same(nativeFailure, Assert.Throws<InvalidOperationException>(() =>
                NativeWindowModalSession.TryReleaseWindow(CocoaWindow(11), () => completed++)));
            var rejected = new Operations(22);
            Assert.Same(nativeFailure, Assert.Throws<InvalidOperationException>(() =>
                NativeWindowModalSession.TryBegin(rejected, out _)));
            Assert.Equal(new[] { "Release" }, rejected.Calls);
            Assert.Equal(new[] { "Begin", "End" }, api.Calls);
            Assert.Equal(0, completed);
        }));
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void FailedIdentityReleaseDoesNotPublishSuccessfulCompletion()
    {
        var api = new Operations(11) { OnRelease = () => throw new InvalidOperationException("Release failed.") };
        Assert.True(NativeWindowModalSession.TryBegin(api, out var session));
        using (session)
        {
            int completed = 0;
            Assert.Throws<InvalidOperationException>(() =>
                NativeWindowModalSession.TryReleaseWindow(CocoaWindow(11), () => completed++));
            Assert.Equal(0, completed);
            Assert.True(session!.IsReleased); // End succeeded; this is not a success notification.
            Assert.False(NativeWindowModalSession.IsActive);
        }
    }

    [Fact]
    public void OuterPollUnwindDoesNotRetryFailedNestedEnd()
    {
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() =>
        {
            var outer = new Operations(11);
            var inner = new Operations(22) { OnEnd = () => throw new InvalidOperationException("End failed.") };
            Assert.True(NativeWindowModalSession.TryBegin(outer, out _));
            outer.OnPoll = () =>
            {
                Assert.True(NativeWindowModalSession.TryBegin(inner, out var nested));
                Assert.Throws<InvalidOperationException>(nested!.Dispose);
            };
            Assert.Throws<InvalidOperationException>(() => NativeWindowModalSession.TryPumpEvents());
            Assert.Equal(new[] { "Begin", "End" }, inner.Calls);
            Assert.Equal(new[] { "Begin", "Poll" }, outer.Calls);
            Assert.True(NativeWindowModalSession.RetainsWindow(CocoaWindow(11)));
            Assert.True(NativeWindowModalSession.RetainsWindow(CocoaWindow(22)));
        }));
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void CompletionCanEnterAnotherSessionAfterParentIdentityIsRestored()
    {
        Assert.True(NativeWindowModalSession.TryBegin(new Operations(11), out var outer));
        using (outer)
        {
            Assert.True(NativeWindowModalSession.TryBegin(new Operations(22), out var inner));
            using (inner)
            {
                Assert.True(NativeWindowModalSession.TryReleaseWindow(CocoaWindow(22), () =>
                {
                    Assert.True(NativeWindowModalSession.RetainsWindow(CocoaWindow(11)));
                    Assert.False(NativeWindowModalSession.RetainsWindow(CocoaWindow(22)));
                    Assert.True(NativeWindowModalSession.TryBegin(new Operations(33), out var replacement));
                    using (replacement) Assert.True(NativeWindowModalSession.TryPumpEvents());
                }));
            }
            Assert.True(NativeWindowModalSession.TryPumpEvents());
        }
    }

    [Fact]
    public void UnownedWindowDoesNotAcquireOrInvokeReleaseCallback()
    {
        int completed = 0;
        Assert.True(NativeWindowModalSession.TryBegin(new Operations(11), out var session));
        using (session)
        {
            Assert.False(NativeWindowModalSession.TryReleaseWindow(CocoaWindow(22), () => completed++));
            Assert.False(NativeWindowModalSession.TryReleaseWindow(new(NativeWindowKind.Win32, 11, 0, "Fixture"), () => completed++));
            Assert.Equal(0, completed);
            Assert.False(session!.IsReleased);
        }
    }

    private static NativeWindowHandle CocoaWindow(nint value) => new(NativeWindowKind.Cocoa, value, 0, "Fixture");

    private sealed class Operations(nint identity) : INativeModalSessionOperations
    {
        public List<string> Calls { get; } = [];
        public Action? OnBegin { get; init; }
        public Action? OnPoll { get; set; }
        public Action? OnEnd { get; set; }
        public Action? OnRelease { get; set; }
        public nint Response { get; init; } = NativeWindowModalSession.ContinueResponse;
        public nint Window => identity;
        public nint Begin() { Calls.Add("Begin"); OnBegin?.Invoke(); return identity; }
        public nint Poll(nint session)
        {
            Assert.Equal(identity, session);
            Calls.Add("Poll"); OnPoll?.Invoke(); return Response;
        }
        public void End(nint session) { Assert.Equal(identity, session); Calls.Add("End"); OnEnd?.Invoke(); }
        public void Dispose() { Calls.Add("Release"); OnRelease?.Invoke(); }
    }
}
