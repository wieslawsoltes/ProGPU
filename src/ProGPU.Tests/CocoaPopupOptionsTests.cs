using ProGPU.Backend;
using Xunit;
using Operations = ProGPU.Tests.CocoaPopupWindowTests.Operations;

namespace ProGPU.Tests;

public sealed class CocoaPopupOptionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestedLevelReachesOwnedPanelBeforeLoad(bool topMost)
    {
        var operations = new Operations();
        var options = CocoaPopupWindowTests.Options();
        options.TopMost = topMost;
        using var window = CocoaPopupWindowTests.Create(operations, sourceScheduled: true, options: options);
        bool loaded = false;
        window.Load += () =>
        {
            loaded = true;
            Assert.Equal(("topmost", (object)topMost), Assert.Single(operations.OptionRequests));
            Assert.Equal(topMost, window.TopMost);
            Assert.False(window.IsVisible);
            Assert.Equal(NativeWindowHandle.Empty, window.Owner);
            Assert.Null(window.Native!.Glfw);
        };
        window.Initialize();
        Assert.True(loaded);
        Assert.Empty(operations.Calls);
    }

    [Fact]
    public void PreinitializationLevelCanChangeWithoutCreatingNativeObjects()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations);
        window.TopMost = true;
        Assert.True(window.TopMost);
        Assert.Empty(operations.OptionRequests);
        window.Initialize();
        Assert.Equal(("topmost", (object)true), Assert.Single(operations.OptionRequests));
    }

    [Fact]
    public void IncompatibleContextOptionsRemainRejectedWithTopMost()
    {
        var options = CocoaPopupWindowTests.Options();
        options.TopMost = true;
        options.IsContextControlDisabled = false;
        Assert.Throws<NotSupportedException>(() => CocoaPopupWindowTests.Create(new(), options: options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialLevelFailureCannotPublishLoadAndRetainsFailedCleanup(bool throws)
    {
        var operations = new Operations { OptionsAccepted = false, HideAccepted = false };
        var original = new ApplicationException("native option failed");
        if (throws) operations.OnOption = () => throw original;
        var window = CocoaPopupWindowTests.Create(operations);
        bool loaded = false;
        window.Load += () => loaded = true;
        Exception failure = Assert.ThrowsAny<Exception>(window.Initialize);
        if (throws) Assert.Same(original, failure);
        else Assert.Contains("initial level", failure.Message);
        Assert.IsType<InvalidOperationException>(failure.Data["PopupInitializationRetirement"]);
        Assert.False(loaded);
        Assert.False(window.IsReleased);
        operations.HideAccepted = true;
        Assert.True(window.TryCompleteDispose());
        Assert.Equal(1, operations.Calls.Count(call => call == "release"));
    }

    [Fact]
    public void NativeLevelReentryCannotChangeInitialIntentOrPublishDisposedLoad()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations);
        operations.OnOption = () =>
        {
            Assert.Throws<InvalidOperationException>(() => window.TopMost = true);
            window.Dispose();
            Assert.DoesNotContain("release", operations.Calls);
        };
        bool loaded = false;
        window.Load += () => loaded = true;
        Assert.Throws<ObjectDisposedException>(window.Initialize);
        Assert.False(loaded);
        Assert.True(window.IsReleased);
    }

    [Fact]
    public void TypedPlatformMutationsRetainExactIdentityGeometryAndLease()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations, sourceScheduled: true);
        window.Initialize();
        using var lease = window.AcquireRenderLease();
        using var platform = NativeWindowPlatformFactory.Create(window);
        Assert.True(platform.Capabilities.Supports(NativeWindowFeatures.TopMost | NativeWindowFeatures.SizeConstraints));
        operations.OptionRequests.Clear();
        Assert.True(platform.SetTopMost(true));
        Assert.True(window.TopMost);
        Assert.True(platform.SetOpacity(0.375));
        Assert.True(platform.SetSizeConstraints(new(0, 7), new(int.MaxValue, 333)));
        Assert.Equal(new (string, object)[]
        {
            ("topmost", true), ("opacity", 0.375), ("constraints", (new NativeWindowSize(0, 7), new NativeWindowSize(int.MaxValue, 333)))
        }, operations.OptionRequests);
        Assert.Equal(operations.Geometry.Window, lease.Window);
        Assert.Equal(operations.Geometry.ContentView, lease.ContentView);
        Assert.Equal(operations.Geometry, ReadGeometry(window));
        Assert.False(window.IsVisible);
        Assert.Empty(operations.Calls);
    }

    [Fact]
    public void RejectedLevelDoesNotPublishRequestedValue()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        operations.OptionsAccepted = false;
        using var platform = NativeWindowPlatformFactory.Create(window);
        Assert.False(platform.SetTopMost(true));
        Assert.False(window.TopMost);
        Assert.Throws<InvalidOperationException>(() => window.TopMost = true);
        Assert.False(window.TopMost);
        Assert.False(platform.SetOpacity(0.5));
        Assert.False(platform.SetSizeConstraints(default, NativeWindowSize.Unbounded));
    }

    [Theory]
    [InlineData(NativeWindowZOrder.Front)]
    [InlineData(NativeWindowZOrder.Back)]
    public void OrderingRequiresVisibleBoundPanelAndNeverShowsHiddenPanel(NativeWindowZOrder value)
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations, sourceScheduled: true);
        window.Initialize();
        using var platform = NativeWindowPlatformFactory.Create(window);
        operations.OptionRequests.Clear();
        Assert.False(platform.SetZOrder(value));
        Assert.True(window.BindOwner(new(NativeWindowKind.Cocoa, 1, 0, "NSWindow")));
        Assert.False(platform.SetZOrder(value));
        Assert.Empty(operations.OptionRequests);
        Assert.Empty(operations.Calls);
        window.IsVisible = true;
        Assert.True(platform.SetZOrder(value));
        Assert.Equal(("zorder", (object)value), Assert.Single(operations.OptionRequests));
        operations.OptionsAccepted = false;
        Assert.False(platform.SetZOrder(value));
        window.IsVisible = false;
        Assert.False(platform.SetZOrder(value));
        Assert.Equal(2, operations.OptionRequests.Count);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void InvalidOpacityNeverReachesNativeMutation(double value)
    {
        var operations = new Operations();
        using var window = Ready(operations);
        operations.OptionRequests.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => window.SetOpacity(value));
        Assert.Empty(operations.OptionRequests);
    }

    [Fact]
    public void InvalidConstraintsAndOrderingRejectBeforeNativeMutation()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        operations.OptionRequests.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => window.SetSizeConstraints(new(-1, 0), NativeWindowSize.Unbounded));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.SetSizeConstraints(new(10, 20), new(9, 20)));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.SetSizeConstraints(new(10, 20), new(10, 19)));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.SetZOrder((NativeWindowZOrder)99));
        Assert.Empty(operations.OptionRequests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryOptionRejectsReentrantCloseAndRetainsRendererLease(int option)
    {
        var operations = new Operations();
        using var window = Ready(operations);
        window.IsVisible = true;
        using var lease = window.AcquireRenderLease();
        operations.OnOption = () =>
        {
            Assert.Throws<InvalidOperationException>(() => window.SetOpacity(0.75));
            window.Close();
            Assert.DoesNotContain("hide", operations.Calls);
            Assert.DoesNotContain("release", operations.Calls);
        };
        Assert.False(Apply(window, option));
        Assert.True(window.IsClosing);
        Assert.False(window.IsVisible);
        Assert.False(window.TopMost);
        Assert.Contains("hide", operations.Calls);
        Assert.DoesNotContain("release", operations.Calls);
        window.Dispose();
        Assert.False(window.IsReleased);
        Assert.Equal(operations.Window, lease.Window);
        lease.Dispose();
        Assert.True(window.TryCompleteDispose());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryOptionPreservesNativeFailureAcrossReentrantDisposalFailure(int option)
    {
        var operations = new Operations();
        var window = Ready(operations);
        window.IsVisible = true;
        var original = new ApplicationException("native failure");
        operations.OnOption = () =>
        {
            window.Dispose();
            operations.HideAccepted = false;
            throw original;
        };
        Assert.Same(original, Assert.Throws<ApplicationException>(() => Apply(window, option)));
        Assert.IsType<InvalidOperationException>(original.Data["PopupOptionRetirement"]);
        Assert.False(window.IsReleased);
        Assert.False(window.TopMost);
        operations.HideAccepted = true;
        Assert.True(window.TryCompleteDispose());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryOptionRejectsIdentityChangesNativeCallbacksAndForeignThreads(int option)
    {
        var operations = new Operations();
        using var window = Ready(operations);
        window.IsVisible = true;
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() => Assert.Throws<InvalidOperationException>(() => Apply(window, option))));
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        operations.Input.BeginNativeCallback();
        try { Assert.Throws<InvalidOperationException>(() => Apply(window, option)); }
        finally { operations.Input.EndNativeCallback(); }
        operations.OnOption = () => operations.Current = false;
        Assert.False(Apply(window, option));
        Assert.False(window.TopMost);
        operations.Current = true;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SurfaceOptionCloseCannotPublishSuccessOrReleaseBorrowedView(int option)
    {
        var operations = new Operations();
        using var surface = new CocoaOwnedPopupSurface(operations);
        using var lease = surface.AcquireRenderLease();
        operations.OnOption = surface.Dispose;
        bool accepted = option switch
        {
            0 => surface.SetTopMost(true),
            1 => surface.SetOpacity(0.5),
            2 => surface.SetZOrder(NativeWindowZOrder.Front),
            _ => surface.SetSizeConstraints(default, NativeWindowSize.Unbounded)
        };
        Assert.False(accepted);
        Assert.False(surface.IsReleased);
        Assert.DoesNotContain("release", operations.Calls);
        lease.Dispose();
        Assert.True(surface.IsReleased);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingExceptionDataCannotReplaceNativeFailureDuringCleanup(bool initialize)
    {
        var operations = new Operations();
        var window = CocoaPopupWindowTests.Create(operations);
        if (!initialize) window.Initialize();
        var original = new ThrowingDataException();
        operations.OnOption = () =>
        {
            window.Dispose();
            operations.HideAccepted = false;
            throw original;
        };
        if (initialize) Assert.Same(original, Assert.Throws<ThrowingDataException>(window.Initialize));
        else Assert.Same(original, Assert.Throws<ThrowingDataException>(() => window.SetTopMost(true)));
        Assert.False(window.IsReleased);
        operations.HideAccepted = true;
        Assert.True(window.TryCompleteDispose());
    }

    [Fact]
    public void SurfaceCleanupCannotReplaceFailureWithThrowingExceptionData()
    {
        var operations = new Operations();
        using var surface = new CocoaOwnedPopupSurface(operations);
        var original = new ThrowingDataException();
        operations.OnOption = () =>
        {
            surface.Dispose();
            operations.HideAccepted = false;
            throw original;
        };
        Assert.Same(original, Assert.Throws<ThrowingDataException>(() => surface.SetOpacity(0.5)));
        Assert.False(surface.IsReleased);
        operations.HideAccepted = true;
        Assert.True(surface.TryCompleteDispose());
    }

    [Fact]
    public void IdentityFailureSurvivesThrowingDiagnosticDataAndRetirement()
    {
        var operations = new Operations();
        using var surface = new CocoaOwnedPopupSurface(operations);
        var original = new ThrowingDataException();
        operations.OnIdentity = () =>
        {
            surface.Dispose();
            operations.HideAccepted = false;
            throw original;
        };
        Assert.Same(original, Assert.Throws<ThrowingDataException>(() => surface.SetTopMost(true)));
        Assert.Empty(operations.OptionRequests);
        Assert.False(surface.IsReleased);
        operations.HideAccepted = true;
        Assert.True(surface.TryCompleteDispose());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReentrantDisposalCannotPublishSuccessAfterDispatchRetirement(int option)
    {
        var operations = new Operations();
        using var window = Ready(operations);
        window.IsVisible = true;
        operations.OnOption = () =>
        {
            window.Dispose();
            Assert.DoesNotContain("release", operations.Calls);
        };
        Assert.False(Apply(window, option));
        Assert.False(window.TopMost);
        Assert.True(window.IsReleased);
        Assert.Equal(1, operations.Calls.Count(call => call == "release"));
    }

    [Fact]
    public void DeferredHideCanDisposeWithoutPublishingLevelOrEndingRenderLease()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        window.IsVisible = true;
        using var lease = window.AcquireRenderLease();
        operations.OnOption = window.Close;
        operations.OnHide = window.Dispose;
        Assert.False(window.SetTopMost(true));
        Assert.False(window.TopMost);
        Assert.False(window.IsVisible);
        Assert.False(window.IsReleased);
        Assert.DoesNotContain("release", operations.Calls);
        operations.OnHide = null;
        lease.Dispose();
        Assert.True(window.TryCompleteDispose());
    }

    [Fact]
    public void DeferredHideFailureCannotPublishLevelAndRetirementCanRetry()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        window.IsVisible = true;
        operations.OnOption = window.Close;
        operations.HideAccepted = false;
        Assert.Throws<InvalidOperationException>(() => window.SetTopMost(true));
        Assert.False(window.TopMost);
        Assert.True(window.IsClosing);
        Assert.DoesNotContain("release", operations.Calls);
        operations.HideAccepted = true;
        window.Dispose();
        Assert.True(window.IsReleased);
    }

    [Fact]
    public void ReentrantCloseCancelsHeldInputBeforeDeferredNativeHide()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        window.IsVisible = true;
        Assert.True(operations.Input.TryWrite(new(CocoaPopupPointerKind.Down, 1, 2, 3, 0, 1, 0)));
        window.DoEvents();
        int canceled = 0;
        ((INativePointerInputContext)input).PointerEvent += value =>
        {
            if (value.Kind != NativePointerEventKind.Cancel) return;
            ++canceled;
            Assert.DoesNotContain("hide", operations.Calls);
        };
        operations.OnOption = window.Close;
        Assert.False(window.SetTopMost(true));
        Assert.Equal(1, canceled);
        Assert.Contains("hide", operations.Calls);
        Assert.False(operations.Input.CanReceive);
    }

    private sealed class ThrowingDataException : Exception
    {
        public override System.Collections.IDictionary Data => throw new InvalidOperationException("diagnostic storage failed");
    }

    private static bool Apply(CocoaPopupWindow window, int option) => option switch
    {
        0 => window.SetTopMost(true),
        1 => window.SetOpacity(0.5),
        2 => window.SetZOrder(NativeWindowZOrder.Front),
        _ => window.SetSizeConstraints(default, NativeWindowSize.Unbounded)
    };

    private static CocoaPopupWindow Ready(Operations operations)
    {
        var window = CocoaPopupWindowTests.Create(operations);
        window.Initialize();
        return window;
    }

    private static NativeWindowGeometrySnapshot ReadGeometry(CocoaPopupWindow window)
    {
        Assert.True(window.TryGetGeometry(out var geometry));
        return geometry;
    }
}
