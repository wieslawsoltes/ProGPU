using System.Reflection;
using ProGPU.Backend;
using Silk.NET.Windowing;
using Xunit;
using Operations = ProGPU.Tests.CocoaPopupWindowTests.Operations;

namespace ProGPU.Tests;

public sealed class CocoaPopupRetirementTests
{
    [Fact]
    public void NullWindowIsRejected()
        => Assert.Throws<ArgumentNullException>(() => NativeWindowLifetime.TryDispose(null!));

    [Fact]
    public void UninitializedOwnedWindowRetiresWithoutCreatingNativeResources()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations);
        Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.Empty(operations.Calls);
        Assert.Null(window.Native);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualNativeRetirementCompletesOnceForEitherFactory(bool sourceScheduled)
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations, sourceScheduled: sourceScheduled);
        window.Initialize();
        Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.False(window.IsInitialized);
        Assert.Null(window.Native);
        Assert.Equal(new[] { "hide", "release" }, operations.Calls);
    }

    [Fact]
    public void HostMustRetainTheWindowUntilTheRenderLeaseIsReleased()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations);
        window.Initialize();
        using var lease = window.AcquireRenderLease();
        Assert.False(NativeWindowLifetime.TryDispose(window));
        Assert.Equal(operations.ContentView, lease.ContentView);
        Assert.Equal(operations.Window.Handle, window.Native!.Cocoa);
        Assert.DoesNotContain("release", operations.Calls);
        lease.Dispose();
        Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.False(window.IsInitialized);
        Assert.Null(window.Native);
        Assert.Equal(1, operations.Calls.Count(call => call == "release"));
    }

    [Fact]
    public void NativeCallbackCannotPublishCompletionOrDestroyThePanel()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations);
        window.Initialize();
        operations.Input.BeginNativeCallback();
        try
        {
            Assert.False(NativeWindowLifetime.TryDispose(window));
            Assert.False(NativeWindowLifetime.TryDispose(window));
            Assert.Empty(operations.Calls);
        }
        finally { operations.Input.EndNativeCallback(); }
        Assert.False(window.IsReleased);
        Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.Equal(new[] { "hide", "release" }, operations.Calls);
    }

    [Fact]
    public void ManagedInputCallbackDefersCompletionUntilTheCurrentDrainUnwinds()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations);
        window.Initialize();
        using var input = NativeWindowInput.CreateInput(window);
        window.IsVisible = true;
        var native = (INativePointerInputContext)input;
        int downs = 0;
        native.PointerEvent += value =>
        {
            if (value.Kind != NativePointerEventKind.Down) return;
            downs++;
            Assert.False(NativeWindowLifetime.TryDispose(window));
            Assert.DoesNotContain("release", operations.Calls);
        };
        Assert.True(operations.Input.TryWrite(new(CocoaPopupPointerKind.Down, 1, 2, 3, 0, 1, 0)));
        window.DoEvents();
        Assert.Equal(1, downs);
        Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.Equal(1, operations.Calls.Count(call => call == "release"));
    }

    [Fact]
    public void InitializationCallbackCannotPublishRetirementBeforeCreationUnwinds()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations);
        window.Load += () =>
        {
            Assert.False(NativeWindowLifetime.TryDispose(window));
            Assert.DoesNotContain("release", operations.Calls);
        };
        window.Initialize();
        Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.Equal(1, operations.Calls.Count(call => call == "release"));
    }

    [Fact]
    public void FailedNativeHideRemainsRetryableAndNeverReportsCompletion()
    {
        var operations = new Operations { HideAccepted = false };
        using var window = CocoaPopupWindowTests.Create(operations);
        window.Initialize();
        Assert.Throws<InvalidOperationException>(() => NativeWindowLifetime.TryDispose(window));
        Assert.False(window.IsReleased);
        Assert.DoesNotContain("release", operations.Calls);
        operations.HideAccepted = true;
        Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.Equal(1, operations.Calls.Count(call => call == "release"));
    }

    [Fact]
    public void ForeignThreadCannotRetireAnOwnedWindow()
    {
        var operations = new Operations();
        using var window = CocoaPopupWindowTests.Create(operations);
        window.Initialize();
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() => NativeWindowLifetime.TryDispose(window)));
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Empty(operations.Calls);
        Assert.True(NativeWindowLifetime.TryDispose(window));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherProvidersKeepTheirOwnDisposalWithoutPropertyOrHandleProbes(bool throws)
    {
        var window = DispatchProxy.Create<IWindow, ForeignWindow>();
        var provider = (ForeignWindow)(object)window;
        var failure = new InvalidOperationException("provider disposal failed");
        provider.Failure = throws ? failure : null;
        if (throws)
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => NativeWindowLifetime.TryDispose(window)));
        else
            Assert.True(NativeWindowLifetime.TryDispose(window));
        Assert.Equal(1, provider.Disposals);
    }

    public class ForeignWindow : DispatchProxy
    {
        public int Disposals;
        public Exception? Failure;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IDisposable.Dispose), targetMethod!.Name);
            Disposals++;
            if (Failure is not null) throw Failure;
            return null;
        }
    }
}
