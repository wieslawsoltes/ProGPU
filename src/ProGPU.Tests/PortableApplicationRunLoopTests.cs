using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableApplicationRunLoopTests
{
    [Fact]
    public void RetiredFirstHostTransfersToRemainingHostWithoutImplicitShutdown()
    {
        var state = new State { Hosts = [new object(), new object()] };
        var source = new Source(state);
        state.Run = host =>
        {
            Assert.Same(state.Hosts[0], host);
            state.Hosts.RemoveAt(0);
            if (state.Hosts.Count == 0) state.Shutdown = true;
        };
        PortableApplicationRunLoop.Run(ref source);
        Assert.Equal(2, state.Runs);
        Assert.Equal(0, state.Waits);
    }

    [Fact]
    public void ExplicitLifetimeCanWaitWithoutHostsAndResumeWithNewHost()
    {
        var state = new State();
        var source = new Source(state);
        state.Wait = () => state.Hosts.Add(new object());
        state.Run = _ => state.Shutdown = true;
        PortableApplicationRunLoop.Run(ref source);
        Assert.Equal(1, state.Waits);
        Assert.Equal(1, state.Runs);
    }

    [Fact]
    public void ShutdownFromHostlessWaitDoesNotEnterAnotherHost()
    {
        var state = new State();
        var source = new Source(state);
        state.Wait = () => state.Shutdown = true;
        PortableApplicationRunLoop.Run(ref source);
        Assert.Equal(0, state.Runs);
        Assert.Equal(1, state.Waits);
        state.Hosts.Add(new object());
        PortableApplicationRunLoop.Run(ref source);
        Assert.Equal(0, state.Runs);
    }

    [Fact]
    public void ActualApplicationShutdownStopsBeforeRemainingLiveHosts()
    {
        var state = new State { Hosts = [new object(), new object()] };
        var source = new Source(state);
        state.Run = _ => state.Shutdown = true;
        PortableApplicationRunLoop.Run(ref source);
        Assert.Equal(1, state.Runs);
        Assert.Equal(0, state.Waits);
        Assert.Equal(2, state.Hosts.Count);
    }

    [Fact]
    public void PrematureHostReturnFailsInsteadOfShutdownOrBusyRetry()
    {
        var state = new State { Hosts = [new object()] };
        var source = new Source(state);
        Assert.Throws<InvalidOperationException>(() => PortableApplicationRunLoop.Run(ref source));
        Assert.False(state.Shutdown);
        Assert.Equal(1, state.Runs);
    }

    [Fact]
    public void HostlessSpuriousReturnFailsInsteadOfBusyRetry()
    {
        var state = new State();
        var source = new Source(state);
        Assert.Throws<InvalidOperationException>(() => PortableApplicationRunLoop.Run(ref source));
        Assert.Equal(1, state.Waits);
    }

    [Fact]
    public void CallbackFailuresPropagateWithoutRetryOrLifetimeMutation()
    {
        var failure = new InvalidOperationException("Application callback failure");
        var state = new State { Hosts = [new object()], Run = _ => throw failure };
        var source = new Source(state);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => PortableApplicationRunLoop.Run(ref source)));
        Assert.Equal(1, state.Runs);
        Assert.False(state.Shutdown);
    }

    private sealed class State
    {
        internal List<object> Hosts = [];
        internal bool Shutdown;
        internal int Runs, Waits;
        internal Action<object>? Run;
        internal Action? Wait;
    }

    private readonly struct Source(State state) : IPortableApplicationRunLoopSource
    {
        public bool IsShutdownRequested => state.Shutdown;
        public object? FindRunHost() => state.Hosts.Count == 0 ? null : state.Hosts[0];
        public bool IsHostAlive(object host) => state.Hosts.Contains(host);
        public void RunHost(object host) { state.Runs++; state.Run?.Invoke(host); }
        public void WaitForHost() { state.Waits++; state.Wait?.Invoke(); }
    }
}
