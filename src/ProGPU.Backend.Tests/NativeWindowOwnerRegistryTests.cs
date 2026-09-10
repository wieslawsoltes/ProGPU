using ProGPU.Backend;
using Xunit;

namespace ProGPU.Backend.Tests;

public sealed class NativeWindowOwnerRegistryTests
{
    [Fact]
    public void RegistrationResolvesTypedOwnerAndNativeHandleUntilDisposed()
    {
        nint presentationHandle = (nint)0x505701;
        var owner = new TestOwner(new NativeWindowHandle(
            NativeWindowKind.X11,
            (nint)0x1234,
            (nint)0x5678,
            "X11"));

        using IDisposable registration = NativeWindowOwnerRegistry.Register(presentationHandle, owner);

        Assert.True(NativeWindowOwnerRegistry.TryResolve(presentationHandle, out INativeWindowOwner? resolved));
        Assert.Same(owner, resolved);
        Assert.True(NativeWindowOwnerRegistry.TryResolveNativeHandle(presentationHandle, out NativeWindowHandle native));
        Assert.Equal(owner.NativeHandle, native);

        registration.Dispose();

        Assert.False(NativeWindowOwnerRegistry.TryResolve(presentationHandle, out _));
        Assert.False(NativeWindowOwnerRegistry.TryResolveNativeHandle(presentationHandle, out native));
        Assert.Equal(NativeWindowHandle.Empty, native);
    }

    [Fact]
    public void StaleRegistrationCannotRemoveReplacement()
    {
        nint presentationHandle = (nint)0x505702;
        var first = new TestOwner(new NativeWindowHandle(NativeWindowKind.X11, (nint)1, (nint)2, "first"));
        var replacement = new TestOwner(new NativeWindowHandle(NativeWindowKind.X11, (nint)3, (nint)4, "replacement"));
        IDisposable staleRegistration = NativeWindowOwnerRegistry.Register(presentationHandle, first);
        using IDisposable replacementRegistration = NativeWindowOwnerRegistry.Register(presentationHandle, replacement);

        staleRegistration.Dispose();

        Assert.True(NativeWindowOwnerRegistry.TryResolve(presentationHandle, out INativeWindowOwner? resolved));
        Assert.Same(replacement, resolved);
    }

    [Fact]
    public void DeadOrNotYetNativeOwnerDoesNotYieldNativeHandle()
    {
        nint presentationHandle = (nint)0x505703;
        var owner = new TestOwner(NativeWindowHandle.Empty);
        using IDisposable registration = NativeWindowOwnerRegistry.Register(presentationHandle, owner);

        Assert.True(NativeWindowOwnerRegistry.TryResolve(presentationHandle, out _));
        Assert.False(NativeWindowOwnerRegistry.TryResolveNativeHandle(presentationHandle, out _));

        owner.IsAlive = false;

        Assert.False(NativeWindowOwnerRegistry.TryResolve(presentationHandle, out _));
    }

    [Fact]
    public void RegistrationRejectsZeroHandleAndDeadOwner()
    {
        var liveOwner = new TestOwner(NativeWindowHandle.Empty);
        var deadOwner = new TestOwner(NativeWindowHandle.Empty) { IsAlive = false };

        Assert.Throws<ArgumentException>(() => NativeWindowOwnerRegistry.Register(0, liveOwner));
        Assert.Throws<ArgumentException>(() => NativeWindowOwnerRegistry.Register((nint)1, deadOwner));
    }

    private sealed class TestOwner : INativeWindowOwner
    {
        public TestOwner(NativeWindowHandle nativeHandle)
        {
            NativeHandle = nativeHandle;
        }

        public NativeWindowHandle NativeHandle { get; set; }

        public bool IsAlive { get; set; } = true;

        public bool IsVisible { get; set; } = true;

        public bool IsEnabled { get; private set; } = true;

        public bool TrySetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            return true;
        }

        public bool TryActivate() => true;
    }
}
