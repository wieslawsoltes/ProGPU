using ProGPU.Backend;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeWindowSystemMenuTests
{
    [Theory]
    [InlineData(false, 0xF060u)]
    [InlineData(true, 0xF030u)]
    [InlineData(false, 123u)] // Preserve application-owned system-menu entries.
    public void SelectionPreservesCoordinatesAlignmentAndOnePostedCommand(bool rightAligned, uint command)
    {
        var api = new Operations { RightAligned = rightAligned, Command = command };
        Assert.True(Win32SystemMenu.Show(11, new(-1920, 240), ref api));
        Assert.Equal(new NativeWindowPoint(-1920, 240), api.Position);
        Assert.Equal(rightAligned ? 0x10Au : 0x102u, api.Flags);
        Assert.Equal(new[] { "Owner", "Track", "Owner", "Post" }, api.Calls);
        Assert.Equal((11, command), ((int)api.PostedOwner, api.PostedCommand));
    }

    [Fact]
    public void CancellationDoesNotPostACommand()
    {
        var api = new Operations { Command = 0 };
        Assert.True(Win32SystemMenu.Show(11, default, ref api));
        Assert.Equal(new[] { "Owner", "Track" }, api.Calls);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void RejectedOperationsDoNotReportSuccess(bool local, bool track, bool failPost)
    {
        var api = new Operations { Local = local, TrackAccepted = track, PostAccepted = !failPost };
        Assert.False(Win32SystemMenu.Show(11, default, ref api));
        if (!local) Assert.DoesNotContain("Track", api.Calls);
        if (!track) Assert.DoesNotContain("Post", api.Calls);
    }

    [Fact]
    public void OwnerMenuReplacementDuringModalTrackingPreventsPosting()
    {
        var api = new Operations { ReplaceMenuDuringTracking = true };
        Assert.False(Win32SystemMenu.Show(11, default, ref api));
        Assert.DoesNotContain("Post", api.Calls);
    }

    [Fact]
    public void InvalidOrUnsupportedNativeOwnershipIsRejected()
    {
        var api = new Operations();
        Assert.False(Win32SystemMenu.Show(0, default, ref api));
        Assert.Empty(api.Calls);
        Assert.False(NativeWindowSystemMenu.TryShow(NativeWindowHandle.Empty, default));
        Assert.False(NativeWindowSystemMenu.TryShow(new(NativeWindowKind.Wayland, 11, 0, "test"), default));
    }

    [Fact]
    public void TypedMenuCallbackIsOptionalAndKeepsActivationAndDesktopCoordinates()
    {
        var activation = new object();
        var legacy = new PortableWindowActivationCallbacks(value => value);
        Assert.Null(legacy.ShowSystemMenu);
        var callbacks = new PortableWindowActivationCallbacks(value => value)
        {
            ShowSystemMenu = (owner, x, y) => ReferenceEquals(owner, activation) && x == -12.5 && y == 24.25
        };
        Assert.True(callbacks.ShowSystemMenu(activation, -12.5, 24.25));
        Assert.False(callbacks.ShowSystemMenu(new object(), -12.5, 24.25));
    }

    private sealed class Operations : IWin32SystemMenuOperations
    {
        public bool Local = true, TrackAccepted = true, PostAccepted = true, ReplaceMenuDuringTracking;
        public uint Command = 0xF060;
        public bool RightAligned { get; init; }
        public List<string> Calls { get; } = [];
        public NativeWindowPoint Position;
        public uint Flags, PostedCommand;
        public nint PostedOwner;
        private nint _menu = 22;

        public bool TryGetLocalMenu(nint owner, out nint menu)
        { Calls.Add("Owner"); menu = _menu; return Local; }

        public bool TryTrack(nint owner, nint menu, NativeWindowPoint position, uint flags, out uint command)
        {
            Calls.Add("Track"); Position = position; Flags = flags; command = Command;
            if (ReplaceMenuDuringTracking) _menu = 33;
            return TrackAccepted;
        }

        public bool PostSystemCommand(nint owner, uint command)
        { Calls.Add("Post"); PostedOwner = owner; PostedCommand = command; return PostAccepted; }
    }
}
