using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class CocoaOwnedPopupSurfaceTests
{
    [Fact]
    public void NativePointerCallbackDefersWindowDestructionUntilManagedRetirement()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        operations.Input.BeginNativeCallback();
        surface.Dispose();
        Assert.False(surface.IsReleased);
        Assert.False(surface.TryCompleteDispose());
        Assert.Empty(operations.Events);
        operations.Input.EndNativeCallback();
        Assert.False(surface.IsReleased);
        Assert.Empty(operations.Events);
        Assert.True(surface.TryCompleteDispose());
        Assert.Equal(new[] { "hide", "release" }, operations.Events);
        Assert.True(surface.TryCompleteDispose());
    }

    [Fact]
    public void LastRenderLeaseReleasedDuringNativeCallbackStillWaitsForManagedRetirement()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        var lease = surface.AcquireRenderLease();
        operations.Input.BeginNativeCallback();
        surface.Dispose();
        lease.Dispose();
        Assert.Empty(operations.Events);
        operations.Input.EndNativeCallback();
        Assert.True(surface.TryCompleteDispose());
        Assert.Equal(new[] { "hide", "release" }, operations.Events);
    }

    [Fact]
    public void NativeCallbackCannotReenterSurfaceMutationOrInputDelivery()
    {
        var operations = new Operations();
        using var surface = new CocoaOwnedPopupSurface(operations);
        operations.Input.BeginNativeCallback();
        try
        {
            Assert.Throws<InvalidOperationException>(() => surface.Show());
            Assert.Throws<InvalidOperationException>(() => surface.ReadInput([], out _));
            Assert.Throws<InvalidOperationException>(() => surface.AcquireRenderLease());
        }
        finally { operations.Input.EndNativeCallback(); }
        Assert.True(surface.Show());
    }

    [Fact]
    public void InputIsPublishedOnlyAfterNativeGateAcceptsIt()
    {
        var operations = new Operations();
        using var surface = new CocoaOwnedPopupSurface(operations);
        Assert.True(surface.Show());
        operations.OnInputAllowed = _ => Assert.False(operations.Input.CanReceive);
        Assert.True(surface.SetInputAllowed(true));
        Assert.True(operations.Input.CanReceive);
        var value = new CocoaPopupPointerEvent(CocoaPopupPointerKind.Down, 1, 2, 3, 0, 1, CocoaPopupModifiers.Shift);
        Assert.True(operations.Input.TryWrite(value));
        Span<CocoaPopupPointerEvent> output = stackalloc CocoaPopupPointerEvent[1];
        Assert.Equal(1, surface.ReadInput(output, out _));
        Assert.Equal(value, output[0]);
    }

    [Fact]
    public void FailedNativeInputAdmissionLeavesQueueBlocked()
    {
        var operations = new Operations { InputAccepted = false };
        using var surface = new CocoaOwnedPopupSurface(operations);
        Assert.True(surface.Show());
        Assert.False(surface.SetInputAllowed(true));
        Assert.False(operations.Input.Enabled);
        Assert.False(operations.Input.CanReceive);
    }

    [Fact]
    public void DisablingStopsQueuedDeliveryBeforeCallingTheNativeGate()
    {
        var operations = new Operations();
        using var surface = new CocoaOwnedPopupSurface(operations);
        Assert.True(surface.Show());
        Assert.True(surface.SetInputAllowed(true));
        Assert.True(operations.Input.TryWrite(new(CocoaPopupPointerKind.Down, 1, 2, 3, 0, 1, CocoaPopupModifiers.None)));
        operations.OnInputAllowed = _ => Assert.False(operations.Input.CanReceive);
        operations.InputAccepted = false;
        Assert.False(surface.SetInputAllowed(false));
        Span<CocoaPopupPointerEvent> output = stackalloc CocoaPopupPointerEvent[1];
        Assert.Equal(0, surface.ReadInput(output, out _));
    }

    [Fact]
    public void FaultedQueueCannotReenableNativeInput()
    {
        var operations = new Operations();
        using var surface = new CocoaOwnedPopupSurface(operations);
        operations.Input.Fail(CocoaPopupInputFailure.NativeCallback);
        operations.OnInputAllowed = _ => Assert.Fail("Faulted source enabled its native panel.");
        Assert.Throws<InvalidOperationException>(() => surface.SetInputAllowed(true));
    }

    [Fact]
    public void CloseDuringNativeInputAdmissionDoesNotReenableRetiringSurface()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        operations.OnInputAllowed = _ => surface.Dispose();
        Assert.False(surface.SetInputAllowed(true));
        Assert.False(operations.Input.CanReceive);
        Assert.True(surface.IsReleased);
    }

    [Fact]
    public void RetainedRenderLeaseDoesNotKeepInputAliveAfterClose()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        using var lease = surface.AcquireRenderLease();
        Assert.True(surface.Show());
        Assert.True(surface.SetInputAllowed(true));
        surface.Dispose();
        Assert.False(surface.IsReleased);
        Assert.False(operations.Input.CanReceive);
        Assert.Throws<ObjectDisposedException>(() => surface.ReadInput([], out _));
    }

    [Fact]
    public void HiddenSurfaceClosesExactlyOnce()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        surface.Dispose();
        surface.Dispose();
        Assert.Equal(new[] { "hide", "release" }, operations.Events);
        Assert.True(surface.IsReleased);
        Assert.Throws<ObjectDisposedException>(() => surface.Show());
    }

    [Fact]
    public void CloseHidesButRetainsPanelAndViewUntilEveryRenderLeaseIsReleased()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        var first = surface.AcquireRenderLease();
        var second = surface.AcquireRenderLease();
        Assert.Equal(operations.Window, first.Window);
        Assert.Equal(operations.ContentView, second.ContentView);
        surface.Dispose();
        Assert.False(surface.IsReleased);
        Assert.DoesNotContain("release", operations.Events);
        Assert.Throws<ObjectDisposedException>(() => surface.AcquireRenderLease());
        first.Dispose();
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.ContentView);
        Assert.False(surface.IsReleased);
        second.Dispose();
        Assert.True(surface.IsReleased);
        Assert.Equal(1, operations.Events.Count(value => value == "release"));
        Assert.Equal("release", operations.Events[^1]);
    }

    [Fact]
    public void ReleasingLeaseDoesNotDisposeItsLiveSurface()
    {
        var operations = new Operations();
        using var surface = new CocoaOwnedPopupSurface(operations);
        surface.AcquireRenderLease().Dispose();
        Assert.Empty(operations.Events);
        Assert.True(surface.Show());
        Assert.True(surface.Hide());
        Assert.True(surface.Show());
    }

    [Fact]
    public void ReentrantCloseWaitsUntilShowReturnsAndDoesNotReportVisibleSuccess()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        operations.OnShow = () =>
        {
            surface.Dispose();
            Assert.False(surface.IsReleased);
            Assert.DoesNotContain("hide", operations.Events);
        };
        Assert.False(surface.Show());
        Assert.True(surface.IsReleased);
        Assert.Equal(new[] { "show", "hide", "release" }, operations.Events);
    }

    [Fact]
    public void ReentrantCloseDuringIdentityReadCannotPublishLease()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        operations.OnIdentity = () => surface.Dispose();
        Assert.Throws<ObjectDisposedException>(() => surface.AcquireRenderLease());
        Assert.True(surface.IsReleased);
        Assert.Equal(new[] { "hide", "release" }, operations.Events);
    }

    [Fact]
    public void RecursiveMutationIsRejectedButOuterTransitionRemainsUsable()
    {
        var operations = new Operations();
        using var surface = new CocoaOwnedPopupSurface(operations);
        operations.OnShow = () => Assert.Throws<InvalidOperationException>(() => surface.Hide());
        Assert.True(surface.Show());
        operations.OnShow = null;
        Assert.True(surface.Hide());
    }

    [Fact]
    public void ThrowingShowDrainsRequestedCloseWithoutLosingTheOriginalFailure()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        operations.OnShow = () => { surface.Dispose(); throw new ArgumentException("show failure"); };
        Assert.Equal("show failure", Assert.Throws<ArgumentException>(() => surface.Show()).Message);
        Assert.True(surface.IsReleased);
    }

    [Fact]
    public void FailedHideRetainsExplicitOwnership()
    {
        var operations = new Operations { HideAccepted = false };
        var surface = new CocoaOwnedPopupSurface(operations);
        Assert.Throws<InvalidOperationException>(() => surface.Dispose());
        Assert.False(surface.IsReleased);
        Assert.DoesNotContain("release", operations.Events);
        operations.HideAccepted = true;
        surface.Dispose();
        Assert.True(surface.IsReleased);
    }

    [Fact]
    public void LostOwnerIdentityRejectsNewWorkButStillAllowsOwnedPanelCleanup()
    {
        var operations = new Operations { Current = false };
        var surface = new CocoaOwnedPopupSurface(operations);
        Assert.Throws<InvalidOperationException>(() => surface.Show());
        Assert.Throws<InvalidOperationException>(() => surface.AcquireRenderLease());
        Assert.Empty(operations.Events);
        surface.Dispose();
        Assert.True(surface.IsReleased);
    }

    [Fact]
    public void GeometryReadClosedByCallbackDoesNotPublishSnapshot()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        operations.OnGeometry = () => surface.Dispose();
        Assert.False(surface.TryGetGeometry(out var geometry));
        Assert.Equal(default, geometry);
        Assert.True(surface.IsReleased);
    }

    [Fact]
    public void NativeGeometryIsReturnedWithoutFramebufferRescaling()
    {
        var operations = new Operations();
        using var surface = new CocoaOwnedPopupSurface(operations);
        Assert.True(surface.TryGetGeometry(out var geometry));
        Assert.Equal(operations.Geometry, geometry);
    }

    [Fact]
    public void ForeignThreadCannotRetireSurfaceOrConsumeLease()
    {
        var operations = new Operations();
        var surface = new CocoaOwnedPopupSurface(operations);
        var lease = surface.AcquireRenderLease();
        Exception? surfaceError = null, leaseError = null, viewError = null;
        var thread = new Thread(() =>
        {
            surfaceError = Record.Exception(surface.Dispose);
            leaseError = Record.Exception(lease.Dispose);
            viewError = Record.Exception(() => { _ = lease.ContentView; });
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(surfaceError);
        Assert.IsType<InvalidOperationException>(leaseError);
        Assert.IsType<InvalidOperationException>(viewError);
        Assert.Empty(operations.Events);
        surface.Dispose();
        Assert.False(surface.IsReleased);
        lease.Dispose();
        Assert.True(surface.IsReleased);
    }

    [Theory]
    [InlineData(-1600, 30, 250, 90, 0, 0, 1920, 1080)]
    [InlineData(25.25, -130.5, 17.5, 41.75, -300, 120, 1200, 900)]
    [InlineData(4000, 5000, 120, 40, 500, -700, 900, 600)]
    public void DesktopRectangleUsesPrimaryOriginAndPreservesFractionalAndNegativeCoordinates(
        double x, double y, double width, double height,
        double px, double py, double pw, double ph)
    {
        var bounds = new NativeWindowBounds(x, y, width, height);
        var primary = new CocoaMenuRect(px, py, pw, ph);
        Assert.True(CocoaWindowGeometry.TryMapDesktopRectangle(bounds, primary, out var native));
        Assert.Equal(new CocoaMenuRect(px + x, py + ph - y - height, width, height), native);
        Assert.True(CocoaWindowGeometry.TryMapRectangle(native, primary, out var roundTrip));
        Assert.Equal(bounds, roundTrip);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, double.PositiveInfinity, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, -1)]
    [InlineData(double.MaxValue, 0, double.MaxValue, 1)]
    [InlineData(0, double.MaxValue, 1, double.MaxValue)]
    public void InvalidDesktopRectangleFailsWithoutPartialOutput(double x, double y, double width, double height)
    {
        Assert.False(CocoaWindowGeometry.TryMapDesktopRectangle(new(x, y, width, height),
            new(0, 0, 1920, 1080), out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void DesktopConversionRejectsOverflowFromPrimaryOrigin()
    {
        Assert.False(CocoaWindowGeometry.TryMapDesktopRectangle(new(double.MaxValue, 0, 1, 1),
            new(double.MaxValue, 0, 1, 1), out var result));
        Assert.Equal(default, result);
    }

    private sealed class Operations : ICocoaOwnedPopupOperations
    {
        internal readonly List<string> Events = new();
        internal Action? OnShow, OnIdentity, OnGeometry;
        internal Action<bool>? OnInputAllowed;
        internal bool Current = true, HideAccepted = true;
        internal bool InputAccepted = true;
        public NativeWindowHandle Window => new(NativeWindowKind.Cocoa, 11, 0, "test-owned-panel");
        public nint ContentView => 12;
        public CocoaPopupInputQueue Input { get; } = new();
        public bool IsCurrent { get { OnIdentity?.Invoke(); return Current; } }
        public bool BindOwner(NativeWindowHandle owner) => true;
        public bool SetInputAllowed(bool allowed) { OnInputAllowed?.Invoke(allowed); return InputAccepted; }
        public bool SupportsCursor(Silk.NET.Input.StandardCursor cursor) => true;
        public bool SetCursor(Silk.NET.Input.StandardCursor cursor, bool hidden) => true;
        internal NativeWindowGeometrySnapshot Geometry => new(Window, ContentView, 13,
            new(-500, 25, 80, 60), new(-500, 25, 80, 60), 2);
        public bool Show() { Events.Add("show"); Input.SetVisible(true); OnShow?.Invoke(); return true; }
        public bool Hide() { Events.Add("hide"); Input.SetVisible(false); return HideAccepted; }
        public bool SetBounds(NativeWindowBounds bounds) => true;
        public bool SetTopMost(bool value) => true;
        public bool SetOpacity(double value) => true;
        public bool SetZOrder(NativeWindowZOrder value) => true;
        public bool SetSizeConstraints(NativeWindowSize minimum, NativeWindowSize maximum) => true;
        public bool TryGetGeometry(out NativeWindowGeometrySnapshot snapshot)
        { OnGeometry?.Invoke(); snapshot = Geometry; return true; }
        public void Dispose() => Events.Add("release");
    }
}
