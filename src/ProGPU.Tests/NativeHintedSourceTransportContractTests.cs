using System.Reflection;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeHintedSourceTransportContractTests
{
    [Fact]
    public void SourceRecordsRetainNativeDoubleLayout()
    {
        Assert.Equal(72, Marshal.SizeOf<NativeHintedSourceOptions>());
        Assert.Equal(24, Marshal.SizeOf<NativeHintedSourceStyle>());
        Assert.Equal(32, Marshal.SizeOf<NativeHintedSourceLogicalMetrics>());
        Assert.Equal(40, Marshal.SizeOf<NativeHintedSourceGlyphMetrics>());
        Assert.Equal(56, Marshal.SizeOf<NativeHintedSourceLineMetrics>());
        Assert.Equal(48, Marshal.SizeOf<NativeHintedSourceClusterBox>());
        Assert.Equal(40, Marshal.SizeOf<NativeHintedSourceCaretStop>());
        Assert.Equal(32, Marshal.SizeOf<NativeHintedSourceRectangle>());
        Assert.Equal(48, Marshal.SizeOf<NativeHintedSourceHit>());
        Assert.Equal(64, Marshal.SizeOf<NativeHintedSourceRunFrame>());
        Assert.Equal(16, Marshal.OffsetOf<NativeHintedSourceOptions>(nameof(NativeHintedSourceOptions.EmSize)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<NativeHintedSourceRunFrame>(nameof(NativeHintedSourceRunFrame.ParagraphBaselineY)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<NativeHintedSourceRunFrame>(nameof(NativeHintedSourceRunFrame.RasterParagraphOrigin)).ToInt32());
    }

    [Fact]
    public void ExplicitSourceOptionsNeverReplaceOriginalDoublesWithCapturePixels()
    {
        double em = Math.BitIncrement(13.0), dpi = 1.5, width = 100.0 / 1.5;
        var options = NativeHintedSourceOptions.Create(em, dpi, width, 12.0, 7.0,
            NativeSourceEmPolicy.NearestTiesToEven, NativeSourceAdvancePolicy.PhysicalTiesToEven, false);
        Assert.Equal(1U, options.Version);
        Assert.Equal(72U, options.StructSize);
        Assert.Equal(em, options.EmSize); Assert.Equal(dpi, options.PixelsPerDip);
        Assert.Equal(width, options.MaximumWidth); Assert.Equal(7.0, options.TabOrigin);
        Assert.Equal(2U, options.EmPolicy); Assert.Equal(1U, options.AdvancePolicy);
        Assert.Equal(0U, options.OffsetPolicy); Assert.Equal(0U, options.AllowEmergencyBreak);
        Assert.Equal(0U, options.Flags);
    }

    [Fact]
    public void RawSourceQueriesAndContinuationKeepDoubleInputs()
    {
        Assert.NotNull(typeof(NativeHintedSourceParagraph).GetMethod(nameof(NativeHintedSourceParagraph.HitTest), [typeof(double), typeof(double)]));
        Assert.NotNull(typeof(NativeHintedSourceParagraph).GetMethod(nameof(NativeHintedSourceParagraph.Reflow), [typeof(int), typeof(double)]));
        Assert.NotNull(typeof(NativeHintedSourceParagraph).GetMethod(nameof(NativeHintedSourceParagraph.HitTestLine), [typeof(int), typeof(double)]));
        Assert.NotNull(typeof(NativeHintedSourceParagraph).GetMethod(nameof(NativeHintedSourceParagraph.GetLineCaret), [typeof(int), typeof(int), typeof(bool)]));
        Assert.Null(typeof(NativeHintedSourceParagraph).GetProperty("IntrinsicWidths", BindingFlags.Instance | BindingFlags.Public));
        Assert.Equal(typeof(NativeHintedSourceRunFrame), typeof(NativeHintedGlyphResourceReadLease)
            .GetMethod(nameof(NativeHintedGlyphResourceReadLease.ValidateSourceRun))!.ReturnType);
    }

    [Fact]
    public void OrdinaryResourceLeaseRejectsDoubleMetricsWithoutCallingNative()
    {
        NativeMethods.HintedGlyphResourceView view = default;
        int destroyed = 0;
        using var resource = new NativeHintedGlyphResource(17, in view, _ => destroyed++);
        using var lease = resource.AcquireReadLease();
        Assert.False(lease.HasSourceMetrics);
        Assert.Throws<NotSupportedException>(() => lease.CopySourceMetrics([0U], 13.0, 1.5, new double[1], new NativeHintedSourceGlyphOffset[1]));
        Assert.Throws<NotSupportedException>(() => lease.ValidateSourceRun([0U], 13.0, 1.5, default, [1.0], [default]));
        resource.Dispose(); Assert.Equal(0, destroyed);
        lease.Dispose(); Assert.Equal(1, destroyed);
    }
}
