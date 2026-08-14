using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using Silk.NET.WebGPU;

internal static class ManagedPictureBenchmark
{
    private const uint Width = 960U;
    private const uint Height = 540U;
    private static readonly Vector4 ClearColor = new(0.015f, 0.02f, 0.035f, 1f);

    internal static void Run(string[] args)
    {
        int primitiveCount = ReadPositive(args, "--rectangles", 384);
        int warmupCount = ReadNonNegative(args, "--warmup", 60);
        int iterationCount = ReadPositive(args, "--iterations", 300);
        string? outputJson = ReadString(args, "--output-json");
        bool writeImages = HasFlag(args, "--write-images");

        using GpuPicture picture = CreatePicture(primitiveCount);
        const ulong sceneId = 0x5049435455524542UL;
        const ulong generation = 1UL;
        long compileAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        long compileStart = Stopwatch.GetTimestamp();
        if (!GpuPictureNativeSceneCompiler.TryCompile(
                picture,
                sceneId,
                generation,
                out NativeCompiledPicture? compiled,
                out NativePictureCompileFailure failure) ||
            compiled is null)
        {
            throw new InvalidOperationException(
                $"The matched GpuPicture compiler failed: {failure}.");
        }
        double compileMilliseconds = Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds;
        long compileAllocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - compileAllocationStart;

        using var context = new WgpuContext();
        context.Initialize(window: null);
        using var nativeTarget = CreateTarget(context, "Matched native picture target");
        using var managedTarget = CreateTarget(context, "Matched managed picture target");
        using var native = new NativeCompositor(context, TextureFormat.Rgba8Unorm);
        using var managed = new Compositor(
            context,
            TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with
            {
                EnableCompiledSceneCache = true,
                EnableGpuHitTesting = false,
                PrimarySampleCount = 1
            });

        var managedVisual = new DrawingVisual
        {
            Size = new Vector2(Width, Height)
        };
        managedVisual.Context.DrawPicture(picture);

        long updateAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        long updateStart = Stopwatch.GetTimestamp();
        NativeSceneUpdateMetrics update = native.UpdateScene(compiled.Stream);
        double updateMilliseconds = Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
        long updateAllocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - updateAllocationStart;
        NativeSceneUpdateMetrics retainedUpdate = native.UpdateScene(compiled.Stream);
        if (update.SnapshotReused || !retainedUpdate.SnapshotReused ||
            update.DrawCount != (uint)compiled.NativeDrawCount ||
            update.CommandCount != (uint)compiled.NativeDrawCount ||
            retainedUpdate.SnapshotBytes != update.SnapshotBytes)
        {
            throw new InvalidOperationException(
                $"The matched retained scene contract failed: first={update}, " +
                $"retained={retainedUpdate}.");
        }

        NativeSceneFrameMetrics nativeMetrics = default;
        void RenderNative()
        {
            nativeMetrics = native.RenderScene(
                nativeTarget,
                1f,
                sceneId,
                generation,
                ClearColor);
        }
        void RenderManaged()
        {
            managed.RenderOffscreen(
                managedVisual,
                Width,
                Height,
                managedTarget,
                padding: 0f,
                dpiScale: 1f,
                ClearColor);
        }

        RenderNative();
        native.WaitForSubmission(native.GetLastSubmissionToken());
        RenderManaged();
        context.PollDevice(wait: true);
        for (int index = 0; index < warmupCount; index++)
        {
            if ((index & 1) == 0)
            {
                RenderNative();
                native.WaitForSubmission(native.GetLastSubmissionToken());
                RenderManaged();
                context.PollDevice(wait: true);
            }
            else
            {
                RenderManaged();
                context.PollDevice(wait: true);
                RenderNative();
                native.WaitForSubmission(native.GetLastSubmissionToken());
            }
        }

        var nativeSamples = new TimingSample[iterationCount];
        var managedSamples = new TimingSample[iterationCount];
        for (int index = 0; index < iterationCount; index++)
        {
            if ((index & 1) == 0)
            {
                nativeSamples[index] = MeasureNative();
                managedSamples[index] = MeasureManaged();
            }
            else
            {
                managedSamples[index] = MeasureManaged();
                nativeSamples[index] = MeasureNative();
            }
        }

        RenderNative();
        native.WaitForSubmission(native.GetLastSubmissionToken());
        if (nativeMetrics.VertexUploadBytes != 0UL ||
            nativeMetrics.IndexUploadBytes != 0UL ||
            nativeMetrics.BrushUploadBytes != 0UL ||
            nativeMetrics.GradientStopUploadBytes != 0UL ||
            nativeMetrics.UniformUploadBytes != 0UL ||
            nativeMetrics.SubmissionCount != 1UL)
        {
            throw new InvalidOperationException(
                "Stable compiled-picture replay uploaded retained native payload: " +
                nativeMetrics);
        }
        RenderManaged();
        context.PollDevice(wait: true);

        byte[] nativePixels = nativeTarget.ReadPixels();
        byte[] managedPixels = managedTarget.ReadPixels();
        PixelComparison pixels = ComparePixels(nativePixels, managedPixels);
        int changedPixelLimit = Math.Max(1, pixels.PixelCount / 100);
        if (pixels.MaximumChannelDifference > 96 ||
            pixels.PixelsOverThree > changedPixelLimit ||
            pixels.MeanAbsoluteChannelDifference > 0.15)
        {
            throw new InvalidOperationException(
                "Matched GpuPicture pixel parity exceeded its independent-AA " +
                $"budget: {pixels}.");
        }

        string? nativeImagePath = null;
        string? managedImagePath = null;
        string? differenceImagePath = null;
        if (writeImages)
        {
            string directory = Path.GetFullPath(
                "artifacts/progpu-native/differential/managed-picture");
            Directory.CreateDirectory(directory);
            nativeImagePath = Path.Combine(directory, "managed-picture-native.ppm");
            managedImagePath = Path.Combine(directory, "managed-picture-managed.ppm");
            differenceImagePath = Path.Combine(directory, "managed-picture-difference-32x.ppm");
            WritePpm(nativeImagePath, nativePixels);
            WritePpm(managedImagePath, managedPixels);
            WriteDifferencePpm(differenceImagePath, nativePixels, managedPixels, 32);
        }

        var report = new ManagedPictureBenchmarkReport(
            Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Adapter: context.AdapterName,
            Backend: context.AdapterBackendType.ToString(),
            SourceCommandCount: compiled.SourceCommandCount,
            NativeCommandCount: compiled.NativeDrawCount,
            AnalyticPrimitiveCount: compiled.AnalyticPrimitiveCount,
            GeometryPrimitiveCount: compiled.GeometryPrimitiveCount,
            BrushCount: compiled.BrushCount,
            GradientStopCount: compiled.GradientStopCount,
            StreamBytes: compiled.Stream.Length,
            CompileMilliseconds: compileMilliseconds,
            CompileAllocatedBytes: compileAllocatedBytes,
            UpdateMilliseconds: updateMilliseconds,
            UpdateAllocatedBytes: updateAllocatedBytes,
            WarmupIterations: warmupCount,
            MeasuredIterations: iterationCount,
            Native: Summarize(nativeSamples),
            Managed: Summarize(managedSamples),
            NativeSceneUpdate: update,
            StableNativeFrame: nativeMetrics,
            PixelParity: pixels,
            NativeImage: nativeImagePath,
            ManagedImage: managedImagePath,
            DifferenceImage: differenceImagePath);
        if (report.Native.AllocatedBytesPerFrame != 0d ||
            report.Managed.AllocatedBytesPerFrame != 0d)
        {
            throw new InvalidOperationException(
                "Matched stable replay must allocate zero managed bytes per " +
                $"frame: native={report.Native.AllocatedBytesPerFrame}, " +
                $"managed={report.Managed.AllocatedBytesPerFrame}.");
        }
        string json = JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(outputJson))
        {
            string fullPath = Path.GetFullPath(outputJson);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json);
        }

        TimingSample MeasureNative()
        {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long submitStart = Stopwatch.GetTimestamp();
            RenderNative();
            double submission = Stopwatch.GetElapsedTime(submitStart).TotalMilliseconds;
            NativeSubmissionToken token = native.GetLastSubmissionToken();
            long waitStart = Stopwatch.GetTimestamp();
            native.WaitForSubmission(token);
            double completion = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
            return new(
                submission,
                completion,
                Stopwatch.GetElapsedTime(submitStart).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocationStart);
        }

        TimingSample MeasureManaged()
        {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long submitStart = Stopwatch.GetTimestamp();
            RenderManaged();
            double submission = Stopwatch.GetElapsedTime(submitStart).TotalMilliseconds;
            long waitStart = Stopwatch.GetTimestamp();
            context.PollDevice(wait: true);
            double completion = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
            return new(
                submission,
                completion,
                Stopwatch.GetElapsedTime(submitStart).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocationStart);
        }
    }

    private static GpuPicture CreatePicture(int primitiveCount)
    {
        GradientStop[] coolStops =
        [
            new(new Vector4(0.10f, 0.70f, 1f, 1f), 0f),
            new(new Vector4(0.56f, 0.22f, 0.96f, 1f), 1f)
        ];
        GradientStop[] warmStops =
        [
            new(new Vector4(1f, 0.72f, 0.12f, 1f), 0f),
            new(new Vector4(0.96f, 0.16f, 0.38f, 1f), 1f)
        ];
        Brush[] brushes =
        [
            new SolidColorBrush(new Vector4(0.10f, 0.62f, 0.96f, 1f)),
            new LinearGradientBrush(
                new Vector2(0f, 0f),
                new Vector2(Width, Height),
                coolStops),
            new RadialGradientBrush(
                new Vector2(Width * 0.5f, Height * 0.5f),
                Width * 0.45f,
                Height * 0.45f,
                warmStops),
            new TwoPointConicalGradientBrush(
                new Vector2(Width * 0.25f, Height * 0.5f),
                8f,
                new Vector2(Width * 0.75f, Height * 0.5f),
                Width * 0.22f,
                coolStops),
            new SweepGradientBrush(
                new Vector2(Width * 0.5f, Height * 0.5f),
                warmStops)
        ];
        var linePen = new Pen(brushes[4], 2.25f)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        int columns = 24;
        int rows = Math.Max(1, (primitiveCount + columns - 1) / columns);
        float cellWidth = Width / (float)columns;
        float cellHeight = Height / (float)rows;
        float inset = MathF.Min(3f, MathF.Min(cellWidth, cellHeight) * 0.15f);

        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(new Rect(0f, 0f, Width, Height));
        int analyticCount = Math.Max(1, primitiveCount * 5 / 6);
        for (int index = 0; index < analyticCount; index++)
        {
            int xIndex = index % columns;
            int yIndex = index / columns;
            var rect = new Rect(
                xIndex * cellWidth + inset,
                yIndex * cellHeight + inset,
                MathF.Max(1f, cellWidth - inset * 2f),
                MathF.Max(1f, cellHeight - inset * 2f));
            Brush brush = brushes[index % brushes.Length];
            switch (index % 3)
            {
                case 0:
                    drawing.DrawRectangle(brush, null, rect);
                    break;
                case 1:
                    drawing.DrawEllipse(
                        brush,
                        null,
                        new Vector2(
                            rect.X + rect.Width * 0.5f,
                            rect.Y + rect.Height * 0.5f),
                        rect.Width * 0.5f,
                        rect.Height * 0.5f);
                    break;
                default:
                    drawing.DrawRoundedRectangle(
                        brush,
                        null,
                        rect,
                        MathF.Min(rect.Width, rect.Height) * 0.22f);
                    break;
            }
        }
        for (int index = analyticCount; index < primitiveCount; index++)
        {
            int xIndex = index % columns;
            int yIndex = index / columns;
            Vector2 start = new(
                xIndex * cellWidth + inset,
                yIndex * cellHeight + inset);
            Vector2 end = new(
                (xIndex + 1) * cellWidth - inset,
                (yIndex + 1) * cellHeight - inset);
            drawing.DrawLine(linePen, start, end);
        }
        return recorder.EndRecording();
    }

    private static GpuTexture CreateTarget(WgpuContext context, string label) =>
        new(
            context,
            Width,
            Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            label);

    private static TimingSummary Summarize(TimingSample[] samples)
    {
        double[] submission = samples.Select(static value => value.SubmissionMilliseconds)
            .OrderBy(static value => value).ToArray();
        double[] completion = samples.Select(static value => value.CompletionWaitMilliseconds)
            .OrderBy(static value => value).ToArray();
        double[] total = samples.Select(static value => value.TotalMilliseconds)
            .OrderBy(static value => value).ToArray();
        return new(
            SubmissionP50Milliseconds: Percentile(submission, 0.50),
            SubmissionP95Milliseconds: Percentile(submission, 0.95),
            CompletionWaitP50Milliseconds: Percentile(completion, 0.50),
            CompletionWaitP95Milliseconds: Percentile(completion, 0.95),
            TotalP50Milliseconds: Percentile(total, 0.50),
            TotalP95Milliseconds: Percentile(total, 0.95),
            MaximumMilliseconds: total[^1],
            AllocatedBytesPerFrame: samples.Average(static value => (double)value.AllocatedBytes));
    }

    private static double Percentile(double[] ordered, double percentile) =>
        ordered[Math.Clamp(
            (int)Math.Ceiling(percentile * ordered.Length) - 1,
            0,
            ordered.Length - 1)];

    private static PixelComparison ComparePixels(byte[] native, byte[] managed)
    {
        int maximum = 0;
        int overThree = 0;
        long total = 0L;
        ulong nativeHash = 14695981039346656037UL;
        ulong managedHash = 14695981039346656037UL;
        for (int offset = 0; offset < native.Length; offset += 4)
        {
            int pixelMaximum = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                byte nativeValue = native[offset + channel];
                byte managedValue = managed[offset + channel];
                int difference = Math.Abs(nativeValue - managedValue);
                maximum = Math.Max(maximum, difference);
                pixelMaximum = Math.Max(pixelMaximum, difference);
                total += difference;
                nativeHash = (nativeHash ^ nativeValue) * 1099511628211UL;
                managedHash = (managedHash ^ managedValue) * 1099511628211UL;
            }
            if (pixelMaximum > 3)
                overThree++;
        }
        return new(
            PixelCount: checked((int)(Width * Height)),
            MaximumChannelDifference: maximum,
            PixelsOverThree: overThree,
            MeanAbsoluteChannelDifference: total / (double)(native.Length),
            NativeFnv1A64: nativeHash.ToString("X16"),
            ManagedFnv1A64: managedHash.ToString("X16"));
    }

    private static void WritePpm(string path, byte[] pixels)
    {
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes($"P6\n{Width} {Height}\n255\n"));
        for (int offset = 0; offset < pixels.Length; offset += 4)
            writer.Write(pixels, offset, 3);
    }

    private static void WriteDifferencePpm(
        string path,
        byte[] native,
        byte[] managed,
        int amplification)
    {
        byte[] difference = new byte[native.Length];
        for (int offset = 0; offset < native.Length; offset += 4)
        {
            difference[offset] = (byte)Math.Min(
                255,
                Math.Abs(native[offset] - managed[offset]) * amplification);
            difference[offset + 1] = (byte)Math.Min(
                255,
                Math.Abs(native[offset + 1] - managed[offset + 1]) * amplification);
            difference[offset + 2] = (byte)Math.Min(
                255,
                Math.Abs(native[offset + 2] - managed[offset + 2]) * amplification);
            difference[offset + 3] = 255;
        }
        WritePpm(path, difference);
    }

    private static bool HasFlag(string[] args, string name) =>
        Array.Exists(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));

    private static int ReadPositive(string[] args, string name, int fallback)
    {
        string? value = ReadString(args, name);
        return int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static int ReadNonNegative(string[] args, string name, int fallback)
    {
        string? value = ReadString(args, name);
        return int.TryParse(value, out int parsed) && parsed >= 0 ? parsed : fallback;
    }

    private static string? ReadString(string[] args, string name)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    private readonly record struct TimingSample(
        double SubmissionMilliseconds,
        double CompletionWaitMilliseconds,
        double TotalMilliseconds,
        long AllocatedBytes);

    private sealed record ManagedPictureBenchmarkReport(
        string Runtime,
        string OperatingSystem,
        string Adapter,
        string Backend,
        int SourceCommandCount,
        int NativeCommandCount,
        int AnalyticPrimitiveCount,
        int GeometryPrimitiveCount,
        int BrushCount,
        int GradientStopCount,
        int StreamBytes,
        double CompileMilliseconds,
        long CompileAllocatedBytes,
        double UpdateMilliseconds,
        long UpdateAllocatedBytes,
        int WarmupIterations,
        int MeasuredIterations,
        TimingSummary Native,
        TimingSummary Managed,
        NativeSceneUpdateMetrics NativeSceneUpdate,
        NativeSceneFrameMetrics StableNativeFrame,
        PixelComparison PixelParity,
        string? NativeImage,
        string? ManagedImage,
        string? DifferenceImage);

    private sealed record TimingSummary(
        double SubmissionP50Milliseconds,
        double SubmissionP95Milliseconds,
        double CompletionWaitP50Milliseconds,
        double CompletionWaitP95Milliseconds,
        double TotalP50Milliseconds,
        double TotalP95Milliseconds,
        double MaximumMilliseconds,
        double AllocatedBytesPerFrame);

    private sealed record PixelComparison(
        int PixelCount,
        int MaximumChannelDifference,
        int PixelsOverThree,
        double MeanAbsoluteChannelDifference,
        string NativeFnv1A64,
        string ManagedFnv1A64);
}
