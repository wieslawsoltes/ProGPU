using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeWindowGeometryTests
{
    private static readonly NativeWindowHandle Window = new(NativeWindowKind.Cocoa, 17, 0, "NSWindow");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1.5)]
    public void SnapshotReadsIndependentContentAndFrameInPointsAndKeepsBorrowedNativeIdentity(double scale)
    {
        var api = new Operations { Reading = new(new(620, 409, 560, 250), new(618, 407, 564, 280), new(0, 0, 1920, 1080), scale) };
        Assert.True(CocoaWindowGeometry.TryCapture(Window, ref api, out var snapshot));
        Assert.Equal(new NativeWindowGeometrySnapshot(Window, 23, 148521,
            new(620, 421, 560, 250), new(618, 393, 564, 280), scale), snapshot);
        Assert.Equal(new[] { "Retain", "Read", "Revalidate", "Release" }, api.Calls);
        Assert.Equal(api.Owner, api.Released);
    }

    [Theory]
    [InlineData(100, 200, -300, 400, -400, 780)]
    [InlineData(-100, -200, -300, -400, -200, 1180)]
    [InlineData(100, 200, 2500, 1400, 2400, -220)]
    [InlineData(0.25, 0.5, -19.5, 10.25, -19.75, 970.25)]
    public void PrimaryScreenOffsetsNegativeDesktopOriginsAndFractionalPointsRemainExact(
        double primaryX, double primaryY, double x, double y, double expectedX, double expectedY)
    {
        Assert.True(CocoaWindowGeometry.TryMapRectangle(new(x, y, 200, 100),
            new(primaryX, primaryY, 1920, 1080), out var actual));
        Assert.Equal(new NativeWindowBounds(expectedX, expectedY, 200, 100), actual);
    }

    [Theory]
    [InlineData(double.NaN, 0, 100, 100)]
    [InlineData(0, double.PositiveInfinity, 100, 100)]
    [InlineData(0, 0, 0, 100)]
    [InlineData(0, 0, 100, -1)]
    [InlineData(0, 0, double.NaN, 100)]
    [InlineData(0, 0, 100, double.NegativeInfinity)]
    [InlineData(double.MaxValue, 0, double.MaxValue, 100)]
    [InlineData(0, double.MaxValue, 100, double.MaxValue)]
    public void InvalidContentOrPrimaryScreenDoesNotInventGeometry(double x, double y, double width, double height)
    {
        var invalid = new CocoaMenuRect(x, y, width, height);
        var valid = new CocoaMenuRect(0, 0, 1920, 1080);
        Assert.False(CocoaWindowGeometry.TryMapRectangle(invalid, valid, out var result));
        Assert.Equal(default, result);
        Assert.False(CocoaWindowGeometry.TryMapRectangle(valid, invalid, out result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void FiniteInputWhoseDesktopTranslationOverflowsIsRejected()
    {
        Assert.False(CocoaWindowGeometry.TryMapRectangle(new(double.MaxValue, 0, 1, 1),
            new(-double.MaxValue, 0, 1, 1), out var result));
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidBackingScaleReleasesLeaseAndPublishesNothing(double scale)
    {
        var api = new Operations();
        api.Reading = api.Reading with { BackingScale = scale };
        Assert.False(CocoaWindowGeometry.TryCapture(Window, ref api, out var snapshot));
        Assert.Equal(default, snapshot);
        Assert.Equal(new[] { "Retain", "Read", "Release" }, api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherInvalidActualContentOrActualFrameRejectsWholeSnapshot(bool content)
    {
        var api = new Operations();
        api.Reading = content ? api.Reading with { ContentScreenBounds = default } :
            api.Reading with { FrameScreenBounds = default };
        Assert.False(CocoaWindowGeometry.TryCapture(Window, ref api, out var snapshot));
        Assert.Equal(default, snapshot);
        Assert.Equal("Release", api.Calls[^1]);
    }

    [Theory]
    [InlineData(NativeWindowKind.Unknown, 17, 0)]
    [InlineData(NativeWindowKind.Win32, 17, 0)]
    [InlineData(NativeWindowKind.X11, 17, 19)]
    [InlineData(NativeWindowKind.Cocoa, 0, 0)]
    [InlineData(NativeWindowKind.Cocoa, 17, 19)]
    public void UnsupportedOrForeignWindowAdmissionNeverEntersNativeOperations(NativeWindowKind kind, int handle, int display)
    {
        var api = new Operations();
        Assert.False(CocoaWindowGeometry.TryCapture(new(kind, handle, display, "fixture"), ref api, out var snapshot));
        Assert.Equal(default, snapshot);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void MissingLiveNativeWindowPublishesNothingAndDoesNotReleaseAnUnacquiredLease()
    {
        var api = new Operations { Retained = false };
        Assert.False(CocoaWindowGeometry.TryCapture(Window, ref api, out var snapshot));
        Assert.Equal(default, snapshot);
        Assert.Equal(new[] { "Retain" }, api.Calls);
    }

    [Theory]
    [InlineData(19, 23, 148521)]
    [InlineData(17, 0, 148521)]
    [InlineData(17, 23, 0)]
    [InlineData(17, 23, -1)]
    public void MismatchedRetainedWindowOrMissingContentGenerationIsNotRead(int window, int view, long number)
    {
        var api = new Operations { Owner = new(window, view, number) };
        Assert.False(CocoaWindowGeometry.TryCapture(Window, ref api, out var snapshot));
        Assert.Equal(default, snapshot);
        Assert.Equal(new[] { "Retain", "Release" }, api.Calls);
    }

    [Fact]
    public void UnavailableNativeReadingDoesNotPublishPartialContentOrFrame()
    {
        var api = new Operations { Readable = false };
        Assert.False(CocoaWindowGeometry.TryCapture(Window, ref api, out var snapshot));
        Assert.Equal(default, snapshot);
        Assert.Equal(new[] { "Retain", "Read", "Release" }, api.Calls);
    }

    [Theory]
    [InlineData(0, 23, 148521)]
    [InlineData(19, 23, 148521)]
    [InlineData(17, 29, 148521)]
    [InlineData(17, 23, 148522)]
    [InlineData(17, 23, 0)]
    public void RetirementRehostingAndRecreatedWindowDeviceRejectTheWholeRead(int window, int view, long number)
    {
        var api = new Operations { Current = new(window, view, number) };
        Assert.False(CocoaWindowGeometry.TryCapture(Window, ref api, out var snapshot));
        Assert.Equal(default, snapshot);
        Assert.Equal(new[] { "Retain", "Read", "Revalidate", "Release" }, api.Calls);
        Assert.Equal(api.Owner, api.Released);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Revalidate")]
    [InlineData("Release")]
    public void HostExceptionsPreserveOriginalErrorAndDefaultOutputAfterLeaseCleanup(string stage)
    {
        var api = new Operations { ThrowAt = stage };
        NativeWindowGeometrySnapshot snapshot = new(Window, 99, 99, default, default, 1);
        var error = Assert.Throws<InvalidOperationException>(() => CocoaWindowGeometry.TryCapture(Window, ref api, out snapshot));
        Assert.Same(api.Error, error);
        Assert.Equal(default, snapshot);
        Assert.Equal("Release", api.Calls[^1]);
        Assert.Equal(1, api.Calls.Count(call => call == "Release"));
    }

    [Fact]
    public unsafe void NativeRectangleRetainsTheActual64BitAppKitAbi()
    {
        Assert.Equal(32, sizeof(CocoaMenuRect));
    }

    private struct Operations : ICocoaWindowGeometryOperations
    {
        internal List<string> Calls = [];
        internal bool Retained = true, Readable = true;
        internal CocoaGeometryOwner Owner = new(17, 23, 148521), Current = new(17, 23, 148521), Released;
        internal CocoaGeometryReading Reading = new(new(620, 409, 560, 250), new(618, 407, 564, 280), new(0, 0, 1920, 1080), 2);
        internal InvalidOperationException Error = new("native geometry fixture");
        internal string? ThrowAt;

        public Operations() { }
        public bool TryRetain(nint window, out CocoaGeometryOwner owner)
        { Calls.Add("Retain"); owner = Owner; return Retained; }
        public bool TryRead(in CocoaGeometryOwner owner, out CocoaGeometryReading reading)
        { Record("Read"); reading = Reading; return Readable; }
        public bool IsCurrent(in CocoaGeometryOwner owner)
        { Record("Revalidate"); return CocoaWindowGeometry.IsSameOwner(owner, Current); }
        public void Release(in CocoaGeometryOwner owner)
        { Released = owner; Record("Release"); }
        private void Record(string stage)
        { Calls.Add(stage); if (stage == ThrowAt) throw Error; }
    }
}
