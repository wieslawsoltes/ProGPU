using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

// Generated wire/lease controls only: no native module, font parser or GPU.
public sealed unsafe class NativeHintedGlyphNominalMetricsTests
{
    [Fact]
    public void OrdinaryPreparationDoesNotInventNominalMetrics()
    {
        NativeMethods.HintedGlyphResourceView view = default;
        using var resource = new NativeHintedGlyphResource(1, in view, _ => { });
        using var lease = resource.AcquireReadLease();
        Assert.False(lease.HasNominalMetrics);
        Assert.Throws<NotSupportedException>(() => { _ = lease.NominalMetrics.Length; });
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = lease.HasNominalMetrics; });
    }

    [Fact]
    public void ExplicitEmptyMetricsRemainDifferentFromUnavailable()
    {
        NativeMethods.HintedGlyphResourceView view = default;
        var nominal = Nominal(0, 0);
        using var resource = new NativeHintedGlyphResource(2, in view, _ => { }, nominal);
        using var lease = resource.AcquireReadLease();
        Assert.True(lease.HasNominalMetrics);
        Assert.Empty(lease.NominalMetrics.ToArray());
    }

    [Fact]
    public void OriginalNominalSpanSharesOwnerWithoutCopyOrPerReadCrossing()
    {
        NativePositionedTextGlyph* glyphs = stackalloc NativePositionedTextGlyph[2];
        NativeHintedParagraphGlyphOwner* owners = stackalloc NativeHintedParagraphGlyphOwner[2];
        uint* outlines = stackalloc uint[] { uint.MaxValue, uint.MaxValue };
        int* ends = stackalloc int[2]; sbyte* levels = stackalloc sbyte[2];
        NativeHintedGlyphNominalMetrics* values = stackalloc NativeHintedGlyphNominalMetrics[2];
        values[0] = new() { PositionedIndex = 0, FontIndex = 3, GlyphId = 27, AdvanceWidthDesignUnits = 613 };
        values[1] = new() { PositionedIndex = 1, FontIndex = 3, GlyphId = 27, AdvanceWidthDesignUnits = 613 };
        var view = new NativeMethods.HintedGlyphResourceView
        {
            Counts = new() { PositionedGlyphCount = 2 }, PositionedGlyphs = (nuint)glyphs,
            PositionedOwners = (nuint)owners, PositionedOutlineIndices = (nuint)outlines,
            PositionedClusterEnds = (nuint)ends, PositionedBidiLevels = (nuint)levels,
        };
        int releases = 0;
        using var resource = new NativeHintedGlyphResource(3, in view, _ => releases++, Nominal((nuint)values, 2));
        using var first = resource.AcquireReadLease();
        using var second = resource.AcquireReadLease();
        resource.Dispose();
        Assert.Equal(0, releases);
        Assert.Equal((nuint)values, (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(first.NominalMetrics)));
        Assert.Equal(613U, first.NominalMetrics[0].AdvanceWidthDesignUnits);
        Assert.Equal(1U, second.NominalMetrics[1].PositionedIndex);
        Assert.Equal(27U, second.NominalMetrics[1].GlyphId);
        Assert.Equal(uint.MaxValue, second.PositionedOutlineIndices[1]);
        first.Dispose(); Assert.Equal(0, releases);
        second.Dispose(); Assert.Equal(1, releases);
        Assert.Throws<ObjectDisposedException>(() => { _ = second.NominalMetrics.Length; });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidNominalHeaderPublishesNoLeaseAndReleasesFailedUse(int kind)
    {
        NativeMethods.HintedGlyphResourceView view = default;
        var nominal = Nominal(0, 0);
        switch (kind)
        {
            case 0: nominal.AbiVersion++; break;
            case 1: nominal.StructSize--; break;
            case 2: nominal.Reserved = 1; break;
            case 3: nominal.MetricCount = 1; break;
        }
        int releases = 0;
        using var resource = new NativeHintedGlyphResource(4, in view, _ => releases++, nominal);
        Assert.Throws<InvalidOperationException>(() => resource.AcquireReadLease());
        resource.Dispose(); Assert.Equal(1, releases);
    }

    [Fact]
    public void NominalRangeValidationRejectsNullAndAddressOverflowWithoutReadingMemory()
    {
        var missing = Nominal(0, 1);
        Assert.Throws<InvalidOperationException>(() => NativeHintedGlyphResourceReadLease.ValidateNominalMetrics(in missing, 1));
        var overflow = Nominal(nuint.MaxValue, 1);
        Assert.Throws<OverflowException>(() => NativeHintedGlyphResourceReadLease.ValidateNominalMetrics(in overflow, 1));
        var excessive = Nominal(1, uint.MaxValue);
        Assert.Throws<OverflowException>(() => NativeHintedGlyphResourceReadLease.ValidateNominalMetrics(in excessive, uint.MaxValue));
    }

    private static NativeMethods.HintedGlyphNominalMetricsView Nominal(nuint values, uint count) => new()
    {
        AbiVersion = NativeMethods.AbiVersion, StructSize = (uint)sizeof(NativeMethods.HintedGlyphNominalMetricsView),
        Metrics = values, MetricCount = count,
    };
}
