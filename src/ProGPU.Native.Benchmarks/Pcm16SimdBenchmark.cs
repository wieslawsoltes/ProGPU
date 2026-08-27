using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using ProGPU.Media.Audio;

internal static class Pcm16SimdBenchmark
{
    private static readonly MediaAudioStereoLevels Levels =
        new(0.625F, 1.75F);

    internal static void Run(string[] args)
    {
        int frames = ReadPositive(args, "--frames", 48_000);
        int warmupCount = ReadNonNegative(args, "--warmup", 20);
        int sampleCount = ReadPositive(args, "--samples", 30);
        int iterationsPerSample = ReadPositive(args, "--iterations", 20);
        bool measureScalar = Array.Exists(
            args,
            static value => string.Equals(
                value,
                "--scalar",
                StringComparison.OrdinalIgnoreCase));
        short[] source = CreateSource(checked(frames * 2));
        short[] expected = source.ToArray();
        short[] actual = source.ToArray();
        int expectedOffset = 0;
        int actualOffset = 0;
        ApplyScalar(expected, Levels, ref expectedOffset);
        MediaPcm16StereoProcessor.ApplyStereo(
            actual,
            channelCount: 2,
            Levels,
            ref actualOffset);
        if (expectedOffset != actualOffset ||
            !expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                "Intrinsic-SIMD PCM16 output differs from the scalar oracle.");
        }

        short[] working = new short[source.Length];
        for (int index = 0; index < warmupCount; index++)
        {
            source.CopyTo(working, 0);
            int channelOffset = 0;
            ApplyMeasured(
                working,
                measureScalar,
                ref channelOffset);
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
                source.CopyTo(working, 0);
                int channelOffset = 0;
                long start = Stopwatch.GetTimestamp();
                ApplyMeasured(
                    working,
                    measureScalar,
                    ref channelOffset);
                elapsedMicroseconds +=
                    Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                checksum ^= working[
                    (sample + iteration) % working.Length];
                checksum ^= channelOffset;
            }

            samples[sample] =
                elapsedMicroseconds / iterationsPerSample;
        }

        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Array.Sort(samples);
        int measuredBlocks = checked(
            sampleCount * iterationsPerSample);
        string path = measureScalar
            ? "Scalar oracle"
            : "Intrinsic-SIMD";
        FormattableString environment = $"PCM16 CPU benchmark: runtime={RuntimeInformation.FrameworkDescription}, os={RuntimeInformation.OSDescription}, arch={RuntimeInformation.ProcessArchitecture}, vector128={Vector128.IsHardwareAccelerated}, vector256={Vector256.IsHardwareAccelerated}, vector512={Vector512.IsHardwareAccelerated}.";
        FormattableString workload = $"Workload: {frames} stereo frames/block, levels=({Levels.Left:F3}, {Levels.Right:F3}), {warmupCount} warmups, {sampleCount} samples x {iterationsPerSample} blocks.";
        FormattableString summary = $"{path}: p50={Percentile(samples, 0.50):F3} us/block, p95={Percentile(samples, 0.95):F3} us/block, p99={Percentile(samples, 0.99):F3} us/block, allocated={(double)allocatedBytes / measuredBlocks:F1} B/block, checksum={checksum}.";
        Console.WriteLine(FormattableString.Invariant(environment));
        Console.WriteLine(FormattableString.Invariant(workload));
        Console.WriteLine(FormattableString.Invariant(summary));
    }

    private static void ApplyMeasured(
        Span<short> samples,
        bool scalar,
        ref int channelOffset)
    {
        if (scalar)
        {
            ApplyScalar(samples, Levels, ref channelOffset);
            return;
        }

        MediaPcm16StereoProcessor.ApplyStereo(
            samples,
            channelCount: 2,
            Levels,
            ref channelOffset);
    }

    private static void ApplyScalar(
        Span<short> samples,
        in MediaAudioStereoLevels levels,
        ref int channelOffset)
    {
        int left = Quantize(levels.Left);
        int right = Quantize(levels.Right);
        int channel = channelOffset;
        for (int index = 0; index < samples.Length; index++)
        {
            int fixedLevel = channel == 0 ? left : right;
            int scaled = samples[index] * fixedLevel / 32_768;
            samples[index] = (short)Math.Clamp(
                scaled,
                short.MinValue,
                short.MaxValue);
            channel ^= 1;
        }

        channelOffset = channel;
    }

    private static int Quantize(float level) =>
        (int)Math.Round(
            level * 32_768D,
            MidpointRounding.AwayFromZero);

    private static short[] CreateSource(int sampleCount)
    {
        var source = new short[sampleCount];
        var random = new Random(0x50_43_4D);
        random.NextBytes(MemoryMarshal.AsBytes(source.AsSpan()));
        source[0] = short.MinValue;
        source[^1] = short.MaxValue;
        return source;
    }

    private static double Percentile(
        double[] sortedSamples,
        double percentile)
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
}
