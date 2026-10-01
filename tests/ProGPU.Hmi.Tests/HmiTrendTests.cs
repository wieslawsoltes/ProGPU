using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiTrendTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static HmiTagSample Sample(double seconds, double value, HmiQuality quality = HmiQuality.Good) => new(HmiValue.From(value), quality, Start.AddSeconds(seconds));

    [Fact]
    public void IrregularSamplesKeepTheirSourceTimeSpacing()
    {
        var buckets = new HmiTrendBucket[10];
        HmiTrendReducer.Reduce(new[] { Sample(0, 1), Sample(1, 2), Sample(9, 3) }, Start, Start.AddSeconds(10), buckets);
        Assert.Equal(0d, buckets[0].FirstX);
        Assert.Equal(0.1, buckets[1].FirstX, 12);
        Assert.Equal(0.9, buckets[9].FirstX, 12);
        Assert.Equal(3, buckets.Sum(b => b.Count));
    }

    [Fact]
    public void ReductionPreservesSpikesWithinAPixelBucket()
    {
        var buckets = new HmiTrendBucket[1];
        HmiTrendReducer.Reduce(new[] { Sample(0, 2), Sample(0.1, 99), Sample(0.2, -20), Sample(0.3, 3) }, Start, Start.AddSeconds(1), buckets);
        Assert.Equal(-20d, buckets[0].Minimum); Assert.Equal(99d, buckets[0].Maximum);
        Assert.Equal(2d, buckets[0].First); Assert.Equal(3d, buckets[0].Last);
        Assert.False(buckets[0].ContainsGap);
    }

    [Fact]
    public void InvalidTelemetryCreatesABreakNotAnInterpolatedNormalValue()
    {
        var buckets = new HmiTrendBucket[10];
        HmiTrendReducer.Reduce(new[] { Sample(0, 1), Sample(2, 999, HmiQuality.Bad), Sample(3, 4) }, Start, Start.AddSeconds(10), buckets);
        Assert.True(buckets[2].ContainsGap);
        Assert.True(buckets[3].BreakBefore);
        Assert.Equal(4d, buckets[3].First);
    }

    [Fact]
    public void MissingTimeIntervalsCanSplitAContinuousQualitySeries()
    {
        var buckets = new HmiTrendBucket[10];
        HmiTrendReducer.Reduce(new[] { Sample(0, 1), Sample(9, 2) }, Start, Start.AddSeconds(10), buckets, TimeSpan.FromSeconds(5));
        Assert.True(buckets[9].BreakBefore);
        HmiTrendReducer.Reduce(new[] { Sample(0, 1), Sample(9, 2) }, Start, Start.AddSeconds(10), buckets, TimeSpan.Zero);
        Assert.False(buckets[9].BreakBefore);
    }

    [Fact]
    public void InvalidSampleInsideASingleBucketCannotBeBridged()
    {
        var buckets = new HmiTrendBucket[1];
        HmiTrendReducer.Reduce(new[] { Sample(0, 1), Sample(0.1, 2, HmiQuality.Stale), Sample(0.2, 3) }, Start, Start.AddSeconds(1), buckets);
        Assert.True(buckets[0].ContainsGap);
    }

    [Fact]
    public void WindowExcludesOldAndFutureSamplesAndRejectsOutOfOrderInput()
    {
        var buckets = new HmiTrendBucket[10];
        HmiTrendReducer.Reduce(new[] { Sample(-1, 20), Sample(0, 1), Sample(10, 2), Sample(11, 30) }, Start, Start.AddSeconds(10), buckets);
        Assert.Equal(2, buckets.Sum(b => b.Count));
        Assert.Throws<ArgumentException>(() => HmiTrendReducer.Reduce(new[] { Sample(2, 1), Sample(1, 2) }, Start, Start.AddSeconds(10), buckets));
    }

    [Fact]
    public void StaleQualityTransitionIsRetainedInRuntimeHistory()
    {
        var runtime = new HmiRuntime(HmiDemoProject.Create(), Start);
        runtime.AdvanceTime(Start.AddSeconds(6));
        Assert.Equal(HmiQuality.Stale, runtime.GetHistory("Tank.Level")[^1].Quality);
        runtime.AdvanceTime(Start.AddSeconds(7));
        Assert.Equal(2, runtime.GetHistory("Tank.Level").Count);
        runtime.Publish(new Dictionary<string, HmiTagSample> { ["Tank.Level"] = Sample(5, 50) }, Start.AddSeconds(7));
        var buckets = new HmiTrendBucket[10];
        HmiTrendReducer.Reduce(runtime.GetHistory("Tank.Level"), Start, Start.AddSeconds(10), buckets);
        Assert.True(buckets[0].ContainsGap);
        Assert.True(buckets[5].BreakBefore);
    }
}
