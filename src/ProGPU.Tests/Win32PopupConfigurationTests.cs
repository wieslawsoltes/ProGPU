using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public class Win32PopupConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguresOwnedNonactivatingToolPopupWithoutDiscardingUnrelatedStyles(bool raisedWindowEdge)
    {
        var api = new FakeOperations();
        if (raisedWindowEdge) api.Values[-20] |= 0x00000100;
        Assert.True(Win32PopupConfiguration.Apply(11, 22, ref api));
        Assert.Equal((nint)11, api.Values[-8]);
        Assert.Equal(0x82000000u, unchecked((uint)api.Values[-16])); // retain ClipChildren
        Assert.Equal(0x08080080u, unchecked((uint)api.Values[-20])); // retain Layered
        Assert.Equal(new[] { -8, -16, -20, 1, 0 }, api.Writes);
        Assert.True(api.HookInstalled);
        Assert.True(api.IdentityEnded);
    }

    [Theory]
    [InlineData(1, (int)Win32PopupConfigurationFailure.OwnerWrite)]
    [InlineData(2, (int)Win32PopupConfigurationFailure.PopupStyleWrite)]
    [InlineData(3, (int)Win32PopupConfigurationFailure.PopupExtendedStyleWrite)]
    [InlineData(4, (int)Win32PopupConfigurationFailure.NonActivationHook)]
    [InlineData(5, (int)Win32PopupConfigurationFailure.FrameRefresh)]
    public void AnyApplyFailureRestoresAllOriginalAttributes(int failingCall,
        int expectedFailure)
    {
        var api = new FakeOperations { FailingCall = failingCall };
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api, out var failure));
        Assert.Equal((Win32PopupConfigurationFailure)expectedFailure, failure);
        Assert.Equal((nint)33, api.Values[-8]);
        Assert.Equal((nint)0x02cf0000, api.Values[-16]);
        Assert.Equal((nint)0x000c0000, api.Values[-20]);
        Assert.Equal(new[] { -20, -16, -8, 0 }, api.Writes.TakeLast(4));
        Assert.False(api.HookInstalled);
    }

    [Fact]
    public void FailedBorderlessConfigurationRestoresOriginalRaisedEdge()
    {
        var api = new FakeOperations { FailingCall = 5 };
        api.Values[-20] |= 0x00000100;
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api, out var failure));
        Assert.Equal(Win32PopupConfigurationFailure.FrameRefresh, failure);
        Assert.Equal((nint)0x000c0100, api.Values[-20]); // original edge, app-window and layered bits
        Assert.Equal((nint)0x02cf0000, api.Values[-16]);
        Assert.Equal((nint)33, api.Values[-8]);
        Assert.False(api.HookInstalled);
    }

    [Theory]
    [InlineData(0x40000000u, (int)Win32PopupConfigurationFailure.ChildPopup)]
    [InlineData(0x10000000u, (int)Win32PopupConfigurationFailure.VisiblePopup)]
    public void ChildOrVisiblePopupIsRejectedBeforeMutation(uint style,
        int expectedFailure)
    {
        var api = new FakeOperations();
        api.Values[-16] = unchecked((nint)(int)style);
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api, out var failure));
        Assert.Equal((Win32PopupConfigurationFailure)expectedFailure, failure);
        Assert.Empty(api.Writes);
    }

    [Fact]
    public void ForeignWindowsOrInvalidHandlesAreRejectedBeforeMutation()
    {
        var api = new FakeOperations { Local = false };
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api, out var foreignFailure));
        Assert.Equal(Win32PopupConfigurationFailure.NonlocalWindows, foreignFailure);
        Assert.False(Win32PopupConfiguration.Apply(0, 22, ref api, out var zeroFailure));
        Assert.Equal(Win32PopupConfigurationFailure.InvalidIdentity, zeroFailure);
        Assert.False(Win32PopupConfiguration.Apply(11, 11, ref api, out var sameFailure));
        Assert.Equal(Win32PopupConfigurationFailure.InvalidIdentity, sameFailure);
        Assert.Empty(api.Writes);
        Assert.False(NativePopupWindow.TryConfigureOwner(NativeWindowHandle.Empty, NativeWindowHandle.Empty));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("visible")]
    [InlineData("style")]
    [InlineData("extended")]
    [InlineData("child-owner")]
    [InlineData("destroyed")]
    [InlineData("read-failure")]
    [InlineData("hook")]
    public void ReentrantFrameChangesCannotPublishSuccessfulConfiguration(string change)
    {
        var api = new FakeOperations
        {
            OnRefresh = state =>
            {
                switch (change)
                {
                    case "owner": state.Values[-8] = 44; break;
                    case "visible": state.Values[-16] |= 0x10000000; break;
                    case "style": state.Values[-16] ^= 0x02000000; break;
                    case "extended": state.Values[-20] &= ~0x08000000; break;
                    case "child-owner": state.OwnerStyle = 0x40000000; break;
                    case "destroyed": state.Local = false; break;
                    case "read-failure": state.ReadSucceeds = false; break;
                    case "hook": state.HookInstalled = false; break;
                }
            }
        };
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api, out var failure));
        Assert.Equal(Win32PopupConfigurationFailure.ConfigurationChanged, failure);
        if (change == "destroyed")
        {
            Assert.Equal(new[] { -8, -16, -20, 1, 0 }, api.Writes);
            return; // Invalid/foreign HWNDs must never receive rollback writes.
        }
        Assert.Equal((nint)33, api.Values[-8]);
        Assert.Equal((nint)0x02cf0000, api.Values[-16]);
        Assert.Equal((nint)0x000c0000, api.Values[-20]);
        Assert.Equal(new[] { -20, -16, -8, 0 }, api.Writes.TakeLast(4));
        Assert.False(api.HookInstalled);
    }

    [Fact]
    public void RejectedReconfigurationRetainsTheExistingLifetimeHook()
    {
        var api = new FakeOperations { HookInstalled = true, FailingCall = 5 };
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api, out var failure));
        Assert.Equal(Win32PopupConfigurationFailure.FrameRefresh, failure);
        Assert.True(api.HookInstalled);
    }

    [Fact]
    public void MissingLifetimeTrackingRejectsBeforeAttributeMutation()
    {
        var api = new FakeOperations { IdentityAccepted = false };
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api, out var failure));
        Assert.Equal(Win32PopupConfigurationFailure.ConfigurationIdentity, failure);
        Assert.Empty(api.Writes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SameThreadHandleReuseDuringAWriteCannotReceiveFurtherMutations(int replacedAfterWrite)
    {
        var api = new FakeOperations
        {
            OnWrite = state =>
            {
                if (state.Writes.Count != replacedAfterWrite) return;
                state.IdentityAlive = false; // Numeric HWND/thread/process all remain unchanged.
                state.Values[-8] = 77;
                state.Values[-16] = 0x1234;
                state.Values[-20] = 0x5678;
            }
        };
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api, out var failure));
        Assert.Equal(Win32PopupConfigurationFailure.ConfigurationChanged, failure);
        Assert.Equal(new[] { -8, -16, -20 }.Take(replacedAfterWrite), api.Writes);
        Assert.Equal((nint)77, api.Values[-8]);
        Assert.Equal((nint)0x1234, api.Values[-16]);
        Assert.Equal((nint)0x5678, api.Values[-20]);
        Assert.True(api.IdentityEnded);
    }

    [Fact]
    public void SameThreadHandleReuseDuringRollbackStopsRemainingWrites()
    {
        var api = new FakeOperations
        {
            FailingCall = 2,
            OnWrite = state =>
            {
                if (state.Writes.Count != 3) return;
                state.IdentityAlive = false;
                state.Values[-8] = 77;
                state.Values[-16] = 0x1234;
                state.Values[-20] = 0x5678;
            }
        };
        Assert.False(Win32PopupConfiguration.Apply(11, 22, ref api, out var failure));
        Assert.Equal(Win32PopupConfigurationFailure.PopupStyleWrite, failure);
        Assert.Equal(new[] { -8, -16, -20 }, api.Writes);
        Assert.Equal((nint)77, api.Values[-8]);
        Assert.Equal((nint)0x1234, api.Values[-16]);
        Assert.Equal((nint)0x5678, api.Values[-20]);
        Assert.True(api.IdentityEnded);
    }

    private sealed class FakeOperations : IWin32PopupOperations
    {
        public readonly Dictionary<int, nint> Values = new()
        { [-8] = 33, [-16] = 0x02cf0000, [-20] = 0x000c0000 };
        public readonly List<int> Writes = [];
        public int FailingCall;
        public bool Local = true;
        public bool HookInstalled;
        public nint OwnerStyle;
        public bool ReadSucceeds = true;
        public Action<FakeOperations>? OnRefresh;
        public Action<FakeOperations>? OnWrite;
        public bool IdentityAccepted = true;
        public bool IdentityAlive;
        public bool IdentityEnded;
        public bool BeginConfigurationIdentity(nint owner, nint popup) => IdentityAlive = IdentityAccepted;
        public bool HasConfigurationIdentity(nint owner, nint popup) => Local && IdentityAlive;
        public void EndConfigurationIdentity(nint owner, nint popup) { IdentityAlive = false; IdentityEnded = true; }
        public bool InstallNonActivationHook(nint window)
        {
            Writes.Add(1);
            return HookInstalled = Writes.Count != FailingCall;
        }
        public void RemoveNonActivationHook(nint window) => HookInstalled = false;
        public bool HasNonActivationHook(nint window) => HookInstalled;
        public bool AreLocalWindows(nint owner, nint popup) => Local;
        public bool TryRead(nint window, int index, out nint value)
        {
            value = window == 11 ? OwnerStyle : Values[index];
            return ReadSucceeds;
        }
        public bool TryWrite(nint window, int index, nint value)
        {
            Writes.Add(index);
            if (Writes.Count == FailingCall) return false;
            Values[index] = value;
            OnWrite?.Invoke(this);
            return true;
        }
        public bool RefreshFrame(nint window)
        {
            Writes.Add(0);
            var callback = OnRefresh;
            OnRefresh = null;
            callback?.Invoke(this);
            return Writes.Count != FailingCall;
        }
    }
}
