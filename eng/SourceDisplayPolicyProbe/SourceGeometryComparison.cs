using System.Text.Json;
using HintedDisplayPolicyProbe;

namespace SourceDisplayPolicyProbe;

// Diagnostic values copied from one native owner. Never raster-float promotion.
internal readonly record struct SourceRunMetric(int SourceLineIndex, int SourceRunIndex,
    int PositionedIndex, double Advance, double OffsetX, double OffsetY);
internal readonly record struct SourceLineMetric(double Width, double Top, double Height, double BaselineOffset, double BaselineY, double OriginX);
internal sealed record GeometryOccurrence(int SourceGlyphIndex, int PositionedIndex, int LogicalIndex,
    double ExpectedAdvance, double ActualAdvance, double[] ExpectedOffset, double[] ActualOffset,
    bool ExactAdvance, bool ExactOffset);
internal sealed record GeometryRun(int SourceLineIndex, int SourceRunIndex, string Status, string[] Reasons, GeometryOccurrence[] Occurrences);
internal sealed record GeometryLine(int SourceLineIndex, int? NativeLineIndex, bool? ExactFullWidth,
    bool? ExactHeight, bool? ExactBaseline, bool? ExactOrigin, SourceLineMetric? Native);
internal sealed record GeometryComparison(string Status, int OriginalOccurrences, int ComparedOccurrences,
    GeometryLine[] Lines, GeometryRun[] Runs, ParagraphSourceComparison OriginalIdentityAlignment);

internal static class SourceGeometryComparison
{
    internal static GeometryComparison Compare(JsonElement original, ParagraphSourceComparison alignment,
        SourceLineMetric[] nativeLines, SourceRunMetric[] nativeMetrics)
    {
        var metricKeys = new HashSet<(int, int, int)>();
        foreach (var value in nativeMetrics)
            if (!metricKeys.Add((value.SourceLineIndex, value.SourceRunIndex, value.PositionedIndex)) ||
                !double.IsFinite(value.Advance) || !double.IsFinite(value.OffsetX) || !double.IsFinite(value.OffsetY))
                throw new InvalidDataException("Duplicated or nonfinite original native run metrics.");
        foreach (var line in nativeLines)
            if (!double.IsFinite(line.Width) || !double.IsFinite(line.Top) || !double.IsFinite(line.Height) ||
                !double.IsFinite(line.BaselineOffset) || !double.IsFinite(line.BaselineY) || !double.IsFinite(line.OriginX))
                throw new InvalidDataException("Nonfinite original native line metrics.");
        var lines = new List<GeometryLine>(); var runs = new List<GeometryRun>();
        var sourceLines = original.GetProperty("Lines").EnumerateArray().ToArray();
        foreach (var line in alignment.Lines)
        {
            var expected = sourceLines[line.SourceLineIndex];
            SourceLineMetric? actual = line.NativeLineIndex is { } index ? nativeLines[index] : null;
            lines.Add(new(line.SourceLineIndex, line.NativeLineIndex,
                actual.HasValue ? actual.Value.Width == expected.GetProperty("WidthIncludingTrailingWhitespace").GetDouble() : null,
                actual.HasValue ? actual.Value.Height == expected.GetProperty("Height").GetDouble() : null,
                actual.HasValue ? actual.Value.BaselineOffset == expected.GetProperty("Baseline").GetDouble() : null,
                actual.HasValue ? actual.Value.OriginX == expected.GetProperty("Start").GetDouble() : null, actual));
        }
        var used = new HashSet<(int, int, int)>();
        foreach (var run in alignment.Runs)
        {
            var values = new List<GeometryOccurrence>(); var failures = run.Reasons.ToList();
            foreach (var occurrence in run.Occurrences)
            {
                var key = (run.SourceLineIndex, run.SourceRunIndex, occurrence.PositionedIndex);
                var selected = nativeMetrics.Where(value => (value.SourceLineIndex, value.SourceRunIndex, value.PositionedIndex) == key).ToArray();
                if (selected.Length != 1) { failures.Add("missing-original-native-source-run-metric"); continue; }
                used.Add(key); var metric = selected[0];
                var offsets = occurrence.OriginalSourceOffset;
                values.Add(new(occurrence.SourceGlyphIndex, occurrence.PositionedIndex, occurrence.LogicalIndex,
                    occurrence.SourceAdvance, metric.Advance, offsets, [metric.OffsetX, metric.OffsetY],
                    metric.Advance == occurrence.SourceAdvance, metric.OffsetX == offsets[0] && metric.OffsetY == offsets[1]));
            }
            runs.Add(new(run.SourceLineIndex, run.SourceRunIndex, failures.Count != 0 ? "Unmatched" :
                values.All(value => value.ExactAdvance && value.ExactOffset) ? "ExactSourceRunMetrics" : "SourceRunMetricDifferences",
                failures.ToArray(), values.ToArray()));
        }
        if (used.Count != nativeMetrics.Length) throw new InvalidDataException("Source metrics contain unowned or unmatched occurrence tails.");
        bool unmatched = !alignment.ExactFittingSourceRanges || alignment.UnmatchedNativePositionedIndices.Length != 0 || runs.Any(run => run.Status == "Unmatched");
        bool exact = !unmatched && runs.All(run => run.Status == "ExactSourceRunMetrics") &&
            lines.All(line => line.ExactFullWidth == true && line.ExactHeight == true && line.ExactBaseline == true && line.ExactOrigin == true);
        return new(unmatched ? "ExplicitUnmatchedDomains" : exact ? "ExactComparedSourceGeometry" : "SourceGeometryDifferences",
            alignment.SourceOccurrenceCount, runs.Sum(run => run.Occurrences.Length), lines.ToArray(), runs.ToArray(), alignment);
    }
}
