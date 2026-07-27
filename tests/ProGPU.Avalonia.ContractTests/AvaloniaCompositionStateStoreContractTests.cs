using Avalonia.ProGpu;
using Xunit;

namespace ProGPU.Avalonia.ContractTests;

public sealed class AvaloniaCompositionStateStoreContractTests
{
    [Fact]
    public void Allocate_And_Lookup_Preserve_Identity()
    {
        var store = new AvaloniaCompositionStateStore<object>();
        var value = new object();

        ulong handle = store.Allocate(41, value);

        Assert.NotEqual(0UL, handle);
        Assert.True(store.TryGet(handle, 41, out object? resolved));
        Assert.Same(value, resolved);
        Assert.False(store.TryGet(handle, 42, out _));
        Assert.False(store.TryGet(0, 41, out _));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Released_Handle_Cannot_Resolve_Reused_Slot()
    {
        var store = new AvaloniaCompositionStateStore<object>();
        var first = new object();
        ulong firstHandle = store.Allocate(1, first);

        Assert.True(store.Release(firstHandle, 1, out object? released));
        Assert.Same(first, released);

        var second = new object();
        ulong secondHandle = store.Allocate(2, second);

        Assert.Equal((uint)firstHandle, (uint)secondHandle);
        Assert.NotEqual(firstHandle, secondHandle);
        Assert.False(store.TryGet(firstHandle, 1, out _));
        Assert.True(store.TryGet(secondHandle, 2, out object? resolved));
        Assert.Same(second, resolved);
    }

    [Fact]
    public void Release_Requires_Exact_Retained_Identity()
    {
        var store = new AvaloniaCompositionStateStore<object>();
        var value = new object();
        ulong handle = store.Allocate(7, value);

        Assert.False(store.Release(handle, 8, out _));
        Assert.True(store.TryGet(handle, 7, out object? resolved));
        Assert.Same(value, resolved);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Store_Grows_In_Bounded_Pages_And_Enumerates_Live_Entries()
    {
        var store = new AvaloniaCompositionStateStore<object>();
        for (int index = 0; index < 257; index++)
            store.Allocate(index + 1, new object());

        int liveCount = 0;
        for (int index = 0; index < store.AllocatedSlotCount; index++)
        {
            if (store.TryGetAt(
                    index,
                    out ulong handle,
                    out long retainedId,
                    out object? value))
            {
                Assert.NotEqual(0UL, handle);
                Assert.InRange(retainedId, 1, 257);
                Assert.NotNull(value);
                liveCount++;
            }
        }

        Assert.Equal(257, store.Count);
        Assert.Equal(257, store.AllocatedSlotCount);
        Assert.Equal(2, store.PageCount);
        Assert.Equal(257, liveCount);
    }

    [Fact]
    public void Clear_Releases_All_Pages()
    {
        var store = new AvaloniaCompositionStateStore<object>();
        store.Allocate(1, new object());
        store.Allocate(2, new object());

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.AllocatedSlotCount);
        Assert.Equal(0, store.PageCount);
    }
}
