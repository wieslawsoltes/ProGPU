using System;
using System.Diagnostics.CodeAnalysis;

namespace Avalonia.ProGpu;

/// <summary>
/// Stores retained compositor state behind compact generation-checked handles.
/// </summary>
/// <remarks>
/// Allocation and lookup are O(1). Storage grows in 256-entry pages, so adding
/// N live entries uses O(N) references with at most 255 unused slots in the
/// final page. Released slots are reused without allocating and advance their
/// generation before another value can occupy the same index.
/// </remarks>
internal sealed class AvaloniaCompositionStateStore<T>
    where T : class
{
    private const int PageShift = 8;
    private const int PageSize = 1 << PageShift;
    private const int PageMask = PageSize - 1;

    private Slot[][] _pages = Array.Empty<Slot[]>();
    private int _slotCount;
    private int _count;
    private int _freeHead = -1;

    internal int Count => _count;
    internal int AllocatedSlotCount => _slotCount;
    internal int PageCount => (_slotCount + PageMask) >> PageShift;

    internal ulong Allocate(long retainedId, T value)
    {
        if (retainedId <= 0)
            throw new ArgumentOutOfRangeException(nameof(retainedId));
        ArgumentNullException.ThrowIfNull(value);

        int index;
        ref Slot slot = ref GetAllocationSlot(out index);
        if (slot.Generation == 0)
            slot.Generation = 1;
        slot.RetainedId = retainedId;
        slot.Value = value;
        slot.NextFree = -1;
        _count++;
        return Encode(index, slot.Generation);
    }

    internal bool TryGet(
        ulong handle,
        long retainedId,
        [NotNullWhen(true)] out T? value)
    {
        if (!TryDecode(handle, out int index, out uint generation) ||
            index >= _slotCount)
        {
            value = null;
            return false;
        }

        ref Slot slot = ref GetSlot(index);
        if (slot.Generation != generation ||
            slot.RetainedId != retainedId ||
            slot.Value is null)
        {
            value = null;
            return false;
        }

        value = slot.Value;
        return true;
    }

    internal bool TryGetAt(
        int index,
        out ulong handle,
        out long retainedId,
        [NotNullWhen(true)] out T? value)
    {
        if ((uint)index >= (uint)_slotCount)
        {
            handle = 0;
            retainedId = 0;
            value = null;
            return false;
        }

        ref Slot slot = ref GetSlot(index);
        if (slot.Value is null)
        {
            handle = 0;
            retainedId = 0;
            value = null;
            return false;
        }

        handle = Encode(index, slot.Generation);
        retainedId = slot.RetainedId;
        value = slot.Value;
        return true;
    }

    internal bool Release(
        ulong handle,
        long retainedId,
        [NotNullWhen(true)] out T? value)
    {
        if (!TryDecode(handle, out int index, out uint generation) ||
            index >= _slotCount)
        {
            value = null;
            return false;
        }

        ref Slot slot = ref GetSlot(index);
        if (slot.Generation != generation ||
            slot.RetainedId != retainedId ||
            slot.Value is null)
        {
            value = null;
            return false;
        }

        value = slot.Value;
        slot.Value = null;
        slot.RetainedId = 0;
        slot.Generation = NextGeneration(slot.Generation);
        slot.NextFree = _freeHead;
        _freeHead = index;
        _count--;
        return true;
    }

    internal void Clear()
    {
        _pages = Array.Empty<Slot[]>();
        _slotCount = 0;
        _count = 0;
        _freeHead = -1;
    }

    private ref Slot GetAllocationSlot(out int index)
    {
        if (_freeHead >= 0)
        {
            index = _freeHead;
            ref Slot reused = ref GetSlot(index);
            _freeHead = reused.NextFree;
            return ref reused;
        }

        if (_slotCount == int.MaxValue)
        {
            throw new InvalidOperationException(
                "The retained composition state store exhausted its handle space.");
        }

        index = _slotCount++;
        int pageIndex = index >> PageShift;
        EnsurePage(pageIndex);
        return ref _pages[pageIndex][index & PageMask];
    }

    private ref Slot GetSlot(int index) =>
        ref _pages[index >> PageShift][index & PageMask];

    private void EnsurePage(int pageIndex)
    {
        if (pageIndex >= _pages.Length)
        {
            int nextLength = _pages.Length == 0 ? 4 : _pages.Length;
            while (nextLength <= pageIndex)
                nextLength = checked(nextLength * 2);
            Array.Resize(ref _pages, nextLength);
        }

        _pages[pageIndex] ??= new Slot[PageSize];
    }

    private static ulong Encode(int index, uint generation) =>
        ((ulong)generation << 32) | ((uint)index + 1u);

    private static bool TryDecode(
        ulong handle,
        out int index,
        out uint generation)
    {
        uint encodedIndex = (uint)handle;
        generation = (uint)(handle >> 32);
        if (encodedIndex == 0 ||
            generation == 0 ||
            encodedIndex > int.MaxValue)
        {
            index = -1;
            return false;
        }

        index = (int)(encodedIndex - 1u);
        return true;
    }

    private static uint NextGeneration(uint generation)
    {
        unchecked
        {
            generation++;
        }

        return generation == 0 ? 1u : generation;
    }

    private struct Slot
    {
        internal T? Value;
        internal long RetainedId;
        internal uint Generation;
        internal int NextFree;
    }
}
