using ProGPU.Backend.Native;

// Diagnostic only. The caller captures both batches from the same immutable
// source/context, descriptor order, exact device size, phase and interpreter.
// This does not change the public Unhinted policy or admit GDI+ source behavior.
internal static class ScanWitness
{
    internal const uint Mask = 7u | NativeFontScanMetadata.ContourOverride | (0x38u << 16);

    internal static NativePathSegment[] Apply(
        NativeHintedGlyph[] originalGlyphs, byte[] originalTags, int[] originalEnds,
        NativeFontOutlineDescriptor[] originalDescriptors, NativePathSegment[] originalSegments,
        NativeHintedGlyph[] witnessGlyphs, byte[] witnessTags, int[] witnessEnds,
        NativeFontOutlineDescriptor[] witnessDescriptors, NativePathSegment[] witnessSegments)
    {
        static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); }

        Check(originalGlyphs.Length == witnessGlyphs.Length &&
            originalDescriptors.Length == originalGlyphs.Length &&
            witnessDescriptors.Length == originalGlyphs.Length &&
            originalTags.Length == witnessTags.Length && originalEnds.Length == witnessEnds.Length &&
            originalSegments.Length == witnessSegments.Length, "Incomplete scan witness inventory.");
        uint nextPoint = 0, nextContour = 0;
        for (int index = 0; index < originalGlyphs.Length; index++)
        {
            var a = originalGlyphs[index]; var b = witnessGlyphs[index];
            Check(a.GlyphIndex == b.GlyphIndex && a.PointOffset == b.PointOffset && a.PointCount == b.PointCount &&
                a.ContourOffset == b.ContourOffset && a.ContourCount == b.ContourCount &&
                a.PointOffset == nextPoint && a.ContourOffset == nextContour &&
                (ulong)a.PointOffset + a.PointCount <= (ulong)originalTags.Length &&
                (ulong)a.ContourOffset + a.ContourCount <= (ulong)originalEnds.Length,
                "Scan witness descriptor topology changed.");
            Check(originalDescriptors[index].GlyphIndex == a.GlyphIndex && witnessDescriptors[index].GlyphIndex == a.GlyphIndex &&
                originalDescriptors[index].OutlineIndex == witnessDescriptors[index].OutlineIndex,
                "Scan witness outline mapping changed.");
            for (uint p = 0; p < a.PointCount; p++)
                Check((originalTags[a.PointOffset + p] & 3) == (witnessTags[b.PointOffset + p] & 3),
                    "Scan witness curve topology changed.");
            int previous = -1;
            for (uint c = 0; c < a.ContourCount; c++)
            {
                int end = originalEnds[a.ContourOffset + c];
                Check(end > previous && (uint)end < a.PointCount && end == witnessEnds[b.ContourOffset + c],
                    "Scan witness contour boundary changed.");
                previous = end;
            }
            Check(a.PointCount == 0 ? a.ContourCount == 0 : previous == checked((int)a.PointCount) - 1,
                "Scan witness contour inventory incomplete.");
            nextPoint = checked(nextPoint + a.PointCount);
            nextContour = checked(nextContour + a.ContourCount);
        }
        Check(nextPoint == originalTags.Length && nextContour == originalEnds.Length,
            "Scan witness has unowned topology records.");
        for (int i = 0; i < originalSegments.Length; i++)
        {
            var a = originalSegments[i]; var b = witnessSegments[i];
            Check(a.Kind == b.Kind && a.Pad1 == b.Pad1 && a.Pad2 == b.Pad2 &&
                (a.Pad0 & NativeFontScanMetadata.SegmentMarker) != 0 &&
                (b.Pad0 & NativeFontScanMetadata.SegmentMarker) != 0,
                "Scan witness segment topology changed.");
        }
        // Only a new diagnostic array is changed after every descriptor passes.
        // Keep all original geometry bits, high-precision flags and contour ranges.
        var result = new NativePathSegment[originalSegments.Length];
        for (int i = 0; i < result.Length; i++)
        {
            var a = originalSegments[i];
            uint scan = (a.Pad0 & ~Mask) | (witnessSegments[i].Pad0 & Mask);
            result[i] = new(a.Kind, a.P0, a.P1, a.P2, a.P3, scan, a.Pad1, a.Pad2);
        }
        return result;
    }
}
