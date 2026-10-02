using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed unsafe class NativeHintedSourceResourceImportTests
{
    private static NativeMethods.HintedSourceGlyphResourceView SourceView() => new()
    {
        AbiVersion = NativeMethods.AbiVersion,
        StructSize = (uint)sizeof(NativeMethods.HintedSourceGlyphResourceView), Version = 2,
        Source = new()
        {
            Options = NativeHintedSourceOptions.Create(Math.BitIncrement(13.0), 1.5, 100.0 / 1.5, 0, 0,
                NativeSourceEmPolicy.FloatCaptureNearestHalfUp, NativeSourceAdvancePolicy.SourceIdealUnits, true,
                offsetPolicy: NativeSourceOffsetPolicy.SourceIdealUnits)
        }
    };

    private static NativeHintedGlyphResource Owner(nint identity, Action<nint> destroy, bool source)
    {
        NativeMethods.HintedGlyphResourceView raster = default;
        return new(identity, in raster, destroy, sourceView: source ? SourceView() : null);
    }

    [Fact]
    public void GeneratedSourceImportRecordsPreserveVersionedLayout()
    {
        Assert.Equal(16, sizeof(NativeHintedSourcePositioningRun));
        Assert.Equal(24, sizeof(NativeHintedSourceFittingSlice));
        Assert.Equal(24, sizeof(NativeHintedSourceFittedLine));
        Assert.Equal(16 + 2 * IntPtr.Size, sizeof(NativeMethods.HintedGlyphResourceInput));
        Assert.Equal(48 + sizeof(NativeMethods.HintedSourceParagraphView) + 7 * IntPtr.Size,
            sizeof(NativeMethods.HintedSourceGlyphResourceView));
        Assert.Equal(16, Marshal.OffsetOf<NativeMethods.HintedGlyphResourceInput>(nameof(NativeMethods.HintedGlyphResourceInput.Raster)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<NativeMethods.HintedSourceGlyphResourceView>(nameof(NativeMethods.HintedSourceGlyphResourceView.Source)).ToInt32());
    }

    [Fact]
    public void MixedResourcesPreserveIndicesAndBothOwnersThroughOneCrossing()
    {
        int destroyed = 0, calls = 0;
        using var raw = Owner(11, _ => destroyed++, false);
        using var source = Owner(22, _ => destroyed++, true);
        NativeMilHintedGlyphBinding[] bindings = [new() { ResourceIndex = 2, PositionedIndexStart = 1 }, new() { ResourceIndex = 1 }];
        var status = NativeHintedSourceResourceImport.Apply(77, [9, 8, 7], [raw, source, raw], bindings, [6U, 5U],
            (channel, batch, size, input, count, selected, selectedCount, indices, indexCount) =>
            {
                calls++;
                Assert.Equal((nint)77, channel); Assert.Equal((nuint)3, size); Assert.Equal((byte)8, batch[1]);
                Assert.Equal(3U, count); Assert.Equal(2U, selectedCount); Assert.Equal(2U, indexCount);
                Assert.Equal(2U, selected[0].ResourceIndex); Assert.Equal(1U, selected[1].ResourceIndex); Assert.Equal(5U, indices[1]);
                for (int i = 0; i < count; i++)
                {
                    Assert.Equal(NativeMethods.AbiVersion, input[i].AbiVersion); Assert.Equal(2U, input[i].Version);
                    Assert.Equal((uint)sizeof(NativeMethods.HintedGlyphResourceInput), input[i].StructSize);
                    Assert.Equal(0U, input[i].Reserved); Assert.NotEqual((nuint)0, input[i].Raster);
                }
                Assert.Equal((nuint)0, input[0].Source); Assert.Equal((nuint)0, input[2].Source);
                var original = (NativeMethods.HintedSourceGlyphResourceView*)input[1].Source;
                Assert.Equal(2U, original->Version); Assert.Equal(Math.BitIncrement(13.0), original->Source.Options.EmSize);
                Assert.Equal(1.5, original->Source.Options.PixelsPerDip);
                raw.Dispose(); source.Dispose(); Assert.Equal(0, destroyed);
                Assert.True(source.SourceViewWhileRetained().HasValue);
                return NativeMilStatus.Success;
            });
        Assert.Equal(NativeMilStatus.Success, status); Assert.Equal(1, calls); Assert.Equal(2, destroyed);
    }

    [Fact]
    public void LaterAcquisitionFailureNeverDispatchesAndReleasesEarlierLease()
    {
        int destroyed = 0, calls = 0;
        using var first = Owner(1, _ => destroyed++, true);
        using var later = Owner(2, _ => destroyed++, false); later.Dispose();
        Assert.Throws<ObjectDisposedException>(() => NativeHintedSourceResourceImport.Apply(1, [], [first, later], [], [],
            (_, _, _, _, _, _, _, _, _) => { calls++; return NativeMilStatus.Success; }));
        Assert.Equal(0, calls); Assert.Equal(1, destroyed);
        first.Dispose(); Assert.Equal(2, destroyed);
    }

    [Fact]
    public void MissingSourceEntrypointDoesNotRetryAnOrdinaryTransaction()
    {
        int calls = 0, destroyed = 0;
        using var source = Owner(1, _ => destroyed++, true);
        var missing = new EntryPointNotFoundException("source apply");
        Assert.Same(missing, Assert.Throws<EntryPointNotFoundException>(() =>
            NativeHintedSourceResourceImport.Apply(1, [], [source], [], [], (_, _, _, _, _, _, _, _, _) =>
            { calls++; throw missing; })));
        Assert.Equal(1, calls); source.Dispose(); Assert.Equal(1, destroyed);
    }

    [Fact]
    public void CallbackFailureDrainsEveryOwnerAndRetainsRetirementFault()
    {
        int destroyed = 0, attempts = 0;
        using var raw = Owner(1, _ => destroyed++, false);
        using var source = Owner(2, _ => { if (++attempts == 1) throw new InvalidOperationException("retire"); destroyed++; }, true);
        var primary = new EntryPointNotFoundException("missing source capability");
        Assert.Same(primary, Assert.Throws<EntryPointNotFoundException>(() =>
            NativeHintedSourceResourceImport.Apply(1, [], [raw, source], [], [], (_, _, _, _, _, _, _, _, _) =>
            { raw.Dispose(); source.Dispose(); throw primary; })));
        Assert.Equal(1, destroyed); Assert.IsType<InvalidOperationException>(primary.Data["HintedSourceResourceCleanupFailure"]);
        source.Dispose(); Assert.Equal(2, destroyed); Assert.Equal(2, attempts);
    }

    [Fact]
    public void NativeRejectionRemainsPrimaryWhenRetirementAlsoFails()
    {
        int attempts = 0;
        using var source = Owner(1, _ => { if (++attempts == 1) throw new InvalidOperationException("retire"); }, true);
        var error = Assert.Throws<NativeMilException>(() => NativeHintedSourceResourceImport.Apply(1, [], [source], [], [],
            (_, _, _, _, _, _, _, _, _) => { source.Dispose(); return NativeMilStatus.InvalidArgument; }));
        Assert.Equal(NativeMilStatus.InvalidArgument, error.Status);
        Assert.IsType<InvalidOperationException>(error.Data["HintedSourceResourceCleanupFailure"]);
        source.Dispose(); Assert.Equal(2, attempts);
    }

    [Fact]
    public void InvalidSourceVersionAndMismatchedCountsRejectBeforeOwnershipTransfer()
    {
        NativeMethods.HintedGlyphResourceView raster = default;
        var source = SourceView(); source.Version = 1;
        Assert.Throws<InvalidOperationException>(() => NativeHintedGlyphResource.ValidateSourceView(in source, in raster));
        source = SourceView(); source.Source.GlyphCount = 1;
        Assert.Throws<InvalidOperationException>(() => NativeHintedGlyphResource.ValidateSourceView(in source, in raster));
        source = SourceView(); source.Flags = 1;
        Assert.Throws<InvalidOperationException>(() => NativeHintedGlyphResource.ValidateSourceView(in source, in raster));
        source = SourceView(); raster.Counts.CaretStopCount = 1;
        Assert.Throws<InvalidOperationException>(() => NativeHintedGlyphResource.ValidateSourceView(in source, in raster));
    }
}
