using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public class PortableDefaultServiceSlotTests
{
    [Fact]
    public void DefaultSurvivesOverrideDisposalAndRepeatedInitialization()
    {
        var slot = new PortableDefaultServiceSlot<object>();
        var fallback = new object(); var custom = new object();
        Assert.Null(slot.Current);
        using var registration = slot.Register(custom);
        slot.EnsureDefault(fallback);
        slot.EnsureDefault(new object());
        Assert.Same(custom, slot.Current);
        registration.Dispose(); registration.Dispose();
        Assert.Same(fallback, slot.Current);
    }

    [Fact]
    public void OldRegistrationsCannotClearNewOverridesOrResurrectPriorOnes()
    {
        var slot = new PortableDefaultServiceSlot<object>();
        var fallback = new object(); var current = new object();
        slot.EnsureDefault(fallback);
        using var first = slot.Register(new object());
        using var second = slot.Register(current);
        first.Dispose();
        Assert.Same(current, slot.Current);
        second.Dispose();
        Assert.Same(fallback, slot.Current);
        first.Dispose();
        Assert.Same(fallback, slot.Current);
    }

    [Fact]
    public void UninitializedSlotRetainsExplicitOnlySemanticsAndRejectsNull()
    {
        var slot = new PortableDefaultServiceSlot<object>();
        using var registration = slot.Register(new object());
        registration.Dispose();
        Assert.Null(slot.Current);
        Assert.Throws<ArgumentNullException>(() => slot.Register(null!));
        Assert.Throws<ArgumentNullException>(() => slot.EnsureDefault(null!));
    }

    [Fact]
    public void ConcurrentOverrideChurnCannotRemoveTheDefault()
    {
        var slot = new PortableDefaultServiceSlot<object>();
        var fallback = new object(); slot.EnsureDefault(fallback);
        Parallel.For(0, 256, _ =>
        {
            using var registration = slot.Register(new object());
            slot.EnsureDefault(new object());
            Assert.NotNull(slot.Current);
        });
        Assert.Same(fallback, slot.Current);
    }
}
