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
        if (Array.Exists(
                args,
                static value => string.Equals(
                    value,
                    "--convert",
                    StringComparison.OrdinalIgnoreCase)))
        {
            RunConversion(args);
            return;
        }

        if (Array.Exists(
                args,
                static value => string.Equals(
                    value,
                    "--processed",
                    StringComparison.OrdinalIgnoreCase)))
        {
            RunProcessed(args);
            return;
        }

        if (Array.Exists(
                args,
                static value => string.Equals(
                    value,
                    "--wide",
                    StringComparison.OrdinalIgnoreCase)))
        {
            RunWide(args);
            return;
        }

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

    private static void RunConversion(string[] args)
    {
        int frames = ReadPositive(args, "--frames", 1_024);
        int warmupCount = ReadNonNegative(args, "--warmup", 60);
        int sampleCount = ReadPositive(args, "--samples", 80);
        int iterationsPerSample = ReadPositive(args, "--iterations", 200);
        bool measureScalar = Array.Exists(
            args,
            static value => string.Equals(
                value,
                "--scalar",
                StringComparison.OrdinalIgnoreCase));
        short[] source = CreateSource(checked(frames * 2));
        float[] expected = new float[source.Length];
        float[] actual = new float[source.Length];
        ConvertScalar(source, expected);
        MediaPcm16FloatConverter.ConvertToNormalizedFloat(
            source,
            actual);
        for (int index = 0; index < expected.Length; index++)
        {
            if (BitConverter.SingleToInt32Bits(expected[index]) !=
                BitConverter.SingleToInt32Bits(actual[index]))
            {
                throw new InvalidOperationException(
                    "Intrinsic-SIMD PCM16 float conversion differs from the scalar oracle.");
            }
        }

        float[] destination = new float[source.Length];
        for (int index = 0; index < warmupCount; index++)
        {
            ApplyConversionMeasured(
                source,
                destination,
                measureScalar);
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
                ApplyConversionMeasured(
                    source,
                    destination,
                    measureScalar);
                elapsedMicroseconds +=
                    Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                checksum ^= BitConverter.SingleToInt32Bits(
                    destination[
                        (sample + iteration) % destination.Length]);
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
        FormattableString environment = $"PCM16 float-conversion benchmark: runtime={RuntimeInformation.FrameworkDescription}, os={RuntimeInformation.OSDescription}, arch={RuntimeInformation.ProcessArchitecture}, vector128={Vector128.IsHardwareAccelerated}, vector256={Vector256.IsHardwareAccelerated}, vector512={Vector512.IsHardwareAccelerated}.";
        FormattableString workload = $"Workload: {frames} stereo PCM16 frames/block, normalized float output, {warmupCount} warmups, {sampleCount} samples x {iterationsPerSample} blocks.";
        FormattableString summary = $"{path}: p50={Percentile(samples, 0.50):F3} us/block, p95={Percentile(samples, 0.95):F3} us/block, p99={Percentile(samples, 0.99):F3} us/block, allocated={(double)allocatedBytes / measuredBlocks:F1} B/block, checksum={checksum}.";
        Console.WriteLine(FormattableString.Invariant(environment));
        Console.WriteLine(FormattableString.Invariant(workload));
        Console.WriteLine(FormattableString.Invariant(summary));
    }

    private static void ApplyConversionMeasured(
        ReadOnlySpan<short> source,
        Span<float> destination,
        bool scalar)
    {
        if (scalar)
        {
            ConvertScalar(source, destination);
            return;
        }

        MediaPcm16FloatConverter.ConvertToNormalizedFloat(
            source,
            destination);
    }

    private static void ConvertScalar(
        ReadOnlySpan<short> source,
        Span<float> destination)
    {
        for (int index = 0; index < source.Length; index++)
        {
            destination[index] = source[index] / 32_768F;
        }
    }

    private static void RunProcessed(string[] args)
    {
        const int leftLevel = 20_480;
        const int rightLevel = 57_344;
        const string nonFiniteMessage = "non-finite benchmark sample";
        int frames = ReadPositive(args, "--frames", 1_024);
        int warmupCount = ReadNonNegative(args, "--warmup", 60);
        int sampleCount = ReadPositive(args, "--samples", 80);
        int iterationsPerSample = ReadPositive(args, "--iterations", 200);
        bool measureScalar = Array.Exists(
            args,
            static value => string.Equals(
                value,
                "--scalar",
                StringComparison.OrdinalIgnoreCase));
        float[] source = CreateProcessedSource(checked(frames * 2));
        long[] initial = CreateAccumulator(source.Length);
        long[] expected = initial.ToArray();
        long[] actual = initial.ToArray();
        AddProcessedScalar(
            source,
            leftLevel,
            rightLevel,
            expected,
            nonFiniteMessage);
        MediaPcm16ProcessedAccumulator.AddStereo(
            source,
            leftLevel,
            rightLevel,
            actual,
            nonFiniteMessage);
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                "Intrinsic-SIMD processed PCM16 accumulation differs from the scalar oracle.");
        }

        long[] accumulator = new long[source.Length];
        for (int index = 0; index < warmupCount; index++)
        {
            initial.CopyTo(accumulator, 0);
            ApplyProcessedMeasured(
                source,
                accumulator,
                leftLevel,
                rightLevel,
                measureScalar,
                nonFiniteMessage);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var samples = new double[sampleCount];
        long checksum = 0;
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int sample = 0; sample < sampleCount; sample++)
        {
            double elapsedMicroseconds = 0D;
            for (int iteration = 0;
                 iteration < iterationsPerSample;
                 iteration++)
            {
                initial.CopyTo(accumulator, 0);
                long start = Stopwatch.GetTimestamp();
                ApplyProcessedMeasured(
                    source,
                    accumulator,
                    leftLevel,
                    rightLevel,
                    measureScalar,
                    nonFiniteMessage);
                elapsedMicroseconds +=
                    Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                checksum ^= accumulator[
                    (sample + iteration) % accumulator.Length];
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
        FormattableString environment = $"Processed PCM16 CPU benchmark: runtime={RuntimeInformation.FrameworkDescription}, os={RuntimeInformation.OSDescription}, arch={RuntimeInformation.ProcessArchitecture}, vector128={Vector128.IsHardwareAccelerated}, vector256={Vector256.IsHardwareAccelerated}, vector512={Vector512.IsHardwareAccelerated}.";
        FormattableString workload = $"Workload: {frames} stereo float frames/block, Q15 levels=({leftLevel}, {rightLevel}), saturating Int64 accumulate, {warmupCount} warmups, {sampleCount} samples x {iterationsPerSample} blocks.";
        FormattableString summary = $"{path}: p50={Percentile(samples, 0.50):F3} us/block, p95={Percentile(samples, 0.95):F3} us/block, p99={Percentile(samples, 0.99):F3} us/block, allocated={(double)allocatedBytes / measuredBlocks:F1} B/block, checksum={checksum}.";
        Console.WriteLine(FormattableString.Invariant(environment));
        Console.WriteLine(FormattableString.Invariant(workload));
        Console.WriteLine(FormattableString.Invariant(summary));
    }

    private static void ApplyProcessedMeasured(
        ReadOnlySpan<float> source,
        Span<long> accumulator,
        int leftLevel,
        int rightLevel,
        bool scalar,
        string nonFiniteMessage)
    {
        if (scalar)
        {
            AddProcessedScalar(
                source,
                leftLevel,
                rightLevel,
                accumulator,
                nonFiniteMessage);
            return;
        }

        MediaPcm16ProcessedAccumulator.AddStereo(
            source,
            leftLevel,
            rightLevel,
            accumulator,
            nonFiniteMessage);
    }

    private static void AddProcessedScalar(
        ReadOnlySpan<float> source,
        int leftLevel,
        int rightLevel,
        Span<long> accumulator,
        string nonFiniteMessage)
    {
        for (int index = 0; index < source.Length; index++)
        {
            float sample = source[index];
            if (!float.IsFinite(sample))
            {
                throw new InvalidDataException(nonFiniteMessage);
            }

            int level = (index & 1) == 0
                ? leftLevel
                : rightLevel;
            double scaled = (double)sample * level;
            long contribution =
                scaled >= long.MaxValue
                    ? long.MaxValue
                    : scaled <= long.MinValue
                        ? long.MinValue
                        : checked((long)Math.Round(
                            scaled,
                            MidpointRounding.AwayFromZero));
            long current = accumulator[index];
            if (contribution > 0 &&
                current > long.MaxValue - contribution)
            {
                accumulator[index] = long.MaxValue;
            }
            else if (contribution < 0 &&
                     current < long.MinValue - contribution)
            {
                accumulator[index] = long.MinValue;
            }
            else
            {
                accumulator[index] = current + contribution;
            }
        }
    }

    private static void RunWide(string[] args)
    {
        const int leftLevel = 20_480;
        const int rightLevel = 57_344;
        int frames = ReadPositive(args, "--frames", 1_024);
        int warmupCount = ReadNonNegative(args, "--warmup", 60);
        int sampleCount = ReadPositive(args, "--samples", 80);
        int iterationsPerSample = ReadPositive(args, "--iterations", 200);
        bool measureScalar = Array.Exists(
            args,
            static value => string.Equals(
                value,
                "--scalar",
                StringComparison.OrdinalIgnoreCase));
        short[] source = CreateSource(checked(frames * 2));
        long[] initial = CreateAccumulator(source.Length);
        long[] expectedAccumulator = initial.ToArray();
        long[] actualAccumulator = initial.ToArray();
        short[] expected = new short[source.Length];
        short[] actual = new short[source.Length];
        AddWideScalar(
            source,
            leftLevel,
            rightLevel,
            expectedAccumulator);
        SaturateScalar(expectedAccumulator, expected);
        MediaPcm16WideAccumulator.AddStereo(
            source,
            leftLevel,
            rightLevel,
            actualAccumulator);
        MediaPcm16WideAccumulator.WriteSaturated(
            actualAccumulator,
            actual);
        if (!expectedAccumulator.AsSpan().SequenceEqual(
                actualAccumulator) ||
            !expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                "Intrinsic-SIMD wide PCM16 mixing differs from the scalar oracle.");
        }

        long[] accumulator = new long[source.Length];
        short[] output = new short[source.Length];
        for (int index = 0; index < warmupCount; index++)
        {
            initial.CopyTo(accumulator, 0);
            ApplyWideMeasured(
                source,
                accumulator,
                output,
                leftLevel,
                rightLevel,
                measureScalar);
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
                initial.CopyTo(accumulator, 0);
                long start = Stopwatch.GetTimestamp();
                ApplyWideMeasured(
                    source,
                    accumulator,
                    output,
                    leftLevel,
                    rightLevel,
                    measureScalar);
                elapsedMicroseconds +=
                    Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                checksum ^= output[
                    (sample + iteration) % output.Length];
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
        FormattableString environment = $"Wide PCM16 CPU benchmark: runtime={RuntimeInformation.FrameworkDescription}, os={RuntimeInformation.OSDescription}, arch={RuntimeInformation.ProcessArchitecture}, vector128={Vector128.IsHardwareAccelerated}, vector256={Vector256.IsHardwareAccelerated}, vector512={Vector512.IsHardwareAccelerated}.";
        FormattableString workload = $"Workload: {frames} stereo frames/block, Q15 levels=({leftLevel}, {rightLevel}), accumulate+saturate, {warmupCount} warmups, {sampleCount} samples x {iterationsPerSample} blocks.";
        FormattableString summary = $"{path}: p50={Percentile(samples, 0.50):F3} us/block, p95={Percentile(samples, 0.95):F3} us/block, p99={Percentile(samples, 0.99):F3} us/block, allocated={(double)allocatedBytes / measuredBlocks:F1} B/block, checksum={checksum}.";
        Console.WriteLine(FormattableString.Invariant(environment));
        Console.WriteLine(FormattableString.Invariant(workload));
        Console.WriteLine(FormattableString.Invariant(summary));
    }

    private static void ApplyWideMeasured(
        ReadOnlySpan<short> source,
        Span<long> accumulator,
        Span<short> output,
        int leftLevel,
        int rightLevel,
        bool scalar)
    {
        if (scalar)
        {
            AddWideScalar(
                source,
                leftLevel,
                rightLevel,
                accumulator);
            SaturateScalar(accumulator, output);
            return;
        }

        MediaPcm16WideAccumulator.AddStereo(
            source,
            leftLevel,
            rightLevel,
            accumulator);
        MediaPcm16WideAccumulator.WriteSaturated(
            accumulator,
            output);
    }

    private static void AddWideScalar(
        ReadOnlySpan<short> source,
        int leftLevel,
        int rightLevel,
        Span<long> accumulator)
    {
        for (int index = 0; index < source.Length; index++)
        {
            int level = (index & 1) == 0
                ? leftLevel
                : rightLevel;
            accumulator[index] +=
                (long)source[index] * level / 32_768;
        }
    }

    private static void SaturateScalar(
        ReadOnlySpan<long> accumulator,
        Span<short> output)
    {
        for (int index = 0; index < accumulator.Length; index++)
        {
            output[index] =
                (short)Math.Clamp(
                    accumulator[index],
                    short.MinValue,
                    short.MaxValue);
        }
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

    private static float[] CreateProcessedSource(int sampleCount)
    {
        var source = new float[sampleCount];
        var random = new Random(0x50_52_43);
        for (int index = 0; index < source.Length; index++)
        {
            source[index] =
                (float)(random.NextDouble() * 4D - 2D);
        }
        source[0] = 0.5F / 20_480F;
        source[^1] = -0.5F / 57_344F;
        return source;
    }

    private static long[] CreateAccumulator(int sampleCount)
    {
        var accumulator = new long[sampleCount];
        var random = new Random(0x41_43_43);
        for (int index = 0; index < accumulator.Length; index++)
        {
            accumulator[index] = random.NextInt64(
                -65_536L,
                65_537L);
        }
        return accumulator;
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
