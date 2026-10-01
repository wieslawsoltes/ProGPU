using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace Avalonia.ProGpu.UnitTests;

public sealed class NativeTextHintingContractTests
{
    [Fact]
    public void GeneratedRecordsMatchIndependentFixedWidthLayout()
    {
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.HintedFontRequest>());
        Assert.Equal(16, Marshal.SizeOf<NativeHintedPoint>());
        Assert.Equal(136, Marshal.SizeOf<NativeHintedGlyph>());
        Assert.Equal(12, Marshal.SizeOf<NativeHintedBatchCounts>());
        string[] prefix = ["GlyphIndex", "PointOffset", "PointCount", "ContourOffset", "ContourCount", "OutlineFlags"];
        string[] metrics = ["AdvanceX266", "AdvanceY266", "HorizontalBearingX266", "HorizontalBearingY266",
            "Width266", "Height266", "HorizontalAdvance266", "VerticalBearingX266", "VerticalBearingY266",
            "VerticalAdvance266", "LinearHorizontalAdvance1616", "LinearVerticalAdvance1616",
            "LeftSideBearingDelta266", "RightSideBearingDelta266"];
        for (int index = 0; index < prefix.Length; index++)
        {
            Assert.Equal(typeof(uint), typeof(NativeHintedGlyph).GetField(prefix[index])!.FieldType);
            Assert.Equal(index * 4, (int)Marshal.OffsetOf<NativeHintedGlyph>(prefix[index]));
        }
        for (int index = 0; index < metrics.Length; index++)
        {
            Assert.Equal(typeof(long), typeof(NativeHintedGlyph).GetField(metrics[index])!.FieldType);
            Assert.Equal(24 + index * 8, (int)Marshal.OffsetOf<NativeHintedGlyph>(metrics[index]));
        }
    }

    [Fact]
    public void SignedFixedPointRecordsDoNotNarrowThroughFloatOrNativeLong()
    {
        NativeHintedPoint[] points = [new() { X266 = long.MinValue, Y266 = long.MaxValue }];
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(points.AsSpan());
        Assert.Equal(long.MinValue, BinaryPrimitives.ReadInt64LittleEndian(bytes));
        Assert.Equal(long.MaxValue, BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]));
        NativeHintedGlyph[] glyphs = [new()
        {
            GlyphIndex = uint.MaxValue, OutlineFlags = uint.MaxValue,
            AdvanceX266 = -9_007_199_254_740_993, RightSideBearingDelta266 = 9_007_199_254_740_993,
        }];
        bytes = MemoryMarshal.AsBytes(glyphs.AsSpan());
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]));
        Assert.Equal(-9_007_199_254_740_993, BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]));
        Assert.Equal(9_007_199_254_740_993, BinaryPrimitives.ReadInt64LittleEndian(bytes[128..]));
    }

    [Fact]
    public void RetainedParagraphRecordsMatchIndependentWireOffsets()
    {
        Assert.Equal(44, Marshal.SizeOf<NativeHintedParagraphDeviceStyle>());
        Assert.Equal(36, Marshal.SizeOf<NativeHintedParagraphCounts>());
        Assert.Equal(40, Marshal.SizeOf<NativeHintedParagraphRun>());
        Assert.Equal(12, Marshal.SizeOf<NativeHintedParagraphGlyphOwner>());
        string[] device = ["FontIndex", "SourceScale", "LogicalUnitsPerPhysicalPixel", "XPixelsPerEm266",
            "YPixelsPerEm266", "Interpreter", "XPhase266", "YPhase266", "VariationStart", "VariationCount", "Reserved"];
        string[] counts = ["SourceScalarCount", "AdmittedScalarCount", "StyleCount", "RunCount", "LogicalGlyphCount",
            "PositionedGlyphCount", "LineCount", "ClusterBoxCount", "CaretStopCount"];
        string[] runs = ["ScalarStart", "ScalarCount", "LogicalStart", "LogicalCount", "FontIndex", "StyleIndex",
            "BidiLevel", "SourceScale", "LogicalUnitsPerPhysicalPixel", "SourceDescriptorCount"];
        for (int index = 0; index < device.Length; index++)
            Assert.Equal(index * 4, (int)Marshal.OffsetOf<NativeHintedParagraphDeviceStyle>(device[index]));
        for (int index = 0; index < counts.Length; index++)
            Assert.Equal(index * 4, (int)Marshal.OffsetOf<NativeHintedParagraphCounts>(counts[index]));
        for (int index = 0; index < runs.Length; index++)
            Assert.Equal(index * 4, (int)Marshal.OffsetOf<NativeHintedParagraphRun>(runs[index]));
        Assert.Equal(typeof(int), typeof(NativeHintedParagraphRun).GetField("BidiLevel")!.FieldType);

        string[] buffers = ["SourceScalars", "AdmittedScalars", "ScalarLevels", "Styles", "SourceMetrics", "Runs",
            "LogicalGlyphs", "LogicalOwners", "LogicalClusterEnds", "LogicalBidiLevels", "GlyphScales",
            "PositionedGlyphs", "PositionedOwners", "PositionedClusterEnds", "PositionedBidiLevels", "Lines", "LineOrigins"];
        string[] capacities = ["SourceScalarCapacity", "AdmittedScalarCapacity", "ScalarLevelCapacity", "StyleCapacity",
            "SourceMetricCapacity", "RunCapacity", "LogicalGlyphCapacity", "LogicalOwnerCapacity", "LogicalClusterEndCapacity",
            "LogicalBidiLevelCapacity", "GlyphScaleCapacity", "PositionedGlyphCapacity", "PositionedOwnerCapacity",
            "PositionedClusterEndCapacity", "PositionedBidiLevelCapacity", "LineCapacity", "LineOriginCapacity"];
        int pairSize = IntPtr.Size == 8 ? 16 : 8;
        Assert.Equal(8 + buffers.Length * pairSize, Marshal.SizeOf<NativeMethods.HintedParagraphFormatBuffers>());
        for (int index = 0; index < buffers.Length; index++)
        {
            Assert.Equal(8 + index * pairSize, (int)Marshal.OffsetOf<NativeMethods.HintedParagraphFormatBuffers>(buffers[index]));
            Assert.Equal(8 + index * pairSize + IntPtr.Size,
                (int)Marshal.OffsetOf<NativeMethods.HintedParagraphFormatBuffers>(capacities[index]));
        }
        int targetOffset = (20 + IntPtr.Size - 1) & -IntPtr.Size;
        Assert.Equal(targetOffset, (int)Marshal.OffsetOf<NativeMethods.HintedParagraphFrameRequest>("TargetView"));
        Assert.Equal(targetOffset + IntPtr.Size,
            (int)Marshal.OffsetOf<NativeMethods.HintedParagraphFrameRequest>("LogicalOrigin"));
        int frameSize = (targetOffset + IntPtr.Size + 36 + IntPtr.Size - 1) & -IntPtr.Size;
        Assert.Equal(frameSize, Marshal.SizeOf<NativeMethods.HintedParagraphFrameRequest>());
    }

    [Fact]
    public void BatchLeasesHaveTheirOwnDisposedIdentityAndReleaseOnce()
    {
        int releases = 0;
        var owner = new NativeTextContextOwner(42, _ => releases++, nameof(NativeHintedFontBatch));
        using (var lease = owner.Acquire())
        {
            owner.Dispose();
            Assert.Equal(0, releases);
            Assert.Equal(nameof(NativeHintedFontBatch), Assert.Throws<ObjectDisposedException>(() =>
            {
                using var rejected = owner.Acquire();
            }).ObjectName);
        }
        Assert.Equal(1, releases);
        owner.Dispose();
        Assert.Equal(1, releases);
    }

    [Fact]
    public void ShapedRunLeasesRetainTheirOwnDisposedIdentity()
    {
        int releases = 0;
        var owner = new NativeTextContextOwner(43, _ => releases++, nameof(NativeHintedTextRun));
        using (var lease = owner.Acquire())
        {
            owner.Dispose();
            Assert.Equal(0, releases);
            Assert.Equal(nameof(NativeHintedTextRun), Assert.Throws<ObjectDisposedException>(() =>
            {
                using var rejected = owner.Acquire();
            }).ObjectName);
        }
        Assert.Equal(1, releases);
        owner.Dispose();
        Assert.Equal(1, releases);
    }

    [Fact]
    public void HintedContextShapingRejectsIgnoredBorrowedResources()
    {
        var valid = new NativeTextShapeInput([], []);
        NativeTextShapingContext.ValidateHintedShapeResources(in valid);
        foreach (int resource in new[] { 0, 1, 2 })
        {
            bool rejected = false;
            var invalid = new NativeTextShapeInput(resource == 0 ? new byte[] { 1 } : [], [],
                faceIndex: resource == 1 ? 1U : 0U,
                normalizationData: resource == 2 ? new byte[] { 1 } : []);
            try { NativeTextShapingContext.ValidateHintedShapeResources(in invalid); }
            catch (ArgumentException) { rejected = true; }
            Assert.True(rejected);
        }
    }
}
