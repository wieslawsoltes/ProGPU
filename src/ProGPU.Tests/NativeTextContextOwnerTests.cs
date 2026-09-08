using ProGPU.Backend.Native;
using Xunit;

namespace Avalonia.ProGpu.UnitTests;

public sealed class NativeTextContextOwnerTests
{
    [Fact]
    public void NestedUsesDeferReentrantDisposalAndReleaseExactlyOnce()
    {
        int released = 0;
        var owner = new NativeTextContextOwner(123, handle =>
        {
            Assert.Equal((nint)123, handle);
            released++;
        });
        using (var first = owner.Acquire())
        {
            Assert.Equal((nint)123, first.Handle);
            using (var nested = owner.Acquire())
            {
                Assert.Equal(first.Handle, nested.Handle);
                owner.Dispose();
                Assert.Equal(0, released);
                Assert.Throws<ObjectDisposedException>(() => { using var rejected = owner.Acquire(); });
            }
            Assert.Equal(0, released);
        }
        Assert.Equal(1, released);
        owner.Dispose();
        Assert.Equal(1, released);
        Assert.Throws<ObjectDisposedException>(() => { using var rejected = owner.Acquire(); });
    }

    [Fact]
    public async Task ConcurrentOperationsNeverShareMutablePlanStorage()
    {
        int active = 0, completed = 0, released = 0;
        using var owner = new NativeTextContextOwner(456, _ =>
        {
            Assert.Equal(0, Volatile.Read(ref active));
            Interlocked.Increment(ref released);
        });
        Task[] workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int operation = 0; operation < 32; operation++)
            {
                using var use = owner.Acquire();
                Assert.Equal((nint)456, use.Handle);
                Assert.Equal(1, Interlocked.Increment(ref active));
                try
                {
                    Thread.Yield();
                    Assert.Equal(0, Volatile.Read(ref released));
                    Interlocked.Increment(ref completed);
                }
                finally { Interlocked.Decrement(ref active); }
            }
        })).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
        owner.Dispose();
        Assert.Equal(256, completed);
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task ConcurrentDisposeDoesNotReleaseAnActivePointer()
    {
        using var entered = new ManualResetEventSlim();
        using var exit = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        int operationFinished = 0, released = 0;
        var owner = new NativeTextContextOwner(789, _ =>
        {
            Assert.Equal(1, Volatile.Read(ref operationFinished));
            Interlocked.Increment(ref released);
        });
        Task operation = Task.Run(() =>
        {
            using var use = owner.Acquire();
            entered.Set();
            if (!exit.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            Assert.Equal((nint)789, use.Handle);
            Assert.Equal(0, Volatile.Read(ref released));
            Volatile.Write(ref operationFinished, 1);
        });
        Task disposal = Task.Run(() =>
        {
            if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            disposeStarted.Set();
            owner.Dispose();
        });
        try
        {
            Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, Volatile.Read(ref released));
        }
        finally { exit.Set(); }
        await Task.WhenAll(operation, disposal).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, released);
        owner.Dispose();
        Assert.Equal(1, released);
    }
}
