using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeMilVisualVisibilityTests
{
    [Fact]
    public void SnapshotEntryPreservesTheEightByteUnsignedNativeLayout()
    {
        Assert.Equal(8, Unsafe.SizeOf<NativeMilVisualVisibility>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<NativeMilVisualVisibility>());
        Assert.Equal(0, Marshal.OffsetOf<NativeMilVisualVisibility>(nameof(NativeMilVisualVisibility.Handle)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<NativeMilVisualVisibility>(nameof(NativeMilVisualVisibility.Visibility)).ToInt32());
        NativeMilVisualVisibility entry = new() { Handle = uint.MaxValue, Visibility = 2 };
        ReadOnlySpan<uint> fields = MemoryMarshal.Cast<NativeMilVisualVisibility, uint>(
            MemoryMarshal.CreateReadOnlySpan(ref entry, 1));
        Assert.Equal(uint.MaxValue, fields[0]);
        Assert.Equal(2U, fields[1]);
    }
}
