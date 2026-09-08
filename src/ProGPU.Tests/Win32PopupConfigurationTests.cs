using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public class Win32PopupConfigurationTests
{
    [Fact]
    public void ConfiguresOwnedNonactivatingToolPopupWithoutDiscardingUnrelatedStyles()
    {
        var api = new FakeOperations();
        Assert.True(Win32PopupConfiguration.Apply(11, 22, ref api));
        Assert.Equal((nint)11, api.Values[-8]);
        Assert.Equal(0x82000000u, unchecked((uint)api.Values[-16])); // retain ClipChildren
        Assert.Equal(0x08080080u, unchecked((uint)api.Values[-20])); // retain Layered
        Assert.Equal(new[] { -8, -16, -20, 1, 0 }, api.Writes);
        Assert.True(api.HookInstalled);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AnyApplyFailureRestoresAllOriginalAttributes(int failingCall)
    {
        var api = new FakeOperations { FailingCall = failingCall };
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api));
        Assert.Equal((nint)33, api.Values[-8]);
        Assert.Equal((nint)0x02cf0000, api.Values[-16]);
        Assert.Equal((nint)0x000c0000, api.Values[-20]);
        Assert.Equal(new[] { -20, -16, -8, 0 }, api.Writes.TakeLast(4));
        Assert.False(api.HookInstalled);
    }

    [Theory]
    [InlineData(0x40000000u)]
    [InlineData(0x10000000u)]
    public void ChildOrVisiblePopupIsRejectedBeforeMutation(uint style)
    {
        var api = new FakeOperations();
        api.Values[-16] = unchecked((nint)(int)style);
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api));
        Assert.Empty(api.Writes);
    }

    [Fact]
    public void ForeignWindowsOrInvalidHandlesAreRejectedBeforeMutation()
    {
        var api = new FakeOperations { Local = false };
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api));
        Assert.False(Win32PopupConfiguration.Apply(0, 22, ref api));
        Assert.False(Win32PopupConfiguration.Apply(11, 11, ref api));
        Assert.Empty(api.Writes);
        Assert.False(NativePopupWindow.TryConfigureOwner(NativeWindowHandle.Empty, NativeWindowHandle.Empty));
    }

    private sealed class FakeOperations : IWin32PopupOperations
    {
        public readonly Dictionary<int, nint> Values = new()
        { [-8] = 33, [-16] = 0x02cf0000, [-20] = 0x000c0000 };
        public readonly List<int> Writes = [];
        public int FailingCall;
        public bool Local = true;
        public bool HookInstalled;
        public bool InstallNonActivationHook(nint window)
        {
            Writes.Add(1);
            return HookInstalled = Writes.Count != FailingCall;
        }
        public void RemoveNonActivationHook(nint window) => HookInstalled = false;
        public bool AreLocalWindows(nint owner, nint popup) => Local;
        public bool TryRead(nint window, int index, out nint value)
        {
            value = window == 11 ? 0 : Values[index];
            return true;
        }
        public bool TryWrite(nint window, int index, nint value)
        {
            Writes.Add(index);
            if (Writes.Count == FailingCall) return false;
            Values[index] = value;
            return true;
        }
        public bool RefreshFrame(nint window)
        {
            Writes.Add(0);
            return Writes.Count != FailingCall;
        }
    }
}
