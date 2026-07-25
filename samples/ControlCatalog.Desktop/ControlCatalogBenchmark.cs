using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
#if PROGPU_AVALONIA_BACKEND
using Avalonia.ProGpu;
using ProGPU.Scene;
#endif

namespace ControlCatalog.Desktop
{
    internal sealed class ControlCatalogBenchmark : IDisposable
    {
        private const string OutputVariable = "PROGPU_AVALONIA_BENCHMARK_OUTPUT";
        private const string ScreenshotVariable = "PROGPU_AVALONIA_BENCHMARK_SCREENSHOT";
        private const string WarmupVariable = "PROGPU_AVALONIA_BENCHMARK_WARMUP_FRAMES";
        private const string MeasureVariable = "PROGPU_AVALONIA_BENCHMARK_MEASURE_FRAMES";
        private const string WholeFontsVariable = "PROGPU_AVALONIA_BENCHMARK_WHOLE_FONTS";
        private const string WholeFontsSwitch = "ProGPU.Avalonia.Diagnostics.UseWholeSystemFontFiles";

        private readonly string _backend;
        private readonly string _page;
        private readonly string _outputPath;
        private readonly string? _screenshotPath;
        private readonly int _warmupFrames;
        private readonly int _measureFrames;
        private readonly Process _process;
        private readonly long _startupTimestamp = Stopwatch.GetTimestamp();
        private readonly Stopwatch _measurementClock = new();
        private readonly Action<TimeSpan> _animationFrameCallback;
        private Window? _window;
        private int _totalFrames;
        private int _measuredFrames;
        private int _completed;
        private long _allocatedBytesStart;
        private TimeSpan _cpuStart;
        private int _gen0Start;
        private int _gen1Start;
        private int _gen2Start;
        private double _frameMs;
        private double _maxFrameMs;
        private long _lastAnimationTimestamp;
#if PROGPU_AVALONIA_BACKEND
        private int _metricSamples;
        private double _compileMs;
        private double _uploadMs;
        private double _renderMs;
        private double _compositorMs;
        private double _maxCompileMs;
        private int _sceneCacheHits;
        private double _firstRenderedFrameMs;
        private CompositorMetrics _lastMetrics;
#endif

        private ControlCatalogBenchmark(
            string backend,
            string page,
            string outputPath,
            string? screenshotPath,
            int warmupFrames,
            int measureFrames)
        {
            _backend = backend;
            _page = page;
            _outputPath = outputPath;
            _screenshotPath = screenshotPath;
            _warmupFrames = warmupFrames;
            _measureFrames = measureFrames;
            _process = Process.GetCurrentProcess();
            _animationFrameCallback = OnAnimationFrame;
#if PROGPU_AVALONIA_BACKEND
            ProGpuRenderingDiagnostics.FrameRendered += OnProGpuFrameRendered;
#endif
            AvaloniaBenchmarkEventSource.Log.WorkloadStarted(page);
        }

        public static ControlCatalogBenchmark? TryStart(string backend, string? page)
        {
            var outputPath = Environment.GetEnvironmentVariable(OutputVariable);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return null;
            }

            if (ReadBoolean(WholeFontsVariable))
            {
                AppContext.SetSwitch(WholeFontsSwitch, true);
            }

            var screenshotPath = Environment.GetEnvironmentVariable(ScreenshotVariable);
            return new ControlCatalogBenchmark(
                backend,
                string.IsNullOrWhiteSpace(page) ? "Home" : page,
                Path.GetFullPath(outputPath),
                string.IsNullOrWhiteSpace(screenshotPath) ? null : Path.GetFullPath(screenshotPath),
                ReadPositiveInt(WarmupVariable, 120),
                ReadPositiveInt(MeasureVariable, 300));
        }

        public void Attach()
        {
            Dispatcher.UIThread.Post(TryAttach, DispatcherPriority.Loaded);
        }

        private void TryAttach()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime ||
                lifetime.MainWindow is not { } window)
            {
                Dispatcher.UIThread.Post(TryAttach, DispatcherPriority.Background);
                return;
            }

            _window = window;
            window.RequestAnimationFrame(_animationFrameCallback);
        }

        private void OnAnimationFrame(TimeSpan timestamp)
        {
            if (Volatile.Read(ref _completed) != 0 || _window is not { } window)
            {
                return;
            }

            window.InvalidateVisual();
            _totalFrames++;

            long timestampTicks = timestamp.Ticks;
            if (_lastAnimationTimestamp != 0 && _totalFrames > _warmupFrames)
            {
                double elapsedMilliseconds =
                    (timestampTicks - _lastAnimationTimestamp) * 1000d / TimeSpan.TicksPerSecond;
                _frameMs += elapsedMilliseconds;
                _maxFrameMs = Math.Max(_maxFrameMs, elapsedMilliseconds);
            }
            _lastAnimationTimestamp = timestampTicks;

            if (_totalFrames == _warmupFrames)
            {
                CollectRetainedMemory();
                _allocatedBytesStart = GC.GetTotalAllocatedBytes(precise: true);
                _cpuStart = _process.TotalProcessorTime;
                _gen0Start = GC.CollectionCount(0);
                _gen1Start = GC.CollectionCount(1);
                _gen2Start = GC.CollectionCount(2);
                _measurementClock.Restart();
                AvaloniaBenchmarkEventSource.Log.MeasurementStarted(_page);
            }
            else if (_totalFrames > _warmupFrames)
            {
                _measuredFrames++;
                if (_measuredFrames == _measureFrames &&
                    Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    Complete();
                    return;
                }
            }

            window.RequestAnimationFrame(_animationFrameCallback);
        }

#if PROGPU_AVALONIA_BACKEND
        private void OnProGpuFrameRendered(CompositorMetrics metrics)
        {
            if (_firstRenderedFrameMs == 0d)
            {
                _firstRenderedFrameMs = Stopwatch
                    .GetElapsedTime(_startupTimestamp)
                    .TotalMilliseconds;
            }

            if (Volatile.Read(ref _completed) != 0 || _totalFrames <= _warmupFrames)
            {
                return;
            }

            _metricSamples++;
            _compileMs += metrics.VisualTreeCompileTimeMs;
            _uploadMs += metrics.GpuUploadTimeMs;
            _renderMs += metrics.RenderPassTimeMs;
            _compositorMs += metrics.FrameTimeMs;
            _maxCompileMs = Math.Max(_maxCompileMs, metrics.VisualTreeCompileTimeMs);
            if (metrics.SceneCacheHit)
            {
                _sceneCacheHits++;
            }
            _lastMetrics = metrics;
        }
#endif

        private void Complete()
        {
            _measurementClock.Stop();
            AvaloniaBenchmarkEventSource.Log.MeasurementStopped(_page);
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - _allocatedBytesStart;
            var cpu = _process.TotalProcessorTime - _cpuStart;
            var elapsedSeconds = Math.Max(_measurementClock.Elapsed.TotalSeconds, double.Epsilon);

            CollectRetainedMemory();
            _process.Refresh();
            var gcInfo = GC.GetGCMemoryInfo();
            var memory = ProcessMemorySnapshot.Capture(_process);
            var result = new BenchmarkResult
            {
                Backend = _backend,
                Page = _page,
                WarmupFrames = _warmupFrames,
                MeasuredFrames = _measureFrames,
                ElapsedSeconds = elapsedSeconds,
                FramesPerSecond = _measureFrames / elapsedSeconds,
                ProcessCpuSeconds = cpu.TotalSeconds,
                ProcessCpuPercent = cpu.TotalSeconds / elapsedSeconds /
                    Math.Max(1, Environment.ProcessorCount) * 100,
                AllocatedBytes = allocatedBytes,
                AllocatedBytesPerFrame = (double)allocatedBytes / _measureFrames,
                ManagedBytes = GC.GetTotalMemory(forceFullCollection: false),
                GcCommittedBytes = gcInfo.TotalCommittedBytes,
                ManagedFragmentedBytes = gcInfo.FragmentedBytes,
                ProcessWorkingSetBytes = _process.WorkingSet64,
                ProcessResidentBytes = memory.ResidentBytes,
                ProcessWiredBytes = memory.WiredBytes,
                ProcessPhysicalFootprintBytes = memory.PhysicalFootprintBytes,
                ProcessPeakPhysicalFootprintBytes = memory.LifetimeMaxPhysicalFootprintBytes,
                Gen0Collections = GC.CollectionCount(0) - _gen0Start,
                Gen1Collections = GC.CollectionCount(1) - _gen1Start,
                Gen2Collections = GC.CollectionCount(2) - _gen2Start,
                AverageFrameMs = _frameMs / _measureFrames,
                MaxFrameMs = _maxFrameMs,
#if PROGPU_AVALONIA_BACKEND
                AverageCompileMs = Average(_compileMs, _metricSamples),
                AverageUploadMs = Average(_uploadMs, _metricSamples),
                AverageRenderMs = Average(_renderMs, _metricSamples),
                AverageCompositorMs = Average(_compositorMs, _metricSamples),
                MaxCompileMs = _maxCompileMs,
                FirstRenderedFrameMs = _firstRenderedFrameMs,
                SceneCacheHits = _sceneCacheHits,
                PathAtlasEntries = _lastMetrics.PathAtlasCachedCount,
                PathAtlasCpuCacheBytes = _lastMetrics.PathAtlasCpuCacheBytes,
                PathAtlasTextureBytes = _lastMetrics.PathAtlasTextureBytes,
                PathAtlasWidth = _lastMetrics.PathAtlasWidth,
                PathAtlasHeight = _lastMetrics.PathAtlasHeight,
                PathRasterStagingBytes = _lastMetrics.PathRasterStagingBytes,
                PathPeakRasterStagingBytes = _lastMetrics.PathPeakRasterStagingBytes,
                PathPeakRasterWidth = _lastMetrics.PathPeakRasterWidth,
                PathPeakRasterHeight = _lastMetrics.PathPeakRasterHeight,
                GlyphAtlasTextureBytes = _lastMetrics.GlyphAtlasTextureBytes,
                ColorGlyphAtlasTextureBytes = _lastMetrics.ColorGlyphAtlasTextureBytes,
                GlyphUniformStagingBytes = _lastMetrics.GlyphUniformStagingBytes,
                GlyphUniformUploadBytes = _lastMetrics.GlyphUniformUploadBytes,
                GlyphCoverageStagingBytes = _lastMetrics.GlyphCoverageStagingBytes,
                GlyphOutlineGpuBytes = _lastMetrics.GlyphOutlineGpuBytes,
                GlyphOutlineCompiledCount = _lastMetrics.GlyphOutlineCompiledCount,
                GlyphOutlineRecordCapacity = _lastMetrics.GlyphOutlineRecordCapacity,
                GlyphOutlineSegmentCount = _lastMetrics.GlyphOutlineSegmentCount,
                GlyphOutlineSegmentCapacity = _lastMetrics.GlyphOutlineSegmentCapacity,
                GlyphRasterBatchSubmissions = _lastMetrics.GlyphRasterBatchSubmissions,
                GlyphRasterComputePasses = _lastMetrics.GlyphRasterComputePasses,
                SceneBufferBytes = _lastMetrics.SceneBufferBytes,
                BrushStorageBufferBytes = _lastMetrics.BrushStorageBufferBytes,
                GradientStopStorageBufferBytes = _lastMetrics.GradientStopStorageBufferBytes,
                ActiveBrushCount = _lastMetrics.ActiveBrushCount,
                ActiveGradientStopCount = _lastMetrics.ActiveGradientStopCount,
                EffectParameterBufferBytes = _lastMetrics.EffectParameterBufferBytes,
                EffectShaderCount = _lastMetrics.EffectShaderCount,
                EffectPipelineCount = _lastMetrics.EffectPipelineCount,
                SceneShaderCount = _lastMetrics.SceneShaderCount,
                SceneRenderPipelineCount = _lastMetrics.SceneRenderPipelineCount,
                SceneComputePipelineCount = _lastMetrics.SceneComputePipelineCount,
                BaseBindGroupLayoutCount = _lastMetrics.BaseBindGroupLayoutCount,
                BasePipelineLayoutCount = _lastMetrics.BasePipelineLayoutCount,
                BaseBindGroupCount = _lastMetrics.BaseBindGroupCount,
                PersistentTextureBindGroupCount = _lastMetrics.PersistentTextureBindGroupCount,
                MaskBindGroupCount = _lastMetrics.MaskBindGroupCount,
                GpuHitTestingEnabled = _lastMetrics.GpuHitTestingEnabled,
                DrawCalls = _lastMetrics.DrawCallsCount,
                RecordedCommands = _lastMetrics.RecordedCommandCount,
                RecordedCommandCapacity = _lastMetrics.RecordedCommandCapacity,
                VectorVertices = _lastMetrics.VectorVerticesCount,
                TextVertices = _lastMetrics.TextVerticesCount
#endif
            };

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"[SampleBenchmark] RESULT backend={result.Backend} page=\"{result.Page}\" frames={_measureFrames}" +
                    $" wallFps={result.FramesPerSecond:F2} frameMs={result.AverageFrameMs:F4}" +
                    $" compileMs={result.AverageCompileMs:F4} uploadMs={result.AverageUploadMs:F4}" +
                    $" renderMs={result.AverageRenderMs:F4} compositorMs={result.AverageCompositorMs:F4}" +
                    $" maxFrameMs={result.MaxFrameMs:F4} maxCompileMs={result.MaxCompileMs:F4}" +
                    $" firstRenderedFrameMs={result.FirstRenderedFrameMs:F2}" +
                    $" allocatedBytesPerFrame={result.AllocatedBytesPerFrame:F0}" +
                    $" managedHeapBytes={result.ManagedBytes} gcCommittedBytes={result.GcCommittedBytes}" +
                    $" managedFragmentedBytes={result.ManagedFragmentedBytes}" +
                    $" processWorkingSetBytes={result.ProcessWorkingSetBytes}" +
                    $" processResidentBytes={result.ProcessResidentBytes}" +
                    $" processWiredBytes={result.ProcessWiredBytes}" +
                    $" processPhysicalFootprintBytes={result.ProcessPhysicalFootprintBytes}" +
                    $" processPeakPhysicalFootprintBytes={result.ProcessPeakPhysicalFootprintBytes}" +
                    $" gen0Collections={result.Gen0Collections} gen1Collections={result.Gen1Collections}" +
                    $" gen2Collections={result.Gen2Collections} pathAtlasEntries={result.PathAtlasEntries}" +
                    $" pathAtlasCpuCacheBytes={result.PathAtlasCpuCacheBytes}" +
                    $" pathAtlasTextureBytes={result.PathAtlasTextureBytes}" +
                    $" pathAtlasWidth={result.PathAtlasWidth}" +
                    $" pathAtlasHeight={result.PathAtlasHeight}" +
                    $" pathPeakRasterWidth={result.PathPeakRasterWidth}" +
                    $" pathPeakRasterHeight={result.PathPeakRasterHeight}" +
                    $" glyphAtlasTextureBytes={result.GlyphAtlasTextureBytes}" +
                    $" colorGlyphAtlasTextureBytes={result.ColorGlyphAtlasTextureBytes}" +
                    $" glyphUniformStagingBytes={result.GlyphUniformStagingBytes}" +
                    $" glyphUniformUploadBytes={result.GlyphUniformUploadBytes}" +
                    $" glyphCoverageStagingBytes={result.GlyphCoverageStagingBytes}" +
                    $" glyphOutlineGpuBytes={result.GlyphOutlineGpuBytes}" +
                    $" glyphOutlineCompiled={result.GlyphOutlineCompiledCount}" +
                    $" glyphOutlineRecordCapacity={result.GlyphOutlineRecordCapacity}" +
                    $" glyphOutlineSegments={result.GlyphOutlineSegmentCount}" +
                    $" glyphOutlineSegmentCapacity={result.GlyphOutlineSegmentCapacity}" +
                    $" glyphRasterBatchSubmissions={result.GlyphRasterBatchSubmissions}" +
                    $" glyphRasterComputePasses={result.GlyphRasterComputePasses}" +
                    $" sceneBufferBytes={result.SceneBufferBytes}" +
                    $" brushStorageBufferBytes={result.BrushStorageBufferBytes}" +
                    $" gradientStopStorageBufferBytes={result.GradientStopStorageBufferBytes}" +
                    $" activeBrushes={result.ActiveBrushCount}" +
                    $" activeGradientStops={result.ActiveGradientStopCount}" +
                    $" effectParameterBufferBytes={result.EffectParameterBufferBytes}" +
                    $" effectShaders={result.EffectShaderCount}" +
                    $" effectPipelines={result.EffectPipelineCount}" +
                    $" sceneShaders={result.SceneShaderCount}" +
                    $" sceneRenderPipelines={result.SceneRenderPipelineCount}" +
                    $" sceneComputePipelines={result.SceneComputePipelineCount}" +
                    $" baseBindGroupLayouts={result.BaseBindGroupLayoutCount}" +
                    $" basePipelineLayouts={result.BasePipelineLayoutCount}" +
                    $" baseBindGroups={result.BaseBindGroupCount}" +
                    $" persistentTextureBindGroups={result.PersistentTextureBindGroupCount}" +
                    $" maskBindGroups={result.MaskBindGroupCount}" +
                    $" gpuHitTestingEnabled={result.GpuHitTestingEnabled} draws={result.DrawCalls}" +
                    $" recordedCommands={result.RecordedCommands}" +
                    $" recordedCommandCapacity={result.RecordedCommandCapacity}" +
                    $" vectorVertices={result.VectorVertices} textVertices={result.TextVertices}"));

            try
            {
                WriteScreenshot();
                var directory = Path.GetDirectoryName(_outputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(
                    _outputPath,
                    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Environment.Exit(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Environment.Exit(3);
            }
        }

        private void WriteScreenshot()
        {
            if (_screenshotPath is null || _window is not { } window)
            {
                return;
            }

            int width = Math.Max(1, (int)Math.Ceiling(window.ClientSize.Width));
            int height = Math.Max(1, (int)Math.Ceiling(window.ClientSize.Height));
            using var bitmap = new RenderTargetBitmap(
                new PixelSize(width, height),
                new Vector(96 * window.RenderScaling, 96 * window.RenderScaling));
            bitmap.Render(window);
            var directory = Path.GetDirectoryName(_screenshotPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            bitmap.Save(_screenshotPath);
        }

        private static void CollectRetainedMemory()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        private static double Average(double total, int samples)
            => samples == 0 ? 0 : total / samples;

        public void Dispose()
        {
#if PROGPU_AVALONIA_BACKEND
            ProGpuRenderingDiagnostics.FrameRendered -= OnProGpuFrameRendered;
#endif
            _process.Dispose();
        }

        private static int ReadPositiveInt(string variable, int fallback)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                   parsed > 0
                ? parsed
                : fallback;
        }

        private static bool ReadBoolean(string variable)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class BenchmarkResult
        {
            public string Backend { get; init; } = string.Empty;
            public string Page { get; init; } = string.Empty;
            public int WarmupFrames { get; init; }
            public int MeasuredFrames { get; init; }
            public double ElapsedSeconds { get; init; }
            public double FramesPerSecond { get; init; }
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
            public double AverageFrameMs { get; init; }
            public double AverageCompileMs { get; init; }
            public double AverageUploadMs { get; init; }
            public double AverageRenderMs { get; init; }
            public double AverageCompositorMs { get; init; }
            public double MaxFrameMs { get; init; }
            public double MaxCompileMs { get; init; }
            public double FirstRenderedFrameMs { get; init; }
            public int SceneCacheHits { get; init; }
            public int PathAtlasEntries { get; init; }
            public long PathAtlasCpuCacheBytes { get; init; }
            public ulong PathAtlasTextureBytes { get; init; }
            public uint PathAtlasWidth { get; init; }
            public uint PathAtlasHeight { get; init; }
            public uint PathRasterStagingBytes { get; init; }
            public uint PathPeakRasterStagingBytes { get; init; }
            public uint PathPeakRasterWidth { get; init; }
            public uint PathPeakRasterHeight { get; init; }
            public ulong GlyphAtlasTextureBytes { get; init; }
            public ulong ColorGlyphAtlasTextureBytes { get; init; }
            public ulong GlyphUniformStagingBytes { get; init; }
            public ulong GlyphUniformUploadBytes { get; init; }
            public ulong GlyphCoverageStagingBytes { get; init; }
            public ulong GlyphOutlineGpuBytes { get; init; }
            public int GlyphOutlineCompiledCount { get; init; }
            public int GlyphOutlineRecordCapacity { get; init; }
            public int GlyphOutlineSegmentCount { get; init; }
            public int GlyphOutlineSegmentCapacity { get; init; }
            public ulong GlyphRasterBatchSubmissions { get; init; }
            public ulong GlyphRasterComputePasses { get; init; }
            public ulong SceneBufferBytes { get; init; }
            public ulong BrushStorageBufferBytes { get; init; }
            public ulong GradientStopStorageBufferBytes { get; init; }
            public int ActiveBrushCount { get; init; }
            public int ActiveGradientStopCount { get; init; }
            public ulong EffectParameterBufferBytes { get; init; }
            public int EffectShaderCount { get; init; }
            public int EffectPipelineCount { get; init; }
            public int SceneShaderCount { get; init; }
            public int SceneRenderPipelineCount { get; init; }
            public int SceneComputePipelineCount { get; init; }
            public int BaseBindGroupLayoutCount { get; init; }
            public int BasePipelineLayoutCount { get; init; }
            public int BaseBindGroupCount { get; init; }
            public int PersistentTextureBindGroupCount { get; init; }
            public int MaskBindGroupCount { get; init; }
            public bool GpuHitTestingEnabled { get; init; }
            public int DrawCalls { get; init; }
            public int RecordedCommands { get; init; }
            public int RecordedCommandCapacity { get; init; }
            public int VectorVertices { get; init; }
            public int TextVertices { get; init; }
        }
    }

    internal readonly record struct ProcessMemorySnapshot(
        long ResidentBytes,
        long WiredBytes,
        long PhysicalFootprintBytes,
        long LifetimeMaxPhysicalFootprintBytes)
    {
        public static ProcessMemorySnapshot Capture(Process process)
        {
            if (OperatingSystem.IsMacOS() && MacOsProcessMemory.TryCapture(process.Id, out var mac))
            {
                return mac;
            }
            return new ProcessMemorySnapshot(
                process.WorkingSet64,
                0,
                process.WorkingSet64,
                process.PeakWorkingSet64);
        }
    }

    internal static unsafe class MacOsProcessMemory
    {
        private const int RUsageInfoV4Flavor = 4;

        public static bool TryCapture(int processId, out ProcessMemorySnapshot snapshot)
        {
            var usage = default(RUsageInfoV4);
            if (ProcPidRUsage(processId, RUsageInfoV4Flavor, ref usage) == 0)
            {
                snapshot = new ProcessMemorySnapshot(
                    checked((long)usage.ResidentSize),
                    checked((long)usage.WiredSize),
                    checked((long)usage.PhysicalFootprint),
                    checked((long)usage.LifetimeMaxPhysicalFootprint));
                return true;
            }
            snapshot = default;
            return false;
        }

        [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pid_rusage")]
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

    [EventSource(Name = "ProGPU-SampleBenchmark")]
    internal sealed class AvaloniaBenchmarkEventSource : EventSource
    {
        public static readonly AvaloniaBenchmarkEventSource Log = new();

        [Event(1, Level = EventLevel.Informational)]
        public void WorkloadStarted(string page) => WriteEvent(1, page);

        [Event(2, Level = EventLevel.Informational)]
        public void MeasurementStarted(string page) => WriteEvent(2, page);

        [Event(3, Level = EventLevel.Informational)]
        public void MeasurementStopped(string page) => WriteEvent(3, page);
    }
}
