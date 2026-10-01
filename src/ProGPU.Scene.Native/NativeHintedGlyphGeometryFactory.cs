using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Backend.Native;
using ProGPU.Text;

namespace ProGPU.Scene.Native;

/// <summary>Retains an original native hinted generation for recorded GPU replay.</summary>
public static class NativeHintedGlyphGeometryFactory
{
    /// <summary>
    /// Copies the producer's physical Y-up GPU geometry and preserves every
    /// positioned owner, including no-ink owners. The original read lease owns
    /// all font bytes, axes, source, shaping, layout and interaction metadata
    /// until the last recorded use retires. No font is decoded or hinted here.
    /// </summary>
    public static HintedGlyphGeometry Create(NativeHintedGlyphResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        NativeHintedGlyphResourceReadLease lease = resource.AcquireReadLease();
        try
        {
            Validate(lease);
            var segments = new GpuSegment[lease.Segments.Length];
            for (int i = 0; i < segments.Length; i++)
            {
                ref readonly NativePathSegment s = ref lease.Segments[i];
                segments[i] = new GpuSegment
                {
                    P0 = s.P0, P1 = s.P1, P2 = s.P2, P3 = s.P3,
                    SegmentType = (uint)s.Kind,
                    Pad0 = s.Pad0, Pad1 = s.Pad1, Pad2 = s.Pad2
                };
            }
            var outlines = new GpuGlyphRecord[lease.Outlines.Length];
            for (int i = 0; i < outlines.Length; i++)
            {
                ref readonly NativeGlyphOutline o = ref lease.Outlines[i];
                outlines[i] = new GpuGlyphRecord
                {
                    StartSegment = checked((uint)o.SegmentOffset),
                    SegmentCount = checked((uint)o.SegmentCount),
                    MinX = o.Minimum.X, MinY = o.Minimum.Y,
                    MaxX = o.Maximum.X, MaxY = o.Maximum.Y
                };
            }
            var occurrences = new HintedGlyphOccurrence[lease.Glyphs.Length];
            for (int i = 0; i < occurrences.Length; i++)
            {
                ref readonly NativePositionedTextGlyph g = ref lease.Glyphs[i];
                ref readonly NativeHintedParagraphGlyphOwner owner = ref lease.PositionedOwners[i];
                occurrences[i] = new HintedGlyphOccurrence(
                    checked((uint)i), g.GlyphIndex, g.GlyphId, g.FontIndex,
                    owner.RunIndex, owner.RunGlyphIndex, owner.DescriptorIndex,
                    lease.PositionedOutlineIndices[i], checked((uint)g.Cluster),
                    lease.ClusterEnds[i], lease.BidiLevels[i],
                    new Vector2(g.X, g.Y), new Vector2(g.AdvanceX, g.AdvanceY));
            }
            // Ownership transfers only after every conversion has succeeded.
            return new HintedGlyphGeometry(lease.DpiScale, outlines, segments, occurrences, lease);
        }
        catch (Exception failure)
        {
            try { lease.Dispose(); }
            catch (Exception cleanup) { failure.Data["NativeHintedGlyphReadLeaseCleanupFailure"] = cleanup; }
            throw;
        }
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw new ArgumentException("The original hinted glyph generation has invalid retained records.");
    }

    private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
    private static bool Nonnegative(float v) => float.IsFinite(v) && v >= 0f;
    private static bool Scalar(uint v) => v <= 0x10FFFFU && v is not (>= 0xD800U and <= 0xDFFFU);
    private static bool Extent(float x, float y, float w, float h) =>
        float.IsFinite(x) && float.IsFinite(y) && Nonnegative(w) && Nonnegative(h) &&
        float.IsFinite(x + w) && float.IsFinite(y + h);
    private static bool Same(in NativeHintedParagraphGlyphOwner a, in NativeHintedParagraphGlyphOwner b) =>
        a.RunIndex == b.RunIndex && a.RunGlyphIndex == b.RunGlyphIndex && a.DescriptorIndex == b.DescriptorIndex;
    private static bool Range(uint start, uint count, int length) =>
        start <= (uint)length && count <= (uint)length - start;

    private static bool HasSourceBoundary(ReadOnlySpan<NativeTextScalar> source, uint start, uint count, int cluster)
    {
        int lo = checked((int)start), hi = checked((int)(start + count));
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (source[mid].InputIndex < cluster) lo = mid + 1;
            else hi = mid;
        }
        return lo < start + count && source[lo].InputIndex == cluster;
    }

    private static bool ValidDigitPolicy(uint policy)
    {
        const uint scalarMask = 0x001F_FFFFU;
        const uint contextual = 0x8000_0000U;
        const uint sourceBidi = 0x4000_0000U;
        if (policy == 0) return true;
        uint zero = policy & scalarMask;
        // The producer validated decimal values zero through nine with its
        // original Unicode tables. Here validate the wire bits/scalar extent,
        // without introducing a different Unicode version or reshaping policy.
        if ((policy & ~(scalarMask | contextual | sourceBidi)) != 0 || zero == 0 || zero > 0x10FFF6U)
            return false;
        for (uint i = 0; i < 10; i++) if (!Scalar(zero + i)) return false;
        return true;
    }

    private static void ValidateScalars(ReadOnlySpan<NativeTextScalar> scalars)
    {
        ulong end = 0;
        foreach (ref readonly NativeTextScalar s in scalars)
        {
            Require(Scalar(s.CodePoint) && s.InputLength != 0 && s.Reserved == 0 &&
                s.InputIndex >= end && (ulong)s.InputIndex + s.InputLength <= int.MaxValue);
            end = (ulong)s.InputIndex + s.InputLength;
        }
    }

    private static void Validate(NativeHintedGlyphResourceReadLease v)
    {
        const int maximumSlots = 1 << 24;
        const int maximumOutlines = 1 << 20;
        float reciprocal = 1f / v.DpiScale;
        Require(float.IsFinite(v.DpiScale) && v.DpiScale > 0f && float.IsFinite(reciprocal) &&
            (v.Projection is NativeHintedProjectionPolicy.Automatic or
                NativeHintedProjectionPolicy.IntrinsicSimd or NativeHintedProjectionPolicy.ScalarReference) &&
            (uint)v.Coverage <= (uint)NativeHintedCoverage.AntialiasedVector &&
            v.ParagraphLevel is 0 or 1 && (uint)v.ShapingDirection <= (uint)NativeTextDirection.RightToLeft &&
            (v.ShapingFlags & ~NativeTextShapeFlags.ZeroMarkAdvances) == 0);
        NativeHintedParagraphCounts c = v.Counts;
        Require(c.SourceScalarCount <= maximumSlots && c.AdmittedScalarCount == c.SourceScalarCount &&
            c.StyleCount <= maximumSlots && c.RunCount <= maximumSlots && c.LogicalGlyphCount <= maximumSlots &&
            c.PositionedGlyphCount <= maximumSlots && c.LineCount <= maximumSlots &&
            c.ClusterBoxCount <= maximumSlots && c.CaretStopCount <= maximumSlots &&
            v.Outlines.Length <= maximumOutlines && v.Segments.Length <= maximumSlots &&
            v.FontBytes.Length <= 256 * 1024 * 1024 && v.FontSources.Length <= maximumSlots &&
            v.SourceOutlineIndices.Length <= maximumSlots && v.RunOutlineIndices.Length == v.LogicalGlyphs.Length &&
            v.VariationCoordinates1616.Length <= maximumSlots && v.NormalizedCoordinates.Length <= maximumSlots &&
            v.Features.Length <= maximumSlots &&
            v.PreContext.Length <= maximumSlots && v.PostContext.Length <= maximumSlots);
        NativeTextLayoutOptions l = v.Layout;
        NativeTextParagraphResult result = v.Result;
        Require(l.StructSize == Unsafe.SizeOf<NativeTextLayoutOptions>() &&
            float.IsFinite(l.Scale) && l.Scale > 0f && Nonnegative(l.MaximumWidth) &&
            Nonnegative(l.LineHeight) && Nonnegative(l.EllipsisAdvance) &&
            l.Direction <= (uint)NativeTextDirection.RightToLeft && l.Trimming == 0 &&
            l.Alignment <= (uint)NativeTextAlignment.Justify && l.Reserved0 == 0 && l.Reserved1 == 0 &&
            result.StructSize == Unsafe.SizeOf<NativeTextParagraphResult>() && result.ErrorCode == 0 &&
            result.ErrorStage == 0 && result.GlyphCount == c.PositionedGlyphCount &&
            result.ShapedGlyphCount == c.LogicalGlyphCount && result.LineCount == c.LineCount &&
            result.ParagraphLevel == v.ParagraphLevel && Nonnegative(result.ContentWidth) &&
            Nonnegative(result.ContentHeight) && Nonnegative(result.MeasuredWidth) && Nonnegative(result.MeasuredHeight));
        ValidateScalars(v.SourceScalars);
        ValidateScalars(v.AdmittedScalars);
        ValidateScalars(v.PreContext);
        ValidateScalars(v.PostContext);
        for (int i = 0; i < v.SourceScalars.Length; i++)
        {
            ref readonly NativeTextScalar source = ref v.SourceScalars[i];
            ref readonly NativeTextScalar admitted = ref v.AdmittedScalars[i];
            ref readonly NativeTextBidiLevel level = ref v.ScalarLevels[i];
            Require(source.InputIndex == admitted.InputIndex && source.InputLength == admitted.InputLength &&
                admitted.CodePoint is not (9U or 0xFFFCU) && level.InputIndex == source.InputIndex &&
                level.InputLength == source.InputLength && level.Level is >= 0 and <= 125 && level.Reserved == 0);
        }
        foreach (ref readonly NativeTextFeature f in v.Features)
        {
            Require(f.Tag != 0 && f.Start <= f.End);
            for (int shift = 0; shift < 32; shift += 8)
                Require(((f.Tag >> shift) & 255) is >= 32 and <= 126);
        }
        foreach (short coordinate in v.NormalizedCoordinates)
            Require(coordinate is >= -16384 and <= 16384);
        uint fontEnd = 0;
        foreach (ref readonly NativeHintedGlyphFontSource font in v.FontSources)
        {
            // Exact original bytes, face and variable-instance validation belong
            // to the originating producer. Do not reopen or normalize the font.
            Require(font.ByteOffset == fontEnd && font.ByteCount > 0 &&
                Range(font.ByteOffset, font.ByteCount, v.FontBytes.Length) &&
                font.FaceIndex <= ushort.MaxValue && font.UnitsPerEm > 0);
            fontEnd = checked(fontEnd + font.ByteCount);
        }
        Require(fontEnd == v.FontBytes.Length);
        uint scalarEnd = 0, variationEnd = 0;
        bool sourceDigitBidi = false;
        for (int i = 0; i < v.Styles.Length; i++)
        {
            ref readonly NativeTextStyleRun s = ref v.Styles[i];
            ref readonly NativeHintedParagraphDeviceStyle d = ref v.DeviceStyles[i];
            ref readonly NativeTextStyleMetrics m = ref v.SourceMetrics[i];
            Require(s.ScalarStart == scalarEnd && s.ScalarCount > 0 &&
                Range(s.ScalarStart, s.ScalarCount, v.SourceScalars.Length) &&
                s.FontIndex < v.FontSources.Length && float.IsFinite(s.Scale) && s.Scale > 0f &&
                Range(s.FeatureStart, s.FeatureCount, v.Features.Length) &&
                ValidDigitPolicy(s.DigitSubstitution) &&
                (s.Percent == 0 || Scalar(s.Percent)) && (s.GroupSeparator == 0 || Scalar(s.GroupSeparator)) &&
                (s.DecimalSeparator == 0 || Scalar(s.DecimalSeparator)) &&
                d.FontIndex == s.FontIndex && d.SourceScale == s.Scale && d.Reserved == 0 &&
                d.LogicalUnitsPerPhysicalPixel == reciprocal && d.LogicalUnitsPerPhysicalPixel * v.DpiScale == 1f &&
                d.XPixelsPerEm266 is > 0 and <= int.MaxValue && d.YPixelsPerEm266 is > 0 and <= int.MaxValue &&
                d.Interpreter is 35 or 40 && d.XPhase266 < 64 && d.YPhase266 < 64 &&
                d.VariationStart == variationEnd && Range(d.VariationStart, d.VariationCount, v.VariationCoordinates1616.Length) &&
                d.VariationCount == v.NormalizedCoordinates.Length &&
                Nonnegative(m.Ascent) && Nonnegative(m.Descent) && float.IsFinite(m.Ascent + m.Descent));
            scalarEnd = checked(scalarEnd + s.ScalarCount);
            variationEnd = checked(variationEnd + d.VariationCount);
            sourceDigitBidi |= (s.DigitSubstitution & 0x4000_0000U) != 0;
        }
        Require(scalarEnd == c.SourceScalarCount && variationEnd == v.VariationCoordinates1616.Length &&
            sourceDigitBidi == v.SourceDigitBidi);
        uint runScalarEnd = 0, logicalEnd = 0, sourceEnd = 0, runEnd = 0, outlineEnd = 0, segmentEnd = 0;
        for (int i = 0; i < v.Runs.Length; i++)
        {
            ref readonly NativeHintedParagraphRun r = ref v.Runs[i];
            ref readonly NativeHintedGlyphRunSlice slice = ref v.RunSlices[i];
            Require(r.ScalarStart == runScalarEnd && r.ScalarCount > 0 && Range(r.ScalarStart, r.ScalarCount, v.SourceScalars.Length) &&
                r.LogicalStart == logicalEnd && Range(r.LogicalStart, r.LogicalCount, v.LogicalGlyphs.Length) &&
                r.StyleIndex < v.Styles.Length && r.FontIndex < v.FontSources.Length && r.BidiLevel is >= 0 and <= 125 &&
                slice.SourceStart == sourceEnd && slice.SourceCount == r.SourceDescriptorCount &&
                Range(slice.SourceStart, slice.SourceCount, v.SourceOutlineIndices.Length) &&
                slice.RunStart == runEnd && slice.RunCount == r.LogicalCount && Range(slice.RunStart, slice.RunCount, v.RunOutlineIndices.Length) &&
                slice.OutlineStart == outlineEnd && Range(slice.OutlineStart, slice.OutlineCount, v.Outlines.Length) &&
                slice.SegmentStart == segmentEnd && Range(slice.SegmentStart, slice.SegmentCount, v.Segments.Length));
            ref readonly NativeTextStyleRun style = ref v.Styles[(int)r.StyleIndex];
            ref readonly NativeHintedParagraphDeviceStyle device = ref v.DeviceStyles[(int)r.StyleIndex];
            Require(r.FontIndex == style.FontIndex && r.SourceScale == style.Scale &&
                r.LogicalUnitsPerPhysicalPixel == device.LogicalUnitsPerPhysicalPixel &&
                r.ScalarStart >= style.ScalarStart &&
                (ulong)r.ScalarStart + r.ScalarCount <= (ulong)style.ScalarStart + style.ScalarCount);
            for (uint j = 0; j < r.ScalarCount; j++)
                Require(v.ScalarLevels[(int)(r.ScalarStart + j)].Level == r.BidiLevel);
            uint inkCount = 0;
            for (uint j = 0; j < slice.SourceCount; j++)
            {
                uint outline = v.SourceOutlineIndices[(int)(slice.SourceStart + j)];
                if (outline == uint.MaxValue) continue;
                Require(outline == slice.OutlineStart + inkCount && outline < v.Outlines.Length &&
                    v.OutlineOwners[(int)outline].RunIndex == i && v.OutlineOwners[(int)outline].DescriptorIndex == j);
                ref readonly NativeGlyphOutline geometry = ref v.Outlines[(int)outline];
                Require(geometry.SegmentOffset >= slice.SegmentStart &&
                    (ulong)geometry.SegmentOffset + (ulong)geometry.SegmentCount <= (ulong)slice.SegmentStart + slice.SegmentCount);
                inkCount++;
            }
            Require(inkCount == slice.OutlineCount);
            var usedRunGlyphs = new bool[checked((int)r.LogicalCount)];
            var descriptorIds = new uint[checked((int)slice.SourceCount)];
            Array.Fill(descriptorIds, uint.MaxValue);
            uint reversedGroupEnd = r.LogicalCount, groupStart = 0, groupEnd = 0;
            for (uint j = 0; j < r.LogicalCount; j++)
            {
                int index = checked((int)(r.LogicalStart + j));
                ref readonly NativeHintedParagraphGlyphOwner owner = ref v.LogicalOwners[index];
                ref readonly NativeTextShapingGlyph glyph = ref v.LogicalGlyphs[index];
                Require(owner.RunIndex == i && owner.RunGlyphIndex < r.LogicalCount && owner.DescriptorIndex < slice.SourceCount &&
                    !usedRunGlyphs[(int)owner.RunGlyphIndex] && (glyph.Flags & ~7U) == 0 && Scalar(glyph.CodePoint) && glyph.Cluster >= 0 &&
                    v.LogicalClusterEnds[index] > glyph.Cluster && v.LogicalBidiLevels[index] == r.BidiLevel &&
                    v.GlyphScales[index] == r.LogicalUnitsPerPhysicalPixel / 64f &&
                    v.RunOutlineIndices[(int)(slice.RunStart + owner.RunGlyphIndex)] ==
                        v.SourceOutlineIndices[(int)(slice.SourceStart + owner.DescriptorIndex)]);
                usedRunGlyphs[(int)owner.RunGlyphIndex] = true;
                ref readonly NativeTextScalar last = ref v.SourceScalars[^1];
                Require(HasSourceBoundary(v.SourceScalars, r.ScalarStart, r.ScalarCount, glyph.Cluster) &&
                    (ulong)v.LogicalClusterEnds[index] <= (ulong)last.InputIndex + last.InputLength &&
                    (descriptorIds[(int)owner.DescriptorIndex] == uint.MaxValue ||
                        descriptorIds[(int)owner.DescriptorIndex] == glyph.GlyphId));
                descriptorIds[(int)owner.DescriptorIndex] = glyph.GlyphId;
                if ((r.BidiLevel & 1) == 0) Require(owner.RunGlyphIndex == j);
                else
                {
                    if (j == groupEnd)
                    {
                        groupStart = j; groupEnd = j + 1;
                        while (groupEnd < r.LogicalCount &&
                            v.LogicalGlyphs[(int)(r.LogicalStart + groupEnd)].Cluster == glyph.Cluster) groupEnd++;
                        reversedGroupEnd -= groupEnd - groupStart;
                    }
                    Require(owner.RunGlyphIndex == reversedGroupEnd + j - groupStart);
                }
                if (index > 0)
                {
                    int previous = v.LogicalGlyphs[index - 1].Cluster;
                    Require(glyph.Cluster >= previous &&
                        (glyph.Cluster == previous ? v.LogicalClusterEnds[index] == v.LogicalClusterEnds[index - 1] :
                            v.LogicalClusterEnds[index - 1] <= glyph.Cluster));
                }
            }
            runScalarEnd += r.ScalarCount; logicalEnd += r.LogicalCount; sourceEnd += slice.SourceCount;
            runEnd += slice.RunCount; outlineEnd += slice.OutlineCount; segmentEnd += slice.SegmentCount;
        }
        Require(runScalarEnd == c.SourceScalarCount && logicalEnd == c.LogicalGlyphCount &&
            sourceEnd == v.SourceOutlineIndices.Length && runEnd == v.RunOutlineIndices.Length &&
            outlineEnd == v.Outlines.Length && segmentEnd == v.Segments.Length);
        nuint geometryEnd = 0;
        foreach (ref readonly NativeGlyphOutline o in v.Outlines)
        {
            Require(geometryEnd <= (nuint)v.Segments.Length && o.SegmentOffset == geometryEnd &&
                o.SegmentCount > 0 && o.SegmentCount <= (nuint)v.Segments.Length - geometryEnd &&
                o.RasterScale == 1f && o.SubpixelX == 0f && Finite(o.Minimum) && Finite(o.Maximum) &&
                o.Maximum.X > o.Minimum.X && o.Maximum.Y > o.Minimum.Y);
            Vector2 first = default, previous = default;
            bool closed = true;
            for (nuint j = 0; j < o.SegmentCount; j++)
            {
                ref readonly NativePathSegment s = ref v.Segments[checked((int)(o.SegmentOffset + j))];
                Require((uint)s.Kind <= (uint)NativePathSegmentKind.Cubic && s.Pad0 == 0 && s.Pad1 == 0 && s.Pad2 == 0 &&
                    Finite(s.P0) && Finite(s.P1) && Finite(s.P2) && Finite(s.P3));
                if (closed) first = s.P0;
                else Require(previous == s.P0);
                Vector2 end = s.Kind == NativePathSegmentKind.Line ? s.P1 :
                    s.Kind == NativePathSegmentKind.Quadratic ? s.P2 : s.P3;
                Require(Inside(s.P0, o) && Inside(s.P1, o) &&
                    ((uint)s.Kind < (uint)NativePathSegmentKind.Quadratic || Inside(s.P2, o)) &&
                    ((uint)s.Kind < (uint)NativePathSegmentKind.Cubic || Inside(s.P3, o)));
                closed = end == first;
                previous = end;
            }
            Require(closed);
            geometryEnd += o.SegmentCount;
        }
        Require(geometryEnd == (nuint)v.Segments.Length);
        for (int i = 0; i < v.Glyphs.Length; i++)
        {
            ref readonly NativePositionedTextGlyph g = ref v.Glyphs[i];
            ref readonly NativeHintedParagraphGlyphOwner owner = ref v.PositionedOwners[i];
            Require(g.GlyphIndex < v.LogicalGlyphs.Length && Same(owner, v.LogicalOwners[(int)g.GlyphIndex]) &&
                Finite(new(g.X, g.Y)) && Finite(new(g.AdvanceX, g.AdvanceY)) && Finite(new(g.X + g.AdvanceX, g.Y + g.AdvanceY)));
            ref readonly NativeTextShapingGlyph logical = ref v.LogicalGlyphs[(int)g.GlyphIndex];
            ref readonly NativeHintedGlyphRunSlice slice = ref v.RunSlices[(int)owner.RunIndex];
            Require(g.GlyphId == logical.GlyphId && g.Cluster == logical.Cluster &&
                g.FontIndex == v.Runs[(int)owner.RunIndex].FontIndex &&
                v.ClusterEnds[i] == v.LogicalClusterEnds[(int)g.GlyphIndex] && v.BidiLevels[i] is >= 0 and <= 125 &&
                v.PositionedOutlineIndices[i] == v.SourceOutlineIndices[(int)(slice.SourceStart + owner.DescriptorIndex)]);
        }
        uint covered = 0;
        for (int i = 0; i < v.Lines.Length; i++)
        {
            ref readonly NativePositionedTextLine line = ref v.Lines[i];
            Require(line.GlyphStart == covered && line.GlyphCount > 0 && Range(covered, line.GlyphCount, v.Glyphs.Length) &&
                line.InputStart >= 0 && line.InputEnd >= line.InputStart && Extent(0, line.BaselineY, line.Width, line.Height) &&
                float.IsFinite(v.LineOrigins[i]) && line.Clipped <= 1 && (line.Reserved0 & ~1U) == 0 && line.Reserved1 == 0 && line.Reserved2 == 0);
            uint first = uint.MaxValue, last = 0;
            for (uint j = 0; j < line.GlyphCount; j++)
            {
                uint logical = v.Glyphs[(int)(covered + j)].GlyphIndex;
                first = Math.Min(first, logical); last = Math.Max(last, logical);
            }
            int inputEnd = last + 1 < c.LogicalGlyphCount ? v.LogicalGlyphs[(int)last + 1].Cluster :
                checked(v.LogicalGlyphs[(int)last].Cluster + 1);
            Require(line.InputStart == v.LogicalGlyphs[(int)first].Cluster && line.InputEnd == inputEnd);
            covered += line.GlyphCount;
        }
        Require(covered == c.PositionedGlyphCount);
        foreach (ref readonly NativeTextClusterBox b in v.Boxes)
            Require(b.InputStart >= 0 && b.InputEnd > b.InputStart && b.LineIndex < c.LineCount && b.BidiLevel is >= 0 and <= 125 &&
                b.Reserved0 == 0 && b.Reserved1 == 0 && b.Reserved2 == 0 && Extent(b.X, b.Y, b.Width, b.Height));
        foreach (ref readonly NativeTextCaretStop caret in v.Carets)
            Require(caret.InputPosition >= 0 && caret.LineIndex < c.LineCount && caret.BidiLevel is >= 0 and <= 125 && caret.Trailing <= 1 &&
                caret.Reserved0 == 0 && caret.Reserved1 == 0 && Extent(caret.X, caret.Y, 0, caret.Height));
    }

    private static bool Inside(Vector2 p, in NativeGlyphOutline o) =>
        p.X >= o.Minimum.X && p.X <= o.Maximum.X && p.Y >= o.Minimum.Y && p.Y <= o.Maximum.Y;
}
