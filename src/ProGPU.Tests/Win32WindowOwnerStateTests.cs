using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class Win32WindowOwnerStateTests
{
    [Fact]
    public void OwnershipCanBeAssignedReplacedAndClearedWithoutRedundantWrites()
    {
        var api = new Operations();
        Assert.True(Win32WindowOwnerState.Apply(1, 2, ref api));
        Assert.True(Win32WindowOwnerState.Apply(1, 2, ref api));
        Assert.Equal(1, api.Writes);
        Assert.True(Win32WindowOwnerState.Apply(1, 3, ref api));
        Assert.True(Win32WindowOwnerState.Apply(1, 0, ref api));
        Assert.Equal((nint)0, api.Owners[1]);
        Assert.Equal(3, api.Writes);
    }

    [Fact]
    public void RejectsMissingForeignChildAndCyclicOwnersBeforeWriting()
    {
        var api = new Operations();
        Assert.False(Win32WindowOwnerState.Apply(0, 2, ref api));
        Assert.False(Win32WindowOwnerState.Apply(1, 1, ref api));
        Assert.False(Win32WindowOwnerState.Apply(1, 99, ref api));
        api.RejectedWindow = 2;
        Assert.False(Win32WindowOwnerState.Apply(1, 2, ref api));
        api.RejectedWindow = 0;
        api.Owners[2] = 3;
        api.Owners[3] = 1;
        Assert.False(Win32WindowOwnerState.Apply(1, 2, ref api));
        api.Owners[3] = 2; // Malformed external cycle, not involving the target.
        Assert.False(Win32WindowOwnerState.Apply(1, 2, ref api));
        Assert.Equal(0, api.Writes);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void FailedOrOverriddenNativeWritesDoNotReportSuccess(bool reject, bool overwrite)
    {
        var api = new Operations { RejectWrite = reject, Overwrite = overwrite };
        Assert.False(Win32WindowOwnerState.Apply(1, 2, ref api));
    }

    private sealed class Operations : IWin32WindowOwnerOperations
    {
        public Dictionary<nint, nint> Owners { get; } = new() { [1] = 0, [2] = 0, [3] = 0 };
        public nint RejectedWindow;
        public bool RejectWrite, Overwrite;
        public int Writes;
        public bool IsLocalTopLevel(nint window) => Owners.ContainsKey(window) && window != RejectedWindow;
        public bool TryGetOwner(nint window, out nint owner) => Owners.TryGetValue(window, out owner);
        public bool TrySetOwner(nint window, nint owner)
        {
            Writes++;
            if (RejectWrite) return false;
            Owners[window] = Overwrite ? 3 : owner;
            return true;
        }
    }
}
