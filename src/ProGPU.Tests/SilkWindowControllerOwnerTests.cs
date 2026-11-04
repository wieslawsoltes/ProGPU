using ProGPU.Backend;
using Xunit;
using Operations = ProGPU.Tests.CocoaPopupWindowTests.Operations;

namespace ProGPU.Tests;

public sealed class SilkWindowControllerOwnerTests
{
    [Fact]
    public void ClosingCallbackCanAssignItsLiveNativeOwnerToAConfirmationWindow()
    {
        var ownerOperations = CreateOperations(10);
        var childOperations = CreateOperations(20);
        using var ownerWindow = Create(ownerOperations);
        using var childWindow = Create(childOperations);
        using var owner = new SilkWindowController(ownerWindow);
        using var child = new SilkWindowController(childWindow);
        Assert.True(owner.Attach());
        Assert.True(child.Attach());
        int callbacks = 0;
        ownerWindow.Closing += () =>
        {
            callbacks++;
            Assert.True(ownerWindow.IsClosing);
            Assert.True(ownerWindow.IsInitialized);
            Assert.True(child.TrySetOwner(owner));
            Assert.Equal(ownerOperations.Window, child.Parent);
            Assert.Equal(ownerOperations.Window, childWindow.Owner);
        };

        ownerWindow.Close();

        Assert.Equal(1, callbacks);
        Assert.Equal(ownerOperations.Window, childOperations.OwnerRequests[^1]);
        Assert.True(child.TrySetOwner(null));
        Assert.Equal(NativeWindowHandle.Empty, child.Parent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeOwnerRefusalOrExceptionDoesNotPublishTheRequestedOwner(bool throws)
    {
        var ownerOperations = CreateOperations(10);
        var childOperations = CreateOperations(20);
        using var ownerWindow = Create(ownerOperations);
        using var childWindow = Create(childOperations);
        using var owner = new SilkWindowController(ownerWindow);
        using var child = new SilkWindowController(childWindow);
        Assert.True(owner.Attach());
        Assert.True(child.Attach());
        childOperations.OwnerAccepted = false;
        var failure = new InvalidOperationException("native ownership refused");
        childOperations.OnBindOwner = () => { if (throws) throw failure; };
        ownerWindow.Closing += () =>
        {
            if (throws) Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => child.TrySetOwner(owner)));
            else Assert.False(child.TrySetOwner(owner));
        };

        ownerWindow.Close();

        Assert.Equal(NativeWindowHandle.Empty, child.Parent);
        Assert.Equal(NativeWindowHandle.Empty, childWindow.Owner);
        Assert.Equal(ownerOperations.Window, childOperations.OwnerRequests[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposedOwnerControllerOrNativeWindowIsRejected(bool disposeController)
    {
        var childOperations = CreateOperations(20);
        using var ownerWindow = Create(CreateOperations(10));
        using var childWindow = Create(childOperations);
        using var owner = new SilkWindowController(ownerWindow);
        using var child = new SilkWindowController(childWindow);
        Assert.True(owner.Attach());
        Assert.True(child.Attach());
        if (disposeController) owner.Dispose();
        else ownerWindow.Dispose();
        int requests = childOperations.OwnerRequests.Count;

        Assert.False(child.TrySetOwner(owner));
        Assert.Equal(requests, childOperations.OwnerRequests.Count);
        Assert.Equal(NativeWindowHandle.Empty, child.Parent);
    }

    [Fact]
    public void OwnerRetirementDuringNativeBindingDoesNotPublishControllerOwnership()
    {
        var childOperations = CreateOperations(20);
        using var ownerWindow = Create(CreateOperations(10));
        using var childWindow = Create(childOperations);
        using var owner = new SilkWindowController(ownerWindow);
        using var child = new SilkWindowController(childWindow);
        Assert.True(owner.Attach());
        Assert.True(child.Attach());
        childOperations.OnBindOwner = owner.Dispose;

        Assert.False(child.TrySetOwner(owner));
        Assert.Equal(NativeWindowHandle.Empty, child.Parent);
    }

    [Fact]
    public void SelfCyclesAndClosingChildrenRemainRejected()
    {
        using var ownerWindow = Create(CreateOperations(10));
        using var childWindow = Create(CreateOperations(20));
        using var owner = new SilkWindowController(ownerWindow);
        using var child = new SilkWindowController(childWindow);
        Assert.True(owner.Attach());
        Assert.True(child.Attach());
        Assert.False(owner.TrySetOwner(owner));
        Assert.True(child.TrySetOwner(owner));
        Assert.False(owner.TrySetOwner(child));
        childWindow.Close();
        Assert.False(child.TrySetOwner(null));
        Assert.Equal(owner.Handle, child.Parent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantOwnerBindingCannotPublishACycle(bool indirect)
    {
        var childOperations = CreateOperations(20);
        using var ownerWindow = Create(CreateOperations(10));
        using var childWindow = Create(childOperations);
        using var ancestorWindow = Create(CreateOperations(30));
        using var owner = new SilkWindowController(ownerWindow);
        using var child = new SilkWindowController(childWindow);
        using var ancestor = new SilkWindowController(ancestorWindow);
        Assert.True(owner.Attach());
        Assert.True(child.Attach());
        Assert.True(ancestor.Attach());
        if (indirect) Assert.True(owner.TrySetOwner(ancestor));
        childOperations.OnBindOwner = () =>
        {
            // The original cycle check has already run. A native setter can
            // dispatch source callbacks that change another controller's owner.
            childOperations.OnBindOwner = null;
            Assert.True((indirect ? ancestor : owner).TrySetOwner(child));
        };

        Assert.False(child.TrySetOwner(owner));
        Assert.Equal(NativeWindowHandle.Empty, child.Parent);
        Assert.Equal(child.Handle, (indirect ? ancestor : owner).Parent);
    }

    private static Operations CreateOperations(nint handle)
    {
        var window = new NativeWindowHandle(NativeWindowKind.Cocoa, handle, 0, "NSPanel");
        var operations = new Operations { Window = window };
        operations.Geometry = operations.Geometry with { Window = window };
        return operations;
    }

    private static CocoaPopupWindow Create(Operations operations)
    {
        var window = CocoaPopupWindowTests.Create(operations, sourceScheduled: true);
        window.Initialize();
        return window;
    }
}
