using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using ProGPU.Text;

internal static class GlyphCpuFallbackBenchmark
{
    private const uint Width = 64U;
    private const uint Height = 64U;

    internal static void Run(string[] args)
    {
        int warmupCount = ReadNonNegative(args, "--warmup", 200);
        int sampleCount = ReadPositive(args, "--samples", 40);
        int iterationsPerSample = ReadPositive(args, "--iterations", 50);
        bool measureSimd = !Array.Exists(
            args,
            static value => string.Equals(
                value,
                "--scalar",
                StringComparison.OrdinalIgnoreCase));
        GpuSegment[] segments = CreateRepresentativeGlyph();
        var record = new GpuGlyphRecord
        {
            StartSegment = 0U,
            SegmentCount = (uint)segments.Length,
            MinX = 0F,
            MinY = 0F,
            MaxX = 52F,
            MaxY = 52F
        };

        byte[] scalar = Rasterize(segments, record, useSimd: false);
        byte[] simd = Rasterize(segments, record, useSimd: true);
        if (!scalar.AsSpan().SequenceEqual(simd))
        {
            throw new InvalidOperationException(
                "Intrinsic-SIMD glyph coverage differs from the scalar oracle.");
        }

        int checksum = 0;
        for (int index = 0; index < warmupCount; index++)
        {
            checksum ^= Rasterize(segments, record, measureSimd)[index % scalar.Length];
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var samples = new double[sampleCount];
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int sample = 0; sample < sampleCount; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int iteration = 0; iteration < iterationsPerSample; iteration++)
            {
                byte[] result = Rasterize(segments, record, measureSimd);
                checksum ^= result[(sample + iteration) % result.Length];
            }

            samples[sample] =
                Stopwatch.GetElapsedTime(start).TotalMicroseconds /
                iterationsPerSample;
        }

        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Array.Sort(samples);
        int measuredGlyphs = checked(sampleCount * iterationsPerSample);

        FormattableString environment = $"Glyph CPU fallback benchmark: runtime={RuntimeInformation.FrameworkDescription}, os={RuntimeInformation.OSDescription}, arch={RuntimeInformation.ProcessArchitecture}, vector128={Vector128.IsHardwareAccelerated}, vector256={Vector256.IsHardwareAccelerated}, vector512={Vector512.IsHardwareAccelerated}.";
        FormattableString workload = $"Workload: {Width}x{Height}, {segments.Length} segments, {warmupCount} warmups, {sampleCount} samples x {iterationsPerSample} glyphs.";
        string path = measureSimd ? "Intrinsic-SIMD" : "Scalar oracle";
        FormattableString summary = $"{path} glyph coverage: p50={Percentile(samples, 0.50):F3} us/glyph, p95={Percentile(samples, 0.95):F3} us/glyph, p99={Percentile(samples, 0.99):F3} us/glyph, allocated={(double)allocatedBytes / measuredGlyphs:F1} B/glyph, checksum={checksum}.";
        Console.WriteLine(FormattableString.Invariant(environment));
        Console.WriteLine(FormattableString.Invariant(workload));
        Console.WriteLine(FormattableString.Invariant(summary));
    }

    private static byte[] Rasterize(
        ReadOnlySpan<GpuSegment> segments,
        GpuGlyphRecord record,
        bool useSimd) =>
        GlyphAtlas.RasterizeGlyphCoverageCpu(
            segments,
            record,
            xStart: -6,
            yStart: -58,
            scale: 1.125F,
            subpixelX: 0.375F,
            Width,
            Height,
            useSimd);

    private static double Percentile(double[] sortedSamples, double percentile)
    {
        int index = Math.Clamp(
            (int)Math.Ceiling(percentile * sortedSamples.Length) - 1,
            0,
            sortedSamples.Length - 1);
        return sortedSamples[index];
    }

    private static int ReadPositive(
        string[] args,
        string name,
        int fallback)
    {
        int value = ReadInteger(args, name, fallback);
        return value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                name,
                value,
                "The benchmark value must be positive.");
    }

    private static int ReadNonNegative(
        string[] args,
        string name,
        int fallback)
    {
        int value = ReadInteger(args, name, fallback);
        return value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(
                name,
                value,
                "The benchmark value must be non-negative.");
    }

    private static int ReadInteger(
        string[] args,
        string name,
        int fallback)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(
                    args[index + 1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static GpuSegment[] CreateRepresentativeGlyph() =>
    [
        Line(new(0F, 0F), new(16F, 0F)),
        Quadratic(new(16F, 0F), new(25F, 5F), new(22F, 18F)),
        Cubic(new(22F, 18F), new(19F, 29F), new(3F, 28F), new(0F, 0F)),

        Line(new(28F, 4F), new(46F, 4F)),
        Cubic(new(46F, 4F), new(55F, 12F), new(51F, 29F), new(39F, 31F)),
        Quadratic(new(39F, 31F), new(27F, 28F), new(28F, 4F)),

        Line(new(5F, 34F), new(20F, 34F)),
        Quadratic(new(20F, 34F), new(28F, 42F), new(18F, 52F)),
        Cubic(new(18F, 52F), new(7F, 55F), new(-2F, 47F), new(5F, 34F)),

        Line(new(29F, 36F), new(49F, 36F)),
        Quadratic(new(49F, 36F), new(57F, 45F), new(46F, 52F)),
        Cubic(new(46F, 52F), new(34F, 56F), new(25F, 46F), new(29F, 36F))
    ];

    private static GpuSegment Line(Vector2 start, Vector2 end) => new()
    {
        P0 = start,
        P1 = end,
        SegmentType = 0U
    };

    private static GpuSegment Quadratic(
        Vector2 start,
        Vector2 control,
        Vector2 end) => new()
    {
        P0 = start,
        P1 = control,
        P2 = end,
        SegmentType = 1U
    };

    private static GpuSegment Cubic(
        Vector2 start,
        Vector2 control1,
        Vector2 control2,
        Vector2 end) => new()
    {
        P0 = start,
        P1 = control1,
        P2 = control2,
        P3 = end,
        SegmentType = 2U
    };
}
