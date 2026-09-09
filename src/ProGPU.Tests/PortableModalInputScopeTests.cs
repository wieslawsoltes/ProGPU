using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableModalInputScopeTests
{
    [Fact]
    public void NestedScopesAdmitOnlyTheCurrentIdentityAndRestoreOuterState()
    {
        object owner = new(), dialog = new(), nested = new();
        Assert.False(PortableModalInputScope.IsActive);
        Assert.True(PortableModalInputScope.AllowsInput(null));
        using (PortableModalInputScope.Enter(dialog))
        {
            Assert.True(PortableModalInputScope.IsActive);
            Assert.True(PortableModalInputScope.AllowsInput(dialog));
            Assert.False(PortableModalInputScope.AllowsInput(owner));
            Assert.False(PortableModalInputScope.AllowsInput(null));
            using (PortableModalInputScope.Enter(nested))
            {
                Assert.False(PortableModalInputScope.AllowsInput(dialog));
                Assert.True(PortableModalInputScope.AllowsInput(nested));
            }
            Assert.True(PortableModalInputScope.AllowsInput(dialog));
        }
        Assert.False(PortableModalInputScope.IsActive);
        Assert.True(PortableModalInputScope.AllowsInput(owner));
    }

    [Fact]
    public void IncorrectReleaseOrderDoesNotRemoveEitherScope()
    {
        object owner = new(), child = new();
        var outer = PortableModalInputScope.Enter(owner);
        var inner = PortableModalInputScope.Enter(child);
        try
        {
            Assert.Throws<InvalidOperationException>(outer.Dispose);
            Assert.True(PortableModalInputScope.AllowsInput(child));
        }
        finally { inner.Dispose(); outer.Dispose(); }
        outer.Dispose(); // Owning-thread disposal is idempotent.
        Assert.False(PortableModalInputScope.IsActive);
    }

    [Fact]
    public void ThreadLocalAdmissionCannotBeReleasedFromAnotherThread()
    {
        object owner = new();
        using var scope = PortableModalInputScope.Enter(owner);
        Exception? failure = null;
        bool otherThreadActive = true;
        var thread = new Thread(() =>
        {
            otherThreadActive = PortableModalInputScope.IsActive;
            try { scope.Dispose(); }
            catch (Exception error) { failure = error; }
        });
        thread.Start(); thread.Join();
        Assert.False(otherThreadActive);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.True(PortableModalInputScope.AllowsInput(owner));
    }

    [Fact]
    public void ExceptionsRestoreUnrestrictedAdmission()
    {
        Assert.Throws<ArgumentNullException>(() => PortableModalInputScope.Enter(null!));
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var scope = PortableModalInputScope.Enter(new object());
            throw new InvalidOperationException("Dialog failed.");
        }));
        Assert.False(PortableModalInputScope.IsActive);
    }
}
