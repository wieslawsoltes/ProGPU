using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class X11PopupConfigurationTests
{
    [Fact]
    public void AllConfigurationStepsAndServerConfirmationAreRequired()
    {
        var operations = new Operations();
        Assert.True(X11PopupConfiguration.Apply(ref operations));
        Assert.Equal(new[] { "admit", "owner", "override", "types", "confirm" }, operations.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RejectedStepCannotBeHiddenByOtherSuccessfulSteps(int reject)
    {
        var operations = new Operations { Reject = reject };
        Assert.False(X11PopupConfiguration.Apply(ref operations));
        Assert.Equal(reject + 1, operations.Calls.Count);
        // In particular a rejected owner does not run the override operation,
        // whose success previously turned the WPF-local OR result into true.
    }

    [Fact]
    public void NativeFailurePropagatesForHostOwnedHiddenWindowDisposal()
    {
        var operations = new Operations { Throw = true };
        var error = Assert.Throws<InvalidOperationException>(() => X11PopupConfiguration.Apply(ref operations));
        Assert.Equal("native operation", error.Message);
        Assert.Single(operations.Calls);
    }

    [Fact]
    public void NonmatchingNativeIdentitiesAreRejectedWithoutXlib()
    {
        var owner = new NativeWindowHandle(NativeWindowKind.X11, 1, 10, "XID");
        Assert.False(NativePopupWindow.TryConfigureOwner(owner, owner));
        Assert.False(NativePopupWindow.TryConfigureOwner(owner, owner with { Handle = 2, Display = 11 }));
        Assert.False(NativePopupWindow.TryConfigureOwner(owner, owner with { Handle = 0 }));
        Assert.False(NativePopupWindow.TryConfigureOwner(owner, owner with { Handle = -1 }));
        Assert.False(NativePopupWindow.TryConfigureOwner(owner, owner with { Kind = NativeWindowKind.Wayland }));
    }

    private sealed class Operations : IX11PopupOperations
    {
        internal int Reject = -1;
        internal bool Throw;
        internal List<string> Calls { get; } = new();
        private bool Step(string name)
        {
            Calls.Add(name);
            if (Throw) throw new InvalidOperationException("native operation");
            return Calls.Count - 1 != Reject;
        }
        public bool AdmitHiddenPair() => Step("admit");
        public bool SetOwner() => Step("owner");
        public bool SetOverrideRedirect() => Step("override");
        public bool SetMenuTypes() => Step("types");
        public bool ConfirmHiddenConfiguration() => Step("confirm");
    }
}
