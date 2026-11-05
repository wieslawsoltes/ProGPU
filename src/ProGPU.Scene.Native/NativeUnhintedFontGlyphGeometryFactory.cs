using System.Numerics;
using ProGPU.Backend.Native;
using ProGPU.Text;

namespace ProGPU.Scene.Native;

public static partial class NativeHintedGlyphGeometryFactory
{
    /// <summary>
    /// Captures instruction-free physical outlines and a separate monochrome
    /// scan-metadata witness from the same immutable font and frozen request.
    /// Only scan/dropout bits are imported after complete topology validation;
    /// original coordinates, bounds, placement and non-scan flags are retained.
    /// This explicit composition does not select a GDI/GDI+ rendering policy.
    /// </summary>
    public static HintedGlyphGeometry CaptureUnhintedFontBatchWithScanMetadata(
        NativeTextShapingContext context, uint fontIndex, ReadOnlySpan<uint> glyphIndices,
        ReadOnlySpan<Vector2> positions, uint xPixelsPerEm26_6, uint yPixelsPerEm26_6,
        NativeFontHintInterpreter interpreter, float dpiScale,
        uint xPhase26_6 = 0, uint yPhase26_6 = 0,
        ReadOnlySpan<int> variationCoordinates16_16 = default,
        NativeHintedProjectionPolicy projection = NativeHintedProjectionPolicy.Automatic)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!float.IsFinite(dpiScale) || dpiScale <= 0f || !float.IsFinite(1f / dpiScale))
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        if (glyphIndices.Length > 1 << 24 || positions.Length != glyphIndices.Length)
            throw new ArgumentException("Positions must cover every original font descriptor.", nameof(positions));
        if (variationCoordinates16_16.Length > 1 << 20)
            throw new ArgumentOutOfRangeException(nameof(variationCoordinates16_16));
        if (interpreter is not (NativeFontHintInterpreter.TrueType35 or NativeFontHintInterpreter.TrueType40))
            throw new ArgumentOutOfRangeException(nameof(interpreter));
        if (projection is not (NativeHintedProjectionPolicy.Automatic or
            NativeHintedProjectionPolicy.IntrinsicSimd or NativeHintedProjectionPolicy.ScalarReference))
            throw new ArgumentOutOfRangeException(nameof(projection));
        // Both captures consume these private snapshots, never mutable caller spans.
        uint[] ids = glyphIndices.ToArray();
        Vector2[] originalPositions = positions.ToArray();
        int[] coordinates = variationCoordinates16_16.ToArray();
        foreach (Vector2 position in originalPositions)
            if (!Finite(position)) throw new ArgumentException("Original positions must be finite.", nameof(positions));

        NativeFontOutlineGeometry? originalOwner = null;
        try
        {
            FontScanSnapshot original;
            NativePathSegment[] retainedSegments;
            // Complete witness retirement precedes publication. The independent
            // original geometry owner remains the only transferred native lease.
            using (NativeHintedFontBatch raw = context.CaptureOutlineBatch(fontIndex, ids,
                xPixelsPerEm26_6, yPixelsPerEm26_6, interpreter, NativeFontOutlinePolicy.Unhinted,
                xPhase26_6, yPhase26_6, coordinates))
            using (NativeHintedFontBatch witness = context.CaptureOutlineBatch(fontIndex, ids,
                xPixelsPerEm26_6, yPixelsPerEm26_6, interpreter, NativeFontOutlinePolicy.Monochrome,
                xPhase26_6, yPhase26_6, coordinates))
            using (NativeFontOutlineGeometry witnessOwner = witness.CreateGeometry(projection, NativeHintedCoverage.Monochrome))
            {
                originalOwner = raw.CreateGeometry(projection, NativeHintedCoverage.Monochrome);
                original = ReadFontScanSnapshot(raw, originalOwner);
                FontScanSnapshot scan = ReadFontScanSnapshot(witness, witnessOwner);
                Require(original.Glyphs.Length == ids.Length);
                for (int i = 0; i < ids.Length; i++) Require(original.Glyphs[i].GlyphIndex == ids[i]);
                retainedSegments = ApplyUnhintedScanMetadata(original, scan);
            }
            ValidateGeometry(original.Outlines, retainedSegments, monochrome: true);
            ConvertGeometry(original.Outlines, retainedSegments, out var outlines, out var segments);
            var placements = new FontGlyphPlacement[ids.Length];
            for (int i = 0; i < placements.Length; i++)
                placements[i] = new(checked((uint)i), ids[i], original.Descriptors[i].OutlineIndex, originalPositions[i]);
            return HintedGlyphGeometry.FromFontPlacements(dpiScale, outlines, segments, placements, originalOwner);
        }
        catch (Exception failure)
        {
            try { originalOwner?.Dispose(); }
            catch (Exception cleanup)
            {
                try { failure.Data["NativeFontGeometryCleanupFailure"] = cleanup; }
                catch { /* Preserve the original publication failure. */ }
            }
            throw;
        }
    }

    internal sealed record FontScanSnapshot(NativeHintedGlyph[] Glyphs, byte[] Tags, int[] Ends,
        NativeGlyphOutline[] Outlines, NativeFontOutlineDescriptor[] Descriptors, NativePathSegment[] Segments);

    private static FontScanSnapshot ReadFontScanSnapshot(NativeHintedFontBatch batch, NativeFontOutlineGeometry owner)
    {
        Require(batch.Counts.Glyphs <= 1 << 24 && batch.Counts.Points <= 1 << 24 && batch.Counts.Contours <= 1 << 24 &&
            owner.Counts.Descriptors == batch.Counts.Glyphs && owner.Counts.Outlines <= 1 << 20 && owner.Counts.Segments <= 1 << 24);
        var glyphs = new NativeHintedGlyph[checked((int)batch.Counts.Glyphs)];
        var points = new NativeHintedPoint[checked((int)batch.Counts.Points)];
        var tags = new byte[points.Length];
        var ends = new int[checked((int)batch.Counts.Contours)];
        batch.CopyTo(glyphs, points, tags, ends);
        var outlines = new NativeGlyphOutline[checked((int)owner.Counts.Outlines)];
        var segments = new NativePathSegment[checked((int)owner.Counts.Segments)];
        var descriptors = new NativeFontOutlineDescriptor[glyphs.Length];
        owner.CopyTo(outlines, segments, descriptors);
        ValidateGeometry(outlines, segments, monochrome: true);
        return new(glyphs, tags, ends, outlines, descriptors, segments);
    }

    internal const uint UnhintedScanMask = 7u | NativeFontScanMetadata.ContourOverride | (0x38u << 16);

    // Original ProGPU-owned provenance: eng/probes/UnfittedScanWitness/native/ScanWitness.cs.
    // The public factory supplies both captures itself; arbitrary external pairs
    // are never admitted. Reject the complete inventory before constructing output.
    internal static NativePathSegment[] ApplyUnhintedScanMetadata(FontScanSnapshot original, FontScanSnapshot witness)
    {
        Require(original.Glyphs.Length == witness.Glyphs.Length &&
            original.Descriptors.Length == original.Glyphs.Length && witness.Descriptors.Length == original.Glyphs.Length &&
            original.Tags.Length == witness.Tags.Length && original.Ends.Length == witness.Ends.Length &&
            original.Outlines.Length == witness.Outlines.Length && original.Segments.Length == witness.Segments.Length);
        uint nextPoint = 0, nextContour = 0;
        for (int i = 0; i < original.Glyphs.Length; i++)
        {
            ref readonly NativeHintedGlyph a = ref original.Glyphs[i];
            ref readonly NativeHintedGlyph b = ref witness.Glyphs[i];
            Require(a.GlyphIndex == b.GlyphIndex && a.PointOffset == b.PointOffset && a.PointCount == b.PointCount &&
                a.ContourOffset == b.ContourOffset && a.ContourCount == b.ContourCount &&
                a.PointOffset == nextPoint && a.ContourOffset == nextContour &&
                Range(a.PointOffset, a.PointCount, original.Tags.Length) && Range(a.ContourOffset, a.ContourCount, original.Ends.Length));
            uint outline = original.Descriptors[i].OutlineIndex;
            Require(original.Descriptors[i].GlyphIndex == a.GlyphIndex && witness.Descriptors[i].GlyphIndex == a.GlyphIndex &&
                outline == witness.Descriptors[i].OutlineIndex && (outline == uint.MaxValue || outline < original.Outlines.Length));
            for (uint p = 0; p < a.PointCount; p++)
                Require((original.Tags[a.PointOffset + p] & 3) == (witness.Tags[b.PointOffset + p] & 3));
            int previous = -1;
            for (uint c = 0; c < a.ContourCount; c++)
            {
                int end = original.Ends[a.ContourOffset + c];
                Require(end > previous && (uint)end < a.PointCount && end == witness.Ends[b.ContourOffset + c]);
                previous = end;
            }
            Require(a.PointCount == 0 ? a.ContourCount == 0 : previous == checked((int)a.PointCount) - 1);
            nextPoint = checked(nextPoint + a.PointCount);
            nextContour = checked(nextContour + a.ContourCount);
        }
        Require(nextPoint == original.Tags.Length && nextContour == original.Ends.Length);
        for (int i = 0; i < original.Outlines.Length; i++)
            Require(original.Outlines[i].SegmentOffset == witness.Outlines[i].SegmentOffset &&
                original.Outlines[i].SegmentCount == witness.Outlines[i].SegmentCount);
        for (int i = 0; i < original.Segments.Length; i++)
        {
            ref readonly NativePathSegment a = ref original.Segments[i];
            ref readonly NativePathSegment b = ref witness.Segments[i];
            Require(a.Kind == b.Kind && a.Pad1 == b.Pad1 && a.Pad2 == b.Pad2 &&
                (a.Pad0 & NativeFontScanMetadata.SegmentMarker) != 0 && (b.Pad0 & NativeFontScanMetadata.SegmentMarker) != 0);
        }
        var result = new NativePathSegment[original.Segments.Length];
        for (int i = 0; i < result.Length; i++)
        {
            ref readonly NativePathSegment a = ref original.Segments[i];
            uint scan = (a.Pad0 & ~UnhintedScanMask) | (witness.Segments[i].Pad0 & UnhintedScanMask);
            result[i] = new(a.Kind, a.P0, a.P1, a.P2, a.P3, scan, a.Pad1, a.Pad2);
        }
        return result;
    }
}
