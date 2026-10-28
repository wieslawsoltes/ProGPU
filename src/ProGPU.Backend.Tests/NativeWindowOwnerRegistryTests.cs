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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantLivenessReplacementCannotBeDeletedOrPublished(bool oldAlive)
    {
        nint key = (nint)0x505704;
        var old = new TestOwner(NativeWindowHandle.Empty);
        var replacement = new TestOwner(NativeWindowHandle.Empty);
        using IDisposable registration = NativeWindowOwnerRegistry.Register(key, old);
        IDisposable? current = null;
        try
        {
            old.ReadAlive = () =>
            {
                current = NativeWindowOwnerRegistry.Register(key, replacement);
                replacement.ReadAlive = () => throw new InvalidOperationException("No retry into replacement.");
                return oldAlive;
            };
            Assert.False(NativeWindowOwnerRegistry.TryResolve(key, out var resolved));
            Assert.Null(resolved);
            replacement.ReadAlive = null;
            Assert.True(NativeWindowOwnerRegistry.TryResolve(key, out resolved));
            Assert.Same(replacement, resolved);
        }
        finally { current?.Dispose(); }
    }

    [Fact]
    public void LivenessCallbackCannotPublishItsDisposedRegistration()
    {
        nint key = (nint)0x505705;
        var owner = new TestOwner(NativeWindowHandle.Empty);
        using IDisposable registration = NativeWindowOwnerRegistry.Register(key, owner);
        owner.ReadAlive = () => { registration.Dispose(); return true; };
        Assert.False(NativeWindowOwnerRegistry.TryResolve(key, out var resolved));
        Assert.Null(resolved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeHandleCallbackCannotPublishRetiredRegistration(bool replace)
    {
        nint key = (nint)0x505706;
        var original = new NativeWindowHandle(NativeWindowKind.X11, (nint)7, (nint)8, "old");
        var owner = new TestOwner(original);
        var replacement = new TestOwner(NativeWindowHandle.Empty);
        using IDisposable registration = NativeWindowOwnerRegistry.Register(key, owner);
        IDisposable? current = null;
        try
        {
            owner.ReadHandle = () =>
            {
                if (replace) current = NativeWindowOwnerRegistry.Register(key, replacement);
                else registration.Dispose();
                owner.ReadAlive = () => throw new InvalidOperationException("Retired provider must not be queried again.");
                return original;
            };
            Assert.False(NativeWindowOwnerRegistry.TryResolveNativeHandle(key, out var resolved));
            Assert.Equal(NativeWindowHandle.Empty, resolved);
            if (replace)
            {
                Assert.True(NativeWindowOwnerRegistry.TryResolve(key, out var live));
                Assert.Same(replacement, live);
            }
        }
        finally { current?.Dispose(); }
    }

    [Fact]
    public void NativeHandleCallbackCannotPublishOwnerItRetired()
    {
        nint key = (nint)0x505707;
        var original = new NativeWindowHandle(NativeWindowKind.X11, (nint)7, (nint)8, "old");
        var owner = new TestOwner(original);
        using IDisposable registration = NativeWindowOwnerRegistry.Register(key, owner);
        owner.ReadHandle = () => { owner.IsAlive = false; return original; };
        Assert.False(NativeWindowOwnerRegistry.TryResolveNativeHandle(key, out var resolved));
        Assert.Equal(NativeWindowHandle.Empty, resolved);
        Assert.False(NativeWindowOwnerRegistry.TryResolve(key, out _));
    }

    [Fact]
    public void FinalLivenessCallbackCannotPublishAReplacedNativeOwner()
    {
        nint key = (nint)0x505709;
        var original = new NativeWindowHandle(NativeWindowKind.X11, (nint)7, (nint)8, "old");
        var owner = new TestOwner(original);
        var replacement = new TestOwner(NativeWindowHandle.Empty);
        using IDisposable registration = NativeWindowOwnerRegistry.Register(key, owner);
        IDisposable? current = null;
        try
        {
            owner.ReadHandle = () =>
            {
                owner.ReadAlive = () =>
                {
                    current = NativeWindowOwnerRegistry.Register(key, replacement);
                    return true;
                };
                return original;
            };
            Assert.False(NativeWindowOwnerRegistry.TryResolveNativeHandle(key, out var resolved));
            Assert.Equal(NativeWindowHandle.Empty, resolved);
            Assert.True(NativeWindowOwnerRegistry.TryResolve(key, out var live));
            Assert.Same(replacement, live);
        }
        finally { current?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderExceptionRetainsOriginalIdentityAndRegistration(bool duringHandle)
    {
        nint key = (nint)0x505708;
        var owner = new TestOwner(NativeWindowHandle.Empty);
        using IDisposable registration = NativeWindowOwnerRegistry.Register(key, owner);
        var error = new InvalidOperationException("Original provider exception");
        if (duringHandle) owner.ReadHandle = () => throw error;
        else owner.ReadAlive = () => throw error;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
            NativeWindowOwnerRegistry.TryResolveNativeHandle(key, out _)));
        owner.ReadAlive = null;
        owner.ReadHandle = null;
        Assert.True(NativeWindowOwnerRegistry.TryResolve(key, out var resolved));
        Assert.Same(owner, resolved);
    }

    private sealed class TestOwner : INativeWindowOwner
    {
        public TestOwner(NativeWindowHandle nativeHandle)
        {
            NativeHandle = nativeHandle;
        }

        private NativeWindowHandle _handle;
        private bool _alive = true;
        public Func<bool>? ReadAlive { get; set; }
        public Func<NativeWindowHandle>? ReadHandle { get; set; }

        public NativeWindowHandle NativeHandle { get => ReadHandle?.Invoke() ?? _handle; set => _handle = value; }

        public bool IsAlive { get => ReadAlive?.Invoke() ?? _alive; set => _alive = value; }

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
