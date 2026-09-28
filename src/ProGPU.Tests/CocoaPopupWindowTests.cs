using ProGPU.Backend;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Xunit;

namespace ProGPU.Tests;

public sealed class CocoaPopupWindowTests
{
    [Fact]
    public void HiddenInitializationPublishesOnlyTheOwnedCocoaIdentity()
    {
        var operations = new Operations();
        using var window = Create(operations);
        Assert.Null(window.Native);
        Assert.Equal(0, window.Handle);
        window.Load += () =>
        {
            Assert.True(window.IsInitialized);
            Assert.False(window.IsVisible);
            Assert.Equal(operations.Window.Handle, window.Native!.Cocoa);
            Assert.Null(window.Native.Glfw);
        };
        window.Initialize();
        window.Initialize();
        Assert.Equal(operations.Window, GlfwNativeWindowPlatform.ResolveWindowHandle(window));
        Assert.Empty(operations.Calls);
        Assert.Throws<ArgumentException>(() => new GlfwNativeWindowPlatform(window));
        using var platform = NativeWindowPlatformFactory.Create(window);
        Assert.IsType<CocoaPopupNativeWindowPlatform>(platform);
        Assert.Equal(operations.Window, platform.Handle);
    }

    [Fact]
    public void ControllerKeepsExactPanelGeometryAndIndependentInputPolicy()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        using var controller = new SilkWindowController(window);
        Assert.True(controller.Attach());
        Assert.Equal(operations.Window, controller.Handle);
        Assert.True(controller.TryGetGeometrySnapshot(out var snapshot));
        Assert.Equal(operations.Geometry, snapshot);
        Assert.True(controller.SetInputAllowed(false));
        Assert.False(operations.Input.Enabled);
        Assert.True(controller.SetEnabled(true));
        Assert.False(operations.Input.Enabled);
        Assert.True(controller.SetInputAllowed(true));
        Assert.True(operations.Input.Enabled);
        Assert.True(controller.SetEnabled(false));
        Assert.False(operations.Input.Enabled);
        Assert.True(controller.SetInputAllowed(false));
        Assert.True(controller.SetInputAllowed(true));
        Assert.False(operations.Input.Enabled);
        controller.Dispose();
        Assert.False(window.IsReleased); // The controller only borrows ownership.
    }

    [Fact]
    public void GeometrySeparatesNegativeDesktopOriginsFromBackingPixels()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        Assert.Equal(new(-200, 30), window.Position);
        Assert.Equal(new(20, 10), window.Size);
        Assert.Equal(new(40, 20), window.FramebufferSize);
        Assert.Equal(new(-197, 34), window.PointToScreen(new(3, 4)));
        Assert.Equal(new(3, 4), window.PointToClient(new(-197, 34)));
        Assert.Equal(new(6, 8), window.PointToFramebuffer(new(3, 4)));
    }

    [Fact]
    public void GeometryRefreshReportsRealBackingScaleChangesWithoutResizingSourcePoints()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        int resizeCalls = 0;
        var framebufferEvents = new List<Vector2D<int>>();
        window.Resize += _ => resizeCalls++;
        window.FramebufferResize += framebufferEvents.Add;
        operations.Geometry = operations.Geometry with { BackingScale = 1 };
        window.DoEvents();
        Assert.Equal(0, resizeCalls);
        Assert.Equal(new[] { new Vector2D<int>(20, 10) }, framebufferEvents);
        Assert.Equal(new(-200, 30), window.Position);
    }

    [Fact]
    public void ReentrantGeometryChangeDoesNotPublishStaleFramebufferEvents()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        var framebuffers = new List<Vector2D<int>>();
        var sizes = new List<Vector2D<int>>();
        window.Move += _ => window.Size = new(9, 4);
        window.Resize += sizes.Add;
        window.FramebufferResize += framebuffers.Add;
        operations.Bounds = new(4, 5, 100, 200);
        window.DoEvents();
        Assert.Equal(new[] { new Vector2D<int>(9, 4) }, sizes);
        Assert.Equal(new[] { new Vector2D<int>(18, 8) }, framebuffers);
        Assert.Equal(new(4, 5), window.Position);
    }

    [Fact]
    public void RejectedBoundsDoNotReplaceRetainedGeometry()
    {
        var operations = new Operations { BoundsAccepted = false };
        using var window = Create(operations);
        window.Initialize();
        Assert.Throws<InvalidOperationException>(() => window.Size = new(70, 90));
        Assert.Equal(new(20, 10), window.Size);
        Assert.Equal(new(40, 20), window.FramebufferSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => window.Size = new(0, 5));
    }

    [Fact]
    public void GeometryIdentityChangeCannotReplaceTheOwnedPanelHandle()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        operations.Geometry = operations.Geometry with { ContentView = 77 };
        Assert.False(window.TryGetGeometry(out var changed));
        Assert.Equal(default, changed);
        Assert.Throws<InvalidOperationException>(window.DoEvents);
        Assert.Equal(operations.Window.Handle, window.Handle);
    }

    [Fact]
    public void UpdatesDrainInputWithoutPollingTheOwnerOrRecursing()
    {
        var operations = new Operations();
        int wakeCalls = 0;
        using var window = Create(operations, () => wakeCalls++);
        window.Initialize();
        var sequence = new List<string>();
        window.InputPending += () =>
        {
            sequence.Add("input");
            Assert.Throws<InvalidOperationException>(window.DoEvents);
            Assert.Throws<InvalidOperationException>(window.DoUpdate);
        };
        window.Update += _ => sequence.Add("update");
        window.DoUpdate();
        Assert.Equal(new[] { "input", "update" }, sequence);
        Assert.Equal(0, wakeCalls);
        window.ContinueEvents();
        Assert.Equal(1, wakeCalls);
    }

    [Fact]
    public void DisposalInInputCallbackStopsFollowingUpdateAndDefersNativeRetirement()
    {
        var operations = new Operations();
        var window = Create(operations);
        window.Initialize();
        bool updated = false;
        window.InputPending += () =>
        {
            window.Dispose();
            Assert.False(window.IsReleased);
            Assert.DoesNotContain("release", operations.Calls);
        };
        window.Update += _ => updated = true;
        window.DoUpdate();
        Assert.False(updated);
        Assert.True(window.IsReleased);
        Assert.Equal(new[] { "hide", "release" }, operations.Calls);
        Assert.Null(window.Native);
    }

    [Fact]
    public void ClosingPanelStaysAliveUntilTheRendererReleasesItsLease()
    {
        var operations = new Operations();
        var window = Create(operations);
        window.Initialize();
        var native = window.Native!;
        var lease = window.AcquireRenderLease();
        window.Dispose();
        Assert.False(window.IsReleased);
        Assert.Equal(operations.Window.Handle, native.Cocoa);
        Assert.DoesNotContain("release", operations.Calls);
        lease.Dispose();
        Assert.True(window.TryCompleteDispose());
        Assert.Null(native.Cocoa);
        Assert.Equal(0, window.Handle);
        Assert.Equal(1, operations.Calls.Count(value => value == "release"));
    }

    [Fact]
    public void NativeCallbackRetirementCompletesOnlyAfterPollingReturns()
    {
        var operations = new Operations();
        var window = Create(operations);
        window.Initialize();
        operations.Input.BeginNativeCallback();
        window.Dispose();
        Assert.False(window.IsReleased);
        Assert.Empty(operations.Calls);
        operations.Input.EndNativeCallback();
        Assert.False(window.IsReleased);
        window.DoEvents();
        Assert.True(window.IsReleased);
        Assert.Equal(new[] { "hide", "release" }, operations.Calls);
    }

    [Fact]
    public void FailedRetirementRetainsOwnershipForAnExplicitRetry()
    {
        var operations = new Operations { HideAccepted = false };
        var window = Create(operations);
        window.Initialize();
        Assert.Throws<InvalidOperationException>(window.Dispose);
        Assert.False(window.IsReleased);
        Assert.Equal(operations.Window.Handle, window.Handle);
        operations.HideAccepted = true;
        Assert.True(window.TryCompleteDispose());
        Assert.True(window.IsReleased);
    }

    [Fact]
    public void LoadCallbackDisposalDoesNotPublishInitializedWindow()
    {
        var operations = new Operations();
        var window = Create(operations);
        window.Load += window.Dispose;
        window.Initialize();
        Assert.False(window.IsInitialized);
        Assert.True(window.IsReleased);
    }

    [Fact]
    public void DisposalDuringNativeCreationRetiresTheUnpublishedPanel()
    {
        var operations = new Operations();
        CocoaPopupWindow? window = null;
        window = Create(operations, onCreate: () =>
        {
            window!.Dispose();
            Assert.False(window.IsReleased);
        });
        Assert.Throws<ObjectDisposedException>(window.Initialize);
        Assert.True(window.IsReleased);
        Assert.False(window.IsInitialized);
        Assert.Equal(new[] { "hide", "release" }, operations.Calls);
    }

    [Fact]
    public void RecursiveInitializationCannotAllocateOrRepositionAnotherPanel()
    {
        var operations = new Operations();
        CocoaPopupWindow? window = null;
        window = Create(operations, onCreate: () =>
        {
            Assert.Throws<InvalidOperationException>(window!.Initialize);
            Assert.Throws<InvalidOperationException>(() => window.Position = new(1, 2));
        });
        using (window)
        {
            window.Initialize();
            Assert.True(window.IsInitialized);
            Assert.Equal(new(-200, 30), window.Position);
        }
    }

    [Fact]
    public void OnlyVisiblePanelsRenderAndHiddenPanelsCannotBecomeKey()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        int renderCalls = 0, closingCalls = 0;
        window.Render += _ => renderCalls++;
        window.Closing += () => closingCalls++;
        window.DoRender();
        Assert.Equal(0, renderCalls);
        window.IsVisible = true;
        window.DoRender();
        Assert.Equal(1, renderCalls);
        Assert.Throws<NotSupportedException>(window.Focus);
        window.Close();
        window.Close();
        Assert.Equal(1, closingCalls);
        window.DoRender();
        Assert.Equal(1, renderCalls);
        Assert.False(window.IsVisible);
        Assert.Throws<NotSupportedException>(() => window.IsClosing = false);
    }

    [Fact]
    public void OwnedWindowRejectsForeignThreadMutationAndRetirement()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() =>
        {
            Assert.Throws<InvalidOperationException>(window.DoEvents);
            Assert.Throws<InvalidOperationException>(window.Dispose);
            Assert.Throws<InvalidOperationException>(() => window.Position = new(1, 2));
        }));
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void UnsupportedPopupChromeAndPresentationAreNotReportedAsWorking()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        Assert.Throws<NotSupportedException>(() => window.ShouldSwapAutomatically = true);
        Assert.Throws<NotSupportedException>(() => window.WindowBorder = WindowBorder.Resizable);
        Assert.Throws<NotSupportedException>(() => window.WindowState = WindowState.Fullscreen);
        Assert.Throws<NotSupportedException>(() => window.TopMost = true);
        Assert.Throws<NotSupportedException>(() => window.Run(() => { }));
        using var platform = NativeWindowPlatformFactory.Create(window);
        Assert.False(platform.SetParent(new(NativeWindowKind.Cocoa, 123, 0, "NSWindow")));
        Assert.False(platform.SetOpacity(0.5));
        Assert.False(platform.SetBackdrop(NativeWindowBackdrop.Mica));
        Assert.False(platform.SupportsManagedResize);
    }

    private static CocoaPopupWindow Create(Operations operations, Action? wakeOwner = null, Action? onCreate = null)
    {
        var options = WindowOptions.Default;
        options.API = GraphicsAPI.None;
        options.IsVisible = false;
        options.WindowBorder = WindowBorder.Hidden;
        options.ShouldSwapAutomatically = false;
        options.IsContextControlDisabled = true;
        options.Title = string.Empty;
        options.Position = new(-200, 30);
        options.Size = new(20, 10);
        options.FramesPerSecond = options.UpdatesPerSecond = 0;
        return new(new Parent(), new(NativeWindowKind.Cocoa, 1, 0, "NSWindow"), options,
            wakeOwner ?? (() => { }), (bounds, _) =>
            {
                operations.Bounds = bounds;
                onCreate?.Invoke();
                return new(operations);
            });
    }

    private sealed class Parent : IWindowHost
    {
        public IWindow CreateWindow(WindowOptions options) => throw new NotSupportedException();
    }

    private sealed class Operations : ICocoaOwnedPopupOperations
    {
        public NativeWindowHandle Window { get; } = new(NativeWindowKind.Cocoa, 2, 0, "NSPanel");
        public nint ContentView => 3;
        public CocoaPopupInputQueue Input { get; } = new();
        public bool IsCurrent => true;
        public bool HideAccepted = true, BoundsAccepted = true;
        public List<string> Calls { get; } = [];
        public NativeWindowGeometrySnapshot Geometry = new(
            new(NativeWindowKind.Cocoa, 2, 0, "NSPanel"), 3, 4, default, default, 2);
        public NativeWindowBounds Bounds
        {
            set => Geometry = Geometry with { ContentBounds = value, FrameBounds = value };
        }
        public bool SetInputAllowed(bool allowed) => true;
        public bool Show() { Calls.Add("show"); Input.SetVisible(true); return true; }
        public bool Hide() { Calls.Add("hide"); Input.SetVisible(false); return HideAccepted; }
        public bool SetBounds(NativeWindowBounds bounds)
        {
            if (!BoundsAccepted) return false;
            Bounds = bounds;
            return true;
        }
        public bool TryGetGeometry(out NativeWindowGeometrySnapshot snapshot) { snapshot = Geometry; return true; }
        public void Dispose() => Calls.Add("release");
    }
}
