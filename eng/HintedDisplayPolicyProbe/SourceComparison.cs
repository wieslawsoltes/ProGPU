using System.Buffers;
using System.Text;
using System.Text.Json;

namespace HintedDisplayPolicyProbe;

// Diagnostic DTOs only, not a native wire layout or a new product capability.
// All values are copied from one original complete-paragraph generation.
internal readonly record struct NativeSourceScalar(uint CodePoint, int Start, int Length);
internal readonly record struct NativeSourceGlyph(int PositionedIndex, int LogicalIndex, uint GlyphId, uint FontIndex,
    int Start, int End, int BidiLevel, long Advance26_6, long OffsetX26_6, long OffsetY26_6,
    float PositionedAdvance, float X, float Y);
internal readonly record struct NativeSourceLine(int Start, int End, int GlyphStart, int GlyphCount,
    float Width, float Height, float Baseline);
internal sealed record NativeSourceParagraph(string FontSha256, uint FaceIndex,
    NativeSourceScalar[] Scalars, NativeSourceGlyph[] Glyphs, NativeSourceLine[] Lines);

internal sealed record SourceAdvanceComparison(int SourceGlyphIndex, int PositionedIndex, int LogicalIndex,
    uint GlyphId, int SourceStart, int SourceEnd, int BidiLevel, double SourceAdvance,
    long NativeAdvance26_6, double NativeAdvanceDip, double AdvanceDeltaDip, bool ExactLogicalAdvance,
    float NativePositionedAdvance, double PositionedAdvanceDeltaDip, bool ExactPositionedAdvance,
    double[] OriginalSourceOffset, long[] NativeLogicalOffset26_6, double[] NativeLogicalOffsetDip,
    float NativePositionedX, float NativePositionedY,
    int SourceGlyphRunBidiLevel, int NativeProjectedSourceGlyphRunBidiLevel);
internal sealed record SourceRunComparison(int SourceLineIndex, int SourceRunIndex, int SourceStart, int SourceEnd,
    string Status, string[] Reasons, SourceAdvanceComparison[] Occurrences,
    int SourceGlyphRunBidiLevel, int? NativeBidiLevel, int? NativeProjectedSourceGlyphRunBidiLevel);
internal sealed record SourceLineComparison(int SourceLineIndex, int SourceStart, int OriginalLength,
    int SourceEnd, int VirtualEndOfParagraphLength, int? NativeLineIndex, string Status,
    double SourceWidth, double SourceWidthIncludingTrailingWhitespace, float? NativeWidth,
    bool? ExactWidthExcludingTrailingWhitespace, bool? ExactWidthIncludingTrailingWhitespace);
internal sealed record ParagraphSourceComparison(string Status, bool ExactFittingSourceRanges,
    int SourceRunCount, int AlignedRunCount, int SourceOccurrenceCount, int ComparedOccurrenceCount,
    int[] UnmatchedNativePositionedIndices, string[] Reasons, SourceLineComparison[] Lines, SourceRunComparison[] Runs,
    string OffsetFrameQualification);

internal static class SourceComparison
{
    internal static ParagraphSourceComparison Compare(JsonElement sourceCase, NativeSourceParagraph native)
    {
        string text = sourceCase.GetProperty("Text").GetString()!;
        double dpi = sourceCase.GetProperty("Dpi").GetDouble();
        // Do not accept a typed snapshot which has lost its original identity.
        bool sourceIdentity = MatchesSource(text, native.Scalars);
        bool fontIdentity = native.FontSha256 == ReferenceInput.FontHash && native.FaceIndex == 0;
        ValidateNative(text.Length, native);
        var reasons = new List<string>();
        if (!sourceIdentity) reasons.Add("native-original-UTF16-source-mismatch");
        if (!fontIdentity) reasons.Add("native-original-font-bytes-or-face-mismatch");
        var lines = new List<SourceLineComparison>();
        var runs = new List<SourceRunComparison>();
        var used = new HashSet<int>();
        JsonElement[] sourceLines = sourceCase.GetProperty("Lines").EnumerateArray().ToArray();
        int sourceLineIndex = 0, sourceOccurrenceCount = 0;
        foreach (var line in sourceLines)
        {
            int start = line.GetProperty("SourceStart").GetInt32();
            int rawLength = line.GetProperty("Length").GetInt32();
            int rawEnd = checked(start + rawLength);
            // The oracle's final TextEndOfParagraph has exactly one virtual
            // UTF-16 unit. Do not relabel arbitrary overshoot as source input.
            if (rawEnd > text.Length && (rawEnd != checked(text.Length + 1) || sourceLineIndex != sourceLines.Length - 1))
                throw new InvalidDataException("Only the original final row's single virtual end-of-paragraph unit is qualified.");
            int end = Math.Min(rawEnd, text.Length);
            int[] matches = native.Lines.Select((value, index) => (value, index))
                .Where(pair => pair.value.Start == start && pair.value.End == end).Select(pair => pair.index).ToArray();
            int? nativeLineIndex = matches.Length == 1 ? matches[0] : null;
            NativeSourceLine? nativeLine = nativeLineIndex.HasValue ? native.Lines[nativeLineIndex.Value] : null;
            double width = line.GetProperty("Width").GetDouble();
            double fullWidth = line.GetProperty("WidthIncludingTrailingWhitespace").GetDouble();
            lines.Add(new(sourceLineIndex, start, rawLength, end, rawEnd - end, nativeLineIndex,
                matches.Length == 1 ? "AlignedSourceRange" : matches.Length == 0 ? "DifferentFittingSourceRange" : "AmbiguousNativeLineRange",
                width, fullWidth, nativeLine?.Width, nativeLine.HasValue ? nativeLine.Value.Width == width : null,
                nativeLine.HasValue ? nativeLine.Value.Width == fullWidth : null));
            int sourceRunIndex = 0;
            foreach (var run in line.GetProperty("Runs").EnumerateArray())
            {
                int runStart = run.GetProperty("TextSourceCharacterIndex").GetInt32();
                int runEnd = checked(runStart + run.GetProperty("TextSourceLength").GetInt32());
                uint[] ids = run.GetProperty("GlyphIds").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
                sourceOccurrenceCount += ids.Length;
                var failures = new List<string>();
                if (!sourceIdentity) failures.Add("native-original-UTF16-source-mismatch");
                if (!fontIdentity) failures.Add("native-original-font-bytes-or-face-mismatch");
                if (!nativeLineIndex.HasValue) failures.Add("different-or-ambiguous-fitting-source-range");
                var candidates = native.Glyphs.Where(glyph => glyph.Start >= runStart && glyph.Start < runEnd)
                    .OrderBy(glyph => glyph.LogicalIndex).ToArray();
                if (native.Glyphs.Any(glyph => glyph.Start < runStart && glyph.End > runStart) || candidates.Any(glyph => glyph.End > runEnd))
                    failures.Add("native-cluster-crosses-original-run-boundary");
                if (candidates.Length != ids.Length) failures.Add("different-original-occurrence-count");
                if (candidates.Any(glyph => glyph.FontIndex != 0)) failures.Add("different-original-physical-font-owner");
                int level = run.GetProperty("BidiLevel").GetInt32();
                if (level is not (0 or 1)) failures.Add("unsupported-original-source-glyph-run-bidi-level");
                // WPF shaped GlyphRuns publish direction, while native snapshots
                // retain the full embedding level. Prove one exact native level
                // inside this original source run before projecting its direction.
                int? nativeLevel = candidates.Length == 0 ? null : candidates[0].BidiLevel;
                if (nativeLevel.HasValue && candidates.Any(glyph => glyph.BidiLevel != nativeLevel.Value))
                {
                    failures.Add("mixed-native-embedding-levels-within-original-source-run");
                    nativeLevel = null;
                }
                int? projectedLevel = nativeLevel.HasValue ? nativeLevel.Value & 1 : null;
                if (projectedLevel.HasValue && projectedLevel.Value != level)
                    failures.Add("different-original-run-bidi-level");
                if (nativeLine.HasValue && candidates.Any(glyph => glyph.PositionedIndex < nativeLine.Value.GlyphStart ||
                    glyph.PositionedIndex - nativeLine.Value.GlyphStart >= nativeLine.Value.GlyphCount))
                    failures.Add("different-original-occurrence-line-owner");
                int[] map = run.GetProperty("Clusters").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                var sourceClusters = ClusterOwners(runStart, runEnd, map, ids.Length);
                if (sourceClusters is null) failures.Add("unrepresentable-original-cluster-map");
                // Native logical ordering and source cluster glyph ranges are
                // compared as-is; matching IDs are never searched/reordered.
                if (sourceClusters is not null && candidates.Length == ids.Length)
                {
                    for (int i = 0; i < ids.Length; ++i)
                    {
                        if (candidates[i].GlyphId != ids[i]) { failures.Add("different-original-glyph-sequence"); break; }
                    }
                    for (int i = 0; i < ids.Length; ++i)
                    {
                        if (candidates[i].Start != sourceClusters[i].Start || candidates[i].End != sourceClusters[i].End)
                        { failures.Add("different-original-UTF16-cluster-coverage"); break; }
                    }
                }
                var occurrences = new List<SourceAdvanceComparison>();
                if (failures.Count == 0)
                {
                    double[] advances = run.GetProperty("Advances").EnumerateArray().Select(value => value.GetDouble()).ToArray();
                    var offsets = run.GetProperty("Offsets").EnumerateArray().ToArray();
                    for (int i = 0; i < candidates.Length; ++i)
                    {
                        var glyph = candidates[i];
                        if (!used.Add(glyph.PositionedIndex)) throw new InvalidDataException("Source runs overlap a native occurrence.");
                        double actual = ReferenceInput.DeviceToDip(glyph.Advance26_6, dpi);
                        occurrences.Add(new(i, glyph.PositionedIndex, glyph.LogicalIndex, glyph.GlyphId, glyph.Start, glyph.End,
                            glyph.BidiLevel, advances[i], glyph.Advance26_6, actual, actual - advances[i], actual == advances[i],
                            glyph.PositionedAdvance, (double)glyph.PositionedAdvance - advances[i], glyph.PositionedAdvance == advances[i],
                            offsets[i].EnumerateArray().Select(value => value.GetDouble()).ToArray(),
                            [glyph.OffsetX26_6, glyph.OffsetY26_6],
                            [ReferenceInput.DeviceToDip(glyph.OffsetX26_6, dpi), ReferenceInput.DeviceToDip(glyph.OffsetY26_6, dpi)], glyph.X, glyph.Y,
                            level, projectedLevel!.Value));
                    }
                }
                runs.Add(new(sourceLineIndex, sourceRunIndex++, runStart, runEnd, failures.Count != 0 ? "Unmatched" :
                    occurrences.All(value => value.ExactLogicalAdvance && value.ExactPositionedAdvance) ? "AlignedExactAdvances" : "AlignedAdvanceDifference",
                    failures.ToArray(), occurrences.ToArray(), level, nativeLevel, projectedLevel));
            }
            ++sourceLineIndex;
        }
        bool exactFitting = lines.Count == native.Lines.Length && lines.All(line => line.NativeLineIndex.HasValue) &&
            lines.Select(line => line.NativeLineIndex).Distinct().Count() == native.Lines.Length;
        if (!exactFitting) reasons.Add("different-original-line-fitting-source-ranges");
        int[] unmatched = native.Glyphs.Where(glyph => !used.Contains(glyph.PositionedIndex)).Select(glyph => glyph.PositionedIndex).ToArray();
        if (unmatched.Length != 0) reasons.Add("unmatched-native-positioned-occurrences");
        int aligned = runs.Count(run => run.Status != "Unmatched");
        return new(reasons.Count != 0 || aligned != runs.Count ? "ExplicitUnmatchedDomains" :
            runs.Any(run => run.Status == "AlignedAdvanceDifference") ? "AlignedAdvanceDifferences" : "AlignedExactAdvances",
            exactFitting, runs.Count, aligned, sourceOccurrenceCount, runs.Sum(run => run.Occurrences.Length), unmatched,
            reasons.ToArray(), lines.ToArray(), runs.ToArray(),
            "Original GlyphRun offsets and native signed logical offsets/positions are retained separately; their frames are not equated. No baseline/height/caret/pixel or Display admission follows.");
    }

    private static (int Start, int End)[]? ClusterOwners(int start, int end, int[] map, int glyphCount)
    {
        if (map.Length != end - start) return null;
        var blocks = new List<(int GlyphStart, int Start, int End)>();
        for (int i = 0; i < map.Length;)
        {
            int next = i + 1;
            while (next < map.Length && map[next] == map[i]) ++next;
            if (map[i] < 0 || map[i] >= glyphCount || blocks.Any(block => block.GlyphStart == map[i])) return null;
            blocks.Add((map[i], start + i, start + next)); i = next;
        }
        var ordered = blocks.OrderBy(block => block.GlyphStart).ToArray();
        if (ordered.Length == 0 || ordered[0].GlyphStart != 0) return null;
        var result = new (int Start, int End)[glyphCount];
        for (int i = 0; i < ordered.Length; ++i)
        {
            int next = i + 1 < ordered.Length ? ordered[i + 1].GlyphStart : glyphCount;
            for (int glyph = ordered[i].GlyphStart; glyph < next; ++glyph) result[glyph] = (ordered[i].Start, ordered[i].End);
        }
        return result;
    }

    private static bool MatchesSource(string text, NativeSourceScalar[] scalars)
    {
        int offset = 0;
        foreach (var scalar in scalars)
        {
            if (scalar.Start != offset || offset >= text.Length || Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out int length) != OperationStatus.Done ||
                scalar.Length != length || scalar.CodePoint != (uint)rune.Value) return false;
            offset += length;
        }
        return offset == text.Length;
    }

    private static void ValidateNative(int sourceLength, NativeSourceParagraph native)
    {
        var logicalIndices = new HashSet<int>();
        for (int i = 0; i < native.Glyphs.Length; ++i)
        {
            var glyph = native.Glyphs[i];
            if (glyph.PositionedIndex != i || glyph.LogicalIndex < 0 || !logicalIndices.Add(glyph.LogicalIndex) ||
                glyph.Start < 0 || glyph.Start >= glyph.End || glyph.End > sourceLength || glyph.BidiLevel < 0 ||
                !float.IsFinite(glyph.PositionedAdvance) || !float.IsFinite(glyph.X) || !float.IsFinite(glyph.Y))
                throw new InvalidDataException("Invalid retained native occurrence metadata.");
        }
        int next = 0;
        foreach (var line in native.Lines)
        {
            if (line.Start < 0 || line.End < line.Start || line.End > sourceLength || line.GlyphStart != next ||
                line.GlyphCount < 0 || line.GlyphCount > native.Glyphs.Length - next || !float.IsFinite(line.Width) ||
                !float.IsFinite(line.Height) || !float.IsFinite(line.Baseline)) throw new InvalidDataException("Invalid retained native line metadata.");
            next += line.GlyphCount;
        }
        if (next != native.Glyphs.Length) throw new InvalidDataException("Native lines do not cover the complete retained generation.");
    }
}
