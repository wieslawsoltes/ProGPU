using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Microsoft.Graphics.Canvas;
using Windows.UI;

internal static class Win2DColorSimdBenchmark
{
    internal static void Run(string[] args)
    {
        int pixelCount = ReadPositive(args, "--pixels", 262_144);
        int warmupCount = ReadNonNegative(args, "--warmup", 40);
        int sampleCount = ReadPositive(args, "--samples", 60);
        int iterationsPerSample = ReadPositive(args, "--iterations", 40);
        bool scalar = Array.Exists(
            args,
            static value => string.Equals(
                value,
                "--scalar",
                StringComparison.OrdinalIgnoreCase));
        ProGpuCanvasCpuConversionMode mode = scalar
            ? ProGpuCanvasCpuConversionMode.ScalarReference
            : ProGpuCanvasCpuConversionMode.IntrinsicSimd;
        Color[] source = CreateSource(pixelCount);
        byte[] expected = new byte[checked(pixelCount * 4)];
        byte[] destination = new byte[expected.Length];
        CanvasColorBgraConverter.Convert(
            source,
            expected,
            ProGpuCanvasCpuConversionMode.ScalarReference);
        ProGpuCanvasCpuConversionPath path =
            CanvasColorBgraConverter.Convert(source, destination, mode);
        if (!expected.AsSpan().SequenceEqual(destination))
        {
            throw new InvalidOperationException(
                "Win2D Color intrinsic-SIMD output differs from the scalar oracle.");
        }

        for (int index = 0; index < warmupCount; index++)
        {
            path = CanvasColorBgraConverter.Convert(
                source,
                destination,
                mode);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var samples = new double[sampleCount];
        int checksum = 0;
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int sample = 0; sample < sampleCount; sample++)
        {
            double elapsedMicroseconds = 0D;
            for (int iteration = 0;
                 iteration < iterationsPerSample;
                 iteration++)
            {
                long start = Stopwatch.GetTimestamp();
                path = CanvasColorBgraConverter.Convert(
                    source,
                    destination,
                    mode);
                elapsedMicroseconds +=
                    Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                checksum ^= destination[
                    (sample + iteration) % destination.Length];
            }

            samples[sample] =
                elapsedMicroseconds / iterationsPerSample;
        }

        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Array.Sort(samples);
        int measuredBlocks = checked(sampleCount * iterationsPerSample);
        FormattableString environment = $"Win2D Color CPU benchmark: runtime={RuntimeInformation.FrameworkDescription}, os={RuntimeInformation.OSDescription}, arch={RuntimeInformation.ProcessArchitecture}, vector128={Vector128.IsHardwareAccelerated}, avx2={Avx2.IsSupported}.";
        FormattableString workload = $"Workload: {pixelCount} ARGB Color pixels to BGRA8, {warmupCount} warmups, {sampleCount} samples x {iterationsPerSample} blocks.";
        FormattableString summary = $"{path}: p50={Percentile(samples, 0.50):F3} us/block, p95={Percentile(samples, 0.95):F3} us/block, p99={Percentile(samples, 0.99):F3} us/block, allocated={(double)allocatedBytes / measuredBlocks:F1} B/block, checksum={checksum}.";
        Console.WriteLine(FormattableString.Invariant(environment));
        Console.WriteLine(FormattableString.Invariant(workload));
        Console.WriteLine(FormattableString.Invariant(summary));
    }

    private static Color[] CreateSource(int pixelCount)
    {
        var source = new Color[pixelCount];
        uint state = 0x9E37_79B9U;
        for (int index = 0; index < source.Length; index++)
        {
            state = unchecked(state * 1_664_525U + 1_013_904_223U);
            source[index] = Color.FromArgb(
                (byte)(state >> 24),
                (byte)(state >> 16),
                (byte)(state >> 8),
                (byte)state);
        }

        return source;
    }

    private static int ReadPositive(
        string[] args,
        string name,
        int fallback)
    {
        int value = ReadInteger(args, name, fallback);
        return value > 0
            ? value
            : throw new ArgumentOutOfRangeException(name);
    }

    private static int ReadNonNegative(
        string[] args,
        string name,
        int fallback)
    {
        int value = ReadInteger(args, name, fallback);
        return value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(name);
    }

    private static int ReadInteger(
        string[] args,
        string name,
        int fallback)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(
                    args[index],
                    name,
                    StringComparison.OrdinalIgnoreCase) &&
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

    private static double Percentile(
        IReadOnlyList<double> sorted,
        double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
