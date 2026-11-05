using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using ProGPU.Scene.Native;
using Xunit;
using Snapshot = ProGPU.Scene.Native.NativeHintedGlyphGeometryFactory.FontScanSnapshot;

namespace ProGPU.Tests;

public sealed class UnhintedFontScanMetadataTests
{
    private static Snapshot Capture(bool witness)
    {
        uint flags = NativeFontScanMetadata.SegmentMarker | (witness
            ? NativeFontScanMetadata.ContourOverride | 5u | (0x38u << 16)
            : 1u << 16);
        Vector2[] p = [new(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 0),
            new(1, 0), new(1, 1), new(0, 1)];
        if (witness) for (int i = 0; i < p.Length; i++) p[i] += new Vector2(100);
        var segments = new NativePathSegment[8];
        for (int i = 0; i < segments.Length; i++)
            segments[i] = new(NativePathSegmentKind.Line, p[i % 4], p[(i + 1) % 4], default, default, flags, 0, 4);
        return new([
            new() { GlyphIndex = 7, PointCount = 4, ContourCount = 1 },
            new() { GlyphIndex = 8, PointOffset = 4, ContourOffset = 1 },
            new() { GlyphIndex = 7, PointOffset = 4, PointCount = 4, ContourOffset = 1, ContourCount = 1 }],
            Enumerable.Repeat(witness ? (byte)0x21 : (byte)1, 8).ToArray(), [3, 3],
            [new(0, 4, p[0], p[2], 1, 0), new(4, 4, p[0], p[2], 1, 0)],
            [new() { GlyphIndex = 7, OutlineIndex = 0 },
             new() { GlyphIndex = 8, OutlineIndex = uint.MaxValue },
             new() { GlyphIndex = 7, OutlineIndex = 1 }], segments);
    }

    [Fact]
    public void TransfersOnlyScanBitsPreservingSignedZeroDuplicatesAndNoInkSlots()
    {
        Snapshot raw = Capture(false), witness = Capture(true);
        byte[] before = MemoryMarshal.AsBytes(raw.Segments.AsSpan()).ToArray();
        byte[] witnessBefore = MemoryMarshal.AsBytes(witness.Segments.AsSpan()).ToArray();
        var result = NativeHintedGlyphGeometryFactory.ApplyUnhintedScanMetadata(raw, witness);
        Assert.NotSame(raw.Segments, result);
        for (int i = 0; i < result.Length; i++)
        {
            var a = raw.Segments[i];
            Assert.Equal(a.Pad0 & ~NativeHintedGlyphGeometryFactory.UnhintedScanMask,
                result[i].Pad0 & ~NativeHintedGlyphGeometryFactory.UnhintedScanMask);
            Assert.Equal(witness.Segments[i].Pad0 & NativeHintedGlyphGeometryFactory.UnhintedScanMask,
                result[i].Pad0 & NativeHintedGlyphGeometryFactory.UnhintedScanMask);
            result[i] = new(result[i].Kind, result[i].P0, result[i].P1, result[i].P2, result[i].P3,
                a.Pad0, result[i].Pad1, result[i].Pad2);
        }
        Assert.Equal(before, MemoryMarshal.AsBytes(result.AsSpan()).ToArray());
        Assert.Equal(before, MemoryMarshal.AsBytes(raw.Segments.AsSpan()).ToArray());
        Assert.Equal(witnessBefore, MemoryMarshal.AsBytes(witness.Segments.AsSpan()).ToArray());
        Assert.Equal(uint.MaxValue, raw.Descriptors[1].OutlineIndex);
        Assert.Equal(raw.Glyphs[0].GlyphIndex, raw.Glyphs[2].GlyphIndex);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)]
    public void RejectsLateTopologyCorruptionWithoutChangingEitherInput(int corruption)
    {
        Snapshot raw = Capture(false), witness = Capture(true);
        switch (corruption)
        {
            case 0: witness.Glyphs[2].GlyphIndex++; break;
            case 1: witness.Glyphs[2].PointOffset--; break;
            case 2: witness.Glyphs[2].PointCount--; break;
            case 3: witness.Tags[7] = 0; break;
            case 4: witness.Ends[1] = 2; break;
            case 5: witness.Descriptors[2].OutlineIndex = 0; break;
            case 6: witness.Outlines[1] = new(3, 4, Vector2.Zero, Vector2.One, 1, 0); break;
            case 7: witness.Segments[7] = new(NativePathSegmentKind.Quadratic,
                default, default, default, default, NativeFontScanMetadata.SegmentMarker, 0, 4); break;
            case 8: witness.Segments[7] = new(NativePathSegmentKind.Line,
                default, default, default, default, NativeFontScanMetadata.SegmentMarker, 1, 4); break;
            case 9: witness = witness with { Tags = [.. witness.Tags, 1] }; break;
        }
        byte[] rawBytes = MemoryMarshal.AsBytes(raw.Segments.AsSpan()).ToArray();
        byte[] witnessBytes = MemoryMarshal.AsBytes(witness.Segments.AsSpan()).ToArray();
        Assert.Throws<ArgumentException>(() => NativeHintedGlyphGeometryFactory.ApplyUnhintedScanMetadata(raw, witness));
        Assert.Equal(rawBytes, MemoryMarshal.AsBytes(raw.Segments.AsSpan()).ToArray());
        Assert.Equal(witnessBytes, MemoryMarshal.AsBytes(witness.Segments.AsSpan()).ToArray());
    }

    [Fact]
    public void EmptyCapturedInventoriesStayEmpty()
    {
        var empty = new Snapshot([], [], [], [], [], []);
        Assert.Empty(NativeHintedGlyphGeometryFactory.ApplyUnhintedScanMetadata(empty, empty));
    }
}
