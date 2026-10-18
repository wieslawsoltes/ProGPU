using System.Runtime.InteropServices;
using ProGPU.Backend.Native;

internal static class TextFontOutlineValidation
{
    internal static void Run(string fontPath)
    {
        uint[] ids = [0, 3, 4, 3, 1, 2, 4];
        foreach (NativeFontHintInterpreter interpreter in new[] { NativeFontHintInterpreter.TrueType35, NativeFontHintInterpreter.TrueType40 })
        foreach (NativeFontOutlinePolicy policy in Enum.GetValues<NativeFontOutlinePolicy>())
        foreach (NativeHintedCoverage coverage in new[] { NativeHintedCoverage.AntialiasedVector, NativeHintedCoverage.Monochrome })
        {
            byte[] font = File.ReadAllBytes(fontPath);
            using var context = new NativeTextShapingContext(font);
            Array.Clear(font);
            using var batch = context.CaptureOutlineBatch(0, ids, 13 * 64, 13 * 64, interpreter, policy);
            var glyphs = new NativeHintedGlyph[batch.Counts.Glyphs];
            var tags = new byte[batch.Counts.Points];
            var contourEnds = new int[batch.Counts.Contours];
            batch.CopyTo(glyphs, new NativeHintedPoint[batch.Counts.Points],
                tags, contourEnds);
            using var geometry = batch.CreateGeometry(coverage: coverage);
            using var scalar = batch.CreateGeometry(NativeHintedProjectionPolicy.ScalarReference,
                coverage);
            Check(geometry.Counts.Descriptors == ids.Length && geometry.Counts.Outlines > 0 &&
                geometry.Counts.Segments > 0, "complete actual font geometry");
            var expected = Copy(geometry);
            Equal(expected, Copy(scalar));
            if (coverage == NativeHintedCoverage.Monochrome)
                ValidateScanMetadata(glyphs, tags, contourEnds, expected.Outlines, expected.Segments, expected.Descriptors);
            for (int i = 0; i < ids.Length; i++)
            {
                ref readonly var descriptor = ref expected.Descriptors[i];
                Check(descriptor.GlyphIndex == ids[i] && glyphs[i].GlyphIndex == ids[i], "original repeated IDs");
                Check(glyphs[i].PointCount == 0
                    ? descriptor.OutlineIndex == uint.MaxValue
                    : descriptor.OutlineIndex < geometry.Counts.Outlines, "original ink/no-ink mapping");
            }

            var untouched = Buffers(geometry);
            var sentinel = Buffers(geometry);
            bool rejected = false;
            try { geometry.CopyTo(untouched.Outlines, untouched.Segments, untouched.Descriptors.AsSpan(0, ids.Length - 1)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "short descriptor output rejected");
            Equal(sentinel, untouched);

            // The geometry owns its source generation independently of both
            // public source handles, across the actual loaded package boundary.
            batch.Dispose();
            context.Dispose();
            Equal(expected, Copy(geometry));
            Parallel.For(0, 8, _ => Equal(expected, Copy(geometry)));
            scalar.Dispose();
            Equal(expected, Copy(geometry));
            geometry.Dispose();
            rejected = false;
            try { Copy(geometry); }
            catch (ObjectDisposedException error) { rejected = error.ObjectName == nameof(NativeFontOutlineGeometry); }
            Check(rejected, "disposed geometry rejected");
        }
        Console.WriteLine("package-consumer: font outline geometry passed (four load targets, both interpreters, vector/binary preparation, raw contour policies, scalar equality, original descriptor order, atomic tails, independent lifetime; not binary pixel qualification)");
    }

    private static void ValidateScanMetadata(NativeHintedGlyph[] glyphs, byte[] tags, int[] ends,
        NativeGlyphOutline[] outlines, NativePathSegment[] segments, NativeFontOutlineDescriptor[] descriptors)
    {
        for (int i = 0; i < glyphs.Length; i++)
        {
            ref readonly NativeHintedGlyph glyph = ref glyphs[i];
            uint outlineIndex = descriptors[i].OutlineIndex;
            if (outlineIndex == uint.MaxValue) continue;
            NativeGlyphOutline outline = outlines[outlineIndex];
            uint mode = (glyph.OutlineFlags & 8U) != 0 ? 2U :
                ((glyph.OutlineFlags & 16U) != 0 ? 4U : 0U) | ((glyph.OutlineFlags & 32U) != 0 ? 0U : 1U);
            uint pointStart = 0, segmentStart = 0;
            for (uint contour = 0; contour < glyph.ContourCount; contour++)
            {
                byte tag = tags[glyph.PointOffset + pointStart];
                bool hasOverride = (tag & 4) != 0;
                if (hasOverride) mode = (uint)tag >> 5;
                uint pointEnd = checked((uint)ends[glyph.ContourOffset + contour] + 1);
                uint pointCount = pointEnd - pointStart;
                pointStart = pointEnd;
                if (pointCount < 2) continue;
                Check(segmentStart < outline.SegmentCount, "every original contour has a segment descriptor");
                NativePathSegment first = segments[checked((int)(outline.SegmentOffset + segmentStart))];
                uint descriptor = NativeFontScanMetadata.SegmentMarker | mode | (glyph.OutlineFlags << 16) |
                    (hasOverride ? NativeFontScanMetadata.ContourOverride : 0U);
                Check(first.Pad0 == descriptor && first.Pad1 == segmentStart && first.Pad2 > 0 &&
                    first.Pad2 <= outline.SegmentCount - segmentStart, "exact original contour flags and range");
                for (uint j = 0; j < first.Pad2; j++)
                {
                    NativePathSegment segment = segments[checked((int)(outline.SegmentOffset + segmentStart + j))];
                    Check(segment.Pad0 == descriptor && segment.Pad1 == segmentStart && segment.Pad2 == first.Pad2,
                        "complete immutable contour identity");
                }
                segmentStart += first.Pad2;
            }
            Check(segmentStart == outline.SegmentCount, "exact original contour partition");
        }
    }

    private static (NativeGlyphOutline[] Outlines, NativePathSegment[] Segments, NativeFontOutlineDescriptor[] Descriptors)
        Buffers(NativeFontOutlineGeometry geometry)
    {
        var result = (Outlines: new NativeGlyphOutline[checked((int)geometry.Counts.Outlines) + 1],
            Segments: new NativePathSegment[checked((int)geometry.Counts.Segments) + 1],
            Descriptors: new NativeFontOutlineDescriptor[checked((int)geometry.Counts.Descriptors) + 1]);
        MemoryMarshal.AsBytes(result.Outlines.AsSpan()).Fill(0xA5);
        MemoryMarshal.AsBytes(result.Segments.AsSpan()).Fill(0xA5);
        MemoryMarshal.AsBytes(result.Descriptors.AsSpan()).Fill(0xA5);
        return result;
    }

    private static (NativeGlyphOutline[] Outlines, NativePathSegment[] Segments, NativeFontOutlineDescriptor[] Descriptors)
        Copy(NativeFontOutlineGeometry geometry)
    {
        var result = Buffers(geometry);
        var sentinel = Buffers(geometry);
        geometry.CopyTo(result.Outlines, result.Segments, result.Descriptors);
        Check(MemoryMarshal.AsBytes(result.Outlines.AsSpan(^1)).SequenceEqual(MemoryMarshal.AsBytes(sentinel.Outlines.AsSpan(^1))) &&
            MemoryMarshal.AsBytes(result.Segments.AsSpan(^1)).SequenceEqual(MemoryMarshal.AsBytes(sentinel.Segments.AsSpan(^1))) &&
            MemoryMarshal.AsBytes(result.Descriptors.AsSpan(^1)).SequenceEqual(MemoryMarshal.AsBytes(sentinel.Descriptors.AsSpan(^1))),
            "unchanged caller tails");
        return result;
    }

    private static void Equal(
        (NativeGlyphOutline[] Outlines, NativePathSegment[] Segments, NativeFontOutlineDescriptor[] Descriptors) expected,
        (NativeGlyphOutline[] Outlines, NativePathSegment[] Segments, NativeFontOutlineDescriptor[] Descriptors) actual)
    {
        Check(MemoryMarshal.AsBytes(expected.Outlines.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.Outlines.AsSpan())) &&
            MemoryMarshal.AsBytes(expected.Segments.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.Segments.AsSpan())) &&
            MemoryMarshal.AsBytes(expected.Descriptors.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual.Descriptors.AsSpan())),
            "complete original geometry equality");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Native font outline package: " + message);
    }
}
