using System.Numerics;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

// Managed marshaling/lease controls only; native arithmetic controls are the
// separately authored C++ fixture, not replaced by this injected callback.
public sealed unsafe class NativeHintedSourceGlyphFrameTests
{
    [Fact]
    public void ValidationUsesSameRetainedOwnerAndBorrowsExactInputsOnce()
    {
        int calls = 0, releases = 0;
        NativeMethods.HintedGlyphResourceView view = default;
        var nominal = Nominal();
        using var resource = new NativeHintedGlyphResource(73, in view, _ => releases++, nominal,
            (handle, indices, count, em, baseline, advances, offsets, frame) =>
            {
                Assert.Equal(73, handle); Assert.Equal(3U, count); Assert.Equal(10, em); Assert.Equal(new Vector2(5, 7), baseline);
                Assert.Equal(2U, indices[0]); Assert.Equal(1U, indices[1]); Assert.Equal(2U, indices[2]);
                Assert.Equal(7, advances[2]); Assert.Equal(-16, offsets[2].X); Assert.Equal(0, releases);
                *frame = new() { LineIndex = 4, ParagraphBaselineY = 20, SourceBaselineOrigin = baseline,
                    ParagraphOrigin = new(5, -13), BaselineRelativeOrigin = new(0, -20) };
                calls++;
                return NativeRendererStatus.Success;
            });
        using var lease = resource.AcquireReadLease();
        resource.Dispose(); // A live reader still owns native validation.
        uint[] indices = [2, 1, 2, 99]; double[] advances = [7, 0, 7, 123];
        NativeHintedSourceGlyphOffset[] offsets = [new() { X = -9 }, new() { X = -11, Y = -1.5 }, new() { X = -16 }, new() { X = 456 }];
        var result = lease.ValidateSourceFrame(indices.AsSpan(0, 3), 10, new(5, 7), advances.AsSpan(0, 3), offsets.AsSpan(0, 3));
        Assert.Equal(new Vector2(5, -13), result.ParagraphOrigin);
        Assert.Equal(new Vector2(5, 7), result.SourceBaselineOrigin); Assert.Equal(4U, result.LineIndex);
        Assert.Equal(1, calls); Assert.Equal(99U, indices[3]); Assert.Equal(123, advances[3]); Assert.Equal(456, offsets[3].X);
        lease.Dispose(); Assert.Equal(1, releases);
        Assert.Throws<ObjectDisposedException>(() => lease.ValidateSourceFrame([0], 10, default, [7], [default]));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ShapeMismatchRejectsBeforeCrossing(int kind)
    {
        int calls = 0;
        NativeMethods.HintedGlyphResourceView view = default;
        using var resource = new NativeHintedGlyphResource(1, in view, _ => { }, Nominal(),
            (_, _, _, _, _, _, _, _) => { calls++; return NativeRendererStatus.Success; });
        using var lease = resource.AcquireReadLease();
        Assert.Throws<ArgumentException>(() => lease.ValidateSourceFrame(kind == 0 ? [] : [0], 10, default,
            kind == 1 ? [] : [7], kind == 2 ? [] : [default]));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void LegacyResourceCannotEnterSourceFrameValidation()
    {
        NativeMethods.HintedGlyphResourceView view = default;
        using var resource = new NativeHintedGlyphResource(1, in view, _ => { });
        using var lease = resource.AcquireReadLease();
        Assert.Throws<NotSupportedException>(() => lease.ValidateSourceFrame([0], 10, default, [7], [default]));
    }

    [Fact]
    public void NativeRejectionRetainsReaderAndAllowsRetry()
    {
        int calls = 0, releases = 0;
        NativeMethods.HintedGlyphResourceView view = default;
        using var resource = new NativeHintedGlyphResource(1, in view, _ => releases++, Nominal(),
            (_, _, _, _, _, _, _, _) => ++calls == 1 ? NativeRendererStatus.Unsupported : NativeRendererStatus.Success);
        using var lease = resource.AcquireReadLease(); resource.Dispose();
        Assert.Throws<NotSupportedException>(() => lease.ValidateSourceFrame([0], 10, default, [7], [default]));
        Assert.False(lease.IsDisposed); Assert.Equal(0, releases);
        _ = lease.ValidateSourceFrame([0], 10, default, [7], [default]);
        Assert.Equal(2, calls); lease.Dispose(); Assert.Equal(1, releases);
    }

    private static NativeMethods.HintedGlyphNominalMetricsView Nominal() => new()
    { AbiVersion = NativeMethods.AbiVersion, StructSize = (uint)sizeof(NativeMethods.HintedGlyphNominalMetricsView) };
}
