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
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
#if PROGPU_AVALONIA_BACKEND
using Avalonia.ProGpu;
using ProGPU.Backend;
using ProGPU.Scene;
#endif

namespace ControlCatalog.Desktop
{
    internal sealed class ControlCatalogBenchmark : IDisposable
    {
        private const int BenchmarkSchemaVersion = 2;
        private const string OutputVariable = "PROGPU_AVALONIA_BENCHMARK_OUTPUT";
        private const string ScreenshotVariable = "PROGPU_AVALONIA_BENCHMARK_SCREENSHOT";
        private const string WarmupVariable = "PROGPU_AVALONIA_BENCHMARK_WARMUP_FRAMES";
        private const string MeasureVariable = "PROGPU_AVALONIA_BENCHMARK_MEASURE_FRAMES";
        private const string RunVariable = "PROGPU_AVALONIA_BENCHMARK_RUN";
        private const string WholeFontsVariable = "PROGPU_AVALONIA_BENCHMARK_WHOLE_FONTS";
        private const string RootGeometryClipVariable =
            "PROGPU_AVALONIA_BENCHMARK_ROOT_GEOMETRY_CLIP";
        private const string RootAliasedTextVariable =
            "PROGPU_AVALONIA_BENCHMARK_ROOT_ALIASED_TEXT";
        private const string RootConicOpacityMaskVariable =
            "PROGPU_AVALONIA_BENCHMARK_ROOT_CONIC_OPACITY_MASK";
        private const string TextBlurEffectVariable =
            "PROGPU_AVALONIA_BENCHMARK_TEXT_BLUR_EFFECT";
        private const string TextDropShadowEffectVariable =
            "PROGPU_AVALONIA_BENCHMARK_TEXT_DROP_SHADOW_EFFECT";
        private const string BitmapCacheScaleVariable =
            "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_SCALE";
        private const string BitmapCacheSnapVariable =
            "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_SNAP";
        private const string BitmapCacheClearTypeVariable =
            "PROGPU_AVALONIA_BENCHMARK_BITMAP_CACHE_CLEARTYPE";
        private const string PopupVariable =
            "PROGPU_AVALONIA_BENCHMARK_POPUP";
        private const string EmojiStressVariable =
            "PROGPU_AVALONIA_BENCHMARK_EMOJI_STRESS";
        private const string CustomVisualVariable =
            "PROGPU_AVALONIA_BENCHMARK_CUSTOM_VISUAL";
        private const string WholeFontsSwitch = "ProGPU.Avalonia.Diagnostics.UseWholeSystemFontFiles";

        private readonly string _backend;
        private readonly string _textShaper;
        private readonly string _page;
        private readonly int _run;
        private readonly string _outputPath;
        private readonly string? _screenshotPath;
        private readonly int _warmupFrames;
        private readonly int _measureFrames;
        private readonly double[] _frameTimeSamples;
        private readonly Process _process;
        private readonly long _startupTimestamp = Stopwatch.GetTimestamp();
        private readonly Stopwatch _measurementClock = new();
        private readonly Action<TimeSpan> _animationFrameCallback;
        private Window? _window;
        private Control? _compositionAnimationTarget;
        private Control? _frameInvalidationTarget;
        private Control? _benchmarkScreenshotRoot;
        private Popup? _popupFixture;
        private CompositionCustomVisual? _customVisualFixture;
        private bool _bitmapCacheFixturePending;
        private int _totalFrames;
        private int _measuredFrames;
        private int _completed;
        private long _allocatedBytesStart;
        private TimeSpan _cpuStart;
        private int _gen0Start;
        private int _gen1Start;
        private int _gen2Start;
        private double _frameMs;
        private long _lastAnimationTimestamp;
#if PROGPU_AVALONIA_BACKEND
        private readonly double[] _compileTimeSamples;
        private readonly double[] _uploadTimeSamples;
        private readonly double[] _renderTimeSamples;
        private readonly double[] _compositorTimeSamples;
        private int _metricSamples;
        private double _compileMs;
        private double _uploadMs;
        private double _renderMs;
        private double _compositorMs;
        private int _sceneCacheHits;
        private long _incrementalScenePageHits;
        private long _incrementalScenePageMisses;
        private long _incrementalScenePageCompilations;
        private long _incrementalScenePageReusedArrays;
        private long _incrementalSceneUploadPageWrites;
        private long _incrementalSceneUploadBytes;
        private long _sceneUploadBatchCount;
        private long _sceneUploadCopyCount;
        private double _firstRenderedFrameMs;
        private CompositorMetrics _lastMetrics;
#endif

        private ControlCatalogBenchmark(
            string backend,
            string textShaper,
            string page,
            int run,
            string outputPath,
            string? screenshotPath,
            int warmupFrames,
            int measureFrames)
        {
            _backend = backend;
            _textShaper = textShaper;
            _page = page;
            _run = run;
            _outputPath = outputPath;
            _screenshotPath = screenshotPath;
            _warmupFrames = warmupFrames;
            _measureFrames = measureFrames;
            _frameTimeSamples = new double[measureFrames];
#if PROGPU_AVALONIA_BACKEND
            _compileTimeSamples = new double[measureFrames];
            _uploadTimeSamples = new double[measureFrames];
            _renderTimeSamples = new double[measureFrames];
            _compositorTimeSamples = new double[measureFrames];
#endif
            _process = Process.GetCurrentProcess();
            _animationFrameCallback = OnAnimationFrame;
#if PROGPU_AVALONIA_BACKEND
            ProGpuRenderingDiagnostics.FrameRendered += OnProGpuFrameRendered;
#endif
            AvaloniaBenchmarkEventSource.Log.WorkloadStarted(page);
        }

        public static ControlCatalogBenchmark? TryStart(string backend, string? page, string textShaper)
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
                textShaper,
                string.IsNullOrWhiteSpace(page) ? "Home" : page,
                ReadPositiveInt(RunVariable, 1),
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

            AttachEmojiStressFixture(window);
            _window = window;
            _compositionAnimationTarget =
                FindScreenshotRoot(window.Content as Avalonia.Visual ?? window)
                    as Control ??
                window.Content as Control;
            _frameInvalidationTarget =
                FindFrameInvalidationTarget(_compositionAnimationTarget);
            if (ReadBoolean(CustomVisualVariable) &&
                _compositionAnimationTarget is { } customVisualOwner)
            {
                AttachCustomVisualFixture(customVisualOwner);
            }
            if (_frameInvalidationTarget is { } effectTarget)
            {
                if (ReadBoolean(TextBlurEffectVariable))
                {
                    effectTarget.Effect =
                        new Avalonia.Media.BlurEffect { Radius = 4 };
                    Console.WriteLine(
                        "[ControlCatalog] text effect fixture blur radius=4");
                }
                else if (ReadBoolean(TextDropShadowEffectVariable))
                {
                    effectTarget.Effect = new Avalonia.Media.DropShadowEffect
                    {
                        OffsetX = 5,
                        OffsetY = 3,
                        BlurRadius = 4,
                        Color = Colors.DarkGreen,
                        Opacity = 0.75
                    };
                    Console.WriteLine(
                        "[ControlCatalog] text effect fixture" +
                        " drop-shadow offset=(5,3) radius=4 opacity=0.75");
                }
            }
            if (ReadBoolean(RootGeometryClipVariable) &&
                _compositionAnimationTarget is { Bounds.Width: > 0, Bounds.Height: > 0 } target)
            {
                target.Clip = new EllipseGeometry(
                    new Avalonia.Rect(40, 40, 160, 120));
            }
            if (ReadBoolean(RootAliasedTextVariable) &&
                _compositionAnimationTarget is { } textOptionsTarget)
            {
                TextOptions.SetTextRenderingMode(
                    textOptionsTarget,
                    Avalonia.Media.TextRenderingMode.Alias);
            }
            if (ReadBoolean(RootConicOpacityMaskVariable) &&
                _compositionAnimationTarget is { } conicMaskTarget)
            {
                conicMaskTarget.OpacityMask = new ConicGradientBrush
                {
                    Angle = 23,
                    Center = RelativePoint.Center,
                    SpreadMethod = GradientSpreadMethod.Repeat,
                    GradientStops = new GradientStops
                    {
                        new(
                            Color.FromArgb(24, 255, 255, 255),
                            0),
                        new(
                            Color.FromArgb(255, 255, 255, 255),
                            0.5),
                        new(
                            Color.FromArgb(24, 255, 255, 255),
                            1)
                    }
                };
                Console.WriteLine(
                    "[ControlCatalog] root conic opacity mask fixture angle=23");
            }
            if (_compositionAnimationTarget is { } cacheFixtureRoot)
            {
                _bitmapCacheFixturePending =
                    !ApplyBitmapCacheFixture(cacheFixtureRoot);
            }
            if (ReadBoolean(PopupVariable) &&
                _compositionAnimationTarget is { } popupTarget)
            {
                _popupFixture = new Popup
                {
                    PlacementTarget = popupTarget,
                    Placement = PlacementMode.Bottom,
                    HorizontalOffset = 24,
                    VerticalOffset = 12,
                    WindowManagerAddShadowHint = true,
                    Child = new Border
                    {
                        Width = 240,
                        Height = 96,
                        Padding = new Thickness(16),
                        Background = Brushes.DarkSlateBlue,
                        BorderBrush = Brushes.Gold,
                        BorderThickness = new Thickness(2),
                        CornerRadius = new CornerRadius(8),
                        Child = new TextBlock
                        {
                            Text = "ProGPU native popup",
                            Foreground = Brushes.White,
                            FontSize = 18
                        }
                    }
                };
                _popupFixture.IsOpen = true;
                if (_popupFixture.IsUsingOverlayLayer)
                {
                    throw new InvalidOperationException(
                        "The native popup fixture unexpectedly used the overlay layer.");
                }
                Console.WriteLine(
                    "[ControlCatalog] native popup fixture opened");
            }
            window.RequestAnimationFrame(_animationFrameCallback);
        }

        private void OnAnimationFrame(TimeSpan timestamp)
        {
            if (Volatile.Read(ref _completed) != 0 || _window is not { } window)
            {
                return;
            }

            if (_bitmapCacheFixturePending &&
                _compositionAnimationTarget is { } cacheFixtureRoot)
            {
                _bitmapCacheFixturePending =
                    !ApplyBitmapCacheFixture(cacheFixtureRoot);
            }

            if (_frameInvalidationTarget is { } animationTarget)
            {
                animationTarget.Opacity = (_totalFrames & 1) == 0
                    ? 0.999
                    : 1.0;
            }
            _totalFrames++;

            long timestampTicks = timestamp.Ticks;
            if (_lastAnimationTimestamp != 0 && _totalFrames > _warmupFrames)
            {
                double elapsedMilliseconds =
                    (timestampTicks - _lastAnimationTimestamp) * 1000d / TimeSpan.TicksPerSecond;
                _frameMs += elapsedMilliseconds;
                if ((uint)_measuredFrames < (uint)_frameTimeSamples.Length)
                {
                    _frameTimeSamples[_measuredFrames] = elapsedMilliseconds;
                }
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

        private void AttachCustomVisualFixture(Avalonia.Visual owner)
        {
            var elementVisual = ElementComposition.GetElementVisual(owner) ??
                throw new InvalidOperationException(
                    "The custom-visual benchmark owner is not attached to a compositor.");
            if (ElementComposition.GetElementChildVisual(owner) != null)
            {
                throw new InvalidOperationException(
                    "The custom-visual benchmark owner already has a composition child.");
            }

            var customVisual = elementVisual.Compositor.CreateCustomVisual(
                new BenchmarkCustomVisualHandler());
            customVisual.Size = new(64, 64);
            ElementComposition.SetElementChildVisual(owner, customVisual);
            customVisual.SendHandlerMessage(
                BenchmarkCustomVisualHandler.StartMessage);
            _customVisualFixture = customVisual;
        }

        private sealed class BenchmarkCustomVisualHandler :
            CompositionCustomVisualHandler
        {
            internal static readonly object StartMessage = new();
            private static readonly ImmutableSolidColorBrush s_brush =
                new(Colors.DeepSkyBlue);
            private float _phase;
            private bool _running;

            public override void OnMessage(object message)
            {
                if (!ReferenceEquals(message, StartMessage))
                    return;

                _running = true;
                Invalidate();
                RegisterForNextAnimationFrameUpdate();
            }

            public override void OnAnimationFrameUpdate()
            {
                if (!_running)
                    return;

                _phase = (_phase + 0.125f) % 32f;
                Invalidate();
                RegisterForNextAnimationFrameUpdate();
            }

            public override void OnRender(
                ImmediateDrawingContext drawingContext)
            {
                drawingContext.DrawEllipse(
                    s_brush,
                    null,
                    new Point(16 + _phase, 32),
                    12,
                    12);
            }
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

            if ((uint)_metricSamples >= (uint)_compileTimeSamples.Length)
            {
                _lastMetrics = metrics;
                return;
            }

            int sampleIndex = _metricSamples;
            _compileTimeSamples[sampleIndex] =
                metrics.VisualTreeCompileTimeMs;
            _uploadTimeSamples[sampleIndex] = metrics.GpuUploadTimeMs;
            _renderTimeSamples[sampleIndex] = metrics.RenderPassTimeMs;
            _compositorTimeSamples[sampleIndex] = metrics.FrameTimeMs;
            _metricSamples++;
            _compileMs += metrics.VisualTreeCompileTimeMs;
            _uploadMs += metrics.GpuUploadTimeMs;
            _renderMs += metrics.RenderPassTimeMs;
            _compositorMs += metrics.FrameTimeMs;
            if (metrics.SceneCacheHit)
            {
                _sceneCacheHits++;
            }
            _incrementalScenePageHits += metrics.IncrementalScenePageHits;
            _incrementalScenePageMisses += metrics.IncrementalScenePageMisses;
            _incrementalScenePageCompilations +=
                metrics.IncrementalScenePageCompilations;
            _incrementalScenePageReusedArrays +=
                metrics.IncrementalScenePageReusedArrays;
            _incrementalSceneUploadPageWrites +=
                metrics.IncrementalSceneUploadPageWrites;
            _incrementalSceneUploadBytes +=
                metrics.IncrementalSceneUploadBytes;
            _sceneUploadBatchCount += metrics.SceneUploadBatchCount;
            _sceneUploadCopyCount += metrics.SceneUploadCopyCount;
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
            if (_frameInvalidationTarget is { } animationTarget)
            {
                animationTarget.Opacity = 1.0;
            }

            CollectRetainedMemory();
            _process.Refresh();
            var gcInfo = GC.GetGCMemoryInfo();
            var memory = ProcessMemorySnapshot.Capture(_process);
            Distribution frameTime = CalculateDistribution(
                _frameTimeSamples,
                _measuredFrames);
#if PROGPU_AVALONIA_BACKEND
            Distribution compileTime = CalculateDistribution(
                _compileTimeSamples,
                _metricSamples);
            Distribution uploadTime = CalculateDistribution(
                _uploadTimeSamples,
                _metricSamples);
            Distribution renderTime = CalculateDistribution(
                _renderTimeSamples,
                _metricSamples);
            Distribution compositorTime = CalculateDistribution(
                _compositorTimeSamples,
                _metricSamples);
            WgpuNativeResourceSnapshot nativeGpu = default;
            _ = WgpuContext.TryGetFirstActiveContext(out var activeContext) &&
                activeContext.TryCaptureNativeResourceSnapshot(out nativeGpu);
#endif
            var result = new BenchmarkResult
            {
                SchemaVersion = BenchmarkSchemaVersion,
                Backend = _backend,
                TextShaper = _textShaper,
                Page = _page,
                Run = _run,
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
                FrameTimeSampleCount = frameTime.Count,
                MinFrameMs = frameTime.Minimum,
                MedianFrameMs = frameTime.Median,
                P95FrameMs = frameTime.P95,
                P99FrameMs = frameTime.P99,
                MaxFrameMs = frameTime.Maximum,
#if PROGPU_AVALONIA_BACKEND
                AverageCompileMs = Average(_compileMs, _metricSamples),
                AverageUploadMs = Average(_uploadMs, _metricSamples),
                AverageRenderMs = Average(_renderMs, _metricSamples),
                AverageCompositorMs = Average(_compositorMs, _metricSamples),
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
                FirstRenderedFrameMs = _firstRenderedFrameMs,
                RenderTargetWidth = _lastMetrics.RenderTargetWidth,
                RenderTargetHeight = _lastMetrics.RenderTargetHeight,
                DpiScale = _lastMetrics.DpiScale,
                PresentationPath =
                    _lastMetrics.PresentationPath,
                SceneCacheHits = _sceneCacheHits,
                IncrementalScenePages = _lastMetrics.IncrementalScenePageCount,
                IncrementalScenePageHits = _incrementalScenePageHits,
                IncrementalScenePageMisses = _incrementalScenePageMisses,
                IncrementalScenePageCompilations =
                    _incrementalScenePageCompilations,
                IncrementalScenePageReusedArrays =
                    _incrementalScenePageReusedArrays,
                IncrementalScenePageBytes =
                    _lastMetrics.IncrementalScenePageBytes,
                IncrementalScenePageRejectReason =
                    _lastMetrics.IncrementalScenePageRejectReason,
                IncrementalScenePageMissReason =
                    _lastMetrics.IncrementalScenePageMissReason,
                IncrementalSceneUploadPageWrites =
                    _incrementalSceneUploadPageWrites,
                IncrementalSceneUploadBytes =
                    _incrementalSceneUploadBytes,
                IncrementalSceneUploadShadowBytes =
                    _lastMetrics.IncrementalSceneUploadShadowBytes,
                SceneUploadBatchCount = _sceneUploadBatchCount,
                SceneUploadCopyCount = _sceneUploadCopyCount,
                SceneUploadArenaBytes =
                    _lastMetrics.SceneUploadArenaBytes,
                PathAtlasEntries = _lastMetrics.PathAtlasCachedCount,
                PathAtlasCpuCacheBytes = _lastMetrics.PathAtlasCpuCacheBytes,
                PathAtlasTextureBytes = _lastMetrics.PathAtlasTextureBytes,
                PathAtlasCurrentFramePaths =
                    _lastMetrics.PathAtlasCurrentFramePathCount,
                PathAtlasCurrentFrameCoverageBytes =
                    _lastMetrics.PathAtlasCurrentFrameCoverageBytes,
                PathAtlasCachedCoverageBytes =
                    _lastMetrics.PathAtlasCachedCoverageBytes,
                PathAtlasCachedPaddedCoverageBytes =
                    _lastMetrics.PathAtlasCachedPaddedCoverageBytes,
                PathAtlasGrowthCount = _lastMetrics.PathAtlasGrowthCount,
                PathAtlasShrinkCount = _lastMetrics.PathAtlasShrinkCount,
                PathAtlasFramesSinceResize = _lastMetrics.PathAtlasFramesSinceResize,
                PathAtlasWidth = _lastMetrics.PathAtlasWidth,
                PathAtlasHeight = _lastMetrics.PathAtlasHeight,
                PathRasterStagingBytes = _lastMetrics.PathRasterStagingBytes,
                PathPeakRasterStagingBytes = _lastMetrics.PathPeakRasterStagingBytes,
                PathPeakRasterWidth = _lastMetrics.PathPeakRasterWidth,
                PathPeakRasterHeight = _lastMetrics.PathPeakRasterHeight,
                GlyphAtlasTextureBytes = _lastMetrics.GlyphAtlasTextureBytes,
                ColorGlyphAtlasTextureBytes = _lastMetrics.ColorGlyphAtlasTextureBytes,
                BitmapGlyphMetricCacheCount =
                    _lastMetrics.BitmapGlyphMetricCacheCount,
                BitmapGlyphDecodedPixelBytes =
                    _lastMetrics.BitmapGlyphDecodedPixelBytes,
                BitmapGlyphMetricEvictions =
                    _lastMetrics.BitmapGlyphMetricEvictions,
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
                MaskTexturePoolCount = _lastMetrics.MaskTexturePoolCount,
                MaskTextureRetentionLimit = _lastMetrics.MaskTextureRetentionLimit,
                MaskTexturePoolBytes = _lastMetrics.MaskTexturePoolBytes,
                MaskRenderScratchTextureBytes = _lastMetrics.MaskRenderScratchTextureBytes,
                MaskRenderPasses = _lastMetrics.MaskRenderPassCount,
                MaskRenderDrawCalls = _lastMetrics.MaskRenderDrawCallCount,
                MaskCopyBytes = _lastMetrics.MaskCopyBytes,
                EffectTextureBytes = _lastMetrics.EffectTextureBytes,
                LayerTextureBytes = _lastMetrics.LayerTextureBytes,
                AdvancedBlendTextureBytes = _lastMetrics.AdvancedBlendTextureBytes,
                WavefrontTextureBytes = _lastMetrics.WavefrontTextureBytes,
                MsaaTextureBytes = _lastMetrics.MsaaTextureBytes,
                TrackedIntermediateTextureBytes = _lastMetrics.TrackedIntermediateTextureBytes,
                MetalAllocatedBytes = nativeGpu.MetalAllocatedBytes,
                NativeCommandBuffers = nativeGpu.CommandBuffers.KeptFromUser,
                NativeBuffers = nativeGpu.Buffers.KeptFromUser,
                NativeTextures = nativeGpu.Textures.KeptFromUser,
                NativeTextureViews = nativeGpu.TextureViews.KeptFromUser,
                NativeBindGroups = nativeGpu.BindGroups.KeptFromUser,
                NativeBindGroupLayouts = nativeGpu.BindGroupLayouts.KeptFromUser,
                NativeShaderModules = nativeGpu.ShaderModules.KeptFromUser,
                NativeRenderPipelines = nativeGpu.RenderPipelines.KeptFromUser,
                NativeComputePipelines = nativeGpu.ComputePipelines.KeptFromUser,
                GpuHitTestingEnabled = _lastMetrics.GpuHitTestingEnabled,
                DrawCalls = _lastMetrics.DrawCallsCount,
                RecordedCommands = _lastMetrics.RecordedCommandCount,
                RecordedCommandCapacity = _lastMetrics.RecordedCommandCapacity,
                RetainedCompositionPictures = _lastMetrics.RetainedCompositionPictureCount,
                RetainedCompositionPictureHits = _lastMetrics.RetainedCompositionPictureHits,
                RetainedCompositionPictureMisses = _lastMetrics.RetainedCompositionPictureMisses,
                RetainedCompositionPictureCompilations =
                    _lastMetrics.RetainedCompositionPictureCompilations,
                RetainedCompositionScenes = _lastMetrics.RetainedCompositionSceneCount,
                RetainedCompositionSceneNodes = _lastMetrics.RetainedCompositionSceneNodeCount,
                RetainedCompositionFallbackNodes =
                    _lastMetrics.RetainedCompositionFallbackNodeCount,
                RetainedCompositionCustomVisualNodes =
                    _lastMetrics.RetainedCompositionCustomVisualNodeCount,
                RetainedCompositionCustomVisualCompilations =
                    _lastMetrics.RetainedCompositionCustomVisualCompilations,
                RetainedCompositionSceneFullSynchronizations =
                    _lastMetrics.RetainedCompositionSceneFullSynchronizations,
                RetainedCompositionSceneIncrementalSynchronizations =
                    _lastMetrics.RetainedCompositionSceneIncrementalSynchronizations,
                RetainedCompositionSceneUnchangedReuses =
                    _lastMetrics.RetainedCompositionSceneUnchangedReuses,
                VectorVertices = _lastMetrics.VectorVerticesCount,
                TextVertices = _lastMetrics.TextVerticesCount
#endif
            };

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"[SampleBenchmark] RESULT backend={result.Backend} textShaper={result.TextShaper}" +
                    $" page=\"{result.Page}\" run={result.Run} frames={_measureFrames}" +
                    $" wallFps={result.FramesPerSecond:F2} frameMs={result.AverageFrameMs:F4}" +
                    $" medianFrameMs={result.MedianFrameMs:F4}" +
                    $" p95FrameMs={result.P95FrameMs:F4}" +
                    $" p99FrameMs={result.P99FrameMs:F4}" +
                    $" compileMs={result.AverageCompileMs:F4} uploadMs={result.AverageUploadMs:F4}" +
                    $" renderMs={result.AverageRenderMs:F4} compositorMs={result.AverageCompositorMs:F4}" +
                    $" maxFrameMs={result.MaxFrameMs:F4} maxCompileMs={result.MaxCompileMs:F4}" +
                    $" firstRenderedFrameMs={result.FirstRenderedFrameMs:F2}" +
                    $" renderTarget={result.RenderTargetWidth}x{result.RenderTargetHeight}" +
                    $" dpiScale={result.DpiScale:F3}" +
                    $" presentationPath={result.PresentationPath}" +
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
                    $" pathAtlasCurrentFramePaths={result.PathAtlasCurrentFramePaths}" +
                    $" pathAtlasCurrentFrameCoverageBytes={result.PathAtlasCurrentFrameCoverageBytes}" +
                    $" pathAtlasCachedCoverageBytes={result.PathAtlasCachedCoverageBytes}" +
                    $" pathAtlasCachedPaddedCoverageBytes={result.PathAtlasCachedPaddedCoverageBytes}" +
                    $" pathAtlasGrowthCount={result.PathAtlasGrowthCount}" +
                    $" pathAtlasShrinkCount={result.PathAtlasShrinkCount}" +
                    $" pathAtlasFramesSinceResize={result.PathAtlasFramesSinceResize}" +
                    $" pathAtlasWidth={result.PathAtlasWidth}" +
                    $" pathAtlasHeight={result.PathAtlasHeight}" +
                    $" pathPeakRasterWidth={result.PathPeakRasterWidth}" +
                    $" pathPeakRasterHeight={result.PathPeakRasterHeight}" +
                    $" glyphAtlasTextureBytes={result.GlyphAtlasTextureBytes}" +
                    $" colorGlyphAtlasTextureBytes={result.ColorGlyphAtlasTextureBytes}" +
                    $" bitmapGlyphMetricCacheCount={result.BitmapGlyphMetricCacheCount}" +
                    $" bitmapGlyphDecodedPixelBytes={result.BitmapGlyphDecodedPixelBytes}" +
                    $" bitmapGlyphMetricEvictions={result.BitmapGlyphMetricEvictions}" +
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
                    $" maskTexturePoolCount={result.MaskTexturePoolCount}" +
                    $" maskTextureRetentionLimit={result.MaskTextureRetentionLimit}" +
                    $" maskTexturePoolBytes={result.MaskTexturePoolBytes}" +
                    $" maskRenderScratchTextureBytes={result.MaskRenderScratchTextureBytes}" +
                    $" maskRenderPasses={result.MaskRenderPasses}" +
                    $" maskRenderDrawCalls={result.MaskRenderDrawCalls}" +
                    $" maskCopyBytes={result.MaskCopyBytes}" +
                    $" effectTextureBytes={result.EffectTextureBytes}" +
                    $" layerTextureBytes={result.LayerTextureBytes}" +
                    $" advancedBlendTextureBytes={result.AdvancedBlendTextureBytes}" +
                    $" wavefrontTextureBytes={result.WavefrontTextureBytes}" +
                    $" msaaTextureBytes={result.MsaaTextureBytes}" +
                    $" trackedIntermediateTextureBytes={result.TrackedIntermediateTextureBytes}" +
                    $" metalAllocatedBytes={result.MetalAllocatedBytes}" +
                    $" nativeCommandBuffers={result.NativeCommandBuffers}" +
                    $" nativeBuffers={result.NativeBuffers}" +
                    $" nativeTextures={result.NativeTextures}" +
                    $" nativeTextureViews={result.NativeTextureViews}" +
                    $" nativeBindGroups={result.NativeBindGroups}" +
                    $" nativeBindGroupLayouts={result.NativeBindGroupLayouts}" +
                    $" nativeShaderModules={result.NativeShaderModules}" +
                    $" nativeRenderPipelines={result.NativeRenderPipelines}" +
                    $" nativeComputePipelines={result.NativeComputePipelines}" +
                    $" gpuHitTestingEnabled={result.GpuHitTestingEnabled} draws={result.DrawCalls}" +
                    $" recordedCommands={result.RecordedCommands}" +
                    $" recordedCommandCapacity={result.RecordedCommandCapacity}" +
                    $" retainedPictures={result.RetainedCompositionPictures}" +
                    $" retainedPictureHits={result.RetainedCompositionPictureHits}" +
                    $" retainedPictureMisses={result.RetainedCompositionPictureMisses}" +
                    $" retainedPictureCompilations={result.RetainedCompositionPictureCompilations}" +
                    $" retainedScenes={result.RetainedCompositionScenes}" +
                    $" retainedSceneNodes={result.RetainedCompositionSceneNodes}" +
                    $" retainedFallbackNodes={result.RetainedCompositionFallbackNodes}" +
                    $" retainedCustomVisualNodes={result.RetainedCompositionCustomVisualNodes}" +
                    $" retainedCustomVisualCompilations={result.RetainedCompositionCustomVisualCompilations}" +
                    $" retainedSceneFullSyncs={result.RetainedCompositionSceneFullSynchronizations}" +
                    $" retainedSceneIncrementalSyncs={result.RetainedCompositionSceneIncrementalSynchronizations}" +
                    $" retainedSceneUnchangedReuses={result.RetainedCompositionSceneUnchangedReuses}" +
                    $" incrementalPages={result.IncrementalScenePages}" +
                    $" incrementalPageHits={result.IncrementalScenePageHits}" +
                    $" incrementalPageMisses={result.IncrementalScenePageMisses}" +
                    $" incrementalPageCompilations={result.IncrementalScenePageCompilations}" +
                    $" incrementalPageReusedArrays={result.IncrementalScenePageReusedArrays}" +
                    $" incrementalPageBytes={result.IncrementalScenePageBytes}" +
                    $" incrementalPageReject=\"{result.IncrementalScenePageRejectReason}\"" +
                    $" incrementalPageMissReason=\"{result.IncrementalScenePageMissReason}\"" +
                    $" incrementalUploadPageWrites={result.IncrementalSceneUploadPageWrites}" +
                    $" incrementalUploadBytes={result.IncrementalSceneUploadBytes}" +
                    $" incrementalUploadShadowBytes={result.IncrementalSceneUploadShadowBytes}" +
                    $" sceneUploadBatches={result.SceneUploadBatchCount}" +
                    $" sceneUploadCopies={result.SceneUploadCopyCount}" +
                    $" sceneUploadArenaBytes={result.SceneUploadArenaBytes}" +
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

            Avalonia.Visual renderRoot = _benchmarkScreenshotRoot ??
                FindScreenshotRoot(window.Content as Avalonia.Visual ?? window);
            int width = Math.Max(1, (int)Math.Ceiling(renderRoot.Bounds.Width));
            int height = Math.Max(1, (int)Math.Ceiling(renderRoot.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(
                new PixelSize(width, height),
                new Vector(96 * window.RenderScaling, 96 * window.RenderScaling));
            bitmap.Render(renderRoot);
            var directory = Path.GetDirectoryName(_screenshotPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            bitmap.Save(_screenshotPath);
        }

        private void AttachEmojiStressFixture(Window window)
        {
            if (!ReadBoolean(EmojiStressVariable) ||
                !OperatingSystem.IsMacOS() ||
                window.Content is not Control originalContent)
            {
                return;
            }

            window.Content = null;
            var root = new Grid();
            root.Children.Add(originalContent);
            var emojiPanel = new Border
            {
                Width = 520,
                Padding = new Thickness(12),
                Margin = new Thickness(470, 88, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Background = Brushes.Black,
                BorderBrush = Brushes.DimGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new TextBlock
                {
                    Text =
                        "😀 😃 😄 😁 😆 😅 😂 😊 😇 🙂 🙃 😉 😌 😍 🥰 😘 😋 😜 🤪 🤨 🧐 🤓 😎 " +
                        "🥳 😏 😒 😞 😔 😟 😕 🙁 ☹️ 😣 😖 😫 😩 🥺 😢 😭 😤 😠 😡 🤬 🤯 😳 " +
                        "🥶 😱 😨 😰 🤗 🤔 🫣 🤭 🤫 🤥 😶 😐 😑 😬 🙄 😯 😦 😧 😮 😲 🥱 😴",
                    FontFamily = new FontFamily("Apple Color Emoji"),
                    FontSize = 32,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            emojiPanel.SetValue(Panel.ZIndexProperty, int.MaxValue);
            root.Children.Add(emojiPanel);
            window.Content = root;
            _benchmarkScreenshotRoot = emojiPanel;
            Console.WriteLine(
                "[ControlCatalog] Apple color-emoji stress fixture attached");
        }

        private static Avalonia.Visual FindScreenshotRoot(
            Avalonia.Visual windowContent)
        {
            TabControl? catalog = windowContent
                .GetVisualDescendants()
                .OfType<TabControl>()
                .FirstOrDefault();
            if (catalog?.SelectedItem is TabItem selectedItem)
            {
                if (selectedItem.Content is ContentControl
                    {
                        Content: Avalonia.Visual deferredContent
                    })
                {
                    return deferredContent;
                }
                if (selectedItem.Content is Avalonia.Visual directContent)
                {
                    return directContent;
                }
            }

            return windowContent;
        }

        private static void CollectRetainedMemory()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        private static double Average(double total, int samples)
            => samples == 0 ? 0 : total / samples;

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
                Percentile(samples, count, 0.50),
                Percentile(samples, count, 0.95),
                Percentile(samples, count, 0.99),
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

        public void Dispose()
        {
            if (_popupFixture is { } popup)
            {
                popup.IsOpen = false;
                _popupFixture = null;
            }
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

        private static Control? FindFrameInvalidationTarget(Control? root)
        {
            if (root == null)
            {
                return null;
            }

            foreach (Avalonia.Visual descendant in root.GetVisualDescendants())
            {
                if (descendant is TextBlock
                    {
                        IsVisible: true,
                        Bounds.Width: > 0,
                        Bounds.Height: > 0
                    } text)
                {
                    return text;
                }
            }

            return root;
        }

        private static bool ApplyBitmapCacheFixture(Control root)
        {
            string? scaleValue =
                Environment.GetEnvironmentVariable(BitmapCacheScaleVariable);
            string? snapValue =
                Environment.GetEnvironmentVariable(BitmapCacheSnapVariable);
            string? clearTypeValue =
                Environment.GetEnvironmentVariable(
                    BitmapCacheClearTypeVariable);
            if (string.IsNullOrWhiteSpace(scaleValue) &&
                string.IsNullOrWhiteSpace(snapValue) &&
                string.IsNullOrWhiteSpace(clearTypeValue))
            {
                return true;
            }

            double? scale = double.TryParse(
                    scaleValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsedScale)
                ? parsedScale
                : null;
            bool? snap = string.IsNullOrWhiteSpace(snapValue)
                ? null
                : ReadBoolean(BitmapCacheSnapVariable);
            bool? clearType = string.IsNullOrWhiteSpace(clearTypeValue)
                ? null
                : ReadBoolean(BitmapCacheClearTypeVariable);

            foreach (Avalonia.Visual visual in
                     root.GetVisualDescendants().Prepend(root))
            {
                if (visual.CacheMode is not BitmapCache cache)
                {
                    continue;
                }

                if (scale.HasValue)
                {
                    cache.RenderAtScale = scale.Value;
                }
                if (snap.HasValue)
                {
                    cache.SnapsToDevicePixels = snap.Value;
                    if (snap.Value)
                    {
                        visual.RenderTransform =
                            new TranslateTransform(0.375, 0.375);
                    }
                }
                if (clearType.HasValue)
                {
                    cache.EnableClearType = clearType.Value;
                    TextOptions.SetTextRenderingMode(
                        root,
                        Avalonia.Media.TextRenderingMode.SubpixelAntialias);
                }
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"[ControlCatalog] bitmap cache fixture" +
                        $" scale={cache.RenderAtScale}" +
                        $" snap={cache.SnapsToDevicePixels}" +
                        $" clearType={cache.EnableClearType}"));
                return true;
            }

            return false;
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
            public int SchemaVersion { get; init; }
            public string Backend { get; init; } = string.Empty;
            public string TextShaper { get; init; } = string.Empty;
            public string Page { get; init; } = string.Empty;
            public int Run { get; init; }
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
            public int FrameTimeSampleCount { get; init; }
            public double MinFrameMs { get; init; }
            public double MedianFrameMs { get; init; }
            public double P95FrameMs { get; init; }
            public double P99FrameMs { get; init; }
            public double AverageCompileMs { get; init; }
            public double AverageUploadMs { get; init; }
            public double AverageRenderMs { get; init; }
            public double AverageCompositorMs { get; init; }
            public int CompositorMetricSampleCount { get; init; }
            public double MedianCompileMs { get; init; }
            public double P95CompileMs { get; init; }
            public double P99CompileMs { get; init; }
            public double MaxFrameMs { get; init; }
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
            public double FirstRenderedFrameMs { get; init; }
            public uint RenderTargetWidth { get; init; }
            public uint RenderTargetHeight { get; init; }
            public float DpiScale { get; init; }
            public string? PresentationPath { get; init; }
            public int SceneCacheHits { get; init; }
            public int PathAtlasEntries { get; init; }
            public long PathAtlasCpuCacheBytes { get; init; }
            public ulong PathAtlasTextureBytes { get; init; }
            public int PathAtlasCurrentFramePaths { get; init; }
            public ulong PathAtlasCurrentFrameCoverageBytes { get; init; }
            public ulong PathAtlasCachedCoverageBytes { get; init; }
            public ulong PathAtlasCachedPaddedCoverageBytes { get; init; }
            public uint PathAtlasGrowthCount { get; init; }
            public uint PathAtlasShrinkCount { get; init; }
            public uint PathAtlasFramesSinceResize { get; init; }
            public uint PathAtlasWidth { get; init; }
            public uint PathAtlasHeight { get; init; }
            public uint PathRasterStagingBytes { get; init; }
            public uint PathPeakRasterStagingBytes { get; init; }
            public uint PathPeakRasterWidth { get; init; }
            public uint PathPeakRasterHeight { get; init; }
            public ulong GlyphAtlasTextureBytes { get; init; }
            public ulong ColorGlyphAtlasTextureBytes { get; init; }
            public int BitmapGlyphMetricCacheCount { get; init; }
            public long BitmapGlyphDecodedPixelBytes { get; init; }
            public ulong BitmapGlyphMetricEvictions { get; init; }
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
            public int MaskTexturePoolCount { get; init; }
            public int MaskTextureRetentionLimit { get; init; }
            public ulong MaskTexturePoolBytes { get; init; }
            public ulong MaskRenderScratchTextureBytes { get; init; }
            public int MaskRenderPasses { get; init; }
            public int MaskRenderDrawCalls { get; init; }
            public ulong MaskCopyBytes { get; init; }
            public ulong EffectTextureBytes { get; init; }
            public ulong LayerTextureBytes { get; init; }
            public ulong AdvancedBlendTextureBytes { get; init; }
            public ulong WavefrontTextureBytes { get; init; }
            public ulong MsaaTextureBytes { get; init; }
            public ulong TrackedIntermediateTextureBytes { get; init; }
            public ulong MetalAllocatedBytes { get; init; }
            public ulong NativeCommandBuffers { get; init; }
            public ulong NativeBuffers { get; init; }
            public ulong NativeTextures { get; init; }
            public ulong NativeTextureViews { get; init; }
            public ulong NativeBindGroups { get; init; }
            public ulong NativeBindGroupLayouts { get; init; }
            public ulong NativeShaderModules { get; init; }
            public ulong NativeRenderPipelines { get; init; }
            public ulong NativeComputePipelines { get; init; }
            public bool GpuHitTestingEnabled { get; init; }
            public int DrawCalls { get; init; }
            public int RecordedCommands { get; init; }
            public int RecordedCommandCapacity { get; init; }
            public int RetainedCompositionPictures { get; init; }
            public long RetainedCompositionPictureHits { get; init; }
            public long RetainedCompositionPictureMisses { get; init; }
            public long RetainedCompositionPictureCompilations { get; init; }
            public int RetainedCompositionScenes { get; init; }
            public int RetainedCompositionSceneNodes { get; init; }
            public int RetainedCompositionFallbackNodes { get; init; }
            public int RetainedCompositionCustomVisualNodes { get; init; }
            public long RetainedCompositionCustomVisualCompilations { get; init; }
            public long RetainedCompositionSceneFullSynchronizations { get; init; }
            public long RetainedCompositionSceneIncrementalSynchronizations { get; init; }
            public long RetainedCompositionSceneUnchangedReuses { get; init; }
            public int IncrementalScenePages { get; init; }
            public long IncrementalScenePageHits { get; init; }
            public long IncrementalScenePageMisses { get; init; }
            public long IncrementalScenePageCompilations { get; init; }
            public long IncrementalScenePageReusedArrays { get; init; }
            public long IncrementalScenePageBytes { get; init; }
            public string? IncrementalScenePageRejectReason { get; init; }
            public string? IncrementalScenePageMissReason { get; init; }
            public long IncrementalSceneUploadPageWrites { get; init; }
            public long IncrementalSceneUploadBytes { get; init; }
            public long IncrementalSceneUploadShadowBytes { get; init; }
            public long SceneUploadBatchCount { get; init; }
            public long SceneUploadCopyCount { get; init; }
            public ulong SceneUploadArenaBytes { get; init; }
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
