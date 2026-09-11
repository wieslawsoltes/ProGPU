using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class Win32WindowEnabledStateTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AdmissionUsesResultingStateIncludingIdempotentRequests(bool initial, bool requested)
    {
        var operations = new Operations { Enabled = initial };
        Assert.True(Win32WindowEnabledState.Apply(123, requested, ref operations));
        Assert.Equal(requested, operations.Enabled);
        Assert.Equal(new[] { "Owner", "Set", "Owner", "Read" }, operations.Calls);
    }

    [Fact]
    public void MissingOrForeignWindowIsNotMutated()
    {
        var operations = new Operations();
        Assert.False(Win32WindowEnabledState.Apply(0, false, ref operations));
        Assert.Empty(operations.Calls);
        operations.Local = false;
        Assert.False(Win32WindowEnabledState.Apply(123, false, ref operations));
        Assert.Equal(new[] { "Owner" }, operations.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReentrantStateChangesOrDestructionDoNotReportSuccess(bool destroyed)
    {
        var operations = new Operations { DestroyDuringSet = destroyed, RejectState = !destroyed };
        Assert.False(Win32WindowEnabledState.Apply(123, false, ref operations));
        Assert.Equal(destroyed ? new[] { "Owner", "Set", "Owner" } : new[] { "Owner", "Set", "Owner", "Read" }, operations.Calls);
    }

    [Fact]
    public void OperationFailuresPropagateWithoutFallback()
    {
        var failure = new InvalidOperationException("Native callback failure.");
        var operations = new Operations { Failure = failure };
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            Win32WindowEnabledState.Apply(123, false, ref operations)));
        Assert.Equal(new[] { "Owner", "Set" }, operations.Calls);
    }

    private sealed class Operations : IWin32WindowEnabledOperations
    {
        public bool Local = true, Enabled = true, DestroyDuringSet, RejectState;
        public Exception? Failure;
        public List<string> Calls { get; } = [];
        public bool IsLocalWindow(nint window) { Calls.Add("Owner"); return Local; }
        public void SetEnabled(nint window, bool enabled)
        {
            Calls.Add("Set");
            if (Failure != null) throw Failure;
            if (DestroyDuringSet) Local = false;
            if (!RejectState) Enabled = enabled;
        }
        public bool IsEnabled(nint window) { Calls.Add("Read"); return Enabled; }
    }
}
