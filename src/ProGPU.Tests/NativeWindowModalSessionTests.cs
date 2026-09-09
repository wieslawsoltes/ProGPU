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

    private sealed class Operations(nint identity) : INativeModalSessionOperations
    {
        public List<string> Calls { get; } = [];
        public Action? OnBegin { get; init; }
        public Action? OnPoll { get; set; }
        public nint Response { get; init; } = NativeWindowModalSession.ContinueResponse;
        public nint Window => identity;
        public nint Begin() { Calls.Add("Begin"); OnBegin?.Invoke(); return identity; }
        public nint Poll(nint session)
        {
            Assert.Equal(identity, session);
            Calls.Add("Poll"); OnPoll?.Invoke(); return Response;
        }
        public void End(nint session) { Assert.Equal(identity, session); Calls.Add("End"); }
        public void Dispose() => Calls.Add("Release");
    }
}
