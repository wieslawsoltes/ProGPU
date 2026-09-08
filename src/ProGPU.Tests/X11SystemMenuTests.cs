using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class X11SystemMenuTests
{
    private static NativeWindowHandle Owner => new(NativeWindowKind.X11, 17, 23, "XID");

    [Theory]
    [InlineData(-1920, 240)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void RequestPreservesOwnerRootDeviceAndSignedDesktopCoordinates(int x, int y)
    {
        var api = new Operations();
        Assert.True(X11SystemMenu.Show(Owner, new(x, y), ref api));
        Assert.Equal(new[] { "Target", "Open", "Device", "Send", "Close" }, api.Calls);
        Assert.Equal((nint)23, api.OwnerDisplay);
        Assert.Equal((nuint)17, api.PointerOwner);
        Assert.Equal((nuint)31, api.Root); // Not a guessed default-screen root.
        Assert.Equal((nint)41, api.Request.Display); // Not the host input connection.
        Assert.Equal((nuint)17, api.Request.Window);
        Assert.Equal((nuint)37, api.Request.MessageType);
        Assert.Equal(33, api.Request.Type);
        Assert.Equal(32, api.Request.Format);
        Assert.Equal((nint)9, api.Request.Device); // Not a hardcoded master-pointer ID.
        Assert.Equal((nint)x, api.Request.X);
        Assert.Equal((nint)y, api.Request.Y);
        Assert.Equal((nint)0, api.Request.Reserved0);
        Assert.Equal((nint)0, api.Request.Reserved1);
        Assert.Equal((nuint)0, api.Request.Serial);
        Assert.Equal(0, api.Request.SendEvent);
        Assert.Equal((nint)41, api.ClosedConnection);
    }

    [Theory]
    [InlineData(0, 23)]
    [InlineData(17, 0)]
    public void MissingOwnershipIsRejectedBeforeNativeAccess(int window, int display)
    {
        var api = new Operations();
        Assert.False(X11SystemMenu.Show(new(NativeWindowKind.X11, window, display, "XID"), default, ref api));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void OtherSurfaceKindsAreNotInterpretedAsXids()
    {
        var api = new Operations();
        Assert.False(X11SystemMenu.Show(Owner with { Kind = NativeWindowKind.Wayland }, default, ref api));
        Assert.Empty(api.Calls);
        if (IntPtr.Size == 8)
        {
            Assert.False(X11SystemMenu.Show(Owner with { Handle = unchecked((nint)(1UL << 40)) }, default, ref api));
            Assert.Empty(api.Calls);
        }
    }

    [Fact]
    public void MissingAdvertisementDoesNotOpenAnotherConnection()
    {
        var api = new Operations { TargetAccepted = false };
        Assert.False(X11SystemMenu.Show(Owner, default, ref api));
        Assert.Equal(new[] { "Target" }, api.Calls);
    }

    [Fact]
    public void ConnectionFailureDoesNotSendOrCloseTheHostDisplay()
    {
        var api = new Operations { Connection = 0 };
        Assert.False(X11SystemMenu.Show(Owner, default, ref api));
        Assert.Equal(new[] { "Target", "Open" }, api.Calls);
    }

    [Theory]
    [InlineData(false, 9)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public void MissingClientPointerFailsAndClosesOnlyTemporaryConnection(bool accepted, int device)
    {
        var api = new Operations { PointerAccepted = accepted, Device = device };
        Assert.False(X11SystemMenu.Show(Owner, default, ref api));
        Assert.Equal(new[] { "Target", "Open", "Device", "Close" }, api.Calls);
        Assert.Equal((nint)41, api.ClosedConnection);
    }

    [Fact]
    public void RejectedSendIsNotSuccessfulDisplay()
    {
        var api = new Operations { SendAccepted = false };
        Assert.False(X11SystemMenu.Show(Owner, default, ref api));
        Assert.Equal(new[] { "Target", "Open", "Device", "Send", "Close" }, api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureStillReleasesConnectionAndDoesNotHideUnrelatedErrors(bool duringSend)
    {
        var api = new Operations { ThrowAt = duringSend ? "Send" : "Device" };
        Assert.Throws<InvalidOperationException>(() => X11SystemMenu.Show(Owner, default, ref api));
        Assert.Equal("Close", api.Calls[^1]);
        Assert.Equal((nint)41, api.ClosedConnection);
    }

    [Fact]
    public unsafe void XlibLayoutUsesNativeLongSlotsAndFitsFullEventStorage()
    {
        X11MenuMessage message = default;
        Assert.Equal(IntPtr.Size == 8 ? 96 : 48, sizeof(X11MenuMessage));
        Assert.Equal(IntPtr.Size == 8 ? 56 : 28, (byte*)&message.Device - (byte*)&message);
        Assert.Equal(IntPtr.Size, (byte*)&message.X - (byte*)&message.Device);
        Assert.True(sizeof(X11MenuMessage) <= X11SystemMenu.EventStorageWords * IntPtr.Size);
        Assert.Equal(0x180000, X11SystemMenu.RootEventMask);
    }

    [Fact]
    public unsafe void Format32PropertiesUseNativeLongStrideAndLeaveTailUntouched()
    {
        nuint* data = stackalloc nuint[] { 1, uint.MaxValue, 37 };
        Span<nuint> result = stackalloc nuint[] { 99, 99, 99, 99 };
        Assert.True(X11SystemMenu.CopyProperty32(0, 4, 4, 32, 3, 0, (nint)data, result, out int count));
        Assert.Equal(3, count);
        Assert.Equal(new nuint[] { 1, uint.MaxValue, 37, 99 }, result.ToArray());
    }

    [Theory]
    [InlineData(1, 4, 32, 1, 0)]
    [InlineData(0, 5, 32, 1, 0)]
    [InlineData(0, 4, 8, 1, 0)]
    [InlineData(0, 4, 32, 2, 0)]
    [InlineData(0, 4, 32, 1, 4)]
    public unsafe void MalformedOrTruncatedPropertiesDoNotPublishPartialData(int status, int type, int format, int items, int remaining)
    {
        nuint value = 37;
        Span<nuint> result = stackalloc nuint[] { 99 };
        Assert.False(X11SystemMenu.CopyProperty32(status, 4, (nuint)type, format, (nuint)items,
            (nuint)remaining, (nint)(&value), result, out int count));
        Assert.Equal(0, count);
        Assert.Equal((nuint)99, result[0]);
        Assert.False(X11SystemMenu.CopyProperty32(0, 4, 4, 32, 1, 0, 0, result, out _));
    }

    private sealed class Operations : IX11SystemMenuOperations
    {
        public readonly List<string> Calls = [];
        public bool TargetAccepted = true, PointerAccepted = true, SendAccepted = true;
        public int Device = 9;
        public string? ThrowAt;
        public nint Connection = 41, OwnerDisplay, ClosedConnection;
        public nuint PointerOwner, Root;
        public X11MenuMessage Request;
        public bool TryGetTarget(NativeWindowHandle owner, out nuint root, out nuint message)
        { Calls.Add("Target"); root = 31; message = 37; return TargetAccepted; }
        public nint OpenInputConnection(nint ownerDisplay)
        { Calls.Add("Open"); OwnerDisplay = ownerDisplay; return Connection; }
        public bool TryGetClientPointer(nint connection, nuint owner, out int device)
        {
            Calls.Add("Device"); Assert.Equal(Connection, connection); PointerOwner = owner;
            if (ThrowAt == "Device") throw new InvalidOperationException();
            device = Device; return PointerAccepted;
        }
        public bool Send(nint connection, nuint root, in X11MenuMessage message)
        {
            Calls.Add("Send"); Assert.Equal(Connection, connection); Root = root; Request = message;
            if (ThrowAt == "Send") throw new InvalidOperationException();
            return SendAccepted;
        }
        public void CloseInputConnection(nint connection)
        { Calls.Add("Close"); ClosedConnection = connection; }
    }
}
