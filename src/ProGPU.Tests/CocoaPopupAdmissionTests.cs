using System.Reflection;
using ProGPU.Backend;
using Silk.NET.Windowing;
using Xunit;
using Operations = ProGPU.Tests.CocoaPopupWindowTests.Operations;

namespace ProGPU.Tests;

public sealed class CocoaPopupAdmissionTests
{
    private static readonly NativeWindowHandle Owner = new(NativeWindowKind.Cocoa, 10, 0, "NSWindow");

    [Fact]
    public void NullArgumentsAreRejectedBeforeProviderAccess()
    {
        Assert.Throws<ArgumentNullException>(() => NativePopupWindow.TryPrepareOwner(Owner, (IWindow)null!));
        Assert.Throws<ArgumentNullException>(() => NativePopupWindow.TryShowOwned(Owner, (IWindow)null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => NativePopupWindow.ShowWithoutActivation(null!));
        using var window = Create(new());
        Assert.Throws<ArgumentNullException>(() => NativePopupWindow.TryShowOwned(Owner, window, null!));
    }

    [Fact]
    public void SourcePreparationAndReopenRetainTheActualPanelAndView()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        using var lease = window.AcquireRenderLease();
        Assert.True(NativePopupWindow.TryPrepareOwner(Owner, window));
        Assert.Equal(Owner, window.Owner);
        Assert.False(window.IsVisible);
        Assert.True(Show(window));
        Assert.Null(window.Native!.Glfw);
        Assert.False(NativePopupWindow.TryPrepareOwner(Owner, window));
        Assert.False(Show(window));
        window.IsVisible = false;
        Assert.True(Show(window));
        Assert.Equal(operations.Window, lease.Window);
        Assert.Equal(operations.ContentView, lease.ContentView);
        Assert.Equal(new[] { "show", "hide", "show" }, operations.Calls);
        Assert.Equal(new[] { Owner }, operations.OwnerRequests);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData((int)NativeWindowKind.Win32, 10, 0)]
    [InlineData((int)NativeWindowKind.Cocoa, 10, 1)]
    public void InvalidOwnerCannotBindOrInvokeTheDisplayCallback(int kind, int handle, int display)
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        var owner = new NativeWindowHandle((NativeWindowKind)kind, handle, display, "invalid");
        Assert.False(NativePopupWindow.TryPrepareOwner(owner, window));
        Assert.False(NativePopupWindow.TryShowOwned(owner, window, () => throw new Exception("must not run")));
        Assert.Empty(operations.Calls);
        Assert.Empty(operations.OwnerRequests);
    }

    [Fact]
    public void DisplayCannotImplicitlyBindOrReplaceAnOwner()
    {
        var operations = new Operations();
        using var window = Create(operations);
        window.Initialize();
        Assert.False(Show(window));
        Assert.Throws<InvalidOperationException>(() => NativePopupWindow.ShowWithoutActivation(window));
        Assert.True(NativePopupWindow.TryPrepareOwner(Owner, window));
        Assert.False(NativePopupWindow.TryShowOwned(Owner with { Handle = 20 }, window,
            () => throw new Exception("must not run")));
        Assert.Equal(Owner, window.Owner);
        Assert.Empty(operations.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackMustLeaveTheSameWindowVisible(bool showThenHide)
    {
        using var window = Prepared(new());
        Assert.False(NativePopupWindow.TryShowOwned(Owner, window, () =>
        {
            if (showThenHide)
            {
                NativePopupWindow.ShowWithoutActivation(window);
                window.IsVisible = false;
            }
        }));
        Assert.True(Show(window));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CallbackCannotRebindOrRecursivelyAdmitDisplay(int operation)
    {
        using var window = Prepared(new());
        var failure = Assert.Throws<InvalidOperationException>(() => NativePopupWindow.TryShowOwned(Owner, window, () =>
        {
            if (operation == 0) window.BindOwner(Owner);
            else if (operation == 1) window.BindOwner(Owner with { Handle = 20 });
            else Show(window);
        }));
        Assert.NotNull(failure);
        Assert.Equal(Owner, window.Owner);
        Assert.False(window.IsVisible);
        Assert.True(Show(window));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackDisposalRetainsNativeOwnershipUntilTheCallbackAndLeaseEnd(bool retainLease)
    {
        var operations = new Operations();
        using var window = Prepared(operations);
        using var lease = retainLease ? window.AcquireRenderLease() : null;
        Assert.False(NativePopupWindow.TryShowOwned(Owner, window, () =>
        {
            Assert.False(NativeWindowLifetime.TryDispose(window));
            Assert.DoesNotContain("release", operations.Calls);
        }));
        Assert.Equal(!retainLease, window.IsReleased);
        lease?.Dispose();
        Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.Equal(1, operations.Calls.Count(call => call == "release"));
    }

    [Fact]
    public void OriginalCallbackFailureSurvivesFailedNativeRetirement()
    {
        var operations = new Operations();
        using var window = Prepared(operations);
        var failure = new InvalidOperationException("source preparation");
        try
        {
            operations.HideAccepted = false;
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                NativePopupWindow.TryShowOwned(Owner, window, () =>
                {
                    window.Dispose();
                    throw failure;
                })));
            Assert.IsType<InvalidOperationException>(failure.Data["PopupShowRetirement"]);
            Assert.False(window.IsReleased);
        }
        finally { operations.HideAccepted = true; }
        Assert.True(NativeWindowLifetime.TryDispose(window));
    }

    [Fact]
    public void ChangedViewIdentityCannotPublishVisibleSuccess()
    {
        var operations = new Operations();
        using var window = Prepared(operations);
        Assert.False(NativePopupWindow.TryShowOwned(Owner, window, () =>
        {
            NativePopupWindow.ShowWithoutActivation(window);
            operations.Geometry = operations.Geometry with { ContentView = 99 };
        }));
    }

    [Fact]
    public void ForeignProviderCannotSupplyItsOpaqueHandleAsGlfwIdentity()
    {
        var window = DispatchProxy.Create<IWindow, NoNativeProvider>();
        Assert.False(NativePopupWindow.TryPrepareOwner(NativeWindowHandle.Empty, window));
        Assert.False(NativePopupWindow.TryShowOwned(NativeWindowHandle.Empty, window,
            () => throw new Exception("must not run")));
        Assert.Throws<PlatformNotSupportedException>(() => NativePopupWindow.ShowWithoutActivation(window));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignThreadCannotDisplayTheOwnedPanel(bool throughAdmission)
    {
        using var window = Prepared(new());
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (throughAdmission) Show(window);
                else NativePopupWindow.ShowWithoutActivation(window);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(failure);
        Assert.False(window.IsVisible);
    }

    private static bool Show(IWindow window) => NativePopupWindow.TryShowOwned(Owner, window,
        () => NativePopupWindow.ShowWithoutActivation(window));

    private static CocoaPopupWindow Create(Operations operations) =>
        CocoaPopupWindowTests.Create(operations, sourceScheduled: true);

    private static CocoaPopupWindow Prepared(Operations operations)
    {
        var window = Create(operations);
        window.Initialize();
        Assert.True(NativePopupWindow.TryPrepareOwner(Owner, window));
        return window;
    }

    public class NoNativeProvider : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "get_IsInitialized" => true,
            "get_IsClosing" => false,
            "get_Native" => null,
            _ => throw new InvalidOperationException("Unexpected provider access: " + targetMethod.Name),
        };
    }
}
