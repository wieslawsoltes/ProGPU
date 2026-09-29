using System.Reflection;
using ProGPU.Backend;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Xunit;
using Operations = ProGPU.Tests.CocoaPopupWindowTests.Operations;

namespace ProGPU.Tests;

public sealed class CocoaPopupInputTransparencyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TransparencyAndEnabledStateRemainIndependent(bool transparent, bool enabled)
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        NativeWindowInput.SetInputTransparent(window, transparent);
        Assert.True(window.SetInputAllowed(enabled));
        Assert.Equal(enabled && !transparent, operations.Input.CanReceive);
        Assert.Equal(enabled && !transparent, operations.InputRequests[^1]);
        NativeWindowInput.SetInputTransparent(window, false);
        Assert.Equal(enabled, operations.Input.CanReceive);
        Assert.True(window.SetInputAllowed(true));
        Assert.True(operations.Input.CanReceive);
    }

    [Fact]
    public void TransparencyBeforeInputAdmissionSurvivesContextReplacementAndReopen()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        NativeWindowInput.SetInputTransparent(window, true);
        using (var input = NativeWindowInput.CreateInput(window))
            Assert.False(operations.Input.CanReceive);
        window.IsVisible = false;
        using var replacement = NativeWindowInput.CreateInput(window);
        window.IsVisible = true;
        Assert.False(operations.Input.CanReceive);
        Assert.All(operations.InputRequests, allowed => Assert.False(allowed));
        NativeWindowInput.SetInputTransparent(window, false);
        Assert.True(operations.Input.CanReceive);
    }

    [Fact]
    public void OwnerBindingCannotOverrideTransparencyOrCreateInputWithoutAContext()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations, sourceScheduled: true);
        window.Initialize();
        NativeWindowInput.SetInputTransparent(window, true);
        using var input = NativeWindowInput.CreateInput(window);
        var owner = new NativeWindowHandle(NativeWindowKind.Cocoa, 10, 0, "NSWindow");
        Assert.True(window.BindOwner(owner));
        window.IsVisible = true;
        Assert.False(operations.Input.CanReceive);
        window.IsVisible = false;
        Assert.True(window.BindOwner(NativeWindowHandle.Empty));
        NativeWindowInput.SetInputTransparent(window, false);
        Assert.False(operations.InputRequests[^1]);
        Assert.True(window.BindOwner(owner with { Handle = 11 }));
        window.IsVisible = true;
        Assert.True(operations.Input.CanReceive);
        input.Dispose();
        NativeWindowInput.SetInputTransparent(window, false);
        Assert.False(operations.Input.CanReceive);
    }

    [Fact]
    public void TransparencyDuringDownCancelsPressedStateAndDiscardsCopiedTailWithoutClick()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        var kinds = new List<NativePointerEventKind>();
        ((INativePointerInputContext)input).PointerEvent += value => kinds.Add(value.Kind);
        input.Mice[0].MouseDown += (_, _) => NativeWindowInput.SetInputTransparent(window, true);
        int ups = 0, clicks = 0;
        input.Mice[0].MouseUp += (_, _) => ups++;
        input.Mice[0].Click += (_, _, _) => clicks++;
        Assert.True(operations.Input.TryWrite(new(CocoaPopupPointerKind.Down, 2, 3, 1, 0, 1, 0)));
        Assert.True(operations.Input.TryWrite(new(CocoaPopupPointerKind.Up, 2, 3, 2, 0, 1, 0)));
        window.DoEvents();
        Assert.Equal(new[] { NativePointerEventKind.Down, NativePointerEventKind.Cancel }, kinds);
        Assert.False(input.Mice[0].IsButtonPressed(MouseButton.Left));
        Assert.Equal(0, ups);
        Assert.Equal(0, clicks);
        NativeWindowInput.SetInputTransparent(window, false);
        window.DoEvents();
        Assert.Equal(2, kinds.Count);
    }

    [Fact]
    public void RejectedNativePolicyReportsFailureAndBlocksQueuedInput()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        operations.InputAdmission = _ => false;
        try
        {
            Assert.Throws<PlatformNotSupportedException>(() => NativeWindowInput.SetInputTransparent(window, true));
            Assert.False(operations.Input.CanReceive);
        }
        finally { operations.InputAdmission = null; }
    }

    [Fact]
    public void UnsupportedProviderDoesNotReadOrCastOpaqueHandle()
    {
        var window = DispatchProxy.Create<IWindow, CocoaPopupAdmissionTests.NoNativeProvider>();
        Assert.Throws<PlatformNotSupportedException>(() => NativeWindowInput.SetInputTransparent(window, true));
        Assert.Throws<PlatformNotSupportedException>(() => NativeWindowInput.SetInputTransparent(window, false));
        Assert.Throws<ArgumentNullException>(() => NativeWindowInput.SetInputTransparent(null!, true));
    }

    [Fact]
    public void UninitializedAndDisposedOwnedWindowsRejectPolicyChanges()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations);
        Assert.Throws<InvalidOperationException>(() => NativeWindowInput.SetInputTransparent(window, true));
        Assert.Empty(operations.InputRequests);
        window.Initialize();
        window.IsVisible = true;
        using var input = NativeWindowInput.CreateInput(window);
        Assert.True(operations.Input.CanReceive);
        window.Dispose();
        Assert.Throws<ObjectDisposedException>(() => NativeWindowInput.SetInputTransparent(window, true));
    }

    [Fact]
    public void ClosingWindowRejectsPolicyWithoutNativeMutation()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        window.IsClosing = true;
        int requests = operations.InputRequests.Count;
        Assert.Throws<InvalidOperationException>(() => NativeWindowInput.SetInputTransparent(window, true));
        Assert.Equal(requests, operations.InputRequests.Count);
    }

    [Fact]
    public void ForeignThreadCannotChangePolicy()
    {
        var operations = new Operations();
        using var window = Ready(operations);
        using var input = NativeWindowInput.CreateInput(window);
        int requests = operations.InputRequests.Count;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { NativeWindowInput.SetInputTransparent(window, true); }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(requests, operations.InputRequests.Count);
        Assert.True(operations.Input.CanReceive);
    }

    private static CocoaPopupWindow Ready(Operations operations)
    {
        var window = CocoaPopupWindowTests.Create(operations);
        window.Initialize();
        window.IsVisible = true;
        return window;
    }
}
