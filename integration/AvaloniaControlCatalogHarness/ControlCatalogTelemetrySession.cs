using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
#if PROGPU_AVALONIA_BACKEND
using Avalonia.ProGpu;
using Avalonia.SilkNet;
using ProGPU.Backend;
using CompositorMetrics = ProGPU.Scene.CompositorMetrics;
#endif

namespace ControlCatalog.Desktop;

/// <summary>
/// Runs a bounded fresh-process ControlCatalog workload. The frame clock is
/// driven through Avalonia's public animation-frame contract and all telemetry
/// is emitted through a typed JSON writer.
/// </summary>
internal sealed class ControlCatalogTelemetrySession : IDisposable
{
    private const int SchemaVersion = 2;
    private const string OutputVariable =
        "PROGPU_AVALONIA_BENCHMARK_OUTPUT";
    private const string ScreenshotVariable =
        "PROGPU_AVALONIA_BENCHMARK_SCREENSHOT";
    private const string WarmupVariable =
        "PROGPU_AVALONIA_BENCHMARK_WARMUP_FRAMES";
    private const string MeasureVariable =
        "PROGPU_AVALONIA_BENCHMARK_MEASURE_FRAMES";
    private const string RunVariable =
        "PROGPU_AVALONIA_BENCHMARK_RUN";
    private const string HoldSecondsVariable =
        "PROGPU_AVALONIA_BENCHMARK_DIAGNOSTIC_HOLD_SECONDS";
    private const string ReadyVariable =
        "PROGPU_AVALONIA_BENCHMARK_DIAGNOSTIC_READY";

    private readonly string _backend;
    private readonly string _page;
    private readonly string _textShaper;
    private readonly string _outputPath;
    private readonly string? _screenshotPath;
    private readonly string? _readyPath;
    private readonly int _warmupFrames;
    private readonly int _measureFrames;
    private readonly int _run;
    private readonly int _holdSeconds;
    private readonly bool _useSilkFrameSource;
    private readonly double[] _frameTimes;
#if PROGPU_AVALONIA_BACKEND
    private readonly double[] _compileTimes;
    private readonly double[] _uploadTimes;
    private readonly double[] _renderTimes;
    private readonly double[] _compositorTimes;
#endif
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Action<TimeSpan> _animationFrameCallback;
    private readonly IDisposable _windowOpenedSubscription;
    private readonly long _startupTimestamp = Stopwatch.GetTimestamp();
    private Window? _window;
    private BenchmarkVisualFixture? _fixture;
    private TimeSpan _previousAnimationTimestamp;
    private int _warmupCompleted;
    private int _measuredFrames;
    private bool _attached;
    private bool _measurementArmed;
#if PROGPU_AVALONIA_BACKEND
    private bool _measurementActive;
#endif
    private bool _completed;
    private long _measurementStartTimestamp;
    private double _firstRenderedFrameMilliseconds;
    private long _allocatedBytesStart;
    private TimeSpan _cpuStart;
    private int _gen0Start;
    private int _gen1Start;
    private int _gen2Start;
#if PROGPU_AVALONIA_BACKEND
    private int _metricSamples;
    private double _compileMilliseconds;
    private double _uploadMilliseconds;
    private double _renderMilliseconds;
    private double _compositorMilliseconds;
    private int _sceneCacheHits;
    private CompositorMetrics _measurementStartMetrics;
    private CompositorMetrics _lastMetrics;
#endif

    private ControlCatalogTelemetrySession(
        string backend,
        string page,
        string textShaper,
        string outputPath,
        string? screenshotPath,
        string? readyPath,
        int warmupFrames,
        int measureFrames,
        int run,
        int holdSeconds)
    {
        _backend = backend;
        _page = page;
        _textShaper = textShaper;
        _outputPath = outputPath;
        _screenshotPath = screenshotPath;
        _readyPath = readyPath;
        _warmupFrames = warmupFrames;
        _measureFrames = measureFrames;
        _run = run;
        _holdSeconds = holdSeconds;
        _useSilkFrameSource = backend.Contains(
            "SilkNet",
            StringComparison.Ordinal);
        _frameTimes = new double[measureFrames];
#if PROGPU_AVALONIA_BACKEND
        _compileTimes = new double[measureFrames];
        _uploadTimes = new double[measureFrames];
        _renderTimes = new double[measureFrames];
        _compositorTimes = new double[measureFrames];
        ProGpuRenderingDiagnostics.FrameRendered +=
            OnProGpuFrameRendered;
        if (_useSilkFrameSource)
        {
            SilkNetPlatform.FramePreparing +=
                OnSilkFramePreparing;
        }
#endif
        _animationFrameCallback = OnAnimationFrame;
        _windowOpenedSubscription =
            Window.WindowOpenedEvent.AddClassHandler<MainWindow>(
                OnMainWindowOpened);
        ControlCatalogBenchmarkEventSource.Log.WorkloadStarted(page);
    }

    public static ControlCatalogTelemetrySession? TryStart(
        string backend,
        string? page,
        string textShaper)
    {
        string? outputPath =
            Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return null;
        }

        return new ControlCatalogTelemetrySession(
            backend,
            string.IsNullOrWhiteSpace(page) ? "Default" : page,
            textShaper,
            Path.GetFullPath(outputPath),
            ReadOptionalPath(ScreenshotVariable),
            ReadOptionalPath(ReadyVariable),
            ReadPositiveInt(WarmupVariable, 120),
            ReadPositiveInt(MeasureVariable, 300),
            ReadPositiveInt(RunVariable, 1),
            ReadNonNegativeInt(HoldSecondsVariable, 0));
    }

    public void Attach()
    {
        if (_attached)
        {
            throw new InvalidOperationException(
                "The ControlCatalog telemetry session was attached twice.");
        }

        _attached = true;
    }

    private void OnMainWindowOpened(
        MainWindow window,
        RoutedEventArgs _)
    {
        if (!_attached || _window is not null)
        {
            return;
        }

        _window = window;
        _fixture = BenchmarkVisualFixture.Create(window);
        if (!_useSilkFrameSource)
        {
            window.RequestAnimationFrame(_animationFrameCallback);
        }
    }

#if PROGPU_AVALONIA_BACKEND
    private void OnSilkFramePreparing()
    {
        if (_completed || _window is null)
        {
            return;
        }

        _fixture?.Pulse();
        OnAnimationFrame(
            Stopwatch.GetElapsedTime(_startupTimestamp));
    }
#endif

    private void OnAnimationFrame(TimeSpan timestamp)
    {
        if (_completed || _window is null)
        {
            return;
        }

        if (_previousAnimationTimestamp == default)
        {
            _firstRenderedFrameMilliseconds =
                Stopwatch.GetElapsedTime(_startupTimestamp)
                    .TotalMilliseconds;
            _previousAnimationTimestamp = timestamp;
            RequestNextFrame();
            return;
        }

        if (_warmupCompleted < _warmupFrames)
        {
            _warmupCompleted++;
            _previousAnimationTimestamp = timestamp;
            if (_warmupCompleted == _warmupFrames)
            {
                PrepareMeasurement();
            }
            RequestNextFrame();
            return;
        }

        if (_measurementArmed)
        {
            _measurementArmed = false;
#if PROGPU_AVALONIA_BACKEND
            _measurementActive = true;
#endif
            _fixture?.ActivateMeasurementMutations();
            _previousAnimationTimestamp = timestamp;
            _measurementStartTimestamp = Stopwatch.GetTimestamp();
            ControlCatalogBenchmarkEventSource.Log.MeasurementStarted(
                _page);
            RequestNextFrame();
            return;
        }

        double frameMilliseconds =
            Math.Max(
                0d,
                (timestamp - _previousAnimationTimestamp)
                    .TotalMilliseconds);
        _previousAnimationTimestamp = timestamp;
        _frameTimes[_measuredFrames] = frameMilliseconds;
        _measuredFrames++;
        if (_measuredFrames < _measureFrames)
        {
            _fixture?.Advance(_measuredFrames);
            RequestNextFrame();
            return;
        }

#if PROGPU_AVALONIA_BACKEND
        _measurementActive = false;
#endif
        _completed = true;
        Dispatcher.UIThread.Post(
            Complete,
            DispatcherPriority.Background);
    }

    private void PrepareMeasurement()
    {
        CollectRetainedMemory();
        _allocatedBytesStart =
            GC.GetTotalAllocatedBytes(precise: true);
        _cpuStart = _process.TotalProcessorTime;
        _gen0Start = GC.CollectionCount(0);
        _gen1Start = GC.CollectionCount(1);
        _gen2Start = GC.CollectionCount(2);
#if PROGPU_AVALONIA_BACKEND
        _measurementStartMetrics = _lastMetrics;
#endif
        _measurementArmed = true;
        _previousAnimationTimestamp = default;
    }

    private void RequestNextFrame()
    {
#if PROGPU_AVALONIA_BACKEND
        if (_useSilkFrameSource)
        {
            return;
        }
#endif
        _fixture?.Pulse();
        _window?.RequestAnimationFrame(_animationFrameCallback);
    }

#if PROGPU_AVALONIA_BACKEND
    private void OnProGpuFrameRendered(CompositorMetrics metrics)
    {
        _lastMetrics = metrics;
        if (!_measurementActive ||
            _metricSamples >= _measureFrames)
        {
            return;
        }

        _compileTimes[_metricSamples] =
            metrics.VisualTreeCompileTimeMs;
        _uploadTimes[_metricSamples] =
            metrics.GpuUploadTimeMs;
        _renderTimes[_metricSamples] =
            metrics.RenderPassTimeMs;
        _compositorTimes[_metricSamples] =
            metrics.FrameTimeMs;
        _compileMilliseconds +=
            metrics.VisualTreeCompileTimeMs;
        _uploadMilliseconds += metrics.GpuUploadTimeMs;
        _renderMilliseconds += metrics.RenderPassTimeMs;
        _compositorMilliseconds += metrics.FrameTimeMs;
        if (metrics.SceneCacheHit)
        {
            _sceneCacheHits++;
        }
        _metricSamples++;
    }
#endif

    private void Complete()
    {
        ControlCatalogBenchmarkEventSource.Log.MeasurementStopped(
            _page);
        long measurementEndTimestamp = Stopwatch.GetTimestamp();
        long allocatedBytes = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: true) -
            _allocatedBytesStart);
        TimeSpan cpu = _process.TotalProcessorTime - _cpuStart;
        double elapsedSeconds = Math.Max(
            Stopwatch.GetElapsedTime(
                    _measurementStartTimestamp,
                    measurementEndTimestamp)
                .TotalSeconds,
            double.Epsilon);
        Distribution frameTime =
            Distribution.Calculate(_frameTimes, _measuredFrames);

        CollectRetainedMemory();
        _process.Refresh();
        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
        DesktopProcessMemory memory =
            DesktopProcessMemory.Capture(_process);

#if PROGPU_AVALONIA_BACKEND
        Distribution compileTime =
            Distribution.Calculate(_compileTimes, _metricSamples);
        Distribution uploadTime =
            Distribution.Calculate(_uploadTimes, _metricSamples);
        Distribution renderTime =
            Distribution.Calculate(_renderTimes, _metricSamples);
        Distribution compositorTime =
            Distribution.Calculate(_compositorTimes, _metricSamples);
        WgpuNativeResourceSnapshot nativeGpu = default;
        _ = WgpuContext.TryGetFirstActiveContext(
                out WgpuContext? context) &&
            context.TryCaptureNativeResourceSnapshot(out nativeGpu);
#endif

        string? outputDirectory =
            Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using (FileStream output = File.Create(_outputPath))
        using (var writer = new Utf8JsonWriter(
                   output,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("SchemaVersion", SchemaVersion);
            writer.WriteString("Backend", _backend);
            writer.WriteString("TextShaper", _textShaper);
            writer.WriteString("Page", _page);
            writer.WriteNumber("Run", _run);
            writer.WriteNumber("WarmupFrames", _warmupFrames);
            writer.WriteNumber("MeasuredFrames", _measuredFrames);
            writer.WriteNumber("ElapsedSeconds", elapsedSeconds);
            writer.WriteNumber(
                "FramesPerSecond",
                _measuredFrames / elapsedSeconds);
            writer.WriteNumber(
                "AverageFrameMs",
                Average(_frameTimes, _measuredFrames));
            WriteDistribution(writer, "Frame", frameTime);
            writer.WriteNumber(
                "ProcessCpuSeconds",
                cpu.TotalSeconds);
            writer.WriteNumber(
                "ProcessCpuPercent",
                cpu.TotalSeconds / elapsedSeconds /
                Math.Max(1, Environment.ProcessorCount) * 100d);
            writer.WriteNumber("AllocatedBytes", allocatedBytes);
            writer.WriteNumber(
                "AllocatedBytesPerFrame",
                (double)allocatedBytes / _measuredFrames);
            writer.WriteNumber(
                "ManagedBytes",
                GC.GetTotalMemory(forceFullCollection: false));
            writer.WriteNumber(
                "GcCommittedBytes",
                gcInfo.TotalCommittedBytes);
            writer.WriteNumber(
                "ManagedFragmentedBytes",
                gcInfo.FragmentedBytes);
            writer.WriteNumber(
                "ProcessWorkingSetBytes",
                _process.WorkingSet64);
            writer.WriteNumber(
                "ProcessResidentBytes",
                memory.ResidentBytes);
            writer.WriteNumber(
                "ProcessWiredBytes",
                memory.WiredBytes);
            writer.WriteNumber(
                "ProcessPhysicalFootprintBytes",
                memory.PhysicalFootprintBytes);
            writer.WriteNumber(
                "ProcessPeakPhysicalFootprintBytes",
                memory.LifetimeMaxPhysicalFootprintBytes);
            writer.WriteNumber(
                "Gen0Collections",
                GC.CollectionCount(0) - _gen0Start);
            writer.WriteNumber(
                "Gen1Collections",
                GC.CollectionCount(1) - _gen1Start);
            writer.WriteNumber(
                "Gen2Collections",
                GC.CollectionCount(2) - _gen2Start);
            writer.WriteNumber(
                "FirstRenderedFrameMs",
                _firstRenderedFrameMilliseconds);
            WriteWindowMetrics(writer);
#if PROGPU_AVALONIA_BACKEND
            WriteProGpuMetrics(
                writer,
                compileTime,
                uploadTime,
                renderTime,
                compositorTime,
                nativeGpu);
#else
            WriteReferenceMetrics(writer);
#endif
            writer.WriteEndObject();
            writer.Flush();
        }

        CaptureScreenshot();
        SignalDiagnosticHold();
        Environment.Exit(0);
    }

#if PROGPU_AVALONIA_BACKEND
    private void WriteProGpuMetrics(
        Utf8JsonWriter writer,
        Distribution compileTime,
        Distribution uploadTime,
        Distribution renderTime,
        Distribution compositorTime,
        WgpuNativeResourceSnapshot nativeGpu)
    {
        writer.WriteNumber(
            "AverageCompileMs",
            Average(_compileMilliseconds, _metricSamples));
        writer.WriteNumber(
            "AverageUploadMs",
            Average(_uploadMilliseconds, _metricSamples));
        writer.WriteNumber(
            "AverageRenderMs",
            Average(_renderMilliseconds, _metricSamples));
        writer.WriteNumber(
            "AverageCompositorMs",
            Average(_compositorMilliseconds, _metricSamples));
        writer.WriteNumber(
            "RenderTargetWidth",
            _lastMetrics.RenderTargetWidth);
        writer.WriteNumber(
            "RenderTargetHeight",
            _lastMetrics.RenderTargetHeight);
        writer.WriteNumber(
            "DpiScale",
            _lastMetrics.DpiScale);
        writer.WriteNumber(
            "CompositorMetricSampleCount",
            _metricSamples);
        WriteDistribution(writer, "Compile", compileTime);
        WriteDistribution(writer, "Upload", uploadTime);
        WriteDistribution(writer, "Render", renderTime);
        WriteDistribution(writer, "Compositor", compositorTime);
        writer.WriteNumber("SceneCacheHits", _sceneCacheHits);
        writer.WriteNumber(
            "DrawCalls",
            _lastMetrics.DrawCallsCount);
        writer.WriteNumber(
            "RecordedCommands",
            _lastMetrics.RecordedCommandCount);
        writer.WriteNumber(
            "VectorVertices",
            _lastMetrics.VectorVerticesCount);
        writer.WriteNumber(
            "TextVertices",
            _lastMetrics.TextVerticesCount);
        writer.WriteNumber(
            "PathAtlasEntries",
            _lastMetrics.PathAtlasCachedCount);
        writer.WriteNumber(
            "PathAtlasTextureBytes",
            _lastMetrics.PathAtlasTextureBytes);
        writer.WriteNumber(
            "GlyphAtlasTextureBytes",
            _lastMetrics.GlyphAtlasTextureBytes);
        writer.WriteNumber(
            "ColorGlyphAtlasTextureBytes",
            _lastMetrics.ColorGlyphAtlasTextureBytes);
        writer.WriteNumber(
            "TrackedIntermediateTextureBytes",
            _lastMetrics.TrackedIntermediateTextureBytes);
        writer.WriteNumber(
            "MetalAllocatedBytes",
            nativeGpu.MetalAllocatedBytes);
        writer.WriteString(
            "PresentationPath",
            _lastMetrics.PresentationPath ?? "Unavailable");
        writer.WriteNumber(
            "RetainedCompositionScenes",
            _lastMetrics.RetainedCompositionSceneCount);
        writer.WriteNumber(
            "RetainedCompositionServerBackendRenders",
            _lastMetrics
                .RetainedCompositionServerBackendRenderCount);
        writer.WriteNumber(
            "RetainedCompositionSceneNodes",
            _lastMetrics.RetainedCompositionSceneNodeCount);
        writer.WriteNumber(
            "RetainedCompositionFallbackNodes",
            _lastMetrics.RetainedCompositionFallbackNodeCount);
        writer.WriteNumber(
            "RetainedCompositionCustomVisualNodes",
            _lastMetrics.RetainedCompositionCustomVisualNodeCount);
        writer.WriteNumber(
            "RetainedCompositionCustomVisualCompilations",
            _lastMetrics
                .RetainedCompositionCustomVisualCompilations);
        writer.WriteNumber(
            "RetainedCompositionPictureHits",
            _lastMetrics.RetainedCompositionPictureHits);
        writer.WriteNumber(
            "RetainedCompositionPictureCompilations",
            _lastMetrics.RetainedCompositionPictureCompilations);
        writer.WriteNumber(
            "RetainedCompositionLayoutClipSynchronizations",
            _lastMetrics
                .RetainedCompositionLayoutClipSynchronizations);
        writer.WriteNumber(
            "RetainedCompositionGeometryClipSynchronizations",
            _lastMetrics
                .RetainedCompositionGeometryClipSynchronizations);
        writer.WriteNumber(
            "RetainedCompositionBitmapCacheSynchronizations",
            _lastMetrics
                .RetainedCompositionBitmapCacheSynchronizations);
        writer.WriteNumber(
            "RetainedCompositionEffectSynchronizations",
            _lastMetrics
                .RetainedCompositionEffectSynchronizations);
        writer.WriteNumber(
            "RetainedCompositionOpacityMaskSynchronizations",
            _lastMetrics
                .RetainedCompositionOpacityMaskSynchronizations);
        writer.WriteNumber(
            "RetainedCompositionInheritedDrawingOptionsSynchronizations",
            _lastMetrics
                .RetainedCompositionInheritedDrawingOptionsSynchronizations);
        writer.WriteNumber(
            "RetainedCompositionTopologySynchronizationsDuringMeasurement",
            Delta(
                _lastMetrics
                    .RetainedCompositionTopologySynchronizations,
                _measurementStartMetrics
                    .RetainedCompositionTopologySynchronizations));
        writer.WriteNumber(
            "RetainedCompositionAdornerSynchronizationsDuringMeasurement",
            Delta(
                _lastMetrics
                    .RetainedCompositionAdornerSynchronizations,
                _measurementStartMetrics
                    .RetainedCompositionAdornerSynchronizations));
        writer.WriteNumber(
            "RetainedCompositionSceneFullSynchronizationsDuringMeasurement",
            Delta(
                _lastMetrics
                    .RetainedCompositionSceneFullSynchronizations,
                _measurementStartMetrics
                    .RetainedCompositionSceneFullSynchronizations));
    }
#endif

    private static void WriteReferenceMetrics(
        Utf8JsonWriter writer)
    {
        writer.WriteNumber("AverageCompileMs", 0d);
        writer.WriteNumber("AverageUploadMs", 0d);
        writer.WriteNumber("AverageRenderMs", 0d);
        writer.WriteNumber("AverageCompositorMs", 0d);
        writer.WriteNumber("CompositorMetricSampleCount", 0);
        writer.WriteNumber("DrawCalls", 0);
        writer.WriteNumber("RecordedCommands", 0);
        writer.WriteString("PresentationPath", "Skia");
    }

    private void WriteWindowMetrics(Utf8JsonWriter writer)
    {
        if (_window is null)
        {
            return;
        }

        double scaling = _window.RenderScaling;
        writer.WriteNumber(
            "WindowLogicalWidth",
            _window.Bounds.Width);
        writer.WriteNumber(
            "WindowLogicalHeight",
            _window.Bounds.Height);
        writer.WriteNumber("WindowRenderScaling", scaling);
        writer.WriteNumber(
            "WindowPhysicalWidth",
            Math.Max(
                1,
                checked((int)Math.Ceiling(
                    _window.Bounds.Width * scaling))));
        writer.WriteNumber(
            "WindowPhysicalHeight",
            Math.Max(
                1,
                checked((int)Math.Ceiling(
                    _window.Bounds.Height * scaling))));
    }

    private void CaptureScreenshot()
    {
        if (_window is null ||
            string.IsNullOrWhiteSpace(_screenshotPath))
        {
            return;
        }

        // RenderTargetBitmap.Render records in the visual's logical coordinate
        // space. Keep deterministic catalog captures at one pixel per logical
        // unit; the live-surface telemetry independently validates the native
        // physical framebuffer size and render scaling.
        int width = Math.Max(
            1,
            checked((int)Math.Ceiling(_window.Bounds.Width)));
        int height = Math.Max(
            1,
            checked((int)Math.Ceiling(_window.Bounds.Height)));
        string path = Path.GetFullPath(_screenshotPath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var bitmap = new RenderTargetBitmap(
            new PixelSize(width, height),
            new Vector(96d, 96d));
        bitmap.Render(_window);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    private void SignalDiagnosticHold()
    {
        if (!string.IsNullOrWhiteSpace(_readyPath))
        {
            string path = Path.GetFullPath(_readyPath);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(
                path,
                _process.Id.ToString(CultureInfo.InvariantCulture));
        }

        if (_holdSeconds > 0)
        {
            ControlCatalogBenchmarkEventSource.Log
                .SnapshotHoldStarted(_page);
            Thread.Sleep(TimeSpan.FromSeconds(_holdSeconds));
        }
    }

    private static void WriteDistribution(
        Utf8JsonWriter writer,
        string prefix,
        Distribution value)
    {
        writer.WriteNumber(
            $"{prefix}TimeSampleCount",
            value.Count);
        writer.WriteNumber($"Min{prefix}Ms", value.Minimum);
        writer.WriteNumber(
            $"Median{prefix}Ms",
            value.Median);
        writer.WriteNumber($"P95{prefix}Ms", value.P95);
        writer.WriteNumber($"P99{prefix}Ms", value.P99);
        writer.WriteNumber($"Max{prefix}Ms", value.Maximum);
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

    private static string? ReadOptionalPath(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Path.GetFullPath(value);
    }

    private static int ReadPositiveInt(
        string name,
        int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(
                   value,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out int result) &&
               result > 0
            ? result
            : fallback;
    }

    private static int ReadNonNegativeInt(
        string name,
        int fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(
                   value,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out int result) &&
               result >= 0
            ? result
            : fallback;
    }

    private static double Average(double total, int count) =>
        count == 0 ? 0d : total / count;

    private static double Average(double[] values, int count)
    {
        double total = 0d;
        for (int index = 0; index < count; index++)
        {
            total += values[index];
        }
        return Average(total, count);
    }

#if PROGPU_AVALONIA_BACKEND
    private static long Delta(long current, long baseline) =>
        Math.Max(0, current - baseline);
#endif

    public void Dispose()
    {
        _windowOpenedSubscription.Dispose();
#if PROGPU_AVALONIA_BACKEND
        ProGpuRenderingDiagnostics.FrameRendered -=
            OnProGpuFrameRendered;
        if (_useSilkFrameSource)
        {
            SilkNetPlatform.FramePreparing -=
                OnSilkFramePreparing;
        }
#endif
        _process.Dispose();
    }

    private readonly record struct Distribution(
        int Count,
        double Minimum,
        double Median,
        double P95,
        double P99,
        double Maximum)
    {
        public static Distribution Calculate(
            double[] values,
            int count)
        {
            count = Math.Clamp(count, 0, values.Length);
            if (count == 0)
            {
                return default;
            }

            Array.Sort(values, 0, count);
            return new Distribution(
                count,
                values[0],
                Percentile(values, count, 0.50d),
                Percentile(values, count, 0.95d),
                Percentile(values, count, 0.99d),
                values[count - 1]);
        }

        private static double Percentile(
            double[] sortedValues,
            int count,
            double percentile)
        {
            double position = (count - 1) * percentile;
            int lower = (int)position;
            int upper = Math.Min(count - 1, lower + 1);
            double fraction = position - lower;
            return sortedValues[lower] +
                ((sortedValues[upper] - sortedValues[lower]) *
                 fraction);
        }
    }
}

internal sealed class BenchmarkVisualFixture
{
    private const string CustomVisualVariable =
        "PROGPU_AVALONIA_BENCHMARK_CUSTOM_VISUAL";
    private const string LayoutClipVariable =
        "PROGPU_AVALONIA_BENCHMARK_LAYOUT_CLIP";
    private const string GeometryClipVariable =
        "PROGPU_AVALONIA_BENCHMARK_GEOMETRY_CLIP";
    private const string RootGeometryClipVariable =
        "PROGPU_AVALONIA_BENCHMARK_ROOT_GEOMETRY_CLIP";
    private const string RootAliasedTextVariable =
        "PROGPU_AVALONIA_BENCHMARK_ROOT_ALIASED_TEXT";
    private const string RootConicOpacityMaskVariable =
        "PROGPU_AVALONIA_BENCHMARK_ROOT_CONIC_OPACITY_MASK";
    private const string BitmapCacheVariable =
        "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_CHANNEL";
    private const string BitmapCacheScaleVariable =
        "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_SCALE";
    private const string BitmapCacheSnapVariable =
        "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_SNAP";
    private const string BitmapCacheClearTypeVariable =
        "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_CLEARTYPE";
    private const string EffectVariable =
        "PROGPU_AVALONIA_BENCHMARK_EFFECT_CHANNEL";
    private const string TextBlurEffectVariable =
        "PROGPU_AVALONIA_BENCHMARK_TEXT_BLUR_EFFECT";
    private const string TextDropShadowEffectVariable =
        "PROGPU_AVALONIA_BENCHMARK_TEXT_DROP_SHADOW_EFFECT";
    private const string OpacityMaskVariable =
        "PROGPU_AVALONIA_BENCHMARK_OPACITY_MASK_CHANNEL";
    private const string DrawingOptionsVariable =
        "PROGPU_AVALONIA_BENCHMARK_INHERITED_DRAWING_OPTIONS_CHANNEL";
    private const string TopologyVariable =
        "PROGPU_AVALONIA_BENCHMARK_TOPOLOGY_CHANNEL";
    private const string AdornerVariable =
        "PROGPU_AVALONIA_BENCHMARK_ADORNER_CHANNEL";
    private const string SkiaSharpCustomDrawVariable =
        "PROGPU_AVALONIA_BENCHMARK_SKIASHARP_CUSTOM_DRAW";

    private readonly Visual _target;
    private readonly Panel? _panel;
    private readonly bool _customVisual;
    private readonly bool _geometryClip;
    private readonly bool _drawingOptions;
    private readonly bool _topology;
    private readonly bool _adorner;
    private readonly BenchmarkPulseControl? _pulse;
    private readonly BenchmarkSkiaSharpControl? _skiaSharpControl;
    private readonly Panel? _topologyFirstParent;
    private readonly Panel? _topologySecondParent;
    private Visual? _adornerFirstTarget;
    private Visual? _adornerSecondTarget;
    private Border? _topologyChild;
    private Border? _adornerVisual;
    private bool _customVisualSelected;
    private bool _pulsePhase;
    private bool _topologyUsesFirstParent = true;
    private bool _adornerUsesFirstTarget = true;

    private BenchmarkVisualFixture(
        Visual target,
        Panel? panel,
        BenchmarkPulseControl? pulse,
        bool customVisual,
        bool geometryClip,
        bool drawingOptions,
        bool topology,
        bool adorner,
        Panel? topologyFirstParent,
        Panel? topologySecondParent,
        Border? topologyChild,
        Border? adornerVisual,
        Visual? adornerFirstTarget,
        Visual? adornerSecondTarget,
        BenchmarkSkiaSharpControl? skiaSharpControl)
    {
        _target = target;
        _panel = panel;
        _pulse = pulse;
        _customVisual = customVisual;
        _geometryClip = geometryClip;
        _drawingOptions = drawingOptions;
        _topology = topology;
        _adorner = adorner;
        _topologyFirstParent = topologyFirstParent;
        _topologySecondParent = topologySecondParent;
        _topologyChild = topologyChild;
        _adornerVisual = adornerVisual;
        _adornerFirstTarget = adornerFirstTarget;
        _adornerSecondTarget = adornerSecondTarget;
        _skiaSharpControl = skiaSharpControl;
    }

    public static BenchmarkVisualFixture? Create(Window window)
    {
        bool customVisual = ReadEnabled(CustomVisualVariable);
        bool layoutClip = ReadEnabled(LayoutClipVariable);
        bool geometryClip = ReadEnabled(GeometryClipVariable);
        bool rootGeometryClip =
            ReadEnabled(RootGeometryClipVariable);
        bool rootAliasedText =
            ReadEnabled(RootAliasedTextVariable);
        bool rootConicOpacityMask =
            ReadEnabled(RootConicOpacityMaskVariable);
        bool bitmapCache = ReadEnabled(BitmapCacheVariable);
        double? bitmapCacheScale =
            ReadOptionalDouble(BitmapCacheScaleVariable);
        bool? bitmapCacheSnap =
            ReadOptionalBoolean(BitmapCacheSnapVariable);
        bool? bitmapCacheClearType =
            ReadOptionalBoolean(BitmapCacheClearTypeVariable);
        bool effect = ReadEnabled(EffectVariable);
        bool textBlurEffect =
            ReadEnabled(TextBlurEffectVariable);
        bool textDropShadowEffect =
            ReadEnabled(TextDropShadowEffectVariable);
        bool opacityMask = ReadEnabled(OpacityMaskVariable);
        bool drawingOptions = ReadEnabled(DrawingOptionsVariable);
        bool topology = ReadEnabled(TopologyVariable);
        bool adorner = ReadEnabled(AdornerVariable);
        bool skiaSharpCustomDraw =
            ReadEnabled(SkiaSharpCustomDrawVariable);
        Visual target = window.Content as Visual ?? window;
        if (layoutClip)
        {
            target.ClipToBounds = true;
        }
        if (rootGeometryClip)
        {
            target.Clip = new EllipseGeometry(
                new Rect(24, 24, 160, 120));
            Console.Error.WriteLine(
                "[ControlCatalog] root elliptical geometry clip fixture 160x120");
        }
        else if (geometryClip)
        {
            target.Clip = new RectangleGeometry(
                new Rect(
                    1,
                    1,
                    Math.Max(1, window.ClientSize.Width - 2),
                    Math.Max(1, window.ClientSize.Height - 2)));
        }
        if (bitmapCache ||
            bitmapCacheScale.HasValue ||
            bitmapCacheSnap.HasValue ||
            bitmapCacheClearType.HasValue)
        {
            double scale = bitmapCacheScale ?? 1;
            bool snap = bitmapCacheSnap ?? bitmapCache;
            bool clearType = bitmapCacheClearType ?? false;
            target.CacheMode = new BitmapCache
            {
                RenderAtScale = scale,
                SnapsToDevicePixels = snap,
                EnableClearType = clearType
            };
            Console.Error.WriteLine(
                FormattableString.Invariant(
                    $"[ControlCatalog] bitmap cache fixture scale={scale:G} snap={snap} clearType={clearType}"));
        }
        if (textBlurEffect)
        {
            target.Effect = new BlurEffect { Radius = 4 };
            Console.Error.WriteLine(
                "[ControlCatalog] text effect fixture blur radius=4");
        }
        else if (textDropShadowEffect)
        {
            target.Effect = new DropShadowEffect
            {
                OffsetX = 6,
                OffsetY = 4,
                BlurRadius = 4,
                Color = Colors.DeepSkyBlue,
                Opacity = 0.75
            };
            Console.Error.WriteLine(
                "[ControlCatalog] text effect fixture drop-shadow offset=6,4 blur=4");
        }
        else if (effect)
        {
            target.Effect = new BlurEffect { Radius = 0.25 };
        }
        if (rootConicOpacityMask)
        {
            target.OpacityMask = new ConicGradientBrush
            {
                Angle = 23,
                GradientStops =
                {
                    new GradientStop(Colors.White, 0),
                    new GradientStop(
                        Color.FromArgb(64, 255, 255, 255),
                        0.5),
                    new GradientStop(Colors.White, 1)
                }
            };
            Console.Error.WriteLine(
                "[ControlCatalog] root conic opacity mask fixture angle=23");
        }
        else if (opacityMask)
        {
            target.OpacityMask = Brushes.White;
        }
        if (rootAliasedText)
        {
            TextOptions.SetTextRenderingMode(
                target,
                Avalonia.Media.TextRenderingMode.Alias);
            Console.Error.WriteLine(
                "[ControlCatalog] root inherited aliased-text fixture");
        }
        if (drawingOptions)
        {
            RenderOptions.SetBitmapInterpolationMode(
                target,
                BitmapInterpolationMode.HighQuality);
        }

        Panel? panel = target
            .GetSelfAndVisualDescendants()
            .OfType<Panel>()
            .FirstOrDefault();
        BenchmarkPulseControl? pulse = null;
        if (panel is not null)
        {
            pulse = new BenchmarkPulseControl();
            panel.Children.Add(pulse);
        }

        BenchmarkSkiaSharpControl? skiaSharpControl = null;
        if (skiaSharpCustomDraw && panel is not null)
        {
            skiaSharpControl = new BenchmarkSkiaSharpControl
            {
                Width = 320,
                Height = 240,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                IsHitTestVisible = false
            };
            panel.Children.Add(skiaSharpControl);
            Console.Error.WriteLine(
                "[ControlCatalog] deterministic SkiaSharp custom-draw fixture attached");
        }

        Panel? topologyFirstParent = null;
        Panel? topologySecondParent = null;
        Border? topologyChild = null;
        if (topology && panel is not null)
        {
            topologyFirstParent = new Canvas
            {
                Width = 2,
                Height = 2,
                IsHitTestVisible = false
            };
            topologySecondParent = new Canvas
            {
                Width = 2,
                Height = 2,
                IsHitTestVisible = false
            };
            topologyChild = new Border
            {
                Width = 1,
                Height = 1,
                Background = new SolidColorBrush(
                    Color.FromArgb(1, 0, 0, 0)),
                IsHitTestVisible = false
            };
            topologyFirstParent.Children.Add(topologyChild);
            panel.Children.Add(topologyFirstParent);
            panel.Children.Add(topologySecondParent);
        }

        Border? adornerVisual = null;
        Visual? adornerFirstTarget = null;
        Visual? adornerSecondTarget = null;

        return new BenchmarkVisualFixture(
            target,
            panel,
            pulse,
            customVisual,
            geometryClip,
            drawingOptions,
            topology,
            adorner,
            topologyFirstParent,
            topologySecondParent,
            topologyChild,
            adornerVisual,
            adornerFirstTarget,
            adornerSecondTarget,
            skiaSharpControl);
    }

    private static bool TrySelectCustomVisualTab(Visual root)
    {
        foreach (TabControl tabControl in root
                     .GetSelfAndVisualDescendants()
                     .OfType<TabControl>())
        {
            foreach (object? item in tabControl.Items)
            {
                if (item is TabItem tabItem &&
                    string.Equals(
                        tabItem.Header as string,
                        "Custom",
                        StringComparison.Ordinal))
                {
                    tabControl.SelectedItem = tabItem;
                    return true;
                }
            }
        }

        return false;
    }

    public void Pulse()
    {
        if (_adorner && _adornerVisual is null)
        {
            TryPrepareAdornerFixture();
        }

        if (_customVisual && !_customVisualSelected)
        {
            _customVisualSelected =
                TrySelectCustomVisualTab(_target);
        }

        if (_pulse is null)
        {
            return;
        }

        _pulsePhase = !_pulsePhase;
        _pulse.SetPhase(_pulsePhase);
        _skiaSharpControl?.SetPhase(_pulsePhase);
    }

    private void TryPrepareAdornerFixture()
    {
        foreach (Visual firstTarget in
                 _target.GetSelfAndVisualDescendants())
        {
            if (AdornerLayer.GetAdorner(firstTarget) is not
                    Border adorner ||
                AdornerLayer.GetAdornerLayer(firstTarget) is not
                    { } layer)
            {
                continue;
            }

            foreach (Visual secondTarget in
                     _target.GetSelfAndVisualDescendants())
            {
                if (ReferenceEquals(firstTarget, secondTarget) ||
                    secondTarget is not Control ||
                    !ReferenceEquals(
                        layer,
                        AdornerLayer.GetAdornerLayer(secondTarget)))
                {
                    continue;
                }

                _adornerVisual = adorner;
                _adornerFirstTarget = firstTarget;
                _adornerSecondTarget = secondTarget;
                return;
            }
        }
    }

    public void ActivateMeasurementMutations()
    {
        if (_geometryClip)
        {
            _target.Clip = new RectangleGeometry(
                new Rect(
                    1.25,
                    1.25,
                    Math.Max(1, _target.Bounds.Width - 2.5),
                    Math.Max(1, _target.Bounds.Height - 2.5)));
            Console.Error.WriteLine(
                "[ControlCatalog] typed retained geometry-clip channel fixture attached");
        }

        if (_drawingOptions)
        {
            RenderOptions.SetBitmapInterpolationMode(
                _target,
                BitmapInterpolationMode.LowQuality);
            Console.Error.WriteLine(
                "[ControlCatalog] inherited drawing-options channel fixture attached");
        }

        if (_topology && _panel is not null)
        {
            MoveTopologyChild();
            Console.Error.WriteLine(
                "[ControlCatalog] typed retained topology channel fixture");
        }

        if (_adorner && _adornerVisual is not null)
        {
            MoveAdornerTarget();
            Console.Error.WriteLine(
                "[ControlCatalog] typed retained adorner channel fixture");
        }
    }

    public void Advance(int measuredFrame)
    {
        if (_topology)
        {
            MoveTopologyChild();
        }
        if (_adorner)
        {
            MoveAdornerTarget();
        }

        if (measuredFrame != 2)
        {
            return;
        }

        _pulse?.InvalidateVisual();
    }

    private void MoveTopologyChild()
    {
        if (_topologyChild is null ||
            _topologyFirstParent is null ||
            _topologySecondParent is null)
        {
            return;
        }

        Panel source = _topologyUsesFirstParent
            ? _topologyFirstParent
            : _topologySecondParent;
        Panel destination = _topologyUsesFirstParent
            ? _topologySecondParent
            : _topologyFirstParent;
        source.Children.Remove(_topologyChild);
        destination.Children.Add(_topologyChild);
        _topologyUsesFirstParent = !_topologyUsesFirstParent;
    }

    private void MoveAdornerTarget()
    {
        if (_adornerVisual is null ||
            _adornerFirstTarget is null ||
            _adornerSecondTarget is null)
        {
            return;
        }

        _adornerUsesFirstTarget = !_adornerUsesFirstTarget;
        AdornerLayer.SetAdornedElement(
            _adornerVisual,
            _adornerUsesFirstTarget
                ? _adornerFirstTarget
                : _adornerSecondTarget);
    }

    private static bool ReadEnabled(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.Equals(
                   value,
                   "1",
                   StringComparison.Ordinal) ||
               bool.TryParse(value, out bool enabled) && enabled;
    }

    private static bool? ReadOptionalBoolean(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (string.Equals(value, "1", StringComparison.Ordinal))
        {
            return true;
        }
        if (string.Equals(value, "0", StringComparison.Ordinal))
        {
            return false;
        }
        return bool.TryParse(value, out bool result)
            ? result
            : null;
    }

    private static double? ReadOptionalDouble(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double result)
            ? result
            : null;
    }
}

internal sealed class BenchmarkPulseControl : Control
{
    private readonly IBrush _firstBrush =
        new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    private readonly IBrush _secondBrush =
        new SolidColorBrush(
            Color.FromArgb(1, 255, 255, 255));
    private bool _phase;

    public BenchmarkPulseControl()
    {
        Width = 1;
        Height = 1;
        IsHitTestVisible = false;
    }

    public void SetPhase(bool phase)
    {
        _phase = phase;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(
            _phase ? _firstBrush : _secondBrush,
            new Rect(Bounds.Size));
    }
}

internal sealed class BenchmarkSkiaSharpControl : Control
{
    private bool _phase;

    internal void SetPhase(bool phase)
    {
        _phase = phase;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.Custom(
            new BenchmarkSkiaSharpDrawOperation(
                new Rect(Bounds.Size),
                _phase));
    }

    private sealed class BenchmarkSkiaSharpDrawOperation :
        ICustomDrawOperation
    {
        private readonly bool _phase;

        internal BenchmarkSkiaSharpDrawOperation(
            Rect bounds,
            bool phase)
        {
            Bounds = bounds;
            _phase = phase;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point point) => false;

        public bool Equals(ICustomDrawOperation? other) =>
            other is BenchmarkSkiaSharpDrawOperation operation &&
            operation.Bounds == Bounds &&
            operation._phase == _phase;

        public void Render(ImmediateDrawingContext context)
        {
            ISkiaSharpApiLeaseFeature? feature =
                context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
                return;

            using ISkiaSharpApiLease lease = feature.Lease();
            SKCanvas canvas = lease.SkCanvas;
            using var fill = new SKPaint
            {
                Color = new SKColor(24, 116, 205, 208),
                IsAntialias = true
            };
            using var stroke = new SKPaint
            {
                Color = new SKColor(252, 190, 45),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f
            };
            using var pathBuilder = new SKPathBuilder();
            pathBuilder.MoveTo(0, 10);
            pathBuilder.QuadTo(18, -8, 34, 12);
            pathBuilder.CubicTo(44, 26, 58, -4, 72, 14);
            using SKPath path = pathBuilder.Detach();

            float phaseOffset = _phase ? 0.375f : 0f;
            for (int index = 0; index < 96; index++)
            {
                int save = canvas.Save();
                float x = (index % 12) * 25f + phaseOffset;
                float y = (index / 12) * 28f;
                canvas.Translate(x, y);
                canvas.ClipRect(new SKRect(0, 0, 24, 27));
                canvas.DrawRect(1, 2, 19, 17, fill);
                canvas.DrawCircle(12, 13, 7, stroke);
                canvas.Scale(0.28f);
                canvas.DrawPath(path, stroke);
                canvas.RestoreToCount(save);
            }
        }

        public void Dispose()
        {
        }
    }
}

internal readonly record struct DesktopProcessMemory(
    long ResidentBytes,
    long WiredBytes,
    long PhysicalFootprintBytes,
    long LifetimeMaxPhysicalFootprintBytes)
{
    public static DesktopProcessMemory Capture(Process process)
    {
        if (OperatingSystem.IsMacOS() &&
            MacProcessMemory.TryCapture(process.Id, out var result))
        {
            return result;
        }

        return new DesktopProcessMemory(
            process.WorkingSet64,
            0,
            process.WorkingSet64,
            process.PeakWorkingSet64);
    }
}

internal static unsafe class MacProcessMemory
{
    private const int RUsageInfoV4Flavor = 4;

    public static bool TryCapture(
        int processId,
        out DesktopProcessMemory result)
    {
        var usage = default(RUsageInfoV4);
        if (ProcPidRUsage(
                processId,
                RUsageInfoV4Flavor,
                ref usage) == 0)
        {
            result = new DesktopProcessMemory(
                checked((long)usage.ResidentSize),
                checked((long)usage.WiredSize),
                checked((long)usage.PhysicalFootprint),
                checked((long)usage.LifetimeMaxPhysicalFootprint));
            return true;
        }

        result = default;
        return false;
    }

    [DllImport(
        "/usr/lib/libproc.dylib",
        EntryPoint = "proc_pid_rusage")]
    private static extern int ProcPidRUsage(
        int processId,
        int flavor,
        ref RUsageInfoV4 buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct RUsageInfoV4
    {
        public fixed byte Uuid[16];
        public ulong UserTime;
        public ulong SystemTime;
        public ulong PackageIdleWakeups;
        public ulong InterruptWakeups;
        public ulong PageIns;
        public ulong WiredSize;
        public ulong ResidentSize;
        public ulong PhysicalFootprint;
        public ulong ProcessStartAbsoluteTime;
        public ulong ProcessExitAbsoluteTime;
        public ulong ChildUserTime;
        public ulong ChildSystemTime;
        public ulong ChildPackageIdleWakeups;
        public ulong ChildInterruptWakeups;
        public ulong ChildPageIns;
        public ulong ChildElapsedAbsoluteTime;
        public ulong DiskBytesRead;
        public ulong DiskBytesWritten;
        public ulong CpuTimeDefault;
        public ulong CpuTimeMaintenance;
        public ulong CpuTimeBackground;
        public ulong CpuTimeUtility;
        public ulong CpuTimeLegacy;
        public ulong CpuTimeUserInitiated;
        public ulong CpuTimeUserInteractive;
        public ulong BilledSystemTime;
        public ulong ServicedSystemTime;
        public ulong LogicalWrites;
        public ulong LifetimeMaxPhysicalFootprint;
        public ulong Instructions;
        public ulong Cycles;
        public ulong BilledEnergy;
        public ulong ServicedEnergy;
        public ulong IntervalMaxPhysicalFootprint;
        public ulong RunnableTime;
    }
}

[EventSource(Name = "ProGPU-ControlCatalog-Benchmark")]
internal sealed class ControlCatalogBenchmarkEventSource : EventSource
{
    public static readonly ControlCatalogBenchmarkEventSource Log =
        new();

    [Event(1, Level = EventLevel.Informational)]
    public void WorkloadStarted(string page) =>
        WriteEvent(1, page);

    [Event(2, Level = EventLevel.Informational)]
    public void MeasurementStarted(string page) =>
        WriteEvent(2, page);

    [Event(3, Level = EventLevel.Informational)]
    public void MeasurementStopped(string page) =>
        WriteEvent(3, page);

    [Event(4, Level = EventLevel.Informational)]
    public void SnapshotHoldStarted(string page) =>
        WriteEvent(4, page);
}
