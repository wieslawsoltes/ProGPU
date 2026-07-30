using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Avalonia;
using Avalonia.ProGpu;
using ProGPU.Avalonia;
using ProGPU.Backend;
using ProGPU.Samples;
using ProGPU.Scene;

namespace ProGPU.Samples.Avalonia;

/// <summary>
/// Deterministic fresh-process benchmark for the Avalonia-hosted ProGPU samples.
/// The ordinary Avalonia shell and the embedded sample compositor are reported
/// separately so a shell frame cannot be mistaken for sample rendering.
/// </summary>
internal sealed partial class AvaloniaSampleBenchmark : IDisposable
{
    private const int BenchmarkSchemaVersion = 3;
    private const string OutputVariable =
        "PROGPU_AVALONIA_SAMPLE_BENCHMARK_OUTPUT";
    private const string WarmupVariable =
        "PROGPU_AVALONIA_SAMPLE_BENCHMARK_WARMUP_FRAMES";
    private const string MeasureVariable =
        "PROGPU_AVALONIA_SAMPLE_BENCHMARK_MEASURE_FRAMES";
    private const string HoldVariable =
        "PROGPU_AVALONIA_SAMPLE_BENCHMARK_HOLD_MS";
    private const string RunVariable =
        "PROGPU_AVALONIA_SAMPLE_BENCHMARK_RUN";

    private readonly string _sample;
    private readonly string _textShaper;
    private readonly string _outputPath;
    private readonly int _warmupFrames;
    private readonly int _measureFrames;
    private readonly int _holdMilliseconds;
    private readonly int _run;
    private readonly double[] _frameTimeSamples;
    private readonly double[] _embeddedCompileTimeSamples;
    private readonly double[] _embeddedUploadTimeSamples;
    private readonly double[] _embeddedRenderTimeSamples;
    private readonly double[] _embeddedCompositorTimeSamples;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _measurementClock = new();
    private readonly long _startupTimestamp = Stopwatch.GetTimestamp();
    private MainWindow? _window;
    private ProGpuHostControl? _host;
    private int _totalFrames;
    private int _measuredFrames;
    private bool _attached;
    private bool _completed;
    private long _allocatedBytesStart;
    private TimeSpan _cpuStart;
    private int _gen0Start;
    private int _gen1Start;
    private int _gen2Start;
    private double _frameMilliseconds;
    private double _maximumFrameMilliseconds;
    private int _outerFramesSeen;
    private int _outerMetricSamples;
    private double _outerCompileMilliseconds;
    private double _outerUploadMilliseconds;
    private double _outerRenderMilliseconds;
    private double _outerCompositorMilliseconds;
    private int _outerSceneCacheHits;
    private double _firstOuterRenderedFrameMilliseconds;
    private CompositorMetrics _lastOuterMetrics;
    private int _embeddedMetricSamples;
    private double _embeddedCompileMilliseconds;
    private double _embeddedUploadMilliseconds;
    private double _embeddedRenderMilliseconds;
    private double _embeddedCompositorMilliseconds;
    private int _embeddedSceneCacheHits;
    private CompositorMetrics _lastEmbeddedMetrics;

    private AvaloniaSampleBenchmark(
        string sample,
        string textShaper,
        string outputPath,
        int warmupFrames,
        int measureFrames,
        int holdMilliseconds,
        int run)
    {
        _sample = sample;
        _textShaper = textShaper;
        _outputPath = outputPath;
        _warmupFrames = warmupFrames;
        _measureFrames = measureFrames;
        _holdMilliseconds = holdMilliseconds;
        _run = run;
        _frameTimeSamples = new double[measureFrames];
        _embeddedCompileTimeSamples = new double[measureFrames];
        _embeddedUploadTimeSamples = new double[measureFrames];
        _embeddedRenderTimeSamples = new double[measureFrames];
        _embeddedCompositorTimeSamples = new double[measureFrames];
        ProGpuRenderingDiagnostics.FrameRendered += OnOuterFrameRendered;
        SampleBenchmarkEventSource.Log.WorkloadStarted(sample);
    }

    public static AvaloniaSampleBenchmark? TryStart(
        string sample,
        string textShaper)
    {
        string? outputPath = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return null;
        }

        return new AvaloniaSampleBenchmark(
            sample,
            textShaper,
            Path.GetFullPath(outputPath),
            ReadPositiveInt(WarmupVariable, 120),
            ReadPositiveInt(MeasureVariable, 300),
            ReadNonNegativeInt(HoldVariable, 0),
            ReadPositiveInt(RunVariable, 1));
    }

    public void Attach(MainWindow window, ProGpuHostControl host)
    {
        if (_attached)
        {
            throw new InvalidOperationException(
                "The Avalonia sample benchmark was attached more than once.");
        }
        if (host.WinuiRoot is null || host.Compositor is null ||
            host.WgpuContext is null)
        {
            throw new InvalidOperationException(
                "The selected embedded ProGPU sample was not initialized.");
        }

        _window = window;
        _host = host;
        _attached = true;
    }

    public bool ObserveFrame(TimeSpan timestamp, double deltaSeconds)
    {
        _ = timestamp;
        if (_completed)
        {
            return false;
        }
        if (!_attached || _host?.Compositor is not { } embeddedCompositor)
        {
            throw new InvalidOperationException(
                "The Avalonia sample benchmark observed a frame before attachment.");
        }

        _totalFrames++;
        if (_totalFrames == _warmupFrames)
        {
            CollectRetainedMemory();
            _allocatedBytesStart = GC.GetTotalAllocatedBytes(precise: true);
            _cpuStart = _process.TotalProcessorTime;
            _gen0Start = GC.CollectionCount(0);
            _gen1Start = GC.CollectionCount(1);
            _gen2Start = GC.CollectionCount(2);
            _measurementClock.Restart();
            SampleBenchmarkEventSource.Log.MeasurementStarted(_sample);
            return true;
        }
        if (_totalFrames <= _warmupFrames)
        {
            return true;
        }

        double frameMilliseconds = Math.Max(0d, deltaSeconds * 1000d);
        _frameMilliseconds += frameMilliseconds;
        _maximumFrameMilliseconds = Math.Max(
            _maximumFrameMilliseconds,
            frameMilliseconds);
        _frameTimeSamples[_measuredFrames] = frameMilliseconds;

        CompositorMetrics embeddedMetrics = embeddedCompositor.Metrics;
        _embeddedCompileTimeSamples[_measuredFrames] =
            embeddedMetrics.VisualTreeCompileTimeMs;
        _embeddedUploadTimeSamples[_measuredFrames] =
            embeddedMetrics.GpuUploadTimeMs;
        _embeddedRenderTimeSamples[_measuredFrames] =
            embeddedMetrics.RenderPassTimeMs;
        _embeddedCompositorTimeSamples[_measuredFrames] =
            embeddedMetrics.FrameTimeMs;
        _embeddedMetricSamples++;
        _embeddedCompileMilliseconds +=
            embeddedMetrics.VisualTreeCompileTimeMs;
        _embeddedUploadMilliseconds += embeddedMetrics.GpuUploadTimeMs;
        _embeddedRenderMilliseconds += embeddedMetrics.RenderPassTimeMs;
        _embeddedCompositorMilliseconds += embeddedMetrics.FrameTimeMs;
        if (embeddedMetrics.SceneCacheHit)
        {
            _embeddedSceneCacheHits++;
        }
        _lastEmbeddedMetrics = embeddedMetrics;

        _measuredFrames++;
        if (_measuredFrames < _measureFrames)
        {
            return true;
        }

        _completed = true;
        Complete();
        return false;
    }

    private void OnOuterFrameRendered(CompositorMetrics metrics)
    {
        _outerFramesSeen++;
        _lastOuterMetrics = metrics;
        if (_firstOuterRenderedFrameMilliseconds == 0d)
        {
            _firstOuterRenderedFrameMilliseconds = Stopwatch
                .GetElapsedTime(_startupTimestamp)
                .TotalMilliseconds;
        }
        if (_completed || _totalFrames <= _warmupFrames)
        {
            return;
        }

        _outerMetricSamples++;
        _outerCompileMilliseconds += metrics.VisualTreeCompileTimeMs;
        _outerUploadMilliseconds += metrics.GpuUploadTimeMs;
        _outerRenderMilliseconds += metrics.RenderPassTimeMs;
        _outerCompositorMilliseconds += metrics.FrameTimeMs;
        if (metrics.SceneCacheHit)
        {
            _outerSceneCacheHits++;
        }
    }

    private void Complete()
    {
        _measurementClock.Stop();
        SampleBenchmarkEventSource.Log.MeasurementStopped(_sample);
        if (_window is null || _host is null)
        {
            throw new InvalidOperationException(
                "The Avalonia sample benchmark lost its host.");
        }

        ProGpuAvaloniaHostFrameState hostFrame =
            _host.LastPresentedFrameState;
        if (!hostFrame.HasPresentedFrame)
        {
            throw new InvalidOperationException(
                $"The embedded sample '{_sample}' did not present a ProGPU frame.");
        }
        if (_outerFramesSeen == 0 ||
            _lastOuterMetrics.DrawCallsCount == 0)
        {
            throw new InvalidOperationException(
                "The Avalonia shell did not render through the ProGPU compositor: " +
                $"frames={_outerFramesSeen}, measurementFrames={_outerMetricSamples}, " +
                $"drawCalls={_lastOuterMetrics.DrawCallsCount}, " +
                $"serverBackendRenders={_lastOuterMetrics.RetainedCompositionServerBackendRenderCount}, " +
                $"retainedScenes={_lastOuterMetrics.RetainedCompositionSceneCount}, " +
                $"fallbackNodes={_lastOuterMetrics.RetainedCompositionFallbackNodeCount}.");
        }
        if (_lastOuterMetrics.RetainedCompositionFallbackNodeCount != 0)
        {
            throw new InvalidOperationException(
                "The Avalonia shell used retained-composition fallback nodes: " +
                _lastOuterMetrics.RetainedCompositionFallbackNodeCount);
        }
        if (_embeddedMetricSamples == 0 ||
            _lastEmbeddedMetrics.DrawCallsCount == 0)
        {
            throw new InvalidOperationException(
                $"The embedded sample '{_sample}' produced no ProGPU draw calls.");
        }

        long allocatedBytes = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: true) - _allocatedBytesStart);
        TimeSpan cpu = _process.TotalProcessorTime - _cpuStart;
        double elapsedSeconds = Math.Max(
            _measurementClock.Elapsed.TotalSeconds,
            double.Epsilon);
        PresentedTextureContent textureContent =
            AnalyzePresentedTexture(_host, hostFrame.PresentationMode);
        if (hostFrame.PresentationMode ==
                ProGpuAvaloniaPresentationMode.SameDeviceTexture &&
            (textureContent.NonTransparentPixels == 0 ||
             textureContent.PixelsDifferentFromFirst == 0))
        {
            throw new InvalidOperationException(
                $"The embedded sample '{_sample}' submitted a blank same-device texture: " +
                $"nonTransparent={textureContent.NonTransparentPixels}, " +
                $"differentFromFirst={textureContent.PixelsDifferentFromFirst}, " +
                $"size={textureContent.Width}x{textureContent.Height}.");
        }

        CollectRetainedMemory();
        _process.Refresh();
        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
        ProcessMemorySnapshot memory =
            ProcessMemorySnapshot.CaptureCurrent();
        WgpuNativeResourceSnapshot nativeGpu = default;
        _ = WgpuContext.TryGetFirstActiveContext(out WgpuContext? context) &&
            context.TryCaptureNativeResourceSnapshot(out nativeGpu);
        Distribution frameTime = CalculateDistribution(
            _frameTimeSamples,
            _measuredFrames);
        Distribution compileTime = CalculateDistribution(
            _embeddedCompileTimeSamples,
            _embeddedMetricSamples);
        Distribution uploadTime = CalculateDistribution(
            _embeddedUploadTimeSamples,
            _embeddedMetricSamples);
        Distribution renderTime = CalculateDistribution(
            _embeddedRenderTimeSamples,
            _embeddedMetricSamples);
        Distribution compositorTime = CalculateDistribution(
            _embeddedCompositorTimeSamples,
            _embeddedMetricSamples);

        var result = new BenchmarkResult
        {
            SchemaVersion = BenchmarkSchemaVersion,
            Backend = "ProGPU/Silk.NET + embedded ProGPU",
            TextShaper = _textShaper,
            Page = _sample,
            Run = _run,
            WarmupFrames = _warmupFrames,
            MeasuredFrames = _measureFrames,
            ElapsedSeconds = elapsedSeconds,
            FramesPerSecond = _measureFrames / elapsedSeconds,
            AverageFrameMs = _frameMilliseconds / _measureFrames,
            FrameTimeSampleCount = frameTime.Count,
            MinFrameMs = frameTime.Minimum,
            MedianFrameMs = frameTime.Median,
            P95FrameMs = frameTime.P95,
            P99FrameMs = frameTime.P99,
            MaxFrameMs = frameTime.Maximum,
            ProcessCpuSeconds = cpu.TotalSeconds,
            ProcessCpuPercent = cpu.TotalSeconds / elapsedSeconds /
                Math.Max(1, Environment.ProcessorCount) * 100d,
            AllocatedBytes = allocatedBytes,
            AllocatedBytesPerFrame =
                (double)allocatedBytes / _measureFrames,
            ManagedBytes = GC.GetTotalMemory(forceFullCollection: false),
            GcCommittedBytes = gcInfo.TotalCommittedBytes,
            ManagedFragmentedBytes = gcInfo.FragmentedBytes,
            ProcessWorkingSetBytes = _process.WorkingSet64,
            ProcessResidentBytes = memory.ResidentBytes,
            ProcessWiredBytes = memory.WiredBytes,
            ProcessPhysicalFootprintBytes =
                memory.PhysicalFootprintBytes,
            ProcessPeakPhysicalFootprintBytes =
                memory.LifetimeMaxPhysicalFootprintBytes,
            Gen0Collections = GC.CollectionCount(0) - _gen0Start,
            Gen1Collections = GC.CollectionCount(1) - _gen1Start,
            Gen2Collections = GC.CollectionCount(2) - _gen2Start,
            AverageCompileMs = Average(
                _embeddedCompileMilliseconds,
                _embeddedMetricSamples),
            AverageUploadMs = Average(
                _embeddedUploadMilliseconds,
                _embeddedMetricSamples),
            AverageRenderMs = Average(
                _embeddedRenderMilliseconds,
                _embeddedMetricSamples),
            AverageCompositorMs = Average(
                _embeddedCompositorMilliseconds,
                _embeddedMetricSamples),
            CompositorMetricSampleCount = compositorTime.Count,
            MedianCompileMs = compileTime.Median,
            P95CompileMs = compileTime.P95,
            P99CompileMs = compileTime.P99,
            MaxCompileMs = compileTime.Maximum,
            MedianUploadMs = uploadTime.Median,
            P95UploadMs = uploadTime.P95,
            P99UploadMs = uploadTime.P99,
            MaxUploadMs = uploadTime.Maximum,
            MedianRenderMs = renderTime.Median,
            P95RenderMs = renderTime.P95,
            P99RenderMs = renderTime.P99,
            MaxRenderMs = renderTime.Maximum,
            MedianCompositorMs = compositorTime.Median,
            P95CompositorMs = compositorTime.P95,
            P99CompositorMs = compositorTime.P99,
            MaxCompositorMs = compositorTime.Maximum,
            SceneCacheHits = _embeddedSceneCacheHits,
            DrawCalls = _lastEmbeddedMetrics.DrawCallsCount,
            RecordedCommands =
                _lastEmbeddedMetrics.RecordedCommandCount,
            VectorVertices = _lastEmbeddedMetrics.VectorVerticesCount,
            TextVertices = _lastEmbeddedMetrics.TextVerticesCount,
            PathAtlasEntries =
                _lastEmbeddedMetrics.PathAtlasCachedCount,
            PathAtlasTextureBytes =
                _lastEmbeddedMetrics.PathAtlasTextureBytes,
            GlyphAtlasTextureBytes =
                _lastEmbeddedMetrics.GlyphAtlasTextureBytes,
            ColorGlyphAtlasTextureBytes =
                _lastEmbeddedMetrics.ColorGlyphAtlasTextureBytes,
            TrackedIntermediateTextureBytes =
                _lastEmbeddedMetrics.TrackedIntermediateTextureBytes,
            MetalAllocatedBytes = nativeGpu.MetalAllocatedBytes,
            OuterAverageCompileMs = Average(
                _outerCompileMilliseconds,
                _outerMetricSamples),
            OuterAverageUploadMs = Average(
                _outerUploadMilliseconds,
                _outerMetricSamples),
            OuterAverageRenderMs = Average(
                _outerRenderMilliseconds,
                _outerMetricSamples),
            OuterAverageCompositorMs = Average(
                _outerCompositorMilliseconds,
                _outerMetricSamples),
            OuterSceneCacheHits = _outerSceneCacheHits,
            OuterFramesSeen = _outerFramesSeen,
            OuterMeasurementFrames = _outerMetricSamples,
            RetainedCompositionScenes =
                _lastOuterMetrics.RetainedCompositionSceneCount,
            RetainedCompositionServerBackendRenders =
                _lastOuterMetrics.RetainedCompositionServerBackendRenderCount,
            RetainedCompositionSceneNodes =
                _lastOuterMetrics.RetainedCompositionSceneNodeCount,
            RetainedCompositionFallbackNodes =
                _lastOuterMetrics.RetainedCompositionFallbackNodeCount,
            RetainedCompositionPictureHits =
                _lastOuterMetrics.RetainedCompositionPictureHits,
            RetainedCompositionPictureCompilations =
                _lastOuterMetrics.RetainedCompositionPictureCompilations,
            FirstRenderedFrameMs =
                _firstOuterRenderedFrameMilliseconds,
            PresentationMode = hostFrame.PresentationMode.ToString(),
            PresentedFrames = hostFrame.PresentedFrameCount,
            RenderTargetWidth = hostFrame.HostFrame.RenderTargetWidth,
            RenderTargetHeight = hostFrame.HostFrame.RenderTargetHeight,
            DpiScale = hostFrame.HostFrame.DpiScale,
            GpuHandleType = hostFrame.GpuHandleType,
            PresentationSetupStatus =
                _host.PresentationSetupStatus,
            PresentedTextureNonTransparentPixels =
                textureContent.NonTransparentPixels,
            PresentedTexturePixelsDifferentFromFirst =
                textureContent.PixelsDifferentFromFirst,
            EmbeddedBackendKind =
                _host.WgpuContext?.BackendKind.ToString() ??
                "Unavailable",
            SharedTextureMemoryRequested =
                Program.EnableSharedTextureMemory,
            SharedImageReadbackRequested =
                Program.EnableSharedImageReadback
        };

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"[AvaloniaSampleBenchmark] RESULT sample=\"{result.Page}\"" +
                $" run={result.Run}" +
                $" textShaper={result.TextShaper}" +
                $" frames={result.MeasuredFrames}" +
                $" fps={result.FramesPerSecond:F2}" +
                $" frameMs={result.AverageFrameMs:F4}" +
                $" embeddedCompileMs={result.AverageCompileMs:F4}" +
                $" embeddedRenderMs={result.AverageRenderMs:F4}" +
                $" outerCompileMs={result.OuterAverageCompileMs:F4}" +
                $" allocatedBytesPerFrame={result.AllocatedBytesPerFrame:F0}" +
                $" managedBytes={result.ManagedBytes}" +
                $" physicalFootprintBytes={result.ProcessPhysicalFootprintBytes}" +
                $" metalAllocatedBytes={result.MetalAllocatedBytes}" +
                $" presentation={result.PresentationMode}" +
                $" setup=\"{result.PresentationSetupStatus}\"" +
                $" textureNonTransparent={result.PresentedTextureNonTransparentPixels}" +
                $" textureDifferent={result.PresentedTexturePixelsDifferentFromFirst}" +
                $" retainedScenes={result.RetainedCompositionScenes}" +
                $" retainedFallbackNodes={result.RetainedCompositionFallbackNodes}"));

        string? outputDirectory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        using (FileStream output = File.Create(_outputPath))
        {
            JsonSerializer.Serialize(
                output,
                result,
                BenchmarkJsonContext.Default.BenchmarkResult);
        }
        if (_holdMilliseconds > 0)
        {
            SampleBenchmarkEventSource.Log.SnapshotHoldStarted(_sample);
            Thread.Sleep(_holdMilliseconds);
        }
        Environment.Exit(0);
    }

    private static void CollectRetainedMemory()
    {
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Aggressive,
            blocking: true,
            compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Aggressive,
            blocking: true,
            compacting: true);
    }

    private static int ReadPositiveInt(string variable, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(
                   value,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out int parsed) &&
               parsed > 0
            ? parsed
            : fallback;
    }

    private static int ReadNonNegativeInt(
        string variable,
        int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(
                   value,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out int parsed) &&
               parsed >= 0
            ? parsed
            : fallback;
    }

    private static double Average(double total, int samples)
        => samples == 0 ? 0d : total / samples;

    private static PresentedTextureContent AnalyzePresentedTexture(
        ProGpuHostControl host,
        ProGpuAvaloniaPresentationMode presentationMode)
    {
        if (presentationMode !=
            ProGpuAvaloniaPresentationMode.SameDeviceTexture)
        {
            return default;
        }

        _ = host.TryReadPresentedTexture(
            Span<byte>.Empty,
            out PixelSize pixelSize);
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            return default;
        }

        byte[] pixels = GC.AllocateUninitializedArray<byte>(
            checked(pixelSize.Width * pixelSize.Height * 4));
        if (!host.TryReadPresentedTexture(pixels, out PixelSize readSize) ||
            readSize != pixelSize)
        {
            return default;
        }

        int nonTransparent = 0;
        int different = 0;
        byte firstB = pixels[0];
        byte firstG = pixels[1];
        byte firstR = pixels[2];
        byte firstA = pixels[3];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] != 0)
            {
                nonTransparent++;
            }
            if (pixels[offset] != firstB ||
                pixels[offset + 1] != firstG ||
                pixels[offset + 2] != firstR ||
                pixels[offset + 3] != firstA)
            {
                different++;
            }
        }

        return new PresentedTextureContent(
            pixelSize.Width,
            pixelSize.Height,
            nonTransparent,
            different);
    }

    private static Distribution CalculateDistribution(
        double[] samples,
        int count)
    {
        count = Math.Clamp(count, 0, samples.Length);
        if (count == 0)
        {
            return default;
        }

        Array.Sort(samples, 0, count);
        return new Distribution(
            count,
            samples[0],
            Percentile(samples, count, 0.50d),
            Percentile(samples, count, 0.95d),
            Percentile(samples, count, 0.99d),
            samples[count - 1]);
    }

    private static double Percentile(
        double[] sortedSamples,
        int count,
        double percentile)
    {
        double index = (count - 1) * percentile;
        int lowerIndex = (int)index;
        int upperIndex = Math.Min(count - 1, lowerIndex + 1);
        double fraction = index - lowerIndex;
        return sortedSamples[lowerIndex] +
            ((sortedSamples[upperIndex] - sortedSamples[lowerIndex]) *
                fraction);
    }

    private readonly record struct Distribution(
        int Count,
        double Minimum,
        double Median,
        double P95,
        double P99,
        double Maximum);

    private readonly record struct PresentedTextureContent(
        int Width,
        int Height,
        int NonTransparentPixels,
        int PixelsDifferentFromFirst);

    public void Dispose()
    {
        ProGpuRenderingDiagnostics.FrameRendered -= OnOuterFrameRendered;
        _process.Dispose();
    }

    private sealed class BenchmarkResult
    {
        public int SchemaVersion { get; init; }
        public string Backend { get; init; } = string.Empty;
        public string TextShaper { get; init; } = string.Empty;
        public string Page { get; init; } = string.Empty;
        public int Run { get; init; }
        public int WarmupFrames { get; init; }
        public int MeasuredFrames { get; init; }
        public double ElapsedSeconds { get; init; }
        public double FramesPerSecond { get; init; }
        public double AverageFrameMs { get; init; }
        public int FrameTimeSampleCount { get; init; }
        public double MinFrameMs { get; init; }
        public double MedianFrameMs { get; init; }
        public double P95FrameMs { get; init; }
        public double P99FrameMs { get; init; }
        public double MaxFrameMs { get; init; }
        public double ProcessCpuSeconds { get; init; }
        public double ProcessCpuPercent { get; init; }
        public long AllocatedBytes { get; init; }
        public double AllocatedBytesPerFrame { get; init; }
        public long ManagedBytes { get; init; }
        public long GcCommittedBytes { get; init; }
        public long ManagedFragmentedBytes { get; init; }
        public long ProcessWorkingSetBytes { get; init; }
        public long ProcessResidentBytes { get; init; }
        public long ProcessWiredBytes { get; init; }
        public long ProcessPhysicalFootprintBytes { get; init; }
        public long ProcessPeakPhysicalFootprintBytes { get; init; }
        public int Gen0Collections { get; init; }
        public int Gen1Collections { get; init; }
        public int Gen2Collections { get; init; }
        public double AverageCompileMs { get; init; }
        public double AverageUploadMs { get; init; }
        public double AverageRenderMs { get; init; }
        public double AverageCompositorMs { get; init; }
        public int CompositorMetricSampleCount { get; init; }
        public double MedianCompileMs { get; init; }
        public double P95CompileMs { get; init; }
        public double P99CompileMs { get; init; }
        public double MaxCompileMs { get; init; }
        public double MedianUploadMs { get; init; }
        public double P95UploadMs { get; init; }
        public double P99UploadMs { get; init; }
        public double MaxUploadMs { get; init; }
        public double MedianRenderMs { get; init; }
        public double P95RenderMs { get; init; }
        public double P99RenderMs { get; init; }
        public double MaxRenderMs { get; init; }
        public double MedianCompositorMs { get; init; }
        public double P95CompositorMs { get; init; }
        public double P99CompositorMs { get; init; }
        public double MaxCompositorMs { get; init; }
        public int SceneCacheHits { get; init; }
        public int DrawCalls { get; init; }
        public int RecordedCommands { get; init; }
        public int VectorVertices { get; init; }
        public int TextVertices { get; init; }
        public int PathAtlasEntries { get; init; }
        public ulong PathAtlasTextureBytes { get; init; }
        public ulong GlyphAtlasTextureBytes { get; init; }
        public ulong ColorGlyphAtlasTextureBytes { get; init; }
        public ulong TrackedIntermediateTextureBytes { get; init; }
        public ulong MetalAllocatedBytes { get; init; }
        public double OuterAverageCompileMs { get; init; }
        public double OuterAverageUploadMs { get; init; }
        public double OuterAverageRenderMs { get; init; }
        public double OuterAverageCompositorMs { get; init; }
        public int OuterSceneCacheHits { get; init; }
        public int OuterFramesSeen { get; init; }
        public int OuterMeasurementFrames { get; init; }
        public int RetainedCompositionScenes { get; init; }
        public long RetainedCompositionServerBackendRenders { get; init; }
        public int RetainedCompositionSceneNodes { get; init; }
        public int RetainedCompositionFallbackNodes { get; init; }
        public long RetainedCompositionPictureHits { get; init; }
        public long RetainedCompositionPictureCompilations { get; init; }
        public double FirstRenderedFrameMs { get; init; }
        public string PresentationMode { get; init; } = string.Empty;
        public ulong PresentedFrames { get; init; }
        public uint RenderTargetWidth { get; init; }
        public uint RenderTargetHeight { get; init; }
        public double DpiScale { get; init; }
        public string GpuHandleType { get; init; } = string.Empty;
        public string PresentationSetupStatus { get; init; } =
            string.Empty;
        public int PresentedTextureNonTransparentPixels { get; init; }
        public int PresentedTexturePixelsDifferentFromFirst { get; init; }
        public string EmbeddedBackendKind { get; init; } =
            string.Empty;
        public bool SharedTextureMemoryRequested { get; init; }
        public bool SharedImageReadbackRequested { get; init; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(BenchmarkResult))]
    private sealed partial class BenchmarkJsonContext :
        JsonSerializerContext;
}
