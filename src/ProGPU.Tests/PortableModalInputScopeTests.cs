using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortableModalInputScopeTests
{
    [Fact]
    public void NativeSurfacesFollowNestedPolicyIncludingPopupsAndNewWindows()
    {
        object owner = new(), dialog = new(), nested = new();
        bool ownerAllowed = false, popupAllowed = false, dialogAllowed = false, lateAllowed = true;
        using var first = PortableModalInputScope.RegisterWindow(owner, value => ownerAllowed = value);
        using var popup = PortableModalInputScope.RegisterWindow(owner, value => popupAllowed = value);
        using var second = PortableModalInputScope.RegisterWindow(dialog, value => dialogAllowed = value);
        Assert.True(ownerAllowed && popupAllowed && dialogAllowed);
        using (PortableModalInputScope.Enter(dialog))
        {
            Assert.False(ownerAllowed || popupAllowed);
            Assert.True(dialogAllowed);
            using var late = PortableModalInputScope.RegisterWindow(owner, value => lateAllowed = value);
            Assert.False(lateAllowed);
            using (PortableModalInputScope.Enter(nested)) Assert.False(dialogAllowed);
            Assert.True(dialogAllowed);
            Assert.False(ownerAllowed || popupAllowed || lateAllowed);
        }
        Assert.True(ownerAllowed && popupAllowed && dialogAllowed);
        Assert.True(PortableModalInputScope.IsNativeInputPolicySynchronized);
    }

    [Fact]
    public void FailedNativeEntryRestoresPreviousScopeAndEverySurvivingGate()
    {
        bool firstAllowed = true, lastAllowed = true;
        using var first = PortableModalInputScope.RegisterWindow(new object(), value => firstAllowed = value);
        using var failing = PortableModalInputScope.RegisterWindow(new object(), value =>
        {
            if (!value) throw new InvalidOperationException("Native disable rejected.");
        });
        using var last = PortableModalInputScope.RegisterWindow(new object(), value => lastAllowed = value);
        Assert.Throws<AggregateException>(() => PortableModalInputScope.Enter(new object()));
        Assert.False(PortableModalInputScope.IsActive);
        Assert.True(firstAllowed && lastAllowed);
        Assert.True(PortableModalInputScope.IsNativeInputPolicySynchronized);
    }

    [Fact]
    public void ExitFailureStillReleasesScopeAndPublishesOtherGates()
    {
        bool rejectEnable = false, allowed = true;
        var failing = PortableModalInputScope.RegisterWindow(new object(), value =>
        {
            if (value && rejectEnable) throw new InvalidOperationException("Native enable rejected.");
        });
        using var other = PortableModalInputScope.RegisterWindow(new object(), value => allowed = value);
        var scope = PortableModalInputScope.Enter(new object());
        try
        {
            rejectEnable = true;
            Assert.Throws<AggregateException>(scope.Dispose);
            Assert.False(PortableModalInputScope.IsActive);
            Assert.True(allowed);
            Assert.False(PortableModalInputScope.IsNativeInputPolicySynchronized);
        }
        finally
        {
            rejectEnable = false;
            failing.Dispose();
            using var recovered = PortableModalInputScope.Enter(new object());
        }
        Assert.True(PortableModalInputScope.IsNativeInputPolicySynchronized);
    }

    [Fact]
    public void PublicationImmediatelyAdmitsCallbackCreatedSurfaces()
    {
        object owner = new();
        IDisposable? created = null;
        var admissions = new List<bool>();
        using var first = PortableModalInputScope.RegisterWindow(owner, allowed =>
        {
            if (!allowed && created == null)
                created = PortableModalInputScope.RegisterWindow(owner, admissions.Add);
        });
        try
        {
            using (PortableModalInputScope.Enter(new object()))
            {
                Assert.NotNull(created);
                Assert.Equal(new[] { false }, admissions);
            }
            Assert.Equal(new[] { false, true }, admissions);
        }
        finally { created?.Dispose(); }
    }

    [Fact]
    public void PublicationAllowsSurfaceRemovalButRejectsRecursiveScopeMutation()
    {
        IDisposable? removed = null;
        using var first = PortableModalInputScope.RegisterWindow(new object(), allowed =>
        {
            if (!allowed)
            {
                removed?.Dispose();
                Assert.Throws<InvalidOperationException>(() => PortableModalInputScope.Enter(new object()));
            }
        });
        int removedCalls = 0;
        removed = PortableModalInputScope.RegisterWindow(new object(), _ => removedCalls++);
        using (PortableModalInputScope.Enter(new object())) Assert.Equal(2, removedCalls);
        removed.Dispose();
    }

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
