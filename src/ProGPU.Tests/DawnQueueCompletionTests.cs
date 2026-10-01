using ProGPU.Backend.Dawn;
using W = WebGpuSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class DawnQueueCompletionTests
{
    [Fact]
    public void ActualQueueWaitRetainsNativeOwnershipAndRequiresRealSuccess()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "ProGPU.Backend.Dawn", "DawnGpuContext.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root.FullName, "src", "ProGPU.Backend.Dawn", "DawnGpuContext.cs"));
        int start = source.IndexOf("private static void WaitForQueue(", StringComparison.Ordinal);
        int end = source.IndexOf("private static void Wait(", start, StringComparison.Ordinal);
        string wait = source[start..end];
        Assert.Contains("Mode = W.CallbackMode.AllowSpontaneous", wait);
        Assert.Contains("state.BeginNativeUse()", wait);
        Assert.Contains("queue.OnSubmittedWorkDone(callback)", wait);
        Assert.Contains("state.CancelUnqueuedNativeUse(); throw;", wait);
        Assert.Contains("Wait(instance, future, \"wait for submitted Dawn work\");", wait);
        Assert.Contains("state.RequireSuccess();", wait);
        Assert.Contains("finally { state.EndManagedUse(); }", wait);
        Assert.DoesNotContain(".Free()", wait);
        Assert.Contains("instance.WaitAny(1, &wait, ulong.MaxValue)", source);
        string consumer = File.ReadAllText(Path.Combine(root.FullName, "tests", "ProGPU.DawnSystemWarp.Conformance", "Program.cs"));
        Assert.Contains("DawnGpuContext.VerifyDawnQueueWaitAbandonmentForDiagnostics();", consumer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackAndManagedReturnEachRetainTheirOwnUse(bool callbackFirst)
    {
        var state = new DawnQueueCompletion();
        Assert.NotEqual(0, state.BeginNativeUse());
        if (callbackFirst)
        {
            state.Complete(W.QueueWorkDoneStatus.Success, "native completion");
            Assert.Equal((1, 0), state.Retirement);
            state.EndManagedUse();
        }
        else
        {
            state.EndManagedUse();
            Assert.Equal((0, 0), state.Retirement);
            Assert.Throws<InvalidOperationException>(state.RequireSuccess);
            state.Complete(W.QueueWorkDoneStatus.Success, "native completion");
        }
        state.RequireSuccess();
        state.EndManagedUse();
        Assert.Equal((1, 1), state.Retirement);
        Assert.Throws<InvalidOperationException>(() => state.BeginNativeUse());
    }

    [Theory]
    [InlineData(W.QueueWorkDoneStatus.Error)]
    [InlineData(W.QueueWorkDoneStatus.CallbackCancelled)]
    public void NativeFailureRetiresButNeverBecomesSuccessfulWork(W.QueueWorkDoneStatus status)
    {
        var state = new DawnQueueCompletion();
        state.BeginNativeUse();
        state.EndManagedUse();
        state.Complete(status, "original native message");
        var error = Assert.Throws<InvalidOperationException>(state.RequireSuccess);
        Assert.Contains(status.ToString(), error.Message);
        Assert.Contains("original native message", error.Message);
        Assert.Equal((1, 1), state.Retirement);
    }

    [Fact]
    public void DecoderFailureRetiresAndRemainsObservable()
    {
        var state = new DawnQueueCompletion();
        var original = new InvalidOperationException("decode failure");
        state.BeginNativeUse();
        state.Complete(W.QueueWorkDoneStatus.Success, string.Empty, original);
        state.EndManagedUse();
        Assert.Same(original, Assert.Throws<InvalidOperationException>(state.RequireSuccess).InnerException);
        Assert.Equal((1, 1), state.Retirement);
    }

    [Fact]
    public void FailedImportRetiresWithoutInventingCallbackOrCompletion()
    {
        var state = new DawnQueueCompletion();
        state.BeginNativeUse();
        state.CancelUnqueuedNativeUse();
        state.EndManagedUse();
        state.CancelUnqueuedNativeUse();
        state.EndManagedUse();
        Assert.Equal((0, 1), state.Retirement);
        Assert.Throws<InvalidOperationException>(state.RequireSuccess);
    }

    [Fact]
    public void ConcurrentReturnAndCompletionRetireExactlyOnce()
    {
        for (int index = 0; index < 32; index++)
        {
            var state = new DawnQueueCompletion();
            state.BeginNativeUse();
            Parallel.Invoke(state.EndManagedUse,
                () => state.Complete(W.QueueWorkDoneStatus.Success, string.Empty));
            state.RequireSuccess();
            Assert.Equal((1, 1), state.Retirement);
        }
    }
}
