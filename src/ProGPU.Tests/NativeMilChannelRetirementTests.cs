using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeMilChannelRetirementTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PreEntryImportFailureClosesAdmissionAndRetriesOnlyExactTeardown(int failureKind)
    {
        Exception original = failureKind switch
        {
            0 => new DllNotFoundException("test import unavailable"),
            1 => new EntryPointNotFoundException("test destroy export unavailable"),
            _ => new BadImageFormatException("test import architecture unavailable")
        };
        int calls = 0;
        var state = new RetirementState((nint)0x1234);
        Action<nint> release = handle =>
        {
            Assert.Equal((nint)0x1234, handle);
            Assert.True(state.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => state.GetHandle());
            if (++calls == 1) throw original;
        };

        Assert.Equal((nint)0x1234, state.GetHandle());
        Assert.Same(original, Assert.Throws(original.GetType(), () => state.Dispose(release)));
        Assert.True(state.IsDisposed);
        Assert.Equal((nint)0x1234, state.Handle);
        Assert.Null(state.UnknownCompletion);
        Assert.Throws<ObjectDisposedException>(() => state.GetHandle());

        state.Dispose(release);
        Assert.Equal(2, calls);
        Assert.Equal((nint)0, state.Handle);
        state.Dispose(release);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ReentrantDisposeCannotPublishAdmissionOrDispatchDestroyTwice()
    {
        int calls = 0;
        var state = new RetirementState((nint)0x2345);
        Action<nint>? release = null;
        release = handle =>
        {
            Assert.Equal((nint)0x2345, handle);
            Assert.Equal(1, ++calls);
            Assert.Throws<ObjectDisposedException>(() => state.GetHandle());
            state.Dispose(release!);
            Assert.Equal((nint)0x2345, state.Handle);
            Assert.True(state.IsDisposed);
        };

        state.Dispose(release);
        state.Dispose(release);
        Assert.Equal(1, calls);
        Assert.Equal((nint)0, state.Handle);
    }

    [Fact]
    public void ReentrantDisposeDuringFailedBindingLeavesExactHandleForLaterRetry()
    {
        int calls = 0;
        var state = new RetirementState((nint)0x3456);
        var original = new EntryPointNotFoundException("test pre-entry failure");
        Action<nint>? release = null;
        release = handle =>
        {
            Assert.Equal((nint)0x3456, handle);
            if (++calls == 1)
            {
                state.Dispose(release!);
                throw original;
            }
        };

        Assert.Same(original, Assert.Throws<EntryPointNotFoundException>(() => state.Dispose(release)));
        Assert.Equal(1, calls);
        Assert.Equal((nint)0x3456, state.Handle);
        state.Dispose(release);
        Assert.Equal(2, calls);
        Assert.Equal((nint)0, state.Handle);
    }

    [Fact]
    public async Task ConcurrentDisposersWaitForOneDestroyAndNeverReadmit()
    {
        using var entered = new ManualResetEventSlim();
        using var exit = new ManualResetEventSlim();
        using var otherStarted = new ManualResetEventSlim();
        using var otherCompleted = new ManualResetEventSlim();
        int calls = 0;
        var state = new RetirementState((nint)0x4567);
        Action<nint> release = handle =>
        {
            Assert.Equal((nint)0x4567, handle);
            Assert.Equal(1, Interlocked.Increment(ref calls));
            entered.Set();
            if (!exit.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        Task first = Task.Run(() => state.Dispose(release));
        Task other = Task.Run(() =>
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            otherStarted.Set();
            state.Dispose(release);
            otherCompleted.Set();
        });
        try
        {
            Assert.True(otherStarted.Wait(TimeSpan.FromSeconds(10)));
            // A concurrent caller must not report successful retirement while
            // the original consuming import is still in progress.
            Assert.False(otherCompleted.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.True(state.IsDisposed);
            Assert.Equal((nint)0x4567, state.Handle);
            Assert.Throws<ObjectDisposedException>(() => state.GetHandle());
        }
        finally { exit.Set(); }

        await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(otherCompleted.IsSet);
        Assert.Equal(1, calls);
        Assert.Equal((nint)0, state.Handle);
    }

    [Fact]
    public async Task ConcurrentRetryAfterPreEntryFailureStillDestroysOnlyOnce()
    {
        int attempts = 0, completed = 0;
        var state = new RetirementState((nint)0x5678);
        Action<nint> release = handle =>
        {
            Assert.Equal((nint)0x5678, handle);
            if (Interlocked.Increment(ref attempts) == 1)
                throw new DllNotFoundException("test pre-entry failure");
            Interlocked.Increment(ref completed);
        };
        Assert.Throws<DllNotFoundException>(() => state.Dispose(release));
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => state.Dispose(release))))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, attempts);
        Assert.Equal(1, completed);
        Assert.Equal((nint)0, state.Handle);
    }

    [Fact]
    public void UnknownCompletionPreservesOriginalFailureWithoutRedispatch()
    {
        int calls = 0;
        var state = new RetirementState((nint)0x6789);
        var original = new InvalidOperationException("completion cannot be established");
        Action<nint> release = _ =>
        {
            ++calls;
            // Models an unclassified fault, potentially after consumption.
            throw original;
        };

        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => state.Dispose(release)));
        Assert.Equal((nint)0x6789, state.Handle);
        Assert.True(state.IsDisposed);
        Assert.Same(original, state.UnknownCompletion);
        Assert.Throws<ObjectDisposedException>(() => state.GetHandle());
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => state.Dispose(release)));
        Assert.Equal(1, calls);
    }

    // Controllable device-free state for the SAME primitive used by the public
    // channel. No production fake-handle constructor or provider is introduced.
    private sealed class RetirementState(nint handle)
    {
        private readonly object _gate = new();
        private nint _handle = handle;
        private int _disposeState;
        private bool _destroying;
        private Exception? _unknownCompletion;

        internal nint Handle => Volatile.Read(ref _handle);
        internal bool IsDisposed => Volatile.Read(ref _disposeState) != 0;
        internal Exception? UnknownCompletion => _unknownCompletion;
        internal nint GetHandle() => NativeMilChannelRetirement.GetHandle(ref _handle, ref _disposeState, this);
        internal void Dispose(Action<nint> release)
            => NativeMilChannelRetirement.Dispose(_gate, ref _handle,
                ref _disposeState, ref _destroying, ref _unknownCompletion, release);
    }
}
