namespace ProGPU.Hmi;

/// <summary>A time bucket, not an evenly spaced sample. X coordinates are normalized source timestamps.</summary>
public readonly record struct HmiTrendBucket(
    int Count, double FirstX, double LastX, double First, double Last,
    double Minimum, double Maximum, bool BreakBefore, bool ContainsGap);

/// <summary>
/// Allocation-free time-domain min/max reduction into caller-owned storage.
/// A bucket containing invalid telemetry is marked as a gap instead of bridging it.
/// The caller retains chronological input ownership for the duration of this operation.
/// </summary>
public static class HmiTrendReducer
{
    public static void Reduce(IReadOnlyList<HmiTagSample> samples, DateTimeOffset start, DateTimeOffset end,
        Span<HmiTrendBucket> buckets, TimeSpan? maximumGap = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        if (buckets.Length is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(buckets));
        if (maximumGap < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumGap));
        buckets.Clear();
        double duration = (end - start).TotalSeconds;
        DateTimeOffset? previousTimestamp = null;
        DateTimeOffset? previousGood = null;
        bool pendingBreak = true;
        int previousBucket = -1;
        for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            var sample = samples[sampleIndex];
            if (previousTimestamp is { } previous && sample.Timestamp < previous)
                throw new ArgumentException("Trend samples must be ordered by source timestamp.", nameof(samples));
            previousTimestamp = sample.Timestamp;
            if (sample.Timestamp < start || sample.Timestamp > end) continue;
            double x = (sample.Timestamp - start).TotalSeconds / duration;
            int index = Math.Min(buckets.Length - 1, (int)(x * buckets.Length));
            ref var bucket = ref buckets[index];
            if (sample.Quality != HmiQuality.Good || sample.Value.Type != HmiTagType.Number || !double.IsFinite(sample.Value.Number))
            {
                bucket = bucket with { ContainsGap = true };
                previousGood = null;
                pendingBreak = true;
                previousBucket = index;
                continue;
            }
            bool timeGap = maximumGap is { } gap && gap > TimeSpan.Zero && previousGood is { } last && sample.Timestamp - last > gap;
            double value = sample.Value.Number;
            if (bucket.Count == 0)
            {
                bucket = new(1, x, x, value, value, value, value,
                    pendingBreak || timeGap, bucket.ContainsGap);
            }
            else
            {
                bucket = bucket with
                {
                    Count = bucket.Count + 1, LastX = x, Last = value,
                    Minimum = Math.Min(bucket.Minimum, value), Maximum = Math.Max(bucket.Maximum, value),
                    ContainsGap = bucket.ContainsGap || timeGap || pendingBreak && previousBucket == index
                };
            }
            previousGood = sample.Timestamp;
            previousBucket = index;
            pendingBreak = bucket.ContainsGap;
        }
    }
}
