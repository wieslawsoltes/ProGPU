using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class CocoaSystemMenuTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SelectionUsesOriginalOwnerAndDispatchesOnceAfterTrackingAndRevalidation(int command)
    {
        var api = new Operations { Command = (CocoaMenuCommand)command };
        Assert.True(CocoaSystemMenu.Show(17, new(-1920, 240), ref api));
        Assert.Equal(new[] { "Retain", "Screen", "Track", "Revalidate", "Perform", "Release" }, api.Calls);
        Assert.Equal(new CocoaMenuPoint(-1920, 840), api.Position);
        Assert.Equal((nint)17, api.DispatchedWindow);
        Assert.Equal((CocoaMenuCommand)command, api.DispatchedCommand);
        Assert.Equal(new CocoaMenuOwner(17, 23, 29), api.Released);
    }

    [Fact]
    public void CancellationDoesNotDispatchOrRevalidateButReleasesTheLease()
    {
        var api = new Operations { Command = CocoaMenuCommand.None };
        Assert.True(CocoaSystemMenu.Show(17, default, ref api));
        Assert.Equal(new[] { "Retain", "Screen", "Track", "Release" }, api.Calls);
    }

    [Fact]
    public void MissingOwnerDoesNotReachNativeTracking()
    {
        var api = new Operations();
        Assert.False(CocoaSystemMenu.Show(0, default, ref api));
        Assert.Empty(api.Calls);
        api.OwnerAccepted = false;
        Assert.False(CocoaSystemMenu.Show(17, default, ref api));
        Assert.Equal(new[] { "Retain" }, api.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void MissingScreenOrTrackingFailureReleasesOwner(bool screen, bool track)
    {
        var api = new Operations { ScreenAccepted = screen, TrackAccepted = track };
        Assert.False(CocoaSystemMenu.Show(17, default, ref api));
        Assert.Equal("Release", api.Calls[^1]);
        Assert.DoesNotContain("Perform", api.Calls);
        if (!screen) Assert.DoesNotContain("Track", api.Calls);
    }

    [Fact]
    public void WindowsWithoutAvailableNativeActionsAreExplicitlyUnsupported()
    {
        var api = new Operations { InitialActions = CocoaMenuActions.None };
        Assert.False(CocoaSystemMenu.Show(17, default, ref api));
        Assert.Equal(new[] { "Retain", "Release" }, api.Calls);
    }

    [Fact]
    public void OwnerClosedOrRehostedDuringTrackingDoesNotReceiveAnAction()
    {
        var api = new Operations { OwnerStillMatches = false };
        Assert.False(CocoaSystemMenu.Show(17, default, ref api));
        Assert.Equal(new[] { "Retain", "Screen", "Track", "Revalidate", "Release" }, api.Calls);
    }

    [Fact]
    public void CapabilityRemovedDuringTrackingDoesNotReceiveAnAction()
    {
        var api = new Operations { CurrentActions = CocoaMenuActions.Minimize, Command = CocoaMenuCommand.Close };
        Assert.False(CocoaSystemMenu.Show(17, default, ref api));
        Assert.DoesNotContain("Perform", api.Calls);
        Assert.Equal("Release", api.Calls[^1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void DisabledOrUnknownSelectionIsNeverDispatched(int command)
    {
        var api = new Operations { InitialActions = CocoaMenuActions.Close, Command = (CocoaMenuCommand)command };
        Assert.False(CocoaSystemMenu.Show(17, default, ref api));
        Assert.DoesNotContain("Perform", api.Calls);
        Assert.DoesNotContain("Revalidate", api.Calls);
    }

    [Theory]
    [InlineData("Screen")]
    [InlineData("Track")]
    [InlineData("Revalidate")]
    [InlineData("Perform")]
    public void HostErrorsPropagateAfterReleasingLease(string stage)
    {
        var api = new Operations { ThrowAt = stage };
        Assert.Throws<InvalidOperationException>(() => CocoaSystemMenu.Show(17, default, ref api));
        Assert.Equal("Release", api.Calls[^1]);
        Assert.Equal(new CocoaMenuOwner(17, 23, 29), api.Released);
    }

    [Theory]
    [InlineData(-100, -200, -100, 1280)]
    [InlineData(1920, 1200, 1920, -120)]
    [InlineData(0, 0, 0, 1080)]
    public void DesktopMappingUsesPrimaryScreenPointsNotActiveMonitorOrRetinaPixels(int x, int y, double nativeX, double nativeY)
    {
        Assert.True(CocoaSystemMenu.TryMapDesktopPoint(new(x, y), new(0, 0, 1920, 1080), out var point));
        Assert.Equal(new CocoaMenuPoint(nativeX, nativeY), point);
    }

    [Theory]
    [InlineData(double.NaN, 1080)]
    [InlineData(1920, double.PositiveInfinity)]
    [InlineData(0, 1080)]
    [InlineData(1920, -1)]
    public void MissingOrInvalidScreenGeometryDoesNotInventCoordinates(double width, double height)
    {
        Assert.False(CocoaSystemMenu.TryMapDesktopPoint(default, new(0, 0, width, height), out var point));
        Assert.Equal(default, point);
    }

    [Fact]
    public unsafe void AppKitCoordinateRecordsKeepTheNative64BitAbi()
    {
        Assert.Equal(16, sizeof(CocoaMenuPoint));
        Assert.Equal(32, sizeof(CocoaMenuRect));
    }

    [Fact]
    public void NativeCallbackRecordsOnlyKnownItemsForItsOwnTargetAndOnlyOnce()
    {
        CocoaMenuSelection selection = new() { Target = 17, MinimizeItem = 21, ZoomItem = 22, CloseItem = 23 };
        selection.Select(18, 23); // Nested/unrelated target must not choose our action.
        selection.Select(17, 0);
        selection.Select(17, 99);
        Assert.Equal(CocoaMenuCommand.None, selection.Command);
        selection.Select(17, 22);
        selection.Select(17, 23);
        Assert.Equal(CocoaMenuCommand.Zoom, selection.Command);
    }

    [Fact]
    public unsafe void NestedTrackingRestoresItsOuterContextOnExceptionAndDropsLateActions()
    {
        CocoaMenuSelection outer = new() { Target = 17, CloseItem = 23 };
        CocoaMenuSelection inner = new() { Target = 31, ZoomItem = 37 };
        using (var outerScope = CocoaMenuSelectionContext.Enter(&outer))
        {
            bool caught = false;
            try
            {
                using var innerScope = CocoaMenuSelectionContext.Enter(&inner);
                CocoaMenuSelectionContext.Record(17, 23);
                Assert.Equal(CocoaMenuCommand.None, outer.Command);
                CocoaMenuSelectionContext.Record(31, 37);
                Assert.Equal(CocoaMenuCommand.Zoom, inner.Command);
                throw new InvalidOperationException("fixture tracking interruption");
            }
            catch (InvalidOperationException) { caught = true; }
            Assert.True(caught);
            CocoaMenuSelectionContext.Record(17, 23);
            Assert.Equal(CocoaMenuCommand.Close, outer.Command);
        }
        outer.Command = CocoaMenuCommand.None;
        CocoaMenuSelectionContext.Record(17, 23);
        Assert.Equal(CocoaMenuCommand.None, outer.Command);
    }

    private sealed class Operations : ICocoaSystemMenuOperations
    {
        public readonly List<string> Calls = [];
        public bool OwnerAccepted = true, ScreenAccepted = true, TrackAccepted = true, OwnerStillMatches = true;
        public CocoaMenuActions InitialActions = CocoaMenuActions.Minimize | CocoaMenuActions.Zoom | CocoaMenuActions.Close;
        public CocoaMenuActions CurrentActions = CocoaMenuActions.Minimize | CocoaMenuActions.Zoom | CocoaMenuActions.Close;
        public CocoaMenuCommand Command = CocoaMenuCommand.Close, DispatchedCommand;
        public string? ThrowAt;
        public nint DispatchedWindow;
        public CocoaMenuPoint Position;
        public CocoaMenuOwner Released;

        public bool TryRetainOwner(nint window, out CocoaMenuOwner owner, out CocoaMenuActions actions)
        { Calls.Add("Retain"); owner = new(window, 23, 29); actions = InitialActions; return OwnerAccepted; }
        public bool TryGetPrimaryScreen(out CocoaMenuRect frame)
        { Record("Screen"); frame = new(0, 0, 1920, 1080); return ScreenAccepted; }
        public bool TryTrack(in CocoaMenuOwner owner, CocoaMenuActions actions, CocoaMenuPoint point, out CocoaMenuCommand command)
        {
            Record("Track"); Assert.Equal(new CocoaMenuOwner(17, 23, 29), owner);
            Assert.Equal(InitialActions, actions); Position = point; command = Command; return TrackAccepted;
        }
        public bool TryRevalidateOwner(in CocoaMenuOwner owner, out CocoaMenuActions actions)
        { Record("Revalidate"); Assert.Equal(new CocoaMenuOwner(17, 23, 29), owner); actions = CurrentActions; return OwnerStillMatches; }
        public void Perform(nint window, CocoaMenuCommand command)
        { Record("Perform"); DispatchedWindow = window; DispatchedCommand = command; }
        public void ReleaseOwner(in CocoaMenuOwner owner)
        { Calls.Add("Release"); Released = owner; }
        private void Record(string stage)
        { Calls.Add(stage); if (stage == ThrowAt) throw new InvalidOperationException(); }
    }
}
